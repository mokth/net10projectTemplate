# Sales Pricing Engine — Status & Handover Reference

**Purpose.** A self-contained reference for the sales pricing engine: what it is, what is finished,
what is NOT, how to verify it, and the rules that must not be broken. Written so that a future
session can act on it **without** reading `plans/plan-salesPricingEngine.prompt.md` or the original
work session.

**Status date:** 2026-09-17 · **Verified:** solution build 0 errors; test suite
**1423 passed / 0 failed / 0 skipped**; migrations applied to `ERPWeb` on `.\SQLEXPRESS`.

> **Naming note.** There are two pricing docs and they overlap:
> - `docs/sales-pricing-engine.md` — the original *plan of record* (design rationale, review record).
> - `docs/sales-price-engine.md` — **this file**, the *status / handover* view.
>
> If you only want one, keep this file and fold the plan's rationale into §10. They are intentionally
> separate for now so nothing was lost.

e-Invoice (LHDN MyInvois) is explicitly **out of scope**.

---

## 1. The system in ten lines

There is **no central `SalesPrice` table**. A line price comes from one of **three masters**, walked in
a **fixed order** by **one engine**.

| Master | Role | Keyed by |
|---|---|---|
| `SaItemCust` | **Negotiated price** — permanent, undated | customer, item, UOM, MOQ |
| `IvCustPrice` (+ `IvCustPriceGroup`) | **Price list line** — the dated / tiered / multi-currency vehicle | company, list, item, UOM, validity, band, currency |
| `IvStockMaster.SellingPrice` | **Item default** — the guaranteed last resort in every mode | company, item |

The customer-group default list is an **assignment fallback, not a price level of its own**:
`SaCust.CustPriceCode` wins; `SaCustGroup.CustPriceCode` is only reached when the customer level
produced nothing.

**A missing price BLOCKS the line. It never becomes RM 0.00.** That was the original defect this
whole effort was created to fix.

---

## 2. Summary of the original plan

`plans/plan-salesPricingEngine.prompt.md`. Objective, verbatim in spirit: *make the EXISTING pricing
engine actually drive documents, then extend the three existing masters with the missing SME
capabilities (validity, quantity bands, currency), then govern price override and add a price-inquiry
screen.*

The plan **rejected** a proposed redesign (a central `SalesPrice` table with a 7-level hierarchy)
because the repo already had a working 4-step engine plus 3 masters, and rebuilding would have
invalidated a large passing test suite.

### The ten decisions the plan locked in

1. No central `SalesPrice` table — three masters, one engine.
2. `SaItemCust` is the **permanent negotiated price** — undated, key unchanged, no DDL.
3. `IvCustPrice` is the **dated / tiered / multi-currency** vehicle. We own the table, so its key
   could be changed safely.
4. Customer group → price list is an **assignment fallback**, not a price level.
5. Prices are stored **tax-EXCLUSIVE**; grossed up once, at the document boundary.
6. Price override is **permission-gated, reason-required, and retains the original price**.
7. **Dealer price stays FAIL CLOSED** — no source exists; never silently map it to selling price.
8. **No price-history ledger.** Audit = `ModifiedBy`/`ModifiedDate` plus the override columns.
9. `CompanyCode` scopes pricing; **`BranchCode` never enters a pricing key**.
10. The **pricing method is configurable PER COMPANY** — but it only selects *eligible* sources.
    The walk **order is fixed by specificity and is never configurable**.

### The six phases

| # | Phase | Delivers |
|---|---|---|
| 1 | **Wire the engine + company pricing mode** | replaces `Popup.UnitPrice = item.SellingPrice ?? 0m` on 4 pages; adds `Company.SalesPriceMethod` |
| 2 | Persist the pricing source | `PricingSource` / `PricingRef` on the detail tables |
| 3 | Price-list upgrade | validity + quantity bands + currency on `IvCustPrice` |
| 4 | Price override governance | `PRICE_OVERRIDE` permission, reason, original retained |
| 5 | Customer group → default price list | `SaCustGroup.CustPriceCode` |
| 6 | Price inquiry + explanation ladder | `/sales/price-inquiry` |

Phase 1 alone removed a **live money defect**, which is why the plan marked it highest value.

---

## 3. Status board

### DONE — verified

| Area | State | Evidence |
|---|---|---|
| Phase 1 — engine wired into all 4 documents | DONE | `SellingPrice ?? 0m` grep-clean in `SaSo`/`SaDo`/`SaInvoice`/`SaCdn` |
| Phase 1 — per-company pricing mode | DONE + applied to dev | `Company.SalesPriceMethod`; `alter-company-sales-price-method.sql` |
| Phase 2 — provenance on all 4 documents | DONE + applied to dev | 8 `PricingProvenance_*` round-trip tests |
| Phase 3 — `IvCustPrice` validity/bands/currency | DONE + applied to dev | surrogate `Id` PK, 3 indexes, dual-range overlap rule |
| Phase 3.5 — price-list UI columns + editor | DONE | `SaCustPriceGroupList.razor(.cs)` |
| Phase 4 — override governance (5 documents) | DONE + applied to dev | `SaPriceOverridePolicy`; `PRICE_OVERRIDE` seeded |
| Phase 4.3 — override UI | DONE | `SaSo`, `SaInvoice`, `SaCdn`, `SaQt` gated + reason field |
| Phase 5 — group default price list | DONE + applied to dev | 10 tests; group level no longer dead code |
| Phase 6 — inquiry + explanation ladder | DONE | `SaItemFamilyPriceExplainer`; `/sales/price-inquiry` |
| Menu + permission deployment | DONE on dev | 6 `MenuPermission` grants, 0 duplicates |
| Fresh-DB create scripts | DONE | override columns added to all 5 `create-*.sql` |

### NOT DONE — open work

| # | Item | Why it is open | Effort |
|---|---|---|---|
| 1 | **Manual smoke of Verification steps 4–9** | Requires a browser + a human running the app | ~1 hour |
| 2 | **`PRICE_OVERRIDE` has no ROLE grant** | Seeding a permission grants nothing; needs a `RoleMenuPermission` row | 1 SQL statement, owner's decision |
| 3 | No direct test for `CompanyService` blank/unknown-token coercion | Never written | small |
| 4 | No DI container-resolution test for the pricing services | Never written | small |
| 5 | Test name typo `Explain_AgressWithTheResolvedPrice` | Should be `Agrees` | trivial |
| 6 | Zero automated UI coverage | `ErpWeb.Tests` has **no reference** to `ErpWeb.UI` | structural decision |
| 7 | `SaPriceInquiryTests.cs` does not exist | Its cases were folded into `SaLinePricingConsumerTests` | trivial, cosmetic |

**Until item 2 is done, the price box on every sales document is locked for every user** — including
administrators, unless they hold the permission through a role. See §6.

---

## 4. What is DONE — the artefacts

### Engine (pure, no DB, no UI)

| File | Contains |
|---|---|
| `ErpWeb.Core/Sales/SaItemFamilyPricing.cs` | `SaPriceSource` enum, `SaPriceSourceTokens` (**persisted contract**), `SaPriceSourceResult`, the named `Select*` selectors, `ResolveSource`, `WithinWindow`/`WithinBand`, `SaItemFamilyPriceResolver.Resolve` |
| `ErpWeb.Core/Sales/SaCompanyPriceMethod.cs` | mode tokens, `Normalize`, `ResolveSources` (the pure mode → eligible-source map), `IsEligible`, `Describe` |
| `ErpWeb.Core/Sales/SaPriceOverridePolicy.cs` | the single override rule: `IsOverride`, `NormalizeOriginal`, `NormalizeReason`, `Validate` |
| `ErpWeb.Core/Sales/SaItemFamilyPriceExplanation.cs` | `SaPriceExplanationLevel`, `SaPriceExplanation`, `SaItemFamilyPriceExplainer.Explain` |
| `ErpWeb.Core/Sales/SaItemFamilyRuleMatch.cs` | one definition of band/window/class matching, shared by price lists and discounts |

`SaPriceSourceTokens` values are **written to the database**. Never rename a token without a
migration.

### Service layer

| File | Contains |
|---|---|
| `SaSalesRefService.ItemFamily.PriceSources.cs` | `ResolveLinePricingAsync` (the 4-stage pipeline), `ExplainLinePriceAsync`, `GetSalesPriceMethodAsync`, `LoadPriceContextAsync` (shared by both entry points), `LoadPriceCandidatesAsync`, `LoadPriceListCandidatesAsync` |
| `SaItemFamilyResults.cs` | pricing DTOs, `SaLinePricingRequest`, edit VMs |
| `SaSalesRefService.ItemFamily.cs` | aggregate save, `FindOverlappingPriceLines` (the dual-range overlap rule) |
| `SaSalesRefService.cs` | 5th ctor param `ISaCustLookupService` (reuses the D-6 validation rather than reimplementing it) |

`SaSalesRefService` is constructed in **8 test files** — adding a constructor parameter means updating
all of them.

### Schema

| Table | Columns | Notes |
|---|---|---|
| `SaSODetail`, `SaDODetail`, `SaInvoiceDetail`, `SaCDNDetail`, `SaQTDetail` | `PricingSource nvarchar(40)`, `PricingRef nvarchar(60)`, `OriginalUnitPrice decimal(18,4)`, `OverrideReason nvarchar(100)` | all nullable, no default ⇒ cannot change an existing document |
| `IvCustPrice` | `Id int IDENTITY` PK, `ValidFrom`, `ValidTo`, `MinQty`, `MaxQty`, `CurrencyCode` | see §5 for why the key is a surrogate |
| `Company` | `SalesPriceMethod nvarchar(32)` | NULL = full chain ⇒ unmigrated companies unaffected |
| `SaCustGroup` | `CustPriceCode nvarchar(20)` | width matches `SaCust.CustPriceCode` |

### UI

| Screen | Route | Note |
|---|---|---|
| `SaSo.razor(.cs)` | `/sales/orders/...` | resolves + overrides |
| `SaDo.razor(.cs)` | `/sales/delivery-orders/...` | **no price control in the popup** — qty + pack only |
| `SaInvoice.razor(.cs)` | `/sales/invoices/...` | resolves + overrides |
| `SaCdn.razor(.cs)` | `/sales/{credit\|debit}-notes/...` | resolves + overrides |
| `SaQt.razor(.cs)` | `/sales/quotations/...` | resolves + overrides (was originally excluded) |
| `SaCustPriceGroupList.razor(.cs)` | `/sales/price-groups` | header + lines as one aggregate |
| `SaCustGroupList.razor(.cs)` | `/sales/customer-groups` | default price list field |
| `PriceInquiry.razor(.cs)` | `/sales/price-inquiry` | read-only |
| `AdminCompany.razor(.cs)` | `/admin/company` | pricing-mode combo + narrowing confirmation |

### Menus and permissions

- `MenuCodes.SalesPriceInquiry`, `PermissionCodes.PriceOverride` (+ listed in `PermissionCodes.All`,
  otherwise admins can delete it), `PermissionCodes.ViewPrice`.
- `ErpWeb/Menus/menus.xml` — source of truth, synced on **every** startup, and it **soft-disables**
  any `dbo.Menu` row whose code is absent from the XML. A missing row means `/unauthorized`.
- `PermissionCodes`, the `menus.xml` row and the `init-*-menu.sql` seed are **three separate
  artefacts**; all three are needed. `MenuDeploymentParityTests` guards the constant↔XML pair.

### Dev database — confirmed state

| Fact | Value |
|---|---|
| `SaQTDetail.OriginalUnitPrice` / `OverrideReason` | present, nullable |
| `Permission` rows with `PRICE_OVERRIDE` | 1 |
| `MenuPermission` grants for `PRICE_OVERRIDE` | **6** (SA_SO, SA_DO, SA_INVOICE, SA_QT, SA_CN, SA_DN) |
| Duplicate grants | 0 |
| `PRICE_OVERRIDE` granted to any ROLE | **NO** ← open item 2 |

---

## 5. Load-bearing rules — do not break these

1. **The 4-stage pipeline order is load-bearing.** Price → tax-basis conversion → discount →
   document calculation, and **only stage 4 may round**. Stage 3 must consume the *stage 2* price.
   `SaInvoiceCalc.CalculateLine` derives the per-unit discount from the line's raw `UnitPrice` and
   only then divides by `(1 + t)`, so resolving the discount against the tax-exclusive price would
   double-scale it. Pinned by `SaCompanyPriceMethodTests`.
2. **Echoing the resolved price back is not an override.** This is what keeps the ordinary path
   permission-free and `NULL`-storing.
3. **The override declaration is deliberately NOT copied** SO→INV / DO→INV / INV→CDN, while
   `PricingSource`/`PricingRef` **are**. Copying `OriginalUnitPrice` would demand `PRICE_OVERRIDE`
   from a clerk who never made the decision. `FromDto` *does* carry it, so an unrelated edit cannot
   erase it.
4. **A reopened line adopts its loaded price as the baseline** (`Popup.OriginalUnitPrice ??= line.UnitPrice`).
   Without it, editing an old line is not an override — no reason demanded, nothing recorded.
5. **Never re-price a reopened line.** The price moves only when a pricing *input* changes, so
   editing an unrelated field can never silently move a draft to today's price.
6. **`IvCustPrice` needs a surrogate key.** Two quantity bands legitimately start on the same
   `ValidFrom`, so the natural key **cannot** contain `ValidFrom` and stay unique. `ValidFrom` and
   `MinQty` are NOT NULL purely so the unique index can contain them (SQL Server treats NULLs as
   equal in a unique index). `1900-01-01` = "always"; `MinQty 0` = "any quantity".
7. **Overlap requires BOTH ranges.** Bands overlapping in different periods are legitimate; windows
   overlapping over different bands are legitimate. A naive rule rejects valid SME data.
8. **Currency mismatch fails closed**, it does not fall through — but only when candidate lines
   actually exist for the item.
9. **DEALER fails closed.** No dealer price source exists anywhere in the repo.
10. **The company mode is read server-side** and cannot reorder the walk.
11. **`Item Default` is never removable** — a missing price must still resolve.
12. **`IvCustPrice.CurrencyCode` IS in the unique index** (deliberate deviation from plan 3.2, which
    contradicted 3.4). Leaving it out would make mixed-currency lists illegal.
13. **Any `Serializable` save must catch `IsSerializationConflict`** and return `Concurrency`. Its
    omission was already a real defect once.

---

## 6. Deployment

### Script order

| Situation | Steps |
|---|---|
| **Fresh database** | `init-sales-item-family.sql` (already contains the Phase 3 `IvCustPrice` shape). The `alter-*` scripts are then unnecessary for `IvCustPrice` — the create scripts carry the detail-table and `Company` columns. |
| **Existing database** | `alter-ivcustprice-phase3.sql` **before** using the pricing screens, then `alter-sa-detail-pricing-source.sql`, `alter-sa-detail-price-override.sql`, `alter-saqt-detail-price-override.sql`, `alter-company-sales-price-method.sql`, `alter-sacustgroup-pricelist.sql`, then the `init-*-menu.sql` seeds. |

`init-sales-item-family.sql` prints a loud `STOP:` when it finds a pre-Phase-3 `IvCustPrice`, so a
divergent schema is never silent.

### The permission is seeded but NOT granted

Seeding a `Permission` row grants nothing to anybody. To let a role override prices:

```sql
-- dbo.RoleMenuPermission uses IsAllowed, NOT IsActive (it is the one grant table that differs).
INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsAllowed, CreatedDate, CreatedBy)
SELECT @roleId, mp.MenuId, mp.PermissionId, 1, SYSUTCDATETIME(), N'DEPLOY'
FROM dbo.MenuPermission mp
JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE p.PermissionCode = N'PRICE_OVERRIDE'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.RoleMenuPermission r
      WHERE r.RoleId = @roleId AND r.MenuId = mp.MenuId AND r.PermissionId = mp.PermissionId);
```

### Two SQL batching traps (both already handled in the scripts)

- SQL Server compiles a **whole batch before executing it**, so a batch that ADDS a column and then
  REFERENCES it cannot compile. Use a separate `GO`, or `EXEC sp_executesql`.
- `RETURN` exits only its **own** batch. A guard using `RETURN` does **not** stop the batches below
  it — every block needs its own table-existence check. (This caused a real bug: a script warned
  "menu tree missing" and then created a parentless row anyway.)

### Applied-to-dev record

All of the above have been applied to `ERPWeb` on `.\SQLEXPRESS`, each verified **twice** for
idempotency (second run a clean no-op).

---

## 7. How to verify

### Build and test

```powershell
dotnet build ErpWeb.slnx --nologo -v:q
```

```powershell
# RUN THESE TWO LINES AS THEIR OWN COMMAND, THEN RUN dotnet test SEPARATELY.
# Putting the connection string on the same line as dotnet test TRUNCATES it at the first ';'
# and produces a confusing "requires a scratch SQL Server" failure.
$env:ConnectionStrings__SqlServerTestConnection = 'Server=.\SQLEXPRESS;Database=ERPWeb_test;Trusted_Connection=True;TrustServerCertificate=True;'
$env:ERPWEB_REQUIRE_SQLSERVER_TESTS = '1'
```

```powershell
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --nologo
```

**Expected: `Failed: 0, Passed: 1423, Skipped: 0`.**

### Two verification traps

1. **`Skipped: 0` is the evidence that matters.** Most SQL Server concurrency classes `return`
   early when no scratch server is configured, and an early return is reported as **PASSED**. A green
   suite can therefore hide that the SQL tests never ran. `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` turns
   that self-skip into a **failure**, so it is the only way to prove they executed.
2. **`ErpWeb.Tests` does NOT reference `ErpWeb.UI`.** `dotnet test` never compiles a `.razor` or
   `.razor.cs` file. **Always run `dotnet build ErpWeb.slnx` after touching `ErpWeb.UI`**, or a
   broken page ships as "verified".

The scratch database must already exist — the concurrency harness probes `CanConnect()` before
anything calls `EnsureCreatedAsync`, so a first run against a non-existent DB fails spuriously.

### Manual smoke — the open item (steps 4–9 of the original plan)

Not yet performed. Needs the app running (`dotnet run --project ErpWeb`, `http://localhost:5000`).

| # | Step | Pass condition |
|---|---|---|
| 4 | Add a line per price source: customer special price, price list, item default, **and a case with no price at all** | the three resolve to the right number; the no-price case is **BLOCKED with a message, NOT RM 0.00** |
| 5 | Toggle the line's *Tax inclusive* flag | the stored price grosses up/down consistently **and** the persisted discount still matches what the document engine independently recomputes |
| 6 | SO → Invoice, then change the price list | the invoice keeps the SO's **price and source** (no re-resolve) |
| 7 | Override, as a user **without** `PRICE_OVERRIDE`, then with it | without: price is read-only. With: changing it demands a reason and `OriginalUnitPrice` is recorded |
| 8 | Price Inquiry vs the document line | the same number |
| 9 | Set mode to `ITEM_DEFAULT_ONLY`, then back to NULL | a line that used `SaItemCust` switches to the item default and back; a **tampered client value changes nothing** (server-side read) |

Before step 7 can pass, complete §6's role grant (open item 2).

---

## 8. Test map

Suite total: **1423 executed**. `--list-tests` reports 1407 names because a `[Theory]` counts once
there but expands per data row at runtime.

### Pricing-specific

| Test class | Tests | Covers |
|---|---|---|
| `SaCompanyPriceMethodTests` | 43 | pure mode logic, eligible-source mapping, **pipeline order** |
| `SaItemFamilyPricingContractTests` | 49 | pure resolution contract (E1–E11), discount JOIN/SPLIT, tie-break, tax basis |
| `SaLinePricingConsumerTests` | 46 | DB-backed: source chain, gross-up, block-on-no-price, UOM mismatch, group fallback, explanation ladder |
| `SaItemFamilyServiceTests` | 40 | price-group aggregate save, VIEW_PRICE masking, band/date validation, overlap rejection, export |
| `SaItemFamilyResolutionServiceTests` | 24 | DB-loaded chain, legacy float scaling, date-part windows |
| `SaItemFamilySqlServerConcurrencyTests` | 5 | duplicate insert race, overlapping-rule race, stale-version delete |

### Contain the override tests (whole-class counts)

`SaSoServiceTests` (73) holds the 4 override tests — recorded-and-retained, denied without
permission, reason required, no-override echo. `SaQtServiceTests` (42), `SaInvoiceServiceTests` (68),
`SaDoServiceTests` (58), `SaCdnServiceTests` (39) cover their own documents; `SaQtSqlServerConcurrencyTests` (2)
covers the quotation.

### Known coverage gaps

- No test reaches `ErpWeb.UI` (§7 trap 2). The SO→INV provenance **copy** is asserted only by reading
  the code and by manual smoke step 6.
- `CompanyService` blank/unknown-token coercion has no direct test.
- No DI container-resolution test proving the pricing services compose.
- Some gate tests hold **private copies** of messages rather than referencing constants, so changing
  a user-facing message requires updating those tests.

---

## 9. Deviations from the plan, and open questions

**Accepted deviations** (all deliberate, all documented in code):

| # | Plan said | Implemented | Why |
|---|---|---|---|
| 1 | `CurrencyCode` out of the unique index (3.2) | **in** the index | 3.4 requires MYR + USD tiers on one band to coexist; the two clauses contradict |
| 2 | Phase 3.5: key new lines on a temporary negative `Id` | keyed on the **natural key** (item; UOM; effective-from; band floor; currency) | avoids a synthetic identity entirely; item+UOM stopped being unique once tiers existed |
| 3 | Phase 6.4: grant ACCESS **+ VIEW** | **ACCESS only** | the screen is read-only, so VIEW grants nothing |
| 4 | `SaPriceInquiryTests.cs` | folded into `SaLinePricingConsumerTests` | avoids duplicating a fixture |
| 5 | `SaDo` override UI | VM fields only, **no price control** | the DO popup has no price field at all |
| 6 | `SaQTDetail` excluded from override governance | **included** | a quote is the *first* place a price is offered; excluding it left `SaQt.razor` with an editable price and no gate while every other document enforced one |
| 7 | Phase 5.3 "only when the customer's own is blank" | implemented as **walk order** | a customer list with no line for the item correctly falls through to the group; that is the intended fall-through, not a bug |

**Open questions for the owner:**

1. **Currency placement.** Per-LINE `CurrencyCode` (chosen) allows mixed currencies in one list.
   Per-HEADER (`IvCustPriceGroup.CurrencyCode`) is more SME-intuitive ("EXPORT LIST = USD").
   Worth revisiting if mixed-currency lists confuse users.
2. **`SaItemCust` stays undated.** If a customer ever needs dated negotiated prices, put them on a
   dedicated price list — do not widen the legacy primary key.
3. **If an order other than the fixed specificity order is ever needed**, add a new named mode. Do
   **not** make the order configurable — a configurable order is how pricing becomes unexplainable.
4. **Dealer price stays fail-closed** until a real source is found.

### Adjacent, out of plan — flagged, not actioned

Seven inventory transaction pages have an editable `UnitPrice` with **no** `PRICE_OVERRIDE` gate:
`IvMiscIssue`, `IvMiscReceipt`, `IvScrap`, `IvStockAdjustment`, `IvStockReturn`, `IvStockTransfer`,
`IvVendorReturn`. These are inventory **valuation**, not sales pricing, so they were deliberately
left alone. Revisit only if valuation input is meant to be governed too.

---

## 10. Reference

**Original plan:** `plans/plan-salesPricingEngine.prompt.md` (full phased plan, review record,
verification steps). Superseded as a *status* source by this file, retained for design rationale.

**Full plan of record:** `docs/sales-pricing-engine.md` (the design narrative).

**Global settings registry:** `docs/app-settings.md` — the dynamic parameter table (`dbo.AdSmParam`) that now
holds scattered module switches, and which shows `SALES.PRICE_METHOD` as a READ-ONLY projection over
`Company.SalesPriceMethod`. The method itself is still edited on `/admin/company`; the registry never
becomes a second place to write it.

**Superseded legacy doc:** `docs/sales-item-family-logic.md` carries a "PARTLY SUPERSEDED" banner —
it documents the LEGACY adapter behaviour and the pre-Phase-3 schema (3-level chain, no group level,
no validity/bands/currency, `CustPriceCode nvarchar(10)`). Do not treat it as current for pricing.

### Key symbols, quick index

| Looking for | Go to |
|---|---|
| The walk order / which source wins | `SaItemFamilyPriceResolver.Resolve`, `ResolveSource` |
| The mode → eligible sources map | `SaCompanyPriceMethod.ResolveSources` |
| The 4-stage pipeline | `SaSalesRefService.ItemFamily.PriceSources.cs` → `ResolveLinePricingAsync` |
| The override rule | `SaPriceOverridePolicy` |
| Why a price did not apply (support) | `SaItemFamilyPriceExplainer.Explain` + `/sales/price-inquiry` |
| Band/window/class matching | `SaItemFamilyRuleMatch` |
| Overlap rejection | `SaSalesRefService.ItemFamily.cs` → `FindOverlappingPriceLines` |

---

## 11. Change log

- **2026-09-17** — Created. Captures the closed `SaQt` override gap, the fresh-DB create-script fix,
  and the `RoleMenuPermission.IsAllowed` seed-script fix. Verified 0 build errors and
  1423/1423/0-skipped.
