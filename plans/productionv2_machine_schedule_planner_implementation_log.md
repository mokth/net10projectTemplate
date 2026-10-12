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
- Initial Release now reconstructs active reservations through the canonical scheduler, rejects stale or overlapping company-wide machine capacity, and writes the new Work Order's productive fragments in the Release transaction under sorted machine locks and the execution lock. It does not silently move an existing Work Order.
- Calendar impact guards now serialize and protect company/machine calendar writes, holiday edits/imports, shift/shift-group edits, and preventive maintenance add/edit/delete/cancel/complete operations. They conservatively block changes overlapping active persisted Work Order schedule windows.
- Release now takes sorted physical-machine locks before its shared scheduling-source lock and Work Order execution lock; this keeps its lock order aligned with the planned planner commit path. The SQL preflight's missing-allocation check no longer treats branch as part of physical machine identity.
- Timeline zoom now has discrete useful widths, Month headers expose date and weekday, and the page reports the read-only state without technical readiness codes. The write gate remains fail-closed.
- Added an authoritative same-machine, forward-only Preview/Commit service. Preview reconstructs the affected route, composes company-wide occupied productive intervals with the canonical scheduler, reports downstream/Work Order impact, and returns a stale-input token without saving schedule changes. Commit reacquires sorted machine locks, the shared scheduling-source lock, and the Work Order execution lock; it recomputes and compares the token before updating schedule fields, machine provenance, allocation fragments, schedule revisions, and audit history in one transaction. Released snapshot revision/hash and production definition fields remain unchanged.
- Added the Change Schedule dialog with requested date/time, required reason, full server preview impact, warnings/blockers, cancel, and guarded commit. The button requires both edit permission and the runtime readiness gate.
- Added a controlled allocation backfill service and planner maintenance dialog. Dry-run reconstructs current persisted schedules across the company for physical-machine overlap checks while targeting writes to the current branch. Apply requires a current preview token, repeats validation under sorted machine locks and transaction-owned scheduling/execution locks, writes only verified missing fragments, adds audit events, and confirms the result before commit. It never changes dates or schedule revisions.
- Reschedule previews, commit validation, readiness checks, backfill verification, and SQL preflight now validate allocation provenance against the owning Work Order's company, branch, operation, machine, and schedule revision. Company-wide machine capacity remains keyed by company and physical machine; reports redact Work Order numbers from other branches.
- Wired DevExpress pre-update cancellation and appointment-form suppression to route drag intent through the same Preview dialog. A separate browser-acceptance gate remains false, so drag stays disabled until the installed 26.1.4 behavior is demonstrated. The page also rejects maintenance, execution-locked, and backward-scheduled appointment drags with a user-facing reason.
- Readiness still fails closed because durable calendar/release evidence is not connected and the required SQL Server concurrency suite and browser UAT have not passed. The backfill and reschedule code builds, but those release gates are still outstanding; write mode must remain disabled.

## Build verification

- `dotnet build ErpWeb.UI/ErpWeb.UI.csproj --no-restore -m:1 -nr:false -v:minimal` succeeded after aligning the Razor component base class with `PageBase` and using the value-type scheduler interval collection correctly.
- Result: 0 errors, 9 warnings. Warnings include the ImageSharp license notice and existing nullable/member-hiding warnings in unrelated files.
- `dotnet build ErpWeb.Core/ErpWeb.Core.csproj --no-restore -m:1 -nr:false -v:q` succeeded after adding Preview/Commit and allocation backfill. Result: 0 errors. The incremental build reports the ImageSharp license warning; the full build reports the repository's existing warning set.
- `dotnet build ErpWeb.UI/ErpWeb.UI.csproj --no-restore -m:1 -nr:false -v:q` succeeded after adding the reschedule/backfill dialogs and guarded DevExpress pre-update handlers. Result: 0 errors, 9 warnings, including the ImageSharp license notice and existing nullable/member-hiding warnings in unrelated files.
- Final verification after the allocation provenance checks: `dotnet build ErpWeb.Core/ErpWeb.Core.csproj --no-restore -m:1 -nr:false -v:q` succeeded with 0 errors and the ImageSharp license warning; `dotnet build ErpWeb.UI/ErpWeb.UI.csproj --no-restore -m:1 -nr:false -v:q` succeeded with 0 errors and 9 warnings (the ImageSharp notice plus existing nullable/member-hiding warnings in unrelated UI files).
- A focused occupied-interval scheduler test was added and its test assembly built. The runner failed to connect to its testhost after 90 seconds; a no-build retry with a 180-second connection window failed the same way. Runtime test evidence is still outstanding.
- Build results do not substitute for SQL Server concurrency evidence, live backfill review, drag behavior verification, or browser UAT. No database backfill or schedule commit was run in this coding turn.
