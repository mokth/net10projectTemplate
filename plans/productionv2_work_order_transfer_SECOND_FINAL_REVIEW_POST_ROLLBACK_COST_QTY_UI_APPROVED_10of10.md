# ProductionV2 Work Order Transfer
## SECOND DEEP REVIEW — FINAL CORRECTIVE POST / ROLLBACK / COST / QTY / UI PLAN
### APPROVED FOR IMPLEMENTATION — 10/10, CONDITIONAL ON ALL MANDATORY GATES

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Current reviewed HEAD:** `d98abe79133f4be2c91ac7c7fe2d3e7399f9b236`  
**Current reviewed tree:** `44fdf4861a6d49ad0c8a405ced4ae9e8f3c1f8cd`  
**Previous reviewed HEAD:** `d9a67ced8ea414463b80160a6e5e68e8cfdcc8fe`  
**Original design baseline:** `productionv2_work_order_transfer_FINAL_APPROVED_10of10_AI_AGENT_PLAN_v5_CROSS_TRANSACTION_SAFE(1).md`  
**Supersedes:** `productionv2_work_order_transfer_POST_ROLLBACK_UI_COST_QTY_FINAL_APPROVED_10of10_AI_AGENT_PLAN.md`  
**Status:** **APPROVED FOR IMPLEMENTATION**  
**Priority:** **Production quantity balance, exact cost/value lineage, and safe reversal are absolute release gates.**

---

# 1. Second-review verdict

The current WOT forward posting core is fundamentally sound and must be preserved:

```text
SOURCE PROCESS_WIP
    TRANSFER_OUT
    exact full Qty
    exact full BaseQty
    exact full TotalCost
            |
            v
TARGET PROCESS_WIP
    TRANSFER_IN
    same Qty
    same BaseQty
    same TotalCost
```

Current code already has strong foundations:

- branch stock serialization;
- both Work Order execution locks in ascending order;
- source balance lock and RowVersion snapshot;
- target natural-WIP identity;
- V2 StockPosting;
- `ProductionStockWriter`;
- `ProductionPoolValuationService`;
- grouped `TRANSFER_OUT / TRANSFER_IN`;
- exact source depletion;
- SQL Server race tests;
- no Inventory history/value facts;
- downstream shared-WIP provenance support.

Do **not** rewrite these working parts.

This review found several corrections that are required before implementation.

---

# 2. Corrections to the previous plan

## 2.1 Keep `COMPLETED` source Work Order support

The original V5 plan blocked COMPLETED source WOs.

The current WOT implementation deliberately permits:

```text
RELEASED
IN_PROGRESS
COMPLETED
```

for the **source**.

Keep this current WOT-specific rule.

Reason:

- WOT moves an existing verified residual WIP balance;
- it does not update Work Order GoodQty/RemainingQty;
- a completed Work Order may legitimately retain surplus stockable WIP that must be reassigned;
- blocking it can strand valid WIP and force an artificial adjustment.

Still block source:

```text
DRAFT
CLOSED
CANCELLED
```

Target remains:

```text
RELEASED
IN_PROGRESS
```

only.

This is an intentional, documented deviation from Production Stock Return/Adjustment.

## 2.2 Do NOT make target sequence eligibility a hard WOT posting gate

The original V5 required the target consuming operation to already pass:

```text
IProductionOperationEligibilityService
```

before WOT could post.

Do not add that as a hard WOT blocker.

Reason:

WOT is a WIP ownership/pre-positioning transaction. It does not execute the target operation.

The current Daily Production sequence gate remains authoritative:

```text
WOT may pre-position WIP
        |
        v
Daily Production still checks predecessor Stage/Process completion
        |
        +-- blocked until route sequence is ready
```

Therefore:

- never update predecessor GoodQty;
- never update `TransferredQty`;
- never bypass Daily Production;
- optionally calculate/display the sequence readiness and exact `BlockingReason`;
- allow WOT selection/post even if the consumer is not yet sequence-ready;
- show a clear warning:
  > WIP can be transferred now but cannot be consumed by Daily Production until predecessor operations are complete.

Mandatory regression test proves Daily Production remains blocked.

## 2.3 Remove the previous rollback `RequiredReceiptMovement...` extension

Do **not** extend:

```text
ProductionPoolValuationDirectives.RequiredReceiptMovementByOutboundMovementId
```

for WOT rollback.

That previous proposal was incorrect for the current valuation implementation.

When a reversal movement has:

```text
OriginalMovementId != null
```

`ProductionPoolValuationService.RecordAsync()` uses its generic reversal branch before the normal outbound-allocation branch.

Current generic reversal semantics already provide the required behavior:

- target reversal OUT references original `TRANSFER_IN`;
- `CalculateAvailableBaseQty()` reduces that receipt through `reversedOut`;
- source reversal IN references original `TRANSFER_OUT`;
- the original OUT allocations are canceled with linked reversal allocations;
- original dependency edges receive linked reversal dependency rows;
- original source contribution availability is restored.

Use this existing behavior.

## 2.4 Fix POST/ROLLBACK idempotency to current Production command style

Current WOT uses a request ID created with the draft and checks RowVersion before replay.

That is weaker than Stock Return/Adjustment.

Change WOT POST and ROLLBACK to a stable per-action command RequestId and perform replay detection **before current RowVersion validation**.

---

# 3. Architecture decision

Keep the current business document:

```text
PrWorkOrderTransfer
ProductionWorkOrderTransfer
```

Do NOT convert WOT to `IvTrxBatch` in this correction.

Reason:

The safety-critical work is quantity, valuation, lineage, idempotency and rollback.

A simultaneous document-shell migration increases risk without improving stock mathematics.

Instead, upgrade the existing WOT shell to current Production lifecycle semantics:

```text
DocumentRevision
PRIMARY StockPosting
REVERSAL StockPosting
ProductionPostingGenerationGuard
immutable WOT facts
linked reversal facts
stable command RequestId
POSTED -> safe ROLLBACK -> NEW
```

A future `IvTrxBatch` convergence is separate scope.

---

# 4. Non-negotiable WOT business contract

WOT remains:

```text
one source PROCESS_WIP pool
full remaining balance only
same item
same physical production lot
same production UOM contract
same exact BaseQty
same exact accumulated Production value
one compatible target natural WIP identity
```

No quantity editor.

No partial transfer.

Do not cap/split the source automatically.

If transfer quantity exceeds the target material's informational remaining requirement:

```text
WARN
DO NOT silently cap
DO NOT change transfer qty
```

WOT must never update:

```text
IvBalLoc
IvTrxHistory
StockValuationFact
ProductionMaterialMovement

ProductionWorkOrder.GoodQty
ProductionWorkOrder.RemainingQty

ProductionWorkOrderOperation.InputQty
ProcessedQty
GoodQty
ScrapQty
RejectQty
HoldQty
ReworkQty
TransferredQty
RemainingQty

ProductionWorkOrderMaterial.ReservedQty
PickedQty
IssuedQty
ReturnedQty
ConsumedQty
VarianceQty
```

---

# 5. Keep the proven forward movement engine

Continue using:

```text
IBranchStockTransactionLock
ProductionWorkOrderExecutionLock
IStockPostingCoordinator
IProductionStockWriter
ProductionPoolValuationService
TRANSFER_OUT
TRANSFER_IN
```

Do not create separate transfer-reversal movement codes.

`StockMovementRegistry` already declares:

```text
TRANSFER_OUT  direction -1, inverse TRANSFER_IN
TRANSFER_IN   direction +1, inverse TRANSFER_OUT
```

Forward:

```text
SOURCE:
TRANSFER_OUT
Qty             = full source Qty
BaseQty         = full source BaseQty
ExactTotalValue = full source TotalCost

TARGET:
TRANSFER_IN
Qty             = same Qty
BaseQty         = same BaseQty
ExactTotalValue = same TotalCost
```

After forward Apply:

```text
source.Qty             == 0
source.BaseQty         == 0
source.TotalCost       == 0
source.AverageUnitCost == 0
```

---

# 6. WOT command/API contract

Add:

```csharp
public sealed record ProductionWorkOrderTransferCommand(
    long Id,
    byte[] ExpectedVersion,
    Guid RequestId,
    string? Reason = null);
```

Change interface:

```text
PostAsync(ProductionWorkOrderTransferCommand request)
RollbackAsync(ProductionWorkOrderTransferCommand request)
```

The UI must keep the same pending command object across retries:

```text
_pending ??= new(... Guid.NewGuid() ...)
```

and clear it only after successful completion.

## Important

The existing WOT column:

```text
PostingRequestId
```

is no longer the authoritative StockPosting action RequestId.

Keep it for schema/backward compatibility in this release.

Do not use it to build new StockPosting commands.

Use:

```text
request.RequestId
```

for POST and ROLLBACK.

The Draft `ProductionPostingLink.PostingRequestId` may continue to use its own generated link identity, like current Production stock transactions.

---

# 7. Replay-before-version rule

For POST and ROLLBACK, after branch lock and before normal document RowVersion/lifecycle validation:

1. build a small semantic fingerprint from:
   ```text
   Id
   ExpectedVersion
   CommandType
   Reason
   ```
2. query:
   ```text
   StockPosting
   Company
   Branch
   CommandType
   RequestId
   ```
3. if found:
   - fingerprint differs -> reject RequestId reuse;
   - unsealed -> reconciliation required;
   - sealed -> perform NO mutation and return the current WOT document;
4. only when there is no replay continue to RowVersion/current-state validation.

This protects:

```text
database commit succeeded
HTTP/UI response lost
same command retried
```

from creating ambiguity or a second posting.

Keep StockPostingCoordinator's own replay/uniqueness checks as the second line of defence.

---

# 8. Model/schema changes

Enhance `ProductionWorkOrderTransfer`.

Add:

```text
DocumentRevision int NOT NULL

SourceSnapshotRevision int NULL
SourceSnapshotHash nvarchar(64) NULL

ReversalPostingId bigint NULL
ReversalReason nvarchar(500) NULL

RollbackDate datetime2 NULL
RollbackBy nvarchar(10) NULL
RollbackCount int NOT NULL DEFAULT 0
RollbackOperationId uniqueidentifier NULL
```

## Why Source snapshot fields are nullable in storage

Do not fabricate historical snapshot evidence for old WOT rows.

For all NEW WOTs created after deployment:

```text
SourceSnapshotRevision required
SourceSnapshotHash required
```

For legacy NEW draft:

```text
Refresh Source Snapshot
```

must populate them before POST.

For legacy POSTED WOT without complete new rollback evidence:

```text
view allowed
rollback blocked
```

---

# 9. DocumentRevision migration

Do NOT blindly set every existing WOT to revision `1`.

Current old WOT posting uses:

```text
StockPosting.DocumentRevision = TargetSnapshotRevision
```

For migration:

## Existing POSTED WOT

If exactly one matching sealed current WOT PRIMARY exists:

```text
ProductionWorkOrderTransfer.DocumentRevision
    = StockPosting.DocumentRevision
```

This preserves existing StockPosting generation identity.

## Existing NEW WOT

Set:

```text
DocumentRevision = 1
```

## New WOT

Start:

```text
DocumentRevision = 1
```

After each successful rollback:

```text
DocumentRevision++
```

New POST uses the new document revision.

---

# 10. New immutable WOT fact

Add:

```text
PrProductionWorkOrderTransferFact
```

Suggested fields:

```text
Id bigint PK
TransferId bigint
DocumentRevision int

StockPostingId bigint
ProductionPostingLinkId bigint

SourceProductionBalLotId bigint
DestinationProductionBalLotId bigint

SourceLegMovementId bigint
TargetLegMovementId bigint
MovementGroupId uniqueidentifier

Qty decimal(18,4)
BaseQty decimal(18,4)
BaseUom nvarchar(10)
TotalValue decimal(19,6)
UnitCost decimal(19,6)

SourceBaseBefore decimal(18,4)
SourceValueBefore decimal(19,6)
SourceBaseAfter decimal(18,4)
SourceValueAfter decimal(19,6)

TargetBaseBefore decimal(18,4)
TargetValueBefore decimal(19,6)
TargetBaseAfter decimal(18,4)
TargetValueAfter decimal(19,6)

SourceGenerationBefore int NULL
SourceGenerationAfter int NULL
TargetGenerationBefore int NULL
TargetGenerationAfter int NULL

ReversesFactId bigint NULL
CreatedAtUtc datetime2
```

Constraints/indexes:

```text
StockPostingId UNIQUE
SourceLegMovementId UNIQUE
TargetLegMovementId UNIQUE
MovementGroupId UNIQUE
ReversesFactId UNIQUE WHERE NOT NULL
```

`ReversesFactId` is a self FK.

Facts are append-only.

---

# 11. WOT header current-generation pointer semantics

Define these existing fields clearly:

```text
StockPostingId
DestinationProductionBalLotId
TransferOutMovementId
TransferInMovementId
MovementGroupId
ProductionPostingLinkId
```

When status is POSTED:

```text
they identify the current active PRIMARY generation
```

After rollback -> NEW:

- keep the last PRIMARY stock/movement IDs until the next POST for audit/current-generation context;
- set `ReversalPostingId` to the successful reversal;
- `ProductionPostingLinkId` should point to the newly created DRAFT link for the next forward generation;
- immutable facts remain the historical authority.

On new successful POST:

```text
overwrite current PRIMARY pointers
ReversalPostingId = null
ReversalReason = null
```

Do not delete historical PRIMARY/REVERSAL postings or facts.

---

# 12. ProductionPostingLink generation protection

Add:

```text
ProductionPostingCommandTypes.WorkOrderTransferRollback
    = "WO_TRANSFER_ROLLBACK"
```

Add a filtered unique generation index for forward WOT links:

```text
CompanyCode
BranchCode
CommandType
ProductionDocumentLineId
DocumentRevision
```

where:

```text
ProductionDocumentLineId IS NOT NULL
CommandType = 'WO_TRANSFER_POST'
```

This prevents duplicate Draft/forward links for the same WOT document generation.

Rollback link uses:

```text
CommandType = WO_TRANSFER_ROLLBACK
OriginalPostingLinkId = original forward link
ProductionDocumentLineId = transfer.Uid
DocumentRevision = current posted revision
PostingRequestId = request.RequestId
```

---

# 13. AppDbContext ownership guard

Add:

```csharp
public bool ProductionWorkOrderTransferWrite { get; set; }
```

Extend Production stock transaction write validation.

When false, reject write of:

```text
ProductionWorkOrderTransfer
ProductionWorkOrderTransferFact
```

Regardless of capability reject:

```text
Modified WOT fact
Deleted WOT fact
```

Message:

> Work Order Transfer posting evidence is immutable; append a linked reversal.

Every WOT Create/Update/Refresh/Delete/Post/Rollback service write must enable the capability.

---

# 14. Source Work Order rule

Keep current WOT rule:

```text
RELEASED
IN_PROGRESS
COMPLETED
```

Block:

```text
DRAFT
CLOSED
CANCELLED
```

This applies to NEW source selection/forward POST.

Rollback does **not** reapply this forward-status rule.

Like current Production reversal services, rollback uses immutable posting evidence and current stock/dependency safety.

A WO becoming COMPLETED/CLOSED after WOT must not by itself make an exact reversal mathematically impossible.

---

# 15. Source snapshot safety

At source selection/Create/Refresh/POST require:

```text
SnapshotRevision > 0
SnapshotHash exists and length == 64
IsLegacySnapshot == false
ProductionSnapshotFormatVersions.IsFullHierarchy(...)
ProductionSnapshotHashVersions.SupportsDailyProduction(...)
```

New draft stores:

```text
SourceSnapshotRevision
SourceSnapshotHash
```

At POST under the source WO execution lock require exact match.

Source WIP RowVersion and live facts remain independently frozen:

```text
Qty
BaseQty
TotalCost
UOM/BaseUOM/factor
item
lot
physical lot
producer route
producer operation
production location
LastMovementDate
LastStockEventEffectiveAt
```

---

# 16. Source WIP shape/scalar contract

Require:

```text
Kind == WIP
BalanceStage == PROCESS_WIP
OutputType == WIP_STOCKED

ProducingRouteStepId != null
WorkOrderOperationId != null

Qty > 0
BaseQty > 0

Uom not blank
BaseUom not blank
ConversionFactorToBase > 0

StockStatusCode == AVAILABLE
OriginType == PRODUCED

WorkOrderMaterialId == null
OriginalIssueMovementId == null

ProductionLocationId != null
LotNo not blank
```

Quantity:

```text
IvQty.Round(BaseQty / ConversionFactorToBase)
    == IvQty.Round(Qty)
```

Cost:

```text
TotalCost >= 0
AverageUnitCost >= 0
Money(TotalCost / BaseQty)
    == AverageUnitCost
```

Zero-cost VERIFIED WIP remains legal.

---

# 17. Source producer structural contract

Load producer route + final operation.

Require:

```text
route.WorkOrderId == source.WorkOrderId
operation.RouteStepId == route.Uid
operation.Uid == source.WorkOrderOperationId
operation.IsFinalOperation == true

route.OutputType == WIP_STOCKED
route.OutputItemCode == source.ItemCode

route.OutputUom == source.Uom
route.OutputBaseUom == source.BaseUom
route.OutputConversionFactorToBase
    == source.ConversionFactorToBase
```

The current service is missing the `OutputUom == source.Uom` check.

Add it.

---

# 18. Physical lot gate

Normal current Daily Production creates natural WIP with:

```text
LotNo == PhysicalLotNo
```

For WOT V1 require source:

```text
PhysicalLotNo null/blank
OR
PhysicalLotNo == LotNo
```

If not:

> This WIP uses an alternate/migrated physical-lot identity that Work Order Transfer cannot safely merge into the current natural target WIP model.

Block before mutation.

New target:

```text
LotNo = source.LotNo
PhysicalLotNo = source.LotNo
```

Existing target may be accepted when:

```text
(destination.PhysicalLotNo is null/blank
 OR destination.PhysicalLotNo == source.LotNo)
```

Do not merge two different physical lots.

---

# 19. PoolCode / LotIdentity correction

`PoolCode` is not physical lot identity.

Do not copy:

```text
source.PoolCode
source.LotIdentityKind
```

into a newly created target WIP.

New target:

```text
PoolCode = null
LotIdentityKind = null
```

Existing target:

```text
retain existing PoolCode
retain existing LotIdentityKind
do not require source equality
do not overwrite
```

Movement genealogy remains authoritative through:

```text
valuation evidence
movement allocation
pool dependency
immutable WOT fact
MovementGroupId
```

Update current tests that incorrectly require target PoolCode cloning.

---

# 20. Source valuation + contribution completeness

`ProductionCostReadiness.PoolValuationError()` is required but is not enough.

At lookup/Create/Refresh and authoritatively at POST:

1. call `PoolValuationError`;
2. load `ProductionPoolValuation`;
3. require:
   ```text
   Status == VERIFIED
   TrackedBaseQty == source.BaseQty
   TrackedValue == source.TotalCost
   ```
4. call:
   ```text
   ProductionContributionAvailabilityService.ReadAsync(
       db,
       source.Uid,
       valuation.Generation)
   ```
5. require at least one active positive receipt contribution;
6. all receipt evidence VERIFIED;
7. every available BaseQty >= 0;
8. require:
   ```text
   IvQty.Round(sum AvailableBaseQty)
       == IvQty.Round(source.BaseQty)
   ```

Because WOT moves the entire current pool, the entire live source quantity must be explainable by current-generation contribution evidence.

Failure blocks before mutation.

---

# 21. Target Work Order/material rule

Target Work Order:

```text
RELEASED
IN_PROGRESS
```

Require current full snapshot:

```text
SnapshotRevision > 0
SnapshotHash length 64
full hierarchy
not legacy
supported hash version
```

Material:

```text
WorkOrderId == target
ComponentCode == source.ItemCode
SupplySource == INTERNAL_ROUTE_WIP
ProducingRouteStepId != null
WorkOrderOperationId != null

BaseUom == source.BaseUom
ConversionFactorToBase > 0
RequiredUom not blank
RequiredBaseQty >= 0
```

Validate material scalar UOM evidence where current entity values allow:

```text
RequiredQty
RequiredBaseQty
ConversionFactorToBase
```

must not contradict each other at `IvQty` precision.

Consumer RequiredUom/factor may differ from producer WIP UOM.

Do not require consumer UOM == source WIP UOM.

---

# 22. Target producer contract

Target producer route must match source WIP exactly:

```text
OutputType == WIP_STOCKED
OutputItemCode == source.ItemCode
OutputUom == source.Uom
OutputBaseUom == source.BaseUom
OutputConversionFactorToBase
    == source.ConversionFactorToBase
```

Exactly one final producer operation.

The target live WIP belongs to the target **producer** operation/route.

Do not assign the consuming operation to:

```text
ProductionBalLot.WorkOrderOperationId
```

Consumer context remains WOT business context only.

---

# 23. Target sequence readiness — information, not WOT authority

Load the complete target route/operation graph and evaluate:

```text
IProductionOperationEligibilityService.Evaluate(...)
```

Use the result for:

```text
target lookup readiness
UI warning
POST confirmation context
```

Do NOT use `IsEligible == false` as the sole reason to reject WOT.

Required regression:

```text
target consumer blocked by predecessor
WOT POST succeeds
target WIP qty/value correct
Daily Production still blocks consumption
no GoodQty/TransferredQty was changed

then predecessor becomes complete
Daily Production can consume normally
```

This proves WOT does not bypass current execution logic.

---

# 24. Existing target WIP identity

Locate current natural target identity using:

```text
Company
Branch
Kind == WIP
TargetWorkOrderId
TargetProducingRouteStepId
ItemCode
LotNo
```

If absent:

create zero natural target WIP using target producer identity.

If present lock and require:

```text
Kind == WIP
BalanceStage == PROCESS_WIP
OutputType == WIP_STOCKED

WorkOrderId == target
ProducingRouteStepId == target producer route
WorkOrderOperationId == target final producer operation
WorkOrderMaterialId == null

ItemCode == source item
LotNo == source lot

Uom == source Uom
BaseUom == source BaseUom
factor == source factor

StockStatusCode == AVAILABLE
OriginType == PRODUCED

ProductionLocationId == target producer location
WorkCentreCode == target producer WC
ProcessCode == target producer operation
```

PoolCode is intentionally not compared.

---

# 25. Existing target valuation/contribution gate

If target positive:

```text
Qty > 0
BaseQty > 0
```

require:

- Qty/BaseQty/factor scalar reconciliation;
- TotalCost/AverageUnitCost reconciliation;
- `PoolValuationError == null`;
- valuation VERIFIED;
- tracked qty/value == live;
- complete current-generation contribution availability;
- available contribution sum == target live BaseQty.

If target zero require exact zero:

```text
Qty == 0
BaseQty == 0
TotalCost == 0
AverageUnitCost == 0
```

A new verified transfer must never cleanse or hide a contaminated existing target pool.

---

# 26. Draft operations

## Create

No stock mutation.

Recommended transaction:

1. ADD permission;
2. WOT write capability;
3. branch stock lock;
4. lock/read source;
5. source shape/snapshot/cost/contribution validation;
6. target validation;
7. duplicate NEW-source check;
8. freeze source/target identity;
9. DocumentRevision = 1;
10. create one DRAFT forward posting link;
11. save.

## Update

NEW only.

Editable:

```text
TransferDateTime
TargetWorkOrderMaterialId
Remark
```

Source identity remains immutable.

Revalidate target.

## Refresh Source

NEW only.

Lock source and refresh:

```text
Qty
BaseQty
TransferTotalCost
SourceBalanceRowVersion
SourceLastMovementDate
SourceLastStockEventEffectiveAt
SourceSnapshotRevision
SourceSnapshotHash
```

Only when source item/lot/WO/producer identity remains the same.

---

# 27. Future-date behavior

Keep current WOT ability to prepare a future NEW draft.

Example:

```text
13 Oct
save WOT dated 14 Oct      ALLOW
edit                         ALLOW
POST                         BLOCK

14 Oct
POST                         ALLOW after normal gates
```

Authoritative server POST guard:

```csharp
if (transfer.TransferDateTime > clock.Now)
    reject;
```

Use the current Production exact DateTime convention.

Do not rely only on UI button state.

StockPostingCoordinator remains the authority for period-close validation.

---

# 28. Forward POST lock/replay order

Before transaction read only source/target WO IDs required to establish execution-lock order.

Inside transaction:

1. POST permission;
2. WOT write capability;
3. branch stock lock;
4. **request replay check before current RowVersion validation**;
5. acquire both WO execution locks ascending WorkOrderId;
6. lock WOT row;
7. RowVersion + NEW lifecycle;
8. verify WO IDs did not change since pre-read;
9. lock both WO rows ascending;
10. lock source WIP;
11. source snapshot/scalar/cost/contribution revalidation;
12. future-date guard;
13. source chronology;
14. target snapshot/material/producer revalidation;
15. evaluate target sequence for warning only;
16. lock/create destination;
17. destination identity/cost/contribution revalidation;
18. destination chronology;
19. capture source/target before state;
20. build PRIMARY StockPosting command;
21. BeginInTransaction;
22. apply OUT/IN;
23. valuation;
24. append immutable fact;
25. hard invariants;
26. mark link/document success;
27. seal;
28. commit.

No quantity/value mutation outside this transaction.

---

# 29. Correct StockPosting revision

Current WOT incorrectly uses:

```text
TargetSnapshotRevision
```

as:

```text
StockPosting.DocumentRevision
```

Stop.

Use:

```text
transfer.DocumentRevision
```

Manufacturing snapshots remain separate evidence:

```text
SourceSnapshotRevision/Hash
TargetSnapshotRevision/Hash
```

This is mandatory for rollback/repost generation correctness.

---

# 30. Forward PRIMARY command

Semantic RequestId:

```text
request.RequestId
```

Command:

```text
CommandType        = WO_TRANSFER_POST
SourceModule       = PRODUCTION
SourceDocumentType = WORK_ORDER_TRANSFER
SourceDocumentId   = transfer.Uid
SourceDocumentNo   = transfer.TransferNo
DocumentRevision   = transfer.DocumentRevision
PostingRole        = PRIMARY
EffectiveAt        = transfer.TransferDateTime
ProductionPostingLinkId = current forward DRAFT link
```

Evidence source snapshot must include at least:

```text
source balance identity + RowVersion
source snapshot revision/hash
source Qty/BaseQty/TotalCost
source UOM contract
source last events

target WO/material/producer/consumer identity
target snapshot revision/hash
target current pool ID/before qty/value/generation when existing

effective date
```

---

# 31. Forward movement invariant

Create exactly:

```text
1 TRANSFER_OUT
1 TRANSFER_IN
1 MovementGroupId
```

Current forward `ProductionPoolValuationService` transfer-pair support remains.

Do not weaken:

```text
same posting
different pool
different WO
same item
same Qty
same BaseQty
same Uom
same BaseUom
same factor
same UnitCost
same TotalCost
same document
same tenant
both VERIFIED
OriginalMovementId null
```

---

# 32. Forward contribution and cost invariants

After `ProductionPoolValuationService.RecordAsync`:

## Source

```text
live Qty/BaseQty/TotalCost == 0
tracked BaseQty/Value == 0
```

Require:

```text
sum active ProductionMovementAllocation
for original WOT TRANSFER_OUT
==
TRANSFER_OUT.BaseQty
```

The pre-post contribution completeness gate makes this mandatory.

## Target

```text
valuation.Status == VERIFIED
TrackedBaseQty == live BaseQty
TrackedValue == live TotalCost
```

Read current target generation.

Require:

```text
all active receipt evidence VERIFIED
all available BaseQty >= 0
sum available BaseQty == live target BaseQty
```

---

# 33. Combined conservation invariant

Capture:

```text
sourceBaseBefore
sourceValueBefore
targetBaseBefore
targetValueBefore
```

After forward:

```text
sourceBaseAfter = 0
sourceValueAfter = 0

sourceBaseBefore + targetBaseBefore
    == sourceBaseAfter + targetBaseAfter

sourceValueBefore + targetValueBefore
    == sourceValueAfter + targetValueAfter
```

Use:

```text
IvQty.Round
StockLedgerPrecision.Money
```

Also:

```text
Qty == IvQty.Round(BaseQty / factor)
```

for both live pools.

Any mismatch rolls back the entire transaction.

---

# 34. Forward immutable fact

Append exactly one forward WOT fact after movements/valuation and before seal.

Capture:

- exact movement IDs;
- source/destination pool IDs;
- qty/base/value;
- before/after qty/value;
- valuation generations;
- posting/link/group IDs.

Before seal verify:

```text
exactly one forward fact
```

for this WOT revision/StockPosting.

---

# 35. No-side-effect assertion

For WOT StockPosting:

```text
IvTrxHistory count == 0
StockValuationFact count == 0
ProductionMaterialMovement count == 0
```

Snapshot current execution projections before POST and assert unchanged after WOT.

---

# 36. Rollback business contract

Add ROLLBACK permission.

Only current POSTED WOT can rollback.

Require:

```text
stable RequestId
reason 1..500 chars
```

Rollback is a V2 REVERSAL StockPosting.

Never directly edit balances without movement evidence.

Never delete the original posting.

---

# 37. Rollback effective date and period

Follow current Production rollback semantics:

```text
rollback EffectiveAt = clock.Now
PostingRole = REVERSAL
ReversesPostingId = current PRIMARY StockPostingId
```

The reversal is posted in the current Production period.

Therefore:

- original WOT period may already be closed;
- exact reversal may still proceed when the current reversal period is open;
- current period closed -> rollback blocked;
- do not backdate the reversal into the old period.

StockPostingCoordinator remains period authority.

---

# 38. Rollback replay/lock order

Before transaction read source/target WO IDs only for deterministic execution-lock ordering.

Inside transaction:

1. ROLLBACK permission;
2. validate RequestId/reason;
3. WOT write capability;
4. branch stock lock;
5. **rollback request replay check before current RowVersion validation**;
6. source/target execution locks ascending;
7. lock WOT;
8. current RowVersion;
9. require POSTED;
10. require current PRIMARY pointer;
11. `ProductionPostingGenerationGuard.ValidateRollbackTargetAsync`;
12. load current forward posting link/fact;
13. lock both WO rows;
14. lock source/destination balances deterministically;
15. exact post-forward-state validation;
16. later-active-movement/dependency checks;
17. exact target transfer contribution availability;
18. build REVERSAL StockPosting;
19. create rollback posting link;
20. apply reversal pair;
21. generic valuation reversal;
22. append reversal fact;
23. exact restoration invariants;
24. set document NEW + revision++;
25. create next-generation DRAFT forward link;
26. seal;
27. commit.

---

# 39. Rollback must use exact immutable forward evidence

Require one forward fact for:

```text
TransferId
current DocumentRevision
current PRIMARY StockPosting
ReversesFactId == null
```

Load original:

```text
TRANSFER_OUT
TRANSFER_IN
```

Require them to match the immutable fact and current WOT header.

If fact/evidence is missing:

```text
BLOCK
```

Do not infer a reversal amount from today's balance.

---

# 40. Strict current-state match before rollback

Because this plan deliberately chooses safe rollback over clever algebra:

## Source current state

Require exact forward-after state:

```text
source BaseQty == fact.SourceBaseAfter
source TotalCost == fact.SourceValueAfter
```

For full transfer this must normally be:

```text
0 / 0
```

Also verify source identity/UOM/physical lot metadata still matches the forward fact/document.

## Target current state

Require:

```text
target BaseQty == fact.TargetBaseAfter
target TotalCost == fact.TargetValueAfter
```

and current valuation matches live.

If not:

> The WIP balance no longer matches the posted Work Order Transfer. Reconcile or reverse later activity first.

This prevents rollback across silent/manual projection drift.

---

# 41. Later active movement blockers

Load original forward StockPosting sequence.

For BOTH source and target pools reject any later active production movement where:

```text
PostingSequence > original WOT PostingSequence
OriginalMovementId == null
posting sealed
posting does not have a sealed reversal
```

Message:

> Reverse later active Production movements on this WIP pool first.

This intentionally blocks even later positive movements.

Safety is preferred over attempting proportional algebra against a changed pool.

After a later transaction is fully rolled back, it no longer blocks WOT rollback.

---

# 42. Downstream dependency blocker

Additionally call:

```text
ProductionPoolValuationService.HasActiveDependentsAsync(
    [original TransferInMovementId])
```

If true:

> Rollback is blocked because downstream Production movements depend on the transferred WIP. Reverse downstream production first.

Covers:

```text
WOT -> Daily Production CONSUME
WOT -> another WOT
WOT -> downstream output / FG chain
```

Failed rollback must cause zero balance/value mutation.

---

# 43. Exact target transferred-contribution check

Read current target pool valuation generation:

```text
ProductionContributionAvailabilityService.ReadAsync(...)
```

Find:

```text
Movement.Uid == original TransferInMovementId
```

Require:

```text
evidence VERIFIED
AvailableBaseQty == original TransferIn.BaseQty
```

If not:

> The transferred WIP has been partially or fully consumed and cannot be rolled back.

This is a pre-mutation safety gate.

---

# 44. Reversal movement pair

Use current movement codes with `OriginalMovementId`.

## Remove target transfer

```text
Balance            = target
MovementType       = TRANSFER_OUT
Qty                = original TransferIn.Qty
BaseQty            = original TransferIn.BaseQty
ExactTotalValue    = original TransferIn.TotalCost
OriginalMovementId = original TransferInMovementId
```

## Restore source

```text
Balance            = source
MovementType       = TRANSFER_IN
Qty                = original TransferOut.Qty
BaseQty            = original TransferOut.BaseQty
ExactTotalValue    = original TransferOut.TotalCost
OriginalMovementId = original TransferOutMovementId
```

Use a new reversal MovementGroupId.

Do not reuse original group.

Before valuation assert reversal legs exactly mirror the original pair.

---

# 45. Required valuation-service change for rollback

Current:

```text
ValidateWorkOrderTransferPairs(...)
```

assumes WOT movement pairs are forward PRIMARY and explicitly requires:

```text
OriginalMovementId == null
```

Therefore change only the pair-routing behavior.

Recommended:

```csharp
var transferPairs =
    context.Posting.ReversesPostingId is null
        ? ValidateWorkOrderTransferPairs(movements, context.Posting.Id)
        : [];
```

For WOT REVERSAL:

```text
do not run forward transfer-pair authority/dependency logic
use existing generic OriginalMovementId reversal logic
```

The WOT service itself must validate the reversal pair before calling `RecordAsync`.

Do NOT alter Return/Adjustment exact-receipt directive rules.

Do NOT add a WOT `RequiredReceiptMovementByOutboundMovementId` path.

---

# 46. Why generic valuation reversal is correct

For target reversal `TRANSFER_OUT`:

```text
OriginalMovementId = original TRANSFER_IN
```

Current contribution availability formula already treats this as:

```text
reversedOut
```

which reduces the original transfer receipt availability.

For source restoration `TRANSFER_IN`:

```text
OriginalMovementId = original TRANSFER_OUT
```

Current valuation code loads the original OUT allocations and appends linked reversal allocations.

That cancels the original usage of source receipts and restores their availability.

Current generic dependency reversal also appends linked `ReversesDependencyId` rows.

This is the desired lineage behavior.

---

# 47. Rollback restoration invariant

After reversal require exact original before-state:

```text
source.BaseQty   == fact.SourceBaseBefore
source.TotalCost == fact.SourceValueBefore

target.BaseQty   == fact.TargetBaseBefore
target.TotalCost == fact.TargetValueBefore
```

For both:

```text
valuation TrackedBaseQty == live BaseQty
valuation TrackedValue == live TotalCost
no negative qty/value
no stranded value
```

Read current contribution availability and require:

```text
sum available current-generation BaseQty
==
live BaseQty
```

Source original receipt contributions must be restored.

Target original transfer contribution must have zero remaining availability after its reversal.

---

# 48. Reversal immutable fact

Append one new WOT fact:

```text
StockPostingId = REVERSAL
ReversesFactId = forwardFact.Id
```

Record:

```text
source restore TRANSFER_IN
target removal TRANSFER_OUT
new MovementGroupId
same exact Qty/BaseQty/TotalValue
```

Never modify forward fact.

---

# 49. Document state after rollback

After all reversal invariants:

```text
Status = NEW

ReversalPostingId = reversal StockPosting
ReversalReason = reason
RollbackDate = clock.Now
RollbackBy = user
RollbackCount++
RollbackOperationId = request.RequestId

DocumentRevision++
```

Keep last PRIMARY movement/posting pointers until next successful POST.

Create a NEW forward DRAFT posting link for the incremented revision and set:

```text
ProductionPostingLinkId = new link
PostingRequestId = new link identity (legacy compatibility only)
```

Refresh source live snapshot fields produced by the rollback:

```text
SourceBalanceRowVersion
SourceLastMovementDate
SourceLastStockEventEffectiveAt
SourceSnapshotRevision/Hash when the current source WO snapshot is valid
```

Target forward eligibility is revalidated again before any repost.

---

# 50. Repost rule after rollback

The rollback movement sets source/target last stock event to rollback EffectiveAt.

Therefore an old transfer date may now violate chronology.

Do not silently alter it.

Example:

```text
original WOT date = 10 Oct
rollback = 13 Oct
document becomes NEW

attempt repost at 10 Oct -> BLOCK
```

User must choose a valid new effective date.

New successful primary:

```text
uses incremented DocumentRevision
overwrites current primary pointers
clears current ReversalPostingId/ReversalReason
historical reversal remains in StockPosting/facts
```

---

# 51. Delete/archive

Keep current WOT soft-delete behavior.

Rules:

```text
NEW:
    soft-delete/archive allowed

POSTED:
    rollback first

NEW after rollback:
    soft-delete/archive allowed
    never delete historical facts/postings/movements
```

Do not physically delete posting evidence.

---

# 52. Legacy POSTED WOT rule

Existing WOTs posted before this upgrade have no immutable WOT fact.

Do not fabricate before-state values.

Rollback is allowed only when exact evidence can be safely established by an explicit reconciliation/migration routine.

Default safe behavior:

> This Work Order Transfer predates rollback evidence and cannot be reversed automatically. Run WOT reconciliation first.

In a development/test database where old data may be discarded, recreate using the new schema instead of inventing history.

---

# 53. UI must follow current Production Stock Return standard

Primary references:

```text
PrProductionStockReturnEntry.razor/.cs/.css
PrProductionStockReturnList.razor/.cs

PrProductionStockAdjustmentEntry.razor/.css
PrProductionStockAdjustmentList.razor
```

Keep current WOT route to avoid unrelated navigation breakage:

```text
/planning/work-order-transfer
```

Do not invent a separate visual language.

---

# 54. Entry page structure

```text
iv-page

Hero
  Work Order Transfer
  Transfer No
  Status
  From WO
  To WO

Command Bar
  LEFT
    BACK
    CHANGE SOURCE/TARGET where valid

  RIGHT
    SAVE DRAFT
    POST
    ROLLBACK
    DELETE
```

State:

```text
new unsaved:
    SAVE / BACK

saved NEW:
    SAVE / POST / DELETE / BACK

POSTED:
    ROLLBACK / BACK
```

Cost only with VIEW_COST.

---

# 55. Entry cards

```text
Transfer Details
  Transfer Date
  Status
  Transfer No
  Remark

FROM — Source WIP
  searchable source lookup
  WO
  Item / Description
  Lot
  Qty / UOM
  BaseQty/BaseUOM
  WC / Process
  value if VIEW_COST

TO — Target Requirement
  searchable target lookup
  Target WO / Product
  Material
  Consumer WC / Process
  Producer WC / Process
  material requirement information
  current target WIP/value if VIEW_COST
  sequence readiness warning if not currently executable

Transfer Summary / Readiness
  full-balance rule
  physical lot
  qty/base qty
  exact value if allowed
  posting blockers
```

---

# 56. Professional semantic widths

```text
Date                 <= 190px
Transfer / WO No     160–220px
Item Code            160–220px
Lot                  160–240px
WC / Process         140–200px
UOM                   90–120px
Qty                   112–140px
Description          280–480px
Remark               wider/full row
Search lookup        up to ~760px
```

Desktop:

```text
4-column compact grid where practical
```

Tablet:

```text
2 columns
```

Mobile:

```text
1 column
buttons wrap
lookups full available width
```

No raw bootstrap layout with random field spans.

---

# 57. Source/target lookup redesign

Current service scans up to:

```text
2000 source candidates
3000 target materials
```

then performs row-by-row validation.

That silently makes candidates beyond the cap unreachable.

Replace with a bounded paged candidate-scanning design.

Requirements:

- no hidden global truncation;
- every eligible candidate must be reachable;
- SQL filters the cheap structural conditions first;
- preload related WO/route/operation/value evidence in bounded batches;
- no uncontrolled N+1 for thousands of candidates;
- 300–400ms search debounce;
- server paging;
- useful loading/empty state.

Source columns:

```text
WO
Item / Description
Lot
Qty/UOM
WC
Process
Cost readiness
Select
```

Target columns:

```text
WO
Product
Material
Requirement
Consumer WC/Process
Producer WC/Process
Sequence Ready / Waiting
Select
```

Sequence `Waiting` remains selectable; show the blocking reason.

---

# 58. List UI

Use:

```text
CommonDataGridEx
iv-list-desktop
iv-list-compact
filter popup
standard hero/toolbar
```

Columns:

```text
Transfer No
Date
Item
Lot
Qty/UOM
From WO
From WC/Process
To WO
Consumer WC/Process
Status
```

Single-document actions for this critical release:

```text
NEW
VIEW
EDIT
POST
ROLLBACK
DELETE
```

Do not introduce multi-POST or multi-ROLLBACK until the single-document rollback race suite is proven.

---

# 59. Permissions

Menu permissions:

```text
ACCESS
ADD
EDIT
DELETE
POST
ROLLBACK
VIEW_COST
```

Do not auto-grant ROLLBACK to existing roles.

Use normal role administration.

---

# 60. Core files

At minimum:

```text
ErpWeb.Core/Production/IProductionWorkOrderTransferService.cs
ErpWeb.Core/Production/ProductionWorkOrderTransferService.cs

ErpWeb.Core/Production/ProductionPoolValuationService.cs
ErpWeb.Core/Production/ProductionContributionAvailabilityService.cs
ErpWeb.Core/Production/ProductionPostingGenerationGuard.cs

ErpWeb.Model/Entities/Production/ProductionWorkOrderTransfer.cs
ErpWeb.Model/Entities/Production/ProductionDomainConstants.cs
ErpWeb.Model/Configurations/Production/ProductionWorkOrderTransferConfiguration.cs
ErpWeb.Model/Configurations/Production/ProductionPostingLinkConfiguration.cs
ErpWeb.Model/Data/AppDbContext.cs

ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor.css

ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferList.razor
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferList.razor.cs

scripts/create-production-work-order-transfer.sql
scripts/init-planning-work-order-transfer-menu.sql

ErpWeb.Tests/Production/Transaction/ProductionWorkOrderTransferServiceTests.cs
ErpWeb.Tests/Production/Transaction/ProductionWorkOrderTransferSqlServerTests.cs
ErpWeb.Tests/Production/Transaction/ProductionWorkOrderTransferSchemaTests.cs
```

Do not refactor unrelated production transaction services.

---

# 61. SQL/preflight requirements

Scripts must be:

```text
SET XACT_ABORT ON
transactional
idempotent
additive
```

Do not drop WIP identity indexes.

Do not mutate live Production balances in deployment SQL.

Preflight verifies:

- `PrProductionBalLot` WIP uniqueness;
- StockPosting PRIMARY/REVERSAL uniqueness;
- WOT fact table/FKs/indexes;
- transfer movement types;
- valuation evidence/allocation/dependency tables;
- current ProductionPostingLink infrastructure;
- all required FK/index constraints trusted/enabled.

Migration of `DocumentRevision` follows section 9.

Do not fabricate source snapshot/fact evidence for legacy rows.

---

# 62. Mandatory forward service tests

## Source

1. DRAFT source WO blocked.
2. CLOSED source blocked.
3. CANCELLED source blocked.
4. RELEASED allowed.
5. IN_PROGRESS allowed.
6. COMPLETED with verified residual WIP allowed.
7. invalid/missing source snapshot blocked.
8. source snapshot changes after draft -> POST blocked.
9. source balance RowVersion changes -> POST blocked.
10. malformed producer route/op blocked.
11. producer `OutputUom` mismatch blocked.
12. physical lot != LotNo blocked.
13. UNVALUED source blocked.
14. numeric valuation mismatch blocked.
15. incomplete current-generation contribution evidence blocked.

## Target

16. target DRAFT/COMPLETED/CLOSED/CANCELLED blocked.
17. target snapshot invalid/change blocked.
18. non-INTERNAL_ROUTE_WIP blocked.
19. component mismatch blocked.
20. material BaseUOM/scalar contract invalid blocked.
21. producer UOM/BaseUOM/factor mismatch blocked.
22. multiple/no final producer operation blocked.
23. existing target identity collision blocked.
24. existing UNVALUED target blocked.
25. existing target incomplete contribution evidence blocked.
26. source PoolCode not copied to new target.
27. existing target PoolCode remains unchanged.
28. target consumer sequence not ready: WOT still allowed, warning returned/displayed.

## Posting

29. future NEW save allowed.
30. future POST blocked.
31. closed effective period POST blocked.
32. source exactly zero after full transfer.
33. target exact qty/base/value increase.
34. aggregate BaseQty conserved.
35. aggregate value conserved.
36. OUT allocations exactly cover OUT BaseQty.
37. target available contribution sum == live target BaseQty.
38. no Inventory history.
39. no Inventory valuation facts.
40. no ProductionMaterialMovement.
41. no WO/operation/material execution projection changes.

---

# 63. Mandatory POST replay/idempotency tests

1. same POST RequestId + same semantic input:
   ```text
   one sealed PRIMARY
   one movement pair
   replay returns current document
   ```
2. same RequestId + different ExpectedVersion/input -> reject RequestId reuse.
3. commit succeeds / simulated response loss / retry old RowVersion with same RequestId -> deterministic replay succeeds, no duplicate.
4. unsealed existing request -> reconciliation error.
5. two different POST RequestIds concurrently for same NEW revision -> exactly one PRIMARY due source-revision uniqueness.
6. POST after already POSTED with unrelated RequestId -> no second primary; clear validation/document-already-posted response.

---

# 64. Mandatory rollback tests

## Happy path

1. POST WOT.
2. ROLLBACK with reason.
3. one sealed REVERSAL referencing primary.
4. one rollback link.
5. source exact pre-WOT qty/value restored.
6. target exact pre-WOT qty/value restored.
7. source valuation == live.
8. target valuation == live.
9. source original contribution availability restored.
10. target transfer-in contribution availability becomes zero.
11. linked reversal fact exists.
12. document -> NEW.
13. revision increments.
14. next forward DRAFT link created.

## Blockers

15. no reason -> blocked.
16. missing primary -> blocked.
17. missing/legacy immutable fact -> blocked.
18. primary already reversed -> blocked.
19. source current state differs from forward-after fact -> blocked.
20. target current state differs from forward-after fact -> blocked.
21. active target dependent -> blocked.
22. later active target movement -> blocked.
23. later active source movement -> blocked.
24. original transfer contribution partly consumed -> blocked.
25. current rollback period closed -> blocked.
26. movement/fact/value evidence mismatch -> blocked.

## Current Production reversal semantics

27. original WOT period closed + current period open -> rollback may succeed if all stock/evidence gates pass.
28. source/target WO status changed after posting does not alone corrupt/block exact reversal.
29. same rollback RequestId replay -> one reversal only.
30. same rollback RequestId with different reason/input -> reject.

---

# 65. Mandatory route-sequence regression

Scenario:

```text
Target consumer has incomplete predecessor
```

Expected:

```text
WOT target can be selected
UI says WAITING / shows BlockingReason
WOT POST succeeds
target WIP receives exact qty/value

Daily Production target consumer:
BLOCKED by existing sequence gate

complete predecessor normally

Daily Production:
now allowed
can consume transferred WIP
```

Assert:

```text
WOT never modifies predecessor GoodQty
WOT never modifies consumer GoodQty
WOT never modifies TransferredQty
```

---

# 66. Mandatory source COMPLETED regression

Scenario:

```text
source WO = COMPLETED
verified residual PROCESS_WIP exists
target = active
```

Expected:

```text
WOT POST succeeds
source residual WIP -> zero
target receives exact qty/value
WO source status remains COMPLETED
no WO execution projection changes
```

Immediate rollback:

```text
source residual WIP restored exactly
source status remains COMPLETED
```

This validates current WOT-specific residual-stock behavior.

---

# 67. Cross-transaction lineage tests

## A. Source Daily Production

```text
IP
 -> source Daily Production
 -> source WIP
 -> WOT
```

Attempt source Daily Production rollback:

```text
BLOCKED by WOT dependency
```

Rollback WOT.

Attempt source Daily Production rollback again with no other downstream use:

```text
WOT itself no longer blocks
```

## B. Target consumption

```text
WOT
 -> target Daily Production consumes
```

WOT rollback:

```text
BLOCK
zero mutation
```

Rollback target Daily Production first.

Then WOT rollback:

```text
ALLOW if all remaining gates pass
```

## C. WOT chain

```text
WO-A --WOT1--> WO-B --WOT2--> WO-C
```

WOT1 rollback while WOT2 active:

```text
BLOCK
```

Rollback WOT2, then WOT1:

```text
ALLOW
```

## D. FG path

```text
WOT PROCESS_WIP
 -> target Daily Production
 -> FG_STAGING
 -> FG Receipt
```

WOT never touches:

```text
FG_STAGING directly
IvBalLoc
IvTrxHistory
Inventory valuation facts
```

---

# 68. Mandatory SQL Server concurrency tests

1. concurrent duplicate NEW drafts -> one active draft.
2. double POST same request -> one sealed primary.
3. different POST requests same revision -> one primary.
4. concurrent transfers merge into one target WIP identity.
5. WOT POST vs target Daily Production -> serialized/no negative qty.
6. WOT POST vs natural target production -> one target identity.
7. WOT ROLLBACK vs target Daily Production -> one valid outcome; never partial.
8. WOT ROLLBACK vs WOT2 target-out -> one valid outcome.
9. double ROLLBACK same request -> one sealed reversal.
10. double ROLLBACK different requests -> one sealed reversal due unique reversal target.
11. rollback vs new source movement -> serialize/block.
12. no deadlock under source/target WO IDs in opposite numeric order.
13. after every successful race:
    ```text
    Qty >= 0
    BaseQty >= 0
    TotalCost >= 0
    valuation == live
    contribution availability == live BaseQty
    ```

---

# 69. Repost after rollback tests

1. POST revision 1.
2. ROLLBACK revision 1 -> REVERSAL.
3. document revision becomes 2.
4. old effective date now before rollback stock event -> repost blocked.
5. user changes to valid date.
6. source/target snapshot/readiness revalidated.
7. POST revision 2.
8. distinct sealed PRIMARY for rev2.
9. current WOT header points to rev2 movement/posting.
10. rev1 PRIMARY/REVERSAL remain queryable in immutable history.

---

# 70. Reconciliation release gate

After POST and after ROLLBACK run/assert the equivalent current Production Stock Reconciliation checks for both pools.

Require:

```text
Qty/BaseQty/factor coherent
BaseQty >= 0
TotalCost >= 0
no stranded value

valuation.TrackedBaseQty == live BaseQty
valuation.TrackedValue == live TotalCost

current-generation available contribution sum
    == live BaseQty

no duplicate active natural WIP identity
```

No reconciliation ERROR may remain.

---

# 71. Full acceptance scenario

1. Release source WO.
2. IP source material.
3. Post source Daily Production.
4. verify source WIP VERIFIED + complete contribution evidence.

5. Create target active WO where consumer predecessor is not complete.
6. create WOT.
7. verify target UI says sequence WAITING but allows transfer.
8. POST WOT.

Verify:

```text
source exact zero
target exact qty/value increase
aggregate qty/value unchanged
no execution projection changed
no Inventory changed
```

9. try target Daily Production -> sequence blocked.

10. ROLLBACK WOT immediately.
11. verify exact pre-transfer source/target restoration.

12. update WOT date to valid post-rollback date.
13. POST again.

14. complete target predecessor normally.
15. target Daily Production consumes transferred WIP.
16. WOT rollback now blocked.

17. continue production to FG_STAGING.
18. post FG Receipt.
19. run Production reconciliation.
20. run full:
    ```text
    dotnet test ErpWeb.slnx
    ```

No regression.

---

# 72. UI acceptance

Desktop:

- Stock Return-style hero/command bar;
- clean horizontal alignment;
- semantic widths;
- no raw HTML transaction list;
- FROM/TO cards balanced;
- lookup instead of memorized WO codes;
- status/action visibility clear.

Mobile:

- one column;
- no editor overflow;
- usable lookup;
- compact list;
- buttons wrap.

Behavior:

- future draft save works;
- future POST server-blocks;
- POST uses stable retry RequestId;
- ROLLBACK reason required;
- downstream blocker message is understandable;
- target sequence WAITING is informative, not misleading;
- cost hidden without VIEW_COST.

---

# 73. AI agent MUST NOT

Do NOT:

- change Inventory FIFO/cost layers;
- touch `IvBalLoc` for WOT;
- create Inventory history/value facts;
- mutate material ISSUE/RETURN/CONSUME;
- alter Work Order/operation execution quantities;
- weaken Daily Production route sequence;
- weaken IP/Daily/FG rollback dependency rules;
- introduce partial WOT;
- silently cap transfer to target requirement;
- copy source PoolCode into new target;
- merge different physical lots;
- invent historical source snapshot/fact evidence;
- rollback without exact immutable forward evidence;
- rollback across later active pool movements;
- calculate full-transfer value as `qty * current average` when exact original TotalCost is available;
- bypass `ProductionStockWriter`;
- bypass `ProductionPoolValuationService`;
- directly update `ProductionPoolValuation`;
- remove current forward WOT pair validation;
- extend `RequiredReceiptMovementByOutboundMovementId` for WOT reversal;
- convert WOT to IvTrxBatch in this patch;
- refactor unrelated Production services.

---

# 74. Implementation order

## Phase 1 — lifecycle/evidence schema

- DocumentRevision migration
- nullable source snapshot evidence
- reversal metadata
- immutable fact
- write ownership guard
- rollback command constant
- WOT posting-link generation index

## Phase 2 — forward safety

- physical lot gate
- PoolCode correction
- OutputUom check
- source contribution completeness
- target material/UOM checks
- target contribution completeness
- future POST guard
- true DocumentRevision

## Phase 3 — POST idempotency

- command DTO
- stable per-action RequestId
- replay-before-version
- UI pending request preservation

## Phase 4 — harden forward facts/invariants

- before/after capture
- immutable fact
- aggregate conservation
- allocation/contribution assertions
- no-side-effect assertions

## Phase 5 — rollback

- ROLLBACK permission/API/UI
- generation guard
- strict forward-after current-state match
- later movement/dependency blocks
- exact target contribution check
- TRANSFER_OUT/TRANSFER_IN reversals with OriginalMovementId
- skip forward pair authority on reversal
- immutable reversal fact
- exact restoration
- NEW revision + new forward Draft link

## Phase 6 — UI standardization

- entry
- list
- paged lookups
- semantic widths
- target sequence readiness warning

## Phase 7 — SQL Server + cross-transaction validation

Do not start broad UI refactoring before core posting/rollback tests are green.

---

# 75. Final approval matrix

| Area | Score |
|---|---:|
| Existing forward movement core preservation | 10/10 |
| Full BaseQty conservation | 10/10 |
| Exact production value conservation | 10/10 |
| Source valuation readiness | 10/10 |
| Source contribution completeness | 10/10 |
| Target valuation/contribution protection | 10/10 |
| Physical lot identity safety | 10/10 |
| PoolCode safety | 10/10 |
| Completed-source residual WIP behavior | 10/10 |
| Daily Production sequence isolation | 10/10 |
| POST idempotency/replay | 10/10 |
| Posting generation identity | 10/10 |
| Rollback exact-evidence gate | 10/10 |
| Rollback contribution restoration | 10/10 |
| Rollback downstream dependency protection | 10/10 |
| Rollback exact qty/value restoration | 10/10 |
| Closed-period reversal semantics | 10/10 |
| Future POST protection | 10/10 |
| Concurrency/deadlock safety plan | 10/10 |
| IP / Daily / WIP / FG isolation | 10/10 |
| UI consistency/usability | 10/10 |
| Audit/history preservation | 10/10 |

# FINAL STATUS

**APPROVED FOR IMPLEMENTATION — 10/10 PLAN**

This approval applies to the corrected PLAN only.

The implementation is not release-approved until:

```text
all forward tests
all rollback tests
all SQL Server race tests
all cross-transaction lineage tests
all reconciliation gates
full solution tests
```

pass.

The controlling rule is:

> A Work Order Transfer may be convenient to operate, but it must never create or destroy Production quantity/value, hide incomplete costing lineage, bypass Daily Production execution rules, or reverse stock after the transferred contribution has been used.
