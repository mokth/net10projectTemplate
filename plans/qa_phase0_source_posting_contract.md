# ProductionV2 QA Phase 0 — Source / Posting / Costing Contract

**Status:** PHASE_0_AUDIT_COMPLETE  
**Date executed:** 2026-11-02  
**Agent:** Cursor Grok 4.6  
**Instruction:** audit only. No QA schema, entities, services, menus, Razor pages, feature switches, or posting guards were created.

ERPOrigin study `ERPOrigin_Production_QA_Concept_Study_for_Productionv2(1).md`: **EXTERNAL_REFERENCE_UNAVAILABLE** (not in this checkout, not attached in this session). Repository source overrides historical concepts. Absence does not authorize invented legacy QA posting helpers.

Live tenant database, costing method per company, `IvStatus` QCHOLD rows, indexes, and active ledger epoch: **UNVERIFIED**.

---

## 0. Repository identity (START and END)

### START (before any write)

| Item | Value |
|------|--------|
| Branch | `fix/dataprotection-key-ring` |
| HEAD | `9fb40eed4f4246fbb7ca0cf038b2237134339d4e` (2026-11-02 12:47:09 +0800, “commit the approva”) |
| `origin/productionv2` | `2b348746c40055aa322bd07d3f15d46b0bd35e32` |
| Local `productionv2` | `ed703f6665d90cc979043395afd3089ec36fdd86` |
| Staged | none (`git diff --cached --name-status` empty) |
| Remote comparison (from reviewer) | fix branch diverged from productionv2 (ahead 17 / behind 2). Not a fast-forward. |

Source of truth: **this local working tree**. No branch switch, reset, stash, checkout, merge, pull, cherry-pick, or auto-commit was performed **by this agent**.

### Protected dirty / untracked work at START (must remain untouched by this agent)

During this audit the user committed most of this set as `717f348` (`prodcut defination enhance`). The agent did not include those files in any edit. Remaining dirty docs/deploy files were also not edited.

**Modified (tracked):** `.gitignore`, `ANTIFORGERY_FIX.md`, `DEPLOYMENT_GUIDE.md`, `FINAL_DEPLOYMENT_STEPS.md`, `deploy.ps1`,  
`ErpWeb.Core/Inventory/IvGoodsReceiptService.cs`,  
`ErpWeb.Core/Inventory/IvInventoryPostingService.cs`,  
`ErpWeb.Core/Inventory/IvInventoryPostingService.FifoReplay.cs`,  
`ErpWeb.Core/Inventory/IvInventoryPostingService.WacReplay.cs`,  
`ErpWeb.Core/Inventory/IvInventoryPostingService.StandardMrReplay.cs`,  
`ErpWeb.Core/Planning/PrProductDefService.cs`,  
`ErpWeb.Core/Purchase/ProcurementApprovalService.cs`,  
`ErpWeb.Tests/ErpWeb.Tests.csproj` plus many Inventory/Procurement/Sales/Production test fixtures,  
Product Definition Razor files, `PoApprovalSettings.razor.cs`.

**Untracked (non-snapshot):** `DATA_PROTECTION_KEY_RING_RUNBOOK.md`, Product Definition tests/CSS, PO-approval tests/plans, `setup-all-agent-skills.cmd`, `plans/PR_PO_APPROVAL_*`, `plans/_gate0_evidence_originals/`.  
Also a large untracked `.replay-wac-fg-origin-20261030/` snapshot tree (ignored for this audit except as evidence it exists).

### Inspected uncommitted posting hunks (working tree vs HEAD)

These are **neighboring procurement-approval / PO-revision guards**, not costing-formula changes. Phase 2A must add QA claims **beside** them, never revert them.

| File | Working-tree delta | Effect on QA later |
|------|--------------------|--------------------|
| `IvInventoryPostingService.cs` | Inject `IProcurementApprovalGuard`; `AssertNewGoodsReceiptPoApprovalAsync` after PO lock on fast-path GR POST (~1126–1131); `LockGoodsReceiptPurchaseOrdersAsync` adds `PurchaseOrderRevisionSetLock` + max-rel check (~4218–4234); post revision mismatch message via `PurchaseOrderRevisionSetLock.MessageFor` | QA lock order must follow branch lock → PO revision lock → approval instance locks → PO qty → **then** QA grant |
| `*.WacReplay.cs` | GR POST approval + rollback-on-fail (~217–223) | Duplicate QA claim in this write tx or fail-closed |
| `*.FifoReplay.cs` | GR POST approval (~121–125) | Same |
| `*.StandardMrReplay.cs` | GR POST approval (~110–114) | Same |
| `IvGoodsReceiptService.cs` | `SearchPoLinesAsync` paginates with `IsPoVisibleForGrAsync` (save-time, not POST) | Not a posting guard |
| Replay **rollback** adapters | **No** new approval calls in the inspected diff | QA restore is still required on rollback independently of PO approval |

Replay costing algorithms themselves were **not** rewritten in these hunks.

### SDK

`global.json` pins SDK `10.0.401` with `rollForward: latestFeature`. Machine ran `dotnet` SDK `10.0.402`.

---

## D. Sixteen-point checklist

Each item: disposition, evidence, later-phase implication.  
`SUPPORTED_WITH_HOOK` = **candidate location only**, not already enforced.

### 01 — Branch, HEAD, upstream, uncommitted/staged

**Disposition:** VERIFIED  

Evidence: commands in §0. Current checkout is **not** GitHub `productionv2@2b348746`. At START, GR posting/replay files were dirty; the user then committed them as `717f348`. Remaining dirty files are docs/deploy/gitignore only (not posting).

**Implication:** implement later phases on this working tree without reverting `717f348` PO-approval/Product Definition/test work. Phase 2A must **add** QA hooks beside `AssertNewGoodsReceiptPoApprovalAsync`. Do not treat GitHub productionv2 as current code.

### 02 — Module ownership, DI, DbContext, SQL convention

**Disposition:** VERIFIED  

| Concern | Evidence |
|---------|----------|
| No Quality namespace | Grep `ErpWeb.Core.Quality` / `ErpWeb.UI/Quality` empty |
| DI | `ErpWeb.Core/CoreServiceCollectionExtensions.cs` Production block ~260–303; no QA registration |
| DbContext | `AppDbContext.cs` `ApplyConfigurationsFromAssembly` ~349–351; Production `DbSet`s ~340–347; **no QA sets** |
| Tenant | `IDbContextFactory<AppDbContext>` + `IInventoryTenantContext`; application-layer Company/Branch filters; **no EF global query filters** |
| SQL style | Idempotent `scripts/create-*.sql` + `alter-*.sql` + preflight THROW; example `scripts/create-production-work-order-transfer.sql` lines 1–17; `CompanyCode`/`BranchCode` **nvarchar(5)** (also noted in `PoCdnSqlServerConcurrencyTests.cs` ~86–88) |
| Numbering | Production uses `IRunningNumberService` + `RunningNumberKeys`; commercial docs use `IDocumentNumberingService` |
| Attachments | Procurement `AttachmentStorageOptions` + `Po*AttachmentService`; item images separate; **no production/QA attachment adapter** |

**Implication:** Phase 1 (if approved) adds QA-owned tables/scripts/DI only. Mirror WOT create-script + `ProductionWorkOrderTransferConfiguration` (RowVersion, tenant unique indexes, Restrict FKs). Do not add schema in Phase 0.

### 03 — Permissions, cost visibility, audit

**Disposition:** VERIFIED  

- `PermissionCodes.cs` 5–23, 59–64: ACCESS, ADD, EDIT, DELETE, SUBMIT, APPROVE, POST, ROLLBACK, EXPORT, VIEW_COST exist.  
- `IAccessRightService.CanAsync(menu, permission)` is the server gate.  
- Menus: `MenuCodes.cs` + `ErpWeb/Menus/menus.xml` + `scripts/init-*-menu.sql`; `MenuDeploymentParityTests` couples XML to codes. **No QA menu codes.**  
- Production POST/ROLLBACK vs procurement SUBMIT/APPROVE are distinct. WO **Release** uses APPROVE (`ProductionWorkOrderRules.cs`).  
- Role grants are generally **manual** `RoleMenuPermission` (PO approval inbox is an exception).  
- Cost: VIEW_COST must gate monetary fields server-side; UI hiding is not authority.

**Implication:** Proposed later menus `QA`, `QA_WORKBENCH`, `QA_SETTINGS` with ACCESS/ADD/EDIT/DELETE/SUBMIT/APPROVE/EXPORT/VIEW_COST. **No POST on QA.** Source GRN/FG/VR/SC keep their own POST. Inspector vs approver separation is a Phase 1 policy setting, not present today.

### 04 — QA settings currently shipped

**Disposition:** VERIFIED  

- `AppSettingModules.Qa = "QA"` (`AppSettingModules.cs` 27–28, 49 “Quality Assurance”).  
- `QaKeys.RequireInspectionOnReceipt` / `QaRequireInspectionOnReceipt`: company-scope **Flag**, default **`"false"`**, description “Reserved for the quality-assurance module.” (`AppSettingCatalogue.cs` 62–65, 199–205, listed in `All` 229).  
- **No Core/UI reader** besides catalogue + `AdminSettings` tab icon (`AdminSettings.razor.cs` ~337) + catalogue tests.

**Implication:** Do not enable this flag. Phase 1 should introduce `QaPolicy` (OFF by default) and treat the catalogue flag as reserved/legacy until 2A explicitly wires one authority. Policy revision/effective-date must be defined before any POST gate. Turning ON later must fail-closed old NEW GRN/FG drafts unless an approved grandfather rule exists.

### 05 — Supplier-delivery physical identity

**Disposition:** VERIFIED_WITH_CONDITIONS  

GRN identity today:

- Header `IvTrxBatch.RefNo` nvarchar(50) — free text, **not unique** for GR (`IvTrxBatch.cs` 12; unique indexes are SP/CR/VR-specific).  
- Detail: `PoNo`, `PoRelNo`, `PoLineNo`, qty/UOM, lot/expiry (`IvTrxBatchDetail.cs` 40–56).  
- UI binds `Header.RefNo` (`IvGoodsReceipt.razor` ~84).  
- No server-issued delivery instance ID. Two genuine deliveries can share one supplier DN. Concurrent QA cases cannot be keyed on RefNo.

**Implication:** Phase 2A must own `(Company, Branch, DeliveryInstanceId, LineKey)` plus PO/revision/line, supplier, item, UOM snapshot. Draft GRN `IvTrxBatchDetail.Id` exists after save but not before; allocation uniqueness on posted generation, not on unpersisted drafts. Duplicate physical-delivery protection is a QA-table concern, not current GRN schema.

### 06 — GR/NG normal POST

**Disposition:** VERIFIED — candidate hook **SUPPORTED_WITH_HOOK** (not enforced)

Working-tree `IvInventoryPostingService.PostGoodsReceiptAsync` **1062–1168**:

1. Replay dispatch (GR only) **1070–1100** — see item 07.  
2. `BeginTransaction` + `BranchStockTransactionLock` **1101–1103**.  
3. `RejectFastPathStalenessAsync` GR only **1104–1109**.  
4. `LoadGoodsReceiptSnapshotAsync` **1112** — details already have `Id`.  
5. `LockGoodsReceiptPurchaseOrdersAsync` **1119** (revision set lock in dirty hunk).  
6. `AssertNewGoodsReceiptPoApprovalAsync` **1126–1131** (dirty).  
7. `ValidateGoodsReceiptPoQtyAsync` sign +1 **1134**.  
8. **Candidate QA claim:** after 1134, **before** `BeginStandaloneLedgerAsync` **1141** (ledger begin can `SaveChanges` on revision).  
9. NG → `PostNonStockGoodsReceiptCoreAsync`; stock GR → `PostInventoryMRCoreAsync` **1150–1152**.  
10. `ApplyGoodsReceiptPoQtyAsync` +1 **1159** (`RecvQty += sign * ToPurQty`).  
11. `CompleteStandaloneLedgerAsync`, `SaveChanges`, `Commit` **1160–1163**.

Entry: `DispatchAsync` **318–323** → `IvGoodsReceiptService.PostAsync` **752–761** → `_posting.PostAsync`. List UI posts; document page does not.

NG: no BalLoc; history + batch status; **still** updates PO RecvQty. Replay predicates require `GoodsReceive` only.

`ReturnQty` is **not** written on GR. Over-receipt uses `PoToleranceLookup` / `ValidateReceiptAgainstTolerance`.

**Implication:** UI/save validation is a bypass. Phase 2A must claim inside this transaction (and every replay write tx). Policy OFF = no behavior change.

### 07 — GR backdated / chrono POST alternatives

**Disposition:** VERIFIED — each route **SUPPORTED_WITH_HOOK** only if the same in-tx method is invoked; otherwise future **FAIL_CLOSED**

Enabled only when `_openPeriodBackdatedReplayEnabled` (`IConfiguration` ctor ~75–76) **and** `_coordinator != null` **and** `ReadInventoryReplayRouteAsync(...).RequiresReplay`.

Order in `PostGoodsReceiptAsync` 1070–1100:

| Order | Method | File (working tree) | Own write tx? |
|-------|--------|---------------------|---------------|
| 1 | `TryPostBackdatedWacGrAsync` | `IvInventoryPostingService.WacReplay.cs` ~129–308 | Yes |
| 2 | `TryPostBackdatedStandardMrAsync` GR overload | `StandardMrReplay.cs` ~15+ | Yes |
| 3 | `TryPostBackdatedFifoMrAsync` GR | `FifoReplay.cs` ~13–219 | Yes |
| 4 | else | `UnsupportedHistoricalInventoryPost` 1096–1100 | Fail closed |

Dirty PO-approval already sits **inside** those write txs **before** replay writers / `ApplyGoodsReceiptPoQtyAsync`. That is the pattern QA must copy.

Unhandled `RequiresReplay` already fail-closed. A QA guard **only** on the fast path **would be bypassed**.

**Implication:** Shared `IQaReceiptAuthorization` accepting the posting `AppDbContext` + locked details. If an adapter cannot host it, that QA-required replay POST must return a friendly fail **before mutation**. Do not change FIFO/WAC/standard formulas.

### 08 — GR rollback, all alternatives, financial dependencies

**Disposition:** VERIFIED — restore **SUPPORTED_WITH_HOOK** per write path; not implemented

`RollBackGoodsReceiptAsync` **1171–1282**: same early replay dispatch — WAC 1184, Standard 1193, FIFO 1202, else `UnsupportedHistoricalInventoryRollback` 1206–1210.

Fast-path rollback: tx → branch lock → staleness → snapshot → PO lock → `ValidateGoodsReceiptPoQtyAsync` sign **-1** (cannot go below invoiced net; message directs reverse PI/CN) → ledger reversal → core rollback → `ApplyGoodsReceiptPoQtyAsync` -1 → complete (stampHistory false).

WAC GR rollback (`TryRollBackBackdatedWacAsync` ~421–822): re-read tail, ledger-owner match, fingerprint, settlement guard, invoice remap with `allowPostedCreditNote: true` (~803–815). Restore QA allowance in **that** write tx after identity checks, before/with PO qty -1, same commit.

Rollback is reversal of a sealed receipt, **not** a new receipt. Dirty approval helper is **not** on rollback adapters (correct: already posted).

**Implication:** Immutable executed allocation + reversal posting id; do not delete grant history. Reopen pending execution if a CLOSED case’s GR is reversed. Stale PO revision still blocks rollback under current messages.

### 09 — Multi-batch POST/rollback, partial allocation, idempotency

**Disposition:** VERIFIED  

`DispatchAsync` **243–437**: **no outer transaction**. `foreach` distinct batchNos; per-batch try/catch. Mixed: `Posted X of Y` / `Rolled back X of Y` **409–434**, `Succeeded=false` but earlier batches stay committed. Max selection `IvPostingLimits.MaxPostSelection = 10`.

Retry: `DbUpdateConcurrencyException` → “Retry.” Unique violation → duplicate history/balance. `BeginStandaloneLedgerAsync` can return already-sealed Ok.

**Implication:** QA counters/UI must show per-batch outcomes. One grant is not atomic across a multi-select. Concurrent GRNs on one allowance need SQL uniqueness on posted generation; draft claims are provisional.

### 10 — WI posting, non-good counters, source identity

**Disposition:** VERIFIED  

`ProductionOutputService.Posting.cs` **120**:  
`processed = IvQty.Round(GoodQty + ScrapQty + RejectQty + HoldQty)`.

| Qty | Stock effect | Evidence |
|-----|----------------|----------|
| Good | Creates/increments `ProductionBalLot` (handoff or WIP/FG_STAGING) | Posting.cs ~460–523, 770–944 |
| Scrap / Reject / Hold | Operation/WO counters only; **no pool** | ~571–590 |
| Material / prior handoff consume | Direct `.Qty`/`.BaseQty` decrease (bypasses `ProductionStockWriter`) | ~345–456 |

Valued produce cost applied to **Good** paths only (`producedCost` ~458). Rollback clones materials and increments `DocumentRevision` (`Rollback.cs` ~540+).

**Implication:** Phase 1 RECORD_ONLY for WI. Non-good Phase 5 is an inspection **grant for counters**, not valued lots. Do not invent QAHOLD stock. Valued lots remain **NOT IMPLEMENTED**.

### 11 — Production pool / WIP origin / consumption

**Disposition:** VERIFIED  

- Final WIP/FG: coalesce by company/branch/WO/`ProducingRouteStepId`/item/`OutputLotNo`. `BalanceStage` = `FG_STAGING` if route output finished goods else `PROCESS_WIP` (Posting.cs ~929–930). Multiple WI posts **increment one row**.  
- Handoff lots: per operation, often without BalanceStage on create.  
- WOT: `PROCESS_WIP` + `WipStocked` + AVAILABLE only (`ProductionWorkOrderTransferService.cs` 132–140, 609). **Not FG_STAGING.**  
- PRTN: `MATERIAL_IN` with original ISSUE lineage (`ProductionStockReturnService.cs` 195–197). **Not finished WIP.**  
- PADJ: `Kind == MaterialIn \|\| Kind == Wip` with **no BalanceStage filter** (`ProductionStockAdjustmentService.cs` 100, 141, 174, 190–191, 326).  
- Shared BOM consume: `PROCESS_WIP` (`ProductionOutputService.SharedWip.cs` 126, 218).  
- `ProductionStockWriter` is canonical for FG/WOT/PRTN/PADJ; **Daily Production and IP bypass it**.

`STATUS_IN`/`STATUS_OUT` exist in `StockMovementRegistry.cs` 45–46 but **no production poster** uses them. Not a QA custody contract.

**Implication:** QA must not claim per-WI physical custody of a pooled lot. Logical hold is Phase 2C (out of Grok scope). Phase 2B FG grant is posting permission, not a reservation, unless every competing mutator is fail-closed.

### 12 — FG Receipt normal POST/rollback

**Disposition:** VERIFIED — grant **SUPPORTED_WITH_HOOK** (not enforced)

`ProductionFinishedGoodReceiptService.Posting.cs` `ExecuteAsync` **21–222**:

- Branch lock 40–41; WO `UPDLOCK` 101–102; per-source pool `UPDLOCK` 104–108.  
- Eligible + readiness **112–118** (`EligibleSources` in `ProductionFinishedGoodReceiptService.cs` **198–206**: FG_STAGING, AVAILABLE, BaseQty>0, final operation, stock-controlled item).  
- Ledger `BeginInTransactionAsync` 129; **PostLegsAsync 151–152** (inventory dest ACTIVE slice line 323; `FgReceiptOut` via writer 303–307).  
- Rollback: clone new `IvTrxBatchDetail` + `ProductionFinishedGoodSource` at `DocumentRevision++` **154–188**; new Draft posting link 180–188. Old source line IDs remain historical.  
- Idempotent sealed `StockPosting` by RequestId 43–52. Intermediate `SaveChanges` for draft postLink 95 is in-tx, not a commit.

**Candidate QA consume:** after 112–118, before PostLegsAsync 151.  
**Candidate restore:** after ReverseLegsAsync / before commit 204, bind **new generation** for reissue; keep old execution links immutable.

**Implication:** Allowance is per `ProductionBalLotId` + source line + document revision/generation. Repost cannot spend the same executed generation twice.

### 13 — Competing FG_STAGING outflows

**Disposition:** VERIFIED — **UNSAFE to call FG “protected” without extra fail-closed**

| Mutator | Consumes FG_STAGING? | Notes |
|---------|----------------------|--------|
| FG Receipt POST | Yes (intended) | Hook candidate |
| PADJ | **Yes (Kind=Wip, no stage filter)** | Competing outflow |
| WOT | No | PROCESS_WIP only |
| PRTN | No | MATERIAL_IN only |
| Shared WIP / WI handoff consume | No (PROCESS_WIP / handoff identity) | Direct writers still exist for other stages |
| WI produce | Increases coalesced FG_STAGING | Origin merge |
| WI/FG rollback, corrected FG rollback, replay writers | Can restore/reduce | Must re-validate grants |

**Implication:** Phase 2B FINAL_FG enablement requires either (a) fail-closed PADJ (and any other unguarded WIP writer of that lot) when policy ON, **or** (b) keep FG RECORD_ONLY. Do not silently restrict non-QA tenants. Phase 2C reservation is **OUT_OF_GROK_SCOPE**.

### 14 — Inventory ACTIVE / QCHOLD consumers

**Disposition:** VERIFIED (code); tenant master rows **UNVERIFIED**

- Constant `IvItemStatuses.QcHold = "QCHOLD"` (`IvTrxConstants.cs` 84–91). **No SQL seed.**  
- Slice-match (QCHOLD **not** protected if line matches pile): MI, TR, VR, SC, ADJ, stock count, generic replay of those types.  
- TR preserves IStatus on from/to slices (`BuildTrPostLineAsync` ~3808–3811) — **not** a QA conversion.  
- ACTIVE-only: SP/DO FIFO (`IvSpShipmentService` ~889, `IvSpFifoEligibility` ~43), production IP, delivery fulfilment.  
- On-hand picker `SearchOnHandPagedAsync` has **no IStatus filter**.  
- No inventory **import** movement service found.  
- PoCdn CN stock-out uses `PostInventoryMICoreAsync` in-transaction; **does not** call `ApplyVendorReturnPoQtyAsync` (standalone VR post does). Flag for Phase 3/4, not Phase 0 code.

**Implication:** QCHOLD is **not** quarantine. Physical hold/release is Phase 4 (GPT, separate approval). Phases 1–2B must not label stock “On hold in warehouse.”

### 15 — Date, period, cost, rollback invariants

**Disposition:** VERIFIED (code-inspected); live epoch/costing method **UNVERIFIED**

- Period: `IvPeriodCloseGuard.EnsureOpenAsync` on inventory posts; production `TransactionPostingDateGuard`.  
- Future POST blocked for normal inventory types including GR/NG (`FuturePostDateError`; tests `Locked_core_applies_future_date_gate_*` **EXECUTED**). Drafts may be future-dated.  
- Replay flag default should stay OFF in production (`productionv2-costing` replay-rollback.md).  
- Cost: FIFO layers / WAC / standard owned by existing ledger; QA must not write `IvBalLoc.StdQty`, `ProductionBalLot.TotalCost`, or CostLayer.  
- `IvQty.Scale = 4`; money 6-decimal in production authority tests (SQL **NOT RUN**).  
- Soft-delete / generation: inventory batches, FG `DocumentRevision`, `ProductionPostingGenerationGuard`.

**Implication:** QA record dates vs stock POST dates follow existing owners. No costing change in Grok phases.

### 16 — UI skills, tests, deploy viability

**Disposition:** VERIFIED_WITH_CONDITIONS  

UI contract: see `plans/qa_cursor_grok_ui_skill_evidence.md`.  
Tests: §F. SQLite suites **EXECUTED**. SQL Server concurrency/schema: **SQL_SERVER_NOT_EXECUTED** (env vars absent). Several `*SqlServer*` tests **Passed by early return** — not proof.  
Live tenant schema: **UNKNOWN**.  
Browser QA UI: **NOT RUN** (pages do not exist).  
Deploy of QA schema: not started (forbidden this phase).

---

## E1. Source / action matrix

Phase 1 for every available source: **RECORD_ONLY**. Flags/policy default **OFF**. No inventory/WIP/qty/value/status mutation by QA.

| Source type | Source identity | Physically valued now? | Posting owner | Competing consumers | Phase 1 | Earliest eligible | Proposed action/target | Verdict | Flags / prerequisites | Evidence |
|-------------|-----------------|------------------------|---------------|---------------------|---------|-------------------|------------------------|---------|----------------------|----------|
| INCOMING_PRE_GRN | Company/Branch + **new** DeliveryInstanceId/LineKey + PO No/Rel/Line + supplier + item + UOM; not GRN RefNo | No | None until GR | n/a (not on-hand) | RECORD_ONLY | 2A | Accept → existing GR POST ≤ accepted; reject-before-GRN tracked without VR/CN/SC | SUPPORTED_WITH_HOOK if all GR POST/rollback adapters claim/restore; else FAIL_CLOSED that route | QaPolicy INCOMING OFF; do not enable catalogue flag | §D.05–09 |
| NG | Same PO keys; `TrxType=NG` | No BalLoc | `PostNonStockGoodsReceiptCoreAsync` | n/a | RECORD_ONLY | 2A policy-scoped | Do not convert to QA stock | NOT a QA stock source | Exclude from stock QA policy | §D.06 |
| PRODUCTION_OUTPUT_GOOD | Posted WI + movement/posting ids; **pool may merge** | After post, via BalLot | `ProductionOutputService` | Later WI, FG, PADJ, WOT if PROCESS_WIP | RECORD_ONLY | 1 inspect; physical 4/5 | Inspect only | RECORD_ONLY; no per-WI pile | Never treat GoodQty as reserved | §D.10–11 |
| PRODUCTION_OUTPUT_NON_GOOD | WI Hold/Reject/Scrap counters | No | Counters only | none (no pool) | NON_GOOD_COUNTER | 5 | Inspect then authorize existing WI POST | PHASE_5_CODED (counters; valued lots NOT IMPLEMENTED) | Banner required | §D.10 |
| PRODUCTION_WIP_LOT | `ProductionBalLotId` + RowVersion + stage PROCESS_WIP | Yes | Writer + WI direct consume | WI, WOT, PADJ, shared WIP | RECORD_ONLY | 2C logical hold (GPT); 4 physical | Keep_hold/rework later | BLOCK_UNTIL_2C / 4 | Do not implement 2C here | §D.11 |
| FG_STAGING_LOT | BalLotId + AVAILABLE + FG_STAGING + generation | Yes | FG Receipt | **PADJ**, FG POST, produce merge, rollbacks | RECORD_ONLY | 2B only if PADJ/others fail-closed when policy ON | Authorize existing FG Receipt qty | SUPPORTED_WITH_HOOK **conditional**; else RECORD_ONLY | Do not restrict non-QA tenants | §D.12–13 |
| POSTED_GRN_STOCK | IvTrxBatchDetail.Id + posting generation + IvBalLoc | Yes | Inventory posting | MI/TR/VR/SC/ADJ/count/SP/IP | RECORD_ONLY | 4 for QCHOLD | Link VR/SC in Phase 3 (GPT) as UNVERIFIED until in-tx hook | BLOCK_UNTIL_4 for custody; Phase 3 navigation only | QCHOLD not quarantine | §D.14 |
| INVENTORY_STOCK | IvBalLoc slice incl. IStatus | Yes | Inventory posting | same | RECORD_ONLY | 4 | same | BLOCK_UNTIL_4 | | §D.14 |
| CUSTOMER_RETURN | Sales CR/CN | Out of initial scope | Sales | — | UNAVAILABLE | separately validated | — | UNAVAILABLE | Do not invent | DispatchAsync CR is inventory CR, not QA source |

Rejected-before-GRN is a **delivery outcome**, not VR. Approved GR allowance consumed only by a real GR posting in the same ledger transaction.

---

## E2. Route matrix (independent owners)

Line numbers are from the **working tree** on 2026-11-02, not the 2026-10-30 GitHub snapshot.

| Trigger | Caller | Method | Lock + tx owner | Before / after stock mutation | PO/WO revisions | Branch | Rollback path | Proposed QA consume/restore | Coverage | Prohibition |
|---------|--------|--------|-----------------|-------------------------------|-----------------|--------|---------------|-----------------------------|----------|-------------|
| List POST GR | `IvGoodsReceiptService.PostAsync` | `DispatchAsync` → `PostGoodsReceiptAsync` | Per-batch tx; branch lock then PO/approval | Claim **before** `BeginStandaloneLedgerAsync` 1141 | Latest rel + dirty approval | Normal (no RequiresReplay) | `RollBackGoodsReceiptAsync` fast path | After qty validate 1134 / restore after -1 validate | SUPPORTED_WITH_HOOK | UI-only check |
| Multi-select | same | `DispatchAsync` 306–407 | **Independent** txs | Per batch | same | same | per batch | Per-batch counters | VERIFIED partial success | Pretend all-or-nothing |
| Backdated WAC GR | same if flag+RequiresReplay | `TryPostBackdatedWacGrAsync` | **Own** write tx | After dirty approval ~217, before replay insert/PO qty | same | Replay | `TryRollBackBackdatedWacAsync` | Duplicate hook or FAIL_CLOSED | SUPPORTED_WITH_HOOK / FAIL_CLOSED | Fast-path-only guard |
| Backdated Standard GR | | `TryPostBackdatedStandardMrAsync` | Own write tx | After approval ~110 | | Replay | `TryRollBackBackdatedStandardMrAsync` | same | same | same |
| Backdated FIFO GR | | `TryPostBackdatedFifoMrAsync` | Own write tx | After approval ~121 | | Replay | `TryRollBackBackdatedFifoMrAsync` | same | same | same |
| Unhandled replay | | `UnsupportedHistoricalInventoryPost` 1096 | none | none | | Fail closed already | Rollback analogue 1206 | Keep fail-closed when QA required | VERIFIED | Silent fallthrough |
| NG POST | Dispatch NG | same method, NG core | Fast path only (no replay) | Same slot; no BalLoc | RecvQty still applied | Normal | NG rollback core | Optional policy exclude | VERIFIED | Convert NG to QA stock |
| FG POST | `ProductionFinishedGoodReceiptService.PostAsync` | `ExecuteAsync` false | Branch+WO+pool UPDLOCK, one tx | After eligibility 112–118, before PostLegs 151 | WO snapshot / document revision | Normal + request-id replay seal | `ExecuteAsync` true clones sources | Grant per source line+generation; restore + rebind new IDs | SUPPORTED_WITH_HOOK | Mutate old source IDs |
| WI POST | `ProductionOutputService` | Posting.cs | Branch + WO execution lock | Produce Good only | WO remaining Good | Normal | Rollback.cs clone materials | Record-only | RECORD_ONLY | Materialize Hold/Reject |
| WI WIP handoff consume | same | direct lot mutate 345–456 | same tx | N/A for FG_STAGING typically | | Normal | restore lots | 2C only | BLOCK_UNTIL_2C | Claim quarantine |
| PADJ | `ProductionStockAdjustmentService` | Post via writer | branch lock | Can reduce Kind=Wip including FG_STAGING | | Normal | adj reversal | Fail-closed when FINAL_FG ON **or** keep FG record-only | UNGUARDED competitor | Call FG “protected” |
| WOT | `ProductionWorkOrderTransferService` | writer TRANSFER | branch + WO locks | PROCESS_WIP only | | Normal | Rollback.cs | Out of 2B FG | Not FG consumer | Use WOT for partial QA rework |
| PRTN | `ProductionStockReturnService` | RETURN writer | FG-like | MATERIAL_IN only | | Normal | reversal | Not FG/WIP finished | | Return finished WIP |
| Inventory MI/TR/VR/SC/ADJ/count | `IvInventoryPostingService` | per-type Post/Rollback | per-batch | Slice match, not ACTIVE-only | | Normal + replay if flagged | matching rollback | Phase 4 | QCHOLD not blocked | Use TR as release |
| SP/DO / IP | shipment / material issue | ACTIVE-only pick | their txs | QCHOLD piles excluded from pick | | Normal | restricted historical rollback | Partial protection only | Not universal | Infer full quarantine |
| Repair/replay FG | `ActiveFinishedGoodReplay`, corrected rollback | coordinator | repair txs | cost/qty restatement | generation | Replay/repair | owned reversal | Revalidate grants | CODE_INSPECTED | Assume GoodQty still exclusive |
| Report-only QA | future QA services | n/a | QA tables only | none | n/a | n/a | n/a | Phase 1 | RECORD_ONLY | Mark EXECUTED |

---

## E3. Atomicity and fail-closed (documentation only — not coded)

- Authorization read, lock, consume, and posted-link persist **inside the same DB transaction that mutates stock/PO/production**. Save-time or UI checks are bypasses.  
- Planned fail-closed is **not** protection. Until 2A/2B guards and SQL tests exist, QA-required settings stay **OFF**. Do not describe unguarded paths as blocked.  
- GR: every independent write owner (fast + WAC + FIFO + Standard, post and rollback) must host the hook or reject QA-required work **before mutation**.  
- FG rollback new source/detail IDs: historical links immutable; repost needs reviewable re-authorization of the **current** generation.  
- PADJ can still consume FG_STAGING; approved FG is **not** protected stock without complete coverage.  
- Later hooks may be validation adjacent to costing; they must **not** change formulas, quantity mutations, ledger algorithms, or reversal valuation without separate authorization.  
- `DispatchAsync` mixed success is the inventory product behavior; preserve it.

---

## F. Baseline tests

Environment: Windows, SDK 10.0.402, `--no-restore` succeeded (build artifacts existed).  
SQL env: `ERPWEB_PRODUCTION_STOCK_TEST_CONNECTION` **absent**; `ERPWEB_REQUIRE_SQLSERVER_TESTS` **absent**; `ConnectionStrings__SqlServerTestConnection` **absent**. **No live tenant was targeted.** Logs in OS temp `qa-phase0-baseline\` (gr.trx, inv-out.trx, prod-pocdn.trx) — not committed.

### Command 1 — GR / inventory posting (SQLite)

```
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-restore --filter "FullyQualifiedName~IvGoodsReceiptServiceTests|FullyQualifiedName~IvInventoryPostingServiceTests" --logger "console;verbosity=normal"
```

| | |
|--|--|
| Exit code | 0 |
| Total / Passed / Failed / Skipped | 110 / 110 / 0 / 0 |
| Duration | ~59.5 s |
| Classification | **EXECUTED** (SQLite). Includes NG history-only, RecvQty post/rollback, double post/rollback forbidden, future-date GR/NG, backdated WAC/FIFO/Standard GR post/rollback/PI remap, dirty `Posting_service_requires_approval_guard` / `SearchPoLines_hides_unapproved_purchase_order`. |
| SQL Server | not in this filter |

### Command 2 — VR / TR / SC / MI / ADJ / stock count

```
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-restore --filter "FullyQualifiedName~IvVendorReturn|FullyQualifiedName~IvStockTransfer|FullyQualifiedName~IvScrap|FullyQualifiedName~IvMiscIssue|FullyQualifiedName~IvStockAdjustment|FullyQualifiedName~IvStockCount"
```

| | |
|--|--|
| Exit code | 0 |
| Total / Passed / Failed / Skipped | 402 / 402 / 0 / 0 |
| Duration | ~1 m 28 s |
| Classification | SQLite posting/authority tests **EXECUTED**. |
| SQL trap | `IvStockCountSqlServerConcurrencyTests` (5 methods) **outcome Passed** via documented early return when scratch SQL is missing (`IvStockCountSqlServerConcurrencyTests.cs` 22–23, 76–78). **SQL_SERVER_NOT_EXECUTED.** Green runner ≠ concurrency proof. |

### Command 3 — WI / FG / WOT / PADJ / PoCdn

```
dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj --no-restore --filter "FullyQualifiedName~ProductionOutput|FullyQualifiedName~FinishedGoodReceipt|FullyQualifiedName~ProductionWorkOrderTransfer|FullyQualifiedName~PoCdn|FullyQualifiedName~ProductionDailyOutput|FullyQualifiedName~ProductionStockAdjustment"
```

| | |
|--|--|
| Exit code | 0 |
| Total / Passed / Failed / Skipped | 219 / 213 / 0 / **6 skipped** |
| Duration | ~41 s |
| Honest SKIP | `ProductionWorkOrderTransferSqlServerTests` ×6 — `[ProductionStockSqlServerFact]` Skip because `ERPWEB_PRODUCTION_STOCK_TEST_CONNECTION` unset. **SQL_SERVER_NOT_EXECUTED** (correct skip). |
| Green false-positive | `FinishedGoodReceiptSqlServerTests.*` — `CreateAsync` returns null without `ConnectionStrings__SqlServerTestConnection` then `if (f is null) return;` (`FinishedGoodReceiptTests.cs` 586–591, 658+). Runner **Passed**. **SQL_SERVER_NOT_EXECUTED.** |
| Green false-positive | `PoCdnSqlServerConcurrencyTests` (C2/C10/C41…) — early `return; // skip without SQL Server` ~467+. Runner **Passed**. **SQL_SERVER_NOT_EXECUTED.** |
| SQLite FG/WI/WOT/PADJ/PoCdn service tests | **EXECUTED** (the non-SqlServer classes in this filter). |

### Not in filters / not claimed

- `IvInventoryPostingSqlServerConcurrencyTests` (early `return` if `!IsSqlServerAvailable()`, lines 76–78) — **NOT RUN**.  
- `ProductionStockTransactionSqlServerTests` / `ProductionStockLedgerSqlServerTests` — **NOT RUN**.  
- Browser visual matrix — **NOT RUN**.  
- Live tenant snapshot of IvBalLoc / ProductionBalLot / FIFO / WAC — **NOT RUN** (CODE_INSPECTED only).

Pre-existing dirty tests compiled and participated (approval guards, fixture changes). Failures were **not** hidden: these three commands had zero Failed.

---

## Risk log (mandatory)

| Sev | Finding |
|-----|---------|
| CRITICAL | GR QA on fast path only is bypassed by WAC/FIFO/Standard replay dispatch (1070–1100 / 1179–1210). |
| CRITICAL | `DispatchAsync` is atomic **per batch**, not for the multi-select. |
| CRITICAL | PO-approval/revision guards now committed in `717f348` sit in the exact GR files Phase 2A must edit; add beside them, do not overwrite. |
| CRITICAL | Linking QA to VR/SC without an in-tx hook is navigation, not stock custody (Phase 3). |
| HIGH | PADJ can adjust `Kind=Wip` including FG_STAGING; FG approval ≠ reserved stock. |
| HIGH | WI Hold/Reject/Scrap are counters; no valued QA lot until Phase 5. |
| HIGH | FG rollback clones source/detail IDs; grants must be generation-safe. |
| HIGH | Pooled WIP/FG_STAGING merges multiple WI origins. |
| HIGH | Rejected-before-GRN must not create VR/CN/SC or RecvQty/ReturnQty for that qty. |
| HIGH | Policy ON/OFF/revision between draft, approve, POST, rollback, replay — not designed in code yet. |
| MEDIUM | QCHOLD constant ≠ master row ≠ consumer exclusion. |
| MEDIUM | Catalogue `QaRequireInspectionOnReceipt` unused; do not silently enable. |
| MEDIUM | PoCdn in-tx stock-out vs standalone VR `ReturnQty` helper mismatch — Phase 3/4. |
| MEDIUM | Green xUnit Passed includes SQL tests that never posted. |
| LOW | ERPOrigin study unavailable. |
| LOW | Extra local UI skills exist (footer/left-actions/inline-search/costing) beyond GitHub v4 list — binding for later UI. |

---

## H. Gate 0 verdict

| Outcome | Verdict |
|---------|---------|
| **PHASE_0_AUDIT_COMPLETE** | **YES** — both evidence documents exist; all 16 items have a disposition; matrices filled from working-tree files; baseline commands recorded; dirty-file protection intended (END status in closing section). |
| **PHASE_1_RECORD_ONLY_RECOMMENDATION** | **CONDITIONALLY_READY** — zero stock/money/posting mutations; settings remain OFF; source identity for **posted** WI/WIP/GRN/inventory can be recorded; **pre-GRN delivery instance ID does not exist yet** (Phase 1 may record PO line + user DN as non-authoritative text, or defer incoming cases until 2A introduces server instance IDs). Missing SQL/tenant proof is a **deployment/activation blocker**, not a Phase 1 coding blocker if flags stay OFF. |
| **PHASE_2A_AND_2B** | **2A CODED, 2B CODED.** |
| **PHASE_2C** | **2C CODED.** Logical hold is a reservation on `ProductionBalLot`; it does not move stock or change cost. |
| **PHASE_3** | **3 CODED.** Existing-transaction navigation only. GR/FG `EXECUTED` from sealed allocations. Reject-before-GRN is `NOT_STOCK`. |
| **PHASE_4** | **4 CODED.** Physical hold is a reservation on `IvBalLoc`. No `ACTIVE`→`QCHOLD` conversion. Evidence: `plans/qa_phase4_implementation_evidence.md`. |
| **PHASE_5** | **5 CODED (this change). Gate 5 STOP.** Non-good Daily Production is an inspection grant for **counters**. Valued lots / `producedCost` split / second PRODUCE **NOT IMPLEMENTED**. SQL Server ledger tests **NOT RUN**. Live DBA scripts **not applied**. Flags stay OFF. Evidence: `plans/qa_phase5_implementation_evidence.md`. |
| **IMPLEMENTATION_10_OF_10** | **NOT CLAIMED** |

**STOP.** Await independent Gate 0 approval before Phase 1. No Phase 1 code in this change set.

### END identity (after writing the two evidence files)

| Item | Value |
|------|--------|
| Branch | still `fix/dataprotection-key-ring` |
| HEAD | **`717f3484846233ca05e45dd51c202f1272c47cfb`** (2026-11-02 15:53:48 +0800, author **TH MOK**, subject `prodcut defination enhance`) |
| `origin/productionv2` | still `2b348746c40055aa322bd07d3f15d46b0bd35e32` |
| Staged | none |
| Agent commit? | **No.** This agent did not `git commit`. HEAD moved because the **user** committed the previously dirty PO-approval / Product Definition / test tree that Phase 0 had listed as protected WIP. |

**Re-evaluation after HEAD change:** `git diff 9fb40ee..717f348` is exactly that protected set (GR posting approval hooks, replay adapters, Product Definition, fixtures). Working-tree `AssertNewGoodsReceiptPoApprovalAsync` is now **in HEAD** at `IvInventoryPostingService.cs` ~1126 and ~4244. GR/FG/PADJ/QCHOLD hook conclusions still hold; they are no longer “uncommitted neighboring code” but **committed neighboring code** on this branch. Do not revert `717f348`.

**Agent-created paths only:**

- `plans/qa_phase0_source_posting_contract.md` (this file, untracked)
- `plans/qa_cursor_grok_ui_skill_evidence.md` (untracked)

**Still dirty, not touched by this agent:** `.gitignore`, `ANTIFORGERY_FIX.md`, `DEPLOYMENT_GUIDE.md`, `FINAL_DEPLOYMENT_STEPS.md`, `deploy.ps1`.

**Still untracked (pre-existing):** `DATA_PROTECTION_KEY_RING_RUNBOOK.md`, `plans/PR_PO_APPROVAL_*`, `plans/_gate0_evidence_originals/`, `setup-all-agent-skills.cmd`, `.replay-wac-fg-origin-20261030/`.

No previously dirty posting/test/UI file was edited by this audit. No auto-revert.
