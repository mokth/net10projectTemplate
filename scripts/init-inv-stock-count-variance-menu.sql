/* ============================================================================
   Menu + permission seed for the Stock Count Variance page
   (/inventory/stock-count-variance, plan-inventoryInquirySuite Phase 3, item 14).
   Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
   Target database: the same database as ConnectionStrings:DefaultConnection (ERPWeb).
   Run with:  sqlcmd -E -d ERPWeb -i scripts/init-inv-stock-count-variance-menu.sql

   MENU DEPLOYMENT COUPLING (same rule as init-inv-trx-inquiry-menu.sql):
   MenuSyncService rebuilds dbo.Menu from ErpWeb/Menus/menus.xml at startup and SOFT-DISABLES every
   dbo.Menu row whose MenuCode is ABSENT from the XML; AccessRightService filters the cache on
   IsActive, so a soft-disabled menu disappears from the nav AND MenuAuthorize redirects its page to
   /unauthorized. The XML row and this script MUST ship together, and the XML wins on Route/Name/Sort.

   PERMISSIONS: ACCESS to open, EXPORT to download the xlsx. There is deliberately NO VIEW_PRICE row:
   the variance report shows quantities and the sheet's own snapshot price, not a stock value, so a
   price permission here would advertise a capability the screen does not gate.

   NOTE: this menu is SEPARATE from INV_STOCK_COUNT on purpose. Reading count evidence is not the same
   right as creating, counting, posting or rolling back a sheet - and the variance service checks
   ACCESS against THIS menu, so a user can hold one without the other.

   A ROLE still needs a dbo.RoleMenuPermission row (IsAllowed) before any user sees this menu.
   That grant is deliberately the deployment owner's step and is NOT made here.
   ============================================================================ */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @inventoryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'INVENTORY');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @inventoryId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'INV_INQUIRY', N'Inquiry', @inventoryId, NULL, 3, 0, 1, SYSUTCDATETIME(), N'SEED');

    DECLARE @inquiryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'INV_INQUIRY');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_STOCK_COUNT_VAR')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'INV_STOCK_COUNT_VAR', N'Stock Count Variance', @inquiryId, N'/inventory/stock-count-variance', 7, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Stock Count Variance',
        ParentMenuId = @inquiryId,
        Route = N'/inventory/stock-count-variance',
        SortOrder = 7,
        IsActive = 1
    WHERE MenuCode = N'INV_STOCK_COUNT_VAR';

    PRINT N'Menu rows INV_INQUIRY + INV_STOCK_COUNT_VAR ensured (requires the matching rows in menus.xml).';
END
ELSE
    PRINT N'dbo.Menu or INVENTORY missing - run init-menu-access.sql first, then re-run this script.';
GO

-- ── Menu permissions: ACCESS + EXPORT ────────────────────────────────────────────────────────────
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'EXPORT')
WHERE m.MenuCode = N'INV_STOCK_COUNT_VAR'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'INV_STOCK_COUNT_VAR'
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
WHERE m.MenuCode = N'INV_STOCK_COUNT_VAR'
  AND p.PermissionCode IN (N'ACCESS', N'EXPORT')
ORDER BY p.PermissionCode;

PRINT N'init-inv-stock-count-variance-menu.sql: 2 ACTIVE MenuPermission rows expected above.';
GO
