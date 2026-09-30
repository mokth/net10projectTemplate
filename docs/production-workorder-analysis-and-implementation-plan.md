# Production Work Order — reverse-engineering and implementation plan

## Executive summary

The legacy ProductionPlan module is not a single Work Order page. `WorkOrder.aspx` creates a **schedule/work-order snapshot** from a product definition, then separate issue-to-production, daily-production/WIP, and finished-goods documents move stock and change operational state. The important legacy split is:

```text
PrDef* (product definition/BOM/routing) --copied on Process--> PrSch* (work-order snapshot)
                                                               |
                                                               +--> IvTrxBatch / IvTrxBatchDetail
                                                                     |                 |
                                                          IvBalance / IvBalLoc       WIPItemBal / WIPItemBalLoc
                                                                     |
                                                               IvTrxHistory
```

The snapshot conclusion is confirmed: `WorkOrder.GetProDefMas` reads `PrDef*`; `ProcessNewWithTime` constructs and saves `PrSchMas`, `PrSchWCenter`, `PrSchProcess`, `PrSchBOM`, `PrSchMachine`, and `PrSchLabour`. `GetProcessTable` subsequently loads the latter tables—not the product definition—when opening an existing WO. The user can re-process and edit parts of the snapshot before saving, so it is not immutable in the legacy application.

The new system should retain the snapshot principle, but make it explicit, revisioned, auditable, and protected after release. It should not reproduce the Web Forms page/session-`DataTable` architecture or its embedded SQL.

## Scope, evidence, and limits

The following repository areas were reviewed:

| Area | Key evidence |
|---|---|
| WO entry, generation and persistence | `ProductionPlan/ProdPlan/WorkOrder.aspx(.cs)` |
| WO/DR variation and listing | `WorkOrderDR.aspx.cs`, `WorkOrderView.aspx(.cs)`, `WorkOrderViewDR.aspx(.cs)` |
| Product definition/routing/BOM | `ProdDefination.aspx(.cs)`, `ERPClasses/BL/ErpDataClasses.dbml` |
| Material issue | `IssueToProduction.aspx.cs`, `ProductionPlan/Helper/IssueToProdHelper.cs` |
| Output/WIP production | `DailyPrdOutPut.aspx.cs`, `ProductionPlan/Helper/DailyProdHelper.cs`, `DailyProdPostHelper.cs` |
| FG receipt and reversal | `FinishGoodRec.aspx.cs`, `ProductionPlan/Helper/FinishGoodPostHelper.cs` |
| Inventory posting | `ERPClasses/Classes/CPosting.cs` via the posting helpers |

The workspace contains no `.razor` files and no ASP.NET Core/Blazor project file. Therefore **New ERP Architecture Findings are UNCONFIRMED**; the new-side design below is an integration blueprint, not a claim about existing services or conventions. No SQL stored-procedure call was confirmed in the reviewed workflow: the legacy code uses inline SQL and `BaseADOERP`/`CADOS` table adapters. A live database schema was not available, so DBML definitions are treated as the source for the primary keys it declares and not as proof of every production-database constraint.

## Old ERP architecture findings and dependency map

### UI and code responsibilities

| Function | Legacy UI | Implementation responsibility |
|---|---|---|
| Create/edit/reprocess WO | `WorkOrder.aspx`, `WorkOrder.aspx.cs` | Loads product definition; processes its routing; saves `PrSch*`; allocates delivery-request balance. |
| WO listing/inquiry | `WorkOrderView.aspx`, `WorkOrderViewDR.aspx`, `Reports/WorkOrderListing.aspx` | Search/display entry points. Detailed filter/query audit remains required. |
| Delivery-request WO | `WorkOrderDR.aspx`, `WorkOrderDR.aspx.cs` | Variant of the snapshot/generation flow with DR context. |
| Issue material | `IssueToProduction.aspx`, `IssueToProdView.aspx` | Builds an `IvTrxBatch` issue document from WO BOM data and lots. |
| Record WIP/output | `DailyPrdOutPut.aspx`, `DailyProductionStd.aspx`, `DailyInputEntry.aspx` | Creates output batches from WIP and routes them to a subsequent process. |
| Receive finished goods | `FinishGoodRec.aspx`, `FinishGoodRecView.aspx` | Posts `FG`, decreases WIP, increases inventory, and may close the WO. |
| Reschedule | `WorkOrderReSchedule.aspx`, `Reschedule/WorkOrderRescheduling.aspx` | Scheduling UI; needs a separate migration investigation. |
| Print/report | `Reports/WorkOrderListing.aspx`, production report projects | Report source/name identified; report data queries need a dedicated pass. |

### Dependency map

```text
IvMas + PrDefMas
  └─ PrDefWCenter → PrDefProcess → PrDefBOM / PrDefMachine → PrDefLabour
       └─ WorkOrder.ProcessNewWithTime
            └─ PrSchMas → PrSchWCenter → PrSchProcess → PrSchBOM / PrSchMachine → PrSchLabour
                 ├─ PrSchMacMain (machine schedule projection)
                 ├─ PrSchDR → SaDeliveryRequest → SaSODetail.WorkOrderNo
                 ├─ Issue document (IvTrxBatch, IvTrxBatchDetail) → inventory posting
                 ├─ Daily output → WIPItemBalLoc / WIPItemBal
                 └─ FG receipt → IvBalance, IvBalLoc, IvTrxHistory and WO status
```

`WorkOrder.aspx` exposes five editable snapshot grids: Centre, Process, BOM, Machine, and Labour. Its client callbacks call `PROCESS`, `REPROCESS`, `NORMALIZE QUANTITY`, and `SAVE`; business logic is inside page code-behind rather than a service boundary.

## Old database tables and relationships

| Legacy table/view | Confirmed purpose and important fields | Key/relationship evidence | Created/updated by |
|---|---|---|---|
| `PrDefMas` | Product-definition master: `ICode`, description, `StdBatchSize`, UOM, `TotalTime`, remark. | PK `ICode`; DBML associations to routing children. | Read by `GetProDefMas`; maintained by product-definition UI. |
| `PrDefWCenter` | Routing work-centre item and sequence: `ProdCode`, `WCCode`, `ICode`, `SeqNo`, `StdPackSize`. | DBML PK `(ProdCode, WCCode, ICode)`, FK-like association to `PrDefMas`. | Copied to `PrSchWCenter`. |
| `PrDefProcess` | Process sequence, setup/operation loss quantities, final-process flag, remark. | DBML PK `(ProdCode, WCCode, WCICode, ProcessCode)`. | Copied to `PrSchProcess`. |
| `PrDefBOM` | Component, standard quantity/UOM/warehouse and default flags. | DBML PK `(ProdCode, WCCode, WCICode, ProcessCode, ICode)`. | Selected only where `BomDefault=1`, then copied to `PrSchBOM`. |
| `PrDefMachine` | Machine, cycle/conversion/startup/queue time, sequence and default. | DBML PK includes product/routing/process/machine. | Default machine rows copied to `PrSchMachine`. |
| `PrDefLabour` | Labour code/cost tied to machine/routing. | DBML PK includes product/routing/process/machine/labour. | Copied to `PrSchLabour`. |
| `PrSchMas` | WO header: `(ScheCode, RelNo)`, status, product, planned qty/UOM, dates, delivery/consignment/desire qty, remark. | DBML declares PK `(ScheCode, RelNo)`. | `AddUpdateMasterSch`, FG close/open helper. |
| `PrSchWCenter`, `PrSchProcess`, `PrSchBOM`, `PrSchMachine`, `PrSchLabour` | Per-WO routing and BOM snapshots, including scheduled dates. | Joined by `ScheCode`, `RelNo`, work centre/item/process in `GetProcessTable`. | Built in `ProcessNewWithTime`; batch-edited in `WorkOrder`; saved atomically with header. |
| `PrSchMacMain` | Machine scheduling projection with planned dates, qty, completed state. | Loaded/inserted by `SetPrSchMacMainRecord`. | Created at WO save. |
| `PrSchDR` | WO-to-delivery-request allocation. | Read by WO save and FG helper. | Updated with DR allocation. |
| `SaDeliveryRequest` | Demand source and `BalanceQty`/status. | Linked by delivery number + revision through `PrSchDR`. | WO creation reduces balance; FG completion updates status. |
| `SaSODetail` | Sales order link. | `UpdateSOWorkOrderNo` writes `WorkOrderNo`. | WO save. |
| `IvTrxBatch`, `IvTrxBatchDetail` | Inventory transaction document header/detail, with `BatchStatus` and types such as `FG`. | Issue/FG/output screens create and save these. | Draft save, then posting helper. |
| `IvBalance`, `IvBalLoc`, `IvBalLocCost`, `IvTrxHistory`, `IvTrxHistoryRollback` | Inventory balance, lot/location balance, cost layers/history, reversal history. | Opened and updated by `FinishGoodPostHelper`/`CPosting`. | Inventory post/rollback. |
| `WIPItemBal`, `WIPItemBalLoc` | WIP aggregate and lot-level balance; fields include WO, routing, lot, qty/UOM, transaction type/date. | DBML supplies fields; Daily and FG helpers query/update it. | Daily-output and FG posting/reversal. |
| `PrShift`, `PrShiftCalendar`, `PrPreventive`, `PrProcess` | Manufacturing calendar, downtime and stock-process configuration. | Used by `ProcessNewWithTime`. | Scheduling master data. |
| `PrScheduleNum`, `AdSmNumDate`, `AdSmNum` | WO/reference document numbering. | Used by `getScheduleNo` and batch helpers. | Incremented with WO/transaction creation. |

**Unconfirmed:** database-level FKs for the `PrSch*` family, some exact `PrSch*` primary-key/index definitions, and all live-schema triggers. The reviewed DBML/adapters confirm the principal fields for `PrSchDailyHdr`, `PrSchDailyPlan`, `PrSchDailyProcess` and `PrSchDailyProd`; verify those definitions against the live database before migration.

## Old Work Order workflow and lifecycle

### Confirmed behaviour

1. A manual WO begins with a selected product definition; a WO can also be initiated from one or more Delivery Requests. `LoadDRInfo` rejects mixed product codes and DR lines whose outstanding quantity is zero.
2. `GetProDefMas` loads current product-definition master/routing and selects only default BOM and default machine lines.
3. **Process** calls `ProcessNewWithTime`: it creates schedule detail snapshots, calculates planned quantities/dates, and requires a machine unless the process is configured as a stock process.
4. **Save** (`WorkOrder.Save`) validates an existing product and at least one centre with start date, creates/uses the WO number, stores `PrSch*` and `PrSchMacMain` in one SQL transaction, reduces linked DR balances, and writes the WO number to the linked sales-order detail.
5. The UI sets initial header status to `RELEASE`; new/header mode nomenclature also contains `NEW`, and transaction documents use `NEW`/`POSTED`.
6. A material issue is a separate inventory batch; `IssueToProduction.Save` refuses an already `POSTED` batch.
7. Daily production takes eligible WIP and creates inventory batches; the process helper verifies predecessor routing/WIP and final-process flags.
8. FG receipt posts an `FG` inventory batch, removes WIP (not beyond available WIP), creates inventory history/balance changes, writes daily-production records, and calls `CloseSchedule`.
9. `FinishGoodPostHelper.checkScheClose` sums WO daily-production rows of `TrxType='FG'` and `GoodQty`; standard FG posting closes the WO when that total reaches `PrSchMas.ScheQty`, otherwise it leaves the WO `IN PROGRESS`.
10. FG rollback calls inventory rollback, restores WIP/daily records and calls `OpenSchedule`. It writes audit-log records.

### Status/lifecycle assessment

| State/action | Legacy evidence | Recommended new rule |
|---|---|---|
| Draft | **UNCONFIRMED.** UI defaults to `RELEASE`; page also contains `NEW` checks. | Use explicit `Draft`; no stock/WIP transaction permitted. |
| Released | `ddlStatus = "RELEASE"`; machine-main is inserted as `Release`. | Snapshot frozen; issue and production allowed. |
| In progress | FG helper writes DR `IN PROGRESS`; DR allocation writes `IN PROCESS`. WO header transition is not fully confirmed. | First posted issue/production moves WO to `InProgress`. |
| Completed/closed | Daily production can mark the final process operationally complete; standard FG `CloseSchedule` closes when summed FG `GoodQty` reaches `PrSchMas.ScheQty`. Variant-path equivalence is **UNCONFIRMED**. | Separate `Completed` (operational) from `Closed` (financially/inventory settled). |
| Cancel/reopen | FG rollback reopens the schedule. No controlled WO cancellation method was confirmed in `WorkOrder`. | Cancellation only when no posted descendants, otherwise reversal workflow. |

Legacy allows sizeable editing before save and supports `REPROCESS`/`NORMALIZE QUANTITY`; post-release edit controls are not a trustworthy state machine. Do not carry this ambiguity forward.

## BOM logic and calculation specifications

### Confirmed legacy formulas

| Calculation | Formula / behaviour | Source |
|---|---|---|
| Centre planned qty | `CentreScheQty = HeaderScheQty × (CentreStdPackSize / HeaderStdBatchSize)` | `WorkOrder.aspx.cs`, `ProcessNewWithTime`. |
| BOM requirement | `round(BomStdQty / CentreStdPackSize × CentreScheQty, 4)` | same method, BOM construction. This simplifies to `BomStdQty × HeaderScheQty / HeaderStdBatchSize` when the same centre factors are used. |
| Tolerance | `toleranceQty = round(stdQty × tolerance / 100, 4)` is calculated but deliberately **not added** to required qty. | `ProcessNewWithTime` comment dated 2022. |
| Machine cycle minutes | `CycleTime / CentreStdPackSize × (CentreScheQty / numberOfDefaultMachines) + ConversionTime + StartupTime + QueueTime` before calendar scheduling. | `ProcessNewWithTime`. |
| Saved machine time | Cycle is converted from minutes to hours (`/60`) for schedule-machine persistence; conversion/startup/queue values are also persisted in hour form. | `ProcessNewWithTime`. |
| Labour planned cost | `LabourCost × HeaderScheQty`. | `ProcessNewWithTime`. It is not confirmed whether this is currency, rate, or a true cost roll-up. |
| Normalize quantity | Existing centre qty, BOM standard qty, cycle time and labour cost are divided by header scheduled qty; conversion/startup/queue time is retained. | `NormalizeQuantity`. |
| Remaining issue qty (simple loader) | `max(StdQty - IssuedQty, 0)`, rounded to four decimals. | `IssueToProdHelper.LoadBOM`. |

The legacy system supports decimal BOM quantities at four decimal places in the confirmed requirement calculation. It has catch-weight fields (`IvMas.DefCatchWt`, `CatchWtUOM`) and a client-side `catch weight × keyed quantity` calculator, but end-to-end UOM conversion and rounding rules were not confirmed in the reviewed posting code.

### What is not confirmed

The reviewed code does not prove a universal BOM output quantity, UOM conversion graph, yield/scrap equation, substitute-material policy, or a full actual-cost formula. `SetupLostQty`, `OperationLostQty`, `WIPBomDefault`, and `tolerance` are persisted but their posting/cost consequences need trace tests. Do not infer that `Issued - Returned` is actual use: a standalone material-return workflow was not identified in the reviewed files.

### Recommended calculation contract

Use decimal arithmetic—not `float`/`double`—and store both source and calculated quantities:

```text
RequiredBaseQty = RoundForItem(
    WoPlannedOutputBaseQty
    × ComponentQtyPerBomOutputBaseQty
    × (1 + WastagePct)
    / BomOutputBaseQty)

NetIssuedQty = PostedIssueQty - PostedReturnQty
OpenRequirementQty = max(RequiredBaseQty - NetIssuedQty, 0)
GoodQty = ProducedQty - RejectedQty
OpenProductionQty = PlannedQty - GoodQty - ApprovedOverproductionDispositionQty
YieldPct = GoodQty / InputOrPlannedBasis × 100   // basis must be configured
```

`BomOutputBaseQty`, UOM conversion, rounding mode/scale, yield basis, and whether tolerance is planning-only or an issue limit must be explicit master-data/business decisions.

## Material issue/return, production completion, costing and inventory posting

### Material issue

`IssueToProduction` loads eligible BOM/lot data through `IssueToProdHelper`, creates an `IvTrxBatch` and detail rows, and stops editing after `BatchStatus=POSTED`. `LoadBOMAllLot` can filter by process, material warehouse and transaction date; lots are represented by `IvBalLoc`. This supports partial issue and lot selection. It does **not** by itself prove a reservation mechanism, negative-stock policy, or a posted material-return document.

Recommended design: use immutable posted `ProductionMaterialIssue` and `ProductionMaterialReturn` documents, each with document/line references to WO material requirement, source warehouse/location/lot/serial, base and transaction UOM quantities, cost, posting date and reversal reference. Never derive returned quantity from editable issue rows.

### Production/WIP and partial completion

`DailyProdHelper` verifies preceding work centre/process and validates a WIP lot belongs to the supplied WO; it reads `PrSchProcess.FinalProcess`. `DailyPrdOutPut` passes output, scrap and reject quantities into helper routines and posts batches through `DailyProdPostHelper`. WIP is held by item/work-centre/process/lot/revision/WO in `WIPItemBalLoc`.

This confirms partial production at the transaction level (multiple WIP lots and daily rows), but the exact header outstanding-quantity formula is **UNCONFIRMED**. The new module must calculate it from posted, non-reversed completion lines rather than a status field.

### Finished goods receipt and reversal

`FinishGoodPostHelper.Post` prevents duplicate destination lots, calls `CPosting.PostInventoryTransaction("FG", ...)`, deducts matched WIP and refuses to consume more than WIP quantity/weight, then records daily data and attempts WO closure. `Rollback` calls `CPosting.RollbackInventoryTransaction`, restores WIP, removes associated daily data and reopens the WO. Both posting and rollback operate over inventory balance, location balance, cost balance/history and WIP in a transaction; rollback also emits audit logs.

The legacy code has genuine reversal behaviour, but its order of operations and exception/atomicity boundaries should be integration-tested before using it as an accounting specification.

### Costing

Confirmed facts: BOM lines have no captured per-line cost in WO snapshot fields reviewed; labour cost is copied/calculated; inventory posting opens `IvBalLocCost` and inventory-history tables. **UNCONFIRMED:** standard vs moving average/FIFO/actual method, material/labour/machine/overhead roll-up, WIP valuation and GL posting. No direct GL posting was confirmed in this study.

Recommended new design: preserve inventory cost determined by the existing inventory-cost engine on every posted issue/return/receipt; write production cost layers that reference those movements. Do not invent a second costing engine. Implement material, labour, machine, overhead and subcontract buckets; calculate variances only after the costing basis and period-close behaviour are confirmed.

## Legacy problems / KEEP, IMPROVE, REMOVE, ADD

| Classification | Finding / decision |
|---|---|
| KEEP | Product definition is copied to a WO snapshot; WO-specific routing/BOM/date plan is valuable for auditability. |
| KEEP | Process sequencing, work-centre/machine schedule, WIP by lot and transactional FG rollback are proven concepts. |
| KEEP | Delivery-request allocation and sales-order WO linkage, subject to redesigned allocation records. |
| IMPROVE | Replace strings such as `NEW`, `RELEASE`, `IN PROCESS`, `IN PROGRESS`, `CLOSED` with one controlled state machine. |
| IMPROVE | Replace page-owned session `DataTable`s and inline concatenated SQL with application services, EF transactions, optimistic concurrency and parameterized queries. |
| IMPROVE | Make BOM revision/output quantity/UOM/rounding/tolerance snapshot attributes explicit. Legacy flags alone are insufficient. |
| IMPROVE | Separate plan, execution, inventory posting, costing and closure; a header save must not silently create external effects beyond intentional DR allocation. |
| REMOVE | Duplicate WO entry variants as competing implementations; retain only distinct entry intents (manual, sales/DR, MRP) on a shared domain service. |
| REMOVE | Reprocessing that can replace a released snapshot without an auditable change order. |
| ADD | Revision snapshots, material substitutions/approvals, returns, reservations/shortage policy, serial/lot traceability, full audit trail, idempotent posting/reversal and accounting-period locks. |
| ADD | Explicit handling of over/under production, scrap/by-product, UOM conversion, concurrent posting and KPI/inquiry views. |

## Proposed new database design and old-to-new mapping

The names below are proposals; apply the actual new ERP naming, tenant and audit conventions after its architecture is available.

| Old ERP | New ERP proposal | Action / rationale |
|---|---|---|
| `PrDefMas` + `PrDefWCenter` + `PrDefProcess` + `PrDefBOM` + `PrDefMachine` + `PrDefLabour` | Product/BOM aggregate: `ProductionBom`, `ProductionBomRevision`, `BomOperation`, `BomComponent`, `OperationResource`, `OperationLabourRate` | Redesign and revision. Separate master definition from effective revision. |
| `PrSchMas` | `ProductionWorkOrder` | Redesign. Identity, planned quantities/dates/status, source references, BOM snapshot ID, row version. |
| `PrSchWCenter` + `PrSchProcess` | `ProductionWorkOrderOperation` | Merge. One sequential operation model with work-centre/resource schedule. |
| `PrSchBOM` | `ProductionWorkOrderMaterial` | Redesign. Frozen per-WO requirement with required/issued/returned/consumed/variance quantities. |
| `PrSchMachine` + `PrSchLabour` + `PrSchMacMain` | `ProductionWorkOrderOperationResource` and `ProductionOperationSchedule` | Split execution resource assignment from calendar reservation/schedule. |
| `PrSchDR` | `ProductionWorkOrderDemandAllocation` | Replace. Many-to-many allocation with allocated/released/completed quantities. |
| `IvTrxBatch` + detail | Existing new-ERP stock document/movement entities | Integrate; do not duplicate inventory ledger. Add source-document references. |
| `WIPItemBal`, `WIPItemBalLoc` | Inventory/WIP ledger dimensions or `ProductionWipLotBalance` projection | Replace as an append-only projection sourced from transactions. |
| `PrScheduleNum`/`AdSmNum*` | Existing document-numbering service | Replace. Ensure uniqueness/concurrency. |

Minimum proposed entities:

```text
ProductionWorkOrder (TenantId, CompanyId, BranchId, Id, Number, Status, ProductId,
  BomRevisionId, SnapshotHash, Planned/Completed/Scrap/Reject Qty, dates, RowVersion)
  ├─ Operations (sequence, work centre, resource plan, planned dates, status)
  │   └─ Resources (machine/labour, planned and actual cost/time)
  ├─ Materials (component, warehouse, required/issued/returned/actual/variance quantities)
  ├─ DemandAllocations (Sales/DR/MRP source, allocated and fulfilled quantities)
  ├─ MaterialIssue / MaterialReturn documents and lines
  ├─ Completion documents and lines (good/reject/scrap/by-product, WIP/FG destination)
  ├─ Cost/variance snapshot and posting references
  └─ StatusHistory / ChangeOrder / AuditEvent
```

Every owned record must carry the new ERP’s tenant/company/branch dimensions, create/update actor/time and concurrency token. Warehouse/location/lot/serial dimensions belong on stock-movement lines, not only on the WO header.

## Proposed workflow, posting rules and business rules

```text
Draft → Released → In Progress → Completed → Closed
  │        │             │          │
  └─ Cancelled           └─ Reopen only through controlled reversal/change approval
```

1. Create a Draft from manual demand, sales/DR allocation, MRP or planning. Select an effective BOM revision and capture a complete immutable snapshot.
2. Validate material/resource/UOM data. Release freezes snapshot; optionally create reservations according to inventory policy.
3. Post issues/returns; post operation completion/WIP moves; post FG receipt. Each source document creates/reverses inventory movements through the platform’s stock service in the same transaction/outbox unit.
4. Complete only when all planned output is accounted for by good/scrap/reject/approved variance. Close only when required operational and costing conditions are met.

| ID | Rule |
|---|---|
| BR-WO-001 | A Released WO cannot change BOM revision, product, output UOM or snapshot lines. A controlled change order must create a new revision/snapshot and variance audit. |
| BR-WO-002 | Required component quantity uses planned output, BOM output quantity, UOM conversion and configured rounding; calculations use decimal precision. |
| BR-WO-003 | A stock posting must be idempotent, atomic with its production document, traceable to its WO/source line, and reversible by an explicit reversal document. |
| BR-WO-004 | Material return cannot exceed net posted issue for the returned component/lot/location unless an approved exception policy says otherwise. |
| BR-WO-005 | Completion cannot consume more WIP lot quantity than available, matching the confirmed legacy FG/WIP protection. |
| BR-WO-006 | A WO cannot be closed while posted transactions, unresolved WIP, pending cost calculation, or open mandatory variances remain. |
| BR-WO-007 | Over-issue, substitute material, over-production and negative stock require a permissioned, reason-coded approval. |
| BR-WO-008 | Posting/reversal is blocked in closed inventory/accounting periods; reversal must use a permitted date and retain the original linkage. |
| BR-WO-009 | All state-changing commands require optimistic concurrency on the WO and document rows; retries must be safe. |
| BR-WO-010 | Lot/serial-controlled items require full source/destination traceability on issue, WIP transfer, completion and FG receipt. |

## UI and inquiry design

Use the existing new-ERP DevExpress Blazor conventions once available. The WO detail should be a document shell with: Header/status timeline; Operations; Materials; Issues/Returns; Completions/WIP; Costs & Variance; Demand allocation; Attachments/Audit. Permission-based commands should be state-aware: Save Draft, Release, Change Order, Issue, Return, Record Completion, Receive FG, Complete, Close, Reverse, Cancel.

Required inquiries/KPIs: WO backlog by status/date/customer/product; material shortage and reservation coverage; planned vs actual output; WIP ageing by operation/lot; material issue/return/variance; resource schedule/load; yield/scrap/reject; cost and variance; complete traceability from sales demand to component lot to FG lot.

## Edge cases and required decisions

| Scenario | Proposed handling |
|---|---|
| WO quantity changes after creation | Draft: recalculate on demand. Released: controlled change order showing material/resource/date deltas; never overwrite posted history. |
| BOM changed/deleted after WO creation | Preserve snapshot; new BOM affects new WOs only. Change order selects a new valid revision. |
| Insufficient/negative stock | Show shortage; block or allow approved negative issue according to inventory policy—**UNCONFIRMED business decision**. |
| Over issue / material return / substitution | Reason + approval + traceable ledger document; update variance, never mutate requirement history. |
| Decimal BOM/UOM | Convert to base UOM using decimal factors and defined rounding per item; retain entered and base quantities. |
| Multiple partial completions | Aggregate posted good/reject/scrap by WO/operation; calculate outstanding live from ledger. |
| Over/under production | Require tolerance/approval; completion disposition must reconcile planned output at close. |
| Zero-cost material / costing | Allow posting only if inventory policy permits; flag pending/revaluation variance. **Cost method requires confirmation.** |
| Lots/serials/multiple warehouses | Enforce item tracking rules on each movement, including WIP location and FG destination. |
| Cancellation/reversal/closed period | Void drafts; reverse posted documents in order, preserve audit chain; prohibit direct deletes and respect period locks. |
| Concurrent edit/post | Row version + transaction isolation/outbox; revalidate available stock/WIP immediately before posting. |

## Migration and compatibility concerns

Do not bulk-copy `PrSch*` as if it were a clean target schema. Map legacy WOs with their snapshot lines and original identifiers, then reconcile issue/FG/WIP/inventory documents by reference number and quantity/cost. Preserve legacy status as a migration attribute and map to a reviewed target state; unresolved combinations go to an exception queue. Historical dates, lot numbers, `RelNo`, demand links, user/audit references and posted/reversed batch references must be retained. Recalculate projections only after ledger migration is reconciled; do not recompute historical requirements from a current BOM.

## Second-pass omissions and corrections

The first pass covered the main work-order snapshot and inventory tables, but a second trace through the production pages, posting helpers, adapters and inquiry pages found additional execution tables, projections and cross-module paths. These are part of the migration scope even when they are not directly edited by `WorkOrder.aspx`.

### Additional tables and dependencies

| Area | Additional object | What the code shows / migration implication |
|---|---|---|
| Daily execution | `PrSchDailyHdr` | Daily-input document header (`DocNo`, date, shift, operator, machine, WO/product/work-centre/process and daily batch). Preserve as a document header or map to the new completion document. |
| Daily execution | `PrSchDailyDtl` | Header detail by line with start/end time, good/scrap/reject, lost time, operator, reason, length check, remark and schedule flag. It is needed for labour/time/efficiency history and is linked by `DocNo`. |
| Daily execution | `PrSchDailyPlan` | Identity-keyed plan rows with planned/actual/scrap/reject quantities, machine/operator, shift, duration, batch/reference and `NEW`/`IN`/`UPDATED`/`POSTED` status. It is a planning-to-execution projection, not just UI state. |
| Daily execution | `PrSchDailyProcess` | Per-WO/work-centre/process progress aggregate (`TotalQty`, `Completed`, `LotNo`, sequence/date fields). Rebuild or reconcile it from posted completion/WIP movements. |
| Daily execution | `PrSchDailyProd` | Actual good, scrap, reject, on-hold and weight quantities, lots, WIP predecessor references, machine/operator/times, warehouse/location, final-process and transaction type (`IP`, `WI`, `FG`, etc.). This is the main production history table. |
| Resources | `PrMachine`, `PrWorkCentre` | Master data used by daily input/output, rescheduling and reports; the snapshot must retain the selected resource identity even if the master later changes. |
| Scheduling | `PrShiftGroup`, `PrCalendar`, `vPrSchMacForReschedule` | Shift-group/calendar and rescheduling projections feed date calculation and later machine rescheduling. Preserve the calendar version/effective dates used to produce the WO schedule. |
| Inventory control | `IvStatus` | Active inventory-status master used when selecting/validating WIP and stock status; include the status dimension in movement and WIP mapping. |
| Inventory control | `IvBalloc` | Positive-lot allocation rows read by issue-to-production. The code proves an allocation/availability source, but not whether it is a hard reservation; reservation semantics remain a decision. |
| FG staging | `PrSchMasFG`, `PrSchMasFGLots` | Standard daily-FG staging rows carry FG/reject/scrap quantity, source/destination warehouses, FG lot, operator, batch/reference and `NEW`/`POSTED` status; lot-split rows are consumed by the mobile/FG posting helper. They must be migrated or deliberately replaced by the new FG receipt document. |
| Mobile/alternate FG | `PrSchFinishGood`, `PrSchMasSinJim` | Mobile FG staging carries source process/WIP lot, source quantity/revision, destination lot/warehouse/location, batch GUID, error/status and posting references. `PrSchMasSinJim` is a customer/variant path; do not silently merge it with standard FG. |
| Read models | `vprd_IssueToPrdList`, `vprd_DailyProdOutput`, `vprd_WipFinishLots` | SQL views used by issue, daily-output and FG-receipt screens. Recreate as query projections; do not treat them as authoritative ledgers. |
| Read models/reporting | `vprd_WorkOrder*`, `vprd_WorkProgress*`, `vprd_PrSchDailyProd`, `vprd_WIPItemBalLoc`, `vprd_DailyProdHis`, `vprd_DailyProcessOutput`, `vprd_DailyEfficiencyProd`, `vprd_OutStandingFG` | Inquiry/report views expose WO backlog, progress, daily history, WIP, output and outstanding-FG states. Report parity requires mapping their filters and joins, not only recreating the entry screen. |
| Reporting/attachments | `AdReportID`, `IvItemAttachs`, `SaSO` | Work-order inquiry/printing resolves report metadata, loads item attachments and joins sales-order context. These are downstream compatibility requirements. |
| Demand/planning | `SaPlan` | `WorkOrder.BindItemBal` adds a forecast row from `BudgetQty` when the completion date falls in the plan period. It is informational demand context, not a WO transaction. |
| MRP/planning | `PrMRP`, `vPrMRPProcess_*` | MRP make/buy/sub-item records exist in the production data model and may be an upstream WO source. The reviewed WorkOrder page does not itself prove the generation contract; trace the MRP conversion before migration. |
| Generation | `GenerateWorkOrderHelper` | Automatic DR-to-WO generation creates the same `PrSch*` snapshot plus `PrSchDR`/`PrSchMacMain`, advances numbering, updates `SaDeliveryRequest` and links `SaSODetail`. It must use the same domain service as manual creation. |
| Lifecycle | `WorkOrderHelper` | Release/reactivate/delete/cancel-style operations mutate `PrSchMas` and child snapshots, clear `SaSODetail.WorkOrderNo`, and write the six `WORDER ...` audit categories. Direct delete must be replaced by guarded void/reversal rules after posting. |
| Cross-module execution | `WipLotTransferPostHelper` | WIP lot transfers can write/delete `PrSchDailyProd`; partial production and rework migration is incomplete without this path. |
| Cross-module quality | `QaMRB`, `QaOnHold`, QA/MRB posting helpers | QA/MRB workflows update or roll back daily-production/WIP records for holds, release and disposition. Include these tables, permissions and reversal effects in the new completion model. |
| Variant paths | `DailyInputEntry`, Mobile/NoIssue/JONG pages and related helpers | These are alternate entry/posting routes. Consolidate their business rules or explicitly mark them as variant-only; otherwise duplicate posting behaviour will remain. |
| Subcontracting | `SubConPostIssueHelper`, `SubConPostRecvHelper` and SubCon pages | External-process issue/receipt can consume or return production quantities outside the standard machine/WIP path. Include source WO/operation, supplier, quantity, lot and reversal links if this scope is retained. |

`PrSchMasFG` is used by the standard daily-FG staging helper; the separately named SweetKiss/SinJim helpers are variant paths. Treat `PrSchFinishGood`, `PrSchMasSinJim` and any customer-specific lot tables as explicit variants rather than assuming they are interchangeable with standard `PrSchMasFG`.

### Confirmed execution and status flow

The code supports this more precise sequence (the values belong to different entities and must not be collapsed blindly):

```text
PrSchDailyPlan: NEW/IN -> UPDATED -> IvTrxBatch WI/IP -> POSTED
IvTrxBatch:     NEW -> POSTED -> explicit rollback (period validation)
PrSchMas:       RELEASE -> IN PROGRESS -> COMPLETED (final-process quantity) -> CLOSED (FG good quantity)
```

`DailyProdPostHelper.StartPost` limits a transaction to at most three batch numbers, requires each batch to be postable, writes `PrSchDailyProcess`/`PrSchDailyProd`/WIP balances and then marks the batch `POSTED`. `IssueProdPostHelper` posts `IP`, updates WIP and moves the WO to `IN PROGRESS`. `FinishGoodPostHelper.checkScheClose` is confirmed for the standard FG path: it sums non-reversed `PrSchDailyProd` rows with `TrxType='FG'` and `GoodQty`; when the total reaches `PrSchMas.ScheQty`, the header is set to `CLOSED`, otherwise it remains `IN PROGRESS` (linked delivery-request status is updated only when its balance is zero).

The status vocabulary is inconsistent across pages (`RELEASE`, `Release`, `IN PROCESS`, `IN PROGRESS`, `COMPLETED`, `CLOSED`, `NEW`, `UPDATED`, `POSTED`, and an `A` filter value). The target must define one state machine plus an explicit mapping for each legacy entity/status.

### Corrected calculation and posting findings

- `IssueProdHelper.LoadBOM` calculates a simple remaining requirement as `max(StdQty - IssuedQty, 0)` (four-decimal rounding). That is different from `CalculateReqBOM`, whose confirmed issue formula is `round(StdPackSize * requestedOutput, 4)`; the overload adds similarly scaled scrap and reject quantities and allocates lots by `IvBalLoc.TransDate`.
- The `IP`/`IPS` posting code consumes inventory through `IvBalLoc`/`IvBalLocCost` and writes inventory history. `PostIPSTransaction` includes input + scrap + reject quantities in the issue quantity. Preserve this distinction when specifying requirement, consumption and variance calculations.
- `DailyPrdOutPut.SaveDiff` validates that input does not exceed WIP, separates JONG and NONJONG batches, posts transaction type `WI`, and validates the accounting date. This is a separate completion path, not merely another screen over the same save operation.
- `WorkOrder.Save` performs the header/child snapshot update, numbering, delivery-request balance/status, `PrSchDR`, `PrSchMacMain`, `AdSmNumDate` and `SaSODetail` linkage inside its transaction, then emits audit categories `WORDER MAS`, `WORDER WCENTER`, `WORDER PROCESS`, `WORDER BOM`, `WORDER MACHINE` and `WORDER LABOUR`. The replacement must retain equivalent atomicity and audit coverage.

### Legacy behaviours requiring explicit remediation

- `WorkOrder.ReProcess` reloads the current product definition and reruns process construction, so it can replace a WO snapshot with today’s defaults. Treat this as a controlled change order in the target; never allow silent replacement after release/posting.
- Scheduling direction is data, not presentation: `chkStartfr` makes `ProcessNewWithTime` schedule forward from the start date; when it is false the routine back-schedules from the completion date. Persist the choice and test both directions across shift boundaries, downtime and stock processes.
- The markup exposes a `RECALCULATE EDITS`/`REPROCESSTIME` button, but its callback and server branch are commented out. Treat it as dead UI until the intended semantics are recovered; do not promise this command in the new module without a defined change-order contract.
- The browser recalculates `ScheduleQty = DeliveryQty + DesireQty + ConsignmentQty`, while hidden client fields (`hdProcess`, `hdWCCode`, `hdWCICode`, `hdProdCode`) drive grid callbacks. Recalculate and authorize these values on the server; never trust the callback payload or session `DataTable` as the source of truth.
- The `FULLFILL`/close-delivery-request command is permission-gated by `AdUser.FullFill`, and the remark source can switch between product-definition and DR text. Preserve both authorization and source-selection rules in the application service/audit event.
- `IsBomDefaultisTick` enforces a default BOM per process/routing group, but its generated SQL contains a duplicated `ProcessCode` predicate. Add a regression test and replace the query with a parameterized, set-based validation.
- `DailyProdPostHelper` contains a suspicious branch where `PrSchDailyProcess.Completed` can remain `0` even when `TotalQty >= WCScheQty` (the intended assignment is commented/inconsistent). Confirm against production data before migration and do not use that flag as the sole completion truth.
- `WorkOrderHelper.WorkOrderDelete` physically deletes child snapshots and clears sales-order linkage. Permit this only for unposted drafts; posted history must use cancellation/reversal with references.

### Migration additions

The migration work must include daily headers/plans/process aggregates/history, batch-to-production references, WIP-lot transfers, QA/MRB dispositions, alternate entry routes, report/attachment links and the DR-generated WO path. Reconcile projections only after the authoritative inventory/WIP/production ledgers are loaded. Resolve the exact legacy object behind the commented `WIPBalItemLoc` spelling (it may be a legacy alias/typo), and inspect live schema foreign keys, indexes, triggers and views before designing the final mapping.

## Implementation phases and testing strategy

1. **Discovery closure:** query the live legacy schema; trace all `PrSchDProd`, posting helper and report dependencies; document stored procedures/triggers if any; inspect the actual new Blazor solution.
2. **Foundation:** adopt new ERP document, inventory, numbering, auditing, authorization and posting conventions; create a test fixture database.
3. **Master/snapshot:** implement revisioned BOM/routing and Draft/Release WO snapshot with calculation tests.
4. **Execution:** implement issue/return, WIP completion and FG receipt using existing stock posting service and idempotent outbox.
5. **Cost/close:** implement cost aggregation, variance, close/reopen/reversal and period controls after costing rules are confirmed.
6. **Migration/reporting:** migrate a representative reconciled cohort, build inquiry/KPI/report parity, then run parallel validation.

Automated tests must cover BOM ratios (including 1:5 and fractional 0.2), rounding/UOM conversion, all status transitions, partial issue/return, partial/output/scrap/reject, over-production approvals, lot/serial movement, stock shortage, duplicate post retry, rollback, closed period, concurrent commands and cost/quantity reconciliation. Add integration tests that assert every production command’s expected inventory and WIP ledger movements.

## Risks and open questions

1. **CONFIRMED for standard FG posting:** `FinishGoodPostHelper` closes when summed `PrSchDailyProd` FG `GoodQty` reaches `PrSchMas.ScheQty`; confirm whether variant paths use the same predicate.
2. **UNCONFIRMED:** material-return implementation and actual-used formula.
3. **UNCONFIRMED:** legacy inventory costing method, cost-layer rules and GL effect.
4. **UNCONFIRMED:** UOM conversion, scrap/yield/wastage semantics, tolerance enforcement and substitute material policy.
5. **UNCONFIRMED:** exact reservation semantics of `IvBalloc`, and whether `PrSchDailyProcess.Completed` is reliable in all variants.
6. **UNCONFIRMED:** exact object represented by `WIPBalItemLoc` in legacy comments, plus live-schema foreign keys/indexes/triggers.
7. **UNCONFIRMED:** new Blazor ERP architecture and conventions; no source was present in this workspace.
8. **Risk:** legacy code uses floating-point arithmetic, inline SQL, duplicated page variants and mixed status text. Treat legacy numbers as a behavioural reference to test, not as a safe implementation pattern.

Before implementation, resolve each unconfirmed item with a source-trace review plus production/business-owner decision. The decision log should become part of the new module’s specification and acceptance tests.
