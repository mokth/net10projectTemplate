# Phase 3 report — Read-only affected-document explanation

**Date:** 2026-10-29
**Repo:** `mokth/net10projectTemplate` · **Branch:** `productionv2` · **Baseline SHA:** `371725c`
**Scope executed:** PHASE 3 only.
**Gate status:** COMPLETE — waiting for approval before Phase 4.

---

## 1. Scope compliance

| Constraint | Result |
|---|---|
| No costing/quantity algorithm change | ✅ No calculator, simulator, coordinator or valuation file touched in this phase |
| No replay `if`/rounding/exception-text change | ✅ Phase 2 metadata untouched |
| No DB/schema/migration change | ✅ Read-only `AsNoTracking` over existing columns only |
| No new page / no new flow | ✅ Existing GRN list page only |
| Enrichment only after a definitive failure | ✅ Proven: `evidenceReader.Calls == 0` after successful saves/posts |
| No lookup inside the stock transaction lock | ✅ Runs only in the outer `catch`, after the failed batch unwound |
| Fails closed | ✅ Missing/foreign/reversed/stale/cancelled/error → `null` → generic wording |
| Original internal error preserved | ✅ `ErrorMessage` untouched; existing tests still assert the technical text |
| No cross-tenant leakage | ✅ Reader filters on trusted `CompanyCode` + `BranchCode`; foreign rows return `null` |

### Files changed (4 product, 2 new test helpers, 3 test files)

| File | Change |
|---|---|
| `ErpWeb.Core/Inventory/IIvFailureEvidenceReader.cs` | **new**: `IvFailureEvidence` record + reader contract |
| `ErpWeb.Core/Inventory/IvFailureEvidenceReader.cs` | **new**: tenant-scoped, bounded, fail-closed lookup |
| `ErpWeb.Core/Inventory/IvInventoryOperatorErrors.cs` | **modified**: optional `IvFailureEvidence` fills affected document/item/location |
| `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` | **modified**: bounded lookup on the `StockLedgerException` path + 3-lookup cap |
| `ErpWeb.Core/CoreServiceCollectionExtensions.cs` | **modified**: `Scoped` registration |
| `ErpWeb.UI/.../IvGoodsReceiptList.razor{,.cs,.css}` | proven facts rendered (label/value), no links |
| `ErpWeb.Tests/.../IvFailureEvidenceReaderTests.cs` | **new**: 15 cases |
| `ErpWeb.Tests/.../IvInventoryOperatorErrorsTests.cs` | **modified**: +3 evidence mapping tests |
| `ErpWeb.Tests/.../IvGoodsReceiptServiceTests.cs` | **modified**: evidence assertions + multi-batch cap test |
| `ErpWeb.Tests/Infrastructure/Helpers/CountingFailureEvidenceReader.cs` | **new**: delegating counter used to prove the failure-only rule |

Unrelated working-tree change present before this phase and **not touched by it**:
`ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs` (local min-width edit).

---

## 2. The reader

```csharp
public async Task<IvFailureEvidence?> TryReadAsync(
    string companyCode, string branchCode, long eventId,
    CancellationToken cancellationToken = default)
```

* One primary-key `AsNoTracking` projection — no `Include`, no aggregate, no tracking, no write.
* Filtered by the caller's trusted company **and** branch; a row owned by anyone else is `null`.
* Rejects `EntryRole` other than `ORIGINAL`, any row that is itself a reversal, and any row that is
  not `POSTED` — a stale or reversed movement cannot be presented as "the" transaction.
* Negative event ids are stock-count generations in the replay timeline: the record reports
  `StockCount` with **no** document number and no date instead of inventing one.
* `eventId == 0`, `eventId > int.MaxValue`, blank scope → `null` before any query.
* Any `OperationCanceledException` or other exception is swallowed and logged at `Debug`.

Call site (`DispatchAsync`, `catch (StockLedgerException)`):

```csharp
IvFailureEvidence? evidence = null;
if (explanationLookups < MaxFailureEvidenceLookups
    && _failureEvidence is not null
    && ex.Error.EventId is not null)
{
    explanationLookups++;
    evidence = await TryReadFailureEvidenceAsync(
        scope.CompanyCode, scope.BranchCode!, ex.Error, cancellationToken);
}
```

* **Only** inside the refusal catch — never on the success path.
* 2-second linked-timeout budget; a timeout yields the generic message, never a second failure.
* **Cap of 3 lookups per operation**: a wide multi-batch rollback cannot multiply queries; the 4th and
  later failures keep code + next step and simply omit the affected-document hint.
* `ErrorMessage`, `Succeeded`, counts, batch order and all database effects are unchanged (re-asserted).

---

## 3. What the operator now sees

Without evidence (old behaviour, still the fallback):

> Cannot roll back Goods Receipt 1. Removing this record would cause insufficient historical stock
> when a later transaction is applied.

With proven evidence:

> Cannot roll back Goods Receipt 1. Removing this record would cause insufficient historical stock
> on 12/09/2026 when a later transaction is applied.
>
> Affected transaction: Miscellaneous Issue 7, dated 12/09/2026
> Item: A100 — Stock item
> Warehouse: MAIN
> Location: BIN1

Rules kept:

* The affected date is the **affected row's own** `TrxDtTime`, never "today" or the current balance.
* A stock transfer carries two sides; the operated location is ambiguous, so the transfer reports the
  item and family but **no** warehouse/location/lot rather than picking a side.
* No amount, on-hand quantity or shortage is computed here. Phase 2 already refuses to invent those;
  this phase does not reintroduce them from a current balance.

---

## 4. Navigation / permission policy

**Decision: no link is rendered.** The facts are plain text in the existing toast; no `href`, no
`NavigationManager` call, no "Open document" button. Therefore:

* there is no broken target for a document family that has no route (e.g. stock-count generation);
* there is no navigation to a record the operator may not view — the evidence is only ever the row the
  operator's own refused request already touched, and it was read **with the caller's tenant**;
* no `ViewCost` data is exposed (no cost, value or unit price is included);
* the single-receipt entry screen, list page, permissions and menus are unchanged.

When Phase 4 adds links on other pages, the resolver must re-check `Access`/`ViewCost` at render time;
that is deliberately **out of scope** here rather than half-implemented.

---

## 5. Tests

### New: `IvFailureEvidenceReaderTests` (15 cases)

Positive: issue row → item/source location/number/date; receipt row → destination side; transfer row →
item only, ambiguous sides omitted.
Omitted: reversal role; row reversing another; stale (non-posted) row; missing row; foreign company;
foreign branch (while the owning scope still resolves); negative count generation (no number/date);
`0` and `long.MaxValue`; blank company; blank branch; broken factory (throws) → `null`; pre-cancelled
token → `null`.

Seeding note: an earlier draft set `LedgerVersion = 2` without the matching epoch/posting columns and
violated `CK_IvTrxHistory_V2`; the rows are now valid legacy-shaped originals with unique batch numbers
(the V2 shape is exercised end-to-end by the GRN tests below).

### Modified: `IvInventoryOperatorErrorsTests` (+3 tests)

* verified evidence fills `Affected transaction` = `Miscellaneous Issue 7, dated 02/10/2026`,
  `Item` = `A100 — Stock A`, `Warehouse` = `MAIN`, and puts `on 02/10/2026` into the explanation while
  still containing no `event`/`42`;
* missing evidence leaves the explanation generic and adds no affected-document/Item detail;
* a negative event id (stock-count generation) names no document and prints no `-1`.

### GRN end-to-end (`Replay_negative_grn_rollback_still_fails_and_gains_operator_message`)

Now also asserts, on the real SQLite ledger:

* `evidenceReader.Calls == 0` after the successful GRN post **and** the successful issue post;
* exactly one lookup on the failed rollback;
* `Affected transaction` = `Miscellaneous Issue {later batch}, dated 12/09/2026`;
* the explanation contains `on 12/09/2026`;
* the item begins with `A100`;
* and, unchanged from Phase 2, the technical `ErrorMessage`, counts and full state parity.

### New: `Replay_negative_rollback_caps_explanation_lookups_per_operation`

Four backdated receipts of 5 each, a later issue of all 20 (today's balance 0), roll back all four:

* `SucceededCount == 0`, `FailedCount == 4` — refusal remains atomic per batch;
* `evidenceReader.Calls == 3` — the cap holds;
* the first three failures carry `Affected transaction`; the fourth keeps the code, explanation and
  next step but not the document hint;
* balance still 0 afterwards — refusing all four changed nothing.

### Results

```
dotnet build ErpWeb.Tests/ErpWeb.Tests.csproj                    → 0 errors
dotnet build ErpWeb.UI/ErpWeb.UI.csproj                          → 0 errors (20 pre-existing warnings)
focused: failure-evidence + operator-bridge + GRN replay         → Passed 45, Failed 0
dotnet test --filter "Category=Inventory"                        → Passed 1182, Failed 0, Skipped 0 (4 m 01 s)
DI registration tests                                            → Passed 8, Failed 0
```

---

## 6. Not verified (honest status)

| Item | Status | Why |
|---|---|---|
| GRN banner with the new fact rows, visually | **NOT RUN** | The running `ErpWeb` process holds the old binaries; restart the app to see it. No UI logic was inferred from a screenshot. |
| Full `dotnet test` across all categories | **NOT RUN to completion** | Pre-existing `Planning.Master` / `ProductDefinitionSnapshotLoaderTests` hang recorded in Phase 0; nothing in this phase executes there. |
| SQL Server behaviour of the lookup | **NOT RUN** | SQLite in-memory only; the query is a filtered PK projection with no provider-specific construct. |
| Multi-tenant SO/DO/PO evidence | **N/A in this phase** | Only the inventory history reader exists; other modules are Phase 5. |

---

## 7. Deliberate decisions

1. **Evidence, not inference.** Every fact comes from one proven history row of the caller's tenant.
   Nothing is derived from the error string, the current balance or the current business date.
2. **Fail closed, always.** A timeout, a reversal, a stale row or a foreign tenant yields the same
   generic wording as before — never a plausible but wrong document name.
3. **No navigation.** Text-only facts avoid every permission/broken-route risk; links are a Phase 4
   concern on pages that already own a resolver.
4. **Cap of three per operation.** One refusal may explain several receipts, but a 10-batch rollback
   must not become ten round-trips.
5. **Stock-count generations stay anonymous.** A negative event id proves "a count did this" without a
   document number, so only the family is reported.
6. **Legacy rows are valid evidence.** Rows without `LedgerVersion` are treated like `ORIGINAL` rows
   (`EntryRole` null or `ORIGINAL`), because pre-V2 history is still real history.

---

## 8. Next phase (not started)

**Phase 4 — remaining Inventory / StockLedger / Costing UI:** apply the same operator contract to
Miscellaneous Receipt, Miscellaneous Issue, Scrap, Vendor Return, Customer Return, Stock Transfer,
Stock Adjustment, Stock Count, Inventory Inquiry/Costing Center and any other posting/rollback screen
found in Phase 0, including guarded source-document links.

**STOP.** Awaiting approval to start Phase 4.
