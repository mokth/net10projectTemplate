---
name: inventory-transaction-ui
description: >-
  UI/UX standard for Inventory transaction list and entry pages in
  mokth/net10projectTemplate. Use for goods receipt, miscellaneous issue/receipt,
  stock transfer, stock adjustment, stock count, stock return, vendor return, scrap
  and similar Inventory transactions. Requires erp-ui-core.
---

# Inventory Transaction UI Skill

## Required companion

Read first:

`.agents/skills/erp-ui-core/skill.md`

This skill owns **Inventory-specific entry behavior**. The shared core owns list fundamentals, theme, permissions, accessibility, performance and general engineering rules.

---

# 1. Actual repository location

Inventory UI:

`ErpWeb.UI/Inventory`

Transaction pages:

`ErpWeb.UI/Inventory/Transactions`

Do not use old root-level Inventory pages as the primary reference when the newer `Transactions` page family exists.

---

# 2. Mandatory live references

Before coding, inspect the current branch versions of:

## Primary baseline

- `ErpWeb.UI/Inventory/Transactions/IvStockTransferList.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransferList.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransferList.razor.css`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor.cs`
- `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor.css`

## Shared infrastructure

- `ErpWeb/wwwroot/css/inventory-chrome.css`
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor`
- `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs`
- `ErpWeb.UI/Inventory/Lookups/IvStockMasterPicker.razor`
- `ErpWeb.UI/Inventory/Lookups/IvBalLocPicker.razor`

Then inspect the closest domain reference from the table below.

---

# 3. Reference-selection table

| New/change resembles | Read this live reference |
|---|---|
| warehouse/location/lot movement | `IvStockTransfer` |
| issuing stock out | `IvMiscIssue` |
| receiving stock in | `IvMiscReceipt` / `IvGoodsReceipt` |
| +/- quantity/value adjustment | `IvStockAdjustment` |
| scrap/write-off | `IvScrap` |
| internal stock return | `IvStockReturn` |
| return to supplier | `IvVendorReturn` |
| physical stock count | `IvStockCount` |

Do not use Stock Count as the default reference for a normal transaction. It has a specialized Generate/Count/Post/Recovery lifecycle.

---

# 4. Inventory UI Standard Evidence Block

Before implementation, fill this in:

```text
INVENTORY UI STANDARD EVIDENCE
Target page:
Transaction purpose:
List reference:
Entry reference:
Entry CSS prefix:
Key/header identity:
Line identity:
Stock source/destination picker:
Lot-control behavior:
Modes:
Statuses editable:
Toolbar actions:
Permissions:
Posting/rollback/cancel/delete eligibility:
Cost/unit-price visibility/editability:
Intentional deviations:
```

Do not code the entry UI until this is known.

---

# 5. Inventory list page

Follow the shared list contract exactly unless an established Inventory transaction differs.

Use:

- `iv-page`
- `iv-hero`
- `iv-card`
- search + FILTER
- `CommonDataGridEx`
- mobile compact list
- filter/confirmation `DxPopup`

The hero eyebrow is `Inventory`.

The KPI should be a useful Inventory count/metric, not decorative.

---

# 6. Inventory list actions

Do not assume every Inventory transaction has the same action set.

Observed examples:

### Stock Transfer / Misc Issue family

```text
NEW
POST
ROLLBACK
CANCEL
DELETE
```

### Goods Receipt family

```text
NEW
POST
ROLLBACK
DELETE
```

### Stock Count

Specialized actions may include:

```text
GENERATE
SAVE
POST
ROLLBACK
RECOVER
```

For a new transaction, derive actions from actual service/status behavior.

Toolbar order should generally be:

```text
create -> positive lifecycle -> reversal/cancel -> destructive
```

Examples:

```text
NEW -> POST -> ROLLBACK -> CANCEL -> DELETE
```

Never add CANCEL or ROLLBACK simply for visual consistency when the service has no such transition.

---

# 7. Standard Inventory entry anatomy

Normal Inventory batch/header-detail pages should feel like the same family:

```text
Entry
├─ feedback
├─ hero
│  ├─ Inventory eyebrow
│  ├─ New/Edit/View heading
│  ├─ mode/batch/status/line chips
│  └─ useful quantity/value KPI
├─ compact header card
├─ line card
│  ├─ section heading
│  ├─ Add item/source action
│  ├─ DxGrid
│  └─ empty state
├─ sticky footer
│  ├─ line/value hint
│  └─ Close|Edit OR Cancel|Save
├─ line editor/picker popup(s)
└─ discard/confirmation popup(s)
```

Exact class prefix may differ by transaction.

Examples in the current family include:

- `tr-*`
- `mi-*`
- `mr-*`
- `sa-*`
- `sc-*`
- `sr-*`
- `vr-*`

Do not create visual differences merely because the prefix differs.

---

# 8. Entry routes

For a normal Inventory transaction, prefer one entry component with explicit modes, following the live neighboring family.

Typical pattern:

```razor
@page "/inventory/.../{Mode:regex(^(new|edit|view)$)}"
@page "/inventory/.../{Mode:regex(^(new|edit|view)$)}/{BatchNo:int}"
```

Use the actual key type.

Do not force this exact route onto a specialized transaction such as Stock Count if its existing lifecycle has another mode.

---

# 9. Header form

Use compact `DxFormLayout`.

Typical fields:

- transaction date;
- status read-only;
- reference;
- remark;
- transaction-specific warehouse/type context.

Rules:

- status is not a free-edit field;
- View mode is read-only;
- `required-field` only where genuinely required;
- database/domain max length should be represented;
- use ERP date format;
- do not make the header taller than necessary.

Stock detail belongs in lines/pickers, not in a giant header form.

---

# 10. Stock line interaction

For normal Inventory detail:

- use `DxGrid`;
- item code/description may be stacked;
- stock source location/lot may be stacked;
- qty and amount align right;
- use existing Inventory picker components;
- hide mutation controls in View mode;
- row double-click may edit only while editable.

For stock-decreasing lines, make available stock context clear.

For lot-controlled items, show/require lot information according to existing service/domain rules.

Do not invent stock by allowing free-form warehouse/location/lot text when the repository has a picker.

---

# 11. Line editor popup

Complex stock lines should normally use a popup rather than turning the whole grid into a dense inline form.

Use:

- `DxPopup`
- `common-popup`
- `CloseOnOutsideClick="false"`
- compact `DxFormLayout`
- grouped Item / Source / Destination / Details sections where relevant
- Cancel then Add/Update

Reuse:

- `IvStockMasterPicker`
- `IvBalLocPicker`
- `IvCodeComboBox`
- other current Inventory lookup components

Do not duplicate the picker query logic in the page.

---

# 12. Inventory cost / unit price UI

Cost is business-sensitive.

A UI standard must not decide that the user can type cost.

Before rendering an editable unit-price/cost field, verify:

- transaction's cost authority;
- permission;
- costing policy/service;
- whether value is system-derived;
- whether override is explicitly supported.

If the system owns the cost:

- display read-only when useful;
- do not present an editable field merely because another receipt has one.

If an override is supported:

- make system-derived value visible;
- make override permission/state explicit;
- preserve service validation.

UI consistency must never weaken costing authority.

---

# 13. Inventory editability model

The normal family follows an explicit model similar to:

```text
New -> editable
Edit NEW -> editable if permission
View -> read-only
Posted/Cancelled/Reversed/etc. -> read-only unless a defined transition returns it to editable state
```

Use computed code-behind properties.

Do not infer editability from route alone.

For example, the current Stock Transfer family requires status NEW plus Edit permission before View can offer Edit.

---

# 14. Save footer

Normal editable entry:

```text
Cancel | Save
```

View:

```text
Close | Edit
```

Show Edit only if permission + state permit it.

Footer hint should show something useful:

```text
4 lines · Total 12,430.00
```

or:

```text
Add at least one item to save this issue.
```

Save must be disabled while submitting and while minimum document conditions are not met.

---

# 15. Dirty/discard behavior

Track meaningful edits.

Cancel should not silently lose:

- header edits;
- added/edited lines;
- split/lot changes;
- other document mutations.

Use the existing discard confirmation.

Clear dirty state only after successful persistence or intentional reload.

---

# 16. Specialized Inventory exception — Stock Count

Stock Count is not the template for generic transaction pages.

It can have workflow-specific modes/actions such as:

- Generate;
- Count;
- Save;
- Post;
- Rollback;
- Recover.

When changing Stock Count:

1. inspect its own current Razor/code-behind/CSS first;
2. preserve the count workflow;
3. apply shared visual/accessibility rules without flattening its state model.

---

# 17. Inventory CSS rules

List page:

- reuse global `iv-*`.

Entry page:

- use existing transaction-specific isolated prefix;
- keep visual measures aligned to sibling Inventory entries;
- do not create another global Inventory framework;
- do not paste `inventory-chrome.css` into `.razor.css`.

If a page already has a scoped style family, extend it minimally.

---

# 18. Inventory rejection rules

Reject an Inventory implementation if it:

- uses Sales `SaDocPage` for a normal stock batch;
- puts a large editable stock form directly into the list grid;
- uses free-form lot/location values when a repository picker should be used;
- makes cost editable without verified authority;
- shows line edit/delete in View mode;
- permits posted/cancelled document editing without a defined reversal/reopen;
- puts POST/ROLLBACK inside Save footer just because they are lifecycle actions;
- duplicates list search inside `CommonDataGridEx`;
- copies Stock Count workflow into a normal receipt/issue.

---

# 19. Inventory Definition of Done

- [ ] Core skill passed.
- [ ] Inventory evidence block completed.
- [ ] Closest stock-flow reference inspected.
- [ ] List follows shared list standard.
- [ ] Entry uses Inventory batch/header-detail anatomy unless documented exception.
- [ ] All transaction behavior lives in `.razor.cs`.
- [ ] Page CSS is isolated and visually aligned.
- [ ] Existing Inventory pickers are reused.
- [ ] Lot/warehouse/location behavior follows domain rules.
- [ ] Cost fields follow verified cost authority.
- [ ] Mode + status + permission drive editing.
- [ ] Save/dirty/discard behavior is safe.
- [ ] Posting/rollback/cancel/delete actions match actual service capability.
- [ ] Mobile layout and dark theme remain usable.
