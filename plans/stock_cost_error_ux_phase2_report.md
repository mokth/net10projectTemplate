# Phase 2 report — Structured result propagation + original GRN fix

**Date:** 2026-10-29
**Repo:** `mokth/net10projectTemplate` · **Branch:** `productionv2` · **Baseline SHA:** `371725c`
**Scope executed:** PHASE 2 only.
**Gate status:** COMPLETE — waiting for approval before Phase 3.

---

## 1. Scope compliance

| Constraint | Result |
|---|---|
| No costing/quantity algorithm change | ✅ Only init-only metadata added at one producer |
| No `if`, rounding or exception-text change in the replay simulator | ✅ Diff in §3 is purely additive |
| No DB/schema/migration change | ✅ |
| No status/`Succeeded`/counts change | ✅ `FromBatches` and the partial rebuild untouched |
| No new page / no new flow | ✅ Existing GRN list page only |
| `ValidationMessageFormat` untouched | ✅ No field/line validation replaced |
| Original internal error preserved | ✅ `ErrorMessage` still carries the ledger text; already-existing tests asserting it still pass |

### Files changed (6 product, 3 test)

| File | Change |
|---|---|
| `ErpWeb.Core/StockLedger/StockLedgerErrors.cs` | +27: optional `Phase` / `EventId` metadata and `StockLedgerErrorPhase` |
| `ErpWeb.Core/StockLedger/StockReplayPhysicalSimulator.cs` | +14: attach metadata at the existing throw |
| `ErpWeb.Core/Inventory/IIvInventoryPostingService.cs` | +33: `OperatorError` on the batch result, `OperatorErrors`/`HasOperatorErrors` on the aggregate, additive `Fail` overload |
| `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` | +13: the `StockLedgerException` catch |
| `ErpWeb.Core/Inventory/IvInventoryOperatorErrors.cs` | **new**: ledger error + trx type → operator error, reference id |
| `ErpWeb.UI/.../IvGoodsReceiptList.razor{,.cs,.css}` | friendly summary + expandable per-receipt detail |
| `ErpWeb.Tests/.../IvInventoryOperatorErrorsTests.cs` | **new**: 26 assertions across 8 tests |
| `ErpWeb.Tests/.../IvGoodsReceiptServiceTests.cs` | +1 end-to-end GRN replay test |
| `ErpWeb.Tests/.../IvInventoryPostingServiceTests.cs` | +1 partial-rollback parity test |

Unchanged by this phase: `IvGoodsReceiptService` (it already forwarded `Posting` through `OkPosting`), `IvGoodsReceiptOperationResult`, every producer message, every guard.

---

## 2. Structured propagation

```csharp
catch (StockLedgerException ex)
{
    var reference = IvInventoryOperatorErrors.NewReferenceId();
    _logger.LogWarning(ex,
        "Inventory {Action} rejected for batch {BatchNo}: {Code} (phase {Phase}, event {EventId}, ref {Reference})",
        post ? "POST" : "ROLLBACK", batchNo, ex.Error.Code, ex.Error.Phase, ex.Error.EventId, reference);
    results.Add(IvInventoryPostingBatchResult.Fail(
        batchNo,
        ex.Error.Message,                                  // unchanged technical text
        IvInventoryOperatorErrors.FromLedgerError(ex.Error, type, post, batchNo, reference)));
}
```

Guarantees:

* `ErrorMessage` is byte-identical to before — the existing test `Rollback_negative_fails_atomically`
  still asserts it and still passes.
* `Succeeded`, `SucceededCount`, `FailedCount`, `Batches`, `OperationId` and batch order are untouched.
* `OperatorError` is additive and **null** on every failure that is not a `StockLedgerException`
  (concurrency, unique violation, plain guard messages). Those keep their existing text.
* `ReferenceId` is written to the log line and quoted to the operator, so support can find the original
  code, phase and event id. It is not embedded in the message text.
* The document noun comes from the caller's `trxType` (`GR`→Goods Receipt, `MR`→Miscellaneous Receipt,
  …), never from error text. Unknown type → generic noun.
* Item, warehouse, lot, available/required/shortage and cost are deliberately **not** filled at this
  boundary: the posting catch cannot prove them for the failing line, and a wrong quantity is worse
  than a missing one.

---

## 3. Algorithm-safety proof (the only calculation-adjacent file)

`git diff` of `ErpWeb.Core/StockLedger/StockReplayPhysicalSimulator.cs` is exactly:

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

* `if (quantity < 0m)` — unchanged.
* Message string — character-for-character identical.
* Code — unchanged.
* Rounding, ordering, step collection, return values — untouched.
* Call sites — untouched; `phase` was already a parameter, so nothing was re-derived or parsed.

---

## 4. GRN page

* Replaced the raw `<span>@ErrorMessage</span>` toast with a two-level banner when a structured error
  exists: **headline** ("Cannot roll back Goods Receipt 1. Removing this record would cause insufficient
  historical stock when a later transaction is applied.") plus a collapsed
  `What happened for each receipt` list with reason, next step and support reference.
* When no batch carries a structured error the markup falls back to the original toast — byte-identical
  behaviour for every other failure.
* Mixed results list **every** failed receipt: coded failures use operator text, uncoded ones fall back
  to their existing message (nothing is hidden).
* Preserved: `IsSubmitting`, blocking work, confirm dialog dismissal, `SucceededCount > 0` grid reload,
  selection clearing, `DismissError` semantics, toasts for success.
* No `MarkupString`; all text is Razor-encoded.

---

## 5. Tests

### New: `IvInventoryOperatorErrorsTests` (8 tests, 23 cases)

trx-type → document noun (9 types + padding/whitespace + unknown/null), phase metadata mapping (4),
GRN rollback wording with no ledger terms and no event id, post ≠ historical, retryable flag does not
by itself advertise a retry, busy stock does, reference id shape/uniqueness, null error rejected,
aggregate exposes only failed-batch errors with technical totals intact, result without structured
errors advertises none.

### New: GRN end-to-end (the reported scenario)

`Replay_negative_grn_rollback_still_fails_and_gains_operator_message`

Setup: opening 15 EA, GRN 10 dated −2 (balance 25), later issue 20 today (balance 5 — **positive**),
then roll back the GRN.

Asserts:
* rollback still fails;
* `ErrorMessage` still contains `negative` **and** `event` (technical text preserved);
* `Posting.OperatorErrors` is a single item with
  `INVENTORY.HISTORICAL_NEGATIVE_STOCK_REPLAYING`, title `Cannot roll back Goods Receipt 1.`,
  a "historical" explanation, a next step, a reference, and `MayRetryAfterRefresh == false`;
* `ToOperatorText()` contains none of `slice`, `pile`, `event`, `replaying`, `epoch`, `lineage`, `rowversion`;
* `SucceededCount == 0`, `FailedCount == 1`;
* **state parity**: `IvBalLoc.StdQty`, `StockCostState.InventoryValue`, `PoOrderDetail.RecvQty`,
  `IvTrxHistory` count, batch status and `RollbackCount` all unchanged.

This proves the phase metadata reaches the message (it is `_REPLAYING`, i.e. the failure came from the
later issue, not from removing the receipt) and that the rollback outcome is untouched.

### New: partial rollback parity

`Partial_rollback_keeps_counts_ordering_and_plain_failures_untouched` — 3 selected batches
(2 rollbackable, 1 missing): `SucceededCount == 2`, `FailedCount == 1`, batch order preserved,
`HasOperatorErrors == false` (plain failures stay plain), summary still `Rolled back 2 of 3.`,
balance and history return to zero. The 1- and 2-batch paths are covered by the pre-existing
`IvInventoryPostingServiceTests` cases, which all still pass.

### Results

```
dotnet build ErpWeb.Tests/ErpWeb.Tests.csproj          → 0 errors
dotnet test --filter "…GRN|…Posting|StockReplay|StockLedgerInfrastructure|
                      …OperatorError…|ServiceRegistration"
                                                      → Passed 294, Failed 0, Skipped 0 (63 s)
Phase 1 presenter suite re-run                        → included above (66)
Phase 0 ledger baseline suite re-run                  → included above (31)
```

---

## 6. Not verified (honest status)

| Item | Status | Why |
|---|---|---|
| GRN banner visual/UAT | **NOT RUN** | The application is currently running (`ErpWeb`, pid 46376) with the **pre-change** binaries; `dotnet build ErpWeb/ErpWeb.csproj` fails only on file locks (8 `MSB3021/MSB3027`), not on code. Restart the app to see the new banner. |
| Full `dotnet test` | **NOT RUN to completion** | Pre-existing `Planning.Master` / `ProductDefinitionSnapshotLoaderTests` hang (recorded in Phase 0). Nothing in Phase 2 executes there. |
| SQL Server lock behaviour | **NOT RUN** | SQLite in-memory only; no lock/isolation code touched. |
| Mixed coded+plain failure on the page | **NOT RUN (UI)** | Logic implemented and reviewed; only compilation proves it. |

`dotnet build ErpWeb.slnx` was not executed for the same lock reason; `ErpWeb.UI`, `ErpWeb.Core` and the
test project all compile cleanly.

---

## 7. Deliberate decisions

1. **Metadata on `StockLedgerError`, not `Exception.Data`.** Typed, testable, and it cannot be confused
   with a message rewrite. Defaults keep every existing constructor call valid.
2. **`EventId` is carried but never presented.** It makes the log line correlate with the technical
   message without giving the UI an id it would have to explain.
3. **No enrichment in this phase.** Affected document number/date is exactly what Phase 3's read-only
   reader is for; inventing it here would contradict "better to omit details".
4. **Reference id issued at the boundary.** Short, upper-case, logged on the same line as the code —
   no correlation id inside `ErrorMessage`, so existing assertions and logs stay stable.
5. **Uncoded failures keep the legacy toast.** Only `StockLedgerException` gets the new treatment in
   Phase 2; other guards are Phase 4 scope.

---

## 8. Next phase (not started)

**Phase 3 — read-only affected-document explanation:** tenant-scoped, bounded `AsNoTracking` lookup after
a definitive failure only, filling `AffectedDocumentKind/Number/Date` on the context so the message can
say *"…would leave insufficient stock on 02/10/2026 when Delivery Order DO-55 is applied"*; permission
and ownership checks before any link is rendered.

**STOP.** Awaiting approval to start Phase 3.
