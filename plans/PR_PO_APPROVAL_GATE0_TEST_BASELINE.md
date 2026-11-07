# PR/PO Approval — Gate 0 Test / Tenant / Build Baseline

**Status:** INITIAL REWRITE NOT RUN. **0B addendum 2026-11-02 14:53 +08** records later unit-test/build attempts. **NOT** a Gate 3 pass. **NOT** Gate 0 certification.  
**Local HEAD:** `9fb40eed4f4246fbb7ca0cf038b2237134339d4e` (`fix/dataprotection-key-ring`).  
**Recorded:** 2026-11-02 13:06:26 +08:00 / 05:06:26 UTC. **0B addendum:** 2026-11-02 14:53 +08.  
**xUnit:** `ErpWeb.Tests.csproj` package `xunit` **2.9.3** (runner 3.1.4). Do not add `Assert.Skip` (v3). Do not upgrade xUnit as part of this ERP work.

Historical draft backed up at `plans/_gate0_evidence_originals/PR_PO_APPROVAL_GATE0_TEST_BASELINE.md.20261102T130626+08.bak` (SHA-256 `A944C49E08EBDCB4837AB50C418A59565E94E9F0DE80C462564EC42242CAD0A2`). That draft pinned `03a142cc` and included DEMO catalog counts. Those counts are **HISTORICAL / NOT CURRENT PROOF**.

---

## 1. Checked-in vs local test files

| File | HEAD blob | Worktree SHA-256 | Status |
|------|-----------|------------------|--------|
| `PoOrderServiceTests.cs` | `1d3e292e9b8e6fc4b8149e5d6aaf8104b9ca07c3` | `A0BA49E9F5CB91CF3F073E3770F2DBB7563371252202DDE9F013DE5D597EBEB8` | **DIRTY** (+6) |
| `PoPrServiceTests.cs` | `50f78448e900e66a6067cd951710cef1149091f8` | `336FF0DC9A144205BDA35CD9B74F153BE5C8CBCD700D1D81963CEA4B040CD944` | **DIRTY** (+6) |
| `ProcurementApprovalServiceTests.cs` | `a18c2a0394e029efb701eebded754b698294a085` | `6E0201B7BBCA97D671C4290D7F6117FE88E05DA457397776A5627B54D9DBA819` | **DIRTY** (+72/−) |
| `ProcurementApprovalSqlServerConcurrencyTests.cs` | `1043916f5bafcb39ed64c2d0b32c3cc06889e0b6` | `A38B1CF20940C1604F1F29AB6025A85B763778B3A5CD261C1B1799D03888EF51` | **DIRTY** (419-line rewrite) |
| `PoOrderSqlServerConcurrencyTests.cs` | `a6e2d7010935eb650cb564e76176b9d28441ac72` | `7FC8E713BC92F417351B76F2D182ABB768D315A35579DEA9D015DE73A7C6FFAB` | **DIRTY** (+6) |
| `PoPrSqlServerConcurrencyTests.cs` | `3151e461b90c847cc5249d054d54fd52e4dcec01` | `A35DA0307CF8CE8B178286F9878DDF5492CC17A35D97C1B8B84EC71E860516DD` | **DIRTY** (+6) |
| `IvInventoryPostingServiceTests.cs` | `3d096fecbbcdfdce4951d164d92f3fb6f892a771` | `76E27E875F87E89EDBF2A316DDCC479B49CB3CC8D253ABDE6B4BB6A8474F4DB7` | **DIRTY** (+138) |
| `IvGoodsReceiptServiceTests.cs` | `a32dbb4496b42bf25ad7852b5f9166d6fdb4bd32` | `ED2AB34194A1BC86E418C90CDADF9E5C0B66D3C36288F6C2B227563C35B74036` | **DIRTY** (+6) |
| `PoApprovalSqlGate3Fixture.cs` | **not in HEAD** | `38B25686B5A6CB1C2C9B81A2E721EB82B376FCAC3901C7447FDD9655CFBCFA11` | **UNTRACKED WORKTREE** |
| `ErpWeb.Tests.csproj` | `1802e4a0854733b488205a7783f8cc4b745c5fd1` | `105E13423473788E7737191D7557EEFAE886BEC45AEDB08BC8D5A8FA6C6E5719` | **DIRTY** (+6; copies `create-po-approval.sql`) |

`git diff --cached --name-status`: **empty**. Nothing staged.

### Constructor mismatch (do not invent compiler output)

**COMMITTED `9fb40eed` test factories omit the required `IProcurementApprovalGuard` argument** that production constructors now require:

- `PoOrderService` ctor `PoOrderService.cs` 198–209 `approval ?? throw`.
- COMMITTED `PoOrderServiceTests.cs` factory: `new PoOrderAttachmentService(...)` then `new PoOrderService(..., logger)` — **no guard argument**.
- COMMITTED `PoPrServiceTests.cs` factory: `new PoPrService(..., logger)` and `new PoPrAttachmentService(...)` — **no guard argument**.

That is a **source-level mismatch** on a clean checkout of HEAD. This rewrite **did not run** `dotnet build`; no CSC error log is claimed.

**WORKTREE 0B:** the same factories pass `TestDisabledProcurementApprovalGuard.Instance` (test project), not the Core no-op. `ServiceRegistrationTests` asserts production `IProcurementApprovalGuard` is `ProcurementApprovalService`. That is an **explicit OFF stub** in tests (D8). It is **not** proof the committed tree compiles, and it is **not** a production DI fallback. The dirty posting constructor still falls back to Core `DisabledProcurementApprovalGuard.Instance`.

**WORKTREE posting tests:** `IvInventoryPostingServiceTests.cs` includes `approval: DisabledProcurementApprovalGuard.Instance` at ~564 and a `RecordingProcurementApprovalGuard` (~829). Present only in the dirty tree.

---

## 2. SQL harness: committed vs dirty

### COMMITTED (`HEAD` `ProcurementApprovalSqlServerConcurrencyTests.cs`)

Class comment ~21–23: skipped unless **DefaultConnection** can open DEMO and `PoApprovalPolicy` exists; policy snapshotted and restored in `finally`.

`GetSqlServerConnectionString` ~35–57: reads `appsettings.json` **DefaultConnection**.

`RunWithPolicyAsync` ~192–204:

- if `!IsSqlServerAvailable()` → **`return;`**
- if `!SchemaReadyAsync` → **`return;`**
- if `!snapshot.Succeeded` → **`return;`**
- if fixture null → **`return;`**

A silent `return` is **NOT PASS**. Using shared DEMO DefaultConnection is **forbidden** for Gate 3.

### WORKTREE (only if found locally — **found**)

`ErpWeb.Tests/Procurement/Transaction/PoApprovalSqlGate3Fixture.cs` (untracked):

- Connection **only** from env `PR_PO_APPROVAL_TEST_SQL_CONNECTION`.
- Catalog must start with `erpweb_poapr_test_`; rejects `ERPWeb` / `DEMO` / `master`.
- `[PoApprovalSqlFact]`: if env unset **and** `PR_PO_APPROVAL_SQL_REQUIRED` is not `1` → **Skip** with `MissingConnectionMessage`.
- If `PR_PO_APPROVAL_SQL_REQUIRED=1` and connection missing → `InitializeAsync` **throws**.

This machine at capture time:

```
PR_PO_APPROVAL_TEST_SQL_CONNECTION=UNSET
PR_PO_APPROVAL_SQL_REQUIRED=UNSET
```

Therefore the dirty SQL facts would **Skip**, not run. **Skip is NOT PASS.** This rewrite **did not execute** those tests.

---

## 3. Build and test commands

### 3a. Documentation rewrite (13:06 +08) — NOT RUN

No execution log tied to HEAD `9fb40eed` at the 13:06 rewrite. Do not treat as PASS.

### 3b. Step 0B addendum (2026-11-02 ~14:55 +08) — RUN

Stopped locking host `ErpWeb.exe` PID 52844, then:

```
dotnet build ErpWeb.slnx --nologo
```

**Result:** exit **0**. `0 Error(s)`, `108 Warning(s)`, elapsed ~19.5s. **Not** a Gate 3 pass. Warnings are preexisting (xUnit analyzers, NU1902/NU1903, etc.).

```
dotnet test ErpWeb.Tests\ErpWeb.Tests.csproj --no-build --nologo --filter "FullyQualifiedName~PoPrServiceTests|FullyQualifiedName~PoOrderServiceTests|FullyQualifiedName~IvGoodsReceiptServiceTests|FullyQualifiedName~IvInventoryPostingServiceTests|FullyQualifiedName~ProcurementApprovalServiceTests|FullyQualifiedName~ServiceRegistrationTests|FullyQualifiedName~PoApprovalSettingsDisplayTests"
```

**Result:** exit **0**. Failed **0**, Passed **216**, Skipped **0**, Duration ~1m 5s. SQLite / in-memory. Does **not** prove SQL Server applock semantics or GR/NG Gate 1.

```
dotnet test ErpWeb.Tests\ErpWeb.Tests.csproj --no-build --nologo --filter "FullyQualifiedName~ProcurementApprovalSqlServerConcurrencyTests"
```

**Result (14:55 +08, env unset):** exit **0**. Failed **0**, Passed **0**, Skipped **6**. Skip is NOT PASS.

### 3c. Isolated SQL addendum (2026-11-02 ~15:00 +08) — RUN

Created/used disposable catalog `erpweb_poapr_test_20261102` on local `.\SQLEXPRESS` (Windows auth). **Not** `ERPWeb`, **not** `DEMO`, **not** `DefaultConnection`.

```
PR_PO_APPROVAL_TEST_SQL_CONNECTION=Server=.\SQLEXPRESS;Database=erpweb_poapr_test_20261102;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true
PR_PO_APPROVAL_SQL_REQUIRED=1
dotnet test ErpWeb.Tests\ErpWeb.Tests.csproj --no-build --nologo --filter "FullyQualifiedName~ProcurementApprovalSqlServerConcurrencyTests"
```

**Result:** exit **0**. Failed **0**, Passed **6**, Skipped **0**, Duration ~3s (catalog already created by fixture `EnsureCreated` + `create-po-approval.sql`).

Passed facts:
- `Preflight_isolated_catalog_has_filtered_unique_index`
- `SqlServer_two_policy_publishers_leave_one_published_row`
- `SqlServer_concurrent_submit_returns_the_same_pending_instance`
- `SqlServer_concurrent_approve_and_reject_exactly_one_wins`
- `SqlServer_disabling_policy_does_not_unlock_pending_conversion`
- `SqlServer_cross_company_pending_is_not_visible_to_demo_inbox`

Post-run catalog check (redacted): `DB_NAME()=erpweb_poapr_test_20261102`; approval tables present; `UX_PoApprovalInstance_Active` filter `([Status] IN (N'PENDING', N'APPROVED', N'NOT_REQUIRED'))`.

**This is not Gate 3.** Missing: R1–R6 GR vs revise, NEW WAC/Std/FIFO post, rollback/VR, repair, enable-vs-GR, multi-PO batch, attachment vs submit. Those remain **NOT RUN**. Never DefaultConnection. Never shared/production DEMO.

---

## 4. Tenant catalog table

Live tenant / shared `ERPWeb` / `DEMO` catalogs were **not** queried. Previous draft DEMO instance counts are **not inherited**.

| Tenant alias | Catalog identity | Approval tables present | Active policy counts | Pending/Approved/NotRequired | Time zone proof | Data sampled? | Timestamp |
|--------------|------------------|-------------------------|----------------------|------------------------------|-----------------|---------------|-----------|
| Isolated Gate 3 fixture | `erpweb_poapr_test_20261102` on `.\SQLEXPRESS` | yes (`PoApprovalPolicy`, `PoApprovalInstance` + filtered unique) | fixture-seeded only | fixture-seeded only | not proven | SQL tests wrote fixture rows only | 2026-11-02 15:00 +08 |
| Shared ERPWeb / DEMO | **not queried** | UNKNOWN — NOT VERIFIED | UNKNOWN — NOT VERIFIED | UNKNOWN — NOT VERIFIED | UNKNOWN — NOT VERIFIED | no | 2026-11-02 15:00 +08 |

Required later for D7: instance counts **per authorized tenant** before fingerprint changes. Other live servers: **NOT QUERIED**.

App model (source only): single `DefaultConnection` in `ErpWeb/Program.cs`; company/branch isolation is claim-scoped, not separate catalogs in this checkout.

---

## 5. Stock / costing protection matrix

Approval may **veto before mutation**. Successful-path costing and quantity algorithms must remain the existing ones. **This rewrite made zero source changes to these engines.**

| Scenario | Required invariant | Costing files | Evidence status |
|----------|--------------------|---------------|-----------------|
| NEW stock GR POST | Denied: no RecvQty, IvBalLoc, FIFO/WAC/Standard, ledger, batch POSTED. Permitted: existing `PostInventoryMRCoreAsync` + `ApplyGoodsReceiptPoQtyAsync(+1)` unchanged | `IvInventoryPostingService.cs` COMMITTED unguarded; WORKTREE WIP helper | **NOT RUN.** COMMITTED gap C2 |
| NEW NG POST | same RecvQty; NG core does not write IvBalLoc | same | **NOT RUN** |
| NEW backdated WAC GR | Gate as NEW-post; **do not change WAC arithmetic** | `IvInventoryPostingService.WacReplay.cs` DIRTY flag only on NEW call 211 | **NOT RUN** |
| NEW backdated Standard GR | Gate as NEW-post; Standard engine frozen | `StandardMrReplay.cs` DIRTY 107 | **NOT RUN** |
| NEW backdated FIFO GR | Gate as NEW-post; CostLayer engine frozen | `FifoReplay.cs` DIRTY 117 | **NOT RUN** |
| GR rollback | Must **not** be blocked by NEW-receipt approval; RecvQty −1 exact reverse | WORKTREE helper default false at 1231 | **NOT RUN.** Observed ungated; not certified |
| Vendor return | Must **not** be blocked by NEW-receipt approval; ReturnQty only | WORKTREE 4403 default false | **NOT RUN** |
| Historical replay of already-posted descendants | No first-time approval; costing repair/replay internals frozen | replay partials rollback calls omit flag | **NOT RUN** |
| Repair `RepostAsync` | Public `PostAsync` = NEW-post until signed repair context | `CostingRepairAdapters.cs` 293–299 CLEAN | **NOT RUN.** D5 OPEN |
| Invoice without GR | Existing `ValidateMatchOnPostAsync` 1633–1636 blocks net received 0 (non-CN). **No new approval gate. Tax/FX frozen** | `PoInvoiceService.cs` | **NOT RUN** this rewrite |
| Purchase CDN | No RecvQty write in CDN service; VR subpath for stock | `PoCdnService.cs` 1890+ | **NOT RUN** |
| Partial PR→PO consumption | Remaining-qty formulas unchanged; convert gated by approval only | `PoOrderService` consumption 1765–1801 | **NOT RUN** |
| Month-end / closed period | Existing period-close guards unchanged | inventory period services (not edited) | **NOT RUN** / **FROZEN** |
| Future-date restriction | Existing `EnsureValidMovementDate` / `RequiresInventoryBusinessDateGate` unchanged (`IvInventoryPostingService.cs` 79–80) | posting service | **NOT RUN** / **FROZEN** |
| Cross-company access | Inbox/submit scoped to tenant; DIRTY SQL test exists for ISO2 vs DEMO | WORKTREE SQL test `SqlServer_cross_company_pending_is_not_visible_to_demo_inbox` | **NOT RUN** (would skip) |
| Audit | Events/instances preserved; no delete of history | approval service | **NOT RUN** |
| Concurrency / all-or-nothing multi-PO | Denied or lock-timeout: no partial RecvQty/stock | PATH_LOCK_MATRIX R1–R6 | **NOT RUN** |
| Denied-post all-or-nothing | Exception/timeout after lock: rollback entire tx; no costing side effects | posting tx Begin/Rollback | **NOT RUN** |

If a later implementation would need to edit any frozen costing/quantity method: **BLOCKED — COSTING/QUANTITY LOGIC IMPACT**.

---

## 6. Schema notes (source, not live proof)

| Object | Script | EF | Live catalog |
|--------|--------|----|--------------|
| Approval tables | `scripts/create-po-approval.sql` idempotent | mapped | UNKNOWN — NOT VERIFIED |
| `UX_PoApprovalInstance_Active` filtered unique | script (historical 136–139) | **not in EF mapping** (prior note; not re-diffed line-by-line this pass) | UNKNOWN |
| Menus | `scripts/init-po-approval-menu.sql` | `MenuCodes` | UNKNOWN |
| Fingerprint version column | none | none | n/a V1 |

Missing schema after mandatory-guard deploy must **block post** with an operator-visible error, never behave as policy OFF.

---

## 7. Protected regression list (later — not run)

Policy **OFF** (real guard, no published policy, no Pending): `PoPrServiceTests`, `PoOrderServiceTests`, `IvGoodsReceiptServiceTests`, `IvInventoryPostingServiceTests`, PR remaining-qty, invoice net-received, CDN/VR/GR rollback.

Policy **ON** (isolated `erpweb_poapr_test_*` only): submit/approve/NotRequired; Pending blocks disable; self-approval denied; GR/NG PostAsync denied/permitted per D3; multi-PO all-or-nothing; revise vs GR; NEW WAC/Std/FIFO gated; rollback/VR not gated; repair PostAsync as NEW; picker no cutover persist; DI real guard.

---

## 8. Gate 0 documentation-rewrite checklist

| Criterion | Result |
|-----------|--------|
| Local HEAD `9fb40eed` verified | **PASS** (`git rev-parse HEAD`) |
| Remote `origin/productionv2` `2b348746` vs local `productionv2` `ed703f66` distinguished | **PASS** (`git show-ref --verify`) |
| Original three evidence files backed up with matching SHA-256 | **PASS** |
| Only Markdown evidence targeted by the 13:06 rewrite | **PASS** for that rewrite |
| 0B worktree changed approval service, settings token mapping, and tests | recorded; posting/costing engines **not** edited in 0B |
| COMMITTED vs WORKTREE vs UNKNOWN labelled | **PASS** |
| `dotnet build ErpWeb.slnx` | **0 errors** this addendum (after stopping ErpWeb PID 52844) |
| Focused unit tests | **216 passed / 0 failed / 0 skipped** this addendum |
| Isolated SQL concurrency | **6 passed** on `erpweb_poapr_test_20261102`. **Not** Gate 3; R1–R6 / GR/NG / costing races **NOT RUN** |
| Tenant catalog inventory | UNKNOWN — NOT VERIFIED |
| Gate 0 certified? | **NO** |
| Gates 1–3 | **BLOCKED** |

---

EVIDENCE REWRITE COMPLETE (if criteria satisfied); GATE 0 NOT CERTIFIED; GATES 1–3 BLOCKED PENDING SIGNED DECISIONS, BUILD, AND ISOLATED SQL CONCURRENCY TESTS.
