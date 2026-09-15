-- Menu + permission seed for the flat sales reference masters
-- (SaCustSubGroup, SaShipVia, SaSOType, SaComment, SaShippingLeadTime, SaLMW).
-- Manual DBA script - do NOT run at app startup.
-- Idempotent: safe to re-run.
--
-- MENU DEPLOYMENT COUPLING (docs/sales-master-plan.md §12.2)
--   MenuSyncService reconciles dbo.Menu against ErpWeb/Menus/menus.xml on EVERY startup and
--   SOFT-DISABLES (IsActive = 0) every row whose MenuCode is absent from the XML.
--   AccessRightService filters the cache on IsActive, so a soft-disabled menu disappears from
--   the nav AND MenuAuthorize redirects its pages to /unauthorized (an admin bypass can still
--   reach the URL, which hides the fault). The XML rows and this script MUST ship together.
--   Keep MenuName / Route / SortOrder here in step with menus.xml; the XML wins.
--
-- Run order: scripts/init-sales-master-refs.sql -> init-sales-master-menu.sql -> role grants.
--
-- ROLE MAPPING IS NOT PRESCRIBED HERE (plan §12.2 item 3). The plan deliberately leaves it to the
-- deployment owner; the default is to mirror the roles already granted to the equivalent shipped
-- masters (SA_CUST_TYPE, SA_CUST_GROUP, SA_PAY_TERM, ...). The section at the bottom copies those
-- grants and is commented out until the owner confirms.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── Menu rows under SA_MASTER ─────────────────────────────────────────────────
DECLARE @saMasterId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SA_MASTER');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @saMasterId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_CUST_SUB_GROUP')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_CUST_SUB_GROUP', N'Customer Sub Groups', @saMasterId, N'/sales/customer-sub-groups', 12, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_SHIP_VIA')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_SHIP_VIA', N'Ship Via', @saMasterId, N'/sales/ship-vias', 13, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_SO_TYPE')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_SO_TYPE', N'SO Types', @saMasterId, N'/sales/so-types', 14, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_COMMENT')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_COMMENT', N'Comments', @saMasterId, N'/sales/comments', 15, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_SHIP_LEAD_TIME')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_SHIP_LEAD_TIME', N'Shipping Lead Time', @saMasterId, N'/sales/shipping-lead-time', 16, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_LMW')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_LMW', N'LMW Licences', @saMasterId, N'/sales/lmw', 17, 0, 1, SYSUTCDATETIME(), N'SEED');

    PRINT N'Menu rows SA_CUST_SUB_GROUP / SA_SHIP_VIA / SA_SO_TYPE / SA_COMMENT / SA_SHIP_LEAD_TIME / SA_LMW ensured (requires matching entries in menus.xml).';
END
ELSE
    PRINT N'dbo.Menu or SA_MASTER missing - run init-menu-access.sql first, then re-run this script.';
GO

-- ── Menu permissions ──────────────────────────────────────────────────────────
-- ACCESS / ADD / EDIT / DELETE / EXPORT for all six. EXPORT is in scope for every master (D-15).
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'EXPORT')
WHERE m.MenuCode IN (N'SA_CUST_SUB_GROUP', N'SA_SHIP_VIA', N'SA_SO_TYPE',
                     N'SA_COMMENT', N'SA_SHIP_LEAD_TIME', N'SA_LMW')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

-- ── Role grants — SUPPLIED BY THE DEPLOYMENT OWNER ────────────────────────────
-- Plan §12.2 item 3 leaves the role mapping to the deployment owner. The default is to mirror the
-- roles already granted to SA_CUST_TYPE. Uncomment and verify before running.
--
-- INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsActive)
-- SELECT src.RoleId, dst.MenuId, src.PermissionId, 1
-- FROM dbo.RoleMenuPermission src
-- INNER JOIN dbo.Menu srcMenu ON srcMenu.MenuId = src.MenuId AND srcMenu.MenuCode = N'SA_CUST_TYPE'
-- CROSS JOIN dbo.Menu dst
-- INNER JOIN dbo.MenuPermission mp ON mp.MenuId = dst.MenuId AND mp.PermissionId = src.PermissionId
-- WHERE dst.MenuCode IN (N'SA_CUST_SUB_GROUP', N'SA_SHIP_VIA', N'SA_SO_TYPE',
--                        N'SA_COMMENT', N'SA_SHIP_LEAD_TIME', N'SA_LMW')
--   AND NOT EXISTS (
--       SELECT 1 FROM dbo.RoleMenuPermission rmp
--       WHERE rmp.RoleId = src.RoleId AND rmp.MenuId = dst.MenuId AND rmp.PermissionId = src.PermissionId);
-- GO

PRINT N'init-sales-master-menu.sql complete.';
GO
