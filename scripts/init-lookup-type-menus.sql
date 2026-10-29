/* ============================================================================
   Menu + permission seed for the five per-family lookup maintenance screens.

   Families and their fixed placement (server-owned, see AdLookupTypeRegistry):
     INV_WAREHOUSE_TYPE      /inventory/lookups/WAREHOUSE_TYPE      under INV_MASTER, sort  8
     PO_SUPPLIER_TYPE        /purchase/lookups/SUPPLIER_TYPE        under PO_MASTER,  sort  7
     PO_SUPPLIER_SUBGROUP    /purchase/lookups/SUPPLIER_SUBGROUP    under PO_MASTER,  sort  8
     PLN_MACHINE_TYPE        /planning/lookups/MACHINE_TYPE         under PLN_MASTER, sort 16
     PLN_WORK_CENTRE_CLASS   /planning/lookups/WORK_CENTRE_CLASS    under PLN_MASTER, sort 17

   Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
   Target database: the same database as ConnectionStrings:DefaultConnection.

   Run with:  sqlcmd -S <server> -d <db> -i scripts/init-lookup-type-menus.sql

   DEPLOYMENT ORDER
     1. Verify G0 (AdLookupValue table + indexes, ADMIN_LOOKUPS permissions).
     2. Deploy the assembly AND ErpWeb/Menus/menus.xml together. Startup MenuSyncService creates the
        five rows with ACCESS applicability only; it also SOFT-DISABLES any menu row whose code is
        absent from the XML, so the XML and this script must never ship apart.
     3. Run this script to add ADD / EDIT applicability.
     4. An administrator grants roles on the Role Permissions screen. This script deliberately does
        NOT touch dbo.RoleMenuPermission: no role mass-grants, no implied least-privilege decision.

   PERMISSIONS: exactly ACCESS, ADD, EDIT. No DELETE (values are deactivated, never hard-deleted), no
   POST/ROLLBACK/EXPORT. All five codes are maintenance-only grants; assignment dropdowns on business
   screens stay ungated by them.
   ============================================================================ */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

-- ── 0. Prerequisites: fail loudly; never print success while silently skipping ────────────────────
IF OBJECT_ID(N'dbo.Menu', N'U') IS NULL
BEGIN
    ;THROW 51000, N'dbo.Menu is missing. Run the base menu scripts (init-menu-access.sql) first.', 1;
END

IF OBJECT_ID(N'dbo.Permission', N'U') IS NULL
BEGIN
    ;THROW 51000, N'dbo.Permission is missing. Run the base permission scripts first.', 1;
END

IF OBJECT_ID(N'dbo.MenuPermission', N'U') IS NULL
BEGIN
    ;THROW 51000, N'dbo.MenuPermission is missing. Run the base menu scripts first.', 1;
END

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_MASTER')
BEGIN
    ;THROW 51000, N'Parent menu INV_MASTER is missing. Run the Inventory master menu seed first.', 1;
END

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_MASTER')
BEGIN
    ;THROW 51000, N'Parent menu PO_MASTER is missing. Run the Purchase master menu seed first.', 1;
END

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_MASTER')
BEGIN
    ;THROW 51000, N'Parent menu PLN_MASTER is missing. Run the Planning master menu seed first.', 1;
END

IF (SELECT COUNT(*) FROM dbo.Permission
    WHERE PermissionCode IN (N'ACCESS', N'ADD', N'EDIT') AND IsActive = 1) <> 3
BEGIN
    ;THROW 51000, N'dbo.Permission is missing an active ACCESS, ADD or EDIT row. Seed permissions first.', 1;
END

-- ── 1. The five menu definitions ─────────────────────────────────────────────────────────────────
DECLARE @menus TABLE
(
    MenuCode   nvarchar(50)  NOT NULL PRIMARY KEY,
    MenuName   nvarchar(100) NOT NULL,
    ParentCode nvarchar(50)  NOT NULL,
    Route      nvarchar(200) NOT NULL,
    SortOrder  int           NOT NULL
);

INSERT INTO @menus (MenuCode, MenuName, ParentCode, Route, SortOrder) VALUES
    (N'INV_WAREHOUSE_TYPE',    N'Warehouse Types',      N'INV_MASTER', N'/inventory/lookups/WAREHOUSE_TYPE',    8),
    (N'PO_SUPPLIER_TYPE',      N'Supplier Types',       N'PO_MASTER',  N'/purchase/lookups/SUPPLIER_TYPE',      7),
    (N'PO_SUPPLIER_SUBGROUP',  N'Supplier Subgroups',   N'PO_MASTER',  N'/purchase/lookups/SUPPLIER_SUBGROUP',  8),
    (N'PLN_MACHINE_TYPE',      N'Machine Types',        N'PLN_MASTER', N'/planning/lookups/MACHINE_TYPE',      16),
    (N'PLN_WORK_CENTRE_CLASS', N'Work Centre Classes',  N'PLN_MASTER', N'/planning/lookups/WORK_CENTRE_CLASS', 17);

-- Insert only what is absent: an existing row may already carry an operator's name/icon edits.
INSERT INTO dbo.Menu
    (MenuCode, MenuName, ParentMenuId, Route, Icon, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
SELECT d.MenuCode, d.MenuName, parent.MenuId, d.Route, N'fa-solid fa-list-check',
       d.SortOrder, 0, 1, SYSUTCDATETIME(), N'SEED'
FROM @menus d
INNER JOIN dbo.Menu parent ON parent.MenuCode = d.ParentCode
WHERE NOT EXISTS (SELECT 1 FROM dbo.Menu m WHERE m.MenuCode = d.MenuCode);

-- Converge an existing row onto the approved parent, route, name and order.
UPDATE m
SET m.MenuName     = d.MenuName,
    m.ParentMenuId = parent.MenuId,
    m.Route        = d.Route,
    m.Icon         = N'fa-solid fa-list-check',
    m.SortOrder    = d.SortOrder,
    m.IsActive     = 1,
    m.ModifiedDate = SYSUTCDATETIME(),
    m.ModifiedBy   = N'SEED'
FROM dbo.Menu m
INNER JOIN @menus d    ON d.MenuCode = m.MenuCode
INNER JOIN dbo.Menu parent ON parent.MenuCode = d.ParentCode;

-- ── 2. Applicability: exactly ACCESS, ADD, EDIT on each of the five codes ─────────────────────────
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
CROSS JOIN dbo.Permission p
WHERE m.MenuCode IN (SELECT MenuCode FROM @menus)
  AND p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);

-- Re-activate rows an earlier state left disabled. AccessRightService filters on mp.IsActive, so a
-- disabled applicability row silently removes the permission even when a role was granted it.
UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m       ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode IN (SELECT MenuCode FROM @menus)
  AND p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT')
  AND mp.IsActive <> 1;

-- ── 3. Verification: exactly 15 active pairs, or fail ────────────────────────────────────────────
SELECT
    m.MenuCode,
    parent.MenuCode AS ParentCode,
    m.Route,
    m.SortOrder,
    p.PermissionCode,
    IsActive = mp.IsActive
FROM dbo.Menu m
INNER JOIN dbo.Menu parent      ON parent.MenuId = m.ParentMenuId
INNER JOIN dbo.MenuPermission mp ON mp.MenuId = m.MenuId
INNER JOIN dbo.Permission p      ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode IN (SELECT MenuCode FROM @menus)
  AND p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT')
ORDER BY parent.MenuCode, m.SortOrder, p.PermissionCode;

DECLARE @activePairs int = (
    SELECT COUNT(*)
    FROM dbo.Menu m
    INNER JOIN dbo.MenuPermission mp ON mp.MenuId = m.MenuId
    INNER JOIN dbo.Permission p      ON p.PermissionId = mp.PermissionId
    WHERE m.MenuCode IN (SELECT MenuCode FROM @menus)
      AND m.IsActive = 1
      AND mp.IsActive = 1
      AND p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT'));

IF @activePairs <> 15
BEGIN
    ;THROW 51001, N'Expected exactly 15 active ACCESS/ADD/EDIT MenuPermission pairs across the five lookup menus.', 1;
END

PRINT N'init-lookup-type-menus.sql: 5 menus and 15 active MenuPermission pairs ensured. Role grants are NOT made here.';
