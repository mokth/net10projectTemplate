# Production → Finished Good Cost Authority Hardening
## APPROVED Implementation Plan for Cursor / Grok 4.7

**Status:** APPROVED FOR IMPLEMENTATION  
**Review score:** 10.0 / 10  
**Repository:** `mokth/net10projectTemplate`  
**Branch reviewed:** `production`  
**Review date:** 2026-10-07  
**Primary objective:** Guarantee that the monetary value flowing through **Issue to Production → Daily Production → WIP/FG staging → Finished Good Receipt → Inventory** is derived from the authoritative V2 stock valuation and is conserved end-to-end.

---

## 1. Executive decision

Do **not** redesign Finished Good Receipt costing.

The current Finished Good Receipt implementation already has the correct value-transfer concept:

- source = verified `ProductionBalLot` in `FG_STAGING`;
- partial receipt transfers a proportional amount of the source pool value;
- final depletion transfers the exact remaining value;
- `IvTrxHistory.ExactTransferredValue` records the production value entering Inventory;
- the V2 inventory valuation engine then handles Moving Average, FIFO, or Standard Cost;
- Standard Cost additionally records `ProductionStandardCostVariance`.

The defect is **upstream of FG**.

The current Issue-to-Production posting sequence can create `ProductionBalLot` and production valuation evidence **before** the V2 inventory valuation engine has finalized the authoritative issue cost. The later V2 synchronization updates `ProductionMaterialMovement`, but it does not rebuild the already-created production pool/evidence.

There is also a concrete FIFO defect: `InventoryValuationService.SynchronizeProductionMaterialCostAsync()` assumes one valuation fact per `InventoryHistoryId` by calling `ToDictionary(...)`. A FIFO issue may have multiple valuation facts for one inventory history when it consumes more than one FIFO layer.

The approved solution is:

> **Finalize V2 inventory valuation inside the same transaction, but before sealing the posting and before creating the production balance lot/evidence. Then build the production pool from the finalized authoritative production material cost.**

This preserves the current architecture instead of introducing a second costing engine.

---

# 2. Verified current repository behavior

## 2.1 Finished Good value transfer is already correct

Relevant files:

- `ErpWeb.Core/Production/FinishedGoodReceiptMath.cs`
- `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.Posting.cs`
- `ErpWeb.Core/Production/ProductionStockWriter.cs`
- `ErpWeb.Core/Production/ProductionCostReadiness.cs`

Current FG logic:

```text
Production FG source pool
    BaseQty
    TotalCost
        |
        | partial:
        | transferred value = TotalCost × receipt base qty / source base qty
        |
        | final depletion:
        | transferred value = exact remaining TotalCost
        v
IvTrxHistory.ExactTransferredValue
        |
        v
V2 Inventory Valuation
```

`FinishedGoodReceiptMath.AllocateValue(...)` deliberately gives the final allocation the remaining value so rounding does not leave stranded production value.

`ProductionFinishedGoodReceiptService.Posting.cs` records:

- `ExactTransferredValue`
- `ValuationStatus = "VERIFIED"`
- `PriceEvidence = "FG_EXACT_BASE_CURRENCY"`

and removes the same exact value from the source production balance through `ProductionStockWriter`.

**Decision: keep this logic.**

---

## 2.2 Daily Production cost propagation is also conceptually correct

Relevant file:

- `ErpWeb.Core/Production/ProductionOutputService.Posting.cs`

Daily Production consumes production balance lots using:

```text
partial consumption:
    cost = lot.TotalCost × consumedBaseQty / lot.BaseQty

final depletion:
    cost = exact remaining lot.TotalCost
```

It adds every consumed material/WIP amount into:

```text
totalConsumedCost
```

and produces the next WIP or final FG staging pool using:

```text
Produced TotalCost = totalConsumedCost

Produced AverageUnitCost =
    Produced TotalCost / Produced BaseQty
```

Therefore, once the **input production pool is correct**, WIP → WIP → FG cost propagation is already value-conservative.

---

# 3. Confirmed defect 1 — IP creates the production pool before V2 costing is finalized

Relevant current path:

- `ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs`
- `ErpWeb.Core/StockLedger/InventoryValuationService.cs`
- `ErpWeb.Core/StockLedger/StockPostingCoordinator.cs`

Current sequence is effectively:

```text
1. Lock inventory balance
2. Copy IvBalLoc.UnitPrice into IP detail
3. Post inventory stock-out and create IvTrxHistory
4. Create ProductionMaterialMovement using history.UnitPrice
5. Create ProductionBalLot using that ProductionMaterialMovement cost
6. Create ProductionBalLotMovement
7. Record ProductionPoolValuation / ProductionValuationEvidence
8. Assert production pool is VERIFIED
9. Complete StockPosting
10. V2 InventoryValuationService finally calculates authoritative cost
11. SynchronizeProductionMaterialCostAsync updates ProductionMaterialMovement
12. StockPosting is sealed
```

The problem is steps 5–8 occur **before** step 10.

`InventoryValuationService.SynchronizeProductionMaterialCostAsync()` currently updates:

- `ProductionMaterialMovement.UnitCost`
- `ProductionMaterialMovement.TotalCost`

but does **not** update:

- `ProductionBalLot.TotalCost`
- `ProductionBalLot.AverageUnitCost`
- `ProductionBalLotMovement.UnitCost`
- `ProductionBalLotMovement.TotalCost`
- immutable `ProductionValuationEvidence`
- `ProductionPoolValuation.TrackedValue`

That means a production pool can remain valued at the old `IvBalLoc.UnitPrice` even though the authoritative V2 ledger later determines another value.

This is especially important when:

- Moving Average state differs from `IvBalLoc.UnitPrice`;
- FIFO consumes layers with costs different from `IvBalLoc.UnitPrice`;
- Standard Cost is active.

---

# 4. Confirmed defect 2 — FIFO split valuation facts can break synchronization

Current code in:

`ErpWeb.Core/StockLedger/InventoryValuationService.cs`

builds the production issue fact lookup approximately as:

```csharp
facts
    .Where(...)
    .ToDictionary(x => x.InventoryHistoryId!.Value);
```

That assumes:

```text
1 InventoryHistoryId = 1 StockValuationFact
```

This is not true for FIFO.

A single inventory issue history can consume:

```text
FIFO layer A
FIFO layer B
FIFO layer C
```

and therefore produce several `StockValuationFact` rows with the same `InventoryHistoryId`, differentiated by `SplitOrdinal`.

The synchronization must use:

```text
1 InventoryHistoryId
    -> N StockValuationFact rows
    -> SUM(BaseQty)
    -> SUM(CostAmount)
```

The aggregated monetary amount is what must flow into the production material movement/pool.

---

# 5. Authority rules after this change

These are mandatory architecture rules for the implementation.

## Rule A — V2 StockValuationFact is the inventory monetary authority

For Issue to Production, do not treat these as the final financial authority:

- `IvBalLoc.UnitPrice`
- `IvBalLoc.Cost`
- draft `IvTrxBatchDetail.UnitPrice`
- pre-valuation `IvTrxHistory.UnitPrice`

They can remain transaction/display/evidence fields, but the value entering Production must ultimately equal the V2 valuation facts created for that posting.

For an IP inventory history:

```text
Authoritative IP Base Qty
    = SUM(active StockValuationFact.BaseQty)

Authoritative IP Total Cost
    = SUM(active StockValuationFact.CostAmount)
```

for the current posting/history.

---

## Rule B — ProductionMaterialMovement receives the authoritative V2 value

After inventory valuation:

```text
ProductionMaterialMovement.TotalCost
    = authoritative V2 cost allocated to that production movement

ProductionMaterialMovement.UnitCost
    = TotalCost / BaseQty
```

No production pool may be created from the provisional pre-valuation cost.

---

## Rule C — ProductionBalLot must be created only after Rule B is complete

For MATERIAL_IN:

```text
ProductionBalLot.TotalCost
    = ProductionMaterialMovement.TotalCost

ProductionBalLot.AverageUnitCost
    = TotalCost / BaseQty
```

`ProductionBalLotMovement` for the ISSUE must carry the same quantity/value.

---

## Rule D — Production valuation evidence must prove V2 authority

An Issue movement must not be marked `VERIFIED` merely because:

```text
IvTrxHistory.UnitPrice != null
and
IvTrxHistory.PriceEvidence != blank
```

For V2 IP, verification must additionally prove that the production-side issue value equals the authoritative V2 stock valuation for its inventory history.

---

## Rule E — Daily Production remains pooled-cost based

Do not replace the existing Daily Production pooling logic.

Once the incoming MATERIAL_IN pool is authoritative, Daily Production should continue to consume:

```text
ProductionBalLot.TotalCost
```

and propagate it into produced WIP / FG staging.

---

## Rule F — FG remains exact value transfer

Do not recalculate FG from:

- item master cost;
- purchase price;
- manually entered price;
- `IvBalLoc.UnitPrice`.

FG continues to transfer the verified production pool value.

---

# 6. Approved implementation

## Phase 1 — Add a pre-seal V2 valuation operation

### File

`ErpWeb.Core/StockLedger/StockPostingCoordinator.cs`

### Interface change

Extend `IStockPostingCoordinator` with an explicit method such as:

```csharp
Task ValuePendingInTransactionAsync(
    StockPostingContext context,
    CancellationToken cancellationToken = default);
```

### Required behavior

Implementation:

```text
context must still be unsealed
        |
        v
IInventoryValuationService.ValuePendingAsync(context)
        |
        v
SaveChangesAsync
        |
        v
DO NOT seal StockPosting
DO NOT commit transaction
```

The method exists so a caller can finalize valuation facts **inside the same transaction before dependent production facts are created**.

### Refactor

`CompleteInTransactionAsync(...)` should reuse this method:

```text
ValuePendingInTransactionAsync(context)
Seal posting
Save
Clear DB write context
```

This preserves all current callers.

`ExecuteAsync(...)` may also use the common valuation helper before sealing so there is only one implementation of the valuation step.

### Important requirement

`ValuePendingInTransactionAsync()` must be idempotent for the same unsealed context.

The current `InventoryValuationService` already discovers histories that do not yet have valuation facts, so a later `CompleteInTransactionAsync()` should find no additional unvalued history and simply continue to sealing.

Do not create duplicate `StockValuationFact` rows.

---

# 7. Expose the pre-seal valuation through the Inventory posting abstraction

## Files

- `ErpWeb.Core/Inventory/IIvInventoryPostingService.cs`
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`

Add:

```csharp
Task ValuePostingInTransactionAsync(
    StockPostingContext? context,
    CancellationToken cancellationToken = default);
```

Recommended default interface behavior for a null/non-V2 context may remain no-op for compatibility, but **Production Material Issue already requires active V2**, so its actual call must have a non-null context.

Implementation delegates to:

```csharp
_coordinator.ValuePendingInTransactionAsync(context, cancellationToken)
```

Do not expose `IInventoryValuationService` directly to Production services. Keep the existing Stock Posting / Inventory Posting abstraction boundary.

---

# 8. Fix FIFO-safe production material synchronization

## File

`ErpWeb.Core/StockLedger/InventoryValuationService.cs`

Refactor:

`SynchronizeProductionMaterialCostAsync(...)`

### Current incorrect assumption

Do not use:

```csharp
ToDictionary(x => x.InventoryHistoryId)
```

for issue valuation facts.

### Required algorithm

Group the current posting's Production Material outbound facts:

```text
StockValuationFact
where:
    Direction < 0
    InventoryHistoryId != null
    MovementCode == PRODUCTION_MATERIAL_OUT
```

by:

```text
InventoryHistoryId
```

For every history group calculate:

```text
ValuedBaseQty = SUM(fact.BaseQty)
ValuedAmount  = SUM(fact.CostAmount)
```

Validate:

```text
all rows belong to the current posting
all rows are VALUED
all rows have the expected cost method
ValuedBaseQty > 0
ValuedAmount >= 0
```

Then locate the corresponding `ProductionMaterialMovement` row(s).

Validate:

```text
SUM(ProductionMaterialMovement.BaseQty)
    == ValuedBaseQty
```

using the repository's stock-ledger quantity precision.

Allocate `ValuedAmount` over production rows proportionally to `BaseQty`.

Use the existing cost-conservation pattern:

```text
for all rows except last:
    rounded proportional amount

last row:
    exact remaining amount
```

so:

```text
SUM(ProductionMaterialMovement.TotalCost)
    == SUM(StockValuationFact.CostAmount)
```

exactly.

Set:

```text
ProductionMaterialMovement.TotalCost
ProductionMaterialMovement.UnitCost
ProductionMaterialMovement.StockPostingId
ProductionMaterialMovement.SourceLineId
```

appropriately.

### FIFO lineage

For one history with several FIFO valuation facts:

```text
History
 ├─ Fact split 0
 ├─ Fact split 1
 └─ Fact split N
```

the production movement cost is the **sum** of those fact costs.

Do not select one FIFO split and ignore the others.

Do not average the FIFO layer unit costs first and multiply if that could lose the exact final residual. Sum the authoritative `CostAmount` facts and allocate their exact total.

---

# 9. Reorder Issue-to-Production posting

## File

`ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs`

### Current area

`PostDraftBatchAsync(...)`

### Approved new order

The posting must become:

```text
A. Validate / lock / create StockPosting context
B. Validate source inventory cost readiness
C. Post inventory stock-out
D. Persist IvTrxHistory
E. Create ProductionMaterialMovement lineage rows
   - cost may still be provisional at this exact point
F. Save ProductionMaterialMovement rows
G. PRE-SEAL V2 VALUATION
   _inventoryPosting.ValuePostingInTransactionAsync(...)
H. Reload/observe finalized ProductionMaterialMovement cost
I. Validate StockValuationFact ↔ ProductionMaterialMovement equality
J. Create MATERIAL_IN ProductionBalLot
K. Create ISSUE ProductionBalLotMovement
L. Record ProductionPoolValuation / ProductionValuationEvidence
M. Assert cross-ledger production invariants
N. Update Work Order/material projections/link
O. CompletePostingInTransactionAsync(...)
   - valuation is now an idempotent no-op
   - seal StockPosting
P. Commit transaction
```

### Critical prohibition

Do **not** call:

```text
CreateMaterialInLotsAsync(...)
```

before pre-seal V2 valuation has completed successfully.

That is the key fix.

---

# 10. Make CreateMaterialInLotsAsync consume finalized cost only

## File

`ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs`

Method:

`CreateMaterialInLotsAsync(...)`

It may continue to copy:

```csharp
TotalCost = movement.TotalCost
AverageUnitCost = movement.TotalCost / movement.BaseQty
```

provided it is only called **after** the pre-seal valuation.

Add a fail-closed guard before creating each lot.

At minimum prove:

```text
movement.StockPostingId == current StockPosting.Id
movement.InventoryHistoryId != null
movement.BaseQty > 0
movement.TotalCost >= 0
```

and that authoritative valuation exists for that history/posting.

Prefer centralizing the proof in a helper/invariant rather than duplicating large queries in this method.

---

# 11. Change production ISSUE valuation evidence to use V2 valuation facts

## File

`ErpWeb.Core/Production/ProductionPoolValuationService.cs`

Current ISSUE verification effectively trusts:

```text
IvTrxHistory.UnitPrice
IvTrxHistory.PriceEvidence
```

That is insufficient once V2 is active.

### Approved rule

For:

```text
movement.MovementType == ProductionBalLotMovementTypes.Issue
```

load the current posting's active/valued `StockValuationFact` rows for:

```text
movement.InventoryHistoryId
```

Aggregate:

```text
factQty   = SUM(BaseQty)
factValue = SUM(CostAmount)
```

Require:

```text
factQty == movement.BaseQty
factValue == movement.TotalCost
```

If not equal:

```text
status = UNVALUED
```

or fail immediately with `StockLedgerException` / `InvalidOperationException` before sealing.

For a valid issue:

```text
Status   = VERIFIED
Currency = COMPANY_BASE
Price    = movement.UnitCost
```

Use an explicit V2 basis, e.g.:

```text
STOCK_VALUATION:MOVING_AVERAGE
STOCK_VALUATION:FIFO
STOCK_VALUATION:STANDARD
```

The exact string may follow the repository's constant style, but it must identify the V2 authoritative source rather than copying legacy/display price evidence.

### Do not update existing immutable evidence after creation

`AppDbContext` already protects `ProductionValuationEvidence` from modification.

Therefore:

> create the evidence correctly once; never create it from provisional cost and patch it later.

---

# 12. Strengthen ProductionPostingInvariant

## File

`ErpWeb.Core/Production/ProductionPostingInvariant.cs`

Existing invariant proves:

- V2 movement identity;
- evidence exists;
- evidence says VERIFIED;
- pool tracked qty/value equals live production lot.

That is not enough because the live lot and projection can both agree with the same wrong provisional value.

Add a cross-ledger invariant for Issue movements.

For every IP `ProductionBalLotMovement`:

```text
movement.InventoryHistoryId
        |
        v
current StockPosting StockValuationFact rows
        |
        +-- SUM BaseQty
        +-- SUM CostAmount
```

Require:

```text
movement.BaseQty
    == SUM(StockValuationFact.BaseQty)

movement.TotalCost
    == SUM(StockValuationFact.CostAmount)

movement.UnitCost
    == movement.TotalCost / movement.BaseQty
```

Then require its production pool:

```text
ProductionBalLot.TotalCost
    == ProductionPoolValuation.TrackedValue
```

The combined proof is therefore:

```text
StockValuationFact
      ||
      || exact quantity/value
      \/
ProductionBalLotMovement
      ||
      \/
ProductionBalLot
      ||
      \/
ProductionPoolValuation
```

This cross-ledger equality is the missing safety guarantee.

---

# 13. Rollback requirements

## File to regression-test

`ErpWeb.Core/Production/ProductionMaterialIssueService.Rollback.cs`

No redesign is required unless tests expose an issue.

After the forward fix, original IP movements contain the authoritative value. Existing rollback uses the original:

```text
UnitCost
TotalCost
```

when generating:

- `ProductionMaterialMovement` reversal;
- `ProductionBalLotMovement` reversal.

This is desirable.

Regression requirements:

```text
Forward authoritative value = X

Rollback:
    inventory V2 reversal amount = X
    production reversal amount  = X
    MATERIAL_IN pool qty/value  = 0/0

Re-post:
    new forward posting obtains valuation from current valid V2 authority
```

Rollback must remain blocked when downstream production dependencies exist.

Do not weaken:

- pooled-value dependency checks;
- quantity dependency checks;
- chronology checks;
- V2 reversal semantics.

---

# 14. Finished Good Receipt changes

## Files reviewed

- `ErpWeb.Core/Production/FinishedGoodReceiptMath.cs`
- `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.Posting.cs`
- `ErpWeb.Core/Production/ProductionStockWriter.cs`
- `ErpWeb.Core/StockLedger/InventoryValuationService.cs`

### Functional change

**None expected to the FG value formula.**

The implementation agent should only modify FG code if a new regression test proves a defect.

Keep:

```text
source production pool verified
        ↓
exact/proportional transferred value
        ↓
IvTrxHistory.ExactTransferredValue
        ↓
InventoryValuationService
```

### Moving Average

FG receipt amount must be the exact production transfer amount.

`StockCostState` then recomputes:

```text
InventoryValue += FG Actual Production Value
OnHandBaseQty += FG Qty
AverageUnitCost = InventoryValue / OnHandBaseQty
```

### FIFO

FG actual production value becomes the cost of the new FIFO receipt layer.

### Standard

Inventory receives the current approved Standard Cost.

The difference between:

```text
ActualProductionValue
and
StandardInventoryValue
```

continues to be stored in:

`ProductionStandardCostVariance`

Do not remove this behavior.

---

# 15. Tests — mandatory

## 15.1 Extend `ProductionCostGateTests.cs`

File:

`ErpWeb.Tests/Production/Transaction/ProductionCostGateTests.cs`

Existing tests prove that production lineage is internally VERIFIED.

Add tests proving it equals the V2 inventory ledger.

### Test A — Moving Average authority beats IvBalLoc display price

Arrange:

```text
IvBalLoc.UnitPrice      = RM5.00
IvBalLoc.PriceEvidence  = nonblank

Authoritative StockCostState:
Qty                     = 10
InventoryValue          = RM20
AverageUnitCost         = RM2.00

IP Qty                  = 4
```

Expected:

```text
StockValuationFact      = RM8
ProductionMaterialMove  = RM8
ProductionBalLot        = RM8
ProductionBalLotMove    = RM8
Pool TrackedValue       = RM8
Evidence Price          = RM2
```

The test must specifically prove that `RM5 × 4 = RM20` is **not** allowed to leak into the production pool.

This test should fail against the current code and pass after the fix.

---

## 15.2 FIFO split-fact test

Use an active FIFO policy.

Create enough FIFO receipt layers that one IP history consumes at least two layers.

Example:

```text
Layer A: 2 units @ RM2 = RM4
Layer B: 5 units @ RM3 = RM15

IP issue: 4 units

FIFO cost:
2 @ RM2 = RM4
2 @ RM3 = RM6
Total   = RM10
```

Expected:

```text
StockValuationFact count for IP history >= 2
SUM fact BaseQty    = 4
SUM fact CostAmount = 10

ProductionMaterialMovement.TotalCost = 10
ProductionBalLot.TotalCost           = 10
ProductionBalLotMovement.TotalCost   = 10
ProductionPoolValuation.TrackedValue = 10
```

Also prove no duplicate-key exception occurs.

---

## 15.3 Standard Cost test

Use active Standard policy and approved `ItemStandardCostRevision`.

Example:

```text
IvBalLoc.UnitPrice   = RM2
Approved Standard    = RM6
IP Qty               = 4
```

Expected production issue value:

```text
4 × RM6 = RM24
```

Require:

```text
ProductionMaterialMovement.TotalCost = RM24
ProductionBalLot.TotalCost           = RM24
ProductionPoolValuation.TrackedValue = RM24
```

The stale `IvBalLoc.UnitPrice` must not override Standard Cost.

---

## 15.4 Valuation failure remains atomic

Existing test:

`Ip_stock_ledger_exception_is_a_controlled_rollback`

must remain green.

The new sequence should fail earlier, but the result must still be:

```text
inventory stock unchanged
batch remains NEW
no ProductionBalLot
no ProductionPoolValuation
no sealed StockPosting
transaction rolled back
```

---

## 15.5 Cross-ledger invariant corruption test

After valuation but before seal, deliberately alter a production movement/pool test value using the existing test hook or a focused test hook.

Expected:

```text
ProductionPostingInvariantException
whole transaction rollback
```

This proves internal self-reconciliation is no longer sufficient; the value must also equal V2 inventory valuation.

---

# 16. InventoryValuationService tests

File:

`ErpWeb.Tests/Inventory/Transaction/InventoryValuationServiceTests.cs`

Add a regression test around production material synchronization.

The test must create:

```text
one InventoryHistoryId
multiple FIFO StockValuationFact splits
one production issue monetary requirement
```

and prove synchronization uses:

```text
SUM(CostAmount)
```

rather than requiring one fact.

If a helper is extracted from `SynchronizeProductionMaterialCostAsync`, unit-test the helper's deterministic residual allocation as well.

---

# 17. New end-to-end production cost conservation test

Recommended new file:

`ErpWeb.Tests/Production/Transaction/ProductionCostConservationTests.cs`

Purpose: prove the whole production value chain.

Scenario:

```text
Authoritative inventory issue value = RM40
        |
        v
Issue to Production MATERIAL_IN = RM40
        |
        v
Daily Production consumes RM40
        |
        v
Final FG_STAGING = RM40
        |
        v
Finished Good Receipt transfers RM40
        |
        v
IvTrxHistory.ExactTransferredValue = RM40
```

For Moving Average / FIFO:

```text
FG inventory valuation fact receipt value = RM40
```

For Standard:

```text
ActualProductionValue = RM40

Inventory receives:
    FG Qty × effective standard cost

ProductionStandardCostVariance =
    ActualProductionValue - StandardInventoryValue
```

The test must also verify:

```text
no stranded value in zero-quantity production pools
```

---

# 18. Existing tests that must remain green

At minimum run/retain:

- `ProductionCostGateTests`
- `ProductionCostReadinessTests`
- `ProductionStockLedgerP0Tests`
- `ProductionStockLedgerPostingTests`
- `FinishedGoodReceiptTests`
- `InventoryValuationServiceTests`
- relevant `IvInventoryPostingServiceTests`

Do not weaken existing assertions to make the new implementation pass.

---

# 19. No database migration expected

The recommended design uses existing tables/columns:

- `StockPosting`
- `StockValuationFact`
- `StockCostState`
- `StockFifoLayer`
- `ProductionMaterialMovement`
- `ProductionBalLot`
- `ProductionBalLotMovement`
- `ProductionPoolValuation`
- `ProductionValuationEvidence`
- `ProductionFinishedGoodFact`
- `ProductionStandardCostVariance`

No new persisted field is required for this fix.

If the implementing agent believes a schema migration is necessary, it must stop and document the exact missing persisted fact before adding one. Do not add a column merely to work around posting order.

---

# 20. Files expected to change

Primary implementation files:

```text
ErpWeb.Core/StockLedger/StockPostingCoordinator.cs

ErpWeb.Core/Inventory/IIvInventoryPostingService.cs
ErpWeb.Core/Inventory/IvInventoryPostingService.cs

ErpWeb.Core/StockLedger/InventoryValuationService.cs

ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs
ErpWeb.Core/Production/ProductionPoolValuationService.cs
ErpWeb.Core/Production/ProductionPostingInvariant.cs
```

Tests:

```text
ErpWeb.Tests/Production/Transaction/ProductionCostGateTests.cs
ErpWeb.Tests/Inventory/Transaction/InventoryValuationServiceTests.cs
ErpWeb.Tests/Production/Transaction/ProductionCostConservationTests.cs   (recommended new)
```

Possible test-fixture update:

```text
ErpWeb.Tests/Production/Transaction/ProductionLedgerTestFixture.cs
```

FG production files are **not** expected to need functional changes unless a regression test demonstrates otherwise.

---

# 21. Explicit non-goals

This plan does **not** add actual shop-floor:

- labour time cost;
- machine running cost;
- setup cost;
- factory overhead absorption;
- subcontract actual cost.

The current Daily Production `totalConsumedCost` is primarily material/WIP value.

The repository already supports cost components in `ItemStandardCostRevision`:

```text
MaterialCost
LabourCost
MachineCost
OverheadCost
SubcontractCost
TotalStandardCost
```

Therefore Standard Cost can already value Inventory using those approved standard components and record production variance.

Actual labour/machine/overhead absorption is a separate manufacturing-costing enhancement and must not be mixed into this integrity fix.

---

# 22. Do-not-do list for the Code Agent

Do **not**:

1. recalculate FG from item master cost;
2. let users type FG cost;
3. make `IvBalLoc.UnitPrice` the authoritative production cost;
4. patch `ProductionValuationEvidence` after it has been created;
5. update historical production movement cost after the posting is sealed;
6. create duplicate valuation facts during pre-seal + complete calls;
7. collapse FIFO to a single arbitrary valuation fact;
8. lose the final rounding residual;
9. seal the StockPosting before production pool/evidence is complete;
10. weaken rollback dependency checks;
11. remove Standard Cost production variance;
12. add a schema migration without first proving it is required.

---

# 23. Implementation sequence for Cursor / Grok 4.7

Implement in this order.

### Step 1
Add `IStockPostingCoordinator.ValuePendingInTransactionAsync`.

Refactor `CompleteInTransactionAsync` to call it.

Run stock-ledger tests.

### Step 2
Expose valuation-only operation from `IIvInventoryPostingService` / `IvInventoryPostingService`.

Run inventory posting tests.

### Step 3
Make `SynchronizeProductionMaterialCostAsync` FIFO-safe by grouping valuation facts by history and aggregating exact value.

Add/execute FIFO regression test.

### Step 4
Reorder IP posting so valuation happens before `CreateMaterialInLotsAsync`.

Do not change FG yet.

### Step 5
Make `ProductionPoolValuationService` ISSUE verification use V2 valuation facts.

### Step 6
Strengthen `ProductionPostingInvariant` with StockValuationFact ↔ production movement value equality.

### Step 7
Add Moving Average, FIFO, Standard, atomicity, rollback and end-to-end cost-conservation tests.

### Step 8
Run the focused Production and Inventory suites.

### Step 9
Run full solution build and full test suite before marking complete.

---

# 24. Required validation commands

Use repository-standard commands where available.

At minimum:

```powershell
dotnet build ErpWeb.slnx
```

Focused tests:

```powershell
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~ProductionCostGateTests"
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~InventoryValuationServiceTests"
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~ProductionCostConservationTests"
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~FinishedGoodReceipt"
```

Then run the repository's grouped test runner / complete test suite according to the current `ErpWeb.Tests/run-tests.ps1` convention.

The implementation is not complete if only new tests pass while existing Production/Inventory posting tests fail.

---

# 25. Acceptance criteria

Implementation is APPROVED only when all are true.

## Inventory → Production

- [ ] IP cannot create a production pool before V2 valuation is finalized.
- [ ] Moving Average production cost equals V2 Moving Average issue value.
- [ ] FIFO production cost equals the sum of all FIFO split valuation facts.
- [ ] Standard production cost equals the applicable V2 Standard issue valuation.
- [ ] `ProductionMaterialMovement.TotalCost` equals authoritative V2 value.
- [ ] `ProductionBalLot.TotalCost` equals the finalized production material value.
- [ ] `ProductionBalLotMovement.TotalCost` equals the same value.
- [ ] `ProductionPoolValuation.TrackedValue` equals the live pool value.
- [ ] production issue evidence cannot be VERIFIED without V2 valuation proof.

## Daily Production

- [ ] consumption removes the exact/proportional verified source value.
- [ ] final depletion leaves no stranded value.
- [ ] produced WIP/FG staging receives exactly `totalConsumedCost`.

## Finished Good

- [ ] partial FG receipt transfers proportional verified production value.
- [ ] final FG receipt transfers exact remaining production value.
- [ ] production source qty/value reduce by the same amount.
- [ ] `IvTrxHistory.ExactTransferredValue` equals production transferred value.

## Inventory costing

- [ ] Moving Average updates inventory value/average from exact FG actual value.
- [ ] FIFO creates a receipt layer using FG production receipt value.
- [ ] Standard Cost books approved standard value and records actual-vs-standard variance.

## Rollback

- [ ] IP rollback reverses the exact authoritative forward value.
- [ ] FG rollback restores exact original production value.
- [ ] downstream-dependency blocking remains intact.
- [ ] re-post does not duplicate prior valuation/evidence.

## Atomicity

- [ ] any valuation/invariant failure rolls back the entire transaction.
- [ ] no orphan StockPosting, production pool, valuation evidence, or partial stock mutation remains.

---

# 26. Cost-conservation invariant to keep permanently

After implementation, the following should be treated as a permanent ERP accounting invariant:

```text
For IP:

Σ StockValuationFact.CostAmount
        ==
Σ ProductionMaterialMovement.TotalCost
        ==
Σ corresponding ProductionBalLotMovement.TotalCost
        ==
Production pool value introduced


For Daily Production:

Σ consumed production value
        ==
produced WIP / FG staging value


For FG Receipt:

production value removed
        ==
IvTrxHistory.ExactTransferredValue
        ==
actual production value entering inventory
```

For Standard Cost only:

```text
Actual Production Transfer
        =
Standard Inventory Receipt
        +
Production Variance
```

with the sign interpreted according to the existing `ProductionStandardCostVariance` convention.

---

# 27. Final assessment

### Finished Good Receipt logic

**Current design:** strong and should remain.

### Production costing architecture before this fix

The V2 ledger is authoritative, but Issue-to-Production production-pool creation is currently ordered too early and FIFO synchronization assumes a single valuation fact per history.

### Architecture after this plan

```text
Inventory
  |
  | V2 authoritative valuation
  | Moving Average / FIFO / Standard
  v
StockValuationFact
  |
  | exact synchronized value
  v
ProductionMaterialMovement
  |
  v
ProductionBalLot (MATERIAL_IN)
  |
  v
Daily Production
  |
  v
WIP / FG_STAGING
  |
  | exact production value transfer
  v
Finished Good Receipt
  |
  v
Inventory V2 Valuation
```

This gives one monetary authority and preserves quantity/value lineage across the complete production cycle.

---

## APPROVAL

**Status: APPROVED FOR CURSOR / GROK 4.7 IMPLEMENTATION**

**Score: 10.0 / 10**

The score is based on the current `production` branch behavior verified in the source files and existing Production/Inventory tests listed above. The plan deliberately avoids an unnecessary database migration and preserves the existing Finished Good, rollback, pooled valuation, FIFO, Moving Average, Standard Cost, and variance architectures.
