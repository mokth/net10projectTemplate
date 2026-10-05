# Stock ledger P2 writer/reader coverage

Reference scan: 2026-10-03. This is a release checklist, not an activation approval.

## Inventory posting entry points

- `IvInventoryPostingService`: MR/CR/GR/NG, MI/SC/VR, TR and ADJ post/rollback cores.
- `IIvInventoryPostingService`: all five `*InTransactionAsync` methods and NEW stock-in deletion.
- `IvStockPostingRepository`: `LoadHistoryForBatchAsync`, generation-scoped history loading,
  duplicate detection, history insertion and legacy-only `RemoveHistory`.
- V2 rollback rule: `IvInventoryHistoryWriter.AppendReversal` swaps FROM/TO snapshots and retains
  the original. `RemoveHistory` rejects V2 rows. V1 keeps its existing delete behavior.

## Outer in-transaction owners

- `Sales/SaInvoiceService.cs`: SP post and rollback.
- `Sales/SaDoService.cs`: SP post and rollback.
- `Sales/SaCdnService.cs`: CR post/rollback and NEW batch deletion.
- `Purchase/PoCdnService.cs`: MI post/rollback and NEW batch deletion.
- `Inventory/IvStockCountService.cs`: ADJ post.
- `Production/ProductionMaterialIssueService.Lifecycle.cs`: IP post.
- `Production/ProductionMaterialIssueService.Rollback.cs`: IP rollback.

Every listed owner must supply one `StockPostingContext` when an ACTIVE epoch exists. A nested
inventory adapter must not start a transaction or create a second envelope.

## Production balance writers

- `ProductionMaterialIssueService.Lifecycle.CreateMaterialInLotsAsync`: ISSUE balance creation.
- `ProductionMaterialIssueService.RollbackAsync`: ISSUE_REVERSAL.
- `ProductionOutputService.PostAsync`: CONSUME, PRODUCE and process handoff movements.
- `ProductionOutputService.RollbackAsync`: consume/produce/handoff reversals.
- `ProductionBalLotOpening`: legacy/opening balance creation.
- `ProductionStockWriter`: sole target V2 projection writer; deterministic balance/source-line
  ordering, no negative clamping, append-only movement facts.
- `ProductionContributionAllocator`: immutable FIFO contribution ordering and shared budgets.

## Readers requiring generation/epoch semantics

- `IvStockHistoryRepository`
- `IvStockCommonRepository`
- `IvTrxHistoryService`
- `InventoryAsOfStockService`
- `IvInventoryReconciliationService`
- `IvPeriodCloseService` and snapshot helper
- `ProductionBalanceInquiryService`
- Production material totals/dependency readers

V2 readers include all sealed generations and reversals for chronology. Operational “current
generation” lookups use document revision/posting identity and must not collapse reversal pairs.

## Direct DbSet scan

`IvTrxHistories`, `ProductionBalLots`, and `ProductionBalLotMovements` also occur in service and
test fixtures. Tests are not production writers. Direct production mutations outside the writer
remain an activation blocker until routed through `ProductionStockWriter`.

## Activation blockers

- [x] All outer owners pass one shared context.
- [x] All inventory post cores stamp V2 history and all rollback cores append reversals.
- [x] Material issue post/rollback use one inventory + production envelope.
- [ ] Output/handoff post and rollback still stamp facts after inline mutation; `ProductionStockWriter` is the V2 mutator for new plans and tests.
- [x] Chronology uses effective instant plus posting sequence (not date-only or mutable balance date).
- [x] Branch timezone resolves the effective instant server-side.
- [x] Production stock-card readers select active-epoch V2 semantics explicitly; inventory card remains V1-compatible with generation-scoped history.
- [x] Quantity close writes immutable `StockPeriodSnapshotHdr/Line` revisions when an ACTIVE epoch exists.
- [x] SQL Server atomicity and idempotency tests exist and fail when `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` without a scratch database.

## Production V2 cost gates and cutover

Forward Issue to Production and Daily Production require an ACTIVE V2 epoch, verified cost
lineage, and a sealed `StockPosting` in the same transaction. Do not backfill `VERIFIED` onto
legacy production pools. Activation remains a manual DBA step.

Required order:

1. Reverse FG, then later Daily, then earlier Daily/handoffs, then Issue to Production.
2. Repair inventory `UnitPrice` / `PriceEvidence` on locked `IvBalLoc` rows.
3. Confirm usable legacy positive `PrProductionBalLot` count is zero (or explicitly quarantined
   `UNVALUED` and unused by new Daily/FG posting).
4. `scripts/preflight-production-stock-ledger.sql`
5. Schema scripts: `scripts/create-stock-posting-ledger.sql`,
   `scripts/alter-inventory-history-ledger.sql`, `scripts/alter-production-stock-ledger.sql`
6. `scripts/create-stock-ledger-write-guard.sql` before activation
7. SQL Server scratch/restored-database release gates
8. `scripts/activate-stock-ledger-epoch.sql`
9. `scripts/verify-stock-ledger-cutover.sql`
10. Repost IP, then Daily in route/process order, then FG

