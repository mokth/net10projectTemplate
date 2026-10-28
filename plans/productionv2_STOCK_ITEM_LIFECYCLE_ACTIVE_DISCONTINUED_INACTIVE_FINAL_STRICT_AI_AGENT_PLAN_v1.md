---
plan_id: PRODUCTIONV2-STOCK-ITEM-LIFECYCLE-20261028
project: mokth/net10projectTemplate
branch: productionv2
reviewed_commit: d7a744439490d2c63863d96a59dc277c4c3cab4a
reviewed_on: 2026-10-28
status: READY_FOR_PHASED_IMPLEMENTATION_WITH_MANDATORY_GATES
priority: HIGH
implementation_target: GPT-6 High or equivalent capable coding agent
change_type: additive item-lifecycle controls; preserve inventory/sales/procurement/production/costing contracts
review_scope: repository-grounded static inspection; SQL Server deployment and runtime test outcomes NOT yet verified
---

# ProductionV2 — Stock Item Lifecycle (ACTIVE → DISCONTINUED → INACTIVE)
## Strict, Repository-Grounded, Phase-Gated AI Coding Agent Implementation Plan — v1

**Decision:** Replace the misleading two-state item-status experience with three user-visible states. Maintain backwards-compatible `IvStockMaster.Active`/`IsActive`; add `IsDiscontinued`. Use ONE authoritative service to validate lifecycle transitions, and ONE authoritative, item-scoped blocker engine for retirement. Never let a status change corrupt inventory, valuation, history, WIP, demand, or rollback.

**Critical release condition:** This plan is *ready for a capable coding agent to execute in phases*, **not** a claim that code has passed tests. The agent must satisfy each gate with evidence. Do NOT claim 10/10 or production approval on a plan alone.

---

## A. Exact Repository Baseline — What Was Actually Verified

Repository branch `productionv2`, pinned SHA `d7a744439490d2c63863d96a59dc277c4c3cab4a` (2026-10-28). Read these files again at the executing branch HEAD; stop and reconcile if they diverge.

| File / code location | Verified current behavior / risk |
|---|---|
| `ErpWeb.Model/Entities/Inventory/IvStockMaster.cs` | `IsActive` is a `bool`, default true; item identity `ICode`. |
| `ErpWeb.Model/Configurations/Inventory/IvStockMasterConfiguration.cs` | `IsActive` maps to physical SQL column `Active`, and the company/item primary key is `{CompanyCode, ICode}`. Preserve column `Active`. |
| `ErpWeb.Core/Inventory/IvStockMasterService.cs` `SaveAsync` (~183–843) | Validates item code, permissions, structural locks and concurrency. `ApplyEditableFields` (~1610–1647) directly assigns `entity.IsActive = model.IsActive` **without a retirement guard**. |
| Same file `SetActiveAsync` (~845–922) | Bulk `ACTIVATE`/`DEACTIVATE` checks EDIT permission, company-scoped rows, transaction and `RowVersion`, but **does not check on-hand or operational dependencies**. |
| Same file `EvaluateStructuralLockAsync` (~1189–1428) | Existing read-only structural checker covers balances/history, NEW inventory drafts, PR/PO/SO/DO/invoice/DR/WO/production balances. Its rule is **not** the same as item retirement: historical invoice/history may lock structural changes forever but must NOT permanently prevent retirement. Reuse its query patterns, **not its return value**. |
| `ErpWeb.Model/Repositories/Inventory/IvStockMasterRepository.cs` (~277–355) | Normal item lookup and barcode lookup filter `IsActive`. |
| `ErpWeb.Core/Inventory/IvInventoryLookupService.cs` (~255–265) | Paged item lookup requests `IsActive: true`; raw material source balance lookup checks `sm.IsActive && sm.StockControl`. |
| `ErpWeb.Core/Inventory/IvMiscIssueService.cs` (~709–718) | Save-time stock item validation rejects inactive master. |
| `ErpWeb.Core/Inventory/IvGoodsReceiptService.cs` (~845–849) | GR from PO rejects inactive stock item. |
| `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` (~3599, 3820, 3901) | Several posting-line builders reject `!item.IsActive`; never blindly weaken these or reverse/replay safeguards. |
| `ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs` (~313–366) | `LockStockMastersAsync` obtains SQL Server `UPDLOCK, HOLDLOCK` on company/item keys in deterministic order for posting. Lifecycle changes must be serialized compatibly to avoid TOCTOU. |
| `ErpWeb.Core/Sales/SaSoService.cs` (~1848–1895) | Sales order preparation rejects inactive item. |
| `ErpWeb.Core/Sales/SaDoService.cs` (~2393–2414) | DO preparation rejects inactive item but does not distinguish new demand from linked fulfillment. |
| `ErpWeb.Core/Sales/SaInvoiceService.cs` (~2130–2157) | Invoicing preparation rejects inactive item, including linked-document paths. |
| `ErpWeb.Core/Purchase/PoPrService.cs` (~2143–2144) | Stock purchasing-item resolver requires active master. |
| `ErpWeb.Core/Purchase/PoOrderService.cs` (~2236–2247) | `ResolveItemAsync` selects active stock item, otherwise falls back to `PoPurItem`. **Do not let discontinued/inactive stock codes be silently reclassified as an indirect purchase item.** |
| `ErpWeb.Core/Production/ProductionWorkOrderService.cs` (~537–550) | WO snapshot builder checks only `product.IsActive`; production component availability and snapshot refresh must be audited too. |
| `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.Posting.cs` (~205–208) | FG posting rejects inactive destination item. |
| `ErpWeb.Core/Sales/SaDeliveryRequestService.cs` (~1485–1498) | Delivery request source preparation checks active item. |
| `ErpWeb.Core/Inventory/IvStockMasterService.cs` (~924–1114) | Delete uses `CountReferencesBulkAsync`; delete protection is **not** a suitable substitute for retirement. |
| `ErpWeb.Model/Repositories/Inventory/IvStockMasterRepository.cs` (~220–268) | Existing delete-reference method counts only lot, balance, inventory batch-detail, inventory history: do not use it as a complete open-obligation check. |
| `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor` (~326–355) | Item code locked on edit; editable `Active` checkbox, directly bound to form VM. |
| `ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor.cs` (~113–141, 396–445) | Bulk `ACTIVATE`, `DEACTIVATE`, confirmation; list defaults `AppliedIsActive = true`. |
| `ErpWeb.Tests/Inventory/Master/IvStockMasterServiceTests.cs` (~733–820) | Existing activate/deactivate and atomic stale-token tests; must extend, not silently break. |
| `ErpWeb.Tests/Inventory/Master/IvStockMasterPlanTests.cs` (~645–831) | Extensive structural-lock tests: reuse fixture patterns and preserve behavior. |
| `scripts/alter-ivstockmaster-expirycontrol.sql` | Example of idempotent, explicit SQL Server column/constraint script; no app-startup migration. |

**Not verified:** the live SQL Server state, deployed client version, successful build/tests, *all* downstream consumers. The agent must discover those before changing production behavior. This baseline is a source-code inspection, not runtime certification.

**Do not conflate:** Stock Master lifecycle (`IvStockMaster.Active`) with `IvBalLoc.IStatus`/`IvItemStatuses.Active`, the per-stock-slice status. The latter may represent inventory lot/location state and is OUT OF SCOPE.

---

## B. Business Policy — Contract to Implement

### B1. User-visible states (company-scoped, not warehouse-scoped)

| User-visible state | `IvStockMaster.IsActive` (SQL `Active`) | New `IsDiscontinued` | Meaning |
|---|---:|---:|---|
| **ACTIVE** | true | false | Standard new demand and existing obligations allowed. |
| **DISCONTINUED** | true | true | **No new net business demand**; continue, correct or close legitimate obligations established before discontinuation. |
| **INACTIVE** | false | false | Fully retired: not selectable for ordinary new business; existing historical records, inquiries, authorized reversals and reconciliation still supported under existing safety rules. |
| Invalid | false | true | Impossible; reject in application and SQL CHECK. |

`IsDiscontinued` describes *business lifecycle*, **not** quarantine, defective stock, warehouse hold, blocked lot, month-end closure, stock-control flag, or `IsActive` on a different master entity. Do not retrofit generic `Active` logic outside the item lifecycle.

### B2. Allowed transitions

| From | To | Rule |
|---|---|---|
| ACTIVE | DISCONTINUED | EDIT permission; reason; confirmation; audited; immediate future-demand gate. Existing balances and obligations **do not block**. |
| DISCONTINUED | ACTIVE | EDIT permission; reason; revalidate mandatory master setup; audited; concurrency check. |
| DISCONTINUED | INACTIVE | EDIT permission; reason; ALL retirement checks pass **at authoritative server execution time**, transactionally. |
| INACTIVE | ACTIVE | EDIT permission; reason; revalidate essential item setup; audited; concurrency check; no automatic reopening of closed transactions. |
| ACTIVE | INACTIVE | **Reject direct transition**: first discontinue; no one-click skip of outstanding obligations. |
| INACTIVE | DISCONTINUED | Reject direct transition. |
| Same -> same | Same | No lifecycle event; return current status without altering unrelated master properties. |

**Existing data:** `Active=1` becomes ACTIVE; `Active=0` becomes INACTIVE (`IsDiscontinued=0`) without retroactively asserting that retirement checks had passed. Treat legacy inactive records as grandfathered historical states; do not reactivate, rewrite, or delete them. If legacy inactive items have balances/open work, flag them in a read-only exception report for authorized reconciliation; do not silently change statuses.

### B3. Operational matrix — mandatory final authority

**Key distinction:** An item can be discontinued while its obligations remain; never prohibit normal completion of an existing authorized SO/PO/WO solely because it is discontinued. Existing document does **not** automatically authorize adding lines or increasing quantity.

| Operation | ACTIVE | DISCONTINUED | INACTIVE |
|---|---|---|---|
| New manual SO/quotation line or additional unlinked quantity | Allow | **Block** | Block |
| Fulfill existing SO via DO/Invoice up to eligible remaining quantity | Allow | **Allow** | Ordinarily blocked by retirement precondition; controlled remediation only |
| New unrelated direct DO/Invoice that creates fresh demand | Allow | **Block** | Block |
| Existing DO→Invoice of previously dispatched quantity | Allow | Allow | Controlled legacy completion if required, with no new stock movement |
| New PR/PO or increased/new unlinked purchasing quantity | Allow | **Block** | Block |
| GRN against qualifying pre-existing PO, returns to supplier | Allow | Allow | Controlled legacy resolution; no unrestricted new receipt |
| New WO/new product demand/new unrelated component material | Allow | **Block** | Block |
| Issue/consume against released existing WO snapshot, complete output, receive FG | Allow | **Allow** | No new operations; authorized correction paths only |
| Existing production material return | Allow | Allow | Controlled cleanup permitted if still relevant; guard should normally keep item non-inactive until cleared |
| Legitimate stock transfer, stock count correction, damage disposal, material return | Allow | Allow | Limited administrative/reconciliation paths with explicit permission and audit, never general demand |
| Rollback/reverse/replay of already posted document | Existing business/date/costing rules | **Same existing rules** | **Same existing rules**; no blanket `IsActive` prohibition introduced into rollback/replay |
| Historical valuation/stock card/SST/e-Invoice audit | Always | Always | Always |

**Exact workflow classifications MUST be established from real document identities and statuses in repository**. For ambiguous workflows, default to **block creation of new demand**, retain **existing original posting/rollback policy**, and document the unresolved flow for user approval. Do not fake linkage or use a user-provided boolean saying `ExistingDocument=true`.

**Discontinuation effective boundary:** `DiscontinuedAtUtc` (server UTC) is stored per item and used only when necessary to establish demand provenance. Existing-document allowance requires a persisted authoritative source/line/revision established before discontinuation, *plus* no increase to original unfulfilled demand. Do not make all future-dated documents automatically eligible: use persisted creation time/line identity and committed revision evidence; if reliable evidence cannot be established, block and report. Any new/reopened/additional line after discontinuation counts as new demand unless explicitly covered by pre-existing authorized commitment. Backdated business document dates must never bypass the lifecycle rule. **Freeze the allowed committed quantity logically at the transition:** subsequent create/revise/quantity-increase paths must be guarded *in the same transaction* against the actual old persisted line quantity and already-fulfilled amounts. If a module does not retain enough pre-discontinuation revision/line evidence to prove this, introduce a small immutable commitment-baseline evidence mechanism or block new downstream conversion from that source until resolved; **do not infer pre-cutover quantity from current values alone**. Prove all relevant document writers are covered, including imports and document-copy operations.

**Quotation policy:** An outstanding quotation is not automatically a binding SO. Default: do **not** create a new SO by converting an old quotation after discontinuation; either cancel/expire the quote or obtain a separately approved exception policy. An open quotation may appear as an *actionable commercial reference* in the retirement preview, but must **not** freeze an item forever if it can no longer create valid demand. Define the exact terminal/expiry handling after inspecting current quotation rules. This is an explicit conservative default pending G0, not a claim about Malaysia's legal treatment of quotations.

### B4. Retirement (DISCONTINUED -> INACTIVE) blockers

At transaction commit, all *applicable* checks must be clear for **entire company across all branches/warehouses/locations/lots**. Never inspect only current user's warehouse filter. Use consistent `CompanyCode`; apply branch scope only when checking branch-identified documents but aggregate over all branches.

1. `IvBalLoc`: reject ANY non-zero quantity, including negative quantity; do not treat negative as zero. Consider all lot/status slices and other stock repositories representing quantity.
2. Inventory valuation: reject unresolved positive/negative value (e.g. cost layers, active FIFO value, WAC financial residual, standard cost ledger discrepancies) when operationally attributable to the item. **Read authoritative valuation/reconciliation mechanisms**; do not infer financial zero from `IvBalLoc.StdQty == 0`. Histories with zero current balance by themselves are NOT blockers.
3. Inventory NEW/draft/partially executed transactions, allocations/reservations, outstanding receipts/issues/stock count corrections, failed/posting-in-progress operations whose eventual processing requires the item. Check archive/deleted/force-close semantics to avoid false positives.
4. PR/PO still actionable/outstanding on **latest effective revision**, outstanding GRN/invoice/return obligations where relevant; no false positives from fully closed/cancelled/superseded rows.
5. SO/DO/Invoice/quotation/Delivery Request: outstanding fulfillment, dispatch, invoice/return/correction obligations where they require the item; preserve existing revision/`IsCurrent` semantics. Posted/historical invoice alone must not block forever.
6. Production: nonterminal WO as product, nonterminal WO using item as material, allocated/shared WIP, production balances (include negative quantities), pending daily output, IP, FG, production return/adjustment/transfer, unposted transactions, reschedules/unfinished route obligations. Include item as intermediate route output, not just WO header product.
7. Unresolved reservation/soft-reservation and stock ownership/consignment/transfer-in-transit obligations if implemented for the item.
8. Integrity: unknown dependencies, inconsistent quantities, or unavailable valuation authority fail closed with *actionable diagnostic*, never assume clean. Read-only preview may show `INDETERMINATE`; transition must refuse.

**Strict nuance:** some historical records require *no* further item processing. Identify live vs terminal status from real code; do not simply count all historical transaction rows. No user-editable “force inactive anyway” in v1. A data-repair path is separately approved, authorized and audited; cannot circumvent costing/quantity rules.

### B5. Item Code itself

Do not change item-code semantics. Preserve `ICode` required/max 30, company-scoped uniqueness, immutable after first save, and existing `RowVersion` checks. Status transitions must NOT clone items, rewrite their code, alter `StockControl`, change UOM, renumber lots, or move valuation.

---

## C. Architecture & Interfaces

### C1. Additive schema, backward compatibility

**`IvStockMaster`** (`ErpWeb.Model/Entities/Inventory/IvStockMaster.cs`):

```csharp
public bool IsDiscontinued { get; set; } // default false; maps to dbo.IvStockMaster.IsDiscontinued
public DateTime? DiscontinuedAtUtc { get; set; } // optional UTC informational cutover boundary
```

Do NOT add a mutable stored `Status` string AND retain two booleans: duplicated sources of truth invite contradictions. Define computed domain status `IvStockItemLifecycleStatus` with controlled values (`ACTIVE`, `DISCONTINUED`, `INACTIVE`) and a single mapper/validator. Prefer enum/type-safe constants in Core or Model with explicit mappings, **not** user-maintainable `AdLookupValue`; this status controls ERP workflow.

`DiscontinuedAtUtc` must be set on ACTIVE→DISCONTINUED and **cleared on reactivation**; persistent historical discontinuations are recorded in audit table. SQL default zero for `IsDiscontinued`; no backfill of existing `Active`. SQL CHECK: `NOT (Active = 0 AND IsDiscontinued = 1)`. Define UTC and SQL `datetime2` precision consistent with existing config.

**Lifecycle audit** `IvStockMasterLifecycleEvent` (new append-only table; do not repurpose price-audit or inventory movements):

- identity, `CompanyCode`, `ICode`, old/new status codes, `OccurredAtUtc`, `ActorUserId`, required `Reason`, source action (`ITEM_FORM`/`BULK`/`SYSTEM` as controlled code), and a correlation/request ID when available.
- FK only where verified safe; historical audit must survive item deletion constraints. Choose *no physical delete cascade*. If item delete still allowed when unused, retain immutable audit independently.
- Company+item+timestamp index. Never delete history on reactivation. For bulk, write one event per changed item, atomically.
- Do not record sensitive tokens or unrestricted user data. Actor is from authenticated tenant, not client.

**SQL scripts:** `scripts/alter-ivstockmaster-lifecycle.sql` and, if necessary, a separately identified audit table script. Idempotent column creation/defaults/CHECK/index creation. No hardcoded `USE ERPLiteEx` against a multi-tenant deployment. No EF auto-migration at app start. Include preflight, repeat-run, rollback-to-old-binary compatibility, and schema validation. If the old deployed binary does not tolerate the additive column, block release until verified; feature flags are optional only if supported by actual architecture.

### C2. One service-level authority, no UI-only security

Create an item-specific, testable **`IItemLifecyclePolicy`** or equivalent under `ErpWeb.Core/Inventory/`:

```csharp
// Shapes are illustrative; implement using the project's existing DTO/result conventions.
ItemLifecycleStatus GetStatus(IvStockMaster item);
Task<ItemRetirementCheckResult> PreviewRetirementAsync(
    string companyCode, string itemCode, CancellationToken ct);
Task<ItemRetirementCheckResult> EvaluateRetirementInTransactionAsync(
    AppDbContext db, string companyCode, string itemCode, CancellationToken ct);
Task<ItemDemandPolicyResult> AuthorizeDemandAsync(
    AppDbContext db, string companyCode, string itemCode,
    ItemDemandIntent intent, PersistedSourceIdentity? source, CancellationToken ct);
```

- Preview is read-only/UI explanation; final evaluator is **mandatory and repeated** under the status-changing SQL transaction.
- Company from authenticated scope, **not** user form; `companyCode` passed internally only after scoped validation.
- Demand classifier distinguishes `NEW_DEMAND`, `EXISTING_FULFILMENT`, `EXISTING_CORRECTION`, `HISTORICAL_READ`, `ROLLBACK_REPLAY`. No fake flags from browser. Persisted source identity must be retrieved and validated server-side.
- Return structured blocker rows: `SourceModule`, `ReferenceType`, `ReferenceNo`, `Branch`, `Quantity`, `Value` if authoritative, `Reason`, `QueryRoute` (only allowlisted), `Severity`, and `EvaluatedAtUtc`; aggregate counts plus sample IDs; bound the number of examples to avoid huge UI loads.
- Fail closed on query errors for retirement, but preserve old operational posting/rollback rules unless a verified safe, separately tested adjustment is needed.
- Avoid making `IItemLifecyclePolicy` depend on a dozen unrelated mutable services; reuse read-only existing repositories/queries or small evaluator classes, preserve dependency direction.
- No `SELECT *` query over all item movements or entire company; `AnyAsync`/server-side aggregate queries and supporting indexes preferred. Apply company+item filters before joining; investigate query plans for large tenants.

**Status write entry points:**

1. Stock Master `SaveAsync`: when existing item requests a changed lifecycle status, run the **same authoritative transition policy** and audit in the item's transactional save. No direct `entity.IsActive = model.IsActive` bypass. Keep existing gallery, conversion and structural guards unchanged; ensure lifecycle failure occurs before external image writes or safely cleans them up.
2. `SetActiveAsync`: retire or replace in controlled way; existing callers/tests need compatibility. Map legacy `Activate=true` to INACTIVE→ACTIVE (and DISCONTINUED→ACTIVE if user confirms), legacy `Deactivate=false` only to DISCONTINUED→INACTIVE; **never** silently translate ACTIVE→INACTIVE. Prefer new explicit `ChangeLifecycleAsync` and migrate UI to call it. If the method cannot represent reason/target cleanly, reject old actions with a meaningful error rather than silently changing status.
3. Bulk status changes: same policy; single SQL transaction, deterministic order by company/item, optimistic `RowVersion`, all-or-none, no partial audit. Validate `EDIT` and tenant scope on server; do not assume toolbar controls are authorization.
4. On insertion: default ACTIVE and explicitly validate status initial state; users may create a discontinued draft only if business explicitly approves (default **do not**). Do not allow creating an item already INACTIVE to evade master checks unless separately decided.
5. `DeleteAsync` is not lifecycle transition. Leave existing delete operation intact but audit table persistence must not be cascaded away.

**Concurrency/TOCTOU:**

- Use a SQL transaction; acquire item row lock in deterministic order using existing `IvStockPostingRepository.LockStockMastersAsync` semantics (`UPDLOCK, HOLDLOCK`) or an equivalent shared locking helper. Re-check token and blockers **after** acquiring lock.
- Do not let concurrent new SO/PO/WO demand or new inventory balance be inserted after retirement precheck but before commit. Lifecycle-aware creation paths must participate in the same per-item lock protocol or an equivalent proven serializable/key-lock mechanism. Merely re-reading under `READ COMMITTED` is **not enough**. Test race: item retirement vs transaction save/posting.
- Respect lock order already used by posting and stock ledger; no deadlocks due to opposite lock order. For multi-item docs lock item keys in a stable sort; ensure transactions do not hold broader locks while awaiting unrelated UI/HTTP calls.
- Do not introduce unbounded retries or swallow concurrency failures.

### C3. Lifecycle state API / UI model

- Expose derived lifecycle status and optional status caption in `IvStockMasterEditVm` and list row. If `IsActive` remains in VM for compatibility, validate it together with `IsDiscontinued` using a single mapping routine.
- Keep old public interfaces where other modules depend on them; extend carefully; no bulk global rename `IsActive`.
- Server error: `Cannot retire RM001: 2 active Purchase Orders, 1 Work Order, 500 KG inventory. Choose Discontinued instead.` UI can fetch details via preview.
- Require reason (nonblank, max length and encoding validated), recorded in audit, for all actual transitions. No permission bypass via `SaveAsync` or direct API call.

---

## D. Module-by-Module Rules — Do Not Implement a Blanket `!IsActive` Patch

### D1. Selection versus authorization

For **new** document/item search, repository item lookup must generally show only ACTIVE items (`IsActive && !IsDiscontinued`). Existing document edit/view must still display its persisted item with a `[Discontinued]` badge; do not change/erase the bound item when missing from the new-item dropdown. This needs separate `ListSelectableForNewDemand` vs `ResolveExistingBinding` semantics rather than rewriting historical lookup queries.

For valid existing fulfillment, discontinued rows remain allowed and must resolve correctly. **Never use the filtered lookup result as the only server authorization**; direct item code entry/API can bypass UI filtering.

**Performance:** preserve `IvStockMasterRepository.ListActiveForLookupAsync`, `SearchActiveAsync` contract if other callers need historically-active rows. Introduce intent-specific lookup methods/filters or make callers explicit; do not secretly make operational stock lookup exclude discontinued items when stock must be transferred/consumed.

### D2. Sales (highest risk of false blocking)

Audit all entrypoints for `SaQtService`, `SaSoService`, `SaDoService`, `SaInvoiceService`, SO→DR/WO/DO, and sales returns/credit notes. At a minimum, gate **new or increased** sales demand in `SaSoService` and direct/standalone DO/invoices; permit downstream DO/Invoice with **persisted source linkage and quantity ≤ remaining eligible old commitment**. Verify linkage and revision identity from actual entities (`IsCurrent`, `SO`, `DO` line identities, returns, posted status), not just `CreatedDate` on a new document. New line on an existing order, changed item, or greater quantity after cutover = new demand. Closing, cancelling, returning and posting audit/history must not be blocked just because item discontinued.

For direct invoice not linked to a prior SO/DO, reject new demand. For DO linked to existing valid SO, allow within outstanding quantity. Do not turn a previously posted DO into a new stock shipment as an exception; preserve current shipment posting checks.

### D3. Procurement

Audit `PoPrService`, `PoOrderService`, `IvGoodsReceiptService`, PO→GRN, purchase returns, supplier returns, and direct purchasing items (`PoPurItem`). New PR/PO and quantity increases must reject discontinued stock item; authorized receipts against existing eligible pre-discontinuation PO remain allowed; close/cancel/return operations remain possible.

**Specific security trap:** `PoOrderService.ResolveItemAsync` currently falls back to `PoPurItem` when no **active** `IvStockMaster` is found. If an inactive/discontinued stock item with same code exists, it must not be treated as a new indirect purchase item automatically. Determine actual item identity: if stock row exists, enforce stock lifecycle; allow indirect purchasing item only when it is truly a separately classified legitimate purchase item under existing rules. Add collision tests.

### D4. Production / BOM / route / delivery request

Audit WO draft creation, release, change definition/snapshot refresh, manual/auto DR→WO, BOM explosion and components, machine/work-centre scheduling, WO transfers, material issue/return, daily output, FG receipt, production stock adjustment and production WIP. New WO for discontinued product must fail. A discontinued component must not be newly introduced via refreshed BOM or newly created WO. Existing released WO snapshots may still consume/return/finish their committed components up to the authorized quantity; controlled scrap/adjustment allowed.

Production steps must use actual WO/source IDs and snapshot revision, not a client `isOld=true` toggle. No hidden mismatch: finished item may be discontinued while existing WO must still be receivable as FG. Do not permit INACTIVE while any such commitment/balance exists.

### D5. Inventory posting and costing

Keep the entire `IvInventoryPostingService`/stock-ledger replay/costing machinery unchanged by default. Discontinued remains `IsActive=true`, so existing safe postings need no global relaxation. New independent inventory receipts creating fresh purchase demand must be categorized properly; non-demand cleanup (e.g. transfer/disposal/adjustment) can operate while discontinued when authorized.

For INACTIVE: before final transition all operationally relevant balances and live obligations must be cleared. **Do not globally remove** current `!item.IsActive` checks, because it might admit invalid receipts/negative stock. Authorized rollback/replay of historical posted transactions must be checked independently; its behavior is governed by existing period closure, posting chronology, WAC/FIFO/Standard costing, stock history and valuation guards. Never make the new lifecycle status an input to valuation replay math. If existing rollback inherently needs active status, document and provide *narrow safe correction path* without enabling new demand; make it a separate approval gate if deep costing changes required.

No automatic stock write-off, quantity zeroing, creation/rewind of FIFO cost layers, valuation adjustment, tax postings, material consumption or rebooking as part of status transition. Retirement is metadata + audit only.

### D6. Lookups, reporting, analytics

- New demand lookups: ACTIVE only; existing document selectors/lookup for completion: eligible discontinued preserved.
- Stock history, stock card, month-end valuation, inventory reconciliation, sales and purchase history, production WIP/costing and e-Invoice historical views: **all statuses remain queryable**. Never join historical reports on `sm.IsActive` as a mandatory predicate.
- Normal operational lookup that targets existing physical stock may include DISCONTINUED; visually badge it. Warehouse/lot/location active controls remain independent.
- Item Master list: default display ACTIVE + DISCONTINUED, separate INACTIVE filter, `All` option; don't hide the company's open obligations by default. Search and exports must reflect selected status and permission.
- Analytics: optional summary `Discontinued stock balance/value` and `Retirement blockers`. No inaccurate KPI that treats discontinued as zero stock.

---

## E. UI / UX — Simplicity and Explainability First

### E1. Stock Master edit

Replace editable `Active` checkbox in `ErpWeb.UI/Inventory/Masters/IvStockMasterEntry.razor` with one prominent **Status** selector/badge and contextual text:

- `Active` — available for new business.
- `Discontinued` — stops new orders/requirements; existing work continues.
- `Inactive` — fully retired; requires checks and no live commitments.

Do not show ambiguous dual toggles. When changing status, ask for **Reason** and confirmation. Discontinue dialog: prominent warning that new demand stops; show open obligations optionally. Inactivate dialog: run **Preview Retirement**, display category totals + drilldown. Provide **Copy Blockers** / **Open Inquiry** when a known safe route exists; no arbitrary navigation constructed from DB data. A busy indicator while evaluation runs, clear errors, and a retry action. Use the repository's `PageBase`, `MenuAuthorize`/`MenuCodes.InventoryItemMaster` and DevExpress patterns. Use two-way bindings or explicit `TextExpression` where required by `DxTextBox`.

View mode read-only. Disable status editing if no EDIT permission, but server enforcement remains authoritative. Prevent inadvertent dirty form overwrite: if lifecycle action is separate from Save, reload/merge carefully; if Save includes change, use one atomic operation and show result consistently. Preserve image gallery UX and existing item copy, validation and structural-lock notices.

### E2. Grid / bulk

`ErpWeb.UI/Inventory/Masters/IvStockMasterList.razor.cs`: replace old `ACTIVATE/DEACTIVATE` ambiguity with **Discontinue**, **Set Inactive**, **Reactivate**. Show status column, status filter (`Active`, `Discontinued`, `Inactive`, `All`), counts as supported, and correct default `Active + Discontinued`. Bulk preview blocks the **entire batch** when any selected item is ineligible; return structured per-item errors, not only generic message. Preserve current `RowVersion` consistency and transaction atomicity.

When retiring, explicitly say: `2 of 5 selected items cannot be retired. No items changed.` Acknowledge reactivation effects; do not auto-reopen/alter documents. Do not add DELETE or a bypass role.

### E3. Convenient explanatory UI

Optional read-only **Why can't I retire this item?** action next to Status. Categories: Inventory Balance, Valuation, Open Inventory, Procurement, Sales, Production/WIP, Reservations, Data Integrity. Include source modules, code numbers, document links, outstanding qty/value with correct UOM and currency. Do not invent values; show `Not yet calculated` or `Unavailable` when appropriate. Detail query must cap results, page, tenant-filter, and obey inquiry rights so the status preview does not leak restricted commercial details.

---

## F. Mandatory Discovery / Safety Gate G0 (before any coding)

The agent MUST produce `artifacts/item-lifecycle-G0-repository-matrix.md` (or `plans/evidence/` if repository convention differs), with all of these verified **on actual current HEAD**:

1. Git SHA/dirty state, dotnet SDK/project solution, DB migration script convention, SQL table/column types, actual default values, `RowVersion` mapping.
2. Inventory master Save/SetActive/UI/repository consumers; enumerate *all* `IvStockMaster.IsActive`, `IvStockMasters`, `ListActiveForLookupAsync`, `SearchActiveAsync` usages via repository-wide search; mark **new demand**, **existing completion**, **correction**, **history**, **posting**, **rollback**, **costing**.
3. Sales and purchase creation/update/post/rollback/returns, direct-entry paths, links to source docs and delta quantity rules.
4. WO/DR/BOM/snapshot/production issue/WI/FG/return/adjustment/transfer flows; actual terminal status values and item roles.
5. Stock balances across company, production balances and valuation/costing authority and reservations/allocations; exact existing entities, indexes and quantities. Identify missing information, **never guess table or field names**.
6. Concurrency: item locks, transaction boundaries, new-demand creation order, stock posting lock order. Show a concurrency diagram.
7. Existing live inactive items, if DBA permits query: counts with nonzero stock, open obligations, unresolved valuation. No PII/tenant details printed in developer logs.
8. Existing test fixture behavior, especially `IvStockMasterServiceTests.Activate_Deactivate_Works`, `BulkSetActive_OneStaleToken_ChangesNone`, and `IvStockMasterPlanTests`.
9. Present an **impact table of exact services and methods to modify**. If essential source cannot be verified, STOP / mark BLOCKED with concrete gap; no phantom components.

**G0 pass criteria:** policy matrix complete, all write entrypoints classified, SQL schema examined, no unresolved baseline assumptions that can affect stock/costing.

---

## G. Phase-Gated Execution Plan

### Phase 1 — Schema, model, migration and API definitions (G1)

Files likely: `IvStockMaster.cs`, `IvStockMasterConfiguration.cs`, new audit entity+configuration, `AppDbContext.cs`, `scripts/alter-ivstockmaster-lifecycle.sql`, new Core models/interfaces.

Tasks:
1. Add `IsDiscontinued` bit NOT NULL DEFAULT 0 and nullable `DiscontinuedAtUtc`, and valid-state CHECK; legacy rows untouched.
2. Add append-only lifecycle audit table, company/item/time index, no destructive cascade.
3. Version-safe idempotent SQL script: preflight constraints, create-if-missing, re-run without changing current values; schema-level verification queries; no implicit DB alter from app startup.
4. Explicit status mapping functions with invalid state rejected; validation on new/old VMs.
5. Tests for mapping and EF SQL physical columns, legacy data, SQL check in SQL Server tests as possible.

**G1 gate:** migrate representative staging backup twice, row counts/Active values identical, existing Item Code/structural rules intact, schema can be understood by old binary or a safe staged rollout is defined. No application release ahead of schema deployment.

### Phase 2 — Authoritative retirement blockers (G2)

Files likely: `ErpWeb.Core/Inventory/ItemLifecycle/` (new), existing read-only repositories; **no costing writer modifications**.

Tasks:
1. Implement one company-scoped, read-only, item-specific `RetirementDependencyEvaluator` that yields a **deterministic blocker report**; module sub-evaluators organized by domain.
2. Reuse query patterns from `EvaluateStructuralLockAsync` but tailor to *remaining operational obligation*, not any historical footprint.
3. Cover full B4 matrix using real current statuses and revision rules; validate all branches/warehouses/lots; negative quantity/value; production WIP, reserved stock, partial/failed documents and chronology/reconciliation.
4. `PreviewAsync`: safe, efficient, read-only, paged details; `ValidateInTransactionAsync`: same rule set, no stale cached result, fail closed when authority unavailable.
5. Performance: `AnyAsync`, `GroupBy`/SUM correctly rounded with existing quantity utilities, indexes and bounded summaries, no N+1 across tenants.
6. Never mutate inventory/production balance or price/cost ledger while checking eligibility.

**G2 gate:** every module has explicit closed/terminal status test, historical-only case retires successfully, no false inactivity based on zero vs negative balance, and SQL query plan reviewed on representative volumes.

### Phase 3 — Secure lifecycle commands, auditing, concurrency (G3)

Files likely: `IvStockMasterService.cs`, `IIvStockMasterService.cs`, new lifecycle service, DTOs/results.

Tasks:
1. Implement validated transition graph B2 with reason and actor and result types. One status-change policy used by Save and bulk, no bypass.
2. Transactionally lock affected item rows, compare rowversions, evaluate policy and blockers, change status fields, stamp modified fields, append audit, commit. All-or-none bulk.
3. On Active→Discontinued capture server UTC cutover and make it visible to demand guards. On Reactivate clear timestamp but retain audit.
4. Audit role checks, company isolation, invalid status transition and stale token error; ensure retry after conflict reloads fresh status and blocker report.
5. Establish serialized item-key locking with demand-creating code paths, not only in lifecycle service. Integrate shared deterministic locking, assess SQL Server isolation and deadlock behavior.
6. Legacy `SetActiveAsync` cannot bypass policy; keep signature only if semantics explicitly routed/rejected with clear migration path.
7. Save+gallery and Save+UOM/price still atomic per existing design. Never write images before a lifecycle validation error that can be known earlier; preserve existing cleanup.

**G3 gate:** concurrent cross-company, stale-token, two-user bulk, race-with-SO/PO/WO/post scenarios pass; audit exactly once per changed item; prohibited transition changes NO rows.

### Phase 4 — New demand vs existing completion: Sales & Purchase (G4)

Files likely: Sales `SaQtService`, `SaSoService`, `SaDoService`, `SaInvoiceService`, DR services; Purchase `PoPrService`, `PoOrderService`; receipts/returns as required by G0.

Tasks:
1. New selection shows ACTIVE only. Persisted discontinued items on existing lines stay displayed.
2. Server validates all new/additional item quantities, not only UI; direct RPC/API/auto-import, copy/revision/conversion routes all covered. Explicitly test old quotation→new SO after discontinuation: reject absent separately approved binding commitment.
3. Discontinued SO→DO→Invoice allowed when backed by **pre-cutover** linked commitment and no net increase; compare against immutable baseline or verified audited original quantities, not the current editable document alone. Standalone post-cutover DO/Invoice blocked. Existing returns/credit notes preserve prior rules.
4. Existing PO→GRN allowed within qualified outstanding quantity, while new PR/PO or increased quantity blocked.
5. Fix `PoOrderService.ResolveItemAsync` fallthrough trap: stock code with inactive/discontinued master must not silently resolve as indirect `PoPurItem` for a new order.
6. Lock item status authoritatively in the same transaction used to create/increase demand (where technically possible); if the architecture requires separate context, document safe serialization and prove with SQL Server race tests.
7. Preserve numbering, taxes, e-Invoice snapshots, invoice status, allocation, stock posting/costing; no new business rules beyond item lifecycle.

**G4 gate:** exhaustive new/old/linkage/partial qty/backdated creation tests, server bypass tests, no previously valid linked fulfillment rejected solely due to Discontinued.

### Phase 5 — Production, manufacturing, inventory completion (G5)

Files likely: `ProductionWorkOrderService.cs` + draft commands, DR, BOM/snapshot, `ProductionMaterialIssueService.*`, `ProductionOutputService.*`, `ProductionFinishedGoodReceiptService.*`, stock return/adjustment/transfer; inventory lookup paths.

Tasks:
1. New WO with discontinued finished product or newly selected discontinued component rejected; validate draft/release/snapshot refresh/auto WO, not just UI.
2. Existing WO component material issue, operation completion, output, FG receipt, production return/transfer/adjustment allowed per original snapshot and quantities while Discontinued.
3. New BOM revision that adds/increases discontinued material to active WO counts as new demand and must be denied unless existing committed quantity legitimately covers it.
4. Existing stock movement/reconciliation correction allowed under existing stock guards. Never change FIFO, WAC, Standard, WIP valuation, replay, month-end posting chronology or cost layers to implement status.
5. Inactive historical rollback/reversal: explicitly demonstrate existing logic works for relevant paths or log a separate required safety-fix with independent approval (do not wave through a broad `IsActive` bypass).
6. Preserve all lot/expiry/stock-control/warehouse-location restrictions and stock equality/quantity invariants.

**G5 gate:** live WO/FG and PO-GRN/SO-DO-Invoice completion regression passes for Discontinued; no issue/FG/receipt counts or costing balances differ from baseline for identical existing transactions.

### Phase 6 — Stock Master UI, bulk UX and report visibility (G6)

Files likely: `IvStockMasterEntry.razor(.cs)`, `IvStockMasterList.razor(.cs)`, inventory lookup UI, DTO/filter objects, source reports where G0 found active-only history filters.

Tasks:
1. Single Status selector; explanatory helper, confirmation+reason; no editable raw Active flag.
2. Side-effect-free `Preview Retirement` with totals and permission-safe inquiry routes; never use preview as final approval.
3. Bulk status tools Discontinue / Inactivate / Reactivate, all-or-none; row-version checks and clear per-item blockers.
4. New filter default `Active + Discontinued`; switchable Inactive/All; fix list/grid/export consistency; show badges in old document view.
5. Report historical completeness; no filtering out inactive from valuation or e-Invoice/stock history; pay particular attention to joins on `IsActive` in `IvStockCommonRepository` and other inquiries.
6. Mobile responsiveness, keyboard focus, success/error notices, no DevExpress textbox `TextExpression` runtime regression; no arbitrary duplicate status controls.

**G6 gate:** UI and service access tests including non-EDIT direct navigation, stale screen state, deep links, bulk warnings, historical report inclusion, screen-view smoke tests.

### Phase 7 — DB/staging integration, security, performance, deployment (G7)

Tasks:
1. Schema deployed on **throwaway/test backup** before app; repeat migration twice; verify zero existing business-data mutation.
2. Run SQL Server concurrency/integration tests for retirement vs same-item SO/PO/WO/GRN/FG and stock posting. SQLite test success alone **insufficient** because SQLite does not mimic SQL Server `UPDLOCK/HOLDLOCK`.
3. Confirm exact cross-company security (Company A cannot view/mutate Company B) and across-branch scope for retirement.
4. Verify large tenant performance and indexes; no unbounded blocker detail list; cancellation honored.
5. Verify lifecycle status and audit in transaction; rerun after crash/idempotent request; no orphaned audit or partial bulk.
6. Baseline snapshot test of inventory quantity sums, all costing modes FIFO/WAC/Standard financial totals, WIP, stock posting chronology and period closure. Status change itself must create **zero** inventory or accounting movements. Regression reruns on normal existing transactions.
7. Prepare DBA deploy/runbook and rollback: new schema may safely remain while old binary is deployed only if additive compatibility proven. Do not drop data to roll back app. Preserve `Active` historical values.
8. Build `ErpWeb.slnx` (or actual solution found in G0), run targeted tests and full suite; record precise commands and test totals. No claim of passing unless actually run.

**G7 gate:** SQL Server and UI evidence, migration/re-run evidence, no cost/quantity mutation, no unresolved P0/P1 issues, runbook validated.

### Phase 8 — Independent final review / release gate (G8)

Second reviewer verifies every file diff against this policy and latest HEAD; resolves all new `IsActive` usages into intent categories; examines SQL queries, transaction isolation, historical report visibility, UI, auth, rollback and concurrency. Issue a pass/fail table for **design**, **business logic**, **costing/quantity**, **security/tenant**, **performance**, **maintainability**, **tests/deployment**, each with direct evidence. Do **not** award arbitrary 10/10. Release only with complete evidence and ≥9.6/10 justified review score, zero critical/high defects.

---

## H. Exhaustive Test Requirements (agent MUST implement, not just enumerate)

### H1. Lifecycle state/permissions

- Default new item ACTIVE; existing `Active=0` stays INACTIVE after SQL migration; no unexpected reactivation.
- ACTIVE→DISCONTINUED works with reason + audit; ACTIVE→INACTIVE blocked; DISCONTINUED→INACTIVE with no obligations succeeds; DISCONTINUED→ACTIVE and INACTIVE→ACTIVE work with permission/master validity.
- `(IsActive=false, IsDiscontinued=true)` rejected in C#, EF save and SQL CHECK; source forms cannot forge states.
- Change status through both edit Save and bulk, no bypass; stale rowversion rejects all; no partial bulk; tenant isolation; unauthorized role denied.
- Repeated same-state command does not create duplicate audit; audit source, reason, actor and timestamp correct; client-supplied actor/company ignored.
- ACTIVE item with history only may become DISCONTINUED then INACTIVE; any outstanding obligation blocks at INACTIVE step.

### H2. Retirement blockers

- Positive/negative on-hand in other warehouse, branch, location, lot, stock status blocks; zero-qty but financially residual value blocks; nonzero value missing from stock table blocks.
- NEW inventory draft blocks; already POSTED old transaction alone not blocker; open reservations block; future-dated draft blocks if actionable.
- Open PR/PO latest revision blocks; closed/cancelled/fully-ordered or superseded revisions do not false-block. Partial GRN remains blocked.
- Current open SO, DO, pending invoice/delivery request obligations block; closed historical document alone does not. Partially fulfilled order blocks appropriately.
- Nonterminal WO as produced item, component material and intermediate process, WIP (positive or negative), FG not received, material pending/production stock blocked; completed closed work alone not blocker.
- Query unavailable/inconsistent totals -> fail closed with diagnostic, **no mutation**.

### H3. Demand/cutover

- New item in new SO/PR/PO/WO/DR, direct DO/Invoice, fresh BOM component after Discontinued rejected server-side.
- Old SO→DO→Invoice works within pre-existing quantity; extra line/quantity fails; old PO→GRN works; new PO revision increasing quantity fails.
- Historical edit does not erase item; existing snapshots and status/time provenance tracked; backdated date cannot bypass; future-dated document created before discontinuation classified using actual committed provenance.
- Discontinued valid WO continues IP→WI→FG, returns and rollback within original business rules; no new WO/procurement demand.
- Inactive item never reappears as an indirect purchasing item with same code.
- Existing permitted stock transfer/disposal and rollback retain original qty/costing/period guards.

### H4. Concurrency/costing/history/UI

- SQL Server race: deactivate item vs create new SO/PO/WO/stock posting; exactly one valid serialized outcome; no stranded operational obligation on inactive item.
- SQL Server bulk race with one stale token -> all unchanged.
- Three costing modes FIFO/WAC/Standard: deactivation causes **no** difference in quantity or value ledgers; replay/reversal tests stay green.
- Closed-period protection preserved; no data rewritten by lifecycle SQL migration.
- Inactive item in historical stock card, valuation, sales/purchase and production/e-Invoice reports remains visible when filters include it.
- UI status badges, safe confirmation, reason, blocker navigation, filter, export, stale screen, access rights, batch feedback.

### H5. Test suites to extend

- `ErpWeb.Tests/Inventory/Master/IvStockMasterServiceTests.cs`
- `ErpWeb.Tests/Inventory/Master/IvStockMasterPlanTests.cs`
- `ErpWeb.Tests/Inventory/Transaction/IvInventoryPostingServiceTests.cs` and SQL Server concurrency tests
- `ErpWeb.Tests/Inventory/Transaction/IvGoodsReceiptServiceTests.cs`
- `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderServiceTests.cs` and SQL Server concurrency tests
- Existing Sales and Purchase tests discovered in G0
- New dedicated `IvStockMasterLifecyclePolicyTests.cs`, `IvStockMasterLifecycleSqlServerConcurrencyTests.cs`, and audit/migration verification tests

No fake green tests by replacing production validations with mocks that always allow demand. Add realistic integration fixtures using actual service paths.

---

## I. Deployment Safety & Operational Runbook

**Before:**

1. Identify every company database and backup strategy; deploy one test tenant first. Inspect current `Active` values; enumerate legacy inactive stock/open obligations without modifying them.
2. Confirm zero current invalid combinations (new column absent at first deployment); run schema script transactionally/idempotently.
3. No production source-data backfill to Discontinued; default false only; preserve `IsActive`, item codes, quantities, costing history, all accounting/e-Invoice references.
4. Confirm app rollout and schema ordering. Coordinate any long-running old application instances; status changes are blocked until all relevant consumers are upgraded or incompatible routes disabled.

**After:**

1. Verify row counts, original `Active`, balance totals, cost layer totals, WIP totals unchanged.
2. Exercise Active→Discontinued on a known test item with open SO/PO/WO and verify existing completion remains possible.
3. Verify retirement blocker list, negative/zero valuation cases, concurrent save, audit, cross-company isolation.
4. Monitor unauthorized/invalid transition errors and open document fulfillment; no automatic inactive conversion.

**Rollback:** Stop new status transitions, revert application binaries only if compatibility verified, leave additional schema/audit data intact. Never drop columns that contain lifecycle decisions or rewrite history. If critical issue, re-enable affected items by controlled audited transition only after validating operational consequences; no direct blanket SQL `UPDATE Active=1` as a routine workaround.

---

## J. Agent Work Order / Strict Acceptance Contract

**Tell coding agent:**

> Implement `productionv2_STOCK_ITEM_LIFECYCLE_ACTIVE_DISCONTINUED_INACTIVE_FINAL_STRICT_AI_AGENT_PLAN_v1.md` against `mokth/net10projectTemplate`, branch `productionv2`. First complete G0 and report exact files, real contracts and inconsistencies. Then implement one phase at a time. Before each phase, show changed-file list, business behavior, costing/quantity impact, tests and risks; after each phase, run focused tests and record actual results. No skipping blockers, no hard-coded success, no blanket `IsActive` changes to posting/rollback, no silent reclassification as indirect purchase item, no DB changes at startup. If a technical or business ambiguity could change costing, document it and STOP that sub-scope rather than guessing. All gates must pass before a release recommendation.

### Artifacts required at every gate

1. SHA & changed files (including unexpected changes flagged).
2. Exact method signatures / SQL scripts added or modified.
3. Behavior matrix with pass/fail cases; tests actually run including command + totals.
4. Risk: active/new demand; discontinued/existing fulfillment; inactive/history; stock/costing/rollback; security; race; DB deployment.
5. Quantities/values comparison where applicable and explicit statement on any impact.
6. Remaining gap list and next-phase gate decision.

### Hard stop / prohibited shortcuts

- No direct `IsActive=false` on used item via Save, bulk, reflection, admin shortcut or API.
- No direct ACTIVE→INACTIVE without DISCONTINUED.
- No arbitrary force-inactivate/override in v1.
- No client-side-only validation.
- No reuse of structural lock or delete-reference count as the entire retirement policy.
- No treating all POSTED history as an outstanding obligation or all future business dates as proof of old demand.
- No changing stock quantities, UOM, cost layers, WIP, stock posting rules, accounting entries or e-Invoice history to facilitate status changes.
- No SQL `SELECT` omitting company scope or using only current branch for company-wide retirement.
- No fake cross-module promises: if a new demand source is not explicitly protected, feature cannot be declared complete.
- No unchecked “discontinued = IsActive false”; Discontinued must remain operational for prior commitments.
- No `if (item == null) return indirectItem` when an inactive stock item with same code exists.

**Required end-user acceptance:** Users can stop new orders for an obsolete item immediately without destroying or immobilizing existing stock and commitments; once fully cleared, users can retire it safely and still access complete history, valuation and e-Invoice evidence.

**Current plan verdict:** Strong design foundation **conditionally approved for phased coding**, subject to G0 mapping and all tests. **NOT** a claim of completed code or verified production deployment.
