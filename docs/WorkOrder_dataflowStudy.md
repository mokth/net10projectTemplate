# Work Order Creation from Product Definition

## Objective

Please study how my old ERP creates a **Production Work Order** from the existing **Product Definition**.

I want to migrate the same core concept, data flow, and business logic into my new ERP, but the implementation should be reviewed and enhanced where necessary so that it is suitable for a modern production/manufacturing ERP.

Do not simply copy the old code blindly.

First understand:

1. How the Product Definition acts as the production template.
2. How its data is copied into a Work Order.
3. How the relationships between Work Center, Process, BOM, Machine, and Labour are preserved.
4. How the Work Order becomes an independent production snapshot after creation.
5. How scheduling dates should later be calculated.

---

# 1. Work Order Creation Trigger

During **Work Order Entry**, the user selects a `ProductCode`.

Example:

```text
ProductCode = FG001
```

The system must use the selected `ProductCode` to locate the corresponding Product Definition.

Conceptually:

```text
Work Order Entry
      |
      v
Select ProductCode
      |
      v
Find Product Definition
      |
      +---- Not Found
      |       -> Block creation / show validation message
      |
      +---- Found
              |
              v
      Load complete Product Definition structure
              |
              v
      Copy Product Definition into Work Order tables
              |
              v
      Calculate Work Order scheduling/timing
```

---

# 2. Product Definition to Work Order Table Mapping

When a Product Definition is found, the system copies the relevant records into the corresponding Work Order / Production Schedule tables.

| Product Definition Table | Work Order Table       |
| ------------------------ | ---------------------- |
| `[dbo].[PrDefMas]`       | `[dbo].[PrSchMas]`     |
| `[dbo].[PrDefWCenter]`   | `[dbo].[PrSchWCenter]` |
| `[dbo].[PrDefProcess]`   | `[dbo].[PrSchProcess]` |
| `[dbo].[PrDefBOM]`       | `[dbo].[PrSchBOM]`     |
| `[dbo].[PrDefMachine]`   | `[dbo].[PrSchMachine]` |
| `[dbo].[PrDefLabour]`    | `[dbo].[PrSchLabour]`  |

The Work Order tables should therefore represent a **snapshot of the Product Definition at the time the Work Order is created**.

---

# 3. Important Snapshot Principle

The Product Definition is the master/template.

The Work Order is a transactional production document.

After the Product Definition has been copied into the Work Order, the Work Order should normally become independent from later changes to the Product Definition.

Example:

```text
01-Oct
Product Definition FG001
BOM:
- RM001 Qty 2
- RM002 Qty 1

02-Oct
WO0001 created
WO0001 receives:
- RM001 Qty 2
- RM002 Qty 1

05-Oct
Product Definition FG001 changed:
- RM001 Qty 2.5
- RM003 Qty 1
```

Existing:

```text
WO0001
```

should normally remain based on the definition that existed on `02-Oct`.

A new Work Order created after `05-Oct` should use the revised definition.

This is important for:

* production history;
* costing;
* material traceability;
* audit;
* variance analysis;
* reproducibility of old Work Orders.

Please verify whether the existing old ERP follows this behavior.

---

# 4. Hierarchical Structure That Must Be Preserved

The copied records are not six unrelated tables.

They form one production structure.

Conceptually:

```text
PrDefMas
   |
   +-- PrDefWCenter
          |
          +-- PrDefProcess
          |      |
          |      +-- PrDefMachine
          |      |
          |      +-- PrDefLabour
          |
          +-- PrDefBOM
```

When copied into the Work Order:

```text
PrSchMas
   |
   +-- PrSchWCenter
          |
          +-- PrSchProcess
          |      |
          |      +-- PrSchMachine
          |      |
          |      +-- PrSchLabour
          |
          +-- PrSchBOM
```

All parent-child relationships must remain correct after copying.

Do not merely copy records based only on `ProductCode`.

Investigate how the old system links:

* Product Definition;
* Work Center;
* Process;
* BOM;
* Machine;
* Labour.

Identify all important:

* primary keys;
* foreign keys;
* sequence numbers;
* Work Center references;
* Process references;
* machine references;
* BOM references;
* labour references.

---

# 5. Expected Work Order Creation Flow

The expected high-level process is approximately:

```text
1. User creates new Work Order.

2. User selects ProductCode.

3. System retrieves PrDefMas.

4. System retrieves all related:
   - PrDefWCenter
   - PrDefProcess
   - PrDefBOM
   - PrDefMachine
   - PrDefLabour

5. Validate Product Definition.

6. Create PrSchMas.

7. Copy Work Centers:
   PrDefWCenter
       ->
   PrSchWCenter

8. Copy Processes:
   PrDefProcess
       ->
   PrSchProcess

9. Copy BOM:
   PrDefBOM
       ->
   PrSchBOM

10. Copy Machines:
    PrDefMachine
        ->
    PrSchMachine

11. Copy Labour:
    PrDefLabour
        ->
    PrSchLabour

12. Maintain all new Work Order parent/child IDs and relationships.

13. Apply Work Order quantity to quantities or requirements that need scaling.

14. Calculate production scheduling dates.

15. Save the complete Work Order atomically.
```

---

# 6. Work Order Quantity

Please investigate how the old ERP uses the Work Order production quantity.

For example:

```text
Product Definition standard quantity = 1 FG

BOM:
RM001 = 2
RM002 = 0.5

Work Order Qty = 100
```

Expected requirement may become:

```text
RM001 = 200
RM002 = 50
```

However, do not assume every field should simply be multiplied by Work Order Qty.

Determine whether the Product Definition supports concepts such as:

* standard production quantity;
* batch size;
* yield;
* scrap/wastage;
* pack size;
* BOM quantity per batch;
* BOM quantity per finished unit;
* fixed quantity;
* variable quantity.

Document the exact scaling rule.

---

# 7. Scheduling Date Fields

When the Work Order is first generated, several production scheduling dates must eventually be calculated.

Relevant fields include:

## Machine

```sql
[dbo].[PrSchMachine].[StartDate]
[dbo].[PrSchMachine].[CompleteDate]
```

## Process

```sql
[dbo].[PrSchProcess].[StartDate]
[dbo].[PrSchProcess].[EndDate]
```

## Work Center

```sql
[dbo].[PrSchWCenter].[StartDate]
[dbo].[PrSchWCenter].[CompleteDate]
```

## Work Order Header

```sql
[dbo].[PrSchMas].[StartDate]
[dbo].[PrSchMas].[CompletedDate]
```

These dates are used to determine:

> How long the Work Order requires to produce the required Finished Goods quantity.

---

# 8. Do Not Treat the Date Fields as Simple Copy Fields

These scheduling dates should **not simply be copied from Product Definition**.

They are calculated based on production rules.

The calculation logic will be studied separately.

Likely inputs may include:

```text
Work Order Qty
       |
       v
Process / Work Center sequence
       |
       v
Machine cycle time
       |
       +-- Setup time
       +-- Run time
       +-- Queue time
       +-- Transfer time
       +-- Other production time
       |
       v
Machine Start / Complete
       |
       v
Process Start / End
       |
       v
Work Center Start / Complete
       |
       v
Work Order Start / Completed
```

Do not invent the formulas.

Locate and document the actual formula from the old ERP first.

---

# 9. Sequence and Parallel Production Must Be Preserved

My Product Definition supports production sequencing.

For example:

```text
FG001

Work Center WC1 - Sequence 1
Work Center WC2 - Sequence 2
Work Center WC3 - Sequence 3
```

This means:

```text
WC1
 |
 v
WC2
 |
 v
WC3
```

WC2 normally starts after WC1 is completed.

However, when Work Centers have the same sequence:

```text
WC1 - Sequence 1
WC2 - Sequence 1
WC3 - Sequence 2
```

the intention is approximately:

```text
       +--> WC1 --+
Start--|          |--> WC3
       +--> WC2 --+
```

WC1 and WC2 may operate in parallel.

WC3 starts only when its required preceding work is completed.

The same concept may also exist at Process level.

Please study the old implementation rather than assuming the exact dependency rule.

---

# 10. Final Process and Work Center Output

The Product Definition can also define an intermediate or final output.

Example:

```text
FG001

WC1
Output = WIP001

WC2
Output = WIP002

WC3
Input:
- WIP001
- WIP002

Output = FG001
```

Therefore, when creating the Work Order, ensure that the copied structure retains enough information to understand:

* Work Center output item;
* WIP item;
* Finished Goods item;
* BOM dependencies;
* Process sequence;
* final process;
* upstream/downstream dependency.

---

# 11. Validation Before Work Order Creation

Please identify and recommend validation rules.

At minimum investigate:

```text
Product Definition exists
Product Definition is active
At least one Work Center exists
Process sequence is valid
Work Center sequence is valid
Final process is defined correctly
BOM items exist
Machine references are valid
Labour references are valid
Output/WIP items exist
UOM is valid
No broken parent-child references exist
```

Also detect impossible production definitions such as:

```text
WC1 depends on WC2
WC2 depends on WC1
```

or other circular dependencies.

---

# 12. Transaction Safety

Creation of the Work Order should be atomic.

For example:

```text
BEGIN TRANSACTION

Create PrSchMas
Create PrSchWCenter
Create PrSchProcess
Create PrSchBOM
Create PrSchMachine
Create PrSchLabour

Calculate / initialize required values

COMMIT
```

If anything fails:

```text
ROLLBACK
```

The system must not leave an incomplete Work Order such as:

```text
PrSchMas exists
PrSchWCenter exists
PrSchProcess missing
PrSchBOM partially inserted
```

---

# 13. Preserve Source Definition Information

Please assess whether the new ERP should store source information such as:

```text
ProductDefinitionId
ProductDefinitionVersion
ProductDefinitionRevision
CopiedDateTime
```

on the Work Order.

This would allow the system to identify:

> Which Product Definition / revision was used when this Work Order was created?

If the current database does not have versioning, recommend a practical design.

---

# 14. Do Not Automatically Refresh Existing Work Orders

After the Work Order has been created, changing the Product Definition should not silently rewrite the Work Order.

If a feature such as:

```text
Refresh from Product Definition
```

is considered, it must be an explicit controlled operation.

Please analyze carefully what should happen if:

* Work Order is still Draft;
* materials have already been reserved;
* materials have already been issued;
* production has started;
* a Process is completed;
* WIP exists;
* Finished Goods have been received;
* accounting/costing transactions exist.

The safest rule may be:

```text
Draft WO
    -> refresh may be allowed with validation.

Released / In Progress WO
    -> definition refresh should normally be prohibited or tightly controlled.

Completed WO
    -> never refresh.
```

Review this against the old ERP behavior.

---

# 15. Separate Definition Quantities from Actual Production

When copying the Product Definition, distinguish between:

```text
Standard / Planned
```

and:

```text
Actual
```

Example:

```text
Standard Material Qty = 100 KG
Actual Issued Qty     = 103 KG

Standard Machine Time = 5 Hours
Actual Machine Time   = 5.8 Hours

Standard Labour       = 10 Hours
Actual Labour         = 12 Hours
```

The Product Definition supplies the standard/planned values.

Actual values should come from production execution.

This is important later for:

* material variance;
* machine efficiency;
* labour efficiency;
* production costing;
* yield;
* scrap analysis.

---

# 16. Required Analysis of the Old ERP

Please inspect the existing ASP.NET WebForms production system and determine exactly:

### A. Work Order Entry

Find:

* Work Order entry page;
* ProductCode selection event;
* Save / Create logic;
* services/business classes involved;
* stored procedures involved;
* SQL statements involved.

### B. Product Definition Loading

Determine:

* how `PrDefMas` is located;
* how child records are loaded;
* what keys connect the tables;
* whether data is loaded recursively or by queries.

### C. Copy Logic

Determine exactly how these mappings work:

```text
PrDefMas      -> PrSchMas
PrDefWCenter  -> PrSchWCenter
PrDefProcess  -> PrSchProcess
PrDefBOM      -> PrSchBOM
PrDefMachine  -> PrSchMachine
PrDefLabour   -> PrSchLabour
```

For every field, classify it as:

```text
COPY
CALCULATE
GENERATE
DEFAULT
DO NOT COPY
```

Produce a mapping table such as:

| Source Table | Source Field | Target Table | Target Field | Rule      |
| ------------ | ------------ | ------------ | ------------ | --------- |
| PrDefMas     | ProdCode     | PrSchMas     | ProdCode     | COPY      |
| PrDefBOM     | StdQty       | PrSchBOM     | RequiredQty  | CALCULATE |
| PrDefMachine | CycleTime    | PrSchMachine | CycleTime    | COPY      |
| N/A          | N/A          | PrSchMas     | WorkOrderNo  | GENERATE  |
| N/A          | N/A          | PrSchMas     | Status       | DEFAULT   |

Do this for all important fields.

---

# 17. Analyze Scheduling Logic Separately

Locate all old ERP logic responsible for calculating:

```text
PrSchMachine.StartDate
PrSchMachine.CompleteDate

PrSchProcess.StartDate
PrSchProcess.EndDate

PrSchWCenter.StartDate
PrSchWCenter.CompleteDate

PrSchMas.StartDate
PrSchMas.CompletedDate
```

Document:

* formulas;
* execution sequence;
* dependencies;
* quantity calculations;
* machine timing;
* process timing;
* Work Center timing;
* parallel processing;
* sequential processing;
* setup time;
* cycle time;
* labour impact;
* working calendar;
* working hours;
* breaks;
* holidays;
* machine capacity;
* rounding rules.

If some of these features do not exist in the old ERP, state that clearly.

Do not invent them as if they already exist.

Instead, classify recommendations as:

```text
OLD ERP BEHAVIOR
NEW ERP ENHANCEMENT
```

---

# 18. Proposed New ERP Architecture

After understanding the old implementation, recommend how to implement the same concept cleanly in the new Blazor ERP.

Prefer separating responsibilities such as:

```text
WorkOrderCreationService
        |
        +-- ProductDefinitionLoader
        |
        +-- WorkOrderSnapshotBuilder
        |
        +-- WorkOrderQuantityCalculator
        |
        +-- ProductionScheduleCalculator
        |
        +-- WorkOrderValidator
```

Example flow:

```text
CreateWorkOrderAsync()
      |
      +-- Validate Product
      |
      +-- Load Product Definition
      |
      +-- Validate Definition
      |
      +-- Create Snapshot
      |
      +-- Scale Quantities
      |
      +-- Build Relationships
      |
      +-- Calculate Schedule
      |
      +-- Save Transaction
```

Do not force this exact architecture if the current project already has a better established pattern.

Follow the existing project architecture where practical.

---

# 19. Idempotency / Duplicate Protection

Investigate how the system prevents the Product Definition from accidentally being copied twice.

For example, if the user:

```text
Select FG001
Save
Edit Work Order
Save again
```

the second save must not create duplicate:

```text
Work Centers
Processes
BOM rows
Machines
Labour rows
```

Determine the correct behavior.

---

# 20. Expected Deliverable

Before implementing anything, produce a detailed analysis and implementation plan containing:

## Section 1 — Existing Old ERP Behavior

Document the exact current workflow.

## Section 2 — Database Relationships

Show the relationships among all `PrDef*` and `PrSch*` tables.

## Section 3 — Field Mapping

Complete source-to-target mapping.

## Section 4 — Quantity Scaling Rules

Explain how Work Order Qty affects BOM, machine, labour, and process values.

## Section 5 — Scheduling Logic

Explain how all Start/Complete dates are calculated.

## Section 6 — Sequence and Parallel Processing

Explain how Work Center and Process sequences behave.

## Section 7 — WIP / Output Flow

Explain intermediate WIP and final FG production.

## Section 8 — Problems in the Old Design

Identify technical debt, unsafe assumptions, duplicated logic, or missing validation.

## Section 9 — Recommended Enhancements

Clearly separate enhancements from existing behavior.

## Section 10 — New ERP Design

Provide:

* entities;
* DB changes;
* services;
* methods;
* transaction boundaries;
* validation;
* scheduling design.

## Section 11 — Implementation Plan

Provide an ordered implementation plan that an AI coding agent can execute safely.

---

# Important Rules

1. **Do not start coding immediately.**
2. Study the existing implementation first.
3. Do not guess production rules when the old ERP already contains the answer.
4. Distinguish clearly between:

   * existing behavior;
   * inferred behavior;
   * recommended enhancement.
5. Preserve historical Work Order data.
6. Treat the Work Order as a snapshot of the Product Definition.
7. Preserve all parent-child relationships during copying.
8. Work Order creation must be transactional.
9. Scheduling calculations should be separated from simple data-copy logic.
10. Do not redesign existing production concepts unless there is a clear technical or business reason.
11. Any proposed enhancement must explain:

    * why it is required;
    * what problem it solves;
    * database impact;
    * compatibility impact;
    * migration impact.

The final goal is:

> Preserve the proven production concepts from the old ERP, while redesigning the implementation so the new ERP has a maintainable, auditable, extensible Work Order creation and scheduling architecture.

---

# VERIFIED ANALYSIS — Field Mapping (from WorkOrder.aspx.cs)

> Source of truth: `ProductionPlan/ProdPlan/WorkOrder.aspx.cs`  
> Key methods: `GetProDefMas`, `ProcessNewWithTime`, `AddMasterSchNew`, `AddUpdateMasterSch`, `Save` / `UpdateTableSave`  
> Persist adapters: `ERPClasses/Classes/CAdapter.cs` (`SetPrSchMas`, `SetPrSchWCenter`, `SetPrSchProcess`, `SetPrSchBom`, `SetPrSchMachine`, `SetPrSchLabour`)  
> Verified: 2026-03-29 — no assumed formulas.

## Rule legend

| Rule | Meaning |
| ---- | ------- |
| **COPY** | Value taken as-is from Product Definition (or UI entry that mirrors Def) |
| **CALCULATE** | Derived from Def fields + Work Order qty / batch / calendar |
| **GENERATE** | System-generated (numbering, identity keys) |
| **DEFAULT** | Fixed/system default at create time |
| **ENTRY** | User / Delivery Request / screen input (not from PrDef structure) |
| **DO NOT COPY** | Exists on Def but is not written to WO |
| **WO ONLY** | Exists only on Work Order / schedule tables |
| **EXECUTION** | Filled later by production posting (not at WO create) |

## Load filter at first create (`GetProDefMas`)

| Source | Filter applied when loading into session |
| ------ | ---------------------------------------- |
| `PrDefMas` | `ICode = ProductCode` |
| `PrDefWCenter` | All rows for `ProdCode` |
| `PrDefProcess` | All rows for `ProdCode` (joined to WC) |
| `PrDefBOM` | **`BomDefault = 1` only** |
| `PrDefMachine` | **`MacDefault = 1` only** |
| `PrDefLabour` | All rows for `ProdCode` |

Non-default BOM / machine rows on Product Definition are **not** auto-copied. After Process, the user may still change / add alternate BOM or machine on the Work Order grids (shortage / machine down).

---

## A. PrDefMas → PrSchMas

| Source Table | Source Field | Target Table | Target Field | Rule | Notes (verified) |
| ------------ | ------------ | ------------ | ------------ | ---- | ---------------- |
| N/A | N/A | PrSchMas | ScheCode | GENERATE | `"AUTO"` until Save → `getScheduleNo` / `UpdateScheduleNo` (prefix from Def.Prefix or `"SC"`) |
| N/A | N/A | PrSchMas | RelNo | DEFAULT | New = `1` (`txtRel`) |
| N/A | N/A | PrSchMas | Status | ENTRY / DEFAULT | UI `ddlStatus`, NewMode default `"RELEASE"` |
| N/A / SaDeliveryRequest | DRNo | PrSchMas | DRNo | ENTRY | From selected Delivery Request(s); empty if product-only path |
| PrDefMas | ICode | PrSchMas | ICode | COPY | Product / FG code |
| PrDefMas | IDesc | PrSchMas | IDesc | COPY | May be overwritten by UI |
| PrDefMas | StdBatchSize | PrSchMas | StdBatchSize | COPY | Shown as `txtSize`; used in qty scaling |
| N/A | N/A | PrSchMas | ScheQty | ENTRY | Work Order qty (`txtSchQty`); from DR outstanding when DR path |
| PrDefMas | StdUOM | PrSchMas | StdUOM | COPY | Validated vs `IvMas.StdUOM` before Process |
| N/A | N/A | PrSchMas | Active | DEFAULT | `1` |
| N/A | N/A | PrSchMas | StartDate | CALCULATE | After Process: Min(`PrSchWCenter.StartDate`); also UI `txtStartDate` |
| N/A | N/A | PrSchMas | CompletedDate | CALCULATE | After Process: Max(`PrSchWCenter.CompleteDate`); also UI `txtCompDate` |
| N/A | N/A | PrSchMas | TransactionDate | ENTRY | UI `txtTransactionDate` (numbering year/month) |
| N/A | N/A | PrSchMas | Created | DEFAULT | `DateTime.Now` on new |
| N/A | N/A | PrSchMas | Updated | DEFAULT | `DateTime.Now` on Save |
| N/A | N/A | PrSchMas | UserID | DEFAULT | Login user on create |
| N/A | N/A | PrSchMas | UpdatedUID | DEFAULT | Login user on Save |
| N/A | N/A | PrSchMas | ConsignQty | ENTRY | UI / DR |
| N/A | N/A | PrSchMas | DesireQty | ENTRY | UI |
| N/A | N/A | PrSchMas | DeliveryQty | ENTRY | UI / DR outstanding |
| PrDefMas | Remark | PrSchMas | Remarks | COPY / ENTRY | Copied from Def unless UI takes DR remark (`chkProdDefRmrk`) |
| N/A | N/A | PrSchMas | StartFromStartDate | ENTRY | UI `chkStartfr` — forward vs backward schedule |
| N/A | N/A | PrSchMas | FinalIssue | EXECUTION / DEFAULT | Not set in create Process path; used later by issue posting |
| N/A | N/A | PrSchMas | Release | DEFAULT | Adapter supports field; create path sets RelNo primarily |
| PrDefMas | Prefix | N/A (session / txtPrefix) | — | COPY (indirect) | Used only for WO numbering prefix, not stored as PrSchMas column |
| PrDefMas | Active | — | — | DO NOT COPY | Def Active not written to PrSchMas.Active (WO sets Active=1 itself) |
| PrDefMas | TotalTime | — | — | DO NOT COPY | Not used in WO create |
| PrDefMas | ActPrdCode | — | — | DO NOT COPY | Not mapped in WorkOrder create |
| PrDefMas | DRNo | — | — | DO NOT COPY | Def DRNo not used; WO DRNo comes from entry |
| PrDefMas | CompCode / BranchCode / LocCode | — | — | DO NOT COPY | Multi-company columns on Def children; not copied into PrSch* create |
| PrDefMas | Created / Updated / UserID / UpdatedUID | — | — | DO NOT COPY | WO uses its own audit fields |

---

## B. PrDefWCenter → PrSchWCenter

| Source Table | Source Field | Target Table | Target Field | Rule | Notes (verified) |
| ------------ | ------------ | ------------ | ------------ | ---- | ---------------- |
| N/A | N/A | PrSchWCenter | ScheCode | GENERATE | Same as header (`AUTO` → assigned number) |
| N/A | N/A | PrSchWCenter | RelNo | DEFAULT | Same as header |
| PrDefWCenter | ProdCode | PrSchWCenter | ProdCode | COPY | |
| PrDefWCenter | WCCode | PrSchWCenter | WCCode | COPY | PK part |
| PrDefWCenter | ICode | PrSchWCenter | ICode | COPY | WC output item (WIP/FG); children use this as `WCICode` |
| PrDefWCenter | IDesc | PrSchWCenter | IDesc | COPY | |
| PrDefWCenter | Class | PrSchWCenter | Class | COPY | |
| PrDefWCenter | SeqNo | PrSchWCenter | SeqNo | COPY | Same SeqNo = parallel WC scheduling group |
| PrDefWCenter | StdPackSize | PrSchWCenter | StdPackSize | CALCULATE | `Def.StdPackSize / PrDefMas.StdBatchSize` |
| PrDefWCenter | StdUOM | PrSchWCenter | StdUOM | COPY | |
| N/A | N/A | PrSchWCenter | ScheQty | CALCULATE | `txtSchQty * PrSchWCenter.StdPackSize` (after ratio above) |
| N/A | N/A | PrSchWCenter | StartDate | CALCULATE | From default machines under this WC (`MacDefault=1`) |
| N/A | N/A | PrSchWCenter | CompleteDate | CALCULATE | From default machines under this WC |
| N/A | N/A | PrSchWCenter | Completed | EXECUTION / DEFAULT | Not set true at create; production marks later |
| PrDefWCenter | SCode | — | — | DO NOT COPY | |
| PrDefWCenter | CompCode / BranchCode / LocCode | — | — | DO NOT COPY | |

**Qty identity check:**

```text
PrSchWCenter.StdPackSize = PrDefWCenter.StdPackSize / StdBatchSize
PrSchWCenter.ScheQty     = ScheQty * PrSchWCenter.StdPackSize
                         = ScheQty * PrDefWCenter.StdPackSize / StdBatchSize
```

---

## C. PrDefProcess → PrSchProcess

| Source Table | Source Field | Target Table | Target Field | Rule | Notes (verified) |
| ------------ | ------------ | ------------ | ------------ | ---- | ---------------- |
| N/A | N/A | PrSchProcess | ScheCode | GENERATE | |
| N/A | N/A | PrSchProcess | RelNo | DEFAULT | |
| PrDefProcess | ProdCode | PrSchProcess | ProdCode | COPY | |
| PrDefProcess | WCCode | PrSchProcess | WCCode | COPY | |
| PrDefProcess | WCICode | PrSchProcess | WCICode | COPY | = parent WC.ICode |
| PrDefProcess | ProcessCode | PrSchProcess | ProcessCode | COPY | |
| PrDefProcess | SeqNo | PrSchProcess | SeqNo | COPY | Same SeqNo = parallel processes within WC |
| PrDefProcess | SetupLostQty | PrSchProcess | SetupLostQty | COPY | Not scaled by ScheQty |
| PrDefProcess | OperationLostQty | PrSchProcess | OperationLostQty | COPY | Not scaled by ScheQty |
| PrDefProcess | FinalProcess | PrSchProcess | FinalProcess | COPY | Bit |
| PrDefProcess | Remark | PrSchProcess | Remark | COPY | Added 06-Jun-2023 |
| N/A | N/A | PrSchProcess | StartDate | CALCULATE | From `MacDefault=1` machines of this process |
| N/A | N/A | PrSchProcess | EndDate | CALCULATE | From `MacDefault=1` machines of this process |
| N/A | N/A | PrSchProcess | Completed | EXECUTION / DEFAULT | Not set at create |
| (session only) | WSeqNo | — | — | WO ONLY (transient) | Work Center SeqNo held in session for sort; **not** a PrSchProcess DB column |
| PrDefProcess | SCode / CompCode / BranchCode / LocCode | — | — | DO NOT COPY | |

Stock processes (`PrProcess.Stock = true` for WC+Process) may skip machine requirement / date fill.

---

## D. PrDefBOM → PrSchBOM

**First create load:** only `BomDefault = 1`.

| Source Table | Source Field | Target Table | Target Field | Rule | Notes (verified) |
| ------------ | ------------ | ------------ | ------------ | ---- | ---------------- |
| N/A | N/A | PrSchBOM | ScheCode | GENERATE | |
| N/A | N/A | PrSchBOM | RelNo | DEFAULT | |
| PrDefBOM | ProdCode | PrSchBOM | ProdCode | COPY | |
| PrDefBOM | WCCode | PrSchBOM | WCCode | COPY | |
| PrDefBOM | WCICode | PrSchBOM | WCICode | COPY | |
| PrDefBOM | ProcessCode | PrSchBOM | ProcessCode | COPY | |
| PrDefBOM | ICode | PrSchBOM | ICode | COPY | Component / RM / WIP item |
| PrDefBOM | IName | PrSchBOM | IName | COPY | |
| PrDefBOM | StdQty | PrSchBOM | StdQty | CALCULATE | See formula below (scaled requirement) |
| PrDefBOM | StdUOM | PrSchBOM | StdUOM | COPY | |
| PrDefBOM | Warehouse | PrSchBOM | Warehouse | COPY | |
| PrDefBOM | BomDefault | PrSchBOM | BomDefault | COPY | First create always from Def defaults (=1) |
| PrDefBOM | WIPBomDefault | PrSchBOM | WIPBomDefault | COPY | |
| PrDefBOM | tolerance | PrSchBOM | tolerance | COPY | % stored; **not** added into StdQty (removed 2-Sep-2022) |
| (session only) | WSeqNo / PSeqNo | — | — | WO ONLY (transient) | Sort helpers; not DB columns |
| PrDefBOM | CompCode / BranchCode / LocCode / levelID / UID | — | — | DO NOT COPY | |

**BOM quantity formula (verified):**

```text
PrSchBOM.StdQty = Round(
    PrDefBOM.StdQty / PrDefWCenter.StdPackSize * PrSchWCenter.ScheQty
  , 4)

# Equivalent:
PrSchBOM.StdQty = Round(PrDefBOM.StdQty * ScheQty / StdBatchSize, 4)
```

**After Process (WO override):** grid insert/update may add alternate materials, change `StdQty`, toggle `BomDefault`. Save requires: if any row has `BomDefault<>1`, same WC+WCICode+Process must still have ≥1 `BomDefault=1` (`IsBomDefaultisTick`).

---

## E. PrDefMachine → PrSchMachine

**First create load:** only `MacDefault = 1`.

| Source Table | Source Field | Target Table | Target Field | Rule | Notes (verified) |
| ------------ | ------------ | ------------ | ------------ | ---- | ---------------- |
| N/A | N/A | PrSchMachine | ScheCode | GENERATE | |
| N/A | N/A | PrSchMachine | RelNo | DEFAULT | |
| PrDefMachine | ProdCode | PrSchMachine | ProdCode | COPY | |
| PrDefMachine | WCCode | PrSchMachine | WCCode | COPY | |
| PrDefMachine | WCICode | PrSchMachine | WCICode | COPY | |
| PrDefMachine | ProcessCode | PrSchMachine | ProcessCode | COPY | |
| PrDefMachine | MachineCode | PrSchMachine | MachineCode | COPY | |
| PrDefMachine | MachineName | PrSchMachine | MachineName | COPY | |
| PrDefMachine | CycleTime | PrSchMachine | CycleTime | CALCULATE | Def stored in **seconds**; WO stores **minutes**. See formula |
| PrDefMachine | ConversionTime | PrSchMachine | ConversionTime | CALCULATE | Def seconds → WO minutes (`/ 60`) for default machines |
| PrDefMachine | StartupTime | PrSchMachine | StartupTime | CALCULATE | Def seconds → WO minutes (`/ 60`) |
| PrDefMachine | QueueTime | PrSchMachine | QueueTime | CALCULATE | Def seconds → WO minutes (`/ 60`) |
| PrDefMachine | SeqNo | PrSchMachine | SeqNo | COPY | Same SeqNo = parallel machines; qty split by count |
| PrDefMachine | MacDefault | PrSchMachine | MacDefault | COPY | Only defaults drive schedule dates |
| N/A | N/A | PrSchMachine | StartDate | CALCULATE | Via shift calendar / preventive / shift group (`MacDefault=1` only) |
| N/A | N/A | PrSchMachine | CompleteDate | CALCULATE | Same |
| PrDefMachine | CompCode / BranchCode / LocCode | — | — | DO NOT COPY | |

**Default machine CycleTime (minutes) — verified:**

```text
CycleTime_min = (
    CycleTime_sec / PrDefWCenter.StdPackSize
    * (PrSchWCenter.ScheQty / macCount_sameSeq)
) / 60
```

Scheduling minutes used before calendar walk also include Conversion + Startup + Queue (seconds), then convert to minutes for day walk.

**Non-default machine path** (`MacDefault=0` in `ProcessNewWithTime`): exists in code but on **first** create those rows are not in session (filtered at load). Becomes relevant after WO edits + re-process from SCH snapshot (`copyDatatoProductDefination`). Non-default insert does not set Start/Complete dates.

**Unit note:** `copyDatatoProductDefination` multiplies WO minutes × 60 when copying SCH → Def-shaped session for re-process (round-trip).

---

## F. PrDefLabour → PrSchLabour

| Source Table | Source Field | Target Table | Target Field | Rule | Notes (verified) |
| ------------ | ------------ | ------------ | ------------ | ---- | ---------------- |
| N/A | N/A | PrSchLabour | ScheCode | GENERATE | |
| N/A | N/A | PrSchLabour | RelNo | DEFAULT | |
| PrDefLabour | ProdCode | PrSchLabour | ProdCode | COPY | |
| PrDefLabour | WCCode | PrSchLabour | WCCode | COPY | |
| PrDefLabour | WCICode | PrSchLabour | WCICode | COPY | |
| PrDefLabour | ProcessCode | PrSchLabour | ProcessCode | COPY | |
| PrDefLabour | MachineCode | PrSchLabour | MachineCode | COPY | Linked under machine being processed |
| PrDefLabour | LabourCode | PrSchLabour | LabourCode | COPY | |
| PrDefLabour | LabourCost | PrSchLabour | LabourCost | CALCULATE | `Def.LabourCost * txtSchQty` |
| PrDefLabour | CompCode / BranchCode / LocCode | — | — | DO NOT COPY | |

Labour is copied for machines present in the process loop (default machines always; non-default only if those machine rows exist in session).

---

## G. Work Order–only / related tables at Save

| Target | Field / purpose | Rule | Notes |
| ------ | --------------- | ---- | ----- |
| PrSchMacMain | ScheCode, RelNo, ProdCode, MachineCode, Status, ScheQty, MacDefault, … | GENERATE / COPY from SCH machines | Built in `SetPrSchMacMainRecord` at Save |
| PrSchDR | ScheCode, RelNo, DeliveryNo, RevNo, ProdCode, OrderQty, … | ENTRY | Links WO to Delivery Request |
| PrScheduleNum / AdSmNumDate | Year, Month, Seq, Prefix | GENERATE | Numbering |
| (session) `*_DEL` tables | deleted SCH children | WO ONLY | Applied inside Save transaction before inserts |

---

## H. Quantity scaling summary (all entities)

| Entity | Scaled by ScheQty? | Formula / behavior |
| ------ | ------------------ | ------------------ |
| PrSchMas.ScheQty | ENTRY | User / DR |
| PrSchWCenter.ScheQty | Yes | `ScheQty * (Def.StdPackSize / StdBatchSize)` |
| PrSchBOM.StdQty | Yes | `Def.StdQty * ScheQty / StdBatchSize` (via pack-size intermediate) |
| PrSchMachine times | Yes | Cycle scaled by ScheQty & pack size; split by parallel machine count |
| PrSchLabour.LabourCost | Yes | `Def.LabourCost * ScheQty` |
| SetupLostQty / OperationLostQty | No | COPY as-is |
| tolerance | No (flag only) | COPY %; not applied into StdQty |

---

## I. Scheduling date calculation — CRITICAL (`ProcessNewWithTime`)

> **Source of truth:** `ProductionPlan/ProdPlan/WorkOrder.aspx.cs` → `ProcessNewWithTime()`  
> **UI page:** `ERP/ProdPlan/WorkOrder.aspx` (same code-behind `ProductionPlan.ProdPlan.WorkOrder`)  
> **Triggered by:** PROCESS button (`ProcessWorkOrder`) and RE-PROCESS button (`ReProcess`)  
> **Do not invent formulas.** Port this exact logic. Dates are **never copied** from Product Definition.

### I.1 Who calls it

| UI action | Callback | Path before `ProcessNewWithTime` |
| --------- | -------- | -------------------------------- |
| **PROCESS** (mode NEW) | `PROCESS:` → `ProcessWorkOrder()` | Uses session loaded from Product Definition |
| **PROCESS** (mode PROCESS, click again) | same | `GetProdDefProcessTable()` then `copyDatatoProductDefination()` (SCH → def session), then schedule from **current WO edits** |
| **RE-PROCESS** | `REPROCESS:` → `ReProcess()` | Reloads Product Definition via `GetProdDefProcessTable()`, then schedules fresh from **Def** |

Both buttons end in the **same** date engine: `ProcessNewWithTime()`.

Required UI inputs before Process/ReProcess:

* `txtSchQty` > 0  
* `txtSize` (StdBatchSize)  
* `txtCompDate` always required (validated even in forward mode)  
* If `chkStartfr` (START FROM START DATE) checked → `txtStartDate` required  

---

### I.2 Two modes: Forward vs Backward

Controlled by UI checkbox **`chkStartfr`** → saved as `PrSchMas.StartFromStartDate`.

| Mode | `chkStartfr` | Anchor variable `compDate` | Calendar walk | SeqNo sort order |
| ---- | ------------ | -------------------------- | ------------- | ---------------- |
| **Forward** | Checked | `txtStartDate.Date` | Add working minutes | WC / Process / Machine SeqNo **Ascending** |
| **Backward** | Unchecked | `txtCompDate.Date` | Subtract working minutes | WC / Process / Machine SeqNo **Descending** |

Initialisation (exact):

```text
compDate    = txtCompDate.Date
IF chkStartfr.Checked THEN
    compDate = txtStartDate.Date
END IF
macCompDate = compDate
maxD        = compDate
```

`compDate` is the **shared anchor** that advances after each Machine SeqNo group (see I.7).  
`macCompDate` is the **cursor** that moves day-by-day while consuming one machine’s required minutes.

---

### I.3 Prerequisites / hard stops

Before any date is written:

1. Every default machine for the product year must exist in `PrShiftCalendar` (`CheckMachineAvailability`).  
2. At least one Work Center in session.  
3. For each non-stock process: at least one `MacDefault=1` machine.  
4. For each machine being scheduled: `PrShiftCalendar` rows exist; every calendar day touched while walking must exist.  
5. Shift group loaded: `PrShiftGroupBL.GetShitGroupInfo()` → `TotalWorkingMinPerDay`, `TotalMinPerShift`, ordered `Shifts`.

Stock process exception: `PrProcess` where `Work_Centre=WC` and `Process_Cd=Process` and `Stock=true` may skip machine requirement / date fill for that process (`checkStockProcess`).

---

### I.4 Nested loop order (structure traversal)

```text
FOR EACH distinct WC.SeqNo   (Asc if forward, Desc if backward)
  FOR EACH Work Center with that SeqNo
    Create PrSchWCenter row (qty scaled; dates filled later)

    FOR EACH distinct Process.SeqNo in this WC  (Asc/Desc same rule)
      FOR EACH Process with that SeqNo
        Create PrSchProcess row (dates filled later)
        Create PrSchBOM rows (qty scaled; no dates)

        FOR EACH distinct Machine.SeqNo where MacDefault=1  (Asc/Desc same rule)
          FOR EACH default machine with that SeqNo   ← PARALLEL (same SeqNo)
            Compute required minutes
            Walk calendar → set Machine StartDate / CompleteDate
          END
          Advance shared anchor `compDate` to the SLOWEST machine of this SeqNo
        END

        Optionally add MacDefault=0 machines (NO Start/Complete dates)
        Roll Process StartDate / EndDate from MacDefault=1 machines
      END
    END

    Roll Work Center StartDate / CompleteDate from MacDefault=1 machines
  END
END

Header: StartDate = Min(all WC.StartDate)
        CompletedDate = Max(all WC.CompleteDate)
Update txtStartDate / txtCompDate
```

**Sequence vs parallel (critical):**

* **Same SeqNo** at a level → treated as parallel (share same anchor; qty may be split).  
* **Next SeqNo** → waits for the slowest parallel item of the previous SeqNo (anchor advances).

Applies at Machine SeqNo (explicitly), and Process/WC SeqNo via outer sort order + advancing `compDate`.

---

### I.5 Required machine minutes (before calendar)

Product Definition machine times are treated as **seconds**.  
Work Order stores Cycle/Conversion/Startup/Queue as **minutes**.

First, WC qty (already computed when WC row is built):

```text
PrSchWCenter.StdPackSize = PrDefWCenter.StdPackSize / StdBatchSize
PrSchWCenter.ScheQty     = ScheQty * PrSchWCenter.StdPackSize
```

For each **default** machine in a Machine SeqNo group (`macCount = number of MacDefault=1 machines with that SeqNo`):

```text
# Exact code (seconds), then convert to minutes for the walk:
minMac_seconds =
      (CycleTime_sec / PrDefWCenter.StdPackSize)
    * (PrSchWCenter.ScheQty / macCount)
    + ConversionTime_sec
    + StartupTime_sec
    + QueueTime_sec

minMac_minutes = Round(minMac_seconds / 60)     # Int64 after Math.Round
```

Values written to `PrSchMachine` (minutes):

```text
CycleTime      = (CycleTime_sec / StdPackSize * (ScheQty / macCount)) / 60
ConversionTime = ConversionTime_sec / 60
StartupTime    = StartupTime_sec / 60
QueueTime      = QueueTime_sec / 60
```

**Non-default machines (`MacDefault=0`):**

* Not loaded on first create from Def (filtered).  
* If present in session: CycleTime scaled **without** `/ macCount`; Conversion/Startup/Queue copied as-is (no `/60` in that branch).  
* **No StartDate / CompleteDate** assigned. They do **not** drive schedule.

---

### I.6 Calendar walk — exact per-machine algorithm

For one default machine, after `minMac_minutes` is known:

```text
macCompDate = compDate          # reset to current shared anchor (parallel machines share this)
Load PrShiftCalendar WHERE MachineCode = this machine
Load PrPreventive   WHERE Machine_Cd  = this machine
                      (column down_min = DATEDIFF(minute, Start_Tm, End_Tm))

isFirstday = true

WHILE minMac > 0:

  # Calendar day must exist
  IF no PrShiftCalendar row for date(macCompDate) THEN ERROR and abort

  IF Date_Cd = 'W' on that day:          # working day
      # Preventive downtime: ADD minutes back onto remaining work
      IF PrPreventive has Down_Dt = that day THEN
          minMac = minMac + down_min
      END IF

      TotalWorkingMinPerDay = shiftgroup.TotalWorkingMinPerDay
      IF isFirstday THEN
          isFirstday = false
          workingTime = GetFirstDayTotalDelayWorkingTime(shiftGrp, macCompDate)
          IF workingTime > 0 THEN
              TotalWorkingMinPerDay = workingTime   # remaining minutes from anchor clock time
          END IF
      END IF

      IF minMac < TotalWorkingMinPerDay:
          # Fits (partially) within this day → walk shifts
          IF forward (chkStartfr):
              Optionally reverse shift list order for forward placement
              FOR EACH shift in shiftgroup.Shifts:
                  IF remaining work fits in this shift:
                      CalTimeWithInDay(macCompDate, minMac, shift)  
                      # adds minutes; jumps over break windows Break_Tm1..5
                  ELSE:
                      # consume one shift worth, may roll to next day shift start
                      advance macCompDate by TotalMinPerShift (or to day end then next early start)
                      minMac -= shiftworkingMin
                  END IF
              END FOR
          ELSE (backward):
              FOR EACH shift:
                  IF remaining fits in shift:
                      macCompDate = macCompDate.AddMinutes(-minMac)
                      minMac adjusted
                  ELSE:
                      macCompDate = macCompDate.AddMinutes(-TotalMinPerShift)
                      minMac -= shiftworkingMin
                  END IF
              END FOR
          END IF

      ELSE:
          # Needs one or more full working days
          minMac = minMac - TotalWorkingMinPerDay
          macCompDate = same calendar day at first shift Start_Tm
          IF forward THEN macCompDate = macCompDate.AddDays(+1)
          ELSE           macCompDate = macCompDate.AddDays(-1)
          END IF
      END IF

  ELSE:   # non-working day (holiday / rest)
      macCompDate = first shift Start_Tm of that day
      IF forward THEN macCompDate.AddDays(+1) ELSE macCompDate.AddDays(-1)
  END IF

END WHILE

# Snap final cursor onto a working day if landed on non-working
WHILE macCompDate is not Date_Cd='W':
    IF forward THEN +1 day ELSE -1 day
END WHILE
```

#### First-day remaining capacity (`GetFirstDayTotalDelayWorkingTime` / `GetActualWorkingTime`)

On the first working day of a machine walk, available minutes are **not** always a full day. From the first shift of the group:

```text
workintime = End_Tm.TimeOfDay - startFrom.TimeOfDay   (minutes)
subtract break durations whose Break_To is still at/after startFrom.TimeOfDay
return workintime
```

So if the user anchors at mid-shift, only the **remaining** portion of that day is usable first.

#### In-day breaks (`CalTimeWithInDay`) — forward only when fitting in a shift

If adding `minMac` would cross a break start (`Break_Tm1From` … `Break_Tm5From`):

1. Advance `macCompDate` to the break start.  
2. Skip the break duration (`Break_To - Break_From`).  
3. Reduce remaining `minMac` by the work minutes already consumed before the break.  
4. Continue for remaining breaks / remaining minutes.

---

### I.7 Assign Machine Start / Complete (exact)

After the while-loop finishes for one machine:

| Mode | `PrSchMachine.StartDate` | `PrSchMachine.CompleteDate` |
| ---- | ------------------------ | -------------------------- |
| **Forward** (`chkStartfr`) | `compDate` (anchor **before** this machine’s walk) | `macCompDate` (cursor **after** walk) |
| **Backward** | `macCompDate` (cursor after walk) | `compDate` (anchor before walk) |

Then, after **all machines in the same Machine SeqNo** are written:

```text
Among those MacDefault=1 machines in this SeqNo:
  score = CycleTime + ConversionTime + StartupTime + QueueTime   (minutes on SCH row)

Pick the machine with the MAXIMUM score.
IF forward:
    compDate = that machine's CompleteDate     # next SeqNo starts when slowest finishes
ELSE:
    compDate = that machine's StartDate        # next SeqNo ends when slowest must start (backward)
END IF
```

This is how **parallel machines (same SeqNo)** share one start, and **sequential SeqNo** chains after the slowest.

---

### I.8 Roll-up: Process → Work Center → Header

#### Process (`PrSchProcess`) — from its `MacDefault=1` machines only

```text
# Backward default path builds maxD = Max(CompleteDate)
# Forward then rebuilds maxD = Max(StartDate)   [code overwrites]

IF forward:
    Process.StartDate = maxD          # Max of machine StartDates
    Process.EndDate   = macCompDate   # cursor after last machine SeqNo of this process
ELSE:
    Process.StartDate = macCompDate
    Process.EndDate   = maxD          # Max of machine CompleteDates
END IF
```

Stock process: dates may be left unset (no machine error).

#### Work Center (`PrSchWCenter`) — from all `MacDefault=1` machines under that WC

```text
# First maxD = Max(CompleteDate) of WC machines
# If forward: maxD is replaced with Min(StartDate) of WC machines  (Compute Min)

IF forward:
    WC.StartDate    = maxD            # Min machine StartDate
    WC.CompleteDate = macCompDate
ELSE:
    WC.StartDate    = macCompDate
    WC.CompleteDate = maxD            # Max machine CompleteDate
END IF
```

#### Work Order header (`PrSchMas` + UI)

After all WC rows:

```text
txtStartDate  = Min(PrSchWCenter.StartDate)
txtCompDate   = Max(PrSchWCenter.CompleteDate)

PrSchMas.StartDate     = txtStartDate
PrSchMas.CompletedDate = txtCompDate
PrSchMas.StartFromStartDate = chkStartfr
```

Via `AddMasterSchNew()` / later `AddUpdateMasterSch()` on Save.

---

### I.9 Roll-up diagram

```text
                    ┌── Mac A (SeqNo=1) ──┐
Anchor ─────────────┤                     ├──► advance anchor to SLOWEST
                    └── Mac B (SeqNo=1) ──┘
                              │
                    ┌── Mac C (SeqNo=2) ──► next SeqNo uses new anchor
                              │
                    Process Start / End
                              │
                    Work Center Start / Complete
                              │
                    Work Order Start / Completed
                    = Min(WC.Start), Max(WC.Complete)
```

---

### I.10 Worked example (forward — verified formulas)

Assumptions:

* `chkStartfr = true`, `txtStartDate = 2026-04-01 08:00`  
* `StdBatchSize = 100`, `ScheQty = 100`  
* One WC, one Process, one default machine, `StdPackSize = 100`  
* `CycleTime = 36000` sec (10 hours), Conversion=Startup=Queue=0  
* One shift, 480 working minutes/day, no breaks, no preventive  
* Anchor is already at shift start → first-day capacity = 480  

```text
WC.StdPackSize = 100/100 = 1
WC.ScheQty     = 100 * 1 = 100

minMac_seconds = (36000/100)* (100/1) + 0 = 36000
minMac_minutes = Round(36000/60) = 600

Day 1 (W): consume 480 → remaining 120 → jump to Day2 08:00
Day 2 (W): add 120 minutes → macCompDate = 2026-04-02 10:00

Machine.StartDate    = 2026-04-01 08:00   (anchor)
Machine.CompleteDate = 2026-04-02 10:00
Process / WC / Header follow the same range.
```

Backward example (same times): fix `txtCompDate = 2026-04-02 10:00`, walk earlier → `StartDate` becomes `2026-04-01 08:00`.

---

### I.11 Tables involved in scheduling

| Table / source | Role |
| -------------- | ---- |
| `PrDefMachine` / session | Cycle, Conversion, Startup, Queue (seconds); SeqNo; MacDefault |
| `PrDefWCenter` | StdPackSize, SeqNo |
| `PrDefMas` | StdBatchSize |
| UI | ScheQty, StartDate, CompDate, chkStartfr |
| `PrShiftGroup` + `PrShift` | Shifts, breaks, TotalWorkingMinPerDay, TotalMinPerShift |
| `PrShiftCalendar` | Per machine per day; `Date_Cd='W'` = working |
| `PrPreventive` | Extra downtime minutes on a day |
| `PrProcess.Stock` | Skip machine requirement for stock processes |

---

### I.12 What is NOT in the live path

* `WorkScheduleHelper.Process(...)` exists in comments (~line 3722) but is **commented out**. Live dates come only from the inline calendar walk above.  
* Non-default machines do **not** affect Start/Complete.  
* Labour does **not** affect dates.  
* BOM does **not** affect dates.  
* Tolerance does **not** affect dates.

---

### I.13 Porting rules (Blazor) — must preserve

1. Separate **required minutes** from **calendar placement**.  
2. Support **forward** and **backward** from one flag + one user anchor.  
3. Only **`MacDefault=1`** machines schedule.  
4. **Same Machine SeqNo** = parallel (shared anchor, qty split); next SeqNo waits for slowest.  
5. Honor **working calendar**, **shift minutes**, **breaks**, **preventive**, **first-day remaining capacity**.  
6. Final header = Min WC Start / Max WC Complete.  
7. Do not silently invent 24×7 continuous time unless product owners redesign the model.

---

## J. Snapshot / default / override behavior (verified)

| Event | Behavior |
| ----- | -------- |
| Select Product | Load Def → session (`BomDefault=1`, `MacDefault=1` only for BOM/Machine) |
| Process | Build SCH session snapshot + scale qty + calculate dates |
| Edit BOM/Machine on WO | Allowed after Process (alternate material / machine) |
| Save | Persist SCH; independent of later PrDef changes |
| ReProcess | Reloads **current** PrDef again and rebuilds (can wipe WO overrides) |
| Process again while mode=Process | Uses current SCH via `copyDatatoProductDefination`, then re-schedules |

---

## K. Primary keys to preserve when porting

| Table | PK |
| ----- | -- |
| PrDefMas | ICode |
| PrDefWCenter | ProdCode, WCCode, ICode |
| PrDefProcess | ProdCode, WCCode, WCICode, ProcessCode |
| PrDefBOM | ProdCode, WCCode, WCICode, ProcessCode, ICode |
| PrDefMachine | ProdCode, WCCode, WCICode, ProcessCode, MachineCode |
| PrDefLabour | ProdCode, WCCode, WCICode, ProcessCode, MachineCode, LabourCode |
| PrSchMas | ScheCode, RelNo |
| PrSchWCenter | ScheCode, RelNo, ProdCode, WCCode, ICode |
| PrSchProcess | ScheCode, RelNo, ProdCode, WCCode, WCICode, ProcessCode |
| PrSchBOM | ScheCode, RelNo, ProdCode, WCCode, WCICode, ProcessCode, ICode |
| PrSchMachine | ScheCode, RelNo, ProdCode, WCCode, WCICode, ProcessCode, MachineCode |
| PrSchLabour | ScheCode, RelNo, ProdCode, WCCode, WCICode, ProcessCode, MachineCode, LabourCode |

Parent link for WO children: `ScheCode + RelNo` → `PrSchMas`. Structure link: `WCCode + WCICode (+ ProcessCode)` mirrors Product Definition.
