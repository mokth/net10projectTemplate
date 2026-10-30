---
name: erp-grid-row-actions-left
description: >-
  ERP grid row-action placement standard for mokth/net10projectTemplate. Use whenever
  creating, editing, standardizing or reviewing a transaction list, master list,
  inquiry or direct DxGrid with per-row View, Edit, Delete or similar actions.
  Put actions at the left edge and anchor them so horizontal scrolling never
  forces users to scroll right to open a document. Preserve the current page's
  security, workflow, business logic, data source and user grid preferences.
---

# ERP Grid Row Actions — Always on the Left

## Companion entry-page standard

For Save, Cancel, Close, Edit, sticky entry footers, and consistent DevExpress button colors, read **`.agents/skills/erp-entry-footer-standard/skill.md`**. Grid row actions stay **fixed on the left**; page-level entry actions stay **at the bottom-right**. They are two linked standards and must not be confused.

## Goal

On a list such as `/inventory/misc-receipt`, the user must be able to click **View**, **Edit**, or any existing per-row action **without scrolling horizontally to the far right**.

Correct desktop order (left to right):

```text
| ACTIONS [View] [Edit] | [Select] | Batch No | Date | Status | ... other columns ... |
^ always on the left     ^ optional
```

If a checkbox is required by the current list, keep it immediately beside the action column (the action column is the **leftmost** grid column). **The action column must stay visible while the user scrolls the data columns horizontally.** This is both a **column order** rule and a **fixed/frozen position** rule; `VisibleIndex="0"` alone is not sufficient.

For page types that have no row actions, do not manufacture them. This standard applies equally to transaction lists, master lists and inquiry grids that already have action buttons, and to direct entry/detail grids **only when they actually have per-row commands**. Do not confuse page-level New/Post/Rollback buttons with row actions.

## Source-of-truth workflow

1. Inspect the **current requested repository and branch**, especially `productionv2` when that branch is the target. Never rely on an old plan or screenshot as proof of current code.
2. For transaction screens read `.agents/skills/erp-transaction-ui-standard/skill.md`, `.agents/skills/erp-ui-core/skill.md`, and the correct module-specific skill.
3. Read `.agents/skills/devexpress-dxgrid-column-readability/skill.md` for sizing and persistence constraints.
4. Inspect the actual target page's `.razor`, `.razor.cs`, `.razor.css`, shared grid component, `GridKey`, and layout load/save path.
5. Determine whether it uses `CommonDataGridEx`, `CommonDataGrid` or a direct `DxGrid`. Make the smallest safe change in the owning component; avoid page-by-page duplicates when the shared component owns the actions.

## Verified repository references (productionv2, inspected 2026-10-30)

- `ErpWeb.UI/Inventory/Transactions/IvMiscReceiptList.razor` contains route `/inventory/misc-receipt` and uses `CommonDataGridEx` with `ActionButtons`, `allowSelect="true"`, and `GridKey="inv-misc-receipt-list"`.
- `ErpWeb.UI/Inventory/Transactions/IvMiscReceiptList.razor.cs` defines **VIEW** and **EDIT** row actions.
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor` currently renders the selection column and a `DxGridCommandColumn`; both use `VisibleIndex="0"`, and the action column is **not fixed to the left**. Do not presume that a `VisibleIndex` default fixes the real user's scrolling problem.
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs` restores and saves per-grid layouts through `Grid_LayoutAutoLoading` and `Grid_LayoutAutoSaving`; this can restore prior column order.
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGrid.razor` is a second shared grid with row actions and must be checked for the same requirement if used by target pages.

These paths and details are starting points, **not permanent assumptions**. Recheck them against current code before implementing.

## Mandatory UX behavior

1. **Position:** Action buttons belong in a dedicated leftmost column before the business-data columns. If selection checkboxes exist, place them beside the actions, not on the far right.
2. **Remain visible:** Use the native DevExpress `DxGridCommandColumn.FixedPosition="GridColumnFixedPosition.Left"` (or the matching action `DxGridDataColumn` where commands are custom templates). This anchors actions while horizontal scrolling.
3. **Stable order:** Set `VisibleIndex="0"` for the action column. Give the adjacent selection column an unambiguous visible index, normally `1`, when both are in the same fixed-left zone. Do not assign both columns `0`.
4. **Prevent accidental movement:** For non-optional actions use `AllowReorder="false"`; evaluate `ShowInColumnChooser="false"` and keep the action column visible so layout customization cannot make View/Edit inaccessible. Verify actual DevExpress behavior for the installed version.
5. **Compact but usable:** Size the column to fit the *existing* action icons (normally `View`/`Edit`), with readable hover tooltips/accessible names. Preserve widths/`MinWidth`, avoid clipping and avoid oversized blank columns. Do not add button text to every row unless the current UI pattern requires it.
6. **Respect permissions and status:** Preserve existing View/Edit/Delete eligibility, per-row handlers, navigation, confirmations and disabled states. A visual-only change must never make a prohibited operation possible.
7. **Stay responsive:** Check narrow desktop/tablet layout and the current mobile compact-list implementation. Do not introduce a frozen column wider than the available viewport; avoid creating an extra horizontal-scroll container.
8. **Minimal impact:** No inventory quantity, costing/FIFO/CostLayer, accounting, production WIP, posting/rollback, transaction status, database, service, or permission changes. No new component library or JavaScript sticky-position workaround.

## Recommended implementation — shared `CommonDataGridEx`

The exact markup must be adapted to the live component; **reuse its existing `CellDisplayTemplate`, action toolbar, callbacks, and action-width helpers unchanged**. The important settings are:

```razor
<Columns>
    @if (ActionButtons.Count > 0)
    {
        <DxGridCommandColumn Name="RowActions"
                             Caption="Actions"
                             VisibleIndex="0"
                             FixedPosition="GridColumnFixedPosition.Left"
                             AllowReorder="false"
                             ShowInColumnChooser="false"
                             NewButtonVisible="false"
                             EditButtonVisible="false"
                             DeleteButtonVisible="false"
                             Width="@GetGridActionColWidth()"
                             MinWidth="@GetGridActionColMinWidth()">
            <CellDisplayTemplate>
                @* KEEP the existing action toolbar / bindings here; do not replace it. *@
            </CellDisplayTemplate>
        </DxGridCommandColumn>
    }

    @if (allowSelect)
    {
        <DxGridSelectionColumn Width="60px"
                               MinWidth="48"
                               VisibleIndex="@(ActionButtons.Count > 0 ? 1 : 0)"
                               FixedPosition="GridColumnFixedPosition.Left"
                               AllowReorder="false" />
    }

    @* Keep the existing generated business columns and their sizing. *@
</Columns>
```

**Notes:**

- Keep `Width` as a CSS-unit string (`70px`), `MinWidth` as integer pixels.
- Keep `ColumnResizeMode="GridColumnResizeMode.ColumnsContainer"` and `TextWrapEnabled="false"` where already used.
- Fixed-left columns display in a separate zone before normal columns. `VisibleIndex` determines order **within a zone**. Do not renumber every business column just to reserve two fixed-left positions unless the real layout demonstrably requires it.
- If the page uses custom `DxGridDataColumn` action templates instead of `DxGridCommandColumn`, apply the same `FixedPosition`, `VisibleIndex`, and non-reorder logic to the actual actions column.
- A list with no selection checkbox does not need a dummy selection column.
- For a grid where every row has *more* actions, keep the existing actions and their proper state checks; validate resulting fixed-column width on smaller screens rather than silently deleting actions.

## Mandatory persisted-layout investigation

**This is the most likely regression area.** `CommonDataGridEx` uses per-grid `GridKey` and v2 layout persistence. Older stored layouts may place a command column at the far right or override its `VisibleIndex`.

Before marking the page fixed:

1. Test with **no saved layout**, a **legacy saved layout**, and an **existing v2 saved layout**.
2. Confirm `LayoutAutoLoading` cannot restore the action column to the far right or hide it. Test an actual user layout that previously moved the action/selection column.
3. If persisted layout overrides the enforced contract, implement the **smallest targeted normalization** in the existing shared layout path, limited to the **command and adjacent selection column**. Preserve every business data column's visible order, width, visibility, sort, group, page-size and other user preferences.
4. Investigate how the current DevExpress `GridPersistentLayoutColumn` identifies command and selection columns (including `ColumnType`; distinguish them from data columns). Do **not** assume the persistent model has a `FixedPosition` property — current DevExpress 26.1 documentation lists position/visibility/width but not fixed position as persisted column fields.
5. Set the markup's native `FixedPosition` regardless of layout normalization. An index-only patch does **not** guarantee a sticky column during scrolling.
6. Do **not** delete all user layouts, strip all widths again, bump the shared layout schema, or reset unrelated grids to solve one action-column issue. If a change to load/save behavior is essential, test non-transaction grids that share this component and reset-layout behavior. Avoid repeated save/load loops.
7. If the installed DevExpress version behaves differently from documentation, inspect its actual APIs and validate in the running application; prefer the tested native API over speculative reflection, custom JavaScript or CSS sticky rules.

## `CommonDataGrid` and direct `DxGrid`

For `CommonDataGrid`, follow the same left-anchored command-column principle, but inspect its separate persistence code and current row-action/selection ordering before modifying it. Do not assume that fixing `CommonDataGridEx` automatically fixes this component.

For direct `DxGrid` transaction entry line grids: use the rule **only when row commands exist**. Keep Delete/Edit hidden in View mode; keep any existing row-level command eligibility, including only allowing mutations in applicable edit modes. Fixed left applies to the command column, not automatically to every data column.

## UI review checklist — must pass

- [ ] The list opens with actions on the **left** (fresh layout).
- [ ] With a wide grid, horizontal scroll to the far right: View/Edit remain visible and clickable on the left.
- [ ] Action icons are the first visible grid column; optional selection checkbox is adjacent.
- [ ] On an older persisted layout and on a v2 saved layout, View/Edit do not jump to the right after reload.
- [ ] User-customized data column widths/order remain intact after rollout, refresh, navigation and new login.
- [ ] RESET LAYOUT retains left-anchored actions and restores expected default business columns.
- [ ] Sort, filter, selection, data paging, export, virtual/server data source and loading remain unchanged.
- [ ] Tooltips/accessible names show which action an icon invokes; no clipped or overlapping buttons.
- [ ] VIEW, EDIT and other row operations obey their original permissions and status rules.
- [ ] Mobile compact list and narrow viewport remain usable; frozen area is not too wide.
- [ ] No new database schema, costing/quantity logic, transaction state changes, posting logic or permission bypass.

## Evidence to return with an implementation/review

```text
GRID ROW ACTION PLACEMENT EVIDENCE
Branch and commit inspected:
Pages inspected:
Shared grid component:
Action column type:
Action VisibleIndex:
Action FixedPosition:
Selection VisibleIndex and fixed behavior:
Action icons/width/tooltips:
GridKey and persisted-layout keys:
Fresh, legacy and v2 layout test results:
Horizontal-scroll test result:
Mobile/narrow-screen test result:
Permissions/status/handlers unchanged:
Files changed:
Unexpected deviations or remaining risks:
```

**Acceptance criterion:** An ERP user can open View/Edit from the first (left) part of a transaction row at any horizontal scroll position, with no need to scroll to the far right, while all existing business behavior and saved business-column preferences remain correct.
