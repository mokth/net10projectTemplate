-- Menu + permission seed for the dynamic settings registry screen (/admin/settings).
-- Manual DBA script - do NOT run at app startup.
-- Idempotent: safe to re-run.
--
-- MENU DEPLOYMENT COUPLING (see the menu sync trap in the repo notes)
--   MenuSyncService runs on every startup from ErpWeb/Menus/menus.xml and SOFT-DISABLES (IsActive = 0)
--   every dbo.Menu row whose MenuCode is absent from the XML. A soft-disabled menu is not merely hidden:
--   AccessRightService filters on menu.IsActive, so MenuAuthorize redirects the page to /unauthorized.
--   Administrators with HasAdminBypass() can still reach the URL by typing it, which masks the failure.
--
--   Therefore ErpWeb/Menus/menus.xml, ErpWeb.Core/Menus/MenuCodes.cs and this script MUST ship together.
--   Keep MenuName/Route/SortOrder here in step with menus.xml; the XML wins.
--
--   The XML's automatic ACCESS mapping only applies to menus the sync itself INSERTS. Because this script
--   may create the row first, the sync then treats it as "updated" and adds no mapping - hence the
--   explicit dbo.MenuPermission inserts below.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @adminId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'ADMIN');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @adminId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'ADMIN_SETTINGS')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'ADMIN_SETTINGS', N'System Settings', @adminId, N'/admin/settings', 7, 0, 1, SYSUTCDATETIME(), N'SEED');

    PRINT N'Menu row ADMIN_SETTINGS ensured (requires a matching entry in ErpWeb/Menus/menus.xml).';
END
ELSE
    PRINT N'dbo.Menu or ADMIN missing - run init-menu-access.sql first, then re-run this script.';
GO

-- The screen lists settings (ACCESS) and allows changing them (EDIT). There is no NEW/DELETE toolbar:
-- a setting's existence comes from the code catalogue, never from a button.
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'EDIT')
WHERE m.MenuCode = N'ADMIN_SETTINGS'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

PRINT N'init-admin-settings-menu.sql complete. Ensure ADMIN_SETTINGS is present in ErpWeb/Menus/menus.xml.';

-- To let a ROLE actually edit settings, a RoleMenuPermission row is still required for that role.
-- dbo.RoleMenuPermission uses IsAllowed, NOT IsActive (the one grant table that differs).
--
--   DECLARE @roleId int = (SELECT RoleId FROM dbo.Role WHERE RoleCode = N'ADMIN');
--   INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsAllowed, CreatedDate, CreatedBy)
--   SELECT @roleId, mp.MenuId, mp.PermissionId, 1, SYSUTCDATETIME(), N'DEPLOY'
--   FROM dbo.MenuPermission mp
--   JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
--   JOIN dbo.Menu m ON m.MenuId = mp.MenuId
--   WHERE m.MenuCode = N'ADMIN_SETTINGS'
--     AND NOT EXISTS (
--         SELECT 1 FROM dbo.RoleMenuPermission r
--         WHERE r.RoleId = @roleId AND r.MenuId = mp.MenuId AND r.PermissionId = mp.PermissionId);
GO
