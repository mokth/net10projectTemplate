# Phase 1 report — Pure error presenter + tests

**Date:** 2026-10-29
**Repo:** `mokth/net10projectTemplate` · **Branch:** `productionv2` · **Baseline HEAD:** `371725c`
**Scope executed:** PHASE 1 only — pure presenter + unit tests.
**Gate status:** COMPLETE — waiting for approval before Phase 2.

---

## 1. Scope compliance

| Constraint | Result |
|---|---|
| No business logic change | ✅ No existing business file touched |
| No costing/quantity algorithm change | ✅ Presenter is a pure function; consumes no ledger state |
| No DB/schema/EF change | ✅ No migration, entity, DbContext or SQL change |
| No new transaction screen | ✅ No `.razor` file added or edited |
| No existing producer message reworded | ✅ All 199 producer sites are untouched until Phase 2 |
| Baseline contract preserved | ✅ `StockLedgerErrorCodes` (21 constants) not edited |

### Files added (3)

| File | Purpose |
|---|---|
| `ErpWeb.Core/Services/ErpOperatorError.cs` | Presentation contract: `ErpOperatorError`, `ErpOperatorErrorDetail`, `ErpRelatedDocumentHint`, `StockOperatorFailureContext` + 5 enums |
| `ErpWeb.Core/Services/ErpOperatorErrorPresenter.cs` | Pure deterministic mapper: failure → operator text |
| `ErpWeb.Tests/Other/ErpOperatorErrorPresenterTests.cs` | 66 tests (`Category=Shared`) |

`git status` confirms **no modified file** attributable to Phase 1. The single `M` entry
(`ErpWeb.UI/Components/Common/DataGrid/CommonDataGridEx.razor.cs`, 2 lines, last written 10:21) predates
this phase and is unrelated to the presenter work.

---

## 2. The contract

`ErpOperatorError` (`PresentationCode`, `Title`, `Explanation`, `NextStep`, `ReferenceId`,
`MayRetryAfterRefresh`, `Details`, `RelatedDocuments`) matches the plan's §2.1 DTO plus two
denormalised lists. It contains **no** exception, tenant id, posting id, event id or SQL text.

`StockOperatorFailureContext` is the only input. Every property is optional, so a producer with
nothing but a code still gets a correct message; every extra property only *adds precision*.

Key design decisions:

1. **Classification is structured, never textual.** `Family` (when a producer supplied one) wins;
   otherwise the persisted code plus `Action` and `RejectionPhase` decide. No error string is parsed.
2. **Unknown document kind is never echoed.** Only the `ErpDocumentKind` enum can put a name in the
   headline, so a stored kind such as `IV_GR` cannot reach an operator. With an unknown kind the
   headline falls back to `document {number}` / `this record` / `this action`.
3. **No fabricated values.** Missing item, warehouse, lot, quantity or date simply removes that row.
   There are no `{item}`-style placeholders anywhere in the presenter.
4. **No fabricated reference.** `ReferenceId` is copied when the caller issued one and the
   `Reference:` line is omitted otherwise. The presenter never generates an id (it would be
   non-deterministic and would not exist in the server log).
5. **Monetary rows are gated.** Any `ExtraDetails` row flagged `IsMonetary` is dropped unless
   `CanViewCost` is set.
6. **Dates and quantities are formatted once**, here: `dd/MM/yyyy` and `#,0.#### <UOM>`
   (inventory scale = 4, thousands separators, invariant digits). No ISO date and no culture-dependent
   separator reaches the operator.
7. **Producer-supplied text is treated as data**: whitespace collapsed, control characters dropped,
   values capped at 80 characters, details capped at 12, related documents at 5.

---

## 3. Code → family defaults

| Persisted code | Family | Notes |
|---|---|---|
| `LEDGER_DISABLED` | `UnsupportedReplay` | Legacy freeze switch; never a quantity problem |
| `STOCK_BUSY` | `StockBusy` | Only family allowed to advertise retry |
| `REQUEST_ID_REUSED` | `AlreadyProcessed` | Request-level duplicate |
| `DOCUMENT_ALREADY_POSTED` | `AlreadyProcessed` | Document-level duplicate |
| `STALE_DOCUMENT` | `StaleDocument` | Version check refused the write |
| `CLOSED_PERIOD` | `ClosedPeriod` | Uses `DocumentDate` + `ClosedThrough` |
| `BACKDATED_STOCK_EVENT` | `UnsupportedReplay` | Unsupported historical shape |
| `COUNT_SCOPE_FROZEN` | `CountLocked` | Reserved (guard is currently a no-op) |
| `INVALID_STOCK_IDENTITY` | `StockIdentityUnverified` | Covers both scope and stock-identity causes without guessing |
| `INSUFFICIENT_BASE_QTY` | `HistoricalNegativeStock` **or** `StockShortageNow` | Split by action/phase — see §4 |
| `INVALID_UOM_CONVERSION` | `UomConversion` | Names `FromUom`→`ToUom` when proven |
| `REVERSAL_DEPENDENCY` | `DependentDocument` | |
| `REVERSAL_ALREADY_EXISTS` | `AlreadyProcessed` | |
| `LEDGER_MISMATCH` | `ReconciliationRequired` | |
| `LEGACY_LINEAGE_UNVERIFIED` | `ReconciliationRequired` | Reserved |
| `HISTORY_BEFORE_CUTOVER` | `UnsupportedReplay` | Reserved |
| `VALUATION_REQUIRED` | `MissingReceiptCost` | Covers opening/receipt layer + revision gaps |
| `COUNT_SNAPSHOT_CHANGED` | `StaleDocument` | Reserved |
| `COST_POLICY_MISSING` | `CostMethodSetup` | |
| `COST_POLICY_INVALID` | `CostMethodSetup` | Conflicting/incomplete settings wording |
| `COST_METHOD_NOT_IMPLEMENTED` | `CostMethodSetup` | Reserved |

All 21 are asserted to resolve to a **specific** family and to produce **specific** text — the five
reserved presets included, so a future producer cannot silently become "something failed".

---

## 4. The `INSUFFICIENT_BASE_QTY` split (the plan's central risk)

The single most dangerous mapping is treating this code as "not enough stock now". The presenter
therefore resolves it from **structured evidence only**:

| Evidence | Family | Headline guidance |
|---|---|---|
| `Action = Post/Save`, no replay phase | `StockShortageNow` | "There is not enough available stock…" (Production origin says "…to issue to Production") |
| `Action = Rollback/Cancel` | `HistoricalNegativeStock` | "Removing this record would cause insufficient historical stock when a later transaction is applied." |
| `RejectionPhase = Removing` | `HistoricalNegativeStock` | "Removing this record would make the stock balance at its original date fall below zero." |
| `RejectionPhase = Rewinding` | `HistoricalNegativeStock` | "A later stock movement for this item cannot be reversed against the current stock balance." |
| `RejectionPhase = Replaying` | `HistoricalNegativeStock` | "…when a later transaction is applied" + affected document/date when proven |

Each phase produces its own `PresentationCode` (`…_REMOVING`, `…_REWINDING`, `…_REPLAYING`), its own
reason and its own guidance — verified by test.

**Consequence for Phase 2:** the presenter is only as precise as what producers hand it. The narrow
metadata allowance on `StockReplayPhysicalSimulator.EnsureNonnegative` (phase + affected document) is
what turns the GRN message from *"would cause insufficient historical stock…"* into *"would cause
insufficient stock on 02/10/2026 when Delivery Order DO-55 is applied"*. Until Phase 2, the presenter
deliberately stays on the safer generic-historical wording rather than guessing which document owns
the timeline position.

---

## 5. Families implemented (19)

| Family | Reason shown | Retry after refresh |
|---|---|---|
| `StockShortageNow` | not enough available stock (Production variant) | no |
| `HistoricalNegativeStock` | removal/replay would go negative; phase-specific | no |
| `MissingReceiptCost` | cost of the movement could not be verified | no |
| `CostStateNeedsReview` | quantity and cost records disagree | no |
| `UnsupportedReplay` | older transaction not correctable automatically | no |
| `DependentDocument` | a later stock/costing transaction depends on this | no |
| `ClosedPeriod` | date falls in a closed inventory period (+ closed-through) | no |
| `FutureDocumentDate` | dated movement cannot be posted yet | no |
| `UomConversion` | approved unit conversion missing | no |
| `CurrencyRate` | no approved exchange rate covers the date | no |
| `StaleDocument` | record or balance changed while working | **yes** |
| `AlreadyProcessed` | already posted / reversed / request reused | no |
| `CountLocked` | stock count in progress blocks changes | no |
| `PermissionDenied` | no permission for the action | no |
| `CostMethodSetup` | no valid costing method / settings conflict | no |
| `ReconciliationRequired` | related stock/costing records need reconciliation | no |
| `StockBusy` | another stock transaction holds the branch | **yes** |
| `StockIdentityUnverified` | stock record or company/branch scope unverified | no |
| `UnknownFailure` | generic, always truthful | no |

`MayRetryAfterRefresh` is **not** copied from `StockLedgerError.Error.Retryable`. It is true only for
`StockBusy` and `StaleDocument`, where the refused attempt provably did not apply. This satisfies the
plan's rule that a retry must never be advertised on an unknown outcome.

---

## 6. Test coverage — 66 tests, all passing

| Group | Count | What is pinned |
|---|---|---|
| Code catalogue (theory × 21) | 42 | Every persisted code → specific family; specific title/reason/guidance; no forbidden vocabulary; no raw code in output |
| Classification | 3 | `INSUFFICIENT_BASE_QTY` action split; replay-phase codes/reason/guidance; structured family wins over code |
| Headline | 3 | Known document named; unknown kind never leaks a stored kind; kind without number stays readable |
| Details | 6 | Quantities + UOM + scale; unproven values omitted; cost rows gated by `CanViewCost`; multiline/long values collapsed and capped; detail/link caps; affected transaction only when proven |
| Dates | 2 | `dd/MM/yyyy` with closed-through; `default(DateTime)` never renders `01/01/0001` |
| Retry/reference/determinism | 3 | Retry limited to two families; reference only when issued (and trimmed); identical context → identical text |
| Wording contracts | 4 | Locale key shape; Production shortage wording; code-specific "already processed" wording; every family complete |
| Safety | 3 | Null context; no `{}`/`null` placeholders; `Safe()` behaviour |

Forbidden-vocabulary assertion (word-boundary regex, case-insensitive) rejects: `slice`, `pile`,
`epoch`, `lineage`, `sealed`, `watermark`, `rowversion`, `tail`, `event`, `hash`, `canonical`,
`exception`, `NullReference`, `SQL`, `FIFO`, `LIFO`, `WAC`, `ledger`, `replay`, `stack trace`,
`posting id`, `history id`, `tenant id`.

---

## 7. Verification log

```
dotnet build ErpWeb.Tests/ErpWeb.Tests.csproj
  → 0 Error(s); no warning from either new file (3,386 pre-existing suite warnings unchanged)

dotnet test --filter "FullyQualifiedName~ErpOperatorErrorPresenterTests"
  → Passed! Failed: 0, Passed: 66, Skipped: 0, Total: 66 (100 ms)

dotnet test --filter "StockLedgerInfrastructureTests|StockReplayPhysicalSimulatorTests"   (Phase 0 baseline re-run)
  → Passed! Failed: 0, Passed: 31, Skipped: 0, Total: 31 (21 s)
```

The full suite is still **not run to completion** — same pre-existing `Planning.Master` /
`ProductDefinitionSnapshotLoaderTests` hang recorded in the Phase 0 baseline. It is not a regression
from this phase and nothing in Phase 1 executes at runtime until a producer calls it.

---

## 8. Deliberate deviations / additions (for approval)

1. **Four families added beyond the plan's example list** — `StockBusy`, `StockIdentityUnverified`,
   `AlreadyProcessed`, `CountLocked` were implied by the census families 21/23/31/32 but were not in
   the `such as` list. Merging them into `UnknownFailure` would have made five live codes generic, so
   each got its own family.
2. **`PresentationCode` includes the origin prefix** (`INVENTORY.STOCK_SHORTAGE_NOW`,
   `PRODUCTION.STOCK_SHORTAGE_NOW`). The origin only chooses wording; it is **not** an authorisation
   signal anywhere.
3. **`CountLocked` is implemented but currently unreachable**, because `NoActiveStockFreezeGuard`
   returns `null`. It is a preset so that enabling the guard needs no presenter change.
4. **`VALUATION_REQUIRED` has one family, not four.** FIFO-layer exhaustion, standard-cost absence and
   missing opening-history are not distinguishable at the exception boundary today. The unified
   `MissingReceiptCost` wording is truthful for all three. Splitting it is a Phase 2 decision that
   requires producers to supply the family explicitly.
5. **No `Details` sanitisation of producer intent.** The presenter caps and collapses `ExtraDetails`
   but does not guess whether a producer put technical text in a label. That contract is documented
   on the type and is the Phase 2 producer's responsibility.

---

## 9. Next phase (not started)

**Phase 2 — boundary integration, no algorithm change:**
`IvInventoryPostingService.DispatchAsync` catch → build `StockOperatorFailureContext` (action = Post,
origin = Inventory, document kind/number from the batch) → attach `ErpOperatorError` to
`IvInventoryPostingResult`/`BatchResult` *in addition to* the existing message → keep
`ValidationMessageFormat` field dictionaries intact. Then the same for stock count, sales shipment and
purchase settlement.

**STOP.** Awaiting approval to start Phase 2.
