# productionv2 Machine Schedule Planner — Business-First UI / Workflow Enhancement

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Strict re-review HEAD:** `80dd0b7f9279988650a98c64b6c657f339adb6ee`  
**Latest reviewed commit:** `checked costing --continue`  
**Plan status:** **APPROVED FOR IMPLEMENTATION — 10/10 (after strict repository re-review)**

> Approval applies to this implementation plan only. It does **not** declare Machine Schedule write mode production-ready. The existing planner write feature flag, readiness evidence, allocation integrity, concurrency gates, and broader Planning/Production regression gates remain authoritative.

---

## 1. Strict re-review result

The previous version was **not yet 10/10**. This re-review found and fixes the following repository-level gaps:

1. **Selected idle machine would still not render a timeline.**  
   Current Razor shows the empty state whenever `board.Jobs.Count == 0`; therefore merely adding an idle machine resource is insufficient.

2. **Visible board and authoritative capacity were inconsistent across branches.**  
   Current board jobs are branch-scoped, but `MachineScheduleRescheduleService` correctly treats physical machine capacity as **company-wide by CompanyCode + MachineCode**. A machine can therefore look free on the current branch board while another branch has authoritative capacity reserved on it.

3. **Late WO calculation lacked the WO-level planned completion field.**  
   Existing `MachineSchedulePlannerJob.PlannedCompletion` is the selected operation/machine completion, not the Work Order completion used by the intended due-date rule.

4. **Attention counts could over-count the same Work Order.**  
   One WO can produce several operation bars. `Late WOs` and `Due WOs` must count distinct `WorkOrderId`, not bars/operations.

5. **Priority lookup design was unnecessarily complex.**  
   Repository already defines `SaDeliveryRequestPriorities.Normal`, `High`, and `Urgent`. A new dynamic lookup is unnecessary.

6. **Allocation backfill is a maintenance write but currently requires only planner Edit.**  
   Once moved under diagnostics, UI-only hiding is not enough. The service authorization should be hardened using the existing Admin Settings permission.

7. **Work Order lookup can appear broken for WOs outside the current date window.**  
   The lookup searches all active WOs, while the board only loads the visible date range. Selecting a valid WO scheduled next week can currently return an empty board this week.

8. **Unverified capacity/calendar data must not be hidden as “technical detail.”**  
   Raw readiness codes belong in diagnostics, but a human-readable warning that the visible capacity may be incomplete remains business-critical.

9. **Large machine-code lists need explicit SQL Server parameter-safety.**  
   The board allows up to 3,000 operations; calendar/capacity queries must chunk large `Contains` lists rather than assume fewer than SQL Server’s practical parameter limit.

All nine corrections are mandatory in this revised plan.

---

## 2. Objective

Turn **Planning → Production → Machine Schedule** into a production planner’s working board that immediately answers:

1. What is scheduled on each machine?
2. Which visible Work Orders are late against their current Delivery Request required date?
3. Which jobs are NORMAL / HIGH / URGENT?
4. Which operations are already started/locked?
5. Where are machine non-working time and preventive maintenance?
6. Is apparently free machine time actually reserved by another branch?
7. Which eligible operation can be safely moved on the same machine?
8. What downstream impact will a move cause before confirmation?

The core workflow remains:

`Released/In-Progress WO → selected machine operation → timeline → server preview → user confirm → atomic same-machine commit`

This is a **UI + planner read-model + permission-hardening enhancement**. It must reuse the current scheduler, allocation, calendar, locking, preview/commit and audit logic.

---

## 3. Repository truths that MUST drive the implementation

### 3.1 Schedule writes

`MachineScheduleRescheduleService` remains the single authoritative schedule-write path.

Preserve:

- Released/In-Progress only;
- same selected machine only;
- no cross-machine drag;
- backward schedules remain blocked;
- execution quantities block rescheduling;
- NEW/POSTED Daily Production blocks affected operations;
- preview is read-only;
- commit requires fresh preview token;
- WO + machine rowversion and ScheduleRevision checks;
- company-wide physical machine capacity;
- sorted machine locks;
- scheduling-source lock;
- WO execution lock;
- atomic SQL transaction;
- allocation fragment replacement;
- schedule audit;
- snapshot hash/revision freeze for schedule-only changes.

**No UI code may reproduce or bypass this logic.**

### 3.2 Demand relationship

A WO links to at most one Delivery Request through:

- `PrWorkOrderDemandAllocation`
- unique index `UQ_PrWorkOrderDemandAllocation_WorkOrder`

Delivery Request already provides:

- `DeliveryRequestNo`
- `RequiredDate`
- `Priority`
- `ProjectCode`
- `WarehouseCode`
- `Status`

No duplicate demand columns are required on Work Order.

### 3.3 Priority values

Reuse repository constants:

- `SaDeliveryRequestPriorities.Normal`
- `SaDeliveryRequestPriorities.High`
- `SaDeliveryRequestPriorities.Urgent`

Do not add another priority master/lookup or hardcode a different set.

### 3.4 Capacity scope

The current rescheduler/readiness logic treats physical machine identity as:

`CompanyCode + MachineCode`

Capacity is therefore company-wide even though the planner’s WO/job list is branch-scoped.

The UI must not visually imply that a slot is free when another branch has a current verified allocation on the same physical machine.

---

## 4. Scope

### In scope

- business-first planner layout;
- compact controls;
- current DR demand context;
- exact late/due indicators;
- priority filtering using existing constants;
- explicit idle-machine visibility;
- selected idle machine calendar rendering;
- anonymized other-branch machine-capacity overlays;
- useful View Only mode;
- admin-only planner diagnostics;
- work-order lookup date navigation;
- human-readable capacity-integrity warning;
- read-model/performance tests;
- browser UAT and regression verification.

### Out of scope

- machine reassignment;
- cross-machine drag;
- appointment duration resize;
- BOM/Product Definition changes;
- planned quantity changes;
- machine-selection changes;
- auto-optimization/AI scheduling;
- WIP/FG/stock/costing changes;
- Daily Production posting/rollback changes;
- new schema for demand or preferences;
- automatic whole-factory rescheduling.

---

## 5. UI standard evidence

```text
UI STANDARD EVIDENCE
Target module: Planning / Production
Target page: PrMachineSchedulePlanner
Page archetype: finite-capacity machine timeline / production planning workspace
Closest live references: PrWorkOrderEntry, PrWorkOrderList, PrDailyProductionList
Shared controls: DxScheduler, DxButton, DxComboBox, DxPopup, DxCheckBox, existing iv-* shell
Shared CSS: site.css + current iv-page / iv-hero / iv-card patterns
Route: /planning/work-orders/machine-schedule
Workflow actions: inspect → select → preview → confirm
Editable status: RELEASED / IN_PROGRESS only when existing service says eligible
Permissions: planner Access/Edit, Work Order Access, Admin Settings Edit for diagnostics
Intentional deviation: timeline rather than CommonDataGridEx because time/capacity is the primary domain
```

```text
PRODUCTION UI STANDARD EVIDENCE
Target workflow: understand machine load and safely change timing of eligible unstarted operations
Phases: find → inspect → preview → confirm
Locked identity: WO + route/operation + selected machine
Post/Rollback: not owned by this page
Historical facts: saved WO/machine schedule + execution blocker state
Live values: current DR demand context, machine calendar, maintenance, readiness/capacity integrity
```

---

## 6. Target operator workflow

### 6.1 Normal flow

1. Open Machine Schedule.
2. Default Week view opens.
3. By default show machines with visible current-branch scheduled jobs.
4. Show any authoritative **other-branch capacity reservations** on those same machines as anonymized locked bars.
5. Planner sees late/due/priority/locked/maintenance information.
6. Search/select Work Centre, Machine or Work Order.
7. If selected WO is outside the current date window, automatically move the board to that WO’s scheduled period once and show a short status message.
8. Select a job bar.
9. Review current DR required date / priority and execution state.
10. Use `Change Schedule` or same-machine drag.
11. Existing server Preview calculates authoritative timing/capacity/downstream impact.
12. UI also shows the business consequence against the current DR required date.
13. Confirm calls existing Commit.
14. Reload persisted schedule.

### 6.2 View Only flow

If Edit permission, feature setting or runtime readiness prevents schedule changes:

- board remains a useful inquiry;
- timeline, DR context, maintenance and capacity reservations remain visible;
- display a concise message:
  `Schedule changes are currently disabled. You can still review machine load and Work Order timing.`
- raw readiness codes do not dominate the page;
- authorized admins can open Planner Diagnostics.

---

## 7. Target UI layout

### 7.1 Hero

Keep `iv-hero`.

**Title:** `Machine Schedule`

**Subtitle:**  
`Review machine load, due Work Orders and maintenance. Move eligible jobs only after schedule preview.`

Hero/chips:

- mode: `View only` / `Rescheduling enabled`;
- current date range;
- KPI: **Late WOs**.

If the 3,000-job guard truncates the board, make it explicit that attention counts are based on the loaded subset.

### 7.2 Date/view row

Keep:

- Previous
- Today
- Next
- period label
- Day / Week / Month
- Zoom − / + / Reset

Do not reset to Today when switching view.

### 7.3 Primary filter row

Always visible:

- Search — `WO, DR, product or process`
- Work Centre
- Machine
- Work Order
- `Show idle machines` compact `DxCheckBox`
- `MORE FILTERS`
- Refresh

Work Order remains a searchable server lookup.

### 7.4 More Filters popup

Use `DxPopup` + `common-popup`.

Draft/apply model:

- Process
- Product
- Status
- Priority (`NORMAL`, `HIGH`, `URGENT` via repository constants)
- `Late only`

Actions:

- Clear
- Cancel
- Apply

Do not reload for draft secondary-filter changes.

---

## 8. Exact demand/late semantics

### 8.1 Extend `MachineSchedulePlannerJob`

Add read-model-only fields:

- `DateTime WorkOrderPlannedStart`
- `DateTime WorkOrderPlannedCompletion`
- `long? DeliveryRequestId`
- `string? DeliveryRequestNo`
- `string? DeliveryRequestStatus`
- `DateTime? RequiredDate`
- `string? Priority`
- `string? ProjectCode`
- `string? WarehouseCode`
- `bool DemandAllocationIsActive`
- `bool HasActiveDemandContext`
- `bool IsLateToRequiredDate`
- `int LateDays`

These are DTO/read-model fields only. No EF schema change.

### 8.2 Active demand context

For late/due/priority business logic, demand is active only when:

- demand allocation exists;
- `IsActive == true`;
- DR status is `RELEASED` or `IN_PRODUCTION`.

A cancelled/completed/inconsistent DR link may still be displayed for investigation, but it must not drive normal late/priority attention.

### 8.3 Exact lateness rule

A WO is late when:

```text
HasActiveDemandContext
AND RequiredDate exists
AND WorkOrderPlannedCompletion.Date > RequiredDate.Date
```

`LateDays` is the **calendar-date difference**:

```text
(WorkOrderPlannedCompletion.Date - RequiredDate.Date).Days
```

Do not use operation completion for the late rule.

Do not compare time-of-day to a date-only requirement.

Do not invent working-day lateness unless the business requirement later changes.

### 8.4 Live vs historical labeling

DR values are current demand context, not a frozen Work Order snapshot.

Use labels such as:

- `Current DR Required Date`
- `Current Priority`

Do not present them as historical release-time WO facts.

---

## 9. Demand query/filter implementation

Primary file:

`ErpWeb.Core/Production/MachineSchedulePlannerService.cs`

### 9.1 Extend query

Add:

- `string? Priority`
- `bool LateOnly`
- `bool ShowIdleMachines`

### 9.2 Filters MUST occur before board Take

`Priority`, `LateOnly`, and DR-number search must be translated to SQL before:

`Take(MaxBoardJobs + 1)`

Use the existing WO → demand allocation → DR relationship/subquery.

Never:

1. load 3,001 rows;
2. enrich;
3. filter in memory.

That would silently omit valid matches.

### 9.3 Search

Extend SearchText to include active linked `DeliveryRequestNo`.

Keep server-side search.

### 9.4 Priority

Do **not** add `MachineSchedulePlannerLookupKind.Priority`.

Use the existing `SaDeliveryRequestPriorities` constants in the UI/filter model.

This removes unnecessary DB work and guarantees consistency with Delivery Request Entry validation.

### 9.5 Visible-job demand enrichment

After the bounded visible jobs are selected:

1. collect distinct visible `WorkOrderId`;
2. load matching demand allocation + DR rows using `AsNoTracking`;
3. batch ID lists safely;
4. map by WorkOrderId;
5. enrich jobs in memory.

No per-job query.

Defensive tenant conditions must include current company/branch when loading demand rows.

---

## 10. Attention summary — exact counting

Add board summary fields, calculated centrally in the planner service after enrichment:

- `LateWorkOrderCount`
- `DueThisWindowWorkOrderCount`
- `LockedJobCount` — existing operation count
- `VisibleMaintenanceMachineCount`
- `ExternalCapacityMachineCount`
- `AttentionCountsArePartial`

Rules:

### Late WOs

```text
jobs
→ HasActiveDemandContext
→ IsLateToRequiredDate
→ DISTINCT WorkOrderId
→ Count
```

Never count each operation bar as another late WO.

### Due this window

Active-demand WOs whose:

```text
RequiredDate.Date >= WindowStart.Date
AND RequiredDate.Date < WindowEnd.Date
```

Count **distinct WorkOrderId**.

### Maintenance

Count distinct visible machine codes with maintenance overlapping the visible window.

### Partial counts

If `HasMoreJobsThanLimit == true`:

- `AttentionCountsArePartial = true`;
- UI says `Counts based on loaded operations`.

Do not imply the counts are company totals.

---

## 11. Idle-machine behaviour — corrected

### 11.1 Default

`ShowIdleMachines = false`

Resources consist of:

- machines used by visible scheduled jobs;
- plus explicitly selected Machine filter, even when it has zero current-branch jobs.

### 11.2 Explicit idle-machine mode

When `ShowIdleMachines = true`:

- load active machines eligible for the current branch:
  - `CompCode == current company`
  - `Active == true`
  - `BranchCode == null OR BranchCode == current branch`
- preserve the existing maximum 500 idle-machine behavior;
- always append scheduled job machines so a scheduled machine is not lost because of the idle cap.

This also corrects the current inconsistency where the board resource query does not apply the branch/global machine-master scope used by the machine lookup.

### 11.3 Zero-job scheduler rendering

This is mandatory.

Current Razor must be changed from conceptually:

```text
Jobs.Count == 0 → hide scheduler
```

to:

```text
Machines.Count == 0 → true empty state

Machines.Count > 0 → render scheduler
    even when Jobs.Count == 0
```

If an idle machine is explicitly selected:

- show its working/non-working calendar;
- show maintenance;
- show other-branch reserved capacity;
- show a small message:
  `No current-branch jobs are scheduled on this machine in the selected period.`

This is what makes spare-capacity inspection actually work.

---

## 12. Company-wide external capacity overlay

### 12.1 Why it is mandatory

Authoritative reschedule capacity is company-wide, while normal board jobs are branch-scoped.

Without this overlay:

- current branch may see an apparently free slot;
- another branch may already reserve the physical machine;
- Preview correctly rejects/moves around that reservation;
- user experiences the page as inconsistent.

The board must visually align with the authoritative capacity model.

### 12.2 Data source

Use existing `PrMachineScheduleAllocation`.

Only query external allocations when allocation schema is ready.

For visible machine codes and visible time window, load rows that are:

- same `CompanyCode`;
- `BranchCode != current branch`;
- current `WorkOrderMachine.ScheduleRevision`;
- selected machine;
- linked to active WO (`RELEASED` / `IN_PROGRESS`);
- overlap visible window;
- match current allocation identity.

Do not show stale/obsolete allocation revisions as authoritative capacity.

### 12.3 Privacy / branch isolation

Do **not** expose another branch’s:

- Work Order number;
- product;
- customer;
- Delivery Request;
- quantity.

External appointment text:

`Reserved · other branch`

Description:

`This physical machine capacity is already reserved outside the current branch.`

### 12.4 Behaviour

- external bars are locked/non-editable;
- no drag;
- no Change Schedule;
- no cross-resource interaction;
- they are capacity context, not current-branch jobs;
- they are not removed by Priority/Product/DR filters when their machine remains visible, because capacity still exists.

Use a distinct appointment label/style and text, not color alone.

### 12.5 Capacity-integrity warning

If readiness says any of these are non-zero/not ready:

- missing allocations;
- stale allocations;
- unexplained overlaps;
- allocation schema missing;

show a human business warning above the board:

`Machine capacity data needs attention. Some reserved time may be incomplete; schedule changes remain disabled until verification passes.`

Raw technical codes remain in Admin diagnostics.

---

## 13. Work Order lookup must navigate to its schedule

Current WO lookup is all active WOs; board is date-windowed.

Add a lightweight planner service method, for example:

`GetWorkOrderScheduleWindowAsync(string workOrderNo, CancellationToken ct)`

Return only:

- WorkOrderId
- WorkOrderNo
- PlannedStartDateTime
- PlannedCompletionDateTime
- status

with existing tenant/access checks.

On Work Order selection:

1. resolve its window;
2. if it overlaps the current board period, reload normally;
3. if it does not overlap:
   - move `PeriodStart` to the period containing `PlannedStartDateTime`;
   - preserve Day/Week/Month mode;
   - reload once;
   - show status:
     `Moved to WO000123 scheduled period.`

Do not auto-jump on Clear, ordinary Refresh, or secondary-filter changes.

This removes the current “valid WO but empty board” confusion.

---

## 14. Timeline and labels

Keep existing `DxScheduler` architecture.

Keep:

- Timeline view;
- machine resource grouping;
- Day/Week/Month;
- zoom;
- horizontal scroll;
- maintenance/non-working shading;
- server drag intent;
- no create/delete;
- no resize;
- no cross-machine drag.

Appointment subject for current-branch jobs:

`WO000123 · PRODUCT-A`

Tooltip/description adds, when present:

- DR number;
- Current DR Required Date;
- Current Priority;
- Project;
- WO planned completion;
- `LATE BY n DAY(S)`.

Label precedence for current-branch jobs:

1. execution locked;
2. late;
3. normal scheduled.

Maintenance and external capacity use their own appointment types/labels.

Even when a locked job is also late, selected details must still show its late status.

---

## 15. Selected-operation details

Keep the existing detail panel.

Business information first:

- Work Order
- Product
- WO status
- execution status
- Demand source
- DR number
- DR status
- Current DR Required Date
- Current Priority
- Project
- WO planned completion
- Late / days late
- Work Centre
- Operation
- Machine
- operation planned start/completion
- planned quantity
- productive duration

For manual WOs:

`Demand source: Manual / no Delivery Request`

No fake Required Date. No DR-based late classification.

Actions remain:

- Open Work Order
- Change Schedule

Keep the human reason when Change Schedule is unavailable.

---

## 16. Reschedule dialog

Do not rewrite its scheduling logic.

Keep:

- requested date;
- requested time;
- reason;
- Preview;
- target before/after;
- WO before/after;
- affected operations;
- warnings;
- blockers;
- Commit.

Add business context:

- Current DR Required Date;
- Current Priority;
- current late status.

After Preview, derive an **advisory UI warning only**:

- if `WorkOrderCompletionAfter.Date > RequiredDate.Date`:
  `This change would leave the Work Order n calendar day(s) after the current DR required date.`
- if a move resolves current lateness, show that positively.

This advisory must **not** change `CanCommit` and must not become a second server blocker.

The scheduling service remains authoritative for schedule feasibility.

---

## 17. Planner diagnostics and permission hardening

### 17.1 UI

Move these out of the normal planner flow:

- Dry-run allocations
- Apply verified fragments
- Refresh readiness evidence
- raw readiness blocking codes

Show a collapsed/secondary `Planner diagnostics` area only to an authorized admin user.

### 17.2 Existing permission reuse

Do not add a new permission.

Use:

- Planner `Access`
- `AdminSettings + Edit`

for planner maintenance/diagnostic commands.

### 17.3 Service hardening

Change `MachineScheduleAllocationBackfillService.AuthorizeAsync`.

Current behavior:

- Planner Access
- Planner Edit

Corrected maintenance behavior:

- Planner Access
- Admin Settings Edit

Apply this to both:

- `PreviewBackfillAsync`
- `ApplyVerifiedBackfillAsync`

Why:

- backfill writes allocation fragments and audit rows;
- it is a readiness/maintenance command, not ordinary schedule editing;
- UI hiding alone is not security;
- readiness evidence already uses Admin Settings Edit.

Do not require the Machine Schedule write feature flag to be ON; admins must be able to prepare/repair readiness while writes remain OFF.

Add explicit authorization tests.

---

## 18. Performance and SQL Server safety

### Mandatory

- preserve `MaxBoardJobs = 3000`;
- preserve max visible window;
- no N+1 demand queries;
- no N+1 calendar queries;
- no per-external-allocation service query;
- default idle machines OFF;
- keep reload cancellation/version guard;
- keep debounce;
- SQL Priority/Late/DR filters before `Take`;
- `AsNoTracking` for read models;
- bounded server lookups.

### Large `IN` safety

Any new or touched query using a potentially large list of:

- WorkOrder IDs;
- machine codes;
- group codes;
- shift codes;

must be chunked or otherwise implemented so it does not depend on exceeding SQL Server parameter limits.

In particular review/harden:

- visible-job DR enrichment;
- external allocation query;
- `LoadMachineCalendarsAsync` machine-code filtering;
- preventive-maintenance machine-code filtering.

Do not fix this by loading whole tables into memory.

### Representative verification

Test/measure with:

- 100 operations;
- 3,000 visible operations;
- many WOs sharing the same DR;
- many operations from the same WO;
- >2,100 synthetic machine-code keys in SQL integration where practical, or an equivalent query-construction test proving chunking;
- idle toggle OFF and ON;
- external capacity present on a selected physical machine.

---

## 19. Database/entity impact

### Schema

**No schema change.**

Do not add:

- RequiredDate to WO;
- Priority to WO;
- duplicate DR fields;
- new capacity table;
- new UI preference table.

### Existing entities used

- `ProductionWorkOrder`
- `PrWorkOrderDemandAllocation`
- `SaDeliveryRequest`
- `ProductionMachineScheduleAllocation`
- `ProductionWorkOrderMachine`
- `PrMachine`

### Migration

None expected.

If implementation discovers a real schema contradiction, stop and report it rather than silently expanding scope.

---

## 20. Files expected to change

### Required

- `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor.cs`
- `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor.css`
- `ErpWeb.Core/Production/MachineSchedulePlannerService.cs`
- `ErpWeb.Core/Production/MachineScheduleAllocationBackfillService.cs`

### Tests

Add a focused planner service test file, e.g.:

- `ErpWeb.Tests/Planning/Transaction/MachineSchedulePlannerServiceTests.cs`

Extend where necessary:

- `ErpWeb.Tests/Planning/MachineSchedulePlannerInputTests.cs`
- `ErpWeb.Tests/Planning/Transaction/MachineScheduleRescheduleServiceTests.cs`

Add backfill authorization coverage in the most appropriate existing/new production-planning test fixture.

### Normally MUST NOT change

- `MachineScheduleRescheduleService` scheduling algorithm;
- `ProductionCalendarScheduler`;
- `WorkOrderScheduleCalculator`;
- costing services;
- inventory services;
- ProductionOutput posting/rollback;
- FG/IP/production-stock transaction logic.

A test-only change to prove unchanged rescheduling behavior is acceptable.

---

## 21. Quantity / costing / production safeguards

This enhancement has **zero intentional inventory/production quantity/costing effect**.

Must never mutate:

- WO PlannedQty;
- operation planned/output qty;
- Good/Scrap/Reject/Hold/Rework/Transferred qty;
- selected machine;
- BOM/materials;
- WIP;
- FG;
- stock balance;
- FIFO/WAC/Standard costing;
- CostLayer;
- COGS;
- Daily Production posting facts.

Existing schedule commit continues to change only its established schedule fields, revisions, allocation fragments and schedule audit.

No new Post/Rollback semantics.

---

## 22. Concurrency and atomicity

Do not change the existing schedule commit contract.

Preserve:

- preview token;
- WO RowVersion;
- machine RowVersion;
- WO ScheduleRevision;
- machine ScheduleRevision;
- sorted machine locks;
- scheduling-source lock;
- execution lock;
- commit-time revalidation;
- all-or-nothing SQL transaction;
- rollback on any failure.

Demand/priority/attention data is advisory read-model information and must never weaken these checks.

External capacity overlay is also read-only; authoritative commit still re-queries/revalidates capacity.

---

## 23. Testing plan

### 23.1 Planner read-model tests

Mandatory:

1. DR-linked WO maps DR number/status/required date/priority/project.
2. `WorkOrderPlannedCompletion` is mapped separately from operation completion.
3. Manual WO has no active DR context and is not late.
4. inactive allocation is not active demand.
5. CANCELLED/COMPLETED DR does not drive active late/priority attention.
6. same required/completion calendar date is not late.
7. next-day completion is `LateDays == 1`.
8. Priority filter uses repository priority values and filters before `Take`.
9. Late-only filter occurs before `Take`.
10. DR-number search returns the linked WO.
11. Late count is distinct by WorkOrderId when one WO has multiple operations.
12. Due count is distinct by WorkOrderId.
13. default idle OFF excludes irrelevant idle machines.
14. selected idle machine is returned as a resource even with zero jobs.
15. Show Idle ON uses branch/global machine-master scope and preserves cap/warning.
16. scheduled machine remains included even if idle-machine cap is reached.
17. external other-branch current allocation appears on the visible physical machine.
18. external allocation exposes no WO/product/customer/DR identity.
19. stale external allocation revision is not displayed as current authoritative capacity.
20. external capacity is retained even when current-branch business filters narrow jobs.
21. capacity-integrity warning state is exposed when readiness is incomplete.
22. demand enrichment is bounded/batched, not per job.

### 23.2 Work Order lookup navigation tests

23. selected WO already inside visible period does not jump.
24. selected WO outside visible period moves to the correct Day/Week/Month period once.
25. clear/refresh does not cause an unwanted auto-jump loop.

### 23.3 Zero-job board UI tests

26. selected idle machine + zero jobs still renders scheduler.
27. maintenance on selected idle machine remains visible.
28. external capacity on selected idle machine remains visible.
29. true no-machine/no-job state shows the normal empty state.

### 23.4 Diagnostics permission tests

30. planner Edit without Admin Settings Edit cannot preview/apply allocation backfill.
31. Admin Settings Edit + planner Access can run backfill diagnostics.
32. feature-write setting may remain OFF while authorized admin backfill/readiness maintenance runs.

### 23.5 Existing scheduler regression

Re-run existing focused cases for:

- split shifts/breaks;
- holidays;
- preventive maintenance;
- allocation readiness;
- same-machine preview;
- downstream propagation;
- stale preview;
- execution locking;
- atomicity;
- same-machine concurrency;
- Daily Production Create/Post/Rollback races;
- adjacent allocations;
- feature OFF;
- feature ON + ready.

No expected schedule result may change because of this UI/read-model enhancement.

### 23.6 Browser UAT

A first-time planner must be able to:

1. understand the page purpose;
2. see relevant machines by default;
3. identify late/due/priority jobs;
4. select an idle machine and see its calendar;
5. toggle all eligible idle machines;
6. find WO without memorizing code;
7. select a WO scheduled outside the current week and be taken to its scheduled period;
8. search DR;
9. see other-branch reserved physical capacity without confidential detail;
10. understand locked/started operations;
11. understand maintenance/non-working time;
12. select a job and understand current DR context;
13. open WO;
14. preview a schedule move;
15. understand if the proposed WO completion is after the current DR due date;
16. cancel with no ghost change;
17. commit when authorized/ready and see persisted dates after reload;
18. use View Only mode as a useful inquiry.

---

## 24. Responsive / accessibility verification

Verify:

- 1366×768;
- 1920×1080;
- Windows 125% scaling;
- narrow/mobile layout.

Must pass:

- compact aligned filters;
- no giant code inputs;
- More Filters popup within viewport;
- scheduler horizontal scrolling;
- readable Day/Week/Month headers;
- selected detail wrapping;
- tappable actions;
- one meaningful H1;
- status not communicated only by color;
- `role=status` / `role=alert` as appropriate;
- no browser console/DevExpress binding errors.

---

## 25. Implementation order — do not reorder casually

1. Capture current build/test baseline.
2. Add failing planner read-model tests for WO completion, DR context, distinct counts and idle behavior.
3. Add failing tests for cross-branch external capacity.
4. Add failing backfill authorization tests.
5. Extend planner DTO/query/board summary models.
6. Implement Priority/Late/DR search SQL filters before `Take`.
7. Implement batched DR enrichment.
8. Implement default scheduled-machine resource model + selected-idle machine support.
9. Fix Razor zero-job scheduler rendering.
10. Add external company-wide capacity overlay with anonymized bars.
11. Add human capacity-integrity warning.
12. Reuse repository priority constants; do not build a new priority lookup.
13. Add WO schedule-window resolver and auto-period navigation.
14. Refactor primary/More Filters UI.
15. Add attention strip with distinct-WO semantics.
16. Enrich selected-job details and tooltip.
17. Add advisory DR due-date consequence to Preview UI.
18. Move technical commands to Planner Diagnostics.
19. Harden allocation-backfill service authorization.
20. Chunk/harden large machine/ID query lists.
21. Run focused planner/scheduler SQL tests.
22. Run browser UAT at required resolutions/scaling.
23. Compare broader Planning/Production results against implementation-start baseline.
24. Produce changed-file list, test evidence and screenshots before declaring complete.

Do not begin by enabling writes or modifying the scheduling algorithm.

---

## 26. Acceptance criteria

Implementation is accepted only if all apply:

- [ ] Planner purpose is obvious.
- [ ] Late/Due counts are distinct WOs, not operation bars.
- [ ] Late calculation uses WO planned completion, not operation completion.
- [ ] Priority uses existing NORMAL/HIGH/URGENT constants.
- [ ] Current DR fields are clearly live demand context.
- [ ] Priority/Late/DR filters execute before 3,000-row limit.
- [ ] No N+1 demand queries.
- [ ] Default board excludes irrelevant idle machines.
- [ ] Selected idle machine renders a usable timeline even with zero jobs.
- [ ] Show Idle is explicit and bounded.
- [ ] Branch/global machine-master scope matches existing lookup semantics.
- [ ] Other-branch company-wide capacity is shown anonymously on visible physical machines.
- [ ] No other-branch business identity leaks.
- [ ] Capacity-integrity problems remain visibly understandable.
- [ ] Valid WO outside current period does not look “missing”.
- [ ] Primary controls are compact.
- [ ] Technical diagnostics are secondary/admin-only.
- [ ] Backfill service itself enforces admin maintenance permission.
- [ ] Same-machine-only rescheduling remains.
- [ ] Cross-machine drag remains disabled.
- [ ] Create/delete/resize remain disabled.
- [ ] Drag never directly writes dates.
- [ ] Existing Preview/Commit remains authoritative.
- [ ] Existing concurrency and readiness protections remain.
- [ ] No DB migration.
- [ ] No stock/WIP/FG/costing logic change.
- [ ] No SQL Server parameter-limit regression from large machine/WO lists.
- [ ] Day/Week/Month/zoom/scroll remain usable.
- [ ] 1366×768, 1920×1080, 125% scaling verified.
- [ ] focused planner/scheduler tests pass.
- [ ] no new broader Planning/Production regressions versus baseline.

---

## 27. Things the AI coding agent MUST NOT change

Reject implementation if it:

- enables `MACHINE_SCHEDULE_PLANNER_WRITE_ENABLED`;
- forces `IsWriteReady = true`;
- removes readiness checks;
- weakens backfill/readiness authorization;
- changes reschedule algorithm just for UI convenience;
- allows cross-machine drag;
- allows resize/create/delete;
- writes browser appointment dates directly;
- creates a second scheduling algorithm;
- creates another capacity table;
- duplicates DR required date/priority into WO DB columns;
- treats another branch’s capacity as free;
- reveals another branch’s WO/customer/product/DR data through capacity overlays;
- uses operation completion as WO lateness;
- counts one WO multiple times because it has multiple operations;
- filters Priority/Late only after the 3,000-row limit;
- loads all WOs/DRs/machines client-side;
- adds per-job queries;
- hides capacity-integrity warnings just to make the UI look clean;
- changes snapshot hash/revision semantics;
- changes machine selection;
- changes BOM/materials;
- changes production/stock/WIP/FG quantities;
- changes FIFO/WAC/Standard costing;
- changes Daily Production/FG/IP/production-stock posting or rollback;
- removes transaction/locking/concurrency protections;
- claims the overall Machine Schedule feature production-ready while the existing closure matrix still contains FAIL/UNKNOWN gates.

---

## 28. Rollback

No schema migration is expected.

Code rollback:

- revert planner UI/read-model changes;
- revert backfill permission hardening only if explicitly required after security review.

Operational safety switch remains:

`MACHINE_SCHEDULE_PLANNER_WRITE_ENABLED = OFF`

Turning it OFF must continue to return the page to safe inquiry/View Only mode without altering saved schedules.

---

## 29. Final approval

After this strict re-review and the mandatory corrections above, this plan is:

# **APPROVED FOR IMPLEMENTATION — 10/10**

The approval is justified because the plan now aligns all three views of the same reality:

1. **business demand** — DR required date / priority;
2. **visible planner board** — current branch jobs + real company-wide physical capacity;
3. **authoritative commit engine** — existing finite-capacity preview/commit with locks and allocation validation.

The target user experience is:

> Open Machine Schedule → see the real usable machine timeline → understand late/due/priority demand → see capacity already reserved elsewhere → find the WO even if it is scheduled outside the current week → select an operation → preview the authoritative same-machine change → understand downstream and due-date impact → confirm safely.

No costing, stock quantity, WIP, FG, posting, rollback, machine-selection or BOM logic is weakened by this enhancement.

**Production write enablement remains separately governed by the existing Machine Schedule Planner closure/readiness gates.**
