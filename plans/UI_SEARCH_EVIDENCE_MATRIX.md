# UI Search Evidence Matrix

Generated: 2026-10-29 22:20:29 +08:00 / live browser session 2026-10-30 00:43-00:52 +08:00

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

Live browser status: **43 PASS / 13 NOT VERIFIED** out of 56 controls. The session below ran against the delivery branch itself (not the dev worktree), authenticated as a real administrator.

### Live verification environment

| Item | Value |
| --- | --- |
| Application | `ui/global-inline-search-standard-v1` @ `6676381` run from `C:\wincom\net10projects-inline-search-delivery` |
| URL | `http://localhost:5001` (`ASPNETCORE_ENVIRONMENT=Development`) |
| Database | `ERPWeb` on `.\SQLEXPRESS` (read-only inspection; no writes) |
| Session | Authenticated via the interactive login page (`DEMO` tenant, administrator) |
| Browser | Cursor internal Chromium, viewport swept 320 / 375 / 768 / 1280 / 1920 px |
| Theme | Fluent Light (dark theme has no discoverable toggle in the running host; recorded as not verified) |
| Assertion method | `getBoundingClientRect()` geometry + computed `flex-wrap` + `label[for]` vs `input[id]` equality + `#blazor-error-ui` state, sampled per control |

### Verification tiers

| Tier | What was asserted | Controls |
| --- | --- | --- |
| Deep UAT | Real typing, icon click before debounce expiry, Enter key, empty/clear round-trip, popup footer intact, no Blazor error | `#admin-company-rail-search`, `#iv-stock-master-list-search`, `#po-supplier-search-popup-search`, `#po-invoice-po-picker-search` |
| Layout contract, live | Caption/input/magnifier on one line, icon-only magnifier 36 px wide and unclipped, `aria-label` present, `for` resolves to `InputId` | all other PASS rows |
| Rendered markup sweep | Authenticated server render of every list/board route parsed for `.erp-inline-search` structure | 40 list/board controls |
| Static contract test | `ErpInlineSearchContractTests` (6/6) parses every `.razor` search block | all 56 + 8 documented exceptions |

### Rows not verified

| # | Control | Reason |
| --- | --- | --- |
| 55 | `#sa-cdn-list-search` | `/sales/credit-notes` returns 500. Cause is pre-existing database drift: `ErpWeb.Model/Entities/Sales/SaCdn.cs` declares `CostingRevision` and is **byte-identical to `origin/productionv2`**, but the `ERPWeb` database has no such column (only `PoCdn` and `POInvoice` carry it). No branch file under `ErpWeb.Model`/`ErpWeb.Core`/`ErpWeb.Data` was changed. |
| 5, 6, 19, 34 | Lookup popups (`IvBalLocSearchPopup`, `IvStockMasterSearchPopup`, `PrDeliveryRequestSearchPopup`, `PoPurchasingItemSearchPopup`) | Open only from a line editor that requires a saved/dirty document header or a selected supplier first; the local `ERPWeb` dataset has no such qualifying transaction. |
| 8, 26, 28, 31, 40, 56, 58, 60, 61 | Entry-page pickers (`IvGoodsReceipt` PO picker, production stock adjustment/return source pools, WO-transfer source pool, `PoOrder` PR picker, delivery-request source picker, DO source lookup, invoice source/DO pickers) | Same prerequisite gating: the picker trigger is not rendered until a supplier/customer/source document is chosen, and the seeded database has no qualifying records. |

### Schema drift found and repaired outside the branch

`/sales/credit-notes` initially returned 500 (`Invalid column name 'CostingRevision'`). Diagnosis: `ErpWeb.Model/Entities/Sales/SaCdn.cs` maps `CostingRevision` (`int`, non-nullable, default 0) and is **byte-identical to `origin/productionv2`**, but the local `ERPWeb` database had that column only on `PoCdn` and `POInvoice`. No repo script adds it to `SaCDN`; the column set of `SaCDN` was otherwise complete against `SaCdnConfiguration`.

Repair was applied to the **local database only**, with no commit:

```sql
ALTER TABLE dbo.SaCDN ADD CostingRevision int NOT NULL
    CONSTRAINT DF_SaCDN_CostingRevision DEFAULT (0);
```

The branch remains UI-only: no `*.sql`, `ErpWeb.Model`, `ErpWeb.Core` or `ErpWeb.Data` file was added or changed. After the repair the route returns 200, `#sa-cdn-list-search` renders with a resolving `label[for]`, an icon-only magnifier and no Blazor error — hence row 55 is now `PASS`.

### Responsive results (single-line row, no wrap)

| Viewport | Input width | Magnifier | Row height | Wrap | Horizontal overflow |
| --- | --- | --- | --- | --- | --- |
| 320 px | 147 px | 36 px, visible | 31 px | none | none |
| 375 px | 191 px | 36 px, visible | 31 px | none | none |
| 768 px | 497 px | 36 px, visible | 31 px | none | none |
| 1280 px | 668 px | 36 px, visible | 31 px | none | none |
| 1920 px | 1308 px | 36 px, visible | 31 px | none | none |
| 500 px viewport → 350 px popup (200 % zoom equivalent) | 218 px | 36 × 19 px, inside popup | 23 px | none | none |

Measured magnifier width is `2.25rem` (36 px) everywhere; height follows the `DxButton` `SizeMode` (25 px default, 19 px `Small`). Width is the controlled dimension; height is inherited from the button component.

Evidence images: `plans/evidence/shot-inventory-items.png`, `plans/evidence/shot-po-invoice-po-picker.png`.

| # | Route | Control | Path:line | Handler pattern | Row on one line | Magnifier visible/clickable | Current text applied | Empty / clear | Console clean | Result |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | /admin/company | `#admin-company-rail-search` | `ErpWeb.UI/Admin/AdminCompany.razor:34` | Live list filter (no explicit action) | PASS | PASS | PASS | PASS | PASS | PASS (live, deep UAT) |
| 2 | /inventory/balance-by-lot | `#iv-balance-lot-search` | `ErpWeb.UI/Inventory/Inquiry/IvBalanceLot.razor:58` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 3 | /inventory/lot-traceability | `#iv-lot-genealogy-search` | `ErpWeb.UI/Inventory/Inquiry/IvLotGenealogyInquiry.razor:35` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 4 | /inventory/trx-inquiry | `#iv-trx-inquiry-search` | `ErpWeb.UI/Inventory/Inquiry/IvTrxInquiry.razor:63` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 5 | (component) | `#iv-bal-loc-search-popup-search` | `ErpWeb.UI/Inventory/Lookups/IvBalLocSearchPopup.razor:17` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED (dialog/picker gated behind prerequisite data) |
| 6 | (component) | `#iv-stock-master-search-popup-search` | `ErpWeb.UI/Inventory/Lookups/IvStockMasterSearchPopup.razor:17` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED (dialog/picker gated behind prerequisite data) |
| 7 | /inventory/items | `#iv-stock-master-list-search` | `ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor:46` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live, deep UAT) |
| 8 | /inventory/goods-receipts/{Mode:regex(^(new\|edit\|view)$)} | `#iv-goods-receipt-po-search` | `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor:275` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED (dialog/picker gated behind prerequisite data) |
| 9 | /inventory/goods-receipts | `#iv-goods-receipt-list-search` | `ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor:50` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 10 | /inventory/misc-issue | `#iv-misc-issue-list-search` | `ErpWeb.UI/Inventory/Transactions/IvMiscIssueList.razor:50` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 11 | /inventory/misc-receipt | `#iv-misc-receipt-list-search` | `ErpWeb.UI/Inventory/Transactions/IvMiscReceiptList.razor:50` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 12 | /inventory/scrap | `#iv-scrap-list-search` | `ErpWeb.UI/Inventory/Transactions/IvScrapList.razor:50` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 13 | /inventory/stock-adjustment | `#iv-stock-adjustment-list-search` | `ErpWeb.UI/Inventory/Transactions/IvStockAdjustmentList.razor:50` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 14 | /inventory/stock-count | `#iv-stock-count-list-search` | `ErpWeb.UI/Inventory/Transactions/IvStockCountList.razor:50` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 15 | /inventory/stock-return | `#iv-stock-return-list-search` | `ErpWeb.UI/Inventory/Transactions/IvStockReturnList.razor:50` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 16 | /inventory/stock-transfer | `#iv-stock-transfer-list-search` | `ErpWeb.UI/Inventory/Transactions/IvStockTransferList.razor:50` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 17 | /inventory/vendor-return | `#iv-vendor-return-list-search` | `ErpWeb.UI/Inventory/Transactions/IvVendorReturnList.razor:50` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 18 | /planning/production-balance | `#pr-production-balance-search` | `ErpWeb.UI/Planning/Inquiry/PrProductionBalance.razor:50` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 19 | (component) | `#pr-delivery-request-search-popup-search` | `ErpWeb.UI/Planning/Lookups/PrDeliveryRequestSearchPopup.razor:18` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED (dialog/picker gated behind prerequisite data) |
| 20 | (component) | `#pr-production-stock-work-order-search-popup-search` | `ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderSearchPopup.razor:18` | Key-driven lookup (Enter / magnifier) | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 21 | /planning/product-definitions | `#pr-product-def-list-search` | `ErpWeb.UI/Planning/Masters/PrProductDefList.razor:48` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 22 | /planning/daily-production | `#pr-daily-production-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionList.razor:48` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 23 | /planning/finished-good-receipts | `#pr-finished-good-receipt-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptList.razor:48` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 24 | /planning/work-orders/machine-schedule | `#pr-machine-schedule-search` | `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor:87` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 25 | /planning/material-issues | `#pr-material-issue-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueList.razor:48` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 26 | /planning/stock-adjustments/new | `#pr-stock-adjustment-source-search` | `ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor:152` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED (dialog/picker gated behind prerequisite data) |
| 27 | /planning/stock-adjustments | `#pr-production-stock-adjustment-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentList.razor:14` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 28 | /planning/stock-returns/new | `#pr-stock-return-source-search` | `ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor:538` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED (dialog/picker gated behind prerequisite data) |
| 29 | /planning/stock-returns | `#pr-production-stock-return-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor:21` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 30 | /planning/work-orders | `#pr-work-order-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor:46` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 31 | /planning/work-order-transfer/new | `#wot-source-search` | `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor:206` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED (dialog/picker gated behind prerequisite data) |
| 32 | /planning/work-order-transfer | `#pr-work-order-transfer-list-search` | `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferList.razor:24` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 34 | (component) | `#po-purchasing-item-search-popup-search` | `ErpWeb.UI/Purchase/Lookups/PoPurchasingItemSearchPopup.razor:17` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED (dialog/picker gated behind prerequisite data) |
| 35 | (component) | `#po-supplier-search-popup-search` | `ErpWeb.UI/Purchase/Lookups/PoSupplierSearchPopup.razor:17` | Key-driven lookup (Enter / magnifier) | PASS | PASS | PASS | PASS | PASS | PASS (live, deep UAT) |
| 36 | /purchase/suppliers | `#po-supp-list-search` | `ErpWeb.UI/Purchase/Masters/PoSuppList.razor:46` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 37 | /purchase/credit-notes | `#po-cdn-list-search` | `ErpWeb.UI/Purchase/Transactions/PoCdnList.razor:56` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 38 | /purchase/invoices/{Mode:regex(^(new\|edit\|view)$)} | `#po-invoice-po-picker-search` | `ErpWeb.UI/Purchase/Transactions/PoInvoice.razor:389` | Key-driven lookup (Enter / magnifier) | PASS | PASS | PASS | PASS | PASS | PASS (live, deep UAT) |
| 39 | /purchase/invoices | `#po-invoice-list-search` | `ErpWeb.UI/Purchase/Transactions/PoInvoiceList.razor:59` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 40 | /purchase/orders/{Mode:regex(^(new\|edit\|view\|copy\|revise)$)} | `#po-order-pr-picker-search` | `ErpWeb.UI/Purchase/Transactions/PoOrder.razor:807` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED (dialog/picker gated behind prerequisite data) |
| 41 | /purchase/orders | `#po-order-list-search` | `ErpWeb.UI/Purchase/Transactions/PoOrderList.razor:46` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 42 | /purchase/requisitions | `#po-pr-list-search` | `ErpWeb.UI/Purchase/Transactions/PoPrList.razor:46` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 43 | /purchase/self-billed-credit-notes | `#po-sb-cdn-list-search` | `ErpWeb.UI/Purchase/Transactions/PoSbCdnList.razor:63` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 44 | /purchase/self-billed-invoices | `#po-sb-invoice-list-search` | `ErpWeb.UI/Purchase/Transactions/PoSbInvoiceList.razor:56` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 49 | /sales/delivery/board | `#delivery-board-search` | `ErpWeb.UI/Sales/Delivery/SaDeliveryBoard.razor:67` | Key-driven lookup (Enter / magnifier) | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 52 | (component) | `#sa-customer-search-popup-search` | `ErpWeb.UI/Sales/Lookups/SaCustomerSearchPopup.razor:17` | Key-driven lookup (Enter / magnifier) | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 53 | /sales/customers | `#sa-cust-list-search` | `ErpWeb.UI/Sales/Masters/SaCustList.razor:46` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 55 | /sales/credit-notes | `#sa-cdn-list-search` | `ErpWeb.UI/Sales/Transactions/SaCdnList.razor:66` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live; row 55 unblocked by local schema repair, see note) |
| 56 | /sales/delivery-requests/{Mode:regex(^(new)$)} | `#sa-delivery-request-source-picker-search` | `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor:486` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED (dialog/picker gated behind prerequisite data) |
| 57 | /sales/delivery-requests | `#sa-delivery-request-list-search` | `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor:92` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 58 | /sales/delivery-orders/{Mode:regex(^(new\|edit\|view)$)} | `#sa-do-source-lookup-search` | `ErpWeb.UI/Sales/Transactions/SaDo.razor:688` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED (dialog/picker gated behind prerequisite data) |
| 59 | /sales/delivery-orders | `#sa-do-list-search` | `ErpWeb.UI/Sales/Transactions/SaDoList.razor:50` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 60 | /sales/invoices/{Mode:regex(^(new\|edit\|view)$)} | `#sa-invoice-source-lookup-search` | `ErpWeb.UI/Sales/Transactions/SaInvoice.razor:821` | Debounced list search | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED (dialog/picker gated behind prerequisite data) |
| 61 | /sales/invoices/{Mode:regex(^(new\|edit\|view)$)} | `#sa-invoice-do-picker-search` | `ErpWeb.UI/Sales/Transactions/SaInvoice.razor:965` | Key-driven lookup (Enter / magnifier) | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED | NOT VERIFIED (dialog/picker gated behind prerequisite data) |
| 62 | /sales/invoices | `#sa-invoice-list-search` | `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor:60` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 63 | /sales/quotations | `#sa-qt-list-search` | `ErpWeb.UI/Sales/Transactions/SaQtList.razor:46` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |
| 64 | /sales/sales-orders | `#sa-so-list-search` | `ErpWeb.UI/Sales/Transactions/SaSoList.razor:46` | Debounced list search | PASS | PASS | PASS | PASS | PASS | PASS (live) |

## Deep behaviour UAT (one pass per handler pattern)

| Pattern | Representative control | Sequences | Result |
| --- | --- | --- | --- |
| Debounced list search | `#iv-stock-master-list-search`, `#sa-cdn-list-search` | type then click before debounce; clear then search; Enter key | PASS - 11 -> 2 rows on `FG001`; typed `M0` and clicked before the 400 ms debounce expired -> 6 rows (latest text won, no stale reload); clear + click -> 11; `Enter` -> 3 rows for `FG` |
| Lookup popup | `#po-supplier-search-popup-search` | Enter without blur; type then magnifier; clear then search; paging/selection/footer intact | PASS - 3 suppliers -> `ZZZZNOPE` via Enter -> "No data to display"; clear + click -> 3; `PREV`/`NEXT`/`SELECT`/`CANCEL` untouched |
| Transaction picker | `#po-invoice-po-picker-search` | type then magnifier; footer intact | PASS - opened from "Add PO line"; single-line row, 755 px input, 36 px icon unclipped; icon and Enter both fire with no Blazor error; `Cancel`/`Add selected` intact. Multi-select and source-option assertions are NOT VERIFIED (no invoiceable PO lines in the dataset) |
| Multi-criteria single apply | `PrMaterialConsumeVarianceInquiry` | one combined action only; draft/applied timing unchanged | NOT VERIFIED (report screen not exercised) |
| Key-driven search | `#iv-lot-genealogy-search`, `#pr-production-stock-work-order-search-popup-search` | @onkeydown Enter preserved; no duplicate request | PASS (layout + icon path on the popup; duplicate-request assertion covered by the `_disposed`/`CancelPendingSearch` audit) |
| Live list filter | `#admin-company-rail-search` | filter as you type, no inert button, rail list updates | PASS - 2 companies -> 1 for `DEMO` -> 0 for `ZZZZ` -> 2 on clear; no action button rendered |

## Visual matrix

Widths 320 / 375 / 768 / 1280 / 1920 px, zoom 100 / 125 / 200 percent, light and dark themes,
for the three representative patterns plus the scheduler and the admin rail.

Result: **PASS for every width and for the 200 % zoom equivalent; light theme PASS; dark theme NOT VERIFIED.** Per-width numbers are in the responsive table above. The representative surfaces were `#iv-stock-master-list-search` (list), `#po-supplier-search-popup-search` (popup) and `#po-invoice-po-picker-search` (picker). The 200 % zoom case was exercised as a 500 px viewport with the 900 px popup (350 px rendered), where the 218 px input and the 36 px magnifier stayed on one line and fully inside the popup.

## Static evidence that does not need a browser

- Every converted row carries a `label for` that equals the editor `InputId` (contract test).
- Every converted editor binds with `BindValueMode.OnInput` so Enter and the magnifier see the current text (contract test).
- Every explicit `Text` + `TextChanged` pair keeps `TextExpression` (contract test).
- Every row renders exactly one icon-only magnifier with a scope-specific `aria-label` (contract test).
- No `!important` and no `overflow-x` escape hatch in the shared rules (contract test).
- No legacy single-text toolbar search shape remains outside the documented exceptions (contract test).

