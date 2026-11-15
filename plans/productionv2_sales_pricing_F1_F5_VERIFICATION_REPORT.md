# productionv2 — Sales Pricing Post-Implementation Safe Fix Verification Report (Gates F0–F5)

- **Repository / Branch:** `mokth/net10projectTemplate` (`productionv2`)
- **Base Commit (`HEAD`):** `960cbeb72d625fb12cd5cfcb6b31fbeb3d7ef2bb`
- **Plan Reference:** `newplans/productionv2_sales_pricing_POST_IMPLEMENTATION_SAFE_FIX_AGENT_PLAN_v1.md`
- **Baseline Audit Reference:** `plans/productionv2_sales_pricing_F0_BASELINE.md`
- **Status:** **COMPLETE & VERIFIED (Gates F0, F1, F2, F3, F5 PASSED; Gate F4 Deferred per Plan §8)**

---

## 1. Gate Summary

| Gate | Scope | Status | Key Evidence |
| --- | --- | --- | --- |
| **F0** | Baseline & Historical Data Audit | **PASS** | `0` historical SO↔DO / DO↔Invoice / SO↔Invoice item or price mismatches in `ERPWeb` (`.\SQLEXPRESS`); baseline recorded in `plans/productionv2_sales_pricing_F0_BASELINE.md`. |
| **F1** | Source-Linked Item Identity Lock (`SaDo`, `SaInvoice`) | **PASS** | Server-side `ICode` equality enforced in `SaDoService.PrepareLinesAsync` and `SaInvoiceService.PrepareLinesAsync` (including upstream `doSoDetail.ICode` vs `doDetail.ICode` check); UI item pickers and handlers locked when `Popup.HasSource` is true. |
| **F2** | Source-Linked Commercial Terms & Price-Override Governance | **PASS** | Server-side validation of `ItemDiscount`..`ItemDiscount6`, `ItemDiscAmount`, `ItemDiscAmount1`, `IsInclusive`, `TaxGrCode`, and `OrderType` against source/existing draft; server-authoritative `OriginalUnitPrice` from `salesOrderDetail.UnitPrice` / `linkedDoDetail.UnitPrice` prevents `OriginalUnitPrice = null` or forged-baseline bypass while allowing unchanged inherited overrides without `PRICE_OVERRIDE`; `OrderType` and `SourceDisplay` wired across `SaDoLineVm` and `SaInvoiceLineVm`. |
| **F3** | Price Inquiry UOM Reset & Cross-Cutting Verification | **PASS** | `PriceInquiry.OnItemSelected` always updates `Uom` to `SaLinePricingTrigger.CommercialUom(item.SellingUom, item.StdUom)`; `VIEW_PRICE` (`C21`), currency fail-closed (`C06`), and historical FX freeze verified. |
| **F4** | Deferred Business Decisions (`B01`–`B06`) | **DEFERRED** | No speculative schema or business-policy changes made (`B01`–`B06` remain deferred per Plan §8). |
| **F5** | Full Verification & Costing / StockLedger Boundary Audit | **PASS** | Solution builds with `0 Error(s)`; `38 / 38` targeted F1–F3 tests pass; `750 / 750` Sales pricing & transaction tests pass; `100%` of `Category=StockLedger` tests pass; zero protected costing/inventory files modified. |

---

## 2. Files Modified in This Implementation

### Backend Service Layer (`ErpWeb.Core`)
1. `ErpWeb.Core/Sales/SaDoService.cs` (`PrepareLinesAsync`):
   - **F1:** Validates `string.Equals((salesOrderDetail.ICode ?? string.Empty).Trim(), iCode, StringComparison.OrdinalIgnoreCase)` on SO-linked DO lines; rejects mismatch on `Lines[i].ICode` with `"This item differs from the selected SO/DO line. Remove and reselect the source line."` before any stock/reservation side effect.
   - **F2:** Matches existing draft lines from `existingDetails` when updating a `NEW` DO; validates linked commercial terms (`ItemDiscount`..`ItemDiscount6`, `ItemDiscAmount`, `ItemDiscAmount1`, `IsInclusive`, normalized `TaxGrCode`↔`TaxGroup`, normalized `OrderType`) against `existingLinkedDetail ?? salesOrderDetail`; enforces server-side price override governance against `salesOrderDetail.UnitPrice` so `OriginalUnitPrice = null` or forged values cannot bypass `SaPriceOverridePolicy`, while unchanged inherited SO overrides require no `PRICE_OVERRIDE` permission and store `OriginalUnitPrice = null`.
2. `ErpWeb.Core/Sales/SaInvoiceService.cs` (`PrepareLinesAsync`):
   - **F1:** Validates `doDetail.ICode` vs `iCode` on `LinkDo` lines, checks upstream `doSoDetail.ICode` vs `doDetail.ICode` when `HasSoReference(soNo, soLine)` is true, and validates `soDetail.ICode` vs `iCode` on direct SO-linked lines; rejects mismatch on `Lines[i].ICode` before any allocation or stock side effect.
   - **F2:** Matches existing draft lines from `existingDetails` when updating a `NEW` Invoice; validates linked commercial terms (`ItemDiscount`..`ItemDiscount6`, `ItemDiscAmount`, `ItemDiscAmount1`, `IsInclusive`, normalized `TaxGrCode`↔`TaxGroup`, normalized `OrderType`) against `existingLinkedDetail` or `linkedDoDetail`/`linkedSoDetail`; enforces server-side price override governance against `sourceUnitPrice` (`linkedDoDetail?.UnitPrice ?? linkedSoDetail!.UnitPrice`) so `OriginalUnitPrice = null` or forged values cannot bypass `PermissionCodes.PriceOverride` and `OverrideReason`, while unchanged source prices require no `PRICE_OVERRIDE` and authorized overrides on `NEW` drafts round-trip cleanly on subsequent edits.

### Blazor UI Layer (`ErpWeb.UI`)
3. `ErpWeb.UI/Sales/Transactions/SaDo.razor` & `ErpWeb.UI/Sales/Transactions/SaDo.razor.cs`:
   - Added `CanEditPopupItem => CanEditDocument && !Popup.HasSource`, locked `<IvStockMasterPicker Enabled="@CanEditPopupItem" />`, guarded `OnPopupItemClearedAsync` / `OnPopupItemSelectedAsync` when `Popup.HasSource` is true, added popup source hint (`@Popup.SourceDisplay`), and wired `OrderType` across `SaDoLineVm` (`Clone`, `ToRequest`, `ToCalcState`, `FromDto`, `FromSalesOrder`).
4. `ErpWeb.UI/Sales/Transactions/SaInvoice.razor` & `ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs`:
   - Added `CanEditPopupItem => CanEditDocument && !Popup.HasSource` and `CanEditPopupCommercialTerms => CanEditDocument && !Popup.HasSource`, locked `<IvStockMasterPicker Enabled="@CanEditPopupItem" />` and commercial discount/tax editors when `Popup.HasSource` is true, guarded handlers (`OnPopupItemClearedAsync`, `OnPopupItemSelectedAsync`, `OnPopupBasisChangedAsync`, `OnPopupTaxChangedAsync`, `OnPopupDiscountModeChanged`), added popup source hint (`@Popup.SourceDisplay`), and wired `OrderType` in `SaInvoiceLineVm.ToCalcState()` and `SaInvoiceLineVm.FromSalesOrder()`.
5. `ErpWeb.UI/Sales/PriceInquiry.razor.cs`:
   - **F3:** Updated `OnItemSelected` so switching items always sets `Uom` to `SaLinePricingTrigger.CommercialUom(item.SellingUom, item.StdUom)` when non-blank.

### Automated Tests (`ErpWeb.Tests`)
6. `ErpWeb.Tests/Sales/Transaction/SaDoServiceTests.cs`:
   - `F1_SoLinkedDo_RejectsMismatchedItem_BeforeAnyStockSideEffect`
   - `F2_SoLinkedDo_EnforcesCommercialTerms_AndAllowsInheritedOverrideWithoutPermission`
7. `ErpWeb.Tests/Sales/Transaction/SaInvoiceServiceTests.cs`:
   - `F1_DoLinkedInvoice_RejectsMismatchedItem_BeforeAnySideEffect`
   - `F1_DoLinkedInvoice_RejectsUpstreamSoDoItemMismatch`
   - `F1_SoLinkedInvoice_RejectsMismatchedItem_BeforeAnyStockSideEffect`
   - `F2_LinkedInvoice_EnforcesCommercialTerms_AndServerAuthoritativePriceOverride`
8. `ErpWeb.Tests/Sales/Transaction/SaInvoiceFromDeliveryOrderTests.cs`:
   - `F2_FromSalesOrder_round_trips_order_type_and_source_display_on_do_and_invoice`
9. `ErpWeb.Tests/Sales/Transaction/SaLinePricingTriggerTests.cs`:
   - `F3_PriceInquiry_SwitchingItems_UpdatesUomToSelectedItemCommercialUom`

---

## 3. Verification Results (Gate F5)

- **Solution Build:** `dotnet build ErpWeb.slnx --no-restore -m:1 -v minimal` → **0 Error(s)**.
- **Targeted Gate F1–F3 Tests:** `38 passed, 0 failed, 0 skipped`.
- **Full Sales Pricing & Transaction Regression Suite (17 test classes):** `750 passed, 0 failed, 0 skipped`.
- **StockLedger & EInvoice Regression Suite:** `333 passed` (all `Category=StockLedger` tests passed 100%; only the 2 pre-existing `SaEInvoiceSelfBillAndTinTests` UBL root namespace assertions failed, unchanged from Gate F0).
- **Protected Denylist Check:** Zero modifications to `ErpWeb.Core/StockLedger/**`, `ErpWeb.Core/Inventory/**`, `IvItemPackPolicy`, `SaInvoiceCalc`, `SaDocApplicationService`, `SaCdnService`, or `PostOneAsync`/`RollbackOneAsync`.
