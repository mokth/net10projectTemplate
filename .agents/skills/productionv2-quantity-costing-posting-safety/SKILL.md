---
name: productionv2-quantity-costing-posting-safety
description: Mandatory repository-first safeguards for any productionv2 ERP task that creates, edits, posts, reverses, imports, recalculates, reports, or otherwise affects stock quantity, available/reserved stock, PO/PR/SO/DO quantities, production WIP, FIFO/CostLayer, inventory valuation, COGS, landed cost or purchase price variance. Verify SQL transactions, application locks, database locks, idempotency and fingerprints when applicable, plus business-rule controls. Use for Inventory, Sales, Procurement, Production, UI/API, jobs, migrations, reports, and tests.
---

# Productionv2 — Quantity, Costing & Posting Safety (Five Protections)

## 0. Purpose, authority and activation

**Activate for EVERY task that may affect quantity or costing — directly or indirectly.** This includes UX simplification, adding an import/API, editing a posted document, changing a lookup/filter used by a posting flow, performance tuning, changing a report's source, and a seemingly harmless refactor. If uncertain, activate the skill.

**This skill supplements, never replaces, the existing skill:**

- **MUST read** `.agents/skills/productionv2-costing/SKILL.md` before planning, editing, reviewing or approving.
- **MUST read** `.agents/skills/productionv2-costing/references/stock-balance.md` if any quantity/availability/WIP or physical movement could change.
- Load the relevant existing `references/{inventory,sales,procurement,production,replay-rollback,testing}.md` according to the actual workflow.
- If a UI task, also read the existing module UI skill; UI simplicity must not weaken backend guards.

**Source of truth:** current working tree of `mokth/net10projectTemplate`, branch **`productionv2`**, including uncommitted local changes; **never assume default `main` is equivalent**. Pin and report the current commit and local diff. A skill or earlier conversation is not proof that a route is actually wired, enabled or safe. If source or critical evidence is missing, report **NOT VERIFIED** instead of inventing guarantees.

**Hard rule:** do not change quantities, costing, CostLayer/FIFO, WIP, stock ledger, DB schema, transaction semantics or rollout flags without explicit task authorization, a source-grounded impact plan, and evidence. Never bypass existing conservative blockers to make a workflow pass.

## 1. First classify WHAT the operation actually changes

Before choosing locks or idempotency, identify **the real authority and side effect**. Do not assume all document types post stock.

| Area / path | Identify the actual authoritative side effects |
|---|---|
| Inventory MR, MI, GR, adjustments, transfers, counts, scrap, returns | Physical stock slices, inventory history, V2 ledger owner, cost state, FIFO layers, active valuation generations. |
| Sales DO | Shipment/SP stock-out **when applicable**; SO/DO allocations; COGS and status. |
| Sales Invoice | **Direct shipment** can own stock-out; linked-to-DO lines must **not issue twice**; billing/allocations/status still require concurrency protection. |
| Sales CN | Customer Return/CR stock-in **only if supported `ReturnStock` and stock-controlled lines**; financial-only CN does not change stock. |
| Sales DN | Typically financial-only under current rules; inspect actual code before claiming any inventory movement. |
| Procurement PR / PO | Approval, revisions, document identity, **PR remaining quantity/PO consumption**, receive commitments, authorizations. PR/PO normally do **not** require a stock-ledger posting RequestId merely to save/approve. |
| Purchase GR/PI/CN/DN | Physical GR/vendor return when applicable; PO received/invoiced quantities; receipt settlement, PPV, WAC/FIFO/Standard cost adjustments and downstream consumed value. |
| Production IP, daily/output, FG, returns, subcon, transfers | RM inventory issue and receipt, production stock/WIP pools, operation output, allocated values, FG receipt, reversal and downstream sales cost. |

**For each touched public entrypoint record:** user action → `Post/Save/Submit/Approve/Rollback` method → trusted company/branch scope and permission → database transaction and lock order → eligibility/status/period/date → quantity changes → cost changes → history/ledger → downstream effects → rollback/repost. Trace the **public path**, not only a helper that might never be called.

## 2. The FIVE distinct protections — required review gates

**These are five different controls, not five locks.** For each route, mark each control **APPLIES + VERIFIED**, **APPLIES + MISSING**, **NOT APPLICABLE (with reason)**, or **NOT VERIFIED**. Do not automatically add all five to every PR/PO/Sales/Procurement operation.

### P1 — Database transaction: atomic business effects

- Use the *existing* transaction pattern (usually EF Core `BeginTransactionAsync`) to cover all mutations for **one logical posting root**: document state, allocated quantities, stock, valuation, financial adjustment, audit/history, and sealing.
- Multiple `SaveChangesAsync` calls inside a single transaction are **not multiple commits**. The authoritative commit is `CommitAsync`; exceptions/cancellation/failed guards must roll back **all effects**.
- Verify nested service calls use the **same `AppDbContext` and active transaction** where required; never inadvertently create a new independent transaction for stock or costing.
- Check client-disconnect ambiguity: if COMMIT succeeded but response was lost, a retry must not repeat the physical/financial effects.
- A batch UI selection may process documents in **separate per-document transactions**. Identify and document partial-success behaviour; never assert the whole selection is atomic unless it actually is.
- Validate exception paths, explicit early returns, `finally`/abort logic, transaction disposal and pooled SQL-session cleanup. No orphan `NEW`/`POSTED` statuses, ledger roots, allocations or cost facts.

**Fail:** stock/history is saved but document status fails, or purchase/WIP cost gets committed independently of its owner without a designed reversible linkage.

### P2 — Application lock: serialize cooperating shared-stock/cost operations

- When entering a **V2 stock posting path**, inspect actual use of `IBranchStockTransactionLock` / `StockPostingCoordinator`; the present SQL Server pattern uses `sys.sp_getapplock`, exclusive lock, transaction owner, resource `STOCK_WRITE|Company|Branch` and a bounded timeout.
- **Not every Sales/Purchase document invokes this branch lock.** A DO/direct invoice/customer return/vendor return can use it *through the stock ledger when stock movement is applicable*; commercial-only transactions may instead rely on document locks, status, constraints and approval/quantity guards.
- For a shared quantity or costing authority, confirm **all competing paths cooperate under compatible locking**. Cross-document races matter: DO versus direct INV; two POs against one PR; GR versus purchase price variance; inventory issue versus Production IP/FG.
- Acquire in a proven consistent order relative to document/PO/PR/balance row locks. **Never move or add an application lock blindly**; inspect current ordering across callers and test for deadlock/lock inversion, including `NOWAIT` and retry handling where present.
- A branch-level lock is scoped to the **same SQL database + named resource**, not a global lock across tenant databases. It coordinates participating paths only; it does not itself validate sufficient stock or costing.

**Fail:** two cooperating writers can both validate stale stock/PR availability or allocate the same FIFO layer while bypassing the authoritative lock.

### P3 — Database locking + concurrency: protect the actual records

- Inspect the relevant repository's `LockForUpdateAsync`, row-lock statements (e.g. SQL Server `WITH (UPDLOCK, HOLDLOCK)`), ordered per-slice balance locks, and document `RowVersion`/concurrency handling.
- Lock **the smallest authoritative set that is sufficient**, in consistent order, while preserving current indexes/performance. Do not create SQL lock hints in unrelated code by default.
- **Re-read and revalidate inside the lock/transaction**: document status, quantities, remaining allocations, source revision, period, UOM, stock availability and cost evidence. A UI preflight or an out-of-transaction preview is not authoritative.
- Verify database uniqueness/foreign keys where the current design uses them, and preserve handling for deadlocks, timeouts, optimistic-concurrency conflicts and retries.
- `RowVersion` protects against stale updates; it is not, by itself, a complete duplicate-post or cross-document quantity-allocation design.

**Fail:** two PO saves can over-consume PR quantity, or two different shipment documents can both rely on the same stale availability snapshot.

### P4 — Idempotency: the same logical operation has one effect

- In the **V2 stock ledger**, verify deterministic or reliably persisted `RequestId`, source document + revision + posting role identity, and DB uniqueness. The current `StockPosting` model has a unique `(CompanyCode, BranchCode, CommandType, RequestId)` index and separate unique source document/revision/role identity.
- Inventory posting command factory derives a deterministic request ID from company/branch/batch/revision/role; purchase-cost posting command factory hashes its semantic request. **Do not replace this with a fresh random GUID on every retry.**
- On a repeated sealed request with the **same** fingerprint, the coordinator must not rerun the handler; an **unsealed** or incompatible record is **not** a successful posted result. On a reused RequestId with different fingerprint, reject.
- **Document-level status guard** (e.g. a posted DO, invoice or CN/DN) and **stock ledger idempotent replay** are different layers: a second document POST can return a status conflict while the stock coordinator prevents duplicate ledger effects.
- When rollback followed by a legitimate repost is allowed, verify source revision/posting role changes and exact reversal lineage. Do not reuse the old primary identity for a new cycle or increment revision merely because a browser retried.
- PR/PO approval submission uses its **own** instance/status/fingerprint/idempotency-key mechanisms, not automatically `StockPosting.RequestId`. PR→PO quantity consumption has separate uniqueness/remaining-quantity protections. Verify *creation* retries separately from *approval* retries.

**Fail:** browser closes after COMMIT, user retries, and stock/COGS/receipt value/PR consumption is applied twice.

### P5 — Fingerprints: reject changed or stale semantic input

- A fingerprint is a **content/evidence check**, **not a lock**. Use the existing canonicalization and hash implementation; do not invent a second hashing scheme.
- In the V2 ledger, distinguish `StockPosting.RequestFingerprint`, `SourceSnapshotHash`, and recorded source snapshot. A matching RequestId with a different request fingerprint must fail.
- Also recognize **different** fingerprints in other workflows: Sales CN customer-return batch `IvTrxBatch.SourceFingerprint`; Purchase CN vendor-return batch source fingerprint; Procurement approval material fingerprint; historical replay-plan fingerprint. These are **not necessarily the same field or same purpose**.
- Hash only the authoritative, stable semantic inputs for that operation. Ensure quantities, item/UOM/warehouse/lot, revision, source ownership and effective date are represented **where required by the current contract**, without introducing transient UI state or nondeterministic serialization.
- For read-then-write costing/replay plans, **recompute evidence under the final locks** and reject stale plans before any mutation.

**Fail:** a changed line qty/cost or source association gets accepted as the same idempotent request, or a preview calculation is blindly used after its evidence changed.

## 3. Sixth mandatory gate — business invariants (never optional)

Even a perfect transaction and locking design can commit **wrong arithmetic**. Validate these under the correct time and policy:

1. **Quantity:** reconcile opening + posted IN − posted OUT ± transfers/adjustments/reversals = ending **per trusted company, branch, item, warehouse, location, lot, status and base UOM**. Honor reservations versus physical stock; use actual repo sign/UOM/negative-stock rules.
2. **FIFO/CostLayer:** exact active receipt-layer eligibility, allocations, partial consumption, returned cost lineage, active generation, original source owner and reversal; no double-consume or invented average cost.
3. **WAC / Standard:** existing chronological, precision, variance and effective-dated method; reconcile item cost state, stock valuation facts, physical quantities, purchase price variance, remaining-stock versus already-consumed value.
4. **Sales:** one physical shipment owner (DO or direct invoice); linked invoice must not issue again; CN ReturnStock creates eligible stock-IN; financial-only CN/DN must not quietly change on-hand. No orphan SP/CR batches.
5. **Procurement:** PR→PO remaining qty, PO→GR received, GR→PI invoice settlement, CN/DN adjustments, supplier return and FX/tax distinction. Rollbacks may be blocked by downstream invoices/returns/consumption.
6. **Production:** material issue → RM stock decrement + production material-in → WIP operations → FG receipt; preserve lot/pool/WO lineages, partial yield, scrap, returns, subcon and downstream sale cost. WIP quantity AND value must reconcile.
7. **Date, period, audit:** NEW draft versus future POST rules, transaction-effective business date, open/closed periods, history preservation, reversal/repost and permission checks at server boundary. Reject unsupported backdated or consumed-root replay atomically.
8. **Multi-tenant:** never rely on a UI-provided CompanyCode/BranchCode alone for write authority; keep trusted scope and ownership of all linked records.

## 4. Existing patterns to inspect first (examples, not a substitute for search)

The following were present in `productionv2` at verification commit `54fe856a1d0cdace4ef23e4061b7f7237fb7682b` (2026-11-26 UTC). **They can move or change; search the current tree on every use.**

| Responsibility | Existing code to inspect |
|---|---|
| Shared V2 stock transaction, identity, seal/replay | `ErpWeb.Core/StockLedger/StockPostingCoordinator.cs` |
| SQL Server transaction-owned branch application lock | `ErpWeb.Core/StockLedger/BranchStockTransactionLock.cs` |
| Inventory request ID / source fingerprint | `ErpWeb.Core/StockLedger/InventoryStockPostingCommandFactory.cs`, `StockPostingFingerprint.cs` |
| Purchase costing request ID | `ErpWeb.Core/Purchase/PurchaseCostPostingCommandFactory.cs` |
| DB uniqueness and evidence | `ErpWeb.Model/Configurations/StockLedger/StockPostingConfiguration.cs` |
| Sales shipment and customer return | `ErpWeb.Core/Sales/SaDoService.cs`, `SaInvoiceService.cs`, `SaCdnService.cs` |
| Sales document locks | `ErpWeb.Model/Repositories/Sales/SaDoRepository.cs`, `SaInvoiceRepository.cs`, `SaCdnRepository.cs` |
| PR and PO quantity/approval | `ErpWeb.Core/Purchase/PoPrService.cs`, `PoOrderService.cs`, `ProcurementApprovalService.cs` |
| Purchase invoice / CN/DN costing and vendor return | `ErpWeb.Core/Purchase/PoInvoiceService.cs`, `PoCdnService.cs`, `PoCdnService.Replay.cs` |
| DB test examples | `ErpWeb.Tests/Procurement/Transaction/*SqlServerConcurrencyTests.cs`, `ErpWeb.Tests/Inventory/Transaction/IvInventoryPostingSqlServerConcurrencyTests.cs` |

**Do not claim every route uses all five controls just because one shared helper exists.** Prove the public path reaches it and the feature/epoch is active in the relevant tenant.

## 5. Required adversarial verification matrix

For each affected public route, mark each scenario **PASS / FAIL / NOT RUN / NOT APPLICABLE** with concrete test names, provider and observed database evidence:

- Duplicate same-document POST: sequential twice and **two concurrent requests**; exactly one authoritative effect; validate status and cost/qty history.
- **Two different documents competing for same authority**: DO versus direct Invoice for stock; PO-A versus PO-B on PR remaining; GR versus later cost replay; simultaneous WIP consumers; negative stock/over-allocation blocked.
- Browser lost response **before COMMIT**, **after COMMIT**, retry after restart; distinguish status conflict from idempotent successful ledger replay.
- Fault injection after stock mutation, after history, after costing, before document status, before COMMIT; no partial committed state.
- Rollback after downstream use, reversal, and repost cycle; correct exact inverse, new revision identity, no duplicate history/CostLayer/WIP movement.
- Multi-line and partial operations, split FIFO layers, cross-warehouse/lot/UOM, same/different company/branch, zero stock, rounding.
- WAC / FIFO / Standard cost methods, PPV settlement, already-consumed inventory, Sales COGS/returns, Production FG/WIP; reconciliation at business-effective cutoffs.
- Future-dated draft allowed if existing rule; future POST blocked; closed period and backdated replay blockers enforced at authoritative lock.
- PR/PO approval: simultaneous submit/approve/reject, stale material fingerprint, prior approval instance, unauthorized actor, same PR line consumed by multiple POs.
- Deadlock/timeout/lock inversion and SQL session marker cleanup on pooled connections; retry only with safe idempotent semantics.

**Mandatory SQL Server concurrency evidence** for any changed locking/transaction/posting code. SQLite/unit tests cannot prove `sp_getapplock`, `UPDLOCK`, transaction isolation or SQL Server deadlock behaviour. Existing SQL Server tests may silently skip when no scratch DB is configured: report actual executed versus skipped test counts. Do not claim tests passed merely because test classes exist.

## 6. The agent's mandatory operating procedure

**Before coding or approving**

1. Verify branch, HEAD, local diff, feature flags and actual tenant/DB provider; read both this skill and `productionv2-costing` guardrails.
2. Inventory every affected **public write/rollback/repost entrypoint**, including non-stock and mixed stock/financial variants. List actual DB tables/entities affected and which module owns each mutation.
3. Draw call path UI → service → repository → locks/transaction → authoritative quantity/cost writer → DB uniqueness → downstream consumer → rollback/replay.
4. Produce a **P1–P5 Applicability & Evidence table** (template below); record reasons for N/A and every uncertain claim.
5. Write quantity/value reconciliation equations, lock order, duplicate/retry behaviour, known blockers and acceptance tests **before modifying**.

**During implementation**

6. Prefer smallest code change using existing repositories, coordinators, writers, business rules and permission checks. Preserve guard order; no new parallel posting engine, ad-hoc `CostLayer` writes, catch-and-continue, lock inversion or silently loosened validation.
7. Recheck quantities, statuses, fingerprint/plan and time-period constraints **inside** the final transaction and authoritative lock.
8. Ensure cancellation, exception and early-failure paths roll back all changes and clear any SQL session markers. Preserve audit and transaction history.

**After implementation or review**

9. Run focused tests, SQL Server contention tests, rollback/repost tests and cross-module reconciliation. Record pass/fail/skips, exact commands, commit and providers. If a test cannot run, state **NOT VERIFIED**.
10. Report unchanged vs changed behaviour, unresolved gaps, business impact, detailed rollback plan, and **GO / NO-GO**. Do not mark 10/10 or production-ready without complete evidence and explicit approval for high-risk changes.

### Required P1–P5 evidence table (fill per public route)

| Route + variants | P1 Transaction | P2 Application lock | P3 DB lock/concurrency | P4 Idempotency | P5 Fingerprint | Qty/value + rollback evidence | Status |
|---|---|---|---|---|---|---|---|
| `<actual public method>` | `<method:line + transaction scope>` | `<resource/acquire or N/A rationale>` | `<row lock/version + order>` | `<document status or RequestId + DB constraint>` | `<hash source/check or N/A>` | `<tables/tests/reconciliation>` | `VERIFIED / PARTIAL / BLOCKED / NOT VERIFIED` |

## 7. Stop / reject conditions

**NO-GO** if any applicable condition occurs:

- No source-backed proof of posting/rollback path, quantity/cost authority, committed scope, or trusted tenant identity.
- Physical, reserved, PO/PR, WIP, COGS or cost/ledger totals disagree after supported transactions.
- Competing documents can consume the same stock/PR/FIFO resource beyond authoritative availability.
- Same logical request can post twice; uncertain committed response is blindly retried; same RequestId and modified fingerprint is silently accepted.
- Lock ordering can deadlock or checks occur outside locks without authoritative revalidation.
- Closed period, future-date rule, unsupported historical replay, or downstream reversal dependency is bypassed.
- Tests require SQL Server but are only run with SQLite or silently skipped.
- UI-only task changes stock/costing/schema, or agent adds independent stock/cost logic instead of reusing existing patterns.

**Final report must say exactly what was verified, what remains unverified, and why.** No automatic schema migration, cost repair, rollout activation or production data patch.
