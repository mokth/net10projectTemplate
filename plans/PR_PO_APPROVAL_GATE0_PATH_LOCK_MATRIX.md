# PR/PO Approval — Gate 0 Path / Transaction / Lock Matrix

**Status:** EVIDENCE REWRITE + STEP 0B WORKTREE ADDENDUM. **NOT** Gate 0 certification. **NOT** Gate 1–3 authorization.  
**Classification:** B costing-adjacent investigation. Costing, stock, RecvQty, FIFO/WAC/Standard, WIP, invoice arithmetic, period close, and future-date engines are **READ-ONLY / FROZEN**.  
**Recorded:** 2026-11-02 13:06:26 +08:00 (initial rewrite). **0B addendum:** 2026-11-02 14:53 +08:00.

## Verification pin (this rewrite)

| Ref | Value | Source |
|-----|-------|--------|
| Local HEAD | `9fb40eed4f4246fbb7ca0cf038b2237134339d4e` | `git rev-parse HEAD` |
| Current branch | `fix/dataprotection-key-ring` | `git branch --show-current` |
| Local branch SHA | `9fb40eed4f4246fbb7ca0cf038b2237134339d4e` | `git show-ref --verify refs/heads/fix/dataprotection-key-ring` |
| Remote tracking SHA | `9fb40eed4f4246fbb7ca0cf038b2237134339d4e` | `git show-ref --verify refs/remotes/origin/fix/dataprotection-key-ring` |
| Local `productionv2` | `ed703f6665d90cc979043395afd3089ec36fdd86` | `git show-ref --verify refs/heads/productionv2` |
| Remote `origin/productionv2` | `2b348746c40055aa322bd07d3f15d46b0bd35e32` | `git show-ref --verify refs/remotes/origin/productionv2` |
| Staged index | empty | `git diff --cached --name-status` |
| This documentation refresh | Markdown evidence only | no posting/costing/schema edits |

**Do not treat local `productionv2` (`ed703f66`) as remote `origin/productionv2` (`2b348746`).** The approval implementation is not on remote `productionv2`.

**Source labels used below:** **COMMITTED** = bytes of `HEAD:path` at `9fb40eed`. **WORKTREE** = on-disk working copy. **UNKNOWN** = not independently read or not present here.

## Original evidence-file backup (pre-rewrite)

The three target files **existed on disk**, were **untracked** (`git ls-files --error-unmatch` failed; `EXISTS_ON_DISK=yes`), and were **not** on the remote branch. They pinned stale HEAD `03a142cc` and are retained as historical/unverified drafts, not erased.

| Original path | Status | Size | SHA-256 | Backup |
|---------------|--------|------|---------|--------|
| `plans/PR_PO_APPROVAL_GATE0_PATH_LOCK_MATRIX.md` | untracked, on disk | 18676 | `B3BF4C921311F297F757272C6B058AEEE2A65C735448671D5DD594AF147FED5A` | `plans/_gate0_evidence_originals/PR_PO_APPROVAL_GATE0_PATH_LOCK_MATRIX.md.20261102T130626+08.bak` (hash match) |
| `plans/PR_PO_APPROVAL_GATE0_DECISIONS.md` | untracked, on disk | 19246 | `442C01E4B51B77D13CB68D09D7797C26D5B9F5B45A8708F53FF3C8FDEBF0CD63` | `plans/_gate0_evidence_originals/PR_PO_APPROVAL_GATE0_DECISIONS.md.20261102T130626+08.bak` (hash match) |
| `plans/PR_PO_APPROVAL_GATE0_TEST_BASELINE.md` | untracked, on disk | 9386 | `A944C49E08EBDCB4837AB50C418A59565E94E9F0DE80C462564EC42242CAD0A2` | `plans/_gate0_evidence_originals/PR_PO_APPROVAL_GATE0_TEST_BASELINE.md.20261102T130626+08.bak` (hash match) |

Owner conclusions in those originals (including DEMO instance counts and “policy applock not implemented”) are **historical/unverified relative to HEAD `9fb40eed`**. They are not copied forward as current facts.

## Dirty-file disposition (must preserve; do not reset/clean)

Observed `git status --short --untracked-files=all` at 13:06:26 +08. Staged: none.

| Path | git | Disposition |
|------|-----|-------------|
| `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` | modified | **UNREVIEWED WIP.** Worktree adds optional `IProcurementApprovalGuard?` with `DisabledProcurementApprovalGuard.Instance` fallback and `forNewReceiptPost` on shared `LockGoodsReceiptPurchaseOrdersAsync`. **Not Gate 1 architecture.** Preserve bytes. |
| `IvInventoryPostingService.WacReplay.cs` / `.StandardMrReplay.cs` / `.FifoReplay.cs` | modified | **UNREVIEWED WIP.** Each NEW-root call passes `forNewReceiptPost: true` (2-line diffs). Rollback callers still omit the flag. Preserve. |
| `ErpWeb.Core/Inventory/IvGoodsReceiptService.cs` | **clean vs HEAD** | **NOT PRESENT IN THIS CHECKOUT** as dirty GR-picker approval. Prior draft claimed `IsPoVisibleForGrAsync` in this file; **that dirty change is not here.** |
| Test factories / SQL harness / `ErpWeb.Tests.csproj` | modified | Uncommitted test wiring. Preserve. Not a build/SQL pass. |
| Planning Product Definition + CSS/tests | modified/untracked | Unrelated. Preserve. |
| Deploy docs, `.gitignore`, `DATA_PROTECTION_KEY_RING_RUNBOOK.md`, `.replay-wac-fg-origin-20261030/` | modified/untracked | Unrelated. Preserve. |
| Purchase approval/PO/PR/attachment `.cs` | mixed | COMMITTED `9fb40eed` eight Purchase files plus fingerprint were clean at the 13:06 rewrite. **WORKTREE 0B (this addendum):** `ProcurementApprovalService.cs` DIRTY; `PoApprovalSettings.razor.cs` DIRTY (Version/RowVersion pass-through only). `PoOrderService.cs` / `PoPrService.cs` / attachments / fingerprint remain CLEAN vs HEAD. |

### Worktree SHA-256 (on-disk, includes dirty bytes)

| File | SHA-256 | vs HEAD |
|------|---------|---------|
| `IvInventoryPostingService.cs` | `41C94A66AA10D026197CF700FFFAC2CD82128CE561FC96B101B7F61CAE8126CC` | DIRTY (HEAD blob `aac9c6ce`) |
| `IvInventoryPostingService.WacReplay.cs` | `19408388B04567B387E5B73F2B83EA180DE549D930441E25C4ED42D3F5958E3D` | DIRTY |
| `IvInventoryPostingService.FifoReplay.cs` | `7F936A851E372B730B6B43DC885F73020D27F7D7D3D6ECDFB82ED201EA44A6A2` | DIRTY |
| `IvInventoryPostingService.StandardMrReplay.cs` | `E03B24C856F53043F2C8FE6C52E82630626475BE71234B4B32AF4C61930872DB` | DIRTY |
| `IvGoodsReceiptService.cs` | `B7A167158956281B307D7D5767F85EA7B581EB253F83FD9CD4B948646C864B1E` | CLEAN (HEAD blob `f1451f7a`) |
| `PoOrderService.cs` | `89CBA338F2ED345EE5555B6A981E6F5D84DED607A501DFCBF7A10A664666FB84` | CLEAN |
| `PoPrService.cs` | `EFD1E20A02281C4C2AA0F3B13D02AE3DCDE12BC1F2E7B0AB305E10F4121366D2` | CLEAN |
| `ProcurementApprovalService.cs` | HEAD blob `9fb40eed` CLEAN at 13:06 rewrite; **DIRTY as of 0B addendum** | DIRTY (Submit `PO_REV`+latest, status eligibility, Reassign branch/RowVersion, attachment historical freeze, policy Version/RowVersion compare) |
| `ProcurementApprovalFingerprint.cs` | `7D00FE763D7A612F12EEA37062A4C02EDE97DF391334324F323EB260AADF92E3` | CLEAN |
| `ProcurementApprovalSqlLocks.cs` | `CA1EE05755496EA6C893AABA2F839C015C3B46C0D85A9B42C849AFD696AB61F0` | CLEAN |
| `DisabledProcurementApprovalGuard.cs` | `6F55AC78B2F0CF3DDDB5D3CDB5F801993FD67BEEA2CC8A63AC5D074623994FD3` | CLEAN |
| `CostingRepairAdapters.cs` | `4215F060FE94381913D6B25D2C6CCA5479FBAD456638738977C6FE16286323C1` | CLEAN (`git status` unmodified) |

## Non-negotiable facts (verified)

- A UI picker is **not** an authorization gate.
- **COMMITTED** `PostGoodsReceiptAsync` calls `LockGoodsReceiptPurchaseOrdersAsync` **without** an approval guard or `forNewReceiptPost` (`IvInventoryPostingService.cs` ~1138 HEAD). Constructor has **no** `IProcurementApprovalGuard` field (HEAD ~46–72).
- `PoOrderRepository.LockLatestForUpdateAsync` (`PoOrderRepository.cs` 136–149, COMMITTED=WORKTREE) still does unlocked `GetMaxRelNoAsync` then `LockForUpdateAsync`. Existence of `PurchaseOrderRevisionSetLock` is **not** proof of atomic latest-revision authorization.
- **WORKTREE** posting gate inside the shared lock helper is **UNREVIEWED WIP**, not approved architecture. Observed rollback/VR callers omit `forNewReceiptPost: true`; that is **not** a certified guarantee for every future caller.
- Costing/quantity formulas in posting, replay, repair, invoice, and CDN were **read, not edited** by this rewrite or by Step 0B.
- **Step 0B WORKTREE (approval engine only, not Gate 1):** PO Submit acquires `PO_REV`, reads max RelNo, locks that row, rechecks max (`ProcurementApprovalService.cs` 291–324). `SubmitLockedAsync` rejects non-NEW/OPEN via `SubmitDocumentStatusError` (1070–1074, 1538–1561). `ReassignAsync` compares company+branch and RowVersion (577–588). Attachment **delete** freeze includes Rejected/Returned/Withdrawn/Cancelled/Superseded (`IsSubmittedAttachmentStatus` 1567+). `SavePolicyAsync` compares Version/RowVersion when tokens are present (142–151). Settings `ToDto`/`FromDto` carry those tokens (`PoApprovalSettings.razor.cs` 644–682). Tests use `TestDisabledProcurementApprovalGuard`. **Not** a Gate 0 pass. **Not** GR/NG posting.

---

## Global lock-order proposal (design only — UNSIGNED)

Existing writers already mix orders. A future Gate 1 **must not** introduce a deadlock cycle and **must not** change costing call sequences.

Proposed **single** order for a transaction that both posts stock and checks approval (UNSIGNED):

1. Branch stock applock `STOCK_WRITE|{COMPANY}|{BRANCH}` — existing `BranchStockTransactionLock`.
2. Per-PO revision-set applock `PO_REV|{COMPANY}|{BRANCH}|{PONO}` — `ProcurementApprovalSqlLocks.PoRevisionResource` (`ProcurementApprovalSqlLocks.cs` 15–16, 63–81). Sorted by `PoNo`.
3. PR headers `POPR WITH (UPDLOCK, HOLDLOCK)`.
4. PO header exact revision `POOrder WITH (UPDLOCK, HOLDLOCK)` on rechecked max `PoRelNo`.
5. PR details `UPDLOCK`.
6. Policy applock `PO_APPR_POLICY|{COMPANY}|{DOCTYPE}` — `ProcurementApprovalSqlLocks.PolicyResource` (12–13). Publisher acquires **only this**.
7. Approval instance `PoApprovalInstance WITH (UPDLOCK, HOLDLOCK)`.

**Deadlock / inversion flags (OPEN):**

| Writer | Observed order | Risk |
|--------|----------------|------|
| `SavePolicyAsync` | policy applock only (`ProcurementApprovalService.cs` 132–141) | Publisher does **not** take document headers. **Compatible** with header-then-policy **if** every gate follows that rule. **UNPROVEN** vs GR. |
| `SubmitAsync` PO | COMMITTED `9fb40eed`: exact `LockPoAsync(DocRev)` then policy. **WORKTREE 0B:** `PO_REV` 291 → max RelNo 299 → reject stale `DocRev` 306–309 → lock latest 312 → recheck 319–323 → `SubmitLockedAsync` policy 1076 | WORKTREE Submit of old RelNo rejected (SQLite unit tests). SQL vs concurrent revise **NOT RUN**. |
| `DecideAsync` / `WithdrawAsync` | `LockDocumentThenInstanceAsync` (`332–333`, `480–481`) → PO_REV then exact DocRev then instance | **Improved vs older drafts** that claimed instance-first. Still **no policy lock**. SQL barrier vs revise/GR **NOT RUN**. |
| `ReassignAsync` | instance lock only (`576`) | No document lock (allowed if no PR/PO read). **WORKTREE 0B:** company+branch 577–579 and RowVersion 584–588. |
| WORKTREE GR NEW | BranchStock → `PO_REV` (if flag) → `LockLatest` → policy → instance (`4193–4273`) | Order **looks** header-then-policy, but lives **inside shared helper**. **UNREVIEWED WIP**. |
| COMMITTED GR | BranchStock → `LockLatest` only | **No policy/instance/PO_REV**. **FAIL**. |
| Vendor return | stock core then PO lock helper **without** NEW-post flag (WORKTREE `4403`) | Must remain ungated. Observed default `forNewReceiptPost=false`. **Not proven for all signs/callers.** |
| Applock timeout | `sp_getapplock` result &lt; 0 → timeout message (`ProcurementApprovalSqlLocks.cs` 54–57) | Fail-closed **if** caller treats non-null as abort. SQLite returns null (no-op) at line 24–27. |

Never conclude a race is safe because an applock class exists.

---

## Authoritative path table

Legend: **NEW-post** = first RecvQty/stock/commitment; **reversal**; **replay-already-posted**; **write**; **read**. Denied-post invariant: denied approval must leave batch status, RecvQty, IvBalLoc, FIFO/CostLayer, WIP, ledger, and settlement **untouched**. This rewrite did **not** execute those invariants.

### A. Inventory posting / GR / NG / VR / repair

| Flow | Entry point | Called service | Exact LOCAL file:line | COMMITTED / LOCAL-DIRTY / UNKNOWN | SQL tx | Lock acquisition order | Policy/instance resource | Mutation | Denied-post invariants | Rollback/repair classification | Permission | Gate 1 risk / evidence |
|------|-------------|----------------|-----------------------|-----------------------------------|--------|------------------------|--------------------------|----------|------------------------|--------------------------------|------------|------------------------|
| Stock GR POST | UI / `IvGoodsReceiptService.PostAsync` 704–713 | `IvInventoryPostingService.PostGoodsReceiptAsync` | COMMITTED ~1075–1155 HEAD; WORKTREE 1062–1161 (lock at 1119) | **COMMITTED: no guard.** **LOCAL-DIRTY: `forNewReceiptPost: true` at 1119** | Begin ~1102 / Commit ~1155 | COMMITTED: BranchStock → `LockLatest` only. DIRTY: BranchStock → `PO_REV` per PO → `LockLatest` → recheck max → policy → instances → `AssertPoReceivableAsync` **inside helper** | DIRTY: `PO_APPR_POLICY\|COMPANY\|PO` + instance keys. COMMITTED: none | RecvQty +1, IvBalLoc, history, ledger, batch POSTED | **NOT ENFORCED on COMMITTED.** DIRTY attempts veto before qty/ledger **inside shared helper** — uncertified | **NEW-post** | existing GR post rights | **CRITICAL.** Committed bypass. Dirty helper is **UNREVIEWED WIP**. Costing core must stay unchanged. |
| Non-stock NG POST | same | `PostNonStockGoodsReceiptCoreAsync` | WORKTREE 1142–1144, core ~1277+ | same split | same | same | same | RecvQty +1; NG core does not write IvBalLoc | same | **NEW-post** | same | same |
| Direct `PostAsync` | tests, repair | `PostAsync` 231–235 → `DispatchAsync` 243+ | COMMITTED=WORKTREE signature; DIRTY ctor 46–72 | COMMITTED ctor has **no** guard. DIRTY optional guard + `?? DisabledProcurementApprovalGuard.Instance` (72) | via PostGoodsReceipt | same as GR/NG | DIRTY fallback **permits** if null | same as GR/NG | Production fallback **unacceptable**. Tests may use explicit OFF stub (D8) | **NEW-post if GR/NG** | server post | **CRITICAL** constructor fallback in DIRTY posting |
| WAC NEW backdated GR | `PostGoodsReceiptAsync` 1070–1077 | `TryPostBackdatedWacGrAsync` | `WacReplay.cs` WORKTREE 210–211 `forNewReceiptPost: true`; COMMITTED call has no flag | **COMMITTED unguarded.** **LOCAL-DIRTY flag on NEW root only** | own tx after BranchStock ~197–199 | Branch → helper | DIRTY inherits helper gate | RecvQty +1 + historical WAC insert | Costing arithmetic **FROZEN**; gate must be fail-before-mutation | **NEW-post for approval** (root commitment). Costing engine is replay | same | Gate NEW-post; **do not rename Replay as exemption** |
| Standard NEW backdated GR | 1078–1086 | `TryPostBackdatedStandardMrAsync` | `StandardMrReplay.cs` WORKTREE 106–107 flag true | same | own tx | same | same | RecvQty +1 + Standard root | same | **NEW-post for approval** | same | same |
| FIFO NEW backdated GR | 1087–1095 | `TryPostBackdatedFifoMrAsync` | `FifoReplay.cs` WORKTREE 116–117 flag true | same | own tx | same | same | RecvQty +1 + FIFO root | same | **NEW-post for approval** | same | same |
| GR rollback fast | `IvGoodsReceiptService.RollbackAsync` 716+ | `RollBackGoodsReceiptAsync` | WORKTREE 1231 helper **no flag** | COMMITTED unguarded; DIRTY default `forNewReceiptPost=false` | Begin ~1204 | Branch → helper without NEW gate | none intended | RecvQty −1, reverse stock/ledger | Must **not** be blocked by new-receipt approval | **reversal** | rollback rights | Observed ungated in DIRTY. **Not a full-caller proof.** |
| WAC/Std/FIFO rollback replay | rollback 1171–1197 | `TryRollBackBackdated*` | WAC 673; Std 781–782; FIFO 383 — **no flag** | DIRTY observed omit flag | own tx | Branch → helper without NEW gate | none intended | RecvQty −1 | must not hit NEW-post gate | **reversal** | rollback | same |
| Vendor return PO qty | `IvVendorReturnService` posting | `ApplyVendorReturnPoQtyAsync` | WORKTREE 4389–4404 helper **no flag** | DIRTY observed omit flag | caller tx | **Stock core first, PO lock after** | none intended | ReturnQty ±; **not** first RecvQty | must not hit NEW-post gate | ReturnQty write | VR post | same. Lock-order vs BranchStock is **inverted vs GR** (pre-existing). |
| Repair reverse | `CostingRepairService` | `PurchaseGoodsReceiptRepairAdapter.ReverseAsync` | `CostingRepairAdapters.cs` 278–290 | CLEAN vs git status | via GR rollback | as rollback | none | as rollback | must remain reversal | **reversal** | repair | Do not add NEW-post gate here |
| Repair repost | same | `RepostAsync` | `CostingRepairAdapters.cs` 293–299 `_posting.PostAsync(...)` | CLEAN. `CostingRepairExecutionContext` unused | via PostAsync | same as NEW post | would inherit posting gate | same as GR post | Public PostAsync is **not** privileged identity | **NEW-post of a NEW batch** after rollback. **No tamper-resistant proof** | repair | **UNRESOLVED D5.** STOP rather than public skip flag |
| GR picker | UI | `SearchPoLinesAsync` | `IvGoodsReceiptService.cs` 191+ | CLEAN. **No** `IsPoVisibleForGrAsync` in this checkout | no write tx | none | none | none | N/A | **read** | ACCESS | Prior dirty picker claim **NOT PRESENT** |
| GR save new/update | UI | `SaveNewAsync` 493; `UpdateAsync` | `IvGoodsReceiptService.cs` | CLEAN | draft tx **no** BranchStock on save new | line pickability | **no approval** | draft lines; **no RecvQty** | N/A | **write draft only** | ADD/EDIT | Do not treat save as post gate |
| GR delete draft | UI | `DeleteAsync` | `IvGoodsReceiptService.cs` | CLEAN | tx + BranchStock | branch | none | draft delete | N/A | write | DELETE | — |

**WORKTREE callers of `LockGoodsReceiptPurchaseOrdersAsync`:** PostGoodsReceipt 1119 (`true`); RollBackGoodsReceipt 1231 (default); ApplyVendorReturnPoQty 4403 (default); WacReplay 211 (`true`), 673 (default); StandardMrReplay 106–107 (`true`), 781 (default); FifoReplay 116–117 (`true`), 383 (default).

**COMMITTED callers:** same methods, **no** `forNewReceiptPost` parameter exists (HEAD helper signature has four args only).

**`LockLatestForUpdateAsync` callers (COMMITTED=WORKTREE unless noted):** `PoOrderService.SaveExistingAsync` 1049; `LockRequestedPoAsync` 1466; attachment `LockLatestPoForAttachmentAsync` 703; GR helper COMMITTED ~4210 / WORKTREE 4221. **Do not rewrite the repository helper globally without tracing every caller.**

### B. PR / PO document and approval engine (COMMITTED = WORKTREE)

| Flow | Entry point | Called service | Exact LOCAL file:line | Label | SQL tx | Lock order | Policy/instance resource | Mutation | Denied-post invariants | Classification | Permission | Gate 1 risk / evidence |
|------|-------------|----------------|-----------------------|-------|--------|------------|--------------------------|----------|------------------------|----------------|------------|------------------------|
| PR New | `PoPr.razor` | `PoPrService.SaveNewAsync` | 356–534; `CreatedDate = DateTime.UtcNow` 454–472 | COMMITTED | yes | numbering; **no** approval lock | none | NEW PR header/lines | N/A | write NEW PR | ADD | Timestamp D4. No auto-submit |
| PR Edit | UI | `UpdateAsync` | 896–1055; PO refs 936–947; PR lock 949; instances 976–984 | COMMITTED | yes | live PO headers → PR `LockForUpdate` → instances → material-edit assert | instance | material if fingerprint changes | fail closed before save | write | EDIT | leftover `_approval is not null` at 976 despite required ctor |
| PR Copy | UI | `CopyAsync` | 1109+ | COMMITTED | read then in-memory draft | none | none | draft only | N/A | read/clone | ADD | Copy is not submit |
| PR Cancel | UI | `CancelAsync` | 1238+; instances 1326; `ClosePendingOnDocumentCancelAsync` 1371 | COMMITTED | yes | PO refs → PR lock → instances | instance | close pending | N/A | write | CANCEL | — |
| PR Delete | UI | `DeleteAsync` | 1401+; instances 1479; close pending 1528 | COMMITTED | yes | similar | instance | delete + close pending | N/A | write | DELETE | — |
| PR from WO | production | `CreateFromWorkOrderMaterialAsync` | 536–862; `CreatedDate = now` 849 | COMMITTED | WO locks | **no auto-submit** | none | NEW PR | N/A | write NEW | WO/PR | Confirm `now` kind at 849 (same UtcNow pattern as SaveNew) |
| PR Submit | UI | `ProcurementApprovalService.SubmitAsync` | PR arm 264–287; `LockPrAsync` 266; `SubmitLockedAsync` 273–280 | WORKTREE 0B | 262–287 | PR header → status check 1070 → policy applock 1076 → instance | `PO_APPR_POLICY\|COMPANY\|PR` + instance | snapshot PolicyId/Version/fingerprint/assignees | N/A | write instance | SUBMIT | WORKTREE rejects cancelled/non-NEW/OPEN (`SubmitDocumentStatusError` 1538–1551). SQL races **NOT RUN** |
| PO Submit | UI | `SubmitAsync` PO arm | WORKTREE 289–341; COMMITTED `9fb40eed` locked requested DocRev only | WORKTREE 0B | 262–341 | `PO_REV` 291 → max RelNo 299 → stale reject 306 → `LockPoAsync` latest 312 → recheck 319 → SubmitLocked | policy + instance for **latest** RelNo | same | N/A | write instance | SUBMIT | WORKTREE C3 mitigated on Submit path. `LockLatestForUpdateAsync` still non-atomic. GR still unguarded (C2). Isolated SQL **NOT RUN** |
| Decide Approve/Reject/Return | inbox | `DecideAsync` | 305–464; `LockDocumentThenInstanceAsync` 332–333 | COMMITTED | 330–463 | lookup key → tenant/branch → `PO_REV` if PO (1444) → exact header → instance + RowVersion (1469–1473) | instance; **no policy lock** | decision/event | N/A | write | APPROVE/REJECT | Better than instance-first. SQL vs revise **NOT RUN**. Missing document → fail closed |
| Withdraw | UI | `WithdrawAsync` | 467–510; `LockDocumentThenInstanceAsync` 480 | COMMITTED | 478–509 | same as Decide | instance | status Withdrawn | N/A | write | submitter | same |
| Reassign | settings/inbox | `ReassignAsync` | 553–; `LockInstanceByIdAsync` 576; company+branch 577–579; RowVersion 584–588 | WORKTREE 0B | 574–commit | **instance only** (no PR/PO read) | instance | assignee UserId | N/A | write | settings EDIT | WORKTREE H4 mitigated. Still instance-only. SQL **NOT RUN** |
| Policy save | `PoApprovalSettings` | `SavePolicyAsync` | 109–; applock 134; Version/RowVersion compare 142–151 | WORKTREE 0B | 132–commit | **policy applock** then compare tokens if Version&gt;0 or RowVersion present | `PO_APPR_POLICY\|COMPANY\|DOCTYPE` | unpublish + insert Version+1 | N/A | write policy+event | settings EDIT | WORKTREE D9: `ToDto` 675–682 sends Version/RowVersion. First-enable Version=0 still last-writer-wins under applock. Settings help/layout **not** rewritten |
| PR picker for PO | PO UI | `SearchPrForPoAsync` | `PoOrderService.cs` 461–535; chunk 493–516; `FilterConvertiblePrNosReadOnlyAsync` | COMMITTED | no write | none | read-only `AssertGateAsync(..., persistCutover: false)` 961–969 | none intended | N/A | **read** | ACCESS | Partial improvement vs Take(100). Still per-PR N+1. **Do not rewrite picker in this task** |
| PR remaining lines | PO UI | `GetPrRemainingLinesAsync` | 541–577; `EvaluatePrConvertibleReadOnlyAsync` 569 | COMMITTED | no write | none | read-only | none; remaining-qty formula **unchanged** | N/A | advisory read | ACCESS | PO Save remains authoritative |
| PO New / from PR | UI | `SaveNewAsync` | 582+; consumption 627; `LockConsumptionAsync` 1765–1792; policy lock 1801 | COMMITTED | yes | PR headers → PR instances → PR details → CJ; then policy if PR keys | PR policy + instances | PO + existing PR remaining calc | fail before consumption | write | ADD | Required guard. Leftover `if (_approval is not null)` at 1773 |
| PO Edit | UI | `SaveExistingAsync` revise:false | via 750; `PO_REV` 1040; latest lock 1049; recheck 1059 | COMMITTED | yes | PO_REV → LockLatest → recheck max → instances | instance | write same RelNo | fail material-edit | write | EDIT | Latest check **present** on save. Submit path **lacks** it |
| PO Revise | UI | `ReviseAsync` 773 → SaveExisting revise:true | 1040–1065; `StampAttachRevNoAsync` 1234 / 2661–2666 | COMMITTED | yes | same as edit + new RelNo | instance | new RelNo; **all** attachment RevNo restamped | N/A | write / new RelNo | EDIT | Restamp is **not** a revision snapshot (H3) |
| PO Copy | UI | `CopyAsync` | 754–771 `CloneDraft(..., resetPrAndReceive: true)` | COMMITTED | none | none | none | in-memory draft | N/A | read/clone | ADD | Not a commit |
| Force Close | list | `ForceCloseAsync` 969 → `ChangeStatusAsync` 1322 | `LockRequestedPoAsync` 1332; policy 1360; `AssertPoCloseableAsync` 1376 | COMMITTED | yes | PO_REV (1450) → requested or latest → recheck 1474 → policy/instance | PO policy + instance | CLOSED | fail before status | write | CLOSE | Remaining-balance formula **unchanged** |
| Reopen | list | `ReopenAsync` 992 | `ChangeStatusAsync` forceClose:false | COMMITTED | yes | PO_REV + header | **no approval assert** | write | N/A | write | REOPEN | Documented; no extra gate proposed |
| PO Cancel/Delete | UI | `CancelAsync` 778; `DeleteAsync` 872 | `LockRequestedPoAsync` + consumption + close pending | COMMITTED | yes | PO_REV + header | instance close pending | write | N/A | write | CANCEL/DELETE | — |
| PO attachment | `MapPoOrderAttachmentEndpoints` (`Program.cs` 224) | `PoOrderAttachmentService` | `LockLatestPoForAttachmentAsync` 688–715 | COMMITTED service; WORKTREE freeze in approval | short tx | PO_REV → LockLatest → recheck | `AssertAttachmentMutationAsync` 926–948 | files+rows | N/A | write | attach | WORKTREE delete freeze includes Rejected/Returned/Withdrawn/Cancelled/Superseded. Restamp 2661–2666 still **not** a snapshot (D6) |
| PR attachment | `MapPoPrAttachmentEndpoints` (`Program.cs` 223) | `PoPrAttachmentService` | PR lock + mutation assert | COMMITTED | short tx | PR header | same freeze function | files+rows | N/A | write | attach | same |
| Temp attach promote | PO/PR save | `StampAttachRevNoAsync` 2661; `MoveDraftSafeAsync` 2669 | COMMITTED | after document commit | none extra | **no re-assert in stamp/move** | DocId/RevNo | N/A | write | — | Must not bypass freeze (D6 UNSIGNED) |
| Invoice post | UI | `PoInvoiceService.PostAsync` / `ValidateMatchOnPostAsync` | 1621–1636 net received ≤ 0 blocks non-CN | CLEAN / out of approval commit | invoice+PO locks | **no approval** | `InvoicedQty` | **blocked without RecvQty** | N/A | write | POST | **No new invoice gate.** Costing/tax **FROZEN** |
| CDN post | UI | `PoCdnService.PostAsync` | 1890+ | CLEAN | CDN lock; VR subpath for stock | **no RecvQty write in CDN service** | ReturnQty via VR | N/A | write | POST | **No extra CDN gate** |
| Self-billed | UI | `PoSbInvoiceService` | not re-traced line-by-line this pass | UNKNOWN beyond prior notes | n/a | n/a | no PO commitment | N/A | out of scope | — | No Gate 1 work |
| Background jobs | — | no `HostedService` in `ErpWeb.Core` (search this rewrite) | none found | COMMITTED search | — | — | **none found** | — | — | — | — |
| HTTP APIs | `Program.cs` 223–224 | attachment endpoints only | COMMITTED | not Post GR | via attachment services | files | N/A | write files | attach | Direct PostAsync still a server API on posting service |

---

## Latest-revision race schedule (required later — **NOT RUN**)

`LockLatestForUpdateAsync` is still non-atomic. **WORKTREE 0B** PO Submit now uses `PO_REV` + max recheck. GR/NG still does not on COMMITTED; DIRTY posting helper is UNREVIEWED WIP.

**Isolated SQL (2026-11-02 ~15:00 +08), catalog `erpweb_poapr_test_20261102`:** six `ProcurementApprovalSqlServerConcurrencyTests` **passed** (two policy publishers; concurrent submit; concurrent approve vs reject; disable vs pending convert; cross-company inbox; filtered unique preflight). That does **not** cover R1–R6 or GR/NG posting.

| # | Connection A | Connection B | Required outcome | Evidence status |
|---|--------------|--------------|------------------|-----------------|
| R1 | GR post reads max=rev1, pauses before lock | PO revise commits rev2 | A must not post against rev1; **no** RecvQty/stock | **NOT RUN** (no GR SQL tests in current harness) |
| R2 | PO revise holds `PO_REV` | GR waits then rechecks max | GR posts only latest approved or fails cleanly | **NOT RUN** |
| R3 | GR draft PoRelNo=1 | rev2 is latest | Post rejects stale line RelNo **and** approval of rev1 must not authorize rev2 | **NOT RUN** SQL. WORKTREE unit test `PO_submit_rejects_stale_revision_and_accepts_latest` covers Submit only, not GR |
| R4 | GR vs ForceClose/Cancel latest | serialize on PO set lock | one winner; no partial batch | **NOT RUN** |
| R5 | Multi-PO GR; one PO revises | sorted PoNo locks | all-or-nothing batch | **NOT RUN** |
| R6 | Latest approved; older rev also approved | GR for old RelNo | reject even if old instance APPROVED | **NOT RUN** |

---

## Open findings that remain blocking

| ID | Finding | Local evidence | Status |
|----|---------|----------------|--------|
| C1 | Required-guard constructors vs committed test factories | COMMITTED factories omit guard. **WORKTREE:** `TestDisabledProcurementApprovalGuard` in test project; `ServiceRegistrationTests` asserts production guard is `ProcurementApprovalService` (83–94). Clean `9fb40eed` still would not compile tests | WORKTREE test mismatch **addressed**. Clean-HEAD compile still OPEN if tests are reset to commit |
| C2 | Committed GR/NG posting has no approval | HEAD ctor/helper have no guard. WORKTREE WIP uses shared helper flag | **OPEN CRITICAL.** Dirty path **not** approved. Blocks Gate 1 |
| C3 | PO Submit not covered by `PO_REV` / latest check | COMMITTED `SubmitAsync` locked requested DocRev only. **WORKTREE 0B** 291–324 | WORKTREE **mitigated** (SQLite). Isolated SQL vs revise **NOT RUN**. GR latest-rev still OPEN (C2) |
| H1 | `documentStatus` unused | COMMITTED unused. **WORKTREE** `SubmitDocumentStatusError` 1070–1074 / 1538–1561 (PR/PO NEW or OPEN only) | WORKTREE **mitigated** (SQLite). SQL **NOT RUN** |
| H2 | Policy vs GR; DTO Version unused | `AssertGateAsync` still loads policy with no lock. **WORKTREE** `SavePolicyAsync` 142–151 compares tokens; `ToDto` 675–682 sends them | Policy optimistic concurrency **mitigated** in WORKTREE. Enable-vs-GR still OPEN (C2). First-enable Version=0 still last-writer-wins |
| H3 | Attachment freeze misses historical submitted cycles | **WORKTREE** delete freeze via `IsSubmittedAttachmentStatus` 1567+. Restamp 2661–2666 unchanged | Delete freeze **mitigated**. Snapshot/restamp still OPEN D6. Add-while-Returned still allowed |
| H4 | Reassign company-only, no RowVersion | **WORKTREE** 577–588 company+branch + RowVersion | WORKTREE **mitigated** (SQLite) |
| H5 | Cutover timestamps mixed kinds | PR `CreatedDate = DateTime.UtcNow` 454–472; PO `_dates.Now` 689 (company-local `CurrentDateService.cs` 33–38); policy `CutoverAtUtc = DateTime.UtcNow`. Null Created → fail “not submitted”. **No PoDate/CreateDt fallback.** Tenant TZ / old-row kinds **UNKNOWN** | **OPEN HIGH.** Unsigned D4. No 0B change |
| M1 | Picker N+1 | 493–516 / 961–969 | OPEN MEDIUM; **no picker rewrite this task** |
| M2 | Public Core no-op guard + SQL harness | Core `DisabledProcurementApprovalGuard` still public (dirty posting fallback). Tests use `TestDisabledProcurementApprovalGuard`. Production DI still real service. DIRTY SQL fixture skips unless env | Test stub **moved**. Posting null fallback still OPEN (C2). SQL env unset this pass |
| Repair | Public PostAsync | `CostingRepairAdapters.cs` 293–299 | OPEN D5 |
| Catalog | Live tenant inventory | **Not queried this rewrite** (read-write risk). | UNKNOWN — NOT VERIFIED |

---

EVIDENCE REWRITE COMPLETE (if criteria satisfied); GATE 0 NOT CERTIFIED; GATES 1–3 BLOCKED PENDING SIGNED DECISIONS, BUILD, AND ISOLATED SQL CONCURRENCY TESTS.
