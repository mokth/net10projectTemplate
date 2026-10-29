# Phase 4 report — Remaining Inventory / StockLedger / Costing UI

**Date:** 2026-10-29
**Repo:** `mokth/net10projectTemplate` · **Branch:** `productionv2` · **Baseline SHA:** `371725c`
**Scope executed:** PHASE 4 only.
**Gate status:** COMPLETE — waiting for approval before Phase 5.

---

## 1. Scope compliance

| Constraint | Result |
|---|---|
| No costing/quantity algorithm change | ✅ No calculator, simulator, replay coordinator, valuation or coordinator file touched by this phase |
| No posting/rollback rule change | ✅ Every posting decision, guard, `if`, rounding and status assignment is byte-identical |
| No DB/schema/migration change | ✅ No entity, mapping, migration or SQL touched |
| No new page / no new flow | ✅ Existing pages only; no new route, modal or menu |
| Existing field/line validation preserved | ✅ Every `ErrorMessage = "..."` validation string is untouched; only the *post/rollback result* branch gained a banner |
| Legacy raw message still available | ✅ `ErrorMessage` on every result is unchanged; pages still fall back to it verbatim when no structured error exists |
| No monetary value exposed | ✅ The inventory mapping never supplies a monetary fact; the view model also filters `IsMonetary` defensively |
| No internal DB id on screen | ✅ `IvStockDateRules` / `IvStockMovementRules` now log the id and print business text only |

---

## 2. Files changed (this phase only)

### New UI

| File | Purpose |
|---|---|
| `ErpWeb.UI/Components/Inventory/IvOperatorFailureView.cs` | Presentation-only mapping from `IvInventoryPostingResult` / `ErpOperatorError` to headline + distinct reasons + per-record notes + ≤6 facts |
| `ErpWeb.UI/Components/Inventory/IvOperatorFailureBanner.razor` | One shared banner reusing the existing `iv-toast` chrome; collapsed `<details>` per record |

### Core (message text and structured carry only)

| File | Change |
|---|---|
| `ErpWeb.Core/Inventory/IIvStockCountService.cs` | `IvStockCountOperationResult` gains optional `ErpOperatorError? OperatorError`; `Fail(message, operatorError)` overload |
| `ErpWeb.Core/Inventory/IvStockCountService.cs` | Five existing `catch (StockLedgerException)` blocks keep `Fail(ex.Error.Message)` and add the operator error + a warning log with reference |
| `ErpWeb.Core/Inventory/IvInventoryOperatorErrors.cs` | `FromLedgerError(error, documentKind, documentNumber, action, reference, evidence)` overload for documents without a batch number |
| `ErpWeb.Core/Inventory/IvStockDateRules.cs` | `NullStockDateMessage()` no longer prints `balLocId` |
| `ErpWeb.Core/Inventory/IvStockMovementRules.cs` | `RollbackLaterMovementMessage` no longer prints `balLocId` |
| `ErpWeb.Core/Inventory/IvInventoryPostingService.Chronology.cs` | Logs the discarded balance id at `Warning` before returning the operator text |

### Pages (8 ledger lists + 2 stock-count screens)

`IvGoodsReceiptList`, `IvMiscIssueList`, `IvMiscReceiptList`, `IvScrapList`, `IvStockAdjustmentList`,
`IvStockReturnList`, `IvStockTransferList`, `IvVendorReturnList`, `IvStockCountList`, `IvStockCount`
— each `.razor` renders `IvOperatorFailureBanner` when a structured failure exists, otherwise the
pre-existing error toast; each `.razor.cs` sets `OperatorFailure = IvOperatorFailureView.From(result.Posting)`
(or `FromError(result.OperatorError)` for stock count) and clears it on dismiss/retry.

### Shared chrome

| File | Change |
|---|---|
| `ErpWeb.UI/Components/_Imports.razor` | `@using ErpWeb.UI.Components.Inventory` |
| `ErpWeb/wwwroot/css/inventory-chrome.css` | Banner-only rules added to the global chrome so a *shared* component can be styled (scoped page CSS cannot reach it) |
| `ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor.css` | The now-dead scoped duplicate of those rules removed — one source of truth |

### Tests

| File | Change |
|---|---|
| `ErpWeb.Tests/Inventory/Transaction/IvOperatorFailureViewTests.cs` | **new**: 6 cases |
| `ErpWeb.Tests/Inventory/Transaction/IvStockChronologyRulesTests.cs` | asserts the two cleaned messages carry no numeric id |
| `ErpWeb.Tests/Inventory/Transaction/IvStockCountServiceTests.cs` | plain refusal carries **no** operator error; structured carry keeps the original message |

Nothing in `StockReplayPhysicalSimulator`, `ProductionIssueEffectiveCostReader`, `StockLedgerErrors`, the
sales/procurement/production UI, or any migration file was modified by this phase.

---

## 3. What the operator sees

**Single coded failure** (headline + reason):

> Cannot roll back this Scrap document.
> Removing this record would cause insufficient historical stock when a later transaction is applied.

Expanding *“What happened for each record”* shows the record, its own explanation, the next action,
proven facts (Item / Warehouse / Location / Lot / Affected transaction / date) from Phase 3 evidence,
and `Support reference: 3F9C1A77B2`.

**Mixed batch result** (partial success):

> 2 of the 3 selected records could not be completed.

…followed by one collapsed record per failure. A record that failed *without* a ledger code keeps its
own existing text inside the banner, so nothing is silently hidden.

**Uncoded failure only** (concurrency, unique violation, plain guard, field validation): the page
shows exactly the same toast it showed before this project. This is the Phase 2 decision, re-asserted
here rather than reversed.

---

## 4. Register coverage (Phase 0 → Phase 4 allocation)

| Register ID | Surface | Disposition |
|---|---|---|
| B-01 | `IvGoodsReceiptList` | structured banner (Phase 2, re-pointed at the shared component) |
| B-02 … B-08 | MI, MR, Scrap, ADJ, Stock Return, Transfer, Vendor Return lists | structured banner (`From(result.Posting)`) |
| B-09 | `IvStockCountList` | structured banner for post / rollback / cancel / delete |
| B-10 | `IvStockCount` | structured banner for generate, candidate load, **preview post**, post, rollback |
| B-11 | `IvGoodsReceipt` entry | not touched (save/validation only, per register) |
| B-12 | MI/MR/Scrap/VR/TR/ADJ entry pages | not touched (field validation preserved) |
| D-11 `INVALID_UOM_CONVERSION` | all lists | presenter code mapping |
| D-14 `LEDGER_MISMATCH` | all lists | presenter code mapping (never “insufficient stock”) |
| D-17 `VALUATION_REQUIRED` | all lists | presenter code mapping |
| D-19 `COST_POLICY_MISSING` | all lists | presenter code mapping |
| D-20 `COST_POLICY_INVALID` | all lists | presenter code mapping |
| Stock-date / lot leak (§6.1, finding 6) | posting chronology | id removed from text, logged instead |
| Future document date | `IvStockMovementRules` | already operator text, `dd/MM/yyyy`, no id — left as is |
| Closed period (`CLOSED_PERIOD`) | all lists | mapped family; the raw message (which still carries the closed-through date) remains in `ErrorMessage` |
| Inventory inquiry / Costing Center | read-only pages | unchanged by design: they are the *destination* the messages point at, not a transaction surface |

**Residual technical text, reported, not fixed here:** uncoded batch-level integrity strings in
`IvInventoryPostingService` (e.g. `"Balance Id {n} was not found for this company/branch."`) still
travel through `ErrorMessage` exactly as at baseline. They are **not** ledger rejections, so no
structured error exists to present, and converting them would mean either parsing free text (an
explicit anti-pattern) or attaching a code at dozens of internal `Fail(...)` sites — both outside a
message-only phase. Logged here for the Phase 6 census. Frequency in normal operation is zero; each
requires a balance row to vanish or change identity inside the stock lock.

---

## 5. Money and permission policy

* `ErpOperatorErrorPresenter` only emits a monetary fact when the caller supplies one; **no inventory
  caller supplies any**, so no cost, value, unit price or margin can reach these screens.
* `IvOperatorFailureView` additionally drops `IsMonetary` facts unconditionally — belt and braces if a
  future caller adds one without a cost check.
* Facts come from the Phase 3 reader, which is tenant-scoped and returns `null` on anything unproven, so
  a fact can never name another company's / branch's document, and no link is rendered.
* No `ViewCost` check is needed on this screen because there is nothing to hide; the masked-by-default
  policy is enforced one layer earlier.

---

## 6. Tests

### New `IvOperatorFailureViewTests` (6 cases)

Single coded failure → headline and reason; monetary details never rendered; mixed results list plain
failures instead of hiding them; no structured error → `null` so the page keeps its own message; the
single-error overload matches the batch shape; facts capped at six.

The mixed case initially asserted `Reason == null`; the implementation correctly promotes the single
distinct explanation (from the one plain failure) to the headline reason, so the assertion now pins that
operator-friendly behaviour instead of suppressing it.

### `IvStockChronologyRulesTests`

Both cleaned messages contain no `Id 12` / balance number; the business wording and the
“roll back the later transaction first” next step remain.

### `IvStockCountServiceTests`

* `Post_refuses_a_count_that_became_too_old_between_save_and_post` now also asserts
  `OperatorError is null` — a plain guard refusal must not gain invented structured text.
* New `Structured_failure_keeps_the_original_message_and_adds_operator_text` — `Fail(msg, operatorError)`
  preserves the original `ErrorMessage` verbatim, carries the operator explanation, hides the ISO date
  from operator text, and keeps the support reference.

### Results (commands actually run)

```
dotnet build ErpWeb.UI/ErpWeb.UI.csproj                     → 0 errors
dotnet build ErpWeb.slnx                                    → 0 errors, 105 warnings (incremental; pre-existing nullable/xUnit-analyzer/CS0108 noise)
focused: IvOperatorFailureView + IvInventoryOperatorErrors
         + IvStockDateRules + IvStockMovementRules           → Passed 43, Failed 0
focused: IvStockCountService + IvOperatorFailureView
         + IvStockChronologyRules                           → Passed 45, Failed 0
dotnet test --filter "Category=Inventory"                    → Passed 1192, Failed 0, Skipped 0 (5 m 29 s)
```

Phase 3 baseline for the same filter was 1182 passing; the delta is the tests added since.

---

## 7. Concurrent working-tree state — READ THIS BEFORE REVIEWING THE DIFF

Another workstream is **actively editing this same working tree** while this phase ran. Observed
evidence:

* new untracked snapshot directory `.replay-standard-consumed-7f291a/`;
* an untracked `ErpWeb.Core/Inventory/IvInventoryPostingService.StandardProductionReplay.cs` plus
  in-flight diffs in `IvInventoryPostingService.StandardMrReplay.cs`,
  `.StandardTransferReplay.cs`, `.WacProductionReplay.cs`
  (`StandardProductionReplayPlan`, `ApplyStandardProductionAsync`, `PlanStandardProductionLeafAsync`),
  and modified `plans/productionv2_backdated_replay_impact_manifest.md` /
  `plans/productionv2_replay_architecture_assessment.md`;
* one transient `CS0246: StandardProductionReplayPlan could not be found` while that file was being
  written mid-build; the very next build of `ErpWeb.Core` succeeded with 0 errors.

**None of those files were touched by this phase.** The Phase 4 changes and the concurrent replay work
are disjoint file sets. However, the 1 192-passing Inventory run above was executed **on top of** that
in-flight code, which is why the exact numbers cannot be attributed to Phase 4 alone. Nothing was
committed, so no concurrent work was rewritten or lost.

---

## 8. Not verified (honest status)

| Item | Status | Why |
|---|---|---|
| Banner visually, in a browser | **NOT RUN** | The running `ErpWeb` process holds old binaries; no UI assertion was inferred from a screenshot |
| Stock-count ledger failure end-to-end through `IvStockCountService` | **NOT RUN** | All five stock-count catches are only reachable with the V2 replay fixture (`_openPeriodBackdatedReplayEnabled && _coordinator != null`); producing a forced `StockLedgerException` there is a replay-test build, not a message test. Covered by: identical catch shape to the GRN/MI path, plus contract tests for `Fail(msg, operatorError)` and the view model. Repro script: run `Public_backdated_WAC_count_posts_rolls_back_and_reposts`, flip the active `StockCostPolicyRevision.CostMethod` to FIFO after save and before `svc.PostAsync` |
| `COUNT_SCOPE_FROZEN`, `COUNT_SNAPSHOT_CHANGED` | **N/A** | Never produced anywhere in the repository (Phase 0 finding 2); `IStockFreezeGuard` is a permanent no-op. Implementing them would be a feature, not a message |
| Visual check with `ViewCost` denied | **NOT RUN (UI)** | No monetary fact is produced for these screens at all; the model-level guarantee is unit-tested |
| SQL Server behaviour | **NOT RUN** | No query was added by this phase; the only DB work is the Phase 3 reader, unchanged here |
| Full `dotnet test` across all categories | **NOT RUN** | Pre-existing `Planning.Master` / `ProductDefinitionSnapshotLoaderTests` hang recorded in Phase 0; plus the concurrent replay work above makes a whole-suite number meaningless right now |

---

## 9. Deliberate decisions

1. **Coded failures get the banner; uncoded failures keep their text.** Reversed here would mean
   code-mapping or text-parsing inside the UI, both explicit anti-patterns. The Phase 2 decision stands.
2. **A plain sibling inside a mixed result is shown, not dropped.** Omitting it would let the banner
   claim “1 of 3 failed” while two records actually failed.
3. **The reason line appears when all failures agree.** One explanation repeated under six records is
   noise; several distinct explanations stay per record only.
4. **One shared component, one global style block.** Ten pages with ten copies of the markup, or scoped
   CSS that cannot reach a child component, would both drift; the shared chrome is the only place a
   shared component can be styled.
5. **Stock-count preview maps the operator error too.** Otherwise the friendliest-looking step of the
   flow (the preview that refuses the post) would be the one screen still showing raw ledger text.
6. **The balance id goes to the log, not the operator.** Logged at `Warning` with the batch/reference so
   support can still find the row.

---

## 10. Next phase (not started)

**Phase 5 — cross-module stock / cost errors:** Sales DO and direct Invoice shipment, Purchase
Invoice/CN/DN and GRN costing dependencies, Production Material Issue, Daily Production, FG Receipt,
Production Stock Return / Adjustment, Work Order Transfer — register items A-05…A-16, C-01…C-16,
E-04…E-13, including the two UI `rethrow`/`catch-all ex.Message` anti-patterns (C-13, C-14).

**STOP.** Awaiting approval to start Phase 5.
