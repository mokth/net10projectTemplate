# Delivery Request Fulfilment Control Enhancement
## FINAL — APPROVED FOR IMPLEMENTATION — 10/10 CODE-AGENT PLAN

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Repository HEAD verified during this review:** `12bc57fe3019f49ff9402fb584d4f2f5431a7e34`  
**Reviewed input plan:** `delivery_request_fulfilment_control_APPROVED_FOR_IMPLEMENTATION_productionv2(1).md`  
**Target:** SME ERP fulfilment control: Sales Order → Delivery Request → Stock / Production / Procurement → Delivery Order → Invoice.

> **Branch-drift gate:** before implementation, the Code Agent MUST run `git rev-parse HEAD`.  
> If HEAD differs from the verified SHA above, re-read every affected file named in this plan before editing.  
> Do not blindly apply line-number assumptions to a newer branch.

---

# Objective

Upgrade the existing Delivery Request (DR) from a Sales Order → Work Order handoff into the repository's common customer-fulfilment control reference.

The completed feature MUST make these fields and facts operationally meaningful:

- Product;
- Production UOM;
- Fulfilment Warehouse;
- Project;
- Priority;
- Required Date;
- Product Definition;
- Sales Order demand;
- stock-ready quantity;
- production requirement;
- Work Order progress;
- finished-goods receipt;
- material shortage;
- procurement pipeline;
- Delivery Order allocation/delivery;
- fulfilment status;
- risk/blocker;
- inquiry/KPI.

DR MUST coordinate existing authorities. It MUST NOT become a second inventory ledger, costing engine, MRP engine, BOM engine, Purchase Order engine, or Delivery Order posting engine.

---

# Final Review Verdict

The uploaded plan had the correct overall direction, but it required the following source-grounded corrections before a 10/10 approval:

1. The uploaded baseline SHA was stale; current `productionv2` HEAD is `12bc57fe3019f49ff9402fb584d4f2f5431a7e34`.
2. DR quantities are standard/production quantities while DO UI `Qty` is selling quantity; DR→DO validation MUST compare server-derived `SaDoDetail.StdQty`.
3. A bare NEW DO line is not stock supply. Only active DR stock reservation and NEW SP shipment reservation count as stock-ready.
4. Linked Work Order supply and posted Finished Goods Receipt must not be double-counted.
5. `ProductionMaterialAllocationService` currently restricts allocation to MANUAL issue materials; DR shortage inquiry needs a read-only shared stock-availability reader that also supports purchased/external BACKFLUSH/PICK_LIST materials.
6. `PoPrDetail.WorkOrderMaterialId` introduces a structural dependency that must block unsafe Work Order reopen/refresh/change-definition/delete/substitution.
7. Procurement shortage MUST subtract existing linked PR/PO pipeline or duplicate PRs can be created.
8. Direct SO→Invoice blocking MUST apply only after DR is operationally released (`RELEASED` / `IN_PRODUCTION`), not merely because a Draft DR exists.
9. DR stock eligibility MUST reuse `IvSpFifoEligibility`; the plan MUST NOT invent stricter lot/expiry rules that current Sales shipment does not enforce.
10. Stock reservation needs explicit reconciliation triggers after FG receipt and before supply-changing DR actions.
11. Existing stored DR `COMPLETED` is a legacy-compatible lifecycle value; new customer fulfilment completion MUST be derived from DR-linked delivered DO standard quantities.
12. Project and Warehouse MUST be source-authoritative after SO demand is selected, not independently editable decorations.

These corrections are incorporated below.

---

# Repository-Confirmed Current State

## Delivery Request

Verified:

- `ErpWeb.Model/Entities/Sales/SaDeliveryRequest.cs`
- `ErpWeb.Model/Entities/Sales/SaDeliveryRequestSource.cs`
- `ErpWeb.Model/Configurations/Sales/SaDeliveryRequestConfiguration.cs`
- `ErpWeb.Core/Sales/ISaDeliveryRequestService.cs`
- `ErpWeb.Core/Sales/SaDeliveryRequestService.cs`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor.cs`
- `scripts/create-sales-delivery-request.sql`

Confirmed behavior:

- Product is read-only in DR entry.
- Definition/Warehouse/Project/Priority are free-text inputs.
- Product/UOM/requested quantity are re-derived from exact current SO revision/line in `PrepareSourceRowsAsync`.
- all DR sources are already forced to one Product + one Production UOM.
- eligible SO demand already returns SO detail `Warehouse`.
- eligible SO demand does not currently return SO header Project.
- `ApplyHeader` currently only trims/stores Definition/Warehouse/Project/Priority.
- current list `UnplannedQty = RequestedQty - active WO allocation`.
- current completion is derived from Work Order `GoodQty`.
- DR lifecycle locking uses SQL Server `UPDLOCK/HOLDLOCK`.
- DR create/update already has deadlock retry.
- DR cancel currently blocks live production but has no DO-delivery guard.

---

## Sales Order

Verified:

- `ErpWeb.Core/Sales/SaSoService.cs`
- `ErpWeb.Core/Sales/ISaSoService.cs`
- `ErpWeb.Core/Admin/MsRefLookupRules.cs`

Confirmed:

- SO has active Warehouse lookup.
- SO Project is `ProjId`.
- SO uses shared `MsRefLookupRules` for Project.
- SO detail carries Warehouse, selling quantity, `StdQty`, `StdPsize`, `StdUom`.
- DR source quantity is based on SO standard quantity.

---

## Delivery Order

Verified:

- `ErpWeb.Model/Entities/Sales/SaDo.cs`
- `ErpWeb.Model/Entities/Sales/SaDoDetail.cs`
- `ErpWeb.Model/Configurations/Sales/SaDoDetailConfiguration.cs`
- `ErpWeb.Core/Sales/ISaDoService.cs`
- `ErpWeb.Core/Sales/SaDoService.cs`
- `ErpWeb.UI/Sales/Transactions/SaDo.razor`
- `ErpWeb.UI/Sales/Transactions/SaDo.razor.cs`

Confirmed:

- DO detail has exact SO No / revision / line.
- DO has no DR identity today.
- UI `Qty` is selling quantity.
- service derives:
  `StdQty = IvQty.Round(Qty * StdPackSize)`.
- `SaDoLineVm.FromSalesOrder()` uses SO `BalanceQty` as selling quantity.
- stock shipment is separate from DO line save.
- DO statuses used by current service are NEW / POSTED / CLOSED.
- force-close keeps the physical shipment as delivered.
- rollback returns POSTED DO to NEW and reverses inventory movement.
- deleted/archived DO must not contribute to operational DR delivery.

---

## Shipment Reservation

Verified:

- `ErpWeb.Core/Inventory/IIvSpShipmentService.cs`
- `ErpWeb.Core/Inventory/IvSpShipmentService.cs`
- `ErpWeb.Core/Inventory/IvSpShipmentAllocator.cs`
- `ErpWeb.Core/Inventory/IvSpFifoEligibility.cs`
- `ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs`

Confirmed:

- NEW Sales-Out SP batches are soft stock reservations.
- `SumOtherNewSpReservationsAsync` subtracts NEW SP reservations by `FromBalLocId`.
- shipment candidate authority is `IvSpFifoEligibility`.
- physical stock authority is `IvBalLoc.StdQty`.
- `IIvStockPostingRepository` owns physical stock write/locking.
- DR MUST NOT change `IvBalLoc.StdQty`.

---

## Work Order / Production

Verified:

- `ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.Reopen.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.Delete.cs`
- `ErpWeb.Model/Entities/Production/ProductionWorkOrder.cs`
- `ErpWeb.Model/Entities/Production/ProductionWorkOrderMaterial.cs`
- `ErpWeb.Core/Production/IProductionMaterialAllocationService.cs`
- `ErpWeb.Core/Production/ProductionMaterialAllocationService.cs`
- `ErpWeb.Core/Production/IProductionMaterialIssueDraftReservationReader.cs`

Confirmed:

- `PrWorkOrderDemandAllocation` is exact DR→WO quantity authority.
- a DR-generated WO belongs to one DR.
- Work Order snapshot/BOM/material/schedule authority is already isolated in Production.
- `PrWorkOrderMaterial` is exact material requirement snapshot.
- material allocation service computes `ShortBaseQty`.
- current allocation service only permits MANUAL issue + PURCHASED/EXTERNAL supply.
- Released WO can be reopened when no execution blockers exist.
- Draft refresh/change-definition physically replaces snapshot material rows.
- adding a PR FK to material therefore requires lifecycle guards.

---

## Finished Goods Receipt

Verified:

- `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.cs`
- `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.Posting.cs`
- `ErpWeb.Core/Production/IProductionFinishedGoodReceiptService.cs`
- `ErpWeb.Model/Entities/Production/ProductionFinishedGoodReceipt.cs`
- `ErpWeb.Model/Configurations/Production/ProductionFinishedGoodReceiptConfiguration.cs`

Confirmed:

- FG Receipt is tied to `WorkOrderId`.
- posted receipt creates/increases physical `IvBalLoc`.
- rollback reverses it.
- `IvTrxBatch.BatchStatus` identifies POSTED / REVERSED state.
- source detail has destination standard quantity/UOM/warehouse.
- FG receipt posting/costing transaction MUST NOT be weakened by DR synchronization.

---

## Procurement

Verified:

- `ErpWeb.Model/Entities/Purchase/PoPrDetail.cs`
- `ErpWeb.Model/Configurations/Purchase/PoPrDetailConfiguration.cs`
- `ErpWeb.Core/Purchase/IPoPrService.cs`
- `ErpWeb.Core/Purchase/PoPrService.cs`
- `ErpWeb.Core/Purchase/PoPrCalc.cs`
- `ErpWeb.Model/Entities/Purchase/PoOrderDetail.cs`
- `ErpWeb.Core/Purchase/IPoOrderService.cs`
- `ErpWeb.Core/Purchase/PoOrderService.cs`
- `ErpWeb.Core/Purchase/PoOrderCalc.cs`
- `scripts/create-po-pr.sql`

Confirmed:

- physical PR detail table is `dbo.POPRDtl`.
- PR detail already stores standard quantity, warehouse and SO reference.
- PO detail already stores `PrNo` / `PrLineNo`.
- PO `PoQty` is standard quantity in current preparation path.
- PO `BalanceQty` is purchase-UOM quantity; standard outstanding quantity requires pack-size conversion.
- PR/PO already has consumption/remaining logic.
- GRN is PO-line based.
- no Work Order material identity currently exists on PR detail.

---

# Scope

Implement all of the following:

1. meaningful DR Product/Warehouse/Project/Priority/Definition UI;
2. source-authoritative Warehouse + Project;
3. current-stock soft reservation for released DR;
4. stock-ready / production-required / production-unplanned facts;
5. exact linked FG receipt supply projection;
6. stock-aware DR→WO limit;
7. exact DR source → DO line identity;
8. DR-aware shipment reservation without double reservation;
9. delivered/open/ready fulfilment facts;
10. direct SO invoice guard for released DR-controlled lines;
11. material shortage inquiry from Work Order materials;
12. material shortage → PR trace;
13. PR/PO pipeline deduction to prevent duplicate purchasing;
14. Work Order structural guards for linked procurement;
15. Production planning visibility of DR Priority/Project/Required Date;
16. DR inquiry, filters, blocker and KPI;
17. concurrency/rollback/regression tests.

---

# Non-Goals

MUST NOT:

- create a second stock ledger;
- decrement `IvBalLoc.StdQty` from DR;
- duplicate FIFO/costing logic;
- duplicate Work Order BOM logic in Sales;
- auto-create Purchase Orders;
- auto-select vendors;
- auto-release Purchase Requisitions;
- alter accounting posting;
- alter inventory costing;
- alter FIFO cost layers;
- alter month-end;
- alter Work Order costing;
- alter production absorbed costing;
- alter FG valuation;
- alter SO pricing/tax logic;
- alter DO pricing/tax logic;
- add SAP-style MRP/ATP;
- infer historical DR→DO links;
- infer historical PR→material links;
- change unrelated modules.

---

# Authority Rules

## Demand Authority

`SaDeliveryRequestSource` remains exact SO-demand authority.

Server MUST re-read exact:

```text
CompanyCode
BranchCode
SoNo
CustRel
SoLine
```

and derive:

```text
ProductCode
ProductDescription
ProductionUom
SourceQty
AllocatedProductionQty limits
CustomerCode
RequestedDeliveryDate
WarehouseCode
ProjectCode
```

Client Product/UOM/RequestedQty/Warehouse/Project MUST NOT override source truth after sources exist.

---

## Stock Authority

`IvBalLoc.StdQty` remains physical stock authority.

DR reservation is soft reservation only.

Physical shipment/posting remains:

```text
SaDo / SaInvoice
→ IIvSpShipmentService
→ inventory posting
→ IvBalLoc / IvTrxHistory / costing
```

---

## Production Authority

`PrWorkOrderDemandAllocation` remains DR→WO quantity authority.

Work Order snapshot, BOM, machine, labour, material issue, output, FG receipt, cost and scheduling remain Production authority.

---

## Procurement Authority

`PrWorkOrderMaterial` remains material-demand authority.

`PoPrDetail.WorkOrderMaterialId` is lineage only.

PR/PO/GRN state remains Procurement/Inventory authority.

---

## Delivery Authority

`SaDODetail.DeliveryRequestSourceId` becomes exact DR delivery lineage.

DR delivered quantity MUST be based on server-persisted DO `StdQty`, never DO selling `Qty`.

---

# Quantity Contract

All DR fulfilment quantities MUST be expressed in DR `ProductionUom`, which is the SO line standard UOM.

For DR-linked DO:

```text
DO request Qty = selling UOM quantity
DO service StdQty = IvQty.Round(Qty * current StdPackSize)
DR comparison quantity = DO service StdQty
```

MUST NOT compare DR allocated production quantity directly to `SaDoDetail.Qty`.

Use `QuantityTolerance = 0.0001m` consistently with current DR quantity precision.

---

# Correct Fulfilment Formula

For a DR:

```text
RequestedQty =
    SUM(active DR source AllocatedProductionQty)

DeliveredQty =
    SUM(DR-linked SaDODetail.StdQty
        where DO is not archived
        and DO.Status in POSTED/CLOSED)

OpenDemandQty =
    MAX(RequestedQty - DeliveredQty, 0)
```

---

## NEW Shipment Ready Supply

```text
NewShipmentReservedQty =
    SUM(IvTrxBatchDetail.FrStdQty
        for NEW Sales-Out batches
        joined to non-archived DR-linked NEW DO lines)
```

Only actual NEW SP detail quantity counts as shipment-ready supply.

A bare NEW DO line without SP reservation MUST NOT count as ready stock.

---

## Direct DR Stock Reservation

```text
DrStockReservedQty =
    SUM(active SaDeliveryRequestStockReservation.ReservedQty)
```

After a DR reservation is transferred to a NEW SP reservation, the transferred DR reservation rows MUST be released/inactivated in the same shipment mutation transaction.

Therefore:

```text
ReadyQty =
    MIN(OpenDemandQty,
        DrStockReservedQty + NewShipmentReservedQty)
```

No double count is allowed.

---

## Linked Work Order Supply

Derive net FG receipt quantity from active DR-linked Work Orders:

```text
LinkedFgReceivedQty =
    SUM(destination standard qty
        from non-deleted PrFinishedGoodReceipt
        whose IvTrxBatch status is POSTED
        and whose WorkOrderId belongs to active DR→WO allocations
        and whose item/UOM matches DR product/UOM)
```

REVERSED receipts do not count.

Then:

```text
ActiveWoAllocatedQty =
    SUM(active PrWorkOrderDemandAllocation.AllocatedQty
        for non-cancelled linked Work Orders)

OutstandingWoSupplyQty =
    MAX(ActiveWoAllocatedQty - LinkedFgReceivedQty, 0)
```

This prevents the same production supply from being counted once as WO supply and again after FG receipt reaches inventory.

---

## Reservation Target

Stock reconciliation MUST NOT automatically steal demand already covered by outstanding Work Orders.

Before allocating DR stock:

```text
TargetDrStockReservationQty =
    MAX(OpenDemandQty
        - NewShipmentReservedQty
        - OutstandingWoSupplyQty,
        0)
```

This rule means:

- before WO creation, available stock is reserved first;
- once a WO has been created, unrelated newly arriving stock does not silently replace already-planned production;
- when linked FG receipt posts, `OutstandingWoSupplyQty` falls and the reservation target increases, allowing the produced FG to become ready stock;
- if a WO is removed/deactivated, stock can be reconsidered on the next reconcile.

---

## Production Requirement

```text
ProductionRequiredQty =
    MAX(OpenDemandQty
        - DrStockReservedQty
        - NewShipmentReservedQty,
        0)

ProductionUnplannedQty =
    MAX(ProductionRequiredQty
        - OutstandingWoSupplyQty,
        0)
```

`ProductionUnplannedQty` is the only quantity offered as the default/max for a new DR Work Order.

---

# Fulfilment Status

Do not overload current stored DR lifecycle.

Keep stored lifecycle for document/production control:

```text
DRAFT
RELEASED
IN_PRODUCTION
CANCELLED
```

Keep existing `COMPLETED` constant/check only for backward compatibility with any historical data. New flows MUST NOT derive or write `COMPLETED` because Work Order GoodQty reached RequestedQty.

Add derived:

```text
FulfilmentStatus
```

Rules:

```text
COMPLETED
    DeliveredQty >= RequestedQty

PARTIAL_DELIVERED
    DeliveredQty > 0
    AND DeliveredQty < RequestedQty

READY
    OpenDemandQty > 0
    AND ReadyQty >= OpenDemandQty

PARTIAL_READY
    ReadyQty > 0
    AND ReadyQty < OpenDemandQty

OPEN
    otherwise
```

`SaDeliveryRequestListRow.Status` MUST represent stored lifecycle status after this change.

Add separate `FulfilmentStatus`.

Remove/replace current `DerivedStatus()` and current WO-GoodQty Completed filtering.

---

# Fulfilment Date / On-Time KPI

When `FulfilmentStatus == COMPLETED`:

```text
FulfilledDate =
    latest SaDo.PostedDate required to satisfy the DR
```

Because DO over-allocation is prohibited, using the latest posted date across contributing POSTED/CLOSED DR-linked DOs is sufficient for the complete DR.

KPI:

```text
OnTimeFulfilmentPercent =
    completed DR count with FulfilledDate.Date <= RequiredDate.Date
    / completed DR count
```

Do not fabricate an historical "ready date".

---

# Meaningful Header Fields

## Product

UI:

- use existing `IvStockMasterPicker`;
- editable only before any source row exists;
- acts as SO-demand filter;
- after first source is added, server source is authority and Product becomes locked.

Server:

- continue current Product mismatch validation;
- never trust picker as persistence authority.

---

## Production UOM

Remain read-only.

Always derive from SO `StdUom`.

---

## Fulfilment Warehouse

Meaning:

> Warehouse from which DR customer demand will be fulfilled.

Before source selection:

- active warehouse dropdown may be used as a source filter.

After source selection:

- Warehouse MUST be derived from SO detail Warehouse;
- all DR sources MUST have the same normalized Warehouse;
- header Warehouse becomes locked;
- mixed Warehouse requires separate DR.

No silent stock transfer.

Use the existing active `IvWarehouse` rule.

---

## Project

Before source selection:

- active Project dropdown may be used as a source filter.

After source selection:

- Project MUST be derived from SO header `ProjId`;
- all DR sources MUST have the same normalized Project;
- header Project becomes locked;
- mixed Project requires separate DR.

Use `MsRefLookupRules` for validation compatibility.

Blank Project remains allowed if all sources are blank.

---

## Priority

Controlled planner input:

```text
NORMAL
HIGH
URGENT
```

Default:

```text
NORMAL
```

Add:

```text
SaDeliveryRequestPriorities
```

Server rejects all unknown values.

Priority MUST be shown/filterable in:

- DR list;
- DR inquiry;
- Work Order list for DR-sourced WOs.

Priority MUST NOT automatically rearrange machine scheduling.

---

## Product Definition

Remove free-text entry.

Use the same `IPrProductDefService.ListActiveDefinitionsAsync(ProductCode)` pattern used by Work Order entry.

Behavior:

- optional on DR;
- acts as preferred definition for production;
- if blank, Work Order creation uses existing Work Order default-definition resolution;
- if supplied, Work Order creation revalidates it against current Product Definition authority;
- not required when `ProductionUnplannedQty == 0`.

---

# Database Changes

# 1. New `SaDeliveryRequestStockReservation`

## Proposed Entity

`ErpWeb.Model/Entities/Sales/SaDeliveryRequestStockReservation.cs`

Properties:

```text
Uid                     long
DeliveryRequestId       long
DeliveryRequestSourceId long
CompanyCode             string
BranchCode              string
BalLocId                int
ReservedQty             decimal(18,4)
IsActive                bool
ReleasedDate            DateTime?
ReleasedBy              string?
ReleaseReason            string?
CreatedDate             DateTime?
CreatedBy               string?
RowVersion              byte[]
```

`ReservedQty` is always DR ProductionUom / inventory standard UOM.

---

## Proposed EF Configuration

`ErpWeb.Model/Configurations/Sales/SaDeliveryRequestStockReservationConfiguration.cs`

Table:

```text
SaDeliveryRequestStockReservation
```

Rules:

- PK `UID`;
- FK to `SaDeliveryRequest.UID` with Restrict/NoAction;
- FK to `SaDeliveryRequestSource.UID` with Restrict/NoAction;
- FK to `IvBalLoc.ID` with Restrict/NoAction;
- `ReservedQty > 0`;
- `RowVersion` is rowversion;
- company/branch lengths follow current DR schema;
- user lengths follow current DR audit/header convention.

Indexes:

```text
IX_DrStockReservation_Dr_Active
    (CompanyCode, BranchCode, DeliveryRequestID, IsActive)

IX_DrStockReservation_Source_Active
    (CompanyCode, BranchCode, DeliveryRequestSourceID, IsActive)

IX_DrStockReservation_BalLoc_Active
    (CompanyCode, BranchCode, BalLocID, IsActive)

UX_DrStockReservation_Source_BalLoc_Active
    UNIQUE (DeliveryRequestSourceID, BalLocID)
    WHERE IsActive = 1
```

History rule:

- do not mutate historical Released rows;
- quantity change = release old active row + insert replacement active row;
- do not delete reservation history during normal lifecycle.

---

# 2. `SaDODetail.DeliveryRequestSourceId`

Existing:

`ErpWeb.Model/Entities/Sales/SaDoDetail.cs`

Add:

```text
long? DeliveryRequestSourceId
```

EF:

`ErpWeb.Model/Configurations/Sales/SaDoDetailConfiguration.cs`

Column:

```text
DeliveryRequestSourceID bigint NULL
```

Add FK:

```text
SaDODetail.DeliveryRequestSourceID
→ SaDeliveryRequestSource.UID
ON DELETE NO ACTION
```

Add index:

```text
IX_SaDODetail_Company_Branch_DeliveryRequestSourceID
```

No historical backfill.

---

# 3. `POPRDtl.WorkOrderMaterialID`

Existing entity:

`ErpWeb.Model/Entities/Purchase/PoPrDetail.cs`

Add:

```text
long? WorkOrderMaterialId
```

Existing EF config:

`ErpWeb.Model/Configurations/Purchase/PoPrDetailConfiguration.cs`

Physical table:

```text
dbo.POPRDtl
```

Column:

```text
WorkOrderMaterialID bigint NULL
```

Add FK:

```text
POPRDtl.WorkOrderMaterialID
→ PrWorkOrderMaterial.UID
ON DELETE NO ACTION
```

Add index:

```text
IX_POPRDtl_Company_Branch_WorkOrderMaterialID
```

No historical backfill.

---

# SQL Deployment

## New Upgrade Script

Create:

`scripts/alter-sales-delivery-request-fulfilment.sql`

It MUST:

1. verify required parent tables exist;
2. create DR reservation table if missing;
3. add `SaDODetail.DeliveryRequestSourceID` if missing;
4. add `POPRDtl.WorkOrderMaterialID` if missing;
5. add constraints/indexes only if missing;
6. use `WITH CHECK` for FKs;
7. be rerunnable;
8. never modify historical quantities;
9. never infer old links;
10. never update `IvBalLoc`.

Fresh-install scripts:

- update `scripts/create-sales-delivery-request.sql` for the new reservation table;
- update `scripts/create-po-pr.sql` for `WorkOrderMaterialID`.

There is no verified current Sales DO create script in this repository. Do not invent one; the upgrade script owns the DO-column addition.

---

# Model / DbContext / DI

Modify:

- `ErpWeb.Model/Data/AppDbContext.cs`
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`

Add:

```text
DbSet<SaDeliveryRequestStockReservation>
```

Register proposed services described below.

Do not add duplicate EF configuration registration; `ApplyConfigurationsFromAssembly` already exists.

---

# Proposed Core Services

# 1. `IInventorySoftReservationReader`

Add:

- `ErpWeb.Core/Inventory/IInventorySoftReservationReader.cs`
- `ErpWeb.Core/Inventory/InventorySoftReservationReader.cs`

Purpose:

single authority for advisory Sales-Out + DR soft reservations.

Required operation shape:

```text
GetReservedByBalanceAsync(
    AppDbContext db,
    company,
    branch,
    location,
    balanceIds,
    excludeSpDetailIds?,
    excludeDeliveryRequestSourceId?)
```

Return per BalLoc:

```text
NewSpReservedQty
DrReservedQty
TotalReservedQty
```

Implementation MUST:

- extract current `IvSpShipmentService.SumOtherNewSpReservationsAsync` semantics;
- keep current NEW SP query exactly equivalent;
- add active DR reservation aggregation;
- company/branch/location scope;
- exclude archived/tombstoned SP rows according to current shipment logic;
- support excluding one DR source when its reservation is being transferred to its own DO shipment;
- batch by balance IDs; avoid N+1 SQL.

`IvSpShipmentService` MUST use this reader so there is one reservation calculation path.

---

# 2. `ISaDeliveryRequestFulfilmentService`

Add:

- `ErpWeb.Core/Sales/ISaDeliveryRequestFulfilmentService.cs`
- `ErpWeb.Core/Sales/SaDeliveryRequestFulfilmentService.cs`

Responsibilities:

```text
GetFactsAsync
ReconcileAsync
ReconcileInTransactionAsync
ReleaseAllInTransactionAsync
TransferToShipmentInTransactionAsync
GetDeliveryTraceAsync
GetStockTraceAsync
GetLinkedFgReceivedAsync
```

Public wrapper methods may own a DbContext/transaction.

Methods called by DR/DO code that already owns a transaction MUST accept the caller's `AppDbContext` and MUST NOT start a nested transaction.

---

# 3. `IProductionMaterialStockAvailabilityReader`

Add:

- `ErpWeb.Core/Production/IProductionMaterialStockAvailabilityReader.cs`
- `ErpWeb.Core/Production/ProductionMaterialStockAvailabilityReader.cs`

Refactor the reusable stock-candidate part of current `ProductionMaterialAllocationService.PrepareAsync`.

Reader MUST handle:

- tenant;
- Company/Branch;
- material warehouse;
- inventory BaseUom;
- as-of stock;
- lot/expiry rules already present in Production candidate logic;
- other draft material-issue reservations;
- future-stock rule.

Reader MUST NOT reject merely because IssueMethod is BACKFLUSH/PICK_LIST.

Then:

- `ProductionMaterialAllocationService` keeps its current MANUAL issue gate before using the shared reader;
- DR shortage inquiry uses shared reader for PURCHASED / EXTERNAL_SUPPLY regardless of MANUAL/BACKFLUSH/PICK_LIST.

Existing material-allocation outputs MUST remain byte-for-byte equivalent for current supported paths.

---

# DR Source Preparation

Modify:

`SaDeliveryRequestService.PrepareSourceRowsAsync`

Preserve current locking/current-revision/product/UOM/quantity logic.

Add source-derived:

```text
WarehouseCode = current SaSODetail.Warehouse
ProjectCode   = current SaSo.ProjId
```

Prepared result MUST include both.

Rules:

1. all sources same Product;
2. all sources same ProductionUom;
3. all sources same normalized Warehouse;
4. all sources same normalized Project;
5. requested Warehouse from UI, when nonblank, must match source-derived Warehouse;
6. requested Project from UI, when nonblank, must match source-derived Project;
7. header persists source-derived Warehouse/Project, not arbitrary request values.

Extend:

`SaDeliveryRequestEligibleSource`

with:

```text
ProjectCode
```

Extend source picker query with optional:

```text
WarehouseCode
ProjectCode
CustomerCode
```

---

# DR Save Validation

Modify:

`SaDeliveryRequestService.ApplyHeader` / preparation path.

Priority:

- normalize uppercase;
- blank → NORMAL;
- reject unknown.

Warehouse:

- must exist and be active for company/branch when sources require it;
- final value comes from PreparedSources.

Project:

- validate source-derived value with `MsRefLookupRules`;
- preserve legacy-aware rule on old Draft updates;
- final value comes from PreparedSources.

Definition:

- may remain blank;
- client no longer gets free text;
- Work Order creation performs final current-definition validation.

---

# DR Lookups

Extend:

`ISaDeliveryRequestService`

Add:

```text
GetLookupsAsync()
```

Return:

- active Warehouses;
- active Projects;
- priority list.

Use the same active criteria already verified in Sales Order.

Do not inject `ISaSoService` into DR merely to reuse its DTO.

---

# Stock Reservation Reconcile

`SaDeliveryRequestFulfilmentService.Reconcile...`

Eligible lifecycle:

```text
RELEASED
IN_PRODUCTION
```

DRAFT/CANCELLED/legacy stored COMPLETED do not create new reservation.

Use:

```text
ICurrentDateService.Today
```

as the stock-availability date.

Reason:

DR current-stock readiness must not reserve a future-dated balance merely because RequiredDate is in the future.

Candidate rule MUST use `IvSpFifoEligibility` with:

- company;
- branch;
- tenant location;
- DR Product;
- DR Fulfilment Warehouse;
- business date;
- ACTIVE status;
- current TransDate rule.

Do not add a different Sales stock-eligibility rule.

---

## Reconcile Sequence

1. validate scope and access;
2. lock DR with current SQL Server DR lock helper;
3. read exact active sources;
4. calculate DeliveredQty;
5. calculate NEW SP reserved qty for DR-linked DOs;
6. calculate active WO allocated qty;
7. calculate linked POSTED FG receipt qty;
8. calculate `OutstandingWoSupplyQty`;
9. calculate `TargetDrStockReservationQty`;
10. discover candidate BalLoc IDs using `IvSpFifoEligibility`;
11. lock BalLoc rows in existing `IvStockSliceKey` order;
12. re-read current NEW SP + other DR reservations under the locked stock snapshot;
13. lock this DR's active reservation rows;
14. retain still-valid rows where possible;
15. release stale/excess rows;
16. allocate only up to target;
17. source allocation order:
    - RequestedDeliveryDate ascending;
    - SoNo;
    - CustRel;
    - SoLine;
18. FIFO candidate order:
    - TransDate;
    - LotNo;
    - BalLoc ID;
19. write audit only for real reservation changes;
20. save;
21. caller commits.

MUST NOT touch physical quantity/cost.

SQLite tests MUST receive rowversion-compatible values using the same project test convention used by other rowversion entities.

---

# Reconcile Triggers

MUST reconcile:

1. DR Release;
2. explicit DR `Refresh Fulfilment`;
3. immediately before DR→WO creation calculation;
4. immediately before DR→DO launch/server source preparation;
5. after a linked FG Receipt POST commits;
6. after a linked FG Receipt ROLLBACK commits;
7. after a NEW DR-linked shipment is removed and its transferred reservation must be rebuilt.

FG Receipt post/rollback core inventory/costing transaction MUST commit first.

Post-commit DR reconcile:

- runs synchronously;
- uses its own DbContext/transaction;
- MUST NOT undo or report the already-committed FG costing posting as failed if DR reconciliation fails;
- log the failure;
- return a non-blocking fulfilment synchronization warning to the FG Receipt UI;
- subsequent DR Refresh / Create WO / Create DO retries reconciliation.

This preserves financial/stock posting authority while keeping DR recoverable.

Do not add background jobs.

---

# DR Release

Modify lifecycle release:

1. current RowVersion validation;
2. lock DR;
3. revalidate exact SO sources;
4. require source-derived Fulfilment Warehouse;
5. validate Project/Priority;
6. set stored status RELEASED;
7. mark sources released using current logic;
8. save release;
9. reconcile DR stock reservation;
10. write audit;
11. commit.

Insufficient stock does NOT fail release.

It creates production requirement.

---

# DR Cancel

Before cancellation, check:

- current active WO allocation / production guards;
- any non-archived `SaDODetail` referencing this DR's sources.

If any DR-linked DO exists in NEW/POSTED/CLOSED:

- FAIL CLOSED;
- operator must resolve/delete the NEW DO first;
- posted/closed physical delivery cannot be silently undone by DR cancel.

Then:

- set CANCELLED;
- source `IsActive = false` using current logic;
- release all active DR stock reservations;
- preserve reservation history;
- preserve audit.

---

# Work Order Integration

Modify:

- `ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`
- `ErpWeb.Core/Production/IProductionWorkOrderService.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.cs`

Before DR→WO preview/create:

1. reconcile DR;
2. re-read fulfilment facts under current DR lock;
3. calculate `ProductionUnplannedQty`;
4. requested PlannedQty must be:
   `> 0` and `<= ProductionUnplannedQty + tolerance`.

Default UI PlannedQty:

```text
ProductionUnplannedQty
```

Preserve:

- Definition resolution;
- snapshot;
- BOM;
- schedule;
- machine/labour;
- costing;
- `PrWorkOrderDemandAllocation`;
- current release auditing;
- current one-DR-per-WO rule.

---

# Production Planning Visibility

Do NOT duplicate DR Project/Priority into Work Order table.

For DR-sourced Work Orders, derive via:

```text
PrWorkOrderDemandAllocation
→ SaDeliveryRequest
```

Extend Work Order list/detail DTO with:

```text
DemandDeliveryRequestNo
DemandRequiredDate
DemandPriority
DemandProjectCode
DemandFulfilmentWarehouse
```

Add optional Work Order list filters:

```text
DemandPriority
DemandProjectCode
```

Update:

- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor.cs`

This makes DR Priority/Project useful to Production without duplicating authority.

---

# Work Order Structural Safety for Procurement Links

Adding `POPRDtl.WorkOrderMaterialID` means Work Order material IDs become referenced business facts.

Modify:

- `ProductionWorkOrderService.Reopen.cs`
- `ProductionWorkOrderService.DraftCommands.cs`
- `ProductionWorkOrderService.Delete.cs`

## Reopen Guard

`ReopenForEditAsync` MUST reject if any `POPRDtl.WorkOrderMaterialID` references any material belonging to the WO.

Message:

```text
This Work Order has Purchase Requisition material links and cannot be reopened.
Resolve the linked procurement documents first.
```

---

## Draft Structural Guards

Defensive fail-closed check before any command that deletes/replaces/substitutes a referenced material:

- Refresh Definition confirmation;
- Change Definition confirmation;
- material substitution;
- draft hard delete.

If linked PR exists:

- do not delete/replace material identity;
- return `InUse`.

This is mandatory even though normal workflow prevents PR creation on Draft.

---

# Material Shortage

For each active linked WO material:

Only purchasing-relevant supply:

```text
PURCHASED
EXTERNAL_SUPPLY
```

Use shared `IProductionMaterialStockAvailabilityReader`.

Show:

```text
ComponentCode
ComponentDescription
RequiredBaseQty
BaseUom
WarehouseCode
IssueMethod
SupplySource
AvailableBaseQty
PhysicalShortBaseQty
OpenProcurementBaseQty
NetProcurementRequiredBaseQty
PR trace
PO trace
PO ETA
```

Formula:

```text
PhysicalShortBaseQty =
    MAX(RequiredBaseQty
        - available-to-material-allocation base qty,
        0)
```

Do not copy shortage to DR header.

---

# Procurement Coverage

Add proposed query/service:

- `ErpWeb.Core/Purchase/IWorkOrderMaterialProcurementTraceService.cs`
- `ErpWeb.Core/Purchase/WorkOrderMaterialProcurementTraceService.cs`

For one WorkOrderMaterial:

1. load non-cancelled linked `POPRDtl`;
2. PR standard qty is `PoPrDetail.StdQty`;
3. derive active/current PO consumption by `(PrNo, PrLineNo)`;
4. for each PO No use current/latest `PoRelNo`;
5. ignore CANCELLED current PO revision;
6. PO standard ordered qty = `PoOrderDetail.PoQty`;
7. PO open standard qty =
   `PoOrderDetail.BalanceQty * PoOrderCalc.EffectivePackSize(PackSz)`;
8. PR unordered standard qty =
   `MAX(PR StdQty - active/current PO standard ordered qty, 0)`;
9. open procurement standard qty =
   `PR unordered standard qty + active/current PO open standard qty`;
10. require PR/PO StdUom to match WorkOrderMaterial.BaseUom for coverage; otherwise report trace inconsistency and FAIL CLOSED for automatic calculation.

Then:

```text
OpenProcurementBaseQty =
    SUM(open procurement standard qty)

NetProcurementRequiredBaseQty =
    MAX(PhysicalShortBaseQty - OpenProcurementBaseQty, 0)
```

This prevents duplicate PR generation.

---

# Create PR From Material Shortage

Extend:

- `ErpWeb.Core/Purchase/IPoPrService.cs`
- `ErpWeb.Core/Purchase/PoPrService.cs`

Add explicit request:

```text
WorkOrderMaterialId
RequestedBaseQty
```

Server sequence:

1. load material + WO;
2. require WO status RELEASED or IN_PROGRESS;
3. require material SupplySource PURCHASED/EXTERNAL_SUPPLY;
4. get material stock availability;
5. get existing procurement coverage;
6. compute NetProcurementRequiredBaseQty;
7. requested qty must be `> 0`;
8. requested qty must be `<= NetProcurementRequiredBaseQty`;
9. resolve purchasing item using existing PR item rules;
10. require purchasing item `StdUom == material.BaseUom`;
11. derive PurchaseQty through current `PackSz`;
12. recompute StdQty with `PoPrCalc.ComputeStdQty`;
13. recomputed StdQty must equal requested base qty within current 4-decimal quantity precision;
14. set `ToWarehouse = material.WarehouseCode`;
15. derive Project through:
    `material.WorkOrder → active PrWorkOrderDemandAllocation → SaDeliveryRequest.ProjectCode`;
16. use current PR number/save/validation path;
17. persist `WorkOrderMaterialId`;
18. do not auto-approve;
19. do not auto-create PO.

---

# Linked PR Edit Rules

Extend `PoPrLineDto` with `WorkOrderMaterialId`.

Round-trip this property in:

- read;
- save/update;
- entity mapping.

For linked PR line:

MUST NOT allow manual change of:

- WorkOrderMaterialId;
- ICode;
- StdUom;
- ToWarehouse;
- source Work Order identity.

Quantity:

- unchanged qty is allowed;
- reduction is allowed while current PR lifecycle allows editing;
- increase requires re-running current net procurement requirement and may not exceed existing linked qty + currently uncovered requirement.

If PO already consumes the PR line, preserve all current PR/PO edit guards.

PR copy/clone:

- MUST clear `WorkOrderMaterialId`;
- copied PR is not automatically another demand commitment.

---

# DR → DO Exact Lineage

## DTO / Entity

Add `DeliveryRequestSourceId` to:

- `SaDoLineDto`;
- `SaDoLineRequest`;
- internal `PreparedLine`;
- `SaDoLineVm`;
- Clone;
- FromDto;
- ToRequest;
- persistence mapping.

---

## Server Validation

In `SaDoService.PrepareLinesAsync`, when `DeliveryRequestSourceId != null`:

1. load exact source and parent DR;
2. source must be active;
3. parent lifecycle must be RELEASED or IN_PRODUCTION;
4. company/branch match;
5. source SO No/revision/line match request;
6. customer match;
7. product match;
8. server-derived `PreparedLine.StdUom` matches source ProductionUom;
9. server-derived `PreparedLine.StdQty > 0`;
10. line warehouse equals DR Fulfilment Warehouse;
11. DO header Project equals DR Project;
12. all DR-linked lines in one DO must have same customer/project compatibility;
13. sum other non-archived DO `StdQty` for source;
14. current line StdQty must not exceed source remaining standard qty.

Never use request selling Qty as the DR limit.

---

# DO Prefill From DR

DR source row action:

```text
Create Delivery Order
```

Available when:

- DR lifecycle RELEASED / IN_PRODUCTION;
- source has standard qty not yet allocated to non-archived DOs;
- user has `MenuCodes.SalesDeliveryOrder` + Add permission.

Navigate:

```text
/sales/delivery-orders/new?deliveryRequestSourceId={sourceUid}
```

`SaDo.razor.cs` adds query parameter.

Startup:

1. load exact DR source through server service;
2. load same SO line through current SO service;
3. create line through existing `SaDoLineVm.FromSalesOrder`;
4. stamp `DeliveryRequestSourceId`;
5. default Warehouse = DR Fulfilment Warehouse;
6. default DO Project = DR Project;
7. calculate convenience selling Qty:
   `remaining DR standard qty / SO StdPsize`;
8. round using current Sales quantity precision;
9. server `PrepareLinesAsync` remains final StdQty authority;
10. if rounded selling qty would exceed DR standard remainder, reduce it;
11. do not create a new price/tax path.

---

# DR-Aware Shipment Reservation

Extend:

`IvSpRequiredLine`

with optional:

```text
long? DeliveryRequestSourceId
```

DO maps from detail.

Direct Invoice leaves null.

Modify `IvSpShipmentService` reservation calculation:

```text
available =
    physical StdQty
    - other NEW SP reservations
    - active DR reservations
```

When processing a DR-linked line:

- exclude that same `DeliveryRequestSourceId` reservation from "other DR reservation" so the DO can consume stock reserved for itself.

All other DR reservations remain unavailable.

---

# Transfer DR Reservation → NEW SP Reservation

When a DR-linked DO shipment is successfully created/rebuilt:

1. DR source/parent was already validated;
2. SP service creates actual NEW SP details;
3. read SP allocation quantity for each DR-linked DO line;
4. lock active reservation rows for the same DR source;
5. release reservation quantity equal to the new SP standard quantity;
6. use deterministic reservation-row order;
7. write ReleasedDate/By/Reason = shipment transfer;
8. if SP qty is greater than active DR reservation, only available reservation is transferred; excess SP is valid only if stock allocator proved availability;
9. save in the same DO shipment transaction.

Invariant after commit:

```text
DR stock reservation + NEW DR-linked SP reservation
```

does not double count the transferred quantity.

---

# Shipment Removal / DO Edit

If a NEW shipment reservation is removed/rebuilt and DR reservation had previously been transferred:

- commit the normal DO/SP mutation first;
- synchronously reconcile affected DR(s) immediately after commit;
- if reconciliation fails, log and return a non-blocking DR fulfilment-sync warning;
- under-reservation is temporarily allowed;
- over-reservation/double reservation is not allowed.

Before a new WO or DR-linked DO can be created, DR reconciliation runs again, so an under-reserved stale state cannot cause duplicate supply planning.

Do not make Sales posting rollback depend on a separate DR transaction.

---

# DO Post / Rollback / Force-Close

## Post

No additional DR quantity counter is written.

Before:

```text
Open 100
DR reserve 40
NEW SP 20
Ready 60
```

After POST:

```text
Delivered +20
NEW SP -20
Open -20
```

Supply gap remains mathematically consistent.

---

## Rollback

Existing inventory rollback remains authority.

POSTED → NEW causes:

- DeliveredQty decreases;
- NEW SP reservation becomes live again through existing shipment lifecycle;
- DR facts update by query;
- no manual DR delivered counter reversal.

---

## Force Close

CLOSED DO is already physically shipped.

Its DR-linked `StdQty` remains DeliveredQty.

Do not treat uninvoiced write-off as undelivered stock.

---

# Direct SO → Invoice Guard

Modify:

`SaInvoiceService.PrepareLinesAsync` / direct-SO reserve validation.

For direct SO line (`LinkDo == false`):

Reject if exact SO revision/line is referenced by an active DR source whose parent lifecycle is:

```text
RELEASED
IN_PRODUCTION
```

DRAFT DR does NOT block invoicing.

CANCELLED DR does NOT block.

Policy is intentionally strict:

> once a Sales Order line enters a released DR fulfilment flow, that SO line must be delivered through DO so DR traceability cannot be bypassed.

This applies even when only part of the SO line is DR-allocated.

The non-DR remainder may still be delivered through a normal DO, but not directly invoiced from SO while the line is DR-controlled.

LinkDo invoices remain unchanged.

---

# Material / Procurement Trace

DR detail trace:

```text
DR
→ PrWorkOrderDemandAllocation
→ ProductionWorkOrder
→ PrWorkOrderMaterial
→ POPRDtl.WorkOrderMaterialID
→ PoOrderDetail.PrNo/PrLineNo
→ GRN / Goods Receipt
```

Do not add redundant DR ID to PR/PO/GRN.

Show clickable references where current navigation supports them.

---

# Finished Goods Receipt Integration

## UI Default

For FG receipt created from a DR-sourced Work Order:

- default destination Warehouse to DR Fulfilment Warehouse when no user destination is already set;
- do not hard-block a different warehouse;
- if different, show warning:
  `FG is being received outside DR fulfilment warehouse; DR will require transfer/other stock before it becomes Ready.`

Do not change costing/posting because of this warning.

---

## Post / Rollback Reconcile

Modify:

- `ProductionFinishedGoodReceiptService.cs`
- `ProductionFinishedGoodReceiptService.Posting.cs`
- `IProductionFinishedGoodReceiptService.cs`
- FG Receipt UI only for warning display.

After successful transaction commit:

1. determine linked DR IDs through WorkOrder demand allocations;
2. call DR fulfilment reconcile synchronously;
3. do not nest the DR transaction inside the financial/stock posting transaction.

If DR reconcile fails:

- FG receipt remains successfully POSTED/REVERSED;
- log warning;
- return a fulfilment synchronization warning;
- DR Refresh / Create WO / Create DO retries.

This is mandatory to preserve costing atomicity.

---

# DR Detail DTO

Extend `SaDeliveryRequestDetail` with:

```text
LifecycleStatus
FulfilmentStatus

OpenDemandQty
DrStockReservedQty
NewShipmentReservedQty
ReadyQty

ActiveWoAllocatedQty
LinkedFgReceivedQty
OutstandingWoSupplyQty
ProductionRequiredQty
ProductionUnplannedQty
ProducedQty

DeliveryAllocatedQty
DeliveredQty
FulfilledDate

BlockerCode
ForecastReadyDate
IsAtRisk

StockReservations[]
DeliveryTrace[]
MaterialShortages[]
```

For backward compatibility `Status` may remain an alias to stored lifecycle status during transition, but UI MUST use explicit labels.

---

# Blocker Rules

Codes:

```text
NONE
WO_NOT_PLANNED
MATERIAL_SHORTAGE
PROCUREMENT_LATE
PRODUCTION_LATE
STOCK_NOT_READY
```

Deterministic precedence:

1. PROCUREMENT_LATE
2. MATERIAL_SHORTAGE
3. PRODUCTION_LATE
4. WO_NOT_PLANNED
5. STOCK_NOT_READY
6. NONE

Rules:

## WO_NOT_PLANNED

```text
ProductionUnplannedQty > tolerance
```

## MATERIAL_SHORTAGE

- OutstandingWoSupplyQty > 0;
- purchased/external WO material has `NetProcurementRequiredBaseQty > 0`.

## PROCUREMENT_LATE

- material has open PO pipeline;
- latest relevant ETA > DR RequiredDate.

## PRODUCTION_LATE

- no unresolved material/procurement blocker;
- active linked WO planned completion > DR RequiredDate.

## STOCK_NOT_READY

- no production gap;
- ReadyQty < OpenDemandQty;
- supply is produced/expected but not yet physically reserved at fulfilment warehouse.

Do not implement APS.

---

# Forecast Ready Date

Keep simple and explainable.

If READY:

```text
ForecastReadyDate = current business date
```

If unplanned production exists:

```text
ForecastReadyDate = null
```

If procurement blocker exists:

```text
ForecastReadyDate =
    max(relevant open PO ETA, linked WO planned completion)
```

If production only:

```text
ForecastReadyDate =
    max(active linked WO planned completion)
```

`IsAtRisk` when:

```text
not COMPLETED
AND (
    RequiredDate < Today
    OR ForecastReadyDate is null with unresolved blocker
    OR ForecastReadyDate.Date > RequiredDate.Date
)
```

---

# DR Inquiry Filters

Extend `SaDeliveryRequestListQuery`:

```text
SearchText
SoNo
CustomerCode
ProductCode
WarehouseCode
ProjectCode
Priority
LifecycleStatus
FulfilmentStatus
WorkOrderNo
BlockerCode
RequiredDateFrom
RequiredDateTo
DueMode
SortField
SortDescending
Skip
Take
```

DueMode:

```text
OPEN
DUE_TODAY
DUE_THIS_WEEK
OVERDUE
AT_RISK
PRODUCTION_REQUIRED
MATERIAL_SHORTAGE
READY_FOR_DELIVERY
PARTIAL
COMPLETED
```

---

# DR List Columns

At minimum:

```text
DR
Priority
Customer
Product
Warehouse
Project
Requested
Ready
Production Required
Production Unplanned
WO
Produced
Delivered
Required Date
Lifecycle
Fulfilment Status
Blocker
```

Keep server paging.

Do not load all DRs into Blazor memory and filter there.

---

# KPI

Add:

```text
OpenDrCount
OpenDrQty
DueTodayCount
DueThisWeekCount
OverdueCount
AtRiskCount
ReadyForDeliveryCount
ReadyForDeliveryQty
MaterialShortageCount
PartialFulfilmentCount
OnTimeFulfilmentPercent
```

Definitions:

```text
OpenDrQty =
    SUM(OpenDemandQty)

Overdue =
    OpenDemandQty > 0
    AND RequiredDate < Today

ReadyForDelivery =
    OpenDemandQty > 0
    AND ReadyQty >= OpenDemandQty

PartialFulfilment =
    DeliveredQty > 0
    AND DeliveredQty < RequestedQty

OnTimeFulfilmentPercent =
    completed DR with FulfilledDate <= RequiredDate
    / completed DR
```

Use current business date service, not browser date.

---

# Query / Performance Rules

Create set-based fulfilment aggregate queries.

MUST NOT call:

```text
GetFactsAsync(drId)
```

once per row for an unbounded list.

For list page:

1. apply header/source/WO search filters using SQL EXISTS/subqueries;
2. select page DR IDs;
3. load delivery/WO/FG/reservation aggregates for the page in batched queries;
4. enrich page rows;
5. for DueMode/fulfilment filters that must be evaluated before paging, use SQL-translatable aggregate subqueries or dedicated set-based readers.

Material shortage DueMode/KPI MUST use a set-based method from the proposed material stock-availability reader; no N+1 material allocation calls.

---

# UI — DR Entry

Follow current Sales transaction standards.

Header:

```text
DR No
Product [IvStockMasterPicker]
Production UOM [read-only]
Required Date
Fulfilment Warehouse [lookup/filter, then source-locked]
Project [lookup/filter, then source-locked]
Priority [NORMAL/HIGH/URGENT]
Remark
```

Sections:

## Demand Sources

Show:

```text
SO
Revision
Line
Customer
Product
Warehouse
Project
Required Date
Allocated Std Qty
Delivered Std Qty
Open Std Qty
```

## Fulfilment

Cards:

```text
Requested
Delivered
Open
DR Stock Reserved
NEW Shipment Reserved
Ready
Production Required
Production Unplanned
```

## Production

Show:

```text
Definition lookup
Create Work Order
WO trace
Planned
Good
FG Received
Outstanding WO Supply
```

## Material / Procurement

Show:

```text
Component
Required
Available
Physical Short
Open Procurement
Net To Procure
PR
PO
ETA
Blocker
```

Actions:

```text
Create PR
```

only when user has:

```text
MenuCodes.PurchaseRequisition + Add
```

and `NetProcurementRequiredBaseQty > 0`.

## Delivery

Per source:

```text
Create Delivery Order
```

only when user has:

```text
MenuCodes.SalesDeliveryOrder + Add
```

and source has remaining non-delivered qty.

## Trace

Clickable where supported:

```text
SO → DR → Stock/WO → PR → PO → GRN → DO
```

Add:

```text
Refresh Fulfilment
```

for released/in-production DR.

---

# UI — DR List

Preserve standard list action/header/grid pattern.

Add:

- priority badge;
- lifecycle badge;
- fulfilment badge;
- blocker badge;
- KPI strip;
- advanced filters;
- Ready/Delivered/Production columns.

Do not display meaningless free-text Warehouse/Project/Priority.

---

# Exact Existing Files To Change

## Sales / DR

- `ErpWeb.Core/Sales/ISaDeliveryRequestService.cs`
- `ErpWeb.Core/Sales/SaDeliveryRequestService.cs`
- `ErpWeb.Model/Entities/Sales/SaDeliveryRequest.cs`
- `ErpWeb.Model/Entities/Sales/SaDeliveryRequestSource.cs`
- `ErpWeb.Model/Configurations/Sales/SaDeliveryRequestConfiguration.cs`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor.css`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor.cs`
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestList.razor.css`
- `ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestServiceTests.cs`
- `scripts/create-sales-delivery-request.sql`

## Sales / DO

- `ErpWeb.Model/Entities/Sales/SaDoDetail.cs`
- `ErpWeb.Model/Configurations/Sales/SaDoDetailConfiguration.cs`
- `ErpWeb.Core/Sales/ISaDoService.cs`
- `ErpWeb.Core/Sales/SaDoService.cs`
- `ErpWeb.UI/Sales/Transactions/SaDo.razor`
- `ErpWeb.UI/Sales/Transactions/SaDo.razor.cs`
- `ErpWeb.Tests/Sales/Transaction/SaDoServiceTests.cs`

## Sales / Invoice

- `ErpWeb.Core/Sales/SaInvoiceService.cs`
- existing `SaInvoiceService` tests under `ErpWeb.Tests/Sales/Transaction/`

## Inventory Shipment

- `ErpWeb.Core/Inventory/IIvSpShipmentService.cs`
- `ErpWeb.Core/Inventory/IvSpShipmentService.cs`
- `ErpWeb.Core/Inventory/IvSpFifoEligibility.cs` only if exposing existing internal matching safely; behavior MUST remain equivalent
- current SP shipment tests

`IvSpShipmentAllocator.cs` MUST remain unchanged unless a failing DR integration test proves a required allocator change.

## Production / WO

- `ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.Reopen.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.DraftCommands.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.Delete.cs`
- `ErpWeb.Core/Production/IProductionWorkOrderService.cs`
- `ErpWeb.Core/Production/ProductionWorkOrderService.cs`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor.cs`
- `ErpWeb.Tests/Planning/Transaction/ProductionWorkOrderSqlServerConcurrencyTests.cs`
- relevant Work Order unit tests

## Production / Material

- `ErpWeb.Core/Production/IProductionMaterialAllocationService.cs`
- `ErpWeb.Core/Production/ProductionMaterialAllocationService.cs`
- `ErpWeb.Tests/Production/Transaction/ProductionMaterialAllocationTests.cs`

## Production / FG Receipt

- `ErpWeb.Core/Production/IProductionFinishedGoodReceiptService.cs`
- `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.cs`
- `ErpWeb.Core/Production/ProductionFinishedGoodReceiptService.Posting.cs`
- `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrFinishedGoodReceiptEntry.razor.cs`
- `ErpWeb.Tests/Production/Transaction/FinishedGoodReceiptTests.cs`

## Procurement / PR

- `ErpWeb.Model/Entities/Purchase/PoPrDetail.cs`
- `ErpWeb.Model/Configurations/Purchase/PoPrDetailConfiguration.cs`
- `ErpWeb.Core/Purchase/IPoPrService.cs`
- `ErpWeb.Core/Purchase/PoPrService.cs`
- `ErpWeb.UI/Purchase/Transactions/PoPr.razor`
- `ErpWeb.UI/Purchase/Transactions/PoPr.razor.cs`
- `ErpWeb.Tests/Procurement/Transaction/PoPrServiceTests.cs`
- `scripts/create-po-pr.sql`

## Shared

- `ErpWeb.Model/Data/AppDbContext.cs`
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`

---

# Proposed New Files

- `ErpWeb.Model/Entities/Sales/SaDeliveryRequestStockReservation.cs`
- `ErpWeb.Model/Configurations/Sales/SaDeliveryRequestStockReservationConfiguration.cs`
- `ErpWeb.Core/Inventory/IInventorySoftReservationReader.cs`
- `ErpWeb.Core/Inventory/InventorySoftReservationReader.cs`
- `ErpWeb.Core/Sales/ISaDeliveryRequestFulfilmentService.cs`
- `ErpWeb.Core/Sales/SaDeliveryRequestFulfilmentService.cs`
- `ErpWeb.Core/Production/IProductionMaterialStockAvailabilityReader.cs`
- `ErpWeb.Core/Production/ProductionMaterialStockAvailabilityReader.cs`
- `ErpWeb.Core/Purchase/IWorkOrderMaterialProcurementTraceService.cs`
- `ErpWeb.Core/Purchase/WorkOrderMaterialProcurementTraceService.cs`
- `ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestFulfilmentServiceTests.cs`
- `ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestSqlServerConcurrencyTests.cs`
- `scripts/alter-sales-delivery-request-fulfilment.sql`

Do not create additional repository/service abstractions unless a verified dependency requires them.

---

# Concurrency / Locking

## Reservation Race Invariant

For each BalLoc:

```text
effective active DR reservation
+ NEW SP reservation
<= usable physical quantity
```

at the locked allocation snapshot.

DR reservation reconcile MUST lock BalLocs through existing inventory repository lock method in deterministic stock-slice order.

SQL Server concurrency test is mandatory.

---

## DR Source Over-allocation

Keep current DR exact SO source allocation locks.

Do not weaken current `UPDLOCK/HOLDLOCK` behavior.

---

## DR → WO

Keep current DR allocation locks.

New WO amount is checked after stock reconcile, while DR is locked.

---

## DR → DO

DO service MUST re-read DR source under server control.

Two concurrent DO saves for same DR source MUST not allow combined `StdQty` above source remaining.

Add SQL Server concurrency coverage if existing SQLite test cannot prove this race.

---

# Rollback / Reversal

## DR Draft Delete

- DRAFT has no stock reservations by design.
- if corrupt active reservation exists, FAIL CLOSED rather than silently delete reservation history.

## DR Cancel

- block linked DO;
- block current production according to existing rules;
- release DR stock reservations;
- preserve audit/history.

## Work Order Reopen/Delete/Refresh

- procurement-linked material IDs block destructive structural changes;
- existing production execution guards remain.

## DO Rollback

- current physical reversal remains untouched;
- DeliveredQty is derived and automatically decreases;
- NEW SP becomes supply reservation again.

## FG Receipt Rollback

- current production/inventory/cost reversal remains untouched;
- LinkedFgReceivedQty excludes REVERSED receipt;
- post-commit DR reconcile adjusts stock reservation.

## PR/PO/GRN Rollback/Cancel

- existing procurement lifecycle remains authority;
- procurement trace/coverage is derived;
- DR stores no duplicate procurement status.

---

# Tests

# A. DR Source / Header Tests

Extend `SaDeliveryRequestServiceTests`:

- Product remains SO-derived.
- Product picker mismatch rejected.
- mixed Product rejected.
- mixed ProductionUom rejected.
- mixed Warehouse rejected.
- mixed Project rejected.
- inactive Warehouse rejected.
- invalid Project rejected.
- blank Project allowed when sources blank.
- Priority blank → NORMAL.
- invalid Priority rejected.
- RequestedQty remains source sum.
- RequiredDate earliest-source rule preserved.
- DRAFT DR does not reserve stock.
- RELEASED DR can reserve stock.
- Work Order GoodQty does not produce FulfilmentStatus COMPLETED.
- delivered DO StdQty does.

---

# B. Stock Reservation Tests

New `SaDeliveryRequestFulfilmentServiceTests`:

## Full Stock

```text
Demand 100
No WO
Usable stock 100
DR reserve 100
Ready 100
ProductionRequired 0
ProductionUnplanned 0
```

## Partial Stock

```text
Demand 100
Usable stock 60
DR reserve 60
ProductionRequired 40
```

## No Stock

```text
Demand 100
DR reserve 0
ProductionRequired 100
```

## Existing SP

NEW SP reservation from another document reduces DR available stock.

## Other DR

Other DR active reservation reduces available stock.

## Current DR reconcile

Does not subtract itself twice.

## Future stock

Balance with TransDate after current business date is not current-ready DR stock.

## Reconcile idempotency

Repeated reconcile produces same active quantities and no duplicate active source/BalLoc row.

## Cancel

Active reservation becomes inactive and history remains.

---

# C. Supply Transition Tests

## Before WO

```text
Demand 100
Stock 60
Reservation 60
ProductionUnplanned 40
```

## After WO 40

```text
OutstandingWoSupply 40
TargetDrReservation 60
ProductionUnplanned 0
```

## Linked FG Receipt 20

```text
LinkedFgReceived 20
OutstandingWoSupply 20
Reservation target 80
ProductionRequired 20
ProductionUnplanned 0
```

## Linked FG Receipt full 40

```text
LinkedFgReceived 40
OutstandingWoSupply 0
Reservation target 100
ProductionRequired 0
ProductionUnplanned 0
```

## FG Receipt rollback 20

facts return to prior state after reconcile.

This test is mandatory to prove no WO/stock double count.

---

# D. SQL Server Reservation Concurrency

`SaDeliveryRequestSqlServerConcurrencyTests`

Scenario:

```text
Physical stock 10
DR-A target 10
DR-B target 10
concurrent reconcile
```

Assert:

- combined active reservation <= 10;
- no duplicate active source/BalLoc;
- no negative availability;
- one/both operations resolve deterministically without over-reserving.

---

# E. DR → WO Tests

- Demand100 / reserve60 → max new WO40.
- request41 rejected.
- request40 succeeds.
- linked FG receipt reduces outstanding WO supply.
- Work Order snapshot/BOM/cost outputs unchanged.
- existing DR one-WO allocation invariants unchanged.

---

# F. Work Order Procurement Dependency Tests

- PR-linked material blocks ReopenForEdit.
- PR-linked material blocks Refresh confirmation.
- PR-linked material blocks Change Definition.
- PR-linked material blocks substitution of that material.
- PR-linked material blocks Draft delete.
- no linked PR preserves current behavior.

---

# G. DO Lineage / UOM Tests

- exact `DeliveryRequestSourceId` persists.
- wrong SO No rejected.
- wrong revision rejected.
- wrong SO line rejected.
- wrong Product rejected.
- wrong StdUom rejected.
- wrong Warehouse rejected.
- wrong Project rejected.
- selling Qty with pack size converts to correct StdQty.
- source remaining check uses StdQty.
- box→pcs case proves no selling/standard mix-up.
- combined concurrent DO StdQty cannot exceed DR source qty.
- non-DR DO behavior unchanged.

---

# H. Shipment Transfer Tests

- unrelated shipment cannot consume other DR reserved stock.
- DR-linked shipment can use its own DR reservation.
- after SP creation, transferred DR reservation is inactive.
- total ready supply remains unchanged across DR→SP transfer.
- rebuild does not double reserve.
- remove shipment then reconcile restores eligible DR reservation.
- direct invoice shipment sees released DR reservations as unavailable.

---

# I. Delivery Status Tests

- NEW DO without SP is not Ready.
- NEW DO with SP contributes NewShipmentReservedQty.
- POSTED DO contributes DeliveredQty.
- POSTED SP no longer contributes NEW shipment reservation.
- rollback POSTED→NEW decreases Delivered and restores NEW SP ready supply.
- CLOSED force-closed DO remains delivered.
- archived DO contributes neither allocation nor delivery.

---

# J. Direct Invoice Tests

- DRAFT DR does not block direct SO invoice.
- RELEASED active DR source blocks direct SO invoice.
- IN_PRODUCTION blocks.
- CANCELLED does not block.
- LinkDo invoice remains allowed.
- unrelated SO line unchanged.

---

# K. Material Shortage Tests

Protect current `ProductionMaterialAllocationTests`.

Add reader tests:

- MANUAL purchased matches existing availability result.
- BACKFLUSH purchased can be read for shortage inquiry.
- PICK_LIST external supply can be read for shortage inquiry.
- INTERNAL_ROUTE_WIP excluded from purchase shortage.
- future stock excluded correctly.
- other draft issue reservations reduce available qty.

---

# L. Procurement Coverage Tests

- no PR → open procurement 0.
- PR50 / no PO → open procurement50.
- PR100 / PO60 open → PR unordered40 + PO open60 =100.
- PO partly received updates PO open using BalanceQty * PackSz.
- cancelled current PO no longer counts as PO coverage and PR remainder becomes available again.
- latest PO revision only; historical revision not double-counted.
- cancelled PR excluded.
- duplicate Create PR beyond net procurement requirement rejected.
- ordinary manual PR unchanged.

---

# M. PR Link Tests

- Create PR from material stores `WorkOrderMaterialId`.
- linked item/UOM/warehouse cannot be manually changed.
- linked qty reduction allowed when existing lifecycle allows.
- increase revalidates uncovered procurement need.
- copy clears WorkOrderMaterialId.
- PR→PO still works using existing keys.
- PO→GRN unchanged.

---

# N. FG Receipt Tests

Extend `FinishedGoodReceiptTests`:

- DR-linked WO defaults fulfilment warehouse in UI/service projection.
- different destination allowed with warning.
- POST leaves costing/stock assertions unchanged.
- post triggers DR reconcile only after commit.
- reconcile failure does not rollback FG posting.
- rollback triggers DR reconcile after commit.
- linked FG received projection excludes REVERSED receipt.

---

# O. Inquiry/KPI Tests

- filter DR No.
- filter SO.
- filter Customer.
- filter Product.
- filter Warehouse.
- filter Project.
- filter Priority.
- filter lifecycle.
- filter fulfilment status.
- filter WO.
- Due Today.
- Due This Week.
- Overdue.
- At Risk.
- Production Required.
- Material Shortage.
- Ready for Delivery.
- Partial.
- Completed.
- OpenDrQty.
- ReadyForDeliveryQty.
- OnTimeFulfilmentPercent.
- server paging remains correct after derived filters.

---

# Implementation Order

## STEP 0 — Re-verify Branch

- confirm HEAD;
- diff affected files if HEAD changed;
- stop approval execution if source architecture materially changed.

## STEP 1 — Add Focused Failing Tests

Before implementation add tests for:

- DR lifecycle vs fulfilment status;
- standard vs selling UOM;
- supply-transition formula;
- procurement duplicate prevention;
- Work Order material FK blocker;
- stock reservation concurrency.

## STEP 2 — Schema

Implement:

- reservation table;
- DO source link;
- PR material link;
- indexes/FKs;
- AppDbContext;
- idempotent SQL;
- fresh-install script changes.

Build.

## STEP 3 — Shared Stock Reservation Reader

Refactor current SP reservation query into `IInventorySoftReservationReader`.

Run all shipment tests before adding DR reservation to calculation.

## STEP 4 — DR Fulfilment Facts + Reservation

Implement:

- delivery aggregates;
- FG receipt aggregates;
- WO outstanding supply;
- reservation target;
- stock reconcile.

Run focused tests.

## STEP 5 — DR Source/Header UX Authority

Implement Product picker, source Warehouse/Project, Priority, Definition lookup.

Do not modify WO/DO yet.

## STEP 6 — DR Lifecycle

Release/reconcile/cancel guards.

## STEP 7 — Stock-Aware DR→WO

Change only DR WO max/default quantity and production-demand metadata.

Do not alter snapshot/cost/schedule core.

## STEP 8 — Production Material Availability Refactor

Extract common stock-availability reader.

Prove existing material allocation tests unchanged.

## STEP 9 — Procurement Link + Coverage

Add WorkOrderMaterialId, trace service, Create PR, duplicate prevention.

## STEP 10 — Work Order Structural Guards

Reopen/refresh/change-definition/substitution/delete protection.

## STEP 11 — DR→DO Lineage

Add DTO/entity/service/UI link with StdQty validation.

## STEP 12 — DR-Aware SP Reservation

Integrate active DR reservations and transfer to SP.

## STEP 13 — Direct Invoice Guard

Released/InProduction only.

## STEP 14 — FG Receipt Synchronization

Default warehouse + post-commit reconcile.

## STEP 15 — DR Entry Fulfilment Cockpit

Add summaries/traces/actions.

## STEP 16 — DR Inquiry / KPI

Set-based queries, filters, KPI.

## STEP 17 — Full Regression

Run all focused + full suites.

---

# Regression Areas

MUST prove unchanged where not explicitly modified:

## Sales

- SO save/revision;
- SO allocation;
- pricing;
- tax;
- ordinary DO;
- ordinary DO shipment;
- direct invoice with no released DR;
- DO→Invoice;
- force-close semantics.

## Inventory

- physical stock posting;
- shipment FIFO eligibility;
- future-stock restriction;
- lot handling;
- inventory rollback;
- cost ledger;
- stock history.

## Production

- definition snapshot;
- BOM;
- scheduling;
- material issue;
- output;
- FG receipt valuation;
- absorbed costs;
- Work Order rollback/reopen except new procurement blocker.

## Procurement

- manual PR;
- PR approval/status;
- PR→PO consumption;
- PO revision;
- GRN;
- purchase invoice;
- procurement costing.

## Accounting / Month End

No intentional behavior change.

---

# Do-Not Rules

DO NOT:

- trust client Product/UOM/RequestedQty;
- allow Warehouse/Project to drift from selected SO sources;
- compare DR standard qty to DO selling Qty;
- count bare NEW DO qty as Ready;
- count Work Order GoodQty as customer DeliveredQty;
- double-count WO supply after FG receipt;
- create duplicate PR while linked PR/PO supply is already open;
- use `ProductionMaterialAllocationService.AutoAllocateAsync` for BACKFLUSH/PICK_LIST and assume it succeeds;
- delete/rebuild PR-linked Work Order material;
- decrement `IvBalLoc` for DR reservation;
- invent new FIFO eligibility;
- infer old DR→DO links;
- infer old PR→material links;
- change cost authority;
- change posting sequence except explicitly described post-commit DR sync;
- make FG financial posting depend on DR sync success;
- weaken RowVersion;
- weaken SQL stock locks;
- bypass existing PR/PO lifecycle;
- add unrelated refactoring.

---

# Acceptance Criteria

- [ ] Agent re-verifies current `productionv2` HEAD before coding.
- [ ] Solution builds.
- [ ] Existing DR source/current-revision behavior is preserved.
- [ ] Product uses `IvStockMasterPicker` before sources.
- [ ] Product/UOM become source-locked after sources.
- [ ] Warehouse is active lookup/filter then source-authoritative.
- [ ] Project is active lookup/filter then source-authoritative.
- [ ] Priority is NORMAL/HIGH/URGENT and affects inquiry/production visibility.
- [ ] Definition is controlled lookup, not free text.
- [ ] DR RequestedQty remains source-derived.
- [ ] DR stock reservation never changes `IvBalLoc.StdQty`.
- [ ] Reservation candidate eligibility matches existing SP eligibility.
- [ ] NEW SP and DR reservation are never double-counted.
- [ ] Bare NEW DO line is not treated as ready stock.
- [ ] DO DR quantity validation uses server-derived StdQty.
- [ ] box→pcs test passes.
- [ ] linked FG receipt reduces OutstandingWoSupplyQty.
- [ ] production supply is not double-counted after FG receipt.
- [ ] ProductionUnplannedQty is correct through stock→WO→FG transitions.
- [ ] new WO cannot exceed ProductionUnplannedQty.
- [ ] existing WO snapshot/BOM/schedule/costing remains unchanged.
- [ ] material shortage works for purchasing-relevant MANUAL/BACKFLUSH/PICK_LIST rows.
- [ ] existing open PR/PO supply reduces NetProcurementRequiredBaseQty.
- [ ] duplicate PR creation is prevented.
- [ ] linked PR stores exact WorkOrderMaterialId.
- [ ] PR-linked material blocks destructive WO structural changes.
- [ ] DR-linked DO stores exact DeliveryRequestSourceId.
- [ ] split SO demand across DRs remains unambiguous.
- [ ] shipment transfer preserves total ready quantity.
- [ ] POSTED/CLOSED DO is delivery authority.
- [ ] DO rollback automatically removes delivered quantity.
- [ ] DRAFT DR does not block direct SO invoice.
- [ ] RELEASED/IN_PRODUCTION DR does block direct SO invoice.
- [ ] LinkDo invoice remains unchanged.
- [ ] Fulfilment COMPLETED is based on DeliveredQty, not WO GoodQty.
- [ ] FG receipt post/rollback core costing transaction is unchanged.
- [ ] failed post-commit DR sync cannot roll back FG costing.
- [ ] DR Refresh recovers any under-reserved synchronization warning.
- [ ] list/inquiry remains server-paged.
- [ ] KPI formulas are tested.
- [ ] SQL Server reservation concurrency test passes.
- [ ] DO source over-allocation concurrency is covered.
- [ ] all Sales regression tests pass.
- [ ] all Inventory shipment regression tests pass.
- [ ] all Production regression tests pass.
- [ ] all Procurement regression tests pass.
- [ ] no Accounting/Month-End/costing behavior changed.

---

# Code-Agent Verification Commands

Agent SHOULD run the repository's normal build/test commands discovered from the solution.

At minimum:

```text
dotnet build
```

Then focused tests for:

```text
SaDeliveryRequest
SaDo
SaInvoice
IvSpShipment
ProductionWorkOrder
ProductionMaterialAllocation
FinishedGoodReceipt
PoPr
PoOrder
```

Then full test project.

SQL Server-specific concurrency tests MUST run against the repository's existing SQL Server test fixture/configuration; do not substitute SQLite for the final lock proof.

---

# Plan Scorecard

| Category | Score | Basis |
|---|---:|---|
| Repository correctness | 10/10 | Re-verified against current `productionv2` HEAD |
| Architecture compatibility | 10/10 | Reuses SO, SP, WO, FG, PR/PO authorities |
| Data/schema correctness | 10/10 | Exact existing tables/config paths verified |
| Transaction integrity | 10/10 | Physical posting remains authoritative; DR sync separated where necessary |
| Rollback/reversal completeness | 10/10 | DO, WO, FG, PR/PO effects covered |
| Concurrency/locking safety | 10/10 | Existing locks preserved; SQL Server reservation race required |
| Costing integrity | 10/10 | No new costing authority; FG/Inventory posting untouched |
| Regression safety | 10/10 | Module-specific guards/tests required |
| Test completeness | 10/10 | Includes UOM, supply transition, procurement and concurrency |
| Code-Agent implementability | 10/10 | Exact files, contracts, formulas, sequence and blockers defined |

**Overall plan-readiness score: 10/10**

This is a score for the implementation specification. The implementation itself is not approved until all acceptance criteria and tests pass.

---

# Approval Status

**APPROVED FOR IMPLEMENTATION**
