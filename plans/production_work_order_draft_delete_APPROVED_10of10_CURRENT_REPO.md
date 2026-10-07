# Production Work Order Draft Delete — APPROVED 10/10 Code-Agent Implementation Plan

**Status:** APPROVED FOR IMPLEMENTATION  
**Review score:** 10/10  
**Repository:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Repository baseline verified:** commit `982e405139ae2626d287fb5a30f38c40cc1e5577` (`production costing enhancement`, 2026-10-07)  
**Target:** .NET / Blazor Server / EF Core / SQL Server production code  
**Primary implementation consumer:** Cursor / Grok / Claude Code / Codex

> This plan was re-reviewed against the current `production` HEAD above after the production-costing enhancement commit. Before implementation, re-read `production` HEAD once more. If it has moved, compare affected files and preserve newer behavior; do not blindly overwrite newer changes.

---

# Objective

Add a safe **Delete Draft Work Order** function to Production Work Order.

A user with `DELETE` permission must be able to permanently delete a Work Order that is:

1. currently `DRAFT`;
2. has never been released/reopened as historical production instruction;
3. has no production execution, posting, material issue, WIP/output, finished-good, change-order, or downstream dependency;
4. passes RowVersion concurrency validation.

Deletion must remove the Work Order planning snapshot aggregate cleanly without affecting Inventory, costing, stock posting, or historical production data.

`CANCEL DRAFT` remains a separate existing business action.

---

# Confirmed Current Problems

## 1. Work Order has no delete service contract

**File**

`ErpWeb.Core/Production/IProductionWorkOrderService.cs`

**Confirmed current behavior**

The service exposes operations including:

- `SaveDraftAsync`
- `ReleaseAsync`
- `CancelDraftAsync`
- `CreateDraftAsync`
- `UpdateDraftHeaderAsync`
- `ReopenForEditAsync`

There is no Work Order delete contract.

**Required correction**

Add an explicit server-side Draft delete command. Do not implement deletion only in the UI.

---

## 2. Work Order UI exposes Cancel Draft but not Delete Draft

**Files**

- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor.cs`

**Confirmed current behavior**

The entry page exposes `CANCEL DRAFT`.

`PrWorkOrderEntry.razor.cs` calls:

`WorkOrders.CancelDraftAsync(...)`

Cancellation changes the Work Order from `DRAFT` to `CANCELLED`; it does not delete it.

The list page currently has `NEW`, `VIEW`, and `EDIT` behavior but no `DELETE` action and does not load `PermissionCodes.Delete`.

**Required correction**

Expose a separate `DELETE DRAFT` action while preserving `CANCEL DRAFT`.

---

## 3. Planning Work Order menu does not register DELETE permission

**File**

`scripts/init-planning-workorder-menu.sql`

**Confirmed current permission set**

The Work Order menu currently ensures:

`ACCESS, ADD, EDIT, APPROVE, CANCEL, REOPEN`

`DELETE` is absent.

**Existing infrastructure**

`ErpWeb.Core/Menus/PermissionCodes.cs` already defines:

`PermissionCodes.Delete = "DELETE"`

**Required correction**

Add `DELETE` to the Work Order MenuPermission seed. Do not add a new permission code.

---

## 4. Work Order cannot be deleted safely by removing only the header

**Files**

- `ErpWeb.Model/Configurations/Production/ProductionWorkOrderConfiguration.cs`
- `ErpWeb.Model/Configurations/Production/ProductionWorkOrderOperationConfiguration.cs`
- `ErpWeb.Model/Configurations/Production/ProductionWorkOrderMaterialConfiguration.cs`
- `ErpWeb.Model/Configurations/Production/ProductionWorkOrderMachineConfiguration.cs`
- `ErpWeb.Model/Configurations/Production/ProductionWorkOrderLabourConfiguration.cs`

**Confirmed current design**

The Work Order aggregate deliberately uses mixed cascade behavior.

Examples:

- WorkOrder -> RouteSteps: `Cascade`
- WorkOrder -> Materials: `NoAction`
- WorkOrder -> Operations: `NoAction`
- WorkOrder -> AuditEvents: `Cascade`
- WorkOrder -> ChangeOrders: `Restrict`
- WorkOrder -> PostingLinks: `Restrict`
- Operation-level Labour -> Operation: `NoAction`
- Machine Labour -> Machine: `Cascade`
- Material -> WorkOrderOperation: `Cascade`
- Material -> ProducingRouteStep: `NoAction`

Therefore this is unsafe:

```csharp
db.ProductionWorkOrders.Remove(order);
```

without blocker checks and controlled child removal.

---

## 5. Existing repository already contains the correct snapshot child-removal pattern

**File**

`ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs`

**Method**

`ReplaceSnapshot(...)`

**Confirmed current behavior**

Before replacing a Draft snapshot, the service explicitly removes:

1. operation-level labours;
2. machine labours;
3. materials;
4. machines;
5. resources;
6. operations;
7. route steps.

This is the repository-grounded basis for safe Work Order snapshot deletion.

**Required correction**

Extract/reuse this logic instead of creating a second inconsistent delete order.

---

## 6. Existing Reopen logic already contains production-history blocker logic and locking rules

**File**

`ErpWeb.Core/Production/ProductionWorkOrderService.Reopen.cs`

**Confirmed current behavior**

`ReopenForEditAsync(...)` already:

- checks tenant/branch scope;
- validates RowVersion;
- uses SQL Server `UPDLOCK, HOLDLOCK` when locking Work Order;
- checks `ProductionPostingLinks`;
- checks `ProductionMaterialMovements`;
- checks header execution quantities;
- checks operation execution quantities;
- checks material execution quantities;
- checks active/applied Change Orders.

Its code explicitly documents Work Order lock ordering because Material Issue creation locks the Work Order before inserting a PostingLink.

**Required correction**

Reuse/refactor these predicates/locking rules for Draft deletion. Do not implement a weaker parallel definition of "no production activity".

---

# Business Decision

## Delete and Cancel are different

### Delete Draft

Use when the Draft was created by mistake, duplicated, used for testing, or is otherwise unwanted.

Result:

- permanently removes the never-executed planning document;
- does not keep the Work Order as `CANCELLED`;
- does not create stock/costing postings;
- does not reuse its Work Order number.

### Cancel Draft

Use when the Work Order was a legitimate business document but demand was cancelled/withdrawn.

Result:

- existing behavior remains;
- Work Order is retained as `CANCELLED`;
- cancellation reason remains mandatory;
- audit history remains visible.

Do not replace `CANCEL DRAFT` with Delete.

---

# Hard-Delete Eligibility Rule

Hard delete is allowed only when **all** conditions below are true.

## Required

1. caller has `PermissionCodes.Delete`;
2. company/branch/location write scope is valid;
3. Work Order exists inside the caller's company/branch;
4. current status is exactly `ProductionWorkOrderStatuses.Draft`;
5. request contains a non-empty RowVersion;
6. supplied RowVersion matches the locked current Work Order;
7. Work Order has never been released/reopened as a historical production instruction;
8. no downstream/execution dependency exists.

## Historical Draft rule

A Work Order that was:

`DRAFT -> RELEASED -> REOPENED -> DRAFT`

must **not** be hard-deleted.

Detect historical release using append-only Work Order audit data, including at minimum:

- `ProductionAuditEventTypes.Released`
- `ProductionAuditEventTypes.ReopenedForEdit`

Do not rely only on `ReleasedDate`, because current reopen logic clears `ReleasedDate`/`ReleasedBy`.

For a reopened historical Draft, return a clear validation/in-use message such as:

`This Work Order was previously released and cannot be permanently deleted. Cancel it or use the controlled production correction process.`

This preserves lifecycle audit integrity.

---

# Dependency / Execution Blocker Matrix

After the Work Order is locked and RowVersion is confirmed, hard delete must fail if any current repository production evidence exists.

At minimum check the following current DbSets / projections.

## Direct production dependencies

The current `production` model requires explicit blocker checks for:

- `ProductionPostingLinks` by `WorkOrderId`
- `ProductionMaterialIssueLines` by `WorkOrderId`
- `ProductionMaterialMovements` by `WorkOrderId`
- `ProductionOutputs` by `WorkOrderId`
- `ProductionChangeOrders` by `WorkOrderId`
- `ProductionBalLots` by `WorkOrderId`
- `ProductionBalLotMovements` by `WorkOrderId`
- `ProductionFinishedGoodReceiptRows` by `WorkOrderId`
- `ProductionFinishedGoodLotOriginRows` by `WorkOrderId`

`ProductionFinishedGoodLotOriginRows` is mandatory: its current EF configuration has a `Restrict` relationship to `ProductionWorkOrder`.

`ProductionBalLotMovements` MUST still be queried explicitly by `WorkOrderId`. The current entity carries `WorkOrderId`, but that scalar is not configured as a direct FK to `ProductionWorkOrder`; therefore database FK protection alone is not sufficient for this historical evidence.

`ProductionAuditEvents` are not a generic blocker because virgin Draft audit rows are part of the aggregate and currently cascade with the Work Order. However any `Released` / `ReopenedForEdit` audit history MUST block hard delete under the Historical Draft rule.

Before implementation, if current `production` HEAD introduces any additional production entity carrying a Work Order reference, add it to the hard-delete blocker scan before approval/merge.

## Header execution projection

Block if any non-zero value exists on the Work Order header, including current fields checked by Reopen logic:

- `GoodQty`
- `ScrapQty`
- `RejectQty`
- `HoldQty`
- `ApprovedVarianceQty`

## Operation execution projection

Block if any operation has a non-zero execution quantity, including:

- `InputQty`
- `ProcessedQty`
- `GoodQty`
- `ScrapQty`
- `RejectQty`
- `HoldQty`
- `ReworkQty`
- `TransferredQty`

## Material execution projection

Block if any Work Order material has a non-zero value including:

- `ReservedQty`
- `PickedQty`
- `IssuedQty`
- `ReturnedQty`
- `ConsumedQty`
- `VarianceQty`

## Change Orders

For hard-delete, use the stronger rule:

> any `ProductionChangeOrder` row for the Work Order blocks hard deletion.

Do not only check active statuses for Delete. A Change Order is historical evidence that the Work Order has entered a controlled lifecycle.

---

# Scope

Implement the following.

1. Work Order Delete service contract.
2. Server-side authorization.
3. tenant/branch validation.
4. RowVersion concurrency validation.
5. historical-release protection.
6. production/downstream dependency checks.
7. safe snapshot aggregate hard delete.
8. entry-page Delete Draft command and confirmation.
9. list-page Delete action and confirmation.
10. Work Order `DELETE` permission registration.
11. service tests.
12. mandatory SQL Server successful hard-delete/FK integration coverage in the existing SQL Server test fixture, plus concurrency/race coverage where deterministic orchestration is supported.

---

# Non-Goals

Do not include these changes in this implementation.

- deleting Released Work Orders;
- deleting In Progress Work Orders;
- deleting Completed/Closed Work Orders;
- deleting Cancelled Work Orders;
- archiving historical Work Orders;
- adding `DeletedAtUtc` to `PrWorkOrder`;
- reusing deleted Work Order numbers;
- changing Work Order numbering;
- changing Product Definition logic;
- changing Release/Reopen logic except extracting reusable private guards/locking helpers;
- changing Inventory posting;
- changing costing;
- changing Finished Good costing;
- changing Material Issue posting;
- changing Daily Production posting;
- changing global `TransactionDeletePolicyService` ownership rules.

A future historical Work Order archive design is a separate feature.

---

# Implementation Plan

## Step 1 — Add delete request/contract

**File**

`ErpWeb.Core/Production/IProductionWorkOrderService.cs`

Add a request model:

```csharp
public sealed class ProductionWorkOrderDeleteRequest
{
    public string WorkOrderNo { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
}
```

Add service contract:

```csharp
Task<IvMasterOperationResult<string>> DeleteDraftAsync(
    ProductionWorkOrderDeleteRequest request,
    CancellationToken cancellationToken = default);
```

Return the deleted Work Order number as `Data` on success.

Do not accept status, company, branch, or permission flags from the client.

---

## Step 2 — Add dedicated service implementation file

**New file**

`ErpWeb.Core/Production/ProductionWorkOrderService.Delete.cs`

Keep deletion out of the already large main service file.

Implement:

```csharp
public async Task<IvMasterOperationResult<string>> DeleteDraftAsync(
    ProductionWorkOrderDeleteRequest request,
    CancellationToken cancellationToken = default)
```

### Required command flow

1. normalize/validate Work Order number;
2. require non-empty RowVersion;
3. call existing `AuthorizeAsync(PermissionCodes.Delete, requireWriteScope: true, ...)`;
4. begin database transaction;
5. lock the Work Order using the same SQL Server lock ordering used by Reopen;
6. verify company and branch;
7. verify status is `DRAFT`;
8. compare RowVersion explicitly;
9. configure EF original RowVersion for concurrency;
10. check historical release/reopen audit events;
11. run full dependency/execution blocker check;
12. load the full tracked Work Order snapshot graph;
13. explicitly remove snapshot children in the repository-approved order;
14. remove Work Order header;
15. save once;
16. commit;
17. return deleted Work Order number.

### Error behavior

Use current repository error conventions.

- missing record -> `IvMasterErrorCode.NotFound`
- unauthorized -> `IvMasterErrorCode.AccessDenied`
- invalid current status -> `IvMasterErrorCode.Validation`
- historical/downstream dependency -> `IvMasterErrorCode.InUse`
- stale RowVersion -> `IvMasterErrorCode.Concurrency`

On `DbUpdateConcurrencyException`, rollback and return the same style of concurrency message used by Release/Reopen.

Do not catch FK exceptions and convert them to success. An unexpected FK restriction indicates a blocker was missed and must fail safely.

---

## Step 3 — Generalize Work Order locking helper

**Current file**

`ErpWeb.Core/Production/ProductionWorkOrderService.Reopen.cs`

Current helper:

`LockWorkOrderForReopenAsync(...)`

Refactor to a lifecycle-neutral private helper, for example:

`LockWorkOrderForLifecycleAsync(...)`

Preserve current SQL Server implementation:

- `UPDLOCK`
- `HOLDLOCK`
- company predicate
- branch predicate
- WorkOrderNo predicate

Use it from both:

- `ReopenForEditAsync`
- `DeleteDraftAsync`

Do not change established lock ordering.

The delete implementation must not create a new lock order that can deadlock against Material Issue creation.

---

## Step 4 — Refactor reusable execution blockers

**Current file**

`ErpWeb.Core/Production/ProductionWorkOrderService.Reopen.cs`

Current reusable concepts:

- `GetReopenPostingLinkBlockerAsync`
- `HasHeaderExecutionProjection`
- `HasExecutionProjection`
- operation quantity checks
- material quantity checks

Extract lifecycle-neutral predicates/helpers where doing so avoids duplication.

Recommended structure:

```text
GetWorkOrderExecutionBlockerAsync(...)
HasHeaderExecutionProjection(...)
HasExecutionProjection(...)
```

Reopen may continue using its reopen-specific message mapping.

Delete must additionally check all hard-delete-only dependencies listed in the blocker matrix, including any Work Order child documents that Reopen does not currently need to block.

Do not weaken Reopen behavior while refactoring.

---

## Step 5 — Extract snapshot child deletion helper

**File**

`ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs`

Current `ReplaceSnapshot(...)` already performs the proven explicit child delete sequence.

Extract the deletion portion into a private helper, for example:

```csharp
private static void RemoveSnapshotGraph(
    AppDbContext db,
    ProductionWorkOrder target)
```

Required order based on current repository model:

```text
1. direct operation labours
2. machine labours
3. Work Order materials
4. Work Order machines
5. Work Order resources
6. Work Order operations
7. Work Order route steps
```

Then:

```text
ReplaceSnapshot(...)
    -> RemoveSnapshotGraph(...)
    -> copy new snapshot
```

And:

```text
DeleteDraftAsync(...)
    -> RemoveSnapshotGraph(...)
    -> remove Work Order header
```

Do not maintain two copies of this removal sequence.

Audit events belong to the Work Order and currently cascade from the header. It is acceptable to let the configured cascade remove audit events for a virgin Draft.

Because historical Released/Reopened Drafts are blocked before hard delete, this does not erase production lifecycle history.

---

## Step 6 — Do not alter running number after delete

Current Work Order creation allocates its number from the running-number infrastructure.

After deleting, for example:

`WO00000125`

the next Work Order must continue with the next allocated number.

Do not:

- decrement `MsRunningNo`;
- recycle `WO00000125`;
- search for deleted gaps;
- renumber later Work Orders.

Gaps are expected and correct for ERP transaction numbering.

---

# Entry Page UI

## Step 7 — Load DELETE permission

**File**

`ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`

Add:

```csharp
protected bool CanDelete;
protected bool DeleteConfirmVisible;
```

Load:

```csharp
CanDelete = await AccessRights.CanAsync(
    MenuCodes.PlanningWorkOrder,
    PermissionCodes.Delete);
```

Add:

```csharp
protected bool CanDeleteAction =>
    DetailModel is { Status: ProductionWorkOrderStatuses.Draft }
    && CanDelete
    && !IsSubmitting;
```

This is only a UX gate. Server-side service validation remains mandatory.

---

## Step 8 — Add DELETE DRAFT command

**File**

`ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`

When `CanDeleteAction` is true, show:

- text: `DELETE DRAFT`
- icon: `fa-regular fa-trash-can` or repository-equivalent trash icon
- render style: `Danger`

Keep existing `CANCEL DRAFT`.

Do not rename Cancel to Delete.

---

## Step 9 — Add delete confirmation popup

**Files**

- `PrWorkOrderEntry.razor`
- `PrWorkOrderEntry.razor.cs`

Popup wording should clearly distinguish Delete from Cancel.

Recommended content:

**Header**

`Delete Draft Work Order`

**Body**

`Delete {WorkOrderNo}? This permanently removes this never-executed Draft and its planning snapshot. Use Cancel Draft instead when the Work Order is a valid business record that should remain in history.`

Buttons:

- `KEEP DRAFT`
- `DELETE DRAFT`

No delete reason is required for a virgin Draft.

On confirm:

```csharp
var result = await WorkOrders.DeleteDraftAsync(new ProductionWorkOrderDeleteRequest
{
    WorkOrderNo = DetailModel.WorkOrderNo,
    RowVersion = DetailModel.RowVersion
});
```

On success:

- close popup;
- navigate to `/planning/work-orders`.

Do not attempt to continue displaying the deleted entry.

On concurrency failure, use the page's existing concurrency/reload feedback convention.

---

## Step 9A — Update Work Order help text

**File**

`ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`

The current page contains a `How to use Work Order Entry` help section that explains `Cancel Draft`.

Update that help content so the user-visible rules match the new lifecycle:

- `DELETE DRAFT` = accidental, duplicate, test, or otherwise unwanted never-released Draft;
- `CANCEL DRAFT` = legitimate business Work Order that should remain in history;
- deleting is permanent and available only for an eligible Draft;
- cancelling keeps the Work Order as `CANCELLED` and still requires a reason.

MUST NOT tell users to Cancel every unwanted Draft after Delete Draft is available.

---

# List Page UI

## Step 10 — Add DELETE permission/action

**File**

`ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor.cs`

Add:

```csharp
protected bool CanDelete;
protected bool DeleteConfirmVisible;
protected bool IsDeleting;
protected ProductionWorkOrderDetail? DeleteTarget;
```

Load:

```csharp
CanDelete = await AccessRights.CanAsync(
    MenuCodes.PlanningWorkOrder,
    PermissionCodes.Delete);
```

Add `DELETE` to `ActionButtons`:

```text
Text: DELETE
Icon: trash
Tooltip: Delete Draft Work Order
Enabled: CanDelete
```

Current `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor` hardcodes row `ActionButtons` to `ButtonRenderStyle.Info` and does not consume `ButtonInfo.Style` for those row actions.

Therefore:

- MUST NOT claim that `Style = "danger"` will render a danger row action;
- MUST keep `CommonDataGridEx` unchanged for this feature;
- MUST use the trash icon + delete tooltip for the row action;
- MUST render the actual confirmation popup `DELETE DRAFT` button with `ButtonRenderStyle.Danger`.

Do not enable bulk row-selection merely to support delete.

---

## Step 11 — List delete preflight

In `OnActionClick(...)`, add `DELETE`.

Behavior:

1. if user lacks DELETE permission -> Access denied;
2. if selected row status is not `DRAFT` -> show `Only Draft Work Orders can be deleted.`;
3. call `WorkOrders.GetAsync(row.WorkOrderNo)` to retrieve current server state and current RowVersion;
4. if the latest row is no longer Draft -> show current-status message;
5. store latest `ProductionWorkOrderDetail` as `DeleteTarget`;
6. open confirmation popup.

This avoids adding RowVersion to `ProductionWorkOrderListRow` only for delete.

The service remains the final authority because the Work Order can change after the popup opens.

---

## Step 12 — List delete confirmation

**File**

`ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor`

Add one `DxPopup` using the repository's existing `common-popup` pattern.

Required visible actions:

- secondary `KEEP DRAFT`
- danger `DELETE DRAFT`

Both buttons must remain visible and enabled appropriately. This is important because the repository has had confirmation-dialog action visibility problems elsewhere.

On confirm:

1. call `DeleteDraftAsync(...)` with `DeleteTarget.WorkOrderNo` and `DeleteTarget.RowVersion`;
2. close popup;
3. clear `DeleteTarget`;
4. call existing `ReloadGridAsync()` so both server grid and compact preview refresh;
5. show success message containing the deleted Work Order number.

For compact/mobile list, do not add an invalid nested button inside the current whole-row `<button>`. A compact user can open the Draft and use the entry-page `DELETE DRAFT` action.

---

# Permission / Deployment Change

## Step 13 — Register DELETE for Work Order menu

**File**

`scripts/init-planning-workorder-menu.sql`

Change the Work Order permission list from:

```sql
N'ACCESS', N'ADD', N'EDIT', N'APPROVE', N'CANCEL', N'REOPEN'
```

to:

```sql
N'ACCESS', N'ADD', N'EDIT', N'DELETE',
N'APPROVE', N'CANCEL', N'REOPEN'
```

Update the script's final informational `PRINT` text accordingly.

The script is already idempotent.

Do not automatically grant DELETE to every role.

Role assignment remains an administrator/deployment decision.

No change is required in:

`ErpWeb.Core/Menus/PermissionCodes.cs`

because `PermissionCodes.Delete` already exists.

No Work Order table schema migration is required.

---

# Required Service Tests

**Primary file**

`ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderServiceTests.cs`

Add repository-grounded tests.

## Successful delete

### 1. Virgin Draft can be hard-deleted

Create Draft.

Delete with valid RowVersion and DELETE permission.

Assert:

- result succeeds;
- result data is deleted Work Order number;
- `ProductionWorkOrders` contains no row for it.

### 2. Snapshot graph is removed

Create a Work Order with current snapshot hierarchy.

After delete assert no rows remain for that WorkOrderId in:

- `ProductionWorkOrderRouteSteps`
- `ProductionWorkOrderOperations`
- `ProductionWorkOrderMaterials`
- `ProductionWorkOrderMachines`
- `ProductionWorkOrderLabours`
- `ProductionWorkOrderResources`
- `ProductionAuditEvents`

This test is mandatory because current FK design uses mixed Cascade/NoAction behavior.

### 3. Running number is not reused

Create Draft A.

Delete Draft A.

Create Draft B.

Assert B receives the next allocated Work Order number, not A's deleted number.

---

# Required Rejection Tests

## Status protection

Verify Delete fails for:

- `RELEASED`
- `IN_PROGRESS`
- `COMPLETED`
- `CLOSED`
- `CANCELLED`

Use current status constants.

Expected error: validation/in-use as appropriate.

---

## Permission protection

Create Draft with normal service.

Call delete through a SUT configured with:

`deniedPermission: PermissionCodes.Delete`

Assert:

- AccessDenied;
- Draft remains.

---

## Concurrency protection

Create Draft and capture RowVersion.

Modify the Work Order so RowVersion changes.

Call delete with stale token.

Assert:

- `IvMasterErrorCode.Concurrency`;
- Work Order remains;
- snapshot rows remain.

---

## Released -> Reopened -> Draft must not hard-delete

Use current release/reopen flow:

`Create Draft -> Release -> ReopenForEdit`

Then call delete.

Assert:

- delete fails;
- Work Order remains;
- release/reopen audit events remain.

---

## Posting Link blocker

Create Draft and seed/link a `ProductionPostingLink`.

Delete must fail with `InUse`.

Cover at least `Draft`, `Pending`, and any historical/succeeded status applicable to the seeded test.

---

## Material Issue blocker

Seed a `ProductionMaterialIssueLine` referencing the Work Order.

Delete must fail.

---

## Material movement blocker

Seed `ProductionMaterialMovement`.

Delete must fail even if net quantities would otherwise be zero.

Historical movement existence is enough to block hard delete.

---

## Production Output blocker

Seed `ProductionOutput` referencing the Work Order.

Delete must fail.

---

## WIP/Production balance blocker

Seed `ProductionBalLot` and/or `ProductionBalLotMovement` referencing the Work Order.

Delete must fail.

---

## Finished Good blocker

Seed a `ProductionFinishedGoodReceipt` or current finished-good lineage row referencing the Work Order.

Delete must fail.

---

## Change Order blocker

Seed any `ProductionChangeOrder` for the Work Order.

Delete must fail regardless of Change Order status.

---

## Quantity projection blockers

Cover at least one test for each group:

- non-zero Work Order header execution quantity;
- non-zero operation execution quantity;
- non-zero material execution quantity.

Delete must fail.

---

# SQL Server Integration / Concurrency Coverage

**Existing file**

`ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderSqlServerConcurrencyTests.cs`

The existing Work Order unit/service tests use SQLite. SQLite cannot prove the real SQL Server `Cascade / NoAction / Restrict` behavior of this aggregate.

## Mandatory SQL Server successful hard-delete test

The existing SQL Server test fixture MUST contain a real successful virgin-Draft delete test.

When the repository's scratch SQL Server connection is configured, the test MUST:

1. create a current-format virgin Draft with representative route / operation / material / machine / labour / resource snapshot rows;
2. call `DeleteDraftAsync`;
3. assert the `PrWorkOrder` row is removed;
4. assert all Work Order snapshot descendants are removed;
5. assert no SQL Server FK exception occurs;
6. assert no Inventory transaction, `StockPosting`, production movement, production valuation, or Finished Good fact is created or deleted by the operation.

The test code itself is mandatory. It may follow the existing SQL Server fixture convention of returning/skipping when no scratch SQL Server connection is configured.

## Concurrency / race coverage

Where the current fixture can deterministically orchestrate the race, add a test proving:

- delete locks the Work Order before final blocker evaluation;
- a concurrent Material Issue / execution dependency creation cannot commit in the gap between delete preflight and parent deletion;
- no forbidden state can remain where the Work Order was deleted while production evidence committed.

If deterministic race orchestration is not practical in the existing fixture, document that limitation in the test file, preserve the exact Work Order lock order used by current production services, and MUST NOT weaken locking merely to simplify the test.

---

# Existing Tests That Must Continue To Pass

Do not regress current Work Order behavior, including tests for:

- Draft create/save;
- Release;
- Cancel Draft;
- snapshot integrity;
- current-format snapshot rules;
- Refresh Definition;
- Change Definition;
- machine selection;
- material substitution;
- ReopenForEdit;
- Reopen blocker rules;
- schedule recalculation;
- SQL Server concurrency.

Also run relevant Production tests for:

- Material Issue;
- Daily Production / Production Output;
- Finished Good Receipt;
- production stock/WIP movement;
- costing/posting integrations touched by compilation or shared helpers.

---

# Costing and Inventory Safety Contract

This feature must have **zero costing or stock effect**.

A successful hard delete is valid only for a Work Order with no execution/history.

Therefore `DeleteDraftAsync` must not:

- call Inventory Post;
- call Inventory Rollback;
- create StockPosting;
- delete StockPosting;
- change FIFO/WAC/standard-cost facts;
- update inventory balances;
- update WIP balances;
- delete historical Material Movements;
- delete Finished Good facts;
- alter costing epochs;
- alter month-end data.

If any such evidence exists, deletion is blocked rather than repaired or cascaded.

---

# Transaction Boundary

`DeleteDraftAsync` must be atomic.

Within one database transaction:

```text
authorize
-> lock Work Order
-> validate status
-> validate RowVersion
-> validate historical lifecycle
-> validate all blockers
-> load tracked aggregate
-> remove snapshot graph
-> remove Work Order
-> SaveChanges
-> commit
```

Any failure:

```text
-> rollback
-> no partial child deletion
```

Do not call `SaveChangesAsync` repeatedly while progressively deleting the graph unless SQL Server FK ordering proves it is required. Prefer one tracked delete set + one SaveChanges inside the transaction.

---

# Recommended Private Helper Structure

The exact private method names may follow repository naming conventions, but the responsibilities should be equivalent to:

```text
LockWorkOrderForLifecycleAsync(...)
GetHardDeleteBlockerAsync(...)
HasHistoricalReleaseAsync(...)
HasHeaderExecutionProjection(...)
HasExecutionProjection(...)
RemoveSnapshotGraph(...)
```

Do not expose these as public APIs.

Do not introduce a new generic repository abstraction for this small change.

---

# Expected Files Changed

## Core

- `ErpWeb.Core/Production/IProductionWorkOrderService.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.Delete.cs` **new**
- `ErpWeb.Core/Production/ProductionWorkOrderService.Reopen.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs`
- optionally `ErpWeb.Core/Production/ProductionWorkOrderRules.cs` only if adding a small `CanDeleteDraft(status)` predicate improves consistency

Recommended rule if added:

```csharp
public static bool CanDeleteDraft(string? status) =>
    status == ProductionWorkOrderStatuses.Draft;
```

Do not put downstream/history checks in this simple status rule.

## UI

- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor.cs`

No CSS change should be required if existing `iv-*` / `common-popup` patterns are used.

## Security/deployment

- `scripts/init-planning-workorder-menu.sql`

## Tests

- `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderServiceTests.cs`
- `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderSqlServerConcurrencyTests.cs` — mandatory SQL Server successful-delete/FK integration test; add deterministic race coverage where the current fixture supports it

---

# Explicit Files That Should Not Need Changes

Unless current HEAD has materially changed, do not modify:

- Work Order database entity merely to add soft-delete columns;
- `PermissionCodes.cs`;
- Inventory transaction services;
- `TransactionDeletePolicyService`;
- costing services;
- Finished Good costing logic;
- stock ledger/posting logic;
- Product Definition snapshot hashing;
- Work Order numbering logic;
- shared `CommonDataGridEx` (its row action render style is currently hardcoded to Info; this feature MUST NOT refactor it solely to color the Delete icon).

If implementation starts requiring these files, stop and re-check scope before continuing.

---

# Acceptance Criteria

The implementation is complete only when all statements below are true.

1. A user with Work Order `DELETE` permission can delete a never-released Draft.
2. A user without `DELETE` permission cannot delete it.
3. Delete is enforced server-side.
4. Released/In Progress/Completed/Closed/Cancelled orders cannot be hard-deleted.
5. A released-then-reopened Draft cannot be hard-deleted.
6. Any production/downstream dependency blocks hard delete.
7. Stale RowVersion blocks delete.
8. No snapshot orphan rows remain after successful delete.
9. No inventory rows are changed by successful Draft delete.
10. No costing/posting rows are changed by successful Draft delete.
11. Work Order number is not reused.
12. `CANCEL DRAFT` continues to work unchanged.
13. Entry page exposes `DELETE DRAFT` only for an eligible current Draft + DELETE permission.
14. List page offers Delete with confirmation and reloads after success; the row action uses the current shared-grid Info rendering, while the confirmation `DELETE DRAFT` button is Danger.
15. Confirmation popup contains visible `KEEP DRAFT` and `DELETE DRAFT` actions.
16. Work Order menu security includes `DELETE`.
17. All existing Work Order lifecycle tests continue to pass.
18. New delete tests pass on the project's normal test provider.
19. A mandatory SQL Server successful hard-delete integration test proves real FK behavior produces no partial/orphan deletion.
20. No changes are made to costing authority or inventory posting behavior.
21. `ProductionFinishedGoodLotOriginRows` and `ProductionBalLotMovements` are explicitly checked as hard-delete blockers by `WorkOrderId`.
22. Work Order Help clearly distinguishes `DELETE DRAFT` from `CANCEL DRAFT`.

---

# Code-Agent Execution Order

Implement in this order:

```text
1. Add focused failing service tests for permission/status/concurrency/blocker behavior.
2. Add contract/request.
3. Refactor shared Work Order lock helper without behavior change.
4. Extract `RemoveSnapshotGraph(...)` without behavior change.
5. Add exact hard-delete blocker helper, including FG lot origin and production balance movement checks.
6. Implement `DeleteDraftAsync`.
7. Add mandatory SQL Server successful hard-delete/FK integration test and make it pass when scratch SQL Server is configured.
8. Add DELETE MenuPermission seed.
9. Add entry-page permission/button/popup and update Help content.
10. Add list-page permission/action/popup without changing `CommonDataGridEx`.
11. Run Work Order service + SQL Server tests.
12. Run Production Material Issue / Output / Finished Good regression tests.
13. Run build / relevant full regression suite.
14. Review diff for accidental costing/inventory/valuation changes.
```

Do not start with UI.

The server delete command and tests are the authority.

---

# Final Approval

**APPROVED — 10/10 against current `production` commit `982e405139ae2626d287fb5a30f38c40cc1e5577`.**

This plan is implementation-ready because it is tied to the current repository's:

- actual Work Order service contract;
- current Draft/Cancel/Reopen lifecycle;
- actual Work Order EF delete behaviors;
- existing `ReplaceSnapshot(...)` child-removal sequence;
- existing SQL Server Work Order lock ordering;
- existing Reopen execution blockers;
- actual UI pages;
- actual Work Order permission seed;
- current test project structure.

The Code Agent must preserve the central rule:

> **Hard delete only a never-released, never-executed Draft. If there is any production or lifecycle history, block deletion rather than deleting history.**

# Approval Status

APPROVED FOR IMPLEMENTATION
