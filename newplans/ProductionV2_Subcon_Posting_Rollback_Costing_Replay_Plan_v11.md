# ProductionV2 — Subcontracting Posting, Rollback & Historical Cost Replay
## AI Coding Agent implementation plan — v11.0 (source-grounded, safety-gated)

**Review date:** 23 November 2026 (Malaysia)  
**GitHub source:** `mokth/net10projectTemplate`, **`productionv2`** branch  
**Pinned remote HEAD checked:** `3ecf34cb18db0e422fb2e1bca94582644446a5eb` — *Extend WAC count replay to SC/VR/GR and fix SA_CDN COGS attribution* (2026-11-23 02:52:36 UTC)  
**Status:** **DESIGN PLAN — NOT APPROVED FOR LIVE POSTING / NOT 10/10.** Implement per-gate only, after current local worktree, SQL Server schema, costing owner, supported replay shapes and regression baseline are verified. **Do not turn Subcon live flags on to bypass a blocker.**  
**Change class:** C (stock / cost / WIP posting) and D (historical replay / rollback). Requires explicit approval of exact changes to protected posting/valuation/ledger APIs.  
**Existing broader business plan:** `newplans/productionv2_SUBCON_REAL_WORLD_IMPLEMENTATION_PLAN_v10_STRICT_DEEP_REVIEW.md` is complementary. This v11 concentrates on **exact posting and rollback integration**, especially chronology, cost methods, downstream effects and release gates. Do not treat earlier plan's repository pin or assumptions as more current than the live source.

---

## 1. Executive decision and target outcome

Build Subcon as an **operator-friendly orchestration module**, **not** a separate inventory, FIFO, WAC, Standard Cost, production WIP, or posting engine.

A correct subcontract process must preserve **both ownership/custody and value**:

1. **Inventory-only, same SKU processing:** move **company-owned** material from company warehouse to segregated supplier custody; receive the *same* item back; allow partial return and leftover return. A pure custody movement is a **net-zero quantity/value transfer** and must not create an invented purchase receipt, new production cost, or Sales COGS.
2. **Inventory-only BOM-to-FG assembly / SKU conversion:** dispatch raw materials without consuming them yet; when completed outputs are physically received/accepted, atomically consume **actual** supplier-held components and recognize **new FG** using the existing authorized valuation policies; record scrap/byproduct/leftover and service fees without double counting. This is **NOT** an ordinary TR or unpriced MR.
3. **Production WO external operation:** dispatch **partial eligible WIP** to supplier custody, remove that WIP from normal operation/FG eligibility, receive it at the correct stage with verified costs, and continue through existing Production authorities. A WO Transfer is **not** a substitute (current owner moves the whole WIP balance between Work Orders).
4. **Every Post/Rollback/Repost must either make the full supported, proven physical + valuation + business change atomically, or reject with zero persisted effects.** Historical replay must restore all affected later active values/generations or refuse safely. **Backdated is not automatically supported because the transaction date field accepts an old date.**

### Required outcome, by transaction kind

| User action | Correct physical effect | Correct costing effect | Rollback obligation |
|---|---|---|---|
| Save/Edit NEW Job / Send Out / Receive | **None** | **None** | Delete/cancel NEW only when no references |
| Post same-SKU **Send Out** | Internal warehouse −Q; authorized supplier-held slice +Q | Net company item quantity/value unchanged; preserve original applicable layer/value lineage | Supplier slice −Q; original source +Q, exact active history reversal |
| Post same-SKU **Receive Back** | Supplier-held slice −Q; authorized receiving slice +Q | No duplicate receipt valuation; correct transferred layer/value | Reverse receive into supplier slice; reject if later consumed |
| Post **leftover material return** | Same as supplier-to-company TR for the remaining identified component quantity | Preserve original material cost, never re-price as supplier purchase | Reverse exactly, subject to dependencies |
| Post **actual BOM conversion / assembly** | Supplier-held inputs −actual consumption; output FG +actual accepted base qty; scrap/byproduct as separately authorized legs | One existing-ledger posting owner: remove exact input cost; recognize FG and any approved fee/variance using WAC/FIFO/Standard authority | Reverse complete material/output/value allocation and append reversal; refuse if output later used and no certified replay |
| Post **Production external WIP out/in** | Split/move eligible pool qty and custody at WO/route/operation level | Preserve pool value/contribution lineage and approved processing-cost additions | Exact inverse paired WIP movements + downstream proof |
| Supplier processing **PO/PI/CN/DN** | Usually **no physical stock** | Commercial settlement / separately approved cost capitalization and subsequent corrections, **never duplicate FG receipt** | Reverse commercial/cost settlement with dependents, not arbitrary stock |
| Backdated Post / Rollback | Historical effective-time reconstruction + all dependent corrections **or reject** | Reprice/correct every impacted active layer, pool, COGS, PPV, variance and month snapshot as supported | Append inverse active generations; no history deletion or partial recost |

### Go/no-go policy

**Do not claim “all current costing scenarios are supported” until every specifically required route has a tested public posting implementation.** The existing framework has **bounded** WAC/FIFO/Standard replay, not a proven universal arbitrary multi-item, multi-root, cross-module historical replay engine. This plan makes all relevant cases **handled** as either **SUPPORTED / BLOCKED WITH REASON**; extending a blocked case to *supported* is a separately scoped costing project with new proof and approval. Users must never see a successful Post when an unsupported case was skipped.

---

## 2. Current repository evidence (pinned source, not inferred)

| Verified current source / behavior | Consequence for implementation |
|---|---|
| `ErpWeb.Core/Subcon/ISubconInventorySourceAdapter.cs`: `SubconBlockedInventorySourceAdapter` blocks dispatch Post, dispatch Rollback, receipt Post, receipt Rollback; Production adapter also blocks | Existing Subcon **does not post**. Keep until all safety gates pass. |
| `ErpWeb.Core/CoreServiceCollectionExtensions.cs` lines **307–310** binds both blocked adapters | New posting service must be registered **only after** proof and per-tenant capability gate; avoid accidental global activation. |
| `SubconApplicationService.PostDispatchAsync` / `RollbackDispatchAsync` dispatch by mode to adapters (approximately lines **397–434**). `PostReceiptAsync` uses inventory adapter (~**708–721**). No `RollbackReceiptAsync` in `ISubconApplicationService` | Add genuine receipt-rollback interface, user action, service authorization and proof; don't mistake adapter method for reachable UI/API. |
| `SubconCodes.cs`: only `NEW`/`CANCELLED` status; `SavePolicyAsync` explicitly refuses enabling all live flags; `SubconPostingBanner` says drafts only | Add posted lifecycle **only with proof**, and make status/UX truthful; draft and posted outstanding must not be mixed. |
| `SubconApplicationService.SearchDispatchLinesAsync` filters dispatch `Status == NEW` (~**738–745**), and Receive-Back can save allocations against draft dispatch | Posted receiving lookup and receipt eligibility must be changed; do not use saved draft allocations as posted quantities. |
| `ErpWeb.Model/Entities/Subcon/SubconEntities.cs`: Job, materials, outputs, dispatch, receipt and lines exist, but no authoritative posting links, active generation or physical custody cost allocation | Add minimal linkage/evidence; do **not** create writable `SubconStockBalance` or `SubconCostLayer` authoritative tables. |
| `IIvInventoryPostingService.cs`: public `PostAsync`/`RollbackAsync` dispatch per batch; in-transaction APIs exist for stock-in/out/adjustment, **not TR** | Calling `PostAsync("TR",...)` inside a Subcon Save transaction is **not atomic** with Subcon document status. Add a **minimal existing-owner transfer-in-transaction seam** subject to source proof and approval. |
| `IvInventoryPostingService.cs` `PostInventoryTRAsync` (~**3050–3525**) and `RollBackInventoryTRAsync` (~**3540–3810**) each own a SQL transaction, branch lock, active ledger, physical move, history, valuation and batch state | Reuse this exact owner, checks, valuers and reversal; extract shared core rather than reproduce code in Subcon. |
| `IvInventoryPostingService.DispatchAsync` (~**250–445**) iterates selected batches; several can succeed while another fails | **Never** implement a multi-batch document as `PostAsync(TR, [batch1,batch2,...])` and then mark one Subcon document posted; use **one physical batch/root per indivisible operation** or a new explicitly atomic root. |
| `IvStockTransferService.ValidateLineAsync` (~**738–930**) forbids same/existing destination lot for lot-controlled TR; normal non-lot transfer is valid | **Lot-preserving round-trip is not supported by generic TR.** Keep lot processing blocked until approved source-path extension; do not relax all global TR checks. |
| `IvWarehouse.WarehouseType` is metadata; no demonstrated global exclusion of vendor-held stock from Sales, MI, IP, reserve, count or transfer selectors | **Supplier warehouse label alone does not isolate custody.** Shared eligibility and posting guards are a prerequisite to any live dispatch. |
| `StockPostingCoordinator.BeginInTransactionAsync` requires `CurrentTransaction`, acquires branch stock lock, checks active epoch, request ID/fingerprint, source revision, periods and freeze scopes | Use as sole V2 posting coordinator; one authoritative root per atomic business action and exact source revision/active generation. |
| `IvInventoryPostingService.cs` has `FuturePostDateError`, `IvPeriodCloseGuard`, `RejectFastPathStalenessAsync`, replay selectors; backdated TR tries WAC, FIFO, Standard bounded handlers before a fail-closed result | Inherit exact business-date, period, stale chronology and replay policy; do not write direct `IvBalLoc` on a historical transaction. |
| `plans/productionv2_backdated_replay_impact_manifest.md` describes **bounded** public WAC/FIFO/Standard replay (e.g. 1-line TR, <=100 same-month ordinary descendants), not universal cross-module replay | Treat the manifest as a coverage **lead**, then re-run exact public-route tests on the pinned local commit. Its own historical coverage baseline is older than current HEAD. |
| `ProductionBalLot` is authoritative WIP qty/value, `ProductionStockWriter` posts pool movements, but `ProductionOutputService.Posting.cs` also directly consumes/modifies lots; `ProductionWorkOrderTransferService` moves **full** WIP balance | Partial external custody needs verified controls in **all** Production consumers; cannot piggyback on WO Transfer, or change one writer and assume safe. |
| `.agents/skills/productionv2-costing/SKILL.md` + `references/{stock-balance,inventory,production,procurement,replay-rollback,testing}.md` require fail-closed method-specific proof and SQL Server tests | Mandatory implementation/review instructions. No default replay flag enablement. |

**Source pointers (all on the pinned branch):**

- [`SubconApplicationService.cs`](https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/Subcon/SubconApplicationService.cs)
- [`ISubconInventorySourceAdapter.cs`](https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/Subcon/ISubconInventorySourceAdapter.cs)
- [`IIvInventoryPostingService.cs`](https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/Inventory/IIvInventoryPostingService.cs)
- [`IvInventoryPostingService.cs`](https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/Inventory/IvInventoryPostingService.cs)
- [`CoreServiceCollectionExtensions.cs`](https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/CoreServiceCollectionExtensions.cs)
- [`StockPostingCoordinator.cs`](https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/StockLedger/StockPostingCoordinator.cs)
- [`ProductionOutputService.Posting.cs`](https://github.com/mokth/net10projectTemplate/blob/productionv2/ErpWeb.Core/Production/ProductionOutputService.Posting.cs)
- [`Stock replay manifest`](https://github.com/mokth/net10projectTemplate/blob/productionv2/plans/productionv2_backdated_replay_impact_manifest.md)

**Evidence limitation:** inspected **remote GitHub source**, not local unstaged/working changes, live tenant schema, running UI, or executed tests. All implementation must begin with **current local** source/database verification. No build/tests/UAT have been executed by this review.

---

## 3. Mandatory transaction design: ownership, stock, value and status

### 3.1 Exactly one posting owner per physical leg

**Inventory custody transfer owner** (same SKU, non-lot pilot): underlying `INVENTORY / TR` batch and V2 StockPosting **remain the physical/valuation authority**. Subcon doc is a parent operational reference linked to that **single batch, source revision and active posting generation**. The Subcon-specific ledger metadata and Subcon status are committed in the **same SQL transaction** as the TR. **Do not create a second separate V2 posting root with the same physical legs.**

**Inventory conversion owner** (different SKU / actual assembly): needs an **approved new V2 source-command family** *within existing* `StockPostingCoordinator` + `InventoryValuationService` + cost strategies. Atomic consume and FG receipt require one root with explicitly enumerated physical legs and immutable input/output valuation allocation; Subcon owns the business document, canonical ledger owns authoritative stock/cost. An Inventory TR cannot convert one item to another; `MR + MI` as independent successful posts is **forbidden**.

**Production WO source**: existing Production posting/pool ownership, with a new approved partial-custody operation/paired movement contract, not a fake Inventory transfer of WIP. Underlying Production contribution/valuation reconciliation must work from original material issue to downstream FG.

**Commercial settlement source**: existing PO/GR/PI/CN/DN authorities, with an explicit contract linking the supplier processing fee or expense to the Subcon Job. Commercial posting must never post physical movement a second time.

### 3.2 Suggested lifecycle (minimal, explicit)

- **Job**: `NEW` → `ACTIVE` (first valid posted leg) → `PARTIAL` → `COMPLETED` → `CLOSED`; `REOPENED` if valid dependent reversal. Derive closure from actual material/output/scrap/fee reconciliations; never fake completeness via header flag.
- **Each inventory/production movement document**: `NEW` → `POSTED` → eligible **Rollback** to `NEW` with a **separate immutable reversal audit generation**. `CANCELLED` = unused draft only. Keep original date and lineage. Existing TR batch uses `NEW` after rollback; respect that.
- **Partial receives**: each receive document independently posts once, against one or more **posted** send-out lines; keep stable line IDs and quantities. Subsequent receipt/return must not silently alter the original dispatch quantity.
- **Posting linkage** (additive, minimal): `SubconDocKind`, `SubconDocId/LineId`, `JobId`, `BatchNo` and/or `StockPostingId`, exact `PostingRole`, `DocumentRevision`, `OperationId/RequestId`, `EffectiveAt`, `Active/Reversed`, `OriginalPostingId`, audit stamp, and rowversion. DB indexes prevent duplicate active source generation and cross-company/cross-branch references.
- **Actual allocation records**: record which dispatch-line/company-owned physical slice supplied each receive/consume/return/scrap and **how much base qty and value** via links to **authoritative ledger facts**, never a second independently editable valuation total.
- **Cost evidence**: input layer/valuation fact IDs, WIP contribution/production movement IDs, approved fee/variance/expense owner and exact output/return allocations. Preserve past versions; do not delete cost facts on rollback.

**No shadow authoritative fields:** `SubconCurrentStock`, `SubconCostLayer`, `SubconWacBalance`, standalone supplier FG value, or separate writable WIP balance. An inquiry may materialize a **rebuildable**, reconciled projection from sealed movements, but it must never be the source of stock or cost truth.

### 3.3 Inventory-only custody balance and user availability

For each **company/branch, supplier, job, dispatch line, item, warehouse/bin, lot, status, base UOM, effective date**:

```
OutstandingSupplierQty = PostedSendOutQty
                       + PostedAdditionalSupplyQty
                       - PostedSameSkuReturnsQty
                       - PostedActualInputConsumptionQty
                       - PostedAuthorizedScrapOrLossQty
                       +/- SupportedPostedCorrectionsAndReversals
```

- `OutstandingSupplierQty >= 0`; cannot consume/return more than **this particular job/line owns**, even when another job's identical stock is in the same physical supplier warehouse.
- Physical supplier-held `IvBalLoc` quantity reconciles with the **sum of all active allocations** by that supplier/warehouse/item/slice, with explicitly explained exceptions for supplier-owned/consignment stock (outside this initial feature).
- Total company-owned stock across company + supplier slices is conserved by a **custody transfer**; *available for ordinary Sales/MI/IP and standard stock count* excludes supplier-held stock unless a separately approved business workflow expressly authorizes it.
- Pickers/search are conveniences, **never** the sole gate. The *same shared eligibility rule* must re-check at every server-side stock consumer, stock count source, reservation path, delivery, material issue and direct posting path. Unauthorized generic TR from supplier to unrelated location must also be blocked.
- Use `IvWarehouse.WarehouseType` / approved lookup plus supplier/job custody allocation only after inspecting all existing consumers. **Do not assume `WarehouseType='SUBCON'` is enforced**. Supplier-specific warehouse can group stock; job/source allocation must still prevent cross-job usage. Do not create one warehouse for every job without justification.

---

## 4. Concrete Post and Rollback algorithms (operator workflow through SQL ledger)

### 4.1 Send-Out Post — existing Inventory TR owner, ONE atomic root

**UI** Save NEW → user selects Post on list/view → confirmation with supplier, source, quantity → application service → exact original Inventory TR owner → V2 valuation → Subcon link/status → commit.

**Algorithm (pseudocode, not an existing API):**

1. Authorize `SubconSendOut.Post` **and** the approved Inventory TR posting authority (prefer both `INV_STOCK_TRANSFER.Post` and Subcon Post unless an explicit security architecture change is approved); trusted company+branch and owning warehouse.
2. Resolve `NEW` document and **stable line IDs/version**, active policy per tenant, mode, supplier, each source `IvBalLocId`/lot/status, canonical UOM conversion, existing source balance, and **not already allocated/posted** facts. Validate date, promised return, optional PO/Job/BOM snapshots; ensure target supplier custody location exists and is segregated.
3. Read **effective-date** as-of physical, FIFO/WAC/Standard and reservation/QA evidence, not merely current `StdQty`. Determine if historical replay is necessary and whether exact **shape** is supported. If unsupported, return **BLOCKED** before any mutation.
4. Create/freeze one underlying `TR` batch with exactly all lines for this indivisible dispatch and stable Subcon source tags. A batch cannot contain a mixture of unrelated document owners or multiple suppliers. Reuse existing Transfer validations; **lot-controlled** returns/dispatch remain blocked until approved extension.
5. Begin one SQL transaction; acquire **existing branch stock transaction lock in canonical order**; re-read and lock document/line/version, TR batch, source/destination slices, supplier allocation and active ledger epoch; recheck the planned as-of/price/layer/dependency fingerprint, period, business date, physical/available qty and eligibility. Existing `StockPostingCoordinator` starts the **one** inventory posting root.
6. Call the **new approved in-transaction TR entry** extracted from `PostInventoryTRAsync` so that **physical source −Q, supplier destination +Q, `IvTrxHistory`, active V2 valuation facts/FIFO layer lineage, and source `IvTrxBatch.BatchStatus=POSTED`** all occur in this transaction. No new homegrown value arithmetic.
7. Add `SubconPostingLink`, exact custody allocations and audit; set Subcon dispatch `POSTED` (or per-leg posted). Run same-root physical/financial reconciliation and seal using existing coordinator. Save/commit **once**. Expose original operation ID and batch number for support.
8. If any line fails, **rollback entire SQL transaction**: no posted Subcon header, no inventory delta, no layer consumption, no orphan link, no audit root left half-sealed. For ambiguous network timeouts, query RequestId+fingerprint/active generation before any retry; do **not** blindly retry.

**Required API seam:** design and separately approve a `PostStockTransferInTransactionAsync` (and reversal equivalent) *or* a narrower existing-owner transaction callback. Existing `IIvInventoryPostingService.PostAsync` is **not** sufficient because it creates its own transaction and multi-batch behavior. Critically, **historical TR helpers currently open their own contexts/transactions**: simply exposing the fast-path method does **not** solve backdated transaction sharing. Refactor only the proven common path; historical plans must be prebuilt and rechecked within the caller-owned root. If no safe seam is proven, keep Subcon Post blocked.

### 4.2 Send-Out Rollback — reverse exactly, then restore `NEW`

1. Confirm posted **active** original Subcon link and Inventory TR posting generation; authorize `SubconSendOut.Rollback` plus approved Inventory TR rollback authority; check original effective date remains open.
2. **Refuse** if any active receive-back, actual FG conversion, material consumption, scrap, another dispatch/transfer, customer sale, Production issue/WIP/output/FG, stock count dependency, QA hold or financial correction relies on the dispatch and the exact dependency-aware replay is not certified. Offer the operator the related document(s) to reverse first; **do not auto-cancel commercial/source documents without user action**.
3. Build physical and valuation removal/dependency impact plan at original effective time; under the same branch lock revalidate identity, active generation, historical tail, FIFO sources, item pool cost and affected allocation ledger, and periods/freeze.
4. Call existing-owner in-transaction TR rollback/replay to reverse the **exact original** (supplier −Q, original company slice +Q), write append-only reversal facts and restore TR batch `NEW`. Set Subcon dispatch `NEW` only after stock/root reversal is sealed; mark old Subcon posting link **reversed**, preserve historical evidence and increment revision on eligible repost.
5. All effects and Subcon changes commit **in one transaction**. Repeat rollback returns a safe already-reversed/business refusal and never doubles source stock.

### 4.3 Same-SKU Receive Back / unused-material return

1. Eligible source is **supplier-held stock created by an active posted send-out**; select supplier/job/dispatch line from searchable lookup. A `NEW` dispatch is **not** eligible. Available quantity is the smaller of source slice availability and per-job remaining allocation at effective time.
2. Validate receiving warehouse/location/stock status and QA/lot policy. Post **one atomic supplier→company TR** (with exact source lot/layer identity) and a linked Receive Back document. Use the same in-transaction owner and date/period/replay checks as Send Out.
3. **No new receipt layer at current supplier price**; maintain original company-owned value. Same-SKU processing fee, if capitalizable, is a **separate approved valuation/settlement operation**, not a pretend extra inventory receipt/quantity increase.
4. Rollback Receive Back first: reverse exact receipt TR, returning stock to supplier custody and restoring allocated outstanding amount. Reject if subsequent stock sale/issue/count/FG action used this returned stock and no complete safe replay exists.
5. Support many partial receives and multiple send-outs, but each document's own Post/Rollback is atomic. **Do not rollback entire dispatch while one posted receive depends on it.**

### 4.4 Assembly/Conversion: component(s) A+B → FG C (different SKU)

**Do not implement as a standalone `MI` followed by separate `MR/GR` commits**. One half succeeding corrupts financial quantity/cost and Subcon outstanding.

1. Source: posted supplier-held component custody. Freeze BOM snapshot only as **planned requirements**; accept operator-verified **actual component consumed**, quantities by job/dispatch line/warehouse/bin/lot/UOM, supported substitute/additional parts and yield. Deduct only actual quantity. Scrap/loss and leftover are **explicit** events, not automatic BOM difference.
2. Target: received/accepted FG quantity and destination (supplier custody if finished but not physically returned, or normal goods-receipt location if physically returned), exact stock control/UOM/lot and QA grant. Never post main warehouse FG before actual eligible receipt.
3. Existing V2 coordinator opens **one new approved conversion posting owner**, with physical component issue leg(s), FG receipt leg(s), optional authorized scrap/byproduct/variance leg(s), and precise input→output lineage. All are part of one root and one SQL transaction.
4. The valuation owner obtains component **method-specific effective cost** and approved capitalization/variance policy; it does **not** use user-entered selling price, latest purchase price or manually approximated WAC. Output valuation/remainder/variance must reconcile to source costs and any separately approved processing fee without double-counting.
5. On successful seal: update supplier custody allocations, actual consumed records, output receipt allocation, Job progress and remaining material balances, lot genealogy, posted documents and cost/quantity audit atomically.
6. Rollback: confirm the output FG has **not** been sold/consumed/reworked/transferred/counted or posted to a closed period unless the entire dependent graph can be legally and completely replayed. Reverse FG valuation/quantity, restore exact consumed source lots/custody/value, reverse scrap/byproduct/fee/variance relationships, preserve history and job allocations in **one root**. Reject incomplete or incompatible descendant chains.
7. For a **same-SKU transformation with cost uplift** (e.g. painted A returned as A), the physical leg can be a TR, but the incremental **cost** is not a TR. Require a separately approved value-only adjustment/fee settlement authority and effective-date downstream propagation; otherwise post physical return without capitalizing the fee only if accounting policy explicitly permits expense treatment, or block until settled. Never silently use full FG conversion for a same-SKU custody return.

### 4.5 Production external operation / WIP

1. Source is a **released** WO and **frozen** operation/route snapshot with a valid `SUBCONTRACT` external step (currently process metadata, **not** an operating custody movement). Validate current ProductionBalLot pool, active contribution valuation, lot, base qty, source operation and available partial quantity.
2. Post **partial WIP custody-out** via an approved Production ledger contract: create paired movement evidence, move/split Q **and its exact attributable pool cost** to a supplier-specific offsite balance/ownership reference. Total company WIP qty/value unchanged at dispatch; normal Daily Output, WIP consume, next-operation and FG eligibility must exclude this quantity **at server posting time**, not just in lookup UI.
3. Return processed WIP partially to the correct WO operation/stage, with QA disposition and approved fee/conversion cost. Production valuation authority makes legitimate additional contributions; no artificial material-in or second raw-material issue. Extra RM supplied to the vendor is tracked by an **independent Inventory supplier-custody line** and linked to a **single consumption owner** when used, preventing duplicate RM cost absorption.
4. Rollback external return if no later step consumes it, then rollback external dispatch. Every pool/dependency/movement and cost must reverse exactly. If later Daily Production/FG/sale/WO transfer uses it, the bounded current replay must prove the full graph or the rollback is refused.
5. Current `ProductionWorkOrderTransferService` is a full-lot inter-WO transfer; **do not call it for partial vendor WIP**. Identify all `ProductionOutputService.Posting`, `ProductionFinishedGoodReceiptService`, material issue, adjustment, stock return and WO transfer entrypoints that must share the custody guard.

### 4.6 Fees, PO/PI, returned scrap, loss, QA and settlement

- Processing charge policy decided by Finance **before** live capitalization: (a) expense, (b) approved accrued cost at FG acceptance then actual PI true-up, or (c) cost settlement with exact effective-date authority. **Do not assume** a service PO can be invoiced without GR; current Procurement service/PI route has restrictions and is not yet proven for this Subcon scenario.
- If fee is capitalized, ledger allocation must cover **partially completed outputs, unsold stock, already-sold COGS, remaining WIP, canceled/reversed outputs, and closed months** as relevant, using approved original/active facts. Late PI price variance that cannot reach consumed FG/COGS must **block** or follow separately approved finance-period adjustment, **never silently rewrite sold FG's cost or closed stock**.
- Supplier-caused losses, scrap, recoverable byproducts, rework and damage need approved owner and expense/valuation treatment; do not absorb the same loss twice into FG and scrap expense. Returned scrap as valued stock and irrecoverable scrap write-off are different transactions.
- QA frozen/not-installed gate is **not** equivalent to acceptance. Where QA is required, posting must await a working permitted QA authorization; do not silently bypass. A logical hold is not a reliable physical eligibility exclusion without a validated shared guard.
- Vendor-owned materials, consignment, intercompany transfer, direct purchase of FG and subcontract service-only transactions are different accounting models: **out of pilot** until explicit ownership/value policy and evidence. Don't post company-owned inventory for materials that were never company-owned.

---

## 5. Time & historical replay design — the critical section

### 5.1 Definitions and rules

- **DraftDate**: saved future/past draft under existing rules — **zero stock, zero WIP, zero financial value**.
- **EffectiveAt**: transaction's original business-effective timestamp, not the moment the Post button was clicked. Same-day tie order must respect the actual `StockPosting` sequence and parent→child dependency (Send Out must precede its receipt; consumption must not precede custody).
- **PostTimeUtc**: audit time only. Do not use it to price historical inventory or reorder earlier events.
- **RequiresReplay**: an insertion/removal changes the active physical/financial timeline or downstream dependency. An old date **without** later dependent events may use the proven fast path; an old date **with** descendants needs valid bounded replay or must block.
- **Rollback** = remove the **active financial/physical effect** using appended reversal/correction generations while keeping original history, then recompute **all** subsequently affected active facts and current balances **if and only if the public replay owner supports that exact graph**.
- **Period/freeze**: original date and any applicable reversed/affected periods must be open per authoritative `IvPeriodCloseGuard` / `IStockPeriodGuard`, plus stock-count freeze and period snapshots. Never mutate a closed period just because today's date is open.
- **Future Post**: inherit `FuturePostDateError` / production `TransactionPostingDateGuard` where applicable. Do not accept future stock consumption/transfer at Post merely because a future NEW document is valid.
- **Feature flag**: keep `OpenPeriodBackdatedReplayEnabled` and per-tenant Subcon capability flags OFF until independent case proof. The historical replay switch alone does not grant Subcon historical coverage.

### 5.2 Mandatory replay protocol for each candidate root

**Read-only plan (outside lock):**

1. Load current authoritative posted source and revisions, company/branch, warehouse/lot/status/UOM and cost method **at effective time**, method revisions and opening stock valuation evidence.
2. Build the full ordered **physical** timeline on **all** touched piles/items (including supplier-held piles), plus active **valuation** facts/generations/layer consumptions, active stock counts (end-of-day anchors), PO settlement/PPV, Sales shipment/return COGS and Production WIP/output/FG dependencies. Exclude reversed active effects but retain audit lineage.
3. Simulate insertion/removal with canonical effective-time, posting sequence, cost-owner and generation order. Recompute FIFO split decisions, WAC averages/value-only deltas, Standard cost/variance, FG/COGS and other effects only where existing authorized planners support it. **If the graph or a required cost authority is unknown, STOP**.
4. Record fingerprint: active ledger epoch, branch posting watermark, source document revision/status/rowversion, impacted balances/lots, cost-state version, active valuation generation IDs, affected downstream owners and period/freeze status. Build an exact list of required reversed/replacement facts.

**Authoritative root (one SQL transaction):**

5. Acquire existing branch lock; enforce deterministic slice/lot order. **Re-read** and revalidate the entire fingerprint, permissions, policies, stock/QA reservation, original/later periods, same-day ordering, source UOM, cost authority and all affected downstream owners. Stale graph ⇒ retry outside lock or safe rejection, never partial write.
6. Begin the **one existing-ledger root** for the physical owner; append required physical movements, original fact(s), exact reversal and replacement lineage for later active facts; update authoritative current `IvBalLoc`, FIFO remaining/consumption, WAC state, Standard variance, Production pools, Sales COGS, PI/PPV and Subcon allocations, as the **proven** graph requires.
7. `ValuePending` / `CompleteReplayInTransactionAsync` through current coordinator; run physical and value reconciliation before seal. Mutate Subcon operational status/links **inside the same transaction**. Commit exactly once. Missing fact or mismatch ⇒ entire rollback and readable reason.
8. After commit, compare active timeline and as-of balances, current stock, `StockCostState`, layer balances, WIP and FG/COGS, supplier statement, period report and audit. Store support trace and exact operation reference; no destructive history edits.

**CRITICAL:** existing public replay handlers are **bounded and often open their own transactions**. Introducing an in-transaction Subcon wrapper must not bypass their preplan/under-lock revalidation or convert multi-item conversions into a sequence of individually committed single-item roots.

### 5.3 Current coverage and required extension matrix (source-informed; exact pin must be re-tested)

The `productionv2_backdated_replay_impact_manifest.md` is a **historical coverage report** and contains restrictions. Treat the table below as an **initial gate estimate, NOT automatic support**. “Candidate” means target an existing public route and reproduce its tests; it is **not** certified Subcon support.

| Historical shape / later dependency | WAC | FIFO | Standard | Subcon decision until proven |
|---|---|---|---|---|
| **Same-SKU non-lot, single-line** TR before limited same-month MR/MI/SC/TR descendants, existing/new destination | Candidate: bounded public TR replay | Candidate: bounded public TR replay | Candidate: bounded public TR replay | **BLOCK** until Subcon atomic seam + custody exclusions + actual end-to-end SQL tests; then allow only proven shape. |
| **Multi-line dispatch/return** with later dated movements | Generic TR historical multi-line **not proven** | Not proven | Not proven | **BLOCK historical Post/Rollback** or split into independently authorized operator actions; never partially post one doc. |
| **Lot-controlled transfer and return to original/existing lot** | Existing TR lot validation rejects round trip | Same | Same | **BLOCK** pending explicit approved lot-preserving transaction and layer/lot lineage design. |
| TR before a later **Stock Count** | Historical count corrections exist for some MR/MI/SC/VR/GR shapes, not generic TR | No proven TR-before-count route | No proven TR-before-count route | **BLOCK** until count observation and all active generations are corrected by certified TR public writer. Count remains absolute observation. |
| TR before **Sales DO/direct Invoice/linked invoice**, return/CN/COGS | No proven arbitrary TR→sale propagation | No proven | No proven | **BLOCK** if Sales stock/COGS depends on modified historical slice or cost; do not change shipment owner/COGS silently. |
| TR before Production **IP → WIP → Output → FG → sale** | No proven full TR-dependent production replay | No proven | No proven | **BLOCK** if descendants impacted. Current MR/GR bounded production replay does **not** automatically cover Subcon TR or conversion. |
| TR/Conversion around **GR→PI→CN/DN PPV/price variance** | Some bounded WAC receipt/PI replay; not universal supplier/FG conversion | Partial / not proven | Partial / not proven | **BLOCK** until every consumed/on-hand settlement value correction is proven; never use a neutral TR to smuggle price variance. |
| **Inventory BOM A+B→FG C** with later movements | No approved general public multi-item conversion root | Same | Same | **BLOCK** until new approved atomic physical+valuation owner and new method-specific replay graphs. |
| **Same-item processing cost uplift** after item is returned or sold | Value-only replay may support narrow original owners; not generic Subcon fee uplift | Layer cost restatement unproven | Effective standard variance path unproven | **BLOCK capitalization** until approved specific value event and downstream application; physical custody return alone may proceed only under approved expense policy. |
| **Partial WIP out/in**, later Daily Output/FG | Production historical graph bounded around specific MR/GR → 1 IP/Output/FG shapes, not external partial custody | Same limitation | Same limitation | **BLOCK** until Production partial custody + active descendant replay certified. |
| **Cross-month historical tails**, even with months nominally open | Current manifest TR route bounded to same-month | Same | Same | **BLOCK** until cross-month, period valuation/snapshot and concurrency proof is explicitly added. |
| **>100 affected events**, multi-count, mixed items/methods, same-day equal-time competitors | Public bounded routes not proved | Not proved | Not proved | **BLOCK** or approved bounded expansion per method/shape; never truncate the tail and post. |
| Future POST or closed/frozen period (post OR rollback) | Forbidden by existing policy | Forbidden | Forbidden | **ALWAYS BLOCK**; business-friendly reason; zero effects. |

**Do not overstate WAC support:** at this pinned HEAD recent WAC work extends certain Stock Count replay involving **SC/VR/GR**; that is **not** evidence that TR, Subcon or multi-item conversion can replay through a stock count. **FIFO** needs exact active layer receipt/issue allocations, not average. **Standard** needs effective-dated approved standard and correctly owned variance, not current standard or WAC fallback.

### 5.4 Four concrete backdated examples (expected behavior)

**Example A — late Send Out would make a later sale physically impossible.** On Nov 8 MAIN has 100 pcs. Nov 9 MAIN shipped 50 pcs. On Nov 12 the operator attempts to **Post a Nov 8 Send Out of 70 pcs** to the supplier. Effective MAIN after Nov 8 becomes 30, so the Nov 9 shipment of 50 is impossible in the replayed history. **Reject entire Send Out**. Do not borrow stock from the future or post only 30.

**Example B — rollback after Receive Back.** Nov 8 Send Out 70, Nov 12 Receive Back 40, Nov 14 operator rolls back Nov 8 Send Out. The Receive Back depends on that dispatch and its source stock. **Reject**; user first rolls back the active Receive Back (if legally possible) and only then the dispatch, **or** a future independently certified dependency-rewriting replay handles both atomically. Never leave Receive Back posted against an unposted dispatch.

**Example C — later Stock Count.** Nov 8 dispatch is posted late, but MAIN was physically counted on Nov 10. Its **absolute counted quantity must not be rewritten**. Any required pre-count event and derived count adjustment/cost basis must be corrected via active Stock Count generations, respecting end-of-day ordering and valuation. Current generic TR→Count public route is **not proven**: **block** instead of rewriting the count's frozen physical observation.

**Example D — later price variance / sold FG.** Nov 5 components are supplied, Nov 10 FG is accepted, Nov 12 FG sold, Nov 20 supplier PI changes processing cost effective to Nov 10. Merely raising current stock value is wrong because some FG is **already sold**. A valid solution needs an explicitly approved valuation settlement allocating change to **remaining FG and Sales COGS** (and maybe WIP), with FIFO/WAC/Standard-specific lineage and open periods. If unavailable, **block capitalized historical cost adjustment** and follow approved Finance expense/current-period policy; do not silently reprice historic cost.

---

## 6. Method-specific acceptance: every cost scenario must reconcile

### 6.1 WAC / Moving Average

- Read authoritative `StockCostState` and **all** active quantity and value-only facts at effective time, not `IvBalLoc.Cost` as universal authority.
- Supplier custody TR must keep item-wide valuation neutral: `Source −q/+?` and `Supplier +q`, **no receipt price event**, same company/branch item pool (subject to actual current valuation policy).
- Zero on-hand, zero-value residual, fractional UOM, rounding/precision, inbound after zero and value-only PI/CN settlements must reconcile; do not invent an average from sale price or current purchase price.
- Backdated movement must recompute later moving averages and exact active correction generations **when supported**. Reject missing value evidence, non-replayable subsequent purchase settlement/Stock Count, or unmatched current pool value.
- The fee-capitalization path must prove impact on already-consumed stock/FG/COGS, not just increase current WAC.

### 6.2 FIFO

- Source is the **active identifiable eligible receipt layer(s)** and split consumptions, including multi-layer partial quantities and current valid reversal/replacement generations.
- Custody transfer must not create new ownership/value; exact eligible cost follows transferred split(s). If existing TR's FIFO writer models split OUT and IN specially, reuse its proof and lineage **without assuming** it works for returning to the original lot or post-conversion.
- Actual BOM consumption must allocate **actual** layer costs to FG generation; unused RM returned to Inventory must trace exact unconsumed original layers/values. Partial split, mixed original GR costs, production and sales usage and subsequent rollback must never double-consume a layer or restore the wrong layer cost.
- Reversal must restore only the exact original active consumption and recompute later allocations where publicly supported; otherwise reject. No `AvgCost` approximation.

### 6.3 Standard Cost

- Retrieve approved effective-dated standard policy **for each input and FG**; require actual currency/UOM precision/approval and existing variance treatment.
- Custody TR is value-neutral (per actual standard transfer method), not repriced as receipt.
- Conversion outputs are valued at approved FG standard; input values, applied fees and FG value difference must reconcile through an **authorized and auditable variance account/effect owner** (or block if absent). Do not forcibly equate input total with FG book value by rewriting standard cost.
- Backdated transactions that cross a standard revision effective date or produce downstream variance must follow the correct historical policy, not today's revision.

### 6.4 Cross-method / cross-item constraints

- BOM could contain materials with **different item-specific methods** and FG with another method. One physical/financial root must reconcile **all involved items and variance**; a sequence of single-item WAC/FIFO/Standard postings is not atomic conversion.
- Currency/FX, pack size, unit conversion, quantity precision, tax/rounding and period valuation must be taken from **existing approved owners**. Do not derive a cost from supplier quote, UI price, BOM standard cost or unsupported expense automatically.
- Test all methods as a **matrix**, not one happy-path case per method. **Absence of a proven cost authority is a hard error**, not zero cost.

---

## 7. Concurrency, permissions, availability and reporting controls

### 7.1 Atomicity and concurrency

- One transaction and **one authoritative ledger root per indivisible Subcon Post/Rollback**. No nested independent inventory `PostAsync` and no background post that commits a later second half.
- Use existing `BranchStockTransactionLock`, deterministic balance/lot lock order, source `rowversion`, unique doc + line and active posting-generation indexes. Plan outside root and **recheck under lock**. Catch unique/rowversion conflicts with retry/reload guidance; no silent last-write-wins.
- Simultaneous: two operators send last 5 units; Receive Back vs consume same supplier allocation; post vs rollback; two jobs with same vendor/item/slice; concurrent Sales/Production issue; concurrent count, PI variance and period close. Exactly one safe winner; losers have **zero effects**.
- Timeouts: business RequestId/fingerprint + sealed active posting lookup; after an uncertain commit the retry must not make a second material movement. `DocumentRevision`/new posting generation on successful rollback/repost keeps audit distinct. Always clear any SQL pooled session posting marker (coordinator pattern).
- On exception, cancellation, duplicate reference, SQL deadlock, missing cost state, insufficient historical slice, or failed value reconciliation: rollback the entire SQL transaction including Subcon status, batch, source quantity, FIFO layers, WIP, PO/Sales effects and material allocations.

### 7.2 Security

- Trusted company and branch scope is server authoritative; cross-company/branch document, supplier, warehouse, balance or posting references rejected.
- `SubconSendOut` / `SubconReceiveBack` permissions: `Access, Add, Edit, Delete, Post, Rollback, Print, ViewCost` where relevant; no POST via draft-save. Normal employee may view quantities without `ViewCost`.
- For custody inventory transfer also require approved Inventory TR posting/rollback authority (both rights by default); for Production WIP also require authorized Production transaction right. Do not let an adapter bypass original owner permissions; agent must document a tested explicit delegated permission alternative if business needs to hide Inventory menu.
- Segregate financial fee approval/override from ordinary warehouse Post. Stamp `CreatedBy/PostedBy/RollbackBy`, operation ID, reason and before/after audit snapshots without exposing sensitive cost data to unauthorized users.

### 7.3 Availability, reservations, holds and stock count

- Supplier-held stock remains company-owned on the balance sheet **but not normally available** for ordinary Sales DO, direct Invoice, MI, IP, automatic reservations, pick/pack, or stock take at normal warehouse.
- Check **every consumer's actual posting**: `IvInventoryPostingService` stock-outs/transfers, Sales DO/direct INV, Production Material Issue, stock reservations, Stock Count, QA physical holds, transfer source pickers and any other current `IvBalLoc` decrease. Stock selection filters alone are insufficient.
- Do not bypass reservation/QA holds, negative-balance enforcement or closed-period snapshots when Subcon is involved. Tenant stock-accounting method and normal warehouse valuation must remain consistent.
- Supplier confirmation / supplier custody count needs a **separate approved reconciliation workflow**; do not silently include remote supplier bins in employee physical stock-count sheets or change frozen stock count observations.

### 7.4 Support / audit / inquiry

- For every Job: show **Sent, At Supplier, Received, Consumed, Scrap/Rejected, Returned Leftover, FG Accepted, Balance, Processing Fee/Cost status** with linked source documents and effective dates. Use per-item/base-UOM quantities and clear labels; do not sum mixed UOM as one number.
- Support inquiry must trace `SubconJob → DispatchLine → Inventory TR batch/history → StockPosting → active valuation/layers → Supplier slice → Receive/Consume → FG/WIP → Sales/PI/QA → reversal`. Show only authorized cost details.
- Add a read-only reconciliation/diagnostic command/report that proves supplier physical qty, Subcon outstanding allocation qty, input/FG value, cost method and active posting generation. It must give business-friendly exceptions and a technical support reference, **not automatic data repair**.

---

## 8. Minimal file/component impact and boundaries

### Existing files that must be inspected, may be changed only with a narrowly approved diff

| Area | Verified / likely affected source | Intended scope, not blanket rewrite |
|---|---|---|
| Subcon service, contracts, messages | `ErpWeb.Core/Subcon/{ISubconApplicationService,ISubconInventorySourceAdapter,SubconApplicationService,SubconCodes,SubconDtos}.cs` | Add validated action/rollback/return/conversion contracts, status/link handling; leave blocked adapters as default |
| Subcon entities/mappings | `ErpWeb.Model/Entities/Subcon/SubconEntities.cs`; `ErpWeb.Model/Configurations/Subcon/SubconConfiguration.cs`; `AppDbContext.cs` | Minimal source/posted-generation links and immutable custody/consumption evidence; migration reviewed tenant-by-tenant |
| Inventory TR save, posting | `ErpWeb.Core/Inventory/{IvStockTransferService,IIvInventoryPostingService,IvInventoryPostingService}.cs` | Extract existing-owner in-transaction TR **without duplicating behavior**; preserve public regular TR behavior, history, cost and rollback |
| Inventory historical paths | `IvInventoryPostingService.WacTransferReplay.cs`; `IvInventoryPostingService.FifoReplay.cs` and **resolve exact Standard TR replay file in current worktree**; `StockReplay*` planners/writers | Only bounded approved Subcon-aware transfer/replay extension; do not assume same transaction support exists |
| V2 posting/valuation | `ErpWeb.Core/StockLedger/{StockPostingCoordinator,StockMovementRegistry}.cs`, `StockLedger/Costing/*`, `StockReplay*` | **Protected**. Only approved new source command / allowed valuation contract with explicit reconciliation and tests |
| Stock eligibility | `ErpWeb.Model/Entities/Inventory/{IvBalLoc,IvWarehouse}.cs`; stock consumers in Inventory, Sales, Production, reservation/Stock Count/QA | Central authoritative supplier-custody exclusion and method-specific checks; no global change to ordinary stock ownership |
| Production WIP | `ProductionStockWriter.cs`, `ProductionOutputService.Posting.cs`, `ProductionWorkOrderTransferService*.cs`, `ProductionFinishedGoodReceiptService*.cs`, `ProductionPoolValuationService.cs` and current lifecycle interfaces | New partial offsite authority/eligibility; **do not** overwrite `ProductionBalLot` directly from Subcon |
| Procurement, fees | Current `PoOrderService`, `PoInvoiceService`, `PoCdnService`, cost settlement services | Map fee to approved commercial/cost owner, preserving PO/PI status, PPV and invoice accounting |
| UI | `ErpWeb.UI/Subcon/{SubconWorkbench,SubconSendOut,SubconSendOutList,SubconReceiveBack,SubconReceiveBackList,SubconInquiry,SubconSettings,SubconPostingBanner}.*` | Current theme, readable action bar, lookup, status, Post/Rollback confirmations; clear blocked reasons; user-friendly no memorized IDs |
| Dependency injection/menu | `ErpWeb.Core/CoreServiceCollectionExtensions.cs`, `MenuCodes.cs`, current menus XML, scripts | Keep blocked default; enable only independently certified per-mode/capability provider, enforce roles |
| Tests | `ErpWeb.Tests/Subcon/*`, `ErpWeb.Tests/Inventory/Transaction/IvStockTransfer*`, `ErpWeb.Tests/Inventory/Transaction/IvStockCount*`, relevant Production/Sales/Purchase/StockLedger tests | Real SQL Server atomicity, period, cost-method, historical replay and cross-module regression, not only SQLite |

**Implementation agent must inspect actual current paths and signatures** before editing. Where this plan names a file family or a proposed API, it is a required discovery or design target, **not proof that the API already exists**.

### Existing behavior agent MUST NOT change without separate user approval

- Existing `IvBalLoc` quantity meaning, unique slice keys, stock history/reversal semantics; standard Inventory GR/MI/MR/TR/ADJ/Stock Count/Post/Rollback; existing `CostLayer`, WAC and Standard algorithms, original-source valuation ownership, rounding/FX.
- Inventory transfer lot restrictions outside the narrowly approved dedicated Subcon return contract; never globally permit existing lot replacement without verified genealogy.
- Sales DO vs direct Invoice physical owner, FIFO/COGS, CN stock returns; PO/GR/PI/CN/PPV; Production IP → material-in/WIP → Daily Output → FG and existing rollbacks.
- QA grant/frozen gate, reservations, permissions, closed periods, future posting dates, historical replay feature flag, Stock Count absolute observation, month-end close snapshots and tenant isolation.
- No manual SQL to fix cost/stock, no silent historical revaluation, no weakening negative-stock or group approval rules, no new independent cost strategies. Never blindly enable `SubconEnabled` or `OpenPeriodBackdatedReplayEnabled` in config/database.

---

## 9. AI Agent execution phases — precise gates and deliverables

> **Sequence matters.** Phases G0–G2 prove the ability to implement safely. Scenarios may go live **individually** only after their own SQL Server full-path gate, not when UI looks finished.

| Gate | Implementer task | Required review artifacts | Go criterion |
|---|---|---|---|
| **G0 — Forensic source & SQL proof, READ-ONLY** | Pin local HEAD/worktree, inspect all current owners, the full `productionv2-costing` skill, active SQL schema/epochs, existing TR historical paths, supplier warehouse consumers and real tests. Build exact per-source method/cost/period/replay/permission table and enumerate unsupported shapes. | Source map with file/line/method; physical + financial dependency diagram; TR in-transaction feasibility POC design; baseline test logs; tenant schema inventory; source-limited vs universal scenario list | **No code mutation or live setting change**. Human approves next gate. |
| **G1 — Supplier custody exclusion & reconciled source identity** | Design supplier WH + allocation tracking, shared server-side consuming guards; prove Sales/MI/IP/count/QA/reservations cannot pick supplier stock. Support multi-job same item and source-lot identity. | Eligibility interception matrix, SQL Server concurrent 2-job tests, per-slice reconciliation & migration plan | No route bypasses custody; zero regression to normal warehouse availability. |
| **G2 — Existing TR transaction seam** | Extract protected TR physical/valuation Post/Rollback into caller-owned transaction while preserving current standalone public route. Carry `StockPosting`/batch ID/active posting links; prove one atomic root, replay routing, error abort. | Focused original TR test baseline vs new in-tx equivalence, crash injection each write stage, SQL Server concurrency and pooled-session cleanup | Single batch/source atomic; no committed partial TR or Subcon state; existing TR unchanged. |
| **G3 — Pilot: non-lot same-SKU Send Out / Receive Back / Rollback** | Wire Subcon inventory adapter + posted lifecycle; partial independent receipts, leftovers, supplier statement; enable behind tenant/mode/capability gate. Historical actions accepted **only for exact proven shapes**. | Normal & backdated WAC/FIFO/Standard results; Qty/value/layer proofs; UI UAT; operator errors; rollback/repost | Go for **non-lot same-item custody only** after independent review. No conversion/WIP/lot yet. |
| **G4 — Actual BOM conversion source / fee policy feasibility** | READ-ONLY proof first: new V2 multi-item command contract, method-specific input/FG lineage, Standard variance, physical allocation, scrap/byproducts/fee/QA, late PI settlement. Define exactly which protected owner code must change. | Detailed schema and command proposal; 2 alternatives only if genuinely viable; Finance approval; method-specific replay graph/coverage | **Separate signed approval before any new costing/ledger mutation.** |
| **G5 — Inventory-only actual assembly / conversion Post & Rollback** | Implement approved source command, component consumption + FG receipt + exact cost/variance once; partials and unused returns; no separate MI/MR commits; price/fee governance. | Full WAC/FIFO/Standard method matrix, input/output cost reconciliation, split FIFO, standard variance, partial/scrap/sale dependencies, SQL Server end-to-end | Go only for enumerated proven conversion shapes. Historical output/consumption remains blocked until method-specific replay proof. |
| **G6 — Production partial offsite WIP source** | Approved partial pool-custody contract; guard Daily Output/FG/WO transfer/returns; optional extra RM; material-in and shared WIP valuation, QA. | Exact offsite pool qty/value proof, WO stage eligibility & role rights, downstream reversal graph and tests | Separate Production owner approval and exhaustive WIP/FG/rollback regression. |
| **G7 — Historical replay expansion (PER METHOD/GRAPH)** | Extend existing public planners/writers only for explicitly required Subcon historical shapes: TR→Count, TR→Sales, TR→Production, multi-line/multi-item, conversion→Sales/PI, WIP descendants, cross-month, >100 events if justified. | Per-graph impact manifest with current support, exact active correction generations, SQL Server history/period/concurrency/rollback tests; cost snapshots | Each graph independently approved & enabled; **unknown remains BLOCKED**. No generic flag to promise all. |
| **G8 — Financial completion & later settlement** | Connect approved service PO/PI/CN/DN fee policy, expense/accrual/capitalization, partial FG and consumed/sold cost changes; show supplier invoice vs value reconciliation. | Finance & tax/UOM policy signoff, PPV/COGS/WIP trace, closed-period behavior, all downstream reversals | No stranded or double capitalized fee; old and new periods reconcile. |
| **G9 — UX, reporting, operations, release** | Final workbench, action buttons, at-supplier KPIs, job close, audit/inquiry/export, migration, help messages, demo/UAT for normal Malaysian SME users. | Operator task timings & screenshots, support SQL trace docs, tenant migration/rollback runbook, CI+SQL Server full suite, feature gate evidence | Explicit release approval per mode/capability/tenant. |

**UI may be drafted before G3, but never display active Post buttons that misleadingly report success.** Current disabled banner is more truthful than fabricated POSTED inventory.

---

## 10. Required integration tests: pass/fail with actual quantities AND values

### 10.1 Exact foundational assertions (all methods)

For every active **physical stock slice** at one effective cutoff:

```
EndingQty = OpeningQty + ActiveReceipts − ActiveIssues
           + ActiveTransferIn − ActiveTransferOut
           + ActiveAdjustments ± ActiveReversals
```

For each relevant **cost method and item pool**:

```
EndingValue = OpeningValue + ProvenValuedReceipts − ProvenValuedIssues
             + ApprovedValueOnlyAdjustments ± ActiveCorrections
```

For WIP: `EndingWipQty/Value = Opening + MaterialIn + Produced/TransferIn − Consumed − FG/TransferOut ± Corrections`, with **different RM vs FG UOM and yield** handled correctly. Verify final live `IvBalLoc`, active V2 physical timeline, `StockCostState`, `StockValuationFact`, FIFO layer balances/consumptions, WIP contribution/lot/movement, FG, COGS, PO settlement, Subcon allocations and reports. Money/qty precision must follow existing policy; **no unexplained reconciliation tolerance**.

### 10.2 Worked basic same-SKU test

Opening: MAIN contains **100 pcs** of A, total verified book cost **RM500** (illustrative flat valuation). SUBCON supplier slice is zero.

| Step | MAIN A | Supplier A | Company total A | Company total value | Required result |
|---|---:|---:|---:|---:|---|
| Before | 100 | 0 | 100 | RM500 | Baseline |
| POST Send Out 100 | 0 | 100 | 100 | RM500 | One TR root, one active Subcon link |
| POST Receive Back 40 | 40 | 60 | 100 | RM500 | One second TR root; supplier outstanding 60 |
| Rollback Receive Back 40 | 0 | 100 | 100 | RM500 | Exact appended reversal, Receive NEW |
| Rollback Send Out 100 | 100 | 0 | 100 | RM500 | Exact appended reversal, Send Out NEW |
| Repost Send Out 100 | 0 | 100 | 100 | RM500 | **New active generation**, no duplicate old facts |

With FIFO input layers **40 @ RM5 + 60 @ RM8**, send 70: expected **transferred provenance RM440** (40×5 + 30×8), and remaining company book value stays equal before/after **subject to authoritative current FIFO TR allocation semantics**. A later 30 return must preserve its **actual transferred cost splits**. If the current owner cannot prove exact transferred split lineage, mark FIFO support **BLOCKED**, not guessed.

### 10.3 Worked BOM-to-FG candidate (illustrative WAC/FIFO-flat input, not Standard posting formula)

A is RM5/unit; B is RM2/unit; send **100 A + 50 B** to supplier (RM600 raw-material value). Supplier actually uses **80 A + 40 B** to make **40 C**. Materials consumed = RM400 + RM80 = **RM480**; unused supplier-held RM remains **20 A + 10 B = RM120**. An independently authorized capitalizable processing fee of **RM80** would make the illustrative FG cost **RM560 / 40 = RM14 each**. **Total post: RM120 remaining RM + RM560 FG = RM680** corresponding to opening RM600 + RM80 approved fee liability/capitalization. If the fee is expensed or FG Standard differs, the approved financial treatment changes—**not** the physical raw/FG facts. Test rollback restores exactly original material layers/slices, removes output valuation/quantity and reverses fee/variance evidence, or blocks when 10 C have been sold and replay cannot reach their COGS.

### 10.4 Mandatory adversarial matrix (each row WAC, FIFO, Standard unless N/A)

| ID | Scenario | Minimum proof / safe refusal |
|---|---|---|
| T01 | `NEW → POSTED → Rollback → POSTED`; duplicate click and post-after-timeout | One active effect, preserved reversed facts, exact qty/value on every cycle |
| T02 | Multi-line (20 lines, 2 items/warehouses), one line short/invalid | **Zero** moved/valued; no partial posted status |
| T03 | Partial dispatch 100 then receive 30+20, leftover 50; 2 jobs sharing supplier bin | Per-job outstanding exact; no cross-job consumption |
| T04 | Lot-controlled material, same/existing lot on return | Initially **BLOCK**; no partial lot/valuation change |
| T05 | FIFO receipt layers at differing costs, partial transfer/return, consumption, reversal | Exact layer provenance and remaining split; no duplicate layer |
| T06 | WAC zero quantity/value, price-only change, GR→PI→CN/DN then dispatch/return | No negative/phantom value; correct current active pool or **BLOCK** |
| T07 | Standard policy revision between dispatch, conversion, return; variance | Effective-date standard + exact variance or **BLOCK** |
| T08 | Historical dispatch before later MI/Scrap/TR/GR; same-month <=supported tail | Exact event-order replay and value reconciliation; otherwise block |
| T09 | Historical dispatch before later Sales DO/direct INV, linked invoice, CN | No broken Sales stock/COGS; exact owner graph replay or **BLOCK** |
| T10 | Historical dispatch before Stock Count, including zero variance/count-to-zero | Preserve absolute count/end-of-day generation; currently **BLOCK** for generic TR |
| T11 | Historical rollback with later Receive Back, FG conversion, scrap or WIP output | Refuse until dependent originals reversed or complete certified replay |
| T12 | Historical action across period close or closed original period | **BLOCK**. All balances/cost/period snapshots unchanged |
| T13 | Two or more later different-cost GRs, PI price variance, vendor CN after material dispatched | Correct on-hand vs consumed WAC/FIFO/Standard settlement, or **BLOCK** |
| T14 | Conversion partial output, later Sales 10 FG, then late supplier service fee | Correct remaining FG + historical COGS plus variance, or **BLOCK capitalization** |
| T15 | Production WIP 1,000; send 300 offsite; receive 200; normal Daily Output/FG attempts extra 100 | Only eligible internal WIP can advance; pool value/qty exact |
| T16 | Two simultaneous Send Out posts contend for last 5 units | One commit; second refuses without stock/cost mismatch |
| T17 | Receive Back vs FG conversion vs Receive Back Rollback races | One legal ordering; no double consumption or negative supplier custody |
| T18 | Out-of-order same-day parent/child effective timestamp and posting sequence | Deterministic order; no receipt before dispatch |
| T19 | Backdated multi-item / mixed-method conversion, >100 events, cross-month open tails | Unsupported matrix => **atomic BLOCK** until certified expansion |
| T20 | Future-dated NEW allowed; future POST, frozen count and QA holds | Existing business-date/freeze/hold guard enforced at Post & Rollback |
| T21 | Duplicate `RequestId` different payload; stale `rowversion`, source UOM/lot changed | Conflict and no effects; no request ID reuse |
| T22 | SQL error/exception at every stage: after quantity, after history, after valuation, after Subcon status, before seal | **Full SQL rollback** and clean pooled-session context |
| T23 | Intercompany, non-stock, consignment, vendor-owned parts, different supplier/branch | Reject unsupported ownership; no illegal inventory asset |
| T24 | Period-close/reopen/reclose + historical as-of inquiry after a legal replay | Accurate current and historical valuation snapshots; no closed-period rewrite |
| T25 | Permission denied, altered tenant/source doc ID, cost-view restriction | No mutation, no cost exposure |
| T26 | FG component scrap, yield, substitutions, byproduct, extra material, currency/FX and rounding | Actual qty/value/variance reconcile or **BLOCK** unsupported subcase |
| T27 | Rollback of 2nd/3rd receive and dispatch, then repost after changed BOM/price | Original active allocations & frozen documents not lost; new generation distinct |
| T28 | Ordinary Inventory/Sales/Procurement/Production posting on non-Subcon stock | No regression to legacy working flows |

**Important:** also run existing Inventory MR/MI/GR/TR/ADJ/Count/Scrap/VR, Sales SO/DO/Invoice/CN, Procurement PO/PI/CN/DN, Production IP/Daily/FG/PRTN/PADJ/WO transfer, QA, reservation and month-end suites. **SQL Server tests** are required for branch locks, UPDLOCK/HOLDLOCK, rowversion, unique indexes and session cleanup. SQLite-only results are insufficient.

---

## 11. Deployment & failure rollback (feature, not transaction)

1. Capture pre-migration **per-tenant** schema, active cost method/epoch, warehouse/supplier mapping, existing draft volume, cost/stock snapshots, backups and baseline test results. Use checked, numbered, non-destructive SQL scripts—not automatic startup migrations.
2. Run migrations with preflight, reproducible verification and safe failure stop; existing drafts remain NEW and unposted until explicitly validated. Never fabricate old history/layers to make Subcon appear previously posted.
3. Capability flags are **separate** for non-lot same-SKU custody, lot return, BOM conversion, Production external WIP, capitalized fee, and each historical replay graph (per method where needed). Flags are **OFF by default** and are enabled only after SQL Server UAT and independent release signoff. A global `SubconEnabled` does not authorize unsupported subroutes.
4. Deploy backend checks **before** revealing Post UI or activating supplier-custody posting. All consumers must honor offsite exclusion first; do not leave a window where supplier stock is available for Sales.
5. Rollout pilot to one approved tenant/branch and non-lot same-SKU Post/Rollback with operator instruction and support-level SQL trace. Gradually extend **only validated shapes**.
6. If rollback of the **software feature** is required after legitimate posted documents exist: disable new posting by capability, preserve all posted historical evidence and reader compatibility, and use **approved business transaction reversal** for stock/cost. Never delete posted Subcon rows, reverse via SQL DELETE, or erase sealed V2 facts. A binary/schema downgrade is only valid if proven data-compatible and rehearsed; otherwise keep the schema and revert UI/feature registration to read-only.

---

## 12. Agent deliverables and explicit sign-off request

The implementing agent must return **in this order**:

1. **Evidence**: current local commit and `git status`, exact source paths/methods and SQL schema; drift from remote pinned HEAD, especially any newer costing changes.
2. **Architecture**: per-operation posting-owner matrix (TR / conversion / Production WIP / Procurement), complete source-to-downstream graph, inventory supplier eligibility guard coverage, and proposed exact protected owner diffs.
3. **Scenario classification**: full T01–T28 × WAC/FIFO/Standard × Post/Rollback/Backdate: `SUPPORTED` (public route + tests), `BLOCKED` (explicit reason, no effects), `UNVERIFIED` (never enable).
4. **Implementation**: gated PRs/commits by G1–G9; no broad modifications to costing owners. Document one root/one transaction per action and active lineage links.
5. **Proof**: before/after physical slices, FIFO allocations, WAC state, Standard variance, WIP, FG/COGS, supplier custody/Job statement, month-end and SQL Server concurrent failure injections. Provide actual test commands, exit codes and skips.
6. **UAT**: real user flow with 20-line BOM kit, partial same-SKU return, leftover, blocked historical case, rollback/repost; professional existing DevExpress styling, minimal clicks and clear confirmations.
7. **GO/NO-GO**: a separate verdict **per capability and per costing/replay shape**. If core transaction-sharing, supplier isolation, method-specific valuation or SQL Server proof fails, status **NO-GO, retain blocked adapters**.

### Approval sought from reviewer/user

- **G0 forensic + G1 design:** May be reviewed now (no stock/cost mutation).
- **G2 TR in-transaction refactor and G3 live non-lot custody:** **NOT APPROVED** without current local source proof, explicit protected interface diff, and all required tests.
- **G4–G8 conversion / WIP / cost settlement / historical expansion:** **Separate high-risk approval required**, because they change authoritative stock/cost/WIP/replay semantics.
- **Overall:** **NOT APPROVED FOR IMPLEMENTATION AS ONE BIG AGENT PROMPT; NO LIVE ACTIVATION.** Execute and independently validate **one gate at a time**. “All scenarios” means no unsafe silent success: unsupported cases are accurately rejected until their full replay path is proven and approved.

---

## Agent handoff prompt (copy to coding agent)

> **Task:** On the CURRENT LOCAL `productionv2` branch, perform **G0 READ-ONLY FIRST** for the attached *ProductionV2 Subcon Posting, Rollback & Historical Cost Replay v11*. Inspect actual checkout (`git status`, HEAD and worktrees), current SQL Server tenant schema, `.agents/skills/productionv2-costing/SKILL.md` and all referenced skills. Trace Inventory TR Post/Rollback, WAC/FIFO/Standard historical replay, Stock Count, Sales stock/COGS, Procurement PI/PPV, Production WIP/FG, QA, reservation, warehouse eligibility and Subcon service/UI. Produce exact file+method evidence for the one-root transaction seam, supplier-held stock isolation, base-UOM/value provenance, full Post/Rollback/Backdate downstream matrix and T01–T28 risk/test evidence. **Do not modify source, database, costing, quantity, ledger, flags, UI Post actions or adapter registrations in G0.** Mark each route SUPPORTED / BLOCKED / UNVERIFIED with reasons. Return proposed minimal G1/G2 diffs and explicit approval request. No invented “all cost methods supported” claim, no 10/10 without real proof.

**End of plan.**
