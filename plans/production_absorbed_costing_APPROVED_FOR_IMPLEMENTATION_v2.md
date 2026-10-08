# Simplified Standard Absorbed Production Costing — APPROVED AI Code Agent Plan

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Verified commit:** `16e3d1a9641d9242668004e53b95e3336955148f`  
**Status:** repository-grounded, costing-critical execution specification

> Implementation guard: if `production` HEAD has moved beyond the verified commit, the Code Agent MUST diff the files named in this plan before changing them. Do not assume the plan still matches changed source.

# Objective

Enhance Production valuation so a V4 Daily Production output carries:

```text
Produced Pool Value
=
Actual verified material/WIP value
+
Standard absorbed conversion cost
```

Standard absorbed conversion cost is:

```text
Labour
+ Machine
+ Utilities / Overhead
+ Other production cost
```

V1 conversion-cost policy:

- rates are frozen standards, not actual payroll/runtime/meter cost;
- rates are expressed in company base currency;
- rates are absorbed per posted `GoodQty`;
- Daily Production users enter no cost;
- Scrap/Reject/Hold conversion variance is not modeled in V1;
- FG Receipt MUST continue transferring production-pool value and MUST NOT recalculate production cost.

Existing Moving Average, FIFO, Standard Cost, Material Issue, pool valuation, rollback, V2 stock-ledger and FG value transfer MUST remain authoritative.

# Confirmed Problems

## Daily Production currently transfers only consumed material/WIP value

`ErpWeb.Core/Production/ProductionOutputService.Posting.cs`

Current `totalConsumedCost` is built only from production-pool material/handoff consumption and is used as the produced pool/movement value.

No labour/machine/OH/other value is added.

## Frozen labour/machine inputs already exist

Existing:

- `ProductionWorkOrderLabour.Rate`
- `ProductionWorkOrderLabour.RateBasis`
- `ProductionWorkOrderLabour.ContributesToPlan`
- `ProductionWorkOrderMachine.MachineRatePerHour`
- machine cycle/setup/conversion standards

They do not currently increase production output value.

## Machine rate precision is currently reduced to quantity precision

`PrProductDefService.ValidateOperationsAsync(...)` currently uses `IvQty.Round(machine.MachineRatePerHour)` even though machine cost columns support six decimals.

This MUST be corrected as part of V4.

## Product Definition has no simple OH/other absorbed rate

Add two optional per-good-output-unit rates on the operation.

## Current valuation evidence assumes PRODUCE value comes from consumed inputs

`ProductionPoolValuationService.RecordAsync(...)` currently labels forward PRODUCE evidence as `CONSUMED_INPUTS`.

V4 requires explicit immutable absorbed-conversion evidence.

## V3/V4 gates are distributed across multiple files

Daily Production Create/Workspace/Post and Work Order Draft/UI rules currently use different `Current`/format checks.

Bumping `Current` to V4 without changing all gates would break existing Released/InProgress V3 execution or mislabel V3 Drafts as current.

# Scope / Non-Goals

## Scope

Implement:

- Product Definition Utilities/OH and Other cost per output unit;
- Machine Master hourly-cost defaulting into a newly assigned Product Definition machine;
- six-decimal machine-rate preservation;
- frozen Work Order operation cost rates;
- frozen Work Order machine planned cost and per-unit absorbed rate;
- V4 snapshot hash;
- V3 Product Definition source hash;
- immutable conversion-cost facts linked directly to PRODUCE movements;
- V4 Daily Production produced value;
- forward/reversal valuation proof;
- exact V4 rollback evidence;
- V3 execution backward compatibility;
- user-friendly Product Definition costing inputs;
- dedicated Work Order COSTING tab;
- SQL/preflight/reset/test coverage.

## Non-Goals

MUST NOT implement:

- payroll/salary actual cost;
- operator timeclock actual cost;
- actual machine runtime;
- electricity meter consumption;
- monthly actual overhead allocation;
- GL journals;
- cost-centre accounting;
- percentage-of-labour overhead formulas;
- complex overhead masters;
- manual Daily Production cost entry;
- Scrap/Reject/Hold conversion-cost variance;
- changes to material/WIP cost authority;
- changes to `FinishedGoodReceiptMath.AllocateValue(...)`;
- cost recalculation inside FG Receipt;
- unrelated UI refactoring.

# Exact Files

## Model / EF

Existing:

- `ErpWeb.Model/Entities/Planning/PrBomOperation.cs`
- `ErpWeb.Model/Configurations/Planning/PrBomOperationConfiguration.cs`
- `ErpWeb.Model/Entities/Production/ProductionWorkOrderOperation.cs`
- `ErpWeb.Model/Entities/Production/ProductionWorkOrderMachine.cs`
- `ErpWeb.Model/Configurations/Production/ProductionWorkOrderOperationConfiguration.cs`
- `ErpWeb.Model/Configurations/Production/ProductionWorkOrderMachineConfiguration.cs`
- `ErpWeb.Model/Entities/Production/ProductionDomainConstants.cs`
- `ErpWeb.Model/Data/AppDbContext.cs`

New:

- `ErpWeb.Model/Entities/Production/ProductionConversionCostFact.cs`
- `ErpWeb.Model/Configurations/Production/ProductionConversionCostFactConfiguration.cs`

## Product Definition

- `ErpWeb.Core/Planning/IPrProductDefService.cs`
- `ErpWeb.Core/Planning/PrProductDefService.cs`
- `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor`
- `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor.cs`

## Work Order

- `ErpWeb.Core/Production/WorkOrderSnapshotBuilder.cs`
- `ErpWeb.Core/Production/WorkOrderQuantityCalculator.cs`
- `ErpWeb.Core/Production/WorkOrderSnapshotHasher.cs`
- `ErpWeb.Core/Production/WorkOrderReadinessValidator.cs`
- `ErpWeb.Core/Production/IProductionWorkOrderService.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`

## Daily Production / Valuation / Rollback

Existing:

- `ErpWeb.Core/Production/ProductionOutputService.cs`
- `ErpWeb.Core/Production/ProductionOutputService.Entry.cs`
- `ErpWeb.Core/Production/ProductionOutputService.Posting.cs`
- `ErpWeb.Core/Production/ProductionOutputService.Rollback.cs`
- `ErpWeb.Core/Production/ProductionPoolValuationService.cs`
- `ErpWeb.Core/Production/ProductionPostingInvariant.cs`

New:

- `ErpWeb.Core/Production/ProductionAbsorbedCostCalculator.cs`

## SQL

Existing:

- `scripts/create-product-definition-routing.sql`
- `scripts/alter-product-definition-phase1-foundation.sql`
- `scripts/create-production-workorder.sql`
- `scripts/reset_all_erp_transaction_data_production_branch.sql`

New:

- `scripts/alter-production-absorbed-conversion-cost.sql`
- `scripts/preflight-production-absorbed-conversion-cost.sql`

## Tests

- `ErpWeb.Tests/Planning/Master/PrProductDefServiceTests.cs`
- `ErpWeb.Tests/Planning/Master/ProductDefinitionAuthoringTests.cs`
- `ErpWeb.Tests/Planning/Master/ProductDefinitionPhase1SchemaTests.cs`
- `ErpWeb.Tests/Planning/Transaction/WorkOrderQuantityCalculatorTests.cs`
- `ErpWeb.Tests/Planning/Transaction/WorkOrderSnapshotBuilderTests.cs`
- `ErpWeb.Tests/Planning/Transaction/WorkOrderSnapshotHasherTests.cs`
- `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderSchemaTests.cs`
- `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderServiceTests.cs`
- `ErpWeb.Tests/Planning/Transaction/WorkOrderReadinessValidatorTests.cs`
- `ErpWeb.Tests/Production/Transaction/ProductionDailyOutputSchemaTests.cs`
- `ErpWeb.Tests/Production/Transaction/ProductionOutputEntryServiceTests.cs`
- `ErpWeb.Tests/Production/Transaction/ProductionCostGateTests.cs`
- `ErpWeb.Tests/Production/Transaction/ProductionStockLedgerPostingTests.cs`
- `ErpWeb.Tests/Production/Transaction/ProductionStockLedgerSqlServerTests.cs`
- `ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs`
- `ErpWeb.Tests/Inventory/Transaction/InventoryValuationServiceTests.cs`

# Exact Changes

## 1. Product Definition operation rates

### `PrBomOperation`

Add:

```csharp
public decimal UtilitiesOverheadCostPerOutputUnit { get; set; }
public decimal OtherCostPerOutputUnit { get; set; }
```

Semantics:

- company base currency;
- per good output unit of the owning operation;
- zero = not used;
- negative forbidden.

### EF

Map both as:

```text
decimal(19,6) NOT NULL DEFAULT 0
```

with non-negative check.

### `PrProductDefOperationVm`

Add the same two fields.

### `PrProductDefService`

Update:

- `ValidateOperationsAsync(...)`
- `NormalizedOperation`
- `AddRoute(...)`
- `MapEdit(...)`
- `CreateNewVersionAsync(...)`

Rules:

```text
Utilities/OH rate -> round 6 decimals
Other rate        -> round 6 decimals
MachineRatePerHour -> round 6 decimals
```

MUST NOT use `IvQty.Round` for monetary rates.

Existing quantities/times remain on their current precision rules.

Clone/new-version MUST preserve all three monetary rates exactly.

---

## 2. Product Definition UX

### Operation editor

Add one compact optional Costing group to the existing process editor:

```text
Utilities / OH per output unit
Other production cost per output unit
```

Helper:

`Optional standard conversion cost in company base currency. Applied per good output unit. Leave 0 when not used.`

Use `DxSpinEdit`, `MinValue=0`, six-decimal cost precision.

Do not add these fields as extra columns to the existing operation table.

### Machine editor

Keep the existing `Rate / hour` field.

When a user assigns or changes a machine on an unsaved machine editor row:

```text
MachineRatePerHour = selected PrMachine.HourlyCost
```

Rules:

- default only when the machine selection changes/new row is assigned;
- user may override the Product Definition rate;
- editing an existing saved machine MUST NOT silently reread/overwrite the master rate;
- changing Machine Master later MUST NOT alter an existing Product Definition;
- cloning a Product Definition preserves the Product Definition rate.

Display/edit machine rate at six-decimal costing precision.

No other Machine Master time field is auto-copied in this plan because its current unit semantics are not part of the verified Product Definition costing contract.

---

## 3. Work Order frozen fields

### `ProductionWorkOrderOperation`

Add:

```csharp
public decimal UtilitiesOverheadCostPerOutputUnit { get; set; }
public decimal OtherCostPerOutputUnit { get; set; }
```

DB:

```text
decimal(19,6) NOT NULL DEFAULT 0
```

### `ProductionWorkOrderMachine`

Add:

```csharp
public decimal PlannedCostAmount { get; set; }
public decimal CostPerOutputUnit { get; set; }
```

DB:

```text
decimal(19,6) NOT NULL DEFAULT 0
```

All new fields MUST be non-negative.

Existing rows backfill to zero.

V3 rows remain V3 and MUST NOT absorb the new values.

---

## 4. Work Order snapshot copy

### `WorkOrderSnapshotBuilder.BuildOperation(...)`

Copy the two authored operation rates into the Work Order operation.

Daily Production MUST NEVER reread current Product Definition rates.

The Work Order snapshot is execution authority.

---

## 5. Machine standard absorbed cost

### `WorkOrderQuantityCalculator.CalculateMachineAsync(...)`

Preserve existing quantity/scheduling calculations.

After `PlannedCycleCount` is known:

```text
runMachineSeconds
    = PlannedCycleCount × CycleSeconds

costableMachineSeconds
    = SetupSeconds
    + ConversionSeconds
    + runMachineSeconds

PlannedCostAmount
    = Money((costableMachineSeconds / 3600) × MachineRatePerHour)

CostPerOutputUnit
    = PlannedOutputQty > 0
      ? Money(PlannedCostAmount / PlannedOutputQty)
      : 0
```

Rules:

- `MachineRatePerHour` means standard cost per active machine-hour;
- use `PlannedCycleCount`, NOT `PlannedCycleSlots`;
- parallel capacity reduces elapsed calendar time but does not remove machine-cycle cost;
- Setup and Conversion are one standard charge for the operation/machine option;
- Queue is scheduling delay and MUST NOT be costed;
- no actual runtime;
- all amounts use `StockLedgerPrecision.Money`;
- calculate values for alternatives as well as the selected machine for traceability;
- only the selected machine contributes to Daily Production absorbed cost.

`SelectDraftMachineAsync(...)` already recalculates quantities. Preserve that path so selected-machine cost is refreshed before Release.

---

## 6. Snapshot contract V4

### `ProductionSnapshotHashVersions`

Add:

```text
AbsorbedConversionCostV4 = 4
Current = AbsorbedConversionCostV4
```

Add central helpers:

```text
SupportsDailyProduction(version)
    => version >= RouteOutputContractV3

UsesAbsorbedConversionCost(version)
    => version >= AbsorbedConversionCostV4
```

### `ProductionDefinitionSourceHashVersions`

Add:

```text
AbsorbedConversionCostV3 = 3
Current = AbsorbedConversionCostV3
```

### Snapshot format

DO NOT change:

```text
ProductionSnapshotFormatVersions.Current = 3
```

### `WorkOrderSnapshotHasher`

Preserve byte-for-byte behavior of:

- snapshot V1;
- snapshot V2;
- snapshot V3;
- definition source V1;
- definition source V2.

Add:

- `ComputeSnapshotHashV4(...)`
- `ComputeDefinitionSourceHashV3(...)`

V4 hash includes:

- operation Utilities/OH rate;
- operation Other rate;
- machine `PlannedCostAmount`;
- machine `CostPerOutputUnit`.

Definition source V3 includes the two authored operation cost rates.

Do not inject the new fields into old hash writers.

---

## 7. Current Draft vs legacy executable V3 rules

### `IProductionWorkOrderService.cs`

Expose:

```csharp
public int SnapshotHashVersion { get; init; }
```

on `ProductionWorkOrderDetail`.

Map it in `ProductionWorkOrderService.MapDetail(...)`.

### `ProductionWorkOrderService.DraftCommands.cs`

Change `RequireCurrentSnapshot(...)` so Draft structural/edit/release paths require:

```text
SnapshotFormatVersion >= current format
AND
SnapshotHashVersion >= current hash
```

A V3 Draft after deployment is not current for structural editing/re-release.

### `WorkOrderReadinessValidator`

Keep Release requiring V4 Current.

Update stale wording from “route-output contract” to generic/current production costing contract wording.

### Work Order UI

`PrWorkOrderEntry.razor.cs`:

`IsCurrentSnapshot` MUST check both format and hash version.

`NeedsDefinitionUpgrade` MUST trigger for an old hash contract even when the format is still v3.

Messages MUST say:

`Refresh Definition to upgrade the Work Order snapshot/costing contract before editing or re-releasing.`

---

## 8. Existing V3 Daily Production compatibility

Update every Daily Production execution gate.

### `ProductionOutputService.Entry.cs`

`EligibleOperationsQuery(...)` MUST exclude only snapshots below V3 minimum execution support.

`IsEligibleDailyProductionOperation(...)` MUST use:

```text
ProductionSnapshotHashVersions.SupportsDailyProduction(...)
```

### `ProductionOutputService.cs`

`CreateAsync(...)` MUST use `SupportsDailyProduction(...)`.

Do not require V4 for an already Released/InProgress V3 WO.

### `ProductionOutputService.Posting.cs`

`PostAsync(...)` MUST use `SupportsDailyProduction(...)`.

Define:

```text
absorbedCostEnabled
    = ProductionSnapshotHashVersions.UsesAbsorbedConversionCost(order.SnapshotHashVersion)
```

Behavior:

```text
V1/V2 -> not executable
V3    -> existing material/WIP-only costing
V4+   -> material/WIP + standard absorbed conversion cost
```

Remove/update hard-coded “refresh to V3” error text so messages remain correct after V4 becomes Current.

---

## 9. New immutable conversion-cost fact

### New constants

Add `ProductionConversionCostTypes`:

```text
LABOUR
MACHINE
UTILITIES_OVERHEAD
OTHER
```

with `IsKnown`.

### New entity/table

`ProductionConversionCostFact` / `dbo.PrProductionConversionCostFact`

Columns:

```text
Id                    bigint IDENTITY PK
CompanyCode           nvarchar(5)   NOT NULL
BranchCode            nvarchar(5)   NOT NULL
StockPostingId        bigint        NOT NULL
ProductionOutputId    bigint        NOT NULL
ProductionMovementId  bigint        NOT NULL
WorkOrderId           bigint        NOT NULL
RouteStepId           bigint        NOT NULL
WorkOrderOperationId  bigint        NOT NULL
CostType              nvarchar(20)  NOT NULL
SourceLineKey         nvarchar(80)  NOT NULL
WorkOrderLabourId     bigint        NULL
WorkOrderMachineId    bigint        NULL
BasisQty              decimal(18,4) NOT NULL
BasisUom              nvarchar(10)  NOT NULL
RatePerOutputUnit     decimal(19,6) NOT NULL
CostAmount            decimal(19,6) NOT NULL
ReversesFactId        bigint        NULL
CreatedAtUtc          datetime2(7)  NOT NULL
CreatedBy             nvarchar(10)  NOT NULL
```

All amounts are company base currency.

Required `Restrict/NO ACTION` FKs:

- StockPosting;
- ProductionOutput;
- ProductionBalLotMovement through `ProductionMovementId`;
- WorkOrder;
- RouteStep;
- WorkOrderOperation;
- optional WorkOrderLabour;
- optional WorkOrderMachine;
- self `ReversesFactId`.

Indexes:

```text
UNIQUE (ProductionMovementId, SourceLineKey)

UNIQUE ReversesFactId
WHERE ReversesFactId IS NOT NULL

INDEX (StockPostingId, ProductionOutputId)
INDEX (WorkOrderId, WorkOrderOperationId)
```

Checks:

```text
CostType known
BasisQty > 0
RatePerOutputUnit > 0
CostAmount > 0
```

Source-shape:

```text
LABOUR:
  LabourId != null
  MachineId == null

MACHINE:
  MachineId != null
  LabourId == null

UTILITIES_OVERHEAD / OTHER:
  LabourId == null
  MachineId == null
```

Do not write zero-cost fact rows.

### Immutability

Add DbSet to `AppDbContext`.

Extend the existing immutable evidence guard:

- Added allowed;
- Modified rejected;
- Deleted rejected.

Rollback adds a linked fact; it never edits/deletes the original.

---

## 10. Absorbed cost calculator

### New `ProductionAbsorbedCostCalculator.cs`

Pure deterministic helper.

No DB access.

Inputs:

- frozen `ProductionWorkOrderOperation`;
- its frozen machine/labour graph;
- current Daily Production `GoodQty`;
- Daily Production `OutputUom`.

Before costing:

```text
Normalize(OutputUom)
==
Normalize(operation.PlannedOutputUom)
```

MUST be true.

Otherwise FAIL CLOSED.

### Labour

Include:

- operation-owned labour; and
- selected-machine-owned labour;

where:

```text
ContributesToPlan == true
RateBasis == PER_OUTPUT_UNIT
Rate > 0
```

Per fact:

```text
BasisQty = GoodQty
RatePerOutputUnit = frozen labour Rate
CostAmount = Money(GoodQty × Rate)
SourceLineKey = LABOUR:{WorkOrderLabourUid}
```

Unsupported contributing rate basis => FAIL CLOSED.

### Machine

Exactly one selected machine is expected for machine-based operations.

When selected machine `CostPerOutputUnit > 0`:

```text
BasisQty = GoodQty
RatePerOutputUnit = frozen machine CostPerOutputUnit
CostAmount = Money(GoodQty × CostPerOutputUnit)
SourceLineKey = MACHINE:{WorkOrderMachineUid}
```

More than one selected => FAIL CLOSED.

For a machine-based operation with machine options but no selected machine => FAIL CLOSED.

### Utilities/OH

When rate > 0:

```text
CostAmount = Money(GoodQty × rate)
SourceLineKey = UTILITIES_OVERHEAD:{OperationUid}
```

### Other

When rate > 0:

```text
CostAmount = Money(GoodQty × rate)
SourceLineKey = OTHER:{OperationUid}
```

### Rounding authority

V1 uses transaction-level six-decimal rounding:

```text
Fact CostAmount
= StockLedgerPrecision.Money(GoodQty × frozen rate)
```

No cumulative residual redistribution.

The immutable fact sum is authoritative.

---

## 11. Daily Production forward posting

### Lock/read order

Preserve existing:

- branch stock transaction lock;
- EF transaction;
- output lock;
- posting-link lock;
- Work Order lock;
- route/operation/sequence locks;
- material/pool lock order.

After locking Work Order/operation:

- load frozen machine/labour rows for this operation;
- do not reread current Product Definition or Machine Master.

### V4 calculation

If `absorbedCostEnabled`:

```text
conversionLines = calculator(...)
conversionCost = Money(sum line.CostAmount)
```

Continue existing material/handoff consumption unchanged.

Set:

```text
materialAndWipCost = existing totalConsumedCost

producedCost
    = Money(materialAndWipCost + conversionCost)
```

Use `producedCost` everywhere the forward PRODUCE path currently uses `totalConsumedCost`:

- handoff output lot;
- handoff PRODUCE movement;
- final WIP/FG staging lot;
- final PRODUCE movement.

### Cost authority gate

For a V4 output that creates a pool:

```text
hasCostAuthority
=
consumedLots.Count > 0
OR
conversionCost > 0
```

If false, fail valuation readiness.

V3 retains the current consumed-input requirement/behavior.

### Fact persistence order

After the forward PRODUCE movement has been saved and has a UID:

1. query the current output/posting-link forward PRODUCE movement;
2. V4 MUST resolve exactly one forward PRODUCE when `GoodQty > 0` and a pool is produced;
3. attach every conversion line to that `ProductionMovementId`;
4. use current StockPosting ID;
5. `CreatedAtUtc = context.Posting.PostedAtUtc`;
6. save conversion facts;
7. then call `StampOutputLedgerFactsAsync(...)`.

Any failure rolls back the entire transaction.

---

## 12. Production pool valuation

### `ProductionPoolValuationService.RecordAsync(...)`

For a forward PRODUCE:

```text
consumedValue
    = Money(sum current output/posting CONSUME TotalCost)

conversionValue
    = Money(sum original conversion facts
            where ProductionMovementId == current PRODUCE movement)

expectedProducedValue
    = Money(consumedValue + conversionValue)
```

Require:

```text
movement.TotalCost == expectedProducedValue
```

Status:

```text
inputsVerified
    = all consumed input evidence VERIFIED

hasAuthority
    = at least one consumed input
      OR conversionValue > 0

PRODUCE VERIFIED
    = inputsVerified
      AND hasAuthority
      AND conversion facts reconcile
```

Evidence basis:

```text
CONSUMED_INPUTS
ABSORBED_CONVERSION
CONSUMED_INPUTS+ABSORBED_CONVERSION
```

Pool tracked value MUST continue to equal `ProductionBalLot.TotalCost`.

For reversal movements, preserve the existing original-evidence `REVERSAL` behavior. Conversion reversal facts are audit/source evidence; the PRODUCE_REVERSAL movement already reverses the exact original total value.

---

## 13. Forward posting invariant

### `ProductionPostingInvariant.AssertOutputVerifiedAsync(...)`

For V4 forward output:

Require exactly one forward PRODUCE when conversion facts exist.

Require:

```text
PRODUCE.TotalCost
=
Money(
  sum current CONSUME.TotalCost
  +
  sum conversion facts for PRODUCE movement
)
```

Every conversion fact MUST match:

- company;
- branch;
- StockPosting;
- ProductionOutput;
- WorkOrder;
- RouteStep;
- WorkOrderOperation;
- produced movement;
- `BasisQty == output.GoodQty`;
- `BasisUom == output.OutputUom`;
- CostType source shape.

No orphan or duplicate conversion fact.

Continue existing movement evidence/pool reconciliation.

---

## 14. Rollback / reversal

### Precondition

Load original forward conversion facts for the original PRODUCE movement.

If any exist:

```text
ledger.LedgerEnabled MUST be true
ledger.Context MUST NOT be null
```

Otherwise FAIL CLOSED before balance mutation.

### Reversal facts

Preserve existing exact PRODUCE reversal value:

```text
ProduceReversal.TotalCost
=
OriginalProduce.TotalCost
```

Do not recalculate historical conversion rates.

After PRODUCE_REVERSAL movement IDs are saved:

for every original conversion fact:

- find the reversal PRODUCE movement whose `OriginalMovementId` is the original fact's `ProductionMovementId`;
- create exactly one conversion reversal fact;
- `ProductionMovementId = reversal PRODUCE movement UID`;
- copy CostType/source IDs/BasisQty/BasisUom/Rate/CostAmount;
- `ReversesFactId = original fact Id`;
- `StockPostingId = rollback StockPosting`;
- `CreatedAtUtc = rollback context.Posting.PostedAtUtc`.

Amounts remain positive magnitude, matching the repository's existing linked-reversal fact pattern.

Do not add/subtract conversion value from the pool a second time.

### New rollback invariant

Add:

```text
ProductionPostingInvariant.AssertOutputRollbackVerifiedAsync(...)
```

It MUST verify:

- exact movement reversals exist;
- reversal movement `OriginalMovementId` linkage;
- exact quantity/value equality to originals;
- exact conversion fact reversal count;
- exact conversion fact rate/basis/amount/source equality;
- reversal fact points to the correct PRODUCE_REVERSAL movement;
- no duplicate `ReversesFactId`;
- touched pool value/quantity reconciles.

Run after `StampOutputLedgerFactsAsync(...)` and before `CompleteInTransactionAsync(...)`.

---

## 15. Work Order user-friendly COSTING tab

Do NOT add cost columns to the already-dense OPERATIONS or MACHINES grids.

Add a read-only `COSTING` tab after `LABOUR`.

### V4 banner

```text
Absorbed conversion costing · V4
```

Subtext:

`Planned conversion cost only. Actual material/WIP cost is added during Daily Production posting.`

### V3 Released/InProgress banner

```text
Legacy V3 · material/WIP-only production costing
```

Subtext:

`This Work Order keeps its original costing contract. It is not silently upgraded.`

### V3 Draft banner

```text
Refresh Definition required before re-release
```

### Cost grid

One row per visible operation:

```text
Process
Output Qty / UOM
Labour / unit
Machine / unit
Utilities/OH / unit
Other / unit
Total conversion / unit
Planned conversion cost
```

Display calculations:

```text
LabourPerUnit
    = sum contributing frozen PER_OUTPUT_UNIT labour rates

MachinePerUnit
    = selected machine CostPerOutputUnit

TotalConversionPerUnit
    = LabourPerUnit
      + MachinePerUnit
      + UtilitiesOverheadCostPerOutputUnit
      + OtherCostPerOutputUnit
```

Planned display total:

```text
plannedLabour
    = Money(LabourPerUnit × PlannedOutputQty)

plannedMachine
    = selected machine PlannedCostAmount

plannedOH
    = Money(OH rate × PlannedOutputQty)

plannedOther
    = Money(Other rate × PlannedOutputQty)

plannedConversion
    = Money(plannedLabour + plannedMachine + plannedOH + plannedOther)
```

This display is planning information only. It MUST NOT become posting authority.

---

# Database Changes

## Product Definition

`dbo.PrBomOperation`:

```text
UtilitiesOverheadCostPerOutputUnit decimal(19,6) NOT NULL DEFAULT 0
OtherCostPerOutputUnit             decimal(19,6) NOT NULL DEFAULT 0
```

Non-negative constraint.

## Work Order operation

`dbo.PrWorkOrderOperation`:

same two columns, `(19,6)`, NOT NULL, default 0, non-negative.

## Work Order machine

`dbo.PrWorkOrderMachine`:

```text
PlannedCostAmount decimal(19,6) NOT NULL DEFAULT 0
CostPerOutputUnit decimal(19,6) NOT NULL DEFAULT 0
```

Non-negative.

## Conversion cost fact

Create `dbo.PrProductionConversionCostFact` exactly as specified in Exact Changes.

## Backfill

Existing Product Definition and WO rows:

- new numeric columns = 0;
- no historical rate inference;
- no conversion fact backfill;
- V3 historical WOs stay V3.

## SQL deployment

`alter-production-absorbed-conversion-cost.sql` MUST be idempotent:

- `COL_LENGTH` guards;
- object/index/constraint existence guards;
- `WITH VALUES` default backfill where applicable;
- no destructive rewrite.

`preflight-production-absorbed-conversion-cost.sql` MUST verify:

- required columns/table/indexes/constraints;
- no negative rates/cost fields;
- fact FK/index integrity;
- V4 rows do not exist without required schema;
- existing V3 Released/InProgress WOs are accepted as legacy executable contracts.

## Reset

Add `PrProductionConversionCostFact` before its referenced Production Output/StockPosting/WO/movement rows in reset deletion order.

# Transaction / Execution Order

## V4 forward

1. authorize;
2. begin existing EF transaction;
3. acquire branch stock transaction lock;
4. lock output;
5. validate replay/status;
6. lock forward posting link;
7. lock Work Order;
8. validate V3+ execution contract;
9. lock route step and operation;
10. lock execution sequence graph;
11. begin existing V2 StockPosting context;
12. load frozen operation machine/labour rows;
13. for V4, validate cost UOM and calculate conversion lines;
14. validate/lock existing material/handoff pools;
15. consume material/WIP using existing exact logic;
16. `producedCost = consumedValue + conversionValue`;
17. write/update output pool and PRODUCE movement;
18. persist movement set;
19. attach/persist immutable conversion facts to the single PRODUCE movement;
20. update execution projections;
21. `StampOutputLedgerFactsAsync(...)`;
22. `ProductionPoolValuationService.RecordAsync(...)`;
23. `ProductionPostingInvariant.AssertOutputVerifiedAsync(...)`;
24. update document/WO/audit status;
25. seal V2 posting;
26. commit.

## V4 rollback

1. authorize;
2. begin transaction;
3. acquire branch stock lock;
4. lock output/posting/WO/route/operation;
5. retain existing downstream dependency guards;
6. create rollback posting link;
7. begin reversal StockPosting;
8. if original conversion facts exist, require active V2 context;
9. reverse CONSUME rows exactly;
10. reverse PRODUCE rows exactly;
11. save reversal movement IDs;
12. append exact linked conversion reversal facts;
13. update execution projections/status/audit;
14. `StampOutputLedgerFactsAsync(...)`;
15. run `AssertOutputRollbackVerifiedAsync(...)`;
16. seal reversal posting;
17. commit.

# Authority Rules

## Quantity

Unchanged existing Daily Production / material movement / UOM authority.

## Material/WIP value

Unchanged verified inventory/production pool authority.

## Labour

Frozen Work Order contributing `PER_OUTPUT_UNIT` rate.

## Machine

Frozen selected Work Order `CostPerOutputUnit`.

## Utilities/OH

Frozen Work Order operation rate.

## Other

Frozen Work Order operation rate.

## Conversion fact

Immutable `PrProductionConversionCostFact`.

## Produced pool

```text
verified consumed material/WIP
+
verified conversion facts
```

## FG

Unchanged:

- `ProductionBalLot.TotalCost`;
- `FinishedGoodReceiptMath.AllocateValue`;
- `ExactTransferredValue`;
- inventory valuation engine.

## Standard Cost

Unchanged inventory authority:

- FG inventory is received at current effective Standard Cost;
- `ProductionStandardCostVariance` records:
  `ActualProductionValue - StandardInventoryValue`;
- V4 richer production-pool value reaches `ActualProductionValue` through existing `ExactTransferredValue`.

# Invariants

## V4 forward

```text
ConversionValue
=
Money(sum original conversion facts for PRODUCE movement)
```

```text
ProducedValue
=
Money(ConsumedMaterialWipValue + ConversionValue)
```

```text
ProductionBalLot.TotalCost
=
ProductionPoolValuation.TrackedValue
```

```text
PRODUCE evidence status
=
VERIFIED
```

## Conversion fact

```text
Fact.ProductionMovementId
=
current forward PRODUCE movement
```

and all fact scope/source identities match the movement/output/WO.

## V4 rollback

```text
ProduceReversal.TotalCost
=
OriginalProduce.TotalCost
```

```text
ReversalFact.CostAmount
=
OriginalFact.CostAmount
```

```text
ReversalFact.ReversesFactId
=
OriginalFact.Id
```

```text
ReversalFact.ProductionMovementId
=
matching PRODUCE_REVERSAL movement
```

## FG

```text
FG exact transferred value
=
value allocated from Production pool
```

No labour/machine/OH recalculation in FG.

# Tests

## Product Definition

Add/extend:

- six-decimal machine rate survives save/reload;
- OH/Other rates survive save/reload;
- clone/new version preserves rates;
- negatives rejected;
- machine master defaulting does not overwrite a saved override.

## Schema

Assert all new fields are `(19,6)` and non-negative.

Assert conversion fact:

- table/FKs;
- source-shape constraint;
- movement/source unique index;
- reversal unique index.

## Machine calculation

Test:

- setup + conversion + cycle-count run seconds;
- queue excluded;
- parallel slots affect schedule, not total cycle machine cost;
- zero rate;
- six-decimal money;
- alternative machines calculated;
- selected machine used for absorption.

## Hash compatibility

Must prove:

- snapshot V1 unchanged;
- V2 unchanged;
- V3 unchanged;
- V4 changes for OH;
- V4 changes for Other;
- V4 changes for derived machine cost;
- definition-source V1/V2 unchanged;
- definition-source V3 includes new authored rates;
- insertion order stable.

Update numeric Current assertion to V4.

Keep snapshot format Current at 3.

## Work Order current/legacy rules

Test:

- V3 Released/InProgress remains executable by Daily Production;
- V3 Draft fails current structural/edit/release guard;
- reopened V3 Draft requires refresh before re-release;
- V4 Draft passes current guard.

## Daily Production eligibility

Test all paths:

- search includes executable V3 and V4;
- search excludes V1/V2;
- workspace opens V3 and V4;
- Create succeeds for V3/V4;
- Post succeeds for valid V3/V4;
- V3 never writes conversion facts;
- V4 writes conversion facts.

## Forward costing

Example:

```text
consumed material/WIP  800.000000
labour                  80.000000
machine                 50.000000
utilities/OH            20.000000
other                   10.000000
--------------------------------
PRODUCE                 960.000000
```

Assert:

- pool total = 960;
- PRODUCE total = 960;
- conversion facts = 160;
- every fact points to PRODUCE movement;
- correct CostTypes/source IDs;
- evidence VERIFIED;
- pool projection = pool balance.

## Partial output

Assert each post uses:

```text
Money(GoodQty × frozen rate)
```

and fact sum is exact authority.

Do not assert one-shot vs split posts are mathematically identical beyond the defined six-decimal transaction rounding contract.

## Scrap/Reject/Hold policy

Test:

- material standard/consume still follows existing processed quantity;
- V1 conversion facts use GoodQty only;
- scrap/reject/hold do not create conversion facts;
- no hidden conversion-loss variance is written.

## UOM

Mismatched Daily Production output UOM vs operation planned output UOM MUST block V4 costing.

## Conversion-only operation

V4 with no consumed material but non-zero verified conversion cost may create a VERIFIED pool.

No inputs + zero conversion cost remains blocked.

## Forward invariant failures

Fail/rollback transaction on:

- missing expected conversion fact;
- mismatched amount;
- wrong movement ID;
- wrong output/WO/operation;
- duplicate fact;
- unsupported labour basis;
- duplicate selected machine.

## Rollback

Test:

- exact linked reversal facts;
- reversal fact points to PRODUCE_REVERSAL;
- original facts immutable;
- exact pool restoration;
- second replay creates no duplicate;
- no V2 ledger context with V4 facts => rollback blocked before mutation;
- downstream dependency guards unchanged;
- rollback invariant failure rolls back the entire transaction.

## SQL Server

Test:

- precision;
- CHECK constraints;
- unique movement/source line;
- unique reversal;
- FK restrictions;
- atomic failure rollback.

## FG regression

Prove full V4 pool value:

- partial FG allocation;
- final depletion zero qty/zero value;
- FG rollback restores exact pool value;
- no FG cost formula added.

## Inventory valuation

Moving Average/FIFO:

- FG uses full `ExactTransferredValue` as `PRODUCTION_ACTUAL`.

Standard Cost:

- inventory receipt uses effective Standard Cost;
- `ProductionStandardCostVariance.ActualProductionValue` equals V4 full production transferred value;
- variance reversal remains exact.

# Implementation Order

1. Add failing tests for hash contracts, rate precision, V3/V4 gates and forward/rollback value conservation.
2. Add SQL/model fields and conversion fact schema.
3. Fix Product Definition six-decimal monetary normalization.
4. Add Product Definition OH/Other persistence and friendly UI.
5. Add Machine Master hourly-cost defaulting for new/changed Product Definition machine assignment.
6. Add Work Order snapshot fields and machine derived cost.
7. Add hash V4/source-hash V3 and central version helpers.
8. Update Work Order current Draft predicates/UI hash-version awareness.
9. Update every Daily Production V3/V4 eligibility/Create/Post gate.
10. Implement pure absorbed-cost calculator.
11. Integrate V4 forward produced value and direct movement-linked facts.
12. Extend pool valuation and forward invariant.
13. Implement V4 reversal facts + rollback invariant.
14. Add Work Order COSTING tab.
15. Run FG/Moving Average/FIFO/Standard Cost regression tests.
16. Run SQL Server tests/preflight.
17. Run full solution build/test.

# Regression Areas

MUST remain unchanged unless explicitly specified:

- Material Issue;
- material/WIP cost allocation;
- Moving Average;
- FIFO;
- Standard Cost;
- `ProductionStandardCostVariance`;
- material consume variance;
- handoff sequence;
- production pool dependency graph;
- Work Order reopen/change definition;
- machine selection;
- Daily Production rollback guards;
- FG posting/rollback;
- stock-ledger sealing;
- closed-period/backdated protections;
- company/branch scoping;
- reset/test database behavior.

# Do-Not Rules

DO NOT:

- call standard absorbed conversion cost “actual labour/machine cost”;
- read payroll;
- read actual runtime;
- read current Product Definition during Daily Production;
- read current Machine Master rate during Daily Production;
- overwrite a saved Product Definition rate when Machine Master changes;
- use `IvQty.Round` for money rates;
- absorb QueueSeconds;
- absorb conversion cost to Scrap/Reject/Hold in V1;
- silently upgrade Released/InProgress V3 WOs;
- make V3 WOs fail Daily Production merely because Current becomes V4;
- modify old hash algorithms;
- write conversion facts without direct PRODUCE movement lineage;
- allow V4 rollback without its immutable reversal evidence;
- recalculate costs in FG;
- modify `FinishedGoodReceiptMath.AllocateValue`;
- weaken existing rollback/dependency guards;
- update/delete posted conversion facts;
- add cost columns to the already-dense Work Order Operations/Machines grids;
- add unrelated refactoring.

# Acceptance Criteria

- [ ] Product Definition has optional OH/Other standard conversion rates.
- [ ] All production cost rates retain six-decimal precision.
- [ ] New machine assignment defaults its rate from Machine Master HourlyCost without later silent overwrite.
- [ ] Work Order freezes OH/Other and derived machine cost.
- [ ] Snapshot hash V4 and definition hash V3 are implemented without changing historical hash algorithms.
- [ ] Snapshot format remains v3.
- [ ] V3 Released/InProgress WOs remain Daily Production executable.
- [ ] V3 Drafts require refresh before structural edit/re-release.
- [ ] Daily Production V3 remains material/WIP-only.
- [ ] Daily Production V4 adds standard absorbed conversion cost.
- [ ] V4 cost UOM mismatch fails closed.
- [ ] Conversion facts link directly to the forward PRODUCE movement.
- [ ] V4 produced value exactly equals consumed material/WIP plus conversion fact value.
- [ ] Production pool value/projection remains exact.
- [ ] V4 rollback requires V2 posting context.
- [ ] V4 rollback creates exact movement-linked conversion reversal facts.
- [ ] Forward and rollback invariants run before StockPosting seal.
- [ ] Original facts remain immutable.
- [ ] FG still transfers production-pool value without recalculation.
- [ ] Moving Average/FIFO consume V4 full exact production value.
- [ ] Standard Cost keeps Standard inventory authority and records V4 actual-vs-standard variance.
- [ ] Product Definition UI clearly labels optional per-output-unit standard cost.
- [ ] Work Order has a dedicated readable COSTING tab rather than more dense grid columns.
- [ ] V3 Work Order UI clearly identifies legacy material/WIP-only costing.
- [ ] SQL upgrade/preflight/reset scripts are idempotent/correct.
- [ ] Focused tests pass.
- [ ] SQL Server tests pass.
- [ ] Full solution build and regression suite pass.
- [ ] Deployment is coordinated so all running IIS instances use the V4-capable binaries before users create/release V4 Work Orders.

# Deployment Safety

Schema first, then application binaries.

Do not run mixed old/new application binaries while V4 Work Orders are being created/released.

Required production rollout:

1. stop/drain Production writes;
2. back up DB;
3. run absorbed-cost schema upgrade;
4. run preflight;
5. deploy V4-capable application to all IIS instances;
6. recycle/start all instances;
7. smoke-test Product Definition → WO → Daily Production → FG on a test WO;
8. reopen Production writes.

Existing Released/InProgress V3 WOs continue under their legacy material/WIP-only contract.

# Approval Status

APPROVED FOR IMPLEMENTATION
