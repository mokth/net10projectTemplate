---
name: devexpress-blazor-editor-binding-safety
description: >
  Prevent DevExpress Blazor editor binding and validation runtime errors in ERP UI code.
  Apply whenever creating, editing, reviewing, or refactoring DevExpress input controls
  such as DxTextBox, DxMemo, DxSpinEdit, DxDateEdit, DxCheckBox, DxComboBox,
  DxDropDownBox, DxTagBox, and DxListBox, especially inside EditForm or other validated forms.
---

# DevExpress Blazor Editor Binding Safety

## Purpose

Prevent recurring DevExpress Blazor runtime errors such as:

```text
System.InvalidOperationException:
DevExpress.Blazor... requires a value for the 'TextExpression' property.
It is specified automatically when you use two-way binding ('bind-Text').
```

and equivalent errors for:

- `ValueExpression`
- `DateExpression`
- `CheckedExpression`
- `ValuesExpression`

This skill applies to DevExpress Blazor editors used in ERP entry forms, transaction pages,
master pages, popup editors, lookup editors, and manually templated edit forms.

The goal is to preserve proper DevExpress/Blazor validation.
Do NOT hide binding mistakes by disabling validation on normal editable ERP fields.

---

# Core Rule

For every DevExpress editor that participates in form validation:

1. Prefer normal two-way binding using `@bind-*`.
2. If an explicit `*Changed` event is required, do NOT use `@bind-*` on the same property.
3. When using explicit `Property + PropertyChanged`, also provide the matching `PropertyExpression`.
4. The expression must point to the SAME model property represented by the editor.
5. Do not disable validation merely to suppress an exception.

---

# Mandatory Binding Matrix

| Editor / Binding | Normal two-way binding | Explicit event pattern requires |
|---|---|---|
| `DxTextBox` | `@bind-Text` | `Text + TextChanged + TextExpression` |
| `DxMemo` | `@bind-Text` | `Text + TextChanged + TextExpression` |
| `DxSpinEdit<T>` | `@bind-Value` | `Value + ValueChanged + ValueExpression` |
| `DxDateEdit<T>` | `@bind-Date` | `Date + DateChanged + DateExpression` |
| `DxCheckBox<T>` | `@bind-Checked` | `Checked + CheckedChanged + CheckedExpression` |
| `DxComboBox<TData,TValue>` | `@bind-Value` | `Value + ValueChanged + ValueExpression` |
| `DxDropDownBox` | `@bind-Value` | `Value + ValueChanged + ValueExpression` |
| `DxListBox<TData,TValue>` single selection | `@bind-Value` | `Value + ValueChanged + ValueExpression` |
| Multi-value editors when using `Values` | `@bind-Values` | `Values + ValuesChanged + ValuesExpression` |

IMPORTANT:

- Verify the exact API of the installed DevExpress version before changing an unfamiliar editor.
- Some list/drop-down editors support both `Value` and `Text` validation modes.
- Do not guess property names for an editor that is not listed above.

---

# Rule 1 — Preferred Pattern: Use `@bind-*`

Use two-way binding when no special change-processing pipeline is needed.

## DxTextBox

```razor
<DxTextBox @bind-Text="Model.CustomerName" />
```

## DxMemo

```razor
<DxMemo @bind-Text="Model.Remarks" />
```

## DxSpinEdit

```razor
<DxSpinEdit @bind-Value="Model.Quantity" />
```

## DxDateEdit

```razor
<DxDateEdit @bind-Date="Model.RequiredDate" />
```

## DxCheckBox

```razor
<DxCheckBox @bind-Checked="Model.IsActive" />
```

## DxComboBox

```razor
<DxComboBox Data="@Customers"
            @bind-Value="Model.CustomerCode"
            ValueFieldName="CustomerCode"
            TextFieldName="CustomerName" />
```

DevExpress/Blazor generates the corresponding changed handler and expression automatically.

---

# Rule 2 — Explicit Changed Handler: Expression Is Mandatory

When custom logic requires `TextChanged`, `ValueChanged`, `DateChanged`,
`CheckedChanged`, or `ValuesChanged`, use the full explicit pattern.

## DxTextBox

```razor
<DxTextBox Text="@Model.CustomerName"
           TextChanged="@OnCustomerNameChanged"
           TextExpression="@(() => Model.CustomerName)" />
```

```csharp
private void OnCustomerNameChanged(string value)
{
    Model.CustomerName = value;
    // Additional business/UI logic here.
}
```

## DxSpinEdit

```razor
<DxSpinEdit Value="@Model.Quantity"
            ValueChanged="@OnQuantityChanged"
            ValueExpression="@(() => Model.Quantity)" />
```

```csharp
private void OnQuantityChanged(decimal value)
{
    Model.Quantity = value;
    RecalculateLine();
}
```

## DxDateEdit

```razor
<DxDateEdit Date="@Model.RequiredDate"
            DateChanged="@OnRequiredDateChanged"
            DateExpression="@(() => Model.RequiredDate)" />
```

## DxCheckBox

```razor
<DxCheckBox Checked="@Model.IsActive"
            CheckedChanged="@OnIsActiveChanged"
            CheckedExpression="@(() => Model.IsActive)" />
```

## DxComboBox

```razor
<DxComboBox Data="@Customers"
            Value="@Model.CustomerCode"
            ValueChanged="@OnCustomerChanged"
            ValueExpression="@(() => Model.CustomerCode)"
            ValueFieldName="CustomerCode"
            TextFieldName="CustomerName" />
```

The expression must identify the exact bound field.

Correct:

```razor
Value="@Model.CustomerCode"
ValueExpression="@(() => Model.CustomerCode)"
```

Wrong:

```razor
Value="@Model.CustomerCode"
ValueExpression="@(() => Model.CustomerName)"
```

---

# Rule 3 — NEVER Mix `@bind-*` and the Matching `*Changed`

Do NOT write:

```razor
<DxSpinEdit @bind-Value="Model.Quantity"
            ValueChanged="@OnQuantityChanged" />
```

Do NOT write:

```razor
<DxTextBox @bind-Text="Model.CustomerName"
           TextChanged="@OnCustomerNameChanged" />
```

Blazor's `@bind-*` already generates the corresponding `*Changed`
parameter and `*Expression`.

This can cause errors similar to:

```text
The component parameter 'ValueChanged' is used two or more times
for this component.
```

Choose ONE pattern only:

### Pattern A — Normal binding

```razor
<DxSpinEdit @bind-Value="Model.Quantity" />
```

### Pattern B — Explicit handler

```razor
<DxSpinEdit Value="@Model.Quantity"
            ValueChanged="@OnQuantityChanged"
            ValueExpression="@(() => Model.Quantity)" />
```

---

# Rule 4 — Custom Logic Without Manual Changed Wiring

If practical, keep `@bind-*` and move additional logic into a wrapper property.

Example:

```razor
<DxTextBox @bind-Text="CustomerName" />
```

```csharp
private string? CustomerName
{
    get => Model.CustomerName;
    set
    {
        if (Model.CustomerName == value)
            return;

        Model.CustomerName = value;
        HandleCustomerNameChanged(value);
    }
}
```

Use this only when it is clear and does not obscure the real edit model.

If the repository already uses a supported `@bind-*:after` convention,
that may also be used, but do not introduce it blindly into codebases
that do not already use or support that pattern.

---

# Rule 5 — Read-Only / Display-Only Controls

A value that is purely display-only should not accidentally participate in validation.

Preferred: use normal display markup where an editor is unnecessary.

```razor
<div class="form-control-plaintext">
    @Model.CustomerName
</div>
```

If a DevExpress editor must be used for display:

```razor
<DxTextBox Text="@Model.CustomerName"
           ReadOnly="true"
           ValidationEnabled="false" />
```

Use `ValidationEnabled="false"` only when the control genuinely should NOT
participate in validation.

---

# Rule 6 — Do NOT Use `ValidationEnabled="false"` as a Shortcut Fix

Forbidden fix for a normal editable transaction field:

```razor
<DxTextBox Text="@Model.CustomerName"
           TextChanged="@OnCustomerNameChanged"
           ValidationEnabled="false" />
```

if `CustomerName` is a real editable/validated field.

Correct it with:

```razor
<DxTextBox Text="@Model.CustomerName"
           TextChanged="@OnCustomerNameChanged"
           TextExpression="@(() => Model.CustomerName)" />
```

Disabling validation can hide:

- `[Required]`
- range validation
- custom validation
- business validation messages
- invalid transaction input

For ERP transaction pages, validation must remain enabled unless there is a
clear functional reason otherwise.

---

# Rule 7 — Event Handler Must Update the Model

With explicit `*Changed` handling, the handler is responsible for updating
the bound model value.

Wrong:

```csharp
private void OnQuantityChanged(decimal value)
{
    RecalculateLine();
}
```

Correct:

```csharp
private void OnQuantityChanged(decimal value)
{
    Model.Quantity = value;
    RecalculateLine();
}
```

For async handlers:

```csharp
private async Task OnCustomerChanged(string? value)
{
    Model.CustomerCode = value;

    if (!string.IsNullOrWhiteSpace(value))
        await LoadCustomerDefaultsAsync(value);
}
```

Do not leave the UI showing one value while the edit model contains another.

---

# Rule 8 — Model and Expression Must Be Stable

Before rendering an editable DevExpress control:

- Ensure the edit model exists.
- Ensure nested objects used by expressions are initialized.
- Avoid expressions that can resolve through null intermediate objects.

Risky:

```razor
Text="@Model.Customer.Address.Line1"
TextExpression="@(() => Model.Customer.Address.Line1)"
```

when `Customer` or `Address` may be null.

Initialize the edit model/nested objects before rendering, or conditionally
render the editor only when its model path is valid.

---

# Rule 9 — ComboBox / Lookup Controls Need the Same Discipline

Lookup controls are not exempt.

If the user selects a customer, vendor, item, warehouse, project, priority,
machine, employee, or other ERP master value through `DxComboBox`, the
selected value must be linked to the real edit-model property.

Preferred:

```razor
<DxComboBox Data="@Warehouses"
            @bind-Value="Model.WarehouseCode"
            ValueFieldName="WarehouseCode"
            TextFieldName="WarehouseName" />
```

With custom handler:

```razor
<DxComboBox Data="@Warehouses"
            Value="@Model.WarehouseCode"
            ValueChanged="@OnWarehouseChanged"
            ValueExpression="@(() => Model.WarehouseCode)"
            ValueFieldName="WarehouseCode"
            TextFieldName="WarehouseName" />
```

Do not create lookup controls that display a value but fail to update the
actual transaction model.

---

# Rule 10 — EditForm Review Is Mandatory

Treat every DevExpress editor inside or associated with an `EditForm`
as validation-sensitive.

Before completing a Razor page, inspect:

```razor
<EditForm Model="...">
```

or:

```razor
<EditForm EditContext="...">
```

Then review every `Dx*` editor in the form.

For each editor determine:

1. What exact model field does it edit?
2. Is it two-way bound?
3. If it has an explicit changed handler, is the matching expression present?
4. Does the handler update that exact model field?
5. Is validation intentionally enabled?
6. Is the model path non-null and stable?

---

# Required AI Agent Review Procedure

Whenever an AI agent creates or edits a DevExpress Blazor entry page:

## Step 1 — Identify all DevExpress editors

Search the Razor file for relevant controls, including:

```text
<DxTextBox
<DxMemo
<DxSpinEdit
<DxDateEdit
<DxCheckBox
<DxComboBox
<DxDropDownBox
<DxTagBox
<DxListBox
```

Also inspect any custom components that wrap DevExpress editors.

## Step 2 — Audit every manual changed handler

Search for:

```text
TextChanged=
ValueChanged=
DateChanged=
CheckedChanged=
ValuesChanged=
```

For every occurrence, confirm its matching expression exists unless the
control is intentionally outside validation and validation is deliberately disabled.

## Step 3 — Audit all `@bind-*` controls

Ensure the same component does NOT also declare its matching `*Changed`.

Examples to reject:

```text
@bind-Text + TextChanged
@bind-Value + ValueChanged
@bind-Date + DateChanged
@bind-Checked + CheckedChanged
@bind-Values + ValuesChanged
```

## Step 4 — Verify exact field consistency

For every explicit editor, compare:

```text
Property
PropertyChanged
PropertyExpression
```

All three must represent the same logical value.

## Step 5 — Verify handler assignment

Confirm the handler writes the changed value back to the edit model before
performing dependent logic.

## Step 6 — Build

The implementation is not complete until the relevant project builds.

## Step 7 — Runtime smoke test

For affected forms:

1. Open create mode.
2. Open edit mode where applicable.
3. Focus and change every affected editor.
4. Trigger validation with required fields blank.
5. Enter valid data.
6. Save.
7. Reopen the saved record.
8. Confirm no `InvalidOperationException`.
9. Confirm validation messages still work.
10. Confirm dependent calculations/lookups still run.

---

# Common Error Signatures

If runtime logs contain:

```text
requires a value for the 'TextExpression' property
```

Inspect `DxTextBox` / `DxMemo` using `Text` or `TextChanged`.

If logs contain:

```text
requires a value for the 'ValueExpression' property
```

Inspect value-based DevExpress editors such as `DxSpinEdit`,
`DxComboBox`, `DxDropDownBox`, or similar editors.

If logs contain:

```text
requires a value for the 'DateExpression' property
```

Inspect `DxDateEdit` using `Date` / `DateChanged`.

If logs contain:

```text
requires a value for the 'CheckedExpression' property
```

Inspect `DxCheckBox` using `Checked` / `CheckedChanged`.

If logs contain:

```text
The component parameter 'ValueChanged' is used two or more times
```

look for `@bind-Value` combined with an explicit `ValueChanged`.

Apply the equivalent check for other bindable properties.

---

# Forbidden AI Coding Patterns

Do not generate any of these without a documented reason.

## Missing expression

```razor
<DxTextBox Text="@Model.RefNo"
           TextChanged="@OnRefNoChanged" />
```

## Duplicate bind/change event

```razor
<DxSpinEdit @bind-Value="Model.Qty"
            ValueChanged="@OnQtyChanged" />
```

## Suppressing validation to hide binding error

```razor
<DxTextBox Text="@Model.RefNo"
           TextChanged="@OnRefNoChanged"
           ValidationEnabled="false" />
```

## Wrong expression target

```razor
<DxComboBox Value="@Model.CustomerCode"
            ValueChanged="@OnCustomerChanged"
            ValueExpression="@(() => Model.CustomerName)" />
```

## Handler not updating model

```csharp
private void OnQtyChanged(decimal value)
{
    Recalculate();
}
```

---

# Preferred ERP Coding Standard

For normal editable fields:

```text
USE @bind-*.
```

For editable fields that require custom change handling:

```text
USE Property + PropertyChanged + matching PropertyExpression.
```

For display-only fields:

```text
USE display markup, or explicitly disable validation when a DevExpress
editor is intentionally used only for display.
```

For AI-generated changes:

```text
NEVER mark the work complete until the entire edited Razor form has been
audited for this pattern, not only the one control that originally failed.
```

---

# Completion Gate

An AI agent may report the DevExpress editor work as complete only when all
of the following are true:

- [ ] Every new/modified DevExpress editor has a clear model field.
- [ ] Normal editors use `@bind-*` where practical.
- [ ] Every explicit `*Changed` binding has the matching `*Expression`.
- [ ] No editor mixes `@bind-*` with the corresponding `*Changed`.
- [ ] Every explicit changed handler writes the value back to the model.
- [ ] Validation has not been disabled simply to suppress exceptions.
- [ ] Read-only/display-only fields are intentionally non-validating.
- [ ] Nested model paths are initialized safely.
- [ ] Lookup editors update the real transaction/master model field.
- [ ] The affected project builds successfully.
- [ ] Runtime create/edit/save validation smoke tests pass.
- [ ] No `TextExpression`, `ValueExpression`, `DateExpression`,
      `CheckedExpression`, or `ValuesExpression` exception remains.

If any item above is unresolved, the implementation is NOT approved.

---

# DevExpress Reference Basis

This rule is based on the DevExpress Blazor editor validation/binding model:

- `DxTextBox.TextExpression`
  https://docs.devexpress.com/Blazor/DevExpress.Blazor.DxTextBox.TextExpression
- `DxSpinEdit<T>.ValueExpression`
  https://docs.devexpress.com/Blazor/DevExpress.Blazor.DxSpinEdit-1.ValueExpression
- `DxDateEdit<T>.DateExpression`
  https://docs.devexpress.com/Blazor/DevExpress.Blazor.DxDateEdit-1.DateExpression
- `DxCheckBox<T>.CheckedExpression`
  https://docs.devexpress.com/Blazor/DevExpress.Blazor.DxCheckBox-1.CheckedExpression
- `DxComboBox<TData,TValue>` API
  https://docs.devexpress.com/Blazor/DevExpress.Blazor.DxComboBox-2
- DevExpress troubleshooting: duplicate `ValueChanged` with `@bind-Value`
  https://docs.devexpress.com/Blazor/403279/troubleshooting/common-component-issues/the-component-parameter-value-changed-is-used-two-or-more-times

Before modifying an unfamiliar DevExpress control, verify the API against
the DevExpress version installed by the target repository.
