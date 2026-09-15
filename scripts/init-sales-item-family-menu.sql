-- Menu + permission seed for the Sales item family
-- (IvCustPriceGroup / IvCustPrice / SaItemCust / SaDisGroupItem).
-- Manual DBA script - do NOT run at app startup.
-- Idempotent: safe to re-run.
--
-- MENU DEPLOYMENT COUPLING (plan plans/sales-item-family-v2-plan.md §23)
--   MenuSyncService reconciles dbo.Menu against ErpWeb/Menus/menus.xml on EVERY startup and
--   SOFT-DISABLES (IsActive = 0) every row whose MenuCode is absent from the XML.
--   AccessRightService filters the cache on IsActive, so a soft-disabled menu disappears from the
--   nav AND MenuAuthorize redirects its pages to /unauthorized (an admin bypass can still reach the
--   URL, which hides the fault). The XML rows and this script MUST ship together.
--   Keep MenuName / Route / SortOrder here in step with menus.xml; the XML wins.
--
-- Run order:
--   1. scripts/init-sales-item-family.sql   (schema)
--   2. scripts/init-sales-item-family-menu.sql   (this file)
--   3. role grants (see the commented block at the bottom)
--
-- ROLE MAPPING IS NOT PRESCRIBED HERE. The default is to mirror the roles already granted to the
-- equivalent shipped master (SA_CUST_TYPE); the block at the bottom is commented out until the
-- deployment owner confirms.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── VIEW_PRICE — a built-in permission, seeded like VIEW_COST ─────────────────
-- Visibility only: it never changes what is stored, and the server never trusts a client price.
-- It must also be listed in PermissionCodes.All so it counts as built-in and cannot be deleted.
MERGE dbo.Permission AS t
USING (VALUES (N'VIEW_PRICE', N'View Price', N'Data', N'Sales item family price visibility', 21))
      AS s(PermissionCode, PermissionName, PermissionType, Description, SortOrder)
ON t.PermissionCode = s.PermissionCode
WHEN NOT MATCHED THEN
    INSERT (PermissionCode, PermissionName, PermissionType, Description, SortOrder, IsActive, CreatedDate, CreatedBy)
    VALUES (s.PermissionCode, s.PermissionName, s.PermissionType, s.Description, s.SortOrder, 1, SYSUTCDATETIME(), N'SEED');
GO

-- ── Menu rows under SA_MASTER ─────────────────────────────────────────────────
DECLARE @saMasterId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SA_MASTER');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @saMasterId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_CUST_PRICE_GROUP')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_CUST_PRICE_GROUP', N'Price Groups', @saMasterId, N'/sales/price-groups', 18, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_CUST_PRICE')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_CUST_PRICE', N'Customer Prices', @saMasterId, N'/sales/customer-prices', 19, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_ITEM_CUST')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_ITEM_CUST', N'Customer Items', @saMasterId, N'/sales/customer-items', 20, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_DIS_GROUP_ITEM')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_DIS_GROUP_ITEM', N'Item Discounts', @saMasterId, N'/sales/item-discounts', 21, 0, 1, SYSUTCDATETIME(), N'SEED');

    PRINT N'Menu rows SA_CUST_PRICE_GROUP / SA_CUST_PRICE / SA_ITEM_CUST / SA_DIS_GROUP_ITEM ensured (requires matching entries in menus.xml).';
END
ELSE
    PRINT N'dbo.Menu or SA_MASTER missing - run init-menu-access.sql first, then re-run this script.';
GO

-- ── Menu permissions ──────────────────────────────────────────────────────────
-- ACCESS / ADD / EDIT / DELETE / EXPORT for all four screens.
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'EXPORT')
WHERE m.MenuCode IN (N'SA_CUST_PRICE_GROUP', N'SA_CUST_PRICE', N'SA_ITEM_CUST', N'SA_DIS_GROUP_ITEM')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

-- VIEW_PRICE only on the three screens that can display a price. The item-discount screen shows
-- discount slots, not prices, so it is deliberately excluded.
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode = N'VIEW_PRICE'
WHERE m.MenuCode IN (N'SA_CUST_PRICE_GROUP', N'SA_CUST_PRICE', N'SA_ITEM_CUST')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

-- ── Role grants — SUPPLIED BY THE DEPLOYMENT OWNER ────────────────────────────
-- Default: mirror the roles already granted to SA_CUST_TYPE. Uncomment and verify before running.
--
-- INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsActive)
-- SELECT src.RoleId, dst.MenuId, src.PermissionId, 1
-- FROM dbo.RoleMenuPermission src
-- INNER JOIN dbo.Menu srcMenu ON srcMenu.MenuId = src.MenuId AND srcMenu.MenuCode = N'SA_CUST_TYPE'
-- CROSS JOIN dbo.Menu dst
-- INNER JOIN dbo.MenuPermission mp ON mp.MenuId = dst.MenuId AND mp.PermissionId = src.PermissionId
-- WHERE dst.MenuCode IN (N'SA_CUST_PRICE_GROUP', N'SA_CUST_PRICE', N'SA_ITEM_CUST', N'SA_DIS_GROUP_ITEM')
--   AND NOT EXISTS (
--       SELECT 1 FROM dbo.RoleMenuPermission rmp
--       WHERE rmp.RoleId = src.RoleId AND rmp.MenuId = dst.MenuId AND rmp.PermissionId = src.PermissionId);
-- GO

PRINT N'init-sales-item-family-menu.sql complete.';
GO
