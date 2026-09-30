# Production Work Order Enhancement Plan

> **Document status.** This is the **candidate authoritative** Work Order enhancement plan.
> It is promoted to **authoritative** only after the [Gate before Milestone 1](#18-gate-before-milestone-1) checklist is satisfied.
> This revision folds in the approved architectural reviews: Milestone 0 (Product Definition readiness), the Snapshot Source Matrix,
> Option A1 labour ownership, the in-flight legacy status contract, atomic Draft recalculation, operation quantity/UOM propagation,
> fixed non-machine duration, labour rate UOM, aggregate row-version advancement, activity-level calendar provenance/staleness,
> deterministic horizon/locking, the frozen hash/token vocabulary, and `DefinitionValidityAtReleasePolicy`.
> [`plans/workorder-plan.md`](workorder-plan.md) is the older Phase-1 founding plan and is
> marked historical once this plan is promoted.

## 1. Objective

Complete and harden the current Production Work Order implementation so that it:

- preserves the proven legacy production concepts;
- snapshots the complete Product Definition hierarchy;
- correctly calculates what, where, how, and when to produce;
- preserves Work Center, Process, BOM, Machine, Labour, WIP, and Finished Good relationships;
- supports sequential and parallel production;
- keeps released Work Orders independent from later Product Definition changes;
- provides a professional Blazor UI consistent with Product Definition Entry; and
- establishes a safe foundation for material issue, WIP, Finished Goods, costing, and traceability.

This plan retains the new `PrWorkOrder*` aggregate. It does not return to the legacy `PrSch*` table design.

## 2. Verified Current State

The current implementation provides a useful Phase-1 foundation:

- manual Work Order creation;
- Product Definition/BOM resolution by as-of date;
- BOM quantity explosion;
- transactional Draft save;
- snapshot hash and snapshot revision;
- audit events;
- optimistic concurrency;
- Draft cancellation;
- Draft-to-Released transition;
- calendar-aware machine scheduling; and
- responsive Work Order list and entry screens.

Verification performed on 2026-09-29 (historical evidence, not living status):

- focused Work Order, BOM, and scheduling tests: 38 passed;
- all Planning-category tests: 101 passed;
- Model, Core, UI, and test projects compiled during the test run.

The legacy WebForms source is not present in this workspace. Legacy behavior is therefore considered confirmed only where it is documented as verified in `docs/WorkOrder_dataflowStudy.md`.

### 2.1 Verified gap claims

Each gap in section 3 was re-verified against the current code:

| Claim | Evidence |
|---|---|
| Flattened hierarchy | WO has operations and materials only; no `ProductionWorkOrderRouteStep` |
| Unique `SequenceNo` serializes parallel | Unique index `(WorkOrderId, SequenceNo)` and a builder that assigns `operationSequence++` |
| Header date-only loses time-of-day | SQL `date` on header `PlannedStartDate` / `PlannedCompletionDate` |
| Draft save silently rebuilds | Every `SaveDraftAsync` calls the snapshot builder and replaces children |
| Release validation incomplete | No readiness checks; a no-routing Draft can currently Release |
| Generic resources | `ProductionWorkOrderResource` with `MACHINE` / `LABOUR` instead of first-class entities |

## 3. Main Gaps

### 3.1 Flattened production hierarchy

The current Work Order snapshot flattens routing into operations and generic resources. It does not retain a separate Work Center/route-step snapshot.

Missing or incomplete relationships include:

- Work Center stage ownership;
- Work Center output/WIP item;
- separate Work Center and Process sequences;
- material-to-consuming-operation ownership;
- labour-to-machine ownership;
- machine default and sequence semantics;
- material issue method and supply source; and
- intermediate WIP dependencies.

### 3.2 Parallel sequences are not preserved

The legacy rule is:

```text
Same sequence = parallel
Next sequence = wait for the slowest preceding member
```

The current operation builder assigns a new unique sequential number to every operation, and the database requires Work Order operation sequence to be unique. This serializes or rejects valid parallel routing.

### 3.3 Scheduling loses time-of-day

Header planned dates are stored as SQL `date`. The legacy scheduling behavior and the current calendar engine both require date and time, including mid-shift anchors, breaks, overnight shifts, and precise completion times.

### 3.4 Draft save silently rebuilds from Product Definition

Saving an existing Draft currently rebuilds its snapshot from the applicable Product Definition. A normal header edit can therefore adopt a newer definition without a dedicated refresh decision.

### 3.5 Release validation is incomplete

An empty or incomplete routing can currently produce a warning and still be released. Release needs an explicit readiness validator.

### 3.6 Execution is intentionally incomplete

The following remain behind the execution gate:

- reservations and allocations;
- material issue and return;
- operation execution;
- WIP movement;
- Finished Goods receipt;
- costing and variance;
- reversals;
- completion and closure; and
- Change Order application.

## 4. Target Aggregate

```text
ProductionWorkOrder
├── RouteSteps / Work Centers
│   ├── Operations / Processes
│   │   ├── Materials
│   │   ├── Machines
│   │   │   └── Labour
│   │   └── Operation execution facts (future phase)
│   └── Output / WIP definition
├── Audit Events
├── Change Orders
└── Posting Links
```

Every snapshot row must contain enough copied information to remain meaningful if its source Product Definition changes later.

### 4.1 Frozen manufacturing decisions

The following decisions close the outstanding review questions and are binding for this milestone:

- machine cycles are discrete and always round up; continuous-process cycle math is out of scope;
- Product Definition machine rows are alternatives, not sequential activities: snapshot all eligible options, select exactly one default option, and schedule only the selected option;
- sequential machine activities must be represented as separate operations;
- setup and operation loss quantities are snapshotted for information only and do not gross-up material, operation, or machine requirements in this milestone;
- `DefinitionEffectiveDate` is an explicit snapshot input, defaults from the planned start date during creation, and changes only through an explicit definition refresh;
- copied route, operation, material, machine, and labour rows are read-only; planners may edit approved header planning inputs only;
- the initial labour rate basis is explicitly `PER_OUTPUT_UNIT`; hourly, per-minute, and fixed-operation labour bases are deferred;
- schedule timestamps use plant-local `DateTimeKind.Unspecified`, while audit timestamps remain UTC; and
- no historical Work Order may be reconstructed silently from the current Product Definition.

### 4.2 Frozen identity, hashing, and concurrency vocabulary

These names are binding across schema, services, DTOs, and UI. Do not reuse one name for two concepts.

| Token | Meaning |
|---|---|
| `WorkOrderRowVersion` | Optimistic concurrency token for the Work Order aggregate |
| `SourceProductDefinitionRevisionID` | Exact Product Definition revision identity used to create the snapshot |
| `DefinitionEffectiveDate` | The effective date used to resolve the source revision |
| `DefinitionSourceHash` | Canonical hash of the Product Definition source payload used during Refresh preview/confirm ("did the source definition change after preview?") |
| `DefinitionSourceHashVersion` | Canonicalization version for `DefinitionSourceHash`; version 1 in this milestone |
| `SnapshotHash` | Canonical hash of the persisted Work Order snapshot hierarchy ("did the saved snapshot itself change?") |
| `ScheduleSourceHash` (calendar/scheduling-source hash) | Hash of the calendar / machine / scheduling-source inputs used to detect stale schedules |
| `SnapshotRevisionNo` | Persisted snapshot-generation counter (column `SnapshotRevision`); increments once per committed canonical snapshot change (create, header recalculation, schedule recalculation, explicit refresh) |
| `SnapshotFormatVersion` | Snapshot schema format version (`1` legacy, `2` current) |

`SnapshotRevisionNo` is **not** the Product Definition revision number (`SourceRevisionNo`) and is **not** `SnapshotFormatVersion`. The previously ambiguous lone term `SnapshotRevision` is resolved to `SnapshotRevisionNo`.

### 4.3 Frozen domain policies

These are Milestone 0 decisions and are binding:

- **Defaulting constraint.** A Work Order default is allowed **only** when it preserves an existing documented business rule. Missing planning semantics must fail readiness / Release rather than be guessed. In particular, never silently default `IssueMethod`, `SupplySource`, the internal-WIP producing route, the selected machine alternative, a UOM conversion factor, a routing parent/child relationship, or any cycle/rate field that affects duration.
- **Revision overlap policy.** Product Definition activation **rejects** an overlapping active effective interval for the same company and product; it does not silently supersede. The Work Order resolver and the Product Definition activation rule must agree.
- **`DefinitionValidityAtReleasePolicy` = `FROZEN_SNAPSHOT_ALLOWED`.** A Draft that was built from a valid, fully approved snapshot may Release from that frozen source revision even if the revision has since been superseded. Release never silently advances to a newer revision. Explicit Refresh remains the only way to adopt a newer revision. Rationale: the objective states released Work Orders stay independent of later Product Definition changes, and "a Product Definition change alone does not stale an unchanged saved Work Order snapshot".
- **Canonical restrictive action.** Use EF `DeleteBehavior.NoAction` and SQL `ON DELETE NO ACTION`. Do not alternate with `RESTRICT` terminology.
- **Parallel sequencing.** Equal sequence values are intentionally valid when parallel execution is supported and no dependency edge forces serial order.

### 4.4 Dependency order

```text
Milestone 0  Product Definition readiness / Work Order Snapshot Source Matrix
Milestone 1  Work Order schema + migration compatibility
Milestone 2  Snapshot loader / builder / quantity contracts
Milestone 3  Scheduling + WIP dependency graph
Milestone 4  Readiness validation + Release concurrency
Milestone 5  Work Order UI / UAT
Milestone 6  Execution-posting gate
```

Milestone 1 schema work starts only when the [Gate before Milestone 1](#18-gate-before-milestone-1) checklist is complete.

## 5. Milestone 0 — Product Definition Readiness and Snapshot Source Matrix

A Work Order snapshot cannot be more complete than its authoritative Product Definition source. Milestone 0 establishes, for every field the snapshot needs, whether Product Definition can author it, whether it is deterministically derived, whether a documented default applies, or whether it is unsupported and therefore Release-blocking.

### 5.1 Current Product Definition authoring gap

| Capability the Work Order snapshot needs | Product Definition state today |
|---|---|
| Route steps (`PrBomRouteStep`, `RouteStepId`, `OutputType`, yield) | Entity/schema exist; the service never writes route steps |
| Multiple machine alternatives | Schema supports options, but the live service rejects more than one machine per process |
| `ProcessType`, `IssueMethod`, `SupplySource` | Columns/defaults exist; not exposed on Product Definition view models or UI |
| `OutputPerCycle`, `MachineRatePerHour` | Not editable through the live save path |
| `ProducingRouteStepID` for `INTERNAL_ROUTE_WIP` | Does not exist on `PrDefBOM` |
| Operation-level / richer labour (`PrBomLabourRequirement`) | Schema only; live labour is `PrBomLabourStandard` under the single machine |
| UOM conversion | `IvItemUomConversion` exists; no Core conversion service consumes it |

### 5.2 Snapshot Source Matrix

Every field required by the snapshot builder must appear here with an explicit command impact. Impact values:

| Impact | Meaning |
|---|---|
| Block Build | Cannot create or refresh a valid version-2 snapshot |
| Block Recalculate | Quantity or schedule recalculation is unsafe |
| Block Release | A persisted Draft cannot Release |
| Warning Only | Non-blocking advisory |
| N/A | The command does not use the field |

| Snapshot field | Product Definition source | Authorable today | Allowed default | Build | Recalculate | Release |
|---|---|---|---|---|---|---|
| Route step | Product Definition route step | No / incomplete | No | Block | N/A | Block |
| Operation | Product Definition process | Yes | No | Block | N/A | Block |
| Process type | Product Definition process | Incomplete | No | Block missing/unknown | Block missing/unknown | Block missing/unknown |
| Operation input/output UOM | Route-step output plus approved process/WIP conversion | Partial | Same-UOM only under an explicit rule | Block if unresolved | Block | Block |
| Non-machine duration basis | Frozen Work Order/Product Definition contract | Derived as `FIXED_OPERATION` | Yes, documented constant | N/A | Block unknown basis | Block unknown basis |
| Machine alternatives | Product Definition machine options | Constrained to one | No silent synthesis | Block unsupported definition | Block | Block |
| Labour | Product Definition labour standard / requirement | Partial | No synthetic row | Invalid authored row blocks; zero rows allowed | N/A | Warning if none; block invalid row |
| Labour rate UOM | Owning operation output UOM or explicit labour source UOM | Missing | Current legacy rate uses owning output UOM | Block contributing row if unresolved | Block | Block |
| Issue method | BOM | Not authored | No | Block missing/unknown | N/A | Block missing/unknown |
| Supply source | BOM | Not authored | No | Block | N/A | Block |
| Producing route step | BOM / internal-WIP mapping | Missing | No | Block for `INTERNAL_ROUTE_WIP` | Block schedule dependencies | Block |
| Output per cycle | Machine standard | Incomplete | Only with an explicit legacy rule | Block for machine-based process; N/A for duration-based | Block for machine-based process | Block for machine-based process |
| Output-per-cycle UOM | Owning operation output UOM under the current source contract | Derived | Explicit documented derivation | Block if unresolved | Block | Block |
| Machine rate per hour | Machine standard | Incomplete | Info-only this milestone | Allow (costing deferred) | N/A | Allow (not duration) |
| UOM conversion | Inventory UOM source | Schema only | No guessed factor | Block if conversion required | Block | Block |

Duration-affecting fields that are missing always Block Recalculate and Release.

### 5.3 Labour authorship contract

- The supported live source is `PrBomLabourStandard` under the selected machine, with `PER_OUTPUT_UNIT` cost.
- Operation-level labour and richer labour (headcount, planned minutes) require either adopting `PrBomLabourRequirement` in Product Definition or synthesizing Work Order rows under a documented rule. This plan adopts `PrBomLabourRequirement` rows when present and falls back to `PrBomLabourStandard` otherwise; the chosen source is recorded on the snapshot row.
- Labour belonging to an unselected machine alternative is snapshotted with `ContributesToPlan = false` and `PlannedAmount = 0`.
- Zero labour rows are permitted in this milestone and produce a readiness warning, not an invented standard. Any authored labour row must have valid ownership, non-negative rate, and a resolvable `RateUOM`; an invalid contributing row blocks Build/Recalculate/Release.

### 5.4 Internal-WIP producer contract

- `ProducingRouteStepID` is added to the source mapping for `INTERNAL_ROUTE_WIP` materials, or resolved deterministically from route-step output items when exactly one producer exists.
- The producer must belong to the same Work Order, its output item must equal the consumed WIP item, there must be no self-dependency or cycle, and the producer must precede the consumer unless the frozen parallel policy explicitly permits otherwise.
- An internal WIP item must have exactly one resolvable producer.
- Item-match, same-Work-Order membership, and cycle detection are service-level validations. The `INTERNAL_ROUTE_WIP` ↔ non-null producer relationship is enforced in SQL with a NULL-safe check (section 6.3).

### 5.5 UOM conversion contract

- A Core UOM conversion service is introduced over `IvItemUomConversion` and is the single place conversion happens.
- Every conversion factor used by a quantity or duration equation is snapshotted on the affected material or machine row.
- A missing or incompatible conversion is Release-blocking.

### 5.6 Milestone 0 deliverables

- The Snapshot Source Matrix (5.2) completed against the live Product Definition write path.
- The unsupported Product Definition combinations enumerated, each mapped to a blocking error code.
- The labour authorship, internal-WIP producer, and UOM conversion contracts frozen.
- Product Definition authoring gaps scheduled (route steps, machine alternatives, `ProcessType` / `IssueMethod` / `SupplySource`, `OutputPerCycle`, `ProducingRouteStepID`, UOM service) so Milestone 1 acceptance is reachable.

### 5.7 Milestone 0 acceptance

- No snapshot field is silently guessed; every row in 5.2 has an explicit source/default/impact classification.
- Every unsupported combination returns a stable error code and blocks the affected command.
- Every field designated authorable is reachable through Product Definition UI/API.
- Unit tests cover the source / derive / default / block decision per field.

## 6. Milestone 1 — Correct the Snapshot Schema

This milestone must be completed before Work Orders are used for real production.

### 6.1 Add `PrWorkOrderRouteStep`

Create:

- `ErpWeb.Model/Entities/Production/ProductionWorkOrderRouteStep.cs`
- `ErpWeb.Model/Configurations/Production/ProductionWorkOrderRouteStepConfiguration.cs`
- corresponding changes in `AppDbContext`;
- an idempotent SQL upgrade in `scripts/create-production-workorder.sql`.

Required fields:

```text
UID
WorkOrderID
SourceRouteStepID nullable
SourceRouteStepKey nullable
StageSequence
WorkCentreCode
WorkCentreDescription
OutputItemCode
OutputItemDescription
OutputBaseQty
OutputUOM
PlannedQty
PlannedStartDateTime
PlannedCompletionDateTime
CreatedDate
CreatedBy
ModifiedDate
ModifiedBy
RowVersion
```

Constraints:

- `StageSequence` must not be unique;
- equal stage sequences represent parallel route steps;
- use `(WorkOrderID, SourceRouteStepKey)` as the preferred unique source identity when available;
- require `StageSequence > 0`, `OutputBaseQty > 0`, and `PlannedQty >= 0`; and
- use `ON DELETE CASCADE` from Work Order to route step.

### 6.2 Expand `PrWorkOrderOperation`

Add:

```text
RouteStepID
SourceOperationID nullable
SourceOperationKey nullable
ProcessSequence
ProcessType
StandardDurationMinutes
NonMachineDurationBasis nullable
PlannedInputQty
PlannedInputUOM
PlannedOutputQty
PlannedOutputUOM
CalendarSourceType nullable
CalendarSourceID nullable
CalendarSourceLastModified nullable
ScheduleSourceHash nullable
CalendarHorizonStart nullable
CalendarHorizonEnd nullable
PlannedStartDateTime
PlannedCompletionDateTime
```

Retain:

```text
OperationCode
OperationDescription
IsFinalOperation
SetupLossQty
OperationLossQty
Execution projections
```

Replace the unique `(WorkOrderID, SequenceNo)` index with:

```text
UNIQUE(RouteStepID, SourceOperationKey) where SourceOperationKey is not null
INDEX(RouteStepID, ProcessSequence)
```

`ProcessSequence` must not be unique if parallel processes are supported.

Each active route step must have exactly one `IsFinalOperation` row. The terminal route step is validated separately by matching its output item to the Work Order finished good.

`PlannedInputQty` is expressed in `PlannedInputUOM`, and `PlannedOutputQty` is expressed in `PlannedOutputUOM`. Under the current Product Definition model, `PlannedOutputUOM` is authored by the owning route-step output. `PlannedInputUOM` must be authored or derived by an approved process/WIP conversion rule; it may equal the output UOM only when that same-UOM rule is explicit. Otherwise snapshot build and Release fail with `WO_OPERATION_UOM_INVALID`. Any conversion between an operation quantity, route output, material BOM denominator, or machine output-per-cycle basis must be explicit and snapshotted; never infer a UOM from item code alone.

For a version-2 duration-based operation, `NonMachineDurationBasis` must be `FIXED_OPERATION`; for a machine-timed operation it must be null. Enforce this relationship in service validation and with a NULL-safe database check where practical.

### 6.3 Expand `PrWorkOrderMaterial`

Add:

```text
WorkOrderOperationID nullable
SourceOperationID nullable
SourceMaterialID nullable
SourceMaterialKey nullable
MaterialSequence
IssueMethod
SupplySource
ProducingRouteStepID nullable
StandardQty
StandardUOM
MaterialBomOutputQty
MaterialBomOutputUOM
ScrapPercent
TolerancePercent
RequiredQty
RequiredUOM
RequiredBaseQty
BaseUOM
ConversionFactorToBase
```

Retain separate planned and actual values:

```text
ReservedQty
PickedQty
IssuedQty
ReturnedQty
ConsumedQty
VarianceQty
```

All routed Product Definitions must assign a material to its consuming operation before activation and Work Order release.

`RequiredQty` is always expressed in `RequiredUOM`. `RequiredBaseQty` is the inventory-posting quantity expressed in `BaseUOM`, and `ConversionFactorToBase` is the number of base units represented by one required unit. `INTERNAL_ROUTE_WIP` materials must identify their unique producer through `ProducingRouteStepID`; do not infer an ambiguous producer from item code at scheduling time.

**NULL-safe `SupplySource` check.** SQL Server `CHECK` constraints treat `UNKNOWN` differently from `FALSE`, so a naive `IF` expression can admit rows that should be rejected when `SupplySource` is nullable. Make the semantically required column `NOT NULL` where possible and include `NULL` explicitly:

```text
CHECK (
      (SupplySource = 'INTERNAL_ROUTE_WIP'
          AND ProducingRouteStepID IS NOT NULL)
   OR (SupplySource IS NOT NULL
          AND SupplySource <> 'INTERNAL_ROUTE_WIP'
          AND ProducingRouteStepID IS NULL)
)
```

Use the real persisted enum representation in the actual script. Legacy version-1 rows are exempted explicitly by `SnapshotFormatVersion = 1`, not by relying on NULL semantics. Item-match, same-Work-Order membership, and cycle detection remain service-level validations.

### 6.4 Add explicit machine and labour snapshots

Create `PrWorkOrderMachine` with:

```text
UID
OperationID
SourceMachineOptionID nullable
SourceMachineKey nullable
Priority
MachineCode
MachineDescription
IsDefault
IsSelected
ParallelMachineCount
CycleQuantityMode
CycleSeconds
OutputPerCycle
OutputPerCycleUOM
RequiredMachineOutputQty
RequiredMachineOutputUOM
PlannedCycleCount
PlannedCycleSlots
PlannedRunMinutes
ConversionSeconds
SetupSeconds
QueueSeconds
MachineRatePerHour
CalendarSourceID nullable
CalendarSourceLastModified nullable
ScheduleSourceHash nullable
CalendarHorizonStart nullable
CalendarHorizonEnd nullable
PlannedStartDateTime
PlannedCompletionDateTime
CreatedDate
CreatedBy
RowVersion
```

`CycleQuantityMode` is fixed to `DISCRETE` in this milestone. Snapshot all eligible alternatives and set `IsSelected = IsDefault` during snapshot creation. Exactly one selected row is required for a machine-based operation. Planner machine override is out of scope. Use only the selected row for duration; `ParallelMachineCount` is capacity within that option and does not mean that multiple alternative rows execute together. The current Product Definition contract defines `OutputPerCycle` in the owning operation's output UOM, so snapshot that value explicitly as `OutputPerCycleUOM = PlannedOutputUOM`; if a future source supplies a separate UOM, use it only with an explicit snapshotted conversion.

Create `PrWorkOrderLabour` with:

```text
UID
OperationID nullable
MachineID nullable
SourceLabourID nullable
SourceLabourKey nullable
LabourCode
LabourDescription
PlannedUnits nullable
PlannedMinutes nullable
RateBasis
RateUOM
Rate
LabourBasisQty
ConversionFactorToRateUOM
ContributesToPlan
PlannedAmount
CreatedDate
CreatedBy
RowVersion
```

Persist `RateBasis = PER_OUTPUT_UNIT` for the currently supported Product Definition labour standard. `RateUOM` is mandatory for a contributing version-2 labour row and identifies the output UOM to which one rate unit applies. Derive `LabourBasisQty` by converting the owning operation's planned output from `PlannedOutputUOM` to `RateUOM`, snapshot `ConversionFactorToRateUOM`, and calculate `PlannedAmount = Rate * LabourBasisQty`. The legacy `CostPerOutputUnit` source is treated as authored per owning-operation output UOM and snapshots that UOM explicitly; if the owning output UOM is unresolved, Build/Recalculate/Release fail rather than guessing.

Operation-level labour and labour linked to the selected machine use `ContributesToPlan = true`. Labour linked to an unselected machine alternative remains in the snapshot for traceability with `ContributesToPlan = false` and `PlannedAmount = 0`. `PlannedUnits` and `PlannedMinutes` are nullable informational standards copied only when the source provides them; they do not participate in the `PER_OUTPUT_UNIT` equation. Do not infer the rate or UOM basis from which numeric field is nonzero.

**Option A1 — exclusive owner with a single cascade path.** A single `PrWorkOrderLabour` table is used, but the earlier dual-cascade design (`Operation → Labour CASCADE` alongside `Operation → Machine → Labour CASCADE`) is **not** safe on SQL Server: SQL Server evaluates FK cascade paths on schema structure, not on a row-level `CHECK`, so two structural paths from `Operation` to `Labour` can be rejected as a multiple-cascade-path error even though the exclusive-owner `CHECK` prevents any single row from using both.

Frozen design:

```text
CHECK: exactly one of (MachineID, OperationID) is non-null
  — machine-owned labour: MachineID set, OperationID null
  — operation-level labour: OperationID set, MachineID null

Operation -> Machine                        CASCADE
Machine   -> machine-owned Labour           CASCADE
Operation -> direct operation-level Labour  ON DELETE NO ACTION
any secondary/foreign cross-reference FK    ON DELETE NO ACTION
```

The aggregate/service explicitly deletes direct operation-level Labour rows before deleting an Operation:

```text
Delete Operation
  1. delete Labour where OperationID = operation   (explicit)
  2. delete Operation
       -> Machine cascades
          -> machine-owned Labour cascades
```

This leaves exactly one structural cascade path from `Operation` to `Labour` (via `Machine`). Option B (separate `PrWorkOrderMachineLabour` / `PrWorkOrderOperationLabour` tables) is noted as an alternative but is not selected; do not mix the two designs.

The current generic resource structure may remain temporarily for compatibility, but new Work Orders must use the explicit machine and labour relationships. Remove the generic structure after migration and report compatibility have been verified.

### 6.5 Preserve full schedule timestamps

Change header schedule fields from SQL `date` to `datetime2`:

```text
PlannedStartDateTime
PlannedCompletionDateTime
```

Use plant-local `DateTimeKind.Unspecified` for production scheduling. Continue to use UTC for audit timestamps.

Use the same `PlannedStartDateTime` and `PlannedCompletionDateTime` names and SQL `datetime2` type on the Work Order, route step, operation, and selected machine snapshot. Do not retain date-only operation columns.

### 6.6 Snapshot metadata, constraints, and ownership

Add to `PrWorkOrder`:

```text
DefinitionEffectiveDate
SourceProductDefinitionRevisionID
SourceRevisionNo
SourceEffectiveFrom
ProductDefinitionBaseQty
ProductDefinitionBaseUOM
SnapshotFormatVersion
SnapshotHashVersion
IsLegacySnapshot
LegacySnapshotReason nullable
ScheduleAnchorDateTime
SchedulingDirection
ScheduleCalculationTrace nullable
```

Map compatibility columns instead of duplicating them: `SnapshotAsOfDate` becomes `DefinitionEffectiveDate`, `SourceBomHdrID` is the physical source revision ID, `SourceBomVersion` is the source revision number, and `BomBaseQty/BomBaseUOM` are the Product Definition base values unless a deliberate database rename is approved. New snapshots use `SnapshotFormatVersion = 2`; upgraded Phase-1 rows remain version 1. The persisted `SnapshotRevision` column is the `SnapshotRevisionNo` counter defined in 4.2.

Every input used by a quantity, duration, schedule, or planned-labour-amount equation must be copied into the Work Order snapshot or represented by the exact immutable source revision. Prefer copying the manufacturing calculation inputs so that a saved Work Order can be explained without rereading current master data.

**Single cascade ownership path:**

```text
WorkOrder -> RouteStep -> Operation -> Material
                                  -> Machine -> Labour
                                  -> operation-level Labour
```

Avoid redundant cascading Work Order foreign keys on descendants. Add database checks for positive sequences, `ParallelMachineCount >= 1`, `OutputPerCycle > 0`, `ConversionFactorToBase > 0`, and non-negative planned quantities, standards, times, losses, and rates. Service validation remains mandatory.

**Foreign-key delete matrix.** Every relationship gets an explicit delete behavior using the canonical restrictive action `ON DELETE NO ACTION`:

| Relationship | Delete behavior |
|---|---|
| Work Order → Route step | `CASCADE` |
| Route step → Operation | `CASCADE` |
| Operation → Material | `CASCADE` |
| Operation → Machine | `CASCADE` |
| Machine → machine-owned Labour | `CASCADE` |
| Operation → direct operation-level Labour | `ON DELETE NO ACTION` (service deletes explicitly) |
| Material.`ProducingRouteStepID` → Route step | `ON DELETE NO ACTION` |
| Any other cross-branch foreign key | `ON DELETE NO ACTION` |

Add filtered source-identity uniqueness where a stable key exists:

```text
UNIQUE(WorkOrderID, SourceRouteStepKey) WHERE SourceRouteStepKey IS NOT NULL
UNIQUE(RouteStepID, SourceOperationKey) WHERE SourceOperationKey IS NOT NULL
UNIQUE(WorkOrderOperationID, SourceMaterialKey) WHERE SourceMaterialKey IS NOT NULL
UNIQUE(OperationID, SourceMachineKey) WHERE SourceMachineKey IS NOT NULL
UNIQUE(OperationID) WHERE IsSelected = 1
UNIQUE(OperationID) WHERE IsDefault = 1
UNIQUE(OperationID, SourceLabourKey) WHERE MachineID IS NULL AND SourceLabourKey IS NOT NULL
UNIQUE(MachineID, SourceLabourKey) WHERE MachineID IS NOT NULL AND SourceLabourKey IS NOT NULL
```

`PrWorkOrderMaterial.WorkOrderOperationID` remains nullable in SQL only for legacy version-1 compatibility. Every `SnapshotFormatVersion >= 2` material must have a consuming operation, enforced by snapshot building and Release readiness. Apply the same pattern to other nullable source/ownership fields retained solely for legacy rows.

The filtered machine indexes enforce at most one selected and at most one default option. Snapshot building and Release readiness enforce the remaining at-least-one rule for machine-based operations.

**Nullable compatibility deprecation lifecycle:**

```text
v1 legacy rows
  -> nullable compatibility allowed
  -> new v2 rows require full relationships
  -> legacy marker
  -> future cleanup removes nullable compatibility if practical
```

**Snapshot provenance.** The snapshot stores or audits, without duplicating columns where an equivalent audit event already exists: `SourceProductDefinitionRevisionID`, `DefinitionEffectiveDate`, `SnapshotFormatVersion`, `SnapshotHash`, `SnapshotHashVersion`, `SnapshotRevisionNo`, and the create/refresh actor and timestamp. The explicit-refresh audit event records the refresh reason.

**Aggregate row-version invariant.** Every successful mutation of the Work Order aggregate must issue exactly one update to the `PrWorkOrder` header inside the same transaction so SQL Server advances `WorkOrderRowVersion`, even when only child rows or schedule provenance changed. Schedule recalculation, explicit refresh, child replacement, audit-affecting canonical edits, Release, and cancellation all follow this rule. A failed or rolled-back command does not advance the header row version.

### 6.7 Legacy snapshot migration

The SQL upgrade must not reconstruct existing Work Orders from today's Product Definition. Mark all existing records as `SnapshotFormatVersion = 1` and `IsLegacySnapshot = true`, retaining their existing source identifiers and data unchanged.

Legacy status contract:

| Legacy status | Snapshot structure | Operational behavior |
|---|---|---|
| Draft | Compatibility / refreshable | Must explicit-refresh to version 2 before Release |
| Released | Frozen version-1 snapshot | Continue approved version-1 execution, or finish before cutover |
| In Progress | Frozen version-1 snapshot | Continue approved version-1 execution |
| Completed | Immutable | Historical read-only |
| Closed | Immutable | Historical read-only |
| Cancelled | Immutable | Historical read-only |

For a legacy `Released` / `In Progress` record: the snapshot hierarchy is frozen and never rebuilt from the current Product Definition, only the Phase-1 execution actions required to finish the Work Order are permitted, no version-2-only structural editing is allowed, and the record may progress toward Completed / Closed / Cancelled. The correct rule is "never rebuild a legacy Work Order's snapshot structure", which is not the same as "never allow further operational transactions".

If Phase-1 execution posting is not yet implemented, the migration/deployment plan (section 16) must freeze an operational cutover rule before version-2 Release is enabled: either finish all existing version-1 `Released` / `In Progress` Work Orders first, or provide a documented compatibility execution path for them.

- a legacy Draft must complete an explicit, user-confirmed definition refresh before Release;
- the refresh produces the current snapshot format, clears the legacy flag, records the prior reason, increments `SnapshotRevisionNo`, and writes an audit event; and
- existing finished legacy records remain readable and immutable as legacy snapshots.

### 6.8 Milestone 1 acceptance

- Multiple centers and processes may share a sequence.
- Work Center output/WIP information survives snapshotting.
- Every routed material identifies its consuming process.
- Labour retains its machine relationship.
- Schedule timestamps retain time-of-day.
- Every formula input has a defined snapshot field and UOM basis.
- Alternative machines cannot be mistaken for sequential or parallel required steps.
- The actual SQL Server cascade graph installs without multiple-cascade-path errors, including the single structural path from Operation to Labour.
- The `INTERNAL_ROUTE_WIP` producer check is NULL-safe and exempts only explicit legacy rows.
- SQL creation and upgrade scripts are repeatable on SQL Server.
- Existing Phase-1 records are marked and handled by the explicit legacy policy.

### 6.9 Stable persisted-value contracts

Follow the repository convention of static domain classes containing normalized string constants plus `IsKnown` validation; do not scatter literals through services or UI code. Persist only these values in this milestone:

Reuse `PrProcessTypes`, `PrMaterialIssueMethods`, `PrMaterialSupplySources`, and `ProductionSchedulingDirections`. Add equivalent centralized classes for machine cycle quantity mode, non-machine duration basis, labour rate basis, and calendar source type.

```text
ProcessType:
  MACHINE, AUTOMATED, MANUAL, INSPECTION, WAIT, PACKING, SUBCONTRACT

IssueMethod:
  MANUAL, BACKFLUSH, PICK_LIST

SupplySource:
  PURCHASED, INTERNAL_ROUTE_WIP, SEPARATE_PRODUCT_DEFINITION, EXTERNAL_SUPPLY

CycleQuantityMode:
  DISCRETE

LabourRateBasis:
  PER_OUTPUT_UNIT

NonMachineDurationBasis:
  FIXED_OPERATION

SchedulingDirection:
  FORWARD, BACKWARD

CalendarSourceType:
  MACHINE, PLANT_DEFAULT
```

Process scheduling behavior is fixed as follows:

| Process type | Duration source | Calendar source | Machine requirement |
|---|---|---|---|
| `MACHINE`, `AUTOMATED` | selected-machine timing | selected machine calendar | exactly one selected machine |
| `MANUAL`, `INSPECTION`, `WAIT`, `SUBCONTRACT` | fixed-operation `StandardDurationMinutes` | plant-default calendar stored on the operation | no machine required |
| `PACKING` with eligible machines | selected-machine timing | selected machine calendar | exactly one selected machine |
| `PACKING` without machines | fixed-operation `StandardDurationMinutes` | plant-default calendar stored on the operation | positive standard duration required |

Unknown persisted values are validation errors and block Release with `WO_PROCESS_TYPE_INVALID` or the corresponding field validation result.

## 7. Milestone 2 — Refactor Snapshot Creation

### 7.1 Add focused services

Create:

```text
IProductDefinitionSnapshotLoader
ProductDefinitionSnapshotLoader

IWorkOrderSnapshotBuilder
WorkOrderSnapshotBuilder

IWorkOrderQuantityCalculator
WorkOrderQuantityCalculator

IWorkOrderScheduleCalculator
WorkOrderScheduleCalculator

IWorkOrderReadinessValidator
WorkOrderReadinessValidator
```

Keep `ProductionWorkOrderService` as the transaction, permission, lifecycle, and command coordinator.

### 7.2 Snapshot loading flow

```text
Select Product
  -> choose DefinitionEffectiveDate
  -> resolve one exact applicable Product Definition/BOM revision
  -> load route steps
  -> load operations
  -> load default materials
  -> load every eligible machine option and identify the one default selection
  -> load labour
  -> validate parent-child relationships
  -> calculate quantities
  -> calculate schedule
  -> build independent Work Order snapshot
  -> persist atomically
```

Load version-owned rows by their source IDs and keys, not only by Product Code.

Resolve the revision once by company, product, active status, and the half-open effective interval `[EffectiveFrom, EffectiveTo)` containing `DefinitionEffectiveDate`; null bounds are unbounded. Then load all route, operation, material, machine, and labour rows owned by that exact revision under one coherent read transaction. Snapshot the revision ID, revision number, and effective date. Do not issue independent "current row" queries that can observe different revisions.

Resolution must be cardinality-safe:

- zero matching active revisions returns `WO_DEFINITION_REVISION_NOT_FOUND`;
- more than one matching active revision returns `WO_DEFINITION_REVISION_AMBIGUOUS`; and
- the loader must never break an overlap by choosing the highest revision, latest creation date, or first row.

Product Definition activation must also reject overlapping active effective intervals for the same company and product (section 4.3) before Work Order creation or refresh can encounter them.

On create, default `DefinitionEffectiveDate` from `PlannedStartDateTime.Date`. A later header-only planned-date change does not silently change it. A planner may propose another effective date only through the refresh preview and confirmation workflow.

**Stored revision identity after create.** Persist `SourceProductDefinitionRevisionID`, `DefinitionEffectiveDate`, `SnapshotFormatVersion`, `SnapshotHash`, and `SnapshotHashVersion`. Then an ordinary header save does not resolve the revision again, explicit Refresh re-resolves against the confirmed `DefinitionEffectiveDate`, and Release validates the persisted snapshot rather than resolving a revision. Under `DefinitionValidityAtReleasePolicy = FROZEN_SNAPSHOT_ALLOWED`, Release does not require the source revision to remain active and never silently advances the revision.

### 7.3 Quantity contract

**Operation quantity propagation contract.** Until process yield, transformation, or quantity-allocation rules are introduced, every active operation receives the full owning route-step planned quantity:

```text
Operation.PlannedInputQty =
    RouteStep.PlannedQty converted from RouteStep.OutputUOM
    to Operation.PlannedInputUOM

Operation.PlannedOutputQty =
    RouteStep.PlannedQty converted from RouteStep.OutputUOM
    to Operation.PlannedOutputUOM
```

This rule applies to sequential, parallel, machine, manual, inspection, wait, packing, and subcontract operations. Equal `ProcessSequence` means parallel timing, not quantity splitting; each parallel operation receives the full route-step quantity. Material ownership determines which consuming operation provides the material basis. No quantity is divided among parallel processes unless a future explicit allocation contract replaces this rule. Any unresolved conversion blocks Build, Recalculate, and Release with `WO_OPERATION_UOM_INVALID` or `WO_UOM_CONVERSION_MISSING`.

Use:

```text
RoutePlannedQty =
    WorkOrderQty * RouteOutputBaseQty / ProductDefinitionBaseQty

MaterialRequiredQty =
    OperationMaterialBasisQty
    * MaterialStandardQty / MaterialBomOutputQty
    * (1 + ScrapPercent / 100)

OperationMaterialBasisQty =
    ConsumingOperation.PlannedOutputQty converted from PlannedOutputUOM
    to MaterialBomOutputUOM

MachineCycleCount =
    Ceiling(RequiredMachineOutputQty / OutputPerCycle)

RequiredMachineOutputQty =
    Operation.PlannedOutputQty converted from PlannedOutputUOM
    to the selected Machine.OutputPerCycleUOM

MachineCycleSlots =
    Ceiling(MachineCycleCount / ParallelMachineCount)

MachineRunMinutes =
    MachineCycleSlots * CycleSeconds / 60

LabourBasisQty =
    ConsumingOperation.PlannedOutputQty converted from PlannedOutputUOM
    to Labour.RateUOM

LabourPlannedAmount =
    Labour.Rate * LabourBasisQty
```

Rules:

- calculate intermediate values at high precision;
- store quantities to four decimals using `MidpointRounding.AwayFromZero`;
- normalize formula inputs to their defined base UOM before arithmetic, then convert the stored/display quantity to `RequiredUOM`;
- do not add tolerance to required quantity;
- use tolerance as an issue/variance control;
- reject missing or zero Product Definition base quantity;
- reject missing or zero route output quantity;
- reject missing or zero machine `OutputPerCycle`;
- for migrated definitions, initialize `OutputPerCycle` from the legacy Work Center standard pack/output quantity;
- apply `Ceiling` to both discrete cycle count and parallel cycle slots;
- calculate machine duration from only the selected machine option;
- keep `MachineRatePerHour` as copied source data for the future costing phase, but do not calculate or store a planned machine amount in this milestone; and
- preserve setup and operation losses as separate informational planned values without grossing up any requirement in this milestone.

Machine cycles use the owning operation's planned output quantity and never gross-up from `SetupLossQty` or `OperationLossQty`. A missing conversion from `PlannedOutputUOM` to `OutputPerCycleUOM` is release-blocking.

The Work Order must retain `ProductDefinitionBaseQty`, each `RouteOutputBaseQty`, each `MaterialBomOutputQty`, all associated UOMs and conversions, scrap and tolerance percentages, output-per-cycle, timing standards, parallel count, and applicable rates. Recalculation must be reproducible from the snapshot without querying the current Product Definition.

### 7.4 Split Draft commands

Replace the current save/rebuild coupling with:

```text
CreateDraftAsync
UpdateDraftHeaderAsync
RecalculateDraftScheduleAsync
PreviewRefreshFromDefinitionAsync
RefreshDraftFromDefinitionAsync
ReleaseAsync
CancelDraftAsync
```

An ordinary Draft save must not load the current Product Definition or silently replace snapshot rows.

`UpdateDraftHeaderAsync` may change planned quantity, schedule anchor, scheduling direction, source reference, and remark according to validation rules. Route, operation, material, machine, and labour snapshot rows are read-only in this milestone.

The command uses atomic recalculation, not a persisted stale-calculation state:

- changing `PlannedQty` recalculates route and operation quantities, material requirements, selected-machine cycle counts/durations, contributing labour amounts, affected schedule timestamps, calendar provenance, and `SnapshotHash` in the same transaction;
- changing `ScheduleAnchorDateTime` or `SchedulingDirection` recalculates all affected route, operation, and selected-machine timestamps, calendar provenance, scheduling trace, and `SnapshotHash` in the same transaction;
- changing only source reference or remark leaves production calculations unchanged but rebuilds `SnapshotHash` because both fields are canonical hash inputs;
- every successful canonical snapshot change increments `SnapshotRevisionNo` exactly once and advances optimistic concurrency state; and
- a version-2 Draft is never committed with header planning inputs that disagree with its derived quantities or schedule.

`RecalculateDraftScheduleAsync` exists for external scheduling-input changes such as machine calendar, shift, holiday, or preventive-downtime edits. It reloads current scheduling sources, recalculates timestamps and calendar provenance, rebuilds the hash, increments `SnapshotRevisionNo`, and persists atomically without refreshing Product Definition structure.

### 7.5 Explicit definition refresh

Refresh must:

1. be allowed only for Draft Work Orders;
2. resolve the exact revision applicable to the confirmed `DefinitionEffectiveDate`;
3. compare it with the saved Work Order snapshot;
4. return added, removed, and changed rows;
5. require user confirmation and a reason;
6. replace the snapshot in one transaction;
7. increment `SnapshotRevisionNo`; and
8. write a detailed audit event.

Released, In Progress, Completed, Closed, and Cancelled Work Orders must never refresh from Product Definition.

Changing the Product Definition by itself does not stale or mutate a saved Work Order. Refresh comparison matches rows by stable source key first and source ID second, and reports calculation-input changes as well as added or removed rows.

### 7.6 Canonical snapshot hash

Define `SnapshotHashVersion = 1` as SHA-256 over a canonical UTF-8 representation of the manufacturing snapshot and its planned schedule.

Include:

- product and exact source revision identity;
- `DefinitionEffectiveDate`, Product Definition base quantity/UOM, Work Order planned quantity, scheduling direction, and anchor;
- all route, operation, material, machine, and labour source identities and copied calculation inputs;
- the selected-machine flag, calculated planned quantities, per-activity `ScheduleSourceHash` and horizon, and planned timestamps; and
- header source reference and remark.

Exclude lifecycle status, mutable audit fields, `RowVersion`, database-generated Work Order child IDs, and future actual execution quantities. Encode null explicitly, use invariant decimal scales and round-trip plant-local timestamps, and length-prefix text values. Canonically order each child collection by source key, source ID, sequence, code, and the level's remaining stable business discriminator. Reject duplicate canonical identities for version-2 snapshots instead of relying on input or database order. Hash computation during preview, persistence, reload, and Release must use the same canonicalizer.

`SnapshotHash` intentionally represents the entire releasable planning document, not only numeric manufacturing calculations. Source reference and remark therefore remain canonical inputs: a remark-only edit advances `SnapshotRevisionNo`, rebuilds the hash, and invalidates an older Release preview even though quantities and timestamps remain unchanged. Do not introduce a separate planning-document hash in this milestone.

A `SnapshotFormatVersion` bump does not by itself bump `SnapshotHashVersion`; `SnapshotHashVersion` changes only when the canonical field set or canonicalization rules change.

### 7.7 Canonical Product Definition source hash

Define `DefinitionSourceHashVersion = 1` as SHA-256 over the exact Product Definition payload used by Refresh preview. Include the source revision identity/status/effective bounds and every route, operation, material, machine, labour, UOM, timing, rate, and dependency field that can affect the resulting Work Order snapshot. Exclude volatile audit fields, database row versions, and unrelated descriptions or metadata that cannot change the snapshot.

Use the same canonical primitives as `SnapshotHash`: explicit null encoding, invariant decimal scales, round-trip timestamps, length-prefixed UTF-8 text, and deterministic child ordering by source key, source ID, sequence, code, and remaining stable business discriminator. Reject duplicate canonical source identities instead of depending on query order. Refresh Preview returns the hash and version; Confirm Refresh recomputes both from a coherently loaded exact revision and fails if the version is unsupported or the hash changed.

## 8. Milestone 3 — Correct Hierarchical Scheduling

### 8.1 Scheduling levels

Schedule in this order:

```text
Work Center StageSequence
  -> ProcessSequence within the Work Center
    -> selected machine option for each machine-based Process
```

At the route and process levels:

- equal sequence means parallel;
- all parallel members use the same anchor;
- the next sequence waits for the slowest preceding member;
- backward scheduling applies the inverse dependency.

Sequence is not the only source of precedence. Build a production dependency graph from each route-step output item through `INTERNAL_ROUTE_WIP` material ownership to the consuming operation and route step.

For forward scheduling:

```text
EarliestStart(member) = max(
    header or user anchor,
    completion of the preceding sequence group,
    completion of every dependency predecessor)
```

Backward scheduling applies the corresponding latest-finish calculation from sequence and dependency successors. Reject circular dependencies, missing or ambiguous producers, a consumer sequenced before its producer, and a producer/consumer pair assigned the same sequence. Equal-sequence rows may run in parallel only when no dependency exists between them.

### 8.2 Operation duration

For machine operations:

```text
Duration = Setup + Conversion + Queue + Run
```

For manual, inspection, wait, packing, and subcontract operations:

```text
NonMachineDurationBasis = FIXED_OPERATION
DurationMinutes = StandardDurationMinutes
```

`FIXED_OPERATION` means one fixed duration for the entire snapshotted operation, independent of Work Order or operation quantity. A quantity-only Draft change therefore leaves a non-machine operation's duration unchanged, although downstream timestamps may still move when another operation's duration changes. Do not infer a duration basis from numeric values. Quantity-dependent, per-batch, and per-output-unit duration bases require a future explicit Product Definition and snapshot contract.

Labour does not independently extend the schedule unless a later approved rule explicitly enables it.

Queue, setup, conversion, and run remain separate calculation components even when the UI displays their sum. The selected machine alternative is the only machine row that contributes duration.

### 8.3 Calendar behavior

Retain support for:

- machine shift calendars;
- shift groups;
- breaks;
- non-working days;
- holidays;
- preventive downtime;
- overnight shifts;
- forward scheduling; and
- backward scheduling.

Add:

- date-time anchors instead of date-only anchors;
- a scheduling calculation trace for support and audit;
- explicit errors for missing calendar coverage;
- later-phase finite-capacity conflict checks against Released and In-Progress Work Orders.

Calendar provenance belongs to the activity whose calendar controls placement:

- for a machine-based operation, store source identity, maximum source last-modified value, hash, and horizon on the selected `PrWorkOrderMachine` row;
- for a non-machine operation, store the plant-default calendar source identity, maximum source last-modified value, hash, and horizon on `PrWorkOrderOperation`; and
- a route step may expose an aggregate display hash, but it is not the authoritative scheduling source.

The deterministic `ScheduleSourceHash` covers the exact configured horizon used by the calculation and includes ordered machine/plant working-day rows, shift-group membership, shift start/end times, every break interval, holidays, non-working days, preventive-downtime windows, and effective-date boundaries. The scheduling trace records which source and horizon governed each scheduled activity. This replaces the existing narrower machine-calendar fingerprint for Work Order integrity purposes.

Use this deterministic horizon-expansion contract:

```text
InitialSchedulingHorizonDays   = 365
SchedulingHorizonExtensionDays = 365
MaxSchedulingHorizonDays       = 1825
OvernightBoundaryBufferDays    = 1
```

For forward scheduling, begin one buffer day before the activity anchor and extend 365 days forward; for backward scheduling, begin 365 days before the anchor and include one buffer day after it. Attempt scheduling, extend only in the scheduling direction by 365 days when coverage/capacity is insufficient, and repeat until success or the five-year maximum is reached. Snapshot the final actual horizon, constants, and direction used on the controlling machine/operation. Return `WO_CALENDAR_COVERAGE_MISSING` when the maximum is reached or a required interval inside the attempted horizon has no valid calendar definition. The hash always covers the final actual horizon, including overnight boundary rows.

### 8.4 Scheduling tests

Add tests for:

1. three sequential centers;
2. two parallel centers followed by one center;
3. parallel processes within one center;
4. sequential process groups;
5. selected alternatives are not scheduled together;
6. discrete cycles where output-per-cycle does not divide quantity;
7. parallel capacity where cycle count does not divide machine count;
8. forward and backward equivalence;
9. mid-shift start;
10. overnight shift;
11. crossing one or more breaks;
12. holiday/off-day skipping;
13. preventive downtime;
14. a manual operation without a machine;
15. missing calendar coverage;
16. a valid later-sequence WIP dependency;
17. a same-sequence WIP dependency conflict;
18. ambiguous WIP producers; and
19. a circular dependency.

## 9. Milestone 4 — Release Readiness and Lifecycle

### 9.1 Add `ValidateForReleaseAsync`

Release must be blocked when:

- no route step exists;
- no operation exists;
- any active route step has a missing or ambiguous final process;
- the final output does not match the Work Order finished good;
- a required operation has neither exactly one selected/default machine option nor an approved non-machine duration;
- a material has no consuming operation;
- an `INTERNAL_ROUTE_WIP` material has no unique producing route step;
- an item, UOM, warehouse, machine, or labour reference is invalid;
- a required UOM conversion is missing;
- an operation or selected-machine quantity has a missing or incompatible UOM basis;
- a process type is unknown or violates its duration/machine rule;
- calendar coverage is incomplete;
- a current `ScheduleSourceHash` differs from the saved machine/operation calendar hash;
- schedule dates are incomplete or invalid;
- a circular production dependency exists;
- sequence order contradicts a production dependency;
- the snapshot hash is invalid; or
- a legacy Draft has not completed an explicit refresh to the current snapshot format.

Return stable error codes and user-friendly messages. Keep warnings separate from release-blocking errors.

Use at least:

```text
WO_NO_ROUTE
WO_NO_OPERATION
WO_FINAL_PROCESS_MISSING
WO_FINAL_PROCESS_AMBIGUOUS
WO_FINAL_OUTPUT_MISMATCH
WO_MACHINE_REQUIRED
WO_OPERATION_DURATION_REQUIRED
WO_MATERIAL_OPERATION_MISSING
WO_WIP_PRODUCER_MISSING
WO_WIP_PRODUCER_AMBIGUOUS
WO_ISSUE_METHOD_INVALID
WO_SUPPLY_SOURCE_INVALID
WO_REFERENCE_INVALID
WO_UOM_CONVERSION_MISSING
WO_OPERATION_UOM_INVALID
WO_LABOUR_RATE_UOM_INVALID
WO_DURATION_BASIS_INVALID
WO_PROCESS_TYPE_INVALID
WO_CALENDAR_COVERAGE_MISSING
WO_SCHEDULE_STALE
WO_SCHEDULING_SOURCE_BUSY
WO_SCHEDULE_INVALID
WO_DEPENDENCY_CYCLE
WO_SEQUENCE_DEPENDENCY_CONFLICT
WO_SNAPSHOT_HASH_INVALID
WO_SNAPSHOT_STALE
WO_DEFINITION_REVISION_NOT_FOUND
WO_DEFINITION_REVISION_AMBIGUOUS
WO_DEFINITION_SOURCE_STALE
WO_LEGACY_SNAPSHOT_REFRESH_REQUIRED
WO_CONCURRENCY_CONFLICT
WO_RELEASE_DISABLED
```

### 9.2 Transactional Release concurrency

Replace the current Release signature with a request contract containing the frozen concurrency tokens:

```text
WorkOrderNo
WorkOrderRowVersion
SourceProductDefinitionRevisionID
SnapshotRevisionNo
SnapshotHash
ScheduleSourceHash (per selected machine and non-machine scheduled operation)
```

Preview and confirm/Release commands carry explicit optimistic concurrency tokens. The server never trusts browser-only values; it reloads and revalidates authoritative values inside the transaction before mutation.

| Command | Client must send | Server revalidates in transaction |
|---|---|---|
| Confirm Refresh | `WorkOrderRowVersion`, `SourceProductDefinitionRevisionID`, `DefinitionSourceHashVersion`, `DefinitionSourceHash` (from preview) | Reload aggregate; compare tokens; re-resolve/compare Product Definition revision; apply or fail |
| Recalculate Schedule | `WorkOrderRowVersion`, `SnapshotRevisionNo`, `SnapshotHash` | Reload; recompute schedule under lock; fail on conflict |
| Release | `WorkOrderRowVersion`, `SourceProductDefinitionRevisionID`, `SnapshotRevisionNo`, `SnapshotHash`, `ScheduleSourceHash` | Reload; rehash; readiness; recompute current scheduling-source hashes over saved horizons |

Preview → confirm Refresh flow:

```text
Refresh Preview
  -> returns WorkOrderRowVersion, SourceProductDefinitionRevisionID,
     DefinitionSourceHashVersion, DefinitionSourceHash,
     ScheduleSourceHash as applicable
Confirm Refresh
  -> server verifies all tokens still match inside one transaction
  -> apply or fail with WO_DEFINITION_SOURCE_STALE / WO_SNAPSHOT_STALE /
     WO_CONCURRENCY_CONFLICT / revision codes
```

**Single-transaction Release boundary.** Final validation and the status transition occur inside one transaction, with no gap between "validator passed" and "status changed":

```text
Begin transaction
  1. lock/read Work Order
  2. verify WorkOrderRowVersion / status / expected SnapshotRevisionNo / SnapshotHash
  3. verify snapshot readiness
  4. verify persisted source-revision identity (never re-pick a revision)
  5. verify schedule/calendar freshness (ScheduleSourceHash)
  6. verify WIP graph
  7. transition DRAFT -> RELEASED
  8. persist audit event
Commit
```

If a current calendar hash differs, return `WO_SCHEDULE_STALE` and require `RecalculateDraftScheduleAsync`; otherwise transition `DRAFT -> RELEASED`, write the audit event, and commit. Any saved snapshot change after preview returns `WO_SNAPSHOT_STALE` and requires reload and re-preview. Under `DefinitionValidityAtReleasePolicy = FROZEN_SNAPSHOT_ALLOWED`, a superseded source revision does not by itself block Release; Release still never re-picks a revision. A Product Definition change alone does not stale an unchanged saved Work Order snapshot. After Release, calendar provenance and planned timestamps remain frozen until a future controlled change-order policy is implemented.

Deterministic outcomes:

| Event | Outcome |
|---|---|
| Product Definition changes during refresh preview | `WO_DEFINITION_SOURCE_STALE`; require re-preview; do not mutate |
| Calendar changes during recalculation | Fail or recompute under lock; never commit a partial schedule |
| Another user edits the Draft during Release | `WO_CONCURRENCY_CONFLICT` or `WO_SNAPSHOT_STALE`; require reload |
| Another process Releases the same Work Order | Concurrency conflict; the second Release fails |
| Source definition revision becomes superseded between load and commit | Release the frozen snapshot; never silently pick another revision |
| Calendar hash changed since save | `WO_SCHEDULE_STALE`; require `RecalculateDraftScheduleAsync` |

Prefer fail-with-stable-code over silent retry unless retry is idempotent and documented.

Schedule recalculation, Release, and every plant calendar, machine calendar, shift, shift-group, holiday, and preventive-downtime write use a transaction-owned SQL application lock with resource key `PR_SCHEDULE|{NormalizedCompanyCode}`. Recalculate and Release acquire `Shared`; scheduling-source writes acquire `Exclusive`. A company-wide key is intentional because a shift or shift-group edit can affect multiple machines and years. The timeout is 15 seconds; timeout or cancellation returns `WO_SCHEDULING_SOURCE_BUSY` without mutation. Acquire this scheduling lock before Work Order row locks and before reading scheduling sources; if a future command spans companies, acquire normalized company keys in ordinal order. This prevents source changes between schedule-hash comparison and commit without serializing concurrent Release/recalculation reads within one company.

The page's `IsDirty` and `PreviewIsStale` flags are UI-only guards used to disable Release and warn the user. They are not server readiness rules and are never trusted by `ReleaseAsync`.

No `WO_CALCULATION_STALE` state is required because planning-input changes use atomic recalculation and cannot commit stale derived values.

### 9.3 Lifecycle

Target lifecycle:

```text
DRAFT -> RELEASED -> IN_PROGRESS -> COMPLETED -> CLOSED
DRAFT -> CANCELLED
```

For the snapshot milestone, implement only:

```text
DRAFT
RELEASED
CANCELLED
```

Do not activate `IN_PROGRESS`, `COMPLETED`, or `CLOSED` transitions until execution posting exists.

### 9.4 Fail-closed Release feature toggle

The new Release path is guarded by a feature toggle or config switch. The toggle is a safety switch, not a path back to unsafe logic. When disabled it must **fail closed**:

- for a version-2 Draft: the Release command is unavailable/blocked and the Draft remains intact (`WO_RELEASE_DISABLED`);
- the old Phase-1 permissive Release path must **not** be silently invoked, because that would bypass every new readiness check;
- for legacy version-1 Work Orders: only the explicitly documented compatibility policy applies.

## 10. Milestone 5 — Professional Work Order UI

Use `PrProductDefEntry` as the main design reference. Reuse the existing `iv-*` visual system, DevExpress components, responsive behavior, validation presentation, contextual selection, and persistent footer.

### 10.1 Header

Show:

```text
Work Order number
Status
Product and description
Planned quantity and UOM
BOM revision
Snapshot revision
Snapshot format / legacy indicator
Definition effective date
Source type and reference
Scheduling direction
Start date and time
Completion date and time
Remark
```

Commands:

```text
Back
Preview
Save Draft
Recalculate Schedule
Refresh from Definition
Release
Cancel Draft
```

Release must be disabled whenever the page is dirty, the preview is stale, readiness has blocking errors, or the Release feature is disabled.

`DefinitionEffectiveDate` is selected during creation and shown thereafter. Changing it starts the definition-refresh preview; a normal header save does not resolve a different revision. Copied structural tabs are read-only in this milestone.

### 10.2 Workflow tabs

Use:

```text
1 - Overview
2 - Route
3 - Operations
4 - Materials
5 - Machines
6 - Labour
7 - Audit
```

### 10.3 Route tab

Display:

```text
Stage sequence
Work Center
Output/WIP item
Planned quantity
Start
Completion
Duration
Readiness/status
```

Visually group equal stage sequences as parallel. Selecting a Work Center filters Operations.

### 10.4 Operations tab

Display:

```text
Process sequence
Process
Process type
Non-machine duration basis
Final process
Planned input and UOM
Planned output and UOM
Start
Completion
Duration
Setup loss
Operation loss
```

Selecting an operation filters Materials, Machines, and Labour.

### 10.5 Materials tab

Display:

```text
Sequence
Component
Description
Consuming process
Standard quantity
Standard UOM
Required quantity and UOM
Required base quantity and base UOM
Scrap percent
Tolerance percent
Issue method
Supply source
Warehouse
```

Do not show `Reserved`, `Picked`, `Issued`, `Returned`, `Consumed`, `Variance`, or `Open requirement` in this milestone. The schema may retain those execution projections for compatibility, but their equations and UI remain inactive until the execution-posting contract is approved. Do not calculate a material-progress percentage or fulfillment summary from inactive execution values.

### 10.6 Machines tab

Display:

```text
Priority
Machine
Default indicator
Selected indicator
Parallel count
Cycle mode
Cycle seconds
Output per cycle
Output-per-cycle UOM
Required machine output and UOM
Planned cycle count
Planned cycle slots
Planned run minutes
Setup
Conversion
Queue
Start
Completion
```

### 10.7 Labour tab

Display:

```text
Process
Machine
Labour code
Description
Planned units
Planned minutes
Rate basis
Rate UOM
Labour basis quantity
Contributes to plan
Planned amount
```

Show nullable labour units/minutes as not supplied rather than zero. Clearly mark labour copied from unselected alternatives as non-contributing.

### 10.8 Footer and dialogs

Add:

- persistent `Unsaved changes`, `Ready`, or `Release blocked` state;
- release-readiness panel;
- definition-refresh comparison dialog;
- concurrency reload dialog;
- unsaved-navigation guard;
- stale-preview indicator;
- responsive compact route cards; and
- light/dark theme verification.

Remove or clearly label placeholder KPIs until their underlying posting logic exists.

### 10.9 Schema-to-UI contract

Keep a schema-to-UI mapping beside the UI specification and update it whenever a displayed field changes:

| UI field | Entity | Column or calculation | Source |
|---|---|---|---|
| Definition effective date | Work Order | `DefinitionEffectiveDate` | saved snapshot input |
| Stage duration | Route step | completion minus start | calculated schedule |
| Process duration | Operation | completion minus start | calculated schedule |
| Process input/output | Operation | planned quantity + explicit UOM columns | calculated snapshot |
| Non-machine duration basis | Operation | `NonMachineDurationBasis = FIXED_OPERATION` | snapshot |
| Scrap % | Material | `ScrapPercent` | snapshot |
| Tolerance % | Material | `TolerancePercent` | snapshot |
| Warehouse | Material | warehouse code/ID | snapshot |
| Required quantity | Material | `RequiredQty` + `RequiredUOM` | calculated snapshot |
| Required base quantity | Material | `RequiredBaseQty` + `BaseUOM` | calculated snapshot |
| Open requirement | Material | deferred and hidden | execution-posting milestone |
| Selected machine | Machine | `IsSelected` | snapshot |
| Machine output basis | Machine | required output/UOM, output per cycle/UOM, cycle count/slots | calculated snapshot |
| Machine duration | Machine | queue + setup + conversion + run | calculated schedule |
| Calendar provenance | Selected machine or non-machine operation | source type/ID, horizon, last-modified, hash | calculated schedule |
| Labour rate basis | Labour | `RateBasis` | snapshot; `PER_OUTPUT_UNIT` in this milestone |
| Labour rate UOM/basis quantity | Labour | `RateUOM`, `LabourBasisQty`, snapshotted conversion | calculated snapshot |
| Planned amount | Labour | rate × normalized `LabourBasisQty` in `RateUOM` | calculated snapshot |

The UI must not invent fields or derive UOM, rate-basis, selected-machine, or execution semantics from unrelated values. Execution-derived material fields remain hidden until Milestone 6 defines their equations.

## 11. Milestone 6 — Execution Gate

Do not begin execution posting until the following contracts have approved examples and automated tests:

1. reservation and allocation priority;
2. shortage and negative-stock policy;
3. issue, return, and actual-consumption equations;
4. operation input, good, scrap, reject, hold, rework, and transfer equations;
5. WIP lot and quantity model;
6. Finished Goods receipt and reversal;
7. material, machine, labour, overhead, subcontract, WIP, and FG costing;
8. posting idempotency key;
9. atomic transaction boundary and lock order;
10. Change Order policy;
11. completion and closure predicates; and
12. future accounting/GL derivation contract.

After approval, implement in this order:

```text
Reservation/allocation
  -> Material issue/return
  -> Operation execution
  -> WIP movement
  -> Finished Goods receipt
  -> Costing and variance
  -> Completion and closure
  -> Reversal and Change Orders
```

## 12. Automated Verification

### 12.1 Unit tests

- base quantity other than one;
- fractional BOM quantities;
- scrap and tolerance;
- BOM, required, and inventory-base UOM conversion, including fractional and round-trip cases;
- missing UOM conversion blocks Release;
- operation input/output UOM conversion and mismatch rejection, including route output `BOX`, operation output `PCS`, and material denominator `PCS`;
- sequential and parallel operations each receive the full normalized owning-route quantity; parallel timing never splits quantity;
- `RequiredMachineOutputQty` maps to operation planned output in the selected machine's output-per-cycle UOM;
- labour basis quantity converts operation output to `RateUOM` and snapshots the conversion;
- `FIXED_OPERATION` duration remains unchanged when quantity changes;
- discrete cycles for quantities `10 / 3`, `1 / 5`, `10 / 3 / 2 parallel`, and `10 / 3 / 3 parallel`;
- output-per-cycle greater than required quantity;
- parallel-machine count greater than required cycles;
- setup and operation losses remain informational and do not gross-up requirements;
- only the selected machine option contributes duration; unselected alternatives contribute no labour amount;
- parallel sequence grouping;
- valid WIP/output linkage, ambiguous producer rejection, sequence conflict, and dependency-cycle rejection;
- canonical snapshot hash ordering, null, decimal, and timestamp stability;
- canonical `DefinitionSourceHashVersion = 1` ordering, null/decimal/timestamp stability, and irrelevant-audit exclusion;
- canonical ordering remains deterministic with missing source keys, and duplicate canonical identities are rejected;
- readiness validation;
- refresh eligibility; and
- lifecycle rules.

### 12.2 Service tests

- snapshot contains all hierarchy levels;
- Product Definition changes do not mutate a saved Work Order;
- ordinary header save does not refresh the snapshot;
- explicit refresh increments `SnapshotRevisionNo`;
- child-only schedule recalculation and explicit refresh each touch the header so `WorkOrderRowVersion` advances exactly once;
- failed or rolled-back child mutation advances neither `WorkOrderRowVersion` nor `SnapshotRevisionNo`;
- a client holding the pre-recalculation or pre-refresh header row version is rejected afterward;
- `SnapshotRevisionNo` does not move on a rejected or failed command;
- failed refresh leaves the old snapshot intact;
- Released Work Order cannot refresh;
- repeated save does not duplicate children;
- concurrent save/refresh is rejected safely;
- create and refresh resolve the exact revision applicable to `DefinitionEffectiveDate`;
- a future revision is ignored before its effective date and selected on the exact boundary;
- changing planned start alone does not change `DefinitionEffectiveDate` or the saved revision;
- changing quantity from 100 to 200 atomically recalculates route/operation quantities, materials, selected-machine cycles, contributing labour amount, affected timestamps, revision, and hash;
- changing only remark or source reference changes no production calculation and rebuilds the hash once;
- Machine A selected with Labour A and Machine B unselected with Labour B snapshots both labour rows but only Labour A contributes amount;
- two active Product Definition revisions matching the effective date fail with `WO_DEFINITION_REVISION_AMBIGUOUS`;
- zero matching active revisions fail with `WO_DEFINITION_REVISION_NOT_FOUND`;
- revision supersession: create a Work Order from Revision 5, activate Revision 6, attempt Release; the result follows `DefinitionValidityAtReleasePolicy` (`FROZEN_SNAPSHOT_ALLOWED` releases the frozen snapshot);
- two selected machines using different calendars retain independent scheduling-source provenance;
- forward/backward scheduling expands the horizon by the frozen increments, snapshots the final horizon, handles year boundaries, and fails at the maximum with `WO_CALENDAR_COVERAGE_MISSING`;
- a Draft whose selected-machine or plant-default calendar hash changes fails Release with `WO_SCHEDULE_STALE`, then succeeds after schedule recalculation;
- Client A preview followed by Client B Draft modification causes Client A Release to return `WO_SNAPSHOT_STALE`;
- a Refresh preview followed by a Product Definition change causes Confirm Refresh to return `WO_DEFINITION_SOURCE_STALE` with no partial mutation;
- changing only the Product Definition does not stale an unchanged saved Work Order;
- all release expectations and readiness are revalidated in the Release transaction;
- a legacy Draft cannot Release before explicit refresh;
- a legacy `Released` / `In Progress` Work Order remains operationally usable under the frozen execution policy and its snapshot is never rebuilt;
- missing routing blocks Release;
- transaction failure leaves no partial aggregate;
- with the Release feature disabled, Release is blocked safely and no permissive fallback path runs; and
- tenant and branch boundaries remain enforced.

### 12.3 SQL Server tests

Add a Work Order SQL Server fixture covering:

- installation and upgrade scripts;
- installation on an empty database and idempotent upgrade from the Phase-1 schema;
- database check constraints and the intended single cascade path;
- the exclusive-owner Labour check and proof that only one structural cascade path exists from Operation to Labour;
- labour delete-path tests: delete an Operation that owns (a) direct operation-level Labour only, (b) Machine plus machine-owned Labour only, and (c) both ownership types on different rows; expect all owned rows deleted, no orphans, and successful schema install;
- the NULL-safe `SupplySource` / `ProducingRouteStepID` check accepts and rejects the correct rows, including nullable and legacy cases;
- filtered source-key uniqueness at route, operation, material, machine, and labour levels;
- filtered at-most-one selected/default machine indexes;
- aggregate row-version concurrency, including child-only recalculation/refresh and rollback;
- concurrent number allocation;
- parallel sequence persistence (equal sequences allowed for parallel steps);
- atomic snapshot replacement;
- Release versus Draft update concurrency;
- scheduling-source lock order prevents a calendar or preventive write from racing Release after hash comparison;
- company-scoped Shared/Exclusive scheduling locks coexist for concurrent reads, block writes correctly, and return `WO_SCHEDULING_SOURCE_BUSY` after the 15-second timeout;
- legacy marking without Product Definition reconstruction;
- legacy status counts and the in-flight cutover policy; and
- rollback after child insertion failure.

### 12.4 UI verification

- dirty page cannot release;
- selected Work Center filters processes;
- selected process filters materials and resources;
- parallel stages are visually understandable;
- refresh diff requires confirmation;
- structural snapshot tabs remain read-only;
- definition effective date changes enter the refresh workflow rather than normal save;
- Released Work Order is read-only;
- navigation guard works;
- validation appears at field and summary levels;
- desktop and compact layouts work; and
- light and dark themes remain legible.

### 12.5 Planning consistency contract

1. A persisted `SnapshotFormatVersion = 2` Draft is always internally self-consistent.
2. Changing `PlannedQty` atomically recalculates route/operation quantities, material requirements, selected-machine cycles and durations, contributing labour amounts, dependent schedule timestamps, calendar provenance, `SnapshotHash`, and `SnapshotRevisionNo`.
3. Changing `ScheduleAnchorDateTime` or `SchedulingDirection` atomically recalculates route/operation/machine timestamps, calendar provenance, scheduling trace, `SnapshotHash`, and `SnapshotRevisionNo`.
4. Changing only remark or source reference does not recalculate production values, but rebuilds the hash and increments `SnapshotRevisionNo` because those fields are canonical inputs.
5. Changing the current Product Definition does not mutate or stale a saved Work Order.
6. Changing an applicable machine or plant calendar while the Work Order is Draft makes the schedule stale and blocks Release until `RecalculateDraftScheduleAsync` succeeds.
7. Release never trusts UI preview state; it validates the persisted aggregate, expected concurrency values, readiness, the persisted source-revision identity, and current scheduling-source hashes in one transaction.
8. Every committed aggregate mutation updates the `PrWorkOrder` header exactly once so `WorkOrderRowVersion` advances even when only children changed; failed commands do not advance it.

## 13. Exact Implementation Order

Execute in this sequence:

Milestone 0 — freeze the domain contracts:

1. Complete the Snapshot Source Matrix (5.2) and enumerate unsupported Product Definition combinations.
2. Freeze the labour authorship, internal-WIP producer, and UOM conversion contracts (5.3–5.5).
3. Freeze Product Definition overlap policy and `DefinitionValidityAtReleasePolicy` (4.3).
4. Freeze the identity, hashing, and concurrency vocabulary (4.2).
5. Freeze source ID/key identity and filtered uniqueness at every snapshot level.
6. Freeze stable persisted-value constants and process-type behavior.
7. Freeze quantity/UOM, rounding, scrap, tolerance, informational-loss, and machine-output-basis rules.
8. Freeze discrete cycle, parallel-capacity, machine-alternative, and selected-option semantics.
9. Freeze route/process/WIP dependency and final-process rules.
10. Freeze calendar ownership, `ScheduleSourceHash`, and stale-calendar Release behavior.
11. Freeze atomic Draft recalculation, read-only structure, and refresh behavior.
12. Freeze snapshot-hash canonicalization and duplicate canonical-identity rejection.
13. Freeze the Phase-1 legacy migration, legacy status contract, and Release policy.
14. Draft the foreign-key delete matrix.

Milestone 0B — Product Definition authoring readiness:

15. Implement persisted route-step authoring in `PrProductDefService`, its DTOs, and Product Definition Entry.
16. Expose and validate `ProcessType` through Product Definition DTO/UI/save/clone/revision paths.
17. Expose and validate `IssueMethod` and `SupplySource` through BOM DTO/UI/save/clone/revision paths.
18. Add and author `ProducingRouteStepID` for `INTERNAL_ROUTE_WIP`, including same-definition producer validation.
19. Enable multiple eligible machine alternatives, stable priority, and exactly one default machine per machine-based process.
20. Expose `OutputPerCycle`, its owning operation output-UOM contract, timing standards, and copied `MachineRatePerHour` source data.
21. Author/derive operation input/output UOMs and expose the frozen `FIXED_OPERATION` non-machine duration basis.
22. Implement the Core UOM conversion service over `IvItemUomConversion`.
23. Finalize labour source authorship, including operation/machine ownership and explicit `RateUOM` for `PER_OUTPUT_UNIT`.
24. Add Product Definition activation/readiness validation and stable error mappings for every required Snapshot Source Matrix field.
25. Add Product Definition service/UI/schema tests for route steps, alternatives, process/material semantics, UOMs, labour, overlap, and activation blocking.
26. Complete and approve the Section 18 gate; only then begin Work Order schema Milestone 1.

Milestone 1 — schema and migration:

27. Add `PrWorkOrderRouteStep`, `PrWorkOrderMachine`, and `PrWorkOrderLabour` (Option A1).
28. Expand Work Order header, operation quantity/UOM/duration/calendar, material, machine, and labour-rate-UOM snapshot fields.
29. Remove the unique Work Order operation-sequence restriction.
30. Standardize all planned schedule columns to `datetime2`.
31. Add constraints, filtered selected/default/source-key indexes, foreign keys, and the single cascade ownership graph.
32. Update `AppDbContext` and EF configurations.
33. Implement the idempotent SQL installation/upgrade, legacy markers, and the NULL-safe producer check.

Milestone 2–4 — services:

34. Implement Product Definition overlap validation and the cardinality-safe `ProductDefinitionSnapshotLoader`.
35. Implement operation quantity propagation, labour-basis conversion, `WorkOrderQuantityCalculator`, and UOM normalization.
36. Implement the dependency-aware `WorkOrderScheduleCalculator` with fixed non-machine duration, deterministic horizon expansion, and machine/operation calendar provenance.
37. Implement the company-scoped scheduling-source application-lock contract.
38. Implement the full `ScheduleSourceHash` hasher and calendar-staleness comparison.
39. Implement canonical `DefinitionSourceHash` version 1.
40. Implement the canonical snapshot hasher and `WorkOrderSnapshotBuilder`.
41. Split Create, atomic Header Save, Schedule Recalculate, Refresh Preview, and Refresh commands; every aggregate mutation touches the header once.
42. Implement refresh diff, transaction, revision increment, header row-version advancement, and audit.
43. Implement `WorkOrderReadinessValidator` and stable error codes.
44. Implement transactional Release with expected row version/revision/hash, single-transaction boundary, and current calendar hashes.
45. Implement the fail-closed Release feature toggle.

Milestone 5 — UI and verification:

46. Update DTOs and detail/query mappings.
47. Rebuild Work Order Entry using the Product Definition workflow pattern and schema-to-UI contract.
48. Keep execution-derived material projections hidden behind the execution gate.
49. Add unit, service, scheduler, SQL Server, and UI tests.
50. Run installation and upgrade against a scratch SQL Server database.
51. Perform all representative user-acceptance scenarios.
52. Enable Release for production use only after acceptance passes.
53. Begin the separate execution/posting milestone.

## 14. User Acceptance Scenarios

Use at least these eleven representative Product Definitions:

### Scenario A — Simple product

```text
One Work Center
One Process
One Machine
One Labour
Purchased raw materials
```

Verify quantity, duration, calendar placement, snapshot, and release.

### Scenario B — Sequential production

```text
WC1 -> WIP1
WC2 consumes WIP1 -> WIP2
WC3 consumes WIP2 -> Finished Good
```

Verify WIP/output relationships and that every stage waits for its predecessor.

### Scenario C — Parallel production

```text
WC1 sequence 1 -> WIP1
WC2 sequence 1 -> WIP2
WC3 sequence 2 consumes WIP1 and WIP2 -> Finished Good
```

Verify WC1 and WC2 share the same anchor and WC3 starts only after the slower parallel stage completes.

### Scenario D — Pack size and fractional BOM

```text
Finished good definition base = 5 FG
Material standard = 1 PCS per 5 FG
Work Order quantity = 13 FG
```

Expected material requirement: `13 * 1 / 5 = 2.6 PCS` (no scrap), stored to four decimals as `2.6000`. Verify the material requirement, required UOM, inventory-base quantity, conversion, and final rounding follow the frozen rules.

### Scenario E — Non-divisible machine cycles

```text
Work Order quantity = 10
OutputPerCycle = 3
CycleSeconds = 60
ParallelMachineCount = 2
```

Verify `CycleCount = 4`, `CycleSlots = 2`, and `RunMinutes = 2`.

### Scenario F — Dependency conflicts with sequence

```text
WC1 sequence 1 -> WIP1
WC2 sequence 1 consumes WIP1
```

Verify Release fails with `WO_SEQUENCE_DEPENDENCY_CONFLICT` instead of scheduling the dependent stages in parallel.

### Scenario G — Multiple processes in one Work Center

```text
WC1
  Process 10 - Cutting (machine)
  Process 20 - Drilling (machine)
  Process 30 - Inspection (fixed-operation duration, final)
```

Verify every process receives the full normalized route quantity, sequencing is correct, machine/manual calendar placement composes correctly, materials remain owned by their consuming process, and exactly one final process exists.

### Scenario H — Parallel processes in one Work Center

```text
WC1
  Process A sequence 10
  Process B sequence 10
  Process C sequence 20 (final)
```

Verify A and B each receive the full route quantity, share the scheduling anchor, and C starts only after the slower parallel predecessor completes.

### Scenario I — Machine alternatives

```text
Process P1
  Machine A priority 1 default
    Labour LA
  Machine B priority 2 alternative
    Labour LB
```

Verify both machines and labour sets are snapshotted, `IsSelected = IsDefault` for exactly one row, only Machine A controls duration/calendar, Labour LB remains traceable with zero planned amount, and zero/multiple selected rows block Release.

### Scenario J — Cross-UOM planning

```text
Route output = BOX
Operation input/output = PCS
Selected-machine OutputPerCycle = PCS
Material BOM denominator = BOX or PCS
Inventory base = PCS
Labour rate UOM = PCS
```

Verify operation propagation, material requirement, machine output basis, labour basis, inventory-base quantity, every conversion factor, and missing-conversion failures.

### Scenario K — Fixed non-machine duration

```text
Manual operation StandardDurationMinutes = 30
NonMachineDurationBasis = FIXED_OPERATION
Change Work Order quantity from 100 to 200
```

Verify the manual operation remains 30 minutes, its quantities and labour amount recalculate, dependent timestamps remain internally consistent, and the snapshot revision/hash/row version advance once.

For every scenario, compare:

- expected material quantities;
- expected Work Center quantities;
- expected machine minutes;
- expected start and completion timestamps;
- expected BOM and routing revision;
- saved/reloaded snapshot equality; and
- behavior after changing the Product Definition.

## 15. Definition of Done for the Enhanced Foundation

The Work Order foundation is complete when:

- the Snapshot Source Matrix classifies every snapshot field as authored, derived, defaulted, or blocking;
- no snapshot field is silently guessed, and every unsupported Product Definition combination is Release-blocking with a stable error code;
- the full Product Definition hierarchy is copied into an independent snapshot;
- Work Center, Process, material, machine, and labour relationships are preserved;
- every formula input required to reproduce quantity, duration, schedule, and planned labour amount is stored in the snapshot;
- every quantity has an explicit UOM basis and inventory-base equivalent where applicable;
- operation planned input/output quantities have explicit UOMs, and `RequiredMachineOutputQty` maps to the selected machine's normalized operation planned output;
- every active operation receives the full normalized owning-route quantity; parallel timing does not imply quantity splitting;
- non-machine duration is explicitly `FIXED_OPERATION` and does not scale with quantity;
- every contributing `PER_OUTPUT_UNIT` labour row has an explicit `RateUOM`, basis quantity, and snapshotted conversion;
- non-divisible machine cycles use the approved discrete-cycle rule;
- alternative machines cannot be scheduled as multiple required machine steps;
- equal sequence numbers schedule in parallel only when no dependency exists;
- WIP/output dependencies remain traceable and cannot contradict sequence order;
- every active route step has exactly one final process and the terminal output matches the finished good;
- quantity formulas match approved examples;
- setup and operation losses remain informational and do not silently change requirements;
- ordinary Draft save never silently refreshes from Product Definition;
- a persisted version-2 Draft can never contain current planning inputs with stale derived quantities, labour amounts, schedule values, or hash;
- explicit Draft refresh provides a diff, reason, revision, and audit event;
- Released Work Orders cannot be refreshed or edited directly;
- schedule timestamps retain time-of-day;
- every selected machine and non-machine scheduled operation records the actual calendar source, horizon, and complete `ScheduleSourceHash` that governed placement;
- a Draft whose applicable calendar, shift, break, holiday, non-working day, or preventive downtime changed cannot Release until schedule recalculation succeeds;
- Product Definition resolution uses the explicit effective date and one coherent revision;
- zero or overlapping effective Product Definition revisions fail deterministically and overlap is rejected at activation;
- the persisted source-revision identity is used at Release, which never silently re-picks a revision;
- snapshot hashes use the documented canonical representation;
- refresh preview/confirm uses canonical, versioned `DefinitionSourceHash` rules equivalent in rigor to `SnapshotHash`;
- duplicate canonical child identities are rejected for version-2 snapshots;
- deterministic scheduling-horizon expansion records the final actual horizon and fails at the frozen maximum;
- company-scoped scheduling locks have frozen key, mode, acquisition order, and timeout behavior;
- SQL enforces at most one selected and at most one default machine per operation;
- Release revalidates row version, snapshot revision, snapshot hash, readiness, source-revision identity, and scope in one transaction;
- every committed aggregate mutation advances the header `WorkOrderRowVersion` exactly once, including child-only changes;
- the Release feature toggle fails closed and never restores the permissive Phase-1 path;
- old Phase-1 records are never reconstructed silently from today's Product Definition;
- legacy Draft, in-flight, and finished statuses follow the frozen legacy status contract, and no live Work Order becomes unusable after deployment;
- copied Draft structure remains read-only in this milestone;
- labour belonging to an unselected machine alternative remains traceable but contributes no planned amount, and the Labour table has exactly one structural cascade path;
- `ProcessType`, `SupplySource`, `IssueMethod`, `CycleQuantityMode`, `NonMachineDurationBasis`, `LabourRateBasis`, `SchedulingDirection`, and `CalendarSourceType` use centralized stable persisted-value contracts;
- manual and machine operations both receive valid schedules;
- invalid or incomplete routing cannot be released;
- UI exposes the complete production structure clearly;
- execution-derived values such as Open Requirement remain hidden until the execution contract is approved;
- SQL Server concurrency, labour delete-path, and rollback tests pass;
- all eleven acceptance scenarios reconcile; and
- material/WIP/FG posting remains disabled until the execution gate is approved.

### 15.1 Minimum production-safe Release slice

The code-complete slice (schema, loader/builder, quantity and schedule calculation, command split, readiness, and UI for Scenarios A–K) is necessary but not sufficient to enable production Release. Release is enabled only when the production-safe slice is also complete:

- migration compatibility and legacy status handling;
- Product Definition Milestone 0B authoring/readiness and activation validation;
- snapshot correctness (source matrix, no silent defaults);
- quantity propagation, operation/labour UOM, and fixed-duration correctness;
- schedule calculation, deterministic horizon, calendar provenance, and company-scoped scheduling locks;
- Release readiness and single-transaction concurrency protection;
- aggregate header row-version advancement for every committed mutation;
- stable validation error codes;
- UI sufficient to inspect the hierarchy and to Refresh / Release;
- SQL Server integration tests (including labour delete paths and the cascade graph);
- fail-closed feature toggle and rollback strategy.

## 16. Migration and Deployment

### 16.1 Pre-migration tenant data audit

Before changing revision policy, foreign keys, or generic-resource storage, run an audit. Conceptual checks:

1. Product Definition revisions with overlapping effective dates.
2. Active revisions that would be ambiguous under the strict resolver.
3. Existing Work Orders containing only generic resources.
4. Work Orders with no operations, no materials, or no routing.
5. BOM rows that cannot map to a consuming process.
6. Materials whose internal-WIP producer cannot be resolved.
7. Sequence patterns that violate the frozen parallel/serial contract. Equal `SequenceNo`, stage, or process sequences are **valid** when parallel execution is supported and no dependency edge forces serial order; flag only duplicates invalid within a scope where parallel is not allowed, or that conflict with dependency edges.
8. Invalid or missing UOM conversions.

Disposition per condition: auto-migrated | preserved as legacy | blocked for manual correction | historical-read only.

### 16.2 Deployment and rollback sequencing

```text
1. Deploy Product Definition Milestone 0B additive schema/service/UI
2. Complete Product Definition readiness data correction and activation tests
3. Deploy additive Work Order schema
4. Deploy dual-compatible read code
5. Run the Work Order data audit / migration
6. Enable v2 snapshot creation
7. Enable Release validation
8. Stop old generic-resource writes
9. Observe production
10. Later remove obsolete compatibility paths
```

The Release toggle (9.4) is the rollback control: disabling it blocks version-2 Release and leaves Drafts intact; it never restores the permissive path. Legacy Work Orders follow the compatibility policy only.

### 16.3 Generic-resource cutover

```text
new snapshots write Machine/Labour only
  -> compatibility reads may temporarily support old resources
  -> migration verified
  -> stop old resource writes
  -> retire PrWorkOrderResource
```

Dual-write is never enabled indefinitely.

## 17. Traceability Matrix

| Finding | Plan section | Implementation area | Required test |
|---|---|---|---|
| Product Definition route steps not authored | 5.1, 5.2 | `PrProductDefService`, Work Order loader | Route-step snapshot integration |
| Product Definition readiness tasks missing from sequence | 5.6, 13 Milestone 0B | Product Definition service/UI/schema | Milestone 0B readiness suite |
| Machine alternatives blocked | 5.1, 5.2, 6.4 | Product Definition validation + machine snapshot | Multi-option vs single-option cases |
| `ProcessType` / `IssueMethod` / `SupplySource` incomplete | 5.2, 6.9 | Product Definition UI/API or command-impact block | Build/Release impact codes |
| Overlap-policy conflict | 4.3, 7.2 | Product Definition activation + Work Order resolver | 0 / 1 / >1 effective revisions |
| Stored revision identity / validity at Release | 4.3, 6.6, 7.2, 9.2 | Header snapshot + Release | Save does not re-resolve; supersession Release test |
| Producer-route second FK | 6.3, 6.6 | Material configuration | SQL Server `ON DELETE NO ACTION` fixture |
| Labour exclusive ownership + cascade | 6.4, 6.6 | Labour EF + check | Cascade install + exclusive-owner + operation-delete orphan test |
| WIP graph incomplete | 5.4, 8.1, 9.1 | Schedule calculator + readiness | Cycle, ambiguous, sequence conflict |
| Parallel sequence audit false positive | 16.1 | Audit scripts | Equal sequence allowed when parallel and no dependency |
| Labour authorship mismatch | 4.1, 5.3, 6.4 | Snapshot builder | Selected vs unselected labour amount |
| UOM service absent | 5.5, 7.3 | UOM conversion service + calculator | Base/alt UOM + missing conversion |
| Header inputs could leave stale calculations | 7.4, 12.5 | atomic Draft update transaction | Qty/anchor/direction recalc + remark-only save |
| Calendar provenance at wrong level | 6.2, 6.4, 8.3 | operation/machine snapshot + schedule hasher | Multi-machine calendars + non-machine plant calendar |
| Operation quantity UOM ambiguous | 6.2, 7.3 | operation snapshot + UOM calculator | BOX route / PCS operation and denominator |
| Operation quantity propagation undefined | 7.3 | quantity calculator | Sequential/parallel full-route-quantity propagation |
| Machine output basis undefined | 6.4, 7.3 | machine snapshot + quantity calculator | Required output maps to normalized operation output |
| Non-machine duration quantity basis undefined | 6.2, 6.9, 8.2 | Product Definition + scheduler | Quantity change retains fixed duration |
| Labour rate UOM undefined | 5.3, 6.4, 7.3 | Product Definition + labour calculator | Output-to-rate-UOM conversion |
| Machine planned cost scope unclear | 5.2, 7.3, 11 | snapshot builder | Rate copied; no planned machine amount |
| Execution projection leaked into planning UI | 10.5, 10.9, 11 | Work Order UI | Open Requirement and execution values remain hidden |
| Generic resource retirement | 6.4, 16.3 | Snapshot read/write | v1-read / v2-write only |
| Nullable compatibility permanence | 6.6, 6.7 | Schema + Release readiness | v2 requires consuming operation |
| Legacy Draft vs in-flight vs finished | 6.7, 16.1 | SQL upgrade + Release/Refresh | Draft needs refresh; active remains usable; finished never rebuilds |
| Hash / token vocabulary | 4.2, 6.6, 7.6 | Header + command contracts | Token-specific stale/refresh failures |
| Definition source hash canonicalization | 4.2, 7.7, 9.2 | Refresh preview/confirm | Stable ordering + content-change failure |
| Concurrency tokens | 7.5, 9.2 | Refresh confirm + Release | Stale preview / concurrent edit fail |
| Child-only changes may not advance header rowversion | 6.6, 7.4, 12.5 | aggregate command coordinator | Recalculate/refresh advance once; rollback does not |
| Release transaction gap | 9.2 | `ReleaseAsync` | Single-transaction validate + transition |
| Calendar race on Release | 9.2 | Lock order + Release | Concurrent calendar write vs Release |
| Scheduling horizon selection ambiguous | 8.3 | scheduler + source hasher | Expansion, max, backward, year boundary |
| Selected/default machine SQL integrity | 6.6 | machine configuration/DDL | Filtered at-most-one indexes |
| Fail-closed toggle | 9.4, 16.2 | Release command + config | Disabled Release blocked; no permissive fallback |
| `SupplySource` check null semantics | 6.3 | SQL constraint | Nullable + legacy rows |
| Scope / production safety | 13, 15.1 | Release enablement gate | Production-safe slice checklist |
| Dual plan files | Document status | `plans/*` | Promotion note |

## 18. Gate before Milestone 1

Milestone 1 begins only when all of the following are recorded in this plan:

- [ ] Snapshot Source Matrix completed (5.2) with Build / Recalculate / Release columns.
- [ ] Unsupported Product Definition combinations identified and mapped to error codes.
- [ ] Overlap policy frozen (activation rejects overlap; resolver agrees).
- [ ] `DefinitionValidityAtReleasePolicy` frozen (`FROZEN_SNAPSHOT_ALLOWED`).
- [ ] Identity, hashing, and concurrency vocabulary frozen (4.2).
- [ ] Stored revision identity and the no-silent-re-resolve rule frozen.
- [ ] Internal-WIP producer contract frozen, including the NULL-safe check intent.
- [ ] UOM conversion contract frozen.
- [ ] Operation input/output UOM and `RequiredMachineOutputQty` mappings frozen.
- [ ] Operation quantity propagation frozen (full route quantity; no implicit parallel split).
- [ ] `FIXED_OPERATION` non-machine duration basis frozen.
- [ ] Labour authorship and Option A1 exclusive-owner cascade design frozen.
- [ ] Labour `RateUOM`, basis quantity, and conversion contract frozen.
- [ ] Selected-machine and non-machine calendar ownership plus complete `ScheduleSourceHash` payload frozen.
- [ ] Deterministic scheduling-horizon expansion and company-scoped scheduling-lock contract frozen.
- [ ] Atomic Draft planning-input recalculation contract frozen.
- [ ] Aggregate header `WorkOrderRowVersion` advancement invariant frozen.
- [ ] Canonical `DefinitionSourceHashVersion = 1` contract frozen.
- [ ] Stable persisted-value constants and process-type behavior table frozen.
- [ ] Execution-derived fields confirmed hidden until Milestone 6.
- [ ] Foreign-key delete matrix drafted (`ON DELETE NO ACTION` canonical).
- [ ] Parallel sequencing uniqueness contract frozen.
- [ ] Legacy status contract approved (Draft / in-flight / finished).
- [ ] Migration audit queries identified (parallel-safe sequence item, status counts).
- [ ] Deployment and rollback sequencing written (fail-closed toggle).
- [ ] Concurrency token contract written.
- [ ] Single-transaction Release boundary written.
- [ ] Minimum production-safe Release slice named (15.1).
- [ ] Milestone 0B Product Definition authoring/readiness tasks completed and their tests passing.

Schema coding then proceeds against stable contracts.
