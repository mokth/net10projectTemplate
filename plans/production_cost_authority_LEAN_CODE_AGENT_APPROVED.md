# Production Cost Authority Hardening
## LEAN CODE-AGENT PLAN — APPROVED

**Repo:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Status:** APPROVED  
**Target:** Cursor / Grok 4.7  
**Scope:** IP → Daily Production → FG → Inventory costing authority

---

# 1. Objective

Make V2 Inventory valuation the only monetary authority for Production.

Required value chain:

```text
StockValuationFact
=
ProductionMaterialMovement
=
ProductionBalLotMovement
=
ProductionBalLot / ProductionPoolValuation
=
Daily Production propagated value
=
FG ExactTransferredValue
```

For Standard Cost:

```text
Actual Production Value
=
Standard Inventory Value
+
ProductionStandardCostVariance
```

---

# 2. Confirmed Defects

## D1 — IP creates Production pool before V2 valuation

Current file:

```text
ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs
```

Current Production pool/evidence is created before:

```text
InventoryValuationService.ValuePendingAsync(...)
```

Fix: V2 valuation must happen before `CreateMaterialInLotsAsync()`.

---

## D2 — FIFO synchronization assumes one fact per history

Current file:

```text
ErpWeb.Core/StockLedger/InventoryValuationService.cs
```

Current `SynchronizeProductionMaterialCostAsync()` uses one `StockValuationFact` per `InventoryHistoryId`.

FIFO can create:

```text
1 InventoryHistoryId
→ N StockValuationFact rows
```

Fix: group and sum by `InventoryHistoryId`.

---

## D3 — Production money precision is too low

V2 ledger:

```text
money = decimal(19,6)
```

Production authority fields are mainly:

```text
decimal(18,4)
```

Fix authority money fields to `decimal(19,6)`.

Keep quantity precision at 4 decimals.

---

## D4 — ProductionPostingLinkId assigned too late

Before pre-seal valuation:

```csharp
ledgerContext.Posting.ProductionPostingLinkId = link.Uid;
```

must already be set.

---

## D5 — FIFO FG does not use ExactTransferredValue

Moving Average uses `ExactTransferredValue`.

FIFO currently reconstructs FG receipt cost from rounded unit price.

Fix FIFO receipt to use exact transferred value when present.

---

# 3. Do Not Change

Do NOT change general Inventory costing rules:

```text
Moving Average formula
FIFO issue order
Standard Cost formula
GR
MR
MI
TR
ADJ
CR
VR
Scrap
```

Do NOT add actual labour/machine/overhead absorption.

---

# 4. Precision Rule

Quantity:

```text
IvQty.Round(...)
4 decimals
```

Money:

```text
StockLedgerPrecision.Money(...)
6 decimals
```

Do NOT use `IvQty.Round` for authoritative cost/value.

---

# 5. Database Change

Create:

```text
scripts/alter-production-cost-money-precision-v2.sql
```

Alter to `decimal(19,6)`:

```text
PrMaterialMovement.UnitCost
PrMaterialMovement.TotalCost

PrProductionBalLot.TotalCost
PrProductionBalLot.AverageUnitCost

PrProductionBalLotMovement.UnitCost
PrProductionBalLotMovement.TotalCost

PrFinishedGoodFact.TotalValue
PrPoolValuation.TrackedValue
PrValuationEvidence.Price

IvTrxHistory.ExactTransferredValue
```

Do NOT change:

```text
IvBalLoc.UnitPrice
IvBalLoc.Cost
IvTrxHistory.UnitPrice
PrFinishedGoodPriceSnapshot.*
```

Script requirements:

```text
transactional
idempotent
preflight existing values
fail on overflow
```

Update fresh-install SQL:

```text
scripts/create-production-material-issue.sql
scripts/create-production-daily-output.sql
scripts/create-finished-good-receipt.sql
scripts/create-stock-valuation-ledger.sql
```

---

# 6. EF Precision Changes

Update:

```text
ProductionMaterialMovementConfiguration.cs
    UnitCost       19,6
    TotalCost      19,6

ProductionBalLotConfiguration.cs
    TotalCost        19,6
    AverageUnitCost  19,6

ProductionBalLotMovementConfiguration.cs
    UnitCost       19,6
    TotalCost      19,6

ProductionFinishedGoodReceiptConfiguration.cs
    ProductionFinishedGoodFact.TotalValue    19,6
    ProductionPoolValuation.TrackedValue     19,6
    ProductionValuationEvidence.Price        19,6

IvTrxHistoryConfiguration.cs
    ExactTransferredValue                    19,6
```

---

# 7. Add Pre-Seal Valuation API

File:

```text
ErpWeb.Core/StockLedger/StockPostingCoordinator.cs
```

Add:

```csharp
Task ValuePendingInTransactionAsync(
    StockPostingContext context,
    CancellationToken cancellationToken = default);
```

Implementation:

```text
EnsureUnsealed
valuation required
ValuePendingAsync
SaveChanges
```

Must NOT:

```text
seal
commit
clear DB session context
```

Do NOT redesign `ExecuteAsync`.

---

# 8. Expose Through Inventory Posting Service

Files:

```text
IIvInventoryPostingService.cs
IvInventoryPostingService.cs
```

Add:

```csharp
Task ValuePostingInTransactionAsync(
    StockPostingContext context,
    CancellationToken cancellationToken = default);
```

Delegate to coordinator.

Fail if coordinator unavailable.

No silent no-op.

---

# 9. Reorder IP Posting

File:

```text
ProductionMaterialIssueService.Lifecycle.cs
```

Required order:

```text
1. validate draft/work order/material/period

2. BeginPostingInTransactionAsync

3. ledgerContext.Posting.ProductionPostingLinkId = link.Uid

4. lock source inventory

5. post physical stock-out

6. save IvTrxHistory

7. create ProductionMaterialMovement
   UnitCost = 0
   TotalCost = 0
   save row
   stamp:
       StockPostingId
       SourceLineId = InventoryBatchDetailId
       SplitOrdinal = 0

8. call ValuePostingInTransactionAsync

9. verify ProductionMaterialMovement now equals V2 valuation

10. CreateMaterialInLotsAsync

11. create/stamp ProductionBalLotMovement

12. ProductionPoolValuationService.RecordAsync

13. ProductionPostingInvariant.AssertIssueVerifiedAsync

14. update Work Order/material/link/audit

15. CompletePostingInTransactionAsync

16. AssertSealed

17. commit
```

Hard rule:

```text
CreateMaterialInLotsAsync()
MUST NOT execute before V2 valuation.
```

---

# 10. Fix Production Material Synchronization

File:

```text
InventoryValuationService.cs
```

Method:

```text
SynchronizeProductionMaterialCostAsync
```

Use:

```text
facts
where:
    Direction < 0
    InventoryHistoryId != null
    MovementCode == PRODUCTION_MATERIAL_OUT
    ValuationStatus == VALUED
group by InventoryHistoryId
```

For each history:

```text
FactQty   = SUM(BaseQty)
FactValue = SUM(CostAmount)
```

Require exactly:

```text
1 ProductionMaterialMovement
```

for that `InventoryHistoryId`.

If count != 1:

```text
throw LedgerMismatch
```

Require:

```text
FactQty == movement.BaseQty
```

Set:

```csharp
movement.TotalCost = Money(FactValue);
movement.UnitCost  = Money(movement.TotalCost / movement.BaseQty);
movement.StockPostingId = current posting id;
```

---

# 11. FIFO Lineage

One FIFO issue:

```text
InventoryHistory
  ├─ Fact split 0
  ├─ Fact split 1
  └─ Fact split N
```

All facts must link to the same:

```text
ProductionMaterialMovement.Uid
```

Set for every fact:

```text
ProductionMovementId
WorkOrderId
WorkOrderOperationId
```

Do NOT copy one FIFO fact's:

```text
SourceLineId
SplitOrdinal
```

into ProductionMaterialMovement.

Production identity remains:

```text
SourceLineId = InventoryBatchDetailId
SplitOrdinal = 0
```

---

# 12. Split Issue Stamping

Current:

```text
StampIssueLedgerFactsAsync
```

Refactor responsibilities.

Before valuation:

```text
stamp ProductionMaterialMovement V2 identity
```

After pool creation:

```text
stamp ProductionBalLotMovement V2 identity
```

Do not overwrite already-valued ProductionMaterialMovement.

---

# 13. CreateMaterialInLotsAsync Guard

Before lot creation require:

```text
movement.StockPostingId == context.Posting.Id
movement.InventoryHistoryId != null
movement.BaseQty > 0
movement.TotalCost >= 0
```

Load authoritative facts for:

```text
current posting
same InventoryHistoryId
PRODUCTION_MATERIAL_OUT
VALUED
```

Require:

```text
SUM(BaseQty) == movement.BaseQty
SUM(CostAmount) == movement.TotalCost
```

Then:

```text
ProductionBalLot.TotalCost = movement.TotalCost

ProductionBalLot.AverageUnitCost =
Money(TotalCost / BaseQty)
```

Matching ISSUE balance movement must carry same value.

---

# 14. ProductionPoolValuationService

File:

```text
ProductionPoolValuationService.cs
```

For ISSUE:

Do NOT verify using:

```text
IvTrxHistory.UnitPrice
IvTrxHistory.PriceEvidence
```

Use V2 facts.

Require:

```text
StockPostingId == current posting
InventoryHistoryId == movement.InventoryHistoryId
Direction < 0
MovementCode == PRODUCTION_MATERIAL_OUT
ValuationStatus == VALUED
```

Then:

```text
SUM(BaseQty) == movement.BaseQty
SUM(CostAmount) == movement.TotalCost
```

If mismatch:

```text
throw
```

Success evidence:

```text
Status   = VERIFIED
Currency = COMPANY_BASE
Price    = Money(TotalCost / BaseQty)
Basis    = STOCK_VALUATION:<CostMethod>
```

---

# 15. ProductionPostingInvariant

File:

```text
ProductionPostingInvariant.cs
```

For every IP ISSUE:

Require:

```text
SUM(V2 fact BaseQty)
=
movement.BaseQty

SUM(V2 fact CostAmount)
=
movement.TotalCost

movement.UnitCost
=
Money(TotalCost / BaseQty)
```

Keep existing pool reconciliation:

```text
Pool.TrackedBaseQty == Lot.BaseQty
Pool.TrackedValue == Lot.TotalCost
```

---

# 16. Production Money Rounding

Update money calculations in:

```text
ProductionOutputService.Posting.cs
ProductionStockWriter.cs
FinishedGoodReceiptMath.cs
```

Use `StockLedgerPrecision.Money`.

Money includes:

```text
TotalCost
UnitCost
AverageUnitCost
consumed value
produced value
transferred value
remaining value
```

Keep quantity on `IvQty.Round`.

---

# 17. FIFO FG Exact Value

File:

```text
InventoryValuationService.cs
```

In FIFO receipt path:

If:

```csharp
history.ExactTransferredValue is decimal exact
```

use:

```text
forcedAmount = Money(abs(exact))
forcedUnit   = Money(forcedAmount / receiptQty)
source       = ProductionActual
```

FIFO valuation fact:

```text
CostAmount = forcedAmount
```

FIFO layer value:

```text
OriginalValue = forcedAmount
RemainingValue = forcedAmount
```

Do NOT use rounded `history.UnitPrice` when exact value exists.

Ordinary FIFO receipt behavior remains unchanged.

---

# 18. FG Transfer Rule

Partial FG:

```text
value =
Money(
    source.TotalCost
    × receiptBaseQty
    / source.BaseQty)
```

Final depletion:

```text
value = exact remaining source.TotalCost
```

Last allocation receives residual.

Store:

```text
IvTrxHistory.ExactTransferredValue
```

at 6 decimals.

---

# 19. Standard Cost

Do NOT change formula.

Inventory:

```text
FG Qty × approved Standard Cost
```

Actual Production:

```text
ExactTransferredValue
```

Variance:

```text
Actual - Standard
```

Keep:

```text
ProductionStandardCostVariance
```

---

# 20. Test Fixture

File:

```text
ProductionLedgerTestFixture.cs
```

For valuation-aware tests default to:

```csharp
valuation ?? new InventoryValuationService()
```

If no-valuation behavior is required, create explicit helper:

```text
CreateCoordinatorWithoutValuation
```

---

# 21. Mandatory Tests

## T1 Moving Average authority

```text
IvBalLoc price = 5

StockCostState:
Qty 10
Value 20
Average 2

IP Qty 4
```

Expected:

```text
V2 cost = 8
ProductionMaterialMovement = 8
ProductionBalLotMovement = 8
ProductionBalLot = 8
Pool = 8
```

---

## T2 6-decimal preservation

```text
Qty = 3
Value = 1
Average = 0.333333

Issue = 1
```

Expected after DB reload:

```text
0.333333
```

Not:

```text
0.3333
```

---

## T3 FIFO multi-layer IP

```text
Layer A: 2 @ 2
Layer B: 5 @ 3

Issue 4
```

Expected:

```text
Fact A = 4
Fact B = 6

Total = 10

one ProductionMaterialMovement
Production value = 10

all facts point to same ProductionMovementId
```

---

## T4 Standard IP

```text
IvBalLoc price = 2
Standard = 6
Qty = 4
```

Expected:

```text
V2 = 24
Production = 24
```

---

## T5 FIFO FG exact value

```text
FG Qty = 3
ExactTransferredValue = 1.000001
display price = 0.3333
```

Expected:

```text
FIFO fact value = 1.000001
FIFO layer value = 1.000001
```

Not:

```text
0.9999
```

---

## T6 Pre-seal idempotency

Call:

```text
ValuePostingInTransactionAsync
CompletePostingInTransactionAsync
```

Require:

```text
no duplicate facts
no duplicate FIFO consumption
no double StockCostState mutation
```

---

## T7 Posting lineage

Before valuation:

```text
Posting.ProductionPostingLinkId == link.Uid
```

After valuation:

```text
StockValuationFact.ProductionPostingLinkId == link.Uid
```

---

## T8 Atomic rollback

Force cross-ledger mismatch.

Require rollback of:

```text
IvBalLoc
StockCostState
FIFO layers
Production rows
StockPosting seal
```

Batch remains NEW.

---

## T9 End-to-end

Test:

```text
IP
→ Production MATERIAL_IN
→ Daily
→ FG_STAGING
→ FG Receipt
→ Inventory
```

MA/FIFO:

```text
IP value
=
Production value
=
FG ExactTransferredValue
=
FG Inventory valuation
```

Standard:

```text
Actual = ExactTransferredValue
Variance = Actual - Standard
```

---

# 22. SQL Server Test

SQLite is not enough for decimal scale.

Add/extend:

```text
ProductionStockLedgerSqlServerTests.cs
```

Verify:

```text
target money column scale = 6
```

Persist and reload a 6-decimal value.

---

# 23. Expected Files

Core:

```text
ErpWeb.Core/StockLedger/StockPostingCoordinator.cs
ErpWeb.Core/StockLedger/InventoryValuationService.cs

ErpWeb.Core/Inventory/IIvInventoryPostingService.cs
ErpWeb.Core/Inventory/IvInventoryPostingService.cs

ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs
ErpWeb.Core/Production/ProductionPoolValuationService.cs
ErpWeb.Core/Production/ProductionPostingInvariant.cs
ErpWeb.Core/Production/ProductionOutputService.Posting.cs
ErpWeb.Core/Production/ProductionStockWriter.cs
ErpWeb.Core/Production/FinishedGoodReceiptMath.cs
```

Model:

```text
ProductionMaterialMovementConfiguration.cs
ProductionBalLotConfiguration.cs
ProductionBalLotMovementConfiguration.cs
ProductionFinishedGoodReceiptConfiguration.cs
IvTrxHistoryConfiguration.cs
```

SQL:

```text
alter-production-cost-money-precision-v2.sql
create-production-material-issue.sql
create-production-daily-output.sql
create-finished-good-receipt.sql
create-stock-valuation-ledger.sql
```

Tests:

```text
ProductionLedgerTestFixture.cs
ProductionCostGateTests.cs
InventoryValuationServiceTests.cs
FinishedGoodReceiptTests.cs
ProductionCostConservationTests.cs
ProductionStockLedgerSqlServerTests.cs
```

---

# 24. Implementation Order

```text
1. Add failing tests.

2. Add SQL/EF 6-decimal precision.

3. Replace Production money rounding.

4. Add pre-seal valuation API.

5. Stamp ProductionMaterialMovement before valuation.

6. Fix FIFO aggregation.

7. Reorder IP posting.

8. Fix Production pool valuation verification.

9. Strengthen Production invariant.

10. Fix FIFO FG exact value.

11. Run focused tests.

12. Run full build/test.
```

---

# 25. Hard Do-Not Rules

```text
DO NOT use IvBalLoc.UnitPrice as Production authority.

DO NOT use IvQty.Round for money.

DO NOT create ProductionBalLot before V2 valuation.

DO NOT patch immutable valuation evidence.

DO NOT choose one FIFO fact and ignore others.

DO NOT overwrite Production movement identity with FIFO split identity.

DO NOT reconstruct FIFO FG value from rounded unit price.

DO NOT change general Inventory costing formulas.

DO NOT remove Standard Cost variance.

DO NOT weaken rollback/dependency checks.

DO NOT deploy new app before precision SQL.
```

---

# 26. Done Criteria

```text
[ ] MA IP value matches V2.

[ ] FIFO IP value matches summed FIFO facts.

[ ] Standard IP value matches V2 Standard cost.

[ ] Production money persists 6 decimals.

[ ] IP pool created only after valuation.

[ ] FIFO split facts link to one Production movement.

[ ] Daily Production preserves value.

[ ] FG partial/final transfer preserves value.

[ ] MA FG uses exact value.

[ ] FIFO FG uses exact value.

[ ] Standard variance remains correct.

[ ] rollback reverses exact value.

[ ] failure is fully atomic.

[ ] existing Inventory costing regression tests remain green.
```

---

**APPROVED FOR CURSOR / GROK 4.7 IMPLEMENTATION**
