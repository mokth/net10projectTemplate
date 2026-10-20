# Machine Schedule Release Blocker — `WO00000002` Zero Cycle Time

**Status:** Diagnosed against the local `ERPWeb` database. Release-scan consistency fix applied and building. Product-definition cycle time still awaiting a value.
**Date:** 2026-10-19
**Environment:** local `.\SQLEXPRESS` / `ERPWeb` / company `DEMO` / branch `HQ`

---

## 1. Reported symptom

After running machine planning, creating a second Work Order for the same product as
`WO00000002` could not be released:

```text
Work Order WO00000002 has an incomplete or inconsistent saved machine schedule.
Recalculate or investigate it before release.
```

The message names `WO00000002`, but the Work Order being released was the **new** one
(`WO00000004`). That is the key diagnostic: the failure happens while *inspecting existing
capacity*, not while validating the Work Order under release.

---

## 2. Where the message comes from

Release computes machine capacity reservations before flipping the status:

```csharp
var allocationFragmentCount = await ReserveMachineCapacityForReleaseAsync(
    db, entity, scope, cancellationToken);
```

`ReserveMachineCapacityForReleaseAsync` loads **every active selected machine row on the same
physical machine codes**, company-wide (not branch-scoped), because the reservation key is
`CompanyCode + MachineCode`:

```csharp
var activeRows = await db.ProductionWorkOrderMachines.AsNoTracking()
    .Where(machine => machine.IsSelected
        && currentMachineCodes.Contains(machine.MachineCode)
        && machine.Operation != null
        && machine.Operation.WorkOrder != null
        && machine.Operation.WorkOrder.CompanyCode == scope.CompanyCode
        && (machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.Released
            || machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress))
```

Each row is then reconstructed through the canonical calendar scheduler, and the first guard
inside that reconstruction raises the reported message:

```csharp
if (plannedStart is null || plannedCompletion is null
    || operationStart is null || operationCompletion is null
    || calendarHorizonStart is null || calendarHorizonEnd is null
    || string.IsNullOrWhiteSpace(savedSourceHash)
    || plannedCompletion <= plannedStart
    || operationStart != plannedStart
    || operationCompletion != plannedCompletion)
{
    throw new WorkOrderCommandException(
        IvMasterErrorCode.Validation,
        $"{owner} has an incomplete or inconsistent saved machine schedule. Recalculate or investigate it before release.");
}
```

So `{owner}` is `"Work Order WO00000002"`, and at least one of its selected machine rows
violates one of those conditions.

> Note: when the Work Order being released has **no** selected machines, this scan is skipped
> entirely (`if (selectedMachines.Count == 0) return 0;`). The failure therefore proves the new
> Work Order shares the same physical machine.

---

## 3. Database evidence

### 3.1 Work Orders present

| WorkOrderNo | Status | Product | WO Start | WO Completion | SnapshotFormat | WO SchedRev | Created |
|---|---|---|---|---|---|---|---|
| `WO00000004` | DRAFT | FG002 | 2026-10-19 08:00 | 2026-10-19 08:00 | 3 | 0 | 2026-10-19 03:00 |
| `WO00000003` | RELEASED | FG001 | 2026-10-12 08:00 | 2026-10-12 11:03:39.998 | 3 | 9 | 2026-10-09 05:08 |
| `WO00000002` | IN_PROGRESS | FG002 | 2026-10-08 08:00 | 2026-10-08 08:00 | 3 | 0 | 2026-10-08 07:12 |
| `WO00000001` | DRAFT | FG001 | 2026-10-08 08:00 | 2026-10-08 11:03:39.998 | 3 | 0 | 2026-10-08 05:25 |

`WO00000004` is the new Work Order that could not be released.

### 3.2 `WO00000002` selected machine row (the defective row)

```text
OperationCode      : P1
ProcessType        : MACHINE
MachineCode        : M1        (IsSelected = 1, IsDefault = 1, Priority = 1)
ScheduleRevision   : 0
PlannedStart       : 2026-10-08 08:00:00.0000000
PlannedCompletion  : 2026-10-08 08:00:00.0000000     <-- identical to start
OperationStart     : 2026-10-08 08:00:00.0000000
OperationCompletion: 2026-10-08 08:00:00.0000000
CalendarHorizon    : 2026-10-07 -> 2027-10-08          (populated)
ScheduleSourceHash : 6951D64C...883DB440                 (populated)
OutputPerCycle     : 300
PlannedCycleCount  : 1
PlannedCycleSlots  : 1
PlannedRunMinutes  : 0
SetupSeconds       : 0
ConversionSeconds  : 0
QueueSeconds       : 0
CycleSeconds       : 0          <-- root cause
```

The horizon and hash are **fine**. The only failing condition is
`plannedCompletion <= plannedStart`, caused by every time component being zero.

### 3.3 All Work Orders touching machine `M1`

| WorkOrderNo | Status | Operation | Selected | Duration (s) | Start | Completion |
|---|---|---|---|---|---|---|
| `WO00000001` | DRAFT | P1 | 1 | 5019.998 | 2026-10-08 08:00 | 2026-10-08 09:23:39.998 |
| `WO00000002` | IN_PROGRESS | P1 | 1 | **0** | 2026-10-08 08:00 | 2026-10-08 08:00 |
| `WO00000003` | RELEASED | P1 | 1 | 5019.998 | 2026-10-12 08:00 | 2026-10-12 09:23:39.998 |
| `WO00000004` | DRAFT | P1 | 1 | **0** | 2026-10-19 08:00 | 2026-10-19 08:00 |

Because `WO00000002` is the only *active* zero-duration row on `M1`, it blocks **every** release
that targets `M1` — including `WO00000001` (FG001), which has a perfectly valid schedule.

### 3.4 New Work Order shares the same defect

`WO00000004` also has duration `0`, so even after removing the `WO00000002` blocker it would fail
reconstruction **on itself**. Fixing only the blocker is not sufficient.

### 3.5 Source of the zero — the ACTIVE Product Definition

| Product | Ver | Status | Op | Machine | IsPrimary | CycleSeconds | OutputPerCycle | Setup | Conversion | Queue | Rate/hr |
|---|---|---|---|---|---|---|---|---|---|---|---|
| FG001 | 7 | ACTIVE | P1 | M1 | 1 | 50 | 1 | 20 | 0 | 0 | 0 |
| FG001 | 7 | ACTIVE | P2 | M2 | 1 | 60 | 1 | 0 | 0 | 0 | 0 |
| **FG002** | **1** | **ACTIVE** | **P1** | **M1** | 1 | **0** | **300** | **0** | **0** | **0** | **3** |

- `PrBomHdr.UID = 11` (FG002, version 1, ACTIVE, `DefinitionCode = STANDARD`, `IsDefaultDefinition = 1`, `ValidationStatus = UNVERIFIED`)
- `PrBomOperation.UID = 10021` (P1, `ProcessType = MACHINE`, `CentralSequence = 10`)
- `PrBomMachineOption.UID = 10021` (M1, `CycleSeconds = 0`, `OutputPerCycle = 300`, `MachineRatePerHour = 3`)

There is only one FG002 definition revision, so there is no correctly-configured alternative.

### 3.6 `WO00000002` cannot be repaired through the UI

```text
Operation P1:  PlannedQty 0 | ProcessedQty 10 | GoodQty 10 | other qty 0
Production outputs:   1
Production posting links: 9
```

- `ReopenForEdit` requires `Status == RELEASED` — it is `IN_PROGRESS`, so reopening is refused.
- Reschedule blocks started production:

```csharp
if (order.GoodQty > 0m || order.ScrapQty > 0m || order.RejectQty > 0m || order.HoldQty > 0m
    || order.ApprovedVarianceQty > 0m)
    blockers.Add("Work Order execution quantities show that production has started.");
```

- Backfill refuses unverifiable schedules (`SCHEDULE_UNVERIFIED`, blocking), and Apply refuses
  when any blocker exists.
- Planner writes are fail-closed while anything is missing/stale.

**Conclusion:** no in-app command can fix the `WO00000002` row. Any repair is either code or
reviewed data surgery.

### 3.7 Allocation fragments

| UID | Machine | WorkOrder | Revision | Fragment | Start | End |
|---|---|---|---|---|---|---|
| 30015 | M1 | `WO00000003` | 9 | 1 | 2026-10-12 08:00 | 2026-10-12 09:23:39.998 |
| 30016 | M2 | `WO00000003` | 9 | 1 | 2026-10-12 09:23:39.998 | 2026-10-12 11:03:39.998 |

`WO00000002` has **no** fragments (it was released before the allocation schema existed). This
matters for any repair that makes its duration positive: planner readiness counts active
machine operations without current fragments and would report
`ALLOCATION_BACKFILL_INCOMPLETE` until the backfill runs.

---

## 4. Root cause

Two independent defects combine:

### 4.1 Data defect
FG002's ACTIVE definition has `CycleSeconds = 0` with all other machine times zero. The run
duration is derived purely from it:

```csharp
machine.PlannedRunMinutes = Round4(machine.PlannedCycleSlots * machine.CycleSeconds / 60m);
```

For a `MACHINE` step, `StandardDurationMinutes` is ignored — the machine is the only source of
duration:

```csharp
var duration = machine is null
    ? TimeSpan.FromMinutes((double)operation.StandardDurationMinutes)
    : TimeSpan.FromSeconds((double)(machine.SetupSeconds + machine.ConversionSeconds + machine.QueueSeconds))
      + TimeSpan.FromMinutes((double)machine.PlannedRunMinutes);
```

Result: `duration == 0` → `ActualStart == ActualCompletion` → zero-length interval.

### 4.2 Code inconsistency
The Release capacity scan was the **only** consumer that did not exclude zero-duration machine
rows. Every other consumer already filters them:

| Consumer | Filters positive duration? |
|---|---|
| `MachineSchedulePlannerReadinessService.GetReadinessAsync` | Yes |
| `MachineScheduleRescheduleService.LoadExistingCapacityAsync` | Yes |
| `MachineScheduleAllocationBackfillService.LoadActiveSchedulesAsync` | Yes |
| `scripts/preflight-machine-schedule-planner.sql` | Yes |
| `ReserveMachineCapacityForReleaseAsync` (Release) | **No — was missing** |

A half-open interval with `Start == End` can never overlap (`Start < right.End && End > right.Start`
is false), so such a row reserves no capacity and is harmless to capacity math. Yet it blocked
every release on the machine.

---

## 5. Is cycle time mandatory in this ERP?

**No — not technically.** The code accepts `CycleSeconds = 0`. It is only *effectively* required
for machine-paced steps, because Release refuses a zero-length schedule.

### 5.1 Product Definition validation — only non-negative

```csharp
if (machine.CycleSeconds < 0m || machine.ConversionSeconds < 0m
    || machine.SetupSeconds < 0m || machine.QueueSeconds < 0m)
    errors[$"{machinePrefix}.CycleSeconds"] = "Machine times cannot be negative.";
if (machine.MachineRatePerHour < 0m)
    errors[$"{machinePrefix}.MachineRatePerHour"] = "Machine rate per hour cannot be negative.";
```

Activation requires the machine to **exist**, and requires a positive standard duration only
for duration-based processes:

```csharp
var requiresMachine = processType is PrProcessTypes.Machine or PrProcessTypes.Automated
                      || (processType == PrProcessTypes.Packing && source.Machines.Count > 0);
var requiresDuration = durationBased
                       || (processType == PrProcessTypes.Packing && source.Machines.Count == 0);
...
if (requiresMachine)
{
    if (machines.Count == 0)
        errors[...] = $"Route {i + 1}: process {operation} requires at least one machine before activation.";
    else if (machines.All(m => m.Labours.Count == 0))
        errors[...] = $"Route {i + 1}: process {operation} requires a labour/operator on a machine option before activation.";
}

if (requiresDuration && standardDuration <= 0m)
    errors[...] = $"Route {i + 1}: process {operation} requires a standard duration greater than zero before activation.";
```

`requiresDuration` is **false** for `MACHINE` / `AUTOMATED`, so a zero-cycle-time machine passes
activation unchecked.

### 5.2 Work Order readiness — no cycle-time check

```csharp
foreach (var machine in operation.Machines)
{
    if (machine.OutputPerCycle <= 0m)
        report.Add(ProductionReadinessErrorCodes.OutputPerCycleMissing, ...);

    if (machine.ParallelMachineCount < 1)
        report.Add(ProductionReadinessErrorCodes.ParallelMachineInvalid, ...);

    if (!ProductionMachineCycleQuantityModes.IsKnown(machine.CycleQuantityMode))
        report.Add(ProductionReadinessErrorCodes.DurationBasisInvalid, ...);
}
```

No `CycleSeconds` guard here either.

### 5.3 Quantity calculation — no cycle-time check

Guards `OutputPerCycle` and `ParallelMachineCount` only, then computes the run minutes from
whatever `CycleSeconds` contains.

### 5.4 Practical rule table

| Process type | Duration source | `CycleSeconds = 0` allowed? |
|---|---|---|
| `MACHINE`, `AUTOMATED` | machine only | Data-valid, but **unreleasable** |
| `PACKING` with machines | machine only | same |
| `PACKING` without machines | `StandardDurationMinutes` (> 0 enforced) | irrelevant — no machine row |
| `MANUAL`, `INSPECTION`, `WAIT`, `SUBCONTRACT` | `StandardDurationMinutes` (> 0 enforced) | machines not allowed at all |

### 5.5 General ERP expectation

Cycle time is the fundamental capacity driver: capacity = available time ÷ cycle time. Without it
you cannot answer "when will this finish", compute load, or detect overload — hence the usual
design:

- **Machine-paced step** — cycle time genuinely mandatory; reject zero at definition activation
  rather than accept it and block release much later.
- **Manual / fixed-duration step** — standard minutes per batch suffices; cycle time optional.
- **Allowed zero** — only meaningful for a non-capacity, sub-second operation, and should be an
  explicit "not machine-paced" flag, never a silent `0`.

Two gaps in this codebase:

1. Activation checks that a machine *exists* but never that its timing is positive, so a
   definition can become ACTIVE with an un-schedulable step (exactly FG002 v1, which is also
   `ValidationStatus = UNVERIFIED`).
2. There is no fine-grained readiness error for "zero machine duration". The failure surfaces
   late as a reconstruction error that names a *different* Work Order, which is what made this
   hard to diagnose.

---

## 6. Options considered

### 6.1 Desired path (adopted)
Fix the Release-scan inconsistency **and** give FG002 a real cycle time, then refresh the new
Draft. Touches no frozen history on `WO00000002` and fabricates no schedule data.

### 6.2 "1 minute repair" (rejected)
Set `PlannedRunMinutes = 1` on `WO00000002`, move completion to `08:01`, add the missing
allocation fragment, and patch `WO00000004`'s snapshot columns directly. Rejected because:

- `WorkOrderSnapshotHasher` includes `PlannedRunMinutes` and both machine timestamps, so any SQL
  `UPDATE` leaves a **stale `SnapshotHash`**:

```csharp
w.Add(machine.PlannedCycleCount);
w.Add(machine.PlannedCycleSlots);
w.Add(machine.PlannedRunMinutes);
w.AddPlannedTimestamp(machine.PlannedStartDateTime);
w.AddPlannedTimestamp(machine.PlannedCompletionDateTime);
```

- No app command can recompute that hash for an `IN_PROGRESS` Work Order, so the integrity token
  would be permanently wrong.

### 6.3 `IsSelected = 0` on `WO00000002` (rejected)
Removes it from capacity, but it disappears from the planner board
(`m.IsSelected` filters) and Daily Production would show no selected machine for a live order —
a visible loss of machine identity.

### 6.4 Filter in the Release scan (adopted as one half)
One-line consistency fix, leaves `WO00000002` untouched, matches every other consumer.

---

## 7. Change applied

`ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs` —
`ReserveMachineCapacityForReleaseAsync`, active-row load:

```csharp
var activeRows = await db.ProductionWorkOrderMachines.AsNoTracking()
    .Where(machine => machine.IsSelected
        && currentMachineCodes.Contains(machine.MachineCode)
        && machine.Operation != null
        && machine.Operation.WorkOrder != null
        && machine.Operation.WorkOrder.CompanyCode == scope.CompanyCode
        && (machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.Released
            || machine.Operation.WorkOrder.Status == ProductionWorkOrderStatuses.InProgress)
        // A zero-duration machine row reserves no capacity: a half-open interval whose
        // start equals its end can never overlap anything. Readiness, the planner,
        // allocation backfill and the SQL preflight all exclude these rows, so Release
        // must too — otherwise one legacy zero-duration Work Order blocks every release
        // on that physical machine. Releasing the current Work Order still requires a
        // positive, reproducible schedule of its own.
        && (machine.SetupSeconds + machine.ConversionSeconds + machine.QueueSeconds
            + machine.PlannedRunMinutes * 60m) > 0m)
```

Build verification:

```text
dotnet build ErpWeb.Core/ErpWeb.Core.csproj --no-restore -m:1 -nr:false -v:q
    3267 Warning(s)
       0 Error(s)
```

**Scope of the fix:**
- Stops one zero-duration Work Order from blocking releases on that machine.
- Does **not** allow releasing a Work Order whose own schedule is zero-length — the reconstruction
  guard still applies to the Work Order under release.

---

## 8. Remaining work

1. **Set FG002 / P1 / M1 `CycleSeconds`** on `PrBomMachineOption.UID = 10021`
   (`PrBomHdr.UID = 11`, ACTIVE v1). Edit in place rather than inserting a revision — a second
   ACTIVE revision for the same `(ProdCode, DefinitionCode)` risks
   `WO_DEFINITION_REVISION_AMBIGUOUS`.

   Because `OutputPerCycle = 300`, the cycle count is `ceil(qty / 300)`, so both qty 50 and
   qty 100 consume exactly **one** cycle:

   | CycleSeconds | Window for one cycle | Machine cost @ 3/hr |
   |---|---|---|
   | 300 | 5 min | 0.25 |
   | 15000 (50 s/unit, same as FG001 M1) | 4 h 10 m | 12.50 |
   | 18000 (60 s/unit, same as FG001 M2) | 5 h 00 m | 15.00 |

2. **Run `More → Update from Product Definition` on `WO00000004`.** This is the only path that
   rebuilds the frozen snapshot and recomputes `PlannedRunMinutes`, `ScheduleSourceHash` and
   `SnapshotHash` from the real machine calendar. A SQL-only `UPDATE` would leave a stale hash
   and `Recalculate Schedule` alone would reuse the frozen `CycleSeconds = 0`.

3. **Release `WO00000004`** and verify the persisted rows plus the new allocation fragments.

4. **Decide on `WO00000002`.** Its frozen snapshot remains as recorded (zero-length, no
   fragments). Options were: leave as-is (now harmless to other releases), or execute a
   controlled repair command at the service layer that recomputes the schedule, bumps
   `ScheduleRevision`, and writes fragments + audit — never a hand-written `UPDATE`.

---

## 9. Suggested hardening (not implemented)

1. **Reject zero machine duration at definition activation**, next to the existing non-negative
   check, with a field-level message.
2. **Add a readiness error code** such as `WO_MACHINE_DURATION_REQUIRED`, surfaced with
   `OperationCode` and `MachineCode`.
3. **Make the reconstruction error specific.** Instead of one generic sentence, report the
   Work Order, operation, machine and the exact failed predicate
   (`PLANNED_COMPLETION_NOT_AFTER_START`, `OPERATION_START_MISMATCH`, `SOURCE_HASH_MISSING`, …).
4. **Align the release message with reality.** "Recalculate … before release" is misleading for an
   active Work Order, where `Recalculate Schedule` is unavailable (`CanEdit` requires `DRAFT`).

---

## 10. Verification queries

Full detail for one Work Order:

```sql
SET NOCOUNT ON;
DECLARE @wo nvarchar(30) = N'WO00000002';

SELECT
    w.WorkOrderNo,
    w.Status,
    w.SchedulingDirection,
    o.OperationCode,
    m.MachineCode,
    m.ScheduleRevision,
    m.PlannedStartDateTime      AS MachineStart,
    m.PlannedCompletionDateTime AS MachineCompletion,
    o.PlannedStartDateTime      AS OperationStart,
    o.PlannedCompletionDateTime AS OperationCompletion,
    m.CalendarHorizonStart,
    m.CalendarHorizonEnd,
    m.ScheduleSourceHash,
    m.SetupSeconds, m.ConversionSeconds, m.QueueSeconds, m.PlannedRunMinutes,
    CASE
        WHEN m.PlannedStartDateTime IS NULL          THEN 'MACHINE_START_NULL'
        WHEN m.PlannedCompletionDateTime IS NULL     THEN 'MACHINE_COMPLETION_NULL'
        WHEN o.PlannedStartDateTime IS NULL          THEN 'OPERATION_START_NULL'
        WHEN o.PlannedCompletionDateTime IS NULL     THEN 'OPERATION_COMPLETION_NULL'
        WHEN m.CalendarHorizonStart IS NULL          THEN 'HORIZON_START_NULL'
        WHEN m.CalendarHorizonEnd IS NULL            THEN 'HORIZON_END_NULL'
        WHEN NULLIF(LTRIM(RTRIM(m.ScheduleSourceHash)), '') IS NULL THEN 'SOURCE_HASH_NULL'
        WHEN m.PlannedCompletionDateTime <= m.PlannedStartDateTime THEN 'NON_POSITIVE_MACHINE_SPAN'
        WHEN o.PlannedStartDateTime <> m.PlannedStartDateTime THEN 'OP_START_MISMATCH'
        WHEN o.PlannedCompletionDateTime <> m.PlannedCompletionDateTime THEN 'OP_COMPLETION_MISMATCH'
        ELSE 'PROVENANCE_COMPLETE'
    END AS DetectedIssue,
    (SELECT COUNT(*)
       FROM dbo.PrMachineScheduleAllocation a
      WHERE a.WorkOrderMachineID = m.UID
        AND a.ScheduleRevision = m.ScheduleRevision) AS CurrentAllocationFragments
FROM dbo.PrWorkOrder w
JOIN dbo.PrWorkOrderOperation o ON o.WorkOrderID = w.UID
JOIN dbo.PrWorkOrderMachine m ON m.OperationID = o.UID
WHERE w.WorkOrderNo = @wo
  AND m.IsSelected = 1;
```

Zero-duration selected rows across the company:

```sql
SET NOCOUNT ON;
SELECT w.WorkOrderNo, w.Status, o.OperationCode, m.MachineCode, m.ScheduleRevision,
       (m.SetupSeconds + m.ConversionSeconds + m.QueueSeconds + m.PlannedRunMinutes * 60) AS DurationSeconds,
       m.PlannedStartDateTime, m.PlannedCompletionDateTime
FROM dbo.PrWorkOrder w
JOIN dbo.PrWorkOrderOperation o ON o.WorkOrderID = w.UID
JOIN dbo.PrWorkOrderMachine m ON m.OperationID = o.UID
WHERE m.IsSelected = 1
  AND (m.SetupSeconds + m.ConversionSeconds + m.QueueSeconds + m.PlannedRunMinutes * 60) <= 0
ORDER BY w.Status, w.WorkOrderNo;
```

Machine options with zero productive time:

```sql
SET NOCOUNT ON;
SELECT h.ProdCode, h.Version, h.Status, o.OperationCode, mo.MachineCode,
       mo.CycleSeconds, mo.SetupSeconds, mo.ConversionSeconds, mo.QueueSeconds,
       mo.OutputPerCycle, mo.MachineRatePerHour
FROM dbo.PrBomHdr h
JOIN dbo.PrBomOperation o ON o.BomHdrId = h.UID
JOIN dbo.PrBomMachineOption mo ON mo.OperationID = o.UID
WHERE h.Status = N'ACTIVE'
  AND mo.CycleSeconds <= 0
ORDER BY h.ProdCode, o.CentralSequence;
```

---

## 11. Rules of engagement for this class of defect

- **Never** hand-write schedule dates/hashes with SQL on an active Work Order. The hash and the
  canonical reconstruction must agree, or the row moves from one error to another.
- Use **Update from Product Definition** for Drafts; use a **service-layer repair command** for
  active Work Orders.
- Fix the **source** (definition cycle time), not the symptom (fabricated minutes).
- Keep the release gate honest: exclude rows that genuinely reserve no capacity; do not begin
  skipping rows that merely *appear* inconsistent.
