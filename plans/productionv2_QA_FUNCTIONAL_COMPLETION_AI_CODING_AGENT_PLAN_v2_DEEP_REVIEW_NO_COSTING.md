# ProductionV2 — QA Functional Completion Plan (NO COSTING)
## v2.0 — Deep-review corrected AI Coding Agent Plan

**Repository:** `mokth/net10projectTemplate` · **target branch:** `productionv2`  
**Reviewed current HEAD:** `584411e317ac6990ed8a5f0886207779d821deaf` (10 November 2026).  
**Source plan:** `productionv2_QA_FUNCTIONAL_COMPLETION_AI_CODING_AGENT_PLAN_v1_NO_COSTING.md`.  
**Review decision:** **APPROVED FOR GATE 0 AUDIT ONLY; NOT YET APPROVED FOR UNATTENDED IMPLEMENTATION OR STOCK-CONTROL ACTIVATION.** Once Gate 0 reconciles the **actual local checkout, tenant state and schema**, the non-costing phases may be individually reviewed and approved. No part of this document authorizes costing, stock/WIP movement or posting changes.

**Why v2 was necessary:** v1 had an unsafe scope ambiguity. It called this a *no-costing* project while also planning to implement new GRN/FG/non-good/hold grants inside interfaces invoked by live stock posting. Such edits can change whether stock moves and whether existing historical posting/replay succeeds—even if formulas remain untouched. The entire stock-affecting implementation has been **removed from this execution plan**. Its requirements are documented as **design-only future dependencies** at the end. v2 adds concrete repository-verified source/permission/concurrency defects and a stronger acceptance contract for a genuinely usable **record-only QA** module.

---

# A. Deep review of v1 — correction register

| ID | Severity | What was wrong or insufficient in v1 | Correction in v2 |
|---|---|---|---|
| R01 | **CRITICAL** | Phase 4 proposed new grant/claim/hold behavior in services running inside Inventory and Production POST/ROLLBACK while explicitly excluding stock logic. A grant can stop/allow a stock transaction and create rollback obligations. | **No Phase 4 posting authorization implementation.** All five policy gates remain frozen. Future activation requires separate post-costing plan and approval. |
| R02 | **HIGH** | It treated `sourceHeaderId` as a reliable posted receipt key without accounting for source-page navigation passing **`BatchNo`** while `QaSourceResolver` fetches **`IvTrxBatch.Id`**. | Add explicit batch-number-to-ID resolution under server tenant scope, with tests where `BatchNo != Id`; never silently treat them as equivalent. |
| R03 | **HIGH** | Missing source IDs may escape validation: posted-GRN resolver can return success with no header ID; Daily Production resolver explicitly returns success when output ID is absent. | Require an existing, authoritative source identity for source-linked cases; separately label legitimate **unlinked manual** incoming records and never treat them as verified sources. |
| R04 | **HIGH** | v1 lacked a precise anti-overinspection invariant. Current resolver checks each line against source quantity, potentially allowing **sum of multiple lines > source quantity**. | Validate aggregate requested QA decision qty per exact source line/lot/generation; distinguish repeat inspection from a second allocation of source quantity. |
| R05 | **HIGH** | Current case-level `InspectedBy` is overwritten by the last saved inspection; approval separation compares only this field. Earlier inspectors may be able to approve a case after another person inspects the last line. | Approver must be different from **every current contributing line inspector** when separation is ON; test multiple inspectors and concurrent review. |
| R06 | **HIGH** | Strict RowVersion was proposed without considering EF SQL Server rowversion versus SQLite test-provider behavior and existing clients. | Define SQL Server-required nonempty optimistic token for existing records, provider-faithful concurrency tests and a separate SQLite-compatible test strategy; catch EF concurrency/unique errors. |
| R07 | **HIGH** | Record-only handoffs were close to implying `Posted verified` means an actual QA authorization/consumption. A manually linked posted SC/VR may prove document status but **not** consumption of QA-reserved goods. | Distinguish **document status verified** from **QA disposition executed**. No `EXECUTED` without authoritative existing sealed QA allocation. All record-only links remain advisory/linked. |
| R08 | **HIGH** | v1 conflated partial source qty, partial inspected qty, one QA inspection per case line and later reinspection; existing `QaInspection` has **one record per case line**, and saving inspection again replaces values. | Adopt explicit *decision quantity per QA line* and separate **reinspection case/action**. A case line can be submitted only when its defined decision qty is fully classified; do not fabricate multi-attempt history. |
| R09 | **HIGH** | First-pass yield was accepted as a KPI despite current `IsFirstPass` being approved-and-zero-rejected divided by all cases reaching inspection. It is **not genuine first-attempt yield** and an in-progress case lowers the denominator. | Rename existing ratio to a precisely defined approval/no-rejection metric, or add immutable QA inspection attempt history in an independently approved QA-only extension before showing real FPY. |
| R10 | **MEDIUM** | Existing `INVENTORY_STOCK` lookup actually lists posted **MR** details; arbitrary `IvBalLoc` selection requires different identity and behavior. | Implement a read-only warehouse pile picker with explicit `IvBalLoc.Id`, or relabel the existing path as **Posted MR** until safe general-stock support is ready. Never silently alias a pile ID to a batch ID. |
| R11 | **MEDIUM** | Multiple source-picker selections call `SelectSource()`, which rewrites the case header. Mixing PO/lot/doc rows can leave inconsistent line identities. | Fix *one source-owner per case*, scoped multi-line selection and server validation. One WIP/FG lot per lot case remains mandatory. |
| R12 | **MEDIUM** | The UX allowed redundant `Start inspection` steps and did not specify Cancel Case versus cancel editing. Reject button UI is tied to Approve; Inspect can be shown without Edit. | Consolidate inspection steps without deleting backend lifecycle; implement independent permission matrix, explicit `Cancel case`, plain-language buttons and clean confirmations. |
| R13 | **MEDIUM** | Source-page `Request QA` may open a pre-GRN case from an unsaved/new GRN without a durable delivery identity. The PO lookup is insufficient for non-PO receipts. | Source entry must either resolve a persisted authoritative source or clearly create a **manual record-only** delivery inspection; do not imply matching GRN/stock. |
| R14 | **MEDIUM** | v1 implied attaching or draft-prefilling stock transactions before proving existing service contracts. | For non-costing release, only open existing destination pages and optionally prepare a draft **through proven existing public draft service**, separately reviewed. No auto-POST and no silent target-quantity modification. |
| R15 | **MEDIUM** | Reporting performance was diagnosed, but the plan did not require filtering the summary/denominator at SQL scope, nor protection against N+1. | Aggregate case/KPI counts in SQL; separately fetch bounded page and child data; explain date/filter scope and non-FPY semantics. |
| R16 | **HIGH** | Historical enabled QA policies and existing allocations could be altered while ‘freezing’ new behavior, potentially affecting ongoing customers. | Preserve historical rollback/hold protection and require a tenant-by-tenant obligation audit **before any change** to authorizers or policy handling. No blanket disable, cleanup or forced migration. |
| R17 | **MEDIUM** | New QA-only schema was optional but lacked an exact backout/provenance plan and an explicit bounded ownership model for action quantities. | Prefer existing QA schema. Any `QaCorrectiveAction`/`QaDocumentLink` must be separately reviewed QA-only, idempotent, scoped and auditable; **not** a second stock ledger. |
| R18 | **MEDIUM** | v1 lacked a clear user-facing release definition when valuation-dependent routing is not possible. | Product is explicitly **QA Inspection & Follow-up (record-only)** until a separately approved inventory/production integration. The release note must list every deferred physical disposition. |

**v1 review score:** **8.1/10** (strong safeguards but a critical scope conflict and incomplete source/quantity/approval contracts).  
**v2 plan quality:** **9.4/10 — conditional**; this is a planning assessment, **not** a completed test score. Remaining gap to 10/10: live tenant inspection, current checkout diff, SQL Server concurrency, exact document identity and UI UAT have not been executed in this review. **Do not mark “APPROVED FOR IMPLEMENTATION — 10/10”.**

---

# B. Non-negotiable project boundary and release definition

## B1. Objective

Finish the optional **QA Inspection & Follow-up module** for Malaysian SME customers so an ordinary clerk/inspector can: choose a genuine source, record quantities/results, obtain a separate review, capture ACCEPT/REJECT/REWORK/HOLD decisions, assign and track follow-up, find the existing receiving/production/scrap/return documents, and report on what is **recorded** or **independently verified**—without changing how Inventory, Production or Procurement move or cost stock.

**Level A in scope:** complete, accurate, professional, optional **record-only** QA + follow-up, case action tracking, safe document navigation, truthful inquiry, reports, audit and SQL Server QA-only UAT. Existing non-QA transaction behavior is unchanged.

**Level B NOT in this plan:** QA as a compulsory gate on GRN, FG, Daily Production or stock issue; claims of held/reserved stock; new physical `QCHOLD` conversion; automatic WIP rework, scrapping or supplier returns; any trusted posted inventory disposition allocation. Such behavior **cannot be declared complete** merely by creating a QA document link.

An existing company with previously active holds/allocations is a **legacy exception that must be preserved and investigated**, not an excuse to activate further stock-affecting features.

## B2. Absolute prohibited changes

The agent **MUST NOT edit** any of these to implement this plan:

- `ErpWeb.Core/Inventory/IvInventoryPostingService*.cs`, `IvStockCountService*.cs`, stock replay adapters, GRN/MR/SC/VR/TR/ADJ/MI actual POST/ROLLBACK services, and inventory ledger/history writers.
- `ErpWeb.Core/Production/ProductionOutputService*.cs`, `ProductionFinishedGoodReceiptService*.cs`, `ProductionStockWriter.cs`, WIP/production quantity writers and their posting/rollback entry points.
- `ErpWeb.Core/StockLedger/*`, FIFO/WAC/Standard cost/replay, `CostLayer`, valuation, costing correction, purchase cost settlement, production absorbed-cost, month-end close, future-date/stock logic, UOM conversion algorithms.
- Existing Inventory/Production balance entities, transaction/history schemas, document revision semantics, posting coordinator, stock lock semantics or business-period rules.
- `QaReceiptAuthorizationService`, `QaFgAuthorizationService`, `QaNonGoodAuthorizationService`, `QaPhysicalHoldAuthorizationService`, `QaLogicalHoldAuthorizationService` **for any change that alters POST/ROLLBACK/claim/hold behavior**. QA-owned **read-only contract documentation** is allowed. A genuine safety defect in these services requires a **separate approved hotfix plan** with costing-team coordination; do not smuggle it into Phase 1.
- No run of `scripts/create-qa-*.sql` or menu schema scripts on customer/live DB; no feature-gate enable, no automatic migrations, no changes to policy flags during deployment, no new stock transaction type.

**Permitted:** QA-only case/policy display and non-posting service validations, QA-only DTO/UI, QA-only reports, QA-only data tables **after separate Gate 0 DBA signoff**, QA-only tests. In Inventory/Production source UI, only minimal **presentation/navigation** changes to the Request QA button are allowed; any draft-service call is independently gated. Never alter source save/post/rollback logic.

**Fail-closed stop rule:** If a source lookup, action closure, record-only report or link cannot be made accurate without altering a prohibited writer/posted document schema, **mark that subfeature BLOCKED**, show a truthful workflow note, record a separate integration dependency, and continue unrelated QA-only tasks. Do not change financial or physical posting code just to make a QA button succeed.

## B3. Existing repository evidence — current source, not old claims

- `ErpWeb.Core/Quality/QaCaseService.cs`: CRUD, submission/inspection/review/approval/rejection/close, case/audit/inquiry/source-search. `GetInquiryAsync` materializes all matching cases **before** in-memory paging.
- `QaCaseService.SaveInspectionAsync`: can save one inspection line at a time; header `InspectedBy` is overwritten; `SubmitForReviewAsync` verifies inspection **exists**, not that inspected qty equals intended decision qty.
- `QaCaseService.Stale`: a missing/empty `RowVersion` skips a stale test.
- `QaCaseService.ApproveAsync`: checks last case `InspectedBy`; may invoke existing logical/physical placement when an **older ON** policy is present.
- `QaPolicyService`: five posting gates frozen for new enablement, with historical ON values possible. `QaSettings` exposes them as read-only.
- `QaSourceResolver.RevalidatePostedOrPileAsync`: expects `IvTrxBatch.Id`; posted GR/MR Request QA links pass `BatchNo`. `RevalidateOutputAsync` returns success for missing output ID. Source-line maximum checks are per-row, not a total across repeated source identities.
- `QaCaseService.SearchSourcesAsync` maps `INVENTORY_STOCK` to posted MR; no general `IvBalLoc` picker. `QaCaseEntry` source result selection overwrites header; prefill uses a bounded first page without guaranteed exact source retrieval.
- `QaDestinationComposer` currently uses grants/allocations to show certain `EXECUTED` statuses, while a new destination link is navigation only. `QaCaseEntry` has `CanInspect` / `Reject` visibility problems and success messages suggesting accepted stock can be posted from QA.
- Existing UI: `ErpWeb.UI/Quality/{QaWorkbench,QaCaseEntry,QaSettings,QaInquiry}.razor[.cs,.css]`, shared `QaHowToUsePopup`, `QaRecordOnlyBanner`. Existing source Request QA buttons in `IvGoodsReceipt`, `IvMiscReceipt`, `PrDailyProductionEntry`, `PrFinishedGoodReceiptEntry`.
- Existing QA schema: fifteen tables in `QaSchemaReadiness.AllowlistedTables`; `QaPolicy`, `QaCase`, line, inspection, disposition, audit, grants, allocations and holds. All-absent schema is **history UNKNOWN**, not proof that QA was never previously deployed.
- Tests: `ErpWeb.Tests/Quality/*`, existing Inventory/Production integration tests. Historical evidence reports older SQLite passes; **this v2 review did not execute build/test or inspect live tenant databases**. Costing is still under separate development.

---

# C. Execution governance and Gate 0

## C1. Mandatory skills; use live source over skill prose

Read and apply on **current working checkout**:

- `.agents/skills/productionv2-costing/SKILL.md`, `references/stock-balance.md`, `references/replay-rollback.md`, `references/testing.md` (**read only; no costing work**).
- `.agents/skills/erp-ui-core/skill.md`, `.agents/skills/erp-transaction-ui-standard/skill.md` and the specific Inventory/Production/Purchase page skills where presenting a button/link.
- `.agents/skills/erp-inline-search-standard/skill.md`, `.agents/skills/erp-large-data-smart-lookup-standard/skill.md`, `.agents/skills/erp-grid-row-actions-left/skill.md`, `.agents/skills/erp-entry-footer-standard/skill.md`, `.agents/skills/devexpress-dxgrid-column-readability/skill.md`, `.agents/skills/devexpress-blazor-editor-binding-safety/skill.md`, `.agents/skills/devexpress-popup-action-safety/skill.md`.

If a skill describes branch `production`, **the active requested `productionv2` branch and its current code override that stale branch reference.**

## C2. Gate 0 — AUDIT ONLY (mandatory stop)

1. Capture `git status -sb`, `git rev-parse HEAD`, `git diff --name-status`, `git worktree list`, HEAD of `origin/productionv2`; compare to pinned review. Do **not** reset, merge, cherry-pick or overwrite worktrees. If costing/replay is dirty or has moved, isolate QA changes in a clean feature worktree approved by the user.
2. Trace the actual paths **QA UI → QA service → DB validation → source document → existing POST/rollback interfaces → stock/WIP/history → downstream/period policy** for risk understanding **only**. Record call sites. Do not modify existing posting behavior.
3. Inventory all QA schemas, keys, indexes, SQL scripts and existing QA records **read-only per company/tenant**: existing published policies, legacy ON flags, active logical/physical holds, posted/reversed receipt/FG/non-good allocations, orphaned rows, missing indexes; classify `READY`, `PARTIAL`, `ALL_ABSENT_HISTORY_UNKNOWN`, `PROBE_ERROR`. An app admin may have catalog privileges a normal IIS login lacks; test least-privilege metadata access separately.
4. Verify `BatchNo` versus `IvTrxBatch.Id` (including `BatchNo != Id`), identity of GR/MR from UI routes, WI output IDs/revision, WIP/FG lot identity, true Item/UOM source, alternate non-PO GRN flow, supplier identity and stock pile ID. Build a **source identity contract table**, not assumptions from `RefNo`.
5. Trace concurrency token behavior: SQL Server `RowVersion`, EF errors, current client requests; SQLite may not generate SQL Server rowversion. Inspect `RunningNumberKeys.QualityCase` concurrency and exact uniqueness scope, auditing before prescribing a fix.
6. Check legacy menu permissions, role access, `PermissionCodes` values, report export rights, file/DMS infrastructure, existing lookup patterns and safety of current source button routes.
7. Baseline clean build + targeted Quality tests + existing Inventory/Production regression tests. Capture commands, passed/failed/skipped and current failures; this plan's author has not run them. For SQL Server UAT use an **isolated scratch DB** approved by operations.
8. Inspect approved QA-only schema-deployment mechanics; never assume `create-qa-*.sql` ran because menus exist. Write explicit missing-schema/older-history operational recovery procedure without automatic deletion.
9. Produce **Gate 0 safety decision** for each feature: SAFE_QA_ONLY, SAFE_PRESENTATION_ONLY, DEFER_STOCK_POSTING, or BLOCKED_CONTRACT. Any critical missing schema, unknown tenant provenance with historical obligations, or conflicting dirty changes require STOP and user review.

**Required artifact:** `docs/qa/qa_gate0_current_repo_evidence.md`, including exact HEAD/diff; tenant (non-sensitive counts only) QA objects/policies/holds; source identity matrix; permission matrix; current tests; schema delta request; explicit prohibited-file baseline. **Do not start Phase 1 automatically.**

---

# D. Operator workflow contract — what ordinary ERP users actually do

## D1. Standard no-costing lifecycle

**Typical incoming/production workflow:**

`Open source / New QA case → select exact source and inspection decision qty → Save draft → Inspect selected lines (one screen) → Submit for review → independent Approve/Reject → view action cards (accept / supplier follow-up / rework / hold / reject) → link existing destination or record follow-up → verify real document status where possible → complete action → Close case`.

**Important wording:**

- “Approve inspection” means a quality **record was approved**. It does **not** release stock, validate stock posting or create physical material.
- “Hold” means **quality follow-up needed** for a newly recorded case in this release. Do **not** imply new warehouse/WIP quantity reservation. Existing historical reservations must still be respected.
- “Reject quantity” means **inspection rejected**. It does not automatically mean **case rejected** by reviewer.
- “Rework” creates an **assigned corrective-action task only**. It does not move WIP, post consumption or produce a new lot.
- “Document linked and posted” means the existing transaction was verified posted; it **does not prove a QA allocation was claimed**.
- Before-GRN rejected material is **not stock**. Do not offer stock Scrap or Vendor Return for it.

## D2. Supported source families and identity rules

| QA source | Required real identity | Partial/multiple behavior | Record-only destination | Explicit stop condition |
|---|---|---|---|---|
| Pre-GRN supplier delivery with PO | company+branch+PO+release+PO line; delivery occurrence is a QA record key **not a GRN key** | partial inspection by chosen decision qty; separate delivery/reinspection case has a separate identity | existing GRN page, supplier rejection task | Missing PO line/invalid source; do not infer same delivery from supplier note |
| Pre-GRN non-PO delivery | manual delivery note and validated tenant source fields, **tagged MANUAL_UNLINKED**; no authoritative PO/GRN line | record-only with strict item/UOM validation via existing master | record supplier inspection; open GRN as an unlinked operator step | Cannot assert accepted qty controls future GRN |
| Posted GRN | actual `IvTrxBatch.Id` + detail `Id` + document revision, type GR and POSTED; resolve user-facing BatchNo | many lines in **same batch**; sum per detail ≤ actual chosen quantity | VR / CN handoff, read-only linked doc status | BatchNo used as ID without trusted resolution; rolled-back source |
| Posted MR | actual batch `Id` and details; posted MR type; **label it Posted Misc Receipt** | many lines from same batch | SC handoff | Do not silently classify it as arbitrary inventory stock |
| Other on-hand warehouse stock | stable exact `IvBalLoc.Id` + company/branch/item/warehouse/location/lot/status/base UOM + current row/version if available | one exact pile per case line; multiple piles only with explicitly supported mixed-pile case rules | read-only case/corrective task, SC screen navigation | No trustworthy pile ID or source schema representation; source moved or identity changed |
| Daily Production good | `ProductionOutput.Uid`, revision/status, item/WO/lot/UOM | one output generation; case sum ≤ source selected qty | WIP/FG activity navigation only | `outputId` missing or deleted, unposted where source contract disallows |
| Daily Production non-good | `ProductionOutput.Uid`, revision, existing scrap+reject+hold counters | record counter inspection; **not** an inventory issue | non-good corrective action | Attempt to create a valued scrap/hold lot or claim POST authorization |
| Production WIP lot | `ProductionBalLot.Uid` + stage `PROCESS_WIP`, WO/lot/base UOM | **one production lot per case**; multiple lots use separate cases | rework/hold follow-up; existing WO navigation | mixed-lot / wrong-stage / stale lot |
| FG staging lot | `ProductionBalLot.Uid` + stage `FG_STAGING`, WO/lot/base UOM | **one staging lot per case** | existing FG receipt page navigation | missing lot or attempt to auto-receive |

**Do not reuse `SourceHeaderId` for both `IvTrxBatch.Id` and `IvBalLoc.Id` under the same source type without an unambiguous type discriminator.** If a new source key cannot be expressed in existing QA-only columns safely, make a minimal **QA-only typed source identity** proposal at Gate 0; retain and migrate/interpret historical MR-origin case data without rewriting inventory documents.

**Case-line quantity contract:**

- Each case line has a **decision quantity** (the selected part of the source to be classified). For **verified source-linked** cases, `0 < decision qty ≤ currently eligible source qty` in the *source's validated UOM*. For explicitly **manual/unlinked** incoming inspection, validate positive qty/item/UOM as QA-entered data, but display **source quantity unverified**; never claim a trusted source bound or posting entitlement.
- Current `QaInspection` is unique per `QaCaseLine`. Do not pretend it supports a history of sequential inspection attempts on one line. For v2, a completed decision line must have `InspectedQty == decision qty` and `Accept + Reject + Rework + Hold == decision qty` **at final review**. A partially edited/incomplete line can remain in inspection but cannot be approved as fully classified.
- Partial source example: GRN line 100 units, QA case decides 20 → QA line qty 20, inspected 20, dispositions 20; remaining 80 is **not automatically accepted**. A separate case can address another portion after explicit duplicate/reinspection checks.
- **Repeated inspection is not a new receipt authorization.** Multiple cases against the same source are allowed only under a clear rule: *new portion*, *resampling*, or *reinspection*, visible to the reviewer. No double counting in case KPIs or claims of cumulative source stock. The current unique PO-line-per-case restriction stays unless a schema/logic change explicitly proves otherwise.
- For source documents with multiple lines, aggregate requested qty by exact source detail + generation + lot/UOM, including duplicate/repeated case lines. Do not validate each row independently and miss a sum above available.
- Quantities use existing `IvQty` precision and current source conversion. No new global conversion formula. If the base/entered UOM cannot be proven equal or converted with existing supported data, refuse the source selection.
- Distinguish *sample measured* from *decision quantity*. The current schema lacks reliable sampling semantics; a future QA-only sample count/plan is a separately reviewed enhancement and must not silently justify accepting uninspected quantity.

---

# E. Implementation phases — QA ONLY

## Phase 1 — Case correctness, source integrity, security and concurrency

**Allowed code:** `ErpWeb.Core/Quality/QaCaseService.cs`, `QaSourceResolver.cs`, `QaSourcePrefill.cs`, `QaDtos.cs`, `QaCodes.cs`, `QaDestinationComposer.cs` **for non-posting case/inquiry/identity logic only**; `ErpWeb.UI/Quality/*`; `ErpWeb.Tests/Quality/*`. QA policy case/display fixes only; **no changes to authorization service claim/place/restore implementation**.

**Implementation steps:**

1. Introduce a centralized **QA case command validation policy** using existing tenant/branch and access patterns. For a source-linked case require authoritative source ID, exact source type, matching detail, active/posted state as appropriate, stable generation/revision, item/UOM/lot/warehouse/location/status. Manual incoming without PO must carry an explicit **unlinked** business flag/description; no pseudo source IDs. Add `BatchNo → Id` scoped resolution at read/prefill boundary; never use BatchNo as internal ID.
2. Reject foreign, mixed or stale identities in `SaveDraft`, `SaveInspection`, `SubmitForReview`, `Approve`; revalidate under appropriate QA-only transaction/lock where needed, and always scope reads by company+branch. Stop silent success on missing posted GR/production output ID. Validate line-source **aggregate quantity** and repeated PO detail rules, with one source-owner per case. Preserve historical case records as readable even if old source is unavailable; block further approval, do not delete them.
3. Define **finalization** for the current one-inspection-per-line model: every line must have a decision quantity and complete classification before review. Draft/in-inspection can hold incomplete work; approval must never treat uninspected remainder as accepted. `PASS`, `FAIL`, `CONDITIONAL` must be consistent with disposition/reason configuration, without silently reclassifying it.
4. Correct inspector separation using **all contributing `QaInspection.Inspector` values**, not just `QaCase.InspectedBy`. Check under the final QA review concurrency boundary. A reviewer who inspected any contributing line cannot approve when separation is ON. `InspectedBy` may remain a display summary, not authority.
5. Enforce permissions in service and UI separately for `Add/Edit/Submit/Approve/Reject/Delete`, `Close`, and historical release according to **actual existing `PermissionCodes`**. No new permission constant without reviewing the menu/role installer. Inspect operations require `Edit` unless an existing dedicated inspect permission is genuinely wired. Reject permission must be independent from Approve. Direct service calls with forged route/query data must fail.
6. Require the current SQL Server `RowVersion` when changing **existing** QA cases and policies; distinguish a new unsaved entity. Always force a real rowversion-producing update of the QA case header along with line changes, catch `DbUpdateConcurrencyException`, unique conflicts and duplicate transitions, and provide reload messages. Do not assume SQLite's rowversion works like SQL Server; provide explicit testing adapters or SQL Server integration proof. Double-click Approve/Reject/Cancel must have one accepted transition/audit, never two.
7. Save/submit/inspect/reject/close must be **QA-only atomic transactions**, including audit and any corrective-action links. If state change fails, no partial QA line/inspection/audit commit. Check isolation/locking order against existing QA approval path; do not take stock balance locks for ordinary record-only case editing.
8. Preserve **historical ON policies, live physical/logical hold obligations and posted QA allocations**. Do not disable, delete, release or start creating new stock obligations as part of this phase. If audit finds a new-case approval could place an unsafe hold under an older ON policy, **STOP that legacy tenant rollout** and open a separately approved compatibility/safety fix with posting owners; no stealth `qaAuthorizer` code change.
9. Clarify `Cancel Case` versus editor `Cancel`, rejected *case* versus rejected *quantity*. Preserve audit, existing referential constraints and retention; `Close` must respect all existing hold and outstanding-action rules. Restrict reopening/reinspection to a linked new case or proven existing legal state path; do not turn a posted old case into a mutable draft.
10. Fix QA case numbering race only after confirming `IRunningNumberService` and case uniqueness implementation. A unique index is the final guard; accept harmless sequence gaps and return actionable retry/conflict errors, not raw SQL exceptions.

**Phase 1 tests (minimum):** no/mismatched RowVersion, simultaneous two-tab edits and Approve/Reject, two inspectors on different lines, all-permission deny/allow, fake and cross-branch source, BatchNo != Id, outputId absent, sum of duplicated line qty > source, changed revision, wrong stage/lot, malformed conversion, partial source vs partial final inspection, repeated cases with explicit purpose, QA schema absent/partial, old ON/holds untouched. Run SQL Server concurrency tests (SQLite cannot certify rowversion).

**Phase 1 gate:** All QA-only correctness behavior verified, no existing stock/posting-owner changes, existing policy freeze unchanged. **STOP for review.**

## Phase 2 — Daily-use Quality Workbench and inspection UX

**Allowed code:** `ErpWeb.UI/Quality/QaWorkbench.razor[.cs,.css]`, `QaCaseEntry.razor[.cs,.css]`, `QaSettings`, `QaHowToUsePopup`, `QaRecordOnlyBanner`, QA-only DTO/read services; source-page `Request QA` **presentation-only** with approved minimal diff. Reuse live DevExpress and shared ERP controls.

**Screen design (customer-first):**

- Workbench default tabs/filter: **My inspections / Awaiting review / Follow-up needed / On hold / All cases**. Reuse `CommonDataGridEx`, server-side search/filter; case row actions **left**; compact tablet/mobile list with same capability; avoid duplicating a second grid implementation.
- On **Request QA**, auto-open the exact persisted source via authoritative ID; if not saved/identifiable, show **Save source first** or **Manual inspection (not linked to stock)**. Do not use `RefNo`, a query-string supplier name or a forged `returnUrl` as authority.
- Source picker: server paging and exact-code search with cancelable/debounced queries, item/lot/PO/WO/supplier context; support 10k+ rows; verify selection is from **one permitted source**; allow multiple lines from the same document with explicit **Add selected**. No 50-row-only result or first-200 prefill assumption. If the source is naturally a multi-line GR, prefill exact lines using a **direct scoped source fetch**, not “search text matches first page”.
- Display **source qty / decision qty / inspected qty / classified qty / uninspected qty** separately. Label entered UOM. Quick buttons: **Pass entire decision qty**, **Reject with reason**, **Split decision**, **Hold**, **Rework task**; user can correct before review. Batch actions must be all-or-nothing or state clearly that they save each line independently.
- Minimize clicks: allow Save Draft, then one **Inspect & Submit for Review** step when safe; preserve existing backend state/audit semantics. Hide redundant `Start inspection` from default UX if inspection can already begin from Submitted; do not remove protected server state transitions. Never automatically approve.
- Approval panel displays source and quantity summary, result, reasons, inspector names (including all contributing inspectors), editable reviewer remarks and outstanding tasks. Independent **Approve / Reject** actions; prevent accidental double submissions; show clear confirmation and stale-result refresh.
- Explicit **Cancel case** business action for allowed statuses (distinct from **Cancel editing**). Close Case only when mandatory follow-ups and holds are resolved under documented rules. No action visible when its service permission/state would reject it, other than transient stale state.
- All current `ACCEPT/REJECT/REWORK/HOLD` and source status constants shown as plain language: **Received from supplier, Produced goods, Work in progress, Stock inspection, Passed, Rejected qty, Needs rework, Needs follow-up**. Technical keys remain in the data model only.
- Record-only banner clearly states: **“This inspection records the quality decision. It does not block or post inventory/production transactions.”** If an older ON tenant has active historical stock controls, do **not** show this unconditional message there; instead show a controlled **legacy policy warning** and block unsafe workflow until reviewed.
- Use existing `.razor` + `.razor.cs` + `.razor.css` pattern, `DxFormLayout`, `DxGrid`, `DxPopup`, `DxTextBox`, `DxSpinEdit`, `DxComboBox`, common CSS tokens; avoid wide textboxes for short codes, wasted whitespace, hidden action buttons, horizontal scroll to row action. Use valid `TextExpression`/`ValueExpression` bindings for manual changed events and visible popup footer actions.
- Verification widths **1440, 1024, 768, 390**; zoom **100%, 125%**, light and dark themes, keyboard-only, tablet touch targets, error/reload/empty/loading, long source codes and multilingual-friendly labels. Build success is **not visual proof**.

**Phase 2 gate:** A real SME operator can create, inspect, submit and review a multi-line source with minimal unnecessary clicks; UI matches canonical module patterns; server permissions and source identity remain authoritative. No stock/cost/flags changed. **STOP for review.**

## Phase 3 — QA disposition actions, reinspection and safe document link tracking

**Allowed code:** QA-only `QaCaseService`/new `QaCorrectiveActionService` if justified, `QaDestinationComposer`, `QaDtos`, QA UI, QA-only entity/configuration and DBA-approved QA scripts. Destination **navigation** pages only unless an independent review proves a non-posting public draft save can be called safely. **No actual stock posting.**

**Minimum delivered workflow:**

1. **Action from approved disposition:** for `REJECT`, `REWORK`, `HOLD` create a simple operator-friendly action card on the relevant case line: **What needs to happen?**, assigned user/role, due date, optional reason, status **Open → In progress → Completed / Cancelled**, notes and audit. `ACCEPT` needs no automatic physical action. Avoid overengineering an approval rules engine.
2. **Correct destination per source:** pre-GRN reject → notify/replace supplier (not stock); posted GR reject → open existing vendor return or purchase CN (CN owns its VR); received MR/warehouse stock reject → open Scrap; WIP rework/hold → link WO/operation and assigned task; FG staging reject → follow-up in staging, not inventory scrap; Daily Production non-good → corrective task against existing counters only.
3. **Reinspection** creates a linked new QA case from the action/source with an explicit reason. Preserve the earlier approved case and its audit. Do not mutate or erase the first result to simulate a second inspection; trace first/subsequent attempts separately.
4. **Document link**: operator can select an existing GRN/FG/VR/SC/CN/WI document from an authorized, company/branch-scoped **read-only lookup**, with target type, stable ID/line, revision where meaningful. A URL `?qaCase=` is navigation decoration only. No stock document numbers as security identities. Do not auto-create a draft by database inserts; use the existing public draft service only behind a separately approved non-posting contract.
5. **Link status truth:** `ACTION_OPEN`, `DOCUMENT_LINKED_DRAFT`, `DOCUMENT_LINKED_POSTED`, `DOCUMENT_LINKED_REVERSED`, `DOCUMENT_CANCELLED`, `DOCUMENT_MISSING`, `ACTION_COMPLETED` may be QA display states. They mean a **real target document's** status was read and validated; they do **not** mean QA quantity was consumed. Existing `EXECUTED` stays exclusively for **previously sealed QA allocation proof**. The UI must say **“Posted document linked; quality quantity not authoritatively claimed”** where appropriate.
6. Match target to original QA source as far as **existing authoritative read models permit**: same company+branch, document type, line/item/UOM, lot/location/status where meaningful, reasonable decided qty and document revision. If unable to prove exact match or relation, show **“Linked — needs manual verification”** and keep action open. Do not claim physical consumption from the same batch/item/qty alone.
7. On target rollback, cancellation, delete, changed generation, or post status reversal, recompute link display from actual target read; flag action for review rather than silently closing. Do not edit target's posting or rollback logic. Report stale/orphan/unverified links in an admin queue.
8. A user may mark **follow-up administratively completed** with evidence/comment if genuinely permissible for record-only policy; display it as **administrative closure**, not stock execution. For mandatory corrective actions, prevent case closure until completed or explicitly waived by authorized reviewer with a recorded reason. Existing active historical holds always block closure where current rules require.
9. Avoid double-allocation claims: if one target document is linked to multiple QA actions/cases, report it and require review. This schema **must not imply exclusivity of actual inventory stock** or replace existing stock reservation. QA action quantity is an **instruction amount**, not a balance.
10. If QA-only schema is needed, prefer minimal `QaCorrectiveAction` and `QaDocumentLink` tables under separately approved QA-only DDL. Add company+branch+case/line+immutable source/target keys, `RowVersion`, audit, FK to QA case/line, bounded unique keys, indexes; **do not add FK/migration to Inventory/Production posting tables**, and do not add valuation columns.

**Phase 3 gate:** Each non-accept disposition can be tracked to an accountable next action and linked to existing documents without changing a stock movement. All link statuses are honest. A full WIP-to-Inventory/Rework posting workflow is **NOT** claimed complete. **STOP for review.**

## Phase 4 — Operational inquiry, reports, policy UX and support handbook

**Allowed code:** `QaCaseService.GetInquiryAsync` (or new read-only QA query service), `QaInquiry.razor[.cs,.css]`, QA DTOs/menus/reports, QA support docs, approved **QA-only** indexes.

1. Replace current **unbounded `Include` + `ToListAsync` before `Skip/Take`** with company+branch SQL-filtered `CountAsync`, stable ordered `Skip/Take`, then fetch child records for **only** the selected page (or use aggregate projections). Ensure no N+1, no unbounded Blazor circuit collections, tenant isolation, pagination stability and correct filter consistency.
2. KPI cards: **Awaiting inspection**, **Awaiting review**, **Open corrective actions**, **Reinspection due**, **Active quality-record follow-ups**, **Closed**, **Overdue actions**, and **Approval without rejected qty** (defined measure). Show filters by date, item, supplier, source, WO, inspector/owner and status as data allows; no source preloading.
3. Do **not** call the current `IsFirstPass()` ratio **“First-pass yield”**. Its numerator/denominator do not measure immutable first attempts. Rename it to **“Approved with no rejected qty / cases that reached inspection”** with visible denominator and inclusion rules; preferably use a business-meaningful *completed inspected cases* denominator and **document the exact choice**. True FPY stays **NOT AVAILABLE** until reliable attempt history is implemented and approved as QA-only.
4. Distinguish `QA_ACCEPTED_QTY` (inspection decision), `POSTED_DOCUMENT_QTY` (existing document observed), `QA_SEALED_ALLOCATION_QTY` (only historical verified allocations) and `ACTION_FOLLOWUP_QTY`. Never sum them together as physical inventory/production balance.
5. Provide read-only reports **by supplier, item, WO, defect/reason, inspector, disposition and aging** using existing report/export framework and permission checks. No valuation columns or new inventory math. Test CSV/Excel/PDF only where existing framework supports them.
6. Settings must distinguish optional **Record inspections** availability (can be implemented through existing menu/permissions, no new flag required) from **unavailable stock posting controls**. Frozen gates remain read-only OFF/legacy ON. Inspector separation and QA-only defaults remain safely configurable; do not set `QaPolicy.Enabled` in a way that derives or enables stock flags.
7. Clear human help: “How to record”, “How to approve”, “What does Hold mean?”, “How do I follow up rework?”, “What happens when linked document is rolled back?”, “Why doesn't Quality post stock?”. Remove misleading “Accepted for GRN/FG posting” messages from record-only screens.
8. Build a **support runbook** covering per-company menus and permissions, QA table/index prerequisite, schema UNKNOWN/partial handling, old ON/allocations, when not to enable gates, safe QA-only deployment and rollback, and read-only diagnostic queries. No automatic live SQL or table drops.
9. Performance evidence at 10k/100k seeded QA cases and long PO/source lists; report SQL query count, bounded result size, indexed predicates and test latency targets agreed at Gate 0. Avoid false hard-coded SLA claims without environment evidence.

**Phase 4 gate:** Accurate, bounded QA inquiry and useful reports; non-technical help; no unverified stock assertions, no posting changes. **STOP for review.**

## Phase 5 — Strict SQL Server UAT, no-cost regression and record-only release

This is **not** an authorization to publish stock gates or to apply scripts to customer tenants.

**Required automated/UI scenarios:**

| Group | Must pass / be explicitly recorded |
|---|---|
| QA command atomicity | Case create/save/submit/inspection/review/approve/reject/cancel/close, audit and action rows; injected DB failure leaves no partial QA writes |
| SQL Server concurrency | Two inspectors edit same case, two reviewers approve/reject, duplicate Save/Submit, same action double completion, RowVersion present/stale/missing, unique case-number conflict |
| Inspector independence | Alice inspects first line, Bob inspects second, neither can approve if separation ON; independent Carol can; OFF follows configured policy |
| Source identity | `BatchNo != Id`; GR vs MR types; forged/bogus/missing header ID; WO/lot wrong stage; mixed sources; duplicate PO line; non-PO manual source labelled; changed source revision |
| QA quantity | 100 source/20 inspected decision, two lines each 70 against same 100 refused unless legitimate explicit reinspection; UOM mismatch; classification sum; split result; no uninspected auto-accept |
| Record-only links | Unlinked `qaCase` URL cannot fake a document; link to draft→POST→ROLLBACK→repost→delete; CN-owned VR; multiple actions linked one target; linked posted is not `EXECUTED` |
| Permissions | Access/Add/Edit/Submit/Approve/Reject/Delete/Close/missing permission; UI and service independent, direct forged requests and cross-tenant/branch denies |
| History safety | QA absent, partial schema, probe denied; old ON policy, active physical/logical holds, sealed allocations and rollback continue unchanged on old docs; do not wipe/disable |
| Stock neutrality | In normal record-only cases: zero changes to `IvBalLoc`, `ProductionBalLot`, inventory history, `CostLayer`, FIFO/WAC/Standard, PO received qty, Daily Production counters, FG posted qty and posting coordinator facts |
| Existing ERP regression | Normal GR/MR/SC/VR/CN/MI/TR/ADJ/Count, WI/FG post/rollback/future-date/month-end behavior unaffected; pay attention to concurrent costing/replay branch |
| Performance/UI | Bounded SQL case paging and KPI queries, 10k+ lookup, 1440/1024/768/390, 125% zoom, keyboard, tablet, light/dark, popup footer, stale/conflict messages |

**Proof requirements:** Exact test commands, branch+commit, passing/failed/skipped, SQL Server test DB version/fixture, code diff allowlist, service-level access matrix, screenshots per major size, and explicit list of deferred physical/costing features. SQLite-only “pass” cannot certify SQL Server concurrency or deployment. Known baseline failures must be reproduced and separated from regressions; **do not waive new failures**.

**Staged record-only release**: deploy QA-only UI/service after approved DBA schema check; enable per-company **menu access only** as optional feature; leave all five `QaPolicy` posting flags unchanged/frozen. Train staff on record-only semantics. Backout = hide QA menus/disable new QA case entry via permissions while preserving all QA data and **every existing historical hold/rollback enforcement**. Never back out by dropping tables or blindly setting flags OFF.

**Phase 5 gate:** `RECORD_ONLY_QA_READY` only when all mandatory tests, tenant evidence and operator UAT pass; `STOCK_CONTROLLED_QA_READY` **must remain FALSE**. **STOP for human sign-off before customer rollout.**

---

# F. Non-costing feature acceptance by source (no physical disposition claim)

| Source / outcome | QA screen behavior | External action | Truthful verified result | Must remain blocked/deferred |
|---|---|---|---|---|
| Incoming PO ACCEPT | Inspect and approve a selected decision qty | Open existing GRN entry; operator enters/posts | QA case approved; GRN posted **only if actual source is independently verified**, link is advisory | QA-enforced GRN accepted-qty claim, split received quantities |
| Incoming pre-GRN REJECT | Log supplier reject qty/reason; assign supplier follow-up | Supplier replacement/credit communication | Follow-up administratively completed | Vendor return/SC of unreceived stock |
| Posted GRN REJECT | Log qty/defect, link post-GRN batch+detail | Existing VR or purchase CN owner | Target status checked; **not** QA allocation | Authorizing consumption from QCHOLD, automatic CN/VR posting |
| Posted MR / warehouse stock REJECT | Log exact posted MR/pile identity | Existing Scrap document | Linked SC posted, without claim of QA-reserved qty | New stock hold/release/consumption |
| WIP PASS | Approve inspection record | Navigate WO/current operation | QA record approved | Automatic WIP movement |
| WIP REWORK | Rework task, owner/due/notes, later reinspection | User follows existing WO process manually | Rework task/reinspection linked, quantity not posted by QA | Automatic return/rework lot/cost allocation |
| WIP HOLD | Hold follow-up task, visible warning | Manual coordination; historical reservation preserved | Follow-up status shown | New logical reservation activation |
| FG staging ACCEPT | Approve source lot inspection | Navigate FG receipt | FG document status only; QA grant not claimed | QA compulsory FG posting gate |
| FG staging REJECT/HOLD | Track corrective action on staging | Manual existing workflow | Follow-up status | QCHOLD, inventory scrap of unreceived FG |
| Daily Production non-good | Inspect existing non-good counter record | Existing WI owner remains authoritative | QA case/action status only | Non-good posting grant, valued lots, second PRODUCE |

---

# G. Data/schema and transactional rules

## G1. Existing objects remain canonical

`QaPolicy`, `QaCase`, `QaCaseLine`, `QaInspection`, `QaDispositionLine`, `QaAuditEvent`, the existing QA grant/hold/allocation objects and `QaSchemaReadiness` remain the foundation. Do not duplicate grant engines or add a parallel reserved-stock balance. Reuse `MsRef`/existing lookup and document-store patterns if suitable.

## G2. Optional QA-only tables (design after Gate 0; no automatic implementation)

**`QaCorrectiveAction`** (minimal): `Uid`, company, branch, CaseId, CaseLineId, ActionNo/Kind, requiredQty/decisionUOM (informational only), description, owner/role, due date, status, source/reinspection case reference if valid, completion/waiver reason, audit timestamps, RowVersion; FK only to QA. Action qty is **not stock/WIP on-hand, reservation, cost or posted quantity**.

**`QaDocumentLink`** (only if not represented by existing QA metadata): `Uid`, company, branch, action/case line ID, target doc type, stable target header ID and target line ID, revision/generation where available, operator verification state/time, created/modified/audit and RowVersion. Do not maintain a cached `EXECUTED` flag; derive authoritative actual posted status from source document read model. Restrict one *active* identical link per action+target generation; permit many actions to same target only with explicit warning and review. No FK to core stock document tables unless independently reviewed and proven safe (default **no**).

**`QaInspectionAttempt`** only in a **separate approved QA-only enhancement** if true first-attempt KPI, sampled inspection or multi-attempt-in-one-line become mandatory. Until then do **not** fabricate first-pass history from current single-row inspection model; use reinspection cases for operational tracking.

Any approved QA schema must have SQL Server check/unique/FK/index constraints, tenant scope on every query, exact-identity conversion/backfill plan for existing data, `RowVersion`, idempotent apply scripts, least-privilege catalog readiness and a no-drop backout. Do not silently change existing fifteen-table readiness contract to reject all current installed tenants; if a QA-only feature table is added, make its readiness **optional and version-aware**, not a blanket new requirement for core QA reads without a rollout strategy.

## G3. QA-only atomicity

- All QA writes use existing scoped EF context and proper SQL transaction, with QA case concurrency/version/unique guards. No shared writable static state and no cross-company fallback.
- `Draft save` cannot create inventory, WIP, grants or holds. `Inspect`/`Approve` cannot post or reserve. `Corrective-action completion` cannot move stock. `Document link` cannot mutate target document. `Close` cannot erase outstanding historical stock obligations.
- On failed QA case write, there is **zero partial QA audit/action/link mutation**. On a failed business document post/rollback, this plan changes no posting behavior and must not forge QA status from a UI event.
- Do not hold an EF transaction across user dialogs, files or external network calls. Enforce server identity and concurrency at each command.
- For a tenant with historical existing ON gates, follow preserved stock authorization/rollback behavior and freeze new QA rollout until safety review. No automatic policy OFF toggle, no release-as-workaround.

---

# H. UI action/permission matrix

| Action | Allowed status | Existing permission to inspect/use | Service-side rule |
|---|---|---|---|
| View cases/source | Any accessible case | QA Workbench `Access`, plus target document `Access` for navigation | company+branch only |
| New case | New | QA Workbench `Add` | source integrity and QA schema readiness |
| Edit / Save draft | `DRAFT` | `Edit` (or `Add` for new) | required version for existing, source unmodified/stable |
| Submit | `DRAFT` | current repo-defined `Edit` for Submit (verify Gate 0) | at least one valid decision line; no partial QA write |
| Inspect line | `SUBMITTED`/`IN_INSPECTION` | `Edit` unless actual dedicated Inspect right exists | active source, version, valid result/decision qty |
| Submit for review | `IN_INSPECTION` | `Submit` | all decision qty fully classified and inspector recorded |
| Approve | `PENDING_REVIEW` | `Approve` | cannot approve if actor inspected **any contributing line** when separation ON |
| Reject review | `SUBMITTED`/`PENDING_REVIEW` as current service supports | `Reject` **independent** of Approve | reviewer reason, audit; no physical reject |
| Cancel QA case | `DRAFT`/`SUBMITTED` | existing `Delete` as current service | distinct from editor Cancel, no deletion of approved audit |
| Delete draft | `DRAFT` only | `Delete` | no linked historical obligations/action references |
| Close case | `APPROVED`/`REJECTED` | `Edit` | all mandatory QA actions done/waived; existing holds remain enforced |
| Release historical hold | Only where existing service permits, with active hold | existing `Approve` | **no new release semantics or stock writer edits**; exact legacy obligations preserved |
| Link existing target | Allowed approved case/action with no hold bypass | QA `Edit` or assigned action authority plus target `Access` | record-only; target status read under tenant scope; never POST |
| QA Settings | QA settings route | `Access`/`Edit` | five stock posting gates remain frozen, existing ON history never blindly disabled |
| QA Inquiry/export | QA inquiry route | `Access` plus existing export right if applicable | server scoped, no price/cost data inferred |

Use exact current `PermissionCodes` names at Gate 0. Do not invent `Inspect`, `Close` or `ReleaseHold` permission constants simply because they sound desirable. When service-level `SubmitAsync` currently uses `Edit` but UI checks `Submit`, reconcile to **one explicit repo-defined policy**; update UI and QA case workflow service only, not global permission semantics without an approved menu migration.

---

# I. Regression / acceptance ledger

Every PR must include these artifacts under `docs/qa/`:

1. `qa_phase_<N>_implementation_evidence.md`: branch/HEAD, git diff, exact changed files, risks, feature statuses, test commands and results, screenshots when UI, and STOP gate.
2. `qa_permission_matrix.md`: every action, service checks, page visibility and direct-call denial tests.
3. `qa_source_identity_matrix.md`: per source type, real owner IDs, UI route IDs vs server database IDs, source revisions, legitimate manual records and stale/error cases.
4. `qa_quantity_and_attempt_rules.md`: chosen QA decision qty, per-line inspection/dispositions, partial/reinspection and aggregate validation, report denominators.
5. `qa_tenant_readiness.md`: presence/partial/probe error, policy flags, old holds/allocations; **no secrets or real customer data**.
6. `qa_link_verification_contract.md`: what the screen can independently prove from existing documents and what it cannot prove about QA stock.
7. `qa_uat_sqlserver_results.md`: exact fixtures, two-session races, statuses, baseline failures/skips and repeatable commands.
8. `qa_record_only_release_and_support.md`: user instructions, scope exclusions, optional menu enablement, DBA-only install, no-drop backout, rollback/historical protections.

**Accept non-costing QA release only if ALL are true:**

- [ ] Every supported case is bound to a correctly verified real source or unmistakably labelled manual/unlinked inspection; BatchNo/Id mismatches and missing output IDs are rejected correctly.
- [ ] Case quantities and inspection quantities are conserved within QA decisions, including repeated source-line aggregations, partial source inspection and honest reinspection tracking.
- [ ] Independent reviewer cannot approve any case line they inspected when separation ON; all role/tenant/branch constraints pass SQL Server tests.
- [ ] Stale/missing RowVersion, concurrent approval, duplicate commands and QA-only DB failures leave correct case/action/audit state.
- [ ] Workbench, Case, Settings, Inquiry and source navigation are professionally aligned, responsive and easy for a normal ERP clerk to use. Real browser visual UAT completed.
- [ ] ACCEPT, REJECT, REWORK, HOLD create understandable QA decisions and required administrative follow-up. Never claim physical transaction creation by QA.
- [ ] Linked existing GRN/SC/VR/CN/WI/FG documents are accurately described; rolled-back/missing/changed documents downgrade link state; no `EXECUTED` without an existing sealed allocation.
- [ ] Existing QA table and provenance safety, legacy ON holds/allocations, stock rollback guards and normal non-QA posting behavior remain unchanged.
- [ ] QA inquiry is SQL-page bounded with truthful KPI names/denominators; no false First-Pass Yield or stock totals; exports do not expose unauthorized data.
- [ ] No diffs in prohibited stock/cost/production posting files, no posting policy gates activated, no customer SQL changes, no unreviewed QA schema changes.
- [ ] Build, focused tests, SQL Server concurrency tests, regression and UAT documented against actual final commit. Unrun/failed cases explicitly block the corresponding gate.

**Stop/rollback trigger:** any stock/WIP/cost/posted document mutation from QA-only steps, permission/tenant leak, wrong source identity, duplicate/partial QA case write, previous ON hold lost, reporting `EXECUTED` without proof, or a prohibited file diff. Restore the QA feature branch without dropping customer history or modifying costing; document and review.

---

# J. Deferred STOCK/POSTING integration — DESIGN-ONLY dependency register

**These are NOT coding tasks in this plan.** They require a **separate future AI agent plan** after the costing/replay branch is stable, with explicit owner signoff and independent stock/WIP/ledger regression.

| Future feature | Existing seam worth re-checking | Why blocked now | Minimum future proof |
|---|---|---|---|
| QA-authorized GRN | `IQaReceiptAuthorization.ClaimGoodsReceiptAsync` / `RestoreGoodsReceiptAsync` | No trustworthy shared delivery-instance identity; frozen gate; on/off affects whether GRN posts | GRN immutable delivery identity, partial per-line accepted qty, same transaction, POST/rollback/repost, FIFO/WAC/Standard + historical replay, SQL Server races |
| QA-authorized FG | `IQaFgAuthorization.ClaimFinishedGoodAsync` / Restore | FG pool/revision/base UOM and frozen gate; downstream WIP/inventory dependency | exact FG lot/generation, partial receipt, rollback and downstream tests |
| New WIP QA hold | `IQaLogicalHoldAuthorization` + Production stock writers | New reservation affects available WIP; output/material rollback/consumption and concurrent costing are changing | all WIP depletion routes covered, no bypass, release/rollback/period tests |
| New physical stock hold | `IQaPhysicalHoldAuthorization` | New reservation prevents stock out; current SC/VR has no trusted QA case lineage and no new allocation claims | exact pile + case-line identity, all stock-out/count/transfer/replay routes, reservation and rollback proof |
| QA-controlled SC/VR/CN | Existing SC/VR and purchase CN-owned posting | Manual link does not authorize a reserved quantity; possible duplicate posting ownership | persisted QA link in authoritative transaction; sealed allocation claim/rollback with CN-owned VR distinction |
| Non-good WI posting gate | `IQaNonGoodAuthorization.ClaimDailyProductionAsync` / Restore | Non-good counts remain counters; frozen gate; WI revision & downstream production invariants | immutable WI counters/generation and SQL Server post/reverse races |
| Quarantine `ACTIVE→QCHOLD` | No existing dedicated QA conversion owner | Would change physical status identity and require cost/ledger replay | new separately designed ledger-safe reclassification and all costing method tests |
| Valued reject/scrap WIP or FG lot | Production stock/absorbed conversion costing | Production currently preserves one PRODUCE movement and particular WIP/FG lifecycle | independent production/costing design, conversion cost allocation proof, downstream rollback reconciliation |

**Policy:** Never infer permission to start these features from a successful record-only QA UI review. `Record-only QA complete` is a valid separate product milestone; **“QA integrated posting complete” is not**.

---

# K. Proposed phase commits / stop gates

| Stage | Suggested PR | Files expected | Gate decision |
|---|---|---|---|
| G0 | `qa/g0-repository-evidence` | Evidence only | Human review of current checkout and tenant/legacy hold policy |
| P1 | `qa/p1-case-source-security` | QA case/source/DTO/tests, minimal UI permission | SQL Server concurrency + tenant/source correctness |
| P2 | `qa/p2-operator-ux` | Quality UI + permitted source button presentation | Browser/tablet/operator UAT |
| P3 | `qa/p3-corrective-action-links` | QA action/link service/entity (only if approved), inquiry/UI/tests | No phantom stock/verified execution |
| P4 | `qa/p4-kpis-support` | Read-only QA queries/report UI/support docs | Paging+KPI correctness and role/export proof |
| P5 | `qa/p5-uat-record-only-release` | Tests/evidence only | **RECORD_ONLY_QA_READY** only; still no stock gates |
| DEFERRED | Separate future plan | No implementation here | Costing team and user approve later |

**Mandatory agent final output at each gate:** actual current commit and diff; exact files; concrete tested flows, failures/skips; deferred blockers; recommendation **PASS / BLOCKED / FIX REQUIRED**; stop. No claiming “10/10” from assumptions or previously archived test artifacts.

---

# L. Current-source references used for this v2 correction

All from `productionv2` commit `584411e317ac6990ed8a5f0886207779d821deaf`:

- `ErpWeb.Core/Quality/{QaCaseService,QaSourceResolver,QaSourcePrefill,QaDestinationComposer,QaPolicyService,QaSchemaReadiness,QaCodes,QaDtos}.cs`
- `ErpWeb.Core/Quality/{QaReceiptAuthorizationService,QaFgAuthorizationService,QaLogicalHoldAuthorizationService,QaPhysicalHoldAuthorizationService,QaNonGoodAuthorizationService}.cs`
- `ErpWeb.Model/Entities/Quality/QaFoundation.cs`; `ErpWeb.Model/Configurations/Quality/QaFoundationConfiguration.cs`
- `ErpWeb.UI/Quality/{QaCaseEntry,QaWorkbench,QaSettings,QaInquiry}.razor[.cs,.css]`, `QaHowToUsePopup.razor`, `QaRecordOnlyBanner.razor`
- `ErpWeb.UI/Inventory/Transactions/{IvGoodsReceipt,IvMiscReceipt}.razor.cs`
- `ErpWeb.UI/Planning/WorkOrders/{PrDailyProductionEntry,PrFinishedGoodReceiptEntry}.razor.cs`
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`, `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.Posting.cs` **read only**
- `ErpWeb.Tests/Quality/*`, `plans/qa_safety_fix_F0_repo_and_schema_matrix.md`, `plans/qa_safety_fix_F1_schema_readiness.md`, `plans/qa_safety_fix_F2_source_generation.md`, `plans/qa_safety_fix_F3_hold_invariants.md`, `plans/qa_safety_fix_F4_professional_ui.md`, `plans/productionv2_qa_replay_integration_20261104.md`

**Important evidence limitation:** This plan review used the current remote repository and the previously produced v1 plan. It did not read a local dirty working tree, query real customer databases, run the application, open an authenticated browser, or run tests. Those are mandatory Gate 0 / Phase 5 conditions; do not infer them from GitHub source or historical plan notes.

**END — v2.0 / NO COSTING, NO STOCK POSTING CHANGES / HUMAN GATE REQUIRED**
