# Plan: Sales-entry defect fixes (Customer profile + INV/SO/DO/QT/CN)

## Goal
Fix the reported sales-entry defects and the related confirmed lookup/UI defects across the
Customer Profile and the INV / SO / DO / QT / CN entry pages. **No schema change, no new files,
no new dependencies.**

Scope covers six distinct fixes: (1) customer tax-group source, (2) document tax-group
first-record fallback, (3) popup warehouse default, (4) salesman permission lookup,
(5) numeric popup clipping, (6) invoice TIN / "Reg. no." captions.

## Confirmed root causes (verified by reading code)
1. **Customer profile "Tax group" reads the wrong table.**
   `ErpWeb.Core/Sales/SaCustLookupService.cs` `ListTaxGroupsForAssignmentAsync` (L164-165) calls
   `ListMsCodesAsync(IvMsCodeTypes.Tax)` -> reads `IvMsCode(TAX)`, not `SaTaxGroup.TaxGrCode`.
   `ValidateTaxGroupAssignmentAsync` (L204) delegates to the same list, so the SAVE GATE is wrong too.
   Mirror defect: `ErpWeb.Core/Purchase/PoSupplierLookupService.cs` (L66-67).
   Consequence: `SaCust.TaxGrCode` is stored from the wrong master -> the INV/SO/DO/QT header
   `DxComboBox` (data = `SaTaxGroup`) renders BLANK over a non-empty column. That is the real
   reason "tax group does not come from the customer profile" on the invoice screen.
2. **Tax group + salesman defaults from customer are ALREADY implemented** on all 5 pages
   (`GetCustomerDefaultsAsync` -> `ApplyDefaults`). Only the fallback is missing.
3. **Missing "blank tax group -> first dropdown record" fallback** in all 5 pages.
4. **Popup warehouse never uses `IvStockMaster.DefWarehouse`**: every page pre-fills
   `Warehouses.FirstOrDefault()` on "new line", then guards the item-default with
   `if (string.IsNullOrWhiteSpace(...))`, which can never be true.
5. **NEW: SO/DO/QT/CN salesman picker is empty for users without the SA_SALES_REP menu** — they call
   the menu-gated `ISaSalesRefService.ListSalesRepsAsync` instead of the ungated
   `ISaCustLookupService.ListSalesRepsForAssignmentAsync` (documented anti-pattern in
   `plans/sales-master-lookup-fixes.md:275`). Invoice is NOT affected (its service reads
   `db.SaSalesReps` directly).
6. **Popup numeric fields clipped**: `DxFormLayout` sizes each editor from its own caption
   (documented in `SaCustPriceGroupList.razor.css` header comment). Inv/SO/QT put Qty/Pack/Price in
   `ColSpanMd="3"` (25%) cells inside a 760px popup; the injected full-width price-hint /
   override-reason items reflow the row and the numbers are clipped.

---

## Phase 1 - Shared root cause (do FIRST; unblocks Phase 2 & 4)
1. Rewrite `SaCustLookupService.ListTaxGroupsForAssignmentAsync` to query `db.SaTaxGroups`,
   company-scoped via `_tenant.TryCompanyScope()`, `OrderBy(x => x.TaxGrCode)`,
   project `Code = TaxGrCode`, `Desc = TaxGrDesc ?? TaxGrCode`. `SaTaxGroup` has no IsActive.
   (`SaTaxGroup` entity is already in scope - `using ErpWeb.Model.Entities.Sales;` is present.)
2. Same rewrite in `PoSupplierLookupService.ListTaxGroupsForAssignmentAsync`.
3. Do NOT touch `IvMsCodeTypes.Tax` usages in `SaSalesRefService` (L1915, L3527) or
   `SaSalesMasterServiceTests` - those are the LHDN `SaTaxGroup.TaxType` mapping, a different concept.
4. Existing-seed/test fixes (the lookup list becomes the save gate's source):
   - `ErpWeb.Tests/SaCustServiceTests.cs` seed (L86-92): add a `SaTaxGroup` row
     `CompanyCode="DEMO", TaxGrCode="SR", TaxGrDesc="Standard Rated", Percentage=6m`.
     Test at L499 (`model.TaxGrCode = "SR"`) must still succeed; L346 ("NOPE") must still fail.
     Keep or drop the `IvMsCode` TAX row - it is now unused but harmless.
   - `ErpWeb.Tests/PoSupplierServiceTests.cs` seed (L53-56): same addition.

4a. **Regression test - the defect's origin (MANDATORY, must fail before Phase 1 and pass after).**
    Prove the lookup source is `SaTaxGroup`, not `IvMsCode(TAX)`. Seed in the SAME company:
    - `SaTaxGroup` `SR` = "Standard Rated", `Percentage = 6m`;
    - a decoy `IvMsCode` record `XX` with `CodeType = IvMsCodeTypes.Tax`.
    Call `ListTaxGroupsForAssignmentAsync()` and assert:
    - it returns `SR` (Code `SR`, Desc "Standard Rated");
    - it does NOT return `XX` (an `IvMsCode(TAX)` row must be invisible);
    - the returned count equals the company's `SaTaxGroup` row count.
    Repeat the same three assertions for `PoSupplierLookupService` (its test file constructs the
    service at L442). Reuse the existing helpers - `SaCustServiceTests.CreateLookups()` (L1058) -
    so NO new test file is required.

4b. **Regression test - company isolation (MANDATORY).**
    ```
    Company DEMO : SaTaxGroup SR = "Standard Rated"
    Company OTHER: SaTaxGroup OS = "Other Sales"
    Lookup as DEMO
      => returns SR only, never OS
    ```
    Assert no cross-company leakage in BOTH the lookup and the save-path validation:
    `ValidateTaxGroupAssignmentAsync("OS", ...)` must be false for a DEMO caller, and the
    customer/supplier save must report the `TaxGrCode` field error.

## Phase 2 - Invoice header (Defect 2) - independent of Phase 3-6
5. `ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs` `ApplyDefaults` L693: when
   `string.IsNullOrWhiteSpace(d.TaxGrCode)` use `TaxGroups.FirstOrDefault()?.TaxGrCode`.
   (Lookup is already `OrderBy(TaxGrCode)`, so "first record" is deterministic.)
6. `ErpWeb.UI/Sales/Transactions/SaInvoice.razor` L278 caption `TIN or BRN` -> `TIN`;
   L284 caption `BRN` -> `Reg. no.`. Both are already `ReadOnly="true"` bound to `BuyerTin`
   (`SaCust.TinNo`) / `BuyerBrn` (`SaCust.CustBrn`) - no binding change.
7. **OPTIONAL / DO NOT BLOCK IMPLEMENTATION** - defensive display of a legacy/unknown code
   (house pattern from `SaCustEntry.razor.cs` L386-403 `CreditTerms`): append the current
   `TaxGrCode` / `SalesmanCode` to the combo's option list when it is not present, so a legacy code
   DISPLAYS instead of blanking (and being silently rewritten on save). Salesman combo is
   `IvCodeComboBox Data="@SalesReps"`; tax combo is `DxComboBox Data="@TaxGroups"`.
   **Do NOT ship this by default**: adopting an unknown current value into the option list can make
   invalid master data look legitimate. First confirm the `CreditTerms` pattern is suitable for
   these specific controls, then deliver it as a SEPARATE change with its own test. Phases 1-6 must
   be implementable, testable and verifiable WITHOUT this step.

## Phase 3 - Popup warehouse = item's DefWarehouse (all 5 pages, parallel)
8. In each `OnNewLineClick`: stop pre-filling the first warehouse (set null) AND in each
   `OnPopupItemChangedAsync`: assign the item default UNCONDITIONALLY
   (`item.DefWarehouse ?? Warehouses.FirstOrDefault()?.WarehouseCode`):
   - `SaInvoice.razor.cs` L849 (prefill) / L1147-1150 (guard)
   - `SaSo.razor.cs` L713 / L792-794  (property `Warehouse`)
   - `SaDo.razor.cs` L692 / L881-883  (property `FrWarehouse`)
   - `SaQt.razor.cs` L841 / L912-914  (property `Warehouse`)
   - `SaCdn.razor.cs` L656 / L715-717  (property `FrWarehouse`)
   `SaInvoiceItemLookupRow.DefWarehouse` is already projected from `IvStockMaster.DefWarehouse`
   (`SaInvoiceService.cs:173`) - same for the SO/DO/QT/CN item lookup rows.

## Phase 4 - Tax-group first-record fallback parity (SO/DO/QT/CN)
9. Same one-line fallback as step 5 in `ApplyDefaults`:
   - `SaSo.razor.cs` L568, `SaDo.razor.cs` L544, `SaQt.razor.cs` L641, `SaCdn.razor.cs` L577.
   All four tax-group lookups are `SaTaxGroup` ordered by `TaxGrCode` - already correct.

## Phase 5 - SO/DO/QT/CN salesman picker gate (NEW defect)
10. Replace the gated call `SalesRefService.ListSalesRepsAsync(_cts.Token)` with the ungated
    `Lookups.ListSalesRepsForAssignmentAsync(_cts.Token)` in:
    `SaSo.razor.cs:247`, `SaDo.razor.cs:236`, `SaQt.razor.cs:309`, `SaCdn.razor.cs:291`.
    (`ISaCustLookupService Lookups` is already injected on all four pages; the ungated method
    returns `IvCodeLookupRow` with `Code=SrepCode`, `Desc=SrepName`, active-only - no mapping needed.)
    Keep the `SalesRefService` injection - it is still used for `ResolveLinePricingAsync`.

## Phase 6 - Popup numeric-field clipping (Defect 3b)
11. Make each numeric row sum to 12 columns and force injected full-width items onto their own row
    (`BeginRow="true"`), so the overridden-price state cannot reflow the row:
    - `SaInvoice.razor`: Qty L608, Pack L613, Unit price L618 -> `ColSpanMd="4"`;
      "Override reason (required)" item L626 -> `BeginRow="true"`.
    - `SaSo.razor`: Unit price L595 -> `ColSpanMd="4"`, the `_priceHint` item after it -> `ColSpanMd="8"`
      (one clean row); "Override reason (required)" L607 -> `BeginRow="true"`.
    - `SaQt.razor`: same as SaSo (Unit price L675, price hint, override hint/reason L692-705).
    - `SaDo.razor`: Qty L534 + Pack L539 -> `ColSpanMd="6"` each (DO popup has NO price control).
12. **Acceptance criteria for the layout fix (measure in the browser at 100% zoom, popup open):**
    - Qty value fully visible - every digit, no clipping.
    - Pack value fully visible.
    - Unit price value fully visible, including a 4-decimal value (for example `1,234.5678`).
    - No horizontal clipping in any of the three fields, and the popup body shows NO horizontal
      scrollbar.
    - After the price is changed, the "Override reason (required)" item starts on a NEW row.
    Verify in BOTH states: price not overridden, and price overridden.

13. Fallback if DevExpress still clips: widen the popup (`Width="760px"`) and/or set
    `CaptionPosition="CaptionPosition.Top"` on those items; last resort is the house precedent - an
    explicit label-above-control CSS grid instead of `DxFormLayout`
    (see `ErpWeb.UI/Sales/Masters/SaCustPriceGroupList.razor.css`).

---

## Relevant files
- `ErpWeb.Core/Sales/SaCustLookupService.cs` - L164-165 (rewrite), L204 (delegates, auto-fixed)
- `ErpWeb.Core/Purchase/PoSupplierLookupService.cs` - L66-67 (mirror rewrite)
- `ErpWeb.Tests/SaCustServiceTests.cs` - L86-92 seed, L346, L499; steps 4a/4b reuse the existing
  `CreateLookups()` helper (L1058) - no new test file needed
- `ErpWeb.Tests/PoSupplierServiceTests.cs` - L53-56 seed; constructs `PoSupplierLookupService` (L442)
  for the mirror 4a assertions
- `ErpWeb.UI/Sales/Masters/SaCustEntry.razor` - L625 consumer (no change needed)
- `ErpWeb.UI/Sales/Transactions/SaInvoice.razor(.cs)` - captions, spans, prefill/guard, ApplyDefaults
- `ErpWeb.UI/Sales/Transactions/SaSo.razor(.cs)` - spans, prefill/guard, ApplyDefaults, sales reps
- `ErpWeb.UI/Sales/Transactions/SaDo.razor(.cs)` - spans, prefill/guard, ApplyDefaults, sales reps
- `ErpWeb.UI/Sales/Transactions/SaQt.razor(.cs)` - same
- `ErpWeb.UI/Sales/Transactions/SaCdn.razor(.cs)` - same (except captions/TIN - CN has no buyer id)
- Reference only: `ErpWeb.UI/Sales/Masters/SaCustPriceGroupList.razor.css`, `SaDocPage.razor.css`

## Out of scope
- No schema/DDL, no new tables/columns/menus.
- `IvMsCode(TAX)` / `SaTaxGroup.TaxType` LHDN mapping (correct as-is).
- Invoice `BuyerTin`/`BuyerBrn` server snapshot behaviour, Post/Rollback, numbering.
- Purchase-side UI (only the one-line supplier tax-group lookup source fix).

## Verification
1. `dotnet build ErpWeb.slnx --nologo -v:q` - mandatory after any `.razor` change (`dotnet test`
   alone does not compile pages). Known noise: MSB3021/MSB3027 on the HOST project if `ErpWeb` is
   running - 0 `error CS` is the pass condition.
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` - expect the 13 pre-existing SaCust/PoSupplier WIP
   failures to be unchanged, and no NEW failures. Watch `SaCustServiceTests` /
   `PoSupplierServiceTests` for tax-group seeds.
3. Manual smoke (browser):
   - Customer profile -> Payment information -> Tax group lists `SaTaxGroup.TaxGrCode` values; save
     an unknown code -> "Tax group 'X' is not valid."
   - INV / SO / DO / QT / **CN** new document: pick a customer -> header Tax group + Salesman
     populate; clear the tax group -> it falls back to the first dropdown record.
   - INV: captions read "TIN" and "Reg. no.", both read-only and prefilled from the customer.
   - Add item popup (INV / SO / QT): pick an item with a `DefWarehouse` -> warehouse follows the
     item; Qty / Pack / Unit price are no longer clipped after a price override - check the
     measurable criteria in Phase 6 step 12. DO popup: Qty / Pack only, fully visible.
   - Log in as a user WITHOUT `SA_SALES_REP`: SO / DO / QT / **CN** salesman combo still lists reps.

## Decisions / assumptions
- Fix the shared lookup source (Phase 1) rather than patching each page - one change fixes the
  customer profile AND all five document headers.
- Only the INVOICE gets the TIN / "Reg. no." caption change (SO/DO/QT have no buyer-identity fields).
- Warehouse: item `DefWarehouse` wins; the first active warehouse stays the fallback when the item
  has none (so a stock-controlled line can still be saved).
- The injected "Override reason"/"price hint" items are layout-only; no behaviour change.
- Phase 1 steps **4a (lookup source: `SaTaxGroup` not `IvMsCode(TAX)`) and 4b (company isolation)
  are MANDATORY** and are the primary regression evidence for this plan.
- Phase 2 step 7 (legacy/unknown code shown in the combo) is **OPTIONAL**: it must not block, and
  must not be bundled with, Phases 1-6.
- Layout work is accepted only against the measurable criteria in Phase 6 step 12 (browser, 100%
  zoom, both override states) - "looks better" is not a pass condition.
- Regression tests live in the two existing test files; no new test project/file is introduced.
