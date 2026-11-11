# Phase 5 — SQL Server / UAT / record-only release evidence

**Evidence date:** 2026-11-11  
**Plan:** `plans/productionv2_QA_SOLID_COMPLETION_AI_AGENT_PLAN_v4_NO_COSTING.md`  
**Work authorized:** Phase 5 verification. No costing, CostLayer, stock/WIP writers, POST-ROLLBACK, replay, authorizer edits, origin pull, or Sales mix.  
**Git operations:** none (`pull` / `checkout` / `reset` / `stash` / `merge` / commit not used).

---

## Phase 5 status

**`PARTIAL`** — SQL Server two-session proof on scratch catalog. Logged-in DEMO browser UAT same-user path **PASS** (see `plans/qa_phase5_browser_uat_20261111.md`). Owner signoff has not been given.

Not `QA_INVENTORY_POSTING_COMPLETE`.  
Not a 10/10.  
Do **not** treat this as fully stock-integrated QA.

Release label `QA_RECORD_AND_FOLLOW_UP_COMPLETE` is **not** applied: it still needs owner signoff. Browser UAT on incoming pre-GRN (same user, SoD Off) is done.

---

## 1. Repo identity

| Fact | Value |
|---|---|
| Worktree | `C:/wincom/productionv2` |
| Branch | `productionv2` (not switched) |
| Local HEAD | `e24f54069316a346171fed7f14017c09d593cb95` |
| HEAD subject | Merge pull request #5 from mokth/fix/dataprotection-key-ring |
| Tracking | behind `origin/productionv2` **14**, ahead 0 |
| Origin 14 commits | inventory costing / WAC replay — **not pulled**, **not tested**, **not fixed** |
| Sales pricing dirty files | still present; **not mixed** into QA |
| SDK / solution | `net10.0` / `ErpWeb.slnx` |

---

## 2. Commands and results

### 2.1 Solution build

```
dotnet build .\ErpWeb.slnx
```

After one test compile fix (`using ErpWeb.Model.Entities.Quality` in `QaPhase5SafetyTests.cs`):

**0 errors.** Warnings are pre-existing and were not used as a QA pass.

### 2.2 Quality filter with SQL Server scratch (executed)

Environment set in the same PowerShell session as **separate statements** (not concatenated onto `dotnet test`):

```
$env:ConnectionStrings__SqlServerTestConnection = 'Server=.\SQLEXPRESS;Database=ERPWeb_QaPhase5_test;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true'
$env:ERPWEB_REQUIRE_SQLSERVER_TESTS = '1'
dotnet test .\ErpWeb.Tests\ErpWeb.Tests.csproj --filter "FullyQualifiedName~Quality" --no-build
```

**Failed: 0, Passed: 133, Skipped: 0, Total: 133, Duration: 45 s**

`ERPWEB_REQUIRE_SQLSERVER_TESTS=1` plus `Skipped: 0` is the proof the SQL class ran. A silent early-return would have failed the require-flag.

Scratch catalog: **`ERPWeb_QaPhase5_test`** on `.\SQLEXPRESS`. Not `DefaultConnection` / live `ERPWeb`.

### 2.3 Inventory / Production POST-ROLLBACK baseline (SQLite, recorded not fixed)

SQL env **cleared** before this run so inventory SQL suites could not write to the QA scratch catalog or the live tenant.

```
dotnet test .\ErpWeb.Tests\ErpWeb.Tests.csproj --no-build --filter "(Category=InventoryPosting&Category!=SqlServer)|(FullyQualifiedName~FinishedGoodReceiptSqliteReplayBoundaryTests)|(FullyQualifiedName~ProductionOutputEntryServiceTests.Post)"
```

**Failed: 0, Passed: 139, Skipped: 0, Total: 139, Duration: 22 s**

This is the **local HEAD** SQLite posting baseline. It is **not**:

- SQL Server lock/session proof for GRN/MR/MI/TR/ADJ/SC/VR/count/DP/FG
- proof of the 14 unpulled origin costing commits

Those costing SQL Server regressions remain **NOT EXECUTED** and **NOT FIXED**, as required.

---

## 3. SQL Server two-session and schema

Class: `ErpWeb.Tests/Quality/QaSqlServerConcurrencyTests.cs`  
DBA script: `scripts/create-qa-followup.sql` (copied to test output)

| Check | Result |
|---|---|
| Two connections allocate distinct case numbers | executed, passed |
| Two connections cannot both approve the same rowversion | executed, passed (loser `ChangedReload`) |
| Unique `UX_QaCase_Company_Branch_No` | executed, passed |
| Unique `UX_QaCorrectiveAction_Case_No` | executed, passed |
| Follow-up script idempotent; rebuilds dropped tables | executed, passed |
| Schema probe enlists in the current transaction | executed, passed |
| Record-only workflow leaves `IvBalLoc` / `IvTrxHistory` / `IvTrxBatch` / `StockCostState` / `ProductionBalLot` / `ProductionBalLotMovement` counts unchanged | executed, passed |

Post-run catalog probe (`ERPWeb_QaPhase5_test`):

- QA tables present: `QaPolicy`, `QaCase`, `QaCaseLine`, `QaInspection`, `QaDispositionLine`, `QaAuditEvent`, grant/hold/allocation tables, `QaCorrectiveAction`, `QaInspectionAttempt`
- Indexes present: `UX_QaCase_Company_Branch_No`, `UX_QaCorrectiveAction_Case_No`, `UX_QaCorrectiveAction_RequestId`, `UX_QaInspectionAttempt_Line_No`
- `IvBalLoc` = 0, `IvTrxHistory` = 0
- `QaCase` = 6 leftover record-only rows from the concurrency suite
- No automatic tenant migration. Script is additive DBA SQL only.

**CostLayer:** this local HEAD has no `CostLayer` entity/DbSet (that work lives in the unpulled origin costing commits). Negative-delta used `StockCostState` plus physical/WIP tables instead.

SQLite companion: `QaPhase5SafetyTests` — seeded pile qty 100 unchanged through approve / follow-up / close; unique case-no index rejects a duplicate row.

---

## 4. Seven source families

Code coverage exists. Browser UAT ran for **incoming PO lookup** on DEMO as Demo Admin. Other families remain uncreated in this session.

| Source type | Code tests | Browser UAT |
|---|---|---|
| `INCOMING_PRE_GRN` (PO lookup) | Phase 1, Follow-up, Foundation, Phase 5 | **PASS** (QC00000003 Closed, Posted 0) |
| `INCOMING_PRE_GRN` (unlinked typed line) | Phase 1 | **UI gap** — Lookup only |
| `POSTED_GRN` (Id vs BatchNo) | Phase 1, Follow-up | listed only |
| `PRODUCTION_OUTPUT_GOOD` | Phase 1 | historical QC00000001 Draft only |
| `PRODUCTION_OUTPUT_NON_GOOD` | Destination, Non-good grant | listed only |
| `PRODUCTION_WIP_LOT` | Logical hold, source generation | listed only |
| `FG_STAGING_LOT` | Foundation, FG grant | historical QC00000002 Pending review only |
| `INVENTORY_STOCK` | Physical hold | listed only |

---

## 5. Browser end-user UAT

**PASS** for same-user incoming record-only on DEMO. Detail: `plans/qa_phase5_browser_uat_20261111.md`.

- App: `http://localhost:5000`, user Demo Admin `ADM01`.
- SoD turned Off in Settings so the inspector could Approve. Posting gates stayed Off.
- Path: New incoming (PO lookup) → Save QC00000003 → Inspect Pass → Submit review → Approve → Follow-up Done → Reinspect same source (reason required) → Pass → Approve → Close.
- Inquiry: Closed · Accept 6 · Reject 0 · Posted 0. Destination suggested only.
- SQL: grants/holds 0; `IvTrxHistory` / `IvTrxBatch` 0 in the UAT window.

Remaining UI gaps: unlinked typed pre-GRN line, keyboard-first, 1920 vs 1366, export CSV body, other source-family live creates, reinspect-with-reject mix. DEMO `InspectorApproverSeparation` is now **Off** until an owner restores it.

---

## 6. Forbidden-file audit

**Authorizers (unchanged):**

- `QaReceiptAuthorizationService.cs`
- `QaFgAuthorizationService.cs`
- `QaLogicalHoldAuthorizationService.cs`
- `QaPhysicalHoldAuthorizationService.cs`
- `QaNonGoodAuthorizationService.cs`

**Costing / posting cores:** no diff under `ErpWeb.Core/StockLedger`, `ErpWeb.Core/Inventory`, or `ErpWeb.Model/Entities/Costing`.

**`AppDbContext`:** only added `QaCorrectiveActions` and `QaInspectionAttempts`.

**`Program.cs`:** only `MapQaInquiryExportEndpoints()`.

**QA-adjacent inventory UI (source identity hrefs, not posting):**

- `IvGoodsReceipt.razor.cs` — `sourceHeaderId` uses batch **Id**, not `BatchNo`
- `IvMiscReceipt.razor.cs` — same

**Sales pricing files remain dirty and were not edited in Phase 5.**

Quality Core files contain no `CostLayer` / `IvInventoryPosting` call sites.

---

## 7. Record-only release notes (opt-in)

Support text is on Quality Settings:

- Enable menus `QA_WORKBENCH`, `QA_INQUIRY`, `QA_SETTINGS` and matching rights (`ACCESS`, `ADD`, `EDIT`, `SUBMIT`, `APPROVE`, `REJECT`, `DELETE`, `EXPORT`).
- Do **not** turn posting gates on. Frozen historical ON stays ON; Settings cannot newly enable them.
- Follow-up tables: DBA runs `scripts/create-qa-followup.sql` on tenants that need corrective-action tracking. Inspection still saves if those tables are absent.
- Rollback: remove Quality menus from roles. Do **not** drop `QaCase`, follow-up, or audit tables.
- Attachments remain deferred.

Release only for companies with known **record-only** policy and reviewed schema. This is not inventory-posting QA.

---

## 8. Explicit blockers

1. Owner signoff for `QA_RECORD_AND_FOLLOW_UP_COMPLETE`.
2. Decide whether DEMO SoD (`InspectorApproverSeparation`) should be restored to On.
3. Unpulled origin costing (14 commits) SQL Server POST/ROLLBACK — recorded, not fixed, not pulled.
4. SQL Server inventory/production concurrency suites — not run in this QA task (would not prove origin costing; Category=SqlServer excluded).
5. Other source-family live creates, unlinked typed line, keyboard/viewport matrix, export CSV body.

---

## 9. Recommendation

Code + isolated SQL Server two-session proof is enough to **stop at the Phase 5 gate** for record-only QA engineering work.

Do **not** enable Quality menus on a production company until owner signoff. DEMO SoD is currently Off.

Do **not** label inventory-posting QA complete.
