# Plan: Sales Pricing Engine — Foundation Fix + Master Upgrade

Verified 2026-09-16 against `c:\wincom\net10projects`. Supersedes the 35-section
"Sales Pricing Engine" study, which proposed a single central `SalesPrice` table with a 7-level
hierarchy. That design is rejected: the repo already has a working 4-step engine and 3 masters,
and rebuilding would invalidate 1266 passing tests.

e-Invoice (LHDN MyInvois) is explicitly out of scope.

## Objective

Make the EXISTING pricing engine actually drive documents, then extend the three existing masters
with the missing SME capabilities (validity, quantity bands, currency), then govern price override
and add a price-inquiry screen.

## Verified starting facts

- Engine exists, pure and tested: `ErpWeb.Core/Sales/SaItemFamilyPricing.cs`
  (`SaItemFamilyPriceResolver.Resolve` 4-step chain; `SaItemFamilyDiscountResolver`).
- Service entry points exist: `SaSalesRefService.ItemFamily.Resolve.cs`
  (`ResolveItemPriceAsync`, `ResolveItemDiscountAsync` on `ISaSalesRefService`).
- **CRITICAL DEFECT: zero production callers.** Only tests and docs reference them.
  All four transaction pages still do `Popup.UnitPrice = item.SellingPrice ?? 0m`:
  - `ErpWeb.UI/Sales/Transactions/SaSo.razor.cs:738`
  - `SaDo.razor.cs:846`
  - `SaInvoice.razor.cs:1075`
  - `SaCdn.razor.cs:633`

  A missing price silently becomes RM 0.00, violating the engine's own "never 0" rule.
- `ISaSalesRefService` is ALREADY injected in `SaSo.razor.cs:19`, `SaDo.razor.cs:19`,
  `SaCdn.razor.cs:19`. NOT in `SaInvoice.razor.cs` — add the injection.
- Price freeze and SO→INV inheritance ALREADY WORK: `SaInvoiceLineVm.FromSalesOrder` /
  `FromDeliveryOrder` copy `UnitPrice` plus 8 discount slots, with no re-resolution.
- `IvCustPriceGroup`, `IvCustPrice`, `SaDisGroupItem` were CREATED by
  `scripts/init-sales-item-family.sql`, so their keys are ours to change.
  `IvCustPrice` PK today = `(CompanyCode, CustPriceCode, ICode, UOM)` (script line 86).
- `SaItemCust` is LEGACY-live; PK `(CompanyCode, CustCode, ICode, SellingUOM, MOQ)`
  was already widened by our script, so do NOT widen again (destructive).
- `SaCustGroup` HAS a Blazor screen and service: `SaSalesRefService.ListCustGroupsAsync`,
  `GetCustGroupAsync`, `SaveCustGroupAsync`; UI `ErpWeb.UI/Sales/Masters/SaCustGroupList.razor(.cs)`.
  Entity `ErpWeb.Model/Entities/CustomerProfile/SaCustGroup.cs` is code + description only.
- `IvCustPrice` has NO dates, NO quantity bands, NO currency. `SaItemCust` has `Currency`
  (fail-closed) and `MOQ` (int). Only `SaDisGroupItem` is dated.
- `PermissionCodes` (`ErpWeb.Core/Menus/PermissionCodes.cs`) has NO price-override.
  `PermissionCodes.All` must include any new constant or admins can delete it.
- No price explanation or inquiry anywhere (grep for `GetPriceExplanation|PriceInquiry` = empty).
- No field-level audit infrastructure in the repo (only `IvTrxHistory` plus Serilog).
- Tax basis: masters are tax-EXCLUSIVE; `IsInclusive` is per-line on the popup
  (`SaSo.razor:583`); `SaInvoiceCalc.CalculateLine` un-taxes inclusive lines via `/(1+t)`.
  The master-to-line boundary conversion does NOT exist anywhere yet.
- Detail entities store price and discounts but NO source: `SaSoDetail`, `SaDoDetail`,
  `SaInvoiceDetail`, `SaCdnDetail` have no `PricingSource`, `OriginalUnitPrice` or `OverrideReason`.

## Best-practice decisions

1. **No central `SalesPrice` table.** Keep three masters, one engine.
2. **`SaItemCust` = permanent negotiated price** — remains undated, key unchanged, no DDL.
3. **`IvCustPrice` (price-list line) = the dated / tiered / multi-currency vehicle.**
   We own this table, so its PK widens safely.
4. **Customer Group → Price List is an ASSIGNMENT FALLBACK, not a price level.**
   `SaCust.CustPriceCode` wins; blank falls back to `SaCustGroup.CustPriceCode`.
5. **Prices stored tax-EXCLUSIVE; gross up at the document boundary only.**
   One conversion point, inside the new orchestrator.
6. **Price override is permission-gated, requires a reason, and retains the original price.**
7. **Dealer price stays FAIL CLOSED** (no source exists; do not invent one).
8. **No price-history ledger.** Audit = `ModifiedBy`/`ModifiedDate` plus override columns.
9. **CompanyCode scopes pricing; BranchCode never enters a pricing key.**
10. **The pricing method is configurable PER COMPANY.** `Company.SalesPriceMethod` selects which
    sources are *eligible*; the walk order is fixed by specificity and is never configurable.
11. **`IvStockMaster.SellingPrice` is the guaranteed last resort in every mode** and cannot be
    switched off, because a missing price must still resolve rather than become RM 0.00.

## Company pricing mode (per-company configuration)

Stored as a nullable column on `Company`, following the existing per-company precedent
(`Company.TimeZoneId`, read by `CurrentDateService.ResolveTimeZoneId()` with a safe fallback and
edited on `ErpWeb.UI/Admin/AdminCompany.razor`). No new options table — that would be
over-engineering. **NULL = `CUSTOMER_ITEM_AND_LIST`**, so existing companies are unaffected.

The setting controls **eligibility only**. It cannot reorder the sources: the order is fixed by
specificity, so a company cannot configure a group price to beat a negotiated customer price.

| Mode token | Chain actually walked | Typical SME use |
|---|---|---|
| `CUSTOMER_ITEM_AND_LIST` *(default)* | Customer Item → Cust List → Group List → **Item Default** | full model; best default |
| `CUSTOMER_ITEM_ONLY` | Customer Item → **Item Default** | negotiated / contract customers |
| `PRICE_LIST_ONLY` | Cust List → Group List → **Item Default** | distributors on price lists |
| `ITEM_DEFAULT_ONLY` | **Item Default** | simple retail / single-price shop |

Guards: the field is admin-only and confirmed on save, because `ITEM_DEFAULT_ONLY` silently stops
`SaItemCust` negotiated prices from applying. The mode is always read SERVER-SIDE — never trust a
value posted by the page. `SaCust.PriceMethod` containing DEALER still fails closed ahead of the
mode. When the item itself has no usable selling price (0, blank, or a different UOM) the result
is a BLOCK, not a fallback — the mode defines the search path, it does not guarantee a price.

## Line pricing pipeline (four stages)

The four stages are strictly ordered and each has exactly ONE owner. Nothing may skip a stage, and
only stage 4 may round.

| # | Stage | Owner | Input → Output | Rounds? |
|---|---|---|---|---|
| 1 | **Price resolution** | `ResolveLinePricingAsync` plus the pure `Select*`/`Resolve` | (customer, item, UOM, qty, date, currency, company mode) → ONE tax-EXCLUSIVE unit price + source + reject reasons | no |
| 2 | **Tax-basis conversion** | `SaItemFamilyPriceBasis.ToInclusive` | tax-exclusive price → the value that will be stored on the line (inclusive when `IsInclusive`) | no |
| 3 | **Discount resolution** | `ResolveItemDiscountAsync` plus `SaItemFamilyDiscountResolver` | the STAGE 2 price → two engine slots + an UNROUNDED per-unit discount | no |
| 4 | **Document calculation** | `SaInvoiceCalc.CalculateLine` / `CalculateHeader` | stored `UnitPrice` + slots + `IsInclusive` + tax% + `decPoint` → `Amount`, `NetAmount`, `TaxAmt`, header totals | **yes — the only place** |

**The ordering is load-bearing.** Stage 3 must consume the STAGE 2 price, not the stage 1 price.
The shipped `SaInvoiceCalc.CalculateLine` computes the per-unit discount from the line's raw
`UnitPrice` and only then divides by `(1 + t)` on the inclusive path. Resolving the discount
against the tax-EXCLUSIVE price would produce a discount the document engine then re-scales, so the
persisted discount would be wrong.

**[CORRECTED 2026-09-16]** The earlier draft of this plan resolved the discount and then applied
the basis conversion. That order is wrong and is fixed here.

Only the line's `UnitPrice` and the discount slots are PERSISTED. Stages 1-3 run at line entry;
stage 4 is recomputed on every load and is never stored as the source of truth for a new line.
`SaItemFamilyPriceBasis` and `SaInvoiceCalc` already exist and are test-pinned — this pipeline
describes the call order, it does not add a second implementation of either.

## Target resolution order (extends the shipped chain)

0. `SaCust.PriceMethod` contains DEALER → fail closed (unchanged).
1. Read the company pricing mode (above) and build the ordered list of ELIGIBLE sources.
2. `GetCustomerItemPriceAsync` — `SaItemCust` (customer special price) for (CustCode, ICode, UOM);
   highest `MOQ <= Qty` wins; `UnitPrice > 0`; currency mismatch → fail closed.
   (UNCHANGED, test-pinned.)
3. `GetCustomerPriceListPriceAsync` — `IvCustPrice` where `CustPriceCode` = `SaCust.CustPriceCode`;
   then `GetCustomerGroupPriceAsync` — `IvCustPrice` where `CustPriceCode` = the customer group's
   (NEW fallback). For each, a line qualifies when:
   - date window contains DOC DATE (date part): `ValidFrom <= date` and (`ValidTo` null or `>= date`)
   - quantity band contains Qty: `MinQty` (0 = any) and `MaxQty` (null = unlimited)
   - currency: exact match to document currency, or line currency blank (= company base)
   - header `IvCustPriceGroup.IsActive`
   - ordering: `MinQty DESC` → `ValidFrom DESC` → `ICode`
   - if item/UOM lines exist but ALL are a different currency → BLOCK with explicit message
   - a mismatch is not an error when nothing exists at all → fall through to the next source
4. `GetItemDefaultPriceAsync` — `IvStockMaster.SellingPrice` when document UOM equals item
   `SellingUom`/`StdUom` and item `IsActive` and price > 0. No UOM conversion. (UNCHANGED.)
5. No source produced a price → BLOCK with an actionable message. Never 0.

## Phases

Each phase is independently shippable. Gate = build + tests + smoke.

### Phase 1 — Wire the engine + company pricing mode — highest value

> **STATUS: SHIPPED.** All four pages resolve through `ResolveLinePricingAsync`; the
> `SellingPrice ?? 0m` defect is grep-clean in `SaSo`/`SaDo`/`SaInvoice`/`SaCdn`. Company mode is
> live on `Company.SalesPriceMethod` + the admin combo, read server-side. Migration
> `scripts/alter-company-sales-price-method.sql` applied to dev.

1.1 Introduce ONE named method per price source, each returning the same shared result shape
    `SaPriceSourceResult` → Found, UnitPrice, Source, Ref, MatchedBand (MOQ or MinQty/MaxQty),
    ValidFrom, ValidTo, Currency, RejectReason.

    | Layer | Method | Reads |
    |---|---|---|
    | Source | `GetCustomerItemPriceAsync` | `SaItemCust` (MOQ band) |
    | Source | `GetCustomerPriceListPriceAsync` | `IvCustPrice` via `SaCust.CustPriceCode` |
    | Source | `GetCustomerGroupPriceAsync` | `IvCustPrice` via `SaCustGroup.CustPriceCode` (Phase 5) |
    | Source | `GetItemDefaultPriceAsync` | `IvStockMaster.SellingPrice` (UOM must match) |
    | Config | `GetSalesPriceMethodAsync` (private) | `Company.SalesPriceMethod` |
    | Orchestrator | `ResolveLinePricingAsync` | walks the eligible sources, first Found wins |

    `RejectReason` is what makes the Phase 6 explanation ladder work: "level 2 not applied — price
    list line exists but is stored in USD".

    Keep the matching rules PURE and give each source a named pure selector
    (`SelectCustomerItem`, `SelectCustomerPriceList`, `SelectCustomerGroupPrice`,
    `SelectItemDefault`), with the existing `SaItemFamilyPriceResolver.Resolve` delegating to them
    in order. That preserves the 11 pinned contract tests (E1–E11) untouched while making
    precedence testable with no database.

1.2 New orchestrator on `ISaSalesRefService`:
    `ResolveLinePricingAsync(SaLinePricingRequest)` → `IvMasterOperationResult<SaLinePricingResult>`.

    Request: CustCode, ICode, UOM, Qty, DocDate, DocCurrency, TaxPercent, IsInclusive, IClass.

    Result: UnitPrice (**basis-corrected, ready to assign**), PricingSource, PricingRef,
    MatchedMoq/MinQty/MaxQty, ValidFrom/ValidTo, ItemDiscount..ItemDiscount6,
    ItemDiscAmount, ItemDiscAmount1, DiscountPerUnit, DiscountRuleId, BlockedMessage.

    The orchestrator runs the four pipeline stages in order (see "Line pricing pipeline"): loads
    the customer and the company mode, walks the eligible sources, converts the tax basis, THEN
    resolves the discount against the converted price. These steps live in ONE place; pages never
    re-derive them.

1.3 Stage 2 — tax-basis conversion rule:
    `IsInclusive ? exclusive * (1 + TaxPercent/100) : exclusive`.

    `SaInvoiceCalc` already un-taxes inclusive lines, so the stored master must be grossed up.
    Never round here — masters keep 4 dp; document rounding stays in `SaInvoiceCalc.Money`.
    This runs BEFORE the discount is resolved (stage 2 before stage 3).

1.4 Wire all four pages, replacing `Popup.UnitPrice = item.SellingPrice ?? 0m`:
    - `SaSo.razor.cs` `OnPopupItemChanged` (~L725) — add await plus sequence guard
    - `SaDo.razor.cs` (~L846), `SaInvoice.razor.cs` (~L1075), `SaCdn.razor.cs` (~L633)
    - add `[Inject] private ISaSalesRefService SalesRefService` to `SaInvoice.razor.cs`
    - call on item / UOM / qty / date / currency change (NOT on every keystroke)
    - use a sequence guard like the existing `_customerApplySeq` to drop stale results

1.5 Blocking behaviour: no price → set `PopupError` to the engine message and disable the popup
    Save. Do not insert a zero-price line.

1.6 Popup shows the price source as a hint (the "price details" feature for free).

1.7 Company pricing mode — the ONE additive column in this phase:
    `Company.SalesPriceMethod nvarchar(32) NULL` (NULL = `CUSTOMER_ITEM_AND_LIST`).
    Entity plus `CompanyConfiguration` plus `ICompanyService`/`CompanyService` mapping.
    Script `scripts/alter-company-sales-price-method.sql` (additive, idempotent).

1.8 Mode → eligible source list mapping, as a pure function so it is unit-testable with no DB.
    The order is fixed; the mode only removes sources. Item Default is never removable.

1.9 Admin UI: add the mode combo beside `TimeZoneId` on `ErpWeb.UI/Admin/AdminCompany.razor(.cs)`.
    Admin-only, with a confirmation on save because `ITEM_DEFAULT_ONLY` stops `SaItemCust`
    negotiated prices from applying. Enforce the mode SERVER-SIDE — never trust a value posted
    by the page.

1.10 Tests: `SaLinePricingConsumerTests` — gross-up exclusive→inclusive, block on no price,
     discount slots assigned, dealer fail-closed through the orchestrator, plus one case per mode
     proving the eligible source list and the item-default fallback. Add a PIPELINE-ORDER test:
     for an inclusive line carrying a percentage discount, the resolved discount must equal what
     `SaInvoiceCalc.CalculateLine` independently computes from the stored `UnitPrice`. That test
     fails under the previous (wrong) stage order.

### Phase 2 — Persist the pricing source (small additive DDL)

> **STATUS: SHIPPED** for all four documents, including step 2.3 — `SaInvoiceLineVm.FromSalesOrder`
> and `FromDeliveryOrder` copy the provenance verbatim, never re-resolving. 8 round-trip tests
> (`PricingProvenance_*` in `SaSoServiceTests`, `SaDoServiceTests`, `SaInvoiceServiceTests`,
> `SaCdnServiceTests`). Dev verified: all four detail tables carry `PricingSource` = 80 bytes and
> `PricingRef` = 120 bytes. The SO→INV COPY itself has no unit test because `ErpWeb.Tests` does not
> reference `ErpWeb.UI`; it is asserted by reading the code and by manual smoke (Verification 6).

2.1 Add to `SaSoDetail`, `SaDoDetail`, `SaInvoiceDetail`, `SaCdnDetail`:
    `PricingSource nvarchar(40) NULL`, `PricingRef nvarchar(60) NULL`.

    `PricingRef` is a human-readable token (`PL-DEALER`, `MOQ=100`, `DGI#12`) chosen for support
    readability — an agent can explain a price without a join. `IvCustPrice` gains an `Id`
    surrogate in Phase 3, but the token is still preferred because the price-group popup saves its
    lines as an aggregate, so line `Id` values are not stable identifiers across saves.

2.2 Entity plus `*Configuration` plus mapping plus save/read in each document service
    (`SaSoService.cs`, `SaDoService.cs`, `SaInvoiceService.cs`, `SaCdnService.cs`).

2.3 Set it from the orchestrator result; on SO→INV and DO→INV copy it verbatim (no re-resolve).

2.4 Script `scripts/alter-sa-detail-pricing-source.sql` (additive, idempotent).

2.5 Tests: source survives save/reload; invoice inherits the SO's source.

### Phase 3 — Price List upgrade: validity + quantity bands + currency

> **STATUS: SHIPPED 2026-09-17.** Entity, `IvCustPriceConfiguration` (surrogate `Id` PK + the three
> indexes), the loader, the read projections, the aggregate save and the **dual-range overlap rule**
> (`FindOverlappingPriceLines`, reusing `SaItemFamilyRuleMatch.BandsOverlap`/`WindowsOverlap`) are all
> in place, plus the UI line columns and editor fields. Migration
> `scripts/alter-ivcustprice-phase3.sql` verified idempotent twice on scratch and twice on dev and
> APPLIED to both (dev's 1 legacy row correctly migrated to the `1900-01-01` sentinel).
>
> **Deviation from 3.2, deliberate and documented:** `CurrencyCode` IS part of the unique index. 3.2
> says it stays out, but 3.4 requires a MYR tier and a USD tier on the SAME band to coexist, and those
> two statements cannot both hold — leaving currency out of the key would silently make mixed-currency
> lists illegal. The reasoning is repeated in the migration script's header.
>
> The line editor now keys on the NATURAL key (item + UOM + band floor + effective-from + currency),
> because item + UOM stopped being unique the moment tiers existed.

3.1 `IvCustPrice` new columns:
    - `Id int IDENTITY` — NEW surrogate primary key (rendered necessary in 3.2)
    - `ValidFrom date NOT NULL DEFAULT '1900-01-01'` (sentinel = "always"; NOT NULL so the natural
      unique index can contain it — SQL Server treats NULLs as equal in a unique index, which
      would allow only ONE undated row per key)
    - `ValidTo date NULL` (open-ended; deliberately NOT in the key)
    - `MinQty decimal(18,4) NOT NULL DEFAULT 0` (0 = any quantity; NOT NULL for the same reason)
    - `MaxQty decimal(18,4) NULL` (NULL = unlimited; deliberately NOT in the key)
    - `CurrencyCode nvarchar(5) NULL` (blank = company base)

3.2 **Primary-key strategy — FIXED by review.** Do NOT extend the natural PK with `ValidFrom`:
    two quantity bands legitimately start on the SAME `ValidFrom` (a `MinQty 1` tier and a
    `MinQty 10` tier both effective 2026-09-01), so `(CompanyCode, CustPriceCode, ICode, UOM,
    ValidFrom)` would collide and make tiered pricing impossible.

    Instead:
    - PK = surrogate `Id int IDENTITY` (new column).
    - UNIQUE index = the natural key
      `(CompanyCode, CustPriceCode, ICode, UOM, ValidFrom, MinQty)`.
    - Query index = `(CompanyCode, CustPriceCode, ICode, UOM, ValidFrom, ValidTo)`.
    - Plus a currency-covering index.

    This mirrors the shipped `SaDisGroupItem`, created by the same script with an `int IDENTITY` PK
    plus a separate unique index over its band+window natural key — the same shape of problem,
    already solved the same way. Surrogate-key precedent also exists at `PoPurItem.Id`
    (`HasKey(Id)`, `ValueGeneratedOnAdd`).

    `ValidTo`, `MaxQty` and `CurrencyCode` stay OUT of the unique index so a tier or a promotion can
    be edited without key churn. Uniqueness is therefore per (list, item, UOM, effective-from,
    band-floor) — exactly one row per band start.

    The aggregate save in `SaSalesRefService.ItemFamily.cs`, `IvCustPriceConfiguration`, the
    migration script, and the `SaItemFamilyServiceTests` fixture must all move from the 4-part
    composite key to `Id`.

    Extend `scripts/init-sales-item-family.sql`, keeping `GO` batches separate for the NOT NULL
    steps and the PK/index steps — under `SET XACT_ABORT ON` one failing batch rolls back the
    earlier ones. Dev holds ZERO `IvCustPrice` rows today, but the script must still be written to
    run against a populated table (add `Id` as IDENTITY, then swap the PK) because production is
    not guaranteed empty.

3.3 Resolver: apply the date/quantity/currency filters and ordering from the target chain above.
    Date comparison on the DATE PART only.

3.4 **Overlap rule — now stated explicitly across BOTH ranges.** For a NEW or edited row, reject the
    save when an existing row for the same `(CompanyCode, CustPriceCode, ICode, UOM, CurrencyCode)`
    overlaps in **both**:
    - the QUANTITY range (inclusive): `newMinQty <= existingMaxQty` AND `newMaxQty >= existingMinQty`
      (NULL `MaxQty` = +infinity, so it overlaps everything at or above `MinQty`), **and**
    - the VALIDITY window (inclusive, compared on the date part): `newValidFrom <= existingValidTo`
      AND `newValidTo >= existingValidFrom` (NULL `ValidTo` = open-ended).

    BOTH must overlap for the pair to be ambiguous. The legitimate cases are called out because a
    naive rule would wrongly reject them:
    - bands that overlap in DIFFERENT periods → **accepted** (a tier change effective 2026-10-01)
    - windows that overlap over DIFFERENT bands → **accepted** (a promotion restricted to qty 1-9)

    This mirrors the shipped `SaDisGroupItem` rule and must reuse its shared matcher helpers rather
    than re-deriving the comparisons.

    Currency is part of the overlap key: a MYR tier and a USD tier over the same band may coexist,
    because the resolver filters by currency before ranking.

    The validator lives in the SERVICE and is the single implementation — the UI must not duplicate
    it, and any future bulk/import path must call the same one.

    Defence in depth: legacy rows may already be ambiguous, so the resolver keeps its deterministic
    tie-break (`MinQty DESC` → `ValidFrom DESC` → `ICode`) and reports the losing rows, exactly as
    `SaItemFamilyDiscountResolver.CompetingRuleIds` does for discounts.

    The migration is safe by construction: every pre-existing row maps to `ValidFrom = '1900-01-01'`
    and `MinQty = 0`, so no two migrated rows for the same key can overlap — the unique index admits
    exactly one of them.

    The save runs in a `Serializable` transaction, so it MUST catch `IsSerializationConflict`
    and return `Concurrency` — this exact omission was a real defect found before.

3.5 UI `SaCustPriceGroupList.razor(.cs)`: new line columns (UOM, Min Qty, Max Qty,
    Valid From, Valid To, Currency, Price). Currency defaults to company base. The grid key becomes
    `Id`; because it is an identity column, new unsaved lines need a temporary negative key until
    the aggregate save returns the real one.

3.6 Tests: boundary days inclusive, MinQty exactly / MaxQty exactly / between / unlimited,
    newest `ValidFrom` wins, currency match and currency-block, **two bands sharing one `ValidFrom`
    both persist and resolve**, overlap in BOTH ranges rejected, band-overlap in different periods
    ACCEPTED, window-overlap over different bands ACCEPTED.

### Phase 4 — Price override governance

> **STATUS: SHIPPED 2026-09-17.** One shared rule (`SaPriceOverridePolicy`) is called by ALL FOUR
> document services at their single prepare choke point, so the check is server-side and cannot drift
> per document. Columns on the four detail tables + configs + save/read + DTOs + requests + UI VMs.
> Migration `scripts/alter-sa-detail-price-override.sql` verified idempotent twice on scratch and twice
> on dev and APPLIED to both. `PRICE_OVERRIDE` is a built-in permission (constant + `All` + a
> `PermissionType='Data'` seed row granted to the four transaction menus); the CN/DN screen is TWO menu
> rows (`SA_CN`, `SA_DN`), so five grants cover four screens.
>
> Two design points worth keeping:
> 1. **A caller that echoes the resolved price back is NOT an override**, so the ordinary path needs
>    neither the permission nor a reason, and stores NULL — preserving "NULL = never overridden".
> 2. **[CORRECTED 2026-09-17]** The earlier note here claimed the override record survives the
>    DO→INV and INV→CDN copy paths. It deliberately does NOT. `PricingSource`/`PricingRef` ARE copied
>    verbatim, but copying `OriginalUnitPrice` would make an invoice or delivery order that merely
>    CARRIES an inherited price demand `PRICE_OVERRIDE` from whoever bills or ships — someone who never
>    made the decision. The audit trail stays on the document that took it, and the copied price becomes
>    the derived line's new baseline. `FromDto` (loading an existing document) DOES carry the record, so
>    an unrelated edit cannot erase it.
>
> 3. **A reopened line adopts its loaded price as the baseline** (`Popup.OriginalUnitPrice ??= line.UnitPrice`).
>    Without it, an existing line has no engine baseline at all, so a manual price change on a reopen
>    would not be an override — no reason demanded, nothing recorded.
>
> 4 tests in `SaSoServiceTests`: recorded-and-retained, denied without permission, reason required, and
> the no-override echo case.
>
> **`SaQTDetail` was initially excluded and has since been included.** The original reasoning was that a
> quotation is a proposal rather than a posted document — but that left `SaQt.razor` with an editable
> price and no gate at all, while every other sales document enforced one. A quote is the *first* place
> a price is offered, so it is the most valuable place to govern. `SaQtDetail` now carries the two
> columns and `SaQtService` calls the same policy. Schema: `scripts/alter-saqt-detail-price-override.sql`
> (existing databases) and `scripts/create-saqt.sql` (fresh ones).

4.1 `PermissionCodes.PriceOverride = "PRICE_OVERRIDE"` plus an entry in `PermissionCodes.All`
    (otherwise it is admin-deletable) plus a `PermissionType='Data'` seed row granting the four
    transaction menus.

4.2 Detail columns `OriginalUnitPrice decimal(18,4) NULL`, `OverrideReason nvarchar(100) NULL`.

4.3 UI: the `UnitPrice` spin edit `Enabled` becomes `CanEditDocument && CanOverridePrice`.
    Without the permission the price renders read-only. With it, a change from the resolved price
    requires a reason (new popup field) before Save; the engine message is the baseline.

4.4 Persist `OriginalUnitPrice` = engine price, plus the reason. Enforce in the SERVICE, not only
    in the UI (server is the execution point — repo convention).

4.5 Script `scripts/alter-sa-detail-price-override.sql` plus an extension to the menu seed script.

4.6 Tests: override allowed with permission and reason recorded; denied without permission;
    reason required; original retained.

### Phase 5 — Customer Group → default Price List

> **STATUS: SHIPPED 2026-09-17.** 10 new tests (5 consumer + 5 master-CRUD); suite 1400 green with
> `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`. `scripts/alter-sacustgroup-pricelist.sql` verified idempotent
> twice on scratch and twice on dev; on dev the single existing group correctly reads as "no
> default". Pure addition — no dynamic SQL was needed.
>
> Two implementation notes that differ from the text below:
> 1. 5.3's "only when the customer's own is blank" is implemented as **walk order**, not a blank
>    check: the customer list is simply tried first, so a customer list with no line for the item
>    falls through to the group. That is the intended fall-through (see the target chain above), not
>    a bug.
> 2. `SaSalesRefService` gained a 5th constructor parameter (`ISaCustLookupService`) so the shipped
>    3-clause D-6 validation could be REUSED rather than reimplemented. All 8 test files that
>    construct the service were updated to match.
>
> Before this phase the group level was dead code: the orchestrator hardcoded
> `GroupPriceCode = string.Empty` and `SaItemFamilyPriceCandidates.GroupPriceListLines` was never
> populated, so `CUSTOMER_GROUP_PRICE_LIST` could never resolve anything.

5.1 `SaCustGroup.CustPriceCode nvarchar(20) NULL` (width = `SaCust.CustPriceCode`'s live width).
    Entity plus `SaCustGroupConfiguration` plus `SaCustGroupEditVm` plus `SaCustGroupListRow`
    plus validation.

5.2 UI field on `SaCustGroupList.razor(.cs)` using `IvCodeComboBox` fed by
    `ISaCustLookupService.ListPriceGroupsForAssignmentAsync`, reusing
    `ValidateCustPriceCodeAssignmentAsync` (the 3-clause D-6 contract).

5.3 `GetCustomerGroupPriceAsync` reads `SaCustGroup.CustPriceCode` and returns a candidate only
    when the company mode makes the group level eligible. The customer's own `CustPriceCode` is
    always tried first by `GetCustomerPriceListPriceAsync`. An unknown or retired code still
    yields no lines and the walk continues to the next source.

5.4 Script `scripts/alter-sacustgroup-pricelist.sql`.

5.5 Tests: customer value wins; group used when customer blank; both blank → item default;
    tenant isolation.

### Phase 6 — Price Inquiry + explanation ladder

> **STATUS: SHIPPED 2026-09-17.** The pure ladder is `SaItemFamilyPriceExplainer.Explain` in the new
> `SaItemFamilyPriceExplanation.cs` — the tested `Resolve` is untouched, and the ladder walks the same
> sources through the SAME public `ResolveSource` entry point, so an inquiry can never disagree with a
> document. `ExplainLinePriceAsync` is on `ISaSalesRefService`; the page is
> `ErpWeb.UI/Sales/PriceInquiry.razor(.cs)` at `/sales/price-inquiry`.
>
> 9 tests in `SaLinePricingConsumerTests`, including a `Theory` asserting the ladder's price equals
> `ResolveLinePricingAsync`'s `BaseUnitPrice` for all five price sources, and a test proving that
> EXCLUDED levels are still reported with the company method as the reason (the answer to the top
> support question).
>
> `ExplainLinePriceAsync` and `ResolveLinePricingAsync` now share one `LoadPriceContextAsync`, so the
> two can never load a different picture of the same line. The refactor is covered by the 22 existing
> consumer tests.
>
> Menu deployment: BOTH artefacts added (`MenuCodes.SalesPriceInquiry`, the `menus.xml` row) plus
> `scripts/init-sales-price-inquiry-menu.sql` (ACCESS only — the screen is read-only). Verified on
> scratch and dev.

6.1 Pure explanation API in `SaItemFamilyPricing.cs` (NEW type and method; leave the tested
    `Resolve` untouched): returns every level's outcome — candidate price, band, validity,
    currency, applied/not-applied and why, INCLUDING sources skipped because the company mode
    excluded them. That answers the top support question: "why didn't the customer's special
    price apply?" → "this company is set to `PRICE_LIST_ONLY`".

6.2 Service `ExplainLinePriceAsync` on `ISaSalesRefService` (tenant-scoped, not menu-gated).

6.3 New page `ErpWeb.UI/Sales/PriceInquiry.razor(.cs)`, route `/sales/price-inquiry`.

    Inputs: Customer, Item, UOM, Qty, Date, Currency → final price plus level, source and
    validity window. MUST call the same engine.

6.4 Menu deployment — BOTH artefacts required (repo trap):
    `ErpWeb/Menus/menus.xml` row plus `MenuCodes` constant plus
    `scripts/init-sales-price-inquiry-menu.sql` (ACCESS/VIEW plus grants).
    `MenuDeploymentParityTests` guards this; do not delete it.

6.5 Tests: ladder content for each level; inquiry result equals document resolution result.

## Files to modify

Core:
- `ErpWeb.Core/Sales/SaItemFamilyPricing.cs` — date/qty/currency matching, group fallback,
  explanation API, new candidate types
- `ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.Resolve.cs` — orchestrator, group load,
  new columns
- `ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.PriceSources.cs` — the four named source
  methods (new partial)
- `ErpWeb.Core/Sales/SaCompanyPriceMethod.cs` — mode tokens plus the pure mode→source-list map (new)
- `ErpWeb.Core/Security/ICompanyService.cs`, `CompanyService.cs` — company mode mapping
- `ErpWeb.Core/Sales/SaItemFamilyResults.cs` — `SaLinePricingRequest`/`Result`, explanation DTOs,
  edit VMs
- `ErpWeb.Core/Sales/ISaSalesRefService.cs` — new signatures
- `ErpWeb.Core/Sales/SaSalesRefService.ItemFamily.cs` — line save/list plus overlap validation
- `ErpWeb.Core/Sales/SaSalesRefService.cs` — `SaCustGroup` save and VM changes
- `ErpWeb.Core/Menus/PermissionCodes.cs`, `ErpWeb.Core/Menus/MenuCodes.cs`
- `ErpWeb.Core/Sales/SaSoService.cs`, `SaDoService.cs`, `SaInvoiceService.cs`, `SaCdnService.cs`

Model:
- `ErpWeb.Model/Entities/Sales/IvCustPrice.cs` plus `Configurations/Sales/IvCustPriceConfiguration.cs`
- `ErpWeb.Model/Entities/Sales/{SaSoDetail,SaDoDetail,SaInvoiceDetail,SaCdnDetail}.cs` plus configs
- `ErpWeb.Model/Entities/CustomerProfile/SaCustGroup.cs` plus
  `Configurations/Sales/SaCustGroupConfiguration.cs`
- `ErpWeb.Model/Entities/Company.cs` (`SalesPriceMethod`) plus `Configurations/CompanyConfiguration.cs`

UI:
- `ErpWeb.UI/Sales/Transactions/{SaSo,SaDo,SaInvoice,SaCdn}.razor.cs` and `.razor`
- `ErpWeb.UI/Sales/Masters/SaCustPriceGroupList.razor(.cs)`
- `ErpWeb.UI/Sales/Masters/SaCustGroupList.razor(.cs)`
- `ErpWeb.UI/Admin/AdminCompany.razor(.cs)` — company pricing mode combo
- `ErpWeb.UI/Sales/PriceInquiry.razor(.cs)` (new)

Scripts and menus:
- `scripts/init-sales-item-family.sql` (extend), `scripts/init-sales-item-family-menu.sql` (extend)
- new `scripts/alter-company-sales-price-method.sql`, `alter-sa-detail-pricing-source.sql`,
  `alter-sa-detail-price-override.sql`, `alter-sacustgroup-pricelist.sql`,
  `init-sales-price-inquiry-menu.sql`
- `ErpWeb/Menus/menus.xml`

Docs and tests:
- `docs/sales-item-family-logic.md` (update shipped table),
  `docs/sales-pricing-engine.md` (new plan of record)
- `ErpWeb.Tests/SaItemFamilyPricingContractTests.cs`, `SaItemFamilyResolutionServiceTests.cs`,
  `SaItemFamilySqlServerConcurrencyTests.cs`, new `SaLinePricingConsumerTests.cs`,
  new `SaPriceInquiryTests.cs`, new `SaCompanyPriceMethodTests.cs`

## Verification

1. `dotnet build ErpWeb.slnx --nologo -v:q` → 0 errors.
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` with
   `ConnectionStrings__SqlServerTestConnection` = a scratch DB whose name contains "test"
   and `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` (turns self-skip into a failure).

   PRE-CREATE the scratch DB: `EnsureCreatedAsync` before `CanConnect()`, else the first run's
   SQL Server tests fail spuriously.

3. Apply each SQL script TWICE on the scratch DB (second run a clean no-op) before touching dev.
4. Manual smoke per page: add a line with (a) customer special price, (b) price list price,
   (c) item default, (d) no price → blocked with a message, NOT RM 0.00.
5. Toggle the line's `IsInclusive` and confirm the stored price grosses up/down consistently, and
   that the persisted discount still matches what the document engine independently recomputes.
6. SO → Invoice: confirm the invoice keeps the SO price AND source after the price list changes.
7. Override: verify a user without `PRICE_OVERRIDE` gets a read-only price; with it, a reason
   is required and `OriginalUnitPrice` is recorded.
8. Price Inquiry returns the same number as the document line.
9. Company mode: set `ITEM_DEFAULT_ONLY` and confirm a line that previously used `SaItemCust` now
   uses the item default; set NULL/`CUSTOMER_ITEM_AND_LIST` and confirm the negotiated price
   returns. Confirm the mode is read server-side (a tampered client value changes nothing).

## Scope boundaries

IN: wiring, the named per-source methods, per-company pricing mode, source persistence,
price-list validity/bands/currency, override governance, group price-list default, inquiry screen.

OUT (deliberately): e-Invoice (LHDN MyInvois); a central `SalesPrice`/`PricingLevel` table;
dealer price; a price-history ledger; UOM conversion; branch-level pricing; customer-level
`SaDisGroup`/`CustDiscount`; final-price caching; promotional/contract/bundle/rebate pricing.

## Further considerations

1. Currency placement: `CurrencyCode` per LINE (chosen) allows mixed currencies in one list.
   Per-HEADER (`IvCustPriceGroup.CurrencyCode`) is more SME-intuitive ("EXPORT LIST = USD").
   Recommend revisiting after Phase 3 if mixed-currency lists confuse users.
2. `SaItemCust` stays undated. If a customer ever needs dated negotiated prices, the fallback
   is to put them on a dedicated price list, not to widen the legacy PK.
3. Dealer price remains fail-closed with an actionable message. Revisit only if a dealer price
   source is found; do not silently map DEALER to SELLING.
4. Phase 1 alone removes a live money defect AND delivers the company pricing mode. If value is
   needed this week, ship Phase 1 and defer Phases 2–6.
5. If a company ever needs a source order different from the fixed specificity order, do not make
   the order configurable — add a new named mode instead. A configurable order is how pricing
   becomes unexplainable and support tickets become unfixable.

## Review record

**2026-09-16 — external review. Three findings accepted and applied (the plan was not rewritten):**

1. **`IvCustPrice` primary key could not hold two quantity bands starting on the same `ValidFrom`.**
   Accepted — the natural PK can never contain `ValidFrom` alone. Replaced with an `Id` surrogate PK
   plus a separate unique index over the natural key, mirroring the shipped `SaDisGroupItem`
   (see 3.1, 3.2).
2. **Overlap rules were implied rather than defined.** Accepted — now explicit across BOTH the
   quantity range and the validity window, with the legitimate non-overlap cases (different
   periods, different bands) explicitly ACCEPTED so a naive rule cannot reject them (see 3.4).
3. **The stage order of price → discount → tax conversion → document calculation was implicit and
   ordered WRONG.** Accepted — this was a real defect, not a documentation gap: the shipped
   `SaInvoiceCalc.CalculateLine` derives the per-unit discount from the line's raw `UnitPrice`
   before dividing by `(1 + t)`, so resolving the discount against the tax-exclusive price would
   have produced a double-scaled discount. Now an explicit four-stage pipeline with one owner per
   stage and a single rounding point, plus a pinning test (see "Line pricing pipeline", 1.2, 1.3,
   1.10, Verification 5).
