# Delivery Request UI Standardization — Code Agent Plan

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Verified baseline:** commit `213cdbd1e1cf77a890d1604eaf9d2873370855bb` (`Implement Sales Order delivery request traceability`)  
**Reference UI:** `SaSoList` + `SaSo`  
**Target UI:** `SaDeliveryRequestList` + `SaDeliveryRequestEntry`

# Objective

Standardize the new Delivery Request list and entry pages so they follow the current Sales Order UI/UX conventions already implemented in `productionv2`.

The implementation MUST reuse the existing shared Sales UI chrome and components rather than create another DR-specific design system. Delivery Request business rules, SO→DR traceability, Work Order creation logic, lifecycle rules, permissions, persistence, posting/costing behavior, and database schema MUST remain unchanged.

# Confirmed Current Problems

## 1. Delivery Request list does not use the Sales list standard

**Current files**
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor.css`

**Verified current behavior**
- Uses native `<input>`, `<select>`, `<button>`, and a hand-built HTML `<table>`.
- Search executes `LoadAsync()` on every `@oninput` keystroke with no debounce.
- Status selection does not apply until another load/refresh occurs.
- Uses a custom `dr-toolbar`, `dr-table`, `dr-status`, and `dr-link` presentation.
- Does not use `CommonDataGridEx`.
- Does not provide the Sales Order desktop-grid/mobile-compact split.
- Does not use the standard filter popup pattern.
- Does not expose standard row VIEW/EDIT actions.
- `TotalUnplanned` is calculated only from the currently loaded `Rows`; the service call is capped by `Take`, so it is not a reliable all-record total when `TotalCount` exceeds the loaded page.

**Required correction**
- Rebuild the list presentation using the `SaSoList` pattern: inventory chrome, DevExpress search/filter controls, `CommonDataGridEx`, compact mobile cards, standard toolbar/actions, and filter popup.
- Replace the current misleading list-level unplanned KPI with a count KPI unless a true server aggregate is explicitly added later.

## 2. Delivery Request entry does not use the Sales document standard

**Current files**
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor.css`

**Verified current behavior**
- Uses `<div class="iv-page dr-page">` instead of the Sales document wrapper `SaDocPage`.
- Uses the list-style `iv-hero` rather than the Sales document `sdoc-titlebar`.
- Uses a top `dr-commandbar` instead of the standard sticky `sdoc-footer` action area.
- Uses native Blazor/HTML editors (`InputText`, `InputDate`, `InputNumber`, `<textarea>`) instead of the DevExpress form layout/editor pattern used by Sales Order.
- Uses hand-built HTML tables for SO source rows, Work Orders, and audit events.
- Uses a custom fixed-position `dr-picker-backdrop`/`dr-picker` modal instead of `DxPopup` + `common-popup`.
- Success/error messages are not closable in the same way as Sales Order.
- Server validation dictionaries are flattened into a single string instead of using the existing `SdValidationSummary` surface.
- There is no dirty-state/discard confirmation when leaving New/Edit.
- Lifecycle actions are mixed with edit/save actions in the top command bar.

**Required correction**
- Rebuild presentation around `SaDocPage`, `sdoc-titlebar`, `sdoc-master`, `sdoc-items`, `sdoc-footer`, `DxFormLayout`, `DxGrid`, `DxButton`, and `DxPopup`.
- Preserve all existing DR service calls and server-side rules.

## 3. Delivery Request list paging is service-ready, but grid sorting support is incomplete

**Current files**
- `ErpWeb.Core/Sales/ISaDeliveryRequestService.cs`
- `ErpWeb.Core/Sales/SaDeliveryRequestService.cs`

**Verified current behavior**
- `SaDeliveryRequestListQuery` already supports `SearchText`, `Status`, `ProductCode`, `RequiredDateFrom`, `RequiredDateTo`, `Skip`, and `Take`.
- `SearchAsync()` already performs server-side filtering, counting, paging, and stable default ordering by `RequiredDate DESC`, then `Uid DESC`.
- It does not currently accept a sort field/direction.
- Some displayed list values (`WoAllocatedQty`, `UnplannedQty`, `ProducedQty`, derived `Status`, `SourceCount`, `WorkOrderCount`) are calculated after/around additional allocation queries and MUST NOT pretend to support server sorting unless that sorting is implemented correctly.

**Required correction**
- Add safe server sorting only for fields whose displayed value comes directly from the Delivery Request header query.
- Mark derived/progress columns non-sortable in `CommonDataGridEx`.

# Scope

Included:
- Delivery Request list visual/layout standardization.
- Delivery Request entry visual/layout standardization.
- Standard search debounce and filter popup on the list.
- Standard `CommonDataGridEx` server-paged list.
- Standard compact/mobile list cards.
- Standard view/edit navigation actions.
- Standard document title/master/items/footer layout for entry.
- DevExpress editors/grids/popups for DR entry.
- Dirty-state/discard confirmation for New/Edit.
- Confirmation UI for DR lifecycle actions.
- Reuse of `SdValidationSummary`.
- Minimal list-query sorting support required by the shared server grid.
- Minimal backward-compatible `GridColumnData.AllowSort` support so derived DR columns can be explicitly non-sortable.
- Focused tests for list filtering/paging/sorting changes.

# Non-Goals

MUST NOT change:
- SO→DR traceability model.
- DR→WO allocation model.
- `SaDeliveryRequest`, `SaDeliveryRequestSource`, or audit entity schema.
- DR numbering behavior.
- `CreateDraftAsync`, `UpdateDraftAsync`, `ReleaseAsync`, `CancelAsync`, or `DeleteDraftAsync` business rules.
- Work Order creation semantics in `CreateDraftFromDeliveryRequestAsync`.
- Production snapshot/scheduling logic.
- Inventory, costing, posting, rollback, month-end, or accounting logic.
- Permission codes or menu codes.
- Sales Order UI files merely to make DR match them.
- `SaDocPage.razor.css` or `inventory-chrome.css` unless a verified shared defect is discovered during implementation.
- Product Definition/Warehouse/Project/Priority lookup behavior; this task is UI standardization, not lookup redesign.
- A new UI test framework/package.

# Files to Change

## UI — Delivery Request list
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor.css`

## UI — Delivery Request entry
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor.css`

## Shared grid — minimal backward-compatible enhancement
- `ErpWeb.UI/Components/Common/DataGrid/DataGridModel.cs`
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor`

## Core — list sort contract only
- `ErpWeb.Core/Sales/ISaDeliveryRequestService.cs`
- `ErpWeb.Core/Sales/SaDeliveryRequestService.cs`

## Tests
- `ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestServiceTests.cs`

## Reference-only — DO NOT MODIFY
- `ErpWeb.UI/Sales/Transactions/SaSoList.razor`
- `ErpWeb.UI/Sales/Transactions/SaSoList.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaSoList.razor.css`
- `ErpWeb.UI/Sales/Transactions/SaSo.razor`
- `ErpWeb.UI/Sales/Transactions/SaSo.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaSo.razor.css`
- `ErpWeb.UI/Sales/Transactions/SaDocPage.razor`
- `ErpWeb.UI/Sales/Transactions/SaDocPage.razor.css`
- `ErpWeb.UI/Sales/Transactions/SdValidationSummary.razor`

# Database Changes

**None.**

Do not create migrations, SQL scripts, columns, indexes, tables, views, triggers, or data backfills for this task.

# Exact Code Changes

## A. `DataGridModel.cs` — add explicit non-sortable column support

Add a backward-compatible property to `GridColumnData`:

- `bool? AllowSort { get; set; }`
- Default MUST remain `null` so all existing grids inherit DevExpress/default sorting behavior unchanged.

Do not change existing property defaults.

## B. `CommonDataGridEx.razor` — honor `GridColumnData.AllowSort`

For every `DxGridDataColumn` rendering branch in `RenderColumn(...)`:

- Pass `AllowSort="@col.AllowSort"`.
- Do not alter filtering, grouping, layout persistence, toolbar behavior, action buttons, selection, export, or existing column rendering.

Purpose:
- DR direct header columns can remain sortable.
- DR derived progress/status/count columns can be marked `AllowSort = false` and MUST NOT show a misleading sort interaction.

## C. `ISaDeliveryRequestService.cs` — list sort contract

Extend `SaDeliveryRequestListQuery` with:

- `string? SortField { get; set; }`
- `bool SortDescending { get; set; } = true`

Do not remove or rename existing query fields.

## D. `SaDeliveryRequestService.SearchAsync()` — safe direct-field sorting

Keep all existing authorization, tenant/branch filtering, status derivation/filtering, product filtering, required-date filtering, count logic, allocation calculations, and paging limits.

Before `Skip/Take`, replace the fixed ordering with a whitelist switch for these direct header fields only:

- `DeliveryRequestNo`
- `ProductCode`
- `ProductionUom`
- `RequestedQty`
- `RequiredDate`
- `CreatedDate`
- `CreatedBy`

Rules:
- Field matching MUST be case-insensitive.
- Unknown/null sort field MUST fall back to current ordering: `RequiredDate DESC`, then `Uid DESC`.
- Supported sort MUST honor `SortDescending`.
- Always add `Uid` as deterministic tie-breaker in the same stable direction appropriate to the implementation.
- MUST NOT dynamically build SQL from raw field names.
- MUST NOT claim server sorting for `WoAllocatedQty`, `UnplannedQty`, `ProducedQty`, derived `Status`, `SourceCount`, or `WorkOrderCount` in this task.

## E. `SaDeliveryRequestList.razor.cs` — follow `SaSoList` state model

Refactor list page state to the Sales Order list pattern.

### Required fields/state

Add/maintain:
- `DxGrid? _grid`
- debounce timer + search version guard matching the Sales Order approach
- `IsBootstrapping`
- `SearchText`
- `TotalCount`
- `CompactRows`
- `CanAdd`
- `CanEdit`
- filter popup state
- applied vs draft filter values
- `SaDeliveryRequestGridDataSource DataSource`
- `Columns`
- `Buttons`
- `ActionButtons`

### Filter model

Use:
- Status
- Required date from
- Required date to

The service already supports these fields.

`HasActiveFilters` MUST include search text plus applied popup filters.

### Search

Implement `OnSearchTextChanged(string text)` using the same 400 ms debounce/version-guard pattern as `SaSoList`.

MUST NOT call the database on every keypress.

### Grid datasource

Create `SaDeliveryRequestGridDataSource : GridCustomDataSource` in this code-behind, modeled on `SaSoGridDataSource`.

It MUST:
- carry the current `SaDeliveryRequestListQuery` filters;
- use `Skip/Take` from `GridCustomDataSourceItemsOptions`;
- pass supported `SortField` and `SortDescending` from the first grid sort item;
- return the service `TotalCount` from `GetItemCountAsync`;
- use `Take` within the existing service cap;
- never perform client-side paging over an incomplete server page.

### Columns

Use `GridColumnData` for at least:
- DR No. → `DeliveryRequestNo` — sortable
- Product → `ProductCode` — sortable
- Description → `ProductDescription` — non-sortable unless service support is explicitly added
- UOM → `ProductionUom` — sortable
- Required → `RequestedQty` — sortable, `n4`
- WO allocated → `WoAllocatedQty` — **AllowSort=false**
- Unplanned → `UnplannedQty` — **AllowSort=false**
- Produced → `ProducedQty` — **AllowSort=false**
- Required date → `RequiredDate` — sortable, `dd/MM/yyyy`
- Status → `Status` — **AllowSort=false** because displayed status can be derived from production progress
- SO sources → `SourceCount` — **AllowSort=false**
- Work Orders → `WorkOrderCount` — **AllowSort=false**
- Created by → `CreatedBy` — sortable
- Created date → `CreatedDate` — sortable

Do not invent audit fields not present in `SaDeliveryRequestListRow`.

### Toolbar and row actions

Toolbar buttons:
- `NEW` only, enabled by `CanAdd`.
- Use the `CommonDataGridEx` built-in `REFRESH` and `RESET LAYOUT` actions.

Row actions:
- `VIEW` → `/sales/delivery-requests/view/{uid}`
- `EDIT` → `/sales/delivery-requests/edit/{uid}` only when user has Edit permission and row status is Draft.
- For non-Draft rows, clicking EDIT MUST show a clear error and MUST NOT navigate.

Do not add list-level delete/release/cancel batch functionality in this task.

### Compact preview

Follow `SaSoList`:
- Fetch a bounded first-page preview for mobile cards.
- Card shows DR No., product, required date, and status.
- Card click opens View.
- Use standard `iv-status` classes:
  - Draft → `is-hold`
  - Cancelled → `is-off`
  - Released / In Production / Completed → `is-on`

### Count/KPI

- `TotalCountLabel` uses server `TotalCount`.
- Hero KPI MUST show Delivery Request count, matching Sales Order list behavior.
- Remove `TotalUnplanned` from the hero; do not label a page/subset sum as a system-wide total.

### Disposal

Implement `IDisposable` and dispose the debounce timer exactly as done in `SaSoList`.

## F. `SaDeliveryRequestList.razor` — match Sales Order list composition

Rebuild markup to this structure:

1. `<div class="iv-page">`
2. closable success toast
3. closable error toast
4. `iv-hero`
   - Sales eyebrow
   - `Delivery Requests` title
   - total count chip
   - `Filtered` chip when active
   - right-side count KPI
5. `iv-card`
6. `iv-toolbar-row`
   - `DxTextBox` search
   - `DxButton` FILTER
7. skeleton while bootstrapping
8. desktop `CommonDataGridEx`
9. compact/mobile card list
10. `DxPopup` filter dialog using `common-popup`

`CommonDataGridEx` configuration SHOULD mirror `SaSoList`:
- `ShowToolbarText="true"`
- `ShowResetLayoutButton="true"`
- `UseBuiltInExport="false"`
- `ShowSearchBox="false"`
- `ShowFilterRow="false"`
- `ShowGroupPanel="false"`
- `GridKey="sa-delivery-request-list"`
- selection disabled because this plan adds no batch operations

Remove native list `<table>`, native filter `<select>`, native search `<input>`, and DR-specific toolbar markup.

## G. `SaDeliveryRequestList.razor.css` — keep only page-specific deltas

Match `SaSoList.razor.css` philosophy:
- shared chrome comes from `/css/inventory-chrome.css`;
- retain only `CommonDataGridEx` minimum-height/border tweaks if needed;
- remove obsolete `.dr-toolbar`, `.dr-status-filter`, `.dr-table__row`, `.dr-link`, and `.dr-status*` styles after Razor no longer references them.

Do not duplicate `iv-*` definitions locally.

## H. `SaDeliveryRequestEntry.razor.cs` — standard document interaction state

Keep the existing injected services and all service-call payloads.

### Add standard page state

Add:
- `Dictionary<string,string>` or compatible read-only backing state for `ValidationErrors`
- `_isDirty`
- `ConfirmDiscardVisible`
- lifecycle-confirm popup state (`ConfirmLifecycleVisible`, action key/message/button text/style)
- `IsView` helper
- `CanEditFromView` helper: Edit permission + existing DR + Draft + view mode + not submitting

### Validation handling

For `CreateDraftAsync`, `UpdateDraftAsync`, lifecycle calls, and Work Order creation:
- preserve `result.ValidationErrors` instead of flattening them immediately;
- render them through `SdValidationSummary`;
- use `ErrorMessage` only as fallback/non-field error text;
- clear validation state before new server operations.

### Dirty tracking

Mark `_isDirty = true` for editable user changes to:
- Required date
- Definition code
- Warehouse code
- Project code
- Priority
- Remark
- SO source add/remove
- SO allocated quantity edits

After successful load/save/apply-detail:
- `_isDirty = false`.

### Edit navigation

Add:
- `OnClose()` → return to Delivery Request list.
- `OnEditFromView()` → edit route for current `Uid`.
- `OnCancelEditAsync()` → if dirty, show discard popup; otherwise return to list/view as appropriate.
- `ConfirmDiscardAsync()` → clear dirty state and navigate away.

Do not change server editability rules.

### Lifecycle UI safety

Keep existing server methods:
- `ReleaseAsync()`
- `CancelAsync()`
- `DeleteAsync()`

But invoke them from a confirmation popup in view presentation.

MUST NOT execute Release/Cancel/Delete simply because a top raw HTML button was clicked.

Use explicit wording:
- Release: `Release Delivery Request`
- Cancel lifecycle: `Cancel Delivery Request`
- Delete: `Delete Draft`

Edit/New presentation MUST show edit actions (`Cancel`, `Save Draft`) rather than lifecycle actions. This prevents a user from executing a lifecycle command while unsaved fields are visible. Lifecycle actions remain available after returning to View, subject to the same existing permission/status properties.

### Source quantity callback

Replace direct inline `@bind-Value` where necessary with a callback such as `OnSourceQuantityChanged(SourceEditorRow row, decimal value)` so dirty state is always updated.

Do not change quantity validation authority: the service remains authoritative.

## I. `SaDeliveryRequestEntry.razor` — match Sales Order document composition

Wrap content in:

`<SaDocPage> ... </SaDocPage>`

### Top messages

Use:
- `sdoc-toast sdoc-toast--ok` with dismiss button for success
- `<SdValidationSummary Errors="@ValidationErrors" FallbackMessage="@ErrorMessage" OnDismiss="DismissError" />`

### Loading

Use `sdoc-loading`, not an `iv-card` skeleton intended for list pages.

### Title bar

Use `sdoc-titlebar`:
- title: `@PageHeading · @(Detail?.DeliveryRequestNo ?? "AUTO")`
- status surface: current DR status + mode context
- KPI: `Unplanned` and UOM when existing; for New show requested/source quantity context or `—`

### Progress summary

Retain the useful four values:
- Requested
- WO allocated
- Unplanned
- Produced

Render as a compact DR-specific summary inside `sdoc-master` using the existing `sdoc` design tokens. Do not retain the old large independent card system.

### Header/master form

Use `<section class="sdoc-master">` + `<DxFormLayout SizeMode="SizeMode.Small" CssClass="sdoc-header-form">`.

Suggested groups:

**Document**
- Request number — `DxTextBox`, read-only
- Required date — `DxDateEdit`, enabled by `CanEdit`
- Status — read-only when existing

**Production**
- Product — read-only `DxTextBox`
- Production UOM — read-only `DxTextBox`
- Product definition — `DxTextBox`
- Warehouse — `DxTextBox`

**Planning / reference**
- Project — `DxTextBox`
- Priority — `DxTextBox`
- Remark — `DxMemo`

Do not introduce new lookup services/components in this task.

### SO demand source section

Use `<section class="sdoc-items">`.

Header:
- title `Sales Order demand sources`
- existing explanatory hint
- DevExpress `Add SO line` button when `CanEdit`

Use `DxGrid Data="@SourceRows"` instead of HTML `<table>`.

Required columns:
- Sales Order/revision/line
- Product + description
- Demand
- UOM
- Allocated
- Required date
- row action

Rules:
- SO number remains clickable to existing `OpenSalesOrder(...)` navigation.
- In edit mode, allocated quantity uses `DxSpinEdit`.
- In view mode, allocated quantity is formatted read-only.
- Remove action is a text/icon `DxButton` using Danger style.
- Preserve existing exact-source identity: SoNo + CustRel + SoLine.

### Work Order section

Preserve the existing visibility rule:
- existing DR;
- status Released or In Production;
- `UnplannedQty > 0.0001m`.

Use `sdoc-master` and DevExpress controls:
- `DxSpinEdit` planned quantity
- `DxDateEdit` start
- `DxDateEdit` completion
- `DxButton` Create Draft Work Order
- read-only `DxGrid` for linked Work Orders

Keep `CreateWorkOrderAsync()` payload and navigation unchanged.

### Audit section

Use `sdoc-master` and a read-only `DxGrid` for:
- occurred date/time
- event type
- actor
- reason

Keep existing event ordering from the service.

### Sticky footer

Use `sdoc-footer`.

**View mode**
- Close
- Edit when `CanEditFromView`
- Release when `CanRelease`
- Cancel Delivery Request when `CanCancel`
- Delete Draft when `CanDelete`

**New/Edit mode**
- Cancel
- Save Draft

Use `DxButton`; no raw Bootstrap command buttons.

### Source picker

Replace `dr-picker-backdrop`/`dr-picker` with:

`<DxPopup HeaderText="Select current SO demand" CloseOnOutsideClick="false" Width="980px" CssClass="sdoc-popup common-popup" ...>`

Inside:
- `sdoc-popup-body`
- `DxTextBox` for exact SO search
- `DxButton` Search
- `DxGrid` for eligible sources
- Add action button per eligible source
- `sdoc-popup-footer` with Close

Keep `ListEligibleSalesOrderDemandAsync()` and `AddEligibleSource(...)` rules unchanged.

### Confirmation popups

Use `DxPopup` + `common-popup` for:
- discard unsaved changes
- lifecycle confirmation

Every popup MUST render visible action buttons in its `<Content>` footer.

## J. `SaDeliveryRequestEntry.razor.css` — remove duplicate page framework

Delete styles made obsolete by the standard shell:
- `.dr-commandbar`
- `.dr-form-grid*`
- `.dr-picker-backdrop`
- `.dr-picker`
- old standalone KPI card styling where replaced

Retain/add only DR-specific deltas such as:
- compact progress-summary layout
- source quantity editor width
- optional DR-specific grid/cell stacking

Use `--sdoc-*` variables from `SaDocPage.razor.css` rather than hard-coded duplicate page colors/borders/radii where possible.

# Transaction / Execution Order

No posting or costing transaction order changes are allowed.

For UI commands, preserve this execution sequence:

## Save Draft
1. Validate local UI prerequisites already enforced by the page (`CanEdit`, at least one source).
2. Set submitting state and clear previous error/validation state.
3. Build the existing `SaDeliveryRequestDraftRequest` / `SaDeliveryRequestUpdateRequest`.
4. Call the existing service method.
5. On success, clear dirty state and navigate to View of the saved DR.
6. On failure, display the returned validation dictionary/fallback error.
7. Clear submitting state.

## Release / Cancel / Delete
1. User initiates action from View footer.
2. Show confirmation popup.
3. User confirms.
4. Execute the existing service method with current `Uid` + `RowVersion`.
5. On success, apply returned detail or navigate to list for delete.
6. On failure, show authoritative server error/validation.
7. No client-side status mutation before service success.

## Create Work Order
Keep the current service call and atomic server behavior unchanged.

# Authority Rules

- DR status authority remains `SaDeliveryRequestService`.
- SO source eligibility/available quantity authority remains `ListEligibleSalesOrderDemandAsync()` and save-time service validation.
- DR requested quantity remains derived from `SourceRows.Sum(x => x.Quantity)` in the existing draft request flow and revalidated server-side.
- WO allocated / unplanned / produced quantities remain service-derived display values.
- UI MUST NOT calculate or persist a competing production-progress authority.
- `RowVersion` remains the concurrency authority for mutation calls.

# Invariants

- UI standardization MUST NOT change the persisted DR data for the same valid user input.
- A DR source remains uniquely identified by SO number + SO revision + SO line.
- All source rows in one DR must continue to use the same ProductCode and ProductionUom as enforced today.
- Non-Draft DRs remain non-editable through the existing service/page rules.
- Release/Cancel/Delete permissions remain server/page permission-gated exactly as today.
- Work Order creation remains available only under the existing DR status/unplanned-quantity rule.
- List `TotalCount` must represent the server-filtered total, not the number of rows currently rendered.
- Derived grid columns MUST NOT expose misleading server-sort behavior.

# Rollback / Reversal

No ERP rollback/reversal logic is changed.

UI failure behavior:
- Failed Save/Release/Cancel/Delete/Create-WO MUST leave the current page data intact except for displayed errors.
- No optimistic local status change before server success.
- Delete navigation occurs only after `DeleteDraftAsync()` succeeds.
- Discard confirmation affects only unsaved browser state; it MUST NOT call a server rollback API.

# Concurrency / Locking

Do not change existing service transactions, row locks, retries, or RowVersion checks.

The UI MUST continue sending the current `Detail.RowVersion` for:
- Update Draft
- Release
- Cancel
- Delete
- Create Work Order from DR

Do not add client-side retry loops.

# Tests

## Service tests — `SaDeliveryRequestServiceTests.cs`

Add focused tests:

1. `Search_filters_by_required_date_range`
   - create records on multiple required dates;
   - assert From/To bounds return only matching DRs.

2. `Search_pages_with_skip_take_and_stable_default_order`
   - create enough records for multiple pages;
   - assert `TotalCount` is full filtered count;
   - assert page rows respect `Skip/Take`;
   - assert default order remains RequiredDate DESC then Uid DESC.

3. `Search_sorts_supported_direct_field`
   - verify at least `DeliveryRequestNo` ASC/DESC and `RequestedQty` ASC/DESC.

4. `Search_unknown_sort_field_falls_back_to_default_order`
   - pass an unsupported value;
   - assert no exception/dynamic query behavior;
   - assert default stable ordering.

5. Existing Delivery Request tests MUST continue to pass unchanged, especially:
   - source reservation
   - draft update lineage identity
   - release behavior
   - delete behavior
   - historical SO revision rejection

## UI/build verification

The current test project does not contain a component-render test stack and explicitly requires a solution build after Razor changes.

Required commands:

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter FullyQualifiedName~SaDeliveryRequestServiceTests
dotnet build ErpWeb.slnx
```

Do not add bUnit/Playwright/NUnit/etc. solely for this task.

## Manual verification matrix

### List
- Desktop grid matches Sales Order list chrome/spacing/toolbars.
- Mobile width uses compact cards, not a crushed HTML table.
- Search waits for debounce and then refreshes.
- Filter popup Apply/Clear works.
- `Filtered` chip reflects active search/filter state.
- Total count remains correct across paging.
- NEW respects Add permission.
- VIEW opens the selected DR.
- EDIT opens Draft DR only and respects Edit permission.
- Non-Draft EDIT is blocked with a clear message.
- Refresh reloads data.
- Reset Layout works.
- Derived progress/status/count columns cannot be user-sorted.

### Entry — New
- Uses Sales document shell.
- Add SO line popup works.
- Add/remove source works.
- Quantity edit works.
- Dirty state appears after edit.
- Cancel with dirty state shows discard popup with visible buttons.
- Save Draft creates exactly the same service request semantics as before.

### Entry — View
- DR number/status/progress render correctly.
- SO links open correct revision/line context route already used by page.
- Linked Work Orders open correctly.
- Edit appears only for editable Draft.
- Release/Cancel/Delete visibility follows existing permission/status rules.
- Lifecycle confirmation popup has visible Cancel/Confirm actions.
- Successful lifecycle action refreshes detail/status.

### Entry — Edit
- Existing data loads unchanged.
- Editable fields use DevExpress editors.
- source quantity changes mark dirty.
- Save persists using existing update service.
- lifecycle buttons are not mixed into unsaved edit mode.

### Work Order / audit
- Create Draft Work Order still calls the existing production service.
- Linked Work Order grid values remain unchanged.
- Audit events remain visible and in service-provided order.

# Implementation Order

**STEP 1 — baseline**
- Re-read the reference Sales Order list/entry files and target DR files at the current `productionv2` HEAD.
- If HEAD has moved, reconcile the plan against the changed files before editing.

**STEP 2 — shared grid capability**
- Add `GridColumnData.AllowSort`.
- Wire it to every `DxGridDataColumn` branch in `CommonDataGridEx.razor`.
- Build `ErpWeb.UI` immediately to catch DevExpress/Razor parameter errors.

**STEP 3 — DR list query sorting**
- Add `SortField` / `SortDescending` to `SaDeliveryRequestListQuery`.
- Implement whitelist sorting in `SaDeliveryRequestService.SearchAsync()`.
- Add focused service tests and run them.

**STEP 4 — DR list code-behind**
- Introduce custom grid datasource, debounce, filter state, columns, toolbar/actions, compact preview, permissions, reload flow.

**STEP 5 — DR list Razor/CSS**
- Replace native toolbar/table with the `SaSoList` composition.
- Remove obsolete DR list CSS.
- Build.

**STEP 6 — DR entry code-behind**
- Add validation dictionary, dirty/discard state, view/edit navigation, lifecycle confirmation state, and source-quantity change callback.
- Keep service payload construction and business calls intact.

**STEP 7 — DR entry Razor/CSS**
- Convert to `SaDocPage` + `sdoc-*` structure.
- Convert editors to DevExpress form controls.
- Convert source/work-order/audit tables to `DxGrid`.
- Convert source picker and confirmations to `DxPopup`.
- Move document actions to standard sticky footer.
- Remove obsolete custom page/modal CSS.

**STEP 8 — focused verification**
- Run Delivery Request service tests.
- Exercise list/New/View/Edit/lifecycle/source-picker/Create-WO flows manually.

**STEP 9 — full regression**
- `dotnet build ErpWeb.slnx`
- Run the full test suite if practical for the agent environment.
- Recheck Sales Order list/entry visually to ensure shared grid changes caused no regression.

# Regression Areas

Must verify:
- Sales Order list (`CommonDataGridEx` consumer).
- Other lists using `GridColumnData` / `CommonDataGridEx`.
- Delivery Request search/filter/paging.
- Delivery Request Add/Edit/View navigation.
- DR source allocation editing.
- DR lifecycle commands.
- DR→WO creation/navigation.
- SO→DR navigation from Sales Order production-demand trace.
- DR→SO navigation from source rows.
- responsive/mobile layout.

Must remain unchanged:
- SO calculations and Sales Order document behavior.
- Delivery Request persistence rules.
- Production/costing logic.
- database schema.

# Do-Not Rules

DO NOT:
- rewrite the DR service architecture for a UI task;
- change DB schema;
- duplicate `iv-*` or `sdoc-*` shared styles inside DR CSS;
- copy/paste the entire Sales Order page and leave irrelevant SO fields/logic;
- introduce a second generic grid component;
- use a client-side full-data load to fake server paging;
- sort derived progress fields incorrectly within only the current page;
- change status derivation;
- bypass permissions;
- bypass RowVersion concurrency;
- call lifecycle actions against unsaved edit state;
- add list-level batch delete/release/cancel in this scope;
- modify costing, inventory, posting, rollback, month-end, or accounting code;
- introduce unrelated refactoring.

# Acceptance Criteria

- [ ] Delivery Request List visually follows the current Sales Order List standard.
- [ ] Delivery Request Entry visually follows the current Sales Order Entry/document standard.
- [ ] List uses `CommonDataGridEx` and server paging.
- [ ] Search uses debounce rather than one request per keystroke.
- [ ] Filter popup supports status and required-date range.
- [ ] Desktop and compact/mobile list presentations both work.
- [ ] List toolbar/action icons and text follow the Sales Order conventions.
- [ ] Derived DR progress/status/count columns do not expose invalid sort behavior.
- [ ] Entry uses `SaDocPage`, `DxFormLayout`, `DxGrid`, `DxButton`, and `DxPopup`.
- [ ] Entry success/error/validation surfaces match Sales Order behavior.
- [ ] New/Edit dirty-state discard confirmation works.
- [ ] Lifecycle confirmations contain visible action buttons.
- [ ] Existing permissions/status guards remain effective.
- [ ] Existing SO→DR→WO traceability remains unchanged.
- [ ] Existing DR service tests pass.
- [ ] New list paging/filter/sort tests pass.
- [ ] `dotnet build ErpWeb.slnx` succeeds.
- [ ] No database migration/schema change is produced.
- [ ] No Inventory/Costing/Posting/Rollback logic is changed.
- [ ] Shared `CommonDataGridEx` consumers show no regression from nullable `AllowSort` support.

# Approval Status

**APPROVED FOR IMPLEMENTATION**
