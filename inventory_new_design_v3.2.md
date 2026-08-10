# BizEERP Inventory Module — Implementation Contract v3.2

> **Purpose:** FINAL implementation contract for the BizEERP Inventory Module on live ErpWeb.
>
> **Target stack:** .NET 10 + ASP.NET Core + EF Core + SQL Server + Blazor Server + DevExpress.
>
> **Status:** FINAL implementation contract. Supersedes `inventory_new_design_v3.1.md` and `inventory_new_design_v3_10-10.md`.
>
> **Phase 0:** APPROVED TO CODE  
> **Phases 1–5:** gated (each phase must pass human review before the next)  
> **Full-module AI build:** FORBIDDEN  
> **Review history only:** [`inventory_design_review_v3.2.md`](inventory_design_review_v3.2.md) — not for coding.
>
> **Principle:** Lock the inventory engine. Sync to existing ErpWeb. Implement one phase at a time. Do not redesign while coding.

---

# AI CODING AGENT — NO SCOPE EXPANSION

**MUST:**

- Use `inventory_new_design_v3.2.md` as the **sole** coding contract
- Implement **ONLY** the currently approved phase exit criteria
- Obey the 19 Locked Non-Negotiables and Final Clarifications below

**MUST NOT:**

- Redesign locked architecture
- Implement deferred features (FIFO, Serial, Reservation, ASM/DSM, Outbox, GL, Landed Cost, cross-branch transfer)
- Add “helpful” extras beyond the current phase
- Start Phase N+1 before Phase N passes human review
- Treat review documents as implementation specs

---

# Locked Non-Negotiables

1. Posted StockLedger rows are immutable.
2. Posted inventory documents cannot be edited.
3. Corrections use compensating reversal.
4. StockLedger is the historical source of truth.
5. StockBalance is the current operational cache.
6. V1 costing is Moving Average only.
7. Negative stock is prohibited.
8. Closed inventory periods cannot accept normal posting.
9. Transfer OUT and IN are atomic.
10. Transfer preserves source cost.
11. Transfer preserves LotId.
12. Phase 2 is non-lot only.
13. Phase 3 introduces lot-aware processing.
14. VIEW_COST is enforced in Core.
15. Posting is idempotent.
16. Deadlock retry is limited to 3 attempts.
17. Phase 2 allocation is one-to-one with ledger rows.
18. Each phase must pass review before the next phase begins.
19. The coding agent must not redesign locked architecture.

---

# Final Clarifications (LOCKED)

## 1. Deadlock = 3 total attempts (never “3 retries”)

```text
PostAsync deadlock handling:
  maxAttempts = 3   // TOTAL attempts, not retries-after-failure

  Attempt 1 = initial transaction
  Attempt 2 = retry after deadlock
  Attempt 3 = retry after deadlock

  If all 3 attempts fail with deadlock → DeadlockRetryExhausted

FORBIDDEN wording in contract/code: "3 retries"
REQUIRED wording: "3 total attempts" / maxAttempts = 3
```

## 2. Allocation 1:1 is a Phase 2-only invariant

```text
PHASE 2 ONLY:
  For every StockLedger row inserted, insert exactly one StockMovementAllocation
  with StockMovementAllocation.StockLedgerId = that ledger Id.
  SourceLotId / TargetLotId = null.
  ST line → 2 ledger rows → 2 allocations.

PHASE 3:
  One document line MAY create MANY StockLedger rows (multi-lot split)
  and thus MANY StockMovementAllocation rows.
  The Phase 2 one-to-one invariant does NOT apply in Phase 3.
```

## 3. Lot behaviour — batch vs non-batch

```text
Item.IsBatchItem = false  (non-batch)
  Phase 2 and Phase 3:
    LotNo / LotId on lines, ledger, allocations MUST be null
    No Lot / LotBalance rows created

Item.IsBatchItem = true   (batch)
  Phase 2:
    Posting REJECTED (LotNotAllowedInPhase)
    Lot fields if present → REJECTED
  Phase 3:
    LotNo / LotId REQUIRED on stock movements
    Lot created on inbound (OB/GRN) when new lot identity
    LotBalance updated on every lot movement
```

## 4. LotId preservation — Phase 3 transfers only

```text
Rule 11 applies to Phase 3 transfers of batch items:

  Transfer OUT ledger.LotId = X
  Transfer IN  ledger.LotId = X   // SAME identity
  No new Lot row is created for a normal warehouse transfer

Phase 2 transfers:
  LotId is always null (non-lot phase)
  Rule 11 has no LotId to preserve; cost preservation and atomic OUT+IN still apply
```

## 5. MAIN is automatic on Warehouse create

```text
Warehouse create service/command MUST, in the SAME database transaction:
  1. Insert Warehouse
  2. Insert WarehouseLocation:
       LocationCode = "MAIN"
       LocationName = "Main"
       same CompanyId, BranchId
       IsActive = true

UI must not be required to create MAIN separately.
If a document line omits LocationId, resolve to that warehouse's MAIN location.
Reject deactivating/deleting the last active location on a warehouse.
```

## 6. No scope expansion

See **AI CODING AGENT — NO SCOPE EXPANSION** at the top of this document.

---

# 0. Decision Checklist (ALL LOCKED)

## Architecture and policy (DONE)

- [x] `ItemCost` grain finalized (warehouse-level; not location-level)
- [x] `ItemCost` owns **costing state only** — no duplicated Qty
- [x] Relationship `ItemCost` ↔ `StockBalance` ↔ `LotBalance` defined
- [x] Phase 2 = **non-lot only**; Phase 3 adds lot-aware docs
- [x] Stock Take explicitly in **Phase 2**
- [x] Exact concurrency/locking implementation specified
- [x] Transfer cost behaviour locked
- [x] Negative stock policy locked (V1 = not allowed)
- [x] Backdated posting policy locked (Option A — restricted)

## Additional locked rules (DONE)

- [x] UOM precision / rounding / direction
- [x] Zero quantity rules
- [x] Zero-cost receipt rules
- [x] Stock Take state machine
- [x] Document status machines per type
- [x] Database unique / index requirements
- [x] Mandatory Phase 2 MAV / concurrency / idempotency tests
- [x] Posted documents are non-editable
- [x] Posting failure / idempotency / deadlock maxAttempts = 3
- [x] Phase 2-only allocation 1:1 invariant
- [x] Batch vs non-batch Lot behaviour
- [x] Phase 3 transfer LotId preservation
- [x] Automatic MAIN location on Warehouse create
- [x] AI no-scope-expansion instruction

---

# 1. Executive Architecture (LOCKED — DO NOT REDESIGN)

1. `InventoryDocument` = business transaction.
2. `InventoryDocumentLine` = requested business movement.
3. `StockMovementAllocation` = physical/cost allocation bridge. **Phase 2:** exactly one allocation per StockLedger row. **Phase 3:** one line may create many ledger/allocation rows (multi-lot).
4. `StockLedger` = **immutable** historical truth.
5. One line → zero, one, or many ledger rows (many allowed from Phase 3).
6. `StockBalance` = **operational quantity** cache only.
7. `ItemCost` = **current Moving Average cost state** only (no qty).
8. `Lot` = traceability identity (not a warehouse balance).
9. `LotBalance` = current lot qty by warehouse/location (Phase 3).
10. Historical / as-of valuation from ledger (or validated snapshots) — never copy today’s `StockBalance` as history.
11. Reversal = compensating transaction.
12. Posted ledger: never UPDATE / DELETE.
13. Posting = atomic + concurrency-safe + idempotent.
14. Closed periods block ordinary posting.
15. V1 backdating restricted (see §22).
16. Document numbering = DB-atomic.
17. Scope via `ICompanyContext` + RBAC + explicit validation.
18. V1 costing = **Moving Average only**.

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
StockLedger          ← historical truth (immutable)
        │
        ├──► StockBalance   ← qty only
        ├──► ItemCost       ← AverageCost / LastCost only
        └──► LotBalance     ← lot qty (Phase 3)
```

**Stock value (operational):**

```text
Value = StockBalance.QtyOnHand × ItemCost.AverageCost
```

(Computed in queries/services. Do not store the same qty on both tables.)

---

# 2. Scope

## 2.1 In scope (Phases 0–5)

- `ICompanyContext` bridge; Branch / Warehouse / Location
- Item, ItemVariant, UOM, conversion, ReasonCode
- Docs: OB, GRN, GI, ST, SA + Stock Take worksheet
- MAV via `ItemCost`; StockLedger; StockBalance
- Lot/Batch in Phase 3
- Period close, as-of, snapshot, reconciliation
- Menus, RBAC (`POST`, `REVERSE`, `VIEW_COST`, `APPROVE`, …)
- SQL scripts + EF Fluent
- Blazor Admin UI pattern

## 2.2 Deferred (do not implement in V1)

FIFO, Serial, Reservation, ASM/DSM, Outbox, GL, Landed cost, Cross-branch transfer, Inter-company transfer, SR/PR/MI/MR as first-class types.

---

# 3. ErpWeb Integration Bridge (LOCKED)

```text
Cookie Claims → ICurrentUserService → ICompanyContext → Inventory Services
```

```csharp
public interface ICompanyContext
{
    int CompanyId { get; }
    string CompanyCode { get; }
    long BranchId { get; }
    string BranchCode { get; }

    /// <summary>Legacy userlogin claim. NOT WarehouseLocation.</summary>
    string? LegacyLocationCode { get; }

    string TimeZoneId { get; }       // default Asia/Kuala_Lumpur
    string BaseCurrencyCode { get; } // default MYR
}
```

**Forbidden:** JWT-only-for-inventory, TenantId, rewriting Company/RBAC, trusting client CompanyId/BranchId.

Reuse existing `Company` (`int CompanyId`, `CompanyCode`). New inventory entities use `long Id`.

---

# 4. Organization Model (LOCKED)

```text
Company
  └── Branch
        ├── Warehouse
        │     └── WarehouseLocation   // bins share warehouse ItemCost
        └── Inventory transactions
```

`ICurrentUserService.LocationCode` ≠ inventory bin.

### Base classes

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

### Branch

```csharp
public class Branch : SoftDeletableCompanyEntity
{
    public string BranchCode { get; set; } = null!;
    public string BranchName { get; set; } = null!;
    public bool IsActive { get; set; } = true;
}
```

Unique: `(CompanyId, BranchCode)`. Seed `HQ` with company bootstrap.

Query filters on inventory entities only; not sole security boundary.

---

# 5. Quantity vs Cost vs Lot Grains (LOCKED)

| Table | Grain | Owns |
|---|---|---|
| **StockBalance** | Company + Branch + Warehouse + **Location** + ItemVariant | `QtyOnHand` (and `ReservedQty`=0 in V1) |
| **ItemCost** | Company + Branch + Warehouse + ItemVariant (**no Location**) | `AverageCost`, `LastCost`, timestamps |
| **LotBalance** (P3) | Company + Branch + Warehouse + Location + Lot | Lot `QtyOnHand` |

### Rules

1. **Bins share warehouse cost.** Locations A01/A02/A03 under WH-A all use the same `ItemCost` for that WH + ItemVariant.
2. **Do not store Qty on ItemCost.**
3. **Do not store AverageCost on StockBalance.** Join/query `ItemCost` when displaying cost (subject to `VIEW_COST`). Dual cost sources are forbidden in V1.
4. When lot tracking enabled (Phase 3):

```text
SUM(LotBalance.QtyOnHand for ItemVariant+WH+Loc)
  == StockBalance.QtyOnHand
```

5. Operational value:

```text
SUM(StockBalance.QtyOnHand) × ItemCost.AverageCost
  (aggregated to warehouse: sum location qtys × warehouse avg)
```

### Example

```text
WH-A: 100 @ cost state RM10
WH-B: 100 @ cost state RM15
→ Separate ItemCost rows; no company-wide blended average in V1.
```

---

# 6. Masters

## 6.1 Item / ItemVariant / UOM

```csharp
public class Item : SoftDeletableCompanyEntity
{
    public string ItemCode { get; set; } = null!;
    public string ItemDescription { get; set; } = null!;
    public long BaseUOMId { get; set; }
    public bool IsStockItem { get; set; } = true;
    public bool IsBatchItem { get; set; } // Phase 3; ignored for posting until P3
    public CostingMethod CostingMethod { get; set; } = CostingMethod.MOVING_AVG;
    public decimal MinStockQty { get; set; }
    public decimal MaxStockQty { get; set; }
    public decimal ReorderQty { get; set; }
    public string? TaxCode { get; set; } // SST placeholder; unused by inventory post V1
    public bool IsActive { get; set; } = true;
}

public enum CostingMethod { MOVING_AVG = 1 }

public class ItemVariant : SoftDeletableCompanyEntity
{
    public long ItemId { get; set; }
    public string SKU { get; set; } = null!;
    public string? Barcode { get; set; }
    public string? VariantDescription { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

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
    /// <summary>Multiply From qty by Rate to get To qty. Example: From=BOX, To=PCS, Rate=12.</summary>
    public decimal ConversionRate { get; set; }
}
```

Uniques: Item `(CompanyId, ItemCode)`; Variant `(CompanyId, SKU)`; UOM `(CompanyId, UOMCode)`.

## 6.2 UOM conversion / precision / rounding (LOCKED)

**Direction:** Rate always means `ToQty = FromQty × ConversionRate`.  
Example: 1 BOX = 12 PCS → From=BOX, To=PCS, Rate=12. Inverse uses `1/Rate` only when converting the other way via a defined row or computed reciprocal for the same pair.

**Precision:**

| Concern | Rule |
|---|---|
| Stored conversion rate | `decimal(19,10)` |
| Document line Qty (transaction UOM) | `decimal(19,6)`; UI display per UOM.DecimalPlaces (default 4) |
| QtyInBase / QtyOutBase on ledger | `decimal(19,6)` |
| Rounding at post | Round half-away-from-zero to 6 dp for base qty |
| Display | Round to UOM.DecimalPlaces for UI only; do not rewrite posted values |

**Historical:** persist `ConversionRateUsed`, `UnitQty`, `QtyInBase`/`QtyOutBase` on ledger at post. Never recompute history from current conversion table.

**Invalid:** ConversionRate ≤ 0 → reject.

## 6.3 Warehouse / Location / ReasonCode

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

public class ReasonCode : SoftDeletableCompanyEntity
{
    public string ReasonCodeValue { get; set; } = null!;
    public string ReasonName { get; set; } = null!;
    public string AppliesTo { get; set; } = null!; // SA, STK, GI, ...
    public bool IsActive { get; set; } = true;
}
```

Every Warehouse create **must** auto-create `WarehouseLocation` with `LocationCode = "MAIN"` and `LocationName = "Main"` in the **same database transaction** (see Final Clarification §5). UI must not be required to create MAIN separately. V1 document lines require `LocationId`; if UI omits location, resolve to that warehouse’s `MAIN` location. Reject deactivating/deleting the last active location on a warehouse.

---

# 7. ItemCost — Costing State Only (LOCKED)

```csharp
public class ItemCost : BranchScopedEntity
{
    public long WarehouseId { get; set; }
    public long ItemVariantId { get; set; }

    public decimal AverageCost { get; set; }
    public decimal LastCost { get; set; }   // last inbound unit cost that affected MAV

    public DateTime LastUpdatedAtUtc { get; set; }
    public long? LastDocumentId { get; set; }
}
```

Unique: `(CompanyId, BranchId, WarehouseId, ItemVariantId)`.

### Who updates / when

Only `PostingEngine`, inside posting transaction, after locks, after cost calculation for the movement.

### MAV formulas (LOCKED)

Let `Q` = sum of `StockBalance.QtyOnHand` for same Company+Branch+Warehouse+ItemVariant (all locations).  
Let `A` = `ItemCost.AverageCost`.  
Let `V = Q × A` (computed, not stored on ItemCost).

**Inbound** (OB, GRN, transfer IN, positive SA):

```text
NewQ = Q + InQty
NewV = Q * A + InQty * InUnitCost
NewA = NewQ == 0 ? 0 : NewV / NewQ
LastCost = InUnitCost
```

**Outbound** (GI, transfer OUT, negative SA / SA reducing stock):

```text
OutUnitCost = A                    // current warehouse average
OutValue    = OutQty * OutUnitCost
NewQ        = Q - OutQty
NewA        = NewQ == 0 ? 0 : A    // average unchanged on pure issue when qty remains
```

Historical ledger costs never change when later receipts change `A`.

---

# 8. StockBalance (LOCKED)

```csharp
public class StockBalance : BranchScopedEntity
{
    public long WarehouseId { get; set; }
    public long LocationId { get; set; }
    public long ItemVariantId { get; set; }

    public decimal QtyOnHand { get; set; }
    public decimal ReservedQty { get; set; } // always 0 until reservations exist

    public DateTime LastUpdatedAtUtc { get; set; }

    public decimal AvailableQty => QtyOnHand - ReservedQty;
}
```

Unique: `(CompanyId, BranchId, WarehouseId, LocationId, ItemVariantId)`.

No `AverageCost` column on StockBalance in V1.

---

# 9. Lot (Phase 3) (LOCKED)

### Batch vs non-batch (see Final Clarification §3)

| Item | Phase 2 | Phase 3 |
|---|---|---|
| `IsBatchItem = false` | LotNo/LotId must be null; no Lot/LotBalance | Same — LotNo/LotId must be null; no Lot/LotBalance |
| `IsBatchItem = true` | Posting rejected (`LotNotAllowedInPhase`) | LotNo/LotId required; Lot on inbound; LotBalance on every lot movement |

### Lot entity

```csharp
public class Lot : CompanyScopedEntity
{
    public long ItemVariantId { get; set; }
    public string LotNo { get; set; } = null!;
    public string? SupplierRef { get; set; } // no FK
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

public class LotBalance : BranchScopedEntity
{
    public long LotId { get; set; }
    public long WarehouseId { get; set; }
    public long LocationId { get; set; }
    public decimal QtyOnHand { get; set; }
}
```

Unique Lot: `(CompanyId, ItemVariantId, LotNo)`.  
Unique LotBalance: `(CompanyId, BranchId, LotId, WarehouseId, LocationId)`.

**Phase 3 batch transfers:** preserve the same LotId on OUT and IN (no new Lot for normal warehouse transfer). New Lot only on receipt/OB/transformation — not on transfer. Non-batch and Phase 2: LotId remains null.

---

# 10. Documents

## 10.1 Header

```csharp
public class InventoryDocument : BranchScopedEntity
{
    public string DocNo { get; set; } = null!;
    public DocumentType DocType { get; set; }
    public DateTime DocDate { get; set; }

    public long? WarehouseId { get; set; } // OB/GRN/GI/SA

    public long? SourceWarehouseId { get; set; }      // ST
    public long? DestinationWarehouseId { get; set; } // ST
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
    public long? StockTakeId { get; set; }

    public ICollection<InventoryDocumentLine> Lines { get; set; } = [];
}
```

## 10.2 Line

```csharp
public class InventoryDocumentLine : BranchScopedEntity
{
    public long DocumentId { get; set; }
    public int LineNo { get; set; }
    public long ItemVariantId { get; set; }
    public long UOMId { get; set; }

    public decimal Qty { get; set; }                 // always > 0
    public decimal ConversionRateUsed { get; set; }
    public decimal QtyInBase { get; set; }

    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }

    public long LocationId { get; set; }

    public string? LotNo { get; set; }  // Phase 3 batch only; Phase 2 / non-batch must be null
    public long? LotId { get; set; }

    public long? ReasonCodeId { get; set; }
    public string? Remarks { get; set; }
}
```

## 10.3 Document types (V1)

```csharp
public enum DocumentType
{
    OB = 1,
    GRN = 2,
    GI = 3,
    ST = 4,
    SA = 5
}
```

## 10.4 Document status enum

```csharp
public enum DocumentStatus
{
    DRAFT = 0,
    SUBMITTED = 1,
    APPROVED = 2,
    POSTED = 3,
    CANCELLED = 4,
    REVERSED = 5
}
```

### Per-type state machines (LOCKED)

**OB / GRN / GI / ST / SA:**

```text
DRAFT → CANCELLED
DRAFT → POSTED
DRAFT → SUBMITTED → APPROVED → POSTED
POSTED → REVERSED
```

V1 default policy: **DRAFT → POSTED** for users with `POST` (submit/approve not required for these docs).  
**Exception:** SA generated from Stock Take must come from an approved Stock Take (Stock Take requires `APPROVE`).  
No company toggle for approval in V1.

**Posted documents:** no edit of qty/item/warehouse/lot/cost/UOM. Correction = Reverse + new doc.

---

# 11. Line quantity and cost rules (LOCKED)

### Quantity

```text
Every movement line Qty MUST be > 0
Qty <= 0 → reject
Direction comes from DocType / movement side (IN vs OUT), never from negative qty
```

SA variance magnitude is stored as positive `Qty` with sign implied by adjustment direction field:

```csharp
// on SA line:
public AdjustmentDirection Direction { get; set; } // Increase | Decrease
```

Or generate SA lines with DocType SA and `AdjustmentDirection`. Do not allow `Qty = -8`.

### Zero-cost receipts (LOCKED)

| Scenario | Rule |
|---|---|
| OB with UnitCost = 0 | **Allowed** (migration/free stock) — requires `POST`; log warning in audit |
| GRN UnitCost = 0 | **Allowed only with** `AllowZeroCost` flag on document + `APPROVE` permission (or SYSTEM_ADMIN). Without flag → reject |
| ST | Cost from source MAV; not user-entered zero |
| GI | Cost from MAV; user does not set issue cost in V1 |
| SA increase at 0 | Same as GRN zero-cost rule |
| SA decrease | Uses current MAV |

Zero-cost inbound **does** affect MAV (pulls average down). That is intentional and must be controlled.

---

# 12. Opening Balance (OB)

First-class doc for go-live.

**Phase 2:** non-lot only. `IsBatchItem` items and any LotNo/LotId → `LotNotAllowedInPhase`.  
**Phase 3:** lot-enabled OB for batch items (LotNo/LotId required when `IsBatchItem`).

Fields: ItemVariant, Warehouse, Location, Qty>0, UnitCost≥0. Lot fields only in Phase 3 for batch items.

---

# 13. Stock Transfer (ST) + Cost (LOCKED)

Required: `SourceWarehouseId`, `DestinationWarehouseId`; locations required.  
V1: **same Branch only**. Cross-branch → `CrossBranchTransferNotAllowed`.

### Transfer costing (LOCKED)

```text
Transfer does NOT create economic gain/loss.

OUT unit cost = ItemCost.AverageCost at SOURCE warehouse (at post time)
IN  unit cost = SAME out unit cost

Source ItemCost updated as outbound MAV
Destination ItemCost updated as inbound MAV using that transfer unit cost
```

Example:

```text
WH-A Avg=10, transfer 40 → WH-B
Ledger OUT WH-A @ 10
Ledger IN  WH-B @ 10
WH-B new average = blend(existing WH-B, 40@10)
```

Atomic OUT+IN in one transaction.

### LotId on transfer (LOCKED)

- **Phase 2:** LotId always null; cost preservation and atomic OUT+IN still apply.
- **Phase 3 batch items:** Transfer OUT and IN use the **same** `LotId`. No new Lot row for a normal warehouse transfer (Final Clarification §4 / Non-Negotiable 11).
- **Phase 3 non-batch:** LotId remains null.

---

# 14. Stock Adjustment (SA)

Requires `ReasonCodeId`.  
Manual SA: users with ADD+POST.  
System SA from Stock Take: linked via `StockTakeId`; not freely editable after generation except cancel before post.

---

# 15. Stock Take (Phase 2) (LOCKED)

```text
StockTake worksheet → Count → Variance → Approve → Generate SA → Post SA → StockLedger
```

```csharp
public class StockTake : BranchScopedEntity
{
    public string StockTakeNo { get; set; } = null!;
    public DateTime CountDate { get; set; }
    public long WarehouseId { get; set; }
    public StockTakeStatus Status { get; set; }
    public long? GeneratedAdjustmentDocumentId { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public ICollection<StockTakeLine> Lines { get; set; } = [];
}

public class StockTakeLine : BranchScopedEntity
{
    public long StockTakeId { get; set; }
    public int LineNo { get; set; }
    public long ItemVariantId { get; set; }
    public long LocationId { get; set; }
    public long? LotId { get; set; } // Phase 3 only
    public decimal SystemQty { get; set; }
    public decimal CountedQty { get; set; }
    public decimal VarianceQty { get; set; }
    public long? ReasonCodeId { get; set; }
}

public enum StockTakeStatus
{
    DRAFT = 0,
    COUNTING = 1,
    COMPLETED = 2,          // counting finished by user
    PENDING_APPROVAL = 3,
    APPROVED = 4,           // frozen — no line edits
    ADJUSTMENT_GENERATED = 5,
    POSTED = 6,             // generated SA posted
    CANCELLED = 9
}
```

### Stock Take transitions (LOCKED)

```text
DRAFT → COUNTING → COMPLETED → PENDING_APPROVAL → APPROVED
APPROVED → ADJUSTMENT_GENERATED → POSTED
Any pre-APPROVED (except POSTED) → CANCELLED (with rules)
```

- `APPROVED` and later: **lines immutable**
- Generate SA requires `APPROVE` on Stock Take menu + creates SA in DRAFT/APPROVED then post with `POST`
- Approval uses existing `PermissionCodes.Approve`

Phase 2 Stock Take = non-lot only.

---

# 16. StockLedger (IMMUTABLE)

Same shape as v3.1: denormalized codes/names for audit, `QtyInBase`/`QtyOutBase`, costs, `LedgerSequence`, `DocumentId`/`DocumentLineId`.

No stored RunningQty/RunningValue.

Ordering: `TransactionDate, LedgerSequence, Id`.

---

# 17. StockMovementAllocation

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

**Phase 2-only invariant (Non-Negotiable 17):**

```text
For every StockLedger row inserted in Phase 2, insert exactly one StockMovementAllocation
with StockMovementAllocation.StockLedgerId = that ledger Id.
SourceLotId / TargetLotId = null.
ST line → 2 ledger rows → 2 allocations.
```

**Phase 3:** one document line may create many StockLedger rows (multi-lot) and many allocations. The Phase 2 one-to-one invariant does **not** apply.

**Insert order:** StockLedger first → then Allocation with `StockLedgerId`.

---

# 18. Posting Engine + Insert Order (LOCKED)

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

### Transaction steps

```text
BEGIN TRAN (READ COMMITTED)
 1. Load document + lines WITH UPDLOCK on document row
 2. If Status == POSTED → return existing result (idempotent), COMMIT/exit
 3. Validate company/branch, period, warehouses, locations, items, UOM, qty rules, zero-cost rules
 4. Phase 2: reject any lot fields / IsBatchItem stock movements
 5. Acquire inventory locks (§19)
 6. Validate available qty (negative stock policy)
 7. Compute conversions; set ConversionRateUsed / QtyInBase
 8. Compute costs (MAV / transfer rules)
 9. INSERT StockLedger
10. INSERT StockMovementAllocation
11. UPDATE/INSERT StockBalance
12. UPDATE/INSERT ItemCost
13. Phase 3: UPDATE/INSERT LotBalance / Lot
14. Set document POSTED + PostedBy/PostedAtUtc
15. COMMIT
```

Any failure → ROLLBACK. Prefer one business SaveChanges + raw SQL for sequences/locks.

### Failure / idempotency / deadlock (LOCKED)

| Situation | Behaviour |
|---|---|
| Client calls Post after success | Idempotent: Status POSTED → same result, no new ledger |
| Failure before COMMIT | No ledger; document remains prior status; safe to call Post again |
| Deadlock | Rollback; PostAsync retries within **maxAttempts = 3 total attempts** (Attempt 1 initial, Attempt 2–3 after deadlock). After 3 failed attempts → `DeadlockRetryExhausted`. Do **not** interpret as “3 retries”. |
| Double-click | Same as idempotent post |

---

# 19. Concurrency / Locking (LOCKED — exact)

### Isolation

```text
Transaction isolation: READ COMMITTED
Inventory allocation: UPDLOCK, HOLDLOCK on mutable state rows
```

### What is locked (mutable inventory state only)

**Do not** lock parent `Company` or `Branch` rows for ordinary posting.

Lock these rows, in this **exact order**:

```text
1. ItemVariantId   (sort ascending when multiple)
2. WarehouseId     (for ST: lock lower WarehouseId first, then higher — avoids deadlock)
3. LocationId      (ascending)
4. LotId           (Phase 3; ascending; nulls last)
5. StockBalance    rows (UPDLOCK, HOLDLOCK)
6. ItemCost        rows (UPDLOCK, HOLDLOCK)
7. LotBalance      rows (Phase 3; UPDLOCK, HOLDLOCK)
```

Also: `UPDLOCK` on the `InventoryDocument` row at start.

### Missing rows

If `StockBalance` / `ItemCost` missing for an inbound:

1. Hold locks on the key range / attempt insert
2. Insert zero row then lock, **or** use atomic upsert pattern under the same transaction
3. Concurrent first-receipts: unique constraint + retry once on duplicate key, then lock and continue

### Concurrent behaviours

| Scenario | Expected |
|---|---|
| Two GI against same WH/Loc/Item, total > available | One succeeds; other fails `InsufficientStock` |
| Two GRN same WH/Item | Both succeed sequentially; MAV applied in lock order |
| Duplicate Post same DocId | One posting only |
| ST WH-A→WH-B and ST WH-B→WH-A concurrent | WarehouseId ascending lock order prevents deadlock cycle |

### Example SQL shape

```sql
SELECT *
FROM StockBalance WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyId=@c AND BranchId=@b
  AND WarehouseId=@w AND LocationId=@l AND ItemVariantId=@i;

SELECT *
FROM ItemCost WITH (UPDLOCK, HOLDLOCK)
WHERE CompanyId=@c AND BranchId=@b
  AND WarehouseId=@w AND ItemVariantId=@i;
```

`RowVersion` = secondary optimistic check; not a substitute for UPDLOCK.

---

# 20. Negative Stock Policy (LOCKED)

```text
V1: AllowNegativeStock = false (hard-coded default)
```

`AvailableQty < required OutQty` → fail with `InsufficientStock`.  
No posting path may leave `StockBalance.QtyOnHand < 0`.

Future per-item/warehouse override is out of V1 scope.

---

# 21. Idempotent Posting + Reversal (LOCKED)

- `PostAsync` on POSTED doc → return existing result; no new ledger
- Unique `(CompanyId, BranchId, DocType, DocNo)`
- Reversal creates compensating document + opposite ledger via normal engine
- Original remains; one reversal per document; needs `REVERSE` + open period

---

# 22. Backdated Posting Policy (LOCKED — Option A)

```text
DocDate must fall in a currently OPEN InventoryPeriod for the company.
DocDate must be <= company business "today" (timezone Asia/Kuala_Lumpur) unless AllowFutureDate=false (default reject future).
If ANY posted StockLedger exists for same Company+Branch+Warehouse+ItemVariant
   with (TransactionDate, LedgerSequence) > proposed DocDate ordering
→ BLOCK with BackdatedPostingNotAllowed
```

No V1 cost recalculation / rebuild engine.  
Closed period → `PeriodClosed`.

This avoids rewriting MAV history.

---

# 23. Period / Snapshot / As-Of (LOCKED)

`InventoryPeriod` company-scoped; close computes as-of from StockLedger ≤ EndDate; writes `StockSnapshot`; never copy live StockBalance as historical truth.

Close is transactional + concurrency-safe on period row.

---

# 24. Document Numbering (LOCKED)

Format: `{Prefix}{yyMM}{running}` — example: `GRN26080001`.

```csharp
public class DocumentSequence : BranchScopedEntity
{
    public string DocType { get; set; } = null!;
    public string Prefix { get; set; } = null!;
    public int YearMonth { get; set; }
    public long CurrentNumber { get; set; }
    public int NumberLength { get; set; } = 4;
}
```

Unique: `(CompanyId, BranchId, DocType, YearMonth)`.

Atomic:

```sql
UPDATE DocumentSequence
SET CurrentNumber = CurrentNumber + 1
OUTPUT INSERTED.CurrentNumber
WHERE CompanyId=@c AND BranchId=@b AND DocType=@t AND YearMonth=@ym;
```

`DocNo` unique: `(CompanyId, BranchId, DocType, DocNo)`.

Separate atomic `LedgerSequence` allocator (company or branch scoped — use **Branch** scope).

---

# 25. Date / Time (LOCKED)

| Field | Kind |
|---|---|
| DocDate, TransactionDate, CountDate | Business date (company TZ) |
| CreatedAtUtc, ModifiedAtUtc, PostedAtUtc, ApprovedAtUtc | UTC |

Default TZ: `Asia/Kuala_Lumpur`.

---

# 26. Precision (LOCKED)

```text
Quantity:      decimal(19,6)
Unit cost:     decimal(19,6)
Amount:        decimal(19,4)
Exchange rate: decimal(19,10)
Conversion:    decimal(19,10)
```

Fluent API must set these explicitly.

---

# 27. Database Strategy + Constraints (LOCKED)

SQL scripts + EF Fluent. No EF Migrations for inventory alone.

### Uniques

```text
Branch                 (CompanyId, BranchCode)
Item                   (CompanyId, ItemCode)
ItemVariant            (CompanyId, SKU)
UOM                    (CompanyId, UOMCode)
Warehouse              (CompanyId, BranchId, WarehouseCode)
WarehouseLocation      (CompanyId, BranchId, WarehouseId, LocationCode)
StockBalance           (CompanyId, BranchId, WarehouseId, LocationId, ItemVariantId)
ItemCost               (CompanyId, BranchId, WarehouseId, ItemVariantId)
Lot                    (CompanyId, ItemVariantId, LotNo)
LotBalance             (CompanyId, BranchId, LotId, WarehouseId, LocationId)
InventoryDocument      (CompanyId, BranchId, DocType, DocNo)
InventoryDocumentLine  (DocumentId, LineNo)
DocumentSequence       (CompanyId, BranchId, DocType, YearMonth)
InventoryPeriod        (CompanyId, FiscalYear, FiscalMonth)
StockSnapshot          (CompanyId, BranchId, PeriodId, WarehouseId, ItemVariantId)
StockTake              (CompanyId, BranchId, StockTakeNo)
StockTakeLine          (StockTakeId, LineNo)
```

### Indexes (StockLedger)

```text
(CompanyId, BranchId, ItemVariantId, WarehouseId, TransactionDate, LedgerSequence, Id)
(CompanyId, BranchId, LotId, TransactionDate, LedgerSequence, Id)
(CompanyId, BranchId, DocType, DocNo)
(CompanyId, BranchId, DocumentId)
(CompanyId, BranchId, WarehouseId, TransactionDate)
```

Delete: masters soft-delete; posted history Restrict; no physical delete of ledger.

---

# 28. Reconciliation (LOCKED)

```csharp
public interface IInventoryReconciliationService
{
    Task<IReadOnlyList<StockIntegrityIssue>> FindIssuesAsync(...);
    Task RebuildOperationalBalancesAsync(...); // admin + audit only
}
```

Must prove:

```text
Ledger Σ(In-Out) by WH/Loc/Item == StockBalance.QtyOnHand
Phase 3: Σ LotBalance == StockBalance for lot items
ItemCost exists for every StockBalance group at WH/Item with Qty>0 (or after any post)
```

Value check: `Σ(Qty × AverageCost)` vs sum of ledger residual valuation method used by as-of service.

---

# 29. Security / UI (LOCKED)

Existing `MenuCodes` / `PermissionCodes` / `CanAsync`.  
`VIEW_COST` enforced in Core (omit/mask cost fields).

Screens: Item, UOM, Warehouse, Location, OB, GRN, GI, ST, Stock Take, SA, Stock Card, Stock Balance, Inventory Period.

Pattern: `PageBase`, `CommonDataGrid`, `MenuAuthorize`, DevExpress theme.

---

# 30. Implementation Phases (LOCKED)

## Phase 0 — Foundation

`ICompanyContext`, Branch CRUD, RBAC untouched.

**Exit:** DEMO login works; Branch works; no Tenant/JWT.

## Phase 1 — Masters

Item, ItemVariant, UOM, UOMConversion, Warehouse (**auto-creates MAIN location**), ReasonCode.

**Exit:** Create item + warehouse + location.

## Phase 2 — Core Inventory (**non-lot only**)

Document, Line, Sequence, StockLedger, StockBalance, ItemCost, PostingEngine, Allocation (1:1).

Documents: **OB, GRN, GI, ST, Stock Take, SA**.

Reject `IsBatchItem` movements and LotNo/LotId on lines.

**Exit:** All Phase 2 mandatory tests (§31) green.

## Phase 3 — Lot / Batch

Lot, LotBalance, multi-lot allocation (Phase 2 1:1 invariant does **not** apply); lot-aware OB/GRN/GI/ST/Stock Take for `IsBatchItem`; Phase 3 transfer preserves same LotId for batch items; Σ LotBalance = StockBalance.

**Exit:** Multi-lot RCV/ISSUE/transfer/history + reconcile lot totals.

## Phase 4 — Period

InventoryPeriod, close, as-of, snapshot, valuation.

**Exit:** July close viewed Aug 10 historically correct.

## Phase 5 — Hardening

Concurrency stress, reversal matrix, reconciliation, UOM history, VIEW_COST, audit, performance, recovery/retry docs.

**Exit:** Full mandatory suite green.

### Gate rule

One phase → review → next. No full-module AI pass. No engine redesign during coding. No “helpful” FIFO/Serial/ASM/Outbox.

---

# 31. Phase 2 Mandatory Tests (LOCKED)

### MAV receipt

```text
GRN 100 @ 10 → Qty 100, Avg 10
GRN 100 @ 14 → Qty 200, Avg 12
GI 50        → Issue amount 600; remaining Qty 150; Avg still 12
Later GRN must not change GI ledger cost
```

### Transfer cost

```text
WH-A Avg 10; transfer 40 to WH-B
OUT @ 10, IN @ 10; WH-B MAV blends with 40@10
```

### Concurrent issue

```text
Stock 100; A GI 80 and B GI 50 concurrent
→ Cannot both succeed if negative stock forbidden
```

### Duplicate post

```text
Post same document twice → one ledger set only
```

### Reversal

```text
GRN +100 then Reverse → net qty 0; originals retained
```

### Idempotent / retry

```text
Post succeeds; client retries → same result, no duplicate ledger
```

### Zero qty / negative qty

```text
Line Qty 0 or <0 → rejected
```

### Zero-cost GRN

```text
Without AllowZeroCost → rejected
With AllowZeroCost + approve → posts and affects MAV
```

### Negative stock

```text
Qty 10; GI 15 → InsufficientStock
```

### Backdate

```text
Later posted txn exists; earlier DocDate → BackdatedPostingNotAllowed
```

### Stock Take

```text
System 100, Count 92 → Approve → SA -8 → Post → ledger & balances correct; lines frozen after approve
```

### Same-branch ST

```text
Cross-branch ST → rejected
```

---

# 32. Error Codes (LOCKED)

```text
PeriodClosed
BackdatedPostingNotAllowed
DocumentAlreadyPosted
InsufficientStock
InvalidWarehouse
InvalidBranch
InvalidCompany
InvalidUOM
InvalidConversion
DuplicatePosting
DocumentAlreadyReversed
CrossBranchTransferNotAllowed
LotNotAllowedInPhase
ZeroQtyNotAllowed
ZeroCostNotAllowed
ViewCostDenied
StockTakeNotEditable
DocumentNotEditableWhenPosted
DeadlockRetryExhausted
```

---

# 33. Final Architecture Rules (MANDATORY)

1. StockLedger is historical truth; never UPDATE/DELETE after post.
2. StockBalance = qty only; ItemCost = AverageCost/LastCost only; no duplicated qty on ItemCost.
3. ItemCost grain = Company+Branch+Warehouse+ItemVariant; locations share warehouse cost.
4. LotBalance (P3) detail; SUM(LotBalance)=StockBalance for lot items.
5. Value = Qty × AverageCost (computed).
6. Phase 2 non-lot; Phase 3 lot; Stock Take in Phase 2.
7. Transfer cost = source MAV out = same cost in; no transfer profit.
8. Negative stock forbidden in V1.
9. Backdating restricted (Option A); no V1 cost rebuild.
10. Line Qty always > 0; direction by doc/movement type.
11. Zero-cost GRN controlled; OB zero-cost allowed with audit.
12. Lock only mutable inventory rows; fixed order; UPDLOCK,HOLDLOCK; READ COMMITTED.
13. Posted docs immutable; reverse to correct.
14. Cookie → ICurrentUserService → ICompanyContext; existing RBAC; VIEW_COST in Core.
15. SQL scripts + Fluent; uniques/indexes as specified.
16. Implement one phase at a time (Non-Negotiable 18).
17. Deadlock: maxAttempts = 3 total attempts (Non-Negotiable 16).
18. Phase 2 allocation is exactly one StockMovementAllocation per StockLedger row (Non-Negotiable 17).
19. Coding agent must not redesign locked architecture or expand scope (Non-Negotiables 19 + AI section).

---

# 34. Approval Status

| Item | Status |
|---|---|
| Inventory engine | **LOCKED** |
| v3.2 FINAL contract (19 non-negotiables + 6 clarifications) | **DONE** |
| Full AI “build entire inventory” | **FORBIDDEN** |
| Phase 0 implementation | **APPROVED TO START** |
| Coding input | **This file only** — not review docs |

```text
LOCK THE INVENTORY ENGINE
  + SYNC TO EXISTING ERPWEB
  + IMPLEMENT IN SMALL PHASES
  + TEST POSTING / CONCURRENCY / RECONCILIATION
  + NO SCOPE EXPANSION
```

**Next step:** Phase 0 (`ICompanyContext` + `Branch`) only.
