# ProductionV2 — Stock Item Lifecycle Implementation Report

Plan: `productionv2_STOCK_ITEM_LIFECYCLE_ACTIVE_DISCONTINUED_INACTIVE_FINAL_STRICT_AI_AGENT_PLAN_v1.md`
Date: 2026-10-28
Status: **IMPLEMENTED — all gates evidenced; release recommendation subject to the residual items in §8.**

## 1. Delivered behaviour

| State | `Active` | `IsDiscontinued` | Meaning |
|---|---|---|---|
| ACTIVE | 1 | 0 | Accepts new demand, fulfils existing obligations. |
| DISCONTINUED | 1 | 1 | Rejects **new/increased** demand in sales, purchasing and production; existing commitments continue and can be completed, received, issued, posted and reversed. |
| INACTIVE | 0 | 0 | Retired; only reachable when every retirement blocker is clear company-wide. |

`Active = 0, IsDiscontinued = 1` is rejected by:

* SQL CHECK constraint `CK_IvStockMaster_LifecycleState`;
* `IvStockItemLifecycle` / `ItemLifecyclePolicy.ValidateTransition` in the service layer (returns `Validation`, changes no rows).

Every accepted transition writes exactly one append-only row to `IvStockMasterLifecycleEvent`
(company, item, from/to status, reason, source action, user, UTC timestamp, row version).

## 2. Files

### Schema / model

* `ErpWeb.Model/Entities/Inventory/IvStockMaster.cs` — `IsDiscontinued`, `DiscontinuedAtUtc`.
* `ErpWeb.Model/Entities/Inventory/IvStockMasterLifecycleEvent.cs` — audit entity.
* `ErpWeb.Model/Configurations/Inventory/IvStockMasterConfiguration.cs` — column mappings, CHECK constraint, `IX_IvStockMaster_Company_Discontinued`.
* `ErpWeb.Model/Configurations/Inventory/IvStockMasterLifecycleEventConfiguration.cs`.
* `ErpWeb.Model/Data/AppDbContext.cs` — `IvStockMasterLifecycleEvents`.
* `scripts/alter-ivstockmaster-lifecycle.sql` — deployable, idempotent, additive migration.

### Authoritative policy / retirement

* `ErpWeb.Core/Inventory/Lifecycle/ItemLifecyclePolicy.cs` — transitions, demand authorization, intent classification, `UPDLOCK, HOLDLOCK` item and demand locking, audit append, status application.
* `ErpWeb.Core/Inventory/Lifecycle/ItemRetirementEvaluator.cs` — company-wide blocker evaluation (sales orders/DO/invoices/returns, purchase orders/requests/receipts, production/WIP and component demand, inventory balances, open documents) with structured blocker rows and bounded samples.
* `ErpWeb.Core/Inventory/Lifecycle/ItemLifecycleContracts.cs`, `IvStockItemLifecycle.cs` — statuses, codes, result/contract types.

### Services

* `ErpWeb.Core/Inventory/IvStockMasterService.cs` (and `IIvStockMasterService.cs`, `IvMasterResults.cs`) —
  `PreviewLifecycleAsync`, `ChangeLifecycleAsync`, `GetLifecycleEventsAsync`; strict compatibility shim
  `SetActiveAsync` → `ChangeLifecycleAsync`.
  `ChangeLifecycleAsync` is two-phase: (1) lock every requested item in deterministic code order and verify
  every `RowVersion`; (2) evaluate transitions/blockers and apply. A stale token in a bulk request changes
  **no** rows.
* `ErpWeb.Core/Sales/SaSoService.cs`, `SaQtService.cs`, `SaDoService.cs`, `SaInvoiceService.cs` — new/increased
  demand blocked; persisted-line fulfilment and quantity ≤ existing commitment allowed.
* `ErpWeb.Core/Purchase/PoOrderService.cs`, `PoPrService.cs` — new/increased stock-item demand blocked; stock
  masters never silently fall back to indirect purchasing items.
* `ErpWeb.Core/Production/ProductionWorkOrderService.cs`, `ProductionWorkOrderService.DraftCommands.cs` —
  finished product, route outputs, newly introduced/increased components and material substitution are gated;
  refreshed/released snapshots keep existing fulfilment rights.
* `ErpWeb.Model/Repositories/Inventory/IvStockMasterRepository.cs`, `StockMasterSearchArgs.cs` — lifecycle filter
  (`ACTIVE`, `DISCONTINUED`, `INACTIVE`, `ACTIVE_DISCONTINUED`, `ALL`) and `IsDiscontinued` sorting.

### UI / export

* `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor(.cs)` — status display, lifecycle commands with reason,
  retirement preview/blocker explanation, invalid-state prevention.
* `ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor(.cs)` — `DISCONTINUE`, `SET INACTIVE`, `REACTIVATE`
  bulk actions, lifecycle filter (default `Active + Discontinued`), audited bulk reason, pre-flight retirement
  check, status chip/column and mobile card rendering.
* `ErpWeb/Inventory/IvStockMasterExportEndpoints.cs` — `lifecycle` query parameter; `Status` column replaces the
  raw active boolean.

### Tests

* `ErpWeb.Tests/Inventory/Master/IvStockMasterServiceTests.cs` — state matrix, shim compatibility, bulk stale
  token, transitions.
* `ErpWeb.Tests/Inventory/Master/IvStockLifecycleSqlServerConcurrencyTests.cs` — SQL Server evidence:
  competing commands produce one winner/one audit row; lifecycle command blocks behind a demand
  `UPDLOCK, HOLDLOCK` on the same item. Each host runs on a uniquely derived sibling database and drops it on
  dispose, so it cannot delete a database shared with parallel SQL suites.
* `ErpWeb.Tests/Inventory/Transaction/IvStockCountSqlServerConcurrencyTests.cs` — seeded pile now carries
  `TransDate`; without it the as-of candidate query returned zero lines (the suite had only ever passed by
  silently skipping when SQL Server was not configured).
* Extended sales/procurement/inventory suites for the new demand gates.

## 3. Gate evidence

| Gate | Evidence |
|---|---|
| G1 schema | `alter-ivstockmaster-lifecycle.sql` applied to local `SQLEXPRESS` scratch DB; re-ran successfully (idempotent); columns, CHECK constraint, index and audit table verified. Existing `Active` values untouched. Additive change is readable by the previous binary. |
| G2 retirement | `ItemRetirementEvaluator` covers sales, purchasing, production/WIP, inventory and open-document blockers; terminal/closed documents are excluded; unit tests cover historical-only retirement and zero vs negative balance. |
| G3 commands | Permission + reason + audit enforced in service; two-phase concurrency; SQL Server race test `Competing_lifecycle_commands_change_exactly_one_state_and_audit_once` passed. |
| G4 demand vs fulfilment | `ClassifyLineIntent` + `AuthorizeDemand` + deterministic `LockDemandMastersAsync` in SO/QT/DO/Invoice/PO/PR; existing linked fulfilment remains permitted. |
| G5 production | Snapshot demand validation for FG, route outputs, new/increased components and substitution; existing released commitments unaffected. |
| G6 UI | Entry/list/bulk/export smoke paths; lifecycle filter default keeps discontinued items visible for reconciliation; service still enforces EDIT permission independently of the UI. |
| G7 SQL integration | `IvStockLifecycleSqlServerConcurrencyTests` (2/2) and `IvStockCountSqlServerConcurrencyTests` (5/5) executed against `SQLEXPRESS` with `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`. |
| G8 review | `dotnet build ErpWeb.slnx` → 0 errors (315 pre-existing warnings). Focused lifecycle + SQL tests → 40/40. Full inventory suite → 1164/1164. |

## 4. Commands used

```powershell
# scratch SQL Server (database name must contain "test")
$env:ConnectionStrings__SqlServerTestConnection = "Server=.\SQLEXPRESS;Database=ERPWeb_LifecycleTest;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
$env:ERPWEB_REQUIRE_SQLSERVER_TESTS = "1"

dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~Inventory"
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --filter "FullyQualifiedName~IvStockLifecycleSqlServerConcurrencyTests"
```

## 5. Deployment order

1. Back up `IvStockMaster` and record `COUNT(*)` / `SUM(CAST(Active AS int))`.
2. Run `scripts/alter-ivstockmaster-lifecycle.sql` through the normal SQL deploy process (never at app startup).
3. Re-run it once to prove idempotence; confirm the counts are unchanged and `IsDiscontinued = 0` for all rows.
4. Deploy the application. Old binaries ignore the new column/table, so the schema may stay in place on rollback.
5. Smoke: open a discontinued item in Stock Master, confirm new SO/PO/WO demand is blocked while an existing
   obligation can still be completed; attempt `SET INACTIVE` with an open obligation and confirm the blocker list.

## 6. Costing / quantity impact

No lifecycle code writes quantities, WAC/FIFO/standard cost, valuation replay state or historical audit rows.
Retirement checks and demand gates are read-only; posting/rollback paths are unchanged. Posting an **existing**
commitment for a discontinued item produces the same quantities and cost as an active item.

## 7. Residual risks / notes

* The CHECK constraint is delivered by the SQL script for existing databases; databases created with
  `EnsureCreated` get it from the EF model. Deploy the script first.
* SQL Server concurrency tests are opt-in: without `ConnectionStrings:SqlServerTestConnection` they return
  silently as passed. Always run with `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` for release evidence.
* `IvStockLifecycleSqlServerConcurrencyTests` creates a temporary sibling database per host; ensure the SQL login
  has `CREATE DATABASE`/`DROP DATABASE` rights on the scratch server only.

## 8. Known pre-existing failure (not introduced by this change)

`ErpWeb.Tests.Planning.Transaction.ProductionWorkOrderServiceTests.Nested_phantom_material_keeps_its_own_bom_revision_and_line_snapshots`
fails with `Assert.Single(...)` finding no `RM002` line. Verified by reverting the Production lifecycle diff and
re-running the single test: it still fails on baseline HEAD. It is outside the lifecycle scope and remains open.

Release recommendation: **GO for the lifecycle scope**, with the pre-existing planning test failure tracked
separately and the deployment order in §5 followed.
