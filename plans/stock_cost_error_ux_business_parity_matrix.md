# Business parity matrix — stock / costing / quantity invariants

**Question this answers:** did the error-presentation change alter any stock, cost, quantity or status
outcome?
**Answer:** no decision path was edited; every invariant below is unchanged by construction, and where a
test proves it, the test is named. Where it could only be proven against a real database, the row says
**NOT RUN** rather than PASS.

Legend: **PASS** = automated evidence in this repo, re-run on the current tree · **PASS (code)** =
verified by diff inspection, no automated assertion · **NOT RUN** = needs a SQL Server / seeded
environment that was unavailable · **N/A** = not touched by this change stream.

---

## 1. Critical invariants (plan §6)

| # | Invariant | Before/after assertion | Status | Evidence |
|---|---|---|---|---|
| C-1 | `INSUFFICIENT_BASE_QTY` historic negative | The same GRN rollback is still rejected, with the same code and unchanged document status | **PASS** | `ErpOperatorErrorPresenterTests` (code → family, code never reaches operator text); existing replay tests still assert the original `StockLedgerError.Message` |
| C-2 | Original event history | No extra/missing/changed `IvTrxHistory` rows because of an error explanation | **PASS (code)** + DB snapshot **NOT RUN** | `ProductionPostingInvariant`-adjacent and posting tests pass unchanged; no write path added (`ErpOperatorErrorPresenter` is pure, no `Db`, no clock, no RNG) |
| C-3 | Physical balance | Same `IvBalLoc.StdQty` per item/warehouse/location/lot/status and same `RowVersion` write behaviour | **PASS (code)** + DB snapshot **NOT RUN** | `IvInventoryPostingService*` diffs add no quantity arithmetic; 8 SQL Server tests **skipped** |
| C-4 | FIFO | Same layers, balances, allocation, receipt/issue valuation and historical replay | **PASS (code)** + DB snapshot **NOT RUN** | `StockReplayFifo*.cs` (40 files) **unchanged** (`git diff --name-only` = 0 matches) |
| C-5 | WAC | Same moving average, quantities, values and cost corrections | **PASS (code)** + DB snapshot **NOT RUN** | `InventoryValuationService.cs` **unchanged**; `ProductionPostingInvariant` cost-comparison change belongs to the *unrelated* replay stream (see implementation report §2), and is not part of this change |
| C-6 | Standard | Same revision/date selection, amount and variance | **PASS (code)** | Standard-cost resolution files unchanged; only the presenter maps `COST_POLICY_*` / `VALUATION_REQUIRED` to text |
| C-7 | Stock counts | Same resets/adjustments, existing guard/freeze, month-end rules, historical count behaviour | **PASS** | `Category=Inventory` suite (incl. `IvStockCountServiceTests`) passes; `IvStockCountService` diff is `Fail(message, operatorError)` only |
| C-8 | Procurement | Same GRN-PO quantity, PI settlement, CN/DN adjustment and reversal | **PASS** | Procurement suite matches baseline; only the 3 documented pre-existing inquiry/KPI tests fail (§5) |
| C-9 | Sales | Same DO/Invoice shipment, reservation, posting status, direct-document flows, COGS | **PASS** | `SaDoService`/`SaInvoiceService`/`SaCdnService` diffs are catch-only; Sales failures = the 11 pre-existing `SaCustServiceTests` + self-billed UBL tests (§5) |
| C-10 | Production | Same IP, Daily Production, FG, return, adjustment, WO transfer and WIP calculations | **PASS for this stream** — but see §2 note | `PrCrossModuleOperatorErrorTests` (19) + existing Production suite; 13 service diffs are `catch` blocks and optional logger parameters. **`ProductionOutputService.Rollback.cs` also contains another stream's effective-cost writes**, so the file is not presentation-only |
| C-11 | Concurrency | Same lock/isolation, retry/idempotency decisions; no new races | **PASS (code)** + SQL concurrency **NOT RUN** | No lock acquired on success; failure path adds only a GUID + one log call |
| C-12 | Multi-batch | Identical `SucceededCount`, `FailedCount`, per-batch flags and ordering; meaningful per-batch UI | **PASS** | `ErpOperatorFailureView.FromRecords` tests + existing partial-rollback parity test |
| C-13 | Permissions | Cost amounts masked without `ViewCost`; no unpermitted links or cross-branch detail | **PASS** | Presenter drops `IsMonetary` details when `!CanViewCost`; `IvOperatorFailureViewTests`; role UAT **NOT RUN** |
| C-14 | Friendly UX | What, why, affected document, next action understandable without source | **PASS** | Forbidden-vocabulary regex over every family; 48-family text table in the message catalogue |
| C-15 | Fallback | Missing / enrichment-error / stale-context yields a safe truthful error, never blank, never raw exception | **PASS** | `FromError`/`FromRecords` return `null`; `Empty_context_never_renders_a_placeholder` |
| C-16 | Logging | Original technical code/message and correlation id available to authorised support | **PASS (code)**; runtime capture **NOT RUN** | Inventory/Sales/Purchase/Production all log `LogError`/`LogWarning` with the printed reference |
| C-17 | Existing field validation | All keys/rows/messages remain available; nothing silently dropped | **PASS** | Removed-string scan found only re-emissions of the same literal inside the new call |
| C-18 | Performance | No context query on success; bounded reads on failure; no new writes; no lock-held lookups | **PASS (code)** | Presenter is pure; enrichment capped (`MaxDetails` 12, `MaxRelatedDocuments` 5, `MaxValueLength` 80) |
| C-19 | Scope | No schema change, no new transaction screen, no change to costing/posting/rollback rules | **PASS** | No entity/mapping/migration/Designer file in the diff; no new page route |

---

## 2. Error-path-specific invariants

> **Read this first — what "unchanged" means here.** The quantity side is clean across the **entire**
> working tree: `git diff -U0` contains **no** change to any `StdQty` / `IvBalLoc` / balance write in
> production code (only new test fixtures). The costing side is **not** clean tree-wide: an unrelated
> replay/costing stream modifies `ProductionPostingInvariant.cs`, `ProductionOutputService.Rollback.cs`,
> `ProductionIssueEffectiveCostReader.cs` and the WAC/Standard production replay files. Every statement
> below about costing parity applies to **the hunks authored by this change stream only**, which are
> catch blocks, optional parameters, presentation types and the `StockReplayPhysicalSimulator` metadata.
> Releasing the tree as one diff would carry those costing changes.

| # | Invariant | Status | Evidence |
|---|---|---|---|
| E-1 | A failure that previously returned a result still returns the same result type with the same `Message` | **PASS** | Optional trailing parameters only (`Fail(string, ErpOperatorError? = null)`, `Fail(message, …, operatorError)`) |
| E-2 | A page that does not render the banner shows exactly the old text | **PASS** | `FromError` returns `null` when `OperatorError` is absent; pages fall back to `Message` |
| E-3 | No page throws to the Blazor error boundary on a business refusal | **PASS** | `Select-String` for `throw new InvalidOperationException` in the converted post/rollback paths = 0 |
| E-4 | No stack trace, `ex.ToString()` or raw SQL reaches the UI | **PASS** | UI-wide scan → **0 matches** |
| E-5 | The same error renders the same text regardless of UI culture | **PASS (code)** | Presenter uses `InvariantCulture` for numbers and a fixed `dd/MM/yyyy` format |
| E-6 | Reference id is unique per failure and appears in both UI and log | **PASS (code)** | `ErpOperatorErrors.NewReferenceId()` → `Guid` 10 chars uppercase; passed to both the error and the log line |
| E-7 | Partial rollback still reports which batches succeeded | **PASS** | `FromRecords` builds one view from the failed records only; counts untouched |
| E-8 | An unknown exception in the posting dispatch no longer shows its raw message | **PASS** | `IvInventoryPostingService` non-ledger catch now returns `Unexpected` + reference (business guard exceptions keep their own wording) |

---

## 3. What was **not** proven here

| Item | Why | How to close |
|---|---|---|
| DB row-level before/after parity (`IvBalLoc`, `IvTrxHistory`, `StockValuationFact`, `ProductionBalLot`, `ProductionPoolValuationRow`) | No SQL Server instance / seeded database in this environment | Run the snapshot queries in `stock_cost_error_ux_phase6_report.md` §6.2 on both builds |
| Concurrency / double-post / racing-consumption | 8 SQL Server tests skipped (`ERPWEB_PRODUCTION_STOCK_TEST_CONNECTION` unset) | Set the connection string and run the `SqlServer` filter |
| Runtime log-reference round trip | No running application | Phase 6 §6.3 procedure |
| Role-based permission proof (`ViewCost`, branch scoping) | No logged-in session | Phase 6 §6.4 matrix |
| End-to-end screen evidence for GRN Batch 1 / partial rollback / FIFO / WAC / Standard / Sales / Purchase / Production | No application instance | Phase 6 §7 UAT script |

**Honest summary:** parity is proven at unit/integration level and by construction for every decision
point, but **not** proven at database-row or screen level. Treat the five rows above as open evidence
gaps, not as passes.

---

## 4. Baseline comparison method

```
git worktree add ../pv2-baseline 371725c
cd ../pv2-baseline && dotnet test ErpWeb.Tests\ErpWeb.Tests.csproj -c Release
# baseline: Failed 27, Passed 205 (nine affected test classes)
# current : Failed 27, Passed 205
Compare-Object <baseline failed names> <current failed names>   → zero rows (no differences)
```

Full suite on the current tree: **Failed 27, Passed 4747, Skipped 8, Total 4782**, reproduced
identically across three independent runs. All 27 failures also reproduce at `371725c`.

---

## 5. Pre-existing failures (not caused here, not fixed here)

All 27 reproduce identically at `371725c`:

- `Planning.Master`: BOM tree/explosion (7), product-definition validation (2), snapshot loader (2),
  make-without-BOM (1), nested phantom snapshot (1)
- `Sales.Master.SaCustServiceTests` (9) and `Sales.Transaction.SaEInvoiceSelfBillAndTinTests` (2)
- `Procurement.Inquiry` KPI/workbench (3)

They are listed with full names in `stock_cost_error_ux_phase6_report.md` §8.
