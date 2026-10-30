# Costing test matrix and release gates

## Minimum evidence by change class
A UI-only: compile/UI tests and diff proving no service, entity, SQL, ledger, valuation or posting mutation. B costing-adjacent: A + affected posting/rollback and reporting regressions. C costing mutation: B + all relevant WAC/FIFO/Standard, quantity/value, lineage, partial, error-injection and cross-module tests. D replay/rollback: C + SQL Server concurrency/session, historical event-order, active-generation, month-close, full regression and performance evidence.

## Required scenarios (mark each PASS/FAIL/SKIP/NOT RUN)
- Normal post; rollback; repost; duplicate post; retry after timeout; two simultaneous users; transaction cancellation; exception between physical and financial writes.
- Partial multi-line post and rollback; split receipts/issues; transfers; mixed UOM/lot/status; negative historical stock; future draft and future post; closed and reopened periods.
- WAC: zero stock, rounding, value-only events, count reset and backdated receipt. FIFO: multiple layers, partial consumption, return allocation, consumed return layer and reversal. Standard: effective-dated policy and variance.
- Procurement: GR/PI/CN/PPV consumed/on-hand; Sales: DO/direct Invoice/COGS/returns; Production: IP/WIP/output/WO transfer/FG/return and downstream sale.
- Backdated insertion/removal; multiple active corrections; exact inverse facts; original-owner lineage; stale plan/rowversion; concurrent branch locks; SQL pooled-session marker cleanup.
- Month-end snapshot reconciles physical, item cost state, layers, valuation, WIP and COGS. Closed-period write refused with no side effects.

## Verification rules
Run commands against pinned current revision and report their actual exit codes, counts, failures, skipped tests and DB provider. **SQLite cannot validate SQL Server locking/session semantics.** A focused green suite does not replace a full green regression. Do not hide existing failures: classify baseline-versus-new with evidence. No automatic production rollout, migration, data repair or forced backfill.

## Release gate (all must pass)
[ ] Supported transaction matrix documented and implemented at public entrypoints.
[ ] All affected physical, financial, Production and commercial dependencies reconcile.
[ ] Every rollback/repost path audited and idempotence/lineage proven.
[ ] SQL Server concurrency, session cleanup, deadlock/retry and atomicity proven.
[ ] Full regression green or formally triaged with no unresolved critical/high regression.
[ ] Month-end, historical as-of, closed-period and future-date rules proven.
[ ] Performance, migration and rollback/runbook approved.
[ ] Explicit human sign-off and feature flag rollout plan.

If any box fails: **NO-GO**. Never score 10/10 or declare production-ready merely because requested.
