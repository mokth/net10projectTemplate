# Work order entry usability review

22 November 2026. Branch `productionv2`, HEAD `9561c17238159c56bd5ff1ca58afbd63a714eb5b`.
The earlier Production Setup UI changes and untracked `newplans/datatables.pdf` are present at entry and will be preserved.

## Scope and current flow

Class A: UI presentation, labels and navigation only. No live company, warehouse, costing method or stock slice is selected for mutation. The signed-in company/branch and existing server permissions remain authoritative.

Inspected work order entry Razor/code/CSS, delivery-request picker, work order contracts/constants, draft command and release entry points, snapshot/quantity helpers and existing work order tests. Existing path: authorised page → manual input or eligible delivery request → optional preview → existing CreateDraft/UpdateDraftHeader or CreateAndRelease/UpdateAndRelease/ReleaseCurrent → server tenant/permission/concurrency/validation and transaction checks → saved production plan and machine schedule. Delivery-request creation retains its existing demand allocation rules. Material issues and actual production postings happen through their separate existing services. Reopen, refresh, change setup, substitute, cancel and delete retain their existing guards and reason requirements.

## Findings and bounded changes

- Replace snapshot/contract/revision jargon in the everyday flow with saved plan, production setup and version; keep support details available separately.
- Present the order source first, followed by product/quantity, dates, and a clearly visible check-materials action.
- Explain start-date versus finish-date scheduling, including which date is entered and which is calculated.
- Make Send to production distinct from Save draft and from issuing stock. Keep the existing release confirmation and save/release calls.
- Use readable status/enum names and names beside codes. Preserve the original bound values.
- Reduce default table columns and expose the rest with Show more details. Keep required quantities and units together. Do not aggregate different materials/units into an unlabelled total.
- Replace engineering roadmap text with practical production instructions. Actual server blockers, feature flags and permissions remain intact.
- Keep optional notes/reference and support details expandable, with a short working example and clear next-action guidance.
- Correct the delivery-request picker's single-click behaviour: its grid focused a row but did not update the item used by Use this request. Connect the existing focused-row event to that selected item; retain the double-click and selected-item paths and the same selection callback. Reduce its initial 15 columns to 8, with the remaining facts under Show more details.
- Use a single-column product/quantity/setup/date summary in the send confirmation. The previous general-purpose two-column summary broke long values in a narrow popup.
- Limit the entry dialogs to 95% of the viewport, using the same `min(...px, 95vw)` pattern as other production forms. The 390px check caught the fixed 460px send dialog clipping its buttons; normal desktop widths stay the same.
- Override the shared footer's negative sticky offset on this entry. At desktop size it could put the save/send button centres below the viewport. Keep this page's footer at `bottom: 0` with bottom padding so the actions remain fully visible and clickable.

## Invariants and checks

| Risk | Required invariant / evidence |
| --- | --- |
| Stock, WIP or valuation changes | Zero changes to core services, calculations, physical/financial ledgers, entities or schema. No real ERP posting/release during UI verification. |
| Labels change submitted values | Keep all original input bindings, enum values, identity and concurrency tokens; compare the binding sets. |
| Easier actions bypass business guards | Retain permission predicates, existing command handlers, release confirmation, cancellation/deletion distinctions and reason fields. |
| Preview mistaken for a saved order | Clearly mark changed/stale previews; saving and sending still run authoritative server checks. |
| Incorrect quantities hidden or combined | Show original per-material values with their units; no new quantity calculations. |
| Unclear scheduling | Explain the existing Forward/Backward options; do not change scheduling/date defaults or calculations. |

Verification: solution build; focused work order, quantity, readiness and scheduling tests; relevant UI contracts; browser checks on real components with synthetic data and mocked services if available. SQL Server concurrency, live-company UAT and the full ERP suite are separate evidence and will be stated explicitly. No new tests for plain caption changes; meaningful tests only for new state-dependent display decisions.

Rollback: revert only this task's UI/helper/test/document changes. No database rollback is needed.

## Outcome

Implementation complete locally. Verification status: **PARTIAL** because the broader focused regression includes a failure in unchanged backend nested-material logic. **GO for review of the local UI diff; NO-GO for an assertion of full production readiness.** No deployment or live ERP transaction was performed.

Changed entry Razor/code/CSS, the delivery-request picker Razor/code, new display-only `WorkOrderEntryText`, and nine state/error presentation tests. The earlier Production Setup changes and `newplans/datatables.pdf` remain intact. Existing create/save/send/reopen/refresh/change-setup/substitute/select-machine/cancel/delete service calls, request values, tenant boundaries, permission conditions and concurrency tokens remain in their existing owners. Preview navigation now opens Materials (tab 3) instead of Production stages (tab 1).

All original `@bind-Value/Text/Checked/Date` targets in work order entry are retained. The only additions are the three display toggles. The redundant single-choice Manual source selector is replaced with an explanation of the existing direct-entry/delivery-request paths; those stored source values are unchanged. Names, quantities, precision and units remain sourced from the existing models. No new stock-availability or cost calculation is introduced.

### Build and automated checks

Commands run from the repository root:

```powershell
dotnet build ErpWeb.slnx --no-restore --verbosity quiet -p:BuildInParallel=false -p:UseSharedCompilation=false -m:1
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~WorkOrderEntryPresentationTests|FullyQualifiedName~ProductionSetupPresentationTests|FullyQualifiedName~WorkOrderQuantityCalculatorTests|FullyQualifiedName~WorkOrderReadinessValidatorTests|FullyQualifiedName~WorkOrderScheduleCalculatorTests|FullyQualifiedName~WorkOrderSnapshotBuilderTests|FullyQualifiedName~WorkOrderSnapshotHasherTests|FullyQualifiedName~ProductionWorkOrderServiceTests|FullyQualifiedName~ProductionWorkOrderCalcTests|FullyQualifiedName~MachineScheduleDraftServiceTests|FullyQualifiedName~ErpWeb.Tests.UI|FullyQualifiedName~DxPopupFooterContractTests' --logger 'trx;LogFileName=work-order-focused.trx' --results-directory .codex-work/work-order-usability/artifacts/focused-results --verbosity quiet
git diff --check
```

The sandbox test launch aborted before test execution because vstest could not connect to its testhost. A rerun with local process access executed **292 tests: 291 passed, 1 failed, 0 skipped**. All nine new state-dependent work-order presentation tests passed, including old-plan update precedence, unsaved/stale preview guidance, read-only statuses, permitted actions and support-code/quantity preservation. The quantity, readiness, scheduling, snapshot builder/hasher, draft-machine scheduling, work-order calculation and UI contract groups passed.

The failure is `ProductionWorkOrderServiceTests.Nested_phantom_material_keeps_its_own_bom_revision_and_line_snapshots`, at line 1065: its direct `SaveDraftAsync` result does not contain expected nested material `RM002`. This test, its Core/Model dependencies and its stock/BOM calculation path have no diff against the pinned commit and do not call the modified UI. It is recorded as an existing backend regression outside this presentation task; no costing or material-explosion rule was changed to make it pass.

The solution build passed with **0 errors / 117 existing warnings**. After the dialog-width-only change, `dotnet build ErpWeb.UI/ErpWeb.UI.csproj --no-restore --verbosity quiet -p:BuildProjectReferences=false -p:BuildInParallel=false -p:UseSharedCompilation=false -m:1` also passed with **0 errors / 8 existing warnings**. The final footer CSS correction was built with that same UI command: **0 errors / 0 warnings**. The latest focused test run, before the final width/footer presentation adjustments, returned **291 passed / 1 failed / 0 skipped**, including all nine work-order presentation tests passing. No command or state logic changed after that test run. Build/test output is retained under ignored `.codex-work/work-order-usability/artifacts`. `git diff --check` passed; no Core/Model/Data changes are present.

### Browser checks

Real DevExpress/Blazor components are hosted in a local fixture with synthetic gift-box data and mocked services, with no database connection. Verified directly so far:

- Missing-product feedback uses the Product caption and explains the next action.
- Product resolution fills the name/unit/usual production setup; quantity editing marks a previous preview as needing an update.
- Check materials & dates opens Materials and explicitly marks the preview as unsaved; box quantities in PCS and ribbon quantities in M stay on separate lines.
- Finish-date scheduling makes the start date read-only, while the entered finish date remains editable.
- Show more details exposes the retained material fields and readable issue-method text.
- Send confirmation identifies the exact product, quantity/unit, setup and entered date, explains read-only details and separate stock issue; the confirmation was dismissed without sending.
- Synthetic save validation opens the optional notes area and uses friendly field names.
- Sent orders show separate material/actual-production guidance, preserve read-only inputs and expose the existing permitted material-issue/return-to-draft actions.
- Older drafts retain their edit/send blocker and direct the user to update the production setup.
- Saved delivery-request drafts retain their existing save-to-update path instead of a generic preview call.

The rebuilt request picker displays 8 default columns; a single click enables Use this request. Choosing sample DR100 fills the product and **100 PCS still to plan**, keeping the original **150 PCS requested / 50 PCS already planned** facts available. Check materials & dates follows the delivery-request preview path and opens Materials. At 390px, the form stacks into one column and the page width stays 390px.

Final rebuilt visual checks passed. The desktop send dialog is 460px wide with a readable single-column summary. At 390px, its settled bounds stay within the viewport (left 28px, right 362.5px), with both confirmation buttons fully inside. The help popup also fits the 390×844 viewport and its Close action works. The corrected footer uses `bottom: 0`; the send button ends at 879.5px within an 887px desktop viewport and at 836.2px within the 844px mobile viewport. Clicking Send to production now opens the confirmation directly. Confirmations were dismissed without invoking the final send. The browser viewport was restored to its original 1612px width. The fixture omits the full application's Font Awesome registration; decorative icon absence in the preview is not an application change.

SQL Server concurrency, full ERP suite, actual-company posting/reconciliation and live-user UAT were **not run**. This task has **zero costing, stock, WIP, physical/financial ledger, schema, feature-flag or posting-rule changes**. The local UI fixture performs no stock movement, demand allocation or machine-capacity reservation. UI usability evidence does not certify production costing or SQL Server locking semantics.
