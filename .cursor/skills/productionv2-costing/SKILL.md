---
name: productionv2-costing-guardrails
description: Repository-first, fail-closed costing, stock quantity, WIP, posting, rollback and replay safeguards for productionv2 ERP. Use for Inventory, Procurement, Sales, Production and any change that can influence stock/cost, including UI changes, reports, migrations and transaction workflows.
---

# Productionv2 Costing + Stock Balance Guardrails — v1.1 (development)

## Mission and authority
**Cost/quantity correctness outranks convenience, speed and feature completion.** This is a mandatory review and implementation discipline, **not** an authorization to rewrite costing or turn on historical replay. Current repository code and schema are the source of truth; this skill provides invariants and verification steps, not a substitute for source inspection. Inspect branch `productionv2` of `mokth/net10projectTemplate` at the actual working commit, including local uncommitted changes if working locally. Do not use default `main`, memory, a historical plan, or a manifest as proof of current implementation. Pin the commit, record local diff, trace call sites and read real tests. If repo or database access is unavailable, state that and STOP before approving costing modifications.

**Default stance:** no costing, quantity, CostLayer, valuation, ledger, posting, rollback, or schema changes for a UI-only, wording-only, or unrelated request. No automatic schema changes or migration. No rollout-flag activation without a separately approved readiness gate. Preserve existing supported behavior and explicit conservative blockers. Never bypass a blocker to make a demo pass.

## Mandatory entry procedure (every task)
1. Identify scope, tenant/company, branch, warehouse/location/lot/status, stock item/UOM, costing method and business date. Record current HEAD and diff. Search actual implementations, dependencies, entity mappings, migrations, SQL, feature flags and tests; resolve names from current code rather than guessing.
2. Classify task: **A non-costing UI-only**, **B costing-adjacent** (workflow/report/query/validation), **C costing mutation**, **D historical replay or rollback**. For A: forbid cost/quantity/ledger/schema changes. For B: prove unchanged posting and valuation. For C/D: require explicit impact map, adversarial tests and human approval before implementation.
3. Draw the full path: UI/API → authorization/tenant scope → draft/edit → business validation → transaction/lock → inventory/production ledger & valuation → downstream consumer → rollback/reversal → audit/report/period close. Trace each affected public Post/Rollback entry, not just helpers.
4. Identify current effective costing policy (WAC/FIFO/Standard or any actual method), original posting owner, active valuation generation, inventory physical identity, cost authority and all downstream dependencies. Treat foreign-root restatements and reversed originals distinctly.
5. Write invariants, evidence, failure modes, tests and explicit non-goals **before editing**. Use existing coordinator, services, cost resolvers, transaction patterns and permission checks; do not create parallel calculation engines.
6. Implement minimal bounded changes. Validate before and **again inside** the authoritative lock/transaction; re-read and reject/retry stale plans. Never partially commit. Reject unsupported cases atomically with a business-readable reason.
7. Run focused + cross-module + full regression as applicable, SQL Server concurrency tests for lock/session behavior, reconciliation and period-close checks. A SQLite pass does not prove SQL Server semantics. Report exact commands, pass/fail/skips, commit and untested cases.
8. Output status: VERIFIED / PARTIAL / BLOCKED / NOT VERIFIED. Never claim 10/10, production-ready or a precise completion percentage without full evidence.

**Stock balance is a first-class release gate, not a subsidiary costing check.** For every Inventory, Procurement, Sales or Production task that can touch physical quantity, reservation/availability, UOM, stock count or WIP, load `references/stock-balance.md` and produce a per-slice quantity reconciliation alongside cost reconciliation. A correct valuation with incorrect physical balance is an automatic NO-GO.

## Non-negotiable invariants
- **Physical stock:** per tenant/branch/item/warehouse/location/lot/status/UOM, opening + valid receipts − valid issues ± valid transfers/adjustments = current balance. Transfers must preserve net item quantity; no unexplained negative physical slices or future stock usage.
- **Value:** opening value + valid receipts/adjustments − valid issues/COGS ± supported value-only corrections = current active valuation; reconcile physical ledger, item cost state, FIFO layers and financial reports at matching effective time. Never silently round away a material discrepancy.
- **FIFO:** every issue split consumes identifiable eligible layers exactly once; layer received − consumed + valid reversals = remaining; no negative layer remainder; return/reversal must preserve source allocation and valuation lineage. Do not replace layer-specific return costing with average cost.
- **WAC:** moving average follows supported chronological event order, uses exact persisted precision/rounding rules, and handles zero stock and value-only adjustments without unexplained residuals. Never infer missing historical cost authority.
- **Standard:** use approved effective-dated standard policy and existing variance/PPV treatment; do not silently recost fixed standard issue values as WAC/FIFO.
- **Production:** RM inventory issue = matching material-in contribution, subject to actual existing business rules; every WIP pool movement and inter-operation transfer balances by quantity and value, with identifiable owner/dependency. Output/FG cost includes only valid eligible contributions and allocations; no double capitalization, orphan WIP or lost residuals.
- **Sales:** one and only one physical stock owner for a shipment (DO versus direct Invoice); invoice billing alone must not duplicate stock issue or COGS. Return/CN allocation must tie to the active outbound cost facts; financial-only notes must not mutate stock.
- **Procurement:** GR physical receipt, PI/CN settlement, landed cost/PPV and consumed-versus-on-hand splits must reconcile. Commercial amounts and inventory valuation are different authorities; do not alter document amounts merely to balance replay.
- **Lifecycle:** draft → edit → post → downstream use → rollback/reversal → repost must be idempotent where specified, audit-preserving and atomic. A reversal must exactly undo its eligible active effects, not delete historical evidence. Repost cannot double-apply quantities/costs.
- **Time/period:** future-dated drafts versus future POST follow current business-date policy; period close and period snapshot rules checked at appropriate root lock. Never mutate closed-period business history. Canonical stock-count end-of-day keys and active generations are distinct from legacy midnight history representation.
- **Concurrency:** company/branch scope, database lock order, rowversion/watermark, duplicate-post detection, transaction isolation and SQL-session cleanup must remain correct. Failed validation/exception/cancellation must leave no partial stock, cost, WIP, PO/SO or audit state.
- **Security:** verify access/post/rollback permissions server-side; scope all queries and updates to trusted company/branch, not UI-selected identifiers alone; maintain audit history and original business ownership.

## Replay safety (explicitly incomplete unless proven otherwise)
Read `references/replay-rollback.md` and current manifest. Historical replay may be bounded to specific methods, document shapes and downstream graphs. **Unsupported dependency => refuse entire root atomically**, not best-effort recost. Preserve `OpenPeriodBackdatedReplayEnabled` OFF by default. Never treat an internal simulator, writer or a focused SQLite test as evidence that a public route is enabled or safe. Do not infer production readiness from an estimated 80% figure.

## Required task deliverable
Produce: (1) pinned repo revision + inspected files, (2) exact current flow and affected public routes, (3) risk/invariant matrix, (4) minimal implementation plan or reviewed diff, (5) test evidence including SQL Server and full-suite status, (6) unsupported scenarios and rollback plan, (7) clear GO/NO-GO. For UI-only tasks, explicitly attest **zero costing/stock/WIP/ledger/schema changes** and verify no behavioral change. For high-risk changes, require explicit approval; do not self-approve.

## Load focused references
- **`references/stock-balance.md` (mandatory for any quantity/stock/WIP-affecting route)** for physical balance equations, reservation/availability, stock count, cross-module movement matrix, concurrency and negative-stock guards.
- `references/inventory.md` for physical stock, WAC/FIFO/Standard, count and transfers.
- `references/procurement.md` for GR/PI/CN settlement.
- `references/sales.md` for shipment/COGS/returns.
- `references/production.md` for RM/WIP/FG and dependencies.
- `references/replay-rollback.md` for time, generations, reversals, locking and rollout.
- `references/testing.md` for verification matrix and release gates.
- `references/repository-evidence.md` for source locations and evidence limitations.
