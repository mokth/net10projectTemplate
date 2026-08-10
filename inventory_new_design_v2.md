# BizEERP Inventory Module — New Design

> **Standalone implementation guide.** Give this file to any AI or developer.
> Covers: entity model, services, month-end, lot traceability, SQL views.
> Target stack: .NET + EF Core + SQL Server + Blazor.

---

## 1. Architecture Decision: Remove Tenant, Use Company Only

**Before (Problematic):**
```
Tenant → Company → Branch    (3-level hierarchy, Tenant does nothing)
```

**After (Simplified):**
```
Company → Branch             (2-level hierarchy)
```

### Base Entity Inheritance Chain

```
BaseEntity
  ├─ Id, CreatedAt, CreatedBy, ModifiedAt, ModifiedBy, RowVersion
  │
  └─ CompanyEntity : BaseEntity
       ├─ CompanyId
       │
       ├─ BranchEntity : CompanyEntity
       │    └─ BranchId
       │
       └─ SoftDeletableEntity : CompanyEntity
            └─ IsDeleted, DeletedAt, DeletedBy
```

### Migration Steps

1. Delete `Tenant` entity and `TenantSetting` entity
2. Remove `TenantId` from `TenantEntity` base class, rename to `CompanyEntity`
3. All entities that inherited `TenantEntity` now inherit `CompanyEntity`
4. `IntegrationEvent` changes from `TenantEntity` to `CompanyEntity`
5. Remove `ITenantProvider`, replace with `ICompanyContextProvider`:
   - Returns `CompanyId` and `BranchId?`
   - Reads from JWT claims or HTTP headers
6. Update all global query filters in `BizEERPDbContext`:
   - `CompanyEntity` → filter by `CompanyId`
   - `BranchEntity` → filter by `CompanyId + BranchId`
   - `SoftDeletableEntity` → filter by `CompanyId + !IsDeleted`
7. Update `DocumentSequenceService` to use `companyId + branchId` only

---

## 2. Entity Model — Complete

### 2.1 Core Entities

```csharp
// Company.cs
public class Company : BaseEntity
{
    public string CompanyCode { get; set; }
    public string CompanyName { get; set; }
    public string BaseCurrencyCode { get; set; } = "MYR";
    public bool IsActive { get; set; } = true;
    public ICollection<Branch> Branches { get; set; } = new List<Branch>();
}

// Branch.cs
public class Branch : CompanyEntity
{
    public string BranchCode { get; set; }
    public string BranchName { get; set; }
    public bool IsActive { get; set; } = true;
    public Company Company { get; set; } = null!;
}

// DocumentSequence.cs
public class DocumentSequence : BranchEntity
{
    public string DocType { get; set; }
    public string? Prefix { get; set; }
    public long CurrentNumber { get; set; } = 1;
    public int NumberLength { get; set; } = 6;
}

// IntegrationEvent.cs
public class IntegrationEvent : CompanyEntity
{
    public string EventType { get; set; }
    public string EventPayload { get; set; }
    public IntegrationEventStatus Status { get; set; } = IntegrationEventStatus.PENDING;
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
    public string? ErrorMessage { get; set; }
    public DateTime? PublishedAt { get; set; }
}
```

### 2.2 Inventory Reference Data (all `CompanyEntity`)

```csharp
ItemCategory      — CategoryCode, CategoryName, IsActive
ItemSubCategory   — CategoryId, SubCategoryCode, SubCategoryName, IsActive
Brand             — BrandCode, BrandName, IsActive
Color             — ColorCode, ColorName, IsActive
Size              — SizeCode, SizeName, IsActive
Model             — ModelCode, ModelName, IsActive
UOM               — UOMCode, UOMName, DecimalPlace, IsActive
UOMConversion     — ItemId, FromUOMId, ToUOMId, ConversionRate
ReasonCode        — Code, Name, DocType?, IsActive
```

### 2.3 Inventory Master Data

```csharp
// Item.cs — inherits SoftDeletableEntity
public class Item : SoftDeletableEntity
{
    public long? BranchId { get; set; }           // null = company-wide
    public string ItemCode { get; set; }
    public string ItemDescription { get; set; }
    public long? CategoryId { get; set; }
    public long? SubCategoryId { get; set; }
    public long? BrandId { get; set; }
    public long BaseUOMId { get; set; }
    public bool IsStockItem { get; set; } = true;
    public bool IsBatchItem { get; set; }
    public bool IsSerialItem { get; set; }
    public CostingMethod CostingMethod { get; set; } = CostingMethod.MOVING_AVG;
    public decimal MinStockQty { get; set; }
    public decimal MaxStockQty { get; set; }
    public decimal ReorderQty { get; set; }
    public bool IsActive { get; set; } = true;
}

// ItemVariant.cs — inherits SoftDeletableEntity
public class ItemVariant : SoftDeletableEntity
{
    public long ItemId { get; set; }
    public string SKU { get; set; }
    public string? Barcode { get; set; }
    public long? ColorId { get; set; }
    public long? SizeId { get; set; }
    public long? ModelId { get; set; }
    public string? VariantDescription { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

// Warehouse.cs — inherits SoftDeletableEntity
public class Warehouse : SoftDeletableEntity
{
    public long BranchId { get; set; }            // REQUIRED
    public string WarehouseCode { get; set; }
    public string WarehouseName { get; set; }
    public bool IsActive { get; set; } = true;
}
```

**Design Note:** `Warehouse` inherits `SoftDeletableEntity` (CompanyEntity) but `BranchId` is required. The global query filter for `SoftDeletableEntity` is `CompanyId + !IsDeleted`. For correct branch isolation, use `BranchId` in your WHERE clauses explicitly, or create a `SoftDeletableBranchEntity` base class.

### 2.4 Transactional Tables

```csharp
// InventoryDocument.cs — inherits BranchEntity
public class InventoryDocument : BranchEntity
{
    public string DocNo { get; set; }
    public string DocType { get; set; }           // GRN, GI, MI, MR, ST, SA, SR, PR, SOH, ASM, DSM
    public DateTime DocDate { get; set; }
    public long WarehouseId { get; set; }
    public string? ReferenceNo { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.DRAFT;
    public string? Remarks { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedAt { get; set; }
    public ICollection<InventoryDocumentLine> Lines { get; set; } = new List<InventoryDocumentLine>();
}

// InventoryDocumentLine.cs — inherits BranchEntity
public class InventoryDocumentLine : BranchEntity
{
    public long DocumentId { get; set; }
    public int LineNo { get; set; }
    public long ItemVariantId { get; set; }
    public long UOMId { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }
    public long? LocationId { get; set; }
    public string? BatchNo { get; set; }
    public string? SerialNo { get; set; }
    public long? ReasonCodeId { get; set; }
    public long? LotId { get; set; }              // For OUT: which lot to consume
    public string? Remarks { get; set; }
    
    // ── NEW: Stock Transfer pairing ──
    public long? TransferSourceLineId { get; set; } // For ST IN lines: which OUT line feeds this IN
    public InventoryDocumentLine? TransferSourceLine { get; set; }
    public ICollection<InventoryDocumentLine> TransferTargetLines { get; set; } = new List<InventoryDocumentLine>();
}
```

### 2.5 Document Types (Enum)

```csharp
public enum DocumentType
{
    GRN,   // Goods Received Note (IN) — from supplier
    GI,    // Goods Issue (OUT) — to customer/production
    MI,    // Material Issue (OUT) — raw materials for production
    MR,    // Material Return (IN) — return materials to stock
    ST,    // Stock Transfer (IN/OUT) — between warehouses
    SA,    // Stock Adjustment (IN/OUT) — inventory count corrections
    SR,    // Sales Return (IN) — customer returns
    PR,    // Purchase Return (OUT) — return to supplier
    SOH,   // Stock On Hand Adjustment (IN/OUT)
    ASM,   // Assembly Receipt (IN) — finished good from production
    DSM    // Disassembly (OUT) — break finished good into components
}
```

---

## 3. THE History Table — StockLedger (Enhanced)

This is the **single source of truth** for ALL inventory movements. It is a **fully denormalized, append-only journal**.

**Important:** one document line may create **multiple StockLedger rows**. This happens when a line is split across multiple lots, FIFO cost layers, serial numbers, or other stock allocations. Each row represents one actual stock movement/allocation.

The StockLedger is **immutable**: posted rows are never updated or deleted. Corrections are represented by compensating/reversal movements.

```csharp
public class StockLedger : BranchEntity
{
    // ═══ Transaction Identity ═══
    public DateTime TransactionDate { get; set; }
    public string DocType { get; set; }
    public string DocNo { get; set; }
    
    // ═══ Document Header Context ═══
    public DateTime DocDate { get; set; }
    public string? ReferenceNo { get; set; }
    public string? DocRemarks { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedAt { get; set; }
    
    // ═══ Document Line Context ═══
    public int LineNo { get; set; }
    public long UOMId { get; set; }
    public string UOMCode { get; set; }              // Denormalized
    public decimal UnitQty { get; set; }             // Qty in line's UOM
    public decimal QtyInBase { get; set; }           // QtyIn converted to Base UOM
    public decimal QtyOutBase { get; set; }          // QtyOut converted to Base UOM
    public long? LocationId { get; set; }
    public string? LocationCode { get; set; }        // Denormalized
    public string? BatchNo { get; set; }
    public string? SerialNo { get; set; }
    public long? ReasonCodeId { get; set; }
    public string? ReasonCodeName { get; set; }      // Denormalized
    public string? LineRemarks { get; set; }
    
    // ═══ Stock Movement ═══
    public long WarehouseId { get; set; }
    public long ItemVariantId { get; set; }
    public decimal QtyIn { get; set; }
    public decimal QtyOut { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }
    public string CostingMethod { get; set; }
    
    // ═══ Lot Tracking ═══
    public long? LotId { get; set; }
    public string? LotNo { get; set; }               // Denormalized for fast queries
    
    // ═══ Denormalized Reporting Columns ═══
    public string SKU { get; set; }                  // From ItemVariant
    public string ItemDescription { get; set; }      // From Item
    public string WarehouseName { get; set; }        // From Warehouse
    
    // ═══ Polymorphic Reference ═══
    public string ReferenceType { get; set; }        // "DocumentLine", "SalesInvoice", etc.
    public long ReferenceId { get; set; }
    
    // Navigation
    public Warehouse Warehouse { get; set; } = null!;
    public ItemVariant ItemVariant { get; set; } = null!;
    public Lot? Lot { get; set; }
    public UOM UOM { get; set; } = null!;
}
```

### Why Denormalized?

- **Stock card report**: the ledger already contains the reporting dimensions needed for the common stock-card query.
- **Audit**: Every field the auditor needs is in ONE row.
- **Performance**: No JOINs to Document/DocumentLine/UOM/Item/Warehouse are required for common reporting.
- **Immutability**: Even if master data later changes, the historical row retains the SKU, description, warehouse, UOM and other values captured at posting time.
- **Correct lot/FIFO history**: a single document line can produce multiple immutable movement rows when stock is allocated from multiple lots/cost layers.

**Running balances are deliberately NOT stored as mutable fields in StockLedger.** Historical running quantity/value is calculated from immutable movements using SQL window functions or an as-of-date stock calculation. Current state is maintained in `StockBalance` and costing state in `ItemCost`/`CostLayer`.

---

## 4. Lot Genealogy — Complete Traceability

### 4.1 Lot Entity (Enhanced)

```csharp
public class Lot : BranchEntity
{
    public string LotNo { get; set; }
    public long ItemVariantId { get; set; }
    public long WarehouseId { get; set; }
    
    // ═══ Source ═══
    public long? SupplierId { get; set; }
    public string SourceDocType { get; set; }
    public string SourceDocNo { get; set; }
    public int SourceDocLineNo { get; set; }
    
    // ═══ Transfer Genealogy (1:1 or 1:N split) ═══
    public long? SourceLotId { get; set; }            // For ST: parent lot
    public Lot? SourceLot { get; set; }
    public ICollection<Lot> ChildLots { get; set; } = new List<Lot>();
    
    // ═══ Assembly/Disassembly Genealogy (N:1 or 1:N) ═══
    public long? OutputOfCompositionId { get; set; }   // This lot was PRODUCED by this composition
    public LotComposition? OutputOfComposition { get; set; }
    
    // ═══ Quantities ═══
    public decimal ReceivedQty { get; set; }
    public decimal CurrentQty { get; set; }
    public DateTime ReceivedDate { get; set; }
    public decimal ReceivedUnitCost { get; set; }
    public string CostCurrencyCode { get; set; } = "MYR";
    
    // ═══ Dates ═══
    public DateTime? ManufactureDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    
    // ═══ Status ═══
    public LotStatus Status { get; set; } = LotStatus.ACTIVE;
    public DateTime? ClosedAt { get; set; }
    public string? StatusChangedBy { get; set; }
    public DateTime? StatusChangedAt { get; set; }
    public string? Remarks { get; set; }
    
    // Navigation
    public ItemVariant ItemVariant { get; set; } = null!;
    public Warehouse Warehouse { get; set; } = null!;
    public Supplier? Supplier { get; set; }
}
```

### 4.2 LotComposition — Assembly/Disassembly Event (NEW)

```csharp
/// <summary>
/// Records ONE manufacturing event.
/// ASSEMBLY: Multiple input lots → One output lot
/// DISASSEMBLY: One input lot → Multiple output lots
/// </summary>
public class LotComposition : BranchEntity
{
    public string DocType { get; set; }              // 'MI' (assembly) or 'DSM' (disassembly)
    public string DocNo { get; set; }
    public DateTime DocDate { get; set; }
    
    /// <summary>The primary lot — OUTPUT for assembly, INPUT for disassembly</summary>
    public long PrimaryLotId { get; set; }
    public Lot PrimaryLot { get; set; } = null!;
    
    public long ItemVariantId { get; set; }          // The SKU being produced/disassembled
    public decimal Quantity { get; set; }
    public string OperationType { get; set; }        // "ASSEMBLY" or "DISASSEMBLY"
    
    public decimal TotalInputCost { get; set; }
    public decimal OutputUnitCost { get; set; }
    
    public string? Remarks { get; set; }
    
    public ItemVariant ItemVariant { get; set; } = null!;
    public ICollection<LotCompositionLine> Lines { get; set; } = new List<LotCompositionLine>();
}
```

### 4.3 LotCompositionLine — Inputs and Outputs

```csharp
public class LotCompositionLine : BranchEntity
{
    public long CompositionId { get; set; }
    public LotComposition Composition { get; set; } = null!;
    
    public int LineNo { get; set; }
    public long ItemVariantId { get; set; }          // Input SKU
    public long LotId { get; set; }                  // WHICH lot of the input was consumed
    public decimal ConsumedQty { get; set; }
    public long UOMId { get; set; }
    public decimal UnitCost { get; set; }            // Cost at time of consumption
    public decimal TotalCost { get; set; }
    
    public Lot Lot { get; set; } = null!;
    public ItemVariant ItemVariant { get; set; } = null!;
    public UOM UOM { get; set; } = null!;
}
```

### 4.4 Lot Status Enum

```csharp
public enum LotStatus
{
    ACTIVE,        // Available for allocation
    CLOSED,        // Fully consumed/sold
    QUARANTINED,   // Held for quality inspection
    RECALLED       // Supplier recall initiated
}
```

---

## 5. Stock Allocation and FIFO Cost Layers

### 5.1 StockMovementAllocation

A document line is a business instruction; actual inventory consumption may be split across multiple lots/cost layers. Keep that allocation explicit.

```csharp
public class StockMovementAllocation : BranchEntity
{
    public long StockLedgerId { get; set; }
    public long? SourceLotId { get; set; }       // Lot consumed
    public long? TargetLotId { get; set; }       // Lot created/received
    public long? CostLayerId { get; set; }

    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }

    public StockLedger StockLedger { get; set; } = null!;
    public Lot? SourceLot { get; set; }
    public Lot? TargetLot { get; set; }
    public CostLayer? CostLayer { get; set; }
}
```

Examples:

```text
GI Line 1 Qty 100
    ├── Lot A 40
    └── Lot B 60

=> two StockLedger rows and two allocations.
```

For a stock transfer:

```text
Warehouse A
    Lot A 30 ───────► Warehouse B Lot C 30
    Lot B 30 ───────► Warehouse B Lot D 30
```

The allocation records preserve exactly which source lot produced which destination lot.

### 5.2 CostLayer — FIFO Only

```csharp
public class CostLayer : BranchEntity
{
    public long ItemVariantId { get; set; }
    public long WarehouseId { get; set; }
    public long? LotId { get; set; }

    public long SourceDocumentId { get; set; }
    public long SourceDocumentLineId { get; set; }

    public DateTime ReceivedDate { get; set; }
    public decimal OriginalQty { get; set; }
    public decimal RemainingQty { get; set; }
    public decimal UnitCost { get; set; }
    public string CurrencyCode { get; set; } = "MYR";

    public ItemVariant ItemVariant { get; set; } = null!;
    public Warehouse Warehouse { get; set; } = null!;
    public Lot? Lot { get; set; }
    public ICollection<CostLayerAllocation> Allocations { get; set; } = new List<CostLayerAllocation>();
}
```

### 5.3 CostLayerAllocation

```csharp
public class CostLayerAllocation : BranchEntity
{
    public long CostLayerId { get; set; }
    public long StockLedgerId { get; set; }

    public decimal AllocatedQty { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }

    public CostLayer CostLayer { get; set; } = null!;
    public StockLedger StockLedger { get; set; } = null!;
}
```

Rules:

- Receipt into a FIFO item creates a CostLayer.
- OUT consumes one or more oldest open CostLayers.
- `RemainingQty` is reduced inside the same posting transaction.
- Reversal creates compensating movements and restores the affected layer quantities.
- CostLayer rows are not deleted as part of normal reversal; their remaining quantities are adjusted according to the reversal transaction.
- FIFO allocation must be concurrency-safe.

---

## 6. Live Tables (Updated by PostingEngine Only)

```csharp
// StockBalance.cs — inherits BranchEntity
public class StockBalance : BranchEntity
{
    public long WarehouseId { get; set; }
    public long ItemVariantId { get; set; }
    public decimal QtyOnHand { get; set; }
    public decimal ReservedQty { get; set; }
    public decimal AverageCost { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    // Computed (in DB or app): AvailableQty = QtyOnHand - ReservedQty
    // Computed: InventoryValue = QtyOnHand * AverageCost
}

// ItemCost.cs — inherits BranchEntity
public class ItemCost : BranchEntity
{
    public long ItemVariantId { get; set; }
    public long WarehouseId { get; set; }
    public CostingMethod CostingMethod { get; set; } = CostingMethod.MOVING_AVG;
    public decimal CurrentQty { get; set; }
    public decimal AverageCost { get; set; }
    public decimal LastCost { get; set; }
    public decimal StandardCost { get; set; }
    public DateTime? LastCostUpdated { get; set; }
}
```

---

## 7. Month-End Tables

```csharp
// InventoryPeriod.cs — inherits CompanyEntity
public class InventoryPeriod : CompanyEntity
{
    public int FiscalYear { get; set; }
    public int FiscalMonth { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsClosed { get; set; }
    public string? ClosedBy { get; set; }
    public DateTime? ClosedAt { get; set; }
}

// StockSnapshot.cs — inherits BranchEntity
public class StockSnapshot : BranchEntity
{
    public long PeriodId { get; set; }
    public long WarehouseId { get; set; }
    public long ItemVariantId { get; set; }
    public decimal ClosingQty { get; set; }
    public decimal ClosingCost { get; set; }
    public decimal ClosingValue { get; set; }          // ClosingQty * ClosingCost
    public DateTime SnapshotDate { get; set; }
    
    public InventoryPeriod Period { get; set; } = null!;
    public Warehouse Warehouse { get; set; } = null!;
    public ItemVariant ItemVariant { get; set; } = null!;
}
```

---

## 8. Other Support Tables

```csharp
// StockReservation.cs — inherits BranchEntity
public class StockReservation : BranchEntity
{
    public long ItemVariantId { get; set; }
    public long WarehouseId { get; set; }
    public string SourceDocType { get; set; }
    public long SourceDocId { get; set; }
    public long? SourceDocLineId { get; set; }
    public decimal ReservedQty { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public ReservationStatus Status { get; set; } = ReservationStatus.ACTIVE;
}

// Batch.cs — optional legacy/business concept.
 // If the business does not distinguish Batch from Lot, REMOVE Batch and use Lot
 // as the single traceability entity. Do not maintain two overlapping identifiers.
// Batch.cs — inherits BranchEntity
public class Batch : BranchEntity
{
    public long ItemVariantId { get; set; }
    public long WarehouseId { get; set; }
    public string BatchNo { get; set; }
    public DateTime? ManufactureDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public decimal CurrentQty { get; set; }
    public bool IsClosed { get; set; }
}

// SerialNumber.cs — branch-scoped inventory identity
public class SerialNumber : BranchEntity
{
    public long ItemVariantId { get; set; }
    public string SerialNo { get; set; }
    public SerialStatus Status { get; set; } = SerialStatus.IN_STOCK;
    public long? CurrentWarehouseId { get; set; }
    public long? CurrentLocationId { get; set; }
    public DateTime? ReceivedDate { get; set; }
    public DateTime? SoldDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
}

// WarehouseLocation.cs — inherits BranchEntity
public class WarehouseLocation : BranchEntity
{
    public long WarehouseId { get; set; }
    public string LocationCode { get; set; }
    public string? LocationName { get; set; }
    public bool IsActive { get; set; } = true;
}
```

---

## 9. Enums

```csharp
public enum CostingMethod      { MOVING_AVG, FIFO, STANDARD }
public enum DocumentStatus     { DRAFT, SUBMITTED, APPROVED, POSTED, CANCELLED, REVERSED }
public enum InventoryDirection { IN, OUT }
public enum LotStatus          { ACTIVE, CLOSED, QUARANTINED, RECALLED }
public enum ReservationStatus  { ACTIVE, FULFILLED, CANCELLED, EXPIRED }
public enum SerialStatus       { IN_STOCK, SOLD, RMA, SCRAPPED }
public enum IntegrationEventStatus { PENDING, PUBLISHED, FAILED }
```

---

## 10. Services

### 9.1 IPostingEngine Interface

```csharp
public interface IPostingEngine
{
    Task<DocumentDto> CreateDocumentAsync(CreateDocumentDto dto, CancellationToken ct = default);
    Task SubmitForApprovalAsync(long documentId, CancellationToken ct = default);
    Task ApproveAsync(long documentId, string approvedBy, CancellationToken ct = default);
    Task<PostingResultDto> PostAsync(long documentId, string postedBy, CancellationToken ct = default);
    Task CancelAsync(long documentId, CancellationToken ct = default);
    Task<DocumentDto> ReverseAsync(long documentId, string reversedBy, CancellationToken ct = default);
    Task<IReadOnlyList<StockCardEntryDto>> GetStockCardAsync(StockCardQueryDto query, CancellationToken ct = default);
    Task<StockBalanceDto?> GetStockBalanceAsync(long itemVariantId, long warehouseId, CancellationToken ct = default);
}
```

### 9.2 PostingEngine.PostAsync — Key Logic

When a document is posted, for EACH line the engine must:

1. **Validate**: Item is active, period is open, no back-dating, sufficient stock (for OUT)
2. **Calculate Costs**: Moving average or FIFO as configured
3. **Handle Lot**:
   - IN documents: create new Lot, set `SourceDocType/SourceDocNo`, set `SupplierId` if GRN
   - OUT documents: allocate from Lots (user-specified or FIFO auto), reduce `Lot.CurrentQty`
   - ST documents: IN lines create child Lot with `SourceLotId` pointing to the OUT line's lot
   - ASM documents: create output Lot with `OutputOfCompositionId` linking to LotComposition
4. **Insert StockLedger**: ONE fully denormalized row per line
5. **Update StockBalance**: QtyOnHand, AverageCost
6. **Update ItemCost**: AverageCost, LastCost if IN
7. **Update CostLayer**: For FIFO items
8. **Publish IntegrationEvent**: Outbox pattern, JSON payload with document info

**Atomicity**: ALL changes for a document happen in ONE `SaveChangesAsync()` call. If any line fails validation, the entire document is rolled back.

**Atomicity**: ALL changes for a document happen in ONE database transaction. `SaveChangesAsync()` is part of that transaction; it is not itself the complete concurrency strategy.

### 10.2.1 Posting Concurrency Rules

Posting must protect the affected stock state from concurrent postings.

Required sequence:

```text
Begin database transaction
    ↓
Lock/read affected StockBalance rows with update protection
    ↓
Validate available quantity
    ↓
Lock/read affected Lots / CostLayers
    ↓
Allocate stock and cost
    ↓
Insert immutable StockLedger rows
    ↓
Update StockBalance / ItemCost / CostLayer / Lot state
    ↓
Insert IntegrationEvent outbox row
    ↓
Commit
```

Two concurrent OUT postings must never both consume the same available quantity.

Use SQL Server transaction/isolation and appropriate row-update locking (`UPDLOCK`/`HOLDLOCK` or an equivalent safe strategy) where required. Optimistic `RowVersion` checks may be added as a secondary protection, but must not replace the inventory allocation lock strategy.

### 10.2.2 Reversal Rules

Reversal is implemented as a new compensating document.

```text
Original GRN +100
        ↓
Reversal document -100
```

Rules:

- Never update or delete the original StockLedger rows.
- Never delete the original document.
- Create new compensating StockLedger rows.
- Restore Lot/CostLayer quantities through the compensating transaction.
- Reverse StockBalance/ItemCost through normal posting logic.
- Record the relationship between the original and reversal documents.
- A document can only be reversed once unless a controlled correction workflow explicitly supports otherwise.
- Reversal must itself obey period-open rules. If the original period is closed, use the application's approved adjustment-period policy rather than silently posting into the closed period.



### 9.3 ClosePeriodService (NEW)

```csharp
public class ClosePeriodService
{
    private readonly BizEERPDbContext _db;
    
    public async Task ClosePeriodAsync(long periodId, string closedBy)
    {
        var period = await _db.InventoryPeriods.FindAsync(periodId)
            ?? throw new InvalidOperationException("Period not found.");
        
        if (period.IsClosed)
            throw new InvalidOperationException("Period is already closed.");
        
        // 1. Verify ALL documents in this period are POSTED or CANCELLED
        var unposted = await _db.InventoryDocuments
            .CountAsync(d => d.DocDate >= period.StartDate 
                          && d.DocDate <= period.EndDate
                          && d.Status != DocumentStatus.POSTED 
                          && d.Status != DocumentStatus.CANCELLED
                          && d.Status != DocumentStatus.REVERSED);
        
        if (unposted > 0)
            throw new InvalidOperationException(
                $"{unposted} document(s) in this period are not yet posted. Post or cancel them first.");
        
        // 2. Snapshot all current stock balances
        var balances = await _db.StockBalances
            .Where(sb => sb.QtyOnHand != 0 || sb.ReservedQty != 0)
            .ToListAsync();
        
        foreach (var bal in balances)
        {
            _db.StockSnapshots.Add(new StockSnapshot
            {
                PeriodId = periodId,
                WarehouseId = bal.WarehouseId,
                ItemVariantId = bal.ItemVariantId,
                ClosingQty = bal.QtyOnHand,
                ClosingCost = bal.AverageCost,
                ClosingValue = bal.QtyOnHand * bal.AverageCost,
                SnapshotDate = period.EndDate
            });
        }
        
        // 3. Close the period
        period.IsClosed = true;
        period.ClosedBy = closedBy;
        period.ClosedAt = DateTime.UtcNow;
        
        await _db.SaveChangesAsync();
    }
}
```

### 9.4 StockIntegrityService

Already designed — performs:
- `FindNegativeRunningQtyAsync()` — detects corrupted stock cards
- `FindBackDatedTransactionsAsync()` — detects transactions posted before existing ones
- `RecalculateRunningValuesAsync()` — rebuilds running totals from scratch

### 9.5 DocumentSequenceService

Generates document numbers using a **database-level atomic increment**. Do not use read-then-increment application logic under concurrency. Uses a per-branch sequence row rather than a global semaphore:

```csharp
public async Task<string> GenerateDocNoAsync(string docType, 
    long companyId, long branchId, CancellationToken ct)
{
    var seq = await _db.DocumentSequences
        .Where(ds => ds.CompanyId == companyId 
                  && ds.BranchId == branchId 
                  && ds.DocType == docType)
        .FirstOrDefaultAsync(ct);
    
    if (seq == null)
    {
        seq = new DocumentSequence { /* auto-create with prefix */ };
        _db.DocumentSequences.Add(seq);
    }
    else
    {
        seq.CurrentNumber++;
    }
    
    await _db.SaveChangesAsync(ct);
    return $"{seq.Prefix}{seq.CurrentNumber:D6}";
}
```

**Mandatory concurrency rule:** use a database atomic operation such as `UPDATE ... OUTPUT INSERTED.CurrentNumber`, or a SQL Server `SEQUENCE`. The application must never rely on `seq.CurrentNumber++` followed by `SaveChangesAsync()` as the uniqueness mechanism.

Example:

```sql
UPDATE DocumentSequence
SET CurrentNumber = CurrentNumber + 1
OUTPUT INSERTED.CurrentNumber
WHERE CompanyId = @CompanyId
  AND BranchId = @BranchId
  AND DocType = @DocType;
```

---

## 11. Data Flow Summary

```
User creates GRN-000042 (DRAFT)
  │
  ├─ SubmitForApproval → SUBMITTED (validates period open)
  ├─ Approve → APPROVED
  ├─ Post → POSTED
  │    │
  │    ├─ StockLedger: INSERT 1 fully denormalized row per document line
  │    ├─ StockBalance: UPSERT QtyOnHand, AverageCost per warehouse/SKU
  │    ├─ ItemCost: UPSERT AverageCost, LastCost, InventoryValue
  │    ├─ CostLayer: INSERT for FIFO items (receipt creates layer)
  │    ├─ Lot: INSERT new Lot (for IN) or UPDATE Lot.CurrentQty (for OUT)
  │    ├─ LotComposition: INSERT for assembly/disassembly
  │    └─ IntegrationEvent: INSERT outbox row
  │
  └─ InventoryDocument stays as working record (NOT deleted)

Month-End Close:
  ├─ Verify all period documents are POSTED/CANCELLED
  ├─ StockSnapshot: INSERT frozen balances for every SKU/Warehouse
  └─ InventoryPeriod.IsClosed = true (blocks future posting to this period)

Reporting:
  ├─ Current stock: query StockBalance
  ├─ Stock card: query StockLedger (denormalized, no JOINs needed)
  ├─ Month-end valuation: query StockSnapshot
  ├─ Lot audit trail: query vw_LotFullTrace or execute sp_LotAuditTrail
  └─ Lot genealogy: query vw_LotRecursiveUp (CTE)
```

---

## 12. SQL Views for Lot Traceability

### 11.1 vw_LotLineage — Direct Parent-Child Relationships

```sql
CREATE VIEW vw_LotLineage AS
SELECT 
    'TRANSFER' AS RelationshipType,
    parent.LotNo AS ParentLotNo,  parent.Id AS ParentLotId,
    child.LotNo  AS ChildLotNo,   child.Id  AS ChildLotId,
    child.ItemVariantId, child.CurrentQty AS ChildQty,
    child.ReceivedDate AS EventDate,
    NULL AS ConsumedQty, NULL AS UnitCost
FROM Lot child
JOIN Lot parent ON parent.Id = child.SourceLotId

UNION ALL

SELECT 
    'ASSEMBLY_INPUT',
    input_lot.LotNo, input_lot.Id,
    output_lot.LotNo, output_lot.Id,
    lc.ItemVariantId, lcl.ConsumedQty,
    lc.DocDate, lcl.ConsumedQty, lcl.UnitCost
FROM LotComposition lc
JOIN LotCompositionLine lcl ON lcl.CompositionId = lc.Id
JOIN Lot input_lot  ON input_lot.Id  = lcl.LotId
JOIN Lot output_lot ON output_lot.Id = lc.PrimaryLotId
WHERE lc.OperationType = 'ASSEMBLY'

UNION ALL

SELECT 
    'DISASSEMBLY_OUTPUT',
    input_lot.LotNo, input_lot.Id,
    output_lot.LotNo, output_lot.Id,
    lc.ItemVariantId, lc.Quantity,
    lc.DocDate, lcl.ConsumedQty, lcl.UnitCost
FROM LotComposition lc
JOIN LotCompositionLine lcl ON lcl.CompositionId = lc.Id
JOIN Lot input_lot  ON input_lot.Id  = lc.PrimaryLotId
JOIN Lot output_lot ON output_lot.Id = lcl.LotId
WHERE lc.OperationType = 'DISASSEMBLY';
```

### 11.2 vw_LotFullTrace — Complete Movement Timeline

```sql
CREATE VIEW vw_LotFullTrace AS
SELECT 
    sl.TransactionDate, sl.DocType, sl.DocNo, sl.LineNo,
    sl.SKU, sl.ItemDescription, sl.WarehouseName,
    sl.LotNo, sl.QtyInBase AS QtyIn, sl.QtyOutBase AS QtyOut,
    SUM(sl.QtyInBase - sl.QtyOutBase) OVER (PARTITION BY sl.ItemVariantId, sl.WarehouseId ORDER BY sl.TransactionDate, sl.Id ROWS UNBOUNDED PRECEDING) AS RunningQty, sl.UnitCost, sl.Amount,
    sl.BatchNo, sl.SerialNo, sl.ReasonCodeName,
    sl.PostedBy, sl.PostedAt, sl.ReferenceNo, sl.LocationCode,
    lot.SourceLotId, parent.LotNo AS SourceLotNo,
    lot.Status AS LotStatus, lot.SupplierId
FROM StockLedger sl
JOIN Lot lot ON lot.Id = sl.LotId
LEFT JOIN Lot parent ON parent.Id = lot.SourceLotId;
```

### 11.3 vw_StockCard — Standard Stock Card Report

```sql
CREATE VIEW vw_StockCard AS
SELECT
    sl.ItemVariantId, sl.SKU, sl.ItemDescription,
    sl.WarehouseId, sl.WarehouseName,
    sl.TransactionDate, sl.DocType, sl.DocNo, sl.LineNo,
    sl.LotNo,
    sl.QtyInBase AS QtyIn, sl.QtyOutBase AS QtyOut,
    -- Historical running balance is derived from immutable movements.
    SUM(sl.QtyInBase - sl.QtyOutBase) OVER (
        PARTITION BY sl.ItemVariantId, sl.WarehouseId 
        ORDER BY sl.TransactionDate, sl.Id
        ROWS UNBOUNDED PRECEDING
    ) AS RunningQty,
    sl.UnitCost, sl.Amount,
    sl.BatchNo, sl.SerialNo, sl.ReasonCodeName,
    sl.ReferenceNo, sl.LineRemarks
FROM StockLedger sl;
```

### 11.4 vw_LotRecursiveUp — Full Genealogy (CTE)

```sql
CREATE VIEW vw_LotRecursiveUp AS
WITH LotTree AS (
    SELECT 
        l.Id AS LotId, l.LotNo, l.ItemVariantId,
        l.SourceLotId, CAST(l.LotNo AS VARCHAR(MAX)) AS LineagePath, 0 AS Depth
    FROM Lot l
    
    UNION ALL
    
    SELECT 
        parent.Id, parent.LotNo, parent.ItemVariantId,
        parent.SourceLotId,
        CAST(tree.LineagePath + ' ← ' + parent.LotNo AS VARCHAR(MAX)),
        tree.Depth + 1
    FROM LotTree tree
    JOIN Lot parent ON parent.Id = tree.SourceLotId
    WHERE tree.Depth < 20
    
    UNION ALL
    
    SELECT 
        input_lot.Id, input_lot.LotNo, input_lot.ItemVariantId,
        input_lot.SourceLotId,
        CAST(tree.LineagePath + ' ← [ASM] ' + input_lot.LotNo AS VARCHAR(MAX)),
        tree.Depth + 1
    FROM LotTree tree
    JOIN LotComposition lc ON lc.PrimaryLotId = tree.LotId AND lc.OperationType = 'ASSEMBLY'
    JOIN LotCompositionLine lcl ON lcl.CompositionId = lc.Id
    JOIN Lot input_lot ON input_lot.Id = lcl.LotId
    WHERE tree.Depth < 20
)
SELECT * FROM LotTree;
```

### 11.5 sp_LotAuditTrail — Full Audit Stored Procedure

```sql
CREATE PROCEDURE sp_LotAuditTrail @LotNo VARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;

    -- 1. Lot header with source info
    SELECT l.LotNo, iv.SKU, i.ItemDescription,
           l.ReceivedDate, l.ReceivedQty, l.CurrentQty, l.ReceivedUnitCost, l.Status,
           l.SourceDocType, l.SourceDocNo,
           s.SupplierCode, s.SupplierName,
           parent.LotNo AS ParentLotNo
    FROM Lot l
    JOIN ItemVariant iv ON iv.Id = l.ItemVariantId
    JOIN Item i ON i.Id = iv.ItemId
    LEFT JOIN Supplier s ON s.Id = l.SupplierId
    LEFT JOIN Lot parent ON parent.Id = l.SourceLotId
    WHERE l.LotNo = @LotNo;

    -- 2. All stock movements
    SELECT sl.TransactionDate, sl.DocType, sl.DocNo, sl.LineNo,
           sl.QtyInBase AS QtyIn, sl.QtyOutBase AS QtyOut, sl.RunningQty,
           sl.UnitCost, sl.Amount, sl.RunningValue,
           sl.WarehouseName, sl.LocationCode,
           sl.PostedBy, sl.PostedAt
    FROM StockLedger sl
    WHERE sl.LotNo = @LotNo
    ORDER BY sl.TransactionDate, sl.Id;

    -- 3. If assembled: show input materials
    SELECT lcl.LineNo, iv.SKU AS InputSKU, i.ItemDescription AS InputDesc,
           input_lot.LotNo AS InputLotNo,
           lcl.ConsumedQty, lcl.UnitCost, lcl.TotalCost
    FROM LotComposition lc
    JOIN LotCompositionLine lcl ON lcl.CompositionId = lc.Id
    JOIN Lot input_lot ON input_lot.Id = lcl.LotId
    JOIN ItemVariant iv ON iv.Id = lcl.ItemVariantId
    JOIN Item i ON i.Id = iv.ItemId
    WHERE lc.PrimaryLotId = (SELECT Id FROM Lot WHERE LotNo = @LotNo)
      AND lc.OperationType = 'ASSEMBLY'
    ORDER BY lcl.LineNo;

    -- 4. If used in assembly: show what was produced
    SELECT lc.DocNo, lc.DocDate, iv.SKU AS OutputSKU, i.ItemDescription AS OutputDesc,
           output_lot.LotNo AS OutputLotNo, lc.Quantity, lc.OutputUnitCost
    FROM LotCompositionLine lcl
    JOIN LotComposition lc ON lc.Id = lcl.CompositionId
    JOIN Lot output_lot ON output_lot.Id = lc.PrimaryLotId
    JOIN ItemVariant iv ON iv.Id = lc.ItemVariantId
    JOIN Item i ON i.Id = iv.ItemId
    WHERE lcl.LotId = (SELECT Id FROM Lot WHERE LotNo = @LotNo)
      AND lc.OperationType = 'ASSEMBLY';
END
```

---

## 13. DbContext Global Query Filters

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    // CompanyEntity: filter by CompanyId
    // BranchEntity: filter by CompanyId + BranchId
    // SoftDeletableEntity: filter by CompanyId + !IsDeleted
    
    // Apply using reflection to avoid 40+ manual registrations:
    foreach (var entityType in modelBuilder.Model.GetEntityTypes())
    {
        if (typeof(BranchEntity).IsAssignableFrom(entityType.ClrType))
        {
            modelBuilder.Entity(entityType.ClrType)
                .HasQueryFilter(e => EF.Property<long>(e, "CompanyId") == _companyContext.CompanyId
                                  && EF.Property<long>(e, "BranchId") == _companyContext.BranchId);
        }
        else if (typeof(SoftDeletableEntity).IsAssignableFrom(entityType.ClrType))
        {
            modelBuilder.Entity(entityType.ClrType)
                .HasQueryFilter(e => EF.Property<long>(e, "CompanyId") == _companyContext.CompanyId
                                  && !EF.Property<bool>(e, "IsDeleted"));
        }
        else if (typeof(CompanyEntity).IsAssignableFrom(entityType.ClrType))
        {
            modelBuilder.Entity(entityType.ClrType)
                .HasQueryFilter(e => EF.Property<long>(e, "CompanyId") == _companyContext.CompanyId);
        }
    }
}
```

---

## 14. Database Constraints, Indexes and Integrity Rules

### 14.1 Required Unique Constraints

```text
Company
    UNIQUE (CompanyCode)

Branch
    UNIQUE (CompanyId, BranchCode)

Item
    UNIQUE (CompanyId, ItemCode)

ItemVariant
    UNIQUE (CompanyId, SKU)

Warehouse
    UNIQUE (CompanyId, BranchId, WarehouseCode)

StockBalance
    UNIQUE (CompanyId, BranchId, WarehouseId, ItemVariantId)

Lot
    UNIQUE (CompanyId, BranchId, LotNo)

InventoryDocument
    UNIQUE (CompanyId, BranchId, DocType, DocNo)

InventoryDocumentLine
    UNIQUE (DocumentId, LineNo)

DocumentSequence
    UNIQUE (CompanyId, BranchId, DocType)
```

### 14.2 Required StockLedger Indexes

At minimum:

```text
(CompanyId, BranchId, ItemVariantId, WarehouseId, TransactionDate, Id)
(CompanyId, BranchId, LotId, TransactionDate, Id)
(CompanyId, BranchId, DocType, DocNo)
(CompanyId, BranchId, ReferenceType, ReferenceId)
(CompanyId, BranchId, WarehouseId, TransactionDate)
```

Index design should be validated against real reporting/query plans after production-like data volume is available.

### 14.3 Ledger Ordering

Inventory history and valuation require deterministic ordering.

Use:

```text
TransactionDate
    → PostedAt
        → Id
```

or introduce an explicit `LedgerSequence` generated at posting time.

The ordering rule must be consistent across:

- stock-card running balance
- moving-average costing
- FIFO allocation
- backdated transaction detection
- reversal
- audit reports

### 14.4 Backdated Transaction Policy

The system must explicitly define whether backdated posting is allowed.

Recommended default:

- Posting into a closed period: **blocked**.
- Posting into an open period after later transactions exist: **blocked by default**.
- If backdated posting is required, execute a controlled recalculation/revaluation process for all affected dates and costing layers.
- Never silently rewrite immutable StockLedger history.


## 14. Implementation Order (Recommended)

**Phase 1: Foundation (depends on nothing)**
1. Remove Tenant, update all base entity classes
2. Update all existing entities to new base classes
3. Update DbContext, global query filters, `ICompanyContextProvider`
4. Update `DocumentSequenceService`

**Phase 2: Core Inventory (depends on Phase 1)**
5. Add `TransferSourceLineId` to `InventoryDocumentLine`
6. Add `SourceLotId` to `Lot`
7. Add `StockMovementAllocation`, `CostLayer`, and `CostLayerAllocation`
8. Enhance `StockLedger` with immutable denormalized movement columns
9. Allow multiple StockLedger rows per document line when lot/FIFO/serial allocation requires splitting
10. Update `PostingEngine.PostLineAsync` to write the correct movement/allocation rows

**Phase 3: Lot Genealogy (depends on Phase 2)**
11. Create `LotComposition` and direction-aware `LotCompositionLine` entities
12. Add `OutputOfCompositionId` to `Lot`
13. Update `PostingEngine` to handle ASM/DSM document types with explicit INPUT/OUTPUT composition lines

**Phase 4: Month-End (depends on Phase 2)**
14. Create `ClosePeriodService`
15. Implement true as-of-period stock valuation
16. Add controller endpoint for closing a period
17. Add `ValidatePeriodOpenAsync` check in PostingEngine

**Phase 5: Reporting (depends on Phase 3, 4)**
18. Create SQL views: `vw_LotLineage`, `vw_LotFullTrace`, `vw_StockCard`, `vw_LotRecursiveUp`
19. Create stored procedure: `sp_LotAuditTrail`
20. Add `StockIntegrityService` checks/rebuild tools; do not depend on mutable RunningQty fields

**Phase 6: Stock Transfer Full Support (depends on Phase 3)**
21. Implement ST-specific posting logic using `StockMovementAllocation` (one transfer may consume multiple source lots and create multiple destination lots)
22. Implement ST-specific reversal using compensating movements; never delete historical ledger rows

---

## 15. Final Architecture Rules

These rules are mandatory for implementation:

1. **InventoryDocument is the business transaction.**
2. **StockLedger is the immutable movement journal.**
3. **One document line may create many StockLedger rows.**
4. **StockMovementAllocation explains how a movement was split across lots/cost layers.**
5. **StockBalance is current state, not historical truth.**
6. **ItemCost is current costing state.**
7. **CostLayer/CostLayerAllocation provide FIFO history and allocation.**
8. **Lot is the traceability identity; Batch must only remain if the business has a distinct Batch concept.**
9. **Assembly/Disassembly genealogy uses explicit INPUT/OUTPUT composition lines.**
10. **Transfers preserve source-to-target lot genealogy, including split transfers.**
11. **StockLedger rows are never updated or deleted after posting.**
12. **Reversal is a compensating transaction.**
13. **Month-end snapshots are calculated as-of the period end, not copied from today's live balance.**
14. **Document numbering uses database-level atomic concurrency control.**
15. **Posting uses a database transaction plus concurrency protection for StockBalance/Lot/CostLayer.**
16. **Closed periods cannot receive ordinary postings.**
17. **Historical running balances are calculated from immutable ledger movements.**
18. **Company/Branch isolation is enforced centrally through query filters and database constraints.**
19. **All critical CompanyId/BranchId combinations have supporting indexes and unique constraints.**
20. **Outbox IntegrationEvent is committed in the same transaction as the inventory posting.**

### Recommended Core Data Flow

```text
InventoryDocument
      │
      ▼
PostingEngine
      │
      ├── DocumentValidator
      ├── StockMovementService
      ├── LotAllocationService
      ├── CostingService
      │      ├── Moving Average
      │      ├── FIFO
      │      └── Standard Cost
      ├── StockBalanceService
      ├── StockLedgerService
      └── OutboxService
              │
              ▼
       Database Transaction
              │
       ┌──────┼────────┬──────────┐
       ▼      ▼        ▼          ▼
StockLedger Lot/Alloc CostLayer StockBalance
       │
       ▼
Reporting / Audit / Month-End
```

## 15. What This Design Covers

| Capability | How |
|---|---|
| Multi-branch inventory | `BranchEntity` with global query filters |
| All stock movements | 11 document types (GRN, GI, MI, MR, ST, SA, SR, PR, SOH, ASM, DSM) |
| Document workflow | DRAFT → SUBMITTED → APPROVED → POSTED → (CANCELLED / REVERSED) |
| Complete history journal | `StockLedger` — denormalized, immutable movement journal; one document line may produce multiple rows |
| Lot tracking (receipt) | GRN/MR creates Lot with supplier, cost, dates |
| Lot tracking (transfer) | ST creates child Lot with `SourceLotId` → parent |
| Lot tracking (assembly) | MI + ASM creates output Lot with `LotComposition` linking input lots |
| Lot tracking (disassembly) | DSM + MR restores input Lot quantities via `LotComposition` |
| Full genealogy | Recursive CTE view walks entire lot family tree |
| Moving average costing | `ItemCost.AverageCost` plus immutable StockLedger movements; historical values are calculated as-of date |
| FIFO costing | `CostLayer` + `CostLayerAllocation`, oldest-first allocation with immutable movement history |
| Month-end closing | `ClosePeriodService` calculates as-of-period balances/valuation → `StockSnapshot` → period locked |
| Month-end reporting | `StockSnapshot` provides frozen point-in-time valuation |
| Stock card | `vw_StockCard` — one query, no JOINs |
| Audit trail | `sp_LotAuditTrail` — 4 result sets per lot |
| Lot traceability | `vw_LotLineage`, `vw_LotRecursiveUp` |
| Reversal | `ReverseAsync` creates a compensating document/movements; original history remains immutable |
| Cross-module events | `IntegrationEvent` outbox pattern |
