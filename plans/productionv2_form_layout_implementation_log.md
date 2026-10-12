# ERP form layout implementation log

Date: 2026-10-11. Branch: `productionv2`.
Baseline and current HEAD: `61a4a66099061ef07157af15f8020cb257077420`.
The reviewed baseline matches; no checkout/reset or branch change was needed.
The two supplied plan files were already untracked and remain untouched.

## UI standard evidence — Admin Settings canary

- Target module: Admin (configuration editor; not a transaction entry).
- Target page: `ErpWeb.UI/Admin/AdminSettings.razor`.
- Page archetype: master/settings list with an inline-content editor popup.
- Closest live reference: the existing Admin Settings form and its isolated CSS.
- Shared components: `MenuAuthorize`, DevExpress editors and `DxPopup`.
- Shared/global CSS: `site.css`, `inventory-chrome.css`, `pr-popup-grid.css`; new opt-in `erp-form-layout.css` loads before isolated styles.
- Route/mode model: `/admin/settings`; selected setting controls the popup; `CanEdit` controls read-only behavior.
- List actions: select module, filter/search and open editor (unchanged).
- Entry/workflow actions: Save, Clear (use default), Cancel (unchanged).
- Editable statuses: `CanEdit`; read-only settings redirect to their owning screen (unchanged).
- Permission codes: `MenuCodes.AdminSettings`, `PermissionCodes.Edit` (unchanged).
- Intentional deviations: None.

## Evidence and scope decisions

- Inventory contains 231 Razor artifacts and 84 isolated stylesheets: 189 route pages, 18 ordinary components, 4 popup components, 13 lookups and 7 imports. See the migration manifest for every artifact.
- Inventory metadata is source-derived. Potential module candidates still require individual field review and runtime approval; classification is not a claim of visual verification.
- Shared `.iv-field` is a grid with a 6px gap. Admin Settings stretches two fields with different child counts (the Stored at field includes a hint). The scoped primitive top-aligns fields and their content.
- Admin editor CSS already forces editor width to 100%; logical max width will constrain the control without replacing that rule.
- No wrapper is needed: each affected DevExpress editor supports a plain `CssClass`. Existing isolated editor selectors remain valid.
- Preserve the 520px popup, existing field order, natural checkbox sizing, action order and content-based action area. There is one popup and no footer template, so the explicit `ShowFooter` requirement does not apply.
- The existing popup actions are disabled during submission. Source inspection also found that Save/Clear handlers do not explicitly return on `IsSubmitting`; this is a pre-existing behavior concern, recorded separately and outside this layout-only change. No double-submission safety claim is made.
- No lookup implementation or business code will be changed.

## Staged verification

Phase 1: shared CSS, registration, source contracts and inventory implemented. Solution build passed with zero errors. The final incremental build reports 53 pre-existing warnings; the new test file produces no warnings. All 22 focused form-layout, popup-footer and grid-sizing tests passed (including four new form-layout tests).

The full suite is failing in unchanged service tests. Four representative failures also reproduce in a clean export of baseline commit `61a4a66099061ef07157af15f8020cb257077420`, built separately under ignored `artifacts/form-layout/baseline`:

| Baseline test | Result |
| --- | --- |
| `ProductionAbsorbedCostCalculatorTests.Calculates_only_frozen_contributors_for_good_quantity` | FAIL on baseline and working tree |
| `PoSupplierServiceTests.GlCode_Existing_FinancialChange_Succeeds` | FAIL on baseline and working tree; validation failed |
| `SaCustServiceTests.AppInvoice_On_CopiesGeneralToInvoice` | FAIL on baseline and working tree; validation failed |
| `PrBomStructureTreeTests.Structure_multi_level_make_make_buy` | FAIL on baseline and working tree; expected 3, actual 2 |

The full-suite gate was initially a blocker under the plan. The user directed us to continue through the five named cross-archetype canaries, so those canaries are implemented while unrelated baseline failures remain untouched. Broad rollout remains deferred until the baseline suite and required visual sign-off gates are resolved.

In Fluent Light at a 1087×768 browser viewport, Stored at and Value editor tops now align, Number is compact, Remark remains full width, and Save/Clear/Cancel are visible. The post-change popup is open in the retained browser tab. Narrower/wider viewports, dark theme, zoom, validation error state and console checks remain unverified.

The first restricted build exited unsuccessfully without compiler diagnostics. Serial project builds passed. The restricted testhost could launch but could not connect to its local VSTest socket; the focused contracts passed when run with loopback access.

## Batch report — shared infrastructure

```text
BATCH: Phase 0 inventory + Phase 1 shared infrastructure
BASE SHA: 61a4a66099061ef07157af15f8020cb257077420
END SHA: 61a4a66099061ef07157af15f8020cb257077420 (uncommitted changes)

Pages/components migrated: NONE (stylesheet registered in host App.razor)
Pages/components reviewed but unchanged: AdminSettings; source inventory of all 231 Razor artifacts
Shared CSS changed: new ErpWeb/wwwroot/css/erp-form-layout.css
Page CSS changed: NONE

Behavior/code-behind changed: NONE
Core/Model/Repository changed: NONE
Database/SQL changed: NONE
Binding semantics changed: NONE
Lookup behavior changed: NONE

Build: PASS
Tests: FAIL (3,276 passed / 110 failed / 0 skipped; 3,386 total); PASS (22 focused layout/popup/grid contracts)
Visual verification: NOT AVAILABLE (authentication required; no form opted in yet)
Viewports checked: NONE
Themes checked: NONE
Known exceptions: pre-existing service-test failures; existing Admin submission-guard concern recorded above
Rollback commit(s): NONE (uncommitted)
```

Verification logs are in ignored `artifacts/form-layout`: `phase1-build.log`, `phase1-tests.log`, `layout-contract-tests.log`, and `baseline-tests.log`. `git diff --check` passes. The sole tracked Razor change registers the stylesheet; it changes no editor, event, expression, route or popup.

Rollback is limited to removing the new stylesheet registration and the task-created stylesheet, contract-test file, manifest and log. Preserve the user's two supplied plan files. No database rollback is involved.

Full-suite run finished before the Admin canary in 5m 37s: 3,276 passed, 110 failed, 0 skipped. The failure inventory is in `productionv2_form_layout_test_failures.md`; only the four representative failures listed above were independently rerun against the clean baseline. The post-canary incremental build passed and all 23 focused layout/popup/grid tests passed; the full suite was not rerun after this markup change.

## Batch report — Admin Settings canary

```text
BATCH: Admin Settings canary
BASE SHA: 61a4a66099061ef07157af15f8020cb257077420
END SHA: 61a4a66099061ef07157af15f8020cb257077420 (uncommitted changes)

Pages/components migrated: ErpWeb.UI/Admin/AdminSettings.razor (one settings editor)
Pages/components reviewed but unchanged: sibling controls/popup body; no second popup exists in the file
Shared CSS changed: none in this batch
Page CSS changed: none; existing isolated full-width editor rules remain compatible

Behavior/code-behind changed: NONE
Core/Model/Repository changed: NONE
Database/SQL changed: NONE
Binding semantics changed: NONE
Lookup behavior changed: NONE

Build: PASS (solution)
Tests: PASS (23 focused); full suite: FAIL before this batch (baseline failures documented)
Visual verification: PASS at one desktop view
Viewports checked: 1087x768
Themes checked: Fluent Light
Known exceptions: other sizes/themes/states are pending; later cross-archetype canaries proceed under the user's direction, while broad rollout waits for the full-suite and visual sign-off gates
Rollback commit(s): NONE (uncommitted)
```

The canary changes only the editor-grid opt-in classes and semantic `CssClass` values. The existing 520px popup width, field order, 900px breakpoint, binding expressions and action markup are preserved. The `Admin_Settings_Canary_Uses_Scoped_Semantic_Widths` source contract covers this opt-in.

## Batch report — cross-archetype canaries

```text
BATCH: Sales customer master, Sales Order, Purchase Order, Inventory Goods Receipt, Daily Production Entry
BASE SHA: 61a4a66099061ef07157af15f8020cb257077420
END SHA: 61a4a66099061ef07157af15f8020cb257077420 (uncommitted changes)

Pages/components migrated: ErpWeb.UI/Sales/Masters/SaCustEntry.razor; ErpWeb.UI/Sales/Transactions/SaSo.razor; ErpWeb.UI/Purchase/Transactions/PoOrder.razor; ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor; ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor
Shared CSS changed: none
Page CSS changed: none; existing page and commercial document rules remain in place

Behavior/code-behind changed: NONE
Core/Model/Repository changed: NONE
Database/SQL changed: NONE
Binding semantics changed: NONE
Lookup behavior changed: NONE

Build: PASS (UI, test, and app projects built serially; 0 errors)
Tests: PASS (5 form-layout contracts; 1 popup-footer contract); full suite not rerun after the canary markup
Visual verification: Admin Settings remains checked at 1087x768 in Fluent Light; other four pages await a signed-in runtime inspection
Known exceptions: the baseline full suite has 110 failures, four representative failures reproduced on the clean baseline; broad rollout remains gated
Rollback commit(s): NONE (uncommitted)
```

The canaries add opt-in scopes and semantic width classes to existing editors or their immediate wrappers. They preserve customer identity columns, commercial document structure and `sdoc-*` behavior, receipt popup actions, and the production operation-selection/workspace and quantity-change bindings. No line-grid columns, lifecycle, revision, posting, rollback, or lookup implementation changed. Full runtime sign-off is pending: after the local app restart, the browser opened at the sign-in page, so the user must sign in again before the remaining four pages can be inspected.
