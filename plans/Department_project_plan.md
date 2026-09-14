---
name: Department & Project masters
overview: Add company+branch scoped MsDept and MsProject reference tables under the ADMIN_MASTER menu group, expose them through a new ErpWeb.Core/Admin reference service and two list/popup pages under ErpWeb.UI/Admin/Master, then replace the free-text Department/Project boxes on all eight transaction entry pages with the existing IvCodeComboBox. Sales Invoice is the only document that needs new columns.
todos:
  - id: schema
    content: scripts/create-ms-dept-project.sql — additive, idempotent MsDept + MsProject
    status: not-started
  - id: model
    content: Entities, EF configurations (1:1 to SQL names), AppDbContext DbSets
    status: not-started
  - id: core
    content: ErpWeb.Core/Admin — IMsRefService + MsRefService, RequireBranchScopeAsync gate, reference-aware delete guard, shared MsRefLookupRules validator
    status: not-started
  - id: ui-masters
    content: MsRefListPageBase + MsDeptList / MsProjectList under ErpWeb.UI/Admin/Master
    status: not-started
  - id: menus
    content: menus.xml + MenuCodes.cs + init menu SQL (must ship together)
    status: not-started
  - id: wire-sales
    content: SaSo / SaDo / SaCdn / SaInvoice Dept+Project lookups (SaCdn converts existing boxes)
    status: not-started
  - id: wire-purchase
    content: PoPr / PoOrder / PoInvoice / PoCdn Dept+Project lookups
    status: not-started
  - id: sainvoice-columns
    content: Add Dept + ProjID to Sales Invoice end-to-end via scripts/create-sainvoice.sql
    status: not-started
  - id: tests
    content: MsRefServiceTests incl. delete-blocked-when-referenced, scope isolation, legacy-orphan save behavior
    status: not-started
isProject: false
---

# Department & Project Reference Masters — Implementation Plan

Target workspace: `c:\wincom\net10projects`

> **Revision status:** revised per the final review. All decisions formerly listed as "Open decisions"
> are now locked in §2. If any later section appears to contradict §2, §2 wins.

## 1. Summary

Several transaction entry pages already capture a **Department** and a **Project**, but every one
of them is a plain free-text `DxTextBox` (max 20 chars) with **no master table behind it**. Nothing
validates or controls what gets typed, so the values cannot be trusted as reporting dimensions.

This plan adds two reference masters — `MsDept` and `MsProject` — under the existing
`Admin → Master` menu group, then wires them into the transaction pages as code lookups.

Sales Invoice is the only document that needs **new columns**; every other table already has them.

## 2. Locked decisions

These are implementation requirements, not options. The earlier draft's "Open decisions" section is
superseded by this table.

| Decision | Choice |
| --- | --- |
| Tenant scope | **Company + Branch** (matches `AdSmNum` / the numbering masters) |
| Scope implementation | `RequireBranchScopeAsync`, copied from `AdSmNumAdminService` / `IvInventoryRefService` — **not** company-only Country scope |
| Project fields | Start/End dates, Status (Active/Closed), Customer/contract link, Budget amount, Default Department, Manager/owner (free text) |
| Sales Invoice | **In scope** — currently has neither `Dept` nor `ProjID` |
| Menu codes | `ADMIN_DEPT` / `ADMIN_PROJECT` (matches `ADMIN_USERS` / `ADMIN_ROLES` / `ADMIN_COMPANY`) |
| Lookup population | Query `db.MsDepts` / `db.MsProjects` **directly** inside each transaction service's existing `GetLookupsAsync` — do **not** inject `IMsRefService` into the eight transaction services (see Phase 5.1 and Phases 5-6) |
| `ListActiveLookupsAsync` | **Master UI only** (Project's default-department selector). **Never** a transaction lookup path |
| EF/SQL naming | **1:1** mapping to the Phase 1 SQL column names. No legacy aliases (`Active` / `Created` / `UserID` / `UpdatedUID`) |
| Code width | `DeptCode` / `ProjCode` **`nvarchar(20)`**; `GlCode` **`nvarchar(20)`** |
| `ManagerEmpId` | **Free text** until an employee master exists. No employee combo |
| Optional codes | Blank is allowed |
| New/changed codes | Must **exist**. Active status is enforced per the documented rule in Phase 5.1 — not assumed |
| Historical orphan codes | May remain **unchanged** when editing an old document |
| `SaDoDetail.Dept` | Line-level free text; **out of this UI pass** (documented, not an omission) |
| `PoPurItem` / `PoDesc` `Dept` | Delete-guard coverage only; item-level combo migration is future scope |
| SaInvoice DDL path | `scripts/create-sainvoice.sql` — confirmed live additive path. No second path |
| Inventory ledger Dept/Project | **Out of scope for v1**; project-level stock cost is a later slice |
| Menu changes | `menus.xml` + `MenuCodes.cs` + init SQL ship as **one atomic change set** |
| Project "actual" cost | **Derived** by reporting over transaction headers; no `ActualAmnt` column |

## 3. Current state — where Dept/Project appear today

Every column below is **`nvarchar(20)`**.

| Entity | Dept | Project | EF configuration |
| --- | --- | --- | --- |
| `SaSo` | — | `ProjId` → column `ProjID` | `SaSoConfiguration.cs:65` |
| `SaDo` | — | `ProjId` → `ProjID` | `SaDoConfiguration.cs:61` |
| `SaDoDetail` | `Dept` | — | `SaDoDetailConfiguration.cs:49` |
| `SaCdn` | `Dept` | `ProjId` → `ProjID` | `SaCdnConfiguration.cs:43,45` |
| `SaInvoice` | **missing** | **missing** | — |
| `PoPr` | `DeptCode` | `ProjId` → `ProjID` | `PoPrConfiguration.cs:20,31` |
| `PoOrder` | `DeptCode` | `ProjId` → `ProjID` | `PoOrderConfiguration.cs:57,66` |
| `PoOrderDetail` | — | `ProjId` → `ProjID` | `PoOrderDetailConfiguration.cs:51` |
| `PoInvoice` | `Dept` | `ProjId` → `ProjID` | `PoInvoiceConfiguration.cs:43,45` |
| `PoCdn` | `Dept` | `ProjId` → `ProjID` | `PoCdnConfiguration.cs:46,48` |
| `PoPurItem` | `Dept` | — | `PoPurItemConfiguration.cs:22` |
| `PoDesc` | `DeptCode` | — | `PoDescConfiguration.cs:16` |

### 3.1 Nothing sits behind them

- No Department or Project master entity, configuration, or `DbSet` exists anywhere.
- `HRDept` and `MsDepartment` appear **only in legacy planning docs**
  (`plans/po-logic.md:193`, `plans/tc_approval_sql_seed_098c1247.plan.md:135`) — carry-overs from
  `ERP_5.5`, never ported.
- `PoSupplier.Department`/`Department2`/`Department3`/`Department4` (`nvarchar(50)`) and
  `SaCust` / `SaCustContact.Department` are **contact-person labels** — a different concept.
  Do not attempt to align them with `MsDept`.
- `ErpWeb/docs/Invoice-entry logic.md` already specifies **DEPARTMENT** and **PROJECT ID** as
  dropdowns on the invoice header, and `plans/invoice_header_dropdowns_eea86f19.plan.md` parked
  them as explicitly out of scope ("Out of scope: DEPARTMENT / PROJECT ID"). Phase 7 closes that gap.

### 3.2 UI presence today (drives the Phase 5/6 edit list)

Verified in the razor files — this is what actually has to change, and where the original draft was
wrong about `SaCdn`:

| Page | Dept control | Project control | Action |
| --- | --- | --- | --- |
| `SaSo.razor` | none | `DxTextBox` L180-182 | swap Project to combo |
| `SaDo.razor` | none (header) | `DxTextBox` L163-165 | swap Project to combo |
| `SaCdn.razor` | **`DxTextBox` L199 (already exists)** | **`DxTextBox` L172-174 (already exists)** | **convert both** to combos |
| `SaInvoice.razor` | none | none | **add** both (Phase 7) |
| `PoPr.razor` | `DxTextBox` | `DxTextBox` | convert both |
| `PoOrder.razor` | `DxTextBox` | `DxTextBox` | convert both |
| `PoInvoice.razor` | `DxTextBox` | `DxTextBox` | convert both |
| `PoCdn.razor` | **none** | **none** | **add** both |

> `SaCdn` **already has Department and Project UI**. The original draft told the reader to "add the
> Department field" to `SaCdn`; that was incorrect and would have created a duplicate control.

## 4. Reuse targets

Copy these; do not invent new shapes.

| Concern | Location |
| --- | --- |
| Result envelope | `ErpWeb.Core/Inventory/IvMasterResults.cs` — `IvMasterOperationResult<T>`, `DeleteCheckResult`, `IvMasterReferenceHit`, `IvMasterErrorCode` |
| **Scope + permission gate** | `ErpWeb.Core/Numbering/AdSmNumAdminService.cs` — `RequireBranchScopeAsync` (~L949); also `ErpWeb.Core/Inventory/IvInventoryRefService.cs` (~L2121) |
| Per-entity CRUD shape | `SaSalesRefService.cs` — Country block at ~L765-955 |
| Delete guard | `SaSalesRefService.CanDeleteCountriesAsync` (L914) → `CountCountryReferencesBulkAsync` → `BuildDeleteCheck` (L3240) |
| Master list page | `ErpWeb.UI/Sales/Masters/SaCountryList.razor(.cs)`; branch-scoped admin base `ErpWeb.UI/Admin/Master/AdSmNumListPageBase.cs` |
| Lookup row type | `IvCodeLookupRow { Code, Desc, Rate, DisplayText }` — `ErpWeb.Core/Inventory/IvInventoryLookupService.cs:8` |
| Combo widget | `ErpWeb.UI/Inventory/Lookups/IvCodeComboBox.razor.cs` — `Data`, `Value`, `ValueChanged`, `Enabled`, `ReadOnly`, `IsLoading`, `InputCssClass`, `NullText` |
| Menu seed template | `scripts/init-pocdn-menu.sql` |
| Idempotent create script | `scripts/init-sales-masters.sql`; column-add pattern `scripts/create-sainvoice.sql` |
| Tenant-scoped EF config | `ErpWeb.Model/Configurations/Sales/SaCurrencyConfiguration.cs` — **structure only** (`ToTable`, composite `HasKey`, `HasMaxLength`). Its legacy column renames (`Active`/`Created`/`UserID`/`UpdatedUID`) apply to *pre-existing* tables and must **not** be copied (see §2) |
| Parity guard | `ErpWeb.Tests/MenuDeploymentParityTests.cs` |

**Additive-lookup precedent:** `SaInvoiceOperationResult.OkLookups(..., IReadOnlyList<IvCodeLookupRow>? salesReps = null)`
at `ErpWeb.Core/Sales/ISaInvoiceService.cs:68` shows that appending an **optional trailing parameter**
to an `OkLookups` factory is the established, non-breaking way to extend lookups.

## 5. Menu deployment trap (read before adding any page)

`ErpWeb/Program.cs:121` calls `MenuSyncService.SyncFromXmlAsync()` on **every startup**. It
**soft-disables** (`IsActive = 0`) every `dbo.Menu` row whose `MenuCode` is absent from
`ErpWeb/Menus/menus.xml`. `AccessRightService.EnsureCacheAsync` filters on `menu.IsActive`, so a
soft-disabled menu is not merely missing from the nav — `MenuAuthorize` redirects its page to
`/unauthorized`. Administrators on `HasAdminBypass()` can still reach the URL by typing it, which
**masks the failure**.

Adding a page therefore requires **all three together**:

1. `ErpWeb/Menus/menus.xml`
2. `ErpWeb.Core/Menus/MenuCodes.cs`
3. `scripts/init-ms-dept-project-menu.sql`

The XML's auto-ACCESS mapping only applies to menus the sync itself *inserts*. If SQL already
created the row, the sync sees it as "updated" and adds no mapping — hence the explicit
`dbo.MenuPermission` inserts in step 3.

`MenuDeploymentParityTests.Every_declared_menu_code_is_present_in_the_shipped_menus_xml` fails the
build if step 2 lands without step 1. **Keep that guard.**

---

## Phase 1 — Schema (blocking)

**New file:** `scripts/create-ms-dept-project.sql`

Additive and idempotent (`IF OBJECT_ID(N'dbo.X', N'U') IS NULL` guard), matching
`scripts/init-sales-masters.sql`. Manual DBA-run only — **never** at app startup.

> The column names below are **authoritative**. Phase 2 maps EF to them **1:1** with no aliases.

**`dbo.MsDept`**

| Column | Type | Notes |
| --- | --- | --- |
| `CompanyCode` | `nvarchar(10) NOT NULL` | PK part |
| `BranchCode` | `nvarchar(10) NOT NULL` | PK part |
| `DeptCode` | `nvarchar(20) NOT NULL` | PK part — **must match transaction width** |
| `DeptName` | `nvarchar(100) NULL` | |
| `ManagerEmpId` | `nvarchar(20) NULL` | free text — no employee master exists |
| `GlCode` | `nvarchar(20) NULL` | cost centre |
| `IsActive` | `bit NOT NULL DEFAULT (1)` | |
| `Remarks` | `nvarchar(500) NULL` | |
| `CreatedDate` | `datetime2 NULL` | |
| `CreatedBy` | `nvarchar(20) NULL` | |
| `ModifiedDate` | `datetime2 NULL` | |
| `ModifiedBy` | `nvarchar(20) NULL` | |
| `RowVersion` | `rowversion NOT NULL` | |

`CONSTRAINT PK_MsDept PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DeptCode)`

**`dbo.MsProject`**

| Column | Type | Notes |
| --- | --- | --- |
| `CompanyCode` | `nvarchar(10) NOT NULL` | PK part |
| `BranchCode` | `nvarchar(10) NOT NULL` | PK part |
| `ProjCode` | `nvarchar(20) NOT NULL` | PK part — **must match `ProjID` width** |
| `ProjName` | `nvarchar(150) NULL` | |
| `CustCode` | `nvarchar(20) NULL` | customer / contract link (same company scope as `SaCust`) |
| `DeptCode` | `nvarchar(20) NULL` | default department (logical FK → `MsDept`) |
| `ManagerEmpId` | `nvarchar(20) NULL` | free text — no employee master exists |
| `StartDate` | `date NULL` | |
| `EndDate` | `date NULL` | planned end |
| `CloseDate` | `date NULL` | actual close |
| `Status` | `nvarchar(20) NOT NULL DEFAULT (N'ACTIVE')` | `ACTIVE` / `CLOSED` |
| `BudgetAmnt` | `decimal(18,4) NULL` | budget only — actual is derived |
| `Remarks` | `nvarchar(500) NULL` | |
| audit + `RowVersion` | | as above |

`CONSTRAINT PK_MsProject PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, ProjCode)`

> **Width is load-bearing.** The transaction columns are `nvarchar(20)` and the save paths already
> call `TruncateOptional(..., 20)` (e.g. `SaSoService.cs:1869`, `SaDoService.cs:2451`,
> `SaCdnService.cs:536-537`, `PoPrService.cs:453-459`). A wider master code would silently truncate
> and the lookup would no longer match the stored value.

**Acceptance:** script runs clean twice (idempotent) against a scratch DB; both tables exist with the
PKs above.

---

## Phase 2 — Model

**New files**

- `ErpWeb.Model/Entities/MsDept.cs`
- `ErpWeb.Model/Entities/MsProject.cs`
- `ErpWeb.Model/Configurations/MsDeptConfiguration.cs`
- `ErpWeb.Model/Configurations/MsProjectConfiguration.cs`

Place the entities at the **entity root** (alongside `MsRunningNo.cs`, `AdSmNum.cs`) rather than
creating a new `Entities/Admin/` folder — that matches the existing convention for `Ms`/`Ad` tables.

**Configurations** follow the *structure* of `Configurations/Sales/SaCurrencyConfiguration.cs`:
`builder.ToTable("MsDept")`, composite `HasKey`, `HasMaxLength` on every string.

**Column mapping is 1:1 — locked.** These are brand-new tables, so they take the modern names from
Phase 1 with **no legacy aliases**:

- `IsActive` stays `IsActive` — do **not** add `.HasColumnName("Active")`
- `CreatedDate` / `CreatedBy` stay as-is — do **not** rename to `Created` / `UserID`
- `ModifiedDate` / `ModifiedBy` stay as-is — do **not** rename to `Updated` / `UpdatedUID`

> `SaCurrencyConfiguration` uses those aliases because `SaCurrency` is a pre-existing table whose
> physical columns predate the current convention (the same reason `scripts/create-sainvoice.sql`
> creates `SaTaxGroup` with `Created`/`UserID`/`Updated`/`UpdatedUID`). Copying the aliases onto a
> brand-new table would write to columns that do not exist. Structure from `SaCurrency`; naming from
> Phase 1.

**Modified file:** `ErpWeb.Model/Data/AppDbContext.cs` — add two `DbSet` lines next to the existing
`AdSmNums` / `AdSmNumDates` block:

- `public DbSet<MsDept> MsDepts => Set<MsDept>();`
- `public DbSet<MsProject> MsProjects => Set<MsProject>();`

> Configurations are picked up automatically — `OnModelCreating` calls
> `ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly)`, so no registration is needed.

---

## Phase 3 — Core service

**New files**

- `ErpWeb.Core/Admin/IMsRefService.cs`
- `ErpWeb.Core/Admin/MsRefService.cs`
- `ErpWeb.Core/Admin/MsRefLookupRules.cs` — shared legacy-aware validator (see Phase 5.1)

**Dependencies** (copy the `AdSmNumAdminService` constructor shape — `ErpWeb.Core/Numbering/AdSmNumAdminService.cs`):
`IDbContextFactory<AppDbContext>`, `IInventoryTenantContext`, `IAccessRightService`, `ICurrentDateService`.

**Methods** — five per entity, mirroring the Country CRUD block:

| Department | Project |
| --- | --- |
| `ListDepartmentsAsync(ct)` | `ListProjectsAsync(ct)` |
| `GetDepartmentAsync(code, ct)` | `GetProjectAsync(code, ct)` |
| `SaveDepartmentAsync(model, isNew, ct)` | `SaveProjectAsync(model, isNew, ct)` |
| `SetDepartmentActiveAsync(tokens, isActive, ct)` | `SetProjectActiveAsync(tokens, isActive, ct)` |
| `CanDeleteDepartmentsAsync(codes, ct)` | `CanDeleteProjectsAsync(codes, ct)` |
| `DeleteDepartmentsAsync(codes, ct)` | `DeleteProjectsAsync(codes, ct)` |

Plus one helper that is **master UI only**:

- `ListActiveLookupsAsync(ct)` → `(IReadOnlyList<IvCodeLookupRow> Departments, IReadOnlyList<IvCodeLookupRow> Projects)`,
  active rows only, ordered by code.

> **`ListActiveLookupsAsync` is never called from a transaction service.** It exists solely so the
> `MsProjectList` popup can populate its default-department selector. Transaction pages get their
> lists by querying the DbSets directly in their own `GetLookupsAsync` (Phases 5-6). If the
> Project popup ends up querying active departments inline, drop this method entirely.

**Scope gate:** every method starts with
`var ctx = await RequireBranchScopeAsync(MenuCodes.AdminDept /* or AdminProject */, permission, ct)`
and returns `Fail…(ctx.Error.Value)` when set.

- Portal from `AdSmNumAdminService.cs:949` (or `IvInventoryRefService.cs:2121`).
- **Do not** copy `SaSalesRefService.RequireCompanyScopeAsync` for authorization — it is
  company-only and would not enforce the locked Company + Branch scope.
- Permissions: `Access` for reads, `Add`/`Edit` for `Save`, `Delete` for the delete pair.
- Apply the same branch scope to **delete and reference checks**, not just CRUD.

**Modified file:** `ErpWeb.Core/CoreServiceCollectionExtensions.cs` — one line in `AddErpWebCore`:

- `services.AddScoped<IMsRefService, MsRefService>();`

### Phase 3.1 — Delete guard: count header *and* line references

`CanDelete…` must block when the code is still referenced, scoped to the caller's Company + Branch.
Build the reference map (the `IvReferenceCount` / `BuildDeleteCheck` helper at
`SaSalesRefService.cs:3240` does the formatting) and cover **all** of these:

| Dept references (`DeptCode` / `Dept`) | Project references (`ProjId` → `ProjID`) |
| --- | --- |
| `SaCdn.Dept` | `SaSo.ProjId` |
| `SaDoDetail.Dept` | `SaDo.ProjId` |
| `SaInvoice.Dept` *(Phase 7)* | `SaCdn.ProjId` |
| `PoPr.DeptCode` | `SaInvoice.ProjID` *(Phase 7)* |
| `PoOrder.DeptCode` | `PoPr.ProjId` |
| `PoInvoice.Dept` | `PoOrder.ProjId` |
| `PoCdn.Dept` | `PoOrderDetail.ProjId` |
| `PoPurItem.Dept` *(master default)* | `PoInvoice.ProjId` |
| `PoDesc.DeptCode` *(master default)* | `PoCdn.ProjId` |
| `MsProject.DeptCode` *(default department)* | — |

`MsProject.CustCode` deliberately does **not** participate: it references a customer, not another
project.

Return the hits as `IvMasterReferenceHit` entries so the UI can explain **why** the delete was
refused (`SaRefListMessages.FormatDeleteBlocked` renders them).

> `PoPurItem` / `PoDesc` appear here as **reference coverage only**. Migrating those masters to the
> combo is future scope; the guard is required regardless so a referenced code cannot be deleted.

---

## Phase 4 — UI masters + menu (ship as one atomic change)

**New files**

- `ErpWeb.UI/Admin/Master/MsRefListPageBase.cs`
- `ErpWeb.UI/Admin/Master/MsDeptList.razor` + `.razor.cs`
- `ErpWeb.UI/Admin/Master/MsProjectList.razor` + `.razor.cs`

Do **not** reuse `SaRefListPageBase` / `SaKeyedRefListPageBase` / `SaCodeRefListPageBase` — they
hard-inject `ISaSalesRefService`. Create `MsRefListPageBase<TRow>` alongside them, injecting
`IAccessRightService` + `IMsRefService`, mirroring the toolbar (`NEW` / `ACTIVATE` / `DEACTIVATE` /
`DELETE` / `EXPORT`), the `CommonDataGridEx` usage, and the three popups (edit, confirm-view, confirm-delete)
from `SaCountryList.razor(.cs)`. For the branch-scoped loading/selector behaviour, follow
`AdSmNumListPageBase.cs`.

`MsProjectList` needs a richer popup than `SaCountryList`: code, name, customer lookup, default
department lookup (via `ListActiveLookupsAsync`), manager, start/end/close dates, status, budget,
remarks.

**Modified file:** `ErpWeb.UI/Admin/_Imports.razor` — add `@using ErpWeb.UI.Inventory.Lookups` so
`IvCodeComboBox` is available in the new pages (the Admin imports currently omit it).

### 4.1 Menu triple

**`ErpWeb/Menus/menus.xml`** — under the existing `ADMIN_MASTER` block (currently
"Continuous Numbers" `SortOrder="1"` and "Period Numbers" `SortOrder="2"`):

```xml
<Menu Code="ADMIN_DEPT"    Name="Departments" Route="/admin/departments" SortOrder="3" />
<Menu Code="ADMIN_PROJECT" Name="Projects"    Route="/admin/projects"    SortOrder="4" />
```

**`ErpWeb.Core/Menus/MenuCodes.cs`** — add next to `AdminMaster`:

- `public const string AdminDept = "ADMIN_DEPT";`
- `public const string AdminProject = "ADMIN_PROJECT";`

**New file:** `scripts/init-ms-dept-project-menu.sql` — modelled on `scripts/init-pocdn-menu.sql`:

1. Insert the two `dbo.Menu` rows (only `IF NOT EXISTS`), parented on `ADMIN_MASTER`,
   `IsActive = 1`, `AlwaysVisible = 0`.
2. Insert `dbo.MenuPermission` mappings for `ACCESS`, `ADD`, `EDIT`, `DELETE`, `EXPORT`
   (EXPORT because both list pages ship an EXPORT toolbar button), guarded by a `NOT EXISTS` check.
3. Print a reminder that `menus.xml` must carry the same two codes.

---

## Phase 5 — Wire the sales transactions

For **each** service below, three coordinated edits:

1. Add `Departments` + `Projects` `init` properties (default `[]`) to the operation-result type.
2. Append optional trailing parameters to the `OkLookups` factory and populate them.
3. Populate both lists in `GetLookupsAsync` by querying `db.MsDepts` / `db.MsProjects`
   (company + branch scoped, `IsActive`, ordered by code) and projecting to `IvCodeLookupRow`.

Then in each page's `LoadAsync`, assign `Departments = lookups.Departments.ToList();`
`Projects = lookups.Projects.ToList();` and swap the `DxTextBox` for `IvCodeComboBox`.

| Page | Control state today | Result type / factory | Service |
| --- | --- | --- | --- |
| `SaSo.razor(.cs)` | Project `DxTextBox` L180-182 | `ISaSoService.cs:33`, `OkLookups` L57-66 | `SaSoService.cs:56` |
| `SaDo.razor(.cs)` | Project `DxTextBox` L163-165 | `ISaDoService.cs:36`, `OkLookups` L64 | `SaDoService.cs:76` |
| `SaCdn.razor(.cs)` | **Dept L199 + Project L172-174 already present as text boxes** | `ISaCdnService.cs:38`, `OkLookups` L70 | `SaCdnService.cs:71` |
| `SaInvoice.razor(.cs)` | none — **add both** | `ISaInvoiceService.cs:36`, `OkLookups` L68 | `SaInvoiceService.cs:87` |

**SaCdn note (corrects the earlier draft):** `SaCdn` already exposes Department (L199) and Project
(L172-174). Convert both existing text boxes to `IvCodeComboBox`. Do **not** add a new Department
field.

**Scope boundary — `SaDoDetail.Dept`:** `SaDo` wires **Project at the header only** (`SaDo.ProjId`).
`SaDoDetail.Dept` is a line-level free-text field and is explicitly **out of this UI migration**.
This is a deliberate boundary, not a missed item.

**No save-path changes are needed** for documents whose columns already exist: the stored value stays
the code and the existing `TruncateOptional(..., 20)` calls are unchanged — except for the new
validation below.

### Phase 5.1 — Legacy-aware validation (applies to every wired service)

Existing rows may hold free-text Dept/Project values that do not exist in the new masters. Navigate
straight to the locked rule:

- Blank is allowed for these optional fields.
- A **new or changed** non-blank code **must exist**.
- **Active status.** A new or changed code must also be active. This is a *documented* rule, not an
  assumption carried in by the review — it matches the existing pattern throughout the codebase:
  every master lookup filters active rows (e.g. `PoPrService.cs:127` vendors, `SaCdnService.cs:92`
  customers), and every save-path code check tests active, all via
  `AnyAsync(x => ... && x.IsActive == true)` (`SaSoService.cs:1590`, `SaDoService.cs:2058`,
  `SaCdnService.cs:1809`, `SaInvoiceService.cs:1775`).
- **Inactive-selection exception.** If the business requires an inactive code to remain selectable
  (for example, closing out an old document against a deactivated department), that exception is
  implemented in exactly two places — the shared helper below and the dropdown query — and recorded
  here before implementation. Do not scatter per-service exceptions.
- An **existing historical orphan** code may remain unchanged when editing an old document.
- If the operator **changes** the field, the new value must pass normal master validation.

Implement this **once** in `ErpWeb.Core/Admin/MsRefLookupRules.cs` and call it from each sales and
purchase save path, so all eight services behave identically. Pass the previously persisted value
alongside the incoming value; the rule only rejects when the value is dirty.

---

## Phase 6 — Wire the purchase transactions

Identical treatment, including Phase 5.1 validation via the same shared helper.

| Page | Control state today | Result type / factory | Service |
| --- | --- | --- | --- |
| `PoPr.razor(.cs)` | Dept L87, Project L113 (text boxes) | `PoPrLookups` — `IPoPrService.cs:151` | `PoPrService.cs:74` |
| `PoOrder.razor(.cs)` | Project L186, Dept L223 (text boxes) | `PoOrderLookups` — `IPoOrderService.cs:193`; page `ApplyLookups` at `PoOrder.razor.cs:378` | `PoOrderService.cs:218` |
| `PoInvoice.razor(.cs)` | Dept/Project L171-176 (text boxes) | `PoInvoiceLookups` — `IPoInvoiceService.cs:182` | `PoInvoiceService.cs:52` |
| `PoCdn.razor(.cs)` | **neither control exists** | `IPoCdnService.cs:38` (`PayCodes` init style — no `Lookups` class) | `PoCdnService.cs:138` |

**`PoCdn` note (confirmed correct in the earlier draft):** the entity carries `Dept` + `ProjId` but
the page exposes neither — add both.

`PoPurItem.Dept` and `PoDesc.DeptCode` stay free text in v1; they are covered by the delete guard and
recorded as future scope.

---

## Phase 7 — Sales Invoice columns

`SaInvoice` has **neither** property today (`ErpWeb.Model/Entities/Sales/SaInvoice.cs`). Add both
end-to-end.

**DDL path is confirmed:** `scripts/create-sainvoice.sql` is the live, additive, idempotent path for
this table (manual DBA script, never run at app startup; `IF OBJECT_ID` for tables and
`IF COL_LENGTH` for every column add). It already carries the whole `SaInvoice` column family
(L119-188). Do **not** open a second schema path.

1. `scripts/create-sainvoice.sql` — add `Dept nvarchar(20) NULL` and `ProjID nvarchar(20) NULL`,
   each behind an idempotent `COL_LENGTH(N'dbo.SaInvoice', N'Dept') IS NULL` guard, alongside the
   other column adds.
2. `ErpWeb.Model/Entities/Sales/SaInvoice.cs` — `public string? Dept { get; set; }` and
   `public string? ProjId { get; set; }`.
3. `ErpWeb.Model/Configurations/Sales/SaInvoiceConfiguration.cs` — `Dept` `HasMaxLength(20)`;
   `ProjId` mapped `.HasColumnName("ProjID").HasMaxLength(20)`.
4. `ErpWeb.Core/Sales/ISaInvoiceService.cs` — add both to `SaInvoiceDocument` and to the save request.
5. `ErpWeb.Core/Sales/SaInvoiceService.cs` — map on load/save via `TruncateOptional(..., 20)` and the
   Phase 5.1 legacy-aware validation, matching the sibling documents.
6. `ErpWeb.UI/Sales/Transactions/SaInvoice.razor(.cs)` — add the two `IvCodeComboBox` fields to the
   document-identity card.

This closes the gap documented in `ErpWeb/docs/Invoice-entry logic.md` and parked in
`plans/invoice_header_dropdowns_eea86f19.plan.md`.

---

## Phase 8 — Verification

1. `dotnet build ErpWeb.slnx --nologo -v:q` — expect **0 errors** (~22 s baseline).
2. `dotnet test ErpWeb.Tests/ErpWeb.Tests.csproj` — no regressions against the 953-green baseline.
3. Run `MenuDeploymentParityTests` alone to prove `menus.xml` ↔ `MenuCodes.cs` agree.
4. New `ErpWeb.Tests/MsRefServiceTests.cs` (model it on `PoMasterRefServiceTests.cs`):
   - list / get / save / duplicate-key rejection;
   - `CanDeleteAsync` **blocked** when a `ProjCode` is referenced by `SaSo`, and a second case
     blocked by a **line-level** reference (`PoOrderDetail`);
   - branch/company scope isolation (a code from another company **or branch** is not visible);
   - access-denied path when `IAccessRightService.CanAsync` returns false;
   - **legacy-orphan save behavior:** editing a document whose stored Dept/Project is unknown succeeds
     when the field is untouched, and fails when the field is changed to another unknown value.
5. Manual smoke: create a Department and a Project at `/admin/departments` and `/admin/projects`;
   confirm both appear in the SO and PO dropdowns; confirm deleting a referenced code is refused with
   a readable reason.

---

## File manifest

**New (15)**

- `scripts/create-ms-dept-project.sql`
- `scripts/init-ms-dept-project-menu.sql`
- `ErpWeb.Model/Entities/MsDept.cs`, `ErpWeb.Model/Entities/MsProject.cs`
- `ErpWeb.Model/Configurations/MsDeptConfiguration.cs`, `ErpWeb.Model/Configurations/MsProjectConfiguration.cs`
- `ErpWeb.Core/Admin/IMsRefService.cs`, `ErpWeb.Core/Admin/MsRefService.cs`, `ErpWeb.Core/Admin/MsRefLookupRules.cs`
- `ErpWeb.UI/Admin/Master/MsRefListPageBase.cs`
- `ErpWeb.UI/Admin/Master/MsDeptList.razor` + `.razor.cs`
- `ErpWeb.UI/Admin/Master/MsProjectList.razor` + `.razor.cs`
- `ErpWeb.Tests/MsRefServiceTests.cs`

**Modified (~26)**

- `ErpWeb.Model/Data/AppDbContext.cs`
- `ErpWeb.Core/CoreServiceCollectionExtensions.cs`
- `ErpWeb.Core/Menus/MenuCodes.cs`
- `ErpWeb/Menus/menus.xml`
- `ErpWeb.UI/Admin/_Imports.razor`
- Sales: `SaSo.razor(.cs)`, `SaDo.razor(.cs)`, `SaCdn.razor(.cs)`, `SaInvoice.razor(.cs)`
- Sales core: `ISaSoService.cs`, `SaSoService.cs`, `ISaDoService.cs`, `SaDoService.cs`,
  `ISaCdnService.cs`, `SaCdnService.cs`, `ISaInvoiceService.cs`, `SaInvoiceService.cs`
- Sales model: `SaInvoice.cs`, `SaInvoiceConfiguration.cs`, `scripts/create-sainvoice.sql`
- Purchase: `PoPr.razor(.cs)`, `PoOrder.razor(.cs)`, `PoInvoice.razor(.cs)`, `PoCdn.razor(.cs)`
- Purchase core: `IPoPrService.cs`, `PoPrService.cs`, `IPoOrderService.cs`, `PoOrderService.cs`,
  `IPoInvoiceService.cs`, `PoInvoiceService.cs`, `IPoCdnService.cs`, `PoCdnService.cs`

## Out of scope

- **Project dimension on the inventory ledger.** `IvTrxBatch` and `IvTrxBatchDetail` have no
  Proj/Dept columns, so project-level *stock* cost cannot be reported from this work alone. If KPI
  means COGS per project, that is a separate slice to be added later — not part of v1.
- **`SaDoDetail.Dept`** — line-level free-text field stays as-is. Only header-level Project on
  `SaSo` / `SaDo` participates in this migration.
- **`PoPurItem` / `PoDesc` Dept** — covered by delete guards; no combo migration in v1.
- **Employee master.** No employee entity exists, so `ManagerEmpId` stays free text and there is no
  employee lookup.
- **Field-level master audit.** No audit-trail infrastructure exists (only `IvTrxHistory` + Serilog),
  so master edits have no old/new history.
- **Contact-person "Department" text** on `PoSupplier` / `SaCust` / `SaCustContact`.
- **Collapsing `Dept` and `ProjID`.** `docs/cdn-logic.md:338` records the legacy rule
  "Department posted = `SaCDN.Dept`, or `ProjID` if `web.config PostProjAsDept=TRUE`". Keep the two
  columns distinct on transactions and let the posting layer decide.

## Risks

| Risk | Mitigation |
| --- | --- |
| Menu rows soft-disabled at startup (the trap, §5) | Ship the XML + `MenuCodes` + SQL triple together; `MenuDeploymentParityTests` guards it |
| Code width mismatch causes silent truncation | `nvarchar(20)` for `DeptCode` / `ProjCode` / `GlCode`, asserted in Phase 1 and Phase 4 |
| `OkLookups` signature change breaks the ~953 existing tests | Append **optional trailing** parameters only, per the `salesReps = null` precedent |
| Delete guard misses a reference table and orphans history | Enumerate header **and** line references explicitly (see the Phase 3 delete-guard table); scope the check to Company + Branch |
| Legacy free-text values break historical edits | Shared `MsRefLookupRules` validator with the dirty-only rule (Phase 5.1), applied identically to all eight services |
| Cross-module inconsistency (scope / validation drifting per service) | One branch-scope portal copied from `AdSmNumAdminService`; one shared validation helper; tests assert both |
| EF aliases written onto a new table | 1:1 mapping locked in §2; `SaCurrency` used for structure only |
| `SaInvoice` column additions regress posting tests | Add via `scripts/create-sainvoice.sql`; `SaInvoiceSqlServerConcurrencyTests` / `SaInvoiceServiceTests` assert column existence via projection — run them in Phase 8 |
