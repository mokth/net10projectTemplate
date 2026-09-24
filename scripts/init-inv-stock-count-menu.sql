-- Menu + permission seed for the Inventory Stock Count (cycle count) document.
-- Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
-- Target database: the same database as ConnectionStrings:DefaultConnection (ERPWeb).
-- Run with:  sqlcmd -E -d ERPWeb -i scripts/init-inv-stock-count-menu.sql
--
-- MENU DEPLOYMENT COUPLING (same rule as init-pobsb-menu.sql)
--   MenuSyncService builds dbo.Menu rows from ErpWeb/Menus/menus.xml at startup and preserves MenuId,
--   but it SOFT-DISABLES every dbo.Menu row whose MenuCode is ABSENT from menus.xml. The
--   INV_STOCK_COUNT row in menus.xml and this script must therefore ship together, or
--   AccessRightService (which filters menu.IsActive) locks the screen out.
--   MenuSyncService auto-creates only the ACCESS grant for a menu it inserts; ADD / EDIT / DELETE /
--   POST / ROLLBACK / CANCEL must come from this script or no grant can ever be effective.
--
-- Run order: create-iv-stock-count.sql -> init-inv-stock-count-menu.sql
--   (init-menu-access.sql must have been run first: it creates the INV_TRANSACTIONS parent menu.)

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @invTransactionsId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'INV_TRANSACTIONS');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @invTransactionsId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_STOCK_COUNT')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'INV_STOCK_COUNT', N'Stock Count', @invTransactionsId, N'/inventory/stock-count', 9, 0, 1, SYSUTCDATETIME(), N'SEED');

    -- Re-align an existing row with the shipped XML (route / parent / sort order can drift).
    UPDATE dbo.Menu
    SET MenuName = N'Stock Count',
        ParentMenuId = @invTransactionsId,
        Route = N'/inventory/stock-count',
        SortOrder = 9,
        IsActive = 1
    WHERE MenuCode = N'INV_STOCK_COUNT';

    PRINT N'Menu row INV_STOCK_COUNT ensured (requires the matching row in menus.xml).';
END
ELSE
    PRINT N'dbo.Menu or INV_TRANSACTIONS missing - run init-menu-access.sql first, then re-run this script.';
GO

-- ── Menu permissions ─────────────────────────────────────────────────────────────────────────────
-- The document lifecycle needs all seven: ACCESS to open, ADD to create, EDIT for scope/count entry,
-- DELETE for a DRAFT sheet, POST to post the adjustment, ROLLBACK to restore it, CANCEL to abandon it.
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'POST', N'ROLLBACK', N'CANCEL')
WHERE m.MenuCode = N'INV_STOCK_COUNT'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

-- Re-activate a grant an earlier state left disabled (MenuPermission has IsActive; the ROLE grant
-- table dbo.RoleMenuPermission does NOT - it uses IsAllowed, and role grants are deliberately left
-- to the deployment owner).
UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'INV_STOCK_COUNT'
  AND p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'POST', N'ROLLBACK', N'CANCEL')
  AND mp.IsActive <> 1;
GO

-- ── Verification ─────────────────────────────────────────────────────────────────────────────────
SELECT
    m.MenuCode,
    p.PermissionCode,
    IsActive = mp.IsActive
FROM dbo.Menu m
INNER JOIN dbo.MenuPermission mp ON mp.MenuId = m.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'INV_STOCK_COUNT'
ORDER BY p.PermissionCode;

PRINT N'init-inv-stock-count-menu.sql: 7 ACTIVE MenuPermission rows expected above.';
GO
