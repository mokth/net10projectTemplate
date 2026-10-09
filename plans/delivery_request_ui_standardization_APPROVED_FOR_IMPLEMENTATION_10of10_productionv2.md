# Delivery Request UI Standardization — APPROVED Code Agent Plan

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Verified HEAD:** `213cdbd1e1cf77a890d1604eaf9d2873370855bb`  
**HEAD message:** `Implement Sales Order delivery request traceability`  
**DevExpress.Blazor:** `26.1.4`  
**Reference UI:** `SaSoList` + `SaSo`  
**Target UI:** `SaDeliveryRequestList` + `SaDeliveryRequestEntry`  
**Plan status:** `APPROVED FOR IMPLEMENTATION — 10/10`

# Objective

Standardize the Delivery Request list and entry pages so they follow the current Sales Order UI/UX conventions already present in `productionv2`.

The implementation MUST reuse the existing Sales/shared UI patterns instead of creating a parallel DR-specific design system.

The implementation MUST NOT change Delivery Request persistence semantics, SO→DR lineage, DR→WO allocation rules, numbering, lifecycle authority, Work Order creation semantics, posting, costing, rollback, month-end, accounting, permissions, or database schema.

# Confirmed Problems

## 1. Delivery Request list does not follow the Sales list standard

Verified current target files:

- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor.css`

Confirmed current behavior:

- Uses native HTML/Blazor `<input>`, `<select>`, `<button>`, and `<table>`.
- Search calls `LoadAsync()` on every `@oninput` keystroke.
- Status filter changes do not automatically reload.
- Uses DR-specific `dr-toolbar`, `dr-table`, `dr-status`, and `dr-link` presentation.
- Does not use `CommonDataGridEx`.
- Does not provide the Sales desktop-grid/mobile-compact split.
- Does not use the Sales filter-popup pattern.
- Does not provide standard VIEW/EDIT row actions.
- `TotalUnplanned` is calculated only from the currently loaded `Rows`; current load is bounded by `Take = 100`, so that value is not a reliable all-record total when more rows exist.

Required correction:

- Rebuild the list using the current `SaSoList` composition and shared `iv-*` chrome.
- Replace the misleading unplanned hero KPI with the server-filtered Delivery Request count.

## 2. Delivery Request entry does not follow the Sales document standard

Verified current target files:

- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor.css`

Confirmed current behavior:

- Uses `<div class="iv-page dr-page">` instead of `SaDocPage`.
- Uses list-style `iv-hero` instead of `sdoc-titlebar`.
- Uses a top `dr-commandbar` instead of the Sales sticky `sdoc-footer`.
- Uses `InputText`, `InputDate`, `InputNumber`, raw `<textarea>`, and raw Bootstrap buttons rather than the DevExpress document form pattern.
- Uses hand-built HTML tables for SO demand sources, Work Orders, and audit events.
- Uses custom fixed-position picker markup instead of `DxPopup`.
- Success/error surfaces are not dismissible like Sales Order.
- Server validation dictionaries are flattened into one string.
- No dirty-state/discard confirmation exists.
- Lifecycle commands are mixed with Save/Edit controls.

Required correction:

- Rebuild presentation around `SaDocPage`, `sdoc-*`, `DxFormLayout`, `DxGrid`, `DxButton`, `DxPopup`, `SdValidationSummary`, and the Sales footer-action pattern.
- Preserve the existing service calls and payloads.

## 3. Delivery Request list is server-pageable but not server-sortable

Verified files:

- `ErpWeb.Core/Sales/ISaDeliveryRequestService.cs`
- `ErpWeb.Core/Sales/SaDeliveryRequestService.cs`

Confirmed current behavior:

- `SaDeliveryRequestListQuery` supports `SearchText`, `Status`, `ProductCode`, `RequiredDateFrom`, `RequiredDateTo`, `Skip`, and `Take`.
- `SearchAsync()` already performs tenant/branch filtering, filter application, total counting, server paging, and stable default ordering.
- Current default ordering is `RequiredDate DESC`, then `Uid DESC`.
- No list sort field/direction contract exists.
- Several displayed values are derived after the header query:
  - `WoAllocatedQty`
  - `UnplannedQty`
  - `ProducedQty`
  - displayed/derived `Status`
  - `SourceCount`
  - `WorkOrderCount`

Required correction:

- Add safe server sorting for direct header fields only.
- Explicitly disable user sorting for derived/progress columns.

## 4. Shared grid REFRESH requires an explicit page handler

Verified shared component:

- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs`

Confirmed behavior:

- The built-in REFRESH toolbar invokes `OnButtonEventHandle` with button text `REFRESH`.
- It does not reload an external `GridCustomDataSource` itself.

Required correction:

- `SaDeliveryRequestList.OnButtonClick(...)` MUST explicitly handle `REFRESH` and call the DR reload path.

## 5. Direct `/edit/{uid}` navigation can target a non-Draft DR

Confirmed current DR editability:

- Draft is editable.
- Non-Draft is not editable.
- A user can still manually navigate to the edit route.

Required correction:

- Add a Sales-style read-only presentation helper.
- A non-Draft DR opened through `/edit/{uid}` MUST render as read-only presentation and MUST NOT show Save controls.
- No server editability rule changes are required.

## 6. Entry does not provide the Sales-style concurrency recovery UX

Verified current result contract:

- `IvMasterOperationResult<T>` exposes `ErrorCode`.
- `IvMasterErrorCode.Concurrency` exists.
- DR save/lifecycle and DR→WO creation use RowVersion checks and can return concurrency failures.
- Sales Order already provides a `Reload required` popup pattern.

Required correction:

- Add a DR concurrency popup and `ReloadLatestAsync()`.
- Continue using existing RowVersion authority.
- Do not add automatic retry.

## 7. Existing Work Order trace can disappear when no unplanned quantity remains

Confirmed current Razor condition wraps the entire Work Order section in:

- DR status Released/In Production; and
- `UnplannedQty > 0.0001m`.

This means linked Work Orders can disappear from the UI once the DR is fully allocated/produced.

Required correction:

- Keep linked Work Order trace visible whenever `Detail.WorkOrders.Count > 0`.
- Show Work Order creation controls only when the existing creation eligibility remains true.
- This is presentation-only; no DR→WO business rule changes.

# Scope / Non-Goals

## In scope

- Delivery Request list UI standardization.
- Delivery Request entry UI standardization.
- `CommonDataGridEx` list integration.
- Server paging using the existing DR service.
- Search debounce.
- Status + required-date filter popup.
- Compact/mobile list cards.
- Standard VIEW/EDIT row actions.
- Safe direct-field server sorting.
- Explicit non-sortable derived columns.
- Sales document shell for entry.
- DevExpress form/grid/popup controls.
- Validation dictionary display via `SdValidationSummary`.
- Dirty-state/discard confirmation.
- Lifecycle confirmation.
- Concurrency reload popup.
- Existing linked Work Order trace visibility.
- Focused service tests for the list-query changes.
- Full Razor/solution build validation.

## MUST NOT change

- SO→DR traceability schema or source identity.
- DR→WO allocation schema or semantics.
- `SaDeliveryRequest`, `SaDeliveryRequestSource`, audit entities, or EF mappings.
- Delivery Request numbering.
- `CreateDraftAsync`.
- `UpdateDraftAsync`.
- `ReleaseAsync`.
- `CancelAsync`.
- `DeleteDraftAsync`.
- `CreateDraftFromDeliveryRequestAsync` semantics.
- Production snapshot/scheduling.
- Inventory.
- costing.
- posting.
- rollback/unpost.
- month-end.
- accounting.
- menu codes.
- permission codes.
- Sales Order reference pages.
- `SaDocPage.razor.css`.
- `inventory-chrome.css`.
- lookup architecture for Definition/Warehouse/Project/Priority.
- a new UI testing framework/package.

# Exact Files

## UI — Delivery Request list

- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor.css`

## UI — Delivery Request entry

- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor.css`

## Shared grid — backward-compatible enhancement

- `ErpWeb.UI/Components/Common/DataGrid/DataGridModel.cs`
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor`

## Core — list sort contract only

- `ErpWeb.Core/Sales/ISaDeliveryRequestService.cs`
- `ErpWeb.Core/Sales/SaDeliveryRequestService.cs`

## Tests

- `ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestServiceTests.cs`

## Reference only — DO NOT MODIFY

- `ErpWeb.UI/Sales/Transactions/SaSoList.razor`
- `ErpWeb.UI/Sales/Transactions/SaSoList.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaSoList.razor.css`
- `ErpWeb.UI/Sales/Transactions/SaSo.razor`
- `ErpWeb.UI/Sales/Transactions/SaSo.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaSo.razor.css`
- `ErpWeb.UI/Sales/Transactions/SaDocPage.razor`
- `ErpWeb.UI/Sales/Transactions/SaDocPage.razor.css`
- `ErpWeb.UI/Sales/Transactions/SdValidationSummary.razor`
- `ErpWeb.UI/Services/DocumentReturnNavigation.cs`

# Database Changes

**None.**

MUST NOT create:

- migration;
- SQL upgrade script;
- new table;
- new column;
- new index;
- new constraint;
- new view;
- new trigger;
- data backfill.

# Exact Changes

## A. `DataGridModel.cs` — add column-level sort capability

Add to `GridColumnData`:

```csharp
public bool? AllowSort { get; set; }
```

Rules:

- MUST be nullable.
- Default remains `null`.
- Existing grid consumers therefore retain DevExpress/default sorting behavior unchanged.
- Do not change existing defaults.

## B. `CommonDataGridEx.razor` — honor `AllowSort`

In every `DxGridDataColumn` rendering branch inside `RenderColumn(...)`, pass:

```razor
AllowSort="@col.AllowSort"
```

This includes:

- datetime
- time
- stringicon
- stringicon2
- stringiconlink
- link
- click
- numeric
- bool
- default

MUST NOT change:

- selection behavior;
- action buttons;
- filtering;
- grouping;
- layout persistence;
- export;
- toolbar behavior;
- column chooser;
- reset-layout behavior.

## C. `ISaDeliveryRequestService.cs` — extend list query

Add to `SaDeliveryRequestListQuery`:

```csharp
public string? SortField { get; set; }
public bool SortDescending { get; set; } = true;
```

Do not rename/remove any current query property.

## D. `SaDeliveryRequestService.SearchAsync()` — safe whitelist sorting

Preserve all existing:

- authorization;
- company/branch isolation;
- search filtering;
- status filtering/derivation;
- product filtering;
- required-date filtering;
- total count;
- paging limit;
- source counting;
- allocation/progress calculation;
- RowVersion mapping.

Supported sort fields MUST be only:

- `nameof(SaDeliveryRequestListRow.DeliveryRequestNo)`
- `nameof(SaDeliveryRequestListRow.ProductCode)`
- `nameof(SaDeliveryRequestListRow.ProductionUom)`
- `nameof(SaDeliveryRequestListRow.RequestedQty)`
- `nameof(SaDeliveryRequestListRow.RequiredDate)`
- `nameof(SaDeliveryRequestListRow.CreatedDate)`
- `nameof(SaDeliveryRequestListRow.CreatedBy)`

Rules:

1. Sort-field matching MUST be case-insensitive.
2. Do not use dynamic SQL.
3. Do not build an unrestricted reflection/expression sorter from client input.
4. Unsupported/null/empty sort field MUST use:
   - `RequiredDate DESC`
   - then `Uid DESC`.
5. For a supported ascending sort:
   - primary field ASC
   - then `Uid ASC`.
6. For a supported descending sort:
   - primary field DESC
   - then `Uid DESC`.
7. Apply ordering before `Skip/Take`.
8. MUST NOT add server sorting for:
   - `ProductDescription` in this task;
   - `WoAllocatedQty`;
   - `UnplannedQty`;
   - `ProducedQty`;
   - derived `Status`;
   - `SourceCount`;
   - `WorkOrderCount`.

## E. `SaDeliveryRequestList.razor.cs` — use Sales list state/data-source pattern

Add required imports matching the actual shared grid approach:

- `System.Collections`
- `System.Timers`
- `DevExpress.Blazor`
- `ErpWeb.UI.Components.Common.DataGrid`
- Timer alias if needed.

Implement `IDisposable`.

### State

Use:

- `DxGrid? _grid`
- debounce timer
- search-version guard
- `IsBootstrapping`
- `SearchText`
- `TotalCount`
- `CompactRows`
- `CanAdd`
- `CanEdit`
- filter popup state
- applied filter values
- draft filter values
- `SaDeliveryRequestGridDataSource DataSource`
- `Columns`
- `Buttons`
- `ActionButtons`

### Exact status filter options

Use:

- All
- `SaDeliveryRequestStatuses.Draft`
- `SaDeliveryRequestStatuses.Released`
- `SaDeliveryRequestStatuses.InProduction`
- `SaDeliveryRequestStatuses.Completed`
- `SaDeliveryRequestStatuses.Cancelled`

Keep applied and draft filter values separate so Cancel/Close of the popup does not silently change the active query.

### Search debounce

Implement `OnSearchTextChanged(string text)` using the same 400 ms debounce/version-guard strategy as `SaSoList`.

MUST NOT query on every keypress.

On debounce completion:

1. update datasource filters;
2. reload grid/compact preview;
3. update UI.

Dispose the timer in `Dispose()`.

### Grid custom datasource

Add proposed new page-local class:

`SaDeliveryRequestGridDataSource : GridCustomDataSource`

Model it on `SaSoGridDataSource` / `IvStockMasterGridDataSource`.

`GetItemCountAsync`:

- clone current filters;
- `Skip = 0`;
- `Take = 1`;
- call loader;
- return server `TotalCount`.

`GetItemsAsync`:

- clone filters;
- `Skip = Math.Max(0, options.StartIndex)`;
- `Take = Math.Clamp(options.Count <= 0 ? 20 : options.Count, 1, 100)`;
- if `SortInfo` exists, pass first sort field/direction exactly as the existing Sales/shared pattern does;
- call service loader;
- return only that page.

MUST NOT load all DRs and then page locally.

### Grid columns

Use:

1. DR No.
   - `DeliveryRequestNo`
   - sortable
2. Product
   - `ProductCode`
   - sortable
3. Description
   - `ProductDescription`
   - `AllowSort = false`
4. UOM
   - `ProductionUom`
   - sortable
5. Requested
   - `RequestedQty`
   - decimal
   - `n4`
   - sortable
6. WO allocated
   - `WoAllocatedQty`
   - decimal
   - `n4`
   - `AllowSort = false`
7. Unplanned
   - `UnplannedQty`
   - decimal
   - `n4`
   - `AllowSort = false`
8. Produced
   - `ProducedQty`
   - decimal
   - `n4`
   - `AllowSort = false`
9. Required date
   - `RequiredDate`
   - date
   - `dd/MM/yyyy`
   - sortable
   - `SortIndex = 0`
   - `SortOrder = GridColumnSortOrder.Descending`
10. Status
    - `Status`
    - `AllowSort = false`
11. SO sources
    - `SourceCount`
    - integer
    - `AllowSort = false`
12. Work Orders
    - `WorkOrderCount`
    - integer
    - `AllowSort = false`
13. Created by
    - `CreatedBy`
    - sortable
14. Created date
    - `CreatedDate`
    - datetime
    - `dd/MM/yyyy HH:mm`
    - sortable

Do not invent list fields.

### Toolbar buttons

Page-specific toolbar:

- `NEW`
  - plus icon
  - enabled by `CanAdd`.

Use `CommonDataGridEx` built-in:

- REFRESH
- RESET LAYOUT.

### Mandatory REFRESH handler

`OnButtonClick(...)` MUST contain:

- `NEW` → `/sales/delivery-requests/new`
- `REFRESH` → `ReloadGridAsync()`

Without the explicit `REFRESH` branch, the shared toolbar does not reload the external custom datasource.

### Row actions

Provide:

- VIEW
- EDIT

VIEW:

- `/sales/delivery-requests/view/{uid}`

EDIT:

- require `CanEdit`;
- require displayed row status `DRAFT`;
- otherwise set a clear page error and do not navigate;
- navigate to `/sales/delivery-requests/edit/{uid}` only when allowed.

Do not add list-level Release/Cancel/Delete/batch lifecycle actions.

### Compact preview

Use a bounded preview query:

- `Skip = 0`
- `Take = 50`

Card shows:

- DR number
- product
- required date
- status

Card click opens View.

Status class mapping:

- Draft → `is-hold`
- Cancelled → `is-off`
- Released → `is-on`
- In Production → `is-on`
- Completed → `is-on`

### Total count

`TotalCount` MUST come from server `TotalCount`.

Hero KPI MUST show Delivery Request count.

Remove `TotalUnplanned` from the list hero.

## F. `SaDeliveryRequestList.razor` — match `SaSoList`

Required structure:

1. `<div class="iv-page">`
2. dismissible success toast
3. dismissible error toast
4. `iv-hero`
5. total-count chip
6. `Filtered` chip when search/applied filters are active
7. right-side Delivery Request count KPI
8. `iv-card`
9. `iv-toolbar-row`
10. `DxTextBox` search
11. `DxButton` FILTER
12. bootstrapping skeleton
13. desktop `CommonDataGridEx`
14. compact/mobile card list
15. `DxPopup` filter dialog

`CommonDataGridEx` MUST use:

```text
KeyName = nameof(SaDeliveryRequestListRow.Uid)
GridKey = "sa-delivery-request-list"
ShowToolbarText = true
ShowResetLayoutButton = true
UseBuiltInExport = false
ShowSearchBox = false
ShowFilterRow = false
ShowGroupPanel = false
allowSelect = false
OnGridInstance = OnGridInstance
OnButtonEventHandle = OnButtonClick
OnActionEventHandle = OnActionClick
```

Filter popup:

- Header: `Filter delivery requests`
- Status
- Required date from
- Required date to
- Clear
- Apply
- `CloseOnOutsideClick="false"`
- `CssClass="common-popup"`

Remove:

- native list search input;
- native status select;
- raw Refresh button;
- raw New button;
- hand-built list table.

## G. `SaDeliveryRequestList.razor.css`

Follow `SaSoList.razor.css` philosophy.

Shared chrome remains in `/css/inventory-chrome.css`.

Retain only page-specific deltas required by the grid.

Delete obsolete selectors after Razor no longer references them:

- `.dr-toolbar`
- `.dr-status-filter`
- `.dr-table__row`
- `.dr-link`
- `.dr-status*`

Do not duplicate shared `iv-*` rules.

## H. `SaDeliveryRequestEntry.razor.cs` — Sales document interaction model

Keep current injected services:

- `ISaDeliveryRequestService`
- `IProductionWorkOrderService`
- `IAccessRightService`

Add `ErpWeb.UI.Services` for `DocumentReturnNavigation`.

### Add state

Add:

- `Dictionary<string,string> ValidationErrors`
- `_isDirty`
- `ConfirmDiscardVisible`
- `ConfirmLifecycleVisible`
- proposed page-local lifecycle action enum/state
- `ConcurrencyVisible`
- `SourcePickerError`
- lifecycle message/button text/style state as needed.

### Presentation helpers

Keep:

- `IsNew`
- `IsEdit`

Add:

```text
IsView
IsReadOnlyPresentation
CanEditFromView
CanOfferWorkOrderCreation
ShowWorkOrderSection
```

Required semantics:

`IsReadOnlyPresentation` MUST be true when:

- route mode is View; OR
- an existing loaded DR is not Draft.

This prevents a manually entered `/edit/{uid}` route from presenting Save controls for a non-Draft DR.

`CanEdit` remains:

- New + Add permission; OR
- Edit + Edit permission + loaded Draft;
- false while submitting.

`CanEditFromView` requires:

- read-only/view presentation;
- loaded Draft;
- Edit permission;
- not submitting.

Update lifecycle UI helpers so:

- Release/Cancel/Delete are available only in read-only presentation;
- existing status/permission rules remain unchanged.

Do not weaken server checks.

### Validation state

Before each server mutation:

1. clear `ErrorMessage`;
2. clear `ValidationErrors`;
3. clear stale success state as appropriate.

For failed operations:

- copy `result.ValidationErrors` into `ValidationErrors`;
- use `result.Message` as fallback `ErrorMessage`;
- do not flatten the dictionary into a single string when field errors exist.

Use `SdValidationSummary` in Razor.

### Concurrency handling

For these operations:

- Update Draft
- Release
- Cancel
- Delete
- Create Work Order

if `result.ErrorCode == IvMasterErrorCode.Concurrency`:

1. preserve/show the server message;
2. open `ConcurrencyVisible`;
3. do not retry automatically;
4. do not mutate local status;
5. allow user to reload latest.

Add:

`ReloadLatestAsync()`

Behavior:

- close concurrency popup;
- call the existing page load path for current `Uid`;
- replace page state with latest server detail;
- reset dirty/validation state;
- remain on the current route/presentation.

New Draft creation does not require concurrency reload handling because it has no existing DR RowVersion.

### Dirty tracking

`_isDirty` MUST become true when the user changes:

- Required date
- Definition code
- Warehouse code
- Project code
- Priority
- Remark
- source added
- source removed
- source allocated quantity changed

Use explicit change callbacks where necessary.

For text/memo fields, using the same `@bind-Text:after` pattern already present in Sales Order is acceptable.

For Required date, use an explicit `DateChanged` callback or equivalent verified DevExpress binding that marks dirty.

For source quantity, use:

```text
OnSourceQuantityChanged(SourceEditorRow row, decimal value)
```

and assign the value + mark dirty.

Do not mark dirty for transient Work Order creation inputs because those are not DR draft fields.

Reset `_isDirty = false` after:

- initial/new load;
- `ApplyDetail(...)`;
- successful save before navigation;
- successful reload latest.

### Exact cancel/close navigation

View Close:

```text
DocumentReturnNavigation.NavigateBack(
    Navigation,
    "/sales/delivery-requests")
```

New/Edit Cancel:

- if dirty → open discard popup;
- if not dirty → navigate to `/sales/delivery-requests`.

Discard confirmation:

- clear dirty state;
- navigate to `/sales/delivery-requests`.

Do not leave this as “list/view as appropriate”; use the exact Sales Order-style list fallback.

### Edit from view

`OnEditFromView()`:

- require current Detail;
- navigate to `/sales/delivery-requests/edit/{Detail.Uid}`.

### Lifecycle confirmation

Do not call Release/Cancel/Delete directly from footer click.

Add a proposed page-local action model, for example:

```text
None
Release
Cancel
Delete
```

Begin action:

- validate current UI permission/status helper;
- set confirmation message;
- open `ConfirmLifecycleVisible`.

Suggested exact messages:

Release:
`Release this Delivery Request to production?`

Cancel:
`Cancel this Delivery Request? Active or produced Work Order quantity will still be rejected by the server.`

Delete:
`Permanently delete this never-released Draft Delivery Request?`

Confirm button styles:

- Release → Primary
- Cancel → Danger
- Delete → Danger

`ConfirmLifecycleAsync()`:

- use current `Detail.Uid`;
- use current `Detail.RowVersion`;
- call the same existing service method;
- preserve current Cancel reason text:
  `Cancelled from Delivery Request entry.`
- no optimistic status mutation.

Success:

- Release/Cancel → `ApplyDetail(result.Data)` + success toast.
- Delete → navigate to list.

Failure:

- preserve validation dictionary/fallback error;
- concurrency uses concurrency popup.

### Source picker error isolation

Add `SourcePickerError`.

`LoadEligibleAsync()` and duplicate/mismatch checks while picker is open SHOULD display picker-local errors through `sdoc-popup-error` instead of placing an error behind the modal at the top of the page.

Clear picker error when:

- opening picker;
- closing picker;
- starting a new picker search;
- successful source add.

Keep the existing exact source rules unchanged.

### Source add/remove authority

Preserve:

- identity: `SoNo + CustRel + SoLine`;
- one product/UOM per DR;
- product/UOM auto-derived from first source;
- current earliest required-date adjustment;
- service authority for final validation.

### Work Order creation

Keep the existing request payload exactly:

- `DeliveryRequestId`
- `PlannedQty`
- `DefinitionCode`
- `PlannedStartDate`
- `PlannedCompletionDate`
- `SchedulingDirection = Forward`
- `Remark`
- `DeliveryRequestRowVersion`

Do not alter production service semantics.

`CanOfferWorkOrderCreation` requires:

- read-only presentation;
- loaded Detail;
- status Released or In Production;
- `UnplannedQty > 0.0001m`;
- Work Order Add permission;
- not submitting.

`ShowWorkOrderSection` MUST be true when either:

- `Detail.WorkOrders.Count > 0`; OR
- Work Order creation is currently eligible by status/unplanned quantity.

This keeps historical/current Work Order trace visible after remaining unplanned quantity reaches zero.

## I. `SaDeliveryRequestEntry.razor` — match `SaSo`

Wrap the page in:

```razor
<SaDocPage>
    ...
</SaDocPage>
```

### Messages

Success:

- `sdoc-toast sdoc-toast--ok`
- dismiss button.

Errors:

```razor
<SdValidationSummary
    Errors="@ValidationErrors"
    FallbackMessage="@ErrorMessage"
    OnDismiss="DismissError" />
```

### Loading

Use `sdoc-loading`.

Do not use list-page skeleton cards for document loading.

### Title bar

Use `sdoc-titlebar`.

Title:

- New: `New Delivery Request · AUTO`
- Edit Draft: `Edit Delivery Request · {DRNo}`
- Read-only presentation: `View Delivery Request · {DRNo}`

Status area:

- current status
- mode/presentation context.

KPI:

- existing DR → `Unplanned` quantity + UOM.
- New DR → `Requested` = current source allocated sum, or `—` when no sources.

### Master form

Use:

```razor
<section class="sdoc-master">
    <DxFormLayout SizeMode="SizeMode.Small"
                  CssClass="sdoc-header-form">
```

Suggested groups:

#### Document

- Request number — read-only `DxTextBox`
- Required date — `DxDateEdit`
- Status — read-only `DxTextBox` for existing DR

#### Production

- Product — read-only `DxTextBox`
- Production UOM — read-only `DxTextBox`
- Product definition — `DxTextBox`
- Warehouse — `DxTextBox`

#### Planning / Reference

- Project — `DxTextBox`
- Priority — `DxTextBox`
- Remark — `DxMemo`

Use `Enabled="@CanEdit"` for editable DR draft fields.

Do not add new lookup services in this task.

### SO demand sources

Use:

```razor
<section class="sdoc-items">
```

Header:

- `Sales Order demand sources`
- existing explanatory hint
- `DxButton` Add SO line when `CanEdit`.

Use `DxGrid Data="@SourceRows"`.

Required columns:

- SO / revision / line
- Product + description
- Demand
- UOM
- Allocated
- Required date
- row action

SO link:

- must call existing `OpenSalesOrder(source)`;
- this route opens the exact SO revision;
- the SO line number remains displayed context only because the current Sales Order route does not accept a line parameter.

Allocated quantity:

- edit → `DxSpinEdit` in `CellDisplayTemplate` using explicit quantity callback;
- view → formatted `n4`.

Remove:

- `DxButton`
- Danger
- text/icon style
- only when `CanEdit`.

Empty state MUST be clear and actionable.

### Work Order section

Use `ShowWorkOrderSection`.

Render with `sdoc-master`.

If `CanOfferWorkOrderCreation`:

- planned qty → `DxSpinEdit`
- start → `DxDateEdit`
- completion → `DxDateEdit`
- `DxButton` Create Draft Work Order.

If `Detail.WorkOrders.Count > 0`:

- always show read-only linked Work Order `DxGrid`, even if no further Work Order can be created.

Columns:

- Work Order
- Allocated
- Planned
- Good
- Status

Work Order number stays clickable through existing `OpenWorkOrder(...)`.

### Audit section

For existing DR:

- use `sdoc-master`;
- read-only `DxGrid`.

Columns:

- occurred date/time (`dd/MM/yyyy HH:mm`)
- event type
- actor
- reason

Preserve server-provided event order.

### Sticky footer

Use `sdoc-footer`.

#### Read-only presentation

Show:

- Close
- Edit when `CanEditFromView`
- Release when `CanRelease`
- Cancel Delivery Request when `CanCancel`
- Delete Draft when `CanDelete`

All controls use `DxButton`.

Release/Cancel/Delete open confirmation popup only.

#### New/Edit Draft presentation

Show:

- Cancel
- Save Draft

Save:

- `Text="Saving…"` while submitting, otherwise `Save Draft`;
- enabled only when existing local prerequisites are satisfied;
- at minimum requires editable state + at least one source + not submitting.

No lifecycle buttons in editable presentation.

### Source picker

Replace custom backdrop/panel with:

```razor
<DxPopup
    HeaderText="Select current SO demand"
    CloseOnOutsideClick="false"
    Width="980px"
    CssClass="sdoc-popup common-popup"
    ...>
```

Inside:

- `sdoc-popup-body`
- `SourcePickerError` using `sdoc-popup-error`
- `DxTextBox` for exact SO number
- `DxButton` Search
- read-only `DxGrid`
- row Add action
- `sdoc-popup-footer`
- visible Close button

Eligible source grid columns:

- SO / revision / line
- Product + description
- Standard demand
- Available
- Add action

Keep `ListEligibleSalesOrderDemandAsync()` unchanged.

### Discard popup

Use `DxPopup` + `common-popup`.

Actions MUST both be visible:

- Keep editing
- Discard

### Lifecycle confirmation popup

Use `DxPopup` + `common-popup`.

Actions MUST both be visible:

- Cancel
- context-sensitive confirm action.

### Concurrency popup

Use Sales-style:

Header:
`Reload required`

Body:
`This Delivery Request changed by another user. Reload the latest version before continuing.`

Actions:

- Close
- Reload latest

No automatic retry.

## J. `SaDeliveryRequestEntry.razor.css`

Delete obsolete page-framework styles after markup migration:

- `.dr-commandbar`
- `.dr-form-grid*`
- `.dr-picker-backdrop`
- `.dr-picker`
- old standalone KPI card system

Retain/add only DR-specific deltas:

- compact progress summary
- quantity editor width
- optional source/work-order cell stack
- any small responsive adjustment not already provided by `SaDocPage`.

Use `--sdoc-*` variables.

Do not duplicate shared document chrome.

# Transaction / Execution Order

No posting/costing transaction order changes are allowed.

## Save Draft

1. Verify local editable state.
2. Require at least one source.
3. Set submitting state.
4. Clear old validation/error state.
5. Build the existing draft/update request.
6. Call existing DR service.
7. Success:
   - clear dirty state;
   - navigate to saved View route.
8. Failure:
   - concurrency → reload popup;
   - otherwise render validation dictionary/fallback message.
9. Clear submitting state.

## Release / Cancel

1. User starts action from read-only footer.
2. Open confirmation popup.
3. User confirms.
4. Clear stale validation/error state.
5. Call existing service with current Uid + RowVersion.
6. Success:
   - apply returned detail;
   - keep page in read-only presentation;
   - show success toast.
7. Failure:
   - concurrency → reload popup;
   - otherwise show server validation/error.
8. No local status mutation before success.

## Delete

1. User starts Delete Draft from read-only footer.
2. Open confirmation popup.
3. User confirms.
4. Call `DeleteDraftAsync` with current Uid + RowVersion.
5. Success → navigate list.
6. Failure:
   - concurrency → reload popup;
   - otherwise show server error.
7. Do not remove local data optimistically.

## Create Work Order

Keep current production transaction path unchanged.

UI sequence:

1. verify current UI eligibility;
2. call existing production service with current DR RowVersion;
3. concurrency → reload popup;
4. success → existing Work Order edit navigation;
5. other failure → validation/fallback error.

# Invariants

- Same valid user input MUST produce the same persisted DR data before and after this UI refactor.
- DR source identity remains `SoNo + CustRel + SoLine`.
- All sources in one DR continue to use one ProductCode + ProductionUom.
- DR requested quantity remains derived from source allocated quantities and server-validated.
- RowVersion remains mutation concurrency authority.
- Non-Draft DRs remain non-editable.
- UI MUST NOT create an alternative DR status authority.
- UI MUST NOT create an alternative WO allocation/progress authority.
- `TotalCount` comes from the server-filtered count.
- Derived progress/count/status columns MUST NOT expose misleading server-sort behavior.
- Existing linked Work Orders remain traceable even when no further WO can be created.
- No lifecycle command runs against unsaved editable presentation.
- No database schema change is produced.

# Tests

## Focused service tests

File:

`ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestServiceTests.cs`

Add:

### 1. `Search_filters_by_required_date_range`

Assert:

- From boundary included;
- To date included for the full date;
- outside dates excluded.

### 2. `Search_pages_with_skip_take_and_stable_default_order`

Assert:

- full filtered `TotalCount`;
- page count respects `Take`;
- `Skip` returns the correct next rows;
- default ordering remains `RequiredDate DESC`, then `Uid DESC`.

### 3. `Search_sorts_supported_direct_field`

At minimum verify:

- `DeliveryRequestNo` ASC;
- `DeliveryRequestNo` DESC;
- `RequestedQty` ASC;
- `RequestedQty` DESC.

### 4. `Search_sort_field_is_case_insensitive`

Pass a differently-cased supported field name and assert correct ordering.

### 5. `Search_unknown_sort_field_falls_back_to_default`

Assert:

- no exception;
- no dynamic/unrestricted behavior;
- order is `RequiredDate DESC`, then `Uid DESC`.

Prefer direct scoped DR header seeding for Search-only tests so the tests isolate filtering/paging/sorting and do not depend on numbering/source-allocation setup.

Existing DR tests MUST remain unchanged and pass, including:

- source reservation;
- draft update lineage identity;
- release behavior;
- delete behavior;
- historical SO revision rejection.

## Required build/test commands

Run in this order:

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter FullyQualifiedName~SaDeliveryRequestServiceTests
dotnet build ErpWeb.slnx
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj
```

The repository currently has no component-render test framework. Do not add bUnit/Playwright/etc. only for this task.

## Manual UI verification

### List

- Desktop chrome/spacing matches Sales Order list.
- Mobile uses compact cards.
- Search waits ~400 ms before reload.
- Status/date filters Apply/Clear correctly.
- Closing filter popup without Apply does not change active filters.
- Filtered chip reflects search/applied filters.
- Required date initially shows descending sort indicator.
- Total count stays correct across pages.
- NEW respects Add permission.
- VIEW opens correct DR.
- EDIT opens Draft only.
- EDIT on non-Draft shows error and does not navigate.
- REFRESH actually reloads server data.
- RESET LAYOUT works.
- Derived columns cannot be sorted.
- Existing Sales Order and Inventory item lists still sort/render normally after shared grid change.

### Entry — New

- Uses Sales document shell.
- Add SO line popup is DevExpress/common-popup.
- Picker errors are visible inside popup.
- Add/remove source works.
- quantity edit works.
- required date/product/UOM auto behavior remains unchanged.
- draft field changes mark dirty.
- Cancel with dirty state shows visible Keep Editing / Discard buttons.
- Save Draft uses the same request semantics as before.

### Entry — View Draft

- title/status/progress render correctly.
- Edit visible only with Edit permission.
- Release/Cancel/Delete visibility follows existing status/permission rules.
- lifecycle actions open confirmation before server call.
- confirmation footer actions are visible.
- success refreshes detail/status.
- SO link opens exact SO revision.
- line number remains displayed context.
- Close uses safe return navigation/fallback list.

### Entry — direct Edit route for non-Draft

- renders read-only presentation;
- no Save Draft control;
- no draft fields editable;
- applicable lifecycle/WO actions follow normal read-only rules.

### Concurrency

Using stale RowVersion for Update/Lifecycle/Create-WO:

- no automatic retry;
- `Reload required` popup appears;
- Reload latest refreshes current detail;
- dirty state is reset only by explicit reload.

### Work Order trace

- Creation eligibility is unchanged.
- Create Draft Work Order payload is unchanged.
- Linked Work Orders remain visible after `UnplannedQty` reaches zero.
- Work Order navigation remains correct.

### Audit

- all service-returned events remain visible;
- event order remains unchanged.

# Implementation Order

## STEP 1 — baseline gate

Before editing:

- confirm repo `mokth/net10projectTemplate`;
- confirm branch `productionv2`;
- compare HEAD to `213cdbd1e1cf77a890d1604eaf9d2873370855bb`.

If HEAD moved and any target/reference file changed, re-diff those files before implementation.

## STEP 2 — shared grid sort flag

- add `GridColumnData.AllowSort`;
- wire to every `DxGridDataColumn` branch;
- build `ErpWeb.UI/ErpWeb.UI.csproj`.

## STEP 3 — DR list query sort contract

- add SortField/SortDescending;
- implement safe whitelist ordering;
- add focused Search tests;
- run focused tests.

## STEP 4 — DR list code-behind

Implement:

- permissions;
- custom datasource;
- columns;
- exact default sort;
- debounce;
- applied/draft filters;
- compact preview;
- NEW;
- mandatory REFRESH handler;
- VIEW/EDIT.

## STEP 5 — DR list Razor/CSS

- migrate to `iv-page` / `CommonDataGridEx`;
- add filter popup;
- add compact layout;
- remove old table/native filter controls;
- remove obsolete CSS;
- build.

## STEP 6 — DR entry code-behind

Implement:

- validation dictionary;
- read-only presentation helper;
- dirty state;
- exact cancel/close navigation;
- lifecycle confirmation state;
- concurrency reload state;
- source-picker local error;
- source quantity callback;
- Work Order section visibility helpers.

Do not alter service request semantics.

## STEP 7 — DR entry Razor/CSS

- migrate to `SaDocPage`;
- migrate master fields to DevExpress;
- source rows to `DxGrid`;
- WO trace to `DxGrid`;
- audit to `DxGrid`;
- picker to `DxPopup`;
- lifecycle/discard/concurrency dialogs to `DxPopup`;
- footer to `sdoc-footer`;
- remove obsolete DR page framework CSS.

## STEP 8 — focused verification

- focused DR tests;
- solution build;
- manual List/New/View/Edit/lifecycle/concurrency/WO trace verification.

## STEP 9 — full regression

- full test project;
- visually recheck `SaSoList`;
- visually recheck another `CommonDataGridEx` custom-source consumer such as `IvStockMasterList`;
- confirm no migration/schema artifact exists.

# Regression Areas

Must verify:

- `SaSoList`
- `IvStockMasterList`
- other `GridColumnData` / `CommonDataGridEx` consumers
- Delivery Request search
- status/date filters
- paging
- sorting
- compact/mobile layout
- Add/Edit/View navigation
- DR draft source allocation
- DR lifecycle
- RowVersion concurrency behavior
- DR→WO creation/navigation
- SO→DR navigation
- DR→SO revision navigation
- audit display
- responsive document layout

Must remain unchanged:

- Sales Order calculations
- SO revisions
- DR persistence
- DR numbering
- DR lifecycle service rules
- Production Work Order rules
- Inventory
- costing
- posting
- rollback
- month-end
- accounting
- database schema

# Do-Not Rules

DO NOT:

- rewrite DR service architecture for a UI task;
- change database schema;
- change SO→DR lineage;
- change DR→WO allocation;
- change numbering;
- change status derivation;
- bypass permissions;
- bypass RowVersion;
- auto-retry concurrency;
- mutate status optimistically;
- call lifecycle actions from unsaved editable presentation;
- hide linked WOs solely because unplanned qty is zero;
- load all DRs client-side to fake paging/sorting;
- sort a derived field within only the current page;
- add unrestricted dynamic sorting;
- add batch lifecycle actions;
- duplicate shared `iv-*` / `sdoc-*` CSS;
- create a second generic grid component;
- copy irrelevant Sales Order domain logic into DR;
- modify costing/inventory/posting/rollback/month-end/accounting;
- introduce unrelated refactoring;
- add a UI test package only for this task.

# Acceptance Criteria

- [ ] Current branch/HEAD is rechecked before coding.
- [ ] Delivery Request List follows the current Sales Order List standard.
- [ ] Delivery Request Entry follows the current Sales Order document standard.
- [ ] List uses `CommonDataGridEx`.
- [ ] List uses real server paging.
- [ ] Search is debounced.
- [ ] Filter popup uses applied/draft state.
- [ ] Required date has correct initial descending sort indicator.
- [ ] Supported direct fields sort server-side.
- [ ] Derived progress/status/count fields are not user-sortable.
- [ ] Shared `AllowSort` default remains backward-compatible.
- [ ] REFRESH actually reloads the external DR datasource.
- [ ] Desktop and compact/mobile list modes work.
- [ ] Hero KPI uses server TotalCount, not partial Unplanned sum.
- [ ] Entry uses `SaDocPage`.
- [ ] Entry uses `DxFormLayout`, `DxGrid`, `DxButton`, and `DxPopup`.
- [ ] `SdValidationSummary` receives authoritative validation dictionaries.
- [ ] Dirty discard confirmation works.
- [ ] Cancel/discard navigation is deterministic.
- [ ] Non-Draft `/edit/{uid}` renders read-only presentation.
- [ ] Lifecycle commands are confirmation-gated.
- [ ] Lifecycle commands are absent from editable presentation.
- [ ] Concurrency failures show Reload Required and never auto-retry.
- [ ] Reload latest refreshes RowVersion/detail safely.
- [ ] Source picker errors are visible inside the popup.
- [ ] SO source identity and validation semantics are unchanged.
- [ ] DR→WO creation payload/authority is unchanged.
- [ ] Linked Work Orders remain visible when no unplanned qty remains.
- [ ] SO→DR→WO traceability remains intact.
- [ ] Existing DR tests pass.
- [ ] New list Search tests pass.
- [ ] `dotnet build ErpWeb.slnx` succeeds.
- [ ] Full test project succeeds in the available environment.
- [ ] No database migration/schema file is created.
- [ ] No Inventory/Costing/Posting/Rollback/Month-End/Accounting logic is changed.
- [ ] Sales Order list and another shared-grid consumer show no regression.

# Approval

## Scorecard

| Category | Score |
|---|---:|
| Repository correctness | 10/10 |
| Architecture compatibility | 10/10 |
| Data/schema correctness | 10/10 |
| Transaction integrity | 10/10 |
| Rollback/reversal safety | 10/10 |
| Concurrency safety | 10/10 |
| Costing integrity | 10/10 |
| Regression safety | 10/10 |
| Test completeness | 10/10 |
| Code-Agent implementability | 10/10 |

**FINAL STATUS: APPROVED FOR IMPLEMENTATION — 10/10**

This approval applies to the verified `productionv2` HEAD above. If the branch moves before implementation, the Code Agent MUST re-verify changed target/reference files before executing this plan.
