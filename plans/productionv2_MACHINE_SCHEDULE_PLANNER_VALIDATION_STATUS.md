# Machine Schedule Planner Validation Status

Date: 2026-10-12  
Status: **NOT COMPLETE** under section 14 of the approved plan. The focused planner implementation and SQL gates pass, but the broad regression gate fails and browser/operator gates remain unknown.

Scope: this status covers `productionv2_MACHINE_SCHEDULE_PLANNER_FINAL_CLOSURE_APPROVED_PLAN.md`. The separate Work Order WIP Transfer plan is outside this request.

## Build and test evidence

- Full solution build: `dotnet build ErpWeb.slnx --no-restore -m:1 -nr:false -v:q -clp:ErrorsOnly` — **Build succeeded, 0 errors, 58 warnings**.
- Focused planner/calendar/readiness/release suite, with `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` and the isolated LocalDB connection — **53 passed, 0 failed, 0 skipped**. Result: `ErpWeb.Tests/TestResults/MachinePlannerFocusedFinal.trx`.
- Mandatory planner SQL cases — **7 passed, 0 failed, 0 skipped**. Result: `ErpWeb.Tests/TestResults/MachinePlannerSqlEvidenceRebuilt.trx`.
- Broader Planning + Production regression filter (`FullyQualifiedName~Planning|FullyQualifiedName~Production`) — **649 passed, 36 failed, 3 skipped, 688 total**. Result: `ErpWeb.Tests/TestResults/PlanningProductionRegressionFinal.trx`. Failures include BOM/product-definition, nested phantom snapshot, production material valuation/cost/issue, process handoff, and Work Order Transfer tests. These are outside the focused planner set; they still prevent the broader regression gate from passing.

The SQL integration tests ran against `(localdb)\MSSQLLocalDB`, database `ERPWeb_MachineSchedulePlanner_test_20261012_a73c9d61`. The supplied `.\SQLEXPRESS` / `ERPWeb` database was not used.

## Schema, backfill, and preflight

- `scripts/create-machine-schedule-planner.sql` applied successfully to the isolated scratch database.
- Allocation revision columns, positive-interval check constraint, and allocation indexes were verified.
- The SQL backfill test verified dry-run is read-only and apply creates/verifies allocation fragments while preserving schedule dates and revision.
- `scripts/preflight-machine-schedule-planner.sql` after the SQL suite reported:

  | Finding | Count |
  | --- | ---: |
  | Active capacity operations | 42 |
  | Unverified or missing current allocations | 0 |
  | Stale active allocation provenance | 0 |
  | Unexplained active fragment overlaps | 0 |

## Browser attempt

The app was started on `http://127.0.0.1:5099` with the isolated scratch database. Opening `/planning/work-orders/machine-schedule` redirects to `/login`. The scratch database contains zero `dbo.userlogin` rows and zero `dbo.UserRoleMapping` rows, so no authorized operator can sign in yet. The browser tab is left open for handoff. No planner before/after screenshots, browser-console check, or operator walkthrough could be captured while the login gate is empty.

## Section 14 completion matrix

| Gate | Status | Evidence / blocker |
| --- | --- | --- |
| Build | PASS | Full solution build: 0 errors. |
| Existing tests | FAIL | Focused suite passes 53/53; broader Planning + Production filter has 36 failures and 3 skips. |
| Schema | PASS | Schema script applied; columns, constraint, and indexes verified. |
| Backfill | PASS | SQL dry-run/apply integration test passes; missing current allocations = 0. |
| Overlap | PASS | Preflight unexplained active overlaps = 0. |
| Provenance | PASS | Preflight stale active allocation provenance = 0. |
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
| Feature OFF | UNKNOWN | Browser cannot reach the planner board until an authorized test account exists. |
| Feature ON + ready | UNKNOWN | Requires signed-in browser UAT. |
| Drag | UNKNOWN | No browser drag/preview/confirm/reload evidence. |
| Cancel | UNKNOWN | No browser cancel evidence. |
| Stale preview | UNKNOWN | Service rejects stale previews; browser refresh behavior is not verified. |
| Timeline Day | UNKNOWN | Browser UAT blocked at sign-in. |
| Timeline Week | UNKNOWN | Browser UAT blocked at sign-in. |
| Timeline Month | UNKNOWN | Browser UAT blocked at sign-in. |
| Zoom | UNKNOWN | Browser UAT blocked at sign-in. |
| Scroll | UNKNOWN | Browser UAT blocked at sign-in. |
| Filters | UNKNOWN | Browser UAT blocked at sign-in. |
| Responsive | UNKNOWN | No 1366×768, 1920×1080, or 125% scaling evidence. |
| Browser console | UNKNOWN | No signed-in planner page available for console inspection. |
| SQL tests | PASS | Mandatory mode ran all 7 SQL cases; 0 skipped. |
| UAT | UNKNOWN | No test user/role is seeded in the scratch database. |
| Rollback | UNKNOWN | Browser feature-off transition was not exercised. |

Section 14 states that any FAIL or UNKNOWN means the feature is not complete. The remaining user action is to provision a disposable operator login and planner access in the isolated scratch database, then sign in to the open local tab so the browser acceptance script can be completed.

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
