# PRODUCTIONV2 — FLEXIBLE SUBCONTRACTING MODULE
## AI Coding Agent Plan v5 · Inventory-Only + Production-Integrated · Grok 4.6

**Repository:** `mokth/net10projectTemplate` · **Branch:** `productionv2` · **Observed branch HEAD:** `2b348746c40055aa322bd07d3f15d46b0bd35e32` (reconfirm before implementation)

**Decision:** **One subcontracting module, one shared transaction experience, two optional source modes.** Production must **not** be required to use subcontracting.

**Review state:** **PHASE 0 AUDIT APPROVED TO EXECUTE ONLY.** Phases 1–4 remain gated by branch-specific source-of-truth and cost/posting evidence. No automatic approval or promise of 10/10. **Grok 4.6** assigned to Phases 0–4, one phase per explicit gate. Phase 5 conversion/capitalization is **excluded**.

**Supersedes:** v4 `productionv2_SUBCON_MODULE_AI_CODING_AGENT_PLAN_v4_FINAL_STRICT_GROK_4_6.md`. Implement **this** document, not both; the v4 WO-only mandatory assumptions are obsolete.

---

## 1. Crucial requirement correction

The previous plan wrongly assumed every subcontracted job belongs to a Production Work Order. Malaysian SMEs may use only **Inventory + Sales + Procurement**, yet send stocked items to outside workshops for polishing, printing, coating, packaging, finishing, sorting or rework. They need PO → send out → partial return → stock available, with supplier balances, statements and KPIs, **without enabling Production**.

The solution is a **standalone Subcontracting bounded module** with optional integrations, not a `Production` child module and not two separately maintained implementations.

### Supported tenant configurations

| Company configuration | New job source(s) permitted | Default UI entry | Required modules |
|---|---|---|---|
| Subcon OFF | none; outstanding historical reconciliation remains available to authorized administrators | no new jobs | none |
| **Inventory Subcon** | Inventory stock item/lot/location | Inventory item or PO | Inventory, Procurement for standard service PO |
| **Production Subcon** | WO route/operation WIP | WO operation or PO | Production + Inventory/Procurement as actually required by current services |
| **Both** | Inventory **and** WO operation | auto-detect from launch context; optional source picker for New | Source-specific |

**Allow both sources in one company.** Do not use one mutually exclusive `SubconMode` flag that prevents legitimate coexistence. If user opens from Stock Inquiry, preselect Inventory. If from WO operation, preselect Production. If from PO, ask only if ambiguous. Once a job has posted activity, **source type is immutable**.

**Never require WO, BOM, routing, machines or production-location tables in Inventory jobs.** Database FK(s) to WO/operations must be nullable for Inventory jobs. Underlying inventory source reference is mandatory for inventory custody movements. Conversely, Production jobs must not pretend that WO WIP is already ordinary `IvBalLoc` stock.

---

## 2. Verified current repository facts (code read at observed HEAD)

Evidence below is based on current **productionv2** source, not default-branch search:

1. `ErpWeb.Model/Entities/Production/ProductionWorkOrderOperation.cs` has `ProcessType` documentation including `SUBCONTRACT`, `PlannedInputQty`, `PlannedOutputQty`, `GoodQty`, `HoldQty`, `RejectQty`, `RemainingQty`; this proves a production routing classification exists, **not** a functioning subcon issue/receipt lifecycle.
2. `ErpWeb.Model/Entities/Production/ProductionBalLot.cs` explicitly says `Qty/BaseQty` are authoritative and movements are audit; holds `TotalCost`, `AverageUnitCost`, WO, operation and route-step references. Production subcontract issue cannot safely invent a second authoritative quantity ledger.
3. `ErpWeb.Core/Production/ProductionOutputService.Posting.cs` (`PostAsync`, first ~110 lines and movement logic ~418–585) locks WO/operation, enforces routing, and consumes/produces `ProductionBalLot` with cost calculations and posting links. **A custody-only table is insufficient if this path still sees dispatched WIP as available.**
4. `ErpWeb.Model/Entities/Inventory/IvBalLoc.cs` stores item + warehouse/location/lot/status quantity and valuation fields; `ErpWeb.Core/Inventory/IvStockTransferService.cs` validates transfers of stock-controlled items and delegates `PostAsync`/`RollbackAsync` to `IIvInventoryPostingService`. Existing transfer is a **candidate**, not yet proof of supplier custody or seller-ineligible stock.
5. `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` routes posted Inventory Stock Transfer through current posting, authorization, future-date and reconciliation/valuation paths. **Do not implement subcon by calling unrelated Misc Issue and Misc Receipt**, which could consume/recreate value.
6. `ErpWeb.Model/Entities/Purchase/PoOrder.cs`/`PoOrderDetail.cs` contain existing PO and quantities; `ErpWeb.Core/Purchase/PoOrderCalc.cs` has `IsServiceIType` for `SERVICE`, and `PoOrderService.cs` uses service classification. Reuse existing commercial service PO path, subject to live invoice/receipt/status review.
7. `ErpWeb.Core/Settings/AppSettingModules.cs`, `AppSettingCatalogue.cs`, `AppSettingService.cs`, and `AppSettingResolver.cs` provide a known-key settings registry, no-schema-change additions, company/branch/global resolution and audited edits. Prefer registering Subcon settings there to adding flags to Company tables. Decide whether each safety-critical toggle is **company-only** and fail closed if unresolved.
8. `ErpWeb.Core/Menus/MenuCodes.cs` has Inventory Stock Transfer and PO permission names; `ErpWeb/docs/menu-access.md` documents action/service-layer, company-scoped access checks. Verify live enforcement for new screens/actions.
9. `ErpWeb.Core/Production/ProductionProcessHandoff.cs` can reject parallel-process predecessor handoff. `ProductionWorkOrderRouteStep` describes **non-100% yield as deferred**; do not silently support transformation/yield in this phase.
10. `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor[.cs]`, stock-transfer page, `ErpWeb.UI/Planning/WorkOrders/PrDailyProductionEntry.razor[.cs]` and PO entry are candidate UI references. Examine the live versions before coding.

**Still NOT verified end-to-end:** exact live QA/inspection/hold handling; all stock allocation, sales reservation, stock-count and warehouse eligibility consumers; inventory transfer cost-layer/replay conservation; service PO vs invoice posting; customer-specific menu feature enablement; safe transaction composition across Subcon + inventory/production posting; full test results. **Phase 0 must close these gaps.**

---

## 3. SME business examples and supported capabilities

### A. Inventory-only, same-stock-item finishing (Phase 2I/3I target)

Company uses Inventory, Procurement and Sales, **no Production module**.

- Stock: `METAL-PART` 1,000 PCS in main warehouse.
- PO for polishing service, vendor `ABC POLISHING` (unit service fee 0.80/PCS).
- Dispatch 600 PCS of `METAL-PART` + lot and optional company-owned accessories when separately authorized.
- Vendor physically holds 600. Company-owned quantity must move to a controlled offsite slice **without being available to Sales/ordinary warehouse issue**.
- Vendor returns 350 PCS, then 240 PCS; 10 still external. Receipt may be held for QC; only released/eligible stock can be used by Sales or internal movements.
- No new SKU, no change in UOM or quantity/yield basis. Record service fee commercially; capitalization is a **separate** decision.

### B. Production-integrated, WIP operation outsourcing (Phase 2P/3P target)

WO-001: Cutting (internal) → Painting (`ProcessType=SUBCONTRACT`) → Assembly (internal).

- Create linked service PO, dispatch 600 WIP from verified operation/lot.
- Partial receipts 350 + 200, 50 remain at vendor.
- Accepted 550 become eligible for next operation **only via validated production posting/eligibility rules**. Never fabricate output/FG by simply adding a receipt record.
- No inventory-only warehouse transfer masquerading as a WO WIP handoff.

### C. Inventory-only transformation (architectural capability, **not enabled** in Phases 1–4)

Send `RAW-PLATE` 100 KG and receive `PAINTED-PART` 90 PCS, with 5 KG scrap and vendor service cost. **This is a conversion / material-consumption / valuation event**, not a same-item stock transfer. It needs an actual inventory processing order with input→output genealogy and cost conservation. Preserve as a designed source type or feature gate, but **do not allow Post** until a separately approved conversion + costing implementation exists. UI must say “This processing changes the item or quantity. It requires Inventory Processing setup; posting is not available yet.”

### D. Customer-owned material (future, separately gated)

If subcontractor processes **a customer's** property that is not company-owned, do not put it into company-owned valued `IvBalLoc` or WO costs. Distinct ownership and custody/accounting rules require a separate design approval.

---

## 4. One-domain architecture, no three projects

```
ErpWeb.UI/ Subcontracting (single menu family and common screens)
  |-- New Job / Send Out / Receive Back / Inquiry / Dashboard
  +-- simple source panels (Inventory lookup OR WO-operation lookup)
             |
ErpWeb.Core/Subcontracting  (bounded orchestration + domain validation)
  |-- SubconJobService, DispatchService, ReceiptService, InquiryService
  |-- ISubconSourceAdapter / source resolver (only where justified)
  |       |-- InventorySubconSourceAdapter (IvBalLoc + stock transfer)
  |       +-- ProductionSubconSourceAdapter (WO operation + ProductionBalLot)
  |-- Procurement link adapter (existing PO SERVICE line)
  |-- QA link adapter (ONLY actual existing QA path)
             |
existing Inventory posting, Production posting, Purchase PO/invoice services
```

**Reuse one existing repo project per layer**, not new deployments, microservices, competing posting engines or variant Razor pages for each company. An adapter is a thin, source-specific boundary, **not** a new ledger or generic rules engine. If current repo already has a reusable transaction orchestration pattern, prefer that and remove unnecessary adapters.

**Domain separation**: Subcon owns *commercial job/dispatch/receipt identity, vendor custody trace and reconciliation*; Inventory owns *Inventory quantity, lot and value*; Production owns *WO/WIP quantity, costing and stage eligibility*; Procurement owns *PO/invoice obligations*. Inquiry aggregates these without becoming an alternative posting authority.

---

## 5. Tenant settings, menus and rights

Use existing `AppSettingModules`, `AppSettingCatalogue` and settings UI patterns; register new `SUBCONTRACTING` module only if no existing appropriate grouping exists. Proposed keys (final names decided Phase 0):

| Key | Default | Meaning / gate |
|---|---|---|
| `ENABLED` | false | Prevent new transactions by default; preserve authorized history/reconciliation |
| `INVENTORY_SOURCE_ENABLED` | false | Inventory-only job source permitted after inventory source safety gate |
| `PRODUCTION_SOURCE_ENABLED` | false | WO WIP source permitted only with Production and WIP posting gate |
| `REQUIRE_SERVICE_PO` | true | Posted dispatch requires eligible service PO unless approved alternative policy |
| `ALLOW_SUPPLIED_MATERIALS` | false | Separate material-custody gate; not proof consumption is supported |
| `REQUIRE_INSPECTION_RELEASE` | per verified existing QA policy | Stock/WIP remains held until accepted according to actual QA integration |
| `INVENTORY_CONVERSION_ENABLED` | false | Locked false through Phases 0–4; a configured `true` is rejected without implementation gate |
| `SERVICE_COST_TREATMENT` | `PENDING_REVIEW` | Finance sign-off: expense permitted or capitalization required; do not guess |

**No silent default enable**. Validate cross-setting combinations on admin Save and POST. Company/branch permissions remain authoritative; do not rely on menu hiding. Disabling after posting must **not orphan external balances**: keep inquiry and manager-authorized completion/return/reversal path, prohibit new jobs, require zero outstanding before full disable (or formally supervised migration).

**UI menu recommendations**:
- Shared functional menu: `Subcontracting` (top-level when Production is not installed). Items: **Overview**, **Send Out**, **Receive Back**, **Jobs / Inquiry**, **Settings** (admin only).
- Optional shortcut in **Inventory > Subcontracting** and **Production > Subcontracting**, both navigate to the **same** pages/services. Avoid duplicate menu records/rights and route implementations.
- PO generation remains in existing **Procurement > Purchase Order**; expose `Create Subcon PO` shortcut from Job, obey approval.
- No Production menu in inventory-only tenant. Do not inject WO lookups or WO permissions into inventory-only users.
- Named permissions: ACCESS, ADD, EDIT, DELETE (NEW only), POST, ROLLBACK, PRINT, VIEW_COST, APPROVE_EXCEPTION (reuse current shared codes where available). Authorize in core service as well as page.

---

## 6. Transaction model (provisional: Phase 0 must finalize existing schema reuse)

**Common**:
- `SubconJob`: tenant/company, branch, JobNo, SourceType (`INVENTORY_STOCK` / `WO_OPERATION`), vendor, planned qty/UOM, service PO line reference, due date, lifecycle; external reference; rowversion, audit, immutable source after first post.
- `SubconJobSource` (or fields, **not both**): `INVENTORY_STOCK` requires original `IvBalLoc`/stock item-lot identity; `WO_OPERATION` requires WO, route step, operation, WIP lot/source. All source-specific FKs nullable in common schema but conditional validation is mandatory.
- `SubconDispatch` + `SubconDispatchLine`: vendor/date/status, source job, base Qty, lot, original allocation and posting link, printed delivery note, transport notes; allow multiple partial issues.
- `SubconReceipt` + `SubconReceiptLine`: dispatch-line reference, physical returned, accepted, held, rejected returned, unprocessed returned, QA reference, posting link; allow multiple partial receipts and separate later QA release.
- `SubconCustodyEvent`: immutable per company+branch+source lot+vendor+transaction+line+effective date, dispatch/return/reversal/approved external loss. Derived supplier balance only; **never** a competing writable `IvBalLoc` or `ProductionBalLot`.
- Optional `SubconMaterialLine`: **only after Phase 2M safety approval**, company-owned materials mapped to actual stock posting, source lot/UOM and cost.

**Unique keys / checks**: company+branch+JobNo, document numbers, source type/link, dispatch posting request ID, receipt posting request ID, negative qty forbidden, source-specific FK conditional checks, immutable posted source identity, rowversion, source document genealogy. A physical consolidated truck manifest can contain links to separate mode-specific posted documents without pretending cross-module posting is atomic; a single dispatch header may combine jobs **only if** entire header can post in one verified atomic transaction and all source locks are correct. Default v1: **do not mix Inventory and Production source modes inside one post header**; print a consolidation manifest if needed.

---

## 7. Inventory-only source: posting and rollback contract (Phases 2I/3I)

### 7.1 Safe same-item custody
- **Candidate mechanism**: existing Inventory Stock Transfer, with supplier-owned-location representation as **company-owned offsite custody**. Dispatch transfers original item/lot/status to a controlled vendor warehouse/location; receipt transfers back. No issue/receipt purchase or inventory consumption.
- Phase 0 must choose, document and test exact mapping: supplier-dedicated warehouse, or company offsite warehouse with supplier-specific location; ensure branch, lot, stock status and company isolation. **Never assume ordinary warehouse transfers automatically isolate the stock from Sales**.
- Verify **all availability consumers**: sales SO/DO/Invoice/packing, inventory issue/transfer/count, soft reservations, stock valuation and inquiry, supplier/vendor returns, lot genealogy, and future-as-of availability. Vendor stock must not be accidentally sold, used or included as available in normal warehouse choices.
- If existing filters cannot exclude vendor custody without modifying core costing/stock engines, **STOP and request a separate safety design**; do not deploy an offsite warehouse that sales can ship from.
- Only physically accepted stock returns to an eligible factory slice. Physically returned *held* stock moves to an approved non-sellable offsite/quarantine slice (if the existing stock model supports it); do **not** silently release it because `IStatus` was preserved by a normal stock transfer.
- Qty/lot/valuation conserved across dispatch/receipt/reversal, except an explicit independently approved loss/scrap/cost adjustment. Reconciliation must use posting records and stock slices, not only Subcon events.

### 7.2 Lifecycle
1. Create or link service PO, ensure correct supplier and service item.
2. New Subcon Job from PO or Inventory Item. Stock lookup (item → location/lot with available qty). Default quantity, PO, vendor and dates. Save NEW.
3. `Send Out`: explicit POST creates **one** stock movement and **one** linked custody event in a proven atomic boundary. Supplier balance increments; local usable stock decreases; total company-owned stock value conserved.
4. `Receive Back`: enter qty, inspection result, and source issue. Explicit POST returns physical qty and adjusts vendor custody; accepted/held must land in appropriate eligible/ineligible slices. No manufacturing output / duplicate goods receipt.
5. `Rollback` of dispatch available in Phase 2I itself, and of receipt in Phase 3I, blocked if later receipt, reservations, sale, warehouse movement, hold release, invoice/cost dependency, closed period or backdated replay would be invalidated. Standard existing POSTED→NEW semantics only if validated; else compensating reversal with audit.

### 7.3 Items changing SKU/UOM or yield
Stock transfer has **same item identity**, so it cannot be used for input SKU X → output SKU Y, many-to-one, one-to-many, weight-to-pieces conversion, process loss, material consumption or service-cost capitalization. **Block transaction Post** for these until the separately approved Inventory Processing / Conversion feature is ready. Do not misuse stock adjustment + receipt to fake conversion; that breaks genealogy/cost.

---

## 8. Production source: WIP operation contract (Phases 2P/3P)

- Job links to actual `ProductionWorkOrderOperation` `ProcessType=SUBCONTRACT` (or proven routing designation), WO/route/lot. The `SUBCONTRACT` value already exists in entity documentation; verify behavior before any new flags or duplicate fields.
- Derived *eligible dispatch quantity* is a consistent, locked view of posted predecessor output/available `ProductionBalLot` (or exact live source determined Phase 0) **minus already dispatched/used/held quantities**, with UOM conversion; source production lot quantity/cost cannot be duplicated in subcon tables.
- Before any dispatch, all existing production execution services that could consume the same WIP must respect vendor-outside quantities. This requires **atomic** availability gating at the same lock/transaction boundary as current Daily Production/transfer/FG. If not safely possible without an unapproved costing algorithm change, **stop Production mode**; Inventory mode may still proceed independently if safe.
- A supplier physical receipt is **not** automatically Daily Production GoodQty, a new `ProductionBalLot`, a material issue, FG receipt, or operation completion. Phase 0 decides whether existing output posting can represent outsourced completion without cost misstatement; if not, hold accepted qty and block next operation until approved integration.
- Reuse verified QA decisions, operation sequence gate, partial handoff and next operation eligibility; blocked parallel sequence and non-100 yield cases must remain blocked, not bypassed.
- Phase 2P must include safe dispatch **and rollback**; Phase 3P safe receipt, QA release and rollback; all downstream edges/closed period checks mandatory.
- Subcon fee cannot silently alter WIP costing; show commercial spend separately. If fee must be capitalized into WIP/FG, prohibit claiming complete cost / completing cost-sensitive WO under an unapproved policy.

---

## 9. Company-supplied materials (separate 2M gate)

Optional for either source; normal dispatch does NOT imply consumption. Require original company-owned stock source and lot. Distinguish:
- **Raw materials still in Inventory**: move by existing inventory transfer into verified supplier custody; consume/return only through approved existing movement and valuation semantics.
- **Materials already issued to WO/Production**: do NOT issue from Inventory a second time. Reuse actual production-material custody/movement provenance; otherwise block.
- **Vendor's own materials**: not our inventory, only service/vendor commercial charge.
- Materials actually **consumed** to create a different output are a valuation/conversion event, not a balance-only note; disable if existing costing-safe posting API absent.

For each company+vendor+job+item+lot/base UOM: `OutstandingVendorMaterial = postedTransferIn - postedReturn - postedApprovedConsumption - postedApprovedLoss`, with each change linked to real stock/WIP posting. A note-only “used 20” must never make inventory disappear. Under 0–4 allow physical custody and return only when provably safe. Consumption, loss, and costing corrections are outside scope unless an existing fully tested service supports them unchanged.

---

## 10. Commercial PO / AP / e-invoice and pricing

- One service PO may cover one or several Subcon Jobs **only if** each quantity allocation is traceable and not overcommitted; partial dispatch/receipt require line-level matching. Job may be created from PO or generate/link PO from Job. No duplicate PO on retry.
- Use existing service item classification (`IType = SERVICE`) and PO workflows; verify whether an indirect item can also represent processing service. Do not hijack stock item GRN to post WIP.
- `BillableQty` is an explicitly configured commercial basis (accepted PCS, physically processed PCS, batch fee, kg, etc.), **not** blindly equal to returned quantity. Overbilling, reject/rework charge, tax, discounts, currency and invoice-before-return policy require validated rules.
- Preserve existing procurement approval, invoice matching, SST/e-invoice, self-billed rules, supplier identity, audit and period close. Do not make tax assumptions for a household operator or individual.
- Service `PO commitment`, `invoice expense`, and `capitalized stock/WIP cost` are **different metrics**. Financial treatment requires an accounting decision and test; no hidden capitalization in phases 0–4.

---

## 11. Single fast and friendly UI

All screens in **Subcontracting**, with adaptive source section:

**Jobs / Overview**: search supplier, PO, item, WO (only when source allows), status, due date; recent jobs, open/overdue and remaining qty; quick `Send Out`, `Receive Back`, `Print` actions.

**New Job**: start from PO (common), from stock balance (Inventory mode), or from WO operation (Production mode). Lookup for supplier/PO/item/lot; auto-fill quantities/UOM/rate/due date. Hide fields that do not apply. No typing raw IDs, WO codes or supplier codes.

**Send Out**: show selected job, available eligible qty, outstanding PO processing qty, supplier, optional lot/material lines, dispatch quantity and date. Default quantities safely; Save and Post deliberately separate. Print dispatch/delivery note. Bulk rows one-screen by supplier, only if atomic.

**Receive Back**: scan/search supplier delivery note/job/dispatch. Show outstanding qty prefilled; input physical returned, accepted, hold/rejected, unresolved remaining; optional receipt images and operator notes. One clear `Save`, `Post`, `Rollback`; `Accept/Release` only according to actual QA policy.

**UI principles**: use actual Misc Receipt and PO transaction style, leftmost grid actions, compact aligned input widths, responsive tablet and desktop, keyboard-friendly repetitive entry, clear status/remaining. Show plain messages like `You sent 600 PCS; 250 PCS are still with ABC Polishing`. For blocked conversion: `Different output item requires Inventory Processing approval; no stock movement was posted.` No technical FIFO/replay exceptions to user.

**Company with Production OFF** must not show empty WO filters, `Select Work Order` blockers or Production menus; direct URL + backend request must still be forbidden when Production source is disabled. Company with BOTH gets non-intrusive source badge and contextual launch shortcuts, not separate duplicate entry pages.

---

## 12. Reconciliation, reports and KPIs

Read-only source-authoritative inquiry; each figure drillable to posted movement, PO, receipt and lot/operation. Filters: company+branch (authorized), vendor, job, item, WO (conditional), PO, source mode, lot, due date and as-of date.

Metrics: Open jobs, qty with subcontractor, overdue qty/aging, part-returned jobs, on-time complete dispatches, supplier average lead time, accepted/returned ratio, rejection %, approved loss, material balance, service PO commitment, invoice spend, **cost capitalization status**. Segment Inventory-only versus Production, with combined totals **only** if unit basis and accounting meaning match. Do not sum mixed PCS/KG as a single quantity. Show `—` for invalid denominators and separate order-weighted vs quantity-weighted on-time measures.

Print: Dispatch Note, Receipt Acknowledgement, Outstanding Job List, Vendor Balance Statement, Supplier Performance and Source-to-Cost Reconciliation. As-of historical balances derived from posted/reversed events + underlying stock/WIP history, never current snapshot projected backward without chronology.

---

## 13. Quantities, state transitions, atomicity and date policies

**Custody (base UOM per source lot and vendor):**
```
NetDispatched = Σposted dispatch - Σeffective posted reversals
PhysicallyReturned = Σposted physical return - Σeffective return reversals
ResolvedExternalLoss = Σloss actually posted via authorized quantity/value write-off
OutstandingAtVendor = NetDispatched - PhysicallyReturned - ResolvedExternalLoss >= 0
```
Do not count hold/rejected returned twice. Accepted/hold/rejected returned/unprocessed returned classification must sum to physical return (unless a documented QA-later state allows `pending classification`). Supplier-side scrap not physically returned is **not** physical receipt; costed loss requires separate approved posting.

Inventory source invariant: `factory eligible + vendor offsite + held/non-eligible + approved consumed/loss = source total adjusted for other authorized movements` at relevant effective date and cost method, with no unexplained value drift. Production source invariant: outside WIP cannot also be consumed by internal operation; accepted next-step quantity <= once-only posted authorized accepted quantity. Partial receipt cannot create duplicate input for same operation.

**States**: Job `OPEN`, `PARTIALLY_DISPATCHED`, `OUTSIDE`, `PARTIALLY_RETURNED`, `ON_HOLD`, `CLOSED`, `CANCELLED` as derived where possible; document `NEW`, `POSTED`, `ROLLED_BACK/REVERSED` according to proven existing ERP pattern. Never delete posted history or flip status manually. Mode/site/lot editing prohibited once posted.

**Atomic posting**: inventory/production movement + Subcon document status + posting key + custody event must commit under a single verified DB transaction/lock domain, or feature remains unavailable. Calling two independent `PostAsync` services sequentially and compensating afterwards is NOT an approved substitute. Need exact lock ordering, unique source-activity keys, request idempotency, branch + company verification, retry semantics, concurrency exception mapping.

**Posting/rollback**: same transaction rechecks period close, future date, stock as-of, later dependency/consumption/QA/sale/invoice and work-order status. Saving a future-dated NEW may follow existing policy; POST/ROLLBACK must obey current business-date and replay controls. Backdated posting that requires unsupported stock replay must stop without partial commit.

**Testing cost freeze**: actual code diff must not edit `FIFO`, `CostLayer`, valuation, WIP cost formula, replay, month-end engine in phases 0–4. Tests record *cost/lot movement before and after* for all stock and WIP integrations, including standard/WAC/FIFO where configured. Unexplained effects = blocker even when no costing files changed.

---

## 14. Grok 4.6 implementation gates (one stop per phase)

### Phase 0 — BRANCH AUDIT ONLY, NOW AUTHORIZED
At live HEAD, produce `plans/subcon_dual_mode_phase0_evidence.md` with exact file path, method, line, current behavior and tests for both modes, and a **revised exact-file plan**. Mandatory investigation:
1. Inventory transfer source balance and valuation/lot replay, vendor location/status, stock eligibility across SO/DO/Invoice, picking, stock counts, reservations, warehouse queries.
2. Production WO process `SUBCONTRACT` behavior, `ProductionBalLot`, Daily Production, handoff, QA and operation/FG dependencies; confirmation whether vendor custody can be blocked for internal production consumers.
3. PO SERVICE item and invoice matching; finance requirement for capitalization and expense-only policy.
4. AppSetting catalogue/register/scope and company-scoped named menu authorization.
5. Unified schema proposal, conditional FKs, concurrency lock order, transaction composition, rollback and month-end.
6. Existing UI source for Misc Receipt, Stock Transfer, PO, Work Order and QA; reference screenshot/wireframe.
7. Exact inventory-only non-Production test fixture and production-integrated fixture, source/qty/cost trace before/after.
8. Document all unsupported cases and how they are disabled; clearly state which of 2I / 2P / 2M / 3I / 3P are safe.
**STOP.** No code, DDL or implementation changes in Phase 0.

### Phase 1 — BASE FOUNDATION (NOT YET APPROVED)
Settings/permissions, common Job + PO service linkage, contextual UI, source selection, supplier lookup, read-only inquiry and provisional dispatch/receipt drafts; **no posting**. Schema only after approval of Phase 0 data model and backward-compatible migration. Test Inventory-only tenant startup with Production disabled; other modules unchanged.

### Phase 2I — INVENTORY DISPATCH + ROLLBACK (GATED)
Only same-SKU/UOM qty-preserving transfers at approved vendor location; atomic transfer + custody; exclusions from sales/ordinary inventory flows and valuation conservation proven. Every POST has tested rollback, retry, duplicate, concurrency, future/closed period checks. Test independently of Production.

### Phase 2P — PRODUCTION WIP DISPATCH + ROLLBACK (SEPARATE GATE)
Only if WO WIP availability and lock integration can be proven without touching cost calculation. Keep disabled until proof. A pass of Phase 2I is **not** a Phase 2P approval.

### Phase 2M — OPTIONAL MATERIAL CUSTODY (SEPARATE GATE)
May be postponed; stock owned/lot/source trace, no double issue or implicit consumption, posting+rollback validated. Do not slip into 2I/2P implementation.

### Phase 3I — INVENTORY RETURN + ROLLBACK (GATED)
Partial physical returns, accepted/held slices, vendor qty decrease, downstream sales release only if accepted and eligible; reversal cannot invalidate subsequent sales/stock transactions.

### Phase 3P — PRODUCTION WIP RETURN + ROLLBACK (SEPARATE GATE)
Partial WIP receipt, QA, actual WO operation/next-step eligibility, no double production output, no cost modification, rollback dependency checks. Keep disabled until end-to-end source proof.

### Phase 4 — INQUIRY, KPI, PRINTING, UAT (GATED)
Mode-aware inquiry, supplier statements, as-of reconciliation, source-linked drilldowns, usable tablet and desktop screens, role/regression tests. No false mixed-unit totals, no cost completeness claims.

### Phase 5 (NOT ASSIGNED) — INVENTORY CONVERSION, CONSUMPTION + CAPITALIZATION
If inventory-only customer needs raw SKU→processed SKU, yield/scrap, extra materials consumed, cost service fee added to item/WIP/FG or customer-owned stock, prepare and approve separate accounting + posting design before implementation. **Do not allow a same-SKU-only v1 to claim complete conversion functionality.**

---

## 15. Must-have end-to-end tests and numerical evidence

1. Inventory-only company with Production flag OFF: create service PO + Subcon Job → dispatch 600 → receive 350 + 240 → outstanding 10; stock at vendor/factory reconcile by lot; no WO read/write.
2. Inventory-only stock 100, two users dispatch 70: exactly one can consume the last eligible stock; no overdispatch; no sale of vendor-held stock.
3. Dispatch 600, receipt 300 accepted + 20 on hold + 10 rejected returned, physically returned 330, vendor outstanding 270; only accepted 300 is sale-eligible.
4. Qty/cost before/after dispatch/return: no unexplained valuation change, cost layer provenance preserved per existing transfer/replay behavior (FIFO/WAC/standard scenarios).
5. Duplicate POST after timeout and repeated rollback: exactly one movement/event, consistent response.
6. Partial receipt then attempted parent dispatch rollback: blocked, or safe ordered reversal with complete dependency checks.
7. Closed period, post future date, backdated transaction, future-as-of stock: same existing policy and no partial commits.
8. Service PO edit/cancel and invoice accrual while job open; ensure no duplicate inventory GRN, over-invoice or unapproved capitalization.
9. Production WO 1000 WIP: dispatch 600 + 400; partial returns; next operation cannot consume vendor-held/held WIP; posted Good/hold/FG balances reconcile; no duplicate work order output.
10. Multiple jobs per supplier, multiple branches and companies: batch/manifest trace accurate; no cross-tenant access.
11. Vendor supplied raw materials 100 (if 2M approved): 65 consumed only via approved posting, 35 returned; issue previously consumed for WO cannot be consumed again. If no approved service, transaction fails clearly.
12. SKU transformation `RAW-A` to `COATED-B` attempted in Phase 0–4: posting blocked with meaningful message; no adjustment or receipt created.
13. Disable/enable setting with outstanding supplier balance: no new dispatch after disable; authorized receipt/reconciliation still possible; no orphaned stock.
14. Stock count, SO/DO/Invoice, stock alerts, reservation, valuation report, cost inquiry, lot genealogy and period close not broken by offsite location.
15. CRUD permissions by named action, `VIEW_COST` protection, hidden Production controls, direct HTTP URL/API, audit.
16. Keyboard/tab order, 768/1024/1440 width, left row actions, Save/Cancel/Post/Rollback, lookups, simple actionable errors; daily bulk processing and printing.

Each gate delivers commands actually run, pass/fail counts, database evidence with baseline and output, changed files and explicit `COSTING FILES MODIFIED: NONE` from diff. A file unchanged does **not** prove zero indirect costing impact.

---

## 16. Absolute MUST NOT

- DO NOT require `WorkOrderId`, Production installation, WO menu, BOM or route step for Inventory-source jobs.
- DO NOT create separate custom projects/pages/services for Inventory-only versus Production; one common module, thin source-aware integration.
- DO NOT convert item X to item Y using Inventory Transfer, Misc Issue+Receipt, stock adjustment or fake GRN without approved conversion/valuation accounting.
- DO NOT double-count WO WIP or company stock; no second editable stock/WIP balance ledger.
- DO NOT let vendor-held stock be pickable by sales or usable by internal production.
- DO NOT alter FIFO, CostLayer, stock/WIP valuation formulas, existing replay and month-end engines in Phases 0–4.
- DO NOT automatically capitalize service PO/invoice amount, omit required cost silently or claim finished goods margin/cost complete.
- DO NOT start Phase 1 from this document without Phase 0 evidence; do not mark 10/10 only because design is comprehensive.
- DO NOT downgrade tenant/company/branch permission enforcement, atomicity, QA holds, rollback safety or audit for UI convenience.

---

## 17. Paste-ready Cursor / Grok 4.6 handoff

> Inspect CURRENT `mokth/net10projectTemplate` **productionv2** branch and record HEAD SHA. Read `productionv2_SUBCON_MODULE_AI_CODING_AGENT_PLAN_v5_DUAL_MODE_GROK_4_6.md`. **Execute Phase 0 ONLY.** We now require **ONE independent Subcontracting module supporting Inventory-only customers without Production AND Work Order-integrated customers**, with a shared daily UI and source-specific posting adapters. Inventory mode must not require WO, BOM, routing, Production permissions or production menu. Production WIP must not be represented as ordinary Inventory stock. Study actual current Inventory Transfer, Sales availability, ProductionBalLot/Daily Production, PO service item/invoice, settings and permissions. Return exact-file evidence, source-of-truth and locking/cost maps, a revised plan, test matrix and blockers, then **STOP**. Do not implement/post/create migrations in Phase 0. Grok 4.6 may implement Phases 1–4 only after separate explicit approval for each subphase. No CostLayer/FIFO/WIP costing/replay/month-end modifications, no inventory conversion or fee capitalization without separate signed-off Phase 5.

---

## 18. Approval / delivery status

**Design objective accepted:** single configurable subcontracting feature with both Inventory stock and Production WIP sources.

**Approved now:** Grok 4.6 Phase 0 only.

**NOT approved yet:** Phase 1, 2I, 2P, 2M, 3I, 3P, 4. **Phase 5 excluded.** Continue after current-branch forensic review and go/no-go on each underlying movement/valuation path. Target release is only those source modes with complete, proven quantity and valuation safety; partial rollout must clearly hide disabled functionality.
