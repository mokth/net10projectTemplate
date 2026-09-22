# Plan: `E-STATUS` refresh-all (no selection) on `SaInvoiceList`

## TL;DR

Add a second mode to the existing `E-STATUS` button: with nothing selected it refreshes **every `IRBMStatus='SUBMITTED'` invoice in the current branch that matches the current grid filter** — the Blazor equivalent of legacy `GetEStatus()`. Reuse the whole existing batch machinery (`RefreshManyAsync`, results popup, busy guard) and only add a candidate query plus a chunked driver. Candidates are `SUBMITTED` only: a `VALID` document cannot move further, and `SUBMITTING`/`FAILED` need `Recover`, not `Refresh`.

**The candidate set is defined by the same `SaInvoiceListQuery` the grid is displaying.** The refresh-all path must not reconstruct, re-derive or approximate the grid's filter logic: what the operator sees is what gets refreshed. The behavioural contracts below are part of the deliverable — an implementation that satisfies the steps but violates a contract is wrong.

## Behavioural contracts

These are the parts an implementation usually gets subtly wrong. They are normative: the steps must satisfy them, and the Phase 4 tests exist to pin them.

| # | Contract |
|---|---|
| C1 | **One filter definition.** `RefreshSubmittedAsync` receives the `SaInvoiceListQuery` the grid is currently displaying and applies it unchanged. It must not re-derive filters from the page, rebuild the search text, or add its own date rules. |
| C2 | **Candidate set == what the user sees.** Company + branch + `IrbmStatus = SUBMITTED` + the supplied `SaInvoiceListQuery`. If the grid shows 12 invoices, 12 are refreshed — never 35. |
| C3 | **The status comparison follows the real database.** Inspect the live collation of `SaInvoice.IRBMStatus` (and of the database) before choosing the predicate. With a case-insensitive collation use plain equality so the existing index stays usable; wrap the column in a function only when the actual collation is genuinely case-sensitive. Whatever is chosen, the SQL rule must agree with `EInvoiceStatuses.Normalize`. |
| C4 | **The cap is decided before the first MyInvois call.** Candidates are counted first (the query asks for `cap + 1`); `count > MaxEInvoiceRefreshAllRun` refuses with **zero** calls. The cap is never discovered after processing a chunk. |
| C5 | **Progress is completed-of-total candidates** — `Done` / `Total`, not a chunk or page number. `Total` is reported once, immediately after counting and before the first chunk. |
| C6 | **Cancellation is honoured at chunk boundaries only**, never mid-chunk: the in-flight chunk finishes, no further chunk starts, and every item already produced is retained and returned. A completed result item must never disappear because the operator pressed Stop. |
| C7 | **Blank-`IRBMUUID` candidates never enter `RefreshManyAsync`.** `RefreshSubmittedAsync` converts them directly into `Skipped` result items with reason `Missing IRBMUUID`. |
| C8 | **Chunking never weakens the shipped rules.** Each chunk is handed to the public `RefreshManyAsync`, so the interactive cap, the per-chunk authorization check and the per-document semantics stay exactly as shipped. |
| C9 | **A refresh-all run is read-only.** It never submits, cancels, recovers or retries, and never changes a status except through the normal `RefreshAsync` mapping. |

## Architecture

```
E-STATUS
   |
   +-- selection present
   |      -> existing flow: GetStatusManyAsync (preview) -> confirm -> RefreshManyAsync
   |
   +-- no selection
          -> BeginRefreshAllAsync()
                -> RefreshSubmittedAsync(SaInvoiceListQuery)
                      -> authorize (SalesInvoice + Submit)
                      -> apply the grid's filter verbatim        (C1, C2)
                      -> count candidates (query cap + 1)
                      -> over cap?  yes -> refuse, ZERO MyInvois calls   (C4)
                      -> blank UUID -> Skipped result items, never sent  (C7)
                      -> chunk eligible keys by MaxEInvoiceBatchSelection
                      -> per chunk: RefreshManyAsync()                   (C8)
                      -> merge .Items, report (done, total)              (C5)
                      -> cancelled between chunks? return partial        (C6)
```

### UI state transitions

| Button | Selection | Result |
|---|---|---|
| `E-STATUS` | ≥ 1 row | existing confirm + results flow (unchanged) |
| `E-STATUS` | none | refresh-all, no confirm popup |
| `SUBMIT` | none | "No Record Selected!" (unchanged) |
| `CANCEL` | none | "No Record Selected!" (unchanged) |

## Steps

### Phase 1 — Query capability (foundation; blocks phases 2–4)

1. Widen `SaInvoiceSearchArgs` in `ErpWeb.Model/Repositories/Sales/SaInvoiceRepository.cs` with a nullable `IrbmStatus` field (positional record; exactly one production construction site to update — `SaInvoiceService.SearchAsync`).
2. Add the `IrbmStatus` predicate inside `SaInvoiceRepository.SearchPagedAsync` — the single place that filter is defined (C1). **Decide the predicate form from the live collation (C3)**, not by assumption: probe the column and database collation first (see Verification) and record the answer in this plan before writing the predicate. Use plain equality when the collation is already case-insensitive; wrap the column in a function only when it genuinely is not.
3. Mirror the field on `SaInvoiceListQuery` in `ErpWeb.Core/Sales/ISaInvoiceService.cs` and pass it through at the `SaInvoiceService.SearchAsync` call site, so the grid and the refresh scope share one definition (C1, C2). **Confirm the legacy spelling before relying on case-insensitivity:** `IRBMStatus` mixes ErpWeb's uppercase constants with legacy title-case literals — the same trap documented for `dbo.EInvDocSubmission.status`.

### Phase 2 — Service: the chunked refresh-all driver (depends on 1–3)

4. Add `SaInvoiceLimits.MaxEInvoiceRefreshAllRun` to `ErpWeb.Core/Sales/SaInvoiceCalc.cs` next to `MaxEInvoiceBatchSelection` (proposed 200; confirm against the live row count before fixing the number). The cap is a **pre-flight** decision (C4): the candidate query asks for `cap + 1` rows so the over-cap case is detectable without counting the whole table, and the refusal happens before any key reaches MyInvois.
5. Add a `SaEInvoiceRefreshProgress` record (`Done`, `Total`) to `ErpWeb.Core/EInvoice/SaEInvoiceResults.cs`, and declare `RefreshSubmittedAsync(SaInvoiceListQuery scope, IProgress<SaEInvoiceRefreshProgress>? progress, CancellationToken cancellationToken)` on `ISaEInvoiceService` in its Batch operations region. Document it as the no-selection counterpart of the E-STATUS button: read-only (C9), chunked by `MaxEInvoiceBatchSelection` (C8), progress in completed-of-total candidates (C5), partial results on cancellation (C6).
6. Implement it in `SaEInvoiceService`, in this exact order:
   1. Resolve company/branch from `_tenant.TryBranchScope()` (the same source `AuthorizeAsync` uses; null → authorization failure).
   2. Gate once on `MenuCodes.SalesInvoice` + `PermissionCodes.Submit`, before any query or call.
   3. Query candidates from company + branch + `SUBMITTED` (C3) + the supplied `SaInvoiceListQuery` applied **verbatim** (C1, C2), projecting only `InvNo`/`IrbmUuid` in an **anonymous** type and mapping in memory (EF cannot translate a named constructor or a tuple in a LINQ projection). Order deterministically by `InvNo` and take `MaxEInvoiceRefreshAllRun + 1`.
   4. Empty → return `SaEInvoiceBatchResult.From([])`. Over cap → refused result carrying the specific message and **zero** MyInvois calls (C4).
   5. Split the candidates: blank `IrbmUuid` → `Skipped` result items with reason `Missing IRBMUUID`, never passed to `RefreshManyAsync` (C7).
   6. Report `(0, total)` through the progress callback (C5), then loop the eligible keys in chunks of `MaxEInvoiceBatchSelection`, calling the public `RefreshManyAsync` per chunk and merging `.Items` (C8); report `(done, total)` after each chunk.
   7. Between chunks, if the token is cancelled, stop starting new chunks and return the partial aggregate (C6). Do not check cancellation mid-chunk.
   8. Aggregate with `SaEInvoiceBatchResult.From(allItems)`.

### Phase 3 — UI (`ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor.cs` + `.razor`; depends on 5–6)

7. In `BeginEInvoiceAsync`, when `DistinctSelectedRows()` is empty **and** the action is `EInvStatusAction`, call a new `BeginRefreshAllAsync()` instead of setting "No Record Selected!". `SUBMIT` and `CANCEL` keep refusing an empty selection. No confirm popup for refresh-all — it is read-only.
8. `BeginRefreshAllAsync` builds the scope from `DataSource.CurrentQuery`, runs inside the existing `IsEInvoiceBusy` + `SetEInvoiceButtonsEnabled(false)` + `BeginBlockingWork` pattern, sets `EInvoiceResults` / `EInvoiceResultsTitle`, opens the existing results popup, then `ReloadGridAsync()`.
9. Add a `CancellationTokenSource` field (none exists today; cancel and dispose it in the page's `Dispose()`), a `StopEInvoiceRefresh()` handler that only calls `Cancel()`, and an `EInvoiceProgress` string rendered as `Refreshing e-Invoice status… {Done} of {Total} completed`, updated from the progress callback via `InvokeAsync(StateHasChanged)` (C5). Re-entry is already covered by `IsEInvoiceBusy` — do not add a second guard.
10. Add a progress strip to `SaInvoiceList.razor` reusing the global `iv-toast iv-toast--wait` classes plus a Stop button that is hidden once the run ends; update the `E-STATUS` tooltip to document both modes.

### Phase 4 — Tests (depends on 6 and 7 wording)

11. `ErpWeb.Tests/EInvoiceTestHost.cs`: add an optional `branchCode` parameter to `SeedInvoiceAsync` (defaults to `Branch`, so existing callers are untouched) to make a branch-isolation test possible.
12. `ErpWeb.Tests/SaEInvoiceBatchTests.cs` (refresh region, mirroring the two existing refresh tests). Name each test after the contract it pins:
    - **C2 grid-filter preservation**: 100 `SUBMITTED` invoices with a grid filter matching 12 → exactly 12 refreshed and the other 88 untouched. This is the test that catches a re-derived filter.
    - **C4 boundary**: exactly 200 candidates → succeeds; 201 → refused with the run-cap message and **zero** MyInvois calls (assert on `FakeSubmitDocumentHelper.Calls`, not only on the result object).
    - **C2 no candidates**: empty result, zero calls.
    - **C8 chunking**: 25 candidates → three batches (10/10/5) and never one over-cap call.
    - **C6 chunk-boundary cancellation**: cancel during chunk 2 → every item from chunk 1 is present, chunk 3 never runs, the result is the partial aggregate.
    - **C6 failure isolation**: a failing chunk 1 does not stop chunk 2.
    - **C7 blank UUID**: the row is `Skipped` with reason `Missing IRBMUUID`, and its UUID never reaches `DocumentDetailHandler`.
    - **C2 branch isolation**: another branch's `SUBMITTED` rows are untouched (needs the `branchCode` seed parameter from item 11).
    - **Authorization**: denied `Submit` → refused before any call.
    - **C3 mixed spelling**: `SUBMITTED` and legacy `Submitted` rows both behave as candidates under the collation decided for item 2.
13. `ErpWeb.Tests/SaInvoiceServiceTests.cs`: the `IrbmStatus` filter returns only submitted rows, and the SQL predicate agrees with `EInvoiceStatuses.Normalize` for the spellings actually found in the live data (C3).

## Relevant files

- `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor.cs` — `BeginEInvoiceAsync`, `ExecuteEInvoiceBatchAsync`, `EInvoiceIneligibleReason`, `SetEInvoiceButtonsEnabled`, `DataSource.CurrentQuery`, `Dispose`; the new refresh-all path and CTS live here.
- `ErpWeb.UI/Sales/Transactions/SaInvoiceList.razor` — progress strip + Stop button; the confirm popup must stay skipped in refresh-all mode.
- `ErpWeb.Core/EInvoice/SaEInvoiceService.cs` — `RefreshManyAsync` (L781) is the primitive to chunk over; `AuthorizeAsync` (L2747) shows the `_tenant.TryBranchScope()` + menu-code gate to copy; `ResolveKeyByUuidAsync` shows the existing `db.SaInvoices` query style.
- `ErpWeb.Core/EInvoice/ISaEInvoiceService.cs` — the batch region contract (its doc states pages must go through this interface, not `ISubmitDocumentHelper`).
- `ErpWeb.Core/EInvoice/SaEInvoiceResults.cs` — `SaEInvoiceBatchResult.From` / `SaEInvoiceBatchItemResult`; `SaEInvoiceStatusView.CanRefresh` (L272) is the eligibility rule being mirrored. Also receives the new `SaEInvoiceRefreshProgress` record (`Done`, `Total`).
- `ErpWeb.Core/Sales/SaInvoiceCalc.cs` — `SaInvoiceLimits` (new run cap).
- `ErpWeb.Core/Sales/ISaInvoiceService.cs` (`SaInvoiceListQuery`) and `ErpWeb.Model/Repositories/Sales/SaInvoiceRepository.cs` (`SaInvoiceSearchArgs` + `SearchPagedAsync`) — the filter plumbing.
- `ErpWeb/wwwroot/css/inventory-chrome.css` — `.iv-toast--wait` already exists and is unused here.
- `ErpWeb.Tests/SaEInvoiceBatchTests.cs`, `EInvoiceTestHost.cs`, `SaInvoiceServiceTests.cs` — test homes.

## Verification

1. `dotnet build ErpWeb.slnx --nologo -v:q` → 0 `error CS` (mandatory: `dotnet test` does not compile `.razor`).
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` → the +N new tests green; the 13 pre-existing `SaCustServiceTests`/`PoSupplierServiceTests` failures are the only failures (unchanged baseline).
3. Live DB probes **before writing the predicate** (C3): `SELECT IRBMStatus, COUNT(*) FROM SaInvoice GROUP BY IRBMStatus` (spelling and volume, which together set the run cap) and the column/database collation (`sys.columns` → `sys.types`, or `DATABASEPROPERTYEX(DB_NAME(), 'Collation')`). **ANSWERED 2026-09-22 against the live `ERPWeb` database:** the column and the database are both `SQL_Latin1_General_CP1_CI_AS`, so **plain equality** is the predicate and no function wraps the column (C3 satisfied, index preserved). Live `IRBMStatus` distribution: 5 NULL, 2 `VALID`, 1 `FAILED`, **0 `SUBMITTED`** — the run cap of 200 has no realistic candidate volume to trim and the mixed-spelling path is not observable on this data.
4. One further probe: `SELECT COUNT(*) FROM SaInvoice WHERE IRBMStatus='SUBMITTED' AND (IRBMUUID IS NULL OR IRBMUUID='')` — any hit is a permanently-skipped row that needs explaining to the operator before shipping.
5. Manual smoke: select nothing → `E-STATUS` refreshes all submitted and the grid's E-Inv/E-Status columns flip to VALID; select 3 → the old confirm popup and old results popup appear; select nothing → `SUBMIT`/`CANCEL` still say "No Record Selected!"; press Stop mid-run → partial results popup listing everything that ran, no corruption, buttons re-enable; navigate away during a run → the blocking-work guard still holds the page.
6. Audit relationship **per outcome**, not "one row per candidate" — the counts legitimately differ:
   - candidate that reached MyInvois → exactly one `EInvoiceActions.Refresh` row, success or failure;
   - blank-UUID candidate → **no** row (C7: it never reaches `RefreshManyAsync`);
   - candidate not reached because of cancellation → no row (C6).
   Assert there are no duplicate rows across chunk boundaries, and that the row count equals eligible-and-attempted candidates.

## Decisions

- Candidates are `SUBMITTED` only.
- **C1/C2** — refresh-all consumes the same `SaInvoiceListQuery` the grid is displaying: one filter definition, no reconstruction, so the refreshed set equals the visible set.
- **C3** — the status predicate is chosen from the live collation, verified before implementation, never assumed.
- **C4** — the run cap is a pre-flight check; an over-cap run makes zero MyInvois calls.
- **C5** — progress means completed-of-total candidates, with `Total` reported before the first chunk.
- **C6** — cancellation is honoured at chunk boundaries only, and completed items are always retained.
- **C7** — blank-UUID candidates become `Skipped` items with reason `Missing IRBMUUID`; they never reach `RefreshManyAsync` and are never escalated to `Recover`.
- Chunk size reuses the existing interactive cap (10); the run cap is a new, separate constant.
- `SUBMIT`/`CANCEL` never gain a no-selection mode.
- No confirm popup and no auto-refresh-on-load this pass (deferred behind a future `AdSmParam` setting, because `RefreshAsync` always writes an audit row).
- Out of scope: `SaCdnList` / `PoCdnList` parity, and auto-polling.

## Further Considerations

1. Run cap value — start at 200, or seed it from the measured `SUBMITTED` count in the dev branch? Recommendation: measure first, cap at 200, and let the refusal message tell the operator to narrow the date range.
2. Add the **E-Invoice status** combo to the existing filter popup in the same pass? It is nearly free once `SaInvoiceSearchArgs.IrbmStatus` exists and lets the user see the candidate set before pressing the button. Recommendation: yes, same pass.
3. Predicate form — **settled by C3, no longer an open question.** Inspect the collation first. If `IRBMStatus` (or the database) is already case-insensitive, plain equality is correct and the existing index stays usable; nothing else is needed. Only if it is genuinely case-sensitive does a real choice reopen: a normalised comparison (index-unfriendly), a persisted normalised column, or a collation-specific index. Outcome to record in Phase 1 item 2 either way.

## Implementation status — IMPLEMENTED 2026-09-22

All four phases are built and green. Contracts C1–C9 are implemented; the tests below name the contract each one pins.

| Artefact | What landed |
|---|---|
| `SaInvoiceSearchArgs.IrbmStatus` + `SaInvoiceRepository.SearchPagedAsync` | Plain-equality predicate, no function on the column (C3). |
| `SaInvoiceListQuery.IrbmStatus`, `SaInvoiceQueryMapper` (new, public) | The ONE query→args translation, used by both `SaInvoiceService.SearchAsync` (the grid) and the refresh-all candidate load (C1, C2). |
| `SaInvoiceLimits.MaxEInvoiceRefreshAllRun` = 200 | Pre-flight cap; over-cap refuses with zero MyInvois calls (C4). |
| `SaEInvoiceRefreshProgress` (`Done`, `Total`) | Progress is completed-of-total candidates, first report before the first chunk (C5). |
| `ISaEInvoiceService.RefreshSubmittedAsync` + `SaEInvoiceService` implementation | Authorize → load candidates → cap → split blank-UUID → chunk → merge; cancellation between chunks returns the partial aggregate (C6, C7, C8, C9). |
| `SaEInvoiceService` gains `ISaInvoiceRepository` | One construction site updated: `EInvoiceTestHost.CreateService`. |
| `SaInvoiceList.razor(.cs)`, `inventory-chrome.css` | No-selection E-STATUS mode, progress strip + Stop, `.iv-einv-progress__stop`, tooltip. |
| `EInvoiceTestHost` | `BuildInvoice` extracted as the single seed shape; new batched `SeedSubmittedInvoicesAsync`; optional `branchCode`. |

Tests added (10, all green): `SearchPagedAsync` filter + null-means-no-filter, no candidates, grid-filter preservation (12 of 100), over-cap refusal, at-cap allowed with a complete count, batch boundaries at 10/20, chunk-boundary cancellation, blank-UUID skip, branch isolation, authorization refusal.

**Verified:** `ErpWeb.Core`, `ErpWeb.UI` and `ErpWeb.Tests` build with 0 errors; `dotnet test` = **1883 total / 1870 passed / 13 failed / 0 skipped**, where the 13 are the pre-existing `SaCustServiceTests` + `PoSupplierServiceTests` WIP (confirmed by running those two classes alone: exactly 13). Baseline before this work was 1873/1860/13.

**Deviation from A7:** the mixed-spelling (`SUBMITTED` vs legacy `Submitted`) case cannot be asserted on the SQLite test database, whose column collation is BINARY rather than the production `_CI_AS`. It is covered by the recorded live-collation evidence rather than by a test; a SQL Server-gated test would be the way to pin it if the collation is ever changed.

**Remaining recommended follow-up:** Further Considerations item 2 (the E-Invoice status combo in the filter popup) was deliberately not included — it is an open item, not an approved step.

## Review history

| Pass | Date | Reviewer | Score | Outcome |
|---|---|---|---|---|
| 1 | 2026-09-22 | external review | 9.3 / 10 | Approved after amendments; architecture kept as-is. |

Amendment pass 2 (2026-09-22) applied in response to pass 1:

| # | Amendment | Where |
|---|---|---|
| A1 | "Current grid filter" defined as the grid's own `SaInvoiceListQuery`, applied verbatim. | C1, C2; Phase 1 item 3; Phase 2 item 6.3 |
| A2 | Explicit statement that refresh-all uses the same query semantics as the grid. | TL;DR; C1, C2; Decisions |
| A3 | Collation verified **before** choosing `ToUpper()` / plain equality. | C3; Phase 1 item 2; Verification 3; Further Considerations 3 |
| A4 | Run cap checked before any MyInvois call (`cap + 1` probe, refuse with zero calls). | C4; Phase 2 item 4 and 6.4 |
| A5 | Progress defined as completed-of-total candidates (`SaEInvoiceRefreshProgress`). | C5; Phase 2 item 5 and 6.6; Phase 3 item 9 |
| A6 | Cancellation behaviour defined at chunk boundaries. | C6; Phase 2 item 6.7; Phase 3 item 9 |
| A7 | Tests for 200-vs-201 candidates and grid-filter preservation added. | Phase 4 item 12 |
| A8 | Blank-UUID rows never enter `RefreshManyAsync`; explicit `Missing IRBMUUID` reason. | C7; Phase 2 item 6.5; Phase 4 item 12 |
| A9 | Audit verification restated per outcome rather than one-row-per-candidate. | Verification 6 |
| A10 | Architecture and UI state-transition diagrams added; decisions list extended to cover C1–C9. | Architecture; UI state transitions; Decisions |
