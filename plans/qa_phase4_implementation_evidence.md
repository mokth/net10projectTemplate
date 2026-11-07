# ProductionV2 QA Phase 4 — Physical warehouse reservation

**Status:** PHASE_4_CODED. Gate 4 STOP.  
**Date:** 2026-11-02  
**Agent:** Cursor Grok 4.6  
**HEAD:** `717f3484846233ca05e45dd51c202f1272c47cfb` (`prodcut defination enhance`)  
**Branch:** `fix/dataprotection-key-ring`  
**Task class:** **C costing-adjacent** (reservation on an existing `IvBalLoc` slice). No new ledger command, no FIFO/WAC/standard formula change, no `IStatus` conversion, no `ACTIVE`→`QCHOLD` movement.

Live DBA scripts were **not applied**. `PhysicalHoldEnabled` stays **OFF** by default. No git commit.

---

## 1. What Phase 4 is

Physical hold is a **reservation** on the exact warehouse pile (`IvBalLoc` Id + item/warehouse/location/lot/`IStatus`). It is **not** quarantine and **not** a status conversion.

| Action | Stock effect |
|--------|----------------|
| Approve HOLD/REJECT on Posted GRN or inventory stock, flag ON | Insert `QaPhysicalHold`. `StdQty`, `Cost`, `IStatus` unchanged |
| MI / SP / IP / DO / invoice / ADJ (net decrease) / count (via ADJ) | Remaining on-hand after the take must cover unclaimed reserved qty |
| QCHOLD pile, flag ON | Blocked except approved scrap or vendor return |
| SC / VR (including PoCdn stock-out as `VendorReturn`) | In-tx **Claim** of remaining reserved qty; idempotent by batch/detail |
| TR | Hold-preserving geo move is allowed only when reserved qty is 0. Transfer of a reserved pile is refused. Transfer is not a quality release |
| Rollback of a claimed SC/VR | Restore allocations `POSTED` → `REVERSED` |
| Release hold | Hold `ACTIVE` → `RELEASED`. Pile qty/cost/status unchanged |

**Not implemented (explicitly out of Gate 4 PASS for a new command):** `ACTIVE−Q` / `QCHOLD+Q` ledger conversion, FIFO layer ID preservation under status reclass, a new inventory trx type. SQL Server FIFO/WAC/standard ledger tests for a conversion command were **not run** because that command does not exist.

---

## 2. Costing / stock-balance attestation (class C)

Inspected posting owners that now call `IQaPhysicalHoldAuthorization`:

- `PostInventoryMICoreAsync` (MI, SC, VR, SP, IP, DO, invoice, PoCdn stock-out)
- `RollBackInventoryMICoreAsync` restore
- `PostInventoryADJCoreAsync` remaining check on negative net
- `PostInventoryTRCoreAsync` remaining / reserved-pile block
- WAC / FIFO / Standard **MI** replay insert + rollback restore
- WAC / FIFO / Standard **TR** replay remaining check
- Stock count via `PostStockAdjustmentInTransactionAsync` (ADJ core)

Unchanged:

- FIFO layer pooling (company+branch+item; `IStatus` is not in the financial pool)
- WAC / standard cost formulas
- Production absorbed-cost
- `IvBalLoc` unique slice including `IStatus`
- TR still copies the same `IStatus` from/to; it is not a QA conversion

**Stock-balance.md:** reservation ≠ issue. On-hand is unchanged until an existing document posts. Available for ordinary outflow is on-hand minus unclaimed reserved qty when the flag is ON.

---

## 3. Code delivered

| Area | Path |
|------|------|
| Schema | `QaPhysicalHold`, `QaPhysicalAllocation`; `scripts/create-qa-physical-hold.sql` (**write only, not executed live**) |
| Policy | `PhysicalHoldEnabled` savable, default OFF |
| Authorization | `QaPhysicalHoldAuthorizationService` Place / Release / AssertAndClaim / Restore |
| Posting hooks | `IvInventoryPostingService` MI/ADJ/TR core + MI/TR replay adapters |
| Case | Place after logical hold; `PhysicalHeldQty`; close blocked while remaining > 0 |
| UI | Settings checkbox; case Physical reserved chip; Release hold |
| Destinations | Sealed physical SC/VR allocations → `EXECUTED`; remaining reserved is a reservation caption, not Phase-4 conversion |

Existing transactions remain the only stock posting owners. Quality does not insert `IvTrxBatch` rows.

---

## 4. Tests

Command:

```
dotnet build ErpWeb.Tests --no-restore
dotnet test ErpWeb.Tests --no-build --filter "Category=Quality"
```

Quality suite: **56 Passed** (previous 46 plus physical-hold and destination updates).

Covered:

- Flag OFF: approve and QCHOLD MI are no-ops
- Flag ON: HOLD 20 of 100 does not change `StdQty` / `Cost` / `IStatus`
- MI 80 allowed, 81 blocked
- QCHOLD MI blocked; SC claims; restore reverses; TR of reserved pile blocked
- Policy can enable physical hold
- Destinations: scrap stays unverified until a sealed allocation; reservation caption is not QCHOLD conversion

**Not run:** SQL Server concurrency / FIFO/WAC/standard conversion replay, live tenant script apply, browser visual matrix.

---

## 5. What Gate 4 does not claim

- Physical `ACTIVE`→`QCHOLD` ledger conversion
- QCHOLD as quarantine in master data
- Phase 5 non-good Daily Production valued lots
- Enabling `PhysicalHoldEnabled` in a live tenant
- IMPLEMENTATION_10_OF_10

---

## 6. Gate 4 verdict

| Item | Verdict |
|------|---------|
| PHASE_4_CODED | **YES** (reservation architecture) |
| ACTIVE→QCHOLD conversion | **NOT IMPLEMENTED** |
| SQL Server ledger tests for a new command | **NOT RUN** (no new command) |
| Existing transactions remain the only stock posting owners | **YES** |
| Flags default OFF / live SQL not applied | **YES** |
| HEAD still `717f348` / no agent commit | **YES** |
| Browser visual matrix | **NOT RUN** |
| Phase 5 | **CODED separately** — see `plans/qa_phase5_implementation_evidence.md` |
| IMPLEMENTATION_10_OF_10 | **NOT CLAIMED** |

**STOP.** Do not start Phase 5. Do not apply `create-qa-*.sql` against a live tenant in this change set. Do not enable physical hold in live tenants without a DBA script and a separate readiness gate.
