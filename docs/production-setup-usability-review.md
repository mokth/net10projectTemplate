# Production setup usability review

Reviewed 22 November 2026 on `productionv2`, HEAD `9561c17238159c56bd5ff1ca58afbd63a714eb5b`.
Starting tracked diff: clean. Existing untracked `newplans/datatables.pdf` is outside this task.

## Scope and evidence

Class A: presentation and authoring navigation only. Applies to the existing signed-in company and branch; no tenant, stock item, warehouse, costing method or business date is selected or changed by this review.

Inspected `PrProductDefList`, `PrProductDefEntry`, `PrBomExplorer`, `PrProductDefCentreProjection`, `IPrProductDefService`, `PrProductDefService`, planning entity contracts, product-definition authoring/dirty/phase-one tests, `ProductDefinitionSnapshotLoader`, `WorkOrderQuantityCalculator` and `WorkOrderSnapshotBuilder` call sites. Located the existing definition schema scripts; no schema work is needed.

Current flow: authorised list → company-scoped product/definition lookup → local stage/process/material/machine/operator editors → existing `SaveAsync` or `CreateNewVersionAsync` → server permission, tenant, validation, transaction and concurrency checks → versioned definition tables. Saving can already change a purchased stock item's manufacturing classification to made in-house. Activation already controls the current revision/default. Work orders later load a separate definition snapshot and perform their own readiness checks. This page does not post stock or reverse production.

## Findings and bounded implementation

- Internal enum values are exposed as choices. Display plain English while retaining every stored value.
- Stage, route, centre, operation and process are mixed. Use production area, production stage and work step consistently, with familiar original terms in help where useful.
- Essential inputs compete with losses, substitute groups, timing and cost options. Keep the everyday fields visible and put specialist fields in expandable sections without clearing or changing their values.
- Quantities lack nearby context. Explain the quantity of finished product that the material list makes, and distinguish stage output from machine output per cycle.
- Selectors show mostly codes. Include existing names/descriptions and allow search by either.
- Empty-state text assumes that no materials means previous-stage consumption, and machine/operator warnings ignore process type. Describe only known facts and use the existing process-type rules.
- Revision status and unsaved local rows are unclear. Explain draft/use/new-version actions and keep the existing unsaved-change protection.
- Validation exposes property paths. Present the relevant work step/material and a readable field label without altering server validation.
- Add guided previous/next navigation, short instructions at each tab, and a concise working example in help.
- Expand the material tree on load so the actual materials are immediately visible. Identify the separate breakdown screen as the version currently in use, rather than implying it previews unsaved draft changes.
- Use the selected stock item's existing manufacturing type for the classification notice; previously the new-entry view could show a purchased-to-made warning for an item already made in-house. The save service still reads the authoritative stock item from the database.

## Invariants and verification plan

| Risk | Invariant / verification |
| --- | --- |
| Stock, quantity, WIP, valuation or posting change | Zero changes to services, calculations, persisted entities, transactions, stock/cost/ledger or schema. Compare final file scope. |
| New captions alter saved values | Enum labels and lookup descriptions are presentation only; retain original value bindings and defaults. Verify mappings and build. |
| Collapsed fields lose values | Keep all existing bound controls; no reset on expansion/collapse. Inspect bindings and regression tests. |
| Optional work is shown as mandatory | Match `PrProcessTypes` and existing activation rules; test manual, inspection, wait, subcontract, packing and machine cases. |
| Navigation loses entered rows | Use existing guarded navigation and dirty snapshots. Run authoring and dirty-state tests. |
| Validation becomes misleading | Retain server messages/keys and contextualise labels; test nested and missing-row cases. |
| Existing stock classification side effect is obscured | Retain the notice that draft save also changes purchased to made in-house. |

Run solution build, focused product-definition/planning tests and relevant UI contract checks. Inspect a rendered page if an authenticated local application is available. SQL Server concurrency and the full ERP suite are not automatically required for presentation-only changes; record exactly what ran. No production records, activation, posting or database migrations are authorised or needed for UI verification.

Rollback: revert only the presentation files introduced/changed by this task; no database rollback is needed.

## Verification outcome

**VERIFIED: presentation build and focused checks. PARTIAL: wider repository regression.**

Implemented the bounded UI changes in the list, entry, material breakdown, isolated entry CSS and menu caption, with display-only helpers in `ProductionSetupText`. Stored definition/process/issue/supply codes, quantities, units, rates, loss allowances, versioning and save/activation service calls are retained. All **46 original two-way binding fields** remain present. Stage switching starts a clean work-step editor for the selected stage; new-entry navigation uses the existing unsaved-editor guard. Expandable sections keep their controls bound when collapsed.

**Zero costing, stock quantity, WIP, ledger, posting, rollback, entity, migration or database schema changes.** No production ERP records were created or activated. No costing-policy or rollout flags were changed. The metadata correction to the classification notice does not change the save service's stock classification rules or authority.

Commands and results:

1. `dotnet build ErpWeb.slnx --no-restore --verbosity quiet -p:BuildInParallel=false -p:UseSharedCompilation=false -m:1` — **passed, 0 errors**. The final incremental build reported 8 warnings in existing, untouched UI files. The earlier full build also reported existing nullable/analyser/package-advisory warnings. Final log: `.codex-work/production-setup-preview/artifacts/final-build.log`.
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~ProductionSetupPresentationTests|FullyQualifiedName~ProductDefinitionAuthoringTests|FullyQualifiedName~PrProductDefPhase1Tests|FullyQualifiedName~PrProductDefCentreProjectionTests|FullyQualifiedName~PrProductDefEntryDirtyTests|FullyQualifiedName~PrBomStructureMergeTests|FullyQualifiedName~WorkOrderSnapshotBuilderTests|FullyQualifiedName~ErpWeb.Tests.UI|FullyQualifiedName~DxPopupFooterContractTests' --logger 'trx;LogFileName=production-setup-focused.trx' --results-directory .codex-work/production-setup-preview/artifacts/focused-results --verbosity quiet` — **110 passed, 0 failed, 0 skipped**. Re-run against the final compiled UI.
3. Broader check with filter `'(FullyQualifiedName~ErpWeb.Tests.Planning.Master&FullyQualifiedName!~SqlServer)|FullyQualifiedName~ErpWeb.Tests.Planning.Transaction.WorkOrderSnapshotBuilderTests|FullyQualifiedName~ErpWeb.Tests.UI|FullyQualifiedName~ErpWeb.Tests.Other.DxPopupFooterContractTests'` — **235 passed, 11 failed, 0 skipped**. Results: `.codex-work/production-setup-preview/artifacts/test-results/production-setup.trx`.
4. `git diff --check` and editable-binding comparison against the pinned HEAD — passed; no binding fields removed or added.

The first sandboxed test attempt aborted before connecting to its test host. Tests were then run with approved execution outside the sandbox; the counts above are completed runs, not the aborted attempt.

Browser verification used the real Razor component in a loopback-only host with mocked services and sample data, with no database connection. Checked the desktop layout, 390px single-column layout, stage selection, next-step navigation, unsaved-entry prompt, material quantity explanation, default material-tree expansion, machine/operator search by name, local assignments, contextual validation, help example and read-only version guidance. Restored the browser viewport afterward. This is not authenticated, live-company UAT. The material breakdown screen was compiled and reviewed; its calculations were not altered.

### Wider failures that remain

| Unchanged test class | Failures | Observed result |
| --- | --- | --- |
| `PrBomStructureTreeTests` | 7 | Older MAKE/PHANTOM recursion, missing-child, circular and depth expectations differ from the current explicit supply-source/child-definition behavior. |
| `PrProductDefServiceTests` | 1 | `Circular_bom_rejected_on_save` expects rejection but the current service accepts the test's definition. |
| `BomExplosionServiceTests` | 1 | `Make_without_bom_is_hard_error` expects an older error-message substring. |
| `ProductDefinitionSnapshotLoaderTests` | 2 | Date-window expectations and current active-definition resolution differ; one test receives a null result. |

These tests exercise unchanged core implementations and do not call the UI. The core/model files and these test files have no diff from the pinned HEAD. No production rule was weakened to make them pass. Their broader resolution is outside this presentation-only change.

SQL Server concurrency tests, the complete ERP suite, live-company UAT and production reconciliation were **not run**. No stock/cost reconciliation is applicable to this UI-only diff because it introduces no physical or valuation writes.

**GO for local review of the UI changes. NO-GO for claiming a fully verified production release from this evidence:** the 11 wider failures require separate investigation. Rollback remains a source-only revert of these task files; no database rollback is required.
