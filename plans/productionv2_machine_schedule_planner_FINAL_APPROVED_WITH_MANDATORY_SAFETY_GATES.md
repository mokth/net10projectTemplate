# ProductionV2 --- Machine Schedule Planner & Safe Drag/Drop Rescheduling

## APPROVED AI CODING AGENT IMPLEMENTATION PLAN

**Repository:** `mokth/net10projectTemplate`\
**Target branch:** `productionv2`\
**Scope:** Production / Work Order machine scheduling\
**Target users:** Production planners and supervisors in Malaysian SME
manufacturing\
**Implementation status:** **APPROVED FOR IMPLEMENTATION ---
DEEP-REVIEWED SAFETY REVISION**\
**Design target:** intuitive, low-training, fast, informative, safe, and
consistent with the existing ERP architecture.

------------------------------------------------------------------------

# 1. Objective

Implement a visual **Machine Schedule Planner** that lets a production
planner see all relevant not-yet-started Work Order machine schedules on
a machine/time timeline and safely reschedule eligible jobs by
drag/drop.

The feature MUST:

1.  show machine rows against a date/time timeline;
2.  show Work Order machine jobs as schedule bars;
3.  allow eligible jobs to be moved by drag/drop;
4.  use the existing production calendar/scheduling engine for shifts,
    breaks, holidays/off-days and preventive maintenance;
5.  prevent the same physical machine from being scheduled for more than
    one job at the same time;
6.  protect Daily Production/executed production from being moved;
7.  propagate a valid machine reschedule through Process/Operation, Work
    Centre/Route Step and Work Order planned dates;
8.  respect serial/parallel routing and internal-WIP dependencies;
9.  preview all affected dates before committing a multi-operation
    change;
10. enforce collision/concurrency rules on the server, not only in the
    browser;
11. remain performant by loading only the visible scheduling window;
12. retain an auditable record of planner changes.

This is **not** an SAP-style APS implementation and must not introduce
unnecessary planning complexity.

------------------------------------------------------------------------

# 2. Verified Existing Architecture --- Reuse, Do Not Replace

The implementation MUST build on the existing `productionv2` scheduling
stack already present in the repository.

Important verified components include:

-   `ErpWeb.Core/Production/WorkOrderScheduleCalculator.cs`
-   `ErpWeb.Core/Production/ProductionCalendarScheduler.cs`
-   `ErpWeb.Core/Production/ProductionCalendarScheduleDataLoader.cs`
-   `ErpWeb.Core/Production/WorkOrderCalendarProvider.cs`
-   `ErpWeb.Core/Production/WorkOrderSchedulingLock.cs`
-   `ErpWeb.Core/Production/ProductionWorkOrderService.cs`
-   `ErpWeb.Core/Production/ProductionWorkOrderService.Reopen.cs`
-   `ErpWeb.Core/Production/ProductionOutputService.Posting.cs`
-   `ErpWeb.Model/Entities/Production/ProductionWorkOrder.cs`
-   `ProductionWorkOrderRouteStep`
-   `ProductionWorkOrderOperation`
-   `ProductionWorkOrderMachine`
-   `ProductionOutput`

The newer Work Order aggregate uses:

`PrWorkOrder -> route steps -> operations -> machine options`

and planned timestamps including:

-   Work Order `PlannedStartDateTime` / `PlannedCompletionDateTime`
-   Route Step planned start/completion
-   Operation planned start/completion
-   selected `ProductionWorkOrderMachine.PlannedStartDateTime` /
    `PlannedCompletionDateTime`

The repository also contains legacy-style `PrSchMas`, `PrSchWcenter`,
`PrSchProcess`, `PrSchMachine` and `PrSchDailyProd` entities. **Do not
blindly dual-write legacy tables.** Before implementation, trace whether
the current Work Order/Daily Production path requires any compatibility
synchronization. Only update legacy structures when an existing verified
contract explicitly requires it.

------------------------------------------------------------------------

# 3. Existing Scheduling Rules That Must Remain Canonical

Do not create another independent date arithmetic engine.

The existing scheduling components already account for:

-   machine shift calendars;
-   working/off days;
-   shift start/end times;
-   shift breaks;
-   overnight shifts;
-   planned preventive-maintenance downtime;
-   forward/backward scheduling;
-   machine setup/conversion/queue/run duration;
-   same-sequence parallel processing;
-   sequence-based serial processing;
-   internal-route-WIP predecessor dependencies;
-   route-step and Work Order date roll-up.

`ProductionWorkOrderMachine` duration currently includes
setup/conversion/queue plus planned run minutes. Preserve the existing
duration/cost calculation contract.

The Machine Schedule Planner is a **placement/occupancy/rescheduling
layer around this engine**, not a replacement scheduler.

------------------------------------------------------------------------

# 4. Core Business Rule --- Physical Machine Cannot Overlap

For this milestone, treat a physical `MachineCode` as exclusive
capacity:

> A machine cannot have two active planned production jobs occupying the
> same time interval.

Use half-open interval semantics:

`[Start, Completion)`

Therefore:

-   job A ending exactly at 12:00 and job B starting exactly at 12:00 is
    allowed;
-   any positive intersection is a conflict.

Overlap test:

`Existing.Start < Proposed.Completion && Existing.Completion > Proposed.Start`

Exclude from conflict checking:

-   the schedule line currently being rescheduled;
-   cancelled/deleted/non-active scheduling records according to the
    actual Work Order lifecycle;
-   records that the current domain explicitly treats as
    non-capacity-consuming.

Do NOT infer from `ParallelMachineCount` that the same physical
`MachineCode` may accept overlapping Work Orders. That field currently
represents capacity inside a selected machine option/calculation
contract. Do not weaken the physical-machine no-overlap rule without a
future explicit multi-unit resource design.

------------------------------------------------------------------------

# 5. Machine Schedule Planner UI

Create a new production-planning page consistent with the project's
existing list/entry visual conventions.

Suggested route/name:

`Planning/WorkOrders/PrMachineSchedulePlanner`

Exact naming may follow the repository's current menu/permission naming
standard.

## 5.1 Primary layout

Use a timeline/Gantt/scheduler presentation:

-   **Y-axis / rows:** Machine
-   **X-axis:** Date/time
-   **bars:** scheduled Work Order machine operations

Conceptually:

``` text
                    MON 12        TUE 13        WED 14        THU 15
CUT-01        [WO001 CUTTING]              [WO009 CUTTING------]
CUT-02              [WO004--------]
CNC-01        [WO002--------------]        [PM BLOCK]
PACK-01       [WO005]      [WO008]                       [WO011]
```

Do not make users edit dates in a large grid as the primary workflow.

## 5.2 DevExpress implementation decision

Before coding the UI, inspect the exact DevExpress Blazor
version/package references in `productionv2` and identify the supported
component best suited to interactive scheduling/Gantt behavior.

Prefer an existing licensed DevExpress component already available in
the solution if it supports the required timeline and drag/drop
behavior.

Do NOT introduce a new commercial library without explicit approval.

If the installed DevExpress components cannot safely provide the
required behavior, implement a focused timeline component using the
project's existing Blazor/Bootstrap/JS interop conventions. Keep custom
JavaScript isolated behind a small adapter.

The agent must document the selected component and why before
implementing the page.

------------------------------------------------------------------------

# 6. Default Planner Experience

Default to **Week** view.

Toolbar should stay compact:

-   Previous period
-   Today
-   Next period
-   Day / Week / Month
-   Work Centre filter
-   Machine filter
-   Status filter
-   Search WO/Product
-   Refresh

Do not expose technical scheduling parameters in the normal toolbar.

The planner should normally be able to open this page and understand
machine loading without entering a Work Order first.

------------------------------------------------------------------------

# 7. Schedule Bar Content

Show only useful information that fits the available width.

Priority:

1.  Work Order No.
2.  Product/item code or short description
3.  Process/operation
4.  quantity where space permits

Do not overload narrow bars.

Hover/click should expose additional information.

Recommended tooltip/side-panel data:

-   Work Order number
-   product/item
-   Work Centre
-   operation/process
-   machine
-   planned quantity/UOM
-   planned duration
-   planned start
-   planned completion
-   Work Order status
-   execution status
-   predecessor information
-   successor information
-   delivery/due date where an existing reliable source is available
-   conflict/late warning where applicable

------------------------------------------------------------------------

# 8. Side Detail Panel

Selecting a schedule bar opens a compact side panel without navigating
away from the board.

Primary actions only:

-   Open Work Order
-   Reschedule (when eligible)
-   View Dependencies

Avoid button-heavy UI.

Technical schedule hashes/traces should be hidden under an
Advanced/Trace section and not shown to normal users by default.

------------------------------------------------------------------------

# 9. Visual Availability

The same timeline should make capacity constraints understandable.

Display:

-   normal working intervals;
-   non-working/off hours as subdued background;
-   company/machine off-days;
-   holidays;
-   preventive-maintenance windows;
-   optionally shift boundaries when zoom level permits.

Preventive maintenance must be visibly different from Work Order bars
and must not be draggable from this page.

The UI must not suggest that a non-working period is schedulable.

------------------------------------------------------------------------

# 10. Drag/Drop Interaction

Only an eligible, not-started schedule bar may be dragged.

During drag:

1.  display proposed start;
2.  calculate/display proposed completion using server-authoritative
    scheduling logic as soon as practical;
3.  visually indicate valid/invalid placement;
4.  snap to usable machine capacity rather than arbitrary midnight/date
    positions;
5.  show a clear reason when the proposed placement is invalid.

Example error:

> `CUT-01 is already scheduled for WO005 from 15 Oct 10:30 to 15 Oct 15:20.`

Never silently drop a job into an invalid interval.

------------------------------------------------------------------------

# 11. Preview Before Commit

A simple move that affects no downstream schedule may use a compact
confirmation.

If the move changes dependent operations, display a **Schedule Impact
Preview** before commit.

Example:

  -----------------------------------------------------------------------
  Scope             Before            After             Impact
  ----------------- ----------------- ----------------- -----------------
  CUT / CUT-01      12 Oct 08:00--13  14 Oct 08:00--15  +2 days
                    Oct 11:35         Oct 11:35         

  PRINT / PRN-01    13 Oct--14 Oct    15 Oct--16 Oct    moved

  PACK / PK-01      14 Oct            16 Oct            moved

  WO completion     14 Oct            16 Oct            +2 days
  -----------------------------------------------------------------------

Actions:

-   Cancel
-   Apply Reschedule

Do not silently ripple many dates after one drag.

------------------------------------------------------------------------

# 12. Rescheduling Eligibility / Execution Protection

Create one canonical server-side eligibility policy. UI disabling is
only a convenience; server validation is authoritative.

Minimum policy:

### Eligible

-   operation/machine schedule exists;
-   operation has not begun production execution;
-   Work Order lifecycle permits planning changes;
-   no blocking production transaction exists for that operation;
-   requested move does not violate dependency or calendar rules.

### Blocked / controlled

-   posted Daily Production exists for the target operation;
-   operation execution quantities indicate execution has started;
-   Work Order/operation is completed/cancelled or otherwise terminal;
-   production posting is currently in progress;
-   another lifecycle rule makes the snapshot immutable.

### Daily Production draft --- HARD BLOCK

A current `ProductionOutput` draft (`Status == NEW`) linked to the
target or any operation that the proposed ripple must move is a **hard
rescheduling blocker**.

Reason: the draft stores `SnapshotRevision`, `SnapshotHash`,
`PlannedMachineCode`, production date, shift and material facts. Moving
its operation underneath an existing draft creates ambiguous execution
intent.

The user must delete/cancel/resolve the NEW Daily Production draft and
recreate it after rescheduling. Do **not** auto-delete, auto-migrate or
silently rewrite production drafts from the scheduling planner.

`POSTED` production is also a hard blocker for moving that operation.
`REVERSED` remains execution history and must be treated conservatively
as a blocker unless a separately verified correction contract explicitly
permits schedule movement.

### Important

Do not lock an entire partially executed Work Order merely because one
unrelated operation has started, unless dependency rules require it.

Use operation-level execution facts wherever possible.

------------------------------------------------------------------------

# 13. Canonical Execution Check

Implement/reuse a domain service that determines whether an operation is
safe to reschedule.

Check current production-domain facts rather than relying only on header
status.

Relevant verified facts include:

-   `ProductionOutput` linked by `WorkOrderOperationId`;
-   Production Output status;
-   production posting links where relevant;
-   operation execution quantities such as
    processed/good/scrap/reject/hold/rework/transferred;
-   material/production movements where they prove execution;
-   current Work Order lifecycle.

Do not infer current execution solely from legacy
`PrSchDailyProd.ScheCode` text when the newer aggregate has direct IDs.

Return structured reason codes so the UI can explain why a bar is
locked.

------------------------------------------------------------------------

# 14. Rescheduling Engine Design

Do NOT simply overwrite
`ProductionWorkOrderMachine.PlannedStartDateTime` and
`PlannedCompletionDateTime`.

Introduce a dedicated application/domain service, for example:

`IProductionMachineReschedulingService`

with separate **Preview** and **Commit** commands.

Conceptual API:

``` text
PreviewRescheduleAsync(request)
CommitRescheduleAsync(request)
```

Request should identify at minimum:

-   WorkOrderId
-   OperationId
-   Machine row/selected machine identity
-   requested anchor DateTime
-   expected Work Order RowVersion and/or scheduling version
-   user-entered reason if policy requires one

Preview returns:

-   eligibility;
-   normalized actual proposed start/completion;
-   affected operations;
-   affected route steps;
-   new WO start/completion;
-   conflicts;
-   warnings;
-   preview token/version information sufficient to detect stale commit.

Commit must revalidate everything. Never trust a previous preview as
current state.

------------------------------------------------------------------------

# 14A. CRITICAL SNAPSHOT INVARIANT --- SCHEDULE REVISION IS NOT PRODUCTION SNAPSHOT REVISION

This deep review found a critical boundary that the implementation MUST
preserve.

Existing Draft schedule recalculation (`RecalculateDraftScheduleAsync`)
recomputes `SnapshotHash` and increments `SnapshotRevision`. That
behavior is correct for a mutable Draft snapshot, but **must not be
reused for planner rescheduling of Released/InProgress Work Orders**.

Daily Production stores and validates the Work Order
`SnapshotRevision` + `SnapshotHash`. A pure planner date move must
therefore NOT mutate the frozen production-definition/costing snapshot
identity.

Implement a separate optimistic-concurrency concept for planning
placement, e.g. `ScheduleRevision` (integer) plus normal `RowVersion`.
The exact schema name may follow project conventions, but the invariant
is mandatory:

-   BOM/definition/cost/execution snapshot change -\> existing snapshot
    revision/hash rules;
-   pure schedule placement change -\> schedule revision / rowversion
    only;
-   do not increment `SnapshotRevision`;
-   do not recompute `SnapshotHash` merely because planned timestamps
    moved on a Released/InProgress WO;
-   Daily Production drafts must never become stale merely because an
    unrelated eligible operation was rescheduled.

If `WorkOrderSnapshotHasher` currently includes planned timestamps, do
not casually change its canonical contract. First add characterization
tests and isolate the planning-placement version from the frozen
execution snapshot contract. If this cannot be done without changing the
existing snapshot hash contract, STOP and redesign before
implementation.

# 15. Operation-Level Rescheduling Semantics

The current `WorkOrderScheduleCalculator.ScheduleAsync()` schedules the
Work Order from its overall anchor. Do not misuse it in a way that
discards the planner's selected operation anchor.

Extend/refactor scheduling functionality carefully so that the existing
calendar placement logic can schedule from a selected operation while
preserving:

-   fixed/executed predecessors;
-   the selected operation's requested anchor;
-   parallel siblings where they do not need to move;
-   serial successors;
-   internal-WIP dependency edges;
-   current duration calculation;
-   current calendar source/hash provenance.

Avoid cloning the algorithm into a separate rescheduler.

Prefer extracting/reusing internal scheduling primitives from
`WorkOrderScheduleCalculator` and `ProductionCalendarScheduler`.

Existing Work Order creation/recalculation behavior must remain
regression-compatible.

------------------------------------------------------------------------

# 16. Dependency Propagation

After moving an eligible operation, recompute only the necessary
dependent planning scope.

Rules:

1.  selected operation is anchored at the requested usable machine
    position;
2.  serial successors may not start before required predecessors finish;
3.  same-sequence parallel siblings should not be moved unnecessarily;
4.  an `INTERNAL_ROUTE_WIP` consumer may not start before its producing
    route step is available;
5.  route-step dates are rolled up from operation dates;
6.  Work Order dates are rolled up from route-step dates;
7.  already executed/frozen operations must never be shifted;
8.  if the requested change would require moving a frozen operation,
    reject the proposal with an explanatory dependency conflict.

Do not hide such conflicts by changing historical dates.

------------------------------------------------------------------------

# 17. Machine Occupancy / Conflict Service

Introduce a single authoritative overlap query/service, e.g.:

`IMachineScheduleOccupancyService`

Responsibilities:

-   query active planned jobs for a machine intersecting a requested
    window;
-   exclude the current schedule identity;
-   return conflicting WO/operation details;
-   support timeline range queries;
-   participate in commit-time validation.

The same overlap semantics must be used by:

-   planner timeline;
-   preview;
-   commit;
-   tests;
-   any future Work Order schedule recalculation that can create machine
    overlaps.

Do not duplicate overlap logic in Razor components.

------------------------------------------------------------------------

# 18. Concurrency --- Hard No-Overlap Guarantee

Client-side collision detection is not sufficient.

Two planners may simultaneously see the same free slot and both attempt
to schedule it.

Commit must therefore:

1.  begin a DB transaction;
2.  acquire a deterministic scheduling/machine lock appropriate to SQL
    Server;
3.  reload the Work Order/operation/machine and execution state;
4.  validate RowVersion/staleness;
5.  recompute the proposed schedule against current calendar data;
6.  query machine occupancy again while protected from concurrent
    conflicting commits;
7.  reject overlap;
8.  apply dependent schedule changes;
9.  save audit data;
10. commit.

Investigate whether to extend the existing `WorkOrderSchedulingLock` or
introduce a machine-specific application lock such as a normalized
company+machine scheduling resource.

Prefer the narrowest lock that guarantees correctness without
serializing unrelated machines unnecessarily.

Example conceptual resource:

`PR_MACHINE_SCHEDULE|{COMPANY}|{MACHINE}`

If multiple machines are changed by dependency propagation, acquire
locks in deterministic normalized machine-code order to avoid deadlocks.

Retain the existing scheduling-source lock semantics for calendar-source
updates.

------------------------------------------------------------------------

# 18A. LOCK ORDER --- REQUIRED TO AVOID PRODUCTION DEADLOCKS

Define and test one global lock order for reschedule commits. The coding
agent must inspect existing Production Output, Material Issue,
completion/reopen and calendar writer lock orders before finalizing it.

Minimum principle:

1.  scheduling-source/shared calendar lock when required;
2.  machine application locks in normalized machine-code order;
3.  Work Order row locks in deterministic WorkOrder ID/order;
4.  operation/machine rows;
5.  execution/draft recheck;
6.  occupancy query;
7.  update + audit.

Do not invent a lock order that conflicts with existing posting paths.
Add SQL Server concurrency tests that exercise reschedule versus Daily
Production create/post and calendar maintenance writes, not only
reschedule-versus-reschedule.

# 19. Database-Level Protection

SQL Server cannot enforce arbitrary interval non-overlap with a normal
unique index.

Therefore the authoritative guarantee should be the transaction +
deterministic locking + overlap query.

Do not add a misleading unique index that does not actually prevent
interval overlap.

Add supporting indexes for fast overlap searches.

Candidate index shape should be verified against actual
table/configuration names, but should conceptually support:

`Company / MachineCode / PlannedStartDateTime / PlannedCompletionDateTime`

Because machine code currently resides under operation/machine rows,
inspect existing indexes before adding anything and avoid duplicate
indexes.

------------------------------------------------------------------------

# 19A. OCCUPANCY MUST USE PRODUCTIVE INTERVALS, NOT ONLY OUTER START/END

This is essential for correctness. Existing machine scheduling can span
breaks, nights, holidays and preventive windows. A job may therefore
have an outer `[PlannedStart, PlannedCompletion)` containing periods
when the machine is not actually occupied.

Do **not** blindly treat that entire outer interval as continuous
machine occupancy, or valid work in a second shift/day could be falsely
blocked. Conversely, do not allow another job to consume productive
fragments already assigned to the first job.

The implementation must establish a canonical finite-capacity occupancy
representation. Preferred safe design:

-   during schedule placement, materialize the actual productive
    capacity fragments consumed by the job (start/end fragments
    returned/derived from `ProductionCalendarScheduler`);
-   persist/query those fragments in a dedicated schedule-allocation
    table keyed by company, machine, WO, operation and schedule
    revision;
-   overlap enforcement operates on allocation fragments using half-open
    intervals;
-   break/off/holiday/preventive gaps are not allocation fragments and
    remain available only if the machine calendar itself says they are
    productive (normally they are not);
-   allocation fragments are replaced atomically when an eligible job is
    rescheduled.

If the current scheduler cannot expose consumed fragments, refactor it
so the same canonical placement calculation returns both
`ActualStart/ActualCompletion` and consumed productive intervals. Do not
implement a second duration walker.

This allocation representation also makes the planner board and future
utilization KPI accurate.

# 20. Audit Trail

Every committed manual reschedule should be auditable.

Prefer extending the existing production audit-event approach if it fits
the current schema.

Record enough information to answer:

-   who moved it;
-   when;
-   Work Order;
-   operation/process;
-   machine;
-   previous start/completion;
-   new start/completion;
-   affected WO completion before/after;
-   reason if captured;
-   whether downstream schedules changed.

Do not store only a free-text message when structured fields are
practical.

Add an audit/information action from the side panel if consistent with
existing ERP UI patterns.

------------------------------------------------------------------------

# 21. Planner Query / Read Model

Do not load complete Work Order aggregates for every visible bar.

Create a focused schedule-board query/read model.

Input:

-   company/branch scope;
-   visible start/end;
-   optional Work Centre;
-   optional machine(s);
-   optional status;
-   search term.

Return only required schedule-card fields plus constraint blocks.

Query scheduled jobs by interval intersection:

`Start < VisibleEnd && Completion > VisibleStart`

Also return relevant machine availability/unavailability information for
the same range.

Use `AsNoTracking()` for board reads.

Project directly to DTOs; avoid large `Include()` graphs.

------------------------------------------------------------------------

# 22. Performance Requirements

The board must use **windowed loading**.

Never load years of production schedules into the browser.

Default Week view should request only the visible week plus a small
rendering buffer if required by the chosen component.

When the user navigates to another period, request that period.

Use server-side filtering for:

-   Work Centre;
-   machine;
-   status;
-   WO/product search.

Debounce search/filter changes.

Avoid N+1 queries for tooltip/details. Basic tooltip data should be in
the board DTO; load full side-panel/dependency detail on selection.

Do not poll continuously at a high rate. Provide explicit Refresh and,
if appropriate, a modest refresh strategy consistent with the existing
Blazor Server application.

------------------------------------------------------------------------

# 23. Unscheduled Queue --- Phase 2 Boundary

The architecture should allow a future **Unscheduled / Needs Attention**
panel where eligible operations can be dragged onto machines.

Do NOT expand Phase 1 into automatic machine assignment or full
dispatching unless required for the current schedule data to function.

Phase 1 priority:

> safely view and reschedule existing machine assignments.

Design DTO/service boundaries so Phase 2 can be added without rewriting
the planner.

------------------------------------------------------------------------

# 24. Informative but Simple Planner Warnings

Keep warnings operational, not technical.

Examples:

-   `Machine already occupied by WO005.`
-   `Preventive maintenance blocks this period.`
-   `No working shift is available at the requested time.`
-   `Daily Production has already started for this process.`
-   `Moving CUTTING to 15 Oct would delay PACKING to 17 Oct.`
-   `This change would require moving a process that has already started.`
-   `Schedule changed by another user. Refresh and try again.`

Do not expose exception stack traces, hashes or internal entity names to
users.

------------------------------------------------------------------------

# 25. KPI / Summary Strip

Keep this secondary to the scheduling board.

Useful compact indicators for the visible period:

-   scheduled jobs;
-   unscheduled/attention jobs when Phase 2 exists;
-   machine utilization where calculation is reliable;
-   conflicts: must normally be zero after hard enforcement;
-   late-risk Work Orders where a reliable requested/delivery date
    exists.

Do not implement speculative KPI calculations without a verified source
field.

------------------------------------------------------------------------

# 26. Late-Risk Indicator

Only implement if the current Work Order/DR/SO chain provides a reliable
required/delivery date.

If verified:

-   compare recalculated Work Order completion against the relevant
    committed requirement date;
-   show a warning in preview;
-   never silently reject a planner change solely because it is late
    unless an existing business rule requires blocking.

Planner should be informed, not trapped by unnecessary hard validation.

------------------------------------------------------------------------

# 27. Permissions

Use existing permission architecture.

Separate at least conceptually:

-   View Machine Schedule
-   Reschedule Machine Schedule

If the project's permission convention supports a separate override
permission, reserve it for future controlled exceptions. **Do not add an
overlap override in this milestone** because the requirement is hard
no-overlap.

Users without write permission can still use the planner as an inquiry
screen.

------------------------------------------------------------------------

# 28. UI Responsiveness and Accessibility

The scheduling board should remain usable on normal office
monitors/laptops.

Production planning is desktop-oriented; do not compromise the desktop
planner to force the entire Gantt onto a phone-sized layout.

For smaller screens:

-   retain horizontal scrolling;
-   keep machine names sticky/frozen;
-   maintain readable minimum bar widths;
-   allow side panel to become an overlay.

Use tooltips/icons plus text where necessary; do not rely on color alone
to distinguish locked/conflicted/maintenance states.

------------------------------------------------------------------------

# 29. Do Not Change These Core Contracts

Unless a verified defect requires it, this project must NOT alter:

-   BOM calculation;
-   material requirements;
-   Work Order planned quantity;
-   machine cycle/output calculation;
-   production costing formulas;
-   absorbed costing;
-   inventory posting;
-   lot costing/tracking;
-   Daily Production posting quantities;
-   WIP consumption policy;
-   material issue logic;
-   finished-good receipt logic;
-   Delivery Request allocation;
-   Work Order snapshot definition content.

Rescheduling changes **planning timestamps and scheduling provenance**,
not production definition/cost facts.

------------------------------------------------------------------------

# 30. Calendar Source / Hash Handling

Existing scheduling stores calendar provenance/hash data on
machine/operation scheduling records.

A committed reschedule must refresh this provenance using the existing
hash/provider mechanisms.

Do not manually copy old calendar hashes after recalculation.

Preview must not persist new hashes.

Commit should detect stale calendar/source changes and recompute rather
than applying an obsolete preview.

------------------------------------------------------------------------

# 31. Preview Must Be Side-Effect Free

`PreviewRescheduleAsync` must not modify tracked persistent entities or
call `SaveChanges`.

Use DTO/cloned planning state or a deliberately detached calculation
model.

Preview may return a preview fingerprint/token based on relevant
versions, but commit always performs fresh validation.

This is essential because planners may cancel the preview.

------------------------------------------------------------------------

# 32. Work Order Date Roll-Up

After accepted rescheduling:

-   Machine planned timestamps = calculated machine placement;
-   Operation planned timestamps = operation placement;
-   Route Step/Work Centre planned start = minimum relevant operation
    start;
-   Route Step/Work Centre planned completion = maximum relevant
    operation completion;
-   Work Order planned start = minimum relevant route-step start;
-   Work Order planned completion = maximum relevant route-step
    completion.

Use existing domain scheduling semantics rather than independently
recalculating these in the UI.

------------------------------------------------------------------------

# 33. Forward/Backward Work Orders

Preserve the Work Order's existing scheduling direction semantics.

For manual machine drag/drop, the user's dropped position is an explicit
planner anchor for the selected operation. Dependency propagation must
still respect the Work Order's routing constraints.

Do not unexpectedly reinterpret a drag as a change to the Work Order's
overall scheduling direction.

If backward-scheduled Work Orders require special successor/predecessor
propagation behavior, cover it explicitly in tests before enabling
drag/drop for them.

If safe semantics cannot be proven immediately, Phase 1 may restrict
manual drag to the verified-safe direction and clearly display why; do
not guess.

------------------------------------------------------------------------

# 33A. RELEASED / IN-PROGRESS POLICY

The planner is specifically intended to move not-yet-started operations
on already released production. Therefore do not route the normal
operation through `ReopenForEditAsync`; reopening changes lifecycle
semantics and is intentionally blocked once execution exists.

Allowed scope for Phase 1:

-   `RELEASED`: eligible unstarted operations may be rescheduled;
-   `IN_PROGRESS`: only unstarted operations whose required
    predecessor/execution constraints remain satisfiable may be
    rescheduled;
-   `COMPLETED` / cancelled/terminal: no reschedule;
-   `DRAFT`: continue to use the existing Work Order draft scheduling
    workflow rather than the production planner unless explicitly
    unified later.

A reschedule must not change WO status, release metadata, BOM snapshot,
costing facts, quantities, selected machine, or production transaction
facts.

# 34. Drag Resize

Phase 1 should **not allow users to resize schedule bars to change
duration**.

Duration is calculated from production data and machine
setup/conversion/queue/run requirements.

Planner changes placement, not calculated productive duration.

This prevents UI edits from corrupting production standards/cost
calculations.

------------------------------------------------------------------------

# 35. Machine Change by Vertical Drag

Do not automatically enable dragging a job from Machine A row to Machine
B row in Phase 1.

Changing machine is a different business operation because machine
alternatives can have different:

-   cycle time;
-   setup/conversion/queue time;
-   rates/cost;
-   calendars;
-   labour ownership;
-   capacity.

Phase 1 drag/drop = **change time on the currently selected physical
machine**.

A future controlled "Change Machine" action may select another valid
snapshotted alternative and recalculate cost/duration through existing
domain rules.

------------------------------------------------------------------------

# 36. Recommended Visual States

Use semantic states consistent with the application theme:

-   Not Started / draggable
-   Selected
-   Locked / execution started
-   Completed
-   Preventive maintenance
-   Off/non-working period
-   Warning / late risk
-   Invalid drag target

Do not hard-code arbitrary colors if the existing application has theme
variables. Use theme/CSS semantic classes so light/dark themes remain
readable.

------------------------------------------------------------------------

# 37. Refresh / Stale UI Behavior

Because multiple planners may be working concurrently:

-   board DTOs should carry version/staleness identifiers;
-   commit must return a clear concurrency error when data changed;
-   on concurrency failure, refresh the affected machine/time range
    rather than reloading the entire application;
-   preserve the user's visible date range and filters after refresh.

------------------------------------------------------------------------

# 38. Implementation Sequence

## Phase A --- Discovery guard

Before editing code:

1.  verify current DevExpress version/components;
2.  trace current menu/permission registration conventions;
3.  trace Work Order aggregate mappings/indexes;
4.  trace how current Daily Production drafts/posts link to operations;
5.  trace any live compatibility writes to legacy `PrSch*` tables;
6.  run existing Work Order and Daily Production tests to establish
    baseline.

Stop and report if repository reality conflicts with this plan.

## Phase B --- Domain/read model

1.  add schedule-board DTO/query service;
2.  add operation reschedule eligibility service/policy;
3.  add machine occupancy service;
4.  add overlap indexes only after checking existing indexes;
5.  unit-test interval semantics.

## Phase C --- Rescheduling engine

1.  refactor reusable scheduling primitives without changing current
    Work Order scheduling behavior;
2.  implement side-effect-free preview;
3.  implement dependency propagation;
4.  implement frozen/executed-operation protection;
5.  implement calendar/hash recalculation;
6.  add preview tests.

## Phase D --- Transactional commit

1.  add deterministic machine scheduling lock strategy;
2.  reload/revalidate versions;
3.  re-run execution check;
4.  re-run calendar placement;
5.  re-run occupancy check;
6.  apply timestamps/rollups;
7.  write audit event;
8.  save atomically;
9.  add concurrency integration tests.

## Phase E --- Planner UI

1.  create planner page;
2.  add visible-window query;
3.  add timeline/machine rows;
4.  add constraint backgrounds;
5.  add schedule bars/tooltips;
6.  add side panel;
7.  add drag/drop preview;
8.  add impact confirmation;
9.  add error/reload handling;
10. integrate permissions/menu.

## Phase F --- Regression/performance

Run existing production tests plus new scheduling tests. Verify no
changes to posting/costing/BOM behavior.

------------------------------------------------------------------------

# 39. Mandatory Automated Tests

At minimum add tests for:

### Calendar placement

-   normal day shift;
-   shift break;
-   overnight shift;
-   holiday/off-day;
-   preventive maintenance;
-   job spanning multiple working days;
-   missing calendar coverage.

### Collision

-   no overlap;
-   exact boundary A.End == B.Start allowed;
-   partial overlap rejected;
-   proposed job fully inside existing job rejected;
-   existing job fully inside proposed job rejected;
-   same time on different machines allowed;
-   current schedule excluded from self-conflict;
-   concurrent attempts for same machine/slot: exactly one succeeds.

### Execution protection

-   no Daily Production -\> eligible;
-   posted ProductionOutput on same operation -\> blocked;
-   execution quantities on same operation -\> blocked;
-   unrelated executed operation does not automatically block
    independent eligible operation;
-   dependency requiring movement of frozen operation -\> proposal
    rejected;
-   Daily Production draft behavior matches the verified policy.

### Dependency

-   serial operation moved later propagates successor;
-   parallel sibling remains unchanged when valid;
-   slowest parallel member controls next sequence;
-   internal-route-WIP dependency respected;
-   Work Centre/route-step roll-up correct;
-   Work Order roll-up correct.

### Concurrency/staleness

-   stale RowVersion rejected;
-   calendar changed between preview and commit -\> recompute/reject
    appropriately;
-   schedule changed between preview and commit -\> no overlap created;
-   machine locks acquired deterministically.

### Regression

-   existing Work Order initial scheduling unchanged;
-   current Work Order release/reopen tests remain green;
-   Daily Production posting remains green;
-   production costing tests remain green;
-   inventory posting tests remain green.

------------------------------------------------------------------------

# 40. Manual Acceptance Scenarios

## Scenario A --- Simple drag

MC01 has WO001 on Monday and free Tuesday. Drag WO001 to Tuesday.
Preview shows new dates. Commit succeeds. Parent dates roll up.

## Scenario B --- Collision

MC01 has WO001 Tuesday 08:00--12:00. Drag WO002 to Tuesday 10:00. UI
identifies conflict and server rejects if attempted. No database dates
change.

## Scenario C --- Preventive maintenance

MC01 has planned maintenance Tuesday 13:00--17:00. Drag an 8-hour job to
Tuesday morning. Existing calendar scheduler places productive time only
in usable intervals and completes later according to the shift calendar.

## Scenario D --- Holiday

Drag to a company/machine non-working date. Scheduler snaps/continues to
the next valid machine working interval according to existing calendar
rules.

## Scenario E --- Downstream impact

Move CUT two days later. PRINT and PACK depend on CUT. Preview clearly
lists affected dates and new WO completion. Nothing persists until
Apply.

## Scenario F --- Started production

Daily Production has been posted for CUT. CUT bar is locked. Planner can
inspect it but cannot drag it.

## Scenario G --- Partial WO

CUT is completed, PACK has not started. PACK can be rescheduled if
dependencies and machine capacity permit. CUT history remains unchanged.

## Scenario H --- Two planners

Two users drag different jobs into the same free MC01 slot. Both
previews may initially look valid. At commit, transaction/machine lock
makes one succeed and the other receive a clear conflict/stale message.
Overlap never reaches committed state.

------------------------------------------------------------------------

# 41. UX Acceptance Criteria

A normal planner should be able to:

1.  open Machine Schedule Planner;
2.  see this week's machine loading immediately;
3.  identify free/occupied/maintenance/non-working periods without
    opening multiple screens;
4.  search a WO/product quickly;
5.  click a job and understand what it is;
6.  drag an eligible job to another date;
7.  understand immediately if the move is impossible;
8.  see downstream consequences before saving;
9.  apply the change with one clear confirmation;
10. remain on the same scheduling board after save.

If routine rescheduling requires navigating through multiple Work Order
entry tabs, the UX objective has not been met.

------------------------------------------------------------------------

# 42. Performance Acceptance Criteria

Validate with realistic SME data volumes, not only tiny unit-test
fixtures.

Target behavior:

-   Week view loads only intersecting schedules and required machine
    constraint data;
-   board query remains a projected/no-tracking query;
-   no per-bar database query;
-   selecting one bar may load additional detail on demand;
-   navigating weeks does not reload unrelated years/history;
-   drag interaction remains responsive because expensive authoritative
    recalculation occurs through focused preview requests, not repeated
    full aggregate reloads.

Record query timings during implementation and inspect SQL plans if the
board becomes slow.

------------------------------------------------------------------------

# 42A. MANDATORY SHADOW / DRY-RUN ROLLOUT GATE

Because this feature can affect factory execution, do not deploy
drag/drop write capability immediately after automated tests.

Required rollout:

1.  deploy board in **read-only** mode first and compare displayed
    schedules with existing Work Order Entry for representative WOs;
2.  enable Preview only; planners compare proposed dates manually for at
    least the agreed UAT sample;
3.  run a SQL preflight that proves zero committed allocation overlaps
    for the migrated/open schedule set;
4.  enable Commit only for authorized UAT users/company;
5.  verify audit, rollback/recovery procedure and backup before
    production enablement;
6.  only then enable normal planner write permission.

Provide a feature flag/configuration switch that can disable planner
writes without disabling production posting.

# 43. Definition of Done

Implementation is complete only when:

-   visual machine timeline is usable;
-   eligible schedules can be drag-rescheduled;
-   started production cannot be moved;
-   physical machine overlaps cannot be committed;
-   shift/holiday/break/preventive rules use existing scheduler;
-   dependencies propagate correctly;
-   preview shows downstream effects;
-   parent dates roll up correctly;
-   audit history is recorded;
-   concurrency cannot create overlap;
-   board queries are windowed and performant;
-   existing production/costing/posting behavior remains unchanged;
-   all existing and new tests pass;
-   no unnecessary SAP/APS complexity is introduced.

------------------------------------------------------------------------

# 44. Explicit Non-Goals for This Milestone

Do NOT add unless separately approved:

-   automatic whole-factory optimization;
-   AI auto-scheduling;
-   automatic priority sequencing;
-   drag-to-change machine;
-   duration resizing;
-   overlap override;
-   automatic labour rescheduling;
-   procurement auto-expediting;
-   automatic SO/DR promise-date changes;
-   full APS finite-capacity solver;
-   replacement of the existing Work Order scheduler.

These can be future enhancements after planners are successfully using
the basic machine schedule board.

------------------------------------------------------------------------

# 45. Agent Guardrails

The coding agent MUST:

1.  inspect current code before modifying each subsystem;
2.  reuse current production conventions and naming;
3.  keep business rules out of Razor components;
4.  avoid duplicated scheduling calculations;
5.  never trust client drag coordinates as final schedule timestamps;
6.  never update executed historical production timestamps;
7.  never permit machine overlap due to a race condition;
8.  never silently move dependent operations without preview;
9.  never alter costing/BOM/posting merely to make scheduling easier;
10. add tests before declaring the feature complete.

If an unverified repository contract is encountered, stop and document
the finding rather than inventing behavior.

------------------------------------------------------------------------

# 46. Final Architecture

``` text
Machine Schedule Planner UI
        |
        +--> Schedule Board Query Service
        |       +--> visible WO machine jobs
        |       +--> shifts/off-days/maintenance
        |
        +--> Preview Reschedule
        |       +--> eligibility/execution guard
        |       +--> existing calendar scheduler
        |       +--> machine occupancy service
        |       +--> dependency propagation
        |       +--> impact DTO
        |
        +--> Commit Reschedule
                +--> transaction
                +--> deterministic machine lock(s)
                +--> reload + RowVersion
                +--> execution recheck
                +--> calendar recomputation
                +--> occupancy recheck
                +--> dependency recomputation
                +--> parent date roll-up
                +--> scheduling provenance/hash refresh
                +--> audit event
                +--> atomic SaveChanges
```

This keeps the UI simple while placing correctness in the domain/service
layer.

------------------------------------------------------------------------

# 47. Approval Assessment

**Status: APPROVED FOR IMPLEMENTATION**

The plan is intentionally additive and preserves the current production
scheduling, costing, BOM and posting architecture. The most important
safety improvement is that machine occupancy becomes an authoritative
server-side constraint while the existing calendar scheduler remains the
source of truth for usable production time.

The UI is deliberately optimized for SME production planners: visual
week board, machine-first view, drag/drop for routine changes,
side-panel details, clear locked states, and impact preview only when
consequences extend beyond the selected job.

**Implementation gate:** the agent must complete Phase A discovery and
report any repository mismatch before proceeding. In particular,
DevExpress component availability and any live synchronization
requirement between the newer `PrWorkOrder*` aggregate and legacy
`PrSch*` tables must be verified rather than assumed.

# 47. Deep Review Amendments / Approval Conditions

The original plan was re-reviewed against `productionv2` scheduling,
Work Order lifecycle and Daily Production code. The following are now
**non-negotiable correctness conditions**:

1.  no NEW Daily Production draft may sit on an operation being moved by
    the proposed ripple;
2.  pure Released/InProgress schedule changes must not increment or
    rewrite the frozen production `SnapshotRevision`/`SnapshotHash`;
3.  machine collision must be checked against actual consumed productive
    allocation fragments, not merely outer start/completion timestamps;
4.  a deterministic SQL Server lock order must protect reschedule vs
    reschedule **and** reschedule vs execution/calendar changes;
5.  Draft Work Orders keep the existing Draft recalculation contract;
    planner writes target Released/InProgress unstarted scope;
6.  commit recalculates from current calendars and current occupancy and
    never trusts preview results;
7.  no drag changes machine, duration, BOM, quantity, costing or
    execution facts;
8.  rollout must pass read-only -\> preview -\> controlled commit gates.

**Final review status:** APPROVED FOR IMPLEMENTATION only when the
coding agent implements all safety amendments above. Any attempt to
simplify away the snapshot boundary, productive-fragment allocation,
execution blocker, or transactional lock rules invalidates this
approval.

# FINAL RELEASE-GATE AMENDMENTS --- THIRD DEEP REVIEW

## Status

**APPROVED FOR IMPLEMENTATION ONLY AFTER ALL SAFETY GATES BELOW PASS.**

This third deep review challenged the design against the current
`productionv2` scheduler and execution paths. These amendments override
any weaker wording elsewhere in this plan.

## A. Finite capacity is part of placement, not merely a conflict check

Do not schedule a job against the raw machine calendar and then only
reject it if it overlaps another Work Order.

Canonical calculation must be:

`machine calendar usable productive intervals` **minus**
`productive allocation fragments already committed to other active jobs`
= `effective free machine capacity`

Then the existing canonical duration walker schedules the proposed
operation through that effective capacity.

Example: machine works 08:00--17:00, WO-A occupies 10:00--12:00, and
WO-B requires four productive hours from 09:00. If routing policy
permits, WO-B can consume 09:00--10:00 plus 12:00--15:00. It must never
consume 10:00--12:00.

This avoids both machine overlap and unnecessary rejection when later
free capacity exists.

## B. Reuse the scheduler's existing consumed productive intervals

Verified repository fact:
`ProductionCalendarScheduler.ScheduleMachine(...)` already returns:

-   `ActualStart`
-   `ActualCompletion`
-   `ProductiveDuration`
-   `ConsumedIntervals`

Therefore `ScheduleMachineResult.ConsumedIntervals` is the canonical
source for machine allocation fragments.

Do not: - reconstruct fragments from outer start/completion; - create a
second duration walker; - treat breaks, off-hours, holidays or
preventive downtime as machine occupancy.

Persist the exact consumed intervals for finite-capacity enforcement.

## C. Add a canonical machine-allocation persistence model

For every capacity-consuming scheduled machine operation, persist the
productive fragments returned by the canonical scheduler in a dedicated
allocation structure.

Each allocation fragment must identify at least:

-   tenant/company and branch where required by the current tenancy
    contract;
-   MachineCode;
-   WorkOrderID;
-   OperationID;
-   selected machine row identity where useful;
-   schedule/allocation revision;
-   fragment Start;
-   fragment End;
-   creation/modification/audit provenance as appropriate.

Use half-open `[Start, End)` semantics.

Schedule timestamp updates and allocation replacement must occur in the
**same transaction**. A failed commit must leave both the previous
planned dates and previous allocation fragments intact.

## D. Existing open Work Orders require a mandatory allocation backfill/preflight

Do not enable planner writes with allocations only for newly rescheduled
jobs. That would make old active Work Orders invisible to
finite-capacity enforcement.

Before write enablement:

1.  enumerate every active Released/InProgress capacity-consuming
    machine operation;
2.  reconstruct its productive fragments using the same canonical
    scheduler/calendar provenance where this can be proven;
3.  compare reconstructed outer start/completion with persisted planned
    timestamps;
4.  mark any schedule that cannot be reproduced exactly enough as
    `UNVERIFIED`;
5.  detect pre-existing physical-machine allocation overlaps;
6.  do not silently move or repair those WOs;
7.  block planner writes for affected machine/ranges until discrepancies
    are reviewed;
8.  require zero unexplained active overlaps and zero unverified active
    allocations in the enabled scope before normal go-live.

## E. Schedule-vs-Daily-Production race must be serialized

A simple execution check before rescheduling is insufficient.

Verified current Daily Production posting behavior locks stock/posting
resources and then locks the Work Order/execution graph. A planner could
otherwise check "not started", while another user creates or posts Daily
Production immediately afterward.

Introduce one compatible **schedule-vs-execution serialization
boundary** for the same WO/operation.

Both sides must participate:

-   Machine Schedule reschedule commit;
-   Daily Production Create;
-   Daily Production Post;
-   rollback/reversal where it can alter execution eligibility.

A narrowly scoped SQL Server application lock keyed by
company/branch/WorkOrder (or operation only if proven sufficient) is
acceptable, but the exact key/order must be validated against existing
stock and production lock order.

Invariant:

> Once one side crosses the serialization boundary, the other side
> cannot make an eligibility-changing commit for the same execution
> scope until the first transaction finishes and the second revalidates.

Do not globally serialize unrelated Work Orders.

## F. SQL Server lock-order tests are mandatory

Add real SQL Server concurrency tests for:

-   planner commit vs planner commit on same machine/same slot;
-   planner commit vs planner commit on different machines;
-   planner commit vs Daily Production Create;
-   planner commit vs Daily Production Post;
-   planner commit vs production rollback/reversal where applicable;
-   planner commit vs preventive/calendar writer;
-   propagation touching multiple machines, with locks acquired in
    deterministic normalized machine-code order.

Exactly one valid final state must result. Deadlocks must not be
accepted as normal workflow.

## G. Calendar and preventive changes need an impact contract

A future shift, holiday, machine-calendar or preventive-maintenance edit
can invalidate already committed future allocations.

Do not silently rewrite Work Order schedules from calendar/master
maintenance.

Calendar/maintenance writers must:

1.  detect future committed allocation fragments affected by the
    proposed change;
2.  either block the master-data change pending rescheduling, or
    explicitly mark affected schedules `Needs Attention` under a
    controlled policy;
3.  surface those affected jobs in Machine Schedule Planner;
4.  never rewrite executed historical production.

Add tests for: - new preventive window across an existing future
allocation; - working day changed to holiday; - shift shortened after a
future WO was scheduled; - overnight shift/calendar ownership.

## H. Backward scheduling is gated

Do not assume operation-level drag propagation is safe for
backward-scheduled WOs.

Before enabling backward-WO drag/drop, tests must prove:

-   selected-operation anchor semantics;
-   predecessor/successor direction;
-   parallel groups;
-   internal WIP dependencies;
-   occupancy-aware placement;
-   frozen executed operations.

If not proven, display backward WOs but disable write drag for them in
Phase 1 with a clear message.

## I. Production snapshot identity remains frozen

For Released/InProgress pure schedule placement changes:

-   do not increment `SnapshotRevision`;
-   do not recompute/change `SnapshotHash`;
-   do not rebuild BOM/definition/cost snapshot;
-   do not alter quantity, machine selection, costing or execution
    facts.

Use separate schedule/allocation versioning plus RowVersion.

Draft Work Orders continue using their existing Draft recalculation
contract.

## J. No direct planned-date bypass

After this feature is introduced, Released/InProgress machine planned
dates must not be directly updated by UI code, ad-hoc service methods or
scripts while bypassing allocation enforcement.

All such schedule placement changes must go through the canonical
scheduling/rescheduling service so that:

-   calendar rules;
-   occupancy subtraction;
-   dependency propagation;
-   execution guards;
-   allocations;
-   audit;
-   concurrency

remain consistent.

## K. NEW Daily Production draft remains a hard blocker

A `ProductionOutput` with `Status == NEW` on any operation that the
proposed ripple must move is a hard blocker.

Do not auto-delete, migrate or rewrite the draft.

The user must resolve/delete the draft and recreate it after
rescheduling.

POSTED is a hard blocker for the operation. REVERSED remains execution
history and stays blocked unless a separately verified correction policy
explicitly permits movement.

## L. Required finite-capacity tests

In addition to the plan's existing tests, add:

-   split shift with another WO occupying only one productive fragment;
-   lunch/break gap is not treated as capacity;
-   overnight shift consumed intervals;
-   preventive window splitting a shift;
-   long job scheduled around another WO's occupied interval;
-   exact productive-fragment boundary is allowed;
-   multiple occupied fragments in one day;
-   propagation onto a second machine whose desired time is occupied;
-   rollback on any propagation failure restores all old dates and
    allocations;
-   existing active schedule backfill detects historical overlap;
-   unverified backfill blocks write enablement.

## M. Go-live safety gate

Production write capability is **NOT APPROVED** until all of these are
true:

1.  existing scheduler regression suite is green;
2.  new finite-capacity unit/integration tests are green;
3.  SQL Server race/deadlock tests are green;
4.  active open schedules have verified allocation fragments;
5.  zero unexplained active physical-machine overlaps exist in enabled
    scope;
6.  backward scheduling writes are either proven or disabled;
7.  calendar/maintenance impact behavior is implemented;
8.  read-only board UAT matches Work Order Entry/current production
    expectations;
9.  preview-only UAT is signed off;
10. write feature flag and recovery procedure are tested;
11. audit trail is verified;
12. backup/recovery procedure is confirmed before enabling production
    writes.

## Final safety assessment

With every gate above implemented and verified:

**APPROVED --- production-safe design target / 10.0 design score.**

If any of sections A--G, I, K or M are omitted:

**NOT APPROVED FOR PRODUCTION WRITE ENABLEMENT.**

These are correctness requirements, not optional enhancements.

# MANDATORY SAFETY GATE ENFORCEMENT --- RELEASE CONTRACT

This section is authoritative. The AI coding agent MUST implement these
gates as executable controls, tests, or preflight checks. A checklist in
documentation is not sufficient.

## Gate 1 --- Feature is fail-closed

Add a production/planning application setting using the existing
`AppSettingCatalogue` pattern, for example:

`Planning.MachineSchedulePlannerWriteEnabled`

Default: **false**.

The UI may expose the planner in read-only mode while false. No
drag/drop commit endpoint may write schedule/allocation changes unless
this gate is true **and** the runtime readiness check below passes.

Do not use only a hidden button or client-side flag. Enforce the gate in
the application service.

## Gate 2 --- Runtime readiness/preflight service

Add one authoritative readiness service, for example:

`IMachineSchedulePlannerReadinessService`

It returns a structured result containing at least:

-   `IsWriteReady`;
-   allocation schema/index readiness;
-   active allocation backfill completeness;
-   count of unverified active allocations;
-   count of unexplained active machine overlaps;
-   stale calendar/allocation count;
-   backward-scheduling write support state;
-   required feature-setting state;
-   any blocking reason codes/messages.

The commit service MUST call readiness server-side. If
`IsWriteReady == false`, fail closed.

The planner should show a simple banner such as **Scheduling changes are
temporarily read-only** with details available to an administrator.

## Gate 3 --- Canonical finite-capacity placement

PASS only if all placement paths use:

`calendar usable intervals - committed allocation fragments = effective capacity`

and then reuse the canonical scheduler.

PASS evidence: - unit tests for split shifts, breaks, holidays,
maintenance and overnight; - allocation-fragment collision tests; -
long-duration job scheduling around occupied fragments; - exact boundary
test.

Any code path that updates Released/InProgress machine dates without
this service is a FAIL.

## Gate 4 --- Canonical `ConsumedIntervals`

PASS only if persisted allocations originate from
`ScheduleMachineResult.ConsumedIntervals` (or a refactored canonical
result from the same scheduler).

No second interval-duration algorithm is allowed.

Add a regression test proving:
`sum(all persisted allocation fragment durations) == scheduler ProductiveDuration`.

## Gate 5 --- Atomic dates + allocations

Schedule timestamp changes, parent rollups, schedule revision,
allocation deletion/replacement and audit records must commit in one
database transaction.

Inject/fake a failure before commit and prove: - old planned dates
remain; - old allocations remain; - schedule revision remains; - no
partial audit event appears.

## Gate 6 --- Existing active schedule backfill

Before enabling writes for a tenant/scope:

-   every active Released/InProgress capacity-consuming operation has
    verified allocation fragments;
-   unverified count = 0;
-   unexplained overlap count = 0.

The backfill/preflight tool is report-first. It must never silently
reschedule an existing WO.

If either count is non-zero, `IsWriteReady=false`.

## Gate 7 --- Daily Production serialization

PASS only when schedule commit and Daily Production execution entry
share the verified serialization contract.

Required SQL Server race tests: - Reschedule vs ProductionOutput
Create; - Reschedule vs ProductionOutput Post; - Reschedule vs
applicable rollback/reversal.

No final state may contain execution created/posted against an operation
whose schedule was concurrently moved without the losing side
reloading/revalidating.

## Gate 8 --- Machine concurrency

Required real SQL Server tests:

-   two planners target same machine and overlapping productive
    capacity;
-   two planners target exact adjacent boundaries;
-   propagation from different WOs converges on same downstream machine;
-   two unrelated machines can proceed without unnecessary global
    blocking.

Expected: - conflicting schedules: at most one compatible commit wins; -
adjacent schedules: both can succeed; - no overlap can remain after
either test; - no partial propagation.

## Gate 9 --- Calendar/preventive impact

Calendar, shift and preventive maintenance changes must detect affected
future allocations.

PASS only when the selected policy is implemented consistently: - block
master change pending reschedule; OR - commit master change but mark all
affected schedules `Needs Attention` and block unsafe planner
commit/production workflow as explicitly designed.

Never silently rewrite executed production.

Tests must cover holiday, shift shortening, preventive insertion and
overnight shift.

## Gate 10 --- Execution-state blockers

For every operation in the proposed ripple set, recheck under
transaction/serialization:

-   NEW ProductionOutput =\> block;
-   POSTED =\> block;
-   REVERSED =\> block unless a separately approved correction policy
    exists;
-   completed/cancelled WO =\> block;
-   execution quantities/movements indicating started work =\> block.

Do not rely on UI state captured during preview.

## Gate 11 --- Frozen production snapshot

For Released/InProgress pure schedule movement, test before/after
equality of:

-   SnapshotRevision;
-   SnapshotHash;
-   BOM/material snapshot;
-   planned quantities;
-   selected machine;
-   costing facts/rates;
-   execution quantities.

Only scheduling fields, allocation/schedule revision and schedule audit
may change.

## Gate 12 --- Dependency correctness

Regression tests must prove:

-   StageSequence serial behavior;
-   equal StageSequence parallel behavior;
-   ProcessSequence serial behavior;
-   equal ProcessSequence parallel behavior;
-   `INTERNAL_ROUTE_WIP` predecessor dependency;
-   downstream ripple;
-   upstream frozen/executed operation remains unchanged;
-   parent Process/RouteStep/WO min/max rollups are exact.

If a moved operation would require changing a frozen
predecessor/successor illegally, preview and commit both fail with the
same reason.

## Gate 13 --- Backward scheduling

Backward-scheduled WOs remain read-only in the drag planner until
dedicated occupancy-aware backward tests pass.

Do not let a generic feature flag accidentally enable them.

Once proven, add a separate capability/readiness flag and tests before
write enablement.

## Gate 14 --- Preview/commit equivalence

Every preview returns a token/version containing enough state to detect
changes, including relevant WO RowVersions/schedule revisions and
calendar/allocation provenance.

At commit: - reacquire locks; - reload current facts; - rerun/revalidate
placement; - reject stale preview if result is no longer equivalent.

Never trust dates sent back by the browser as authoritative schedule
results.

## Gate 15 --- Server authority

Client drag/drop provides only intent, e.g. operation + desired anchor.

Server computes authoritative: - snapped start; - completion; - consumed
intervals; - dependency ripple; - conflicts; - parent rollups.

No client-calculated completion/occupancy is persisted.

## Gate 16 --- Performance safety

Timeline reads are range-bounded and machine-bounded.

PASS targets must be measured on representative SME production data and
recorded in UAT evidence. Do not solve performance by weakening conflict
checks or loading incomplete occupancy.

Required indexes for allocation/window queries must be verified in SQL
Server execution plans or equivalent integration evidence.

## Gate 17 --- Audit and traceability

Every successful reschedule records: - actor; - reason; - timestamp; -
old/new operation dates; - old/new WO dates; - affected operations; -
machine; - schedule revision; - preview/request correlation identifier.

Failed previews/commits must not generate misleading success audit
events.

## Gate 18 --- Existing regression suite

All existing Production/Planning tests must remain green.

For the production release pipeline, run SQL Server suites with:

`ERPWEB_REQUIRE_SQLSERVER_TESTS=1`

so critical SQL Server tests cannot silently skip.

A skipped mandatory scheduling/concurrency test counts as FAIL for
release.

## Gate 19 --- Staged enablement

Required rollout sequence:

1.  schema + allocation model, write feature OFF;
2.  backfill/preflight;
3.  read-only planner;
4.  preview-only planner;
5.  UAT on copied/controlled production-like data;
6.  enable write for one controlled company/branch;
7.  monitor audit/conflict/readiness results;
8.  broader enablement only after sign-off.

Do not deploy schema and immediately enable drag/drop writes globally.

## Gate 20 --- Production go-live certificate

Add an automated/admin-readable readiness report. Production write
enablement requires ALL rows PASS:

-   feature configuration valid;
-   schema/index ready;
-   allocation backfill complete;
-   unverified allocations = 0;
-   unexplained overlaps = 0;
-   stale/invalid future allocations = 0 or resolved under approved
    policy;
-   forward scheduling tests pass;
-   backward write either proven or disabled;
-   Daily Production race tests pass;
-   machine concurrency tests pass;
-   calendar-impact tests pass;
-   dependency tests pass;
-   snapshot-freeze tests pass;
-   atomic rollback tests pass;
-   existing regression tests pass;
-   SQL Server mandatory tests executed, not skipped;
-   UAT sign-off recorded;
-   recovery/feature-disable procedure tested.

**If any mandatory row is FAIL or UNKNOWN, planner write mode MUST
remain disabled.**

## Coding-agent completion rule

The AI coding agent is explicitly prohibited from reporting this feature
as `DONE`, `APPROVED`, `PRODUCTION READY`, or `10/10 IMPLEMENTED` merely
because the code compiles or UI works.

It may report production readiness only after attaching the Gate 1--20
evidence matrix with every mandatory gate PASS.

Any UNKNOWN is treated as FAIL.
