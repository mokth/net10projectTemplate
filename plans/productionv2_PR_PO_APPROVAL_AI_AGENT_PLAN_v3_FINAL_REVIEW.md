# productionv2 — PR/PO Approval Workflow — AI Coding Agent Plan v3 — Final Design Review

**Status:** DESIGN REVIEWED; IMPLEMENTATION APPROVAL WITHHELD — CURRENT BRANCH CODE NOT VERIFIED.
**Review verdict:** Architecture and safeguards strengthened; **not APPROVED FOR IMPLEMENTATION — 10/10** until Phase 0 is completed against current `productionv2` source and all critical paths are traced. Do not mistake design acceptance for repository-grounded approval.
**Target:** `mokth/net10projectTemplate`, branch `productionv2`.
**Scope:** Purchase Requisition (PR) and Purchase Order (PO) only; reusable extension points for other documents later.

## Review corrections from v1–v2
1. Separate **approval policy applicability** from **transaction permission**. Disabling a policy must not silently unlock a previously submitted document; preserve its frozen cycle or require an authorized administrative resolution with audit.
2. Do not automatically block every print or GRN endpoint: establish exactly which existing transitions create a commitment or downstream document, and guard at the authoritative service boundary. Read-only preview/printing can remain available if permitted.
3. Policy amount thresholds must use the *existing authoritative amount and currency snapshot*, including tax/discount and FX semantics; never calculate a competing document total.
4. Define no-approval explicitly as an auditable **Not Required** outcome; it is not the same as Approved, and cannot arise from missing configuration.
5. Do not use a generic fingerprint alone as security. All write paths must enforce editability and revision checks under the same database transaction; a fingerprint is supplementary evidence.
6. An approval can never retroactively authorize a PR already consumed or a PO already received. Legacy treatment requires a documented cutover.
7. Avoid a single broad approval permission that lets an assignee approve all documents; require both module/action authorization and active assignee membership.
8. If PR lines are partly converted, approval changes must not reset quantities, reopen consumed lines, or alter remaining-to-order calculations.
9. Do not assume ANY/ALL group membership semantics: resolve to distinct active user IDs at submission; define absence, disabled users, and mid-cycle deactivation behavior.
10. Keep optional delegation out of the initial release unless current repository provides a tested delegation mechanism.

## Final review — mandatory design decisions (v3)
1. **Lifecycle truth table:** Phase 0 must produce a state/action matrix for PR and PO: New, Edit, Delete, Submit, Withdraw, Approve, Return, Reject, Cancel, Print, Send, Import, Convert, Receive, Invoice, Rollback. For each state, record whether the action is allowed, its authorization, approval requirement, and exact service enforcement point. Do not treat print as a commitment unless code demonstrates that it is.
2. **Effective policy resolution:** At submit, resolve one enabled policy version and one matching band using authoritative persisted amount and existing FX/base-currency logic. A deliberately configured no-approval rule produces a persisted `NotRequired` outcome; missing rule is a configuration error. Avoid a synthetic approval instance if the repository already has a safe audit mechanism; retain proof of exemption either way.
3. **Disabled and legacy documents:** Policy defaults OFF. Enabling must not retroactively hold existing already-progressed/converted/received documents. Drafts and in-flight records require an explicit per-status cutover matrix, accepted by business owner before migration. Disabling only affects newly eligible cycles; previously pending cycles continue until explicitly resolved with audit.
4. **Downstream atomic guard:** Approval check and PR-to-PO consumption or PO-to-GRN transition must occur under the existing atomic transaction/concurrency pattern, or otherwise recheck at the authoritative commit point. Never check only at page load or before a separate unguarded commit.
5. **Material revision:** Revision must be incremented or approval invalidated on every material write path, including line edits, header totals, vendor, discounts, currency, tax, imports, batch updates and recalculations. If the existing schema cannot support this, use the narrowest compatible transactional validation approach. Nonmaterial comments must not force reapproval unless current business policy requires it.
6. **No ambiguous rejection behavior:** `Return` and `Withdraw` may reopen editable drafts only when existing downstream constraints permit; `Reject` ends the cycle and must require explicit authorized rework/resubmission (never auto-reopen or auto-approve). Maintain all prior cycles.
7. **Approval assignment:** Resolve distinct active users at submit. For ANY, exactly one successful decision completes the step; for ALL, every distinct active assignee must approve. If assignee becomes inactive, block and require audited reassignment with appropriate permission. No user can approve own document by default, including when assigned through a group.
8. **PO amount change:** A PO imported from approved PRs is still independently approved. Any PO amount/vendor/line change before downstream consumption invalidates its approval; after downstream consumption, use only existing permitted amendment/reversal processes, never mutate receipt history.
9. **Tenant, ownership and audit:** Use current tenant context, current user identity, role permission and assignment on each operation. Append-only events must contain who, when (UTC with local display), what, old/new state, document revision, cycle and reason. Sensitive comments must follow existing visibility rules.
10. **Operational scope:** No generic BPM designer, arbitrary expressions, external notifications, bulk approval, escalation scheduler, or custom delegation in first release. Configuration UI should fit two tabs, simple bands and 1–3 sequential levels; common inbox and inquiry only.
11. **Deadlock and failure recovery:** Decide lock ordering between document, approval instance, and PR line balance using repository conventions; test deadlocks, lost updates, duplicate submits, approval/return races, and network retries. Ensure no orphaned pending cycles after failed commit.
12. **Audit-safe administration:** Provide a minimal permissioned, reason-required reassignment/recovery action for unavailable approvers; do not permit a silent force-approve, direct SQL status flip, or rule edit that changes in-flight membership.

## 0. Mandatory discovery gate — DO NOT CODE until completed
The GitHub repository and `productionv2` branch were identified, but the connector could not retrieve branch source files during preparation. Therefore **no PR/PO code paths, entity names, permissions, state values, migrations, or service methods are verified**. The coding agent must inspect the *current checked-out productionv2 source* and record exact paths and signatures for:
1. PR and PO list/entry UI, DTOs, entities, services, create/edit/delete/submit/print/cancel flows, status enums, numbering, validations.
2. PR→PO import/selection, partial conversion, multiple POs, line remaining quantities, vendor selection, and PO downstream GRN/invoice interactions.
3. Current authorization/role/user/company/tenant model; permission checks; audit/history; transaction boundaries, DbContext patterns, EF migrations and concurrency tokens.
4. Any existing approval infrastructure (including stock-take or zero-cost approvals), determine if reusable; do not create a parallel engine when one already exists.
5. PR/PO document total, currency, tax, discount, base/local currency, rounding and change tracking. Determine existing authoritative amount; do not recompute.
6. All routes to advancing PR/PO state: UI, service, background jobs, APIs, import/bulk actions and direct transitions.
7. Existing posted, closed, cancelled, reversed, partial, and historical document behaviors.

Produce a short **Repository Evidence Map** listing verified file paths, code references, statuses, invariants, and unresolved decisions. If any critical path is unverified, STOP and request clarification/access. Do not invent architecture.

## 1. Objective and scope
Build a simple, configurable, auditable approval gate for PR and PO that is easy for Malaysian SME users. Reuse current architecture and UI conventions. Approval is an authorization to proceed, NOT an accounting, inventory, or costing transaction.

In scope: per-document enable/disable, amount bands, 1–3 sequential levels, user or existing group/role approvers, ANY/ALL per level, submit/approve/reject/return/resubmit, delegation deferred unless already safely supported, inbox, history, lightweight KPI, access checks, edit invalidation and downstream guards.

Out of scope: generic BPM designer, scripting, arbitrary condition trees, new SO/DO approval integration, email infrastructure, mobile push, ERP-wide status redesign, GL, FIFO, CostLayer, stock/WIP quantity, tax or posting algorithm changes.

## 2. Business policy
- Approval is configurable separately for PR and PO per tenant/company. Disabled means current behavior must remain unchanged, including legacy documents.
- Configuration should have simple amount bands in company base currency and a default catch-all policy; avoid hardcoded RM thresholds. Provide clear preview showing which rule applies.
- Level count: 1–3 sequential. Each level has explicit users or existing groups/roles, with ANY (one authorized approver) or ALL (all assigned approvers). If dynamic groups are used, snapshot eligible members on submission; reject empty memberships.
- Avoid overlapping bands, gaps, ambiguous boundary inclusivity; validate configuration on save. Decide/document inclusive lower/upper bounds, base-currency conversion policy and rounding from existing authoritative totals. Do not introduce a new FX rate source.
- No approval needed is a deliberate explicit rule; it must not be an accidental fallback when no policy matches. Fail closed when enabled but no valid matching rule exists.
- PR and PO are independently approved. PR approval does not imply PO approval. PO with no PR is supported if existing PO flow permits it. PR without vendor must remain selectable in PO according to current business rules; approval must not introduce a vendor prerequisite for PR.
- On submit, snapshot policy/version, resolved distinct active assignees, document total, currency, and document revision; optionally record a canonical material-content fingerprint for diagnosis. Subsequent configuration edits must not silently change in-flight approvals.
- Approval status is orthogonal to existing procurement processing status where possible. Avoid renumbering existing enums or changing current workflow statuses without repository evidence.
- Creator cannot approve own document by default; configurable exception only if explicit existing permission and company policy support it. Prevent approval via alternate accounts/identity confusion only to extent supported by current identity model.
- Approve only current active level. ANY closes that level on first approval; ALL requires each distinct assigned user. One user must not satisfy multiple ALL assignments unless explicitly intended and recorded.
- Reject ends current approval cycle with reason; Return for Correction releases to edit with reason; resubmit starts a new cycle and preserves history. Rejection vs return semantics must be clear in UI.
- Allow withdrawal before completion by creator with authorization; never silently withdraw an already approved and consumed document.
- Editing material fields (lines, quantities, price, vendor, currency, discounts, taxes, delivery terms where relevant) after submission is blocked. Authorized return/withdraw is required first. If any approved document is edited via supported workflow, approval must be invalidated and resubmitted **only when downstream state permits editing**; never rewrite consumed/posted documents.
- Prevent approving deleted/cancelled/closed/ineligible docs; enforce company/tenant isolation and user permissions at service layer.
- Approval may not bypass existing period, stock, pricing, tax, vendor, or procurement validations.

## 3. Data design — adapt to current repo patterns
Prefer minimal entities/tables; use existing approval infrastructure if present. Conceptual data:
- ApprovalPolicy: Tenant/Company, DocType (PR/PO), enabled, version, effective status.
- ApprovalRule: Policy, lower/upper amount in base currency, priority or non-overlapping bands, explicit no-approval flag.
- ApprovalRuleLevel: Rule, sequence 1–3, ANY/ALL, assigned user/group references.
- ApprovalInstance: Tenant, DocType, DocId, cycle/revision, status, policy snapshot, amount/currency/base amount, material fingerprint, submitter/time, concurrency token.
- ApprovalStep/Assignee: level, snapshotted authorized users, decisions/timestamps.
- ApprovalEvent: append-only actor, action, time, reason, previous/new status, instance/cycle, tenant and relevant document revision.
Use existing naming, keys, user identity, timestamps, audit and migration conventions. Unique constraint for one active cycle per (tenant, document type, document ID). Unique assignee decisions per cycle/level/user. Index inbox by tenant, assignee, active status. Enforce FK strategy compatible with tenant DB architecture. No speculative schema migration until evidence map complete.

## 4. Transaction flow and safeguards
**Draft → Submit → Pending Level 1 → [Level 2 → Level 3] → Approved → Existing PR/PO downstream processing.**
Rejected/Returned/Withdrawn → editable only if existing document state permits → resubmit new cycle.

Every submit/approve/reject/return/withdraw and related document gate must be atomic using existing transaction strategy. On submit: lock/read current document, verify editability, existing validations, policy, total and fingerprint; create immutable approval snapshot and active instance; update approval gate in one transaction. On decision: verify actor/tenant/permission, active instance, current step, document revision, and expected concurrency version; write decision/history and advance status atomically. Use optimistic concurrency or repo-standard locking to reject double clicks and simultaneous approvers safely. Make retries idempotent where appropriate, never produce duplicate approvals.

**Mandatory guard:** integrate at the existing service/business transition boundary for all routes that allow PR conversion/PO creation, PO confirmation/printing/sending/GRN receipt or other downstream commitment, as identified by the repository. Do not assume a particular existing state named POSTED. A draft PO may be saved before approval; which transitions are blocked must be explicitly mapped from current code. No UI-only enforcement. On PR→PO conversion, require PR approval when enabled and preserve remaining-quantity, partial-conversion, vendor and source-link semantics. Existing PO standalone flow remains supported. Check stale approval/revision at the final transition under concurrency control. Fail closed for enabled policy with missing or stale approval.

Do not allow policy disabling, document mutation, import, API, or status shortcut to bypass approval. For policy disablement, define effect on in-flight documents explicitly (recommended: retain current active cycles; disable affects new submissions only). Historical/legacy documents must not be retroactively blocked without an explicit migration policy.

## 5. UI/UX
- Approval Settings: two tabs PR/PO; Enable toggle, clear amount bands and levels, approver lookup, ANY/ALL selector, preview, validation. No technical codes for users to memorize.
- PR/PO entry: keep existing layout and toolbar; add Submit for Approval and approval status badge; show current approver(s), submitted date, comments and View History. Buttons reflect actual permissions/state; explain disabled reasons in plain language.
- Common My Approvals inbox: Pending Mine (default), All Pending (permissioned), Completed; document no/date/requester/vendor/amount/current level/age; row actions Approve, Return, Reject, View. Open document read-only before decision. Mobile/tablet responsive, concise columns and efficient batch only if independently safe; prefer no batch in v1.
- Approval action dialog: document summary, key changes, decision, mandatory reason for rejection/return; confirm and show success or actionable failure.
- History: chronological actor, action, date/time, level, reason, cycle and policy version; immutable and tenant-filtered.
- KPIs: pending by PR/PO, overdue by configurable business-day threshold (or simple calendar days if repo lacks calendars), average approval time, approved/rejected counts. Distinguish cycle metrics from document metrics and avoid duplicate counts.
- Reuse current DevExpress/Blazor page patterns, lookup, spacing, toolbar, authorization, localization and error messages. Do not create a unique UI framework.

## 6. Permissions
Separate permissions: ConfigureApproval, SubmitPR/PO, ApprovePR/PO (plus assignment), ViewOwnApprovals, ViewAllApprovals, ViewApprovalHistory, Withdraw (subject to document ownership/policy). Reuse current permission codes and role model; do not create duplicate permission systems. Authorize both page and service methods. An assigned user without appropriate approval permission cannot approve. Never expose another tenant's documents or approver lists.

## 7. Phases and mandatory gates
**Phase 0 — Repository evidence and design alignment:** inspect current branch, write evidence map, verify exact transition guard points and current approval reuse; STOP if uncertain. No business logic changes.
**Phase 1 — Configuration and approval engine:** minimal migrations, rule validation, snapshotting, service authorization, concurrency and audit; unit tests. Gate: deterministic selection, no gaps, no duplicates, no cross-tenant access.
**Phase 2 — PR integration:** submit/return/approve, service-level PR downstream guard, PR→PO partial/standalone cases, entry UI and tests. Gate: existing PR behavior unchanged when disabled.
**Phase 3 — PO integration:** independent PO approval, all actual downstream transition guards, entry UI and tests. Gate: no GRN/commitment bypass when approval required; no costing/quantity modifications.
**Phase 4 — Shared inbox, inquiry, KPI, regression and UAT:** common UX, audit, permission matrix, real SME scenarios, migration/rollout guidance. Gate: acceptance checklist signed off.
Each phase must report files changed, DB migration, tests run, risks and exact gate results; STOP on failed gates. Do not silently proceed.

## 8A. Additional strict correctness gates
- **Approval scope/cutover:** record when the policy takes effect. Distinguish documents created before enablement, drafts submitted after enablement, and in-flight cycles. An explicit, audited cutover decision is required; never infer approval from an old status.
- **Amount policy:** document whether inclusive tax, freight, discount, and currency conversion participate, based on the actual persisted PO/PR total. If totals can change outside the document service (e.g., recalculation/import), ensure approval revision is invalidated or the operation is denied.
- **Config edits:** validate complete bands and approver coverage *before publishing* a new policy version. Existing instances remain bound to prior snapshots. Do not delete users or policies needed for audit.
- **Approval actor:** check current active account, tenant, permission, resolved assignee, current level, and no-self-approval policy on every decision. If an assigned account becomes inactive, block it and provide an audited administrator reassignment process; do not silently approve.
- **ALL vs ANY:** in ALL, all distinct assignees must approve. In ANY, one vote advances once. Reject/return are terminal for that cycle; concurrent approve-vs-reject is serialized so exactly one valid outcome wins.
- **Crash/retry:** transaction commits the decision, event, current step, and gate together. A retry of the same request returns the existing outcome or a clear stale-decision response; no double events.
- **Bypass inventory:** inventory/GRN services must not independently duplicate approval calculations. Check the PO gate through an existing procurement boundary or a narrow shared guard; avoid modifying FIFO/CostLayer or receipt math.
- **Cancellation:** specify whether cancellation of pending/approved unconsumed PR/PO is allowed under existing permissions; never allow a new approval cycle on cancelled, posted, fully converted, or otherwise ineligible documents.
- **No silent reapproval:** changing approver list or policy while an instance is pending must not change active assignments. Explicit reassignment is a separate audited administrative action.
- **Multi-tenant:** ensure policy, instance, history, inbox, and document queries use the current tenant database/context and cannot be accessed by document ID guessing.
- **Migration:** migration must be additive and backward-compatible. Deploy schema before enabling policies; default OFF; preserve existing records and old API behavior until gated rollout.
- **Operational recovery:** provide clear user messages for no matching rule, no active approver, stale approval, changed document, already processed request, and insufficient permission. Never allow an admin override that skips history.

## 8. Test matrix (minimum)
- Approval disabled for PR and/or PO: original behavior and all existing tests pass.
- Each amount boundary (exact min/max), zero, discount/tax, multi-currency, changed FX if relevant; policy gaps/overlaps rejected.
- No rule, no approver, disabled user, reassigned role, cross-company, self-approval, unauthorized direct service/API calls.
- 1/2/3 levels, ANY/ALL, concurrent votes, duplicate click, stale instance, role changes after submission, config version changes mid-flight.
- Draft edits, submitted edits blocked, return/edit/resubmit, reject/reopen, withdrawal, cancellation, delete, already-converted PR, partially converted PR, PR split across POs, PO with/without PR.
- PR without vendor, PO supplier selection, multi-vendor purchase conversion if currently supported, changes to PO vendor/amount after approval.
- PO downstream GRN/invoice and any other discovered transition, partial receipts, cancellation/reversal, posted/closed periods, historical documents.
- Atomic failure injection: approval history + status + document state all commit or all rollback; duplicate approvals impossible.
- Tenant isolation, page/API permission enforcement, accessibility, localization, empty inbox, large datasets, performance.
- Existing stock quantities, FIFO/CostLayer, WIP, tax, document totals, posting and rollback must remain unchanged; compare before/after regression results.

## 9. Acceptance criteria
1. SME administrator configures PR/PO rules without developer changes.
2. Submitter sees clear approval state and can track decisions.
3. Approvers can act from one inbox; ANY/ALL and level sequencing are correct.
4. All service-side transition paths reject unapproved or stale PR/PO where required; no bypass through UI/API/import.
5. PR and PO approvals are independent; PR without vendor and standalone PO behaviors remain intact if supported currently.
6. Material edit invalidation, revisions, cancellation and downstream consumption are safe.
7. Audit is immutable, permissions and tenant isolation verified, concurrency and atomicity tested.
8. Existing inventory/costing/quantity/posting logic and existing disabled-approval behavior are untouched.
9. Repository evidence map, migrations, automated tests and manual UAT supplied.

## 10. Explicit MUST NOT changes
Do not modify CostLayer/FIFO/stock/WIP algorithms, stock balances, posting/reversal, purchase price/tax/rounding calculations, month-end, document numbering, existing PR/PO conversion quantity logic, core permission infrastructure or unrelated sales modules. Do not invent class names, tables or statuses; do not add new workflow engine frameworks; do not rewrite PR/PO services wholesale. Avoid changing existing enum numeric values or historical status meanings.

## 11. Open decisions to resolve from current code before Phase 1
- What are the exact PR/PO existing statuses and business transition gates?
- Is there an existing reusable approval service or only specialized stock-take approval?
- Which total/base-currency amount is authoritative and how is FX fixed?
- How are users/groups/permissions and tenant identity represented?
- What is the desired behavior for active approvals if approval is disabled or rules change?
- Are print/send actions commitments in this implementation? Which are safe before approval?
- Is delegation already supported? If not, defer it rather than adding complexity.

## 12. Mandatory evidence-based approval checklist
Before changing this plan to APPROVED FOR IMPLEMENTATION, attach a current-branch evidence appendix:
- Exact commit SHA, repository/branch, PR/PO UI/service/entity/DTO/status/permission/migration paths.
- Existing approval or stock-take approval reuse decision supported by code references.
- Table of **every** PR and PO mutation and downstream entry point, its existing preconditions, and the exact approval guard placement.
- Current total/currency calculation source and PR→PO remaining quantity source.
- Concurrency/transaction strategy and test fixtures that already exist.
- Evidence that disabled approval preserves original PR/PO workflows, including legacy docs.
- Automated test names/results for Phase 1–4 gates; code review for permission, tenant and concurrency cases.

**Final v3 verdict:** DESIGN SPECIFICATION READY FOR REPOSITORY VALIDATION; **NOT APPROVED FOR IMPLEMENTATION — 10/10**. The current productionv2 branch was identified, but source retrieval was unsuccessful and the state/action guard matrix is unverified. Execute Phase 0 only, then request final repository-grounded review. This is an evidence limitation, not a request to add complexity to the SME workflow.

**Approval release gate:** Approval may be granted only after the evidence appendix maps all critical transitions, reconciles actual schema/status/permissions, and verifies the proposed changes preserve current quantity, costing, posting and rollback invariants. No numeric 10/10 claim is valid before that evidence exists.
