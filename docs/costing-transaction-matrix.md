# Costing transaction matrix

Approved V1 financial method: **moving weighted average** at
`CompanyCode + BranchCode + ItemCode`. Physical allocation remains at warehouse/location/lot/status
grain. `StockValuationFact` is the historical monetary authority; `IvBalLoc` prices remain operational
cache/display fields only.

Every V2 route below reaches `IvInventoryPostingService`, stamps the new `IvTrxHistory` generation,
and is valued by `InventoryValuationService` inside the same `StockPostingContext`, DbContext and
database transaction before the posting is sealed.

| Route | Service / entry point | Physical leg and dimensions | Current source / rollback seam | V1 valuation and close rule |
|---|---|---|---|---|
| Misc receipt (`MR`) | `IvMiscReceiptService.PostAsync` -> `IvInventoryPostingService.PostAsync` | IN to item/warehouse/location/lot/status | `IvTrxBatch` line; append-only V2 reversal when enabled | Approved explicit base price; `MANUAL_APPROVED`. Missing cost blocks posting/close. |
| Goods receipt (`GR`) | `IvGoodsReceiptService.PostAsync` | IN; PO/line identities are copied to history | GR batch/detail; V2 exact reversal | Receipt commercial/base price; `RECEIPT_ACTUAL`. PI variance is a later adjustment and never rewrites this fact. |
| Non-stock goods receipt (`NG`) | `IvGoodsReceiptService.PostAsync` | No controlled-stock balance leg | Operational history only | Not financially relevant to inventory valuation. |
| Misc issue (`MI`) | `IvMiscIssueService.PostAsync` | OUT from item/warehouse/location/lot/status | Source `IvBalLocId`; V2 exact reversal | Current pool average; final depletion consumes exact remaining value. |
| Scrap (`SC`) | `IvScrapService.PostAsync` | OUT | Same issue core | Current pool average; classified `SCRAP_OUT`. |
| Vendor return (`VR`) / physical purchase CN | `IvVendorReturnService.PostAsync` or `PoCdnService.PostAsync` | OUT | Same issue core, PO/CN write-back in caller transaction | Current pool average in V1; exact rollback. |
| Customer/sales return (`CR`) | `IvStockReturnService.PostAsync` or `SaCdnService.PostAsync` | IN | Invoice/DO reference is retained on history when supplied | Original outbound fact cost when resolvable (`ORIGINAL_SALE_RETURN`); otherwise posting requires approved explicit cost. |
| Transfer (`TR`) | `IvStockTransferService.PostAsync` | OUT source slice + IN destination slice | One history row with both balance identities; exact reversal | Two deterministic fact splits. Destination receives exact source-out value. Net pool quantity/value is zero. |
| Adjustment / stock take (`ADJ`) | `IvStockAdjustmentService.PostAsync`; `IvStockCountService.PostAsync` | IN or OUT on counted slice | Stock-count owns the surrounding transaction; same adjustment core | IN requires approved cost; OUT uses moving average. Required unvalued legs block close. |
| Delivery Order shipment (`SP`) | `SaDoService.PostAsync` -> `IvSpShipmentService` -> stock-out core | OUT | `IvTrxHistory.DoNo` identifies the owner | DO creates the only physical OUT valuation and is the historical COGS source (`SA_DO`). |
| Invoice, `LinkDo == true` | `SaInvoiceService.PostAsync` | **No second stock movement** | Invoice retains DO/application identity; current code filters the line before shipment creation/post | Resolves/aggregates the originating DO valuation. Creates no invoice valuation and no duplicate COGS. |
| Direct invoice, `LinkDo == false` | `SaInvoiceService.PostAsync` -> `IvSpShipmentService` -> stock-out core | OUT | `IvTrxHistory.InvNo` identifies the owner | Invoice owns the OUT valuation/COGS (`SA_INVOICE`). |
| Mixed invoice | `SaInvoiceService.PostAsync` | Linked lines: none; direct lines: OUT | Per-line `LinkDo` plus DO/application identity | Profitability combines linked DO facts with direct invoice facts once each. |
| Issue to production (`IP`) | `ProductionMaterialIssueService.PostAsync` -> stock-out core | Inventory OUT | Existing `InventoryHistoryId`, `StockPostingId`, source/split/reversal fields on production movements | Inventory OUT moving-average amount is authoritative production material input. No duplicate production lineage columns. |
| Production return | production material rollback/return lifecycle | Inventory IN / production OUT as applicable | Existing movement/original/reversal lineage | Exact original value; never recalculated at current price. |
| Daily production / WIP output | `ProductionOutputService.PostAsync` | Production balance movements | Existing `ProductionBalLotMovement` posting/history/source identities | Production subledger preserves material/prior-WIP/conversion value and exact final depletion. |
| Finished-good receipt (`FG`) | `ProductionFinishedGoodReceiptService.PostAsync` | Production FG/WIP OUT + inventory FG IN | Same V2 envelope; history carries `ExactTransferredValue` | Inventory receipt uses the exact production transfer amount (`PRODUCTION_ACTUAL`). Rollback reverses exact value. |

## Mandatory invariants

- `BaseQty` and `CostAmount` are positive magnitudes; `Direction` is `+1` or `-1`.
- Fact identity is `StockPostingId + PostingLineNo + SplitOrdinal`.
- Inventory history/balance/lot foreign keys remain `int`; posting and production identities remain `long`.
- Backdated movement is accepted only when the cost pool has no later valued fact.
- Reversal facts copy the original amount and link `ReversesValuationFactId`; they never use current cost.
- Required `UNVALUED` facts, physical/value mismatches, or cost-state mismatches block financial close.
- `StockPeriodSnapshot*` remains quantity-only. `StockValuationPeriodSnapshot*` is the independent financial close.
