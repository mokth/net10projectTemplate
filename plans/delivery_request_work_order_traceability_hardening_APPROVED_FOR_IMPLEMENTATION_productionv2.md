# Delivery Request → Work Order Traceability Hardening Plan

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Plan type:** Repository-grounded AI Code Agent execution specification

# Objective

Harden the existing Sales Order → Delivery Request → Work Order implementation in `productionv2` without redesigning the workflow.

Fix only the verified risks:

1. make Delivery Request database/schema and numbering deployment fail-safe;
2. prevent the normal Work Order preview command from being exposed on Delivery-Request-sourced Work Orders;
3. fail closed when a Work Order claims `DELIVERY_REQUEST` source type but its relational demand allocation is missing;
4. reconcile Delivery Request lifecycle status when a released Work Order is reopened or its allocation is deactivated.

# Confirmed Current Problems

## 1. Delivery Request schema deployment is not part of the documented deployment flow

**Files**
- `scripts/create-sales-delivery-request.sql`
- `DATABASE_SETUP_GUIDE.md`
- `DEPLOYMENT_GUIDE.md`
- `DEPLOYMENT_SUMMARY.md`
- `ErpWeb/Program.cs`

**Confirmed behavior**
- `create-sales-delivery-request.sql` creates:
  - `SaDeliveryRequest`
  - `SaDeliveryRequestSource`
  - `SaDeliveryRequestAudit`
  - `PrWorkOrderDemandAllocation`
- `Program.cs` only wires `AddErpWebModel` / `AddErpWebCore`; it does not automatically migrate or create the Delivery Request schema.
- Existing deployment/setup documents do not require the Delivery Request schema script.

**Required correction**
- Make the schema script an explicit mandatory deployment step before the Delivery Request menu/feature is enabled.
- Add fail-fast validation to the DR menu/numbering initialization script so it cannot enable the feature against a database missing the required DR tables.

---

## 2. Delivery Request numbering seed is hard-coded to `DEMO/HQ`

**File**
- `scripts/init-sales-delivery-request-menu.sql`

**Confirmed behavior**
- Script currently declares:
  - `@Company = 'DEMO'`
  - `@Branch = 'HQ'`
- `SaDeliveryRequestService.AllocateNumberAsync()` calls the existing document numbering authority with module `DR`.
- `DocumentNumberingService` fails when no numbering configuration exists for the actual tenant/branch.

**Required correction**
- Convert the DR numbering section into an explicit per-company/per-branch deployment input.
- The script MUST fail if placeholder/sample values were not replaced.
- The script MUST remain idempotent.

---

## 3. `CALCULATE PREVIEW` is an invalid caller for a Delivery-Request-sourced Work Order

**Files**
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.cs`

**Confirmed behavior**
- `PrWorkOrderEntry.razor` exposes `CALCULATE PREVIEW` while the Work Order is editable.
- `PrWorkOrderEntry.ProcessPreviewAsync()` calls the normal `IProductionWorkOrderService.ProcessPreviewAsync(Request)`.
- The normal Work Order draft validator in `ProductionWorkOrderService` explicitly rejects any source type other than `MANUAL`.
- A Work Order created from DR has `SourceType = DELIVERY_REQUEST`.

**Required correction**
- Do not expose the normal `CALCULATE PREVIEW` command for an existing DR-sourced Work Order.
- Add a code-behind guard so stale UI/event invocation cannot call the incompatible manual preview path.

---

## 4. Missing DR demand allocation can be silently ignored by lifecycle helpers

**File**
- `ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`

**Confirmed behavior**
- `MarkDeliveryRequestWorkOrderReleasedAsync()` resolves the DR from `entity.DemandAllocations`.
- If no request/allocation is found, the method can return without error.
- `DeactivateDeliveryRequestAllocationAsync()` has the same silent no-op pattern.
- Therefore an entity with `SourceType == DELIVERY_REQUEST` but a missing relational allocation can lose trace integrity without failing the lifecycle command.

**Required correction**
- `DELIVERY_REQUEST` source type MUST require exactly one relational `PrWorkOrderDemandAllocation`.
- Missing DR identity or missing allocation MUST fail closed with `IvMasterErrorCode.InUse`.
- Manual Work Orders MUST retain current no-op behavior.

---

## 5. DR status can remain `IN_PRODUCTION` after Work Order reopen/deactivation

**Files**
- `ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.Reopen.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.cs`

**Confirmed behavior**
- Work Order release calls `MarkDeliveryRequestWorkOrderReleasedAsync()` and stores DR status as `IN_PRODUCTION`.
- `ReopenForEditAsync()` changes a released WO back to `DRAFT` but does not reconcile the linked DR status.
- Draft cancellation deactivates the DR allocation but does not recalculate DR status.
- DR list/detail status derivation falls back to the persisted status when production quantity has not completed the request.

**Required correction**
- Add one internal DR status reconciliation helper.
- After reopen and allocation deactivation, recompute whether the DR still has any production-active Work Order.
- If no production-active WO remains, restore stored DR status to `RELEASED`.
- Do not overwrite `CANCELLED`.
- Completed display derivation based on produced quantity remains unchanged.

# Scope

Included:

- Delivery Request deployment safety.
- DR document numbering deployment safety.
- Work Order UI command gating for DR-sourced orders.
- DR ↔ WO relational allocation integrity checks.
- DR persisted status reconciliation on WO reopen/cancel-allocation paths.
- Focused automated tests.
- Deployment documentation.

# Non-Goals

MUST NOT change:

- SO → DR business concept.
- One Work Order → one Delivery Request allocation rule.
- Sales Order revision/line lineage.
- Product Definition snapshot logic.
- BOM, scheduling, material, labour, machine or costing calculations.
- Production posting, material issue, WIP, finished goods or inventory costing.
- Work Order numbering.
- Sales Order revision rules.
- Delivery Request quantity tolerance (`0.0001m`).
- Existing database table/column definitions.
- Existing DR routes.
- Existing Work Order / Sales Order routes.
- Existing document numbering engine architecture.
- Existing authorization model.

# Files to Change

## Core / Production

1. `ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`
2. `ErpWeb.Core/Production/ProductionWorkOrderService.Reopen.cs`

`ErpWeb.Core/Production/ProductionWorkOrderService.cs` only if the existing cancellation caller needs a direct reconciliation call after `DeactivateDeliveryRequestAllocationAsync`. Prefer keeping reconciliation inside the existing helper so cancellation callers do not duplicate logic.

## UI

3. `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`
4. `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`

## SQL / Deployment

5. `scripts/init-sales-delivery-request-menu.sql`
6. `DATABASE_SETUP_GUIDE.md`
7. `DEPLOYMENT_GUIDE.md`
8. `DEPLOYMENT_SUMMARY.md`

`scripts/create-sales-delivery-request.sql` should remain schema-authoritative. Modify it only if a final schema preflight assertion is needed; do not redesign its DDL.

## Tests

9. `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderServiceTests.cs`
10. `ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestServiceTests.cs` only for numbering/deployment-adjacent service assertions if required. Do not add duplicate tests already covered in the Production Work Order suite.

# Database Changes

No new tables, columns, constraints, indexes, or EF mappings are required.

The existing schema remains:

- `SaDeliveryRequest`
- `SaDeliveryRequestSource`
- `SaDeliveryRequestAudit`
- `PrWorkOrderDemandAllocation`

Existing EF configurations MUST remain unchanged unless compilation proves a direct mapping defect.

## Deployment requirement

Deployment order MUST be:

1. deploy application-compatible base schema;
2. run `scripts/create-sales-delivery-request.sql`;
3. verify all four DR tables exist;
4. run `scripts/init-sales-delivery-request-menu.sql` with explicit target company/branch numbering values;
5. verify `DR` numbering exists for the actual tenant/branch;
6. enable/use `SA_DR`.

`init-sales-delivery-request-menu.sql` MUST fail before menu activation when required DR tables are missing.

# Exact Code Changes

## A. Fail closed when DR allocation is missing

**File:** `ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`

### `MarkDeliveryRequestWorkOrderReleasedAsync(...)`

Required behavior:

```text
if entity.SourceType != DELIVERY_REQUEST
    keep current non-DR no-op behavior

if entity.SourceType == DELIVERY_REQUEST
    require one demand allocation for entity.Uid
    require DeliveryRequestId > 0
    require linked SaDeliveryRequest exists in current company/branch
    otherwise throw WorkOrderCommandException(InUse, ...)
```

After locking the DR allocation collection:

- require exactly one allocation for `entity.Uid`;
- duplicate allocations MUST fail closed even though the current unique index should prevent them;
- do not silently return.

### `DeactivateDeliveryRequestAllocationAsync(...)`

Apply the same integrity rule:

- non-DR Work Order: no-op;
- DR Work Order: missing DR allocation is an integrity failure;
- linked DR must exist in the same company/branch.

Do not create a replacement allocation automatically.

---

## B. Add one authoritative DR status reconciliation helper

**File:** `ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`

Add an internal/private helper, for example:

`ReconcileDeliveryRequestStatusAsync(...)`

The exact name may follow repository naming, but there MUST be one shared implementation.

Inputs must be sufficient to operate on the already locked DR within the existing transaction.

Required logic:

1. If DR stored status is `CANCELLED`, do nothing.
2. Load/lock its Work Order allocations using the existing DR-first lock order.
3. Join/load the linked Work Orders for the current tenant/branch.
4. Ignore inactive allocations for production-active status determination.
5. Determine `hasProductionActiveWorkOrder` when an active allocation points to a WO whose state is production-active:
   - `RELEASED`
   - `IN_PROGRESS`
   - `COMPLETED`
   - `CLOSED`
6. Also treat positive production quantity as active evidence even if a status is inconsistent.
7. If `hasProductionActiveWorkOrder == true`:
   - stored DR status = `IN_PRODUCTION`
8. Otherwise:
   - stored DR status = `RELEASED`
9. Stamp `ModifiedDate` / `ModifiedBy` only when the stored status actually changes.
10. Do not persist `COMPLETED` here; current DR derived completion logic remains authoritative for display.

MUST use existing tenant scope.

MUST NOT infer DR identity from `SourceReference`.

---

## C. Reconcile DR when a WO is reopened

**File:** `ErpWeb.Core/Production/ProductionWorkOrderService.Reopen.cs`

The existing method already calls `LockDemandBridgeBeforeWorkOrderAsync()` before locking/mutating the Work Order.

After the Work Order status is changed from `RELEASED` to `DRAFT`, and before commit:

- if the WO is DR-sourced, invoke the shared DR status reconciliation helper;
- preserve the existing transaction;
- preserve the existing demand-first lock order;
- do not deactivate the allocation on reopen;
- the allocation continues reserving DR quantity while the WO remains Draft.

Expected result:

```text
DR Released
→ WO Draft created
→ WO Released
→ DR IN_PRODUCTION
→ WO Reopened to Draft
→ DR RELEASED when no other production-active WO exists
```

If another active WO for the same DR remains released/in-progress/completed/closed, DR stays `IN_PRODUCTION`.

---

## D. Reconcile DR when allocation is deactivated

**File:** `ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`

At the end of `DeactivateDeliveryRequestAllocationAsync(...)`, after marking the allocation inactive:

- call the shared reconciliation helper before the surrounding transaction commits.

This covers existing Work Order cancellation logic without duplicating DR status rules in `ProductionWorkOrderService.cs`.

Expected result:

- cancelling one DR Work Order does not change DR to `RELEASED` if another production-active WO still exists;
- cancelling/deactivating the last production-active allocation restores DR to `RELEASED`;
- unplanned quantity becomes available through the existing active-allocation calculation.

---

## E. Prevent the incompatible normal preview call

**File:** `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`

For the existing `CALCULATE PREVIEW` menu item:

- hide it for `IsDeliveryRequestSource`, or make visibility conditional on `!IsDeliveryRequestSource`;
- do not replace it with `PreviewFromDeliveryRequestAsync` because the Work Order page does not carry the full Delivery Request creation request contract and the saved Draft already has an authoritative snapshot.

**File:** `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`

At the start of `ProcessPreviewAsync()`:

```text
if IsDeliveryRequestSource:
    set a concise user message that the saved DR Work Order already uses its linked demand snapshot
    return without calling WorkOrders.ProcessPreviewAsync(Request)
```

This is defense in depth for stale rendered UI/events.

Do not weaken `ProductionWorkOrderService` manual preview validation.

---

## F. Harden DR deployment script

**File:** `scripts/init-sales-delivery-request-menu.sql`

Before creating/enabling `SA_DR`, assert these objects exist:

- `dbo.SaDeliveryRequest`
- `dbo.SaDeliveryRequestSource`
- `dbo.SaDeliveryRequestAudit`
- `dbo.PrWorkOrderDemandAllocation`

If any is missing:

```sql
THROW ... 'Run scripts/create-sales-delivery-request.sql before enabling Delivery Request.', 1;
```

The menu script MUST NOT partially enable DR when schema is absent.

### Numbering section

Replace implicit sample deployment with explicit deployment inputs.

Required pattern:

```text
@Company = explicit deployment value
@Branch  = explicit deployment value
```

MUST fail if left blank or left on sample placeholders such as `DEMO/HQ`.

Keep existing idempotent `NOT EXISTS` behavior for `AdSmNumDate`.

Do not invent a new numbering table or numbering service.

---

## G. Update deployment documentation

**Files**
- `DATABASE_SETUP_GUIDE.md`
- `DEPLOYMENT_GUIDE.md`
- `DEPLOYMENT_SUMMARY.md`

Add one concise Delivery Request deployment section containing:

1. run `scripts/create-sales-delivery-request.sql`;
2. verify the four DR tables;
3. set real company/branch values in `scripts/init-sales-delivery-request-menu.sql`;
4. run the menu/numbering script;
5. verify `SA_DR` permissions and `DR` numbering;
6. perform one smoke test: SO → DR → WO.

Do not add unrelated deployment documentation.

# Transaction / Execution Order

## DR-created Work Order release

Preserve current order:

1. authorize;
2. lock Delivery Request;
3. lock DR allocations;
4. validate unplanned quantity;
5. build Work Order snapshot;
6. create Work Order;
7. create `PrWorkOrderDemandAllocation`;
8. save identity/row versions;
9. if releasing, release Work Order;
10. mark/reconcile DR as `IN_PRODUCTION`;
11. append DR audit;
12. commit.

No reordering of DR-first locking.

## Work Order reopen

Required order:

1. authorize;
2. begin transaction;
3. resolve Work Order identity;
4. lock DR bridge first using existing `LockDemandBridgeBeforeWorkOrderAsync`;
5. execute existing reopen blockers;
6. lock/load Work Order aggregate;
7. validate row version;
8. set WO to `DRAFT`;
9. reconcile linked DR status;
10. append existing WO audit;
11. save;
12. commit.

## Work Order cancellation / allocation deactivation

Required order:

1. preserve existing DR-first lock;
2. validate current cancellation guards;
3. mark WO cancelled;
4. deactivate DR allocation;
5. reconcile DR stored status;
6. append audit;
7. save;
8. commit.

# Authority Rules

- Sales Order production demand authority: `SaSoDetail.StdQty`.
- DR source reservation authority: active `SaDeliveryRequestSource.AllocatedProductionQty`.
- DR → WO reservation authority: active `PrWorkOrderDemandAllocation.AllocatedQty`.
- Work Order production result authority: existing Work Order production quantities/status; do not add duplicate quantity fields.
- DR `UnplannedQty` remains derived from `RequestedQty - active WO allocation`.
- DR completion remains derived from production result; do not add a new persisted completed quantity.
- `SourceReference` is display/search trace only; it MUST NOT replace relational DR allocation identity.

# Invariants

1. A `DELIVERY_REQUEST` Work Order MUST have exactly one relational `PrWorkOrderDemandAllocation`.
2. A manual Work Order MUST NOT require a DR allocation.
3. `PrWorkOrderDemandAllocation.AllocatedQty > 0`.
4. Sum of active WO allocations for a DR MUST NOT exceed `SaDeliveryRequest.RequestedQty` beyond existing `0.0001m` tolerance.
5. DR → WO identity MUST come from `DeliveryRequestId`, not `SourceReference`.
6. Reopening a WO MUST NOT release its reserved DR quantity.
7. Cancelling/deactivating a WO allocation MUST release its quantity back into DR `UnplannedQty`.
8. DR stored status is `IN_PRODUCTION` only while at least one active linked WO is production-active.
9. A cancelled DR MUST never be automatically moved back to `RELEASED` or `IN_PRODUCTION`.
10. Existing SO → DR → WO trace navigation must remain intact.

# Rollback / Reversal

No inventory or costing reversal is introduced.

All new DR status/allocation reconciliation occurs inside existing Work Order transactions.

If integrity cannot be proven:

- FAIL CLOSED;
- rollback the transaction;
- do not create a replacement allocation;
- do not guess the DR from text fields.

Work Order reopen continues to preserve historical release audit.

DR audit history remains append-only except existing never-released Draft DR physical deletion behavior.

# Concurrency / Locking

MUST preserve existing lock order:

`Delivery Request → DR allocations → Work Order`

Use existing helpers:

- `LockDemandBridgeBeforeWorkOrderAsync`
- `LockDeliveryRequestForWorkOrderAsync`
- `LockDeliveryRequestAllocationsForWorkOrderAsync`

Do not introduce a second lock strategy.

Existing SQL Server lock hints MUST remain:

- `UPDLOCK`
- `ROWLOCK`
- `HOLDLOCK`

Existing row-version checks MUST remain.

Concurrent Work Order creation/resizing MUST still prevent over-allocation.

# Tests

## `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderServiceTests.cs`

Add focused tests.

### 1. DR Work Order requires relational allocation on release

Arrange:
- DR-sourced Work Order;
- remove/corrupt its `PrWorkOrderDemandAllocation`.

Assert:
- release fails;
- error code is `InUse`;
- transaction does not change WO status;
- DR status remains unchanged.

### 2. DR Work Order requires relational allocation on cancellation

Assert:
- cancellation fails closed when `SourceType == DELIVERY_REQUEST` but allocation is missing.

### 3. Reopen last production-active WO restores DR to `RELEASED`

Flow:

```text
DR RELEASED
create/release WO
DR == IN_PRODUCTION
reopen WO
WO == DRAFT
DR == RELEASED
allocation remains active
```

Assert `UnplannedQty` remains reduced because Draft WO still reserves its quantity.

### 4. Reopen one of multiple production-active WOs keeps DR `IN_PRODUCTION`

- one WO reopened;
- another WO remains released/in-progress;
- DR stays `IN_PRODUCTION`.

### 5. Cancelling last active Draft WO allocation restores DR and quantity

Assert:

```text
allocation.IsActive == false
DR stored status == RELEASED
DR unplanned quantity increases by cancelled allocation quantity
```

### 6. Cancelling one allocation while another production-active WO exists

Assert DR remains `IN_PRODUCTION`.

### 7. Existing DR create allocation test remains green

Existing test:
`Delivery_request_creation_uses_snapshot_pipeline_and_persists_one_demand_allocation`

MUST continue to pass unchanged unless assertion strengthening is needed.

### 8. Existing locking/concurrency tests remain green

Run all Work Order service tests.

## UI verification

No Razor component test suite is currently required if none exists for this page.

Manual/interactive verification:

- DR-sourced Draft WO does not show `CALCULATE PREVIEW`;
- manual WO still shows it;
- direct/stale invocation guard does not call the normal manual preview API.

## SQL Server verification

Because the deployment scripts and lock hints are SQL Server-specific:

- execute `scripts/create-sales-delivery-request.sql` twice: second execution must succeed;
- execute DR menu script with missing DR schema: must fail before enabling `SA_DR`;
- execute with unchanged placeholder company/branch: must fail;
- execute with real test company/branch: must create/retain one `DR` numbering configuration idempotently.

# Implementation Order

## STEP 1 — Add focused failing production tests

Add tests for:

- missing DR allocation fail-closed;
- reopen DR status reconciliation;
- cancellation/deactivation reconciliation;
- multiple WO status behavior.

Do not change production code first.

## STEP 2 — Harden allocation integrity

Modify `ProductionWorkOrderService.DeliveryRequest.cs` so DR-sourced Work Orders cannot silently proceed without a relational allocation.

Run focused Work Order tests.

## STEP 3 — Add shared DR status reconciliation

Implement one helper in `ProductionWorkOrderService.DeliveryRequest.cs`.

Call it from allocation deactivation.

Run focused tests.

## STEP 4 — Wire reopen reconciliation

Update `ProductionWorkOrderService.Reopen.cs` inside the existing transaction and lock order.

Run reopen + DR tests.

## STEP 5 — Fix Work Order UI preview action

Update `.razor` visibility and `.razor.cs` guard.

Build `ErpWeb.UI`.

## STEP 6 — Harden SQL deployment

Update `scripts/init-sales-delivery-request-menu.sql`.

Verify:
- schema fail-fast;
- numbering placeholder fail-fast;
- idempotency.

## STEP 7 — Update deployment documentation

Update the three verified deployment documents only.

## STEP 8 — Focused validation

Run:

- build;
- `SaDeliveryRequestServiceTests`;
- `ProductionWorkOrderServiceTests`.

## STEP 9 — Full regression

Run the complete solution test suite.

Do not approve the implementation if any existing Sales, Production, Inventory, costing, posting, rollback, or Work Order lifecycle test regresses.

# Regression Areas

Must verify unchanged behavior in:

- Sales Order revision/delete protection from DR usage.
- DR source-line allocation.
- DR release/cancel/delete.
- Work Order draft creation from DR.
- Work Order release.
- Work Order reopen.
- Work Order cancellation.
- Work Order hard delete.
- Work Order quantity resize.
- Product Definition snapshot.
- Scheduling.
- Material requirements.
- Inventory posting.
- Production output.
- Finished Goods.
- Costing.

No Inventory or costing code should be modified by this plan.

# Do-Not Rules

DO NOT:

- create new DR tables;
- add new DR quantity columns;
- infer relational lineage from `SourceReference`;
- weaken the manual Work Order preview validator;
- auto-create a missing allocation during release/cancel/reopen;
- deactivate the DR allocation merely because a WO is reopened;
- modify costing, posting, stock, FG, WIP, BOM, machine or labour logic;
- remove existing SQL lock hints;
- bypass row-version validation;
- replace existing document numbering service;
- hard-code production tenant/company/branch values;
- perform unrelated UI standardization in this fix;
- refactor unrelated Production or Sales services.

# Acceptance Criteria

- [ ] Solution builds successfully on `productionv2`.
- [ ] `ISaDeliveryRequestService` and `IProductionWorkOrderService` signatures remain compatible with current callers.
- [ ] Existing DI registration remains valid.
- [ ] No database schema change is introduced.
- [ ] DR menu initialization fails when DR schema is missing.
- [ ] DR numbering deployment cannot silently remain configured only for `DEMO/HQ`.
- [ ] DR numbering works for an explicitly configured real test company/branch.
- [ ] DR-sourced Work Order no longer exposes the incompatible normal `CALCULATE PREVIEW` command.
- [ ] Manual Work Order preview behavior is unchanged.
- [ ] A DR-sourced WO with missing relational allocation fails closed.
- [ ] Reopening the last production-active WO returns the DR stored status to `RELEASED`.
- [ ] Reopening one WO while another production-active WO remains keeps the DR `IN_PRODUCTION`.
- [ ] Reopen keeps the DR allocation active and reserved.
- [ ] Cancelling/deactivating the last allocation restores DR available quantity.
- [ ] Cancelling one allocation does not incorrectly downgrade a DR while another production-active WO exists.
- [ ] Cancelled DR status is never overwritten by reconciliation.
- [ ] SO → DR → WO navigation still works.
- [ ] WO → DR → SO navigation still works.
- [ ] Existing DR creation/allocation tests pass.
- [ ] Existing Work Order release/reopen/delete/cancel tests pass.
- [ ] Full test suite passes.
- [ ] No Inventory/costing/posting behavior changes.

# Approval Status

**APPROVED FOR IMPLEMENTATION**
