# PRODUCTIONV2 — QA OPERATIONAL COMPLETION
## AI Coding Agent implementation plan v4.0 — CURRENT `productionv2`, **NO COSTING / NO STOCK-WRITER CHANGES**

**Plan date:** 2026-11-10  
**Verified remote:** `https://github.com/mokth/net10projectTemplate`, branch **`productionv2`**, HEAD `584411e317ac6990ed8a5f0886207779d821deaf` (verified for this review).  
**User-confirmed local branch:** **`productionv2`**. Do not contradict this or tell the user to change to `productionv2`. The coding agent must inspect its own exact local working tree, including uncommitted changes, before implementation; remote evidence is a reference, **not** a substitute for local evidence.  
**Solution:** `ErpWeb.slnx`; .NET `net10.0`; UI package `DevExpress.Blazor` **26.1.4** at verified remote HEAD.  
**Decision:** **Implementation-ready phase specification, subject to Gate 0 and individual gates. Gate 0 is the only action authorized without fresh local/SQL Server evidence.** Do not assert release readiness or 10/10 until gates actually pass.  
**Supersedes:** v1, v2, v3 QA completion plans. This is the standalone implementation brief, not an instruction to merge old plans blindly.

> **Product outcome:** deliver a professional, optional, end-user-friendly **QA Inspection, Approval, Corrective Action, Traceability and Inquiry** module for Malaysian SME ERP users. Use the existing QA case/inspection/audit structures and existing source transactions. **Do not implement new Inventory/Production quantity posting, CostLayer, valuation or posting authorization functionality.** The stock-integrated part of QA is a separate future release.

---

# 1. HARD SCOPE BOUNDARY — THE MOST IMPORTANT RULE

## 1.1 Allowed now

1. Quality case/source selection, accurate source identity, draft/edit/cancel/inspect/review/reject/approve/close and QA-only audit.
2. QA-only inspection results, dispositions (ACCEPT/REJECT/REWORK/HOLD), corrective-action assignments, due dates, follow-ups and reinspection **as QA records**.
3. Optional before-GRN inspection records, post-GRN inspection records, production output inspection records, WIP/FG staging inspection records and read-only warehouse pile inspection records; **not a new posting right**.
4. Professional Blazor/DevExpress Workbench, fast bulk-inspection entry, simple settings, guidance, inquiry, traceability, reports and authorized exports.
5. Strict tenant/branch/source checks, concurrency, QA quantity integrity and read-only navigation to pre-existing GRN, MR, WI, FG, stock-scrap, vendor-return and supplier credit-note pages.
6. QA-only schema *if strictly necessary* for append-only inspection attempts, corrective actions, document link evidence or attachments; **requires Gate 0 DBA approval, reviewed SQL upgrade/backout and tenant validation**.
7. QA-only test and read-only regression evidence of pre-existing Inventory/Production/Procurement behavior.

## 1.2 Absolutely forbidden in THIS implementation

- **No changes** to FIFO/WAC/Standard costing, `CostLayer`, valuation, absorbed production cost, inventory value or historical cost replay.
- **No changes** to stock/WIP quantity writers, balance maintenance, reserved/available stock calculation, inventory ledger/history, stock status `QCHOLD`, production lot quantity changes, conversion rules or month-end accounting rules.
- **No changes** to posting/rollback/reversal/replay coordinators, POST/ROLLBACK call sites, future-date/period gates or any Inventory/Production transaction status logic.
- **No activating or newly enabling** any of the five frozen QA posting-affecting gates: Incoming GRN, Final FG, WIP Logical Hold, Physical Stock Hold, Non-Good Production.
- **No changes** to the behaviors of `QaReceiptAuthorizationService`, `QaFgAuthorizationService`, `QaNonGoodAuthorizationService`, `QaPhysicalHoldAuthorizationService` or `QaLogicalHoldAuthorizationService`; no altering their allocation/restore or reservation methods in this work.
- No automatic conversion from QA case to posted GRN/FG/WI/scrap/return/CN. Do **not** call an existing Post method from QA.
- No broad generic service/migration/permission refactor; do not rewrite source-document workflows to accommodate QA.
- No executing SQL against customer tenants, changing feature flags, Git checkout/reset/merge/rebase/stash, or touching other worktrees without separate authorization.

**Why the line is strict:** an approval gate or hold can allow/block a stock transaction, even if no costing formula is edited. Since costing/posting is in development, **no-costing** must also mean **no changes to stock-affecting QA authorization**. Historical ON policies and allocations must still work exactly as before. Do not globally bypass or clear them.

## 1.3 Precise meaning of “complete”

**Deliverable = `QA_RECORD_AND_FOLLOW_UP_COMPLETE`**: inspection, QA decisions, review, correction tracking, document tracing, reporting and operator UI. **Not** `QA_INVENTORY_POSTING_COMPLETE`. Every UI, release note and KPI must respect this distinction. Physical quarantine, automatic WIP-to-scrap/rework, compulsory GRN/FG quality gates and valued non-good lots are explicitly **DEFERRED** until the costing/stock layer stabilizes and a separate plan is authorized.

---

# 2. CURRENT REPOSITORY: VERIFIED BASELINE AND DEFECT REGISTER

Inspect the **actual local files again in Gate 0**. Below is the verified remote `productionv2` behavior at the pinned commit; local work can differ.

## 2.1 Files that ALREADY exist — do not reinvent them

**Core QA:**
- `ErpWeb.Core/Quality/QaCaseService.cs`, `IQaCaseService.cs`, `QaDtos.cs`, `QaCodes.cs`.
- `QaSourceResolver.cs`, `QaSourcePrefill.cs`, `QaDestinationComposer.cs`, `QaPolicyService.cs`, `QaSchemaReadiness.cs`, `QaReadinessMessages.cs`.
- `QaReceiptAuthorizationService.cs`, `QaFgAuthorizationService.cs`, `QaNonGoodAuthorizationService.cs`, `QaLogicalHoldAuthorizationService.cs`, `QaPhysicalHoldAuthorizationService.cs` **READ ONLY in this plan**.

**UI:**
- `ErpWeb.UI/Quality/QaWorkbench.razor[.cs,.css]`.
- `ErpWeb.UI/Quality/QaCaseEntry.razor[.cs,.css]`.
- `ErpWeb.UI/Quality/QaSettings.razor[.cs,.css]`, `QaInquiry.razor[.cs,.css]`.
- `QaHowToUsePopup.razor`, `QaRecordOnlyBanner.razor`.
- Existing **Request QA** navigation: `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor.cs`, `IvMiscReceipt.razor.cs`, `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor.cs`, `PrFinishedGoodReceiptEntry.razor.cs` (presentation/navigation changes only).

**DB/test/scripts:**
- `ErpWeb.Model/Entities/Quality/QaFoundation.cs` and `ErpWeb.Model/Configurations/Quality/QaFoundationConfiguration.cs`.
- `ErpWeb.Tests/Quality/` already contains **11 test classes**: preserve and extend, do not replace.
- `scripts/create-qa-foundation.sql`, `create-qa-receipt-grant.sql`, `create-qa-fg-grant.sql`, `create-qa-logical-hold.sql`, `create-qa-physical-hold.sql`, `create-qa-nongood.sql`, `init-qa-menu.sql`.
- `plans/qa_safety_fix_F0_repo_and_schema_matrix.md` through `F4_professional_ui.md` and `plans/qa_closeout_gate5.md` contain historical evidence, not current acceptance evidence.

## 2.2 Findings the implementation MUST address

| ID | Priority | Verified problem / risk | Required resolution |
|---|---|---|---|
| Q01 | **Critical** | `IvGoodsReceipt.OnRequestQa()` and `IvMiscReceipt.OnRequestQa()` pass displayed **BatchNo** in the `sourceHeaderId` query value for posted receipts, while `QaSourceResolver.RevalidatePostedOrPileAsync()` looks up `IvTrxBatch.Id`; these are different keys. | Resolve by authoritative exact source lookup. Keep display BatchNo and DB Id distinct. No guessing or treating `BatchNo == Id`. |
| Q02 | **High** | `QaSourceResolver.RevalidateOutputAsync()` returns success when `SourceHeaderId` is missing; posted GRN `RevalidatePostedOrPileAsync()` can also return success for invalid/missing ID outside the stock-source exception. | Require real IDs for linked posted/production sources, while preserving an **explicitly unlinked manual pre-GRN delivery** case. |
| Q03 | **High** | Source quantity comparison is per QA row; multiple repeated references can each be individually within the source max but exceed it in total. | Group by canonical source identity + source line + relevant UOM; compare aggregate QA target qty/inspected qty against approved live source maximum. Handle case-level vs all-cases business rule separately (do not silently impose global inspection exclusivity). |
| Q04 | **High** | `QaCaseService.Stale` returns false if incoming RowVersion is missing/empty; QA policy edit has a similar optional-token pattern. | SQL Server QA edits/reviews must require valid expected versions; handle EF concurrency exception cleanly; SQLite simulation is not proof. Do not alter source posting rowversions. |
| Q05 | **High** | `ApproveAsync()` checks only current header `InspectedBy`. `SaveInspectionAsync()` overwrites that header; other contributing inspectors can become hidden. | Reviewer separation must evaluate **every actual contributing inspector** (including append-only attempt history when enabled); SQL Server check in authoritative transaction. |
| Q06 | **High** | `SaveInspectionAsync()` permits `InspectedQty < line.Qty`, but `SubmitForReviewAsync()` only checks whether each line has any inspection. | Define exact sample/decision/remaining semantics; do not approve or close unresolved quantities as fully accepted. If sample-only release rules are not approved, label it partial and block full disposition signoff. |
| Q07 | **High** | `QaCaseEntry.CanInspect` does not require Edit; `CanApprove` gates the surrounding **Reject** button although server uses distinct `PermissionCodes.Reject`. | Align independent server and UI permissions for Inspect/Approve/Reject/Close/Cancel. No client-only security. |
| Q08 | **High** | `ApproveAsync()` may still place **real WIP/physical holds** for existing policies that were ON before new gate activation was frozen. | Gate 0 checks historical ON tenants/obligations; never claim record-only applies to those tenants; do not alter historical behavior or let a new QA release inadvertently trigger reservations. |
| Q09 | **Medium** | `SearchSourcesAsync(INVENTORY_STOCK)` actually searches posted **Miscellaneous Receipt** lines; it is not all on-hand stock. | Either relabel existing option as `Posted MR` or add a separate **read-only** `IvBalLoc` picker with a distinct identity contract; no fake stock reservation. |
| Q10 | **Medium** | `QaCaseEntry.TryLoadSourceLinesAsync()` searches up to 200 rows and applies each match via `SelectSource()`, overwriting case header; the full exact source is not guaranteed on first page. | Implement exact source ID/paged fetch and atomic multi-line selection restricted to one source owner. |
| Q11 | **Medium** | `GetInquiryAsync()` materializes all filtered cases and nested graph before paging; current “FirstPassYield” is merely approved/no-reject cases over cases reaching inspection. | SQL-scoped counts and bounded page; truthful KPI name/denominator. Real first-pass yield requires immutable inspection-attempt data. |
| Q12 | **Medium** | `QaDestinationComposer` has `SUGGESTED` and `LINKED_UNVERIFIED` navigation; those are not sealed receipt/issue allocations. Some wording suggests case approval authorizes remaining goods to post. | Fix labels: recorded inspection, suggested manual action, linked document unverified, sealed execution only if proven by existing allocation; never synthesize `EXECUTED`. |
| Q13 | **Medium** | Single `QaInspection` row per case line and overwritten values cannot prove earlier attempts or complete reinspection lineage. | Add **optional, separately approved QA-only append-only attempt table** and keep latest summary; do not alter current stock-owned source identity or fabricate another delivery. |
| Q14 | **Medium** | Current QA case entry says attachments not enabled; daily-use follow-up/actions are not structured. | Add QA-only corrective action tasks; add attachments only by proven existing secure infrastructure and reviewed QA-only metadata. |
| Q15 | **Medium** | Some paths use raw technical source/status codes and unnecessarily long or repetitive workflows for ordinary users. | Single clear next action, aligned compact fields, fast inspection grid, responsive tablet/browser UI, existing DevExpress standards and plain-language descriptions. |
| Q16 | **High** | `QaSchemaReadiness` supports absent/partial/probe-failed classification; **all-absent is provenance unknown**, not “never installed”. A historical ON tenant can have retained obligations. | Read-only per-tenant readiness/obligation matrix; no auto bootstrap, destructive schema repair or blanket turn-off. |
| Q17 | **Medium** | Pre-GRN incoming cases may legitimately use delivery note without PO. Making every case require a PO would break supported manual inspections. | Preserve manual/unlinked incoming workflow with clear badge and no GRN authorization implication. |
| Q18 | **Medium** | Current QA-specific tests primarily cover service contracts and SQLite behavior; real UI and SQL Server concurrency are not established by old `plans/` closeout notes. | Run actual final solution build, SQL Server two-session tests and browser UAT; report failures and skips rather than assert 100%. |

**Important:** These are source-reviewed findings, **not** proof that your local, possibly modified `productionv2` files still have the same defects. Gate 0 must mark each `CONFIRMED`, `ALREADY_FIXED`, `DIFFERENT_CONTRACT` or `NOT_VERIFIED` before code changes.

---

# 3. GATE 0 — MANDATORY LOCAL REPOSITORY RECONCILIATION (AUDIT ONLY)

**This is NOT a “switch to productionv2” instruction. User already confirmed the local branch is productionv2.** The AI agent must show what it *actually* opened and reconcile the proposed paths with the current local tree. The remote pinned commit is a comparison baseline, not the required local HEAD.

## 3.1 Read-only preflight on the ACTUAL local directory

```powershell
# Run from the exact folder/worktree opened by Cursor/Codex.
$root = (git rev-parse --show-toplevel).Trim()
Write-Host "WORKTREE=$root"
git remote get-url origin
git branch --show-current
git rev-parse HEAD
git status -sb
git diff --name-status
git diff --cached --name-status
git worktree list --porcelain
git rev-list --left-right --count HEAD...refs/remotes/origin/productionv2
Test-Path "$root\ErpWeb.slnx"
Test-Path "$root\ErpWeb.Core\Quality\QaCaseService.cs"
Test-Path "$root\ErpWeb.UI\Quality\QaCaseEntry.razor"
Test-Path "$root\ErpWeb.Tests\Quality\QaFoundationTests.cs"
```

- Verify origin identifies `mokth/net10projectTemplate` and the agent is working in the **user-intended** `productionv2` directory. A repo can contain several worktrees; report, do not silently choose or switch.
- **If HEAD is ahead/behind** remote, do NOT call it a different repository. Inspect local commits and diffs; local current code wins for the implementation plan. Do not `git pull`, `checkout`, `reset`, `stash`, `fetch` or `merge` without permission. A stale local tracking ref is not proof of a stale live remote.
- Check if the plan file itself was delivered to the agent. A ChatGPT download does **not** automatically appear as a committed `plans/` file; do not report it “missing from repo” as a codebase mismatch.
- If the local QA classes differ, report the **specific changed class/method/signature**, update this plan to local reality and only then continue.
- No unrelated costing/worktree modifications may be overwritten. Do not include previously uncommitted inventory or production changes in QA commits.
- A helper read-only script is available separately: `qa_productionv2_repo_preflight.ps1`; the script is optional, and its branch checks do **not** replace comparing the local source itself.

## 3.2 Evidence files and stop decision

Produce `plans/qa_gate0_20261110_current_local_evidence.md` (new evidence file; **not** an assumed existing repository file) with:

1. Repo origin/root/branch/HEAD/upstream/diffs/worktree; build SDK + DevExpress version.
2. **File and method map** for all Q01–Q18 with local line references and results.
3. QA source identity matrix: display doc no vs actual database PK, source revision, per-line and per-case uniqueness/quantity rules, manual sources, stale/deleted/rolled-back source rules.
4. UI → `IQaCaseService` → `QaCaseService` → EF model/SQL → read-only source flows; document separate existing POST/rollback interfaces as **read-only dependencies only**.
5. QA schema and tenant rollout matrix, including historical policies ON, active holds and allocations, permissions, company/branch boundaries, one normal DB identity with SQL catalog access.
6. Existing QA tests and baseline Inventory/Production regressions with actual pass/fail/skip counts; **not** prior markdown claims.
7. Exact proposed QA-only file changes + prohibited-file baseline, new-schema request only if needed.

**Gate 0 statuses:** `PASS_FOR_QA_ONLY_WORK`, `NEEDS_PLAN_RECONCILIATION`, `HISTORICAL_ON_TENANT_BLOCKED`, `SQL_SCHEMA_UNVERIFIED`, `CONFLICTING_WORKTREE`, or `STOP_WRONG_CHECKOUT`. **Stop after Gate 0 and request review of evidence.** Successful Git branch name alone is not approval to implement.

---

# 4. FUNCTIONAL BUSINESS CONTRACT (SME OPERATOR VIEW)

## 4.1 Intuitive case workflow

**Create:** From Quality Workbench or source document (GRN/PO, stock, Daily Production, WIP lot, FG staging) → choose source → one click to add relevant lines → Save.  
**Inspect:** Open case → enter result and accept/reject/rework/hold quantities **in a single line grid** (optionally Save All) → submit for review.  
**Review:** Authorized independent reviewer approves, rejects or returns for correction with plain-language remarks.  
**Follow up:** Record actions and owner/due date (supplier contact, correction/reinspection, manual source document link).  
**Close:** Only after all QA-only decisions and required follow-ups are resolved; closing cannot override active legacy holds.  
**Inquiry:** Find case by source/doc/item/supplier/WO/status/inspector/owner/date; display what was inspected and what action remains.  

Use existing status codes as persistence contract unless an explicitly approved QA-only extension is needed. Existing statuses: `DRAFT`, `SUBMITTED`, `IN_INSPECTION`, `PENDING_REVIEW`, `APPROVED`, `REJECTED`, `CLOSED`, `CANCELLED`. Do not add a parallel status machine. Rework/follow-up should be **child action statuses**, not a second stock ledger.

## 4.2 Source-family contract

| Source displayed to end user | Canonical identity | Current allowed QA behavior | Explicit limitation |
|---|---|---|---|
| Incoming delivery / PO before GRN | Company+branch+PO+release+line **or** clearly manual unlinked supplier delivery | Inspect before receipt, record accepted/rejected decisions and supplier follow-up | Does **not** allow or create stock receipt; no physical vendor return before stock receipt |
| Posted GRN | `IvTrxBatch.Id` + `IvTrxBatchDetail.Id` + revision; displayed `BatchNo` kept separately | Inspect posted receipt, link back to GRN | Does not directly quarantine, return or change stock |
| Posted MR | Same batch and detail key as source; label “Posted miscellaneous receipt” | Inspect a posted MR line | Not equivalent to “all warehouse stock” |
| Warehouse stock, if enabled later in QA-only phase | Read-only `IvBalLoc.Id` plus full item/WH/Loc/Lot/IStatus identity | Create dated inspection *observation* of current source pile | Quantity may change after inspection; no hold/reservation or status mutation |
| Daily Production good / non-good | Real `ProductionOutput.Uid`, document revision and item/UOM | Inspect good outputs or recorded scrap/reject/hold counters | Non-good counters are **not valued stock lots** |
| WIP process lot | `ProductionBalLot.Uid`, stage `PROCESS_WIP`, WO/item/UOM | Inspect WIP and record recommended rework/hold decision | Does not move/reserve WIP or modify production posting |
| FG staging lot | `ProductionBalLot.Uid`, stage `FG_STAGING`, WO/item/UOM | Inspect staged FG and record decision | Does not allow/post FG receipt |

**Exact identity rules:** A linked case has exactly one authoritative source owner and may contain multiple **compatible lines**. Do not mix arbitrary POs, batch IDs, WI outputs or WIP lots under a single case header. All linked source identities are revalidated at save, inspect and final review. Scope every lookup by authenticated company+branch. A manually entered delivery note is informative text, never a trusted GRN transaction key.

## 4.3 QA quantity, sampling and disposition rules

For every QA case line, explicitly display and validate:

```
CaseQty >= 0
0 <= InspectedQty <= CaseQty
DecisionQty = AcceptQty + RejectQty + ReworkQty + HoldQty
DecisionQty = InspectedQty     (for inspected subset)
UninspectedQty = CaseQty - InspectedQty
```

- Never silently mark `UninspectedQty` as accepted, rejected or resolved.
- For full case signoff, all lines must have a documented review outcome and **no unclassified remainder**. If customer policy allows statistical sampling, require an explicit separate QA sampling rule/approval and label that quantity accurately; **do not assume it already exists**.
- Bound each line and grouped lines against canonical live source **with UOM normalization from existing approved conversion data only**; QA may *read* conversion, not change it. Do not compare unlike UOMs or modify master UOM.
- Repeated QA cases against a source may be legitimate retests; do not globally “consume” inspection source qty as if QA owns stock. Instead prevent **within-case overstatement**, identify other open inspections and avoid claiming a unique stock entitlement.
- Supplier rejected **before** receipt is not inventory. WIP hold is a QA decision here, not a physical/available quantity change. Non-good counters do not become inventory.
- Source mutation or rollback after inspection must be surfaced as `SOURCE_CHANGED` / `SOURCE_REVERSED` evidence and reviewed; never automatically rewrite the QA record or alter source rollback rules.

## 4.4 Roles and audit

Reuse `MenuCodes.QualityWorkbench`, `QualitySettings`, `QualityInquiry` and existing `PermissionCodes`: `ACCESS`, `ADD`, `EDIT`, `DELETE`, `SUBMIT`, `APPROVE`, `REJECT`, `EXPORT` where already applicable. **Do not invent a new permissions framework.** Inspect exact current server checks before mapping UI buttons.

- Clerk: add/edit draft and submit (as permitted).
- Inspector: inspect submitted/in-inspection cases with server-side Edit rights.
- Reviewer: independently Approve or Reject using respective rights, separated from every contributing inspector when policy requires it.
- QA supervisor: close/cancel/release *QA-only* actions using existing permission semantics; **not** physical stock hold release by default.
- Settings: admins with current Settings Edit permission; never allow activation of frozen stock-affecting policies.
- Each create/edit/inspection/review/action assignment/close/reopen/cancellation is actor/time/old-to-new auditable; use existing audit conventions and add append-only QA-only action history where needed.
- No cross-company/branch data leakage, even via URL-manipulated case IDs, source ID, attachment ID, linked target document or export.

---

# 5. IMPLEMENTATION PLAN — PHASES, FILES, ACCEPTANCE AND STOP GATES

## PHASE 1 — P0: Correct case/source/permission/concurrency foundation

**Objective:** make the existing QA workflow trustworthy before UI redesign. **No new functionality that changes posting.**

**Primary existing files:** `QaCaseService.cs`, `QaSourceResolver.cs`, `QaSourcePrefill.cs`, `QaDtos.cs`, `QaCodes.cs`, `QaCaseEntry.razor.cs`; matching tests in `ErpWeb.Tests/Quality/`.

1. **Source key correction:** Source selectors must return the real entity PK plus displayed doc number; fix exact posted GRN/MR selection and URL prefill, including `BatchNo != Id`, partial/multiple detail rows and revision. Prefer a QA-only exact-id resolver; source transaction **navigation only** adjustments where essential.
2. **Source validation:** Reject missing/stale/foreign source IDs for linked posted GRN/MR/WI/WIP/FG; preserve explicit manual/unlinked incoming delivery. Enforce correct statuses, item, lot, warehouse, branch, UOM and source revision. Handle canceled PO and reversed/deleted receipts correctly.
3. **Quantity integrity:** aggregate repeated line reference quantities; block duplicate/mixed source headers; validate inspected/disposition sum, UninspectedQty and review completeness. Do not invent stock reservation against source.
4. **Concurrency:** require and validate expected `RowVersion` on relevant QA mutations; catch `DbUpdateConcurrencyException`, return friendly “This QA case changed; reload” instead of partial write. Use existing QA transaction/lock pattern for final review transitions, revalidate **after acquiring** the applicable QA lock. Do not change Inventory/Production locks.
5. **Separation of duties:** verify reviewer against **every** recorded inspector, including future attempts; prevent duplicate/competing approvals and rejects. Preserve current historical policy semantics.
6. **Permission defects:** independent Reject UI visibility and server right, Edit requirement for inspection, correct close/cancel permission alignment. Prevent double click and stale edits.
7. **No misleading acceptance:** display “Inspection approved” rather than “GRN authorized / FG accepted for posting” unless an existing sealed grant genuinely proves it; do not grant one.
8. **Legacy policy fence:** prove pre-existing ON tenants are excluded from new record-only rollout; do not tamper with existing holds/grants/allocations and their rollback support.

**Tests:** source PK/BATCH mismatch, malicious source line injection, wrong branch/tenant, PO canceled, WI not found, stale revision, two case lines exceeding source qty together, sample pending, two inspectors/one reviewer, missing version, simultaneous approve/reject, double save, SQL deadlocks/retries, historical policy ON compatibility. Include SQL Server (two independent connections), not SQLite only.

**Exit gate P1:** every confirmed Q01–Q08 and Q17 addressed with source-backed tests; prohibited file diff = **zero**; no active policy switches modified. If correction requires a stock writer, mark **DEFERRED** and STOP that item.

## PHASE 2 — Operator-first QA interface (highest UX priority)

**Objective:** a normal SME user can create, inspect, submit and review quickly without remembering codes or repeatedly opening popups.

**Files:** existing `ErpWeb.UI/Quality/{QaWorkbench,QaCaseEntry,QaSettings,QaInquiry}.razor[.cs,.css]`, `QaHowToUsePopup`, `QaRecordOnlyBanner`. Minimal source-page Request QA navigation only when P1 proves correct.

**Standard to copy:** current repository's `erp-ui-core`, `erp-transaction-ui-standard`, `production-transaction-ui`, `inventory-transaction-ui` where applicable, `erp-inline-search-standard`, `erp-large-data-smart-lookup-standard`, `erp-grid-row-actions-left`, `erp-entry-footer-standard`, `devexpress-dxgrid-column-readability`, `devexpress-blazor-editor-binding-safety`, `devexpress-popup-action-safety` skills. Verify their *live* referenced pages on local `productionv2`, not old skill branch prose.

- **Workbench:** compact header, intuitive `New QA`, inline search, source/status/date/owner filters, saved filters if established patterns permit, left-side per-row actions, pending review/overdue badges, server pagination, meaningful empty state.
- **Case entry:** simple **Source → Inspect → Review → Follow-up** progression. Auto-fill safe source metadata. Display item description, lot, UOM, ordered/source qty, inspection qty, accepted/reject/rework/hold and pending qty side-by-side; small readable inputs, professional grid widths/alignment.
- **Bulk daily entry:** one grid for multiple lines, keyboard-friendly Save All, quick “Pass full inspected amount” action with confirmation, accurate disposition totals, clear validation directly at row. Preserve backend case transitions; avoid mandatory excessive extra clicks.
- **Actions:** consistently placed `New`, `Save`, `Cancel Edit`, `Submit`, `Start/Record Inspection`, `Submit for Review`, `Approve`, `Reject`, `Close Case`, `Cancel Case`, `Delete Draft`; exact visible/enabled state matches backend permission and status. Do not make “Cancel Case” mean leaving the page.
- **Settings:** non-technical explanations, embedded “How QA works” help, clear record-only vs future stock integration, frozen switches visibly read-only, useful inspector/reviewer separation explanation; no fake Publish action if service does not support it.
- **Readability:** user labels instead of `INCOMING_PRE_GRN`, `FG_STAGING_LOT`, `QCHOLD`, `PO_CN`; preserve technical codes only in database; warnings explain what is and is not automated.
- **Responsive:** desktop/browser at 100% scaling, 10–13" tablets, mobile narrow layout. Good contrast, no horizontal clipped buttons, tab order and screen reader label. Use existing DevExpress 26.1.4 APIs; keep markup/state/css separated. For popups enforce `ShowFooter=true` with footer templates and correct `*Expression` bindings.

**UX UAT required:** operator creates a 10-line case and records mixed results in one working session; can find source without memorized ID; reviewer sees clear next action; buttons do not appear for denied roles; no accidental closing/posting. Record actual screenshot/browser evidence; compiling alone is not UI acceptance.

**Exit gate P2:** demonstrated end-user usability, no permissions mismatch, no duplicate QA posting control.

## PHASE 3 — Complete disposition follow-up and reinspection (QA-only)

**Objective:** QA approval should lead to actual **tracked human work**, without moving stock or claiming a transaction posted.

### 3.1 Corrective-action workflow

- For `REJECT`, `REWORK`, `HOLD`, create QA-only action rows with: `ActionType`, source QA case+line, decision qty, assigned user/department, reason, due date, status (`OPEN`, `IN_PROGRESS`, `DONE`, `CANCELLED`), remarks, completion evidence, approver/close actor and timestamps.
- No automatic WIP issue, FG receipt, SCRAP, Vendor Return, GRN or CN; the action is a **task**, not a stock movement.
- Support partial follow-up actions as multiple tasks but prohibit QA-only action quantities pretending to allocate or dispose more than the corresponding decision quantity, unless they are non-quantitative follow-ups.
- Require reason/audit for cancellation, reassignment and decision change; reject duplicate command execution with an idempotent QA action request ID if appropriate.
- A QA case cannot be labelled fully resolved while mandatory corrective actions remain open; preserve legacy active hold checks.

### 3.2 Reinspection and immutable attempts

- The existing unique one-inspection-per-case-line table is a **latest-summary snapshot**, not adequate reinspection history.
- Prefer a **new QA-only append-only `QaInspectionAttempt` table** only after DBA signs off. Persist case+line, attempt number, inspector, sample/decision qty, result, dispositions snapshot, time, notes/evidence and stable source reference. The current `QaInspection` may remain the latest outcome if compatibility requires it.
- Reinspection must link to the **same original delivery/output/lot identity** with a reason. Do not invent a second delivery or grant merely to bypass uniqueness.
- If QA-only schema approval is denied, do **not** fake reinspection by overwriting evidence; keep current single-attempt flow and explicitly mark reinspection as BLOCKED for release.

### 3.3 Document traceability, truthfully

- Read-only link to an *existing* GRN/FG/WI/SC/VR/Purchase CN: scope, document primary key, displayed number, line mapping, status/revision as of check, source company/branch and time checked.
- `SUGGESTED`: navigation idea only; `LINKED_UNVERIFIED`: user-related document exists but has no sealed QA-to-posting allocation; `EXECUTED`: use **existing proven** sealed allocation only (never upgrade status because user entered a doc number).
- If a posted source subsequently rolls back/is deleted/changes, link becomes stale and requires review. Do not prevent or initiate the rollback. Ensure Purchase CN-owned returns do not encourage posting a second vendor return.
- For pre-GRN supplier rejection, record supplier return/claim activity in QA only; **never** imply a physical vendor return or scrap of stock not yet received.

### 3.4 Secure evidence and attachments (optional feature gate)

- Reuse existing attachment storage/authentication patterns where suitable; review size, MIME, virus-scan availability, ACL, tenant isolation, retention and deletion before enabling QA uploads.
- Optional new `QaAction`, `QaInspectionAttempt`, `QaDocumentLink`, `QaAttachment` **QA-only** entities/migrations must be individually approved and must not alter CostLayer, balance/production, or posting tables. Provide SQL upgrade/backout and legacy tenant matrix.
- If file storage or QA-only DB schema cannot be approved, deliver functional corrective-action tracking without attachments, and label attachment support deferred rather than bypass security.

**Tests:** multiple inspectors and attempts; partial follow-up quantities; reopened case; source rollback makes link stale; wrong-tenant document ID; duplicate action creation; reassign vs approve race; attachment path traversal and access control. No physical stock delta from any QA action.

**Exit gate P3:** all required QA-only actions are tracked and auditable; exact signoff on any QA-only schema. Historical inspections are not silently lost.

## PHASE 4 — Inquiry, operational KPI, reports, settings and supportability

**Objective:** manage daily QA workload and answer “what happened, who decided, what is outstanding?” without inventory-side changes.

**Files:** `QaCaseService.GetInquiryAsync`, QA-only query helpers/DTOs, `QaInquiry.razor[.cs,.css]`, `QaWorkbench` KPI cells, existing report/export infrastructure if available.

1. Replace all-matching graph materialization with server-side filtered query + bounded page. Separate aggregates; compute counts in SQL, load child graph only for selected case page, avoid N+1, preserve company/branch filters.
2. KPI definitions (exact and visible): Open cases; Pending inspection; Pending review; Rejected decision qty (**record only**); QA action overdue; Median/average case close days; Completed actions; **Approved without recorded reject**. Do **not** call this genuine FPY unless immutable first-attempt history is delivered and denominator includes only eligible completed first attempts.
3. Inquiry filters: date range, source type, case status, item/lot/WO/PO/supplier, inspector, reviewer, action owner, overdue and branch (within permitted scope). Server-side pagination and stable sorting.
4. Links to case and real source; show whether status is suggested, unverified, historically executed with sealed allocation, or reversed. Never count QA-only decisions as on-hand, posted stock or inventory value.
5. Export using current standard with scoped permissions; include filter snapshot/time and clear `QA records — no automatic stock posting` note. Do not display costing/profit values.
6. Settings and support notes: optional module enablement per company using existing menu/settings only; non-QA tenants unaffected. No new ON setting for frozen stock gates. Explain safe data/schema rollout and legacy obligation classification.
7. Reason-code master/list and inspection checklist templates only if clearly existing patterns can be reused; optional feature, **never schema sprawl**.

**Tests:** 0/1/100/100k QA cases (seeded synthetic), multi-tenant partitioning, filtered KPI/rows agreement, no memory spike due to `ToList` of unbounded source, export permissions, status downgrade after rollback, query performance/indexes.

**Exit gate P4:** truthful metrics and bounded performance; no stock claims or unauthorized export.

## PHASE 5 — SQL Server integration/UAT and controlled QA-only release

**Objective:** prove QA is safe and useful in an actual deployment setting, not just code-complete.

1. Baseline and final `dotnet build .\ErpWeb.slnx` and `dotnet test .\ErpWeb.Tests\ErpWeb.Tests.csproj --filter "FullyQualifiedName~Quality"`; add feature-level SQL Server tests and run relevant baseline Inventory/Production/Procurement regressions. Log actual failures/skips and exact commands.
2. Use approved isolated SQL Server test database, **two connections** for concurrent update/approval, rowversion/unique index, transactional atomicity and real catalog privileges. Verify SQL scripts against the actual schema, no automatic tenant migrations.
3. UAT all seven source families, with explicit manual/unlinked pre-GRN case; batch no != PK, reversed/deleted source, partial quantities, multi-line, wrong tenant, no-source, source edited between draft and approve.
4. Browser end-user UAT: New → Save/Edit → Inspect → Submit Review → Approve/Reject → Corrective Action → Reinspect (when approved) → Close/Cancel; settings/inquiry/export; desktop + tablet + mobile; disabled/visible button correctness; keyboard-first entry.
5. **Negative-delta verification:** QA-only changes create no additional `IvBalLoc`, `CostLayer`, `IvTrxHistory`, inventory/production posting or WIP movement mutations. Existing approved ON-policy behavior is unchanged; do not “turn OFF” policy to make tests pass.
6. Independently run normal GRN, MR, MI, TR, ADJ, SC, VR, stock count, Daily Production, FG POST/ROLLBACK regression on a test branch/database where existing costing work is stable; **do not fix costing failures in this QA task**. Record pre-existing failures separately.
7. Release only for companies with known safe **record-only** QA policy state and reviewed schema; opt-in menus, role setup, support manual, rollback plan for QA-only UI/entities without dropping historical QA data.
8. Final diff audit: no forbidden files, no hidden new schema, no policy gate toggle, no stock posting calls. Make all unexecuted tests explicit blockers.

**Exit gate P5:** written UAT evidence, SQL concurrency tests, owner signoff, release label `QA_RECORD_AND_FOLLOW_UP_COMPLETE`. **Never** label fully stock-integrated QA delivered.

---

# 6. DATABASE/ENTITY STRATEGY (PREFER NO NEW SCHEMA)

**Use existing tables first:** `QaPolicy`, `QaCase`, `QaCaseLine`, `QaInspection`, `QaDispositionLine`, `QaAuditEvent`, existing grants/holds/allocations (read/compatibility only).

**Only if Gate 0 demonstrates need and DBA approves**, add QA-owned tables:

| Potential QA-only table | Purpose | Essential constraints |
|---|---|---|
| `QaInspectionAttempt` | Immutable inspection and reinspection events | Company/branch via parent, unique caseLine+attempt no, inspector/time, immutable original quantities and result, rowversion on edit-disabled record |
| `QaCorrectiveAction` | Who must do what and by when | Unique action ref/idempotent command, case/line foreign key, owner, decision qty with no physical posting, status/history |
| `QaDocumentLink` | User-entered/reference validated source/target link | Link type, source key and target PK/number, company/branch, status/revision checked, explicit VERIFIED_AS_DOCUMENT only |
| `QaAttachment` | Optional evidence | Tenant-scoped metadata and authorized storage pointer; no binary path injection, malware/size controls |

**SQL lifecycle:** versioned additive creation script, idempotent preflight, permission checks, additive index check, tenant-specific readiness test, deploy to test first, backout of *new feature exposure* without deleting action/history; safe restore from backup for schema disaster. No new foreign key into stock-posting transactions if it creates their rollback/deletion dependency. Never delete grant or hold history to get a clean install.

**Transaction rule:** each QA command atomically writes case/line/action/audit and version; failed SQL writes roll back entirely. Run actor/tenant/permission and stale version validation in authoritative operation. Use existing case/audit service patterns and unique keys, no independent second lifecycle engine.

---

# 7. PERMISSION / STATUS / BUTTON MATRIX

Treat this as a **required verification target**; current server behavior is authoritative unless Phase 1 explicitly corrects the UI/server mismatch.

| Action | Case state | Required effective permission | Implementation notes |
|---|---|---|---|
| Create new | New | QualityWorkbench `ADD` | Source lookup scoped by ACCESS and company/branch |
| Edit/Save | `DRAFT` | `EDIT` (or `ADD` for new) | Version guard; no editing approved cases |
| Delete draft | `DRAFT` | Existing `DELETE` | Preserve existing no-posting deletion restriction; audit policy reviewed |
| Submit | `DRAFT` | `SUBMIT` | Lines must be valid |
| Start inspection | `SUBMITTED` | `EDIT` | Keep existing service contract; optional UI shortcut |
| Save inspection | `SUBMITTED` / `IN_INSPECTION` | `EDIT` | All inspector identities audited |
| Submit for review | `IN_INSPECTION` | `SUBMIT` | Every required decision resolved |
| Approve | `PENDING_REVIEW` | `APPROVE` | Enforce separation of duties and legacy policy fence |
| Reject | `PENDING_REVIEW` (or supported `SUBMITTED`) | `REJECT` | Must appear even without APPROVE role |
| Cancel case | `DRAFT` / `SUBMITTED` | Existing `DELETE` for `CancelAsync` | Different from Cancel Edit/navigation; preserve current server checks |
| Release historical hold | Existing active hold | Existing `APPROVE`/hold rules | **No new hold release flow added**; historical compatibility only |
| Close | `APPROVED` / `REJECTED` | Existing `EDIT` for `CloseAsync` | No unresolved action or active historical hold |
| Settings | Company policy | QualitySettings `EDIT` | Frozen stock flags read-only/not newly enabled |
| Inquiry / export | Authorized menu | QualityInquiry `ACCESS` / existing export rights | Do not leak branch/tenant data |

Buttons must not be the enforcement layer. Server operation rejects unauthorized or stale calls even when URL/JSON is manipulated. Use friendly errors, focus restore, confirmation with visible popup actions and no double-submit.

---

# 8. TEST AND ACCEPTANCE MATRIX (MANDATORY BEFORE RELEASE)

**Source test cases**
- GR posted with `BatchNo=1205`, `Id=9814` resolves 9814 and correct detail row; wrong ID cannot silently validate.
- Posted MR has MR type; other receipt types cannot be spoofed as MR. General stock picker does not map pile ID to batch ID.
- PO-linked case different company/branch rejected, canceled PO rejected, non-PO manual delivery allowed as unlinked.
- Missing WI output PK, deleted WI, wrong revision, canceled/reversed posted source fail correctly; WIP stage `PROCESS_WIP` vs FG `FG_STAGING` enforced.
- One source line used twice inside one QA case cannot double its source qty; unrelated legitimate reinspection is not mistaken for a second stock issue.

**Workflow tests**
- 0 inspected, sampled partial, full inspected, all accepted, mixed accept/reject/rework/hold, decimal/UOM boundaries; precision matches current QA rules.
- Two inspectors on two lines; one then attempts approval and is blocked when separation ON.
- Reject-only user can Reject; Edit denied user cannot inspect; direct backend endpoints deny insufficient rights.
- Simultaneous edit/approve, duplicate submit, missing/empty rowversion, stale action row, transaction failure and retry do not overwrite/double audit.
- Reinspection history immutable; no false second incoming delivery ID; close blocked with mandatory actions pending and active historical holds.

**UI/UAT tests**
- 10-line inspection completed efficiently; aligned input widths, clear next step, no technical jargon, short readable user errors.
- Desktop 1920×1080 and 1366×768, 10–13" tablet and narrow mobile; horizontal alignment, popups/actions, scrolling and multi-select tested.
- Actual browser with app permissions, not screenshots from source only.

**Security/tests**
- Two tenants same case numbers; cross-tenant source/attachment/doc IDs denied.
- SQL Server least-privilege schema probe; absent/partial/complete QA tables; historical ON policy preserved.
- Export permission, stored attachment path protection, bounded server queries, KPI denominator accurate.

**Absolute acceptance conditions**
1. All mandatory Phase 1 safety defects resolved or explicitly blocked with user signoff; no hidden regressions.
2. QA record-only end-to-end flows pass SQL Server and real UI UAT; evidence includes test commands and results.
3. No approved QA record is misrepresented as posted stock, moved WIP, or a posting authorization.
4. No cost/quantity/ledger/posting/rollback/replay file modification and no stock-affecting QA gate activation.
5. New QA-only DB schema (if any) has reviewed SQL, index/permission, tenant/backout plan and DBA approval.
6. Legacy ON-policy/historical obligations unchanged; no deadlock/data loss/duplicate QA approval.
7. Support staff can explain and administer it without a developer for routine QA cases.

**Blocking conditions:** mismatched local source, unverified tenant schema, incorrect source PK, unresolved audit/version/security issue, misleading stock status, prohibited diff, unexecuted SQL concurrency tests, failed browser UAT, or unknown impact on previous ON policy. Never mark 10/10 due to user request alone.

---

# 9. WHAT THE AGENT MUST NOT “FIX” AS PART OF QA

- Do not resolve current FIFO/WAC/Standard costing errors or reinterpret CostLayer/physical balance invariants.
- Do not rebuild `IvTrxBatch`/`IvBalLoc`/`ProductionBalLot`/`ProductionOutput` schema or transaction ID formats to make QA lookups convenient.
- Do not add stored procedure triggers or transaction-side QA checks to enforce case completion.
- Do not modify GRN/FG/WI POST/ROLLBACK paths, physical stock status, period close, future-date checks, quantity reservations or valuation replay.
- Do not change Production definition, Work Order quantities or downstream sales/procurement transaction logic.
- Do not disable historical QA authorizers, delete old QA grants/holds/allocations, or force policy OFF.
- Do not create a new QA posting engine, duplicate ERP lookups, new CSS framework or parallel permission system.
- Do not claim `SUGGESTED`/manual document link = `EXECUTED` stock movement.
- Do not perform irreversible Git operations on user worktrees.

If a QA defect is inseparable from a forbidden area, prepare a **separate risk/contract memo** and STOP that item, while continuing independent QA-only work after approval.

---

# 10. PHASE-GATE HANDOFF, EXACT DELIVERABLES AND AGENT START INSTRUCTION

| Gate | Agent output | Approval target |
|---|---|---|
| **G0** | `plans/qa_gate0_20261110_current_local_evidence.md` + mismatch/impact table | User reviews real local source and tenant safety |
| **P1** | QA source/case/security fixes + tests, code-review diff | Correct identities, SQL versions, permissions, no stock file edits |
| **P2** | Professional Quality screens + browser/tablet UAT | Operator friendliness, no double click, standards reused |
| **P3** | QA-only corrective action/reinspection/linking + migrations **only if approved** | Immutable history, factual linked status, no stock changes |
| **P4** | QA inquiry/report/KPI/support docs | Truthful numbers, bounded queries, permitted export |
| **P5** | SQL Server and regression evidence + release/support notes | `QA_RECORD_AND_FOLLOW_UP_COMPLETE`, no costing impact |

**At EVERY phase stop**, report: current local worktree/root/branch/HEAD; actual modified files; requirement IDs closed; tests run with pass/fail/skip; unresolved issues; explicit check for prohibited diffs; recommendation `PASS`, `FIX_REQUIRED` or `BLOCKED`. Do not auto-proceed across gates.

### Copy-paste first instruction for Cursor / AI Coding Agent

> You are implementing **QA Operational Completion v4.0** in my **CURRENT LOCAL** repository `mokth/net10projectTemplate`, branch **`productionv2`**, solution `ErpWeb.slnx`. My local branch is already `productionv2`. **DO NOT assume it is a different repository.** First execute **Gate 0 only**: print exact root, origin, branch, HEAD, upstream comparison, all dirty files and worktrees; inspect actual local QA source and tests; compare each finding Q01–Q18 to the current code; inspect historical QA ON policies and tenant schema if authorized; provide path/method/line evidence and correct any stale plan assumptions. **Do not switch branches or reset/rebase/merge/stash. Do not implement at Gate 0.** Once G0 is approved, complete Phases 1–5 one at a time with mandatory reviews. Strictly no costing, CostLayer, stock/WIP quantity, posting, rollback, replay or new QA hold/grant activation. A stock-dependent feature must be deferred and separately planned. Reuse existing `productionv2` patterns and prioritize daily-user-friendly UI.

### Future integration register — explicitly OUT OF THIS PLAN

- Compulsory pre-GRN QA quality authorization and per-line receipts/grants.
- Finished-goods release for actual FG posting.
- WIP logical hold impact on available WIP or automatic rework quantity movements.
- Warehouse physical hold/quarantine, inventory stock transfer/scrap/vendor return and purchase CN allocation to QA decisions.
- Non-good WI posting authorization, valued reject/scrap lots and absorbed-cost split.
- Full POST → downstream usage → rollback/repost/replay and FIFO/WAC/Standard stock valuation proof.

These require a **separate user-approved plan** after costing and quantity posting architecture are stable. Their mere existence as code seams is not permission to activate them.

---

# 11. REFERENCE EVIDENCE (REMOTE `productionv2` AT REVIEWED HEAD)

These are actual paths, not generic guesses. In a source review use the **current local** version rather than these remote line anchors:

- `QaCaseService.cs` — `SaveDraftAsync`, `SaveInspectionAsync`, `SubmitForReviewAsync`, `ApproveAsync`, `RejectAsync`, `CloseAsync`, `CancelAsync`, `SearchSourcesAsync`, `GetInquiryAsync`, `Stale`.
- `QaSourceResolver.cs` — `FreezeGeneration`, `RevalidateDraftAsync`, `RevalidateInspectionAsync`, `RevalidatePostedOrPileAsync`, `RevalidateOutputAsync`.
- `QaSourcePrefill.cs` — `Matching`, `SearchText`; `QaCaseEntry.razor.cs` — `TryLoadSourceLinesAsync`, `SelectSource`, `CanInspect`, `CanApprove`.
- `QaPolicyService.cs` — `RejectNewEnablement`, `KeepExistingOnly`, `HasStockObligationsAsync`; `QaSchemaReadiness.cs` — 15 allowlisted tables and provenance classification.
- `QaDestinationComposer.cs` — source-dependent suggested, linked-unverified, executed/reversed navigation and current non-FPY `IsFirstPass`.
- `IvGoodsReceipt.razor.cs` and `IvMiscReceipt.razor.cs` — `OnRequestQa` pass displayed batch number in `sourceHeaderId`, while QA source query returns true `IvTrxBatch.Id`.
- `ErpWeb.Model/Entities/Quality/QaFoundation.cs`; `ErpWeb.Model/Configurations/Quality/QaFoundationConfiguration.cs`; `ErpWeb.Core/Menus/{MenuCodes,PermissionCodes}.cs`.
- `ErpWeb.Tests/Quality/` — existing eleven class files; unit coverage is not substitute for SQL Server transactional and browser UAT.

**Final signoff standard:** first prove local identity and source accuracy, then QA-only service correctness, then usability, then SQL Server and regression. **No full-release approval from remote inspection alone.**

**END — v4.0 — `productionv2` — QA operational completion — NO COSTING / NO STOCK-WRITER CHANGES.**
