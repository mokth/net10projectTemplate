# UI Search Control Audit

Generated: 2026-10-29 21:14:19 +08:00  
Repository: `C:\wincom\net10projects-inline-search`  
Branch: `ui/global-inline-search-dev` at `8d09a22`

## File reconciliation

| Scope | Razor files |
| --- | --- |
| `ErpWeb.UI` | 250 |
| `ErpWeb` host | 4 |
| Total audited | 254 |
| UI files with `erp-inline-search` rows | 55 |
| Search-bearing Razor files changed on this branch | 105 |

The local base has 250 ``ErpWeb.UI`` Razor files against 249 on ``origin/productionv2``.
The single extra file is ``ErpWeb.UI/Components/Common/OperatorError/ErpOperatorFailureBanner.razor``,
introduced by local commit ``8d09a22`` (operator-error banner), which is not part of the delivery branch.

## Totals

| Status | Controls |
| --- | --- |
| CONVERTED (inline standard) | 56 |
| EXCLUDED (documented disposition) | 8 |
| BLOCKED | 0 |
| **Total audited controls** | **64** |

## Disposition summary

| Disposition | Controls |
| --- | --- |
| `INLINE_LIVE_FILTER` | 1 |
| `INLINE_SINGLE_TEXT_SEARCH` | 55 |
| `MULTI_CRITERIA_SINGLE_APPLY` | 1 |
| `TYPEAHEAD_FILTER` | 7 |

## Control equation

```text
audited controls (64) = converted (56) + documented exceptions (8) + blocked (0)
razor files (254) = files with a text search control (63) + files without one (191)
```

## Control-level audit

Binding classes: ``BIND_TEXT`` (@bind-Text only), ``BIND_TEXT_AFTER`` (@bind-Text:after side effect), 
``EXPLICIT_TEXT_CHANGED`` (explicit Text/TextChanged/TextExpression), ``KEY_EVENT_DRIVEN`` (Enter/@onkeydown).

| # | Path | Line | Control (InputId / binding) | Kind | Visible caption / placeholder | Label for | Binding | Value mode | Enter path | Click path | Accessible name | Disposition | Status | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `ErpWeb.UI/Admin/AdminCompany.razor` | 34 | `#admin-company-rail-search` | DxTextBox (live filter) | Search | `admin-company-rail-search` | BIND_TEXT | OnInput | none | (none) | (none) | `INLINE_LIVE_FILTER` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 2 | `ErpWeb.UI/Inventory/Inquiry/IvBalanceLot.razor` | 58 | `#iv-balance-lot-search` | DxTextBox + magnifier | Search | `iv-balance-lot-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search lot balances | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 3 | `ErpWeb.UI/Inventory/Inquiry/IvLotGenealogyInquiry.razor` | 35 | `#iv-lot-genealogy-search` | DxTextBox + magnifier | Search | `iv-lot-genealogy-search` | EXPLICIT_TEXT_CHANGED | OnInput | @onkeydown -> apply | @SearchAsync | @(IsSearching ?  | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 4 | `ErpWeb.UI/Inventory/Inquiry/IvTrxInquiry.razor` | 63 | `#iv-trx-inquiry-search` | DxTextBox + magnifier | Search | `iv-trx-inquiry-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search inventory transactions | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 5 | `ErpWeb.UI/Inventory/Lookups/IvBalLocSearchPopup.razor` | 17 | `#iv-bal-loc-search-popup-search` | DxTextBox + magnifier | Search | `iv-bal-loc-search-popup-search` | BIND_TEXT | OnInput | none | @SearchAsync | Search lot balances | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 6 | `ErpWeb.UI/Inventory/Lookups/IvStockMasterSearchPopup.razor` | 17 | `#iv-stock-master-search-popup-search` | DxTextBox + magnifier | Search | `iv-stock-master-search-popup-search` | BIND_TEXT | OnInput | @onkeydown -> apply | @SearchAsync | Search stock items | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 7 | `ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor` | 46 | `#iv-stock-master-list-search` | DxTextBox + magnifier | Search | `iv-stock-master-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search stock items | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 8 | `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor` | 275 | `#iv-goods-receipt-po-search` | DxTextBox + magnifier | Search PO lines | `iv-goods-receipt-po-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplyPoPickerSearchAsync | Search purchase order lines | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 9 | `ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor` | 50 | `#iv-goods-receipt-list-search` | DxTextBox + magnifier | Search | `iv-goods-receipt-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search goods receipts | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 10 | `ErpWeb.UI/Inventory/Transactions/IvMiscIssueList.razor` | 50 | `#iv-misc-issue-list-search` | DxTextBox + magnifier | Search | `iv-misc-issue-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search miscellaneous issues | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 11 | `ErpWeb.UI/Inventory/Transactions/IvMiscReceiptList.razor` | 50 | `#iv-misc-receipt-list-search` | DxTextBox + magnifier | Search | `iv-misc-receipt-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search miscellaneous receipts | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 12 | `ErpWeb.UI/Inventory/Transactions/IvScrapList.razor` | 50 | `#iv-scrap-list-search` | DxTextBox + magnifier | Search | `iv-scrap-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search scrap documents | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 13 | `ErpWeb.UI/Inventory/Transactions/IvStockAdjustmentList.razor` | 50 | `#iv-stock-adjustment-list-search` | DxTextBox + magnifier | Search | `iv-stock-adjustment-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search stock adjustments | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 14 | `ErpWeb.UI/Inventory/Transactions/IvStockCountList.razor` | 50 | `#iv-stock-count-list-search` | DxTextBox + magnifier | Search | `iv-stock-count-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search stock counts | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 15 | `ErpWeb.UI/Inventory/Transactions/IvStockReturnList.razor` | 50 | `#iv-stock-return-list-search` | DxTextBox + magnifier | Search | `iv-stock-return-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search stock returns | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 16 | `ErpWeb.UI/Inventory/Transactions/IvStockTransferList.razor` | 50 | `#iv-stock-transfer-list-search` | DxTextBox + magnifier | Search | `iv-stock-transfer-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search stock transfers | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 17 | `ErpWeb.UI/Inventory/Transactions/IvVendorReturnList.razor` | 50 | `#iv-vendor-return-list-search` | DxTextBox + magnifier | Search | `iv-vendor-return-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search vendor returns | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 18 | `ErpWeb.UI/Planning/Inquiry/PrProductionBalance.razor` | 50 | `#pr-production-balance-search` | DxTextBox + magnifier | Search | `pr-production-balance-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search production balance | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 19 | `ErpWeb.UI/Planning/Lookups/PrDeliveryRequestSearchPopup.razor` | 18 | `#pr-delivery-request-search-popup-search` | DxTextBox + magnifier | Search | `pr-delivery-request-search-popup-search` | BIND_TEXT | OnInput | @onkeydown -> apply | @SearchAsync | Search delivery requests | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 20 | `ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderSearchPopup.razor` | 18 | `#pr-production-stock-work-order-search-popup-search` | DxTextBox + magnifier | Search | `pr-production-stock-work-order-search-popup-search` | BIND_TEXT | OnInput | @onkeydown -> apply | @SearchPageAsync | Search work orders | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 21 | `ErpWeb.UI/Planning/Masters/PrProductDefList.razor` | 48 | `#pr-product-def-list-search` | DxTextBox + magnifier | Search | `pr-product-def-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search products | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 22 | `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionList.razor` | 48 | `#pr-daily-production-list-search` | DxTextBox + magnifier | Search | `pr-daily-production-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search daily production | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 23 | `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptList.razor` | 48 | `#pr-finished-good-receipt-list-search` | DxTextBox + magnifier | Search | `pr-finished-good-receipt-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search finished goods receipts | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 24 | `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor` | 87 | `#pr-machine-schedule-search` | DxTextBox + magnifier | Search | `pr-machine-schedule-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search the machine schedule | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 25 | `ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueList.razor` | 48 | `#pr-material-issue-list-search` | DxTextBox + magnifier | Search | `pr-material-issue-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search material issues | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 26 | `ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor` | 152 | `#pr-stock-adjustment-source-search` | DxTextBox + magnifier | Search | `pr-stock-adjustment-source-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @(() => LoadSourcesAsync(debounce: false)) | Search source production stock pools | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 27 | `ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentList.razor` | 14 | `#pr-production-stock-adjustment-list-search` | DxTextBox + magnifier | Search | `pr-production-stock-adjustment-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search production stock adjustments | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 28 | `ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor` | 538 | `#pr-stock-return-source-search` | DxTextBox + magnifier | Search source pools | `pr-stock-return-source-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySourceSearchAsync | Search production stock source pools | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 29 | `ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor` | 21 | `#pr-production-stock-return-list-search` | DxTextBox + magnifier | Search | `pr-production-stock-return-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search production stock returns | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 30 | `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor` | 46 | `#pr-work-order-list-search` | DxTextBox + magnifier | Search | `pr-work-order-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search work orders | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 31 | `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor` | 206 | `#wot-source-search` | DxTextBox + magnifier | Filter by item, description, or lot | `wot-source-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySourceSearchAsync | Filter WIP balances by item, description, or lot | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 32 | `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferList.razor` | 24 | `#pr-work-order-transfer-list-search` | DxTextBox + magnifier | Search | `pr-work-order-transfer-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search work order transfers | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 33 | `ErpWeb.UI/Purchase/Inquiry/PoSupplierInquiry.razor` | 53 | `(no binding)` | DxTextBox (outside inline row) | Search suppliers | `(n/a)` | EXPLICIT_TEXT_CHANGED | default | none | (n/a) | (n/a) | `TYPEAHEAD_FILTER` | EXCLUDED | Text box narrows the adjacent supplier combo option list, not the grid. |
| 34 | `ErpWeb.UI/Purchase/Lookups/PoPurchasingItemSearchPopup.razor` | 17 | `#po-purchasing-item-search-popup-search` | DxTextBox + magnifier | Search | `po-purchasing-item-search-popup-search` | BIND_TEXT | OnInput | @onkeydown -> apply | @SearchAsync | Search purchasing items | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 35 | `ErpWeb.UI/Purchase/Lookups/PoSupplierSearchPopup.razor` | 17 | `#po-supplier-search-popup-search` | DxTextBox + magnifier | Search | `po-supplier-search-popup-search` | BIND_TEXT | OnInput | @onkeydown -> apply | @SearchAsync | Search suppliers | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 36 | `ErpWeb.UI/Purchase/Masters/PoSuppList.razor` | 46 | `#po-supp-list-search` | DxTextBox + magnifier | Search | `po-supp-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search suppliers | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 37 | `ErpWeb.UI/Purchase/Transactions/PoCdnList.razor` | 56 | `#po-cdn-list-search` | DxTextBox + magnifier | Search | `po-cdn-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search purchase credit notes | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 38 | `ErpWeb.UI/Purchase/Transactions/PoInvoice.razor` | 389 | `#po-invoice-po-picker-search` | DxTextBox + magnifier | Search | `po-invoice-po-picker-search` | BIND_TEXT | OnInput | none | @SearchPoPickerAsync | Search invoiceable purchase order lines | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 39 | `ErpWeb.UI/Purchase/Transactions/PoInvoiceList.razor` | 59 | `#po-invoice-list-search` | DxTextBox + magnifier | Search | `po-invoice-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search purchase invoices | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 40 | `ErpWeb.UI/Purchase/Transactions/PoOrder.razor` | 807 | `#po-order-pr-picker-search` | DxTextBox + magnifier | Search | `po-order-pr-picker-search` | BIND_TEXT | OnInput | none | @SearchPrAsync | Search purchase requisitions | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 41 | `ErpWeb.UI/Purchase/Transactions/PoOrderList.razor` | 46 | `#po-order-list-search` | DxTextBox + magnifier | Search | `po-order-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search purchase orders | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 42 | `ErpWeb.UI/Purchase/Transactions/PoPrList.razor` | 46 | `#po-pr-list-search` | DxTextBox + magnifier | Search | `po-pr-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search purchase requisitions | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 43 | `ErpWeb.UI/Purchase/Transactions/PoSbCdnList.razor` | 63 | `#po-sb-cdn-list-search` | DxTextBox + magnifier | Search | `po-sb-cdn-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search sub-contract credit notes | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 44 | `ErpWeb.UI/Purchase/Transactions/PoSbInvoiceList.razor` | 56 | `#po-sb-invoice-list-search` | DxTextBox + magnifier | Search | `po-sb-invoice-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search sub-contract invoices | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 45 | `ErpWeb.UI/Sales/Analysis/SaSalesByCategory.razor` | 50 | `(no binding)` | DxTextBox (outside inline row) | Search customers | `(n/a)` | EXPLICIT_TEXT_CHANGED | default | none | (n/a) | (n/a) | `TYPEAHEAD_FILTER` | EXCLUDED |  |
| 46 | `ErpWeb.UI/Sales/Analysis/SaSalesByItem.razor` | 51 | `(no binding)` | DxTextBox (outside inline row) | Search customers | `(n/a)` | EXPLICIT_TEXT_CHANGED | default | none | (n/a) | (n/a) | `TYPEAHEAD_FILTER` | EXCLUDED |  |
| 47 | `ErpWeb.UI/Sales/Analysis/SaSalesByWarehouse.razor` | 49 | `(no binding)` | DxTextBox (outside inline row) | Search customers | `(n/a)` | EXPLICIT_TEXT_CHANGED | default | none | (n/a) | (n/a) | `TYPEAHEAD_FILTER` | EXCLUDED |  |
| 48 | `ErpWeb.UI/Sales/Analysis/SaSalesSummary.razor` | 62 | `(no binding)` | DxTextBox (outside inline row) | Search customers | `(n/a)` | EXPLICIT_TEXT_CHANGED | default | none | (n/a) | (n/a) | `TYPEAHEAD_FILTER` | EXCLUDED |  |
| 49 | `ErpWeb.UI/Sales/Delivery/SaDeliveryBoard.razor` | 67 | `#delivery-board-search` | DxTextBox + magnifier | Search | `delivery-board-search` | BIND_TEXT | OnInput | none | ReloadAsync | Search trips by trip no, customer, driver or vehicle | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 50 | `ErpWeb.UI/Sales/Inquiry/SaCustomerInquiry.razor` | 46 | `(no binding)` | DxTextBox (outside inline row) | Search customers | `(n/a)` | EXPLICIT_TEXT_CHANGED | default | none | (n/a) | (n/a) | `TYPEAHEAD_FILTER` | EXCLUDED | Text box narrows the adjacent customer combo option list, not the grid. |
| 51 | `ErpWeb.UI/Sales/Inquiry/SaPriceChangeHistoryInquiry.razor` | 132 | `(no binding)` | DxTextBox (outside inline row) | Search reason text | `(n/a)` | @bind-Text | default | none | (n/a) | (n/a) | `MULTI_CRITERIA_SINGLE_APPLY` | EXCLUDED | Thirteen criteria share one SEARCH action. |
| 52 | `ErpWeb.UI/Sales/Lookups/SaCustomerSearchPopup.razor` | 17 | `#sa-customer-search-popup-search` | DxTextBox + magnifier | Search | `sa-customer-search-popup-search` | BIND_TEXT | OnInput | @onkeydown -> apply | @SearchAsync | Search customers | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 53 | `ErpWeb.UI/Sales/Masters/SaCustList.razor` | 46 | `#sa-cust-list-search` | DxTextBox + magnifier | Search | `sa-cust-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search customers | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 54 | `ErpWeb.UI/Sales/PriceInquiry.razor` | 32 | `@CustomerSearch` | DxTextBox (outside inline row) | Search customers | `(n/a)` | EXPLICIT_TEXT_CHANGED | default | none | (n/a) | (n/a) | `TYPEAHEAD_FILTER` | EXCLUDED |  |
| 55 | `ErpWeb.UI/Sales/Transactions/SaCdnList.razor` | 66 | `#sa-cdn-list-search` | DxTextBox + magnifier | Search | `sa-cdn-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search sales credit notes | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 56 | `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor` | 486 | `#sa-delivery-request-source-picker-search` | DxTextBox + magnifier | Search | `sa-delivery-request-source-picker-search` | BIND_TEXT | OnInput | none | @LoadEligibleAsync | Search eligible sales orders | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 57 | `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor` | 92 | `#sa-delivery-request-list-search` | DxTextBox + magnifier | Search | `sa-delivery-request-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search delivery requests | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 58 | `ErpWeb.UI/Sales/Transactions/SaDo.razor` | 688 | `#sa-do-source-lookup-search` | DxTextBox + magnifier | Search | `sa-do-source-lookup-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @SearchSoSourceHeadersAsync | Search source sales orders | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 59 | `ErpWeb.UI/Sales/Transactions/SaDoList.razor` | 50 | `#sa-do-list-search` | DxTextBox + magnifier | Search | `sa-do-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search delivery orders | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 60 | `ErpWeb.UI/Sales/Transactions/SaInvoice.razor` | 821 | `#sa-invoice-source-lookup-search` | DxTextBox + magnifier | Search | `sa-invoice-source-lookup-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @SearchSourceHeadersAsync | Search source documents | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 61 | `ErpWeb.UI/Sales/Transactions/SaInvoice.razor` | 965 | `#sa-invoice-do-picker-search` | DxTextBox + magnifier | Search | `sa-invoice-do-picker-search` | BIND_TEXT | OnInput | none | @SearchDoPickerAsync | Search delivery orders for lines | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 62 | `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor` | 60 | `#sa-invoice-list-search` | DxTextBox + magnifier | Search | `sa-invoice-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search sales invoices | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 63 | `ErpWeb.UI/Sales/Transactions/SaQtList.razor` | 46 | `#sa-qt-list-search` | DxTextBox + magnifier | Search | `sa-qt-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search quotations | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |
| 64 | `ErpWeb.UI/Sales/Transactions/SaSoList.razor` | 46 | `#sa-so-list-search` | DxTextBox + magnifier | Search | `sa-so-list-search` | EXPLICIT_TEXT_CHANGED | OnInput | none | @ApplySearchAsync | Search sales orders | `INLINE_SINGLE_TEXT_SEARCH` | CONVERTED | see plans/UI_SEARCH_EVIDENCE_MATRIX.md |

## Enter-driven manual searches (latest-text guarantee)

| Path | Control | Enter path | OnInput |
| --- | --- | --- | --- |
| `ErpWeb.UI/Inventory/Inquiry/IvLotGenealogyInquiry.razor` | `#iv-lot-genealogy-search` | @onkeydown -> apply | OnInput |
| `ErpWeb.UI/Inventory/Lookups/IvStockMasterSearchPopup.razor` | `#iv-stock-master-search-popup-search` | @onkeydown -> apply | OnInput |
| `ErpWeb.UI/Planning/Lookups/PrDeliveryRequestSearchPopup.razor` | `#pr-delivery-request-search-popup-search` | @onkeydown -> apply | OnInput |
| `ErpWeb.UI/Planning/Lookups/PrProductionStockWorkOrderSearchPopup.razor` | `#pr-production-stock-work-order-search-popup-search` | @onkeydown -> apply | OnInput |
| `ErpWeb.UI/Purchase/Lookups/PoPurchasingItemSearchPopup.razor` | `#po-purchasing-item-search-popup-search` | @onkeydown -> apply | OnInput |
| `ErpWeb.UI/Purchase/Lookups/PoSupplierSearchPopup.razor` | `#po-supplier-search-popup-search` | @onkeydown -> apply | OnInput |
| `ErpWeb.UI/Sales/Lookups/SaCustomerSearchPopup.razor` | `#sa-customer-search-popup-search` | @onkeydown -> apply | OnInput |

## Side-effectful text callbacks

| Path | Line | Binding | Reason kept off OnInput |
| --- | --- | --- | --- |

## Blocked controls

None.

