---
name: erp-inline-search-standard
description: >-
  Mandatory global inline search/lookup row markup for ErpWeb Blazor UI.
  Use whenever adding or changing list toolbar search, popup grid search,
  picker dialog search, or any single free-text filter row next to a grid —
  not entity SmartLookup fields (see erp-large-data-smart-lookup-standard).
  Enforces one non-wrapping row Caption [input][icon-only magnifier], shared
  inventory-chrome.css classes, BindValueMode.OnInput, label/InputId pairing,
  and ErpInlineSearchContractTests compliance. Do not invent scoped layouts,
  stacked label+input, or Text="SEARCH" buttons.
---

# ERP Global Inline Search Standard

## When this skill applies

Use for **single free-text search rows** on list pages, popups, and pickers:

- Toolbar search above `CommonDataGridEx` (`iv-toolbar-row__search`)
- Popup/picker header search (`erp-inline-search--popup`)
- Live filter rails with no explicit Apply button (`erp-inline-search--live`)

**Do not** use this markup for:

- **Entity code lookups** (item, customer, vendor, WO, …) → `SmartLookupInput` + `erp-large-data-smart-lookup-standard`
- **Typeahead that only narrows an adjacent combo** (documented exceptions in contract tests)
- **Multi-criteria forms with one shared RUN/REFRESH** (documented exceptions)

## Visual contract (non-negotiable)

One horizontal row, never wrapping on normal desktop widths:

```text
Search  [ ................................ input  ][ 🔍 ]
```

- Caption is a **`<label class="erp-inline-search__caption" for="…">`**, same row as input — **not** a block label above the field
- Magnifier is **icon-only**: `IconCssClass="fa-solid fa-magnifying-glass"`, **no** `Text="SEARCH"` / `Text="Search"`
- Button size comes from **global** CSS (`2.25rem`), not ad-hoc scoped flex on `DxButton`

Styles live only in:

`ErpWeb/wwwroot/css/inventory-chrome.css` (`.erp-inline-search`, `__caption`, `__editor`, `__button`, `--popup`, `--live`, `.smart-lookup__search`)

Do **not** add page-scoped CSS to size/wrap the magnifier; DevExpress buttons ignore scoped flex.

## Markup templates

### A. Toolbar search with Apply (most list pages)

Wrapper: `iv-toolbar-row__search erp-inline-search`

```razor
<div class="iv-toolbar-row__search erp-inline-search">
    <label class="erp-inline-search__caption" for="{page-slug}-search">Search</label>
    <div class="erp-inline-search__editor">
        <DxTextBox InputId="{page-slug}-search"
                   Text="@SearchText"
                   TextChanged="OnSearchTextChanged"
                   TextExpression="@(() => SearchText)"
                   BindValueMode="BindValueMode.OnInput"
                   NullText="…real searchable fields…"
                   ClearButtonDisplayMode="DataEditorClearButtonDisplayMode.Auto"
                   CssClass="w-100" />
    </div>
    <DxButton RenderStyle="ButtonRenderStyle.Secondary"
              IconCssClass="fa-solid fa-magnifying-glass"
              CssClass="erp-inline-search__button"
              Click="@ApplySearchAsync"
              aria-label="Search …specific entity…"
              title="Search …specific entity…" />
</div>
```

Canonical reference: `ErpWeb.UI/Purchase/Transactions/PoInvoiceList.razor`

### B. Live filter (debounce only, no magnifier)

Add modifier: `erp-inline-search erp-inline-search--live`

- **No** `DxButton` in the row
- `@bind-Text` or equivalent with `BindValueMode="BindValueMode.OnInput"` and debounced handler in `.razor.cs`

Reference: `ErpWeb.UI/Admin/AdminCompany.razor` (`admin-company-rail-search`)

### C. Popup / picker search

Wrapper: `erp-inline-search erp-inline-search--popup`

Same as (A) inside the popup body; stable `InputId` for evidence/tests.

Reference: `ErpWeb.UI/Purchase/Transactions/PoInvoice.razor` (PO picker)

### D. Entity lookup (not inline-search div)

Use **`SmartLookupInput`** — magnifier class **`smart-lookup__search`** (global pin in `inventory-chrome.css`).

Do not duplicate a custom `[input][Search]` row.

Reference: `ErpWeb.UI/Components/Common/Lookups/SmartLookupInput.razor`

## Binding and behavior

| Rule | Detail |
|------|--------|
| `BindValueMode` | **`BindValueMode.OnInput`** on inline-search `DxTextBox` (so Enter/magnifier see latest text) |
| `Text` + `TextChanged` | Must include **`TextExpression`** |
| Debounce | ~400 ms for server list search when the page family already debounces; cancel pending search on explicit Apply |
| Grid | `ShowSearchBox="false"` on `CommonDataGridEx` — page owns search |
| Accessibility | `for`/`InputId` match; magnifier has `aria-label` + `title` |

SmartLookup debounce may stay **`OnDelayedInput` / 550 ms** per `erp-large-data-smart-lookup-standard` — different control, same icon rules.

## Forbidden patterns (agents keep generating these)

- Label **above** input inside `iv-toolbar-row__search` without `erp-inline-search`
- `DxButton` with **`Text="SEARCH"`** or visible “Search” next to the icon
- Magnifier **below** the input (broken flex / missing global classes)
- New `*.razor.css` rules sizing `DxButton` search icons
- Second search box on the grid when the toolbar already searches
- `erp-inline-search--live` row **with** a magnifier button

## Documented exceptions (do not convert blindly)

If you touch these files, read why they are excluded in:

`ErpWeb.Tests/UI/ErpInlineSearchContractTests.cs` → `DocumentedExceptions`

Includes: combo typeahead inquiry pages, multi-criteria reservation bars, `IvReconciliation`, etc.

Full audit: `plans/UI_SEARCH_CONTROL_AUDIT.md`

## Verification before finishing UI work

1. Grep your new markup: class must include **`erp-inline-search`**
2. Run: `dotnet test ErpWeb.Tests --filter ErpInlineSearchContractTests`
3. Compare visually to a converted page (e.g. `IvGoodsReceiptList.razor`, `PoInvoiceList.razor`)

## Cross-skill map

| Need | Skill |
|------|--------|
| List page chrome, FILTER popup, grid flags | `erp-ui-core` |
| This row’s HTML/CSS | **this skill** |
| Vendor/customer/item code field + popup | `erp-large-data-smart-lookup-standard` + `SmartLookupInput` |
| Module page layout | `inventory-transaction-ui`, `purchase-transaction-ui`, etc. |

## Agent checklist

- [ ] Used `erp-inline-search` (+ `--popup` / `--live` if applicable)
- [ ] `label for=` matches `DxTextBox InputId`
- [ ] `BindValueMode.OnInput` on inline search textbox
- [ ] One icon-only `fa-magnifying-glass` button OR `--live` with zero buttons
- [ ] No new CSS for button width; rely on `inventory-chrome.css`
- [ ] Contract tests pass
- [ ] No duplicate grid search box
