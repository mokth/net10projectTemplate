# Deep Technical & Functional Review — ERP Production Module

**Repository:** `mokth/net10projectTemplate`  
**Branch reviewed:** `production`  
**Baseline commit:** `e1a41fed8114f3373bcabe99bfdcd8931b49bf40`  
**Review date:** 2026-09-30  
**Scope:** Product Definition, Work Order, BOM/quantity logic, routing, machine/labour standards, forward/backward scheduling, calendars, material execution readiness, production output, WIP, partial completion, costing foundation, state/lifecycle, audit, UI/UX, concurrency, and future planner readiness.  
**Decision status:** **READY FOR APPROVAL — Phases 1A–1D only, subject to the gates in this document**  
**Approval basis:** Baseline commit `e1a41fed8114f3373bcabe99bfdcd8931b49bf40`; scope or architectural changes after approval require a recorded change decision.

---

# Executive Summary

The current `production` branch is **substantially stronger than a simple migration of the old ERP production module**. The project already has a modern versioned Product Definition model, an explicit Work Order snapshot aggregate, strict UOM conversion, route/process hierarchy, machine alternatives at the data-model level, calendar-aware forward/backward scheduling, snapshot hashing, row-version concurrency, audit events, change-order scaffolding, and SQL Server concurrency tests.

The current system is best described as:

> **A strong Production Definition + Work Order planning/release foundation, but not yet a production-execution module.**

That distinction is important. A Work Order can currently be created from an active Product Definition, calculate material quantities and machine cycles, calculate a calendar-aware schedule, select an alternate machine already present in the snapshot, substitute an alternate BOM line from the same definition revision, and be released as a frozen execution snapshot. However, actual production execution is intentionally blocked by `ProductionExecutionGate.IsOpen == false`.

The architecture should **not be rebuilt**. The Product Definition and Work Order snapshot foundations are good and should be retained. The next work should concentrate on closing specific gaps before material posting and shop-floor execution are enabled.

The most important findings are:

1. **Product Definition data model is ahead of the UI.** `PrBomMachineOption` supports multiple machine alternatives and priorities, but `PrProductDefEntry` currently forces a single machine per process. This prevents planners from authoring the alternate-machine capability that the Work Order snapshot and selection service already support.
2. **Labour model is ahead of the current authoring path.** `PrBomLabourRequirement` supports operation-level or machine-specific labour, headcount, setup/run minutes, and multiple cost bases, but `PrProductDefService` / `PrProductDefEntry` currently use the older one-labour-per-machine `PrBomLabourStandard` path.
3. **Yield/loss semantics are not finished.** `PrBomRouteStep.YieldPercent` exists, but Product Definition saving currently hard-codes it to `100m`; the Work Order route snapshot does not store yield, and `WorkOrderQuantityCalculator` does not use route yield, `SetupLossQty`, or `OperationLossQty`. This can under-plan upstream material or WIP when real process loss exists.
4. **The current scheduler is calendar-aware, not finite-capacity.** It honors machine calendars, shift groups, breaks, non-working days, and preventive maintenance, but it does not subtract time already occupied by other Work Orders. Two Work Orders can therefore be planned over the same machine interval.
5. **Non-machine scheduling is too coarse.** `WorkOrderCalendarProvider.LoadPlantAsync()` maps company working days to a hard-coded `08:00–17:00` shift with no breaks. This is acceptable as a temporary fallback, but not as the long-term calendar for manual, inspection, packing-without-machine, waiting, or subcontract operations.
6. **A specific backward-scheduling defect exists.** In the outer route-step grouping logic of `WorkOrderScheduleCalculator.ScheduleAsync`, the backward boundary handling can discard the earliest start from a previously scheduled parallel route step. Existing tests cover backward scheduling and forward parallel scheduling, but not backward + parallel route steps.
7. **Execution source-of-truth transactions do not exist yet.** Work Order material and operation entities contain projection fields such as `ReservedQty`, `IssuedQty`, `ConsumedQty`, `GoodQty`, `ScrapQty`, `RejectQty`, `ReworkQty`, etc., but no production material issue/return, production output, WIP transfer, or FG receipt transactional entities/services currently populate them.
8. **The Inventory module already contains the transaction-safety pattern Production should reuse.** Inventory posting uses database transactions, lock/re-read, lot/location handling, negative-stock checks, history posting, idempotency-like duplicate protection, rollback, and SQL Server concurrency tests.
9. **WIP structure is partially excellent but execution is incomplete.** The Product Definition can identify `INTERNAL_ROUTE_WIP`, producer route steps, and route outputs. However, the Work Order route-step snapshot currently does not preserve `OutputType`, so execution cannot yet reliably distinguish `WIP_STOCKED`, `WIP_NONSTOCK`, and `FINISHED_GOODS`.
10. **Future finite machine planning can be added without a major redesign** if a resource-allocation / machine-booking layer is added as a separate transactional planning table rather than changing the existing snapshot tables.

The recommended implementation direction is:

```text
Keep and harden the existing Product Definition + Work Order snapshot foundation
    ↓
Fix authoring gaps and quantity/scheduling correctness
    ↓
Add production execution source transactions
    ↓
Connect those transactions atomically to Inventory
    ↓
Add partial output / WIP / FG receipt lifecycle
    ↓
Add reservation + machine load planning
    ↓
Add actual-vs-planned + costing
    ↓
Only then add finite-capacity / drag-drop / APS features
```

## Approval recommendation

**Approve implementation of Phases 1A–1D as the minimum production-safe release.** The existing Product Definition revision model and version-2 Work Order snapshot architecture remain the foundation; approval does not authorize their replacement.

Approval includes:

- planning-foundation corrections in Phase 1A;
- auditable material issue/return with atomic Inventory posting in Phase 1B;
- production reporting and partial WIP/finished-goods receipt in Phase 1C;
- controlled Work Order lifecycle completion in Phase 1D;
- database migrations, UI changes, automated tests, audit, idempotency, concurrency controls, projection rebuilds, and operating documentation required by those phases.

Approval excludes:

- material reservation/ATP and shortage allocation;
- cross-Work-Order finite-capacity scheduling or automatic optimization;
- costing/accounting redesign, advanced overhead, MRP, OEE, MES, and serial manufacturing;
- generic routing DAGs, sequence-dependent setup, and other Phase 2/3 items;
- automatic migration or reinterpretation of ambiguous legacy loss data.

The execution gate must remain closed through Phases 1A and 1B. It may be enabled only after Phases 1C and 1D meet the release gate, Inventory integration is proven in the same database transaction, reversal paths pass, and the named business owner accepts UAT.

No implementation may bypass posted source transactions by directly editing Work Order execution projections.

---

# A. Current Architecture Review

## A1. Where the Production module actually lives

There is no `ErpWeb.UI/Production` folder on the reviewed branch. The production functionality is currently split into:

```text
ErpWeb.UI/Planning/
    Masters/
        PrProductDefEntry
        PrProductDefList
        PrMachineList
        PrProcessList
        PrWorkCentreList
        PrShift*
        PrCompanyCalendar
        PrMachineShiftCalendar
        PrPreventiveList
        ...
    WorkOrders/
        PrWorkOrderEntry
        PrWorkOrderList

ErpWeb.Core/Planning/
    Product Definition / BOM / master / calendar services

ErpWeb.Core/Production/
    Work Order snapshot
    quantity calculation
    scheduling
    release readiness
    Work Order lifecycle
    schedule/calendar providers
    snapshot hashing
    execution gate

ErpWeb.Model/Entities/Planning/
    Product Definition / routing / machine / calendar entities

ErpWeb.Model/Entities/Production/
    Work Order snapshot aggregate
    audit/change/posting-link entities
```

This split is reasonable. Product Definition and production reference masters are planning/master data; Work Order snapshot/execution logic is correctly separated into `ErpWeb.Core/Production`.

---

## A2. Product Definition — current implementation

### Existing master/revision model

The current Product Definition is not simply the old mutable `PrDef*` structure. The active design centers on `PrBomHdr`:

```text
PrBomHdr
    CompanyCode
    ProdCode
    Version
    Status
    EffectiveFrom / EffectiveTo
    BaseQty / BaseUom
    Validation metadata
    RowVersion
```

This is a good modern foundation because a product can have versioned manufacturing definitions and a Work Order can resolve the correct ACTIVE revision by an effective date.

The source loader does not silently pick a revision if the effective-date data is ambiguous. `ProductDefinitionSnapshotLoader.ResolveRevisionAsync()` requires exactly one ACTIVE revision whose effective interval contains the requested date. Zero matches and multiple matches are hard failures.

That is the correct behavior for production history.

### Current routing hierarchy

The newer Product Definition model contains an explicit route occurrence:

```text
PrBomHdr
  └─ PrBomRouteStep
       ├─ WorkCentreCode
       ├─ StageSequence
       ├─ OutputItemCode
       ├─ OutputType
       ├─ StandardOutputQty
       ├─ OutputUom
       ├─ YieldPercent
       └─ PrBomOperation
            ├─ ProcessSequence
            ├─ ProcessType
            ├─ StandardDurationMinutes
            ├─ IsFinalOperation
            ├─ PrBomMachineOption
            ├─ PrBomLabourRequirement
            └─ PrDefBOM material lines
```

This is materially better than a flattened legacy route because the same Work Center can occur more than once and route occurrences have their own identity.

### Parallel stages and processes

The model intentionally allows equal `StageSequence` values for route steps. The Work Order scheduler interprets equal stage sequence as parallel route steps.

Within a route step, equal `ProcessSequence` values represent parallel operations.

This is a practical SME-friendly way to represent simple parallel production without immediately introducing a full arbitrary dependency graph.

### Internal WIP dependency

A `PrDefBOM` line can declare:

```text
SupplySource = INTERNAL_ROUTE_WIP
ProducingRouteStepId = ...
```

This makes the producer of a consumed WIP item explicit. The Work Order snapshot repoints the Product Definition route-step reference to the copied Work Order route-step occurrence.

The release/readiness logic validates missing producers, ambiguous producers, producer/consumer sequence conflicts, and dependency cycles.

This is strong design and should be retained.

---

## A3. Product Definition — current weaknesses

### A3.1 Alternate-machine authoring is not actually available in the current UI

The entity/service model supports:

```text
PrBomMachineOption
    MachineCode
    IsPrimary / IsDefault
    Priority
    CycleSeconds
    OutputPerCycle
    ConversionSeconds
    SetupSeconds
    QueueSeconds
    MachineRatePerHour
    ParallelMachineCount
```

`PrProductDefService.ValidateOperationsAsync()` can validate multiple machine rows, unique priorities, and exactly one primary/default.

However, `PrProductDefEntry.razor.cs` currently prevents this:

- `SaveMachineAsync()` sets the saved machine to primary.
- If an operation already has a machine, adding another one returns:
  `This process already has its machine. Edit or remove it before assigning another.`
- The UI warns when more than one legacy machine exists and tells the user to keep one and remove the others.

This conflicts with the intended data model and the Work Order capability to snapshot multiple alternatives.

**Recommendation: MUST fix before relying on machine planning.**

The Product Definition machine tab should become a machine-options grid where one option is default and the others are approved alternatives.

---

### A3.2 Labour authoring is also restricted

The database/domain already contains `PrBomLabourRequirement` with:

```text
OperationId
MachineOptionId optional
LabourCode
RequiredHeadcount
SetupMinutes
RunMinutes
CostRate
CostBasis
```

This is the correct direction.

But the current Product Definition service/UI does not expose this structure. The current UI edits `PrBomLabourStandard`, attached to a machine, and limits a machine to one labour/operator.

That prevents practical cases such as:

```text
Operation: Assembly
    2 × Operator
    1 × Technician

Operation: Packing
    3 × Packing staff

Machine M01
    1 × Machine operator

Machine M02
    2 × Machine operator
```

**Recommendation: MUST expose `PrBomLabourRequirement` and retire the one-labour-per-machine authoring restriction for new definitions.**

---

### A3.3 Yield is modeled but not operational

`PrBomRouteStep` has `YieldPercent`, but Product Definition saving currently creates route steps with:

```csharp
YieldPercent = 100m
```

There is no current Product Definition UI field for route yield.

The Work Order route-step entity does not contain yield and the quantity calculator does not apply it.

This creates a hidden risk: a user may believe `SetupLossQty`, `OperationLossQty`, or future yield settings affect planning, while the actual requirements stay based on nominal output only.

A clear policy is required:

```text
Required good output
    ↓
divide by route yield
    ↓
required upstream input
    ↓
apply BOM standard
    ↓
apply material-specific scrap
```

or, if BOM standards are already grossed up for loss, the system must explicitly state that and not apply yield again.

**Recommendation: MUST define and implement one policy before production execution.**

---

### A3.4 Route output type is not fully authorable

`PrBomRouteStep.OutputType` supports:

```text
WIP_STOCKED
WIP_NONSTOCK
FINISHED_GOODS
```

But current Product Definition saving derives it automatically:

```text
if OutputItemCode == finished product
    FINISHED_GOODS
else
    WIP_STOCKED
```

The user cannot currently mark a route output as non-stock WIP.

This matters because shop-floor execution must know whether route output:

- creates a stock receipt,
- creates only an internal WIP quantity,
- or creates final finished goods.

**Recommendation: SHOULD expose route output type explicitly, with sensible defaults.**

---

## A4. Work Order — current architecture

The current Work Order is a true snapshot aggregate, not a live view of Product Definition.

`ProductionWorkOrder` contains:

- source Product Definition revision identity;
- snapshot revision and hash;
- definition-source hash;
- planned quantity;
- planned route/process/machine/material/labour rows;
- schedule anchor and direction;
- planned start/completion;
- production quantity projections;
- release/cancel metadata;
- audit events;
- change orders;
- future posting links;
- row version.

The child hierarchy is:

```text
PrWorkOrder
  └─ PrWorkOrderRouteStep
       └─ PrWorkOrderOperation
            ├─ PrWorkOrderMaterial
            ├─ PrWorkOrderMachine
            │    └─ PrWorkOrderLabour
            └─ operation-level PrWorkOrderLabour
```

This is the correct separation between:

```text
MASTER/TEMPLATE
    Product Definition

TRANSACTION/JOB SNAPSHOT
    Work Order
```

---

## A5. Work Order snapshot behavior

The current snapshot behavior is one of the strongest parts of the module.

`WorkOrderSnapshotBuilder`:

- resolves a single Product Definition revision;
- copies route steps;
- copies operations;
- copies all eligible machine alternatives;
- selects exactly one default machine for scheduling;
- copies default BOM requirements only;
- retains alternate BOMs in the Product Definition for explicit Draft substitution;
- repoints internal WIP producer links;
- performs strict quantity/UOM calculations;
- stores source revision identity;
- computes a definition-source hash;
- computes a Work Order snapshot hash.

After a Work Order is saved, later master changes do not silently alter it.

Draft refresh is explicit:

```text
Preview Refresh
    ↓
show Added / Removed / Changed
    ↓
user confirms with reason
    ↓
replace Draft snapshot
    ↓
increment snapshot revision
    ↓
recalculate schedule
```

After Release, Draft editing is blocked.

This matches the required historical-snapshot principle very well.

---

## A6. BOM and quantity calculation — current behavior

`WorkOrderQuantityCalculator` uses decimal arithmetic and strict UOM conversion.

### Route quantity

```text
RoutePlannedQty
= WorkOrderQty
  × RouteOutputBaseQty
  ÷ ProductDefinitionBaseQty
```

Stored quantities are rounded to the Inventory quantity scale with `MidpointRounding.AwayFromZero`.

### Material requirement

The current calculation is effectively:

```text
Operation output
    ↓ convert into BOM denominator UOM

RequiredQty
= OperationOutputInBomBasis
  × ComponentQtyPerParent
  ÷ BomOutputQty
  × (1 + ScrapPercent / 100)
```

Tolerance is deliberately not added to requirement quantity. It is treated as a variance/issue-control concept.

That is correct.

### Pack-size / fractional ratio

The current model supports either:

```text
BaseQty = 5 FG
StdQty  = 1 RM
```

or an equivalent per-unit ratio.

Example:

```text
WO = 100 FG
BOM BaseQty = 5 FG
RM StdQty = 1 PCS

Required = 100 / 5 × 1 = 20 PCS
```

This is preferable to forcing users to enter `0.2 PCS` unless that is genuinely how they want to author the BOM.

### UOM safety

UOM conversion is strict. The system does not silently assume a factor when the source and target UOM differ.

A missing conversion blocks the calculation/release path.

This is production-safe behavior.

---

## A7. Machine cycle calculation — current behavior

For a discrete machine option:

```text
RequiredMachineOutput
    = operation planned output converted to machine output UOM

CycleCount
    = Ceiling(RequiredMachineOutput / OutputPerCycle)

CycleSlots
    = Ceiling(CycleCount / ParallelMachineCount)

RunMinutes
    = CycleSlots × CycleSeconds / 60
```

Scheduling duration is then:

```text
SetupSeconds
+ ConversionSeconds
+ QueueSeconds
+ RunMinutes
```

Only the selected machine option drives the schedule.

This is correct for the currently defined **discrete cycle** model.

Important current limitations:

- no production/machine efficiency factor;
- no explicit cleanup/post-run time;
- no transfer time between stages;
- no batch-size/changeover policy beyond one setup per Work Order operation;
- no continuous/rate-based machine mode;
- no sequence-dependent setup.

Those are not reasons to redesign the current engine. They are additive future capabilities.

---

## A8. Scheduling — current implementation

### Forward scheduling

Supported.

The system anchors on `ScheduleAnchorDateTime` / planned start and schedules route sequence groups forward.

Equal stage/process sequence values share the same anchor and run in parallel.

The next sequence group waits for the slowest previous member.

### Backward scheduling

Supported in principle.

The system anchors on the required/planned completion boundary and consumes calendar time backward.

However, one defect should be corrected:

In `WorkOrderScheduleCalculator.ScheduleAsync`, the outer route-step-group accumulator uses completion first and then start when direction is backward. With multiple parallel route steps, this can replace an earlier start that should have remained the group boundary.

Recommended correction:

```text
Forward group boundary  = maximum completion
Backward group boundary = minimum start
```

and add explicit tests:

```text
Backward + parallel route steps
Backward + parallel route steps + next predecessor stage
Backward + different duration parallel route steps
```

### Machine calendar behavior

Machine-based operations use:

- `PrShiftCalendar`
- shift groups
- shift master
- normalized shift breaks
- working/off days
- preventive maintenance windows

The calendar scheduler also handles overnight shifts and break interval subtraction.

This is a strong scheduling foundation.

### Non-machine operation calendar

Non-machine operations use the company `PrCalendar`, but `WorkOrderCalendarProvider` turns every working date into one synthetic:

```text
08:00 – 17:00
no breaks
```

This should be upgraded before manual/labour-intensive planning becomes operational.

---

## A9. Finite capacity — current limitation

The scheduler asks:

```text
When is this machine calendar available?
```

but does not yet ask:

```text
What other released/planned Work Orders already occupy this machine?
```

Therefore current scheduling is:

> **calendar-constrained but infinite-capacity across Work Orders**

It is not yet a machine-loading scheduler.

This is acceptable for the current foundation as long as the UI does not imply that planned dates are guaranteed against other orders.

A future finite-capacity layer can be added with a machine allocation table without replacing the Work Order snapshot.

---

## A10. Current Work Order status/lifecycle

Current statuses are:

```text
DRAFT
RELEASED
IN_PROGRESS
COMPLETED
CLOSED
CANCELLED
```

But the currently implemented UI/service lifecycle mainly covers:

```text
Create/Edit Draft
Recalculate
Refresh Definition
Change Draft machine
Substitute Draft material
Release
Cancel Draft
```

The service interface does not yet expose normal production commands for:

- Start production;
- Hold/resume;
- report output;
- issue/return material;
- receive WIP/FG;
- complete;
- close.

The status constants are ahead of the implemented execution workflow.

---

## A11. Execution is intentionally gated

`ProductionExecutionGate` explicitly has:

```text
IsOpen => false
```

and blocks execution until reservation, issue, WIP, receipt, costing, completion, atomic posting, idempotency, and related contracts are approved.

The Work Order UI exposes an **Execution Gate** tab stating that material issue/return is pending the atomic inventory-posting adapter.

This is a good safety decision. The gate should remain closed until the Phase 1 execution contracts below are implemented and tested.

---

# B. Current Production Flow

## B1. Actual current flow

```text
Product Definition Draft
    ↓
Validate / Activate revision
    ↓
ACTIVE versioned Product Definition
    ↓
Work Order Preview / Create Draft
    ↓
Resolve exact Product Definition revision by as-of date
    ↓
Snapshot:
    Route Steps
    Operations
    Default BOM requirements
    All machine alternatives
    Labour currently supported by snapshot contract
    ↓
Calculate:
    Route quantities
    BOM requirements
    UOM conversions
    Machine cycles
    Labour planned amount
    ↓
Schedule:
    Forward or Backward
    Machine calendar
    Shift group / breaks
    Preventive maintenance
    Route/process parallel groups
    Internal-WIP dependency
    ↓
Save Draft
    ↓
Optional:
    Select alternate machine
    Substitute alternate BOM
    Recalculate schedule
    Explicitly refresh Product Definition
    ↓
Release
    ↓
Frozen execution snapshot
    ↓
[EXECUTION GATE CLOSED]
```

## B2. Required full production flow

```text
Production Definition
    ↓
Work Order Draft
    ↓
Snapshot + planning
    ↓
Release
    ↓
Material availability / reservation
    ↓
Material Issue
    ↓
Start Production
    ↓
Operation / Shift Output
    ↓
WIP transfer or WIP receipt
    ↓
Partial FG receipt
    ↓
Additional material/output cycles as required
    ↓
Final completion
    ↓
Material return / variance reconciliation
    ↓
Close Work Order
```

### Current coverage

| Step | Current status |
|---|---|
| Product Definition revisioning | Implemented |
| Route/BOM/machine snapshot | Implemented |
| BOM quantity calculation | Implemented, except yield/loss semantics |
| Machine cycle calculation | Implemented for discrete cycle mode |
| Forward schedule | Implemented |
| Backward schedule | Implemented, one parallel-group defect to fix |
| Shift/break/machine calendar | Implemented |
| Preventive maintenance exclusion | Implemented |
| Cross-WO machine load | Not implemented |
| Material reservation | Not implemented |
| Material issue/return | Not implemented |
| Production output | Not implemented |
| WIP actual movement | Not implemented |
| Partial FG receipt | Not implemented |
| Reject/scrap/rework transaction | Not implemented |
| Completion/close commands | Not implemented |
| Actual costing | Not implemented |

---

# C. Database Relationship Diagram

## C1. Existing Product Definition

```text
PrBomHdr
│
├── PrBomRouteStep
│    │
│    └── PrBomOperation
│         │
│         ├── PrBomMachineOption
│         │     ├── PrBomLabourStandard      [legacy-compatible authoring path]
│         │     └── PrBomLabourRequirement   [target richer labour contract]
│         │
│         ├── PrBomLabourRequirement         [operation-level]
│         │
│         └── PrDefBOM
│               └── ProducingRouteStepId → PrBomRouteStep
│
└── PrDefBOM
```

Important Product Definition concepts already represented:

```text
Revision
Effective dates
Work Center occurrence
Stage sequence
Parallel stage sequence
Process sequence
Parallel process sequence
Machine option
Default machine
Machine priority
Cycle seconds
Output per cycle
Setup / conversion / queue
Parallel machine count
Material standard
Scrap %
Alternate material group
Issue method
Supply source
Internal WIP producer
Output type
Yield field
Labour requirement entity
```

---

## C2. Existing Work Order snapshot

```text
PrWorkOrder
│
├── PrWorkOrderRouteStep
│    │
│    └── PrWorkOrderOperation
│         │
│         ├── PrWorkOrderMachine
│         │      └── PrWorkOrderLabour
│         │
│         ├── PrWorkOrderLabour
│         │
│         └── PrWorkOrderMaterial
│
├── PrWorkOrderAudit
├── PrWorkOrderChange
│      └── PrWorkOrderChangeLine
│
└── PrProductionPostingLink
```

`PrProductionPostingLink` is already the correct place to retain an idempotency and reversal relationship between future Production documents and Inventory postings.

---

## C3. Recommended execution extension

```text
PrWorkOrder
│
├── existing frozen snapshot
│
├── PrProductionMaterialMovement
│      └── PrProductionMaterialMovementLine
│             └── PrProductionPostingLink
│                    └── IvTrxBatch / IvTrxHistory
│
├── PrProductionReport
│      └── PrProductionDisposition
│
├── PrProductionReceipt
│      └── PrProductionReceiptLine
│             └── PrProductionPostingLink
│                    └── IvTrxBatch / IvTrxHistory
│
├── PrMaterialReservation                  [Phase 2 unless stock contention requires earlier]
│
├── PrWipTransfer                          [Phase 2 or only when non-stock WIP requires it]
│
└── PrMachineScheduleAllocation            [Phase 2 finite-capacity]
```

The existing quantity fields on `PrWorkOrder`, `PrWorkOrderOperation`, and `PrWorkOrderMaterial` should remain **projections**, rebuilt from posted source transactions.

They should not become the only source of truth.

---

# D. Gap Analysis

| Area | Current | Problem | Recommended improvement | Priority |
|---|---|---|---|---|
| Product Definition revisions | Strong | None fundamental | Keep version/effective-date model | MUST KEEP |
| Route-step model | Strong | OutputType/Yield not fully authored | Expose and validate both | MUST |
| Parallel work centers | Supported | Simple sequence-group model only | Keep; add generic precedence only if real cases require it | MUST KEEP |
| Parallel processes | Supported | Same as above | Keep current sequence grouping | MUST KEEP |
| Alternate machines — model | Supported | UI prevents authoring >1 | Replace singular editor with machine-options grid | MUST |
| Default machine | Supported | UI forces every saved machine primary | One default, many alternatives | MUST |
| Labour requirements — model | Exists | Current UI/service uses legacy one-labour-per-machine path | Author/persist `PrBomLabourRequirement` | MUST |
| Labour cost bases | Product Definition model supports several | WO snapshot/calculator effectively supports PER_OUTPUT_UNIT only | Support PER_HOUR, PER_OPERATION, PER_OUTPUT_UNIT end-to-end | MUST |
| BOM standard quantity | Strong | No fundamental issue | Keep BaseQty/StdQty model | MUST KEEP |
| Pack-size ratio | Supported | User guidance could improve | Show formula preview | SHOULD |
| BOM alternates | Strong Draft substitution model | Only defaults become requirements by design | Keep exact-revision substitution | MUST KEEP |
| BOM scrap | Implemented | None fundamental | Keep | MUST KEEP |
| Route yield | Field exists | Hard-coded 100; not snapshotted/calculated | Implement agreed yield semantics | MUST |
| Setup/operation loss | Snapshotted | Not applied in quantity planning | Define whether informational or planning quantity | MUST |
| UOM conversion | Strong | None fundamental | Keep strict blocking behavior | MUST KEEP |
| Machine cycle | Strong for discrete mode | No efficiency/continuous-rate mode | Keep Phase 1; extend only when needed | MUST KEEP |
| Forward schedule | Implemented | No cross-WO load | Keep basic scheduler | MUST |
| Backward schedule | Implemented | Parallel route-group boundary bug | Fix + tests | MUST |
| Shift/break handling | Strong for machines | Plant fallback 08:00–17:00 | Use real plant/work-center shift source | MUST before labour scheduling |
| Preventive maintenance | Used | Unplanned downtime not considered | Add downtime availability in Phase 2 | SHOULD |
| Overtime | Shift has OT field | Scheduler does not currently model OT policy | Explicit overtime calendar/override policy | SHOULD |
| Cross-WO machine capacity | Not implemented | Schedule can overlap existing WO | Add schedule allocation/load table | SHOULD |
| WO snapshot | Strong | None fundamental | Keep explicit refresh/hash/revision | MUST KEEP |
| WO Draft machine override | Implemented | Depends on authoring alternatives existing | Fix Product Definition UI | MUST |
| Material requirement | Implemented | Execution source facts missing | Add material movement docs | MUST |
| Material reservation | Not implemented | Competing WOs can overpromise stock | Add lightweight reservation | SHOULD / Phase 1.5 if required |
| Material issue/return | Not implemented | Execution impossible | Add atomic production material movement | MUST |
| Lot/location | Inventory supports it | Production posting not wired | Reuse inventory posting pattern | MUST |
| Serial control | Not demonstrated in reviewed Inventory model | Cannot promise serial behavior | Add only if item/serial subsystem exists or is introduced | FUTURE/REQUIREMENT |
| Production output | Not implemented | Cannot record daily/shift actual | Add production report/run transaction | MUST |
| Partial completion | Projection fields exist | No source transaction or FG receipt | Support repeated partial output/receipts | MUST |
| WIP structural dependency | Strong | Actual WIP quantities/movement missing | Preserve OutputType in snapshot; add execution movement | MUST/SHOULD |
| Reject/scrap/rework | Projection fields exist | No reason/source transaction | Add output disposition rows + reason master | SHOULD, basic quantities MUST |
| Actual start/finish | Missing source facts | Planned vs actual unavailable | Derive projections from production reports | MUST |
| FG receipt | Not implemented | Cannot capitalize/receive production | Add production receipt + inventory adapter | MUST |
| Work Order completion | Status exists | Commands/predicates incomplete | Implement CanComplete/Complete | MUST |
| Work Order close | Status exists | Commands/predicates incomplete | Implement reconciliation + Close | MUST |
| Production costing | Partial standards exist | Actual facts missing; labour basis mismatch | Build after execution facts | SHOULD |
| Audit | Strong for planning lifecycle | Execution events not yet present | Extend append-only audit from posted docs | MUST |
| Idempotency | Posting-link table exists | No production posting command uses it yet | Use it for every stock-affecting command | MUST |
| Concurrency | Strong planning tests | Execution concurrency not implemented | Reuse Inventory lock/order pattern | MUST |
| Planner board | Data partly available | Due date/material/actual data incomplete | Add after execution foundation | SHOULD |
| Machine Gantt/load | Not implemented | No allocation table | Phase 2 | SHOULD |
| Advanced APS | Not implemented | Not required now | Defer | FUTURE |

---

# E. Recommended Target Architecture

## E1. Keep Product Definition as engineering/planning master

```text
Product Definition Revision
    ├─ Route Steps
    │    ├─ output type
    │    ├─ output quantity basis
    │    ├─ yield
    │    └─ transfer policy if later required
    │
    ├─ Operations
    │    ├─ process type
    │    ├─ sequence / parallel group
    │    ├─ duration standard
    │    ├─ machine alternatives
    │    └─ labour requirements
    │
    └─ Materials
         ├─ standard quantity / UOM
         ├─ scrap
         ├─ alternate group
         ├─ issue method
         ├─ supply source
         └─ WIP producer
```

Product Definition should describe **how the product is normally produced**.

It should not contain current Work Order load or execution quantities.

---

## E2. Keep Work Order as frozen job snapshot

```text
Work Order
    ├─ frozen Product Definition provenance
    ├─ frozen route
    ├─ frozen material requirements
    ├─ frozen machine alternatives + selection
    ├─ frozen labour standards
    ├─ planned schedule
    ├─ demand/required date
    └─ rebuildable execution projections
```

The released Work Order becomes the execution contract.

Changes after Release should go through:

```text
Change Order
or
explicit corrective/reversal transaction
```

not direct master-data synchronization.

---

## E3. Add execution as separate source-of-truth documents

The clean boundary should be:

```text
WORK ORDER SNAPSHOT
what should happen

PRODUCTION EXECUTION TRANSACTIONS
what actually happened

INVENTORY POSTINGS
stock/value consequence of what happened
```

Do not make `PrWorkOrderMaterial.IssuedQty` or `PrWorkOrderOperation.GoodQty` the only historical record.

Instead:

```text
posted production transaction
    ↓
update inventory atomically when applicable
    ↓
rebuild/update Work Order projections
```

---

# F. Required Database Changes

The recommendation is intentionally additive. Do not replace the existing `PrBom*` or `PrWorkOrder*` foundation.

## F1. Existing Product Definition fields that should be activated, not recreated

### `PrBomRouteStep.YieldPercent`

**Already exists.**

Action:
- add to Product Definition edit VM;
- expose in UI;
- validate `0 < YieldPercent <= 100` unless over-yield is explicitly a business requirement;
- include in Product Definition source hash;
- copy into Work Order snapshot;
- include in quantity calculation.

Why:
- required for realistic upstream quantity/material planning.

### `PrBomRouteStep.OutputType`

**Already exists.**

Action:
- expose `WIP_STOCKED`, `WIP_NONSTOCK`, `FINISHED_GOODS`;
- default intelligently but do not permanently hard-code based only on item code.

Why:
- shop-floor posting behavior depends on it.

### `PrBomLabourRequirement`

**Already exists.**

Action:
- author and persist it through `PrProductDefService`;
- expose multiple rows in UI.

Why:
- supports manual processes, headcount, multiple labour types, and machine-specific labour.

---

## F2. Add to `PrWorkOrderRouteStep`

| Column | Type | Purpose | Why required |
|---|---|---|---|
| `OutputType` | `nvarchar(20) NOT NULL` | Frozen `WIP_STOCKED / WIP_NONSTOCK / FINISHED_GOODS` | Execution must know whether a route output creates inventory or only internal WIP |
| `YieldPercent` | `decimal(9,4) NOT NULL DEFAULT 100` | Frozen route yield | Reproducible requirement calculation |
| `ActualStartDateTime` | `datetime2 NULL` | Projection of first posted execution activity | Actual-vs-planned |
| `ActualCompletionDateTime` | `datetime2 NULL` | Projection of final completion for route step | WIP/progress reporting |

Do not make actual timestamps directly editable. Derive them from posted production reports/events.

---

## F3. Add to `PrWorkOrder`

| Column | Type | Purpose | Why required |
|---|---|---|---|
| `RequiredCompletionDateTime` | `datetime2 NULL` | Customer/planner demand due date | Do not overload planned completion with due-date meaning |
| `ActualStartDateTime` | `datetime2 NULL` | Projection from first execution event | Actual-vs-planned |
| `ActualCompletionDateTime` | `datetime2 NULL` | Projection from completion events | Actual-vs-planned |
| `HoldDateTime` | `datetime2 NULL` | Current hold projection | Required because Phase 1 adopts `ON_HOLD`; derive from lifecycle events |
| `HoldReason` | `nvarchar(500) NULL` | Current hold reason projection | Required because Phase 1 adopts `ON_HOLD`; retain source reason in audit/event facts |

`RequiredCompletionDateTime` is the most important of these to add early because it allows:

```text
Required date
vs
Planned completion
vs
Actual completion
```

without changing semantics later.

---

## F4. Add to `PrWorkOrderOperation`

| Column | Type | Purpose | Why required |
|---|---|---|---|
| `ActualStartDateTime` | `datetime2 NULL` | Projection from first report/start | Actual-vs-planned |
| `ActualCompletionDateTime` | `datetime2 NULL` | Projection when operation output is resolved | Actual-vs-planned |

Current quantity projection columns are already sufficient as summary fields.

---

## F5. New `PrProductionReason`

A small shared master is preferable to free-text reason reporting.

| Column | Type | Purpose |
|---|---|---|
| `UID` | `bigint IDENTITY PK` | Identity |
| `CompanyCode` | `nvarchar(5)` | Tenant |
| `ReasonCode` | `nvarchar(20)` | Code |
| `ReasonType` | `nvarchar(20)` | `REJECT / SCRAP / REWORK / HOLD / DOWNTIME / VARIANCE` |
| `Description` | `nvarchar(200)` | User text |
| `IsActive` | `bit` | Active flag |
| audit fields | same project convention | Audit |
| `RowVersion` | `rowversion` | Concurrency |

Unique:

```text
CompanyCode + ReasonType + ReasonCode
```

---

## F6. New `PrProductionMaterialMovement`

Use one production document for both ISSUE and RETURN rather than duplicating two nearly identical schemas.

### Header

| Column | Type | Purpose |
|---|---|---|
| `UID` | `bigint IDENTITY PK` | Identity |
| `CompanyCode` | `nvarchar(5)` | Tenant |
| `BranchCode` | `nvarchar(5)` | Tenant |
| `LocationCode` | `nvarchar(10) NULL` | Site |
| `DocumentNo` | `nvarchar(30)` | Production movement no. |
| `WorkOrderID` | `bigint` | Work Order |
| `MovementType` | `nvarchar(10)` | `ISSUE / RETURN` |
| `MovementDateTime` | `datetime2` | Posting date/time |
| `Status` | `nvarchar(20)` | `DRAFT / POSTED / REVERSED / CANCELLED` |
| `Remark` | `nvarchar(500) NULL` | Note |
| `PostedDate` | `datetime2 NULL` | Posting audit |
| `PostedBy` | `nvarchar(10) NULL` | Posting audit |
| standard audit | project convention | Audit |
| `RowVersion` | `rowversion` | Concurrency |

### Line

| Column | Type | Purpose |
|---|---|---|
| `UID` | `bigint IDENTITY PK` | Identity |
| `HeaderID` | `bigint` | Parent |
| `LineNo` | `int` | Stable line |
| `WorkOrderMaterialID` | `bigint` | Exact frozen material requirement |
| `ComponentCode` | `nvarchar(30)` | Snapshot of item |
| `Qty` | `decimal(18,4)` | Transaction quantity |
| `UOM` | `nvarchar(10)` | Transaction UOM |
| `BaseQty` | `decimal(18,4)` | Inventory base quantity |
| `BaseUOM` | `nvarchar(10)` | Inventory base UOM |
| `WarehouseCode` | same Inventory convention | Source/destination warehouse |
| `LocationCode` | same Inventory convention | Source/destination location |
| `LotID` | existing Inventory lot key type, nullable | Lot-controlled item |
| `LotNo` | same Inventory convention, nullable | Snapshot/display |
| `ReasonCode` | `nvarchar(20) NULL` | Return/variance reason |
| `Remark` | `nvarchar(500) NULL` | Line note |

Posting relationship remains in existing `PrProductionPostingLink`.

### Phase-1 issue/consumption simplification

For a practical SME implementation, define this clearly:

- `MANUAL` issue: stock issue may be treated as consumption immediately unless the customer uses a production-staging warehouse/location.
- `BACKFLUSH`: material consumption is generated from posted production output.
- `PICK_LIST`: pick/reserve may be separated before final issue.

Keep `IssuedQty` and `ConsumedQty` as separate projections so the model can later support staging without schema redesign.

---

## F7. New `PrProductionReport`

This is the core daily/shift/shop-floor production fact.

| Column | Type | Purpose |
|---|---|---|
| `UID` | `bigint IDENTITY PK` | Identity |
| `CompanyCode` | `nvarchar(5)` | Tenant |
| `BranchCode` | `nvarchar(5)` | Tenant |
| `ReportNo` | `nvarchar(30)` | Document/event number |
| `WorkOrderID` | `bigint` | Work Order |
| `RouteStepID` | `bigint` | Exact route occurrence |
| `OperationID` | `bigint` | Exact operation |
| `MachineID` | `bigint NULL` | Selected Work Order machine row |
| `ProductionDate` | `date` | Reporting date |
| `ShiftCode` | project shift-code type, nullable | Shift |
| `OperatorCode` | project operator-code type, nullable | Operator/team |
| `StartDateTime` | `datetime2 NULL` | Actual start |
| `EndDateTime` | `datetime2 NULL` | Actual end |
| `InputQty` | `decimal(18,4)` | Actual input handled |
| `GoodQty` | `decimal(18,4)` | Good output |
| `RejectQty` | `decimal(18,4)` | Reject |
| `ScrapQty` | `decimal(18,4)` | Scrap |
| `HoldQty` | `decimal(18,4)` | Hold |
| `ReworkQty` | `decimal(18,4)` | Rework |
| `OutputUOM` | `nvarchar(10)` | Quantity UOM |
| `Remark` | `nvarchar(1000) NULL` | Note |
| `Status` | `nvarchar(20)` | `DRAFT / POSTED / REVERSED` |
| posting/audit fields | project convention | Control |
| `RowVersion` | `rowversion` | Concurrency |

This table is intentionally one fact per operation/machine/shift/run report. It supports daily reporting without forcing one document per day.

---

## F8. New `PrProductionDisposition`

Optional child detail for loss/rework reasons.

| Column | Type | Purpose |
|---|---|---|
| `UID` | `bigint IDENTITY PK` | Identity |
| `ProductionReportID` | `bigint` | Parent |
| `DispositionType` | `nvarchar(20)` | `REJECT / SCRAP / HOLD / REWORK` |
| `Qty` | `decimal(18,4)` | Quantity |
| `ReasonCode` | `nvarchar(20)` | Reason |
| `TargetOperationID` | `bigint NULL` | Rework target |
| `Remark` | `nvarchar(500) NULL` | Explanation |

This keeps the main report fast while allowing multiple rejection/rework reasons when required.

---

## F9. New `PrProductionReceipt`

Supports multiple partial receipts and both WIP-stocked and FG output.

### Header

| Column | Type | Purpose |
|---|---|---|
| `UID` | `bigint IDENTITY PK` | Identity |
| `CompanyCode` | `nvarchar(5)` | Tenant |
| `BranchCode` | `nvarchar(5)` | Tenant |
| `ReceiptNo` | `nvarchar(30)` | Production receipt document |
| `WorkOrderID` | `bigint` | Work Order |
| `ReceiptType` | `nvarchar(20)` | `WIP / FINISHED_GOODS` |
| `ReceiptDateTime` | `datetime2` | Date/time |
| `Status` | `nvarchar(20)` | `DRAFT / POSTED / REVERSED` |
| audit/rowversion | project convention | Control |

### Line

| Column | Type | Purpose |
|---|---|---|
| `UID` | `bigint IDENTITY PK` | Identity |
| `HeaderID` | `bigint` | Parent |
| `RouteStepID` | `bigint` | Producing route step |
| `ItemCode` | `nvarchar(30)` | WIP/FG item |
| `Qty` | `decimal(18,4)` | Receipt quantity |
| `UOM` | `nvarchar(10)` | Receipt UOM |
| `WarehouseCode` | Inventory convention | Destination |
| `LocationCode` | Inventory convention | Destination |
| `LotNo` / `LotID` | Inventory convention, nullable | Lot tracking |
| `ProductionReportID` | `bigint NULL` | Optional source report |

Every posted receipt links to the Inventory batch through `PrProductionPostingLink`.

---

## F10. New `PrMaterialReservation` — Phase 2 / Phase 1.5

One row per allocation slice is sufficient; no header is necessary for the first implementation.

| Column | Type | Purpose |
|---|---|---|
| `UID` | `bigint IDENTITY PK` | Identity |
| `CompanyCode` | `nvarchar(5)` | Tenant |
| `BranchCode` | `nvarchar(5)` | Tenant |
| `WorkOrderID` | `bigint` | WO |
| `WorkOrderMaterialID` | `bigint` | Exact material requirement |
| `WarehouseCode` | Inventory convention | Source warehouse |
| `LocationCode` | Inventory convention | Source location |
| `LotID` | Inventory lot key nullable | Optional lot allocation |
| `ReservedBaseQty` | `decimal(18,4)` | Allocation |
| `Status` | `nvarchar(20)` | `ACTIVE / RELEASED / CONSUMED / CANCELLED` |
| `ReservedDateTime` | `datetime2` | Audit |
| `ReleasedDateTime` | `datetime2 NULL` | Audit |
| standard audit | project convention | Audit |
| `RowVersion` | `rowversion` | Concurrency |

ATP calculation:

```text
AvailableToPromise
= OnHand
  - ActiveReservationsForOtherDemand
```

Reservation must not change physical on-hand stock.

---

## F11. New `PrWipTransfer` — Phase 2 if non-stock WIP needs explicit transfer

For `WIP_NONSTOCK`, an inventory receipt is inappropriate. A small quantity-transfer event can track queue between stages.

| Column | Type | Purpose |
|---|---|---|
| `UID` | `bigint IDENTITY PK` | Identity |
| `WorkOrderID` | `bigint` | WO |
| `FromRouteStepID` | `bigint` | Producer |
| `ToRouteStepID` | `bigint` | Consumer |
| `Qty` | `decimal(18,4)` | Transferred WIP |
| `UOM` | `nvarchar(10)` | UOM |
| `TransferDateTime` | `datetime2` | Time |
| `Status` | `nvarchar(20)` | Posted/reversed |
| audit/rowversion | project convention | Control |

Do not create this table in Phase 1 if actual users do not need explicit non-stock transfer events. WIP queue can initially be derived from producer Good Qty minus downstream Input/Transferred Qty.

---

## F12. New `PrMachineScheduleAllocation` — Phase 2

This is the key additive table that prevents future machine scheduling from requiring a Work Order redesign.

| Column | Type | Purpose |
|---|---|---|
| `UID` | `bigint IDENTITY PK` | Identity |
| `CompanyCode` | `nvarchar(5)` | Tenant |
| `BranchCode` | `nvarchar(5)` | Tenant |
| `WorkOrderID` | `bigint` | WO |
| `OperationID` | `bigint` | WO operation |
| `MachineID` | `bigint` | WO machine option row |
| `MachineCode` | existing machine code type | Query key |
| `PlannedStartDateTime` | `datetime2` | Reserved start |
| `PlannedCompletionDateTime` | `datetime2` | Reserved end |
| `CapacityUnits` | `decimal(18,4)` | Usually 1; future shared capacity |
| `AllocationStatus` | `nvarchar(20)` | `TENTATIVE / FIRM / RELEASED / CANCELLED` |
| `SnapshotRevision` | `int` | Provenance |
| audit/rowversion | project convention | Concurrency |

Indexes:

```text
CompanyCode + MachineCode + AllocationStatus + PlannedStartDateTime
WorkOrderID + OperationID
```

Interval overlap must be enforced in service logic under a machine/resource lock; a normal SQL unique index cannot detect overlapping time ranges.

---

# G. Required Service / Business Logic Changes

## G1. Product Definition

### Modify `PrProductDefService`

Required changes:

```text
1. Persist user-authored RouteStep.OutputType.
2. Persist user-authored RouteStep.YieldPercent.
3. Stop rebuilding every non-FG route output as WIP_STOCKED.
4. Support multiple PrBomMachineOption rows from the UI.
5. Keep exactly one default machine for machine-capable operations.
6. Persist PrBomLabourRequirement.
7. Permit multiple labour requirements.
8. Validate headcount/time/cost basis.
9. Define activation rule for yield/loss semantics.
10. Include the additional standards in source hashing/readiness.
```

### Product Definition quantity validation

Activation should reject:

```text
Yield <= 0
Output base qty <= 0
machine OutputPerCycle <= 0
negative setup/conversion/queue/cleanup
multiple default machines
machine process with no default machine
internal WIP with no unique producer
material with no consuming operation
invalid UOM chain
invalid labour cost basis
```

---

## G2. Work Order quantity calculator

### Extend route quantity logic

The current calculator assumes nominal route quantity scaling.

Add the agreed yield calculation.

A practical contract:

```text
RequiredGoodOutput(step)
    ↓
RequiredInput(step)
= RequiredGoodOutput / (YieldPercent / 100)
```

For internal WIP, calculate backward from the final good requirement so the upstream producer knows how much good WIP must be available after its own losses.

Do not apply `ScrapPercent` and route yield twice.

### SetupLossQty / OperationLossQty

Use the approved Phase 1 policy in N5:

- `SetupLossQty` = fixed expected loss per operation run/batch.
- `OperationLossQty` = fixed expected loss per Work Order operation only if that matches legacy meaning.
- route `YieldPercent` = percentage process yield.

If the legacy fields are not reliably defined, mark them informational until migration mapping is proven rather than silently using them.

---

## G3. Labour calculator

Expand Work Order labour basis constants to match Product Definition:

```text
PER_OUTPUT_UNIT
PER_HOUR
PER_OPERATION
```

Recommended equations:

```text
PER_OUTPUT_UNIT:
    PlannedAmount = PlannedOutputQty × Rate

PER_HOUR:
    PlannedAmount
    = RequiredHeadcount
      × PlannedLabourMinutes / 60
      × Rate

PER_OPERATION:
    PlannedAmount = Rate
```

If labour follows selected machine runtime:

```text
PlannedLabourMinutes
= SetupMinutes + selected machine run minutes
```

This can be supported using existing `PlannedUnits`, `PlannedMinutes`, `RateBasis`, `Rate`, and `PlannedAmount` fields without a major schema rewrite.

---

## G4. Scheduling

### Fix backward parallel boundary

Correct `WorkOrderScheduleCalculator.ScheduleAsync`.

Add regression tests before any production use.

### Improve non-machine calendar

Replace synthetic `08:00–17:00` with one of:

1. branch/company default manufacturing shift group, or
2. Work Center calendar/shift group.

For SME practicality, start with **company/branch default manufacturing shift group**. Work Center-specific calendars can be added only where needed.

### Do not add finite capacity inside the current snapshot calculator

Keep two layers:

```text
Duration/calendar scheduler
    calculates feasible working-time duration

Machine allocation layer
    resolves conflicts with other Work Orders
```

This separation keeps the current scheduler testable and avoids turning it into an APS engine prematurely.

---

## G5. Production execution services

Recommended service boundaries:

```text
IProductionMaterialService
    PreviewIssueAsync
    PostIssueAsync
    ReverseIssueAsync
    PostReturnAsync
    ReverseReturnAsync

IProductionReportingService
    CreateDraftAsync
    PostReportAsync
    ReverseReportAsync

IProductionReceiptService
    PostWipReceiptAsync
    PostFgReceiptAsync
    ReverseReceiptAsync

IProductionLifecycleService
    StartAsync
    HoldAsync / ResumeAsync optional
    CanCompleteAsync
    CompleteAsync
    CanCloseAsync
    CloseAsync

IProductionReservationService        [Phase 2]
    ReserveAsync
    ReleaseAsync
    RebuildAsync

IProductionPlanningService           [Phase 2]
    RecalculateLoadAsync
    AllocateMachineAsync
    MoveAllocationAsync
```

---

## G6. Atomic Inventory adapter

Do not implement Production posting as:

```text
save production doc
COMMIT

then call Inventory service
COMMIT
```

That can leave the database half-posted.

Instead, follow the existing Inventory core pattern:

```text
BEGIN SQL TRANSACTION

lock Production document
lock Work Order
verify status / rowversion / snapshot
claim ProductionPostingLink idempotency key

lock relevant Inventory master/balance slices
validate all stock deltas
validate period / negative-stock policy
create/update IvTrxBatch + details/history
update balances

mark Production document POSTED
update Work Order projections
mark ProductionPostingLink SUCCEEDED
append Work Order audit event

COMMIT
```

Any failure must roll back the entire operation.

Use deterministic lock ordering like the current Inventory posting implementation to avoid deadlocks.

---

# H. UI Changes

## H1. Product Definition Entry

Keep the existing DevExpress tabbed structure, but change how resources are presented.

### Route / Work Center tab

Show:

```text
Stage
Work Center
Output Item
Output Type
Output Qty Basis
Yield %
```

Use friendly labels:

```text
Stocked WIP
Non-stock WIP
Finished Goods
```

rather than exposing raw enum/database values.

### Process tab

Show:

```text
Process
Type
Sequence
Standard duration
Final process
Expected loss
```

Explain:

- same sequence = parallel;
- higher sequence = after previous group.

### Machine tab

Replace singular machine card with a grid:

| Machine | Default | Priority | Output/Cycle | Cycle sec | Setup sec | Queue sec | Parallel | Rate/hour |
|---|---:|---:|---:|---:|---:|---:|---:|---:|

Actions:

```text
Add machine option
Set default
Edit
Remove
```

Use a visible note:

> Non-default machines are approved alternatives. Work Order planners may select one of them without changing the Product Definition.

### Labour tab

Use multiple rows:

| Labour | Headcount | Applies to | Setup min | Run min | Cost basis | Rate |
|---|---:|---|---:|---:|---|---:|

`Applies to`:

```text
Operation
Machine M01
Machine M02
...
```

### Formula preview

For the selected machine, show a simple example:

```text
For 1,000 PCS:
Output/cycle = 5 PCS
Cycles = 200
Parallel machines = 2
Slots = 100
Cycle time = 30 sec
Run time = 50 min
+ setup / queue / conversion
```

This will greatly reduce user misunderstanding.

---

## H2. Work Order Entry

The current Work Order screen is already structurally good.

Keep:

- Overview;
- Route;
- Operations;
- Materials;
- Machines;
- Labour;
- Audit;
- Calculate Preview;
- Recalculate;
- Refresh Definition;
- Release.

Improve Overview with:

```text
Required completion date
Planned completion
Late / On time
Material status
Production progress
Current lifecycle status
```

After Release, do not leave the user only in read-only Draft-entry mode.

Add clear execution actions:

```text
Issue Material
Report Production
Receive WIP / FG
Return Material
Complete
Close
```

These should navigate to dedicated transaction pages instead of overloading the Work Order master screen.

---

## H3. Material Issue / Return

Reuse the visual patterns from Inventory transactions.

Recommended layout:

```text
Header:
WO / Product / Date / Warehouse / Status

Grid:
Material
Required
Reserved
Issued
Returned
Outstanding
Available
Issue Now
UOM
Lot
Location
Shortage
```

Allow partial issues and repeated documents.

---

## H4. Production Output Entry

Optimize for shop-floor speed.

Header:

```text
Date
Shift
WO
Work Center
Process
Machine
Operator
Start
End
```

Quantities:

```text
Input
Good
Reject
Scrap
Hold
Rework
```

Show immediately:

```text
WO Target
Previous Good
This Report
Total Good
Balance
Completion %
```

If Reject/Scrap/Rework > 0, expand reason details.

---

## H5. Work Order Inquiry

The inquiry page should answer operational questions without opening the full entry screen.

Recommended columns:

```text
WO
Product
Planned Qty
Good Qty
Balance
Required Date
Planned Finish
Status
Production %
Material %
Material Shortage
Late
Current Stage
```

---

## H6. Planner pages — Phase 2

### Work Order Board

```text
WO
Product
Qty
Required Date
Planned Start
Planned Finish
Progress
Material Status
Late
```

### Machine Schedule

Timeline/Gantt by machine using `PrMachineScheduleAllocation`.

### Work Center Load

```text
Available Hours
Allocated Hours
Utilization
Overload
```

### Material Shortage

```text
WO
Material
Required
Reserved
Issued
Available
Shortage
Required Date
```

---

# I. Scheduling Algorithm

## I1. Phase 1 scheduling model

Keep the existing scheduler architecture.

### Step 1 — calculate quantities first

Before scheduling:

```text
Work Order Qty
    ↓
Route planned quantities
    ↓
Operation output quantities
    ↓
Machine required output
    ↓
Cycle count / slots
    ↓
Run duration
```

### Step 2 — calculate operation duration

Machine operation:

```text
CycleCount
= Ceiling(RequiredMachineOutput / OutputPerCycle)

CycleSlots
= Ceiling(CycleCount / ParallelMachineCount)

RunSeconds
= CycleSlots × CycleSeconds

OperationDuration
= QueueSeconds
+ SetupSeconds
+ ConversionSeconds
+ RunSeconds
```

If `CleanupSeconds` is later introduced:

```text
+ CleanupSeconds
```

Non-machine operation:

```text
OperationDuration
= StandardDurationMinutes
```

A WAIT operation is preferable to adding ambiguous “waiting time” fields everywhere.

### Step 3 — precedence

Route level:

```text
lower StageSequence before higher StageSequence
same StageSequence = parallel
```

Operation level:

```text
lower ProcessSequence before higher ProcessSequence
same ProcessSequence = parallel
```

Additional hard dependency:

```text
INTERNAL_ROUTE_WIP producer
    must finish before consumer
```

### Step 4 — calendar placement

For machine operation:

```text
selected machine calendar
- off days
- breaks
- preventive maintenance
```

For non-machine:

```text
plant/work-center calendar
```

### Step 5 — forward scheduling

```text
anchor = requested/planned start

for each route sequence group:
    schedule each parallel member from group anchor
    group completion = MAX(member completion)
    next group anchor = group completion
```

### Step 6 — backward scheduling

```text
anchor = required completion

for each route sequence group in reverse:
    schedule each parallel member backward from group anchor
    group start = MIN(member start)
    previous group completion boundary = group start
```

This is where the current outer-group backward defect must be corrected.

### Step 7 — persist provenance

Keep:

```text
Calendar source
Calendar horizon
Schedule source hash
Schedule calculation trace
```

This is one of the existing design strengths.

---

## I2. Example

Product:

```text
WO Qty = 1,000 PCS

WC10 Stage 10
    Process CUT
    Machine CUT01
    OutputPerCycle = 5 PCS
    Cycle = 30 sec
    ParallelMachineCount = 1
    Setup = 600 sec

WC20 Stage 20
    Process PACK
    StandardDuration = 120 min
```

CUT:

```text
CycleCount = ceil(1000 / 5) = 200
Run = 200 × 30 sec = 6,000 sec = 100 min
Total = 10 min setup + 100 min run = 110 min
```

The calendar engine places those 110 working minutes around shifts/breaks/maintenance.

PACK begins only after CUT finishes.

Backward mode reverses this logic from the required completion date.

---

## I3. Phase 2 finite capacity

Do not replace the Phase 1 scheduler.

Before placing a machine operation:

```text
Calendar usable intervals
    MINUS
firm/tentative machine allocations from other WOs
```

Then:

```text
find first feasible slot forward
or
find latest feasible slot backward
```

Initially make machine selection planner-driven:

```text
default machine
or
approved alternate selected by planner
```

Do not jump immediately to automatic optimization.

---

# J. Material Flow

## J1. Target flow

```text
Work Order Material Requirement
    ↓
Material Reservation                [optional Phase 1.5 / Phase 2]
    ↓
Material Issue
    ↓
Production Consumption
    ↓
Unused Material Return
    ↓
WIP / FG Receipt
```

## J2. Core quantities

For every Work Order material:

```text
RequiredQty
ReservedQty
IssuedQty
ReturnedQty
ConsumedQty
VarianceQty
```

Recommended display projections:

```text
NetIssued
= IssuedQty - ReturnedQty

OpenIssueQty
= max(RequiredQty + ApprovedVariance - NetIssued, 0)

OpenConsumptionQty
= max(RequiredQty + ApprovedVariance - ConsumedQty, 0)
```

Do not invent one universal “Outstanding” formula without labeling what is outstanding.

Use explicit labels:

```text
To Reserve
To Issue
To Consume
```

---

## J3. Reservation

Reservation is useful when multiple Work Orders compete for the same item.

Example:

```text
On hand = 1,000

WO001 reserves 700
WO002 needs 500

Available to WO002 = 300
Shortage = 200
```

For this ERP, reservation should be lightweight and quantity-based.

Avoid implementing complex allocation pegging, optimization, or ATP promise engines in Phase 1.

If customers frequently have stock contention, move reservation from Phase 2 into Phase 1.5 immediately after Material Issue is stable.

---

## J4. Lot handling

The existing Inventory module already has:

- lot-controlled stock master;
- `IvLot`;
- balance by warehouse/location/lot;
- lot IDs on transaction details/history.

Production material issue/return and FG/WIP receipt should reuse those structures.

Do not introduce a parallel production-only lot master.

---

## J5. Serial numbers

The reviewed Inventory entities clearly demonstrate lot control but do not demonstrate a serial-number subsystem.

Therefore the Production plan should **not claim serial support yet**.

If serial-tracked manufacturing is later required, add it as a separate Inventory capability and have Production consume that capability.

---

# K. Production Execution Flow

Recommended Work Order execution lifecycle:

```text
DRAFT
    ↓ Release
RELEASED
    ↓ first start/output/issue as policy defines
IN_PROGRESS
    ↓ repeated execution
    ├─ Material Issue
    ├─ Material Return
    ├─ Production Report
    ├─ WIP Receipt/Transfer
    └─ Partial FG Receipt
    ↓
COMPLETED
    ↓ reconciliation
CLOSED
```

Exceptional:

```text
CANCELLED
ON_HOLD      [approved Phase 1 lifecycle state]
```

## K1. Do not turn every operational condition into a primary status

Avoid a state explosion such as:

```text
MATERIAL_ISSUED
PARTIALLY_ISSUED
SHORTAGE
PARTIALLY_COMPLETED
```

Those are better shown as derived indicators:

```text
Lifecycle Status = IN_PROGRESS
Material Status  = PARTIAL / SHORTAGE / READY
Progress Status  = 55%
Schedule Status  = LATE
```

This produces a much cleaner state machine.

---

## K2. Allowed actions

### DRAFT

Allowed:

```text
edit header
refresh definition
recalculate
select approved machine
substitute approved material
release
cancel
```

No production posting.

### RELEASED

Allowed:

```text
reserve
issue
start
approved change order
cancel only if no irreversible execution facts, otherwise reversal workflow
```

Routing snapshot normally frozen.

### IN_PROGRESS

Allowed:

```text
additional issue
material return
production reporting
WIP/FG partial receipt
reject/scrap/rework
hold/resume
approved change order for future effect
```

### COMPLETED

Normal production output should stop.

Allow only defined reconciliation:

```text
remaining material return
approved correction/reversal
final receipt reconciliation
```

### CLOSED

No normal changes.

Only controlled reversal/reopen process if business rules permit.

---

## K3. Completion predicate

`CanComplete` should check at minimum:

```text
Lifecycle status is eligible
Final output accounted for
No unresolved operation quantity
No blocking hold/rework disposition
No pending production documents
No invalid pending posting link
WIP reconciled according to policy
Required final receipt completed
Approved variance covers accepted over/under production
```

Do not require every material requirement to equal exactly 100% consumption if approved variance is allowed.

---

## K4. Close predicate

`CanClose` is stricter:

```text
Completed
All production docs posted/reversed
No active reservation
No outstanding material to return under policy
No unresolved WIP
No pending change order
Costing complete if costing is enabled
Demand allocation reconciled if applicable
```

The existing `ProductionPredicateReasonCodes` already anticipates many of these concepts and should be reused.

---

# L. Implementation Phases

## L0. Delivery and approval controls

The implementation is one approved program delivered through four independently reviewable pull-request groups. Each phase must include its schema migration, service/domain changes, UI changes, automated tests, authorization/audit behavior, and operator notes. A later phase may be developed behind a disabled feature gate, but it may not be enabled before all earlier exit criteria pass.

Required controls for every Phase 1 pull request:

```text
build succeeds with warnings reviewed
unit and integration tests pass
SQL Server migration is forward-tested on a production-like copy
rollback/recovery procedure is documented and rehearsed
company/tenant boundaries and authorization are tested
rowversion/idempotency behavior is tested where commands can be repeated
posted records are immutable; correction uses reversal
audit event contains actor, UTC timestamp, source, reason, and correlation/request ID
no unrelated Phase 2/3 scope is introduced
```

Compatibility rule: additive nullable columns or safe defaults are used where possible. Existing released Work Orders retain their frozen behavior. Draft legacy snapshots must be explicitly refreshed; they must not be silently rewritten.

## L0.1 Approved implementation sequence

| Order | Deliverable | Dependency | Gate owner |
|---:|---|---|---|
| 1 | Phase 1A — planning foundation | Current baseline | Engineering + Production SME |
| 2 | Phase 1B — material execution | Phase 1A accepted | Engineering + Inventory owner |
| 3 | Phase 1C — output and receipt | Phase 1B accepted | Engineering + Production + Inventory |
| 4 | Phase 1D — lifecycle and release | Phases 1A–1C accepted | Product owner + QA/UAT owner |

Names and dates are deployment-planning details and do not block technical approval, but they must be assigned before production rollout.

## Phase 1A — Correct and harden the existing foundation

**Goal:** released Work Orders must be structurally correct before execution is allowed.

Implement:

```text
1. Fix backward + parallel scheduling defect.
2. Add regression tests.
3. Expose multiple machine alternatives in Product Definition UI.
4. Persist exactly one default machine + alternatives.
5. Expose/persist PrBomLabourRequirement.
6. Support multiple labour rows.
7. Support labour rate bases end-to-end.
8. Expose route OutputType.
9. Expose route YieldPercent.
10. Freeze OutputType/Yield into Work Order route snapshot.
11. Define and implement yield/loss quantity rules.
12. Add RequiredCompletionDateTime to Work Order.
13. Replace hard-coded plant 08:00–17:00 with configured manufacturing shift/calendar.
14. Update readiness validation and tests.
```

**Stable end state:** Work Order planning/release is trustworthy.

**Exit criteria:**

- backward scheduling with unequal-duration parallel route steps preserves the earliest start and passes regression tests;
- Product Definition supports multiple machine options with exactly one default;
- multiple operation-level and machine-specific labour requirements round-trip through UI, service, snapshot, and calculation for every supported rate basis;
- OutputType and YieldPercent are authored, validated, hashed, snapshotted, and displayed;
- the approved yield/loss rules in N5 pass worked examples for multi-stage WIP, material scrap, UOM conversion, and pack ratios without double counting;
- required completion is distinct from planned completion;
- non-machine scheduling uses configured manufacturing calendars and has no hard-coded 08:00–17:00 production behavior;
- all existing Work Order snapshot/hash/release tests remain green;
- the execution gate remains closed.

---

## Phase 1B — Material execution

**Goal:** safely move raw material for a released Work Order.

Implement:

```text
PrProductionMaterialMovement
IProductionMaterialService
Inventory atomic posting adapter
PrProductionPostingLink usage
issue
partial issue
multiple issue
return
lot/location handling
negative-stock policy
reversal
projection rebuild
audit
SQL Server concurrency tests
```

Keep execution gate closed until this phase passes tests.

**Exit criteria:**

- issue, partial issue, repeated issue, return, and reversal are represented by immutable source documents;
- Production document, posting link, Inventory movement/history, lot/location balance, audit event, and Work Order projections commit or roll back in one database transaction;
- a repeated request/correlation ID produces no duplicate stock movement;
- over-return, invalid lot/location, inactive item, negative-stock policy, stale rowversion, and concurrent issue scenarios fail safely;
- projections can be rebuilt from posted/reversed source facts and match stored totals;
- SQL Server concurrency tests prove deterministic behavior;
- the execution gate remains closed because production output and lifecycle are not complete.

---

## Phase 1C — Production output + partial receipt

**Goal:** record what production actually makes.

Implement:

```text
PrProductionReason
PrProductionReport
PrProductionDisposition
production output UI
partial reporting
good/reject/scrap/hold/rework
actual start/end
operation projections
WO progress
PrProductionReceipt
partial WIP/FG receipt
multiple receipts
receipt reversal
```

When this phase is stable, the ERP has a practical SME production execution loop.

**Exit criteria:**

- repeated partial reports and repeated partial WIP/FG receipts are supported without overwriting history;
- good, reject, scrap, rework, and hold quantities validate against the approved disposition rules and reason codes;
- stocked WIP and finished-goods outputs post Inventory atomically; non-stock WIP never creates stock accidentally;
- receipt/report reversal restores Inventory and all projections without deleting history;
- actual start/completion projections are reproducible from source facts;
- overproduction and underproduction outside the approved tolerance require an explicit variance approval;
- end-to-end tests cover issue → partial output → partial receipt → additional output → final receipt → reversal.

---

## Phase 1D — Lifecycle completion

Implement:

```text
Start Work Order
IN_PROGRESS transition
CanComplete
Complete
CanClose
Close
Hold/Resume and ON_HOLD transition
controlled cancellation after release
audit events
change-order service activation
```

`ON_HOLD` and Hold/Resume are included in the approved lifecycle because production reports already require a hold disposition. They are not optional in the Phase 1 release.

**Exit criteria / release gate:**

- allowed commands and transition predicates are enforced server-side for DRAFT, RELEASED, IN_PROGRESS, ON_HOLD, COMPLETED, CLOSED, and CANCELLED;
- completion and close return stable reason codes for every unmet predicate;
- a released/in-progress order with irreversible facts cannot be directly cancelled; it requires reversal/reconciliation first;
- closed orders reject normal posting and edits;
- authorization, audit, optimistic concurrency, idempotency, and controlled change-order tests pass;
- a production-like migration and rollback/recovery rehearsal succeeds;
- UAT signs off at least one happy path, partial-production path, shortage/failure path, hold/resume path, and reversal path;
- only after all preceding criteria pass may `ProductionExecutionGate` be enabled through a controlled deployment setting.

---

## Phase 2A — Material reservation and shortage planning

Implement:

```text
PrMaterialReservation
reserve / unreserve
ATP by warehouse/location
WO shortage inquiry
material readiness indicator
reservation concurrency tests
```

Move earlier if customer demand competition makes reservation operationally necessary.

---

## Phase 2B — WIP and execution detail

Implement as required:

```text
non-stock WIP transfer facts
WIP queue inquiry
downtime reporting
reason analytics
rework routing
actual machine/labour time
actual-vs-planned dashboards
```

---

## Phase 2C — Machine capacity planning

Implement:

```text
PrMachineScheduleAllocation
machine load query
work center load
conflict detection
planner machine-selection workflow
manual move/reallocate
calendar + maintenance + existing allocations
```

Do not implement automatic optimizer yet.

---

## Phase 2D — Standard vs actual costing

Use existing snapshot standards plus posted facts:

```text
Standard Material
vs actual material issue/consumption

Standard Machine Time × Rate
vs actual machine time

Standard Labour
vs actual labour

Scrap cost
WIP/FG receipt valuation
```

Add explicit overhead/subcontract rules only when the accounting design is agreed.

---

## Phase 3 — advanced planning

Only after operational data is trustworthy:

```text
finite-capacity auto scheduling
drag/drop Gantt with conflict solving
automatic rescheduling
MRP recommendations
constraint optimization
predictive completion
OEE
MES integration
advanced shop-floor dispatch
```

---

# M. Risks

## M1. Data integrity risk — projections becoming source of truth

Risk:

```text
PrWorkOrderMaterial.IssuedQty = 200
```

is edited directly without a corresponding posted issue document.

Result: no audit/reversal/source trail.

Mitigation:

> All execution projections must be derived from posted facts.

---

## M2. Inventory partial-commit risk

Risk:

```text
Production document posts
Inventory posting fails
```

or the reverse.

Mitigation:

> one SQL transaction, common DbContext/connection, deterministic locks, one idempotency record.

---

## M3. Yield/loss under-planning

The current presence of `YieldPercent`, `SetupLossQty`, and `OperationLossQty` can create a false expectation that these affect quantity planning.

They currently do not complete the full loss/yield calculation.

Mitigation:

> settle the business meaning and add tests before execution.

---

## M4. False machine availability

Current planned dates can look precise but do not include other Work Order bookings.

Mitigation:

- clearly label current schedule as calendar-based;
- Phase 2 allocation table;
- do not claim finite capacity before allocations exist.

---

## M5. Product Definition UI/model mismatch

The data model supports machine alternatives while the UI currently prevents them.

Mitigation:

> fix authoring before planner features are built; otherwise later planning will discover that definitions contain only one eligible machine.

---

## M6. Labour basis mismatch

Product Definition has a richer labour requirement model, while Work Order rate calculation is effectively limited to per-output-unit.

Mitigation:

> align Product Definition → snapshot → calculator → UI before Release permits those rows.

---

## M7. Calendar accuracy

Non-machine operations currently use a synthetic plant shift.

Mitigation:

> central company/branch manufacturing shift group, then optional work-center calendar later.

---

## M8. Legacy snapshot migration

The Work Order model supports legacy snapshot format 1 and current format 2.

Existing release logic correctly requires explicit refresh for legacy rows when current format is required.

Mitigation:

- retain this rule;
- provide a controlled migration/report showing unreleased legacy Drafts.

---

## M9. Reversal complexity

Material and FG postings affect stock and cost.

Mitigation:

- every posted Production transaction must have an explicit reversal path;
- never “edit posted quantity” in place.

---

## M10. Concurrency

Multiple users may:

```text
issue the same material
release the same WO
reserve the same stock
report the same shift
receive the same FG
```

Mitigation:

- rowversion;
- source document status lock;
- idempotency key;
- deterministic balance-slice locking;
- unique request IDs;
- SQL Server concurrency tests.

The repository already demonstrates this discipline in both Inventory and Work Order Release; extend it rather than inventing new conventions.

---

## M11. Performance

Current split-query loading of a single Product Definition revision is sensible.

Future planner pages will need different query shapes.

Do not load complete Work Order aggregates for:

```text
machine Gantt
shortage dashboard
work center load
daily output inquiry
```

Add targeted read models/indexes.

---

# N. Final Recommendation

## N1. What should remain as-is

Keep the following architecture:

```text
PrBomHdr revision/effective-date model
PrBomRouteStep hierarchy
PrBomOperation hierarchy
PrDefBOM material ownership
strict UOM conversion
Work Order frozen snapshot
snapshot/source hashes
explicit Draft refresh
rowversion concurrency
route/process parallel sequence semantics
internal-WIP producer dependency
calendar-aware scheduling engine
machine calendar / shifts / breaks
preventive-maintenance exclusion
audit/change-order/posting-link foundations
Inventory transaction safety patterns
```

These are good design decisions.

---

## N2. What should be refactored now

Refactor before adding execution:

```text
Product Definition single-machine UI
Product Definition single-labour UI/path
route OutputType authoring
route YieldPercent authoring
yield/loss quantity semantics
Work Order route snapshot OutputType/Yield
labour rate-basis compatibility
non-machine calendar source
backward parallel scheduling bug
required/demand completion date
```

---

## N3. What must be added before production execution begins

Minimum production-safe execution slice:

```text
production material issue/return source documents
atomic Inventory posting adapter
idempotency using PrProductionPostingLink
partial issue and return
production output/report source transaction
partial production
FG/WIP receipt
actual dates
basic reject/scrap/rework quantities
Work Order start/complete/close commands
reversal rules
execution audit
SQL Server concurrency tests
```

The execution gate should stay closed until this slice is proven.

---

## N4. What can safely be postponed

Safe to defer:

```text
automatic finite-capacity optimization
drag/drop Gantt
automatic alternate-machine choice
advanced MRP
full labour dispatch
OEE
predictive completion
MES integration
sequence-dependent setup
advanced overhead allocation
serial manufacturing unless requested
```

---

## N5. Approved architectural decisions for implementation

The following are the Phase 1 contracts. They replace the earlier open questions. A different behavior requires a documented change decision, updated examples/tests, and approval from the affected business owner.

| # | Decision | Approved Phase 1 contract |
|---:|---|---|
| 1 | Yield meaning | BOM quantities are **net standards per good parent output**. Route `YieldPercent` grosses up the required input/output of the affected route step. Material `ScrapPercent` applies only to that material line after its BOM ratio is calculated. The calculator must not apply either factor twice. |
| 2 | Setup/operation loss | Existing `SetupLossQty` and `OperationLossQty` remain informational in Phase 1 because legacy meaning is not proven. They are displayed but excluded from planning, release, completion, and costing. Activation warns when non-zero. Using them later requires a migration/mapping decision. |
| 3 | Material issue meaning | Phase 1 uses **issue-as-consumption**: posting an issue reduces Inventory and increases the Work Order's issued/consumed projection in the same transaction. No staging-location model is implied. Return reverses unconsumed/returned quantity through a source document. A future staging model must introduce explicit transfer and consumption facts. |
| 4 | WIP output policy | `WIP_STOCKED` creates Inventory receipts/issues, `WIP_NONSTOCK` creates production/WIP facts only, and `FINISHED_GOODS` creates an FG receipt. OutputType is mandatory on activation and frozen in the Work Order snapshot. |
| 5 | Required date | `RequiredCompletionDateTime` is the external demand/planner due date. `PlannedCompletionDateTime` is scheduler output. One must never overwrite or silently derive the other after initial scheduling. |
| 6 | Labour cost basis | `PER_OUTPUT_UNIT`, `PER_HOUR`, and `PER_OPERATION` are supported end-to-end using the equations in G3. Unsupported/unknown bases block activation and release. |
| 7 | Execution source of truth | Posted and reversed transaction rows are authoritative. Work Order totals/status dates are rebuildable projections and are never directly editable. |
| 8 | Inventory boundary | Each stock-affecting production command and its Inventory effects commit atomically using the same SQL transaction/DbContext/connection, deterministic lock order, and a unique request/correlation ID recorded through `PrProductionPostingLink`. |
| 9 | Completion variance | Default quantity tolerance is zero unless an active company/item policy supplies an approved tolerance. Outside tolerance, completion requires a recorded `VARIANCE` reason and an authorized approval; the implementation must not embed a guessed universal percentage. Material exact-match is not required when approved variance/reconciliation rules are satisfied. |
| 10 | Capacity boundary | Phase 1 scheduling remains calendar-constrained and infinite-capacity across Work Orders and must be labelled accordingly. Finite capacity is a separate Phase 2 `PrMachineScheduleAllocation` layer; it must not be embedded in snapshot tables. |
| 11 | Hold lifecycle | `ON_HOLD`, Hold, and Resume are part of Phase 1. Hold blocks new normal execution postings; authorized correction/reversal remains available. |
| 12 | Legacy compatibility | Existing released snapshots are immutable. Legacy Drafts require explicit preview/refresh. No migration silently reinterprets historical yield, loss, labour, or output type. |

These contracts allow Machine Scheduling, Material Issue, Daily Production Output, WIP, MRP, and costing to be added incrementally without another major Product Definition / Work Order redesign.

## N6. Approval record

Recommended decision:

> **APPROVE Phases 1A–1D for implementation against baseline `e1a41fed8114f3373bcabe99bfdcd8931b49bf40`, using the N5 contracts and L0 controls. Do not enable production execution until the Phase 1D release gate and UAT sign-off pass. Phase 2 and Phase 3 require separate approval.**

Approval roles:

| Role | Confirms |
|---|---|
| Product/ERP owner | scope, priorities, lifecycle, and excluded features |
| Production SME | yield, WIP, reporting, tolerance, and operational workflows |
| Inventory/finance owner | stock transaction boundary, valuation touchpoints, reversal, and period controls |
| Engineering lead | architecture, migrations, security, concurrency, observability, and recovery |
| QA/UAT owner | test evidence and release-gate completion |

Record approver names, decision date, deployment window, and any approved exceptions in the delivery ticket or change record. Do not edit the technical contracts silently after approval.

---

# Explicit Answers to the 22 Required Questions

| # | Question | Answer |
|---:|---|---|
| 1 | Is the current Production Definition architecture correct? | **Core architecture: yes.** Versioned route/operation/BOM design is good. Authoring of machine alternatives, labour requirements, output type, and yield is incomplete. |
| 2 | Is the current Work Order architecture correct? | **For snapshot/planning: yes. For execution: incomplete by design.** Do not rebuild the snapshot aggregate. |
| 3 | Are Product Definition → Work Order snapshot rules correct? | **Yes, strongly implemented.** Exact revision, explicit copy, hashes, explicit refresh, Draft-only changes, no silent master updates. |
| 4 | Is BOM calculation correct? | **Correct for BaseQty/StdQty/scrap/UOM.** Route yield and operation-loss semantics are not yet applied. |
| 5 | Are standard quantities / pack-size ratios handled correctly? | **Yes.** Base quantity + component quantity supports pack ratios cleanly, with strict UOM conversion. |
| 6 | Are machine cycle calculations correct? | **Yes for the defined discrete-cycle model.** Uses ceiling for cycles and parallel slots. Missing efficiency/advanced changeover concepts are future extensions. |
| 7 | Are time units clearly defined and safe? | **Mostly yes in the new model.** Machine times are explicit seconds; standard process duration is minutes. Avoid returning to legacy ambiguous doubles. |
| 8 | Can current architecture support forward scheduling? | **Yes; already implemented.** |
| 9 | Can it support backward scheduling? | **Yes, but fix the parallel route-group boundary defect and add tests.** |
| 10 | Can machine availability and shifts integrate properly? | **Calendar/shift/break/preventive availability: yes. Cross-WO occupied capacity: not yet.** |
| 11 | Can parallel work centers/processes be represented? | **Yes for sequence-group parallelism.** Generic arbitrary dependency DAG is not modeled except internal-WIP edges; probably acceptable for current SME scope. |
| 12 | Can alternate BOMs and machines be handled properly? | **Work Order/service model: yes. Product Definition machine UI: not yet.** BOM exact-revision substitution is already good. |
| 13 | Is the system ready for material issue to production? | **No.** Requirement data is ready, but execution posting is intentionally gated and the source transaction/adapter is missing. |
| 14 | Is material reservation required? | **Recommended.** Keep as Phase 2/1.5 unless customers frequently compete for scarce stock, in which case bring it earlier. |
| 15 | Can daily/shift production output be recorded correctly? | **Not currently.** Add `PrProductionReport` + service/UI. |
| 16 | Can partial production completion be supported? | **Architecture can support it, but transactions are missing.** Repeated reports and repeated FG receipts should be the source facts. |
| 17 | Can WIP be tracked? | **Definition/dependency WIP: yes. Actual WIP quantity: not yet.** Preserve OutputType in WO snapshot and add execution movement/receipt. |
| 18 | Can reject, scrap and rework be recorded later? | **Yes without redesign.** Projection fields already exist; add source report/disposition transactions and reason codes. |
| 19 | Can actual vs planned production be measured? | **Planned side is strong. Actual side is missing.** Add execution facts and derived actual timestamps/hours/quantities. |
| 20 | Can the data eventually support production costing? | **Yes, with additions.** Material/machine/labour standards exist, but actual facts and labour-basis alignment are required. |
| 21 | Can it support machine scheduling without redesigning the database again? | **Yes if a separate `PrMachineScheduleAllocation` layer is added.** No need to replace the WO snapshot. |
| 22 | What important concepts are missing? | Execution documents, atomic production-inventory posting, reservation, actual output/run facts, partial FG/WIP receipt, actual dates, completion/close commands, finite machine load, downtime, reason/disposition, final yield/loss rules, richer labour authoring/cost basis, and a real plant/work-center calendar source. |

---

# Source Review Traceability

The conclusions above were grounded in the `production` branch, particularly:

```text
ErpWeb.Model/Entities/Planning/
    PrBomHdr.cs
    PrBomOperation.cs
    PrDefBOM.cs
    ProductDefinitionPhase1Entities.cs
    PrMachine.cs
    PrShift.cs
    PrShiftBreak.cs
    PrShiftCalendar.cs
    PrCalendar.cs

ErpWeb.Model/Entities/Production/
    ProductionWorkOrder.cs
    ProductionWorkOrderRouteStep.cs
    ProductionWorkOrderOperation.cs
    ProductionWorkOrderMachine.cs
    ProductionWorkOrderMaterial.cs
    ProductionWorkOrderLabour.cs
    ProductionDomainConstants.cs
    ProductionAuditEvent.cs
    ProductionChangeOrder.cs
    ProductionPostingLink.cs

ErpWeb.Core/Planning/
    PrProductDefService.cs

ErpWeb.Core/Production/
    ProductDefinitionSnapshotLoader.cs
    WorkOrderSnapshotBuilder.cs
    WorkOrderQuantityCalculator.cs
    WorkOrderScheduleCalculator.cs
    ProductionCalendarScheduler.cs
    WorkOrderCalendarProvider.cs
    ProductionCalendarScheduleDataLoader.cs
    WorkOrderReadinessValidator.cs
    ProductionWorkOrderService.cs
    ProductionWorkOrderService.DraftCommands.cs
    ProductionWorkOrderRules.cs
    ProductionExecutionGate.cs

ErpWeb.UI/Planning/Masters/
    PrProductDefEntry.razor
    PrProductDefEntry.razor.cs
    PrCompanyCalendar.razor
    PrMachineShiftCalendar.razor

ErpWeb.UI/Planning/WorkOrders/
    PrWorkOrderEntry.razor
    PrWorkOrderEntry.razor.cs
    PrWorkOrderList.razor
    PrWorkOrderList.razor.cs

ErpWeb.Core/Inventory/
    IvInventoryPostingService.cs

ErpWeb.Model/Entities/Inventory/
    IvStockMaster.cs
    IvTrxBatchDetail.cs
    IvLot.cs

scripts/
    create-production-workorder.sql
    create-product-definition-routing.sql
    create-prdefbom.sql

ErpWeb.Tests/
    ProductionWorkOrderServiceTests.cs
    ProductionWorkOrderSqlServerConcurrencyTests.cs
    WorkOrderSnapshotBuilderTests.cs
    WorkOrderQuantityCalculatorTests.cs
    WorkOrderScheduleCalculatorTests.cs
    ProductionCalendarSchedulerTests.cs
    WorkOrderReadinessValidatorTests.cs
```

The existing repository plans were also checked for intent, but conclusions were based on actual current implementation, not plan text alone:

```text
plans/work-order-plan.md
plans/proddef-enhancement-plan.md
plans/wo_entry_enhancements_production_ready.plan.md
```

---

# Recommended Coding-Agent Handoff

Before implementation, give the coding agent the following non-negotiable direction:

> Preserve the existing versioned Product Definition and version-2 Work Order snapshot architecture. Do not replace the current snapshot/hash/UOM/calendar foundations. First fix the identified Product Definition authoring gaps, yield/output-type semantics, labour-basis mismatch, plant-calendar limitation, and backward-parallel scheduling defect. Then implement production execution as separate auditable source transactions that post to the existing Inventory architecture atomically and update Work Order quantities only as rebuildable projections. Keep finite-capacity scheduling as an additive machine-allocation layer rather than embedding other-Work-Order load directly into the snapshot tables.
