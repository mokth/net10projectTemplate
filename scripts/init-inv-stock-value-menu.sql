/* ============================================================================
   Menu + permission seed for the Est. Inventory Value page
   (/inventory/valuation, plan-inventoryInquirySuite Phase 3, item 15).
   Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
   Target database: the same database as ConnectionStrings:DefaultConnection (ERPWeb).
   Run with:  sqlcmd -E -d ERPWeb -i scripts/init-inv-stock-value-menu.sql

   MENU DEPLOYMENT COUPLING (same rule as init-inv-trx-inquiry-menu.sql):
   MenuSyncService rebuilds dbo.Menu from ErpWeb/Menus/menus.xml at startup and SOFT-DISABLES every
   dbo.Menu row whose MenuCode is ABSENT from the XML; AccessRightService filters the cache on
   IsActive, so a soft-disabled menu disappears from the nav AND MenuAuthorize redirects its page to
   /unauthorized. The XML row and this script MUST ship together, and the XML wins on Route/Name/Sort.

   PERMISSIONS: ACCESS to open, EXPORT to download the xlsx, and VIEW_PRICE for the "Est. value"
   column. VIEW_PRICE is a MenuPermission row here for the same reason PRICE_OVERRIDE needed one: a
   permission that is not attached to the menu can never be granted, so CanAsync(menu, VIEW_PRICE)
   would be false for everyone and the money column would be permanently invisible. Option B of D11
   keeps ICurrentUserService.CanViewPrice - which the shipped Balance-by-Lot page uses - untouched.

   The value page reuses the Stock Summary composition but passes THIS menu code, so its ACCESS and
   VIEW_PRICE are its own grant and NOT the summary screen's.

   A ROLE still needs a dbo.RoleMenuPermission row (IsAllowed) before any user sees these columns.
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

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_STOCK_VALUE')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'INV_STOCK_VALUE', N'Est. Inventory Value', @inquiryId, N'/inventory/valuation', 8, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Est. Inventory Value',
        ParentMenuId = @inquiryId,
        Route = N'/inventory/valuation',
        SortOrder = 8,
        IsActive = 1
    WHERE MenuCode = N'INV_STOCK_VALUE';

    PRINT N'Menu rows INV_INQUIRY + INV_STOCK_VALUE ensured (requires the matching rows in menus.xml).';
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
WHERE m.MenuCode = N'INV_STOCK_VALUE'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'INV_STOCK_VALUE'
  AND p.PermissionCode IN (N'ACCESS', N'EXPORT', N'VIEW_PRICE')
  AND mp.IsActive <> 1;
GO

-- ── Verification ─────────────────────────────────────────────────────────────────────────────────
-- The filter names exactly the three permissions the PRINT claims, so the message can never contradict
-- its own query.
SELECT
    m.MenuCode,
    p.PermissionCode,
    IsActive = mp.IsActive
FROM dbo.Menu m
INNER JOIN dbo.MenuPermission mp ON mp.MenuId = m.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'INV_STOCK_VALUE'
  AND p.PermissionCode IN (N'ACCESS', N'EXPORT', N'VIEW_PRICE')
ORDER BY p.PermissionCode;

PRINT N'init-inv-stock-value-menu.sql: 3 ACTIVE MenuPermission rows expected above.';
GO
