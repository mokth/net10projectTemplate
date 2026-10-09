# Delivery Request Fulfilment Control Enhancement
## APPROVED FOR IMPLEMENTATION — `productionv2`

**Repository:** `mokth/net10projectTemplate`  
**Branch:** `productionv2`  
**Verified branch commit:** `213cdbd1e1cf77a890d1604eaf9d2873370855bb`  
**Requirement basis:** `Delivery_Request_DR_Usage_Inquiry_KPI_Summary(1).md`  
**Target:** SME ERP fulfilment control: Sales Order → Delivery Request → Stock / Production / Procurement → Delivery Order → Invoice.

---

# Objective

Upgrade the existing Delivery Request (DR) from a lightweight Sales Order → Work Order handoff into the repository's common fulfilment-control reference.

The implementation MUST make Product, Warehouse, Project, Priority, Required Date, stock fulfilment, production requirement, material shortage, procurement trace, delivery trace, inquiry and KPI data operationally meaningful.

The implementation MUST preserve the existing authority boundaries:

- Sales Order remains the authority for customer demand, product and standard production UOM.
- `IvBalLoc` and the existing inventory posting/stock services remain physical-stock authority.
- Work Order remains manufacturing/BOM/scheduling authority.
- `PrWorkOrderMaterial` remains production material-requirement authority.
- PR/PO/GRN remain procurement authority.
- DO shipment/posting remains physical customer-delivery authority.
- DR coordinates and traces these authorities; it MUST NOT duplicate their accounting, costing, BOM or stock-posting logic.

---

# Confirmed Current Problems

## 1. Product UI is read-only and creation is source-first only

**Current file**
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor`

**Current behavior**
- Product is rendered as a read-only HTML input.
- Product becomes known only after an SO source row is added.

**Verified supporting code**
- `SaDeliveryRequestEntry.razor.cs::AddEligibleSource`
- `SaDeliveryRequestService.PrepareSourceRowsAsync`

**Required correction**
- New DR MUST allow Product selection through the existing `IvStockMasterPicker`.
- Product selection is a demand filter, not an override of Sales Order truth.
- Once one or more source rows exist, Product/UOM MUST be source-controlled and locked.
- Server validation in `PrepareSourceRowsAsync` MUST remain authoritative.

---

## 2. Warehouse, Project and Priority are currently free-text header decorations

**Current files**
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor`
- `ErpWeb.Core/Sales/SaDeliveryRequestService.cs`

**Current behavior**
- `WarehouseCode`, `ProjectCode` and `Priority` are normal text inputs.
- `ApplyHeader` only trims and stores them.
- Warehouse is not validated against active `IvWarehouse`.
- Project does not currently use shared `MsRefLookupRules`.
- Priority accepts any text.

**Required correction**
- Warehouse MUST be an active warehouse lookup and become the DR **Fulfilment Warehouse**.
- Project MUST be an active `MsProject` lookup and use `MsRefLookupRules`.
- Priority MUST be controlled: `NORMAL`, `HIGH`, `URGENT`.
- These values MUST drive fulfilment/inquiry behavior described below.

---

## 3. Product Definition is free text although Work Order has a verified definition lookup

**Current file**
- `ErpWeb.UI/Sales/Transactions/SaDeliveryRequestEntry.razor`

**Current behavior**
- `DefinitionCode` is free text.
- DR→WO creation consumes it in `ProductionWorkOrderService.DeliveryRequest.cs`.

**Verified reusable behavior**
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderEntry.razor.cs`
- `IPrProductDefService.ListActiveDefinitionsAsync`

**Required correction**
- Product Definition MUST NOT remain free text.
- Move it into the Production / Work Order planning section.
- Load active definitions for the selected Product.
- It remains optional until production is required.
- WO creation MUST continue validating/using the selected Product Definition.

---

## 4. Current production requirement ignores available finished-goods stock

**Current files**
- `ErpWeb.Core/Sales/ISaDeliveryRequestService.cs`
- `ErpWeb.Core/Sales/SaDeliveryRequestService.cs`

**Current behavior**
- `UnplannedQty = RequestedQty - active WO allocation`.
- Existing finished-goods stock does not reduce production requirement.

**Required correction**
- Production requirement MUST be derived from open customer demand after stock reservation.

Required formulas:

```text
OpenDemandQty =
    MAX(RequestedQty - DeliveredQty, 0)

StockReservedQty =
    active DR stock reservations not yet transferred to a DO shipment

ProductionRequiredQty =
    MAX(OpenDemandQty - StockReservedQty - DeliveryAllocatedNotPostedQty, 0)

ProductionUnplannedQty =
    MAX(ProductionRequiredQty - ActiveWoAllocatedQty, 0)
```

`DeliveryAllocatedNotPostedQty` prevents a NEW DR-linked DO from making the same demand look available for new production.

---

## 5. Current DR completion is incorrectly derived from Work Order GoodQty

**Current file**
- `ErpWeb.Core/Sales/SaDeliveryRequestService.cs`

**Current behavior**
- `DerivedStatus` returns `COMPLETED` when WO `GoodQty >= RequestedQty`.
- `SearchAsync` treats the same condition as Completed.

**Why this is wrong**
- A DR can be fulfilled partly from existing stock.
- Production can finish while goods remain undelivered.
- Customer fulfilment completion must be based on Delivery Order delivery, not WO output.

**Required correction**
- Remove WO-GoodQty-based DR completion.
- Keep stored DR lifecycle status for document control.
- Add separate derived fulfilment status.

Required fulfilment statuses:

```text
OPEN
PARTIAL_READY
READY
PARTIAL_DELIVERED
COMPLETED
```

Required rule:

```text
COMPLETED when DeliveredQty >= RequestedQty
```

---

## 6. Current DR has no stock-reservation authority

**Current evidence**
- `IvBalLoc.StdQty` is physical stock authority.
- `IIvSpShipmentService` / `IvSpShipmentService` provide current DO/invoice shipment soft-reservation logic.
- `IvSpShipmentService.SumOtherNewSpReservationsAsync` subtracts NEW Sales-Out reservations from stock availability.
- No DR stock-reservation entity exists.

**Required correction**
- Add DR soft stock reservations.
- DR reservation MUST NOT decrement `IvBalLoc.StdQty`.
- Existing NEW shipment reservations and DR reservations MUST both reduce availability.
- DR reservation MUST be concurrency-safe.
- Shipment/DO posting remains physical stock movement authority.

---

## 7. Current DO lineage stops at SO; it cannot identify which split DR was fulfilled

**Current files**
- `ErpWeb.Model/Entities/Sales/SaDoDetail.cs`
- `ErpWeb.Model/Configurations/Sales/SaDoDetailConfiguration.cs`
- `ErpWeb.Core/Sales/SaDoService.cs`

**Current behavior**
- DO detail records `SoNo`, `CustRel`, `SoLine`.
- No Delivery Request source identity exists.

**Failure case**
- One SO line can be split between multiple DRs.
- SO reference alone cannot determine which DR a DO line fulfilled.

**Required correction**
- Add nullable `DeliveryRequestSourceId` to `SaDODetail`.
- One DO line may fulfil one DR source.
- DR-created DO lines MUST persist this ID.
- Non-DR DO lines remain null and behave exactly as today.

---

## 8. Direct SO→Invoice can bypass DR-controlled demand

**Current file**
- `ErpWeb.Core/Sales/SaInvoiceService.cs`

**Current behavior**
- Direct SO invoice is supported through the existing `SaSoLineReserve` path.

**Required correction**
- If an exact SO revision/line has any active non-cancelled DR source allocation, direct SO→Invoice for that line MUST be rejected.
- User must fulfil that controlled demand through DO.
- SO lines with no active DR allocation MUST retain current direct-invoice behavior.
- To invoice directly, user must first cancel/reduce/release the DR allocation.

This guard prevents DR delivered/open KPIs from becoming false.

---

## 9. Material shortage is already available in Production, but DR does not surface it

**Verified existing authority**
- `PrWorkOrderMaterial.RequiredQty`
- `PrWorkOrderMaterial.RequiredBaseQty`
- `PrWorkOrderMaterial.SupplySource`
- `PrWorkOrderMaterial.WarehouseCode`
- `ProductionMaterialAllocationService.AutoAllocateAsync`
- `ProductionMaterialAllocationResult.ShortBaseQty`

**Required correction**
- DR MUST read shortage from linked Work Orders and their material rows.
- DR MUST NOT calculate BOM demand independently.

---

## 10. Procurement trace currently has no exact Work Order material identity

**Current evidence**
- `PoPrDetail` already contains item, warehouse, SO reference and PR→PO lineage.
- `PoOrderDetail` already contains `PrNo` / `PrLineNo`.
- GRN is PO-line based.
- No `WorkOrderMaterialId` exists on PR detail.

**Required correction**
- Add nullable `WorkOrderMaterialId` to `PoPrDetail`.
- PR created from a DR/WO material shortage MUST persist the exact `PrWorkOrderMaterial.UID`.
- PR→PO→GRN trace is then derived through existing PR/PO/GRN references.
- Do not add redundant DR ID to PR/PO/GRN.

---

## 11. Current DR list is too limited for the required inquiry/KPI role

**Current files**
- `SaDeliveryRequestList.razor`
- `SaDeliveryRequestList.razor.cs`
- `ISaDeliveryRequestService.cs`
- `SaDeliveryRequestService.SearchAsync`

**Current filters**
- general search
- stored status
- product
- required-date range

**Required correction**
Add filters/search for:

- DR No.
- SO No.
- Customer
- Product
- Fulfilment Warehouse
- Project
- Required Date
- Priority
- lifecycle status
- fulfilment status
- blocker/risk
- WO No.

---

# Scope

Implement the following in the current `productionv2` branch:

1. meaningful DR header lookups and validations;
2. Product smart lookup and SO-demand filtering;
3. stock availability + DR soft reservation;
4. stock-aware production requirement;
5. DR→WO quantity correction;
6. derived material-shortage/procurement trace;
7. exact DR→DO source identity;
8. DR delivery/open/ready calculations;
9. direct-invoice DR guard;
10. DR inquiry and KPI projections;
11. tests for stock, concurrency, rollback and cross-module traceability.

---

# Non-Goals

The Code Agent MUST NOT:

- build a new MRP engine;
- auto-create PO;
- auto-select supplier;
- auto-create PR without explicit user action;
- alter inventory costing;
- alter FIFO valuation layers;
- alter month-end;
- alter accounting posting;
- alter Work Order BOM quantity formulas;
- alter Work Order scheduling/calendars;
- alter Production absorbed costing;
- alter existing FG valuation;
- convert DR reservation into an `IvBalLoc.StdQty` deduction;
- infer DR delivery using only SO No./SO line after the new lineage exists;
- backfill historical DO rows with guessed DR identities;
- implement SAP-style ATP/MRP complexity.

---

# Architecture / Authority Rules

## Demand authority

`SaDeliveryRequestSource` is the exact DR demand lineage.

The following MUST remain server-derived from current SO detail:

- ProductCode
- ProductDescription
- ProductionUom
- source standard demand
- customer
- requested source delivery date

`SaDeliveryRequest.RequestedQty` MUST equal the sum of active DR source allocations.

---

## Physical stock authority

`IvBalLoc.StdQty` remains physical stock authority.

DR reservations are **soft reservations** only.

Availability for DR allocation MUST account for:

```text
Usable physical stock
- NEW existing SP/DO/direct-invoice reservations
- active DR reservations from other DR sources
```

No DR method may directly reduce/increase `IvBalLoc.StdQty`.

---

## Production authority

`PrWorkOrderDemandAllocation` remains DR→WO quantity authority.

Work Order Product Definition, BOM, material, machine, labour, scheduling, output and costing remain Production-owned.

---

## Procurement authority

`PrWorkOrderMaterial` is material-demand authority.

`PoPrDetail.WorkOrderMaterialId` is trace lineage only.

PR/PO/GRN quantities/statuses remain Procurement/Inventory-owned.

---

## Customer-delivery authority

A DR-linked `SaDODetail.DeliveryRequestSourceId` identifies which DR source is being delivered.

`DeliveredQty` MUST be derived only from active DR-linked DO lines whose DO is physically delivered (`POSTED` or repository-equivalent retained `CLOSED` after force-close).

A DO rolled back to `NEW` MUST no longer count as delivered.

---

# Files to Change

## Existing — Sales / DR

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

## Existing — Inventory reservation integration

- `ErpWeb.Core/Inventory/IIvSpShipmentService.cs`
- `ErpWeb.Core/Inventory/IvSpShipmentService.cs`
- `ErpWeb.Core/Inventory/IvSpShipmentAllocator.cs` only if required by the implementation of exclusion/preference; do not otherwise change allocation ordering
- `ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs`

## Existing — Delivery Order

- `ErpWeb.Model/Entities/Sales/SaDoDetail.cs`
- `ErpWeb.Model/Configurations/Sales/SaDoDetailConfiguration.cs`
- `ErpWeb.Core/Sales/ISaDoService.cs`
- `ErpWeb.Core/Sales/SaDoService.cs`
- `ErpWeb.UI/Sales/Transactions/SaDo.razor`
- `ErpWeb.UI/Sales/Transactions/SaDo.razor.cs`
- `ErpWeb.Tests/Sales/Transaction/SaDoServiceTests.cs`

## Existing — Invoice guard

- `ErpWeb.Core/Sales/SaInvoiceService.cs`
- relevant existing invoice tests under `ErpWeb.Tests/Sales/Transaction/`

## Existing — Production / Work Order

- `ErpWeb.Core/Production/ProductionWorkOrderService.DeliveryRequest.cs`
- `ErpWeb.Core/Production/IProductionWorkOrderService.cs`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor`
- `ErpWeb.UI/Planning/WorkOrders/PrWorkOrderList.razor.cs`
- existing Work Order tests as required

## Existing — Procurement

- `ErpWeb.Model/Entities/Purchase/PoPrDetail.cs`
- matching PR EF configuration under `ErpWeb.Model/Configurations/Purchase/`
- `ErpWeb.Core/Purchase/IPoPrService.cs`
- `ErpWeb.Core/Purchase/PoPrService.cs`
- `ErpWeb.Tests/Procurement/Transaction/PoPrServiceTests.cs`

## Existing — model / DI

- `ErpWeb.Model/Data/AppDbContext.cs`
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`

## Proposed new files

- `ErpWeb.Model/Entities/Sales/SaDeliveryRequestStockReservation.cs`
- `ErpWeb.Model/Configurations/Sales/SaDeliveryRequestStockReservationConfiguration.cs`
- `ErpWeb.Core/Sales/ISaDeliveryRequestFulfilmentService.cs`
- `ErpWeb.Core/Sales/SaDeliveryRequestFulfilmentService.cs`
- `ErpWeb.Core/Inventory/InventorySoftReservationReader.cs`
- `ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestFulfilmentServiceTests.cs`
- `ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestSqlServerConcurrencyTests.cs`
- `scripts/alter-sales-delivery-request-fulfilment.sql`

If the existing purchase configuration file already maps `PoPrDetail`, modify that exact file; do not create a duplicate configuration class.

---

# Database Changes

## 1. New table: `SaDeliveryRequestStockReservation`

Create:

```text
UID                     bigint IDENTITY PK
DeliveryRequestID       bigint NOT NULL
DeliveryRequestSourceID bigint NOT NULL
CompanyCode             nvarchar(10) NOT NULL
BranchCode              nvarchar(10) NOT NULL
BalLocID                int NOT NULL
ReservedQty             decimal(18,4) NOT NULL
IsActive                bit NOT NULL DEFAULT 1
ReleasedDate            datetime2 NULL
ReleasedBy              nvarchar(20) NULL
ReleaseReason            nvarchar(200) NULL
CreatedDate             datetime2 NULL
CreatedBy               nvarchar(20) NULL
RowVersion              rowversion NOT NULL
```

Constraints:

- FK `DeliveryRequestID` → `SaDeliveryRequest.UID`
- FK `DeliveryRequestSourceID` → `SaDeliveryRequestSource.UID`
- FK `BalLocID` → `IvBalLoc.ID`
- CHECK `ReservedQty > 0`

Indexes:

- `(CompanyCode, BranchCode, DeliveryRequestID, IsActive)`
- `(CompanyCode, BranchCode, DeliveryRequestSourceID, IsActive)`
- `(CompanyCode, BranchCode, BalLocID, IsActive)`
- filtered unique active reservation per `(DeliveryRequestSourceID, BalLocID)` where `IsActive = 1`

Do not delete released reservation history.

---

## 2. Add `DeliveryRequestSourceID` to `SaDODetail`

```text
DeliveryRequestSourceID bigint NULL
```

Add:

- FK to `SaDeliveryRequestSource.UID`
- index `(CompanyCode, BranchCode, DeliveryRequestSourceID)`

No backfill.

Existing DO rows remain NULL.

---

## 3. Add `WorkOrderMaterialID` to `PoPrDetail`

```text
WorkOrderMaterialID bigint NULL
```

Add:

- FK to `PrWorkOrderMaterial.UID`
- index `(CompanyCode, BranchCode, WorkOrderMaterialID)`

No backfill.

Existing PR rows remain NULL.

---

## 4. Existing DR header columns

No new header columns are required for:

- Warehouse
- Project
- Priority
- Definition

Use existing columns.

---

## Deployment script rules

`alter-sales-delivery-request-fulfilment.sql` MUST:

- be SQL Server idempotent;
- check object/column/index/FK existence before creation;
- fail if required parent tables are missing;
- never guess historical DR→DO links;
- never rewrite `IvBalLoc`;
- preserve existing DR data.

Update `create-sales-delivery-request.sql` so a fresh database receives the final schema.

Deployment order:

1. current DR base schema exists;
2. apply fulfilment alter script;
3. deploy application code;
4. run smoke/integration tests.

---

# Exact Code Changes

# A. DR Header / Source UX

## `SaDeliveryRequestEntry.razor`

### Product

Replace read-only product input with existing:

```text
IvStockMasterPicker
```

Rules:

- Enabled only when `CanEdit` and `SourceRows.Count == 0`.
- On selection:
  - set ProductCode;
  - cache product description / standard UOM for display;
  - clear incompatible Product Definition;
  - filter eligible SO demand by ProductCode.
- On clear:
  - allowed only when no DR source exists.

After the first source row is added:

- Product is locked.
- Production UOM remains read-only.
- server source validation remains final authority.

### Fulfilment Warehouse

Replace free-text `WarehouseCode` with active warehouse combo.

Label MUST be:

```text
Fulfilment Warehouse
```

Rules:

- first added source defaults Warehouse from `SaDeliveryRequestEligibleSource.WarehouseCode`;
- all active source rows in one DR MUST have the same nonblank SO warehouse as the DR fulfilment warehouse;
- if a source warehouse differs, reject the source with a clear message and require a separate DR;
- do not silently cross-transfer warehouses.

### Project

Replace free text with `IvCodeComboBox`.

Load active `MsProject` rows.

Rules:

- `SaDeliveryRequestEligibleSource` MUST include SO header `ProjId`;
- first source defaults DR Project;
- all active source rows MUST have the same normalized Project value;
- mixed projects require separate DR;
- new/changed Project MUST be validated using `MsRefLookupRules.ValidateAsync`;
- unchanged historical orphan may remain according to existing shared rule.

### Priority

Replace text input with dropdown:

```text
NORMAL
HIGH
URGENT
```

Add `SaDeliveryRequestPriorities` constants.

Default = `NORMAL`.

Reject any other value server-side.

### Product Definition

Remove free-text Definition from the general header.

Show Product Definition only in the Production / Create Work Order section.

Use `IPrProductDefService.ListActiveDefinitionsAsync(ProductCode)` for users entering the WO creation path.

Do not require Definition when `ProductionRequiredQty == 0`.

---

# B. DR Lookups / Server Validation

## `ISaDeliveryRequestService.cs`

Add lookup/result contracts required by DR UI:

- active warehouse list;
- active project list;
- Product/warehouse/project on eligible SO source;
- derived fulfilment quantities/status;
- delivery trace;
- stock reservation trace;
- material shortage trace;
- procurement trace.

Extend `SaDeliveryRequestEligibleSource` with:

```text
ProjectCode
```

Warehouse already exists and MUST be used.

Extend list/detail models with at least:

```text
OpenDemandQty
StockReservedQty
DeliveryAllocatedQty
DeliveredQty
ProductionRequiredQty
WoAllocatedQty
ProductionUnplannedQty
ProducedQty
ReadyQty
FulfilmentStatus
BlockerCode
IsAtRisk
ForecastReadyDate
```

Do not persist these derived fields on the DR header.

---

## `SaDeliveryRequestService.PrepareSourceRowsAsync`

Preserve existing SO-lock/current-revision/product/UOM checks.

Add:

1. capture source Warehouse;
2. capture SO header Project;
3. require all sources to share Product + Production UOM + Fulfilment Warehouse + Project;
4. reject a source already over-allocated to another active DR;
5. continue requiring RequestedQty to equal active source allocation sum.

---

## `SaDeliveryRequestService.ApplyHeader`

Before persistence:

- validate warehouse exists and is active for company/branch;
- validate project using `MsRefLookupRules`;
- normalize Priority;
- Definition may be blank;
- do not trust client Product/UOM/RequestedQty.

---

# C. DR Soft Stock Reservation

## Proposed `ISaDeliveryRequestFulfilmentService`

Required operations:

```text
GetAvailabilityAsync(drId)
ReconcileStockReservationAsync(drId, expectedRowVersion)
ReleaseAllReservationsAsync(drId, reason)
ReleaseSourceReservationsAsync(sourceId, qty, reason)
GetFulfilmentFactsAsync(drIds)
GetMaterialShortagesAsync(drId)
```

Naming may follow current project conventions, but responsibilities MUST remain separated from physical stock posting.

---

## Proposed `InventorySoftReservationReader`

Extract/reuse the exact NEW Sales-Out reservation rule currently implemented by:

```text
IvSpShipmentService.SumOtherNewSpReservationsAsync
```

It MUST provide a single internal calculation for:

```text
NEW SP reservations by BalLoc
active DR reservations by BalLoc
```

Rules:

- preserve company/branch/location scoping;
- exclude deleted/tombstoned inventory batches according to existing shipment rule;
- DR allocation queries MUST exclude released DR reservations;
- DO shipment may exclude its own `DeliveryRequestSourceId` reservation while transferring it.

Do not create another physical stock balance.

---

## `SaDeliveryRequestFulfilmentService.ReconcileStockReservationAsync`

Only allow for:

```text
RELEASED
IN_PRODUCTION
```

Do not reserve stock for DRAFT or CANCELLED DRs.

Transaction sequence:

1. begin DB transaction;
2. lock DR header using SQL Server `UPDLOCK,HOLDLOCK`;
3. validate expected RowVersion when caller supplies one;
4. load/lock active DR sources in deterministic UID order;
5. load candidate `IvBalLoc` rows for:
   - exact ProductCode;
   - exact Fulfilment Warehouse;
   - current company/branch;
   - tenant location rules matching shipment logic;
   - ACTIVE item status;
   - positive stock;
   - valid lot/expiry;
6. lock BalLoc rows in `IvStockSliceKey` order using existing `IIvStockPostingRepository.LockBalLocByIdForTenantAsync`;
7. calculate usable quantity:
   - physical balance;
   - minus NEW SP reservation;
   - minus other active DR reservations;
8. retain valid current DR reservations where possible;
9. release stale/excess reservations;
10. allocate remaining demand by FIFO/FEFO-compatible candidate order;
11. distribute reservation to DR sources by:
    - RequestedDeliveryDate ascending;
    - SoNo;
    - CustRel;
    - SoLine;
12. save reservation/audit rows;
13. commit.

MUST NOT update `IvBalLoc.StdQty`.

---

## DR Release

`SaDeliveryRequestService.ReleaseAsync` MUST:

1. keep existing release validation;
2. require Fulfilment Warehouse;
3. normalize/validate Project/Priority;
4. release DR;
5. invoke stock reservation reconcile in the same logical operation or immediately after the persisted release using a service method that preserves concurrency;
6. return refreshed fulfilment facts.

If reservation cannot obtain all requested quantity:

- release still succeeds;
- shortage becomes ProductionRequired;
- do not fail just because stock is insufficient.

---

## DR Cancel

Cancel MUST:

1. preserve existing cancellation guards;
2. release all active DR stock reservations;
3. keep reservation history;
4. release/cancel active demand-source state according to current DR lifecycle;
5. MUST NOT cancel/rollback existing posted DO or posted production documents automatically.

If downstream posted delivery exists, cancellation MUST FAIL CLOSED unless current lifecycle rules already prohibit it.

---

# D. Stock-Aware Production Requirement / Work Order

## `ProductionWorkOrderService.DeliveryRequest.cs`

Current active-WO allocation authority remains `PrWorkOrderDemandAllocation`.

Before preview/create WO:

Load authoritative DR fulfilment facts.

Replace:

```text
unplanned = RequestedQty - WO allocation
```

with:

```text
productionRequired =
    MAX(OpenDemand
        - StockReserved
        - DeliveryAllocatedNotPosted,
        0)

productionUnplanned =
    MAX(productionRequired
        - ActiveWoAllocated,
        0)
```

`PlannedQty` MUST NOT exceed `ProductionUnplannedQty`.

Default WO quantity in DR UI MUST be `ProductionUnplannedQty`.

Existing:

- Work Order Product;
- Definition;
- snapshot;
- BOM;
- schedule;
- allocation locking;
- release;
- audit;

MUST remain unchanged except for the new maximum-quantity authority.

---

# E. DR Material Shortage

DR detail MUST derive shortage from linked active Work Orders.

For each linked Work Order material:

- read `PrWorkOrderMaterial`;
- use `ProductionMaterialAllocationService` / current stock candidate rules;
- show:
  - Component;
  - RequiredQty/BaseQty;
  - Warehouse;
  - SupplySource;
  - Available/Allocated;
  - ShortBaseQty;
  - PR/PO/GRN trace if present.

Do not persist copied shortage quantity on DR.

Blocker:

```text
MATERIAL_SHORTAGE
```

when a production-required DR has at least one required externally/purchased material with current shortage > tolerance and production cannot be completed from internal/WIP supply.

---

# F. Create PR from Material Shortage

## Schema

Add `PoPrDetail.WorkOrderMaterialId`.

## `IPoPrService` / `PoPrService`

Add an explicit "create from Work Order material" operation.

Request MUST identify:

```text
WorkOrderMaterialId
RequestedBaseQty
```

Server MUST:

1. re-read `PrWorkOrderMaterial`;
2. verify linked WO/DR tenant;
3. verify supply source is `PURCHASED` or `EXTERNAL_SUPPLY`;
4. recalculate current shortage using Production material allocation authority;
5. reject qty <= 0;
6. reject qty > current shortage;
7. map item/UOM/warehouse from the Work Order material;
8. default Project from DR Project;
9. use existing PR save/validation/numbering workflow;
10. persist `WorkOrderMaterialId`;
11. preserve existing PR→PO behavior.

Do not auto-create PO.

Do not bypass PR approvals/status rules.

---

# G. Exact DR → Delivery Order Lineage

## `SaDoDetail`

Add:

```text
DeliveryRequestSourceId
```

## `ISaDoService`

Extend line DTO/request with `DeliveryRequestSourceId`.

## `SaDoService.PrepareLinesAsync`

When `DeliveryRequestSourceId` is supplied:

1. load exact `SaDeliveryRequestSource`;
2. load parent DR;
3. require active source;
4. require DR status Released/InProduction;
5. require exact tenant;
6. require exact SO No / CustRel / SO Line;
7. require exact ProductCode;
8. require exact standard UOM;
9. require line Fulfilment Warehouse;
10. calculate DR source qty already tied to other non-deleted DO lines;
11. reject over-allocation.

Existing non-DR DO line behavior MUST remain unchanged.

---

# H. Create DO from DR

## `SaDeliveryRequestEntry`

For each source row show:

```text
Create Delivery Order
```

when:

- DR is released/in production;
- source has remaining undelivered/unallocated qty;
- user has DO Add permission.

Navigate with:

```text
/sales/delivery-orders/new?deliveryRequestSourceId={sourceUid}
```

## `SaDo.razor.cs`

Add query parameter:

```text
DeliveryRequestSourceId
```

For DR source startup:

1. load exact DR source;
2. set customer from source;
3. reuse the existing Sales Order picker/import flow;
4. use the same SO line pricing/tax/UOM rules;
5. create `SaDoLineVm.FromSalesOrder(...)`;
6. stamp `DeliveryRequestSourceId`;
7. default quantity to source remaining fulfilment qty, not the full original SO qty;
8. default Warehouse to DR Fulfilment Warehouse;
9. default Project to DR Project when compatible;
10. do not create a second pricing path.

The existing `AddFromSo()` path remains the reference behavior.

---

# I. Transfer DR Reservation to DO Shipment

## `IvSpRequiredLine`

Add optional:

```text
DeliveryRequestSourceId
```

DO maps it from `SaDoDetail`.

Direct invoice leaves it null.

## `IvSpShipmentService`

Availability MUST subtract:

```text
other NEW SP reservation
+ active DR stock reservations
```

For a DR-linked DO line:

- its own `DeliveryRequestSourceId` reservations are excluded from the "other DR reservation" amount so its reserved stock is usable.

## DO Add/Replace Shipment execution

Within the existing SaDo transaction:

1. lock DO;
2. validate DR source lineage;
3. create/replace SP shipment through existing `IIvSpShipmentService`;
4. read actual SP BalLoc/qty allocations;
5. release/transfer the corresponding DR source reservation quantity;
6. preserve DO SP reservation as the new soft reservation authority;
7. save;
8. commit.

Other documents MUST see the DO SP reservation and no longer see the transferred DR reservation.

No double reservation is allowed.

---

# J. DO Edit / Delete / Rollback

## NEW DO edit/delete

If a DR-linked line or shipment is removed:

1. release the DO SP reservation using existing shipment service;
2. release/remove the DO→DR source line identity as appropriate;
3. reconcile the DR stock reservation;
4. recompute DR open/ready facts.

## POSTED DO rollback

Current physical rollback remains authoritative.

After successful DO rollback to NEW:

- delivered quantity automatically decreases because status no longer counts as delivered;
- DO line remains linked to the DR source;
- NEW shipment reservation remains/returns according to existing DO rollback behavior;
- DR MUST NOT create duplicate stock reservation over the same NEW DO quantity.

## Force-close

A force-closed DO has already physically delivered its shipment.

DR `DeliveredQty` MUST continue to count the line.

Do not treat force-close write-off/invoice behavior as undelivered stock.

---

# K. Delivered / Ready / Fulfilment Facts

For each DR source:

```text
RequestedQty =
    SaDeliveryRequestSource.AllocatedProductionQty

DeliveryAllocatedQty =
    SUM(DR-linked DO line StdQty where DO is active NEW/POSTED/CLOSED)

DeliveredQty =
    SUM(DR-linked DO line StdQty where DO is POSTED/CLOSED)

OpenSourceQty =
    MAX(RequestedQty - DeliveredQty, 0)
```

For DR header:

```text
RequestedQty = SUM(source requested)
DeliveredQty = SUM(source delivered)
OpenDemandQty = MAX(RequestedQty - DeliveredQty, 0)

StockReservedQty =
    SUM(active DR stock reservation qty)

DeliveryAllocatedNotPostedQty =
    SUM(active NEW DR-linked DO std qty)

ReadyQty =
    MIN(OpenDemandQty,
        StockReservedQty + DeliveryAllocatedNotPostedQty)

ProductionRequiredQty =
    MAX(OpenDemandQty
        - StockReservedQty
        - DeliveryAllocatedNotPostedQty,
        0)
```

WO produced qty remains a production progress metric only.

It MUST NOT directly mark customer demand delivered.

---

# L. Fulfilment Status / Risk

Do not overload the stored DR lifecycle field.

Derived `FulfilmentStatus`:

```text
COMPLETED
    DeliveredQty >= RequestedQty

PARTIAL_DELIVERED
    DeliveredQty > 0 and DeliveredQty < RequestedQty

READY
    OpenDemandQty > 0 and ReadyQty >= OpenDemandQty

PARTIAL_READY
    ReadyQty > 0 and ReadyQty < OpenDemandQty

OPEN
    otherwise
```

Derived blockers:

```text
NONE
STOCK_SHORTAGE
WO_NOT_PLANNED
MATERIAL_SHORTAGE
PRODUCTION_LATE
PROCUREMENT_LATE
```

Blocker priority MUST be deterministic.

Recommended precedence:

```text
PROCUREMENT_LATE
MATERIAL_SHORTAGE
PRODUCTION_LATE
WO_NOT_PLANNED
STOCK_SHORTAGE
NONE
```

At-risk rule:

- DR not completed; and
- RequiredDate is approaching/passed; and
- current forecast ready date is after RequiredDate OR a blocking shortage has no on-time supply.

Do not invent advanced APS forecasting.

Use verified dates:

- WO `PlannedCompletionDateTime`;
- PO `EtaDate`;
- RequiredDate.

---

# M. Direct SO → Invoice Guard

## `SaInvoiceService.PrepareLinesAsync` / reserve validation path

For direct SO lines (`LinkDo == false`):

- if exact current SO revision/line has an active `SaDeliveryRequestSource` whose parent DR is not CANCELLED, reject the direct invoice line;
- message MUST identify the DR number when possible;
- `LinkDo` invoice remains allowed because delivery has already passed through DO.

Do not alter direct invoice behavior for SO lines with no active DR source.

---

# N. DR Inquiry / KPI

## `SaDeliveryRequestListQuery`

Add:

```text
SoNo
CustomerCode
WarehouseCode
ProjectCode
Priority
LifecycleStatus
FulfilmentStatus
WorkOrderNo
BlockerCode
DueMode
```

`DueMode` supports:

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

## List columns

At minimum:

```text
DR
Priority
Customer/Sources
Product
Warehouse
Requested
Stock Reserved
Production Required
WO Allocated
Produced
Ready
Delivered
Required Date
Fulfilment Status
Blocker
```

Use server-side projections; do not load the entire database into memory.

---

## KPI result

Add a compact KPI projection:

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
OnTimeReadinessPercent
```

Definitions:

```text
Open DR Qty =
    SUM(OpenDemandQty)

Overdue =
    RequiredDate < today AND FulfilmentStatus != COMPLETED

Ready For Delivery =
    OpenDemandQty > 0 AND ReadyQty >= OpenDemandQty

On-Time Readiness % =
    completed/ready DRs that first became fully ready on/before RequiredDate
```

If "first became fully ready" cannot be reconstructed for old records, do not fabricate history.

Add an audit event when a DR first transitions to fully Ready so future KPI is authoritative.

Historical rows before deployment may show `N/A` for On-Time Readiness historical timing.

---

# Transaction / Execution Order

## DR release + initial reservation

1. validate current DR RowVersion;
2. lock DR;
3. validate current SO source revisions;
4. validate Warehouse/Project/Priority;
5. set RELEASED;
6. allocate soft stock reservation with locked BalLoc rows;
7. write DR audit;
8. save;
9. commit.

---

## WO creation from DR

1. lock DR;
2. load fulfilment facts;
3. lock current DR→WO allocations;
4. compute ProductionUnplannedQty;
5. reject requested WO qty above remaining production requirement;
6. use existing Work Order snapshot creation;
7. write `PrWorkOrderDemandAllocation`;
8. preserve existing production audit;
9. commit.

---

## DO shipment from DR

1. lock DO;
2. validate exact DR source;
3. validate DO qty against DR source remaining;
4. lock shipment candidate BalLoc rows using existing inventory lock order;
5. include DR reservations in availability;
6. create/replace existing SP shipment;
7. transfer/release same DR soft-reservation quantity;
8. save/audit;
9. commit.

---

## PR creation from material shortage

1. re-read Work Order material;
2. re-calculate shortage;
3. validate requested qty;
4. create PR using existing PR validations;
5. stamp `WorkOrderMaterialId`;
6. commit using Procurement transaction rules.

---

# Concurrency / Locking

MUST preserve:

- DR RowVersion checks;
- existing deadlock retry in `SaDeliveryRequestService`;
- `IvStockSliceKey` deterministic stock lock order;
- `IIvStockPostingRepository.LockBalLocByIdForTenantAsync`;
- existing SO lock/current-revision validation;
- existing Work Order DR allocation lock rules;
- existing DO transaction boundaries.

New reservation race test:

Two DRs attempt to reserve the last 10 units concurrently.

Required invariant:

```text
SUM(active DR reservations)
+ SUM(NEW SP reservations)
<= physical usable stock
```

for each BalLoc within the transactionally protected snapshot.

SQL Server concurrency test is mandatory because SQLite cannot prove `UPDLOCK/HOLDLOCK` behavior.

---

# Rollback / Reversal

## DR draft delete

- keep current draft-only delete rule;
- no active reservation should exist for DRAFT;
- if stale reservation exists due a defect, delete MUST FAIL CLOSED and require cleanup/reconciliation.

## DR cancel

- release soft reservations;
- preserve reservation/audit history;
- do not reverse posted downstream transactions.

## WO rollback/cancel/reopen

- preserve existing Production guards;
- DR production requirement MUST derive again from active `PrWorkOrderDemandAllocation`;
- no manual DR quantity patch.

## DO rollback

- existing DO physical rollback remains unchanged;
- delivered projection follows DO status automatically;
- no manual delivered counter is stored on DR.

## PR/PO/GRN rollback

- preserve existing Procurement/Inventory rollback;
- DR procurement trace is derived;
- no duplicated procurement status on DR.

---

# Invariants

1. `DR RequestedQty == SUM(active DR source AllocatedProductionQty)`.
2. One DR contains one ProductCode.
3. One DR contains one ProductionUOM.
4. One DR contains one Fulfilment Warehouse.
5. One DR contains one normalized Project.
6. DR stock reservation never changes `IvBalLoc.StdQty`.
7. Active DR stock reservation references valid tenant stock.
8. Other DO/invoice shipment allocation cannot consume stock reserved by a DR.
9. A DR-linked DO line identifies exactly one `DeliveryRequestSourceId`.
10. A DR-linked DO line's SO identity MUST equal its DR source SO identity.
11. `DeliveredQty` is derived from DR-linked physically delivered DO lines.
12. WO GoodQty never directly increments DR DeliveredQty.
13. `ProductionRequiredQty` never goes below zero.
14. `ProductionUnplannedQty` never goes below zero.
15. Active WO allocation MUST NOT exceed production requirement.
16. PR created from shortage identifies exact `WorkOrderMaterialId`.
17. No guessed historical DR→DO linkage is created.
18. Cancelled DR reservations are inactive.
19. Direct SO invoice cannot bypass active DR-controlled demand.
20. Company/branch isolation applies to every new query and FK lookup.

---

# Tests

## `SaDeliveryRequestServiceTests`

Add/extend tests for:

- Product remains server-derived from SO source.
- Product selection mismatch is rejected.
- mixed Product rejected.
- mixed ProductionUOM rejected.
- mixed Warehouse rejected.
- mixed Project rejected.
- inactive/unknown Warehouse rejected.
- inactive/unknown Project rejected.
- invalid Priority rejected.
- `NORMAL` default.
- RequiredDate rule preserved.
- RequestedQty still equals source allocation sum.
- WO GoodQty no longer causes fulfilment completion.
- delivered DO qty causes fulfilment completion.

---

## `SaDeliveryRequestFulfilmentServiceTests`

Required tests:

1. full stock:
   - Demand 100;
   - stock 100;
   - reserve 100;
   - ProductionRequired 0.

2. partial stock:
   - Demand 100;
   - stock 60;
   - reserve 60;
   - ProductionRequired 40.

3. no stock:
   - Demand 100;
   - reserve 0;
   - ProductionRequired 100.

4. existing NEW SP reservation reduces availability.

5. another DR reservation reduces availability.

6. current DR reconcile does not double-count its own reservation.

7. lot/expiry candidate rule follows existing shipment eligibility.

8. cancellation releases reservations.

9. repeated reconcile is idempotent.

10. reservation history remains after release.

---

## SQL Server concurrency

New:

`ErpWeb.Tests/Sales/Transaction/SaDeliveryRequestSqlServerConcurrencyTests.cs`

Required scenario:

- physical stock 10;
- DR-A asks 10;
- DR-B asks 10;
- concurrent release/reconcile;
- total active reservations <= 10;
- no negative availability;
- no duplicate active reservation row.

---

## `SaDoServiceTests`

Add:

- DR-linked line must match SO identity.
- wrong product rejected.
- wrong UOM rejected.
- wrong warehouse rejected.
- DO qty above DR remaining rejected.
- NEW DR-linked DO counts allocated but not delivered.
- POSTED DR-linked DO counts delivered.
- rollback to NEW removes delivered amount.
- force-closed posted delivery remains delivered.
- non-DR DO behavior unchanged.

---

## Shipment reservation tests

Add/extend tests around `IvSpShipmentService`:

- unrelated DO cannot consume DR-reserved stock;
- DR-linked DO can consume its own DR reservation;
- transferring to SP does not double-reserve;
- releasing NEW DO shipment allows DR reservation reconciliation;
- direct invoice shipment sees DR reservation as unavailable.

---

## Invoice tests

- direct SO invoice blocked when SO line is controlled by active DR.
- direct SO invoice still allowed when no active DR exists.
- LinkDo invoice from DR-linked DO remains allowed.

---

## Work Order tests

- DR Demand 100 / stock 60 → max WO qty 40.
- creating WO 41 is rejected.
- creating WO 40 succeeds.
- later active WO allocation reduces ProductionUnplanned.
- cancelled/released allocation behavior remains current.

---

## Procurement tests

- Create PR from `PrWorkOrderMaterial` shortage succeeds.
- PR line stores exact `WorkOrderMaterialId`.
- qty above current shortage rejected.
- non-purchased/internal-route material rejected.
- DR Project defaults into PR.
- ordinary manually entered PR remains unchanged.

---

## Inquiry/KPI tests

- Open DR Qty correct.
- Due Today.
- Due This Week.
- Overdue.
- Ready.
- Partial Delivered.
- Completed.
- Material Shortage.
- At Risk with WO completion > DR required date.
- filter by SO.
- filter by Customer.
- filter by Warehouse.
- filter by Project.
- filter by Priority.
- filter by WO.

---

# Implementation Order

## STEP 1 — Protect current behavior with failing tests

Add focused tests for:

- stock-aware production requirement;
- DO DR-source lineage;
- direct invoice guard;
- field validation;
- fulfilment status.

Do not change UI first.

---

## STEP 2 — Apply schema/model foundation

Implement:

- `SaDeliveryRequestStockReservation`;
- `SaDODetail.DeliveryRequestSourceId`;
- `PoPrDetail.WorkOrderMaterialId`;
- EF configurations;
- `AppDbContext` DbSets;
- idempotent SQL alter script;
- fresh-install script update.

Build before continuing.

---

## STEP 3 — Implement shared soft-reservation reader

Extract current NEW-SP reservation query into the new internal reader.

Refactor `IvSpShipmentService` to use it with behavior-preserving tests.

Then add active DR reservation awareness.

Do not alter physical stock posting.

---

## STEP 4 — Implement DR fulfilment service

Implement:

- stock availability;
- reserve/reconcile;
- release;
- fulfilment facts;
- delivery facts;
- material shortage projection.

Pass focused tests before touching WO/DO.

---

## STEP 5 — Harden DR save/release lifecycle

Implement:

- Warehouse validation;
- Project validation;
- Priority validation;
- source Warehouse/Project consistency;
- stock reconcile on release/cancel.

---

## STEP 6 — Make Work Order stock-aware

Modify DR→WO preview/create quantity limit only.

Do not change Work Order costing/snapshot/scheduling core.

---

## STEP 7 — Implement exact DR→DO lineage

Add DTO/entity/service mapping.

Add `Create DO` from DR source.

Reuse existing `SaDo.AddFromSo()` behavior.

---

## STEP 8 — Integrate DR reservation with shipment

Update `IvSpRequiredLine`.

Block other documents from DR-reserved stock.

Transfer DR reservation to NEW SP reservation atomically.

Implement DO edit/delete/rollback regression tests.

---

## STEP 9 — Add direct SO invoice guard

Modify only direct SO line path.

Do not affect LinkDo invoices.

---

## STEP 10 — Add material shortage → PR

Add `WorkOrderMaterialId`.

Add explicit Create PR action.

Do not automate PO.

---

## STEP 11 — Upgrade DR Entry UI

Use:

- `IvStockMasterPicker`;
- active Warehouse combo;
- `IvCodeComboBox` for Project;
- controlled Priority;
- Product Definition dropdown in WO panel;
- fulfilment summary cards;
- material shortage table;
- trace panel;
- Create WO / Create PR / Create DO actions.

Follow existing Sales transaction UI conventions and the repository's `.razor` + code-behind + isolated/global CSS pattern.

---

## STEP 12 — Upgrade DR List / Inquiry / KPI

Add filters, derived columns and KPI cards.

Queries MUST remain server-side and tenant-scoped.

---

## STEP 13 — Full verification

Run:

- solution build;
- Sales transaction tests;
- Inventory shipment tests;
- Production Work Order tests;
- Procurement PR tests;
- new SQL Server concurrency tests;
- full regression suite.

---

# Regression Areas

The Code Agent MUST verify no regression in:

## Sales

- Sales Order save/revision;
- existing SO remaining qty;
- ordinary Delivery Order;
- DO shipment add/edit/post/rollback/force-close;
- direct invoice without DR;
- DO→Invoice;
- price/tax calculations.

## Inventory

- `IvBalLoc` quantity;
- SP shipment FIFO/FEFO;
- lot/expiry behavior;
- inventory posting;
- rollback;
- future-stock rules;
- costing.

## Production

- Work Order snapshot;
- Product Definition selection;
- BOM;
- material issue;
- output;
- FG receipt;
- costing;
- work-order rollback/reopen.

## Procurement

- PR manual entry;
- PR approvals/status;
- PR→PO;
- PO→GRN;
- purchase costing.

Existing unrelated behavior MUST remain unchanged.

---

# Do-Not Rules

DO NOT:

- make Product freely editable after SO sources exist;
- trust client Product/UOM/RequestedQty;
- keep Warehouse/Project/Priority as free text;
- use WO GoodQty as DR delivery completion;
- decrement physical stock when reserving for DR;
- duplicate FIFO/costing engines;
- duplicate BOM material-demand logic in Sales;
- guess DR identity from SO after implementation;
- backfill historical DO rows by inference;
- bypass DO shipment posting;
- bypass PR validation/approval;
- auto-create PO;
- change Work Order costing;
- change inventory costing;
- change month-end;
- weaken RowVersion checks;
- remove SQL Server stock locks;
- introduce cross-company/cross-branch allocation;
- add unrelated refactoring.

---

# Acceptance Criteria

- [ ] Repository branch/commit used by the agent matches `productionv2` baseline or the agent re-verifies drift before coding.
- [ ] Solution builds.
- [ ] Existing DR SO-line/current-revision trace remains correct.
- [ ] Product uses `IvStockMasterPicker` before source selection.
- [ ] Product/UOM lock after source allocation.
- [ ] Warehouse is an active lookup and has fulfilment meaning.
- [ ] Project is an active lookup and uses shared validation.
- [ ] Priority is `NORMAL/HIGH/URGENT` only.
- [ ] Product Definition is no longer free text.
- [ ] DR RequestedQty remains source-derived.
- [ ] Stock reservation never changes `IvBalLoc.StdQty`.
- [ ] NEW SP reservations and other DR reservations reduce DR availability.
- [ ] Other shipments cannot steal active DR-reserved stock.
- [ ] Full/partial/no-stock formulas are correct.
- [ ] WO default/max quantity uses stock-aware ProductionUnplannedQty.
- [ ] Existing Work Order BOM/scheduling/costing behavior is unchanged.
- [ ] DR material shortage comes from linked Work Order material authority.
- [ ] Create PR from shortage stores exact `WorkOrderMaterialId`.
- [ ] PR→PO→GRN remains existing authoritative flow.
- [ ] DO line stores exact `DeliveryRequestSourceId`.
- [ ] A split SO line across two DRs remains unambiguous.
- [ ] DO posting increases derived DR DeliveredQty.
- [ ] DO rollback decreases derived DeliveredQty.
- [ ] Force-closed delivered DO remains delivered.
- [ ] Direct SO invoice cannot bypass active DR-controlled demand.
- [ ] LinkDo invoice still works.
- [ ] DR completion is based on delivered qty, not WO GoodQty.
- [ ] DR list supports SO/Customer/Product/Warehouse/Project/Priority/Date/Status/WO filtering.
- [ ] Open/Due/Overdue/At-Risk/Ready/Material-Shortage KPI results are correct.
- [ ] SQL Server concurrency test proves no double reservation.
- [ ] Existing Sales/Inventory/Production/Procurement regression suites pass.
- [ ] No accounting/costing/month-end logic changed.

---

# Approval Status

**APPROVED FOR IMPLEMENTATION**
