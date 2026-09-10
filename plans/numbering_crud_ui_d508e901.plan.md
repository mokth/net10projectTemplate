---
name: Numbering CRUD UI
overview: Add two Sales-master list pages (UOM layout) for AdSmNum and AdSmNumDate CRUD, scoped to the authenticated company+branch, with numbering-spec validation and menu access.
todos:
  - id: admin-service
    content: IAdSmNumAdminService + AdSmNumAdminService (branch scope, validation, mutual exclusion) + DI
    status: pending
  - id: ui-pages
    content: AdSmNumList and AdSmNumDateList UOM-style pages + page base
    status: pending
  - id: menus
    content: MenuCodes, menus.xml, init-menu-access.sql
    status: pending
  - id: tests
    content: "AdSmNumAdminServiceTests: CRUD, exclusion, branch isolation"
    status: pending
isProject: false
---

# Numbering admin UI (AdSmNum / AdSmNumDate)

Two list+popup pages matching [`IvUomList.razor`](ErpWeb.UI/Inventory/Masters/IvUomList.razor): `iv-page` / `iv-hero` / toasts / `CommonDataGridEx` / `DxPopup` edit / confirm-delete. No Activate/Deactivate (tables have no `IsActive`).

Place under **Sales > Master** (invoice is the first consumer). Isolation: **current write/read branch only** — same grain as numbering allocation. Company/Branch/Location come from [`TryWriteScope`](ErpWeb.Core/Inventory/InventoryTenantContext.cs); UI never accepts tenant from the form.

## Service

New [`ErpWeb.Core/Numbering/IAdSmNumAdminService.cs`](ErpWeb.Core/Numbering/IAdSmNumAdminService.cs) + `AdSmNumAdminService` (do not grow [`SaSalesRefService`](ErpWeb.Core/Sales/SaSalesRefService.cs)). Register in [`CoreServiceCollectionExtensions.cs`](ErpWeb.Core/CoreServiceCollectionExtensions.cs).

Reuse `IvMasterOperationResult<T>` / `IvMasterErrorCode` like sales refs.

**Continuous `AdSmNum`** (PK `CompanyCode, BranchCode, NumCd`):

- List current branch, order by `NumCd`
- Get/Save/Delete by `NumCd` (branch from scope)
- New: NumCd required, max 10, uppercased
- Edit: NumCd read-only
- Validate: `Seq >= 1`, `TotLength > Prefix.Length`, Prefix max 10
- Stamp `LocationCode` / `Updated` / `UpdatedUID` from write scope
- Reject save if any `AdSmNumDate` exists for same tenant+NumCd

**Period `AdSmNumDate`** (identity `uid`; unique `Company, Branch, NumCd, Year, Month`):

- List current branch, order `NumCd`, `Year DESC`, `Month DESC`, `uid DESC`
- Get/Save/Delete by `Uid`; concurrency via `RowVersion`
- New: NumCd max 10; Year `0..2099`; Month `0..12`; persist Year/Month as `0` not null
- Edit: NumCd, Year, Month read-only (business key)
- Validate: `TotLength > 0`, `Seq >= 1`; if `NumberingFormat` set it must contain `{1}`
- Copy Location/User on insert/update
- Reject save if an `AdSmNum` row exists for same tenant+NumCd
- Duplicate unique key → friendly validation message

Delete: always allowed at config level (no invoice-in-use scan). Confirm popup like UOM.

## UI

[`ErpWeb.UI/Sales/Masters/AdSmNumList.razor`](ErpWeb.UI/Sales/Masters/AdSmNumList.razor) (+ `.razor.cs`)

- Route `/sales/numbering/continuous`
- Grid: NumCd, Prefix, TotLength, Seq, Description
- Popup: NumCd, Prefix, TotLength, Seq, NumDes (no Active checkbox)
- Key: `NumCd`

[`ErpWeb.UI/Sales/Masters/AdSmNumDateList.razor`](ErpWeb.UI/Sales/Masters/AdSmNumDateList.razor) (+ `.razor.cs`)

- Route `/sales/numbering/period`
- Grid: NumCd, Year, Month, Prefix, TotLength, Seq, Delimiter, Format
- Popup: NumCd, Year, Month, Prefix, TotLength, Seq, NumberingDelimeter, NumberingFormat, NumDes
- Key: `Uid` (hidden from user)
- Wider popup (~50vw) like currency rates

Toolbar: NEW, DELETE, EXPORT (same buttons as [`SaKeyedRefListPageBase`](ErpWeb.UI/Sales/Masters/SaKeyedRefListPageBase.cs)). Row: VIEW, EDIT. Shared page base in the same folder injecting `IAdSmNumAdminService` (copy UOM toolbar/permission/delete flow; skip activate).

## Menus

[`ErpWeb/Menus/menus.xml`](ErpWeb/Menus/menus.xml) under `SA_MASTER`:

- `SA_SM_NUM` — Continuous Numbers — `/sales/numbering/continuous`
- `SA_SM_NUM_DATE` — Period Numbers — `/sales/numbering/period`

Constants on [`MenuCodes.cs`](ErpWeb.Core/Menus/MenuCodes.cs). Grant ADD/EDIT/DELETE in [`scripts/init-menu-access.sql`](scripts/init-menu-access.sql) with the other `SA_*` masters.

## Tests

[`ErpWeb.Tests/AdSmNumAdminServiceTests.cs`](ErpWeb.Tests/AdSmNumAdminServiceTests.cs) (SQLite, same tenant helper as invoice tests):

- Continuous create/list/edit/delete for current branch
- Period create including new month row; duplicate Year/Month fails
- Mutual exclusion both directions
- Invalid Seq / TotLength rejected
- Other branch rows not listed
