# productionv2 --- Machine Schedule Planner FINAL Closure Plan

## View-Only Enablement + Working Reschedule + User-Friendly Timeline

**Branch reviewed:** `mokth/net10projectTemplate` / `productionv2`\
**Review date:** 12 October 2026\
**Purpose:** close the repeated review/implementation loop with one
bounded, repo-grounded implementation contract.\
**Plan status:** **APPROVED FOR IMPLEMENTATION.**\
**Production-write status:** **BLOCKED until the completion certificate
in §14 is entirely PASS.**

------------------------------------------------------------------------

# 1. Final review conclusion

The current implementation is **not a completed machine schedule
planner**. It is a useful read-only board plus partial safety
infrastructure.

This is not an opinion: the repository implementation log explicitly
states that the following remain outstanding:

-   allocation backfill execution;
-   operation-level reschedule preview/propagation;
-   transactional reschedule commit;
-   calendar-impact handling;
-   mandatory SQL Server concurrency evidence;
-   browser/UAT evidence.

`MachineSchedulePlannerReadinessService` also unconditionally appends
`CALENDAR_RELEASE_EVIDENCE_MISSING` and returns `IsWriteReady = false`.

Therefore the correct solution is **not** to keep redesigning the page
and **not** to hard-code readiness to true. Finish the missing command
path and evidence path once, then enable writes only when the real
checks pass.

------------------------------------------------------------------------

# 2. Hard scope --- no expansion

This implementation must deliver exactly:

1.  readable and intuitive machine schedule timeline;
2.  compact standard ERP filters/lookups;
3.  user-resizable/zoomable timeline time columns;
4.  working same-machine drag/drop rescheduling;
5.  working `Change Schedule` alternative;
6.  preview before commit;
7.  finite-capacity no-overlap scheduling;
8.  safe Released/InProgress schedule-only update;
9.  runtime readiness that can genuinely become PASS;
10. allocation backfill/preflight;
11. calendar-impact protection;
12. concurrency with Daily Production;
13. browser UAT + SQL Server tests;
14. controlled feature enablement.

**Do not add:** machine reassignment by drag, duration resize, APS
auto-optimization, automatic whole-factory reshuffle, labour scheduling,
procurement replanning, BOM edits, costing changes, or unrelated
production redesign.

This bounded scope is the stop condition. Do not create a "future phase"
for any item above.

------------------------------------------------------------------------

# 3. Verified current repository facts

## 3.1 UI is still deliberately read-only

`PrMachineSchedulePlanner.razor` currently contains:

-   `AllowEditAppointment="false"`
-   `AllowDragAppointment="false"`
-   `AllowDragAppointmentBetweenResources="false"`
-   `AllowResizeAppointment="false"`
-   disabled Reschedule button.

This must remain fail-closed until Preview + Commit exist.

## 3.2 Readiness can never currently pass

`MachineSchedulePlannerReadinessService.GetReadinessAsync()` currently:

-   checks company/branch/access;
-   checks `MACHINE_SCHEDULE_PLANNER_WRITE_ENABLED`;
-   checks allocation schema;
-   checks missing allocations;
-   checks stale allocation provenance;
-   checks active overlaps;
-   then **always** adds `CALENDAR_RELEASE_EVIDENCE_MISSING`;
-   returns `IsWriteReady = false`;
-   returns `CalendarAndReleaseEvidenceVerified = false`.

Do not replace that with a literal `true`. Implement the missing
evidence contract.

## 3.3 Existing allocation schema is usable

`scripts/create-machine-schedule-planner.sql` already creates:

-   `PrWorkOrder.ScheduleRevision`;
-   `PrWorkOrderMachine.ScheduleRevision`;
-   `PrMachineScheduleAllocation`;
-   positive interval check;
-   machine/start/end index;
-   machine/revision/fragment unique index;
-   WO/operation index.

Use this schema unless an actual verified deficiency requires a
migration.

## 3.4 Snapshot hash includes schedule dates --- critical

`WorkOrderSnapshotHasher` includes:

-   WO ScheduleAnchorDateTime;
-   WO PlannedStart/Completion;
-   route-step planned dates;
-   operation planned dates;
-   machine planned dates and schedule provenance.

Therefore **do not recompute `SnapshotHash` or increment
`SnapshotRevision` for a pure Released/InProgress planner move.**

Daily Production uses the released snapshot identity. Planner schedule
concurrency belongs to the separate `ScheduleRevision` contract.

Add explicit tests proving schedule-only reschedule preserves: -
`SnapshotHash`; - `SnapshotRevision`; - BOM/material snapshot; - planned
quantities; - selected machine; - costing values; - execution
quantities/history.

## 3.5 Daily Production already has an execution application lock

`ProductionWorkOrderExecutionLock` exists.

Daily Production Post and Rollback acquire it inside SQL transactions;
Post/Rollback retain branch-stock-lock-first ordering.

The implementation log states the intended future schedule-commit lock
order: **sorted machine locks → Work Order execution lock → row
locks/revalidation**.

Before coding Commit, verify Create also participates in the required
execution serialization. If Create does not, add the smallest compatible
transaction/application-lock boundary so planner Commit cannot race a
NEW Daily Production draft into existence after eligibility validation.

## 3.6 Canonical productive intervals already exist

Use
`ProductionCalendarScheduler.ScheduleMachine(...).ConsumedIntervals`.

Never build another duration-fragment algorithm.

## 3.7 Existing lookup projection exists

`ProductionOutputService.GetEligibleOperationFilterOptionsAsync()`
already returns:

-   WorkOrders;
-   Products;
-   WorkCentres;
-   Processes;
-   Machines;

with code/description display values.

Reuse/extract the common lookup projection or common DTO/query service
where appropriate. Do not duplicate five independent planner lookup
queries with different semantics.

------------------------------------------------------------------------

# 4. Final user experience

A normal production planner should understand the page without
documentation.

## Header

**Machine Schedule**

Status chip: - `Rescheduling enabled`, or - `View only`.

Helper: \> Find a Work Order or machine, then select a job. Drag it to
move, or use Change Schedule.

Do not show technical gate codes to ordinary users.

If View Only: \> Scheduling changes are not enabled for this company.

Provide `Why?`/details only to authorized admin/support users.

## Toolbar row 1 --- navigation

`Previous` \| `Today` \| `Next` \| **current date range** \| `Day`
`Week` `Month` \| `−` `+` `Reset`

Optional `Fit` only if it chooses a readable supported scale. It must
never compress text below readable minimum.

## Toolbar row 2 --- compact filters

  Field         Control               Desktop target
  ------------- ------------------- ----------------
  Work Centre   searchable combo              175 px
  Machine       searchable combo              205 px
  Process       searchable combo              175 px
  Work Order    searchable lookup             210 px
  Product       searchable lookup             210 px
  Status        combo                         135 px

These are UX target widths, not inflexible mobile widths.

Rules: - visible labels; - code + description; - `SearchMode=AutoSearch`
where using `DxComboBox`; - no generic free-text substitute for
WO/Product/Process; - no `.filters > * { flex:1 }`; - responsive
wrapping; - Clear Filters; - do not require users to press Refresh after
every normal selector change; - cancel/debounce stale loads.

## Timeline

Timeline is the dominant surface.

Selected details must be collapsible or below the board; do not
permanently steal \~320 px.

Bar content: `WO No · Product`

Full tooltip: - WO; - product; - work centre; - process; - machine; -
planned start/completion; - quantity; - lock reason.

Locked/started jobs visibly show a lock.

Legend: Scheduled \| Started/Locked \| Maintenance \| Non-working \|
Needs attention.

------------------------------------------------------------------------

# 5. Timeline column resizing --- final specification

**Yes: the user must be able to make time columns wider/narrower.**

This is display-only. It must never change any production dates.

## Required behavior

Maintain UI state such as:

`TimelineZoomLevel` or `TimelineCellWidth`.

Buttons: - `−` = narrower; - `+` = wider; - `Reset` = view-specific
default.

Use tested bounds, e.g. a small discrete set rather than unlimited pixel
entry: `48, 64, 80, 100, 125, 150, 180`.

Do not let the user shrink below the width where the active header
format becomes unreadable.

## View-specific scales

Do **not** use Day + 6-hour scales for every view.

### Day

-   date header;
-   hour or 2-hour subscale depending zoom;
-   default relatively wide.

### Week

-   day/date header;
-   4h/6h subscale as appropriate;
-   horizontal scroll.

### Month

-   month/week/day-oriented scale;
-   **no 6-hour subdivision across 30/31 days by default**;
-   day columns must display at least day number and short weekday/date
    format;
-   horizontal scroll is expected and acceptable.

The user asked for readable information, not "fit all 31 days on one
screen at any cost."

Use DevExpress timeline properties/templates supported by installed
26.1.4: - `CellMinWidth`; - timeline `Scales`; -
`DateHeaderCellTemplate`; - `ResourceHeaderCellTemplate` where useful.

Do not rely on brittle DOM selectors when a DevExpress API exists.

## Scroll

Remove/fix any outer `overflow:hidden` that clips the scheduler's own
horizontal scroll.

Verify scrollbar usability at: - 1366×768; - 1920×1080; - Windows 125%
scaling.

## Persistence

If the project has a standard per-user UI preference mechanism, persist
view + zoom. Otherwise keep it for the page session only. **Do not add a
new database preference subsystem just for this feature.**

------------------------------------------------------------------------

# 6. Reschedule API --- exact bounded contract

Add explicit service contracts to the planner scheduling domain.

Example shapes (names may follow project conventions):

-   `PreviewRescheduleAsync(MachineScheduleRescheduleRequest request)`
-   `CommitRescheduleAsync(MachineScheduleCommitRequest request)`

## Request intent

Client sends only: - target Operation ID; - current selected Machine
ID/code; - requested anchor/start; - expected WO ScheduleRevision; -
expected machine ScheduleRevision/RowVersion; - optional preview token
on Commit; - user reason if required.

Client must **not** send authoritative completion, ripple dates or
allocation fragments.

## Preview result

Return: - current selected operation start/end; - proposed authoritative
start/end; - exact affected operations before/after; - WO before/after
dates; - warnings; - hard blockers; - downstream impact; -
preview/correlation token; - revisions/provenance needed to detect
staleness.

Preview has **zero database schedule side effects**.

## Commit result

Return: - committed dates; - affected operation IDs; - new
ScheduleRevision; - audit/correlation ID; - concise user message.

------------------------------------------------------------------------

# 7. Finite-capacity algorithm --- one source of truth

For each affected machine:

1.  load canonical machine calendar;
2.  load all authoritative current active allocation fragments that can
    conflict;
3.  exclude fragments belonging to the schedule revision being replaced;
4.  subtract occupied fragments from usable productive intervals;
5.  feed effective free capacity into the canonical scheduler;
6.  use returned `ConsumedIntervals` as the new allocations;
7.  propagate routing dependencies;
8.  recompute operation/route-step/WO rollups;
9.  validate again under locks at Commit.

Half-open overlap: `A.Start < B.End && A.End > B.Start`.

Adjacent: `A.End == B.Start` is allowed.

Do not validate capacity using only the 3,000-row UI board. The
authoritative Commit query is independent of board limits.

## Initial Work Order scheduling

This is a mandatory closure item: identify every path that can
create/update Released machine schedule dates (Release/Recalculate/other
command).

If initial Release can create overlaps without writing
`PrMachineScheduleAllocation`, the planner's finite-capacity invariant
is incomplete.

Before planner write enablement, either: 1. make Release/future schedule
creation use the same allocation/finite-capacity service; **preferred**,
or 2. keep planner write readiness blocked and document why.

No bypass path is acceptable in production.

------------------------------------------------------------------------

# 8. Released/InProgress schedule-only mutation

Allowed only for affected operations proven unstarted/unlocked.

Hard blockers include: - NEW ProductionOutput; - POSTED
ProductionOutput; - REVERSED ProductionOutput unless separately approved
correction semantics exist; - execution quantities/movements proving
started work; - terminal WO; - stale schedule revision; - unsupported
backward scheduling; - unresolved calendar/allocation readiness.

Schedule-only commit may change: - WO/route/operation/machine planned
schedule fields required by propagation; - ScheduleRevision; -
allocation fragments; - schedule audit.

It must **not** change: - SnapshotRevision; - SnapshotHash; -
BOM/material; - selected machine; - quantities; - costing; - production
output/history.

Because SnapshotHash contains planned dates, add a clear code comment
explaining why the hash is intentionally frozen for a
Released/InProgress schedule-only amendment.

------------------------------------------------------------------------

# 9. Transaction and lock contract

Commit must be atomic.

High-level order must be verified against existing production services
before implementation:

1.  begin SQL transaction;
2.  acquire affected physical machine application locks in deterministic
    sorted order;
3.  acquire same WO execution application lock used by Daily Production;
4.  lock/reload WO and affected operation/machine rows;
5.  re-run execution blockers;
6.  re-run schedule revision/RowVersion checks;
7.  reload current calendar/provenance;
8.  reload authoritative allocations;
9.  recompute or verify preview equivalence;
10. update schedule fields;
11. delete/replace only superseded allocation fragments;
12. increment ScheduleRevision;
13. write audit;
14. save;
15. commit.

Any exception/validation/concurrency failure rolls everything back.

Do not acquire branch stock lock merely for a pure schedule move unless
verified necessary. Instead ensure the execution app-lock ordering is
compatible with Daily Production Create/Post/Rollback and cannot
deadlock.

------------------------------------------------------------------------

# 10. Readiness that can actually pass

Replace permanent fail-closed placeholder with **evidence-backed
readiness**, not a bypass.

`IsWriteReady` must be derived from real runtime/deployment state:

-   company setting enabled;
-   allocation schema ready;
-   active allocation backfill complete;
-   unverified allocation count = 0;
-   stale allocation count = 0;
-   unexplained active overlap count = 0;
-   calendar/release evidence valid;
-   backward writes either supported or individual backward jobs remain
    locked;
-   mandatory deployment version/evidence present.

## Evidence storage

Use the smallest durable mechanism consistent with existing project
patterns. Acceptable options: - app setting/version marker written only
by the successful preflight/admin enable command; - dedicated small
readiness record if necessary.

It must contain enough identity to invalidate when relevant
schema/calendar/allocation release state changes.

Do not require ordinary users to manually set technical flags.

## Branch bug to fix

Current `CountActiveOverlapsAsync` accepts only `companyCode` and SQL
filters only company. Readiness is company+branch scoped.

Fix overlap scope to the actual physical machine identity: - if machine
identity is company-wide by master design, do not incorrectly isolate
branch capacity; - if machines are branch-specific, include BranchCode
consistently.

**First verify PrMachine identity/master scope and real scheduling
semantics. Do not blindly add BranchCode.**

This is important because a physical machine must never be double-booked
merely because two branch codes reference it.

------------------------------------------------------------------------

# 11. Allocation backfill --- executable, not report-only

Current preflight SQL is report-only.

Add a controlled application/maintenance command for active
Released/InProgress selected machines:

1.  load each current persisted schedule;
2.  use saved schedule/calendar provenance and canonical scheduler to
    reconstruct `ConsumedIntervals`;
3.  require reconstructed outer start/end to match persisted schedule
    within exact defined rules;
4.  write fragments only when verified;
5.  mark/report any schedule that cannot be reproduced;
6.  detect existing overlaps;
7.  never silently reschedule old WOs.

Backfill must be: - idempotent; - tenant/scope controlled; -
transaction-safe; - dry-run/report mode first; - auditable.

Write readiness remains false if any required active allocation is
unverified or overlap unresolved.

------------------------------------------------------------------------

# 12. Calendar / preventive maintenance impact

Before write enablement, calendar and preventive writers must protect
future committed allocations.

On shift/holiday/maintenance changes affecting future allocation
fragments:

**Preferred SME behavior:** block save with a clear list/count of
affected scheduled jobs and link/open Machine Schedule Planner to
resolve them.

Alternative `Needs Attention` policy is acceptable only if fully
implemented and production execution cannot silently proceed against
invalid schedule assumptions.

Never automatically rewrite executed history.

Calendar release evidence must become stale when relevant future
calendar/master changes occur, so readiness cannot stay green on
obsolete evidence.

------------------------------------------------------------------------

# 13. UI drag/drop implementation

Only after Preview works:

-   `AppointmentDragMode=Server`;
-   same-machine drag only;
-   `AllowDragAppointmentBetweenResources=false`;
-   resize remains false;
-   create/delete false.

Verify DevExpress 26.1.4 behavior with the actual installed package.

Intercept `AppointmentUpdating` (or exact supported pre-update event).
Cancel default appointment mutation and convert it into Preview intent.

If `AllowEditAppointment=false` prevents the required drag event
combination in 26.1.4, use the documented combination proven by a
component test. Do not guess.

Eligibility: - board globally allows drag only when readiness permits; -
individual locked appointment drops are canceled with a human reason; -
backward unsupported job stays locked; - permission checked server-side.

## Alternative Change Schedule

Always provide for eligible jobs.

Popup: - Requested date; - requested time; - reason (if audit policy
requires); - Preview.

Same Preview/Commit APIs as drag.

This is not duplicate scheduling logic.

------------------------------------------------------------------------

# 14. Completion certificate --- the end of the story

The AI coding agent **must not finish with "remaining future work."**

It may report this feature completed only when this exact matrix is
attached and every mandatory row is PASS.

  -------------------------------------------------------------------------
  Gate                                Required evidence
  ----------------------------------- -------------------------------------
  Build                               solution/project build passes

  Existing tests                      relevant Planning + Production
                                      regression tests pass

  Schema                              allocation schema/index/check
                                      verified

  Backfill                            active required allocations verified;
                                      missing=0

  Overlap                             unexplained active overlap=0

  Provenance                          stale allocation=0

  Initial release path                cannot create unallocated/overlapping
                                      Released machine load

  Snapshot freeze                     schedule move leaves
                                      SnapshotHash/Revision unchanged

  Finite capacity                     split
                                      shifts/breaks/holiday/PM/occupied
                                      fragments tests

  Dependency                          serial/parallel/internal-WIP
                                      propagation tests

  Atomicity                           injected failure leaves
                                      dates/allocations/revisions/audit
                                      unchanged

  Planner race                        same-machine simultaneous commit test

  Adjacent jobs                       boundary scheduling succeeds

  DP Create race                      planner vs NEW draft test

  DP Post race                        planner vs Post test

  DP Rollback race                    planner vs rollback test

  Calendar race                       planner vs calendar/PM writer test

  Backward                            proven PASS **or write explicitly
                                      disabled per job**

  Readiness                           can become true only with real
                                      evidence

  Feature OFF                         page stays safe read-only

  Feature ON + ready                  drag + Change Schedule enabled

  Drag                                browser drag → preview → confirm → DB
                                      reload

  Cancel                              no DB/UI ghost change

  Stale preview                       rejected and refreshed

  Timeline Day                        readable

  Timeline Week                       readable

  Timeline Month                      readable, no truncated time/date
                                      headers

  Zoom                                −/+ /Reset resize display only

  Scroll                              horizontal scroll usable

  Filters                             compact, searchable
                                      WO/Product/Process/Machine/WC

  Responsive                          1366×768, 1920×1080, 125% Windows
                                      scaling

  Browser console                     no unhandled/DevExpress expression
                                      errors

  SQL tests                           run with
                                      `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`,
                                      mandatory tests not skipped

  UAT                                 first-time operator completes find →
                                      move → preview → confirm without
                                      developer help

  Rollback                            disabling write feature immediately
                                      returns safe View Only
  -------------------------------------------------------------------------

**Any FAIL or UNKNOWN = NOT COMPLETE.**

The agent must include: - changed-file list; - migration/scripts; - test
command + actual result; - SQL concurrency logs; - browser/Playwright
evidence; - before/after screenshots; - readiness/preflight output; -
this matrix populated PASS/FAIL; - deployment + rollback steps.

No deferred "future preview/commit phase" is accepted.

------------------------------------------------------------------------

# 15. Browser acceptance script

A tester unfamiliar with the implementation must be able to:

1.  open Machine Schedule;
2.  understand whether it is View Only or editable;
3.  choose Work Order via lookup, not memorize/type code;
4.  optionally filter machine/process/product;
5.  read machine/date/time headings;
6.  switch Day → Week → Month without jumping unexpectedly to Today;
7.  use `+` to widen Month columns and see full headers;
8.  scroll horizontally;
9.  select a job and understand its status;
10. drag an eligible job on the same machine;
11. see current vs proposed schedule and downstream impact;
12. Cancel and observe no change;
13. drag again → Confirm;
14. see success and refreshed persisted schedule;
15. use Change Schedule to perform another allowed move;
16. attempt a collision and receive understandable feedback;
17. attempt a locked/started job and understand why it cannot move.

If the tester needs a developer to explain how to perform steps 3--15,
UX acceptance FAILS.

------------------------------------------------------------------------

# 16. Implementation order --- do not reorder

1.  Freeze scope; create failing tests for current known defects.
2.  Complete/reuse compact lookups.
3.  Fix timeline layout/scales/zoom/scroll and view navigation.
4.  Implement Preview service with zero writes.
5.  Implement canonical finite-capacity allocation composition.
6.  Implement allocation backfill/dry-run.
7.  Protect initial Release/future schedule creation from capacity
    bypass.
8.  Implement Commit transaction/locks/audit/schedule revision.
9.  Complete DP Create/Post/Rollback serialization tests.
10. Implement calendar/PM impact invalidation/block.
11. Implement evidence-backed readiness.
12. Wire Change Schedule.
13. Wire drag → preview (cancel default mutation).
14. Run SQL Server race suite.
15. Run browser UAT.
16. Enable write only in controlled scope.
17. Produce §14 completion certificate.

Do not start by turning on drag.

------------------------------------------------------------------------

# 17. Final prohibitions

The implementation is rejected if it:

-   changes `IsWriteReady` to literal true;
-   deletes the readiness gate;
-   simply toggles `AllowDragAppointment`;
-   persists browser appointment dates directly;
-   uses UI board rows as authoritative capacity;
-   recomputes Released/InProgress SnapshotHash for schedule-only move;
-   changes BOM/costing/qty/machine selection;
-   permits cross-machine drag;
-   permits duration resize;
-   auto-reschedules the whole factory without explicit preview;
-   silently changes old WOs during backfill;
-   stretches all filter controls;
-   leaves Month headers truncated with no usable resize/scroll;
-   reports "done" while any §14 gate is outstanding.

------------------------------------------------------------------------

# 18. Final approval

**This plan is APPROVED FOR IMPLEMENTATION against the inspected
`productionv2` architecture.**

It intentionally converts the previous open-ended design into a finite
closure contract.

The feature itself is **PRODUCTION READY only after §14 is all PASS**.
Until then, fail-closed View Only is correct.

The desired final state is simple:

> Planner opens → user finds WO/machine easily → calendar is readable →
> user can widen Month columns → eligible job drags → Preview explains
> impact → Confirm safely reschedules → no machine overlap → production
> snapshot/costing/history untouched.

That is the completion target. No additional redesign cycle should be
required unless implementation evidence exposes a concrete repository
contradiction.
