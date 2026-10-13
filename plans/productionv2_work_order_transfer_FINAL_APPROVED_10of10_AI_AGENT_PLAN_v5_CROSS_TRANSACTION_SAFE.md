# ProductionV2 Work Order WIP Transfer
## FINAL CROSS-TRANSACTION SAFETY REVIEW — APPROVED FOR IMPLEMENTATION — 10/10 AI CODING AGENT PLAN v5

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Reviewed commit:** `f3d2d94634809559795d5a74649d4759c28baa78`  
**Reviewed tree:** `d6528b42c13da006a4d0a1d4f44d678ac5b80c49`  
**Commit message:** `stock return`  
**Status:** **FINAL GATE — APPROVED FOR IMPLEMENTATION**  
**Score:** **10.0 / 10.0**, conditional on all mandatory safeguards and regression gates in this plan passing.  
**Supersedes:** previous v2/v3/v4 Work Order Transfer plans.

---

# 1. Final review decision

This plan was re-audited against the current `productionv2` branch. The branch changed materially after the previous review because Production Stock Return and Production Stock Adjustment are now implemented.

The current repo establishes stronger production transaction standards. Work Order Transfer must follow them.

The final v3 design therefore makes these important corrections:

1. Use `IvTrxBatch` for document lifecycle and numbering, with a production Work Order Transfer sidecar and immutable posting fact.
2. Acquire the branch stock lock, then `ProductionWorkOrderExecutionLock` for **both** source and target Work Orders in ascending WorkOrderId order.
3. Do **not** copy source `PoolCode` into a newly created target WIP pool.
4. Preserve `LotNo` / `PhysicalLotNo` for physical traceability; preserve genealogy through movement dependency.
5. Separate the target **producer WIP contract** from the target **consumer material contract**.
6. Match target producer `OutputUom/BaseUom/factor` exactly to source WIP; allow the target consumer material's `RequiredUom/factor` to differ if it converts through the same base UOM.
7. Restrict both source and target Work Orders to `RELEASED` / `IN_PROGRESS` in V1.
8. Require not only VERIFIED numeric valuation, but complete current-generation contribution availability equal to the live pool BaseQty.
9. Extend the existing `ProductionPoolValuationDirectives` mechanism narrowly for verified `TRANSFER_IN` authority.
10. Do not add an off-by-default WOT posting feature flag.

These corrections make the plan safer and more consistent with the latest repo.

---

# 2. Business objective

Implement a first-class **Work Order WIP Transfer** transaction that moves the **entire remaining balance** of one verified stockable WIP pool from one active Work Order to another compatible active Work Order.

It must preserve:

- same item;
- same physical lot;
- same total quantity;
- same base quantity;
- same UOM evidence;
- same accumulated production value;
- verified costing lineage;
- production genealogy;
- tenant/branch isolation;
- V2 stock-ledger chronology.

This is not new production, not item conversion, not warehouse Inventory transfer, and not a Work Order quantity adjustment.

---

# 3. Non-negotiable architecture

Do not port OriginERP posting code line-for-line.

Do not make legacy `WipItemBalLoc` or `PrSchDailyProd` authoritative.

The V2 flow must be:

```text
IvTrxBatch (WOTR / NEW)
        |
        +-- PrProductionWorkOrderTransfer
        |
        | POST
        v
BranchStockTransactionLock
        |
        +-- ProductionWorkOrderExecutionLock(source)
        +-- ProductionWorkOrderExecutionLock(target)
        |
        v
StockPostingCoordinator
        |
        +-- ProductionStockWriter
        |      +-- TRANSFER_OUT source ProductionBalLot
        |      +-- TRANSFER_IN  target ProductionBalLot
        |
        +-- ProductionPoolValuationService
        |      +-- exact source contributor allocations
        |      +-- verified inbound transfer authority
        |
        +-- ProductionPoolDependency OUT -> IN
        +-- immutable PrProductionWorkOrderTransferFact
        +-- sealed StockPosting
        |
        v
IvTrxBatch = POSTED
```

`ProductionBalLot` remains the live Production stock authority.

`ProductionBalLotMovement` remains the immutable movement authority.

---

# 4. Repo authorities to re-open before coding

The agent must verify these files are still materially unchanged:

```text
ErpWeb.Model/Entities/Production/ProductionBalLot.cs
ErpWeb.Model/Configurations/Production/ProductionBalLotConfiguration.cs
ErpWeb.Model/Entities/Production/ProductionBalLotMovement.cs
ErpWeb.Model/Configurations/Production/ProductionBalLotMovementConfiguration.cs

ErpWeb.Core/Production/ProductionStockWriter.cs
ErpWeb.Core/Production/ProductionCostReadiness.cs
ErpWeb.Core/Production/ProductionPoolValuationService.cs
ErpWeb.Core/Production/ProductionContributionAvailabilityService.cs

ErpWeb.Core/Production/ProductionOutputService.Posting.cs
ErpWeb.Core/Production/ProductionOutputService.SharedWip.cs

ErpWeb.Core/Production/ProductionStockReturnService*
ErpWeb.Core/Production/ProductionStockAdjustmentService.cs
ErpWeb.Model/Configurations/Production/ProductionStockTransactionMapping.cs

ErpWeb.Core/Production/ProductionWorkOrderExecutionLock.cs
ErpWeb.Core/StockLedger/BranchStockTransactionLock.cs
ErpWeb.Core/StockLedger/StockPostingCoordinator.cs

ErpWeb.Core/Traceability/LotGenealogyService.cs
```

If these core contracts move materially before implementation, STOP and re-review.

---

# 5. Reuse existing movement types

Already present:

```text
TRANSFER_OUT
TRANSFER_IN
```

Do not create duplicate WOT movement codes.

The existing movement registry already defines their signed directions.

---

# 6. V1 scope

Included:

- Work Order Transfer list;
- entry/view/edit;
- NEW -> POSTED;
- delete NEW;
- one source WIP pool per document;
- full remaining source balance only;
- same item;
- same physical lot;
- compatible target `INTERNAL_ROUTE_WIP` requirement;
- exact production value;
- V2 StockPosting;
- source contribution allocation;
- OUT -> IN genealogy;
- STRICT Daily Production compatibility;
- Shared-WIP transfer provenance;
- permissions;
- SQL preflight;
- concurrency tests;
- full regression.

Not included:

- partial transfer;
- item substitution;
- physical re-lotting;
- warehouse Inventory transfer;
- `IvBalLoc`;
- `IvTrxHistory`;
- Inventory FIFO posting;
- GL;
- absorbed conversion cost;
- Work Order execution quantity update;
- Work Order status change;
- BOM change;
- DR/SO/procurement change;
- legacy dual-write;
- rollback.

---

# 7. Source/target Work Order status

V1 allows only:

```text
RELEASED
IN_PROGRESS
```

for both source and target.

Block:

```text
DRAFT
COMPLETED
CLOSED
CANCELLED
```

Although OriginERP allowed completed-source transfer, the current new production architecture treats these stock transactions as execution-time operations on active Work Orders. Completed-WO correction is a separate future feature.

---

# 8. Current snapshot gate

Both source and target Work Orders must have a valid current manufacturing snapshot.

Require:

```text
ProductionSnapshotFormatVersions.IsFullHierarchy(order.SnapshotFormatVersion)
order.IsLegacySnapshot == false
order.SnapshotRevision > 0
order.SnapshotHash != null
order.SnapshotHash.Length == 64
ProductionSnapshotHashVersions.SupportsDailyProduction(order.SnapshotHashVersion)
```

Store both source and target SnapshotRevision/Hash on the draft.

Revalidate both under locks at POST.

---

# 9. Full-balance only

There is no quantity editor.

At save:

```text
TransferQty       = source.Qty
TransferBaseQty   = source.BaseQty
TransferTotalCost = source.TotalCost
```

At POST require exact equality with the saved source snapshot.

If changed, fail:

> Source WIP changed after this transfer was prepared. Refresh the source before posting.

Never silently replace the saved amount.

---

# 10. Source WIP eligibility

Require:

```text
Kind                   == WIP
BalanceStage           == PROCESS_WIP
OutputType             == WIP_STOCKED
ProducingRouteStepId   != null
WorkOrderOperationId   != null
Qty                    > 0
BaseQty                > 0
Uom                    not blank
BaseUom                not blank
ConversionFactorToBase > 0
StockStatusCode        == AVAILABLE
OriginType             == PRODUCED
ProductionLocationId   != null
LotNo                  not blank
```

Do not expose immediate process-handoff balances.

Do not modify `ProductionProcessHandoff.cs`.

---

# 11. Source scalar contract

Use the same safeguard pattern now used by Production Stock Return/Adjustment.

Require:

```text
ConversionFactorToBase > 0

IvQty.Round(BaseQty / ConversionFactorToBase)
    == Qty

if BaseQty > 0:
    StockLedgerPrecision.Money(TotalCost / BaseQty)
        == AverageUnitCost

if BaseQty == 0:
    Qty == 0
    TotalCost == 0
    AverageUnitCost == 0
```

The WOT source must be positive.

---

# 12. Source structural contract

Load source Work Order, producer route and producer operation.

Require:

```text
SourceRoute.WorkOrderId == Source.WorkOrderId
SourceOperation.RouteStepId == Source.ProducingRouteStepId
SourceOperation.Uid == Source.WorkOrderOperationId
SourceOperation.IsFinalOperation == true

SourceRoute.OutputType == WIP_STOCKED
SourceRoute.OutputItemCode == Source.ItemCode

SourceRoute.OutputUom == Source.Uom
SourceRoute.OutputBaseUom == Source.BaseUom
SourceRoute.OutputConversionFactorToBase == Source.ConversionFactorToBase
```

Malformed migrated WIP is not a valid V1 source.

---

# 13. Physical lot gate

Normal current Daily Production WIP uses the same output lot for `LotNo` and `PhysicalLotNo`.

V1 source therefore requires:

```text
PhysicalLotNo is null/blank
OR
PhysicalLotNo == LotNo
```

If these differ, block and require a separate migration/repair decision rather than guessing target identity.

---

# 14. PoolCode safeguard

`PoolCode` is not the physical lot.

Current normal Daily Production WIP creation does not populate it.

Therefore:

- source PoolCode may be stored on the WOT snapshot for diagnosis/audit;
- source PoolCode is **not copied** to a new target WIP;
- new target `PoolCode` is null;
- new target `LotIdentityKind` follows natural current WIP behavior and is null unless that core behavior changes before implementation;
- an existing target PoolCode remains untouched;
- source and target PoolCode are not required to match.

Genealogy is preserved through movement dependency, not by cloning a pool identifier.

---

# 15. Source cost readiness

At source selection and POST:

```text
ProductionCostReadiness.PoolValuationError(db, source) == null
```

Require current pool valuation:

```text
Status         == VERIFIED
TrackedBaseQty == source.BaseQty
TrackedValue   == source.TotalCost
```

No UNVALUED source.

---

# 16. Source contribution completeness

Because V1 moves the entire current pool, its current-generation receipt contribution ledger must fully explain the live quantity.

Use:

```text
ProductionContributionAvailabilityService.ReadAsync(
    db,
    source.Uid,
    sourcePoolValuation.Generation)
```

Require:

- at least one active current-generation positive receipt contribution;
- every receipt evidence status is VERIFIED;
- every available BaseQty is >= 0;
- sum of available BaseQty equals `source.BaseQty` exactly at `IvQty` precision.

Failure blocks WOT before mutation.

---

# 17. Target selection

Target is chosen from a compatible Work Order material requirement.

Require:

```text
TargetMaterial.WorkOrderId == TargetWorkOrderId
TargetMaterial.ComponentCode == Source.ItemCode
TargetMaterial.SupplySource == INTERNAL_ROUTE_WIP
TargetMaterial.ProducingRouteStepId != null
TargetMaterial.WorkOrderOperationId != null

TargetWorkOrderId != SourceWorkOrderId
```

Target consumer operation must still have remaining output:

```text
IvQty.Round(
    PlannedOutputQty - GoodQty) > 0
```

## CRITICAL V5 CORRECTION — target operation must already be sequence-eligible

The current repository's `ProductionOperationSequenceGate` deliberately ignores WIP availability when deciding whether a later operation may start.

It unlocks:

- a later stage only when every lower-stage predecessor operation is complete by posted `GoodQty`;
- a later process in the same route step only when lower process sequence has posted Good qty.

Therefore Work Order Transfer must **not** attempt to make a blocked target operation executable by changing Work Order operation quantities.

At target lookup, Save, and POST, load the complete target Work Order route/operation graph and call the existing:

```text
IProductionOperationEligibilityService.Evaluate(
    targetConsumerOperation,
    targetRouteSteps,
    targetOperations)
```

Require:

```text
eligibility.IsEligible == true
```

If false:

- exclude the target from the normal eligible lookup, or
- show it disabled with the exact existing `BlockingReason`.

POST must re-evaluate eligibility under the target Work Order execution lock.

This safeguard is mandatory.

It guarantees that after WOT posts, the target Daily Production operation can use the transferred WIP without:

- inventing producer GoodQty;
- updating `TransferredQty`;
- bypassing route sequence;
- changing Daily Production core logic;
- creating an execution projection that never really occurred.

If the business later requires "transfer WIP into a target and bypass unfinished predecessor operations", that is a separate Phase-2 execution-credit/bypass design and is **out of V1 scope**.

---

# 18. Separate producer and consumer contracts

Target material has two distinct meanings.

Producer contract:

```text
TargetMaterial.ProducingRouteStepId
```

This determines the live target WIP pool identity.

Consumer contract:

```text
TargetMaterial.WorkOrderOperationId
```

This explains where/why the material is needed.

Do not put the target consuming operation into live `ProductionBalLot.WorkOrderOperationId`.

Store consumer context on the WOT sidecar.

---

# 19. Target producer contract

Require producer route:

```text
ProducerRoute.WorkOrderId == TargetWorkOrderId
ProducerRoute.OutputType == WIP_STOCKED
ProducerRoute.OutputItemCode == Source.ItemCode
ProducerRoute.OutputUom == Source.Uom
ProducerRoute.OutputBaseUom == Source.BaseUom
ProducerRoute.OutputConversionFactorToBase == Source.ConversionFactorToBase
```

Find the producer route's valid final operation.

Require:

```text
ProducerOperation.RouteStepId == ProducerRoute.Uid
ProducerOperation.IsFinalOperation == true
```

---

# 20. Target consumer UOM contract

The target consumer material may legitimately use another required UOM.

Do **not** require:

```text
TargetMaterial.ConversionFactorToBase == Source.ConversionFactorToBase
```

Current Daily Production independently converts the BOM material quantity and the WIP lot quantity to base quantity.

Require:

```text
TargetMaterial.ComponentCode == Source.ItemCode
TargetMaterial.BaseUom == Source.BaseUom
TargetMaterial.ConversionFactorToBase > 0
TargetMaterial.RequiredUom not blank
```

Example: target producer WIP may be BOX while the consuming BOM line is PCS.

That is valid if both convert to the same base UOM.

---

# 21. Target live WIP identity

A newly created target WIP must have the same structural shape that normal target Daily Production would create:

```text
CompanyCode            = current company
BranchCode             = current branch
Kind                   = WIP
ItemCode               = source item
Description            = source description

WorkOrderId            = target WO
WorkOrderNo            = target WO no

ProducingRouteStepId   = target producer route
WorkOrderOperationId   = target producer final operation

BalanceStage           = PROCESS_WIP
OutputType             = WIP_STOCKED
StockStatusCode        = AVAILABLE
OriginType             = PRODUCED

WorkCentreCode         = producer route Work Centre
ProcessCode            = producer final operation code
ProductionLocationId   = location resolved from producer Work Centre

LotNo                  = source LotNo
PhysicalLotNo          = source.PhysicalLotNo ?? source.LotNo

PoolCode               = null
LotIdentityKind        = null

Uom                    = producer OutputUom
BaseUom                = producer OutputBaseUom
ConversionFactorToBase = producer factor

Qty                    = 0
BaseQty                = 0
TotalCost              = 0
AverageUnitCost        = 0
```

`ProductionStockWriter` performs the positive movement.

---

# 22. Target production location

Resolve the target location using the current Daily Production WorkCentre-to-ProductionLocation convention from the **producer route Work Centre**.

Do not use the consumer Work Centre for live target WIP identity.

Do not refactor current Daily Production solely to share this resolver.

---

# 23. Destination hard identity and collision gate

Resolve existing WIP using the current hard unique identity:

```text
CompanyCode
BranchCode
Kind == WIP
TargetWorkOrderId
TargetProducingRouteStepId
ItemCode
LotNo
```

If no row exists, create a zero natural target row.

If a row exists, lock it and require compatibility:

```text
Kind == WIP
BalanceStage == PROCESS_WIP
OutputType == WIP_STOCKED

WorkOrderId == target WO
ProducingRouteStepId == expected producer route
WorkOrderOperationId == expected producer operation

ItemCode == source item
LotNo == source lot
PhysicalLotNo null/blank or == source physical lot

Uom == source Uom
BaseUom == source BaseUom
ConversionFactorToBase == source factor

WorkCentreCode == expected producer WC
ProcessCode == expected producer process
ProductionLocationId == expected producer location

StockStatusCode == AVAILABLE
OriginType == PRODUCED
```

Do not rewrite an existing row's metadata to force a match.

Do not alter the current WIP unique indexes.

---

# 24. Existing target valuation/contribution gate

If existing target `BaseQty > 0`:

- scalar contract must pass;
- `ProductionCostReadiness.PoolValuationError` must be null;
- current valuation must be VERIFIED;
- current contribution availability must be complete;
- current available BaseQty sum must equal live target BaseQty.

If target is empty:

require exact zero:

```text
Qty == 0
BaseQty == 0
TotalCost == 0
AverageUnitCost == 0
```

An empty target may receive the new verified transfer generation.

---

# 25. Natural production compatibility

Two hard acceptance scenarios:

A. Natural target WIP already exists, then WOT merges into it.

B. WOT creates target WIP first, then normal target Daily Production later produces the same lot and finds/merges into the same row.

Both scenarios must retain:

- one target WIP row;
- correct target producer route;
- correct target producer operation;
- correct target production location;
- correct physical lot;
- correct value.

---

# 26. Document shell: IvTrxBatch

Add:

```csharp
public const string ProductionWorkOrderTransfer = "WOTR";
```

to `IvTrxTypes`.

Use `IvTrxBatch` for:

- BatchNo;
- TrxDtTime;
- TrxType;
- BatchStatus;
- RefNo;
- Remarks;
- PostedDate/By;
- PostedCount;
- PostingOperationId;
- Created/Modified metadata.

Status:

```text
NEW
POSTED
```

Do not duplicate status in the production sidecar.

Use `RunningNumberKeys.IvBatch`.

---

# 27. Work Order Transfer sidecar

Create:

```text
PrProductionWorkOrderTransfer
```

with one row per `IvTrxBatch`.

Suggested fields:

```text
BatchId PK/FK
CompanyCode
BranchCode
DocumentRevision
RowVersion

SourceWorkOrderId
SourceSnapshotRevision
SourceSnapshotHash

SourceProductionBalLotId
SourceBalanceRowVersion

SourceProducingRouteStepId
SourceProducerOperationId
SourceWorkCentreCode
SourceProcessCode
SourceProductionLocationId
SourcePoolCode

ItemCode
LotNo
PhysicalLotNo

Qty
Uom
BaseQty
BaseUom
ConversionFactorToBase
TransferTotalCost

TargetWorkOrderId
TargetSnapshotRevision
TargetSnapshotHash

TargetWorkOrderMaterialId

TargetProducingRouteStepId
TargetProducerOperationId
TargetProducerWorkCentreCode
TargetProducerProcessCode
TargetProducerProductionLocationId

TargetConsumingRouteStepId
TargetConsumingOperationId
TargetConsumingWorkCentreCode
TargetConsumingProcessCode

PostingId

DeletedAtUtc
DeletedBy
DeleteReason
```

---

# 28. Immutable posting fact

Create:

```text
PrProductionWorkOrderTransferFact
```

Suggested fields:

```text
Id
BatchId
StockPostingId

SourceProductionBalLotId
DestinationProductionBalLotId

TransferOutMovementId
TransferInMovementId

MovementGroupId

Qty
BaseQty
BaseUom
TotalValue
UnitCost

SourceGeneration
TargetGeneration

CreatedAtUtc
```

Unique:

```text
BatchId
StockPostingId
TransferOutMovementId
TransferInMovementId
MovementGroupId
```

Fact is append-only.

---

# 29. AppDbContext ownership guard

Add:

```csharp
public bool ProductionWorkOrderTransferWrite { get; set; }
```

Extend `ValidateProductionStockTransactionWrites()`.

If the capability is false, reject writes involving:

- `IvTrxBatch` whose current or original type is `WOTR`;
- `ProductionWorkOrderTransfer`;
- `ProductionWorkOrderTransferFact`.

Reject Modified/Deleted WOT fact even when service capability is true:

> Work Order Transfer posting evidence is immutable.

This prevents generic Inventory services from mutating WOT documents.

---

# 30. No IvTrxBatchDetail

V1 is one source pool -> one target pool.

Do not create a fake `IvTrxBatchDetail`.

The batch is only the lifecycle shell.

All WOT data belongs in the production sidecar/fact.

---

# 31. Duplicate NEW draft protection

Do not duplicate status solely for a filtered index.

For Save/Create:

1. begin transaction;
2. enable WOT write capability;
3. acquire branch stock lock;
4. lock source WIP;
5. query WOT sidecars joined to `IvTrxBatch`;
6. reject another non-deleted WOT on the same SourceProductionBalLotId with BatchStatus NEW.

The branch lock serializes concurrent WOT draft creation.

Add a normal lookup index on:

```text
CompanyCode
BranchCode
SourceProductionBalLotId
```

---

# 32. Save/Edit/Delete

Create:

- ADD permission;
- active source/target validation;
- branch lock;
- source lock;
- duplicate draft check;
- snapshot exact source qty/value/version;
- allocate IvBatch BatchNo;
- create WOTR NEW batch + sidecar;
- no stock movement.

Edit:

- NEW/unposted only;
- source identity immutable;
- target/date/ref/remarks editable;
- use RowVersion.

Refresh Source:

- NEW only;
- branch lock + source lock;
- re-run structural/scalar/cost/contribution gates;
- update saved source qty/baseqty/value/version.

Delete:

- NEW/unposted only;
- branch lock;
- RowVersion;
- delete sidecar + batch;
- POSTED cannot delete.

---

# 33. No rollback in V1

Do not implement or display rollback.

The immutable WOT fact is retained so a future controlled reversal can use V2 reversal semantics safely.

---

# 34. POST command / replay

Recommended command DTO:

```text
ProductionWorkOrderTransferCommand
(
    int Id,
    byte[] ExpectedVersion,
    Guid RequestId
)
```

Require non-empty RequestId.

Use StockPosting request fingerprinting.

Replay rules:

- same RequestId + same fingerprint + sealed posting -> return current WOT;
- same RequestId + different fingerprint -> reject;
- existing unsealed posting -> reconciliation required;
- never create another movement pair.

---

# 35. Mandatory lock order

Inside one DB transaction:

1. permission/scope;
2. enable WOT write capability;
3. acquire `IBranchStockTransactionLock`;
4. obtain source/target WO IDs from saved WOT identity;
5. acquire `ProductionWorkOrderExecutionLock` for source and target in ascending WorkOrderId order;
6. replay check / lock WOT batch/sidecar;
7. lock source/target Work Orders in ascending UID order;
8. lock source balance;
9. lock target material / producer route / producer operation / consumer operation;
10. resolve and lock/create target balance;
11. begin StockPosting;
12. create PostingLink PENDING;
13. apply OUT/IN;
14. valuation/dependency/fact/invariants;
15. mark success;
16. seal;
17. commit.

Catch `ProductionWorkOrderExecutionLockException` as concurrency.

---

# 36. Source POST revalidation

Under locks require:

- batch is NEW;
- document not deleted;
- WOT RowVersion matches request;
- source WO still RELEASED/IN_PROGRESS;
- source snapshot unchanged;
- source live balance RowVersion equals saved source balance rowversion;
- source identity unchanged;
- source Qty/BaseQty/TotalCost equal saved;
- scalar contract passes;
- structural contract passes;
- physical-lot gate passes;
- cost readiness passes;
- contribution completeness passes;
- effective date is not before later source events.

Any mismatch stops the POST.

---

# 37. Target POST revalidation

Under locks require:

- target WO still RELEASED/IN_PROGRESS;
- target snapshot revision/hash unchanged;
- material still INTERNAL_ROUTE_WIP;
- same item/base UOM;
- consumer operation still has remaining output;
- **consumer operation still passes `IProductionOperationEligibilityService.Evaluate(...)`;**
- complete target route/operation graph still matches the saved target context;
- producer route still WIP_STOCKED;
- producer route exact UOM chain still equals source WIP;
- producer final operation still valid;
- destination collision/cost/contribution rules pass;
- effective date is not before later target events.

If sequence eligibility changed after Save:

> Target operation is no longer eligible under the Work Order execution sequence. Reload the transfer and resolve the blocking predecessor first.

Do not POST WOT and leave unusable WIP behind a blocked operation.

---

# 38. StockPosting

Add constants:

```text
ProductionPostingCommandTypes.WorkOrderTransferPost = "WO_TRANSFER_POST"
ProductionDocumentTypes.WorkOrderTransfer = "WORK_ORDER_TRANSFER"
```

Command:

```text
RequestId          = request.RequestId
CommandType        = WO_TRANSFER_POST
SourceModule       = PRODUCTION
SourceDocumentType = WORK_ORDER_TRANSFER
SourceDocumentId   = BatchId
SourceDocumentNo   = BatchNo
DocumentRevision   = WOT DocumentRevision
PostingRole        = PRIMARY
EffectiveAt        = batch.TrxDtTime
Evidence           = full fingerprint
```

No legacy fallback.

---

# 39. PostingLink

Create during POST, following PRTN/PADJ style.

Recommended:

```text
WorkOrderId = TargetWorkOrderId

CommandType = WO_TRANSFER_POST
PostingRequestId = RequestId.ToString("N")
ProductionDocumentType = WORK_ORDER_TRANSFER
ProductionDocumentNo = BatchNo.ToString()
ProductionDocumentLineId = BatchId
InventoryBatchNo = BatchNo

SnapshotRevision = TargetSnapshotRevision
SnapshotHash = TargetSnapshotHash

Status = PENDING
```

Set `StockPosting.ProductionPostingLinkId`.

After all invariants, set link SUCCEEDED.

---

# 40. TRANSFER_OUT leg

Use `IProductionStockWriter`.

```text
Balance              = source
MovementType         = TRANSFER_OUT
Qty                  = full source Qty
BaseQty              = full source BaseQty
ExactTotalValue      = full source TotalCost

WorkOrderId          = source WO
WorkOrderMaterialId  = null
WorkOrderOperationId = source producer operation
RouteStepId          = source producer route

PostingLinkId        = link UID
DocumentType         = WORK_ORDER_TRANSFER
DocumentNo           = BatchNo
SourceLineId         = "{BatchId}:OUT"
SplitOrdinal         = 0
OriginalMovementId   = null
ValuationStatus      = VERIFIED
```

---

# 41. TRANSFER_IN leg

```text
Balance              = target natural WIP
MovementType         = TRANSFER_IN
Qty                  = exactly source Qty
BaseQty              = exactly source BaseQty
ExactTotalValue      = exactly source TotalCost

WorkOrderId          = target WO
WorkOrderMaterialId  = selected target material
WorkOrderOperationId = target producer final operation
RouteStepId          = target producer route

PostingLinkId        = link UID
DocumentType         = WORK_ORDER_TRANSFER
DocumentNo           = BatchNo
SourceLineId         = "{BatchId}:IN"
SplitOrdinal         = 0
OriginalMovementId   = null
ValuationStatus      = VERIFIED
```

Do not use the target consuming operation as the live WIP owner.

---

# 42. Movement pair invariants

Generate one GUID `MovementGroupId`.

`ProductionStockWriter` does not currently accept MovementGroupId on `ProductionStockLeg`, so the implementation must:

```text
1. Apply OUT/IN legs with ProductionStockWriter.
2. Identify the returned OUT and IN movements by stable SourceLineId.
3. Set the same MovementGroupId on both returned movement entities.
4. SaveChanges.
5. Only then run OUT valuation / IN transfer-authority valuation.
```

Do not start transfer valuation before the pair has a persisted MovementGroupId.

Set the same group on both movements.

Require:

```text
OUT.Qty       == IN.Qty
OUT.BaseQty   == IN.BaseQty
OUT.TotalCost == IN.TotalCost

OUT.ItemCode  == IN.ItemCode
OUT.BaseUom   == IN.BaseUom
OUT.ConversionFactorToBase == IN.ConversionFactorToBase

OUT.OriginalMovementId == null
IN.OriginalMovementId  == null
OUT.MovementGroupId == IN.MovementGroupId
```

Stamp movement WorkCentre/Process/ProductionLocation from source/target **producer** pool identity.

---

# 43. Exact final-depletion value

V1 transfers the full source.

Use:

```text
ExactTotalValue = source.TotalCost
```

Do not use `Qty * AverageUnitCost` as final authority.

After OUT:

```text
source.Qty             == 0
source.BaseQty         == 0
source.TotalCost       == 0
source.AverageUnitCost == 0
```

---

# 44. Extend current valuation directives narrowly

Extend:

```text
ProductionPoolValuationDirectives
```

with a property such as:

```csharp
IReadOnlyDictionary<long,long>
    VerifiedTransferSourceMovementByInboundMovementId
```

mapping:

```text
TRANSFER_IN UID -> paired TRANSFER_OUT UID
```

Do not overload PADJ manual-cost metadata.

---

# 45. Record OUT valuation first

Call:

```text
ProductionPoolValuationService.RecordAsync(
    context,
    [transferOut],
    ct)
```

first.

Then verify outbound allocation.

Load active `ProductionMovementAllocation` rows for this OUT.

Require:

```text
sum allocation BaseQty == transferOut.BaseQty
```

This is mandatory because source contribution completeness was already proven.

---

# 46. Record IN with transfer authority

Then call:

```text
RecordAsync(
    context,
    [transferIn],
    directives,
    ct)
```

The valuation service must validate:

```text
IN  == TRANSFER_IN
OUT == TRANSFER_OUT

both primary
same Company/Branch
same StockPosting
same non-null MovementGroup

same Item
same BaseUOM
same conversion
same BaseQty
same TotalCost

OUT valuation evidence exists
OUT evidence == VERIFIED
```

Then:

```text
IN evidence.Status = VERIFIED
IN evidence.Basis = "WORK_ORDER_TRANSFER:<OUT_UID>"
```

---

# 47. Target valuation generation

If target was empty:

TRANSFER_IN starts a new VERIFIED target generation.

If target was positive:

it had to be VERIFIED with complete contributions before merge; TRANSFER_IN joins the current verified generation.

Never use verified transfer-in to cleanse an existing UNVALUED positive pool.

---

# 48. Genealogy dependency

After OUT and IN evidence exists, insert exactly one:

```text
ProductionPoolDependency
ContributorMovementId = OUT.Uid
ConsumerMovementId    = IN.Uid
StockPostingId        = current posting
```

Do not use `OriginalMovementId` to pair normal transfer.

Do not create a fake OUT->IN `ProductionMovementAllocation`.

---

# 49. Post-merge target contribution invariant

After IN valuation:

read target current-generation contribution availability.

Require:

```text
all receipt evidence VERIFIED
sum available BaseQty == target live BaseQty
```

This proves target quantity genealogy is complete.

---

# 50. Immutable WOT fact / posting invariants

Create exactly one fact containing:

- source/target pool IDs;
- OUT/IN movement IDs;
- group ID;
- Qty/BaseQty;
- BaseUOM;
- TotalValue;
- UnitCost;
- source/target generations.

Before sealing assert:

1. one OUT;
2. one IN;
3. one WOT fact;
4. both movement evidence VERIFIED;
5. source live qty/value zero;
6. source tracked valuation zero;
7. target tracked valuation equals target live qty/value;
8. OUT allocations total exactly OUT BaseQty;
9. target contribution availability equals live BaseQty;
10. exactly one OUT->IN dependency;
11. aggregate source+target BaseQty conserved;
12. aggregate source+target TotalCost conserved.

Any mismatch rolls back.

---

# 51. No Inventory/Material side effects

For WOT StockPostingId require:

```text
IvTrxHistory count == 0
StockValuationFact count == 0
ProductionMaterialMovement count == 0
```

Relevant `IvBalLoc` quantities must remain unchanged.

`IvTrxBatch` is document lifecycle only.

---

# 52. Work Order projections must not change

Do not update either source or target:

## Work Order header
Status, GoodQty, ScrapQty, RejectQty, HoldQty, RemainingQty, snapshot, planning dates.

## Operations
InputQty, ProcessedQty, GoodQty, ScrapQty, RejectQty, HoldQty, ReworkQty, TransferredQty, RemainingQty.

## Materials
ReservedQty, PickedQty, IssuedQty, ReturnedQty, ConsumedQty, VarianceQty.

Do not reuse `ProductionWorkOrderOperation.TransferredQty`.

---

# 53. Audit events

Add additive event types, e.g.:

```text
WORK_ORDER_WIP_TRANSFER_OUT
WORK_ORDER_WIP_TRANSFER_IN
```

Source audit stores target WO, batch, source balance, OUT movement, group.

Target audit stores source WO, batch, target balance, target material, IN movement, group.

Audit only. No execution projection.

---

# 54. Successful lifecycle completion

After all invariants:

```text
link.Status = SUCCEEDED
link.CompletedDate = current/effective convention

sidecar.PostingId = posting.Id

batch.BatchStatus = POSTED
batch.PostedDate = date
batch.PostedBy = user
batch.PostedCount++
batch.PostingOperationId = RequestId
```

Save.

Then:

```text
StockPostingCoordinator.CompleteInTransactionAsync(...)
```

Require `SealedAtUtc != null`.

Commit.

---

# 55. No hidden posting-disable switch

Do not add an off-by-default WOT posting feature option.

Deployment control is:

- schema/preflight;
- POST permission;
- test gate;
- rollout procedure.

This avoids another UI where POST exists but is unexpectedly disabled.

---

# 56. Shared WIP compatibility

Extend only the current-generation provenance recognition in `ProductionOutputService.SharedWip.cs`.

Keep existing PRODUCE path unchanged.

Also accept WOT TRANSFER_IN only when:

- movement belongs to current lot;
- WorkOrderId matches;
- ProducingRouteStepId matches;
- WorkOrderOperationId matches target producer operation;
- item/BaseUOM/conversion match;
- posting sealed and active;
- MovementGroup exists;
- OUT->IN dependency exists;
- document type is WORK_ORDER_TRANSFER;
- evidence VERIFIED.

Do **not** weaken:

- current location rule;
- date rule;
- stock status;
- WIP_STOCKED;
- item/UOM rules;
- active source/route checks.

---

# 57. Transfer chaining

Must support:

```text
WO-A PRODUCE
 -> WOT1 -> WO-B
 -> WOT2 -> WO-C
 -> Daily Production consume
```

A WOT source current generation may contain PRODUCE, TRANSFER_IN, verified ADJUST_IN, or a mixture of verified current receipts.

General WOT source eligibility relies on VERIFIED pool + complete current-generation contribution availability, not on a PRODUCE-only rule.

---

# 58. Menu/UI

Add:

```text
MenuCodes.PlanningWorkOrderTransfer = "PLN_WO_TRANSFER"
```

Route:

```text
/planning/work-order-transfers
```

Planning -> Transactions.

Recommended sort:

```text
Stock Return       5
Stock Adjustment   6
Work Order Transfer 7
Machine Maintenance 8
```

Permissions:

```text
ACCESS
ADD
EDIT
DELETE
POST
VIEW_COST
```

No ROLLBACK.

No role grants in menu seed.

---

# 59. List UI

Follow current Production Stock Return/Adjustment list standard.

Columns:

- Batch;
- Date;
- Item;
- Lot;
- Qty/UOM;
- From Work Order;
- To Work Order;
- Target consuming Work Centre;
- Target consuming Process;
- Status.

NEW actions:

- VIEW;
- EDIT;
- POST;
- DELETE.

POSTED:

- VIEW.

No multi-post in V1.

---

# 60. Entry UI

Use a compact FROM -> TO design.

FROM lookup:

- searchable WIP lookup;
- do not require remembered WO numbers;
- show WO/item/lot/WC/process/qty.

TO lookup:

- compatible target Work Order material lookup;
- show target WO/product;
- target consumer WC/process;
- remaining requirement;
- target producer route/process as secondary detail.

Quantity:

**Full balance transfer** and read-only.

If source full balance exceeds target remaining requirement, warn clearly. Do not silently split/cap.

Cost fields visible only with VIEW_COST and always read-only.

Buttons:

NEW: SAVE, BACK.

Saved NEW: SAVE, POST, DELETE, BACK.

POSTED: BACK.

---

# 61. DevExpress binding guard

For explicit value/change callbacks, include the required expression.

Example:

```razor
<DxTextBox Text="@Remark"
           TextChanged="@OnRemarkChanged"
           TextExpression="@(() => Remark)" />
```

Use the equivalent expression for other DX editors.

No validation-expression runtime errors.


# 61A. Mandatory Production-module UI standard

This section is **mandatory implementation scope**, not optional polish.

The Work Order Transfer List and Entry pages must visually and behaviorally follow the current Production / Planning transaction UI already present in the repository.

Primary reference pages:

```text
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.css

ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor.css

ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor.css

ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor
ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentList.razor
```

The agent must **reuse the same visual language and layout patterns** rather than inventing a separate WOT style.

Required page-level patterns:

```text
iv-page
iv-hero
iv-card
iv-toolbar-row
iv-toast
common-popup
iv-popup-footer
iv-list-desktop
iv-list-compact
```

Entry pages must use a compact Production form/grid pattern equivalent to:

```text
pwo-form-grid
iv-field
pr-stock-meta
pr-stock-form
```

Do not copy CSS blindly if a shared class already provides the behavior. Create WOT-specific CSS only where needed.

---

# 61B. Lookup / dropdown / read-only decision rule

Every code field must have an explicit interaction rule.

## Rule 1 — User chooses the value

If the user must choose a Work Order, Item, Work Centre, Process, Material, Definition, Warehouse, Project, or similar code from existing ERP master/transaction data:

**do not use a free-text code textbox.**

Use one of:

- existing searchable picker component;
- searchable lookup popup/grid;
- `DxComboBox` when the eligible set is small enough;
- existing Large Lookup pattern when the candidate set can be large.

The user must not be required to remember exact codes.

## Rule 2 — System determines the value

If the value is already determined by the selected source WIP, target Work Order, target material, producer route, or process:

**do not give the user another dropdown.**

Display it as:

- compact read-only field;
- detail row;
- badge/chip;
- read-only code/description pair.

This prevents the UI from presenting choices that the backend does not actually allow.

## Rule 3 — Never decorative input

No field may look editable when it is ignored by posting logic.

Every editable control must map to a real saved field/business decision.

Every derived field must be clearly read-only.

---

# 61C. Required WOT field control types

Use these interaction rules unless the repo changes materially before implementation.

| WOT field | Required UI control |
|---|---|
| Source WIP / Source Work Order | Searchable WIP lookup/popup. User does not type WO manually. |
| Source Work Order No | Derived from selected WIP; read-only. |
| Source Item Code | Derived; read-only code-style field. |
| Source Description | Derived; read-only. |
| Source Lot | Derived; read-only. |
| Source Work Centre | Derived; read-only. |
| Source Process | Derived; read-only. |
| Source Qty / UOM | Derived; read-only. |
| Target Work Order | Searchable Work Order/material lookup, filtered to eligible active target WOs. |
| Target Material Requirement | Searchable lookup/popup filtered by source item + target WO + `INTERNAL_ROUTE_WIP`. |
| Target Item Code | Derived from material; read-only. |
| Target Consumer Work Centre | Derived; read-only. |
| Target Consumer Process | Derived; read-only. |
| Target Producer Work Centre | Derived; read-only secondary detail. |
| Target Producer Process | Derived; read-only secondary detail. |
| Effective Date | `DxDateEdit`. |
| Reference | `DxTextBox`, bounded semantic width. |
| Remarks | `DxMemo`, only field allowed to use a wider/full-row presentation when useful. |
| Status filter | `DxComboBox` in filter popup. |
| Transfer Qty | Read-only full-balance display; never editable. |
| UOM / Base UOM | Derived; compact read-only. |

If a future implementation introduces a genuinely user-selectable Work Centre/Process/Item field, it must use a lookup/dropdown — never free text.

---

# 61D. Mandatory semantic input widths

The agent must not use one generic full-width style for every control.

Use semantic width according to the actual data.

The current Production Stock pages provide these working repo conventions:

```text
Date:
max-width ~190px

Reference:
max-width ~320px

Quantity editor/display:
~112px where an editor is used

Large searchable lookup:
may use up to ~760px when lookup/search usability requires it

Page content:
current Production Stock entry pages use max-width ~1480px
```

For WOT use these additional semantic width targets:

```text
UOM / short status / short sequence:
90–120px

Work Centre / Process:
140–200px

Work Order No / Batch No:
160–220px

Item Code:
160–220px

Lot No:
160–240px

Description:
280–480px as available

Reference:
up to 320px

Date:
up to 190px

Remarks:
wide/full-row only when the content benefits from it
```

These are layout targets, not database constraints.

Do not force a 10-character Work Centre or Process field to span half or the whole page.

Do not use `width:100%` on a short code editor merely because the grid column is wide.

Use a bounded wrapper or semantic CSS class.

---

# 61E. Mandatory horizontal alignment and professional row layout

Desktop form layout must be visually aligned.

Use the Work Order pattern:

```css
display: grid;
grid-template-columns: repeat(4, minmax(0, 1fr));
gap: approximately 14px 16px;
```

or a WOT-specific equivalent consistent with the repo.

Mandatory rules:

1. Controls in the same visual row start on the same horizontal baseline.
2. Labels use one consistent label position and height.
3. Editors in the same row use consistent control height.
4. Validation/helper text must not arbitrarily push neighboring controls out of alignment.
5. Code fields use compact semantic widths.
6. Related fields share the same row.
7. Do not place every field on its own row on desktop.
8. Do not leave large empty horizontal gaps caused by arbitrary Bootstrap column spans.
9. Do not mix random `col-md-*`, fixed pixel widths and flex-grow rules without a deliberate grid.
10. Do not use ad-hoc inline CSS to repair one field at a time when a reusable WOT class solves the layout consistently.

Recommended desktop grouping:

```text
ROW 1
Effective Date | Reference | Status/read-only | Batch/read-only

SOURCE CARD
ROW 1
Source WO | Item Code | Lot | Qty/UOM

ROW 2
Source Work Centre | Source Process | Description (wider span)

TARGET CARD
ROW 1
Target WO lookup | Target Material lookup

ROW 2
Item Code | Consumer WC | Consumer Process | Requirement/remaining qty

ROW 3 (secondary/advanced)
Producer WC | Producer Process | Producer UOM | Base UOM

REMARK
Full row when needed
```

Do not expose internal IDs.

---

# 61F. Required responsive behavior

Follow the repository's existing Production responsive breakpoints.

Desktop:

```text
4-column form/meta grid where practical
```

Tablet around 900–980px:

```text
2-column grid
```

Mobile around 640–700px:

```text
1-column grid
command bars stack vertically
buttons wrap
lookups become full available width
no horizontal form clipping
```

Grid/list tables may use horizontal scrolling only where necessary.

Do not make desktop field widths so rigid that mobile breaks.

---

# 61G. WOT visual structure

Entry page must use a simple professional hierarchy.

Recommended:

```text
Hero
  Work Order Transfer
  Batch / Status chips

Command Bar
  LEFT: Back / Help if needed
  RIGHT: Save / Post / Delete according to status

Card: Transfer Details
  Effective Date
  Reference
  Remarks

Card: FROM — Source WIP
  searchable source lookup
  compact read-only source identity/detail grid

Directional visual
  FROM  →  TO
  keep subtle; do not create oversized decorative graphics

Card: TO — Target Requirement
  searchable target WO/material lookup
  compact consumer context
  secondary producer context

Card: Transfer Summary / Readiness
  Item
  Physical Lot
  Full Qty/UOM
  Value only when VIEW_COST
  readiness / blockers
```

Keep the page information-dense enough for ERP use, but not crowded.

Avoid giant hero cards or oversized empty spaces.

---

# 61H. Lookup popup/grid standard

Searchable popups must follow the current Production Stock source picker style.

Required:

- search box at top;
- 300–400ms debounce when server-backed;
- clear button;
- server-side paging for large candidate sets;
- no attempt to preload thousands of Work Orders/items;
- resizable/readable columns;
- description shown together with code where useful;
- clear READY / reason status when candidates can be ineligible;
- explicit SELECT/ADD action;
- close/done action;
- loading skeleton;
- useful empty-state text.

Source WIP popup suggested columns:

```text
WO
Item / Description
Lot
Current Qty
UOM
Work Centre
Process
Readiness
Action
```

Target popup suggested columns:

```text
Target WO
Product
Material Item
Remaining Requirement
Required UOM
Consumer Work Centre
Consumer Process
Producer Work Centre
Producer Process
Readiness
Action
```

No tiny truncated code columns.

Use the current grid readability standard and allow resize where applicable.

---

# 61I. Code + description display standard

For Work Order, Item, Work Centre and Process:

Do not show only an unexplained code when a description/name exists and is useful.

Preferred display:

```text
FG001
Finished Product ABC
```

or:

```text
WC01 · Cutting
```

depending on available model data.

Code should use `code-style`.

Description/name should be secondary text.

Do not repeat the same description in multiple adjacent fields.

---

# 61J. Read-only presentation rule

A derived value does not always need a disabled textbox.

Prefer the same current Production patterns:

- metadata grid;
- compact detail rows;
- read-only text editor only where form consistency benefits;
- chip/status display where appropriate.

Avoid a screen filled with grey disabled boxes.

The user should visually distinguish:

```text
SELECTABLE
EDITABLE
READ-ONLY SYSTEM RESULT
STATUS
```

without trial-and-error.

---

# 61K. Professional alignment acceptance gate

The AI coding agent must perform a visual/manual acceptance review at desktop and mobile sizes before marking implementation complete.

Desktop acceptance:

- all label rows align;
- input boxes align horizontally;
- no obviously overlong short-code fields;
- no random field widths;
- no large unused gaps;
- FROM and TO cards visually balance;
- buttons align;
- no clipped text in normal data;
- no truncated Work Order/Item/WC/Process codes at typical lengths.

Mobile acceptance:

- one clean column;
- no overlap;
- no horizontal editor overflow;
- buttons remain usable;
- lookup dialogs remain usable;
- labels do not collide with editors.

If the page is functionally correct but visually misaligned, the implementation is **not complete**.

---

# 61L. Mandatory UI regression / interaction tests

At minimum verify manually or with existing UI-test capability:

1. NEW button opens `/planning/work-order-transfers/new`.
2. Source lookup opens and searches.
3. User can select source without knowing WO number.
4. Target lookup is filtered after source selection.
5. User can select target without typing codes manually.
6. Derived Work Centre / Process / Item fields cannot be accidentally edited.
7. Changing source clears incompatible target selection.
8. Changing target refreshes all derived consumer/producer fields.
9. Effective Date editor uses proper date control.
10. Reference field stays compact.
11. Qty/UOM display stays compact.
12. Remarks is the only naturally wide text field.
13. Save/Post/Delete buttons follow document status.
14. POST confirmation clearly shows FROM, TO and full balance.
15. desktop horizontal alignment is visually clean.
16. tablet layout becomes two-column.
17. mobile layout becomes one-column.
18. no DevExpress missing-expression runtime exception.
19. no free-text Work Order/Item/WC/Process selector remains.
20. no nonfunctional toolbar action is visible.

---

# 61M. FINAL cross-transaction safety contract — IP / Daily Production / WIP / FG

This is a mandatory V5 release gate.

The Work Order Transfer feature is approved only if it remains an **isolated ownership move of PROCESS_WIP** and does not alter the existing transaction contracts.

## Transaction boundary matrix

| Existing transaction | Current authoritative effect | Required WOT interaction |
|---|---|---|
| Issue to Production (IP) | Inventory OUT + Production `MATERIAL_IN` ISSUE pool/movement | WOT must not read/write IP `MATERIAL_IN` as transfer source/destination. Branch stock lock serializes posting/rollback races. |
| Daily Production | Consumes MATERIAL_IN/WIP, posts `CONSUME`, creates/merges WIP/FG `PRODUCE`, updates WO/operation/material execution | WOT moves only existing stockable PROCESS_WIP. No GoodQty/ConsumedQty/TransferredQty changes. Target consumer must already be sequence-eligible. |
| WIP Balance | `ProductionBalLot` live Qty/BaseQty/TotalCost + sealed V2 movements | WOT OUT/IN must reconcile live quantity and value exactly and remain visible to stock history/reconciliation. |
| Finished Good Receipt | Takes only `FG_STAGING + FinishedGoods`, posts `FG_RECEIPT_OUT`, creates Inventory receipt | WOT must never select/convert FG_STAGING. FG Receipt remains unchanged. |
| Daily Production rollback | Reverses CONSUME/PRODUCE only when downstream dependency rules allow | WOT source OUT dependencies must block rollback of source PRODUCE when transferred WIP depends on it. |
| IP rollback | Reverses ISSUE only when downstream production/value dependencies are absent | Existing dependency chain must continue to block rollback after ISSUE -> Daily Production -> WOT downstream use. |

---

# 61N. IP isolation — mandatory

WOT source/destination must never be:

```text
Kind == MATERIAL_IN
BalanceStage == MATERIAL
OriginalIssueMovementId != null as MATERIAL_IN ownership identity
```

WOT must never:

- change `IvBalLoc`;
- change IP batch/detail;
- change `ProductionMaterialMovement` ISSUE/RETURN/CONSUME facts;
- change `ProductionWorkOrderMaterial.IssuedQty`;
- change `ReturnedQty`;
- change `ConsumedQty`;
- change IP draft basis.

IP and WOT both use the branch stock lock.

Mandatory SQL Server race tests:

```text
IP POST vs WOT POST
IP ROLLBACK vs WOT POST
```

There must be no mixed/partial balance state.

---

# 61O. Daily Production interaction — mandatory

WOT must not change:

```text
ProductionOutput
ProductionWorkOrderOperation.GoodQty
ProductionWorkOrderOperation.ProcessedQty
ProductionWorkOrderOperation.TransferredQty
ProductionWorkOrderMaterial.ConsumedQty
ProductionWorkOrder.GoodQty
ProductionWorkOrder.RemainingQty
```

Daily Production after WOT must continue using its existing logic:

```text
required material qty
 -> convert to BaseQty
 -> choose eligible target WIP lot
 -> derive lot Qty from lot conversion
 -> consume exact BaseQty/value
 -> create downstream PRODUCE
```

The transferred target pool must be indistinguishable from a naturally produced target WIP pool for this lookup/consume path.

---

# 61P. Source Daily Production rollback must be blocked after transfer

Current Daily Production rollback calls:

```text
ProductionPoolValuationService.HasActiveDependentsAsync(...)
```

for its original PRODUCE movements.

Current outbound valuation creates dependency edges from current inbound contributors to every negative movement.

Therefore when WOT records `TRANSFER_OUT`:

```text
source PRODUCE / current receipt
       |
       v
TRANSFER_OUT
       |
       v
TRANSFER_IN
```

must exist in the production dependency graph.

Mandatory test:

1. Post source Daily Production and create WIP.
2. Post WOT using that WIP.
3. Attempt rollback of the source Daily Production.
4. Rollback must fail with downstream pooled-value dependency / later consumption protection.
5. Source and target balances remain unchanged by the failed rollback.

Do not weaken Daily Production rollback checks.

---

# 61Q. Upstream IP rollback must remain blocked

Mandatory end-to-end lineage:

```text
Inventory lot
 -> IP ISSUE / MATERIAL_IN
 -> Daily Production CONSUME
 -> Daily Production PRODUCE WIP
 -> WOT TRANSFER_OUT
 -> WOT TRANSFER_IN
```

After WOT:

attempt rollback of the original IP.

Existing material/dependency rollback rules must return `InUse` / blocked.

No WOT-specific bypass is permitted.

This proves Work Order Transfer did not sever the source costing lineage.

---

# 61R. FG Receipt isolation — mandatory

Current FG Receipt source eligibility requires:

```text
BaseQty > 0
BalanceStage == FG_STAGING
StockStatusCode == AVAILABLE
OutputType == FINISHED_GOODS
final operation
terminal route stage
```

WOT V1 requires:

```text
BalanceStage == PROCESS_WIP
OutputType == WIP_STOCKED
```

Therefore WOT must never:

- select an FG_STAGING source;
- create an FG_STAGING target;
- create Inventory history;
- create Inventory valuation facts;
- update FG Receipt documents/facts;
- update Inventory BalLoc.

Mandatory test:

```text
WOT target PROCESS_WIP
 -> target Daily Production consumes it
 -> target Daily Production produces FG_STAGING
 -> normal FG Receipt receives that FG_STAGING
```

FG Receipt must work unchanged.

---

# 61S. WIP balance conservation — hard invariant

Before WOT capture:

```text
sourceBaseBefore
sourceValueBefore
targetBaseBefore
targetValueBefore
```

After sealed WOT:

```text
sourceBaseAfter = 0
sourceValueAfter = 0

targetBaseAfter =
    IvQty.Round(targetBaseBefore + sourceBaseBefore)

targetValueAfter =
    Money(targetValueBefore + sourceValueBefore)
```

Combined invariant:

```text
sourceBaseBefore + targetBaseBefore
==
sourceBaseAfter + targetBaseAfter

sourceValueBefore + targetValueBefore
==
sourceValueAfter + targetValueAfter
```

Also:

```text
source Qty == IvQty.Round(source BaseQty / source factor)
target Qty == IvQty.Round(target BaseQty / target factor)
```

No hidden quantity is allowed outside those two live WIP identities.

---

# 61T. Production stock ledger reconciliation — hard gate

Immediately after POST, run/assert the equivalent of current Production stock reconciliation for both source and target IDs.

For each lot:

```text
Live BaseQty
==
opening + sum(signed sealed V2 movement BaseQty)
```

WOT signs are already defined in `StockMovementRegistry`:

```text
TRANSFER_OUT = -1
TRANSFER_IN  = +1
```

Required:

- source ledger closes to zero after full transfer;
- target ledger closes to target live BaseQty;
- no `MissingLedger`;
- no `MissingLive`;
- no `QtyMismatch`.

The WOT tests must include this exact reconciliation, not only entity field assertions.

---

# 61U. Production value reconciliation — hard gate

For source and target pool valuation:

```text
ProductionPoolValuation.TrackedBaseQty == ProductionBalLot.BaseQty
ProductionPoolValuation.TrackedValue   == ProductionBalLot.TotalCost
```

After WOT:

source:

```text
TrackedBaseQty == 0
TrackedValue == 0
```

target:

```text
TrackedBaseQty == target.BaseQty
TrackedValue == target.TotalCost
Status == VERIFIED
```

No stranded value at zero quantity.

---

# 61V. Target Daily Production cost continuity

Mandatory test:

1. Source WIP has known VERIFIED total value.
2. WOT transfers the full source WIP.
3. Target Daily Production consumes some/all transferred WIP.
4. Daily Production's `CONSUME` movement receives the correct proportional/final-depletion value from the target pool.
5. Target output PRODUCE cost equals the existing Daily Production formula:
   - verified consumed production cost
   - plus current absorbed conversion cost where enabled.
6. WOT itself adds **zero** new production cost.
7. No duplicated source producer cost appears because WOT does not update target producer GoodQty.

This is the key cost non-regression test.

---

# 61W. Target sequence eligibility — mandatory release blocker

Test both cases.

## Case A — blocked target

Target consumer operation has an incomplete lower-stage/lower-process predecessor.

Expected:

```text
WOT target lookup = excluded/disabled
WOT Save/Post = blocked
no balance mutation
```

## Case B — eligible target

Target consumer operation already passes the exact current `IProductionOperationEligibilityService`.

Expected:

```text
WOT posts
target WIP becomes available
Daily Production can consume it using the unchanged sequence gate
```

Do not mark a predecessor producer operation GoodQty merely because WIP was transferred in.

---

# 61X. Complete end-to-end production acceptance scenario

This scenario is mandatory before implementation may be called complete:

```text
1. Create/Release source WO.
2. IP source materials.
3. Post source Daily Production.
4. Confirm source WIP balance/value VERIFIED.

5. Create/Release target WO.
6. Bring target execution sequence to the point where the target consuming operation is legitimately eligible.
7. Create WOT from source WIP to target compatible INTERNAL_ROUTE_WIP material.
8. POST WOT.

9. Confirm:
   source WIP BaseQty = 0
   target WIP BaseQty increased exactly
   combined WIP BaseQty unchanged
   combined WIP value unchanged
   source/target valuation reconciled
   stock history shows OUT/IN
   no Inventory balance change
   no WO execution quantity change

10. Post target Daily Production consuming transferred WIP.
11. Confirm consumed value is correct.
12. Confirm downstream output/FG_STAGING cost is correct.

13. Post Finished Good Receipt.
14. Confirm Inventory receives only from FG_STAGING.
15. Confirm FG quantity/value equals the current FG Receipt rules.

16. Attempt source Daily Production rollback after WOT.
    Expected: blocked.

17. Attempt upstream IP rollback while downstream chain remains active.
    Expected: blocked.

18. Run Production stock reconciliation.
    Expected: no ERROR findings for all involved ProductionBalLots.

19. Run full `dotnet test ErpWeb.slnx`.
```

If any step fails, WOT is **not approved for release**, even if its own isolated unit tests pass.

---

# 62. SQL deployment/preflight

Create:

```text
scripts/create-production-work-order-transfer.sql
scripts/preflight-production-work-order-transfer.sql
scripts/init-planning-work-order-transfer-menu.sql
```

Use `SET XACT_ABORT ON` and transaction-safe/idempotent patterns.

Preflight must verify:

- ProductionBalLot and WIP unique indexes;
- MovementGroupId;
- V2 StockPosting infrastructure;
- posting-line/reversal uniqueness;
- valuation evidence/pool/dependency/allocation tables;
- Work Order hierarchy/snapshot prerequisites;
- trusted/enabled required FKs/indexes.

Do not modify existing WIP unique indexes.

---

# 63. SQL Server acceptance

Using an explicitly disposable SQL Server database:

1. run create script;
2. rerun create script;
3. run preflight;
4. expect PASS;
5. intentionally break/disable a prerequisite;
6. verify atomic failure;
7. verify no partially disabled protection remains.

Follow the current Production Stock Transaction SQL Server test pattern.

---

# 64. Mandatory service tests

In addition to the tests below, sections **61M–61X are mandatory cross-transaction release tests** and cannot be replaced by mocks of IP/Daily Production/FG behavior.


Source:

- invalid statuses/snapshot;
- malformed producer identity;
- handoff/FG/MATERIAL_IN blocked;
- UOM roundtrip failure;
- average-cost mismatch;
- UNVALUED;
- contribution mismatch;
- PhysicalLotNo mismatch.

Target:

- same WO;
- invalid status/snapshot;
- wrong supply/item;
- wrong producer UOM;
- producer not WIP_STOCKED;
- missing producer final op;
- missing/completed consumer op;
- different consumer RequiredUom/factor accepted when same BaseUOM.

Destination:

- create natural target;
- compatible merge;
- metadata collision blocks;
- source PoolCode not copied;
- existing target PoolCode retained.

Posting:

- exact source zero;
- exact target qty/value;
- sealed posting;
- PostingLink success;
- immutable fact;
- no Inventory/material effects;
- no Work Order projections.

---

# 65. Mandatory valuation/genealogy tests

- OUT valuation VERIFIED;
- OUT allocations sum exactly transferred BaseQty;
- transfer directive exact pair validation;
- IN VERIFIED;
- basis references OUT;
- OUT->IN dependency exactly once;
- empty target starts verified generation;
- positive verified target merges;
- positive unvalued target blocks;
- target contribution ledger matches live BaseQty;
- no forward OriginalMovementId;
- transfer chaining;
- forward/backward lot genealogy across WOT.

---

# 66. Mandatory SQL Server concurrency tests

- duplicate NEW draft same source;
- same WOT double POST;
- WOT vs source Daily Production consume;
- WOT vs target Daily Production produce/consume;
- two WOTs into same target;
- held source execution lock;
- held target execution lock.

Required outcomes:

- no negative stock;
- no duplicate target WIP;
- no duplicate posting;
- no duplicate movement pair;
- exact value conservation.

---

# 67. Natural-production compatibility tests

A. WOT first, then target Daily Production creates same lot.

B. Natural target Daily Production first, then WOT same lot.

Both must use one target WIP pool and preserve natural producer metadata.

---

# 68. Full regression gate

Run:

```bash
dotnet test ErpWeb.slnx
```

Must preserve:

- Work Order;
- scheduling;
- Daily Production;
- Daily Production rollback;
- process handoff;
- Shared WIP;
- Material Issue;
- Production Stock Return;
- Production Stock Adjustment;
- FG Receipt;
- contribution availability;
- production valuation;
- stock history;
- genealogy;
- V2 ledger;
- SQL schema tests;
- period close.

No existing failure may be ignored without root-cause evidence.

Additionally, run the complete real cross-transaction chain from section 61X. Unit-test success alone is insufficient because WOT shares the same ProductionBalLot, StockPosting, valuation and branch-lock infrastructure as IP, Daily Production and FG Receipt.

---

# 69. Expected files

Add:

```text
ErpWeb.Model/Entities/Production/ProductionWorkOrderTransfer.cs
ErpWeb.Model/Configurations/Production/ProductionWorkOrderTransferConfiguration.cs

ErpWeb.Core/Production/IProductionWorkOrderTransferService.cs
ErpWeb.Core/Production/ProductionWorkOrderTransferService.cs

ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferList.razor
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferList.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferList.razor.css
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor.cs
ErpWeb.UI/Planning/WorkOrders/PrWorkOrderTransferEntry.razor.css

scripts/create-production-work-order-transfer.sql
scripts/preflight-production-work-order-transfer.sql
scripts/init-planning-work-order-transfer-menu.sql

focused WOT service / SQL Server / valuation / genealogy tests
```

Update:

```text
ErpWeb.Model/Data/AppDbContext.cs
ErpWeb.Core/Inventory/IvTrxConstants.cs
ErpWeb.Model/Entities/Production/ProductionDomainConstants.cs
ErpWeb.Core/CoreServiceCollectionExtensions.cs
ErpWeb.Core/Menus/MenuCodes.cs
ErpWeb.Core/Production/ProductionPoolValuationService.cs
ErpWeb.Core/Production/ProductionOutputService.SharedWip.cs
```

Possibly `LotGenealogyService.cs` only for a WOT edge label.

---

# 70. Files that require STOP/REVIEW before modification

Do not change these for WOT without a proven failing requirement:

```text
ProductionOutputService.Posting.cs
ProductionOutputService.Rollback.cs
ProductionProcessHandoff.cs

ProductionMaterialIssueService*
ProductionFinishedGoodReceiptService*

ProductionWorkOrderService*

ProductionBalLot unique-index definitions

Inventory FIFO/costing core
```

If WOT appears to require them, stop and review the design again.

---

# 71. Mandatory stop conditions

STOP instead of improvising if:

1. branch materially changed from reviewed current architecture;
2. ProductionBalLot is no longer authoritative;
3. WIP unique keys changed;
4. movement transfer semantics changed;
5. OriginalMovementId no longer represents reversal;
6. source pool is not VERIFIED;
7. source contribution availability does not fully reconcile;
8. source physical identity is non-standard;
9. source scalar/UOM contract fails;
10. target producer route cannot match source WIP exact producer-UOM contract;
11. target hard-UQ row conflicts with natural producer identity;
12. positive target is UNVALUED;
13. target contribution ledger is incomplete;
14. exact value cannot be conserved;
15. partial transfer becomes necessary;
16. item conversion becomes necessary;
17. Work Order execution quantities would need changes;
18. Daily Production posting core would need modification;
19. Shared-WIP location policy would need weakening;
20. Inventory stock would need movement;
21. existing WIP unique indexes would need alteration;
22. target consuming operation would require bypassing `ProductionOperationSequenceGate`;
23. implementation proposes changing producer GoodQty/TransferredQty to make transferred WIP usable;
24. IP / Daily Production / FG cross-transaction chain fails;
25. Production stock reconciliation reports QtyMismatch/MissingLedger/MissingLive after WOT;
26. full regression fails.

---

# 72. Final non-regression contract

A correct implementation guarantees:

```text
SAME ITEM
SAME PHYSICAL LOT

SOURCE BASE QTY + TARGET BASE QTY
BEFORE == AFTER

SOURCE TOTAL COST + TARGET TOTAL COST
BEFORE == AFTER

SOURCE VERIFIED CURRENT CONTRIBUTORS
 -> TRANSFER_OUT
 -> TRANSFER_IN
 -> TARGET VERIFIED CURRENT CONTRIBUTORS

TARGET WIP STRUCTURE
== TARGET NATURAL DAILY PRODUCTION WIP STRUCTURE

NO WORK ORDER EXECUTION PROJECTION CHANGE
NO DAILY PRODUCTION CORE CHANGE
NO INVENTORY BALANCE CHANGE
NO INVENTORY FIFO/GL CHANGE
NO VALUE DRIFT
NO GENEALOGY BREAK
```

---

# 73. Final scorecard

| Area | Score |
|---|---:|
| Current repo alignment | 10/10 |
| Current transaction lifecycle alignment | 10/10 |
| AppDbContext service ownership | 10/10 |
| Source structural validation | 10/10 |
| Source scalar/UOM validation | 10/10 |
| Contribution lineage completeness | 10/10 |
| Target producer identity | 10/10 |
| Producer vs consumer UOM separation | 10/10 |
| PoolCode safety | 10/10 |
| Destination collision safety | 10/10 |
| Quantity conservation | 10/10 |
| Cost conservation | 10/10 |
| Valuation authority | 10/10 |
| Genealogy | 10/10 |
| Transfer chaining | 10/10 |
| Concurrency | 10/10 |
| Idempotency | 10/10 |
| Natural Daily Production compatibility | 10/10 |
| Shared-WIP non-regression | 10/10 |
| Inventory isolation | 10/10 |
| Work Order non-impact | 10/10 |
| Target execution-sequence compatibility | 10/10 |
| IP non-regression | 10/10 |
| Daily Production non-regression | 10/10 |
| FG Receipt non-regression | 10/10 |
| WIP live/ledger quantity reconciliation | 10/10 |
| Cross-transaction cost continuity | 10/10 |
| SQL deployment/preflight | 10/10 |
| UI workability | 10/10 |
| Production UI standard / semantic widths / alignment | 10/10 |
| Regression coverage | 10/10 |

# FINAL SCORE: 10.0 / 10.0

# FINAL GATE: APPROVED FOR IMPLEMENTATION

---

# 74. Final AI Coding Agent instruction

Implement Work Order WIP Transfer as a first-class **V2 Production stock ownership transfer** using the current `productionv2` conventions.

Do not copy the OriginERP implementation mechanically.

Do not alter Daily Production core calculations.

Do not alter Work Order execution projections.

Do not bypass `ProductionOperationSequenceGate`. Target consumer operation must already be eligible.

Do not move Inventory stock.

Do not create cost.

Do not copy the source internal PoolCode into the target.

Do not weaken Shared WIP policy.

Do not invent a new UI style.

Follow the existing Production module List/Entry standards. User-selected Work Order/Item/Work Centre/Process/material codes must use searchable lookups/dropdowns where selection is truly required. System-derived codes must remain compact read-only values. Short fields must use semantic widths and all form rows must align professionally.

Use:

```text
ONE WOTR IvTrxBatch
ONE Production WOT sidecar
ONE immutable WOT fact

SOURCE VERIFIED WIP
  -> exact full-balance TRANSFER_OUT
  -> exact current contribution allocations

ONE MOVEMENT GROUP
ONE EXACT VALUE

TARGET NATURAL PRODUCER-POOL WIP
  -> VERIFIED TRANSFER_IN

ONE OUT -> IN DEPENDENCY

TARGET CONSUMER MATERIAL/PROCESS
  -> stored separately as business destination trace

BRANCH STOCK LOCK
+ SOURCE WO EXECUTION LOCK
+ TARGET WO EXECUTION LOCK

NO QUANTITY DRIFT
NO COST DRIFT
NO GENEALOGY BREAK
NO WORK ORDER EXECUTION CHANGE
NO INVENTORY CHANGE
```

If all of those cannot remain true together:

**STOP and report the conflict. Do not weaken the safeguard.**

---

**FINAL STATUS:** APPROVED FOR IMPLEMENTATION  
**FINAL SCORE:** 10.0 / 10.0  
**PLAN VERSION:** v5 — Final Cross-Transaction Safety Review + Mandatory Production UI Standard  
**BRANCH:** `productionv2`  
**REVIEWED COMMIT:** `f3d2d94634809559795d5a74649d4759c28baa78`
