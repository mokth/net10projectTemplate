# ProductionV2 QA — Gate 5 closeout (UAT / DBA order)

**Status:** CLOSEOUT_CODED. Plan 0→5 is complete. IMPLEMENTATION_10_OF_10 is **not** claimed.  
**Date:** 2026-11-02  
**HEAD:** `717f3484846233ca05e45dd51c202f1272c47cfb`  
**Live SQL:** **not applied.** Flags stay **OFF**. No git commit.

This is the v4 post-Gate-5 step: UAT signoff package and remaining document-side Request QA. It is **not** valued lots, **not** `ACTIVE`→`QCHOLD` conversion, and **not** production rollout.

---

## 1. What this closeout is

| Item | Result |
|------|--------|
| Posted goods receipt Request QA | Uses `POSTED_GRN` + `sourceHeaderId` (physical-hold source). Draft GR still uses `INCOMING_PRE_GRN` |
| Posted miscellaneous receipt Request QA | Uses `INVENTORY_STOCK` + `sourceHeaderId` |
| DBA apply order | Written below. **Do not run against a live tenant in this change set** |
| Browser visual matrix | **NOT RUN** (app login / live tenant not used in this change set) |
| Valued lots / cost split | **NOT IMPLEMENTED** (would break the single PRODUCE movement) |
| Physical `IStatus` conversion | **NOT IMPLEMENTED** (would need a new ledger command) |

---

## 2. DBA apply order (write only — do not execute live here)

Run as a DBA, once per database, idempotent scripts in this order:

1. `scripts/init-qa-menu.sql`
2. `scripts/create-qa-foundation.sql`
3. `scripts/create-qa-receipt-grant.sql`
4. `scripts/create-qa-fg-grant.sql`
5. `scripts/create-qa-logical-hold.sql`
6. `scripts/create-qa-physical-hold.sql`
7. `scripts/create-qa-nongood.sql`

Then enable flags **per tenant** from Quality Settings only after a separate readiness gate. Default remains OFF.

Backout is not a drop: leave tables in place and keep flags OFF.

---

## 3. Remaining blockers (do not self-approve)

| Blocker | Why 10/10 is not claimed |
|---------|--------------------------|
| SQL Server FIFO/WAC/standard ledger tests | Not run. SQLite Quality suite is not SQL Server proof |
| Live schema | Scripts not applied |
| Browser visual matrix | Desktop/tablet/phone, light/dark, 125% |
| Valued non-good lots | Requires a separate cost-allocation design |
| Physical QCHOLD conversion | Requires a new ledger command, not TR |
| ERPOrigin study | EXTERNAL_REFERENCE_UNAVAILABLE |

---

## 4. UAT checklist (manual)

- Quality Settings: all five gates default OFF; save one at a time; copy never says valued lot or QCHOLD conversion
- Workbench: new case for each source type; source picker pages; inspector cannot approve own case
- Incoming: Request QA from draft GR → inspect → approve → POST GR with flag ON
- FG: Request QA from FG receipt → approve → POST FG
- Logical hold: WIP lot Hold → available reduced, no warehouse move
- Physical hold: posted GR/MR Hold → pile qty/cost/status unchanged; MI blocked past reserved
- Non-good: Request QA (non-good) → approve → POST scrap/reject/hold; no `ProductionBalLot`
- Inquiry: source → disposition → destination; EXECUTED only from sealed allocations
- Rollback of GR / FG / WI / SC / VR restores QA allocations

---

## 5. Verdict

| Item | Verdict |
|------|---------|
| Phases 0→5 coded | **YES** |
| Closeout document Request QA | **YES** (posted GR + posted MR) |
| IMPLEMENTATION_10_OF_10 | **NOT CLAIMED** |
| Production rollout | **NO** |

**STOP.** Do not invent valued lots or QCHOLD conversion. Do not apply `create-qa-*.sql` against a live tenant here. Do not enable flags without a DBA script and a separate readiness gate.
