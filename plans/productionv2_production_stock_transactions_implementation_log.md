PRODUCTION UI STANDARD EVIDENCE
Target workflow: Production Stock Return and Production Stock Adjustment.
List reference: PrFinishedGoodReceiptList.razor/.cs; PrMaterialIssueList.razor/.cs.
Entry reference: PrFinishedGoodReceiptEntry.razor/.cs for source-first production workspace; PrMaterialIssueEntry.razor/.cs for locked Work Order context.
Workflow phase(s): Select Work Order and source pools -> locked adjustment/return workspace -> save NEW -> POST -> rollback -> correction from REVERSED.
Source selector: Server-paged Work Orders and source pool services scoped to tenant and Work Order.
Locked context after selection: Work Order ID, snapshot revision/hash, and existing pool identities are frozen into the draft; no arbitrary pool creation.
Draft identity: NEW generic IvTrxBatch with PRTN/PADJ plus Production sidecar lines and frozen snapshot.
Statuses: NEW, POSTED, REVERSED; historical documents remain read-only.
Save gate: Active RELEASED/IN_PROGRESS Work Order, unchanged valid snapshot, exact pool identity/UOM/factor, positive source quantity, nonzero signed adjustment, reason and OTHER remarks.
Post/readiness gate: Explicit service readiness and feature switch; forward POST disabled by default. PADJ creates only Production ledger entries.
Rollback gate: POSTED only, versioned request, mandatory reason, reverse-order dependency and later-movement checks; posting flags do not block rollback.
Delete/cancel gate: NEW documents may be physically deleted; POSTED documents require rollback; REVERSED documents may be archived.
Correction/reopen behavior: REVERSED documents can seed a new NEW correction using the current Work Order snapshot and current source/cost readiness.
Historical facts shown: Saved requested/base quantities, reason, posting/reversal IDs, approved sidecar cost evidence, and immutable fact totals.
Live values explicitly shown: Current pool quantity/value and readiness; these are labelled current and not presented as posted facts.
Permissions: ACCESS plus ADD/EDIT/DELETE/POST/ROLLBACK/VIEW_COST; PADJ manual/zero approval additionally requires PRICE_OVERRIDE.
Route model: /planning/stock-returns and /planning/stock-adjustments; /new and /{id}/edit|view.
Intentional deviations: PADJ is Production-only and has no Inventory movement/history; manual/zero approval is sidecar-only at six decimals.

IMPLEMENTATION AND VALIDATION STATUS — 2026-10-12
Implemented: additive PRTN/PADJ entities and SQL schema, immutable facts, safe generic-inventory write guards, production posting links, source/cost validation, draft lifecycle, post/replay/rollback, UI routes/lists/editors, menu and permissions, and reconciliation blockers. PADJ uses Production-only movements and exact six-decimal total authority. Both posting switches remain false by default.
Database: `scripts/create-production-stock-transactions.sql` reran on the user-designated `ERPWeb` database. `scripts/preflight-production-stock-transactions.sql` returned PASS. The menu seed completed and its routes/permissions were read back from SQL Server.
Focused tests: 32 passed, 0 failed, 0 skipped, including actual SQL Server migration rerun/rollback checks, schema metadata, material execution/availability, PADJ exact cost evidence, replay/rollback, no-Inventory-side-effect checks, and an injected failure after the Production writer that left no persistent posting or movement.
Regression sample: 56 passed and 5 existing `ProductionMaterialAllocationTests` failed because fixture item `RM001` has no approved MOVING_AVERAGE opening/receipt valuation; four failures stop at Material Issue POST and one reconciliation assertion depends on that failed POST.
Remaining release gate: the plan's full SQL Server PRTN/PADJ service race matrix and all-stage failure-injection matrix were not completed. Keep forward posting disabled until those gates pass.
Build limitation: a fresh build is now blocked by separate in-progress machine-schedule workspace changes: `CoreServiceCollectionExtensions.cs` registers missing `IMachineSchedulePlannerService`/`IMachineSchedulePlannerReadinessService` types, and the UI has a partial `PrMachineSchedulePlanner` page with missing planner DTO/service types. These unrelated files were left untouched. The production stock UI had compiled before those machine-schedule errors appeared; the final test assembly compiled with project-reference rebuilding disabled against the previously successful core artifact.
