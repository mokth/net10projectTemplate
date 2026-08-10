# BizEERP Inventory Module — Implementation Contract v3.1

> **Status:** **SUPERSEDED** by [`inventory_new_design_v3.2.md`](inventory_new_design_v3.2.md).
>
> Kept for history. Do **not** implement from this file — use v3.2.
>
> **Purpose:** Production-ready implementation contract for the BizEERP Inventory Module on the live ErpWeb foundation.
>
> **Target stack:** .NET 10 + ASP.NET Core + EF Core + SQL Server + Blazor Server + DevExpress.
>
> **Original status:** Implementation contract after architectural review. Superseded for coding by v3.2 after second professional review.
>
> **Principle:** Lock the inventory engine. Sync to existing ErpWeb. Do not redesign while coding.

---

# 0. Blocking Checklist (DONE)

All items below are finalized in this document:

- [x] `ICurrentUserService → ICompanyContext` bridge
- [x] No Tenant architecture; no JWT migration
- [x] Branch master; Warehouse/Location finalized; `LocationCode` meaning documented
- [x] `ItemCost` defined + MAV algorithm + lock/update ownership
- [x] Transfer Source/Destination Warehouse (+ Location)
- [x] Lot unique key `(CompanyId, ItemVariantId, LotNo)`
- [x] SupplierRef strategy (no FK to missing table)
- [x] Opening Balance (`OB`) defined
- [x] Stock Take → SA workflow defined
- [x] Allocation/ledger insert order defined
- [x] Inter-branch transfer V1 = same-branch only
- [x] Inventory period as-of rules defined
- [x] Document numbering defined (Prefix + yyMM + running)
- [x] Database locking strategy + lock order defined
- [x] Idempotent posting defined
- [x] Reversal defined
- [x] Reconciliation defined
- [x] Existing RBAC / MenuCodes / PermissionCodes integrated
- [x] `VIEW_COST` enforced in Core
- [x] Blazor/DevExpress UI pattern defined
- [x] SQL-script + Fluent database strategy confirmed
- [x] Phase 0–5 implementation plan finalized
- [x] Deferred features listed (FIFO, Serial, ASM, Outbox, …)

---

# 1. Executive Architecture (LOCKED)

1. `InventoryDocument` is the business transaction.
2. `InventoryDocumentLine` is the requested business movement.
3. `StockMovementAllocation` explains how a document line was physically/cost-wise allocated.
4. `StockLedger` is the immutable inventory movement journal (historical truth).
5. One document line may create **zero, one, or many** StockLedger rows.
6. `StockBalance` is current operational qty state/cache — not historical truth.
7. `ItemCost` is current Moving Average costing state (source of truth for current avg).
8. `Lot` is the traceability identity and is **not** a warehouse balance.
9. `LotBalance` tracks where a lot currently exists (qty by warehouse/location).
10. Historical balances and valuation are derived **as-of** from immutable ledger or validated snapshots.
11. Reversal is a compensating transaction.
12. Posted ledger rows are never updated or deleted.
13. Posting is atomic, concurrency-safe, and idempotent.
14. Closed periods cannot receive ordinary postings.
15. Backdated posting is blocked by default once later posted transactions exist.
16. Document numbering uses database-level atomic concurrency control.
17. Company/Branch isolation is enforced via `ICompanyContext` + explicit command validation + RBAC.
18. V1 costing method is **Moving Average only**.

```text
InventoryDocument
        │
        ▼
InventoryDocumentLine
        │
        ▼
StockMovementAllocation
        │
        ▼
StockLedger  (immutable historical truth)
        │
        ├──► StockBalance  (qty cache)
        ├──► ItemCost      (MAV cost state)
        └──► LotBalance    (lot qty cache)
```

---

# 2. Scope

## 2.1 In Scope (Phases 0–5)

- ErpWeb context bridge (`ICompanyContext`)
- Branch / Warehouse / Location masters
- Item and ItemVariant (SKU)
- UOM and conversion (with historical rate capture)
- Reason codes
- Documents: **OB, GRN, GI, ST, SA** + Stock Take worksheet workflow
- Moving Average costing via `ItemCost`
- Immutable StockLedger + StockBalance
- Lot/Batch (Phase 3) with LotBalance
- Reversal, idempotent posting, concurrency locks
- Inventory period close + as-of valuation + snapshot
- Reconciliation service
- Menus, RBAC (`POST`, `REVERSE`, `VIEW_COST`, …), Blazor UI
- SQL scripts + EF Fluent configuration

## 2.2 Explicitly Deferred (after Phase 5)

- FIFO `CostLayer` / `CostLayerAllocation`
- Standard costing
- Serial numbers
- Reservations
- Assembly / Disassembly / LotComposition
- Sales Return (SR) / Purchase Return (PR) / Material Issue (MI) / Material Return (MR) as first-class types (may map later)
- Transactional outbox / GL / AP / AR integration
- Landed cost / import cost adjustment
- Cross-branch transfer
- Inter-company transfer

Future modules may consume inventory events later; V1 does not implement outbox.

---

# 3. ErpWeb Integration Bridge (BLOCKER — DONE)

## 3.1 Do not redesign foundation

Live ErpWeb already has:

```text
Cookie Authentication
        │
        ▼
ICurrentUserService
        ├── CompanyCode
        ├── BranchCode
        └── LocationCode
```

Also: Company master, Admin RBAC (`MenuCodes`, `PermissionCodes`, `CanAsync`), Blazor Server + DevExpress.

**Forbidden:**

- Introduce JWT solely for Inventory
- Introduce TenantId
- Rewrite Company / Role / menu-access foundation
- Trust client-supplied CompanyId / BranchId

## 3.2 ICompanyContext

```csharp
public interface ICompanyContext
{
    int CompanyId { get; }
    string CompanyCode { get; }
    long BranchId { get; }
    string BranchCode { get; }

    /// <summary>
    /// Legacy claim from userlogin. NOT an inventory bin/location.
    /// Do not use as WarehouseLocationId.
    /// </summary>
    string? LegacyLocationCode { get; }

    string TimeZoneId { get; }   // from Company; default Asia/Kuala_Lumpur
    string BaseCurrencyCode { get; } // from Company; default MYR
}
```

Resolution:

```text
Cookie Claims
     │
     ▼
ICurrentUserService
     │
     ▼
ICompanyContext  (resolve CompanyCode → Company.CompanyId,
                  BranchCode → Branch.Id within company)
     │
     ▼
Inventory Services / PostingEngine
```

Register as scoped in `AddErpWebCore`. Inventory commands always take context from this service, never from DTO company fields alone (DTO may carry display codes; service re-validates).

## 3.3 Meaning of existing LocationCode

| Field | Meaning |
|---|---|
| `ICurrentUserService.LocationCode` | Legacy user claim (historical ERPLite field). **Not** inventory bin. |
| `Warehouse` | Inventory warehouse master (branch-scoped). |
| `WarehouseLocation` | Inventory bin/location inside a warehouse. |

Document this in code comments on `ICompanyContext.LegacyLocationCode`.

## 3.4 Company entity

Reuse existing [`ErpWeb.Model.Entities.Company`](ErpWeb.Model/Entities/Company.cs):

- PK: `int CompanyId`
- Business key: `CompanyCode` (nvarchar, max 5 in live schema)
- Already has `CurrencyCode`, `TimeZoneId`, `FiscalYearStartMonth`

Do **not** force `Company` onto inventory `BaseEntity` with `long Id`. New inventory tables use their own key types; FK to Company via `CompanyId` (int).

## 3.5 Key type policy

| Scope | Type |
|---|---|
| Existing Company PK | `int CompanyId` |
| New Branch / inventory entities | `long Id` (identity) |
| Audit on new entities | `CreatedAtUtc`, `ModifiedAtUtc`, `CreatedBy`, `ModifiedBy` |
| Existing Admin entities | Leave `CreatedDate` / `ModifiedDate` unchanged |

---

# 4. Organizational Scope Model

```text
Company
   └── Branch
         ├── Warehouse
         │     └── WarehouseLocation
         └── Inventory transactions
```

## 4.1 Base classes (inventory module)

```csharp
public abstract class InventoryEntity
{
    public long Id { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

public abstract class CompanyScopedEntity : InventoryEntity
{
    public int CompanyId { get; set; }
}

public abstract class BranchScopedEntity : CompanyScopedEntity
{
    public long BranchId { get; set; }
}

public abstract class SoftDeletableCompanyEntity : CompanyScopedEntity
{
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
}

public abstract class SoftDeletableBranchEntity : BranchScopedEntity
{
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
}
```

## 4.2 Branch

```csharp
public class Branch : SoftDeletableCompanyEntity
{
    public string BranchCode { get; set; } = null!;
    public string BranchName { get; set; } = null!;
    public bool IsActive { get; set; } = true;
}
```

Unique: `(CompanyId, BranchCode)`.

Seed default branch `HQ` when creating a company (align with `CompanyService.DefaultBranchCode`).

## 4.3 Global query filters

Apply to inventory entities only:

- `CompanyScopedEntity` → `CompanyId == context.CompanyId` (+ soft-delete where applicable)
- `BranchScopedEntity` → also `BranchId == context.BranchId` for operational queries

Filters are **not** the only security boundary. Commands still validate scope explicitly.

Elevated/admin jobs that cross companies must use an explicit elevated context — do not casually `IgnoreQueryFilters()`.

---

# 5. Master Data

## 5.1 Item

```csharp
public class Item : SoftDeletableCompanyEntity
{
    public string ItemCode { get; set; } = null!;
    public string ItemDescription { get; set; } = null!;

    public long? CategoryId { get; set; }
    public long? BrandId { get; set; }

    public long BaseUOMId { get; set; }

    public bool IsStockItem { get; set; } = true;
    public bool IsBatchItem { get; set; }   // Lot/Batch controlled (Phase 3)

    public CostingMethod CostingMethod { get; set; } = CostingMethod.MOVING_AVG;

    public decimal MinStockQty { get; set; }
    public decimal MaxStockQty { get; set; }
    public decimal ReorderQty { get; set; }

    /// <summary>Placeholder for Sales/Purchase SST later. Not used by inventory posting V1.</summary>
    public string? TaxCode { get; set; }

    public bool IsActive { get; set; } = true;
}

public enum CostingMethod
{
    MOVING_AVG = 1
    // FIFO / STANDARD deferred
}
```

Unique: `(CompanyId, ItemCode)`.

Branch-specific settings (optional V1.1): `ItemBranchSetting` — do not put nullable `BranchId` on `Item`.

## 5.2 ItemVariant (SKU)

```csharp
public class ItemVariant : SoftDeletableCompanyEntity
{
    public long ItemId { get; set; }
    public string SKU { get; set; } = null!;
    public string? Barcode { get; set; }
    public string? VariantDescription { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}
```

Unique: `(CompanyId, SKU)`.

V1 may create one default variant per item automatically.

## 5.3 UOM and conversion

```csharp
public class UOM : SoftDeletableCompanyEntity
{
    public string UOMCode { get; set; } = null!;
    public string UOMName { get; set; } = null!;
    public int DecimalPlaces { get; set; } = 4;
    public bool IsActive { get; set; } = true;
}

public class UOMConversion : SoftDeletableCompanyEntity
{
    public long ItemId { get; set; }
    public long FromUOMId { get; set; }
    public long ToUOMId { get; set; }
    public decimal ConversionRate { get; set; }
}
```

**Historical rule:** at posting, persist `ConversionRateUsed`, `QtyInBase` / `QtyOutBase`. Never recalculate historical qty from current conversion table.

## 5.4 Warehouse and Location

```csharp
public class Warehouse : SoftDeletableBranchEntity
{
    public string WarehouseCode { get; set; } = null!;
    public string WarehouseName { get; set; } = null!;
    public bool IsActive { get; set; } = true;
}

public class WarehouseLocation : SoftDeletableBranchEntity
{
    public long WarehouseId { get; set; }
    public string LocationCode { get; set; } = null!;
    public string? LocationName { get; set; }
    public bool IsActive { get; set; } = true;
}
```

Integrity: Location.Warehouse must belong to same Branch as Location.BranchId. Enforce in validation + FK.

## 5.5 ReasonCode

```csharp
public class ReasonCode : SoftDeletableCompanyEntity
{
    public string ReasonCodeValue { get; set; } = null!;
    public string ReasonName { get; set; } = null!;
    public string AppliesTo { get; set; } = null!; // SA, GI, STK, etc.
    public bool IsActive { get; set; } = true;
}
```

Adjustments and stock-take variance **require** a reason code.

## 5.6 Reference tables (as needed)

Company-scoped: `ItemCategory`, `Brand` (optional in Phase 1).

---

# 6. Lot Architecture (Phase 3)

## 6.1 Lot = traceability identity

```csharp
public class Lot : CompanyScopedEntity
{
    public long ItemVariantId { get; set; }
    public string LotNo { get; set; } = null!;

    /// <summary>Deferred supplier master — no FK.</summary>
    public string? SupplierRef { get; set; }

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
    public string? Remarks { get; set; }
}
```

**Unique (LOCKED):**

```text
UNIQUE (CompanyId, ItemVariantId, LotNo)
```

Not `(CompanyId, BranchId, LotNo)`.

## 6.2 LotBalance

```csharp
public class LotBalance : BranchScopedEntity
{
    public long LotId { get; set; }
    public long WarehouseId { get; set; }
    public long? LocationId { get; set; }
    public decimal QtyOnHand { get; set; }
}
```

Unique: `(CompanyId, BranchId, LotId, WarehouseId, LocationId)` (use sentinel/filtered unique for null Location if needed).

## 6.3 When a new Lot is created

- OB / GRN receipt (when item is batch-controlled)
- Explicit lot split / transformation (deferred)

**Not** created by normal warehouse transfer — transfer preserves Lot identity.

## 6.4 Terminology

Internal model = `Lot`. UI may label as "Batch No". Do not maintain separate Batch entity.

---

# 7. Inventory Documents

## 7.1 Document header

```csharp
public class InventoryDocument : BranchScopedEntity
{
    public string DocNo { get; set; } = null!;
    public DocumentType DocType { get; set; }
    public DateTime DocDate { get; set; }   // business date

    // Primary warehouse (OB/GRN/GI/SA). For ST see transfer fields.
    public long? WarehouseId { get; set; }

    // Transfer (ST) — required when DocType == ST
    public long? SourceWarehouseId { get; set; }
    public long? DestinationWarehouseId { get; set; }
    public long? SourceLocationId { get; set; }
    public long? DestinationLocationId { get; set; }

    public string? ReferenceNo { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.DRAFT;
    public string? Remarks { get; set; }

    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    public long? ReversalOfDocumentId { get; set; }
    public long? StockTakeId { get; set; }  // when SA generated from stock take

    public ICollection<InventoryDocumentLine> Lines { get; set; } = [];
}
```

## 7.2 Document line

```csharp
public class InventoryDocumentLine : BranchScopedEntity
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
    public string? LotNo { get; set; }       // input; resolved to LotId on post
    public long? LotId { get; set; }

    public long? ReasonCodeId { get; set; }
    public string? Remarks { get; set; }

    public ICollection<StockMovementAllocation> Allocations { get; set; } = [];
}
```

## 7.3 Document types (V1)

```csharp
public enum DocumentType
{
    OB = 1,   // Opening Balance
    GRN = 2,  // Goods Received Note
    GI = 3,   // Goods Issue
    ST = 4,   // Stock Transfer (same branch)
    SA = 5,   // Stock Adjustment (posted variance)
}
```

Stock Take is a **worksheet entity**, not a ledger document type (see §11).

## 7.4 Document status

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

Transitions:

```text
DRAFT → CANCELLED | SUBMITTED
SUBMITTED → CANCELLED | APPROVED
APPROVED → CANCELLED | POSTED
POSTED → REVERSED
```

Posted documents cannot be edited.

---

# 8. Opening Balance (OB)

First-class document for go-live / migration.

Must support per line:

- ItemVariant, Warehouse, Location (optional), Qty, Unit Cost, LotNo (if batch item), value

Posting:

- Creates StockLedger IN movements
- Updates StockBalance
- Initializes/updates ItemCost (MAV)
- Creates Lot + LotBalance when batch-controlled

Example: `OB26080001`.

---

# 9. Stock Transfer (ST)

## 9.1 Required fields

```text
SourceWarehouseId
DestinationWarehouseId
SourceLocationId? 
DestinationLocationId?
```

Validation:

- Source ≠ Destination (warehouse or location must differ)
- Both warehouses belong to **same Branch** (V1)
- Same Company

## 9.2 Inter-branch policy (V1 LOCKED)

**Same branch only.** Cross-branch transfer is deferred.

## 9.3 Posting

Atomic in one transaction:

```text
OUT from Source WH/Loc  +  IN to Destination WH/Loc
```

Same `LotId` preserved. Cost: transfer at current MAV (no gain/loss in V1). Two (or more) StockLedger rows per allocation.

---

# 10. Stock Adjustment (SA)

Always capture:

```text
ReasonCode, OldQuantity (system), AdjustedQuantity (or difference),
UnitCost, Amount, User, DocDate, Document
```

No unexplained quantity changes.

SA may be created manually (with permission) or generated from approved Stock Take.

---

# 11. Stock Take Workflow (BLOCKER — DONE)

Stock Take is **not** a direct stock change.

```text
StockTake (worksheet)
    → Count lines (SystemQty vs CountedQty)
    → Variance
    → Approval
    → Generate SA document
    → PostingEngine
    → StockLedger
```

```csharp
public class StockTake : BranchScopedEntity
{
    public string StockTakeNo { get; set; } = null!;
    public DateTime CountDate { get; set; }
    public long WarehouseId { get; set; }
    public StockTakeStatus Status { get; set; }
    public long? GeneratedAdjustmentDocumentId { get; set; }
    public ICollection<StockTakeLine> Lines { get; set; } = [];
}

public class StockTakeLine : BranchScopedEntity
{
    public long StockTakeId { get; set; }
    public int LineNo { get; set; }
    public long ItemVariantId { get; set; }
    public long? LocationId { get; set; }
    public long? LotId { get; set; }
    public decimal SystemQty { get; set; }
    public decimal CountedQty { get; set; }
    public decimal VarianceQty { get; set; }
    public long? ReasonCodeId { get; set; }
}

public enum StockTakeStatus
{
    DRAFT,
    COUNTING,
    PENDING_APPROVAL,
    APPROVED,
    ADJUSTMENT_GENERATED,
    CANCELLED
}
```

Example:

```text
System Qty = 100
Physical  = 92
Variance  = -8
→ SA line -8 @ current MAV
```

---

# 12. StockMovementAllocation

```csharp
public class StockMovementAllocation : BranchScopedEntity
{
    public long DocumentLineId { get; set; }
    public long StockLedgerId { get; set; }

    public long? SourceLotId { get; set; }
    public long? TargetLotId { get; set; }

    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }
}
```

Responsibility:

- Line: “issue 100”
- Allocation: “40 from Lot A, 60 from Lot B”
- Ledger: immutable movements

---

# 13. StockLedger (IMMUTABLE)

```csharp
public class StockLedger : BranchScopedEntity
{
    public DateTime TransactionDate { get; set; }  // business date (= DocDate)
    public long LedgerSequence { get; set; }

    public DocumentType DocType { get; set; }
    public string DocNo { get; set; } = null!;
    public int LineNo { get; set; }
    public long DocumentId { get; set; }
    public long DocumentLineId { get; set; }

    public string? ReferenceNo { get; set; }
    public string? PostedBy { get; set; }
    public DateTime PostedAtUtc { get; set; }

    public long ItemVariantId { get; set; }
    public string SKU { get; set; } = null!;
    public string ItemDescription { get; set; } = null!;

    public long UOMId { get; set; }
    public string UOMCode { get; set; } = null!;
    public decimal UnitQty { get; set; }
    public decimal ConversionRateUsed { get; set; }
    public decimal QtyInBase { get; set; }
    public decimal QtyOutBase { get; set; }

    public long WarehouseId { get; set; }
    public string WarehouseCode { get; set; } = null!;
    public long? LocationId { get; set; }
    public string? LocationCode { get; set; }

    public long? LotId { get; set; }
    public string? LotNo { get; set; }

    public long? ReasonCodeId { get; set; }
    public string? ReasonCodeName { get; set; }

    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "MYR";
    public decimal ExchangeRate { get; set; } = 1m;
    public decimal BaseAmount { get; set; }

    public CostingMethod CostingMethod { get; set; }
}
```

Rules:

- INSERT only after posting
- Never UPDATE / DELETE after posting
- No stored `RunningQty` / `RunningValue` — compute via window functions
- Ordering: `TransactionDate, LedgerSequence, Id`

---

# 14. StockBalance

```csharp
public class StockBalance : BranchScopedEntity
{
    public long WarehouseId { get; set; }
    public long ItemVariantId { get; set; }

    public decimal QtyOnHand { get; set; }
    public decimal ReservedQty { get; set; } // always 0 in V1 (reservation deferred)

    /// <summary>Mirrored from ItemCost for UI convenience. ItemCost is costing source of truth.</summary>
    public decimal AverageCost { get; set; }

    public DateTime LastUpdatedAtUtc { get; set; }

    public decimal AvailableQty => QtyOnHand - ReservedQty;
}
```

Unique: `(CompanyId, BranchId, WarehouseId, ItemVariantId)`.

Optional Phase 2+: `StockLocationBalance` if location-level current stock is required.

---

# 15. ItemCost — Moving Average State (BLOCKER — DONE)

## 15.1 Ownership

`ItemCost` is the **source of truth for current Moving Average** at grain:

```text
CompanyId + BranchId + WarehouseId + ItemVariantId
```

(Same grain as `StockBalance`.)

```csharp
public class ItemCost : BranchScopedEntity
{
    public long WarehouseId { get; set; }
    public long ItemVariantId { get; set; }

    public decimal QtyOnHand { get; set; }      // must match StockBalance after post
    public decimal AverageCost { get; set; }
    public decimal TotalValue { get; set; }     // QtyOnHand * AverageCost (stored)

    public DateTime LastUpdatedAtUtc { get; set; }
    public long? LastDocumentId { get; set; }
}
```

Unique: `(CompanyId, BranchId, WarehouseId, ItemVariantId)`.

## 15.2 Who updates / when

Only `PostingEngine` inside the posting transaction, after locks, after ledger insert.

On each post:

1. Lock `ItemCost` (+ `StockBalance`) rows
2. Compute new avg / qty / value
3. Update `ItemCost`
4. Mirror `AverageCost` + qty onto `StockBalance`
5. Write unit/total cost onto StockLedger rows (historical)

## 15.3 MAV formulas (LOCKED)

**Receipt / OB / positive SA / transfer IN:**

```text
NewQty = OldQty + InQty
NewValue = OldQty * OldAvg + InQty * InUnitCost
NewAvg = NewQty == 0 ? 0 : NewValue / NewQty
```

**Issue / negative SA / transfer OUT:**

```text
OutUnitCost = Current AverageCost
OutValue = OutQty * OutUnitCost
NewQty = OldQty - OutQty
NewValue = OldValue - OutValue
NewAvg = NewQty == 0 ? 0 : NewValue / NewQty
  (normally NewAvg unchanged on pure issue)
```

## 15.4 Historical cost rule

Posted StockLedger costs **never** change when later receipts change AverageCost.

```text
GRN 100 @ 10 → Avg 10
GRN 100 @ 14 → Avg 12
GI  50       → cost 12 on ledger
later GRN    → must not rewrite GI ledger cost
```

## 15.5 Zero / negative qty

Default: **block negative stock**. If qty would go below zero → fail post.  
If NewQty = 0 → AverageCost = 0, TotalValue = 0.

---

# 16. Posting Engine

```csharp
public interface IPostingEngine
{
    Task<DocumentDto> CreateDocumentAsync(CreateDocumentDto dto, CancellationToken ct = default);
    Task SubmitForApprovalAsync(long documentId, CancellationToken ct = default);
    Task ApproveAsync(long documentId, string approvedBy, CancellationToken ct = default);
    Task<PostingResultDto> PostAsync(long documentId, string postedBy, CancellationToken ct = default);
    Task CancelAsync(long documentId, CancellationToken ct = default);
    Task<DocumentDto> ReverseAsync(long documentId, string reversedBy, CancellationToken ct = default);
}
```

Focused collaborators (do not put all rules in one 2,000-line method):

```text
PostingEngine
 ├── DocumentPostingValidator
 ├── PeriodService
 ├── InventoryMovementEngine
 ├── AllocationEngine (Lot in Phase 3)
 ├── MovingAverageCosting
 ├── InventoryStateUpdater   (StockBalance + ItemCost + LotBalance)
 ├── StockLedgerWriter
 ├── LedgerSequenceService
 └── DocumentSequenceService
```

---

# 17. Posting Transaction + Insert Order (LOCKED)

```text
Begin Transaction
  1. Load document + lines (tracked)
  2. Validate status, company/branch, warehouses, items, UOM, period
  3. If already POSTED → return existing result (idempotent) and exit
  4. Acquire locks in deterministic order (§18)
  5. Validate quantities / negative-stock policy
  6. Calculate UOM conversions; persist ConversionRateUsed
  7. Allocate lots (Phase 3) / build movement plan
  8. Calculate costs (MAV via ItemCost)
  9. INSERT StockLedger rows
 10. INSERT StockMovementAllocation (StockLedgerId now known)
 11. UPDATE StockBalance
 12. UPDATE ItemCost
 13. UPDATE LotBalance (Phase 3)
 14. Mark document POSTED (PostedBy, PostedAtUtc)
 15. Commit
```

On any failure: **ROLLBACK EVERYTHING**.

One business posting → one DB transaction → prefer one final `SaveChanges` plus raw SQL for lock/sequence as needed.

---

# 18. Concurrency Strategy (LOCKED)

## 18.1 Lock hints

For affected current-state rows (`StockBalance`, `ItemCost`, `LotBalance`):

```sql
WITH (UPDLOCK, HOLDLOCK)
```

## 18.2 Lock order (must be identical for all post paths)

```text
CompanyId
→ BranchId
→ ItemVariantId
→ WarehouseId
→ LocationId
→ LotId
→ StockBalance
→ ItemCost
→ LotBalance
```

Never lock in a different order in another code path.

## 18.3 RowVersion

Optimistic secondary protection on operational entities. Does **not** replace UPDLOCK allocation strategy.

## 18.4 Scenario

```text
Available = 100
User A GI 80, User B GI 50  → exactly one may succeed if both would overdraw; never both.
```

---

# 19. Idempotent Posting

If `Document.Status == POSTED`, `PostAsync` returns existing posting result and creates **no** additional ledger rows.

Protect with:

- Status check inside transaction after lock/load
- Unique business constraints where applicable `(CompanyId, BranchId, DocType, DocNo)`

---

# 20. Reversal

```text
Posted Document → Reverse → Reversal Document → compensating StockLedger movements
```

Rules:

- Original document and ledger remain
- Reversal creates opposite qty/cost movements via normal posting logic
- `ReversalOfDocumentId` links documents
- A document may be reversed once
- Subject to period rules
- Requires `REVERSE` permission

---

# 21. Negative Stock Policy

Default: **BLOCK**.

Configurable later per item/warehouse if needed; V1 blocks.

---

# 22. Backdated Posting Policy

```text
Closed period                         → BLOCK
Open period with later posted txns    → BLOCK (V1)
Open period, no later txns            → ALLOW if validations pass
```

No silent rewrite of ledger history.

---

# 23. Document Numbering

## 23.1 Format (Malaysia-friendly)

```text
{Prefix}{yyMM}{running}
Example: GRN26080001
```

## 23.2 Entity

```csharp
public class DocumentSequence : BranchScopedEntity
{
    public string DocType { get; set; } = null!;
    public string Prefix { get; set; } = null!;
    public int YearMonth { get; set; }          // e.g. 2608
    public long CurrentNumber { get; set; }
    public int NumberLength { get; set; } = 4;
}
```

Unique: `(CompanyId, BranchId, DocType, YearMonth)`.

## 23.3 Atomic increment

```sql
UPDATE DocumentSequence
SET CurrentNumber = CurrentNumber + 1
OUTPUT INSERTED.CurrentNumber
WHERE CompanyId = @CompanyId
  AND BranchId = @BranchId
  AND DocType = @DocType
  AND YearMonth = @YearMonth;
```

Never `last + 1` in memory without DB atomicity. Prefer pre-seeded sequence rows per branch/doc type.

Same pattern for `LedgerSequence` allocation.

---

# 24. Date / Time Policy

| Field | Kind |
|---|---|
| `DocDate`, `TransactionDate`, `CountDate` | Business date in company timezone |
| `CreatedAtUtc`, `ModifiedAtUtc`, `PostedAtUtc`, `ApprovedAtUtc` | UTC technical |

Default timezone: `Asia/Kuala_Lumpur` from `Company.TimeZoneId`.

Do **not** use `DateTime.UtcNow.Date` as business DocDate.

---

# 25. Inventory Period + Month-End

```csharp
public class InventoryPeriod : CompanyScopedEntity
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

Unique: `(CompanyId, FiscalYear, FiscalMonth)`.

## Close rules

Never snapshot by copying today’s `StockBalance`.

Correct:

```text
StockLedger where TransactionDate <= period end
  → as-of qty/value
  → StockSnapshot
  → mark period closed
```

```csharp
public class StockSnapshot : BranchScopedEntity
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

Close is transactional and concurrency-protected (RowVersion / lock).

---

# 26. Reconciliation

```csharp
public interface IInventoryReconciliationService
{
    Task<IReadOnlyList<StockIntegrityIssue>> FindIssuesAsync(...);
    Task ReconcileStockBalanceAsync(...);
    Task ReconcileLotBalanceAsync(...);
    Task ReconcileItemCostAsync(...);
    Task RebuildOperationalBalancesAsync(...); // admin only + audit
}
```

Checks:

```text
SUM(Ledger QtyIn - QtyOut) == StockBalance.QtyOnHand
LotBalance vs ledger by lot/warehouse/location
ItemCost.QtyOnHand vs StockBalance
ItemCost.TotalValue ≈ Qty * AverageCost
No duplicate posts / invalid company-branch links
```

Repair = explicit admin operation with audit log.

---

# 27. Precision

```text
Quantity:      decimal(19,6)   (UI often 4 dp)
Unit cost:     decimal(19,6)
Amount/value:  decimal(19,4)
Exchange rate: decimal(19,10)
```

Configure explicitly in Fluent API — do not rely on EF defaults.

---

# 28. Database Strategy

Align with live ErpWeb:

```text
SQL scripts under scripts/
  +
EF Core Fluent configurations in ErpWeb.Model
```

Do **not** introduce EF Migrations for inventory alone unless the whole solution adopts migrations.

Delete policy:

| Data | Policy |
|---|---|
| Masters (Item, WH, …) | Soft delete |
| Posted Document / StockLedger / Allocations / ItemCost history via ledger | No physical delete |
| Lot | No delete; status change |

FK delete behavior for posted history: **Restrict**.

---

# 29. Required Unique Constraints

```text
Branch                 (CompanyId, BranchCode)
Item                   (CompanyId, ItemCode)
ItemVariant            (CompanyId, SKU)
Warehouse              (CompanyId, BranchId, WarehouseCode)
WarehouseLocation      (CompanyId, BranchId, WarehouseId, LocationCode)
StockBalance           (CompanyId, BranchId, WarehouseId, ItemVariantId)
ItemCost               (CompanyId, BranchId, WarehouseId, ItemVariantId)
Lot                    (CompanyId, ItemVariantId, LotNo)
LotBalance             (CompanyId, BranchId, LotId, WarehouseId, LocationId)
InventoryDocument      (CompanyId, BranchId, DocType, DocNo)
InventoryDocumentLine  (DocumentId, LineNo)
DocumentSequence       (CompanyId, BranchId, DocType, YearMonth)
InventoryPeriod        (CompanyId, FiscalYear, FiscalMonth)
StockSnapshot          (CompanyId, BranchId, PeriodId, WarehouseId, ItemVariantId)
StockTake              (CompanyId, BranchId, StockTakeNo)
```

---

# 30. Critical Indexes

StockLedger:

```text
(CompanyId, BranchId, ItemVariantId, WarehouseId, TransactionDate, LedgerSequence, Id)
(CompanyId, BranchId, LotId, TransactionDate, LedgerSequence, Id)
(CompanyId, BranchId, DocType, DocNo)
(CompanyId, BranchId, DocumentId)
(CompanyId, BranchId, WarehouseId, TransactionDate)
```

ItemCost / StockBalance:

```text
(CompanyId, BranchId, WarehouseId, ItemVariantId)  -- unique clustered or supporting
```

---

# 31. Security / RBAC

Use existing ErpWeb pattern — **no separate inventory auth framework**.

```text
MenuCodes + PermissionCodes + AccessRightService.CanAsync()
UI: MenuAuthorize / PermissionAuthorize
Core: re-check CanAsync on every mutating command
```

Minimum permissions used:

| Code | Use |
|---|---|
| ACCESS | Open screen |
| ADD / EDIT / DELETE | Draft maintenance |
| SUBMIT / APPROVE | Workflow |
| POST | Post document |
| REVERSE | Reverse posted |
| CLOSE / REOPEN | Period |
| VIEW_COST | See unit/total cost |

**VIEW_COST:** enforce in Core DTOs/services (mask or omit cost fields). Hiding a grid column is not enough.

---

# 32. Blazor UI Pattern

Follow Admin module:

- `@inherits PageBase`
- `[Authorize]` + `<MenuAuthorize>`
- `CommonDataGrid` / `CommonDataGridEx`
- Existing DevExpress theme
- Services via DI; `IDbContextFactory<AppDbContext>` for data access patterns already used

## Initial screens

```text
Inventory
├── Item
├── UOM
├── Warehouse
├── Location
├── Opening Balance
├── Goods Receipt (GRN)
├── Goods Issue (GI)
├── Stock Transfer (ST)
├── Stock Take
├── Stock Adjustment (SA)
├── Stock Card
├── Stock Balance
└── Inventory Period
```

Add menu entries to `menus.xml` + `MenuCodes` + seed grants (replace/extend `INVENTORY_DEMO` over time).

---

# 33. Application Commands / Queries

Commands:

```text
CreateInventoryDocument, Submit, Approve, Post, Cancel, Reverse
CreateStockTake, SubmitStockTake, ApproveStockTake, GenerateAdjustmentFromStockTake
CloseInventoryPeriod
```

Queries:

```text
GetStockBalance, GetStockCard, GetLotTrace, GetLotBalance,
GetInventoryValuation (as-of), GetInventoryPeriod, GetReconciliationReport
```

DTOs only — do not expose EF entities to UI.

---

# 34. Error Handling

Business results/exceptions (not raw SQL to UI):

```text
PeriodClosed, DocumentAlreadyPosted, InsufficientStock,
LotNotAvailable, InvalidWarehouse, InvalidBranch, InvalidCompany,
InvalidUOM, InvalidConversion, DuplicatePosting, DocumentAlreadyReversed,
CrossBranchTransferNotAllowed, ViewCostDenied
```

---

# 35. Implementation Phases

## Phase 0 — Foundation Bridge

1. `ICompanyContext` on `ICurrentUserService`
2. `Branch` entity + SQL script + Admin/Branch CRUD (or minimal API)
3. Inventory base entity classes
4. Confirm login / Company / RBAC unchanged

**Exit:** DEMO login works; Branch CRUD works; no Tenant/JWT.

## Phase 1 — Masters

Item, ItemVariant (default), UOM, UOMConversion, Warehouse, WarehouseLocation, ReasonCode.

**Exit:** Create item + warehouse + location.

## Phase 2 — Posting Spine

InventoryDocument/Line, DocumentSequence, LedgerSequence, PostingEngine, StockLedger, StockBalance, ItemCost, MAV.

Documents: **OB, GRN, GI, ST, SA**.

**Exit:** Post GRN/GI; stock card; stock balance; idempotent post; concurrency tests pass.

## Phase 3 — Lot / Batch

Lot, LotBalance, allocations, multi-lot issue, transfer preserves lot.

**Exit:** Multi-lot receipt/issue/transfer/history.

## Phase 4 — Period

InventoryPeriod, as-of valuation, StockSnapshot, ClosePeriodService.

**Exit:** July close viewed on August 10 is historically correct.

## Phase 5 — Hardening

Reversal, reconciliation, UOM history, VIEW_COST, full RBAC menus, audit, mandatory tests.

**Exit:** All mandatory tests green.

### Gate rule

One phase → human review → next phase.  
Do **not** implement the entire module in one AI pass.  
Do **not** redesign the locked engine during coding.

---

# 36. Mandatory Test Scenarios

## Basic

- OB +100 @ 10
- GRN +50 @ 14
- GI -20 (cost = current MAV)
- ST 10 WH-A → WH-B (same lot if batch)
- SA -5 with reason

## MAV

```text
GRN 100 @ 10 → Avg 10
GRN 100 @ 20 → Avg 15
GI 50 → ledger cost 15
```

Later receipt must not change GI ledger cost.

## Concurrent issue

Two GI against same balance cannot both overdraw.

## Duplicate post

Two `PostAsync` calls → one ledger set.

## Reversal

Original intact; compensating document; balances restored correctly.

## Month-end

July closes August 10; Aug 1–10 txns do not affect July snapshot.

## UOM history

Conversion changes after post; old ledger qty unchanged.

## Stock Take

Count variance → approved → SA → ledger.

## Transfer

Same Lot identity across warehouses; same-branch only; reject cross-branch.

## VIEW_COST

User without permission cannot receive cost fields from Core.

---

# 37. Performance Notes

Measure with production-like volumes before premature optimization.

Minimum future targets for stock card / as-of / close:

```text
1M+ StockLedger rows
```

Indexes in §30 required from day one for ledger queries.

---

# 38. Final Architecture Rules (MANDATORY)

1. Company is top business scope (existing ErpWeb Company).
2. Branch is operational inventory scope (new master).
3. Cookie claims → `ICurrentUserService` → `ICompanyContext`.
4. Query filters are not the only authorization mechanism.
5. Document → Line → Allocation → StockLedger.
6. StockLedger is immutable historical truth.
7. StockBalance is qty cache; ItemCost is MAV source of truth.
8. Lot is identity; LotBalance is location qty; transfer keeps Lot.
9. Lot unique key = `(CompanyId, ItemVariantId, LotNo)`.
10. V1 costing = Moving Average only.
11. Historical UOM/FX/cost on ledger never rewritten.
12. Month-end snapshots are as-of period end from ledger.
13. Reversal is compensating; no ledger UPDATE/DELETE.
14. Posting is atomic, locked in fixed order, and idempotent.
15. Document numbering is DB-atomic (`Prefix+yyMM+running`).
16. Inter-branch transfer blocked in V1.
17. Supplier on Lot = `SupplierRef` string (no missing FK).
18. Stock Take → SA → Ledger (never silent SA without audit path for counts).
19. OB is first-class for migration/go-live.
20. Use existing MenuCodes / PermissionCodes; VIEW_COST in Core.
21. SQL scripts + Fluent; Restrict delete on history.
22. Business dates ≠ UTC technical timestamps (KL default).
23. No inventory rule hidden only in UI code.
24. Implement Phase 0–5 only; defer FIFO/Serial/ASM/Outbox/GL.

---

# 39. Final Recommendation

Treat this **v3.1** document as the implementation contract.

| Layer | Status |
|---|---|
| Inventory engine (ledger, MAV, period, reversal) | **LOCKED** |
| ErpWeb sync (context, Branch, RBAC, UI, SQL scripts) | **SPECIFIED** |
| AI coding | **Allowed per phase after Phase exit criteria** |

```text
LOCK THE INVENTORY ENGINE
  + SYNC TO EXISTING ERPWEB
  + IMPLEMENT IN SMALL PHASES
  + TEST POSTING / CONCURRENCY / RECONCILIATION
```

**Next coding step:** Phase 0 only (`ICompanyContext` + `Branch`).
