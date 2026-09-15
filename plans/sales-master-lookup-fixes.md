---
name: Sales master lookup fixes
overview: Convert the remaining free-text reference fields on the Sales master screens to the ungated lookups that already ship, add the two missing lookups (customer, salesman), and wire the one customer reference field that no service validates (SaCust.SalesmanCode) through the D-6 contract. Reviewer pass 1 (9.2/10) folded in — see section 0.
todos:
  - id: svc-customer-lookup
    content: Add ISaCustLookupService.SearchCustomersAsync (ungated, company-scoped, bounded; database-side filter then projection then Take) + tests incl. cross-company exclusion
    status: not-started
  - id: svc-salesrep-lookup
    content: Add ISaCustLookupService.ListSalesRepsForAssignmentAsync (ungated; IsActive != false so a legacy NULL stays active) + tests
    status: not-started
  - id: svc-salesman-validation
    content: Add ValidateSalesmanCodeAssignmentAsync and call it from SaCustService.AddLookupValidationErrorsAsync (D-6 three clauses; a new cross-company value is rejected, an unchanged stored value is tolerated) + tests
    status: not-started
  - id: screens-item-family
    content: "Tier 1 — SaItemCustList (Customer, Item, Selling UOM, Currency), SaDisGroupItemList (Item, Item class), SaCustPriceGroupList (line Item, UOM)"
    status: completed
  - id: screens-tier2
    content: "Tier 2 — SaDisGroupList (Pay Code combo + Status option list; Discount Type left as free text — see section 4.6), SaSalesRepList (State, Country), SaLMWList (CustCode combo + CustName derived read-only per section 4.5)"
    status: completed
  - id: screens-custentry
    content: SaCustEntry salesman combo + SaCustList filter popup (Salesman — equality filter, so a combo; Area)
    status: completed
  - id: numeric-coords
    content: "DEFERRED — not part of this pass: SaAreaList / SaCountryList latitude+longitude numeric editors (section 9). Do not implement here."
    status: not-started
  - id: change-control
    content: Inspect SaLMWList save/update and the SaCustList filter query before editing; no change to lookup filtering, scoping, active handling, sorting, null handling, save behaviour, validation or authorization beyond the salesman check; final diff review
    status: completed
  - id: verify
    content: "Build + full test suite DONE (solution 0 errors; 1266 passed / 0 failed / 0 skipped against the scratch SQL Server). Manual per-screen smoke, negative smoke and cross-company smoke are still OUTSTANDING — they need a running app."
    status: not-started
isProject: false
---

# Sales master lookup fixes

**Scope:** presentation-layer reference fields only. Every field in this plan is already validated server-side on save; the pickers exist so an operator cannot *create* the bad value in the first place. The single exception is `SaCust.SalesmanCode`, which nothing validates today — that is a service fix, not a UI one, and it is in scope here (todo `svc-salesman-validation`).

**Supersedes:** the "UI field-level lookups are still text boxes" open item recorded in `plans/sales-item-family-v2-plan.md` §0/§24 and in `docs/sales-item-family-logic.md` §11.1.

**Universe checked (2026-09-15):** all 22 sales-master screens. Verified by grepping `MenuCodes.Sales*` across `ErpWeb.UI/**/*.razor` — every sales-master page lives in `ErpWeb.UI/Sales/Masters/`; none are in `Admin/` or elsewhere. Field inventory taken from every `DxTextBox @bind-Text="@EditModel.…"` (60 hits) plus every `IvCodeComboBox` / `DxComboBox` / `IvStockMasterPicker` (27 hits).

## 0. Review record

> **Status: revision 2 — reviewer feedback folded in. Not started.** The architecture is unchanged; the feedback was field classification, contract precision, scope discipline and test closure. Nothing below reopens a settled decision.

| Pass | Score | Verdict | Disposition |
|---|---|---|---|
| Reviewer pass 1 — field inventory, `SaLMWList.CustName` persistence, query shape, clear-button semantics, missing tests | 9.2/10 | approved with minor revisions | 8 required improvements, 4 added tests, an implementation order and a scope cut — all folded in below |

Readiness after this pass: **9.7/10**; the residual risk is execution discipline (§7 change control), not design.

The reviewer's stated strengths — scope discipline, the 22-screen inventory, the ungated-service choice, the bounded customer lookup, the D-6 validator pattern and service-side integrity — are treated here as **non-regression criteria**: a later revision may not trade any of them away.

### Feedback → where it is answered

| # | Feedback | Where it is now answered |
|---|---|---|
| R2.1 | "19 fields" mixed editable, derived, validation-only and excluded entries | §1.1 — split into 15 reference lookups + 2 vocabulary option-lists + 1 derived read-only + 1 excluded (total 19) |
| R2.2 | `SaLMWList.CustName` persistence described but not defined | §4.5 — it stays a stored denormalisation; the UI derives it, the persistence contract is untouched, and the save path must be inspected before the control changes |
| R2.3 | `SearchCustomersAsync` query shape not pinned | §3.1 — normalized term, predicate → order → projection → `Take`, nothing materialized early |
| R2.4 | `IsActive != false` looked accidental | §3.2 — declared intentional, with the shipped precedent that already depends on it |
| R2.5 | Clear-button behaviour for optional references undefined | §4.0 rule 1 — a **general** rule for every converted field, not just Tier 1; blank tolerance is unchanged |
| R2.6 | `SaCustList` Salesman filter left as "decide first" | §4.3 — **resolved by inspection**: the filter is an equality predicate, so it becomes a combo |
| R2.7 | No explicit "do not change semantics" instruction | §7 change-control rule + the `change-control` todo |
| R2.8 | Numeric coordinates were in scope at low priority | §4.4 / §9 — deferred out of this pass |
| R3.1–R3.4 | Four missing tests | §5 (four new rows: cross-company salesman, cross-company customer, clear optional lookup, derived `CustName`) |
| R4 | No implementation order | §8 |

## 1. Findings — 9 pages, 19 field entries (classification in §1.1)

| # | Page | Field(s) | Line(s) | Action |
|---|---|---|---|---|
| 1 | `SaItemCustList` | Customer, Item, Selling UOM, Currency | 62, 70, 87, 123 | Tier 1 |
| 2 | `SaDisGroupItemList` (Item Discounts) | Item, Item class | 62, 70 | Tier 1 |
| 3 | `SaCustPriceGroupList` | line Item, line UOM | 92, 97 | Tier 1 |
| 4 | `SaDisGroupList` | Pay Code, Status, Discount Type | 70, 87, 94 | Tier 2 |
| 5 | `SaLMWList` | Customer Code, Customer Name | 71, 79 | Tier 2 |
| 6 | `SaCustEntry` | Salesman | 309 | also needs validation |
| 7 | `SaCustList` (filter popup) | Salesman, Area | 158, 165 | combo — the filter is an equality predicate (§4.3) |
| 8 | `SaSalesRepList` | State, Country | 104, 114 | Tier 2 |
| 9 | `SaTaxGroupList` | Tax GL code | 88 | **blocked — see §2** |

**Clean (13 pages, no action):** `SaCurrRateList` (Currency already a `DxComboBox`), `SaShippingLeadTimeList` (`Type` already a `DxComboBox`), `SaDisGroupItemList`'s discount types + effect price (combos), `SaCustList`'s Status/Type/Group filters (combos), `SaTaxGroupList`'s Company/Branch/Location (read-only), and the pure code+description masters where the code *is* the key: `SaAreaList`, `SaCommentList`, `SaCountryList`, `SaCurrencyList`, `SaCustGroupList`, `SaCustSubGroupList`, `SaCustTypeList`, `SaPaymentTermList`, `SaShipViaList`, `SaSOTypeList`, `SaCustPriceList` (read-only listing + price-group combo).

### 1.1 What those 19 entries actually are

The §1 table is an inventory of every field this pass *touches*, not 19 interchangeable lookups. Splitting them is what stops a derived field (`SaLMWList.CustName`) or a vocabulary field (`SaDisGroupList.Status`) being implemented as an independent picker.

| Class | Count | Fields |
|---|---|---|
| Editable **reference** fields → data-driven lookup | 15 | `SaItemCustList`: Customer, Item, Selling UOM, Currency · `SaDisGroupItemList`: Item, Item class · `SaCustPriceGroupList`: line Item, line UOM · `SaDisGroupList`: Pay Code · `SaSalesRepList`: State, Country · `SaLMWList`: Customer Code · `SaCustEntry`: Salesman · `SaCustList` filter: Area, Salesman |
| Editable **vocabulary** fields → fixed option list | 2 (1 converted) | `SaDisGroupList`: Status (`NEW` / `FALSE`, converted) · Discount Type (**left as a free-text box — recorded deviation, §4.6**) |
| **Derived / read-only** | 1 | `SaLMWList.CustName` — denormalised from `SaCust.CustName`, never an independent input (§4.5) |
| **Excluded** — no data source exists | 1 | `SaTaxGroupList` Tax GL code (§2) |
| **Total** | **19** | |
| *Validation-only* | *1 — already counted above* | `SaCust.SalesmanCode` is the one converted field that also gains a server-side check (§3.3). It is **not** a twentieth field. |

`SaAreaList` / `SaCountryList` latitude and longitude are **not** part of the 19: they are numeric columns, not reference fields, and they are deferred (§9).

**Related document:** `plans/sales-master-lookup.md` carries the wider lookup contract — combo width filtering (`MaxICodeLength` / `MaxCodeLength`), retired-value appending, server-derived descriptions and vocabulary validation. This plan is the deliberately narrower, immediately implementable subset and does not depend on that one being approved (§7).

## 2. Two things this plan deliberately does **not** do

| Field | Why not |
|---|---|
| `SaTaxGroup.TaxGlCode` | There is **no chart-of-accounts / GL master anywhere in this repository** (grep for `GlAccount` / `MsGl` / `ChartOfAccount` finds nothing). A combo would have no data source. Leave it as text and do not let a later pass "fix" it into an empty dropdown. |
| Seeding the price from the item picker | `IvStockMasterLookupRow` carries `ICode, IDesc, IType, IClassCode, StdUom, DefWarehouse, DefLocation, LotControl, PurchasePrice` — **no `SellingPrice`/`SellingUom`** (item-family plan R-9). Prefilling a price needs that DTO extended or a separate fetch; that is decision O1 in the item-family plan, not this one. |

## 3. Service work first (the two screens cannot be fixed without it)

### 3.1 `ISaCustLookupService.SearchCustomersAsync` — for `SaItemCustList` + `SaLMWList`

There is no ungated customer list today: `ISaCustService.SearchAsync` gates on the customer-master menu, `SaSoService.GetLookupsAsync` on sales-order access — either would lock a sales-master user out of their own popup. Add to the already-ungated, company-scoped `SaCustLookupService` (same shape as `ListPriceGroupsForAssignmentAsync`):

```csharp
Task<IReadOnlyList<IvCodeLookupRow>> SearchCustomersAsync(
    string? searchText = null,
    int maxRows = 200,
    CancellationToken cancellationToken = default);
```

Implementation notes — **the query shape is a requirement, not a suggestion** (R2.3):
- `_tenant.TryCompanyScope()` → `[]` when null (mirrors `ListPriceGroupsForAssignmentAsync`).
- Normalize the term first: null/whitespace → no term predicate at all (the picker then shows the first `limit` customers by code); otherwise `.Trim()` once. Do not lowercase in the expression — the provider's `Contains` translation is already case-insensitive on both engines in use.
- Build it as **one translation unit, in this order**: `CompanyCode == scope.CompanyCode` **and** `IsActive` → term match (`CustCode.Contains(term) || CustName.Contains(term)`) → `OrderBy(CustCode)` → `Select(...)` into `IvCodeLookupRow` → `Take(limit)`.
- **Nothing materializes early**: no `ToList` / `AsEnumerable` / `Count` / `Skip` before the `Take`, and no client-side filtering afterwards. A Blazor Server picker must never pull the customer table into the circuit (item-family plan §14 / N-7).
- `Math.Clamp(maxRows, 1, 500)` before the `Take`.
- `SaCust.IsActive` is a non-nullable `bool` → no null handling (unlike the salesman lookup, §3.2).
- Return `IvCodeLookupRow { Code = CustCode, Desc = CustName }` so the combo shows "code — name".

### 3.2 `ISaCustLookupService.ListSalesRepsForAssignmentAsync` — for `SaCustEntry` + the `SaCustList` filter

`ISaSalesRefService.ListSalesRepsAsync` exists but gates on `MenuCodes.SalesSalesRep` ACCESS, which a customer-only user does not have. Add to `SaCustLookupService`:

```csharp
Task<IReadOnlyList<IvCodeLookupRow>> ListSalesRepsForAssignmentAsync(CancellationToken cancellationToken = default);
```

Implementation notes:
- Company-scoped, active-only: `SaSalesRep.IsActive` is `bool?` → `x.IsActive != false`.
- **`NULL` meaning "active" is intentional (R2.4).** Do not "fix" it to `== true`. The shipped post-time salesman checks already treat `NULL` as acceptable — `SaInvoiceService.cs:3055-3068` and `SaCdnService.cs:2081-2094` both query `CompanyCode + SrepCode` and reject only `rep is null || rep.IsActive == false`. Tightening the lookup would hide exactly the codes those documents accept, i.e. the combo could not display a reference the post gate deems valid.
- `Code = SrepCode`, `Desc = SrepName`, ordered by `SrepCode`.
- Small code table, so a whole-list read is acceptable here (unlike the three large item-family tables).
- `SaSalesRep.State` / `Country` are free text and **are not validated today**. The combos in §4.2 are therefore UI-only — do not add a check for them here.

### 3.3 `ValidateSalesmanCodeAssignmentAsync` — the missing server-side check

`SaCust.SalesmanCode` is **written but never validated**: `SaCustService` only copies it (`:557`) and reads it back (`:807`, `:854`, `:937`). That is the same defect class D-6 fixed for `SubGroupCode`/`CustPriceCode`. Add the three-clause contract and wire it in:

```csharp
Task<bool> ValidateSalesmanCodeAssignmentAsync(
    string? code, string? existingCode, CancellationToken cancellationToken = default) =>
    ValidateLegacyOrFailClosed(code, existingCode, ListSalesRepsForAssignmentAsync,
        allowLegacyEmptyBypass: true, cancellationToken);
```

- **Three clauses** (quote them in the doc comment, as `ValidateSubGroupAssignmentAsync` does): blank is allowed; a non-blank value must exist in the caller's company; **the value already on the row is tolerated**, so a legacy free-text salesman code cannot block an unrelated edit.
- **Cross-company is a case of clause 2, not clause 3** (R3.1): the lookup is company-scoped, so a **new** value naming another company's salesman must be rejected. An unchanged value that happens to belong to another company is still tolerated by clause 3 — the tolerance is a snapshot string comparison and must never read another company's data to decide.
- Call it from `SaCustService.AddLookupValidationErrorsAsync` (≈ `SaCustService.cs:678`), beside the Group/SubGroup/DisGroup/CustPriceCode checks (`:689`, `:697`, `:739`, `:744`), keyed on `nameof(model.SalesmanCode)`.
- `allowLegacyEmptyBypass: true` because `SalesmanCode` is legacy-adjacent — the convention for such fields (new masters fail closed).

## 4. Screen work

### 4.0 Rules that apply to **every** field changed in this section

These hold for §4.1, §4.2 and §4.3 alike — Tier 1 is not special.

1. **Blank stays legal, and clearing must write blank** (R2.5). For every nullable reference field — Customer, Selling UOM, Currency, Item class, Salesman, Pay Code, State, Country, Area — clearing the control must write the model's existing blank/null representation (`NullIfWhiteSpace` / `null`, exactly what the save path already expects) and must never leave the previous code behind. `IvCodeComboBox` keeps its clear button (`ClearButtonDisplayMode.Auto`). The server-side contract is unchanged in **both** directions: clearing an optional field must still save, and a *non-blank unknown* code must still be rejected.
2. **Key/identity fields stay read-only on edit.** Where the service rejects a key change (Customer / Item / UOM on the item-family tables), the picker or combo is `Enabled="@(!IsEditMode)"` — preserving today's `ReadOnly="@IsEditMode"` behaviour. Do not "improve" it into an editable key.
3. **No picker is the integrity boundary.** Nothing in this section moves, weakens or duplicates validation; the service remains the only gate. A bad code posted straight to the service, with no picker involved, must be rejected exactly as it is today (§6 step 4).
4. **Options load when the popup opens** (in `OnNewClickAsync` / `OnEditClickAsync`, before `PopupVisible = true`), not from the page lifecycle — it keeps the picker fresh and avoids touching `SaRefListPageBase`.
5. **No new filtering, width limiting, retired-value appending or server-derived descriptions.** Those belong to the wider contract (`plans/sales-master-lookup.md` §3.1 / §3.5 / §3.7) and would turn this presentation pass into new repository, control and service behaviour (§4.1 rule 6, §7).

### 4.1 Tier 1 — the item-family screens (8 reference fields)

| Page | Change |
|---|---|
| `SaItemCustList` | Customer → `IvCodeComboBox` over `CustomerOptions`; Item → `IvStockMasterPicker` (`Selected="OnItemSelectedAsync"`); Selling UOM → `IvCodeComboBox` over `UomOptions`; Currency → `IvCodeComboBox` over `CurrencyOptions` |
| `SaDisGroupItemList` | Item → `IvStockMasterPicker`; Item class → `IvCodeComboBox` over `ClassOptions`, `NullText="All classes"` (blank is meaningful) |
| `SaCustPriceGroupList` | line Item → `IvStockMasterPicker` bound to `LineItem`; line UOM → `IvCodeComboBox` bound to `LineUom` |

Rules that apply to every one of them (§4.0 covers what applies everywhere; these are the Tier-1 specifics):

1. **Key parts stay read-only on edit.** Customer/Item/UOM are identity fields on `SaItemCust` (the service rejects a key change), so the picker gets `Enabled="@(!IsEditMode)"` and the combos get `Enabled="@(!IsEditMode)"` — preserving today's `ReadOnly="@IsEditMode"` behaviour.
2. **Clearing is governed by §4.0 rule 1.** Blank is legal for the nullable columns here (Selling UOM, Currency, Item class) and illegal for the identity columns on an existing row; the combo's clear button must produce the first case and never the second.
3. **UOM is not item-limited.** List every active `MsUom` row for the caller's company; the same item with different UOMs and prices is intended behaviour (item-family plan §8.5/§8.6).
4. **Prefill only what the picker actually knows**: `ICode`, `IDesc`, and `SellingUOM` from `StdUom` when the UOM is still blank and the row is new. Never touch the price — `VIEW_PRICE` owns that (§11.1), and the picker row has no selling price anyway.
5. **Options load when the popup opens** (call the loader in `OnNewClickAsync` / `OnEditClickAsync` before `PopupVisible = true`) rather than overriding the base page's `OnInitializedAsync` — it keeps the picker fresh and avoids touching `SaRefListPageBase`'s lifecycle.
6. **No width filtering, no retired-value appending, no server-derived descriptions** (§4.0 rule 5). The reason it is repeated here: this is the section with the item pickers, and a combo over a column that can hold a retired code is exactly where that hazard shows. See the §7 hazard note before shipping.

Option sources (all ungated):

| Field | Lookup |
|---|---|
| Item | `IvStockMasterPicker` + `IIvInventoryLookupService.SearchStockMastersAsync` |
| UOM | `IIvInventoryLookupService.ListActiveUomsAsync` → `IvInventoryLookupResult.Rows` |
| Item class | `IIvInventoryLookupService.ListActiveClassesAsync` |
| Currency | `ISaCustLookupService.ListCurrenciesForAssignmentAsync` |
| Customer | the new `SearchCustomersAsync` (§3.1) |
| Salesman | the new `ListSalesRepsForAssignmentAsync` (§3.2) |
| Pay code / state / country / area | `ListPayCodesForAssignmentAsync` / `ListStatesForAssignmentAsync` / `ListCountriesForAssignmentAsync` / `ListAreasForAssignmentAsync` |

No `@using` changes are needed: `ErpWeb.UI/Sales/_Imports.razor` already imports `ErpWeb.UI.Inventory.Lookups` and `ErpWeb.Core.Inventory`.

### 4.2 Tier 2 — 7 entries: 6 editable (4 reference + 2 vocabulary) plus 1 derived

| Page | Change |
|---|---|
| `SaDisGroupList` | Pay Code → `IvCodeComboBox` over pay codes; **Discount Type stays a free-text box (§4.6 — recorded deviation)**; **Status → `DxComboBox`** over the `NEW` / `FALSE` activation tokens, with the current value appended when a legacy row carries anything else |
| `SaSalesRepList` | State → `IvCodeComboBox` over `ListStatesForAssignmentAsync`; Country → `IvCodeComboBox` over `ListCountriesForAssignmentAsync` |
| `SaLMWList` | Customer Code → customer combo (`SearchCustomersAsync`); **Customer Name is derived and read-only**, filled from the picked customer — it is a stored denormalisation of `SaCust.CustName`, never an independent input (§4.5) |

The two vocabulary combos (`Status`, `Discount Type`) are **UI-only in this pass**: they stop an operator typing an unknown token, but no new server-side vocabulary check is added here (that is `plans/sales-master-lookup.md` row O4). Do not "helpfully" add one — §7 forbids widening the server-side change beyond §3.3. `Discount Type` stays free text entirely — see §4.6.

### 4.3 `SaCustEntry` + the `SaCustList` filter (3 reference fields)

- `SaCustEntry.razor:309` — Salesman → `IvCodeComboBox` over the new salesman lookup, plus `@FieldError(nameof(SaCustEditVm.SalesmanCode))` so the new server-side error surfaces on the field. This is the **last** unvalidated customer reference box on that page.
- `SaCustList` filter popup — Area → `IvCodeComboBox` over `ListAreasForAssignmentAsync`.
- `SaCustList` filter popup — Salesman: **resolved by inspection, no longer an open decision** (R2.6). The popup forwards the value as `salesmanCode` (`SaCustList.razor.cs:511-513`) and `SaCustRepository.cs:184-187` applies `x.SalesmanCode == salesman` — an **equality** predicate, so a free-text box could only ever match a full code and the correct control is the combo over `ListSalesRepsForAssignmentAsync`. If that predicate is ever turned into a `LIKE`, the free-text box must come back with it; record that next to the query, not here.
- The popup's own `SearchText` box (`SaCustList.razor.cs:491-493`) is a genuine `LIKE` grid search. **Do not touch it.**

### 4.4 Numeric coordinates — **deferred, not part of this pass** (R2.8)

`SaAreaList` (lines 79/87) and `SaCountryList` (lines 77/84) render Latitude/Longitude as `DxTextBox`, so any string is accepted for a numeric coordinate. That is numeric-input hardening, not a reference lookup — no lookup and no master is involved — so it is **cut from this pass** and tracked as a separate work item (§9). Do not implement it here, and do not let a later reader treat its absence as an oversight.

### 4.5 `SaLMWList.CustName` — derived, never independently editable (R2.2)

**Current contract (verify before editing — do not assume):** `SaLMW.CustName` is a **stored** column (`ErpWeb.Model/Entities/Sales/SaLMW.cs:23`, max 200), supplied by the client Vm, length-checked only (`SaSalesRefService.Lmw.cs:100` — `ValidateOptionalLength`), written straight through (`:240`) and read back into the list projections (`:380`, `:397`).

Rules:

1. **The persistence contract does not change.** `CustName` keeps being stored on the `SaLMW` row: no schema change, no new derivation at save time, no removal of the field from the edit Vm, and no new persistence mechanism.
2. **The UI stops offering it as a free-text box.** It becomes read-only and is filled from the customer chosen in the Customer Code combo (`SearchCustomersAsync` → `Desc`). Blank customer ⇒ blank name.
3. **Inspect first, then edit.** Read `SaLMWList.razor(.cs)`'s save path and the service's create/update branches to confirm nothing else populates `CustName`, and check what a legacy row that carries a name with **no** matching `SaCust` row does: such a value must still render (read-only) rather than blanking on open.
4. **Explicitly out of scope:** making the service derive `CustName` from `SaCust` at save time. That is `plans/sales-master-lookup.md` §3.7 (item O3) and it would be a second server-side behaviour change; §7 permits only the salesman validator.

### 4.6 Deviations — where the shipped code differs from this plan (recorded, not silent)

| # | Plan said | Shipped | Why |
|---|---|---|---|
| D1 | `SaDisGroupList.Discount Type` → `DxComboBox` over the `SaDisGroupItemList` token set (`PERCENTAGE` / `AMOUNT`) | **left as a free-text box** (with an in-razor comment) | The plan's assumption was wrong for the **header**. `SaDisGroup.DiscountType` is a free-text **category** token — `docs/sales-item-family-logic.md` §1.4 records it as "never set by the screen; used elsewhere as `'Retailer'`", and `GetItemSalesPrice` filters `sadisgroup.discounttype = 'Retailer'`. `PERCENTAGE`/`AMOUNT` is the **item-level** slot vocabulary (`SaDisGroupItem.DiscountType`, doc §1.5). A dropdown over either set would invent a vocabulary and rewrite the category on save. No authoritative header vocabulary exists ⇒ no data source ⇒ same reasoning as `SaTaxGroup.TaxGlCode` (§2). |
| D2 | Status → plain `DxComboBox` over `NEW` / `FALSE` | `DxComboBox` **with the current value appended** when a legacy row carries something else | `GroupStatus` is the activation flag consumers filter on (`GroupStatus='NEW'` only). A legacy row can hold `TRUE`/`FALSE`-era junk (doc §9 Q8). Without the append, the combo renders blank over a non-empty column and the next unrelated save would silently **deactivate** the group — the §7 hazard, with a real data consequence. Same mitigation the repo already uses for `CreditTerms` (`SaCustEntry.razor.cs:386-397`). |
| D3 | Prefill `ICode` / `IDesc` from the picker (rule 4) | `SaCustPriceGroupList`'s line editor now **stores** the picker's `IDesc` on the line | `IvCustPriceLineVm.IDesc` existed and the service already persists it (`SaSalesItemFamily.cs` `:393`/`:407`), but nothing ever filled it — every line this screen wrote had a null description. Filling it from the picker is exactly rule 4 and changes no contract. |

No other deviation: no schema, menu, permission, export, validation or authorization change was made beyond §3.3.

## 5. Test matrix
The `ErpWeb.Tests/SaItemFamilyServiceTests.cs` fixture already constructs `SaCustLookupService`, so the new lookups are tested there; the UI itself has no test harness in this repo (no bUnit), so component changes are covered by smoke only.

| Area | Test |
|---|---|
| Customer lookup | company-scoped (another company's customer is absent); `searchText` filters on code **and** name; unknown term → empty; bound respected |
| Customer lookup — cross-company (R3.2) | signed in as company A, a company-B customer must never be returned **even when the term matches its code exactly** |
| Salesman lookup | company-scoped; active-only; legacy `NULL` `IsActive` is treated as active |
| Salesman validation (D-6) | blank → accepted; unknown code → rejected; **code already on the row → accepted** (the migration-safety clause) |
| Salesman validation — cross-company (R3.1) | a **new** value naming a company-B salesman on a company-A customer → **rejected** on the `SalesmanCode` field key; the same value *already stored* on the row → still accepted (clause 3) |
| Clear an optional lookup (R3.3) | clearing Customer / Selling UOM / Currency / Item class / Salesman writes the model's blank/null form, saves successfully, and a re-read shows no stale code |
| `SaLMWList` derived name (R3.4) | picking a Customer Code fills `CustName`, `CustName` is not independently editable, and a save round-trip preserves the stored name for an unchanged customer / stores the new name for a changed one |
| Existing behaviour preserved | every server-side rejection from the item-family suite still fails with the same field key (`ItemCust_Save_UnknownUom_Rejected`, `ItemCust_Save_UnknownCustomer_Rejected`, `DisGroupItem_Save_*`, `ItemFamily_AccessDenied_IsReported`) — the pickers must not have moved validation into the UI |
| Preconditions | test company codes ≤ 5 chars (`InventoryTenantContext.MaxCompanyLength`); explicit `RowVersion` tokens when seeding on SQLite |

## 6. Verification

1. `dotnet build ErpWeb.slnx --nologo -v:q` → 0 errors.
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` green; with `ConnectionStrings:SqlServerTestConnection` (name contains "test") and `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` so nothing self-skips.
   - **Scratch-DB trap (cost one round trip):** on the *first* run against a database that does not exist yet, `SaLmwSqlServerConcurrencyTests.TC29_*` (3 tests) **fail** with "TC29 requires a scratch SQL Server" even though the connection string is correct — the class probes `CanConnect()` before any `EnsureCreatedAsync()`, and `CanConnect()` is false for a DB that has not been created. The run that follows (DB now exists) is green. Create the scratch DB first, or accept the 3 first-run failures as a harness quirk — **do not** read them as a regression from this plan.
3. Per-screen smoke: open each changed popup, confirm the picker lists data for the signed-in company, pick a value, save, reopen and see it.
4. Negative smoke: post a bad `ICode`/`UOM`/`CustCode`/`SalesmanCode` straight to the service (no picker) and confirm it is **still** rejected with a field error — the pickers are convenience, not the integrity boundary.
5. Regression: `SaCustEntry` still saves with a blank price group and a blank salesman; a customer carrying a retired/unknown salesman code remains editable (D-6 clause 3).
6. Cross-company smoke (R3.1/R3.2): signed in as company A, confirm no company-B customer or salesman appears in any converted picker, and that posting a company-B code straight to the service is rejected.

## 7. Risks and notes

- **Do not reach for the gated services**: `IvInventoryRefService` (INV_UOM / INV_CLASS ACCESS) for UOM/class, `ISaSalesRefService.ListSalesRepsAsync` (SALES_SALES_REP) for salesmen, `ISaCustService.SearchAsync` for customers. Each one turns a legitimate sales-master screen into an empty picker for a user who lacks that other menu — a permission leak that only shows up in negative smoke.
- **`IvStockMasterPicker` is read-only + Search** (a `DxTextBox` with `ReadOnly=true` plus a button). That is intentional: on a new row it *forces* a valid item instead of allowing a typo, and it cannot drift from the master.
- **Unbounded option lists**: customer and salesman lists are bounded / small code tables. The three large item-family tables (`IvCustPrice`, `SaItemCust`, `SaDisGroupItem`) deliberately have **no** assignment-style lookup — their screens keep validated text boxes for the keys until a paged picker is built (item-family plan §24).
- **No schema, menu, permission or export change** is involved. Only the salesman validation is a server-side behaviour change, and it is fail-open for existing values by design.
- **Change control (R2.7) — the rule that keeps this pass small.** Do not change existing lookup filtering, company scoping, active/inactive handling, sorting, null handling, save behaviour, validation rules or authorization behaviour unless a numbered section of *this* plan requires it. The only permitted server-side behaviour change is `ValidateSalesmanCodeAssignmentAsync` (§3.3); the only permitted persistence-neutral UI additions are the pickers and the two vocabulary lists (§4.1/§4.2). A final diff review must show nothing else moved; "while I was there" is out of scope by definition.
- **Relationship to the wider contract.** `plans/sales-master-lookup.md` (not yet approved) adds width filtering, retired-value appending and server-derived descriptions. This pass implements none of them deliberately — do not import them piecemeal from that file.
- **Hazard this leaves open — check it in smoke, do not "fix" it blind.** A stored value that is retired, inactive, or from another company has no row in the new option list, so the combo can render blank over a non-empty column. Saves should still be safe (§3.3 clause 3 tolerates the unchanged value), but *verify* it: open a customer with a retired `SalesmanCode`, save an unrelated field, and confirm the original code is still on the row. If the control drops the value on load-and-save instead of only on an actual clear, that becomes a blocker, not a note.

## 8. Implementation order (R4)
1. **Inspect, do not edit** — the `SaLMWList` save path (§4.5) and the `SaCustList` filter query (§4.3). Both are inspection-first steps; §4.3 is already resolved, §4.5 must be confirmed against the code before the control changes.
2. `SearchCustomersAsync` (§3.1) + tests, including the cross-company exclusion row.
3. `ListSalesRepsForAssignmentAsync` (§3.2) + tests.
4. `ValidateSalesmanCodeAssignmentAsync` (§3.3) + the cross-company rejection test.
5. Wire the validator into `SaCustService.AddLookupValidationErrorsAsync`.
6. Tier 1 screens (§4.1) — the two service lookups must exist first, so steps 2–4 cannot be deferred behind the UI.
7. Tier 2 screens (§4.2), including the derived read-only `CustName` (§4.5).
8. `SaCustEntry` + the `SaCustList` filters (§4.3).
9. Build → full suite (§6 steps 1–2) → per-screen smoke → negative and cross-company smoke (§6 steps 3–6).
10. Final diff review against the §7 change-control rule, then close the `change-control` todo.

## 9. Deferred / explicitly out of scope

| Item | Why | Where it lives |
|---|---|---|
| Latitude/longitude numeric editors on `SaAreaList` / `SaCountryList` | Numeric-input hardening, not a reference lookup (R2.8) | separate work item — not this pass |
| Combo width filtering, retired-value appending, server-derived `CustName` / `IDesc` | Belongs to the wider contract; would widen this pass into repository/control/service behaviour | `plans/sales-master-lookup.md` §3.1 / §3.5 / §3.7 |
| Server-side vocabulary check for `SaDisGroup.Status` / `DiscountType` | Not required here; would be a second server-side change | `plans/sales-master-lookup.md` row O4 |
| Seeding a price from the item picker | `IvStockMasterLookupRow` carries no selling price | §2 + item-family plan O1 |
| `SaTaxGroup.TaxGlCode` | No GL master exists anywhere in this repo | §2 |
| `SaDisGroupList.Discount Type` | Left as free text by recorded deviation D1 (§4.6) — no authoritative vocabulary | §4.6 |

## 10. Implementation status (2026-09-15)

**Code complete; build and unit tests green; manual smoke outstanding.**

| Step (§8) | Status | Evidence |
|---|---|---|
| 1 Inspect (`SaLMWList` save path, `SaCustList` filter query) | done | the filter is an equality predicate (`SaCustRepository.cs:184-187`); `SaLMW.CustName` is client-supplied and stored (`SaSalesRefService.Lmw.cs:100`/`:240`) |
| 2 `SearchCustomersAsync` | done | `SaCustLookupService.cs:230`; predicate → order → projection → `Take`, nothing materializes early |
| 3 `ListSalesRepsForAssignmentAsync` | done | `SaCustLookupService.cs:263`; `IsActive != false` (legacy `NULL` = active) |
| 4 `ValidateSalesmanCodeAssignmentAsync` | done | `SaCustLookupService.cs:282`, `allowLegacyEmptyBypass: true` |
| 5 Wire into `SaCustService` | done | `SaCustService.cs:753`, key `SalesmanCode`, beside the Group/SubGroup/DisGroup/CustPriceCode checks |
| 6 Tier 1 screens | done | `SaItemCustList` (Customer/Item/Selling UOM/Currency), `SaDisGroupItemList` (Item/Item class), `SaCustPriceGroupList` (line Item/UOM) |
| 7 Tier 2 screens | done | `SaDisGroupList` (Pay Code/Status), `SaSalesRepList` (State/Country), `SaLMWList` (CustCode + derived read-only CustName) |
| 8 `SaCustEntry` + `SaCustList` | done | Salesman combo + `@FieldError(nameof(SaCustEditVm.SalesmanCode))`; filter popup Salesman/Area combos |
| 9 Build + tests | done | solution build **0 errors**; `dotnet test` **1266 passed / 0 failed / 0 skipped** (scratch DB `ERPWeb_LookupFixesTest` + `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`) |
| 9 Smoke (per-screen, negative, cross-company) | **outstanding** | needs a running app + a signed-in company; §6 steps 3–6 |
| 10 Diff review against §7 change control | done | only the 7 screens listed above, `SaCustLookupService`, `ISaCustLookupService`, `SaCustService` and `SaCustServiceTests` changed; no schema, menu, permission or export file touched |

Deviations from the plan are recorded in **§4.6** (D1–D3) — all three are smaller than what the plan asked for, none widens the server-side change beyond §3.3.

**Still open for the owner:** the smoke pass (§6 steps 3–6) and the deferred numeric-coordinate item (§9).
