# BizEERP Inventory Module — Production-Ready Design v3

> **Purpose:** Production-ready implementation specification for the BizEERP Inventory Module.
>
> **Target stack:** .NET 8/10 + ASP.NET Core + EF Core + SQL Server + Blazor.
>
> **Design goal:** ERP-grade inventory correctness, traceability, costing, concurrency, auditability, month-end integrity, and maintainable .NET implementation.
>
> **Status:** Implementation-ready design after architectural review.
>
> **Important:** This document supersedes `inventory_new_design_v2(1).md`.

---

# 1. Executive Architecture

The inventory module uses the following core principles:

1. `InventoryDocument` is the business transaction.
2. `InventoryDocumentLine` is the requested business movement.
3. `StockMovementAllocation` explains how a document line was physically/cost-wise allocated.
4. `StockLedger` is the immutable inventory movement journal.
5. One document line may create **zero, one, or many** StockLedger rows.
6. `StockBalance` is current operational state, not historical truth.
7. `Lot` is the traceability identity and is **not a warehouse balance**.
8. `LotBalance`/warehouse-location stock state tracks where a lot currently exists.
9. `CostLayer` is the FIFO operational state.
10. `CostLayerAllocation` records FIFO consumption history.
11. `ItemCost` stores current costing state for moving-average/standard-cost processing.
12. Historical balances and valuation are derived **as-of the requested date** from immutable movements or validated snapshots.
13. Reversal is implemented by a compensating transaction.
14. Posted ledger rows are never updated or deleted.
15. Posting is atomic and concurrency-safe.
16. Closed periods cannot receive ordinary postings.
17. Backdated posting is blocked by default once later posted transactions exist.
18. Document numbering uses database-level atomic concurrency control.
19. Integration events use the transactional outbox pattern.
20. Company/Branch isolation is enforced centrally and validated explicitly at command boundaries.

---

# 2. Scope and Non-Goals

## 2.1 In Scope

- Item and SKU master data
- UOM and conversion
- Warehouses and locations
- Stock receipts
- Stock issues
- Material issue/return
- Stock transfers
- Stock adjustments
- Sales returns
- Purchase returns
- Lot/batch traceability
- Serial-number tracking
- FIFO costing
- Moving-average costing
- Standard costing foundation
- Reservations
- Assembly/disassembly genealogy
- Immutable stock ledger
- Current stock balances
- Month-end inventory periods
- Historical/as-of stock reporting
- Reversal
- Audit trail
- Outbox integration events
- Concurrency protection
- Data integrity/reconciliation

## 2.2 Explicitly Out of Scope for This Module

The inventory module does not become the full manufacturing/accounting module.

Future modules may consume inventory events for:

- General Ledger
- AP
- AR
- Sales
- Purchasing
- Production/BOM
- WIP
- Production variance
- Payroll

The inventory module exposes clean integration boundaries for those modules.

---

# 3. Organizational Scope Model

## 3.1 Company → Branch

The previous Tenant layer is removed because Tenant has no independent business meaning in this design.

```text
Company
   └── Branch
         ├── Warehouse
         │     └── Location
         └── Inventory transactions
```

## 3.2 Entity Scope Types

Use explicit base classes.

```csharp
public abstract class BaseEntity
{
    public long Id { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }

    public DateTime? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

public abstract class CompanyEntity : BaseEntity
{
    public long CompanyId { get; set; }
}

public abstract class BranchEntity : CompanyEntity
{
    public long BranchId { get; set; }
}

public abstract class SoftDeletableCompanyEntity : CompanyEntity
{
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
}

public abstract class SoftDeletableBranchEntity : BranchEntity
{
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
}
```

### Rule

Do not use a Company-scoped base class and then manually add `BranchId` to individual entities unless the entity is intentionally capable of both company-wide and branch-specific scope.

---

# 4. Company Context

Replace `ITenantProvider` with:

```csharp
public interface ICompanyContext
{
    long CompanyId { get; }
    long? BranchId { get; }
}
```

The context should be derived from the authenticated application context.

### Security Rules

- Never trust a client-supplied CompanyId.
- Never trust a client-supplied BranchId without authorization validation.
- JWT claims are preferred for identity/context.
- Headers may be used only when validated by trusted middleware.
- Every command that modifies inventory must validate CompanyId and BranchId explicitly.
- Global query filters are not the only security boundary.

---

# 5. Global Query Filters

Apply filters according to the actual entity scope.

Conceptually:

```text
CompanyEntity
    CompanyId = current CompanyId

BranchEntity
    CompanyId = current CompanyId
    BranchId  = current BranchId

SoftDeletableCompanyEntity
    CompanyId = current CompanyId
    IsDeleted = false

SoftDeletableBranchEntity
    CompanyId = current CompanyId
    BranchId  = current BranchId
    IsDeleted = false
```

Do not attempt to use one filter for every inventory entity.

### Important

Background jobs and administrative processes that intentionally operate across companies/branches must use an explicit elevated context rather than bypassing filters casually.

---

# 6. Core Master Data

## 6.1 Company

```csharp
public class Company : BaseEntity
{
    public string CompanyCode { get; set; } = null!;
    public string CompanyName { get; set; } = null!;
    public string BaseCurrencyCode { get; set; } = "MYR";
    public bool IsActive { get; set; } = true;

    public ICollection<Branch> Branches { get; set; } = [];
}
```

## 6.2 Branch

```csharp
public class Branch : CompanyEntity
{
    public string BranchCode { get; set; } = null!;
    public string BranchName { get; set; } = null!;
    public bool IsActive { get; set; } = true;

    public Company Company { get; set; } = null!;
}
```

## 6.3 Item

An Item is the commercial/product master.

```csharp
public class Item : SoftDeletableCompanyEntity
{
    public string ItemCode { get; set; } = null!;
    public string ItemDescription { get; set; } = null!;

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
```

### Branch-specific item behaviour

If an item can be globally defined but have branch-specific settings, do not add a nullable BranchId to Item.

Create:

```text
Item
ItemBranchSetting
```

Example:

```csharp
public class ItemBranchSetting : BranchEntity
{
    public long ItemId { get; set; }

    public bool IsActive { get; set; }
    public decimal MinStockQty { get; set; }
    public decimal MaxStockQty { get; set; }
    public decimal ReorderQty { get; set; }

    public CostingMethod? CostingMethodOverride { get; set; }
}
```

This avoids ambiguous scope semantics.

---

# 7. Item Variant / SKU

```csharp
public class ItemVariant : SoftDeletableCompanyEntity
{
    public long ItemId { get; set; }

    public string SKU { get; set; } = null!;
    public string? Barcode { get; set; }

    public long? ColorId { get; set; }
    public long? SizeId { get; set; }
    public long? ModelId { get; set; }

    public string? VariantDescription { get; set; }

    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}
```

Unique SKU:

```text
CompanyId + SKU
```

If the business requires branch-specific SKU, use an explicit branch SKU mapping instead of changing the meaning of SKU globally.

---

# 8. Reference Tables

Company-scoped reference data:

```text
ItemCategory
ItemSubCategory
Brand
Color
Size
Model
UOM
ReasonCode
```

UOM:

```csharp
public class UOM : SoftDeletableCompanyEntity
{
    public string UOMCode { get; set; } = null!;
    public string UOMName { get; set; } = null!;
    public int DecimalPlaces { get; set; }
    public bool IsActive { get; set; } = true;
}
```

---

# 9. UOM Conversion

```csharp
public class UOMConversion : SoftDeletableCompanyEntity
{
    public long ItemId { get; set; }

    public long FromUOMId { get; set; }
    public long ToUOMId { get; set; }

    public decimal ConversionRate { get; set; }
}
```

### Historical rule

Never calculate historical transaction quantities using the current conversion table.

At posting time, persist the actual conversion used:

```text
UnitQty
ConversionRateUsed
QtyInBase
QtyOutBase
```

This guarantees historical correctness if the conversion changes later.

---

# 10. Warehouse and Location

Warehouse is branch scoped.

```csharp
public class Warehouse : SoftDeletableBranchEntity
{
    public string WarehouseCode { get; set; } = null!;
    public string WarehouseName { get; set; } = null!;
    public bool IsActive { get; set; } = true;
}
```

Location:

```csharp
public class WarehouseLocation : SoftDeletableBranchEntity
{
    public long WarehouseId { get; set; }

    public string LocationCode { get; set; } = null!;
    public string? LocationName { get; set; }

    public bool IsActive { get; set; } = true;
}
```

### Integrity

A Location must belong to the current Branch and its Warehouse must belong to the same Branch.

This must be enforced in application validation and database constraints where practical.

---

# 11. Critical Lot Architecture Change

## 11.1 Lot Is a Traceability Identity

A Lot is not a warehouse balance.

Do not make warehouse movement create a new lot merely because the item moves between warehouses.

```csharp
public class Lot : BranchEntity
{
    public long ItemVariantId { get; set; }

    public string LotNo { get; set; } = null!;

    public long? SupplierId { get; set; }

    public string? SourceDocType { get; set; }
    public string? SourceDocNo { get; set; }
    public int? SourceDocLineNo { get; set; }

    public DateTime ReceivedDate { get; set; }
    public decimal ReceivedQty { get; set; }
    public decimal ReceivedUnitCost { get; set; }

    public string CostCurrencyCode { get; set; } = "MYR";

    public DateTime? ManufactureDate { get; set; }
    public DateTime? ExpiryDate { get; set; }

    public LotStatus Status { get; set; } = LotStatus.ACTIVE;

    public DateTime? ClosedAtUtc { get; set; }
    public string? StatusChangedBy { get; set; }
    public DateTime? StatusChangedAtUtc { get; set; }

    public string? Remarks { get; set; }
}
```

## 11.2 Lot Balance

Track where the lot physically exists separately:

```csharp
public class LotBalance : BranchEntity
{
    public long LotId { get; set; }
    public long WarehouseId { get; set; }
    public long? LocationId { get; set; }

    public decimal QtyOnHand { get; set; }
}
```

Unique key:

```text
CompanyId
BranchId
LotId
WarehouseId
LocationId
```

### Result

A transfer:

```text
Warehouse A
Lot LOT-001
Qty 30
       ↓
Warehouse B
Lot LOT-001
Qty 30
```

does not create another traceability identity.

---

# 12. When a New Lot Is Created

A new Lot may be created when the business process creates a new traceability identity.

Examples:

```text
GRN receipt
Manufacturing output
Repackaging
Blending
Transformation
Assembly output
Disassembly output
Business-defined lot split
```

A normal warehouse transfer does not automatically create a new lot.

---

# 13. Batch vs Lot

Do not maintain both Batch and Lot unless the business has a genuine distinction.

Recommended:

```text
Lot = traceability identity
```

If the business calls this "Batch", use Lot as the internal model and expose "Batch No" as the UI/business terminology if desired.

Avoid duplicate traceability concepts.

---

# 14. Inventory Documents

```csharp
public class InventoryDocument : BranchEntity
{
    public string DocNo { get; set; } = null!;
    public DocumentType DocType { get; set; }

    public DateTime DocDate { get; set; }

    public long WarehouseId { get; set; }

    public string? ReferenceNo { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.DRAFT;

    public string? Remarks { get; set; }

    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }

    public string? PostedBy { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    public long? ReversalOfDocumentId { get; set; }

    public ICollection<InventoryDocumentLine> Lines { get; set; } = [];
}
```

---

# 15. Inventory Document Lines

```csharp
public class InventoryDocumentLine : BranchEntity
{
    public long DocumentId { get; set; }

    public int LineNo { get; set; }

    public long ItemVariantId { get; set; }
    public long UOMId { get; set; }

    public decimal Qty { get; set; }

    public decimal ConversionRateUsed { get; set; }

    public decimal QtyInBase { get; set; }

    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }

    public long? LocationId { get; set; }

    public string? BatchNo { get; set; }
    public string? SerialNo { get; set; }

    public long? ReasonCodeId { get; set; }

    public string? Remarks { get; set; }

    public ICollection<StockMovementAllocation> Allocations { get; set; } = [];
}
```

---

# 16. Document Types

```csharp
public enum DocumentType
{
    GRN,   // Goods Received Note
    GI,    // Goods Issue
    MI,    // Material Issue
    MR,    // Material Return
    ST,    // Stock Transfer
    SA,    // Stock Adjustment
    SR,    // Sales Return
    PR,    // Purchase Return
    ASM,   // Assembly
    DSM    // Disassembly
}
```

Remove `SOH` unless the business has a demonstrably different process from `SA`.

If `SOH` is retained, its exact semantic difference must be documented before implementation.

---

# 17. Document Status

```csharp
public enum DocumentStatus
{
    DRAFT,
    SUBMITTED,
    APPROVED,
    POSTED,
    CANCELLED,
    REVERSED
}
```

### State transitions

```text
DRAFT
  ├── CANCELLED
  └── SUBMITTED
         ├── CANCELLED
         └── APPROVED
                ├── CANCELLED
                └── POSTED
                       └── REVERSED
```

A POSTED document cannot be edited.

---

# 18. StockMovementAllocation

This is the bridge between the business line and actual inventory allocation.

```csharp
public class StockMovementAllocation : BranchEntity
{
    public long DocumentLineId { get; set; }
    public long StockLedgerId { get; set; }

    public long? SourceLotId { get; set; }
    public long? TargetLotId { get; set; }

    public long? CostLayerId { get; set; }

    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }
}
```

### Responsibility

`InventoryDocumentLine` says:

> I want to issue 100 units.

`StockMovementAllocation` says:

> 40 came from Lot A and 60 came from Lot B.

`StockLedger` says:

> These are the actual immutable inventory movements.

---

# 19. StockLedger — Immutable Source of Historical Truth

One row represents one actual movement allocation.

```csharp
public class StockLedger : BranchEntity
{
    // Identity
    public DateTime TransactionDate { get; set; }
    public long LedgerSequence { get; set; }

    public DocumentType DocType { get; set; }
    public string DocNo { get; set; } = null!;
    public int LineNo { get; set; }

    // Document context
    public DateTime DocDate { get; set; }
    public string? ReferenceNo { get; set; }
    public string? DocRemarks { get; set; }

    public string? PostedBy { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    // Product
    public long ItemVariantId { get; set; }
    public string SKU { get; set; } = null!;
    public string ItemDescription { get; set; } = null!;

    // UOM
    public long UOMId { get; set; }
    public string UOMCode { get; set; } = null!;
    public decimal UnitQty { get; set; }
    public decimal ConversionRateUsed { get; set; }
    public decimal QtyInBase { get; set; }
    public decimal QtyOutBase { get; set; }

    // Location
    public long WarehouseId { get; set; }
    public string WarehouseName { get; set; } = null!;
    public long? LocationId { get; set; }
    public string? LocationCode { get; set; }

    // Traceability
    public long? LotId { get; set; }
    public string? LotNo { get; set; }
    public string? BatchNo { get; set; }
    public string? SerialNo { get; set; }

    // Reason
    public long? ReasonCodeId { get; set; }
    public string? ReasonCodeName { get; set; }

    // Cost
    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }

    public string CurrencyCode { get; set; } = "MYR";
    public decimal ExchangeRate { get; set; } = 1m;
    public decimal BaseAmount { get; set; }

    public CostingMethod CostingMethod { get; set; }

    // Reference
    public string ReferenceType { get; set; } = null!;
    public long ReferenceId { get; set; }
}
```

## Mandatory rules

- INSERT only after posting.
- Never UPDATE after posting.
- Never DELETE after posting.
- Correction = compensating movement.
- Every movement must have deterministic ordering.
- Every StockLedger row must belong to a posted business transaction.

---

# 20. Ledger Ordering

Use:

```text
LedgerSequence
```

as the primary deterministic posting order.

Recommended:

```text
TransactionDate
LedgerSequence
Id
```

`LedgerSequence` should be allocated atomically during posting.

All historical calculations must use the same ordering rule.

This ordering is required for:

- Stock card
- Moving average
- FIFO
- As-of valuation
- Backdated detection
- Reversal
- Audit reporting

---

# 21. StockBalance

Current operational stock state:

```csharp
public class StockBalance : BranchEntity
{
    public long WarehouseId { get; set; }
    public long ItemVariantId { get; set; }

    public decimal QtyOnHand { get; set; }
    public decimal ReservedQty { get; set; }

    public decimal AverageCost { get; set; }

    public DateTime LastUpdatedAtUtc { get; set; }

    public decimal AvailableQty =>
        QtyOnHand - ReservedQty;
}
```

Unique:

```text
CompanyId
BranchId
WarehouseId
ItemVariantId
```

### Important

StockBalance is a cache/current state.

Historical truth remains StockLedger.

---

# 22. Location-Level Balance

If location-level inventory is required:

```csharp
public class StockLocationBalance : BranchEntity
{
    public long WarehouseId { get; set; }
    public long LocationId { get; set; }
    public long ItemVariantId { get; set; }

    public decimal QtyOnHand { get; set; }
}
```

Use this only if the ERP actually needs location-level current stock.

---

# 23. LotBalance

```csharp
public class LotBalance : BranchEntity
{
    public long LotId { get; set; }

    public long WarehouseId { get; set; }
    public long? LocationId { get; set; }

    public decimal QtyOnHand { get; set; }
}
```

This is the current operational quantity of a lot at a physical location.

It must reconcile against StockLedger.

---

# 24. Serial Number

```csharp
public class SerialNumber : BranchEntity
{
    public long ItemVariantId { get; set; }
    public string SerialNo { get; set; } = null!;

    public SerialStatus Status { get; set; }

    public long? CurrentWarehouseId { get; set; }
    public long? CurrentLocationId { get; set; }

    public DateTime? ReceivedDateUtc { get; set; }
    public DateTime? SoldDateUtc { get; set; }
    public DateTime? ExpiryDateUtc { get; set; }
}
```

Serial history must come from StockLedger.

`SerialNumber` is current state, not historical truth.

---

# 25. FIFO CostLayer

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
    public decimal ExchangeRate { get; set; } = 1m;
    public decimal BaseUnitCost { get; set; }
}
```

FIFO rule:

```text
Oldest eligible open layer first.
```

### FIFO eligibility

A layer is eligible only when:

- Same Company
- Same Branch
- Same ItemVariant
- Same Warehouse
- Same relevant stock dimension
- RemainingQty > 0
- Layer is not blocked/quarantined if the business requires quality blocking

---

# 26. CostLayerAllocation

```csharp
public class CostLayerAllocation : BranchEntity
{
    public long CostLayerId { get; set; }
    public long StockLedgerId { get; set; }

    public decimal AllocatedQty { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }
}
```

FIFO allocation must be immutable after posting.

---

# 27. Moving Average Cost

For moving average:

### Receipt

```text
NewQty = OldQty + ReceiptQty

NewValue =
    OldQty * OldAverageCost
    +
    ReceiptQty * ReceiptUnitCost

NewAverageCost =
    NewValue / NewQty
```

For issue:

```text
IssueValue =
    IssueQty * CurrentAverageCost
```

Do not recalculate historical transactions using the current average cost.

The posting engine must use the average cost effective at the posting sequence.

---

# 28. Standard Cost

Standard costing needs explicit effective-date state.

```csharp
public class StandardCost : CompanyEntity
{
    public long ItemVariantId { get; set; }

    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    public decimal StandardUnitCost { get; set; }

    public string CurrencyCode { get; set; } = "MYR";
}
```

Future accounting integration can consume variance information.

Do not mix standard cost with moving-average state.

---

# 29. Currency Model

Persist both transaction and base currency information where required.

```text
TransactionCurrency
TransactionUnitCost
TransactionAmount
ExchangeRate
BaseCurrency
BaseUnitCost
BaseAmount
```

Historical exchange rates must be captured at posting time.

Never recalculate historical inventory valuation using today's exchange rate.

---

# 30. Reservations

```csharp
public class StockReservation : BranchEntity
{
    public long ItemVariantId { get; set; }
    public long WarehouseId { get; set; }

    public string SourceDocType { get; set; } = null!;
    public long SourceDocId { get; set; }
    public long? SourceDocLineId { get; set; }

    public decimal ReservedQty { get; set; }

    public DateTime? ExpiryDateUtc { get; set; }

    public ReservationStatus Status { get; set; }
}
```

Rules:

```text
AvailableQty = QtyOnHand - ActiveReservedQty
```

Reservation does not change StockLedger because reservation is not physical stock movement.

---

# 31. Assembly / Disassembly Genealogy

## 31.1 LotComposition

```csharp
public class LotComposition : BranchEntity
{
    public string DocNo { get; set; } = null!;
    public DateTime DocDate { get; set; }

    public CompositionOperationType OperationType { get; set; }

    public long? PrimaryLotId { get; set; }

    public decimal TotalInputCost { get; set; }
    public decimal TotalOutputCost { get; set; }

    public string? Remarks { get; set; }

    public ICollection<LotCompositionLine> Lines { get; set; } = [];
}
```

## 31.2 Direction-Aware Composition Line

```csharp
public class LotCompositionLine : BranchEntity
{
    public long CompositionId { get; set; }

    public int LineNo { get; set; }

    public CompositionLineType LineType { get; set; }

    public long ItemVariantId { get; set; }
    public long LotId { get; set; }

    public decimal Quantity { get; set; }

    public long UOMId { get; set; }

    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }
}
```

Enums:

```csharp
public enum CompositionLineType
{
    INPUT,
    OUTPUT
}

public enum CompositionOperationType
{
    ASSEMBLY,
    DISASSEMBLY
}
```

### Assembly

```text
INPUT Lot A
INPUT Lot B
INPUT Lot C
       ↓
OUTPUT Lot D
```

### Disassembly

```text
INPUT Lot D
       ↓
OUTPUT Lot A
OUTPUT Lot B
OUTPUT Lot C
```

This is the authoritative genealogy relationship.

---

# 32. Transfer Genealogy

A normal transfer preserves the same Lot identity.

```text
Warehouse A / Lot A
        ↓
Warehouse B / Lot A
```

The transfer allocation records:

```text
SourceWarehouse
SourceLocation
DestinationWarehouse
DestinationLocation
LotId
Quantity
```

No child Lot is created merely because of the transfer.

If a business-specific process creates a new traceability identity, use an explicit lot transformation event.

---

# 33. Posting Engine

```csharp
public interface IPostingEngine
{
    Task<DocumentDto> CreateDocumentAsync(
        CreateDocumentDto dto,
        CancellationToken ct = default);

    Task SubmitForApprovalAsync(
        long documentId,
        CancellationToken ct = default);

    Task ApproveAsync(
        long documentId,
        string approvedBy,
        CancellationToken ct = default);

    Task<PostingResultDto> PostAsync(
        long documentId,
        string postedBy,
        CancellationToken ct = default);

    Task CancelAsync(
        long documentId,
        CancellationToken ct = default);

    Task<DocumentDto> ReverseAsync(
        long documentId,
        string reversedBy,
        CancellationToken ct = default);
}
```

---

# 34. Posting Service Architecture

Use focused services:

```text
PostingEngine
 ├── DocumentPostingValidator
 ├── PeriodService
 ├── InventoryMovementEngine
 ├── AllocationEngine
 │    ├── LotAllocationService
 │    ├── SerialAllocationService
 │    └── ReservationAllocationService
 ├── CostingEngine
 │    ├── MovingAverageCosting
 │    ├── FIFOCosting
 │    └── StandardCosting
 ├── InventoryStateUpdater
 ├── StockLedgerWriter
 ├── LedgerSequenceService
 └── OutboxService
```

Do not put all inventory rules into one 2,000-line `PostAsync()` method.

---

# 35. Posting Transaction

Posting must run inside one database transaction.

```text
Begin Transaction
    ↓
Load Document
    ↓
Validate state/context
    ↓
Validate period
    ↓
Acquire deterministic inventory locks
    ↓
Validate quantities/reservations
    ↓
Calculate UOM conversions
    ↓
Allocate lots/serials/cost layers
    ↓
Calculate costs
    ↓
Create StockLedger rows
    ↓
Update StockBalance
    ↓
Update LotBalance
    ↓
Update CostLayer
    ↓
Update ItemCost
    ↓
Create Outbox event
    ↓
Mark document POSTED
    ↓
Commit
```

If any step fails:

```text
ROLLBACK EVERYTHING
```

---

# 36. Concurrency Strategy

Two concurrent OUT transactions must never consume the same available inventory.

Use SQL Server transactional locking.

For affected current-state rows:

```sql
WITH (UPDLOCK, HOLDLOCK)
```

or an equivalent safe strategy.

### Lock order

Always acquire locks in deterministic order:

```text
CompanyId
BranchId
WarehouseId
ItemVariantId
LocationId
LotId
CostLayerId
```

This reduces deadlock risk.

### RowVersion

`RowVersion` is useful as secondary optimistic protection but does not replace the inventory allocation lock strategy.

---

# 37. Negative Stock Policy

The posting engine must have an explicit policy.

Recommended default:

```text
Negative stock = BLOCK
```

unless a specific item/warehouse configuration explicitly allows it.

If negative stock is allowed:

- Costing implications must be defined.
- FIFO implications must be defined.
- Backdated transactions become more difficult.
- Month-end valuation must account for negative quantities.

Do not leave this as an implicit behaviour.

---

# 38. Backdated Posting Policy

Recommended V1 rule:

```text
Posting into a closed period:
    BLOCK

Posting into an open period when later posted transactions exist:
    BLOCK

Posting with no later transaction:
    ALLOW if all normal validation passes
```

If future requirements need backdated posting after later transactions:

```text
Controlled Recalculation
    ↓
Affected ledger range
    ↓
Recalculate costing
    ↓
Rebuild affected balances
    ↓
Audit event
```

Never silently rewrite immutable ledger history.

---

# 39. Reversal

Reversal creates a new compensating document.

```text
Original GRN +100
       ↓
Reversal document -100
```

Rules:

- Original document remains.
- Original StockLedger remains.
- Original CostLayer remains.
- Original Lot remains.
- Reversal creates compensating movements.
- Reversal updates current operational balances through normal posting logic.
- Original/reversal relationship is stored.
- A document may be reversed once.
- Reversal is subject to period rules.

---

# 40. Idempotent Posting

`PostAsync()` must be safe if the same request is retried.

If:

```text
Document.Status == POSTED
```

the service must not create another set of ledger movements.

Use database uniqueness and posting-state checks as a second layer.

Recommended unique business posting identity:

```text
CompanyId
BranchId
DocumentId
```

for the document's posting event/movement group.

---

# 41. Document Numbering

Never use:

```csharp
seq.CurrentNumber++;
SaveChangesAsync();
```

as the concurrency mechanism.

Use:

```sql
UPDATE DocumentSequence
SET CurrentNumber = CurrentNumber + 1
OUTPUT INSERTED.CurrentNumber
WHERE CompanyId = @CompanyId
  AND BranchId = @BranchId
  AND DocType = @DocType;
```

or SQL Server `SEQUENCE` where its semantics are appropriate.

Prefer pre-created sequence rows for supported document types.

---

# 42. Document Sequence Entity

```csharp
public class DocumentSequence : BranchEntity
{
    public string DocType { get; set; } = null!;
    public string? Prefix { get; set; }

    public long CurrentNumber { get; set; }
    public int NumberLength { get; set; } = 6;
}
```

Unique:

```text
CompanyId
BranchId
DocType
```

Sequence creation must handle duplicate-key races if lazy creation is retained.

Preferred approach:

> Initialize sequence rows when Branch/configuration is created.

---

# 43. Ledger Sequence

Use a separate database-controlled sequence for deterministic ledger ordering.

Possible design:

```text
LedgerSequence
```

must be generated atomically.

Do not depend on identity `Id` alone because inventory ordering should be explicit and business-controlled.

---

# 44. Month-End Period

```csharp
public class InventoryPeriod : CompanyEntity
{
    public int FiscalYear { get; set; }
    public int FiscalMonth { get; set; }

    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    public bool IsClosed { get; set; }

    public string? ClosedBy { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
}
```

A period must be unique:

```text
CompanyId
FiscalYear
FiscalMonth
```

---

# 45. Critical Month-End Rule

Never create a historical snapshot by copying today's `StockBalance`.

Incorrect:

```text
Current StockBalance
      ↓
July Snapshot
```

Correct:

```text
StockLedger
      ↓
TransactionDate <= July 31
      ↓
As-of July 31 calculation
      ↓
July Snapshot
```

This remains true even if July is closed on August 10.

---

# 46. StockSnapshot

```csharp
public class StockSnapshot : BranchEntity
{
    public long PeriodId { get; set; }

    public long WarehouseId { get; set; }
    public long ItemVariantId { get; set; }

    public decimal ClosingQty { get; set; }
    public decimal ClosingCost { get; set; }
    public decimal ClosingValue { get; set; }

    public DateTime SnapshotDate { get; set; }
}
```

Unique:

```text
CompanyId
BranchId
PeriodId
WarehouseId
ItemVariantId
```

If lot-level valuation is required, add a separate lot snapshot or an explicit lot dimension.

---

# 47. As-Of Valuation

Create a reusable service:

```csharp
public interface IInventoryValuationService
{
    Task<InventoryValuationResult> CalculateAsOfAsync(
        InventoryValuationQuery query,
        CancellationToken ct = default);
}
```

It must support:

```text
AsOfDate
Company
Branch
Warehouse
ItemVariant
Lot
```

depending on reporting requirements.

---

# 48. Historical Stock Card

Stock card must use immutable ledger rows.

Example:

```sql
SUM(QtyInBase - QtyOutBase)
OVER
(
    PARTITION BY ItemVariantId, WarehouseId
    ORDER BY TransactionDate, LedgerSequence, Id
    ROWS UNBOUNDED PRECEDING
)
```

For lot-specific stock card:

```text
PARTITION BY
ItemVariantId,
WarehouseId,
LotId
```

For location-specific stock card:

```text
PARTITION BY
ItemVariantId,
WarehouseId,
LocationId
```

Do not force all reporting dimensions into one generic running balance.

---

# 49. ClosePeriodService

The service must:

1. Lock the period.
2. Verify the period is still open.
3. Verify all documents are POSTED/CANCELLED/REVERSED as appropriate.
4. Verify no pending approvals remain.
5. Verify inventory integrity.
6. Calculate balances as-of period end.
7. Calculate valuation according to costing method.
8. Create snapshots.
9. Validate snapshot uniqueness.
10. Mark period closed.
11. Commit all changes atomically.

Closing a period must itself be transactional.

---

# 50. Period Concurrency

Two users must not close the same period simultaneously.

Use:

- RowVersion and/or
- database locking

to ensure only one close operation succeeds.

---

# 51. Stock Integrity Service

Provide:

```csharp
public interface IStockIntegrityService
{
    Task<IReadOnlyList<StockIntegrityIssue>> FindIssuesAsync(...);

    Task ReconcileStockBalanceAsync(...);

    Task ReconcileLotBalanceAsync(...);

    Task ReconcileCostLayersAsync(...);

    Task RebuildOperationalBalancesAsync(...);
}
```

Checks include:

```text
StockLedger quantity vs StockBalance
StockLedger quantity vs LotBalance
CostLayer remaining quantity
Reservation totals
Serial current state
Negative stock
Duplicate document postings
Duplicate snapshots
Invalid Company/Branch relationships
```

Repair operations must be explicit administrative operations and must produce audit logs.

---

# 52. SQL Views

## 52.1 vw_StockCard

Must derive running quantity from immutable ledger data.

Do not store RunningQty in StockLedger.

## 52.2 vw_LotLineage

Show:

```text
Parent Lot
Child Lot
Relationship
Quantity
Date
Document
```

for genuine transformation relationships.

Warehouse transfers do not require a child lot.

## 52.3 vw_LotFullTrace

Use `LEFT JOIN Lot` if non-lot-controlled movements should remain visible.

If the view is intentionally lot-only, document that explicitly.

## 52.4 vw_LotRecursiveUp

Support:

- transfer/transformation genealogy
- assembly input genealogy
- disassembly output genealogy

Use cycle protection rather than relying only on a fixed depth.

---

# 53. Lot Audit Procedure

`sp_LotAuditTrail` must not reference nonexistent fields such as:

```text
StockLedger.RunningQty
StockLedger.RunningValue
```

Calculate running values dynamically with SQL window functions.

The audit output should include:

1. Lot header
2. Movement history
3. Assembly inputs
4. Assembly outputs
5. Parent/child genealogy
6. Current balances
7. Cost information

---

# 54. Database Constraints

Required unique constraints:

```text
Company
    UNIQUE CompanyCode

Branch
    UNIQUE CompanyId, BranchCode

Item
    UNIQUE CompanyId, ItemCode

ItemVariant
    UNIQUE CompanyId, SKU

Warehouse
    UNIQUE CompanyId, BranchId, WarehouseCode

WarehouseLocation
    UNIQUE CompanyId, BranchId, WarehouseId, LocationCode

StockBalance
    UNIQUE CompanyId, BranchId, WarehouseId, ItemVariantId

Lot
    UNIQUE CompanyId, BranchId, LotNo

LotBalance
    UNIQUE CompanyId, BranchId, LotId, WarehouseId, LocationId

InventoryDocument
    UNIQUE CompanyId, BranchId, DocType, DocNo

InventoryDocumentLine
    UNIQUE DocumentId, LineNo

DocumentSequence
    UNIQUE CompanyId, BranchId, DocType

InventoryPeriod
    UNIQUE CompanyId, FiscalYear, FiscalMonth

StockSnapshot
    UNIQUE CompanyId, BranchId, PeriodId, WarehouseId, ItemVariantId
```

---

# 55. Foreign-Key Integrity

Where possible, enforce:

```text
Document.Branch
Document.Warehouse
DocumentLine.Document
DocumentLine.ItemVariant
DocumentLine.UOM
Warehouse.Branch
Location.Warehouse
Lot.ItemVariant
LotBalance.Lot
LotBalance.Warehouse
StockBalance.Warehouse
StockBalance.ItemVariant
CostLayer.ItemVariant
CostLayer.Warehouse
```

with proper foreign keys.

Cross-entity scope consistency should also be validated.

Example:

```text
Document.BranchId
must equal
Warehouse.BranchId
```

Do not rely only on application code.

---

# 56. Critical Indexes

StockLedger:

```text
(CompanyId, BranchId, ItemVariantId, WarehouseId,
 TransactionDate, LedgerSequence, Id)

(CompanyId, BranchId, LotId,
 TransactionDate, LedgerSequence, Id)

(CompanyId, BranchId, DocType, DocNo)

(CompanyId, BranchId, ReferenceType, ReferenceId)

(CompanyId, BranchId, WarehouseId, TransactionDate)
```

CostLayer:

```text
(CompanyId, BranchId, ItemVariantId, WarehouseId,
 RemainingQty, ReceivedDate, Id)
```

LotBalance:

```text
(CompanyId, BranchId, LotId, WarehouseId, LocationId)
```

Actual query plans must be reviewed against production-like data.

---

# 57. Data Types and Precision

Use explicit SQL decimal precision.

Recommended examples:

```text
Quantity: decimal(19,6)
Unit cost: decimal(19,6)
Amount: decimal(19,4)
Exchange rate: decimal(19,10)
```

Exact precision should be confirmed against business requirements.

Do not rely on EF/database defaults for financial fields.

---

# 58. Date/Time Policy

Store technical timestamps as UTC:

```text
CreatedAtUtc
ModifiedAtUtc
PostedAtUtc
ApprovedAtUtc
ClosedAtUtc
```

Business transaction date:

```text
DocDate
TransactionDate
```

is a business date and must be interpreted according to the company's/branch's timezone.

Do not use `DateTime.UtcNow.Date` to determine a business posting date.

---

# 59. Audit Policy

Immutable inventory history:

```text
StockLedger
CostLayerAllocation
StockMovementAllocation
LotComposition
```

should not be editable after posting.

Administrative corrections happen through:

```text
Reversal
Adjustment
Controlled repair
```

Every administrative repair must produce an audit record.

---

# 60. Outbox Pattern

```csharp
public class IntegrationEvent : CompanyEntity
{
    public string EventType { get; set; } = null!;
    public string EventPayload { get; set; } = null!;

    public IntegrationEventStatus Status { get; set; }
        = IntegrationEventStatus.PENDING;

    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;

    public string? ErrorMessage { get; set; }

    public DateTime? PublishedAtUtc { get; set; }
}
```

The outbox row is inserted in the same database transaction as posting.

A background publisher later sends the event.

Never publish the external event first and then commit inventory.

---

# 61. Event Examples

Potential events:

```text
InventoryDocumentPosted
InventoryDocumentReversed
InventoryPeriodClosed
StockAdjusted
StockTransferred
LotCreated
LotConsumed
InventoryBalanceChanged
```

Events should contain identifiers and enough context for downstream consumers without making the inventory database a dependency of every other module.

---

# 62. API/Application Layer

Recommended application commands:

```text
CreateInventoryDocument
SubmitInventoryDocument
ApproveInventoryDocument
PostInventoryDocument
CancelInventoryDocument
ReverseInventoryDocument
CloseInventoryPeriod
CreateReservation
ReleaseReservation
```

Queries:

```text
GetStockBalance
GetStockCard
GetLotTrace
GetLotBalance
GetInventoryValuation
GetFIFOState
GetInventoryPeriod
```

Commands should not expose EF entities directly.

Use DTOs/commands/results.

---

# 63. EF Core Rules

Use:

```text
AsNoTracking()
```

for read-only reporting queries.

Use tracked entities for posting state updates.

Do not keep a DbContext alive across long-running operations.

Use:

```csharp
CancellationToken
```

throughout async application/service/database calls.

Do not call `SaveChangesAsync()` repeatedly inside a single posting operation unless there is a deliberate reason.

Preferred:

```text
One business transaction
One database transaction
One final SaveChanges
```

with raw SQL/EF commands where atomic locking is required.

---

# 64. Transaction Isolation

Use SQL Server transaction isolation intentionally.

For normal posting:

```text
READ COMMITTED
+
UPDLOCK/HOLDLOCK where inventory allocation requires it
```

or an equivalent design.

Do not enable SERIALIZABLE for the entire inventory subsystem without measuring its effect.

Lock only the affected inventory rows/ranges.

---

# 65. Posting Validation

Before modifying state, validate:

```text
Company
Branch
Document status
Period
Warehouse
Item
SKU
UOM
Quantity
UOM conversion
Lot
Serial
Reservation
Negative stock policy
Costing method
Currency
Reason code
Source document
Destination warehouse
```

All referenced entities must belong to the correct Company/Branch scope.

---

# 66. Stock Transfer Posting

Transfer should be atomic.

```text
Source Warehouse
    ↓
OUT allocation
    ↓
Destination Warehouse
    ↓
IN allocation
```

Both movements must be created in the same transaction.

If any part fails:

```text
No source OUT
No destination IN
No balance update
No ledger rows
```

---

# 67. Stock Adjustment

Adjustment must always capture:

```text
ReasonCode
OldQuantity
AdjustedQuantity
Difference
UnitCost
Amount
User
Date
Document
```

Do not allow unexplained quantity changes.

---

# 68. Lot Allocation Rules

For an OUT transaction:

1. If user explicitly selects a lot, validate availability.
2. If automatic allocation is requested, use configured policy.
3. For FIFO, allocate oldest eligible cost layers.
4. Split the document line if multiple lots/layers are required.
5. Create one StockLedger row per actual allocation.
6. Update LotBalance.
7. Update CostLayer remaining quantity.
8. Record StockMovementAllocation.

---

# 69. Serial Allocation Rules

For serial-controlled items:

```text
Qty must equal number of serials.
```

Each serial must:

- Exist for an OUT
- Be IN_STOCK
- Belong to the correct item
- Belong to the correct warehouse/location
- Not already be consumed

Receipt creates serial identities.

Transfer changes current location.

Sale/issue changes status.

All history is retained in StockLedger.

---

# 70. Reservation Allocation

When issuing reserved stock:

```text
Reservation
   ↓
Allocate reservation
   ↓
Reduce ReservedQty
   ↓
Create actual StockLedger movement
```

Reservation itself does not create a stock movement.

---

# 71. Reconciliation Model

The system should periodically validate:

```text
StockBalance
=
SUM(StockLedger QtyIn - QtyOut)

LotBalance
=
SUM(StockLedger by Lot/Warehouse/Location)

FIFO RemainingQty
=
OriginalQty - AllocatedQty

AvailableQty
=
QtyOnHand - ReservedQty
```

Any mismatch becomes an integrity issue.

---

# 72. Reporting Architecture

### Current stock

Use:

```text
StockBalance
```

### Lot current stock

Use:

```text
LotBalance
```

### Historical stock card

Use:

```text
StockLedger
```

### FIFO state

Use:

```text
CostLayer
```

### Month-end

Use:

```text
StockSnapshot
```

### Lot genealogy

Use:

```text
LotComposition
Lot
StockLedger
```

Do not make reports depend on mutable current-state tables for historical information.

---

# 73. Implementation Phases

## Phase 1 — Foundation

1. Remove Tenant.
2. Implement Company/Branch scope classes.
3. Implement Company context.
4. Implement global filters.
5. Implement authorization validation.
6. Update existing entities.
7. Configure EF Core precision.
8. Configure UTC timestamps.
9. Configure database constraints.
10. Configure indexes.

## Phase 2 — Master Data

11. Item.
12. ItemVariant.
13. UOM.
14. UOMConversion.
15. Category/Brand/Color/Size/Model.
16. Warehouse.
17. WarehouseLocation.
18. ReasonCode.
19. Supplier references.

## Phase 3 — Core Transactions

20. InventoryDocument.
21. InventoryDocumentLine.
22. Document workflow.
23. DocumentSequence.
24. PostingEngine.
25. StockLedger.
26. StockBalance.
27. LedgerSequence.

## Phase 4 — Allocation

28. StockMovementAllocation.
29. Lot.
30. LotBalance.
31. SerialNumber.
32. Reservation.
33. FIFO CostLayer.
34. CostLayerAllocation.

## Phase 5 — Costing

35. Moving Average.
36. FIFO.
37. Standard Cost foundation.
38. Currency/exchange-rate capture.
39. Costing unit tests.

## Phase 6 — Transfers

40. Transfer source validation.
41. Source allocation.
42. Destination allocation.
43. Same-lot preservation.
44. Cross-warehouse atomic posting.
45. Transfer reversal.

## Phase 7 — Assembly/Disassembly

46. LotComposition.
47. Direction-aware CompositionLine.
48. Assembly input consumption.
49. Assembly output creation.
50. Disassembly input consumption.
51. Disassembly output creation.
52. Genealogy queries.

## Phase 8 — Period and Valuation

53. InventoryPeriod.
54. As-of valuation.
55. StockSnapshot.
56. ClosePeriodService.
57. Period locking.
58. Month-end reconciliation.

## Phase 9 — Reporting

59. Stock card.
60. Current stock.
61. Lot trace.
62. Lot genealogy.
63. FIFO valuation.
64. Month-end valuation.
65. Audit trail.

## Phase 10 — Integration

66. Outbox.
67. Inventory events.
68. Background publisher.
69. Retry/dead-letter handling.
70. Accounting integration contract.

## Phase 11 — Integrity and Hardening

71. Concurrency tests.
72. Deadlock tests.
73. Duplicate-post tests.
74. Reversal tests.
75. Backdated tests.
76. Period-close tests.
77. FIFO tests.
78. Lot genealogy tests.
79. UOM history tests.
80. Currency tests.
81. Reconciliation tests.
82. Performance tests.

---

# 74. Mandatory Test Scenarios

## Basic

- GRN +100
- GI -20
- MR +10
- PR -10
- SA +5

## FIFO

```text
GRN 100 @ 10
GRN 100 @ 12
GI 150
```

Expected:

```text
100 @ 10
50  @ 12
```

## Moving Average

```text
GRN 100 @ 10
GRN 100 @ 20
```

Expected average:

```text
15
```

Then:

```text
GI 50
```

Issue cost must use the effective average at the time of issue.

## Multi-lot issue

```text
Lot A 40
Lot B 60
GI 100
```

Expected:

```text
2 StockLedger rows
2 allocations
```

## Transfer

```text
Warehouse A
Lot A 100

Transfer 40

Warehouse A
Lot A 60

Warehouse B
Lot A 40
```

No new lot identity.

## Concurrent issue

Two simultaneous GI transactions cannot consume the same stock.

## Duplicate post

Two simultaneous `PostAsync(documentId)` calls produce one posting.

## Reversal

Original remains unchanged; compensating document reverses the operational effect.

## Month-end

July closes on August 10.

Transactions on August 1–10 must not affect July snapshot.

## UOM history

Conversion changes after posting.

Old ledger quantities remain unchanged.

## Currency history

Exchange rate changes after posting.

Historical base amount remains unchanged.

---

# 75. Performance Requirements

Measure using production-like volumes.

Test at minimum:

```text
1 million StockLedger rows
10 million StockLedger rows
50 million StockLedger rows
```

depending on expected ERP scale.

Validate:

- Stock card
- Lot trace
- FIFO allocation
- As-of valuation
- Month-end close
- Current balance
- Reconciliation

Do not prematurely optimize without actual query plans.

---

# 76. Operational Monitoring

Log:

```text
DocumentId
DocNo
DocType
CompanyId
BranchId
Posting duration
Allocation count
Ledger rows created
Cost layers consumed
Transaction retry
Deadlock retry
```

Do not log sensitive business payloads unnecessarily.

Serilog can be used for structured application logging.

Inventory audit data belongs in the database, not only in log files.

---

# 77. Error Handling

Use business exceptions/results for:

```text
PeriodClosed
DocumentAlreadyPosted
InsufficientStock
LotNotAvailable
SerialNotAvailable
InvalidWarehouse
InvalidBranch
InvalidCompany
InvalidUOM
InvalidConversion
InvalidCosting
DuplicatePosting
DocumentAlreadyReversed
```

Do not expose SQL exceptions directly to the UI.

---

# 78. API Reliability

For POST commands:

- Use idempotent business operations.
- Return the document/posting result.
- Support cancellation tokens.
- Avoid retrying non-idempotent operations blindly.
- Database uniqueness must protect against duplicate requests.

---

# 79. EF Core Configuration Checklist

For every entity verify:

- Primary key
- Foreign keys
- Decimal precision
- Required fields
- Max string lengths
- Indexes
- Unique constraints
- RowVersion
- Delete behaviour
- Query filter
- Company/Branch scope

For historical entities:

```text
Do not use cascade delete that could remove posted inventory history.
```

Prefer restrictive delete behaviour for posted transactional data.

---

# 80. Database Delete Policy

Master data may be soft-deleted.

Posted inventory data must never be physically deleted.

Examples:

```text
Item              Soft delete
Warehouse         Soft delete
Location          Soft delete
Document           No delete after posting
StockLedger        No delete
CostLayer          No delete
CostLayerAllocation No delete
Lot                No delete
LotComposition     No delete
```

---

# 81. Final Architecture Rules

These are mandatory:

1. Company is the top business scope.
2. Branch is the operational inventory scope.
3. Company and Branch scopes are represented explicitly by base classes.
4. Global query filters are not the only authorization mechanism.
5. InventoryDocument is the business transaction.
6. DocumentLine is the business movement instruction.
7. Allocation explains physical/cost allocation.
8. StockLedger is immutable historical truth.
9. One DocumentLine may create many StockLedger rows.
10. One StockLedger row represents one actual movement allocation.
11. StockBalance is current state.
12. Lot is a traceability identity.
13. Lot is not a warehouse balance.
14. LotBalance stores current lot quantity by warehouse/location.
15. Warehouse transfer normally preserves the same Lot identity.
16. New lots are created only when a genuine traceability transformation occurs.
17. FIFO uses CostLayer and CostLayerAllocation.
18. Moving Average uses current costing state plus immutable movements.
19. Standard Cost uses effective-dated standard-cost records.
20. Historical transactions store conversion/rate values used at posting.
21. Historical running balances are derived from immutable ledger data.
22. Month-end snapshots are calculated as-of period end.
23. Current StockBalance must never be copied blindly into a historical period snapshot.
24. Reversal is a compensating transaction.
25. Posted history is never updated or deleted.
26. Posting is atomic.
27. Posting uses concurrency protection.
28. Inventory locks are acquired deterministically.
29. Document numbering is database-atomic.
30. Posting is idempotent.
31. Closed periods block ordinary posting.
32. Backdated posting is blocked by default when later transactions exist.
33. UOM conversion changes must not alter historical transactions.
34. Exchange-rate changes must not alter historical transactions.
35. Reservation does not create stock movement.
36. Serial history comes from immutable inventory movements.
37. Assembly/disassembly explicitly distinguishes INPUT and OUTPUT.
38. Lot genealogy must support transformation relationships.
39. SQL reports must not reference nonexistent mutable running-balance fields.
40. Outbox events are committed in the same transaction as inventory posting.
41. Reconciliation tools must detect divergence between ledger and operational state.
42. Critical Company/Branch relationships are enforced by constraints and validation.
43. Financial quantities use explicit decimal precision.
44. Business dates are distinct from UTC technical timestamps.
45. No inventory rule is hidden inside UI code.

---

# 82. Recommended Final Data Flow

```text
                    InventoryDocument
                           │
                           ▼
                  DocumentPostingValidator
                           │
                           ▼
                     PostingEngine
                           │
             ┌─────────────┼─────────────┐
             ▼             ▼             ▼
       MovementEngine  AllocationEngine  CostingEngine
                           │             │
                  ┌────────┼───────┐     ├─ Moving Average
                  ▼        ▼       ▼     ├─ FIFO
                 Lot     Serial  Reserve  └─ Standard
                  │
                  ▼
          StockMovementAllocation
                  │
                  ▼
             StockLedger
          immutable movement truth
                  │
       ┌──────────┼───────────┐
       ▼          ▼           ▼
 StockBalance  LotBalance  CostLayer
 current       current      FIFO state
 state         state
       │          │           │
       └──────────┼───────────┘
                  ▼
           Reconciliation
                  │
                  ▼
        Reporting / Valuation
                  │
          ┌───────┼────────┐
          ▼       ▼        ▼
       StockCard LotTrace MonthEnd
                            │
                            ▼
                       StockSnapshot
                            │
                            ▼
                      Period Closed
```

---

# 83. Final Readiness Checklist

Before allowing the AI coding agent to implement, verify:

## Architecture

- [ ] Company/Branch model approved
- [ ] No ambiguous Tenant concept
- [ ] Scope base classes approved
- [ ] Lot identity separated from warehouse balance
- [ ] Assembly/disassembly direction model approved

## Database

- [ ] All unique constraints defined
- [ ] All foreign keys defined
- [ ] Decimal precision defined
- [ ] Indexes defined
- [ ] Delete behaviour defined
- [ ] Query filters tested

## Posting

- [ ] Transaction boundary defined
- [ ] Lock strategy defined
- [ ] Lock ordering defined
- [ ] Idempotency defined
- [ ] Negative-stock policy defined
- [ ] Backdated policy defined
- [ ] Reversal defined

## Costing

- [ ] Moving Average formula tested
- [ ] FIFO allocation tested
- [ ] Standard Cost model approved
- [ ] Currency model approved
- [ ] Historical UOM conversion captured

## Period

- [ ] As-of valuation implemented
- [ ] Snapshot uniqueness defined
- [ ] Period locking concurrency defined
- [ ] Month-end reconciliation defined

## Traceability

- [ ] Lot lifecycle defined
- [ ] Lot transfer defined
- [ ] Assembly defined
- [ ] Disassembly defined
- [ ] Serial lifecycle defined
- [ ] Genealogy queries tested

## Integration

- [ ] Outbox implemented
- [ ] Event contracts defined
- [ ] Retry handling defined
- [ ] Accounting boundary defined

## Testing

- [ ] Concurrent posting tests
- [ ] FIFO tests
- [ ] Moving average tests
- [ ] Transfer tests
- [ ] Reversal tests
- [ ] Month-end tests
- [ ] Lot genealogy tests
- [ ] Serial tests
- [ ] UOM tests
- [ ] Currency tests
- [ ] Reconciliation tests
- [ ] Performance tests

---

# 84. Final Professional Recommendation

This v3 design should be treated as the **implementation contract**.

The most important architectural corrections compared with v2 are:

```text
1. Lot identity ≠ warehouse stock
2. LotBalance handles physical lot location
3. DocumentLine → Allocation → StockLedger is explicit
4. StockLedger supports multiple movement rows per line
5. Assembly/Disassembly has explicit INPUT/OUTPUT lines
6. Month-end is genuinely AS-OF period end
7. Historical UOM and exchange-rate values are persisted
8. Posting is idempotent
9. Inventory concurrency uses deterministic locking
10. Document numbering is database atomic
11. RunningQty/RunningValue are never treated as stored ledger fields
12. Reconciliation is a first-class capability
13. Standard costing has its own effective-dated model
14. Company/Branch scope is represented correctly in .NET types
15. Transfer normally preserves Lot identity
```

**Target implementation quality: 9.5–10/10**, provided the implementation follows this document and the mandatory concurrency, costing, period, and reconciliation tests pass.

The design should be implemented in phases. Do not let an AI coding agent implement the entire inventory module in one pass. Complete and test each phase before moving to the next.
