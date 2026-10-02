---
name: erp-transaction-ui-standard
description: >-
  Standard look-and-feel and interaction pattern for ERP transaction list and entry pages
  in mokth/net10projectTemplate. Use whenever creating, redesigning, or reviewing a UI
  that has a transaction list page plus new/edit/view entry page. The production Inventory
  transaction screens are the canonical reference.
---

# ERP Transaction UI Standard

## Purpose

This skill defines the mandatory UI/UX house style for transaction screens in this ERP.

Use it whenever an agent creates or changes a module that has:

- a transaction **List page**; and/or
- a transaction **Entry page** with **New / Edit / View** modes.

The objective is consistency. A new Sales, Purchase, Inventory, Production, Planning, Accounting, HR, or other ERP transaction page must feel like it belongs to the same product.

Do **not** invent a new visual language when an existing Inventory transaction pattern already solves the problem.

---

# 1. Canonical Repository References

Target repository and branch:

- Repository: `mokth/net10projectTemplate`
- Branch: `production`

Before implementing a new transaction UI, read the current production versions of the following files.

## Primary list-page references

Use these as the main reference:

- `ErpWeb.UI/Inventory/Transactions/IvStockTransferList.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransferList.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransferList.razor.css`

Cross-check against:

- `ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor`
- `ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor.css`
- `ErpWeb.UI/Inventory/Transactions/IvStockAdjustmentList.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockAdjustmentList.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvStockAdjustmentList.razor.css`

## Primary entry-page references

Use these as the main reference:

- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor.css`

Cross-check against:

- `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor`
- `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor.css`
- `ErpWeb.UI/Inventory/Transactions/IvStockAdjustment.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockAdjustment.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvStockAdjustment.razor.css`

## Shared infrastructure references

Also inspect:

- `ErpWeb/wwwroot/css/inventory-chrome.css`
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor`
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs`
- `ErpWeb/Components/App.razor`

`inventory-chrome.css` is already globally loaded by `App.razor` and is the authoritative visual reference for Inventory list chrome.

> Important: always inspect the current `production` branch before coding. This skill describes the pattern, but the live repository is authoritative when the implementation has evolved.

---

# 2. Core Principle

**Preserve business behavior; standardize presentation and interaction.**

A new page may have different fields, validation, workflow states, totals, actions, and domain rules. That is expected.

However, these aspects should remain visually and behaviorally consistent:

- page shell;
- hero/header area;
- title hierarchy;
- status chips;
- cards;
- search/filter placement;
- grid behavior;
- row actions;
- loading/empty states;
- popup layout;
- footer actions;
- New/Edit/View modes;
- permission handling;
- confirmation behavior;
- responsive layout;
- success/error feedback.

Do not copy Inventory-specific business rules into another module unless they actually apply.

---

# 3. Non-Negotiable Rules

1. **Do not design from scratch.** Start from the closest Inventory transaction reference.
2. **Do not use a visually different page shell** for a normal ERP transaction.
3. **Do not put business entry directly inside the list grid** when the transaction is naturally header/detail based. Use List -> Entry.
4. **Do not create separate New/Edit/View page files** unless there is a compelling technical reason. Prefer one entry component with route mode.
5. **Do not allow POSTED/CANCELLED/closed documents to silently become editable.** Editing state must follow business rules.
6. **Do not expose unauthorized actions.** Use the repository permission pattern.
7. **Do not load an entire large transaction table into memory for the list UI.** Use the existing server-side/custom data-source approach.
8. **Do not create one-off colors, font sizes, shadows, radii, or spacing** that conflict with the reference pages.
9. **Do not rely only on color to communicate status.** Status text must be visible.
10. **Do not duplicate a giant CSS framework inside every new page.** Reuse existing shared styles where appropriate and keep page CSS scoped to page-specific needs.
11. **Do not make desktop-only UI.** List and entry pages must remain usable on narrow screens.
12. **Do not bypass service-layer validation because the UI already validates.** UI validation is convenience; service/domain validation remains authoritative.

---

# 4. Standard List Page Pattern

A transaction list page should follow this visual order:

```text
Page
├─ Success / error message area
├─ Hero
│  ├─ Module icon
│  ├─ Module eyebrow
│  ├─ Page title
│  ├─ Context chips
│  └─ KPI / total count
├─ Main card
│  ├─ Search + Filter toolbar row
│  ├─ Loading skeleton OR
│  ├─ Desktop CommonDataGridEx
│  └─ Mobile compact list
├─ Filter popup
└─ Confirmation popup(s)
```

## 4.1 Page shell

Inventory list pages use:

```razor
<div class="iv-page">
    ...
</div>
```

Within Inventory, reuse the shared `iv-*` classes from `inventory-chrome.css` unless the existing module pattern requires otherwise.

For another module, class names may use that module/page prefix, but the **visual measurements and structure must match** the Inventory standard. Do not invent a different visual system merely to avoid the `iv-` prefix.

## 4.2 Status and error messages

Place status/error messages above the hero.

Pattern:

```razor
@if (!string.IsNullOrWhiteSpace(StatusMessage))
{
    <div class="iv-toast iv-toast--ok" role="status">
        <span>@StatusMessage</span>
        <button type="button" class="iv-toast__close" @onclick="DismissStatus" aria-label="Dismiss">×</button>
    </div>
}

@if (!string.IsNullOrWhiteSpace(ErrorMessage))
{
    <div class="iv-toast iv-toast--err" role="alert">
        <span>@ErrorMessage</span>
        <button type="button" class="iv-toast__close" @onclick="DismissError" aria-label="Dismiss">×</button>
    </div>
}
```

Rules:

- success/info message uses `role="status"`;
- error uses `role="alert"`;
- user can dismiss messages;
- messages must be concise and actionable.

## 4.3 Hero

The hero is mandatory for standard transaction pages.

Structure:

```razor
<header class="iv-hero">
    <div class="iv-hero__mark" aria-hidden="true">
        <i class="fa-solid fa-..."></i>
    </div>

    <div class="iv-hero__copy">
        <p class="iv-eyebrow">Module Name</p>
        <h1 class="iv-title">Transaction Name</h1>
        <div class="iv-chips">
            <span class="iv-chip">...</span>
        </div>
    </div>

    <div class="iv-hero__kpi">
        <span>...</span>
        <strong>...</strong>
    </div>
</header>
```

Hero behavior:

- icon visually identifies the function;
- eyebrow names the module, e.g. `Inventory`, `Sales`, `Purchase`, `Production`;
- H1 contains the transaction name;
- chips show useful current context, not decorative text;
- KPI shows one high-value summary such as total documents, batches, document total, quantity, etc.;
- do not overload the hero with many buttons.

Reference measurements from the Inventory chrome:

- page vertical gap: `14px`;
- hero/card radius: `16px`;
- hero padding: approximately `18px 22px`;
- hero icon: `52px x 52px`, `16px` radius;
- title size: approximately `1.3rem` to `1.65rem` responsive;
- chips: compact pill style;
- use theme variables (`--text`, `--muted`, `--line`, `--surface`, `--accent`, `--navy`) rather than fixed page backgrounds.

## 4.4 Search + Filter row

Search should appear on the left and occupy available width.

Filter button appears beside it.

Pattern:

```razor
<div class="iv-toolbar-row">
    <div class="iv-toolbar-row__search">
        <DxTextBox Text="@SearchText"
                   TextChanged="OnSearchTextChanged"
                   NullText="Search ..."
                   ClearButtonDisplayMode="DataEditorClearButtonDisplayMode.Auto"
                   CssClass="w-100" />
    </div>

    <DxButton RenderStyle="ButtonRenderStyle.Secondary"
              Text="FILTER"
              IconCssClass="fa-solid fa-filter"
              Click="@OpenFilterPopup" />
</div>
```

Rules:

- search placeholder should name realistic searchable fields;
- use clear button;
- use approximately **400 ms debounce** for server search, consistent with current transaction lists;
- advanced criteria belong in the Filter popup, not in a permanently crowded toolbar;
- show a `Filtered` chip in the hero when filters are active.

## 4.5 Loading state

Do not immediately show an empty grid while bootstrapping.

Use the skeleton pattern:

```razor
<div class="iv-skeleton" aria-busy="true">
    <div class="iv-skeleton__line iv-skeleton__line--lg"></div>
    <div class="iv-skeleton__line iv-skeleton__line--md"></div>
    <div class="iv-skeleton__line"></div>
</div>
```

## 4.6 Desktop grid

Prefer the existing `CommonDataGridEx<T>` for standard list pages.

Typical configuration:

```razor
<CommonDataGridEx T="TRow"
                  Columns="@Columns"
                  Buttons="@Buttons"
                  ActionButtons="@ActionButtons"
                  KeyName="@nameof(TRow.Key)"
                  GridKey="module-transaction-list"
                  Title="Transactions"
                  CustomDataSource="@DataSource"
                  ShowToolbarText="true"
                  ShowResetLayoutButton="true"
                  UseBuiltInExport="false"
                  ShowSearchBox="false"
                  ShowFilterRow="false"
                  ShowGroupPanel="false"
                  allowSelect="true"
                  OnGridInstance="@OnGridInstance"
                  OnSelectionsEventHandle="@OnSelectionsEvent"
                  OnButtonEventHandle="@OnButtonClick"
                  OnActionEventHandle="@OnActionClick" />
```

Rules:

- `GridKey` must be stable and unique per page so saved layout state is not shared accidentally;
- use repository grid configuration classes (`GridColumnData`, `ButtonInfo`, `SelectedButtonInfo<T>`);
- use proper numeric/date display formats;
- quantities/amounts should align right;
- date normally follows existing ERP display convention such as `dd/MM/yyyy`;
- include audit columns where the module convention requires them;
- use server-side paging/filtering for large lists;
- use the built-in `REFRESH` behavior of `CommonDataGridEx` rather than adding a duplicate refresh control.

## 4.7 List toolbar actions

Use only actions that genuinely apply to the transaction.

Inventory transaction reference order is:

```text
NEW -> POST -> ROLLBACK -> CANCEL -> DELETE
```

Typical style mapping:

| Action | Style | Typical icon |
|---|---|---|
| NEW | primary | `fas fa-plus` |
| POST / approve-like positive transition | success | `fas fa-check` |
| ROLLBACK | warning | `fas fa-rotate-left` |
| CANCEL | warning | `fas fa-ban` |
| DELETE | danger | `far fa-trash-alt` |

Do not add an action just because it exists on an Inventory page.

If the new transaction only needs NEW + DELETE, use only those.

If it has workflow actions such as RELEASE, COMPLETE, CLOSE, REOPEN, APPROVE, etc., position them between NEW and destructive actions using the same semantic style logic.

## 4.8 Row actions

Preferred row actions:

```text
VIEW, EDIT
```

Rules:

- View should remain available for non-editable documents;
- Edit must check permission and document state;
- when a user requests Edit on a state that is no longer editable, show a clear message and navigate/view safely rather than allowing partial editing;
- icon-only row controls must have a useful tooltip/title.

## 4.9 Permission pattern

Resolve permissions during page initialization using the repository access-right service.

Example pattern:

```csharp
CanAdd = await AccessRights.CanAsync(MenuCode, PermissionCodes.Add);
CanEdit = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
CanDelete = await AccessRights.CanAsync(MenuCode, PermissionCodes.Delete);
```

For workflow transactions, also resolve applicable permissions such as:

- `Post`
- `Rollback`
- `Cancel`
- module-specific permissions.

Buttons should be enabled/visible consistently with the repository convention.

Never rely solely on the disabled button. The event handler and service must still enforce authorization/state rules.

## 4.10 Selection + bulk operations

For bulk transitions such as POST, ROLLBACK, CANCEL, DELETE:

- require at least one selected row;
- validate every selected row is eligible;
- obey any repository batch-size limit such as `IvPostingLimits.MaxPostSelection` where applicable;
- show a confirmation popup;
- disable repeated submission with `IsSubmitting`;
- refresh the grid after success or partial success;
- provide a useful error when only some items were processed.

## 4.11 Filter popup

Use `DxPopup` + `common-popup` + `DxFormLayout`.

Typical behavior:

- `CloseOnOutsideClick="false"` for filters where accidental dismissal would lose draft changes;
- use draft filter variables while popup is open;
- `Clear` resets filters;
- `Apply` commits filters and reloads the grid;
- filter sections should fit within a reasonable popup width (Inventory reference commonly uses about `480px` for simple filters).

Keep the filter popup focused. Do not reproduce every grid column as a filter unless users genuinely need it.

## 4.12 Confirmation popup

Destructive/state-changing actions must use a clear confirmation dialog.

Preferred order:

```text
Cancel | Primary/Warning/Danger action
```

Semantic style:

- normal positive operation -> Primary/Success where appropriate;
- rollback/cancel -> Warning;
- permanent delete -> Danger.

The message should state what will happen, not merely `Are you sure?`.

Example:

```text
Roll back 3 selected transfers? Stock will be restored by the posted quantities.
```

## 4.13 Mobile compact list

The Inventory standard intentionally uses a compact list on narrow screens instead of forcing the full desktop grid.

Structure:

```razor
<div class="iv-list-compact">
    @foreach (var row in CompactRows)
    {
        <button type="button" class="iv-list-compact__item" ...>
            <span class="iv-list-compact__code">Primary identifier</span>
            <span class="iv-list-compact__desc">Useful secondary details</span>
            <div class="iv-list-compact__meta">
                <span class="iv-status ...">Status</span>
                <span class="iv-chip">...</span>
            </div>
        </button>
    }
</div>
```

Rules:

- compact row must be tappable;
- show identifier, date/description, status, and one or two useful metrics;
- do not mirror every desktop grid field;
- use the same active search/filter state as desktop;
- do not fetch an unbounded dataset only to render the mobile preview.

---

# 5. Standard Entry Page Pattern

A transaction entry page should follow this visual order:

```text
Entry page
├─ Success / error messages
├─ Loading card OR
├─ Hero
│  ├─ Module icon
│  ├─ Module eyebrow
│  ├─ New/Edit/View title
│  ├─ mode/document/status chips
│  └─ KPI/document total
├─ Header form card
├─ Detail/line card
│  ├─ section title
│  ├─ Add/Add-from-source action
│  ├─ detail grid
│  └─ empty state
├─ Sticky footer
│  ├─ hint / line count / total
│  └─ Close/Edit OR Cancel/Save
├─ Detail edit popup(s)
├─ picker popup(s)
└─ discard/confirm popup(s)
```

---

# 6. New / Edit / View Routing Standard

Prefer one entry component with mode routes.

Pattern:

```razor
@page "/module/transaction/{Mode:regex(^(new|edit|view)$)}"
@page "/module/transaction/{Mode:regex(^(new|edit|view)$)}/{Id:int}"
```

Equivalent key types may be used when the transaction does not use an integer ID.

Code-behind should make the mode explicit:

```csharp
protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
protected bool IsViewMode => !IsNewMode && !IsEditMode;

protected string PageHeading => IsNewMode
    ? "New ..."
    : IsEditMode
        ? "Edit ..."
        : "View ...";

protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";
```

Do not infer editability from the route alone. Document status and permission must also be considered.

---

# 7. Entry Editability Rules

Use explicit computed properties.

Pattern:

```csharp
protected bool CanEditDocument => (IsNewMode || IsEditMode) && !IsViewMode;

protected bool CanEditFromView =>
    IsViewMode
    && CanEditPermission
    && DocumentStatusAllowsEditing;

protected bool CanSave =>
    CanEditDocument
    && !IsSubmitting
    && RequiredEntryConditionsAreMet;
```

Rules:

- View mode is read-only;
- New mode can edit;
- Edit mode can edit only when current document state allows it;
- if an Edit URL is opened for a now-noneditable document, redirect/navigate to View or otherwise fail safely;
- status transition rules belong in the service/domain layer too.

---

# 8. Entry Hero

Entry hero should use the same visual language as the list hero.

Chips should typically show:

- Mode: New / Edit / View;
- document/batch number;
- transaction subtype when meaningful;
- line count;
- document status.

Example concept:

```text
[Edit] [Batch 10234] [Transfer] [4 lines] [NEW]
```

The KPI should show the most useful document-level number, for example:

- Document total;
- Net value impact;
- Total quantity;
- Planned output;
- Total cost;
- another clearly meaningful metric.

Do not use a KPI simply to fill space.

---

# 9. Header Form Card

Use a compact `DxFormLayout` inside a bordered card.

Typical transaction header fields:

- transaction/document date;
- status (read-only);
- reference number;
- transaction type;
- warehouse/vendor/customer/work order/etc.;
- remark.

Rules:

- use `SizeMode="SizeMode.Small"` unless the current module has a justified exception;
- use responsive `ColSpanMd` layout;
- date display should match ERP date convention;
- status should normally be read-only;
- fields in View mode must not be editable;
- use `maxlength` where the database/domain has a real limit;
- use clear `NullText` placeholders;
- required fields should visually use the repository required-field pattern where available.

Do not make the header form excessively tall. Transaction detail should remain visible without unnecessary scrolling.

---

# 10. Detail / Line Card

Header/detail transactions should have a dedicated line card.

Pattern:

```razor
<section class="xx-card xx-lines">
    <div class="xx-lines__head">
        <div>
            <p class="xx-eyebrow">Lines</p>
            <h2 class="xx-lines__title">Transaction items</h2>
        </div>

        @if (CanEditDocument)
        {
            <DxButton RenderStyle="ButtonRenderStyle.Primary"
                      Text="Add line"
                      IconCssClass="fa-solid fa-plus"
                      ... />
        }
    </div>

    <DxGrid ...>
        ...
    </DxGrid>
</section>
```

The primary line-add action belongs in the line card header.

Examples:

- `Add line`
- `Add item`
- `Add from PO`
- `Add from SO`
- `Add from BOM`
- `Add operation`

Use the action wording that matches the domain.

---

# 11. Detail Grid Standard

Recommended baseline:

```razor
<DxGrid Data="@Lines"
        KeyFieldName="@nameof(LineVm.LineNo)"
        ShowFilterRow="false"
        ShowGroupPanel="false"
        ShowSearchBox="false"
        TextWrapEnabled="false"
        PageSize="20"
        ColumnResizeMode="GridColumnResizeMode.ColumnsContainer"
        CssClass="xx-line-grid"
        RowDoubleClick="@OnLineRowDoubleClick">
```

Rules:

- do not put unnecessary list-page search/filter UI inside a small document line grid;
- use explicit widths for compact code/date/qty columns;
- use minimum width for descriptive text columns;
- right-align quantity, price, cost, and amount;
- use appropriate precision (`n2`, `n4`, etc.) according to domain data;
- use stacked primary/secondary text in one cell where this materially improves readability;
- use totals in `TotalSummary` when useful;
- row double-click may edit only when document is editable.

## 11.1 Row edit/delete actions

Only show row mutation controls while the document is editable.

Pattern:

```razor
@if (CanEditDocument)
{
    <DxGridCommandColumn ...>
        <CellDisplayTemplate>
            <div class="xx-row-actions">
                <DxButton RenderStyle="ButtonRenderStyle.Secondary"
                          RenderStyleMode="ButtonRenderStyleMode.Text"
                          IconCssClass="fa-solid fa-pen"
                          title="Edit" ... />

                <DxButton RenderStyle="ButtonRenderStyle.Danger"
                          RenderStyleMode="ButtonRenderStyleMode.Text"
                          IconCssClass="fa-solid fa-trash"
                          title="Remove" ... />
            </div>
        </CellDisplayTemplate>
    </DxGridCommandColumn>
}
```

Do not display disabled edit/delete clutter in View mode when the Inventory pattern would hide those controls.

---

# 12. Empty State

Do not leave a blank grid as the only explanation.

Use an empty state with:

- icon;
- short heading;
- one-sentence explanation;
- primary action when editable.

Concept:

```text
[icon]
No lines yet
Add at least one item to continue.
[Add line]
```

In View mode, remove the add CTA and use read-only explanatory copy.

---

# 13. Line Editing and Picker Popups

For complex detail lines, prefer a popup editor instead of making the grid itself a large inline editor.

Popup standard:

- `DxPopup`;
- `CssClass="xx-popup common-popup"`;
- `CloseOnOutsideClick="false"` for data entry;
- use `DxFormLayout`;
- group related fields with `DxFormLayoutGroup` when useful;
- popup body scrolls when tall;
- popup footer remains visually separate;
- secondary Cancel/Close first, primary Add/Update last.

Typical widths seen in Inventory:

- simple popup: around `420-640px`;
- richer source picker: around `800-900px`.

Choose the smallest width that comfortably fits the content.

Do not create full-screen popups for small line forms.

---

# 14. Sticky Entry Footer

Entry footer is mandatory for a standard transaction page.

It contains:

- a useful hint/summary on the left;
- primary navigation/action buttons on the right.

## View mode

Preferred order:

```text
Close | Edit
```

`Edit` is shown only when permission and document state allow it.

## New/Edit mode

Preferred order:

```text
Cancel | Save
```

Save button:

- Primary style;
- floppy-disk icon is consistent with current pages;
- changes label to `Saving…` while submitting where current pattern uses it;
- disabled when `CanSave == false`.

Typical footer hint examples:

```text
4 lines · Total 12,430.00
```

or

```text
Add at least one line to save this transaction.
```

Rules:

- footer should stay accessible while scrolling;
- on small screens, stack controls cleanly;
- do not place unrelated workflow actions beside Save unless the page's established domain pattern explicitly requires it.

---

# 15. Unsaved Changes / Discard Pattern

Track whether the document has been changed.

Use an `_isDirty`-style flag or equivalent.

Cancel behavior should not immediately discard meaningful edits.

Typical logic:

```csharp
if (IsEditMode || _isDirty || (IsNewMode && Lines.Count > 0))
{
    ConfirmDiscardVisible = true;
    return;
}

Navigation.NavigateTo(ListRoute);
```

Discard popup should clearly offer:

```text
Keep editing | Discard changes
```

or equivalent wording matching the current page standard.

After a successful Save, clear dirty state before navigating.

---

# 16. Save Pattern

Save handler should follow the existing structure:

1. return if already submitting or document cannot edit;
2. determine Add/Edit permission from mode;
3. check access rights;
4. validate minimum UI requirements;
5. set `IsSubmitting = true`;
6. clear stale errors where appropriate;
7. map header + lines into service request DTO;
8. call `SaveNewAsync(...)` or `UpdateAsync(...)`;
9. on success, clear dirty state and navigate to list or appropriate View page;
10. on failure, show service error;
11. in `finally`, reset `IsSubmitting`.

Do not duplicate domain validation logic excessively in the component. The service remains authoritative.

---

# 17. Visual Language

## 17.1 Typography

The app globally uses `Public Sans`.

Do not introduce another page-specific font.

Use hierarchy matching Inventory:

- eyebrow: small, uppercase, muted, letter-spaced;
- page title: strong but compact;
- section title: around `1rem`;
- labels: compact, about `0.75rem`;
- secondary metadata: muted and smaller.

## 17.2 Surface and borders

Reference language:

- light surface card;
- 1px border using theme line color;
- 16px outer radius;
- minimal/no heavy box shadows;
- subtle accent gradient in hero only;
- generous but compact ERP spacing.

The target is professional and information-dense, not a marketing dashboard.

## 17.3 Colors

Use theme variables first:

```css
var(--text)
var(--muted)
var(--line)
var(--surface)
var(--accent)
var(--navy)
var(--danger)
var(--warning)
var(--bg)
```

Semantic colors:

- green/positive -> active/success/posted where appropriate;
- amber -> hold/warning/rollback/cancel-like caution;
- red -> errors/destructive actions;
- accent blue -> normal primary actions.

Do not hardcode large light backgrounds that break dark theme.

## 17.4 Motion

The Inventory page uses a subtle short fade/translate entrance and skeleton shimmer.

If adding motion:

- keep it subtle;
- respect `prefers-reduced-motion`;
- no decorative bouncing, sliding panels, or unnecessary transitions.

---

# 18. Responsive Rules

## List page

At narrow width:

- hero becomes two-column icon + title area;
- KPI moves below and aligns left;
- desktop data grid is hidden;
- compact mobile list is shown;
- cards remain full width;
- filter popup remains usable.

## Entry page

At narrow width:

- hero KPI moves below title;
- `DxFormLayout` naturally collapses according to spans;
- sticky footer stacks vertically;
- footer buttons remain tappable;
- popup content scrolls instead of overflowing the viewport.

Never solve mobile layout by shrinking text until it is unreadable.

---

# 19. Accessibility Rules

Minimum requirements:

- `<PageTitle>` must reflect the current page/mode;
- H1 page title must exist;
- decorative icons use `aria-hidden="true"`;
- toast close buttons have `aria-label="Dismiss"`;
- success message uses `role="status"`;
- error message uses `role="alert"`;
- icon-only buttons have `title`/tooltip text;
- clickable compact mobile rows use an actual button or accessible interactive element;
- do not communicate meaning with color alone;
- loading skeleton/container should use `aria-busy="true"` where appropriate.

---

# 20. CSS Rules for New Pages

## Prefer reuse over duplication

For Inventory list pages, reuse `inventory-chrome.css` and `iv-*` classes when possible.

For entry pages, the current production screens often use a page-specific prefix (`mr-*`, `tr-*`, `sa-*`, etc.) but intentionally preserve the same visual metrics.

For a new page:

1. reuse an existing shared class when it correctly expresses the pattern;
2. use a page/module prefix for genuinely page-specific styling;
3. keep the visual values aligned with Inventory references;
4. do not copy hundreds of lines of CSS and then casually diverge them;
5. if several new screens require the same new style, consider extracting a shared class rather than multiplying page copies.

Do not perform a broad CSS refactor as part of an unrelated feature unless explicitly requested.

---

# 21. Component Structure Convention

For a normal transaction pair, prefer:

```text
Feature/
├─ XxxList.razor
├─ XxxList.razor.cs
├─ XxxList.razor.css
├─ XxxEntry.razor          // or Xxx.razor if that is module convention
├─ XxxEntry.razor.cs
└─ XxxEntry.razor.css
```

Match the naming convention of the target module rather than forcing `Entry` into a module that consistently omits it.

Keep:

- markup in `.razor`;
- behavior/state/navigation in `.razor.cs`;
- page-specific styles in `.razor.css`.

Avoid giant `@code` blocks in `.razor` when the module uses code-behind.

---

# 22. Agent Implementation Workflow

When asked to build a new list + entry UI, the agent MUST follow this sequence.

## Step 1 — Inspect the target module

Identify:

- existing folder and naming convention;
- menu code;
- permission codes;
- route convention;
- service APIs;
- status model;
- primary key/document number;
- DTO/view models;
- any similar pages already in the same module.

## Step 2 — Inspect Inventory references

At minimum read:

- `IvStockTransferList.razor/.cs/.css`;
- `IvStockTransfer.razor/.cs/.css`;
- `inventory-chrome.css`;
- `CommonDataGridEx`.

Then inspect `GoodsReceipt` or `StockAdjustment` when the new transaction resembles those flows more closely.

## Step 3 — Define state model before markup

Write down:

- New/Edit/View behavior;
- editable statuses;
- list actions;
- row actions;
- line actions;
- save eligibility;
- filter fields;
- confirmation actions;
- mobile compact-row information.

Do not start by randomly placing controls.

## Step 4 — Build list shell first

Implement:

- page authorization;
- toasts;
- hero;
- search/filter toolbar;
- custom data source;
- desktop grid;
- compact mobile list;
- action permissions;
- filter popup;
- confirmation popup.

## Step 5 — Build entry shell

Implement:

- routes;
- New/Edit/View state;
- permission/state checks;
- hero;
- header form;
- detail grid;
- line/picker popup;
- empty state;
- sticky footer;
- discard confirmation;
- save flow.

## Step 6 — Validate consistency

Compare the new UI side-by-side with the Inventory transaction pages.

The new screen should clearly look like the same ERP even when the domain is different.

---

# 23. Common Mistakes to Reject During Review

Reject or revise an implementation when it does any of the following:

- plain H1 + grid with no ERP hero/card structure;
- toolbar buttons scattered in unrelated locations;
- search box duplicated inside `CommonDataGridEx` and above it;
- filters permanently consuming a large section of the list page;
- full desktop grid forced onto phone width;
- new page uses Bootstrap table while neighboring pages use DevExpress grid;
- entry page has different files/routes for New and Edit without justification;
- View mode still shows editable controls;
- Save is active with no required detail lines;
- Edit is allowed for posted/closed documents contrary to domain rules;
- Delete/Post/Rollback executes without a confirmation step;
- action permissions checked only visually but not in handler/service;
- popup closes on outside click and silently loses line edits;
- page-specific hardcoded colors break dark mode;
- giant custom CSS creates a new visual design;
- no empty state;
- no submitting state, allowing double-click duplicate operations;
- mobile UI ignored;
- date/number formats inconsistent with adjacent ERP pages;
- table/grid column widths are left entirely automatic and become unreadable;
- row icon buttons have no tooltip/title;
- business errors are swallowed or shown only in console/log.

---

# 24. Definition of Done Checklist

An agent should not mark a new transaction UI complete until all applicable items pass.

## List page

- [ ] Uses PageBase and module authorization pattern.
- [ ] Success/error message area matches standard.
- [ ] Hero contains icon, eyebrow, title, useful chips, and meaningful KPI/count.
- [ ] Search is above grid and has a realistic placeholder.
- [ ] Search is debounced for server queries.
- [ ] Filter popup exists when filtering is needed.
- [ ] `Filtered` context is visible when filters are active.
- [ ] `CommonDataGridEx` is used unless there is a justified exception.
- [ ] Stable unique `GridKey` is configured.
- [ ] Server-side/custom paging/filtering is used for large data.
- [ ] Grid formats dates/numbers/amounts consistently.
- [ ] Toolbar actions are permission-aware.
- [ ] Row View/Edit actions are state-aware.
- [ ] State-changing/destructive actions use confirmation.
- [ ] Repeated submission is blocked while processing.
- [ ] Loading state is visible.
- [ ] Mobile compact list exists and uses same filter/search context.
- [ ] Empty results are explained clearly.

## Entry page

- [ ] Uses New/Edit/View route pattern.
- [ ] Page title and hero reflect mode.
- [ ] Document status is visible.
- [ ] View mode is read-only.
- [ ] Edit eligibility checks permission + document state.
- [ ] Header form uses compact DevExpress layout.
- [ ] Detail section uses standard card/header/grid structure.
- [ ] Add line/source action is in the line-section header.
- [ ] Row edit/remove actions appear only when editable.
- [ ] Grid has meaningful column widths and number formats.
- [ ] Empty line state is informative.
- [ ] Complex line editing uses a popup/picker pattern.
- [ ] Footer is sticky and uses `Close | Edit` or `Cancel | Save` as appropriate.
- [ ] Save button has `CanSave` logic and submitting protection.
- [ ] Unsaved changes trigger discard confirmation.
- [ ] Successful Save clears dirty state.
- [ ] Service errors are surfaced to the user.
- [ ] Layout remains usable on narrow screens.
- [ ] CSS follows existing theme variables and visual metrics.

---

# 25. Review Standard for Agents

When reviewing a proposed UI implementation, do not merely ask whether it compiles.

Review it against these dimensions:

1. **House-style fidelity** — does it visibly match Inventory transaction pages?
2. **Interaction consistency** — do search, filter, selection, View/Edit, popup, and footer behavior feel familiar?
3. **Mode correctness** — are New/Edit/View states explicit and safe?
4. **Permission correctness** — are actions protected consistently?
5. **Workflow correctness** — are invalid state transitions prevented?
6. **Responsive quality** — is it genuinely usable on smaller screens?
7. **Accessibility** — messages, titles, icons, and interactive elements are understandable.
8. **Maintainability** — did the implementation reuse project components/styles instead of creating a one-off design system?
9. **Performance** — does the list use server paging/filtering rather than loading everything?
10. **ERP usability** — is the page compact, clear, predictable, and efficient for repeated daily use?

A visually attractive page that breaks the established ERP interaction pattern should still be revised.

---

# 26. Final Instruction to the Coding Agent

When creating a new ERP transaction UI:

> **Treat the production Inventory transaction UI as the house design system. Reuse its page anatomy, spacing, cards, hero, chips, grid patterns, popups, permissions, New/Edit/View modes, sticky footer, feedback states, and responsive behavior. Change the business content — not the interaction language.**

If the target module already has a newer pattern that intentionally supersedes the Inventory reference, explain the conflict before deviating and follow the repository's most current established standard.
