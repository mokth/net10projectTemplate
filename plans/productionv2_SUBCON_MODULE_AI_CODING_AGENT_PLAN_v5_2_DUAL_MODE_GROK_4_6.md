# PRODUCTIONV2 — FLEXIBLE SUBCONTRACTING MODULE
## AI Coding Agent Plan v5.2 · REPOSITORY-REVIEWED · Inventory-Only + Production-Integrated · Grok 4.6

**Repository:** `mokth/net10projectTemplate` · **Branch:** `productionv2` · **Reviewed branch HEAD:** `e24f54069316a346171fed7f14017c09d593cb95` (2026-11-07; ancestor `2b348746` is PR #3, two merges behind; re-pin before any phase)

**Decision:** **One subcontracting module, one shared transaction experience, two optional source modes.** Production must **not** be required to use subcontracting.

**Review state:** **PHASE 0 EVIDENCE / DECISION AUDIT APPROVED TO EXECUTE ONLY.** Phases 1–4 remain gated by branch-specific source-of-truth and cost/posting evidence. No automatic approval or promise of 10/10. **Grok 4.6** assigned to Phases 0–4, one phase per explicit gate. Phase 5 conversion/capitalization is **excluded**.

**Source:** the v5 plan is **untracked** in the local working tree at `plans/productionv2_SUBCON_MODULE_AI_CODING_AGENT_PLAN_v5_DUAL_MODE_GROK_4_6.md` (`git status` shows `??` for that path) and was **404 on GitHub**. This v5.2 artifact supersedes both plain-text v5.1 and v5. Before running an agent, place this file at the chosen `plans/` location, record its hash, and reconcile any independently modified copy.

**Supersedes:** v5.1 standalone copy, v5 `productionv2_SUBCON_MODULE_AI_CODING_AGENT_PLAN_v5_DUAL_MODE_GROK_4_6.md`, and v4 `..._v4_FINAL_STRICT_GROK_4_6.md`. Implement **this** document only. The v4 WO-only mandatory assumptions are obsolete.

### v5.2 corrections applied against v5.1 (review-verified)

1. **Skills evidence corrected.** v5.1 stated the seven UI/production skills "were not found at the tested exact paths." **They are git-tracked at HEAD** under `.cursor/skills/...` as lowercase `skill.md`, not `SKILL.md`. The 404 was a filename-case artifact. Exact paths are listed in §10A.
2. **Test host path corrected.** The real path is `ErpWeb.Tests/Infrastructure/TestHosts/AppSettingTestHost.cs`. `ErpWeb.Tests/Settings` does **not** exist.
3. **Duplicate QA gate removed.** v5.1 introduced `REQUIRE_INSPECTION_RELEASE` while `QA.REQUIRE_INSPECTION_ON_RECEIPT` already exists as the reserved posting gate. **Reuse the existing QA key.** Subcon gets no second inspection flag (§5).
4. **QA hold is not a custody mechanism — now stated.** `IStatus` drives sales eligibility; physical holds do not change `IStatus` and are **not** consulted by sales shipment. Added as decision-table candidate D, and Phase 3I is now explicitly dependent on the 2I isolation decision (§7.0, §14).
5. **Vehicle A enforcement point named.** Requires a persisted subcon link on the inventory TR batch plus guards in the transfer service and a block on editing a linked batch from the ordinary Stock Transfer screen; explicitly a validation guard, not valuation (§7.4).
6. **Phase 2P expectation stated.** v5.1 correctly demoted `ProductionWorkOrderTransferService`, which leaves the branch with **no** partial-custody WIP mechanism. 2P is now labelled expected NO-GO pending a new design (§8, §14).
7. **Quantity precision has two authorities** (`IvQty.Scale` constant and `INVENTORY.QTY_DECIMALS` setting); Phase 0 must resolve which governs (§13).
8. **Test 20 requires infrastructure that does not exist**; scoped as a test-only fault-injection seam in Phase 0 (§15).
9. **2I-NO-GO fallback decision added** so a failed gate does not silently leave the module unusable with no product answer (§18).

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

**Allow both sources in one company.** Do not use one mutually exclusive `SubconMode` flag that prevents legitimate coexistence. If the user opens from Stock Inquiry, preselect Inventory. If from a WO operation, preselect Production. If from a PO, ask only if ambiguous. Once a job has posted activity, **source type is immutable**.

**Never require WO, BOM, routing, machines or production-location tables in Inventory jobs.** Database FK(s) to WO/operations must be nullable for Inventory jobs. Underlying inventory source reference is mandatory for inventory custody movements. Conversely, Production jobs must not pretend that WO WIP is already ordinary `IvBalLoc` stock.

---

## 2. VERIFIED branch facts and evidence gaps (rechecked at `e24f5406`)

All paths below were read from the **current local `productionv2` working tree at `e24f5406`**, not the default branch. Line numbers are anchors for this SHA only; recheck after any commit.

| Area | Evidence and exact implication | Gate |
|---|---|---|
| Production marker | `ProductionWorkOrderOperation.cs:41`, `IPrProductDefService.cs:148`, `WorkOrderReadinessValidator.cs:213` describe `SUBCONTRACT` as an operation/process type with duration/readiness behavior. **No dispatch, receipt, custody or posting code exists** for it in `ErpWeb.Core` or `ErpWeb.UI`. This is classification, not a lifecycle. | Greenfield module built on existing posting services. |
| Authoritative WIP | `ProductionBalLot.cs:5,17–24,42–43` documents authoritative `Qty`/`BaseQty` and WIP cost; `ProductionLocation.cs:5–11` is an internal WIP location with work-centre fields and **no supplier semantics**. | No shadow WIP balance. |
| Production transfer | `ProductionWorkOrderTransferService.cs:15` (full-balance WO→WO), `:936–1027` (Post acquires branch lock then `ProductionWorkOrderExecutionLock` at `:974`), `:1177` (lock exception mapping). It is **full-balance ownership transfer between compatible work orders**, not partial vendor custody. | Not a subcon dispatch/return API without separate review. |
| Inventory warehouse | `IvWarehouse.cs:3–13` has free-text `WarehouseType`, `IsActive`, and **no vendor-custody flag**. `IvInventoryLookupService.cs:473–491` exposes all common active warehouses; `SaDoService.cs:2364–2366` takes active warehouse choices. `WarehouseType` is referenced in the warehouse master and a lookup registry only — **zero eligibility checks**. | Warehouse-only isolation currently FAIL. |
| Status transfer critical | `IvStockTransferService.cs:812–829` validates source `IStatus`; `:966–991` builds the destination slice from that **same** `iStatus`. Ordinary TR cannot convert `ACTIVE` to a custody status. `IvStatus.cs` is editable master data with `IsActive`. | Status-only via current TR = FAIL; no data-only configuration works. |
| Sales eligibility | `IvSpShipmentService.cs:889` and `IvSpFifoEligibility.cs:43` require `IStatus == ACTIVE`. Additional consumers: `SaDeliveryRequestFulfilmentService.cs:201`, `ProductionMaterialStockAvailabilityReader.cs:102`, `ProductionMaterialAllocationService.cs:214`, `ProductionMaterialIssueService.Draft.cs:163`. | `IStatus` is the only proven sales/production exclusion axis. |
| QA hold is NOT custody | `QaPhysicalHoldAuthorizationService.cs:95` writes a separate `QaPhysicalHold` row; it never changes `IStatus`. `AssertAndClaimStockOutAsync` (`:141`) blocks with an active hold (`:175`) or `QCHOLD` status (`:182`) and is wired into `IvInventoryPostingService.cs:1662, 2578, 3145, 4838` (inventory stock-out writers including TR) — **but not into `IvSpShipmentService`**. Gated by policy `PhysicalHoldEnabled` (`:29`, `:162`). | QA hold = FAIL for vendor custody; PARTIAL for stock-out only. Also constrains Phase 3I. |
| QA flag is record-only | `AppSettingCatalogue.cs:198–204`: `QA.REQUIRE_INSPECTION_ON_RECEIPT`, default `"false"`, description "Reserved for a later posting gate. Phase 1 Quality is record-only; this flag stays off and does not block GR or FG posting." | Never treat this flag as an enforced subcon release. This key is the **single** inspection authority (§5). |
| On-hand exposure | `IvInventoryLookupService.cs:334–410` builds on-hand results with **no `IStatus` filter**; `:473–491` lists all active warehouses. Vendor custody quantity will appear in inquiry totals. Not proof that a POST succeeds, but proof that selectability and server gates are separate problems. | Inspect every picker and every POST entry point. |
| TR transaction | `IvStockTransferService.cs:611–624` delegates to `_posting.PostAsync(TR)`; `IvInventoryPostingService.cs:2953–2980` opens its own context/transaction, then `:3050–3130` checks locked slices, `:3145` claims the QA stock-out, `:2979` is the transaction. | Cannot wrap this public `PostAsync` in a new ambient transaction. |
| V2 ledger seam | `IIvInventoryPostingService.cs:80–108`, `IvInventoryPostingService.cs:100–133`, `StockLedger/InventoryStockPostingCommandFactory.cs:10–69`. The factory only builds a `StockPostingCommand` (request id, evidence fingerprint, revision, reversal link) — **no physical `IvBalLoc` mutation, no `IvTrxHistory`, no document lifecycle**. | A ledger begin is not atomic TR posting (§7.4C). |
| Permission catalogue | `PermissionCodes.cs:3–66` contains `APPROVE`, `REJECT`, `CANCEL`, `VOID`, `REVERSE`, `VIEW_COST`, `PRICE_OVERRIDE`, `COSTING_METHOD_CHANGE`, `REPAIR_COST` and **no `APPROVE_EXCEPTION`**. | Reuse `APPROVE`, or register a new code under separate approval. |
| Settings | `AppSettingModules.cs:14–53` maintains `All` + `Describe`; `AppSettingCatalogue.cs:15–17` requires a default that parses as its own type and at least one token for every `Token` definition, asserted by `AppSettingCatalogueTests`; `AppSettingDefinition.cs:81` has a nullable CLR type but the catalogue contract still demands a valid default. | Add module, definitions, tokens, tests; fail closed. |
| Quantity precision | `IvTrxConstants.cs:33–38` defines `IvQty.Scale = 4` as a **constant**, while `AppSettingCatalogue.cs:31–36` declares `INVENTORY.QTY_DECIMALS` as a **setting**. Two potential authorities. | Phase 0 must resolve which governs (§13). |
| Reference patterns | `DocumentNumberingService.cs`, `IvInventoryHistoryWriter.cs`, `Traceability/LotGenealogyService.cs`, `ProductionOutputService.Posting.cs:27–100` (branch stock lock → WO execution lock → output → post link → route step → operation graph). | Reuse identity, locks, evidence, audit; do not alter costing. |
| Skills | `productionv2-costing/SKILL.md` (v1.1) plus seven **git-tracked** UI/production skills under `.cursor/skills/` with lowercase `skill.md`. Exact paths in §10A. A `SKILL.md` probe returns 404 because of case, not absence. | Use the exact paths; do not conclude the standards are missing. |
| Tests | `ErpWeb.Tests/Infrastructure/TestHosts/AppSettingTestHost.cs` exists; `ErpWeb.Tests/Settings` does **not**. Tests are grouped by area (`Admin/Inventory/Procurement/Production/Quality/Sales/Transactions/UI`). | Place subcon proofs by area; reuse the real test host. |

**Still NOT verified (Phase 0 blockers):** exact behaviour of every stock-selectability consumer beyond the six confirmed `IStatus == ACTIVE` sites; inventory item-status master edit/restrict semantics; `IvQty.Scale` versus `QTY_DECIMALS` precedence; as-of/replay valuation for this new business context; full QA hold/release contracts and whether `PhysicalHoldEnabled` is on in practice; PO invoice matching and service-classification edge cases; how stock count and period close handle a custody slice; any safe physical production WIP custody mechanism. **A listed path proves a local code mechanism exists — never an end-to-end business approval.**

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

- Create linked service PO, dispatch 600 WIP from a verified operation/lot.
- Partial receipts 350 + 200, 50 remain at vendor.
- Accepted 550 become eligible for the next operation **only via validated production posting/eligibility rules**. Never fabricate output/FG by adding a receipt record.
- No inventory-only warehouse transfer masquerading as a WO WIP handoff.

### C. Inventory-only transformation (architectural capability, **not enabled** in Phases 1–4)

Send `RAW-PLATE` 100 KG and receive `PAINTED-PART` 90 PCS, with 5 KG scrap and vendor service cost. **This is a conversion / material-consumption / valuation event**, not a same-item stock transfer. It needs an actual inventory processing order with input→output genealogy and cost conservation. Preserve as a designed source type or feature gate, but **do not allow Post** until a separately approved conversion + costing implementation exists. UI must say "This processing changes the item or quantity. It requires Inventory Processing setup; posting is not available yet."

### D. Customer-owned material (future, separately gated)

If a subcontractor processes **a customer's** property that is not company-owned, do not put it into company-owned valued `IvBalLoc` or WO costs. Distinct ownership and custody/accounting rules require a separate design approval.

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
  |-- QA link adapter (ONLY the actual existing QA path)
             |
existing Inventory posting, Production posting, Purchase PO/invoice services
```

**Reuse one existing repo project per layer**, not new deployments, microservices, competing posting engines or variant Razor pages per company. An adapter is a thin, source-specific boundary, **not** a new ledger or generic rules engine. Where the repo already has a reusable transaction orchestration pattern, prefer it and delete unnecessary adapters.

**Domain separation:** Subcon owns *commercial job/dispatch/receipt identity, vendor custody trace and reconciliation*; Inventory owns *Inventory quantity, lot and value*; Production owns *WO/WIP quantity, costing and stage eligibility*; Procurement owns *PO/invoice obligations*. Inquiry aggregates these without becoming an alternative posting authority.

---

## 5. Tenant settings, menus and rights

Use existing `AppSettingModules`, `AppSettingCatalogue` and the settings UI. Register a new `SUBCONTRACTING` module (requires `All` + `Describe` + catalogue tests). Proposed keys (final names decided Phase 0):

| Key | Default | Meaning / gate |
|---|---|---|
| `ENABLED` | `false` | Prevent new transactions by default; preserve authorized history/reconciliation |
| `INVENTORY_SOURCE_ENABLED` | `false` | Inventory-only job source permitted after the inventory source safety gate |
| `PRODUCTION_SOURCE_ENABLED` | `false` | WO WIP source permitted only with Production and the WIP posting gate |
| `REQUIRE_SERVICE_PO` | `true` | Posted dispatch requires an eligible service PO unless an approved alternative policy exists |
| `ALLOW_SUPPLIED_MATERIALS` | `false` | Separate material-custody gate; not proof consumption is supported |
| `INVENTORY_CONVERSION_ENABLED` | `false` | Locked false through Phases 0–4; a configured `true` is rejected without an implementation gate |
| `SERVICE_COST_TREATMENT` | `PENDING_REVIEW` | Token: `PENDING_REVIEW` \| `EXPENSE_ONLY` \| `CAPITALIZE`. `CAPITALIZE` is rejected in Phases 0–4. **`AllowedTokens` is mandatory** or the catalogue test fails. |

**Inspection release is NOT a new setting.** v5.1's `REQUIRE_INSPECTION_RELEASE` is **removed**; it would have created a second authority beside the existing key.

- The single inspection authority is `QA.REQUIRE_INSPECTION_ON_RECEIPT` (`ErpWeb.Core/Settings/AppSettingCatalogue.cs:198–204`).
- That key is **record-only today** — its own description says it "stays off and does not block GR or FG posting." Activating it as a real posting gate is a **separate, explicitly approved QA change**, not something Subcon may assume.
- Phase 0 must report its current published policy and the `PhysicalHoldEnabled` value, and record whether either is enforced on receipt, stock-out or sales shipment.
- Subcon must never bypass an **existing** QA physical or logical hold, regardless of any flag value.
- Until the QA gate is genuinely enforced, subcon receipt release cannot claim inspection-based eligibility. The operator-facing text must not imply an inspection guarantee.

**No silent default enable.** Validate cross-setting combinations on admin Save **and** on POST. Company/branch permissions remain authoritative; never rely on menu hiding. Disabling after posting must **not** orphan external balances: keep inquiry and a manager-authorized completion/return/reversal path, prohibit new jobs, require zero outstanding before full disable (or a formally supervised migration).

### UI menu recommendations

- Shared functional menu `Subcontracting` (top-level when Production is not installed). Items: **Overview**, **Send Out**, **Receive Back**, **Jobs / Inquiry**, **Settings** (admin only).
- Optional shortcut in **Inventory > Subcontracting** and **Production > Subcontracting**, both navigating to the **same** pages/services. No duplicate menu records, rights or route implementations.
- PO generation remains in **Procurement > Purchase Order**; expose a `Create Subcon PO` shortcut from the Job, obeying approval.
- No Production menu in an inventory-only tenant. Do not inject WO lookups or WO permissions into inventory-only users.
- Named permissions: `ACCESS`, `ADD`, `EDIT`, `DELETE` (NEW only), `POST`, `ROLLBACK`, `PRINT`, `VIEW_COST`, `APPROVE` (existing code, only if meaningful for manual exceptions). **There is no `APPROVE_EXCEPTION`.** If a distinct exception right is genuinely required, add it to `PermissionCodes.All`, menu rights metadata, seed/sync, tests and server checks under separate approval. Authorize in the core service as well as the page.

---

## 6. Transaction model (provisional: Phase 0 must finalize existing schema reuse)

**Common:**

- `SubconJob`: company, branch, JobNo (via `DocumentNumberingService`), SourceType (`INVENTORY_STOCK` / `WO_OPERATION`), vendor, planned qty/UOM, service PO line reference, due date, lifecycle, external reference, rowversion, audit; source immutable after first post.
- `SubconJobSource` (or fields, **not both**): `INVENTORY_STOCK` requires the original `IvBalLoc`/item-lot identity; `WO_OPERATION` requires WO, route step, operation and WIP lot/source. Source-specific FKs nullable in the common schema, but conditional validation is mandatory.
- `SubconDispatch` + `SubconDispatchLine`: vendor/date/status, source job, base qty, physical lot, original allocation, **subcon link back to the posted or staged inventory TR**, printed delivery note, transport notes; multiple partial issues.
- `SubconReceipt` + `SubconReceiptLine`: dispatch-line reference, original company+branch+item+**physical lot identity**, physically returned, accepted, held, rejected returned, unprocessed returned, QA reference, posting link; partial receipts and separate verified QA release. **Default: original lot required.** A different lot needs its own approved genealogy/identity remap; never substitute silently.
- `SubconCustodyEvent`: immutable per company+branch+source lot+vendor+source TR/production posting+line+effective date, for dispatch/return/reversal. If the selected composition is posted-source-derived (§7.4A), this is a **read projection**, not a second write. Derived supplier balance only; **never** a competing writable `IvBalLoc` or `ProductionBalLot`.
- Optional `SubconMaterialLine`: **only after Phase 2M safety approval**, mapped to a real stock posting with source lot/UOM and cost.

**Unique keys / checks:** company+branch+JobNo; document numbers; source type/link; stable dispatch and receipt operation ids; **no duplicate active source TR linkage**; negative qty forbidden; `return <= posted dispatched − effective prior returns − approved loss`; matching original lot; source-specific FK conditional checks; immutable posted source identity; rowversion; source document genealogy; PO vendor/line identity; cumulative allocated/billable qty `<=` authorized PO commitment on the selected fee basis. Concurrency correctness requires a **DB unique constraint plus an in-transaction recheck**, never a pre-save UI check.

A physical consolidated truck manifest may link separate mode-specific posted documents without pretending cross-module posting is atomic. A single dispatch header may combine jobs **only if** the entire header posts in one verified atomic transaction with correct source locks. **Default v1: do not mix Inventory and Production source modes inside one post header**; print a consolidation manifest instead.

---

## 7. Inventory-only source: posting and rollback contract (Phases 2I/3I)

### 7.0 MANDATORY custody-isolation decision — all naive mechanisms currently NO-GO

Ordinary Inventory TR **preserves `IStatus`** from source to destination (`IvStockTransferService.cs:812–829`, `:966–991`). A status created as `IvStatus` master data therefore cannot be used by that TR to convert dispatched `ACTIVE` stock into a blocked status. `WarehouseType` has no verified eligibility enforcement, and an active vendor warehouse is fully exposed via shared lookups (`IvInventoryLookupService.cs:473–491`, `SaDoService.cs:2364–2366`). **Neither candidate may be called safe until every required consumer is checked.**

Phase 0 must produce a PASS/FAIL table covering **five** candidates:

| Candidate | Mechanism | Current verdict | Why |
|---|---|---|---|
| A | Warehouse/location-only isolation | **FAIL / unproven** | No vendor flag on `IvWarehouse`; `WarehouseType` is never checked; all active warehouses are selectable and sale-eligible. |
| B | Existing-`IvStatus` isolation via current TR | **FAIL** | `:966–991` copies the source status; dispatch cannot create a non-`ACTIVE` destination. |
| C | Explicit service-level source-stock gating in every applicable POST service | **UNDESIGNED / test required** | Would need a proven fail-closed guard in each writer (sales, issue, transfer, count, allocation, production), not just picker filtering. |
| D | QA physical hold as custody | **FAIL for custody / PARTIAL for stock-out** | Holds live in a separate table and never change `IStatus`; `IvSpShipmentService` does not consult them (`:889` checks status only). Blocks inventory stock-out only. |
| E | Inventory mode disabled | **PASS (safe but delivers nothing)** | No custody exposure; see the §18 fallback decision. |

For each row record: exact method/line, sales/production/issue/count/valuation impact, bypass and direct-API test, cost/lot-replay impact, schema change, permission impact and rollback. **A candidate can be chosen only with a proven ordinary-user AND back-end fail-closed non-selectability guarantee.**

1. Protect supplier custody slices from SO/DO/Invoice fulfilment, picking and reservation, direct shipment POST, misc issue, stock transfer, stock-count variance and physical adjustment, inventory allocation, DR fulfilment, production material issue/allocation and FIFO source selection. **Picker filtering is insufficient without independent server-side locked POST guards.** Document whether offsite stock belongs in financial valuation, on-hand inquiry and the stock-count snapshot; preserve owned-inventory valuation even while it is not sellable.
2. If status identity is part of the chosen contract, protect that status from CRUD/deactivation/reclassification, forbid manual assign/remove through normal transfers or adjustments, and prove every stock writer and reader honours it. Do **not** treat `IvStatus` as a magic reserved row, and do **not** assume ordinary TR changes status. An in-transaction status conversion is a **new posting mechanic** and needs separate authorization if it touches inventory writers, valuation or replay.
3. Vendor must be traceable even when several vendors share one physical offsite warehouse: unambiguous vendor/location mapping, branch-scoped uniqueness, authorized retrieval, no cross-vendor selection.
4. A post-disable company still needs an authorized, tested return/reconciliation channel **without** making custody stock generally sellable. A returned held batch remains non-eligible until explicit release under the §5 QA rule.
5. **Decision state at this review:** A = FAIL, B = FAIL, C = UNDESIGNED, D = FAIL for custody, E = safe but empty. **Phase 2I = NO-GO** until Phase 0 evidence plus separate approval.

### 7.1 Safe same-item custody

- **Candidate only:** existing Inventory Stock Transfer, representing **company-owned offsite vendor custody** (not supplier-owned property). No purchase receipt, no Misc Issue + Misc Receipt, no consumption, no output SKU change.
- Phase 0 must choose, document and test the exact mapping — supplier-dedicated warehouse, or company offsite warehouse with supplier-specific location — including branch, lot, stock-status and company isolation. **Never assume ordinary warehouse transfers isolate stock from Sales.**
- Verify **all** availability consumers: sales SO/DO/Invoice/packing, inventory issue/transfer/count, soft reservations, stock valuation and inquiry, supplier/vendor returns, lot genealogy and future-as-of availability.
- If existing filters cannot exclude vendor custody without modifying core costing/stock engines, **STOP** and request a separate safety design. Never deploy an offsite warehouse that sales can ship from.
- Only physically accepted stock returns to an eligible factory slice. Physically returned **held** stock must land in a proven non-sellable slice; per §7.0D that mechanism does **not** exist today, so treat held-return isolation as part of the same unresolved decision — not a QA afterthought.
- Qty/lot/valuation conserved across dispatch/receipt/reversal per base UOM, source item, physical lot and cost method, with FIFO `CostLayer`, WAC, Standard, transfer and backdated replay evidence. Vendor loss/scrap/consumption is **blocked** until its own physical + cost posting is approved. Reconciliation must use sealed posting/stock slices, never Subcon events alone.

### 7.2 Lifecycle

1. Create or link the service PO; ensure correct supplier and service item.
2. New Subcon Job from PO or Inventory Item. Stock lookup (item → location/lot with available qty). Default quantity, PO, vendor and dates. Save `NEW`.
3. `Send Out`: explicit POST creates **one** stock movement and one linked custody fact in a **proven atomic boundary** (§7.4). Supplier balance increments; local usable stock decreases; total company-owned stock value is conserved.
4. `Receive Back`: enter qty, inspection result and source issue. Explicit POST returns physical qty and adjusts vendor custody; accepted/held land in appropriate eligible/ineligible slices. No manufacturing output and no duplicate goods receipt.
5. `Rollback` of dispatch in 2I and of receipt in 3I must be rejected where a later receipt, reservation, sale, warehouse movement, count snapshot, hold/release, PO/invoice/cost dependency, closed period, future transaction or backdated replay invalidates reversal. Keep existing TR `POSTED→NEW` **only if** the real rollback completes atomically; no independent compensation. **Never reverse a dispatch while any return remains posted.**

### 7.3 Items changing SKU/UOM or yield

Stock transfer has **same item identity**, so it cannot serve input SKU X → output SKU Y, many-to-one, one-to-many, weight-to-pieces conversion, process loss, material consumption or service-cost capitalization. **Block transaction Post** until the separately approved Inventory Processing / Conversion feature exists. Do not misuse stock adjustment + receipt to fake conversion; that breaks genealogy and cost.

### 7.4 Atomic-composition vehicle — explicit selection and proof

**What exists:** `IIvStockTransferService.PostAsync` → `_posting.PostAsync(TR)`; `IvInventoryPostingService.PostInventoryTRAsync` owns its own DB transaction (`:2979`). `BeginPostingInTransactionAsync` + `InventoryStockPostingCommandFactory` only builds a `StockPostingCommand` — no physical mutation, no history, no lifecycle. Calling it from Subcon and then separately calling `PostAsync(TR)` is **unsafe**.

Phase 0 must choose exactly **one** vehicle, with tests:

**A — Preferred if proven: derived custody.** The dispatch/receipt document is an immutable, uniquely linked inventory TR; subcon posting status and custody are **derived only** from the sealed posted TR/history. There is no second committed subcon state to update after the TR posts, so the existing TR transaction is the whole authority.

- **Enforcement point (new in v5.2, previously unstated):** vehicle A is only safe with a persisted `SubconJobId`/link on the TR batch **and** guards in `IvStockTransferService.SaveNewAsync`, `UpdateAsync` and `PostAsync`, plus a block that prevents editing or reposting a linked batch from the ordinary **Inventory > Stock Transfer** screen. Without those, the operator can post or mutate the linked TR outside subcon authorization and the "derived" state silently diverges.
- Classify that work correctly: it is a **validation guard only**. It must not alter valuation, `CostLayer`, `IvTrxHistory` semantics, replay or month-end. If the guard cannot be added without touching those, vehicle A is **not** free and the decision must say so.
- Capture rollback as derived from the real TR reversal, with immutable keys and reconciliation after retries.

**B — Only with separate review:** an explicit in-transaction physical TR posting/reversal member that **reuses** existing transfer mutation, validation, locking, valuation, QA and history functions while sharing the caller's transaction, plus the subcon document/event update. It must be a minimal extraction of existing exact posting behaviour — not a parallel transfer engine — and needs owner approval because posting infrastructure is costing-adjacent.

**C — Investigate, do not presume:** coordinator composition using `BeginPostingInTransactionAsync` / `InventoryStockPostingCommandFactory`. Passes only if source + SQL Server integration tests prove the complete physical transfer **and** TR history/status occur in the same transaction as subcon facts, including abort, seal and rollback. A ledger posting alone is insufficient.

**Forbidden:** sequential `TR.PostAsync` then subcon update with manual compensation; an outbox that claims atomic stock movement without idempotent authoritative state; direct `IvBalLoc` writes; any partial commit after failure. Each vehicle needs lock order, source version, idempotency fingerprint, replay/closed-period behaviour, QA hold handling, non-negative stock and test-only failure injection **before** Phase 2I is authorized.

---

## 8. Production source: WIP operation contract (Phases 2P/3P)

- The job links to an actual `ProductionWorkOrderOperation` with `ProcessType=SUBCONTRACT` (or a proven routing designation) plus WO/route/lot. That value is a **duration/operation classification today**, not outsourced physical stock. Do not add duplicate route flags without proven need.
- Derived *eligible dispatch quantity* must be a consistent, locked view of posted predecessor output/available `ProductionBalLot` **minus** already dispatched/used/held quantity, with UOM conversion. Source WIP qty/cost cannot be duplicated in subcon tables. `ProductionLocation` is an existing WIP location table, **not** a supplier-custody subsystem; assess explicitly rather than assuming an offsite `ProductionLocation` blocks internal consumption.
- Before dispatch, trace every existing production execution service that could consume the same WIP: Daily Production, internal WO transfer, WIP material handoff/allocation, FG receipt/completion, material issue/return and their rollbacks. `ProductionWorkOrderTransferService` is **full-balance WO→WO** and its posting (`:936–1027`: branch lock at `:952`, ordered WO execution locks at `:974`) validates WIP and valuation — it is **not** a subcontract partial-dispatch shortcut.
- Required lock order: branch stock lock → affected WO execution locks (sorted UID) → document/post link/route/operation graph/lot locks, in the existing compatible order, revalidating before mutation. Follow `ProductionOutputService.Posting.cs:27–100` as **evidence of a pattern**, not as universal proof for subcon.
- **Updated expectation (v5.2):** with the WO→WO transfer demoted, this branch has **no candidate mechanism at all** for partial vendor WIP custody. Phase 2P is therefore labelled **expected NO-GO pending a new, separately approved design**. Phase 0 must still document the evidence and the exact missing capability, but it should not be read as an investigation likely to end in a safe reuse.
- A supplier physical receipt is **not** Daily Production `GoodQty`, a new `ProductionBalLot`, a material issue, an FG receipt or an operation completion. Phase 0 decides whether existing output posting can represent outsourced completion without cost misstatement; if not, hold accepted qty and block the next operation.
- Reuse and inspect `ProductionOperationEligibilityService`, `ProductionOperationSequenceGate`, `ProductionWorkOrderExecutionLock`, `BranchStockTransactionLock`, the QA stock-out claim and actual release policies. Block parallel-sequence violations and non-100% yield. Do not copy WIP availability or cost without current `ProductionPoolValuationService` and contribution-availability consistency checks. Unreleased/held vendor WIP cannot advance operations even if physically returned.
- Phase 2P must include safe dispatch **and** rollback; Phase 3P safe receipt, QA release and rollback; all downstream edge and closed-period checks mandatory.
- The subcon fee cannot silently alter WIP costing; show commercial spend separately. If the fee must be capitalized into WIP/FG, prohibit claiming complete cost or completing a cost-sensitive WO under an unapproved policy.

---

## 9. Company-supplied materials (separate 2M gate)

Optional for either source; a normal dispatch does **not** imply consumption. Require the original company-owned stock source and lot.

- **Raw materials still in Inventory:** move by existing inventory transfer into verified supplier custody; consume/return only through approved existing movement and valuation semantics.
- **Materials already issued to WO/Production:** do **not** issue from Inventory a second time. Reuse actual production-material custody/movement provenance; otherwise block.
- **Vendor's own materials:** not our inventory, only a service/vendor commercial charge.
- **Materials consumed to create a different output:** a valuation/conversion event, not a balance-only note. Disable if no costing-safe posting API exists.

For each company+vendor+job+item+lot+base UOM: `OutstandingVendorMaterial = postedTransferIn − postedReturn − postedApprovedConsumption − postedApprovedLoss`, each change linked to a real stock/WIP posting. A note-only "used 20" must never make inventory disappear. Under Phases 0–4 allow physical custody and return only when provably safe; consumption, loss and costing corrections stay out of scope unless an existing fully tested service supports them unchanged.

---

## 10. Commercial PO / AP / e-invoice and pricing

- One service PO may cover one or several Subcon Jobs **only if** each quantity allocation is traceable and not overcommitted; partial dispatch/receipt needs line-level matching. A job may be created from a PO or generate/link a PO from the job. **No duplicate PO on retry.**
- Use existing service classification (`IType = SERVICE`, `PoOrderCalc.IsServiceIType`) and PO workflows. Verify whether an indirect item can also represent a processing service. Do not hijack a stock-item GRN to post WIP.
- `BillableQty` is an explicitly configured commercial basis (accepted PCS, physically processed PCS, batch fee, kg, …), **not** blindly the returned quantity. Overbilling, reject/rework charges, tax, discounts, currency and invoice-before-return policy require validated rules.
- Preserve existing procurement approval, invoice matching, SST/e-invoice, self-billed rules, supplier identity, audit and period close. Do not make tax assumptions for a household operator or individual.
- Service PO commitment, invoice expense and capitalized stock/WIP cost are **three different metrics**. Financial treatment requires an accounting decision and a test; no hidden capitalization in Phases 0–4.

---

## 10A. Mandatory CURRENT-repo conventions and explicit non-change list

- **Numbering:** use `DocumentNumberingService` / `IDocumentNumberingService` and current number series (`plans/document_numbering_spec.md`). Check branch+company uniqueness, date-series concurrency and rollback; never generate `MAX+1` and never treat a client-supplied number as authority.
- **Lifecycle and links:** align inventory `NEW/POSTED/CANCELLED` and production Draft/Succeeded posting links with each source mode's actual persisted rules — stable `PostingRequestId`, `DocumentRevision`, operation/fingerprint, soft-archived drafts. Do not invent one universal lifecycle that erases the difference between inventory batches and production documents.
- **Audit/history:** reuse `IvInventoryHistoryWriter`, `IvTrxHistory`, posting generation/reversal evidence and read-only `Traceability/LotGenealogyService`. Audit user, company/branch, changes and reason without creating another stock/WIP cost writer. Add Subcon history only where it is not derivable from immutable source evidence.
- **Schema:** the repo uses explicit SQL scripts under `scripts/`, EF entity/configuration and runtime readiness checks — **there is no EF migrations folder**. Verify the actual conventions and the supported SQL Server version. Implement only after the signed Phase 0 decision: idempotent create/backfill/unique indexes, non-destructive upgrade, and a module-OFF startup regression.
- **UI:** inspect the live `ErpWeb.UI/Inventory/Transactions/` pages (`IvMiscReceipt.razor[.cs]`, stock transfer), PO and Work Order transaction pages, and follow their real toolbar, leftmost row actions, Save/Cancel/Post/Rollback, lookups, responsive widths, error and permission rules. The mandatory standards are **git-tracked at HEAD**:
  - `.cursor/skills/productionv2-costing/SKILL.md` (v1.1) — mandatory procedure, incl. the stock-balance reference.
  - `.cursor/skills/erp-transaction-ui-standard/skill.md`
  - `.cursor/skills/inventory-transaction-ui/skill.md`
  - `.cursor/skills/production-transaction-ui/skill.md`
  - `.cursor/skills/erp-entry-footer-standard/skill.md`
  - `.cursor/skills/erp-grid-row-actions-left/skill.md`
  - `.cursor/skills/erp-inline-search-standard/skill.md`
  - `.cursor/skills/erp-large-data-smart-lookup-standard/skill.md`

  Note the lowercase `skill.md`; a `SKILL.md` probe 404s because of case. Do **not** report these as missing.
- **Tests:** place proofs in `ErpWeb.Tests` by area (`Admin`, `Inventory`, `Procurement`, `Production`, `Quality`, `Sales`, `Transactions`, `UI`). The settings host is `ErpWeb.Tests/Infrastructure/TestHosts/AppSettingTestHost.cs`; `ErpWeb.Tests/Settings` does not exist. Follow the current SQL Server integration pattern — a SQLite pass does not prove SQL Server lock semantics.
- **MUST NOT silently alter:** `IvInventoryPostingService` physical valuation, FIFO/`CostLayer`, WAC/Standard, `StockLedger` replay, `ProductionBalLot` cost math, `ProductionPoolValuationService`, month-end or period guards, or historical movement semantics. Any mandatory physical mutation seam must be designed and reviewed separately and cannot be smuggled in as a harmless adapter.

---

## 11. Single fast and friendly UI

All screens live in **Subcontracting**, with an adaptive source section.

**Jobs / Overview:** search supplier, PO, item, WO (only when the source allows), status, due date; recent jobs, open/overdue and remaining qty; quick `Send Out`, `Receive Back`, `Print`.

**New Job:** start from PO (common), from stock balance (Inventory mode), or from a WO operation (Production mode). Lookup for supplier/PO/item/lot; auto-fill quantity/UOM/rate/due date. Hide fields that do not apply. No typing raw IDs, WO codes or supplier codes.

**Send Out:** show selected job, available eligible qty, outstanding PO processing qty, supplier, optional lot/material lines, dispatch quantity and date. Default quantities safely; **Save and Post are deliberately separate**. Print the dispatch/delivery note. Bulk rows on one screen by supplier, only if atomic.

**Receive Back:** scan/search supplier delivery note/job/dispatch. Show outstanding qty prefilled; enter physical returned, accepted, hold/rejected, unresolved remaining; optional receipt images and operator notes. One clear `Save`, `Post`, `Rollback`; `Accept/Release` only under the §5 QA rule.

**UI principles:** follow the actual Misc Receipt and PO transaction style, leftmost grid actions, compact aligned input widths, responsive tablet and desktop, keyboard-friendly repetitive entry, clear status/remaining. Use plain messages such as `You sent 600 PCS; 250 PCS are still with ABC Polishing`. For a blocked conversion: `Different output item requires Inventory Processing approval; no stock movement was posted.` No technical FIFO/replay exceptions to the user.

**A company with Production OFF** must not show empty WO filters, `Select Work Order` blockers or Production menus; direct URL and backend requests must still be forbidden when the Production source is disabled. A company with **BOTH** gets a non-intrusive source badge and contextual launch shortcuts — not duplicate entry pages.

---

## 12. Reconciliation, reports and KPIs

Read-only, source-authoritative inquiry; every figure drillable to the posted movement, PO, receipt and lot/operation. Filters: company+branch (authorized), vendor, job, item, WO (conditional), PO, source mode, lot, due date and as-of date.

Metrics: open jobs, qty with subcontractor, overdue qty/aging, part-returned jobs, on-time complete dispatches, supplier average lead time, accepted/returned ratio, rejection %, approved loss, material balance, service PO commitment, invoice spend, cost-capitalization status. Segment Inventory-only versus Production; combine totals **only** when unit basis and accounting meaning match. Do not sum mixed PCS/KG. Show `—` for invalid denominators and separate order-weighted from quantity-weighted on-time measures.

Print: Dispatch Note, Receipt Acknowledgement, Outstanding Job List, Vendor Balance Statement, Supplier Performance and Source-to-Cost Reconciliation. As-of historical balances derive from posted/reversed events plus underlying stock/WIP history — never a current snapshot projected backwards.

---

## 13. Quantities, state transitions, atomicity and date policies

**Custody (base UOM and precision per source item + physical lot + vendor + company + branch):**

```
NetDispatched          = Σ posted dispatch − Σ effective posted reversals
PhysicallyReturned     = Σ posted physical return − Σ effective return reversals
ResolvedExternalLoss   = Σ loss actually posted via authorized quantity/value write-off
OutstandingAtVendor    = NetDispatched − PhysicallyReturned − ResolvedExternalLoss >= 0
```

**Precision (v5.2):** the branch has **two potential authorities** — `IvQty.Scale = 4` as a constant (`IvTrxConstants.cs:33–38`) and `INVENTORY.QTY_DECIMALS` as a setting (`AppSettingCatalogue.cs:31–36`). Phase 0 must determine which governs each writer and reader, and must not hardcode either assumption into subcon tables or documents. If the setting is authoritative anywhere, subcon must resolve it the same way.

Before accepting any POST, reject over-return in base UOM using **locked cumulative** dispatch/return evidence, plus mismatched lot/vendor/branch/source TR, unauthorized cross-vendor transfers, unapproved external loss and source-status deactivation. **Blocked by design for this release**, each with an explicit operator message and a test: vendor→vendor onward processing, vendor-side scrap/loss, return to a different lot without approved genealogy, and UOM-changing conversion.

Do not count hold/rejected returned twice. Accepted + held + rejected returned + unprocessed returned must equal the physical return, unless a documented QA-later state permits `pending classification`. Supplier-side scrap not physically returned is **not** a physical receipt; a costed loss requires its own approved posting.

**Invariants.** Inventory: `factory eligible + vendor offsite + held/non-eligible + approved consumed/loss = source total adjusted for other authorized movements` at the relevant effective date and cost method, with no unexplained value drift. Production: outside WIP cannot also be consumed by an internal operation; accepted next-step quantity `<=` the once-only posted accepted quantity; a partial receipt cannot create duplicate input for the same operation.

**States:** Job `OPEN`, `PARTIALLY_DISPATCHED`, `OUTSIDE`, `PARTIALLY_RETURNED`, `ON_HOLD`, `CLOSED`, `CANCELLED` as derived where possible; document `NEW`, `POSTED`, `ROLLED_BACK/REVERSED` per the proven existing pattern. Never delete posted history or flip status manually. Mode/site/lot editing is prohibited once posted.

**Atomic posting:** if subcon status/events are physically persisted, the inventory/production movement + status + posting key + custody fact must commit in one proven DB transaction. If derived (vehicle A), the committed inventory/production source posting is the **only** POST authority and the secondary status is computed — never asynchronously written as if atomic. Every mode must name and test its vehicle (§7.4). Calling two independent `PostAsync` services sequentially and compensating afterwards is **not** an approved substitute. Required: exact lock ordering, unique source-activity keys, request idempotency, branch and company verification, retry semantics, concurrency-exception mapping.

**Posting/rollback:** the same transaction rechecks period close, future date, stock as-of, later dependency/consumption/QA/sale/invoice and work-order status. Saving a future-dated `NEW` may follow existing policy; POST/ROLLBACK must obey current business-date and replay controls. Backdated posting that requires unsupported stock replay must stop without partial commit.

**Commercial guard:** validate per PO line cumulative allocated/billable quantity and invoices/credits against the authorized fee basis; never claim `BillableQty == accepted qty` for lump-sum or kg-rated services. PO edit/close/cancel, vendor change, currency/tax/approval state and invoice-before-receipt must revalidate at the transaction boundary. No service-fee capitalization without separate approval.

**Testing cost freeze:** the actual code diff must not edit FIFO, `CostLayer`, valuation, WIP cost formulas, replay or the month-end engine in Phases 0–4. Tests record cost/lot movement before and after for all stock and WIP integrations, including Standard/WAC/FIFO where configured. Unexplained effects are a blocker even when no costing file changed.

---

## 14. Grok 4.6 implementation gates (one stop per phase; only Phase 0 approved)

### Phase 0 — BRANCH AUDIT ONLY, NOW AUTHORIZED

At the live HEAD, produce two artifacts at explicitly authorized paths: `plans/subcon_dual_mode_phase0_evidence.md` (exact path/method/line/current behaviour) and `plans/subcon_dual_mode_phase0_decision_record.md` (one-page PASS/FAIL). Do not claim evidence that was not actually obtained. The decision record must identify: (a) custody isolation per §7.0 candidates A–E, (b) the §7.4 atomic vehicle including the vehicle-A enforcement point, (c) WIP integration and global lock order, (d) the QA acceptance/hold path and whether `QA.REQUIRE_INSPECTION_ON_RECEIPT` or `PhysicalHoldEnabled` is actually enforced anywhere, (e) per-subphase GO/NO-GO, (f) current HEAD SHA, SQL Server version and tests run, and (g) owner sign-off.

Mandatory investigation:

1. Inventory transfer source balance and valuation/lot replay; vendor location/status; stock eligibility across SO/DO/Invoice, picking, stock counts, reservations and warehouse queries.
2. Production WO `SUBCONTRACT` behaviour, `ProductionBalLot`, Daily Production, handoff, QA and operation/FG dependencies; confirm whether vendor custody can be blocked for internal production consumers. Record the **absence** of a partial-custody mechanism explicitly.
3. PO SERVICE item and invoice matching; the finance requirement for capitalization versus expense-only.
4. AppSetting catalogue/register/scope; the `QA.REQUIRE_INSPECTION_ON_RECEIPT` and `PhysicalHoldEnabled` live values; company-scoped named menu authorization.
5. Unified schema proposal, conditional FKs, concurrency lock order, transaction composition, rollback and month-end.
6. Existing UI source for Misc Receipt, Stock Transfer, PO, Work Order and QA; a reference screenshot or wireframe.
7. Exact inventory-only non-Production fixture and production-integrated fixture, with source/qty/cost trace before and after.
8. All unsupported cases and how they are disabled; a clear statement of which of 2I / 2P / 2M / 3I / 3P are safe.
9. Quantity-precision authority (`IvQty.Scale` vs `INVENTORY.QTY_DECIMALS`).
10. Whether a **test-only fault-injection seam** exists for the §15.20 failure test. It does not exist today; if needed, scope it explicitly as a test-only hook and note it is a code change in a costing-adjacent path.

**STOP.** No committed application code, DDL or production scripts in Phase 0. Disposable/test-database experiments are allowed only with explicitly disposable schema and recorded evidence — no production data, no live tenant writes. **Inconclusive = FAIL/NO-GO.**

### Phase 1A — METADATA / READ-ONLY UX (NOT YET APPROVED)

Register module/settings/permissions/menu metadata (no new inspection flag), disabled-by-default routing, source-context read-only pages, supplier/PO reference lookups from existing sources, and operator guidance. **No** new persisted subcon job link, dispatch or receipt; no DB schema; no posting. Any PO "link" at this stage is display-only and unpersisted. Validate settings catalogue tests, module-OFF and Production-OFF tenant startup, and that no access or dispatch is accidentally enabled.

### Phase 1B — APPROVED DATA FOUNDATION / NEW DRAFTS ONLY (SEPARATE GATE)

Only after the Phase 0 record plus schema sign-off. Add minimal common subcon job/source/dispatch/receipt tables, conditional FKs and unique keys, `DocumentNumberingService` integration, the PO service-line persisted link and immutable/staged TR reference, plus the vehicle-A link column if that vehicle is selected. Rowversion/audit and operator draft entry only: **no POST, no stock mutation, no vendor-balance inflation.** SQL scripts repeatable, controlled backfill, readiness checks present. Test both tenant modes. Skip any schema or service link with no verified use.

### Phase 2I — INVENTORY DISPATCH + ROLLBACK (GATED, CURRENTLY NO-GO)

Only same-SKU/base-UOM qty-preserving TR at an approved vendor custody slice, after §7.0 PASS and §7.4 vehicle PASS. Status-preserving transfer is **not** a quarantine-status conversion. Exclusion from **all** ordinary stock writers, finance stock-valuation conservation and source identity must be proven. Every POST has tested rollback, retry, duplicate, concurrency, future/closed-period checks. Test independently of Production.

### Phase 2P — PRODUCTION WIP DISPATCH + ROLLBACK (SEPARATE GATE, **EXPECTED NO-GO**)

Only if WO WIP availability and lock integration can be proven **without** touching cost calculation. This branch has no partial-custody WIP mechanism; a reuse of `ProductionWorkOrderTransferService` is rejected. Keep disabled until a new design is separately approved. A pass of Phase 2I is **not** a Phase 2P approval.

### Phase 2M — OPTIONAL MATERIAL CUSTODY (SEPARATE GATE)

May be postponed. Stock owned/lot/source trace, no double issue or implicit consumption, posting + rollback validated. Do not slip into 2I/2P implementation.

### Phase 3I — INVENTORY RETURN + ROLLBACK (GATED, CURRENTLY NO-GO)

Partial physical returns to the **same original physical lot** unless separately approved; accepted/held identity supported by real movement controls — **not** by a QA flag. **Dependency (v5.2):** 3I inherits the 2I isolation decision. Because QA physical holds never change `IStatus` and `IvSpShipmentService:889` checks status only, there is currently **no** independent mechanism that keeps a held return from being sold. If 2I's mechanism is rejected, 3I cannot satisfy "only accepted stock is sale-eligible" and must not be approved on the assumption that QA release covers it. Includes the post-disable authorized return path. Reverse only when there are no subsequent sales, counts, movements, released/held stock or invoice/cost dependencies.

### Phase 3P — PRODUCTION WIP RETURN + ROLLBACK (SEPARATE GATE, CURRENTLY NO-GO)

Partial WIP receipt, QA, actual WO operation/next-step eligibility, no double production output, no cost modification, rollback dependency checks. Keep disabled until end-to-end source proof exists.

### Phase 4 — INQUIRY, KPI, PRINTING, UAT (GATED)

Mode-aware inquiry, supplier statements, as-of reconciliation, source-linked drilldowns, usable tablet and desktop screens, role and regression tests. No false mixed-unit totals; no cost-completeness claims.

### Phase 5 (NOT ASSIGNED) — INVENTORY CONVERSION, CONSUMPTION + CAPITALIZATION

If an inventory-only customer needs raw SKU→processed SKU, yield/scrap, extra materials consumed, service fee added to item/WIP/FG, or customer-owned stock, prepare and approve a separate accounting + posting design before implementation. A same-SKU-only v1 must never claim complete conversion functionality.

---

## 15. Mandatory end-to-end tests, negative controls and numerical evidence

1. Inventory-only company with Production flag OFF: service PO + Subcon Job → dispatch 600 → receive 350 + 240 → outstanding 10; stock at vendor/factory reconciles by lot; no WO read/write.
2. Inventory-only stock 100, two users dispatch 70: exactly one can consume the last eligible stock; no overdispatch; no sale of vendor-held stock.
3. Dispatch 600; receipt 300 accepted + 20 on hold + 10 rejected returned, physically returned 330, vendor outstanding 270; only the accepted 300 is sale-eligible.
4. Qty/cost before and after dispatch/return: no unexplained valuation change; cost-layer provenance preserved per existing transfer/replay behaviour (FIFO/WAC/Standard scenarios).
5. Duplicate POST after a timeout, and repeated rollback: exactly one movement/event, consistent response.
6. Partial receipt then attempted parent dispatch rollback: blocked, or a safe ordered reversal with complete dependency checks.
7. Closed period, future-dated post, backdated transaction, future-as-of stock: same existing policy, no partial commits.
8. Service PO edit/cancel and invoice accrual while the job is open: no duplicate inventory GRN, no over-invoice, no unapproved capitalization.
9. Production WO 1000 WIP (only if independently enabled): dispatch 600 + 400; partial returns; the next operation cannot consume vendor-held or held WIP; Good/hold/FG balances reconcile; no duplicate WO output.
10. Multiple jobs per supplier, multiple branches and companies: batch/manifest trace accurate; no cross-tenant access.
11. Vendor-supplied raw materials 100 (if 2M approved): 65 consumed only via an approved posting, 35 returned; material already consumed for a WO cannot be consumed again; with no approved service the transaction fails clearly.
12. SKU transformation `RAW-A` → `COATED-B` attempted in Phases 0–4: posting blocked with a meaningful message; no adjustment or receipt created.
13. Disable/enable with an outstanding supplier balance: no new dispatch after disable; authorized receipt/reconciliation still possible; no orphaned stock.
14. Stock count, SO/DO/Invoice, stock alerts, reservation, valuation report, cost inquiry, lot genealogy and period close not broken by the offsite location.
15. CRUD permissions by named action, `VIEW_COST` protection, hidden Production controls, direct HTTP URL/API denial, audit.
16. Keyboard/tab order, 768/1024/1440 widths, left row actions, Save/Cancel/Post/Rollback, lookups, simple actionable errors, daily bulk processing and printing.
17. **Non-selectability matrix** for `ACTIVE` vendor-offsite and held stock, at both UI and locked POST: SO, DO, picking, SP/stock shipment FIFO, DR, reservation/issue/transfer, material issue/allocation, stock-count variance, production consumption and direct API. Every distinct writer and user bypass must actually fail.
18. **QA hold:** an unaccepted vendor return cannot be sold or consumed; the record-only `QA.REQUIRE_INSPECTION_ON_RECEIPT` flag alone cannot release anything; a physical stock-out claim failure blocks dispatch atomically; QA release and return-rollback dependency; item-status master disable/edit; malicious source-status transfer.
19. **Lot and partial identity:** dispatch 600 of lot L1, return 350 then 240 of L1 leaving 10; reject a 601st return; reject a return to L2; multi-vendor leakage; vendor-to-vendor; unsupported loss/rework; UOM-changing transformation — with no stock or cost drift.
20. **Concurrency/atomicity (SQL Server, two sessions):** stock 100, two concurrent dispatches of 70 → only one POST; the same document posted twice with different IDs/fingerprints → exactly one; inject a failure after the physical TR write and before seal/commit → all-or-nothing. **Note:** no fault-injection seam exists today; Phase 0 must scope a test-only hook before this test can be written. Also cover update-versus-post, rollback-versus-receipt/post, ordered branch/WO locks, deadlock handling and retries.
21. **Numbering and scripts:** parallel `DocumentNumberingService` requests; rollback/retry and idempotent series; run each SQL script twice, preserving pre-existing data and constraints; readiness when the module is OFF and Production is disabled; invalid setting/default rejection; direct URL and API POST denial.
22. **Period/count/replay:** close an inventory period while vendor stock remains; a stock-count snapshot including the custody slice; post backdated before a future vendor return and attempt a reversal across count/sale; confirm dates-as-of inquiry reproduces posted history rather than the current balance; inspect WAC/FIFO/Standard value evidence.
23. **Precision:** prove the resolved authority (`IvQty.Scale` vs `INVENTORY.QTY_DECIMALS`) is applied consistently at save, post, return and reconciliation, including a value that differs at 2 vs 4 decimals.
24. **Production (only if independently enabled):** partial outside WIP cannot be reused by Daily Production, WO transfer, FG, stage handoff or QA release; sequential/parallel operation gates and ordered WO locks; no `ProductionBalLot` shadow or revaluation; failure injection with source/target pool valuations verified.
25. **Commercial matching:** PO service vendor/line change, a billable basis differing from physical qty, partial invoicing and credits, over-allocation and over-billing, PO approval/cancel, taxes, company/branch isolation, and no automatically capitalized conversion fee.

Each gate delivers the exact SHA, commands actually run, pass/fail counts, SQL Server evidence with starting and ending `Qty`, `BaseQty`, WIP, `CostLayer`/value, changed files, and a diff-proven `COSTING FILES MODIFIED: NONE`. A file unchanged does **not** prove zero indirect costing impact.

---

## 16. Absolute MUST NOT

- DO NOT require `WorkOrderId`, Production installation, WO menu, BOM or route step for Inventory-source jobs.
- DO NOT create separate custom projects/pages/services for Inventory-only versus Production; one common module with thin source-aware integration.
- DO NOT convert item X to item Y via Inventory Transfer, Misc Issue + Receipt, stock adjustment or fake GRN without approved conversion/valuation accounting.
- DO NOT double-count WO WIP or company stock; no second editable stock/WIP balance ledger.
- DO NOT use warehouse-only custody without source-level Sales/Inventory/Production POST exclusions, or status-only custody while the existing TR preserves source status.
- DO NOT create, delete or deactivate a "reserved custody status" as ordinary master data.
- DO NOT rely on lookups alone to exclude vendor-held stock: direct API writes and posted source selection must fail closed under locks.
- DO NOT treat a QA physical or logical hold as a vendor-custody mechanism; holds do not change `IStatus` and are not consulted by sales shipment.
- DO NOT introduce a second inspection-release setting beside `QA.REQUIRE_INSPECTION_ON_RECEIPT`.
- DO NOT let vendor-held stock be pickable by sales or usable by internal production.
- DO NOT call two independent stock/custody posts sequentially then compensate, assume V2 `BeginPosting` executes physical TR, or create a second ledger.
- DO NOT alter FIFO, `CostLayer`, stock/WIP valuation formulas, existing replay and month-end engines in Phases 0–4. Any proposed infrastructure seam needs separate owner review and written scope.
- DO NOT automatically capitalize a service PO/invoice amount, omit a required cost silently, or claim finished-goods margin/cost complete.
- DO NOT start Phase 1A/1B from this document without the Phase 0 evidence **and** decision record. Do not mark 10/10 only because the design is comprehensive.
- DO NOT downgrade tenant/company/branch permission enforcement, atomicity, QA holds, rollback safety or audit for UI convenience.

---

## 17. Paste-ready Cursor / Grok 4.6 handoff

> Inspect the CURRENT `mokth/net10projectTemplate` **productionv2** branch and record the HEAD SHA. Read this v5.2 plan from its actual checked-in path or the explicitly supplied workspace copy. **Execute Phase 0 ONLY.** We require ONE independent Subcontracting module supporting Inventory-only customers without Production AND Work Order-integrated customers, with a shared daily UI and source-specific posting adapters. Inventory mode must not require WO, BOM, routing, Production permissions or a production menu. Production WIP must not be represented as ordinary Inventory stock. Study the actual current Inventory Transfer, Sales availability, ProductionBalLot/Daily Production, PO service item/invoice, settings, permissions and QA hold services. Return `plans/subcon_dual_mode_phase0_evidence.md` plus `plans/subcon_dual_mode_phase0_decision_record.md` with PASS/FAIL for custody candidates A–E (warehouse-only, existing-status, service-level gating, QA hold, disabled), a selected fully atomic vehicle including the vehicle-A link/guard enforcement point, branch/WO lock and WIP maps, the live values of `QA.REQUIRE_INSPECTION_ON_RECEIPT` and `PhysicalHoldEnabled`, the quantity-precision authority, the fault-injection seam decision, the test matrix, SQL Server experiments and per-phase go/no-go. Note that this branch contains no partial-custody WIP mechanism, so 2P is expected NO-GO. **STOP after Phase 0; no code or DDL changes.** Do not implement, post or create migrations in Phase 0. Grok 4.6 may implement 1A/1B/2I/2P/2M/3I/3P/4 only after separate explicit approval for each gated subphase. No CostLayer/FIFO/WIP costing/replay/month-end modifications, no inventory conversion and no fee capitalization without a separately signed-off Phase 5.

---

## 18. Approval / delivery status

**Design objective accepted:** a single configurable subcontracting feature with both Inventory stock and Production WIP sources.

**Approved now:** Grok 4.6 **Phase 0 audit/decision only** — no implementation, no production data changes. Branch evidence confirms there is no ready-made safe custody mechanism and no complete atomic TR composition.

**NOT approved yet:** Phase 1A, 1B, 2I, 2P, 2M, 3I, 3P, 4. Current 2I/2P/3I/3P GO = **FAIL/NO-GO** until verified or re-designed. Phase 5 excluded. Continue only after the current-branch forensic review and a go/no-go on each underlying movement and valuation path.

### 18.1 Mandatory fallback decision if Phase 2I returns NO-GO (new in v5.2)

Every gate above resolves to "the feature remains unavailable," which would otherwise leave a customer with a Subcontracting menu that can create jobs but can never send anything out — an outcome nobody has signed off. Before Phase 1B, the owner must record **one** of:

- **F1 — No module.** Do not ship the menu at all; keep the capability out of the product until a posting-safe design is approved. Lowest risk, zero misleading UI.
- **F2 — Read-only custody shell.** Ship 1A only (settings, permissions, contextual read-only pages and guidance) with an explicit "posting not available in this release" message. No job/dispatch persistence, no stock movement.
- **F3 — Separate approved safety design.** Commission the dedicated source-gated design (custody candidate C, or a new in-transaction vehicle B) as its own mini-project with its own review, before any subcon dispatch exists.
- **F4 — Business/process alternative.** Handle the scenario outside the ERP for now (manual delivery note + existing service PO) and record that decision as the accepted interim control.

The decision must name the operator message shown when posting is unavailable, and the target release statement. Target release is only those source modes with complete, proven quantity and valuation safety; partial rollout must clearly hide disabled functionality.
