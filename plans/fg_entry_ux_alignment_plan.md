# Finished Good Receipt Entry UX Alignment Plan

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Verified current commit:** `a0108adea9d6d6042586f39f8e41eea70b88d49d` (`checked in Daily Production Enhancement`)  
**Parent / prior review baseline:** `8133a34875d08ce6d48c1a53c4ec86e63320f780` (`checked in FG fix`)  
**Target:** Finished Good Receipt entry UX alignment with Daily/IP transaction pattern  
**Status:** **APPROVED IMPLEMENTATION PLAN — review findings incorporated**

---

## 1. Objective

Reshape **Finished Good Receipt (FG)** so operators experience the same transaction-entry pattern already used by **Daily Production** and **Issue to Production / Material Issue**, while preserving FG-specific business behavior.

The target interaction is:

> **Select source → enter workspace → edit receipt lines in a grid → save draft → post**

The current FG page already shares the Inventory/Production shell (`iv-hero`, command bar, chips, cards), but it diverges after that shell because:

- the source finder remains stacked above the receipt during editing;
- receipt lines are card-per-line forms rather than a `DxGrid`;
- all source filters are permanently visible;
- Readiness is a full standalone card;
- view mode also renders line cards instead of the same grid language used elsewhere.

This plan aligns the UI **without changing FG accounting, costing, stock movement, posting, rollback, correction, or database semantics**.

---

## 2. Repository Evidence and Design Constraints

### 2.0 Current `production` HEAD verification

This plan was re-reviewed against current `production` HEAD:

- `a0108adea9d6d6042586f39f8e41eea70b88d49d`
- commit message: `checked in Daily Production Enhancement`
- parent: `8133a34875d08ce6d48c1a53c4ec86e63320f780`

The one-commit delta from the previous FG-fix baseline changes:

- `ProductionOperationSequenceGate.cs`
- `ProductionOutputService.Entry.cs`
- `ProductionOutputService.Rollback.cs`
- Daily/Material-Issue sequence and handoff tests

It does **not** change the FG entry Razor/code-behind/CSS, the FG service contract/implementation, the FG math, or the Daily/Material-Issue entry Razor UX used as the visual reference.

Therefore the UX findings below remain valid against current source, while this plan's baseline and acceptance criteria are rebased to `a0108ade...`.

A later review of this plan required the following implementation constraints, now incorporated throughout: load-failed state ahead of permission/selection; four compact readiness outcomes; view-grid `SourceId` key; zero-line workspace copy; removal of the duplicate Work Order field; and a second eligible Work Order in the scoped filter-option test.


### 2.1 Current FG entry

Primary files:

- `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor.cs`
- `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor.css`

Verified current behavior:

1. Editable FG always renders **Find eligible source lots** before the receipt workspace.
2. The current finder exposes Work Order, Work Centre, Process, and Item in one always-open row.
3. Matching source results use `pr-dp-op` cards.
4. Selected destination lines are also rendered as `pr-dp-op` cards.
5. `AddSourceAsync()` locks the document to one Work Order by setting:
   - `Document.WorkOrderId`
   - `Document.WorkOrderNo`
6. `AddSourceAsync()` deliberately allows the same `ProductionBalLotId` more than once and reports:
   - `This source is already on the receipt. A second line will split the destination.`
7. `SearchSourcesAsync()` already scopes subsequent source searches by `Document.WorkOrderId` when the Work Order is locked.
8. `OnWarehouseChangedAsync()` clears Location and reloads valid locations for that specific warehouse/line.
9. `NavigationLock` protects dirty drafts and must remain.
10. `CREATE CORRECTION`, POST, ROLLBACK, DELETE and EDIT DRAFT are FG-specific lifecycle actions and must remain.

### 2.2 Daily Production reference

Reference files:

- `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor.cs`
- `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor.css`

Daily Production already implements the desired high-level pattern:

- explicit selection phase via `IsSelectingOperation`;
- finder disappears once an operation workspace is loaded;
- `CHANGE OPERATION` returns to selection;
- Work Order / Work Centre / Process are primary filters;
- additional filters are behind **More filters / Fewer filters**;
- material/detail editing uses `DxGrid`;
- page order is context/meta → entry details → line grid → footer.

### 2.3 Material Issue / Issue to Production reference

Reference files:

- `ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor.cs`
- `ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor.css`

Material Issue independently confirms the same source-selection pattern:

- `Workspace is null` = finder phase;
- Work Order / Work Centre / Process as primary filters;
- additional filters behind More/Fewer;
- line editing in `DxGrid`;
- operation workspace separated from finder.

### 2.4 Repository UI standard

`.agents/skills/erp-transaction-ui-standard/skill.md` explicitly defines the normal transaction entry shape as:

1. Hero
2. Header form card
3. Detail/line card
4. Detail grid
5. Footer
6. Picker / confirmation popups as required

Therefore this change is not a new design language. It brings FG back to the declared ERP transaction standard.

---

## 3. Critical Business Rule: Do Not Turn FG Into a Single-Source Document

FG cannot copy Daily Production literally.

Daily Production selects one operation. FG can receive **multiple eligible final production balance lots from the same Work Order**.

The current backend explicitly supports:

- multiple receipt lines;
- repeated use of the same source lot to split destinations;
- different destination warehouses on different lines;
- aggregate quantity protection across repeated source lines.

This behavior is verified in:

`ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs`

including:

- `Save_maps_product_codes_grouped_qty_and_non_lot_expiry()` — saves two lines from the same `ProductionBalLotId` and rejects aggregate over-consumption;
- `List_search_summary_and_posting_flag_are_document_owned()` — verifies one FG receipt may contain multiple destination warehouses;
- `Destination_location_is_branch_scoped()` — validates warehouse/location scope;
- `Lot_controlled_source_defaults_and_keeps_destination_lot()` — validates lot-controlled destination behavior;
- correction / rollback / posting tests.

**Design consequence:**

The first selected source moves the page into the workspace, but the workspace must provide **ADD SOURCE LOT** so additional eligible lots from the same locked Work Order can be added.

---

# 4. Target Page State Model

Introduce a clear UI state model rather than allowing finder and workspace to coexist.

The state model must also fail safely for users who can reach the menu but do not hold the Add/Edit permission required by the route. The previous version of this plan used only `CanEditFields && !HasLockedWorkOrder` / `CanEditFields && HasLockedWorkOrder`; that can produce a blank transaction body when Add/Edit permission is missing. Current repository UI guidance explicitly requires edit routes to fail safely.

`FinishedGoodReceiptDocument.Status` defaults to `"NEW"`. A failed `GetAsync` currently does `Document = new()` and returns. Permission and selection guards that key off `Status == "NEW"` therefore cannot run until load success is known. Load failure is its own state, ahead of View, permission denial, selection, and workspace.

## 4.1 States

### State A — Bootstrapping

Existing skeleton behavior remains.

### State B — Load failed

Condition after bootstrap, when `Id > 0` and `GetAsync` failed or returned no document:

```csharp
protected bool LoadFailed =>
    !IsBootstrapping
    && Id > 0
    && Document.Id == 0;
```

Keep the existing early return after a failed `GetAsync`. Render:

- hero;
- command bar;
- the load error already assigned to `ErrorMessage`;
- a compact info card that the receipt could not be loaded.

Do **not**:

- open the source finder;
- treat the empty default document as a new receipt;
- treat it as `EntryPermissionDenied` (a missing receipt must not look like an access denial for users without Edit).

### State C — View

Condition:

```csharp
!LoadFailed && IsViewMode
```

Render:

- hero;
- command bar;
- document/meta card;
- read-only `DxGrid` for receipt lines, keyed on `FinishedGoodReceiptLine.SourceId`;
- compact readiness/status message only when relevant.

Do not render finder.

### State D — Permission-safe route state

For a brand-new route:

```text
/planning/finished-good-receipts/new
```

when the user lacks `PermissionCodes.Add`, render a normal access-denied/info card under the command bar. Do not render an empty body.

For a saved NEW receipt opened through `/edit/{id}` without `PermissionCodes.Edit`, navigate to `/view/{id}` **only after a successful load**. Set `_navigating = true` and use `replace: true`, matching the existing post-save navigation in `SaveAsync`. If route redirection is not used, render the same safe read-only/access-denied state; never leave the page between finder/workspace branches.

Do not redirect, and do not set `EntryPermissionDenied`, when `LoadFailed` is true.

Recommended computed guard:

```csharp
protected bool EntryPermissionDenied =>
    !LoadFailed
    && ((IsNewMode && !CanAdd)
        || (IsEditRequested
            && Id > 0
            && Document.Id > 0
            && string.Equals(Document.Status, "NEW", StringComparison.OrdinalIgnoreCase)
            && !CanEdit));
```

### State E — Select Source

Condition:

```csharp
protected bool IsSelectingSource =>
    !LoadFailed
    && !EntryPermissionDenied
    && !IsViewMode
    && CanEditFields
    && !HasLockedWorkOrder;
```

Render only:

- hero;
- command bar;
- source finder;
- matching source results.

Do **not** render:

- receipt detail form;
- destination line grid;
- readiness block;
- save footer.

Selecting the first source locks the Work Order and transitions immediately to State F.

### State F — Receipt Workspace

Condition:

```csharp
protected bool IsReceiptWorkspace =>
    !LoadFailed
    && !EntryPermissionDenied
    && !IsViewMode
    && CanEditFields
    && HasLockedWorkOrder;
```

Render:

1. Work Order/source context card
2. Receipt details
3. Destination line `DxGrid`
4. Compact readiness/status area when applicable
5. footer

The full-page finder must be hidden.

Zero lines after REMOVE still stay in this state. Empty-grid copy must point at **ADD SOURCE LOT** and **CHANGE WORK ORDER**; do not reuse the current “Find and select a production lot” text.

### State G — Add Source Lot Picker

This is an overlay opened from the line-grid header using **ADD SOURCE LOT**.

It is always scoped to:

```csharp
Document.WorkOrderId
```

It must never allow adding a source from another Work Order.

The picker is transient UI state only. Opening/closing it, paging it, or changing its filters must not affect `Dirty`.

# 5. Concrete Razor Changes

## File

`ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor`

---

## 5.1 Keep the existing hero and command bar

Keep:

- `iv-hero`
- mode chip
- batch chip
- Work Order chip
- status chip
- line-count KPI
- BACK
- EDIT DRAFT
- POST
- ROLLBACK
- DELETE
- CREATE CORRECTION

Keep **CHANGE WORK ORDER** wording for FG.

Do **not** rename it to CHANGE OPERATION because FG is locked by Work Order, not by one operation.

Condition remains conceptually:

```csharp
CanEditFields && HasLockedWorkOrder
```

---

## 5.2 Replace the current always-visible editable layout with explicit branches

Current editable flow is effectively:

```text
Finder
Results
Receipt details
Destination cards
Readiness card
Footer
```

Replace it with a complete state switch. Load failure comes before View and permission denial so a missing receipt cannot render as an access-denied card or as a new-receipt finder:

```razor
@if (IsBootstrapping)
{
    ...
}
else if (LoadFailed)
{
    // load-error card; ErrorMessage already set
}
else if (IsViewMode)
{
    // view workspace
}
else if (EntryPermissionDenied)
{
    // safe access-denied / route fallback card
}
else if (IsSelectingSource)
{
    // finder + results only
}
else if (IsReceiptWorkspace)
{
    // context + details + line grid + compact readiness + footer
}
else
{
    // defensive fallback; do not silently render an empty page
}
```

The defensive fallback should show a small error/info card and log/flag the unexpected state rather than producing a blank transaction body.

This is the single most important structural change.

---

## 5.3 Do not introduce a second DxGrid editing model

The repository's current Daily Production entry (`PrDailyProductionEntry.razor`) edits values by placing DevExpress editors inside `DxGridDataColumn.CellDisplayTemplate`.

FG must follow that exact interaction style.

Do **not** enable DxGrid row-edit/edit-form modes such as:

- edit-row state;
- popup edit form;
- `EditModelSaving`;
- separate detail edit dialog for normal destination fields.

Qty, Warehouse, Location, Destination Lot and Expiry should remain bound directly to `ReceiptLineEditor` through cell templates. This keeps FG behavior visually and technically aligned with Daily/IP.

# 6. Initial Source Finder — Match Daily/IP

The first-source finder should visually follow `PrDailyProductionEntry.razor`.

## 6.1 Primary filters

Always visible:

- Work Order — `ColSpanMd="4"`
- Work Centre — `ColSpanMd="4"`
- Process — `ColSpanMd="4"`

## 6.2 More/Fewer filters

Add:

```csharp
protected bool MoreFiltersVisible;
```

When expanded, show:

- Item

Do not keep Item permanently in the primary row.

Use the same button language as Daily/IP:

```text
More filters
Fewer filters
```

with the sliders icon.

## 6.3 Separate initial-finder clear from locked-picker clear

Do not use one ambiguous synchronous clear method for both modes.

Initial finder:

```csharp
protected async Task ClearInitialSourceFiltersAsync()
{
    SearchWo = string.Empty;
    SearchWorkCentre = string.Empty;
    SearchProcess = string.Empty;
    SearchItem = string.Empty;
    SourcePage = 0;
    await SearchSourcesAsync();
}
```

Locked ADD SOURCE LOT picker:

```csharp
protected async Task ClearPickerFiltersAsync()
{
    SearchWorkCentre = string.Empty;
    SearchProcess = string.Empty;
    SearchItem = string.Empty;
    SourcePage = 0;
    await SearchSourcesAsync();
}
```

The locked picker never clears `Document.WorkOrderId`; its query remains server-scoped to the selected Work Order.

Layout in both modes:

```text
[More/Fewer filters]                     [CLEAR] [FIND]
```

`FIND` resets `SourcePage = 0` before querying.

## 6.4 Results

Keep result cards for the selection list. Daily/IP also use compact card rows for operation selection, so cards are appropriate here.

Result header should match Daily style:

```text
Matching production lots                  18 sources · page 1 / 1
```

Each row keeps:

- Work Order
- Item / source lot
- Work Centre / Process
- lot-control indicator
- available quantity
- readiness text
- SELECT

The source-results cards are **not** the inconsistency that needs removal. The inconsistency is using the same card pattern for editable receipt lines.

## 6.5 Filter-option scope

The initial finder may load filter choices across all eligible FG sources.

The ADD SOURCE LOT picker must load filter choices scoped to the already locked `Document.WorkOrderId`; otherwise the current global `WorkCentres`, `Processes`, and `Items` lists can offer values that belong only to other Work Orders and therefore always return zero rows.

This requires the small read-only service enhancement in section 19. No posting/write rule changes are involved.

# 7. First SELECT Transition

Modify `AddSourceAsync(FinishedGoodSourceRow source)` only at the UI-state level.

Preserve the current guard:

```csharp
if (HasLockedWorkOrder && Document.WorkOrderId != source.WorkOrderId)
```

Preserve duplicate source support.

On first selection:

1. assign `Document.WorkOrderId`;
2. assign `Document.WorkOrderNo`;
3. create the `FinishedGoodReceiptLine` exactly as today;
4. load its destination lookup state using `CreateEditorAsync()`;
5. add it to `Editors`;
6. because `HasLockedWorkOrder` is now true, the Razor state automatically switches from finder to workspace.

No route change is required.

---

# 8. Workspace Section Order

Once a Work Order is locked, render this exact order:

```text
Hero
Command bar

Work Order / source context

Receipt details

Destination lines
    [ADD SOURCE LOT]
    DxGrid
    compact readiness/status

Footer
```

This matches the mental model already taught by Daily/IP.

---

# 9. Work Order / Source Context Card

Add a concise meta card before Receipt details.

Use existing data only; no new service DTO is required.

Recommended fields:

- Work Order — `Document.WorkOrderNo`
- Selected source lines — `Editors.Count`
- Item summary — distinct `Editors[].Line.ItemCode`
- Source lot summary — distinct source lots
- Work Centre / Process summary when one value is common; otherwise `Multiple`
- status

Do not create another large custom card type. Use existing `pr-dp-meta` / `pr-dp-meta__item` styling.

Purpose:

- make the selected Work Order obvious after the finder disappears;
- replace the lost finder context;
- give CHANGE WORK ORDER a clear meaning.

---

# 10. Receipt Details Card

Keep current business fields and bindings:

- Receipt date → `Document.EffectiveDate`
- Reference → `RefNo`
- Remarks → `Remarks`

**Remove** the current read-only Work Order field from this card. The selected Work Order already appears in the context/meta card (section 9) and the hero chip. Showing it again here duplicates the lock after the finder disappears.

Keep existing limits:

- RefNo: 30
- Remarks: 200

Keep `SizeMode.Small` and current responsive form behavior.

No new header fields are required.

---

# 11. Replace Destination Cards With `DxGrid`

This is the primary visual alignment change.

## 11.1 Data source and grid mode

Editable/new workspace:

```razor
<DxGrid Data="@Editors"
        KeyFieldName="@nameof(ReceiptLineEditor.Key)"
        ShowFilterRow="false"
        ShowGroupPanel="false"
        ShowSearchBox="false"
        TextWrapEnabled="false"
        PageSize="20"
        ColumnResizeMode="GridColumnResizeMode.ColumnsContainer"
        CssClass="pr-dp-grid">
```

Use `CellDisplayTemplate` for every editable cell, exactly like current Daily Production.

Do **not** use a separate edit form, popup, `EditModelSaving`, or DxGrid edit-row mode for normal FG line fields.

A representative pattern is:

```razor
<DxGridDataColumn Caption="Receipt Qty" Width="150px">
    <CellDisplayTemplate>
        @{
            var editor = (ReceiptLineEditor)context.DataItem;
        }
        <DxSpinEdit Value="@editor.Line.Quantity"
                    ValueChanged="@((decimal value) => editor.Line.Quantity = value)"
                    Enabled="@CanEditFields"
                    MinValue="0m"
                    Increment="0.0001m"
                    DisplayFormat="n4"
                    ShowSpinButtons="false" />
    </CellDisplayTemplate>
</DxGridDataColumn>
```

This matches the repository's current Daily Production grid technique and avoids creating a second transaction-line editing model.

## 11.2 Grid columns

Recommended column order:

### 1. Source

Stack:

```text
ITEMCODE
WorkCentre / Process
```

Read-only.

Recommended width/min-width: `170–200px`.

### 2. Source Lot

Read-only.

Use `—` when blank.

Recommended width: `130–150px`.

### 3. Available

Read-only.

Display:

```text
12.0000 EA
```

Right aligned.

Recommended width: `125–140px`.

### 4. Receipt Qty

Inline `DxSpinEdit` inside `CellDisplayTemplate` using the exact current FG settings:

```razor
MinValue="0m"
Increment="0.0001m"
DisplayFormat="n4"
ShowSpinButtons="false"
```

Do not change four-decimal quantity behavior.

Recommended width: `145–160px`.

### 5. Warehouse

Inline searchable `DxComboBox` inside `CellDisplayTemplate` using existing `Warehouses`.

Preserve the current method:

```csharp
OnWarehouseChangedAsync(editor, value)
```

Changing Warehouse must continue to:

1. set warehouse;
2. clear location;
3. reload locations for that row.

Representative binding:

```razor
<DxComboBox Data="@Warehouses"
            TextFieldName="@nameof(ProductionOutputChoice.Label)"
            ValueFieldName="@nameof(ProductionOutputChoice.Code)"
            Value="@editor.Line.Warehouse"
            ValueChanged="@(async (string? value) => await OnWarehouseChangedAsync(editor, value))"
            NullText="Required warehouse"
            SearchMode="ListSearchMode.AutoSearch"
            SearchFilterCondition="ListSearchFilterCondition.Contains"
            Enabled="@CanEditFields" />
```

Recommended width: `180–210px`.

### 6. Location

Inline searchable `DxComboBox` inside `CellDisplayTemplate` using:

```csharp
editor.Locations
```

Rules remain:

- optional;
- disabled until Warehouse is selected;
- clearing is allowed;
- values are warehouse-specific.

Daily Production’s in-grid combo binds a page-level list. FG location options are replaced per row inside `OnWarehouseChangedAsync`. After a warehouse change, the location cell must show only the new warehouse’s locations. If the grid cell does not rebind, refresh that row. Do not add a second edit-form model.

Recommended width: `170–190px`.

### 7. Destination Lot

If `editor.Line.LotControl`:

- inline `DxTextBox`;
- required by the backend when saved;
- current source lot remains the default for newly added lot-controlled sources.

If not lot-controlled:

- render `—`;
- do not allow editing.

Recommended width: `160–190px`.

### 8. Expiry

If lot-controlled:

- inline `DxDateEdit`;
- clear button allowed.

If not lot-controlled:

- render `—`.

Recommended width: `130–150px`.

### 9. Destination Qty

Read-only.

Preserve the current truth that destination quantity is authoritative only after server conversion/save.

Use the existing concept:

```text
if Dirty -> Recalculated on save
else     -> 12.0000 EA
```

Do not implement a second client-side UOM conversion engine merely for display.

Recommended width: `140–160px`.

### 10. Value — conditional

Only when:

```csharp
Document.CanViewCost
```

Display current estimated / posted value behavior.

Never expose cost when permission is absent.

### 11. Action

Editable modes only:

- REMOVE button/icon in `CellDisplayTemplate`.

Read-only modes do not show an action column.

# 12. Important Change to `RemoveLine`

Current code does this when the last line is removed:

```csharp
if (Editors.Count == 0)
{
    Document.WorkOrderId = 0;
    Document.WorkOrderNo = "";
}
```

**Change this behavior.**

Removing the final line should **not silently unlock the Work Order and throw the operator back into source selection**.

New behavior:

```csharp
protected void RemoveLine(ReceiptLineEditor editor)
{
    Editors.Remove(editor);
}
```

The workspace remains locked to the selected Work Order with zero lines.

The operator then has two explicit choices:

- **ADD SOURCE LOT** — add another source from the same Work Order;
- **CHANGE WORK ORDER** — intentionally clear the Work Order lock.

This is a major UX improvement because state changes become explicit instead of being triggered by deleting the last row.

`CanSave` remains false while `Editors.Count == 0`.

When `Editors.Count == 0` inside the workspace, replace the current empty copy:

```text
No sources selected
Find and select a production lot to add a receipt line.
```

with copy that matches the locked workspace:

```text
No destination lines
Use ADD SOURCE LOT to add an eligible lot from this Work Order, or CHANGE WORK ORDER to start over.
```

Save stays disabled until at least one line is present.

---

# 13. Add Source Lot Picker

## 13.1 Line-grid header action

Add to the Destination Lines section header:

```text
Destination                                      [ADD SOURCE LOT]
```

Visible when:

```csharp
CanEditFields && HasLockedWorkOrder && Editors.Count < 200
```

The 200-line limit matches the authoritative service rule in `SaveCoreAsync()`.

## 13.2 Popup

Add:

```csharp
protected bool SourcePickerVisible;
```

Use the repository `DxPopup` / `common-popup` pattern already used for transactional pickers/confirmations.

The popup should contain:

- locked Work Order context;
- Work Centre;
- Process;
- More/Fewer → Item;
- CLEAR;
- FIND;
- paged matching source lots;
- existing loading skeleton using `IsSearchingSources`.

Do not show an editable Work Order dropdown inside this popup.

Both the result query and the filter-option query must be scoped to `Document.WorkOrderId`.

## 13.3 Lazy opening order

A saved Edit document should not fetch source finder data during page bootstrap.

Open the popup first, then load the source metadata/results so the user immediately sees the modal/skeleton rather than waiting on a hidden async operation.

Recommended shape:

```csharp
protected async Task OpenSourcePickerAsync()
{
    if (!CanAddSourceLot)
        return;

    SearchWorkCentre = string.Empty;
    SearchProcess = string.Empty;
    SearchItem = string.Empty;
    MoreFiltersVisible = false;
    SourcePage = 0;

    SourcePickerVisible = true;

    await LoadFilterOptionsAsync(Document.WorkOrderId);
    await SearchSourcesAsync();
}
```

Do not clear `Document.WorkOrderId`.

If filter options are cached per Work Order, the implementation may skip reloading them when the cached `WorkOrderId` matches, but correctness is more important than premature caching.

## 13.4 Selecting inside the picker

Reuse `AddSourceAsync()`.

After the row is added successfully:

- close the picker;
- show the added row immediately;
- optionally restore focus to the line-grid area if practical.

If the same source is already on the receipt, preserve the existing message explaining that another selection creates a destination split.

Do not disable duplicate selection because duplicate source lines are legitimate FG behavior.

## 13.5 Picker filters are non-dirty UI state

Opening/closing the picker, toggling More/Fewer, changing picker filters, or paging results must not manually modify `_saved` or `Dirty`.

`Dirty` must continue to be derived only from:

```csharp
JsonSerializer.Serialize(ToRequest())
```

versus `_saved`.

# 14. CHANGE WORK ORDER Behavior

Keep the confirmation dialog.

On confirmed change:

1. clear `Editors`;
2. clear `Document.WorkOrderId`;
3. clear `Document.WorkOrderNo`;
4. close source picker;
5. reset all source filters;
6. reset More/Fewer state;
7. reset paging;
8. reload **global** eligible source filter options;
9. reload eligible source results;
10. automatically return to the Select Source phase.

Keep the current Receipt Date, Reference, and Remarks unless the product explicitly decides otherwise. They are document header data, not source-selection data.

Recommended implementation shape:

```csharp
protected async Task ChangeWorkOrderAsync()
{
    Editors.Clear();
    Document.WorkOrderId = 0;
    Document.WorkOrderNo = "";
    SearchWo = string.Empty;
    SearchWorkCentre = string.Empty;
    SearchProcess = string.Empty;
    SearchItem = string.Empty;
    MoreFiltersVisible = false;
    SourcePickerVisible = false;
    ChangeWorkOrderVisible = false;
    SourcePage = 0;

    await LoadFilterOptionsAsync(workOrderId: null);
    await SearchSourcesAsync();
}
```

Do **not** manually set a dirty flag.

The current component deliberately derives:

```csharp
Dirty => CanEditFields && _saved != JsonSerializer.Serialize(ToRequest());
```

Keep that design. Changing Work Order will be dirty whenever it changes the persisted request payload. On a brand-new unsaved document, returning from a selected Work Order to the original empty request may legitimately make `Dirty` false again.

The existing `NavigationLock` continues to protect actual unsaved payload changes.

# 15. Readiness — Keep the Logic, Remove the Page-Shaping Card

Do **not** remove readiness logic.

Preserve:

- `Document.PostingEnabled`
- `Document.ReadinessErrors`
- service-side `SourceReadinessAsync()`
- POST service validation
- current `OpenPostConfirm()` behavior

But remove the full standalone Readiness `iv-card` from both editable and view layouts.

## 15.1 Compact rendering for saved NEW drafts

Place a compact status block under the line grid and above the footer.

New documents currently set `PostingEnabled = false` in the page even when the feature flag is on. Saved documents copy `PostingEnabled` from `FinishedGoodReceiptOptions`. Compact readiness therefore has four mutually exclusive outcomes:

1. Brand-new unsaved document (`Id == 0`):

```text
Save the draft before posting checks are available.
```

Do not display the placeholder `PostingEnabled = false` as if it were a real release failure. At that point the document cannot be posted anyway.

2. Saved NEW draft with posting disabled (`Id > 0 && !Document.PostingEnabled`):

```text
FG posting is disabled pending release acceptance.
```

Do **not** use the “No known posting-readiness issues” success copy while the flag is off.

3. Saved NEW draft with posting enabled and readiness errors:

```text
⚠ Posting readiness
  • Source 123: pooled cost is unverified; upstream cost evidence is required.
```

4. Saved NEW draft with posting enabled and no readiness errors:

```text
✓ No known posting-readiness issues.
```

Show backend readiness only once the receipt exists / has been mapped from the service.

## 15.2 Posted/reversed documents

Do not show a large readiness area for POSTED or REVERSED documents.

Readiness is principally useful for NEW drafts awaiting POST.

---

# 16. View Mode — Use the Same Grid Language

Current view mode renders `Document.Lines` using `pr-dp-op` cards.

Replace these with a read-only `DxGrid`.

Key the view grid on `FinishedGoodReceiptLine.SourceId`. Split destination lines share `ProductionBalLotId`, so that field (and source lot) is not a unique row key. The editable grid already uses `ReceiptLineEditor.Key` (`Guid`); view mode has no editor wrapper and must use the persisted source-line id:

```razor
<DxGrid Data="@Document.Lines"
        KeyFieldName="@nameof(FinishedGoodReceiptLine.SourceId)"
        ...>
```

Recommended columns:

- Item / Work Centre / Process
- Source Lot
- Receipt Qty
- Destination Qty
- Warehouse
- Location
- Destination Lot
- Expiry
- Value, only when `Document.CanViewCost`

The view grid should use the same column order and visual hierarchy as the editable grid.

Do not load warehouse/location lookup lists solely for view mode.

Use `Document.Lines` directly for the read-only grid.

---

# 17. Code-Behind Changes

## File

`ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor.cs`

## 17.1 Add UI-state fields

Add:

```csharp
protected bool MoreFiltersVisible;
protected bool SourcePickerVisible;
```

Add computed states:

```csharp
protected bool LoadFailed =>
    !IsBootstrapping
    && Id > 0
    && Document.Id == 0;

protected bool EntryPermissionDenied =>
    !LoadFailed
    && ((IsNewMode && !CanAdd)
        || (IsEditRequested
            && Id > 0
            && Document.Id > 0
            && string.Equals(Document.Status, "NEW", StringComparison.OrdinalIgnoreCase)
            && !CanEdit));

protected bool IsSelectingSource =>
    !LoadFailed
    && !EntryPermissionDenied
    && !IsViewMode
    && CanEditFields
    && !HasLockedWorkOrder;

protected bool IsReceiptWorkspace =>
    !LoadFailed
    && !EntryPermissionDenied
    && !IsViewMode
    && CanEditFields
    && HasLockedWorkOrder;

protected bool CanAddSourceLot =>
    IsReceiptWorkspace
    && Editors.Count < 200;
```

No new persisted/domain state is required.

## 17.2 Permission-safe routing

After loading permissions/document:

- `/new` without Add permission: render a normal access-denied/info state;
- `/edit/{id}` for a NEW receipt without Edit permission: navigate to `/view/{id}` **only after a successful load**. Set `_navigating = true` and call `Navigation.NavigateTo(..., replace: true)` using the same pattern as the existing post-save navigation in `SaveAsync`;
- a failed `GetAsync` keeps the current early return, sets `ErrorMessage`, and stays in `LoadFailed`. Do not redirect that case to View and do not treat it as `EntryPermissionDenied`;
- POSTED/REVERSED documents continue to resolve to View through existing `IsViewMode` logic.

Do not rely on Razor branch fall-through to represent denied access.

## 17.3 Bootstrap behavior — lazy source loading

Current editable bootstrap always loads filter options and source rows.

Keep the existing failed-load early return. Change successful editable bootstrap so saved Edit documents already locked to a Work Order load only the data required for the workspace.

Recommended:

```csharp
if (Id > 0)
{
    var result = await Receipts.GetAsync(Id);
    if (!result.Succeeded || result.Data is null)
    {
        ErrorMessage = result.Message ?? "Unable to load the finished good receipt.";
        Document = new();
        IsBootstrapping = false;
        return;
    }

    Document = result.Data;

    if (IsEditRequested
        && string.Equals(Document.Status, "NEW", StringComparison.OrdinalIgnoreCase)
        && !CanEdit)
    {
        _navigating = true;
        Navigation.NavigateTo($"/planning/finished-good-receipts/{Id}/view", replace: true);
        return;
    }
}
else
{
    Document = new() { EffectiveDate = Clock.Now, Status = "NEW", PostingEnabled = false };
}

if (CanEditFields)
{
    await LoadWarehousesAsync();

    Editors = [];
    foreach (var line in Document.Lines)
        Editors.Add(await CreateEditorAsync(line, defaultWarehouse: false));

    if (!HasLockedWorkOrder)
    {
        await LoadFilterOptionsAsync(workOrderId: null);
        await SearchSourcesAsync();
    }
}
```

Benefits:

- saved Edit opens directly in workspace;
- no unnecessary eligible-source/filter-option queries on page load;
- finder cannot flash/stack above an existing draft;
- ADD SOURCE LOT becomes the explicit point where scoped source options/results are loaded.

## 17.4 Filter-option loader

Change the UI helper to accept an optional Work Order scope:

```csharp
private async Task LoadFilterOptionsAsync(long? workOrderId)
{
    var result = workOrderId.HasValue
        ? await Receipts.GetSourceFilterOptionsAsync(workOrderId.Value)
        : await Receipts.GetSourceFilterOptionsAsync();

    FilterOptions = result.Data ?? new FinishedGoodSourceFilterOptions();

    if (!result.Succeeded)
        ErrorMessage = result.Message ?? "Unable to load source filters.";
}
```

If the service is implemented with one optional-parameter method instead of overloads, preserve source compatibility for all existing callers.

## 17.5 Clear/filter methods

Use separate async clear methods for:

- initial source finder;
- locked ADD SOURCE LOT picker.

Both reset page to zero and refresh results.

Use one `FindSourcesAsync()` that resets page then calls `SearchSourcesAsync()`.

## 17.6 Add source

Keep current one-Work-Order guard and duplicate source support.

After successful addition from popup:

```csharp
SourcePickerVisible = false;
```

Do not automatically clear the Work Order if the line is later removed.

The existing guard remains a defensive second line of protection even though the popup query is already Work-Order scoped:

```csharp
if (HasLockedWorkOrder && Document.WorkOrderId != source.WorkOrderId)
{
    ErrorMessage = "This receipt is locked to one Work Order. Use CHANGE WORK ORDER first.";
    return;
}
```

## 17.7 Warehouse/location behavior

Keep unchanged:

```csharp
OnWarehouseChangedAsync()
LoadLineLocationsAsync()
AddHistoricalChoice()
```

This is important for Edit mode because historical destination codes may no longer be active, and the current code deliberately adds historical choices for display while the service still rejects an inactive/invalid destination on Save until replaced.

## 17.8 Save request

Keep `ToRequest()` structurally unchanged.

It must continue sending only:

- `ProductionBalLotId`
- Quantity
- Warehouse
- Location
- LotNo
- ExpiryDate

Do not send client-computed cost or destination conversion quantities.

## 17.9 Dirty-state rule

Do not introduce a mutable `IsDirty` flag.

Keep:

```csharp
protected bool Dirty =>
    CanEditFields
    && _saved != JsonSerializer.Serialize(ToRequest());
```

Search/picker state is intentionally absent from `ToRequest()`, so it remains non-dirty without extra code.

# 18. CSS Changes

## File

`ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor.css`

## 18.1 Keep

Keep shared FG/Daily-style classes still used for:

- page shell;
- command bar;
- filter actions;
- section headings;
- finder result cards;
- meta layout;
- empty states;
- footer;
- grid wrapper.

## 18.2 Remove obsolete line-card styling

Once destination lines move to `DxGrid`, remove CSS that exists only for editable destination cards, especially if no longer referenced:

- `.pr-dp-op__main--line`
- line-card-only quantity layout rules
- any responsive rules used only by the old destination card structure.

Do **not** remove `.pr-dp-op` / `.pr-dp-ops` globally because the source-selection result cards still use them.

## 18.3 Add only minimal FG-specific grid/readiness styles

Add small scoped classes for:

- grid cell stacked primary/secondary text if required;
- compact readiness message;
- popup content spacing;
- action column alignment.

Do not introduce a second visual design system or new arbitrary colors.

Use existing Inventory/Production CSS variables.

---

# 19. Backend / Service Layer — One Small Read-Only Enhancement, No Write Redesign

The FG write/posting architecture remains unchanged.

The only planned service-layer enhancement is to allow source filter choices in the ADD SOURCE LOT popup to be scoped to the already locked Work Order.

## 19.1 Interface

Current contract:

```csharp
Task<IvMasterOperationResult<FinishedGoodSourceFilterOptions>>
    GetSourceFilterOptionsAsync(CancellationToken ct = default);
```

Preferred source-compatible approach: keep that method and add an overload:

```csharp
Task<IvMasterOperationResult<FinishedGoodSourceFilterOptions>>
    GetSourceFilterOptionsAsync(long workOrderId, CancellationToken ct = default);
```

This avoids breaking any existing positional `CancellationToken` callers.

## 19.2 Implementation

In:

`ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.cs`

factor the current filter-option query into a small private core method:

```csharp
private async Task<IvMasterOperationResult<FinishedGoodSourceFilterOptions>>
    GetSourceFilterOptionsCoreAsync(long? workOrderId, CancellationToken ct)
{
    var scope = await ScopeAsync(PermissionCodes.Access, ct);
    await using var db = await factory.CreateDbContextAsync(ct);

    var lots = EligibleSources(db, scope).AsNoTracking();

    if (workOrderId.HasValue)
        lots = lots.Where(x => x.WorkOrderId == workOrderId.Value);

    ...
}
```

Then:

```csharp
public Task<IvMasterOperationResult<FinishedGoodSourceFilterOptions>>
    GetSourceFilterOptionsAsync(CancellationToken ct = default)
    => GetSourceFilterOptionsCoreAsync(null, ct);

public Task<IvMasterOperationResult<FinishedGoodSourceFilterOptions>>
    GetSourceFilterOptionsAsync(long workOrderId, CancellationToken ct = default)
    => GetSourceFilterOptionsCoreAsync(workOrderId, ct);
```

The existing eligibility predicate remains authoritative:

```csharp
EligibleSources(db, scope)
```

Do not duplicate or loosen its rules.

## 19.3 Why this enhancement is justified

Current `SearchSourcesAsync()` is already correctly scoped by `WorkOrderId`, but current `GetSourceFilterOptionsAsync()` builds Work Centre / Process / Item choices across all eligible Work Orders.

Without this enhancement, an ADD SOURCE LOT picker locked to Work Order A can present a filter value that exists only on Work Order B and can therefore never return a row.

The scoped overload fixes that UX inconsistency without changing:

- source eligibility;
- write behavior;
- posting;
- costing;
- stock movement;
- database schema.

## 19.4 Authoritative service rules that remain unchanged

`ProductionFinishedGoodReceiptService.SaveCoreAsync()` already enforces:

- permission and tenant/branch scope;
- 1–200 receipt lines;
- no future receipt date;
- one Work Order per FG receipt;
- cancelled Work Order rejection;
- eligible final FG staging sources only;
- source must belong to the chosen Work Order;
- repeated source-line quantities are aggregated before availability validation;
- requested quantity cannot exceed production balance;
- 4-decimal quantity/UOM conservation;
- active destination warehouse;
- location must belong to destination warehouse;
- destination lot requirement for lot-controlled items;
- lot length;
- expiry cannot precede receipt date;
- optimistic concurrency;
- NEW-only edit/delete;
- posting/rollback/correction lifecycle.

UI validation is convenience only. Do not weaken or duplicate these service rules.

# 20. Posting, Costing, Stock and Correction — Must Remain Unchanged

This UX change must not alter:

- `FinishedGoodReceiptMath.Convert()`;
- destination/base UOM conversion;
- production pool valuation;
- exact transferred value;
- production balance reduction;
- inventory receipt posting;
- lot ownership/origin facts;
- stock history;
- correction creation;
- rollback order rules;
- idempotent POST/ROLLBACK requests;
- posting-disabled feature flag;
- `CanViewCost` permission behavior.

No database migration is required.

---

# 21. NavigationLock — Keep It

Keep:

```razor
<NavigationLock ConfirmExternalNavigation="Dirty"
                OnBeforeInternalNavigation="BeforeNavigate" />
```

Do not remove this simply because Daily/IP do not currently use it.

The new picker/filter UI must not affect dirty state because `Dirty` is based on serialized `ToRequest()`.

Changing only search filters, More/Fewer, paging, or opening/closing the source popup must therefore remain non-dirty.

Receipt header or line changes remain dirty exactly as today.

---

# 22. Permission and Mode Rules

Preserve all existing permission gates:

- Access
- Add
- Edit
- Delete
- Post
- Rollback
- ViewCost

Preserve mode rules:

- POSTED/REVERSED documents are view-only;
- NEW saved document can enter Edit only when Edit permission allows;
- new document requires Add permission;
- correction only from REVERSED with Add permission.

Add the explicit route-safety behavior from section 4:

- `/new` + no Add => visible denied/info state, never blank body;
- `/edit/{id}` + NEW + no Edit => redirect to `/view/{id}` only after a successful load (`_navigating`, `replace: true`);
- failed `GetAsync` => load-error state, never finder and never access-denied;
- command buttons remain gated exactly as current source does.

The UX refactor must not make any hidden action reachable by moving buttons.

Service-layer permission checks remain authoritative even when the UI hides/redirects an action.

# 23. Exact Acceptance Criteria

Implementation is complete only when all of the following are true.

## Current-source baseline

- [ ] implementation is based on `production` commit `a0108adea9d6d6042586f39f8e41eea70b88d49d` or a later commit rechecked for conflicting FG/Daily/IP changes;
- [ ] if `production` advances before implementation, re-diff FG entry/service and Daily/IP entry reference files before coding.

## Permission / route safety

- [ ] `/new` without Add permission does not render a blank transaction body;
- [ ] `/edit/{id}` without Edit permission fails safely, preferably by opening View, **only after a successful load**;
- [ ] a failed `GetAsync` on `/edit/{id}` or `/view/{id}` stays on a load-error state and does not open the finder or the access-denied card;
- [ ] POSTED/REVERSED remain read-only;
- [ ] all service permission checks remain unchanged.

## Selection phase

- [ ] New FG opens with source finder only, not an empty receipt workspace underneath it.
- [ ] Primary filters are Work Order, Work Centre, Process.
- [ ] Item is under More filters.
- [ ] More/Fewer behavior matches Daily/IP.
- [ ] CLEAR resets and refreshes results.
- [ ] FIND resets paging and searches.
- [ ] selecting a source immediately hides the full-page finder.

## Workspace

- [ ] selected Work Order is clearly shown in a context/meta card.
- [ ] receipt details do **not** repeat a read-only Work Order field; the Work Order appears once in the context card (and hero chip).
- [ ] command bar shows CHANGE WORK ORDER.
- [ ] receipt details follow the context card.
- [ ] destination lines are a `DxGrid`, not stacked cards.
- [ ] a zero-line locked workspace points the operator at ADD SOURCE LOT and CHANGE WORK ORDER; it does not say “Find and select a production lot”.
- [ ] line-grid header contains ADD SOURCE LOT.
- [ ] saved Edit opens directly in workspace without loading/showing the finder first.
- [ ] source picker is scoped to the locked Work Order.
- [ ] picker filter choices are also scoped to the locked Work Order.
- [ ] the scoped filter-option test seeds a second eligible Work Order with a distinctive work centre, process, or item; unscoped options include that code and scoped options for the first Work Order do not.
- [ ] source picker cannot change Work Order.
- [ ] same source may be selected again to split destinations.

## Grid

- [ ] editable controls are rendered through `CellDisplayTemplate`, matching current Daily Production.
- [ ] no separate DxGrid row-edit/edit-form model is introduced.
- [ ] Quantity edits inline at four decimals.
- [ ] Warehouse edits inline.
- [ ] changing Warehouse clears and reloads Location.
- [ ] after a warehouse change, the location cell shows only the new warehouse’s locations (refresh the row if the cell does not rebind; do not add a second edit-form model).
- [ ] Location options are warehouse-specific.
- [ ] lot-controlled line exposes Destination Lot and Expiry.
- [ ] non-lot line does not expose fake lot/expiry editing.
- [ ] destination quantity is clearly marked as server-recalculated when dirty.
- [ ] cost/value appears only with `ViewCost` permission.
- [ ] REMOVE works from the grid.
- [ ] removing the last line does **not** unlock the Work Order.
- [ ] Save remains disabled with zero lines.

## ADD SOURCE LOT picker

- [ ] popup becomes visible before async source loading so loading skeleton can render.
- [ ] opening/closing/filtering/paging picker does not change `Dirty`.
- [ ] Work Order field is context-only, not editable.
- [ ] repeated source selection remains allowed.
- [ ] 200-line UI cap matches service cap.

## Change Work Order

- [ ] confirmation remains.
- [ ] changing Work Order clears selected lines.
- [ ] header date/reference/remarks remain intact.
- [ ] global source filter options are reloaded after unlock.
- [ ] page returns to Select Source phase.
- [ ] dirty state remains derived from `ToRequest()` versus `_saved`; no manual dirty flag is added.
- [ ] dirty-state navigation protection remains active only when persisted payload actually differs.

## View/Edit

- [ ] saved draft Edit opens directly in workspace; finder is not stacked above it.
- [ ] View mode uses a read-only grid, not line cards.
- [ ] the view grid is keyed on `FinishedGoodReceiptLine.SourceId`, not `ProductionBalLotId` or source lot.
- [ ] EDIT DRAFT behavior remains unchanged.

## Readiness

- [ ] full standalone Readiness card is removed;
- [ ] brand-new unsaved document (`Id == 0`) says “Save the draft before posting checks are available.” and does not treat placeholder `PostingEnabled=false` as a release failure;
- [ ] saved NEW draft with `!Document.PostingEnabled` says posting is disabled; it does not show the “no known posting-readiness issues” success copy;
- [ ] saved NEW draft with posting enabled shows readiness errors when present, otherwise the compact success copy;
- [ ] service POST validation remains authoritative.

## FG-specific lifecycle

- [ ] POST unchanged.
- [ ] ROLLBACK unchanged.
- [ ] DELETE unchanged.
- [ ] CREATE CORRECTION unchanged.
- [ ] `NavigationLock` retained.
- [ ] no database migration.

# 24. Regression Verification

## 24.1 Targeted automated tests

Run the existing FG transaction test class after implementation:

```bash
dotnet test ErpWeb.Tests --filter "FullyQualifiedName~FinishedGoodReceiptTests"
```

Existing write/posting behavior is not supposed to change, so its regression coverage must remain green.

Pay particular attention to existing coverage for:

- repeated same-source split lines;
- aggregate source quantity validation;
- lot-controlled destination behavior;
- branch-scoped location validation;
- multiple destination warehouses;
- edit concurrency;
- posting/correction/rollback;
- posting-disabled feature flag.

### Required test addition for the scoped filter-option enhancement

Update `FinishedGoodReceiptTests.cs` to prove:

1. unscoped `GetSourceFilterOptionsAsync()` still returns eligible choices across Work Orders;
2. scoped `GetSourceFilterOptionsAsync(workOrderId)` returns only Work Centre / Process / Item choices belonging to that Work Order;
3. a cancelled/ineligible Work Order remains absent because both paths still start from `EligibleSources()`.

The current fixture seeds one eligible Work Order (`WO-FG-HQ`, work centre `WC`, process `PACK`, item `FG`). Unscoped and scoped calls on that fixture alone would return the same lists, so the test cannot assert scoping.

The new or extended test **must seed a second eligible final FG lot** on another Work Order with a different work centre, process, or item. Then assert:

- that other Work Order’s distinctive code is present in unscoped `GetSourceFilterOptionsAsync()`;
- that same code is absent from `GetSourceFilterOptionsAsync(firstWorkOrderId)`;
- `GetSourceFilterOptionsAsync(firstWorkOrderId)` still contains the first Work Order’s work centre / process / item.

Cancelling the first order, which `Source_filter_options_and_exact_search_use_eligible_sources_only` already does, still covers the shared `EligibleSources` rule.

Prefer extending that existing test or adding one focused test next to it.

No tests for posting/cost math should be rewritten for this UX task.

## 24.2 Build verification

At minimum run:

```bash
dotnet build
```

for the affected solution/project set, then the targeted FG tests above.

If the repository's normal CI/test command is available, run it after the targeted tests.

## 24.3 Manual UI scenarios

### Scenario 1 — First source

1. New FG.
2. Confirm only source finder/results show.
3. SELECT an eligible source.
4. Confirm finder disappears and workspace appears.
5. Confirm selected Work Order is obvious.

### Scenario 2 — Additional source from same Work Order

1. In workspace click ADD SOURCE LOT.
2. Confirm popup opens immediately and can show loading skeleton.
3. Confirm Work Order cannot be changed.
4. Confirm WC/Process/Item filter options belong to the locked Work Order.
5. Select another source.
6. Confirm row is appended and popup closes.

### Scenario 3 — Same source split

1. Add a source.
2. Add the same source again.
3. Confirm the existing split-destination message remains.
4. Enter destinations on both rows.
5. Save within total available qty.
6. Confirm save succeeds.
7. Repeat with aggregate qty above source balance.
8. Confirm service rejects it.

### Scenario 4 — Warehouse/location dependency

1. Select Warehouse A and Location A1.
2. Change Warehouse to B.
3. Confirm Location clears immediately.
4. Confirm only B locations are available in that row’s location cell.

Daily Production’s in-grid combo binds a page-level list. FG location options are replaced per row inside `OnWarehouseChangedAsync`. If the grid cell does not rebind after that reload, refresh that row. Do not add a second edit-form model.

### Scenario 5 — Lot control

1. Add lot-controlled source.
2. Confirm destination lot defaults to source lot.
3. Confirm lot and expiry are editable.
4. Add non-lot source.
5. Confirm lot/expiry are not editable for it.

### Scenario 6 — Remove last row

1. Remove every line.
2. Confirm Work Order remains locked.
3. Confirm workspace remains.
4. Confirm Save is disabled.
5. Confirm the empty-grid copy points at ADD SOURCE LOT and CHANGE WORK ORDER, not “Find and select a production lot”.
6. Confirm ADD SOURCE LOT still works.
7. Confirm CHANGE WORK ORDER explicitly returns to selection.

### Scenario 7 — Edit existing draft

1. Open NEW saved FG in Edit.
2. Confirm workspace appears directly.
3. Confirm source finder/filter-options are not loaded/rendered until ADD SOURCE LOT is opened.
4. Confirm saved warehouse/location values render.
5. Confirm historical destination choices remain displayable where current code supports them.
6. Confirm inactive destination still fails service validation until replaced.

### Scenario 8 — Dirty navigation

1. Open source picker, filter and close without changing receipt payload.
2. Navigate away; confirm no dirty warning caused by picker-only actions.
3. Edit qty/header/line.
4. Navigate away.
5. Confirm NavigationLock warning appears.

### Scenario 9 — Permission routes

1. User with Access but without Add opens `/new`.
2. Confirm a visible denied/info state, not blank content.
3. User with Access but without Edit opens a NEW draft `/edit/{id}`.
4. Confirm safe View/redirection behavior (`replace: true` after a successful load).
5. Confirm no unauthorized Save control can be invoked.
6. Open `/edit/{id}` for a receipt that `GetAsync` cannot load (missing or failed).
7. Confirm the load error is shown. Confirm the finder is not shown. Confirm the access-denied card is not shown.

### Scenario 10 — View/post/correction

Verify:

- VIEW grid;
- EDIT DRAFT on NEW;
- POST;
- ROLLBACK with reason;
- REVERSED;
- CREATE CORRECTION.

No lifecycle behavior should change.

# 25. Recommended Implementation Order

Implement in this order to minimize risk.

## Step 1 — Reconfirm current branch

Before editing, confirm `production` is still at `a0108ade...` or compare any newer HEAD against:

- FG entry Razor/code-behind/CSS;
- FG interface/service/math;
- Daily Production entry Razor/code-behind;
- Material Issue entry Razor/code-behind;
- FG tests.

Do not blindly apply this plan if those files have materially changed.

## Step 2 — Add permission-safe state properties

Add:

- `LoadFailed`
- `EntryPermissionDenied`
- `MoreFiltersVisible`
- `SourcePickerVisible`
- `IsSelectingSource`
- `IsReceiptWorkspace`
- `CanAddSourceLot`

Keep the failed-`GetAsync` early return. Implement `/new` no-Add and `/edit` no-Edit safe handling before rearranging the body. Redirect edit-without-Edit only after a successful NEW load, with `_navigating` and `replace: true`.

## Step 3 — Split Razor into selection/workspace branches

Make the full-page finder disappear after Work Order lock.

Put `LoadFailed` first in the body switch, then View, then `EntryPermissionDenied`.

Verify Edit mode opens directly in workspace.

Add defensive fallback instead of a blank final `else`.

## Step 4 — Align initial filters

Implement Daily/IP-style primary + More/Fewer filters and async initial clear/reset behavior.

## Step 5 — Add Work-Order-scoped filter option overload

Update:

- `IProductionFinishedGoodReceiptService.cs`
- `ProductionFinishedGoodReceiptService.cs`

Keep existing unscoped call source-compatible and add the locked-WO overload.

Add/extend the focused service test. The current fixture has only one eligible Work Order, so the test must seed a second eligible final FG lot on another Work Order with a different work centre, process, or item before asserting scoped vs unscoped filter choices.

## Step 6 — Add context/meta card

Ensure selected Work Order remains obvious when finder disappears.

Remove the duplicate read-only Work Order field from the Receipt details card so the Work Order appears once.

## Step 7 — Convert editable destination cards to `DxGrid`

Port existing controls into `CellDisplayTemplate` editors without changing the save payload or introducing DxGrid row-edit/edit-form state.

Preserve warehouse → clear location → reload locations behavior. After a warehouse change, confirm the location cell shows only the new warehouse’s locations; refresh the row if the cell does not rebind.

## Step 8 — Fix last-line removal behavior

Keep Work Order locked after the last row is removed.

Replace the empty-grid copy so it points at ADD SOURCE LOT and CHANGE WORK ORDER. Save remains disabled with zero lines.

## Step 9 — Add ADD SOURCE LOT popup

Set popup visible before awaited loads.

Load Work-Order-scoped filter choices and source results lazily.

Reuse `AddSourceAsync()` and preserve duplicate-source destination splitting.

## Step 10 — Change Work Order reset

On confirmed change:

- clear rows/lock;
- reload global filter options;
- reload results;
- preserve header fields;
- let `Dirty` derive naturally from `ToRequest()`.

## Step 11 — Convert view lines to read-only grid

Use the same column language as edit mode. Key the view `DxGrid` on `FinishedGoodReceiptLine.SourceId`. Do not key view rows on `ProductionBalLotId` or source lot.

## Step 12 — Compact readiness

Remove standalone readiness cards and move messages into the line/workspace footer area.

Use the four compact outcomes from section 15: unsaved, posting disabled, readiness errors, and clear.

## Step 13 — CSS cleanup

Remove obsolete editable-line-card-only styles and add minimal grid/popup/readiness styles.

## Step 14 — Regression verification

Run build, targeted FG tests and all manual scenarios above.

# 26. Files Expected to Change

## Primary required changes

1. `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor`
2. `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor.cs`
3. `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor.css`
4. `ErpWeb.Core/Production/IProductionFinishedGoodReceiptService.cs`
5. `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.cs`
6. `ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs`

The service changes are read-only filter-option scoping only. They must not touch Save/Post/Rollback/Correction write semantics.

## Reference only / no planned logic change

7. `ErpWeb.Core/Production/FinishedGoodReceiptMath.cs`
8. `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor`
9. `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor.cs`
10. `ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor`
11. `ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor.cs`
12. `.agents/skills/erp-transaction-ui-standard/skill.md`

No database migration is expected.

# 27. Explicit Non-Goals

Do not use this UX alignment task to redesign:

- FG posting architecture;
- production balance costing;
- inventory costing;
- stock ledger;
- WIP/FG staging semantics;
- Work Order completion logic;
- correction accounting;
- UOM conversion rules;
- source eligibility rules;
- menu permissions;
- list-page behavior.

If a separate defect is discovered in those areas, handle it under a separate plan rather than expanding this UI task.

---

# 28. Final Approved Design

The final operator flow should be:

```text
NEW FG
  ↓
Find eligible production lot
  ↓ SELECT
Work Order locks
  ↓
Receipt workspace
  ├─ Work Order/source context
  ├─ Receipt details
  ├─ Destination DxGrid (CellDisplayTemplate editors)
  │    └─ ADD SOURCE LOT
  │         └─ popup visible immediately
  │         └─ filters/results scoped to same WO
  ├─ Compact readiness
  └─ SAVE DRAFT
       ↓
VIEW DRAFT
  ├─ Read-only grid
  ├─ EDIT DRAFT
  └─ POST
       ↓
POSTED
  ├─ ROLLBACK
  └─ after reversal → CREATE CORRECTION
```

Permission-safe routing surrounds the flow:

```text
failed GetAsync on /edit/{id} or /view/{id}
  → load-error state (not finder, not access-denied)

/new without Add
  → explicit denied/info state

/edit NEW without Edit
  → redirect to /view/{id} after successful load (_navigating, replace: true)
```

This design achieves the requested Daily/IP consistency while preserving the parts of FG that are genuinely different:

- multi-source final production lots;
- same-source destination splitting;
- destination warehouse/location/lot/expiry capture;
- one-Work-Order document lock;
- readiness checks;
- correction lifecycle;
- dirty-navigation protection;
- FG costing and stock posting;
- server-authoritative quantity/UOM conversion.

The plan is grounded in current repository source at:

`a0108adea9d6d6042586f39f8e41eea70b88d49d`

and explicitly accounts for the new Daily Production enhancement commit that followed the earlier FG-fix baseline.

This revision incorporates the review findings required before implementation: load-failed state ahead of permission/selection, four compact readiness outcomes, view-grid `SourceId` key, zero-line workspace copy, removal of the duplicate Work Order field, and a second eligible Work Order in the scoped filter-option test.

**Final status: APPROVED — implementation-ready against current `production` source after incorporating the review findings.**
