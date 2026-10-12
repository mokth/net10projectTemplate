# Production Stock Return and Adjustment implementation record

The attached approved plan was used as the technical specification for the user's request to start implementation. The original plan remains unchanged; this file records repository evidence and verification.

## UI STANDARD EVIDENCE

Target module: Planning / Production execution.

Target pages:
- `ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor(.cs)`
- `ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnEntry.razor(.cs/.css)`
- `ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentList.razor(.cs)`
- `ErpWeb.UI/Planning/WorkOrders/PrProductionStockAdjustmentEntry.razor(.cs/.css)`

Page archetype: shared Production transaction lists and source-driven execution workspaces; the entry pages retain explicit selection, edit, and historical view phases.

Closest live references: `PrFinishedGoodReceiptList` and `PrFinishedGoodReceiptEntry`, `PrMaterialIssueEntry`, `PrDeliveryRequestSearchPopup`, and the existing shared lookup and picker patterns (`SmartLookupInput`, `SaCustomerPicker`, `SaCustomerSearchPopup`).

Shared components: `CommonDataGridEx`, `PrProductionStockWorkOrderPicker`, `PrProductionStockWorkOrderSearchPopup`, shared `SmartLookupInput`, DevExpress grids and popups.

Shared/global CSS: `ErpWeb/wwwroot/css/pr-popup-grid.css` and the established `iv-*` transaction-list styles. Entry-specific responsive styles remain in each page's `.razor.css`.

Route/mode model: New, Edit, and View remain route/document-state driven. Route changes clear the old editor and cancel pending source searches so a prior Work Order cannot leak into a different document.

List actions: one explicit NEW action; View and permission-gated Edit; POST for eligible NEW rows and ROLLBACK for POSTED rows, each with confirmation.

Entry/workflow actions: select eligible Work Order, add eligible source rows through a popup, edit and remove draft lines, Save, View, POST, ROLLBACK, DELETE, and create a correction after reversal. Posting and rollback use the existing service commands.

Editable statuses: NEW only. POSTED and REVERSED records remain historical views; a correction is a new document after reversal.

Permission codes: Access, Add, Edit, Delete, Post, and Rollback. PADJ also uses PriceOverride for manual/zero cost approval and ViewCost for cost visibility. The transaction lookup does not depend on Work Order maintenance permission.

Intentional deviations: None.

## PRODUCTION UI STANDARD EVIDENCE

Target workflow: Production Stock Return (PRTN) and Production Stock Adjustment (PADJ).

List reference: Finished Good Receipt list and the established Production transaction list pattern.

Entry reference: Finished Good Receipt entry and Material Issue entry.

Workflow phases: choose Work Order; inspect eligible production source rows; build a NEW draft; edit an existing NEW draft; view POSTED or REVERSED history; correct only after rollback/reversal.

Source selector: shared picker delegates to bounded, tenant-scoped PRTN/PADJ eligible-Work-Order queries and exact resolvers. A source popup follows Work Order selection.

Locked context after selection: the selected Work Order and production snapshot remain the document context. Existing records reconstruct from saved source and snapshot facts; current eligibility is shown separately.

Draft identity: saved document ID and row version; command request IDs retain idempotent Save/Post/Rollback behavior.

Statuses: NEW, POSTED, REVERSED.

Save gate: NEW plus Add/Edit permission and no UI validation issues; service validation remains authoritative.

Post/readiness gate: saved NEW document, Post permission, posting enabled, no readiness errors, and no unsaved edits; the existing posting service remains authoritative.

Rollback gate: POSTED plus Rollback permission and a required reason, with confirmation.

Delete/cancel gate: DELETE is confirmed and permission/state gated; draft deletion uses the existing soft-delete path. No new cancel/posting authority was introduced.

Correction/reopen behavior: POSTED documents remain read only until rollback. A correction can be created from a REVERSED document using the existing service flow.

Historical facts shown: saved Work Order/source snapshot, adjustment direction and signed quantity, and saved cost decision. Live values remain separate: PRTN current returnable quantity/readiness and PADJ current source-pool BaseQty.

Permissions: transaction access and action permissions listed above; cost visibility and override remain separately gated for PADJ.

Route model: existing New/Edit/View routes and document state; no new route authority.

Intentional deviations: None.

## Implementation coverage recorded

- Dedicated, tenant-scoped eligible Work Order searches and exact resolvers use structural source eligibility and preserve historical list visibility.
- Shared picker and source-search popups use bounded paging, debounced/cancelled searches, and the existing popup grid styling.
- PRTN keeps requested return quantity separate from current returnable quantity; PADJ keeps saved adjustment BaseQty separate from current pool BaseQty.
- PADJ entry accepts positive quantities for ADD/REDUCE while the service retains signed transaction semantics. Cost override and cost visibility remain permission-gated.
- List search and quantity summaries use consistent Work Order/reference matching and source-UOM formatting.
- Entry pages keep Save, Post, Rollback, Delete, View, and correction paths behind their state and permission gates.

## Verification

- `dotnet build ErpWeb.slnx --maxcpucount:1` compiled Model, Core, UI, and Tests, then could not copy referenced assemblies into the running `ErpWeb` output because files were locked by the already-running host.
- The host build succeeded with 0 warnings and 0 errors using a separate output directory and the already-built project references: `dotnet build ErpWeb/ErpWeb.csproj --no-restore --maxcpucount:1 -p:BuildProjectReferences=false -p:OutputPath=C:\wincom\net10projects\artifacts\codex-build\ErpWeb\ --verbosity minimal`.
- The focused test command compiled `ErpWeb.Tests` but VSTest aborted before executing cases: it could not connect to the testhost after 90 seconds. This is an infrastructure/testhost startup failure, not a reported assertion failure. Lookup and PADJ summary tests are present in `ProductionStockWorkOrderLookupTests` and `ProductionStockAdjustmentServiceTests`.
- Browser acceptance was not run: the available browser state had no local ERP tab and this thread has no attached app terminal. Creating or posting ERP transactions without a confirmed test environment would alter business data.
- The repository already contained unrelated worktree state, including a deleted `build.diag.log`; it was left untouched.
