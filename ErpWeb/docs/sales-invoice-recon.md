# Sales Invoice v1 — Recon (implementation-grade)

**Database:** `ERPWeb` on `.\SQLEXPRESS` (`ErpWeb/appsettings.json` `DefaultConnection`)  
**Audit date:** 2026-09-02  
**Sources:** ErpWeb (`c:\wincom\net10projects`), legacy `ERP_5.5` invoice helpers  
**Gate:** no invoice feature code until this document is treated as the source of schema, FIFO, extract points, and calc rules. Vague restatements of this file are a fail.

Invoice is a **sales document that owns** one SP stock-out batch. It is **not** an inventory screen. Do not add `SP` to `DispatchAsync` as the invoice posting path (wrong menu, own transaction, max 10, continues after failure).

---

## Required facts (copy into implementation)

```text
FIFO:
ORDER BY TransDate ASC, LotNo ASC, IvBalLoc.ID ASC
Filter: CompanyCode, BranchCode, ICode, WHCode, LocationCode = tenant,
        StdQty > 0, IStatus = 'ACTIVE', TransDate IS NOT NULL AND TransDate <= InvDate.Date
Skip: IvStockMaster.StockControl = 0 (no SP lines)

MI transaction extraction:
PostInventoryMIAsync — ErpWeb.Core/Inventory/IvInventoryPostingService.cs, method PostInventoryMIAsync, lines 974–1158
  Extract Core: lines 985–1148 (LockBatch through batch POSTED mutation; exclude CreateDbContext, BeginTransaction, SaveChanges, Commit)
RollBackInventoryMIAsync — same file, method RollBackInventoryMIAsync, lines 1160–1277
  Extract Core: lines 1171–1269 (LockBatch through batch NEW mutation; exclude CreateDbContext, BeginTransaction, SaveChanges, Commit)
Do not pick a different abstraction after coding starts.

Lock order:
Invoice (UPDLOCK/RowVersion) → IvTrxBatch UPDLOCK HOLDLOCK → IvStockMaster UPDLOCK (ICode ordinal ignore-case) → IvBalLoc UPDLOCK HOLDLOCK ordered by IvStockSliceKey CompareTo

TrxDtTime:
IvTrxBatch.TrxDtTime = InvDate.Date (date only, same as IvMiscIssueService line 410).
Post requires batch.TrxDtTime.Date == invoice.InvDate.Date.
Changing invoice date does not auto-update SP or auto-FIFO. Post fails until user Add Shipment (rebuilds and sets TrxDtTime).

Numbering:
GetNextAsync(db, company, "SA_INV_yyyyMM") on the SAME AppDbContext that owns BeginTransaction.
SaveChanges inside GetNextAsync is enlisted; it does not independently commit.
Failed save rolls back LastNo (number reused).
Prefix: SaCust.InvoicePrefix if non-empty else "INV". InvNo = {Prefix}{YY}{MM}{seq:D4}. Unique (CompanyCode, InvNo).

Audit:
IvTrxBatch live: PostedDate datetime NULL, PostedBy nvarchar(20) NULL, RollbackDate datetime NULL, RollbackBy nvarchar(20) NULL.
Mirror those four names on SaInvoice. Created/Updated follow SaCust (datetime2, UserID/UpdatedUID nvarchar(20)).

Live schema:
SaInvoice: TABLE MISSING — create. Do not invent from plan markdown alone; use columns below.
IvTrxBatch indexes: PK_IvTrxBatch(ID); UQ_IvTrxBatch_Company_Branch_BatchNo; IX_IvTrxBatch_Company_BatchStatus.
Filtered unique SP/RefNo: ALLOWED (no existing filter index; no index on RefNo).
```

---

## 1. MI caller-transaction extract (P1)

**File:** [ErpWeb.Core/Inventory/IvInventoryPostingService.cs](../../ErpWeb.Core/Inventory/IvInventoryPostingService.cs)

| Method | Lines (2026-09-02) | Opens own context+tx | Commits |
| --- | --- | --- | --- |
| `PostInventoryMIAsync` | 974–1158 | 982–983 | 1150–1151 |
| `RollBackInventoryMIAsync` | 1160–1277 | 1168–1169 | 1270–1271 |

**Defect if invoice only passes a transaction token:** both methods always `CreateDbContextAsync` then `BeginTransactionAsync`. A wrapper that still calls these as-is will post stock in a **second** connection/transaction.

### Required refactor (named before coding)

1. Add **private** `PostInventoryMICoreAsync(AppDbContext db, string companyCode, string branchCode, string userId, int batchNo, string expectedTrxType, CancellationToken)` containing today’s **985–1148**:
   - `LockBatchForUpdateAsync`
   - NEW check, details, history-exists, `LockStockMastersAsync`, `BuildMiPostLine`
   - `sliceById.OrderBy(kv => kv.Value)` then `LockBalLocByIdForTenantAsync`
   - qty check, `DecreaseBalLocQtyAsync(..., batch.TrxDtTime)`
   - existing `TestHookAfterMiStockUpdate` (line 1097)
   - history insert, `TestHookAfterMiHistory` (line 1140)
   - set `BatchStatus=POSTED`, `PostedDate`, `PostedBy`, `PostedCount`, `PostingOperationId`
   - **no** `SaveChanges`, **no** `Commit`, **no** `CreateDbContext`
2. Keep `PostInventoryMIAsync` as MI’s public path: create context, begin tx, call Core, `SaveChanges` + `Commit` on success.
3. Same split for rollback: Core = **1171–1269**; public method keeps context/tx/commit.
4. Expose Core through `IIvInventoryPostingService` as e.g. `PostStockOutInTransactionAsync` / `RollBackStockOutInTransactionAsync` taking the **caller’s** `AppDbContext` (already in a transaction). Invoice Post/Rollback call these only.

`expectedTrxType` for invoice = `IvTrxTypes.SalesOut` (`"SP"`) — already defined in [IvTrxConstants.cs](../../ErpWeb.Core/Inventory/IvTrxConstants.cs) line 15.

Core must **not** check `MenuCodes.InventoryMiscIssue`. Invoice service checks `SA_INVOICE` + `POST`/`ROLLBACK`.

Do **not** add `SP` to `DispatchAsync` (lines 48–178). That path:

- rejects unknown types at 61–63 (`Posting is not implemented for transaction type 'SP'`)
- authorizes `INV_MISC_ISSUE` (lines 83–93)
- uses `IvPostingLimits.MaxPostSelection = 10`
- **continues** remaining batches after a failure (loop 101–148)

Invoice max-3 is: 0 error, 4 reject before any post, stop after first failure (A posted, B fail → C not attempted). Own loop in `ISaInvoiceService`.

### Existing test hooks (reuse)

| Hook | When | Maps to plan fault |
| --- | --- | --- |
| `TestHookAfterMiStockUpdate` (969, 1097) | after decrease, before history | failure during stock update |
| `TestHookAfterMiHistory` (972, 1140) | after history, before SaveChanges POSTED | failure before invoice status if invoice status is set after Core returns |

**Still add on invoice service:** throw after stock Core succeeds, before `SaInvoice.Status = POSTED`; throw after status set, before outer `Commit`. Expected: invoice NEW, `IvBalLoc` unchanged, no history.

---

## 2. Lock order (copy exactly)

Invoice Post, one transaction, one `AppDbContext`:

1. **Lock invoice** — `SaInvoice` `UPDLOCK, HOLDLOCK` (and `RowVersion` on save). Status must be `NEW`.
2. **Lock SP batch** — `LockBatchForUpdateAsync` ([IvStockPostingRepository.cs](../../ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs) 123–144):  
   `FROM dbo.IvTrxBatch WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode AND BranchCode AND BatchNo`
3. **Lock stock masters** — `LockStockMastersAsync` (165–178): ICodes `OrderBy(..., StringComparer.OrdinalIgnoreCase)` then `IvStockMaster WITH (UPDLOCK, HOLDLOCK)`. Do not skip; MI already does this between batch and piles.
4. **Lock piles** — `sliceById.OrderBy(kv => kv.Value).Select(kv => kv.Key)` then `LockBalLocByIdForTenantAsync` (`IvBalLoc WITH (UPDLOCK, HOLDLOCK) WHERE ID AND CompanyCode AND BranchCode`).

`IvStockSliceKey.CompareTo` ([IvStockSliceKey.cs](../../ErpWeb.Model/Repositories/Inventory/IvStockSliceKey.cs) 34–48), ordinal:

`CompanyCode → BranchCode → ICode → WhCode → LocCode → LotNo → IStatus`

This is **not** FIFO order. Do not sort piles by `TransDate` for locking.

Rollback Core uses the same BalLoc `OrderBy(kv => kv.Value)` (lines 1214–1217).

---

## 3. TrxDtTime vs invoice date

**How MI sets it:** [IvMiscIssueService.cs](../../ErpWeb.Core/Inventory/IvMiscIssueService.cs) line 410:

`batch.TrxDtTime = request.TrxDate == default ? DateTime.Today : request.TrxDate.Date`

**How post uses it:** `DecreaseBalLocQtyAsync` / history `TrxDtTime` / `IvBalLoc.TransDate` are all `batch.TrxDtTime` (posting 1087, 1113). Decrease **overwrites** pile `TransDate` to the batch date ([IvStockPostingRepository.cs](../../ErpWeb.Model/Repositories/Inventory/IvStockPostingRepository.cs) 476–479).

**Legacy post check:** [InvTrxHelper.CheckTrxDtTimeBatch](../../../ERPV55/ERP_5.5/ERPCommonUI/SalesForms/HelperClass/InvTrxHelper.cs) lines 1091–1114. If `IvTrxBatch.TrxDtTime` string ≠ `SaInvoice.InvDate` string → *"Some shipment date is not updated, please add shipment..."* (line 774–777).

**v1 rule (locked):**

- Add Shipment sets `IvTrxBatch.TrxDtTime = InvDate.Date`.
- Save that only changes invoice date does **not** rewrite SP `TrxDtTime` and does **not** rebuild FIFO.
- Post rejects unless `batch.TrxDtTime.Date == invoice.InvDate.Date`.
- User must click Add Shipment (rebuild) after a date change.

FIFO eligibility uses **invoice date**, not pile mutation from a previous post: `TransDate <= InvDate.Date`.

---

## 4. FIFO (deterministic)

**Legacy load:** [ShipmentHelper.OpenTable](../../../ERPV55/ERP_5.5/ERPCommonUI/SalesForms/HelperClass/ShipmentHelper.cs) 849–874:

```text
SELECT * FROM IvBalLoc
WHERE IStatus in ('ACTIVE','REPROCESS','CONSIGMENT') AND StdQty>0
  AND TransDate <= {InvDate}
  AND CompanyCode = {comp} AND BranchCode = {branch} AND LocationCode = {LocationCode}
ORDER BY TransDate, LotNo
```

Then per line: `dtBalLot.Select("ICode=... and WHCode=...")` and walk `dr2[count]` in that array order. `DataTable.Select` is not a reliable sort; ErpWeb must **not** copy that. Use SQL/LINQ `OrderBy` with a unique last key.

**ErpWeb columns:** `IvBalLoc.TransDate` datetime NULL, `LotNo` nvarchar NOT NULL, `ID` int PK. Live statuses in `IvStatus`: `ACTIVE`, `PENDING` only (not legacy REPROCESS/CONSIGMENT; not `IvItemStatuses.Damaged`/`QcHold` unless later seeded).

**v1 query (locked):**

```text
WHERE CompanyCode = @c
  AND BranchCode = @b
  AND ICode = @item
  AND WHCode = @lineWarehouse
  AND LocationCode = @tenantLocation
  AND StdQty > 0
  AND IStatus = N'ACTIVE'
  AND TransDate IS NOT NULL
  AND TransDate <= @invDate
ORDER BY TransDate ASC, LotNo ASC, ID ASC
```

`TransDate IS NOT NULL` matches legacy `TransDate <= date` (NULL fails the inequality in SQL Server).

**Skip shipment** when `IvStockMaster.StockControl == false` (`BuildMiPostLine` 2096–2098). Do not use `IvType.KeepStock` for the post engine; MI uses `StockControl`. Live `IvType`: `FG`/`RM` both KeepStock=1. Treat non-stock / SERVICE as `StockControl = false`.

Each pile → `IvTrxBatchDetail` with `FromBalLocId`, `FrStdQty`, `FrWarehouse`, `FrLocation`, `FrLotNo`, `IStatus`, `InvNo`, `SoLineNo` = invoice line number. Qty via `IvQty.Round` (4 dp). No `IvBalLoc` qty change on Add Shipment. No new `IvLot`.

Repeat Add Shipment on unchanged stock must produce the same `FromBalLocId` sequence.

---

## 5. Live database

Queried 2026-09-02, database **ERPWeb**.

### SaInvoice / SaInvoiceDetail

**Tables do not exist.** Create via idempotent script (`IF OBJECT_ID ... IS NULL`). Do not generate a migration from the plan file alone.

Recommended new tables (aligned to live `SaCust` + SP link columns, not guessed PK from V5.5 single-db `InvNo`):

**SaInvoice**

| Column | Type (live convention) | Notes |
| --- | --- | --- |
| CompanyCode | nvarchar(10) NOT NULL | with InvNo = PK; matches live SaCust |
| InvNo | nvarchar(30) NOT NULL | matches `IvTrxBatchDetail.InvNo` |
| BranchCode | nvarchar(10) NULL | stamp, not part of number |
| LocationCode | nvarchar(10) NULL | tenant stamp |
| CustCode | nvarchar(60) NOT NULL | live SaCust.CustCode |
| InvDate | datetime2 NOT NULL | |
| Status | nvarchar(20) NOT NULL | `NEW` / `POSTED` only |
| DONo | nvarchar(30) NOT NULL | v1: copy of InvNo (intentional) |
| Currency | nvarchar(20) NULL | |
| CurrRate | decimal(18,6) NOT NULL | |
| GrossAmnt, Taxes, TotAmnt | decimal(18,2) | server-calculated |
| PostedDate | datetime NULL | **same name/type as IvTrxBatch** |
| PostedBy | nvarchar(20) NULL | live IvTrxBatch is nvarchar(20), not EF max 10 |
| RollbackDate | datetime NULL | |
| RollbackBy | nvarchar(20) NULL | |
| Created | datetime2 NULL | SaCust |
| UserID | nvarchar(20) NULL | |
| Updated | datetime2 NULL | |
| UpdatedUID | nvarchar(20) NULL | |
| RowVersion | timestamp NOT NULL | SaCust Level A |

PK: `(CompanyCode, InvNo)`. Unique: PK is enough. No InvNo-only unique (multi-company).

**SaInvoiceDetail:** PK identity `ID`; unique `(CompanyCode, InvNo, Line)`; `StdQty` decimal(18,4); `FrWarehouse`; tax/discount columns needed by calc below; FK to header.

Idempotent: `IF OBJECT_ID(N'dbo.SaInvoice','U') IS NULL CREATE ...`; `IF COL_LENGTH(...) IS NULL ALTER`; `IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=...)`. Safe if header exists without detail/index/`RowVersion`. Pattern: [scripts/alter-ivtrxbatch-posting-audit.sql](../../scripts/alter-ivtrxbatch-posting-audit.sql). Target **ERPWeb**, not `USE ERPLiteEx` ([init-msrunningno.sql](../../scripts/init-msrunningno.sql) is stale).

### IvTrxBatch (live)

| Index | Unique | Columns |
| --- | --- | --- |
| PK_IvTrxBatch | yes | ID |
| UQ_IvTrxBatch_Company_Branch_BatchNo | yes | CompanyCode, BranchCode, BatchNo |
| IX_IvTrxBatch_Company_BatchStatus | no | CompanyCode, BatchStatus |

No index on `RefNo`. No filtered indexes. **Filtered unique is allowed:**

```sql
CREATE UNIQUE INDEX UQ_IvTrxBatch_SP_RefNo
ON dbo.IvTrxBatch (CompanyCode, BranchCode, RefNo)
WHERE TrxType = N'SP' AND RefNo IS NOT NULL;
```

If create fails on existing data, enforce at-most-one SP in the invoice transaction only and document why.

Audit columns **exist** (do not add aliases):

| Column | Live type |
| --- | --- |
| PostedDate | datetime NULL |
| PostedBy | nvarchar(20) NULL |
| RollbackDate | datetime NULL |
| RollbackBy | nvarchar(20) NULL |
| PostedCount / RollbackCount | int NOT NULL |
| PostingOperationId / RollbackOperationId | uniqueidentifier NULL |
| TrxDtTime | datetime NOT NULL |
| Created / Updated | datetime NULL (`Created`/`Updated` column names) |
| UserID / UpdatedUID | nvarchar(20) / nvarchar(100) |

EF [IvTrxBatchConfiguration](../../ErpWeb.Model/Configurations/Inventory/IvTrxBatchConfiguration.cs) maps `PostedBy` max 10; live is 20. Invoice stamps should Truncate to 10 like MI (`Truncate(userId, 10)`) or widen EF to 20. Do not invent `PostedOn` / `PostedUser`.

### IvTrxBatchDetail (live)

PK `ID`. Unique `UQ_IvTrxBatchDetail_Company_Branch_Batch_Line` (CompanyCode, BranchCode, BatchNo, TrxLineNo). Non-unique `IX_IvTrxBatchDetail_Company_ICode`, `IX_IvTrxBatchDetail_BatchId`. Columns `InvNo`, `SO_Line_No`, `FromBalLocId` already mapped in EF.

### Other live facts

| Object | Status |
| --- | --- |
| SaTaxGroup | **missing** |
| AdPara | **missing** (no SalesTaxDec) |
| IvMasPack | **missing** (no MinPrice) |
| SaCurrency / SaCurrRate | present (`DEMO`/`MYR`) |
| MsRunningNo | PK `(CompanyCode, DocKey)`; DEMO `IV_BATCH` LastNo=6 |
| IvMSCode | TAX codes exist; **no Percentage column** |
| SaCust.InvoicePrefix | nvarchar(40) NULL |
| SaCust.DiscountMethod / DecPoint | present |

---

## 6. Numbering

[RunningNumberService.GetNextAsync](../../ErpWeb.Core/Numbering/RunningNumberService.cs) 29–69:

- Takes caller `AppDbContext` (no factory).
- SQL Server: `MsRunningNo WITH (UPDLOCK, ROWLOCK, HOLDLOCK)`.
- Always `SaveChangesAsync` after increment (and on first insert).
- Does **not** call `BeginTransaction` / `Commit`.

**Ambient transaction:** EF Core `SaveChanges` on a context that already has `Database.BeginTransactionAsync` **does not commit** the transaction. Outer `RollbackAsync` reverts `LastNo`.

**Must:** invoice `SaveNew` uses one context: `BeginTransaction` → `GetNextAsync(db, company, $"SA_INV_{invDate:yyyyMM}")` → insert `SaInvoice` → `SaveChanges` → `Commit`.

**Must not:** new `AppDbContext` for numbering, or `GetNextAsync` before the invoice transaction starts.

`DocKey` max 20 ([RunningNumberService](../../ErpWeb.Core/Numbering/RunningNumberService.cs) 111–114). `SA_INV_202609` is 13 characters.

Add `RunningNumberKeys.SaInvoice = "SA_INV"`. Period is appended at call site, not stored as a separate column.

Format: `{Prefix}{YY}{MM}{seq:D4}` e.g. prefix `INV` → `INV26090001`. Prefix from `SaCust.InvoicePrefix` if not blank, else `INV`. Branch is not in the number. SP `BatchNo` stays `RunningNumberKeys.IvBatch`.

UI shows AUTO until save. Edit never allocates.

---

## 7. Totals / tax / discount (source wins)

Port the **add-line** path in [InvoiceEntry.aspx.cs](../../../ERPV55/ERP_5.5/ERP/SalesForms/InvoiceEntry.aspx.cs) (not SO/DO branches). Recalculate on every save; do not persist browser totals as truth.

### Line amount

- `Amount = Round(Qty * UnitPrice, 2, AwayFromZero)` (lines 2977–2982; comment 15-11-2022).
- Qty / `StdQty`: `IvQty.Round` (4 dp) for stock; selling qty as entered.
- `CCommon.RoundingToDouble` ([CCommon.cs](../../../ERPV55/ERP_5.5/ERPClasses/Classes/CCommon.cs) 1270–1274) is `ToString("##0.##")` then parse — **do not copy**. Use `decimal.Round(..., AwayFromZero)` to match the explicit `Math.Round(..., MidpointRounding.AwayFromZero)` used after net (3035).

### Discount

Live helper: [SalesDicountHelper.CalculateDiscount](../../../ERPV55/ERP_5.5/ERPCommonUI/SalesForms/HelperClass/SalesDicountHelper.cs) 104–168 (the **active** method, not the commented block).

Edge cases that override the plan bullets:

- Sequential percent stack on `UnitPrice` for `ItemDiscount`…`ItemDiscount6`; then add `ItemDiscAmount` + `ItemDiscAmount1`.
- If `DiscMethod == "JOIN"`: **replace** that total with `(sum of all percent fields)/100 * UnitPrice + amounts`. JOIN does **not** add `CustDiscount`.
- There is **no SPLIT branch** in the active helper. `para.CustDiscount` is **unused**.
- `InvoiceEntry.calculateDiscount()` (2590–2604) still sets `CustDiscount`; the helper ignores it. **v1: port the helper as-is.** Do not re-introduce `ReCalculateItemsDiscount` JOIN/SPLIT+customer (2439–2510) unless product later asks. ErpWeb has `SaCust.DiscountMethod` and `SaCustPaymentOptions.JOIN/SPLIT` only.

### Tax mode

[InvTrxHelper.CheckSameTaxType](../../../ERPV55/ERP_5.5/ERPCommonUI/SalesForms/HelperClass/InvTrxHelper.cs) 3933–3948: all lines same `IsInclusive`. Mixed → reject ST000032.

### Tax percent

Legacy: `SaTaxGroup.Percentage`. **SaTaxGroup is not in ERPWeb. IvMSCode has no rate.**

**v1:** do not hardcode 6%. Until a rate table exists, `taxPercent = 0` and `TaxAmt = 0`. To support real SST, add `SaTaxGroup (TaxGrCode PK, Percentage decimal)` in the same idempotent script — that is a schema add, not an assumption. `AdPara.SalesTaxDec` missing → tax decimal places **2**.

### Net / tax (manual invoice, no SO)

[GetNetAmount(Amount, discount, unitPrice, taxPercent)](../../../ERPV55/ERP_5.5/ERP/SalesForms/InvoiceEntry.aspx.cs) 6391–6431:

- Inclusive + discount (and not DiscountFromGross): `totalDiscount = Round(Qty * (discount / (1 + taxPercent/100)), 2)`; `Net = Round(Qty * unitPrice, 2) - totalDiscount`.
- Exclusive + discount: `totalDiscount = Round(Qty * discount, 2)`; `Net = Round(Qty * unitPrice, 2) - totalDiscount`.
- No discount: `Net = Round(Qty * unitPrice, 2)`.
- Then `NetAmount = Round(Net, 2, AwayFromZero)` (3035). If `SaCust.DecPoint == false`, an extra 4 dp round happens first (3028–3032) then still 2 dp.

TaxAmt (3038–3048):

- Inclusive: `Round((UnitPrice - discountPerUnit) * Qty, taxDp) - NetAmount`
- Exclusive: `Round(NetAmount * taxPercent / 100, taxDp)`

Then [TaxAdaptiveRounding](../../../ERPV55/ERP_5.5/ERPCommonUI/SalesForms/HelperClass/InvTrxHelper.cs) 4174–4286 (`InvoiceEntry` calls it at 4879 with DecPoint 2, cols `TaxAmt`/`NetAmount`):

- Inclusive: if `Round(Amount+tax) != Round(Net+tax)`, add diff to **Amount** (gross).
- Exclusive: accumulate unrounded `amount * pct/100`; adjust line `TaxAmt` so rounded running total matches.

`TableRounding` (4150) delegates to `TableRoundingV3`; invoice add-line uses TaxAdaptiveRounding. Port TaxAdaptiveRounding, not TableRoundingV3, unless recon of save-final path requires both (save also calls TableRounding at 549 / 3096). **v1 save:** run TaxAdaptiveRounding after all lines, then header totals.

### Header [CalculateTotal](../../../ERPV55/ERP_5.5/ERP/SalesForms/InvoiceEntry.aspx.cs) 2376–2436

- `GrossTotal` = sum `NetAmount` where `OrderType` ≠ `EXCLD DIS`; those lines go to `ExcludedDis`.
- `ItemTaxes` = sum `TaxAmt` (getItemTaxAmount), `DiscountTaxAmt` is 0 in this method.
- `SaCust.DecPoint == true` → round taxes/gross/total to **0** dp; else **2** dp AwayFromZero.
- `TxtGrossTotal` = GrossTotal + ExcludedDis; `TxtTotal` = Gross − header DiscountAmt + ExcludedDis + Taxes (`DiscountAmt` is 0 in this method).

### Currency

[SaCurrRate](../../ErpWeb.Model/Entities/Sales/SaCurrRate.cs): `HomeCurPerUnit`, `StartDate`/`EndDate`, `Status`. Require a rate for invoice date. Legacy: non-home rate cannot be 1. Home code live: `MYR`. `LocalAmount = Round(Net * CurrRate, 2)` if the detail column is added.

### Min price / period

`IvMasPack.MinPrice` **table missing** — skip min-price check.  
`AdPara` / inventory period **missing** — default InvDate = `ICurrentDateService` today; no “before current period” reject until a period table exists.

### Item GL

Legacy requires `ItemGLCode` (ST000030). ErpWeb `IvStockMaster.SellingGlCode` exists. v1: persist if present; **do not block save** if empty (no AR/GL in v1).

---

## 8. Shipment completeness / identity / reservation

Unchanged from plan; recon confirms mapping:

- Completeness: `SUM(FrStdQty) for InvNo + SoLineNo == InvoiceDetail.StdQty` for `StockControl` lines. UI highlight is UX only.
- Identity: item, StdQty, FrWarehouse, StockControl. Change → delete SP details; no auto-FIFO on save.
- No reservation. Two NEW invoices may allocate the same piles. Post uses MI locks + `StdQty >= required`.

---

## 9. Authorization / menu / UI clone

- Add `MenuCodes.SalesInvoice = "SA_INVOICE"`.
- [menus.xml](../Menus/menus.xml): SALES currently has masters only (`SA_CUST`, …). Add Transactions → Invoice.
- Permissions: existing `ACCESS/ADD/EDIT/DELETE/POST/ROLLBACK` in [PermissionCodes.cs](../../ErpWeb.Core/Menus/PermissionCodes.cs).
- Clone chrome from `IvMiscIssueList` / `IvMiscIssue` (stock-out family). Customer lookups: `ISaCustLookupService`.
- Routes `/sales/invoices`, `/sales/invoices/{new|edit|view}/{invNo}`. CSS `sinv-`. GridKey `sa-invoice-list`. **No Post on entry.**
- List Post UI must list each invoice: Posted / Failed: reason / Not attempted.

---

## 10. Pattern doc vs this recon

[inventory-trx-pattern.md](inventory-trx-pattern.md) says new stock-out types should `DispatchAsync` into MI and add a type branch. **That applies to inventory screens.** Invoice v1:

- owns SP (`RefNo = InvNo`)
- posts via **Core-in-caller-transaction**, menu `SA_INVOICE`
- does not add a standalone SP inventory page
- does not use `IvPostingLimits = 10`

`FromBalLocId` remains the stock-out line identity (pattern table, stock OUT family).

---

## 11. Implementation checklist after this recon

1. Extract `PostInventoryMICoreAsync` / `RollBackInventoryMICoreAsync` at the line ranges above; existing MI tests (`TestD`/`TestE` in `IvMiscIssuePostingServiceTests`) must still pass.
2. Idempotent `SaInvoice` / `SaInvoiceDetail` / optional `SaTaxGroup` / filtered SP unique SQL against **ERPWeb**.
3. Numbering `SA_INV_yyyyMM` inside the save transaction.
4. CRUD + server calc from §7.
5. FIFO query from §4 + invalidate-on-identity-edit.
6. Invoice Post/Rollback: lock invoice, call Core, set invoice status, one commit.
7. Fault-injection tests (stock calc, stock update, before invoice status, during invoice status) for Post and Rollback.
8. End-to-end: create → ship → post → stock down → rollback → stock restored, SP rows remain `NEW`.
