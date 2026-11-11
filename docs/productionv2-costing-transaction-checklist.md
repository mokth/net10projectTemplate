# Stock and costing changes: developer checklist

Use this checklist whenever a new or changed transaction can affect physical stock, reservations, valuation, WIP, settlement, COGS, or rollback. A transaction is not safe merely because it calls a shared service.

1. Map each public post, rollback and repost route, its permissions and trusted company/branch, stock item, warehouse, location, lot, status, base UOM, business date and document owner. Identify affected Inventory, Procurement, Production, Sales, QA and Subcontract records and active generations.
2. Use the existing posting coordinator, valuation and replay services where applicable. Keep transaction-specific authority in its owner module. Plan dependencies before mutation, then recheck the plan, source evidence, stock, period, policy and concurrency tokens under the existing branch transaction lock. Unsupported or unproven histories must fail without partial writes.
3. Reconcile each physical slice and item-level quantity/value before and after post, rollback and permitted repost. Check WAC, FIFO layers or Standard Cost as applicable, and reconcile affected WIP, settlement, COGS and return allocations. Preserve immutable facts, original owners and audit lineage.
4. Add transaction-specific tests for normal and backdated post/rollback; later receipts, issues and other applicable dependencies; negative historical stock; closed periods; QA holds; failed writes; duplicate and competing requests; and month-end reconciliation. Include both successful paths and atomic refusal of unsupported shapes. Add a regression showing existing transactions remain correct when the new transaction is inserted or removed.
5. Run the focused gate below, all additional affected-module tests and the full regression suite before release. For replay or locking changes, also run relevant SQL Server tests against a disposable database. Record pass/fail/skip counts, database provider, unsupported combinations and any tests not run.

## Repeatable focused gate

From the repository root, after restoring the project's dependencies:

```powershell
powershell -NoProfile -File scripts/testing/run-costing-regression.ps1
```

Add `-IncludeSqlServer` to also run the selected receipt replay concurrency and failure tests in newly created, identity-checked scratch databases on `.\SQLEXPRESS`. Specify another test server with `-SqlServer`; the account must be allowed to create and remove its own scratch databases. The script checks the selected test groups and fails on test failures, missing required groups or skipped tests. `-NoBuild` uses the existing test build.

The gate is a **focused subset**. Its SQL checks cover selected receipt scenarios, not every transaction, costing method or cross-module dependency. Extend the selected groups and scratch scenarios when the transaction impact map requires it. It does not prove production readiness or replace the full suite, performance checks and manual review in the replay plan.

The current Subcontract inventory and Production posting adapters are blocked. The focused gate runs their existing blocking tests so stock and WIP posting cannot be enabled without replacing that safeguard with the required costing, replay and rollback coverage.

Some existing SQL Server tests read the application's default connection string. Do not run the full suite against an operational database; first configure and verify a disposable test database and the selected test fixtures. The focused gate overrides the default connection and clears selected test connection settings for its local test phase; its optional SQL checks use the dedicated scratch runner.

No repository CI workflow currently runs this gate. Integrating it into the actual build/merge process and covering all affected transactions remain release-readiness work; a local pass alone does not enforce a merge or release block.
