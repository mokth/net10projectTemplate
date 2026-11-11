# Productionv2 — Unified Subcontracting Module
## UX-First, Inventory + Production — AI Coding Agent Implementation Plan v6 — Deep Corrective Review

**Target:** `mokth/net10projectTemplate` · `productionv2` · .NET / EF Core / SQL Server / DevExpress Blazor  
**Coding agent:** **Grok 4.6** for Phases 0–4, **one phase at a time, mandatory STOP between phases**  
**Highest product priority:** Make this intuitive enough that an SME operator wants to use it daily, with minimum training and repetitive entry.  
**Highest technical priority:** No stock/WIP quantity, posting, valuation, FIFO/CostLayer, accounting, or rollback corruption.  
**Current authorization:** **Phase 0 repository investigation ONLY.** All implementation phases require the stated explicit phase-gate approval.  
**Costing phase:** Phase 5 is **excluded**; no hidden cost capitalization, no new costing formulas, no change to existing valuation engine.  
**Reviewed:** 2026-11-09. **Scope:** complete corrected v6, supersedes v5. **Remote branch HEAD checked:** `e24f54069316a346171fed7f14017c09d593cb95` (2026-11-07 merge). **Important:** Grok must repin *CURRENT local HEAD and uncommitted diff* before work. This plan is repository-informed, not proof that the complete live transaction graph was audited.


---

## 0. V6 DEEP REVIEW — NEW SOURCE-VERIFIED FINDINGS AND BLOCKING DECISIONS

This revision preserves v5's *single, UX-first Subcon module*, but corrects assumptions that were too optimistic. Checked the exact `productionv2` branch through branch-specific GitHub file reads. All findings require revalidation against Grok's CURRENT local worktree before implementation.

| Severity | Verified evidence / v5 gap | Mandatory change / result |
|---|---|---|
| **CRITICAL** | `IvStockTransferService.ValidateLineAsync` requires a *different* destination lot number and rejects an already-existing destination lot for lot-controlled items; `IvStockTransferPostingServiceTests` proves split-to-new-lot and same/existing lot rejection. | **Inventory non-lot-controlled same-item** is the candidate pilot. **Inventory lot-controlled subcontracting is BLOCKED** until an end-to-end dispatch + partial return + multiple trips + rollback/lot genealogy design is explicitly proven without changing stock/cost rules. No promise that a normal transfer returns to the old lot. |
| **CRITICAL** | `IIvInventoryPostingService` exposes in-transaction stock-in/out/adjustment hooks but **no public in-transaction stock-transfer hook**. `IvInventoryPostingService.PostInventoryTRAsync` begins/commits its own transaction; `IvStockTransferService.PostAsync` delegates to the independent batch poster. | **Atomic Subcon + Inventory transfer is not proven.** Do not call public Stock Transfer `PostAsync` and then update Subcon rows. Require a separately approved supported same-transaction hook/ownership or **BLOCK Phase 2I**. A 'small adapter' is not automatically safe or authorized. |
| **HIGH** | Inventory `IIvInventoryPostingService.PostAsync` iterates distinct batch numbers and explicitly reports `Posted X of Y` on partial multi-batch success. | A Subcon 20-line document **must map to one verified atomic source batch** where possible; never assume `PostAsync([batch1,batch2,...])` is all-or-none. A combined logical document spanning independent batches is **blocked** unless a reviewed atomic composition exists. |
| **CRITICAL** | `IvWarehouse.WarehouseType` exists but was **not proven** to exclude supplier stock from ordinary Sales, stock issue, stock count, reservation or production material-selection paths. | No external custody warehouse is safe merely because it has a 'SUBCON' type/name. Verify and test *all consuming and availability paths* exclude offsite stock, or prevent posting/rollout. Do not amend those protected paths without approval. |
| **CRITICAL** | `ProductionWorkOrderTransferService` is a full-balance transfer **between Work Orders** and cannot be used for partial supplier WIP custody; `ProductionBalLot` remains authoritative. | **Phase 2P remains BLOCKED by default** until all WIP consumers and an atomic offsite exclusion/return mechanism are proven. No additive read-only custody flag pretending to reserve WIP. |
| **HIGH** | Inventory rollback returns `IvTrxBatch.BatchStatus` to **NEW**, while the v5 Subcon lifecycle described a generic `ROLLED_BACK`. Existing history is generation/revision-sensitive. | Distinguish **source operational state** (`NEW` after rollback) from **Subcon historical reversal event** (`REVERSED`/voided posting generation). Prevent stale/reposted source batch IDs, duplicate statement quantities and orphan links. |
| **HIGH** | `PoOrderCalc.IsServiceIType` recognizes `IType = SERVICE`, and `PoOrderService.IsServiceLine` uses item type to classify PO service lines. | Correct v5's broad 'service PO unproven': **service classification exists**. Still prove actual creation, approval, partial billing, tax/invoice settlement and whether a valid PO service quantity basis is *PCS, HR, JOB or LOT*. Do not conflate stock quantity with service-billing quantity. |
| **HIGH** | `QaReadinessMessages` explicitly reports frozen/record-only QA posting gates. | Required physical QA quarantine/release must **BLOCK** when the selected company lacks a proven enforceable route. A QA case alone does not stop sales/usage. |
| **HIGH** | V5 did not specify how a supplier's **merged non-lot stock balance** is allocated back to individual dispatches, how users distinguish sent vs acknowledged, or which company warehouses a supplier mapping may use. | Make supplier + source-dispatch allocation immutable and revalidated at receipt; no return above per-line outstanding even if total warehouse stock exists; disallow shared/general-use warehouse mappings until proven isolated. |
| **HIGH (UX)** | '≥90% success' with just five operators was imprecise, and the plan postponed useful operator evidence until late phases. | Require a tested, realistic UX prototype in Phase 0/1, early Inventory-only pilot, and explicit 5-person × 5-task measurement before a posting release. Keep guards server-side; measure taps/scroll/click/error recovery. |

**Release distinction:** Phases 2I/2P are development gates, **not** licenses to deploy send-out-only processing. The module stays disabled for real transactions until **the same mode** passes dispatch + partial return + QA handling + exact rollback + valuation + supplier statement. Phase 4 inquiry may be built before posting if useful, but its data must be labeled as read-only/sample until authority is verified.

**Verified source paths (2026-11-09 remote HEAD):** `ErpWeb.Core/Inventory/IvStockTransferService.cs`, `ErpWeb.Core/Inventory/IIvInventoryPostingService.cs`, `ErpWeb.Core/Inventory/IvInventoryPostingService.cs`, `ErpWeb.Tests/Inventory/Transaction/IvStockTransferPostingServiceTests.cs`, `ErpWeb.Core/Purchase/PoOrderCalc.cs`, `ErpWeb.Core/Purchase/PoOrderService.cs`, `ErpWeb.Core/Production/ProductionWorkOrderTransferService.cs`, `ErpWeb.Model/Entities/Inventory/IvWarehouse.cs`, `ErpWeb.Core/Quality/QaReadinessMessages.cs`. These are code observations; **no build or live SQL Server execution was performed in this review**.

---

## 1. Executive decision — ONE module, TWO modes, ONE customer experience

Implement **one Subcontracting Module**, not a separate Inventory Subcon module and Production Subcon module. Its pages, numbering, subcontractor selection, commercial PO linkage, documents, supplier statements, KPI, permissions, help and daily actions are shared. Its *source-specific stock/WIP posting* remains separate and delegates to **existing, authoritative** Inventory and Production mechanisms.

| | Inventory mode | Production mode |
|---|---|---|
| Intended company | Inventory / Procurement customer without Production; **non-lot same-item pilot first** | Manufacturing customer using WO/WIP |
| Operator chooses | Stock item, warehouse, lot, quantity | Released WO / eligible operation / WIP lot, quantity |
| Work Order required | **No** | **Yes** |
| Physical/cost authority | Existing Inventory transfer / `IvBalLoc`, history and stock ledger **ONLY if closed-loop authority is proven** | Existing Production balance/operation posting and WIP facts |
| Common daily task | Send out → Receive back | Send out → Receive back |
| Common inquiry | Supplier custody, outstanding, aging, documents | Same, with WO/operation trace |
| Can change item identity or yield in Phase 0–4? | **NO**; separate conversion/costing design | Only already-supported, independently validated operation semantics; **never invent transformation** |
| Processing fee | PO/AP trace only unless separately approved accounting treatment | PO/AP trace only; **no silent WIP capitalization** |

**A user must never choose a technical posting engine. **If their source mode cannot safely POST, it stays invisible/disabled rather than offering a misleading successful demo.**** In an Inventory-only company, hide Production/WIP concepts entirely. In a company with both modules, display a plain-language source selection on *New Send Out*: `From Inventory` or `From Work Order`. Once a draft has posted references, source mode is immutable. A document header must not mix Inventory and Production source modes; an operator can still see all jobs together in one inquiry.

### Explicit product non-goals for the safe baseline
- No second stock ledger, second WIP ledger, duplicate valuation, or generic new posting engine.
- No automatic RAW-A → FINISHED-B conversion; no consumption of supplied BOM/materials as a proxy for conversion.
- No independent production work order for Inventory-only customers.
- No automatic FG receipt, WO completion, production output, sale, customer invoice, or supplier payment when a Subcon receipt posts.
- No automatic service fee capitalization into inventory or WIP. No rewriting existing PO/QA/Inventory/Production business logic simply to fit the new screen.
- No mandatory one-by-one job creation when a user could begin directly at **Send Out**.

---

## 2. Repository evidence checked (branch-specific; NOT an exhaustive Phase 0 audit)

The following files were actually inspected through the GitHub connector on `productionv2`. The observed facts are a starting map; **Phase 0 must still trace real call chains, posting scope, migrations, tests and local changes**.

| Actual file | Repository finding | Design consequence |
|---|---|---|
| `ErpWeb.Core/Inventory/IIvStockTransferService.cs` | Exposes Save, Update, Post, Rollback of Inventory Stock Transfer | Candidate *Inventory* integration; do not assume safe composition with a Subcon document yet |
| `ErpWeb.Core/Inventory/IvStockTransferService.cs` | Uses `IvTrxTypes.StockTransfer` and delegates Post/Rollback to existing Inventory posting service; validates item/balance/warehouse | Reuse, do **not** write `IvBalLoc` directly; investigate transaction boundaries, lot, dates, reversal, cost method |
| `ErpWeb.UI/Inventory/Transactions/IvStockTransfer.razor` | Compact DevExpress entry and existing transfer workflow | UI and source-lookup reference for Inventory source |
| `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor` | Compact `DxFormLayout`, searchable vendor, leftmost fixed command column (`VisibleIndex="0"`, fixed left) | Primary entry styling, action placement and footer reference |
| `ErpWeb.Model/Entities/Inventory/IvWarehouse.cs` | Company+branch warehouse, `WarehouseType`, `RowVersion` | A supplier custody warehouse is *possible as a design*, but its posting/valuation treatment is **not proven** |
| `ErpWeb.Model/Entities/Production/ProductionBalLot.cs` | Live production `Qty/BaseQty`, cost, WO/operation/location, lot/status, version; comments say balance authoritative | WIP source for Production; **do not create a competing balance** |
| `ErpWeb.Model/Entities/Production/ProductionWorkOrderOperation.cs` | Per-operation execution quantities and route/UOM facts | Verify partial WIP eligibility and operation sequence before every dispatch/receipt |
| `ErpWeb.Core/Production/ProductionProcessHandoff.cs` | Explicit same-route-step handoff and UOM contract | Do not bypass route sequence or silently produce new WIP on external return |
| `ErpWeb.Core/Production/ProductionWorkOrderTransferService.cs` | Specifically **full-balance ownership transfer between compatible WOs**, uses posting coordinator and source WIP guards | **NOT** a ready-made partial external subcon issue API. Must not misuse it |
| `ErpWeb.Core/Production/ProductionWorkOrderTransferService.Rollback.cs` | Uses row version, request ID/fingerprint, stock locks, posting links and movement facts | Reuse its *safety patterns*, not its unrelated transfer business semantics |
| `ErpWeb.Core/StockLedger/StockPostingCoordinator.cs` | Has transaction-aware `BeginInTransactionAsync` / `CompleteInTransactionAsync`, idempotent request evaluation and branch lock | Investigate whether an atomic Subcon + authoritative source transaction is possible without altering protected engine |
| `ErpWeb.Model/Entities/Purchase/PoOrderDetail.cs`, `ErpWeb.Core/Purchase/PoOrderService.cs` | PO quantity/receipt/invoice fields and ordinary purchase line handling exist | Service-line/non-stock PO treatment must be proven, not assumed |
| `ErpWeb.Core/Quality/QaCodes.cs`, `ErpWeb.Core/Quality/QaReadinessMessages.cs` | QA includes Inventory Stock and Production WIP sources, but **some authorization/hold gates are explicitly frozen/record-only** | A QA case is **not** proof that stock/WIP is held or released. Fail closed if policy requires a frozen physical gate |
| `ErpWeb.Core/Menus/MenuCodes.cs`, `ErpWeb.Core/Menus/PermissionCodes.cs` | Named menu permissions including Access/Add/Edit/Post/Rollback/ViewCost | Add matching Subcon permissions, scoped server-side |
| `.agents/skills/erp-ui-core/skill.md`, `.agents/skills/production-transaction-ui/skill.md`, `.agents/skills/productionv2-costing/SKILL.md` | Explicit UI and costing guardrails in repo | Grok must load these plus stock-balance, replay and relevant module references before coding |

**Verified file URLs:** `https://github.com/mokth/net10projectTemplate/tree/productionv2`. Resolve source paths there. No code changes, build or SQL integration tests have been executed for this plan. The above evidence does **not** approve implementation.

### Corrected assumption from v4
The older v4 was mostly **WO-only** and assumed a convenient route from WIP dispatch to receipt. That is insufficient. The new plan starts with **two sources from day one** and requires an independent readiness matrix before implementing either posting path. It also recognizes that the currently inspected QA readiness messages explicitly describe frozen authorization gates.

---

## 3. Business use cases — SME Malaysia, simple first

1. **Inventory-only, same StockCode (non-lot pilot):** send 500 `METAL-A` from Main Warehouse to ABC Painting; receive 300 now, 200 next week. Item remains `METAL-A`; owned inventory is never sold/consumed merely because it left premises. **Lot-controlled transfers are NOT included until the distinct/new destination-lot restriction is solved with approved provenance and costing-safe return tests.**
2. **Inventory-only, different StockCode:** send 500 `RAW-A`; supplier returns 480 `PAINTED-B`, 20 scrap. **Phase 0–4 unsupported by default:** needs controlled inventory conversion, item/yield/value allocation and separate approval. Never invent a Misc Issue + Misc Receipt workaround.
3. **Production subcontracted operation:** released WO requires plating; dispatch eligible, unconsumed WIP; receive full/partial quantities; return only approved, correct operation-eligible quantities to production flow.
4. **Multiple WOs/items to one supplier:** consolidate physical delivery *under one mode per Subcon document*, with immutable source line references and supplier selection; an operator can select 20 lines at once.
5. **Multiple sends/returns:** send 100 + 200, receive 40 + 100 + 160; each receipt maps to remaining dispatch quantities; no over-return.
6. **Additional company-owned raw materials:** optional Phase 2M only, approved separately after source/cost proof; distinguish RM already issued to Production from ordinary inventory and vendor-owned supplies.
7. **QA/exception:** accepted, inspection hold, rejected returned, unprocessed returned, supplier-side loss; all outcomes traceable and mutually exclusive.
8. **Financial:** processing service PO, approval if configured, supplier invoice reconciliation. Do not confuse processing fee with physical receipt or inventory value.
9. **Supplier performance:** outstanding by supplier, delayed returns, expected dates, receipt and exception history, one-page statement, printable dispatch note.
10. **Customer setup:** company using only Inventory sees none of the Production menus or WIP questions; enabling Production later must not migrate or rewrite historical Inventory Subcon records.

---

## 4. UX is the product's FIRST acceptance gate

**Operator design principle:** The normal user asks *Who am I sending this to? What item/job? How much?* For returns they ask *Who returned it? Which outstanding item? How much came back?* Do not show costing method, CostLayer, transaction fingerprint, stock posting IDs or schema status on the main screen.

### 4.1 Navigation — one module, no clutter

- **Subcontracting** (single feature/menu family, location selected from existing navigation conventions after Phase 0)
  - **Workbench / Overview** — home, open jobs, two dominant actions
  - **Send Out** — start here; creates/reuses job behind the scenes
  - **Receive Back** — starts from a supplier or an outstanding dispatch
  - **Inquiry / Supplier Statement** — same list and drill-down with filters
  - **Settings** — admin-only, hidden from daily operators
- Standard Procurement PO and existing QA pages remain accessible by deep link; do not clone them.
- Inventory-only tenants must be able to access Subcontracting based on Subcon permissions **without a Production permission or installed Production workflow**.
- No separate compulsory "New Subcon Order" screen. Persist common Job/Agreement identity behind Send Out only when useful for tracking; the operator should not have to create a redundant document first.

### 4.2 Workbench screen: primary user journey

```text
SUBCONTRACTING                                      [Help ?]
[Open: 18]  [Outstanding: 2,450 PCS*]  [Overdue: 6]

[ Send Out ]                 [ Receive Back ]

Search supplier / item / WO / document ...          [Filter]
-------------------------------------------------------------------
Actions | Date | Supplier | Item / WO | Sent | Returned | Balance | Due | Status
[View]  | ...  | ABC      | ...       | 500  | 300      | 200     | ... | Open
-------------------------------------------------------------------
[View supplier statement]   [Print]   [Export if permitted]
*Show per-UOM quantities; do not sum incompatible UOMs as a single PCS value.
```

**Critical UX correction:** A global outstanding KPI cannot add kilograms and pieces. Display `N outstanding lines`, or break down by UOM. Monetary values must be hidden without ViewCost rights.

### 4.3 Send Out — three user decisions, not twenty input fields

1. **Supplier:** searchable `PoSupplierPicker` / existing vendor lookup with name, code, recent suppliers and active-state checks; supplier may be an individual legally registered through existing master data.
2. **Source:** `Inventory stock` or `Work Order WIP` (choice hidden when only one applicable). Searchable popup with code + description + live availability + warehouse/lot or WO/operation. Multi-select and **Add selected**. Automatically select an eligible sole lot, otherwise require a visible choice; do not guess a lot.
3. **Quantity + Dispatch:** editable quantity grid with compact rows, clear available quantity and UOM, promised return date, optional note. Default quantity may prefill *only if validated current availability*; no hidden full-balance dispatch. Save Draft / Post with one understandable confirmation.

Fields auto-filled: item description, UOM, source warehouse/lot/WIP identity, operation, last selected supplier if still valid, branch, source doc, linked eligible service PO, outstanding qty, document number. A document number is generated through existing numbering, never client-side.

**Avoid:** separate mandatory detail dialogs per line; multiple confirmation layers; repeated vendor selection; manual typing of stock codes and WO IDs; showing 12 unused columns; mandatory remark for normal dispatch; giant page titles/toolbars; unsupported quantity guessing; client-only validation.

**Optional commercial actions:** `Link PO` / `Create PO Draft` inline or through existing PO screen. Reuse approval flow; **never** bypass PO approval merely to reduce clicks. Whether PO is mandatory at dispatch is a configurable, explicitly approved business rule, not a hardcoded assumption. Unapproved PO cannot be treated as authorized commitment. No unsupported draft service item creation.

### 4.4 Receive Back — use the supplier's outstanding list

1. Select supplier or scan/search dispatch number; display **only open, eligible lines**.
2. Check one or many lines; enter quantity received (batch grid). Show original sent, previously received, remaining; default quantity only where it is safe and explicit.
3. Post with clear confirmation and preview. If inspection required, capture the outcome *using actual supported QA authority* or mark as **awaiting inspection / blocked**; do not silently mark all accepted.

For exceptional return, reveal `More details`: accept, hold, rejected physically returned, unprocessed returned and supplier-side unresolved loss. Keep exceptions off the routine screen. Supplier-side loss is **not** a normal receipt quantity and cannot reduce valued balances without a separate approved posting.

### 4.5 UX standards to reuse, not reinvent

- Use existing `DxFormLayout SizeMode.Small`, `DxGrid`, `DxComboBox`/pickers, `DxSpinEdit`, `DxDateEdit`, `DxButton`, `DxPopup`, grid paging, the repo's `CommonDataGridEx` where appropriate; no new UI framework.
- `.razor` for markup, `.razor.cs` for orchestration, `.razor.css` only for page-specific styles; read `.agents/skills/erp-ui-core/skill.md` and Production UI skill. Avoid giant inline `@code` blocks.
- **Leftmost, fixed row actions** (existing Misc Receipt uses `VisibleIndex=0` and `FixedPosition=Left`); no horizontal scroll needed for View/Edit.
- Standard position, spacing, semantics and enabled states for `New`, `Edit`, `Save`, `Cancel`, `Post`, `Rollback` per live canonical page. Post is **not** Save; no stock movement on Save Draft.
- Standard compact header/footer; consistent button colors; field widths proportional to content, supplier field wider than date/qty; horizontal alignment; responsive tablet 768/1024 and desktop 1366 widths.
- Keyboard Tab order supplier → item → qty → next line; Enter behavior must not accidentally Post. In-table quantity edit should be usable without a mouse. Preserve edits during lookup/refresh; prompt before discarding dirty work.
- Row-level inline validation near quantity/item; top-level short summary; retain entered data after a validation failure. One supplier-facing date format `dd/MM/yyyy` in user controls (persist dates under current policy).
- **Plain English**: `You can send only 40 PCS from this lot`, `This dispatch has already been posted`, `Cannot undo: 15 PCS were already received`, `This item is awaiting inspection`.
- Show a helpful empty state: `No outstanding items for ABC Painting` with the next meaningful action. Avoid red technical stack traces.
- Hide `ViewCost` amounts without permission; do not hide physical quantities from authorized operators.
- Tooltips/help use everyday vocabulary: *Send Out*, *Receive Back*, *Still With Supplier*, *Overdue*, *Waiting for Inspection*. Separate Help panel for concepts and first-time usage; no jargon-heavy setting labels.
- Read-only view of posted docs; rollback button disabled with a visible business reason if blocked. Don't expose a button that appears clickable but silently does nothing.


### 4.5a UX anti-rejection requirements (user-friendliness is a release blocker)

- **Preview before coding**: Phase 0 produces a wireframe with actual task data (one item, 20 lines, partial return and error), reviews field sizes and vertical scrolling against Misc Receipt, Stock Transfer and Production UIs; Phase 1 delivers a working non-posting prototype for representative operators. Correct navigation and grid friction **before** posting integration.
- One **compact** screen, no oversized hero/header, unnecessary tabs, mandatory modal per line or multiple nearly identical menus. Keep **Send Out** / **Receive Back** dominant; let users start at their daily task, not from a generic `Subcon Job` creation form.
- Search supplier by name/code, items by code/description, WOs by number/description, **server paging** for 10k+ master data, preserve keyboard focus after adding a grid line. Pre-populate safe values but never select ambiguous lot or auto-post.
- Show helpful stock context in human terms: `Available at Main Store`, `Still with ABC`, `Waiting for inspection`, `Ready for next operation`; keep expert lot/status details expandable. Source validation/future date and period close errors state the needed next step.
- Saved draft remains retrievable and re-editable after mobile/tablet disconnect or validation refusal; **no silent loss, duplicate Post or false success**. Confirm only actual irreversible Post/Rollback, not ordinary navigation.
- Run task scripts with Inventory-only staff **before** enabling Production mode. Test desktop keyboard-only and 768/1024px touch behavior, 44px-ish hit targets when feasible, responsive sticky footer, predictable Back navigation and focus.
- If an exception is unsupported (conversion, non-unique lot, frozen QA), say *exactly what is unsupported* and allow draft/view/print where valid; never invent a successful Post or silently swap an Issue/Receipt shortcut.

### 4.6 Usability targets — must be tested with actual operators

These are **targets to measure**, not claims of current behavior:

| Task | Target and measurement |
|---|---|
| Send one common line with pre-existing supplier/item | 3 main decisions (supplier, source, qty), plus deliberate Post confirmation; **median ≤60 sec** in a prepared, eligible one-line task, excluding login |
| Receive a partial return from one dispatch | Supplier/dispatch → quantity → Post; **median ≤45 sec** in a prepared one-line return task |
| Dispatch 20 source lines | One multi-select and editable grid, no 20 separate New screens; target ≤3 minutes for prepared data |
| Find how many pieces are at supplier | 1 workbench query, no report-building required; allow supplier filter |
| Learnability | At least **5 non-developer operators × 5 realistic tasks = 25 trials**; target ≥23/25 completed without coaching, **5/5** on critical "post correct qty" and "recognize blocked action" scenarios. Record task times, errors, scrolling and training needed |
| Form factor | 768px tablet, 1024px tablet, 1366px desktop and keyboard-only; no hidden primary actions or overflow of key fields |
| Errors | A posted/blocked document produces a plain-language cause + next step, and never loses valid draft rows |

Treat a UX gate failure like a functional failure. Correct UI before signing off a phase, without weakening server-side controls.

---

## 5. One architecture — shared orchestration, separate authoritative posting adapters

### 5.1 Layers (provisional design; Phase 0 must select exact paths)

```text
ErpWeb.UI / Shared Subcon Workbench, Send Out, Receive Back, Inquiry
  → ISubconApplicationService (documents, state, rights, query, UX validation)
     → Shared Subcon transaction/orchestration boundary
         ├─ ISubconInventorySourceAdapter  → EXISTING inventory posting/transfer authority
         └─ ISubconProductionSourceAdapter → EXISTING WO/WIP/production authority
     → Existing PO/Approval service and QA service, only through proven boundaries
     → Existing StockPostingCoordinator / transactional execution where applicable
  → Database: minimum common Subcon document/link/custody evidence
  → Inventory/Production authoritative balances and valued facts (NOT duplicated)
```

**Never implement one `ApplyQtyDelta()` or common `CostingService` for both modes.** A shared source contract can expose `FindEligibleSource`, `ValidateBeforePost`, `Post`, `Rollback`, `GetBalance`, `FindDependencies`; its adapters must delegate to existing appropriate business rules. The shared layer contains *workflow*, not stock and WIP accounting math.

### 5.2 Critical atomicity prerequisite

The current Inventory Stock Transfer interface exposes its own `PostAsync` and Rollback; the WIP/Work Order Transfer service has its own locking/transaction scope. **Do not compose two independently committing public APIs and then mark a Subcon document posted.** If source POST succeeds and the Subcon record fails, the system would contain orphan stock/WIP movements.

**Source-verified obstacle:** `IIvInventoryPostingService` has no public transfer-specific `PostTransferInTransactionAsync`/Rollback equivalent; unlike stock-out/in/adjustment, `PostInventoryTRAsync` is currently a standalone transaction. `IvInventoryPostingService.DispatchAsync` posts multiple selected batch numbers independently and may partially succeed. **Do not represent this as an already-solvable integration.** Phase 0 MUST identify an **actually supported same-DbContext / same transaction** boundary for source posting + Subcon doc status + custody evidence + idempotency. If absent, mark Phase 2I **BLOCKED** and submit a minimal isolated extension proposal with explicit protected-engine risk and user approval request. The `IStockPostingCoordinator` transaction-aware APIs are candidates, **not automatic permission to reuse or alter them**. If a minimal integration hook is required inside protected posting code, write the exact diff proposal, classify its cost/quantity implications, STOP and obtain explicit separate approval. Do not implement a best-effort two-step transaction, shadow ledger, try/catch compensation or outbox as an unreviewed workaround.

### 5.3 Boundaries / source-of-truth ownership

- **Inventory physical ownership:** source transfer/ledger and `IvBalLoc` / movement history, not `SubconCustodyEvent`.
- **Production WIP ownership:** `ProductionBalLot` and authoritative production execution/movements, not a Subcon-created alternative WIP balance. A *reservation/custody commitment* may be additive only if **ALL** original WIP consumers enforce it atomically. Otherwise, Production dispatch must stay disabled.
- **Subcon operational evidence:** dispatch/receipt/supplier attribution, immutable source posting links, physical handover evidence and statuses. This is for supplier accountability, not a parallel valued stock ledger.
- **PO/AP financial truth:** existing PO and invoice services, linked by immutable identifiers; Subcon displays status but never silently books purchase expense or WIP cost.
- **QA truth:** existing QA service only when authorization is actually active; otherwise Subcon holds are operationally visible but may not be used as a substitute for a stock/WIP posting gate.
- **Inquiry:** derive outstanding from posted source facts and valid reversal/disposition events as of business date, not editable document totals.

---

## 6. Proposed minimum data model — repository schema review mandatory

**Names are illustrative, not instructions to create tables immediately.** Inspect existing documents, `AppDbContext`, entity configurations, indexes, rowversion, numbering, menu permissions and `StockPosting` facts before deciding. Prefer fewer tables and existing patterns; no tables for calculations already derivable from authoritative events.

1. `SubconJob` or an equivalent shared relationship: `Uid`, company/branch, supplier ID, `Mode=INVENTORY|PRODUCTION`, committed status/policy, optional PO reference, promised return date, audit, rowversion. **Creation optional for ad-hoc flow**; never require an extra operator visit.
2. `SubconDispatch` header + immutable line source keys: source mode, item/UOM/base conversion, source `IvBalLoc` and warehouse/lot **or** WO/operation/`ProductionBalLot` and route, vendor, supplier warehouse (Inventory only if validated), source transaction IDs, quantity, promised date, full audit/rowversion, document version.
3. `SubconReceipt` header + source dispatch line allocations: physical return qty, accepted/hold/reject/unprocessed buckets, destination location/status, original source identity, authoritative receipt posting ID, QA reference if any, version.
4. `SubconPostingLink` / immutable custody event ONLY where not already representable through existing link/movement history: company/branch, doc, action, request ID + fingerprint, source posting IDs, reversal lineage, user/time/effective date, exact source quantity, UOM/lot/stock identity. Posted changes append reversal evidence; no destructive posted edit.
5. `SubconSupplierLocationMapping` only if necessary: supplier + approved warehouse/location for owned stock held externally, company/branch, active dates; no invented general warehouse master or auto-create warehouse unless Phase 0 proves current design supports it.
6. `SubconPOLineLink` as needed: exact PO revision/line, supplier, service item, agreed qty/UOM, approved commercial status, billed qty; not a shadow PO.
7. For optional 2M, `SubconMaterialLine/Allocation` only if the validated underlying Inventory/Production posting transaction can be linked unambiguously.

**Additional source-reference requirements:** Every Subcon receipt allocation must point to the *exact active dispatch line + authoritative physical movement(s) + source revision/generation + supplier + company/branch + destination supplier custody slice*. Distinct dispatches may merge in a non-lot supplier warehouse, so the operational outstanding per dispatch must be enforced under the same transaction lock; a warehouse total alone is not sufficient to justify a return. Maintain **physical custody vs supplier acknowledgment** separately. For lot-controlled transfers, represent source lot → newly created supplier lot → later return lineage without assuming a globally existing destination lot is allowed. No FK from an Inventory-only row to a required Work Order. Feature configuration and warehouse mapping must be tenant-scoped, immutable after use unless safely migrated, and safe on disabled features with posted obligations.

**Identity and DB requirements:** company + branch on every monetary/physical reference; line-key uniqueness; supplier/source immutability once posted; database unique constraints on `(company, branch, action, request ID)` and source posting link; SQL decimal precision consistent with existing per-domain models; explicit base UOM and conversion; rowversion; FK/tenant constraints wherever possible; logical cancellation/posted reversal; no cascading deletion of posted evidence; history and audit; privacy-safe supplier identity; migration/rollback for existing multi-tenant DBs. Resolve company/branch scope from the authenticated tenant, not caller input.

**Do not add or change schema in Phase 0.** Exact migration names and entity impacts are a Phase 1 approval deliverable.

---

## 7. Inventory-only mode — physical custody without Work Order

**Safe candidate baseline (NOT yet proven):** **non-lot-controlled, stock-controlled, same-item, same-base-UOM inventory**, with a company-owned, supplier-specific custody warehouse/location. Both outward and inward Inventory transfer, supplier exclusion from normal stock consumption, one-transaction Subcon link, reversal and cost preservation must be proven **as a full round trip** in every enabled cost method. **Lot-controlled inventory is separately BLOCKED:** current Stock Transfer validation mandates a different, never-before-used destination lot. It is unsafe to promise return to the original lot via the ordinary service; do not create synthetic lots and silently relabel genealogy.

### 7.1 Dispatch: Main Warehouse → controlled supplier custody location

- Stock is held externally but remains **company-owned**. Do not use ordinary sale, vendor return, scrap, production issue or negative stock adjustment to represent a reversible loan to subcontractor.
- Supplier custody may be an *existing warehouse/location* only if it can be safely represented with current company+branch scope, warehouse type, stock status and valuation mechanics, with vendor mapping. **Mandatory consumer map:** Sales DO and direct Invoice availability/picking, Reservations, Stock Transfer, Misc Issue/Scrap/Vendor Return, Production Material Issue/availability, Stock Count and other current inventory consumers. They must not treat offsite stock as locally pickable/allocatable. Mere `WarehouseType=SUBCON` metadata is NOT a guard. Test each existing path server-side or **BLOCK use of that warehouse**. DO NOT assume a warehouse type label changes accounting or makes a location permissible.
- On POST: validate real available source lot/status/as-of stock under existing Inventory lock and posting boundary; create source decrement + matching external warehouse increment **atomically** using existing transfer semantics; maintain lot and cost-layer genealogy. `Total company-owned quantity before = after` (same base UOM).
- A physical dispatch acknowledgment is distinct from posting and from supplier confirmation; label unacknowledged outgoing quantities `Sent (awaiting acknowledgment)` rather than falsely `Supplier confirmed`.
- Never use transfer to change item code, create a new finished item, or turn an untracked item into stock.

### 7.2 Receipt: controlled supplier custody location → receiving/QA location

- Select remaining source dispatch lines; full/partial supplier return, same StockCode, receiving location verified. **For stock-controlled non-lot items**, lock and reconcile remaining physical stock at the supplier-specific location *and* individual dispatch allocation. **For lot-controlled items**, do not claim normal return can restore the original lot: mandatory new destination-lot rule blocks that assumption pending separate provenance/lot-design review. POST must decrement supplier-owned-by-company stock and increment appropriate receiving/hold stock **exactly once**, with original source link.
- Do not mark inventory as available for sale when actual QA policy requires inspection/hold and the current physical hold/release service is frozen. If physical hold is unproven, **block the affected posting/release** until approved handling exists.
- A return with new StockCode, UOM with unsupported conversion, bundling/unbundling, material consumption, yield loss, or added valuation is **out of safe baseline**. Show a clear reason and a disabled `Conversion required` option; do not silently post ordinary Misc Receipt.

### 7.3 Example—reconciliation

```text
Before dispatch: Main = 500 PCS; Vendor = 0 PCS; Company-owned total = 500 PCS
Post dispatch 500: Main = 0; Vendor = 500; Total = 500
Post return 300: Main/approved receiving = 300; Vendor = 200; Total = 500
Remaining with vendor = 200 PCS; active company-owned quantity remains 500 PCS.
```

If some of the returned 300 is on QA hold, it is still physically in-company but **not available** for subsequent sale or unrestricted transfer where the actual status/hold rules require blocking.

---

## 8. Production mode — outsource WO operation WIP, not inventory FG

- Work Order and operation must be released/eligible under current routing and sequence rules, with posted source WIP. Load and validate `ProductionBalLot` quantity, base UOM, lot/status, production location, route, operation, pending downstream use and active posting generation.
- **Do not call `ProductionWorkOrderTransferService` as a generic Subcon transport:** it explicitly implements *full-balance transfer between compatible Work Orders*, not partial supplier dispatch or external custody.
- Phase 0 must prove a **supported** method to reduce dispatchable WIP or atomically reserve/exclude offsite WIP from every consumer (Daily Production, process handoff, FG, WO transfer, rework and other dependent routes). A read-only supplier statement is not enforcement.
- If a safe *custody reservation* can be implemented without modifying core costing/quantity engines, it must be durable, generation/lot-specific, enforced during existing WIP consumption under the same lock, and completely reversible. If integration requires core changes, **STOP before Phase 2** and ask for explicit approval. No duplicate WIP ledger.
- When physically received back: decide separately **(a)** WIP returned physically, **(b)** QA accepted, **(c)** eligible for downstream operation. These can differ by date; none is automatic creation of new production output. Do not auto-complete WO/route or auto-create FG.
- Require same route/operation/source lot identity unless the repo has an approved transformation/handoff model that demonstrably handles the case. Preserve UOM, pooled cost, genealogy and stage status. Rework preserves cycle lineage and cannot duplicate accepted qty.
- No dispatch of QA hold, rejected/scrapped, unposted or already consumed WIP; no bypass of production operation sequence or material availability checks.

### Production custody illustration (not an approved accounting formula)

```text
Eligible WIP at operation = 500 PCS (authoritative source lot)
Send 200 PCS externally → 200 committed offsite, 300 must remain eligible internally
Return 150 PCS physically → vendor outstanding 50 PCS
If 130 accepted and 20 held → eligible downstream increases only by properly released 130
No new production output, no duplicate WIP receipt or cost creation solely on return.
```

**If the current WIP engine cannot enforce the 300/200 split against every consuming path, this scenario is BLOCKED, not "implemented with warnings."**


### 8.1 Mandatory all-consumer WIP proof

Make an explicit matrix of **every current WIP reader/mutator**: Daily Production, next operation/handoff, material execution and return, FG receipt, work-order transfer, stock adjustment, QA authorization, and any scheduling/readiness/reservation view. For each route record whether offsite custody is physically removed or prevented from consumption *inside the existing root SQL lock*. `ProductionBalLot.Qty` is authoritative; a Subcon-only `OffsiteQty` that those routes do not read is **not stock protection**. If one consumer can still use the dispatched 200 PCS, Phase 2P is **NO-GO**. Also prove pool valuation / cost ownership persists while custody changes; no phantom production output on receipt.

### 8.2 No release on half-implemented mode

**Inventory or Production source must not become tenant-enabled after Send Out alone.** The release proof requires Send Out **and** Receive Back, partial return, QA or approved no-QA path, supplier statement, user permissions, source rollback and downstream dependency checks, plus current valuation reconciliation. A development demo with Send Out may run only in an isolated test environment with clear labels; never in a customer's live database.

---

## 9. Quantity/UOM invariants — auditable, as-of and source-specific

All equations operate in **current repository-authorized base UOM and precision** and are scoped by company, branch, supplier, document source line, lot, status and effective date.

1. `NetSent = PostedDispatch - ValidPostedDispatchReversals` (no draft effect).
2. `PhysicalReturned = AcceptedReturned + HoldReturned + RejectedReturned + UnprocessedReturned + other expressly approved physically returned dispositions`. These buckets are mutually exclusive **at the moment of physical receipt**. Later QA release changes category/eligibility, **not physical quantity**.
3. `SupplierResolvedNotReturned = approved supplier-side write-off/claim/verified loss with proper authoritative quantity and accounting posting` — **NOT** an editable reason or custody-only note.
4. `OutstandingAtSupplier = NetSent - PhysicalReturned - SupplierResolvedNotReturned`, never negative. Transit/unacknowledged shown separately where proof is available; do not silently treat unknown as confirmed vendor receipt.
5. `NetSent <= VerifiedDispatchableSource` at POST inside authoritative transaction/lock, taking account of reservations and physical holds according to existing policy.
6. `ReceivingQty <= CurrentOutstandingForExactSourceLine`, including previous receipts and concurrent receipt attempts.
7. `ReleasedDownstream <= PhysicallyReturnedAndAuthorizedAccepted`, once per source; hold/reject/unprocessed never automatically advance.
8. **Inventory same-item transfer:** sum of valued physical quantity across main/eligible supplier custody slices remains unchanged by dispatch/return; changes must be exact transfer in/out pairs, no negative source or duplicate destination.
9. **Production WIP:** original source pool, offsite custody/reservation, downstream handoff and WIP value must agree; no double cost or issue. Do not equate input and output quantities if actual routing/yield/UOM differs.
10. **Optional company-owned materials:** `SupplierMaterialOutstanding = actual posted issued - posted consumed - posted returned - posted authorized write-off`, scoped by exact material lot/source. No double issue of already-issued WO materials.
11. Cost/layer proof: snapshots of quantity, total value, FIFO remaining layers, WAC/Standard effective cost and WO pool values **before/after/rollback**, with no unexplained deltas. Monetary effects may legitimately arise from an existing transfer mechanism; they must be proven, not silently accepted.
12. **Current multi-batch API is NOT atomic as a group**: `IIvInventoryPostingService.PostAsync(trxType, [batchA, batchB])` can report `Posted 1 of 2`. A multi-line Subcon dispatch should use **one fully validated underlying atomic inventory batch** if supported. Never split one logical Post into multiple separately committed source batches without an approved shared transaction. Do not mark partial header success as fully POSTED; if the design intentionally permits partial subdocuments, expose and audit them explicitly as independently posted documents and do not silently retry them.
13. **Receipt attribution:** `posted dispatch A` and `posted dispatch B` may both have item `METAL-A` at the same supplier warehouse. Physical stock balance alone is not enough; a return must atomically decrement the correct remaining dispatch allocations and supplier custody slice. Reconcile by source line, not just supplier total.
14. **Supplier stock isolation:** external custody stock cannot be selected for ordinary Sales, MI, stock transfers or Production issues unless a specifically authorized return/reclassification posts first. This is a *behavioral acceptance gate*, not a UI hiding rule.
15. **Inventory lot transitions:** follow current legal source/destination lot constraints; source lot ID, destination lot ID, active CostLayer binding, uniqueness and eventual receipt genealogy must reconcile. Until a valid return sequence exists, `LotControl=true` is unsupported.

**Example receipt:** send 500 PCS, physically receive 300 consisting of accepted 280 + held 10 + rejected physically returned 10. Outstanding with supplier = 200, not 190; the 10 rejected units are already back at the factory. If 20 become accepted later, acceptance changes but physical received remains 300.

---

## 10. Transaction lifecycle, exceptions and reversal

### Document status vs job status

| Object | Supported flow | Notes |
|---|---|---|
| Dispatch document | `NEW → POSTED → (authoritative rollback → NEW)`, with historical Subcon reversal record | Current Inventory batch becomes `NEW` on rollback; Subcon historical events remain immutable; repost/revision requires fresh links |
| Receipt document | `NEW → POSTED → (source-specific rollback)`, with immutable reversal event | Reconcile receiving batch state, Subcon links and active source revision; cannot post above remaining |
| Job / supplier balance | `OPEN`, `PARTIALLY_RETURNED`, `AWAITING_INSPECTION`, `COMPLETE`, `CANCELLED/BLOCKED` | **Derived** from immutable posted events, not user-set ledger status |
| Commercial PO | Existing lifecycle, approval and revisions | May be Open/Pending Approval while Subcon draft exists; do not forge PO status |
| QA case | Existing QA lifecycle only if usable | QA approval != physical stock/WIP release unless current authorized service performs it |

**Reverse guards:** supplier receipt already exists; later internal stock issue/transfer/sale/stock-count; production next operation/FG/cost settlement; QA allocation/decision; source lot/active generation changed; closed period; unsupported backdated replay; changed PO/invoice; vendor claim/write-off; active reservations; duplicate request. Block with specific reason, never force a backward delta to make balances fit. For eligible rollback, reverse original matching movement and valuation exactly once; idempotent retry returns existing result, not a new movement. Repost only according to repository revision/generation rules.

For corrections after dependencies, offer a **guided separate correction/reversal process** only if an authoritative existing route supports it. No physical DELETE of posted fact/history. If not supported, state `Cannot reverse this transaction automatically. Contact the authorized administrator.` and keep immutable facts.

**Temporal policy:** draft date, dispatch handover time, receipt effective date, promised date, QA approval date and invoice date are separate; no guessing. Follow existing business date, future-date posting block, month close, stock-count as-of, active generation and replay policy. Revalidate at POST/ROLLBACK under lock, not only on Save/UI.

**Concurrency:** SQL Server integration tests for competing dispatch of last stock, competing receipts, Post vs Rollback, double-click and after-timeout retry, duplicate supplier delivery reference, stale rowversion, cross-tenant ID injection, session cleanup and order of locks; all-or-none under error/cancellation/deadlock/retry.

---

## 11. Procurement, QA, supplier identity and accounting

### Purchase service charges

- Link Subcon job/dispatch to an **existing valid service PO line** with exact supplier/PO revision/line, service qty/rate/UOM (may be `PCS`, `HR`, `JOB`, `LOT`, etc.) and normal approval permissions. **Do not equate PO service quantity to physically dispatched or returned stock quantity without an explicit approved commercial conversion.** **Service recognition exists:** `PoOrderCalc.IsServiceIType` checks `IType == SERVICE`, and `PoOrderService.IsServiceLine` uses that rule. **Still verify** service PO draft creation, approval, partial billing/invoice and posted effects end-to-end; a SERVICE line is not proof of a correct accounting settlement. Don't mistake one-time flag for service type.
- `Create PO Draft` may deep-link prefilled to existing PO entry, never bypass required approval, supplier/tax requirements, purchasing limits, price authorization, invoice matching or revision rules.
- PO service fulfillment/acceptance is **commercially** different from returning company-owned materials/WIP. Do not post service PO to an Inventory GRN that creates a second stock receipt.
- If PO is mandatory at dispatch under approved company policy, block unapproved/missing PO at POST; a convenient shortcut cannot override procurement control. If company allows authorized no-PO subcontracting, document explicit exception policy, permission, reason and subsequent invoice path; never add this exception implicitly.
- Price/tax/SST/e-invoice and an individual vendor follow **existing** supplier/PO/AP rules; do not assume legal or tax treatment.
- Partial billing, rework at supplier expense, missing/rejected units, supplier credit, claims and PO revision/invoice dependencies are distinct from physical receipt; prevent quantity-driven double invoicing, duplicated accrual or duplicate capitalization.

### QA is currently a specific integration risk

- Current branch `QaReadinessMessages.cs` explicitly calls several gates **frozen / record-only**. Phase 0 must report the effective per-company state, actual authorization APIs, and whether stock/WIP hold is **enforceable**, not just recorded.
- If QA is **not required** and business policy authorizes direct acceptance, perform documented normal receiving/availability checks through source posting. If QA **is required** but the physical posting gate is frozen, **block dependent receipt/release or route to supported hold**. Never use a record-only QA case as proof that stock cannot be used.
- A rejection physically returned cannot disappear from valued stock/WIP just by tagging `Rejected`; supply and scrap/write-off must go through an existing approved owner. Supplier-side missing goods likewise need an authorized accounting/stock process before reducing outstanding.

### Costing and go-live policy — decisive

- Phases 0–4 **must not edit** FIFO, CostLayer, WAC, Standard, stock/production valuation, month-end, replay or cost correction code. No unapproved cost formula change, even if it appears to simplify UI.
- **No-costing-code edit ≠ no valuation effect.** Inventory transfer can move active cost layers; production receipt may change downstream effective valuation if existing services are invoked. Record before/after quantities, values, layer IDs, pool costs, period snapshots and no unexplained effect in every test.
- **Service fee capitalization is excluded**. Define approved company/accounting policy for whether processing charges are expensed or must be included in finished/inventory stock cost. If fees must be capitalized and no current approved mechanism exists, **block costing-sensitive FG completion, stock release/sale or other affected downstream completion before production rollout**. A warning banner alone is not an acceptable valuation fix.
- Phase 5 design (separate) may eventually support cost capitalization, inventory conversion/yield, material consumption, supplier write-offs and replay integration. **Never sneak it into Phases 1–4.**

---

## 12. Permissions, companies and audit

- Reuse existing `MenuCodes`, `PermissionCodes`, `MenuAuthorize`, server `CanAsync`, trusted company/branch scope and navigation sync; choose a single Subcon menu parent with permissions for Workbench/Send/Receive/Inquiry as permitted by current pattern. No duplicate Inventory/Production menu feature that forces two maintenance paths.
- Matrix: `ACCESS` view, `ADD` create, `EDIT` amend draft, `DELETE/CANCEL` draft, `POST` physical movement, `ROLLBACK` undo eligible posting, `PRINT` reprint docs, `EXPORT` statements, `VIEW_COST` service amount visibility; any `APPROVE` exception only if actual approval engine is reused. Supplier mapping/settings admin-only.
- **Permission-composition rule:** Decide in Phase 0 whether posting via a Subcon screen requires the original Inventory Transfer POST/ROLLBACK permission as well as Subcon rights; current public Inventory poster enforces its own menu permission. **Do not bypass this with a privileged backend shortcut**. Either require both and present an understandable role template, or obtain a separate explicit security architecture approval before exposing a delegated source permission. Verify this for Production services too.
- Permit Inventory-only operators with inventory source rights but no Production rights; Production source requires both Subcon and relevant WO/source authorization. Do not accidentally expose WIP records through global supplier search. PO creation requires real PO permission; no escalation by the Subcon shortcut.
- All queries and updates company/branch-scoped server-side, including lookup lists, PO references, source lots, document links, and supplier-mapped external warehouses. Never trust client company/branch; no cross-company posting by entering another doc number.
- Audit: who prepared, changed, posted, reversed, approved exception, printed/reprinted, and source references, timestamps/business effective dates, reason, immutable version, original event and reversal event; no destructive history editing.
- Enforce correct blank-company/branch fields from actual tenant policy; do not broadly refactor tenancy or Company/Branch optional rules outside scope.

---

## 13. UI deliverables and testable behavior

### Workbench
- Open jobs / supplier lines, overdue and stock by supplier; KPI no mixed UOM sum; clear `Sent`, `Received`, `Outstanding`, `Waiting for Inspection`.
- Open + recent toggle, remembered safe filters where existing UI supports it; fast global search by supplier name/code, StockCode, WO, dispatch/receipt no.
- Leftmost View/Receive/Print actions, status chips, date/due, fixed action column, no manual SQL/report knowledge. Supplier statement one click.
- `Open Subcon` from WO/Stock inquiry only as optional safe deep link; no change to existing production/inventory source page posting.

### Send Out
- Step-free single-page workflow, type/mode auto-adapts, supplier picker, source multi-selection, quantity grid, optional requested date and notes, auto-generated source doc/PO read-only data, Save Draft, Post, print delivery note.
- One default line style for Inventory and WIP; show only key fields. For Production, show WO/operation in readable text; for Inventory, show warehouse/location/lot and available stock. Advanced lot splits visible only when needed.
- On posting, display exactly `Sent 300 PCS to ABC Painting. 200 PCS remain on this dispatch.` from server-confirmed results, and a print/reprint option; no misleading success on partial failure.

### Receive Back
- Supplier or dispatch quick picker, outstanding-only lines, multi-select batch receipt, inline qty, unusual outcome under expandable Details; QA policy and POST state clearly shown.
- Show `Originally sent / Already returned / Still out / Receiving now`, UOM, vendor, item/WO, lot; prohibit over-return at client and server, including concurrent updates.
- Receipt confirmation is deliberately distinct from inspection acceptance; if QA required but unavailable show actionable block, not silent accepted default.

### Inquiry/statement
- Filter supplier/source mode/item/WO/operation/lot/PO/status/date/due, print reprint, CSV/Excel if current report pattern supports it, as-of supplier statement and movement drill-down.
- Supplier outstanding by *source line and base UOM*, not sum of unrelated items or mismatched UOM; show zero/late/partial and backdated reversal history correctly.
- KPI definitions documented: count open source lines, aged qty by base UOM, due-date on-time by fully closed source line, QA reject physically returned ratio, PO committed vs invoiced (no claim capitalized), no mixed denominators.

### Optional settings only if proved necessary
- Enable Subcon, supported source modes by tenant, approved supplier custody warehouse mapping, PO-required timing, QA requirement and approved accounting policy; simple labels and help, safe defaults, disable unsupported features. Never expose a raw `Enable Costing Replay` switch.

---

## 14. Implementation phases — Grok 4.6, ONE phase per explicit approval

### Phase 0 — Repository forensic audit + runnable design evidence (**EXECUTE NOW; READ-ONLY**)

**No code changes. No migrations. No feature flag enablement.**

1. Pin actual local HEAD SHA, branch, worktree, uncommitted diff and DB schema/migration versions. Do not overwrite user changes or assume GitHub remote equals local worktree.
2. Trace end-to-end *both* paths:
   - Inventory UI → `IIvStockTransferService` → `IvInventoryPostingService` / StockPostingCoordinator → `IvTrxBatch/History`, `IvBalLoc`, active layers/valuation → later sale/transfer/count/rollback.
   - Production WO/route → `ProductionBalLot`, operation and process handoff → output/transfer/FG consumers → lock/posting link/cost pool → rollback and downstream dependency gates.
3. Inspect all WIP consuming services, stock statuses, exact source UOM, reserved/available logic, lot lineage, in-transit/vendor warehouse constraints, permission and menu registration.
4. Verify PO service item / invoice/approval behavior, including service receipt without physical GRN, supplier individuals, tax and PO revision/matching; inventory-only customer cannot require Production.
5. Verify QA per-company policy vs **currently frozen record-only gates** and any usable physical hold/posting authority.
6. Map existing UI live pages and skills: `.agents/skills/erp-ui-core/skill.md`, `.agents/skills/production-transaction-ui/skill.md`, `.agents/skills/productionv2-costing/SKILL.md`, `references/stock-balance.md`, `references/inventory.md`, `references/production.md`, `references/procurement.md`, `references/replay-rollback.md`, `references/testing.md`.
7. Produce an exact source/method/call-site inventory, atomic posting design, DB diff proposal, current active cost methods, blocked/allowed scenario matrix and report/query integration plan.
8. Design test fixtures for Inventory and Production, SQL Server concurrency, rollback and period guard; include before/after valued ledger snapshots, and a UX wireframe consistent with actual UI.
9. Walk through representative paper cases: Inventory same-item 500→300+200, Production WIP 500→dispatch200→return150, partial QA held, competing POST, rollback after dependent use, cost incompleteness.
10. Present an explicit GO/NO-GO **per adapter**. `Inventory readiness` and `Production readiness` are independent. If one is blocked, the module may implement only the proven mode, but **the shared architecture/UI must support both without a second module**. Do not present blocked workflows as enabled.

**Phase 0 deliverables:**
- `plans/subcon/phase0-current-repo-evidence.md` — verified current file/method/line/test references, commit, worktree diff, source-to-valuation graphs.
- `plans/subcon/phase0-source-readiness-matrix.md` — two modes, PO, QA, material 2M, conversion, open-period/replay, each PROVEN/NOT PROVEN/BLOCKED with exact cause.
- `plans/subcon/phase0-ux-wireframes-and-uat.md` — desktop/tablet wireframes, 5-task UAT script and existing-page component mapping.
- `plans/subcon/phase1-exact-file-implementation.md` — exact files, proposed schema diff/indexes, public interfaces, transaction orchestration, test commands, rollback and migration plan; **NO implementation**.

**STOP.** Await review and explicit Phase 1 authorization. Do not self-certify 10/10.

**V6 non-negotiable Phase 0 proof tables** (include in the four evidence files):

1. **Inventory transfer API feasibility:** show `IIvInventoryPostingService` signature list, `PostInventoryTRAsync` transaction boundary and `DispatchAsync` multi-batch semantics. Give an exact proposed legal atomic seam **or a BLOCKED conclusion**. No speculative interface names as proof.
2. **Lot round-trip:** test source lot → supplier destination lot → physically returned quantity → receiving lot, including same/existing-lot rejection, multi-send/multi-return, reverse, FIFO/WAC/Standard. If no legal path, mark `LotControl=true` **BLOCKED** (do not modify inventory transfer rules to pass).
3. **External stock consumer matrix:** enumerate every ordinary stock and reservation reader and test that an offsite warehouse/lot cannot be committed by Sales, Production, MI, transfer, etc.; if any can, block stock-custody mode.
4. **WIP all-consumer matrix:** check partial committed WIP cannot also be consumed, output, transferred or FG-received by any current code path; no unproven `custodyQty` workaround.
5. **PO service semantics:** prove SERVICE classification and exact lines from `PoOrderService`, PO approval, AP/Invoice matching and service UOM vs physical quantity. Item stock-control and purchase type are distinct.
6. **QA frozen vs operational matrix** per company enabled configuration, and precise fail-closed dispositions.
7. **Authority idempotence and source lifecycle:** map inventory underlying `POSTED → NEW` on rollback, revision generation/CostLayer link and what happens after repost; map per-line receipt reversal and source rollback dependencies.
8. **Operator prototype/UAT:** mock 1 and 20 rows, tablet/desktop input, ordinary statuses and blocked states. List measurable errors and which existing shared components will be used.
9. **Deployability:** recommend first closed-loop *non-lot inventory* pilot or *none* if authority is missing; never give an overall module GO based solely on UI shell. Explain exact technical changes requiring user approval.

### Phase 1 — Shared shell, configuration and safe PO links (AFTER approval)

- Add single Subcon navigation/permissions; one Workbench page, shared source-mode selector and supplier picker. Responsive list and safe **read-only** empty states.
- Add minimum common entities and migrations **only as proven in Phase 0**; enforce company/branch, unique source/ref and version indexes, audit, enable flag default OFF.
- Link to existing supplier and valid service PO lines; optional `Create PO Draft` deep link, never bypass approval. No stock/WIP posting.
- Implement demo-mode/feature-off explanation and mode-specific UI (Inventory-only hides WO; Production shows WO lookup only when permitted). Do not make user create duplicate jobs.
- UX evaluation of supplier/mode/source lookup, navigation, statuses, tablet responsiveness and 20-line grid shell.

**Exit:** no physical/valuation impact, no new disconnected pages, user permissions correct, schema migration up/down and disabled-mode behavior tested, ≥90% successful read-only workflow tasks. Report exact changed files/tests and STOP.

### Phase 2I — Inventory same-item Send Out + Rollback (AFTER separate approval)

- Implement Inventory source picker, supplier-specific custody mapping, multi-line draft, real Post and rollback **only if Phase 0 has proven/approved an actual same-transaction transfer posting owner**. The existing stock-transfer public API is **not** that owner; Phase 2I is BLOCKED unless a legal transaction seam is found. Never edit protected inventory posting to create that seam without approval.
- **Pilot candidate:** stock-controlled, non-lot-controlled, same item/UOM, one supplier-mapped external warehouse/location excluded from all ordinary consumers. **Block all lot-controlled transfers** until a complete unique-destination-lot and return lineage proves safe; also block conversion, supplier scrapping, unsupported status/cost method and cross-branch if the source rules forbid it.
- Same transaction must commit Inventory movement and Subcon document/evidence or none; source availability rechecked under lock. No independent `IvBalLoc` writes.
- Return readiness/receipt will be Phase 3; Phase 2 must still provide safe dispatch **rollback**. Never go live with un-reversible dispatch.
- Test FIFO, WAC, Standard methods used by tenants; stock 0/exact/short, non-lot custody round trip, **lot-control rejection**, backdates, closed periods, concurrent last stock, duplicate post, rollback, sales/production misuse of offsite stock, exact per-line reconciliation and cost-layer preservation.

**Exit:** zero unexplained stock/cost variance; exact matching transfer/reversal, idempotence, permissions and UX batch results; STOP.

### Phase 2P — Production WIP Send Out + Rollback (AFTER separate approval)

- Implement only if current authoritative WIP allows the lot/operation to be made unavailable while externally held, **and** all downstream production consumers enforce it atomically.
- No abuse of the full-balance WO Transfer service. No parallel WIP ledger, auto output or FG; preserve WIP cost and genealogical source.
- Re-validate WO status, route sequence, WIP source/quantity/status, period and cost readiness at posting; dispatch partial/multi-WO lines only when proven supported; rollback gates same phase.
- Test concurrent WIP consumption and dispatch, output/FG dependencies, retry, rollback after later operation, open/closed periods and cost pool invariants.

**Exit:** exact source/remaining/external WIP proof, all source consumer guards confirmed, no valuation delta unexplained; STOP. **If no safe integration without protected engine changes, mark Phase 2P BLOCKED and do not implement a fake custody balance.**

### Phase 2M — Optional company materials dispatch (SEPARATE approval; disabled by default)

- Requires explicit correct ownership classification, stock transfer vs already-issued production material, material lot/UOM/cost, consumption/return/scrap and credit/write-off paths; safe atomic source posting and reversal for each.
- This is a **gated extension**, not required to approve the shared module baseline; keep UI hidden until tested and signed off. No invented material subledger or double production issue.

### Phase 3 — Receive Back, partial receipt, QA and rollback (AFTER per-mode gate)

- Use the shared receipt screen. One physically returned quantity, complete remaining dispatch allocation, multiple receipts and optional dispositions, respecting current QA authorization status.
- Inventory source: return from supplier-specific custody warehouse to correct receiving/hold warehouse with an **atomic exact original-dispatch allocation**. Candidate **non-lot same-item** first; lot-controlled return remains disabled until source/destination new-lot constraint and genealogy are independently approved. Protect against return of another dispatch's fungible stock.
- Production source: restore physical custody and advance eligibility **only** under proven existing WIP/route rules, without new output, WO completion or cost creation; QA hold/release as separate authorized event.
- Receipt POST and Rollback within same authoritative transaction, with source event links, replay, downstream, stock-count and invoice/QA dependencies. Preserve immutable history.
- Unsupported customer configuration (e.g., mandatory QA but frozen gate; capitalization required before sale/FG) must be clearly BLOCKED rather than bypassed.

**Exit:** partial return reconciliation, qty/valuation/cost-method proof, no double production output, QA policy tests, all dependency/rollback guards and usability pass. STOP.

### Phase 4 — Inquiry, statements, KPIs, printing + operator UAT (AFTER approval)

- Shared inquiry for both modes, supplier statement, outstanding/aging, print/reprint and permitted export, WO/PO/stock drill-down. Read **immutable** source posting/custody events with effective as-of dates and valid reversed history.
- Measure on-time rate, supplier overdue/open, physical/QA exceptions, committed PO value vs invoice value; no unsupported valuation or mixed-UOM KPI claims.
- Pilot with at least five representative operators, Inventory-only and Production users, in desktop/tablet, 1/20-line send, partial return, filter, printer, blocked rollback; fix UX regressions without changing business posting. Permission and security pass.
- Feature flags default OFF, enable only **closed-loop proven source modes** per company (dispatch + return + partials + rollback + QA + no-pick offsite stock + valuation + accounting treatment), documented support runbook and month-end/go-live accounting approval. Supplier outstanding and UI labels must not claim vendor acceptance when shipment is only posted/sent.

**Exit:** independent UAT and automated regression evidence, support-friendly setup, clean reconciliation and feature rollout checklist. STOP for final release approval.

### Phase 5 — NOT part of Grok 4.6 assignment

Separate future plan and approval for inventory item conversion/yield, processing fee capitalization into inventory/WIP, advanced materials consumption, supplier scrapping/write-offs and historical replay/cost revaluation. **Do not build placeholders that falsely claim these financial operations are complete.**

---

## 15. Testing matrix / regression / adverse scenarios

| Scenario | Inventory | Production | Required assertion |
|---|---|---|---|
| Draft New/Edit/Delete / future draft | ✓ | ✓ | No stock/WIP/valuation impact; Save/Cancel intuitive |
| Single item send, exact available | ✓ | ✓ | Authoritative available decreases appropriately; physical total preserved where transfer |
| 20-line batch including 1 invalid | ✓ | ✓ | All-or-none POST, editable grid retains valid data |
| Partial send and partial return, 3 receipts | ✓ | ✓ | Source dispatch line remaining and supplier statement exact |
| Multi-lot / UOM precision | ✓ | ✓ | No lot mismatch, sign reversal, rounding leakage |
| **Stock Transfer destination lot restriction** | ✓ | N/A | Same/existing lot rejected as current code; proposed round trip tested or lot-controlled mode BLOCKED |
| **One Subcon doc mapped to multiple inventory batches** | ✓ | N/A | Public multi-batch partial success detected; no false all-or-none or orphan source post |
| **Stock left at supplier appears in Sales/MI/Production pickers** | ✓ | N/A | Server blocks offsite stock use by every relevant consumer, not just UI filter |
| **Two dispatches same non-lot item, one supplier warehouse** | ✓ | N/A | Receipt allocated to exact dispatch line; other line untouched; no double return |
| **Rollback then repost underlying source** | ✓ | ✓ | Source NEW/revision/generation and Subcon history/link do not duplicate, leak or drift |
| **SERVICE PO charges in HR/JOB vs physical PCS** | ✓ | ✓ | Service billing independent of physical qty; no forced false 1:1 matching |
| **Enabling Send Out without Receive Back** | ✓ | ✓ | Live tenant remains OFF; no stranded custody during incomplete release |
| Same item vs different item | ✓ | N/A | Different-item conversion visibly blocked |
| Source already reserved, QA held, consumed | ✓ | ✓ | Server refuses; no partial posting |
| Inventory FIFO, WAC, Standard before/after | ✓ | Verify applicable | Same active layer/financial generation coherence, no unexplained monetary change |
| Source/return cannot post in closed period | ✓ | ✓ | Fail before mutation; rollback as-of checks too |
| Future-dated posting / backdated replay | ✓ | ✓ | Existing business-date/stock-as-of rules preserved |
| Two users send last 100 simultaneously | ✓ | ✓ | Only valid eligible quantity succeeds; never negative |
| Two returns for same remaining 50 | ✓ | ✓ | Cannot over-return; idempotent retries safe |
| Double click Post/timeout/retry | ✓ | ✓ | Exactly one source movement and Subcon event |
| Post followed by permitted Rollback | ✓ | ✓ | Exact original authority reversal, qty/value/lot match |
| Rollback after subsequent sale/next operation/FG/invoice | ✓ | ✓ | Correct dependency refusal; no partial mutation |
| QA record-only / required QA | ✓ | ✓ | Cannot claim physical hold/release without supported authorization |
| PO pending approval, service invoice, supplier changed | ✓ | ✓ | Approval, commercial matching and supplier identity not bypassed |
| Inventory-only tenant / no Production rights | ✓ | N/A | Full Inventory Subcon UX without WO lookup/permission error |
| Cross-tenant or cross-branch source ID | ✓ | ✓ | Access denied, no information leak |
| Mobile/tablet compact + keyboard grid | ✓ | ✓ | No hidden primary buttons, clear messages, efficient multi-line entry |
| Backdated events and as-of supplier statement | ✓ | ✓ | Same authoritative totals as posting source at cutoff |

**Run** actual `dotnet build`, focused .NET tests, SQL Server integration/concurrency tests, cross-module Inventory/Procurement/Production/QA tests, full regression where available. Grok must report exact commands, pass/fail/skip counts, SQL Server version/test DB, commit/diff and untested cases. SQLite/mocks cannot substitute for SQL transaction/lock validation.

---

## 16. Files / components likely affected — exact diff deferred to Phase 0

**Existing integration references (inspect, reuse; do not automatically edit):**
- `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor[.cs/.css]`, `IvStockTransfer.razor[.cs/.css]`, Inventory balance/source picker.
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor[.cs/.css]`, daily output / FG/Material Issue pages, Production stock/WO lookup.
- `ErpWeb.UI/Purchase/Transactions/PoOrder.razor[.cs/.css]`, `ErpWeb.UI/Purchase/Lookups/PoSupplierPicker.razor[.cs]`.
- `ErpWeb.UI/Quality/QaWorkbench.razor`, `QaCaseEntry.razor`; current QA readiness services.
- `ErpWeb.Core/Inventory/IIvStockTransferService.cs`, `IvStockTransferService.cs`, `IIvInventoryPostingService.cs`, `IvInventoryPostingService*.cs`.
- `ErpWeb.Core/Production/ProductionProcessHandoff.cs`, `ProductionWorkOrderTransferService*.cs`, `ProductionOutputService*.cs`, WIP operation/transfer dependencies.
- `ErpWeb.Core/StockLedger/StockPostingCoordinator.cs`, `StockPostingFingerprint.cs`, period/locking/replay/valuation helpers.
- `ErpWeb.Core/Purchase/PoOrderService.cs`, `IPoOrderService.cs`, existing PO approval and invoice services.
- `ErpWeb.Model/Entities/Production/ProductionBalLot.cs`, `ProductionWorkOrderOperation.cs`, Inventory `IvBalLoc` and `IvWarehouse`, Purchase `PoOrderDetail`.
- `ErpWeb.Core/Menus/MenuCodes.cs`, `PermissionCodes.cs`, navigation/menu permissions, core DI registration, `AppDbContext`, EF configurations/migrations, test fixtures.

**New files (proposed only):** one Subcon UI component family (`Workbench`, `SendOut`, `ReceiveBack`, `Inquiry`) with `.razor/.razor.cs/.razor.css`; common Subcon services/DTOs and minimal entities/configuration; source-specific adapters and tests, exact namespace/path following observed project conventions. No second Inventory and Production Subcon module directories with duplicated pages.

**Protected / deny-by-default code:** `ErpWeb.Core/StockLedger/Costing/**`, `StockReplay*`, `IvInventoryPostingService*.cs`, inventory valuation and `CostLayer` implementations, `ErpWeb.Core/Production/*Cost*`, Production output/FG posting/rollback/correction and replay, period-close engines, production WIP movement valuation, existing inventory posting or balance mutation rules. Any planned touch needs a separate design review and user authorization **before** execution; do not let Grok treat a small diff as automatic permission.

**Database:** no modified legacy stock/WIP/PO/QA table to bypass existing logic; minimum additive, company-scoped Subcon tables only after Phase 0 proof. Existing tenant migrations and schema compatibility must be checked with data migration and downgrade plan.

---

## 17. Strict acceptance / deployment gates

A phase may be approved only with:

1. Current actual source commit + working diff, exact changed files and verified integration methods.
2. Behavior before vs after, **both** source modes / known disabled scenarios and no regression of Inventory/Production/PO/QA.
3. Quantity/WIP **AND** active cost/layer/value snapshots per source lot/slice before/after POST and reversal; zero unexplained variance.
4. Transaction proof: one authoritative posting owner; atomic Subcon-to-source posting; same company+branch scope; SQL Server concurrency, retry, period, future date, missing authority and downstream rollback outcomes.
5. All UI patterns matched to the existing best page, tested with operator task scripts on tablet and browser; no unnecessary clicks, code memorization or misaligned forms.
6. Permissions, audit, PO approval, QA-ready flags and disabled features verified and documented.
7. Exact build/test commands, results, smoke/migration, rollout OFF by default and safe disable with outstanding posted obligations.
8. Accounting decision on outsourced processing charges signed off before enabling any scenario where their omission makes inventory/FG value materially incomplete. No silent release by warning.
9. Every unproven adapter remains **disabled**; operators see a clear explanation, not a broken Post button.
10. Independent review for each phase, particularly 2I/2P/3, and explicit approval to proceed. **No automatic Phase 0→1→2→3→4 progression.**

### Mandatory STOP examples

- No safe Inventory supplier-custody transfer preserving cost layers, or no matching return/rollback.
- No safe all-consumer WIP offsite exclusion or unproven partial WIP posting.
- One public service commits source movement before Subcon status and no supported atomic transaction composition.
- QA required but physical hold/authorization is currently record-only/frozen; supplier missing material needs unapproved write-off.
- No approved PO service billing path or PO approval bypass unavoidable.
- Existing FIFO/WAC/Standard, WIP pool cost or active generation differs unexpectedly after Post/Rollback.
- User's local worktree contains important changes not represented by remote HEAD; plan assumptions stale.

---

## 18. Grok 4.6 execution instructions — paste-ready

> You are the **Grok 4.6 coding agent** implementing the **Unified Subcontracting Module** in the current local `productionv2` repository. Read `productionv2_UNIFIED_SUBCON_UX_FIRST_GROK_4_6_PLAN_v6_DEEP_REVIEW.md` and the repo's `.agents/skills/erp-ui-core/skill.md`, `.agents/skills/production-transaction-ui/skill.md`, `.agents/skills/productionv2-costing/SKILL.md` plus mandatory inventory/production/stock-balance/replay references.  
> **Execute PHASE 0 ONLY now.** Pin current local HEAD + diff. Audit actual Inventory Stock Transfer and authoritative WIP/operation/FG posting and rollback, PO services/approval, QA frozen or active gates, permissions, DB/migrations and SQL Server tests. Trace UI→service→DB→posting→quantity/cost→downstream→reversal for BOTH source modes. Propose a shared UX with **two separate posting adapters**; mark each adapter PROVEN or BLOCKED based on real transaction evidence; Inventory-only users must not require Work Orders.  
> Produce all four Phase 0 evidence files and a revised exact-file Phase 1 plan, with code locations, test strategy, source readiness and UX wireframes. **DO NOT write code or migrations or enable features. STOP for explicit approval.**  
> In future *separately authorized* phases, deliver one excellent Subcontracting Workbench, Send Out and Receive Back screens, minimal clicks and searchable batch entry. Reuse existing UI/post/rollback patterns. Do **not** change FIFO/CostLayer/WAC/Standard, Inventory or WIP valuation, stock replay, period close or production cost math. If safe integration requires protected changes or QA/PO accounting is unsupported, **STOP and report exact dependency; do not improvise.** No Phase 5.

### Grok per-phase response template

```text
PHASE: 0 / 1 / 2I / 2P / 2M / 3 / 4
LOCAL BRANCH / HEAD / WORKTREE DIFF:
ACTUAL FILES INSPECTED AND METHODS:
FEATURES COMPLETED / BLOCKED:
INVENTORY SOURCE: PROVEN / BLOCKED / NOT APPLICABLE
PRODUCTION SOURCE: PROVEN / BLOCKED / NOT APPLICABLE
UX REFERENCE USED / TABLET + DESKTOP EVIDENCE:
POSTING OWNER / TRANSACTION + LOCK ORDER / REPLAY RULE:
QUANTITY & WIP RECONCILIATION:
VALUATION/LAYER/POOL BEFORE AFTER ROLLBACK:
COSTING FILES CHANGED: NONE [verified git diff] / STOP
BUILD / TEST COMMANDS, PASS / FAIL / SKIP:
PO / QA / PERMISSIONS / TENANCY EVIDENCE:
KNOWN UNSUPPORTED CASES AND USER MESSAGES:
DECISION: COMPLETE / PARTIAL / BLOCKED
STOPPED — AWAITING EXPLICIT NEXT PHASE APPROVAL
```

---

## 19. Final v6 deep review decision

**Decision: APPROVED FOR PHASE 0 READ-ONLY AUDIT ONLY; IMPLEMENTATION NOT APPROVED.** Critical, source-verified blockers remain for **Inventory transfer atomic integration**, **lot-controlled Inventory round trip**, **offsite supplier stock isolation** and **partial WIP custody**. The full module is not 10/10 implementation-ready. The product architecture is **ONE Subcon module** with Inventory-only and Production WIP source modes, excellent shared DevExpress operator workflow, and **separate authoritative** source posting integrations. The plan is intentionally **NOT** blanket-approved for Phases 1–4: the current repository contains complex inventory valuation and WIP execution authority, and Phase 0 must first prove the precise atomic integration and cost-safe behavior. Phase 5 stays excluded.

**Never mark `APPROVED FOR IMPLEMENTATION — 10/10` unless actual current repository paths, SQL Server concurrency, source/rollback integration, quantity/value reconciliation and operator UAT have been demonstrated.** UI simplicity must not weaken costing, quantity or permissions.


---

## 20. Reviewer conclusion and exact no-go items (v6)

**What improves vs v5:** The plan now distinguishes a realistic *non-lot same-item inventory pilot* from unsafe lot-controlled return assumptions, recognizes that Stock Transfer's public POST is independently committing and bulk posting is per batch, requires actual offsite warehouse exclusion across every downstream stock consumer, corrects service-PO recognition and existing Inventory rollback status, and makes end-to-end mode release/early usability testing mandatory. It continues to target one shared workbench, no extra module, fast daily entry and no silent cost/quantity changes.

**What is *not* yet verified:** An atomic in-transaction transfer extension that requires no protected-code edit; a closed-loop lot-controlled Inventory return using current destination-lot rules; an all-consumer external-stock exclusion policy; a safely enforceable partial WIP custody/return authority; a deployed QA authorization route for each target tenant; true PO/AP service settlement; production release accounting; actual SQL Server tests or five-person operator UAT. None is represented as solved.

**Approval table:**

| Workstream | Gate | Current decision |
|---|---|---|
| Phase 0 / Grok 4.6 current-repo evidence, source matrix, UX wireframe | Read-only | **GO** |
| Phase 1 / common UI + safe setup/PO linking, no physical posting | Needs Phase 0 findings and exact file/schema scope | **HOLD** |
| Phase 2I / Inventory non-lot dispatch + rollback | Requires atomic source adapter + offsite exclusion | **BLOCKED until proven** |
| Phase 2I / Inventory LOT-controlled dispatch + return | Requires legal new-lot round trip and lot cost proof | **BLOCKED** |
| Phase 2P / WIP dispatch + rollback | Requires all-consumer WIP offsite guard and cost proof | **BLOCKED** |
| Phase 2M / owned materials to supplier | Separate scope and approval | **BLOCKED** |
| Phase 3 / receipt + QA + reversal | Separate per-mode complete source evidence | **HOLD** |
| Phase 4 / inquiry/UAT and live enablement | Complete end-to-end per mode, accounting, UX proof | **HOLD** |
| Phase 5 / costing/conversion/write-offs | Excluded | **OUT OF SCOPE** |

**Explicit user-approval trigger:** If an agent concludes that a minimal protected-code extension is necessary for safe transfer/WIP posting, it must present (1) exact affected methods and diff sketch, (2) quantified source→ledger→valuation→rollback impact, (3) SQL Server concurrency/regression matrix, and (4) rollback strategy. **Stop before changing source posting, FIFO, CostLayer, WIP costs or inventory selection rules.** Neither UX convenience nor prior general phase approval authorizes these edits.
