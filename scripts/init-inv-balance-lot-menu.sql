/* ============================================================================
   Menu + permission seed for the Inventory Balance by Lot inquiry.
   Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
   Target database: the same database as ConnectionStrings:DefaultConnection (ERPWeb).
   Run with:  sqlcmd -E -d ERPWeb -i scripts/init-inv-balance-lot-menu.sql
   ============================================================================ */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* MENU DEPLOYMENT COUPLING (same rule as init-inv-stock-count-menu.sql):
   MenuSyncService builds dbo.Menu rows from ErpWeb/Menus/menus.xml at startup and preserves MenuId,
   but it SOFT-DISABLES every dbo.Menu row whose MenuCode is ABSENT from menus.xml. The INV_INQUIRY
   parent and its INV_BALANCE_LOT child must therefore ship in menus.xml together with this script, or
   AccessRightService (which filters menu.IsActive) locks the screen out. MenuSyncService auto-creates
   only the ACCESS grant for a menu it inserts; EXPORT must come from this script. */

DECLARE @inventoryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'INVENTORY');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @inventoryId IS NOT NULL
BEGIN
    -- 1. The INV_INQUIRY parent (first inventory "Inquiry" menu, D6).
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'INV_INQUIRY', N'Inquiry', @inventoryId, NULL, 3, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Inquiry',
        ParentMenuId = @inventoryId,
        Route = NULL,
        SortOrder = 3,
        IsActive = 1
    WHERE MenuCode = N'INV_INQUIRY';

    DECLARE @inquiryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'INV_INQUIRY');

    -- 2. The page child under INV_INQUIRY.
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_BALANCE_LOT')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'INV_BALANCE_LOT', N'Balance by Lot', @inquiryId, N'/inventory/balance-by-lot', 1, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Balance by Lot',
        ParentMenuId = @inquiryId,
        Route = N'/inventory/balance-by-lot',
        SortOrder = 1,
        IsActive = 1
    WHERE MenuCode = N'INV_BALANCE_LOT';

    PRINT N'Menu rows INV_INQUIRY + INV_BALANCE_LOT ensured (requires the matching rows in menus.xml).';
END
ELSE
    PRINT N'dbo.Menu or INVENTORY missing - run init-menu-access.sql first, then re-run this script.';
GO

-- ── Menu permissions ─────────────────────────────────────────────────────────────────────────────
-- The page is read-only: ACCESS to open, EXPORT to download the xlsx (R9).
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'EXPORT')
WHERE m.MenuCode = N'INV_BALANCE_LOT'
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
WHERE m.MenuCode = N'INV_BALANCE_LOT'
  AND p.PermissionCode IN (N'ACCESS', N'EXPORT')
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
WHERE m.MenuCode = N'INV_BALANCE_LOT'
ORDER BY p.PermissionCode;

PRINT N'init-inv-balance-lot-menu.sql: 2 ACTIVE MenuPermission rows expected above.';
GO
