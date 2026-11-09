/* ============================================================================
   Purchase Approval — menus and permissions
   ----------------------------------------------------------------------------
   TWO artefacts required (MenuDeploymentParityTests):
     1. ErpWeb/Menus/menus.xml  — authoritative
     2. This script — seeds fresh databases

   Settings: ACCESS / EDIT
   Inbox: ACCESS
   All pending: ACCESS on PO_APPROVAL_ALL
   PR/PO reuse existing SUBMIT / APPROVE / REJECT.

   Idempotent: guarded inserts.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

DECLARE @purchaseId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PURCHASE');
DECLARE @masterId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PO_MASTER');
DECLARE @approvalId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PO_APPROVAL');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NULL OR @purchaseId IS NULL
BEGIN
    PRINT N'dbo.Menu or PURCHASE missing - run init-menu-access.sql first, then re-run this script. Rows NOT created.';
END
ELSE
BEGIN
    IF @masterId IS NULL
    BEGIN
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_MASTER', N'Master', @purchaseId, NULL, 1, 0, 1, SYSUTCDATETIME(), N'SEED');
        SET @masterId = SCOPE_IDENTITY();
    END

    IF @approvalId IS NULL
    BEGIN
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_APPROVAL', N'Approval', @purchaseId, NULL, 3, 0, 1, SYSUTCDATETIME(), N'SEED');
        SET @approvalId = SCOPE_IDENTITY();
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_APPROVAL_SETTINGS')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_APPROVAL_SETTINGS', N'Purchase Approval', @approvalId, N'/purchase/approval/settings', 1, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_APPROVAL_INBOX')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_APPROVAL_INBOX', N'My Approvals', @approvalId, N'/purchase/approval/inbox', 2, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_APPROVAL_ALL')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_APPROVAL_ALL', N'All Pending Approvals', @approvalId, N'/purchase/approval/inbox?all=1', 3, 0, 1, SYSUTCDATETIME(), N'SEED');
END
GO

UPDATE dbo.Menu SET MenuName = N'Approval', ParentMenuId = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PURCHASE'), SortOrder = 3, IsActive = 1
WHERE MenuCode = N'PO_APPROVAL'
  AND (MenuName <> N'Approval' OR ParentMenuId <> (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PURCHASE') OR SortOrder <> 3 OR IsActive <> 1);

UPDATE dbo.Menu SET SortOrder = 4, IsActive = 1
WHERE MenuCode = N'PO_INQUIRY' AND (SortOrder <> 4 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Purchase Approval', Route = N'/purchase/approval/settings', ParentMenuId = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PO_APPROVAL'), SortOrder = 1, IsActive = 1
WHERE MenuCode = N'PO_APPROVAL_SETTINGS';

UPDATE dbo.Menu SET MenuName = N'My Approvals', Route = N'/purchase/approval/inbox', ParentMenuId = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PO_APPROVAL'), SortOrder = 2, IsActive = 1
WHERE MenuCode = N'PO_APPROVAL_INBOX';

UPDATE dbo.Menu SET MenuName = N'All Pending Approvals', Route = N'/purchase/approval/inbox?all=1', ParentMenuId = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PO_APPROVAL'), SortOrder = 3, IsActive = 1
WHERE MenuCode = N'PO_APPROVAL_ALL';
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode IN (N'ACCESS', N'EDIT')
WHERE m.MenuCode = N'PO_APPROVAL_SETTINGS'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE m.MenuCode IN (N'PO_APPROVAL_INBOX', N'PO_APPROVAL_ALL')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode IN (N'SUBMIT', N'APPROVE', N'REJECT')
WHERE m.MenuCode IN (N'PO_PR', N'PO_ORDER')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

/* Requesters need inbox ACCESS to track submitted cycles (Submitted by me queue). */
INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsAllowed, CreatedDate, CreatedBy)
SELECT r.RoleId, m.MenuId, p.PermissionId, 1, SYSUTCDATETIME(), N'SEED'
FROM dbo.Role r
INNER JOIN dbo.Menu m ON m.MenuCode = N'PO_APPROVAL_INBOX'
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE r.CompanyCode = N'DEMO'
  AND r.RoleCode IN (N'USER', N'ADMIN')
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
WHERE m.MenuCode IN (N'PO_APPROVAL', N'PO_APPROVAL_SETTINGS', N'PO_APPROVAL_INBOX', N'PO_APPROVAL_ALL')
ORDER BY m.MenuCode;
GO
