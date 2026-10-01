# Inventory Stock Integrity Hardening Plan
## `mokth/net10projectTemplate` — `production` Branch

**Scope:** Inventory stock integrity rules across Inventory, Sales stock-out, Purchase receiving/returns, Stock Count, and Production Material Issue.

**Goal:** Make `IvBalLoc` and `IvTrxHistory` trustworthy as the operational stock balance and posted movement history, while keeping the design practical for a Malaysian SME ERP rather than introducing SAP/WMS-level complexity.

**Implementation principle:** UI validation is helpful, but every critical stock rule must be enforced again in the authoritative posting transaction after the relevant rows are locked.

---

# 1. Executive Decision

Implement the hardening in three priority bands.

## P0 — Must complete before inventory/production is considered production-ready

1. Future-stock / as-of-date rule.
2. Backdated transaction versus later movement protection.
3. Rollback chronology protection.
4. Standard-UOM / base-quantity integrity.
5. One common stock movement date policy.
6. Central locked-row stock posting validation.
7. Legacy `IvBalLoc.TransDate` cleanup policy.

## P1 — Strongly recommended for normal ERP use

8. Expired-lot transaction policy.
9. Stock-status transaction matrix.
10. Lot identity consistency.
11. Source-document quantity ceilings.
12. Available stock versus physical on-hand.
13. Posted stock history immutability.

## P2 — Add after P0/P1 are stable

14. FEFO allocation for expiry-controlled stock.
15. Item-master mutation protection.
16. Stronger reconciliation/pre-close integrity checks.
17. Zero/obsolete balance cleanup policy.

---

# 2. Current Repo Findings Driving This Plan

The following are already present and should be preserved:

- negative-stock prevention in posting paths;
- inventory period-close guard via `IvPeriodCloseGuard`;
- company/branch scoping;
- active item/warehouse/location checks;
- row/database locking in critical posting paths;
- lot-control fields and `IvLot`;
- stock count workflow;
- reconciliation service;
- Sales shipment reservation/allocation logic;
- Sales shipment as-of-date eligibility;
- Production Material Issue lot/expiry validation;
- Goods Receipt PO quantity validation;
- posted/new document status controls.

Important gaps confirmed in the current `production` branch:

### 2.1 Future stock is not consistently blocked

Sales shipment already applies:

```text
IvBalLoc.TransDate <= document date
```

but MI/SC/VR/TR/ADJ, common balance lookup, stock count, and Production Material Issue candidate discovery are not consistently protected by the same rule.

### 2.2 `IvBalLoc.TransDate` can be moved backwards

Several posting paths eventually assign:

```csharp
bal.TransDate = batch.TrxDtTime;
```

Therefore posting an older transaction against an existing balance can rewind the balance chronology.

### 2.3 Rollback does not yet enforce reverse chronology

`IvInventoryPostingService` loads the original history and reverses the movement, but it does not first prove that no later movement exists on the affected stock slice.

### 2.4 Misc Receipt has a standard-UOM integrity gap

`IvMiscReceiptService` validates that the entered UOM exists, then writes the entered quantity into:

```text
ToStdQty
ToStdUom
```

without necessarily converting through `IvUomConversionService`.

That can corrupt `IvBalLoc.StdQty` semantics when the user enters BOX, CTN, KG, etc. instead of the item's standard UOM.

### 2.5 Item Master allows dangerous structural changes

`IvStockMasterService` currently maps updates directly to:

```text
StdUom
StockControl
LotControl
```

without first blocking changes when stock balance/history already exists.

---

# 3. Inventory Invariants

These invariants should become the contract for the whole ERP.

## INV-01 — `IvBalLoc` always stores standard/base quantity

For every balance:

```text
IvBalLoc.StdQty = quantity in IvStockMaster.StdUom
IvBalLoc.StdUom = IvStockMaster.StdUom
```

Document entry UOM may be BOX/CTN/KG/etc., but it must be converted before stock mutation.

Example:

```text
Item RM001
StdUom = PCS
1 BOX = 12 PCS

MR 10 BOX

Document:
EnteredQty = 10
EnteredUom = BOX
Conversion = 12
StdQty = 120
StdUom = PCS

IvBalLoc:
StdQty = 120
StdUom = PCS
```

Never allow one `IvBalLoc` row in BOX while another is in PCS for the same item.

---

## INV-02 — Positive stock must have a usable stock date

Target rule after data cleanup:

```text
IvBalLoc.StdQty > 0
=> IvBalLoc.TransDate IS NOT NULL
```

Do not silently treat an unknown stock date as valid forever.

Legacy rows with `TransDate = NULL` should be repaired before the strict rule is activated.

---

## INV-03 — No future stock

For a document date `D`, an existing stock balance used or modified by the transaction must satisfy:

```text
IvBalLoc.TransDate.Date <= D.Date
```

Same-day stock is allowed.

---

## INV-04 — No backdated mutation after a later posted movement

Before modifying an existing stock slice for a backdated transaction, prove that there is no posted stock movement on that slice with:

```text
IvTrxHistory.TrxDtTime.Date > documentDate.Date
```

If one exists, reject the transaction.

This avoids historical balance rewriting and removes the need for a retrospective stock rebuild engine.

---

## INV-05 — Rollback must follow reverse movement order

A posted batch may be rolled back only if none of its affected balance slices has a later posted movement.

Example:

```text
01-Oct GR +100
05-Oct MI -20
08-Oct TR -30
```

Allowed rollback order:

```text
08-Oct TR
05-Oct MI
01-Oct GR
```

Attempting to rollback the 01-Oct GR first must fail.

---

## INV-06 — No normal future-dated stock movement

For stock-driving transactions:

```text
TransactionDate > BusinessDate
=> reject
```

Applies to:

```text
GR MR CR MI SC VR TR ADJ IP SP
```

Planning dates such as PO required date or Work Order planned date are not stock movements and are outside this rule.

---

## INV-07 — Posted history is immutable

Once posted:

```text
IvTrxHistory
```

must not be edited in place.

Correction must be:

```text
Rollback / reversal
+
new corrected transaction
```

---

## INV-08 — Stock availability is not always physical on-hand

Define:

```text
PhysicalOnHand
ReservedSales
ReservedProduction
OtherDraftAllocation
AvailableToIssue
```

with:

```text
AvailableToIssue =
PhysicalOnHand
- ReservedSales
- ReservedProduction
- OtherBlockingAllocations
```

The first implementation may remain simple, but screens must stop assuming `IvBalLoc.StdQty` always equals freely available stock.

---

# 4. Phase 0 — Shared Policy Infrastructure

Create shared policy helpers under:

```text
ErpWeb.Core/Inventory/
```

Recommended files:

```text
IvStockDateRules.cs
IvStockStatusRules.cs
IvLotRules.cs
IvStockMovementRules.cs
```

Keep helpers small and deterministic.

## 4.1 `IvStockDateRules`

Suggested responsibilities:

```csharp
internal static class IvStockDateRules
{
    public static bool IsAvailableOn(
        DateTime? stockDate,
        DateTime documentDate);

    public static bool IsFutureMovementDate(
        DateTime documentDate,
        DateTime businessDate);

    public static string FutureStockMessage(...);
}
```

After legacy cleanup, use strict stock-date semantics:

```csharp
return stockDate.HasValue
    && stockDate.Value.Date <= documentDate.Date;
```

Do not permanently allow `NULL` positive stock as eligible.

## 4.2 Business-date source

Do not scatter `DateTime.Today` through transaction services.

Use the existing date/clock abstraction where available and add a small central validator if necessary:

```text
IvStockMovementRules.EnsureValidMovementDate(...)
```

Required rule:

```text
documentDate <= businessDate
```

plus the existing period-close rule.

---

# 5. Phase 1 — Future-Stock / As-Of-Date Protection

This phase implements the previously agreed future-stock fix.

## 5.1 Authoritative posting guard

Modify:

```text
ErpWeb.Core/Inventory/IvInventoryPostingService.cs
```

Add locked-row date validation to:

```text
PostInventoryMICoreAsync
PostInventoryTRAsync
PostInventoryADJCoreAsync
PostInventoryMRCoreAsync
```

Rules:

### MI / SC / VR / IP

After locking `FromBalLocId`:

```text
source.TransDate <= batch.TrxDtTime
```

otherwise fail.

### TR

Check:

```text
source.TransDate <= batch.TrxDtTime
```

and if the destination slice already exists:

```text
destination.TransDate <= batch.TrxDtTime
```

Do not allow a 05-Oct transfer to rewrite a destination balance last moved on 10-Oct.

### ADJ

For both positive and negative adjustment against an existing `IvBalLoc`:

```text
existing.TransDate <= adjustment date
```

### MR / CR / GR

For a newly created destination balance:

```text
allowed
```

For an existing destination balance:

```text
existing.TransDate <= receipt date
```

## 5.2 Repository final safeguard

Modify:

```text
ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs
```

Harden:

```text
DecreaseBalLocQtyAsync
IncreaseBalLocQtyAsync
```

The service layer should still provide the user-facing reason.

The repository should refuse to mutate a balance when the expected chronology condition is not true.

Do not depend on UI filtering as the final protection.

## 5.3 Date-aware balance picker

Modify the lookup path:

```text
IvBalLocPicker
  -> IvBalLocSearchPopup
  -> IvInventoryLookupService
  -> IvStockCommonRepository
```

Add:

```csharp
DateTime? AsOfDate
```

to balance-search requests and picker parameters.

Filter:

```text
TransDate IS NOT NULL
AND TransDate < AsOfDate.Date + 1 day
```

Pass the document date from:

```text
IvMiscIssue
IvScrap
IvVendorReturn
IvStockTransfer
IvStockAdjustment
```

Show `Stock Date` in the popup.

## 5.4 Production Material Issue

Modify:

```text
ErpWeb.Core/Production/ProductionMaterialAllocationService.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.Posting.cs
```

Candidate discovery must include:

```text
TransDate <= IssueDate
```

Posting must revalidate the locked balance before building/posting `IP`.

Keep the central `PostInventoryMICoreAsync` guard too.

This gives:

```text
candidate filter
-> production locked-row validation
-> inventory posting validation
-> repository safeguard
```

## 5.5 Stock Count

Modify:

```text
ErpWeb.Core/Inventory/IvStockCountService.cs
ErpWeb.Model/Repositories/Inventory/IvStockCommonRepository.cs
```

Generate the count sheet as of:

```text
CountDate
```

Exclude balances whose stock date is later than the count date.

Revalidate the locked balance during post before creating the ADJ batch.

---

# 6. Phase 2 — Backdated Transaction Protection

This is separate from future-stock filtering.

## 6.1 Add later-movement query

Extend:

```text
IIvStockPostingRepository
IvStockPostingRepository
```

with a query such as:

```csharp
Task<IReadOnlyList<IvLaterMovementHit>> FindLaterMovementsAsync(
    AppDbContext db,
    string companyCode,
    string branchCode,
    IReadOnlyCollection<int> balLocIds,
    DateTime documentDate,
    int? excludeBatchNo,
    CancellationToken cancellationToken);
```

Use `IvTrxHistory` and both:

```text
FromBalLocId
ToBalLocId
```

A movement is "later" when:

```text
TrxDtTime.Date > documentDate.Date
```

and it is posted/valid history.

## 6.2 Apply before existing-balance mutation

Use the later-movement guard in the central posting cores before quantity is changed.

Minimum coverage:

```text
MR
GR
CR
MI
SC
VR
TR
ADJ
IP
SP
```

If a later movement exists, fail with a clear message:

```text
Item RM001 / WH01 / LOT001 has a later posted stock movement
dated 10/10/2026. A transaction dated 05/10/2026 cannot modify
this stock balance.
```

## 6.3 Keep period close independent

Do not replace:

```text
IvPeriodCloseGuard
```

The rules are different:

```text
Period close
= accounting/operational period lock

Later-movement guard
= stock chronology protection
```

Both must pass.

---

# 7. Phase 3 — Rollback Chronology Protection

Modify:

```text
ErpWeb.Core/Inventory/IvInventoryPostingService.cs
```

Apply to every rollback core.

Before applying reversal quantities:

1. load the original batch;
2. load original `IvTrxHistory`;
3. collect all affected `FromBalLocId` / `ToBalLocId`;
4. lock affected balances in deterministic order;
5. check `IvTrxHistory` for later posted movement excluding the batch being rolled back;
6. reject if any later movement exists;
7. only then perform reversal.

Error example:

```text
Batch 123 cannot be rolled back because stock balance 456
has a later posted movement dated 08/10/2026.
Rollback the later transaction first.
```

Do not simply reset `IvBalLoc.TransDate` to the rolled-back batch date.

For the current ERP design, use the simpler and safer policy:

```text
rollback only the latest movement chain
```

rather than implementing historical re-cost/rebuild.

---

# 8. Phase 4 — Standard-UOM Integrity

This is a P0 fix.

## 8.1 Hard invariant

All `IvBalLoc` quantities must be in:

```text
IvStockMaster.StdUom
```

Every stock-driving document should retain entered UOM separately if required, but posting uses standard quantity.

## 8.2 Fix Misc Receipt first

Modify:

```text
ErpWeb.Core/Inventory/IvMiscReceiptService.cs
```

Current behavior allows an active UOM but can persist entered quantity directly as standard quantity.

New flow:

```text
Entered Qty/UOM
    |
    v
IvUomConversionService
    |
    v
StdQty + StdUom
    |
    v
IvTrxBatchDetail
    |
    v
IvBalLoc
```

If selected UOM equals `item.StdUom`:

```text
factor = 1
```

Otherwise require an active item-UOM conversion.

If none exists:

```text
block save/post
```

Do not guess conversion factors.

## 8.3 Review all stock-in entry points

Verify and normalize:

```text
IvGoodsReceiptService
IvMiscReceiptService
IvStockReturnService
Production finished-goods receipt path
any future stock opening/import path
```

`IvGoodsReceiptService` already derives standard quantity from PO pack size; ensure the resulting `ToStdUom` is the item standard UOM.

## 8.4 Review stock-out entry points

Stock-out must always subtract standard/base quantity.

Verify:

```text
MI
SC
VR
TR
IP
SP
ADJ decrease
```

No document UOM should reach `IvBalLoc.StdQty` without conversion.

## 8.5 Persist conversion evidence

Where the existing transaction schema permits, retain:

```text
EnteredQty
EnteredUom
StdQty
StdUom
ConversionFactorUsed
```

If adding a new conversion-rate column to the legacy `IvTrxBatchDetail` is too invasive now, at minimum ensure:

```text
FrStdQty / ToStdQty
FrStdUom / ToStdUom
```

are truly standard quantities/UOMs.

---

# 9. Phase 5 — Common Stock Movement Date Rule

Create one rule used by every stock-driving service.

Before saving/posting:

```text
TransactionDate <= BusinessDate
AND
TransactionDate is outside closed period
```

Apply to:

```text
GR
MR
CR
MI
SC
VR
TR
ADJ
IP
SP
Stock Count posting date
```

Planning/reference documents are not included.

Replace inconsistent direct `DateTime.Today` checks with the common date source.

---

# 10. Phase 6 — Expired-Lot Policy

Do not use one blanket "expired stock cannot move" rule.

Create transaction-specific rules.

## 10.1 Recommended matrix

| Transaction | Expired lot |
|---|---|
| Sales Shipment (`SP`) | Block |
| Issue to Production (`IP`) | Block |
| Misc Issue (`MI`) | Block by default |
| Transfer (`TR`) | Allow |
| Scrap (`SC`) | Allow |
| Vendor Return (`VR`) | Allow |
| Stock Adjustment (`ADJ`) | Allow |
| Stock Count | Allow |
| Customer Return (`CR`) | Allow receipt, retaining status/lot rules |
| Goods Receipt (`GR`) | Expiry must be valid relative to receipt date |
| Misc Receipt (`MR`) | Expiry must be valid relative to receipt date |

## 10.2 Sales shipment

Extend:

```text
ErpWeb.Core/Inventory/IvSpFifoEligibility.cs
IvSpShipmentService.cs
```

to validate lot activity/expiry for lot-controlled items.

Do not allocate an expired lot to customer shipment.

## 10.3 Receipt expiry comparison

Current receipt validation should not rely only on "today".

Preferred receipt rule:

```text
ExpiryDate >= TransactionDate
```

Example:

```text
GR date   01-Sep
Expiry    15-Sep
Today     01-Oct
```

A historical GR dated 01-Sep should not fail simply because today's date is later.

The backdated/later-movement rules separately determine whether the historical transaction may still be posted.

---

# 11. Phase 7 — Stock-Status Transaction Matrix

Create:

```text
IvStockStatusRules.cs
```

Do not treat "status exists and is active" as equivalent to "status is usable for every transaction".

Recommended initial behavior:

| Transaction | ACTIVE | QCHOLD | DAMAGED |
|---|---:|---:|---:|
| Sales Shipment | Yes | No | No |
| Production Issue | Yes | No | No |
| Misc Issue | Yes | No by default | No by default |
| Transfer | Yes | Yes | Yes |
| Scrap | Yes | Yes | Yes |
| Vendor Return | Yes | Yes | Yes |
| Stock Adjustment | Yes | Yes | Yes |
| Stock Count | Yes | Yes | Yes |

Keep this simple and central.

Later, the status master can carry flags such as:

```text
CanSell
CanIssueProduction
CanTransfer
```

only if customers need configurable behavior.

---

# 12. Phase 8 — Lot Identity Consistency

Use `IvLot` as the authoritative lot identity.

When receiving/reusing an existing:

```text
ItemCode + LotNo
```

do not silently overwrite critical lot metadata.

At minimum validate:

```text
ExpiryDate
MfgDate, when supplied
SupplierCode, when business policy requires it
```

Recommended first rule:

```text
same Item + same LotNo
=> ExpiryDate must match existing lot
```

If the existing lot has no expiry and the new receipt supplies one, allow filling it only through an explicit, controlled rule.

Do not silently change:

```text
31-Dec-2026
```

to:

```text
30-Jun-2027
```

for the same lot.

Implement the check centrally around:

```text
FindOrCreateLotAsync
```

in the stock posting repository/service path.

---

# 13. Phase 9 — Source-Document Quantity Ceilings

Treat this as a common downstream-document invariant.

Generic equation:

```text
AlreadyProcessed
+ CurrentDraft/PostQty
<= SourceQty + AllowedTolerance
```

Required flows:

## Purchase

```text
PO -> GR
GR -> Vendor Return
```

Goods Receipt PO quantity validation already exists; keep it under the same locked transaction.

Vendor Return must not exceed quantity legitimately received/available against the source relationship.

## Sales

```text
SO -> DO
DO/Sales -> Customer Return
```

Return quantity must not exceed eligible sold/delivered quantity when linked to a source document.

## Production

```text
Work Order BOM -> Issue to Production
```

Use:

```text
Required BOM Qty
+ configured tolerance
- already issued
```

as the remaining maximum.

Support multiple partial issues.

Always re-check under lock at posting.

---

# 14. Phase 10 — Available Stock / Reservation Model

Do not replace the existing Sales reservation logic immediately.

First introduce a common read model.

Suggested result:

```csharp
public sealed class IvAvailabilityResult
{
    public decimal PhysicalOnHand { get; init; }
    public decimal ReservedSales { get; init; }
    public decimal ReservedProduction { get; init; }
    public decimal OtherBlockingAllocation { get; init; }
    public decimal AvailableToIssue { get; init; }
}
```

Initially:

```text
ReservedSales
= existing NEW SP / shipment allocation logic

ReservedProduction
= NEW/PENDING Production Material Issue allocations, if/when production drafts reserve stock
```

Important design decision:

```text
Work Order BOM requirement alone is NOT a stock reservation.
```

Reserve stock only when the business explicitly allocates/reserves it.

This keeps the SME workflow simple.

---

# 15. Phase 11 — FEFO for Expiry-Controlled Stock

Production allocation already orders lot-controlled candidates by expiry before stock date.

Apply the same principle to Sales shipment.

Recommended order:

## Lot-controlled item with expiry

```text
ExpiryDate ASC
TransDate ASC
LotNo ASC
BalLocId ASC
```

## Non-expiry stock

```text
TransDate ASC
LotNo ASC
BalLocId ASC
```

This is FEFO for expiring stock and FIFO otherwise.

Manual lot override may still be allowed if the lot passes all eligibility rules.

---

# 16. Phase 12 — Item Master Structural-Change Protection

Modify:

```text
ErpWeb.Core/Inventory/IvStockMasterService.cs
```

Before updating an existing item, compare original and requested values.

Structural fields:

```text
StdUom
StockControl
LotControl
```

If any changed, check whether the item has:

```text
non-zero IvBalLoc
OR
posted IvTrxHistory
```

Recommended policy:

## `StdUom`

If any stock history or balance exists:

```text
block direct change
```

Message:

```text
Standard UOM cannot be changed because this item already has inventory history.
Use a controlled inventory conversion/migration process.
```

## `StockControl`

If history/balance exists:

```text
block direct true <-> false change
```

## `LotControl`

If history/balance exists:

```text
block direct true <-> false change
```

Lot semantics cannot be changed safely for existing history.

## 16.1 Deactivation

`SetActiveAsync` currently allows deactivation without checking live stock.

Recommended practical policy:

```text
on-hand > 0
=> block deactivation
```

or, if the business strongly prefers it:

```text
require explicit override permission + warning
```

Default should be block.

---

# 17. Phase 13 — Reconciliation Hardening

The current:

```text
IvInventoryReconciliationService
```

already detects:

```text
duplicate slices
orphan history
balance vs history mismatch
unexpected balance
stock-count/batch divergence
```

Keep it.

Enhance with:

## REC-01 — Positive stock missing date

```text
StdQty > 0 AND TransDate IS NULL
```

## REC-02 — Standard-UOM mismatch

```text
IvBalLoc.StdUom != IvStockMaster.StdUom
```

## REC-03 — Negative balance

Even though posting blocks this, detect legacy/corrupt rows:

```text
StdQty < 0
```

## REC-04 — Lot mismatch

For lot-controlled items:

```text
LotNo empty
LotId missing
inactive/missing IvLot
IvBalLoc lot data inconsistent with IvLot
```

## REC-05 — Non-lot item carrying lot data

Detect legacy corruption.

## REC-06 — Future stock date anomaly

Optionally report:

```text
TransDate > business date
```

for operational review.

## 17.1 Period close

Continue using reconciliation as a period-close precondition.

P0/P1 integrity errors should block period close.

Do not allow period close to silently snapshot known corrupt inventory.

---

# 18. Phase 14 — Legacy Data Preflight / Migration

Before activating strict rules, run a read-only data audit.

Check:

```sql
-- Positive stock with no stock date
StdQty > 0 AND TransDate IS NULL

-- Negative stock
StdQty < 0

-- Balance UOM differs from Stock Master StdUom

-- Duplicate stock slices

-- Lot-controlled stock without valid lot

-- Non-lot items carrying lot values

-- Balance/history mismatch

-- Future TransDate

-- Same Item/Lot with conflicting expiry metadata
```

## 18.1 `NULL TransDate` handling

Preferred repair order:

1. derive latest posted date from `IvTrxHistory` for the balance;
2. if no history exists but the balance is legitimate opening stock, require an explicit opening-stock date;
3. do not silently set everything to today.

After repair, enforce:

```text
positive stock => non-null TransDate
```

---

# 19. Phase 15 — Zero-Balance Lifecycle

Do not immediately delete every zero balance.

A zero `IvBalLoc` may still be referenced by history.

Recommended policy:

```text
keep zero balances by default
```

Hide them from normal on-hand lookup.

Optionally add a maintenance/archive process later for rows that:

```text
StdQty = 0
AND no active draft reservation
AND old enough
AND safe for historical references
```

Do not make physical deletion part of normal posting.

---

# 20. Files Expected to Change

## Core Inventory

```text
ErpWeb.Core/Inventory/IvInventoryPostingService.cs
ErpWeb.Core/Inventory/IvInventoryLookupService.cs
ErpWeb.Core/Inventory/IvInventoryReconciliationService.cs
ErpWeb.Core/Inventory/IvMiscIssueService.cs
ErpWeb.Core/Inventory/IvMiscReceiptService.cs
ErpWeb.Core/Inventory/IvGoodsReceiptService.cs
ErpWeb.Core/Inventory/IvStockReturnService.cs
ErpWeb.Core/Inventory/IvScrapService.cs
ErpWeb.Core/Inventory/IvVendorReturnService.cs
ErpWeb.Core/Inventory/IvStockTransferService.cs
ErpWeb.Core/Inventory/IvStockAdjustmentService.cs
ErpWeb.Core/Inventory/IvStockCountService.cs
ErpWeb.Core/Inventory/IvSpFifoEligibility.cs
ErpWeb.Core/Inventory/IvSpShipmentService.cs
ErpWeb.Core/Inventory/IvStockMasterService.cs
ErpWeb.Core/Inventory/IvUomConversionService.cs
```

## New shared rule files

```text
ErpWeb.Core/Inventory/IvStockDateRules.cs
ErpWeb.Core/Inventory/IvStockMovementRules.cs
ErpWeb.Core/Inventory/IvStockStatusRules.cs
ErpWeb.Core/Inventory/IvLotRules.cs
```

## Repositories

```text
ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs
ErpWeb.Model/Repositories/Inventory/IvStockCommonRepository.cs
ErpWeb.Model/Repositories/Inventory/IvStockHistoryRepository.cs
ErpWeb.Model/Repositories/Inventory/IvOnHandBalanceRow.cs
```

## Production

```text
ErpWeb.Core/Production/ProductionMaterialAllocationService.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.Posting.cs
```

## UI lookup

```text
ErpWeb.UI/Inventory/Lookups/IvBalLocPicker.razor
ErpWeb.UI/Inventory/Lookups/IvBalLocPicker.razor.cs
ErpWeb.UI/Inventory/Lookups/IvBalLocSearchPopup.razor
ErpWeb.UI/Inventory/Lookups/IvBalLocSearchPopup.razor.cs
```

## Inventory transaction UI

Date-aware balance selection/revalidation is expected in:

```text
IvMiscIssue
IvScrap
IvVendorReturn
IvStockTransfer
IvStockAdjustment
IvStockCount
```

---

# 21. Recommended Implementation Sequence

Do not implement all rules simultaneously.

## Sprint / Batch A — Core chronology

1. Add shared stock-date rules.
2. Repair positive `IvBalLoc` rows with null dates.
3. Add future-stock locked-row checks.
4. Add date-aware balance lookup.
5. Fix Production Material Issue candidate date filtering.
6. Fix Stock Count as-of filtering.
7. Add common future transaction-date rule.

**Exit condition:** no transaction can consume or rewrite stock from a later date.

## Sprint / Batch B — Historical integrity

8. Add later-movement repository query.
9. Block backdated mutation after later movement.
10. Block rollback when later movements exist.
11. Harden repository quantity updates.
12. Extend reconciliation for chronology errors.

**Exit condition:** posting/rollback can no longer reorder stock history.

## Sprint / Batch C — Quantity integrity

13. Fix Misc Receipt UOM conversion.
14. Audit every stock-in/out path for standard UOM.
15. Block StdUom/StockControl/LotControl master changes after stock history exists.
16. Add reconciliation check for UOM mismatches.

**Exit condition:** `IvBalLoc.StdQty` has one mathematical meaning everywhere.

## Sprint / Batch D — Lot/status integrity

17. Centralize lot-expiry rules.
18. Centralize stock-status rules.
19. Enforce lot identity consistency.
20. Add FEFO to Sales shipment.
21. Strengthen lot reconciliation.

**Exit condition:** lot-controlled stock is traceable and only eligible transactions can consume it.

## Sprint / Batch E — Availability / document ceilings

22. Complete source-document quantity limits.
23. Add common availability read model.
24. Integrate Sales reservation.
25. Integrate Production allocation/reservation only where explicitly reserved.

**Exit condition:** "available" stock is no longer confused with physical on-hand.

---

# 22. Manual Acceptance Matrix

No separate new unit-test project is required for this plan; use the existing automated suite as regression protection and perform the following transaction-level verification.

## Date / chronology

| Scenario | Expected |
|---|---|
| Stock 01-Oct, MI 05-Oct | Allow |
| Stock 05-Oct, MI 05-Oct | Allow |
| Stock 06-Oct, MI 05-Oct | Reject |
| Existing destination 10-Oct, TR 05-Oct | Reject |
| Existing balance 10-Oct, GR 05-Oct | Reject |
| Existing balance 10-Oct, ADJ 05-Oct | Reject |
| IP 05-Oct, lot stock date 06-Oct | Not selectable / reject |
| Count 05-Oct, stock date 10-Oct | Exclude / reject |
| Today 01-Oct, stock movement dated 02-Oct | Reject |

## Backdating

| Scenario | Expected |
|---|---|
| Last movement 10-Oct, new transaction 12-Oct | Allow |
| Last movement 10-Oct, new transaction 10-Oct | Allow if other rules pass |
| Last movement 10-Oct, new transaction 05-Oct | Reject |

## Rollback

| Scenario | Expected |
|---|---|
| 01-Oct GR, no later movement, rollback GR | Allow |
| 01-Oct GR, 05-Oct MI exists, rollback GR | Reject |
| Rollback 05-Oct MI first, then rollback 01-Oct GR | Allow |

## UOM

| Scenario | Expected |
|---|---|
| StdUom PCS, MR 10 PCS | StdQty 10 PCS |
| StdUom PCS, 1 BOX=12 PCS, MR 10 BOX | StdQty 120 PCS |
| StdUom PCS, MR BOX without conversion | Reject |
| Existing balance PCS receives another UOM | Convert; balance remains PCS |

## Lot / expiry

| Scenario | Expected |
|---|---|
| Expired lot -> Sales Shipment | Reject |
| Expired lot -> Production Issue | Reject |
| Expired lot -> Scrap | Allow |
| Expired lot -> Vendor Return | Allow |
| Existing LOT001 expiry 31-Dec, receipt LOT001 expiry 30-Jun | Reject conflict |
| FEFO lot B expires before A | Suggest B first |

## Status

| Scenario | Expected |
|---|---|
| QCHOLD -> Sales | Reject |
| DAMAGED -> Production Issue | Reject |
| DAMAGED -> Scrap | Allow |
| QCHOLD -> Transfer to quarantine warehouse/location | Allow |

## Master data

| Scenario | Expected |
|---|---|
| No history, change StdUom | Allow |
| Has posted history, change StdUom | Reject |
| Has stock/history, change LotControl | Reject |
| Has stock/history, change StockControl | Reject |
| On-hand > 0, deactivate item | Reject by default |

## Reconciliation

Must report:

```text
duplicate slice
orphan history
balance/history mismatch
positive stock without TransDate
negative balance
StdUom mismatch
invalid lot relationship
```

---

# 23. Error Message Standard

Use specific business messages.

### Future stock

```text
Stock lot LOT001 is dated 10/10/2026 and cannot be used for
transaction date 05/10/2026.
```

### Later movement

```text
This stock balance has a later posted movement dated 10/10/2026.
A transaction dated 05/10/2026 cannot modify it.
```

### Rollback order

```text
Batch 123 cannot be rolled back because this stock balance has a
later posted transaction. Roll back the later transaction first.
```

### UOM

```text
No active conversion exists from BOX to PCS for item RM001.
The transaction cannot be saved.
```

### Lot conflict

```text
Lot LOT001 already exists for item RM001 with expiry 31/12/2026.
The entered expiry 30/06/2027 does not match.
```

Do not return generic:

```text
Inventory error.
```

for integrity failures.

---

# 24. Transaction Coverage Checklist

Every new inventory-producing/consuming feature must be reviewed against this table.

| Rule | GR | MR | CR | MI | SC | VR | TR | ADJ | SP | IP | Count |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Transaction date valid | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Future stock blocked | existing dest | existing dest | existing dest | ✓ | ✓ | ✓ | source+dest | existing | ✓ | ✓ | ✓ |
| Later movement blocked | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Standard UOM | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Lot policy | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Status policy | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Source ceiling | PO | optional | sales source | — | — | GR/PO | — | — | SO/DO | WO BOM | — |
| Rollback chronology | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | generated ADJ |

---

# 25. Non-Goals

Do not add these during this hardening project unless a real customer requires them:

```text
full WMS wave picking
bin route optimization
RF warehouse task engine
advanced serial genealogy
multi-echelon ATP
SAP-style inventory revaluation/rebuild
complex quarantine workflow engine
advanced landed-cost allocation
consignment ownership layers
```

The objective is reliable SME ERP inventory, not warehouse-management-system scope.

---

# 26. Definition of Done

The hardening project is complete when:

1. `IvBalLoc.StdQty` always means standard/base quantity.
2. Positive balances have valid stock dates.
3. No transaction can consume future-dated stock.
4. No old transaction can rewrite a balance after a later posted movement.
5. Rollback follows reverse chronology.
6. No normal stock movement can be posted in the future.
7. Expired/blocked-status stock cannot be sold or issued to production.
8. Lots cannot silently change critical identity data.
9. Downstream documents cannot exceed legitimate source quantities.
10. Item structural inventory settings cannot be changed after live history exists.
11. Reconciliation can detect all key integrity violations.
12. Inventory, Sales and Production use the same core stock rules rather than separate copies.

---

# 27. Recommended First Coding Target

Start with **Batch A + Batch B**, not with FEFO or reservations.

The highest-value implementation path is:

```text
Shared stock-date policy
        ↓
central posting guards
        ↓
as-of stock picker
        ↓
Production IP + Stock Count
        ↓
later-movement guard
        ↓
rollback chronology guard
        ↓
repository defensive update
```

Immediately after that, implement the **standard-UOM fix**, starting with:

```text
IvMiscReceiptService
```

because quantity-unit inconsistency is capable of corrupting the stock balance even when date controls are perfect.

This order gives the strongest integrity improvement with the least architectural churn.
