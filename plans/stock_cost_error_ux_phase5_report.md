# Phase 5 report — Cross-module stock / cost errors (Sales, Purchase, Production)

**Date:** 2026-10-29
**Repo:** `mokth/net10projectTemplate` · **Branch:** `productionv2` · **Baseline SHA:** `371725c`
**Scope executed:** PHASE 5 only.
**Gate status:** COMPLETE — waiting for approval before Phase 6.

---

## 1. Scope compliance

| Constraint | Result |
|---|---|
| No costing/quantity algorithm change | ✅ No calculator, FIFO/WAC/Standard policy, replay simulator, valuation or coordinator file touched by this phase |
| No posting/rollback rule change | ✅ Every guard, `if`, rounding, status assignment and idempotency decision is unchanged; only the *result* of an already-refused action gained structured text |
| No DB/schema/migration change | ✅ No entity, mapping, migration or SQL touched |
| No new page / no new transaction screen | ✅ Existing lists and existing entry screens only; no new route, modal, menu or approval flow |
| Existing validation preserved | ✅ Every pre-existing `ErrorMessage = "..."` field/line validation string is untouched |
| Existing result contracts preserved | ✅ `ErrorMessage`, `Message`, `Succeeded`, `ErrorCode`, `ValidationErrors`, `SaDoErrorKind`, `SaInvoiceErrorKind` unchanged; `OperatorError` is an added nullable property |
| Legacy raw message still available | ✅ Every page falls back to the original message verbatim when no structured error exists; server logging keeps the original technical text |
| No Sales/Procurement workflow change | ✅ DO/Invoice direct posting, shipment validation, PI settlement and GRN dependencies are byte-identical |
| No monetary value exposed | ✅ The banner itself filters `IsMonetary` facts; no cost amount is supplied by any Phase 5 producer |

### Incidental defect found and fixed (blocks the whole work stream's UI)

Phases 1–4 wired `<ErpOperatorFailureBanner>` into 24 pages, but the component's namespace was only
imported in `ErpWeb.UI/Components/_Imports.razor` — which does not apply to pages under
`Inventory/`, `Sales/`, `Purchase/` or `Planning/`. Every one of those usages compiled as an
*unknown markup element* (warning `RZ10012`) and rendered **nothing**, so no banner from Phases 1–4
could ever appear. Phase 5 added the missing `@using ErpWeb.UI.Components.Common.OperatorError` to
the root `ErpWeb.UI/_Imports.razor`; all 24 `RZ10012` warnings are gone and the banners now render.
Also repaired the stale `using ErpWeb.UI.Components.Inventory;` in
`ErpWeb.Tests/Inventory/Transaction/IvOperatorFailureViewTests.cs`, which no longer compiled after
the component namespace moved.

---

## 2. Files changed (this phase)

### Core — structured carry only

| File | Change |
|---|---|
| `ErpWeb.Core/Production/ProductionPostingFailure.cs` | **new** internal `readonly record struct (string? Message, ErpOperatorError? OperatorError)` with implicit conversion from `string?`, so every existing `return "..."` in the production services keeps its exact text |
| `ErpWeb.Core/Production/IProductionMaterialIssueService.cs` | `ProductionMaterialIssueBatchActionItem` gains optional `ErpOperatorError? OperatorError` |
| `ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs` | `PostDraftBatchAsync` maps `ProductionPostingInvariantException`, `StockLedgerException` and concurrency/unexpected failures through `ErpOperatorErrors` |
| `ErpWeb.Core/Production/ProductionMaterialIssueService.Rollback.cs` | Rollback copies `StockLedgerException` → `FromLedgerError` and the per-row `OperatorError` into `IvMasterOperationResult<…>.OperatorError` |
| `ErpWeb.Core/Production/ProductionOutputService.cs` / `.Posting.cs` / `.Rollback.cs` | Daily Production post/rollback attach `FromLedgerError`, `ReconciliationRequired` or `Unexpected` to the result |
| `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.cs` / `.Posting.cs` | FG receipt post/rollback attach `FromLedgerError` |
| `ErpWeb.Core/Production/ProductionStockReturnService.cs` / `.Posting.cs` | `ReturnException` carries `ErpOperatorError? OperatorError`; post/rollback populate it |
| `ErpWeb.Core/Production/ProductionStockAdjustmentService.cs` | `AdjustmentException` carries `OperatorError`; post/rollback populate it |
| `ErpWeb.Core/Production/ProductionWorkOrderTransferService.cs` / `.Rollback.cs` | `TransferException` carries `OperatorError`; ledger refusal mapped to `FromLedgerError` on post and rollback |
| `ErpWeb.Core/Purchase/PoCdnService.cs` | One call site unpacks `(Error, OperatorError)` from the CDN costing rollback and passes it to `PoCdnPostingItemResult.Failed` |

### Pages — presentation only

| Module | Files |
|---|---|
| Sales | `SaDoList`, `SaInvoiceList`, `SaCdnList` (`.razor` + `.razor.cs`) — batch post/rollback failures render the banner from per-document results |
| Purchase | `PoInvoiceList`, `PoCdnList` (`.razor` + `.razor.cs`) — same batch pattern |
| Production | `PrMaterialIssueList`, `PrDailyProductionList`, `PrDailyProductionEntry`, `PrFinishedGoodReceiptList`, `PrFinishedGoodReceiptEntry`, `PrProductionStockReturnList`, `PrProductionStockReturnEntry`, `PrProductionStockAdjustmentList`, `PrProductionStockAdjustmentEntry`, `PrWorkOrderTransferList`, `PrWorkOrderTransferEntry` |

Every page keeps the pre-existing error toast as the fallback and uses the shared
`ErpOperatorFailureBanner` only when a structured error exists.

### Anti-patterns removed in scope

| Pattern | Where | Replacement |
|---|---|---|
| `throw new InvalidOperationException(result.Message)` from a post/rollback result | `PrProductionStockReturnEntry`, `PrProductionStockAdjustmentEntry`, `PrWorkOrderTransferEntry`, `PrWorkOrderTransferList` | Set the message + `OperatorFailure` and return; the result is never re-thrown |
| `catch (Exception ex) { ErrorMessage = ex.Message; }` around post/rollback | same four screens | Generic truthful text ("The response could not be received. Reload … to confirm …"), never a claim that nothing was written and never the raw exception |
| `ErrorMessage = ex.Message` on the FG receipt list batch path | `PrFinishedGoodReceiptList` | Already had a safe generic message; now also builds per-document `ErpOperatorFailureRecord`s |

### Shared chrome / tests

| File | Change |
|---|---|
| `ErpWeb.UI/_Imports.razor` | `@using ErpWeb.UI.Components.Common.OperatorError` (makes every banner render; clears 24 `RZ10012` warnings) |
| `ErpWeb.Tests/Inventory/Transaction/IvOperatorFailureViewTests.cs` | Stale namespace corrected (was not compiling) |
| `ErpWeb.Tests/Production/Transaction/PrCrossModuleOperatorErrorTests.cs` | **new** — 19 cases (see §3) |

---

## 3. Tests added (19 cases, all green)

`PrCrossModuleOperatorErrorTests` pins:

1. Theory over Sales DO, Sales Invoice, Purchase Invoice, Purchase CDN, Material Issue, Daily Production,
   Finished Good Receipt, Production Stock Return, Production Stock Adjustment, Work Order Transfer —
   the headline names the module's document, the presentation code carries the module prefix, and the
   technical ledger text never reaches the operator text.
2. The same `INSUFFICIENT_BASE_QTY` code reads as a **current shortage** on POST and as a
   **historical removal** on ROLLBACK, with different next actions.
3. `LEDGER_MISMATCH` is presented as reconciliation, never as "not enough stock".
4. `Unexpected` stays generic, keeps the support reference and does not offer a retry.
5. `STALE_DOCUMENT` offers refresh-and-retry; `DOCUMENT_ALREADY_POSTED` says do-not-submit-again.
6. Batch view keeps coded and plain (uncoded) failures together, with honest counts.
7. Single-error view uses the same banner shape (Work Order Transfer rollback).
8. No structured error ⇒ `null` ⇒ pages keep their existing message.
9. `IvMasterOperationResult.Fail(…, operatorError:)` keeps the technical message and exposes the
   structured error unchanged.
10. `ProductionPostingFailure`'s implicit conversion preserves existing `return "…"` text.

Result: `Passed! - Failed: 0, Passed: 19, Skipped: 0, Total: 19`.

---

## 4. Build and full test run

| Command | Result |
|---|---|
| `dotnet build ErpWeb.UI\ErpWeb.UI.csproj` | 0 errors, 0 `RZ10012` warnings (was 24) |
| `dotnet build ErpWeb.slnx -c Release` | **0 errors**, 317 warnings (all pre-existing categories: NU1902/NU1903, CS0108-style page warnings) |
| `dotnet test ErpWeb.Tests\ErpWeb.Tests.csproj -c Release --no-build` | **4747 passed, 27 failed, 8 skipped** (12 m 36 s) |
| New suite only | 19/19 passed |
| Inventory banner suite (`ErpOperatorFailureViewTests`) | 6/6 passed |
| Baseline comparison (`git worktree` at `371725c`) for the 27 failing classes | **identical 27 failures, identical test names** |

The 27 failures are pre-existing on this branch and unrelated to the work stream: BOM structure /
product definition, customer master (`SaCustServiceTests`), purchase KPI/inquiry workbench,
e-Invoice self-billed UBL namespaces and the nested-phantom BOM snapshot test. They fail with the same
names and the same assertions on an unmodified `371725c` worktree, which excludes every new file from
this change. They are recorded here, not fixed (Phase 5 forbids changing costing/BOM/customer logic).

---

## 5. Behaviour parity evidence

- **No new writes.** Every producer change is inside an existing `catch`/refusal branch; no query,
  lock or write was added to a success path.
- **No status change.** `Succeeded`, `FailedCount`, per-row success flags and status assignments are
  computed exactly as before; the new properties are only read after a failure is already decided.
- **Same rejected outcome.** A refused post/rollback still returns the same message and the same error
  code; the UI simply prefers the structured explanation when one exists.
- **No retry automation.** No page retries `PostAsync`/`RollbackAsync`; the unexpected-failure text
  tells the operator to reload and confirm before retrying.
- **No entity/tenant leak.** Document numbers come from the document the service already loaded; the
  presenter caps and collapses every producer value and never parses text.

---

## 6. Known limitations / follow-ups for Phase 6

1. Four UI catches in `PrWorkOrderTransferEntry` (source/target lookup, save, update, refresh, delete)
   still show `ex.Message`. They are not stock/cost posting failures. **Phase 6 resolution:**
   registered as out of scope — they forward service-authored business sentences, never exception
   text from a library, and the post/rollback paths on the same page were converted.
2. The entry screens show the banner but not a support log line; logging is done by the services that
   produce the structured error. **Phase 6 resolution:** every Production producer now writes one
   `LogError`/`LogWarning` carrying the same reference printed in the banner
   (`ProductionOperatorErrorLog`), so a reference can be searched in the application log.
3. Sales e-Invoice (`SaCdnList`, `SaInvoiceList`) and self-billed (`PoSb*List`) `ex.Message` catches are
   e-Invoice transport paths, not stock/cost paths, and were deliberately left untouched.
4. No screenshots/UAT were produced in Phase 5; a real UAT script is a Phase 6 deliverable.

---

## 7. Gate request

Phase 5 is complete and waiting for approval to start **Phase 6 — repository-wide safety verification and UAT**
(error-site census vs. the register, required build/test commands, before/after balance/cost/status
parity, and the real warehouse/purchasing UAT script).
