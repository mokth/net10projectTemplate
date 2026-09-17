# Sales "Item Family" Reference Tables — Logic Spec

> ## ⚠️ PARTLY SUPERSEDED (2026-09-17)
>
> This document is a study of the **legacy WebForms adapters** and the schema as it stood *before*
> the pricing upgrade. It remains useful for the legacy behaviour it records, but the following parts
> no longer describe the shipped system — read `docs/sales-pricing-engine.md` first:
>
> | Here | Shipped behaviour |
> |---|---|
> | `IvCustPrice` PK = `(CompanyCode, CustPriceCode, ICode, UOM)` | **surrogate `Id int IDENTITY`** plus a separate unique index, because two quantity bands may share one `ValidFrom` |
> | `IvCustPrice.CustPriceCode nvarchar(10)` | `nvarchar(20)`, matching `SaCust.CustPriceCode` |
> | `IvCustPrice` has no dates / bands / currency | gained `ValidFrom`, `ValidTo`, `MinQty`, `MaxQty`, `CurrencyCode` |
> | the resolution chain lists **three** levels | **four**: Customer Item → Cust List → **Group List** → Item Default |
> | prices are tax-exclusive with no stated boundary rule | grossed up once, in the orchestrator, before the discount is resolved |
> | no per-company configuration | `Company.SalesPriceMethod` makes sources eligible per company |
> | no record of where a price came from | `PricingSource`/`PricingRef` (+ override columns) on all four detail tables |
>
> The legacy sections below are deliberately **left as written** — they document what the old adapters
> did, and rewriting them would destroy that record.

Source of truth for porting the 5 Customer/Price/Discount reference screens from WebForms to the Blazor ERP.

Studied from (this repo `ERPV55/ERP_5.5`):

| Area | File |
|---|---|
| Customer product entry | `ERP/SalesForms/Master/CustomerItems.aspx(.cs)` |
| Customer product grid control | `ERPCommonUI/SalesForms/CustProfile/CustProfItem.ascx(.cs)` |
| Price group line entry | `ERP/SalesForms/Master/CustPriceGroupItems.aspx(.cs)` |
| Price group line control | `ERPCommonUI/SalesForms/Controls/CustPriceGroupItemInfo.ascx(.cs)` |
| Price group header entry | `ERP/Inventory/MasterItem/MasterItemCustPriceGroup.aspx(.cs)` |
| Group discount entry | `ERP/SalesForms/Master/CustomerGroupDiscount.aspx(.cs)` |
| Item discount entry | `ERP/SalesForms/Master/CustomerItemDiscount.aspx(.cs)` |
| Item discount read-only | `ERP/SalesForms/Master/CustItemDiscList.aspx(.cs)` |
| Lists | `ERP/SalesForms/ItembyCustViewEx.aspx.cs` (200.1.2), `GroupDiscountViewEx.aspx.cs` (200.1.9), `ItemDiscountViewEx.aspx.cs` (200.1.10), `CustPriceGroupItemsView.aspx.cs` (200.1.24) |
| Grid shell | `ERP/Controls/ItemGridEntry.ascx(.cs)` |
| Delete / refresh / audit helper | `ERPCommonUI/SalesForms/HelperClass/SalesMasterHelper.cs` |
| Transactions | `ERPClasses/Classes/CAdapter.cs` — `SetSaItembyCust`, `SetCustPrice`, `SetCustPriceGroup`, `SetSaDisGroup`, `SetSaDisGroupItem`, `SetSaDisCust` |
| Column types | `ERPClasses/BL/ErpDataClasses.dbml`, `ERPClasses/BL/ERPListViewData.dbml` |
| Read models | views `vgridCustomerProduct`, `vgridCustPriceGroup` |
| Downstream consumers | `ERPClasses/BL/SaCustomerBL.cs`, `ERPCommonUI/SalesForms/HelperClass/SalesDicountHelper.cs`, `ERP/Service/ListItembyCust.aspx.cs`, `ERP/DataService.cs` |
| Bulk writers (same tables) | `ERP/SalesForms/Master/ImportCustProduct.aspx.cs`, `UploadCustPrice.aspx.cs`, `ERP/SalesForms/UpdateSalesPrice.aspx.cs` |

Four tables in this family, two of which form a header/line pair:

| Table | Role | Screens |
|---|---|---|
| `SaItemCust` | Customer-specific product (customer item code, invoice desc, UOM, price, MOQ) | `CustomerItems` |
| `IvCustPriceGroup` + `IvCustPrice` | Price group header + per-item price lines | `MasterItemCustPriceGroup` (header), `CustPriceGroupItems` (lines) |
| `SaDisGroup` | Discount by **group name + payment term** | `CustomerGroupDiscount` |
| `SaDisGroupItem` | Discount by **item code + qty band + date window** | `CustomerItemDiscount`, read-only `CustItemDiscList` |

`SaDisCust` also exists in this DB (`CAdapter.SetSaDisCust`, lines 9789-9820) but **no screen or BL in this workspace calls it** — see §8.

---

## 1. Tables (verified column lists)

Types are from the LINQ designer (`ErpDataClasses.dbml`), which was generated from this schema. Where the designer **disagrees with the adapter SQL / page code, that is flagged** — do not treat the designer as authoritative without checking live SQL (§9).

### 1.1 `SaItemCust` — customer product

| Column | Type | Null | Notes |
|---|---|---|---|
| ICode | nvarchar(20) | NOT NULL | PK (designer) |
| IDesc | nvarchar(200) | yes | |
| CustCode | nvarchar(20) | NOT NULL | PK (designer) |
| CustICode | nvarchar(100) | NOT NULL | customer's own item code |
| InvDesc | nvarchar(300) | yes | invoice description override |
| SellingUOM | nvarchar(5) | yes | |
| UnitPrice | float | yes | |
| Currency | nvarchar(5) | yes | |
| StdCustPSize | float | yes | standard pack size |
| Status | nvarchar(10) | yes | `NEW` written by save; `FALSE` = "needs refresh" (see §4.1) |
| Created | datetime | yes | **never written by `SetSaItembyCust`** |
| Updated | datetime | yes | |
| UserID | nvarchar(10) | yes | |
| SPart | bit | yes | always `false` from `CustProfItem.Save` |
| UpdatedUID | nvarchar(10) | yes | |
| CompanyCode / BranchCode / LocationCode | nvarchar(5) | yes | tenant stamp |
| DG | **nvarchar(5)** | yes | "discount" — written as *string*, read as int |
| SG | nvarchar(5) | yes | never written by the screens studied |
| MOQ | int | yes | minimum order qty |
| ProjID | nvarchar(20) | yes | never written by the screens studied |

App-level key = **`ICode` + `CustCode` + `SellingUOM` + `MOQ`** (adapter UPDATE/DELETE `WHERE`, list `KeyFieldName`, entry-screen row lookup). The designer only marks `ICode`+`CustCode` as PK, and the control carries a comment "20-Jun-2017 Mok, allow same item with diff UOM - diff price" — so the live PK may have been widened. **BLOCKED, verify (§9 Q1).**

### 1.2 `IvCustPriceGroup` — price group header

| Column | Type | Null | Notes |
|---|---|---|---|
| CustPriceCode | nvarchar(10) | NOT NULL | PK; app key on its own |
| CustPriceDesc | nvarchar(20) | yes | |
| Created | datetime | yes | **not written by `SetCustPriceGroup`** |
| Updated | datetime | yes | |
| UserID | nvarchar(10) | yes | |
| UpdatedUID | nvarchar(10) | yes | |
| CompanyCode / BranchCode / LocationCode | nvarchar(5) | yes | |
| **Editable** | bit | ? | **in the adapter SQL + the page, absent from the designer** |

`Editable` is written by `SetCustPriceGroup` (Insert/Update) and by `MasterItemCustPriceGroup.callpanel_Callback` (`chbEditable.Checked`). It is missing from `ErpDataClasses.dbml`, so the designer is stale here. **BLOCKED: confirm `Editable` type/nullability/default (§9 Q2).**

App key = `CustPriceCode` only — no tenant in the `WHERE`. Same code in two companies overwrites (see §7).

### 1.3 `IvCustPrice` — price group lines

| Column | Type | Null | Notes |
|---|---|---|---|
| CustPriceCode | nvarchar(10) | NOT NULL | PK (designer) |
| CustPriceDesc | nvarchar(20) | yes | denormalised from the header |
| ICode | nvarchar(20) | NOT NULL | PK (designer) |
| SellingPrice | decimal(18,4) | yes | |
| SellPackSize | float | yes | |
| UOM | nvarchar(10) | NOT NULL | PK (designer) |
| Created | datetime | yes | **not written by `SetCustPrice`** |
| Updated | datetime | yes | |
| UserID | nvarchar(10) | yes | |
| UpdatedUID | nvarchar(10) | yes | |
| CompanyCode / BranchCode / LocationCode | nvarchar(5) | yes | |
| IDesc | nvarchar(200) | yes | |

App-level key = **`CustPriceCode` + `ICode` + `UOM`** (adapter `WHERE`, list `KeyFieldName` `ICode;CustPriceCode;UOM`, delete payload `ICode|CustPriceCode|UOM`). Designer PK matches for these three; tenant columns are **not** in the key.

Read model `vgridCustPriceGroup` = the line table + `IDesc` + computed `Barcode` (`nvarchar(20)`).

### 1.4 `SaDisGroup` — customer group discount

| Column | Type | Null | Notes |
|---|---|---|---|
| GroupName | nvarchar(20) | NOT NULL | PK (designer) |
| GroupLevel | smallint | yes | **never set by the screen** |
| Discount | float | yes | |
| DiscountType | nvarchar(10) | yes | **never set by the screen**; used elsewhere as `'Retailer'` |
| GroupStatus | nvarchar(10) | yes | `NEW` = active; consumers filter `GroupStatus='NEW'` |
| UserID | nvarchar(20) | yes | |
| Created | datetime | yes | re-stamped on every save of that group |
| UpdatedUID | nvarchar(10) | yes | |
| Updated | datetime | yes | |
| CompanyCode | nvarchar(5) | yes | |
| BranchCode | nvarchar(5) | yes | |
| LocationCode | nvarchar(10) | yes | |
| PayCode | nvarchar(20) | NOT NULL | PK (designer) |
| Discount2 | float | yes | added 29-03-2013 |
| Discount3 | float | yes | |

App key = **`GroupName` + `PayCode`** (adapter `WHERE`, list `KeyFieldName`), but tenant is **not** in the key.

### 1.5 `SaDisGroupItem` — item discount

| Column | Type | Null | Notes |
|---|---|---|---|
| ID | smallint NOT NULL IDENTITY | no | **not marked PK in the designer** |
| ICode | nvarchar(20) | yes | |
| IDesc | nvarchar(200) | yes | |
| QtyFr | float | yes | |
| QtyTo | float | yes | |
| DateFr | datetime | yes | |
| DateTo | datetime | yes | |
| Discount | float | yes | discount 1 value |
| DiscountType | nvarchar(10) | yes | `PERCENTAGE` \| `AMOUNT` (written by screen) |
| GroupStatus | nvarchar(10) | yes | `NEW` written on add/save |
| UserID | nvarchar(20) | yes | |
| Created | datetime | yes | |
| UpdatedUID | nvarchar(20) | yes | |
| Updated | datetime | yes | |
| CompanyCode / BranchCode / LocationCode | nvarchar(5) / nvarchar(5) / nvarchar(10) | yes | |
| GroupName | nvarchar(20) | yes | **never written by `CustomerItemDiscount`** |
| Discount1 | float | yes | discount 2 value |
| DiscountType1 | nvarchar(50) | yes | `PERCENTAGE` \| `AMOUNT` |
| EffectPrice | nvarchar(50) | yes | `DEALER` \| `SELLING` |
| IClass | nvarchar(10) | yes | item class |

Adapter key = **`ICode` + `ID`**. List pages key on `ID`; the entry screen de-dupes on `ICode` + `IClass` + `QtyFr` + `QtyTo`. **BLOCKED: real PK/unique index (§9 Q3).**

---

## 2. Screen map and navigation

```
menu
 ├─ Customer Product      -> ItembyCustViewEx.aspx    (200.1.2)  -> CustomerItems.aspx    ?ID=ICode&Type=New|Edit|View&Name=CustCode&uom=&MOQ=
 ├─ Price Group Items     -> CustPriceGroupItemsView.aspx (200.1.24) -> CustPriceGroupItems.aspx ?ID=ICode&Type=..&UOM=..&PriceGroup=CustPriceCode
 │     (header)           -> MasterItemCustPriceGroup.aspx ?ID=CustPriceCode&Type=New|Edit|View
 ├─ Group Discount        -> GroupDiscountViewEx.aspx  (200.1.9)  -> CustomerGroupDiscount.aspx ?ID=GroupName&Type=New|Edit|View
 └─ Item Discount         -> ItemDiscountViewEx.aspx   (200.1.10) -> CustomerItemDiscount.aspx ?ID=ID&Type=New|Edit|View&QtyFr=..&QtyTo=..
        (read-only popup) -> CustItemDiscList.aspx ?ID=ICode
```

All four lists inherit `CommonListForm`, show **NEW / DELETE / EXPORT** only (POST / ACTIVATE / REFRESH / ROLLBACK entries are commented out in `SetButtonInfo`), and pass the query string above up to the entry page.

Entry pages are **not** modal dialogs: they are full pages that reload the main grid via `gvCustProd.PerformCallback("LOAD:{code}")` and keep the working set in a **`DataTable` in `Session`** — this is the pattern that must be replaced by per-screen DTO state + explicit service calls.

Shared session key: `"SACUSTITEMSSALES"` is used by **both** `CustomerItems.aspx.cs` and `CustPriceGroupItems.aspx.cs` (and both controls). Two browser tabs on those two screens share one working set → bleed. Do not port this.

---

## 3. Entry-screen CRUD logic

### 3.1 `CustomerItems` + `CustProfItem` → `SaItemCust`

**Load (list → entry).** `LoadItems(CustCode)` runs:

```sql
SELECT SaItemCust.*, vSaCustAcc.CustName
FROM SaItemCust
INNER JOIN vSaCustAcc ON vSaCustAcc.CustCode = SaItemCust.CustCode
                     AND vSaCustAcc.CompanyCode = SaItemCust.CompanyCode
                     AND vSaCustAcc.BranchCode = SaItemCust.BranchCode
WHERE SaItemCust.CustCode = @CustCode
```

Notes: **no `LocationCode` filter and no tenant filter in the WHERE** (tenant only constrains the join to the Account view). Result stored in session; `CustProfItem.LoadData(CustCode, ICode, uom, MOQ)` then selects the single row for the form (`CustCode=.. AND ICode=.. AND SellingUOM=.. AND MOQ=..`).

**Grid add/edit (client → `callpanelCustPrd_Callback`)**

| Callback | Logic |
|---|---|
| `NEW` | only allowed when the customer code is set, else `cpErrMsg = "Customer code not define yet."` |
| `EDIT:<CustCode>:<ICode>:<uom>:<MOQ>` | `LoadData(...)` populates the form, returns `cpEdit=1` |
| `DELETE:<CustCode>:<ICode>:<uom>` | finds the row in the session table and calls `DataRow.Delete()` (soft delete until SAVE) |
| `SAVE` | `CustProfItem.Save()` then `cpSaved=1` |

**`CustProfItem.Save()` row semantics**

1. Existing row lookup: `ICode = txtCPrdICode AND CustCode = hdCustCode AND SellingUOM = txtCPrdSellingUOM AND MOQ = spinSMOQ`. Found → `Updated = now`, `UpdatedUID = user`. Not found → new row + `Created = now`, `UserID = user`.
2. Assigns: `ICode`, `IDesc`, `CustCode`, `CustICode`, `CustName`, `SellingUOM`, `InvDesc`, `StdCustPSize`, `UnitPrice`, `Currency`, `CompanyCode`, `BranchCode`, `LocationCode`, `MOQ`, `Status = "NEW"`, `SPart = false`, `DG = txtDiscount.Text`.
   - ICode/IDesc/CustICode/InvDesc are upper-cased; `SellingUOM` is **not**.
3. Price default: reads `AdUser` for the session user; **if `SellPriceView = False`**, it loads `IvMas` by `ICode = hdCustCode` — i.e. **the customer code is used as an item code** — and if the entered selling price is `0`, uses that `SellingPrice`. In practice the lookup returns nothing and the price becomes `0`. Treat as a defect, not a rule (§7).
4. Changing a key field (ICode / UOM / MOQ) does **not** update the original row: the pre-lookup misses, a new row is inserted, and the adapter's UPDATE never fires for it. The old row stays. Same behaviour in `CustPriceGroupItemInfo`.

**Save to DB (`CustomerItems.Save`)**

1. Session table must exist, else `cpErrMsg = "No data to update."`
2. `txtCustCode` must be set, else `"Customer code no definet yet."`
3. Any row with an empty `CustCode` gets `txtCustCode`.
4. `dtDtlChg = dtSaCustItem.Copy()` (audit snapshot), open connection, `BeginTransaction`.
5. `CAdapter.SetSaItembyCust(ref da)` → `da.Update(table)`; on success `Commit` + `SalesMasterHelper.AuditLog(dtDtlChg, user, comp, branch, loc)`; on failure `Rollback` (the error message is written to the grid's `cpErrMsg`).
6. `AuditLogHelper.LogAudit("CUST ITEM", <comma-joined changed ICode>, "", user, comp, branch, loc, dtDtlChg)`.

**Adapter SQL (`SetSaItembyCust`)**

```sql
-- INSERT (note: Created is NOT in the column list)
INSERT INTO SaItemCust (ICode, IDesc, CustCode, CustICode, InvDesc, SellingUOM,
  UnitPrice, Currency, StdCustPSize, Status, Updated, UserID, SPart, UpdatedUID,
  CompanyCode, BranchCode, LocationCode, DG, SG, MOQ, ProjID)
VALUES (@ICode,@IDesc,@CustCode,@CustICode,@InvDesc,@SellingUOM,
  @UnitPrice,@Currency,@StdCustPSize,@Status,@Updated,@UserID,@SPart,@UpdatedUID,
  @CompanyCode,@BranchCode,@LocationCode,@DG,@SG,@MOQ,@ProjID);

-- UPDATE (sets tenant to the *current session* values; key uses Original row values)
UPDATE SaItemCust SET ICode=@ICode, IDesc=@IDesc, CustCode=@CustCode,
  CustICode=@CustICode, InvDesc=@InvDesc, SellingUOM=@SellingUOM, UnitPrice=@UnitPrice,
  Currency=@Currency, StdCustPSize=@StdCustPSize, Status=@Status, Updated=@Updated,
  UserID=@UserID, SPart=@SPart, UpdatedUID=@UpdatedUID, MOQ=@MOQ, ProjID=@ProjID,
  CompanyCode=@CompanyCode, BranchCode=@BranchCode, LocationCode=@LocationCode,
  DG=@DG, SG=@SG
WHERE (ICode=@OldICode) AND (CustCode=@OldCustCode)
  AND SellingUOM=@OldUOM AND MOQ=@OldMOQ;

-- DELETE
DELETE FROM SaItemCust
WHERE ICode=@OldICode AND CustCode=@OldCustCode AND SellingUOM=@OldUOM AND MOQ=@OldMOQ;
```

Type hazards to preserve-or-fix deliberately: `DG` is `nvarchar(5)` but is written from a `SpinEdit.Text` string and read back via `Convert2NumTool<int>`; `ImportCustProduct` writes a **double** into it.

### 3.2 `CustPriceGroupItems` + `CustPriceGroupItemInfo` → `IvCustPrice` (+ header picker)

**Load.** `LoadItems(PriceGroup)` runs `SELECT * FROM IvCustPrice WHERE CustPriceCode = @code` (**no tenant filter**). Header combo (`txtPriceGroup`) is an `ASPxGridLookup` over `SELECT CustPriceCode, CustPriceDesc, CompanyCode, BranchCode FROM IvCustPriceGroup WHERE CompanyCode=@c AND BranchCode=@b` (no LocationCode filter). Picking a header sets `hdPriceGroup` **client-side** and calls `gvCustProd.PerformCallback("LOAD:" + CustPriceCode)`.

Note: `CustPriceGroupItemInfo.PriceCode` / `PriceGroupDesc` public properties exist but the page **never assigns them**; the control reads `hdPriceGroup.Text` (client-set) and `hdPriceGroupDesc`.

**Grid add/edit (`callpanelCustPrd_Callback`)**

| Callback | Logic |
|---|---|
| `NEW` | requires `hdPriceGroup` non-empty, else `"Price code not define yet."` |
| `EDIT:<UOM>:<ICode>` | `LoadData` → populates ICode, IDesc, UOM, SellPackSize, SellingPrice, Barcode (`IvMasBL.GetBarcode(ICode)`) |
| `DELETE:<UOM>:<ICode>` | `Select("CustPriceCode=hdPriceGroup AND UOM=.. AND ICode=..")` → `Delete()` |
| `SAVE` | `CustPriceGroupItemInfo.Save()` then `cpSaved=1` |

**Save row semantics:** lookup `ICode = txtPGrpICode AND CustPriceCode = hdPriceGroup AND UOM = txtPGrpSellingUOM`; found → reuse row, else new row. Assigns `ICode`, `IDesc`, `CustPriceCode`, `CustPriceDesc` (= `hdPriceGroupDesc`), `UOM`, `SellPackSize`, `SellingPrice`, tenant, `Created = now`, `UserID`. ICode/IDesc/CustPriceDesc upper-cased. `Updated`/`UpdatedUID` are **not** stamped (they only keep whatever came from the DB).

**Page save (`CustPriceGroupItems.Save`)**

1. Session table required, else `"No data to update."`; `txtPriceGroup` required, else `"Price Code no definet yet."`
2. `SELECT CustPriceDesc FROM IvCustPriceGroup WHERE CustPriceCode = txtPriceGroup.Text` and fills **any blank line `CustPriceDesc`** from it. This dereferences `Rows[0]` with no count check → throws if the code has no header row.
3. Open connection → `BeginTransaction` → `CAdapter.SetCustPrice` → `da.Update` → `Commit` / `Rollback`. **No audit log.**
4. Barcode column exists only as a display column added to the session `DataTable`; it is not persisted.

**Adapter SQL (`SetCustPrice`)**

```sql
-- INSERT (no Created)
INSERT INTO IvCustPrice (CustPriceCode, CustPriceDesc, ICode, SellingPrice, SellPackSize,
  UOM, Updated, UserID, UpdatedUID, CompanyCode, BranchCode, LocationCode, IDesc)
VALUES (@CustPriceCode,@CustPriceDesc,@ICode,@SellingPrice,@SellPackSize,
  @UOM,@Updated,@UserID,@UpdatedUID,@CompanyCode,@BranchCode,@LocationCode,@IDesc);

-- UPDATE
UPDATE IvCustPrice SET CustPriceCode=@CustPriceCode, SellPackSize=@SellPackSize, UOM=@UOM,
  CustPriceDesc=@CustPriceDesc, SellingPrice=@SellingPrice, Updated=@Updated, UserID=@UserID,
  UpdatedUID=@UpdatedUID, CompanyCode=@CompanyCode, BranchCode=@BranchCode,
  LocationCode=@LocationCode, IDesc=@IDesc
WHERE (CustPriceCode=@OldCustPriceCode) AND (ICode=@OldICode) AND (UOM=@OldUOM);

-- DELETE
DELETE FROM IvCustPrice
WHERE CustPriceCode=@OldCustPriceCode AND ICode=@OldICode AND UOM=@OldUOM;
```

### 3.3 `MasterItemCustPriceGroup` → `IvCustPriceGroup` (header)

Single-record form. Loads the **whole table** (`Select * from IvCustPriceGroup`, no tenant filter) into a `DataTable`; `BindData(code)` populates `txtCode`, `txtDesc`, `chbEditable` from `Select("CustPriceCode = '" + code + "'")`.

`callpanel_Callback("SAVE")`:

1. `InputValidation.ValidateInput(txtCode.Text)` — result assigned to `errmsg` and written to `cpErr`, **but execution continues** (no `return`).
2. Lookup by `CustPriceCode`; found → `UpdatedUID = user`, `Updated = now`; not found → `UserID = user`, `Created = now`.
3. Assigns `CustPriceCode`, `CustPriceDesc` (upper-cased), tenant, `Editable = chbEditable.Checked`, `Rows.Add` when new.
4. `CAdapter.SetCustPriceGroup` + `da.Update(dtCustPriceGroup)` — **no transaction, no audit log, no tenant in the WHERE**; `Created` is not in the INSERT column list.
5. `Type=View` hides Save/Cancel **server-side only** (no read-only enforcement of posted values).

### 3.4 `CustomerGroupDiscount` + `ItemGridEntry` → `SaDisGroup`

**Load.** `OpenTable(groupName)` loads three tables:

- `dtPayTerm` = `SELECT * FROM SaPaymentTerm` (**no `Active` filter**) → payment-term combo items.
- `dtSaDisGroupSearch` = `SELECT * FROM SaDisGroup` (**all tenants**) → the "find" popup grid, always re-bound at the end of `Page_Load`.
- `dtSaDisGroup` = session table if present, else `SELECT * FROM SaDisGroup WHERE 1=1 [AND GroupName = @groupName]` (**no tenant filter**; the filter is `1=1` by default).

The find button posts back `LoadItem` with the group name: session is cleared, `OpenTable(groupName)` + `BindData(groupName)` + `BindDatagrid()` re-run.

Grid shell: `ItemGridEntry` with `SetKeyFieldName("GroupName;PayCode")`, `TABLE_SESSION_NAME = "SaDisGroup"`, columns `GROUP NAME, PAYMENT TERM, DISCOUNT, DISCOUNT2, DISCOUNT3`.

**Callback actions (`callpanel_Callback`)**

| Action | Logic |
|---|---|
| `EDIT:GroupName,PayCode,Discount,Discount2,Discount3` | `Select("GroupName=.. and PayCode=..")` on the session table; fills `txtGroupName`, `ddlTermofPayment` (by `FindByText`), `txtDiscount`, `txtDiscount2`, `txtDiscount3` |
| `ADD:` | lookup `GroupName = txtGroupName AND PayCode = ddlTermofPayment.SelectedItem.Text`; fills the row (below); `ResetControl()`; `BindDatagrid()` |
| `DELETE:<PayCode>,<PayCode>,...` | for each paycode, `Select("GroupName = txtGroupName AND PayCode = ..")` → `Delete()`; rebind; `ResetControl()` |
| `SAVE:` | see below |

**ADD row assignment:** `GroupName` (upper), `Discount = txtDiscount.Text`, `GroupStatus = "NEW"`, then `Created = now` + `UserID` when new / `Updated = now` + `UpdatedUID` when existing, `PayCode = ddlTermofPayment.Text`, tenant, `Discount2` (`0` when the control text is empty), `Discount3` (same). **`GroupLevel` and `DiscountType` are never set** → NULL.

**SAVE:** require at least one session row with `GroupName = txtGroupName.Text`, else `cpMsg = "No Data Added!"`. Then for *every* row of that group it overwrites `GroupStatus="NEW"`, `Created=now`, `UserID`, `Updated=now`, `UpdatedUID`, tenant — i.e. **the original created-by/created-on is destroyed on every re-save**. Then `BeginTransaction` → `UpdateRec` → `SetSaDisGroup` + `da.Update` → `Commit` (+ `BindDatagrid()` + `ResetControl(true)` which clears the group name and the session rows) / `Rollback`.

**Adapter SQL (`SetSaDisGroup`)**

```sql
-- INSERT (Updated / UpdatedUID params are declared but NOT used; no Created param problem)
INSERT INTO SaDisGroup (GroupName, GroupLevel, Discount, DiscountType, GroupStatus,
  UserID, Created, CompanyCode, BranchCode, LocationCode, PayCode, Discount2, Discount3)
VALUES (@GroupName,@GroupLevel,@Discount,@DiscountType,@GroupStatus,
  @UserID,@Created,@CompanyCode,@BranchCode,@LocationCode,@PayCode,@Discount2,@Discount3);

-- UPDATE (BranchCode is a no-op; key has no tenant)
UPDATE SaDisGroup SET GroupName=@GroupName, GroupLevel=@GroupLevel, Discount=@Discount,
  DiscountType=@DiscountType, GroupStatus=@GroupStatus, UpdatedUID=@UpdatedUID,
  Updated=@Updated, CompanyCode=@CompanyCode, BranchCode=BranchCode,
  LocationCode=@LocationCode, PayCode=@PayCode, Discount2=@Discount2, Discount3=@Discount3
WHERE (GroupName=@OldGroupName) AND (PayCode=@OldPayCode);

-- DELETE
DELETE FROM SaDisGroup WHERE (GroupName=@OldGroupName) AND (PayCode=@OldPayCode);
```

`Ordering note`: because the key excludes the tenant, saving group `G` under company B when company A already owns `(G, PayCode)` overwrites company A's row.

### 3.5 `CustomerItemDiscount` + `ItemGridEntry` → `SaDisGroupItem`

**Load.** Session table; if absent, `SELECT * FROM SaDisGroupItem WHERE 1=1 [AND ID=@id AND QtyFr=@fr AND QtyTo=@to] ORDER BY ICode` — **no tenant filter**. Additionally always loads `dt = SELECT * FROM SaDisGroupItem` (whole table, used by the overlap validator) and `dtClass = SELECT * FROM IvClass` for the class combo.

Grid shell: `SetKeyFieldName("ICode;IClass;QtyFr;QtyTo")`, `TABLE_SESSION_NAME = "SaDisGroupItem"`, bands `ITEM (CODE/CLASS/DESCRIPTION)`, `QTY (FROM/TO)`, `DATE (FROM/TO)`, `DISCOUNT 1 (DISCOUNT/TYPE)`, `DISCOUNT 2 (DISCOUNT/TYPE)`, plus `EFFECT PRICE`.

**Form fields:** ICode (grid-lookup → sets IClass + IDesc client-side), IClass combo, IDesc memo, QtyFr, QtyTo, DateFr, DateTo, Discount% + type (`PERCENTAGE`/`AMOUNT`), Discount2% + type, EffectPrice radio (`DEALER` default / `SELLING`).

**Callback actions**

| Action | Logic |
|---|---|
| `EDIT:ID,ICode,IClass,QtyFr,QtyTo` | `Select` on session table by ID (+keys when ID is empty); populates all fields. **Bug:** `rbDiscountType1.SelectedIndex` is set from `rbDiscountType.Items` |
| `ADD:` | see below |
| `DELETE:<ID,ICode,IClass,QtyFr,QtyTo>│<...>` | split by `'|'`, each split by `','`; `Select` + `Delete()`; `BindDatagrid()` (form is **not** reset) |
| `SAVE:` | `CCommon.CheckRecordAlreadyAdd` gate → `"No Data Added!"`; transaction; `SetSaDisGroupItem`; `ResetControl(true)` |

**ADD:**

1. `hdID` defaults to `"0"` when empty.
2. Lookup on `ICode + IClass + QtyFr + QtyTo` (not on ID). If exactly one hit, `hdID.Text = dtSaDisGroupItem.Rows[0]["ID"]` — **reads row 0 of the whole table, not the matched row**.
3. `validateQty()` (below) — failure → `cpMsg = "Invalid QtyFr or QtyTo"` and return.
4. Assigns: `ICode` (upper), `IDesc` (upper), `QtyFr`, `QtyTo` (from control text), tenant, `DateFr`/`DateTo` (parsed `en-GB`; `DBNull` when blank), `Discount = txtDiscPercent.Text`, `DiscountType` = selected type, `Discount1` (+`DiscountType1` only when the 2nd discount box is non-empty, else `Discount1 = "0"`), `EffectPrice` = selected radio value, `IClass` = class combo value, `GroupStatus = "NEW"`, `Created`, `UserID`, `Updated`, `UpdatedUID`.
   - **`GroupName` is never set** → NULL for rows created here.
5. Rebind + `ResetControl()`.

**`validateQty()` (overlap guard).** Returns `true` immediately when **`QtyFr = 0 AND QtyTo = 0`** (documented as "no need control by qty, all qty will have discount"). Otherwise: candidates = `dt.Select("ICode = @icode AND ID <> hdID AND GroupStatus = 'NEW'")` (whole table); if none, falls back to every row of the session table. It fails when any candidate's `[QtyFr,QtyTo]` interval overlaps the new one (3-way interval test). No date overlap test, no IClass test.

**SAVE:** `CCommon.CheckRecordAlreadyAdd(dtSaDisGroupItem)` gate, `BeginTransaction` → `UpdateRec` → `SetSaDisGroupItem` + `da.Update` → `Commit` + `ResetControl(true)` (clears ICode/IDesc/class and the session rows) / `Rollback`. **No audit log.**

**Adapter SQL (`SetSaDisGroupItem`)** — note ID is IDENTITY and is **not** inserted:

```sql
INSERT INTO SaDisGroupItem (ICode, IDesc, QtyFr, QtyTo, DateFr, DateTo, Discount,
  DiscountType, GroupStatus, UserID, Created, CompanyCode, BranchCode, LocationCode,
  Discount1, DiscountType1, EffectPrice, IClass)
VALUES (@ICode,@IDesc,@QtyFr,@QtyTo,@DateFr,@DateTo,@Discount,@DiscountType,
  @GroupStatus,@UserID,@Created,@CompanyCode,@BranchCode,@LocationCode,
  @Discount1,@DiscountType1,@EffectPrice,@IClass);

UPDATE SaDisGroupItem SET ICode=@ICode, IDesc=@IDesc, QtyFr=@QtyFr, QtyTo=@QtyTo,
  DateFr=@DateFr, DateTo=@DateTo, Discount=@Discount, DiscountType=@DiscountType,
  GroupStatus=@GroupStatus, UpdatedUID=@UpdatedUID, Updated=@Updated,
  CompanyCode=@CompanyCode, BranchCode=BranchCode, LocationCode=@LocationCode,
  Discount1=@Discount1, DiscountType1=@DiscountType1, EffectPrice=@EffectPrice, IClass=@IClass
WHERE (ICode=@OldICode) AND (ID=@OldID);

DELETE FROM SaDisGroupItem WHERE (ICode=@OldICode) AND (ID=@OldID);
```

### 3.6 `CustItemDiscList` — read-only listing (separate defect surface)

Plain `ASPxGridView` bound to:

```sql
SELECT ICode, IDesc, QtyFr, QtyTo, DateFr, DateTo, Discount, DiscountType, GroupStatus,
       GroupName, Discount1, DiscountType1, EffectPrice
FROM SaDisGroupItem
WHERE ICode = @ICode
ORDER BY DateFr
```

`@ICode` comes from the querystring key `ID`. **No tenant filter, no date/qty filter.** Columns displayed: EffectPrice, QTY, DATE, DISCOUNT 1/2, GROUP (Status + Name).

---

## 4. List-screen CRUD logic

Common shape: `CommonListForm` + LINQ server-mode grid, `NEW/DELETE/EXPORT`, per-action rights via `IsValidAccessRight`.

| List | Screen ID | Key field(s) | Read source | Delete |
|---|---|---|---|---|
| `ItembyCustViewEx` | 200.1.2 | `ICode;CustCode;SellingUOM;MOQ` | `db.vgridCustomerProducts` (**no tenant filter in the queryable**; view `vgridCustomerProduct` returns ICode, CustCode, CustName, CustICode, InvDesc, SellingUOM, UnitPrice, Currency, StdCustPSize, Status, Updated, UserID, Created, UpdatedUID, CompanyCode, BranchCode, LocationCode, IDesc, MOQ, Barcode) | `SalesMasterHelper.DeleteCustomerProduct(keys, user, comp, branch, loc)` — loads `SaItemCust WHERE CompanyCode/BranchCode/LocationCode`, deletes by `ICode + CustCode + SellingUOM + MOQ`, adapter delete, then `AuditLog` |
| `GroupDiscountViewEx` | 200.1.9 | `GroupName;PayCode` | `db.SaDisGroups.Where(tenant)` | LINQ `DeleteOnSubmit` per matched entity (tenant + key). **Hard delete** |
| `ItemDiscountViewEx` | 200.1.10 | `ID` | `db.SaDisGroupItems.Where(tenant)` | `SalesMasterHelper.DeleteGroupDiscItem(ids, comp, branch, loc)` — loads items for tenant, `Select("ID=" + id)`, delete via adapter |
| `CustPriceGroupItemsView` | 200.1.24 | `ICode;CustPriceCode;UOM` | `db.vgridCustPriceGroups` (**no tenant filter**) | `SalesMasterHelper.DeletePriceGroupItem(keys, comp, branch, loc)` — deletes by `ICode + CustPriceCode + UOM` |

Extra behaviour:

- **Anti-steal checks (UI only).** `ItembyCustViewEx` and `CustPriceGroupItemsView` compare `UserID` of the target row against the session user before DELETE / REFRESH and show `"Cannot Delete/Refresh another User's Item(s)!"`. These checks are **display-only**: they never `return` before calling the helper, so the delete still happens.
- **REFRESH (handler present, button commented out).**
  - `ItembyCustViewEx` → `SalesMasterHelper.RefreshCustomerProduct` = `SELECT * FROM SaItemCust WHERE Status='FALSE' AND tenant`, sets `Status='NEW'` on the selected keys, adapter update in a transaction. Message `"Item(s) refreshed!"`.
  - `GroupDiscountViewEx` / `ItemDiscountViewEx` → LINQ sets `GroupStatus = "NEW"` for tenant + key.
- **EXPORT** goes through `ASPxGridExporter` with headers `CUSTOMER PRODUCT` / `GROUP DISCOUNT` / `ITEM CODE DISCOUNT` / `CUSTOMER PRICE GROUP ITEMS`.
- **Rights:** `New`, `Edit`, `Access` (view), `Delete`, `Print` per action; `ItembyCustViewEx` additionally computes `POSTRIGHTS` via `CUser.CheckUserRight2((int)CCommon.enGroupRight.Post, userId, dtUserRight, this.ID)` and exposes it as `hdHidden["POSTRIGHTS"]` (Post is not wired to any button).
- **Price hiding:** each list removes the price column when the user's `AdUser.SellPriceView = false` — `UnitPrice` (200.1.2) / `SellingPrice` (200.1.24).

---

## 5. Business meaning / downstream consumers

This is what must keep working after the port; it explains why the tables are shaped the way they are.

**Customer product (`SaItemCust`).** Customer-specific item number, invoice description, selling UOM, price and MOQ used when selling that item to that customer. `vSaCust`/`Customer Profile` is per customer code; rows are per `ICode`+`UOM`+`MOQ`.

**Price group (`IvCustPriceGroup` / `IvCustPrice`).** A named price list; `IvCustPrice` holds one price per item + UOM. `MasterItemCustPriceGroup` maintains the header only. Selected price lists drive pricing in `MasterItem.aspx` (lines ~1341/1349), `UpdateSalesPrice.aspx` (lines ~266/270) and `UploadCustPrice.aspx`.

**Group discount (`SaDisGroup`)** — resolved at SO/Invoice time by *customer + payment term*, not by customer directly:

```sql
-- SaCustomerBL.GetCustDiscount(custCode, payCode)
SELECT Discount, b.DiscountMethod, b.DiscountSeq, Discount2, Discount3
FROM SaDisGroup a RIGHT OUTER JOIN vSaCust b ON a.GroupName = b.GroupDiscount
WHERE a.GroupStatus = 'NEW' AND b.CustCode = @custCode AND a.PayCode = @payCode;
```

So: the customer (Account DB view `vSaCust`) points at a **group name**; `SaDisGroup` supplies the discount per **payment term**. Only `GroupStatus='NEW'` rows are used → that column is an activation flag, and any row the screen writes is active.

**Item discount (`SaDisGroupItem`)** — resolved by *item + quantity + date*; the group name is **not** used by any consumer query studied:

```sql
-- SaCustomerBL.GetDiscountItem(ICode, orderQty) — date window tolerant of NULLs
SELECT Discount, DiscountType, Discount1, DiscountType1 FROM SaDisGroupItem
WHERE ICode = @icode AND QtyFr <= @qty AND QtyTo >= @qty
  AND ((DateFr IS NULL AND DateTo IS NULL)
    OR (DateTo >= getdate() AND DateFr IS NULL)
    OR (DateFr <= getdate() AND DateTo IS NULL)
    OR (DateTo >= getdate() AND DateFr <= getdate()));

-- SalesDicountHelper.GetItemDiscount(ICode, date, qty) — strict window, NULLs excluded
SELECT * FROM SaDisGroupItem
WHERE ICode = @icode AND QtyFr <= @qty AND QtyTo >= @qty
  AND DateFr <= @date AND DateTo >= @date;
```

Neither query filters by tenant or `GroupStatus`. `QtyFr=0/QtyTo=0` ("all qty") is therefore a **live row that never matches** these predicates — the screen's special-case only relaxes the overlap validator, not the lookup. Confirm the intended rule before porting (§9 Q6).

**Discount maths (`SalesDicountHelper.CalculateDiscount`).** Percentage discounts are **cascading** (each applied to the price already reduced by the previous), flat amounts are added, and `DiscMethod = "JOIN"` instead sums all percentages against the original price:

$$\text{itemDisc}_{n} = \Big(P - \sum_{k<n}\text{itemDisc}_k\Big)\times\frac{d_n}{100}, \qquad \text{totalDiscount}=\sum_n \text{itemDisc}_n + \text{amount}_1 + \text{amount}_2$$

**Other readers.**
- `SalesDicountHelper.CalculateDiscount` supports up to 7 percentage slots and 2 amount slots, but this UI writes only `Discount` and `Discount1` (+ `EffectPrice`, `IClass`).
- `ERP/Service/ListItembyCust.aspx.cs` merges `SaDisGroupItem` + `SaDisGroup` + `SaItemCust` + last-3 invoice prices into a mobile/service payload.
- `ERP/DataService.cs` exposes `GetSaDisGroup`, `GetSaDisGroupItem`, `GetSaItemCust`, `GetSaItemCustType`, `GetCustPriceGroup` paging web-methods. `GetItemSalesPrice` joins `sadisgroup sa ON sa.discounttype = 'Retailer'` — so `DiscountType` doubles as a *category* filter elsewhere even though this screen leaves it NULL.
- `ImportCustProduct.aspx` (bulk `SaItemCust` upsert), `UploadCustPrice.aspx` (bulk `IvCustPrice`), `UpdateSalesPrice.aspx` (writes both) use the same adapters.

---

## 6. Rights model in these screens

| Check | Where | Effect |
|---|---|---|
| `AdUser.SellPriceView` | entry pages `HideColByAccessRight()`, controls `HidePriceControls()` | `false` → hide `UnitPrice`/`SellingPrice` control + grid column. Loaded by `SELECT * FROM AdUser ORDER BY ID` on **every postback** (whole table) |
| `AdUser.CurrView` | same block | read into `isCurrView`, **never used** |
| `CCommon.enGroupRight.Access/Edit/Delete/New/Print` | list pages via `IsValidAccessRight` | gates list actions |
| `CCommon.enGroupRight.Post` | `ItembyCustViewEx` only | exposed as `POSTRIGHTS`, no button |
| *entry-screen rights* | — | **none.** `CustomerItems`, `CustPriceGroupItems`, `CustomerGroupDiscount`, `CustomerItemDiscount` only verify `IsLogin`; `Type=View` hides buttons but the controls still post values |
| `CheckCPriceViewOnly()` | `CustomerItems.aspx.cs` | implemented but **the call is commented out** |

Blazor must replace these with `MenuCodes` + `PermissionCodes` and enforce view mode server-side.

---

## 7. Defects / traps that are NOT business rules

Decide explicitly for each — do not silently copy, do not silently "fix" without a decision:

1. `CustProfItem.Save` price default reads `IvMas` by **customer code** (should be the item code).
2. `DG` is `nvarchar(5)` written from a string in one screen and a double in the importer.
3. Changing a key field (ICode/UOM/MOQ) inserts a new row instead of updating the old one (both `SaItemCust` and `IvCustPrice`).
4. `Created` is never persisted by any of the four adapters.
5. `SaDisGroup` save re-stamps `Created`/`UserID` on every save of the group.
6. `SaDisGroup.BranchCode = BranchCode` in the UPDATE (no-op).
7. `SaDisGroupItem` ADD sets `hdID` from the wrong DataTable row.
8. `SaDisGroupItem` EDIT sets `rbDiscountType1` from `rbDiscountType`.
9. Delete-refresh "another user's item" checks warn but do not stop the operation.
10. `CustPriceGroupItems.Save` dereferences `dtDesc.Rows[0]` without a count check.
11. `MasterItemCustPriceGroup` continues after `InputValidation` reports an error.
12. Shared session key `"SACUSTITEMSSALES"` across two screens.
13. Delete/update keys contain **no tenant code** (`IvCustPriceGroup`, `SaDisGroup`, and the view-based lists) → cross-company data loss.
14. `vgridCustomerProduct`, `vgridCustPriceGroup`, `CustItemDiscList`, `Service/ListItembyCust`, and both `SaDisGroup`/`SaDisGroupItem`/`IvCustPrice` load queries have **no tenant predicate**.
15. No `RowVersion` anywhere; the UI's "another user" check is the only concurrency control.

---

## 8. Blazor reconciliation (already-built code)

`ErpWeb.Model` / `ErpWeb.UI` already contain **`SaDisGroup`** and **`SaDisCust`**:

```csharp
// ErpWeb.Model/Entities/CustomerProfile/SaDisGroup.cs
public class SaDisGroup {
    public string CompanyCode, GroupName, PayCode; public short? GroupLevel;
    public double? Discount, Discount2, Discount3; public string? DiscountType, GroupStatus;
    public string? BranchCode, LocationCode;
    public DateTime? CreatedDate, ModifiedDate; public string? CreatedBy, ModifiedBy;
    public ICollection<SaDisCust> Members;
}
// Config: ToTable("SaDisGroup"); HasKey(CompanyCode, GroupName, PayCode);
//         Created->Created, UserID->CreatedBy, Updated->ModifiedDate, UpdatedUID->ModifiedBy
```

Consequences to reconcile before this port:

- The Blazor key includes `CompanyCode` — this repo's `SaDisGroup` screen does not. Porting `CustomerGroupDiscount` as-is would create a second, tenant-agnostic writer against a tenant-keyed model. Decide: extend the legacy key or keep two write paths.
- `SaDisCust` in Blazor is a **membership** table (`CompanyCode, GroupName, PayCode, CustCode, CustName`). In this repo `SaDisCust` exists but has **no callers** and no `CompanyCode` column (`Update/Insert … Set GroupName=@GroupName, CustCode=@CustCode, CustName=@CustName`). Two different meanings for one table name — confirm which the production DB actually holds (§9 Q7).
- `ErpWeb.Model` config max-lengths (`CompanyCode` 10, `GroupName` 40, `PayCode` 40, `DiscountType` 20, `LocationCode` 20) are wider than this schema (5 / 20 / 20 / 10 / 10). Widen deliberately or match legacy.
- No Blazor entity/service/list page exists yet for `SaItemCust`, `IvCustPrice`, `IvCustPriceGroup`, or `SaDisGroupItem` (verified by search over `ErpWeb.Model` + `ErpWeb.Core`).

---

## 9. BLOCKED — verify against live SQL before writing EF

Answer these first; each is a schema fact, not a preference.

| # | Question | Why it matters |
|---|---|---|
| Q1 | Real PK / unique index of `SaItemCust`: is it `(ICode, CustCode)` or `(ICode, CustCode, SellingUOM, MOQ)`? | EF key + the "same item, different UOM" feature |
| Q2 | Does `IvCustPriceGroup.Editable` exist live? Type, nullability, default? | missing from the designer but used by adapter + page |
| Q3 | `SaDisGroupItem`: is `ID` the PK? Is there a unique index on `(ICode, IClass, QtyFr, QtyTo)` or `(ICode, CompanyCode, …)`? | IDENTITY + no PK in the designer is suspicious; drives `DbUpdateConcurrencyException` handling |
| Q4 | Are there unique constraints/indexes on `IvCustPrice(CustPriceCode, ICode, UOM)` and `SaDisGroup(GroupName, PayCode)` live? | duplicate prevention vs. `da.Update` throwing |
| Q5 | `SaItemCust.DG`/`SG` — real type and intent (discount %? group code?), and is `DG` used by any pricing path? | string-vs-number ambiguity |
| Q6 | Is `QtyFr=0 AND QtyTo=0` ("all qty") meant to match every quantity in the pricing lookup? Today no consumer query matches it. | correctness of discount application |
| Q7 | Which `SaDisCust` shape exists in production: legacy `(GroupName, CustCode, CustName)` or the Blazor `(CompanyCode, GroupName, PayCode, CustCode, CustName)`? | two conflicting definitions |
| Q8 | Allowed values of `GroupStatus` (observed: `NEW`, `FALSE`; commented code uses `TRUE`/`FALSE`) and of `DiscountType` (`PERCENTAGE`/`AMOUNT` written by the item screen; `'Retailer'` used as a filter by `GetItemSalesPrice`). | lookups/enum design and consumer filters |
| Q9 | Is there an FK from `SaDisGroupItem.GroupName` → `SaDisGroup.GroupName`? (column exists, never written) | whether to drop or wire the column |
| Q10 | Are `Created`/`Updated`/`UpdatedUID` nullable with no default, and is anything else writing these tables (mobile API, imports)? | default stamping on insert |
| Q11 | `IvCustPrice` `SellingPrice decimal(18,4)` vs `SaItemCust.UnitPrice float` — intended precision per table? | decimal mapping |
| Q12 | Do rows exist with `LocationCode` NULL/mismatched across the family (screens filter by tenant inconsistently)? | data cleanup before adding tenant predicates |

Handy checks:

```sql
SELECT OBJECT_NAME(i.object_id) AS tbl, i.name AS idx, i.is_primary_key, i.type_desc,
       c.name AS col, ic.key_ordinal
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE i.object_id IN (OBJECT_ID('SaItemCust'),OBJECT_ID('IvCustPrice'),
        OBJECT_ID('IvCustPriceGroup'),OBJECT_ID('SaDisGroup'),OBJECT_ID('SaDisGroupItem'),
        OBJECT_ID('SaDisCust'))
ORDER BY tbl, i.index_id, ic.key_ordinal;

SELECT t.name, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity
FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id
JOIN sys.types ty ON ty.user_type_id=c.user_type_id
WHERE t.name IN ('SaItemCust','IvCustPrice','IvCustPriceGroup','SaDisGroup','SaDisGroupItem')
ORDER BY t.name, c.column_id;

SELECT DISTINCT GroupStatus FROM SaDisGroup;  SELECT DISTINCT DiscountType FROM SaDisGroup;
SELECT DISTINCT Status FROM SaItemCust;
SELECT COUNT(*) FROM SaDisCust;
SELECT TOP 5 * FROM SaDisGroupItem WHERE QtyFr=0 AND QtyTo=0;
```

---

## 10. Port checklist

Keep the *rules*, drop the *mechanism*.

1. **Schema** — EF configs generated from the verified findings (§9), including `Editable`, real PKs, decimal precision, and a `RowVersion` if the team accepts a schema change (none exists today).
2. **Service layer** — one service per aggregate, service-owned transactions, tenant (`CompanyCode`+`BranchCode`+`LocationCode`) **inside every key and every read predicate**; delete in-use checks owned by the service, not the page.
3. **Replace session `DataTable` working sets** with typed per-screen line models + explicit `Save` calls; the ORDER/ADD/DELETE-overlap rules belong in the service (`validateQty` → service rule; also decide the missing date-overlap rule).
4. **Preserve the price/quantity semantics:** `MOQ`, `SellingUOM`, `StdCustPSize`, `SellPackSize`, `EffectPrice` (`DEALER`/`SELLING`), `Discount`/`Discount1` + types, and the cascading discount maths in §5.
5. **Decide each item in §7** and record it in the plan; do not let them leak into the Blazor implementation silently.
6. **Rights** — `MenuCodes` + `PermissionCodes` for New/Edit/View/Delete/Export; sell-price visibility as a real permission (not a control-visibility toggle); view mode enforced server-side.
7. **Screens to build** — 4 lists (200.1.2, 200.1.9, 200.1.10, 200.1.24) + 5 entry screens (`CustomerItems`, `CustPriceGroupItems`, `MasterItemCustPriceGroup`, `CustomerGroupDiscount`, `CustomerItemDiscount`) + 1 read-only `CustItemDiscList`.
8. **Checklist before coding:** every §9 question answered CONFIRMED, no §7 item left undecided, and the `SaDisGroup`/`SaDisCust` reconciliation in §8 agreed.

---

## 11. Blazor v2 — implementation status (2026-09-15)

Plan of record: `plans/sales-item-family-v2-plan.md`. Phase 0 findings: `ErpWeb/docs/sales-item-family-phase0-findings.md` (PASS). Schema: `scripts/init-sales-item-family.sql` (applied). Menu/permission seed: `scripts/init-sales-item-family-menu.sql`.

### 11.1 What is implemented

| Object | Table | Screen | Route |
|---|---|---|---|
| Price list header + item prices | `IvCustPriceGroup` + `IvCustPrice` | Price Groups (list + header/line editor) | `/sales/price-groups` |
| Customer item | `SaItemCust` | Customer Items (list + popup CRUD) | `/sales/customer-items` |
| Item discount rule | `SaDisGroupItem` | Item Discounts (list + popup CRUD) | `/sales/item-discounts` |
| Price + discount **resolution** (rules only) | — | `SaItemFamilyPricing.cs` (`ResolveItemPriceAsync` / `ResolveItemDiscountAsync` on `ISaSalesRefService`) | n/a — server capability |

The standalone read-only **Customer Prices** screen (`SA_CUST_PRICE`, `/sales/customer-prices`) was merged into Price Groups on 2026-09-15: it rendered NEW/EDIT/DELETE buttons whose handlers were stubs, and its ADD/EDIT/DELETE permissions were never checked by the service. Item prices are now maintained only in the price-list popup (one aggregate, one writer); `/sales/customer-prices` redirects there and the per-group download is `/sales/price-groups/prices/export`.

Service surface: `ISaSalesRefService` (sales item family section) implemented in `SaSalesRefService.ItemFamily*.cs`; DTOs in `SaItemFamilyResults.cs`; workbooks in `SaMasterRefExportWorkbooks`; downloads at `/sales/price-groups/export`, `/sales/price-groups/prices/export?custPriceCode=…`, `/sales/customer-items/export`, `/sales/item-discounts/export`.

The **resolution contract itself is implemented and tested** — the pure rules live in `ErpWeb.Core/Sales/SaItemFamilyPricing.cs` (no EF, no UI), wrapped by two tenant-scoped, screen-ungated service entry points in `SaSalesRefService.ItemFamily.Resolve.cs`. What remains deferred is only the *consumer*: no SO/DO/INV/CN page calls them yet (§11.2). Tests: `SaItemFamilyPricingContractTests` (precedence, JOIN/SPLIT, slots, tie-break), `SaItemFamilyResolutionServiceTests` (the DB-loaded chain, legacy `float` scaling, date-part windows).

Decisions that differ from the legacy tables (all deliberate — see the plan and the Phase 0 findings):

- `CompanyCode` is in every key (`SaItemCust`'s PK was widened; the other three tables are created by us).
- `SaDisGroupItem.ID` is `int` (never `smallint`), bands/amounts are `decimal(18,4)` (never `float`), the window is `date` (never `datetime`), and the legacy `GroupName`/`GroupLevel`/`Discount2`/`Discount3` columns are **not carried** (write-only, read by no consumer).
- `SaItemCust.UnitPrice` keeps the live `float` and is scaled explicitly at the service boundary; `MOQ` keeps its live `int` (fractional MOQ is unsupported); `Status` is a **refresh flag**, not activation.
- `IvCustPriceGroup` gained `Active`; the price-line table deliberately has none.
- The band/date overlap rule is enforced in the service inside a Serializable transaction: at most one rule may match an item. `0/0` bands are rejected.
- `VIEW_PRICE` is a real permission (visibility only). A denied caller receives **no price value** — including in workbooks — and can neither set nor erase a stored price.

### 11.2 What is deliberately NOT implemented

| Deferred | Reason |
|---|---|
| SO/DO/INV/CN price or discount resolution | consumer work — the rules exist and are tested (`SaItemFamilyPricing.cs`), but no document page calls them yet; the contract below is fixed instead |
| Dealer pricing (`PriceMethod = FOLLOW DEFAULT DEALER PRICE`, `EffectPrice = DEALER`) | Phase 0 verified that **no dealer price column exists anywhere** in the database. Dealers fail closed until a source is approved. `EffectPrice` is stored, validated and inert. |
| Automatic UOM conversion | no conversion table exists in this repository |
| Tax on stored prices | removed from the master; prices are **tax-exclusive** commercial prices. Note the shipped consumer still seeds `UnitPrice = item.SellingPrice ?? 0m` and the line engine un-taxes an inclusive line, so that call site needs the gross-up when the consumer lands (decision O1 in the plan). |
| Customer-level group discount (`SaCust.GroupDiscount` → `SaDisGroup`) | the active helper ignores `CustDiscount`; out of scope by decision |
| Bulk import/upload (`ImportCustProduct`, `UploadCustPrice`, `UpdateSalesPrice`) | legacy-only writers; not ported |
| `SaDisGroup` / `SaDisCust` changes | shipped in v1; verified only, with exactly one Blazor implementation |

### 11.3 Consumer contract (fixed now, implemented later)

The future SO/DO/INV/CN consumer must call, not re-implement:

- **`ISaSalesRefService.ResolveItemPriceAsync(SaItemFamilyPriceRequest)`** → one tax-exclusive price plus the source that produced it, or a blocking reason. It loads the customer's own `PriceMethod`/`CustPriceCode` and the candidate rows itself; a dealer-mode customer and a customer-item price stored in another currency both fail closed.
- **`ISaSalesRefService.ResolveItemDiscountAsync(SaItemFamilyDiscountRequest)`** → the matching rule (or none) with the engine-ready slots and the **unrounded** per-unit discount; when legacy data still holds overlapping rules, the pick is deterministic (class-specific → higher `QtyFr` → earlier `DateFr` → lower `Id`) and the losing rule ids are reported for logging.
- **`SaItemFamilyPriceBasis.ToInclusive` / `.ToExclusive`** at the boundary only — masters store tax-exclusive prices and never do tax maths.

Beyond that, the caller must:

1. resolve the price: `PriceMethod` = DEALER → **fail closed** (unsupported); otherwise `SaItemCust.UnitPrice` (highest `MOQ ≤ qty`) → `IvCustPrice.SellingPrice` via `SaCust.CustPriceCode` → `IvStockMaster.SellingPrice` (only when the UOM matches) → **block** ("no price found"), never `0`;
2. resolve the discount: one `SaDisGroupItem` row per (company, item) matching the band and the date window, class-specific before blank class; map `Discount`/`DiscountType` and `Discount1`/`DiscountType1` onto the engine's percent slots 1-2 / amount slots 1-2 and pass `SaCust.DiscountMethod` (JOIN = sum against the original price, otherwise sequential);
3. convert an exclusive master price to the line's basis before arithmetic when the line is inclusive;
4. never re-derive a price or discount in the UI — the server owns both, and `VIEW_PRICE` never changes what is stored.

