# Delivery Order — List, Entry, Post / Rollback, and Invoice Effect

Sources:

- List: `ERP/SalesForms/DOView.aspx.cs`
- Entry: `ERP/SalesForms/DOEntry.aspx.cs`
- Post / rollback / delete / force close: `ERPCommonUI/SalesForms/HelperClass/DOTrxHelper.cs`
- Shipment: `ERPCommonUI/SalesForms/HelperClass/ShipmentHelper.cs` (`AddShipmentDO`)
- Save-and-post: `ERPCommonUI/Helper/NeedPostHelper.cs` (`PostDONoLock`)
- Invoice import / post / rollback of DO: `ERP/SalesForms/InvoiceEntry.aspx.cs`, `ERPCommonUI/SalesForms/HelperClass/InvTrxHelper.cs`
- Invoice DO lookup: `ERP/SalesForms/LookUp/DOListing.aspx`, `ERP/SalesForms/Controls/DOListing.ascx`
- Status helper: `ERPClasses/Utility/BatchStatusHelper.cs`

Screen ID: `200.3.8`. List key: `DONo`. List datasource: `vgridSaDOs`.

Use this when porting Delivery Order to another Blazor ERP app. DO is a **stock-out (SP batch) + SO fulfilment** document. Invoice later **reuses** that stock-out; it does **not** ship keep-stock lines again when `LinkDO = true`.

SO **import** (full / partial SO listing into DO) is noted where it touches post / invoice, but the line-by-line SO import path is out of scope for this document (as requested). Posting still updates SO shipped qty even for DOs created without SO import.

---

## 1. Core tables and identity

| Table | Role |
|---|---|
| `SaDO` | DO header |
| `SaDODetail` | DO lines |
| `IvTrxBatch` | Shipment batch header (`TrxType = SP`) |
| `IvTrxBatchDetail` | Shipment lots / qty (`TrxType = SP`) |
| `IvBalance` / `IvBalLoc` / `IvBalLocCost` | Stock balances (updated on **post**, reversed on **rollback**) |
| `IvTrxHistory` / `IvTrxHistoryRollback` | Stock history |
| `SaSO` / `SaSODetail` | SO header / lines (shipped qty / status updated on **DO post / rollback**) |

SP batch identity on a DO:

- `TrxType = 'SP'`
- **`InvNo` and `DONo` both = the DO number** (not the later invoice number)
- `SO_Line_No` on SP detail = **DO Line** (not SO line)
- `SO_No` = SO number if any
- `PreRevNo` = real SO line number if any
- On add-shipment, `IvTrxBatch.RefNo` starts as `"Shipment"`; on save it is replaced with the real DO number

Inventory post batch type: `CCommon.enBatchType.SP`.

`BatchStatusHelper.CheckIsPosted(DO)` is **true only when `SaDO.Status == "POSTED"`**. `PARTIAL` and `CLOSED` are **not** treated as posted for Post / Delete / Rollback gates.

---

## 2. Status machine

| Status | Meaning | Who sets it |
|---|---|---|
| **NEW** | Saved, idle, editable / deletable | Save; Refresh; Rollback |
| **OPEN** | Being edited (optimistic lock by `UserID`) | Edit mode |
| **POSTED** | Stock deducted; invoice-eligible | Post |
| **PARTIAL** | Invoiced, but DO qty not fully covered | Invoice post |
| **CLOSED** | Fully invoiced, **or** Force Close | Invoice post / Force Close |

```
NEW ──edit──► OPEN ──save / cancel──► NEW
 NEW ──post──► POSTED ──invoice (full)──► CLOSED
                 │
                 └──invoice (partial qty)──► PARTIAL
 POSTED ──rollback──► NEW   (SP rows deleted; must re-add shipment)
 POSTED ──force close──► CLOSED   (no stock reverse; cannot invoice)
```

Invoice lookup only offers DOs with `Status in ('POSTED','PARTIAL')`. After Force Close or full invoice, the DO disappears from that list.

---

## 3. DO List (`DOView.aspx.cs`)

Default entry page: `DOEntry.aspx`.

Buttons: NEW, DELETE, PRINT, REFRESH, POST, ROLLBACK, FORCE CLOSE, EXPORT, ATTACHMENT.

Grid columns: DO Date, DO No, Inv No, Cust PO (hidden by default), Cust Code, Cust Name, Ship To (`ShipName`), Remarks, Area Code, Status, Salesman, Print Counter, User, Created Date, Updated User, Updated Date.

Default combo startup: indexes `(0,1)`, `(1,3)`, `(2,5)`. Report combo visible.

### 3.1 New

- Right: New.
- Opens `DOEntry.aspx?Type=NEW`.

### 3.2 Edit

- Right: Edit.
- Load `vgridSaDOs` for that `DONo`.
- **OPEN** → only the same `UserID` may continue; otherwise *Access denied. Document is opened by {UserID}*.
- Any status other than NEW / OPEN → *Items must be 'NEW' Status to edit!*
- Opens `DOEntry.aspx?ID={DONo}&Type=EDIT&DOCType=INV&Comp=&Branch=`.

### 3.3 View

- Right: Access. No status gate.
- Opens `DOEntry.aspx?ID={DONo}&Type=VIEW&DOCType=INV&Comp=&Branch=`.

### 3.4 Delete

- Right: Delete.
- Blocked if any selected DO is already POSTED (*This Delivery Order had been POSTED. Access denied!*).
- Each selected DO must be **NEW**; otherwise *Items must be 'NEW' Status to be Deleted!*
- Calls `DOTrxHelper.DeleteDO(DONo)`:

  1. Load full `SaDO` + `IvTrxBatch` / `IvTrxBatchDetail`.
  2. Delete matching `SaDO` header row.
  3. Mark matching `SaDODetail` rows deleted **in a local table**.
  4. If SP details exist for that DO, delete the related `IvTrxBatch` **header**.
  5. Persist only `SetSaDO` + `SetIvTrxBatch` (header tables).
  6. Audit `DO HDR` / `DO DTL` (`AuditLogDel`).

  **Porting note:** `SaDODetail` and `IvTrxBatchDetail` deletes are **not** written through adapters in this path. Confirm whether DB cascade handles them before copying this as-is.

### 3.5 Refresh (unlock)

- Access-right check (generic).
- Each selected DO must be **OPEN**; otherwise *Cannot refresh batch status {Status}*.
- `DOTrxHelper.Refresh`: for rows currently OPEN, set `Status = NEW`. Persist `SaDO` only.
- Web.config `RefreshCheckUser` exists but the “cannot refresh another user” block is commented out.

### 3.6 Print

- Right: Print.
- If the user’s `AdUser.PrintControl = true`, `PrintChecking` requires every selected DO to be **POSTED** or **CLOSED**; else *Please check status before you print.*
- Report from `AdReportID` where `ModuleName='DOView'` and `Name={print type}`. If `Condition1 = devreport`, use DevExpress path. Else default view `vgridSaDOPrint`, file `xrepDO.repx`.
- Parameters: `UserID`, `UserImage` (`Admin/Images/{Comp}-{Branch}-{UserID}.png`).
- Filter: `Where DONo in (...)`.
- Increments `SaDO.PrintCounter` (+1 per selected DO) inside a SQL transaction.
- Special type `LOCAL DO & LAMPIRAN GPB`: two reports (`rptDeliveryOrder` + `rptLampiranGPB_DO`, view `vDOGPB`).
- Attachment uses the same report prep (`OnPreparePrintDev`) and returns a session guid for email.

### 3.7 Post (list)

1. Right: Post.
2. `TrxPostingHelper.CheckAnyActiveTrx()` (wait up to 90s).
3. `TrxPostingHelper.SetTrxPosting("DO", "DO", guid)` — exclusive lock.
4. None of the selected DOs may already be POSTED.
5. **Maximum 3 DOs** per click (*Maximum 3 items per transction!*).
6. For each DO: `DOTrxHelper.StartPost(dono, Comp, Branch, Loc, UserID, screenID "200.3.8")`. On first failure, unset lock and stop.
7. Always `UnSetTrxPosting` in `finally`.
8. Rebind grid.

### 3.8 Rollback (list)

1. Right: Rollback.
2. Same trx lock (`DO` / `DO`).
3. **Every** selected DO must be POSTED (*All items must be in 'POSTED' Status to be Rollback!*).
4. **Maximum 3**.
5. For each: `DOTrxHelper.Rollback(...)`. On first failure, unset lock and stop.
6. Always unset lock; rebind.

### 3.9 Force Close

- Right: **Delete** (changed from Rollback; task 260622090493).
- Month-end (`AdPara.CurrentYear` / `CurrentMonth` vs `DODate`):
  - If DO date is in a **closed** period **and** status is **not** POSTED → *Month End Already Done For DO No {dono} !*
  - A **POSTED** DO **can** be force-closed even after month-end.
- Then: must be POSTED (`CheckIsPosted`); else *All items must be in POSTED Status to Force Close!* (task 260627091568).
- `DOTrxHelper.ForceClosed`: set `CLOSED`, **delete SP batch header + detail**, **no inventory reverse**, **no SO qty reverse**.

### 3.10 Other list rules

- `AdPara.SelfViewEdit`: if true **and** the user is **not** in `AdUserGroupDefault`, the grid is filtered to `UserID == current user`.
- Hidden field `hdBatchType` is set to `"GR"` on load (legacy; posting still uses DO).

---

## 4. Post / Rollback internals (`DOTrxHelper`) — the real biz engine

### 4.1 Data loaded for post / rollback

`OpenTable` / `OpenPostTable` load (scoped by DONo where possible):

- `SaDO`, `SaDODetail`
- SP `IvTrxBatch` / `IvTrxBatchDetail` where `TrxType='sp'` and (`DoNo` or `Invno` = DONo)
- `IvBalance`, `IvBalLoc`, `IvBalLocCost` for those item codes
- `IvTrxHistory`, `IvTrxHistoryRollback` (SP, same DO/Inv filter)
- Related `SaSO` / `SaSODetail` (SO numbers found on DO details)
- `IvType` keep-stock join (`IvMas.IType` → `IvType.KeepStock`)
- `AdPara`, `AdTrackNum`, users, rights

Keep-stock lookup used on rollback: `SaDODetail` left join `IvMas` left join `IvType`.

### 4.2 Post — `StartPost`

**Pre-checks (all must pass, in this order):**

1. **Warehouse tally** (`CheckBatchInvWarehouse`)  
   For each DO line, SP dtl must match: `TrxType=SP`, `InvNo=DONo`, `ICode`, `SO_Line_No` = DO `Line`.  
   `FrWarehouse` (batch) must equal `SaDODetail.FrWarehouse` (case-insensitive).  
   Fail: *Some item shipment warehouse is not tally with do detail warehouse…*

2. **Shipment date** (`CheckTrxDtTimeBatch`)  
   `IvTrxBatch.TrxDtTime` (where `RefNo = DONo`) must equal `SaDO.DODate`.  
   Fail: *Some shipment date is not updated, please add shipment for each of your selected item!*

3. **Shipment complete** (`CheckAllItemAvailableForShipment`)  
   For each DO line:
   - Resolve `IvType.KeepStock` by `ICode`.
   - If **KeepStock**: SP rows must exist for `DONo + SO_Line_No`; `SUM(FrStdQty)` for `DONo + SO_Line_No + ICode` must **equal** line `StdQty` (4 dp, **exact**, not “at least”).
   - If item type row not found: still require an SP row for that line.
   Fail: *some item have not available for shipping…*

4. **SO shipped qty** (`UpdateShippedQtyInSalesOrder`)  
   For each DO line that finds a matching `SaSODetail` (`SONo` + `Line=SOLine` + `CustRel`):
   - `UpdatedShippedQty = DO.Qty + OriginalShippedQty`
   - `BalanceQty = OrderQty + ReturnQty - UpdatedShippedQty`
   - Fail if `BalanceQty < 0`: *Shipped Qty cannot more then Sales Order Quantity!*
   - **No rounding** on write (comment: qty in entry must not auto-round).
   - Lines without SO are skipped (no matching SO dtl).

**Then in memory (before persist):**

- `SaDO.Status = POSTED`, `updateduid = user`, `Updated = now`
- SO header (`updateSOStatus`):
  - For each distinct SO + CustRel on the DO:
    - `sumOrderQty = Sum(OrderQty)`, `sumShippedQty` is actually `Sum(BalanceQty)` of those SO details (variable name is misleading).
    - If remaining `Sum(BalanceQty) > 0` → SO `Status = SHIPPED`
    - Else → SO `Status = CLOSE`
    - Stamp `UpdatedUID` / `Updated`
- `CPosting.PostInventoryTransaction(SP, BatchNo, …)` deducts loc / company balance / loc cost, writes history.  
  BatchNo from existing SP dtl; if none, `BatchNoHelperEx.RetrieveAndUpdateBatchNo()` (should not happen if shipment check passed).
- One SQL transaction via `UpdateRec`: SaDO, SaSO, SaSODetail, IvTrxBatch, IvTrxBatchDetail, IvBalLoc, IvBalance, IvTrxHistory, IvTrxHistoryRollback, AdPara, AdTrackNum, IvBalLocCost.
- Success message: *Item(s) posted!*

If inventory post fails, return false **before** the SQL commit (in-memory SO/DO status changes are discarded).

### 4.3 Rollback — `Rollback`

**Gates:**

- Load same tables as post.
- Empty batch / batch dtl sets a message (*No shipment transaction detail found…*) but does **not** return immediately.
- **Month-end is hard**: DO date year/month cannot be before `AdPara.CurrentYear/CurrentMonth`. Unlike Force Close, POSTED does **not** bypass this.  
  Fail: *Month End Already Done For DO No {dono} !*

**Then:**

1. `RollbackShippedQtyInSalesOrder`:
   - `ShippedQty = OriginalShippedQty - DO.Qty`
   - `BalanceQty = BalanceQty + DO.Qty`
   - Written with `CCommon.RoundingToDouble(..., 2)` — **asymmetric vs post**, which does not round.
2. `SaDO.Status = NEW` (+ UpdatedUID / Updated).
3. `updateSOStatus(..., "SHIPPED")` again (same CLOSE-if-no-balance rule).
4. Inventory reverse **only if any line on that DO is KeepStock**:
   - `CPosting.RollbackInventoryTransaction(SP, …)`
   - Delete matching `IvTrxHistory` rows (`BatchNo` + `dono`)
   - Delete SP `IvTrxBatch` where `BatchNo` + `RefNo = DONo`
   - Delete SP `IvTrxBatchDetail` where `BatchNo` + `DONo` (task 260716091903)
   - If keep-stock but no batch header: *No shipment detail found for DONo …*
   - If keep-stock but no history: *Can not find Transaction history record…*
5. Same `UpdateRec` persist.
6. Success: *Item(s) Rollback!*

After rollback, **shipment is gone**. User must Add Shipment again before re-post.

If **no** keep-stock lines, inventory rollback is skipped, but status still goes NEW.

### 4.4 Force Close vs Rollback vs Invoice CLOSED

| | Rollback | Force Close | Invoice post (full) |
|---|---|---|---|
| Stock | Restored | **Not** restored | **Not** shipped again for `LinkDO` |
| SO qty | Reversed | **Not** reversed | Skipped if `LinkDO` |
| Shipment SP | Deleted | Deleted | Left as the DO’s SP |
| Status | NEW | CLOSED | CLOSED / PARTIAL |
| Can invoice after | After re-post | **No** | Already invoiced |

`ForceClosed`:

- Set `SaDO.Status = CLOSED`.
- Delete all `IvTrxBatchDetail` for that DONo and the related `IvTrxBatch` header(s).
- Persist via `UpdateRecForceClose` (`SaDO` + `IvTrxBatchDetail` + `IvTrxBatch` only).
- Message: *Item(s) Closed!*

`ClosePostedDO` exists (status CLOSED + month-end check + full `UpdateRec`) but the **list button uses `ForceClosed`**, not this method.

### 4.5 Refresh / Delete recap

- **Refresh:** OPEN → NEW on `SaDO` only.
- **Delete:** see §3.4.

---

## 5. DO Entry (`DOEntry.aspx.cs`) — standalone (not SO import)

### 5.1 Sessions / query

| Session | Contents |
|---|---|
| `DODtl` | `SaDODetail` working table |
| `BatchHdrDO` | `IvTrxBatch` |
| `BatchDtlDO` | `IvTrxBatchDetail` |
| `DOHDR` | `SaDO` header |
| `SADOSALES_ITEM` / `SADOSALES_UOM` | Per-customer item / UOM price cache |
| `FULLSO` / `PARTIALSO` | SO listing (import; out of scope) |

Query: `Type` = NEW / EDIT / VIEW / COPY, `ID` = DONo.

`OpenTable`:

- Header: `SaDO` for ID (or `1<>1` for new).
- Details: `SaDODetail` for ID, or session, or empty for NEW.
- Batch: SP rows where `DoNo` or `Invno` = ID.

`DOSaveHdrOnly` from web.config is copied to client hidden `SAVEHDRONLY`.

Shipment control (`SPShipment`): `DocType = "DO"`, company / branch / loc / user from session.

### 5.2 Modes

**NEW**

- `DODate = now`, `DONo = AUTO`, UI status OPEN, `DOType` from `GetDefaultPrefix` (web.config `DOPrefix`, else `"DO"`).
- Clear lines.

**EDIT**

- `batchNoHelper.CheckBatchNo(DONo, DONo, false)` stored in `hdBatchNo`.
- `updateStatus(ID, "OPEN")` — optimistic lock.
- That update loads `SaDO where status <> 'POSTED'`. If `BatchStatusHelper.IsBatchPosted("Select Status from SaInvoice where DONo='…'")` is true, **do not overwrite** status (invoice already posted against this DO).
- UI `DOHeader.Status = "OPEN"`.

**Cancel Save**

- If not NEW: restore `Status = NEW`. Then client cancel.

**VIEW**

- Item / header / customer all read-only; action buttons off.

**COPY**

- New AUTO / OPEN / today’s date. Line copy-from-source is largely commented out.

### 5.3 Bind existing header (`BindData`)

From `SaDO`:

- Date, number, status (COPY overrides to today / AUTO / OPEN).
- Prefix, customer code/name.
- **Shipping UI** ← `ShipName`, `ShipAddress1–4`, `ShipCity`, `ShipState`, `ShipPostalCode`, `ShipCountry`, `ShipTel`, `ShipFax`.
- **Billing UI** ← `InvName`, `InvAddress1–4`, `InvCity`, `InvState`, `InvPostalCode`, `InvCountry`, `InvTel`, `InvFax`.
- Tax group, ship via, currency, contact, pay code, remarks, departure, destination, vessel, Ref1–4, salesman, project, cust discount.

Do **not** re-run `AppInvoice` / `AppShip` when opening an existing DO.

Tax group percentage loaded from Acc `SaTaxGroup` (active, company+branch).

### 5.4 Customer select (`LoadCustomerInfo`) — AppInvoice / AppShip

Same rule as Invoice / SO. Flags live on `SaCust`. Applied **only on customer select**.

Control naming on DO is the same trap as Invoice Entry: map by **meaning**, not control name.

| Flag | Value | Billing (`Ship*` controls → `SaDO.Inv*`) | Shipping (`Deliver*` controls → `SaDO.Ship*`) |
|---|---|---|---|
| **AppInvoice** | true | Registered (`CustName` + `Address1–4` + city/state/postal/country/tel/fax) | — |
| **AppInvoice** | false | Dedicated `Inv*` | — |
| **AppShip** | true | — | Registered |
| **AppShip** | false | — | Dedicated `Ship*` |

The two flags are independent.

Also copies: `ContactPerson`, `Currency`, `PayCode`, `TaxGroup`, `SRepCode`, `InvPrefix`, `DiscountSeq`, `DiscountMethod`, `GroupDiscount`.  
`CurrRate` forced to **1**.

If `AdPara.PrefixControl = 1`, `DOHeader.DOPrefix = customer InvPrefix`.

Clears per-customer item-price session. Sets `SOCUSTCODE`. **Then `DeleteAllItem()`** — deletes all current DO lines **and** matching SP batch rows / empty headers.

Do **not** re-apply these flags when:

1. Opening an existing DO
2. Copying addresses from SO (import path)
3. User later picks a ship-to (if that control exists)

### 5.5 Prefix / warehouse defaults

- `AdUserDefault` where `PageCode = 'DO'` and current user: `Prefix`, `Warehouse`.
- Else web.config `DOPrefix` (default `"DO"`). Blank prefix on save is forced to `"DO"`.
- Item warehouse priority on product load:
  1. User default warehouse
  2. Item `DefWarehouse`
  3. If `AccBranch.SalesWH` is non-empty, it **overwrites** the warehouse

`AdPara.UseItemMaster` drives price lookup (1 master / 2 group / 3 cust product / 4 group-ex).  
`AdPara.UseWeight = false` hides catch weight.  
Default line GL = `AdPara.SalesGLCode1`.  
User `SellPriceView` toggles price list visibility.

### 5.6 Product load (`LoadProductInfo`)

Reset product fields, load `GetCustItems` (cached per customer; mode 4 always re-queries by ICode). Bind UOM list.

Sets: description, selling/cost price, UOMs, cust item code, pack size, qty on hand, tax group, catch weight, warehouse (see priority above).

UOM change: `StdQty = OrderQty * StdCustPSize` from session UOM table.

Stock-per-date: `QtyOnHandPerDateDataBind(DODate)`.

### 5.7 Add line (`AddRow`) — standalone

Validations:

- Item code required.
- Customer required.
- If `SalesOrderNo` is filled **and** `AdPara.InvFullControl = false`:
  - SO line required.
  - `checkqty`: DO order qty cannot exceed SO `BalanceQty` for that SO + current cust rel + line + ICode.
- Extra used-qty vs **other DOs** for same customer + SO + CustRel + SOLine (excludes current DO). If remaining available &lt; requested, qty is **capped** to remaining (does not hard-fail if remaining &gt; 0).

`StdQty` cannot be 0 after that (*This Item Code does not have any quantity!*).

Line fields written:

`DONo, Line, SONo, CustPO (upper), CustRel (default 1), SOLine (default 1), ICode, IDesc (upper), CustICode (upper), Qty, StdQty, WtQty, UnitPrice, SellingUOM, StdUOM, WtUOM, StdPSize, OrderType, Remarks (upper), InvDesc, ItemGLCode, FrWarehouse, IsInclusive, TaxGroup, TaxAmt = 0`.

GL: `IvMas.SellingGLCode` if set, else `DOItemInfo.ItemGLCode` (`AdPara.SalesGLCode1`).  
Tax: line tax group; if blank and customer `Taxable`, use customer `TaxGroup`.  
On **edit existing line**, restore `IsInclusive` from the row (do not reset to 0).

New line vs update: match `DONo + Line`. Next line = `MAX(Line)+1`.

### 5.8 Barcode add (`AddRowBarcode`)

- Customer required.
- Lookup `IvMas` by barcode; fail if not found.
- If same ICode already on this DO: increment `Qty` by 1 on that row; else new row.
- `StdQty = qty * StdPackSize`.
- `SONo = ""`, `CustRel = 1`, `SOLine = 1`.
- Warehouse: user default, else item `DefWarehouse`.
- Tax fallback same as AddRow.

### 5.9 Edit / delete line

**Edit (`ShowSelections`)**

- If `AdUser.FromSOEditControl` and the line has `SONo` → *You are not allowed to edit Item From Sales Order.*
- Load all line fields into `DOItemInfo`.
- `CheckShipment`: if SP dtl exists for this batch + ICode + `SO_Line_No` = DO Line, **qty controls are disabled**.

**Delete (`DeleteItem`)**

- Requires grid selection.
- Soft-delete matching `SaDODetail` and SP dtl (`DONo + ICode + SO_Line_No`).
- `DeleteEmptyHeader`: if no remaining dtl for that BatchNo, delete batch header (do **not** `AcceptChanges` on dtl — task 241007048122).

**Customer change (`DeleteAllItem`)**

- Delete all current DO lines and SP dtl for this DO; delete orphan batch headers.

### 5.10 Grid display (`dispShipQtyInv`) — mutating bind

Working copy of lines for this `DONo`. Adds computed `ShipQty`:

- `SUM(FrStdQty)` for `DONo + TrxType=SP + ICode + SO_Line_No`.
- If that total **equals** line `StdQty` (4 dp) → show that sum.
- If **not equal** → show **0** and **`Delete()` the batch detail row**.

Display is not read-only. `ShipQty = 0` is painted **red**. Barcode column filled from `IvMasBL.GetBarcode`.

### 5.11 Credit limit (save)

- Off if `AdPara.CheckCreditLimitDO = false` (missing column / error → **on**).
- Bypass: grid callback `BYPASS` → `SalesCommonHelper.CheckByPass(password, cust, company, branch)`; on success save with `byPass = true`.
- Limit = `SaCust.CreditLimit` (0 = unlimited).
- Balance = Acc `DebtorAgingInfo.Total + advPayment` (0 if no row).
- This-document amount = Σ over current lines:
  - If line has `SONo`: `DO UnitPrice * Qty` (only if SO dtl item exists)
  - Else: `IvMas.SellingPrice * Qty`
- Fail if `(totAmount + bal) > crLimit`: *Amount Cannot More than Credit Limit!* (`cpTermErrMsg`).
- Missing customer: *CustCode or similar to it not found*.

### 5.12 Save (`Save` / `SaveDO`)

Outer lock: `TrxPostingHelper` `SAVEDO` / `SAVEDO` (wait + set + unset in `finally`).

1. Lines required unless web.config `DOSaveHdrOnly = TRUE` (*No Data Added!*).
2. Credit limit unless bypass / setting off.
3. `IsValidShipQty`: for each line with SP rows, `SUM(FrStdQty)` for that batch + ICode + line must **not exceed** `StdQty` (4 dp). Under-ship is **allowed at save**; post later requires **exact** match for keep-stock. Fail: *Shipment Quantity is more than Standard Quantity*.
4. Date vs current period (`CCommon.ValidateDate` vs `AdPara.CurrentMonth/Year`).
5. Prefix blank → `"DO"`.
6. If `DONo = AUTO`:
   - `CCommon.GenerateAutoNumberWithDateEx(prefix, AdSmNumDate, AdPara, year, month)`
   - `checkAutoNumberingDO`: fail if number already exists
   - `CCommon.TrackNumber(AdTrackNum, prefix, number, "Created", user)`
   - After header filled: `UpdateSequenceNoWithDateEx`; fail *Numbering Date Not Set Yet!*
7. Never persist `DONo = AUTO`.
8. `UpdatedDONoToBatchDetail`: set SP dtl `DONo` and `InvNo` to the real number; set `IvTrxBatch.RefNo` from `"Shipment"` to the DO number.
9. Stamp `DONo` onto all current detail rows.
10. Header fields (addresses **uppercase**):

| UI | `SaDO` |
|---|---|
| Billing (`Ship*` controls) | `InvName`, `InvAddress1–4`, `InvCity`, `InvState`, `InvPostalCode`, `InvCountry`, `InvTel`, `InvFax` |
| Shipping (`Deliver*` controls) | `ShipName`, `ShipAddress1–4`, `ShipCity`, `ShipState`, `ShipPostalCode`, `ShipCountry`, `ShipTel`, `ShipFax` |
| Customer name | `CustName` |
| Tax / pay / currency / ship via / remarks / vessel / refs / salesman / contact / project | matching columns |
| Company / Branch / Location | session |
| Prefix | resolved prefix |
| Status | **NEW** (not OPEN) |

NEW / COPY: `UserID` + `Created`. EDIT: `UpdatedUID` + `Updated`.

11. Block if already POSTED (*This invoice had been posted. Access denied!* — message still says invoice).
12. Persist `UpdateRec`: `SaDO`, `SaDODetail`, `AdSmNum`, `AdSmNumDate`, `IvTrxBatch`, `IvTrxBatchDetail`, `AdTrackNum`.
13. If `needPost = yes` **and** there is at least one detail row: `NeedPostHelper.PostDONoLock(...)` (Post right required; **no extra trx lock** because Save already holds `SAVEDO`). If post fails, **save already committed**.
14. Audit `DO HDR` / `DO DTL`.
15. `ResetControl` (clears header, customer, items, batch sessions).

`NeedPostHelper.PostDONoLock`:

- Must have Post right, else *saved. You dont have the access right to POST.*
- If already POSTED: *had been POSTED. Access denied!*
- Else `StartPost`. Success uses `cpPost`; failure keeps `cpSaved` with *saved. Fail to post. {msg}*.

### 5.13 Shipment from entry

**Add all (`AddShipment`)**

- Fail if no lines.
- `ShipmentHelper.AddShipmentDO`:
  - `CheckBatchNoEx(Invno, Invno)` with `Invno = DONo`. Fail if helper error.
  - `DeleteDetailEx` then rebuild.
  - For each current line: consume `IvBalLoc` FIFO by `FrWarehouse` until `StdQty` (and catch-wt) filled; split lots.
  - Skip keep-stock consume for SERVICE / non-keep-stock.
  - Failures: no loc qty (`ST000051`), warehouse mismatch (*Warehouse not found!*), insufficient (`ST000059`). Partial success allowed (*Some Items Fail to Add Shipment!*).
  - SP dtl fields: `TrxType=SP`, `DONo` + `InvNo` = DO number, `SO_Line_No` = DO Line, `SO_No`, `PreRevNo` = SO line, lot, WH, loc, `FrStdQty` / `FrWtQty`, `Cost` = DO unit price, remarks, `IStatus`.
  - Batch header: `TrxType=SP`, `BatchStatus=NEW`, `TrxDtTime=DODate`, `RefNo="Shipment"` until save, company/branch/loc/user.
  - Cost price on dtl set to 0 after add.

**Edit one line (`EditShipment`)**

- Fail if no lines.
- `InvNo` on shipment UI is forced to **DONo**.
- Loads `SPShipment` (qty = StdQty, warehouse from line).

**View shipment**

- VIEW + POSTED: load from posted trx history.
- Else: load from batch-dtl session.

**Check any shipment**

- Client flag YES/NO from `CheckBatchNo("", DONo)`.

---

## 6. How this affects Invoice (do not skip)

### 6.1 Which DOs can be billed

`DOListing` SQL:

```sql
SELECT DODate, DONo, InvName, Remarks
FROM SaDO
WHERE CustCode = @custcode
  AND Status IN ('POSTED','PARTIAL')
ORDER BY DODate DESC, DONo DESC
```

Force Close or full invoice → `CLOSED` → **not listed**.

### 6.2 Invoice post gate

`InvTrxHelper.isDOPosted()`:

```sql
SELECT Status FROM SaDO
WHERE DONo IN (
  SELECT DISTINCT DONo FROM SaInvoiceDetail
  WHERE LinkDO = 'True' AND InvNo = '{inv}'
)
AND Status <> 'POSTED'
```

If any row returns → invoice post fails: *Some of the DO is not yet posted, please check your Delivery Order!*

Note: `PARTIAL` / `CLOSED` also fail this `<> 'POSTED'` test. A PARTIAL DO can appear in the lookup (§6.1) but invoice **post** still requires the linked DO status to be exactly POSTED. Porting should preserve this unless product intentionally changes it.

### 6.3 Import DO → invoice lines (`AddRowDOInvDtl`)

- Must have a DO number.
- Mix rule: if the invoice already has keep-stock **or** SO-linked (`LinkDO=0 AND SONo<>''`) lines, **cannot add DO** (`ST000013`).
- Cannot mix inclusive / exclusive tax types (`CheckSameTaxType`).
- Duplicate same DO + ICode + Qty + remarks: skip with *already added*.
- Copies **every** DO detail: qty, std/wt qty, UOMs, warehouse, tax group, inclusive flag, GL, SO keys, cust PO, remarks, inv desc, dept, work order.
- **`LinkDO = true`**, `DONo`, `DOLine`.
- Price / item discounts from **SO line** if present, else `IvMas.SellingPrice`. Tax / net / discount computed like a normal invoice line (inclusive handling, `DecPoint`, `SalesTaxDec`).
- If DO GL blank: `IvMas.SellingGLCode`, else `AdPara.SalesGLCode1`.
- Header: remarks, Ref1–3 from DO; salesman from DO.
- `HdrDONo` accumulates comma-separated DO numbers (`updateDONo`).
- Addresses from DO (`DOShipAddress`): shipping ← `SaDO.Ship*`, billing ← `SaDO.Inv*`. Do **not** re-run AppInvoice / AppShip.
- Classification from `IvMas`.
- Whole-document tax rounding after each add.

Qty caps when `InvFullControl = false`:

- With SO: `checkDOQty(DO, ICode, SO, SOLine, CustRel)` — invoice qty cannot exceed DO qty.
- Without SO: `checkDOQty(DO, ICode, DOLine)` — same, by DO line.

Cannot add / edit shipment on a `LinkDO` line: *This Item from DO, Shipment added.*

Manual invoice line with `InvItemInfo.DONo` set also stamps `LinkDO=true` and `DONo`.

### 6.4 Invoice post vs DO (`InvTrxHelper.Post`)

For **`LinkDO = true` lines**:

- **Skip** shipment completeness (`CheckAllItemAvailableForShipment` only checks `LinkDO == false`).
- **Skip** SO `ShippedQty` update (`UpdateShippedQtyInSalesOrder` only when `LinkDO == false`).
- Still require linked DOs to be POSTED (`isDOPosted`).

Invoice still:

- Warehouse / shipment-date / currency checks for **non-LinkDO** keep-stock lines
- Posts its own SP only when a batch exists for the **invoice** number
- Posts AR / GL (`SalesPostTotal` vs detail)

Then `updateDO(invno)`:

1. For each invoice detail, write `SaDODetail.InvNo = this invoice` for that DO.
2. If **no** remaining DO lines with `InvNo is null`:
   - `InvQty = SUM(invoice Qty)` for this inv + DO
   - `DOQty = SUM(DO Qty)` for that DO
   - If `InvQty < DOQty` and current status is not already PARTIAL → `SaDO.Status = PARTIAL`
   - Else → `CLOSED`

Keep-stock **LinkDO lines are not posted to inventory again**. Stock already left on DO post.

### 6.5 Invoice rollback vs DO (`RollbackDO`)

- Distinct `DONo` from `SaInvoiceDetail` for that invoice.
- Clear `SaDODetail.InvNo` (set null).
- Set `SaDO.Status` back to **POSTED** (invoice-eligible again).
- SO qty reverse **only** for `LinkDO = false` invoice lines.

---

## 7. AdPara / config / user switches

| Setting | Effect |
|---|---|
| `UseItemMaster` | Price source (1 master / 2 group / 3 cust / 4 group-ex) |
| `UseWeight` | Hide catch weight when false |
| `SalesGLCode1` | Default sales GL |
| `PrefixControl = 1` | DO prefix from customer `InvPrefix` |
| `CheckCreditLimitDO` | Credit check on save (default on) |
| `InvFullControl` | If false, enforce SO/DO qty caps |
| `CurrentMonth` / `CurrentYear` | Period for save date, rollback, force close |
| `SelfViewEdit` | Own-docs-only on list (non-default groups) |
| web `DOSaveHdrOnly` | Allow header-only save |
| web `DOPrefix` | Default prefix (`DO`) |
| `AdUser.PrintControl` | Print only POSTED / CLOSED |
| `AdUser.SellPriceView` | Price list visible |
| `AdUser.FromSOEditControl` | Block edit of SO-sourced lines |
| `AdUserDefault` PageCode `DO` | Default prefix + warehouse |
| `RefreshCheckUser` | Intended user check on refresh (currently unused) |

---

## 8. Implementation notes (easy to get wrong when porting)

1. SP batch uses **`InvNo = DONo`**. Invoice `LinkDO` lines **do not** get a second SP. `SO_Line_No` on SP is **DO Line**.
2. Post requires **exact** ship qty vs `StdQty` for keep-stock; save only forbids **over**-ship.
3. Grid bind (`dispShipQtyInv`) can **delete** mismatched shipment rows.
4. Save-then-post: save commits even if post fails.
5. Rollback **deletes shipment** and restores stock / SO qty. Force Close **closes without restoring stock or SO qty**.
6. Invoice listing excludes `CLOSED`. Force Close therefore **blocks invoicing**.
7. SO qty is updated on **DO post**, not invoice post, when the invoice is from DO (`LinkDO`).
8. Addresses: AppInvoice / AppShip only on **customer change**; existing DO / copy-from-DO uses stored `Inv*` / `Ship*`.
9. Optimistic lock is status OPEN + UserID, released by save (NEW), cancel (NEW), or list Refresh.
10. List Post / Rollback: max 3, exclusive `DO` trx lock. Entry save uses `SAVEDO` lock; save-and-post does **not** take a second `DO` lock.
11. Delete helper currently may leave orphan details — verify DB cascade.
12. `isDOPosted` on invoice post uses `Status <> 'POSTED'`, so PARTIAL/CLOSED linked DOs also block invoice post.
13. Rollback SO qty rounds to 2 dp; post does not round. Keep that asymmetry unless changing both.
14. DO list Post message on already-posted is *Access denied*; entry save uses the invoice wording.

---

## 9. Out of scope here (SO import into DO)

These exist on `DOEntry.aspx.cs` and are **not** specified line-by-line in this document:

- `AddSOItems` / `AddSOPartialItems` / `AddRowINVSODtl` / `AddRowINVPartialSO`
- `SOShipAddress` / `CopyHeaderRemark` (copy SO billing + shipping + remarks / refs / ship via / pay / tax / project / order-by)
- SO listing session `FULLSO` / `PARTIALSO`

Posting / rollback **still** updates `SaSO` / `SaSODetail` for any DO line that has `SONo` + `SOLine` + `CustRel`, including standalone-typed SO keys.

If a follow-up spec is needed, it should cover full vs partial SO import, available qty, and header copy — still bound to the post / invoice rules above.
