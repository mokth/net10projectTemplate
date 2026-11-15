# productionv2 MACHINE SCHEDULING
# FINAL STRICT AI CODING AGENT PLAN v10
## CURRENT-REPOSITORY RECONCILIATION AFTER b327f92

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Current HEAD reviewed:** `b327f92a4e0b21751dfaa6e0ac1403d433bf321a`  
**Commit:** `checked in`  
**Parent:** `04ed9920ab7f724a3869b334d0256a0edac22c13`  
**Supersedes:** `productionv2_MACHINE_SCHEDULING_FINAL_STRICT_AI_CODING_AGENT_PLAN_v9_CURRENT_REPO_RECONCILIATION.md`

**Purpose:** This is the current implementation work order for an AI coding agent.  
It is NOT a green-field plan anymore.  
A substantial pre-v8 implementation already exists at `b327f92…`, so the agent must preserve useful work and reconcile the remaining architecture/UX/safety gaps.

**Production status:** NOT APPROVED FOR ADVANCED WRITE ENABLEMENT.  
`Planning.MACHINE_SCHEDULE_SEGMENT_WRITE_ENABLED` must remain **OFF** until all mandatory reconciliation and regression gates below pass.

---

# 0. REQUIRED AI IMPLEMENTATION MODEL — GPT-6 SOL HIGH

This plan is intended to be implemented using:

```text
GPT-6 Sol
Thinking level: HIGH
```

## Mandatory model rule

The coding agent MUST use **GPT-6 Sol High** for implementation of this plan.

Do NOT automatically downgrade implementation work to:

```text
Instant
Medium
Fast / low-reasoning mode
```

without explicit user approval.

If the platform/model selector changes during implementation, the agent must state the active model/mode before continuing a critical phase.

## Why High is required

This plan contains repository-wide changes involving:

- SQL Server schema and deployment scripts;
- finite-capacity scheduling;
- multi-WO atomic planning;
- concurrency and applock ordering;
- Work Order snapshot lifecycle;
- UOM/cycle conversion;
- Daily Production interaction;
- maintenance capacity;
- allocation/currentness provenance;
- Blazor/DevExpress stateful UI;
- stock/WIP/FG/costing regression protection.

These are cross-file and cross-transaction concerns. High reasoning is required to reduce the risk of locally-correct but system-incorrect changes.

## Execution policy for GPT-6 Sol High

The agent MUST work **phase by phase**.

Do NOT run the whole plan as one giant autonomous implementation.

Required workflow:

```text
Read current repo
→ implement ONE phase
→ build
→ run named tests
→ inspect failures
→ fix phase
→ produce phase report
→ STOP
→ continue only when the user approves or explicitly asks to continue
```

For critical phases, start from a fresh or sufficiently clean coding context when practical:

```text
Phase B — SegmentSet/schema/link architecture
Phase C — capacity/currentness/provenance
Phase D — UOM/cycle/productive-time split
Phase E — DP REPLAN_REQUIRED
Phase F — Reopen/SnapshotHash safety
Phase I — SQL Server concurrency/performance
```

The agent must re-read the relevant current repository files at the start of each critical phase rather than relying only on earlier context.

## High-risk-phase rule

For Phases C, D, E and F:

- do not simplify the plan architecture;
- do not invent shortcuts to pass tests;
- do not change stock/WIP/FG/costing logic for scheduling convenience;
- do not weaken concurrency or readiness gates;
- do not silently change snapshot semantics;
- if an invariant cannot be proven, STOP and report the blocker.

Especially for Phase F:

```text
If safe manufacturing snapshot integrity cannot be proven,
BLOCK advanced Reopen.

Do NOT blindly recompute SnapshotHash.
```

## Context-size rule

If the coding context becomes too large, fragmented, or uncertain:

1. finish the current atomic change if safe;
2. commit/save the work;
3. summarize:
   - current HEAD,
   - files changed,
   - tests passed/failed,
   - unresolved blockers,
   - next exact phase;
4. start the next phase with a clean context.

Do not continue coding from a context where the agent is no longer certain which repository state or plan phase is authoritative.

## Required phase header

At the start of every implementation phase, the agent should state:

```text
Model: GPT-6 Sol High
Repository: mokth/net10projectTemplate
Branch: productionv2
Current HEAD: <sha>
Plan: v10
Phase: <phase>
Advanced write flag: OFF
```

`MACHINE_SCHEDULE_SEGMENT_WRITE_ENABLED` must remain OFF until the final plan gates pass.

---

# 1. Target behavior remains unchanged

The required machine planning behavior is still:

```text
M01 — Monday

08:00–10:00   WO1 Part 1
10:30–12:30   WO2
13:00–15:00   WO3
15:30–18:00   WO1 Resume
```

Rules:

- many WOs may run on one machine on the same date;
- one WO may appear in several independent segments;
- productive capacity is interval-based;
- outer WO/machine start→completion is summary only;
- final committed productive allocations must never overlap;
- temporary overlap is allowed only in client-side Draft Reschedule Mode.

---

# 2. Current repository reality at b327f92

The current commit is no longer the old whole-operation-only implementation.

## KEEP — useful implementation already present

The AI agent MUST preserve and build on:

```text
MachineScheduleAdvancedPlanningService
MachineScheduleAdvancedPlanningService.Persist
MachineScheduleAdvancedPlanBuilder
MachineScheduleAdvancedPlanBuilder.Actions
MachineScheduleAdvancedPlanBuilder.Projection
MachineScheduleAdvancedPlacement
MachineScheduleAdvancedModels
MachineScheduleDateRangeProjector
MachineScheduleSegmentCalculator
MachineScheduleSegmentBackfillService
MachineScheduleSegmentPersistence
MachineDowntimeWindowReader
ProductionMachineCapacityService segment-aware validation
ProductionWorkOrderExecutionLock.AcquireManyAsync
Planning.MACHINE_SCHEDULE_SEGMENT_WRITE_ENABLED
ProductionWorkOrder Release segment dual-write
MachineScheduleRescheduleService segment dual-write
PrMachineMaintenance capacity integration
current PreviewToken/concurrency foundations
existing advanced service tests
```

Also KEEP:

- company-wide physical machine capacity;
- existing allocation-table capacity authority;
- current Release/reschedule machine locks;
- current scheduling-source lock order;
- current snapshot freeze during Released/InProgress schedule-only writes;
- current DR/date projection foundations;
- current maintenance/blocking reader;
- current SQL Server concurrency tests.

Do NOT throw these away.

---

# 3. Current implementation differs materially from v8

Current schema uses:

```text
PrMachineScheduleSegment
    directly linked by
PrMachineScheduleAllocation.MachineScheduleSegmentID
```

and also adds:

```text
PrProductionOutput.MachineScheduleSegmentID
```

There is NO:

```text
PrMachineScheduleSegmentSet
PrMachineScheduleAllocationSegment
SegmentSetID
IsCurrent segment-set header
FULL_PLAN / REMAINING_ONLY plan mode
```

Current active-segment identity is effectively based on:

```text
SegmentStatus != SUPERSEDED
+
MachineScheduleRevision
```

This is a pre-v8/flat implementation.

---

# 4. Critical decision for v9

Do NOT continue adding more features on top of the flat persistence model without first reconciling currentness/history semantics.

The final persistence model should be:

```text
PrMachineScheduleSegmentSet
        ↓
PrMachineScheduleSegment
        ↓
PrMachineScheduleAllocationSegment
        ↓
existing PrMachineScheduleAllocation
```

The existing allocation table remains the physical-capacity authority.

The segment-set layer answers:

- which segment collection is the current planning truth;
- FULL_PLAN vs REMAINING_ONLY;
- NORMAL vs HELD_UNSCHEDULED vs REPLAN_REQUIRED;
- combined horizon;
- combined source hash;
- one current set per selected WorkOrderMachine;
- stable historical plan identity across Reopen/re-release/replan.

---

# 5. Phase 0 — baseline current b327f92 before refactor

Before any persistence refactor:

1. fetch current HEAD;
2. confirm it is based on or newer than `b327f92…`;
3. run full build;
4. run current machine scheduling tests;
5. record pass/fail baseline;
6. preserve screenshots/current planner behavior.

At minimum run:

```text
MachineSchedulePlannerServiceTests
MachineScheduleRescheduleServiceTests
ProductionMachineCapacityServiceTests
ProductionWorkOrderServiceTests
ProductionWorkOrderSqlServerConcurrencyTests
MachineSchedulePlannerInputTests
MachineSchedulePlannerSelectionContractTests
relevant Daily Production Post/Rollback tests
```

STOP if baseline failures are unexplained.

---

# 6. Phase 1 — introduce SegmentSet without breaking current data

Add:

```text
ProductionMachineScheduleSegmentSet
dbo.PrMachineScheduleSegmentSet
```

Recommended fields:

```text
UID                    bigint identity PK
CompanyCode            nvarchar(5)
BranchCode             nvarchar(5)

WorkOrderID             bigint
WorkOrderOperationID    bigint
WorkOrderMachineID      bigint
MachineCode             nvarchar(30)

Mode                    nvarchar(20)
PlanningState           nvarchar(30)

IsCurrent               bit

PlannedQtyBasis         decimal(18,4)
OutputUOM               nvarchar(10)

CalendarHorizonStart    datetime2 NULL
CalendarHorizonEnd      datetime2 NULL
ScheduleSourceHash      char(64) NULL

MachineScheduleRevision int
Reason                  nvarchar(500) NULL

CreatedDate/CreatedBy
ModifiedDate/ModifiedBy
SupersededDate/SupersededBy
RowVersion              rowversion
```

Modes:

```text
FULL_PLAN
REMAINING_ONLY
```

Planning states:

```text
SCHEDULED
HELD_UNSCHEDULED
MAINTENANCE_UNSCHEDULED
REPLAN_REQUIRED
COMPLETED
```

Constraint:

```text
only one IsCurrent=1 per WorkOrderMachineID
```

Use a filtered unique index.

---

# 7. Phase 1 — attach current flat segments to SegmentSet

Extend `PrMachineScheduleSegment` with:

```text
SegmentSetID bigint NOT NULL
```

Do NOT use `MachineScheduleRevision` alone as current-set identity after migration.

For each selected machine:

- current non-superseded flat segments become one current SegmentSet;
- historical/superseded segments attach to historical SegmentSets when reconstructable;
- if history cannot be safely grouped, preserve it but mark migration finding rather than guessing.

After migration:

```text
SegmentSet.IsCurrent
```

is current-plan authority.

`SegmentStatus` remains per-piece lifecycle/audit state.

---

# 8. Replace direct allocation SegmentID with link table

Add:

```text
ProductionMachineScheduleAllocationSegment
dbo.PrMachineScheduleAllocationSegment
```

Fields:

```text
UID
AllocationID
SegmentID
CreatedDate
CreatedBy
RowVersion
```

Unique:

```text
AllocationID
```

A current productive allocation belongs to exactly one business segment.

Then:

1. backfill link rows from current `PrMachineScheduleAllocation.MachineScheduleSegmentID`;
2. validate one-to-one links;
3. change all new code to read/write through link table;
4. keep old column temporarily nullable for transition;
5. after compatibility period and tests, stop writing the old column;
6. physical drop of old column is OPTIONAL and should be a later reviewed cleanup, not required for initial safe rollout.

Do NOT perform a destructive drop in the same deployment that introduces the new architecture.

---

# 9. Daily Production segment link — stop using it as scheduler authority

Current repo added:

```text
ProductionOutput.MachineScheduleSegmentId
```

Do NOT make this field authoritative.

First-release v10 rule:

- keep nullable column for backward compatibility if already deployed;
- do not require it in Create/Post/Rollback;
- do not auto-assign a segment unless the exact business execution segment is unambiguous;
- do not use it to calculate RemainingQty;
- do not use it to freeze exact actual machine time.

A later traceability project may formalize operator-selected execution segments.

---

# 10. Combined schedule provenance — one hash per current SegmentSet

Current advanced persistence computes a union machine hash and writes it to machine/operation/allocations, which is useful.

Formalize this at SegmentSet level:

```text
SegmentSet.ScheduleSourceHash
Machine.ScheduleSourceHash
Operation.ScheduleSourceHash
every current Allocation.ScheduleSourceHash
```

must all match for the complete current machine schedule.

Per-segment hashes are supplementary only.

Do NOT let each current segment independently define machine provenance.

The current SegmentSet owns:

```text
combined calendar horizon
combined schedule-source hash
machine schedule revision
```

---

# 11. Capacity authority after reconciliation

Capacity continues to come from:

```text
current PrMachineScheduleAllocation fragments
```

never:

```text
Machine.PlannedStartDateTime → Machine.PlannedCompletionDateTime
```

For segment-aware current plans:

```text
Current SegmentSet
→ current Segments
→ allocation-segment links
→ current Allocation fragments
```

Validate:

- one current SegmentSet;
- valid current machine revision;
- every productive allocation links to a current segment;
- every current segment with scheduled capacity has ≥1 fragment;
- no fragment lies outside its segment;
- no current company+machine overlap;
- historical SegmentSets consume no capacity.

---

# 12. Intentional zero-allocation states

The implementation must explicitly support current SegmentSets with no productive allocations for:

```text
HELD_UNSCHEDULED
MAINTENANCE_UNSCHEDULED
COMPLETED
```

Do NOT treat these as corrupt merely because allocation count is zero.

For such states:

```text
ForecastCompletion = null
```

when future completion is genuinely unknown.

Do NOT reconstruct fake capacity from old outer dates.

---

# 13. DP Post/Rollback schedule consistency — missing in current repo

Current DP Post/Rollback must remain focused on production/stock/costing.

Do NOT insert scheduler writes into the posting transaction.

After DP facts change, scheduler read/preview must evaluate:

```text
RemainingQty
vs
FuturePlannedQty
```

If:

```text
RemainingQty > FuturePlannedQty
```

then current SegmentSet becomes logically:

```text
REPLAN_REQUIRED
```

Behavior:

- existing historical/future reservations remain visible;
- planner displays `Replan Remaining required`;
- no new Insert/Split/Move affecting that machine/operation may commit until repaired;
- `Replan Remaining` becomes the safe repair action.

If:

```text
RemainingQty == 0
```

future unused allocations may be released only through a controlled scheduling operation, not inside DP posting.

---

# 14. UOM-safe segment cycle math — current calculator is insufficient

Current `MachineScheduleSegmentCalculator.ComputeQuantity` directly uses:

```text
outputQty / OutputPerCycle
```

This is unsafe when:

```text
Operation.PlannedOutputUOM
!=
Machine.OutputPerCycleUOM
```

Refactor segment quantity calculation to reuse the same conversion contract as:

```text
WorkOrderQuantityCalculator
```

Use:

```text
IUomConversionService
```

to calculate machine-required output before cycle count.

The same rounding/precision rules must be used.

Mandatory test:

```text
operation UOM = BOX
machine OutputPerCycle UOM = PCS
1 BOX = 12 PCS
```

Split/replan cycle count must exactly match canonical Work Order quantity calculation.

---

# 15. Cycle-safe split must use productive time, not wall-clock elapsed time

Current:

```text
SnapToCycleBoundary(productiveStart, requested, cycleSeconds)
```

uses elapsed wall-clock time from one timestamp.

That is wrong across:

- lunch;
- shift breaks;
- shift end;
- off day;
- holiday;
- preventive;
- breakdown.

Replace with productive-fragment-aware logic:

```text
actual productive intervals
→ accumulate productive seconds
→ resolve completed cycle slots
→ map slot boundary back to real timestamp
```

Example:

```text
11:00–12:00 productive
12:00–13:00 lunch
13:00–15:00 productive
```

A 2-hour productive boundary from 11:00 resolves to 14:00, not 13:00.

---

# 16. CycleSeconds = 0 compatibility

Current repository allows machine schedules whose positive duration comes from fixed setup/conversion/queue even when:

```text
CycleSeconds = 0
```

Do NOT reintroduce the old zero-cycle Release blocker.

Rules:

- normal Release/simple move remains allowed if canonical productive duration is positive;
- time-based partial split by cycle boundary is NOT supported when no positive cycle-slot duration exists;
- show:
  ```text
  This machine has no positive cycle duration.
  Partial time-based split is unavailable; move the whole job or correct the machine standard.
  ```

---

# 17. Reopen — current implementation must be changed

Current `Reopen.cs` for advanced segments:

```text
ScheduleAsync(order)
→ ComputeSnapshotHash(order)
→ SnapshotRevision++
```

This is NOT sufficiently safe.

No execution does not prove that unrelated manufacturing snapshot fields were not changed.

Final rule:

## Simple one-segment schedule

May follow the normal existing Reopen lifecycle after proving current snapshot integrity.

## Advanced schedule

Fail closed by default unless a manufacturing-integrity verifier proves that:

- BOM facts match frozen released snapshot/source definition;
- operation/resource standards match;
- selected machine standards match;
- costing standards match;
- only schedule/provenance fields differ.

Only after that proof may the WO normalize back to standard Draft schedule and receive a new Draft hash/revision.

If such proof is not implemented:

```text
Advanced-scheduled Work Order cannot be reopened safely.
Resolve through the controlled scheduling lifecycle first.
```

Do NOT blindly rehash.

This remains a production-write STOP gate.

---

# 18. Draft Reschedule Mode — still missing

Current UI has action-by-action preview/commit.

v10 still requires a planner-wide draft workspace.

Add:

```text
MachineScheduleDraftWorkspace
```

Page/circuit state only:

```text
BaseBoardFingerprint
PendingCommands
DraftAppointments
Conflicts
Warnings
UndoStack
RedoStack
```

The database remains unchanged until final commit.

Required UI:

```text
RESCHEDULING DRAFT

Pending changes: 4
Conflicts:       1

[Undo] [Redo] [Reset All] [Preview Changes]
```

---

# 19. Temporary overlap UX

Example:

```text
WO2 committed: 08:00–14:00
user drags WO1 draft to 10:00–12:00
```

Show both:

```text
WO2 █████████████████
WO1      ▒▒▒▒▒▒▒
         CONFLICT
```

Do NOT immediately save.
Do NOT silently revert.

User may then:

- move WO2;
- split WO1;
- find next slot;
- cancel change.

Final server Preview must have zero unresolved overlap.

---

# 20. Whole pending-set Preview/Commit

Current advanced service previews/commits one action request at a time.

Add a whole-workspace contract:

```text
PreviewDraftAsync
CommitDraftAsync
```

One request contains all pending commands.

Server:

1. resolves complete affected machine/WO set;
2. acquires deterministic locks;
3. reloads authoritative state;
4. applies all proposed commands to one in-memory plan;
5. validates final capacity/dependencies;
6. returns one PreviewToken;
7. atomically commits the entire set.

Do not persist intermediate drag changes.

---

# 21. Lock-scope rule

Current advanced service already has useful:

```text
ProductionMachineScheduleLock.AcquireManyAsync
ProductionWorkOrderExecutionLock.AcquireManyAsync
```

KEEP.

New rule:

If, after locks are acquired and state reloaded, the rebuilt proposal discovers an additional machine or WO that was not in the locked preview scope:

```text
ABORT
→ require fresh preview
```

Do NOT acquire new locks late in arbitrary order.

This prevents deadlocks and preview/commit drift.

---

# 22. Appointment identity — current operation identity is ambiguous

Current planner still uses:

```text
Location = operation:<OperationId>
```

for segment fragments.

For:

```text
WO1 Part1
WO1 Resume
```

both share one operation identity.

Add typed identity:

```text
segment:<SegmentUID>
operation:<OperationID>
maintenance:<ID>
external-capacity:<...>
```

When a current segment exists:

```text
segment:<UID>
```

is the appointment identity.

This allows exact segment selection/editing.

Legacy appointments may keep operation identity.

---

# 23. Permanent WO bar rendering — still missing

Current implementation adds labels/classes but still does not use:

```text
DxSchedulerTimelineView.HorizontalAppointmentTemplate
```

Implement a real appointment body template so the productive range is permanently filled.

Required:

```text
08:00–10:00  █████ WO1 Part 1 █████
```

without hover.

Color/pattern must distinguish:

- normal;
- urgent;
- split;
- resume;
- execution-locked;
- late;
- draft conflict.

Do not depend only on appointment label CSS.

---

# 24. Rich calendar bands — still missing

Current planner resource remains mainly:

```text
UsableIntervals
MaintenanceIntervals
```

Add semantic display bands:

```text
WORKING_SHIFT
SHIFT_BREAK
OFF_DAY
PUBLIC_HOLIDAY
PREVENTIVE
BREAKDOWN
REPAIR
SERVICE
```

DTO fields should include:

```text
MachineCode
Type
Start
End
HolidayName
ShiftGroupCode
ShiftCode
ShiftDescription
ReasonCode
Description
IsProductive
IsHardUnavailable
```

Scheduling authority still comes from the canonical calendar engine.

---

# 25. Holiday / shift UI mapping

Holiday name must come from:

```text
PrHoliday
```

Do NOT invent a new DateCd.

Working machine date:

```text
S1 · Day Shift · 08:00–17:00
```

Lunch:

```text
12:00–13:00 · Break
```

Off day:

```text
OFF DAY
```

Holiday:

```text
PUBLIC HOLIDAY · Deepavali
```

Preventive:

```text
PREVENTIVE · 10:00–11:30
```

Breakdown:

```text
BREAKDOWN · machine unavailable
```

---

# 26. Keep current maintenance capacity integration

Current commit added:

```text
MachineDowntimeWindowReader
```

and integrates maintenance downtime into planner/canonical machine calendar paths.

KEEP this.

Still verify:

- OPEN/IN_PROGRESS known end;
- open-ended downtime;
- COMPLETED historical interval;
- CANCELLED ignored;
- no double counting of planned preventive vs maintenance history;
- readiness fingerprint invalidates when blocking maintenance changes.

Do not replace this with a second maintenance system.

---

# 27. Segment backfill — must be revised for SegmentSet

Current `MachineScheduleSegmentBackfillService` seeds one flat ORIGINAL segment.

Update it to:

```text
Preview
→ create current SegmentSet
→ create ORIGINAL segment
→ create allocation-segment links
```

It must never infer business splits from allocation gaps.

Lunch/shift gaps are calendar fragments, not business segments.

If advanced current segments already exist, do not flatten them.

---

# 28. Release/simple reschedule dual-write after SegmentSet migration

Update current useful dual-write behavior.

## Release

Create:

```text
current SegmentSet FULL_PLAN/SCHEDULED
→ ORIGINAL segment
→ allocation links
```

## simple whole-job reschedule

Supersede prior current SegmentSet and create:

```text
new current SegmentSet FULL_PLAN/SCHEDULED
→ one ORIGINAL segment
→ new allocation links
```

No advanced write flag required for passive metadata consistency.

---

# 29. Advanced persistence after migration

Current advanced persistence already:

- updates machine ScheduleRevision;
- rewrites allocations;
- writes segments;
- projects dates.

Keep that logic structure, but persist:

```text
new current SegmentSet
→ segments
→ allocation links
```

Then mark old SegmentSet non-current/superseded.

Never rely on:

```text
SegmentStatus != SUPERSEDED
```

alone to define current planning truth.

---

# 30. Snapshot fields remain frozen during Released/InProgress scheduling

Advanced scheduling MUST NOT change:

```text
SnapshotRevision
SnapshotHash
ScheduleAnchorDateTime
PlannedQty
Operation.PlannedOutputQty
BOM
selected machine
machine cost standards
```

Schedule-only fields may change:

```text
SegmentSet/segments
allocations
planned dates
schedule provenance
ScheduleRevision
audit
```

Current tests already verify parts of this; preserve them.

---

# 31. Stock/WIP/FG/costing isolation

Do NOT change:

```text
ProductionOutput posting
ProductionOutput rollback
Material Issue posting
Finished Good posting
Production Return
Production Adjustment
Stock ledger
FIFO/WAC/Standard cost
COGS
```

Machine scheduling owns planning/capacity only.

Current unrelated StockLedger changes in commit `b327f92…` are outside this plan; do not expand scheduler work into stock replay.

---

# 32. Mandatory current-repo reconciliation tests

Add dedicated tests for the architectural delta.

## SegmentSet/schema

1. one current SegmentSet per WorkOrderMachine;
2. old SegmentSet remains historical;
3. FULL_PLAN normal release;
4. REMAINING_ONLY replan;
5. HELD_UNSCHEDULED allows zero allocations;
6. REPLAN_REQUIRED state;
7. SegmentSet combined hash matches machine/operation/allocations.

## Allocation link

8. every current productive allocation has exactly one link;
9. links point to current segments;
10. historical SegmentSets consume no capacity;
11. legacy nullable old SegmentID does not become authority.

## UOM/cycle

12. same-UOM cycle math unchanged;
13. BOX→PCS conversion matches WorkOrderQuantityCalculator;
14. productive-time split crosses lunch correctly;
15. productive-time split crosses shift end correctly;
16. CycleSeconds=0 whole-job scheduling still works when total fixed duration positive;
17. CycleSeconds=0 partial split is blocked clearly.

## DP consistency

18. DP Post reducing RemainingQty does not corrupt scheduler;
19. DP Rollback increasing RemainingQty above future plan yields REPLAN_REQUIRED;
20. Replan Remaining clears REPLAN_REQUIRED;
21. DP Post/Rollback stock/cost outputs unchanged.

## Reopen

22. advanced Reopen cannot blindly rehash;
23. non-schedule manufacturing tampering is detected;
24. safe verified normalization to Draft works if implemented;
25. otherwise advanced Reopen is explicitly blocked.

---

# 33. Mandatory Draft Reschedule tests

26. drag does not write DB;
27. multiple pending moves can coexist;
28. temporary overlap visible;
29. Undo;
30. Redo;
31. Reset All;
32. hidden WO still causes conflict;
33. external branch conflict stays anonymous;
34. final Preview sees complete pending set;
35. one atomic commit;
36. stale whole-board token rejected;
37. newly discovered unlocked scope aborts/re-preview.

---

# 34. Mandatory rich UI tests

38. permanent appointment body color visible without hover;
39. `segment:<UID>` selects exact segment;
40. same WO Part1 and Resume selectable independently;
41. Working Shift displayed;
42. Shift Break displayed;
43. Off Day displayed;
44. Public Holiday + name displayed;
45. Preventive displayed;
46. Breakdown/Repair displayed;
47. productive bars never cover lunch/off/holiday gaps;
48. Day view;
49. Week view;
50. Month view does not fake minute precision;
51. Windows 100%;
52. Windows 125%;
53. no DevExpress runtime error.

---

# 35. SQL Server concurrency gates

Retain existing tests and add:

54. whole-draft commit vs whole-draft commit;
55. whole-draft commit vs Release;
56. whole-draft commit vs simple reschedule;
57. whole-draft commit vs Reopen;
58. whole-draft commit vs DP Post;
59. whole-draft commit vs DP Rollback;
60. whole-draft commit vs maintenance change;
61. deterministic sorted machine locks;
62. deterministic sorted WO execution locks;
63. no late lock expansion;
64. no partial multi-WO commit;
65. final company+machine allocations have zero overlap.

---

# 36. Revised implementation order from current b327f92

## Phase A — preserve baseline

- build/tests;
- no behavioral rewrite.

## Phase B — SegmentSet schema + transitional link table

- add SegmentSet;
- add allocation-segment link;
- migrate flat current data;
- keep old direct columns temporarily.

## Phase C — capacity/provenance/currentness refactor

- SegmentSet current authority;
- combined hash;
- zero-allocation planning states;
- revise backfill;
- revise Release/simple reschedule dual-write.

## Phase D — quantity/cycle correctness

- IUomConversionService;
- productive-fragment split boundary;
- zero-cycle compatibility.

## Phase E — DP schedule-consistency layer

- FuturePlannedQty;
- REPLAN_REQUIRED;
- no changes to posting/costing.

## Phase F — Reopen fail-closed safety

- integrity verification or explicit block;
- remove blind advanced rehash.

## Phase G — Draft Reschedule Mode

- pending commands;
- Undo/Redo/Reset;
- conflict overlay;
- whole-set Preview/Commit.

## Phase H — rich calendar + permanent bars

- semantic bands;
- appointment body template;
- typed segment identities.

## Phase I — full SQL/performance/UAT hardening

Advanced flag remains OFF until all gates pass.

---

# 37. MUST NOT

Reject implementation if the agent:

- rewrites working advanced services from scratch without need;
- treats current flat schema as final architecture;
- uses MachineScheduleRevision alone as current plan identity;
- keeps direct allocation SegmentID as the only business link after reconciliation;
- makes Daily Production SegmentID mandatory;
- uses wall-clock percentage/time to derive split quantity;
- skips IUomConversionService;
- recreates the zero-cycle Release blocker;
- reconstructs split jobs from outer machine dates;
- changes DP posting/rollback costing logic for scheduler convenience;
- blindly recomputes SnapshotHash on advanced Reopen;
- directly commits every drag;
- lacks whole-set preview;
- allows hidden conflicts to disappear because of UI filters;
- keeps operation-level identity for several separate same-operation segments;
- relies on hover/label CSS for the primary range display;
- leaves Holiday/Off/Shift/Break visually anonymous;
- changes stock/WIP/FG/costing as part of scheduling;
- auto-enables `MACHINE_SCHEDULE_SEGMENT_WRITE_ENABLED`.

---

# 38. Final acceptance target

After v10 implementation, this must work safely:

```text
M01 Monday

08:00–10:00   WO1 Part 1
10:30–12:30   WO2
13:00–15:00   WO3
15:30–18:00   WO1 Resume
```

with:

```text
WO1 Part1 and Resume as separate business segments
exact productive allocation fragments
no overlap
correct cycle/UOM quantities
correct shift/break/holiday/preventive/breakdown handling
whole WO date recalculation
Draft Reschedule Mode
Undo/Redo/Reset
whole-set Preview
atomic commit
posted execution protection
DP rollback → REPLAN_REQUIRED when necessary
safe Replan Remaining
safe/fail-closed Reopen
no stock/costing changes
```

And the planner must visibly show:

```text
colored WO ranges
Working Shift
Shift Break
Off Day
Public Holiday + name
Preventive
Breakdown/Repair
other-branch reserved capacity
```

without requiring hover for the primary schedule range.

---

# 39. Required AI-agent completion report

The coding agent must return:

```text
Phase
Files changed
Schema changes
Behavior changed
Tests added
Tests run
Pass/fail
Known blocker
Advanced flag state
```

It must explicitly report:

```text
Model used: GPT-6 Sol High
```

and answer:

1. Is SegmentSet now the current-plan authority?
2. Is there exactly one current SegmentSet per selected WorkOrderMachine?
3. Are allocations still the physical-capacity authority?
4. Are allocation-to-segment links explicit?
5. Is UOM conversion identical to WorkOrderQuantityCalculator?
6. Are cycle boundaries based on productive time rather than wall clock?
7. Does CycleSeconds=0 retain valid whole-job behavior?
8. Does DP Rollback raise REPLAN_REQUIRED when future capacity becomes short?
9. Is advanced Reopen safe or intentionally blocked?
10. Can the user rearrange multiple WOs in one Draft Reschedule session?
11. Can temporary conflicts exist only in draft UI?
12. Is final commit atomic?
13. Are WO ranges permanently colored?
14. Are Shift/Break/Off/Holiday/Preventive/Breakdown visible?
15. Can WO1 Part1 and WO1 Resume be selected independently?
16. Are stock/WIP/FG/costing regressions unchanged?
17. Are there zero committed physical-machine overlaps?
18. Is `MACHINE_SCHEDULE_SEGMENT_WRITE_ENABLED` still OFF until approval?

Do not claim completion when any mandatory answer is unknown.
