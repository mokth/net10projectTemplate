# Gate F0 — CURRENT Baseline & Mandatory Proof (`productionv2`)

**Date:** 2026-11-14  
**Branch:** `mokth/net10projectTemplate` → `productionv2`  
**Current HEAD:** `960cbeb72d625fb12cd5cfcb6b31fbeb3d7ef2bb` (`draft subcon check in`)  
**Reviewed Reference Commit:** `1b2d70618ce44d5db0f13ea14d78697dbc27ea5f` (parent of `960cbeb`)  
**Sales Pricing G1–G5 Commit:** `77d5633` (`sales price`)  
**Gate F0 Verdict:** **PASS** (SQL Server verified on `.\SQLEXPRESS`; 0 code files edited during F0)

---

## 1. Repository & Working-Tree Audit

- **Intervening Commit (`1b2d706..960cbeb`):** Touches only `ErpWeb.Core/Subcon/`, `ErpWeb.Model/Entities/Subcon/`, and `ErpWeb.UI/Subcon/`. Zero overlap with Sales, Inventory, Costing, or StockLedger.
- **Uncommitted Working-Tree Changes (preserved untouched):**
  - Production Machine Scheduling (`ErpWeb.Core/Production/MachineSchedule*`, `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner*`, `ErpWeb.Tests/Planning/*`)
  - Purchase Sourcing (`ErpWeb.Core/Purchase/PoSourcing*`, `PoOrderService.cs`, `PoPrService.cs`, `ErpWeb.Model/Entities/Purchase/PoSourcing.cs`, `ErpWeb.UI/Purchase/Transactions/PoSourcing*`, `ErpWeb.Tests/Procurement/Sourcing/*`)
  - Menu & DI registrations (`ErpWeb.Core/CoreServiceCollectionExtensions.cs`, `MenuCodes.cs`, `NavigationService.cs`, `AppSettingCatalogue.cs`, `ErpWeb.Model/Data/AppDbContext.cs`, `ErpWeb/Menus/menus.xml`, `MenuAuthorize.razor.cs`)

---

## 2. File Allowlist & Protected Denylist

### Allowed Target Files (Gates F1–F3)
- `ErpWeb.Core/Sales/SaDoService.cs` (commercial/source-item validation in `PrepareLinesAsync` only)
- `ErpWeb.Core/Sales/SaInvoiceService.cs` (commercial/source-item validation in `PrepareLinesAsync` only)
- `ErpWeb.UI/Sales/Transactions/SaDo.razor` & `ErpWeb.UI/Sales/Transactions/SaDo.razor.cs` (lock item picker on source-linked line, show source hint, preserve `OrderType` in `SaDoLineVm`)
- `ErpWeb.UI/Sales/Transactions/SaInvoice.razor` & `ErpWeb.UI/Sales/Transactions/SaInvoice.razor.cs` (lock item picker on source-linked line, show source hint, preserve `OrderType` in `SaInvoiceLineVm.FromSalesOrder` / `ToCalcState`, compare linked `UnitPrice` against inherited source price in popup)
- `ErpWeb.UI/Sales/PriceInquiry.razor.cs` (default `Uom` to new item's commercial UOM on item switch)
- Sales test files under `ErpWeb.Tests/Sales/` (`SaDoServiceTests.cs`, `SaInvoiceServiceTests.cs`, `SaInvoiceFromDeliveryOrderTests.cs`, `SaLinePricingConsumerTests.cs`, `SaInvoiceSqlServerConcurrencyTests.cs`)

### Protected Denylist (MUST have zero diff)
- `ErpWeb.Core/Inventory/**`, `ErpWeb.Core/Costing/**`, `ErpWeb.Core/StockLedger/**`, `ErpWeb.Core/Production/**`, `ErpWeb.Core/Quality/**`
- `IvQty`, `IvItemPackPolicy`, `SaProductionDemandRules`, `SaInvoiceCalc`
- `SaDoService.PostOneAsync`, `SaDoService.RollbackOneAsync`, `ValidateDoSoReserveAsync`
- `SaInvoiceService.PostOneAsync`, `SaInvoiceService.RollbackOneAsync`, `ValidateInvoiceSoReserveAsync`
- `SaCdnService.PostOneAsync`, `SaCdnService.RollbackOneAsync`, `SalesReturnCostAllocation`
- Database schema / EF Core migrations / `AppDbContext.cs`

---

## 3. Code-Level Proof of Verified Defects at Current HEAD

1. **Defect F1 — Missing Source Item Identity Validation (`SaDoService.cs` & `SaInvoiceService.cs`) and Unlocked Item Picker (`SaDo.razor` & `SaInvoice.razor`):**
   - `SaDoService.PrepareLinesAsync` (`ErpWeb.Core/Sales/SaDoService.cs:2513-2522`) resolves `salesOrderDetail` by `(Line, CustRel)` and copies `SellingUom`/`StdPsize`/`StdQty` (`L2545-2566`), but never compares `salesOrderDetail.ICode` against submitted `iCode` (except when `DeliveryRequestSourceId` is set at `L2631`).
   - `SaInvoiceService.PrepareLinesAsync` (`ErpWeb.Core/Sales/SaInvoiceService.cs:2145-2155`, `2218-2260`, `2290-2301`) loads `linkedDoDetail` and `linkedSoDetail` and uses their frozen pack (`L2330-2361`), but never compares `linkedDoDetail.ICode` or `linkedSoDetail.ICode` against submitted `iCode`.
   - `SaDo.razor:532` and `SaInvoice.razor:621` bind `<IvStockMasterPicker Enabled="@CanEditDocument" />` instead of disabling item replacement when `Popup.HasSource` is true.
2. **Defect F2 — Linked Commercial Identity & Price-Override Governance Bypass (`SaDoService.cs`, `SaInvoiceService.cs`, `SaDo.razor.cs`, `SaInvoice.razor.cs`):**
   - `SaPriceOverridePolicy.Validate` (`ErpWeb.Core/Sales/SaPriceOverridePolicy.cs:39-40`) only checks `originalUnitPrice is { } original && original != unitPrice`. Because `FromSalesOrder` and `FromDeliveryOrder` set `OriginalUnitPrice = null` on child lines (so inherited upstream overrides do not re-prompt shipping/billing clerks for `PRICE_OVERRIDE`), a request that modifies `UnitPrice` on a linked line while sending `OriginalUnitPrice = null` (or a forged `OriginalUnitPrice == UnitPrice`) bypasses `PermissionCodes.PriceOverride` and `OverrideReason` validation completely.
   - Neither `SaDoService.PrepareLinesAsync` nor `SaInvoiceService.PrepareLinesAsync` compares linked line discount slots (`ItemDiscount`..`ItemDiscount6`, `ItemDiscAmount`, `ItemDiscAmount1`), `IsInclusive`, `TaxGrCode`↔`TaxGroup`, or `OrderType` against the persisted source line (or existing draft line when editing a `NEW` document).
   - `SaDoLineVm` (`SaDo.razor.cs:1950-2135`) omits `OrderType` from the view-model and mapping methods (`Clone`, `ToRequest`, `ToCalcState`, `FromDto`, `FromSalesOrder`), and `SaInvoiceLineVm` (`SaInvoice.razor.cs:2658, 2706-2752`) omits `OrderType` from `ToCalcState()` and `FromSalesOrder()`.
3. **Defect F3 — `PriceInquiry.razor.cs` Stale UOM on Item Switch:**
   - `PriceInquiry.razor.cs:72-75` checks `if (string.IsNullOrWhiteSpace(Uom))` before assigning `SaLinePricingTrigger.CommercialUom(item.SellingUom, item.StdUom)`, leaving the previous item's UOM sticky when switching from a `BOX` item to a `PCS` item.

---

## 4. Source-to-Document Field Matrix

| Field | `SaQtDetail` → `SaSoDetail` | `SaSoDetail` → `SaDoDetail` | `SaSoDetail` → `SaInvoiceDetail` | `SaDoDetail` → `SaInvoiceDetail` | `SaInvoiceDetail` → `SaCdnDetail` |
|---|---|---|---|---|---|
| `ICode` | Copied | Must match source (F1) | Must match source (F1) | Must match source (F1) | Copied from Invoice |
| `Qty` / `StdQty` / `StdPsize` | Snapshot frozen on SO | Frozen factor from `SaSoDetail` | Frozen factor from `SaSoDetail` | Frozen factor from `SaDoDetail` | Validated against remaining creditable qty |
| `SellingUom` / `StdUom` | Copied | Frozen from `SaSoDetail` | Frozen from `SaSoDetail` | Frozen from `SaDoDetail` | Copied from Invoice |
| `UnitPrice` | Copied (`QUOTATION`) | Must match source (or authorized override) (F2) | Must match source or require server-checked `PRICE_OVERRIDE` + reason (F2) | Must match source or require server-checked `PRICE_OVERRIDE` + reason (F2) | Copied (`INVOICE`) |
| `ItemDiscount`..`6` | Copied | Must match source (F2) | Must match source (F2) | Must match source (F2) | Copied |
| `ItemDiscAmount`, `ItemDiscAmount1` | Copied | Must match source (F2) | Must match source (F2) | Must match source (F2) | Slot 1 copied; Slot 2 warned (`HasSecondAmountDiscountWarning`) |
| `IsInclusive` | Copied | Must match source (F2) | Must match source (F2) | Must match source (F2) | Copied |
| `TaxGroup` / `TaxGrCode` | Copied | Must match source (F2) | Must match source (F2) | Must match source (F2) | Copied |
| `OrderType` | Copied | Must match source (F2) | Must match source (F2) | Must match source (F2) | N/A |
| `OriginalUnitPrice` / `OverrideReason` | Local to SO if overridden | `null` when unchanged from SO | `null` when unchanged from SO; server-set to `soDetail.UnitPrice` if changed | `null` when unchanged from DO; server-set to `doDetail.UnitPrice` if changed | Local to CN if overridden against Invoice |

---

## 5. Read-Only SQL Server Tenant & Historical Data Audit (`.\SQLEXPRESS` / `ERPWeb`)

Executed read-only queries against `.\SQLEXPRESS` (`ERPWeb`):

| Check | Result |
|---|---|
| `DoSoItemMismatch` (`SaDODetail.ICode <> SaSODetail.ICode`) | `0` |
| `DoSoPriceMismatch` (`SaDODetail.UnitPrice <> SaSODetail.UnitPrice`) | `0` |
| `InvDoItemMismatch` (`SaInvoiceDetail.ICode <> SaDODetail.ICode` where `LinkDo=1`) | `0` |
| `InvDoPriceMismatch` (`SaInvoiceDetail.UnitPrice <> SaDODetail.UnitPrice` where `LinkDo=1`) | `0` |
| `InvSoItemMismatch` (`SaInvoiceDetail.ICode <> SaSODetail.ICode` where `LinkDo=0 AND SONo<>''`) | `0` |
| `InvSoPriceMismatch` (`SaInvoiceDetail.UnitPrice <> SaSODetail.UnitPrice` where `LinkDo=0 AND SONo<>''`) | `0` |
| `Company` currency & price method | `DEMO` (`CurrencyCode=MYR`, `SalesPriceMethod=''`), `AB` (`CurrencyCode=MYR`, `SalesPriceMethod=''`) |
| `IvCustPrice` currency distribution | `DEMO`: `1` row (`CurrencyCode=''`) |
| `SaItemCust` currency distribution | `DEMO`: `1` row (`Currency='MYR'`) |

---

## 6. Baseline Build & Test Suite Results

- **Solution Build:** `dotnet build ErpWeb.slnx --no-restore -m:1 -v minimal` → **0 Error(s), 339 Warning(s)**
- **Sales Suite (`Category=Sales&Category!=SqlServer`):** **1,541 passed, 11 failed, 0 skipped (1,552 total)**
  - All Sales pricing, SO, DO, Invoice, CDN, and DR tests (`SaInvoiceServiceTests`, `SaDoServiceTests`, `SaCdnServiceTests`, `SaSoServiceTests`, `SaInvoiceFromDeliveryOrderTests`, `SaLinePricingConsumerTests`, `SaItemFamilyPricingContractTests`, `SaLinePricingTriggerTests`, `SaPriceInfoPresentationTests`, `SaCompanyPriceMethodTests`, `SaItemFamilyResolutionServiceTests`) passed **100%**.
  - The 11 failures are pre-existing unrelated tests in `SaCustServiceTests` (9) and `SaEInvoiceSelfBillAndTinTests` (2).
