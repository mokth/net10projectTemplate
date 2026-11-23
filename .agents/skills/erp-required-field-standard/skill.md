---
name: erp-required-field-standard
description: >-
  Mandatory required-field visual standard for ErpWeb Blazor UI. Use whenever
  creating, modifying, reviewing, or standardizing any form, entry page, master
  screen, or popup dialog with required inputs. Enforces the shared
  --required-field-bg input tint in site.css (via .required-field) paired with a
  trailing " *" on the field caption/label in the same color as the caption text.
---

# ERP Required Field Standard

## 1. Visual contract (non-negotiable)

Every genuinely mandatory editable field across `ErpWeb.UI` and `ErpWeb` must satisfy **both** rules together:

1. **Standard input background color** — apply `required-field` (backed by `--required-field-bg` in `ErpWeb/wwwroot/css/site.css`):
   - **Light theme (`:root`)**: `#fff6e5` (soft warm cream — clearly distinct from `#ffffff` optional inputs, calm on dense forms, high contrast with `#1c2430` text, and never clashes with `--danger` red validation outlines or `--accent` blue focus).
   - **Dark theme (`[data-bs-theme="dark"]`)**: `#3a3220` (muted warm dark amber — distinct from `#1a2332` surface and high contrast with `#e8edf4` text).
2. **Caption ending with `*`** — end the caption or label text with a single space and asterisk (` *`), rendered in **the exact same color as the caption text** (do not make `*` red or another accent color).

Never apply only one half of the standard (no `required-field` without ` *`, and no ` *` without `required-field`).

---

## 2. Caption & label markup

Write the trailing ` *` directly in the caption/label text so it inherits the caption's exact font, weight, and theme color in both `DxFormLayout` and custom `.iv-field` grids:

### A. `DxFormLayoutItem`

```razor
<DxFormLayoutItem Caption="Date *" ColSpanMd="12">
    <Template>
        <DxDateEdit Date="@Model.DocDate"
                    DisplayFormat="dd/MM/yyyy"
                    InputCssClass="required-field" />
    </Template>
</DxFormLayoutItem>
```

### B. Custom label + field stack (`.iv-field` / `.form-group`)

```razor
<div class="iv-field">
    <label class="iv-field__label" for="supp-code">Supplier code *</label>
    <DxTextBox InputId="supp-code"
               @bind-Text="@Model.SuppCode"
               InputCssClass="required-field" />
</div>
```

### C. Conditionally required fields

Drive **both** the caption ` *` and `required-field` from the same boolean condition:

```razor
<DxFormLayoutItem Caption="@(LineExpiryRequired ? "Expiry date *" : "Expiry date")" ColSpanMd="6">
    <Template>
        <DxDateEdit @bind-Date="@LinePopup.ExpiryDate"
                    DisplayFormat="dd/MM/yyyy"
                    Enabled="@LineExpiryEnabled"
                    InputCssClass="@(LineExpiryRequired ? "required-field" : null)" />
    </Template>
</DxFormLayoutItem>
```

```razor
<div class="iv-field">
    <label class="iv-field__label">@(Model.StockControl ? "Class *" : "Class")</label>
    <IvCodeComboBox Data="@Classes"
                    Value="@Model.IClassCode"
                    InputCssClass="@(Model.StockControl ? "required-field" : null)" />
</div>
```

---

## 3. Control binding matrix

Shared CSS lives in `ErpWeb/wwwroot/css/site.css` (`.required-field` rules). Pass `required-field` using the parameter below:

| Control | Parameter to use | Example |
|---|---|---|
| `DxTextBox` | `InputCssClass` | `InputCssClass="required-field"` (or `"required-field code-style"`) |
| `DxDateEdit` | `InputCssClass` | `InputCssClass="required-field"` |
| `DxSpinEdit` | `InputCssClass` | `InputCssClass="required-field"` |
| `DxComboBox` | `InputCssClass` | `InputCssClass="required-field"` |
| `DxMemo` | `InputCssClass` or `CssClass` | `InputCssClass="required-field"` |
| `IvCodeComboBox` | `InputCssClass` | `InputCssClass="required-field"` |
| `SmartLookupInput` & domain pickers (`IvStockMasterPicker`, `SaCustomerPicker`, `PoSupplierPicker`, `PoPurchasingItemPicker`, `IvBalLocPicker`, `PrProductionStockWorkOrderPicker`) | `InputCssClass` | `InputCssClass="required-field"` |
| Native `<input>` / `<textarea>` / `<select>` | `class` | `class="required-field"` |

---

## 4. When NOT to apply

- **Read-only / auto-generated / display-only fields** (e.g., `Document No.` generated on save, `Status`, `Created By`, calculated totals) — no ` *`, no `required-field`.
- **Optional fields** — even if validated for format when non-empty (e.g., optional email or optional remarks).
- **Search bars & filter toolbars** — list search (`erp-inline-search`) and optional filter inputs are not mandatory entry fields.

---

## 5. Forbidden patterns

- `InputCssClass="required-field"` without trailing ` *` on the caption/label
- Trailing ` *` on the caption/label without `required-field` on the input
- Red or differently colored asterisk spans (e.g. `<span class="...-req">*</span>`, `text-danger`, `var(--danger)`)
- Page-local CSS rules redefining `.required-field` or hardcoding hex colors (`#fff6e5`, `#fff3cd`, `lightyellow`, etc.) in `.razor` or `.razor.css`
- Copying a page-specific asterisk class (`.po-supp-req`, `.sa-cust-req`, `.iv-field__required`) into new pages
