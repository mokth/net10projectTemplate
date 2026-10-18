# productionv2 Machine Schedule Planner — FINAL Strict Review v3

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Final reviewed HEAD:** `80dd0b7f9279988650a98c64b6c657f339adb6ee`  
**Latest commit at review time:** `checked costing --continue`  
**Plan status:** **APPROVED FOR IMPLEMENTATION — 10/10**

> This approval is for the implementation plan only. It does **not** declare Machine Schedule write mode production-ready. The existing feature flag, readiness evidence, allocation integrity, concurrency, browser/UAT, SQL integration, and broader Planning/Production regression gates remain authoritative.

---

## 1. Final strict-review corrections

The previous v2 plan was strong but still contained several material implementation/security/business-semantics gaps. This v3 corrects all of them.

### 1.1 Allocation backfill permission was still too permissive

Backfill currently writes machine allocation fragments and audit rows.

The safe service rule is:

```text
Planner Access
AND Planner Edit
AND Admin Settings Edit
```

Do **not** replace Planner Edit with Admin Settings Edit.

Reason:

- planner Edit remains the production-domain mutation permission;
- Admin Settings Edit proves maintenance/admin authority;
- both are appropriate for a controlled readiness repair/write;
- the Machine Schedule write feature flag itself may remain OFF while this maintenance command runs.

`Refresh readiness evidence` may retain its current repository contract:

```text
Planner Access
AND Admin Settings Edit
```

because it records readiness certification, not a schedule/allocation mutation.

### 1.2 WO auto-navigation must target machine time, not only WO header time

A Work Order can begin with non-machine/labour activity. Jumping to the WO header start may still land in a period with no machine bar.

The planner resolver must return:

- earliest selected scheduled machine start;
- latest selected scheduled machine completion;

and use WO header start/completion only as fallback.

### 1.3 Planner lookups must only offer values that can actually appear on this board

Current Work Centre / Process / Work Order / Product lookups are based on active WOs generally, not necessarily scheduled machine operations.

Correct them so planner-specific lookups are based on active WOs/operations that have at least one:

```text
selected machine
AND machine PlannedStartDateTime != null
AND machine PlannedCompletionDateTime != null
```

Exception:

- Machine lookup remains master-driven so an idle machine can intentionally be inspected.

This avoids valid-looking filters that always produce an empty machine board.

### 1.4 “Late” must not be misrepresented as customer-fulfilment risk

The repository has two separate concepts:

- DR header lifecycle status (`RELEASED`, `IN_PRODUCTION`, etc.);
- authoritative DR fulfilment facts (`OPEN`, `READY`, `PARTIAL_DELIVERED`, `COMPLETED`, etc.).

A DR header can still be `IN_PRODUCTION` while fulfilment is already complete.

Therefore this planner must use exact wording:

- **Late vs DR date**
- **WO after DR required date**
- **Schedule completes n calendar days after current DR required date**

It must **not** claim:

- customer demand is unfulfilled;
- delivery is at risk;
- customer will be late;

unless authoritative fulfilment facts are explicitly loaded and evaluated.

This plan intentionally does **not** add full Sales fulfilment calculation to every planner board reload because that would broaden scope and add heavy data queries.

### 1.5 Demand-enriched DTO mutability must match the loading strategy

The current service materializes `MachineSchedulePlannerJob` first and then enriches execution state in memory.

Therefore:

- fields projected by the main SQL query can be `init`;
- DR/demand fields populated after materialization must be mutable `set`;
- derived fields should be get-only computed properties where practical.

Do not design an `init`-only DR DTO and then attempt post-query mutation.

### 1.6 External capacity overlay must be schema-safe

Older/unprepared databases may not have the allocation schema installed.

Before querying `PrMachineScheduleAllocation`:

1. obtain planner readiness / schema readiness;
2. query external allocation rows **only when `AllocationSchemaReady == true`**.

Do not touch the allocation DbSet first and then discover the table is missing.

### 1.7 External-capacity scheduler interaction must be explicit

Existing appointment location prefixes are:

- `operation:`
- `maintenance:`

Add:

- `external-capacity:`

External capacity bars must:

- have a stable unique appointment ID;
- use their own scheduler label ID;
- be non-draggable;
- never open current-branch WO details;
- show a human explanation when selected/dragged;
- never fall through to the generic “maintenance” message.

### 1.8 Scheduler label IDs must be frozen explicitly

Current labels:

```text
1 Scheduled
2 Execution locked
3 Preventive maintenance
```

Reserve:

```text
4 Late vs DR date
5 Reserved · other branch
```

Do not reuse label IDs dynamically.

### 1.9 Work Order selection must not trigger a double reload

Replace:

```text
@bind-Value:after="ReloadAsync"
```

for Work Order selection with a dedicated handler.

The handler must:

1. set the selected WO;
2. resolve earliest machine schedule;
3. adjust period if required;
4. issue exactly one board reload.

### 1.10 Period navigation with a selected WO needs a useful empty state

Auto-jump should occur only on initial WO selection.

If the user later presses Today / Previous / Next and moves away from the WO’s scheduled period, do not force them back.

Instead show:

```text
Selected WO is not scheduled in this period.
[ Go to WO schedule ]
```

### 1.11 Idle-machine master query should be skipped when not needed

When:

```text
ShowIdleMachines == false
AND no explicit Machine filter
```

do **not** query/load 500 idle machine masters merely to discard them.

Resources should come from visible scheduled jobs only.

When a specific machine is selected, load only that eligible machine master if needed.

### 1.12 External capacity is advisory UI; commit remains authoritative

The overlay improves operator understanding, but the existing reschedule service must still re-query/reconstruct capacity and make the actual commit decision.

Never use the browser overlay as the capacity authority.

### 1.13 Current DR context must not enter schedule concurrency/token logic

DR Required Date / Priority can change independently of machine scheduling.

Do not add them to:

- Work Order schedule revision;
- machine schedule revision;
- preview token;
- schedule lock contract.

They are advisory business context only.

After successful commit the board reloads current demand context.

---

# 2. Objective

Turn **Planning → Production → Machine Schedule** into a practical finite-capacity machine planning workspace that lets a planner answer:

1. What current-branch jobs are scheduled on each machine?
2. Which WO schedules finish after their linked DR required date?
3. Which jobs carry NORMAL / HIGH / URGENT priority?
4. Which operations are already started or otherwise locked?
5. Where are non-working time and preventive maintenance?
6. Is apparently free machine time already reserved by another branch?
7. Can this operation be safely moved on the same machine?
8. What downstream timing changes will occur before confirmation?

Core lifecycle remains:

```text
Released/In-Progress WO
    → selected machine operation
    → machine timeline
    → server-authoritative Preview
    → user reviews downstream/capacity impact
    → Commit
    → atomic persistence + schedule audit
```

This task is:

- UI enhancement;
- planner read-model enhancement;
- planner lookup consistency enhancement;
- controlled diagnostics permission hardening.

It is **not** a new scheduling engine.

---

# 3. Non-negotiable repository truths

## 3.1 Existing schedule mutation path remains authoritative

`MachineScheduleRescheduleService` owns all schedule writes.

Preserve all existing safeguards:

- Released / In-Progress only;
- same selected machine only;
- no cross-machine move;
- backward schedules remain blocked;
- execution quantities block movement;
- NEW Daily Production blocks affected operations;
- POSTED Daily Production blocks affected operations;
- preview is read-only;
- commit requires a fresh preview token;
- WO RowVersion;
- machine RowVersion;
- WO ScheduleRevision;
- machine ScheduleRevision;
- company-wide machine capacity;
- sorted machine locks;
- scheduling-source lock;
- WO execution lock;
- commit-time revalidation;
- atomic SQL transaction;
- allocation-fragment replacement;
- schedule audit;
- frozen production snapshot semantics.

No Razor or page code may reproduce or bypass this logic.

## 3.2 Physical machine capacity is company-wide

Authoritative physical machine identity is:

```text
CompanyCode + MachineCode
```

The board’s current branch filter does not change that capacity rule.

## 3.3 WO → DR link already exists

Use:

- `PrWorkOrderDemandAllocation`
- `SaDeliveryRequest`

No duplicate DR columns on Work Order.

The unique `WorkOrderId` allocation index means one WO links to one DR bridge row.

## 3.4 Priority values already exist

Use:

- `SaDeliveryRequestPriorities.Normal`
- `SaDeliveryRequestPriorities.High`
- `SaDeliveryRequestPriorities.Urgent`

No new priority master or dynamic lookup.

---

# 4. Scope

## In scope

- planner-purpose clarity;
- compact UI;
- current DR number/date/priority/project context;
- late-vs-DR-date indicator;
- distinct-WO attention counts;
- machine-scheduled-only planner lookups;
- idle-machine inspection;
- zero-job scheduler rendering;
- anonymized other-branch capacity bars;
- View Only usability;
- human-readable capacity-integrity warning;
- Work Order schedule navigation;
- Planner Diagnostics;
- backfill permission hardening;
- SQL parameter-safe batching;
- focused tests and browser UAT.

## Out of scope

- machine reassignment;
- cross-machine drag;
- appointment resize;
- BOM/Product Definition edits;
- planned quantity edits;
- selected-machine edits;
- auto-optimization;
- AI scheduling;
- stock/WIP/FG quantity changes;
- costing changes;
- Daily Production posting/rollback changes;
- Production Stock Return/Adjustment changes;
- new database preference table;
- new capacity schema;
- full Sales fulfilment dashboard logic inside Machine Schedule.

---

# 5. UI standard evidence

```text
UI STANDARD EVIDENCE
Target module: Planning / Production
Target page: PrMachineSchedulePlanner
Archetype: finite-capacity machine timeline / planning workspace
References: PrWorkOrderEntry, PrWorkOrderList, PrDailyProductionList
Controls: DxScheduler, DxButton, DxComboBox, DxPopup, DxCheckBox, existing iv-* shell
Route: /planning/work-orders/machine-schedule
Workflow: find → inspect → preview → confirm
Planner mutation permission: Access + Edit + runtime readiness
Diagnostics permissions: see §18
Intentional deviation: timeline instead of CommonDataGridEx because capacity/time is the primary domain
```

---

# 6. Target workflow

## Normal planner flow

1. Open Machine Schedule.
2. Default Week view opens.
3. Show machines with visible current-branch scheduled machine jobs.
4. On those physical machines, show any current verified other-branch capacity anonymously.
5. Planner sees:
   - Late vs DR Date;
   - Current Priority;
   - execution locks;
   - maintenance;
   - non-working time.
6. Search/select Work Centre, Machine or Work Order.
7. Selecting a WO outside the current period jumps once to its earliest scheduled machine period.
8. Select a current-branch job bar.
9. Review WO + DR context.
10. Drag on the same machine or choose `Change Schedule`.
11. Existing server Preview calculates authoritative placement/downstream impact.
12. UI shows an advisory comparison to the current DR required date.
13. User commits only if existing service says `CanCommit`.
14. Board reloads.

## View Only flow

If rescheduling is unavailable:

- inquiry remains fully useful;
- timeline remains visible;
- demand context remains visible;
- external capacity remains visible;
- maintenance/non-working time remains visible;
- main text is human-readable.

Example:

```text
Schedule changes are currently disabled.
You can still review machine load, reserved time and Work Order timing.
```

Raw readiness codes belong in diagnostics.

---

# 7. Main UI layout

## 7.1 Hero

Keep `iv-hero`.

Title:

`Machine Schedule`

Subtitle:

`Review machine load, Work Order timing and maintenance. Move eligible jobs only after schedule preview.`

Show:

- mode chip;
- period chip;
- KPI: `Late vs DR date`.

Do not call this KPI `Customer Late` or `Delivery At Risk`.

## 7.2 Navigation / views

Keep:

- Previous;
- Today;
- Next;
- period label;
- Day;
- Week;
- Month;
- Zoom −;
- Zoom +;
- Reset.

View switching must preserve the selected period anchor.

## 7.3 Primary filters

Always visible:

- Search: `WO, DR, product or process`
- Work Centre
- Machine
- Work Order
- Show idle machines
- MORE FILTERS
- Refresh

## 7.4 More Filters popup

Use `DxPopup` + `common-popup`.

Draft fields:

- Process
- Product
- Status
- Priority
- Late vs DR date only

Actions:

- Clear
- Cancel
- Apply

Apply once; do not reload on every draft edit.

---

# 8. Planner lookup semantics

Planner lookup choices must correspond to records that can appear on this machine board.

## 8.1 Work Centre

Return only active Work Centre values having at least one active WO operation with:

- selected machine;
- planned machine start;
- planned machine completion.

## 8.2 Process

Same rule: only machine-scheduled operations.

## 8.3 Work Order

Only Released/In-Progress WOs with at least one selected scheduled machine row.

Include in description:

- product;
- status;
- earliest machine schedule date where practical.

## 8.4 Product

Only products from active WOs with selected scheduled machine rows.

## 8.5 Machine

Remain master-driven:

```text
CompCode == current company
AND Active == true
AND (BranchCode == null OR BranchCode == current branch)
```

Reason:

the planner must be able to inspect genuinely idle capacity.

---

# 9. Planner DTO/read-model design

## 9.1 `MachineSchedulePlannerJob`

Keep existing schedule fields.

Add main-query projected fields:

```text
DateTime WorkOrderPlannedStart
DateTime WorkOrderPlannedCompletion
```

These may be `init`.

Add demand-enriched fields populated after materialization:

```text
long? DeliveryRequestId
string? DeliveryRequestNo
string? DeliveryRequestStatus
DateTime? RequiredDate
string? Priority
string? ProjectCode
string? WarehouseCode
bool DemandAllocationIsActive
```

These must be mutable `set` if using post-query enrichment.

Prefer computed get-only properties:

```text
bool HasActiveDemandContext
bool IsLateToRequiredDate
int LateDays
```

## 9.2 Active demand context

For planner attention/filter semantics:

```text
DemandAllocationIsActive == true
AND DR status is RELEASED or IN_PRODUCTION
```

A historical/inactive link can still be displayed as context but must not drive Late-only/Priority attention.

---

# 10. Exact “Late vs DR date” rule

The board measures **schedule alignment**, not fulfilment risk.

A WO is Late vs DR Date when:

```text
HasActiveDemandContext
AND RequiredDate exists
AND WorkOrderPlannedCompletion.Date > RequiredDate.Date
```

`LateDays`:

```text
(WorkOrderPlannedCompletion.Date - RequiredDate.Date).Days
```

Rules:

- use WO completion, not operation completion;
- date-only comparison;
- do not invent working-day lateness;
- do not call it customer delivery risk;
- do not infer fulfilment status from DR header status.

UI wording:

```text
Late vs DR date: 2 calendar days
```

or:

```text
WO completion is 2 calendar days after the current DR required date.
```

---

# 11. Query/filter correctness

Extend `MachineSchedulePlannerQuery`:

```text
string? Priority
bool LateOnly
bool ShowIdleMachines
```

## 11.1 SQL filters before 3,000-row limit

Priority, LateOnly and DR-number search must translate into SQL before:

`Take(MaxBoardJobs + 1)`

Never:

1. load first 3,001 operations;
2. enrich;
3. filter in memory.

## 11.2 Priority filter

Use active linked DR + existing repository constants.

## 11.3 DR search

Search may match linked DR number.

Keep search server-side.

## 11.4 Demand enrichment

After bounded jobs are loaded:

1. collect distinct WorkOrderId;
2. query WO allocation + DR fields in batches;
3. include tenant/company/branch predicates;
4. map by WorkOrderId;
5. mutate the demand-enrichment fields.

No per-job DB query.

---

# 12. Attention summary

Add service-produced board summary fields:

- `LateWorkOrderCount`
- `DueThisWindowWorkOrderCount`
- `LockedJobCount`
- `VisibleMaintenanceMachineCount`
- `ExternalCapacityMachineCount`
- `AttentionCountsArePartial`

## Late Work Orders

Count:

```text
DISTINCT WorkOrderId
WHERE IsLateToRequiredDate
```

Do not count operation bars.

## DR due this period

Count distinct active-demand WO IDs where:

```text
RequiredDate.Date >= WindowStart.Date
AND RequiredDate.Date < WindowEnd.Date
```

Label exactly:

`DR due this period`

Do not label it `Customer demand at risk`.

## Partial counts

If board jobs exceed the 3,000 guard:

```text
AttentionCountsArePartial = true
```

UI:

`Counts are based on loaded operations.`

---

# 13. Idle-machine behavior

## Default

`ShowIdleMachines = false`

When there is no explicit Machine filter:

- skip broad idle-machine master query;
- resources come only from visible scheduled-job machine codes.

When a Machine is explicitly selected:

- validate/load only that master using current company/branch eligibility;
- include it even if zero current-branch jobs exist.

Scheduled historical job machines must still be shown even if their current master eligibility changed.

## Show Idle Machines = true

Load eligible active machine masters, bounded to 500.

Always append scheduled-job machines even if not in the first 500 idle masters.

`HasMoreIdleMachinesThanLimit` only applies when Show Idle is enabled.

---

# 14. Zero-job scheduler rendering

Current Razor behavior:

```text
board.Jobs.Count == 0 → hide scheduler
```

must be replaced.

Correct behavior:

```text
board.Machines.Count == 0
    → true empty state

board.Machines.Count > 0
    → render scheduler even when Jobs.Count == 0
```

For selected idle machine:

- render calendar;
- render maintenance;
- render external reserved capacity;
- show:

`No current-branch jobs are scheduled on this machine in the selected period.`

---

# 15. Company-wide external capacity overlay

## 15.1 Purpose

The board must not visually claim a shared physical machine is free when another branch has authoritative current capacity reserved.

## 15.2 Schema-safe ordering

Before querying allocation rows:

1. load planner readiness;
2. check `AllocationSchemaReady`;
3. only then query `PrMachineScheduleAllocation`.

If schema is not ready:

- do not query that table;
- show capacity-integrity warning;
- schedule writes remain disabled by existing readiness.

## 15.3 External allocation query

For visible machine codes and window:

- same CompanyCode;
- BranchCode != current branch;
- interval overlaps visible window;
- active WO status Released/InProgress;
- WorkOrderMachine is selected;
- allocation ScheduleRevision equals current machine ScheduleRevision;
- allocation WorkOrderId/OperationId/MachineId identity matches current rows.

Exclude stale revisions.

## 15.4 Privacy

Never expose external branch:

- WO number;
- product;
- customer;
- DR;
- quantity.

Appointment:

```text
Subject: Reserved · other branch
Description: This physical machine capacity is already reserved outside the current branch.
Location: external-capacity:{allocationUid}
```

## 15.5 Scheduler behavior

External appointment:

- label ID `5`;
- locked;
- cannot drag;
- cannot open WO;
- cannot Change Schedule;
- selecting/hovering shows only the anonymous explanation.

Drag handler must distinguish it from maintenance:

```text
This reserved capacity belongs outside the current branch and cannot be moved here.
```

## 15.6 External capacity remains visible under business filters

If a machine row remains visible, its authoritative external capacity remains visible even when:

- Priority filter;
- Product filter;
- DR search;

narrows current-branch jobs.

Capacity is not a product/demand filter result.

---

# 16. Scheduler labels

Freeze:

```text
1 Scheduled
2 Execution locked
3 Preventive maintenance
4 Late vs DR date
5 Reserved · other branch
```

Label precedence for current-branch jobs:

1. execution locked;
2. late vs DR date;
3. normal scheduled.

A locked job that is also late still shows the late fact in selected details.

Do not communicate meaning by color alone.

---

# 17. Work Order schedule navigation

Add a small planner read method, e.g.:

`GetWorkOrderMachineScheduleWindowAsync`

Return:

- WorkOrderId;
- WorkOrderNo;
- earliest selected machine start;
- latest selected machine completion;
- WO header start/completion as fallback;
- status.

Tenant and planner Access checks required.

## Selection handler

Do not use the existing direct `ReloadAsync` bind-after.

Dedicated handler:

1. update SelectedWorkOrder;
2. if cleared → reload once;
3. resolve machine schedule;
4. if earliest machine start is outside current period:
   - normalize to `DateTimeKind.Unspecified`;
   - compute Day/Week/Month PeriodStart;
5. reload once;
6. show:
   `Moved to WO000123 scheduled period.`

## Later manual period navigation

Do not auto-force the user back.

If selected WO has no bar in the new period:

```text
Selected WO is not scheduled in this period.
[ Go to WO schedule ]
```

---

# 18. Planner diagnostics and permissions

## 18.1 Normal planner UI

Do not prominently show:

- Dry-run allocations;
- Apply verified fragments;
- Refresh readiness evidence;
- raw readiness codes.

Use a secondary/collapsed `Planner diagnostics` area.

## 18.2 Readiness evidence

Retain current service authorization:

```text
Planner Access
AND Admin Settings Edit
```

## 18.3 Allocation backfill

Harden service authorization to:

```text
Planner Access
AND Planner Edit
AND Admin Settings Edit
```

Apply to both:

- PreviewBackfillAsync
- ApplyVerifiedBackfillAsync

The feature-write flag does **not** need to be ON.

## 18.4 UI flags

Use separate UI capabilities:

```text
CanRefreshReadinessEvidence
CanManageAllocationBackfill
```

Do not use one boolean for both commands.

---

# 19. Capacity-integrity warning

If readiness indicates:

- allocation schema missing;
- missing allocations;
- stale allocation provenance;
- unexplained overlaps;

show a human warning above the board:

```text
Machine capacity data needs attention.
Some reserved time may be incomplete; schedule changes remain disabled until verification passes.
```

Raw technical codes remain inside diagnostics.

Also preserve the current per-job calendar verification warning, but word it accurately:

`n scheduled operation(s) could not be verified against the current machine calendar.`

---

# 20. Selected job details

Business facts first:

- Work Order;
- Product;
- WO status;
- execution status;
- demand source;
- DR No;
- DR header status;
- Current DR Required Date;
- Current Priority;
- Project;
- WO planned completion;
- Late vs DR Date / LateDays;
- Work Centre;
- Operation;
- Machine;
- operation planned start/completion;
- planned quantity;
- productive duration.

Manual WO:

`Demand source: Manual / no Delivery Request`

No fake due date.

Actions:

- Open Work Order
- Change Schedule

Keep the existing lock/unavailable reason.

---

# 21. Reschedule dialog

Do not alter the scheduling algorithm.

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

Add advisory business comparison:

If Preview says:

```text
WorkOrderCompletionAfter.Date > RequiredDate.Date
```

show:

`This proposed schedule completes n calendar day(s) after the current DR required date.`

If it resolves lateness:

`This proposed schedule brings the WO on/before the current DR required date.`

Important:

- advisory only;
- does not change `CanCommit`;
- not added to preview token;
- not added to rowversion logic;
- DR changes do not invalidate schedule feasibility.

---

# 22. SQL Server / performance rules

Mandatory:

- preserve `MaxBoardJobs = 3000`;
- preserve max visible window;
- no N+1 DR queries;
- no N+1 calendar queries;
- no per-allocation extra query;
- default idle OFF;
- keep cancellation/version reload guard;
- keep search debounce;
- secondary filter popup applies once;
- server filtering before board limit;
- `AsNoTracking` on read models.

## Large `IN` lists

Any touched/new potentially large list query must be chunk-safe.

Review/harden:

- WorkOrder IDs for demand enrichment;
- visible machine codes;
- machine calendar loads;
- preventive maintenance loads;
- external allocation loads;
- shift group/shift-code loads where a large visible machine set can expand them.

Do not solve parameter limits by loading whole tables client-side.

---

# 23. Database/schema impact

**No schema migration.**

Use existing:

- ProductionWorkOrder
- ProductionWorkOrderOperation
- ProductionWorkOrderMachine
- PrWorkOrderDemandAllocation
- SaDeliveryRequest
- ProductionMachineScheduleAllocation
- PrMachine

Do not add:

- RequiredDate to WO;
- Priority to WO;
- another capacity table;
- another planner settings table.

If implementation discovers a genuine schema contradiction, stop and report it.

---

# 24. Quantity / costing safeguards

This enhancement must have zero intentional impact on:

- Work Order PlannedQty;
- operation output qty;
- Good/Scrap/Reject/Hold/Rework/Transferred qty;
- BOM;
- material requirement;
- selected machine;
- WIP;
- FG;
- inventory balance;
- CostLayer;
- FIFO;
- WAC;
- Standard costing;
- COGS;
- Daily Production posting;
- FG/IP/Production Stock Return/Adjustment posting or rollback.

Existing schedule commit remains the only schedule mutation path.

---

# 25. Files expected to change

## Required

- `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor.cs`
- `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor.css`
- `ErpWeb.Core/Production/MachineSchedulePlannerService.cs`
- `ErpWeb.Core/Production/MachineScheduleAllocationBackfillService.cs`

## Tests

Add/extend:

- `ErpWeb.Tests/Planning/Transaction/MachineSchedulePlannerServiceTests.cs`
- `ErpWeb.Tests/Planning/MachineSchedulePlannerInputTests.cs`
- `ErpWeb.Tests/Planning/Transaction/MachineScheduleRescheduleServiceTests.cs`
- appropriate allocation-backfill authorization/integration test fixture.

## Normally MUST NOT change

- `MachineScheduleRescheduleService` algorithm;
- `ProductionCalendarScheduler`;
- `WorkOrderScheduleCalculator`;
- costing services;
- inventory services;
- ProductionOutput post/rollback services.

---

# 26. Mandatory tests

## Read model

1. WO-level planned completion is distinct from operation completion.
2. linked DR fields map correctly.
3. demand-enrichment properties can be populated after materialization.
4. manual WO has no DR late flag.
5. inactive allocation does not drive Late/Priority attention.
6. CANCELLED/non-active DR does not drive attention.
7. same completion/required date is not late.
8. next-day completion is LateDays = 1.
9. Priority filter runs before Take.
10. Late filter runs before Take.
11. DR-number search runs before Take.
12. Late count is distinct WorkOrderId.
13. Due count is distinct WorkOrderId.

## Lookup consistency

14. Work Centre lookup excludes non-machine-only values.
15. Process lookup excludes non-machine-only operations.
16. Work Order lookup excludes active WOs with no scheduled selected machine.
17. Product lookup only returns products that can appear on machine board.
18. Machine lookup still allows eligible idle machine.

## Idle behavior

19. idle OFF skips broad idle master load.
20. selected idle machine appears with zero jobs.
21. zero jobs + one machine still renders scheduler.
22. maintenance remains visible on idle machine.
23. Show Idle ON preserves 500 cap.
24. scheduled machine remains included beyond idle cap.

## External capacity

25. allocation schema absent → no allocation-table query / no crash.
26. current external allocation appears on shared visible machine.
27. stale revision is not shown as authoritative.
28. external bar leaks no WO/product/DR/customer identity.
29. external capacity remains under Product/Priority filters.
30. selected external bar cannot open WO.
31. drag external bar is cancelled with correct message.
32. label ID 5 maps correctly.

## Work Order navigation

33. selection inside current period reloads once.
34. selection outside period jumps to earliest selected machine start.
35. labour-before-machine WO does not jump to an empty header-start week.
36. clear selection reloads once.
37. manual Today/Previous/Next does not auto-force back.
38. `Go to WO schedule` returns to correct period.

## Diagnostics authorization

39. Planner Edit without Admin Settings Edit cannot preview/apply backfill.
40. Admin Settings Edit without Planner Edit cannot preview/apply backfill.
41. Planner Access + Planner Edit + Admin Settings Edit can backfill.
42. feature-write flag may remain OFF during authorized maintenance.
43. readiness evidence retains its current Planner Access + Admin Settings Edit contract.

## Scheduler regression

Re-run existing focused tests for:

- shifts/breaks;
- holidays;
- maintenance;
- allocation readiness;
- release allocation reservation;
- preview propagation;
- stale preview;
- execution locking;
- atomicity;
- same-machine race;
- DP Create/Post/Rollback race;
- adjacent allocations;
- feature OFF;
- feature ON + ready.

No expected scheduling result may change.

---

# 27. Browser UAT

Verify:

- 1366×768;
- 1920×1080;
- Windows 125% scaling;
- narrow/mobile layout.

First-time planner must be able to:

1. understand page purpose;
2. see relevant scheduled machines;
3. select an idle machine and still see timeline/calendar;
4. identify Late vs DR Date;
5. see NORMAL/HIGH/URGENT;
6. search by DR;
7. select WO outside current week and land on actual machine schedule;
8. see other-branch reserved capacity anonymously;
9. understand maintenance/non-working periods;
10. understand locked operations;
11. select job and understand DR context;
12. open WO;
13. preview a same-machine change;
14. understand downstream effects;
15. understand proposed relation to DR required date;
16. cancel without ghost movement;
17. commit when authorized/ready;
18. see persisted result after reload;
19. use View Only as a useful inquiry.

Browser console must have no new DevExpress binding/runtime errors.

---

# 28. Implementation order

1. Capture current build/test baseline.
2. Add failing DTO/late/distinct-count tests.
3. Add failing lookup-consistency tests.
4. Add failing idle-machine/zero-job tests.
5. Add failing external-capacity/schema-safety tests.
6. Add failing diagnostics authorization tests.
7. Extend planner query/job/board DTOs.
8. Project WO header schedule fields.
9. Implement demand filters before Take.
10. Implement batched mutable demand enrichment.
11. Correct planner lookup semantics.
12. Implement idle OFF fast path.
13. Implement selected-idle resource path.
14. Fix Razor zero-job scheduler rendering.
15. Load readiness before any external allocation query.
16. Implement anonymized external allocation overlay.
17. Add fixed label IDs 4/5.
18. Add external appointment selection/drag behavior.
19. Add capacity-integrity warning.
20. Implement WO machine-schedule resolver.
21. Replace Work Order bind-after with one-reload handler.
22. Add `Go to WO schedule` empty-state action.
23. Refactor primary/More Filters layout.
24. Add exact attention strip.
25. Enrich selected-job details.
26. Add advisory DR-date comparison to Preview.
27. Move diagnostics out of normal flow.
28. Harden backfill service to Access + Edit + Admin Edit.
29. Harden large-list SQL batching.
30. Run focused planner/scheduler SQL tests.
31. Run browser UAT.
32. Compare broader Planning/Production failures to implementation-start baseline.
33. Produce implementation evidence before declaring complete.

Do not enable writes as part of this task.

---

# 29. Reject implementation if it does any of these

- enables `MACHINE_SCHEDULE_PLANNER_WRITE_ENABLED`;
- forces `IsWriteReady = true`;
- weakens readiness;
- replaces authoritative reschedule service with UI writes;
- allows cross-machine drag;
- allows resize/create/delete;
- directly persists scheduler appointment dates;
- creates another scheduling algorithm;
- treats branch-local board as full company capacity;
- hides external shared-machine reservations;
- leaks another branch’s WO/customer/product/DR data;
- queries allocation table before schema readiness;
- uses operation completion as WO late date;
- labels schedule lateness as customer fulfilment risk;
- counts one WO multiple times because of multiple operations;
- filters after the 3,000-row limit;
- leaves dead/non-machine values in planner lookup lists;
- triggers two board reloads for one WO selection;
- uses `init`-only DR fields then mutates them;
- replaces Planner Edit with Admin Edit for backfill;
- loads all WOs/DRs/machines client-side;
- adds N+1 database queries;
- introduces SQL parameter-limit risk;
- changes machine selection;
- changes quantities/BOM/WIP/FG/stock/costing;
- changes Production transaction posting/rollback;
- claims overall production readiness while existing closure gates still fail/are unknown.

---

# 30. Final approval

After this final strict review, the plan is:

# **APPROVED FOR IMPLEMENTATION — 10/10**

The plan now aligns:

1. **current branch production jobs**;
2. **live DR planning context**;
3. **company-wide physical-machine capacity**;
4. **existing service authorization**;
5. **existing preview/commit concurrency model**;
6. **large-data SQL safety**;
7. **the actual DevExpress scheduler interaction model**.

The finished experience should be:

> Open Machine Schedule → see real usable machine capacity → identify WO schedules after their current DR required date → understand priority and execution state → see shared-machine reservations from other branches without exposing their business data → find the WO’s actual machine period → preview the authoritative same-machine change → understand downstream and DR-date impact → commit safely.

No costing, stock quantity, WIP, FG, posting, rollback, BOM, or machine-selection logic is weakened.

**Production write enablement remains separately governed by the existing Machine Schedule Planner closure/readiness gates.**
