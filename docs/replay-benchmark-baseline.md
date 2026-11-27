# Productionv2 Backdated Replay — SQL Server Benchmark Baseline

Recorded on **2026-11-27** against branch `productionv2` (`ErpWeb.Core\StockLedger\StockReplayValuationGenerationResolver.cs` `O(N)` `ToLookup` fix applied) using disposable `.\SQLEXPRESS` scratch database `ErpWeb_Bench_6628ac9ccaf` (SQL Server 2022 Express Edition 64-bit, `RECOVERY SIMPLE`; live `ERPWeb` untouched and scratch database dropped after run).

- **Reproducible Harness:** `scripts/testing/ReplayBenchmark/ReplayBenchmark.csproj` (`--sql-server '.\SQLEXPRESS' --background-rows 5000000`)
- **Background Database Volume (`sys.dm_db_partition_stats`):**
  - `IvTrxHistory`: **5,072,622 rows**
  - `StockValuationFact`: **5,072,622 rows**
  - `StockPosting`: **322,622 rows**
  - `StockFifoLayer`: **55,636 rows**
  - `StockFifoLayerConsumption`: **105,625 rows**
  - Database footprint: **9,224.0 MB** data + **2,824.0 MB** log (seeded in **570.9s**)

---

## 1. Query Plan Verification (`SET STATISTICS XML ON` + `SET STATISTICS IO ON`)

All 7 core replay, snapshot, and FIFO queries executed cleanly via **Index Seek** (`0` full table scans / `0` clustered index scans on the 5M-row database). Runtime and logical reads scale with the affected tail, independent of total historical volume.

| Query | Elapsed (ms) | Logical Reads | Scan? | Primary Physical Operator / Index |
| --- | ---: | ---: | --- | --- |
| `StockPosting.LatestSealed` | 25.84 | 101 | NO (Seek) | `Index Seek [StockPosting][UQ_StockPosting_BranchSequence]` + `Clustered Index Seek [PK_StockPosting]` |
| `PhysicalTimeline.ReadLater(N=100)` | 9.96 | 425 | NO (Seek) | `Index Seek [IX_IvTrxHistory_ReplayFromPile]` + `[IX_IvTrxHistory_ReplayToPile]` + `[PK_IvTrxHistory]` |
| `PhysicalTimeline.ReadLater(N=10K)` | 23.62 | 40,697 | NO (Seek) | `Index Seek [IX_IvTrxHistory_ReplayFromPile]` + `[IX_IvTrxHistory_ReplayToPile]` + `[PK_IvTrxHistory]` |
| `ValuationFact.ItemEffectiveAtSeek` | 6.27 | 76 | NO (Seek) | `Index Seek [StockValuationFact][IX_StockValuationFact_ItemEffectiveAt]` (10,000 rows) |
| `ValuationFact.ByHistorySeek` | 5.57 | 9 | NO (Seek) | `Index Seek [StockValuationFact][IX_StockValuationFact_History]` + `Clustered Index Seek` |
| `ValuationFact.PeriodSnapshotSeek` | 277.53 | 12,568 | NO (Seek) | `Index Seek [StockValuationFact][IX_StockValuationFact_Period]` (72,622 period rows) |
| `FifoLayerConsumption.ByLayerSeek` | 71.70 | 3 | NO (Seek) | `Index Seek [StockFifoLayerConsumption][IX_StockFifoLayerConsumption_Layer]` |

---

## 2. Planner & Locked Validation Scaling (`100` / `1,000` / `10,000` / `50,000` Events)

| Workload | Item | Tail Events | Planning (ms) | Lock-Validation (ms) | Plan Queries | Lock Queries | Alloc (MB) |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `WAC_PLAN_AND_VAL` | `WAC-100` | 100 | 727.5 | 50.4 | 37 | 27 | 16.50 |
| `WAC_PLAN_AND_VAL` | `WAC-1K` | 1,000 | 1546.7 | 234.4 | 37 | 27 | 56.82 |
| `WAC_PLAN_AND_VAL` | `WAC-10K` | 10,000 | 1164.9 | 446.4 | 37 | 27 | 403.64 |
| `WAC_PLAN_AND_VAL` | `WAC-50K` | 50,000 | 3775.1 | 2157.9 | 37 | 27 | 1983.22 |
| `FIFO_PLAN_AND_VAL` | `FIFO-100` | 100 | 241.9 | 12.0 | 26 | 15 | 12.78 |
| `FIFO_PLAN_AND_VAL` | `FIFO-1K` | 1,000 | 791.8 | 105.8 | 26 | 15 | 131.67 |
| `FIFO_PLAN_AND_VAL` | `FIFO-10K` | 10,000 | 13794.7 | 834.8 | 26 | 15 | 6484.77 |

**Optimization note:** Prior to fixing an `O(N^2)` in-memory linear scan (`facts.Where(x => x.InventoryHistoryId == history.Id)`) in `StockReplayValuationGenerationResolver.ResolveActive` (`ToLookup`), `WAC-50K` lock-validation required **37,172.6 ms** on 50k background; after replacing it with an `O(N)` `ToLookup`, `WAC-50K` dropped to **2,157.9 ms** on 5M background (`~17x` faster). Notice `FIFO-10K` planning still allocates **6,484.77 MB** in `StockReplayFifoLayerAllocator.AllocateIssue` due to layer array cloning across issues, justifying keeping the interactive online limit far below `10K`.

---

## 3. End-to-End Public `IvInventoryPostingService.PostAsync` on 5M Database

| Method | Item | Tail Events | Expected | Actual | Planning (ms) | Lock-Hold (ms) | Total (ms) | Queries | Alloc (MB) | Outcome |
| --- | --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| `WAC` | `WAC-10` | 10 | PASS | PASS | 825.1 | 876.3 | 1702.0 | 228 | 39.43 | `POSTED` |
| `WAC` | `WAC-50` | 50 | PASS | PASS | 116.4 | 594.0 | 710.6 | 489 | 40.73 | `POSTED` |
| `WAC` | `WAC-100` | 100 | PASS | PASS | 131.8 | 696.3 | 828.5 | 815 | 80.83 | `POSTED` |
| `WAC` | `WAC-101` | 101 | REFUSE | REFUSED | 148.5 | 0.0 | 148.5 | 74 | 9.84 | Refused before lock (`<= 100` limit) |
| `WAC` | `WAC-1K` | 1,000 | REFUSE | REFUSED | 543.0 | 0.0 | 543.0 | 74 | 68.51 | Refused before lock (`<= 100` limit) |
| `FIFO` | `FIFO-10` | 10 | PASS | PASS | 269.4 | 427.7 | 697.4 | 248 | 25.91 | `POSTED` |
| `FIFO` | `FIFO-50` | 50 | PASS | PASS | 153.9 | 215.3 | 369.5 | 248 | 43.35 | `POSTED` |
| `FIFO` | `FIFO-100` | 100 | PASS | PASS | 126.2 | 204.8 | 331.3 | 248 | 79.47 | `POSTED` |
| `FIFO` | `FIFO-101` | 101 | REFUSE | REFUSED | 41.8 | 0.0 | 41.8 | 72 | 11.81 | Refused before lock (`<= 100` limit) |
| `FIFO` | `FIFO-1K` | 1,000 | REFUSE | REFUSED | 527.9 | 0.0 | 527.9 | 72 | 142.41 | Refused before lock (`<= 100` limit) |

---

## 4. Competing-Writer Contention & Certified Online Envelope

- **Configured `BranchStockTransactionLock` (`sp_getapplock`) Acquisition Timeout:** `15,000 ms` (retained unchanged; never raised as a workaround).
- **100-Event Concurrent Replay + 3 Concurrent Branch Writers (`WAC-100-CONT` + `WAC-COMPETE`):**
  - Replay succeeded: `True` (`~416.0 ms` concurrent hold; max observed `WAC-100` lock hold across runs: `696.3 ms`).
  - All 3 competing writers succeeded: **0 timeouts** (`max wait = 0.3 ms`, `mean writer total = 948.2 ms`), leaving **>95% lock-wait headroom** below the `15,000 ms` acquisition limit even under full `696 ms` replay holds.
- **Certified Online Envelope:** Retain the existing **100-event** online affected-tail limit across `CheckSimpleWacTailAsync`, `CheckSimpleFifoInventoryTailAsync`, and dependent routes (`< 0.9s` end-to-end lock hold, `< 81 MB` managed allocation, `0` timeouts, fail-closed refusal at `0.0 ms` lock hold when exceeded).
