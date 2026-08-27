# Inventory Receipt Standard — Phase 1 Impact Report

**Date:** 2026-08-26  
**Scope:** Miscellaneous Receipt as the standard inventory transaction pattern  
**Status:** Inspection complete. Implementation proceeds under explicit user instruction to complete all plan phases.

---

## Inspected files

- `ErpWeb.UI/Inventory/Transactions/IvMiscReceipt.razor` (+ `.cs`)
- `ErpWeb.Core/Inventory/IvMiscReceiptService.cs`, `IIvMiscReceiptService.cs`, `IvTrxConstants.cs`
- `ErpWeb.Core/Numbering/RunningNumberService.cs`
- `ErpWeb.Model/Repositories/UserLoginRepository.cs` (DbContext factory pattern)
- `ErpWeb.Model/Entities/Inventory/*` (batch, detail, stock, warehouse, location, class, UOM, status)
- `ErpWeb.Model/Entities/Company.cs` (`TimeZoneId`)
- `ErpWeb/docs/inventory-stock-lot.md`
- `ErpWeb.Tests/IvMiscReceiptServiceTests.cs`

---

## Preserved behaviour (do not change)

| Area | Current behaviour |
| --- | --- |
| Page flow | Header → line grid → RECEIVE INFORMATION popup → SAVE staging |
| Duplicate detail lines | Allowed; no merge/reject |
| UOM conversion | None; `ToStdQty` / `ToStdUom` as entered (qty round 4 dp) |
| Item–UOM / Item–Class tables | Do not exist; any active company UOM/Class allowed |
| Lot helper (UI) | `yyMMdd` + 3-digit sequence on open document |
| Quantity | `<= 0` rejected; `decimal.Round(..., 4)` |
| Unit price | `>= 0`; round 4 dp; does not update item master |
| TrxDate | `TrxDtTime = request.TrxDate == default ? DateTime.Today : request.TrxDate.Date`; no period control |
| Audit | `CreatedDate` / `CreatedBy` on batch |
| Running numbers | `RunningNumberService.GetNextAsync(db, …)` with UPDLOCK; unique `UQ_IvTrxBatch_Company_Branch_BatchNo` |
| Staging | `ToLotId` / `ToBalLocId` null; no `IvLot` / `IvBalLoc` / `IvTrxHistory` |

---

## Sprint additions

1. **Schema:** `IvTrxBatchDetail.ExpiryDate`, `IClassCode`
2. **Repositories:** `IvStockMasterRepository`, `IvStockCommonRepository`, `IvStockTransactionRepository` (explicit methods; SAVE uses ambient `AppDbContext`)
3. **Core:** `IIvInventoryLookupService`, `ICurrentDateService` (company `TimeZoneId`, fallback `Asia/Kuala_Lumpur`)
4. **UI:** `IvStockMasterPicker` / SearchPopup, `IvCodeComboBox`; warehouse→location with stale-request guard; SAVE double-submit lock
5. **SAVE validation:** full matrix (item/WH/loc/UOM/class/status/qty/lot/expiry); non-lot empty lot → `""`; non-empty lot or non-null expiry on non-lot → reject
6. **Batch statuses:** `POSTED`, `CANCELLED` constants; repo update/delete reject non-NEW

---

## Out of scope

- Posting, `IvLot` / `IvBalLoc` / costing / accounting
- Reason master, other transaction pages
- UOM conversion, lot-number redesign, generic repository / MediatR / CQRS
- `IBusinessDateService`, accounting-period validation, Serializable isolation
- Server-side item paging (contract shaped for later)

---

## Risks closed by plan

- One DbContext + SQL transaction for validation + `GetNextAsync` + insert
- Expiry floor = company-zone Today (not TrxDate)
- No invented idempotency; two SAVEs = two batches
- AI must not invent duplicate-line / conversion / period rules
