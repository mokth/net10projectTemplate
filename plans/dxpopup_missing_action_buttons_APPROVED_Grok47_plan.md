# DxPopup Missing Action Buttons — Approved Fix Plan for Cursor Grok 4.7

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Scope:** UI defect only  
**Risk:** Low, but affects critical ERP actions  
**Priority:** P1  
**Target:** Fix all currently identified invisible popup-footer actions and prevent recurrence.

---

## 1. Problem

Several `DxPopup` components contain action buttons inside either:

```razor
<FooterTemplate>
```

or:

```razor
<FooterContentTemplate>
```

but the popup does not specify:

```razor
ShowFooter="true"
```

The popup therefore shows its message/body, but the footer containing buttons such as:

- Cancel
- Delete
- Post
- Rollback
- Apply
- Change
- Rebuild cost state

is not displayed.

Do **not** modify business logic, services, posting logic, costing, permissions, or transaction state rules.

This is primarily a Razor popup configuration defect.

---

# 2. Required fix strategy

For every `DxPopup` that contains `FooterTemplate` or `FooterContentTemplate`, explicitly add:

```razor
ShowFooter="true"
```

Example:

```razor
<DxPopup @bind-Visible="ConfirmVisible"
         HeaderText="Confirm"
         Width="420px"
         CloseOnOutsideClick="false"
         ShowFooter="true">
```

Do **not** solve the problem by moving footer buttons into `<BodyTemplate>` or `<Content>`.

Do **not** rewrite existing handlers.

Do **not** replace `FooterTemplate` with `FooterContentTemplate` unless there is an actual separate reason.

The goal is minimal-risk correction.

---

# 3. Confirmed production files to fix

## A. Procurement — Purchase Invoice

### File

`ErpWeb.UI/Purchase/Transactions/PoInvoiceList.razor`

### Popup 1 — Filter documents

Current popup starts with approximately:

```razor
<DxPopup @bind-Visible="FilterPopupVisible"
         HeaderText="Filter documents"
         Width="420px"
         CloseOnOutsideClick="false">
```

It contains:

```razor
<FooterTemplate>
    <DxButton Text="Clear" ... />
    <DxButton Text="Apply" ... />
</FooterTemplate>
```

Change to:

```razor
<DxPopup @bind-Visible="FilterPopupVisible"
         HeaderText="Filter documents"
         Width="420px"
         CloseOnOutsideClick="false"
         ShowFooter="true">
```

### Popup 2 — Confirm

This is critical because one confirmation popup handles:

- DELETE
- POST
- ROLLBACK

`PoInvoiceList.razor.cs` currently maps:

```text
POST     -> Post
ROLLBACK -> Rollback
default  -> Delete
```

Add:

```razor
ShowFooter="true"
```

to this popup.

### Expected result

Purchase Invoice users must see:

```text
Cancel | Delete
Cancel | Post
Cancel | Rollback
```

depending on selected action.

---

# 4. Production — Daily Production

### File

`ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor`

Fix all three confirmation dialogs.

### Popup 1

Header:

```text
Confirm Post
```

Actions:

```text
Cancel | Post
```

Add:

```razor
ShowFooter="true"
```

### Popup 2

Header:

```text
Confirm Delete
```

Actions:

```text
Cancel | Delete
```

Add:

```razor
ShowFooter="true"
```

### Popup 3

Header:

```text
Rollback Daily Production
```

Content includes the rollback reason.

Actions:

```text
Cancel | Rollback
```

Add:

```razor
ShowFooter="true"
```

### Important

Do not modify:

- `OpenPostConfirm()`
- `PostAsync()`
- `OpenDeleteConfirm()`
- `DeleteAsync()`
- `OpenRollbackConfirm()`
- `RollbackAsync()`
- rollback reason validation
- `CanPost`
- `CanDelete`
- `CanRollback`

Those handlers are already present.

---

# 5. Production — Finished Good Receipt

### File

`ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor`

There are **five affected popups**.

### Popup 1 — Add source lot

Header:

```text
Add source lot
```

Footer action:

```text
Close
```

Add:

```razor
ShowFooter="true"
```

### Popup 2 — Confirm Post

Actions:

```text
Cancel | Post
```

Add:

```razor
ShowFooter="true"
```

Do not change:

```razor
Enabled="@(!IsSubmitting && Document.PostingEnabled)"
```

### Popup 3 — Confirm Delete

Actions:

```text
Cancel | Delete
```

Add:

```razor
ShowFooter="true"
```

### Popup 4 — Rollback Finished Good Receipt

Actions:

```text
Cancel | Rollback
```

Add:

```razor
ShowFooter="true"
```

Preserve existing 500-character rollback reason handling.

### Popup 5 — Change Work Order

Actions:

```text
Cancel | Change
```

Add:

```razor
ShowFooter="true"
```

### Do not modify

- posting rules
- inventory movement
- WIP consumption
- costing
- DeleteAsync
- ApplyCommandAsync
- RollbackAsync
- work-order-change logic
- permission checks

---

# 6. Planning — Product Definition List

### File

`ErpWeb.UI/Planning/Masters/PrProductDefList.razor`

Two affected dialogs.

### Popup 1 — Filter product definitions

Currently contains:

```razor
<FooterContentTemplate>
    <DxButton ... Text="Clear" />
    <DxButton ... Text="Apply" />
</FooterContentTemplate>
```

Add:

```razor
ShowFooter="true"
```

### Popup 2 — Confirm delete

Header:

```text
Confirm delete
```

Expected:

```text
Cancel | Delete
```

Add:

```razor
ShowFooter="true"
```

Do not modify product-definition delete logic.

---

# 7. Inventory — Costing Center

### File

`ErpWeb.UI/Inventory/Inquiry/IvCostingCenter.razor`

This is especially important because costing repair is a sensitive ERP operation.

Current popup:

```razor
<DxPopup @bind-Visible="@ConfirmVisible"
         HeaderText="Confirm repair"
         Width="480px"
         CloseOnOutsideClick="false">
```

It contains:

```razor
<FooterContentTemplate>
    <DxButton
        Text="@(PreviewKind == CostingRepairTargetKind.CostState
            ? "Rebuild cost state"
            : "Rollback for correction")"
        ... />

    <DxButton
        Text="Cancel"
        ... />
</FooterContentTemplate>
```

Change popup to:

```razor
<DxPopup @bind-Visible="@ConfirmVisible"
         HeaderText="Confirm repair"
         Width="480px"
         CloseOnOutsideClick="false"
         ShowFooter="true">
```

### Expected actions

For cost-state repair:

```text
Rebuild cost state | Cancel
```

For source posting repair:

```text
Rollback for correction | Cancel
```

### Absolutely do not modify

- `CostingRepairPlanner`
- `CostingRepairService`
- costing state rebuilding
- rollback ownership
- posting reversal
- repair hash
- costing evidence
- `ConfirmRepairAsync()`

This fix is presentation only.

---

# 8. Final confirmed change count

The implementation should correct:

| File | Popups |
|---|---:|
| `PoInvoiceList.razor` | 2 |
| `PrDailyProductionEntry.razor` | 3 |
| `PrFinishedGoodReceiptEntry.razor` | 5 |
| `PrProductDefList.razor` | 2 |
| `IvCostingCenter.razor` | 1 |
| **Total** | **13** |

After changes, run another repository-wide scan. Do **not assume 13 is permanently exhaustive**.

The final scan is authoritative.

---

# 9. Add permanent regression protection

This is important. Otherwise a future AI agent may introduce exactly the same bug again.

Create:

```text
ErpWeb.Tests/Other/DxPopupFooterContractTests.cs
```

The test should inspect Razor source files under:

```text
ErpWeb.UI/**/*.razor
```

## Contract

For every `DxPopup`:

If its markup contains either:

```razor
<FooterTemplate>
```

or:

```razor
<FooterContentTemplate>
```

then its opening `DxPopup` declaration must explicitly contain:

```razor
ShowFooter="true"
```

Any violation fails the test.

The failure message must identify:

```text
relative file path
popup HeaderText if available
```

For example:

```text
DxPopup footer contract violation:
ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor
Header: Confirm Delete
FooterContentTemplate exists but ShowFooter="true" is missing.
```

## Suggested test name

```csharp
public class DxPopupFooterContractTests
{
    [Fact]
    public void Every_DxPopup_With_FooterTemplate_Must_Show_Footer()
}
```

Do not introduce bUnit for this change.

The existing `ErpWeb.Tests.csproj` does not currently use bUnit, and introducing an entire UI test framework for this small defect is unnecessary.

---

# 10. Repository-root discovery for the contract test

Do not hard-code a developer-specific directory such as:

```text
C:\Projects\...
```

Have the test walk upward from:

```csharp
AppContext.BaseDirectory
```

until it locates:

```text
ErpWeb.slnx
```

Then scan:

```text
<repo>/ErpWeb.UI/**/*.razor
```

Fail clearly if the repository root cannot be located.

This keeps the test usable in:

- Cursor
- Visual Studio
- command line
- CI
- other developer machines

---

# 11. Existing correct implementations to use as references

Do not invent another popup standard. The repository already contains correct examples.

Use these as reference:

```text
ErpWeb.UI/Planning/Components/PrMaterialAllocationDialog.razor
```

It already uses:

```razor
ShowFooter="true"
<FooterContentTemplate>
```

Also:

```text
ErpWeb.UI/Planning/Components/PrMaterialIssueBomDialog.razor
```

and:

```text
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor
```

These demonstrate the intended DevExpress footer configuration.

---

# 12. Repository-wide audit after changes

Grok must scan all:

```text
ErpWeb.UI/**/*.razor
```

for:

```text
FooterTemplate
FooterContentTemplate
```

For every occurrence, inspect its owning `DxPopup`.

Classification:

```text
Footer template + ShowFooter="true"       => PASS
Footer template + missing ShowFooter      => FIX
Footer template + ShowFooter="false"      => FIX / investigate
No footer template                        => no change required
```

Do not modify unrelated popups just for consistency.

---

# 13. Manual smoke-test matrix

After building, verify these actual workflows using a development/test database.

### Purchase Invoice

Test:

```text
FILTER
DELETE
POST
ROLLBACK
```

Verify appropriate footer buttons are visible.

For destructive operations, using **Cancel** is enough to prove rendering unless a safe test document exists.

### Daily Production

Verify:

```text
Post     -> Cancel + Post
Delete   -> Cancel + Delete
Rollback -> reason + Cancel + Rollback
```

### Finished Good Receipt

Verify:

```text
Add source lot -> Close
Post           -> Cancel + Post
Delete         -> Cancel + Delete
Rollback       -> Cancel + Rollback
Change WO      -> Cancel + Change
```

### Product Definition

Verify:

```text
Filter -> Clear + Apply
Delete -> Cancel + Delete
```

### Costing Center

Verify both repair types can display:

```text
Cancel + Rollback for correction
```

or:

```text
Cancel + Rebuild cost state
```

Do **not** perform costing repair against valuable production data just to test the UI.

---

# 14. Responsive verification

Check at least:

```text
Desktop: 1920x1080
Laptop:  1366x768
Narrow browser/mobile width
```

Requirements:

- footer visible
- buttons not clipped
- no horizontal overflow
- buttons remain clickable
- body content can scroll independently where applicable
- popup remains inside viewport

Do not add arbitrary fixed heights to solve layout problems.

---

# 15. Build and automated verification

At minimum run:

```bash
dotnet build ErpWeb.slnx
```

This is mandatory because `ErpWeb.Tests.csproj` itself notes that a green test suite does **not** prove Razor pages compile/render correctly.

Then run the new guard:

```bash
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~DxPopupFooterContractTests"
```

Both must pass with zero errors.

There is no need to run the entire 2K+ test suite solely for this UI-only change unless another source change is introduced.

---

# 16. Do not scope-creep

Grok must **not** use this task as an opportunity to:

- redesign popup styling
- redesign transaction UI
- change confirmation messages
- alter delete rules
- alter posting rules
- alter rollback eligibility
- alter permissions
- change costing architecture
- change service methods
- introduce JavaScript dialogs
- replace DevExpress `DxPopup`
- introduce new modal libraries
- add database migrations

This issue needs a focused, safe correction.

---

# 17. Definition of Done

The work is complete only when all of these are true:

1. All **13 currently confirmed** broken popups are fixed.
2. Repository-wide audit reports **zero `DxPopup` footer-template violations**.
3. Purchase Invoice Delete/Post/Rollback buttons display.
4. Daily Production Post/Delete/Rollback buttons display.
5. Finished Good Receipt Post/Delete/Rollback/Change actions display.
6. Product Definition Delete buttons display.
7. Costing Center repair confirmation actions display.
8. Existing handlers and service logic remain unchanged.
9. New `DxPopupFooterContractTests` passes.
10. `dotnet build ErpWeb.slnx` passes.
11. No database migration.
12. No new NuGet package.
13. No unrelated refactor.

---

# 18. Expected implementation size

This should be a **small, controlled patch**:

```text
5 existing Razor files modified
1 new regression-test file
0 service changes
0 database changes
0 model changes
0 business-rule changes
```

---

# 19. Final instruction to Grok 4.7

Before declaring completion, Grok should report exactly:

```text
1. Files changed
2. Every popup fixed
3. Repository-wide FooterTemplate/FooterContentTemplate audit result
4. Build result
5. Regression-test result
6. Any remaining popup violations
```

**Approval status: READY FOR IMPLEMENTATION.**

The agent must not only fix the Delete popup originally noticed. It must correct the entire `DxPopup` footer-template defect class and add a regression guard so another AI coding agent cannot quietly reintroduce it.
