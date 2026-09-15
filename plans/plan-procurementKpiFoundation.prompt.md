# Plan: Procurement KPI Foundation, Malaysia Compliance & Ops Hardening (v8 — seventh review incorporated)

Workspace: c:\wincom\net10projects (ErpWeb.slnx, .NET 10, Blazor + DevExpress, EF Core, SQL Server)
Scope: PR (POPR) / PO (POOrder) / GR+NG (IvTrxBatch) / Purchase Invoice (POInvoice) / CDN (PoCdn)
Exclusions: approval/workflow engine, AP/GL posting, payments, knock-off, AP aging, supplier statement,
and procurement KPI dashboard screens.
Clarified decisions: KPI foundation + Malaysia compliance + ops hardening + sourcing; backfill what is
derivable, report the rest, event log at cutover.

Scope boundary (resolves the v2 contradiction)
- The procurement KPI deliverable is service + DTO + query + test layers only — no dashboard screens.
- Operational, master and document UI changes required to expose new fields, actions and workflows ARE
  in scope: C5, E2, F5, H4, J4 and the browser verification pass.
- Procurement KPI dashboard screens (tiles, charts, dashboards) are OUT of scope.
- Phase I delivers the Malaysia compliance data foundation, validation/state model and integration seam.
  It does NOT certify the ERP as statutory or tax compliant. Still out of scope: AP deduction/posting,
  payment accounting, complete filing ledger, production LHDN integration/certification, and any
  statutory reconciliation that depends on AP/GL.

v2 absorbed the first external review (Phase 0 semantic freeze, dependency correction, event / KPI /
currency / QC / schedule / compliance / deployment semantics). v3 absorbs the second review: scope
boundary, event key precision, append-only enforcement, NG behaviour, QC stock ownership, OTIF /
fill-rate / cancellation semantics, period-control extraction, e-Invoice resubmit and outbox rules,
snapshot immutability, revision semantics, current-vs-as-of cutoff and cross-cutover cohort rules.

v4 absorbs the third review and freezes the remaining business-owned policy values: period-control
scope and blocking behaviour, QC on-hand/costing/valuation policy, OTIF tolerance, fill-rate
cancellation treatment, NG participation in receipt-based KPIs, revision-rate release scope, the exact
`PoDocEvent` schema and event-code vocabulary, `OperationId` retry/re-entry semantics, the PO
local-to-UTC historical migration strategy, the cutover timestamp, and the Malaysia-compliance
foundation-vs-statutory boundary.

v5 closes the remaining semantic-freeze contradictions and adds the Phase 0 reference tables: the
e-Invoice state vocabulary (`INVALID` removed), the concrete outbox attempt model, factual completion
decoupled from OTIF tolerance, effective-dated KPI policy, logical-PO vs revision identity,
revision-rate denominator, period-audit destination, NG scope matrix, DB-enforced event vocabulary,
PO receipt-to-invoice lag matching, return-rate rollback treatment, fill-rate edge cases, and the
cutover runbook.

v6 freezes the outbox identity and claim contract and closes the promise-history hole: the outbox splits
`SubmissionOperationId` from `SubmissionAttempt`, gains an exact column contract and atomic worker-claim
semantics, and gets an explicit state-responsibility matrix against the invoice e-Invoice state.
`OriginalPromisedDate` becomes immutable at issue with promise-change history, `RequiredDate` is frozen
as a PO snapshot, receipt-to-invoice lag aggregation and the return-rate denominator are pinned, and
event ordering, `IsActive` and policy-overlap constraints are specified.

v7 removes the last "either/or" ambiguities: promise history is a single authoritative table with an
explicit OTIF promise-version rule, `RequiredDate` is immutable within a revision, commercial dates are
separated from commitment dates, the outbox gains frozen terminal states, legal transitions and retry
ownership, period-lock versus posting concurrency is made atomic, and `Company.TimeZoneId` is recorded as
verified.

v8 closes the two remaining P0 contract contradictions and hardens the promise/outbox invariants:
`PO_PROMISE_CHANGE` is added to the frozen event vocabulary so the `PoDocEventCode` foreign key can
accept it, the outbox "immutable attempt row" wording is replaced by field-scoped ownership (immutable
identity/payload, lifecycle fields mutable only through legal transitions, response fields write-once),
and `PoPromiseHistory` gains a primary key, an idempotency key and a deterministic cutoff tie-break.
Same-save revision-plus-promise atomicity, the pointer/outbox atomic write, the provider idempotency
seam, the `OperationId` non-reuse rule and the `PoRelNo` numbering convention are frozen.

---

## TL;DR

Before any code, freeze the business semantics (Phase 0). Then lay two foundations — one clock
convention and one immutable event spine — and build delivery dates, spend dimensions, currency
facts, reason codes, GR ops, PO commercial fields, printing, Malaysian compliance and sourcing on top.
Deliver a KPI query layer with tests and the operational UI changes the new fields require — but no
dashboard screens.

Architectural principle to preserve:
ONE CLOCK + ONE IMMUTABLE EVENT SPINE + TRANSACTION-TIME FACTS + EXPLICIT KPI SEMANTICS = TRUSTWORTHY PROCUREMENT KPIs

---

## Verified baseline (do not re-derive)

Existing findings
| Fact | Location |
|---|---|
| Clock is company-local; doc says "Never uses DateTime.Today or UtcNow.Date blindly" | `ErpWeb.Core/Services/CurrentDateService.cs` |
| PO Created/Modified use local `_dates.Now` | `ErpWeb.Core/Purchase/PoOrderService.cs` (~684, 819, 2248) |
| INV/CDN/GR audit stamps use raw `DateTime.UtcNow` | `PoInvoiceService.cs` (~918-920), `PoCdnService.cs` (~628, 1977), `IvInventoryPostingService.cs` (~380, 444) |
| `PoPr.ApprovedDate2` is `string?` | `ErpWeb.Model/Entities/Purchase/PoPr.cs` |
| `PoOrder.CheckOn` / `ApprovedOn` exist, never assigned | `PoOrderService.cs` writes only CheckBy/ApprovedBy/AuthorisedBy |
| `PoOrderDetail.RecvDate` = latest only, date-only | `IvInventoryPostingService.cs:3014` |
| `IvTrxBatch.TrxDtTime` is date-only | `IvGoodsReceiptService.cs:512,585` |
| No event/status history for PR/PO/INV (only `IvTrxHistory` for stock) | entity scan |
| `PoOrderDetail` has no `Category`, no GL | entity |
| `PoOrder` has no `CurrRate`, no base-currency totals | entity |
| `PoInvoice` has `CurrRate` but no base totals, no DueDate | entity |
| `IX_POInvoice_Company_Branch_InvNo` is non-unique | `PoInvoiceConfiguration.cs` |
| `PoCdn.ReasonCode` is the only reason-code field | `PoCdn.cs` |
| `IvTrxBatch` has no attachments; PO/PR/supplier do | entity scan |
| EF configs auto-discovered; DbSet added manually | `AppDbContext.OnModelCreating` |
| Report assembly is a marker only | `ErpWeb.Report/ReportAssemblyMarker.cs` |
| `/dashboard` is a session stub | `ErpWeb.UI/Components/Pages/Dashboard.razor` |
| SQL Server concurrency test pattern exists | `ErpWeb.Tests/*SqlServerConcurrencyTests.cs` |

New findings from review verification (v2)
| Fact | Location | Consequence |
|---|---|---|
| **No EF Migrations folder exists** | repo scan | Deployment is manual idempotent scripts only. EF migrations are NOT used; do not introduce them. |
| **Doc identity is never global** — PO key `(CompanyCode, BranchCode, PoNo, PoRelNo)` | `PoOrderConfiguration.cs:9` | `PoDocEvent` must carry the same identity scope |
| Invoice/CDN key `(CompanyCode, BranchCode, DocNo)` | `PoInvoiceConfiguration.cs:10`, `PoCdnConfiguration` | Same scope required |
| GR PK is `Id`; unique `(CompanyCode, BranchCode, BatchNo)` | `IvTrxBatchConfiguration.cs:10,36` | Event must store BatchId + BatchNo, both scoped |
| **Posting operation identity already exists**: `PostingOperationId` / `RollbackOperationId` = `Guid.NewGuid()` per operation | `IvInventoryPostingService.cs` (~396, 447, 585, 590) | Natural idempotency anchor |
| **But the operation id is not surfaced**: `IvInventoryPostingResult` exposes only SucceededCount/FailedCount/Batches(BatchNo) | `IIvInventoryPostingService.cs:5-19` | Must be exposed before the event writer can key on it |
| `IvTrxBatch.SourceFingerprint` (SHA-256) precedent for content identity | `IvTrxBatch.cs` | Reusable pattern for idempotency |
| No `SiteCode`; tenancy scope is Company / Branch / Location | repo scan | "Site" == Branch/Location in this codebase |
| `SaCdn` and `PoCdn` IRBM field sets differ (only `PoCdn` has `SelfBilled`) | `SaCdnConfiguration.cs:51-57`, `PoCdnConfiguration.cs:60+` | Confirms: do a field-by-field comparison, do not mirror blindly |
| `IvTrxBatch.PostedBy`/`RollbackBy` are `maxLength 10`; PO/INV user columns are 20 | configs | User-id column lengths differ — normalize in new tables |

New findings from second-review verification (v3)
| Fact | Location | Consequence |
|---|---|---|
| **No database permission model exists.** No `GRANT` / `DENY` / role scripts anywhere in `scripts/` | repo scan | Table-level append-only cannot be enforced by existing DB grants. Use the application-level fallback (B3b) plus an optional DBA `DENY` addendum. |
| **No period-close infrastructure exists in the Blazor codebase.** `AdPara.CurrentMonth` / `CurrentYear` appear only in legacy WebForms docs | `plans/POCNDN-plan.md:342, 925` | Period control is net-new work, not a wiring task. P0.11 must define it from first principles. |
| **NG has its own posting path**: `PostNonStockGoodsReceiptCoreAsync` / `RollBackNonStockGoodsReceiptCoreAsync` are separate from stock GR | `IvInventoryPostingService.cs:629, 680, 699, 773` | NG behaviour must be audited explicitly; do not assume GR hardening transfers. |

New findings from fourth-review verification (v6)
| Fact | Location | Consequence |
|---|---|---|
| **`Company.TimeZoneId` EXISTS and is already used.** The clock resolves the company timezone via `db.Companies.AsNoTracking().Select(x => x.TimeZoneId)` with an `Asia/Kuala_Lumpur` fallback | `ErpWeb.Core/Services/CurrentDateService.cs` (`ResolveTimeZoneId`, `DefaultTimeZoneId`) | A3's migration timezone source is a real, verified field. No new configuration is invented. |

Clone templates: `PoMasterRefService` + `PoRefListPageBase` (masters), `PoOrderAttachmentService`
(attachments), `PoOrderCalc` (pure logic + tests), `IvGoodsReceiptService` (trx),
`ErpWeb/docs/inventory-trx-pattern.md` (new inventory trx types).

---

## Steps

### Phase 0 — Contract and semantic freeze (no production code)

Deliverable: one versioned document, `docs/procurement-semantics-contract.md`, containing 16 locked
decisions PLUS 8 reference tables (A–H, listed at the end of this phase). Each entry records the ACTUAL
frozen value, not a list of open questions. Nothing in Phases A–K starts until this exists and is
reviewed. This is the change that makes the plan implementation-ready. Business semantics may NOT remain
open once Phase 0 is signed off. Technical implementation choices MAY remain open where the contract or
API boundary makes them substitutable (for example the Phase H report renderer, which sits behind the
print DTO).

The contract also carries a TRACEABILITY TABLE mapping every implementation requirement in Phases A–K to
the P0 decision or reference table it derives from. This makes the Phase 0 gate machine-checkable
(verification item 2) rather than an assertion. A requirement that cannot be traced to a P0 row is either
a missing Phase 0 decision or an out-of-scope item, and must be resolved before implementation starts.

P0.1  Event transition matrix — document + business transition + event code + required payload, with
      the event-code vocabulary frozen (finalise in the contract): PR_ISSUE, PR_CANCEL, PO_ISSUE,
      PO_REVISE, PO_CANCEL, PO_CLOSE, PO_REOPEN, PO_PROMISE_CHANGE, GR_RECEIVE,
      GR_RECEIPT_ROLLBACK, NG_RECEIVE, NG_RECEIPT_ROLLBACK, VR_RETURN, INVOICE_ISSUE,
      INVOICE_ROLLBACK, CDN_ISSUE, CDN_ROLLBACK. `PO_PROMISE_CHANGE` is the frozen event code for a
      post-issue `PromisedDate` change. It is OPTIONAL from the KPI-event-spine perspective because
      `PoPromiseHistory` is authoritative (C3a, Table H), but the code MUST exist in the vocabulary and
      therefore in the seeded `PoDocEventCode` (B3a), so the FK accepts it when the business chooses to
      log promise changes as events. When emitted it carries the same `OperationId` as the
      promise-change operation. `ACK` is reserved, not emitted.
P0.2  Event idempotency contract — business key and the source-action identity to use.
P0.3  KPI definition matrix — numerator, denominator, source, date basis, inclusion/exclusion,
      zero-denominator behaviour, historical vs current semantics. Frozen values:
      - OTIF tolerance = configurable company-level grace period of N days (N default 0 = on or before
        the promised date).
      - Fill rate excludes valid cancelled quantity from the denominator:
        `EffectiveOrderedQty = OrderedQty - ValidCancelledQty`, then `received / EffectiveOrderedQty`,
        capped at 1.
      - **Closed quantity:** "closed" is a PO line/document STATUS, not a quantity, so it is NOT
        subtracted from the denominator. Only a real cancellation quantity reduces
        `EffectiveOrderedQty`. If a manual close-quantity field ever exists, it must be an explicit
        business decision recorded here before it can affect the KPI.
      - NG (non-stock) receipts are INCLUDED in all receipt-based KPIs: milestones, OTIF, fill rate,
        GR-to-invoice lag and return rate, because NG is the receipt path for indirect/service lines.
      - Revision rate is IN the first KPI release (denominator frozen in K3).
      - **KPI policy is effective-dated.** A `ProcurementKpiPolicyHistory` table (CompanyCode,
        EffectiveFrom, EffectiveTo, OtifToleranceDays, plus audit columns) versions every KPI-affecting
        configuration value. Current KPI uses the policy effective now; as-of KPI uses the policy
        effective at the evaluation date. Without this, a tolerance change from 0 to 3 days would
        silently rewrite historical OTIF.
P0.4  Currency rules — rate direction, source, capture moment, post-issue editability, precision,
      currency rounding, MYR rounding, null/zero handling, backfill policy.
P0.5  Receipt milestone semantics — `CompletelyReceivedOn` is a FACTUAL stamp: the actual date/time the
      required quantity became completely received. It is NOT adjusted by tolerance. Completion is
      evaluated per PO line against ordered quantity and then rolled up to the header. The tolerance
      belongs to the KPI only: OTIF passes when
      `CompletelyReceivedOn <= PromisedDate + configured tolerance`. Keeping the fact and the policy
      separate is required so a future tolerance change cannot rewrite history.
P0.6  PO schedule roll-up semantics.
P0.7  QC semantics — quantity rules: `InspectedQty = AcceptedQty + RejectedQty`; `InspectedQty <=
      RecvQty`; `AcceptedQty <= InspectedQty`; `RejectedQty <= InspectedQty`; partial inspection
      representable with a pending qty; `InspectedQty` and `Pending` are DERIVED, not persisted. Frozen
      stock/costing/valuation policy:
      - QC quantity IS on-hand (visible in the QC location).
      - QC quantity is NOT allocatable/reservable.
      - Cost IS recognised at GR posting, not deferred to QC acceptance.
      - The acceptance transfer is quantity-only; it does NOT change financial valuation.
      - A rejected quantity raises a normal VR creating its own return movement; it does NOT reverse the
        original receipt movement.
      - The QC location IS configurable per warehouse.
P0.8  Reason-code requirement matrix — Mandatory / Conditional / Optional per action and field.
P0.9  e-Invoice state vocabulary and transition matrix — frozen states: NOT_SUBMITTED, SUBMITTED,
      VALID, REJECTED, CANCELLED. Transitions: NOT_SUBMITTED -> SUBMITTED; SUBMITTED -> VALID;
      SUBMITTED -> REJECTED; VALID -> CANCELLED; REJECTED -> NOT_SUBMITTED (after correction, enabling
      resubmit). **`INVALID` is NOT a persisted state.** Local validation failures keep the document at
      NOT_SUBMITTED and record the validation error; IRBM rejections become REJECTED. This removes the
      previous open question about the meaning of INVALID.
P0.10 Identity and uniqueness rules — frozen: **`PoNo` identifies the logical PO; `PoRelNo` identifies
      a revision/version of that logical PO.** Company/Branch/Location scope applies throughout. Every
      KPI declaration must state which identity it uses: spend, cancellation, revision-rate and PO-level
      cycle time operate on the LOGICAL PO; event rows and line-level facts carry the revision identity.
      Counting revisions as separate POs is explicitly forbidden.
P0.11 Period control — frozen policy (no period-close infrastructure exists today, so this is net-new):
      - Scope: CompanyCode + BranchCode.
      - Fiscal-period source: a new period table owned by this work item (no legacy `AdPara` dependency).
      - Lock and reopen authority: a dedicated permission held by finance admin.
      - Reopen reason: mandatory.
      - A locked period REJECTS GR/NG and invoice posting. Editing drafts is NOT blocked.
      - Rollback of an already-posted document inside a locked period: REJECTED.
      - The lock keys on the business document/posting date, not the UTC timestamp.
      - Lock and reopen actions are audited in a SEPARATE administrative audit table (`PeriodAudit`),
        not in the procurement event log (see PC4).
P0.12 KPI data-quality field classification — Mandatory / Conditional / Optional with posting behaviour.
P0.13 Backfill eligibility rules, including event-less historical documents.
P0.14 Schema deployment and version strategy.
P0.15 Revision semantics — frozen:
      - A revision is any edit to an ISSUED PO that changes a commercial field (supplier, item, qty,
        price, or a COMMERCIAL date: `EtaDate`, schedule `DueDate`, `RequiredDate`). Cancel and reopen are
        NOT revisions. A `PromisedDate` change is commitment tracking, NOT a revision, and is versioned
        through `PoPromiseHistory` instead (C3a, Table H).
      - Revision storage: the existing `POOrder` revision rows (PoNo + PoRelNo). **`PoRelNo` numbering
        convention is frozen:** it is per LOGICAL PO (Company + Branch + PoNo), the first issued
        revision is `1` (so `0` is never used as a real revision), it increments by exactly one, gaps
        are NOT permitted and a failed transaction must not consume a number, and allocation is
        serialized per logical PO under the same lock ordering as the existing PO write path (B5).
        `PoRelNo` is part of event identity (B6) and of the revision chain, so it must be allocated
        inside the posting transaction.
      - `PromisedDate` changes are NOT revisions and do not increment `PoRelNo`.
      - Revision event: `PO_REVISE`, emitted once per revised revision, so a PO revised three times
        produces three `PO_REVISE` events with distinct OperationIds.
      - Revision-rate KPI: in the first release. Numerator = issued LOGICAL POs with at least one
        commercial revision; denominator = eligible issued LOGICAL POs. A PO revised three times counts
        once. A separate metric, average commercial revisions per issued PO, covers frequency.
P0.16 Cross-cutover cohort rule — a single configured value `ProcurementEventCutoverUtc` (application
      setting, never inferred) defines the boundary. A document whose required event sequence straddles
      that timestamp is classified pre-cutover/incomplete and reported separately; it is never silently
      mixed with complete post-cutover cohorts. The cutover report and the KPI service both read the
      timestamp from configuration.

**Phase 0 reference tables (part of the same contract document)**

Table A — Logical document identity
| Document | Logical identity | Revision/version identity |
|---|---|---|
| PO | Company + Branch + PoNo | Company + Branch + PoNo + PoRelNo |
| GR/NG | Company + Branch + BatchNo | BatchId + BatchNo (per batch row) |
| Invoice | Company + Branch + DocNo | same unless a revision model is added |
| CDN | Company + Branch + DocNo | same unless a revision model is added |

Table B — KPI policy versioning
`ProcurementKpiPolicyHistory` (CompanyCode, EffectiveFrom, EffectiveTo, OtifToleranceDays, audit
columns). Covers OTIF tolerance, cancellation treatment, fill-rate rules and any future KPI-affecting
configuration. Current KPI resolves the policy effective now; as-of KPI resolves the policy effective
at the evaluation date. **Constraint:** exactly one policy version may be effective for a company at any
evaluation instant; overlapping or gapped ranges are rejected by service validation and by a filtered
unique index where practical.

Table C — Event operation identity
Creation point: the application/service boundary, BEFORE the transaction begins. The caller may supply
it; when omitted the service creates it. Retry MUST reuse it. All batches generated by one logical
posting request share one `PostingOperationId`; one logical rollback request shares one
`RollbackOperationId`. It is persisted on the posting document and written to `PoDocEvent.OperationId`.
Uniqueness is enforced by the B6 unique key.

Table D — e-Invoice state machine
As frozen in P0.9: NOT_SUBMITTED, SUBMITTED, VALID, REJECTED, CANCELLED. `INVALID` does not exist as a
persisted state.

Table E — Outbox attempt model
Two identities, both persisted:
- `SubmissionOperationId` — ONE logical submission or resubmission operation.
- `SubmissionAttempt` — the attempt number WITHIN that operation, starting at 1.
Unique key: `(CompanyCode, BranchCode, DocNo, OperationType, SubmissionOperationId, SubmissionAttempt)`.
Rules: a retry of the same operation reuses `SubmissionOperationId` and appends the next
`SubmissionAttempt`; a NEW resubmission operation creates a NEW `SubmissionOperationId` with the attempt
restarting at 1; attempt IDENTITY and request payload are immutable, while lifecycle and lease fields are
mutable ONLY through the frozen outbox state machine (field-scoped ownership: I2c); a terminal attempt is
never reused or re-opened; the invoice identity is unchanged throughout. Exact column contract: I2b.
Worker claim semantics: I2c. State responsibility: Table G.

Table F — NG scope matrix
| Capability | Applies to NG |
|---|---|
| Event emission | Yes (`NG_RECEIVE`, `NG_RECEIPT_ROLLBACK`) |
| Receipt-based KPIs | Yes |
| PO receipt milestones | Yes |
| Attachments | Yes |
| Delivery header fields | Yes |
| QC inspection | No |
| Weighbridge capture | No |

Table G — e-Invoice state vs outbox attempt state (responsibility matrix)
| Concern | Owner |
|---|---|
| Invoice business e-Invoice state | `PoInvoice` |
| Submission attempt execution state | `IrbOutbox` |
| Provider response and history | `IrbOutbox` |
| Retry eligibility | `IrbOutbox` attempt state plus service policy |
| Resubmit after rejection | `PoInvoice` transition plus a NEW outbox `SubmissionOperationId` |
The two state machines are independent. An outbox attempt can be `FAILED` while the invoice remains
`SUBMITTED`; the invoice becomes `REJECTED` only when a provider rejection response is recorded. A worker
crash leaves the invoice state unchanged and is recovered through the attempt lease (I2c).

Table H — Promise versioning
`PoPromiseHistory` (PoPromiseHistoryId BIGINT IDENTITY primary key, CompanyCode, BranchCode, PoNo,
PoRelNo, LineNo, OldPromisedDate, NewPromisedDate, ChangedOnUtc, ChangedBy, OperationId). Append-only;
every post-issue `PromisedDate` change inserts one row per changed line. This table — not `PoDocEvent` —
is the authoritative source for promise-version reconstruction. The promise a KPI uses is resolved per
C3a.
**Idempotency key:** unique `(CompanyCode, BranchCode, PoNo, PoRelNo, LineNo, OperationId)`. One logical
operation writes at most one history row per changed line, so a retry of the same `OperationId` is
handled as idempotent re-entry rather than producing a duplicate history row. A unique-key violation on
retry is absorbed, not surfaced as a user-visible error.
**Deterministic cutoff resolution (no reliance on timestamp uniqueness):** for a reporting cutoff `T`,
the effective promise is the row with the greatest `(ChangedOnUtc, PoPromiseHistoryId)` where
`ChangedOnUtc <= T`; ordering is `ChangedOnUtc DESC, PoPromiseHistoryId DESC`. If no row qualifies, the
effective promise is `OriginalPromisedDate`. Two rows sharing a `ChangedOnUtc` therefore resolve
deterministically by insertion order.
**Same-transaction rule:** the `PromisedDate` update and its `PoPromiseHistory` insert commit in the SAME
transaction.

### Phase A — Clock and timestamp discipline (mandatory foundation for all production work)

A1. Extend `ICurrentDateService` with a UTC member; implement in `CurrentDateService` and
    `FixedCurrentDateService`. Freeze: **business/document dates = company-local `Today`; audit and
    post stamps = UTC `datetime2`; UI converts to local for display.**
A2. Route all direct `DateTime.UtcNow` / `DateTime.Today` in purchase and inventory services through
    the injected clock.
A3. Change PO `CreatedDate`/`ModifiedDate` from local to UTC, with an explicit historical migration
    strategy: **convert all existing PO `CreatedDate`/`ModifiedDate` values from company-local to UTC
    during cutover**, using the company's configured business timezone (`Company.TimeZoneId`, resolved
    via `ICurrentDateService`) rather than a hard-coded offset, so the conversion stays correct if a
    company is ever configured outside Malaysia. **Verified:** `Company.TimeZoneId` already exists and is
    already read by `CurrentDateService.ResolveTimeZoneId()` with an `Asia/Kuala_Lumpur` fallback, so no
    new configuration is invented here. The conversion script is
    idempotent, guarded by a marker (for example a one-row migration-log entry) so it cannot
    double-apply, and is verified against a DB copy. Preserving mixed local/UTC semantics in a single
    column is explicitly rejected.
A4. Fix `PoPr.ApprovedDate2` to `DateTime?`.
A5. Document the `datetime2` rule for new audit columns.

### Phase B — Document event log (mandatory prerequisite for event-based historical KPIs)

B1. Entity `PoDocEvent` with **tenant-scoped identity**: CompanyCode, BranchCode, DocType, DocNo,
    RelNo, BatchId, BatchNo, EventCode, EventDateUtc, UserId, RefDocNo, RefLineNo, ReasonCode,
    OperationId, SourceKey, Notes.
B1a. **Frozen column contract** (do not let implementation infer nullability):
     CompanyCode NOT NULL; BranchCode NOT NULL; DocType NOT NULL; DocNo NOT NULL; RelNo NOT NULL;
     BatchId NULL; BatchNo NULL; EventCode NOT NULL; EventDateUtc NOT NULL; UserId NOT NULL;
     OperationId NOT NULL; SourceKey NULL; RefDocNo NULL; RefLineNo NULL; ReasonCode NULL; Notes NULL;
     CorrelationId NULL; ParentOperationId NULL; SourceService NULL (the last three are troubleshooting
     metadata, P2 scope, not required by the KPI model).
     Naming convention: new UTC audit columns use an explicit `...Utc` or `...OnUtc` suffix.
     Indexes: unique `(CompanyCode, BranchCode, DocType, DocNo, RelNo, EventCode, OperationId)`;
     query `(CompanyCode, BranchCode, DocType, DocNo)`; query `(DocType, EventCode, EventDateUtc)`;
     query `(EventCode, EventDateUtc)`.
     Identity/ordering: `PoDocEventId BIGINT IDENTITY` is the primary and ordering key, because two
     events can share the same `EventDateUtc`. Deterministic ordering is
     `(EventDateUtc, PoDocEventId)`, never timestamp alone. **`PoDocEventId` represents PERSISTENCE order,
     not business causality.** KPI algorithms must not infer business sequence solely from
     `PoDocEventId`, because separate transactions can legitimately commit concurrently; business sequence
     is derived from the event codes and the document/business dates.
B2. EF config + `DbSet<PoDocEvent>`.
B3. DDL `scripts/create-po-doc-event.sql` (idempotent), including the unique key from B6 and the
    event-code reference table below.
B3a. **DB-enforced event vocabulary.** Create `PoDocEventCode` (EventCode PK, Description, IsActive),
     seed it from the P0.1 vocabulary, and add a foreign key
     `PoDocEvent.EventCode -> PoDocEventCode.EventCode`. Application tests alone cannot stop another SQL
     or application path inserting an unlisted code; the FK makes the vocabulary a database invariant.
     `IsActive` semantics: an inactive code cannot be EMITTED by the application, existing historical
     events remain valid, and deactivation is never deletion.
B3b. **Application-enforced append-only, with optional DB permission hardening.** No DB permission model
     exists in this repo (verified), so true database append-only is not yet possible and the wording
     must not overclaim. Requirements: no update/delete repository methods, no EF mutation paths (no
     `Remove`, no tracked updates) for `PoDocEvent`, and tests proving an event cannot be modified or
     deleted through the application layer. Optionally add a DBA-run
     `scripts/harden-po-doc-event-permissions.sql` issuing `DENY UPDATE, DELETE` to the application role;
     that script is a deployment-checklist item wherever the production security model permits it.
B4. `IPoDocEventWriter` + implementation, always inside the caller's existing transaction.
B5. Emit exactly one event per real business state transition. Technical/exception rollbacks emit
    nothing. CREATE is intentionally not logged (see P0.1/P0.2).
B5a. **Emission granularity: events are document-level by default.** One document state transition emits
     one event even when that transition touches many lines. `RefLineNo` is populated only when the
     transition is genuinely line-specific (for example a single line rejection). This prevents event
     inflation and keeps KPI counts consistent.
B5b. **Multiple events of the same type on one document are legitimate.** A document may carry several
     `PO_REVISE`, `GR_RECEIVE`, `VR_RETURN` or rollback events, provided each represents a different
     business operation and therefore carries a different `OperationId`. The B6 unique key must NOT be
     read as "one event of each type per document".
B6. Idempotency — **exact unique key, per P0.2**:
    `(CompanyCode, BranchCode, DocType, DocNo, RelNo, EventCode, OperationId)`.
    **`OperationId` is the idempotency identity for ALL event types, including GR, NG and VR.** Batch
    identity (`BatchId` / `BatchNo`) is stored as part of the document/source identity but must NEVER
    substitute for `OperationId` in the unique key. `RelNo` is `0` or a sentinel for documents with no
    revision concept rather than nullable, so the key stays deterministic.
B6a. **`OperationId` and `SourceKey` are not interchangeable.**
     - `OperationId` — idempotency identity for exactly one logical business operation. Reuse the existing
       `PostingOperationId` / `RollbackOperationId` where they exist; expose them first (B7).
     - `SourceKey` — stable source/content identity for reconciliation (the existing
       `IvTrxBatch.SourceFingerprint` pattern), never used for uniqueness.
     - Nullability: `OperationId` is NOT NULL on every emitted event.
     - **Retry and re-entry rule:** `OperationId` is created ONCE per logical business operation and
       propagated across retries and re-entry. A retry of the same logical operation MUST reuse the
       original `OperationId`. A new service invocation MUST NOT generate a new `OperationId` merely
       because the previous invocation's response was lost. For operations with no natural operation id,
       the application/service boundary creates the `OperationId` BEFORE the transaction begins and
       passes it through the whole operation; retry and caller contracts must preserve and reuse it.
       Without this rule the failure path (commit succeeds, response lost, caller retries, new GUID,
       second event) produces duplicate events and corrupt KPI counts.
     - **Non-reuse rule:** a NEW, unrelated business operation MUST receive a NEW `OperationId`, even
       when it targets the same document and the same event type. This is what makes legitimate repeated
       events possible (several `PO_REVISE`, `GR_RECEIVE` or `VR_RETURN` operations on one document)
       while keeping the B6 unique key a true idempotency guard. "Retry reuses" and "new operation
       creates" are both mandatory; neither may be applied alone.
B7. **Surface the operation id, with an explicit contract** (see Table C): `PostingOperationId`
    identifies the WHOLE logical posting request; `RollbackOperationId` identifies the WHOLE logical
    rollback request. All batches produced by one logical request share the same operation id. The caller
    may supply it; when omitted the service creates it before the transaction begins. It is persisted on
    the posting document. Add `OperationId` to `IvInventoryPostingResult` and
    `IvInventoryPostingBatchResult` so the writer can key on it.
B8. Hook points: `PoPrService`, `PoOrderService`, `IvInventoryPostingService` (GR/NG/VR post +
    rollback), `PoInvoiceService`, `PoCdnService`.
B9. Invariant: a committed transition has exactly one event; a rolled-back transaction has zero.
B10. Cutover: event log starts empty; add `scripts/report-procurement-cutover.sql`.
B11. **Event timestamp vs business date.** `EventDateUtc` is the audit/ordering timestamp; period and
     cohort calculations use the document/business date from the P0.11 period rules. A 23:30 MYT event
     stores a UTC instant that can fall on the previous UTC date, so the two must never be substituted
     for one another.

### Phase C — Delivery promise and receipt milestones

C1. `PoOrderDetail`: `RequiredDate`, `OriginalPromisedDate`, `PromisedDate`, `FirstRecvDate`,
    `LastRecvDate`, `CompletelyReceivedOn`. `OriginalPromisedDate` is stamped once at PO issue and is
    IMMUTABLE; `PromisedDate` is the current operational promise. Two DATE FAMILIES are distinguished and
    must not be conflated:
    - COMMERCIAL dates (`EtaDate`, schedule `DueDate`, `RequiredDate`) — changing one after issue is a
      commercial revision (P0.15) and creates a new `PoRelNo`.
    - COMMITMENT dates (`PromisedDate`) — supplier-commitment tracking, not an order commercial term.
      Post-issue changes are permitted WITHOUT a revision but are always versioned (Table H).
C2. Copy `PoPrDetail.EtaDt` into `RequiredDate` on the PR→PO pull.
C2a. **`RequiredDate` is immutable WITHIN a revision.** It is copied from PR ETA and stamped at issue. A
    later change to the PR ETA must NOT silently overwrite it, so no PR master-data edit can move a
    historical KPI. Changing `RequiredDate` after issue is a COMMERCIAL change: it requires a PO revision
    and the new revision row carries the new value, so history is preserved by the revision chain. There
    is no separate required-date edit path.
C3. `PromisedDate` defaults to PO date + vendor/item lead time and is user-editable. No supplier
    acknowledgement workflow exists in scope; if one is added later, an `ACK` event may update it.
    The `ACK` constant may be reserved but current implementation must not depend on it.
C3a. **Promise history — closes the OTIF-rewrite hole (single mechanism, no "or").** Every post-issue
    change to `PromisedDate` MUST insert an immutable `PoPromiseHistory` row IN THE SAME TRANSACTION as
    the `PromisedDate` update. That table is the authoritative source for promise-version reconstruction;
    `PoDocEvent` may ADDITIONALLY carry the `PO_PROMISE_CHANGE` frozen event code (P0.1), but the history
    row is what OTIF reads. The OTIF promise version is frozen as follows:
    - Supplier-performance OTIF cohort: the promise effective at PO issue, i.e. `OriginalPromisedDate`.
    - As-of OTIF: the promise version effective at the requested reporting cutoff, resolved from
      `PoPromiseHistory` using the deterministic ordering `ChangedOnUtc DESC, PoPromiseHistoryId DESC`
      (Table H), falling back to `OriginalPromisedDate` when no row precedes the cutoff.
    Two implementations must not be able to read this differently.
C3b. **One save that changes BOTH a commercial field and `PromisedDate` is atomic.** If a single user
    operation changes commercial PO fields (including `RequiredDate`) AND `PromisedDate`, the revision
    write, the `PoPromiseHistory` insert and both events commit as ONE logical operation. The commercial
    change produces the required `PO_REVISE` event; the promise change produces one `PoPromiseHistory`
    row per changed line and, if event logging is enabled, one `PO_PROMISE_CHANGE` event. All events from
    the save share the SAME `OperationId`, so retry and re-entry remain idempotent. A failed transaction
    leaves neither the revision nor the promise history written.
C4. Milestones per P0.5, maintained in the GR write-back in `IvInventoryPostingService`.
C5. Surface the dates in `PoOrder.razor` and `PoOrderList` (including an overdue filter).
C6. `FirstRecvDate` is retained for first-receipt cycle-time analysis only. The primary OTIF KPI uses
    complete-delivery-against-promise (see K3), so the two must not be conflated in the UI or the KPI.

### Phase D — Spend dimensions and base currency

D1. `PoOrderDetail.Category` (from `PoPrDetail.Category`) and `PoOrderDetail.ItemGlCode`.
D2. `PoOrder.CurrRate`; snapshot `CreditorGroup`, `CreditorSubGroup`, `SupplierCategory`, `PayCode`.
D2a. **Snapshot immutability after issue.** Supplier analysis attributes used for KPI reporting are frozen
     at issue; later master-data edits (renaming a creditor group, changing a supplier category) never
     rewrite the snapshot. `CurrRate` is likewise frozen at issue and not re-resolved on later edits.
     Pre-issue behaviour is frozen too: the values are editable while the PO is a draft and are stamped
     once at issue. An as-of KPI therefore reads the issue-time snapshot, never a live join.
D3. Base-currency (MYR) amounts on PO header/line and invoice header/line.
D4. Per P0.4: rate direction/source/capture are locked; stored MYR amounts are authoritative for
    historical reporting and must never be recalculated with today's rate.
D5. Persist inside the same transaction as totals in `PoOrderCalc` / `PoInvoiceCalc`; unit tests.
D6. Populate `Category` on the PR→PO pull; expose in the PO line popup.

### Phase E — Reason codes

E1. `PoReasonCode` master with `ReasonContext`: CANCEL, CLOSE, REOPEN, GR_ROLLBACK,
    INVOICE_ROLLBACK, VR_RETURN, OVER_RECEIPT, OVER_INVOICE, PRICE_VARIANCE.
E2. Service + UI cloned from `PoMasterRefService` / `PoRefListPageBase` / `PoCategoryList.razor`;
    menu code + `scripts/init-po-reason-code-menu.sql`.
E3. Columns: `PoOrder.CancelReasonCode`, `PoOrder.CloseReasonCode`, `IvTrxBatch.RollbackReasonCode`,
    `IvTrxBatchDetail.ReasonCode`, VR line reason.
E4. Requirement per P0.8 (Mandatory / Conditional / Optional), not blanket-mandatory.
E5. **Business rollback requires a reason code. A technical SQL rollback caused by an exception is
    not a business rollback: it requires no reason and emits no event.**
E6. Align `PoCdn.ReasonCode` to the master while tolerating legacy values.
E7. Mirror the reason into `PoDocEvent.ReasonCode`.

### Phase F — GR and NG operations hardening

F1. GR attachments: `IvTrxBatchAttachFile` cloned from `PoOrderAttachmentService`, reusing
    `AttachmentStorageOptions`.
F2. GR header: supplier DO no, vehicle/lorry no, driver, container no.
F3. Inspection/QC — keep the **quarantine-location** variant (preserves `RecvQty`, no new batch
    status). Per P0.7:
     - `InspectedQty = AcceptedQty + RejectedQty`; `InspectedQty <= RecvQty`;
       `AcceptedQty <= InspectedQty`; `RejectedQty <= InspectedQty`.
     - Partial inspection must be representable (e.g. Recv 100, Accepted 70, Rejected 20, Pending 10).
     - `InspectedQty` and `Pending` are DERIVED values and must NOT be persisted (already frozen in P0.7).
     - VR is raised only after inspection is finalised and through the normal VR machinery — not as
       a nested call inside the GR posting transaction.
F3a. **Stock and valuation ownership during hold — frozen policy (see P0.7):**
     - QC location quantity IS on-hand (visible in the QC location).
     - QC location quantity is NOT allocatable or reservable.
     - Cost IS recognised at GR posting, not deferred to QC acceptance.
     - The accept transfer is quantity-only; it does NOT change financial valuation.
     - A rejected quantity raises a normal VR creating its own return movement; it does not reverse the
       original receipt movement.
     - The QC location is configurable per warehouse.
F4. Optional weighbridge capture (net/gross/tare to existing `WtQty`/`WtUom`).
F5. UI updates on `IvGoodsReceipt.razor(.cs)` and `IvGoodsReceiptList.razor(.cs)`.
F6. **NG behaviour audit — do not assume GR hardening transfers.** NG has distinct posting paths
    (`PostNonStockGoodsReceiptCoreAsync` / `RollBackNonStockGoodsReceiptCoreAsync`). For each Phase F item
    (F1 attachments, F2 header fields, F3/F3a QC, F4 weight, F5 UI) record whether it applies to NG,
    applies in modified form, or does not apply — with the business reason. **Already frozen:** NG emits
    its own events (`NG_RECEIVE`, `NG_RECEIPT_ROLLBACK`); NG affects PO receipt milestones; and NG is
    included in ALL receipt-based KPIs (milestones, OTIF, fill rate, GR-to-invoice lag, return rate)
    because it is the receipt path for indirect/service lines. **Frozen NG scope matrix (Table F):**
    event emission yes; receipt-based KPIs yes; PO receipt milestones yes; attachments yes; delivery
    header fields yes; QC inspection NO; weighbridge capture NO. Nothing remains open on NG
    applicability.

### Phase G — PO commercial fields

G1. `PoOrderSchedule` (PoNo, PoRelNo, Line, ScheduleNo, DueDate, Qty, Warehouse, ReceivedQty, Status).
G2. Roll-up per P0.6. Recommended: line ETA = earliest date of the **outstanding** scheduled quantity,
    so the ETA advances as earlier schedules are completed. An arbitrary MIN over all schedule dates
    is explicitly rejected.
G3. Incoterms/logistics header fields: `IncotermCode`, `DeliveryMode`, `PortOfLoading`,
    `PortOfDischarge`, `FreightTerms`, `FreightAmount`.
G4. Deposit/advance payment: `DepositPct`, `DepositAmt`, `DepositPaidOn`, `DepositRefNo` —
    informational only until AP exists; state this in the UI hint.

### Phase H — Reports and printing

H1. Keep DTO-first: `IPoDocPrintService` returning PO / GRN / Invoice / CDN print DTOs.
H2. **Renderer selection is a Phase H implementation decision** — do not lock DevExpress vs QuestPDF in
    this plan; keep the DTO abstraction so either swaps in without touching services.
H3. Define: print-counter transaction behaviour, print permission enforcement, filename convention,
    content type, version/snapshot semantics.
H4. Wire `PrintCounter` on PO print; add print actions guarded by the existing Print permission.
H5. Do not add a PRINT event to the KPI spine unless a real audit requirement is confirmed.

### Phase I — Malaysia compliance foundation (seven work items)

Boundary: Phase I provides the Malaysia compliance DATA foundation, validation/state model and
integration seam. It does NOT certify the ERP as statutory or tax compliant. Out of scope: AP
deduction/posting, payment accounting, a complete filing ledger, production LHDN integration and
certification, and any statutory reconciliation that depends on AP/GL.

I1. Invoice compliance data foundation.
I2. e-Invoice outbox / integration seam. Keep: **no live LHDN call inside the document transaction.**
    Flow: document transaction writes invoice changes plus one `IrbOutbox` row; a background worker
    performs the integration.
I2a. **Outbox attempt model — frozen (Table E).** Two identities: `SubmissionOperationId` (one logical
     submission or resubmission operation) and `SubmissionAttempt` (attempt number within that operation,
     from 1). Unique key
     `(CompanyCode, BranchCode, DocNo, OperationType, SubmissionOperationId, SubmissionAttempt)`. A retry
     of the same operation reuses `SubmissionOperationId` and appends the next attempt; a NEW resubmission
     operation creates a NEW `SubmissionOperationId` with the attempt restarting at 1. Attempt IDENTITY
     and request payload are immutable; lifecycle and lease fields are mutable only through the legal
     transitions in I2c. A terminal attempt is never reused or re-opened. The invoice identity is
     unchanged.
I2b. **Outbox column contract** (mirrors the B1a precision; actual SQL types finalised in the contract):
     CompanyCode NOT NULL; BranchCode NOT NULL; DocNo NOT NULL; OperationType NOT NULL;
     SubmissionOperationId NOT NULL (uniqueidentifier); SubmissionAttempt NOT NULL (int);
     Status NOT NULL (PENDING / PROCESSING / SUCCEEDED / FAILED / ABANDONED); RequestPayload NOT NULL
     (immutable); ResponsePayload NULL; ErrorCode NULL; ErrorMessage NULL; CreatedOnUtc NOT NULL;
     ClaimedOnUtc NULL; ClaimExpiresOnUtc NULL; ClaimedBy NULL; CompletedOnUtc NULL.
I2c. **Worker claim concurrency — frozen.** Claiming is an atomic conditional UPDATE from `PENDING` to
     `PROCESSING` that also stamps `ClaimedOnUtc`, `ClaimExpiresOnUtc` and `ClaimedBy`, executed under an
     update lock so exactly ONE worker transitions the row; a losing worker sees zero rows affected and
     moves on. Only the owning worker may complete the attempt. A crash leaves a stale `PROCESSING` row,
     recovered by re-claiming once `ClaimExpiresOnUtc` has passed; recovery is bounded by a counter so an
     attempt cannot loop forever.
     **Terminal states and legal transitions:** PENDING -> PROCESSING -> SUCCEEDED; PENDING -> PROCESSING
     -> FAILED; PENDING -> PROCESSING -> ABANDONED (retry budget exhausted); stale PROCESSING -> PENDING
     (lease-expiry recovery only). SUCCEEDED and ABANDONED are terminal. FAILED is terminal for THAT
     attempt but permits a NEW attempt row within the same operation.
     **Retry ownership:** retry scheduling is owned by the outbox service/worker policy — not by the
     provider adapter and not by the caller. Creating a retry appends a NEW attempt row within the same
     `SubmissionOperationId`; the previous attempt remains terminal and unchanged.
     **Field-scoped ownership (replaces the ambiguous "attempt rows are immutable" wording).** An attempt
     row is NOT wholly immutable. The frozen split is:
     - IMMUTABLE: CompanyCode, BranchCode, DocNo, OperationType, `SubmissionOperationId`,
       `SubmissionAttempt`, RequestPayload, CreatedOnUtc. No update path may change these, and a direct
       attempt to mutate them is rejected and covered by a test.
     - MUTABLE ONLY THROUGH LEGAL TRANSITIONS: `Status`, plus the lease fields ClaimedOnUtc,
       ClaimExpiresOnUtc and ClaimedBy, written on claim and on lease-expiry recovery and nowhere else.
     - WRITE-ONCE AT TERMINAL COMPLETION: ResponsePayload, ErrorCode, ErrorMessage, CompletedOnUtc.
       Written once when the attempt reaches a terminal state and never overwritten afterwards.
     The SQL types, and whether provider responses live on the attempt row or in a child history table,
     remain implementation choices; the ownership split above is the frozen contract, because it drives
     EF tracking, repository shape, worker SQL and concurrency tests.
     **Current operation pointer:** `PoInvoice.CurrentSubmissionOperationId` holds the operation the
     invoice is currently associated with (see I2e).
I2d. **State responsibility (Table G).** `PoInvoice` owns the business e-Invoice state; `IrbOutbox` owns
     attempt execution state, provider responses and retry eligibility. They are independent: an attempt
     may FAIL while the invoice stays `SUBMITTED`, and the invoice becomes `REJECTED` only when a provider
     rejection response is recorded.
I2e. **Current submission operation pointer.** `PoInvoice.CurrentSubmissionOperationId` (nullable,
     uniqueidentifier) stores the `SubmissionOperationId` the invoice is CURRENTLY associated with,
     updated when a submission operation starts and again when a resubmission operation starts.
     Operational lookup ("which submission operation is this invoice on?") therefore does not need to
     scan the outbox. It is a convenience pointer only: `IrbOutbox` remains the authoritative history, so
     the pointer can always be rebuilt from the outbox if it is ever wrong.
     **Atomic write rule:** creating a new `SubmissionOperationId`, inserting its FIRST `IrbOutbox`
     attempt row, and updating `PoInvoice.CurrentSubmissionOperationId` MUST occur in the SAME database
     transaction. The pointer can therefore never reference an operation that has no outbox row, and a
     rolled-back submission leaves no orphan pointer.
I2f. **Provider-side idempotency and ambiguous-success recovery (integration seam).** The local retry
     contract above covers worker failure, but not the window where the provider accepted the submission
     and the application failed before recording the response. Frozen requirements for that seam:
     - Where the provider supports it, `SubmissionOperationId` is supplied as the provider
       idempotency/correlation key, so a repeated submission is recognised rather than duplicated.
     - Where provider idempotency is NOT available, the adapter MUST perform a reconciliation or status
       lookup before resubmitting an ambiguously completed operation, rather than blindly resending.
     - An ambiguously completed attempt is never marked SUCCEEDED on assumption; it is resolved by the
       lookup, or exhausted into FAILED/ABANDONED through the normal transitions.
     This stays an integration-seam requirement: no live LHDN call is introduced inside the document
     transaction (I2 boundary).
I3. Self-billed data.
I4. SST tax classification: extend `SaTaxGroup` with tax type (SR/ZR/DS/ES/TX), exemption reason,
    SST-02 mapping; purchase invoice lines carry the type.
I5. WHT storage: `PoWhtCode` master plus `PoInvoice.WhtCode/WhtRate/WhtBaseAmt/WhtAmt`. Stored only;
    deduction depends on AP (out of scope) — record as a documented dependency.
I6. Implement the P0.9 state matrix exactly: NOT_SUBMITTED, SUBMITTED, VALID, REJECTED, CANCELLED.
    There is no persisted INVALID state (local validation failures stay at NOT_SUBMITTED with a recorded
    error). Reuse CDN semantics from `docs/cdn-logic.md` where possible and document invoice-specific
    differences.
I6a. **Resubmit semantics — frozen.** A REJECTED invoice becomes resubmittable after correction. The path
     is REJECTED -> correction -> NOT_SUBMITTED -> SUBMITTED. A resubmission creates a NEW outbox
     `SubmissionOperationId` with `SubmissionAttempt` restarting at 1 (never an update of the rejected
     attempt's row). The invoice identity is retained; every prior attempt and provider response is
     preserved in the outbox history.
I7. **Do a field-by-field comparison of the existing CDN e-Invoice implementation against purchase
    invoice requirements and document missing fields before schema work.** Verified: `SaCdn` and
    `PoCdn` IRBM field sets already differ, so mirroring is not sufficient.

### Phase J — Supplier sourcing (independent workstream)

J1. Effective-dated `PoVendorPrice`; PO/PR entry suggests price and shows last price + history.
J2. RFQ: `PoRfq`, `PoRfqVendor`, `PoRfqLine`, `PoRfqVendorLine`; raised from PR lines; comparison
    DTO; award writes back to PR and can generate a PO.
J3. Blanket/contract: `PoContract` + `PoContractLine` with qty/value ceilings; `PoOrder.ContractNo`
    call-off linkage with release validation.
J4. Menus + rights for each new screen.
Dependency is E to J, but J may be developed independently once its own dependencies are ready.

### Phase K — KPI query layer and backfill

K1. `IProcurementKpiService` + DTOs, built on Phases B–F plus invoice data. No dashboard screens.
    Receipt-dependent KPIs (OTIF, fill rate, over-receipt, return rate, GRN-to-invoice lag, receipt
    milestones) require Phase F; list any G or I field dependency individually rather than hiding it
    under a general statement.
K2. Implement only what P0.3 defines. Required per-KPI entries: numerator, denominator, source, date
    basis, inclusion/exclusion, zero-denominator behaviour, historical vs current semantics.
K3. Explicit KPI definitions to lock:
    - **OTIF — primary definition is complete delivery against promise, with tolerance.** OTIF passes
      when the required quantity is completely received within the frozen tolerance of the promised date.
      Frozen tolerance = configurable company-level grace period of N days (N default 0 = on or before
      the promised date). Partial deliveries make first-receipt OTIF ambiguous (ordered 100 promised
      10 Oct; receive 20 on 10 Oct and 80 on 20 Oct), so first-receipt timing is NOT the OTIF measure.
      `FirstRecvDate` serves first-receipt cycle-time only. If partial-delivery OTIF is wanted, define it
      as a separate, explicitly named KPI. **Promise version:** the comparison date is
      `OriginalPromisedDate` for a supplier-performance cohort and the `PoPromiseHistory` version
      effective at the cutoff for an as-of view (C3a, Table H).
    - **Fill rate** = min(received / (ordered - valid cancelled qty), 1). Valid cancelled quantity is
      excluded from the denominator, so a 100-ordered, 30-cancelled, 70-received line reports 100%.
      Edge rules: denominator <= 0 reports **N/A, not 100%**; a valid cancellation can never exceed the
      ordered quantity (validation rejects it); cancellation quantity is capped at (ordered - received),
      so quantity already received cannot be cancelled; negative cancellation quantities are rejected as
      data corruption. Over-receipt is measured separately by the over-receipt KPI. PO CLOSE STATUS does
      not affect the fill-rate denominator; only validated cancellation quantity reduces
      `EffectiveOrderedQty`.
    - **NG participation — frozen:** NG receipts count as receipts in OTIF, fill rate, receipt
      milestones, GR-to-invoice lag and return rate, because NG is the receipt path for indirect/service
      lines.
    - **Revision rate** = issued LOGICAL POs with at least one commercial revision / eligible issued
      LOGICAL POs, in the first release. A PO revised three times counts ONCE, so the rate cannot exceed
      100%. Reported separately: average commercial revisions per issued PO (a frequency metric, not a
      rate). Both use the logical PO identity from P0.10.
    - PR to PO = PO issue event minus PR issue event.
    - PO to receipt = first receipt event minus PO issue event.
    - **PO Final Receipt-to-Final Invoice Lag** (renamed twice: from the ambiguous "GR to invoice", then
      from "receipt-to-invoice lag", because the measure is a FINAL-stage lag and not average invoice
      processing speed). Cohort = logical PO, ONE value per PO. Aggregation frozen as
      `LastInvoiceIssueDate - LastReceiptDate` when the final
      invoice falls on or after the final receipt; otherwise the PO is excluded as out-of-order rather
      than reporting a negative lag. Multiple receipts and multiple invoices collapse to that single
      pair, never a cartesian product. If AP processing speed is the intent later, add a separately named
      `FirstInvoiceIssueDate - LastReceiptDate` metric. Any future variant must be given its own KPI name.
    - **Cancellation** = cancelled issued POs / issued POs, where the denominator is issued PO documents
      eligible for cancellation analysis in the period. Define eligibility exclusions (drafts,
      auto-closed, fully received, cancelled-before-issue, system-generated replacements) and the date
      basis per purpose: ISSUE event date for cohort reporting, CANCEL event date for cancellation
      activity.
    - **Return rate** = business-returned qty / GROSS received qty. The denominator is the gross accepted
      receipt quantity before business returns and INCLUDES QC-rejected quantity, because the rejected
      quantity originated from that receipt (receive 100, reject 20, VR 20 => 20%). Technical rollback is
      EXCLUDED from both numerator and denominator: a rollback is not a business return and emits no
      event. Only `VR_RETURN` events count in the numerator. Cancelled or voided returns are reversed out
      of both sides.
K4. **Price variance must be named explicitly.** Recommended initial pair: PO price variance =
    PO price vs previous effective vendor/item price; Invoice price variance = invoice price vs PO
    price. If only one ships initially, name it and do not use the generic term.
K5. As-of semantics — define the cutoff explicitly, because "current" alone is ambiguous:
    - **Current KPI** — uses current persisted facts at query execution time. A January PO that receives
      more in March shows a different January fill rate when the query runs later.
    - **As-of KPI** — uses event/history or snapshot data as of a specified reporting cutoff.
    Cycle-time metrics use the immutable event log. Mutable-fact KPIs (RecvQty, InvoicedQty) are marked
    current, and every KPI in K2 must declare which of the two it is.
    **Limit:** as-of reporting is supported ONLY where the required historical state exists in event or
    snapshot data. Mutable-fact KPIs without a historical snapshot are current-only, and an implementation
    must not assume arbitrary point-in-time reconstruction is possible.
    **Historical configuration vs historical facts:** distinguish transaction facts from
    configuration/policy. OTIF tolerance, cancellation and fill-rate rules, period status, QC location
    and exchange-rate policy are configuration. A policy-dependent as-of KPI must resolve the policy
    version effective at the evaluation date (Table B); otherwise "as-of" is only partially historical.
K6. Optional `ProcKpiDaily` snapshot behind a feature flag.
K7. Consume the period-control foundation (see the cross-cutting work item below). K does not own period
    control; it only reads the resulting period semantics for period-based KPIs.
K8. Data-quality gates per P0.12 — each KPI dimension classified Mandatory / Conditional / Optional.
    Suggested: Supplier reject; Currency/rate reject; Category reject or conditional by item policy;
    Department conditional; Project conditional; Buyer conditional or reject by document type.
    Do not make all KPI dimensions hard failures automatically.
K9. Backfill scripts plus `scripts/report-procurement-backfill-gaps.sql`; report, never silently rewrite.
K9a. **Event-less historical documents.** A PO created before cutover but received after cutover has a
     RECEIVE event but no ISSUE event, so its cycle time is incomplete. Classify these as
     pre-cutover/incomplete using the configured `ProcurementEventCutoverUtc` (P0.16) rather than an
     inferred document date, never infer the missing event, and surface them in the cutover report.
K10. **Index review as an explicit task**: `PoDocEvent` around actual query patterns (DocType+DocNo;
    DocType+EventCode+EventDateUtc; EventCode+EventDateUtc), and review indexes for supplier,
    category, buyer, department, project, PO date, promised date, receipt completion, invoice date.
    Do not blindly add every index.

---

### Cross-cutting work item — Period-control foundation (extracted from Phase K)

PC1. Period control is transaction integrity, not a reporting feature, so it is NOT owned by Phase K.
     No period-close infrastructure exists in the Blazor codebase today (verified: `AdPara.CurrentMonth` /
     `CurrentYear` survive only in legacy WebForms docs), so this is net-new work.
PC2. Deliverable per P0.11, values frozen: scope = CompanyCode + BranchCode; a new fiscal-period table
     owned by this work item; lock and reopen authority = a dedicated finance-admin permission; reopen
     reason = mandatory; a locked period rejects GR/NG and invoice posting; draft editing is not blocked;
     rollback of a posted document inside a locked period is rejected; the lock keys on the business
     document/posting date.
PC3. Posting services reject GR/NG and invoice posting into locked periods; rollback inside a locked
     period follows the same rule. **Posting-date rule:** the posting date is validated against period
     status INSIDE the final posting transaction, so a draft created in an open period and later dated
     into a locked period is rejected at post time. Changing a POSTED transaction's posting date through
     ordinary edit paths is prohibited outright.
     **Concurrency atomicity:** period-status validation and document posting must participate in a
     concurrency-safe transaction so a successful period lock cannot race with a posting that was
     validated against the previous OPEN state. The lock operation and the posting validation must
     contend on the SAME period row (an update lock or equivalent serialization), making the sequence
     "T1 checks OPEN / T2 locks / T1 posts" impossible. A posting that loses the race fails cleanly and
     is retryable by the user.
PC4. Lock and reopen operations are audited in a SEPARATE administrative audit table (`PeriodAudit`),
     not in the procurement event log. Period lock/reopen is administrative control activity, not a
     procurement document state transition, so mixing it into `PoDocEvent` would pollute document KPIs.
PC5. Phase K consumes the resulting period semantics for period-based KPIs; it does not implement them.

---

## Dependency order

Phase 0 precedes everything. Phase A is the mandatory clock foundation for all subsequent production
work. Phase B is the mandatory prerequisite for event-based historical KPIs. The Period-control
foundation precedes any posting feature being called complete, because it gates whether GR/NG and
invoice posting are permitted at all. C, D, E, F, G, H, I proceed after A according to their own
dependencies. J depends on E. K depends on B, C, D, E, F plus invoice data. Phase I depends on D for
base-currency amounts.

Narrative graph: Phase 0 → Phase A. From A: B, C, D, E, F, G, H, I in sequence or in parallel as
capacity allows. G is independent of posting and may run parallel to Wave 3. Phase I is split: its
schema, state model and outbox land in Wave 3; the external LHDN adapter is out of scope. The
Period-control foundation runs alongside and must land before F and the invoice posting work are called
complete. E → J. B, C, D, E, F → K. D → I (base amounts).

---

## Implementation waves

Wave 0: Phase 0 (semantic contract)
Wave 1: A (Clock) + B (Event Spine)
Wave 2: C (Delivery) + D (Spend / Base Currency) + E (Reasons)
Wave 3: Period-control foundation + F (GR/NG Operations) + I (Compliance schema/state/outbox).
        G (PO Commercial) stays parallel — it has no posting dependency.
        I is split: schema, state model and outbox first; the external LHDN adapter is later and
        explicitly out of scope (Phase I boundary).
Wave 4: H (Printing) + J (Sourcing)
Wave 5: K (KPI service, reconciliation, backfill validation)

---

## Relevant files

Prerequisite / shared
- `ErpWeb.Core/Services/CurrentDateService.cs` — clock convention
- `ErpWeb.Model/Data/AppDbContext.cs` — add every new DbSet
- `ErpWeb.Core/Menus/MenuCodes.cs`
- `docs/procurement-semantics-contract.md` — Phase 0 deliverable
- `scripts/` — idempotent DDL, seeds, menu init, backfill, gap report

KPI spine / dates
- `ErpWeb.Model/Entities/Purchase/PoOrder.cs`, `PoOrderDetail.cs`, `PoPr.cs`, `PoPrDetail.cs`
- `ErpWeb.Model/Configurations/Purchase/PoOrderDetailConfiguration.cs`, `PoOrderConfiguration.cs`, `PoPrConfiguration.cs`
- `ErpWeb.Core/Purchase/PoOrderService.cs`, `PoOrderCalc.cs`, `PoStatusPolicy.cs`, `IPoOrderService.cs`
- `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`, `IIvInventoryPostingService.cs` — GR/NG/VR
  write-back, milestones, operation-id exposure

Spend / reasons / commercial
- `ErpWeb.Core/Purchase/PoMasterRefService.cs`, `IPoMasterRefService.cs`
- `ErpWeb.UI/Purchase/Masters/PoRefListPageBase.cs`, `PoCategoryList.razor`
- `ErpWeb.Model/Entities/Purchase/PoVendorByItem.cs`

GR operations
- `ErpWeb.Model/Entities/Inventory/IvTrxBatch.cs`, `IvTrxBatchDetail.cs`
- `ErpWeb.Model/Configurations/Inventory/IvTrxBatchConfiguration.cs` + new attachment/QC configs
- `ErpWeb.Core/Inventory/IvGoodsReceiptService.cs`, `IIvGoodsReceiptService.cs`, `IvTrxConstants.cs`
- `ErpWeb.Core/Purchase/PoOrderAttachmentService.cs`, `AttachmentStorageOptions.cs`
- `ErpWeb.UI/Inventory/Transactions/IvGoodsReceipt.razor(.cs)`, `IvGoodsReceiptList.razor(.cs)`

Invoice / CDN / compliance
- `ErpWeb.Model/Entities/Purchase/PoInvoice.cs`, `PoInvoiceDetail.cs`, `PoCdn.cs`
- `ErpWeb.Model/Configurations/Purchase/PoInvoiceConfiguration.cs`, `PoCdnConfiguration.cs`
- `ErpWeb.Model/Configurations/Sales/SaCdnConfiguration.cs` — e-invoice field comparison source
- `ErpWeb.Core/Purchase/PoInvoiceService.cs`, `PoInvoiceCalc.cs`, `PoCdnService.cs`
- `ErpWeb.Model/Entities/Sales/SaTaxGroup.cs`, `SaCurrRate.cs`
- `docs/cdn-logic.md`

UI (fields and actions only)
- `ErpWeb.UI/Purchase/Transactions/PoPr.razor(.cs)`, `PoOrder.razor(.cs)`, `PoOrderList.razor(.cs)`,
  `PoInvoice.razor(.cs)`, `PoCdn.razor(.cs)`
- `ErpWeb.UI/Purchase/_Imports.razor`

Tests
- `ErpWeb.Tests/PoOrderCalcTests.cs`, `PoOrderServiceTests.cs`, `PoPrServiceTests.cs`,
  `PoInvoiceServiceTests.cs`, `IvGoodsReceiptServiceTests.cs`
- New: `PoDocEventWriterTests.cs`, event/state atomicity and historical-compatibility suites

---

## Verification

1. Build the solution; no new warnings in touched projects.
2. Phase 0 gate: the semantic contract exists, its traceability table is complete, and every Phase B–K
   requirement maps to a P0 decision or reference table. An untraceable requirement fails the gate.
3. Event atomicity invariant: a successful commit yields state change = 1 and event = 1; a business
   rollback yields state change = 0 and event = 0. Cover **PO, GR, invoice and CDN**, not only GR.
4. Idempotency: a retried posting with the same operation id must not create a second event.
5. SQL Server concurrency: new writes take locks in the same order as the existing GR path
   (header, then details, then numbering) and cannot deadlock.
6. Clock: forced timezone proves audit stamps are UTC and document dates are company-local, including
   a near-midnight MYT (UTC+8) case.
7. **Historical compatibility for the PO local-to-UTC change**: old records, newly created records,
   date filters, sorting, reports, API serialization, UI display, near-midnight MYT. Treat as a
   compatibility item, not just a clock unit test.
8. DDL: run every new script twice on a restored DB copy; second run is a no-op and prints its
   "already exists" branch.
9. Backfill: gap report before and after; count drops and every remaining row is explained; no silent rewrites.
10. GR QC: receive into hold, release partial, reject partial; stock lands only for accepted qty, and
    the VR is raised after inspection finalisation through the normal VR path.
11. KPI reconciliation on a seeded month: category spend in MYR equals the sum of PO base amounts;
    OTIF counts match a manual complete-delivery-against-promise count; every KPI matches its P0.3
    definition.
11a. **Invoice base-currency reconciliation**: invoice MYR totals equal the sum of invoice-line MYR
     amounts and agree with the invoice calculation source within the defined rounding rules.
11b. **Rounding and tax reconciliation matrix**: foreign amount -> converted line amount -> tax -> WHT
     base/amount -> document total -> MYR total. Cover 0-decimal and 2-decimal currencies, rounding half
     cases, negative CDN amounts, zero tax, exemption, and WHT stored-only behaviour. Formulas follow
     existing `PoInvoiceCalc` semantics where applicable.
12. e-Invoice: submit/cancel/resubmit happy path plus an outbox retry path, with no partial writes in
    the document transaction.
13. Browser pass on each touched screen: filters, new/edit/view, cancel, force-close, rollback, dirty
    check, permission denied.
14. Authorization: service-layer permission tests (not UI-only) for reason-code maintenance, GR
    attachment operations, QC release/reject, rollback, print, RFQ award, contract call-off, and
    e-Invoice resubmit/cancel.
15. Scope boundary: **no new procurement dashboard page or component is introduced.** KPI work is
    limited to service, DTO, query and test layers.
16. Append-only enforcement: prove an event cannot be updated or deleted through the application layer.
17. **Retry/re-entry idempotency:** simulate commit-succeeds-then-response-lost, retry the same logical
    operation, and prove the original `OperationId` is reused and exactly one event exists. Cover PO, GR,
    NG, VR, invoice and CDN.
18. **PO historical migration:** run the local-to-UTC conversion on a restored DB copy, prove it is
    idempotent (the marker prevents double-apply), and prove converted values match the expected UTC
    instants, including a near-midnight MYT (UTC+8) case.
19. **Event-code vocabulary:** assert every emitted event code is a member of the frozen vocabulary, and
    that the `PoDocEventCode` foreign key rejects an unlisted code at the database level. Assert in
    particular that `PO_PROMISE_CHANGE`, although optional to emit, is ACCEPTED by the FK because it is
    in the seeded vocabulary (P0.1, B3a).
20. **KPI semantics suite:** PO revised 0 / 1 / 3 times; multiple revisions counted once for revision
    rate; partial receipts with final completion; OTIF tolerance CHANGED between periods (proves policy
    versioning); cancellation before receipt; cancellation after partial receipt; full cancellation;
    over-receipt; GR rollback; VR return; GR rollback plus VR; multiple GRs plus multiple invoices.
21. **Event spine suite:** two concurrent retries with the same `OperationId`; same document and event
    type with different `OperationId`s; an update or delete attempted through the DbContext; an invalid
    `EventCode` insertion; multiple legitimate `GR_RECEIVE` events on one document.
22. **Outbox suite:** first submission; rejection; correction; resubmission; worker timeout; response
    lost after a successful submission; duplicate worker execution; two workers claiming the same attempt
    (only one wins).
23. **Period-control suite:** posting just before lock; posting just after lock; retry after lock;
    rollback after lock; direct service invocation bypassing the UI; finance-admin reopen with a
    mandatory reason; unauthorised reopen; document-date manipulation to dodge the lock; bulk posting.
24. **UTC migration suite:** ordinary date; near-midnight; company timezone resolution; already-applied
    marker; second execution is a no-op; backup and restore validation.
25. **Outbox identity suite:** two distinct `SubmissionOperationId`s for the same invoice; a retry of one
    operation preserves its identity and increments the attempt; a new resubmission starts a new
    operation identity with the attempt back at 1; a concurrent claim where exactly one worker wins; a
    worker crash while `PROCESSING`; stale-claim recovery after `ClaimExpiresOnUtc`; a bounded recovery
    counter.
26. **Promise and required-date history suite:** promise edited before issue; promise edited after issue;
    promise changed after partial receipt; OTIF evaluated against the correct promise version; a PR ETA
    changed after PO creation does not move the PO `RequiredDate`; the KPI does not silently change when
    PR master data changes.
27. **QC valuation suite:** receive 100, reject 20, VR 20; return after accepted receipt; GR rollback
    after QC; QC rejection followed by a later business return; assert the inventory quantity and
    valuation movement is produced once and is NOT reversed twice.
28. **Period-control edge suite:** draft date changed from an open to a locked period; posted transaction
    date modification attempt; direct API/service bypass; concurrent posting against a period being
    locked; a posting that loses the lock race fails cleanly and is retryable.
29. **Promise-version suite:** a `PoPromiseHistory` row is written on every post-issue promise change;
    OTIF uses `OriginalPromisedDate` for a supplier-performance cohort; OTIF uses the cutoff-effective
    promise for an as-of view; editing the promise cannot flip a late delivery to on-time.
30. **Outbox transition suite:** every terminal state is reachable only through its legal transition; a
    retry after FAILED appends a new attempt without mutating the failed row; ABANDONED after the retry
    budget is exhausted; `PoInvoice.CurrentSubmissionOperationId` matches the latest operation and can be
    rebuilt from the outbox.
31. **Promise-history integrity suite:** a duplicate retry of the same promise-change `OperationId` does
    NOT create a second history row (the unique key is absorbed as idempotent re-entry, not surfaced as a
    user-visible duplicate); concurrent promise changes on the same line serialize; two history rows
    sharing a `ChangedOnUtc` resolve deterministically by `PoPromiseHistoryId`; one save changing both
    `RequiredDate` and `PromisedDate` writes the revision, the history rows and both events atomically
    under one `OperationId`, and a failed transaction leaves none of them written.
32. **Outbox field-ownership suite:** a direct attempt to mutate an immutable identity or payload field
    is rejected; lifecycle fields change only through legal transitions; a terminal attempt's response
    fields cannot be overwritten; a terminal attempt cannot be re-opened; the pointer and the first
    outbox attempt commit atomically, so a rolled-back submission leaves no orphan pointer.
33. **Provider ambiguity suite:** the ambiguous-success path (provider accepted, response lost, lease
    expires) is resolved by a status lookup or a provider idempotency key rather than a blind resend, and
    an ambiguous attempt is never marked SUCCEEDED on assumption.
34. **Revision-numbering suite:** `PoRelNo` starts at 1 per logical PO, increments by one with no gaps,
    is allocated inside the posting transaction, and a concurrent or failed write cannot consume or
    duplicate a number.

---

## Decisions

Kept (unchanged)
- UTC audit timestamps plus company-local business dates.
- Append-only document event log.
- Event log starts at cutover; no reconstruction of historical events.
- Stored transaction-time MYR amounts; never recomputed with a live rate.
- Lookup-driven reason codes.
- Quarantine-location QC variant.
- Backfill reports gaps instead of inventing values.
- DTO-first printing.
- No live LHDN call inside the document transaction; outbox architecture.
- Dashboard screens out of scope.
- AP/GL, payments, knock-off, AP aging and supplier statement out of scope.

Changed in v2
- "A blocks B-K" replaced by dependency-specific sequencing plus five implementation waves.
- `ACK` no longer overwrites `PromisedDate` in the current implementation; it is future-compatible only.
- Rejected GR quantity no longer nested-posts a VR; VR follows finalised inspection through normal machinery.
- Report renderer no longer decided in the plan; deferred to Phase H behind the print DTO.
- Aging clarified: AP/payment aging is excluded; procurement document aging including open PR/PO aging
  is in scope (resolves the previous contradiction).
- `PoDocEvent` carries full tenant identity, not `DocType + DocNo`.
- Event idempotency is an explicit contract, not implied by DDL idempotency.

Changed in v3
- Scope boundary restated: document/master/operational UI changes are IN; dashboard screens are OUT.
- K1 corrected to depend on Phases B–F, matching the dependency section.
- `PoDocEvent` unique key and the `OperationId` vs `SourceKey` split are now explicit.
- Append-only is enforced (application layer, since no DB permission model exists) instead of asserted.
- Events are document-level by default, with line-level emission only for line-specific transitions.
- NG gets an explicit behaviour audit instead of inheriting GR assumptions.
- QC on-hand status, costing and valuation during hold are explicit.
- OTIF locked to complete-delivery-against-promise; first receipt is cycle-time only.
- Fill rate capped at 1, with over-receipt measured separately.
- Cancellation KPI cohort eligibility and date basis defined.
- Period control extracted from Phase K into a cross-cutting foundation (Wave 3).
- Phase I corrected to seven work items; resubmit and outbox idempotency defined.
- Snapshot and `CurrRate` immutability after issue is explicit.
- Revision semantics moved into Phase 0 (P0.15).
- "Current" vs "as-of" KPI cutoff defined; cross-cutover cohort rule added (P0.16 and K9a).

Changed in v4
- `OperationId` is the idempotency identity for ALL event types; BatchId/BatchNo never substitute (B6).
- Retry/re-entry rule added: one `OperationId` per logical operation, reused across retries (B6a).
- Exact `PoDocEvent` column contract, nullability and index set frozen (B1a).
- Event-code vocabulary frozen (P0.1); `ACK` reserved but not emitted.
- Phase 0 count corrected from 14 to 16.
- Period-control policy VALUES frozen: Company+Branch, blocks posting, blocks rollback, mandatory reopen
  reason, business-date keyed (P0.11, PC2).
- QC policy VALUES frozen: on-hand, not allocatable, cost at GR, quantity-only acceptance, rejection via
  normal VR, per-warehouse QC location (P0.7, F3a).
- OTIF tolerance frozen: configurable company-level grace period in days (P0.3, K3).
- Fill rate excludes valid cancelled quantity from the denominator (P0.3, K3).
- NG participates in ALL receipt-based KPIs (P0.3, F6, K3).
- Revision-rate KPI is in the first release; revision semantics and event frozen (P0.15, K3).
- PO local-to-UTC historical migration strategy chosen: convert existing values at cutover, idempotent
  and marker-guarded (A3).
- Cutover boundary is a configured `ProcurementEventCutoverUtc`, never inferred (P0.16, K9a).
- As-of reporting limited to KPIs with historical state; others are current-only (K5).
- Malaysia compliance stated as a data/state/integration foundation, not statutory certification.

Changed in v5
- e-Invoice state vocabulary frozen; `INVALID` removed as a persisted state (P0.9, I6).
- Outbox attempt model frozen: immutable attempt rows, monotonic `SubmissionAttempt`, exact unique key,
  retry appends (I2a, Table E).
- `CompletelyReceivedOn` is now a FACTUAL stamp; OTIF tolerance is applied only by the KPI (P0.5, K3).
- KPI-affecting configuration is effective-dated via `ProcurementKpiPolicyHistory` (P0.3, Table B, K5).
- Logical PO identity (`PoNo`) separated from revision identity (`PoRelNo`) (P0.10).
- Revision rate redefined over logical POs with at least one revision, plus a separate frequency metric
  (P0.15, K3).
- Period audit destination frozen: a separate `PeriodAudit` table, not `PoDocEvent` (P0.11, PC4).
- NG scope matrix frozen for all seven capabilities; nothing left open (F6, Table F).
- Event vocabulary is DB-enforced via the `PoDocEventCode` reference table and FK (B3b).
- Append-only wording corrected to application-enforced, with DBA hardening as a deployment item (B3b).
- Operation-identity propagation contract frozen, including multi-batch sharing and rollback identity
  (B7, Table C).
- Multiple same-type events per document explicitly permitted with distinct `OperationId`s (B5b).
- PO receipt-to-invoice lag renamed and given a cohort/matching rule (K3).
- Return rate excludes technical rollback from numerator and denominator (K3).
- Fill-rate edge rules frozen: denominator <= 0 is N/A, cancellation capped at ordered minus received (K3).
- As-of KPIs must resolve the policy version effective at the evaluation date (K5).
- PO migration uses the company's configured timezone, not a hard-coded offset (A3).
- Phase 0 reference tables A–F added.

Changed in v6
- Outbox identity split: `SubmissionOperationId` (logical operation) plus `SubmissionAttempt`, with the
  unique key including both; a retry reuses the operation, a resubmission starts a new one (Table E, I2a).
- Outbox column contract frozen with nullability and lifecycle stamps (I2b).
- Worker claim concurrency frozen: atomic PENDING to PROCESSING with lease fields and bounded stale
  recovery (I2c).
- e-Invoice vs outbox state responsibility matrix added (Table G, I2d).
- `OriginalPromisedDate` immutable at issue plus promise-change history, closing the OTIF-rewrite hole
  (C1, C3a).
- `RequiredDate` frozen as a PO snapshot; a PR ETA change cannot move it (C2a).
- Fill-rate "closed quantity" clarified: close is a status, not a quantity, so it is not subtracted (P0.3).
- Receipt-to-invoice lag aggregation frozen as `LastInvoiceIssueDate - LastReceiptDate`, excluding
  out-of-order POs (K3).
- Return-rate denominator frozen as gross received including QC-rejected quantity (K3).
- Period posting-date rule frozen: validated inside the final posting transaction, posted dates immutable
  (PC3).
- `Company.TimeZoneId` confirmed as an existing verified field, not new configuration (baseline, A3).
- Event ordering key `PoDocEventId` added because timestamps can collide (B1a).
- Event timestamp vs business date separation stated (B11).
- `PoDocEventCode.IsActive` semantics frozen (B3a).
- KPI policy-history overlap constraint frozen (Table B).
- Retention wording corrected: freeze after legal/tax-owner confirmation, do not infer statutory retention.
- Scope rule added: business semantics may not remain open after Phase 0; substitutable technical choices
  may.
- Phase 0 reference tables A–G (Table G added).

Changed in v7
- Promise history frozen to a SINGLE mechanism: an immutable `PoPromiseHistory` table is authoritative,
  with `PoDocEvent` optional as an additional business event (C3a, Table H).
- The OTIF promise version is frozen: `OriginalPromisedDate` for supplier-performance cohorts, the
  cutoff-effective version for as-of views (C3a, K3).
- `RequiredDate` is immutable WITHIN a revision; changing it requires a PO revision (C2a), and P0.15 now
  distinguishes commercial dates from commitment dates.
- `PromisedDate` changes are commitment tracking, not revisions (C1, P0.15).
- K3 fill rate no longer references a "closed quantity" exclusion, removing the contradiction with P0.3.
- KPI renamed to "PO Final Receipt-to-Final Invoice Lag" so the final-stage semantics are self-evident.
- Outbox terminal states, legal transitions and retry ownership frozen (I2c).
- `PoInvoice.CurrentSubmissionOperationId` added as a convenience pointer, with the outbox still
  authoritative (I2e).
- Period lock versus posting concurrency atomicity frozen: both contend on the same period row (PC3).
- `PoDocEventId` explicitly defined as persistence order, not business causality (B1a).
- NG entry reframed as a technical field-mapping task, not an open business question.
- Phase 0 reference tables A–H (Table H added).
- Phase 0 contract gains a TRACEABILITY TABLE mapping every Phase A–K requirement to its originating P0
  decision or reference table, making the Phase 0 gate machine-checkable (the sixth review's final P2 item).

Changed in v8
- `PO_PROMISE_CHANGE` added to the frozen event vocabulary (P0.1) so the `PoDocEventCode` foreign key
  seeded from it (B3a) accepts the event C3a already relied on. It stays optional to emit because
  `PoPromiseHistory` is authoritative; when emitted it uses the promise-change `OperationId`.
- The outbox "attempt rows are immutable" contradiction removed: field-scoped ownership replaces it
  (Table E, I2a, I2c) — identity and payload immutable, lifecycle and lease fields mutable only through
  legal transitions, response fields write-once at terminal completion, terminal attempts never re-used.
- `PoPromiseHistory` gains `PoPromiseHistoryId BIGINT IDENTITY` as primary key plus the idempotency key
  `(CompanyCode, BranchCode, PoNo, PoRelNo, LineNo, OperationId)`, so a retry is absorbed rather than
  duplicating history (Table H).
- Promise-history cutoff resolution made deterministic: `ChangedOnUtc DESC, PoPromiseHistoryId DESC`,
  falling back to `OriginalPromisedDate`, so equal timestamps no longer make as-of OTIF ambiguous
  (Table H, C3a).
- Same-save atomicity frozen (C3b): a save changing both a commercial field and `PromisedDate` writes the
  revision, the history rows and all events in one transaction under one `OperationId`.
- `PoInvoice.CurrentSubmissionOperationId` is now transactionally coupled to the first `IrbOutbox` attempt
  row, so the pointer can never reference an operation with no outbox row (I2e).
- Provider idempotency and ambiguous-success recovery added as an integration seam (I2f): correlation key
  where supported, reconciliation lookup before resubmission otherwise, never SUCCEEDED on assumption.
- `OperationId` non-reuse rule stated explicitly: a new unrelated operation MUST get a new `OperationId`
  even on the same document and event type (B6a).
- `PoRelNo` numbering convention frozen: per logical PO, first revision 1, increment by one, no gaps,
  allocated inside the posting transaction (P0.15).
- Verification items 31–34 added for promise-history integrity, outbox field ownership, provider
  ambiguity and revision numbering; item 19 extended to assert `PO_PROMISE_CHANGE` is FK-accepted.

---

## Further Considerations

1. **QC hold location setup (Phase F3).** The QC policy itself is now frozen (on-hand, not allocatable,
   cost at GR, quantity-only acceptance, rejection via normal VR). Remaining setup item: whether a QC
   hold location already exists in `IvWarehouse`/`IvLocation` per site, or must be created before F3
   starts. Quarantine-location keeps the batch status model intact but consumes a warehouse/location per
   site and changes where stock is visible between receipt and release.
2. **AP dependency for compliance depth (Phases I5–I6).** WHT is stored but not deducted and SST-02
   cannot be finalised until an AP/payment ledger exists. If Malaysian filing is near-term, pull the
   AP/GL slice forward; otherwise ship I5–I6 with an explicit interim-limitation note.
3. **Operation-id exposure (Phase B7) — decided.** Extend the existing `IvInventoryPostingResult` and
   `IvInventoryPostingBatchResult` with `OperationId` rather than introducing a parallel key. It is
   additive, and the operation id is already generated in every posting path. The change still touches
   eight inventory document types and their tests, so schedule it as its own reviewable step.
4. **NG scope parity (Phase F6) — resolved, mapping only.** Table F freezes all seven capabilities; no
   business decision remains open. During implementation, map the frozen delivery-header requirement onto
   the existing NG schema. That is a technical field-mapping task, not a business decision.
5. **Cutover operational runbook (new).** Because A3 changes the meaning of an existing production
   column, publish a runbook before execution: (1) stop or drain writes; (2) back up; (3) record the
   cutover UTC and configure `ProcurementEventCutoverUtc`; (4) run the PO timestamp migration; (5)
   deploy the schema scripts; (6) enable event emission; (7) verify with the migration and gap reports;
   (8) resume traffic; (9) re-run the gap report after one business day.
6. **Event-log retention and archive (P2).** `PoDocEvent` is append-only and will grow. Freeze an online
   retention window AFTER legal/tax-owner confirmation: do NOT infer statutory retention from this
   technical plan, because the plan explicitly avoids statutory certification. Then define the archive
   strategy, confirm archived events stay queryable for as-of KPIs, and ensure the KPI service resolves
   archived ranges rather than silently under-reporting.
