# Deep Reverse-Engineering Study — ProductionPlan/ProdPlan/WorkOrder.aspx.cs

**Repository:** `mokth/ERPOrigin`  
**Primary file studied:** `ProductionPlan/ProdPlan/WorkOrder.aspx.cs`  
**Purpose:** Recover the original Work Order business logic before migrating/enhancing it in the new Blazor ERP.

---

## 1. Scope of Study

The following code paths and related classes were studied:

- `ProductionPlan/ProdPlan/WorkOrder.aspx.cs`
- `ProductionPlan/ProdPlan/WorkOrder.aspx`
- `ERPClasses/BL/PrShiftGroupBL.cs`
- `ERPClasses/BL/WorkScheduleHelper.cs`
- `ERPClasses/BL/ProdDefBL.cs`
- `ERPClasses/Classes/CFunction.cs`
- `ProductionPlan/Helper/GenerateWorkOrderHelper.cs`
- `ProductionPlan/Helper/PlanHelper.cs`
- Product-definition related machine/BOM UI and helper code
- Work Order persistence and schedule-number generation logic

The goal was to reconstruct:

- How a Work Order is created
- How Product Definition data is copied into Work Order tables
- How Work Center quantities are calculated
- How BOM quantities are calculated
- Machine cycle/setup/queue/startup time rules
- Seconds vs minutes handling
- Parallel vs sequential machine logic
- Forward scheduling from Start Date
- Backward scheduling from Required Completion Date
- Shift/calendar/break/maintenance handling
- How default BOM or Machine selections are replaced
- ReProcess / Normalize behavior
- Save/snapshot behavior
- Legacy implementation defects that should **not** be copied into the new ERP

---

# 2. Core Architecture

The old ERP uses a **Product Definition → Work Order Snapshot** model.

| Product Definition | Work Order Snapshot |
|---|---|
| `PrDefMas` | `PrSchMas` |
| `PrDefWCenter` | `PrSchWCenter` |
| `PrDefProcess` | `PrSchProcess` |
| `PrDefBOM` | `PrSchBOM` |
| `PrDefMachine` | `PrSchMachine` |
| `PrDefLabour` | `PrSchLabour` |

Other important tables:

| Table | Purpose |
|---|---|
| `PrSchMacMain` | Generated machine schedule summary / machine planning rows |
| `PrSchDR` | Delivery Request ↔ Work Order linkage |
| `PrShiftCalendar` | Machine work-day / off-day calendar |
| `PrShiftGroup` | Shift grouping |
| `PrShift` | Shift start/end time and breaks |
| `PrPreventive` | Machine preventive-maintenance downtime |
| `PrProcess.Stock` | Allows stock/WIP processes to have no machine |

The design intent is correct for ERP:

> Product Definition is the template.  
> Work Order gets its own independent copy/snapshot.

That means a Work Order may later use a different BOM, machine, timing, process configuration, etc. without changing the Product Definition.

---

# 3. New Work Order Initial State

For a normal manually created Work Order:

```text
Mode = NEW
Schedule = AUTO
RelNo = 1
Status = RELEASE
StartFromStartDate = true
StartDate = Today + first shift start time
```

The page loads the earliest configured shift start time and sets that as the default Work Order start time.

Example:

```text
Today = 30/09/2026
First shift starts = 08:00

Default StartDate = 30/09/2026 08:00
```

---

# 4. Delivery Request Work Order Behavior

When Work Order is created from Delivery Request:

```text
Product Code      <- Delivery Request Product
Delivery Qty      <- Outstanding Qty
Consignment Qty   <- Outstanding Consignment
Schedule Qty      <- Delivery Qty + Consignment Qty
Completed Date    <- Delivery Request Delivery Date
StartFromStartDate = false
```

This causes backward scheduling.

Therefore the system deliberately supports two scheduling modes:

```text
Manual Work Order:
Start Date ---------------------> Calculate Completion Date

Delivery Request Work Order:
Calculate Start Date <----------- Required Completion Date
```

This is an important original business rule.

---

# 5. Selecting Product / Loading Product Definition

Selecting a Product does **not** immediately write Work Order records into SQL.

The Product Definition is loaded into session/DataTable structures first.

The normal Product Definition load uses:

| Definition | Initially Loaded |
|---|---|
| Work Centers | All |
| Processes | All |
| BOM | Only `BomDefault = 1` |
| Machines | Only `MacDefault = 1` |
| Labour | All |

Therefore the default initial Work Order structure is:

```text
All Work Centers
All Processes
Default BOM rows
Default Machines
All related Labour
```

Alternative BOM or Machine definitions still remain selectable from popup/listing UI.

---

# 6. Clicking Process is the Main Conversion Point

The Process button is where Product Definition becomes calculated Work Order data.

High-level flow:

```text
Product Definition
      |
      v
ProcessWorkOrder()
      |
      v
Validate inputs
      |
      v
ProcessNewWithTime()
      |
      +--> PrSchWCenter
      +--> PrSchProcess
      +--> PrSchBOM
      +--> PrSchMachine
      +--> PrSchLabour
      |
      v
Calculate Start / End Date
      |
      v
Prepare PrSchMas
```

The generated records remain in session DataTables until Save.

---

# 7. Important Validation Before Processing

The old Work Order checks:

- Product/description exists
- Schedule Qty is entered
- Schedule Qty > 0
- Product Standard Batch Size exists
- Completed Date exists
- Completed Date is valid
- Work Order UOM matches Item Master StdUOM
- Product Definition BOM exists
- Normal non-stock processes must have a default machine
- Machine calendar must exist for dates being traversed

This validation protects Work Order generation from incomplete Product Definitions.

---

# 8. Work Center Quantity Calculation

For each `PrDefWCenter`:

```text
WorkOrderWC.StdPackSize
    =
DefinitionWC.StdPackSize
    /
Product.StdBatchSize
```

Then:

```text
WorkOrderWC.ScheQty
    =
WorkOrder.ScheduleQty
    *
WorkOrderWC.StdPackSize
```

Formula:

\[
WCScale =
\frac{WCStdPackSize}{ProductStdBatchSize}
\]

\[
WCScheduleQty =
WOQty \times WCScale
\]

Example:

```text
Product StdBatchSize = 5
Work Center StdPackSize = 2
WO Qty = 100

WC Scale = 2 / 5
         = 0.4

WC Schedule Qty = 100 x 0.4
                = 40
```

Important:

> `PrSchWCenter.StdPackSize` is a normalized scale, not necessarily the same raw value stored in `PrDefWCenter.StdPackSize`.

---

# 9. Exact BOM Calculation

Inside `ProcessNewWithTime()`:

```text
WorkOrderBOM.StdQty
    =
DefinitionBOM.StdQty
    /
DefinitionWC.StdPackSize
    *
WorkOrderWC.ScheQty
```

Formula:

\[
BOMRequiredQty =
\frac{DefBOMQty}{DefWCStdPackSize}
\times WCScheduleQty
\]

Substituting the Work Center formula:

\[
BOMRequiredQty =
DefBOMQty
\times
\frac{WOQty}{ProductStdBatchSize}
\]

So the effective business rule is:

> BOM StdQty is defined against the Product Standard Batch Size.

Example:

```text
Product Standard Batch = 5 PCS
BOM RM001 = 2 KG
WO Qty = 100 PCS

No. of standard batches = 100 / 5
                        = 20

Required RM001 = 2 x 20
               = 40 KG
```

The result is rounded to 4 decimal places.

---

# 10. BOM Tolerance

The old code still calculates:

```text
ToleranceQty = RequiredQty x Tolerance%
```

but the tolerance is **not added** into the generated Work Order quantity.

The code effectively does:

```text
PrSchBOM.StdQty = calculated standard quantity
```

not:

```text
PrSchBOM.StdQty = standard quantity + tolerance quantity
```

A comment states this extra tolerance quantity was removed by request on 2 September 2022.

Therefore:

```text
Tolerance %
    -> copied into PrSchBOM
    -> NOT automatically added into StdQty
```

If the new ERP wants scrap/wastage allowance, that should be implemented deliberately as a new rule rather than assumed to exist in the old Work Order generation.

---

# 11. SetupLostQty / OperationLostQty

These values are copied:

```text
PrDefProcess.SetupLostQty
    -> PrSchProcess.SetupLostQty

PrDefProcess.OperationLostQty
    -> PrSchProcess.OperationLostQty
```

`FinalProcess` is also copied.

However, the Work Order generation routine does **not** use these values to increase:

- BOM Qty
- Machine Qty
- Machine duration

Therefore they are copied metadata/business values but are not part of the generation formula here.

---

# 12. Machine Time Units — Critical Finding

## Product Definition Machine Times Are in SECONDS

Independent source evidence confirms:

```text
PrDefMachine.CycleTime       = seconds
PrDefMachine.ConversionTime  = seconds
PrDefMachine.StartupTime     = seconds
PrDefMachine.QueueTime       = seconds
```

Examples in the old UI explicitly say:

```text
cycle time (SEC)
CYCLE TIME (SECS)
```

Other code converts minute input to seconds:

```text
seconds = minutes x 60
```

and labour costing divides CycleTime by 3600 to convert seconds to hours.

Therefore this is high-confidence.

---

# 13. Work Order Machine Times Are Stored/Displayed in MINUTES

During normal Work Order generation:

```text
PrSchMachine.CycleTime
    = calculated cycle seconds / 60

PrSchMachine.ConversionTime
    = definition conversion seconds / 60

PrSchMachine.StartupTime
    = definition startup seconds / 60

PrSchMachine.QueueTime
    = definition queue seconds / 60
```

The Work Order UI also displays:

```text
CYCLE TIME (MIN)
```

Other helper code explicitly comments:

```text
PrSchMachine.CycleTime is in Mins
```

Therefore the legacy architecture mixes units:

| Layer | Unit |
|---|---|
| `PrDefMachine` | seconds |
| Scheduler raw calculation | seconds |
| `PrSchMachine` | minutes |
| Calendar traversal | minutes |

This mixed-unit architecture is one of the major sources of risk.

For the new ERP, use one canonical internal unit.

Recommended:

```text
CycleTimeSeconds
ConversionTimeSeconds
StartupTimeSeconds
QueueTimeSeconds
```

or use a duration type/`TimeSpan`.

---

# 14. Exact Machine Duration Formula

For one Process + one Machine SeqNo group:

The scheduler finds:

```text
same Work Center
same WC Product
same Process
same Machine SeqNo
MacDefault = 1
```

Assume there are `N` default machines within that same Machine SeqNo.

For each machine:

\[
MachineSeconds =
\frac{CycleTime}{WCStdPackSize}
\times
\frac{WCQty}{N}
+
ConversionTime
+
StartupTime
+
QueueTime
\]

Since:

\[
WCQty =
WOQty
\times
\frac{WCStdPackSize}{ProductStdBatchSize}
\]

the formula simplifies to:

\[
MachineSeconds =
CycleTime
\times
\frac{WOQty}
{ProductStdBatchSize \times N}
+
Conversion
+
Startup
+
Queue
\]

Important rule:

> Multiple default machines in the same Machine SeqNo share the production quantity equally.

But fixed overheads are **not shared**:

- Conversion
- Startup
- Queue

Each participating machine gets the full fixed overhead.

---

# 15. Example Machine Calculation

Example:

```text
Product Std Batch = 5 PCS
WO Qty = 100 PCS

Machine CycleTime = 120 sec
ConversionTime = 600 sec
StartupTime = 300 sec
QueueTime = 120 sec
```

One machine:

```text
Production batches = 100 / 5
                   = 20

Cycle seconds = 120 x 20
              = 2,400

Fixed seconds = 600 + 300 + 120
              = 1,020

Total = 3,420 sec
      = 57 min
```

Two machines in the same Machine SeqNo:

```text
Each machine receives 50% of production quantity.

Cycle = 120 x 20 / 2
      = 1,200 sec

Fixed = 1,020 sec

Each machine total = 2,220 sec
                   = 37 min
```

Both machines start from the same incoming scheduling point.

---

# 16. Machine SeqNo = Parallel and Sequential Behavior

Within a Process:

```text
Machine Seq 1
    Machine A
    Machine B
```

Machine A and Machine B are treated as parallel capacity.

Conceptually:

```text
             +--> Machine A ----+
Process ----|                   |----> next machine sequence
             +--> Machine B --------+
```

The code calculates both from the same current `compDate`.

It then advances to the completion/start boundary of the longest-running machine in that machine-sequence group.

Therefore:

```text
Same Machine SeqNo
    = parallel

Next Machine SeqNo
    = starts after previous machine sequence group completes
```

This is one of the most important original production rules to retain.

---

# 17. Work Center / Process Sequence Intent

The overall design clearly intends the same principle at three levels:

```text
Work Center Seq
    |
    +--> Process Seq
            |
            +--> Machine Seq
```

The related `WorkScheduleHelper` comments explicitly say:

```text
Centers may run concurrently
One Center may have Processes run concurrently
One Process may have Machines run concurrently
```

Therefore the intended semantic is:

```text
Same SeqNo
    = parallel

Different ascending SeqNo
    = sequential / dependency barrier
```

However, the actual `WorkOrder.aspx.cs` inline implementation does not reliably achieve that for Work Centers and Processes.

For the new ERP, define the rule explicitly and implement it correctly rather than copying the nested legacy loops literally.

---

# 18. Forward Scheduling

Forward scheduling is used when:

```text
StartFromStartDate = true
```

Initial anchor:

```text
compDate = WorkOrder.StartDate
```

Typical sequence direction:

```text
Work Center Seq ASC
Process Seq ASC
Machine Seq ASC
```

For each scheduled default machine:

```text
Machine.StartDate = current compDate
Machine.CompleteDate = calculated future date
```

For parallel machines within one Machine SeqNo:

```text
all use same incoming compDate
```

Then the scheduler selects the effective boundary from the machine that finishes last and uses that for the next Machine SeqNo.

Conceptually:

```text
Assigned Start
     |
     v
WC Seq 1
     |
     v
Process Seq 1
     |
     +--> Machine Seq 1 parallel group
     |
     +--> Machine Seq 2
     |
     v
Process Seq 2
     |
     v
WC Seq 2
     |
     v
Calculated Completion
```

Final Work Order date is derived from Work Center dates.

---

# 19. Backward Scheduling

Backward scheduling is used when:

```text
StartFromStartDate = false
```

Initial anchor:

```text
compDate = required Work Order CompletedDate
```

Sequence direction reverses:

```text
Work Center Seq DESC
Process Seq DESC
Machine Seq DESC
```

For each default machine:

```text
Machine.CompleteDate = current compDate
Machine.StartDate = calculated backwards date
```

The previous sequence is then anchored from the earliest required start among the relevant parallel machines.

This is how Delivery Request due-date planning works.

Conceptually:

```text
Calculated Start
       ^
       |
WC Seq 1
       ^
       |
Process Seq 1
       ^
       |
Machine Seq 1
       ^
       |
Required Completion
```

---

# 20. Machine Calendar

The scheduler requires `PrShiftCalendar`.

For each machine/date:

```text
Date_Cd = 'W'
```

means working day.

Non-working days are skipped.

If the scheduler reaches a date that has no machine-calendar record at all, processing is stopped with an error such as:

```text
The Date xx-xx-xxxx is not yet defined in Calendar for Machine XXXXX
```

Therefore Work Order scheduling is not simple elapsed-time arithmetic.

It is:

```text
Start/Completion Anchor
+ valid working time
+ breaks
+ skipped off days
+ preventive maintenance
= calculated schedule
```

---

# 21. Shift Groups and Working Minutes

`PrShiftGroupBL` calculates:

```text
TotalWorkingMinPerDay
TotalBreakMinPerDay
TotalShift
TotalMinPerShift
```

For each shift:

```text
Working minutes
    =
Shift duration
    -
Break duration
```

It supports up to 5 break periods.

It also handles overnight shifts such as:

```text
23:00 -> 07:00 next day
```

by moving the end time to the following date.

This is important for 24-hour factories.

---

# 22. Preventive Maintenance

For each machine, `PrPreventive` is loaded.

Downtime minutes are calculated:

```text
down_min = DATEDIFF(minute, Start_Tm, End_Tm)
```

If a machine is scheduled on that downtime date:

```text
required scheduling minutes
    =
required production minutes
    +
preventive downtime minutes
```

Example:

```text
Required machine productive duration = 300 min
Maintenance downtime = 60 min

Scheduling must consume approximately 360 elapsed working-calendar minutes
```

This rule should remain in the new scheduling engine.

---

# 23. Shift Break Handling

The legacy inline Work Order scheduler contains custom logic to move through breaks.

`PrShiftGroupBL` also creates working slots such as:

```text
08:00 - 10:00
10:15 - 12:00
13:00 - 17:00
```

A cleaner helper (`WorkScheduleHelper`) traverses these working slots more directly.

However:

> `WorkOrder.aspx.cs` does not call `WorkScheduleHelper.Process()` for its normal Work Order generation.

Therefore the helper must not be assumed to be the exact behavior of the old Work Order page.

---

# 24. Two Different Scheduling Engines Exist

The repository contains:

1. Large inline scheduler inside `WorkOrder.aspx.cs`
2. Cleaner `ERPClasses/BL/WorkScheduleHelper.cs`

They are related but not identical.

Important differences exist.

For example:

The Work Order inline scheduler uses:

```text
Cycle
+ Conversion
+ Startup
+ Queue
```

But one version in `WorkScheduleHelper` calculates:

```text
Cycle
+ Conversion
+ Startup
```

and appears to omit QueueTime.

Therefore:

> The Work Order inline scheduler is the correct source when reverse-engineering the old Work Order page.

`WorkScheduleHelper` is useful as architectural reference but not as the exact original behavior.

---

# 25. Replacing the Default BOM

Normal Product load only brings:

```text
BomDefault = 1
```

into the initial Work Order definition.

The Work Order BOM UI can then open an available-material popup containing all `PrDefBOM` alternatives for:

```text
Product
Work Center
WC Item
Process
```

Double-clicking another BOM material replaces the current Work Order row values:

```text
ICode
IName
StdUOM
Warehouse
StdQty
```

Importantly:

```text
BomDefault is NOT overwritten by the popup selection
```

Therefore a Work Order-specific alternative can replace the original default while the Work Order row stays active/default.

Conceptually:

```text
Product Definition
RM001  Default = true
RM002  Default = false

Work Order initially:
RM001  Active/default

User replaces RM001 with RM002

Work Order:
RM002  Active/default
```

This is effectively a Work Order-specific substitute material mechanism.

---

# 26. Replacing the Default Machine

The same concept applies to machines.

Normal Product load initially selects:

```text
MacDefault = 1
```

The machine popup shows all `PrDefMachine` alternatives for the process.

Selecting another machine replaces:

```text
MachineCode
MachineName
CycleTime
ConversionTime
StartupTime
QueueTime
```

The existing Work Order row keeps its active/default status.

Conceptually:

```text
Product Definition:
MACHINE-A Default
MACHINE-B Alternative

Work Order initially:
MACHINE-A Active/default

User selects MACHINE-B

Work Order:
MACHINE-B Active/default
```

This is the original Work Order machine-substitution mechanism.

---

# 27. Adding a New BOM Manually

A manually inserted Work Order BOM row gets:

```text
BomDefault = true
WIPBomDefault = false
Tolerance = 0
```

The source comment says it must be default/active so downstream Issue-to-Production logic can find it.

This reinforces an important meaning:

> In Work Order tables, `BomDefault` effectively means “selected/active BOM for this Work Order”.

It no longer purely means “the original Product Definition default”.

The new ERP should consider renaming or conceptually separating this meaning.

Example better design:

```text
IsSelectedForWorkOrder
SourceWasDefinitionDefault
```

---

# 28. Adding a New Machine Manually

A manually inserted Work Order machine starts with:

```text
MacDefault = true
CycleTime = 0
ConversionTime = 0
StartupTime = 0
QueueTime = 0
```

Again this means:

> `MacDefault` in the Work Order behaves more like “active machine used for this Work Order”.

That semantic should be made explicit in the new system.

---

# 29. Save-Time BOM Default Validation

Before saving, `IsBomDefaultisTick()` checks the Work Order BOM.

The intention is:

```text
Every relevant Process that has BOM alternatives must still contain at least one active/default BOM row.
```

If not, Save is blocked.

A similar explicit save validation is not performed for machine default status, because machine presence is already validated while processing normal non-stock processes.

---

# 30. Stock Process Exception

The scheduler checks:

```text
PrProcess.Stock
```

A process marked as Stock is allowed to have no machine.

Normal process:

```text
No default machine
    -> Process error
```

Stock process:

```text
No machine
    -> allowed
```

This represents WIP/storage/stock-type routing steps.

The new Work Order engine should preserve this exception.

---

# 31. ReProcess Behavior — Extremely Important

`ReProcess()` does **not** simply recalculate the existing Work Order snapshot.

It calls:

```text
GetProdDefProcessTable()
```

again.

That reloads current Product Definition data:

```text
PrDefWCenter
PrDefProcess
Default BOM
Default Machine
PrDefLabour
```

Then calls `ProcessNewWithTime()` again.

Therefore:

> ReProcess can reset customized Work Order BOM/Machine selections back to current Product Definition defaults.

Example:

```text
Initial definition:
RM001
Machine A

User changes Work Order:
RM009
Machine C

Full ReProcess:
may reload RM001
may reload Machine A
```

This is an important legacy behavior.

For the new ERP it is much better to separate:

```text
Recalculate Schedule
    -> preserve Work Order BOM/Machine selections

Reload From Product Definition
    -> intentionally rebuild from current Product Definition
```

The old UI mixes these two concepts.

---

# 32. Normalize Quantity

A newer 2025 function called `NormalizeQuantity()` converts generated quantities back toward a unit basis.

It divides:

```text
PrSchWCenter.ScheQty / WO Qty
PrSchBOM.StdQty / WO Qty
PrSchMachine.CycleTime / WO Qty
PrSchLabour.LabourCost / WO Qty
```

But after a June 2025 change:

```text
ConversionTime = NOT divided
StartupTime    = NOT divided
QueueTime      = NOT divided
```

This is logically correct because:

```text
Cycle time = variable with quantity

Conversion time = fixed overhead
Startup time    = fixed overhead
Queue time      = fixed overhead
```

After normalization:

```text
doneProcess_WO = false
```

and the user must run Process again before Save.

---

# 33. Save Creates a Real Snapshot

When Save is finally executed, the generated Work Order records are persisted.

Main tables:

```text
PrSchMas
PrSchWCenter
PrSchProcess
PrSchBOM
PrSchMachine
PrSchLabour
PrSchDR
PrSchMacMain
```

The save is done inside a SQL transaction.

Work Order number is assigned first and propagated through all generated DataTables.

This confirms the old system uses a correct snapshot philosophy:

> Product Definition can later change without automatically changing already-created Work Orders.

That is a business behavior worth retaining.

---

# 34. PrSchMacMain

`PrSchMacMain` is generated from Work Order machine rows that actually have StartDate/CompleteDate.

It stores:

```text
Machine
Process
Work Center
Cycle/Conversion/Startup/Queue time
StartDate
CompleteDate
ScheQty
Machine Seq
Status
Completed
Updated
```

Non-scheduled machine alternatives normally do not become active machine-main schedule records.

---

# 35. Product Definition Machine Master Fallback

`ProdDefBL.GetPrDefMachineEx()` contains useful fallback logic:

```text
If PrDefMachine.ConversionTime = 0
    use PrMachine.ConversionTime

If PrDefMachine.StartupTime = 0
    use PrMachine.StartupTime

If PrDefMachine.QueueTime = 0
    use PrMachine.QueueTime
```

However:

> `WorkOrder.aspx.cs` does not use `GetPrDefMachineEx()` during normal Product Definition → Work Order generation.

It reads `PrDefMachine` directly.

Therefore old Work Order behavior is:

```text
use Product Definition machine timing values
even if they are zero
```

Do not add master-machine fallback in the new ERP unless it is a deliberate enhancement.

---

# 36. Critical Legacy Defect — Work Center Sequence Table

Current `WorkOrder.aspx.cs` does:

```text
dtCenSeq = CFunction.SelectDistinct(dtWC, "SeqNo")
```

`SelectDistinct()` creates a table containing only the columns requested.

Therefore `dtCenSeq` contains:

```text
SeqNo
```

only.

But the next code accesses:

```text
rowCen["WCCode"]
```

That column is not present.

Statically, that should cause:

```text
Column 'WCCode' does not belong to table
```

The same pattern exists in related generated Work Order helper code.

This should not be ported.

Correct design:

```text
Group Work Centers by SeqNo

foreach SeqGroup:
    foreach WorkCenter in that SeqGroup:
        schedule concurrently
```

---

# 37. Critical Legacy Defect — Mixed Time Units During Machine Replacement

Before Process:

```text
PrDefMachine timing = seconds
```

After Process:

```text
PrSchMachine timing = minutes
```

But the machine popup obtains values from Product Definition and copies them directly into a Work Order machine row.

Therefore after a Work Order has already been processed, replacing a machine can introduce:

```text
CycleTime in seconds
into a row expected to contain minutes
```

This is dangerous.

The new ERP must use one canonical unit.

---

# 38. Critical Legacy Defect — Non-Default Machine Unit Inconsistency

When adding non-default machines during `ProcessNewWithTime()`:

- calculated CycleTime is divided by 60
- ConversionTime is copied directly
- StartupTime is copied directly
- QueueTime is copied directly

Therefore one Work Order machine row can potentially contain:

```text
CycleTime       = minutes
ConversionTime  = seconds
StartupTime     = seconds
QueueTime       = seconds
```

This is a real legacy inconsistency.

Do not reproduce it.

---

# 39. Critical Legacy Defect — Lossy Seconds to Minutes Conversion

The scheduler converts raw seconds to minutes through integer types.

Conceptually something like:

```text
integerSeconds / 60
```

before/around rounding.

This can truncate precision.

Example:

```text
119 sec

integer division:
119 / 60 = 1

instead of:
1.9833 min
```

The new scheduler should calculate using:

```text
decimal/double seconds
or TimeSpan
```

until final UI display.

---

# 40. Critical Legacy Defect — Shift Group Handling

`PrShiftCalendar` contains shift-group information.

The cleaner helper can read a date-specific shift group.

However the Work Order inline scheduler generally creates:

```text
PrShiftGroupBL.GetShitGroupInfo()
```

from the default group and reuses it.

Therefore per-machine/per-date shift group configuration is not consistently respected.

The new scheduler should use:

```text
Machine Calendar Date
    -> assigned Shift Group
        -> actual working slots
```

for every date.

---

# 41. Critical Legacy Defect — Reverse() Mutates Shared Shift List

The code calls:

```text
shiftgroup.Shifts.Reverse()
```

during scheduling.

That changes the underlying list.

If called again later, ordering can flip back.

The new scheduler should never mutate the shared shift definition.

Use:

```text
forward:
OrderBy(StartTime)

backward:
OrderByDescending(StartTime)
```

on an enumeration/copy.

---

# 42. Critical Legacy Defect — `isFirstday`

`isFirstday` is scoped too broadly around machine scheduling.

This means the special “remaining available working time on first day” calculation may be applied to one machine and not equally to other parallel machines.

Each machine scheduling operation should independently evaluate its first scheduling date.

---

# 43. Critical Legacy Defect — Process Start Date Aggregation

Forward scheduling logic appears to choose the maximum machine StartDate when setting process start in one section.

Actual Process Start should normally be:

```text
MIN(machine.StartDate)
```

while Process Complete should be:

```text
MAX(machine.CompleteDate)
```

For a parallel operation:

```text
Machine A starts 08:00
Machine B starts 09:00

Process.StartDate should be 08:00
```

not 09:00.

The Work Center-level aggregation later behaves more like expected.

---

# 44. Critical Legacy Defect — Same Process SeqNo Concurrency

The architectural intent is:

```text
same Process SeqNo = parallel
```

But the inline Work Order loops can reuse a shared `compDate` in a way that serializes same-sequence processes.

The new implementation should explicitly schedule by **sequence group**, not by individual row iteration.

Recommended:

```text
for each ProcessSeq group:
    schedule every Process in group from the same incoming boundary

    group completion =
        max completion when forward

    group start =
        min start when backward
```

---

# 45. Critical Legacy Defect — First-Day Break Calculation

`GetActualWorkingTime()` can subtract the full break duration when the starting timestamp is already inside a break.

Example:

```text
Break = 12:00 - 13:00
Start = 12:30
```

Only 30 minutes of break remain.

The old logic can subtract 60 minutes.

The new calendar engine should operate with explicit working slots instead.

---

# 46. Critical Legacy Defect — Machine Availability Check Can Include Alternatives

A calendar availability check queries all machines from Product Definition for the product.

That may include:

```text
non-default alternative machines
```

Therefore an unused alternative without a calendar could potentially trigger a warning/block.

New logic should validate only:

```text
machines actually selected/active for this Work Order
```

unless Product Definition validation is being performed separately.

---

# 47. Critical Legacy Defect — UpdateMacItem Conditions

`UpdateMacItem()` contains suspicious conditions.

The old code appears to update:

```text
SeqNo
```

under a condition checking `QueueTime`, and update:

```text
MacDefault
```

under a condition checking `SeqNo`.

These should logically test their own fields.

This is a straightforward porting bug to avoid.

---

# 48. Recommended Clean Business Rules for the New ERP

After separating the useful business design from implementation defects, the clean rule set should be:

| Rule | Recommended Meaning |
|---|---|
| Product Definition | Reusable template |
| Work Order | Independent snapshot |
| Product StdBatchSize | Main quantity calculation basis |
| WC StdPackSize | Work-center quantity conversion |
| BOM StdQty | Qty required per Product StdBatch |
| Definition BOM Default | Initial suggested BOM |
| WO Active BOM | Material actually used by this WO |
| Definition Machine Default | Initial suggested machine |
| WO Active Machine | Machine actually used by this WO |
| CycleTime | Variable quantity-driven runtime |
| ConversionTime | Fixed overhead |
| StartupTime | Fixed overhead |
| QueueTime | Fixed overhead |
| Same SeqNo | Parallel |
| Different SeqNo | Sequential/barrier |
| Forward schedule | StartDate -> calculate completion |
| Backward schedule | Required completion -> calculate start |
| Calendar off day | Skip |
| Shift break | Skip |
| Maintenance | Machine unavailable |
| Stock process | Machine optional |
| Recalculate | Preserve current WO selections |
| Reload Definition | Deliberately rebuild from Product Definition |
| Save | Persist complete snapshot |

---

# 49. Baseline Formula Set

## Work Center Scale

\[
WCScale =
\frac{WCStdPackSize}{ProductStdBatchSize}
\]

## Work Center Schedule Qty

\[
WCScheduleQty =
WOQty \times WCScale
\]

## BOM Requirement

\[
BOMRequiredQty =
\frac{BOMStdQty}{WCStdPackSize}
\times WCScheduleQty
\]

Equivalent simplified formula:

\[
BOMRequiredQty =
BOMStdQty
\times
\frac{WOQty}{ProductStdBatchSize}
\]

## Machine Cycle Requirement

For `N` parallel active/default machines:

\[
CycleSecondsPerMachine =
\frac{MachineCycleSeconds}{WCStdPackSize}
\times
\frac{WCScheduleQty}{N}
\]

Equivalent:

\[
CycleSecondsPerMachine =
MachineCycleSeconds
\times
\frac{WOQty}
{ProductStdBatchSize \times N}
\]

## Total Machine Duration

\[
TotalMachineSeconds =
CycleSecondsPerMachine
+
ConversionSeconds
+
StartupSeconds
+
QueueSeconds
\]

Then that duration must be consumed only inside valid machine working slots.

---

# 50. Recommended Scheduling Algorithm for the New ERP

A clean scheduler should work using sequence groups.

## Forward

```text
anchor = WorkOrder.StartDate

for WorkCenterSeqGroup ASC:
    WCGroupStart = anchor

    schedule all Work Centers in same Seq from WCGroupStart

    for each Work Center:
        processAnchor = WCGroupStart

        for ProcessSeqGroup ASC:
            schedule all processes in group from processAnchor

            for each Process:
                machineAnchor = processAnchor

                for MachineSeqGroup ASC:
                    schedule all active machines
                    from same machineAnchor

                    machineGroupComplete =
                        MAX(machine completions)

                    machineAnchor =
                        machineGroupComplete

                Process.Start =
                    MIN(active machine starts)

                Process.End =
                    MAX(active machine completions)

            processGroupComplete =
                MAX(process completions)

            processAnchor =
                processGroupComplete

    WCGroupComplete =
        MAX(work center completions)

    anchor =
        WCGroupComplete

WorkOrder.Start =
    MIN(work center starts)

WorkOrder.Completed =
    MAX(work center completions)
```

---

# 51. Recommended Backward Algorithm

```text
anchor = WorkOrder.RequiredCompleteDate

for WorkCenterSeqGroup DESC:
    WCGroupEnd = anchor

    schedule all Work Centers in same Seq backward
    from the same WCGroupEnd

    for each Work Center:
        processAnchor = WCGroupEnd

        for ProcessSeqGroup DESC:
            schedule all processes in group backward
            from same processAnchor

            for each Process:
                machineAnchor = processAnchor

                for MachineSeqGroup DESC:
                    schedule all active machines backward
                    from same machineAnchor

                    machineGroupStart =
                        MIN(machine starts)

                    machineAnchor =
                        machineGroupStart

                Process.Start =
                    MIN(machine starts)

                Process.End =
                    MAX(machine completions)

            processGroupStart =
                MIN(process starts)

            processAnchor =
                processGroupStart

    WCGroupStart =
        MIN(work center starts)

    anchor =
        WCGroupStart

WorkOrder.Start =
    MIN(work center starts)

WorkOrder.Completed =
    MAX(work center completions)
```

---

# 52. Recommended Calendar Engine

Do not manually add/subtract shift totals.

Instead create explicit working intervals.

Example:

```text
Machine M01
2026-09-30

08:00 - 10:00
10:15 - 12:00
13:00 - 17:00
```

Remove/intersect:

```text
Machine maintenance:
14:00 - 15:00
```

Effective working intervals:

```text
08:00 - 10:00
10:15 - 12:00
13:00 - 14:00
15:00 - 17:00
```

Then consume machine duration through these intervals.

This automatically handles:

- breaks
- partial first day
- partial last day
- overnight shifts
- maintenance
- off days
- forward scheduling
- backward scheduling

with far less fragile code.

---

# 53. Recommended Time Storage

Do not repeat the old design:

```text
Definition = seconds
Work Order = minutes
```

Prefer:

```text
CycleTimeSeconds
ConversionTimeSeconds
StartupTimeSeconds
QueueTimeSeconds
```

through all layers.

UI can display:

```text
115 sec
1.92 min
00:01:55
```

without changing persisted meaning.

---

# 54. Recommended BOM / Machine Selection Model

The old names `BomDefault` and `MacDefault` become ambiguous after Work Order creation.

Better new model:

## Product Definition

```text
IsDefaultMaterial
IsDefaultMachine
```

## Work Order

```text
IsSelected
SourceDefinitionId
WasDefinitionDefault
WasManuallyChanged
```

That allows audit questions such as:

```text
Was RM009 originally part of Product Definition?
Was it the default?
Who changed it?
When?
What was replaced?
```

This is much clearer than reusing `Default`.

---

# 55. Recommended Recalculation Commands

Separate them clearly.

## Recalculate Schedule

Preserve:

```text
WO Work Centers
WO Processes
WO BOM selection
WO Machine selection
WO Labour
WO quantities
```

Only recalculate:

```text
machine duration
machine dates
process dates
work center dates
WO start/end
```

## Reload Product Definition

Deliberately recreate:

```text
Work Centers
Processes
Default BOM
Default Machines
Labour
```

then recalculate.

This avoids accidental loss of WO-specific substitutions.

---

# 56. What Should Be Preserved

The following original concepts are worth preserving:

- Product Definition → Work Order snapshot
- Product StdBatchSize scaling
- Work Center pack-size conversion
- BOM quantity scaling
- Alternative BOM selection
- Alternative machine selection
- Machine Seq parallel capacity
- Fixed machine setup overhead
- Forward scheduling
- Backward scheduling
- Machine calendar
- Shift breaks
- Off days
- Preventive maintenance
- Stock-process exception
- SQL transaction during Save
- Work Order audit trail

---

# 57. What Should NOT Be Copied Literally

Do not copy:

- Session DataTable architecture
- Mixed seconds/minutes fields
- `Reverse()` mutating shared shift lists
- integer minute truncation
- nested shared-`compDate` concurrency logic
- Product Definition and WO default meaning mixed together
- full Product Definition reload hidden inside ReProcess
- fragile DataTable `Select()` string expressions
- `SelectDistinct()` Work Center grouping bug
- inconsistent non-default machine units
- machine replacement unit mismatch
- calendar validation of unused alternatives
- process start aggregation bug
- update-field condition mistakes

---

# 58. Final Reverse-Engineered Understanding

The old Production Work Order engine is fundamentally based on this model:

```text
PRODUCT DEFINITION
      |
      | Template
      v
WORK ORDER SNAPSHOT
      |
      +-- Work Center quantity scaling
      |
      +-- Process routing
      |
      +-- BOM material requirement
      |
      +-- Machine requirement
      |      |
      |      +-- Qty-dependent cycle time
      |      +-- Conversion time
      |      +-- Startup time
      |      +-- Queue time
      |
      +-- Labour
      |
      +-- Sequence dependencies
      |
      +-- Machine calendars
      +-- Shift breaks
      +-- Off days
      +-- Preventive maintenance
      |
      v
START / COMPLETE SCHEDULE
      |
      v
SAVE INDEPENDENT WORK ORDER SNAPSHOT
```

The business design is useful.

The implementation is legacy and contains several inconsistencies.

The safest migration strategy is therefore:

> **Preserve the business rules.  
> Rebuild the calculation/scheduling engine cleanly.  
> Do not line-by-line clone `WorkOrder.aspx.cs`.**

---

# 59. Source Files Most Important for Future Migration Review

Use these as the main legacy reference set:

```text
ProductionPlan/ProdPlan/WorkOrder.aspx.cs
ProductionPlan/ProdPlan/WorkOrder.aspx

ERPClasses/BL/PrShiftGroupBL.cs
ERPClasses/BL/WorkScheduleHelper.cs
ERPClasses/BL/ProdDefBL.cs
ERPClasses/Classes/CFunction.cs

ProductionPlan/Helper/GenerateWorkOrderHelper.cs
ProductionPlan/Helper/PlanHelper.cs

ERP/ProdDef/MachineCtrl.ascx
ProductionPlan/ProdPlan/ProdDefination.aspx
ProductionPlan/ProdPlan/ProdDefinationDR.aspx.cs
```

---

# 60. Recommended Next Step for the New Blazor ERP

Use this document as the **legacy baseline specification**.

Then compare the new Blazor tables/services against it:

```text
PrDefMas
PrDefWCenter
PrDefProcess
PrDefBOM
PrDefMachine
PrDefLabour

        ↓

PrSchMas
PrSchWCenter
PrSchProcess
PrSchBOM
PrSchMachine
PrSchLabour
```

The new implementation should explicitly document:

1. quantity basis,
2. units,
3. sequence semantics,
4. parallelism,
5. default/substitution rules,
6. schedule anchor,
7. calendar behavior,
8. maintenance behavior,
9. recalculate behavior,
10. snapshot/audit behavior.

This will make the new Work Order engine much easier for both developers and AI coding agents to implement correctly.
