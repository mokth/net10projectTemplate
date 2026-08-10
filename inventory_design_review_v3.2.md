# Inventory Design Review v3.2 — Review Only

> **Status:** REVIEW / HISTORY only. **Do not use this file for coding.**
>
> **Coding contract:** [`inventory_new_design_v3.2.md`](inventory_new_design_v3.2.md)

---

## Purpose

This file records the professional review conclusions that led to the FINAL implementation contract. Coding agents must implement from `inventory_new_design_v3.2.md` only.

---

## Architecture verdict

| Item | Decision |
|---|---|
| Core ledger architecture | **LOCKED / APPROVED** — do not redesign |
| Overall architecture quality | ~9.7/10 |
| Contract readiness after final polish | FINAL — Phase 0 approved to code |
| Full-module AI implementation | **FORBIDDEN** |
| Implementation style | One phase → human review → next phase |

---

## Locked architecture (do not redesign)

```text
InventoryDocument → InventoryDocumentLine → StockMovementAllocation → StockLedger
                                                      ├── StockBalance (qty cache)
                                                      ├── ItemCost (MAV cost only)
                                                      └── LotBalance (Phase 3)
```

- StockLedger = historical truth (immutable after post)
- StockBalance = operational qty cache
- Corrections = compensating reversal
- Historical/as-of from StockLedger — never copy today’s StockBalance
- V1 costing = Moving Average only
- Same-branch transfer; transfer preserves source cost
- Phase 2 non-lot; Phase 3 lot-aware
- ErpWeb cookie auth + `ICompanyContext`; SQL scripts + EF Fluent

---

## Review series

1. Sync review vs live ErpWeb (CompanyCode / Branch / RBAC / no Tenant)
2. User professional review → v3.1 contract
3. Second hardening review → v3.2 (ItemCost grain, phases, concurrency)
4. Final clarifications → 19 non-negotiables + six wording locks (3 total attempts, Phase-2-only allocation, batch/non-batch Lot, Phase-3 transfer LotId, auto MAIN, no-scope-expansion)

---

## What was deferred (do not implement in V1)

FIFO, Serial, Reservation, ASM/DSM, Outbox, GL, Landed Cost, cross-branch transfer.

---

## Next coding step

Implement **Phase 0 only** (`ICompanyContext` + `Branch`) from [`inventory_new_design_v3.2.md`](inventory_new_design_v3.2.md).
