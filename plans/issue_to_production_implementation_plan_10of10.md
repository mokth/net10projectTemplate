# Issue to Production — 10/10 Implementation Plan
## Target: `mokth/net10projectTemplate` — `production`

**Plan status:** APPROVED / implementation-ready  
**Prepared from:** `Issue_to_Production_Reverse_Engineering_Study.md` + direct verification of the current `production` branch  
**Repository baseline verified:** `e1a41fed8114f3373bcabe99bfdcd8931b49bf40`  
**Goal:** Implement a production-grade **Issue to Production (IP)** workflow in the Blazor Server ERP without copying the old Web Forms design and without duplicating existing inventory / Work Order architecture.

---

# 1. Final Architecture Decision

The implementation should use the following model:

```text
Released / In-Progress Work Order
        ↓
ProductionWorkOrderMaterial
(exact WorkOrderMaterialId + WorkOrderOperationId)
        ↓
Issue-to-Production workspace
(required / issued / returned / outstanding / available)
        ↓
User chooses material quantities + stock lots
        ↓
ProductionMaterialIssueService
        ↓
ONE SQL transaction
        ├─ idempotency via PrProductionPostingLink
        ├─ lock Work Order + material rows
        ├─ validate outstanding / tolerance
        ├─ lock selected IvBalLoc rows
        ├─ create IvTrxBatch / IvTrxBatchDetail (TrxType = IP)
        ├─ call existing PostStockOutInTransactionAsync(..., "IP")
        ├─ insert immutable PrMaterialMovement facts
        ├─ rebuild Work Order material execution projections
        └─ RELEASED → IN_PROGRESS when first execution occurs
        ↓
COMMIT
```

## Non-negotiable decisions

1. **`ProductionWorkOrderMaterial` is the requirement source.**
   - Never use live `PrDefBOM` when issuing against a released Work Order.
   - Never recalculate a released Work Order from current master data during material issue.

2. **Keep inventory posting in the existing inventory engine.**
   - Reuse:
     - `IvTrxBatch`
     - `IvTrxBatchDetail`
     - `IvTrxHistory`
     - `IvBalLoc`
     - `IIvInventoryPostingService`
     - `IIvStockPostingRepository`
   - `IvTrxTypes.IssueToProduction = "IP"` already exists.

3. **Do not add `IP` to the generic public inventory dispatcher.**
   - An IP transaction must always go through the production service.
   - This prevents stock from being issued with no Work Order/material execution facts.

4. **Add exactly one new production execution fact table: `PrMaterialMovement`.**
   - Do not add a separate WIP balance table yet.
   - Do not add another full header/detail issue subsystem.
   - `IvTrxBatch` remains the actual inventory document.
   - `PrProductionPostingLink` remains the command/idempotency header.
   - `PrMaterialMovement` becomes the immutable production-side execution ledger.

5. **Issue is not consumption.**
   - Issue = warehouse stock released to production/WIP.
   - Consumption will be a future explicit movement.
   - Do not update `ConsumedQty` from Issue to Production.

6. **Manual Issue to Production is allowed only for:**
   - Work Order status `RELEASED`
   - Work Order status `IN_PROGRESS`

7. **Fail closed for unsupported material execution modes in this milestone.**
   - `IssueMethod = MANUAL` → allowed.
   - `IssueMethod = BACKFLUSH` → not manually issuable.
   - `IssueMethod = PICK_LIST` → block until pick-list/reservation execution exists.
   - `SupplySource = INTERNAL_ROUTE_WIP` → not warehouse-issued through this page.
   - `SupplySource = SEPARATE_PRODUCT_DEFINITION` → block in this milestone.
   - `PURCHASED` / `EXTERNAL_SUPPLY` → normal warehouse issue when stock-controlled.

8. **Required UOM and inventory Base UOM remain separate.**
   - Operator requirement quantities use `ProductionWorkOrderMaterial.RequiredUom`.
   - Inventory stock-out uses `RequiredBaseQty` / `BaseUom`.
   - Use the Work Order snapshot `ConversionFactorToBase`.

9. **Posted facts are authoritative.**
   - `IssuedQty`, `ReturnedQty`, `ConsumedQty`, `VarianceQty` remain rebuildable projections.
   - Never update them directly from client values.

10. **No global application posting lock.**
    - Use Work Order/material row locks plus existing deterministic stock row locks.

---

# 2. Existing Repository Components to Reuse

The current project already contains most of the foundation required.

| Existing component | Use |
|---|---|
| `ProductionWorkOrder` | Work Order execution header |
| `ProductionWorkOrderOperation` | Exact consuming process |
| `ProductionWorkOrderMaterial` | Frozen Work Order material requirement |
| `ProductionWorkOrderCalc` | Net-issued / open-requirement formulas |
| `PrProductionPostingLink` | Idempotency + production posting audit |
| `IvTrxBatch` | IP inventory document header |
| `IvTrxBatchDetail` | Physical stock allocation lines |
| `IvTrxHistory` | Inventory posted movement |
| `IvBalLoc` | Authoritative warehouse/location/lot balance |
| `IvLot` | Lot metadata / expiry date |
| `IvInventoryPostingService` | Existing stock-out post/rollback engine |
| `PostStockOutInTransactionAsync` | Production IP posting inside caller transaction |
| `RollBackStockOutInTransactionAsync` | Immediate IP rollback inside caller transaction |
| `IIvStockPostingRepository` | SQL row locks and atomic balance updates |
| `IUomConversionService` | Existing UOM authority |
| `IRunningNumberService` | Reuse `RunningNumberKeys.IvBatch` |
| `IInventoryTenantContext` | Company/branch/user scope |
| `IAccessRightService` | Server-side permissions |
| `IvPeriodCloseGuard` | Posting/rollback period protection |
| `AppDbContext.ApplyConfigurationsFromAssembly(...)` | Automatic new entity configuration discovery |

### Important verified inventory behavior

The existing stock-out posting core already:

```text
locks the transaction batch
loads stock masters
validates source balance identity
locks IvBalLoc rows in deterministic order
aggregates required quantity per balance row
rechecks on-hand while locked
uses conditional decrement: StdQty >= required
creates IvTrxHistory
marks batch POSTED
supports rollback
```

Therefore **do not rewrite stock posting logic in Production**.

---

# 3. Business Rules

## 3.1 Quantity definitions

For every `ProductionWorkOrderMaterial`:

```text
RequiredQty
    = existing Work Order snapshot RequiredQty

IssuedQty
    = effective posted ISSUE quantity
      excluding rolled-back ISSUE facts

ReturnedQty
    = future Return-from-Production quantity

NetIssuedQty
    = IssuedQty - ReturnedQty

OutstandingQty
    = max(RequiredQty - NetIssuedQty, 0)

MaxAllowedNetIssue
    = Round4(RequiredQty × (1 + Tolerance / 100))

WipMaterialQty
    = NetIssuedQty - ConsumedQty
```

Reuse:

```csharp
ProductionWorkOrderCalc.NetIssuedQty(...)
ProductionWorkOrderCalc.OpenRequirementQty(...)
```

### Posting validation

For a new issue:

```text
NewNetIssued = CurrentNetIssued + IssueQty

Require:
IssueQty > 0

and

NewNetIssued <= MaxAllowedNetIssue
```

If `Tolerance = 0`, the issue may not exceed the requirement.

Do not add a general “override tolerance” permission in this milestone.

---

## 3.2 Partial issue

Partial and repeated issue is explicitly supported.

Example:

```text
Required       = 100 KG
Issue #1       = 40 KG
Issue #2       = 30 KG
Net Issued     = 70 KG
Outstanding    = 30 KG
```

Next load should default `Issue Now = 30 KG`, not 100 KG.

---

## 3.3 Work Order status

### Allowed

```text
RELEASED
IN_PROGRESS
```

### Blocked

```text
DRAFT
COMPLETED
CLOSED
CANCELLED
```

### First successful issue

If current status is:

```text
RELEASED
```

then change to:

```text
IN_PROGRESS
```

inside the same transaction as the inventory post.

Once a Work Order reaches `IN_PROGRESS`, **do not automatically regress it to RELEASED after rollback**.

---

## 3.4 Snapshot consistency

Every post request must include:

```text
WorkOrderNo
SnapshotRevision
SnapshotHash
```

Before posting:

```text
request.SnapshotRevision == current SnapshotRevision
request.SnapshotHash == current SnapshotHash
```

If not:

```text
reject as stale Work Order snapshot
reload required
```

Do not rely only on `RowVersion` for the screen because normal material execution may update the Work Order row and cause unnecessary stale conflicts.

---

## 3.5 Material issue-method policy

### MANUAL

Normal Issue-to-Production page.

### BACKFLUSH

Display as:

```text
Backflush — issued automatically by production output
```

Do not allow manual IP posting.

### PICK_LIST

Display as:

```text
Pick List — execution not enabled yet
```

Do not bypass the future reservation/picking workflow.

---

## 3.6 Supply-source policy

### PURCHASED

Issue from warehouse stock.

### EXTERNAL_SUPPLY

Allow warehouse issue when the component is stock-controlled.

### INTERNAL_ROUTE_WIP

Do not show normal warehouse allocation.

Display:

```text
Supplied by internal route/WIP
```

Future execution should consume from the producing route step.

### SEPARATE_PRODUCT_DEFINITION

Block in this milestone.

Display:

```text
Supplied by separate production definition/work order
```

This avoids incorrectly treating an internally produced subassembly as normal raw-stock issue.

---

# 4. Stock / Warehouse / Location / Lot Rules

## 4.1 Source warehouse

Use the frozen Work Order material value:

```text
ProductionWorkOrderMaterial.WarehouseCode
```

Do not fall back to the current Product Definition after release.

The snapshot builder already resolves a BOM/item default into the Work Order material where available.

If no warehouse exists:

```text
block material issue
```

---

## 4.2 Source location

If:

```text
ProductionWorkOrderMaterial.LocationCode != null/blank
```

then candidate stock must match that location.

If Work Order material location is blank:

```text
allow any valid location within the Work Order material warehouse
```

The user may choose among available candidate locations.

---

## 4.3 Valid stock candidate

A stock slice is eligible only when all are true:

```text
CompanyCode matches
BranchCode matches
ICode == ComponentCode
WHCode == material WarehouseCode
LocCode matches material LocationCode when specified
IStatus == ACTIVE
StdQty > 0
stock master is active
stock master is stock-controlled
```

For lot-controlled items additionally require:

```text
IvLot exists
IvLot.IsActive == true
LotNo is not blank
```

Expired lot:

```text
ExpiryDate < IssueDate
```

must not be automatically or manually selected.

Future issue dates are not allowed.

---

# 5. Allocation Policy

Create:

```text
IProductionMaterialAllocationService
ProductionMaterialAllocationService
```

## 5.1 Automatic allocation

### Lot with expiry date

Use FEFO:

```text
ExpiryDate ASC
TransDate ASC
LotNo ASC
IvBalLoc.ID ASC
```

### Lot without expiry date

Use FIFO-like allocation:

```text
TransDate ASC
LotNo ASC
IvBalLoc.ID ASC
```

### Non-lot item

Use:

```text
TransDate ASC
IvBalLoc.ID ASC
```

---

## 5.2 Allocation result

Each proposed allocation returns:

```text
FromBalLocId
Warehouse
Location
LotId
LotNo
ExpiryDate
StockDate
AvailableBaseQty
BaseUom
SuggestedBaseQty
UnitPrice (only if VIEW_COST)
```

Allocation is only a **proposal**.

The posting transaction must lock and revalidate every selected balance row.

---

# 6. New Database Table — `PrMaterialMovement`

This is the only required new production execution table for IP.

## 6.1 Purpose

`PrMaterialMovement` provides the missing immutable connection:

```text
exact Work Order
+ exact Work Order material
+ exact operation
+ exact inventory batch/detail
+ exact lot/balance slice
+ exact production movement semantic
+ quantity/UOM
+ cost
+ reversal lineage
```

It becomes the source for rebuilding Work Order material projections.

---

## 6.2 Entity

Create:

```text
ErpWeb.Model/Entities/Production/ProductionMaterialMovement.cs
```

Recommended properties:

```csharp
public long Uid { get; set; }

public string CompanyCode { get; set; }
public string BranchCode { get; set; }

public long WorkOrderId { get; set; }
public long WorkOrderMaterialId { get; set; }
public long WorkOrderOperationId { get; set; }

public string MovementType { get; set; }

public DateTime MovementDate { get; set; }

public string ItemCode { get; set; }

public decimal Qty { get; set; }
public string Uom { get; set; }

public decimal BaseQty { get; set; }
public string BaseUom { get; set; }
public decimal ConversionFactorToBase { get; set; }

public string WarehouseCode { get; set; }
public string LocationCode { get; set; }
public string LotNo { get; set; }
public int? LotId { get; set; }
public int FromBalLocId { get; set; }
public string ItemStatus { get; set; }

public int InventoryBatchId { get; set; }
public int InventoryBatchNo { get; set; }
public int InventoryBatchDetailId { get; set; }
public short InventoryTrxLineNo { get; set; }

// informational snapshot only — do not FK because current inventory rollback
// removes IvTrxHistory rows.
public int? InventoryHistoryId { get; set; }

public string? InventoryPostingOperationId { get; set; }

public decimal UnitCost { get; set; }
public decimal TotalCost { get; set; }

public long PostingLinkId { get; set; }

public long? OriginalMovementId { get; set; }

public string? Reason { get; set; }
public string? Remarks { get; set; }

public DateTime CreatedDate { get; set; }
public string CreatedBy { get; set; }
```

### Navigation properties

Add navigation to:

```text
ProductionWorkOrder
ProductionWorkOrderMaterial
ProductionWorkOrderOperation
ProductionPostingLink
OriginalMovement
IvTrxBatch
IvTrxBatchDetail
IvBalLoc
IvLot
```

Do **not** create an FK to `IvTrxHistory` because the current rollback implementation physically removes history.

---

# 7. Production Movement Types

Add to:

```text
ErpWeb.Model/Entities/Production/ProductionDomainConstants.cs
```

```csharp
public static class ProductionMaterialMovementTypes
{
    public const string Issue = "ISSUE";
    public const string IssueReversal = "ISSUE_REVERSAL";

    // Reserved now so later execution uses the same ledger.
    public const string Return = "RETURN";
    public const string Consume = "CONSUME";
    public const string Adjustment = "ADJUST";
}
```

Also add command/document constants:

```csharp
public static class ProductionPostingCommandTypes
{
    public const string MaterialIssuePost = "MATERIAL_ISSUE_POST";
    public const string MaterialIssueRollback = "MATERIAL_ISSUE_ROLLBACK";
}

public static class ProductionDocumentTypes
{
    public const string MaterialIssue = "MATERIAL_ISSUE";
}
```

Audit event constants:

```csharp
ProductionAuditEventTypes.MaterialIssued
ProductionAuditEventTypes.MaterialIssueRolledBack
```

---

# 8. EF Configuration

Create:

```text
ErpWeb.Model/Configurations/Production/ProductionMaterialMovementConfiguration.cs
```

## Required mapping

Use:

```text
table: PrMaterialMovement
PK: UID bigint identity
Qty / BaseQty: decimal(18,4)
ConversionFactorToBase: decimal(18,8)
UnitCost / TotalCost: decimal(18,4)
CompanyCode: nvarchar(5)
BranchCode: nvarchar(5)
ItemCode: nvarchar(30)
WarehouseCode: nvarchar(20)
LocationCode: nvarchar(10)
LotNo: nvarchar(50)
Uom / BaseUom: nvarchar(10)
ItemStatus: nvarchar(10)
MovementType: nvarchar(20)
InventoryPostingOperationId: nvarchar(64)
Reason: nvarchar(50)
Remarks: nvarchar(250)
CreatedBy: nvarchar(10)
```

## Required constraints

```text
Qty > 0
BaseQty > 0
ConversionFactorToBase > 0
UnitCost >= 0
TotalCost >= 0
MovementType in supported values
```

## Required FKs

Use `DeleteBehavior.Restrict`.

```text
WorkOrderID → PrWorkOrder
WorkOrderMaterialID → PrWorkOrderMaterial
WorkOrderOperationID → PrWorkOrderOperation
PostingLinkID → PrProductionPostingLink
OriginalMovementID → PrMaterialMovement
InventoryBatchID → IvTrxBatch
InventoryBatchDetailID → IvTrxBatchDetail
FromBalLocID → IvBalLoc
LotID → IvLot
```

## Required indexes

```text
IX_PrMaterialMovement_WorkOrder_Date
    (WorkOrderID, MovementDate)

IX_PrMaterialMovement_Material_Type
    (WorkOrderMaterialID, MovementType)

IX_PrMaterialMovement_Operation_Date
    (WorkOrderOperationID, MovementDate)

IX_PrMaterialMovement_InventoryBatch
    (CompanyCode, BranchCode, InventoryBatchNo)

IX_PrMaterialMovement_OriginalMovement
    (OriginalMovementID)

UQ_PrMaterialMovement_PostingLine
    (PostingLinkID, InventoryBatchDetailID, MovementType)
```

This prevents the same posting-link line from being recorded twice.

---

# 9. `AppDbContext`

Modify:

```text
ErpWeb.Model/Data/AppDbContext.cs
```

Add:

```csharp
public DbSet<ProductionMaterialMovement> ProductionMaterialMovements
    => Set<ProductionMaterialMovement>();
```

No manual configuration registration is required because the project already uses:

```csharp
modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
```

---

# 10. SQL Deployment

## 10.1 New incremental script

Create:

```text
scripts/create-production-material-issue.sql
```

Requirements:

```text
SET NOCOUNT ON
SET XACT_ABORT ON
idempotent CREATE/ALTER behavior
PrMaterialMovement table
constraints
foreign keys
indexes
verification section
```

The script must be safe to rerun.

## 10.2 Update base production schema

Modify:

```text
scripts/create-production-workorder.sql
```

Add `PrMaterialMovement` so clean installations receive the table.

Also add it to the final schema verification list.

---

# 11. Production Material Issue Contracts

Create:

```text
ErpWeb.Core/Production/IProductionMaterialIssueService.cs
```

Keep DTOs in the same file, following the current `IProductionWorkOrderService.cs` style.

## 11.1 Workspace query

```csharp
Task<IvMasterOperationResult<ProductionMaterialIssueWorkspace>>
    GetWorkspaceAsync(
        string workOrderNo,
        long? operationId = null,
        CancellationToken cancellationToken = default);
```

### `ProductionMaterialIssueWorkspace`

```text
WorkOrderId
WorkOrderNo
Status
ProductCode
ProductDescription
PlannedQty
OutputUom
SnapshotRevision
SnapshotHash
IssueDate
RouteSteps
Operations
Materials
CanViewCost
```

### Material VM

```text
WorkOrderMaterialId
WorkOrderOperationId
OperationCode
WorkCentreCode
ComponentCode
Description
IssueMethod
SupplySource
RequiredQty
RequiredUom
RequiredBaseQty
BaseUom
ConversionFactorToBase
IssuedQty
ReturnedQty
NetIssuedQty
ConsumedQty
OutstandingQty
TolerancePercent
MaxAllowedNetIssue
AvailableBaseQty
AvailableQty
ShortageQty
WarehouseCode
LocationCode
LotControl
CanManualIssue
BlockingReason
```

---

## 11.2 Stock candidates

```csharp
Task<IvMasterOperationResult<IReadOnlyList<ProductionMaterialStockCandidate>>>
    GetStockCandidatesAsync(
        long workOrderMaterialId,
        DateTime issueDate,
        CancellationToken cancellationToken = default);
```

---

## 11.3 Auto allocation

```csharp
Task<IvMasterOperationResult<ProductionMaterialAllocationResult>>
    AutoAllocateAsync(
        ProductionMaterialAllocationRequest request,
        CancellationToken cancellationToken = default);
```

Request:

```text
WorkOrderMaterialId
IssueDate
RequestedQty
```

Result:

```text
RequestedQty
RequestedBaseQty
AllocatedBaseQty
ShortBaseQty
Allocations[]
```

---

# 12. Post Request Contract

```csharp
public sealed class ProductionMaterialIssuePostRequest
{
    public string PostingRequestId { get; set; }
    public string WorkOrderNo { get; set; }
    public int SnapshotRevision { get; set; }
    public string SnapshotHash { get; set; }
    public DateTime IssueDate { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<ProductionMaterialIssueLineRequest> Lines { get; set; }
}
```

Line:

```csharp
public sealed class ProductionMaterialIssueLineRequest
{
    public long WorkOrderMaterialId { get; set; }

    // Quantity in ProductionWorkOrderMaterial.RequiredUom
    public decimal IssueQty { get; set; }

    public IReadOnlyList<ProductionMaterialIssueAllocationRequest> Allocations { get; set; }
}
```

Allocation:

```csharp
public sealed class ProductionMaterialIssueAllocationRequest
{
    public int FromBalLocId { get; set; }

    // Physical inventory quantity in material BaseUom
    public decimal BaseQty { get; set; }
}
```

### Server validation

For each line:

```text
IssueQty > 0

RequestedBaseQty
    = Round4(IssueQty × ConversionFactorToBase)

sum(Allocation.BaseQty)
    == RequestedBaseQty

within quantity rounding tolerance of 0.0001
```

Never trust client item code, warehouse, location, lot, cost, UOM, tolerance, available quantity or outstanding quantity.

Resolve all of them from server-side Work Order / inventory rows.

---

# 13. `ProductionMaterialIssueService`

Create:

```text
ErpWeb.Core/Production/ProductionMaterialIssueService.cs
```

Dependencies:

```text
IDbContextFactory<AppDbContext>
IInventoryTenantContext
IAccessRightService
IRunningNumberService
ICurrentDateService
IIvStockPostingRepository
IIvStockTransactionRepository
IIvInventoryPostingService
IProductionMaterialAllocationService
ILogger<ProductionMaterialIssueService>
```

Do not inject `IProductionWorkOrderService` into this service. Work directly with the frozen entities to avoid circular orchestration.

---

# 14. Exact Post Transaction Algorithm

`PostAsync(...)` is the most critical method.

## Step 1 — authorization / tenant

Require:

```text
Menu: PLN_MATERIAL_ISSUE
Permission: ACCESS
Permission: ADD
Permission: POST
```

Require valid:

```text
CompanyCode
BranchCode
UserId
```

---

## Step 2 — basic request validation

Require:

```text
PostingRequestId not blank
valid GUID/string format
WorkOrderNo not blank
IssueDate <= today
at least one line
no duplicate WorkOrderMaterialId lines
every IssueQty > 0
every allocation BaseQty > 0
```

Limit line/allocation count to a reasonable upper bound to avoid accidental massive posts.

Suggested:

```text
max material lines = 200
max total allocation rows = 1000
```

---

## Step 3 — start transaction

```csharp
await using var db = await _dbFactory.CreateDbContextAsync(ct);
await using var tx = await db.Database.BeginTransactionAsync(ct);
```

All remaining operations occur inside this transaction.

---

## Step 4 — idempotency

Use existing:

```text
PrProductionPostingLink
```

Identity:

```text
CompanyCode
BranchCode
CommandType = MATERIAL_ISSUE_POST
PostingRequestId
```

Behavior:

### Existing `SUCCEEDED`

Return the original success result:

```text
InventoryBatchNo
PostingOperationId
```

Do not post again.

### Existing `REVERSED`

Return an error:

```text
The original request was already posted and reversed.
Generate a new request to issue again.
```

### Existing `PENDING`

Treat as duplicate/concurrent request.

### No row

Insert:

```text
Status = PENDING
WorkOrderId
ProductionDocumentType = MATERIAL_ISSUE
CreatedDate
CreatedBy
```

The unique key remains the final concurrency guard.

If a duplicate-key race occurs, reload the row and return its committed result.

---

# 15. Work Order Locking

Lock the Work Order using SQL Server:

```sql
SELECT *
FROM dbo.PrWorkOrder WITH (UPDLOCK, HOLDLOCK)
WHERE UID = @WorkOrderId
  AND CompanyCode = @CompanyCode
  AND BranchCode = @BranchCode
```

Or lock by WorkOrderNo under the tenant if that is the existing unique key.

Validate:

```text
Status in RELEASED / IN_PROGRESS
SnapshotRevision matches
SnapshotHash matches
SnapshotFormatVersion == Current
IsLegacySnapshot == false
```

Legacy Work Orders must not enter this execution path until explicitly refreshed/upgraded.

---

# 16. Material Row Locking

Load all requested `ProductionWorkOrderMaterial` rows and lock them in:

```text
WorkOrderMaterialId ASC
```

Use:

```text
UPDLOCK, HOLDLOCK
```

For each material verify:

```text
belongs to locked Work Order
WorkOrderOperationId != null
ComponentCode not blank
RequiredQty > 0
RequiredBaseQty >= 0
ConversionFactorToBase > 0
RequiredUom not blank
BaseUom not blank
```

Then enforce issue-method and supply-source policy.

Because every concurrent issue for the same material must lock the same material row first, concurrent posts cannot both validate against the same old outstanding quantity.

---

# 17. Authoritative Execution Totals

Do not trust the stored projection before validation.

For each affected material, calculate effective quantities from `PrMaterialMovement` inside the transaction.

For ISSUE:

```text
EffectiveIssueQty
    = SUM(ISSUE.Qty)
      - SUM(ISSUE_REVERSAL.Qty)
```

For future return:

```text
ReturnedQty = SUM(RETURN.Qty)
```

For future consumption:

```text
ConsumedQty = SUM(CONSUME.Qty)
```

Current:

```text
NetIssued = EffectiveIssueQty - ReturnedQty
```

Validate:

```text
NetIssued + requested IssueQty <= MaxAllowedNetIssue
```

This ledger calculation is the authoritative concurrency-time check.

---

# 18. Lock Selected Stock Rows

Collect all requested:

```text
FromBalLocId
```

Load/lock them through:

```text
IIvStockPostingRepository.LockBalLocByIdForTenantAsync(...)
```

Lock in the same deterministic `IvStockSliceKey` order used by the inventory posting engine.

For each allocation revalidate:

```text
tenant
item code
warehouse
location
lot
status
on-hand qty
lot active
not expired
```

Also capture the authoritative:

```text
UnitPrice
LotId
TransDate
```

The client-supplied allocation ID is only an identity pointer.

All other values come from locked server rows.

---

# 19. Revalidate Aggregate Stock Quantity

If the same `IvBalLoc` is used by multiple Work Order material lines, aggregate:

```text
RequiredBaseQtyByBalLoc
```

Then require:

```text
sum allocation <= locked IvBalLoc.StdQty
```

This is an additional production-side validation.

The inventory posting engine will still perform its own authoritative quantity validation and conditional decrement.

---

# 20. Create Inventory IP Batch

Use:

```text
IRunningNumberService
RunningNumberKeys.IvBatch
```

No new production running-number series is required.

Create:

```text
IvTrxBatch
```

Recommended values:

```text
TrxType = IP
BatchStatus = NEW
TrxDtTime = IssueDate
RefNo = WorkOrderNo
Remarks = request.Remark
CompanyCode / BranchCode / LocationCode = tenant context
```

Save the batch inside the current transaction so it can be locked by the posting core.

---

# 21. Create Inventory IP Detail Rows

One `IvTrxBatchDetail` = one physical stock allocation.

Therefore one Work Order material can create multiple inventory detail rows.

Example:

```text
RM001 Issue 100 KG
    Lot A = 60
    Lot B = 40
```

creates two IP detail rows.

Populate server-side:

```text
TrxType = IP
ProdCode = WorkOrder.ProductCode
ProdDesc = WorkOrder.ProductDescription
ICode = WorkOrderMaterial.ComponentCode
IDesc = WorkOrderMaterial.ComponentDescription

FromBalLocId = locked balance ID
FrWarehouse = locked WHCode
FrLocation = locked LocCode
FrLotNo = locked LotNo
FrStdQty = allocation BaseQty
FrStdUom = WorkOrderMaterial.BaseUom
FromLotId = locked LotId

IStatus = locked IStatus
ExpiryDate = lot expiry
UnitPrice = locked IvBalLoc.UnitPrice ?? 0
LocationCode = tenant LocationCode
```

### Remarks

Use operator remark at the batch level.

For line remarks, include readable production context only, e.g.:

```text
WO WO000123 / OP CUTTING
```

Do not encode business identity into free-text remarks; `PrMaterialMovement` stores the real IDs.

---

# 22. Allocation Quantity in Required UOM

Each allocation detail posts Base UOM.

For the production movement fact, calculate the corresponding required-UOM quantity:

```text
AllocationQty
    = BaseQty / ConversionFactorToBase
```

Round to `IvQty.Scale`.

To guarantee that the sum of movement `Qty` exactly equals the requested line `IssueQty`:

```text
allocate rounded quantities to all but final allocation
assign the rounding remainder to the final allocation
```

The final allocation must still remain positive.

---

# 23. Call Existing Inventory Posting Core

After saving the NEW IP batch/details:

```csharp
var result = await _posting.PostStockOutInTransactionAsync(
    db,
    companyCode,
    branchCode,
    userId,
    batchNo,
    IvTrxTypes.IssueToProduction,
    cancellationToken);
```

If failed:

```text
ROLLBACK entire transaction
```

Do not leave:

```text
PENDING posting link
NEW orphan batch
partial movement facts
projection updates
```

---

# 24. Save Inventory Posting Stage

The posting core stages:

```text
IvTrxHistory
batch POSTED
stock changes
```

but does not call `SaveChanges()`.

Call:

```csharp
await db.SaveChangesAsync(ct);
```

inside the same transaction.

This produces identity IDs for the `IvTrxHistory` rows.

---

# 25. Create `PrMaterialMovement` ISSUE Facts

Reload:

```text
IvTrxHistory for Company + Branch + BatchNo
```

Map each posted history line back to:

```text
IvTrxBatchDetail
→ request allocation
→ WorkOrderMaterialId
→ WorkOrderOperationId
```

Insert one `PrMaterialMovement` per inventory detail.

Movement:

```text
MovementType = ISSUE
Qty = allocation required-UOM qty
Uom = WorkOrderMaterial.RequiredUom

BaseQty = IvTrxHistory.FrStdQty
BaseUom = WorkOrderMaterial.BaseUom

WarehouseCode = history.FrWarehouse
LocationCode = history.FrLocation
LotNo = history.FrLotNo
LotId = history.FromLotId
FromBalLocId = history.FromBalLocId

InventoryBatchId
InventoryBatchNo
InventoryBatchDetailId
InventoryTrxLineNo
InventoryHistoryId
InventoryPostingOperationId

UnitCost = history.UnitPrice ?? 0
TotalCost = Round4(BaseQty × UnitCost)

PostingLinkId
CreatedDate
CreatedBy
```

Do not allow movement update/delete after commit.

---

# 26. Rebuild Material Projections

After inserting movements, rebuild affected `ProductionWorkOrderMaterial` rows.

For each material:

```text
IssuedQty
    = SUM(ISSUE.Qty)
      - SUM(ISSUE_REVERSAL.Qty)

ReturnedQty
    = SUM(RETURN.Qty)

ConsumedQty
    = SUM(CONSUME.Qty)

VarianceQty
    = preserve current definition until consumption/output phase
```

Do not let the UI write these fields.

---

# 27. Update Work Order Status

If:

```text
Status == RELEASED
```

set:

```text
Status = IN_PROGRESS
ModifiedDate
ModifiedBy
```

Add a `ProductionAuditEvent`:

```text
EventType = MATERIAL_ISSUED
FromStatus = RELEASED
ToStatus = IN_PROGRESS
Reason = "Issue to Production batch {BatchNo}"
```

If already `IN_PROGRESS`, still add a material-issued audit event, but no status transition is required.

---

# 28. Complete Posting Link

Set:

```text
InventoryBatchNo = batchNo
PostingOperationId = result.OperationId
ProductionDocumentNo = batchNo.ToString()
Status = SUCCEEDED
CompletedDate = now
ResultCode = "OK"
ResultMessage = ...
```

Then:

```csharp
await db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
```

Only after this is the command successful.

---

# 29. Posting Response

Return:

```text
PostingRequestId
BatchNo
PostingOperationId
WorkOrderNo
WorkOrderStatus
PostedDate
Affected materials:
    WorkOrderMaterialId
    IssuedQty
    ReturnedQty
    NetIssuedQty
    OutstandingQty
```

The UI should refresh the workspace from the server after success.

---

# 30. Inventory Posting Error Message Hardening

Modify:

```text
ErpWeb.Core/Inventory/IvInventoryPostingService.cs
```

Do not add IP to `DispatchAsync`.

Only improve the stock-out message helpers so direct IP calls produce correct terminology.

Add:

```csharp
IsIssueToProductionTrxType(...)
```

Update messages:

```text
"Issue to Production was not found."
"Only NEW Issue to Production documents can be posted."
"Issue to Production has no lines."
"Only POSTED Issue to Production documents can be rolled back."
"Source balance ... no longer matches the Issue to Production line..."
```

This is a safe localized improvement.

---

# 31. Rollback Design

Rollback is intended for immediate correction of an incorrect IP posting.

It is **not** the same as normal Return from Production.

Create:

```csharp
RollbackAsync(
    ProductionMaterialIssueRollbackRequest request,
    CancellationToken ct)
```

Request:

```text
PostingRequestId
InventoryBatchNo
Reason
```

Require:

```text
ACCESS
ROLLBACK
```

---

# 32. Rollback Transaction Algorithm

## Step 1

Start DB transaction.

## Step 2

Create / resolve idempotency row:

```text
CommandType = MATERIAL_ISSUE_ROLLBACK
OriginalPostingLinkID = original issue posting link
```

## Step 3

Lock the original IP batch and related Work Order/material rows.

Require:

```text
original issue posting link Status == SUCCEEDED
inventory batch Status == POSTED
```

## Step 4 — downstream dependency guard

Block rollback when any affected original ISSUE movement has later effective:

```text
CONSUME
RETURN
ADJUSTMENT that depends on the issue
```

Initial milestone has no consume/return implementation, but implement the query now so the safety rule already exists.

## Step 5

Call:

```csharp
RollBackStockOutInTransactionAsync(
    db,
    companyCode,
    branchCode,
    userId,
    batchNo,
    IvTrxTypes.IssueToProduction,
    ct)
```

The current inventory core:

```text
restores IvBalLoc
removes IvTrxHistory
sets batch back to NEW
records rollback audit values
```

## Step 6

After successful stock rollback, change the IP batch from:

```text
NEW
```

to:

```text
CANCELLED
```

This prevents the rolled-back production issue from being silently reposted outside the production workflow.

Keep its detail rows for audit.

## Step 7

Insert one `ISSUE_REVERSAL` movement for every effective original `ISSUE` movement.

Copy:

```text
Qty
Uom
BaseQty
BaseUom
warehouse/location/lot
cost
```

Set:

```text
OriginalMovementId = original ISSUE movement UID
InventoryHistoryId = null
InventoryPostingOperationId = rollback operation id
```

## Step 8

Rebuild material projections.

## Step 9

Set original posting link:

```text
Status = REVERSED
```

Set rollback posting link:

```text
Status = SUCCEEDED
```

## Step 10

Add:

```text
ProductionAuditEventTypes.MaterialIssueRolledBack
```

Do **not** automatically move Work Order `IN_PROGRESS` back to `RELEASED`.

## Step 11

Commit.

---

# 33. Why Rollback Movement Must Remain

The current generic inventory rollback removes `IvTrxHistory`.

Therefore Production must preserve its own permanent evidence:

```text
ISSUE
+
ISSUE_REVERSAL
```

The effective quantity becomes zero, while the audit trail remains visible.

This is one of the main reasons `PrMaterialMovement` is required.

---

# 34. Production Issue List

Create:

```text
ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueList.razor
ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueList.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueList.razor.css
```

Route:

```text
/planning/material-issues
```

Source:

```text
PrProductionPostingLink
+ ProductionWorkOrder
+ PrMaterialMovement
+ IvTrxBatch
```

Columns:

```text
Batch No
Issue Date
Work Order
Product
Status
Line Count
Posted By
Posted Date
Rollback Date / Reversed
```

Filters:

```text
Date From/To
Work Order
Product
Status
Batch No
```

Actions:

```text
New Issue
View
Rollback
```

No edit/delete for posted documents.

---

# 35. Issue Entry Page

Create:

```text
ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor.css
```

Routes:

```text
/planning/material-issues/new
/planning/material-issues/new/{WorkOrderNo}
/planning/material-issues/{BatchNo:int}
```

Modes:

```text
New
View
```

No “Edit Posted” mode.

---

# 36. Issue Entry UI Layout

```text
┌─────────────────────────────────────────────────────────────┐
│ Issue to Production                                        │
├─────────────────────────────────────────────────────────────┤
│ Work Order : WO000123       Status : RELEASED               │
│ Product    : FG001          Planned : 1,000 PCS             │
│ Issue Date : 01-Oct-2026                                   │
│ Remark     : ______________________________                 │
├─────────────────────────────────────────────────────────────┤
│ Route / Work Centre: [WC1 ▼]                               │
│ Operation          : [CUTTING ▼]                           │
├─────────────────────────────────────────────────────────────┤
│ Material Requirement                                       │
│                                                             │
│ Item   Required Issued Returned Outstanding Available Issue │
│ RM001   500.00  300.00   0.00     200.00     450.00 200.00 │
│ RM002   100.00   80.00   0.00      20.00      10.00  10.00 │
│                                                             │
│ [Auto Fill Outstanding] [Show Shortage Only]               │
│ [Allocate Lots]                              [Post Issue]   │
└─────────────────────────────────────────────────────────────┘
```

---

# 37. Material Grid Columns

Primary columns:

```text
Item
Description
Required
Issued
Returned
Net Issued
Outstanding
Available
Issue Now
UOM
Warehouse
Status
```

Secondary/drill-down:

```text
Operation
Tolerance
Max Allowed
Base UOM
Conversion Factor
Issue Method
Supply Source
```

Do not show cost unless user has:

```text
VIEW_COST
```

---

# 38. Operator Defaults

On load:

```text
Issue Now = 0
```

Provide:

```text
[Auto Fill Outstanding]
```

which calculates:

```text
min(OutstandingQty, AvailableQty)
```

per manually issuable material.

Do not auto-post.

---

# 39. Lot Allocation Dialog

Create reusable component:

```text
ErpWeb.UI/Planning/Components/PrMaterialAllocationDialog.razor
ErpWeb.UI/Planning/Components/PrMaterialAllocationDialog.razor.css
```

Columns:

```text
Select
Warehouse
Location
Lot
On Hand
Expiry
Stock Date
Suggested
Issue Base Qty
Base UOM
```

If `VIEW_COST`:

```text
Unit Cost
```

Buttons:

```text
Auto Allocate
Clear
Apply
Cancel
```

Show FEFO/FIFO explanation in a small help text.

---

# 40. Posting Confirmation

Before posting show:

```text
Work Order
Issue Date
Material count
Allocation row count
Shortage warnings
Over-standard-within-tolerance warnings
```

Hard errors cannot be confirmed past.

Generate:

```text
PostingRequestId = Guid.NewGuid().ToString("N")
```

when the user starts the post.

Retain that same request ID until the server response is known.

Do not generate a new ID on an automatic retry after timeout.

---

# 41. Work Order Integration

Modify:

```text
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs
```

Add:

```text
[Issue Materials]
```

Visible when:

```text
Work Order status RELEASED or IN_PROGRESS
user has PLN_MATERIAL_ISSUE / ACCESS
```

Enabled for new issue when user also has:

```text
ADD
```

Navigate to:

```text
/planning/material-issues/new/{WorkOrderNo}
```

Also add a material execution summary area:

```text
Required
Issued
Returned
Net Issued
Consumed
Outstanding
```

Do not perform posting from the Work Order page itself.

---

# 42. Permissions and Menu

Modify:

```text
ErpWeb.Core/Menus/MenuCodes.cs
```

Add:

```csharp
public const string PlanningMaterialIssue = "PLN_MATERIAL_ISSUE";
```

Modify:

```text
ErpWeb/Menus/menus.xml
```

Under:

```text
Planning
  → Transactions
```

add after Work Orders:

```xml
<Menu Code="PLN_MATERIAL_ISSUE"
      Name="Issue to Production"
      Route="/planning/material-issues"
      SortOrder="2"
      Icon="fa-solid fa-boxes-packing" />
```

---

# 43. Menu SQL Seed

Create:

```text
scripts/init-planning-material-issue-menu.sql
```

Use the same style as:

```text
scripts/init-planning-workorder-menu.sql
```

Seed:

```text
ACCESS
ADD
POST
ROLLBACK
VIEW_COST
```

Do not seed:

```text
EDIT
DELETE
CANCEL
```

Role grants remain deployment-owner decisions.

---

# 44. Core DI Registration

Modify:

```text
ErpWeb.Core/CoreServiceCollectionExtensions.cs
```

Add:

```csharp
services.AddScoped<IProductionMaterialIssueService, ProductionMaterialIssueService>();
services.AddScoped<IProductionMaterialAllocationService, ProductionMaterialAllocationService>();
```

No UI-specific business service.

---

# 45. Recommended Internal Pure Helper

Create:

```text
ErpWeb.Core/Production/ProductionMaterialExecutionCalc.cs
```

Keep pure formulas here:

```text
MaxAllowedNetIssue
MovementEffectiveIssue
Outstanding
BaseQtyForIssueQty
IssueQtyForBaseQty
Rounding remainder allocation
```

Example:

```csharp
public static decimal MaxAllowedNetIssue(
    decimal requiredQty,
    decimal tolerancePercent)
```

Use `IvQty.Round`.

Keep these rules testable without EF.

---

# 46. Read/List Service Methods

`IProductionMaterialIssueService` should also expose:

```csharp
SearchAsync(ProductionMaterialIssueListQuery ...)
GetAsync(int batchNo ...)
PostAsync(ProductionMaterialIssuePostRequest ...)
RollbackAsync(ProductionMaterialIssueRollbackRequest ...)
```

### `GetAsync(batchNo)`

Return:

```text
batch header
Work Order
posting link
posted/reversed status
lines
lot allocations
movement quantity
cost only when VIEW_COST
audit info
```

The detail page is read-only.

---

# 47. Cost Rules

For this milestone:

```text
UnitCost = locked IvBalLoc.UnitPrice ?? 0
```

Stamp that value into `IvTrxBatchDetail.UnitPrice` before posting.

After posting, production movement cost must come from the posted history/detail snapshot, not from client input.

```text
TotalCost = Round4(BaseQty × UnitCost)
```

Do not infer a new costing method.

Do not use Product Definition standard cost as actual issue cost.

If `UnitPrice` is zero:

```text
allow posting
record zero
flag costing completeness for future costing workflow
```

Do not silently substitute PurchasePrice or current master price.

---

# 48. Security Rules

Every server write must enforce tenant and permission checks.

Never trust client:

```text
CompanyCode
BranchCode
WorkOrderId
material ownership
ComponentCode
OperationId
Warehouse
Location
Lot
AvailableQty
UnitPrice
Tolerance
RequiredQty
Snapshot status
```

The client only supplies:

```text
WorkOrderNo
snapshot fingerprint
issue date
material IDs
desired quantities
selected BalLoc IDs
remark
idempotency key
```

Everything else is resolved again on the server.

---

# 49. Tenant Isolation

Every query and lock must include:

```text
CompanyCode
BranchCode
```

A Work Order from another tenant must return “not found”, not a permission-leaking diagnostic.

A selected `IvBalLoc` from another tenant must be rejected.

---

# 50. Period Close

Before actual inventory posting, existing inventory posting already calls:

```text
IvPeriodCloseGuard.EnsureOpenAsync(...)
```

Do not duplicate the full close logic.

The production service may precheck for better UX, but the posting engine remains authoritative.

Rollback must also be blocked by the existing period-close guard.

---

# 51. Concurrency Order

To avoid deadlocks, always use this lock order:

```text
1. PrProductionPostingLink / idempotency
2. ProductionWorkOrder
3. ProductionWorkOrderMaterial rows by UID ASC
4. selected IvBalLoc rows in IvStockSliceKey order
5. IvTrxBatch / inventory posting internals
```

Never lock stock rows first and Work Order rows later in another code path.

---

# 52. Concurrent Same-Material Protection

Scenario:

```text
Required = 100
Issued = 0

User A wants 70
User B wants 70
```

Both may load the same screen.

At posting:

```text
A locks WorkOrderMaterial
A validates 70 <= 100
A posts and commits

B then acquires WorkOrderMaterial lock
B recomputes effective issued = 70
B validates new net = 140
B is rejected
```

This must be covered by a SQL Server concurrency test.

---

# 53. Concurrent Same-Stock Protection

Scenario:

```text
IvBalLoc on hand = 100

User A uses 80
User B uses 50
```

Existing inventory row locks and atomic decrement must ensure only a valid combination can commit.

Production must not introduce a separate stock mutation path.

---

# 54. Idempotent Retry Scenario

Client posts:

```text
PostingRequestId = ABC
```

Server commits successfully but connection times out.

Client retries:

```text
PostingRequestId = ABC
```

Expected:

```text
same BatchNo
same successful command result
no second inventory issue
no second movement
```

Mandatory production-grade test.

---

# 55. No Product Definition Dependency During Posting

Add a test proving:

1. Create/release Work Order.
2. Change Product Definition afterwards.
3. Issue material.
4. Posting uses `ProductionWorkOrderMaterial`, not new Product Definition values.

This is a critical business invariant.

---

# 56. Tests

Create:

```text
ErpWeb.Tests/ProductionMaterialExecutionCalcTests.cs
ErpWeb.Tests/ProductionMaterialAllocationTests.cs
ErpWeb.Tests/ProductionMaterialIssueServiceTests.cs
ErpWeb.Tests/ProductionMaterialIssueSchemaTests.cs
ErpWeb.Tests/ProductionMaterialIssueSqlServerConcurrencyTests.cs
```

Also extend:

```text
ErpWeb.Tests/IvInventoryPostingServiceTests.cs
```

with direct in-transaction `IP` stock-out/rollback coverage.

---

# 57. Required Functional Tests

## Requirement formulas

- outstanding after first issue
- repeated partial issue
- return projection formula placeholder
- tolerance zero
- tolerance positive
- rounding at 4 decimals
- conversion factor > 1
- fractional conversion factor

## Allocation

- FIFO non-lot
- FEFO lot with expiry
- lot without expiry falls back FIFO
- expired lot excluded
- inactive lot excluded
- location restriction
- warehouse restriction
- insufficient stock
- split across multiple lots
- manual allocation

## Posting

- one material / one stock slice
- one material / multiple lots
- multiple materials / multiple operations
- partial issue
- repeated issue
- issue exactly outstanding
- issue within tolerance
- reject above tolerance
- block wrong Work Order status
- block BACKFLUSH
- block PICK_LIST
- block INTERNAL_ROUTE_WIP
- block SEPARATE_PRODUCT_DEFINITION
- stale snapshot
- tenant mismatch
- inactive item
- non-stock-controlled item
- period closed
- expired lot
- invalid allocation total
- changed stock after UI load
- zero cost

## Rollback

- normal rollback
- material projection restored
- batch becomes CANCELLED
- original ISSUE fact remains
- ISSUE_REVERSAL fact created
- original posting link becomes REVERSED
- rollback is idempotent
- second rollback rejected/returns existing result
- period-close rollback blocked
- downstream dependency blocks rollback

---

# 58. SQL Server Concurrency Tests

Mandatory:

### Test A — same stock slice

Two concurrent IP posts consume the same `IvBalLoc`.

Expected:

```text
no negative stock
only valid committed quantity
```

### Test B — same Work Order material over-issue

Two concurrent requests each fit independently but exceed requirement together.

Expected:

```text
one succeeds
other fails tolerance/outstanding validation
```

### Test C — idempotent duplicate request

Two simultaneous commands with identical `PostingRequestId`.

Expected:

```text
one inventory batch
one set of movements
same result returned
```

### Test D — post vs rollback

Rollback starts while post is completing.

Expected:

```text
serial result
no partial stock/projection state
```

### Test E — Work Order cancel/change vs issue

An execution-changing command races with material issue.

Expected:

```text
row locking/snapshot validation prevents inconsistent state
```

---

# 59. UI Acceptance Scenarios

### Scenario 1 — simple manual issue

```text
WO RELEASED
Required RM001 = 100 KG
Available = 150 KG
Issue = 100 KG
```

Expected:

```text
IP batch POSTED
stock -100
PrMaterialMovement ISSUE +100
IssuedQty = 100
Outstanding = 0
WO IN_PROGRESS
```

### Scenario 2 — partial issue

```text
Issue 40
```

Expected:

```text
Issued = 40
Outstanding = 60
```

### Scenario 3 — two lots

```text
Lot A = 60
Lot B = 40
```

Expected:

```text
2 IvTrxBatchDetail
2 movement rows
1 inventory batch
Issued = 100
```

### Scenario 4 — shortage

```text
Outstanding = 100
Available = 30
```

Expected:

```text
Auto Fill = 30
Shortage = 70
```

### Scenario 5 — tolerance

```text
Required = 100
Tolerance = 5%
Max = 105
Current net issue = 100
Additional = 5 → allowed
Additional = 6 → rejected
```

### Scenario 6 — retry

Same request ID posted twice.

Expected:

```text
no duplicate stock issue
```

### Scenario 7 — rollback

Expected:

```text
stock restored
batch CANCELLED
effective IssuedQty reduced
movement audit preserved
WO stays IN_PROGRESS
```

---

# 60. File-by-File Implementation Map

## New Model files

```text
ErpWeb.Model/Entities/Production/ProductionMaterialMovement.cs
ErpWeb.Model/Configurations/Production/ProductionMaterialMovementConfiguration.cs
```

## Modify Model files

```text
ErpWeb.Model/Entities/Production/ProductionDomainConstants.cs
ErpWeb.Model/Data/AppDbContext.cs
```

No structural change is required to:

```text
ProductionWorkOrderMaterial
ProductionPostingLink
IvTrxBatch
IvTrxBatchDetail
IvTrxHistory
IvBalLoc
```

---

## New Core files

```text
ErpWeb.Core/Production/IProductionMaterialIssueService.cs
ErpWeb.Core/Production/IProductionMaterialAllocationService.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.cs
ErpWeb.Core/Production/ProductionMaterialAllocationService.cs
ErpWeb.Core/Production/ProductionMaterialExecutionCalc.cs
```

## Modify Core files

```text
ErpWeb.Core/CoreServiceCollectionExtensions.cs
ErpWeb.Core/Menus/MenuCodes.cs
ErpWeb.Core/Inventory/IvInventoryPostingService.cs
```

`IvInventoryPostingService.cs` change is message hardening only; do not duplicate production logic there.

---

## New UI files

```text
ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueList.razor
ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueList.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueList.razor.css

ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrMaterialIssueEntry.razor.css

ErpWeb.UI/Planning/Components/PrMaterialAllocationDialog.razor
ErpWeb.UI/Planning/Components/PrMaterialAllocationDialog.razor.css
```

## Modify UI files

```text
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs
```

---

## Menu/config files

```text
ErpWeb/Menus/menus.xml
scripts/init-planning-material-issue-menu.sql
```

---

## SQL schema files

```text
scripts/create-production-material-issue.sql
scripts/create-production-workorder.sql
```

---

## Test files

```text
ErpWeb.Tests/ProductionMaterialExecutionCalcTests.cs
ErpWeb.Tests/ProductionMaterialAllocationTests.cs
ErpWeb.Tests/ProductionMaterialIssueServiceTests.cs
ErpWeb.Tests/ProductionMaterialIssueSchemaTests.cs
ErpWeb.Tests/ProductionMaterialIssueSqlServerConcurrencyTests.cs
```

Extend:

```text
ErpWeb.Tests/IvInventoryPostingServiceTests.cs
```

---

# 61. Implementation Sequence

## Phase 1 — Domain + schema

Implement:

```text
ProductionMaterialMovement
configuration
constants
DbSet
SQL scripts
schema tests
```

Exit criteria:

```text
solution builds
SQL script reruns safely
all FK/index/schema tests pass
```

---

## Phase 2 — Pure calculations

Implement:

```text
ProductionMaterialExecutionCalc
```

Cover:

```text
net issue
outstanding
tolerance
base conversion
rounding
allocation remainder
```

Exit criteria:

```text
all calculation tests pass
```

---

## Phase 3 — Allocation/read layer

Implement:

```text
IProductionMaterialAllocationService
ProductionMaterialAllocationService
workspace DTOs/read methods
```

Exit criteria:

```text
manual-issuable materials correctly identified
FIFO/FEFO allocation correct
expired/inactive stock excluded
availability shown in RequiredUom
```

No stock updates yet.

---

## Phase 4 — IP posting orchestration

Implement:

```text
ProductionMaterialIssueService.PostAsync
```

including:

```text
permissions
tenant scope
idempotency
WO/material locks
outstanding/tolerance
stock locks
IP batch creation
inventory posting call
movement facts
projections
WO status
audit
commit
```

Exit criteria:

```text
service tests pass
no partial DB state on failure
```

---

## Phase 5 — Posting list + entry UI

Implement:

```text
list
entry
operation filtering
material grid
auto-fill
allocation popup
post confirmation
```

Exit criteria:

```text
normal user can post a valid partial/full issue without understanding inventory internals
```

---

## Phase 6 — Work Order integration

Add:

```text
Issue Materials button
execution quantities
navigation
```

Exit criteria:

```text
released/in-progress Work Order can launch directly into the correct IP workspace
```

---

## Phase 7 — Rollback

Implement:

```text
RollbackAsync
ISSUE_REVERSAL facts
batch CANCELLED
projection rebuild
audit
idempotency
dependency guard
```

Exit criteria:

```text
stock restored
production history retained
no repost loophole
```

---

## Phase 8 — SQL Server concurrency hardening

Run:

```text
same stock race
same material race
duplicate request race
post/rollback race
WO change/issue race
```

Exit criteria:

```text
no negative stock
no over-issued material due to race
no duplicate posting
no deadlock in expected test paths
```

---

# 62. Explicit Non-Goals for This Milestone

Do **not** implement these inside Issue-to-Production:

```text
Daily Production output
Finished Goods posting
production consumption
backflush
pick-list reservation
WIP route transfer
Return From Production business flow
MRP replenishment
production costing engine
GL posting
quality inspection
mobile barcode workflow
```

The schema and movement ledger must be compatible with these future features, but they are separate implementation milestones.

---

# 63. Future Extension Contract

Later features must reuse:

```text
PrMaterialMovement
```

### Backflush

Creates:

```text
ISSUE
CONSUME
```

inside production output transaction.

### Return from Production

Creates:

```text
RETURN
```

and an explicit stock-in inventory transaction.

### Production consumption

Creates:

```text
CONSUME
```

against exact material / operation / lot lineage.

### WIP transfer

Use exact route-step producer/consumer identity, not fake warehouse IP.

### Actual material variance

```text
ConsumedQty - RequiredQty
```

plus movement actual costs.

---

# 64. Production Readiness Checklist

The implementation is **not production-approved** until all are true:

- [ ] Uses `ProductionWorkOrderMaterial`, not live Product Definition.
- [ ] Only RELEASED / IN_PROGRESS Work Orders can issue.
- [ ] Snapshot revision/hash checked.
- [ ] MANUAL only for initial manual issue.
- [ ] INTERNAL_ROUTE_WIP blocked from warehouse issue.
- [ ] Required and Base UOM are not confused.
- [ ] Partial and repeated issue works.
- [ ] Outstanding is recomputed from movement facts during posting.
- [ ] Tolerance enforced server-side.
- [ ] Multiple lots supported.
- [ ] FEFO/FIFO allocation works.
- [ ] Expired lots blocked.
- [ ] Exact `FromBalLocId` is validated and locked.
- [ ] Existing IP inventory stock-out core is reused.
- [ ] No generic/orphan IP posting route exists.
- [ ] `PrMaterialMovement` written for every posted allocation.
- [ ] Projection fields are rebuilt from movements.
- [ ] Idempotent retry works.
- [ ] One transaction covers inventory + production facts.
- [ ] First issue moves RELEASED → IN_PROGRESS.
- [ ] Rollback restores stock.
- [ ] Rollback creates `ISSUE_REVERSAL`.
- [ ] Rolled-back IP batch is CANCELLED and not silently repostable.
- [ ] Tenant scope enforced in every query.
- [ ] ACCESS / ADD / POST / ROLLBACK / VIEW_COST enforced server-side.
- [ ] Period close honored.
- [ ] No client-supplied cost is trusted.
- [ ] SQL Server same-stock race tested.
- [ ] SQL Server same-material over-issue race tested.
- [ ] Duplicate PostingRequestId tested.
- [ ] Product Definition change after release does not change IP requirement.
- [ ] Solution builds cleanly.
- [ ] Existing inventory tests remain green.

---

# 65. Definition of Done

The Issue-to-Production milestone is complete when a real production operator can:

```text
1. Open a RELEASED Work Order.
2. Click Issue Materials.
3. See exact material requirements by operation.
4. See already-issued and outstanding quantities.
5. See current available warehouse stock.
6. Auto-allocate valid lots using FEFO/FIFO.
7. Adjust the issue quantity manually.
8. Post a partial or full issue.
9. Have stock reduced atomically.
10. Have exact lot/cost/operation movement facts recorded.
11. See Work Order material issued/outstanding values immediately updated.
12. Safely repeat another partial issue later.
13. Be prevented from over-issuing beyond tolerance.
14. Be protected against concurrent stock/material races.
15. Retry safely after a network timeout without duplicate posting.
16. View the posted IP document.
17. Roll it back when no downstream production dependency exists.
18. Retain an auditable production ISSUE + ISSUE_REVERSAL history.
```

---

# Final Implementation Principle

The coding agent should **not clone the old `IssueToProductionEx.aspx` code**.

The old ERP is the behavioral reference only.

The new implementation must preserve the proven business concepts:

```text
Work Order snapshot
operation-owned BOM
partial issue
lot traceability
warehouse → production/WIP
real stock posting
cost capture
rollback
```

while using the new ERP's stronger architecture:

```text
ProductionWorkOrderMaterial
exact WorkOrderOperationId
explicit UOM chain
tolerance
supply source
inventory row locks
idempotency
immutable production movement facts
Blazor service-layer separation
```

This is the recommended production-grade implementation path for `mokth/net10projectTemplate` → `production`.
