---
name: IP Entry Save Flow — Production Ready
overview: Production-ready redesign of Issue to Production for the current production branch. Entry saves a NEW inventory draft only; posting happens from the list; every draft/post is validated against the frozen Work Order process BOM, tolerance, warehouse, lots, historical as-of stock, current on-hand, and snapshot identity.
supersedes: ip_entry_save_flow_0f4b1028.plan.md
review_status: PRODUCTION_READY_PLAN
isProject: false
todos:
  - id: schema-ip-draft
    content: Add allocation-grain PrMaterialIssueLine, DRAFT/CANCELLED posting-link lifecycle, snapshot fingerprint columns, indexes, preflight/backfill/rollback SQL
    status: pending
  - id: inventory-asof
    content: Add reusable inventory as-of stock service using IvTrxHistory to reverse movements after transaction DateTime
    status: pending
  - id: ip-draft-api
    content: Add operation search, BOM preview/calculate, peek, create, update, delete, cancel and draft-aware get/search contracts
    status: pending
  - id: ip-validator
    content: Centralize process BOM, cumulative tolerance, draft allowance, warehouse, lot, as-of stock and full DateTime validation
    status: pending
  - id: ip-post-refactor
    content: Convert current single-shot Post into per-existing-NEW-batch posting with deterministic locks, cost refresh, movement creation and projection rebuild
    status: pending
  - id: ip-ui
    content: Rebuild material issue entry/list/allocation/BOM popup around save-only entry and list-driven post
    status: pending
  - id: ip-verification
    content: Run schema preflight plus manual/integration verification matrix for draft, partial issue, tolerance, warehouse, lot, concurrency, historical stock and posting
    status: pending
---

# Issue to Production — Production-Ready Entry Save / List Post Plan

## 1. Review result

The original plan has the correct business direction but must not be implemented unchanged.

This revised plan keeps the original intent and resolves the repo-level blockers found on the current `production` branch:

1. `PrMaterialIssueLine` is defined at the correct **inventory allocation/detail grain**.
2. Create/Update/Post/Cancel/Delete serialize through locked `ProductionWorkOrderMaterial` rows so concurrent drafts cannot bypass tolerance.
3. “No future stock” uses real historical stock reconstruction from `IvTrxHistory`; `IvBalLoc.TransDate` is never treated as historical availability.
4. The BOM Calculate formula is made unambiguous.
5. NEW draft lot selections are tentative; they do not create a new physical stock reservation subsystem.
6. Posting refreshes cost from locked inventory balances so stale draft cost is not posting authority.
7. `GetAsync` and list line counts work for NEW drafts before any `PrMaterialMovement` exists.
8. One IP document is restricted to one Work Order + one operation/process.
9. Full transaction DateTime is preserved and validated; same-day future stock cannot be used.
10. Multi-selection Post runs one transaction per batch, matching current inventory behaviour.
11. Current immutable movement/rollback design remains intact.
12. Existing posted IP documents remain readable through backfill/fallback rules.

This document replaces the original implementation plan.

---

# 2. Locked business decisions

## 2.1 Lifecycle

```text
NEW ENTRY
   ↓ Save
IvTrxBatch = NEW
PrProductionPostingLink = DRAFT
PrMaterialIssueLine = draft allocation mapping
NO stock movement
NO PrMaterialMovement
NO WO status change

LIST → POST
   ↓
revalidate + lock
   ↓
stock-out
   ↓
PrMaterialMovement ISSUE
   ↓
IvTrxBatch = POSTED
PrProductionPostingLink = SUCCEEDED
WO RELEASED → IN_PROGRESS
```

Entry never posts. Posting is list/service driven.

## 2.2 Requirement authority

Always use the frozen Work Order snapshot:

```text
ProductionWorkOrder
  → ProductionWorkOrderOperation
    → ProductionWorkOrderMaterial
```

Never re-read `PrDefBOM` during material issue.

`WorkOrderMaterialId` is the authoritative Process BOM requirement identity. Item code alone is never sufficient.

## 2.3 Search and document grain

Search result grain:

```text
Work Order × WorkOrderOperation
```

One IP document may contain multiple BOM items, but every item must belong to the same `WorkOrderOperationId`.

## 2.4 Partial issue

Partial issue is first-class.

```text
Required Raw A = 100
IP001 = 30
IP002 = 40
IP003 = 30
Total = 100
```

Tolerance extends the maximum permitted **net** issue only.

## 2.5 Warehouse/location

Every allocation must use:

```text
ProductionWorkOrderMaterial.WarehouseCode
```

If `LocationCode` is populated on the frozen material row, location is locked too.

No other warehouse may be selected merely because the item has stock there.

## 2.6 Lots

One BOM material line may use multiple lots/balance rows.

```text
Raw A issue = 100
Lot A = 30
Lot B = 50
Lot C = 20
```

Each allocation becomes one `IvTrxBatchDetail`.

## 2.7 Draft stock semantics

A NEW IP draft **does not reserve physical stock**.

It consumes only BOM/tolerance allowance for draft validation.

Therefore two drafts may select the same lot. Post is the physical authority and always rechecks current locked stock.

Do not add a separate inventory reservation engine in this enhancement.

## 2.8 Full DateTime

Use the full `IvTrxBatch.TrxDtTime`.

Do not normalize IP transaction time to `.Date`.

Validation:

```text
TrxDtTime <= _clock.Now
```

At 10:00, an IP transaction at 09:00 may be valid; one at 15:00 is future and must fail.

---

# 3. Data model

## 3.1 New `PrMaterialIssueLine`

Add:

```text
ErpWeb.Model/Entities/Production/ProductionMaterialIssueLine.cs
ErpWeb.Model/Configurations/Production/ProductionMaterialIssueLineConfiguration.cs
```

### Grain

**One row = one `IvTrxBatchDetail` allocation row.**

It is not one logical BOM material row.

Recommended fields:

| Field | Type | Purpose |
|---|---:|---|
| UID | bigint | PK identity |
| CompanyCode | varchar(5) | tenant |
| BranchCode | varchar(5) | tenant |
| PostingLinkID | bigint | FK to `PrProductionPostingLink` |
| InventoryBatchID | int | FK to `IvTrxBatch` |
| InventoryBatchDetailID | int | FK to `IvTrxBatchDetail` |
| InventoryBatchNo | int | inquiry/support |
| InventoryTrxLineNo | smallint | inquiry/support |
| WorkOrderID | bigint | FK to `PrWorkOrder` |
| WorkOrderOperationID | bigint | FK to `PrWorkOrderOperation` |
| WorkOrderMaterialID | bigint | FK to `PrWorkOrderMaterial` |
| IssueQty | decimal(18,4) | this allocation's required-UOM portion |
| BaseQty | decimal(18,4) | this allocation's base qty |
| CreatedDate | datetime2 | audit |
| CreatedBy | varchar(10) | audit |

Constraints:

```text
IssueQty > 0
BaseQty > 0
```

Indexes:

```text
UQ_PrMaterialIssueLine_InventoryDetail
    UNIQUE (InventoryBatchDetailID)

UQ_PrMaterialIssueLine_PostingLine
    UNIQUE (PostingLinkID, InventoryTrxLineNo)

IX_PrMaterialIssueLine_Batch
    (CompanyCode, BranchCode, InventoryBatchNo)

IX_PrMaterialIssueLine_Material
    (CompanyCode, BranchCode, WorkOrderMaterialID)

IX_PrMaterialIssueLine_Operation
    (WorkOrderOperationID, WorkOrderMaterialID)
```

All FKs use `Restrict`.

### Why allocation-grain is required

Current IP posting already produces one detail per allocation/lot:

```text
WorkOrderMaterial 101
  Detail 1 → Lot A → IssueQty 30
  Detail 2 → Lot B → IssueQty 50
  Detail 3 → Lot C → IssueQty 20
```

Use existing:

```csharp
ProductionMaterialExecutionCalc.AllocateIssueQty(...)
```

to split the logical issue quantity across allocation BaseQty rows with final-row rounding correction.

Logical draft quantity is reconstructed by:

```text
SUM(PrMaterialIssueLine.IssueQty)
GROUP BY WorkOrderMaterialID
```

## 3.2 `PrProductionPostingLink` changes

Add nullable:

```text
SnapshotRevision int?
SnapshotHash varchar(64)?
```

All new DRAFT IP rows must populate both. Historical existing links may remain null.

Add statuses:

```csharp
public const string Draft = "DRAFT";
public const string Cancelled = "CANCELLED";
```

Keep existing `PENDING`, `SUCCEEDED`, `FAILED`, `REVERSED`.

Lifecycle:

```text
Create     DRAFT
Update     DRAFT
Post       DRAFT → PENDING → SUCCEEDED in one transaction
Cancel     DRAFT → CANCELLED
Rollback   original SUCCEEDED → REVERSED
```

If posting fails and the transaction rolls back, the link remains DRAFT.

## 3.3 Stable PostingRequestId

Generate a GUID in `N` format on Create and keep it through Create/Update/Post.

Do not generate a new posting link during Post.

Rollback keeps its existing separate `MATERIAL_ISSUE_ROLLBACK` link.

## 3.4 One IP link per inventory batch

Add a SQL Server filtered unique index on:

```text
CompanyCode, BranchCode, CommandType, InventoryBatchNo
```

where:

```text
InventoryBatchNo IS NOT NULL
AND CommandType = 'MATERIAL_ISSUE_POST'
```

This guarantees the current rollback `SingleOrDefault` assumption.

## 3.5 AppDbContext

Add:

```csharp
DbSet<ProductionMaterialIssueLine> ProductionMaterialIssueLines
```

and register the configuration.

---

# 4. Existing data compatibility

## 4.1 Backfill map rows

Existing `PrMaterialMovement` ISSUE rows already contain the allocation/detail relationship needed to backfill:

- InventoryBatchID
- InventoryBatchDetailID
- InventoryBatchNo
- InventoryTrxLineNo
- WorkOrderID
- WorkOrderOperationID
- WorkOrderMaterialID
- Qty
- BaseQty
- PostingLinkID

Backfill one `PrMaterialIssueLine` per ISSUE movement.

Do not use ISSUE_REVERSAL rows for backfill.

## 4.2 Historical snapshot fingerprint

Do not invent historical snapshot hashes.

Existing SUCCEEDED/REVERSED links may keep the new fingerprint columns null.

Only new DRAFT documents require them.

## 4.3 Read fallback

Until all existing records are backfilled:

```text
map rows exist
  → read document/count from PrMaterialIssueLine

no map, but posted movement exists
  → legacy fallback to PrMaterialMovement ISSUE
```

Old posted IP documents must remain readable after deployment.

---

# 5. Service contract

Refactor `IProductionMaterialIssueService` to expose:

```csharp
PeekNextBatchNoAsync(...)
SearchEligibleOperationsAsync(...)
GetBomPreviewAsync(...)
CreateAsync(ProductionMaterialIssueSaveRequest request, ...)
UpdateAsync(int batchNo, ProductionMaterialIssueSaveRequest request, ...)
DeleteAsync(IReadOnlyList<int> batchNos, ...)
CancelAsync(IReadOnlyList<int> batchNos, ...)
PostAsync(IReadOnlyList<int> batchNos, ...)
SearchAsync(...)
GetAsync(int batchNo, ...)
RollbackAsync(...)
```

Once migrated, remove UI dependency on the current single-shot:

```csharp
PostAsync(ProductionMaterialIssuePostRequest)
```

Do not maintain two competing posting paths.

## 5.1 Save request

```text
ProductionMaterialIssueSaveRequest
----------------------------------
WorkOrderNo
WorkOrderOperationId
SnapshotRevision
SnapshotHash
TrxDateTime
RefNo
Remark
Lines[]
```

Logical line:

```text
WorkOrderMaterialId
IssueQty
Allocations[]
```

Allocation:

```text
FromBalLocId
BaseQty
```

The server must re-resolve item, warehouse, location, lot, cost, RequiredQty, tolerance and UOM conversion. Never trust those values from the UI.

---

# 6. SearchEligibleOperationsAsync

Filters are ANDed:

- WO No
- Product
- Work Centre
- Process/Operation
- Output Item
- Raw Material
- Selected Machine

Eligible WO status:

```text
RELEASED
IN_PROGRESS
```

Also require full-hierarchy and non-legacy snapshot.

Return distinct `WorkOrderOperationId`, use server paging and `AsNoTracking`.

Do not load full Work Order graphs per result row.

### Process completion limitation

There is currently no authoritative operation-completed status for this flow.

For now:

- exclude COMPLETED/CLOSED/CANCELLED Work Orders,
- optionally hide operations with no manual material allowance remaining,
- do not fabricate an operation-completed flag from issue quantities.

---

# 7. BOM Preview / Calculate

Rename generic `Output Qty` to:

> **Production Qty for This Issue**

Help text:

> Used only to calculate suggested material quantity for this issue. It does not record production output.

Use the consuming operation planned output:

```text
ratio = ProductionQtyThisIssue / Operation.PlannedOutputQty

RequestedMaterialQty =
    Round(Material.RequiredQty × ratio)
```

Do **not** subtract PostedNetIssued from `RequestedMaterialQty`; this input is incremental, not cumulative.

Example:

```text
Operation planned output = 100
Raw A required           = 100
Already posted net       = 60
Production Qty This Issue= 20
RequestedMaterialQty     = 20
```

## 7.1 Cumulative quantities

```text
EffectiveIssue =
    SUM(ISSUE) - SUM(ISSUE_REVERSAL)

PostedNetIssued =
    EffectiveIssue - SUM(RETURN)

MaxAllowed =
    RequiredQty × (1 + TolerancePercent / 100)

OtherOpenDraftQty =
    SUM(PrMaterialIssueLine.IssueQty)
    for other batches where:
       IvTrxBatch.BatchStatus = NEW
       link.Status = DRAFT
       same WorkOrderMaterialID

RemainingAllowed =
    max(MaxAllowed - PostedNetIssued - OtherOpenDraftQty, 0)
```

Use existing:

```csharp
ProductionMaterialExecutionCalc.MaxAllowedNetIssue(...)
```

Suggestion:

```text
SuggestedIssueQty =
    max(0,
        min(RequestedMaterialQty,
            RemainingAllowed,
            AvailableUsableQty))
```

Display both:

```text
StandardRemaining = max(RequiredQty - PostedNetIssued, 0)
AvailableToDraft  = RemainingAllowed
```

---

# 8. Reusable inventory as-of stock service

Add:

```text
ErpWeb.Core/Inventory/IInventoryAsOfStockService.cs
ErpWeb.Core/Inventory/InventoryAsOfStockService.cs
```

Purpose: return stock that existed at transaction DateTime T and still physically exists now.

## 8.1 `IvBalLoc.TransDate` is not historical authority

`IvBalLoc` is the current operational balance. `TransDate` may remain an advisory order/display field, but it must not decide whether stock existed at T.

## 8.2 Reconstruction

For balance/slice S and DateTime T:

```text
PostTNetMovement =
      inbound to S after T
    - outbound from S after T

AsOfQty(T) =
    CurrentQty - PostTNetMovement

Equivalent:
    CurrentQty - PostTInbound + PostTOutbound
```

Use full:

```text
IvTrxHistory.TrxDtTime > T
```

not `.Date`.

A history row contributes by its `ToBalLocId` inbound leg and/or `FromBalLocId` outbound leg.

Usable quantity:

```text
CurrentQty = max(IvBalLoc.StdQty, 0)
AsOfQty    = max(reconstructed qty, 0)
UsableQty  = min(CurrentQty, AsOfQty)
```

Example:

```text
10:00 stock = 20
15:00 receipt = 80
current = 100
IP time = 10:30
AsOf = 20
Usable = 20
```

Candidate DTO should return:

```text
FromBalLocId
Warehouse
Location
LotId
LotNo
ExpiryDate
CurrentBaseQty
AsOfBaseQty
UsableBaseQty
BaseUom
UnitPrice   // ViewCost only
```

Allocation uses `UsableBaseQty`.

Keep FEFO and current advisory FIFO ordering. Do not confuse stock-date ordering with historical eligibility.

The current history model already indexes FromBalLocId and ToBalLocId; implement and profile before adding more indexes.

---

# 9. Central execution validator

Create/refactor into a shared `ProductionMaterialIssueExecutionValidator` used by Create, Update and Post.

## 9.1 Structural validation

Reject:

- missing WO/operation/fingerprint,
- future TrxDateTime,
- no lines,
- duplicate WorkOrderMaterialId request rows,
- IssueQty <= 0,
- no allocations,
- duplicate FromBalLocId inside one material line,
- BaseQty <= 0,
- total detail/allocation rows > `short.MaxValue`.

## 9.2 One document = one operation

Every material must satisfy:

```text
material.WorkOrderId == selected WorkOrder
material.WorkOrderOperationId == selected WorkOrderOperationId
```

Reject a mixed-process request even if all rows belong to the same WO.

## 9.3 Existing manual-issue policy

Keep current rules:

- RequiredQty > 0
- valid required/base UOM and conversion
- IssueMethod = MANUAL
- SupplySource = PURCHASED or EXTERNAL_SUPPLY
- WarehouseCode required

## 9.4 Warehouse/location/item/lot

For every selected balance:

```text
bal.ICode == material.ComponentCode
bal.WhCode == material.WarehouseCode
```

If frozen `LocationCode` is set:

```text
bal.LocCode == material.LocationCode
```

Also require active stock-controlled item and ACTIVE balance status.

For lot-controlled items:

- LotId required,
- lot exists and active,
- LotNo present,
- expiry is not before transaction date.

## 9.5 Exact allocation sum

```text
ExpectedBaseQty =
    BaseQtyForIssueQty(IssueQty, ConversionFactorToBase)

abs(SUM(BaseQty) - ExpectedBaseQty) <= 0.0001
```

## 9.6 Aggregate same-balance usage

Across the entire document:

```text
SUM(all allocations for FromBalLocId)
    <= balance.UsableBaseQty
```

Do not only validate each material independently. This matters when the same balance supports two distinct BOM lines.

---

# 10. Cumulative BOM allowance and concurrency

## 10.1 Formula

```text
RemainingAllowed =
    MaxAllowed
    - PostedNetIssued
    - OtherOpenDraftQty

CurrentIssueQty <= RemainingAllowed
```

## 10.2 Mandatory serialization

A normal read-check is not enough:

```text
Required = 100
User A reads draft=0 → saves 60
User B reads draft=0 → saves 60
```

Both could otherwise pass.

For Create, Update and Post:

1. lock Work Order,
2. sort requested `WorkOrderMaterialId`s ascending,
3. lock each material using the current SQL Server `UPDLOCK, HOLDLOCK` pattern,
4. then query movement facts + other NEW drafts,
5. validate RemainingAllowed.

The material row is the serialization point for BOM issue allowance.

Cancel/Delete must also lock affected material rows before releasing draft allowance.

### Global lock order

```text
1. existing batch/link when editing existing document
2. Work Order
3. WorkOrderMaterial rows ascending
4. inventory balances sorted by IvStockSliceKey
5. lot revalidation
```

All IP write paths must follow the same order.

---

# 11. Create flow

Permission:

```text
ACCESS + ADD
```

Transaction:

```text
BEGIN
1. validate write scope
2. validate full TrxDateTime <= now
3. period-open guard
4. lock WO
5. require RELEASED/IN_PROGRESS
6. require full-hierarchy/non-legacy snapshot
7. verify submitted SnapshotRevision + SnapshotHash
8. verify operation belongs to WO
9. lock requested material rows ascending
10. validate one-operation membership + material policy
11. calculate posted net + other NEW drafts
12. validate tolerance
13. load selected stock balances
14. reconstruct as-of/current usable stock
15. validate warehouse/location/lot/status
16. validate exact allocation + aggregate same-balance usage
17. allocate real IvBatch number
18. create IvTrxBatch NEW with full TrxDtTime
19. normalize RefNo using existing inventory AUTO semantics
20. create one IvTrxBatchDetail per allocation
21. create DRAFT posting link with stable PostingRequestId + fingerprint
22. SaveChanges to obtain IDs
23. split logical IssueQty via AllocateIssueQty(...)
24. create one PrMaterialIssueLine per detail/allocation
25. SaveChanges
COMMIT
```

Save does not stock out, create movements, or change WO status.

### Peek Batch No

Peek is informational only. Actual BatchNo always comes from `GetNextAsync` inside Create.

---

# 12. Update flow

Permission:

```text
ACCESS + EDIT
```

```text
BEGIN
1. lock IP batch
2. lock posting link
3. require batch NEW + link DRAFT
4. lock WO
5. require stored fingerprint still matches current WO
6. reject stale snapshot; never silently refresh it
7. lock old + new material rows ascending
8. validate new request excluding current batch from OtherOpenDraftQty
9. period-open guard
10. validate as-of/current stock
11. remove old PrMaterialIssueLine rows
12. remove old IvTrxBatchDetail rows
13. update header
14. recreate details
15. recreate allocation map
16. preserve PostingRequestId
COMMIT
```

If the WO snapshot changed, the user must recreate the draft against the new snapshot.

---

# 13. Delete / Cancel

## Delete

Permission: `ACCESS + DELETE`.

Only NEW/DRAFT.

Per batch:

```text
lock batch/link
lock affected material rows
recheck NEW/DRAFT
delete PrMaterialIssueLine
delete IvTrxBatchDetail
delete IvTrxBatch
delete PrProductionPostingLink
commit
```

## Cancel

Permission: `ACCESS + CANCEL`.

Only NEW/DRAFT.

```text
lock batch/link
lock affected material rows
batch.BatchStatus = CANCELLED
link.Status = CANCELLED
save audit/modified fields
commit
```

Keep map/details for inquiry. CANCELLED does not contribute to OtherOpenDraftQty and is view-only.

---

# 14. Post flow

Permission:

```text
ACCESS + POST
```

Do not require ADD.

Limit selection using `IvPostingLimits.MaxPostSelection`.

## 14.1 One transaction per batch

Selected batches are independent:

```text
101 success
102 fail
103 success
```

101 and 103 commit; 102 remains NEW/DRAFT.

Return per-batch status plus SucceededCount/FailedCount.

## 14.2 Single batch algorithm

```text
BEGIN
1. lock IvTrxBatch
2. require TrxType=IP and BatchStatus=NEW
3. lock MATERIAL_ISSUE_POST link
4. require link=DRAFT
5. lock WO
6. require RELEASED/IN_PROGRESS
7. require current fingerprint == stored draft fingerprint
8. load PrMaterialIssueLine + IvTrxBatchDetail
9. require at least one line
10. verify exactly one WorkOrderOperation
11. collect/sort material IDs
12. lock material rows ascending
13. revalidate Process BOM + manual issue policy
14. recompute posted net issue
15. recompute other NEW drafts excluding current batch
16. revalidate tolerance
17. period-open guard again
18. collect FromBalLocIds
19. sort balances by IvStockSliceKey
20. lock balances
21. recompute as-of stock for batch.TrxDtTime
22. revalidate current + historical usable stock
23. revalidate item/WH/location/status/lot/expiry
24. aggregate BaseQty per balance and validate once
25. refresh IvTrxBatchDetail.UnitPrice from locked balance
26. link → PENDING (transactional intermediate)
27. PostStockOutInTransactionAsync(..., IssueToProduction)
28. require success
29. persist inventory posting changes as required
30. load IvTrxHistory for batch
31. require one history row per inventory detail
32. create PrMaterialMovement ISSUE from map + history
33. persist ISSUE facts
34. rebuild affected material projections from immutable facts
35. WO RELEASED → IN_PROGRESS
36. add MaterialIssued audit event
37. link PostingOperationId / ResultCode / CompletedDate
38. link → SUCCEEDED
COMMIT
```

Any failure rolls back. Batch remains NEW and link remains DRAFT.

## 14.3 Cost authority

Draft `IvTrxBatchDetail.UnitPrice` is not authoritative.

Immediately before posting, after balance locks:

```text
detail.UnitPrice = lockedBalance.UnitPrice ?? 0
```

Then inventory posting writes history.

`PrMaterialMovement.UnitCost` comes from posted `IvTrxHistory.UnitPrice`.

Do not accept UI-entered cost.

---

# 15. Projection rebuild

After ISSUE movements are persisted, rebuild affected material projections from immutable movement facts:

```text
EffectiveIssue = SUM(ISSUE) - SUM(ISSUE_REVERSAL)
Returned       = SUM(RETURN)
Consumed       = SUM(CONSUME)

material.IssuedQty   = EffectiveIssue
material.ReturnedQty = Returned
material.ConsumedQty = Consumed
```

Do not derive posted projections from draft map rows.

---

# 16. Rollback compatibility

Keep current rollback design.

Original link must be `MATERIAL_ISSUE_POST + SUCCEEDED`.

Rollback continues to:

- block when later dependent movements exist,
- restore inventory,
- insert immutable ISSUE_REVERSAL facts,
- rebuild material projections,
- mark original link REVERSED,
- mark rollback link SUCCEEDED.

The new draft map remains attached to the original document and is not deleted by rollback.

---

# 17. SearchAsync / list read model

Continue joining posting link + WO + inventory batch.

UI status is primarily inventory batch status:

```text
NEW
POSTED
CANCELLED
```

Posting-link status remains internal lifecycle detail.

### LineCount

Primary:

```text
COUNT DISTINCT WorkOrderMaterialID
from PrMaterialIssueLine
```

Legacy fallback:

```text
COUNT DISTINCT WorkOrderMaterialID
from PrMaterialMovement ISSUE
```

A NEW draft must not display 0 simply because no movement exists.

---

# 18. GetAsync read model

## NEW/CANCELLED

Read:

```text
IvTrxBatch
PrProductionPostingLink
PrMaterialIssueLine
IvTrxBatchDetail
ProductionWorkOrderMaterial
```

Group map rows by `WorkOrderMaterialId` into logical material lines with allocation children.

## POSTED

Document structure still comes from map/details.

Posted execution and cost authority comes from `PrMaterialMovement` / `IvTrxHistory`.

Recommended DTO shape:

```text
ProductionMaterialIssueDocument
  BatchNo
  TrxDateTime
  BatchStatus
  RefNo
  Remark
  WorkOrderId/No
  ProductCode
  WorkOrderOperationId
  WorkCentreCode
  OperationCode
  SnapshotRevision/Hash
  CanViewCost
  MaterialLines[]

MaterialLine
  WorkOrderMaterialId
  ItemCode/Description
  Warehouse/Location
  RequiredQty/UOM
  PostedNetIssued
  OtherOpenDraftQty
  TolerancePercent
  MaxAllowed
  CurrentIssueQty
  Allocations[]

Allocation
  InventoryBatchDetailId
  InventoryTrxLineNo
  FromBalLocId
  LotId/LotNo
  BaseQty/BaseUom
  UnitCost? // ViewCost only
```

---

# 19. Entry UI

Files:

```text
PrMaterialIssueEntry.razor
PrMaterialIssueEntry.razor.cs
PrMaterialIssueEntry.razor.css
```

Header:

```text
Batch No     [peek/read-only]
Status       [NEW]
Date & Time  [dd-MM-yyyy HH:mm]
Ref No       [AUTO]
Remark       [...]
```

Search does not force a Work Order dropdown first.

Filters:

```text
WO | Product | Work Centre | Process | Output Item | Raw Material | Machine
```

Results:

```text
WO | Product | WC | Process | Output Item | Machine | Planned Qty | [BOM]
```

After Apply, show read-only WO/Product/WC/Process context.

### Material grid

Recommended columns:

```text
Item
Description
WH
Required
Net Issued
Other NEW
Std Remaining
Tolerance %
Max Allowed
Available to Draft
Current Issue
Lots
```

When Current Issue changes, mark allocations invalid if their total no longer matches. Disable Save until allocation is exact.

Buttons:

```text
[Save] [Cancel]
```

No Post button on entry.

---

# 20. BOM popup

New component under `ErpWeb.UI/Planning/Components/`.

Header:

```text
WO
Product
Work Centre
Process
Operation Planned Output
Production Qty for This Issue
[Calculate]
```

Grid:

```text
Material
Description
Warehouse
Required
Net Issued
Other NEW Draft
Tolerance
Max Allowed
Available As-Of
Requested for This Issue
Suggested Issue
Blocking Reason
```

Only `CanManualIssue` rows can be applied. Blocked rows remain visible read-only.

---

# 21. Allocation dialog

Enhance `PrMaterialAllocationDialog.razor`.

Current caching by `IssueDate.Date` must be replaced with a full DateTime cache/reload key.

Recommended columns:

```text
Warehouse
Location
Lot
Current On Hand
Available As-Of
Usable
Expiry
Stock Date
Suggested
Issue Base Qty
Base UOM
Unit Cost // ViewCost only
```

Apply requires:

```text
0 <= row issue <= row UsableBaseQty
sum selected BaseQty == RequestedBaseQty ± 0.0001
```

Footer shows Required / Allocated / Difference.

---

# 22. List UI

Buttons:

```text
NEW
POST
CANCEL
DELETE
```

Keep Rollback UI out of scope for now.

Row actions:

```text
VIEW
EDIT
```

Rules:

```text
EDIT   NEW only
POST   NEW only
CANCEL NEW only
DELETE NEW only
VIEW   all
```

Show partial batch results when multi-post contains failures.

Routes:

```text
/planning/material-issues/new
/planning/material-issues/edit/{BatchNo}
/planning/material-issues/view/{BatchNo}
```

Keep `/planning/material-issues/{BatchNo}` as view redirect.

---

# 23. Permissions

| Operation | Required |
|---|---|
| View/Search | ACCESS |
| Create | ACCESS + ADD |
| Update | ACCESS + EDIT |
| Delete | ACCESS + DELETE |
| Cancel | ACCESS + CANCEL |
| Post | ACCESS + POST |
| Rollback | ACCESS + ROLLBACK |
| Cost | VIEW_COST |

Post must no longer require ADD.

---

# 24. Security / trust boundaries

Server never trusts UI for:

- BOM membership,
- operation membership,
- RequiredQty,
- tolerance,
- conversion,
- warehouse/location,
- lot identity,
- item status,
- cost,
- available stock.

Client submits identities + intended quantities only.

All write lookups are tenant scoped.

---

# 25. Performance rules

1. Server-page operation search.
2. BOM preview loads selected-operation materials in one query.
3. Load movement facts for all material IDs in one query.
4. Load other NEW draft sums in one grouped query.
5. Load selected balances in one query.
6. Historical stock calculations operate on candidate/selected balance IDs only.
7. Deterministic material/balance lock ordering.
8. `AsNoTracking` for read-only searches/previews.
9. Keep the new map indexes defined above.
10. Do not add history indexes until query plans show a need.

---

# 26. Implementation file map

Core production changes:

```text
IProductionMaterialIssueService.cs
ProductionMaterialIssueService.cs
ProductionMaterialIssueService.Posting.cs
ProductionMaterialIssueService.Read.cs
ProductionMaterialIssueService.Rollback.cs
ProductionMaterialIssuePostValidator.cs
ProductionMaterialAllocationService.cs
```

Recommended new partials:

```text
ProductionMaterialIssueService.Draft.cs
ProductionMaterialIssueService.Search.cs
ProductionMaterialIssueService.Validation.cs
ProductionMaterialIssueService.Document.cs
```

Core inventory:

```text
IInventoryAsOfStockService.cs
InventoryAsOfStockService.cs
```

Model:

```text
ProductionMaterialIssueLine.cs
ProductionMaterialIssueLineConfiguration.cs
ProductionPostingLink.cs
ProductionPostingLinkConfiguration.cs
ProductionDomainConstants.cs
AppDbContext.cs
```

UI:

```text
PrMaterialIssueEntry.razor/.cs/.css
PrMaterialIssueList.razor/.cs
PrMaterialAllocationDialog.razor
new PrMaterialIssueBomDialog.razor
```

---

# 27. SQL scripts

Update baseline schema for new installations and add deployment scripts:

```text
scripts/preflight-production-material-issue-draft.sql
scripts/alter-production-material-issue-draft.sql
scripts/backfill-production-material-issue-line.sql
scripts/rollback-production-material-issue-draft.sql
```

Preflight must verify:

- no unexpected existing object conflict,
- no duplicate IP posting link for same InventoryBatchNo,
- existing ISSUE movement detail IDs exist,
- ISSUE movements resolve to posting link/material/operation,
- existing inventory batch/detail keys are consistent.

Deployment order:

```text
1. preflight
2. add posting-link columns
3. create PrMaterialIssueLine
4. add constraints/indexes/FKs
5. backfill map rows
6. deploy application code
7. smoke verify existing + new IP reads
```

Rollback must never delete existing inventory history or `PrMaterialMovement` facts.

---

# 28. Verification matrix

The implementation is not complete until these cases pass against SQL Server.

## Draft save

- Save creates `IvTrxBatch=NEW`.
- Creates DRAFT link.
- One inventory detail + one map row per allocation.
- No stock change.
- No `PrMaterialMovement`.
- No WO status change.

## Process BOM identity

- Reject another WO material.
- Reject another operation material in same WO.
- Same ItemCode on two processes remains independent.
- Reject non-BOM/free item.
- Reject mixed-process document.

## Partial issue

```text
Required 100
30 + 40 + 30 = 100
```

All documents save/post successfully.

## Tolerance

```text
Required = 100
Tolerance = 5%
Max = 105
Posted net = 80
Other NEW = 10
Available to draft = 15
```

15 passes; over 15 fails after normal quantity rounding rules.

## Concurrent draft protection

Two simultaneous saves against the same material cannot both use the same pre-save RemainingAllowed.

## Warehouse/location

Stock for the same item in another warehouse/location is not offered and a forged balance ID is rejected.

## Multi-lot

```text
30 + 50 + 20 = 100
```

passes; non-exact allocation fails.

## Same balance used by two BOM lines

Document-wide sum against one `FromBalLocId` must not exceed usable stock.

## Historical no-future-stock

```text
10:00 stock = 20
15:00 receipt +80
current = 100
IP time = 10:30
```

Usable = 20. Request 21 fails.

## Historical > current

```text
As-of = 100
Current = 40
```

Usable = 40. Request 50 fails.

## Draft is not stock reservation

Two drafts may select the same lot. After one posts, the other must fail cleanly if current stock is no longer enough.

## Full DateTime

At 10:00, 09:00 is eligible when period is open; 15:00 is rejected as future.

## Stale snapshot

Draft saved against old revision/hash must fail Update/Post after controlled WO snapshot change.

## Cost refresh

Draft saved with old UnitPrice must post using the locked posting-time inventory cost authority.

## NEW GetAsync

Save/reopen NEW shows logical BOM lines and lot allocations without any `PrMaterialMovement`.

## Line count

NEW, POSTED and CANCELLED show correct distinct BOM-material count.

## Cancel/Delete

- NEW may cancel/delete.
- POSTED may not.
- CANCELLED no longer contributes to open draft allowance.

## Multi-post

```text
101 success
102 insufficient stock
103 success
```

101/103 commit; 102 remains NEW/DRAFT; UI reports 2 success / 1 failure.

## Rollback regression

Posted IP rollback still restores stock, writes ISSUE_REVERSAL, rebuilds projections and marks original link REVERSED.

---

# 29. Out of scope

- Backflush / Pick List / Internal WIP issue.
- Separate Product Definition supply.
- New physical inventory reservation engine.
- Operation-completed status design.
- Editing WO BOM warehouse from IP.
- Live Product Definition BOM explosion.
- Enabling Rollback button on the list UI.
- Global inventory costing-method redesign.
- Strict receipt-layer FIFO redesign.

---

# 30. Definition of done

The enhancement is production-ready when:

1. Entry Save produces only a NEW IP draft.
2. Posting is only from an existing NEW batch.
3. Every issue maps to exact frozen `WorkOrderMaterialId`.
4. One document cannot mix operations.
5. Posted + other NEW + current cannot exceed tolerance.
6. Concurrent saves cannot bypass the allowance rule.
7. Warehouse/location is enforced from the WO snapshot.
8. Multi-lot allocation exactly matches BaseQty.
9. Future receipts are excluded by ledger-derived as-of quantity.
10. Current stock is revalidated under locks at Post.
11. Draft cost is not posting authority.
12. NEW drafts can be reopened/edited correctly.
13. NEW/CANCELLED list counts are correct.
14. Existing posted IP documents remain readable after migration.
15. Multi-batch posting is per-batch atomic and supports partial failure.
16. Existing rollback semantics still work.
17. The verification matrix passes without manual database correction.

At that point the implementation is suitable for the current `production` branch.
