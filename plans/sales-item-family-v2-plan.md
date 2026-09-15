---
name: Sales Item Family v2 (price groups, customer price, customer item, item discount)
overview: Deliver the Sales item-family masters (IvCustPriceGroup + IvCustPrice, SaItemCust, SaDisGroupItem) on top of the shipped item master (IvStockMaster) with a decision-closed price/discount contract, tenant-safe keys, additive DDL, and no SO/Invoice price resolution in this phase.
todos:
  - id: phase0
    content: Phase 0 gate — live schema matrix, external writers, dealer-price absence, Editable/Status semantics, 0-0 rows; produce ErpWeb/docs/sales-item-family-phase0-findings.md; binary PASS/FAIL
    status: completed
  - id: sign-off
    content: Close the decision register (§9) with owner sign-off — every row answered, none blank
    status: completed
  - id: phase1
    content: Phase 1 — additive DDL (create-or-alter), entities/configs/DbSets, InventoryLeftoverSite.Apply overloads, DI, key model, ADMIN-only smoke menu
    status: completed
  - id: phase2
    content: Phase 2 — IvCustPriceGroup master (list/entry/export) + SaCustEntry price-group picker conversion (D-6 contract)
    status: completed
  - id: phase3
    content: Phase 3 — IvCustPrice lines in one transaction with the header; unique key; VIEW_PRICE projection + export gate
    status: completed
  - id: phase4
    content: Phase 4 — SaItemCust customer-item master with MOQ band identity and refresh-status semantics preserved
    status: completed
  - id: phase5
    content: Phase 5 — SaDisGroupItem rules with band/date overlap validation inside a Serializable transaction + tenant/item-scoped read-only listing
    status: completed
  - id: phase6
    content: Phase 6 — SaDisGroup / SaDisCust gap verification only (no rebuild, no second writer)
    status: completed
  - id: phase7
    content: Phase 7 — menus.xml + MenuCodes + scripts/init-sales-item-family-menu.sql + permissions seed (VIEW_PRICE) + export endpoints + docs
    status: completed
  - id: resolve
    content: Resolution contract — pure price/discount rules + service entry points (ResolveItemPriceAsync / ResolveItemDiscountAsync) with the §7.2 and §8.1.1 worked examples pinned as tests
    status: completed
  - id: verify
    content: Build + full test suite (SQLite + SQL Server scratch DB with ERPWEB_REQUIRE_SQLSERVER_TESTS=1), schema diff, menu parity, per-screen smoke incl. negative smoke
    status: completed
isProject: false
---

# Sales Item Family v2 — Implementation Plan

**Status: APPROVAL PENDING — decision register §9.** Every decision below carries a recommended, evidence-backed resolution. The plan is executable as written *only* once the owner signs §9; any blank row means Phase 0 = FAIL and no code is written.

**Supersedes:** the review history in `plans/Sales-master-v2-plan.md` (change log only — do not implement from that file) and the out-of-scope item-family sections of `docs/sales-master-plan.md` §7.4.

**Verified-fact sources:** `docs/sales-item-family-logic.md` (legacy logic spec), `ErpWeb/docs/sales-master-phase0-findings.md` (v1 Phase 0 pattern), `ErpWeb/docs/sales-invoice-recon.md` (shipped discount/tax engine), plus direct code verification on 2026-09-15.

## 0. Implementation status (2026-09-15)

Phases 0–7 are implemented, and the §7/§8 resolution contract is implemented as code (its consumer is still deferred, as §8.9 requires). What each deliverable is, and where:

| Deliverable | Artefact |
|---|---|
| Schema | `scripts/init-sales-item-family.sql` (applied to dev `ERPWeb`) |
| Model | `ErpWeb.Model/Entities/Sales/{IvCustPriceGroup,IvCustPrice,SaItemCust,SaDisGroupItem}.cs` + configs + 4 DbSets + `InventoryLeftoverSite.Apply` overloads |
| Service (masters) | `SaSalesRefService.ItemFamily.cs`, `.DisGroupItem.cs`, `.Delete.cs`; DTOs in `SaItemFamilyResults.cs`; surface on `ISaSalesRefService` |
| **Resolution contract** | `ErpWeb.Core/Sales/SaItemFamilyPricing.cs` (pure rules: §7 chain, §8 pipeline, JOIN/SPLIT, slot mapping, tie-break, basis conversion) + `SaSalesRefService.ItemFamily.Resolve.cs` (`ResolveItemPriceAsync`, `ResolveItemDiscountAsync`) |
| Screens | `/sales/price-groups`, `/sales/customer-items`, `/sales/item-discounts` (`ErpWeb.UI/Sales/Masters/`) — **superseded 2026-09-15:** the read-only `/sales/customer-prices` screen was merged into `/sales/price-groups` (see `docs/sales-item-family-logic.md` §11.1); do not rebuild it |
| Permissions/menus | `MenuCodes` ×4, `PermissionCodes.ViewPrice` (+ `All`), `menus.xml` rows 18–21, `scripts/init-sales-item-family-menu.sql` |
| Export | `SaItemFamilyExportEndpoints.cs` (registered in `Program.cs`) + workbooks in `SaMasterRefExportWorkbooks` |
| Tests | `SaItemFamilyServiceTests.cs` (masters, VIEW_PRICE, tenant, exports), `SaItemFamilyPricingContractTests.cs` (§7.2 E1–E11, §8.1.1 JOIN/SPLIT, §8.3 matching, §8.3.1 tie-break), `SaItemFamilyResolutionServiceTests.cs` (DB-loaded chain, legacy `float` scaling, date-part windows), `SaItemFamilySqlServerConcurrencyTests.cs` (§24 concurrency rows) |

**Two plan statements are now superseded by what shipped**, and are recorded rather than silently dropped:

1. **§9.1 row 4/5/6 artefact** — the resolver exists as `SaItemFamilyPricing.cs` + the two `Resolve*Async` entry points (not a single `ResolvePriceAsync`). It is a *pure* rule set so it is testable without a database, with the service methods as the thin DB-loading wrappers.
2. **§24 "Paging"** — there is no paged lookup API for the four masters. The delivered shape is the house one: bounded lists (`MaxExportRows`) rendered by `CommonDataGridEx`, with the only true picker (`ListPriceGroupsForAssignmentAsync`) company-scoped and active-only. The paging row is therefore tested as *scope + bound*, not as page navigation.

**Verified 2026-09-15:** solution builds with 0 errors; `dotnet test ErpWeb.Tests` → **1256 passed, 0 failed, 0 skipped** with `ConnectionStrings:SqlServerTestConnection` pointed at the scratch DB `ERPWeb_ItemFamilyTest` (name contains "Test") **and** `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`, so every concurrency test actually executed — none silently skipped.

The concurrency suite found a real defect while being written: a deadlocked `Serializable` save in `SaveDisGroupItemAsync` escaped as an unhandled `DbUpdateException` instead of the "reload and try again" answer §10 requires (the `SaLMW` precedent handles it via `IsSerializationConflict`). Fixed by adding the same catch; the test that exposed it is `DiscountRule_ConcurrentOverlappingCreates_ExactlyOneCommits`.

**Still open (not a defect, an owner record):** the §9 register's `Owner`/`Approved` columns are unsigned — the implementation followed the closed `Final decision` column, and the signature boxes remain for the business owner. Also open from §26: O1 (the four shipped transaction pages still seed `UnitPrice = item.SellingPrice ?? 0m`), O2/O3 (`CustModel`, `DG`/`SG` carried but unused), O4 (`Editable` dropped), O6 (key widening applied — Branch A).

---

## 1. Objective

Give the Sales module the four missing reference objects — price lists, price-list item prices, customer-specific items, and item discount rules — using the **already-shipped** item master (`IvStockMaster`), the shipped list/entry/export framework, and the shipped document pricing engine's existing semantics. No SO/Invoice price resolution is implemented in this phase; instead the **contract** the future consumer must obey is fixed here so that the masters cannot become ambiguous later.

## 2. Scope / non-scope

**In scope**
- `IvCustPriceGroup` (price-list header) and `IvCustPrice` (item price line) master CRUD + list + export.
- `SaItemCust` customer-item master CRUD + list + export.
- `SaDisGroupItem` item-discount rule CRUD + list + a tenant/item-scoped read-only listing.
- `SaCust.CustPriceCode` conversion from free text to a validated lookup (server-side enforcement).
- Price/Discount resolution **contract** (§7/§8), tenant scope, keys, indexes, concurrency, permissions, menus, export, tests, deployment/rollback.

**Out of scope (stated, not implied)**
| Excluded | Reason |
|---|---|
| SO/DO/INV/CN price or discount **resolution** | consumer implementation deferred; contract fixed in §8.9 |
| Tax (SST/GST) determination | stays in the existing tax engine; masters store commercial prices only (§5) |
| Automatic UOM conversion | no conversion table exists in this repo (§6) |
| Dealer pricing | no dealer price source exists in Blazor; declared unsupported (§7, D6) |
| Bulk import screens (`ImportCustProduct`, `UploadCustPrice`, `UpdateSalesPrice`) | legacy-only writers; Phase 0 must list them as external writers and they must not be ported in this phase |
| Rebuilding `SaDisGroup` / `SaDisCust` | already shipped; Phase 6 is verification only |
| Bulk price recalculation / mass price change | not requested; would need its own contract |

## 3. Existing-system findings (verified 2026-09-15)

| Fact | Evidence |
|---|---|
| `IvStockMaster` is the Blazor item master: PK `(CompanyCode, ICode)`, `IsActive`→col `Active`, `RowVersion`, `SellingPrice`, `SellingUom`, `StdUom`, `IClassCode`→col `IClass`; full CRUD at `/inventory/items`, picker `IvStockMasterPicker` | entity/config/`IvStockMasterService`/`IvStockMasterList.razor` |
| No `IvMas` type exists anywhere in Blazor — zero matches outside docs | whole-solution grep |
| `IvCustPrice`, `IvCustPriceGroup`, `SaItemCust`, `SaDisGroupItem` do **not** exist in Blazor | `ErpWeb.Model` / `ErpWeb.Core` grep |
| `SaDisGroup` + `SaDisCust` **are** shipped (PK `(CompanyCode, GroupName, PayCode)`, Level B) | `ErpWeb/docs/sales-master-phase0-findings.md` |
| `SaCust.CustPriceCode` exists but is free text (`DxTextBox`, maxlength 20) and is `null`-able | `SaCustEntry.razor:740`, `SaCust.cs:52` |
| `SaCust.GroupDiscount` is already validated + delete-blocked | `ISaCustLookupService.ValidateDisGroupAssignmentAsync`, `SaSalesRefService.cs:3184` |
| `SaCust.PriceMethod` stores verbatim legacy strings; constants are `SaCustPaymentOptions.PriceSelling`/`PriceDealer`; `DiscountJoin`/`DiscountSplit` | `SaCustPaymentOptions.cs` |
| The shipped line engine computes discounts as: JOIN = `Σ% × UnitPrice + amounts`; otherwise sequential percent stack then amounts; **no SPLIT branch**; `CustDiscount` unused | `SaInvoiceCalc.CalculateDiscountPerUnit`, `ErpWeb/docs/sales-invoice-recon.md:317-324` |
| Money rounding: `SaInvoiceCalc.Money(v,2,AwayFromZero)`; 0 dp when `SaCust.DecPoint`; the **line `UnitPrice` is tax-inclusive on the inclusive path** and is un-taxed via `/(1+t)` | `SaInvoiceCalc` doc comment, `SaCdnCalc.MoneyNormalize`, `SalesCalcMatrixTests` |
| UOM master is `MsUom`/`MsUOM` (`IvUomList.razor`, `/inventory/uoms`, `INV_UOM`); the ungated lookup is `IIvInventoryLookupService.ListActiveUomsAsync` | code |
| Duplicate handling idiom ships: DB unique index + `when (IsDuplicateKey(ex))` (master services) / `IsUniqueViolation(ex)` (transaction services) + `SqlException.Number is 2601 or 2627`; test template `SaCustSqlServerConcurrencyTests.SqlServer_DuplicateKey_ReturnsDuplicateKey` | code |
| Tenant plumbing: `InventoryTenantContext.MaxCompanyLength = 5`; `InventoryLeftoverSite.Apply` has one overload per entity (none for the four new tables) | `InventoryTenantContext.cs` |
| Legacy item-family tables have **no RowVersion** and **no tenant in their keys** (defects 13-15) | `docs/sales-item-family-logic.md` §7 |

### 3.1 Live schema verified — Phase 0 executed 2026-09-15 (**PASS**)

Full detail: `ErpWeb/docs/sales-item-family-phase0-findings.md`. The facts that shape this plan:

| Fact | Consequence |
|---|---|
| `IvMas` and `IvMasPack` are **ABSENT**; `IvStockMaster` is present (1 row) | D1 is now a verified fact: one item authority, nothing to quarantine |
| `IvCustPriceGroup`, `IvCustPrice`, `SaDisGroupItem` are **ABSENT** | the key-widening question is moot for them — **we define the PK** (§13.4) |
| `SaItemCust` **exists with 0 rows**; PK `(ICode, CustCode, SellingUOM, MOQ)`, no `CompanyCode` in the key, no `RowVersion`, and an unmodelled `CustModel nvarchar(10)` column; neither modelled nor scripted in this repo | **Branch A** key widening, safe because the table is empty; the script is **alter-first** for this table |
| scanned schema has **no dealer-price column** | D6 is verified, not assumed |
| live widths: `IvStockMaster.ICode` **20**, `MsUOM.UOMCode` **5**, `IvClass.IClassCode` **10**, `SaCust.CustPriceCode` **20**, `IvStockMaster.SellingPrice` **decimal(18,4)** | these five numbers define the new columns (§13.4) |
| no overlapping rules, no `0/0` rows, no duplicate business keys, no invalid tenant values | the tie-break (§8.3.1) stays implemented and tested, but has nothing to resolve in this DB |
| the **sales** item lookup row already carries `SellingPrice` (`SaSoItemLookupRow` and siblings) and all four transaction pages seed `UnitPrice = item.SellingPrice ?? 0m` | the price-seed plumbing exists; the *basis* is undeclared and the `?? 0m` fallback contradicts §7 step 4 — see §7.1 and O1 |

## 4. Domain model

Seven objects, three data classes — they must not be implemented as equivalent "masters".

| Object | Domain role | Class | Status |
|---|---|---|---|
| `IvCustPriceGroup` | price list header | master | to create |
| `IvCustPrice` | item price inside a price list (per item + UOM) | rule (child of master) | to create |
| `SaItemCust` | customer-specific item override (customer part no, invoice desc, UOM, price, MOQ) | assignment + rule | to create |
| `SaDisGroup` | discount-group header (group + payment term) | master | shipped |
| `SaDisGroupItem` | item + quantity band + date window discount rule | rule | to create |
| `SaDisCust` | customer → discount-group membership | assignment | shipped |
| `SaCust.CustPriceCode` | customer → price-list assignment | assignment | shipped, unvalidated |
| `SaCust.GroupDiscount` | customer → discount-group assignment | assignment | shipped, validated |

Consequences: masters get CRUD, delete guards and activation; **rule** records get date/band validation and duplicate/overlap keys; **assignment** records get referential validation only.

## 5. Data ownership and authority

**D1 — `IvStockMaster` is the single item master.** New FKs and validations target `IvStockMaster` only. If Phase 0 finds `IvMas` present live, it is declared legacy read-only: no Blazor write path, and the finding records every process that still writes it (legacy WebForms, imports, mobile `DataService`). Any item resolution that cannot be traced to `IvStockMaster` is a Phase 0 FAIL.

**Audit columns.** `Created`/`UserID`/`Updated`/`UpdatedUID` map to `CreatedDate`/`CreatedBy`/`ModifiedDate`/`ModifiedBy` and are stamped **from authenticated server context only** — never accepted from a browser DTO. Field-level old/new audit does not exist in this repo and is **out of scope**; do not half-build one.

**Price visibility is never persistence.** `VIEW_PRICE` (§11) changes what is projected, never what is stored.

## 6. Tenant, company, branch and site scope

**D2 — Company is the tenant key for all four tables; Branch/Location are leftover stamps, not key parts; there is no fallback hierarchy.**

| Entity | Company | Branch | Site/Location | Global |
|---|---|---|---|---|
| `IvCustPriceGroup` | **key** | leftover stamp | leftover stamp | no |
| `IvCustPrice` | **key** (must equal the header's) | leftover stamp | leftover stamp | no |
| `SaItemCust` | **key** | leftover stamp | leftover stamp | no |
| `SaDisGroupItem` | **key** | leftover stamp | leftover stamp | no |

- Executable form: one new `InventoryLeftoverSite.Apply` overload per entity (the convention used by `SaCust`, `SaDisGroup`, `IvStockMaster`, `MsUom`, `IvClass`).
- **No fallback:** no master lookup in this repo cascades branch → company → global. State it in the spec; if branch-level pricing is ever required it needs its own consumer and an additive key, not an assumption.
- Evidence: v1's Phase 0 matrix ("leftover stamp on create — CONFIRMED"); legacy's tenant columns were never in any key (defects 13/14) — the port fixes that by keying on company.
- Every list, load, update, delete and delete-check filters by `CompanyCode` from the server context; a cross-company key must return `NotFound`, not a permission error.

## 7. Price resolution contract

**D4 — exact ordered algorithm.** Input: `(CompanyCode, CustCode, ICode, UOM, Qty, DocDate, PayCode)`. First valid candidate wins; the result is always *one* price plus the rule that produced it (for traceability).

```
0. PriceMethod of the customer decides the BASELINE source set:
   - "FOLLOW DEFAULT DEALER PRICE" (or any value containing DEALER)
        -> dealer price.  NO SOURCE EXISTS IN V2  -> FAIL CLOSED:
           "Dealer pricing is not supported for this customer."
           (do NOT invent a dealer-price table — D6)
   - "FOLLOW SELLING PRICE X DISCOUNT" (or blank)
        -> steps 1..4 below
1. SaItemCust row for (CompanyCode, CustCode, ICode, UOM)  -> UnitPrice
       band selection: rows with MOQ <= Qty; highest MOQ wins; MOQ blank/0 = base row.
       (Customer-specific beats everything else: it is the documented purpose of the table.)
2. IvCustPrice row for (CompanyCode, SaCust.CustPriceCode, ICode, UOM)  -> SellingPrice
       only when SaCust.CustPriceCode is non-blank.
3. IvStockMaster.SellingPrice for (CompanyCode, ICode)
       only when the document UOM equals the item's SellingUom (or StdUom when SellingUom is blank);
       a different UOM is NOT converted -> fall through to 4 (D5).
4. No candidate -> FAIL CLOSED: block the line with
   "No price found for item {ICode} / UOM {UOM}."
   Never default to 0 (that is the legacy defect being removed).
```

- **Termination is always explicit.** Zero, blank or negative stored prices are treated as "not a candidate" and fall through; a stored negative price is additionally a **validation error at save time**.
- Currency: the resolved price is only usable when its currency equals the document currency — see §7.1.
- Precedence evidence: `SaItemCust` is documented as "used when selling that item to that customer"; the legacy SO spec records "normal = customer/item UOM price; Mode 3 = highest MOQ ≤ order qty, else block Add"; price lists are the *assignment* mechanism behind `SaCust.CustPriceCode`; `IvStockMaster.SellingPrice` is the item default. **Sign-off required** (§9 row 4).
- The `SaCust.PriceMethod` / `SaDisGroupItem.EffectPrice` relationship: `EffectPrice` is **not read by any consumer query studied** (verified: `SaCustomerBL.GetDiscountItem` selects `Discount/DiscountType/Discount1/DiscountType1`; the helper selects `*` but no caller branches on it). It is therefore stored, validated against `{DEALER, SELLING}` and documented as **inert until the SO/INV contract (§8.9) defines it**. It must never override the price chosen at step 0-4 by accident.

### 7.1 Price basis and currency

**D7 — every stored commercial price is tax-EXCLUSIVE.**
| Column | Basis | Note |
|---|---|---|
| `IvStockMaster.SellingPrice` | exclusive | existing column — do not change its type |
| `IvCustPrice.SellingPrice` | exclusive | new |
| `SaItemCust.UnitPrice` | exclusive | legacy `float` — see D10 on types |

Boundary rule: the shipped line engine un-taxes an *inclusive* `UnitPrice` (`/(1+t)`). The SO/INV seed step must therefore gross up an exclusive master price (`× (1 + t)`) before assigning it to an inclusive line. **Masters never perform tax maths.**

**Live reality (Phase 0, 2026-09-15).** `IsInclusive` is supplied **per line by the caller** (the services merely reject mixed inclusivity within one document), and the four shipped transaction pages seed `Popup.UnitPrice = item.SellingPrice ?? 0m` directly from the item lookup row. So today a **tax-exclusive** master price (`IvStockMaster.SellingPrice decimal(18,4)`) is assigned into a line that the engine may treat as tax-inclusive, and a missing price becomes **0**. Neither behaviour is a master defect — both live in the *consumer*, which this phase deliberately does not modify.

Decision **O1**: v2 records the correct basis and the conversion rule in the §8.9 contract and **leaves the four transaction pages untouched** (recommended), or the pages are corrected now in a separate, regression-tested change. Silently changing shipped document entry inside a master-data phase is not acceptable.

**D8 — currency: price lists are base-currency; a mismatch fails closed.**
- `IvCustPrice` has **no** currency column (verified) → price-list prices are company base currency. Do not add a currency column without a Phase 0 finding.
- `SaItemCust.Currency` exists (`nvarchar(5)`). If it is non-blank and differs from the document currency, **fail closed** with an explicit message; do not convert implicitly and do not silently ignore the column. If conversion is ever wanted it must go through `SaCurrRate` using the fail-closed pattern (`SaCdnService.ResolveCurrRateAsync`: window must cover the document date, rate ≠ 1 for foreign).

### 7.2 Worked examples — price precedence

Customer `C`, item `I`, UOM `U`, qty 10, document date `D`. “Wins” means: the first row in §7 that produces a usable candidate, and nothing later in the chain is consulted.

| # | Situation | Outcome |
|---|---|---|
| E1 | `SaItemCust` row for (C, I, U), MOQ 0, `UnitPrice` 12.50 | **12.50** from `SaItemCust` — steps 2-4 are not evaluated |
| E2 | `SaItemCust` rows: MOQ 5 → 12.00 and MOQ 10 → 11.00; qty 10 | highest MOQ ≤ qty wins → **11.00** (5-row ignored) |
| E3 | no `SaItemCust` row; `SaCust.CustPriceCode = 'PL1'` has (I, U) at 13.75 | **13.75** from `IvCustPrice` |
| E4 | no `SaItemCust`, no price list; item `SellingPrice` 14.00, `SellingUom = U` | **14.00** from `IvStockMaster` |
| E5 | as E4 but the document UOM is `BOX` while the item's selling UOM is `PCS` | UOM does not match → candidate rejected → **blocked** (no conversion exists) |
| E6 | `SaCust.PriceMethod` contains DEALER | **fail closed** — dealer pricing unsupported (§7.3) |
| E7 | nothing matches at any step | **blocked** with the explicit message; never 0 |
| E8 | item exists but `IvStockMaster.IsActive = false` | not a candidate (the lookup is active-only); the chain may still resolve a customer-specific or price-list row for that item — if none, blocked |
| E9 | `SaItemCust.Currency = 'USD'`, document currency `MYR` | candidate rejected → fail closed (§7.1) |
| E10 | stored `SaItemCust.UnitPrice = 0` (or blank, or negative) | “not a candidate” → fall through; a negative stored value is additionally a validation error at save |
| E11 | `SaCust.CustPriceCode = 'PL1'` but `PL1` does not exist (legacy free text) | step 2 yields nothing → falls to step 3; a mismatch is not an error on its own (D-6 clause 3) |

### 7.3 Dealer mode — migration impact (measured in Phase 0)

`SaCustEntry` offers “FOLLOW DEFAULT DEALER PRICE” as a radio, so live customers can carry that value. Phase 0 counts them. If the count is non-zero the owner picks one of two recorded behaviours: (a) keep **fail closed** with an actionable message (the customer must be re-pointed to a price list or a customer-item price), or (b) an **interim mapping** where DEALER falls through to the §7 selling-price chain until dealer pricing is implemented. Silently treating DEALER as SELLING is not permitted — that is the “discard the operator's stated intent” defect this repo has already fixed once.

## 8. Discount resolution contract

**D5 — pipeline, slot mapping and JOIN/SPLIT semantics.**

```
P0  base price  = §7 result
P1  rule match  = SaDisGroupItem rows for (CompanyCode, ICode) where
        qty:   QtyFr <= Qty AND QtyTo >= Qty          (inclusive)
        date:  DateFr <= DocDate AND (DateTo IS NULL OR DateTo >= DocDate)
        class: a row with IClass = item class is more specific than IClass blank
    -> at most ONE row may match. Multiple matches are PREVENTED at save time (§8.3),
       so the runtime never has to guess.
P2  slots       = the matched row contributes at most two slots:
        Slot A: Discount  + DiscountType   (PERCENTAGE | AMOUNT)
        Slot B: Discount1 + DiscountType1  (PERCENTAGE | AMOUNT)
P3  engine call = SaInvoiceCalc.CalculateDiscountPerUnit(
        unitPrice,
        itemDiscount   = Slot A when PERCENTAGE,
        itemDiscount2  = Slot B when PERCENTAGE,
        itemDiscAmount = Slot A when AMOUNT,
        itemDiscAmount1= Slot B when AMOUNT,
        discMethod     = SaCust.DiscountMethod)
P4  rounding    = engine only (see 8.4)
```

### 8.1 JOIN and SPLIT — D5a (closes the silent-token risk)
| Token | Semantics **defined by this plan** | Engine today |
|---|---|---|
| `JOIN` | all percentage slots are summed and applied **once against the original base price**; flat amounts are added | already correct |
| `SPLIT` | percentages are applied **sequentially** (each against the price already reduced by the previous slot); flat amounts are added last | already behaves this way — **the behaviour is correct, only the name was undocumented** |

Decision: adopt both as **documented, tested** semantics. Add a pinning unit test per mode so a future change to `CalculateDiscountPerUnit` cannot silently flip SPLIT back into "unimplemented". (Alternative — rejecting SPLIT outright — would break every customer carrying that token.)

#### 8.1.1 Worked examples (unitPrice = 100.00, per-unit discount, before tax and rounding)

`ApplyPercent(p, x) = p × (1 − x/100)`, and `x = 0` is a no-op (verified in `SaInvoiceCalc.ApplyPercent`).

| Slots on the rule | Method | Arithmetic | Per-unit discount | Net unit |
|---|---|---|---|---|
| 10% + 5% | `JOIN` | (10 + 5) / 100 × 100.00 | **15.00** | 85.00 |
| 10% + 5% | `SPLIT` | 100.00 → 90.00 → 85.50 | **14.50** | 85.50 |
| 10% + amount 2.00 | `JOIN` | 10.00 + 2.00 | **12.00** | 88.00 |
| 10% + amount 2.00 | `SPLIT` | (100.00 − 90.00) + 2.00 | **12.00** | 88.00 |
| amount 2.00 + amount 3.00 | either | 0 + 2.00 + 3.00 | **5.00** | 95.00 |
| 0% + 0 | either | 0 | **0.00** | 100.00 |
| −5%, or 150%, or amount −1.00 | either | **rejected at save** (§8.3) — never reaches arithmetic | — | — |

JOIN and SPLIT differ **only** when more than one percentage slot is used (0.50 in the first row pair) — which is exactly why the two-slot mapping in §8.2 matters and why both modes need pinning tests. The function returns an **unrounded** per-unit discount; rounding happens in the caller (`Amount = Money(qty × unitPrice)`, `Net = Amount − Money(qty × discountPerUnit)`, an extra 4-dp step when `decPoint == false`, then `NetAmount = Money(net)`; header totals use `decimals = decPoint ? 0 : 2`).

### 8.2 Amounts, percentages and the shipped slot model
- Percentages apply to the base price; flat amounts are added after (`ItemDiscAmount`, `ItemDiscAmount1`).
- `SaDisGroupItem` writes exactly two slots; the engine exposes six percent slots + two amount slots. The mapping above uses percent slots 1-2 and amount slots 1-2 and **leaves slots 3-6 at 0** for this feature. Do not spread rule values across unused slots.
- Customer-level group discount (`SaDisGroup` via `SaCust.GroupDiscount` + `PayCode`) is **explicitly excluded** from this contract: the active helper ignores `CustDiscount` (verified). Record it as a known legacy capability that the SO/INV contract must address if it is ever wanted — not as an accidental omission.

### 8.3 Rule validation (save time, inside the transaction)
| Rule | Enforcement |
|---|---|
| `QtyFr > 0` | validation error (`0/0` forbidden — the legacy "all qty" row can never match either lookup) |
| `QtyTo >= QtyFr` | validation error |
| both bounds non-null | columns are non-nullable |
| at most one rule matches per item | reject a new/edited row whose band+date window overlaps an existing row for the same `ICode`, treating a blank `IClass` as "all classes" |
| `DiscountType`/`DiscountType1` ∈ {PERCENTAGE, AMOUNT} | validation error otherwise |
| percentages ≤ 100 and ≥ 0 | validation error otherwise |
| amounts ≥ 0 and ≤ the base price at evaluation time | reject negatives at save; the engine-level clamp is defined in §8.9 |
| dates inclusive, `DateFr` required, `DateTo` nullable = open-ended | validation error when `DateTo < DateFr` |
| `EffectPrice` ∈ {DEALER, SELLING} | validation error otherwise; stored but inert (§7) |

#### 8.3.1 Legacy overlap and the runtime tie-break

The save-time rule prevents **new** overlaps; it cannot retro-fix rows already in the live table (legacy's validator only de-duplicated on `ICode + IClass + QtyFr + QtyTo`, and its `0/0` special case relaxed even that). Phase 0 reports the overlapping sets per item.

Defence in depth: if more than one row matches at runtime despite the rule, the engine must resolve **deterministically** rather than taking an arbitrary row:

1. class-specific row (`IClass = item class`) beats a blank-class row;
2. then higher `QtyFr` (narrower band wins);
3. then earlier `DateFr` (more recent rule wins);
4. then lower `ID` (stable, insertion order);

and the pick is **logged** with the item code and the competing row ids. Non-deterministic pricing is never acceptable, even while legacy data is dirty. If Phase 0 finds overlaps, the owner chooses: clean the data (preferred, with a report) or rely on this tie-break — recorded in the findings.

### 8.4 Rounding
Masters store `decimal(18,4)` and **never round**. Rounding happens only at the document points: `SaInvoiceCalc.Money(v, 2, AwayFromZero)`, or 0 dp when `SaCust.DecPoint` is true. No second rounding implementation is permitted in a master service or the UI (display formatting is presentation-only).

### 8.5 Quantity and UOM policy
Quantities, band bounds and money are `decimal(18,4)` for **new** columns. UOM is matched by **exact string** — no conversion exists anywhere in this repo. A price is valid for the UOM it was stored against; the same item may hold several prices, one per UOM (`ITEM001/PCS 10.00`, `ITEM001/BOX 95.00`), which is the legacy intent and why `IvCustPrice`'s key includes `UOM`.

**Live columns keep their live types.** The legacy family stores `QtyFr`/`QtyTo`/`Discount`/`Discount1`/`SellPackSize`/`StdCustPSize` as `float`, `SaItemCust.UnitPrice` as `float`, `SaItemCust.MOQ` as `int`, and `SaDisGroupItem.DateFr`/`DateTo` as `datetime` (verified in the legacy designer). Re-typing a populated live column is a destructive change (§13.2 → STOP / DBA remediation), so:

- **table absent** → create it with `decimal(18,4)` / `date`;
- **table exists** → keep the live type, declare the divergence in the schema table (§13.4), and convert at the service boundary with an **explicit, documented scale** (no implicit `float`→`decimal` arithmetic inside a pricing path);
- date windows are compared on the **date part** (`CAST(col AS date)`), so a stored time component can never exclude the boundary day;
- re-typing is a **separate, approved DBA migration** with a data-range check — never bundled into this feature.

### 8.6 UOM and item validation sources
| Field | Source | Rule |
|---|---|---|
| `IvCustPrice.UOM`, `SaItemCust.SellingUOM` | `IIvInventoryLookupService.ListActiveUomsAsync` (`MsUom`, company-scoped, active) | validated; **not** constrained to the item's `SellingUom` |
| `SaDisGroupItem.IClass` | `IIvInventoryLookupService.ListActiveClassesAsync` (`IvClass`) | validated; blank = all classes |
| `ICode` (all four tables) | `IvStockMaster` (D1), via `SearchStockMastersAsync` / `IvStockMasterPicker` | must exist, be active, same company |
| `CustPriceCode` (on `SaCust`) | new `ISaCustLookupService` list + validate (§11.2) | see D-6 |

Use the **ungated lookup service** (`IIvInventoryLookupService`), never `IvInventoryRefService` (gated on `INV_UOM`/`INV_CLASS` ACCESS — a sales-only user would be blocked).

### 8.7 Effective dates
Applies to `SaDisGroupItem` **only**. `IvCustPrice`/`SaItemCust` have no date columns and are **declared undated**: re-pricing edits the row in place, there is no price history, and the audit columns are the only record of who changed what and when. If price effective-dating is ever required it is a new, deliberate decision with an additive column and a named consumer.

### 8.8 Keys, duplicates and identity
Per table, in this order: (1) the natural/business key stays authoritative in the database (PK if it already is, otherwise a unique index); (2) reuse an existing surrogate if one exists; (3) the natural key may serve as the page key only if it fits `Code` + `ParentCode`; (4) a new surrogate column is a last resort — never retrofit a PK onto a live populated table.

| Entity | Business key (intended) | Unique index | Page key |
|---|---|---|---|
| `IvCustPriceGroup` | `(CompanyCode, CustPriceCode)` | PK | `CustPriceCode` |
| `IvCustPrice` | `(CompanyCode, CustPriceCode, ICode, UOM)` | unique | `ICode` + parent `CustPriceCode` (fits the existing token) |
| `SaItemCust` | `(CompanyCode, CustCode, ICode, SellingUOM, MOQ)` | unique | `ICode` + parent `CustCode` (fits; `UOM`/`MOQ` shown in the grid) |
| `SaDisGroupItem` | `(CompanyCode, ICode, IClass, QtyFr, QtyTo, DateFr)` | unique, plus the rule-level overlap check | existing `ID` (reuse — never invent a second identity) |

**Key widening is a two-branch decision, not an assumption.** Every intended key above is *wider* than the live legacy key (legacy `IvCustPriceGroup` keys on `CustPriceCode` alone; `SaItemCust`'s designer PK is `(ICode, CustCode)`; `SaDisGroupItem.ID` is IDENTITY with no PK in the designer). Phase 0 supplies the live PK/index reality, then:

| Branch | What it means | Consequence to record |
|---|---|---|
| **A — DBA widens the key** (preferred) | add `CompanyCode`, widen `SaItemCust` to include `SellingUOM`/`MOQ`, after a duplicate + single-company check, with a backup and a reversal script | per-company codes become possible |
| **B — keep the legacy key** (no DDL) | company scope enforced in the application only (every query filtered by `CompanyCode`; cross-company key → `NotFound`); the unique index is added only if the data permits | a legacy key such as `CustPriceCode`-alone means **one price list code per database**, not per company |

Neither branch is picked silently — the Phase 0 findings state which applies per table, and the register row 13 records it.

Two further live-schema traps to check rather than assume:
- **Identity range**: legacy `SaDisGroupItem.ID` is `smallint` (a 32,767-row ceiling). Phase 0 reports the live type and row count; widening is a DBA item, and no page key may be built on a type the table can outgrow.
- **Code widths**: `SaCust.CustPriceCode` is `nvarchar(20)` in Blazor while legacy `IvCustPriceGroup.CustPriceCode` is `nvarchar(10)`; `MsUom.UomCode` is `nvarchar(10)` while `SaItemCust.SellingUOM` is `nvarchar(5)`. Phase 0 records the live widths; a new master takes the **narrower** width so every stored code is representable, and D-6 clause 3 tolerates existing out-of-range values.

Duplicates are then prevented in three layers: unique index, service pre-check returning `IvMasterErrorCode.DuplicateKey` (create is never a silent upsert), and a SQL Server concurrency test.

### 8.9 Deferred SO/DO/INV/CN consumer contract
The future consumer must:
1. call price resolution (§7) with the document's company/customer/item/UOM/qty/date/pay-code;
2. call discount resolution (§8) and pass the two slots through the **document's** `UnitPrice` basis conversion (§7.1) before arithmetic;
3. never re-derive price or discount in the UI — the server owns both;
4. persist the resolved price and discounts on the line (the line model already carries `UnitPrice`, `ItemDiscount`…`ItemDiscount6`, `ItemDiscAmount`/`ItemDiscAmount1`);
5. treat "no price" as a blocking error, not a zero;
6. leave `EffectPrice` and the customer-level `CustDiscount` unimplemented until a separate decision defines them.

## 9. Decision register — REQUIRED sign-off before Phase 1

**Status vocabulary** (this distinction is load-bearing):

| Status | Meaning |
|---|---|
| `DECIDED` | the policy is final; no live-data fact can change it |
| `DECIDED + P0 verify` | the **policy** is final, but a **live-data fact** is still measured in Phase 0 (e.g. whether `IvMas` exists) and could force a documented amendment |
| `BLOCKED` | policy cannot be finalised before a Phase 0 fact — none should remain when the register is signed |

The **owner** column is signed by the business owner, not by the implementer; the approval record (name, date, version) is kept in the Phase 0 findings document, and the register is versioned with this plan.
Any blank `Final decision` or unsigned row = **Phase 0 FAIL**.

| # | Decision | Final decision | Evidence | Owner | Approved | Status |
|---|---|---|---|---|---|---|
| 1 | Item-master authority | `IvStockMaster` sole authority; `IvMas` legacy read-only if present (D1) | no `IvMas` type in Blazor | ☐ | ☐ | DECIDED + P0 verify |
| 2 | Domain model | §4 classification (master / rule / assignment) | logic spec §5 | ☐ | ☐ | DECIDED |
| 3 | Tenant scope | Company = key; Branch/Location = stamps; no fallback (D2) | v1 Phase 0 matrix; defects 13/14 | ☐ | ☐ | DECIDED + P0 verify |
| 4 | **Price precedence** | §7 steps 0-4 + worked examples §7.2; first valid wins; fail closed at the end (D4) | `SaItemCust` purpose; SO spec “normal = customer/item UOM price” | ☐ | ☐ | DECIDED |
| 5 | **Discount precedence / pipeline** | §8 P0-P4; at most one matching rule; tie-break §8.3.1 | legacy `GetDiscountItem` + shipped helper | ☐ | ☐ | DECIDED |
| 6 | JOIN / SPLIT | §8.1 + worked examples §8.1.1: JOIN = sum vs original; SPLIT = sequential; both test-pinned | shipped `CalculateDiscountPerUnit` / `ApplyPercent` | ☐ | ☐ | DECIDED |
| 7 | **Dealer price** | unsupported in v2 — fail closed, no new table (D6); migration impact §7.3 | no dealer column in Blazor; `DEALER` hits are the `PriceMethod` token and `IvMSCode` CHANNEL | ☐ | ☐ | DECIDED + P0 verify |
| 8 | Tax basis | all stored prices exclusive; conversion at the document boundary (D7) | line engine un-taxes inclusive `UnitPrice` (`IsInclusive`) | ☐ | ☐ | DECIDED + P0 verify |
| 9 | Currency | price lists = base currency; `SaItemCust.Currency` mismatch fails closed (D8) | `IvCustPrice` has no currency column | ☐ | ☐ | DECIDED |
| 10 | Effective dates | `SaDisGroupItem` only; inclusive on the date part; `DateFr` required, `DateTo` nullable; overlaps rejected (§8.7) | no date columns on price tables | ☐ | ☐ | DECIDED + P0 verify |
| 11 | Quantity bands | `QtyFr > 0`, `QtyTo >= QtyFr`, inclusive, no NULLs; new columns `decimal(18,4)`, live columns keep their type (§8.5) | legacy 0/0 never matches | ☐ | ☐ | DECIDED + P0 verify |
| 12 | UOM | exact match, no conversion; validated against `MsUom`; not item-limited (§8.5/8.6) | no conversion table in repo | ☐ | ☐ | DECIDED |
| 13 | Business keys / duplicates | §8.8 table + key-widening branch (A/B) + three-layer prevention | shipped duplicate idiom | ☐ | ☐ | DECIDED + P0 verify |
| 14 | Concurrency level | header `RowVersion` (Level A) per aggregate; children in one transaction; overlap check `Serializable` (§10) | `SaLMW` precedent | ☐ | ☐ | DECIDED |
| 15 | DDL strategy | create-or-alter, additive only, STOP on incompatibility (§13) | script drift observed in this repo | ☐ | ☐ | DECIDED + P0 verify |
| 16 | Lifecycle | `Active` added to `IvCustPriceGroup`; none on `IvCustPrice`; `SaItemCust.Status` = refresh state; `SaDisGroupItem.GroupStatus` not authoritative (§12) | verified column inventory | ☐ | ☐ | DECIDED + P0 verify |
| 17 | Delete rules | parent/child blocking, no cascade, `DeleteCheckResult` incl. `Ok(message)` for deferred consumers (§12.2) | `SaSalesRefService.cs:3184` precedent | ☐ | ☐ | DECIDED |
| 18 | `VIEW_PRICE` | constant + `All` + seed + `CanAsync` + projection rule + **export gate** + deny tests (§11.1) | `VIEW_COST` precedent | ☐ | ☐ | DECIDED |
| 19 | D-6 validation contract | three clauses applied to `CustPriceCode`; width rule §8.8 (§11.2) | `ValidateSubGroupAssignmentAsync` doc comment | ☐ | ☐ | DECIDED |
| 20 | Audit actor | server context only; field-level audit out of scope (§5) | repo has no audit infrastructure | ☐ | ☐ | DECIDED |
| 21 | Migration cases | six cases with STOP / DBA remediation (§13.2) |  | ☐ | ☐ | DECIDED + P0 verify |
| 22 | SO/INV consumer | contract fixed now, implementation deferred (§8.9) |  | ☐ | ☐ | DECIDED |
| 23 | Numeric policy | new columns `decimal(18,4)`/`date`; live `float`/`int`/`datetime` columns preserved and converted at the service boundary (§8.5, §13.4) | legacy designer types | ☐ | ☐ | DECIDED + P0 verify |
| 24 | Legacy overlap | §8.3.1 deterministic tie-break adopted; Phase 0 reports overlapping sets and the owner chooses clean-vs-tolerate | legacy validator de-duped on `ICode+IClass+QtyFr+QtyTo` only | ☐ | ☐ | DECIDED + P0 verify |

### 9.1 Traceability — every decision has an implementation consequence and a test

Gate 3 (plan consistency) is satisfied only if each row below resolves. A decision with no artefact or no test is a plan amendment, not an implementation detail to be improvised.

| Decision | Implementation artefact | Verification |
|---|---|---|
| 1 item authority | FK/validation to `IvStockMaster`; item lookup wired | Phase 0 findings; item-not-found test |
| 3 tenant scope | 4 `InventoryLeftoverSite.Apply` overloads; company filter in every query | 2-company isolation tests (§24) |
| 4 price precedence | `ResolvePriceAsync` implementing §7 (single entry point) | E1-E11 worked-example tests |
| 5/6 discount pipeline | `ResolveDiscountAsync` + existing engine call | JOIN/SPLIT + slot-mapping tests (§8.1.1) |
| 7 dealer | explicit fail-closed branch, no fallback | DEALER-customer test asserting the block message |
| 8 tax basis | contract text + boundary gross-up defined in §8.9 | boundary conversion test (exclusive master → inclusive line) |
| 9 currency | mismatch rejection in the resolver | currency-mismatch test |
| 10/11 dates + bands | validators in the rule service + `Serializable` overlap check | band/date validation + concurrent-overlap tests |
| 12 UOM | `MsUom` lookup validation | invalid-UOM test |
| 13 keys/duplicates | unique indexes + service pre-check + page keys | duplicate + race tests |
| 14 concurrency | `RowVersion` gates + one-transaction aggregate save | 5 concurrency tests (§24) |
| 15/21/23 migration | `scripts/init-sales-item-family.sql` (create-or-alter) | schema diff vs §13.4 on a scratch DB |
| 16/17 lifecycle + delete | `Active` column; `DeleteCheckResult` paths | lifecycle + delete-blocking tests |
| 18 `VIEW_PRICE` | constant + `All` + seed + `CanAsync` + export gate | deny test (flag false, value persisted) + export-without-right test |
| 19 D-6 | `ValidateCustPriceCodeAssignmentAsync` three clauses | blank / unknown / existing-unknown-value tests |
| 20 audit actor | stamping from server context | audit-actor test (DTO-supplied user ignored) |
| 22 SO/INV contract | §8.9 documented; no consumer code in this phase | none (by design) — re-verified when the consumer lands |

## 10. Concurrency model

- **`IvCustPriceGroup` + `IvCustPrice` are one aggregate**: `SavePriceGroupAsync` opens one transaction, validates header → lines → duplicates → UOM → item → dates, writes both, stamps audit, commits; **any failure rolls back everything**. The header carries `RowVersion`; lines do not.
- **`SaItemCust`** carries `RowVersion` (additive column if the live table lacks one).
- **`SaDisGroupItem`** carries `RowVersion`, and the band/date overlap check runs inside a `Serializable` transaction (the `SaLMW` precedent) so two operators cannot create two overlapping rules.
- Level A (real DB `RowVersion`) is the default for all four; Level B (`SaMasterFingerprint`) is used only where a table cannot carry one, and no such case exists here.
- SQLite test caveat: `RowVersion` is forced to `ValueGenerated.Never`, so service-inserted rows return an empty token — tests must seed explicit tokens.
- Stale token → `IvMasterErrorCode.Concurrency`; the UI reloads the row (never retries a blind save).

## 11. Permission and security model

### 11.1 `VIEW_PRICE`
- New `PermissionCodes.ViewPrice = "VIEW_PRICE"`, added to `PermissionCodes.All` (otherwise it is admin-deletable and unlike every built-in), seeded by the menu SQL as `PermissionType = 'Data'`.
- Semantics follow the shipped `VIEW_COST` model: **visibility only** — the server never trusts a client-supplied price and never zeroes a persisted one.
- Projection rule: a read path whose only purpose is to render a price returns the value **only** when authorised. Take the projection decision explicitly rather than hiding an already-returned value in the component.
- **Export gate:** the export endpoints/workbooks for these screens must omit price columns (or refuse) without `VIEW_PRICE`; export must not be a bypass.
- Tests: deny case asserts the flag is false **and** the value is still persisted (mirroring `PoPrServiceTests:686`).

### 11.2 `SaCust.CustPriceCode` (D2-23, D-6 parity)
Three clauses, copied verbatim in spirit from the shipped D-6 contract: **blank is allowed; a non-blank value must exist in the caller's company; the value already on the row is tolerated** so legacy free text cannot block an unrelated edit. Methods follow the shipped naming: `ListPriceGroupsForAssignmentAsync` + `ValidateCustPriceCodeAssignmentAsync` on `ISaCustLookupService`, called from `SaCustService`'s lookup-validation block. Empty assignment list for this new master **fails closed**.

### 11.3 Rights
Each screen is gated by its own `MenuCodes` constant with `ACCESS` + `ADD`/`EDIT`/`DELETE` (+ `EXPORT`). Entry screens are not view-only shells: `Type=View` hiding is presentation, the service enforces.

## 12. CRUD and lifecycle rules

### 12.1 Active / Inactive / Deleted
| Table | Column | Semantics |
|---|---|---|
| `IvCustPriceGroup` | **`Active` (new, additive)** | retired lists are excluded from the `SaCust` assignment list; existing assignments are tolerated |
| `IvCustPrice` | none | presence = priced; removal = delete the line |
| `SaItemCust` | `Status` | **refresh state**, not activation (`NEW` / `FALSE`); no Active toggle is exposed |
| `SaDisGroupItem` | `GroupStatus` | written for compatibility, **not authoritative** (no consumer filters it); not exposed in the UI |

Standing rule: **never hard-delete historical pricing because it is no longer active**; deactivate instead. Inactive rows remain valid for historical documents and are excluded from new selection.

### 12.2 Delete rules
| Master | Blocked when | Allowed |
|---|---|---|
| `IvCustPriceGroup` | lines exist; a customer references it (`SaCust.CustPriceCode`) | with a `DeleteCheckResult.Blocked` message naming the references |
| `IvCustPrice` | never (line delete is the intended removal path) | always |
| `SaItemCust` | no consumer yet → `DeleteCheckResult.Ok("No consumer yet …")` | always, with the note |
| `SaDisGroupItem` | no consumer yet → same explicit note | always |

**No database cascade delete for business master data.** Every delete re-validates the key, tenant and row version.

## 13. DDL and migration strategy

### 13.1 Strategy
One additive, idempotent, DBA-run script (`scripts/init-sales-item-family.sql`), never executed at app startup. Both branches per object: `IF OBJECT_ID(...) IS NULL CREATE TABLE …` and `IF COL_LENGTH(...) IS NULL ALTER TABLE … ADD …`. **Never re-type or re-key an existing column.** `IF OBJECT_ID IS NULL` never converges an existing table, so the script is not the source of truth — the Phase 0 schema matrix is.

### 13.2 Migration cases (each must be reported per table in the Phase 0 findings)
| Case | Action |
|---|---|
| table absent | `CREATE TABLE` with our key/type definitions |
| table exists and matches the matrix | no-op |
| table exists, additive columns missing | `ALTER … ADD` those columns only |
| table exists with incompatible type/length | **STOP — DBA remediation required**; no silent alter |
| table exists with duplicate business keys | **STOP** — report the duplicate rows; a unique index cannot be created |
| table exists with NULL/blank tenant values | **STOP** — report; tenant normalisation is a DBA decision, not a silent update |

### 13.3 Index plan (derived from the contracts, not from PKs)
| Index | Serves |
|---|---|
| `(CompanyCode, CustPriceCode, ICode, UOM)` unique | price lookup (§7 step 2) + duplicate prevention |
| `(CompanyCode, CustCode, ICode, SellingUOM)` | customer-item lookup (§7 step 1) |
| `(CompanyCode, ICode, QtyFr, QtyTo)` + `(DateFr, DateTo)` | discount rule match (§8 P1) |
| `(CompanyCode, Active)` on `IvCustPriceGroup` | assignment lists |
| `(CompanyCode, Updated)` optional | list default sort |

Every index must be traceable to a step in §7/§8 or a list query. Phase 0 confirms what already exists so the script does not duplicate an index.

### 13.4 Authoritative schema table (EF configuration and migration-review source)

This table — not the script — is the source of truth for the EF model and for the migration review. `live` columns are confirmed in Phase 0 and any change here is a plan amendment.

| Table | Intended PK | Business key (unique index) | Tenant columns | Identity | Concurrency | Lifecycle |
|---|---|---|---|---|---|---|
| `IvCustPriceGroup` | `(CompanyCode, CustPriceCode)` | same as PK | `CompanyCode` key; `BranchCode`/`LocationCode` stamps | none | `RowVersion` (new, additive) | `Active` (new, additive) |
| `IvCustPrice` | `(CompanyCode, CustPriceCode, ICode, UOM)` | same as PK | as header (must equal the header's `CompanyCode`) | none | none (aggregate child; header version is the gate) | none |
| `SaItemCust` | `(CompanyCode, CustCode, ICode, SellingUOM, MOQ)` | same as PK | `CompanyCode` key; stamps | none | `RowVersion` (new, additive) | `Status` (refresh state, not activation) |
| `SaDisGroupItem` | `(ID)` — live identity, reuse it | `(CompanyCode, ICode, IClass, QtyFr, QtyTo, DateFr)` | `CompanyCode` key; stamps | `ID` (live type to confirm — designer says `smallint`) | `RowVersion` (new, additive) | `GroupStatus` (not authoritative) |

Live-vs-intended type divergences (from the legacy designer; Phase 0 confirms each):

| Column | Live type | Intended for absent tables | Action for existing tables |
|---|---|---|---|
| `IvCustPrice.SellingPrice` | `decimal(18,4)` | `decimal(18,4)` | none |
| `IvCustPrice.SellPackSize`, `SaItemCust.StdCustPSize` | `float` | `decimal(18,4)` | advisory only — never used in pricing arithmetic |
| `SaItemCust.UnitPrice` | `float` | `decimal(18,4)` | keep live type; explicit service-side scale |
| `SaItemCust.MOQ` | `int` | `decimal(18,4)` | keep live type; fractional MOQ unsupported (documented) |
| `SaDisGroupItem.QtyFr`/`QtyTo`/`Discount`/`Discount1` | `float` | `decimal(18,4)` | keep live type; explicit service-side scale |
| `SaDisGroupItem.DateFr`/`DateTo` | `datetime` | `date` | keep live type; compare on the date part (`CAST(... AS date)`) |
| `IvCustPrice.UOM` | `nvarchar(10)` | `nvarchar(10)` (must be ≥ live `MsUom.UOMCode`) | confirm live width |
| `SaItemCust.SellingUOM` | `nvarchar(5)` | live width (narrower of the two wins, §8.8) | confirm live width |
| `SaCust.CustPriceCode` / `IvCustPriceGroup.CustPriceCode` | `20` in Blazor / `10` legacy | master takes the **narrower** width | D-6 clause 3 tolerates longer legacy values |

**Phase 0 confirmed (2026-09-15) — the numbers below are live, not assumed**

| Column | Live | Therefore the new column is |
|---|---|---|
| `IvStockMaster.ICode` | nvarchar(**20**) | `ICode` = nvarchar(20) in all four tables |
| `MsUOM.UOMCode` | nvarchar(**5**) | `IvCustPrice.UOM`, `SaItemCust.SellingUOM` = nvarchar(5) |
| `IvClass.IClassCode` | nvarchar(**10**) | `SaDisGroupItem.IClass` = nvarchar(10) |
| `SaCust.CustPriceCode` | nvarchar(**20**) | `IvCustPriceGroup.CustPriceCode` = nvarchar(20) (the legacy 10 would reject codes `SaCust` can hold) |
| `IvStockMaster.SellingPrice` | **decimal(18,4)** | `IvCustPrice.SellingPrice` = decimal(18,4) |
| `SaItemCust` (existing) | `ICode` 20, `SellingUOM` 5, `MOQ` **int NOT NULL**, `UnitPrice` **float**, `CustICode` NOT NULL, `InvDesc` NULL, `SPart` bit, `DG`/`SG` nvarchar(5), `ProjID` nvarchar(20), **`CustModel` nvarchar(10)**, no `RowVersion` | live types preserved; add `RowVersion`; widen the PK (§8.8 Branch A) |
| `SaDisGroupItem` | absent (legacy designer said `ID` was **`smallint`**) | create `ID` as **`int IDENTITY`** — never `smallint` |

Nullable/generated columns are otherwise fixed by the live legacy layout (or by us, for the three absent tables); the only **new** generated/derived items in this feature are the additive `RowVersion` columns and `Active` on the price-group header.

## 14. Blazor UI architecture

- **List pages** use the shipped `SaRefListPageBase` (iv-chrome, `CommonDataGridEx`, server paging, `Key(code, rowVersion, parentCode)`, optional `ExportRoute`). `SupportsActivate` is opted-in only for `IvCustPriceGroup`.
- **Entry pages** use the existing shell patterns; `SaCustEntry`'s price-group box becomes an `IvCodeComboBox`/picker.
- **Picker behaviour matrix** (10 states): initial value shown (also in the read-only detail pane); open → paged active list; select → set + clear field error; cancel → unchanged; clear → blank allowed; inactive-existing → shown and kept, not silently blanked; invalid/deleted existing → tolerated and flagged (D-6 clause 3); validation failure → field-level error by key `"CustPriceCode"` (the page holds a case-insensitive `ValidationErrors` dictionary); save failure → keep input; after save → **navigate to the list route** (never the same URL — a same-URL navigation leaves a stale row version and the next save fails with a concurrency error).
- **Lookup shape:** paged + search text + max rows + active filter + company filter + sort allow-list, copying `IvStockMasterSearchRequest`/`IvStockMasterSortFields`. `List<Thing>ForAssignmentAsync` (whole-table) is acceptable **only** for small code tables — not for `IvCustPrice`/`SaItemCust`/`SaDisGroupItem`.
- No session-state working set (the legacy `"SACUSTITEMSSALES"` shared key and its cross-tab bleed are explicitly not ported).

## 15. Service and repository architecture

- New `ISaItemFamilyService` (name to be finalised in Phase 1) on `ErpWeb.Core/Sales`, returning `IvMasterOperationResult<T>` / `IvMasterErrorCode` and keys as `IvMasterKeyToken`.
- One shared generic save path per shape: header-only master (`IvCustPriceGroup`, `SaItemCust`), header+lines aggregate (`IvCustPriceGroup`+`IvCustPrice`), rule table (`SaDisGroupItem`) — mirroring `SaSalesRefService.FlatRef.SaveFlatMasterAsync` rather than four bespoke implementations.
- Lookups in `ISaCustLookupService` (customer-side fields) and a sales-side item-family lookup for list/picker data; item/UOM/class come from the ungated `IIvInventoryLookupService`.
- Repositories stay read-only helpers (paged search, tracked load, exists, delete-check); all mutation happens in the service transaction.
- Export: reuse `SaMasterRefExportWorkbooks.cs` + `SaMasterRefExportEndpoints.cs` + `SaRefListPageBase.ExportRoute`, with `EXPORT` **and** `VIEW_PRICE` enforced in the service.

## 16. Phase 0 gate (binary) — **EXECUTED 2026-09-15: PASS**

Deliverable produced: `ErpWeb/docs/sales-item-family-phase0-findings.md` (per-object sections, live column/index matrix, writer inventory, script inventory, delete-dependency matrix, open decisions, PASS/FAIL line).

**Result: PASS.** No STOP item: no incompatible type on a populated table, no duplicate business keys, no invalid tenant data, no unresolved item authority, no dealer-price source to reconcile. What Phase 0 *changed* in this plan:

1. `SaItemCust` is the **only** existing table → Branch A key widening (safe: 0 rows) and an **alter-first** script; the other three tables are created by us, so their `ICode`/`UOM`/`IClass`/`CustPriceCode` widths and PKs are now fixed numbers in §13.4 rather than intentions.
2. Dealer pricing is **verified** unsupported (no such column exists anywhere in the live schema).
3. The price-seed plumbing **already exists** (`SaSoItemLookupRow` + `UnitPrice = item.SellingPrice ?? 0m` in all four transaction pages) — this corrects the earlier “not implementable” claim, and turns the price basis into a **consumer** decision (O1) rather than a master-build blocker.
4. `SaDisGroupItem.ID` is created as `int`, not the legacy designer’s `smallint` (32,767 ceiling).

**Remaining gate:** the owner signature on the §9 register (24 rows) plus the six open decisions in §26. Until those are signed, Phase 1 does not start.

> **Update (2026-09-15):** Phase 1 onwards *was* executed against the closed `Final decision` column — Phases 0–7 are implemented and green (§0 above). The register's `Owner`/`Approved` boxes are still unsigned and remain an owner record, not a code gate. The six §26 decisions are tracked in §0 under "Still open".

**Original question bank (kept as the audit record — all items answered)**

1. Per table (`IvCustPriceGroup`, `IvCustPrice`, `SaItemCust`, `SaDisGroupItem`): EXISTS/ABSENT, row count, full column matrix (type/length/nullable/identity), PK + unique indexes + FKs, and non-null/violating tenant values. → §3.1, §13.4
2. External writers per table (legacy WebForms, `ImportCustProduct`, `UploadCustPrice`, `UpdateSalesPrice`, mobile `DataService`, any job). → findings §13
3. Item authority: `IvMas` and/or `IvStockMaster` present, row counts, which process writes each. → findings §2
4. Dealer price: confirm no source exists — expected outcome "unsupported". → findings §9 (confirmed)
5. `IvCustPriceGroup.Editable` — type, nullability, default, and whether any code reads it. → table absent; decision O4 in §26
6. `SaDisGroupItem`: `ID` PK/identity reality, unique index, live `QtyFr=0 AND QtyTo=0` rows, `GroupName`/`GroupLevel`/`Discount2/3`/`DiscountType1`/`EffectPrice`/`IClass` usage. → table absent; we define it (§6 of the findings)
7. `SaItemCust`: real PK, `Status` distribution, `DG`/`SG` meaning, `Currency` values. → findings §3
8. Value sets: `ICode`/`UOM`/`IClass` distinct values. → N/A at 0–1 rows; re-check on a production dataset
9. Whether `scripts/` already creates/alters any of these tables. → findings §14 (only `SaDisGroup` via v1)
10. Whether anything today seeds `IvStockMaster.SellingPrice` into a tax-inclusive line. → findings §11.2 (**yes** — see O1)
11. **Dealer-mode customers** count → 0 in this DB (findings §8)
12. **Discount overlaps** → table absent; none
13. **Key reality** → findings §3 (`SaItemCust` 4-part PK without `CompanyCode`)
14. **Type and width divergences** → findings §7
15. **Existing price basis in practice** → findings §11.2
16. **`CustPriceCode` value distribution** → 0 non-blank in this DB (findings §8)

**Three-stage gate (Gate 1 → 2 → 3)**

| Gate | Requirement | Failure action |
|---|---|---|
| 1 — decision closure | all 24 register rows non-blank, owner-signed, no `BLOCKED` status | stop; owner decisions missing |
| 2 — live-data verification | this findings doc is PASS, with no STOP item left open (§13.2) | stop; DBA remediation or plan amendment |
| 3 — plan consistency | every §9 decision resolves through §9.1 to an artefact **and** a test | plan amendment before Phase 1 |

Gate 3 is a checklist, not a formality: a decision that cannot be traced to a test is either an unimplemented rule or an untested one, and both are defects in an ERP pricing path.

## 17. Phase 1 — Infrastructure

Additive DDL (§13) applied and verified on a scratch DB; entities/configurations/DbSets in `ErpWeb.Model`; four `InventoryLeftoverSite.Apply` overloads; key model wired per §8.8 (page keys, `ToKeyToken`, `DeleteCheckResult` wiring); DI registration; an ADMIN-only smoke menu so the pages can be reached before grants; duplicate/`RowVersion` plumbing per §8.8/§10. Green build + tests before Phase 2.

## 18. Phase 2 — `IvCustPriceGroup` + `SaCust` price-group conversion

Master CRUD/list/export with `Active`; `SaCustEntry` price-group box converted per §11.2 (three-clause contract); delete blocked while lines or customer references exist. Green build + tests.

## 19. Phase 3 — `IvCustPrice` lines

Header+lines saved in one transaction (§10); unique key per §8.8; duplicate race test; `VIEW_PRICE` projection + export gate; list page keyed by item with the header as parent. Green build + tests.

## 20. Phase 4 — `SaItemCust`

Customer-item master with the MOQ band identity, `Status` refresh semantics preserved, `Currency` mismatch rules (§7.1), validated UOM/item/customer. Green build + tests.

## 21. Phase 5 — `SaDisGroupItem`

Rule CRUD with §8.3 validation inside a `Serializable` transaction (band/date overlap per item, class-specificity), plus the tenant/item-scoped read-only listing that replaces legacy `CustItemDiscList`. Green build + tests.

## 22. Phase 6 — `SaDisGroup` / `SaDisCust` reconciliation (verification only)

No rebuild and no second writer. Verify the shipped screens still save/list, confirm the gap list (any legacy-only field with no Blazor home), and record the invariant in the spec: **`SaDisGroup`/`SaDisCust` have exactly one Blazor implementation.**

## 23. Phase 7 — Menu, permissions, export, documentation

`menus.xml` rows + `MenuCodes` constants; `scripts/init-sales-item-family-menu.sql` with ADD/EDIT/DELETE/EXPORT permissions **and** the `VIEW_PRICE` permission seed; `MenuDeploymentParityTests` green; export endpoints registered; docs updated (`docs/sales-item-family-logic.md` gets a "Blazor v2" section stating what is implemented, what is deferred, and the §8.9 contract).

Deployment trap (documented because it has bitten before): `menus.xml` is reconciled on every startup and unknown codes are **soft-disabled** (`IsActive=0`), which removes them from navigation *and* redirects their pages to `/unauthorized`; the XML's auto-ACCESS only applies to menus it inserts. So XML **and** SQL are both required, always.

## 24. Test matrix

**Delivered in** (2026-09-15): `SaItemFamilyServiceTests.cs` (masters, VIEW_PRICE, tenant, exports, list scope), `SaItemFamilyPricingContractTests.cs` (the pure contract — §7.2 E1–E11, §8.1.1 JOIN/SPLIT and slot mapping, §8.3 matching, §8.3.1 tie-break), `SaItemFamilyResolutionServiceTests.cs` (the DB-loaded chain, legacy `float` scaling, date-part windows, tenant scope), `SaItemFamilySqlServerConcurrencyTests.cs` (the concurrency rows, on a scratch DB with `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`).

| Area | Tests |
|---|---|
| Service (SQLite) | CRUD per master; blank/existing/invalid `CustPriceCode`; UOM/class/item validation; band rules (`QtyFr>0`, `QtyTo>=QtyFr`, 0/0 rejected); overlap rejection; duplicate `DuplicateKey`; delete checks |
| Contract | price precedence order (each fall-through step), no-price → blocked (never 0); discount JOIN vs SPLIT pinned per mode; percent/amount slot mapping; price-basis gross-up at the boundary |
| Concurrency (SQL Server scratch DB, `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`) | header update/update; line update/update; header delete with stale row version; concurrent duplicate insert; concurrent overlapping band/date creation |
| Tenant | 2-company isolation per table (list, load, save, delete, delete-check) |
| Paging | lookup returns bounded pages, respects search/active/company filters |
| Security | `VIEW_PRICE` deny → flag false **and** value persisted; export without `VIEW_PRICE` omits prices; page rights (`ADD`/`EDIT`/`DELETE`); direct-URL negative smoke |
| Menu | `MenuDeploymentParityTests` with the new codes |
| Worked examples | the §7.2 price-precedence table (E1-E11) and the §8.1.1 JOIN/SPLIT/slot table executed as data-driven cases |
| Legacy data | tie-break determinism (§8.3.1) with deliberately overlapping rows; `float`→`decimal` scaling at the service boundary; `datetime` boundary-day matching |
| Manual smoke | each screen incl. pickers; negative smoke (no rights, direct URL); regression on shipped `SaDisGroup` list |

Test preconditions to state in the suite: scratch DB name contains "test"; `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` so concurrency tests cannot silently skip; test company codes ≤ 5 chars (`MaxCompanyLength`); explicit `RowVersion` tokens when seeding on SQLite.

**Not covered by a behavioural test, and why:** the §24 "Paging" row asks for bounded *pages*; the delivered surfaces are bounded *lists* (`MaxExportRows`) rendered by `CommonDataGridEx`, plus one company-scoped active-only picker. `SaItemFamilyServiceTests` therefore asserts scope, completeness and that the cap is a safety limit — see §0 item 2. A paged/searchable picker for the three large tables remains unbuilt (the screens validate their item/customer/UOM fields server-side), which is the outstanding polish item, not a correctness gap.

## 25. Deployment and rollback

**Deploy order:** (1) run `scripts/init-sales-item-family.sql` on the target DB and record the schema diff; (2) deploy the app; (3) run the menu/permission seed; (4) verify menu parity + access grants.

**Verification after deploy:** schema diff against the Phase 0 matrix (columns + indexes), `VIEW_PRICE` present and assignable, list pages reachable, one save per screen, one delete per screen, one export with and without `VIEW_PRICE`.

**Rollback:** the DDL is additive, so the script is **not auto-reverted**. Rollback = deactivate the new menus (removes navigation and access) and redeploy the previous build; the added columns/tables stay and are inert. Any *destructive* reversal (dropping a table/column) is a separate DBA script that requires explicit approval and a data-backup step — it is never bundled with an application rollback.

**Feature flag:** the menu rows are the flag. Until permissions are granted the screens are unreachable, so a partially deployed feature cannot be used accidentally.

## 26. Open items / follow-ups (explicitly deferred)

1. Dealer pricing source (D6) — unsupported until a source is identified and separately approved.
2. Customer-level group discount (`SaDisGroup` via `CustDiscount`) — not applied by the active helper; needs its own decision.
3. UOM conversion (allocation is currently strict-string) — separate initiative, referenced only.
4. Bulk import/upload screens (`ImportCustProduct`, `UploadCustPrice`, `UpdateSalesPrice`) — legacy-only writers; a port would need this contract as its input.
5. `EffectPrice` consumption — inert until defined by the §8.9 contract.
6. Price effective-dating for `IvCustPrice`/`SaItemCust` — deliberately absent (§8.7).

### Owner decisions raised by Phase 0 (block Phase 1 until signed)

| # | Decision | Recommended default |
|---|---|---|
| O1 | Price basis in the consumer: leave the four transaction pages alone (contract only) or fix them now | **defer** — they are shipped documents; changing `UnitPrice = item.SellingPrice ?? 0m` behaviour needs its own regression-tested change |
| O2 | Carry `SaItemCust.CustModel` (nvarchar(10), absent from the legacy spec) | carry as a mapped, UI-hidden column |
| O3 | Carry `SaItemCust.DG` / `SG` (strings read as int elsewhere) | carry as-is, UI-hidden, never parsed in this phase |
| O4 | Carry legacy `IvCustPriceGroup.Editable` | **drop** — the table is absent and no consumer is documented |
| O5 | Carry `SaDisGroupItem.GroupStatus` | carry, not authoritative, not exposed |
| O6 | `SaItemCust` key widening | **Branch A** (0 rows ⇒ drop/recreate the PK including `CompanyCode`) |
