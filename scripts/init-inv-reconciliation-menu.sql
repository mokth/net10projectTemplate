/* ============================================================================
   Menu + permission seed for the Inventory Reconciliation page
   (/inventory/reconciliation, plan-inventoryInquirySuite Phase 3, item 16).
   Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
   Target database: the same database as ConnectionStrings:DefaultConnection (ERPWeb).
   Run with:  sqlcmd -E -d ERPWeb -i scripts/init-inv-reconciliation-menu.sql

   MENU DEPLOYMENT COUPLING (same rule as init-inv-trx-inquiry-menu.sql):
   MenuSyncService rebuilds dbo.Menu from ErpWeb/Menus/menus.xml at startup and SOFT-DISABLES every
   dbo.Menu row whose MenuCode is ABSENT from the XML; AccessRightService filters the cache on
   IsActive, so a soft-disabled menu disappears from the nav AND MenuAuthorize redirects its page to
   /unauthorized. The XML row and this script MUST ship together, and the XML wins on Route/Name/Sort.

   PERMISSIONS: ACCESS only. There is deliberately NO EXPORT row: the findings are diagnostics, and
   exporting them invites treating a diagnostic list as an audit report (D6). The service's own
   "diagnostic only - needs an opening-balance baseline" caveat is preserved verbatim on the page.

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

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_RECONCILIATION')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'INV_RECONCILIATION', N'Reconciliation', @inquiryId, N'/inventory/reconciliation', 9, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Reconciliation',
        ParentMenuId = @inquiryId,
        Route = N'/inventory/reconciliation',
        SortOrder = 9,
        IsActive = 1
    WHERE MenuCode = N'INV_RECONCILIATION';

    PRINT N'Menu rows INV_INQUIRY + INV_RECONCILIATION ensured (requires the matching rows in menus.xml).';
END
ELSE
    PRINT N'dbo.Menu or INVENTORY missing - run init-menu-access.sql first, then re-run this script.';
GO

-- ── Menu permissions: ACCESS ─────────────────────────────────────────────────────────────────────
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS')
WHERE m.MenuCode = N'INV_RECONCILIATION'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'INV_RECONCILIATION'
  AND p.PermissionCode IN (N'ACCESS')
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
WHERE m.MenuCode = N'INV_RECONCILIATION'
  AND p.PermissionCode IN (N'ACCESS')
ORDER BY p.PermissionCode;

PRINT N'init-inv-reconciliation-menu.sql: 1 ACTIVE MenuPermission row expected above.';
GO
