/* ============================================================================
   Subcontracting — menus and permissions (Phase 1 draft shell)
   ----------------------------------------------------------------------------
   TWO artefacts required (MenuDeploymentParityTests):
     1. ErpWeb/Menus/menus.xml  — authoritative
     2. This script — seeds fresh databases

   Workbench / Send Out / Receive Back: ACCESS / ADD / EDIT / DELETE / PRINT / EXPORT / VIEW_COST
   Inquiry: ACCESS / EXPORT / PRINT / VIEW_COST
   Settings: ACCESS / EDIT
   POST and ROLLBACK exist on Send Out / Receive Back / Workbench so a later
   phase can grant them; the application service still refuses to post.

   Idempotent: guarded inserts.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

DECLARE @parentId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SUBCONTRACTING');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NULL
BEGIN
    PRINT N'dbo.Menu missing - run init-menu-access.sql first, then re-run this script. Rows NOT created.';
END
ELSE
BEGIN
    IF @parentId IS NULL
    BEGIN
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SUBCONTRACTING', N'Subcontracting', NULL, NULL, 5, 0, 1, SYSUTCDATETIME(), N'SEED');
        SET @parentId = SCOPE_IDENTITY();
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SUBCON_WORKBENCH')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SUBCON_WORKBENCH', N'Workbench', @parentId, N'/subcon', 1, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SUBCON_SEND_OUT')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SUBCON_SEND_OUT', N'Send Out', @parentId, N'/subcon/send-out', 2, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SUBCON_RECEIVE_BACK')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SUBCON_RECEIVE_BACK', N'Receive Back', @parentId, N'/subcon/receive-back', 3, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SUBCON_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SUBCON_INQUIRY', N'Inquiry / Statement', @parentId, N'/subcon/inquiry', 4, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SUBCON_SETTINGS')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SUBCON_SETTINGS', N'Settings', @parentId, N'/subcon/settings', 5, 0, 1, SYSUTCDATETIME(), N'SEED');
END
GO

UPDATE dbo.Menu SET MenuName = N'Subcontracting', ParentMenuId = NULL, SortOrder = 5, IsActive = 1
WHERE MenuCode = N'SUBCONTRACTING'
  AND (MenuName <> N'Subcontracting' OR ParentMenuId IS NOT NULL OR SortOrder <> 5 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Workbench', Route = N'/subcon', ParentMenuId = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SUBCONTRACTING'), SortOrder = 1, IsActive = 1
WHERE MenuCode = N'SUBCON_WORKBENCH';

UPDATE dbo.Menu SET MenuName = N'Send Out', Route = N'/subcon/send-out', ParentMenuId = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SUBCONTRACTING'), SortOrder = 2, IsActive = 1
WHERE MenuCode = N'SUBCON_SEND_OUT';

UPDATE dbo.Menu SET MenuName = N'Receive Back', Route = N'/subcon/receive-back', ParentMenuId = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SUBCONTRACTING'), SortOrder = 3, IsActive = 1
WHERE MenuCode = N'SUBCON_RECEIVE_BACK';

UPDATE dbo.Menu SET MenuName = N'Inquiry / Statement', Route = N'/subcon/inquiry', ParentMenuId = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SUBCONTRACTING'), SortOrder = 4, IsActive = 1
WHERE MenuCode = N'SUBCON_INQUIRY';

UPDATE dbo.Menu SET MenuName = N'Settings', Route = N'/subcon/settings', ParentMenuId = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SUBCONTRACTING'), SortOrder = 5, IsActive = 1
WHERE MenuCode = N'SUBCON_SETTINGS';

UPDATE dbo.Menu SET SortOrder = 6, IsActive = 1
WHERE MenuCode = N'PLANNING' AND (SortOrder <> 6 OR IsActive <> 1);

UPDATE dbo.Menu SET SortOrder = 7, IsActive = 1
WHERE MenuCode = N'QUALITY' AND (SortOrder <> 7 OR IsActive <> 1);

UPDATE dbo.Menu SET SortOrder = 8, IsActive = 1
WHERE MenuCode = N'SECURITY' AND (SortOrder <> 8 OR IsActive <> 1);
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'PRINT', N'EXPORT', N'VIEW_COST', N'POST', N'ROLLBACK')
WHERE m.MenuCode IN (N'SUBCON_WORKBENCH', N'SUBCON_SEND_OUT', N'SUBCON_RECEIVE_BACK')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode IN (N'ACCESS', N'EXPORT', N'PRINT', N'VIEW_COST')
WHERE m.MenuCode = N'SUBCON_INQUIRY'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode IN (N'ACCESS', N'EDIT')
WHERE m.MenuCode = N'SUBCON_SETTINGS'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsAllowed, CreatedDate, CreatedBy)
SELECT r.RoleId, m.MenuId, p.PermissionId, 1, SYSUTCDATETIME(), N'SEED'
FROM dbo.Role r
INNER JOIN dbo.Menu m ON m.MenuCode IN (N'SUBCON_WORKBENCH', N'SUBCON_SEND_OUT', N'SUBCON_RECEIVE_BACK', N'SUBCON_INQUIRY', N'SUBCON_SETTINGS')
INNER JOIN dbo.MenuPermission mp ON mp.MenuId = m.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE r.CompanyCode = N'DEMO'
  AND r.RoleCode = N'ADMIN'
  AND r.IsActive = 1
  AND NOT EXISTS (
      SELECT 1 FROM dbo.RoleMenuPermission x
      WHERE x.RoleId = r.RoleId AND x.MenuId = m.MenuId AND x.PermissionId = p.PermissionId);
GO

INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsAllowed, CreatedDate, CreatedBy)
SELECT r.RoleId, m.MenuId, p.PermissionId, 1, SYSUTCDATETIME(), N'SEED'
FROM dbo.Role r
INNER JOIN dbo.Menu m ON m.MenuCode IN (N'SUBCON_WORKBENCH', N'SUBCON_SEND_OUT', N'SUBCON_RECEIVE_BACK', N'SUBCON_INQUIRY')
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE r.CompanyCode = N'DEMO'
  AND r.RoleCode = N'USER'
  AND r.IsActive = 1
  AND NOT EXISTS (
      SELECT 1 FROM dbo.RoleMenuPermission x
      WHERE x.RoleId = r.RoleId AND x.MenuId = m.MenuId AND x.PermissionId = p.PermissionId);
GO
