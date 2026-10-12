# Machine Schedule Planner implementation log

## Discovery guard

- Repository branch: `productionv2`.
- The working tree already contained unrelated, uncommitted production, UI, configuration, menu, and test changes. Those files were preserved; the planner changes were additive.
- DevExpress Blazor is pinned to `26.1.4` in both UI projects. Its installed package includes `DxScheduler` timeline view, resource grouping, server-side appointment drag events, and resource row cell decoration. The planner uses this licensed component.
- The current Work Order aggregate is `PrWorkOrder -> route steps -> operations -> selected machine options`. Work Order, route step, operation, and machine rows already use SQL Server rowversion concurrency tokens.
- The canonical machine calendar scheduler returns `ScheduleMachineResult.ConsumedIntervals`; planner allocation persistence is modeled around those productive intervals.
- Daily Production links directly through `ProductionOutput.WorkOrderOperationId`. Existing operation execution quantities and `NEW`, `POSTED`, `REVERSED` output statuses are available for operation-level locks.
- No active Work Order/Daily Production compatibility write to legacy `PrSch*` schedule tables was found. The only current production reference found is a comment warning against inferring execution from legacy `PrSchDailyProd.ScheCode`.
- Daily Production Create, Post, and Rollback now acquire a transaction-owned Work Order execution application lock before loading/locking the execution graph. Post and Rollback retain the established branch-stock-lock-first ordering. A future schedule commit must acquire its sorted machine locks before this Work Order execution lock, then lock/revalidate rows, so it cannot invert the production row-lock sequence.
- Baseline tests were not run in this coding turn.

## UI standard evidence

```text
Target module: Planning / Production Work Order machine scheduling
Target page: ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor
Page archetype: windowed resource timeline with compact filters and a selected-job detail panel
Closest live references: PrWorkOrderList and PrDailyProductionList; neither provides a timeline
Shared component(s): DevExpress DxScheduler, grouped machine resources, server-side service read model
Shared/global CSS: site.css tokens and existing iv-page / iv-hero / iv-card shell
Route/mode model: /planning/work-orders/machine-schedule; Week view by default
Permissions: dedicated PLN_MACHINE_SCHEDULE ACCESS; Work Order open action also checks PLN_WORK_ORDER ACCESS
Write gate: company setting defaults false; runtime readiness remains fail-closed
Intentional deviation: CommonDataGridEx is not used because the primary surface is a date/time timeline
```

## Production UI standard evidence

```text
Target workflow: inspect active Work Order machine schedules, calendar availability, maintenance, and execution locks
List reference: PrWorkOrderList
Entry reference: none; this is a planning board rather than a production transaction entry
Workflow phase(s): visible-window board -> select schedule -> inspect detail -> future preview/commit phase
Source selector: date window, work centre, machine, Work Order status, and Work Order/product search
Locked context after selection: selected Work Order, route step, operation, and selected machine identity
Draft identity: WorkOrderID + OperationID + selected WorkOrderMachineID
Statuses: Work Order RELEASED/IN_PROGRESS; execution NEW/POSTED/REVERSED and operation execution quantities
Save gate: planner writes require feature setting, runtime readiness, allocation completeness, and release evidence
Post/readiness gate: Daily Production posting remains owned by the existing production service
Rollback/delete/cancel/correction: not part of this board's workflow
Historical facts: operation plan dates, quantity, selected machine, Work Order status, execution blocker state
Live values: machine working intervals and planned preventive-maintenance intervals for the visible window
Permissions: PLN_MACHINE_SCHEDULE ACCESS; PLN_WORK_ORDER ACCESS for navigation
Route model: one read-only planner route
Intentional deviation: normal entry and transaction page actions are omitted because schedule changes require a separate authoritative preview/commit command
```

## Implementation state

- Added schedule-only revisions and productive allocation-fragment persistence mapping/schema script.
- Added report-only SQL preflight; it never rewrites dates or repairs existing schedules.
- Added the default-off write setting, dedicated menu entry, window-bounded board query, calendar/maintenance overlays, execution-state locks, and readiness report.
- Added the DevExpress machine timeline and detail panel. Drag, resize, appointment creation, and resource changes are disabled while readiness is false.
- Productive duration reconstruction now uses the same seconds-plus-run-minutes calculation as `WorkOrderScheduleCalculator`; month windows use the actual calendar month. When the allocation schema is absent, readiness reports the number of active scheduled machine operations as unverified.
- Readiness checks required allocation columns, indexes and interval constraint; active allocation identity/provenance, missing allocations, and half-open fragment overlaps are reported. The read model keeps filter choices available when the selected window has no jobs and warns when result limits are reached.
- Allocation backfill execution, operation-level preview/propagation, transactional commit, calendar impact handling, and the mandatory automated SQL Server evidence remain outstanding. Write mode must stay disabled until those gates pass.

## Build verification

- `dotnet build ErpWeb.UI/ErpWeb.UI.csproj --no-restore -m:1 -nr:false -v:minimal` succeeded after aligning the Razor component base class with `PageBase` and using the value-type scheduler interval collection correctly.
- Result: 0 errors, 9 warnings. Warnings include the ImageSharp license notice and existing nullable/member-hiding warnings in unrelated files.
- Tests were not run. This build result does not satisfy the outstanding transactional, backfill, calendar-impact, SQL Server concurrency, or UAT release gates above.
