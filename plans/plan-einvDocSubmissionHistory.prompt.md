# Plan: Populate `dbo.EInvDocSubmission` from the ErpWeb e-Invoice lifecycle

Source doc reviewed: `docs/einvoice-history.md` (MPosERP `EInvDocSubmission` create/update logic).

Revised 2026-09-21 — review feedback folded in; see §Review history.
Pass 1 was then re-submitted verbatim and re-audited item-by-item — see §Review coverage map.
No new gaps were found in that second pass.

**Status: IMPLEMENTED** (2026-09-21) — see §Implementation record below. The five review blockers are
closed in §Phase 1 and recorded in §Decision register (D-1…D-17).

> **Phase 0 changed three assumptions in this plan.** They are recorded in §Implementation record and
> the plan body below has been left as the pre-implementation record, so the corrections stay visible.

**Previous review:** 8.7/10 — *Conditional Approve*. Architecture 9.5 · DB design 8.5 · EF Core 9 ·
Lifecycle 9 · Error handling 7.5 · Concurrency 8 · Multi-tenancy 7.5 · Migration 8.5 · Testing 9 · Docs 9.
The blocking risk was never coding complexity; it was leaving the **business semantics of the history
row** and the **transaction/failure behaviour** unstated.

**TL;DR:** Map the **existing** physical table into the app's own model (`ErpWeb.Model` +
`AppDbContext`), add **one** writer, and call it from the four lifecycle points in `SaEInvoiceService`.
No database trigger, no new MyInvois HTTP call, no DI change, no UI change. **One row per submitted
document** (a batch creates one row per document, so a single-document submission is exactly one row).
The writer must never fail the business action — which is why it runs on its **own** `AppDbContext`,
**after** the caller has committed (D-8).

## Implementation record — 2026-09-21

All phases are done, including the SQL Server duplicate-key race (Test C). See the verification evidence
below for what actually ran, and for the three harness defects that had to be fixed before the SQL Server
suite could run at all.

**Artefacts**

| Artefact | State |
|---|---|
| `scripts/discover-einvdocsubmission.sql` | new — read-only Phase 0 probe, re-runnable |
| `docs/einvoice-history-phase0-findings.txt` | new — the captured preflight report / schema snapshot (V2/V4) |
| `ErpWeb.Model/Entities/Sales/EInvDocSubmission.cs` | new — 42 live columns + 5 additive, mapped 1:1 |
| `ErpWeb.Model/Configurations/Sales/EInvDocSubmissionConfiguration.cs` | new — key + unique index + read index, **no** `HasTrigger` |
| `ErpWeb.Model/Data/AppDbContext.cs` | +`DbSet<EInvDocSubmission>` |
| `ErpWeb.Core/EInvoice/EInvoiceSubmissionWriter.cs` | new — the single writer, own context, never throws |
| `ErpWeb.Core/EInvoice/SaEInvoiceService.cs` | 4 hooks + classifier now carries `InternalId`/`DocumentCount`/`OverallStatus` |
| `scripts/alter-einvdocsubmission-einvoice.sql` | new — **applied to dev `ERPWeb`**, 3 runs, last 2 clean no-ops |
| `ErpWeb.EInvoiceLib` | submission writer removed (`DbSet` + `HasTrigger` + 4 methods); token path untouched |
| `ErpWeb.Tests/EInvoiceSubmissionHistoryTests.cs` | new — 20 tests |
| `ErpWeb.Tests/EInvoiceTestHost.cs` | `SeedCompanyAsync` now seeds `EInvOnBehalfTin` (see the blocker below) |

**Phase 0 findings that CORRECTED this plan**

| Plan said | Live reality | Consequence |
|---|---|---|
| The legacy `HasTrigger` declaration is "evidence, not proof" a trigger exists | **No trigger** on the table | Do **not** declare `HasTrigger` (declaring it would corrupt EF's generated SQL for nothing). The legacy declaration was stale |
| The table has a PK to inventory | It was a **HEAP** — no PK, no index, no constraint at all | Added `PK_EInvDocSubmission` on the identity column (D-18). EF's `HasKey(Id)` otherwise had no database backing |
| 25 legacy columns, mapped 1:1 | **42 columns** | All 42 are mapped (D-19). The 17 unlisted payload-capture columns are populated from the submission response, which is exactly where they come from |
| Dates are `datetime2`; `document` is a string | Dates are **`datetime`**; `document` is **`text`**; `documentType` is **`nvarchar(3)`** | Existing columns keep their live store types; only the 5 new columns are `datetime2` |
| Legacy NULL/duplicate rows need a D-12 decision | **Row count = 0**, no NULL `companyID` | D-12 needs no action: plain (unfiltered) unique index, no backfill |
| `status` was assumed writable-as-needed | `status` is **NOT NULL with no default** | The writer always supplies it, including a floor value when a row is first created by Recover |

**Deliberate deviations (D-18 … D-23)**

| # | Deviation | Why |
|---|---|---|
| **D-18** | Added `PK_EInvDocSubmission` (not in the plan) | Phase 0 found a HEAP while `Id` is `IDENTITY`. Safe on an empty table, and an EF `HasKey` with no database backing is a latent defect |
| **D-19** | Mapped all 42 live columns, and populate the 17 extras from the submission response | The plan listed only the 25 the legacy entity mapped. Mapping reality is the point of a mirror; leaving them unmapped would silently keep them NULL forever |
| **D-20** | A successful cancel sets `OverallStatus` only when nothing was known (`??=`), not unconditionally | `OverallStatus` is the **submission-level** API vocabulary (Recover stores `"VALID"`). Cancelling ONE document does not rewrite the submission's status; a later Recover/Refresh replaces it with MyInvois' own value. The §Status mapping table's "CANCELLED" entry is therefore narrower in the code than in this document |
| **D-21** | The writer takes `IDbContextFactory` **and an `ILogger`** | The factory is what gives real failure isolation (D-8); the logger is where the warning must be emitted, since the writer is the only place that sees the exception and the key |
| **D-22** | `EInvoiceTestHost.SeedCompanyAsync` seeds `EInvOnBehalfTin` | Pre-existing WIP (`Company.EInvOnBehalfTin` + `scripts/alter-einvoice-outcome-company-profile.sql`) made THAT column the supplier TIN the service validates, but the fixture was never updated — so **every** e-Invoice submit failed validation before reaching MyInvois. Added as an optional parameter defaulting to `"C1234567890"` (the value the WIP's own `CompanyServiceBootstrapTests` uses); pass `null` to exercise the refusal |
| **D-23** | `ApplyStatusAsync` keyed on the **ERP document's** `IRBMSubmitID`, passed in by the caller | As the plan's §3.5 required. The writer never locates a row from a value inside the API payload |

**Verification evidence**

- Migration: applied to dev `ERPWeb` — run 1 created the 5 columns + PK + UX + IX, runs 2 and 3 were clean
  no-ops. Postflight confirms `BranchCode` 10 bytes (nvarchar 5), `OverallStatus` 40 (nvarchar 20),
  `DocumentCount` 4 (int), `CreatedOn`/`LastSyncedOn` 8 (datetime2), plus all three indexes.
- Build: `ErpWeb.Core` + `ErpWeb.Model` compile with **0 errors and no new warnings** (the pre-existing
  project warnings are unrelated). A full-solution build currently fails only because the running
  `ErpWeb` app holds the host's DLLs (MSB3027/MSB3021) — V1 must be re-run with the app stopped.
- Tests (SQLite only): **1846 total — 1833 passed, 13 failed, 0 skipped.** All 20 new history tests pass.
  The 13 failures are pre-existing WIP in `SaCustServiceTests` (9) and `PoSupplierServiceTests` (4) — the
  GL-code/phone validation work visible in `git status`; none touch e-Invoice.
- **Test C — the SQL Server duplicate-key race: DELIVERED and PASSING.** With
  `ConnectionStrings__SqlServerTestConnection` → scratch `ERPWeb_EInvHistoryTest` (built by
  `EnsureCreatedAsync` from the current model, so the EF model and the SQL migration were cross-checked)
  and `ERPWEB_REQUIRE_SQLSERVER_TESTS=1`: full suite **1847 total — 1832 passed, 15 failed, 0 skipped**;
  `SaEInvoiceSqlServerConcurrencyTests` is **2 passed / 1 failed**, and the 8-way
  `Concurrent_history_writes_for_the_same_key_produce_exactly_one_row` is one of the passes — exactly one
  row survives, no racer reports `Failed`. The remaining failure in that class is unrelated (see below).

**Three harness defects had to be fixed first — this suite could never actually have run**

It reported PASSED purely by self-skipping, so all three defects were invisible:

1. `EInvoiceTestHost.DisposeAsync` disposed a **null** `_connection` for factory-built hosts (the whole
   SQL Server path), throwing an NRE in teardown *after* the test body had done its work.
2. `SaEInvoiceSqlServerConcurrencyTests` used company code **`"EINV01"` — six characters** — against a
   `nvarchar(5)` column and `TenantScopeContext.MaxCompanyLength = 5`, so seeding threw
   "String or binary data would be truncated". Changed to `"EINV1"`.
3. `EInvoiceTestHost.BumpInvoiceRowVersionAsync` ran the **SQLite-only** `randomblob(8)`; on SQL Server
   that threw "not a recognized built-in function name", which surfaced as an `MyInvois` transport
   failure instead of a concurrency conflict. Now provider-aware (SQL Server owns `rowversion`, so a
   no-op update is used).

**Deliberately NOT fixed — `A_row_touched_during_the_submission_loses_the_rowversion_check_and_recovers`**

That pre-existing test bumps `RowVersion` from `Helper.OnSubmitCalled`, i.e. during the MyInvois call
(phase 2). Phase 3 then opens a **fresh `DbContext` and reloads the row**, so it reads the already-bumped
value and the write-back cannot lose the check — the test's premise no longer matches the service. It is
unsatisfiable as written regardless of the bump mechanism, and fixing it means redesigning another
feature's test, so it is reported rather than silently rewritten.

**Failure surfaced by enabling the SQL Server suites (not this feature)**

`AdSmParamSqlServerConcurrencyTests.Two_simultaneous_inserts_of_the_same_key_leave_one_row_and_one_concurrency`
now runs (it used to self-skip) and **fails: both simultaneous inserts succeed** instead of one losing the
race. That is the *same class of race* this plan's D-10 addresses for the registry — the global-settings
feature has no duplicate-key recovery. It is pre-existing and outside this feature; worth a follow-up.

**Blocker found in the workspace (NOT caused by this feature)**

The workspace had substantial uncommitted e-Invoice WIP. One consequence was that **all e-Invoice submit
paths failed** with `Supplier.Tin: Supplier TIN is required` before any MyInvois call, because the
supplier TIN moved to `Company.EInvOnBehalfTin` while `EInvoiceTestHost` still seeded only `TaxNo`. That
was 12 failures in `SaEInvoiceLifecycleTests` alone; seeding the column removed them (D-22).

**Still open**

1. **V1 — full-solution build** must be re-run with the `ErpWeb` app stopped. All projects compile
   (`ErpWeb.Core`, `ErpWeb.Model`, `ErpWeb.UI`, `ErpWeb.Tests` each build 0 errors); only the host's
   output copy is blocked (MSB3027/MSB3021, PID holding the DLLs), with 0 `error CS`.
2. ~~Test C~~ — **done and passing on SQL Server.**
3. **V7/V8 — preprod functional and batch gates** (a real submit and a mixed batch) are operator steps.
4. The 15 failures in the SQL-enabled full run = the 13 pre-existing `SaCust`/`PoSupplier` WIP failures +
   1 stale e-Invoice rowversion test + 1 newly-exposed `AdSmParam` race (all three diagnosed above; none
   is caused by this feature and none touches the registry).
5. The `.Tests/EInvOnBehalfTin` fixture gap (D-22) and the three harness defects above are **workspace WIP
   debt** this feature had to clear to be verifiable; the WIP owner should be told.

## What this revision changed

| # | Review finding | Resolution |
|---|---|---|
| 1 | Row granularity was only a "further consideration" | Locked business rule R1 + D-3 |
| 2 | Tenant identity absent from the proposed unique key | Resolved as *not applicable* — grounded: no `TenantId` exists anywhere in the solution (D-2) |
| 3 | `find-then-insert` race underspecified | Deterministic duplicate-key recovery, final-state assertion (D-10) |
| 4 | "Never throws" contradicts sharing the caller's `DbContext` | Writer owns its context and runs after the business commit (D-8) |
| 5 | Status / state mapping incomplete | Full matrix grounded in the shipped constants (§Status mapping) |
| 6 | Additive members ambiguous (`CompanyId` looked like a new column) | Explicit existing-mapped vs. new-column split (§Phase 2) |
| 7 | Accepted/rejected batch policy implicit (only a test expectation) | Locked to Model A with a legacy-grounded justification (D-4) |
| 8 | Date/time semantics undefined | Declared per column, UTC (§Timestamp semantics) |
| 9 | Failure isolation untested | New tests A–E (§Phase 5) |
| 10 | Migration could silently degrade to a non-unique index | Script THROWs; preflight + postflight phases (§Phase 2) |
| 11 | Phase 0 ignored constraints, indexes and duplicate data | Preflight extended to 0.1–0.5 with a blocking duplicate check |
| 12 | `ErpWeb.EInvoiceLib` removal claimed "zero callers" from a narrow search | Whole-solution grep is now an explicit step (§Phase 4) |
| 13 | Dev/prod migration sequencing | Scratch → snapshot → dev → test → preprod → prod gate order (§Verification) |

## Verified starting facts

Grounded in the repo on 2026-09-21 (not assumed):

- `ITenantScopeContext.CompanyCode` is the **only** tenant identity (`ErpWeb.Core/Services/TenantScopeContext.cs:12`);
  `MaxCompanyLength = 5` and an over-long company claim resolves to *no scope* (fail closed).
- **There is no `TenantId`** — a whole-workspace grep returns zero matches. Tenancy is the 5-char
  company code against one shared database.
- The legacy model already declares `HasTrigger("tr_EInvDocSubmission_ForInsert")`
  (`ErpWeb.EInvoiceLib/BL/DataContext/EInvoiceContext.cs:43`) and `DbSet<EInvDocSubmission> :24`.
- The legacy write path is CREATE-only-when-absent on the pair `(submissionUUID, uuid)`, with
  `documentType` hard-coded to `"POS"` and `status` hard-coded to `"Submitted"`; UPDATE re-reads the
  row by the same pair and copies `documentSummary[0]` field-by-field with no null guard
  (`docs/einvoice-history.md` §4–§5).
- `SaEInvoiceService` already holds `IDbContextFactory<AppDbContext>` (`:44`) and `ITenantScopeContext
  :45`; every hook writes through an `await using var db = await _dbFactory.CreateDbContextAsync(...)`
  scope and commits with `SaveChangesAsync`, catching only `DbUpdateConcurrencyException`.
- `SqlErrorClassifier` (`ErpWeb.Core/Services`, `internal`) already owns “lost a race”:
  `IsUniqueViolation` (`:43`) and `IsSerializationConflict` (`:40`). The writer must reuse it rather
  than re-implement `SqlException.Number is 2601 or 2627`.
- Lifecycle vocabulary is locked in `ErpWeb.Core/EInvoice/EInvoiceStatuses.cs`: statuses
  `NEW/SUBMITTING/SUBMITTED/VALID/INVALID/FAILED/REJECTED/CANCELLED`, document types `INV/CN/DN`, and
  the MyInvois→ERP map `SaEInvoiceStatusMap.FromMyInvoisDocumentStatus`.

## Steps

### Phase 0 — verify the live table (blocks everything)

Run every probe as `sqlcmd -E -d ERPWeb -I`. **Always pass `-d`** — an omitted database silently runs
against the login's default DB, which produces a confident, wrong "object does not exist" answer (this
exact mistake has already cost a false conclusion in this repo). `-I` is required for any query using
XML methods.

**0.1 — column shape.** `sys.columns` JOIN `sys.types` for `OBJECT_ID('dbo.EInvDocSubmission')`:
name, type, `max_length`, precision/scale, `is_nullable`, `is_identity`. Every `[Column]`/
`HasPrecision` in Phase 2 uses the **live** width — this repo has a documented history of EF
`HasMaxLength` being *narrower* than live (e.g. `SaInvoiceConfiguration`, `MsUom` 10 vs 5).

**0.2 — trigger.** `sys.triggers` WHERE `parent_id = OBJECT_ID('dbo.EInvDocSubmission')`. The legacy
model declaring `HasTrigger` is *evidence*, not proof.

- No row → do **not** declare `HasTrigger`.
- Row present → the ErpWeb config **must** declare it, or every INSERT fails with `Msg 334` and EF's
  insert strategy (OUTPUT clause vs. batched) is wrong.

**0.3 — constraint and index inventory.** Existing PK, unique constraints/indexes, non-unique indexes,
foreign keys, default constraints, check constraints, and the current row count. Record all of it
before touching anything.

**0.4 — duplicate candidate keys (BLOCKING).** The unique index cannot be created if legacy rows
already collide:

```sql
SELECT companyID, submissionUUID, documentType, documentNo, COUNT(*) AS Cnt
FROM dbo.EInvDocSubmission
GROUP BY companyID, submissionUUID, documentType, documentNo
HAVING COUNT(*) > 1;
```

Any row returned = **STOP**. Decide explicitly: merge, quarantine, or keep the index non-unique *by
decision*. The migration script must **THROW**, never silently fall back (D-11).

**0.5 — legacy `companyID` NULLs.** Count and sample them. `NULL` counts as **equal** in a SQL Server
unique index, so two legacy rows with `NULL` companyID and the same other three parts would collide.
Pick Option A/B/C in D-12 from the **actual count**, not from a guess.

**Exit criteria (Phase 0 FAIL = no Phase 2):** a printed preflight report — column list with live
widths, trigger presence, constraint inventory, duplicate-candidate count, NULL-companyID count — is
captured into a findings file (or appended to `docs/einvoice-history.md`) and kept as the **schema
snapshot** referenced by §Verification gates.

### Phase 1 — lock the business rules (no code)

No design decision below may be left to the implementation. Each is a signed rule; D-numbers cross-
reference §Decision register.

| Rule | Statement |
|---|---|
| **R1 — granularity** | One `EInvDocSubmission` row = **one submitted e-Invoice document within one submission**. A batch of N accepted documents creates N rows sharing one `submissionUUID`. Everything in this plan is written against that definition. |
| **R2 — tenant identity** | The tenant key is `companyID` (≤5 chars). No `TenantId` exists in this solution and none is added. The four-part unique key therefore **already contains** the tenant (D-2). |
| **R3 — accepted vs rejected** | **Model A: only accepted documents are persisted.** A submission in which every document was rejected writes **no** row (D-4). |
| **R4 — unique key** | `(companyID, submissionUUID, documentType, documentNo)` — the four-part key is the authority; the database enforces it, the writer never assumes it (D-9). |
| **R5 — re-submission** | A retry after `REJECTED`/`CANCELLED` obtains a **new** `submissionUUID`, so it creates an **additional** row for the same document number. This matches the legacy behaviour (legacy keyed on `submissionUUID` + `uuid`) and preserves attempt history. The *current* row for a document is the highest `ID` for `(companyID, documentType, documentNo)` — which is what `IX_EInvDocSubmission_Document` serves (D-5). |
| **R6 — status vocabulary** | New rows use the `EInvoiceStatuses` constants (UPPERCASE). Legacy rows keep their Title-case literals; reads normalise through `EInvoiceStatuses.Normalize`, which is case-insensitive. The writer never invents a value outside `EInvoiceStatuses.All` (D-6). |
| **R7 — timestamps** | All persisted timestamps are **UTC** (`DateTime.UtcNow`), matching the existing service. Display-time conversion stays with `CurrentDateService` (D-7). |
| **R8 — writer failure** | A history-write failure must be **invisible** to the business operation. Achieved structurally, not by a bare `catch` (D-8). |
| **R9 — concurrency** | The unique index is authoritative. A duplicate-key race is **recovered deterministically** into an update; it is never surfaced to the operator (D-10). |

**Why R3 is Model A (rejected documents are not persisted)** — grounded, not arbitrary:

1. The legacy table was only ever written from `acceptedDocuments[0]`; a submission with no accepted
documents wrote nothing. Model A preserves that contract, so existing consumers see no change.
2. A rejected document has no `uuid`, no `internalId` and no `longId` — the fields that give the row
its identity and that a history/audit consumer joins on. Persisting it would create a degenerate row.
3. The rejection is **already** recorded twice elsewhere: `SaEInvoiceLog` (action + `errorCode` +
`errorMessage`) and the ERP document's own `IRBMStatus`/`IRBMOutcome`. The history table is a
*submission registry*, not the audit trail.

The same reasoning makes a batch of *mixed* outcomes self-consistent: accepted documents get rows,
rejected ones are represented by the submission's `OverallStatus` on the accepted rows plus the log.

### Phase 2 — model + migration

**2.1 — clarify existing vs. new (closes review item 6).** `CompanyId` is **not** a new database
column; it is the CLR name for the existing `companyID`. Only **five** columns are additive:

| | Column | CLR | Storage | Population |
|---|---|---|---|---|
| Existing | `companyID` | `CompanyId` | live width | already populated (nullable — see D-12) |
| **New** | `BranchCode` | `BranchCode` | ≤5 chars | tenant scope `BranchCode`; NULL allowed |
| **New** | `OverallStatus` | `OverallStatus` | ≤20 chars | MyInvois **submission-level** `overallStatus`, normalised UPPERCASE |
| **New** | `DocumentCount` | `DocumentCount` | `int?` | documents in the submission (submit: ERP count; recover: API `documentCount`) |
| **New** | `CreatedOn` | `CreatedOn` | `datetime2` | first ErpWeb history write, UTC |
| **New** | `LastSyncedOn` | `LastSyncedOn` | `datetime2` | last successful MyInvois sync, UTC |

Implementation mistake this prevents: a *second* `CompanyId` column that duplicates `companyID`.

**2.2 — status vocabulary split.** `status` = **document-level** status (`SUBMITTED/VALID/INVALID/
CANCELLED`); `OverallStatus` = **submission-level** status from the API (`submitted/inprogress/valid/
invalid/cancelled`). They are different concepts, which is why the legacy table needed a second
column once a batch could mix outcomes.

**2.3 — entity.** New `ErpWeb.Model/Entities/Sales/EInvDocSubmission.cs`: `[Table("EInvDocSubmission",
Schema = "dbo")]`, PascalCase properties with `[Column]` preserving the legacy names **verbatim**
(no rename). Existing columns mapped 1:1 (`ID`, `uuid`, `submissionUUID`, `longId`, `internalId`,
`typeName`, `typeVersionName`, `issuerTin/Name`, `receiverId/Name`, the three dates,
`totalSales`/`totalDiscount`/`netAmount`/`total`, `status`, `cancelDateTime`, `rejectRequestDateTime`,
`documentStatusReason`, `createdByUserId`, `document`, `companyID`, `documentNo`, `documentID`,
`documentType`) plus the five new members above. `documentID` stays mapped and unused (D-15). No
`RowVersion` — this is a snapshot, not a concurrency participant.

**2.4 — configuration.** New `ErpWeb.Model/Configurations/Sales/EInvDocSubmissionConfiguration.cs`
(template: `SaEInvoiceLogConfiguration.cs`): `HasKey(Id)`, `ValueGeneratedOnAdd`,
`ToTable("EInvDocSubmission", "dbo")`, widths/`HasColumnType("datetime2")` from step 0.1, plus:

- `HasTrigger("tr_EInvDocSubmission_ForInsert")` — **only** if step 0.2 found it.
- `UX_EInvDocSubmission_Submission` UNIQUE on `(companyID, submissionUUID, documentType, documentNo)`,
  **or** on the same four columns `WHERE companyID IS NOT NULL` **if and only if** D-12 chose Option A.
- `IX_EInvDocSubmission_Document` on `(companyID, documentType, documentNo)` — serves R5's "current row
  = highest ID for this document".

**2.5 — DbSet.** `ErpWeb.Model/Data/AppDbContext.cs` ~L70 (beside `SaEInvoiceLogs`).

**2.6 — migration script.** New `scripts/alter-einvdocsubmission-einvoice.sql`, in this order, with
`SET QUOTED_IDENTIFIER ON` + `SET XACT_ABORT ON`:

1. **Preflight** — re-assert the 0.4 duplicate check and `THROW` on any hit.
2. **Preflight** — assert the unique index does not already exist under a different name.
3. **Add** the five columns, each guarded by `IF COL_LENGTH(...) IS NULL`.
4. **Backfill** — only if D-12 chose Option B.
5. **Indexes** — create both, guarded.
6. **Postflight** — report columns and indexes; print a clear `STOP:` line if the unique index is
   absent, and **never** fall back to a non-unique index silently (D-11).

*Trap:* any statement referencing a just-added column in the **same** batch fails — SQL Server compiles
the whole batch before executing it (this cost a run on `alter-company-sales-price-method.sql`).
Route such statements through `EXEC sp_executesql N'…'`. Keep the preflight `THROW`s in their own
`GO`-separated batch too, because under `XACT_ABORT ON` a later failure rolls back earlier statements
in the same batch and the re-run cannot make progress.

### Phase 3 — writer + lifecycle hooks (depends on Phase 2)

**3.1 — contract.** New `ErpWeb.Core/EInvoice/EInvoiceSubmissionWriter.cs`, `internal static class`.
It takes the **factory**, not an open context — `SaEInvoiceService` already holds
`IDbContextFactory<AppDbContext>` (`:44`), so **no DI change is needed**.

```csharp
internal enum EInvoiceHistoryWrite { Skipped, Inserted, Updated, Failed }

internal static class EInvoiceSubmissionWriter
{
    // CREATE — one call per accepted document.
    public static Task<EInvoiceHistoryWrite> RecordSubmitAsync(
        IDbContextFactory<AppDbContext> factory,
        TenantScope scope,
        SaEInvoiceDocumentKey key,
        string submissionId,
        string? uuid,
        string? internalId,
        int? documentCount,
        DateTime submittedOnUtc,
        CancellationToken ct = default);

    // UPDATE — Refresh / Recover / Cancel.
    public static Task<EInvoiceHistoryWrite> ApplyStatusAsync(
        IDbContextFactory<AppDbContext> factory,
        TenantScope scope,
        SaEInvoiceDocumentKey key,
        string submissionId,          // from the ERP document, NOT the API payload
        DocumentSummary? summary,     // Recover
        DocumentValidatation? detail, // Refresh  (note: library's own spelling)
        string? overallStatus,
        int? documentCount,
        DateTime? cancelOnUtc = null,
        CancellationToken ct = default);
}
```

Returning an enum instead of `void` is what makes tests A–E assertable without inspecting the logger.

**3.2 — failure isolation (closes review item 4).** The review's objection was precise: "never
throws" is incompatible with mutating the caller's tracked `DbContext`, because if the caller's shared
`SaveChangesAsync` then fails, tracked state is unpredictable and a swallowed exception can leave the
business action half-persisted.

| Option | Isolation | Cost | Verdict |
|---|---|---|---|
| **A. Writer owns its context; runs *after* the caller commits** | **Real** — separate connection, transaction and `SaveChangesAsync`; cannot poison the business context | Not atomic with the business commit; a crash between the two commits leaves no history row | **CHOSEN — D-8** |
| B. Writer shares the caller's context and rides the same commit | None — violates R8 | One commit, atomic | Rejected |
| C. Writer shares the context but commits separately afterwards | Partial — business write is safe, but the shared context still holds failed tracked entries | Two commits on one context | Rejected |

Why A is safe even though it is not atomic: the history row is **derivable**. Refresh/Recover
reconstruct it from the document's own `IRBMSubmitID`/`IRBMUUID` plus the MyInvois response, so a
missing row is recoverable; a poisoned business commit is not. And because the write happens only
**after** the business commit succeeded, a rolled-back or concurrency-aborted action never leaves a
phantom history row.

**Placement rule this implies:** the hook goes **after** the
`try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { return ConcurrencyFailure(key); }`
block — **not** beside `AppendLogAsync`. On the concurrency path the method returns early, so no
history write occurs, which is correct.

**3.3 — duplicate-key recovery (closes review item 3).** `RecordSubmitAsync` must not assume
find-then-insert is safe — two concurrent requests can both find nothing and both INSERT:

1. Find by the four-part key on a fresh context.
2. Found → apply the same field updates → save → `Updated`.
3. Not found → insert → save → `Inserted`.
4. `DbUpdateException` with `SqlErrorClassifier.IsUniqueViolation(ex)` → **another writer won the
   race**: discard that context, open a **new** one, re-read the row by the four-part key, apply the
   same updates, save → `Updated`.
5. Any other failure, including a second failure inside step 4 → log **Warning** with the full key and
   return `Failed`. Never throw.

The unique index is authoritative; step 4 is the deterministic recovery. Test C asserts the **final
state** (exactly one row), not merely that SQL Server threw.

**3.4 — merge semantics.** `ApplyStatusAsync` copies **non-null incoming values only**. A NULL in one
MyInvois response must never erase a value an earlier response supplied — different responses
legitimately carry different subsets of fields. (Legacy copied blindly, which is the defect being
improved on.) There is no explicit "clear a field" operation in the MyInvois document summary today, so
no exception to this rule is needed.

Field mapping (legacy column ← API field): `totalSales ← totalExcludingTax`,
`netAmount ← totalNetAmount`, `total ← totalPayableAmount`, `totalDiscount ← totalDiscount`, plus
`longId`, `typeName`, `typeVersionName`, `issuerTin/Name`, `receiverId/Name`,
`dateTimeIssued/Received/Validated`, `documentStatusReason`, `cancelDateTime`, `rejectRequestDateTime`,
`uuid`, `internalId`. `LastSyncedOn = UtcNow` on every successful sync; `OverallStatus` and
`DocumentCount` are written only when supplied.

**3.5 — keying rule.** The business key is built from the **ERP document** — `scope.CompanyCode`,
`key.DocumentType`, `key.DocumentNo`, and the document's own `IRBMSubmitID` — **never** from a value
inside the API payload. A mismatched or stale API response therefore cannot write into a different
submission's row. This is deliberately stricter than legacy, which trusted
`submit.documentSummary[0].uuid` to locate the row.

**3.6 — CREATE hook.** `SaEInvoiceService.SubmitCoreAsync` phase-3 loop (comment `:1353`, counters
`:1373`, `AppendLogAsync` `:1387`, commit `:1400`). For each classification where
`Status == EInvoiceStatuses.Submitted && SubmissionId is not null`, call `RecordSubmitAsync` **after the
commit**, with `status = EInvoiceStatuses.Submitted`, `dateTimeIssued = submittedOnUtc` (a provisional
value the API supersedes later — §Timestamp semantics), `CreatedOn = UtcNow`,
`OverallStatus = SUBMITTED`, `DocumentCount` = documents accepted in this submission, and
`createdByUserId = scope.UserId`. Covers `Submit`, `Retry` and `SubmitManyAsync` — the last of which
shares one `submissionUUID` across N rows (R1).

**3.7 — UPDATE hooks.**
- `RefreshAsync` success branch (`:420-440`, commit `:453`): pass `detail.result` as the
  `DocumentValidatation` payload (already fetched: longId/type/issuer/receiver/dates/totals/status/
  cancelDateTime) with `submissionId = loaded.SubmitId`. If `SubmitId` is null there is nothing to
  update → `Skipped`.
- `RecoverAsync` step 1: pass `match` (`DocumentSummary`) plus `submission.result.overallStatus` /
  `documentCount`, with `submissionId` again taken from the ERP document, not the payload.

**3.8 — CANCEL hook.** `CancelAsync` success branch (commit `:611`), after the commit:
`status = EInvoiceStatuses.Cancelled`, `cancelDateTime = detail.cancelDateTime ?? loaded.CancelOn ??
UtcNow`, `OverallStatus = CANCELLED`. A **failed** cancel must not touch the history row — the
financial document is unchanged and the service only records an error.

### Status mapping (closes review item 5)

Anchored on the shipped constants, not invented. `status` is the **document-level** column;
`OverallStatus` is the **submission-level** column.

| Lifecycle event | Source value | `status` written | `OverallStatus` written |
|---|---|---|---|
| `RecordSubmit` (Submit / Retry / SubmitMany) | ERP classification `EInvoiceStatuses.Submitted` | `EInvoiceStatuses.Submitted` → `SUBMITTED` | `SUBMITTED` for a single document; the submission's overall status otherwise |
| `ApplyStatus` from **Refresh** | `SaEInvoiceStatusMap.FromMyInvoisDocumentStatus(detail.result.status)` | `SUBMITTED` / `VALID` / `INVALID` / `CANCELLED` (unknown → `NEW`) | unchanged — Refresh does not carry a submission-level status |
| `ApplyStatus` from **Recover** | `submission.result.overallStatus` (observed: `submitted`, `inprogress`, `valid`, `invalid`, `cancelled`) | mapped through the **same** `SaEInvoiceStatusMap` | `overallStatus`, normalised UPPERCASE, stored verbatim |
| `CancelAsync` success | `EInvoiceStatuses.Cancelled` | `CANCELLED` | `CANCELLED` |
| Submission rejected in full — `EInvoiceStatuses.Rejected` (API refused before any `uuid`) | ERP classification `REJECTED` | **no row** — `Rejected` is deliberately **absent** from this table (R3 / Model A); it lives on the document (`IRBMStatus`) and in `SaEInvoiceLog` | — |
| Transport failure / `FAILED` | — | history row untouched | — |

Casing note (D-6): new rows write the UPPERCASE constants, so a fresh row's `status` differs
cosmetically from the legacy Title-case literals (`"Submitted"`) already in the table. Readers that
go through `EInvoiceStatuses.Normalize` — which is case-insensitive over `EInvoiceStatuses.All` — see
both identically. Nothing may write a value outside `All`; `OverallStatus` is diagnostic and is
intentionally **not** constrained to `All`, because it is the API's own vocabulary.

### Timestamp semantics (closes review item 8)

| Column | Meaning | Source |
|---|---|---|
| `dateTimeIssued` | The e-Invoice business/document issue timestamp | On create: the submission instant (UTC) as a provisional value. On Refresh/Recover: the API value **when present**, which supersedes it |
| `dateTimeReceived` | MyInvois receipt timestamp | API only (Refresh/Recover) |
| `dateTimeValidated` | Validation timestamp | API only; `RefreshAsync` already stores `ValidOn` from it |
| `cancelDateTime` | Cancellation timestamp | API `cancelDateTime`, else the ERP `CancelOn`, else `UtcNow` at a successful cancel |
| `rejectRequestDateTime` | Rejection-request timestamp | API only |
| `CreatedOn` | First ErpWeb history-row creation | ERP, UTC |
| `LastSyncedOn` | Last successful MyInvois sync | ERP, UTC, every successful `ApplyStatusAsync` |

All persisted timestamps are **UTC** (D-7). Legacy wrote local `DateTime.Now`; that is not carried
forward. Display conversion is `CurrentDateService`'s job (`Asia/Kuala_Lumpur` fallback), not the
history table's.

### Phase 4 — enforce one writer (parallel with Phase 5)

17. **Whole-solution verification first.** Grep the **entire** solution — not just `ErpWeb.Tests` — for
    `IEInvoiceRepository`, `EInvoiceRepository`, `EInvDocSubmission` and `EInvoiceContext`, and record
    the hit list **before** deleting anything. The previous review flagged that "zero callers" was
    asserted from too narrow a search.
18. De-duplicate `ErpWeb.EInvoiceLib`: drop `DbSet<EInvDocSubmission>` (`EInvoiceContext.cs:24`) and the
    `HasTrigger` registration (`:41-44`), plus the four submission methods from `EInvoiceRepository.cs`
    (`:181`, `:218`, `:238`, `:265`) and `IEInvoiceRepository.cs` (`:16-19`). Keep the token methods
    and the LHDN API client untouched. *(Alternative: `[Obsolete]`-mark them and drop only the DbSet +
    trigger.)*
19. Disposition of `ErpWeb.EInvoiceLib/BL/Entity/SaDocSubmission.cs`: prefer keeping it **unmapped** with
    a comment pointing at the new entity, because it is the only in-repo record of the legacy column
    names. Delete it only once Phase 6 has recorded the legacy column list in the doc.

### Phase 5 — tests

20. New `ErpWeb.Tests/EInvoiceSubmissionHistoryTests.cs` on the existing SQLite host (no host change —
    single ctor site `EInvoiceTestHost.cs:199`; `EnsureCreated` builds the table from the model):
    - accepted submit → 1 row with all fields (`documentType == "INV"`, `status == "SUBMITTED"`,
      `CreatedOn` set, `document` NULL);
    - 1 accepted + 1 rejected → 1 row (R3);
    - refresh → **update in place**: totals + `dateTimeValidated` filled, `LastSyncedOn` advanced,
      `ID` unchanged;
    - recover after transport failure → update, no duplicate;
    - batch of N accepted documents sharing one `submissionUUID` → **N rows, one per document** (R1);
    - tenant isolation (`DEMO` vs `OTHER`) — the same `documentNo` in two companies ⇒ two rows;
    - company codes ≤ 5 chars; `documentID` never written (D-15).
21. **Test A — duplicate refresh.** Submit → Refresh → Refresh again ⇒ **exactly one row**, and the
    second refresh reports `Updated`.
22. **Test B — history failure is isolated.** Force the history write to fail (drop/rename the table on
    a scratch context, or inject a factory that throws) and assert the **business** operation still
    succeeds and the document's state is correct. This is the test that turns "never throws" from a
    comment into a contract (R8 / D-8).
23. **Test C — duplicate-key race (SQL Server).** Extend
    `ErpWeb.Tests/SaEInvoiceSqlServerConcurrencyTests.cs`: two concurrent `RecordSubmitAsync` calls for
    the same four-part key ⇒ the **final state holds exactly one row** (SQLite cannot prove a
    unique-index race).
24. **Test D — legacy duplicate data.** If 0.4 found duplicates, the migration must **fail safely**
    (THROW; no delete, no merge). Assert the preflight logic against a seeded duplicate, or assert the
    script's failure mode.
25. **Test E — legacy NULL `companyID`.** Verify whichever D-12 strategy was chosen: Option A ⇒ two
    NULL-companyID rows with otherwise identical keys can still coexist under the filtered index;
    Option B ⇒ the backfill filled them; Option C ⇒ the writer's pre-check still prevents app-created
    duplicates.
26. Grep the suite for the removed library API and update (currently no test touches
    `IEInvoiceRepository`/`EInvoiceContext`).

### Phase 6 — docs

27. Append an "Implemented in ErpWeb" section to `docs/einvoice-history.md`: legacy → ErpWeb mapping,
    `INV`/`CN`/`DN` instead of `"POS"`, no trigger, `documentID` unused, per-hook write points,
    batch-key rationale.
28. That section must additionally document the decisions a future developer is most likely to get
    wrong (the original review's §15 requirement): **row granularity** (R1), **accepted/rejected policy** (Model A),
    **tenant/company uniqueness** (D-2), the **status matrix**, **timestamp semantics** (UTC), the
    writer's **failure behaviour** (D-8) and its **concurrency recovery** (D-10). State explicitly that
    `EInvoiceStatuses` is the authority for the status vocabulary and that this doc is secondary.

## Relevant files

- `ErpWeb.Model/Entities/Sales/EInvDocSubmission.cs` — new entity (legacy column names preserved; 5 additive members).
- `ErpWeb.Model/Configurations/Sales/EInvDocSubmissionConfiguration.cs` — new config; shape from `SaEInvoiceLogConfiguration.cs`; `HasTrigger` **only** if Phase 0.2 found one.
- `ErpWeb.Model/Data/AppDbContext.cs` — DbSet at ~L70.
- `ErpWeb.Core/EInvoice/EInvoiceSubmissionWriter.cs` — **new, the single writer**; owns its own context from the factory; returns `EInvoiceHistoryWrite`.
- `ErpWeb.Core/EInvoice/SaEInvoiceService.cs` — hooks placed **after** each commit in `SubmitCoreAsync` (phase 3), `RefreshAsync`, `RecoverAsync`, `CancelAsync`. The ctor already holds `IDbContextFactory<AppDbContext>` (`:44`) and `ITenantScopeContext` (`:45`), so **no DI change**. Reuse `AppendLogAsync` (`:2290`), `NextAttemptAsync`, `Truncate`, `SubmitClassification` (`:2542`), `EInvoiceDocumentState` (`:1686`).
- `ErpWeb.Core/EInvoice/EInvoiceStatuses.cs` — `EInvoiceStatuses`, `EInvoiceDocumentTypes` (`:117`), `SaEInvoiceStatusMap`; the authority behind §Status mapping.
- `ErpWeb.Core/Services/SqlErrorClassifier.cs` — `IsUniqueViolation` (`:43`), `IsSerializationConflict` (`:40`). Reuse; do not re-implement `SqlException.Number is 2601 or 2627`.
- `ErpWeb.Core/Services/TenantScopeContext.cs` — `TenantScope` (`:12`), `MaxCompanyLength = 5`; the only tenant identity (D-2).
- `docs/einvoice-history.md` — legacy create/update logic, and the Phase 6 "Implemented in ErpWeb" section.
- `ErpWeb.EInvoiceLib/BL/DataContext/EInvoiceContext.cs`, `BL/Repository/EInvoiceRepository.cs`, `Interface/IEInvoiceRepository.cs` — Phase 4 de-duplication. `BL/Entity/SaDocSubmission.cs` is the legacy schema reference.
- `scripts/alter-einvdocsubmission-einvoice.sql` — new migration (template `scripts/create-saeinvoicelog.sql`).
- `ErpWeb.Tests/EInvoiceSubmissionHistoryTests.cs` (new), `ErpWeb.Tests/SaEInvoiceSqlServerConcurrencyTests.cs` (extend), `EInvoiceTestHost.cs` (unchanged).

## Verification gates

Run in this order. **Never advance an environment without the previous gate's output captured** — a
scratch success is not evidence about dev, and a green suite is not evidence that the SQL Server tests
ran (most self-skip silently).

| # | Gate | Action | Pass condition |
|---|---|---|---|
| V1 | Build | `dotnet build ErpWeb.slnx --nologo -v:q` | 0 errors |
| V2 | Preflight on **scratch** | Phase 0.1–0.5 | report captured; **no** blocking duplicate |
| V3 | Migration on **scratch**, twice | `sqlcmd -E -d <scratch> -I -i scripts/alter-einvdocsubmission-einvoice.sql` | run 1 adds 5 columns + 2 indexes; run 2 a clean no-op |
| V4 | Schema **snapshot** | `sys.columns` / `sys.indexes` probe, saved beside the preflight report | `UX_…` + `IX_…` present; snapshot archived **before** touching dev |
| V5 | Migration on **dev** `ERPWeb`, twice | same command with `-d ERPWeb` | idempotent; re-probe **with `-d ERPWeb`** (never omit `-d`) |
| V6 | Unit + DB tests | set `ERPWEB_REQUIRE_SQLSERVER_TESTS=1` and `ConnectionStrings__SqlServerTestConnection` in a **separate** terminal command, echo them, then `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` **unpiped** | `Skipped: 0` |
| V7 | Preprod functional | submit a real invoice; confirm exactly one row with `submissionUUID = SaInvoice.IRBMSubmitID`, `uuid = IRBMUUID`, `documentType = 'INV'`; run **Refresh twice** | the same `ID` updates and **no** second row appears |
| V8 | Preprod batch | submit a multi-document batch, incl. one rejected member | N rows sharing one `submissionUUID`; no row for the rejected member |
| V9 | UI smoke | existing e-Invoice panel submit / refresh / cancel | behaviour unchanged |
| V10 | Production | only after V7–V9 evidence exists | — |

Operational notes:

- Setting `ConnectionStrings__SqlServerTestConnection` on the **same** command line as `dotnet test`
silently truncates the value at the first `;` → the SQL tests then fail/skip while the other flag still
reads as set. Set env vars first, echo them, then run the test command alone.
- If `MSB3027`/`MSB3021` "locked by testhost" appears, clear stale hosts:
`Get-Process -Name testhost*,vstest.console* | Stop-Process -Force -PassThru | Wait-Process`.
- `ErpWeb.Tests` does not reference `ErpWeb.UI`, so V1 — not `dotnet test` — is what proves the solution
still compiles.

## Decision register (locked)

| # | Decision | Rationale / grounding |
|---|---|---|
| **D-1** | Option A — app-owned model; `ErpWeb.EInvoiceLib` stops mapping the table (one EF owner) | Prevents two contexts racing on the same rows |
| **D-2** | **Tenant key = `companyID`; no `TenantId` is introduced.** The four-part key therefore already contains the tenant | Verified: zero `TenantId` anywhere in the workspace; `ITenantScopeContext` derives company from claims (`MaxCompanyLength = 5`); one shared database. **Revisit only** if a deployment ever hosts two tenants sharing a 5-char company code in one DB |
| **D-3** | **One row = one submitted document within one submission** (R1) | The only definition under which a batch is well-defined; pins the key and every test |
| **D-4** | **Model A — accepted documents only** (R3) | Matches the legacy contract (`acceptedDocuments[0]`); rejected documents have no `uuid`/`internalId`/`longId`; rejection is already recorded by `SaEInvoiceLog` + `IRBMStatus`/`IRBMOutcome` |
| **D-5** | A re-submission after rejection/cancel creates an **additional** row (R5); the current row = highest `ID` per document | Preserves attempt history; identical to the legacy `(submissionUUID, uuid)` behaviour |
| **D-6** | New rows write **UPPERCASE** `EInvoiceStatuses` constants; legacy Title-case rows stay readable via `Normalize` | One vocabulary, one membership authority (`EInvoiceStatuses.All`) |
| **D-7** | Persist timestamps **UTC**; display conversion stays in `CurrentDateService` | Matches `SaEInvoiceService`'s existing `DateTime.UtcNow`; does not perpetuate legacy local-time storage |
| **D-8** | Writer owns its own `AppDbContext` and runs **after** the caller's commit; failures are logged, never thrown | Real failure isolation (R8). A poisoned business context is unrecoverable; a missing history row is derivable |
| **D-9** | `UX_EInvDocSubmission_Submission` on `(companyID, submissionUUID, documentType, documentNo)` is the authority | Batch-safe: `SubmitManyAsync` shares one `submissionUUID` and can mix CN + DN or accepted + rejected |
| **D-10** | A duplicate-key race recovers deterministically — `SqlErrorClassifier.IsUniqueViolation` → fresh context → re-read → update | The race is expected, not exceptional; the operator must never see it |
| **D-11** | The migration **THROWs** on duplicate legacy data and never silently creates a non-unique index | A silent fallback would falsify this plan's concurrency guarantee |
| **D-12** | Legacy NULL `companyID`: **Option B (backfill)** if such rows exist, else **Option A (filtered unique index)**; Option C only by explicit decision | `NULL` counts as *equal* in a SQL Server unique index; decided from the Phase 0.5 count, not a guess |
| **D-13** | ErpWeb adds **no** trigger | The document write-back is already done by `SaEInvoiceService.ApplyState`; a trigger would be a second writer against the `RowVersion` guard. Declaring a **pre-existing** legacy trigger via `HasTrigger` is mapping, not authoring |
| **D-14** | `document` (raw signed payload) stays NULL | Matches the `SaEInvoiceLog` security contract |
| **D-15** | `documentID` is mapped but **never written** | No identity key exists on `SaInvoice`/`SaCdn`, and legacy's fallback lookup-by-`internalId` is the documented inverted-lookup bug — not reproduced |
| **D-16** | The writer is a separate `internal static` class, not two more private methods on `SaEInvoiceService` | Keeps the ~2 600-line service readable, is directly unit-testable, and its enum return makes success/failure assertable |
| **D-17** | Out of scope: self-billed families (`SBI`/`SBC`/`SBD`), the legacy business-layer variant, and any external portal/report consumer of the table | — |

## Review history

### Pass 1 — 2026-09-21 · **8.7/10 — Conditional Approve**

**Praise recorded:** existing physical table preserved · legacy column names retained · a single
application writer introduced · the legacy `ErpWeb.EInvoiceLib` table writer removed · no new MyInvois
HTTP call · lifecycle hooks at exactly the four lifecycle points · SQL Server unique-index concurrency
considered up front · migration intended idempotent · raw signed `document` payload stays NULL ·
`documentID` deliberately unused · the no-trigger decision rated 10/10.

**Blockers raised (all now closed):**

| Blocker | Closed by |
|---|---|
| 1. Row identity — "one row = one submitted document" must be a rule, not a consideration | R1 + D-3 |
| 2. Tenant isolation — must `TenantId` participate in uniqueness? | **No.** D-2, grounded in a workspace-wide grep |
| 3. Concurrent insert — define duplicate-key recovery | §3.3 + D-10 + Test C |
| 4. Writer failure — isolate a history failure from the main transaction | §3.2 + D-8 + Test B |
| 5. Status semantics — map legacy `status`, `OverallStatus` and the ERP constants | §Status mapping + D-5/D-6 |

**Lower-severity items folded in:** explicit existing-vs-new column split (`CompanyId`) · accepted/
rejected batch policy promoted to a decision · date/time semantics declared · Phase 0 extended with
PK/index/FK/constraint/duplicate/NULL checks · "no trigger" and "one writer" strengthened · merge rule
("incoming NULL never erases a stored value") stated explicitly · tests A–E added · migration script
phased with preflight/postflight and a hard THROW · documentation list extended · environment sequencing
made explicit.

### Review coverage map (Pass 1 re-verification)

Pass 1 was supplied a second time, verbatim, on 2026-09-21. It was re-audited against the revised plan
section by section. **No new gaps were found**; the only change that second audit produced was naming the
`REJECTED` case explicitly in the status matrix (it had been covered by the catch-all "no row" line).

| Review § | Requirement | Where it lives now |
|---|---|---|
| §2 | PK / index / FK / default / check / unique / row-count inventory, plus a **blocking** duplicate-candidate check | Preflight 0.3 and 0.4 — the latter carrying the review's exact `GROUP BY … HAVING COUNT(*) > 1` query — and 0.5 for NULL `companyID` |
| §3 Issue 1 | "Additive members" wording: `CompanyId` is not a new column | 2.1 existing-vs-new table + the "second `CompanyId` column" warning |
| §3 Issue 2 | Establish the actual tenancy model before assuming either case | R2 + D-2 — resolved **not applicable**, grounded in a workspace-wide `TenantId` grep rather than assumed |
| §4 | Granularity promoted from "further considerations" to a formal decision | R1 + D-3 |
| §5 | Accepted-vs-rejected must be an explicit, justified business decision | R3 + D-4, with the three-clause legacy justification |
| §6 | "Never throws" contradicts the shared `DbContext` — define the contract | §3.2 three-option table, D-8, and the explicit *after-the-commit* placement rule; Test B pins it |
| §7 | Deterministic duplicate-key recovery; assert the **final state** | §3.3 recovery algorithm + D-10; Test C asserts exactly one row |
| §8 | A complete lifecycle → status matrix | §Status mapping — both hooks, both no-row cases, `REJECTED` named explicitly |
| §9 | Make "non-null copy" an explicit rule | §3.4 |
| §10 | Timestamp semantics; explicitly avoid local Malaysian time for persistence | §Timestamp semantics + D-7 (UTC) |
| §11 | Keep the no-trigger decision | D-13 |
| §12 | Whole-solution grep before removing the legacy writer | Phase 4 step 17 (the review's exact identifier list) |
| §13 | Tests A–E | Phase 5 steps 21–25 |
| §14 | Migration phases + fail clearly, never a silent non-unique fallback | 2.6 (six ordered steps) + D-11 |
| §15 | Document granularity / policy / uniqueness / status / timestamps / failure / concurrency | Phase 6 step 28 |
| §16 | Environment sequencing with a snapshot before touching dev | §Verification gates V1–V10 (V4 = the schema snapshot) |
| §17 | Add the six decisions that were missing | §Decision register D-1…D-17 |
| §18 | The recommended revised sequence | §Adopted implementation order — the review's ten steps consolidated into Phases 0–6; no step dropped |
| §19 | The five blockers | Blockers table above; status is now APPROVED FOR IMPLEMENTATION |

### Adopted implementation order

```
PHASE 0  Live database discovery  ─ columns · trigger · constraints · duplicates · NULL companyID
PHASE 1  Lock business rules      ─ R1…R9 + D-1…D-17   (this revision)
PHASE 2  EF entity + config + idempotent SQL migration
PHASE 3  Single writer + Submit / Refresh / Recover / Cancel hooks
PHASE 4  Remove the legacy EInvoiceLib writer (after a whole-solution grep)
PHASE 5  Unit + integration + SQL Server concurrency tests (A–E included)
PHASE 6  Documentation (incl. the decisions future developers get wrong)
         └─ Verification gates V1–V10 gate each environment promotion
```

### Open items (accepted, non-blocking)

1. **`OverallStatus` is stored verbatim, not mapped.** The observed token set (`submitted`, `inprogress`,
   `valid`, `invalid`, `cancelled`) comes from the legacy caller's switch, not from a fresh reading of the
   MyInvois docs. It is a diagnostic column, not a state machine, so an unknown token is stored as-is
   rather than rejected — but nothing may act on it.
2. **`documentCount` may disagree between sources.** On Recover the API value wins; on Submit the ERP
   batch size is used. They are not reconciled.
3. **No in-app consumer yet.** No screen or report reads the table, so its usefulness is unproven inside
   ErpWeb; this plan deliberately ships it without a UI.
4. **R5 means a naive "one row per document" query is wrong.** Any future report must take the highest
   `ID` per `(companyID, documentType, documentNo)`.
5. **Legacy NULL-`companyID` counts are unknown until Phase 0.5 runs**, so D-12 is a rule about *how to
   choose*, not a pre-made choice. This is intentional — it is the one item that genuinely cannot be
   decided from the repository alone.
