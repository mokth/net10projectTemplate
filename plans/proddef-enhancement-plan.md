# Product Definition Enhancement Plan

## 1. Objective

Make Product Definition a reliable production master-data gate so that incomplete or inconsistent definitions cannot be activated or used to create production work orders.

The design must support the following confirmed machine rule:

> One process may have multiple eligible machines. Exactly one machine is the default. The remaining machines are approved alternatives that may replace the default when it is unavailable.

Machine alternatives are an `OR` relationship, not machines that must run together.

---

## 2. Phase 1 Scope

### 2.1 Included

Phase 1 must support:

1. Multiple alternative machines per process.
2. Exactly one default machine for a machine-based process.
3. Explicit alternate-machine selection on a work order.
4. Manual processes without machines.
5. Automated processes without direct labour.
6. Waiting, curing, drying, or aging processes without machine or labour.
7. Multiple labour requirements and labour headcount.
8. Reuse of the same work-centre master as different route occurrences.
9. Reuse of the same process master as different operation occurrences.
10. Sequential and parallel work-centre stages.
11. Sequential processes within a work-centre route step.
12. Process-level BOM ownership.
13. WIP output and downstream WIP consumption.
14. WIP producer/consumer sequence validation.
15. Product revisions and immutable active history.
16. UOM conversion, yield, setup time, and machine output per cycle.
17. QC/inspection and packing processes.
18. Work-order snapshots that retain the exact Product Definition revision and route semantics.
19. Complete work-order snapshots of materials, machine alternatives, labour, yield, UOM, timing, and rates.
20. Deterministic stage-barrier, quantity, yield, cycle, scheduling, costing, and rounding rules.
21. Phase 1 subcontract representation for planning only.
22. Transaction-safe activation and work-order release with optimistic concurrency.

### 2.2 Deferred

Defer the following until the Phase 1 foundation is stable:

- Multiple machines required together for one process.
- Transfer-batch execution.
- Rework loops and repeated operation attempts.
- Optional processes and optional BOM components.
- Complete alternative routings.
- Subcontract purchasing integration.
- Material substitutes.
- By-products and co-products.
- Parallel processes inside one work-centre occurrence.

The Product Definition document should move alternative machines into Phase 1. The separate case in which several machines are required together should remain a future enhancement.

---

## 3. Target Domain Model

### 3.1 Repository-specific design decisions after review

The external review is accepted with two repository-specific adaptations:

1. **Revision ownership:** reuse the existing `PrBomHdr` table as the physical Product Definition revision header. It already owns `CompanyCode`, `ProdCode`, `Version`, `Status`, effective dates, base quantity/UOM, audit fields, and `RowVersion`. Creating parallel `PrProductDefinition` and `PrProductDefinitionRevision` tables would duplicate the current revision aggregate and make migration riskier. The Inventory Item master plus `(CompanyCode, ProdCode)` is the Product Definition root; each `PrBomHdr.UID` is a revision.
2. **Tenant ownership:** retain the repository's established `CompanyCode`, optional `BranchCode`, and optional `LocationCode` convention. The active tenant context, inventory foreign keys, planning tables, and production work orders use these codes. Introducing `TenantId`, `CompanyId`, and `BranchId` only for Product Definition would create a second ownership model. A system-wide surrogate-key tenancy migration, if desired, must be a separate architecture project.

These decisions preserve the valuable intent of the review—stable revision ownership and strict isolation—while matching the current ERP architecture.

### 3.2 Physical hierarchy

The target physical hierarchy is:

```text
IvStockMaster (product master)
 └─ PrBomHdr (Product Definition revision)
     └─ PrBomRouteStep (work-centre occurrence)
         └─ PrBomOperation (process occurrence)
             ├─ PrDefBOM (operation-owned material standards)
             ├─ PrBomMachineOption
             │   └─ Exactly one Default Machine
             └─ PrBomLabourRequirement
```

Every route step, operation, material, machine option, and labour requirement must ultimately belong through foreign keys to exactly one `PrBomHdr.UID` revision.

The retained revision statuses are:

```text
DRAFT
ACTIVE
SUPERSEDED
INACTIVE
```

`INACTIVE` covers a deliberately deactivated or cancelled revision. Adding a second `CANCELLED` status is unnecessary unless a distinct approval workflow later requires it.

Active-revision selection is derived by `CompanyCode + ProdCode + Status + EffectiveFrom/EffectiveTo`; no mutable `CurrentRevisionId` pointer is required. This supports future-dated, non-overlapping revisions without rewriting the product master.

For this plan, `ACTIVE` means approved for use within its effective interval; it does not mean “the single revision effective today.” Multiple `ACTIVE` revisions may exist only when their intervals do not overlap. `SUPERSEDED` remains readable for history and explicit old-version references but is not chosen for a new work order unless legacy effective-date compatibility requires it during migration.

Activation must not silently rewrite manufacturing content on another active revision. If the business requests “replace the open-ended predecessor from this date,” treat that as an explicit activation mode: atomically close only the predecessor's `EffectiveTo` at the new revision's `EffectiveFrom`, record an audit event, and then activate the new revision. Otherwise, an overlap is a blocking error.

### 3.3 Identity rules

The following identities must remain separate:

- `WorkCentreCode` identifies a work-centre master.
- `RouteStepId` identifies one occurrence of that work centre in a product route.
- `ProcessCode` identifies a process master.
- `OperationId` or `OperationKey` identifies one occurrence of that process in the route.
- `MachineCode` identifies a machine master.
- A machine-option row identifies a machine approved for a particular operation occurrence.

This separation is required because the same work centre and the same process may legitimately appear more than once in a product route.

### 3.4 Final-operation terminology

`IsFinalOperation` means the final operation **inside one route step**. It does not mean the operation produces the final finished good.

```text
Route Step WC1
 ├─ Process A
 └─ Process B [IsFinalOperation] -> WIP001

Route Step WC2
 ├─ Process P1
 └─ Process P2 [IsFinalOperation] -> WIP002

Route Step WC3
 └─ Assembly [IsFinalOperation] -> FG001
```

All three route steps have one final operation. Only WC3 is the final finished-goods route step because its output is the Product Definition's `ProdCode`.

---

## 4. Database Changes

### 4.1 Make the existing revision header explicit

Retain `PrBomHdr` as the Product Definition revision table. Its required aggregate key remains:

```text
UID                    Revision identity
CompanyCode            Company ownership and query scope
ProdCode               Product Definition root within company
Version                Monotonic product revision number
Status                 DRAFT / ACTIVE / SUPERSEDED / INACTIVE
EffectiveFrom
EffectiveTo             Exclusive end date
BaseQty                 Standard good-output quantity
BaseUom
BranchCode              Audit/write stamp only; not revision selection
LocationCode            Audit/write stamp only; not revision selection
CreatedDate / CreatedBy
ModifiedDate / ModifiedBy
RowVersion
```

Add:

```text
ValidationRuleVersion
ValidationStatus        UNVERIFIED / VALID / INVALID
ValidatedDate
ValidatedBy
ActivatedDate
ActivatedBy
```

Required indexes and invariants:

- Unique `(CompanyCode, ProdCode, Version)`.
- Product revisions are company-owned, not branch-owned.
- All child rows use revision foreign keys; denormalized `CompanyCode` may be retained only where it materially improves filtering or legacy compatibility.
- Branch-specific and location-specific choices belong to the work order or execution document, not to the reusable Product Definition revision, except for explicitly approved warehouse defaults.
- Effective-date overlap is enforced transactionally during activation because a simple unique index cannot validate date ranges.

Because warehouses are branch-owned in the current inventory model, do not leave an unqualified branch warehouse as a universal company-level BOM standard. Add a branch-default mapping such as `PrBomMaterialBranchDefault(MaterialId, BranchCode, WarehouseCode)`, or resolve the warehouse during work-order creation from branch inventory policy. Migrate the current BOM warehouse into the mapping for its recorded branch. Activation validates configured mappings; work-order validation requires a valid warehouse for the work order's actual branch.

### 4.2 Add a persistent route-step table

Create `PrBomRouteStep` with approximately these columns:

```text
UID
RouteStepKey
BomHdrId
CompanyCode
WorkCentreCode
StageSequence
OutputItemCode
OutputType
StandardOutputQty
OutputUom
YieldPercent
CreatedDate
CreatedBy
ModifiedDate
ModifiedBy
RowVersion
```

Recommended `OutputType` values:

```text
WIP_STOCKED
WIP_NONSTOCK
FINISHED_GOODS
```

This table replaces the current practice of deriving a centre by grouping operations on `WorkCentreCode + OutputItemCode`.

### 4.3 Link operations to route occurrences

Extend or revise `PrBomOperation`:

```text
RouteStepId
OperationKey
OperationCode
ProcessSequence
ProcessType
IsFinalOperation
StandardDurationMinutes
SetupLossQty
OperationLossQty
Remark
```

Recommended `ProcessType` values:

```text
MACHINE
AUTOMATED
MANUAL
INSPECTION
WAIT
PACKING
SUBCONTRACT
```

`OperationKey` must remain independent from `OperationCode`, allowing one process master to be used several times.

### 4.4 Retain and strengthen the existing material table

Retain `PrDefBOM` as the physical Product Definition material table for Phase 1. Do not create a parallel `PrBomMaterial` table.

Add or standardize:

```text
OperationId             Required FK after migration
OperationKey            Temporary compatibility key during migration
MaterialStandardQty
StandardUom
ScrapPercent
TolerancePercent
IssueMethod
SupplySource
```

Recommended `IssueMethod` values:

```text
MANUAL
BACKFLUSH
PICK_LIST
```

Recommended `SupplySource` values:

```text
PURCHASED
INTERNAL_ROUTE_WIP
SEPARATE_PRODUCT_DEFINITION
EXTERNAL_SUPPLY
```

Migrate `OperationKey` ownership to the `OperationId` foreign key, then make `OperationId` required for activated routed definitions. Retain `OperationKey` only while compatibility readers/imports still require it.

Because Product Definitions are company-owned while warehouses are branch-owned, store branch-specific issue defaults in `PrBomMaterialBranchDefault` rather than treating one `PrDefBOM.Warehouse` value as universal.

### 4.5 Support alternative machines

Keep `PrBomMachineOption`, but define it as a list of eligible alternatives:

```text
OperationId
MachineCode
IsDefault
Priority
CycleSeconds
OutputPerCycle
SetupSeconds
ConversionSeconds
QueueSeconds
MachineRatePerHour
```

Rename `IsPrimary` to `IsDefault` in the application. The physical database column can initially remain `IsPrimary` to simplify migration.

Add a filtered unique index to enforce at most one default:

```sql
CREATE UNIQUE INDEX UX_PrBomMachineOption_OneDefault
ON dbo.PrBomMachineOption(OperationID)
WHERE IsPrimary = 1;
```

Application activation validation must enforce at least one default for a machine-based process.

Do not use `ParallelMachineCount` to represent alternative machines. Parallel resources and alternative machines have different execution meanings.

### 4.6 Move labour requirements to operation level

Create `PrBomLabourRequirement`:

```text
UID
OperationId
MachineOptionId NULL
LabourCode
RequiredHeadcount
SetupMinutes
RunMinutes
CostRate
CostBasis
CreatedDate
CreatedBy
ModifiedDate
ModifiedBy
RowVersion
```

Interpretation:

- `MachineOptionId = NULL`: labour is required regardless of the selected machine.
- `MachineOptionId` populated: labour is specific to that machine alternative.

This structure supports manual processes and multiple labour types without requiring a fake machine.

To avoid SQL Server multiple-cascade-path problems, make `OperationId` the ownership/cascade foreign key and configure optional `MachineOptionId` as `DeleteBehavior.Restrict`/`NoAction`. Removing a machine option must explicitly reject, reassign, or delete its machine-specific labour requirements in the application transaction.

Use separate filtered unique indexes for operation-wide labour (`MachineOptionId IS NULL`) and machine-specific labour (`MachineOptionId IS NOT NULL`) so SQL NULL semantics cannot permit ambiguous duplicates.

### 4.7 Add an item-UOM conversion source of truth

The repository currently has UOM codes but no general inventory UOM conversion master. Add an item-specific table such as `IvItemUomConversion` before enforcing conversion-dependent Product Definition rules:

```text
UID
CompanyCode
ItemCode
FromUom
ToUom
FromQty
ToQty
RoundingScale
RoundingMode
IsActive
Created/Modified/Audit
RowVersion
```

Rules:

- Unique active conversion per `CompanyCode + ItemCode + FromUom + ToUom`.
- Both quantities must be greater than zero.
- Reverse conversion is calculated from the approved pair; do not maintain an independently drifting reverse factor.
- Conversion is item-specific because conversions such as KG-to-PCS vary by item.
- Transformations between different items are expressed by Product Definition/BOM standards, not by UOM conversion rows.
- Preview, BOM explosion, MRP, work orders, inventory issue, and costing must call one shared conversion service.

### 4.8 Add database constraints

### 4.6 Add database constraints

Add database constraints for:

- Stage and process sequence greater than zero.
- Output quantity greater than zero.
- Yield greater than zero and no more than 100.
- Scrap and tolerance within the agreed range.
- Machine time values nonnegative.
- Machine output per cycle greater than zero.
- Labour headcount greater than zero.
- Cost values nonnegative.
- Effective To later than Effective From.
- Unique machine code per operation.
- Unique labour requirement per operation/machine combination.
- Foreign keys and cascade behaviour for the new hierarchy.

Deliverables belong under:

- `ErpWeb.Model/Entities/Planning`
- `ErpWeb.Model/Configurations/Planning`
- `scripts`

---

## 5. Centralized Validation Architecture

Create a central validation service, for example:

```text
IProductDefinitionValidator
ProductDefinitionValidator
```

Return structured issues rather than only strings:

```csharp
public sealed record ValidationIssue(
    string Code,
    string FieldPath,
    ValidationSeverity Severity,
    string Message);
```

Validation modes:

```text
Draft
Activation
WorkOrder
```

The following entry paths must all use the same validator:

- Product Definition UI save.
- Product Definition activation.
- Product Definition import.
- APIs or background integrations.
- Work-order preview.
- Work-order release.

Razor handlers may provide immediate usability checks, but server-side centralized validation remains authoritative.

---

## 6. Draft Validation

Drafts may be incomplete, but must not contain malformed data.

Reject:

- Missing or invalid referenced master codes.
- Cross-company references.
- Negative quantities, losses, costs, or times.
- Duplicate component within one operation.
- Duplicate machine option within one operation.
- Duplicate labour requirement.
- Process that does not belong to the selected work centre.
- Machine that is not approved for the selected process.
- Empty or duplicate route and operation identities.
- Circular BOM structures.
- Invalid effective-date ranges.

Allow drafts to temporarily omit:

- A final-process selection.
- A default machine.
- Complete labour standards.
- Complete process duration.
- Downstream WIP consumption.

These omissions become blocking errors during activation.

---

## 7. Activation Validation

Activation is the strict production-readiness gate.

### 7.1 Header rules

- Product must exist and be active.
- Product must be explicitly classified as `MAKE` or `PHANTOM`.
- Do not silently convert `BUY` to `MAKE` during Product Definition save.
- Base quantity must be greater than zero.
- Base UOM must exist and be valid for the product.
- Effective From must be before Effective To.
- Applicable revision dates must not conflict.
- Activation normally fails on any effective overlap. In explicit replace-from-date mode, it may atomically close only the open-ended predecessor at the new start date; non-overlapping future active revisions remain unchanged.
- At least one route step must exist.
- At least one operation must exist.
- Active revisions remain immutable; changes require a new draft version.

Legacy BOM-only definitions should be explicitly identified and migrated. They should not remain a general way to activate new Product Definitions without routing.

### 7.2 Route-step rules

- Every route step must contain at least one operation.
- Stage sequence must be positive.
- Equal stage sequences mean parallel route steps.
- Output item must be active.
- Output quantity must be greater than zero.
- Output UOM must match the item UOM or have an approved conversion.
- Yield must be greater than zero and no more than 100.
- Exactly one final production stage must output the Product Definition's finished-product code.
- Earlier route steps should normally output WIP rather than the same finished-good code.
- Duplicate route identities are prohibited.
- Unused WIP output produces a warning or error based on policy.

#### Phase 1 parallel-stage barrier

Phase 1 uses deterministic stage barriers rather than an explicit dependency network:

1. Route steps with the same `StageSequence` may execute concurrently.
2. Distinct stage values are ordered numerically; gaps such as 10, 20, 40 are valid.
3. A route step in the next distinct stage cannot start until **all route steps in the immediately preceding distinct stage** are complete.
4. A route step is complete only when its final operation is complete, required QC has passed, and its required good-output quantity is accounted for.
5. WIP cannot be consumed until its producing route step has completed the required quantity.
6. Phase 1 is full-stage/full-batch gating; partial transfer batches are deferred.

Example:

```text
Stage 10: WC1 -> WIP001 ─┐
                         ├─ both complete -> Stage 20: WC3 -> FG001
Stage 10: WC2 -> WIP002 ─┘
```

Explicit dependency edges may replace or supplement this barrier model in a future phase.

### 7.3 Operation rules

- Process must belong to the route step's work centre.
- Process sequence must be positive.
- Process sequence must be unique within the route step.
- Phase 1 does not allow parallel processes within one route step.
- Exactly one operation per route step must be marked final.
- The final operation must have the highest process sequence.
- Setup and operation loss values cannot be negative.
- The same process master may be reused through different operation-occurrence identities.
- A scheduling basis must exist through machine timing or process standard duration.

### 7.4 Conditional resource rules

Apply resource validation according to process type:

| Process type | Machine | Labour | Duration source |
|---|---:|---:|---|
| Machine operation | Required | Optional or required by policy | Machine timing |
| Automated | Required | Optional | Machine timing |
| Manual | Optional | Required | Process/labour standard |
| Inspection/QC | Optional | Normally required | Process/labour standard |
| Wait/curing | Not required | Not required | Process duration |
| Subcontract | Not required | Not required | Supplier lead time |

### 7.5 Alternative-machine rules

For `MACHINE` and `AUTOMATED` processes, one or more eligible machine options must exist. For any process type that has one or more machine options—including machine-assisted inspection or packing—exactly one option must be the default.

- Exactly one option must have `IsDefault = true`.
- Zero defaults is an activation error.
- More than one default is an activation error.
- Alternative priority must be positive and unique within the operation.
- Machine codes must be unique within the operation.
- Every machine must be active and approved for the process.
- Each machine option must carry its own timing and output-per-cycle standards.
- Cycle, setup, conversion, and queue values cannot be negative.
- Output per cycle must be greater than zero when cycle time is used.

Machine availability or downtime is operational state. It does not make the Product Definition invalid, but it affects work-order machine selection.

### 7.6 Labour rules

- Manual processes require at least one labour requirement.
- Automated processes may have no direct labour.
- Multiple labour types are allowed.
- Labour code must exist and be active.
- Required headcount must be greater than zero.
- Labour cost and time values cannot be negative.
- Machine-specific labour must reference an eligible machine option on the same operation.
- Duplicate labour requirements are prohibited within the same applicable scope.

### 7.7 BOM and WIP rules

- Material must be assigned to its consuming operation.
- Component item must exist and be active.
- Component cannot directly equal the Product Definition's finished product.
- Standard quantity must be greater than zero.
- Scrap and tolerance must remain within the agreed range.
- Warehouse must be active and permitted for the branch.
- Material UOM must match the stock item or have an approved conversion.
- An internally produced WIP component must have an earlier producing route step.
- WIP cannot be consumed in the same or an earlier stage than its producer.
- A separate `MAKE` or `PHANTOM` component must have an applicable active Product Definition.
- External WIP supply must be explicitly identified rather than inferred.
- BOM and internal route graphs must be acyclic.
- Shared raw materials across operations must retain process ownership while aggregating correctly for planning.

### 7.8 Yield and UOM rules

Define separate concepts:

```text
InputQty
InputUOM
StandardOutputQty
OutputUOM
YieldPercent
OutputPerCycle
```

Reject activation when:

- Yield is `<= 0` or `> 100`.
- A required UOM conversion is missing.
- A mass-to-unit conversion has no defined formula.
- Machine output per cycle is not positive.
- Downstream quantity calculation is dimensionally invalid.

Do not overload `OutputBaseQty` to mean both route-step output conversion and machine output per cycle.

### 7.9 Separate dependency-graph validations

Implement and report two independent cycle checks:

1. **Inter-product BOM cycle** — a manufactured product eventually consumes itself through separate Product Definitions, for example `FG001 -> SUB001 -> SUB002 -> FG001`.
2. **Internal WIP route cycle** — route step A consumes output from route step B while route step B consumes output from route step A.

The validations may share a graph library but must use separate graph construction, issue codes, error messages, and tests. A definition must pass both checks before activation.

### 7.10 Phase 1 subcontract boundary

`SUBCONTRACT` in Phase 1 supports only:

- Route representation.
- Standard supplier lead time.
- Planning dates.
- An optional standard-cost placeholder.
- Work-order snapshot and reporting.

It does not automatically:

- Create purchase orders.
- Issue material to a subcontractor.
- Receive subcontract goods or GRNs.
- Perform supplier invoicing.
- Reserve supplier capacity.

Full procurement/subcontract integration remains a later phase.

### 7.11 Scheduling and costing source of truth

Use the following deterministic rules in Product Definition preview, work-order generation, capacity planning, estimated completion, and production costing:

| Process type | Scheduling duration | Machine cost | Labour cost |
|---|---|---|---|
| `MACHINE` | Selected machine timing | Selected machine duration × frozen hourly rate | Applicable labour requirements using calculated or specified duration |
| `AUTOMATED` | Selected machine timing | Selected machine duration × frozen hourly rate | Optional setup/attendance labour only |
| `MANUAL` | `StandardDurationMinutes`, or explicit labour run standard when configured | None | Headcount × labour duration × rate |
| `INSPECTION` | `StandardDurationMinutes`, or explicit labour standard | Optional only when an inspection machine is selected | Applicable inspection labour standard |
| `WAIT` | `StandardDurationMinutes` | None | None |
| `PACKING` | Machine timing when machine-based; otherwise operation duration | Selected machine cost when applicable | Applicable labour standard |
| `SUBCONTRACT` | Standard supplier lead time | None | Normally none; use the subcontract cost placeholder separately |

Rules:

- Machine-based duration always comes from the selected machine option, never an unselected alternative.
- Machine rate and labour rate used by a work order are frozen in its snapshot.
- Operation duration is not added again when machine timing is authoritative.
- Queue, setup, conversion, and run time remain separate for audit and planning even when a total is displayed.
- Labour `CostBasis` must be an explicit enum such as `PER_HOUR`, `PER_OUTPUT_UNIT`, or `FIXED_PER_OPERATION`; formulas must not infer the basis from a nonzero field.

### 7.12 Quantity, yield, runtime, and rounding formulas

Use decimal arithmetic throughout. Do not use binary floating-point for production quantities or costing standards.

#### BOM scaling

For a BOM defined for a standard good-output quantity:

```text
OutputScale
= RequiredGoodOutputQty / ProductBaseOutputQty

BaseMaterialQty
= OutputScale × MaterialStandardQty

MaterialWithScrap
= BaseMaterialQty × (1 + MaterialScrapPercent / 100)
```

If route yield represents additional expected process loss not already included in the BOM standard:

```text
RequiredInputBasis
= RequiredGoodOutputQty / (YieldPercent / 100)

RequiredMaterialQty
= (RequiredInputBasis / ProductBaseOutputQty)
   × MaterialStandardQty
   × (1 + MaterialScrapPercent / 100)
```

The Product Definition must state whether its BOM standards are gross or net of expected yield. Phase 1 adopts **net good-output BOM standards**, so yield is applied by the engine and must not also be embedded in `MaterialStandardQty`.

For a multi-stage route, calculate required quantities backwards from final good output. Each route step converts its required good output into the upstream input/WIP requirement using that step's yield; the result becomes the required good output of the producing upstream step. Shared or parallel WIP requirements are aggregated only after preserving their producer/consumer ownership.

#### Pack-size example

```text
Product Base Output Qty = 5 PCS
RM001 Standard Qty      = 1 PCS
Work Order Qty          = 100 PCS

RM001 before scrap/yield
= 100 / 5 × 1
= 20 PCS
```

This is preferred to forcing users to enter `0.2 PCS` per unit, although both representations must calculate equivalently within configured precision.

#### Machine cycles and duration

```text
CycleCount
= Ceiling(RequiredMachineOutputQty / OutputPerCycle)

RunSeconds
= CycleCount × CycleSeconds

TotalMachineSeconds
= QueueSeconds
 + SetupSeconds
 + ConversionSeconds
 + RunSeconds
```

Setup and conversion are charged once per work-order operation in Phase 1. Future production-batch splitting may charge them once per actual batch.

#### Cost formulas

```text
MachineCost
= (TotalMachineSeconds / 3600) × MachineRatePerHour

HourlyLabourCost
= RequiredHeadcount
   × ((LabourSetupMinutes + LabourRunMinutes) / 60)
   × LabourHourlyRate

PerOutputLabourCost
= RequiredGoodOutputQty × LabourRatePerOutputUnit
```

When labour run duration is configured to follow the selected machine, use the selected machine run duration instead of storing a duplicate labour run duration.

#### Rounding policy

- Keep full configured decimal precision through intermediate quantity and cost calculations.
- Apply `Ceiling` only to discrete machine cycle counts and indivisible packaging quantities.
- Round material requirements at the final required quantity using the component UOM precision and configured rounding direction; default to round-up where shortage must be prevented.
- Round time only when persisting/displaying the final duration, not between formula steps.
- Round monetary amounts using the ERP currency precision and existing financial rounding policy.
- Centralize these rules in shared calculation services so preview, work-order generation, MRP, and costing cannot drift.

### 7.13 Warning examples

Warnings need not block activation unless business policy promotes them to errors:

- Sequences do not use gaps such as 10, 20, 30.
- Labour or machine cost is zero.
- A process consumes no material.
- A WIP output is not consumed in the current route.
- Scrap or tolerance is unusually high.
- An alternative machine has materially incomplete standards compared with the default.

---

## 8. Product Definition Entry UI

### 8.1 Route-step editor

Display persisted route-step rows rather than projected groups.

Recommended columns:

```text
Stage
Work Centre
Output Item
Output Type
Output Qty
Output UOM
Yield %
Process Count
Validation Status
```

Allow the same work-centre code to appear in multiple route steps.

### 8.2 Operation editor

Add:

```text
Process
Sequence
Process Type
Final Process
Standard Duration
Setup Loss
Operation Loss
```

Allow the same process code to appear multiple times through separate operation occurrences.

### 8.3 Machine-option editor

Replace the single-machine editor with a grid:

| Machine | Default | Priority | Cycle | Output/Cycle | Setup | Queue | Hourly Rate |
|---|---:|---:|---:|---:|---:|---:|---:|

Required behaviour:

- Permit multiple eligible machine rows.
- Selecting one row as default clears the previous default.
- Prevent duplicate machine codes.
- Display the default clearly.
- Explain that non-default rows are approved alternatives.
- Preserve machine-specific timing and output-per-cycle values.

### 8.4 Labour editor

Use a grid:

| Labour | Headcount | Machine-specific | Setup time | Run time / Follow Machine | Rate | Cost Basis |
|---|---:|---|---:|---|---:|---|

Permit multiple labour requirements.

### 8.5 Validation presentation

Add a `Validate` action before activation.

Display issues grouped by:

```text
Header
Route Step
Operation
Material
Machine
Labour
```

Activation remains disabled while blocking errors exist. Warnings may require acknowledgement.

Primary files:

- `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor`
- `ErpWeb.UI/Planning/Masters/PrProductDefEntry.razor.cs`
- `ErpWeb.Core/Planning/PrProductDefService.cs`

---

## 9. Work-Order Snapshot and Machine Selection

### 9.1 Historically self-contained snapshot

A work order must remain interpretable and reproducible without rereading mutable master data. Freeze the complete selected manufacturing definition, not only the selected machine.

```text
ProductionWorkOrder
 ├─ SourceBomHdrId / SourceProductDefinitionRevisionId
 ├─ SourceBomVersion
 ├─ ValidationRuleVersion
 └─ ProductionWorkOrderRouteStep
     ├─ SourceRouteStepKey
     ├─ StageSequence
     ├─ WorkCentreCode and description
     ├─ Output item, type, quantity, UOM, and yield
     ├─ Planned, completed, good, scrap, and remaining quantity
     ├─ Execution status and stage-barrier state
     └─ ProductionWorkOrderOperation
         ├─ SourceOperationKey
         ├─ Process code, description, sequence, and type
         ├─ Final-operation flag and standard duration
         ├─ ProductionWorkOrderMaterial
         ├─ ProductionWorkOrderMachineOption
         └─ ProductionWorkOrderLabour
```

The existing `ProductionWorkOrder.SourceBomHdrId` and `SourceBomVersion` remain the revision reference. Add `ProductionWorkOrderRouteStep` instead of storing route-step output fields redundantly on every operation.

Snapshot values include:

- Route-step identity, stage, work centre, output item/type/quantity/UOM, and yield.
- Route-step planned/completed/good/scrap/remaining quantities and execution status required to enforce stage barriers.
- Operation identity, sequence, type, final flag, duration, and loss standards.
- Material item, quantity, UOM, scrap, tolerance, warehouse, supply method, and source material identity.
- Every eligible machine option, its default flag, priority, timing, output per cycle, and frozen rate.
- Every applicable labour requirement, headcount, time, rate, cost basis, and optional machine association.

Do not flatten centre and process sequences into an unrelated incrementing number. A display line number may exist, but it cannot replace `StageSequence` and `ProcessSequence`.

### 9.2 Machine selection within the snapshot

The work-order operation references one selected snapshotted machine option. All eligible alternatives are retained so later authorized substitution does not depend on a changed Product Definition.

At work-order preview:

1. Resolve the applicable active Product Definition revision.
2. Copy route steps and operation occurrences.
3. Preserve stage and process sequences.
4. Copy eligible machine alternatives.
5. Select the default machine.
6. Calculate duration and cost using only the selected machine.
7. Preserve parallel-stage information.
8. Freeze material, labour, machine, yield, UOM, duration, rate, and cost-basis standards.

At work-order release:

- Require one selected machine for each machine-based process.
- Check current availability.
- If the default is unavailable, require explicit selection of an approved alternative.
- Recalculate timing and cost from the selected machine's standards.
- Record whether the selection is a substitution.

After release:

- A substitution must use an alternative frozen in the work-order snapshot.
- A substitution reason is required.
- Timing and cost are recalculated.
- An audit event or change order is created.
- The Product Definition is never rewritten by the substitution.

### 9.3 Machine-availability and capacity policy

Phase 1 does not implement finite-capacity reservation.

- Machine master state (`ACTIVE`, `DOWN`, `OUT_OF_SERVICE`, when available) is a release-time eligibility check.
- A machine explicitly marked unavailable cannot be selected; the user must choose an approved alternative.
- Calendar/time-slot capacity conflicts are informational warnings only in Phase 1.
- Two work orders may therefore be released against overlapping time on the same active machine; this is a known Phase 1 limitation, not a guaranteed capacity reservation.
- Enforced capacity reservation and transactional time-slot allocation belong to a later finite-scheduling phase.

### 9.4 Transaction and concurrency policy

#### Product Definition activation

- Require the submitted `RowVersion` for the draft revision.
- Run validation, effective-date overlap detection, optional explicit predecessor closing, and activation inside one database transaction.
- Use serializable isolation or an equivalent per-`CompanyCode + ProdCode` application lock during the overlap recheck and status changes.
- Recheck the draft `RowVersion` immediately before activation.
- Rely on database unique/check constraints as the final guard for route identities and default-machine uniqueness.
- A conflict returns a safe reload/retry result; it never silently overwrites another user's activation.

Non-overlapping future `ACTIVE` revisions may coexist. The applicable revision is resolved using the work-order as-of date and the effective interval `[EffectiveFrom, EffectiveTo)`.

#### Work-order preview and release

- Preview creates or refreshes a deterministic snapshot and snapshot hash from one resolved revision.
- Release uses the already saved snapshot; it must not silently rebuild from a newly active Product Definition.
- Require the work-order `RowVersion` and verify the snapshot hash inside the release transaction.
- Recheck selected machine master eligibility immediately before release.
- If the definition or machine choice must change, require an explicit preview refresh while still draft or a change order after release.
- Row-version conflicts return reload/retry guidance.

Primary files:

- `ErpWeb.Model/Entities/Production/ProductionWorkOrderOperation.cs`
- `ErpWeb.Model/Entities/Production/ProductionWorkOrderMaterial.cs`
- New work-order route-step, machine-option, and labour snapshot entities/configurations
- `ErpWeb.Core/Production/IProductionWorkOrderService.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.cs`

---

## 10. Existing-Data Migration

Do not silently alter historical active definitions or released work orders.

Migration procedure:

1. Back up all affected tables.
2. Deploy additive schema changes first.
3. Create route-step rows from each existing work-centre/output grouping.
4. Link existing operations to generated route-step IDs.
5. Convert the current single machine into the default machine option.
6. Convert current labour rows into operation or machine-specific labour requirements.
7. Mark existing definitions as unverified under the new validation-rule version.
8. Run a data-quality audit.
9. Create corrected draft revisions for invalid active definitions.
10. Activate corrected versions through the normal validator.
11. Leave existing released work orders unchanged.
12. Remove compatibility paths only after reconciliation.

Audit categories:

- No routing.
- No final operation.
- Final operation not last.
- Missing or multiple default machines.
- Duplicate process sequences.
- Final stage does not output the finished product.
- WIP consumed before it is produced.
- Missing manufactured-child Product Definition.
- Missing UOM conversion.
- Invalid yield, scrap, or tolerance.
- Inactive master references.
- Conflicting route-step values hidden by current projection logic.

Recommended enforcement rollout:

```text
Audit only
→ Warnings during activation
→ Block invalid activation
→ Block new work orders using unverified definitions
```

---

## 11. Automated Test Plan

### 11.1 Alternative machines

- Three eligible machines with one default activate successfully.
- Zero default machines fails activation.
- Two default machines fails activation.
- Duplicate machine code fails.
- Alternative priorities must be unique.
- Work-order preview selects the default.
- User can select an approved alternative.
- An unapproved machine substitution fails.
- Alternative timing and cost replace the default values.
- Post-release substitution creates an audit record.

### 11.2 Process resources

- Manual process with labour and no machine succeeds.
- Automated process with a machine and no labour succeeds.
- Waiting process with duration and no resources succeeds.
- Machine process without a machine fails activation.
- Multiple labour requirements succeed.
- Labour headcount of zero fails.
- Machine-specific labour cannot reference an unrelated machine option.

### 11.3 Routing

- Single work centre and process succeeds.
- Multiple sequential work centres succeed.
- Parallel work centres with the same stage sequence succeed.
- Two Stage 10 route steps may execute concurrently.
- Stage 20 cannot start until every Stage 10 route step is complete.
- WIP from one parallel route step cannot be consumed before its producing route step completes.
- Duplicate process sequence within one route step fails.
- Final operation not last fails.
- The same work-centre master used twice succeeds.
- The same process master used twice succeeds.
- Stage and process sequences survive work-order snapshot creation.

### 11.4 BOM and WIP

- Material assigned to the consuming process succeeds.
- Internal WIP consumed downstream succeeds.
- WIP consumed before production fails.
- Missing manufactured-child definition fails activation.
- Shared raw material across operations aggregates correctly without losing ownership.
- Inter-product manufactured-BOM cycle fails with the BOM-cycle issue code.
- Internal WIP route cycle fails with the route-cycle issue code.
- Missing UOM conversion fails.
- Invalid yield, scrap, and tolerance fail.
- A process without BOM remains valid.
- Base output `5 PCS` with material quantity `1 PCS` scales to `20 PCS` material for a `100 PCS` work order before scrap/yield.
- Equivalent fractional BOM representation produces the same result within configured precision.
- Yield, scrap, UOM conversion, and UOM rounding combine deterministically.
- Machine cycle count uses ceiling and never produces a fractional cycle.

### 11.5 Revision and history

- Active revision is immutable.
- New draft can be cloned from an active revision.
- Two overlapping active effective intervals for the same company/product fail activation.
- A non-overlapping future active revision may coexist.
- Superseded and inactive revisions remain readable.
- Existing work order retains its original revision.
- New work order uses the newly applicable revision.
- Tenant and company isolation remain enforced.
- Import cannot bypass centralized activation validation.
- Concurrency conflicts return a safe reload error.

### 11.6 Work-order snapshot isolation

- Changing Product Definition machine timing or rate after work-order creation does not alter the existing work order.
- Changing BOM quantities after work-order creation does not alter existing material requirements.
- Changing labour headcount or rate after work-order creation does not alter historical work-order costing.
- Changing yield, UOM, process duration, or route sequence does not alter the existing snapshot.
- A new work order uses the newly applicable active revision and its new standards.
- Work-order release uses the saved snapshot and does not silently rebuild from current master data.
- Snapshot hash changes only through an explicit draft refresh or authorized change order.

### 11.7 Scheduling and costing formulas

- Machine duration uses only the selected machine option.
- Setup, conversion, and queue time are charged once per work-order operation.
- Manual labour cost uses headcount, duration, rate, and explicit cost basis.
- Automated operation with no labour produces zero labour cost.
- Waiting operation uses standard duration and produces no resource cost.
- Preview, work-order generation, and costing services return the same result for the same frozen standards.

### 11.8 Concurrency and availability

- Two users attempting to activate the same draft result in one success and one safe concurrency failure.
- Two concurrent activations with overlapping effective periods cannot both commit.
- Row-version conflict provides reload/retry guidance.
- Work-order release fails when its row version or snapshot hash is stale.
- Work-order release does not silently use Product Definition changes made after snapshot creation.
- A machine marked down before release requires selection of an approved alternative.
- Overlapping time on an active machine produces an informational capacity warning, not an implied reservation, in Phase 1.

---

## 12. Delivery Sequence

Implement in this order:

```text
1. Confirm and document the domain contract
2. Add database structures and constraints
3. Implement the centralized validator
4. Refactor Product Definition persistence service
5. Update the Product Definition entry UI
6. Update work-order snapshots and machine selection
7. Add migration and audit scripts
8. Add automated tests
9. Run data reconciliation
10. Complete user acceptance testing
11. Enable activation enforcement
12. Enable work-order enforcement
```

Avoid implementing the UI against the current projected-centre model and then replacing it again. Establish the persistent route-step and operation-occurrence identities first.

---

## 13. Suggested Work Packages

### Work Package A — Domain and schema foundation

- Update the Product Definition specification.
- Formalize `PrBomHdr` as the revision aggregate and add validation/activation metadata.
- Standardize company ownership and branch/location applicability for every new table.
- Retain `PrDefBOM` as the material table, add operation ownership, and add branch warehouse-default mapping.
- Add the item-specific UOM conversion master and shared conversion contract.
- Add route-step entity and configuration.
- Extend operation fields.
- Extend machine options.
- Add operation-level labour requirements.
- Add database constraints and migration scripts.

### Work Package B — Validation engine

- Add structured validation issues.
- Implement Draft, Activation, and WorkOrder modes.
- Add header, route, operation, BOM, machine, labour, WIP, UOM, inter-product-cycle, and internal-route-cycle validators.
- Integrate the validator into Product Definition save and activation.
- Add shared quantity, yield, cycle, duration, rounding, and cost calculation services.

### Work Package C — Product Definition UI

- Replace projected centres with route-step editing.
- Add process-type and duration fields.
- Build the alternative-machine grid.
- Build the multiple-labour grid.
- Add grouped validation results and activation gating.

### Work Package D — Work-order integration

- Add the complete route-step, operation, material, machine-option, and labour snapshot hierarchy.
- Preserve route and operation identities, stage barriers, yield, UOM, rates, and cost bases.
- Snapshot all eligible machine alternatives.
- Select the default machine.
- Implement authorized alternative selection.
- Recalculate duration and cost.
- Add substitution audit events.
- Enforce row-version/snapshot-hash release checks and document informational capacity behaviour.

### Work Package E — Migration and rollout

- Backfill existing definitions.
- Produce the data-quality report.
- Correct invalid definitions through new revisions.
- Run regression and user-acceptance testing.
- Enable enforcement in stages.

---

## 14. Definition of Done

The enhancement is complete only when:

- Every activation path uses the centralized validator.
- Multiple eligible machines with exactly one default work end to end.
- Only the selected machine enters work-order scheduling and costing.
- Manual, automated, inspection, and waiting processes validate correctly.
- Multiple labour requirements and headcount are supported.
- Route steps and operation occurrences have independent identities.
- Stage and process sequences survive into work-order snapshots.
- Sequential and parallel stage meanings are preserved.
- WIP dependencies and final finished-good output are validated.
- UOM and yield calculations are deterministic and tested.
- BOM scaling, pack-size, machine-cycle, duration, rounding, machine-cost, and labour-cost formulas are centralized and deterministic.
- Active revisions remain immutable.
- Effective-date activation is concurrency-safe and permits only non-overlapping active revision intervals.
- Existing work orders retain self-contained historical route, material, machine, labour, yield, UOM, timing, and rate snapshots.
- Parallel stages obey the Phase 1 all-previous-stage completion barrier.
- Inter-product BOM cycles and internal WIP route cycles are validated separately.
- Phase 1 subcontract and machine-capacity limitations are explicit in UI and documentation.
- Import and API paths cannot bypass validation.
- Migration reports contain no unresolved blocking errors for definitions allowed into new production.
- Production users successfully test representative simple, sequential, parallel, WIP, manual, automated, QC, and alternate-machine products.

This creates a controlled activation boundary that prevents invalid Product Definition data from flowing into planning, material requirements, scheduling, costing, WIP, and finished-goods production.

---

## 15. Review-Amendment Traceability

| Review topic | Disposition in this plan |
|---|---|
| Concrete revision model | Accepted and adapted: existing `PrBomHdr` is formalized as the physical revision aggregate instead of adding duplicate root/revision tables. |
| Tenant/company/branch ownership | Accepted in intent and adapted to repository convention: `CompanyCode` is the company ownership key; branch/location are applied only where operationally relevant. |
| Complete work-order snapshot | Accepted: route step, operation, material, all eligible machine options, labour, yield, UOM, timing, rates, and cost bases are frozen. |
| Parallel-stage semantics | Accepted: equal stages run concurrently and the next distinct stage uses an all-previous-stage completion barrier in Phase 1. |
| Final-operation meaning | Accepted and clarified: final within route step is distinct from the route step that outputs the finished good. |
| Phase 1 subcontract scope | Accepted: routing, lead time, planning, and optional standard cost only; procurement integration remains deferred. |
| Scheduling/costing source of truth | Accepted: deterministic source rules are defined for every process type. |
| Yield, output, cycle, duration, and rounding formulas | Accepted: formulas and a centralized calculation policy are specified. |
| Pack-size/base-output BOM | Accepted: explicit formula, example, and tests are included. |
| Machine availability concurrency | Accepted with a Phase 1 decision: hard machine-state eligibility, but time-slot capacity is informational and not reserved. |
| Two circular-dependency types | Accepted: inter-product BOM and internal WIP route cycles are separate validators and tests. |
| Default-machine database enforcement | Accepted unchanged: filtered unique index supplies “at most one,” activation validation supplies “at least one.” |
| Activation and release concurrency | Accepted: row versions, snapshot hash, transactional overlap recheck, and serialized per-product activation are required. |

Additional repository review identified and resolved three implementation gaps not explicit in the external review:

1. Retain and migrate the existing `PrDefBOM` table instead of creating an ambiguous parallel material table.
2. Add an item-specific UOM conversion master because the repository currently has UOM codes but no general conversion source of truth.
3. Prevent SQL Server multiple cascade paths in operation-level/machine-specific labour ownership by using one cascade owner and a restricted optional machine foreign key.
