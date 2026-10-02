# Inventory Stock Integrity Hardening Plan
## `mokth/net10projectTemplate` — `production` Branch

**Scope:** Inventory stock integrity rules across Inventory, Sales stock-out, Purchase receiving/returns, Stock Count, and Production Material Issue.

**Goal:** Make `IvBalLoc` and `IvTrxHistory` trustworthy as the operational stock balance and posted movement history, while keeping the design practical for a Malaysian SME ERP rather than introducing SAP/WMS-level complexity.

**Implementation principle:** UI validation is helpful, but every critical stock rule must be enforced again in the authoritative posting transaction after the relevant rows are locked.

---

# 1. Executive Decision

## 1.1 First ship (implement now)

Ship a focused chronology + quantity-meaning release. Do not implement the full P1/P2 catalogue in the same change set.

### Chronology

- Shared stock-date rules and one business-date source (`ICurrentDateService`), plus existing `IvPeriodCloseGuard`.
- Candidate / picker eligibility: `IvBalLoc.TransDate IS NOT NULL AND TransDate <= DocumentDate`.
- Central locked-row future-stock guards in posting cores.
- Date-aware balance picker; Stock Count as-of filter; Production Material Issue `TransDate` filter.
- NEW POST: reject only later **calendar-day** history (INV-04).
- ROLLBACK: same-day order by `(TrxDtTime.Date, IvTrxHistory.Id)` (INV-05); latest-only rollback.
- `GetLatestRemainingMovementAsync` + tracking / RowVersion-safe `SetBalLocTransDateAsync` (including zero-net BalLocs).

### Quantity / UOM

- Misc Receipt conversion via `IvUomConversionService` into item `StdUom`.
- Entered Qty/UOM in existing `ToPurQty` / `ToPurUom`; converted values in `ToStdQty` / `ToStdUom`.
- UnitPrice = price per **entered** UOM; document and list totals use entered qty × UnitPrice.
- Reload prefers Pur fields with Std fallback for legacy rows.
- History copies both Pur and Std pairs.
- PMI: allocate from current `IvBalLoc.StdQty`; revalidate `IvBalLoc.StdUom == WorkOrderMaterial.BaseUom` at candidate and post.
- Minimal item-master structural lock for `StdUom` / `StockControl` / `LotControl` (see §12).

### Cutover / integrity readiness

- Gate A / B / C cutover (see §14): hard P0s zero; opening-baseline MISMATCH classified; true mismatches zero; live DB constraint verify.
- Reconciliation additions: positive/null TransDate, negative qty, StdUom mismatch.
- Automated tests + manual UAT matrix.
- Repository race safeguard (not business-rule authority); period-close unchanged.

## 1.2 Deferred (do not expand first ship)

- Full expired-lot transaction matrix.
- Full stock-status transaction matrix.
- Sales FEFO.
- Advanced reservation / ATP / availability model.
- Source-document ceiling hardening beyond already implemented controls.
- New `EnteredQty` / `EnteredUom` schema columns.
- FG receipt redesign (path not implemented yet).
- Advanced item-master migration UX / override permission.
- Full lot-metadata conflict workflow at `FindOrCreateLotAsync` (P1 unless a first customer already requires lot traceability).
- Zero/obsolete balance physical deletion as normal posting behavior.

Keep the long-term invariants below as the contract; deferred items are follow-up hardening, not blockers for the chronology/UOM slice.

---

# 2. Current Repo Findings Driving This Plan

The following are already present and should be preserved:

- negative-stock prevention in posting paths;
- inventory period-close guard via `IvPeriodCloseGuard`;
- company/branch scoping;
- active item/warehouse/location checks;
- row/database locking in critical posting paths;
- lot-control fields and `IvLot`;
- stock count workflow (`MaxBackdateDays` operational window);
- reconciliation service (`DUPLICATE_SLICE`, `ORPHAN_HISTORY`, `MISMATCH`, `UNEXPECTED_BALANCE`, stock-count batch checks);
- Sales shipment reservation/allocation and `TransDate <= document date` eligibility;
- Production Material Issue lot/expiry validation and WO conversion factor;
- Goods Receipt PO pack-size → standard qty and quantity ceilings;
- posted/new document status controls;
- `InventoryAsOfStockService` (committed; currently used by PMI as issue allowance — **must change** in first ship).

Important gaps confirmed on the current `production` branch:

### 2.1 Future stock is not consistently blocked

Sales shipment already applies `IvBalLoc.TransDate <= document date`, but MI/SC/VR/TR/ADJ, common balance lookup, and stock-count candidate generation do not.

Production Material Issue currently uses `InventoryAsOfStockService.UsableBaseQty` (ledger reconstruction) rather than Sales-style `TransDate` eligibility. First ship replaces that with `TransDate <= IssueDate` and current `IvBalLoc.StdQty`.

### 2.2 `IvBalLoc.TransDate` can be moved backwards

Posting helpers assign `TransDate = batch.TrxDtTime` on every quantity change (`IncreaseBalLocQtyAsync` / `DecreaseBalLocQtyAsync` and stock-in paths). An older document can rewind FIFO position. Stock Count already documents this side effect under `IvStockCountLimits.MaxBackdateDays`.

### 2.3 Rollback does not enforce reverse chronology

`IvInventoryPostingService` reverses quantities without proving no later `IvTrxHistory` exists on the affected BalLoc. Rollback also stamps the rolled-back batch date via quantity helpers instead of repairing from remaining history.

### 2.4 Misc Receipt standard-UOM gap

`IvMiscReceiptService` validates an active UOM then writes entered quantity into `ToStdQty` / `ToStdUom` without `IvUomConversionService`. That can store BOX counts in a PCS balance.

Goods Receipt already converts via PO pack size; Production Issue via WO `ConversionFactorToBase`. Do **not** wrap those paths in `IvUomConversionService`.

### 2.5 Item Master allows structural changes after stock exists

`IvStockMasterService` maps updates directly to `StdUom`, `StockControl`, `LotControl` with no balance/history/draft gate. Deactivation also skips on-hand checks (deactivation workflow remains deferred; structural lock is first-ship).

### 2.6 Lot identity

`FindOrCreateLotAsync` returns an existing lot unchanged; it does **not** silently overwrite expiry. The gap is missing conflict rejection when incoming metadata disagrees. Lot conflict UX stays deferred (P1).

### 2.7 List TotalAmount uses standard qty × UnitPrice

`IvStockTransactionRepository` computes list `TotalAmount` / sort as `ToStdQty × UnitPrice` (or `FrStdQty` for MI). Once MR UnitPrice is per entered UOM, 10 BOX @ RM24 would wrongly show RM2,880 instead of RM240. First ship must fix this with the MR UOM change.

---

# 3. Inventory Invariants

## INV-01 — `IvBalLoc` always stores standard/base quantity

```text
IvBalLoc.StdQty = quantity in IvStockMaster.StdUom
IvBalLoc.StdUom = IvStockMaster.StdUom
```

Document entry UOM may be BOX/CTN/KG/etc., but posting mutates stock only after conversion. Never allow one BalLoc row in BOX while another is in PCS for the same item slice.

## INV-02 — Positive stock must have a usable stock date

```text
IvBalLoc.StdQty > 0  =>  IvBalLoc.TransDate IS NOT NULL
```

Activate only after Gate A cutover (legacy null dates repaired). Do not silently treat unknown stock date as valid forever.

## INV-03 — No future stock (candidate eligibility)

For document date `D`, an existing balance used or modified must satisfy:

```text
IvBalLoc.TransDate IS NOT NULL
AND IvBalLoc.TransDate.Date <= D.Date
```

Same-day stock is allowed. Applies to pickers, Stock Count sheet generation, PMI candidates, and locked-row posting revalidation.

## INV-04 — NEW POST: no later-day movement mutation

Before mutating an existing balance for a new post, reject when any remaining posted history on that BalLoc has:

```text
existingHistory.TrxDtTime.Date > documentDate.Date
```

Same-day existing history does **not** block the new posting. The new post appends after same-day movements.

Critical regression: Batch A posts 05-Oct, then Batch B also posts 05-Oct → Batch B **allowed**.

## INV-05 — ROLLBACK: reverse persisted movement order

Rollback is different because history Ids already exist. Order key:

```text
(TrxDtTime.Date, IvTrxHistory.Id)
```

For every BalLoc touched by the rollback batch, reject if a remaining history row has a key later than that batch’s **max** history Id on that balance.

`IvBalLoc.TransDate` is stock availability / latest movement **date**, not authoritative same-day sequence. Same-day rollback order comes from persisted `IvTrxHistory.Id`.

> `IvTrxHistory.Id` is used only as the persisted same-day tie-breaker for the same affected balance; the inventory lock order serializes competing mutations on that balance.

Document dates are stored at midnight on `IvTrxBatch.TrxDtTime`, so date-only cannot order same-day posts for rollback.

## INV-06 — No normal future-dated stock movement

```text
TransactionDate > BusinessDate  =>  reject
```

Plus existing period-close. Applies to GR, MR, CR, MI, SC, VR, TR, ADJ, IP, SP, Stock Count posting date. Planning dates (PO required date, WO planned date) are outside this rule.

## INV-07 — Posted history is immutable

Correction = rollback / reversal + new corrected transaction. No in-place edit of `IvTrxHistory`.

## INV-08 — Stock availability is not always physical on-hand

Long-term: distinguish PhysicalOnHand, ReservedSales, ReservedProduction, AvailableToIssue. **Deferred** from first ship except: do not treat Work Order BOM demand alone as a reservation; do not use `UsableBaseQty` reconstruction as issue allowance.

---

# 4. Eligibility vs Posting Authority

```text
Candidate / picker eligibility
  IvBalLoc.TransDate <= DocumentDate

Authoritative posting permission
  no later posted movement (NEW POST = later calendar day only)

Rollback authority
  latest persisted movement order (Date, History.Id)

InventoryAsOfStockService / UsableBaseQty
  NOT permission to allocate or post
```

### Slice vs BalLocId

Posting business rules reason by the 7-part stock slice (`CompanyCode`, `BranchCode`, `ICode`, `WhCode`, `LocCode`, `LotNo`, `IStatus`). Rollback history ordering/repair uses the persisted `BalLocId` referenced by history. Unique slice constraint `UQ_IvBalLoc_StockSlice` is a cutover prerequisite so these concepts cannot diverge.

### Service vs repository

- **Service / posting layer** = business authority after locks (clear user-facing errors).
- **Repository conditional UPDATE** = race safeguard only — not the sole chronology source.
- Prefer posting-specific guarded mutations, an explicit mutation mode/policy, or service-layer chronology + repository predicates for concurrent change. Do not make rollback depend on a predicate designed only for normal posting.
- Rollback TransDate repair uses tracking-safe `SetBalLocTransDateAsync`, not quantity helpers alone.

---

# 5. Phase 0 — Shared Policy Infrastructure

Create small deterministic helpers under `ErpWeb.Core/Inventory/`:

```text
IvStockDateRules.cs
IvStockMovementRules.cs
```

(Stock-status and lot-policy helpers remain deferred with P1 matrices.)

### `IvStockDateRules`

```csharp
internal static class IvStockDateRules
{
    public static bool IsAvailableOn(DateTime? stockDate, DateTime documentDate);
    public static bool IsFutureMovementDate(DateTime documentDate, DateTime businessDate);
    public static string FutureStockMessage(...);
}
```

Strict semantics after cutover:

```csharp
return stockDate.HasValue
    && stockDate.Value.Date <= documentDate.Date;
```

### Business-date source

Do not scatter `DateTime.Today` through transaction services. Use `ICurrentDateService` / existing clock abstraction:

```text
IvStockMovementRules.EnsureValidMovementDate(...)
documentDate <= businessDate
AND period open (IvPeriodCloseGuard)
```

---

# 6. Phase 1 — Future-Stock / As-Of-Date Protection

## 6.1 Authoritative posting guard

Modify `ErpWeb.Core/Inventory/IvInventoryPostingService.cs` locked-row validation for MI, TR, ADJ, MR (and stock-in destinations that already exist), covering SC/VR/IP via shared MI/ADJ cores where applicable.

Rules:

- **MI / SC / VR / IP:** after locking `FromBalLocId`, `source.TransDate <= batch.TrxDtTime` (date grain).
- **TR:** source and existing destination must satisfy the same.
- **ADJ:** existing BalLoc must satisfy the same.
- **MR / CR / GR:** new destination allowed; existing destination must satisfy `existing.TransDate <= receipt date`.

Also apply INV-04 later-day history check before quantity mutation.

## 6.2 Repository final safeguard

Harden `DecreaseBalLocQtyAsync` / `IncreaseBalLocQtyAsync` (or posting-specific variants) as race protection so a concurrent change cannot silently rewind chronology. Service layer remains the source of user-facing reasons.

## 6.3 Date-aware balance picker

Path: `IvBalLocPicker` → `IvBalLocSearchPopup` → `IvInventoryLookupService` → `IvStockCommonRepository`.

Add `DateTime? AsOfDate` to search requests. Filter:

```text
TransDate IS NOT NULL
AND TransDate < AsOfDate.Date + 1 day
```

Pass document date from MI, SC, VR, TR, ADJ. Show Stock Date in the popup.

## 6.4 Production Material Issue

**Current (must change):** allocation / draft / lifecycle / search use `UsableBaseQty` from `InventoryAsOfStockService`.

**First ship:**

```text
Candidate filter: TransDate IS NOT NULL AND TransDate <= IssueDate
Allocation qty:   current IvBalLoc.StdQty (after ACTIVE / expiry / other existing filters)
Posting:          INV-04 later-day guard + locked StdQty check
Also revalidate:  IvBalLoc.StdUom == WorkOrderMaterial.BaseUom
                  at candidate discovery and at post
```

Files:

- `ErpWeb.Core/Production/ProductionMaterialAllocationService.cs`
- `ErpWeb.Core/Production/ProductionMaterialIssueService.Draft.cs`
- `ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs`
- `ErpWeb.Core/Production/ProductionMaterialIssueService.Search.cs`
- (and posting path as needed)

Do not use ledger reconstruction as issue allowance. If `InventoryAsOfStockService` is retained, inject via DI (no `new InventoryAsOfStockService()`) and treat as diagnostic/read only. Keep central MI posting guard too.

## 6.5 Stock Count

Generate count sheet as of `CountDate`: exclude balances whose stock date is later than `CountDate`. Revalidate locked balance during post before creating the ADJ batch.

`MaxBackdateDays` remains an **extra** operational window. It does not replace chronology:

```text
CountDate must satisfy MaxBackdateDays
AND stock used by the count must be valid as of CountDate
AND posting must pass INV-04 later-day chronology
```

Under INV-03, a backdated count must not rewrite a pile whose `TransDate` is later than `CountDate`.

---

# 7. Phase 2 — Later-Day Movement Protection (NEW POST)

## 7.1 Query

Extend `IIvStockPostingRepository` / `IvStockPostingRepository`:

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

Use `IvTrxHistory` From/To BalLoc. For **NEW POST**, a movement is later when:

```text
TrxDtTime.Date > documentDate.Date
```

(and it is posted/valid history). Same-day history is not later for NEW POST.

## 7.2 Apply before existing-balance mutation

Minimum coverage: MR, GR, CR, MI, SC, VR, TR, ADJ, IP, SP.

Clear message example:

```text
Item RM001 / WH01 / LOT001 has a later posted stock movement
dated 10/10/2026. A transaction dated 05/10/2026 cannot modify
this stock balance.
```

Keep `IvPeriodCloseGuard` independent (period lock ≠ chronology).

---

# 8. Phase 3 — Rollback Chronology + TransDate Repair

## 8.1 Helpers

```text
GetLatestRemainingMovementAsync(db, company, branch, balLocId, excludeBatchNo)
  FromBalLocId == balLocId OR ToBalLocId == balLocId
  AND BatchNo != excludeBatchNo
  ORDER BY TrxDtTime DESC, Id DESC
  → latest remaining TrxDtTime (or null)
```

Query with `BatchNo != rollbackBatchNo` (or exclude target history Ids) **before** deleting history — EF still sees unmarked-for-delete rows until `SaveChanges`.

```text
SetBalLocTransDateAsync(db, balLocId, companyCode, branchCode, DateTime? transDate, ct)
  If IvBalLoc already tracked in this DbContext:
    set tracked.TransDate + ModifiedDate
    return 1   // persistence deferred to caller's SaveChanges
    NEVER bump RowVersion behind an already-modified tracked IvBalLoc
  Else:
    perform scoped direct update (+ ModifiedDate)
    return affected row count
  Identical intent on SQL Server and SQLite/tests
```

**Why a dedicated setter:** ADJ +10/−10 on one BalLoc can have net inverse qty = 0 while removing history still changes which movement is latest. Quantity helpers alone cannot guarantee TransDate repair; they also write `TransDate = batch.TrxDtTime` and can detach entities on the SQLite/test path.

## 8.2 Rollback sequence (one transaction)

1. Lock batch.
2. Load and validate authoritative history.
3. Build affected BalLoc set.
4. Lock affected balances in deterministic slice/id order.
5. Validate no later movement: later date **OR** same date + higher `History.Id` than the batch’s max Id on that BalLoc.
6. Compute latest remaining history date per BalLoc excluding rollback batch.
7. Compute all inverse quantity deltas.
8. Validate all quantity results.
9. Apply inverse quantities.
10. Repair TransDate for every affected BalLoc (including zero-net) via tracking-safe `SetBalLocTransDateAsync`.
11. Remove rollback-batch history.
12. Update batch rollback audit/status.
13. Save.
14. Commit.

No balance mutated before all chronology and quantity validations succeed.

## 8.3 Null after rollback

- Zero qty + null `TransDate` → fine.
- `StdQty > 0` + null `TransDate` → integrity / opening-balance condition: reconciliation Gate A P0; not eligible under strict candidate rule.

Error example:

```text
Batch 123 cannot be rolled back because this stock balance has a
later posted transaction. Roll back the later transaction first.
```

Policy: rollback only the latest movement chain — no historical re-cost/rebuild.

---

# 9. Phase 4 — Standard-UOM Integrity (Misc Receipt first)

## 9.1 Hard invariant

All `IvBalLoc` quantities in `IvStockMaster.StdUom`. Document entry UOM retained separately; posting uses standard quantity.

## 9.2 Misc Receipt dual Qty/UOM (no new columns)

Modify `ErpWeb.Core/Inventory/IvMiscReceiptService.cs`.

Flow:

```text
Entered Qty/UOM
    → IvUomConversionService
    → StdQty + StdUom
```

Persist using existing fields (document the MR convention in a code comment):

```text
ToPurQty / ToPurUom = user-entered Qty/UOM
ToStdQty / ToStdUom = converted item-standard Qty/UOM
IvBalLoc mutates only using ToStdQty / ToStdUom
UnitPrice = price per ENTERED UOM (same idea as GR purchase-UOM price)
LineAmount = entered Qty × UnitPrice
```

Example:

```text
Item RM001, StdUom = PCS, 1 BOX = 12 PCS
User: 10 BOX @ RM24 / BOX

ToPurQty = 10, ToPurUom = BOX
ToStdQty = 120, ToStdUom = PCS
UnitPrice = 24, LineAmount = 240
IvBalLoc += 120 PCS
```

Never interpret as 120 × 24.

If selected UOM equals `item.StdUom`, factor = 1. Otherwise require an active item-UOM conversion; if none, block save/post. Do not guess factors.

Reload:

```text
Quantity = ToPurQty ?? ToStdQty
Uom      = ToPurUom ?? ToStdUom
```

Legacy rows without `ToPur*` continue to open. Posting copies both Pur and Std pairs into `IvTrxHistory` (already has both column pairs).

**Explicit non-goal:** MR UOM hardening changes quantity representation and transaction audit only. It does **not** introduce a new costing/valuation algorithm or set BalLoc UnitPrice from the MR line.

## 9.3 List TotalAmount / sort (required with MR UOM)

`IvStockTransactionRepository` must stop using `ToStdQty × UnitPrice` for MR (and any dual-UOM trx that stores entered qty in `ToPur*`). Use:

```text
(ToPurQty ?? ToStdQty) × UnitPrice   // stock-in / MR
(FrPurQty ?? FrStdQty) × UnitPrice   // stock-out if FrPur* used
```

Legacy fallback when Pur fields are null. Document-level Amount in `IvMiscReceiptService` must match.

## 9.4 Other stock-in / stock-out paths

Audit only — do not re-convert:

- `IvGoodsReceiptService` (PO pack size / `PoOrderCalc`) — keep; ensure `ToStdUom` is item standard UOM.
- Production Issue (WO conversion snapshot) — keep.
- Outbound paths that already key off BalLoc / item StdUom — verify they never write entered UOM into `IvBalLoc.StdQty`.
- FG receipt posting does not exist yet — out of scope.

---

# 10. Phase 5 — Common Stock Movement Date Rule

Before save/post:

```text
TransactionDate <= BusinessDate
AND TransactionDate is outside closed period
```

Apply to GR, MR, CR, MI, SC, VR, TR, ADJ, IP, SP, Stock Count posting date. Replace inconsistent `DateTime.Today` defaults with `ICurrentDateService`.

---

# 11. Deferred Policy Matrices (P1+)

Documented for later; **not** first ship:

| Deferred topic | Notes |
|---|---|
| Expired-lot matrix | SP/IP block; TR/SC/VR/ADJ/Count allow; GR/MR expiry vs transaction date |
| Stock-status matrix | SP/IP ACTIVE only today; full trx×status matrix later |
| Lot identity conflict | Reject receipt whose expiry disagrees with existing `IvLot` |
| FEFO for Sales | Production already orders by expiry; Sales remains FIFO by TransDate |
| Availability / reservation | Common `IvAvailabilityResult`; WO BOM is not a reservation; MI drafts do not soft-reserve like SP |
| Source ceilings | Keep existing GR/PO, VR, SO→DO, WO BOM ceilings; harden later |
| EnteredQty schema | Not required; use `ToPur*` / `ToStd*` |
| Zero-balance lifecycle | Keep zero BalLoc by default; hide from on-hand lookup |

---

# 12. Phase 6 — Item Master Structural Lock (first ship)

Modify `ErpWeb.Core/Inventory/IvStockMasterService.cs`.

Before updating `StdUom`, `StockControl`, or `LotControl` on an existing item, block if **any** of:

1. Posted `IvTrxHistory` for the item exists.
2. **Any** `IvBalLoc` row exists for the item (including `StdQty = 0` — zero balances still carry UOM/slice semantics and history FKs).
3. Inventory `IvTrxBatch` / `IvTrxBatchDetail` exists for the item in **NEW** or **rolled-back** status (unposted drafts still encode UOM/lot-control assumptions).

Message example:

```text
Standard UOM cannot be changed because this item already has inventory history.
Use a controlled inventory conversion/migration process.
```

Advanced migration UX / override permission / deactivation-on-hand workflow remain deferred.

---

# 13. Phase 7 — Reconciliation Hardening

Keep existing diagnostics in `IvInventoryReconciliationService`. Add:

| Code | Check |
|---|---|
| REC-01 | `StdQty > 0 AND TransDate IS NULL` |
| REC-02 | `IvBalLoc.StdUom != IvStockMaster.StdUom` |
| REC-03 | `StdQty < 0` (legacy/corrupt) |

Continue using reconciliation as a period-close precondition. Do not allow period close to silently snapshot known corrupt inventory.

---

# 14. Phase 8 — Cutover Gates (opening-baseline aware)

Current reconciliation assumes `OpeningQty = 0` and is not a full production audit until opening baseline exists. Period Close already treats first-close `UNEXPECTED_BALANCE` + corresponding MISMATCH as opening baseline. Do **not** require blind “zero MISMATCH.”

## Gate A — structural / data corruption (must be zero)

- Negative `IvBalLoc`
- StdUom mismatch
- `DUPLICATE_SLICE`
- `ORPHAN_HISTORY`
- Positive stock + null `TransDate`

Verify live SQL Server has:

- `UQ_IvBalLoc_StockSlice`
- `CK_IvBalLoc_StdQty_NonNegative`

## Gate B — ledger / baseline

For every MISMATCH:

- If legitimate opening-baseline slice (positive BalLoc, no posted history → `UNEXPECTED_BALANCE` + OpeningQty=0 MISMATCH): establish/classify opening baseline first.
  - Preferred: explicit opening-stock baseline preserving qty, StdUom, stock/opening date, WH/loc/lot/status.
  - Or: first-close Period Close as baseline authority.
  - Or: ledger-only/dev DBs may require zero MISMATCH immediately.
- Else (history exists and BalLoc ≠ history/opening-adjusted total): **true** MISMATCH — block cutover.

After baseline: zero unresolved **true** MISMATCH.

## Gate C — chronology

- No positive stock with unknown date.
- Candidate as-of filters enabled.
- Posting later-day guards enabled.
- Rollback same-day ordering enabled.

## Deployment sequence

1. Read-only preflight report.
2. Resolve every `StdQty > 0 AND TransDate IS NULL` (prefer latest posted `IvTrxHistory.TrxDtTime`; no history → explicit opening date; never auto-stamp today).
3. Classify/establish opening baselines (Gate B).
4. Re-run reconciliation.
5. Require Gate A zero; Gate B true-MISMATCH zero.
6. Verify DB constraints.
7. Deploy strict candidate/posting rules (Gate C).

---

# 15. Files Expected to Change (first ship)

### Core Inventory

```text
ErpWeb.Core/Inventory/IvInventoryPostingService.cs
ErpWeb.Core/Inventory/IvInventoryLookupService.cs
ErpWeb.Core/Inventory/IvInventoryReconciliationService.cs
ErpWeb.Core/Inventory/IvMiscIssueService.cs
ErpWeb.Core/Inventory/IvMiscReceiptService.cs
ErpWeb.Core/Inventory/IvGoodsReceiptService.cs   (audit only unless gap found)
ErpWeb.Core/Inventory/IvScrapService.cs
ErpWeb.Core/Inventory/IvVendorReturnService.cs
ErpWeb.Core/Inventory/IvStockTransferService.cs
ErpWeb.Core/Inventory/IvStockAdjustmentService.cs
ErpWeb.Core/Inventory/IvStockCountService.cs
ErpWeb.Core/Inventory/IvStockMasterService.cs
ErpWeb.Core/Inventory/IvUomConversionService.cs  (consume; avoid double-convert elsewhere)
ErpWeb.Core/Inventory/IvStockDateRules.cs        (new)
ErpWeb.Core/Inventory/IvStockMovementRules.cs    (new)
```

### Repositories

```text
ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs
ErpWeb.Model/Repositories/Inventory/IvStockCommonRepository.cs
ErpWeb.Model/Repositories/Inventory/IvStockTransactionRepository.cs  (list TotalAmount / sort)
```

### Production

```text
ErpWeb.Core/Production/ProductionMaterialAllocationService.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.Draft.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.Lifecycle.cs
ErpWeb.Core/Production/ProductionMaterialIssueService.Search.cs
```

### UI lookup / date-aware selection

```text
ErpWeb.UI/Inventory/Lookups/IvBalLocPicker.razor
ErpWeb.UI/Inventory/Lookups/IvBalLocPicker.razor.cs
ErpWeb.UI/Inventory/Lookups/IvBalLocSearchPopup.razor
ErpWeb.UI/Inventory/Lookups/IvBalLocSearchPopup.razor.cs
```

Plus date-aware selection/revalidation in MI, SC, VR, TR, ADJ, Stock Count screens as needed.

### Tests

```text
ErpWeb.Tests/   (chronology, rollback, MR UOM/price/list, PMI eligibility, item-master lock, SQL Server concurrency)
```

---

# 16. Recommended Implementation Sequence

1. Shared stock-date / movement-date rules + business-date source.
2. Null-TransDate / Gate A–B preflight scripts and opening-baseline classification (cutover gate before strict picker).
3. Central posting future-stock + INV-04 later-day guards.
4. Date-aware balance lookup; Stock Count as-of; PMI TransDate + StdQty + StdUom==BaseUom (remove UsableBaseQty allocation).
5. INV-05 rollback chronology + `GetLatestRemainingMovementAsync` + tracking-safe `SetBalLocTransDateAsync`.
6. Repository race safeguards (posting-aware; do not break rollback).
7. Misc Receipt UOM / ToPur* / UnitPrice + list TotalAmount fix.
8. Expanded item-master structural lock.
9. Reconciliation REC-01/02/03 + Gate C enablement.
10. Automated tests + manual UAT.

**Exit condition (first ship):** no transaction can consume future-dated stock; same-day second post remains legal; backdating cannot rewrite after a later business day; rollback follows persisted history order and repairs TransDate; `IvBalLoc.StdQty` means standard UOM; MR audit preserves entered Qty/UOM and list totals; item master cannot invalidate UOM/control semantics after stock/draft/history exists.

---

# 17. Automated Tests + Manual UAT

Keep a transaction-level manual acceptance matrix. Also add `ErpWeb.Tests` coverage (including existing SQL Server concurrency area where appropriate).

| Area | Cases |
|---|---|
| Normal posting | stock date before/same → allow; after → reject; later-day history → reject backdate |
| Same-day post | A then B on 05-Oct → B **allowed** |
| Rollback order | same-day A then B → rollback A reject; B then A allow |
| TransDate repair | 01-Oct GR + 05-Oct MI, rollback MI → TransDate = 01-Oct |
| Zero-net repair | ADJ +10/−10 same BalLoc; rollback → TransDate repaired |
| RowVersion-safe repair | SQL Server: tracked BalLoc + qty rollback + TransDate repair → success, no concurrency exception |
| Opening baseline | BalLoc +100 no history → classified opening-baseline; chronology off until date set |
| True MISMATCH | BalLoc +100, history net +80 → cutover blocked |
| Null legacy | positive + null TransDate → Gate A P0; not eligible under strict candidate |
| PMI eligibility | TransDate after IssueDate excluded; allocation uses StdQty not UsableBaseQty |
| PMI UOM | BalLoc StdUom ≠ material BaseUom → reject at candidate/post |
| MR UOM | 10 BOX → 120 PCS; missing conversion → reject |
| MR reload | reopen 10 BOX not 120 PCS; legacy Std-only rows open |
| MR price | 10 BOX @ RM24 → UnitPrice 24, LineAmount 240; list TotalAmount **240** not 2880 |
| Item master lock | zero-qty BalLoc blocks StdUom change; NEW/rolled-back batch detail blocks; posted history blocks |
| Future movement date | Today 01-Oct, stock movement dated 02-Oct → reject |
| Concurrency | same BalLoc, same DocumentDate, two concurrent posts → serialize; later history order; rollback earlier rejected while later remains |

### Manual UAT highlights

| Scenario | Expected |
|---|---|
| Stock 01-Oct, MI 05-Oct | Allow |
| Stock 05-Oct, MI 05-Oct | Allow |
| Stock 06-Oct, MI 05-Oct | Reject |
| Existing destination 10-Oct, TR 05-Oct | Reject |
| IP 05-Oct, lot stock date 06-Oct | Not selectable / reject |
| Count 05-Oct, stock date 10-Oct | Exclude / reject |
| Last movement 10-Oct, new txn 05-Oct | Reject |
| Last movement 10-Oct, new txn 10-Oct | Allow if other rules pass |
| 01-Oct GR, 05-Oct MI, rollback GR first | Reject |
| Rollback MI then GR | Allow |
| StdUom PCS, MR 10 BOX with 1 BOX=12 | StdQty 120 PCS; reopen 10 BOX |
| Has any BalLoc (incl. 0) or NEW batch, change StdUom | Reject |

---

# 18. Error Message Standard

Use specific business messages. Do not return generic `Inventory error.` for integrity failures.

### Future stock

```text
Stock lot LOT001 is dated 10/10/2026 and cannot be used for
transaction date 05/10/2026.
```

### Later movement (NEW POST)

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

### Item master

```text
Standard UOM cannot be changed because this item already has inventory history.
Use a controlled inventory conversion/migration process.
```

---

# 19. Transaction Coverage Checklist (first ship)

| Rule | GR | MR | CR | MI | SC | VR | TR | ADJ | SP | IP | Count |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Transaction date valid | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Future stock blocked | existing dest | existing dest | existing dest | ✓ | ✓ | ✓ | source+dest | existing | ✓ | ✓ | ✓ |
| Later-day movement blocked | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Standard UOM | ✓ | ✓ (convert+ToPur*) | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ (+ BaseUom match) | ✓ |
| Rollback chronology | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | generated ADJ |

Deferred from checklist for first ship: full lot/status matrices, source ceilings beyond existing, FEFO, reservation model.

---

# 20. Non-Goals

Do not add during this hardening project unless a real customer requires them:

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
new MR costing/valuation algorithm
historical quantity rebuild for posting permission
```

The objective is reliable SME ERP inventory, not warehouse-management-system scope.

---

# 21. Definition of Done — First Ship

The first ship is complete only when all of the following are true:

1. No stock may be consumed before its stock date.
2. Same-day normal posting remains legal.
3. Later-day stock movement blocks unsafe backdating.
4. Same-day rollback respects persisted history order (`IvTrxHistory.Id`).
5. Rollback cannot bypass a later movement.
6. Rollback repairs `IvBalLoc.TransDate` even when net quantity change is zero; repair is RowVersion-safe (SQL Server and SQLite).
7. PMI uses `TransDate <= IssueDate` + current `StdQty`; does not allocate via `UsableBaseQty`.
8. PMI rejects BalLoc when `StdUom ≠ WorkOrderMaterial.BaseUom`.
9. Gate A hard P0s zero; Gate B opening baselines classified then true MISMATCH zero; Gate C chronology enabled.
10. Live target DB has unique stock-slice and non-negative constraints.
11. MR converts entered UOM to item standard UOM; preserves entered Qty/UOM in `ToPur*`; UnitPrice per entered UOM; document and list totals use entered qty × UnitPrice; reload shows original entry.
12. No new MR costing/valuation algorithm.
13. Item master blocks `StdUom` / `StockControl` / `LotControl` when any BalLoc (incl. zero), any NEW/rolled-back batch detail, or posted history exists.
14. Reconciliation detects null stock dates, negative balances, and StdUom mismatch.
15. Period-close and concurrency protection are not weakened.
16. Stock Count respects CountDate (as-of + MaxBackdateDays + chronology).
17. Automated tests cover the matrix in §17.
18. Inventory, Sales eligibility, and Production Material Issue use the same stock-date eligibility rule (not separate as-of rebuild permission).

## Longer-term DoD (deferred milestones)

Expired/blocked-status matrix, lot identity conflict rejection, FEFO for Sales, common availability/reservation model, source-ceiling hardening, posted-history immutability polish, zero-balance lifecycle policy.

---

# 22. Recommended First Coding Target

```text
Shared stock-date policy
        ↓
central posting guards (INV-03 + INV-04)
        ↓
as-of stock picker + Stock Count
        ↓
PMI: TransDate filter + StdQty + StdUom==BaseUom
        ↓
rollback INV-05 + GetLatestRemainingMovementAsync
        ↓
SetBalLocTransDateAsync (tracking / RowVersion safe)
        ↓
repository race safeguard
        ↓
Misc Receipt UOM + ToPur* + list TotalAmount
        ↓
item-master structural lock
        ↓
Gate A/B/C cutover + reconciliation + tests
```

This order gives the strongest integrity improvement with the least architectural churn. After implementing this first ship, stop expanding scope into FEFO, reservations, or status matrices until chronology and UOM meaning are stable in production.
