# QA Safety Fix — F1 schema readiness and frozen gates

**Gate:** F1 only.  
**Status:** IMPLEMENTED — STOP. F2–F5 not authorized.  
**Recorded:** 2026-11-03, local Windows checkout.  
**HEAD:** `dcb614e` (`fix/dataprotection-key-ring`). Merge-base `origin/productionv2`: `371725c0`.  
**Class:** B costing-adjacent. No quantity/cost/FIFO/WAC/ledger algorithm changes. No flag enablement. No live SQL. Dirty `QaSettings.razor(+css)` not overwritten.

This file is F1 evidence. It does **not** authorize F2 incoming source work, F3 hold algebra, F4 UI, F5 UAT, or turning any stock-affecting QA gate on.

## 1. What F1 did

Catalog probe (`QaSchemaReadiness`) runs before every QA EF entry used on POST/ROLLBACK and QA UI writes.

| Catalog | Provenance | POST / Restore | QA UI |
|---------|------------|----------------|-------|
| All 15 allowlisted v2 objects absent | `QA_HISTORY_UNKNOWN` (never `LEGACY_CONFIRMED`) | Skip QA EF (C-01). Legacy POST continues. | Policy Get returns default OFF + info; Save/cases fail `NotInstalled` |
| Any object present, required set incomplete | `PREVIOUSLY_DEPLOYED` + PARTIAL | Fail closed | Fail closed |
| Probe / VIEW DEFINITION not 1 | `QA_HISTORY_UNKNOWN` | Fail closed `PROBE_ERROR` | Fail closed |
| Full set present | `PREVIOUSLY_DEPLOYED` | Query. New claims/grants/holds frozen. Restore and existing-hold asserts still run | Record-only inspect; Save cannot turn gates on |

Probe SQL is enlisted with `command.Transaction = CurrentTransaction?.GetDbTransaction()`. Cache is per `AppDbContext` (`ConditionalWeakTable`), not process-wide. SQLite uses `sqlite_master` table names only (EnsureCreated tests). SQL Server uses `OBJECT_ID` / `COL_LENGTH` / `sys.indexes` after `HAS_PERMS_BY_NAME(dbo, SCHEMA, VIEW DEFINITION)`.

No new schema table. AccERP `Qa*` names are ignored.

## 2. Frozen stock-affecting gates

Save refuses to enable Incoming / Final FG / Logical / Physical / NonGood. Existing ON rows stay ON if already persisted; F1 does not turn them off when posted allocations or active holds exist.

New Claim / Place / grant-on-approve paths do not allocate:

- Incoming GR claim → frozen message when flag ON; no-op when OFF
- FG claim and PADJ staging assert → frozen when flag ON
- Logical/physical Place on approve → frozen when flag ON; record-only when OFF
- NonGood Daily Production claim → frozen when flag ON
- Approve no longer calls `IssueIncomingGrants` / `IssueFgGrants` / `IssueNonGoodGrants` (methods retained, unused)

History-safe:

- Restore* still reverse posted allocations when the feature pair is READY, even if the flag is OFF
- `AssertRemainingCoversHoldsAsync` and physical remaining-on-pile asserts still query existing holds when READY, even if the flag is OFF
- ADJ/TR still have no Restore hook — F3, not invented here

`LoadPublishedAsync` now probes first: all-absent → `null` (flags off); PARTIAL/PROBE_ERROR → throw (fail closed).

PADJ source search skips `QaPolicy` when all objects are absent; still filters `FG_STAGING` only when a published Final FG flag is already ON. No writer/cost change.

## 3. Files

Added: `ErpWeb.Core/Quality/QaSchemaReadiness.cs`, `QaReadinessMessages.cs`, `ErpWeb.Tests/Quality/QaSchemaReadinessTests.cs`, this evidence file.  
Edited: policy, case, receipt/FG/logical/physical/nongood authorizers, PADJ `SearchSourcesAsync`, Quality tests, `Padj_blocks_fg_staging_when_finished_goods_inspection_is_on`.  
Protected / not edited this gate: `ErpWeb.UI/Quality/QaSettings.razor(+css)`, plan file, `create-qa-*.sql`, posting math, CostLayer.

## 4. Tests this session

```
dotnet test ErpWeb.Tests\ErpWeb.Tests.csproj --filter "FullyQualifiedName~ErpWeb.Tests.Quality|FullyQualifiedName~Padj_blocks_fg_staging_when_finished_goods_inspection_is_on"
```

**Passed! Failed 0, Passed 77, Skipped 1, Total 78**, duration 19 s, exit 0. Provider: SQLite in-memory EnsureCreated.

Skipped (intentional): `SqlServer_probe_enlists_in_the_current_transaction` — `ConnectionStrings__SqlServerTestConnection` must name a scratch catalog containing `test`. **NOT EXECUTED.**

Not run: live `ERPWeb` catalog, IIS login VIEW DEFINITION, SQL Server posting races, sealed ledger/FIFO before-after, remote tenants.

## 5. Still blocked (do not camouflage)

1. Zero-object tenants remain `QA_HISTORY_UNKNOWN` / F1_PROVENANCE_BLOCKED. Skip is C-01 compatibility, not a wipe-vs-never-installed proof.
2. Least-privilege SQL metadata visibility untested → permission NULL is PROBE_ERROR.
3. ADJ/TR POST can still claim physical hold with no matching Restore (F3).
4. WI consume still decrements `ProductionBalLot` outside `ProductionStockWriter` (F3).
5. Dirty Settings UI still implies later grant enablement (F4 / R-24).

## 6. GO / NO-GO

| Gate | Result |
|------|--------|
| F1 schema safety + frozen gates | **PASS** as local SQLite evidence |
| SQL Server probe / posting race | **NOT VERIFIED** |
| Flag enablement / live SQL | **NO-GO** |
| F2–F5 | **NO-GO** — not authorized |

**STOP.**
