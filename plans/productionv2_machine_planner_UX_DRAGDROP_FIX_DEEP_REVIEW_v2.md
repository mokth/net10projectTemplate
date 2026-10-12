# productionv2 --- Machine Planner UX and Drag/Drop Recovery: Deep Review v2

**Review date:** 12 October 2026\
**Branch inspected:** `mokth/net10projectTemplate`, `productionv2`\
**Status:** **APPROVED AS A CORRECTIVE IMPLEMENTATION SPECIFICATION; NOT
APPROVED FOR PRODUCTION WRITE until tests and all safety gates pass.**

## Executive decision

The prior fix plan was directionally right but insufficiently precise.
It risked another UI that *looks* editable but cannot safely save. **Do
not simply toggle `AllowDragAppointment=true`.** Deliver a real
end-to-end Preview → Confirm → Commit workflow, and keep write mode
disabled until all release evidence is present.

## 1. Verified repository defects

  --------------------------------------------------------------------------------------------------------------
  Priority          Code                                 Verified finding                      Required
                                                                                               correction
  ----------------- ------------------------------------ ------------------------------------- -----------------
  P0                `PrMachineSchedulePlanner.razor`     `AllowDragAppointment=false`,         Do not enable
                                                         `AllowEditAppointment=false`,         drag before safe
                                                         `AllowResizeAppointment=false`        server
                                                                                               preview/commit
                                                                                               exists.

  P0                same                                 `Reschedule` button permanently       Replace with
                                                         `Enabled=false`                       functional
                                                                                               **Change
                                                                                               Schedule** when
                                                                                               authorized and
                                                                                               ready; otherwise
                                                                                               explain read-only
                                                                                               state.

  P0                `MachineSchedulePlannerService.cs`   Board/readiness service, no public    Implement
                                                         reschedule preview/commit             separately with
                                                                                               mandatory
                                                                                               safety-gate
                                                                                               enforcement.

  P0                same                                 Readiness adds                        Implement real
                                                         `CALENDAR_RELEASE_EVIDENCE_MISSING`   evidence-backed
                                                         and returns `IsWriteReady=false`      readiness; never
                                                                                               hardcode `true`.

  P1                `.razor`                             WO/Product/Process individual lookups Add separate
                                                         absent                                searchable
                                                                                               lookups with
                                                                                               code +
                                                                                               description.

  P1                `.razor.css`                         `.filters > * { flex:1 1 150px }`     Explicit compact
                                                         stretches all controls                semantic widths;
                                                                                               no universal
                                                                                               flex-grow.

  P1                `.razor`                             Timeline 54/82/130px cells and        View-specific
                                                         identical Day + 6-hour scales         readable scales,
                                                                                               zoom, scroll and
                                                                                               header templates.

  P1                `.razor.css`                         Board `overflow:hidden`, permanent    Preserve real
                                                         260--320px detail sidebar             scheduler
                                                                                               scrolling;
                                                                                               collapsible
                                                                                               details panel.

  P1                `.razor.cs`                          Filter options accumulated from       Load scoped
                                                         current board data                    lookup options
                                                                                               independently of
                                                                                               visible week.

  P2                `.razor.cs`                          `ChangeViewAsync` resets Day view to  Preserve selected
                                                         *today*                               date when
                                                                                               changing views;
                                                                                               Today is
                                                                                               separate.

  P2                `.razor.cs`                          `ReloadAsync` returns immediately if  Queue latest
                                                         already loading                       filter/date
                                                                                               request or cancel
                                                                                               stale fetch;
                                                                                               avoid outdated
                                                                                               board.
  --------------------------------------------------------------------------------------------------------------

## 2. User workflow --- zero guesswork

**Planner opens:** prominent heading `Machine Schedule`, concise status
`View only` or `Rescheduling enabled`; one-line helper
`Choose a machine or Work Order, then select a job. Drag to move, or choose Change Schedule.`

**Select:** one click on bar reveals WO, product, operation, machine,
planned dates, lock status. `Open Work Order` and `Change Schedule` are
visible actions.

**Move:** drag eligible bar within **same machine row**; on drop, do not
silently persist. Show one modal: - `WO-001 · CUTTING · MC-01` -
**Current:** 12 Oct 08:00 → 12 Oct 13:00 - **Proposed:** 13 Oct 08:00 →
13 Oct 13:00 - **Also affected:** number of downstream operations +
expandable details - **Issues:** overlap, maintenance, late risk,
execution lock (if any) - buttons **Cancel** and **Confirm Reschedule**.

**Alternative:** Change Schedule opens compact date/time picker and
calls the **same preview and commit APIs**. Users never need drag for
precision or accessibility.

**Read-only:** no dead button or fake drag affordance. Show
`View only — scheduling changes not enabled` and concise reason; admin
can inspect details.

## 3. Compact filter design

Place nav + Day/Week/Month + Zoom/Reset above a separate filter strip.

  ---------------------------------------------------------------------------
  Field            Control                  Preferred width Behavior
  ---------------- ------------------ --------------------- -----------------
  Work Centre      Searchable                         175px code + name; All
                   `DxComboBox` /                           
                   existing                                 
                   `IvCodeComboBox`                         

  Machine          Searchable combo                   205px code + name; All

  Process          Searchable combo                   175px code +
                                                            description; All

  Work Order       Searchable                         210px WO + product +
                   lookup/popup grid                        status

  Product          Searchable item                    210px code +
                   lookup                                   description

  Status           Combo                              135px All/Released/In
                                                            progress
  ---------------------------------------------------------------------------

Widths are UX starting values, not hardcoded minimums on narrow screens.
Use `max-width` / CSS grid auto-flow, `minmax` thoughtfully, and
responsive wrapping. **Never** set `flex-grow:1` on every field. Keep
labels visible. Clear Filters resets all. Prefer
`SearchMode=ListSearchMode.AutoSearch` as used in `SaInvoice.razor`; use
scoped server-side lookup paging when lists are large.

Add exact query fields `WorkOrderNo`, `ProductCode`, `OperationCode`;
retain general text search only as secondary convenience. **Do not
derive lookup choices from the current week's appointments**. Work
Centre/Machine dependency must follow verified master relationship, not
assumed relation. Avoid unnecessary Apply/Refresh clicks: filter
selection reloads; debounce optional free-text; cancel stale queries.

## 4. Timeline design --- full dates, scroll and zoom

Timeline gets the majority of horizontal space. Details become a
collapsible drawer (or below-board panel); avoid a permanently wide
right sidebar.

-   **Day:** hours with clear date heading.
-   **Week (default):** day/date plus appropriate hour divisions,
    horizontally scrollable.
-   **Month:** week/day scales; do **not** use six-hour subdivisions for
    all 30/31 days.
-   **Zoom − / Zoom + / Reset:** adjust `CellMinWidth` within tested
    bounds. `Fit` may select a coarser scale rather than making dates
    illegibly narrow.
-   Visible horizontal scrollbar and full header tooltip/title; do not
    clip the scheduler's internal scrolling region.
-   Machine row header shows code and concise name, full name on hover.
-   Bar shows `WO · Product` and lock indicator where space permits;
    tooltip shows complete WO, process, machine, start/end and quantity.
-   Maintenance/non-working shading must not obscure job bars.
-   Legend: Scheduled, Started/Locked, Maintenance, Nonworking, Needs
    attention.

**Verified DevExpress 26.1 APIs:**
`DxSchedulerTimelineView.DateHeaderCellTemplate`,
`ResourceHeaderCellTemplate`, `HorizontalAppointmentTemplate`,
`CellMinWidth`, `VerticalResourceCellWidth`, and `SnapToCellsMode`. Use
supported templates/properties rather than brittle DOM CSS. See official
DevExpress docs in the implementation references.

**Date correctness:** `ChangeViewAsync` must not jump to today. Month
anchor must be the first day of month when navigating month. Date-window
bounds and timeline duration must match exactly.

## 5. Functional drag/drop contract

1.  Verify actual installed DevExpress Blazor package version and test a
    minimal prototype with grouped timeline resources.
2.  Use `AppointmentDragMode=Server`, enable drag only for
    authorized/readiness-ready operation and ensure locked items cannot
    be moved. If DevExpress only exposes a global drag enable flag,
    cancel each ineligible item via event and show reason; do not
    pretend per-item property exists.
3.  `AllowDragAppointmentBetweenResources=false`: machine change is out
    of scope. `AllowResizeAppointment=false`: duration change is out of
    scope.
4.  Use `AppointmentUpdating` (DevExpress 26.1 event) to **cancel the
    default source update** and capture intent (operation ID, machine
    ID, proposed start, expected revisions). Do not rely on
    `AppointmentUpdated` after mutation.
5.  Server `PreviewRescheduleAsync` computes snapped dates, productive
    intervals, dependencies, rollups, conflicts and execution
    eligibility. Browser dates are only requested anchors.
6.  Preview dialog shows exact before/after plus all affected downstream
    operations; nothing is saved on preview.
7.  `CommitRescheduleAsync` validates signed/opaque preview token,
    reacquires SQL locks, rechecks
    readiness/execution/allocations/calendar/revisions, recomputes,
    atomically commits dates + allocation fragments + audit.
8.  Stale/failed/cancelled commit restores original board; successful
    commit reloads from DB. Never retain ghost bars as truth.
9.  Keyboard and touch-friendly **Change Schedule** path uses identical
    backend calls.

**Important:** `AllowEditAppointment` governs built-in editing, and
`AllowDragAppointment` governs drag. Verify through a DevExpress
component integration test that the desired combination triggers
`AppointmentUpdating`; do not assume it works merely because it
compiles.

## 6. Mandatory production protection

All 20 safety gates in
`plans/productionv2_machine_schedule_planner_FINAL_APPROVED_WITH_MANDATORY_SAFETY_GATES.md`
remain mandatory. In particular:

-   No cross-WO productive-fragment overlap on a physical machine.
-   Use canonical
    `ProductionCalendarScheduler.ScheduleMachine(...).ConsumedIntervals`.
-   Subtract existing allocations from usable intervals **before**
    placing the moved job.
-   Existing Released/InProgress machine allocations fully
    backfilled/verified before write enablement.
-   NEW/POSTED/REVERSED Daily Production and executed quantities block
    movement of affected operations.
-   Daily Production **Create/Post** and planner Commit share compatible
    SQL serialization boundary; SQL lock order proven.
-   Preview and Commit revalidate; no trusting browser-provided dates.
-   All affected dependencies and parent rollups recomputed correctly;
    unrelated operations frozen.
-   Frozen `SnapshotRevision`/`SnapshotHash`, BOM, costing, quantities,
    selected machine and history unchanged.
-   Calendar/preventive writers cannot silently invalidate future
    allocations.
-   Backward WOs stay view-only until tested.
-   `IsWriteReady` must derive from real evidence, not hardcoded bypass.
-   Atomic rollback on failure; full audit.
-   Existing release/recalculate paths cannot bypass finite-capacity
    rules.
-   SQL Server tests run with `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`, no
    mandatory skips.

**Additional hard gate:** Current board query caps results at 3,000.
**Never allow commits when occupancy/affected dependency data is
incomplete because of a UI cap.** Commit must query authoritative
complete occupancy independently of the board.

## 7. Work plan with measurable exit criteria

### Phase A --- read-only UX recovery (no scheduling writes)

Implement semantic-width filters, lookups, readable timeline,
zoom/scroll, collapsible details, help text, accurate read-only status,
correct view navigation and stale-request handling.

**PASS:** a first-time operator can locate a WO and machine without
memorizing codes; dates readable at 1366×768 and 125% Windows scaling;
no horizontally clipped headings without scroll; selected filters
survive date navigation.

### Phase B --- real preview (no scheduling writes)

Add authoritative Preview API and drag-to-preview + Change Schedule
popup. Default DevExpress appointment update is canceled.

**PASS:** user can drag, see exact before/after and downstream impact;
Cancel leaves DB and UI unchanged; locked operations explain why; no
phantom schedule changes.

### Phase C --- safe commit

Implement finite-capacity scheduling, transactional allocation
replacement, concurrency, audit and evidence-backed readiness. Do not
activate writes until every gate passes.

**PASS:** drag → preview → confirm → persisted reload works, no machine
overlaps, no snapshot/costing/history mutations, concurrency tests pass.

### Phase D --- staged rollout

Read-only → preview-only → one controlled company/branch → broad
enablement only after UAT.

**PASS:** release certificate with Gate 1--20 evidence; operator UAT and
rollback drill signed off.

## 8. Required browser UAT, not only code inspection

Test real browser interactions using Playwright/manual evidence: 1. At
1366×768, 1920×1080, 125% scaling, narrow window. 2. Work Centre,
Machine, Process, WO, Product lookups, descriptions, Clear, change
week/month, reload. 3. Long machine/product names; 20+ machine rows;
overlapping text; midnight jobs; maintenance shading. 4. Zoom −/+ and
horizontal scrolling; full date/time caption remains accessible. 5. Drag
eligible WO on same machine; preview opens; Cancel; confirm; reload
shows persisted dates. 6. Locked operation, read-only company, backward
WO, stale token, collision, failed network, rapid double click. 7.
`Change Schedule` without drag performs identical workflow. 8. Two
simultaneous planners; simultaneous Daily Production Create/Post;
calendar/preventive writer race. 9. Browser console clean; no DevExpress
validation-expression errors; all loading/empty/error states usable.

**UAT standard:** Ask a production clerk unfamiliar with the feature to
(a) find WO by lookup, (b) find machine, (c) understand whether job is
movable, (d) reschedule and confirm, (e) explain downstream impact.
Observe actual clicks/confusion; revise until successful without
instruction beyond one-line help.

## 9. AI coding agent deliverables and prohibitions

Deliver changed-file list, screenshots, Playwright/browser recording,
SQL test logs, gate 1--20 evidence matrix, migration/preflight and
rollback guide.

Do not: - merely set `AllowDragAppointment=true`; - hardcode readiness
to true; - mutate appointment source directly on drop; - allow
cross-machine drag or resize; - add generic free-text inputs instead of
lookups; - stretch all short fields; - rely on UI data cap for
authoritative occupancy; - alter production
snapshot/costing/BOM/execution; - claim production-ready before browser
UAT + mandatory safety gates.

**Final review:** corrective plan **approved for AI-agent
implementation**, but **production-write enablement remains blocked**
until implementation and evidence prove every gate PASS.

## 10. Official DevExpress implementation references

-   Scheduler update interception:
    https://docs.devexpress.com/Blazor/DevExpress.Blazor.DxScheduler.AppointmentUpdating
-   Timeline view options:
    https://docs.devexpress.com/Blazor/DevExpress.Blazor.DxSchedulerTimelineView.\_properties
-   Date header template:
    https://docs.devexpress.com/Blazor/DevExpress.Blazor.DxSchedulerTimelineView.DateHeaderCellTemplate
-   Resource header template:
    https://docs.devexpress.com/Blazor/DevExpress.Blazor.DxSchedulerTimelineView.ResourceHeaderCellTemplate
-   Drag mode:
    https://docs.devexpress.com/Blazor/DevExpress.Blazor.DxScheduler.AppointmentDragMode
