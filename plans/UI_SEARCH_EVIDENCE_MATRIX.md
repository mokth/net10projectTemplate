# UI Search Evidence Matrix

Generated: 2026-10-29 22:20:29 +08:00

Every converted search control has its own row. A representative screen never stands in for another control.

## Gate results

| Gate | Command | Result | Log |
| --- | --- | --- | --- |
| Static contract tests | `dotnet test --filter FullyQualifiedName~ErpInlineSearchContractTests` | PASS (6/6) | `plans/evidence/delivery-contract-test.log` |
| Delivery solution build | `dotnet build ErpWeb.slnx` | PASS (0 errors, 113 warnings) | `plans/evidence/delivery-build.log` |
| Delivery UI project build | `dotnet build ErpWeb.UI/ErpWeb.UI.csproj` | PASS (0 errors) | `plans/evidence/delivery-build-ui.log` |
| Delivery host project build | `dotnet build ErpWeb/ErpWeb.csproj` | PASS (0 errors) | `plans/evidence/delivery-build-host.log` |
| Delivery full suite | `dotnet test ErpWeb.Tests --logger trx` | 4592 total, 4557 passed, 27 failed, identical failure set to Baseline B | `plans/evidence/delivery-full-test.log` |
| Baseline A (dev base `8d09a22`) | `dotnet test ErpWeb.Tests --logger trx` | 4782 total, 4747 passed, 27 failed | `plans/evidence/baseline-A-test.log` |
| Baseline B (`origin/productionv2` `371725c`) | `dotnet test ErpWeb.Tests --logger trx` | 4586 total, 4551 passed, 27 failed | `plans/evidence/baseline-B-test.log` |
| Baseline B failure attribution | failing-test subset replay on pristine worktree | 27/27 delivery failures reproduce on Baseline B, 0 new failures | `plans/evidence/baseline-b-suspects.log` |
| Denylist audit | `git status --porcelain` filtered by `ErpWeb.Core`/`ErpWeb.Model`/`ErpWeb.Data`/`*.sql` | PASS (no hits) | `plans/evidence/change-audit.md` |

## Browser smoke matrix

Browser status for every row: **NOT VERIFIED**. No interactive browser session was available (Chrome remote debugging was not enabled and no credentials were supplied).

| # | Route | Control | Path:line | Handler pattern | Row on one line | Magnifier visible/clickable | Current text applied | Empty / clear | Console clean | Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | /admin/company | `#admin-company-rail-search` | `ErpWeb.UI/Admin/AdminCompany.razor:34` | Live list filter (no explicit action) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 2 | /inventory/balance-by-lot | `#iv-balance-lot-search` | `ErpWeb.UI/Inventory/Inquiry/IvBalanceLot.razor:58` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 3 | /inventory/lot-traceability | `#iv-lot-genealogy-search` | `ErpWeb.UI/Inventory/Inquiry/IvLotGenealogyInquiry.razor:35` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 4 | /inventory/trx-inquiry | `#iv-trx-inquiry-search` | `ErpWeb.UI/Inventory/Inquiry/IvTrxInquiry.razor:63` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 5 | (component) | `#iv-bal-loc-search-popup-search` | `ErpWeb.UI/Inventory/Lookups/IvBalLocSearchPopup.razor:17` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 6 | (component) | `#iv-stock-master-search-popup-search` | `ErpWeb.UI/Inventory/Lookups/IvStockMasterSearchPopup.razor:17` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 7 | /inventory/items | `#iv-stock-master-list-search` | `ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor:46` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 8 | /inventory/goods-receipts/{Mode:regex(^(new\|edit\|view)$)} | `#iv-goods-receipt-po-search` | `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor:275` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 9 | /inventory/goods-receipts | `#iv-goods-receipt-list-search` | `ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor:50` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 10 | /inventory/misc-issue | `#iv-misc-issue-list-search` | `ErpWeb.UI/Inventory/Transactions/IvMiscIssueList.razor:50` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 11 | /inventory/misc-receipt | `#iv-misc-receipt-list-search` | `ErpWeb.UI/Inventory/Transactions/IvMiscReceiptList.razor:50` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 12 | /inventory/scrap | `#iv-scrap-list-search` | `ErpWeb.UI/Inventory/Transactions/IvScrapList.razor:50` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 13 | /inventory/stock-adjustment | `#iv-stock-adjustment-list-search` | `ErpWeb.UI/Inventory/Transactions/IvStockAdjustmentList.razor:50` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 14 | /inventory/stock-count | `#iv-stock-count-list-search` | `ErpWeb.UI/Inventory/Transactions/IvStockCountList.razor:50` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 15 | /inventory/stock-return | `#iv-stock-return-list-search` | `ErpWeb.UI/Inventory/Transactions/IvStockReturnList.razor:50` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 16 | /inventory/stock-transfer | `#iv-stock-transfer-list-search` | `ErpWeb.UI/Inventory/Transactions/IvStockTransferList.razor:50` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 17 | /inventory/vendor-return | `#iv-vendor-return-list-search` | `ErpWeb.UI/Inventory/Transactions/IvVendorReturnList.razor:50` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 18 | /planning/production-balance | `#pr-production-balance-search` | `ErpWeb.UI/Planning/Inquiry/PrProductionBalance.razor:50` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 19 | (component) | `#pr-delivery-request-search-popup-search` | `ErpWeb.UI/Planning/Lookups/PrDeliveryRequestSearchPopup.razor:18` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 20 | (component) | `#pr-production-stock-work-order-search-popup-search` | `ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderSearchPopup.razor:18` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 21 | /planning/product-definitions | `#pr-product-def-list-search` | `ErpWeb.UI/Planning/Masters/PrProductDefList.razor:48` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 22 | /planning/daily-production | `#pr-daily-production-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionList.razor:48` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 23 | /planning/finished-good-receipts | `#pr-finished-good-receipt-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptList.razor:48` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 24 | /planning/work-orders/machine-schedule | `#pr-machine-schedule-search` | `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor:87` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 25 | /planning/material-issues | `#pr-material-issue-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueList.razor:48` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 26 | /planning/stock-adjustments/new | `#pr-stock-adjustment-source-search` | `ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor:152` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 27 | /planning/stock-adjustments | `#pr-production-stock-adjustment-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentList.razor:14` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 28 | /planning/stock-returns/new | `#pr-stock-return-source-search` | `ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor:538` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 29 | /planning/stock-returns | `#pr-production-stock-return-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor:21` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 30 | /planning/work-orders | `#pr-work-order-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor:46` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 31 | /planning/work-order-transfer/new | `#wot-source-search` | `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor:206` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 32 | /planning/work-order-transfer | `#pr-work-order-transfer-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferList.razor:24` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 34 | (component) | `#po-purchasing-item-search-popup-search` | `ErpWeb.UI/Purchase/Lookups/PoPurchasingItemSearchPopup.razor:17` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 35 | (component) | `#po-supplier-search-popup-search` | `ErpWeb.UI/Purchase/Lookups/PoSupplierSearchPopup.razor:17` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 36 | /purchase/suppliers | `#po-supp-list-search` | `ErpWeb.UI/Purchase/Masters/PoSuppList.razor:46` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 37 | /purchase/credit-notes | `#po-cdn-list-search` | `ErpWeb.UI/Purchase/Transactions/PoCdnList.razor:56` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 38 | /purchase/invoices/{Mode:regex(^(new\|edit\|view)$)} | `#po-invoice-po-picker-search` | `ErpWeb.UI/Purchase/Transactions/PoInvoice.razor:389` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 39 | /purchase/invoices | `#po-invoice-list-search` | `ErpWeb.UI/Purchase/Transactions/PoInvoiceList.razor:59` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 40 | /purchase/orders/{Mode:regex(^(new\|edit\|view\|copy\|revise)$)} | `#po-order-pr-picker-search` | `ErpWeb.UI/Purchase/Transactions/PoOrder.razor:807` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 41 | /purchase/orders | `#po-order-list-search` | `ErpWeb.UI/Purchase/Transactions/PoOrderList.razor:46` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 42 | /purchase/requisitions | `#po-pr-list-search` | `ErpWeb.UI/Purchase/Transactions/PoPrList.razor:46` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 43 | /purchase/self-billed-credit-notes | `#po-sb-cdn-list-search` | `ErpWeb.UI/Purchase/Transactions/PoSbCdnList.razor:63` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 44 | /purchase/self-billed-invoices | `#po-sb-invoice-list-search` | `ErpWeb.UI/Purchase/Transactions/PoSbInvoiceList.razor:56` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 49 | /sales/delivery/board | `#delivery-board-search` | `ErpWeb.UI/Sales/Delivery/SaDeliveryBoard.razor:67` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 52 | (component) | `#sa-customer-search-popup-search` | `ErpWeb.UI/Sales/Lookups/SaCustomerSearchPopup.razor:17` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 53 | /sales/customers | `#sa-cust-list-search` | `ErpWeb.UI/Sales/Masters/SaCustList.razor:46` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 55 | /sales/credit-notes | `#sa-cdn-list-search` | `ErpWeb.UI/Sales/Transactions/SaCdnList.razor:66` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 56 | /sales/delivery-requests/{Mode:regex(^(new)$)} | `#sa-delivery-request-source-picker-search` | `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor:486` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 57 | /sales/delivery-requests | `#sa-delivery-request-list-search` | `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor:92` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 58 | /sales/delivery-orders/{Mode:regex(^(new\|edit\|view)$)} | `#sa-do-source-lookup-search` | `ErpWeb.UI/Sales/Transactions/SaDo.razor:688` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 59 | /sales/delivery-orders | `#sa-do-list-search` | `ErpWeb.UI/Sales/Transactions/SaDoList.razor:50` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 60 | /sales/invoices/{Mode:regex(^(new\|edit\|view)$)} | `#sa-invoice-source-lookup-search` | `ErpWeb.UI/Sales/Transactions/SaInvoice.razor:821` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 61 | /sales/invoices/{Mode:regex(^(new\|edit\|view)$)} | `#sa-invoice-do-picker-search` | `ErpWeb.UI/Sales/Transactions/SaInvoice.razor:965` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 62 | /sales/invoices | `#sa-invoice-list-search` | `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor:60` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 63 | /sales/quotations | `#sa-qt-list-search` | `ErpWeb.UI/Sales/Transactions/SaQtList.razor:46` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |
| 64 | /sales/sales-orders | `#sa-so-list-search` | `ErpWeb.UI/Sales/Transactions/SaSoList.razor:46` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED |

## Deep behaviour UAT (one pass per handler pattern)

| Pattern | Representative control | Sequences | Result |
| --- | --- | --- | --- |
| Debounced list search | `#po-invoice-list-search` | type then click before debounce; old count/error after newer filter; type then change status; two rapid clicks; navigate away mid-request; dispose during pending timer | NOT VERIFIED |
| Lookup popup | `#po-supplier-search-popup-search` | Enter without blur; type then magnifier; clear then search; paging/selection/footer intact | NOT VERIFIED |
| Transaction picker | `#po-invoice-po-picker-search` | type then magnifier; multi-select preserved; source options intact | NOT VERIFIED |
| Multi-criteria single apply | `PrMaterialConsumeVarianceInquiry` | one combined action only; draft/applied timing unchanged | NOT VERIFIED |
| Key-driven search | `#iv-lot-genealogy-search` | @onkeydown Enter preserved; no duplicate request | NOT VERIFIED |
| Live list filter | `#admin-company-rail-search` | filter as you type, no inert button, rail list updates | NOT VERIFIED |

## Visual matrix

Widths 320 / 375 / 768 / 1280 / 1920 px, zoom 100 / 125 / 200 percent, light and dark themes,
for the three representative patterns plus the scheduler and the admin rail.

Result: **NOT VERIFIED** - No interactive browser session was available (Chrome remote debugging was not enabled and no credentials were supplied).

## Static evidence that does not need a browser

- Every converted row carries a `label for` that equals the editor `InputId` (contract test).
- Every converted editor binds with `BindValueMode.OnInput` so Enter and the magnifier see the current text (contract test).
- Every explicit `Text` + `TextChanged` pair keeps `TextExpression` (contract test).
- Every row renders exactly one icon-only magnifier with a scope-specific `aria-label` (contract test).
- No `!important` and no `overflow-x` escape hatch in the shared rules (contract test).
- No legacy single-text toolbar search shape remains outside the documented exceptions (contract test).

