# Phase 6 report — Repository-wide safety verification and UAT

**Date:** 2026-10-29
**Repo:** `mokth/net10projectTemplate` · **Branch:** `productionv2` · **Baseline SHA:** `371725c`
**Scope executed:** PHASE 6 only (plus the remediation its own census required).
**Gate status:** PASS with explicitly recorded NOT RUN items (no SQL Server test instance was available).

---

## 1. What Phase 6 changed

The census found three gaps left by Phases 1–5. All three are presentation/diagnostics only;
no posting decision, guard, rounding, quantity, cost, status, lock or transaction boundary changed.

| Gap | Remediation |
|---|---|
| **G-1** Production never produced a support reference and never logged one, so the banner's "Reference" field was empty and support had nothing to search for | New `ErpWeb.Core/Production/ProductionOperatorErrorLog.cs` (internal) generates the reference, writes one `LogError` with the original exception/code/phase/retryable, and returns the same `ErpOperatorError` shape. All 18 Production producer sites now use it |
| **G-2** `IvInventoryPostingService` fell back to `Fail(batchNo, ex.Message)` for any non-ledger exception, i.e. raw exception text on an ordinary stock screen | The catch now logs with a reference; a business guard (`InvalidOperationException` / `ArgumentException`) keeps its own wording (no structured error, so the page's existing message wins), and an unknown failure gains an `Unexpected` operator error + reference. Batch counts and flags untouched |
| **G-3** The Production services had no logger at all | Optional trailing `ILogger<T>? logger = null` on `ProductionMaterialIssueService` (both constructors), `ProductionOutputService`, `ProductionStockReturnService`, `ProductionStockAdjustmentService`, `ProductionWorkOrderTransferService`; the FG-receipt logger parameter that existed but was unused is now wired. Optional, so every existing test/manual construction still compiles and DI supplies it |

Files touched by this remediation:

```
ErpWeb.Core/Production/ProductionOperatorErrorLog.cs                 (new)
ErpWeb.Core/Production/ProductionMaterialIssueService.cs             (ctor)
ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.Rollback.cs
ErpWeb.Core/Production/ProductionOutputService.cs                    (ctor)
ErpWeb.Core/Production/ProductionOutputService.Posting.cs
ErpWeb.Core/Production/ProductionOutputService.Rollback.cs
ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.Posting.cs
ErpWeb.Core/Production/ProductionStockReturnService.cs               (ctor)
ErpWeb.Core/Production/ProductionStockReturnService.Posting.cs
ErpWeb.Core/Production/ProductionStockAdjustmentService.cs
ErpWeb.Core/Production/ProductionWorkOrderTransferService.cs
ErpWeb.Core/Production/ProductionWorkOrderTransferService.Rollback.cs
ErpWeb.Core/Inventory/IvInventoryPostingService.cs                   (non-ledger catch only)
```

---

## 2. Error-site census (re-run on the current tree)

Command shape:

```powershell
Select-String -Path (Get-ChildItem ErpWeb.Core -Recurse -Filter *.cs).FullName -Pattern "ex\.Message"
Select-String -Path (Get-ChildItem ErpWeb.UI   -Recurse -Filter *.cs).FullName -Pattern "ex\.Message"
```

| Measure | Phase 0 | Now |
|---|---|---|
| `ex.Message` hits in `ErpWeb.Core` (all modules) | 107 (scoped census) | 153 (whole assembly) |
| `ex.Message` hits in scoped stock/cost files | 34 | 45 |
| `ex.Message` hits in transaction UI pages | 34 register entries | 40 |
| Scoped sites that are raw technical text | 34 | **0** |

Why the raw count is 0 while the string count is not:

| Kind of remaining hit | Count | Why it is safe |
|---|---|---|
| `TransferException` / `ReturnException` / `AdjustmentException` message forwarded to `Fail(...)` | 24 | These are service-authored business sentences (e.g. "Source WIP changed after this transfer was prepared…"), not exception text from a library |
| `InvalidOperationException` business guard forwarded to `Fail(...)` | 8 | Pre-existing validation wording; hiding it would suppress real business validation |
| Stock-return readiness text `"Line {item}: {reason}"` | 5 | Readiness explanation for a NEW draft, produced by business guards |
| Lookup / paging `LargeLookupPage.Fail(ex.Message)` | 4 | Read-only lookup failure, no posting state |
| Sales / Purchase e-Invoice transport catches (`SaCdnList`, `SaInvoiceList`, `SaEInvoice*`, `PoSb*`) | 12 (UI) | e-Invoice transport, explicitly out of the stock/cost scope |
| Inventory entry popup errors (`IvStockTransfer`, `IvStockReturn`, `IvMiscReceipt`) | 4 (UI) | Popup-level lot/lookup action, never `PostAsync`/`RollbackAsync` |
| Production entry save/delete/lookup catches (`PrProduction*Entry`, `PrWorkOrderTransferEntry`, `PrFinishedGoodReceiptEntry` cost trace) | 15 (UI) | Save/delete/lookup only; the four post/rollback paths were converted in Phase 5 |
| Scheduling (`PrMachineSchedulePlanner`) | 7 (UI) | Register E-14, out of scope |

Register disposition after Phase 5 + 6:

| Register | Status |
|---|---|
| B-01…B-10 (Inventory lists/stock count) | Done (Phase 4) |
| C-01…C-16 (cross-module consumers) | Done (Phase 5) except the documented lookup/e-Invoice rows above |
| E-01 (posting dispatch) | Done (Phase 6, G-2) |
| E-02 / E-03 (GRN lot allocation, line validation) | Reviewed: business numbering text, no stack/SQL; recorded as safe |
| E-04…E-12 (cross-module producers) | Done (Phase 5); support reference added (Phase 6, G-1) |
| E-13 (UI rethrow) | Done (Phase 5) |
| E-14 (scheduling) | Recorded only, out of scope |
| Register F (internal-only producers) | Untouched, as required |

No page contains `StackTrace`, `ex.ToString()` or `Exception.ToString()`:

```powershell
Select-String -Path (Get-ChildItem ErpWeb.UI -Recurse -Include *.cs,*.razor).FullName `
  -Pattern "StackTrace|ex\.ToString\(\)|Exception\.ToString"   # → no matches
```

---

## 3. Build and test (actually executed)

| Command | Result |
|---|---|
| `dotnet build ErpWeb.slnx -c Release` | **0 errors**, 115 warnings (all pre-existing categories; e.g. `CS0108` in `IvLotGenealogyInquiry`) |
| `dotnet test ErpWeb.Tests\ErpWeb.Tests.csproj -c Release --no-build` | **4747 passed, 27 failed, 8 skipped, 4782 total**, 16 m 29 s (verified again on the current tree) |
| `dotnet test … --filter "…InventoryPosting|…GoodsReceipt|…StockCount|…Production|…OperatorError"` | 1075 passed, 1 failed (the pre-existing phantom-BOM test), 8 skipped |
| `dotnet test … --filter FullyQualifiedName~PrCrossModuleOperatorErrorTests` | 19/19 passed |
| SQL Server suites | **8 skipped** — `ERPWEB_PRODUCTION_STOCK_TEST_CONNECTION` is not set in this environment |

### The 27 failures are pre-existing, proven

A throw-away `git worktree` at baseline `371725c` was built and the same nine test classes were run:

```
baseline:  Failed 27, Passed 205, Skipped 0
current :  Failed 27, Passed 205, Skipped 0
Compare-Object <baseline names> <current names>   → no differences
```

They are BOM structure/product definition, customer master, purchase KPI/inquiry workbench,
e-Invoice self-billed UBL roots and one nested-phantom snapshot test. Not fixed here (Phase 6 may not
change costing/BOM/customer logic). They are listed in §8 for the release package.

---

## 4. Guardrail diff audit

Tracked working tree: `106 files changed, 2516 insertions(+), 335 deletions(-)`, plus 24 untracked
new files (the operator-error model/presenter/helpers, the failure-evidence reader, the new test classes
and the phase reports) — this includes
**pre-existing uncommitted work from another stream** that was already in the tree before this work
stream started. The two groups are separated below so the audit is honest.

### 4.1 Pre-existing, NOT part of this work stream (verified by diff content)

| File group | Evidence it is another effort |
|---|---|
| `IvInventoryPostingService.WacReplay.cs`, `.StandardMrReplay.cs`, `.WacProductionReplay.cs`, `.StandardTransferReplay.cs`, `IvInventoryPostingService.StandardProductionReplay.cs` | Adds `batch.TrxDtTime` arguments and a `productionPlan` parameter to backdated replay — a functional replay change, unrelated to messages. Not mentioned by any phase report |
| `ProductionIssueEffectiveCostReader.cs`, `ProductionReplayEffectiveCostReader.cs`, `ProductionOutputIssueCostCorrectionWriter.cs` | Production replay cost correction feature. The reader also gains stricter `WorkOrderId` / `WorkOrderOperationId` / `PostingLinkId` guards |
| `ErpWeb.Core/Production/ProductionPostingInvariant.cs` | **Costing behaviour change:** reversal cost compared against `ProductionReplayEffectiveCostReader` effective cost instead of the source row |
| `ErpWeb.Core/Production/ProductionOutputService.Rollback.cs` | **Mixed file — costing behaviour change present.** Reversal writes `effective.TotalCost`/`UnitCost` (pool, consumption evidence, produced output) instead of the original movement's values, plus new reconciliation guards. The `ProductionOperatorErrorLog` catch blocks in the same file belong to this stream; the cost hunks do not |
| `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs` | Local min-width edit (flagged in the Phase 3 report) |
| `plans/productionv2_backdated_replay_impact_manifest.md`, `plans/productionv2_replay_architecture_assessment.md` | Same unrelated effort |

**This matters for the release:** these files must be reviewed/attributed separately, because their
diffs *do* touch replay behaviour and are not covered by this work stream's no-behaviour-change claim.

### 4.2 This work stream — posting/costing decision code

Within the hunks authored by this stream, the only decision-adjacent edit is the one the plan explicitly
allowed (metadata at an existing throw). Its complete diff:

```diff
         if (quantity < 0m)
             throw new StockLedgerException(new(
                 StockLedgerErrorCodes.InsufficientBaseQty,
-                $"Physical replay would make the stock slice negative at event {eventId} while {phase}."));
+                $"Physical replay would make the stock slice negative at event {eventId} while {phase}.")
+            {
+                Phase = phase switch { "removing" => …, "rewinding" => …, "replaying" => …, _ => … },
+                EventId = eventId
+            });
```

Same condition, same code, same message, same caller. The added members are optional metadata read only
by the presenter.

### 4.3 Files that must NOT change and did not

```
ErpWeb.Core/StockLedger/StockReplayPhysicalPlanner.cs
ErpWeb.Core/StockLedger/StockReplayPhysicalTimelineReader.cs
ErpWeb.Core/StockLedger/InventoryValuationService.cs
ErpWeb.Core/StockLedger/StockReplayFifo*.cs (40 files)
ErpWeb.Core/StockLedger/StockPostingCoordinator.cs
ErpWeb.Core/StockLedger/StockPostingGuards.cs
ErpWeb.Core/Inventory/IvBalLoc*.cs
ErpWeb.Core/Inventory/IvTrxHistory*.cs
```

`git diff --name-only` matches none of them (verified: 0 matches).

### 4.3.1 Stock-count service: in scope, additive only

`IvStockCountService.cs` (plus its interface, UI pages and test) *is* modified — stock count was in
scope from Phase 4 (Register B), so this is expected rather than an exception. The complete core diff is
metadata at five existing `catch (StockLedgerException)` blocks:

```diff
             catch (StockLedgerException ex)
             {
-                return IvStockCountOperationResult.Fail(ex.Error.Message);
+                var reference = IvInventoryOperatorErrors.NewReferenceId();
+                _logger.LogWarning(ex,
+                    "Stock count {CountNo} post rejected: {Code} (ref {Reference})",
+                    no, ex.Error.Code, reference);
+                return IvStockCountOperationResult.Fail(ex.Error.Message,
+                    IvInventoryOperatorErrors.FromLedgerError(
+                        ex.Error, ErpDocumentKind.StockCount, no,
+                        ErpErrorAction.Post, reference));
             }
```

Same catches, same condition, same `Fail(...)`, same first argument (the original
`ex.Error.Message`), so any caller that ignored `OperatorError` behaves exactly as before. The only new
API member is an optional parameter: `Fail(string, ErpOperatorError? = null)`. Five sites in total
(generate, candidates, post preview, post, rollback).

### 4.4 Validation strings preserved

A `git diff -U0` scan for removed `ErrorMessage = "…"`, `ValidationErrors[…]`, `Fail("…")` lines returns
only false positives where the same literal is re-emitted inside the new structured call
(verified line-by-line, e.g. `"Posting conflicted with another change; reload and retry."` and
`"Rollback reason is required."`). No user-facing validation text was deleted.

---

## 5. Invariant matrix

| Area | Assertion | Status |
|---|---|---|
| `INSUFFICIENT_BASE_QTY` historic negative | Same rejection, same code, status unchanged | **PASS** — existing replay test still asserts the original message and the new test asserts the same code reaches the presenter |
| Original event history | No extra/missing/changed rows | **PASS (automated unit/integration)**; DB row snapshot **NOT RUN** |
| Physical balance (`IvBalLoc.StdQty`, RowVersion) | Identical | **PASS (existing suites)**; DB snapshot **NOT RUN** |
| FIFO | Same layers/allocation/valuation | **PASS (existing suites)**; DB snapshot **NOT RUN** |
| WAC | Same moving average and corrections | **PASS (existing suites)**; DB snapshot **NOT RUN** |
| Standard | Same revision selection/variance | **PASS (existing suites)**; DB snapshot **NOT RUN** |
| Stock counts | Same freeze/reset/history | **PASS (existing suites, incl. count service)** |
| Procurement | Same GRN-PO, PI settlement, CN/DN | **PASS (existing suites)** |
| Sales | Same DO/Invoice shipment, reservation, COGS | **PASS (existing suites)** |
| Production | Same IP, DP, FG, return, adjustment, WO transfer | **PASS (existing suites)** |
| Concurrency | Same locks/retry/idempotency | **PASS (existing suites)**; SQL concurrency suite **NOT RUN** |
| Multi-batch | Same counts/flags/order, meaningful per-batch UI | **PASS** — partial-rollback parity test + new banner tests |
| Permissions | No cost amount without `ViewCost`; no cross-branch detail | **PASS (unit)** — banner filters `IsMonetary`; role-based UAT **NOT RUN** |
| Friendly UX | What/why/next action, no internal vocabulary | **PASS** — 19 cross-module tests + presenter suite |
| Fallback | Missing structured error keeps the page's own message | **PASS** — `FromRecords`/`FromError` return `null` |
| Logging | Original code/message + reference findable | **PASS (code inspection)** — Inventory (Phase 2/6), Sales/Purchase (Phase 5), Production (Phase 6); runtime log capture **NOT RUN** |
| Existing field validation | All keys/rows/messages still available | **PASS** — §4.4 |
| Performance | No query/lock on success, bounded on failure | **PASS (inspection)** — the only additions are a GUID, one log call and (Inventory) the already-capped evidence lookup |
| Scope | No schema, no new screen, no rule change | **PASS** — §4.3, no entity/mapping/migration diff |

---

## 6. Reproducible commands for the NOT RUN items

### 6.1 SQL Server parity / concurrency suite

```powershell
$env:ERPWEB_PRODUCTION_STOCK_TEST_CONNECTION = "Server=.;Database=ErpTest;Trusted_Connection=True;TrustServerCertificate=True"
dotnet test ErpWeb.Tests\ErpWeb.Tests.csproj -c Release `
  --filter "FullyQualifiedName~SqlServer"
```

Expected: the 8 currently skipped tests run, including the double-post, racing-consumption and
schema-idempotency cases.

### 6.2 Before/after data snapshot (manual, per scenario)

For each scenario run on the **baseline build** and on the **current build** with the same seed data,
capture and compare:

```sql
SELECT b.ItemCode, b.WarehouseCode, b.LocationCode, b.LotNo, b.StockStatus, b.StdQty, b.BaseQty
FROM IvBalLoc b WHERE b.CompanyCode = @c AND b.BranchCode = @br ORDER BY b.ItemCode, b.LotNo;

SELECT h.Id, h.TrxType, h.TrxDtTime, h.BaseQty, h.TrxAmt, h.ReversalOfHistoryId
FROM IvTrxHistory h WHERE h.CompanyCode = @c AND h.BranchCode = @br ORDER BY h.Id;

SELECT * FROM StockValuationFact WHERE CompanyCode = @c ORDER BY Id;
SELECT * FROM ProductionBalLot WHERE CompanyCode = @c ORDER BY Uid;
SELECT * FROM ProductionPoolValuationRow ORDER BY Id;
```

Scenarios: GRN rollback with a later transaction (historic negative), FIFO shortage, WAC missing cost,
Standard revision missing, closed period, stock-count freeze, DO/Invoice direct posting, PI settlement,
MI post/rollback, DP post/rollback, FG post/rollback, Production Stock Return, Production Stock
Adjustment, Work Order Transfer.

### 6.3 Runtime log reference check

```text
1. Trigger a refused post (e.g. roll back a GRN that has a later issue).
2. Read the reference from the banner ("Reference: XXXXXXXXXX").
3. Search the application log for that reference:
   Select-String -Path <log files> -Pattern "<reference>"
4. Expected: one LogError/LogWarning line with the same reference plus the original code and message.
```

### 6.4 Permission matrix (manual)

| Actor | Action | Expected |
|---|---|---|
| Warehouse user, no `ViewCost` | Refused GRN rollback | Banner without any monetary fact |
| Inventory admin, `ViewCost` | Same | Same banner; monetary facts are still never supplied by the inventory presenter |
| Sales user, no Procurement access | DO post failure | No Purchase document link/number invented |
| Production user, other branch | MI post failure | No cross-branch lot/warehouse detail |

---

## 7. UAT script (warehouse + purchasing staff)

For each step the tester must be able to explain the failure in their own words **without** saying
"event 2", "epoch", "tail", "lineage", "rowversion", "slice" or "pile".

| # | Setup | Action | Expected screen | Tester question |
|---|---|---|---|---|
| U-1 | GRN posted, then a later issue for the same item | Roll back the GRN | "Cannot roll back Goods Receipt <no>." + "Removing this record would make the stock balance at its original date fall below zero." + review-later-movements step | What happened and what do you do next? |
| U-2 | Item with FIFO layers consumed | Post an issue larger than available | Shortage wording with available/required, no event ids | Which quantity is short? |
| U-3 | WAC item with no cost on the receipt | Post the PI settlement | Missing-cost wording + costing-center next step | Who fixes it? |
| U-4 | Closed period covering the document date | Post | Closed-period wording with `dd/MM/yyyy` dates | Can you retry after reopening? |
| U-5 | Daily Production with no eligible material | Post MI | "There is not enough eligible stock to issue <item> to Production." | What is the next action? |
| U-6 | FG receipt for a PO already rolled back | Roll back the FG receipt | Reconciliation wording, reference shown | Who do you give the reference to? |
| U-7 | DO with insufficient reserved stock | Post DO | Shortage wording, no cost values for a non-`ViewCost` user | Is any money shown? |
| U-8 | WIP transfer with a changed source | Post Work Order Transfer | "…changed… Refresh…" + stale-document next step | Is retrying safe? |
| U-9 | Any batch with 1 success + 1 failure | Post both | "1 of the 2 selected records could not be completed." + expandable per-record detail | Which record failed? |
| U-10 | Unknown failure (kill the DB connection during post) | Post | Generic "could not be completed" + reference; reload/confirm instruction | What do you do before retrying? |

**Status: NOT RUN** — no application instance, database or trained tester was available in this
environment. The script above is ready to execute.

---

## 8. Pre-existing failures recorded (not fixed)

The 27 baseline failures, for the Phase 7 release package:

```
ErpWeb.Tests.Procurement.Inquiry.PoProcurementKpiServiceTests.PriceMovement_IsUnavailable_WhenLatestSamplesUseDifferentPurchaseUom
ErpWeb.Tests.Procurement.Inquiry.PoPurchaseInquiryWorkbenchTests.DueSoon_SummaryAndRowsShareInclusiveSevenDayWindowAndLatestRevision
ErpWeb.Tests.Procurement.Inquiry.PoPurchaseInquiryWorkbenchTests.PoSummary_And_Presets_Overdue_Partial_Unbilled_Value
ErpWeb.Tests.Planning.Master.PrProductDefServiceTests.Circular_bom_rejected_on_save
ErpWeb.Tests.Planning.Master.PrProductDefServiceTests.Overlapping_active_versions_rejected
ErpWeb.Tests.Planning.Master.PrBomStructureTreeTests.* (7)
ErpWeb.Tests.Planning.Master.ProductDefinitionSnapshotLoaderTests.Boundary_is_half_open_inclusive_from_exclusive_to
ErpWeb.Tests.Planning.Master.ProductDefinitionSnapshotLoaderTests.No_active_revision_reports_not_found
ErpWeb.Tests.Planning.Master.BomExplosionServiceTests.Make_without_bom_is_hard_error
ErpWeb.Tests.Planning.Transaction.ProductionWorkOrderServiceTests.Nested_phantom_material_keeps_its_own_bom_revision_and_line_snapshots
ErpWeb.Tests.Sales.Master.SaCustServiceTests.* (9)
ErpWeb.Tests.Sales.Transaction.SaEInvoiceSelfBillAndTinTests.* (2)
```

None of these files is touched by this work stream; all reproduce identically at `371725c`.

---

## 9. Outstanding for Phase 7

1. Decide and document the attribution of the **pre-existing uncommitted replay work** (§4.1) before
   producing the release diff, otherwise the release audit will appear to contain replay behaviour changes.
2. Finalise `stock_cost_error_ux_error_site_register.md` with the §2 dispositions (partially updated).
3. Produce the operator message catalogue, business parity matrix and UAT evidence from real screens.
4. Run §6/§7 on an environment with SQL Server + seeded data and record PASS/FAIL/NOT RUN honestly.
