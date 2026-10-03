# Production Stock Ledger Remediation Plan

## Scope and release rule

Repair the P0–P3 quantity-ledger defects in commit `99d4f21`. This is a separate repair plan; P4–P7 features remain in `production_stock_ledger_implementation_plan.md`. V2 activation must remain disabled until the SQL Server scratch-database and restored-database release gates pass. A branch with faulty sealed V2 postings must pause its stock writers and use audited compensating events or a rehearsed recovery procedure; sealed facts must never be edited.

## 1. Deployed schema and compatibility

- Configure EF SQL Server to avoid `OUTPUT` for every table with a seal trigger: `StockPosting`, `IvTrxHistory`, `PrProductionBalLotMovement`, `PrMaterialMovement`, and `PrProductionMovementAllocation`.
- Test real inserts, updates, sealing, and rejection of sealed-row edits against SQL Server. Keep schema changes additive and scripts rerunnable.
- Add a database-enforced write guard so pre-V2 application binaries cannot write stock after the branch epoch is active. Verify the guard blocks old writes while permitting approved V2 postings.

## 2. Atomic posting and identity

- Route production issue, output, handoff, and reversal balance changes through the shared V2 stock writer before facts are saved. Inventory and production legs, live projections, and the sealed posting envelope must verify together in one transaction.
- Assign every material and quantity leg a stable source-line and split identity. Do not append exhausted or rounded-to-zero allocations.
- Freeze stage, location, process, disposition, UOM, conversion, and effective date from validated source identities. Reject required mappings that are missing or ambiguous; do not silently substitute generic stages.

## 3. Lineage, revisions, and event time

- Persist FIFO receipt-to-consumption allocations, including handoff legs. Reversal restores the original allocation contributions. Reject reversal of a receipt with active downstream allocations.
- Corrected revisions copy historically referenced inventory and IP details to new IDs. Posting and rollback read only the applicable generation; old detail IDs, facts, and source snapshots remain intact.
- Resolve an omitted effective instant once per command and reuse it on retries. Validate each touched balance against its latest sealed event, including reversals, and post reversals at a new open-period instant.

## 4. Cutover and period close

- Activate only from an approved manifest of individual warehouse and production balance identities. Require resolved location and lineage exceptions and a matching hash. Acquire the branch stock lock, recheck the manifest under it, then activate.
- Separate legacy V1 history, V2 opening, and V2 subsequent legs in all inventory readers; never count legacy stock plus the opening stock twice.
- Close and reopen with the same branch lock and period authority as posting. Persist immutable, versioned as-of quantity snapshots for inventory and production.

## 5. Quantity inquiry

- Use a single sealed, epoch-scoped ledger query with a captured posting watermark for stock card, movement, as-of, export, and reconciliation. Calculate running balance before server-side pagination.
- Compare ledger and live balances under a consistent snapshot or short branch lock, applying identical identity filters. Report missing bridge and allocation links, with document and reversal drilldowns.
- Enforce page-specific permissions. Provide CSV and Excel quantity exports with safe text escaping. Label valuation `UNVALUED`.

## Verification and release gates

- Service regressions: an issue yields matching V2 inventory and production legs; two 5-unit lots satisfy demands of 6 and 4 without a zero leg; multi-material output has unique source keys; issue → reverse → corrected repost retains old detail IDs and snapshots.
- Run applicable original-plan scenarios T01–T21, T24–T26, T31–T35, T45–T48, T56–T61, T63, and T64. Explicitly prove receipt A cannot be reversed after its contribution is consumed, retries return the original posting, and close cannot miss a concurrent post.
- Run required integration tests on an isolated SQL Server scratch database. Fail test setup if unavailable. Exercise actual triggers, filtered unique indexes, transaction rollback, independent concurrent connections, and migration scripts.
- Rehearse cutover and reconciliation on a representative restored database. Require zero unexplained quantity differences before activation.
- Build the solution and rerun the full suite. Update the two stale schema tests; record the baseline for the remaining 49 prior failures before attributing or fixing them. Require all ledger tests and applicable release gates to pass with no new unrelated failures. Verify report paging, export, and permissions through UI/integration checks.

### Pre-repair test baseline (2026-10-03)

The pre-repair full run in `artifacts/stock-ledger-review/stock-ledger-review.trx` had 2,778 passes and 51 failures. Two were stale `ProductionMaterialIssueSchemaTests` assertions. The other 49 failures were: `ProductionWorkOrderServiceTests` 11, `SaCustServiceTests` 9, `WorkOrderQuantityCalculatorTests` 8, `PrBomStructureTreeTests` 7, `PoSupplierServiceTests` 4, `WorkOrderReadinessValidatorTests` 3, `ProductDefinitionSnapshotLoaderTests` 2, `PrProductDefServiceTests` 2, `SaEInvoiceSelfBillAndTinTests` 2, and `BomExplosionServiceTests` 1. These are a recorded baseline, not an attribution to this repair.

## Deferred

Returns, stock take, valuation, FG receipt, and GL integration are outside this P0–P3 repair plan.
