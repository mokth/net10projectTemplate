# Historical replay, rollback, time and concurrency

## Current known architecture (verify at current commit)
`ErpWeb.Core/StockLedger/StockPostingCoordinator.cs` exposes `ExecuteAsync`, `BeginInTransactionAsync`, normal `CompleteInTransactionAsync`, replay `CompleteReplayInTransactionAsync` and `AbortInTransactionAsync`. `IvStockCountService` reads `OpenPeriodBackdatedReplayEnabled`. `plans/productionv2_backdated_replay_impact_manifest.md` documents many bounded routes and unimplemented dependencies; **the manifest is a progress log, not proof**.

## Mandatory replay protocol
1. Determine effective business timestamp, canonical count end-of-day ordering, original posting sequence/line identity and attached correction ordering; distinguish transaction date from audit timestamp.
2. Identify active physical/financial/Production generations, owner history and all downstream consumers. Exclude valid reversals while retaining audit evidence; do not confuse replay-root ownership with original document ownership.
3. Build immutable impact plan before lock where required, fingerprint epoch, branch watermark, affected slices, cost state, active generations and dependent graphs.
4. Acquire the existing branch transaction lock; revalidate full plan and period/freeze/business-date/permissions inside the same SQL transaction. Stale evidence => retry outside lock or fail without side effects.
5. Append exact reversal/replacement facts and original-owner lineage, recompute physical and financial tail in deterministic order, update live balances/layers/cost states and all eligible WIP/COGS/settlement descendants, then seal with existing replay completion validation.
6. Any unsupported method, incomplete graph, missing cost authority, negative historical stock, closed period, malformed lineage, duplicate generation, conflicting rowversion or SQL failure => rollback entire root. Do not silently skip a dependent transaction.
7. Verify after commit: quantity/value reconciliation, active timeline/as-of, audit trace, PO/Sales/Production owners, month-end valuation. Repeated rollback/repost must not double-apply.

## Rollout and known limitations
Keep `OpenPeriodBackdatedReplayEnabled` OFF in production until explicit approved readiness. At last checked manifest, WAC MR/GR can recost one later **unconsumed** V2 Production material issue, but consumed Production tails, multiple issues, mixed Sales dependencies, combined GR/PI settlement, FIFO/Standard Production propagation and SQL Server concurrency were still open. Recheck live code and latest manifest before any claim. Never infer full coverage from an internal writer or focused tests.
