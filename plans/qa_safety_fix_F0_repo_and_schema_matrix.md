# QA Safety Fix — F0 repo and schema matrix (v3)

**Gate:** F0 audit only.  
**Status:** v3 DOCUMENTED — STOP. F1 not started.  
**Recorded:** 2026-11-03, local Windows checkout.  
**Plan:** QA Safety Fix Gates v3. R-01–R-24 binding.

This file is inspection evidence. It does **not** authorize F1 coding, QA flag enablement, live SQL, F2–F5, or costing edits. No customer master, supplier, or cost amounts are copied here (R-22).

## 0. v3 verdict

**F0 complete for independent review.**  
**F1: NOT AUTHORIZED. F1_PROVENANCE_BLOCKED for any tenant whose productionv2 QA objects are all absent.** Catalog zero-tables cannot prove never-deployed (R-13). No EF migration history. `deploy.ps1` / `DEPLOYMENT_GUIDE.md` do not apply `create-qa-*.sql`. Scripts create no durable install marker. Do not invent a new F1 table.

Local app DB `ERPWeb` is **not** that ambiguous case: it already has the full v2 QA table set (PREVIOUSLY_DEPLOYED / feature tables READY, 0 policy rows, 2 cases, 0 allocations). Other sampled databases with zero `dbo.QaPolicy` are **QA_HISTORY_UNKNOWN**, not `LEGACY_CONFIRMED`.

## 1. Repo identity (re-pinned this session)

| Item | Value |
|------|--------|
| Branch | `fix/dataprotection-key-ring` |
| HEAD | `dcb614e10470a4c8ac47fd5f53655ee5a13dae14` |
| Staged | none |
| Merge-base vs `origin/productionv2` | `371725c0bda74e386a66bfa3c7559c6f227a14dd` |

### Dirty / untracked (PROTECTED)

Modified: `.gitignore`, `ANTIFORGERY_FIX.md`, `DEPLOYMENT_GUIDE.md`, `FINAL_DEPLOYMENT_STEPS.md`, `deploy.ps1`, **`ErpWeb.UI/Quality/QaSettings.razor`**, **`ErpWeb.UI/Quality/QaSettings.razor.css`**.  
Untracked: `.replay-wac-fg-origin-20261030/`, `DATA_PROTECTION_KEY_RING_RUNBOOK.md`, `setup-all-agent-skills.cmd`, this evidence file.

F1 (if later authorized) must be Core-only and must not overwrite dirty Settings files.

## 2. Skills / costing class

Read: `.agents/skills/productionv2-costing/SKILL.md`, `references/stock-balance.md`, `testing.md`; `erp-ui-core/skill.md` present for later F4. Companion UI redesign file still **NOT FOUND**.

F1 class would be **B** costing-adjacent. No quantity/cost algorithm changes in F0 (none made).

## 3. Deployment provenance (R-13, R-17)

**Trusted independent QA-install history: NONE found.**

| Source | What it records | QA install? |
|--------|-----------------|-------------|
| `dbo.__EFMigrationsHistory` | **table absent** on `ERPWeb` | cannot prove |
| `deploy.ps1` | publish zip; optional connection string rewrite | does **not** run `create-qa-*.sql` |
| `DEPLOYMENT_GUIDE.md` | IIS/`ERPWeb` checklist | no QA script step |
| `scripts/create-qa-*.sql` | idempotent DBA DDL | no install-history / schema-version table |
| `scripts/init-qa-menu.sql` | seeds `QUALITY` / `QA_*` menus independently of QA tables | menus ≠ tables |
| `ErpWeb/Menus/menus.xml` | QA menus in source | present even if tenant DDL never applied |
| Phase evidence docs | claimed “scripts not applied live” | contradicted by local `ERPWeb` catalog |
| Machine-schedule `PrMachineScheduleReadinessEvidence` | unrelated planner contract | not a QA marker |

**Implication:** `OBJECT_ID(dbo.QaPolicy) IS NULL` is compatible with (a) never installed, (b) wiped after enablement, (c) menus installed without tables. F1 must not no-op on (b). Mark **F1_PROVENANCE_BLOCKED** until operations attest `LEGACY_CONFIRMED` per tenant or an existing (non-invented) history source is nominated.

Suggested F1 exception (design only, not implemented): QA-dependent routes **fail closed** when provenance is UNKNOWN and objects are absent; no-op only after explicit `LEGACY_CONFIRMED`. Do not add a schema table in F1.

## 4. Tenant matrix (read-only catalog; R-22)

Instance: `.\SQLEXPRESS`. F0 login: Windows `dbo` + **sysadmin** (metadata visibility trusted **for this login only**).  
`HAS_PERMS_BY_NAME(dbo.QaPolicy, SELECT)=1`, schema `VIEW DEFINITION=1` on `ERPWeb`.  
App-pool / least-privilege SQL users: **NOT TESTED** → F1 probe must treat permission-NULL as `PROBE_ERROR`, not ABSENT (R-18).  
`ConnectionStrings__SqlServerTestConnection`: unset. `ERPWEB_REQUIRE_SQLSERVER_TESTS`: unset.

Allowlist (productionv2 only): `QaPolicy`, `QaCase`, `QaCaseLine`, `QaInspection`, `QaDispositionLine`, `QaAuditEvent`, `QaReceiptGrant`, `QaReceiptAllocation`, `QaFgGrant`, `QaFgAllocation`, `QaLogicalHold`, `QaPhysicalHold`, `QaPhysicalAllocation`, `QaNonGoodGrant`, `QaNonGoodAllocation`.  
**Do not** treat AccERP `QaHistory` / `QaOnHold` / `QAItemBal` as this module.

### 4.1 `ERPWeb` (app DefaultConnection)

Label: **PREVIOUSLY_DEPLOYED** + **QA_FOUNDATION READY** + **QA_FEATURE_* READY** (all listed tables and critical unique indexes present).  
Provenance: objects exist (not inferred from missing policy).

Non-sensitive counts: `QaPolicy` rows = 0; `QaCase` = 2 (DEMO/HQ QC00000001 DRAFT PRODUCTION_OUTPUT_GOOD record-only; QC00000002 PENDING_REVIEW FG_STAGING_LOT); all grant/hold/allocation counts = 0.  
QA menus present and active: QUALITY, QA_WORKBENCH, QA_INQUIRY, QA_SETTINGS.

No sealed allocations → current rollbacks on this DB are **unlinked** (R-16). Still, Restore* methods query allocation tables (fail if those tables were dropped).

### 4.2 Other local databases (OBJECT_ID only)

`dbo.QaPolicy` / `dbo.QaCase` / `dbo.QaReceiptGrant` = **NO** on every other online user database scanned (ACCERP*, DemoERP, ERP, ERPBWP*, ERPWeb_* scratch DBs, MPOS*, WincomHRM*, WinWebAccount*).  
`ERP` has AccERP `Qa*` names — **not** v2; label **QA_HISTORY_UNKNOWN** relative to productionv2 (do not classify as PARTIAL v2).

`ERPWeb` is the **only** scanned catalog with v2 QA tables.

Scratch DBs with `dbo.Menu` but **no** `QA_*` menu rows (examples): `ERPWeb_test`, `ERPWeb_StockLedgerTest`, `erpweb_poapr_test_20261102`. Menus without tables is possible; tables without this F0 login seeing menus is also possible. Absence of `QA_WORKBENCH` does **not** prove never-deployed DDL.

`ERPWeb_test`: `IvTrxBatch` present, `ProductionBalLot` absent, v2 QA absent → **QA_HISTORY_UNKNOWN** (not LEGACY_CONFIRMED). Unsuitable as sole WI/FG fixture.

Uninspected remote/production tenants (e.g. werp3): **UNKNOWN**.

## 5. SQL metadata / probe constraints (R-18)

- Current F0 identity is sysadmin; cannot certify IIS SQL login can see `sys.indexes` / `COL_LENGTH`.
- Catalog NULL from missing VIEW DEFINITION must be `PROBE_ERROR`.
- Posting already uses `BeginTransaction` + `BranchStockTransactionLock` then QA claim. F1 probe must enlist `CurrentTransaction` and must not close EF-owned connections.
- No DDL during POST. No process-wide `IsQaInstalled` cache.

## 6. Route and restore matrix (R-15, R-16)

DI: live QA services always registered (`CoreServiceCollectionExtensions.cs` 306–312). `DisabledQa*` are ctor fallbacks. `IProductionStockWriter` → `ProductionStockWriter` gets `IQaLogicalHoldAuthorization` from DI.

Today if v2 QA objects are missing: **SQL exception** on any `LoadPublishedAsync` or Restore query — not a safe no-op.

### 6.1 QA UI / case / policy (no stock movement)

| Call site | Tables | Tx / lock | Absence today | Flag-ON (if later) | Restore |
|-----------|--------|-----------|---------------|--------------------|---------|
| `QaPolicyService.Get/Save` | QaPolicy | no posting tx | throw | Save currently persists all five stock flags | n/a |
| `QaCaseService` Search/Get/Inquiry/Save/Submit/Inspect/Review/Reject/Close/Cancel/Delete | foundation | own tx on writes | throw | record-only OK if F1 freeze holds | Close checks holds regardless of flag |
| `ApproveAsync` ~498–604 | foundation + grants/holds | BeginTransaction; branch lock **after** policy load | throw | **issues Incoming/FG/NonGood grants by source type even if flags OFF**; Place* respects flags | n/a |
| `SearchSourcesAsync` | PO/lot/inventory (not grants) | no | OK if those exist | UX only | n/a |
| Request QA buttons GR/MR/WI/FG | none (nav) | n/a | n/a | copy currently implies later grant (R-24) | n/a |

### 6.2 Inventory — POST / ROLLBACK / replay

Link key for restore: `QaReceiptAllocation.BatchNo` or `QaPhysicalAllocation.BatchNo` + company/branch + Status=POSTED. Zero rows today = unlinked.

| Route | Stock effect | QA tables | Claim | Restore hook | Linked vs unlinked | Tests |
|-------|--------------|-----------|-------|--------------|--------------------|-------|
| GR POST `PostGoodsReceiptAsync` ~1148 + WAC/FIFO/Std replay | +on-hand, +RecvQty | Policy then ReceiptGrant/Alloc | yes | n/a | new link if Incoming ON | Quality SQLite claim tests; SQL race NOT_RUN |
| GR/NG ROLLBACK ~1268 + WAC/FIFO/Std | reverse GR | **Allocations without policy** | n/a | `RestoreGoodsReceiptAsync` — no-op if 0 rows; throw if table missing | linked if POSTED alloc exists | SQLite restore in grant tests |
| NG POST | Claim returns null for NonStock **after** method entry | Policy still loaded for GR/NG shared entry | yes | same restore | | |
| MI/SC/VR POST ~2570 + replay `AssertQaPhysicalFromDetailsAsync` | −on-hand | Policy then PhysicalHold/Alloc; SC/VR may claim | yes | | | SQLite physical hold |
| MI/SC/VR ROLLBACK ~2802 + WAC/StdMi/Fifo MI replay | reverse issue | Allocations, no policy | n/a | `RestoreStockOutAsync` | linked if POSTED | |
| ADJ POST ~1662 | ±on-hand | Physical assert/claim | yes | **no Restore on core rollback ~1799** | if claim created alloc, rollback **does not reverse it** (F3; do not fabricate in F1) | |
| TR POST ~3137 + transfer replay POST | net-zero move | Physical assert/claim | yes | **no Restore on core or transfer replay rollback** | same F3 exposure | |
| MR/CR | receipt/return | no QA | | | unlinked | |
| Stock count | count | **not wired** | | | F3 audit | |
| PO-CN VR | uses VR/MI core | same as VR | | Restore if MI rollback path | | |

PO-CN-owned VR is dispatched as `IvTrxTypes.VendorReturn` through MI core (POST claim + ROLLBACK restore). No separate QA matcher for CN ownership (F3).

### 6.3 Production

| Route | Stock effect | QA | Direct BaseQty writer? | Restore |
|-------|--------------|----|------------------------|---------|
| FG POST/ROLLBACK `ProductionFinishedGoodReceiptService.Posting.cs` ~121–126 | FG in / reverse; uses `IProductionStockWriter` | Claim policy-first; Restore allocations no policy | writer + FG service | `RestoreFinishedGoodAsync` by BatchId |
| PADJ search ~329 | none | `LoadPublishedAsync` | n/a | throw if no QaPolicy |
| PADJ save/post ~397/561 | WIP move via writer | `AssertWipAdjustmentAllowedAsync` (FinalFg) | writer | no FG restore on PADJ |
| WI POST `ProductionOutputService.Posting.cs` ~121 | consume **mutates lot.BaseQty in the output service itself** ~354–365 (bypasses `ProductionStockWriter`) + hold assert ~355; NonGood claim | dual path | `RestoreDailyProductionAsync` by output id |
| WI ROLLBACK | reverse consume ~420 hold assert | NonGood restore ~71 | | |
| WOT / StockReturn | via writer | logical hold on negative legs | writer | no dedicated QA restore |
| Production Material Issue | RM + material-in lots | **not wired** | own pile.BaseQty writes | F3 bypass |
| Sales DO/Invoice | stock out | **not wired** | | F3 audit |

Logical hold has **no Restore*** API — only `AssertRemainingCoversHoldsAsync`. Policy OFF today no-ops the assert even if holds exist (H-06 / F3).

## 7. Test baseline (honest)

Command (this session):

`dotnet test ErpWeb.Tests.csproj --filter FullyQualifiedName~ErpWeb.Tests.Quality --no-build`

Result: **Passed! Failed 0, Passed 70, Skipped 0, Total 70, Duration 13 s**, exit 0. Provider: **SQLite in-memory EnsureCreated**. Classification: **EXECUTED_AND_ASSERTED** (SQLite only). Not SQL Server locking proof.

Fact/Theory attribute grep was 97; xUnit executed 70 methods (theories/discovery). Do not treat 97 as executed.

SQL Server Quality concurrency: **NOT_RUN** (no F1 tests exist; `SqlServerTestConnection` unset).  
Existing `*SqlServerConcurrencyTests` with `if (!IsSqlServerAvailable()) return`: **EARLY_RETURN_NO_SQL** pattern; **not executed in F0** (would hit live `ERPWeb` DEMO if run against DefaultConnection).

Sealed ledger / FIFO layer / RecvQty before-after: **NOT_RUN** (no F1 posting fixtures this session).

## 8. F1 allowlist (if independently authorized later)

Add: `ErpWeb.Core/Quality/QaSchemaReadiness.cs` (foundation + per-feature + provenance; no new DB table).  
Edit: policy/authorization/case services, possibly DTO warning fields, PADJ search policy load, Quality tests.  
Do not edit: dirty Settings razor, posting formulas, CostLayer, `create-qa-*.sql`, PR/PO docs.

Fixtures must be isolated DBs. Do not DROP objects on live `ERPWeb`. QA_WIPED_HISTORY remains **BLOCKED/UNPROVEN** until a provenance mechanism exists.

## 9. Unresolved blockers (must not be camouflaged)

1. **F1_PROVENANCE_BLOCKED** for zero-object tenants: cannot distinguish wipe vs pre-QA.  
2. Least-privilege metadata visibility untested.  
3. No SQL Server QA race/rollback EXECUTED.  
4. ADJ/TR POST may claim physical hold with **no** matching Restore (report for F3; F1 must not invent allocations).  
5. WI consume decrements `ProductionBalLot` outside `ProductionStockWriter` (hold assert exists on that path; F3 still owns algebra).  
6. Dirty QaSettings overlap F4/R-24 copy.  
7. Remote/production tenants UNKNOWN.

## 10. GO / NO-GO

| Gate | Result |
|------|--------|
| F0 audit | **PASS** as evidence package |
| F1 start | **NO-GO** this run — not authorized; provenance exception plan required for absent-table tenants |
| Flag enablement / live SQL / F2–F5 | **NO-GO** |

**STOP.**
