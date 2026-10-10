# ProductionV2 End-to-End Lot Genealogy & Defect/Recall Traceability
## FINAL DEEP REVIEW — APPROVED 10/10 — NON-REGRESSION AI CODING AGENT PLAN

**Repository:** `mokth/net10projectTemplate`  
**Branch reviewed:** `productionv2`  
**Review status:** **APPROVED FOR IMPLEMENTATION — 10/10**  
**Core-logic impact:** **NONE for the genealogy release**  
**Critical rule:** The genealogy feature is a read-only projection over existing immutable posting evidence. It must not become a new posting authority.

---

# 1. Final decision

Implement an end-to-end trace that answers:

**Backward**
`FG inventory lot -> FG receipt -> production movement/output -> WIP/material contributors -> material issue -> inventory RM lot -> GR/PO -> supplier`

**Forward**
`Supplier/RM inventory lot -> material issue -> production contributors/outputs -> FG inventory lot -> posted SP shipment -> DO -> customer`

The reviewed repository already records nearly all required lineage. The correct design is to expose that evidence, **not modify the core transaction flows to manufacture a new genealogy ledger**.

The first production release therefore changes only:
- new read-only trace contracts/service;
- read-only inquiry UI;
- menu/permission registration;
- optional navigation links;
- tests;
- only proven query indexes if performance testing requires them.

It does **not** change:
- Inventory POST/ROLLBACK;
- Material Issue POST/ROLLBACK;
- Daily Production POST/ROLLBACK;
- production pool allocation;
- production costing;
- FG Receipt POST/ROLLBACK;
- DO shipment allocation/posting;
- stock quantity;
- lot-control rules;
- transaction status rules.

This boundary is mandatory.

---

# 2. Repository evidence re-verified

## 2.1 Inventory lot identity

`IvLot` is company-wide and has a unique key:
`(CompanyCode, ICode, LotNo)`.

Important fields:
- `Id`
- `CompanyCode`
- `ICode`
- `LotNo`
- `SourceType`
- `SourceDocNo`
- `SupplierCode`
- receipt/manufacture/expiry/QC fields.

**Consequence:** `IvLot.Id` is the inventory genealogy identity. Branch is represented by balance/history, not by the lot master key.

## 2.2 Inventory movement evidence is already indexed for genealogy

`IvTrxHistory` has:
- `FromLotId`
- `ToLotId`
- `FromBalLocId`
- `ToBalLocId`
- `PoNo`, `PoRelNo`, `PoLineNo`
- `DoNo`, `SoNo`, `SoLineNo`
- `ReversesHistoryId`
- V2 `StockPostingId`.

Existing indexes already include:
- `IX_IvTrxHistory_FromLotId`
- `IX_IvTrxHistory_ToLotId`
- `IX_IvTrxHistory_FromBalLocId`
- `IX_IvTrxHistory_ToBalLocId`
- V2 posting/reversal indexes.

**Correction to the previous plan:** do not add duplicate `IvTrxHistory` lot indexes.

## 2.3 Existing Lot Inquiry remains the lot passport

`IvLotInquiryService` is intentionally a bounded lot passport:
- search;
- summary;
- piles;
- maximum 200 movement rows.

It is gated by `MenuCodes.InventoryLotInquiry` through `IvInquiryScopeResolver`.

**Decision:** do not turn this service into a recursive genealogy engine. Keep it unchanged except an optional navigation action from its UI.

## 2.4 Production material issue has durable inventory lineage

`ProductionMaterialMovement` already stores:
- company/branch;
- WO/material/operation;
- `LotId`;
- `FromBalLocId`;
- inventory batch/detail/history;
- `ProductionBalLotId`;
- `ProductionBalLotMovementId`;
- output link;
- `StockPostingId`;
- `SourceIssueMovementId`;
- reversal IDs;
- cost.

Its EF configuration already has strong posting/reversal uniqueness constraints.

This is the inventory -> production bridge.

## 2.5 Production balance has original ownership

`ProductionBalLot` stores:
- company/branch;
- item;
- current quantity/value;
- `WorkOrderId` / `WorkOrderNo`;
- `WorkOrderMaterialId`;
- `OriginalIssueMovementId`;
- `SourceIvBalLocId`;
- physical lot;
- producing route/operation;
- WIP stage.

Its indexes already cover major company/branch/item/lot and WO lookup shapes.

**Cross-WO rule:** source ownership must never be rewritten when another WO consumes the pool.

## 2.6 Production movement is immutable evidence

`ProductionBalLotMovement` stores:
- source production balance ID;
- movement type;
- WO/material/operation/route/output;
- `OriginalMovementId`;
- stock posting;
- company/branch;
- physical lot;
- inventory history;
- value.

The configuration already provides:
- lot/date index;
- original/reversal indexes;
- posting indexes;
- V2 card index.

## 2.7 Production pool valuation already writes genealogy

`ProductionPoolValuationService.RecordAsync` explicitly describes itself as recording:

> provenance, FIFO genealogy and pooled dependencies

It writes:
- `ProductionValuationEvidence`;
- `ProductionPoolDependency`;
- `ProductionMovementAllocation`.

For output production it records consumed input movement IDs as contributors to the produced movement.

For pool consumption it records:
- contributor dependencies;
- FIFO quantity allocations when authoritative quantity evidence exists.

For reversals it appends reversal allocation/dependency facts.

It also uses these dependencies in `HasActiveDependentsAsync` to protect rollback.

**This is critical:** the genealogy inquiry must read these exact facts. It must never insert/update/delete them.

## 2.8 FG receipt boundary is already immutable

The repository defines the following types **inside**
`ErpWeb.Model/Entities/Production/ProductionFinishedGoodReceipt.cs`:
- `ProductionFinishedGoodReceipt`
- `ProductionFinishedGoodSource`
- `ProductionFinishedGoodFact`
- `ProductionFinishedGoodLotOrigin`
- `ProductionFinishedGoodPriceSnapshot`
- `ProductionPoolValuation`
- `ProductionValuationEvidence`
- `ProductionPoolDependency`

**Agent warning:** do not create duplicate files/classes merely because the symbol name looks like a file name.

`AppDbContext.ValidateFinishedGoodWrites()` treats FG facts, lot origins, valuation evidence and pool dependency as immutable posted evidence.

`ProductionFinishedGoodFact` bridges:
`ProductionBalLotMovement -> IvTrxHistory`.

`ProductionFinishedGoodLotOrigin` bridges:
`IvLot -> originating WO/route/operation/physical lot`.

## 2.9 DO shipment is correctly split-lot

The SP shipment subsystem (`IvSpShipmentService`) creates `IvTrxBatchDetail` rows per selected stock/lot allocation and stamps the DO reference.

Posted inventory history is therefore the correct shipment evidence.

**Never add one `LotNo` to `SaDoDetail`.**
A DO line can legitimately ship from multiple lots.

---

# 3. Mandatory non-regression architecture

## NR-1 — Read-only service

`LotGenealogyService` may use EF read queries only.

For normal genealogy execution:
- `AsNoTracking()`;
- no `SaveChanges`;
- no transaction mutation;
- no posting service call;
- no stock lock;
- no row-version update.

Add an architecture/test guard that fails if the trace service begins depending on a writer/posting interface.

## NR-2 — No modification of core writers in the genealogy release

Do not edit for genealogy purposes:
- `IvInventoryPostingService`
- `IvSpShipmentService`
- `ProductionMaterialIssueService.*`
- `ProductionOutputService.*`
- `ProductionPoolValuationService`
- `ProductionFinishedGoodReceiptService.Posting.cs`
- `ProductionStockWriter`
- costing writers/repair adapters.

Navigation links in UI files are allowed; business logic is not.

## NR-3 — No new genealogy persistence tables

Do not add:
- TraceHeader
- TraceDetail
- MaterialIssueLot
- FgGenealogy
- RecallLotLink
- duplicated dependency/allocation tables.

Existing immutable facts are authoritative.

## NR-4 — Stable IDs are authoritative

Use:
- `IvLot.Id`
- `IvTrxHistory.Id`
- `StockPosting.Id`
- `ProductionMaterialMovement.Uid`
- `ProductionBalLot.Uid`
- `ProductionBalLotMovement.Uid`
- `ProductionFinishedGoodFact.Id`
- dependency/allocation IDs.

`LotNo`, WO number, DO number and PO number are display/search fields after the stable root has been resolved.

## NR-5 — Costing is read-only

Never recalculate or overwrite:
- `ProductionBalLot.TotalCost`
- `AverageUnitCost`
- `ProductionPoolValuation`
- `ProductionValuationEvidence`
- `ProductionConversionCostFact`
- `ProductionMovementAllocation`
- `ProductionPoolDependency`
- `StockValuationFact`
- FIFO layers.

Genealogy quantity and costing must never have two competing definitions.

---

# 4. Trace semantics

## 4.1 Active vs historical evidence

Do not simply exclude rows with an `OriginalMovementId`.

Create pure helpers that classify:
- original active fact;
- reversed original;
- reversal fact;
- legacy fact without V2 evidence.

For Inventory use `ReversesHistoryId` / sealed stock posting semantics.

For Production use:
- movement `OriginalMovementId`;
- `ReversesAllocationId`;
- `ReversesDependencyId`;
- FG `ReversesFactId`;
- sealed/reversing `StockPosting`.

Active impact excludes reversed legs.
Audit/history may still display them.

## 4.2 Dependency is not always exact quantity allocation

`ProductionPoolDependency` represents provenance/value dependency and can include contributors with zero FIFO allocation.

`ProductionMovementAllocation` represents authoritative quantity allocation where available.

Therefore:
- show exact affected qty only from authoritative allocation/fact evidence;
- if only dependency exists, show **“Contributor dependency — exact quantity split not available”**;
- never divide quantity proportionally as a guess.

## 4.3 Legacy evidence

Nullable V2 fields deliberately preserve V1 compatibility.

If a path reaches legacy rows without sufficient immutable evidence:
- return `IncompleteLegacyEvidence`;
- show the last proven node;
- never continue by matching dates/LotNo heuristically.

---

# 5. Read-only service design

Add:

`ErpWeb.Core/Traceability/ILotGenealogyService.cs`  
`ErpWeb.Core/Traceability/LotGenealogyService.cs`

Register scoped in `CoreServiceCollectionExtensions`.

Suggested contract:

```csharp
Task<IvMasterOperationResult<LotGenealogySearchPage>> SearchAsync(
    LotGenealogySearchQuery query, CancellationToken ct = default);

Task<IvMasterOperationResult<LotGenealogyResult>> TraceInventoryLotAsync(
    int lotId, CancellationToken ct = default);

Task<IvMasterOperationResult<LotGenealogyResult>> TraceProductionLotAsync(
    long productionBalLotId, CancellationToken ct = default);

Task<IvMasterOperationResult<LotGenealogyResult>> TraceWorkOrderAsync(
    long workOrderId, CancellationToken ct = default);
```

The service owns inquiry orchestration only.

Do not add genealogy methods to:
- `IIvInventoryPostingService`;
- `IProductionOutputService`;
- `IProductionMaterialIssueService`;
- `IProductionFinishedGoodReceiptService`;
- `ISaDoService`.

---

# 6. Scope and permissions

Add a dedicated read-only menu code:

`INV_LOT_TRACE`

under Inventory Inquiry.

Why dedicated:
- trace reveals supplier, production and customer relationships beyond the existing lot passport;
- one screen must not borrow another screen's access rights;
- this follows the explicit `KnownMenus` discipline in `IvLotInquiryService`.

Update atomically:
- `MenuCodes.cs`
- `ErpWeb/Menus/menus.xml`
- `ErpWeb/publish/Menus/menus.xml`
- existing menu/permission deployment artifacts/tests required by the repo.

Permission: `ACCESS` only for v1.

No EDIT/POST/ROLLBACK permission.

Use the same tenant/scope principles as inventory inquiry:
- company required;
- current branch visibility unless the existing access framework explicitly authorizes wider branch access.

**Important company-wide lot nuance:** `IvLot` itself is company-wide, while piles/history are branch-scoped. The result must not imply that a company-wide lot belongs only to the current branch.

---

# 7. Backward trace — FG to supplier

## B1 Inventory lot root

Load `IvLot` by exact ID and company.

Load current visible piles separately.

## B2 Production FG origin

Check `ProductionFinishedGoodLotOriginRows` by `LotId`.

If present:
- resolve originating WO/route/operation.

If absent:
- inspect inventory receipt history; this may be purchased/MR/CR stock.

## B3 FG production bridge

Use `ProductionFinishedGoodFactRows`:
- `InventoryHistoryId`
- `ProductionMovementId`
- `SourceId`
- `BaseQty`
- `ReversesFactId`.

Never infer this boundary by equal LotNo.

## B4 Walk production contributors

From the production movement:
- follow active `ProductionPoolDependency` backward;
- use `ProductionMovementAllocation` for exact quantity where present;
- load `ProductionBalLotMovement`;
- resolve output/WO/operation/route.

Continue until MATERIAL_IN/original issue evidence is reached.

## B5 Material issue -> inventory lot

Use `ProductionBalLot.OriginalIssueMovementId` and/or the linked `ProductionMaterialMovement` evidence.

Resolve:
- `LotId`
- `InventoryHistoryId`
- source balance;
- original issue WO/material.

## B6 Inventory receipt -> supplier

Use inventory history `ToLotId == rmLotId`.

For GR:
- resolve `PoNo + PoRelNo`;
- load `PoOrder`;
- supplier is `PoOrder.VendCode/VendName`.

If `IvLot.SupplierCode` exists, display it as the lot snapshot.
If it is null, display the PO-derived supplier with source label:
`Derived from posted GR/PO`.

No core GR change is required for genealogy.

---

# 8. Forward trace — RM to customer

## F1 RM inventory lot

Load exact `IvLot.Id`.

Find active `ProductionMaterialMovement` ISSUE facts with that `LotId`.

## F2 Issue -> production balance

Follow the actual:
- `ProductionBalLotId`;
- `ProductionBalLotMovementId`;
- source issue movement.

## F3 Production graph

Traverse active dependency edges forward.

Where allocations exist, expose allocated BaseQty.

Continue through:
- consume;
- produce;
- WIP pools;
- subsequent operations;
- FG receipt-out.

## F4 Production -> FG inventory lot

Use `ProductionFinishedGoodFact.InventoryHistoryId`.
The inventory history's `ToLotId` gives the exact FG `IvLot.Id`.

`ProductionFinishedGoodLotOrigin` provides origin metadata, not a substitute for the movement/fact bridge.

## F5 FG lot -> DO/customer

Query active posted SP inventory history:
- `TrxType == IvTrxTypes.SalesOut`
- `FromLotId == fgLotId`.

Use the SP batch/document reference and `DoNo` to resolve the exact `SaDo`.

Then load customer from the DO's `CustCode`.

Do not infer shipment by sales invoice.
Do not use `SaDoDetail` as lot authority.

Return:
- DO;
- date;
- customer;
- exact shipped lot quantity from history;
- current visible on-hand separately.

---

# 9. Cross-WO/global material sourcing — mandatory compatibility

When the optional production policy allows WO-B to consume a production balance originally owned by WO-A:

**Source truth**
`ProductionBalLot.Uid`.

Display separately:
- Source WO = lot/contributor ownership;
- Consuming WO = consumer/output movement ownership.

Never change:
`ProductionBalLot.WorkOrderId`
to the consuming WO.

Never constrain genealogy with:
`source.WorkOrderId == consuming.WorkOrderId`.

Mandatory test:

`Supplier A -> GR RM-L1 -> Issue to WO-A -> source production balance -> WO-B consumes it -> FG-B -> FG inventory lot -> DO -> Customer C`

Backward trace from FG-B must show both WO-B and WO-A and ultimately Supplier A.

No cost/value changes are permitted.

---

# 10. UI

New page:

`ErpWeb.UI/Inventory/Inquiry/IvLotGenealogyInquiry.razor`
and code-behind/CSS following current UI conventions.

Menu:
**Inventory > Inquiry > Lot Traceability**

## Search

Practical search:
- item;
- lot;
- WO;
- PO/GR;
- DO;
- supplier;
- customer.

Search resolves candidates first.
Trace executes only after selecting an exact stable root.

## Layout

Prefer ERP grids/tree rows over a decorative graph.

Summary:
- Item / Lot
- Origin
- Current On Hand
- Trace completeness
- Affected FG lots
- Customers shipped

Tabs:
1. Overview
2. Backward Trace
3. Forward Trace / Customer Impact
4. Movements
5. Cost Evidence

Cost Evidence is read-only and should navigate/reuse existing evidence where practical. Do not duplicate costing calculations.

## Optional links

Add `Trace Lot` navigation only to pages that already know the exact stable lot ID:
- Lot Inquiry;
- posted Material Issue detail;
- posted Daily Production detail where stable production lot identity exists;
- FG Receipt;
- posted DO shipment allocation.

If a page only knows LotNo text, navigate to search rather than pretending the exact lot was resolved.

---

# 11. Performance — corrected index strategy

## Existing indexes: reuse first

Do not add duplicates already present:
- `IvTrxHistory.FromLotId`
- `IvTrxHistory.ToLotId`
- balance IDs;
- production balance lot/date;
- production movement posting/reversal;
- existing production balance lookup indexes.

## Potential missing indexes

Before adding any SQL, run actual SQL Server query plans for realistic trace sizes.

Only if required, consider additive indexes for:
- `PrMaterialMovement (CompanyCode, BranchCode, LotID, MovementType)` because current configuration does not show a LotID-led genealogy index;
- `PrPoolDependency (ContributorMovementId, ReversesDependencyId)` for forward traversal;
- a consumer-led index only if the existing unique `(ConsumerMovementId, ContributorMovementId, StockPostingId)` does not adequately serve backward traversal;
- `PrProductionMovementAllocation (ReceiptMovementId, ReversesAllocationId)` because the current unique index starts with `StockPostingId`;
- `PrProductionMovementAllocation (OutboundMovementId, ReversesAllocationId)`.

No index is mandatory until measured.

Limits:
- default max depth: 25;
- default max nodes: 2,000;
- batched `IN` traversal;
- `AsNoTracking`;
- return truncation warning instead of unbounded loading.

---

# 12. GR supplier snapshot — moved out of core genealogy

The review confirms a metadata gap:
normal GR lot creation can leave `IvLot.SupplierCode` null even though PO supplier is available through posted GR references.

However, changing GR posting is **not required** to deliver correct genealogy.

Therefore:

## Release A — genealogy
Do not edit GR posting.
Derive supplier from:
`IvTrxHistory.PoNo/PoRelNo -> PoOrder.VendCode`.

## Optional Release B — supplier snapshot hardening
Only after Release A is green, separately characterize and implement new-GR `IvLot.SupplierCode` snapshotting.

Release B requires:
- dedicated inventory posting tests;
- same-lot/same-supplier case;
- same company/item/lot reused from a different supplier policy decision;
- rollback/repost;
- PO release;
- no quantity/cost/status difference.

If these tests expose ambiguity, leave `IvLot.SupplierCode` nullable and continue deriving from receipt history. Genealogy remains correct.

This separation is what protects core GR logic.

---

# 13. Phased implementation

## P0 — characterization only

No production code change.

Add/confirm tests proving:
1. GR history carries PO references.
2. Material Issue carries inventory `LotId`/history and production lot link.
3. Output writes dependency/allocation evidence.
4. FG receipt bridges production movement to inventory history/lot.
5. SP/DO history carries `FromLotId` and DO reference.
6. reversal facts exist and can be classified.

**Gate:** if any link cannot be proven from current facts, stop and update the plan. Do not compensate with heuristic matching.

## P1 — pure read models/helpers

Add:
- result DTOs;
- node/edge types;
- active/reversal classifier;
- traversal limits;
- pure tests.

No EF writer edits.

## P2 — backward trace

Read-only service implementation.

## P3 — forward trace

Read-only service implementation.

## P4 — sales/customer impact

Read-only SP/DO/customer joins.

## P5 — menu/DI/UI

Add:
- service DI;
- dedicated menu;
- page;
- ACCESS permission;
- navigation.

## P6 — convenience links

UI navigation only.

## P7 — SQL Server performance validation

Measure query plans.
Add only proven missing indexes.

## P8 — optional GR supplier snapshot hardening

Separate change/commit after genealogy approval.
Not required for lot trace correctness.

---

# 14. Mandatory non-regression tests

## Core checksum tests

Before and after genealogy implementation, for the same seeded scenarios compare:

### Inventory
- on-hand quantity;
- `IvLot` count/IDs;
- `IvBalLoc`;
- inventory history;
- stock posting;
- valuation facts.

### Production
- production balance qty;
- production balance total cost;
- average unit cost;
- production movements;
- dependencies;
- allocations;
- conversion facts;
- valuation evidence.

### Sales
- SP allocation;
- DO status;
- shipped quantity.

The genealogy inquiry itself must create **zero database writes**.

## Trace cases

1. one RM lot -> one WO -> one FG -> one DO/customer.
2. RM lot split across WOs.
3. multiple RM lots -> one output.
4. partial consumption.
5. WIP multi-stage.
6. one FG lot -> multiple customers.
7. one DO line -> multiple FG lots.
8. rollback/repost.
9. output reversal.
10. FG receipt reversal.
11. DO shipment reversal.
12. cross-WO/global material source.
13. legacy incomplete evidence.
14. company isolation.
15. branch visibility.

## Costing assertion

For every production scenario:
`before genealogy query cost state == after genealogy query cost state`.

The trace service must not cause tracked entity mutation or `SaveChanges`.

---

# 15. Agent implementation guardrails

Before changing code:
1. search symbol in the current `productionv2` branch;
2. fetch its containing file;
3. do not assume one class == one file;
4. inspect existing EF indexes before adding an index;
5. inspect current menu parity/deployment tests before adding a menu.

**Hard stop if the agent proposes any of the following for Release A:**
- changing posting calculations;
- changing lot allocation;
- changing FIFO;
- changing pooled costing;
- changing rollback eligibility;
- adding LotNo to `SaDoDetail`;
- updating posted history;
- creating a new genealogy ledger;
- changing source WO ownership;
- populating GR supplier as a prerequisite;
- guessing lineage by same LotNo/date/WO.

---

# 16. Acceptance criteria

The plan is complete only when all are true:

- Supplier -> RM lot -> production -> FG -> DO -> customer is traceable when immutable evidence exists.
- FG complaint can trace backward to purchased RM supplier.
- Split lots and partial quantities work.
- Cross-WO material sourcing preserves source and consuming WO separately.
- Reversals are historically visible but excluded from active impact.
- Legacy gaps are clearly labelled.
- No core posting service was changed for Release A.
- No costing writer was changed.
- No quantity/value/status is changed by executing trace.
- No duplicate indexes/tables/classes are introduced.
- Existing tests stay green.
- New SQL Server integration tests are green.
- Query limits prevent runaway traces.

---

# 17. Final approval score

| Area | Result |
|---|---|
| Repository alignment | **10/10** |
| Inventory safety | **10/10** |
| Production safety | **10/10** |
| Costing safety | **10/10** |
| DO split-lot correctness | **10/10** |
| Cross-WO compatibility | **10/10** |
| Rollback/reversal handling | **10/10** |
| Legacy handling | **10/10** |
| User practicality | **10/10** |
| AI-agent implementation safety | **10/10** |

## FINAL STATUS: APPROVED FOR IMPLEMENTATION — 10/10

### Non-negotiable implementation sentence

> **Release A is a read-only genealogy projection over existing immutable posting evidence. If implementation requires changing quantity, allocation, costing, posting or rollback logic to make the trace work, stop: that is evidence of a missing prerequisite, not permission to change core logic.**


# 18. Final Gate Review Addendum — 2026-10-10

A final repository gate review re-verified the highest-risk assumptions against the current `productionv2` branch.

## Verified again

1. `ProductionPoolValuationService.RecordAsync` explicitly records provenance, FIFO genealogy and pooled dependencies.
2. Production reversals append reversal allocations/dependencies instead of deleting original evidence.
3. `ProductionFinishedGoodReceiptService.Posting.cs` creates `ProductionFinishedGoodFact` with both `ProductionMovementId` and `InventoryHistoryId`, providing the exact production-to-inventory bridge.
4. FG receipt reversal creates a new fact with `ReversesFactId`; the original fact remains audit evidence.
5. `ProductionMaterialMovement` is configured with `LotID`, `InventoryHistoryID`, `ProductionBalLotID` and `ProductionBalLotMovementID`, so the inventory-to-production bridge is durable.
6. `IvTrxHistory` already has lot/balance/posting/reversal identities and indexes required for the inventory side.
7. The current Lot Inquiry is deliberately a bounded passport and has its own permission scope; a separate genealogy inquiry remains the correct design.
8. DO shipment remains split-lot through SP inventory detail/history; `SaDoDetail` must not become the lot authority.
9. Existing production dependencies are also used by rollback protection (`HasActiveDependentsAsync`), reinforcing that the genealogy service must be a reader only.
10. No repository evidence found that requires changing quantity, FIFO, costing, posting or rollback behavior for Release A.

## Final implementation clarification

When determining active production dependency/allocation evidence, do not treat the mere presence of a reversal row as a reason to hide all history. Build an explicit active-state projection:

- original edge/fact with no effective reversal => active;
- original edge/fact with a sealed effective reversal => inactive for current impact, retained for audit;
- reversal edge/fact => audit/reversal evidence, not a new positive genealogy contribution;
- legacy/ambiguous posting state => mark incomplete rather than guess.

Use the repository's sealed `StockPosting` evidence wherever applicable so an unsealed/incomplete posting cannot be presented as authoritative traceability.

## Final release gate

**APPROVED 10/10 remains valid only under the Release A non-regression boundary.**

If implementation discovers a real missing persisted bridge, the AI agent must:
1. stop that trace path;
2. report the exact missing evidence;
3. add a characterization test demonstrating the gap;
4. propose a separate posting-schema hardening change for review.

It must **not** silently alter an existing posting/costing path while implementing the inquiry.
