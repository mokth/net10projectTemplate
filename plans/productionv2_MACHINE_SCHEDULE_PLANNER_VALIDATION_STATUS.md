# Machine Schedule Planner Validation Status

Date: 2026-10-12  
Status: **NOT COMPLETE** under section 14 of the approved plan. The focused planner and SQL gates pass, drag → preview → confirm passed in browser UAT, and the disposable UAT database has been restored to its original schedule with writes disabled. The broader Planning + Production suite still fails, stale-preview refresh has not been exercised in the browser, and actual 125% Windows scaling remains unverified.

Scope: this status covers `productionv2_MACHINE_SCHEDULE_PLANNER_FINAL_CLOSURE_APPROVED_PLAN.md`. The separate Work Order WIP Transfer plan is outside this request.

## Build and test evidence

- Full solution build: `dotnet build ErpWeb.slnx --no-restore -m:1 -nr:false -v:q -clp:ErrorsOnly` — **succeeded, 0 errors, 67 warnings** after the stale-preview refresh change.
- Focused planner/calendar/readiness/release suite, with `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` and the isolated LocalDB connection — **53 passed, 0 failed, 0 skipped**. Latest result: `ErpWeb.Tests/TestResults/MachinePlannerFocusedClosureFinal2.trx`.
- Planner input parser tests — **4 passed, 0 failed, 0 skipped**. Result: `ErpWeb.Tests/TestResults/MachinePlannerInputClosureFinal.trx`.
- The focused run includes all 7 mandatory planner SQL cases; they ran with `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`, with no skips.
- Latest broader Planning + Production filter (`FullyQualifiedName~Planning|FullyQualifiedName~Production`) — **644 passed, 47 failed, 8 skipped, 699 total**. Result: `ErpWeb.Tests/TestResults/PlanningProductionClosureFinal.trx`. Failures span BOM/product-definition, nested phantom snapshot, production material valuation/cost/issue, process handoff, Work Order Transfer, and production SQL fixtures. The broad regression gate remains failed.

The focused run used the exact 53 test methods recorded in `MachinePlannerFocusedResumeExact.trx`:

```powershell
$env:ERPWEB_REQUIRE_SQLSERVER_TESTS = '1'
$env:ConnectionStrings__SqlServerTestConnection = 'Server=(localdb)\MSSQLLocalDB;Database=ERPWeb_MachineSchedulePlanner_test_20261012_a73c9d61;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true'
$trx = [xml](Get-Content ErpWeb.Tests\TestResults\MachinePlannerFocusedResumeExact.trx)
$filter = ($trx.TestRun.TestDefinitions.UnitTest | ForEach-Object { "FullyQualifiedName=$($_.TestMethod.className).$($_.TestMethod.name)" } | Sort-Object -Unique) -join '|'
dotnet test ErpWeb.Tests\ErpWeb.Tests.csproj --no-build --no-restore --filter $filter --logger 'trx;LogFileName=MachinePlannerFocusedClosureFinal2.trx' --results-directory ErpWeb.Tests\TestResults
```

The broader run used `dotnet test ErpWeb.Tests\ErpWeb.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~Planning|FullyQualifiedName~Production" --logger 'trx;LogFileName=PlanningProductionClosureFinal.trx' --results-directory ErpWeb.Tests\TestResults`.

The planner SQL integration tests ran against `(localdb)\MSSQLLocalDB`, database `ERPWeb_MachineSchedulePlanner_test_20261012_a73c9d61`. Browser UAT used the separately authorized disposable `.\SQLEXPRESS` / `ERPWeb` database.

## Schema, backfill, and preflight

- `scripts/create-machine-schedule-planner.sql` applied successfully to the isolated scratch database.
- Allocation revision columns, positive-interval check constraint, and allocation indexes were verified.
- The SQL backfill test verified dry-run is read-only and apply creates/verifies allocation fragments while preserving schedule dates and revision.
- The isolated scratch-database preflight reported 42 active capacity operations, 0 missing current allocations, 0 stale active provenance rows, and 0 unexplained overlaps.
- The same read-only preflight was run on the actual disposable ERPWeb UAT database after restoring its schedule: **2 active capacity operations, 0 missing allocations, 0 stale active provenance rows, and 0 unexplained overlaps**.

## Browser / operator UAT

The user confirmed `Server=.\SQLEXPRESS;Database=ERPWeb;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true` is disposable, authorized drag/confirm UAT, requested the final planner be View Only, and approved a temporary DEMO planner account that would be removed after testing.

- The original ERPWeb baseline was WO00000003 at schedule revision 2: P1/M1 `2026-10-09 08:00–09:23:39.998`; P2/M2 `09:23:39.998–11:03:39.998`.
- The drag handler opened the change dialog after the code-behind retained its target operation independently of the scheduler selection callback. Dragging P1 to 14:00 produced a preview showing the downstream P2 move, and Commit completed with “Machine schedule updated.”
- The schedule was restored through Change Schedule: the preview returned both operations to the original times, then Commit succeeded. ERPWeb now has revision 4 with those original times; the preflight confirms current allocations, provenance, and no overlaps.
- The company write flag (`AdSmParam.ParamId=1003`, DEMO company scope) is **off**. After restarting the app to clear its settings cache, the page visibly showed **View only** and “Schedule changes are disabled by the company setting.” The Oct 5–11 board showed the expected 3 appointments, including WO00000003 P1/P2 at their original times.
- The temporary UAT login and role were removed after signing out. Cleanup verified **0** temporary users, roles, mappings, grants, and enabled planner flag rows.
- The final View Only board screenshot was captured inline during UAT. CUA did not expose a supported filesystem export path, so no PNG is stored beside this report.
- The stale-preview service test passes and the UI now reloads the board after a rejected commit, updates the dialog job snapshot, and retains the rejection message. The stale-preview rejection was not reenacted in the browser after the temporary login was removed, so that browser gate remains **UNKNOWN**.
- Day, Week, Month, zoom, scroll, lookup/filter flows, cancellation, locked-operation messaging, and Change Schedule were verified in the earlier signed-in UAT. No horizontal overflow was observed at 1366×768 or 1920×1080. Actual **125% Windows display scaling** was not tested.
- The earlier browser console query returned no warning/error entries; a fresh console capture was not available after the final stale-preview code change.

## Section 14 completion matrix

| Gate | Status | Evidence / blocker |
| --- | --- | --- |
| Build | PASS | Latest full solution build: 0 errors, 67 warnings. |
| Existing tests | FAIL | Focused suite 53/53 and parser 4/4; latest broader Planning + Production filter 644 passed, 47 failed, 8 skipped. |
| Schema | PASS | Schema script applied; columns, constraint, and indexes verified. |
| Backfill | PASS | SQL dry-run/apply integration test passes; UAT preflight found 0 missing current allocations. |
| Overlap | PASS | UAT preflight found 0 unexplained active fragment overlaps. |
| Provenance | PASS | UAT preflight found 0 stale active allocation rows. |
| Initial release path | PASS | Release capacity reservation and overlap rejection tests pass. |
| Snapshot freeze | PASS | Reschedule test verifies snapshot hash/revision remain unchanged. |
| Finite capacity | PASS | Shift, break, holiday, preventive maintenance, occupied fragments, and half-open adjacency tests pass. |
| Dependency | PASS | Serial/internal-WIP propagation and invalid dependency tests pass. |
| Atomicity | PASS | Injected allocation-write failure preserves dates, revisions, allocations, and audit. |
| Planner race | PASS | Mandatory SQL same-machine concurrent commit test passes. |
| Adjacent jobs | PASS | Half-open adjacent reservation test passes. |
| DP Create race | PASS | Mandatory SQL planner/Create race passes. |
| DP Post race | PASS | Mandatory SQL planner/Post race passes. |
| DP Rollback race | PASS | Mandatory SQL planner/Rollback race passes. |
| Calendar race | PASS | Mandatory SQL planner/calendar writer race passes. |
| Backward | PASS | Backward-scheduled work orders remain locked for reschedule; test passes. |
| Readiness | PASS | SQL evidence refresh establishes readiness only with current evidence and enabled write setting. |
| Feature OFF | PASS | Reloaded browser showed View Only with the company-setting reason; the database flag is 0. |
| Feature ON + ready | PASS | Readiness enabled rescheduling; Change Schedule and drag preview were available in the signed-in DEMO UAT. |
| Drag | PASS | Drag opened preview, showed downstream impact, and Commit persisted the requested schedule; the UAT schedule was then restored. |
| Cancel | PASS | Preview cancel left the schedule unchanged in signed-in UAT. |
| Stale preview | UNKNOWN | Service rejects stale previews and the UI reloads on rejection; browser refresh-after-stale behavior was not reenacted. |
| Timeline Day | PASS | Signed-in UAT observed Day view. |
| Timeline Week | PASS | Signed-in UAT observed Week view. |
| Timeline Month | PASS | Signed-in UAT observed Month view. |
| Zoom | PASS | Signed-in UAT exercised zoom controls; display-only behavior is covered by tests. |
| Scroll | PASS | Signed-in UAT exercised horizontal timeline scrolling. |
| Filters | PASS | Signed-in UAT exercised lookups and search/filter flow. |
| Responsive | UNKNOWN | No horizontal body overflow at 1366×768 and 1920×1080; actual 125% Windows scaling was not tested. |
| Browser console | UNKNOWN | Earlier console query returned no entries, but no fresh capture was available after the last UI change. |
| SQL tests | PASS | Focused mandatory mode ran all 7 planner SQL cases; 0 skipped. |
| UAT | PASS | Drag → preview → confirm and Change Schedule restore completed on ERPWeb; final board was View Only and temporary access was removed. |
| Rollback | PASS | Final company flag is disabled; the reloaded planner showed View Only. The UAT schedule was restored to its original times. |

Section 14 states that any FAIL or UNKNOWN means the feature is not complete. The broad regression gate fails, stale-preview browser refresh is unknown, 125% Windows scaling is unverified, and the final browser console capture is unknown. The disposable ERPWeb UAT database has been restored, the company write flag is off, and the temporary account and role have been removed. Production writes must remain disabled until every §14 gate passes.

## Changed files and scripts

Planner implementation and supporting changes:

- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`
- `ErpWeb.Core/Menus/MenuCodes.cs`
- `ErpWeb.Core/Numbering/RunningNumberKeys.cs`
- `ErpWeb.Core/Production/MachineScheduleAllocationBackfillService.cs`
- `ErpWeb.Core/Production/MachineSchedulePlannerService.cs`
- `ErpWeb.Core/Production/MachineScheduleRescheduleService.cs`
- `ErpWeb.Core/Production/ProductionPoolValuationService.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.cs`
- `ErpWeb.Model/Data/AppDbContext.cs`
- `ErpWeb.Model/Entities/Production/ProductionDomainConstants.cs`
- `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor.cs`
- `ErpWeb.UI/ErpWeb.UI.csproj` (test visibility for internal planner input parser)
- `ErpWeb.Tests/Planning/MachineSchedulePlannerInputTests.cs` (localized date/time parser cases)
- `ErpWeb.Tests/Planning/Master/ProductionCalendarSchedulerTests.cs`
- `ErpWeb.Tests/Planning/Transaction/MachineScheduleRescheduleServiceTests.cs`
- `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderServiceTests.cs`
- `ErpWeb.Tests/Planning/Transaction/WorkOrderQuantityCalculatorTests.cs`
- `ErpWeb.Tests/Planning/Transaction/WorkOrderReadinessValidatorTests.cs`
- `ErpWeb.Tests/Production/Transaction/ProductionLedgerTestFixture.cs`

Database scripts:

- `scripts/create-machine-schedule-planner.sql`
- `scripts/preflight-machine-schedule-planner.sql`

The full Planning + Production regression result and focused SQL `.trx` files are retained under `ErpWeb.Tests/TestResults/`.

## Deployment and rollback

1. Back up the target database and apply `scripts/create-machine-schedule-planner.sql`.
2. Run `scripts/preflight-machine-schedule-planner.sql`; resolve missing allocations, stale provenance, and overlaps before enabling writes.
3. Run backfill in report-only mode, review the output, then run apply mode only on the approved target database.
4. Keep `MACHINE_SCHEDULE_PLANNER_WRITE_ENABLED` disabled until the readiness evidence and browser/UAT gates pass.
5. For rollback, disable `MACHINE_SCHEDULE_PLANNER_WRITE_ENABLED`; the planner must return to View Only. Do not reverse schema/data until a migration-specific rollback is reviewed.




