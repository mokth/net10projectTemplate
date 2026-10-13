# productionv2 — Production POST / ROLLBACK Permission Simplification
## Final Second Review — APPROVED FOR IMPLEMENTATION

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Reviewed SHA:** `df7e5e8372f6b2f3e5409a16b07a8a5ed4c69c80`  
**Review status:** **APPROVED FOR IMPLEMENTATION — 10/10 plan quality**  
**Approval scope:** plan quality and implementation safety only; implementation must still pass the tests and acceptance gates below.  
**Database schema change:** **NONE**  
**Production quantity/costing algorithm change:** **NONE**  
**Inventory posting algorithm change:** **NONE**

---

# 1. Final Review Conclusion

The original plan had the correct backend direction but required two important corrections before it could be considered solid:

1. It covered the three Production **list pages**, but it did not explicitly cover all three **entry pages**.  
   The current entry pages also use `Document.PostingEnabled`, so the first version could have fixed the list toolbar while leaving POST disabled/hidden inside the document entry screen.

2. The first version proposed selection/status-dependent toolbar enablement.  
   The existing Inventory Stock Return / Stock Adjustment implementation already provides the better internal ERP standard:

   ```text
   toolbar enabled by role permission
   -> user clicks action
   -> UI validates selection/status and gives a clear message
   -> backend revalidates permission/status/stock/cost/ledger
   ```

   Production should follow that existing standard instead of inventing another special rule.

This revised plan corrects both issues.

---

# 2. Business Requirement

Apply the same simple authorization model to:

1. **Finished Good Receipt (FG)**
2. **Production Stock Return (PRTN)**
3. **Production Stock Adjustment (PADJ)**

## List pages

Toolbar actions must behave like the existing Inventory transaction lists:

```text
NEW      -> enabled by ADD permission
POST     -> enabled by POST permission
ROLLBACK -> enabled by ROLLBACK permission
```

Selection and status must be validated when the user clicks the action.

Therefore:

```text
POST clicked with no row selected
=> "Select at least one ..."

POST clicked with non-NEW rows
=> "Only NEW ... can be posted."

ROLLBACK clicked with no row selected
=> "Select at least one ..."

ROLLBACK clicked with non-POSTED rows
=> "Only POSTED ... can be rolled back."
```

Do **not** keep the toolbar permanently disabled because of hidden deployment/readiness flags.

---

# 3. Existing Internal ERP Standard to Follow

Use the existing Inventory module as the reference pattern.

Reviewed files:

```text
ErpWeb.UI/Inventory/Transactions/IvStockReturnList.razor.cs
ErpWeb.UI/Inventory/Transactions/IvStockAdjustmentList.razor.cs
```

Those pages use:

```csharp
POST Enabled = CanPost
ROLLBACK Enabled = CanRollback
```

and validate selection/status in:

```text
BeginPostAsync()
BeginRollbackAsync()
```

Production FG / PRTN / PADJ should follow the same user-facing pattern.

This is preferable because it:

- is already familiar inside this ERP;
- exposes the action whenever the user's role permits it;
- avoids unexplained permanently-disabled buttons;
- gives the user an explicit reason when the selected document is invalid;
- does not weaken backend security or stock integrity.

---

# 4. Remove Only the Rollout-Time Posting Gates

The following rollout mechanisms must no longer control normal Production transaction posting:

```text
FinishedGoodReceiptOptions.PostingEnabled

ProductionStockTransactionOptions.ReturnPostingEnabled
ProductionStockTransactionOptions.AdjustmentPostingEnabled

FinishedGoodReceiptPage.PostingEnabled
FinishedGoodReceiptDocument.PostingEnabled

ProductionStockReturnPage.PostingEnabled
ProductionStockReturnDocument.PostingEnabled

ProductionStockAdjustmentPage.PostingEnabled
ProductionStockAdjustmentDocument.PostingEnabled
```

Remove messages such as:

```text
FG posting is disabled pending release acceptance.
Production Stock Return posting is disabled pending release acceptance.
Production Stock Adjustment posting is disabled pending release acceptance.
```

These were valid during initial rollout/staging but are not normal ERP business rules.

---

# 5. Critical Safety Boundary

## DO NOT MODIFY CORE POSTING LOGIC

This task removes a redundant global feature gate.

It must **not** weaken, bypass, reorder or redesign the real backend safeguards.

The AI coding agent must preserve all of the following.

### Permission enforcement

Keep service-side authorization:

```csharp
ScopeAsync(PermissionCodes.Post, ...)
ScopeAsync(PermissionCodes.Rollback, ...)
```

The UI is never the security boundary.

### Tenant / branch / user scope

Keep:

```text
company scope
branch scope
user scope
```

### Document lifecycle

Keep:

```text
NEW    -> POST
POSTED -> ROLLBACK
ROLLBACK result -> REVERSED
```

Do not allow invalid transitions.

### Optimistic concurrency

Keep:

```text
ExpectedVersion
RowVersion
Version(...)
```

### Idempotency

Keep:

```text
RequestId
posting fingerprint
request reuse protection
replay handling
sealed posting verification
```

### Locking / transaction boundaries

Keep:

```text
database transaction
branch transaction lock
UPDLOCK / HOLDLOCK behavior
posting transaction ordering
```

### V2 ledger requirement

Keep all current checks requiring the active compatible V2 ledger.

Do not add a legacy fallback.

### Production / Inventory lineage

Keep:

```text
ProductionBalLot
ProductionBalLotMovement
PrMaterialMovement lineage
source issue movement
Inventory source balance identity
warehouse/location/lot identity
posting links
stock posting IDs
fact/reversal linkage
```

### Quantity and costing

Do not change:

```text
UOM conversion
quantity precision
money precision
rounding
FG exact value allocation
PRTN value transfer
PADJ cost authority
standard cost resolution
manual/zero-cost approval rules
Inventory valuation
StockValuationFacts
destination price evidence
```

### Rollback integrity

Keep:

```text
original posting existence
sealed original posting
later-active-movement protection
strict reversal ordering
destination stock sufficiency
price evidence protection
reversal facts
reversal posting linkage
rollback reason requirement
```

---

# 6. List Page Changes

## 6.1 Finished Good Receipt List

### File

```text
ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptList.razor.cs
```

Current:

```csharp
POST Enabled = CanPost && PostingEnabled
ROLLBACK Enabled = CanRollback
```

Target:

```csharp
POST Enabled = CanPost
ROLLBACK Enabled = CanRollback
```

Do **not** make toolbar enablement depend on:

```text
PostingEnabled
selected row count
NEW/POSTED status
readiness
```

The handler already has the correct place to validate selection and lifecycle.

### `BeginPostAsync()`

Remove only the rollout flag test.

Target sequence:

```text
1. verify CanPost
2. verify at least one selected row
3. verify every selected row is NEW
4. open confirmation
```

### `BeginRollbackAsync()`

Keep:

```text
1. verify CanRollback
2. verify selection
3. verify every selected row is POSTED
4. open confirmation
```

### Selection event

Do **not** add unnecessary `RebuildButtons()` calls based on selection.

Toolbar state is permission-based, matching Inventory.

---

## 6.2 Production Stock Return List

### File

```text
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor.cs
```

Remove the `PostingEnabled` field from list behavior.

Target toolbar:

```csharp
NEW      Enabled = CanAdd
POST     Enabled = CanPost
ROLLBACK Enabled = CanRollback
```

### POST click validation

Keep/normalize:

```text
if no selection
    "Select at least one return."

if no POST permission
    "Access denied."

if any selected row is not NEW
    "Only NEW Production Stock Returns can be posted."
```

Do not check `PostingEnabled`.

### ROLLBACK click validation

Keep:

```text
if no selection
    "Select at least one return."

if no ROLLBACK permission
    "Access denied."

if any selected row is not POSTED
    "Only POSTED Production Stock Returns can be rolled back."
```

---

## 6.3 Production Stock Adjustment List

### File

```text
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentList.razor.cs
```

Target toolbar:

```csharp
NEW      Enabled = CanAdd
POST     Enabled = CanPost
ROLLBACK Enabled = CanRollback
```

Remove all `PostingEnabled` dependence.

Selection/status validation stays inside the click handler.

---

# 7. Entry Page Changes — Required

This section was missing from the first plan and is mandatory.

---

## 7.1 Finished Good Receipt Entry

### Files

```text
ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor
```

Current code uses:

```text
Document.PostingEnabled
```

in multiple places:

- `CanPost`
- new-document initialization
- `OpenPostConfirm()`
- confirmation popup
- readiness block

All rollout-gate references must be removed.

### Visibility

Keep the useful existing concept:

```csharp
ShowPost =
    Id > 0
    && Status == NEW
    && CanPostAccess;
```

This means POST is shown only for a saved NEW document when the role has POST permission.

### Unsaved changes

Do **not** silently hide POST because the document is dirty.

If the document contains unsaved edits:

```text
user clicks POST
-> show "Save the draft before posting."
-> do not call PostAsync
```

This is a UI workflow safeguard, not a Production posting rule.

The user should understand why posting cannot proceed.

### Confirmation popup

Remove:

```text
Document.PostingEnabled
"FG posting is disabled pending release acceptance."
```

POST confirmation button should be disabled only while submitting.

### Readiness panel

Keep genuine `Document.ReadinessErrors`.

Remove only the special block:

```text
FG posting is disabled pending release acceptance.
```

Genuine readiness issues remain visible as information.

They must not replace backend validation.

---

# 8. Production Stock Return Entry

### Files

```text
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor
```

Current:

```csharp
CanPost =
    Id > 0
    && Status == "NEW"
    && CanPostAccess
    && Document.PostingEnabled
    && Document.ReadinessErrors.Count == 0
    && !Dirty;
```

This causes POST to disappear because the Razor page uses:

```razor
@if (CanPost)
```

### Replace with explicit visibility + workflow validation

Add/normalize:

```csharp
ShowPost =
    Id > 0
    && Document.Status == "NEW"
    && CanPostAccess;
```

Render the POST action when `ShowPost` is true.

Do not hide the button because of:

```text
Document.PostingEnabled
Document.ReadinessErrors
Dirty
```

### When clicked

`BeginPost()` must:

```text
1. verify ShowPost
2. if Dirty:
      display "Save the draft before posting."
      return
3. open confirmation
```

Do not block the button based on `ReadinessErrors`.

The readiness block remains visible so users can see known problems, but the backend remains authoritative when POST is attempted.

This produces clearer UX:

```text
button exists because user has permission
known issue is visible
POST attempt returns the real business validation if still invalid
```

### Why keep Dirty handling?

Posting a dirty screen without saving would post the previously persisted document, not the visible edits.

Therefore:

```text
save-before-post
```

is necessary and must remain.

Do not auto-save in this task.

---

# 9. Production Stock Adjustment Entry

### Files

```text
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor
```

Apply the same pattern as PRTN.

Target:

```csharp
ShowPost =
    Id > 0
    && Document.Status == "NEW"
    && CanPostAccess;
```

POST remains visible for the eligible saved NEW document.

On click:

```text
if Dirty
    "Save the draft before posting."
    return
```

Remove:

```text
Document.PostingEnabled
```

Do not use readiness as a hidden UI gate.

PADJ currently has no genuine service `ReadinessErrors` apart from the rollout flag, but preserve the readiness collection contract until cleanup is complete.

---

# 10. Entry Page ROLLBACK Standard

For FG, PRTN and PADJ entry screens:

```text
show ROLLBACK only when:
- document is POSTED
- user has ROLLBACK permission
```

This is a valid lifecycle rule.

ROLLBACK must not depend on any rollout feature flag.

Keep:

```text
rollback reason required
backend reversal checks
```

---

# 11. Backend FG Gate Removal

### File

```text
ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.Posting.cs
```

Remove only:

```csharp
if (!reversal && !options.Value.PostingEnabled)
    throw new FgException(
        "FG posting is disabled pending release acceptance.");
```

Do not move or change the surrounding code.

The posting pipeline remains:

```text
POST permission
-> RequestId
-> transaction
-> branch lock
-> replay / fingerprint
-> load locked document
-> RowVersion
-> lifecycle
-> date
-> Work Order / pool / source validation
-> V2 StockPosting
-> Production reduction
-> Inventory FG receipt
-> value/fact creation
-> invariants
-> seal
-> commit
```

---

# 12. Backend PRTN Gate Removal

### File

```text
ErpWeb.Core/Production/ProductionStockReturnService.Posting.cs
```

Remove only:

```csharp
if (!reversal && !options.Value.ReturnPostingEnabled)
    throw new ReturnException(
        "Production Stock Return posting is disabled pending release acceptance.");
```

Everything after it remains intact.

In particular do not change:

```text
RowVersion
NEW/POSTED lifecycle
Work Order validity
source authority
returnable quantity
source issue lineage
Inventory destination balance identity
Production pool quantity/value
StockPosting
Inventory stock-in core
valuation facts
cross-ledger invariants
rollback ordering
```

---

# 13. Backend PADJ Gate Removal

### File

```text
ErpWeb.Core/Production/ProductionStockAdjustmentService.cs
```

Inside `ExecuteAsync(...)`, remove only:

```csharp
if (!reversal && !options.Value.AdjustmentPostingEnabled)
    throw new AdjustmentException(
        "Production Stock Adjustment posting is disabled pending release acceptance.");
```

Do not modify:

```text
permission
RequestId
fingerprint
transaction
branch lock
replay
RowVersion
lifecycle
Work Order snapshot
pool lock
adjustment quantity
cost authority
price override permission
V2 ledger
facts
posting links
rollback
```

PADJ must remain Production-only.

It must create **no Inventory quantity movement**.

---

# 14. Remove Rollout-Only Readiness Messages

## FG

### File

```text
ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.cs
```

Remove only the readiness error caused by `PostingEnabled == false`.

Keep all genuine source/cost readiness errors.

---

## PRTN

### File

```text
ErpWeb.Core/Production/ProductionStockReturnService.cs
```

Remove only the rollout readiness error.

Keep:

```text
WorkOrderError(...)
source readiness
returnable quantity readiness
source lineage errors
```

---

## PADJ

### File

```text
ErpWeb.Core/Production/ProductionStockAdjustmentService.cs
```

Remove only the rollout readiness error.

Do not change PADJ cost validation.

---

# 15. Contract / Option Cleanup — Separate Cleanup Stage

This cleanup happens **after the functional patch builds and focused tests pass**.

Do not mix the first runtime behavior change with broad refactoring.

## Remove FG rollout contract

### File

```text
ErpWeb.Core/Production/IProductionFinishedGoodReceiptService.cs
```

Remove:

```text
FinishedGoodReceiptOptions
FinishedGoodReceiptPage.PostingEnabled
FinishedGoodReceiptDocument.PostingEnabled
```

Change:

```csharp
FinishedGoodReceiptPage(rows, total, postingEnabled)
```

to:

```csharp
FinishedGoodReceiptPage(rows, total)
```

---

## Remove PRTN rollout contract

### File

```text
ErpWeb.Core/Production/IProductionStockReturnService.cs
```

Remove:

```text
ProductionStockReturnPage.PostingEnabled
ProductionStockReturnDocument.PostingEnabled
```

---

## Remove PADJ rollout contract

### File

```text
ErpWeb.Core/Production/IProductionStockAdjustmentService.cs
```

Remove:

```text
ProductionStockAdjustmentPage.PostingEnabled
ProductionStockAdjustmentDocument.PostingEnabled
```

---

## Remove shared rollout options

### File

```text
ErpWeb.Core/Production/ProductionStockTransactionOptions.cs
```

After repository-wide reference search confirms no remaining use, delete the class.

It contains only:

```text
ReturnPostingEnabled
AdjustmentPostingEnabled
```

If any unexpected reference is found:

```text
STOP CLEANUP
investigate the reference
do not blindly delete it
```

The functional patch does not depend on deleting this class.

---

# 16. Service Constructor / DI Cleanup

### Files

```text
ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.cs
ErpWeb.Core/Production/ProductionStockReturnService.cs
ErpWeb.Core/Production/ProductionStockAdjustmentService.cs
ErpWeb.Core/CoreServiceCollectionExtensions.cs
```

After all gate references are gone:

Remove from FG constructor:

```text
IOptions<FinishedGoodReceiptOptions> options
```

Remove from PRTN/PADJ constructors:

```text
IOptions<ProductionStockTransactionOptions> options
```

Remove DI bindings:

```csharp
services.AddOptions<FinishedGoodReceiptOptions>()
    .BindConfiguration("Production:FinishedGoodReceipt");

services.AddOptions<ProductionStockTransactionOptions>()
    .BindConfiguration(ProductionStockTransactionOptions.SectionName);
```

Do not change service lifetimes.

Do not remove `Microsoft.Extensions.Options` from `CoreServiceCollectionExtensions.cs` if other options/validators still require it.

---

# 17. Configuration Cleanup

Reviewed current branch:

```text
ErpWeb/appsettings.json
```

contains:

```json
"Production": {
  "FinishedGoodReceipt": {
    "PostingEnabled": true
  }
}
```

Remove only that obsolete switch.

The current reviewed branch does **not** contain committed:

```text
Production:StockTransactions:ReturnPostingEnabled
Production:StockTransactions:AdjustmentPostingEnabled
```

so do not invent or add them.

Inspect environment/deployment configuration for obsolete equivalents, but do not make the application depend on them.

Do not touch unrelated Production settings.

---

# 18. SQL Seed Comment Cleanup — No SQL Behavior Change

Two seed scripts contain comments referring to the old rollout behavior.

### Files

```text
scripts/init-planning-finished-good-receipt-menu.sql
scripts/init-planning-production-stock-transactions-menu.sql
```

Update comments only so they no longer imply a posting feature switch exists.

For example:

```text
Role grants remain explicit deployment choices.
This seed does not grant POST/ROLLBACK to any role.
```

Do **not** change SQL inserts, updates, permissions or role grants.

No migration is required.

---

# 19. Permission Model — Do Not Auto-Grant

Existing menu permission seeds include:

```text
POST
ROLLBACK
```

for:

```text
PLN_FINISHED_GOOD_RECEIPT
PLN_STOCK_RETURN
PLN_STOCK_ADJUSTMENT
```

Do not automatically grant these permissions to roles.

Role assignment remains an administrator decision.

The code change means:

```text
role has POST
=> POST toolbar action is available

role does not have POST
=> POST is disabled

role has ROLLBACK
=> ROLLBACK toolbar action is available

role does not have ROLLBACK
=> ROLLBACK is disabled
```

The services independently re-check those permissions.

---

# 20. UI vs Backend Responsibility

This separation is mandatory.

## List toolbar

Answers:

```text
"Does this role have permission to invoke this action?"
```

## Entry screen

Answers:

```text
"Is this a saved document in the correct lifecycle state,
 and are there unsaved screen changes?"
```

## Backend

Answers:

```text
"Is this transaction actually valid to post/reverse right now?"
```

Backend validation includes:

```text
permission
scope
status
row version
date
Work Order
Production pool
Inventory source/destination
lot
quantity
cost
ledger
later movements
reversal integrity
```

Do not encode every backend invariant into button enablement.

That creates unexplained disabled buttons and duplicates business logic.

---

# 21. Tests — Mandatory

## 21.1 Finished Good Receipt

### File

```text
ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs
```

Current tests explicitly test `PostingEnabled` and create services with:

```text
FinishedGoodReceiptOptions { PostingEnabled = true/false }
```

Remove/update only those rollout-specific assertions/helpers.

Specifically remove the obsolete test behavior around:

```text
Assert.True(...PostingEnabled)
WithPostingEnabled(false)
Assert.False(...PostingEnabled)
"disabled pending release acceptance"
```

Update fixture constructor after the options dependency is removed.

Keep all quantity/cost/post/reversal/idempotency/failure tests.

Add/retain tests proving:

```text
POST permission required
valid NEW FG reaches normal posting path
POSTED FG cannot POST again
NEW FG cannot ROLLBACK
POSTED FG can enter reversal path
```

---

# 22. Production Stock Adjustment Tests

### File

```text
ErpWeb.Tests/Production/Transaction/ProductionStockAdjustmentServiceTests.cs
```

Current fixture creates:

```csharp
Options.Create(
    new ProductionStockTransactionOptions
    {
        AdjustmentPostingEnabled = true
    })
```

Remove this constructor argument after service cleanup.

Do not change adjustment math/cost expectations.

Required behavior tests:

```text
POST permission required
NEW PADJ posts through existing pipeline
POSTED PADJ cannot POST
NEW PADJ cannot ROLLBACK
ROLLBACK permission required
POSTED PADJ reaches reversal pipeline
```

---

# 23. Production Stock Return Test Gap

The reviewed branch does not contain a focused:

```text
ProductionStockReturnServiceTests.cs
```

Do not leave the changed PRTN path without direct service coverage.

Preferred addition:

```text
ErpWeb.Tests/Production/Transaction/ProductionStockReturnServiceTests.cs
```

Use existing Production test fixtures/helpers where practical.

Minimum coverage:

```text
POST permission required
valid NEW PRTN reaches normal posting pipeline
non-NEW PRTN cannot POST
NEW PRTN cannot ROLLBACK
ROLLBACK permission required
POSTED PRTN reaches reversal pipeline
later active movement still blocks rollback
```

Do not modify production logic merely to make the test easier to write.

---

# 24. Focused Regression Suite

At minimum run:

```text
FinishedGoodReceiptTests
ProductionStockAdjustmentServiceTests
ProductionStockAdjustmentMathTests
ProductionStockLedgerP0Tests
ProductionStockLedgerPostingTests
ProductionStockTransactionSqlServerTests
ProductionStockMaterialExecutionTests
ProductionStockLedgerSqlServerTests
StockLedgerMigrationTests
```

Also run the new/focused PRTN tests.

Then run the complete `ErpWeb.Tests` project.

Any quantity/value/ledger/reversal regression is a **STOP** condition.

Do not weaken an invariant to make a test pass.

---

# 25. Build Gate

Run:

```bash
dotnet restore
dotnet build ErpWeb.slnx
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj
```

Use the SDK pinned by the repository `global.json`.

The implementation is not approved for deployment if build/tests fail.

---

# 26. Manual List UI Acceptance

Test:

```text
/planning/finished-good-receipts
/planning/stock-returns
/planning/stock-adjustments
```

## Role with ADD only

```text
NEW      enabled
POST     disabled
ROLLBACK disabled
```

## Role with POST

Toolbar:

```text
POST enabled even before selection
```

Click with no selection:

```text
clear "select at least one" message
```

Select NEW:

```text
POST confirmation opens
```

Select POSTED/REVERSED or mixed status:

```text
clear "Only NEW documents can be posted" message
```

## Role with ROLLBACK

Toolbar:

```text
ROLLBACK enabled even before selection
```

Click with no selection:

```text
clear selection message
```

Select POSTED:

```text
rollback confirmation opens
```

Select NEW/REVERSED or mixed status:

```text
clear "Only POSTED documents can be rolled back" message
```

This exactly follows the existing Inventory transaction style.

---

# 27. Manual Entry UI Acceptance

## FG

Saved NEW + POST permission:

```text
POST visible
```

Dirty saved document:

```text
POST remains visible
click -> "Save the draft before posting."
```

Saved/clean NEW:

```text
click POST -> confirmation
```

No `pending release acceptance` message anywhere.

Posted FG + ROLLBACK permission:

```text
ROLLBACK visible
```

---

## PRTN

Saved NEW + POST permission:

```text
POST visible
```

Known readiness warning:

```text
warning remains visible
POST action does not disappear
backend decides whether posting can proceed
```

Dirty document:

```text
POST click tells user to save first
```

Posted PRTN:

```text
ROLLBACK visible only with ROLLBACK permission
```

---

## PADJ

Same behavior as PRTN.

PADJ POST must still never create Inventory quantity movement.

---

# 28. Stock / Cost Smoke Tests

Use controlled test data.

## FG

```text
create/save NEW FG
POST
verify:
- FG status POSTED
- Production FG staging decreases correctly
- Inventory FG increases correctly
- exact value/facts remain correct
ROLLBACK where eligible
verify restoration
```

## PRTN

```text
create return from material originally issued from Inventory
POST
verify:
- exact Inventory source balance receives returned quantity
- Production material pool decreases
- value lineage remains exact
ROLLBACK where eligible
verify both ledgers restore correctly
```

## PADJ

```text
create Production adjustment
POST
verify:
- Production quantity/value changes
- no Inventory quantity movement
ROLLBACK
verify Production restoration
```

---

# 29. Existing SQL / Ledger Preflight Still Applies

Removing the rollout flag does not remove the ledger infrastructure requirement.

Before production deployment, retain the existing operational checks, including:

```text
scripts/preflight-production-stock-transactions.sql
scripts/preflight-finished-good-receipt.sql
scripts/preflight-production-stock-ledger.sql
```

If required V2 ledger infrastructure is unavailable, service posting must still fail.

Do not replace that real runtime safeguard with a UI feature flag.

---

# 30. Expected Files to Change

Functional patch:

```text
ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptList.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor

ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor

ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentList.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor

ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.cs
ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.Posting.cs
ErpWeb.Core/Production/ProductionStockReturnService.cs
ErpWeb.Core/Production/ProductionStockReturnService.Posting.cs
ErpWeb.Core/Production/ProductionStockAdjustmentService.cs
```

Cleanup stage:

```text
ErpWeb.Core/Production/IProductionFinishedGoodReceiptService.cs
ErpWeb.Core/Production/IProductionStockReturnService.cs
ErpWeb.Core/Production/IProductionStockAdjustmentService.cs
ErpWeb.Core/Production/ProductionStockTransactionOptions.cs
ErpWeb.Core/CoreServiceCollectionExtensions.cs
ErpWeb/appsettings.json
scripts/init-planning-finished-good-receipt-menu.sql
scripts/init-planning-production-stock-transactions-menu.sql
```

Tests:

```text
ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs
ErpWeb.Tests/Production/Transaction/ProductionStockAdjustmentServiceTests.cs
ErpWeb.Tests/Production/Transaction/ProductionStockReturnServiceTests.cs
and existing Production ledger regression tests
```

---

# 31. Explicitly Out of Scope

Do not change:

```text
Daily Production
Issue to Production
Work Order release
Work Order completion
Work Order Transfer
Machine scheduling
BOM
Production routing
Inventory reservation
normal Inventory Stock Return posting
normal Inventory Stock Adjustment posting
Sales
Procurement
month end
stock ledger schema
Production ledger schema
lot traceability design
costing formulas
rounding formulas
```

If implementation appears to require changing any of these:

```text
STOP
review scope before continuing
```

---

# 32. Implementation Order

## Stage A — Functional simplification

1. Change FG/PRTN/PADJ list toolbar POST/ROLLBACK to permission-only enablement.
2. Keep list selection/status validation in action handlers.
3. Fix all three entry screens.
4. Remove backend forward-post rollout checks.
5. Remove rollout-only readiness messages.
6. Build.
7. Run focused tests.

## Stage B — Dead rollout-code cleanup

Only after Stage A is green:

8. Remove `PostingEnabled` fields from page/document contracts.
9. Remove options classes/dependencies.
10. Remove DI bindings.
11. Remove obsolete appsettings FG switch.
12. Update SQL seed comments only.
13. Update tests/fixtures for constructor changes.
14. Repository-wide search for:

    ```text
    PostingEnabled
    ReturnPostingEnabled
    AdjustmentPostingEnabled
    FinishedGoodReceiptOptions
    ProductionStockTransactionOptions
    pending release acceptance
    ```

    There must be no live-code references to the obsolete rollout mechanism.

15. Full build and full tests.

## Stage C — Acceptance

16. Role-permission UI matrix.
17. FG stock/cost smoke.
18. PRTN stock/value/rollback smoke.
19. PADJ Production-only smoke.
20. SQL/ledger preflight before production rollout.

---

# 33. Agent STOP Conditions

The coding agent must stop rather than broaden scope if it encounters:

```text
need to alter quantity formula
need to alter cost formula
need to change StockPosting coordinator
need to change Inventory posting service
need to change ProductionStockWriter behavior
need to change locking order
need to bypass RowVersion
need to bypass RequestId/fingerprint
need to weaken rollback rules
need to add legacy fallback
need for database schema migration
need to auto-grant user roles
```

Those are not part of this change.

---

# 34. Final Acceptance Criteria

Implementation is approved only when all statements are true.

## UI / permission

- FG list POST is controlled by POST role permission.
- FG list ROLLBACK is controlled by ROLLBACK role permission.
- PRTN list POST is controlled by POST role permission.
- PRTN list ROLLBACK is controlled by ROLLBACK role permission.
- PADJ list POST is controlled by POST role permission.
- PADJ list ROLLBACK is controlled by ROLLBACK role permission.
- selection/status errors are explained after action click.
- entry POST is not hidden by the obsolete feature gate.
- dirty entry documents tell the user to save before posting.
- no `pending release acceptance` UI remains.

## Backend security

- service POST still checks POST permission.
- service ROLLBACK still checks ROLLBACK permission.
- company/branch/user scope remains enforced.
- direct service invocation cannot bypass permission.

## Lifecycle

- only NEW posts.
- only POSTED rolls back.
- REVERSED cannot be posted/rolled back directly.
- row-version concurrency remains.

## Stock / cost

- FG quantity/value behavior unchanged.
- PRTN Inventory + Production quantity/value behavior unchanged.
- PADJ remains Production-only.
- costing/valuation formulas unchanged.
- lots/source lineage unchanged.
- V2 ledger remains mandatory.
- rollback invariants unchanged.

## Technical

- no DB schema change.
- no unrelated module change.
- solution builds.
- focused tests pass.
- complete test project passes.
- obsolete rollout references are removed after cleanup.

---

# 35. Final Approval

## **APPROVED FOR IMPLEMENTATION — 10/10 PLAN QUALITY**

This second review corrected the two material weaknesses in the previous version:

1. **all entry-page rollout-gate references are now explicitly included**, including the FG Razor popup/readiness UI; and
2. **the toolbar behavior now follows the existing Inventory ERP standard** — POST/ROLLBACK availability is permission-driven, while selection/status is validated after click.

The revised plan is intentionally narrow.

It simplifies the Production user experience without changing:

```text
Production quantities
Inventory quantities
WIP/FG balances
lot lineage
costing
valuation
StockPosting
transaction locking
Work Order logic
rollback integrity
```

Any implementation that changes those areas is outside this approval.
