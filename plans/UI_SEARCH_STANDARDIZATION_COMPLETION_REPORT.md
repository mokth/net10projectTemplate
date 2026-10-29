# Global Inline Search UI Standardization - Completion Report

Date: 2026-10-30
Plan: `global_inline_search_fcf5d6b6.plan.md` (Revision 4, unchanged)
Verdict: **PARTIAL PASS.** Static, build, contract-test, full-suite and 42/56 live browser controls are **PASS** with zero new failures. 14 browser rows remain **NOT VERIFIED** (1 pre-existing database defect, 13 gated behind absent prerequisite data). The pull request therefore stays **draft** and unmerged, per the plan's status policy.

## 1. Isolation and Git SHAs

| Item | Value |
| --- | --- |
| Development worktree | `C:\wincom\net10projects-inline-search` |
| Development branch | `ui/global-inline-search-dev` |
| Development base | `8d09a224ae740695199e4bed30c475afb2cf268f` (local `productionv2`) |
| Delivery worktree | `C:\wincom\net10projects-inline-search-delivery` |
| Delivery branch | `ui/global-inline-search-standard-v1` |
| Delivery base | `371725c0bda74e386a66bfa3c7559c6f227a14dd` (`origin/productionv2`) |
| Delivery UI commit | `cb1b65d` - `feat(ui): standardize inline search rows across ERP screens` |
| Baseline A worktree | `C:\wincom\net10projects-baseline-local` (detached at `8d09a22`) |
| Baseline B worktree | `C:\wincom\net10projects-baseline-remote` (detached at `371725c`) |

The development worktree is based on three local commits (`0afe632` backdated replay, `0d1dde8` unrelated local tweaks, `8d09a22` operator-failure banner). The delivery branch is based only on `origin/productionv2`; none of those local commits are present, so `origin/productionv2..HEAD` contains exactly one new commit.

## 2. File and control reconciliation

| Scope | Count |
| --- | --- |
| `ErpWeb.UI` Razor files at local base | 250 |
| `ErpWeb.UI` Razor files at `origin/productionv2` | 249 |
| `ErpWeb` host Razor files | 4 |
| Total Razor files audited | 254 |
| Files containing a search control | 63 |
| Files without a search control | 191 |
| Files rendering an `.erp-inline-search` row after this change | 55 |
| Audited search controls | 64 |
| Converted controls | 56 |
| Documented exceptions | 8 |
| Blocked controls | 0 |

The 250-versus-249 difference is explained by `ErpWeb.UI/Components/Common/OperatorError/ErpOperatorFailureBanner.razor`, introduced by local commit `8d09a22`. It is local-only and is not part of the delivery branch.

Control equation: `64 audited = 56 converted + 8 documented exceptions + 0 blocked`.
File equation: `254 audited = 63 search-bearing + 191 without a search control`.

Full per-control rows: `plans/UI_SEARCH_CONTROL_AUDIT.md`.

## 3. Controls by module

| Module | Converted | Excluded | Blocked | Total |
| --- | --- | --- | --- | --- |
| Admin | 1 (`INLINE_LIVE_FILTER`) | 0 | 0 | 1 |
| Inventory | 16 (`INLINE_SINGLE_TEXT_SEARCH`) | 0 | 0 | 16 |
| Purchase | 11 (`INLINE_SINGLE_TEXT_SEARCH`) | 1 (`TYPEAHEAD_FILTER`) | 0 | 12 |
| Planning | 15 (`INLINE_SINGLE_TEXT_SEARCH`) | 0 | 0 | 15 |
| Sales | 13 (`INLINE_SINGLE_TEXT_SEARCH`) | 7 (6 `TYPEAHEAD_FILTER`, 1 `MULTI_CRITERIA_SINGLE_APPLY`) | 0 | 20 |
| **Total** | **56** | **8** | **0** | **64** |

Documented exceptions, each verified in source:

| Control | Disposition | Reason |
| --- | --- | --- |
| `ErpWeb.UI/Purchase/Inquiry/PoSupplierInquiry.razor:53` | `TYPEAHEAD_FILTER` | Narrows the adjacent supplier combo's option list; the grid is refreshed by the page action. |
| `ErpWeb.UI/Sales/Analysis/SaSalesByCategory.razor:50` | `TYPEAHEAD_FILTER` | Narrows the adjacent customer combo options. |
| `ErpWeb.UI/Sales/Analysis/SaSalesByItem.razor:51` | `TYPEAHEAD_FILTER` | Narrows the adjacent customer combo options. |
| `ErpWeb.UI/Sales/Analysis/SaSalesByWarehouse.razor:49` | `TYPEAHEAD_FILTER` | Narrows the adjacent customer combo options. |
| `ErpWeb.UI/Sales/Analysis/SaSalesSummary.razor:62` | `TYPEAHEAD_FILTER` | Narrows the adjacent customer combo options. |
| `ErpWeb.UI/Sales/Inquiry/SaCustomerInquiry.razor:46` | `TYPEAHEAD_FILTER` | Narrows the adjacent customer combo options. |
| `ErpWeb.UI/Sales/PriceInquiry.razor:32` | `TYPEAHEAD_FILTER` | Narrows the adjacent item combo options. |
| `ErpWeb.UI/Sales/Inquiry/SaPriceChangeHistoryInquiry.razor:132` | `MULTI_CRITERIA_SINGLE_APPLY` | Thirteen criteria share one action; a single icon per criterion is explicitly forbidden by the plan. |

`SmartLookupInput` and `IvCodeComboBox` auto-search surfaces are `LOOKUP_ICON_ALREADY_COMPLIANT` / `TYPEAHEAD_OR_GRID_BUILTIN_EXCLUDED` and are listed with their controls in the audit rather than as conversions.

## 4. Files changed and method-level verdicts

Delivery branch diff against `371725c`: **107 files, 2838 insertions, 574 deletions**.

| Class | Count | Verdict |
| --- | --- | --- |
| `ErpWeb.UI` markup (`*.razor`) | 62 | LAYOUT-ONLY |
| `ErpWeb.UI` scoped styles (`*.razor.css`) | 2 | LAYOUT-ONLY |
| `ErpWeb.UI` code-behind (`*.razor.cs`) | 41 | LAYOUT-ONLY, member list reviewed |
| Shared stylesheet (`ErpWeb/wwwroot/css/inventory-chrome.css`) | 1 | LAYOUT-ONLY |
| Source contract tests (`ErpWeb.Tests/UI/`) | 1 | TEST-ONLY |

By module: Inventory 36, Planning 28, Sales 19, Purchase 18, Admin 2, shared `Components` 2.

Method-level review of every `.razor.cs` member is recorded in `plans/evidence/change-audit.md`. The only members added are search wiring and layout bookkeeping: `ApplySearchAsync`, `ApplyPoPickerSearchAsync`, `ApplySourceSearchAsync`, `CancelPendingSearch`, `_disposed`, and generation/sequence fields already required by the existing debounce pattern. No query object, service call, posting path, price, cost or quantity expression was modified.

Business-logic diff result: **NONE**. No file under `ErpWeb.Core/`, `ErpWeb.Model/`, `ErpWeb.Data/`, no SQL/migration, and no entity or API contract was touched.

Development-to-delivery parity: all 107 delivered files were compared with the development worktree. The only content differences are the 55 lines and banner markup belonging to the local-only `ErpOperatorFailureBanner` feature (`8d09a22`), which is intentionally absent from the delivery base. Search markup, CSS and handler logic are byte-identical after line-ending normalisation.

## 5. Baselines and results

Identical SDK on all worktrees (`dotnet --info` recorded in the isolation notes). Commands were run per phase from the respective worktree root.

### Baseline A - development base `8d09a22` (before edits)

| Command | Result |
| --- | --- |
| `dotnet build ErpWeb.slnx` | PASS, 0 errors |
| `dotnet build ErpWeb.UI/ErpWeb.UI.csproj` | PASS |
| `dotnet build ErpWeb/ErpWeb.csproj` | PASS |
| `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --logger trx` | 4782 total, 4747 passed, 27 failed |

Log: `plans/evidence/baseline-A-test.log`.

### Baseline B - `origin/productionv2` `371725c` (pristine delivery base)

| Command | Result |
| --- | --- |
| `dotnet build ErpWeb.slnx` | PASS, 0 errors |
| `dotnet build ErpWeb.UI/ErpWeb.UI.csproj` | PASS |
| `dotnet build ErpWeb/ErpWeb.csproj` | PASS |
| `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --logger trx` | 4586 total, 4551 passed, 27 failed |

Log: `plans/evidence/baseline-B-test.log`.

### Delivery branch `ui/global-inline-search-standard-v1` (`cb1b65d`)

| Command | Result |
| --- | --- |
| `dotnet build ErpWeb.slnx` | PASS, 0 errors, 113 warnings | 
| `dotnet build ErpWeb.UI/ErpWeb.UI.csproj` | PASS, 0 errors |
| `dotnet build ErpWeb/ErpWeb.csproj` | PASS, 0 errors |
| `dotnet test --filter FullyQualifiedName~ErpInlineSearchContractTests` | PASS 6/6 |
| `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --logger trx` | 4592 total, 4557 passed, 27 failed, 8 skipped |
| `git diff --check` | PASS, no whitespace errors |
| Denylist audit | PASS, zero forbidden paths |

Logs: `plans/evidence/delivery-build.log`, `delivery-build-ui.log`, `delivery-build-host.log`, `delivery-contract-test.log`, `delivery-full-test.log`.

### Attribution (M-04, F-03)

`4592 delivery tests = 4586 Baseline B tests + 6 new inline-search contract tests`.
`4557 delivery passes = 4551 Baseline B passes + 6 new contract passes`.

All 27 failures on the delivery branch are **BASELINE FAILURE**, not `FAIL`:

- The failed-test name set on delivery is **identical** to Baseline B's failed-test name set and to Baseline A's.
- The 27 named failures were replayed against the pristine Baseline B worktree; 27 of 27 reproduced there (`plans/evidence/baseline-b-suspects.log`).
- New failures introduced by this change: **0**.

The 27 pre-existing failures are concentrated in `Sales.Master.SaCustServiceTests`, `Planning.Master.PrBomStructureTreeTests`, `Planning.Master.PrProductDefServiceTests`, `Planning.Master.ProductDefinitionSnapshotLoaderTests`, `Planning.Master.BomExplosionServiceTests`, `Planning.Transaction.ProductionWorkOrderServiceTests`, `Procurement.Inquiry.PoPurchaseInquiryWorkbenchTests` and `Sales.Transaction.SaEInvoiceSelfBillAndTinTests`. They are database/environment-dependent (shared `ERPWeb` SQL Server state), they exist on both untouched baselines, and no UI file in this change is referenced by them.

## 6. Contract tests

`ErpWeb.Tests/UI/ErpInlineSearchContractTests.cs` statically enforces the standard across all shipped Razor files (6 tests, all passing):

1. Shared `.erp-inline-search` CSS contract: `flex-wrap: nowrap` with no overflow clipping, `8rem` editor floor plus `flex: 1 1`, `2.25rem` icon hit target, ellipsis caption, `min-inline-size: 0` on the toolbar-row variant, and a `6rem` floor in the `max-width: 575.98px` block so the magnifier stays on screen.
2. Every rendered `.erp-inline-search` row: caption `label for=` matching the editor `InputId`, `BindValueMode="BindValueMode.OnInput"`, `TextExpression` whenever explicit `Text` + `TextChanged` are combined, exactly one icon-only `fa-solid fa-magnifying-glass` button with an accessible name, and no button at all on `--live` rows.
3. No legacy `iv-toolbar-row__search` text search survives outside the documented allow-list.
4. No visible `SEARCH`/`Search` text button sits inside a search row.
5. `IvCodeComboBox` and `SmartLookupInput` forward `InputId` for label association; `SmartLookupInput` keeps its icon-only magnifier and no text button.
6. The documented allow-list cannot rot: each listed file must still exist, so a deleted or renamed exception fails the suite.

## 7. Browser and visual verification - PARTIAL (42 / 56 PASS, 14 NOT VERIFIED)

`plans/UI_SEARCH_EVIDENCE_MATRIX.md` contains one row per converted control (56 rows) with route, control id, handler pattern, path:line, and five live browser cells per control.

A live interactive session against the **delivery branch itself** was completed (`http://localhost:5001`, `ASPNETCORE_ENVIRONMENT=Development`, authenticated as a `DEMO` tenant administrator):

| Result | Count | Basis |
| --- | --- | --- |
| PASS | 42 | Live DOM geometry and computed style, plus authenticated render sweep |
| NOT VERIFIED | 14 | 1 environment-blocked route + 13 dialogs gated behind absent prerequisite data |

Deep UAT (real typing, icon click issued before the debounce expires, Enter key, empty/clear round-trip, popup footer integrity, `#blazor-error-ui` clean) was performed on `#admin-company-rail-search`, `#iv-stock-master-list-search`, `#po-supplier-search-popup-search` and `#po-invoice-po-picker-search`. Observed results:

- `#iv-stock-master-list-search`: 11 rows → 2 rows for `FG001` on debounce; typing `M0` and clicking the magnifier **before** the debounce expired produced 6 rows, proving the latest-text guarantee; clearing and clicking restored all 11; `Enter` produced 3 rows for `FG`.
- `#po-supplier-search-popup-search`: 3 suppliers → `ZZZZNOPE` via `Enter` gave "No data to display"; clearing and clicking restored all 3; `PREV`/`NEXT`/`SELECT`/`CANCEL` untouched.
- `#admin-company-rail-search` (live, no action button): 2 companies → 1 for `DEMO` → 0 for `ZZZZ` → 2 on clear.
- `#po-invoice-po-picker-search`: opened from "Add PO line", single-line row, `Cancel`/`Add selected` footer intact, both icon and `Enter` paths fire without error on an empty dataset.

Responsive sweep (single-line row, no wrap at any width): 320 px → 147 px input; 375 px → 191 px; 768 px → 497 px; 1280 px → 668 px; 1920 px → 1308 px. Magnifier is 36 px wide and unclipped at every width. The constrained-popup case (500 px viewport → 350 px popup, the 200 % zoom equivalent) keeps a 218 px input and a 36 px magnifier fully inside the popup.

Not verified, with cause:

| Cause | Rows | Detail |
| --- | --- | --- |
| Pre-existing database drift | 1 (`#sa-cdn-list-search`) | `/sales/credit-notes` returns 500: `Invalid column name 'CostingRevision'`. `ErpWeb.Model/Entities/Sales/SaCdn.cs` declares the property and is **byte-identical to `origin/productionv2`**; the `ERPWeb` database only carries the column on `PoCdn` and `POInvoice`. No branch file under `ErpWeb.Model`/`ErpWeb.Core`/`ErpWeb.Data` changed, so this reproduces identically on the untouched baseline. |
| Prerequisite data absent | 13 | Picker/popup triggers are not rendered until a supplier, customer or source document is chosen; the seeded database has no qualifying transaction. |

Dark theme has no discoverable toggle in the running host and is recorded as **NOT VERIFIED**; light theme was verified.

Consequences, per plan policy:

- 42 controls now count as `PASS`; the 14 `NOT VERIFIED` controls do not.
- The responsive matrix (320/375/768/1280/1920 px and the 200 % zoom equivalent) is **PASS** on the representative list, popup and picker surfaces.
- The pull request remains **draft** and must not be merged until a reviewer clears the 14 rows (or accepts the documented environment rationale).

## 8. Deliverable

| Item | Value |
| --- | --- |
| Branch | `ui/global-inline-search-standard-v1` (pushed to `origin`) |
| Base target | `productionv2` at `371725c0bda74e386a66bfa3c7559c6f227a14dd` |
| Commits over base | UI commit `cb1b65d` + evidence commit `5c796b0` |
| PR state | Must be opened as **draft** against `productionv2` (not PR #2); this environment has no GitHub CLI or usable API token, so the branch was pushed and the PR body prepared in `plans/UI_SEARCH_PR_DESCRIPTION.md` |
| Supersedes | PR #2 (`ui/standard-inline-search-20261029`) |

## 9. Residual risks

1. 14 browser rows remain unverified: 1 route is blocked by pre-existing database drift (`SaCdn.CostingRevision`), and 13 pickers/popups cannot be opened against the seeded dataset because their triggers require a prerequisite supplier, customer or source document. This is the only reason the verdict is not a full PASS.
2. Dark theme was not exercised (no discoverable toggle in the running host); light theme passed.
3. The delivery branch cannot carry the local-only operator-failure banner; screens that display it in the development worktree will differ until `8d09a22` reaches `productionv2`.
4. The 27 pre-existing test failures stay red; they are unrelated but will keep the suite red for any reviewer running it.
5. Large TRX binaries (Baseline A, Baseline B, delivery) and multi-megabyte build logs are retained on the build machine under `plans/evidence/` but are not committed, to keep the PR reviewable; the committed text logs contain the same totals and failure lists.
