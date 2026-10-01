---
name: Production Machine & Maintenance — Malaysia SME ERP Enhancement
branch: production
repository: mokth/net10projectTemplate
scope: Practical machine / preventive / maintenance foundation for common Malaysian SME manufacturing ERP
status: implementation-ready
review_baseline: agreed-locks-2026-10-01-r3
note: This file is the single authoritative implementation plan. Untracked until committed; coding agents must use this document only.
---

# Production Machine & Maintenance — Concrete Enhancement Plan

## 0. Agreed Locks (from plan review)

These supersede any conflicting wording later in this document.

| Topic | Locked decision | Priority |
|---|---|---|
| Scheduler | Subtract capacity only when `Status == PLANNED` (company + machine scoped). COMPLETED and CANCELLED do **not** block. Completing PM does **not** auto-reschedule already-persisted Work Orders; it only affects subsequent Preview/Create/Recalculate. | P1 |
| Complete Preventive | Editable Actual Start (default planned Start) / Actual End (default now). One DB transaction via `IPrMachineMaintenanceService.CompletePreventiveAsync` only. Only transition `PLANNED → COMPLETED`. | P1 |
| Complete concurrency | Unique filtered index on `PrMacMaintenance.PreventiveUid WHERE NOT NULL`. Concurrent second Complete returns already-completed / conflict — never a second history row. | P1 |
| Preventive delete | Hard-delete only when `Status = PLANNED` and no linked `PrMacMaintenance`. Never hard-delete COMPLETED or CANCELLED. Prefer CANCEL to preserve history. FK `ON DELETE NO ACTION`. | P1 |
| KPI labels | Scheduled / Completed / Pending / Overdue PM. | P1 |
| KPI period basis | All four PM cards use **planned** preventive date/window (`DownDt` / planned WindowEnd) in the selected range — same population. Hours/Cost use **actual** maintenance `StartDateTime`/`EndDateTime` period — do not mix silently. | P1 |
| Tenant scope | `CompCode` = mandatory security/ownership boundary on every query. `BranchCode`/`LocCode` = operational metadata from write scope; never substitute for CompCode. Never guess CompCode on backfill. | P1 |
| Start/End UI bug | Normal New/Edit must expose Start/End; server rejects missing/invalid windows (year-0001 is a data defect). | P1 |
| Overlap | Replace one-row-per-day with time-window overlap; ignore CANCELLED; shared overnight normalizer. | P1 |
| Maintenance status | OPEN / IN_PROGRESS / COMPLETED / CANCELLED with field requirements locked in §8.4. | P2 |
| PrMachine.Active | One process per MachineCd per company. Evaluate `CompCode + MachineCd + Active`. Inactive: block NEW Preventive/Maintenance selection; keep history readable. Preflight duplicate MachineCd. | P2 |
| MType length | Keep `nvarchar(10)`; do not widen. | — |
| Reason master | Validate new Preventive/Maintenance ReasonCd against active company reasons. | P2 |
| SQL packaging | `preflight` + `enhance` + `rollback` scripts (shift-break pattern). | P2 |
| P3 fields | SerialNo / MachineType / HourlyCost are nice-to-have reference fields, not P1. | P3 |

---

## 1. Goal

Enhance the existing Production / Planning module so it has the **common machine-maintenance functions normally useful to Malaysian SME manufacturers**, while staying deliberately below SAP / full CMMS / MES complexity.

The enhancement must support everyday operations and future management reporting without disturbing the current Product Definition, Work Order snapshot, machine calendar, or production scheduling architecture.

### Target outcome

Users should be able to answer these normal ERP questions:

- What machines do we have and are they active?
- When is a machine planned to be unavailable for preventive maintenance?
- Was the preventive maintenance completed or cancelled?
- What machine broke down / was repaired / serviced?
- When did maintenance start and finish?
- Why was maintenance needed?
- What action was taken?
- Who handled it?
- How much did the maintenance cost?
- Which machines had the most breakdowns / downtime / maintenance cost this month?

### Explicitly out of scope

Do **not** implement in this milestone:

- OEE
- MTBF / MTTR
- PLC / IoT integration
- predictive maintenance
- sensor readings
- meter readings / run-hour triggers
- spare-parts inventory reservation subsystem
- maintenance approval workflow
- full CMMS work-order engine
- asset depreciation
- calibration certification management
- energy monitoring
- SAP-style equipment hierarchy

Those can be added later only if real customers require them.

---

# 2. Current Repository Baseline

Branch reviewed: `production`.

## Existing machine master

Files:

- `ErpWeb.Model/Entities/Planning/PrMachine.cs`
- `ErpWeb.Model/Configurations/Planning/PrMachineConfiguration.cs`
- `ErpWeb.Core/Planning/PrMachineService.cs`
- `ErpWeb.UI/Planning/Masters/PrMachineList.razor`
- `ErpWeb.UI/Planning/Masters/PrMachineList.razor.cs`

Current useful fields already include:

```text
MachineCd
MachineDes
ProcessCd
ConversionTime
StartupTime
QueueTime
CompCode
BranchCode
LocCode
```

This remains the machine master used by Product Definition and scheduling.

## Existing preventive schedule

Files:

- `ErpWeb.Model/Entities/Planning/PrPreventive.cs`
- `ErpWeb.Model/Configurations/Planning/PrPreventiveConfiguration.cs`
- `ErpWeb.Core/Planning/PrSupportMasterServices.cs`
- `ErpWeb.UI/Planning/Masters/PrPreventiveList.razor`
- `ErpWeb.UI/Planning/Masters/PrPreventiveList.razor.cs`

Current fields:

```text
Uid
MachineCd
DownDt
StartTm
EndTm
ReasonCd
Remark
Created
Updated
UserId
```

`ProductionCalendarScheduleDataLoader` already reads `PrPreventive` and converts it into machine-unavailable windows. **Preserve this architecture.**

## Existing maintenance history table

Files:

- `ErpWeb.Model/Entities/Planning/PrMacMaintenance.cs`
- `ErpWeb.Model/Configurations/Planning/PrMacMaintenanceConfiguration.cs`
- `ErpWeb.Model/Entities/Planning/PrMacMaintenanceImage.cs`
- `ErpWeb.Model/Configurations/Planning/PrMacMaintenanceImageConfiguration.cs`

Current maintenance fields already provide a useful legacy base:

```text
TrxDate
MacCode
Description
ActionTaken
Status
ReportBy
ActionBy
ActionOn
RefCode
RepType
MType
Name
Reminder
```

There is currently no proper application service / CRUD page for `PrMacMaintenance` in the reviewed branch.

## Existing Work Order machine / production foundation

Keep unchanged:

- `ProductionWorkOrderMachine`
- `ProductionWorkOrderOperation`
- `ProductionWorkOrder`
- `ProductionCalendarScheduleDataLoader`
- Work Order machine snapshot semantics
- Product Definition machine options
- Machine calendars / shifts

The Work Order side already contains planned cycle/run data and Good / Scrap / Reject quantities. This maintenance enhancement must not rewrite that architecture.

---

# 3. Important Existing Gaps to Fix

## Gap A — normal Preventive UI loses Start / End time

`PrPreventive` contains:

```text
StartTm
EndTm
```

but `PrPreventiveEditVm` and the standard New/Edit popup currently do not expose them.

Result: a normal preventive entry is incomplete even though the scheduler depends on those timestamps.

**Required fix:** expose Start Time and End Time in list row, edit VM, grid and popup.

---

## Gap B — one preventive row per machine per date

Current service rejects another record when:

```text
MachineCd == machine && DownDt == date
```

This prevents valid scenarios such as:

```text
MC001  2026-10-15  09:00-10:00  Lubrication
MC001  2026-10-15  17:00-18:00  Cleaning
```

**Required fix:** allow multiple preventive windows on the same day; reject only exact duplicates or overlapping active windows.

---

## Gap C — Preventive is not company-scoped in current reads

`PrMachine` is company-scoped, but current `PrPreventive` queries do not contain `CompCode` and `ProductionCalendarScheduleDataLoader` currently reads preventive rows by machine code/date only.

This is inconsistent with the rest of the Planning architecture.

**Required fix:** add tenant fields to `PrPreventive`, backfill safely, and scope all CRUD/scheduling queries by company.

---

## Gap D — maintenance table exists but is not an operational feature

`PrMacMaintenance` has useful legacy fields but lacks:

- tenant scope;
- exact maintenance start/end;
- consistent reason code;
- simple cost breakdown;
- link back to preventive schedule;
- application service;
- menu/page;
- summary reporting.

The plan will enhance this existing table rather than create a large new maintenance subsystem.

---

# 4. Locked Architecture Decisions

| Topic | Decision |
|---|---|
| Machine master | Keep `PrMachine`. Add Active (P2) and optional P3 reference fields (MachineType / SerialNo / HourlyCost). |
| Machine Active semantics | Business invariant is one process per `CompCode + MachineCd` (already enforced in `PrMachineService` / `PrHierarchyService`). Evaluate Active with `CompCode + MachineCd + Active`. Preflight legacy duplicate MachineCd; do not invent multi-process Active aggregation. |
| Preventive schedule | Keep `PrPreventive` as the source of planned machine-unavailable windows. |
| Maintenance transaction/history | Keep and enhance `PrMacMaintenance`; do not introduce a large CMMS work-order hierarchy. |
| Maintenance reasons | Add one small tenant-scoped reason master so KPI grouping is reliable and not free-text dependent. New Preventive/Maintenance rows validate `ReasonCd` against active company reasons. |
| Scheduler integration | Continue subtracting `PrPreventive` windows in `ProductionCalendarScheduleDataLoader`, but only rows with `Status == PLANNED` (plus company + machine scope). |
| Cancelled preventive | Must not block scheduling. |
| Completed preventive | Retain as history; does **not** block scheduling after completion. “Available again” means subsequent Preview/Create/Recalculate — **not** automatic rewrite of already-persisted Work Order schedules. |
| Complete ownership | `CompletePreventiveAsync` lives only on `IPrMachineMaintenanceService`; Preventive UI triggers it and does not dual-write history. |
| Complete concurrency | Unique filtered index on non-null `PreventiveUid`; only `PLANNED → COMPLETED`; second Complete is idempotent/conflict, never duplicate history. |
| Preventive delete | Hard-delete only `PLANNED` with no linked maintenance. Never hard-delete COMPLETED/CANCELLED. Prefer Cancel. FK `ON DELETE NO ACTION`. |
| Tenant authority | `CompCode` = mandatory query/security boundary. `BranchCode`/`LocCode` = write-scope operational metadata/filters only. |
| Work Order snapshots | Never rewrite existing frozen Work Order machine snapshot data when machine master is later edited. |
| Maintenance cost | Store Parts + Labour + Other; derive Total in service/report, do not store duplicate total. |
| Downtime | Derive from maintenance `StartDateTime` / `EndDateTime`; do not create a separate downtime-event subsystem in this milestone. |
| MType | Reuse existing column; keep max length 10; constants PREVENTIVE / BREAKDOWN / REPAIR / SERVICE / OTHER. Do not widen for hypothetical future types. |
| Images | Keep existing `PrMacMaintenanceImages`; attachment/image enhancement is optional after core maintenance is stable. |
| KPI scope | Basic maintenance summary only (Scheduled / Completed / Pending / Overdue PM + breakdown/hours/cost); no OEE / MTBF / MTTR in this milestone. |
| KPI period basis | PM cards keyed by planned preventive window/`DownDt` in selected range (same population). Hours/Cost keyed by actual maintenance datetime period. |

---

# 5. Phase 1 — Enhance `PrMachine` Minimally

## 5.1 Schema

Extend `PrMachine` with (priority-aware):

```text
Active          bit             NOT NULL default 1     -- P2
MachineType     nvarchar(30)    NULL                   -- P3
SerialNo        nvarchar(50)    NULL                   -- P3
HourlyCost      decimal(19,6)   NOT NULL default 0     -- P3
```

### Purpose

`Active`
- lets a customer stop a retired/unavailable machine from being selected for new setup without deleting history.
- Evaluate with `CompCode + MachineCd + Active` only. The app already enforces one process assignment per machine per company; do not implement “any/all process rows Active” aggregation.
- SQL preflight must fail if legacy data has duplicate `(CompCode, Machine_Cd)`.

`MachineType` / `SerialNo` / `HourlyCost`
- optional reference fields (P3). HourlyCost must not retroactively alter Work Order snapshots.

## 5.2 Entity/configuration changes

Update:

- `ErpWeb.Model/Entities/Planning/PrMachine.cs`
- `ErpWeb.Model/Configurations/Planning/PrMachineConfiguration.cs`

Use `decimal` for `HourlyCost`; do not introduce `double` for money.

## 5.3 Service changes

Update:

- `PrMachineEditVm`
- `PrMachineService.ListAsync`
- `PrMachineService.SaveBatchAsync`

Rules:

1. New machine defaults `Active = true`.
2. `HourlyCost >= 0`.
3. Inactive machine remains readable for historical data.
4. Do not physically delete a machine when dependencies exist; preserve current dependency checks.
5. Existing Work Orders / Product Definition snapshots are not updated by machine-master edits.

## 5.4 UI changes

Update machine grid/popup with:

```text
Machine Code
Process
Description
Machine Type
Serial No
Active
Hourly Cost
Conversion
Startup
Queue
```

Keep advanced fields out.

Suggested UX:

- Active shown as badge/check.
- Hourly Cost shown only when user has normal access to this page; no separate costing module is required now.
- Keep scheduling-time fields together in one form section.

---

# 6. Phase 2 — Add Simple Maintenance Reason Master

A reason master is required for useful summaries. Free text should remain available only in `Remark` / `Description`.

## 6.1 New entity/table

Add:

```text
PrMaintenanceReason
────────────────────────────────
ReasonCd        nvarchar(10)   PK part
Description     nvarchar(100)  NOT NULL
ReasonType      nvarchar(20)   NOT NULL
Active          bit            NOT NULL default 1
CompCode        nvarchar(10)   NOT NULL PK part
BranchCode      nvarchar(10)   NULL
LocCode         nvarchar(10)   NULL
Created         datetime2      NULL
Updated         datetime2      NULL
UserID          nvarchar(10)   NULL
UpdatedUID      nvarchar(10)   NULL
```

Allowed `ReasonType` values:

```text
PREVENTIVE
BREAKDOWN
REPAIR
SERVICE
OTHER
```

Do not create complex nested reason/category trees.

## 6.2 Recommended starter data

Do not hard-code these as permanent system values; seed or let customer maintain them:

```text
PM      Preventive Maintenance      PREVENTIVE
MECH    Mechanical                  BREAKDOWN
ELEC    Electrical                  BREAKDOWN
SENSOR  Sensor                      BREAKDOWN
BELT    Belt / Chain                REPAIR
CLEAN   Cleaning                    SERVICE
LUBE    Lubrication                 SERVICE
OTHER   Other                       OTHER
```

## 6.3 Files

Add:

- `ErpWeb.Model/Entities/Planning/PrMaintenanceReason.cs`
- `ErpWeb.Model/Configurations/Planning/PrMaintenanceReasonConfiguration.cs`
- `ErpWeb.Core/Planning/PrMaintenanceReasonService.cs`
- `ErpWeb.UI/Planning/Masters/PrMaintenanceReasonList.razor`
- `ErpWeb.UI/Planning/Masters/PrMaintenanceReasonList.razor.cs`

Add `DbSet<PrMaintenanceReason>` to `AppDbContext`.

## 6.4 Menu/security

Add:

```csharp
MenuCodes.PlanningMaintenanceReason = "PLN_MAINT_REASON";
```

Under Planning > Master:

```xml
<Menu Code="PLN_MAINT_REASON"
      Name="Maintenance Reasons"
      Route="/planning/maintenance-reasons"
      ... />
```

Use the existing menu permission framework and sync mechanism.

---

# 7. Phase 3 — Fix and Enhance `PrPreventive`

`PrPreventive` remains a **planned machine-unavailable schedule**. Do not convert it into a full maintenance work order.

## 7.1 Schema additions

Add:

```text
Status          nvarchar(15)   NOT NULL default 'PLANNED'
CompletedOn     datetime2      NULL
CompletedBy     nvarchar(20)   NULL
CompCode        nvarchar(10)   NOT NULL
BranchCode      nvarchar(10)   NULL
LocCode         nvarchar(10)   NULL
```

Allowed status values:

```text
PLANNED
COMPLETED
CANCELLED
```

Keep existing:

```text
MachineCd
DownDt
StartTm
EndTm
ReasonCd
Remark
```

Do not add recurrence rules in this milestone; the existing Range action is sufficient for SME usage.

## 7.2 Tenant-safe migration/backfill

Because old `PrPreventive` does not contain company scope:

1. Add `CompCode`, `BranchCode`, `LocCode` as nullable first.
2. Backfill from `PrMachine` only when a preventive machine can be mapped unambiguously.
3. If the database contains the same `MachineCd` under multiple companies and old preventive data cannot be uniquely assigned, **stop the migration with a clear diagnostic** instead of guessing.
4. After all rows are resolved, make `CompCode` NOT NULL.
5. Add indexes after backfill.

Do not silently assign preventive rows to an arbitrary company.

## 7.3 Entity / VM changes

Update:

- `PrPreventive`
- `PrPreventiveListRow`
- `PrPreventiveEditVm`

Expose:

```text
Machine
Date
Start Time
End Time
Reason
Status
Completed On
Completed By
Remark
```

## 7.4 Service permission correction

Current `SaveBatchAsync` effectively uses Edit permission broadly.

Align with `PrMachineService` style:

- Add -> `PermissionCodes.Add`
- Edit -> `PermissionCodes.Edit`
- Delete -> `PermissionCodes.Delete`
- List -> `PermissionCodes.Access`

All queries must use company scope.

## 7.5 Machine validation

Before save (new Preventive):

- `MachineCd` must exist under current company.
- New preventive entry requires an **active** machine (`CompCode + MachineCd + Active`).
- Existing history (including COMPLETED/CANCELLED) remains viewable/editable per permission even when the machine later becomes inactive.
- Inactive machines must not appear in NEW Preventive selection lists.

## 7.6 Time validation

Rules:

1. Start and End are required.
2. Same-day window:
   - `End > Start`.
3. Overnight window:
   - if `End <= Start`, interpret End as next day, preserving the current scheduler convention.
4. Duration must be > 0.
5. Do not allow absurd duration > 24 hours for one row.

Create one shared helper to normalize a preventive row into:

```text
WindowStart
WindowEnd
```

Use the same normalization for validation and scheduler loading.

## 7.7 Replace one-record-per-day rule with overlap validation

Allow multiple rows for the same machine/date.

Reject only when an active row (`Status != CANCELLED`) overlaps another active row for the same:

```text
CompCode + MachineCd
```

Overlap rule:

```text
newStart < existingEnd
AND
newEnd > existingStart
```

Check adjacent day rows as well because overnight windows can cross midnight.

Cancelled rows do not participate in overlap checks.

## 7.8 Range entry

Keep the existing RANGE feature.

Enhance it so every generated row contains:

- company/branch/location scope;
- StartTm / EndTm;
- Status = PLANNED;
- ReasonCd;
- Remark.

Range save is all-or-nothing:

- validate the whole requested range first;
- if any generated window conflicts, return the conflicting date/machine;
- do not partially insert the range.

## 7.9 Preventive UI

Grid columns:

```text
Machine
Date
Start
End
Reason
Status
Completed By
Remark
```

Filters:

- Machine
- Date range
- Status

Row actions:

```text
View
Edit
Complete
Cancel
Delete   (only Status=PLANNED AND no linked PrMacMaintenance)
```

### Delete rules (P1)

Physical delete is allowed **only** when:

```text
Status = PLANNED
AND no PrMacMaintenance row references PreventiveUid
```

Do **not** hard-delete:

- COMPLETED
- CANCELLED

Use Cancel to preserve scheduled history for audit/KPI. If a real FK is added (`PrMacMaintenance.PreventiveUid → PrPreventive.Uid`), use `ON DELETE NO ACTION` / RESTRICT — never cascade.

### Complete action

`Complete` should open a small popup with planned context and **actual** execution times:

```text
Machine          (read-only from preventive)
Maintenance      (reason / planned description, read-only)
Actual Start     default = preventive planned StartTm (editable)
Actual End       default = current date/time (editable)
Action Taken     optional
Parts Cost       optional
Labour Cost      optional
Other Cost       optional
Completed By     default current user
```

Completion must transactionally (via `IPrMachineMaintenanceService.CompletePreventiveAsync` only):

1. Begin transaction.
2. Load company-scoped preventive; require `Status = PLANNED` (only allowed completion transition).
3. Validate Actual Start / Actual End (End >= Start; duration > 0).
4. Ensure no maintenance row already owns that `PreventiveUid` (service check + unique filtered index).
5. Insert exactly one `PrMacMaintenance` row:
   - `MacCode` = preventive.MachineCd
   - `MType` = PREVENTIVE
   - `Status` = COMPLETED
   - `ReasonCd` = preventive.ReasonCd
   - `StartDateTime` / `EndDateTime` = Actual Start / Actual End
   - `ActionTaken` / cost fields from popup
   - `ActionBy` / `ActionOn` = current user / now
   - `PreventiveUid` = preventive.Uid
6. Mark `PrPreventive.Status = COMPLETED`; set `CompletedOn` / `CompletedBy`.
7. Save / commit.
8. Translate unique-index / concurrency conflicts into a clean business error (“Already completed” or return existing completed state). **Never** create a second history row.

If already COMPLETED: do not insert another maintenance row; return already-completed / existing state.

Do **not** implement a second Complete path inside `PrPreventiveService` that also inserts maintenance history.

SQL invariant (required in enhance script):

```sql
CREATE UNIQUE INDEX UX_PrMacMaintenance_PreventiveUid
ON dbo.PrMacMaintenance (PreventiveUid)
WHERE PreventiveUid IS NOT NULL;
```

### Cancel action

Set:

```text
Status = CANCELLED
```

Do not hard-delete normal historical preventive rows merely because the work was cancelled.

---

# 8. Phase 4 — Operationalize `PrMacMaintenance`

Enhance the existing table; do not replace it.

## 8.1 Preserve existing columns

Continue using:

```text
TrxDate
MacCode
Description
ActionTaken
Status
ReportBy
ActionBy
ActionOn
RefCode
MType
Name
Reminder
```

`RepType` may remain for legacy compatibility but does not need to drive the new UI unless a current business use is identified.

## 8.2 Add only missing facts

Add:

```text
StartDateTime    datetime2       NULL
EndDateTime      datetime2       NULL
ReasonCd         nvarchar(10)    NULL
PartsCost        decimal(19,2)   NOT NULL default 0
LabourCost       decimal(19,2)   NOT NULL default 0
OtherCost        decimal(19,2)   NOT NULL default 0
PreventiveUid    int             NULL
Remark           nvarchar(500)   NULL
CompCode         nvarchar(10)    NOT NULL
BranchCode       nvarchar(10)    NULL
LocCode          nvarchar(10)    NULL
```

Do not store:

```text
DurationMinutes
TotalCost
```

Derive them:

```text
DurationMinutes = EndDateTime - StartDateTime
TotalCost = PartsCost + LabourCost + OtherCost
```

## 8.3 Use existing `MType` as the maintenance type

Normalize values to constants (keep column `nvarchar(10)` — do **not** widen for this milestone):

```text
PREVENTIVE   (10)
BREAKDOWN    (9)
REPAIR       (6)
SERVICE      (7)
OTHER        (5)
```

Do not introduce a second duplicate `MaintenanceType` column.

Create constants in Core/Planning, e.g.:

```csharp
PrMaintenanceTypes.Preventive
PrMaintenanceTypes.Breakdown
PrMaintenanceTypes.Repair
PrMaintenanceTypes.Service
PrMaintenanceTypes.Other
```

## 8.4 Normalize status (lock before coding service/UI)

Use existing `Status` field with exactly:

```text
OPEN
IN_PROGRESS
COMPLETED
CANCELLED
```

### Field requirements by status

| Status | Required | Notes |
|---|---|---|
| OPEN | Machine, MType, Description/Problem, TrxDate (ReportedDate) | `StartDateTime` may be null |
| IN_PROGRESS | + `StartDateTime` | `EndDateTime` remains null |
| COMPLETED | + `StartDateTime`, `EndDateTime`, and `EndDateTime > StartDateTime` | also require `ActionBy` |
| CANCELLED | — | excluded from breakdown count / hours / cost |

Additional rules:

- cost fields >= 0;
- cancelled rows are excluded from maintenance-cost and downtime KPI totals (hours/cost use completed rows);
- historical completed rows remain editable only with Edit permission (no silent mutate on Complete retry).

## 8.5 Preventive link

`PreventiveUid` is nullable.

For preventive completion created from `PrPreventive`:

```text
PrMacMaintenance.PreventiveUid = PrPreventive.Uid
MType = PREVENTIVE
```

Enforce **exactly one** maintenance history row per preventive via unique filtered index on non-null `PreventiveUid` (see §7.9). Service check alone is insufficient under concurrency.

Do not require `PreventiveUid` for Breakdown / Repair / Service entered directly.

## 8.6 Tenant-safe backfill

Apply the same safe approach as `PrPreventive`:

- add scope columns nullable;
- map by machine only when unambiguous;
- stop migration if old data cannot be safely assigned;
- then make `CompCode` NOT NULL.

### Tenant authority

```text
CompCode   = tenant/security ownership; mandatory on every query predicate
BranchCode = operational metadata / filter (from write scope)
LocCode    = operational metadata / filter (from write scope)
```

On new records default:

```text
CompCode   = write.CompanyCode
BranchCode = write.BranchCode
LocCode    = write.LocationCode
```

Do not treat BranchCode/LocCode as substitutes for CompCode. For this milestone, use current write scope (no alternate branch/location picker unless already standard elsewhere).

## 8.7 Service

Add:

- `ErpWeb.Core/Planning/PrMachineMaintenanceService.cs`

Interfaces / methods:

```text
ListAsync(filter)
GetAsync(id)
CreateAsync(request)
UpdateAsync(request)
CompleteAsync(id, request)
CancelAsync(id, reason)
CompletePreventiveAsync(preventiveUid, request)
```

### Suggested filter DTO

```text
FromDate
ToDate
MachineCd
MaintenanceType
Status
ReasonCd
```

### Service invariants

- every read/write is company scoped (`CompCode` mandatory; Branch/Loc from write scope);
- machine must exist in current company;
- **new** rows require an active machine; inactive machines remain readable in history;
- reason must exist in current company and be Active for new rows;
- date/time validation is server-side;
- cost validation is server-side;
- status field requirements per §8.4;
- preventive completion updates both tables in one DB transaction with unique-index concurrency protection;
- duplicate completion of one preventive is rejected/idempotently returned;
- `RefCode` can remain as a human/reference number; do not build a complex numbering workflow unless required.

## 8.8 Maintenance UI

Add:

- `ErpWeb.UI/Planning/Maintenance/PrMachineMaintenanceList.razor`
- `ErpWeb.UI/Planning/Maintenance/PrMachineMaintenanceList.razor.cs`

Route:

```text
/planning/machine-maintenance
```

Page layout:

### Filter strip

```text
From
To
Machine
Type
Status
Reason
Search
Clear
```

### Grid

```text
Date
Machine
Type
Reason
Description
Start
End
Hours
Status
Action By
Parts
Labour
Other
Total
```

### Entry popup

Keep it straightforward:

```text
Machine *
Type *
Transaction Date *
Reason
Description / Problem *
Reported By
Start Date/Time
End Date/Time
Action Taken
Action By
Status
Parts Cost
Labour Cost
Other Cost
Remark
```

Do not add tabs unless the form genuinely becomes too long.

## 8.9 Existing maintenance images

`PrMacMaintenanceImages` may remain available but is **not required for completion of this milestone**.

If exposed now, attach images using the existing `RefCode` relationship with the project’s normal attachment-security rules. Do not redesign the table purely for this enhancement.

---

# 9. Phase 5 — Integrate Preventive Status with Production Scheduling

File:

- `ErpWeb.Core/Production/ProductionCalendarScheduleDataLoader.cs`

Current scheduling architecture is correct and must remain:

```text
Machine Calendar
    - Preventive unavailable windows
    = usable machine calendar
```

## Required changes

Preventive query must include:

```text
CompCode == requested company
MachineCd == requested machine
Status == PLANNED
```

| Status | Blocks production schedule |
|---|---|
| PLANNED | Yes |
| COMPLETED | No |
| CANCELLED | No |

Once preventive maintenance is completed, the machine becomes available again to the scheduler.

The loader must continue to include the previous/next day around the horizon for overnight spill.

Use the shared preventive window normalizer so UI validation and scheduling interpret overnight times identically.

### Important rule

Do not use `PrMacMaintenance` directly to rewrite already-planned machine calendars in this milestone.

Why:

- `PrPreventive` = planner-known planned downtime;
- `PrMacMaintenance` = actual maintenance/breakdown history.

Completing PM makes the machine available again for **subsequent** Preview / Create / Recalculate scheduling paths. It does **not** automatically reschedule already-persisted Work Orders.

A future real-time rescheduling enhancement can react to an open breakdown, but it is outside this milestone.

---

# 10. Phase 6 — Machine Maintenance Summary / KPI v1

This must remain a **simple ERP management summary**, not a MES dashboard.

## 10.1 Menu

Add a new Planning group if desired:

```text
Planning
  Master
  Transactions
  Inquiry
      Machine Maintenance Summary
```

Recommended codes:

```csharp
MenuCodes.PlanningInquiry = "PLN_INQUIRY";
MenuCodes.PlanningMachineMaintenance = "PLN_MAC_MAINT";
MenuCodes.PlanningMachineSummary = "PLN_MAC_SUMMARY";
```

`PLN_MAC_MAINT` belongs under Transactions.

`PLN_MAC_SUMMARY` belongs under Inquiry.

## 10.2 Service

Add:

- `ErpWeb.Core/Planning/PrMachineMaintenanceSummaryService.cs`

Input:

```text
Company scope
FromDate
ToDate
MachineCd optional
MachineType optional
```

## 10.3 KPI cards

Only show KPIs that can be defended from the captured facts:

```text
Scheduled PM
Completed PM
Pending PM
Overdue PM
Breakdowns
Breakdown Hours
Maintenance Hours
Maintenance Cost
```

Definitions (management-facing; do **not** use “Planned = Status PLANNED only”, which understates scheduled volume):

**Period basis lock (P1):** all four PM cards share one population — preventive rows whose **planned** date/window (`DownDt` / planned `WindowStart`–`WindowEnd`) falls in the selected range. Do **not** filter PM cards by actual completion datetime.

```text
Scheduled PM
= planned window/DownDt in selected range
  AND Status IN (PLANNED, COMPLETED)
  (exclude CANCELLED)

Completed PM
= same scheduled-period population
  AND Status = COMPLETED

Pending PM
= same scheduled-period population
  AND Status = PLANNED
  AND planned WindowEnd >= now

Overdue PM
= same scheduled-period population
  AND Status = PLANNED
  AND planned WindowEnd < now

Breakdowns
= non-cancelled PrMacMaintenance where MType = BREAKDOWN
  (filter by actual maintenance period — StartDateTime/EndDateTime or TrxDate as documented in summary service)

Breakdown Hours
= sum actual duration for completed BREAKDOWN rows in the actual maintenance period

Maintenance Hours
= sum actual duration for completed maintenance rows in the actual maintenance period

Maintenance Cost
= PartsCost + LabourCost + OtherCost for completed maintenance in the actual maintenance period
```

Example (PM scheduled 31-Oct, completed 01-Nov; October summary):

```text
Scheduled PM includes it
Completed PM includes it (status COMPLETED, planned in October)
October Maintenance Hours for that row = excluded if actual work was in November
```

Example card totals:

```text
Scheduled PM       10
Completed PM        7
Pending PM          2
Overdue PM          1
Breakdowns          3
Breakdown Hours     6.5
Maintenance Cost    1280
```

Do not show a percentage KPI when its denominator is not clearly defined.
Do not mix planned date and actual completion date invisibly.

## 10.4 Summary grid

Group by machine:

| Machine | Scheduled | Done | Pending | Overdue | Breakdowns | Maint. Hours | Breakdown Hours | Cost |
|---|---:|---:|---:|---:|---:|---:|---:|---:|

Support drill-through to filtered maintenance list when practical.

## 10.5 Reason summary

A second small section:

| Reason | Type | Count | Hours | Cost |
|---|---|---:|---:|---:|

This is why `ReasonCd` must be controlled by a reason master rather than being only free text.

## 10.6 Do not show yet

Do not implement these until actual production execution/runtime is reliably posted:

```text
Machine Utilization
Efficiency
OEE
MTBF
MTTR
Actual vs Standard Cycle Efficiency
Production Hours
```

The current Work Order model has strong planned data, but this milestone must not present planned data as actual machine runtime.

---

# 11. Database Indexes / Constraints

Use SQL Server-friendly indexes matching current project style.

## `PrMachine`

Suggested:

```text
IX_PrMachine_Company_Active_Process
(CompCode, Active, Process_Cd)
```

Preserve the existing key / uniqueness rules.

## `PrPreventive`

Suggested:

```text
IX_PrPreventive_Company_Machine_Date
(CompCode, Machine_Cd, Down_Dt)

IX_PrPreventive_Company_Status_Date
(CompCode, Status, Down_Dt)
```

Do not create a unique `(MachineCd, DownDt)` index.

## `PrMaintenanceReason`

```text
PK / UQ: (CompCode, ReasonCd)
IX: (CompCode, Active, ReasonType)
```

## `PrMacMaintenance`

Suggested:

```text
IX_PrMacMaintenance_Company_Machine_Date
(CompCode, MacCode, TrxDate)

IX_PrMacMaintenance_Company_Type_Date
(CompCode, MType, TrxDate)

IX_PrMacMaintenance_Company_Status_Date
(CompCode, Status, TrxDate)

IX_PrMacMaintenance_PreventiveUid
(PreventiveUid)

-- REQUIRED unique filtered index (P1 concurrency):
UX_PrMacMaintenance_PreventiveUid
(PreventiveUid) WHERE PreventiveUid IS NOT NULL
```

Add appropriate CHECK constraints:

```text
PartsCost >= 0
LabourCost >= 0
OtherCost >= 0
EndDateTime IS NULL OR StartDateTime IS NULL OR EndDateTime >= StartDateTime
```

The unique filtered index is the database invariant for one history row per preventive; service logic must still translate collisions into a clean business error.

---

# 12. SQL Deployment Scripts

Follow the existing repository pattern (`preflight-pr-shift-break.sql` / `alter-pr-shift-break.sql` / rollback):

```text
scripts/preflight-production-machine-maintenance.sql
scripts/enhance-production-machine-maintenance.sql
scripts/rollback-production-machine-maintenance.sql
```

Use:

```text
OBJECT_ID
COL_LENGTH
sys.indexes
sys.check_constraints
```

### Preflight must detect

- duplicate `MachineCd` inside company (`GROUP BY CompCode, Machine_Cd HAVING COUNT(*) > 1`)
- `PrPreventive` / `PrMacMaintenance` machine not found in `PrMachine`
- ambiguous company backfill candidates
- invalid existing `MType` / `Status` samples
- invalid preventive Start/End or legacy year-0001 time values

**Never guess CompCode during migration.** If mapping is ambiguous, stop and report the rows.

### Enhance sections

1. Enhance `PrMachine` (Active first; P3 columns optional in same script).
2. Create `PrMaintenanceReason`.
3. Add nullable tenant/status/completion fields to `PrPreventive`.
4. Backfill `PrPreventive` scope safely.
5. Add preventive indexes/checks.
6. Enhance `PrMacMaintenance`.
7. Backfill maintenance scope safely.
8. Add maintenance indexes/checks.
9. Seed optional starter reason codes only if no rows exist for the company or via a separate seed block.
10. Validation queries at end.

Do not drop or rename legacy columns in the first deployment.

---

# 13. Menu / Permission Changes

Files:

- `ErpWeb.Core/Menus/MenuCodes.cs`
- `ErpWeb/Menus/menus.xml`

Add:

```text
PLN_MAINT_REASON
PLN_MAC_MAINT
PLN_INQUIRY
PLN_MAC_SUMMARY
```

Recommended navigation:

```text
Planning
├── Master
│   ├── Machines
│   ├── Machine Preventive
│   └── Maintenance Reasons
│
├── Transactions
│   ├── Work Orders
│   └── Machine Maintenance
│
└── Inquiry
    └── Machine Maintenance Summary
```

Keep `Machine Preventive` under Master for now to avoid unnecessary menu movement/regression, even though functionally it behaves like a planned calendar transaction.

---

# 14. Service Registration

Update:

- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`

Register:

```text
IPrMaintenanceReasonService
IPrMachineMaintenanceService
IPrMachineMaintenanceSummaryService
```

Keep current:

```text
IPrMachineService
IPrPreventiveService
```

No new repository layer is required unless implementation shows repeated query complexity that justifies it; the current Planning services already use `IDbContextFactory<AppDbContext>` directly.

---

# 15. Production / Product Definition Integration Rules

## Machine Active flag

Application already enforces one process per `CompCode + MachineCd`. Evaluate Active with:

```text
CompCode == currentCompany && MachineCd == machineCode && Active
```

For new Product Definition / new Draft machine selection:

- inactive machines should not be offered as normal selectable options;
- existing Product Definitions referencing an inactive machine must remain readable;
- existing frozen Work Order snapshots must remain unchanged;
- do not delete historical machine references.

If changing Product Definition filtering is risky for this milestone, implement Active filtering first only in `PrMachine` selection popups and leave a warning for Product Definition; do not break existing definitions.

## Hourly Cost

`PrMachine.HourlyCost` is a machine-master default/reference value only.

If a Product Definition machine option already stores a specific machine rate, that Product Definition value remains authoritative for the Work Order snapshot.

Do not make Work Order historical cost dependent on the current machine master.

---

# 16. Validation / Concurrency / Audit

Follow existing Planning service patterns:

- normalize codes with `PlanningCodeNormalizer`;
- server-side validate all codes and dates;
- company scope every query;
- populate Created / Updated / User fields;
- use transactions where multiple rows/tables change;
- preventive completion + maintenance history insert is one transaction;
- do not silently swallow concurrency conflicts.

For legacy entities without `RowVersion`, use the existing `Updated` timestamp pattern where appropriate. Do not introduce an inconsistent partial RowVersion strategy unless the whole affected aggregate is migrated deliberately.

---

# 17. Required Tests

Keep tests focused; this is not a request for a huge test framework.

## Machine

- create active machine with new fields;
- reject negative HourlyCost;
- inactive machine remains visible for history.

## Preventive

- create same-day non-overlapping windows -> allowed;
- overlapping windows -> rejected;
- overnight overlap into next date -> rejected;
- cancelled row does not block new window;
- Range operation is atomic;
- company A cannot read/edit company B preventive rows;
- scheduler ignores CANCELLED and COMPLETED preventive rows;
- scheduler respects only PLANNED preventive rows;
- ordinary New/Edit preserves StartTm/EndTm;
- two concurrent CompletePreventive calls -> exactly one PrMacMaintenance; second returns already-completed/conflict;
- hard-delete allowed only for PLANNED with no linked history; COMPLETED/CANCELLED/linked -> rejected.

## Maintenance

- create Breakdown row (OPEN);
- IN_PROGRESS requires StartDateTime;
- completed row requires valid end > start;
- negative costs rejected;
- company scoping enforced;
- total cost derived correctly;
- cancelled maintenance excluded from summary;
- preventive completion creates exactly one linked maintenance row;
- repeated Complete call cannot create duplicate history;
- inactive machine: new create rejected; existing history still readable.

## Summary

Using fixed sample facts, verify:

- Scheduled / Completed / Pending / Overdue PM counts on **planned** period basis;
- KPI boundary: scheduled 31-Oct / completed 01-Nov — October Scheduled+Completed include it; October Maintenance Hours exclude Nov actual work;
- breakdown count;
- maintenance hours;
- breakdown hours;
- maintenance cost;
- machine grouping;
- reason grouping;
- cancelled excluded from Scheduled and cost/hours;
- same MachineCd in two companies never cross tenant scheduler/summary.

---

# 18. Manual UAT Scenarios

## UAT 1 — Simple preventive maintenance

```text
Machine: CNC01
Date: 15-Oct-2026
Start: 09:00
End: 11:00
Reason: PM
Status: PLANNED
```

Expected:

- shown on Preventive page;
- scheduler does not place machine work inside 09:00-11:00;
- Complete action creates PREVENTIVE maintenance history;
- status becomes COMPLETED.

## UAT 2 — Two preventive windows same day

```text
09:00-10:00 Lubrication
17:00-18:00 Cleaning
```

Expected: both accepted.

## UAT 3 — overlapping preventive

Existing:

```text
09:00-10:00
```

New:

```text
09:30-10:30
```

Expected: rejected with understandable conflict message.

## UAT 4 — breakdown entry

```text
Machine: PRESS01
Type: BREAKDOWN
Reason: ELEC
Start: 10:15
End: 12:00
Problem: Motor trip
Action: Replaced contactor
Parts: 180
Labour: 80
Other: 0
Status: COMPLETED
```

Expected:

```text
Breakdown count +1
Breakdown hours +1.75
Maintenance hours +1.75
Maintenance cost +260
```

## UAT 5 — cancelled preventive

Expected:

- remains in history as CANCELLED;
- does not block production scheduling;
- does not participate in overlap validation;
- not hard-deletable;
- excluded from Scheduled / Completed / Pending / Overdue counts.

## UAT 6 — tenant isolation

Same `MachineCd` exists in two companies.

Expected:

- each company sees only its own preventive/maintenance/reason data;
- scheduler uses only the requested company’s preventive windows;
- Company A summary never includes Company B maintenance.

## UAT 7 — Complete concurrency

Two users Complete the same PLANNED preventive nearly simultaneously.

Expected:

- exactly one `PrMacMaintenance` with that `PreventiveUid`;
- preventive ends COMPLETED;
- second request returns already-completed / conflict (no duplicate history).

## UAT 8 — delete protection

Expected:

- PLANNED with no history → deletable;
- COMPLETED → not hard-deletable;
- CANCELLED → not hard-deletable;
- any linked `PreventiveUid` → not hard-deletable.

## UAT 9 — KPI period boundary

Preventive planned 31-Oct; completed with Actual End 01-Nov.

October summary expected:

- Scheduled PM includes it;
- Completed PM includes it;
- October Maintenance Hours for that actual window excluded (Nov actual period).

## UAT 10 — inactive machine history

Create maintenance while machine Active; deactivate machine.

Expected:

- old history still readable;
- new Preventive/Maintenance selection rejects inactive machine.

---

# 19. Recommended Implementation Order

Implement in this order to minimize regression risk. **P1 items must be done before production use.**

### Step 1 — database/entity foundation (P1 tenant + concurrency + P2 Active)

- preflight / enhance / rollback SQL;
- `PrMachine.Active` (+ optional P3 columns);
- `PrMaintenanceReason`;
- `PrPreventive` tenant + status + completion fields;
- `PrMacMaintenance` tenant + actual times + costs + PreventiveUid;
- **unique filtered index** `UX_PrMacMaintenance_PreventiveUid`;
- EF configurations / DbSets;
- optional FK PreventiveUid → PrPreventive.Uid with `ON DELETE NO ACTION`.

### Step 2 — reason master (P2)

- service; menu; simple CRUD UI; active-reason validation API used by later screens.

### Step 3 — machine master Active (P2) / optional P3 fields

- VM/service/UI Active (+ optional MachineType/SerialNo/HourlyCost);
- selection filtering by Active; history remains readable when inactive.

### Step 4 — preventive correction (P1)

- tenant scope on all queries (`CompCode` authority; Branch/Loc from write scope);
- Start/End UI + server validation;
- permission split;
- overlap logic + shared overnight normalizer;
- status PLANNED/COMPLETED/CANCELLED;
- Cancel action;
- delete protection (PLANNED + no link only);
- Range atomic validation;
- Complete UI triggers maintenance service only (no dual write).

### Step 5 — scheduler integration (P1)

- company scope;
- subtract **PLANNED only**;
- shared overnight normalization;
- regression tests (COMPLETED/CANCELLED do not block; no auto-reschedule of persisted WOs).

### Step 6 — machine maintenance transaction (P2 core + P1 Complete)

- `IPrMachineMaintenanceService` with `CompletePreventiveAsync` as sole Complete owner;
- concurrency/idempotency + unique-index conflict translation;
- CRUD/list UI with OPEN/IN_PROGRESS/COMPLETED/CANCELLED rules;
- costs/duration;
- reason + active-machine validation (history readable when inactive).

### Step 7 — maintenance summary (P2)

- Scheduled / Completed / Pending / Overdue on **planned** period basis;
- Hours/Cost on **actual** maintenance period;
- by-machine grid; reason summary; drill-through if straightforward.

### Step 8 — final regression

Verify Product Definition machine selection, Work Order create/preview/recalculate, machine calendars, preventive scheduler windows, multi-tenant scoping, legacy maintenance rows, Complete concurrency, delete protection, KPI period boundary.

---

# 20. Do-Not-Break Invariants

1. `PrPreventive` remains the planned unavailable-window source used by production scheduling.
2. Only `Status == PLANNED` preventive windows reduce available machine capacity.
3. Completing PM does not automatically reschedule already-persisted Work Orders.
4. Existing Work Order snapshots remain frozen historical facts.
5. Machine master edits never rewrite existing Work Order machine snapshots.
6. Product Definition machine cycle/output rules remain authoritative for product-specific cycle calculations.
7. Maintenance history does not become a hidden source of real-time rescheduling in this milestone.
8. Cancelled and completed preventive windows do not reduce available machine capacity.
9. All new maintenance/preventive queries are company scoped; CompCode is never guessed on migration.
10. Do not force advanced manufacturing KPI from data the ERP has not actually captured.
11. `CompletePreventiveAsync` is the only writer that creates preventive-linked maintenance history.
12. At most one `PrMacMaintenance` row per non-null `PreventiveUid` (unique filtered index).
13. Hard-delete Preventive only when PLANNED and unlinked; never cascade-delete history.

---

# 21. Final Target Data Flow

```text
PrMachine
   │
   ├── Machine master / process / status / simple cost
   │
   ├───────────────┐
   │               │
   ▼               ▼
PrPreventive    Product Definition
   │               │
   │               ▼
   │            Work Order Machine Snapshot
   │               │
   ▼               ▼
Machine Calendar / Scheduler


PrPreventive --Complete--> PrMacMaintenance
                               │
Direct Breakdown/Repair ------┤
                               │
                               ▼
                    Machine Maintenance Summary
                    - PM planned / completed / overdue
                    - breakdown count / hours
                    - maintenance hours
                    - maintenance cost
                    - reason analysis
```

---

# 22. Final Recommendation

This plan is intentionally **not a full maintenance-management product**.

For the current ERP, the right level is:

```text
Machine Master
+ Machine Calendar
+ Preventive Schedule (PLANNED capacity blocks)
+ Maintenance / Breakdown History
+ Maintenance Reason
+ Simple Cost / Downtime Summary (Scheduled / Completed / Pending / Overdue)
```

That is enough to make the Production module practical for normal SME manufacturing use and gives the system clean historical facts for later reports, without forcing SAP/MES complexity into the product.

### Highest-priority (P1) before production use / before coding Complete path

1. tenant-scope Preventive/Maintenance (`CompCode` authoritative; Branch/Loc metadata only);
2. fix Preventive Start/End handling (UI + server);
3. allow multiple non-overlapping preventive windows per day + shared overnight normalizer;
4. PLANNED / COMPLETED / CANCELLED + scheduler subtracts **PLANNED only**;
5. atomic Complete Preventive → Maintenance with actual Start/End;
6. Complete concurrency/idempotency + unique filtered index on `PreventiveUid`;
7. Preventive delete/history protection;
8. PM KPI selected-period basis (planned for PM cards; actual for hours/cost).

### Next (P2)

9. operationalize `PrMacMaintenance` CRUD UI + OPEN/IN_PROGRESS/COMPLETED/CANCELLED lifecycle;
10. standardize maintenance reasons;
11. add Machine Maintenance Summary;
12. `PrMachine.Active` + inactive-machine historical readability;
13. preflight/enhance/rollback SQL packaging.

### Later (P3)

14. SerialNo / MachineType / HourlyCost and similar reference fields;
15. wording/UX clarifying completion does not auto-reschedule existing Work Orders.

OEE/IoT/predictive features remain outside the roadmap until actual customers request them.

**This document is the single authoritative implementation plan.** Do not reconcile against older review drafts for coding decisions.
