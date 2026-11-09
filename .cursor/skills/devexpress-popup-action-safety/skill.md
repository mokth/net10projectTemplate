---
name: devexpress-popup-action-safety
description: >-
  Mandatory safety and usability rules for DevExpress Blazor DxPopup dialogs in
  mokth/net10projectTemplate. Use whenever creating, editing, reviewing, or debugging
  any popup, confirmation dialog, delete/post/rollback/cancel action, filter popup,
  picker popup, repair dialog, or modal workflow. Prevents invisible footer buttons,
  inaccessible actions, accidental dismissals, double submissions, and inconsistent
  ERP popup behavior.
---

# DevExpress Popup & Action Safety

## Purpose

This skill prevents popup/dialog defects that make ERP actions unavailable or confusing.

It is mandatory whenever an agent touches any UI containing:

- `DxPopup`;
- `FooterTemplate`;
- `FooterContentTemplate`;
- confirmation dialogs;
- Delete / Post / Rollback / Cancel / Release / Reopen actions;
- costing repair actions;
- filter dialogs;
- line edit dialogs;
- picker dialogs;
- destructive or state-changing actions.

This skill is especially intended to prevent the defect class where:

> the popup message is visible, but the action buttons are missing.

Repository:

- `mokth/net10projectTemplate`
- branch: `production`

Current UI stack:

- Blazor Server / Razor components
- DevExpress Blazor 26.1.x
- code-behind `.razor.cs`
- scoped or shared CSS

The live `production` branch is always authoritative.

---

# 1. Critical DxPopup Footer Contract

## Mandatory rule

If a `DxPopup` contains either:

```razor
<FooterTemplate>
```

or:

```razor
<FooterContentTemplate>
```

the owning popup MUST explicitly declare:

```razor
ShowFooter="true"
```

Correct:

```razor
<DxPopup HeaderText="Confirm delete"
         CloseOnOutsideClick="false"
         Width="420px"
         ShowFooter="true"
         @bind-Visible="ConfirmVisible">

    <BodyContentTemplate>
        <p>Delete this document?</p>
    </BodyContentTemplate>

    <FooterContentTemplate>
        <DxButton Text="Cancel"
                  RenderStyle="ButtonRenderStyle.Secondary"
                  Click="@CloseConfirm" />

        <DxButton Text="Delete"
                  RenderStyle="ButtonRenderStyle.Danger"
                  Click="@ConfirmDeleteAsync" />
    </FooterContentTemplate>
</DxPopup>
```

Incorrect:

```razor
<DxPopup HeaderText="Confirm delete"
         @bind-Visible="ConfirmVisible">

    <BodyContentTemplate>
        <p>Delete this document?</p>
    </BodyContentTemplate>

    <FooterContentTemplate>
        <DxButton Text="Cancel" />
        <DxButton Text="Delete" />
    </FooterContentTemplate>
</DxPopup>
```

The incorrect version is rejected even if it compiles.

## Important

`ShowCloseButton="true"` is **not** a replacement for `ShowFooter="true"`.

A visible X button does not make hidden business actions acceptable.

---

# 2. Two Allowed Popup Action Patterns

Agents may use either of these established patterns.

## Pattern A — DevExpress footer

Use when actions logically belong in the popup footer.

Required:

```razor
ShowFooter="true"
```

with:

```razor
<FooterTemplate>
```

or:

```razor
<FooterContentTemplate>
```

This is the preferred pattern for:

- confirmations;
- Post;
- Delete;
- Rollback;
- Apply;
- Save;
- repair actions;
- modal workflow decisions.

## Pattern B — Actions inside popup content

Some existing ERP screens place buttons directly inside `<Content>` or body markup:

```razor
<Content>
    <p>@ConfirmMessage</p>

    <div class="iv-popup-footer">
        <div class="iv-popup-footer__actions">
            <DxButton Text="Cancel" ... />
            <DxButton Text="Delete" ... />
        </div>
    </div>
</Content>
```

This pattern does not depend on `ShowFooter`.

It is valid when matching an existing page/module standard.

## Do not mix patterns accidentally

Do not:

- put some actions in content and some in an invisible footer;
- move actions between patterns without a reason;
- create a third one-off action layout.

---

# 3. Mandatory Exit Path

Every popup must have a clear, usable way to leave it.

For confirmation dialogs, normally provide:

```text
Cancel | Action
```

or equivalent wording:

```text
Keep editing | Discard
Keep released | Reopen
Close | Apply
Cancel | Rollback
```

A popup must never trap the user because:

- footer actions are hidden;
- CloseOnOutsideClick is false and no Cancel/Close action exists;
- all buttons are disabled with no explanation;
- the only exit path is browser refresh.

For destructive/state-changing dialogs, do not rely only on the top-right X.

---

# 4. Confirmation Dialog Standard

Any destructive or important state transition must show a confirmation before executing.

Examples:

- Delete;
- Post;
- Rollback;
- Cancel;
- Release;
- Reopen;
- Change Work Order;
- Change Customer/Supplier;
- Costing repair/rebuild;
- overwrite;
- discard unsaved changes.

Preferred order:

```text
Secondary safe action | Primary state-changing action
```

Examples:

```text
Cancel | Delete
Cancel | Post
Cancel | Rollback
Keep Draft | Release
Keep Released | Reopen
Cancel | Rebuild cost state
```

Semantic button style:

| Meaning | RenderStyle |
|---|---|
| Normal safe secondary action | `Secondary` |
| Normal forward action | `Primary` |
| Successful workflow transition | `Success` |
| Rollback / caution | `Warning` |
| Permanent destructive action | `Danger` |

Do not use `Primary` for permanent deletion.

---

# 5. Confirmation Message Quality

Never use only:

```text
Are you sure?
```

The user must know what will happen.

Good:

```text
Delete the NEW draft DP000123?
```

Good:

```text
Rollback posted receipt FG 000234? A reason is required.
```

Good:

```text
Changing supplier will clear all purchase-order lines. Continue?
```

Good:

```text
Rebuild the derived cost state from sealed evidence. Sealed postings and valuation facts are not edited.
```

The message should identify:

- document/action;
- important consequence;
- whether data is permanently removed;
- whether inventory/costing is affected, when relevant.

Do not expose unnecessary technical IDs to normal ERP users.

---

# 6. CloseOnOutsideClick Rules

For data-entry, confirmation, repair, and destructive dialogs:

```razor
CloseOnOutsideClick="false"
```

is normally required.

This prevents accidental dismissal.

For harmless informational/help popups, outside-click close may be allowed.

Never enable outside-click dismissal on a popup where the user may lose:

- entered reason;
- quantity;
- allocation;
- line edits;
- unsaved transaction input.

---

# 7. Submission Safety

Any async state-changing popup action must prevent duplicate execution.

Use:

```razor
Enabled="@(!IsSubmitting)"
```

or equivalent.

Handler must also guard:

```csharp
if (IsSubmitting)
    return;
```

Then:

```csharp
IsSubmitting = true;
try
{
    ...
}
finally
{
    IsSubmitting = false;
}
```

UI disabling is not enough.

The handler guard is also mandatory.

For actions with additional eligibility:

```razor
Enabled="@(!IsSubmitting && CanPost)"
```

or:

```razor
Enabled="@(!IsSubmitting && Document.PostingEnabled)"
```

Preserve business eligibility checks.

---

# 8. Do Not Hide Important Errors Behind the Popup

When an action fails:

- preserve the popup if the user must correct input there; or
- close it only when the page-level error clearly tells the user what happened.

Never:

1. close the popup;
2. swallow the exception/error;
3. leave the user unsure whether the action succeeded.

For required popup inputs such as rollback reasons:

- validate before calling the service;
- display a clear message;
- do not silently do nothing.

Example:

```csharp
if (string.IsNullOrWhiteSpace(RollbackReason))
{
    ErrorMessage = "Rollback reason is required.";
    return;
}
```

---

# 9. Footer Visibility Audit — Mandatory Before Completion

Whenever an agent adds or changes any `DxPopup`, it MUST audit the entire affected file.

Search for:

```text
<DxPopup
FooterTemplate
FooterContentTemplate
ShowFooter
```

For every popup in that file:

```text
FooterTemplate/FooterContentTemplate + ShowFooter="true"  => PASS
FooterTemplate/FooterContentTemplate + no ShowFooter      => FAIL
FooterTemplate/FooterContentTemplate + ShowFooter="false" => FAIL unless explicitly justified
No footer template                                       => evaluate content action pattern
```

Do not review only the popup that was requested.

A file-level audit is mandatory.

---

# 10. Repository-Wide Guard

When the task involves popup infrastructure, standards, or a known popup defect class, also scan:

```text
ErpWeb.UI/**/*.razor
```

for:

```text
FooterTemplate
FooterContentTemplate
```

Every owning `DxPopup` must explicitly have:

```razor
ShowFooter="true"
```

Report all violations.

Do not stop after fixing the first one.

---

# 11. Automated Contract Test

The repository should keep a source-contract test such as:

```text
ErpWeb.Tests/Other/DxPopupFooterContractTests.cs
```

Required contract:

> Every `DxPopup` containing `FooterTemplate` or `FooterContentTemplate`
> must explicitly declare `ShowFooter="true"`.

The test should scan:

```text
ErpWeb.UI/**/*.razor
```

and fail with:

- relative file path;
- popup `HeaderText`, when available;
- reason for violation.

Example failure:

```text
DxPopup footer contract violation:
ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor
Header: Confirm Delete
FooterContentTemplate exists but ShowFooter="true" is missing.
```

Do not introduce bUnit solely for this contract.

A source-level contract test plus full Razor build is sufficient for this defect class.

---

# 12. Build Requirement

After changing popup Razor markup, always run:

```bash
dotnet build ErpWeb.slnx
```

A green service/unit test suite does not prove Razor markup compiles correctly.

If `DxPopupFooterContractTests` exists, also run:

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~DxPopupFooterContractTests"
```

Do not declare completion if either fails.

---

# 13. Manual Smoke Test Requirement

For every changed popup, manually verify:

1. popup opens;
2. header appears;
3. body/message appears;
4. all expected action buttons appear;
5. Cancel/Close works;
6. primary/destructive action has correct enabled state;
7. repeated clicks cannot double-submit;
8. popup remains usable at laptop height;
9. actions are not clipped below viewport.

For destructive actions, it is acceptable to verify rendering and use Cancel when production-like data should not be changed.

---

# 14. Responsive Popup Rules

Check at minimum:

```text
1920x1080
1366x768
narrow/mobile-width browser
```

Requirements:

- footer remains visible;
- action buttons wrap cleanly if necessary;
- content scrolls instead of pushing actions off-screen;
- no horizontal overflow;
- popup remains within viewport;
- primary action remains reachable.

Prefer a scrollable body:

```css
.popup-body {
    max-height: min(68vh, 640px);
    overflow: auto;
}
```

with footer outside the scroll region.

Do not solve clipping with arbitrary huge fixed heights.

---

# 15. Correct Repository References

Before creating a new footer-based popup, inspect current production versions of:

```text
ErpWeb.UI/Planning/Components/PrMaterialAllocationDialog.razor
ErpWeb.UI/Planning/Components/PrMaterialIssueBomDialog.razor
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor
```

These demonstrate explicit:

```razor
ShowFooter="true"
```

with footer content.

For the alternative content-action pattern, inspect current Inventory transaction list confirmations such as:

```text
ErpWeb.UI/Inventory/Transactions/IvStockTransferList.razor
ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor
```

Always read the current `production` branch before copying.

---

# 16. Review Rules for Delete/Post/Rollback Buttons

When reviewing an action popup, verify all four layers.

## Layer 1 — Visibility

Is the button actually rendered?

Check:

- `ShowFooter`;
- conditional Razor blocks;
- permissions;
- CSS;
- responsive layout.

## Layer 2 — Enabled state

Can the user click it when allowed?

Check:

- `IsSubmitting`;
- `CanDelete`;
- `CanPost`;
- `CanRollback`;
- state-specific flags.

## Layer 3 — Handler wiring

Does the button call the intended handler?

Example:

```razor
Click="@ConfirmDeleteAsync"
```

not a stale or unrelated method.

## Layer 4 — Service/domain enforcement

Does the service still enforce:

- permission;
- document status;
- concurrency;
- costing/inventory safety;
- downstream dependencies?

A visible working button is not permission enforcement.

---

# 17. Popup Anti-Patterns — Reject These

Reject an implementation if any of these are present.

### Hidden footer

```razor
<FooterContentTemplate>
    ...
</FooterContentTemplate>
```

without:

```razor
ShowFooter="true"
```

### No safe exit

```text
message visible
Delete button disabled
no Cancel
CloseOnOutsideClick=false
```

### Browser-native confirmation

```csharp
window.confirm(...)
```

Do not replace standard ERP `DxPopup` flows with browser dialogs.

### Business logic inside markup

Do not put complex posting/delete logic directly in Razor event lambdas.

Use code-behind handlers.

### Double execution

Async destructive button remains enabled while request is running.

### Silent failure

Handler returns because validation failed but shows no reason.

### Accidental data loss

Data-entry popup closes on outside click.

### Mobile clipping

Footer exists but is below the viewport and inaccessible.

### Inconsistent action order

One page shows:

```text
Delete | Cancel
```

while the ERP standard uses:

```text
Cancel | Delete
```

unless there is a strong established module reason.

---

# 18. Agent Workflow

Whenever touching a popup, follow this sequence.

## Step 1 — Read the current popup and code-behind

Identify:

- popup visibility state;
- header;
- body;
- footer/action pattern;
- handlers;
- permission/state guards;
- submission guard.

## Step 2 — Identify the popup type

Classify it:

```text
Informational
Filter
Picker
Line editor
Confirmation
Destructive confirmation
Workflow transition
Repair/recovery
```

## Step 3 — Apply the appropriate contract

If using footer template:

```text
ShowFooter="true" is mandatory.
```

If confirmation/destructive:

```text
safe exit + action button is mandatory.
```

If async state change:

```text
IsSubmitting guard is mandatory.
```

## Step 4 — Audit sibling popups

Inspect every `DxPopup` in the same `.razor` file.

Fix the same defect class if found.

Do not leave obvious sibling violations.

## Step 5 — Run repository contract scan when appropriate

Especially when:

- fixing a popup-footer bug;
- adding popup standards;
- changing a shared popup component;
- user reports the same symptom in multiple screens.

## Step 6 — Build and test

Run Razor build and popup contract test.

## Step 7 — Report

State:

```text
Files changed
Popups changed
Footer-template violations found/fixed
Build result
Contract-test result
Remaining violations
```

---

# 19. Definition of Done

A popup-related task is not complete until all applicable items pass.

- [ ] Every `FooterTemplate` / `FooterContentTemplate` popup explicitly has `ShowFooter="true"`.
- [ ] Confirmation dialogs have a visible safe exit.
- [ ] Critical action buttons are visible.
- [ ] Button order follows ERP standard.
- [ ] Button semantic styles are correct.
- [ ] Async actions block duplicate submissions.
- [ ] Handler has its own submitting guard.
- [ ] Permission/state rules remain enforced.
- [ ] Required reason/input validation is visible to the user.
- [ ] Data-entry popups do not accidentally dismiss on outside click.
- [ ] Popup remains usable at 1366x768.
- [ ] Changed file has been audited for sibling popup defects.
- [ ] Repository-wide popup contract scan was performed when task scope warrants it.
- [ ] `dotnet build ErpWeb.slnx` passes.
- [ ] `DxPopupFooterContractTests` passes when present.
- [ ] No unrelated business logic was changed.

---

# 20. Final Instruction to Coding Agents

> A popup is not correct merely because its buttons exist in Razor source. The user must be able to SEE, REACH, and USE every required action at runtime.

Before completing any `DxPopup` work:

1. verify footer visibility;
2. verify safe exit;
3. verify action enabled state;
4. verify handler wiring;
5. verify double-submit protection;
6. verify responsive reachability;
7. audit sibling popups;
8. build the Razor solution.

Never introduce a `FooterTemplate` or `FooterContentTemplate` without explicitly enabling the footer.
