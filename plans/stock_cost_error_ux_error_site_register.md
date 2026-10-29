# Stock / Cost / Quantity Operator-Error — Error-Site Register

**Phase:** 0 (read-only inventory). No C# or business code was modified to produce this document.
**Branch:** `productionv2`
**Baseline HEAD:** `371725c0bda74e386a66bfa3c7559c6f227a14dd` (matches the plan's inspected baseline)
**Remote:** `origin/productionv2` resolved to the same SHA after `git fetch` (no commits ahead).
**Date of census:** 29 Oct 2026.
**Companion documents:** `plans/stock_cost_error_ux_baseline_report.md`, `plans/stock_cost_error_ux_ledger_producer_appendix.csv`.

---

## 0. VERDICT

The plan's central claim is confirmed by inspection: **there is exactly one technical bottleneck per module, and it is an error-presentation bottleneck, not a business-logic one.**

* Every inventory document (8 types) funnels its POST/ROLLBACK outcome through `IvInventoryPostingService.DispatchAsync` → `IvInventoryPostingResult` → `OkPosting(...)` → one `ErrorMessage` string on the existing page.
* The structured `StockLedgerError.Code` and `bool Retryable` are **discarded** at `ErpWeb.Core/Inventory/IvInventoryPostingService.cs:350-352` before any UI can see them.
* The originally reported string is reproducible end-to-end and its exact composition point is known (see §2.2).
* The technical vocabulary problem is measurable: **79 of 191 distinct ledger error texts (41%) contain operator-forbidden vocabulary** (`pile`, `slice`, `epoch`, `lineage`, `sealed`, `posting root`, `layer`, `event <n>`, `FIFO`, `WAC`).

Two findings **change the plan's assumptions** and are recorded in §9:

1. **Five of the 21 stable ledger codes are declared but never produced anywhere** in the repository (`COUNT_SCOPE_FROZEN`, `COUNT_SNAPSHOT_CHANGED`, `COST_METHOD_NOT_IMPLEMENTED`, `HISTORY_BEFORE_CUTOVER`, `LEGACY_LINEAGE_UNVERIFIED`). Catalogue families 27, 28 and parts of 11 are therefore **not reachable today** and must be presented as "reserved" rather than implemented.
2. **`IStockFreezeGuard` is a permanent no-op** (`NoActiveStockFreezeGuard` always returns `null`), so no "stock count freeze" refusal can currently surface.

Neither finding permits behaviour change in this work stream. They only constrain the wording work.

---

## 1. METHOD AND EVIDENCE INVENTORY

### 1.1 Searches executed (all read-only)

| # | Command (abridged) | Purpose | Result |
|---|---|---|---|
| 1 | `git rev-parse HEAD`, `git status --porcelain`, `git fetch origin productionv2` | live-head proof | HEAD = baseline; see baseline report §1 |
| 2 | `rg -c "StockLedgerErrorCodes\.(21 codes)"` over `ErpWeb.Core/UI/Model/Tests` | which files produce ledger errors | **160 source files** (excluding tests) reference the code constants |
| 3 | `rg -U -o 'StockLedgerErrorCodes\.(\w+),\s*\$?"…"'` | extract each inline ledger message with file+line | **199 producer sites with inline text** → appendix CSV |
| 4 | `rg -n "StockLedgerErrorCodes\.<code>"` per code | find variable-message producers missed by (3) | +8 `CLOSED_PERIOD` sites pass a computed message |
| 5 | `rg -n "ex\.Message"` inside Inventory/Sales/Purchase/Production/StockLedger/Costing | raw exception propagation | **107 hits**; 34 are in-scope stock/cost paths (see Register E) |
| 6 | `rg -n "RollbackAsync\(|\.PostAsync\(|RollBack"` in the four scoped UI trees | user-initiated post/rollback surfaces | **93 call sites** |
| 7 | `rg -n "ErrorMessage = .*(result|ex)\."` in scoped UI | where the string reaches the screen | see Registers B and C |
| 8 | `rg -n "IvInventoryPostingBatchResult\.Fail"` | in-boundary refusal producers | **348+ matches** (many in replay files) |
| 9 | `rg -n "SucceededCount|FailedCount"` in UI | partial-success rendering | 30 hits; see §2.3 |

### 1.2 Exclusions

* `.replay-production-fifo-0d718e43/` (untracked, 3 797 files) is a **prior agent's repository snapshot**, including copies of costing sources. It is **not** build output of this branch and was excluded from every count to avoid double-counting.
* `ErpWeb.Tests/` producers are listed separately (they assert the technical text and must stay green).
* Admin/Numbering/Master pages are out of scope (no stock/costing decision).
* Machine-scheduling, calendar, e-invoice and price-maintenance `ex.Message` sites were reviewed and classified **out of scope** (they are not stock, costing or quantity failures).

### 1.3 What "user-visible" means here

A site is **user-visible** when the text can reach `PageBase.ErrorMessage`, a per-page `ErrorMessage`, a toast/banner, or a `Message`/`ErrorMessage` property the page renders. Everything else is **INTERNAL_ONLY** (logs, retries, tests, projections that become a `Succeeded=false` result upstream).

---

## 2. ERROR TRANSPORT MAP

### 2.1 The four layers

```
LAYER 1 — PRODUCERS (≈199 inline sites + variable-message helpers)
   throw new StockLedgerException(new(StockLedgerErrorCodes.X, "<technical text>", Retryable:?))
   or  IvInventoryPostingBatchResult.Fail(batchNo, "<technical text>")

LAYER 2 — BOUNDARY  (ErpWeb.Core/Inventory/IvInventoryPostingService.cs:342-358)
   catch DbUpdateConcurrencyException      -> Fail(batchNo, "Stock was modified by another user. Retry.")
   catch DbUpdateException (unique)        -> Fail(batchNo, "Posting conflict (duplicate history or balance).")
   catch StockLedgerException ex           -> Fail(batchNo, ex.Error.Message)      <-- CODE + RETRYABLE LOST
   catch Exception ex (logs)               -> Fail(batchNo, ex.Message)            <-- RAW .NET TEXT LOST TO UI

LAYER 3 — AGGREGATION (ErpWeb.Core/Inventory/IIvInventoryPostingService.cs)
   FromBatches:  "Batch {N}: {message}" joined with " "
   partial:      "Rolled back {ok} of {n}. Batch {N}: {message}"

LAYER 4 — MODULE WRAPPER + UI
   8 × OkPosting(IvInventoryPostingResult) -> ErrorMessage copied verbatim
   UI: ErrorMessage = result.ErrorMessage ?? "<page-specific fallback>"
       rendered as  `<div class="iv-toast iv-toast--err" role="alert">`
```

### 2.2 Exact trace of the originally reported message

Reported text: `Batch 1: Physical replay would make the stock slice negative at event 2 while replaying.`

| Step | Location | What happens |
|---|---|---|
| 1 | `ErpWeb.UI/Inventory/Transactions/IvGoodsReceiptList.razor.cs:277` | user confirms ROLLBACK → `GoodsReceipt.RollbackAsync(batchNos)` |
| 2 | `ErpWeb.Core/Inventory/IvGoodsReceiptService.cs:716-726` | resolves trx type, delegates to `_posting.RollbackAsync(...)`, wraps with `IvGoodsReceiptOperationResult.OkPosting` (line 725) |
| 3 | `ErpWeb.Core/Inventory/IvInventoryPostingService.cs:231-235` | `DispatchAsync(post:false)` |
| 4 | `IvInventoryPostingService.cs:313` | dispatch to `RollBackGoodsReceiptAsync` (defined at 1077) |
| 5 | `ErpWeb.Core/StockLedger/StockReplayPhysicalPlanner.cs:459-460` | `StockReplayPhysicalSimulator.SimulateRemoval(balance.StdQty, removedActiveEvent, later)` |
| 6 | `ErpWeb.Core/StockLedger/StockReplayPhysicalSimulator.cs:187-193` | `Apply(...)` → `EnsureNonnegative(quantity, movement.EventId, "replaying")` |
| 7 | `StockReplayPhysicalSimulator.cs:216-220` | `throw new StockLedgerException(new(StockLedgerErrorCodes.InsufficientBaseQty, $"Physical replay would make the stock slice negative at event {eventId} while {phase}."))` |
| 8 | `IvInventoryPostingService.cs:350-352` | `catch (StockLedgerException ex)` → `Fail(batchNo, ex.Error.Message)` — **`InsufficientBaseQty` and `Retryable:false` are dropped here** |
| 9 | `IIvInventoryPostingService.cs:22-30` | `FromBatches` prefixes `Batch 1: ` and joins |
| 10 | `IIvGoodsReceiptService.cs:27-34` | `OkPosting` copies `posting.ErrorMessage` |
| 11 | `IvGoodsReceiptList.razor.cs:296` | `ErrorMessage = result.ErrorMessage ?? …` |
| 12 | `IvGoodsReceiptList.razor` line 15-21 | `<div class="iv-toast iv-toast--err" role="alert">` |

**Three distinct rejection phases share that one message**: `phase` is `"removing"` (shortage caused by the receipt's own removal), `"rewinding"` (a later event cannot be unwound) or `"replaying"` (re-applying a later event goes negative). They require **different** operator next-steps, and the phase is *only* present as English text today. `EventId` is also only present as text, and its meaning is not stable (see §2.4).

### 2.3 Aggregation semantics that must not change

| Behaviour | Current code | Consequence for presentation |
|---|---|---|
| Counts | `SucceededCount = batches.Count(x => x.Succeeded)`; `FailedCount = count - ok` | must be preserved verbatim |
| Ordering | `Batches` in input order; failures joined in that order | per-batch rows must preserve order |
| All-failed text | `"Batch {N}: {msg}"` per failure (IIvInventoryPostingService.cs:25) | produces the reported prefix |
| Partial POST text | `"Posted {ok} of {n}. …"` (lines 362-373) | already tells the truth about partial success |
| Partial ROLLBACK text | `"Rolled back {ok} of {n}. …"` (lines 375-387) | idem |
| Failure return | `IvInventoryPostingResult.Fail(msg)` sets `FailedCount = 1`, **no batches** | a UI that shows per-batch detail must handle "no batch list" |

### 2.4 Event-identity precision (why "event 2" must never become "Batch 2")

* `StockReplayPhysicalTimelineReader` merges **positive** ids (`IvTrxHistory.Id`) and **negative** ids (stock-count generations) into one ordered timeline.
* `StockReplayPhysicalSimulator.ValidateEvent` (lines 194-213) explicitly documents that negative ids identify count generations.
* Ordering is `(EffectiveAt, ClassPriority, OriginalPostingSequence, LineIdentity, AttachedSuborder)` — an integer in a message is a **timeline position identifier, not a document number, not a batch, not a line**.
* Therefore Phase 2+ may only add "affected transaction" detail through a **verified owner lookup** (Phase 3), never by string-parsing `event {id}`.

---

## 3. REGISTER A — EXTERNAL BOUNDARIES (the six mandated surfaces)

Status legend: **USER** = text can reach an operator; **BOUNDARY** = needs additive metadata; **INT** = internal only.

| ID | Surface | Producer / boundary | Result contract | Consumer | Status | Current exposure |
|---|---|---|---|---|---|---|
| A-01 | Inventory posting (all 8 doc types) | `IvInventoryPostingService.DispatchAsync` `ErpWeb.Core/Inventory/IvInventoryPostingService.cs:342-358` | `IvInventoryPostingResult` / `IvInventoryPostingBatchResult` | 8 list pages | **USER + BOUNDARY** | code + retryability dropped; raw `ex.Message` possible |
| A-02 | Inventory aggregation | `IIvInventoryPostingService.cs:22-31` `FromBatches` | same | idem | **USER** | `"Batch {N}: …"` concatenation |
| A-03 | Inventory partial-run summary | `IvInventoryPostingService.cs:360-387` | same | idem | **USER** | truthful counts already present |
| A-04 | 8 document wrappers | `IIvGoodsReceiptService.cs:27`, `IIvMiscReceiptService.cs:40`, `IIvStockAdjustmentService.cs:31`, `IIvStockReturnService.cs:38`, `IIvMiscIssueService.cs:38`, `IIvScrapService.cs:38`, `IIvStockTransferService.cs:38`, `IIvVendorReturnService.cs:38` | per-document `OkPosting` | idem | **BOUNDARY** | single `ErrorMessage` copied; batch detail dropped |
| A-05 | Stock count post/rollback | `IvStockCountService` (+ `.Replay.cs`) | count result with `ErrorMessage`, `PostedBatchNo`, `PostedStaleLines` | `IvStockCountList.razor.cs:315-335`, `IvStockCount.razor.cs:653-660,694` | **USER** | technical `LEDGER_MISMATCH` texts (see D-14) |
| A-06 | Sales DO post/rollback | `SaDoService.cs:1358,1416,1474`; `SaDoService.cs:3504` | `SaDoPostingItemResult.Failed(doNo, ex.Message)`, `SaDoErrorKind` | `SaDoList.razor.cs:256-257`, `SaDo.razor.cs:1456` | **USER + RAW** | per-document `ErrorMessage` |
| A-07 | Sales Invoice post/rollback | `SaInvoiceService.cs:1410,1467` | `SaInvoicePostingItemResult.Failed(no, ex.Message)`, `SaInvoiceErrorKind` | `SaInvoiceList.razor.cs:344-345`, `SaInvoice.razor.cs:1947` | **USER + RAW** | idem |
| A-08 | Sales CDN post/rollback | `SaCdnService.cs:1016,1069` | `SaCdnPostingItemResult.Failed(docNo, ex.Message)` | `SaCdnList.razor.cs:454-455` | **USER + RAW** | idem |
| A-09 | Purchase Invoice (settlement/costing) | `PoInvoiceService.cs:2260` (`InnerException?.Message ?? ex.Message`), `PurchaseReceiptCostSettlementService.cs` (8 ledger code throw sites), `PurchaseCostAdjustmentPostingService.cs` (10) | `PoInvoiceOperationResult` + `ValidationErrors` dictionary | `PoInvoice.razor.cs:417,448,478`, `PoInvoiceList.razor.cs:265-266` | **USER + RAW** | validation headline + raw fallback |
| A-10 | Purchase CN/DN | `PoCdn` services + `PurchaseCdnCostAdjustmentService` | result + validation dictionary | `PoCdn.razor.cs:847,886,981`, `PoCdnList.razor.cs:331-332` | **USER** | idem |
| A-11 | Production Material Issue post/rollback | `ProductionMaterialIssueService.Rollback.cs:412-414` `catch (StockLedgerException ex) → RollbackFail(Validation, ex.Error.Message)` | `IvMasterOperationResult<…>` with `Message` | `PrMaterialIssueList.razor.cs:237,278`, `PrMaterialIssueEntry.razor.cs:449,555` | **USER + BOUNDARY** | code lost at catch |
| A-12 | Daily Production output post/rollback | `ProductionOutputService.Posting.cs:634,649`, `ProductionOutputService.Rollback.cs:563,573` | `Message` (+ `IvMasterErrorCode`) | `PrDailyProductionList.razor.cs:220,228`, `PrDailyProductionEntry.razor.cs:505,580` | **USER + RAW** | raw `ex.Message` |
| A-13 | Finished Good Receipt | `ProductionFinishedGoodReceiptService` + cost authorities | `Message` + `ReadinessErrors` | `PrFinishedGoodReceiptEntry.razor.cs:573`, `PrFinishedGoodReceiptList.razor.cs:225-226,390,411` | **USER** | readiness list + raw message |
| A-14 | Production Stock Return | `ProductionStockReturnService.cs:309,423-429` | `Message` + `ReadinessErrors` (`"Line {code}: {ex.Message}"`) | `PrProductionStockReturnEntry.razor.cs:328,351`, list `:91` | **USER + RAW** | raw `ex.Message` inside a line label |
| A-15 | Production Stock Adjustment | `ProductionStockAdjustmentService.cs:326` (`catch (AdjustmentException) → error = ex.Message`) | `Message` | `PrProductionStockAdjustmentEntry.razor.cs:380,400`, list `:55` (**throws `InvalidOperationException(result.Message)` then shows `ex.Message`**) | **USER + RAW** | double-wrap of technical text |
| A-16 | Work Order Transfer post/rollback | `ProductionWorkOrderTransferService.cs:572-1161`, `.Rollback.cs:346-351` | `Fail<…>(ex.Message, ex.Code)` | `PrWorkOrderTransferEntry.razor.cs:663,708`, list `:186,199` | **USER + RAW** | technical `TransferException` text |
| A-17 | Tenant/branch scope refusal | `IvInventoryPostingService.cs:269-273` `"Company, branch, and location are required."` | `IvInventoryPostingResult.Fail` | all inventory pages | **USER** | no tenant id leaked (good) |
| A-18 | Permission refusal | `IvInventoryPostingService.cs:290-293` `"Not authorized."`; Sales `"Not authorized."`, `SaDoErrorKind.Authorization` | result | all | **USER** | terse but safe |
| A-19 | Selection guards | `IvInventoryPostingService.cs:253-266`; Sales/Purchase equivalents | result | all | **USER** | fine; keep |
| A-20 | Production list double-wrap | `PrProductionStockReturnList.razor.cs:91`, `PrProductionStockAdjustmentList.razor.cs:55` — `throw new InvalidOperationException(result.Message)` then `ErrorMessage = ex.Message` | UI-only | — | **USER** | technical text re-thrown as an exception **inside the UI** |

---

## 4. REGISTER B — INVENTORY UI CONSUMERS (existing pages)

All eight ledger list pages share one identical `ConfirmActionAsync` shape (`BeginBlockingWork` → `switch ConfirmAction` → success status text / `ErrorMessage = result.ErrorMessage` → reload when `SucceededCount > 0`). Verified lines:

| ID | Page | Action lines | Error display line | Disposition |
|---|---|---|---|---|
| B-01 | `IvGoodsReceiptList.razor.cs` | 277-278 | **296** | primary fix target (phase 2) |
| B-02 | `IvMiscIssueList.razor.cs` | 283-284 | **304** | phase 4 |
| B-03 | `IvMiscReceiptList.razor.cs` | 278-279 | **297** | phase 4 |
| B-04 | `IvScrapList.razor.cs` | 283-284 | **304** | phase 4 |
| B-05 | `IvStockAdjustmentList.razor.cs` | 283-284 | **304** | phase 4 |
| B-06 | `IvStockReturnList.razor.cs` | 275-276 | **294** | phase 4 |
| B-07 | `IvStockTransferList.razor.cs` | 283-284 | **304** | phase 4 |
| B-08 | `IvVendorReturnList.razor.cs` | 283-284 | **304** | phase 4 |
| B-09 | `IvStockCountList.razor.cs` | 315-335 | inline per action | phase 4 |
| B-10 | `IvStockCount.razor.cs` | 653, 694 | 657, 696 | phase 4 (16 `ErrorMessage`/`ex.Message` hits in file) |
| B-11 | `IvGoodsReceipt.razor.cs` | save only (no post) | 154, 664, 707 | field/line validation — **do not touch** except friendly fallback |
| B-12 | Entry pages (MI/MR/Scrap/VR/TR/ADJ) | — | 3-8 hits each | field validation preserved |

**Shared detail-panel gap:** no page currently renders `result.Batches`. Even where `SucceededCount/FailedCount` are shown in the status line (e.g. `IvMiscIssueList.razor.cs:293-294`), the *failed* batch identity is only inside the concatenated string.

**Alert surface:** `IvGoodsReceiptList.razor:15-21` (`iv-toast iv-toast--err`, `role="alert"`, dismiss button). Reuse this class set; do not add a new design system.

---

## 5. REGISTER C — CROSS-MODULE UI CONSUMERS

| ID | Page | Post/rollback call | Error display | Notes |
|---|---|---|---|---|
| C-01 | `SaDoList.razor.cs` | 256-257 | 375-380 region | per-DO results; technical fallback |
| C-02 | `SaDo.razor.cs` | 1456 | 1700 `BuildValidationMessage` | keep dictionary authority |
| C-03 | `SaInvoiceList.razor.cs` | 344-345 | ~9 `ex.Message`/`ErrorMessage` hits | file also has raw `ex.Message` usage |
| C-04 | `SaInvoice.razor.cs` | 1947 | 2186 `BuildValidationMessage` | 16 hits in file |
| C-05 | `SaCdnList.razor.cs` | 454-455 | 9 hits | e-invoice counts mixed in (out of scope) |
| C-06 | `PoInvoiceList.razor.cs` | 265-266 | 276 status + error area | shows `OK=x, Failed=y` |
| C-07 | `PoInvoice.razor.cs` | 448, 478 | 417 `BuildValidationMessage` | costing/period errors surface here |
| C-08 | `PoCdnList.razor.cs` | 331-332 | 375, 497, 511 | |
| C-09 | `PoCdn.razor.cs` | 837, 876 | 847, 886, 981 | |
| C-10 | `PrMaterialIssueList.razor.cs` | 237, 278 | 285-288 uses `SucceededCount/FailedCount` | partial-success aware |
| C-11 | `PrDailyProductionList.razor.cs` | 220, 228 | list-level `ErrorMessage` | |
| C-12 | `PrFinishedGoodReceiptList.razor.cs` | 225-226 | 390, 411 | |
| C-13 | `PrProductionStockReturnList.razor.cs` | 91 | throws `InvalidOperationException(result.Message)` | **anti-pattern: rethrow in UI** |
| C-14 | `PrProductionStockAdjustmentList.razor.cs` | 55 | single-line catch-all `ErrorMessage = ex.Message` | same anti-pattern |
| C-15 | `PrWorkOrderTransferList.razor.cs` | 186, 199 | list display | |
| C-16 | `PrWorkOrderTransferEntry.razor.cs` | 663, 708 | 7 hits | |
| C-17 | `IvCostingCenter.razor.cs` / `IvReconciliation.razor.cs` / `IvStockCard.razor.cs` / `IvStockValue.razor.cs` / `IvStockSummary.razor.cs` / `IvBalanceLot.razor.cs` / `IvTrxInquiry.razor.cs` / `IvStockCountVariance.razor.cs` | read-only inquiry | 1-4 hits each | diagnostic destination only; **no new transaction screens** |

---

### 5.1 PHASE 5 DISPOSITION (added 2026-10-29)

| Register ID | Disposition after Phase 5 |
|---|---|
| C-01 `SaDoList` | Structured `OperatorFailure` from per-DO posting results; banner rendered; original message kept as fallback |
| C-03 `SaInvoiceList` | Same batch pattern |
| C-05 `SaCdnList` | Same batch pattern (e-Invoice `ex.Message` paths deliberately untouched — not stock/cost) |
| C-06 `PoInvoiceList` | Same batch pattern |
| C-08 `PoCdnList` | Same batch pattern |
| C-10 `PrMaterialIssueList` | Per-row `OperatorError` on post and rollback; banner rendered |
| C-11 `PrDailyProductionList` | Same, plus `PrDailyProductionEntry` |
| C-12 `PrFinishedGoodReceiptList` | Batch failures now build `ErpOperatorFailureRecord`s; banner rendered; entry screen too |
| C-13 `PrProductionStockReturnList` | Anti-pattern removed in both list and entry |
| C-14 `PrProductionStockAdjustmentList` | Anti-pattern removed in both list and entry |
| C-15 `PrWorkOrderTransferList` | Post/rollback no longer throw; banner rendered |
| C-16 `PrWorkOrderTransferEntry` | Post/rollback no longer throw; banner rendered; lookup/save/delete catches left as Phase 6 item |
| E-04 … E-12 | Cross-module producers now attach `ErpOperatorError` on the existing `catch`/refusal branch; technical message unchanged |
| E-13 | **Resolved** — no UI rethrow remains for post/rollback |
| E-14 | Unchanged, still out of scope (scheduling) |

---

## 6. REGISTER D — STABLE LEDGER CODES (21) AND THEIR PRODUCERS

`ErpWeb.Core/StockLedger/StockLedgerErrors.cs` (values must not change). Producer counts are from the appendix CSV (inline-message sites only).

| ID | Code | Producers (files) | Inline sites | Representative current text | Operator family (plan §4) | Reachable from UI today? |
|---|---|---|---|---|---|---|
| D-01 | `LEDGER_DISABLED` | 6 | 10 | "Backdated WAC replay requires an active V2 posting root." / "Stock ledger V2 is not active for this branch." | 11 (cutover/unsupported) | yes (backdated paths) |
| D-02 | `STOCK_BUSY` | 1 (`BranchStockTransactionLock.cs:58`) | 1 | "Another stock transaction is in progress for this branch. Retry the command." | 24 | yes |
| D-03 | `REQUEST_ID_REUSED` | 1 (`StockPostingCoordinator`) | 2 | "The request ID was already used with different semantic input." | 22 | rarely (retry path) |
| D-04 | `DOCUMENT_ALREADY_POSTED` | 1 (`StockPostingCoordinator`) | 2 | "This source document revision and posting role already has a stock posting." | 21 | yes |
| D-05 | `STALE_DOCUMENT` | 2 | 2 | "The Production material pool changed after receipt replay planning." | 23 | yes |
| D-06 | `CLOSED_PERIOD` | 8 sites pass a **variable** message (`StockPostingGuards.cs:39`, 7 writers) | 0 inline | guard text: "The date {0:yyyy-MM-dd} falls in a closed inventory period (closed through {1:yyyy-MM-dd}). Reopen the period first." (`IvPeriodCloseStatuses.cs:28`) | 25 | yes |
| D-07 | `BACKDATED_STOCK_EVENT` | 2 | 2 | "Cost-method cutover cannot be backdated behind later valued movements." | 11/12 | admin paths |
| D-08 | `COUNT_SCOPE_FROZEN` | **0** | 0 | — | 27 | **not produced** |
| D-09 | `INVALID_STOCK_IDENTITY` | 6 | 18 | "A trusted company/branch write scope is required." | 31/32/47 | yes (rare) |
| D-10 | `INSUFFICIENT_BASE_QTY` | 8 | 8 | "Physical replay would make the stock slice negative at event {eventId} while {phase}." / "FIFO layers at {..:O} contain {available}, but {quantity} is required." / "Production balance {uid} would become negative." | 01/02/03/04/35/43 | **yes — the reported bug** |
| D-11 | `INVALID_UOM_CONVERSION` | 1 (`ProductionStockWriter.cs:135`) | 1 | conversion refusal | 30 | yes |
| D-12 | `REVERSAL_DEPENDENCY` | 6 | 50 | "Standard variance {id} has no exact Inventory fact reversal." | 19/36/42 | yes |
| D-13 | `REVERSAL_ALREADY_EXISTS` | 1 (`InventoryValuationService`) | 4 | "History {historyId} already has a FIFO valuation reversal." | 20 | yes |
| D-14 | `LEDGER_MISMATCH` | 30+ | 50 | "The historical count does not reconcile to its projected stock and cost state." / "FIFO issue has invalid or duplicate source layers." | 05/06/41/47 | yes |
| D-15 | `LEGACY_LINEAGE_UNVERIFIED` | **0** | 0 | — | 19 | **not produced** |
| D-16 | `HISTORY_BEFORE_CUTOVER` | **0** (separate consts with the same *literal* exist in `CostingContracts.cs:60` and `IProductionStockHistoryService.cs:39`, unrelated to this class) | 0 | — | 11 | **not produced via this class** |
| D-17 | `VALUATION_REQUIRED` | 6 | 34 | "Item '{itemCode}' has no approved FIFO opening or receipt layer." | 04/18/34/38/45 | yes — high volume |
| D-18 | `COUNT_SNAPSHOT_CHANGED` | **0** | 0 | — | 28 | **not produced** |
| D-19 | `COST_POLICY_MISSING` | 1 (`StockCostMethodResolver.cs`) | 1 | "No active stock costing policy covers {businessDate:yyyy-MM-dd} for {company}/{branch}." | 09 | yes |
| D-20 | `COST_POLICY_INVALID` | 4 | 14 | "Overlapping active stock costing policies cover {date} …" / "No inventory costing strategy is registered for '{costMethod}'." | 10 | yes (admin paths) |
| D-21 | `COST_METHOD_NOT_IMPLEMENTED` | **0** | 0 | — | 10 | **not produced** |

**Critical classification warning proven by the data:** `INSUFFICIENT_BASE_QTY` alone covers at least six genuinely different situations (historical replay, FIFO layer exhaustion, production contribution, production balance, valuation quantity, UOM path). A per-code map would be **wrong**; classification must use action + producing service + (where available) phase/context.

### 6.1 Non-ledger message families (same pipes, no code)

| Family | Producer | Current text shape |
|---|---|---|
| Period closed | `IvPeriodCloseGuard.EnsureOpenAsync` (`IvPeriodCloseStatuses.cs:28`) | "The date 2026-10-02 falls in a closed inventory period (closed through 2026-09-30). Reopen the period first." |
| Future document date | `IvStockMovementRules.EnsureValidMovementDate` via `FuturePostDateError` (`IvInventoryPostingService.cs:76-85`) | rule text; **MM-dd formatting not guaranteed** |
| Stock-date / lot not available | `IvStockDateRules` (`IvStockDateRules.cs:22-36`) | "Stock lot X is dated dd/MM/yyyy and cannot be used for transaction date dd/MM/yyyy." / "Stock balance Id {balLocId} has no stock date and cannot be used until it is repaired." ← **leaks a DB id** |
| Batch state guards | `IvInventoryPostingService` (e.g. 477-518, 835-861, 1202-1231, 1295-1314) | "Only NEW goods receipts can be posted." etc. — already operator-friendly |
| Quantity/selection guards | `IvInventoryPostingService.cs:253-266` | "Select at most 10 batches." |
| Sales shipment | `SaDoService` posting item results | technical valuation text |
| Purchase settlement | `PurchaseReceiptCostSettlementService`, `PurchaseCostAdjustmentPostingService`, `PurchaseCdnCostAdjustmentService` | `"…has no exact…"`, `"…cannot be allocated"` technical |

---

## 7. REGISTER E — RAW EXCEPTION LEAK SITES (in scope)

These are the points where a non-`StockLedgerException` text becomes operator text. Each must gain a safe boundary + reference id in its phase.

| ID | Path:line | Sink |
|---|---|---|
| E-01 | `ErpWeb.Core/Inventory/IvInventoryPostingService.cs:357` | `Fail(batchNo, ex.Message)` in dispatch |
| E-02 | `ErpWeb.Core/Inventory/IvGoodsReceiptService.cs:406` | `IvGoodsReceiptOperationResult.Fail(ex.Message)` |
| E-03 | `ErpWeb.Core/Inventory/IvGoodsReceiptService.cs:955` | line validation `"Line {lineNo}: {ex.Message}"` |
| E-04 | `ErpWeb.Core/Sales/SaDoService.cs:1358, 1416, 1474, 3504` | posting item results / db-error helper |
| E-05 | `ErpWeb.Core/Sales/SaInvoiceService.cs:1410, 1467` | posting item results |
| E-06 | `ErpWeb.Core/Sales/SaCdnService.cs:1016, 1069` | posting item results |
| E-07 | `ErpWeb.Core/Purchase/PoInvoiceService.cs:2260` | `InnerException?.Message ?? ex.Message` |
| E-08 | `ErpWeb.Core/Production/ProductionOutputService.Posting.cs:634, 649`; `.Rollback.cs:563, 573` | `Fail(ex.Message[,…])` |
| E-09 | `ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs:353` | returns `ex.Message` |
| E-10 | `ErpWeb.Core/Production/ProductionStockReturnService.cs:309, 423-429` | readiness errors `"Line {code}: {ex.Message}"` |
| E-11 | `ErpWeb.Core/Production/ProductionStockAdjustmentService.cs:326` | captured into `error` |
| E-12 | `ErpWeb.Core/Production/ProductionWorkOrderTransferService*.cs` (12 catch sites) | `Fail<…>(ex.Message, ex.Code)` |
| E-13 | `ErpWeb.UI/Planning/WorkOrders/PrProductionStockReturnList.razor.cs:91`; `PrProductionStockAdjustmentList.razor.cs:55` | rethrow + `ErrorMessage = ex.Message` **in the UI** |
| E-14 | `ErpWeb.UI/Planning/WorkOrders/PrMachineSchedulePlanner.razor.cs:397, 897` | out of scope (scheduling) — recorded only |

**Note:** `ErpWeb.Core/Inventory/IvInventoryPostingService.cs:4415`, `PoOrderService.cs:2518`, `PoPrService.cs:2660`, `SaQtService.cs:3056`, `SaSoService.cs:3422` are DB-error text helpers (unique/FK translation). They were reviewed: they return friendly-ish text for write failures but are **not** stock/costing decisions. Listed as **INTERNAL_ONLY / out of scope** unless a scoped page renders them directly.

---

## 8. REGISTER F — INTERNAL-ONLY PRODUCERS (no rewrite)

| ID | Group | Files | Count | Why internal |
|---|---|---|---|---|
| F-01 | FIFO replay writers/projectors | `StockReplayFifo*.cs` | 40 files | exceptions are converted to a per-batch failure upstream; text never reaches UI **directly**; do not touch |
| F-02 | WAC replay writers/planners | `StockReplayWac*.cs` | 27 files | idem |
| F-03 | Standard replay writers | `StockReplayStandard*.cs` | 24 files | idem |
| F-04 | Stock-count writers/generators | `StockCount*.cs` | 12 files | idem |
| F-05 | Posting coordinator/fingerprint/registry | `StockPosting*.cs` | 5 files | produces `StockLedgerError`, caught at boundary |
| F-06 | Valuation engine | `InventoryValuationService.cs` (70 code refs) | 1 | core costing; **highest-risk file — no-touch** |
| F-07 | Cost-method cutover / revaluation | `StockCostMethodCutoverService.cs` (26), `StandardCostRevaluationService.cs` (3) | 2 | admin-only surfaces; wording later |
| F-08 | Production cost writers | `Production*Cost*.cs`, `Production*Writer.cs` | 12 files | internal projection failures |

`LEDGER_MISMATCH` alone has 50 inline sites, 34 of them in FIFO/WAC/Standard/count writers. **None of these may be reworded at source**; the presenter maps at the boundary.

---

## 9. FINDINGS THAT ADJUST THE PLAN

| # | Finding | Evidence | Required adjustment |
|---|---|---|---|
| 1 | 5 codes never produced | §6 rows D-08, D-15, D-16, D-18, D-21; `rg` finds no `StockLedgerErrorCodes.<code>` use anywhere | Implement their **presets** in the presenter (so a future producer is safe) but do **not** build UI paths or UAT scripts that assume they occur. Catalogue families 27/28 = "reserved". |
| 2 | Freeze guard is a stub | `StockPostingGuards.cs:45-53` `NoActiveStockFreezeGuard` returns `null` | Family 27 wording must not be presented as an existing control. |
| 3 | Global freeze/count-lock wording could mislead | as above | Never tell an operator "a stock count is in progress" — it cannot currently be the reason. |
| 4 | `Retryable` is discarded | `IvInventoryPostingService.cs:350-352` | Phase 2 must carry it to the DTO; **do not** derive retryability from message text. |
| 5 | Production lists rethrow result text as exceptions | `PrProductionStockReturnList.razor.cs:91`, `PrProductionStockAdjustmentList.razor.cs:55` | Phase 5 must fix the presentation at these two UI sites (no service change). |
| 6 | `IvStockDateRules.NullStockDateMessage` leaks `balLocId` | `IvStockDateRules.cs:35-36` | Phase 4 must replace with operator text at the boundary (id stays in logs). |
| 7 | Period guard formats ISO `yyyy-MM-dd` | `IvPeriodCloseStatuses.cs:28` | Operator contract requires `dd/MM/yyyy`; format at the presenter, **not** by changing the guard. |
| 8 | No page renders `Batches` today | Registers B/C | Per-batch detail is genuinely additive; no existing element will be displaced. |
| 9 | Eight independent `OkPosting` factories exist | §A-04 | A single additive property on `IvInventoryPostingResult` reaches all eight pages without touching eight DTOs. |

---

## 10. EXPLICIT NO-TOUCH LIST (verified present at HEAD)

**Costing / quantity / posting decision files (may not be edited except the one permitted diagnostic-metadata site):**

```
ErpWeb.Core/StockLedger/StockReplayPhysicalSimulator.cs        (metadata-only allowance, Phase 2, after tests)
ErpWeb.Core/StockLedger/StockReplayPhysicalPlanner.cs
ErpWeb.Core/StockLedger/StockReplayPhysicalTimelineReader.cs
ErpWeb.Core/StockLedger/InventoryValuationService.cs
ErpWeb.Core/StockLedger/StockReplayFifo*.cs        (40 files)
ErpWeb.Core/StockLedger/StockReplayWac*.cs         (27 files)
ErpWeb.Core/StockLedger/StockReplayStandard*.cs    (24 files)
ErpWeb.Core/StockLedger/StockCount*.cs             (12 files)
ErpWeb.Core/StockLedger/StockCostMethodCutoverService.cs
ErpWeb.Core/StockLedger/StockPostingCoordinator.cs
ErpWeb.Core/StockLedger/StockPostingGuards.cs
ErpWeb.Core/StockLedger/BranchStockTransactionLock.cs
ErpWeb.Core/Inventory/IvInventoryPostingService.cs        (except catch blocks at 342-358 and, if unavoidable, no other line)
ErpWeb.Core/Inventory/IvInventoryPostingService.{WacReplay,FifoReplay,StandardMrReplay,StandardMiReplay,...}.cs
ErpWeb.Core/Inventory/IvStockCountService*.cs
ErpWeb.Core/Purchase/PurchaseReceiptCostSettlementService.cs
ErpWeb.Core/Purchase/PurchaseCostAdjustmentPostingService.cs
ErpWeb.Core/Production/ProductionPostingInvariant.cs
ErpWeb.Core/Production/Production*Cost*Writer*.cs
ErpWeb.Core/Production/ProductionStockWriter.cs
```

**Concurrent-work evidence:** one unrelated file is dirty (`ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs`, `GetEffectiveMinWidth` hard-coded to `20`). It is **not** touched by this plan. An untracked `.replay-production-fifo-0d718e43/` snapshot indicates recent FIFO-replay investigation by another agent — treat all replay files as actively owned.

---

## 11. PHASE-BY-PHASE SITE ALLOCATION (proposed)

| Phase | Sites from this register |
|---|---|
| 2 | A-01…A-04, B-01, D-10 (replay variants), D-06, D-02, D-04, D-23-style states; X-01 below |
| 3 | enrichment for D-10 replay rejections (positive `IvTrxHistory.Id` only); negative count ids stay generic |
| 4 | B-02…B-12, D-11, D-14, D-17, D-19, D-20, plus non-ledger period/future-date/stock-date families |
| 5 | A-05…A-16, C-01…C-16, E-04…E-13 |
| 6 | re-run this census; assert 0 remaining scoped raw sites |

---

## 12. TEST CASES REQUIRED PER SITE (Phase 0 → later phases)

| Test | Site | Assertion |
|---|---|---|
| T-01 | D-10 via GRN batch 1 | rollback still fails with `INSUFFICIENT_BASE_QTY`; `IvBalLoc.StdQty`, `IvTrxHistory` rows, `StockPosting` rows byte-identical to baseline; UI text has no `slice|event|replaying` |
| T-02 | D-10 phase=`removing` | different next-step text from T-01 |
| T-03 | D-10 phase=`rewinding` | third distinct text |
| T-04 | multi-batch (1/2/10 receipts) | `SucceededCount`, `FailedCount`, order identical; message names succeeded vs failed |
| T-05 | A-19 | 11 selected → unchanged refusal, unchanged text |
| T-06 | D-06 | closed period text uses `dd/MM/yyyy` and the closed-through date; refusal unchanged |
| T-07 | D-02 | `STOCK_BUSY` still `Retryable` true; UI says "refresh status", never auto-retries |
| T-08 | negative event id | no document name invented; detail omitted |
| T-09 | cross-tenant | enrichment returns nothing for another company/branch |
| T-10 | `VIEW_COST` denied | no monetary values in details |
| T-11 | presenter unit tests | 21 codes × actions; no forbidden vocabulary; no `{placeholder}` leaks |
| T-12 | E-13 sites | UI no longer rethrows; original action outcome unchanged |

---

## 13. KNOWN LIMITS OF THIS CENSUS (honesty statement)

* The register is **complete at the boundary layer** (all documented post/rollback surfaces) and **sampled at the producer layer**: 199 inline ledger messages were extracted mechanically, but messages built by helper methods (`Invalid(...)`, `Stale(...)`, `Fail(batchNo, <variable>)`) are not individually enumerated. Their families are identified in §6.1.
* Six of the eight inventory list pages were not read line-by-line; their identity with the GRN/MI shape is inferred from the identical `OkPosting` factory, identical assignment line and identical status-message block. **Phase 2/4 must re-open each page before editing it.**
* No integration/UAT environment was exercised. No production-like data was used. Full test-suite status is recorded in the baseline report.
* Nothing in this document asserts that business behaviour is correct — only that it is **already** what the customer is running.

---

## 14. PHASE 6 DISPOSITION (final census, current tree)

Re-run of the census on the post-Phase-6 tree. Counts are `Select-String` hits, so they include
harmless re-emissions of business text; the column that matters is "raw technical text shown".

| Location | Hits | Raw technical text shown | Disposition |
|---|---|---|---|
| `ErpWeb.Core` scoped stock/cost files | 45 | 0 | Closed � every ledger/exceptions path either attaches a structured error or is a service-authored business sentence |
| `ErpWeb.Core` producers forwarding `*Exception.Message` to `Fail(...)` | 24 | 0 | Accepted � business wording by design (e.g. stale-document explanations) |
| `ErpWeb.Core` business guards (`InvalidOperationException`) | 8 | 0 | Accepted � suppressing these would hide real validation |
| `ErpWeb.Core` lookup/paging (`LargeLookupPage.Fail`) | 4 | 0 | Accepted � read-only lookup, no posting state |
| `ErpWeb.UI` transaction pages | 40 | 0 | Closed for post/rollback; remaining are save/delete/lookup popups |
| `ErpWeb.UI` Sales/Purchase e-Invoice transport | 12 | 0 (transport text) | Out of scope � explicitly excluded by the plan |
| `ErpWeb.UI` Inventory entry popups | 4 | 0 | Out of scope � popup lot/lookup action, never `PostAsync`/`RollbackAsync` |
| `ErpWeb.UI` Production save/delete/lookup | 15 | 0 | Out of scope � the post/rollback paths on those pages were converted |
| `ErpWeb.UI` `PrMachineSchedulePlanner` | 7 | 0 | Recorded only (E-14) |

Verification commands and results:

```powershell
# no stack traces or exception dumps anywhere in the UI
Select-String -Path (Get-ChildItem ErpWeb.UI -Recurse -Include *.cs,*.razor).FullName `
  -Pattern "StackTrace|ex\.ToString\(\)|Exception\.ToString\("        # ? 0 matches

# protected posting/costing files untouched
git diff --name-only | Select-String `
  "StockReplayPhysicalPlanner|StockReplayPhysicalTimelineReader|InventoryValuationService|StockReplayFifo|StockPostingCoordinator|StockPostingGuards|IvBalLoc|IvTrxHistory"
                                                                      # ? 0 matches
```

`IvStockCountService` is modified (five `catch (StockLedgerException)` blocks) but additively: the
original `Fail(ex.Error.Message)` argument is preserved and `OperatorError` is an optional extra.

Still open at the end of Phase 6: E-02/E-03 numbering text, E-14 scheduling, and the UI save/delete
popups above. None of them can display a stack trace or raw SQL.

