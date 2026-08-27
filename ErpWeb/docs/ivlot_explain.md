# IvLot — entity usage and columns

`IvLot` is the **lot identity / passport** table. It is not on-hand quantity. It answers: “this lot number for this item: where did it come from, and when?”

Quantity lives on `IvBalLoc`. Movements live on `IvTrxHistory`.

Source files:

| Role | Path |
| --- | --- |
| Entity | `ErpWeb.Model/Entities/Inventory/IvLot.cs` |
| EF mapping | `ErpWeb.Model/Configurations/Inventory/IvLotConfiguration.cs` |
| Table | `IvLot` |
| DbSet | `AppDbContext.IvLots` |
| Find-or-create | `IvStockPostingRepository.FindOrCreateLotAsync` |
| Posting (MR) | `IvInventoryPostingService` |

Related rules: [inventory-stock-lot.md](inventory-stock-lot.md).

---

## Why it exists (three stock layers)

Inventory is split on purpose. Do not collapse these layers.

| Layer | Table | Job |
| --- | --- | --- |
| Identity | **`IvLot`** | Birth certificate of a lot. Survives even when qty is 0. |
| Quantity | `IvBalLoc` | On-hand **here, now** (item + warehouse + bin + lot + status). |
| Movement | `IvTrxHistory` | Posted ledger. |
| Staging | `IvTrxBatch` + `IvTrxBatchDetail` | Unposted document. `IvLot` is **not** created on save. |

`LotNo` on `IvBalLoc` is only a **stock dimension** (how much of lot A is in this bin). It is not the lot’s origin.

Same lot can sit in many warehouses, bins, and statuses (`Good` vs `QC Hold`). When a pile goes to qty 0, the balance row can stay or be empty, but the lot row must remain for recall / supplier trace.

That is why origin is **not** stored on `IvBalLoc.PoNo` / `RefNo`. Origin lives on `IvLot` (`SourceType`, `SourceDocNo`, `SupplierCode`, `ReceiptDate`).

---

## When a row is created

Controlled by `IvStockMaster.LotControl`:

- **`LotControl = true`**: posting **must** find-or-create `IvLot` and set `LotId` on the balance / history.
- **`LotControl = false`**: `IvBalLoc.LotId` is null, `LotNo` is `""`. No dummy lot.
- **Unposted save** (`BatchStatus = NEW`): no `IvLot` insert. Lot is created only on **post**.

Lookup key: `(CompanyCode, ICode, LotNo)`. If missing, insert. If two posters race, unique index `UQ_IvLot_Company_ICode_LotNo` plus lock/retry in `FindOrCreateLotAsync`.

Empty `LotNo` is forbidden. Never insert a dummy lot for `""`.

Today, miscellaneous receipt posting fills:

- `SourceType` = `MR` (`IvTrxTypes.MiscellaneousReceipt`)
- `SourceDocNo` = batch number
- `ReceiptDate` = batch date
- `ExpiryDate` = line expiry (if entered)

`SupplierCode`, `MfgDate`, `QcStatus` are on the entity for later PO/GRN/WO. They are not filled by MR yet.

---

## Keys and relationships

```
IvStockMaster (CompanyCode, ICode)
        1 ── * IvLot (Id PK)
                    1 ── * IvBalLoc (LotId nullable)

IvTrxBatchDetail.FromLotId / ToLotId  →  IvLot.Id
IvTrxHistory.FromLotId    / ToLotId   →  IvLot.Id
```

- **PK:** `Id` (identity `int`, column `ID`). Movements FK to this, not to the compound lot key.
- **Business unique:** `(CompanyCode, ICode, LotNo)` — same lot number can exist on different items.
- **Scan index:** `(CompanyCode, LotNo)` — barcode/scan across items; not unique.
- **Supplier index:** `(CompanyCode, SupplierCode)` — recall by supplier later.
- **FK to item:** `(CompanyCode, ICode)` → `IvStockMaster`, `DeleteBehavior.Restrict` (no cascade).

Index names:

| Index | Columns | Unique |
| --- | --- | --- |
| `UQ_IvLot_Company_ICode_LotNo` | `(CompanyCode, ICode, LotNo)` | Yes |
| `IX_IvLot_Company_LotNo` | `(CompanyCode, LotNo)` | No |
| `IX_IvLot_Company_SupplierCode` | `(CompanyCode, SupplierCode)` | No |

---

## Column by column

Mapped in `IvLotConfiguration`. C# names vs DB names differ on audit/active.

### Identity / tenant

| C# | DB | Len | Meaning |
| --- | --- | --- | --- |
| `Id` | `ID` | int identity | Surrogate PK. This is what `LotId` / `FromLotId` / `ToLotId` point at. |
| `CompanyCode` | `CompanyCode` | 5, required | Tenant. Always part of the unique key. |
| `ICode` | `ICode` | 30, required | Item. Lot identity is **per item**, not global. |
| `LotNo` | `LotNo` | 50, required | Human/supplier lot number. Real value only; never `""`. |

### Origin (the reason this table exists)

| C# | DB | Len | Meaning |
| --- | --- | --- | --- |
| `SourceType` | `SourceType` | 20 | How the lot was born: designed `PO` / `GRN` / `WO` / `ADJ`. Today MR posts `MR`. |
| `SourceDocNo` | `SourceDocNo` | 50 | That document number (PO, GRN, WO, MR batch). |
| `SupplierCode` | `SupplierCode` | 20 | Supplier at birth. Code only — no supplier entity yet. For recall. |
| `ReceiptDate` | `ReceiptDate` | datetime? | When the lot **entered the company**. First receipt, not every later transfer. |

First writer wins. `FindOrCreateLotAsync` returns the existing row and does **not** overwrite origin.

### Quality / dates

| C# | DB | Meaning |
| --- | --- | --- |
| `MfgDate` | `MfgDate` | Manufacture date. Optional; not filled by MR. |
| `ExpiryDate` | `ExpiryDate` | Expiry for FEFO / warnings. Copied from the MR line on first create. |
| `QcStatus` | `QcStatus` (10) | **Lot-level** QC (`PASS` / `HOLD` / `FAIL`). **Not** the same as `IvBalLoc.IStatus` (Good / Damaged / QC Hold pile). One lot can have qty in several pile statuses. |
| `Remarks` | `Remarks` (250) | Free text on the lot passport. |

### Leftover / audit

| C# | DB | Meaning |
| --- | --- | --- |
| `LocationCode` | `LocationCode` (10) | Site/plant leftover, same as on other masters. **Not** the stock bin. Bin is `IvBalLoc.LocCode`. Do not use this in posting slice lookup. |
| `IsActive` | `Active` (default true) | Soft flag. Do not delete the row when qty is 0; deactivate if needed. |
| `CreatedDate` | `Created` | Insert time (UTC in posting). |
| `CreatedBy` | `UserID` (10) | Insert user. |
| `ModifiedDate` | `Updated` | Last change. |
| `ModifiedBy` | `UpdatedUID` (10) | Last change user. |

No `RowVersion` on `IvLot`. Concurrency is SQL lock on the unique key (`UPDLOCK`/`HOLDLOCK`), not optimistic concurrency.

### Navigations (not columns)

- `StockMaster` — the item this lot belongs to.
- `Balances` — all `IvBalLoc` piles that currently hold this lot.

---

## How posting uses it (current MR)

1. User saves MR with `ToLotNo` + optional expiry. Staging only. **No `IvLot`.**
2. On post, if the item is lot-controlled:
   - `FindOrCreateLotAsync(company, iCode, lotNo, sourceType=MR, sourceDocNo=batchNo, receiptDate, expiryDate)`
   - `plan.LotId = lot.Id`
3. `FindOrCreateBalLocAsync` stores `LotId` + copies `LotNo` onto the balance (denormalized; must match `IvLot.LotNo`).
4. History / detail get `ToLotId` (and later issues `FromLotId`).

If the same lot is received again, the existing `IvLot` is reused. Qty increases on `IvBalLoc`; origin stays from the first insert.

---

## What it is not for

- **Not quantity.** Qty is `IvBalLoc.StdQty`.
- **Not location.** Location is warehouse + `LocCode` on the balance.
- **Not genealogy.** Parent FG lot vs child material lots is a future `IvLotConsume` table. Do not put parent/child on `IvLot` or `IvBalLoc`.
- **Not the unposted document.** Unposted MR must not insert `IvLot`.
- **Not unique by `LotNo` alone.** Same number on two items = two rows.

---

## Lifetime rule

**Never delete `IvLot` because on-hand is 0.** Traceability (supplier, PO, expiry) must outlive the pile. That is the whole point of splitting identity from quantity.

---

## MUST / MUST NOT (short)

### MUST

- Create `IvLot` only when `LotNo` is a real lot (non-empty).
- If `LotControl == true`, receipt/production must create or find `IvLot` and set `LotId`.
- When `LotId` is set, copied `LotNo` on `IvBalLoc` / history **must match** `IvLot.LotNo`.
- Keep `IvLot` rows after stock is zero.

### MUST NOT

- Do not treat `IvBalLoc.PoNo` / `RefNo` as the lot origin.
- Do not delete `IvLot` because on-hand is 0.
- Do not create an `IvLot` with empty `LotNo`.
- Do not store parent/child lots on `IvLot` or `IvBalLoc` (future consume table).
- Do not cascade-delete lots.
- Do not unique `LotNo` alone on `IvBalLoc`. Same lot must exist in multiple warehouses, bins, and statuses.
