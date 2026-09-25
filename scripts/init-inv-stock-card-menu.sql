/* ============================================================================
   Menu + permission seed for the Inventory Stock Card
   (/inventory/stock-card, plan-inventoryInquirySuite Phase 1).
   Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
   Target database: the same database as ConnectionStrings:DefaultConnection (ERPWeb).
   Run with:  sqlcmd -E -d ERPWeb -i scripts/init-inv-stock-card-menu.sql

   MENU DEPLOYMENT COUPLING: see the header of init-inv-trx-inquiry-menu.sql. The XML row in
   ErpWeb/Menus/menus.xml and this script MUST ship together.

   PERMISSIONS: ACCESS to open, EXPORT to download the xlsx, VIEW_PRICE for the value columns.
   VIEW_PRICE is attached to this menu so it can actually be granted (a permission absent from
   MenuPermission is unreachable). Option B of D11 leaves ICurrentUserService.CanViewPrice - which
   the shipped Balance-by-Lot page uses - untouched.

   A ROLE still needs a dbo.RoleMenuPermission row (IsAllowed); that grant is deliberately manual.
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

    UPDATE dbo.Menu
    SET MenuName = N'Inquiry',
        ParentMenuId = @inventoryId,
        Route = NULL,
        SortOrder = 3,
        IsActive = 1
    WHERE MenuCode = N'INV_INQUIRY';

    DECLARE @inquiryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'INV_INQUIRY');

    -- SortOrder 3: Balance by Lot owns 1, Transaction Inquiry owns 2.
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_STOCK_CARD')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'INV_STOCK_CARD', N'Stock Card', @inquiryId, N'/inventory/stock-card', 3, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Stock Card',
        ParentMenuId = @inquiryId,
        Route = N'/inventory/stock-card',
        SortOrder = 3,
        IsActive = 1
    WHERE MenuCode = N'INV_STOCK_CARD';

    PRINT N'Menu rows INV_INQUIRY + INV_STOCK_CARD ensured (requires the matching rows in menus.xml).';
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
WHERE m.MenuCode = N'INV_STOCK_CARD'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'INV_STOCK_CARD'
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
WHERE m.MenuCode = N'INV_STOCK_CARD'
  AND p.PermissionCode IN (N'ACCESS', N'EXPORT', N'VIEW_PRICE')
ORDER BY p.PermissionCode;

PRINT N'init-inv-stock-card-menu.sql: 3 ACTIVE MenuPermission rows expected above.';
GO
