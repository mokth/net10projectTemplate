# ProductionV2 QA Phase 5 — Non-good Daily Production posting authorization

**Status:** PHASE_5_CODED. Gate 5 STOP.  
**Date:** 2026-11-02  
**Agent:** Cursor Grok 4.6  
**HEAD:** `717f3484846233ca05e45dd51c202f1272c47cfb` (`prodcut defination enhance`)  
**Branch:** `fix/dataprotection-key-ring`  
**Task class:** **C costing-adjacent** (posting authorization for existing WI counters). No valued lots, no `producedCost` split, no extra PRODUCE movement, no inventory scrap, no QAHOLD stock.

Live DBA scripts were **not applied**. `NonGoodEnabled` stays **OFF** by default. No git commit.

---

## 1. What Phase 5 is

Non-good Daily Production qty (Scrap / Reject / Hold) remains a **counter** on the Daily Production document and work-order operation. Quality issues an inspection **grant** that authorizes the existing POST of those counters.

| Action | Stock / cost effect |
|--------|---------------------|
| Approve a `PRODUCTION_OUTPUT_NON_GOOD` case | Insert `QaNonGoodGrant`. No `ProductionBalLot`. No inventory. No cost |
| POST Daily Production with scrap+reject+hold, flag ON | In-tx **Claim** against remaining accepted grant qty |
| POST Daily Production with scrap+reject+hold, flag OFF | No-op (current posting unchanged) |
| POST good qty only | Claim is a no-op (`NonGoodQty == 0`) |
| ROLLBACK Daily Production | Restore allocations `POSTED` → `REVERSED` |
| QA reject of the inspected counter qty | Destination `NOT_STOCK` — not inventory scrap |

**Not implemented (explicitly out of Gate 5 PASS for valued lots):** extra `ProductionBalLot` rows for scrap/reject/hold, splitting `producedCost` off Good, a second PRODUCE movement, QAHOLD stock, inventory scrap from WI counters.

Creating those lots would break the sealed Daily Production invariant that absorbed conversion cost requires **exactly one** current forward PRODUCE movement, and PADJ consumes `Kind == Wip` with no stage filter.

---

## 2. Costing / stock-balance attestation (class C)

Inspected posting owner:

- `ProductionOutputService.Posting.cs` — `ClaimDailyProductionAsync` after `processed` is computed, before remaining-good checks
- `ProductionOutputService.Rollback.cs` — `RestoreDailyProductionAsync` after POSTED status check

Unchanged:

- Good qty still creates/increments `ProductionBalLot`
- `producedCost` still applies to Good only
- Absorbed conversion cost still requires exactly one PRODUCE movement
- Scrap / Reject / Hold still increment operation/WO counters only
- FIFO / WAC / standard formulas
- No new inventory trx type
- No `IStatus` mutation

**Stock-balance.md:** counters are not on-hand. Grant remaining is posting permission, not a warehouse pile.

---

## 3. Code delivered

| Area | Path |
|------|------|
| Schema | `QaNonGoodGrant`, `QaNonGoodAllocation`; `scripts/create-qa-nongood.sql` (**write only, not executed live**); `QaPolicy.NonGoodEnabled` in `create-qa-foundation.sql` |
| Policy | `NonGoodEnabled` savable, default OFF |
| Authorization | `QaNonGoodAuthorizationService` Claim / Restore; disabled no-op when flag OFF |
| Posting hooks | Daily Production POST claim + rollback restore |
| Case | Approve issues grants; source search includes NEW or Posted WI with scrap/reject/hold > 0 |
| UI | Settings checkbox; Daily Production **Request QA (non-good)**; case `outputId` prefill |
| Destinations | Sealed allocations → `EXECUTED` Daily Production view; remaining qty is `NOT_STOCK` (not Phase-5-later, not inventory scrap) |

Existing Daily Production remains the only owner of those counters. Quality does not insert `ProductionBalLot` or `IvTrxBatch` rows.

---

## 4. Tests

Command:

```
dotnet build ErpWeb.Tests --no-restore
dotnet test ErpWeb.Tests --no-build --filter "Category=Quality"
```

Quality suite: **64 Passed** (previous 56 plus non-good grant, policy, and destination updates).

Covered:

- Flag OFF: claim is a no-op
- Flag ON, no grant: fail-closed
- Grant 4, claim 4 ok; claim 5 blocked
- Restore reverses
- Approve issues grant without `ProductionBalLot` / inventory / cost
- Policy can enable non-good
- Destinations: `NOT_STOCK`, not “Phase 5 later”; sealed allocation is `EXECUTED`

**Not run:** SQL Server FIFO/WAC/standard ledger tests (no new ledger command), live tenant script apply, browser visual matrix.

---

## 5. What Gate 5 does not claim

- Valued lots for scrap/reject/hold
- Splitting absorbed conversion cost
- Inventory scrap from Daily Production counters
- Enabling `NonGoodEnabled` in a live tenant
- IMPLEMENTATION_10_OF_10

---

## 6. Gate 5 verdict

| Item | Verdict |
|------|---------|
| PHASE_5_CODED | **YES** (inspection grant for counters) |
| Valued lots / cost split | **NOT IMPLEMENTED** |
| SQL Server ledger tests for a new command | **NOT RUN** (no new command) |
| Existing Daily Production remains the only counter owner | **YES** |
| Flags default OFF / live SQL not applied | **YES** |
| HEAD still `717f348` / no agent commit | **YES** |
| Browser visual matrix | **NOT RUN** |
| IMPLEMENTATION_10_OF_10 | **NOT CLAIMED** |

**STOP.** Do not invent valued lots or a second PRODUCE movement. Do not apply `create-qa-*.sql` against a live tenant in this change set. Do not enable non-good inspection in live tenants without a DBA script and a separate readiness gate.
