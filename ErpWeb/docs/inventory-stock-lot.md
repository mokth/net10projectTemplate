# Inventory Model, Stock, Lot & Transactions — Agent Rules

**Audience:** AI coding agents and developers changing inventory entities, EF configs, posting, receiving, issue, transfer, sales return, or (later) production traceability.

**Read this before** changing any file under:
- `ErpWeb.Model/Entities/Inventory/`
- `ErpWeb.Model/Configurations/Inventory/`
- `ErpWeb.Model/Data/AppDbContext.cs`
- any stock in/out posting code

**Status:** Entity/EF model is in place (masters + lot + balance + batch + history). **MR posting/rollback** is implemented (`IIvInventoryPostingService`). Other trx types and production genealogy are not. Follow these rules when you extend them.

**Do not** treat scaffolded/legacy inventory as the template. Inventory was copied from old tables, then rewritten to match `Company` / `Role`. Follow **this document + current entities**, not old `[Table]`/`[Key]` annotations.

---

## 1. Purpose (what this model is)

Inventory has **masters** plus **three stock layers**. Do not collapse layers into one table. Do not hang optional master lookups off compound FKs.

### Masters (company-scoped codes)

| Table | Job |
| --- | --- |
| `IvType` | Item type (`KeepStock`). Surrogate `Id` + unique `(CompanyCode, TypeCode)` |
| `IvClass` / `IvSubClass` | Classification hierarchy |
| `MsUom` | Unit of measure (`MsUOM` table) |
| `IvWarehouse` | Warehouse, **branch-scoped** |
| `IvStockMaster` | Item. `StockControl` = track qty. `LotControl` = require a real lot |

### Stock layers

| Layer | Table | Job | Lifetime |
| --- | --- | --- | --- |
| Identity | `IvLot` | Birth certificate of a lot (PO/supplier/WO). | **Never delete** when qty hits 0 |
| Quantity | `IvBalLoc` | On-hand qty **here, now** (item + warehouse + bin + lot + status). | Create/update on post |
| Movement | `IvTrxHistory` | Posted ledger (like GL trans). | **Append-only** |
| Staging | `IvTrxBatch` + `IvTrxBatchDetail` | Unposted / posted document. | Kept after post (`POSTED`); edit only when `NEW` |

`IvTrxHistory` is the inventory equivalent of GL transaction history.  
`IvBalLoc` is the as-of-now stock pile.  
`IvLot` is lot traceability (where the lot came from; later: what it was used in).

### Core principles

> **Join by surrogate `Id` where the row is a pile, lot, or movement. Find-or-create by unique business key. Copy dimension columns onto history for reports.**

> **Masters that are identified by codes stay code-keyed (`CompanyCode` + code), except `IvType` which is hybrid (`Id` + unique TypeCode).**

The old system joined by compound keys (`Company + ICode + Warehouse + Location + Lot + IStatus + …`). That works until one column is blank or a transfer changes status. Do **not** go back to that as the FK for movements.

---

## 2. Source files

| Role | Path |
| --- | --- |
| Entities | `ErpWeb.Model/Entities/Inventory/` |
| EF configs | `ErpWeb.Model/Configurations/Inventory/` |
| DbContext | `ErpWeb.Model/Data/AppDbContext.cs` |

Entity namespace: `ErpWeb.Model.Entities.Inventory`  
Config namespace: `ErpWeb.Model.Configurations` (even though files sit under `Configurations/Inventory/`).  
`AppDbContext` uses `ApplyConfigurationsFromAssembly` — new `IEntityTypeConfiguration<T>` is picked up automatically, but you must still add `DbSet<T>`.

| Entity | Class | Table | DbSet |
| --- | --- | --- | --- |
| Type | `IvType` | `IvType` | `IvTypes` |
| Class | `IvClass` | `IvClass` | `IvClasses` |
| Subclass | `IvSubClass` | `IvSubClass` | `IvSubClasses` |
| UOM | `MsUom` | `MsUOM` | `MsUoms` |
| Warehouse | `IvWarehouse` | `IvWarehouse` | `IvWarehouses` |
| Item master | `IvStockMaster` | `IvStockMaster` | `IvStockMasters` |
| Lot master | `IvLot` | `IvLot` | `IvLots` |
| Balance | `IvBalLoc` | `IvBalLoc` | `IvBalLocs` |
| Unposted header | `IvTrxBatch` | `IvTrxBatch` | `IvTrxBatches` |
| Unposted line | `IvTrxBatchDetail` | `IvTrxBatchDetail` | `IvTrxBatchDetails` |
| Posted history | `IvTrxHistory` | `IvTrxHistory` | `IvTrxHistories` |

Do **not** leave leftover files such as `Class1.cs`.

---

## 3. Entity modeling conventions (all inventory tables)

Match `Company` / `Role`, not scaffolded POCOs.

### MUST (shape)

- File-scoped namespace `ErpWeb.Model.Entities.Inventory`.
- Plain class. **No** `partial`. **No** data annotations (`[Table]`, `[Key]`, `[Column]`, `[Required]`, `[MaxLength]`).
- Mapping lives only in `IEntityTypeConfiguration<T>`.
- Nullable reference types: required `string` uses `= string.Empty`; optional uses `string?`.
- Flags: `bool IsActive { get; set; } = true` — never `bool? Active`.
- Audit on C#: `CreatedDate`, `CreatedBy`, `ModifiedDate`, `ModifiedBy`.
- Map audit to existing DB columns in Fluent API (`Created`, `UserID`, `Updated`, `UpdatedUID`, `Active`).
- Quantities and money: `decimal` / `decimal?` with `HasPrecision(18, 4)`. **Never** `double`.
- PascalCase C# names (`IClassCode`, `WhCode`, `UomCode`, `DoNo`, `FrLotNo`). Use `HasColumnName` when the DB name differs.
- `DeleteBehavior.Restrict` on every inventory FK. No cascade delete of lots, balances, or masters that still have children.
- Register `DbSet<>` on `AppDbContext` when adding an entity.

### Tenant fields

| Field | Length | When required |
| --- | --- | --- |
| `CompanyCode` | 5 | Always on inventory rows. Part of almost every business key. |
| `BranchCode` | 5 | Required when it is in the PK/unique key (warehouse, balance, batch, history). Optional leftover on class/type/UOM/item. |
| `LocationCode` | 10 | Leftover site/plant field. **Not** the stock bin. Optional. Do not use in posting slice lookups. |
| `LocCode` | 10 | Bin **inside** `IvBalLoc` unique slice. Unused = `""`, never `NULL`. |

Warehouse is keyed `(CompanyCode, BranchCode, WarehouseCode)`. Balance unique slice **must** include `BranchCode` so it can FK to warehouse.

### MUST NOT (shape)

- Do **not** put `[Key]` on multiple properties. EF Core composite keys are Fluent-only (`HasKey(e => new { ... })`).
- Do **not** use `nvarchar(max)` for codes. Missing `HasMaxLength` breaks indexes (900-byte key limit).
- Do **not** add a navigation for an **optional** code plus required `CompanyCode` (section 6).
- Do **not** rename string copies `FrLot`/`ToLot` in C# — they clash with navigations `FromLot`/`ToLot`. C# names are `FrLotNo`/`ToLotNo`.

---

## 4. MUST / MUST NOT (stock, lot, posting)

### MUST

- Use `Id` (identity `int`, column `ID`) as PK on `IvType`, `IvLot`, `IvBalLoc`, `IvTrxBatch`, `IvTrxBatchDetail`, `IvTrxHistory`.
- Keep the listed unique business keys (section 5). They are for find-or-create and human lookup, **not** as movement FKs.
- Link movements with `FromBalLocId` / `ToBalLocId` and `FromLotId` / `ToLotId`.
- Link detail to batch with `BatchId` → `IvTrxBatch.Id`.
- Store unused `LocCode` / `LotNo` / `IStatus` on `IvBalLoc` as `""`, never `NULL` (SQL unique indexes do not clash two NULLs).
- Create `IvLot` only when `LotNo` is a real lot (non-empty). Do not insert dummy lots for `LotNo = ""`.
- If `IvStockMaster.LotControl == true`, receipt/production **must** create or find `IvLot` and set `LotId`.
- If `LotControl == false`, `IvBalLoc.LotId` is null and `LotNo` is `""`.
- Copy `LotNo` onto `IvBalLoc` and copy `FrLotNo` / `ToLotNo` onto movements (ledger denormalization). When `LotId` is set, copied `LotNo` **must match** `IvLot.LotNo`.
- Update `IvBalLoc.StdQty` with EF concurrency on `RowVersion`. Do not read-add-save without the token.
- Treat posted `IvTrxHistory` as immutable qty. Reverse with a **new** history line.
- Keep `IvLot` rows after stock is zero (traceability must outlive on-hand).

### MUST NOT

- Do **not** unique `LotNo` alone on `IvBalLoc`. Same lot must exist in multiple warehouses, bins, and statuses.
- Do **not** use the 6–8 column stock slice as the foreign key.
- Do **not** treat `IvBalLoc.PoNo` / `RefNo` as the lot origin. Origin lives on `IvLot` (`SourceType`, `SourceDocNo`, `SupplierCode`, `ReceiptDate`).
- Do **not** delete `IvLot` because on-hand is 0.
- Do **not** update posted history qty.
- Do **not** create an `IvLot` with empty `LotNo`.
- Do **not** add production genealogy into `IvBalLoc`. That is a future consume table (section 11).
- Do **not** cascade-delete lots or balances.
- Do **not** name a navigation `ToLot` **and** a string property `ToLot`. String copies are `FrLotNo` / `ToLotNo`; navigations are `FromLot` / `ToLot`.
- Do **not** FK `IvTrxHistory` to `IvTrxBatch`. Batch is staging and is cleared after post. `BatchNo` on history is a business reference only.

---

## 5. Keys

Hybrid key pattern (same as `IvType`): surrogate PK **or** natural PK, plus unique natural key when PK is `Id`.

| Table | PK | Unique business key | FK links |
| --- | --- | --- | --- |
| `IvClass` | `(CompanyCode, IClassCode)` | that PK | — |
| `IvSubClass` | `(CompanyCode, IClassCode, ISubClassCode)` | that PK | class `(CompanyCode, IClassCode)` |
| `IvType` | `Id` | `UQ_IvType_Company_TypeCode` = `(CompanyCode, TypeCode)` | — |
| `MsUom` | `(CompanyCode, UomCode)` | that PK | — |
| `IvWarehouse` | `(CompanyCode, BranchCode, WarehouseCode)` | that PK | — |
| `IvStockMaster` | `(CompanyCode, ICode)` | that PK | **no FKs** to type/class/UOM/warehouse (optional codes; section 6) |
| `IvLot` | `Id` | `UQ_IvLot_Company_ICode_LotNo` = `(CompanyCode, ICode, LotNo)` | item: `(CompanyCode, ICode)` |
| `IvBalLoc` | `Id` | `UQ_IvBalLoc_StockSlice` = `(CompanyCode, BranchCode, ICode, WhCode, LocCode, LotNo, IStatus)` | `LotId`; item `(CompanyCode, ICode)`; warehouse `(CompanyCode, BranchCode, WhCode)` → warehouse principal `(CompanyCode, BranchCode, WarehouseCode)` |
| `IvTrxBatch` | `Id` | `UQ_IvTrxBatch_Company_Branch_BatchNo` = `(CompanyCode, BranchCode, BatchNo)` | — |
| `IvTrxBatchDetail` | `Id` | `UQ_IvTrxBatchDetail_Company_Branch_Batch_Line` = `(CompanyCode, BranchCode, BatchNo, TrxLineNo)` | `BatchId`; `FromBalLocId`; `ToBalLocId`; `FromLotId`; `ToLotId` |
| `IvTrxHistory` | `Id` | `UQ_IvTrxHistory_Company_Branch_Batch_Line` = `(CompanyCode, BranchCode, BatchNo, TrxLineNo)` | `FromBalLocId`; `ToBalLocId`; `FromLotId`; `ToLotId`. **No FK to batch** |

`IStatus` is part of the balance slice. Good / Damaged / QC Hold are **separate piles**.

`RevNo` on `IvBalLoc` is **not** part of the unique key. Do not add it unless revision is a true stock dimension.

Item (`IvStockMaster`) is **company-level**. `BranchCode` on the item is home-branch leftover, not part of the PK. Warehouse and stock piles are **branch-level**.

---

## 6. Required vs optional relationships (do not “fix” this)

### Graph that IS configured in EF

```text
IvClass 1──* IvSubClass

IvStockMaster 1──* IvLot
IvStockMaster 1──* IvBalLoc
IvLot         1──* IvBalLoc       (LotId nullable)
IvWarehouse   1──* IvBalLoc       (WhCode → WarehouseCode)

IvTrxBatch    1──* IvTrxBatchDetail   (BatchId)
IvTrxBatchDetail ── FromBalLoc / ToBalLoc
IvTrxBatchDetail ── FromLot / ToLot

IvTrxHistory     ── FromBalLoc / ToBalLoc
IvTrxHistory     ── FromLot / ToLot
```

### Graph that is codes-only (NO navigation, NO FK)

On `IvStockMaster`: `IType`, `IClassCode`, `ISubClassCode`, `StdUom`, `PurUom`, `SellingUom`, `DefWarehouse`.

**Why:** EF composite FKs cannot mix required `CompanyCode` with a nullable lookup code. If `IType` is null but `CompanyCode` is set, the relationship is invalid. Do not add `IvType` / `IvClass` / `MsUom` navigations on stock until those codes are required non-null columns **and** you accept insert-order coupling.

Lookup in services by `(CompanyCode, code)`, not by navigation.

`ProdCode` / `ProdDesc` on movement lines exist beside `ICode` / `IDesc`. Treat as legacy duplication unless a later module defines a different meaning. Do not delete them in posting work.

---

## 7. How From vs To works on a movement line

| Trx type | FromBalLocId / FromLotId | ToBalLocId / ToLotId |
| --- | --- | --- |
| Stock in / GRN / PO receipt | null | required (create/find pile) |
| Stock out / issue / sales | required | null |
| Transfer / status change | required | required (other warehouse/bin/status, often same LotId) |
| Non-lot item | lot FKs null | lot FKs null |

Copy warehouse/lot/qty onto `Fr*` / `To*` columns for reports. IDs are the join.

---

## 8. Standard string lengths (reuse these)

Do not invent new lengths for the same kind of field.

| Kind | MaxLength | Examples |
| --- | --- | --- |
| Company / branch | 5 | `CompanyCode`, `BranchCode` |
| Site leftover | 10 | `LocationCode` |
| User audit | 10 | `CreatedBy`, `ModifiedBy` |
| Item code | 30 | `ICode`, `IClassCode`, `ISubClassCode`, `ProdCode` |
| Description | 200 | `IDesc`, `ProdDesc`, `TypeDesc` |
| Short name | 100 | `TypeName`, `ISubClassName`, `WarehouseDesc`, `UomDesc` |
| Type / trx / status | 20 / 10 | `TypeCode` 20, `TrxType` 20, `BatchStatus` 20, `IStatus` 10, `QcStatus` 10 |
| Warehouse | 20 | `WarehouseCode`, `WhCode`, `DefWarehouse`, `FrWarehouse` |
| UOM | 10 | `UomCode`, `StdUom`, `FrStdUom` |
| Lot | 50 | `LotNo`, `FrLotNo`, `ToLotNo` |
| Doc nos | 30 | `PoNo`, `SoNo`, `DoNo`, `InvNo` |
| GL / tax | 20 | `SellingGlCode`, `TaxGroup`, `SupplierCode` |
| Currency | 3 | `Currency` |
| Remarks | 250 | `Remarks`, `WarehouseRemark` |
| Image | 500 | `ImagePath` |

Qty/money: `decimal(18,4)`.

---

## 9. Lot traceability (why `IvLot` exists)

`LotNo` on `IvBalLoc` is only a **stock dimension** (qty of lot A in this bin). It is not the lot passport.

Traceability the business needs:

1. Material lot came from which PO / supplier.
2. Later: FG lot consumed which material lots.
3. FG sold to customer (history).
4. Customer return: same FG lot → materials → PO/supplier.

That cannot live only on `IvBalLoc`:

- Same lot exists in many locations.
- Balance rows may go to qty 0; origin must remain.
- Production is many-to-many (one FG lot, many material lots).

### `IvLot` origin fields

| Field | Use |
| --- | --- |
| `SourceType` | `PO` / `GRN` / `WO` / `ADJ` (string, max 20) |
| `SourceDocNo` | Source document number |
| `SupplierCode` | Supplier at birth (no supplier entity yet — store code) |
| `ReceiptDate` | When the lot entered the company |
| `MfgDate` / `ExpiryDate` | Optional |
| `QcStatus` | Optional lot QC, not the same as `IvBalLoc.IStatus` (pile status) |

Find lot by `(CompanyCode, ICode, LotNo)`. Scan-by-number index: `(CompanyCode, LotNo)` — not unique across items.

### Item flags

`IvStockMaster.LotControl` (default `false`).  
`StockControl` = whether qty is tracked. `LotControl` = whether a real lot number is required.  
`IvType.KeepStock` is type-level default intent; posting still obeys the **item** flags.

---

## 10. Posting algorithm (implement this in Core)

**Status:** Implemented for MR via `IIvInventoryPostingService` / `IvStockPostingRepository`. Staging batch is **kept** with `BatchStatus = POSTED` (not deleted). True unpost deletes that batch’s history and returns the document to `NEW`.

Hard rules A–T (single StdQty owner, UPDLOCK/HOLDLOCK, unique keys, posted immutability, history-authoritative rollback, no negative stock, tenant scope, missing BalLoc/Lot key-range find/create): see plan and code.

Atomic transaction phases:

1. **Validate** — `LockBatchForUpdateAsync`; re-read batch + all details; UPDLOCK involved `IvStockMaster`; validate all lines; no BalLoc writes yet.
2. **Lock** — aggregate stock-controlled deltas by `IvStockSliceKey`; `GetOrderedStockSlices` + `LockBalanceSlicesAsync`; find/create Lot and BalLoc (exact unique key + HOLDLOCK; on 2601/2627 re-query with UPDLOCK).
3. **Calculate** — if any `newQty < 0`, fail entire batch.
4. **Apply** — one BalLoc update per aggregated slice (`StockControl` only).
5. **History** — insert one `IvTrxHistory` per document line (non-stock: null BalLoc FKs).
6. **Status** — `POSTED` + audit (`PostedDate/By`, `PostingOperationId`, counts). Keep staging.
7. **Commit**

Rollback: lock batch → read all history → lock slices → if any would go negative, abort unchanged → subtract → delete history → clear detail FKs → `NEW` + rollback audit.

**OpeningQty = 0** is only valid for controlled tests. Do not enable production reconciliation until an opening-balance baseline exists.

### Concurrency

Primary: SQL transaction + `UPDLOCK`/`HOLDLOCK` on batch, StockMaster, and BalLoc/Lot (exact unique key; key-range when missing). Secondary: `IvBalLoc.RowVersion`. `ROWLOCK` is not a correctness dependency.

---

## 11. Not in the model yet (do not invent on BalLoc)

When **production** is implemented, add a consume / genealogy table, for example `IvLotConsume`:

- `ParentLotId` = FG `IvLot.Id`
- `ChildLotId` = material `IvLot.Id`
- `Qty` consumed
- Source WO / batch reference

Then:

- Return FG → `IvTrxHistory` by `FromLotId`/`ToLotId` → `IvLotConsume` children → each child `IvLot.SupplierCode` / `SourceDocNo`.

Until that module exists: do not store parent/child lots on `IvBalLoc` or `IvLot`.

Also not modeled yet (add when needed, on `IvBalLoc`, not by overloading `StdQty`):

- `ReservedQty` / `AvailableQty` for SO allocate

Also not modeled yet: UOM conversion table, serial master, stock-take header. Add new entities with section 3 conventions; do not overload `IvBalLoc`.

---

## 12. Column / property naming traps

| C# | DB column | Notes |
| --- | --- | --- |
| `Id` | `ID` | Hybrid PKs: type, lot, balance, batch, detail, history |
| `ICode` / `IDesc` / `IStatus` | same | |
| `IClassCode` on stock/subclass | `IClass` | Stock and subclass column is `IClass` |
| `ISubClassCode` on stock | `ISubclass` | |
| `UomCode` / `UomDesc` | `UOMCode` / `UOMDesc` | Table `MsUOM` |
| `UneceUom` | `UNECE_UOM` | |
| `WhCode` | `WHCode` | |
| `SellingGlCode` | `SellingGLCode` | |
| `IvBalLoc.LotNo` | `LotNo` | Copy of lot number; FK is `LotId` |
| `FrLotNo` | `FrLot` | String copy. Navigation is `FromLot` |
| `ToLotNo` | `ToLot` | String copy. Navigation is `ToLot` (`IvLot`) |
| `PoNo` | `PO_No` | Document ref, not lot origin |
| `SoNo` | `SO_No` | |
| `DoNo` | `DONo` | |
| `PoRelNo` | `PO_Rel_No` | |
| `SoLineNo` / `PoLineNo` | `SO_Line_No` / `PO_Line_No` | |
| `CreatedDate` | `Created` | |
| `CreatedBy` | `UserID` | |
| `ModifiedDate` | `Updated` | |
| `ModifiedBy` | `UpdatedUID` | Missing on `IvBalLoc` and `IvTrxHistory` — do not invent the column |
| `IsActive` | `Active` | Masters + `IvLot`. Not on balance/batch/history |
| `AsNowCost` | `asNowCost` | History only |
| `LocCode` vs `LocationCode` | both exist | `LocCode` = bin in the stock slice |

EF: `FromLotId` → nav `FromLot`; `ToLotId` → nav `ToLot`. That is why the strings cannot be named `ToLot`.

`IvBalLoc` has `ModifiedDate` but no `ModifiedBy` (legacy table had `Updated`, not `UpdatedUID`).

---

## 13. Agent checklist

### When changing / adding an inventory **entity**

- [ ] POCO + Fluent config; no data annotations on the class
- [ ] `CompanyCode` in the business key unless there is a documented exception
- [ ] Hybrid `Id` PK only for type/lot/balance/batch/detail/history
- [ ] `HasMaxLength` on every string; `HasPrecision(18,4)` on qty/money
- [ ] Audit names mapped to `Created` / `UserID` / `Updated` / `UpdatedUID` / `Active`
- [ ] FKs `Restrict`; no cascade
- [ ] No navigation on optional stock lookup codes (section 6)
- [ ] `DbSet` on `AppDbContext`
- [ ] Do not rename `FrLotNo`/`ToLotNo` back to `ToLot`

### When adding receive, issue, transfer, adjust, or posting

- [ ] Resolve pile by unique slice **or** by `BalLocId` if already stamped
- [ ] Lot-controlled item: find/create `IvLot`, set `LotId`
- [ ] Non-lot item: `LotId` null, `LotNo` `""`
- [ ] Set From/To BalLoc and Lot IDs per trx type
- [ ] Copy `Fr*` / `To*` including `FrLotNo` / `ToLotNo`
- [ ] Update qty with `RowVersion`
- [ ] Insert history; do not update old history qty
- [ ] Do not delete `IvLot`
- [ ] Do not unique `LotNo` on `IvBalLoc`
- [ ] Do not join posting by compound slice as FK
- [ ] Do not FK history to batch
- [ ] Unposted save (`BatchStatus = NEW`): copy `Fr*` / `To*` only; leave BalLoc/Lot IDs null; do **not** insert `IvLot` / `IvBalLoc` / `IvTrxHistory`
- [ ] Post keeps staging with `BatchStatus = POSTED`; do **not** delete batch/details on post
- [ ] `BatchNo` from `IRunningNumberService` + `RunningNumberKeys.IvBatch` (`MsRunningNo`). Do **not** `MAX(IvTrxBatch.BatchNo)`
- [ ] Only `IvStockPostingRepository` / posting engine may write `IvBalLoc.StdQty`

---

## 14. Quick examples

**PO receipt, lot-controlled item**  
Insert/find `IvLot` (SourceType=`PO`/`GRN`, SourceDocNo, SupplierCode).  
Find/create `IvBalLoc` slice. `ToBalLocId` + `ToLotId`. Increase `StdQty`. History in.

**Warehouse transfer, same lot**  
`FromLotId == ToLotId`. Different `FromBalLocId` / `ToBalLocId` (other `WhCode`/`LocCode`/`IStatus`).

**Sales issue**  
`FromBalLocId` + `FromLotId`. Decrease qty. History out.

**Customer return of FG**  
Receive onto `ToLotId` = original FG lot (do not invent a new lot unless business says so).  
Supplier/material trace = `IvLot` origin + (later) consume table, not `IvBalLoc.PoNo`.

**New master field on `IvStockMaster`**  
Add POCO property (`string?` or `decimal?`). Map length/precision and `HasColumnName` if the DB name differs. Do **not** add an EF navigation to `IvType`/`IvClass`/`MsUom` for an optional code.

---

## 15. Unposted batch entry & global `BatchNo`

Staging screens (MR, later issue / transfer / adj) insert `IvTrxBatch` + `IvTrxBatchDetail` only.

| Field | Unposted MR |
| --- | --- |
| `TrxType` | `MR` |
| `BatchStatus` | `NEW` |
| From\* / FromBalLocId / FromLotId | null |
| ToWarehouse / ToLocation / ToLotNo / ToStdQty / ToStdUom | required (location unused = `""`; lot unused = `""`) |
| ToBalLocId / ToLotId | **null until post** |

`BatchNo` is a **company-scoped global running number** shared by all inventory trx types. Allocate on Save via `IRunningNumberService.GetNextAsync(db, company, RunningNumberKeys.IvBatch)` inside the same transaction as the batch insert. Counter table: `MsRunningNo` (`CompanyCode` + `DocKey`, `LastNo`). Do not derive from open batches — staging is cleared after post and `BatchNo` remains on `IvTrxHistory`.

Peek (`PeekNextAsync`) is display-only and does not consume a number.

UI: `ErpWeb.UI/Inventory/Transactions/`. SQL: `scripts/init-msrunningno.sql`.

