/* ============================================================================
   Menu + permission seed for the Stored Closing-Balance Inquiry page
   (/inventory/period-close-inquiry, plan-inventoryPeriodClose Phase 4).
   Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
   Target database: the same database as ConnectionStrings:DefaultConnection (ERPWeb).
   Run with:  sqlcmd -E -d ERPWeb -i scripts/init-inv-period-close-inq-menu.sql

   MENU DEPLOYMENT COUPLING: the INV_PERIOD_CLOSE_INQ row in ErpWeb/Menus/menus.xml and this script
   MUST ship together (MenuSyncService soft-disables any dbo.Menu whose MenuCode is absent from the XML).

   PERMISSIONS: ACCESS to open, EXPORT to download the xlsx, VIEW_PRICE for the value column
   (reusing the built-in VIEW_PRICE seeded by init-inv-stock-value-menu.sql). A permission not
   attached to the menu can never be granted, so VIEW_PRICE gets a MenuPermission row here.

   A ROLE still needs a dbo.RoleMenuPermission row (IsAllowed) before any user sees the value column.
   That grant is deliberately the deployment owner's step and is NOT made here.
   ============================================================================ */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ── VIEW_PRICE must exist as a built-in permission (also in PermissionCodes.All) ───────────────── */
MERGE dbo.Permission AS t
USING (VALUES (N'VIEW_PRICE', N'View Price', N'Data', N'Price visibility on value-bearing screens', 21))
      AS s(PermissionCode, PermissionName, PermissionType, Description, SortOrder)
ON t.PermissionCode = s.PermissionCode
WHEN NOT MATCHED THEN
    INSERT (PermissionCode, PermissionName, PermissionType, Description, SortOrder, IsActive, CreatedDate, CreatedBy)
    VALUES (s.PermissionCode, s.PermissionName, s.PermissionType, s.Description, s.SortOrder, 1, SYSUTCDATETIME(), N'SEED');
GO

DECLARE @inventoryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'INVENTORY');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @inventoryId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'INV_INQUIRY', N'Inquiry', @inventoryId, NULL, 3, 0, 1, SYSUTCDATETIME(), N'SEED');

    DECLARE @inquiryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'INV_INQUIRY');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_PERIOD_CLOSE_INQ')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'INV_PERIOD_CLOSE_INQ', N'Period Close Balances', @inquiryId, N'/inventory/period-close-inquiry', 10, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Period Close Balances',
        ParentMenuId = @inquiryId,
        Route = N'/inventory/period-close-inquiry',
        SortOrder = 10,
        IsActive = 1
    WHERE MenuCode = N'INV_PERIOD_CLOSE_INQ';

    PRINT N'Menu row INV_PERIOD_CLOSE_INQ ensured (requires the matching row in menus.xml).';
END
ELSE
    PRINT N'dbo.Menu or INVENTORY missing - run init-menu-access.sql first, then re-run this script.';
GO

-- ── Menu permissions: ACCESS + EXPORT + VIEW_PRICE ───────────────────────────────────────────────
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'EXPORT', N'VIEW_PRICE')
WHERE m.MenuCode = N'INV_PERIOD_CLOSE_INQ'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'INV_PERIOD_CLOSE_INQ'
  AND p.PermissionCode IN (N'ACCESS', N'EXPORT', N'VIEW_PRICE')
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
WHERE m.MenuCode = N'INV_PERIOD_CLOSE_INQ'
  AND p.PermissionCode IN (N'ACCESS', N'EXPORT', N'VIEW_PRICE')
ORDER BY p.PermissionCode;

PRINT N'init-inv-period-close-inq-menu.sql: 3 ACTIVE MenuPermission rows expected above.';
GO
