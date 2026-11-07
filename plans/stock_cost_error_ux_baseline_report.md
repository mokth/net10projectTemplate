# Stock / Cost / Quantity Operator-Error — Phase 0 Baseline Report

**Plan:** `plans/productionv2_ERP_FRIENDLY_STOCK_COSTING_ERROR_MESSAGES_STRICT_AI_CODING_AGENT_PLAN_v1.md` (Phase 0)
**Branch:** `productionv2` · **HEAD:** `371725c0bda74e386a66bfa3c7559c6f227a14dd`
**Phase 0 outcome:** **PASS** (with two scope corrections, §10)
**Business code changed in Phase 0:** none. Only files created: this report, the error-site register, one CSV appendix, and the plan text itself.

---

## 1. LIVE-HEAD PROOF

```
$ git rev-parse --abbrev-ref HEAD   -> productionv2
$ git rev-parse HEAD                -> 371725c0bda74e386a66bfa3c7559c6f227a14dd
$ git rev-parse origin/productionv2 -> 371725c0bda74e386a66bfa3c7559c6f227a14dd   (after git fetch)
$ git log --oneline -3              -> 371725c checked in
                                       6cc1cf5 checked in stock master active control and validation
                                       f9992d8 checked in
```

The local HEAD is **identical** to the baseline inspected by the plan. No commits exist on `origin/productionv2` that are missing locally; there is nothing to rebase and no concurrent commit to merge.

### 1.1 Working-tree deviations (recorded, not touched)

| Path | State | Assessment |
|---|---|---|
| `ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs` | modified | `GetEffectiveMinWidth` returns hard-coded `20` instead of `GridColumnSizing.GetEffectiveMinWidth(column)` (**+2/−2**). Unrelated to stock/costing. **Left untouched.** |
| `.replay-production-fifo-0d718e43/` | untracked, 3 797 files | Prior-agent repository snapshot (includes copies of `StockReplayFifo*`, `IvInventoryPostingService*`). Excluded from every count to prevent double-counting. Indicates active FIFO-replay investigation — **treat all replay files as owned by concurrent work.** |

No file under `ErpWeb.Core/StockLedger`, `ErpWeb.Core/Inventory`, `ErpWeb.Core/Production`, `ErpWeb.Core/Purchase` or `ErpWeb.Core/Sales` is dirty. Nothing was reverted, staged or committed.

### 1.2 Solution / SDK

* Solution: `ErpWeb.slnx` (SDK-style solution).
* SDK available: **10.0.402**; target framework `net10.0`.
* Test project: `ErpWeb.Tests/ErpWeb.Tests.csproj` — xunit 2.9.3, EF Core **Sqlite** 10.0.0, Moq. 253 `*Tests.cs` files.

---

## 2. BUILD BASELINE (executed)

```
dotnet build ErpWeb.slnx -c Release --nologo -v m
```

| Metric | Result |
|---|---|
| Result | **Build succeeded** |
| Errors | **0** |
| Warnings | **3 385** (pre-existing nullable/obsolete warnings, e.g. `CS8600/CS8602/CS8604`, `CS0108`) |
| Elapsed | **00:02:09.67** |

This is the parity reference: any later phase must still build with 0 errors. Warning count is **not** a gate (it is dominated by pre-existing nullable noise) but should not grow materially.

---

## 3. TEST BASELINE (executed where runnable)

### 3.1 Targeted ledger invariants — **PASS**

```
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj -c Release --no-build --nologo \
  --filter "FullyQualifiedName~StockReplayPhysicalSimulator|FullyQualifiedName~StockLedgerInfrastructure"
```

```
Passed! - Failed: 0, Passed: 31, Skipped: 0, Total: 31, Duration: 24 s
```

This covers the exact invariant the reported bug depends on (`StockReplayPhysicalSimulatorTests`, including the later-issue-makes-negative cases) plus the ledger infrastructure contract (`StockLedgerInfrastructureTests`). These tests must remain **green and unchanged** through every phase.

### 3.2 Full suite — **INCOMPLETE (NOT RUN to completion)**

```
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj -c Release --no-build --nologo
```

| Observation | Value |
|---|---|
| Started | 10:54:11 local |
| Observed failures logged | **27**, all in `Sales.Master`, `Planning.Master`, `Procurement.Inquiry`, `Sales.Transaction` (e-invoice UBL) |
| Failures in Inventory / StockLedger / Production stock suites | **0 observed** |
| Outcome | **Aborted by the operator after ≈13 min.** The `testhost` process (PID observed) had accumulated ~5 974 s of CPU time while producing no output for ≈8 min after the last logged test (`ProductDefinitionSnapshotLoaderTests.No_active_revision_reports_not_found`, xUnit elapsed 00:10:22). This is a **pre-existing hang/spin in the test suite**, not in product code exercised by this plan. |

**Status: NOT RUN / INCOMPLETE.** It is recorded as a blocker for Phase 6 parity evidence, not as a Phase 0 gate failure. Phase 6 must either (a) obtain a stable full-suite run, or (b) use the per-category filters below to produce comparable before/after evidence.

Suggested stable reproduction for later phases:

```powershell
# stock/costing/ledger areas only — the areas this plan can affect
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj -c Release --no-build --nologo `
  --filter "TestCategory=Inventory|FullyQualifiedName~Production|FullyQualifiedName~StockLedger"

# everything except the known-slow/hanging master & scheduling areas
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj -c Release --no-build --nologo `
  --filter "FullyQualifiedName!~Planning.Master&FullyQualifiedName!~Sales.Master"
```

### 3.3 Observed pre-existing failures (report only — must not be fixed here)

These failed on **unmodified HEAD** and are unrelated to stock/costing error presentation:

| Area | Tests observed failing |
|---|---|
| `Sales.Master.SaCustServiceTests` | `AppInvoice_On_CopiesGeneralToInvoice`, `Salesman_LegacyValue_ToleratedOnUnrelatedEdit`, `Update_PreservesLeftoverBranchCode`, `PhoneOnly_EmptyGlCode_OnEdit_Succeeds`, `PaymentFieldChange_RequiresGlCode`, `AppInvoice_Off_GeneralChange_LeavesInvoiceUnchanged`, `ConcurrencyFail_NoChildOrHeaderPersist` |
| `Planning.Master` | `PrProductDefServiceTests.Circular_bom_rejected_on_save`, `Overlapping_active_versions_rejected`; `PrBomStructureTreeTests` (6 cases); `BomExplosionServiceTests.Make_without_bom_is_hard_error`; `ProductDefinitionSnapshotLoaderTests` (2 cases, one `NullReferenceException`, one wrong error code) |
| `Procurement.Inquiry` | `PoPurchaseInquiryWorkbenchTests` (2), `PoProcurementKpiServiceTests.PriceMovement_IsUnavailable_WhenLatestSamplesUseDifferentPurchaseUom` |
| `Sales.Transaction` (e-invoice) | `SaEInvoiceSelfBillAndTinTests.Self_billed_types_use_the_note_and_invoice_ubl_roots` (codes 12 and 13) |

**None of these may be "fixed" inside this work stream.** They are recorded so Phase 6 does not misattribute them to message work.

---

## 4. CONTRACTS CAPTURED (verbatim, must be preserved)

### 4.1 Stable error type — `ErpWeb.Core/StockLedger/StockLedgerErrors.cs`

```startLine:27:38:ErpWeb.Core/StockLedger/StockLedgerErrors.cs
public sealed record StockLedgerError(string Code, string Message, bool Retryable = false);

public sealed class StockLedgerException : Exception
{
    public StockLedgerException(StockLedgerError error)
        : base(error.Message) => Error = error;

    public StockLedgerError Error { get; }
}
```

21 constants, values unchanged (`LEDGER_DISABLED` … `COST_METHOD_NOT_IMPLEMENTED`).

### 4.2 Inventory posting result — `ErpWeb.Core/Inventory/IIvInventoryPostingService.cs`

* `IvInventoryPostingResult`: `Succeeded`, `ErrorMessage`, `SucceededCount`, `FailedCount`, `Batches`
* `IvInventoryPostingBatchResult`: `BatchNo`, `Succeeded`, `ErrorMessage`, `OperationId`
* `Fail(string)` → `FailedCount = 1`, **no batches**
* `FromBatches` → joins `"Batch {N}: {msg}"`, sets `ErrorMessage` only when something failed

### 4.3 Goods-receipt wrapper — `ErpWeb.Core/Inventory/IIvGoodsReceiptService.cs:27-34`

`OkPosting` copies `Succeeded`, `ErrorMessage`, `SucceededCount`, `FailedCount`, `Posting`. **All eight inventory services have an equivalent `OkPosting`** (register A-04), which is why one additive property is sufficient.

### 4.4 Per-batch semantics that must be provably unchanged

| Property | Definition at HEAD | Parity test |
|---|---|---|
| `Succeeded` | `fail == 0` | count + boolean snapshot |
| `SucceededCount` | count of `Batches` with `Succeeded` | exact |
| `FailedCount` | `batches.Count - ok` | exact |
| Order | input `batchNos.Distinct()` order, one result per batch | exact |
| All-failed text | `"Batch {N}: {msg}"` | text may change; **prefix must remain truthful** |
| Partial text | `"Posted/Rolled back {ok} of {n}. …"` | same numbers |
| `Fail(msg)` result | `FailedCount = 1`, `Batches = []` | exact |
| DB effect | same rows written/rolled back per batch | snapshot diff |

### 4.5 Error boundary code (the one permitted edit point)

```startLine:342:358:ErpWeb.Core/Inventory/IvInventoryPostingService.cs
            catch (DbUpdateConcurrencyException)
            {
                results.Add(IvInventoryPostingBatchResult.Fail(batchNo, "Stock was modified by another user. Retry."));
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                results.Add(IvInventoryPostingBatchResult.Fail(batchNo, "Posting conflict (duplicate history or balance)."));
            }
            catch (StockLedgerException ex)
            {
                results.Add(IvInventoryPostingBatchResult.Fail(batchNo, ex.Error.Message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inventory {Action} failed for batch {BatchNo}", post ? "POST" : "ROLLBACK", batchNo);
                results.Add(IvInventoryPostingBatchResult.Fail(batchNo, ex.Message));
            }
```

`ex.Error.Code` and `ex.Error.Retryable` are **not** read anywhere on this path (verified by repo-wide search). Recovering them is purely additive.

### 4.6 Constants relevant to wording

| Constant | Value | File |
|---|---|---|
| `IvPostingLimits.MaxPostSelection` | `10` | `ErpWeb.Core/Inventory/IvTrxConstants.cs:30` |
| `PermissionCodes.Access` | `"ACCESS"` | `ErpWeb.Core/Menus/PermissionCodes.cs:5` |
| `PermissionCodes.Post` | `"POST"` | idem `:10` |
| `PermissionCodes.Rollback` | `"ROLLBACK"` | idem `:11` |
| `PermissionCodes.ViewCost` | `"VIEW_COST"` | idem `:23` |
| `StockBusy` message | "Another stock transaction is in progress for this branch. Retry the command." | `BranchStockTransactionLock.cs:58` |
| Period refusal | "The date {0:yyyy-MM-dd} falls in a closed inventory period (closed through {1:yyyy-MM-dd}). Reopen the period first." | `IvPeriodCloseStatuses.cs:28` |

---

## 5. THE SIX EXTERNAL BOUNDARIES (verified map)

| # | Surface | Entry point | Result carrier | UI |
|---|---|---|---|---|
| 1 | Inventory (8 doc types) | `IvInventoryPostingService.PostAsync/RollbackAsync` → `DispatchAsync` | `IvInventoryPostingResult` + 8 `OkPosting` wrappers | 8 list pages + stock count pages |
| 2 | Sales shipment | `SaDoService.PostAsync/RollbackAsync` | `SaDoPostingItemResult` / `SaDoErrorKind` | `SaDoList`, `SaDo` |
| 3 | Purchase Invoice / CN-DN | `PoInvoiceService`, `PoCdn` services + `PurchaseReceiptCostSettlementService` | result + `ValidationErrors` dictionary | `PoInvoiceList`, `PoInvoice`, `PoCdnList`, `PoCdn` |
| 4 | Material Issue | `ProductionMaterialIssueService` | `IvMasterOperationResult` + `Message` | `PrMaterialIssueList`, `PrMaterialIssueEntry` |
| 5 | Daily Production / FG | `ProductionOutputService`, `ProductionFinishedGoodReceiptService` | `Message` + `ReadinessErrors` | `PrDailyProduction*`, `PrFinishedGoodReceipt*` |
| 6 | Production Stock Return / Adjustment / WOT | `ProductionStockReturnService`, `ProductionStockAdjustmentService`, `ProductionWorkOrderTransferService` | `Message` + `ReadinessErrors` (+ `ex.Code`) | entry + list pages |

---

## 6. CONCURRENCY, LOCKS AND WHY ENRICHMENT MUST BE OUTSIDE THEM

* Per-branch serialisation lives in `ErpWeb.Core/StockLedger/BranchStockTransactionLock.cs`; failure text is `STOCK_BUSY` (Retryable: true).
* Posting/rollback commands open an EF transaction, take the branch lock, write history/balances, then commit; refusal paths roll back and return a `Fail(...)` result.
* The plan forbids read-enrichment while a lock/transaction is held. Verified: **no existing code path performs an error lookup after a failure** — enrichment is genuinely new and must be added after the transaction has completed (post-commit or post-rollback), using a separate `AsNoTracking` read.
* Any enrichment must carry the trusted `CompanyCode` + `BranchCode` from `_tenant.TryWriteScope()` (used at `IvInventoryPostingService.cs:269`), never values echoed from the browser.

---

## 7. REUSE ANALYSIS — WHERE `ErpOperatorError` SHOULD LIVE

| Candidate | Verdict |
|---|---|
| `ErpWeb.Core/Services/` | ✅ Preferred. Already hosts `ValidationMessageFormat`, a deliberately EF-free, UI-free, unit-testable formatter. Same layering; no new dependency direction. |
| New `ErpWeb.Core/Errors/` folder | ✅ Acceptable and arguably clearer. Either is compliant; **do not** put it in `ErpWeb.UI` (Core must not depend on Blazor) and **do not** put it in `ErpWeb.Model` (entity layer). |
| `ErpWeb.Core/StockLedger/` | ❌ Avoid: implies the type belongs to ledger semantics rather than presentation. |
| Extending `ValidationMessageFormat` | ⚠️ Reuse it for **field/line** text only. Do **not** overload it with stock/cost classification — it is authoritative for validation dictionaries and must not acquire a second job. |

**Existing pieces the presenter must reuse rather than duplicate:**

| Piece | Path | Use |
|---|---|---|
| Validation headline/dictionary | `ErpWeb.Core/Services/ValidationMessageFormat.cs`, `ErpWeb.UI/Components/Pages/PageBase.cs:44-67` | keep untouched; do not reclassify field errors as costing errors |
| Error alert style | `iv-toast iv-toast--err` (`IvGoodsReceiptList.razor:15-21`) | reuse for the compact message; extend with a details region inside the same block |
| Source-document navigation | `ErpWeb.UI/Inventory/Inquiry/CostingSourceNavigationResolver.cs` (`Resolve`, `PhysicalLink`) | only place routes may be composed; permission check stays in UI |
| Diagnostic destination | `ErpWeb.UI/Inventory/Inquiry/IvCostingCenter.razor` | optional "authorised user" next step; no new screen |
| Permission checks | `IAccessRights.CanAsync(menuCode, permission)` as used at `IvInventoryPostingService.cs:291` | gate links and cost detail |

**Naming caution:** `IvInventoryPostingBatchResult.Fail(int, string)` has 348+ call sites. A new optional property (`OperatorError`) must not disturb that factory; batch-level error data should be created by an explicit `WithOperatorError(...)`/`init` assignment, not a changed constructor.

---

## 8. TEST PREREQUISITES AND COVERAGE GAPS

### 8.1 Existing coverage that must stay green (unchanged)

| Test file | Guards |
|---|---|
| `ErpWeb.Tests/Inventory/Transaction/StockReplayPhysicalSimulatorTests.cs` | later-issue negative replay, count recomputation, ordering (31-test run above) |
| `ErpWeb.Tests/Inventory/Transaction/StockReplayValuationGenerationResolverTests.cs` | generation resolution |
| `ErpWeb.Tests/Inventory/Transaction/IvStockCountLedgerAuthorityTests.cs` | count authority (66 code refs) |
| `ErpWeb.Tests/Production/Transaction/*` | FG/MI/return/WOT costing parity |
| `ErpWeb.Tests/Other/StockLedgerInfrastructureTests.cs` | code catalogue + infrastructure |

### 8.2 Gaps the implementation phases must fill

| Gap | Needed test |
|---|---|
| No test asserts the **operator-visible** text of a failed GRN rollback | T-01 (register §12) |
| No test asserts `Batch` count/order under a *mixed* success | T-04 |
| No test asserts `StockLedgerError.Code` survives the dispatch boundary | new boundary test (currently impossible — it is dropped) |
| No test asserts absence of forbidden vocabulary | presenter vocabulary test (T-11) |
| No test asserts `VIEW_COST` masking of error details | T-10 |
| No test asserts per-batch detail is bounded (≤10 selected today) | T-05 + detail cap test |

### 8.3 Prerequisites for Phase 2 integration-style proof

* The suite is SQLite-based (`Microsoft.EntityFrameworkCore.Sqlite`), so an in-memory/SQLite fixture is sufficient for a **transactional** GRN rollback proof; no SQL Server is required for the mandated tests.
* SQL Server-specific tests exist separately; they were **NOT RUN** in Phase 0.
* No UAT/production data was available → all UAT scripts in later phases are **NOT RUN** until a human executes them.

---

## 9. BUSINESS-PARITY BASELINE STATEMENT

Nothing changed, so parity holds trivially. The reference for later diffing is:

* HEAD `371725c0bda74e386a66bfa3c7559c6f227a14dd`
* Build: 0 errors / 3 385 warnings
* Targeted ledger tests: **31/31 pass**
* No DB snapshot was taken (no database was touched or provisioned). Phase 6 must capture before/after snapshots of `IvBalLoc`, `IvTrxHistory`, `StockPosting*`, `StockValuationFact`, FIFO layers, WAC/Standard state, PI settlement and production WIP on an equivalent dataset.

---

## 10. GATE CHECKLIST

| Gate requirement | Evidence | Status |
|---|---|---|
| Live HEAD verified, drift reported | §1 (identical SHA; 1 unrelated dirty file; 1 untracked snapshot) | **PASS** |
| Every scope-relevant user-visible error site classified and linked to a UI consumer | Register A/B/C + CSV appendix (199 producer rows) | **PASS** |
| Known high-risk page not missing | 8 inventory lists, stock count (2), GRN entry, Sales DO/Invoice/CDN, Purchase PI/CDN, Production MI/Daily/FG/Return/Adj/WOT all present | **PASS** |
| Technical `ex.Message` exposure dispositioned | Register E (34 in-scope sites) + out-of-scope list | **PASS** |
| Baseline error contract captured | §4 | **PASS** |
| Reference tests enumerated | §3, §8 | **PASS** |
| No C# or business logic changed | `git status` shows only files under `plans/` added | **PASS** |
| Build parity | 0 errors at baseline | **PASS** |

### 10.1 Two corrections to the plan's assumptions (not gate failures)

1. **Five codes are unreachable.** `COUNT_SCOPE_FROZEN`, `COUNT_SNAPSHOT_CHANGED`, `COST_METHOD_NOT_IMPLEMENTED`, `HISTORY_BEFORE_CUTOVER`, `LEGACY_LINEAGE_UNVERIFIED` have **no producer** in any project. Catalogue families 27, 28 (and the cutover half of 11) must be implemented as **reserved presets**, not as live UI paths.
2. **The stock-freeze guard is a stub.** `NoActiveStockFreezeGuard` (`StockPostingGuards.cs:45-53`) permanently returns `null`. No operator may be told "a stock count is in progress" as a cause.

Neither correction authorises any behaviour change; they prevent the implementation from inventing causes that cannot occur.

---

## 11. PHASE 0 — FILES CREATED (all documentation)

```
plans/productionv2_ERP_FRIENDLY_STOCK_COSTING_ERROR_MESSAGES_STRICT_AI_CODING_AGENT_PLAN_v1.md
plans/stock_cost_error_ux_error_site_register.md
plans/stock_cost_error_ux_baseline_report.md
plans/stock_cost_error_ux_ledger_producer_appendix.csv
```

No `.cs`, `.razor`, `.csproj`, SQL, migration or configuration file was created or modified.

---

## 12. REPRODUCE THIS PHASE

```powershell
git rev-parse HEAD
git status --porcelain
dotnet build ErpWeb.slnx -c Release --nologo -v m
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj -c Release --no-build --nologo `
  --filter "FullyQualifiedName~StockReplayPhysicalSimulator|FullyQualifiedName~StockLedgerInfrastructure"
rg -c "StockLedgerErrorCodes\." ErpWeb.Core -g '!**/obj/**'
rg -n "ex\.Message" ErpWeb.Core/Inventory ErpWeb.Core/Sales ErpWeb.Core/Purchase ErpWeb.Core/Production
```

---

**STOP.** Phase 0 ends here. Phase 1 (pure presenter + tests) must not begin without an explicit gate approval.
