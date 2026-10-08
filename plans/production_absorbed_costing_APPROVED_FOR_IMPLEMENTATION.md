# Simplified Absorbed Production Costing — AI Code Agent Implementation Plan

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `production`  
**Target:** .NET / Blazor Server / EF Core / SQL Server production costing  
**Plan type:** Repository-grounded execution specification

# Objective

Extend the existing production costing so a Daily Production output carries a simple full absorbed manufacturing value:

`Produced Cost = Actual Material/WIP Cost + Labour Cost + Machine Cost + Utilities/Overhead Cost + Other Production Cost`

Keep the user-facing model simple:

- Labour continues to use the existing Work Order labour `PER_OUTPUT_UNIT` rate.
- Machine cost is automatically derived from the frozen Work Order machine rate and machine standard.
- Utilities/overhead is a simple per-output-unit rate on the Product Definition operation and frozen into the Work Order.
- Other production cost is a simple per-output-unit rate on the Product Definition operation and frozen into the Work Order.
- Daily Production requires **no new cost input by the shop-floor user**.
- Finished Good Receipt MUST continue to transfer the production-pool value; it MUST NOT recalculate labour/machine/overhead.

The implementation MUST preserve existing Moving Average, FIFO, Standard Cost, posting, rollback, V2 stock-ledger, pool-valuation, and FG value-conservation behavior.

# Confirmed Current Problems

## 1. Daily Production output currently carries material/WIP value only

**File:** `ErpWeb.Core/Production/ProductionOutputService.Posting.cs`  
**Method:** `PostAsync(...)`

Confirmed current behavior:

- `totalConsumedCost` starts at zero.
- material production-pool consumption adds `takeCost`;
- previous-process handoff consumption adds `handoffCost`;
- the produced `ProductionBalLot` / `ProductionBalLotMovement` receives `TotalCost = totalConsumedCost`;
- labour, machine, utilities/overhead and other conversion costs are not added.

Required correction:

- retain the existing exact material/WIP cost logic unchanged;
- calculate absorbed conversion cost from the frozen Work Order snapshot;
- use `producedCost = materialAndWipCost + absorbedConversionCost`.

## 2. Labour and machine costing inputs already exist but are not part of production output value

**Files:**

- `ErpWeb.Model/Entities/Production/ProductionWorkOrderLabour.cs`
- `ErpWeb.Model/Entities/Production/ProductionWorkOrderMachine.cs`
- `ErpWeb.Core/Production/WorkOrderQuantityCalculator.cs`

Confirmed current behavior:

- `ProductionWorkOrderLabour.Rate`, `RateBasis`, `ContributesToPlan`, `PlannedAmount` exist;
- contributing labour is already calculated as `Rate × PlannedOutputQty`;
- `ProductionWorkOrderMachine.MachineRatePerHour`, cycle/setup/conversion data and planned cycle quantities exist;
- machine cost is not calculated into the production pool.

Required correction:

- use contributing `PER_OUTPUT_UNIT` labour rates as the frozen labour-cost authority;
- derive and freeze a machine cost per output unit on the Work Order snapshot.

## 3. Product Definition has no simple utilities/overhead or other production-cost rate

**Files:**

- `ErpWeb.Model/Entities/Planning/PrBomOperation.cs`
- `ErpWeb.Core/Planning/IPrProductDefService.cs`
- `ErpWeb.Core/Planning/PrProductDefService.cs`
- `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor`

Required correction:

Add two non-negative per-output-unit operation rates:

- `UtilitiesOverheadCostPerOutputUnit`
- `OtherCostPerOutputUnit`

These are authored in Product Definition and copied into the Work Order snapshot.

## 4. Production valuation evidence currently describes produced value as consumed-input value

**File:** `ErpWeb.Core/Production/ProductionPoolValuationService.cs`  
**Method:** `RecordAsync(...)`

Confirmed current behavior:

- forward `PRODUCE` is marked from consumed-input verification;
- its evidence basis is `CONSUMED_INPUTS`;
- there is no immutable source record for labour/machine/utilities/other conversion value.

Required correction:

- add immutable conversion-cost facts;
- a V4 `PRODUCE` value MUST reconcile exactly to:
  `verified consumed input value + immutable absorbed conversion-cost facts`.

## 5. Snapshot hashes must not be silently redefined

**Files:**

- `ErpWeb.Core/Production/WorkOrderSnapshotHasher.cs`
- `ErpWeb.Model/Entities/Production/ProductionDomainConstants.cs`

Confirmed current behavior:

- snapshot hash V1/V2/V3 are versioned contracts;
- current snapshot hash is V3;
- definition source hash V1/V2 are versioned contracts.

Required correction:

- add new versions; DO NOT change the payload of existing hash algorithms.

# Scope

Included:

1. Product Definition operation-level utilities/overhead and other cost per output unit.
2. Work Order snapshot of those rates.
3. Frozen derived machine cost per output unit.
4. Existing frozen labour rate participation.
5. Immutable Daily Production conversion-cost facts.
6. Daily Production full absorbed produced value.
7. Production pool valuation reconciliation.
8. Exact rollback/reversal of conversion-cost evidence.
9. Snapshot/source-hash versioning.
10. Backward compatibility for existing released/in-progress V3 Work Orders.
11. Read-only Product Definition / Work Order UI visibility for the new costs.
12. SQL deployment/preflight/reset support.
13. Focused and end-to-end tests through FG inventory valuation.

# Non-Goals

MUST NOT implement in this plan:

- actual employee payroll/salary integration;
- actual operator clock-in/clock-out costing;
- actual machine runtime entry;
- electricity meter/unit consumption;
- monthly actual overhead allocation;
- cost-centre/accounting/GL journal posting;
- a configurable overhead-master engine;
- percentage-of-labour or other complex overhead bases;
- shop-floor manual cost entry;
- changing how scrap/reject/hold absorbs conversion cost in V1;
- changing existing material/WIP cost authority;
- changing `FinishedGoodReceiptMath.AllocateValue(...)`;
- recalculating conversion cost inside Finished Good Receipt;
- the Finished Goods Summary “Cost Trace” popup/button; the new facts created here are the evidence foundation for that separate feature.

# Files to Change

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

**New:**

- `ErpWeb.Model/Entities/Production/ProductionConversionCostFact.cs`
- `ErpWeb.Model/Configurations/Production/ProductionConversionCostFactConfiguration.cs`

## Product Definition

- `ErpWeb.Core/Planning/IPrProductDefService.cs`
- `ErpWeb.Core/Planning/PrProductDefService.cs`
- `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor`
- `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor.cs`

## Work Order snapshot / calculation / display

- `ErpWeb.Core/Production/WorkOrderSnapshotBuilder.cs`
- `ErpWeb.Core/Production/WorkOrderQuantityCalculator.cs`
- `ErpWeb.Core/Production/WorkOrderSnapshotHasher.cs`
- `ErpWeb.Core/Production/IProductionWorkOrderService.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.cs`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor`

## Daily Production / valuation / rollback

Existing:

- `ErpWeb.Core/Production/ProductionOutputService.Posting.cs`
- `ErpWeb.Core/Production/ProductionOutputService.Rollback.cs`
- `ErpWeb.Core/Production/ProductionPoolValuationService.cs`
- `ErpWeb.Core/Production/ProductionPostingInvariant.cs`

**New:**

- `ErpWeb.Core/Production/ProductionAbsorbedCostCalculator.cs`

## SQL

Existing:

- `scripts/create-product-definition-routing.sql`
- `scripts/alter-product-definition-phase1-foundation.sql`
- `scripts/create-production-workorder.sql`
- `scripts/reset_all_erp_transaction_data_production_branch.sql`

**New:**

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
- `ErpWeb.Tests/Production/Transaction/ProductionDailyOutputSchemaTests.cs`
- `ErpWeb.Tests/Production/Transaction/ProductionOutputEntryServiceTests.cs`
- `ErpWeb.Tests/Production/Transaction/ProductionCostGateTests.cs`
- `ErpWeb.Tests/Production/Transaction/ProductionStockLedgerPostingTests.cs`
- `ErpWeb.Tests/Production/Transaction/ProductionStockLedgerSqlServerTests.cs`
- `ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs`
- `ErpWeb.Tests/Inventory/Transaction/InventoryValuationServiceTests.cs`

# Database Changes

## 1. `dbo.PrBomOperation`

Add:

```text
UtilitiesOverheadCostPerOutputUnit decimal(19,6) NOT NULL DEFAULT (0)
OtherCostPerOutputUnit             decimal(19,6) NOT NULL DEFAULT (0)
```

Requirements:

- both MUST be `>= 0`;
- add an idempotent check constraint;
- existing Product Definitions receive zero through `WITH VALUES`;
- no historical value is inferred or backfilled.

Update:

- `PrBomOperation`
- `PrBomOperationConfiguration`
- `create-product-definition-routing.sql`
- `alter-product-definition-phase1-foundation.sql`

## 2. `dbo.PrWorkOrderOperation`

Add:

```text
UtilitiesOverheadCostPerOutputUnit decimal(19,6) NOT NULL DEFAULT (0)
OtherCostPerOutputUnit             decimal(19,6) NOT NULL DEFAULT (0)
```

Requirements:

- both MUST be `>= 0`;
- values are snapshot values copied from Product Definition;
- existing rows receive zero;
- existing Released/InProgress V3 Work Orders MUST NOT begin absorbing these costs merely because columns now exist.

Update:

- entity/configuration;
- `scripts/create-production-workorder.sql`.

## 3. `dbo.PrWorkOrderMachine`

Add:

```text
PlannedCostAmount decimal(19,6) NOT NULL DEFAULT (0)
CostPerOutputUnit decimal(19,6) NOT NULL DEFAULT (0)
```

Requirements:

- both MUST be `>= 0`;
- they are derived Work Order snapshot values;
- existing rows receive zero;
- add them to the existing machine non-negative cost constraint or add a dedicated idempotent constraint.

## 4. New `dbo.PrProductionConversionCostFact`

Create immutable table with:

```text
Id                    bigint IDENTITY PK
CompanyCode           nvarchar(5)  NOT NULL
BranchCode            nvarchar(5)  NOT NULL
StockPostingId        bigint       NOT NULL
ProductionOutputId    bigint       NOT NULL
WorkOrderId           bigint       NOT NULL
RouteStepId           bigint       NOT NULL
WorkOrderOperationId  bigint       NOT NULL
CostType              nvarchar(20) NOT NULL
SourceLineKey         nvarchar(80) NOT NULL
WorkOrderLabourId     bigint       NULL
WorkOrderMachineId    bigint       NULL
BasisQty              decimal(18,4) NOT NULL
BasisUom              nvarchar(10) NOT NULL
RatePerOutputUnit     decimal(19,6) NOT NULL
CostAmount            decimal(19,6) NOT NULL
ReversesFactId        bigint       NULL
CreatedAtUtc          datetime2(7) NOT NULL
CreatedBy             nvarchar(10) NOT NULL
```

Cost types:

```text
LABOUR
MACHINE
UTILITIES_OVERHEAD
OTHER
```

Required FKs, all `NO ACTION`:

- `StockPostingId -> StockPosting.Id`
- `ProductionOutputId -> PrProductionOutput.UID`
- `WorkOrderId -> PrWorkOrder.UID`
- `RouteStepId -> PrWorkOrderRouteStep.UID`
- `WorkOrderOperationId -> PrWorkOrderOperation.UID`
- `WorkOrderLabourId -> PrWorkOrderLabour.UID`
- `WorkOrderMachineId -> PrWorkOrderMachine.UID`
- `ReversesFactId -> PrProductionConversionCostFact.Id`

Required indexes/constraints:

```text
UNIQUE (StockPostingId, ProductionOutputId, SourceLineKey)

UNIQUE filtered ReversesFactId
WHERE ReversesFactId IS NOT NULL

CHECK CostType IN
('LABOUR','MACHINE','UTILITIES_OVERHEAD','OTHER')

CHECK BasisQty > 0
CHECK RatePerOutputUnit > 0
CHECK CostAmount > 0
```

Source-shape check:

- `LABOUR`: `WorkOrderLabourId IS NOT NULL`, `WorkOrderMachineId IS NULL`;
- `MACHINE`: `WorkOrderMachineId IS NOT NULL`, `WorkOrderLabourId IS NULL`;
- `UTILITIES_OVERHEAD` / `OTHER`: both nullable source IDs MUST be null.

Do not persist zero-cost fact rows.

## 5. Immutability

Update `AppDbContext.ValidateFinishedGoodWrites()`:

`ProductionConversionCostFact` MUST follow the same immutable evidence policy as valuation facts:

- `Added` allowed;
- `Modified` rejected;
- `Deleted` rejected;
- rollback creates a linked reversal fact instead.

Add:

```csharp
DbSet<ProductionConversionCostFact> ProductionConversionCostFacts
```

## 6. Deployment order

Required deployment order:

1. deploy application-compatible schema additions for Product Definition and Work Order;
2. create `PrProductionConversionCostFact`;
3. deploy application code;
4. run preflight;
5. only then create/release V4 Work Orders.

The SQL upgrade MUST be idempotent and safe to rerun.

# Exact Code Changes

## A. Product Definition: simple authored conversion-cost rates

### `ErpWeb.Model/Entities/Planning/PrBomOperation.cs`

Add:

```csharp
public decimal UtilitiesOverheadCostPerOutputUnit { get; set; }
public decimal OtherCostPerOutputUnit { get; set; }
```

These rates are expressed in the operation output UOM.

### `ErpWeb.Core/Planning/IPrProductDefService.cs`

Add the same fields to `PrProductDefOperationVm`.

### `ErpWeb.Core/Planning/PrProductDefService.cs`

Update all operation round-trip paths:

- `ValidateOperationsAsync(...)`
- `NormalizedOperation`
- `AddRoute(...)`
- `MapEdit(...)`
- `CreateNewVersionAsync(...)` clone mapping

Rules:

- normalize to six monetary decimals;
- reject negatives;
- zero is valid;
- cloning a Product Definition version MUST preserve both rates exactly.

### `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor`

In the operation editor add:

- `Utilities / OH cost per unit`
- `Other production cost per unit`

Use non-negative decimal editor with six-decimal precision/display appropriate to the existing page.

Do not add a complicated overhead setup screen.

### `PrProductDefEntry.razor.cs`

Update:

- `CloneOperation(...)`
- `SnapshotOperation(...)`
- new/default operation VM behavior
- dirty-state comparison
- `SaveOperationAsync()` validation

A value change MUST participate in the page's unsaved-change detection.

---

## B. Work Order snapshot

### `ProductionWorkOrderOperation`

Add the two frozen rates.

### `WorkOrderSnapshotBuilder.BuildOperation(...)`

Copy:

```text
PrBomOperation.UtilitiesOverheadCostPerOutputUnit
    -> ProductionWorkOrderOperation.UtilitiesOverheadCostPerOutputUnit

PrBomOperation.OtherCostPerOutputUnit
    -> ProductionWorkOrderOperation.OtherCostPerOutputUnit
```

The Work Order is the execution authority after snapshot creation.

Do not read current Product Definition rates during Daily Production posting.

---

## C. Frozen machine cost

### `ProductionWorkOrderMachine`

Add:

```csharp
public decimal PlannedCostAmount { get; set; }
public decimal CostPerOutputUnit { get; set; }
```

### `WorkOrderQuantityCalculator.CalculateMachineAsync(...)`

Keep all existing quantity/scheduling calculations.

After `PlannedCycleCount` is resolved, calculate the simple absorbed machine standard:

```text
runSeconds
    = PlannedCycleCount × CycleSeconds

costableSeconds
    = SetupSeconds
    + ConversionSeconds
    + runSeconds

PlannedCostAmount
    = Money((costableSeconds / 3600) × MachineRatePerHour)

CostPerOutputUnit
    = PlannedOutputQty > 0
      ? Money(PlannedCostAmount / PlannedOutputQty)
      : 0
```

V1 rules:

- use `PlannedCycleCount`, NOT `PlannedCycleSlots`, for run-cost quantity;
- parallel capacity changes elapsed schedule time but MUST NOT erase consumed machine-hours;
- Setup and Conversion are charged once per operation/machine option;
- `QueueSeconds` MUST NOT be included in machine cost;
- no actual runtime is used;
- no current `PrMachine.HourlyCost` is read at Daily Production posting;
- the selected Work Order machine's frozen `CostPerOutputUnit` is authoritative for execution.

`SelectDraftMachineAsync(...)` already reruns `_quantities.CalculateAsync(...)`; preserve that flow so selecting another machine refreshes the frozen machine cost before the Work Order is released.

---

## D. Snapshot/source-hash versioning

### `ProductionDomainConstants.cs`

Add:

```text
ProductionSnapshotHashVersions.AbsorbedConversionCostV4 = 4
ProductionSnapshotHashVersions.Current = AbsorbedConversionCostV4

ProductionDefinitionSourceHashVersions.AbsorbedConversionCostV3 = 3
ProductionDefinitionSourceHashVersions.Current = AbsorbedConversionCostV3
```

Do NOT change:

```text
ProductionSnapshotFormatVersions.Current = 3
```

The hierarchy/schema format is still format 3; this enhancement changes the costing/hash contract, not the route hierarchy.

### `WorkOrderSnapshotHasher.cs`

MUST preserve byte-for-byte behavior of:

- snapshot V1;
- snapshot V2;
- snapshot V3;
- definition-source V1;
- definition-source V2.

Add:

- `ComputeSnapshotHashV4(...)`
- `ComputeDefinitionSourceHashV3(...)`

V4 Work Order hash MUST include:

- `UtilitiesOverheadCostPerOutputUnit`;
- `OtherCostPerOutputUnit`;
- `ProductionWorkOrderMachine.PlannedCostAmount`;
- `ProductionWorkOrderMachine.CostPerOutputUnit`.

Definition source V3 MUST include:

- authored `PrBomOperation.UtilitiesOverheadCostPerOutputUnit`;
- authored `PrBomOperation.OtherCostPerOutputUnit`.

Do not place the new fields unconditionally into shared V1/V2/V3 serialization helpers.

---

## E. Backward compatibility for existing released Work Orders

### `ProductionOutputService.Posting.cs`

Current code requires the Work Order snapshot hash to be at `Current`.

After Current becomes V4, that would incorrectly block existing already-Released/InProgress V3 Work Orders.

Change the execution gate:

```text
minimum executable snapshot hash
    = RouteOutputContractV3
```

Rules:

```text
V1/V2  -> block Daily Production
V3     -> continue existing material/WIP-only production costing
V4+    -> use absorbed conversion costing
```

Define:

```text
absorbedCostEnabled
    = order.SnapshotHashVersion >= AbsorbedConversionCostV4
```

Release/readiness rules MUST continue requiring `Current` for newly released/refreshed Drafts.

This prevents a released Work Order from changing costing method halfway through execution.

---

## F. New pure absorbed-cost calculator

### New `ProductionAbsorbedCostCalculator.cs`

Create a pure deterministic helper; no DB access and no DI requirement.

Inputs MUST come only from the frozen Work Order operation graph plus `GoodQty`.

Output one line per non-zero component with:

```text
CostType
SourceLineKey
WorkOrderLabourId?
WorkOrderMachineId?
BasisQty
BasisUom
RatePerOutputUnit
CostAmount
```

Calculation rules:

### Labour

Use all:

- direct operation labour rows; and
- selected-machine-owned labour rows;

where:

```text
ContributesToPlan == true
RateBasis == PER_OUTPUT_UNIT
Rate > 0
```

Per line:

```text
BasisQty  = GoodQty
Rate      = frozen Work Order labour Rate
CostAmount = Money(GoodQty × Rate)
SourceLineKey = "LABOUR:{WorkOrderLabourUid}"
```

If a contributing labour row has an unsupported rate basis, FAIL CLOSED.

### Machine

Use exactly the selected machine when:

```text
CostPerOutputUnit > 0
```

Calculation:

```text
BasisQty = GoodQty
Rate = frozen machine CostPerOutputUnit
CostAmount = Money(GoodQty × Rate)
SourceLineKey = "MACHINE:{WorkOrderMachineUid}"
```

If more than one machine is marked selected, FAIL CLOSED.

### Utilities / overhead

When operation rate > 0:

```text
CostAmount
    = Money(GoodQty × UtilitiesOverheadCostPerOutputUnit)

SourceLineKey
    = "UTILITIES_OVERHEAD:{OperationUid}"
```

### Other

When operation rate > 0:

```text
CostAmount
    = Money(GoodQty × OtherCostPerOutputUnit)

SourceLineKey
    = "OTHER:{OperationUid}"
```

### General

- `GoodQty` MUST be positive to create conversion-cost facts.
- All cost calculations MUST use `StockLedgerPrecision.Money`.
- Basis quantity remains the repository's four-decimal production quantity.
- Basis UOM MUST equal the operation/output UOM used by the Daily Production document.
- No conversion cost is absorbed against Scrap/Reject/Hold in V1.

---

## G. Daily Production forward posting

### `ProductionOutputService.Posting.cs`

Do not change existing material/WIP consumption pricing.

For V4:

1. after the Work Order/route/operation has been locked and validated, load the frozen machine/labour rows for this operation;
2. calculate absorbed cost lines from the frozen Work Order snapshot;
3. continue the existing exact material/WIP consumption;
4. set:

```text
materialAndWipCost = existing totalConsumedCost
conversionCost = Money(sum conversion cost lines)
producedCost = Money(materialAndWipCost + conversionCost)
```

5. use `producedCost` in every forward produced WIP/FG staging location currently using `totalConsumedCost`:
   - `LockOrCreateHandoffLotAsync(...)`;
   - handoff `ProductionBalLotMovement` `PRODUCE`;
   - `LockOrCreateWipLotAsync(...)`;
   - final `ProductionBalLotMovement` `PRODUCE`.

6. persist one `ProductionConversionCostFact` per non-zero calculator line using:
   - current `StockPostingContext.Posting.Id`;
   - current company/branch;
   - current output/WO/route/operation;
   - frozen source IDs/rates;
   - `ReversesFactId = null`.

7. facts MUST exist before `StampOutputLedgerFactsAsync(...)` invokes `ProductionPoolValuationService.RecordAsync(...)`.

### Existing “no consumed cost basis” gate

Replace the current material-only condition.

For a V4 output that will create a production pool:

```text
hasCostAuthority
    = consumedLots.Count > 0
      OR conversionCost > 0
```

If false, fail with a valuation-required message.

Thus a legitimate labour/machine-only operation can produce verified value, while an output with no material and no conversion-cost authority remains blocked.

For V3 legacy execution, preserve current behavior.

---

## H. Production valuation evidence

### `ProductionPoolValuationService.RecordAsync(...)`

For a forward `ProductionBalLotMovementTypes.Produce`:

Determine:

```text
consumedValue
    = Money(sum current output/posting CONSUME movement TotalCost)

conversionValue
    = Money(sum current output/posting original
            ProductionConversionCostFact.CostAmount)

expectedProducedValue
    = Money(consumedValue + conversionValue)
```

MUST require:

```text
movement.TotalCost == expectedProducedValue
```

Verification rule:

```text
inputVerified
    = every consumed input evidence is VERIFIED

hasAuthoritativeBasis
    = at least one consumed input
      OR conversionValue > 0

produce VERIFIED
    = inputVerified
      AND hasAuthoritativeBasis
      AND conversion facts reconcile exactly
```

Evidence basis:

```text
CONSUMED_INPUTS
ABSORBED_CONVERSION
CONSUMED_INPUTS+ABSORBED_CONVERSION
```

Use the applicable value.

Do not mark a conversion-cost-enriched Produce as `CONSUMED_INPUTS` only.

Pool tracked value MUST continue to equal the actual `ProductionBalLot.TotalCost`.

---

## I. Posting invariant

### `ProductionPostingInvariant.AssertOutputVerifiedAsync(...)`

Add V4 checks.

For the current Daily Production forward posting:

```text
sum(PRODUCE.TotalCost)
=
Money(
    sum(CONSUME.TotalCost)
    + sum(original conversion cost facts)
)
```

Current code creates at most one forward `PRODUCE` movement for one Daily Production output. Add a fail-closed assertion when conversion facts exist and the output does not resolve to exactly one forward Produce movement.

Each forward conversion fact MUST match:

- company;
- branch;
- StockPosting;
- ProductionOutput;
- WorkOrder;
- RouteStep;
- WorkOrderOperation;
- `BasisQty == output.GoodQty`;
- `BasisUom == output.OutputUom`;
- expected source shape for its CostType.

No orphan or duplicate fact is allowed.

---

## J. Rollback / reversal

### `ProductionOutputService.Rollback.cs`

Do not recalculate rollback cost from current Product Definition or Work Order rates.

Before rollback `StampOutputLedgerFactsAsync(...)`:

1. identify the original forward conversion-cost facts for the output/original posting;
2. for each original fact append exactly one reversal fact;
3. copy:
   - CostType;
   - SourceLineKey;
   - source Work Order IDs;
   - BasisQty;
   - BasisUom;
   - RatePerOutputUnit;
   - CostAmount;
4. set:
   - `StockPostingId = rollback StockPosting`;
   - `ReversesFactId = original.Id`;
   - rollback timestamp/user.

Existing Produce reversal MUST continue using the exact original `ProductionBalLotMovement.TotalCost`. Do not add/subtract conversion value a second time.

The conversion-cost reversal fact is audit evidence for the amount already contained in the Produce reversal.

Idempotent rollback/replay MUST NOT create duplicate reversal facts.

---

## K. Work Order read-only cost display

### `IProductionWorkOrderService.cs`

Add to `ProductionWorkOrderOperationVm`:

```text
UtilitiesOverheadCostPerOutputUnit
OtherCostPerOutputUnit
```

Add to `ProductionWorkOrderMachineVm`:

```text
MachineRatePerHour
PlannedCostAmount
CostPerOutputUnit
```

### `ProductionWorkOrderService.cs`

Update:

- `MapOperation(...)`
- `MapMachine(...)`

### `PrWorkOrderEntry.razor`

Operations grid:

- show `Utilities / OH / unit`;
- show `Other cost / unit`.

Machines grid:

- show `Rate / hour`;
- show `Planned machine cost`;
- show `Machine cost / unit`.

Labour grid already exposes Rate and PlannedAmount; keep it.

These Work Order cost fields are read-only snapshot evidence.

Do not add manual cost editing to the Work Order in this plan.

# Transaction / Execution Order

For a V4 Daily Production forward post, preserve the existing transaction and lock architecture and execute:

1. authorize;
2. acquire existing branch stock transaction lock;
3. lock Production Output;
4. validate replay/status;
5. lock posting link;
6. lock Work Order;
7. validate Work Order status and snapshot;
8. lock route step/operation and execution sequence;
9. begin existing V2 stock-ledger posting context;
10. load frozen operation machine/labour cost rows;
11. calculate deterministic absorbed conversion-cost lines;
12. validate/lock material and prior-WIP pools exactly as today;
13. consume material/WIP and calculate exact existing consumed value;
14. calculate `producedCost = consumedValue + conversionValue`;
15. create/update production output pool with `producedCost`;
16. write Produce/Consume production movements;
17. append immutable forward `ProductionConversionCostFact` rows;
18. save;
19. run `StampOutputLedgerFactsAsync(...)`;
20. `ProductionPoolValuationService.RecordAsync(...)` validates the expanded value basis;
21. run existing `ProductionPostingInvariant.AssertOutputVerifiedAsync(...)` with new conversion-cost checks;
22. update execution projections/status/audit;
23. complete/seal existing stock posting;
24. commit.

Any failure MUST roll back the complete transaction, including new cost facts.

# Authority Rules

## Quantity authority

Unchanged:

- Daily Production `GoodQty`, material consume facts, UOM conversion and existing production movement quantities remain authoritative.

## Material/WIP monetary authority

Unchanged:

- existing verified production pool / inventory valuation chain remains authoritative;
- do not use item master purchase price or UI-entered display price.

## Labour authority

For V4 execution:

- frozen `ProductionWorkOrderLabour.Rate`;
- only `ContributesToPlan = true`;
- only `PER_OUTPUT_UNIT`.

## Machine authority

For V4 execution:

- frozen selected `ProductionWorkOrderMachine.CostPerOutputUnit`;
- derived during Work Order calculation;
- never recompute from the current machine master while posting.

## Utilities/overhead authority

- frozen `ProductionWorkOrderOperation.UtilitiesOverheadCostPerOutputUnit`.

## Other production-cost authority

- frozen `ProductionWorkOrderOperation.OtherCostPerOutputUnit`.

## Produced pool monetary authority

```text
verified consumed material/WIP value
+
immutable conversion-cost facts
```

## FG monetary authority

Unchanged:

- `ProductionBalLot.TotalCost`;
- `FinishedGoodReceiptMath.AllocateValue(...)`;
- `ExactTransferredValue`;
- inventory valuation facts.

# Invariants

For V4 forward Daily Production:

```text
ConversionCost
=
Σ ProductionConversionCostFact.CostAmount
```

```text
ProducedValue
=
ConsumedMaterialAndWipValue
+
ConversionCost
```

```text
ProductionBalLot.TotalCost
=
ProductionPoolValuation.TrackedValue
```

For every produced movement:

```text
ProductionValuationEvidence.Status == VERIFIED
```

before the posting may complete.

For FG:

```text
FG transferred value
=
the allocated value from the production pool
```

No independent labour/machine/OH recalculation is permitted in FG.

For rollback:

```text
Reversal conversion fact amount
=
Original conversion fact amount
```

and:

```text
Produce reversal value
=
Original Produce value
```

All monetary equality uses the existing `StockLedgerPrecision.Money` six-decimal authority.

# Rollback / Reversal

MUST preserve existing rollback guards:

- downstream Daily Production dependency;
- active production-pool dependency;
- later consume/FG movement protection;
- posting-link replay/idempotency;
- Work Order serialization.

Forward facts remain immutable.

Rollback MUST append linked reversal facts.

Rollback MUST NOT:

- delete original conversion facts;
- edit original conversion facts;
- recalculate historical rates;
- fetch current Product Definition cost rates;
- fetch current machine master hourly cost;
- separately modify the pool for conversion cost after the existing Produce reversal.

If reversal facts cannot be proven complete and exact, FAIL CLOSED and do not commit.

# Concurrency / Locking

Preserve:

- `BranchStockTransactionLock`;
- current EF transaction boundary;
- `LockOutputAsync`;
- `LockOutputPostLinkAsync`;
- `LockWorkOrderAsync`;
- route/operation locks;
- deterministic material/pool lock order;
- existing posting request replay protection.

The Work Order is locked before cost snapshot rows are read.

Released/InProgress Work Order cost snapshot data MUST be treated as immutable by application services.

Do not introduce a new global locking mechanism.

The unique conversion-fact indexes are an additional duplicate-post/reversal safety net, not a replacement for existing posting-link idempotency.

# Tests

## Product Definition

### `PrProductDefServiceTests.cs`

Extend `Versioned_route_machine_and_labour_round_trip_and_clone_with_bom`:

Assert utilities/overhead and other per-unit rates:

- save;
- reload;
- clone/new version;
- preserve exact six-decimal values.

Add negative-rate validation tests.

### `ProductDefinitionAuthoringTests.cs`

Assert authored rates persist with route/operation source identity.

### `ProductDefinitionPhase1SchemaTests.cs`

Assert:

- columns exist;
- precision/scale = `(19,6)`;
- non-negative constraints exist.

## Work Order calculation

### `WorkOrderQuantityCalculatorTests.cs`

Add:

1. machine cost uses:
   `Setup + Conversion + PlannedCycleCount × CycleSeconds`;
2. queue time does not affect cost;
3. parallel machine count changes elapsed slots but does not erase total machine-cycle cost;
4. zero hourly rate produces zero machine cost;
5. machine cost per output unit is rounded with `StockLedgerPrecision.Money`;
6. existing labour calculation remains unchanged.

## Snapshot builder

### `WorkOrderSnapshotBuilderTests.cs`

Assert:

- operation utilities/overhead copied;
- other cost copied;
- selected and alternate machine derived cost values are calculated;
- labour ownership/contribution remains unchanged;
- selecting the intended machine yields its frozen rate.

## Snapshot hashes

### `WorkOrderSnapshotHasherTests.cs`

Mandatory compatibility tests:

1. V1 output unchanged by new fields;
2. V2 output unchanged by new fields;
3. V3 output unchanged by new fields;
4. V4 changes when utilities/overhead changes;
5. V4 changes when other cost changes;
6. V4 changes when derived machine cost changes;
7. definition source V1/V2 ignore the new fields;
8. definition source V3 changes when the new authored fields change;
9. same V4 content hashes identically regardless of collection insertion order.

### `ProductionDailyOutputSchemaTests.cs`

Update current-hash assertions from V3 to V4.

Keep snapshot **format** assertion at V3.

## Work Order schema

### `ProductionWorkOrderSchemaTests.cs`

Assert new operation/machine fields:

- mapped;
- `(19,6)`;
- non-negative.

## Daily Production forward cost

### `ProductionOutputEntryServiceTests.cs`

Add an end-to-end V4 scenario, for example:

```text
material/WIP cost        800.000000
labour                    80.000000
machine                   50.000000
utilities/OH              20.000000
other                     10.000000
-------------------------------
produced value            960.000000
```

Assert:

- production pool `TotalCost == 960`;
- Produce movement `TotalCost == 960`;
- conversion facts total `160`;
- facts contain the expected types and source IDs;
- pool valuation is VERIFIED;
- tracked value equals pool value.

Additional required tests:

- partial production on the same WO absorbs per GoodQty;
- Scrap/Reject/Hold do not receive conversion cost in V1;
- material consumption still reflects processed quantity as before;
- V3 Released/InProgress Work Order continues legacy material/WIP-only costing;
- V4 labour/machine-only operation with non-zero conversion cost can create a verified pool;
- V4 operation with no consumed input and no non-zero conversion cost is blocked;
- unsupported contributing labour basis fails closed;
- duplicate selected machine state fails closed.

## Rollback

Extend `ProductionOutputEntryServiceTests.cs`:

- forward post creates conversion facts;
- rollback creates exact linked reversal facts;
- pool value after rollback matches pre-post value;
- original facts remain;
- second rollback/replay creates no duplicate reversal;
- later/downstream consumption still blocks rollback exactly as today.

Extend existing:

`Partial_production_consumption_and_rollback_preserve_exact_material_and_wip_costs`

to prove existing material/WIP exactness is unchanged when conversion cost is zero.

## Posting atomicity / valuation gate

### `ProductionCostGateTests.cs`
### `ProductionStockLedgerPostingTests.cs`

Assert:

- produce cannot seal when conversion facts do not reconcile;
- mismatched fact amount fails;
- missing expected fact fails;
- wrong tenant/output/operation linkage fails;
- injected failure after valuation leaves no conversion facts, no sealed posting and no partial pool change;
- replay creates no duplicate facts.

## SQL Server

### `ProductionStockLedgerSqlServerTests.cs`

Assert on real SQL Server:

- fact precision is `(19,6)`;
- unique forward fact index works;
- one reversal per original fact;
- invalid CostType/source shape rejected;
- fact update/delete rejected by application immutability guard where exercised;
- transaction rollback removes partially inserted facts.

## FG regression

### `FinishedGoodReceiptTests.cs`

Seed/produce a pool whose `TotalCost` includes conversion cost and prove:

- FG allocation transfers the exact full pool value;
- partial FG receipt allocates proportionally using existing `AllocateValue`;
- final depletion leaves zero quantity and zero value;
- FG rollback restores the exact full production pool value;
- no FG costing formula is added.

## Inventory valuation methods

### `InventoryValuationServiceTests.cs`

Preserve existing FIFO exact transferred-value test.

Add/extend coverage so an FG `ExactTransferredValue` representing full absorbed production cost:

- is used as `PRODUCTION_ACTUAL` under Moving Average/FIFO as applicable;
- is not replaced by balance/master price.

For STANDARD costing, verify the existing standard-cost valuation path remains authoritative for inventory and the actual-vs-standard production variance uses the richer actual production transfer value. Do not replace `ProductionStandardCostVariance`.

# Implementation Order

## STEP 1 — Add failing focused tests

Add hash compatibility, Product Definition round-trip, machine calculation and Daily Production produced-value tests before implementation.

## STEP 2 — Schema/model additions

Implement:

- two Product Definition operation rates;
- two Work Order operation snapshot rates;
- two Work Order machine derived-cost fields;
- new immutable conversion-cost fact;
- EF configurations;
- DbContext set/immutability;
- SQL upgrade/preflight/reset changes.

## STEP 3 — Product Definition flow

Update VM, validation, persistence, clone, map and Product Definition UI.

## STEP 4 — Work Order snapshot/calculation

Copy operation rates and calculate frozen machine cost.

Update Work Order VM/map/display.

## STEP 5 — Version hashes

Add definition source V3 and snapshot hash V4 without modifying older algorithms.

Update release/current hash behavior and tests.

## STEP 6 — Absorbed-cost calculator

Implement the pure calculator and unit tests.

## STEP 7 — Daily Production forward integration

Integrate V4 cost facts and `producedCost`.

Preserve V3 legacy execution.

## STEP 8 — Valuation evidence and invariants

Require exact:

`Produce = Consume + Conversion Facts`.

## STEP 9 — Rollback

Append exact reversal facts and verify no double reversal of pool value.

## STEP 10 — FG / valuation regression

Prove full value transfers through FG and inventory valuation unchanged.

## STEP 11 — SQL Server and full regression

Run focused tests, SQL Server tests, full solution build/test and deployment preflight.

# Regression Areas

Must verify unchanged unless explicitly enhanced:

- Inventory Moving Average;
- Inventory FIFO;
- Inventory Standard Cost;
- `ProductionStandardCostVariance`;
- Material Issue posting/rollback;
- Daily Production material quantity/variance logic;
- partial material/WIP consumption;
- process handoff;
- production pool dependency graph;
- Work Order release/reopen/change-definition behavior;
- machine alternative selection;
- Production Output rollback;
- FG receipt posting/rollback;
- FG exact transferred value;
- stock ledger sealing;
- period/closed-month guards;
- tenant company/branch isolation;
- reset/test database scripts.

# Do-Not Rules

DO NOT:

- calculate labour from payroll salary;
- read current Product Definition rates during Daily Production;
- read current machine master cost during Daily Production;
- add actual runtime entry in this plan;
- charge QueueSeconds as machine cost;
- charge conversion cost against Scrap/Reject/Hold in V1;
- modify existing material/WIP costing formulas;
- change old snapshot/hash algorithms;
- silently upgrade an already Released/InProgress V3 WO to V4 costing;
- recalculate costs in Finished Good Receipt;
- modify `FinishedGoodReceiptMath.AllocateValue(...)`;
- update/delete posted cost facts;
- weaken current rollback/downstream guards;
- bypass `StockLedgerPrecision.Money`;
- introduce a second production valuation authority;
- add unrelated UI/refactoring.

# Acceptance Criteria

- [ ] Product Definition supports non-negative Utilities/OH and Other cost per output unit.
- [ ] Product Definition save/reload/version clone preserves those rates.
- [ ] Work Order snapshots freeze the new rates.
- [ ] Selected machine has deterministic frozen `PlannedCostAmount` and `CostPerOutputUnit`.
- [ ] Labour cost uses only frozen contributing `PER_OUTPUT_UNIT` Work Order labour.
- [ ] Daily Production user enters no new cost information.
- [ ] V4 produced value equals exact consumed material/WIP value plus immutable conversion-cost facts.
- [ ] Conversion cost is absorbed by `GoodQty` only in V1.
- [ ] Production pool tracked value equals production pool balance value.
- [ ] Produce valuation evidence is VERIFIED only when the complete value basis reconciles.
- [ ] Existing released/in-progress V3 WOs continue legacy material/WIP-only execution.
- [ ] New/refreshed/re-released WOs use V4 costing.
- [ ] Old hash versions remain reproducible and unchanged.
- [ ] Forward conversion-cost facts are immutable.
- [ ] Rollback appends exact linked reversal facts.
- [ ] Rollback does not double-subtract conversion cost.
- [ ] Posting/replay cannot create duplicate cost facts.
- [ ] Transaction failure leaves no partial conversion-cost evidence.
- [ ] Finished Good Receipt continues using the existing production-pool allocation logic.
- [ ] FG receives the full absorbed production value without recalculating components.
- [ ] Moving Average and FIFO continue to consume FG `ExactTransferredValue`.
- [ ] Standard Cost remains inventory authority when configured, with actual production value retained for variance.
- [ ] SQL upgrade and preflight scripts are idempotent.
- [ ] Transaction reset script clears the new transaction fact table in correct FK order.
- [ ] Focused production/planning/inventory tests pass.
- [ ] SQL Server production ledger tests pass.
- [ ] Full solution build and regression suite pass.

# Approval Status

APPROVED FOR IMPLEMENTATION
