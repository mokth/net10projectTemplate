# Implementation report — ERP-friendly stock / costing / quantity errors

**Phase:** 7 — final release package / handoff
**Repository:** `mokth/net10projectTemplate`
**Branch:** `productionv2`
**Base SHA:** `371725c` ("checked in")
**Result SHA:** the current `productionv2` HEAD (see `git log --oneline -3`) — the release is three commits:

| Commit | Scope |
|---|---|
| `0afe632` | `feat(replay):` backdated production/standard effective-cost replay — **the other stream** |
| `0d1dde8` | `chore(ui):` unrelated local tweaks (grid min-width, `LifecycleStatus` `ValueExpression`) |
| `HEAD` | `feat(errors):` operator-facing stock/costing failure presentation — **this stream** |

Each commit was built in an isolated worktree: **0 errors** in all three. The final tree is byte-identical
to the pre-split working tree (`git diff split-backup HEAD` → empty).

**Gate:** PASS for code + unit/integration evidence; **BLOCKED for release** only on the five evidence
gaps in §6 (SQL Server suites, DB row snapshots, UAT, log round-trip, role matrix).

---

## 1. What changed, in one paragraph

Every stock, costing and quantity refusal that an operator can reach from an Inventory, Procurement,
Sales or Production posting screen is now classified from a **structured** value (the persisted
`StockLedgerErrorCode`, the attempted action, the replay phase, a producer-supplied family) and rendered
as a banner: *what failed*, *why*, *the affected document*, *the next business action*, optional
collapsed facts, and a support reference. A missing structured value falls back to the screen's existing
message. No decision, quantity, cost, status, lock or transaction boundary was modified.

---

## 2. ⚠ Attribution: two change streams share this working tree

## 2. Attribution: the tree has been split into three commits

The working tree originally mixed two efforts. It is now:

| Commit | Content | Nature |
|---|---|---|
| `0afe632` `feat(replay)` | WAC/Standard production-tail replay planning, `ProductionReplayEffectiveCostReader`, `ProductionOutputIssueCostCorrectionWriter`, `ProductionPostingInvariant`, the effective-cost writes in `ProductionOutputService.Rollback`, `ProductionIssueEffectiveCostReader` guards, replay plans | **Changes costing behaviour** — the other stream |
| `0d1dde8` `chore(ui)` | `CommonDataGridEx` min-width, `IvStockMasterEntry` `ValueExpression` | Unrelated local tweaks; the grid min-width looks like a workaround, review before merge |
| `HEAD` `feat(errors)` | Everything in §2.2 below | This change stream — presentation only |

`ProductionOutputService.Rollback.cs` was the only genuinely mixed file and was split at hunk level:
`0afe632` carries only the effective-cost hunks, `HEAD` only the `ProductionOperatorErrorLog` catches,
the logger parameter and the `documentNo` plumbing. All three commits build in isolation (0 errors), and
`git diff split-backup HEAD` is empty, so nothing was lost.

### 2.1 Files in the replay commit that change costing (do **not** describe as error-message work)

| File | Why it is not ours |
|---|---|
| `ErpWeb.Core/Production/ProductionPostingInvariant.cs` | **Behaviour change:** compares reversal cost against `ProductionReplayEffectiveCostReader` effective cost instead of the source row |
| `ErpWeb.Core/Inventory/IvInventoryPostingService.WacReplay.cs`, `.StandardMrReplay.cs`, `.WacProductionReplay.cs`, `.StandardTransferReplay.cs`, `.StandardProductionReplay.cs` (new) | Adds backdated replay + `productionPlan` plumbing |
| `ErpWeb.Core/Production/ProductionOutputService.Rollback.cs` (cost hunks only) | Reversal writes `effective.TotalCost`/`UnitCost` from `ProductionReplayEffectiveCostReader` into the pool, consumption evidence and produced output instead of the original movement's values, plus new reconciliation guards |
| `ErpWeb.Core/Production/ProductionIssueEffectiveCostReader.cs`, `ProductionReplayEffectiveCostReader.cs` (new), `ProductionOutputIssueCostCorrectionWriter.cs` (new) | Replay cost correction feature; the reader also gains stricter `WorkOrderId` / `WorkOrderOperationId` / `PostingLinkId` guards |
| `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs` | Column min-width edit (own commit) |
| `plans/productionv2_backdated_replay_impact_manifest.md`, `plans/productionv2_replay_architecture_assessment.md` | Same unrelated effort |

### 2.2 This change stream

| Area | Files | Nature of diff |
|---|---|---|
| `ErpWeb.Core/Services` (new) | `ErpOperatorError.cs`, `ErpOperatorErrorPresenter.cs`, `ErpOperatorErrors.cs` | Pure presentation contract + classifier. No `Db`, no clock, no RNG |
| `ErpWeb.Core/Inventory` | 16 | Optional `OperatorError` on results (`IvMasterResults`, `IIvStockCountService`), readonly enrichment (`IvFailureEvidenceReader`), catch-block metadata (`IvInventoryPostingService`, `IvStockCountService`), message text (`IvStockDateRules`, `IvStockMovementRules`) |
| `ErpWeb.Core/StockLedger` | `StockLedgerErrors.cs` | Optional `Phase`/`EventId` metadata on the existing record. Nothing reads it for decisions |
| `ErpWeb.Core/StockLedger` | `StockReplayPhysicalSimulator.cs` | Attaches `Phase`/`EventId` at the **existing** negative-balance throw. Same condition, same code, same message |
| `ErpWeb.Core/Sales`, `/Purchase` | 6 + 4 | Catch-only: ledger refusal → `ErpOperatorErrors.FromLedgerError` with logger + reference |
| `ErpWeb.Core/Production` | 13 (in this commit) | Catch-only + optional trailing `ILogger<T>?`; new `ProductionOperatorErrorLog.cs`, `ProductionPostingFailure.cs`. `ProductionOutputService.Rollback.cs` appears here with **only** its catch/`documentNo` hunks — its effective-cost writes live in `0afe632` |
| `ErpWeb.UI` | 21 Inventory + 22 Planning + 6 Sales + 4 Purchase + 2 Components + `_Imports.razor` | Bind `<ErpOperatorFailureBanner>`; replace rethrow-with-`ex.Message` on post/rollback paths |
| `ErpWeb.Tests` | 12 | Presenter (66), inventory operator errors, failure-evidence reader, banner view, cross-module (19) |
| `plans` | 10 | This package + phase reports |

New public surface (all additive):
`ErpOperatorError`, `ErpOperatorErrorDetail`, `ErpRelatedDocumentHint`, `ErpErrorFamily`,
`ErpErrorAction`, `ErpErrorOrigin`, `ErpDocumentKind`, `ErpRejectionPhase`, `ErpOperatorErrorPresenter`,
`ErpOperatorErrors`, `ErpOperatorFailureView`, `ErpOperatorFailureBanner`; optional members
`OperatorError` on result records, optional `operatorError` parameter on result factories, optional
`ILogger<T>?` on Production services.

---

## 3. Per-phase test ledger

| Phase | Command | Result |
|---|---|---|
| 1 — presenter | `dotnet test --filter FullyQualifiedName~ErpOperatorErrorPresenterTests` | **66 passed**; inventory-side view tests **31 passed** |
| 2 — structured propagation + GRN fix | `dotnet test --filter "…GRN\|…Posting\|StockReplay\|StockLedgerInfrastructure\|…OperatorError"` | 0 errors on build; full suite **NOT RUN** at that time (pre-existing `Planning.Master` hang recorded in Phase 0) |
| 3 — affected-document explanation | `dotnet test --filter Category=Inventory` | **1182 passed, 0 failed** (4 m 01 s) |
| 4 — remaining inventory UI | `dotnet test --filter Category=Inventory` | **1192 passed, 0 failed** (5 m 29 s); solution build 0 errors |
| 5 — cross-module | `dotnet test --filter FullyQualifiedName~PrCrossModuleOperatorErrorTests` | **19 passed**; full suite **4747 passed / 27 failed / 8 skipped** |
| 6 — safety verification | `dotnet build ErpWeb.slnx -c Release` | **0 errors, 115 warnings** |
| 6 — safety verification | `dotnet test ErpWeb.Tests.csproj -c Release --no-build` | **4747 passed, 27 failed, 8 skipped, 4782 total**, 16 m 29 s |
| 6 — scoped regression | `dotnet test --filter "…InventoryPosting\|…GoodsReceipt\|…StockCount\|…Production\|…OperatorError"` | **1075 passed, 1 failed** (pre-existing phantom-BOM), 8 skipped |
| 6 — baseline comparison | clean `git worktree` at `371725c`, same nine classes | **27 / 27 identical**; `Compare-Object baseline current` produced **zero rows**; both sets counted 27 |
| 6 — reproducibility | three independent full-suite runs (18 m 23 s / 15 m 31 s / 16 m 29 s) | **27 failed / 4747 passed / 8 skipped / 4782 total** in all three |
| 6 — SQL Server | `dotnet test --filter "…SqlServer"` | **8 skipped** — `ERPWEB_PRODUCTION_STOCK_TEST_CONNECTION` not set |

Latest full-suite failures: 27, of which **0 new**. Full names in
`stock_cost_error_ux_phase6_report.md` §8.

---

## 4. Security and permission proof

| Concern | Control | Proof |
|---|---|---|
| Cost amounts without `ViewCost` | `ErpOperatorErrorPresenter.BuildDetails` drops any detail flagged `IsMonetary` when `!context.CanViewCost`; a missing cost is never rendered as `0` | `AssertNoForbiddenVocabulary` + detail-suppression tests in `ErpOperatorErrorPresenterTests` / `IvOperatorFailureViewTests`; role UAT **NOT RUN** |
| Tenant isolation | No tenant/company/branch identifier is ever formatted into text or a route; `InvalidStockIdentity` wording is generic | Presenter has no tenant parameter; forbidden-vocabulary regex includes `tenant id` |
| Cross-module document leakage | Related documents require both a type and a number from the producer; nothing is invented; a Sales user is never handed a Purchase document | `BuildRelatedDocuments` (max 5, `Safe`-sanitised) |
| Cross-branch history | Affected-document detail only appears when the producer supplied it from its own scoped query | `BuildDetails` / `AffectedDocumentText` |
| Raw exception text | UI-wide scan for `StackTrace` / `ex.ToString()` / `Exception.ToString(` → **0 matches** | Phase 6 §2 |
| Log vs screen separation | Screen: business text + reference. Log: original exception, code, phase, retryable, same reference | Inventory (Phase 2/6), Sales/Purchase (Phase 5), Production (Phase 6) |
| Information in reference | 10 hex chars of a GUID; carries no branch, user, item or amount | `ErpOperatorErrors.NewReferenceId()` |
| New writes / queries | None on the success path; failure path adds one `LogError`/`LogWarning` and (inventory) the already-capped evidence read | Diff inspection |

---

## 5. Operator-visible outcome (textual UAT evidence)

Presenter-level output (verified literals from the unit suites, **not** screenshots):

| Scenario | Before (raw producer text) | After (banner) |
|---|---|---|
| GRN rollback, later movement exists | `Physical replay would make the stock slice negative at event 2 while removing.` | **Cannot roll back Goods Receipt IV-GR-000123.** / Removing this record would make the stock balance at its original date fall below zero. / *Review the stock history for that date before retrying.* / Reference `A1B2C3D4E5` |
| Issue exceeds stock | `INSUFFICIENT_BASE_QTY` or a bare quantity message | **Cannot post this issue.** / There is not enough available stock for `ITEM-01`. / *Select another eligible stock location or reduce the quantity, then try again.* / Available `10`, Required `12`, Shortage `2` `PCS` |
| Standard cost missing | `No standard revision for 2026-10-01` | **Cannot post this goods receipt.** / No valid costing method is configured for `01/10/2026`. / *Ask the costing administrator to check the costing settings for that date.* |
| Closed period | `Period closed` | **Cannot post this delivery order.** / A transaction dated `28/10/2026` falls in a closed inventory period (closed through `31/10/2026`). / *Ask an authorised period administrator to review the date.* |
| Production material issue shortage | `Not enough stock` | **Cannot post Daily Production DP-0007.** / There is not enough eligible stock to issue `ITEM-01` to Production. / *Select another eligible stock location or reduce the quantity, then try again.* |
| Batch with one failure | `1 of 2 failed` plus raw text | **1 of the 2 selected records could not be completed.** / expandable per-record detail, each with the same business wording |
| Unknown failure | Exception message / stack | **Cannot post this record.** / The action could not be completed. / *Refresh and check the document status. If the problem continues, give the reference to your administrator.* / Reference `F0E1D2C3B4` |

**Screen-level UAT: NOT RUN.** No application instance, database or tester was available. The 10-step
warehouse/purchasing script and the workspace setup are in `stock_cost_error_ux_phase6_report.md` §6–§7.
Screenshots must not be fabricated; capture them when the script runs.

---

## 6. Known issues and open evidence gaps

| # | Item | Severity | Action |
|---|---|---|---|
| K-1 | ~~Costing replay changes share the working tree~~ | **Resolved** | Split into `0afe632` (replay/costing) + `0d1dde8` (unrelated tweaks) + `HEAD` (this stream). Each builds standalone |
| K-2 | 8 SQL Server tests skipped; DB row parity unproven | High | Set `ERPWEB_PRODUCTION_STOCK_TEST_CONNECTION`, run §6.1, then the snapshot queries in §6.2 |
| K-3 | UAT not executed; no screenshots | High | Run §7 with warehouse + purchasing staff |
| K-4 | Runtime log-reference round trip unproven | Medium | §6.3 procedure |
| K-5 | Role/permission matrix unproven beyond unit tests | Medium | §6.4 matrix |
| K-6 | 27 pre-existing failures remain (BOM, customer master, purchase inquiry, self-billed UBL) | Medium | Separate work stream; not fixed here |
| K-7 | Production entry pages still show `ex.Message` for save/delete/lookup catches | Low | Registered out of scope; can never show a stack trace |
| K-8 | `IvStockDateRules`/`IvStockMovementRules` message signatures lost their id parameters | Low | Internal-only; all callers updated, 0 errors |
| K-9 | Locale keys are reserved, not wired (no localization provider in the solution) | Low | Add a resource provider when translation is needed; presenter resolves key → text |

---

## 7. Release checklist

1. ~~Split the tree~~ — **done**: `0afe632` (replay/costing) → `0d1dde8` (unrelated tweaks) →
   `HEAD` (this stream). Each builds standalone with 0 errors.
2. SHAs: base `371725c` → current HEAD (`git log --oneline -3` lists all three). The `split-backup` branch holds the pre-split
   snapshot; delete it once you are satisfied (`git branch -D split-backup`).
3. Run the four SQL Server suites and paste results into the parity matrix (replacing **NOT RUN**).
4. Run the UAT script; attach screenshots to `plans/stock_cost_error_ux_phase6_report.md` §7.
5. Confirm `git diff 371725c..HEAD --name-only` lists no file from the parity matrix §1
   "must not change" set (`StockReplayPhysicalPlanner`, `TimelineReader`, `InventoryValuationService`,
   `StockReplayFifo*`, `StockPostingCoordinator`, `StockPostingGuards`). It should be clean; replay
   files only appear in `0afe632`.
6. Ship the four documents with the release: error-site register, implementation report, message
   catalogue, parity matrix.

---

## 8. Deliverables in this package

| File | Content |
|---|---|
| `plans/stock_cost_error_ux_error_site_register.md` | Every inspected site + final disposition (§14) |
| `plans/stock_cost_error_ux_implementation_report.md` | This file |
| `plans/stock_cost_error_ux_operator_message_catalogue.md` | 21 codes, 19 families, verbs, fallback priority, locale keys, link policy |
| `plans/stock_cost_error_ux_business_parity_matrix.md` | PASS / FAIL / NOT RUN per invariant |
| `plans/stock_cost_error_ux_unrelated_defects.md` | Pre-existing defects found but deliberately not fixed |
| `plans/stock_cost_error_ux_phase1..6_report.md` | Phase evidence trail |
| `plans/stock_cost_error_ux_baseline_report.md`, `…_ledger_producer_appendix.csv` | Phase 0 census |
