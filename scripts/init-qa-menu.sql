/* ============================================================================
   Quality Assurance — menus and permissions (Phase 1 record-only)
   ----------------------------------------------------------------------------
   TWO artefacts required (MenuDeploymentParityTests):
     1. ErpWeb/Menus/menus.xml  — authoritative
     2. This script — seeds fresh databases

   Workbench: ACCESS / ADD / EDIT / DELETE / SUBMIT / APPROVE / REJECT / EXPORT
   Inquiry: ACCESS / EXPORT
   Settings: ACCESS / EDIT
   No POST or ROLLBACK on Quality menus.

   Idempotent: guarded inserts.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

DECLARE @qualityId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'QUALITY');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NULL
BEGIN
    PRINT N'dbo.Menu missing - run init-menu-access.sql first, then re-run this script. Rows NOT created.';
END
ELSE
BEGIN
    IF @qualityId IS NULL
    BEGIN
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'QUALITY', N'Quality', NULL, NULL, 6, 0, 1, SYSUTCDATETIME(), N'SEED');
        SET @qualityId = SCOPE_IDENTITY();
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'QA_WORKBENCH')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'QA_WORKBENCH', N'Quality Workbench', @qualityId, N'/quality/cases', 1, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'QA_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'QA_INQUIRY', N'Quality Inquiry', @qualityId, N'/quality/inquiry', 2, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'QA_SETTINGS')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'QA_SETTINGS', N'Quality Settings', @qualityId, N'/quality/settings', 3, 0, 1, SYSUTCDATETIME(), N'SEED');
END
GO

UPDATE dbo.Menu SET MenuName = N'Quality', ParentMenuId = NULL, SortOrder = 6, IsActive = 1
WHERE MenuCode = N'QUALITY'
  AND (MenuName <> N'Quality' OR ParentMenuId IS NOT NULL OR SortOrder <> 6 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Quality Workbench', Route = N'/quality/cases', ParentMenuId = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'QUALITY'), SortOrder = 1, IsActive = 1
WHERE MenuCode = N'QA_WORKBENCH';

UPDATE dbo.Menu SET MenuName = N'Quality Inquiry', Route = N'/quality/inquiry', ParentMenuId = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'QUALITY'), SortOrder = 2, IsActive = 1
WHERE MenuCode = N'QA_INQUIRY';

UPDATE dbo.Menu SET MenuName = N'Quality Settings', Route = N'/quality/settings', ParentMenuId = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'QUALITY'), SortOrder = 3, IsActive = 1
WHERE MenuCode = N'QA_SETTINGS';

UPDATE dbo.Menu SET SortOrder = 7, IsActive = 1
WHERE MenuCode = N'SECURITY' AND (SortOrder <> 7 OR IsActive <> 1);
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'SUBMIT', N'APPROVE', N'REJECT', N'EXPORT')
WHERE m.MenuCode = N'QA_WORKBENCH'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode IN (N'ACCESS', N'EXPORT')
WHERE m.MenuCode = N'QA_INQUIRY'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode IN (N'ACCESS', N'EDIT')
WHERE m.MenuCode = N'QA_SETTINGS'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsAllowed, CreatedDate, CreatedBy)
SELECT r.RoleId, m.MenuId, p.PermissionId, 1, SYSUTCDATETIME(), N'SEED'
FROM dbo.Role r
INNER JOIN dbo.Menu m ON m.MenuCode IN (N'QA_WORKBENCH', N'QA_INQUIRY', N'QA_SETTINGS')
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
INNER JOIN dbo.Menu m ON m.MenuCode IN (N'QA_WORKBENCH', N'QA_INQUIRY')
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE r.CompanyCode = N'DEMO'
  AND r.RoleCode = N'USER'
  AND r.IsActive = 1
  AND NOT EXISTS (
      SELECT 1 FROM dbo.RoleMenuPermission x
      WHERE x.RoleId = r.RoleId AND x.MenuId = m.MenuId AND x.PermissionId = p.PermissionId);
GO

SELECT
    MenuCode  = m.MenuCode,
    MenuName  = m.MenuName,
    Route     = m.Route,
    SortOrder = m.SortOrder,
    IsActive  = m.IsActive,
    Parent    = parent.MenuCode
FROM dbo.Menu m
LEFT JOIN dbo.Menu parent ON parent.MenuId = m.ParentMenuId
WHERE m.MenuCode IN (N'QUALITY', N'QA_WORKBENCH', N'QA_INQUIRY', N'QA_SETTINGS')
ORDER BY m.MenuCode;
GO
