---
name: erp-ui-core
description: >-
  Shared non-negotiable UI engineering contract for mokth/net10projectTemplate ERP
  transaction pages. Defines code-behind separation, DevExpress usage, shared list-page
  behavior, theme/CSS ownership, permissions, feedback, responsive behavior, accessibility,
  server paging and review gates. Pair with a module-specific transaction UI skill.
---

# ERP UI Core Contract

## Scope

This is the common engineering and interaction contract for transaction UIs.

It does **not** decide the entry-page anatomy. That comes from the module skill.

Repository authority:

- repository: `mokth/net10projectTemplate`
- target branch: the branch requested by the user; otherwise inspect the active/current branch
- for production work, inspect `production` before coding

---

# 1. Before coding — mandatory evidence

Do not start from memory.

Read the target page and the canonical references named by its module skill.

In the implementation plan or work log, state:

```text
UI STANDARD EVIDENCE
Target module:
Target page(s):
Page archetype:
Closest live reference:
Shared component(s):
Shared/global CSS:
Route/mode model:
List actions:
Entry/workflow actions:
Editable statuses:
Permission codes:
Intentional deviations:
```

`Intentional deviations` must be `None` unless the business workflow genuinely requires one.

Aesthetic preference is not a valid deviation.

---

# 2. File and code ownership

For a normal transaction page, use:

```text
Xxx.razor
Xxx.razor.cs
Xxx.razor.css     # when page-specific styling is needed
```

Mandatory:

- `.razor` owns markup and bindings;
- `.razor.cs` owns page state, loading, navigation, event handlers and orchestration;
- `.razor.css` owns page-specific styling;
- service/domain classes own business validation and state transitions.

Do not add a large `@code` block to a transaction Razor page.

A tiny parameter-only block in a reusable presentation component is acceptable only when that component already follows that convention.

Do not put a `<style>` block into a transaction `.razor` page.

---

# 3. DevExpress is the default UI control set

Use the application's existing DevExpress Blazor controls:

- `DxFormLayout`
- `DxTextBox`
- `DxMemo`
- `DxComboBox`
- `DxDateEdit`
- `DxSpinEdit`
- `DxGrid`
- `DxPopup`
- `DxButton`
- `DxDropDownButton`
- `DxTabs`
- other existing DevExpress controls where appropriate

Reuse repository lookup components such as:

- `IvCodeComboBox`
- `IvStockMasterPicker`
- `IvBalLocPicker`
- existing module pickers

Do not introduce a Bootstrap table/form, MudBlazor component, raw HTML select, or another UI framework for a transaction screen when an established DevExpress/repository component already exists.

Plain HTML is fine for semantic wrappers, labels, compact metadata, empty states and other non-widget structure.

---

# 4. Theme and visual tokens

Authoritative shared theme:

`ErpWeb/wwwroot/css/site.css`

Prefer:

- `var(--bg)`
- `var(--surface)`
- `var(--text)`
- `var(--muted)`
- `var(--line)`
- `var(--accent)`
- `var(--navy)`
- `var(--danger)`
- `var(--field-label)`
- existing module aliases derived from those variables

Required fields should use the existing `required-field` pattern when the field is genuinely mandatory.

Do not:

- hardcode large white backgrounds;
- create a new page color palette;
- add a page-specific font;
- use color alone to communicate status;
- override DevExpress globally from a feature's isolated CSS.

The app uses Public Sans globally.

---

# 5. CSS ownership

## Reuse first

Use an existing shared class/component when it expresses the intended UI.

Current important shared styles:

- `ErpWeb/wwwroot/css/inventory-chrome.css`
- `ErpWeb/wwwroot/css/pr-popup-grid.css`
- `ErpWeb/wwwroot/css/site.css`

Current app loading is defined in:

- `ErpWeb/Components/App.razor`

## Isolated CSS

Use `.razor.css` for page-specific layout and domain-specific presentation.

Use a stable page/module prefix.

Do not copy a large shared framework into every isolated CSS file.

Do not add a new global stylesheet in an unrelated functional task.

If three or more pages need a genuinely new shared primitive, propose/extract it as an intentional UI refactor rather than continuing copy/paste divergence.

---

# 6. Shared transaction LIST contract

Unless the target module skill documents a real exception, a transaction list uses this order:

```text
Page
├─ feedback
├─ iv-hero
│  ├─ icon
│  ├─ module eyebrow
│  ├─ H1
│  ├─ useful chips
│  └─ one useful KPI/count
├─ iv-card
│  ├─ search
│  ├─ FILTER
│  ├─ loading skeleton OR
│  ├─ CommonDataGridEx desktop
│  └─ compact mobile list
├─ filter popup
└─ action confirmation popup(s)
```

Canonical global list chrome:

`ErpWeb/wwwroot/css/inventory-chrome.css`

The `iv-*` prefix on list pages is an existing shared implementation detail. Do not create `sa-list-*`, `po-list-*` or `pr-list-*` copies merely to rename the same visual structure.

---

# 7. List search and filtering

Place free-text search above the grid.

Use:

- meaningful placeholder naming real searchable fields;
- clear button;
- approximately 400 ms debounce for server search when the current family does so;
- FILTER popup for structured filters;
- draft filter state while popup is open;
- Clear and Apply;
- `Filtered` context chip when filters are active.

Do not show both the page search and `CommonDataGridEx` search box.

When using server/custom data:

- `ShowSearchBox="false"`
- `ShowFilterRow="false"`
- `ShowGroupPanel="false"`

Do not load everything and filter only in the browser to simplify implementation.

---

# 8. `CommonDataGridEx` contract for transaction lists

Default transaction list control:

`ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor`

Use a server/custom source for large transaction data.

Provide a stable, unique `GridKey`.

Typical settings:

```razor
<CommonDataGridEx ...
    CustomDataSource="@DataSource"
    ShowToolbarText="true"
    ShowResetLayoutButton="true"
    UseBuiltInExport="false"
    ShowSearchBox="false"
    ShowFilterRow="false"
    ShowGroupPanel="false"
    ... />
```

Do not duplicate REFRESH beside the grid. The shared component already owns REFRESH behavior.

Respect its persisted-layout model; do not add separate per-page grid-layout storage.

Use:

- explicit date formats;
- explicit quantity/amount formats;
- sensible widths;
- right alignment for numeric values;
- stable key field;
- audit columns where the surrounding module does so.

## 8.1 Grid readability and resizing standard

Shared list columns use `GridColumnSize` semantic sizing from
`ErpWeb.UI/Components/Common/DataGrid/GridColumnSizing.cs`. Explicit `Width` and
`MinWidth` values override the semantic preset; rendered `MinWidth` values are integer
pixels. Shared grids remain dense (`TextWrapEnabled="false"`), resizable, and must use a
stable explicit `GridKey`.

The shared layout contract uses the legacy `erp-grid-layout:{grid-key}` key only as a
one-time migration source. The active layout is stored under `{grid-key}:v2`; the first
migration removes stale widths, and `CommonDataGridEx` also removes non-persisted filter
criteria. Subsequent loads preserve user-resized widths. Reset clears both keys.

Direct desktop `DxGrid` entry, lookup, inquiry, and workflow grids should use
`ColumnResizeMode="GridColumnResizeMode.ColumnsContainer"`, keep text wrapping disabled
unless a page-specific reason exists, use CSS units on `Width`, and give important item,
document, reference, name, description, status, and amount columns useful `MinWidth`
values. Do not add a new persistence subsystem to direct grids unless one already exists.

---

# 9. List action rules

Toolbar actions are driven by:

1. user permission;
2. number of selected rows;
3. selected row/document status;
4. business eligibility.

Never enable a toolbar action only because the user has the permission.

Never copy another module's action set wholesale.

Selection handler must re-evaluate toolbar eligibility.

Row actions normally include:

- VIEW
- EDIT when eligible

View remains available for read-only/closed/historical documents unless the domain explicitly prevents access.

Edit must be rejected safely when the document is no longer editable.

---

# 10. Permission contract

Use the repository access-right pattern.

Typical code-behind:

```csharp
CanAdd = await AccessRights.CanAsync(MenuCode, PermissionCodes.Add);
CanEdit = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
```

Resolve workflow permissions that actually apply:

- Post
- Rollback
- Cancel
- Close
- Reopen
- Submit
- module-specific permissions

UI visibility/enabled state is not security.

Handlers and services must still enforce permission and state rules.

---

# 11. Feedback and long-running actions

Provide visible states for:

- initial loading;
- success;
- errors;
- validation errors;
- empty results;
- submitting/processing;
- confirmation for destructive or state-changing actions.

Use:

- `role="status"` for success/informational status;
- `role="alert"` for errors;
- dismissible feedback when the current pattern is dismissible;
- `IsSubmitting` or equivalent to prevent duplicate actions.

State-changing confirmation copy must explain the consequence.

Bad:

```text
Are you sure?
```

Better:

```text
Roll back 3 receipts? The posted inventory movement will be reversed.
```

---

# 12. New/Edit/View and workflow-state correctness

Route mode alone does not grant editability.

Compute explicit properties for:

- mode;
- current status;
- permission;
- historical/current revision;
- downstream-use restrictions;
- submitting state.

Do not leave controls editable in View mode.

If an Edit URL is opened after state changed, fail safely:

- redirect to View; or
- load read-only and show a clear reason.

Service/domain validation remains authoritative.

---

# 13. Entry line grids

For in-document lines, use `DxGrid` directly unless the module's live reference says otherwise.

Defaults:

- no list-page search box;
- no filter row for a small document line collection;
- explicit widths/min-widths;
- no text wrapping unless the current page needs it;
- numeric columns right-aligned;
- quantities commonly `n4`;
- money commonly `n2`;
- unit price/cost precision follows domain model/current page;
- stacked primary/secondary cell text when useful.

Mutation controls are visible only when editing is permitted.

Use icon titles/tooltips.

Do not fill View mode with disabled pencil/trash icons.

---

# 14. Popups

Use `DxPopup` and `common-popup` for normal modal interaction.

Data-entry popups:

- normally `CloseOnOutsideClick="false"`;
- use compact `DxFormLayout`;
- show validation inside the popup;
- scroll the body when tall;
- separate footer visually;
- Cancel/Close before primary action.

Do not put an entire transaction workflow in a modal when the module has a full-page workspace pattern.

---

# 15. Dirty-state and navigation safety

Editable transaction pages should track meaningful unsaved changes.

On Cancel/Back/navigation:

- do not silently discard significant edits;
- use the module's existing discard-confirmation pattern;
- clear dirty state after successful save.

Do not flag the document dirty while merely applying initial defaults unless the current page intentionally does so.

---

# 16. Responsive behavior

Desktop consistency is not enough.

At narrow widths:

- hero/titlebar must reflow;
- forms collapse via `ColSpanMd` or equivalent;
- action groups remain tappable;
- line grids must remain usable/scrollable;
- list page switches to compact mobile list where the current pattern does;
- popups stay within viewport and scroll internally.

Do not solve mobile problems by shrinking text below readable size.

---

# 17. Accessibility minimum

Every changed transaction page must have:

- correct `<PageTitle>`;
- one meaningful H1;
- decorative icons `aria-hidden="true"`;
- close icon `aria-label`;
- icon-only action title/tooltip;
- status not communicated only by color;
- `aria-busy` for meaningful loading containers where applicable;
- real interactive elements for tappable rows/actions.

---

# 18. Performance contract

For list pages:

- server paging/filtering for large transaction data;
- debounce search;
- bounded compact-mobile preview;
- no full table materialization.

For entry pages:

- avoid loading large lookup masters when an existing server lookup/picker exists;
- keep expensive recomputation behind explicit events or existing service methods;
- preserve cancellation/version guards already used by the current page family.

---

# 19. Change-scope rule

A UI feature is not permission for a broad refactor.

Do not:

- rename shared CSS just because its current name is imperfect;
- move shared components between namespaces during unrelated feature work;
- convert every neighboring page to a new pattern;
- redesign business workflow while standardizing visual presentation.

If the requested UI cannot be standardized without a shared refactor, identify that refactor separately.

---

# 20. Core rejection checklist

Reject the implementation if any applicable statement is true:

- transaction page contains a large new inline `@code` block;
- new page introduces a competing UI library;
- list page does not use the established shared list shell without justification;
- list grid duplicates search/filter already provided above it;
- list loads unbounded data client-side;
- unique `GridKey` is missing;
- View mode still edits;
- permissions are only cosmetic;
- state-changing action has no confirmation where peers use one;
- user can double-submit;
- empty state is blank/unexplained;
- CSS hardcodes a light-only surface;
- mobile behavior is ignored;
- implementation copied another module's workflow actions without evidence;
- module-specific skill was not applied.

---

# 21. Core Definition of Done

Before declaring complete:

- [ ] UI Standard Evidence Block exists.
- [ ] Correct module skill was applied.
- [ ] Current live canonical references were inspected.
- [ ] Razor/code-behind/CSS separation follows repository convention.
- [ ] DevExpress/repository components are reused.
- [ ] List behavior matches the shared list contract.
- [ ] Entry anatomy matches the target module contract.
- [ ] Permissions + state drive action availability.
- [ ] Service validation remains authoritative.
- [ ] Loading/error/empty/submitting states are present.
- [ ] Unsaved changes are handled safely.
- [ ] Responsive behavior was checked.
- [ ] Theme variables work in light/dark mode.
- [ ] No unrelated broad refactor was introduced.
