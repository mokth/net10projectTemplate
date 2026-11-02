# ERP Entry Actions UI-only — Phase 0 scope matrix

**Local HEAD:** `cebb85487cd6cbc1f1e928df2bc58658aa1e567a` (`fix/dataprotection-key-ring`, message: `checked in Pur inv cn dn fix`)  
**DevExpress.Blazor:** `26.1.4` (`ErpWeb.UI.csproj`)  
**Scan date:** 2026-10-30  
**Pre-existing dirty (must not overwrite):** `.agents/skills/*`, `.gitignore`, `ANTIFORGERY_FIX.md`, `DEPLOYMENT_GUIDE.md`, `FINAL_DEPLOYMENT_STEPS.md`, `deploy.ps1`, untracked skills/docs/replay snapshot. Allowlisted UI files were clean at start of this change.

## Scan counts (local)

| Scan | Result |
|---|---|
| `<CommonDataGridEx` / `<CommonDataGrid` `.razor` | 123 files. `CommonDataGrid` consumer is `InventoryDemo.razor` only. |
| `FixedPosition=` before this change | `PoCdn.razor` main line grid only. Shared grids get pinning in Phase 1. |
| `<DxGridCommandColumn` | Shared grids + allowlisted entry/detail pages + extra audit-only pages listed below. |
| `LayoutAutoLoading` / `:v2` | `CommonDataGridEx.razor.cs`, `CommonDataGrid.razor.cs`, `GridLayoutPersistence.cs`. |

## Shared list grids — FIX (Phase 1)

| Route / page | File | Archetype | Action column | Layout | Footer | Outcome |
|---|---|---|---|---|---|---|
| All `CommonDataGridEx` lists (122 pages), Gate 1 sample `/inventory/misc-receipt` | `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor` (+ load hook `.razor.cs`) | Transaction/master/inquiry lists | `DxGridCommandColumn` + optional selection; `ActionButtons` | `erp-grid-layout:{GridKey}:v2` via `GridLayoutPersistence` | n/a (list) | **FIX** — pin Command `VisibleIndex=0` / Selection `1` with `FixedPosition.Left`; keep Selection-then-Command declaration order; map `ButtonInfo.Style`; reconcile optional Command/Selection on load |
| Inventory demo | `CommonDataGrid.razor` | Demo | `RowActions` | same `:v2` helper | n/a | **FIX** — same pin/load contract; no `Style` on `GridRowAction` |

## Direct entry/detail action columns — FIX (Phase 2)

| Route | File | Grid | Outcome |
|---|---|---|---|
| `/inventory/misc-receipt/{mode}` family | `IvMiscReceipt.razor` | Main 88px command | **FIX** pin left |
| `/inventory/misc-issue/...` | `IvMiscIssue.razor` | Main 88px | **FIX** pin left |
| `/inventory/goods-receipt/...` | `IvGoodsReceipt.razor` | Main 88px | **FIX** pin left |
| `/inventory/stock-return/...` | `IvStockReturn.razor` | Main 88px | **FIX** pin left |
| `/inventory/vendor-return/...` | `IvVendorReturn.razor` | Main 88px | **FIX** pin left |
| `/inventory/stock-adjustment/...` | `IvStockAdjustment.razor` | Main 88px | **FIX** pin left |
| `/inventory/scrap/...` | `IvScrap.razor` | Main 88px | **FIX** pin left |
| `/inventory/stock-transfer/...` | `IvStockTransfer.razor` | Main 120px | **FIX** pin left |
| `/purchase/credit-notes|debit-notes/...` | `PoCdn.razor` | Main already `FixedPosition.Left` | **SKIP re-pin** |
| same | `PoCdn.razor` | Invoice picker 220px; line picker 90px | **FIX** pin (wide/scrollable popups) |
| `/purchase/self-billed-cdn/...` | `PoSbCdn.razor` | Main 88px (declared after data cols); origin picker 88px | **FIX** pin via `VisibleIndex=0` + `FixedPosition.Left`, declaration order unchanged |
| `/purchase/self-billed-invoice/...` | `PoSbInvoice.razor` | Main 88px (declared after data cols) | **FIX** pin left, declaration order unchanged |
| `/purchase/requisitions/...` | `PoPr.razor` | Main 88px | **FIX** pin left |
| `/purchase/orders/...` | `PoOrder.razor` | Main 88px | **FIX** pin left |
| `/sales/cdn/...` | `SaCdn.razor` | Main 88px | **FIX** pin left |
| `/sales/delivery-requests/...` | `SaDeliveryRequestEntry.razor` | Main sources 88px; shortage 110px | **FIX** pin left |
| `/sales/delivery-orders/...` | `SaDo.razor` | Main 88px; SO picker 115px | **FIX** pin left (picker is wide) |
| `/sales/invoices/...` | `SaInvoice.razor` | Main 88px; SO/DO picker 115px | **FIX** pin left |
| `/sales/quotations/...` | `SaQt.razor` | Main 88px | **FIX** pin left |
| `/sales/orders/...` | `SaSo.razor` | Main 88px | **FIX** pin left |
| `/sales/customers/...` | `SaCustEntry.razor` | Address 96px; Contact 96px | **FIX** pin left |
| `/planning/work-orders/...` | `PrWorkOrderEntry.razor` | Material Change 90px; Machine Select 90px (`DxGridDataColumn`) | **FIX** pin left `VisibleIndex=0` |
| `/planning/stock-adjustments/...` | `PrProductionStockAdjustmentEntry.razor` | REMOVE 90px | **FIX** pin left |
| `/planning/stock-returns/...` | `PrProductionStockReturnEntry.razor` | REMOVE 92px | **FIX** pin left |
| `/planning/finished-good-receipts/...` | `PrFinishedGoodReceiptEntry.razor` | REMOVE 110px | **FIX** pin left |

## Direct grids — tested exceptions / audit-only (no code)

| File | Why | Outcome |
|---|---|---|
| `SaDeliveryRequestEntry.razor` 72px eligible-source picker | Plan-named narrow popup | **EXCEPTION** — do not pin |
| `PoOrder.razor` 56px attachments; 70px/56px PR picker | Narrow attachment / compact picker | **EXCEPTION** — do not pin |
| `PoPr.razor` 56px attachments | Narrow | **EXCEPTION** — do not pin |
| `IvPeriodClose.razor` 90px REOPEN | Specialized lifecycle, not generic footer/actions | **EXCEPTION** — inspect only; do not pin |
| `IvStockCount.razor` | No row-action command column on lines | **EXCEPTION** |
| `SaCustPriceGroupList.razor` 92px, `IvClassList.razor` 72px, `SaDisGroupList.razor` 72px | Narrow modal editors | **EXCEPTION** — audit only |
| `PoSuppEntry.razor` 96px/72px | Extra Phase 0 hit; not on allowlist | **AUDIT ONLY** |
| `PrCompanyCalendar.razor` 70px | Calendar specialized | **AUDIT ONLY** |
| `PrProductDefEntry.razor` Draft vs Activate footer | F14 keep as-is | **EXCEPTION** |

## Footers already conforming (no Phase 3–5 edit)

Inventory 8 batch entries (`mr-footer` / `iv-footer` Cancel/Close + Save/Edit). Sales `SaQt/SaSo/SaDo/SaInvoice/SaCdn/SaDeliveryRequestEntry`. Purchase `PoPr/PoOrder` (F12 is icon-only on `PoInvoice`). Masters `IvStockMasterEntry`, `SaCustEntry`, `PoSuppEntry`.

## Footer visual-fix (Phases 3–5)

| ID | Route | File | Current | Outcome |
|---|---|---|---|---|
| F01 | `/purchase/credit-notes|debit-notes/{new\|edit\|view}` | `PoCdn.razor` (+ new `PoCdn.razor.css`) | Single `sdoc-footer__actions`: Save, Edit, Post/Delete/Rollback, Close. Save `Enabled="@CanSave"`. Close only. | **FIX** mode-aware Cancel/Save and Close/Edit on the right; lifecycle left; keep `@CanSave` and lifecycle bindings byte-for-byte |
| F02 | self-billed CN/DN | `PoSbCdn.razor` | Close always; Edit if `IsView && CanEdit && IsEditable`; Save `Enabled="@(IsEditable && CanSave)"` | **FIX** Close only in View; Cancel in `!IsView`; both `Click="@GoBack"` |
| F03 | self-billed invoice | `PoSbInvoice.razor` | Close always; Edit if `IsView && CanEdit && !Model.IsEInvoiceLocked`; Save `Enabled="@(IsEditable && CanSave)"` | **FIX** same Close/Cancel split |
| F12 | purchase invoice | `PoInvoice.razor` | Cancel lacks icon | **FIX** add `fa-solid fa-xmark` only |
| F04 | `/planning/shifts/{ShiftCd?}` | `PrShiftEntry.razor` | SAVE then CANCEL in card header (reversed) | **FIX** move to bottom `iv-footer`, Cancel then Save, actions `margin-inline-start:auto` |
| F05 | `/planning/work-orders/{mode}` | `PrWorkOrderEntry.razor` | SAVE DRAFT + EDIT DRAFT in command bar | **FIX** one Save Draft to sticky footer with Cancel; View Close + Edit Primary; keep BACK/HOW TO USE/lifecycle/MORE |
| F06 | stock adjustments | `PrProductionStockAdjustmentEntry.razor` | SAVE under `@if (CanSave)`; EDIT DRAFT Secondary | **FIX** relocate Save with same wrapper/`Enabled="@(!Busy)"`; Cancel/Close `BackAsync`; Edit Primary in View |
| F07 | stock returns | `PrProductionStockReturnEntry.razor` | SAVE under `@if (CanEditFields)` `Enabled="@(CanSave && !Busy)"` | **FIX** relocate; Cancel/Close `BackAsync`; Edit stays Primary in View |
| F08 | WO transfer | `PrWorkOrderTransferEntry.razor` | SAVE under `@if (CanEditFields)` `Enabled="@(CanSave && !Busy)"`; confirm ROLLBACK Danger | **FIX** relocate; Cancel/Close `BackToList`; confirm ROLLBACK → Warning |
| F09 | daily production | `PrDailyProductionEntry.razor` + css | `.pr-dp-footer` not sticky; View has no Close | **FIX** sticky; View Close `BackToList` at end of view branch |
| F10 | FG receipt | `PrFinishedGoodReceiptEntry.razor` + css | same; View after `LoadFailed` | **FIX** sticky; View Close only in `IsViewMode` branch |
| F11 | material issue | `PrMaterialIssueEntry.razor` | footer already sticky; View has no Close | **FIX** View Close `BackToList` |
| F13 | DO/Invoice confirm Post | `SaDo.razor`, `SaInvoice.razor` | confirm Post `Primary` | **FIX** `Success` |

## Specialized exceptions (inspect only)

`IvStockCount`, `IvPeriodClose`, `PrHierarchyTree`, calendars, popup master editors, `SaCustPriceGroupList`, Product Def F14 Draft vs Activate.

## OPEN residual dirty-nav (report, do not fix)

`PoCdn`, `PoSbCdn`, `PoSbInvoice`, `PoInvoice`, `PrShiftEntry`, `PrDailyProductionEntry`, `PrMaterialIssueEntry`. Relabeling Close→Cancel does not add discard protection.

## Baseline build/test

Captured during Phase 0/6 execution. See implementation report for actual `dotnet` exit codes (never PASS by inspection).
