-- Menu + permission seed for the Department (ADMIN_DEPT) and Project (ADMIN_PROJECT) masters.
-- Manual DBA script - do NOT run at app startup.
-- Idempotent: safe to re-run.
--
-- MENU DEPLOYMENT COUPLING (see plans/Department_project_plan.md "the menu deployment trap")
--   MenuSyncService runs on every startup from ErpWeb/Menus/menus.xml and SOFT-DISABLES
--   (IsActive = 0) every dbo.Menu row whose MenuCode is absent from the XML. A soft-disabled
--   menu is not merely hidden: AccessRightService filters on menu.IsActive, so MenuAuthorize
--   redirects the page to /unauthorized. Administrators with HasAdminBypass() can still reach
--   the URL by typing it, which masks the failure.
--
--   Therefore ErpWeb/Menus/menus.xml, ErpWeb.Core/Menus/MenuCodes.cs and this script MUST
--   ship together. Keep MenuName/Route/SortOrder here in step with menus.xml; the XML wins.
--
--   The XML's automatic ACCESS mapping only applies to menus the sync itself INSERTS. Because
--   this script may create the rows first, the sync then treats them as "updated" and adds no
--   mapping - hence the explicit dbo.MenuPermission inserts below.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @adminMasterId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'ADMIN_MASTER');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @adminMasterId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'ADMIN_DEPT')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'ADMIN_DEPT', N'Departments', @adminMasterId, N'/admin/departments', 3, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'ADMIN_PROJECT')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'ADMIN_PROJECT', N'Projects', @adminMasterId, N'/admin/projects', 4, 0, 1, SYSUTCDATETIME(), N'SEED');

    PRINT N'Menu rows ADMIN_DEPT / ADMIN_PROJECT ensured (requires matching entries in ErpWeb/Menus/menus.xml).';
END
ELSE
    PRINT N'dbo.Menu or ADMIN_MASTER missing - run init-menu-access.sql first, then re-run this script.';
GO

-- Both list pages ship NEW / ACTIVATE / DEACTIVATE / DELETE / EXPORT toolbar buttons,
-- so all five permissions are required.
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'EXPORT')
WHERE m.MenuCode IN (N'ADMIN_DEPT', N'ADMIN_PROJECT')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

PRINT N'init-ms-dept-project-menu.sql complete. Ensure ADMIN_DEPT / ADMIN_PROJECT are present in ErpWeb/Menus/menus.xml.';
GO
