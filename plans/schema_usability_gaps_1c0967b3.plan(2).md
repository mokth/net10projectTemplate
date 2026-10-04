---
name: Schema usability gaps
overview: "Finish remediation item 1 safely: disable EF SQL OUTPUT on the two live balance tables protected by write-guard triggers, make the production movement schema alter genuinely additive, and prove SQL Server seal/write-guard behavior including the real EF SaveChanges path."
todos:
  - id: disable-output-balances
    content: Disable EF SQL OUTPUT on IvBalLoc and PrProductionBalLot
    status: pending
  - id: additive-movement-alter
    content: Make alter-production-stock-ledger.sql preserve an already-correct movement constraint and never shrink a wider MovementType column
    status: pending
  - id: sqlserver-script-idempotency
    content: Execute the ledger schema/write-guard scripts twice and prove the correct movement constraint and column are untouched on rerun
    status: pending
  - id: sqlserver-seal-tests
    content: Prove StockPosting and ledger fact seal triggers with an isolated non-ACTIVE test branch
    status: pending
  - id: sqlserver-write-guard-tests
    content: Prove 51011 denial and valid-context EF SaveChanges success for IvBalLoc and PrProductionBalLot
    status: pending
  - id: output-model-regression
    content: Add SQL Server model assertions that OUTPUT is disabled for all seven guarded/sealed EF tables
    status: pending
isProject: false
---

# Finish deployed-schema usability — approved implementation plan

**Approval status:** **10/10 — approved for implementation**  
**Repository:** `mokth/net10projectTemplate`  
**Branch verified:** `production`  
**Verification baseline:** commit `c9c8027f88557852212fb7826a7adc8ba8298081`

## 1. Scope and verified repository state

This plan finishes the remaining deployed-schema usability gap without changing stock-ledger activation, posting semantics, cutover behavior, inquiry behavior, costing behavior, or production transaction rules.

The current `production` branch was verified before producing this plan:

- `ErpWeb.Model/Configurations/Inventory/IvBalLocConfiguration.cs`
  currently uses `builder.ToTable("IvBalLoc")` and does **not** disable SQL Server `OUTPUT`.
- `ErpWeb.Model/Configurations/Production/ProductionBalLotConfiguration.cs`
  already has a `ToTable("PrProductionBalLot", table => ...)` callback but does **not** call `UseSqlOutputClause(false)`.
- The five existing ledger/seal mappings already disable `OUTPUT` and must remain unchanged:
  - `StockPosting`
  - `IvTrxHistory`
  - `PrProductionBalLotMovement`
  - `PrMaterialMovement`
  - `PrProductionMovementAllocation`
- `scripts/create-stock-ledger-write-guard.sql` installs write-guard triggers on:
  - `IvBalLoc`
  - `IvTrxHistory`
  - `PrProductionBalLot`
  - `PrProductionBalLotMovement`
  - `PrMaterialMovement`
  - `PrProductionMovementAllocation`
- The guard requires `SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID')` to identify an unsealed posting belonging to the same company/branch and ACTIVE epoch; rejection is error `51011`.
- `StockPostingCoordinator` already sets and clears that same session-context key.
- `scripts/alter-production-stock-ledger.sql` currently drops `CK_PrProductionBalLotMovement_Type` on every run and always executes `ALTER COLUMN MovementType nvarchar(32) NOT NULL`.
- The EF model already defines `MovementType` as `nvarchar(32)` and the current intended movement list includes `FG_RECEIPT_OUT`.
- `ProductionStockLedgerSqlServerTests` already has the required scratch-database safety gate:
  - connection string `SqlServerTestConnection`;
  - catalog name must contain `test`;
  - absence is tolerated unless `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`.
- `StockLedgerInfrastructureTests` already builds a model-only SQL Server `AppDbContext`, so it is the correct location for provider-metadata regression assertions.
- The repo uses EF Core SQL Server `10.0.0`; its public metadata API `IsSqlOutputClauseUsed()` can be used directly in the model test.

Do not broaden this work into a stock-ledger redesign.

---

## 2. Disable SQL Server OUTPUT on the two live balance tables

### 2.1 `IvBalLoc`

Update:

`ErpWeb.Model/Configurations/Inventory/IvBalLocConfiguration.cs`

Replace:

```csharp
builder.ToTable("IvBalLoc");
```

with a SQL Server table-builder callback:

```csharp
builder.ToTable("IvBalLoc", table =>
{
    table.UseSqlOutputClause(false);
});
```

Do not change keys, indexes, rowversion, relationships, stock-slice uniqueness, or field mappings.

### 2.2 `PrProductionBalLot`

Update:

`ErpWeb.Model/Configurations/Production/ProductionBalLotConfiguration.cs`

Inside the existing:

```csharp
builder.ToTable("PrProductionBalLot", table =>
{
    ...
});
```

add:

```csharp
table.UseSqlOutputClause(false);
```

Keep all four existing check constraints unchanged.

### 2.3 Do not modify the five mappings that are already correct

Leave the existing `UseSqlOutputClause(false)` calls unchanged in:

- `StockPostingConfiguration.cs`
- `IvTrxHistoryConfiguration.cs`
- `ProductionBalLotMovementConfiguration.cs`
- `ProductionMaterialMovementConfiguration.cs`
- `ProductionMovementAllocationConfiguration.cs`

The intended invariant after this change is that all seven stock-ledger tables whose normal EF writes can encounter seal/write-guard triggers explicitly opt out of SQL Server `OUTPUT`.

---

## 3. Make `alter-production-stock-ledger.sql` genuinely additive

Update only the `MovementType` widening/check-constraint block in:

`scripts/alter-production-stock-ledger.sql`

Do not rewrite the other additive ledger DDL.

### 3.1 Required behavior

The new block must support all of these deployed states:

| Existing state | Required action |
|---|---|
| `nvarchar(32)` or wider + correct constraint | **No DDL change** |
| `nvarchar(max)` + correct constraint | **No DDL change; never shrink** |
| bounded `nvarchar` narrower than 32 + correct constraint | widen to 32; recreate the constraint only if SQL Server requires dropping it for the column change |
| width already sufficient + stale constraint | replace only the stale constraint |
| width too small + stale/missing constraint | widen and create the intended constraint |
| non-`nvarchar` unexpected type | fail loudly; do not silently perform a destructive conversion |

A second execution against the corrected schema must leave the column and check constraint untouched.

### 3.2 Scope constraint lookup to the actual table

Do not search only by constraint name.

Resolve the constraint from:

```sql
sys.check_constraints
```

with both:

```sql
parent_object_id = OBJECT_ID(N'dbo.PrProductionBalLotMovement')
AND name = N'CK_PrProductionBalLotMovement_Type'
```

This avoids accidentally treating an identically named constraint on another object as the target.

### 3.3 Determine whether the constraint is already current

Read the existing `definition` and consider the constraint current only when it contains the complete intended movement set:

- `OPENING_IN`
- `ISSUE`
- `ISSUE_REVERSAL`
- `PRODUCE`
- `PRODUCE_REVERSAL`
- `CONSUME`
- `CONSUME_REVERSAL`
- `RETURN`
- `RETURN_REVERSAL`
- `TRANSFER_OUT`
- `TRANSFER_IN`
- `STATUS_OUT`
- `STATUS_IN`
- `ADJUST_IN`
- `ADJUST_OUT`
- `SCRAP_OUT`
- `FG_RECEIPT_OUT`

Do not rely on existence of the constraint alone.

`FG_RECEIPT_OUT` can still be treated as the important expansion marker, but the implementation should validate the full expected set so a partially damaged constraint is not mistakenly accepted.

### 3.4 Make the width check byte-safe

SQL Server reports `sys.columns.max_length` in **bytes**:

- `nvarchar(20)` → `40`
- `nvarchar(32)` → `64`
- `nvarchar(max)` → `-1`

Therefore **do not** implement only:

```sql
max_length < 64
```

because `-1 < 64` would incorrectly match `nvarchar(max)` and shrink it.

The widening condition must be equivalent to:

```sql
system type is nvarchar
AND max_length > 0
AND max_length < 64
```

If `max_length = -1` or `max_length >= 64`, do not shrink the deployed column.

### 3.5 Safe DDL ordering

Calculate two flags before modifying anything:

- `@NeedsWiden`
- `@NeedsConstraintRefresh`

Then follow this behavior:

1. if neither flag is true, do nothing;
2. if the constraint must be refreshed, drop only that target constraint;
3. if the column must be widened and SQL Server reports the existing constraint as a dependency, drop the target constraint before the `ALTER COLUMN`;
4. execute `ALTER COLUMN MovementType nvarchar(32) NOT NULL` only when `@NeedsWiden = 1`;
5. recreate `CK_PrProductionBalLotMovement_Type` only when it is missing because it was stale, absent, or had to be removed for the required widening;
6. never replace a correct constraint merely because the deployment script was rerun.

The normal current-schema path (`nvarchar(32)` + correct constraint) must perform zero DDL for this block.

### 3.6 Unexpected schema shape

If `MovementType` exists but its underlying SQL type is not `nvarchar`, stop this script with a clear deployment error stating that manual remediation is required.

Do not silently convert an unknown production schema.

---

## 4. SQL Server script runner and idempotency proof

Extend:

`ErpWeb.Tests/ProductionStockLedgerSqlServerTests.cs`

Keep the existing `TryResolveScratch()` gate exactly in force.

### 4.1 Add a reliable script executor

Add private test helpers rather than duplicating script-loading logic.

Recommended responsibilities:

```text
ResolveRepositoryRoot()
ReadScript(relativePath)
SplitSqlBatchesOnGo(script)
ExecuteScriptAsync(connection, relativePath)
ExecuteLedgerSchemaSequenceAsync(connection)
```

`SplitSqlBatchesOnGo` must split only a standalone `GO` batch separator, case-insensitively, with optional surrounding whitespace.

Do not use a naive substring split that could break SQL text containing the letters `GO`.

Execute batches sequentially on the same open SQL Server connection and stop immediately on the first SQL exception.

### 4.2 Execute the schema sequence in this exact order

```text
scripts/create-stock-posting-ledger.sql
scripts/alter-inventory-history-ledger.sql
scripts/alter-production-stock-ledger.sql
scripts/create-stock-ledger-write-guard.sql
```

Call `EnsureCreatedAsync()` first so the normal EF schema exists.

### 4.3 Prove rerunnability, not merely “no exception”

Add a test such as:

```text
SqlServer_ledger_scripts_are_rerunnable_without_replacing_current_movement_constraint
```

Test sequence:

1. resolve the gated scratch connection;
2. `EnsureCreatedAsync()`;
3. execute the four-script sequence once to normalize any previously used scratch database;
4. read and save the following metadata for `CK_PrProductionBalLotMovement_Type`:
   - `object_id`
   - `definition`
   - `is_disabled`
   - `is_not_trusted`
5. read and save `MovementType` metadata:
   - SQL type name
   - `max_length`
   - nullability
6. assert after the first run:
   - constraint exists;
   - all required movement literals are present;
   - constraint is enabled;
   - column type is `nvarchar`;
   - `max_length = -1` or `max_length >= 64`;
   - column remains NOT NULL;
7. execute the same four-script sequence a second time;
8. reread the metadata;
9. assert:
   - the constraint `object_id` is identical to the value captured after run 1;
   - the definition is unchanged;
   - `is_disabled` / `is_not_trusted` are unchanged;
   - column type, width, and nullability are unchanged.

Why this assertion matters: the current broken script already runs twice successfully, so “second execution did not throw” is **not** a valid idempotency test. Preserving the same constraint identity after the schema is already correct directly proves the defect is fixed.

---

## 5. Seal-trigger SQL Server tests

Seal behavior and ACTIVE-epoch write-guard behavior must be tested on **different tenant fixtures**.

This avoids nondeterministic competition between two AFTER triggers that can both reject the same DML.

### 5.1 Isolated seal fixture

Create a unique company code per test run within the existing 5-character limit, for example:

```csharp
var company = $"S{Guid.NewGuid():N}"[..5].ToUpperInvariant();
const string branch = "HQ";
```

Do not reuse `DEMO/HQ`.

Create a `StockLedgerEpoch` for this company/branch with:

```text
Status = PREPARED
Version = 2
```

**Do not create an ACTIVE epoch for the seal fixture.**

The write guard therefore remains dormant while the seal triggers can be tested deterministically.

### 5.2 Seed a valid unsealed posting

Create an unsealed `StockPosting` referencing the PREPARED epoch.

Use unique request/document values so a persistent scratch database cannot collide with previous executions.

### 5.3 Build valid dependent fact rows before sealing

Use real model-valid support rows; do not disable SQL Server foreign keys.

For production-side rows, add a small private SQL Server fixture builder in this test file that creates only what the FK graph requires:

- minimal `PrBomHdr`;
- `PrWorkOrder` referencing that BOM header;
- one route step;
- one operation;
- one work-order material where needed;
- one `PrProductionPostingLink`;
- one `PrProductionBalLot`;
- the fact rows needed for:
  - `PrProductionBalLotMovement`
  - `PrMaterialMovement`
  - `PrProductionMovementAllocation`

The repo already contains equivalent lightweight fixture shapes in production tests; copy the minimum model-valid pattern, but unlike SQLite-focused tests do **not** turn foreign keys off.

For inventory history, seed the minimum valid inventory master/warehouse/balance data required by the FK relationships.

Insert all target ledger facts while the posting is still unsealed.

### 5.4 Seal and assert exact SQL error numbers

Seal the posting, then prove:

| Object | Expected SQL error |
|---|---:|
| sealed `StockPosting` mutation | `51000` |
| `PrProductionBalLotMovement` referencing sealed posting | `51001` |
| `PrMaterialMovement` referencing sealed posting | `51002` |
| `PrProductionMovementAllocation` referencing sealed posting | `51003` |
| `IvTrxHistory` referencing sealed posting | `51004` |

Prefer a small helper that unwraps `SqlException` from either direct SQL commands or `DbUpdateException`.

Assert `SqlException.Number`; message text may be an additional assertion but must not be the primary contract.

For each fact trigger, cover both trigger images:

- at least one mutation of an already-existing row (`UPDATE` or `DELETE`) to exercise `deleted`;
- an attempted new row referencing the sealed posting to exercise `inserted`.

Because the fixture has no ACTIVE epoch, these assertions cannot be masked by write-guard error `51011`.

---

## 6. ACTIVE write-guard and real EF regression tests

Use a second independent company code, for example:

```csharp
var company = $"G{Guid.NewGuid():N}"[..5].ToUpperInvariant();
const string branch = "HQ";
```

This fixture is exclusively for write-guard behavior.

### 6.1 Seed balances before activating the test epoch

Before creating the ACTIVE epoch, seed:

#### Inventory

A minimal valid set:

- `IvStockMaster`
- `IvWarehouse`
- `IvBalLoc`

Use unique item/warehouse/lot values for the fixture.

#### Production

Seed a model-valid minimal:

- `PrBomHdr`
- `PrWorkOrder`
- `PrProductionBalLot`

The Work Order must reference the real seeded BOM header so SQL Server foreign keys stay enabled.

There is no need to create a full production execution hierarchy merely to update the live production balance unless required by the chosen balance fixture fields.

### 6.2 Create the test-only ACTIVE epoch directly

Insert a `StockLedgerEpoch` row for this fixture with:

```text
Status = ACTIVE
Version = 2
```

This is **test setup only**.

Do **not** execute or modify:

`scripts/activate-stock-ledger-epoch.sql`

The purpose of this test is to exercise the already-installed write guard, not to test cutover.

### 6.3 Create an unsealed posting for the same epoch

Create an unsealed `StockPosting` with:

- matching company;
- matching branch;
- `LedgerEpochId` equal to the ACTIVE epoch id;
- valid positive posting sequence;
- unique request/source identity.

This is the posting id that will be placed in `SESSION_CONTEXT`.

### 6.4 Guarantee one physical SQL Server session

`SESSION_CONTEXT` is connection/session scoped.

For the allow-path test, do not rely on EF opening and closing arbitrary pooled connections between commands.

Use an explicitly opened `SqlConnection` and configure `AppDbContext` with that connection, so these operations occur on the **same physical session**:

1. set `STOCK_LEDGER_V2_POSTING_ID`;
2. perform EF `SaveChangesAsync`;
3. clear `STOCK_LEDGER_V2_POSTING_ID`.

Always clear the context in `finally`, including when the assertion fails.

### 6.5 Deny path — no session context

For **both** live balance tables:

- `IvBalLoc`
- `PrProductionBalLot`

perform a real EF-tracked update while the branch has an ACTIVE epoch and the connection has no posting session context.

Recommended representative mutations:

- `IvBalLoc`: change `StdQty` while keeping it non-negative;
- `PrProductionBalLot`: change `Qty` and `BaseQty` consistently while keeping all check constraints valid.

Assert the EF save fails with a `DbUpdateException` whose inner SQL exception number is:

```text
51011
```

This assertion is important.

If `UseSqlOutputClause(false)` is missing, SQL Server can fail before the trigger with the SQL Server OUTPUT/trigger incompatibility instead of reaching `51011`. Exact `51011` therefore proves the EF command can execute far enough for the intended guard to own the rejection.

After each failed save, dispose/reset that DbContext before the next attempt so failed tracking state does not contaminate the allow-path assertion.

### 6.6 Allow path — valid unsealed posting context

On the same explicitly opened connection:

```sql
EXEC sys.sp_set_session_context
    @key=N'STOCK_LEDGER_V2_POSTING_ID',
    @value=@PostingId;
```

Then perform real EF `SaveChangesAsync` updates to:

- `IvBalLoc`
- `PrProductionBalLot`

Both must succeed.

Finally:

```sql
EXEC sys.sp_set_session_context
    @key=N'STOCK_LEDGER_V2_POSTING_ID',
    @value=NULL;
```

in `finally`.

This is the end-to-end regression proof for the original deployed-schema usability problem:

```text
EF tracked update
+ installed AFTER write-guard trigger
+ valid V2 posting session
= successful SaveChangesAsync
```

---

## 7. SQL Server-free OUTPUT metadata regression

Update:

`ErpWeb.Tests/StockLedgerInfrastructureTests.cs`

Extend the existing model-only SQL Server test or add a focused test.

Build the existing SQL Server model:

```csharp
var sqlOptions = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlServer("Server=(local);Database=StockLedgerModelOnly;Trusted_Connection=True;TrustServerCertificate=True")
    .Options;
```

No SQL Server connection needs to be opened.

For these seven entity types, retrieve the mapped entity type and assert:

```csharp
Assert.False(entityType.IsSqlOutputClauseUsed());
```

Target entities:

- `StockPosting`
- `IvTrxHistory`
- `ProductionBalLotMovement`
- `ProductionMaterialMovement`
- `ProductionMovementAllocation`
- `IvBalLoc`
- `ProductionBalLot`

This is supported by the EF Core SQL Server 10.0.0 package already referenced by `ErpWeb.Model`.

The purpose is to provide a fast provider-metadata regression even when optional SQL Server integration tests are not configured.

---

## 8. Scratch-database safety and isolation rules

Keep all existing safeguards in `ProductionStockLedgerSqlServerTests`:

- use `SqlServerTestConnection`;
- require database/catalog name containing `test`;
- silently skip when no scratch connection exists unless `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`;
- fail when required tests were requested but no safe SQL Server test database is configured.

Additional rules for the new tests:

- never use production/customer connection strings;
- never execute `EnsureDeletedAsync()` against the shared scratch DB;
- never assume the scratch DB is empty;
- never rely on fixed `DEMO/HQ` records;
- generate short unique company/source/document identities within the model's max lengths;
- run schema scripts additively so a previously used scratch DB remains valid;
- clear `SESSION_CONTEXT` in `finally`.

---

## 9. Files to change

### Required

1. `ErpWeb.Model/Configurations/Inventory/IvBalLocConfiguration.cs`
2. `ErpWeb.Model/Configurations/Production/ProductionBalLotConfiguration.cs`
3. `scripts/alter-production-stock-ledger.sql`
4. `ErpWeb.Tests/ProductionStockLedgerSqlServerTests.cs`
5. `ErpWeb.Tests/StockLedgerInfrastructureTests.cs`

### Explicitly out of scope

Do not change unless implementation discovers a compile-only namespace/import requirement:

- `StockPostingCoordinator`
- `create-stock-ledger-write-guard.sql`
- `create-stock-posting-ledger.sql`
- `alter-inventory-history-ledger.sql`
- `activate-stock-ledger-epoch.sql`
- stock posting services
- costing
- inventory transaction behavior
- production posting behavior
- inquiry/reporting behavior

No new EF migration is required for this remediation; the deployed-schema correction remains in the existing additive SQL script.

---

## 10. Implementation order

1. Add `UseSqlOutputClause(false)` to `IvBalLoc`.
2. Add `UseSqlOutputClause(false)` to the existing `PrProductionBalLot` table lambda.
3. Rewrite only the `MovementType` constraint/width block in `alter-production-stock-ledger.sql` using the additive rules above.
4. Add the seven-entity model metadata assertion.
5. Add SQL script resolver / standalone-`GO` batch execution helpers.
6. Add the two-run schema idempotency/constraint-identity test.
7. Add the isolated PREPARED-epoch seal fixture and exact `51000`–`51004` assertions.
8. Add the isolated ACTIVE-epoch write-guard fixture.
9. Add the no-context EF tests for both balance tables and assert exactly `51011`.
10. Add the same-session valid-context EF `SaveChangesAsync` success tests for both balance tables.
11. Run the normal test suite.
12. When `SqlServerTestConnection` is configured, run the SQL Server category with `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` so missing/unsafe integration configuration cannot silently skip the release-gate tests.

---

## 11. Acceptance criteria

The implementation is approved only when all of the following are true.

### EF mapping

- `IvBalLoc` reports SQL Server OUTPUT disabled.
- `PrProductionBalLot` reports SQL Server OUTPUT disabled.
- The existing five ledger/seal mappings continue to report OUTPUT disabled.
- No unrelated entity mapping is changed.

### Additive movement DDL

- `MovementType` is `nvarchar` and is never narrower than 32 characters after the script.
- `nvarchar(max)` or another wider `nvarchar` is never shrunk.
- `CK_PrProductionBalLotMovement_Type` contains the complete intended movement list including `FG_RECEIPT_OUT`.
- After the schema is correct, rerunning the script preserves the same check-constraint `object_id`.
- The second run leaves constraint definition and column metadata unchanged.
- An unexpected non-`nvarchar` deployed column fails explicitly instead of being silently converted.

### Seal behavior

On a fixture branch with no ACTIVE epoch:

- sealed `StockPosting` mutation returns `51000`;
- sealed production movement mutation returns `51001`;
- sealed material movement mutation returns `51002`;
- sealed production allocation mutation returns `51003`;
- sealed inventory history mutation returns `51004`;
- inserted/deleted trigger images are both covered.

### Write guard

On an isolated ACTIVE fixture branch:

- `IvBalLoc` EF `SaveChangesAsync` with no context returns `51011`;
- `PrProductionBalLot` EF `SaveChangesAsync` with no context returns `51011`;
- with a matching unsealed posting id in `STOCK_LEDGER_V2_POSTING_ID`, EF updates to both tables succeed on the same SQL connection;
- session context is always cleared afterward.

### Scope protection

- `activate-stock-ledger-epoch.sql` is not executed by these tests and is not modified.
- Production cutover gates are unchanged.
- `StockPostingCoordinator` posting/session semantics are unchanged.
- No inventory, costing, production, or inquiry business rule is changed by this remediation.

---

## 12. Final implementation verdict

This plan is intentionally narrow but release-critical.

It addresses the actual remaining deployed-schema failure path in the verified `production` branch and includes regression tests that prove behavior rather than only inspecting source text:

```text
correct EF SQL generation
+ additive deployed schema
+ immutable sealed ledger facts
+ ACTIVE-epoch write guard
+ real same-session EF SaveChanges
```

**Plan status: 10/10 — APPROVED FOR IMPLEMENTATION.**
