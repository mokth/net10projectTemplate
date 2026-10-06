/* ============================================================================
   Menu + permission seed for the Costing Diagnostic & Repair Center
   (/inventory/costing-center).
   Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
   menus.xml must contain INV_COSTING_CENTER. MenuSyncService grants ACCESS only
   for a newly inserted XML menu, so VIEW_COST and REPAIR_COST are mapped here.
   A role still needs RoleMenuPermission before a user can open the page.
   ============================================================================ */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.Permission', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.Permission WHERE PermissionCode = N'REPAIR_COST')
BEGIN
    INSERT INTO dbo.Permission
        (PermissionCode, PermissionName, PermissionType, Description, SortOrder, IsActive, CreatedDate, CreatedBy)
    VALUES
        (N'REPAIR_COST', N'Repair Cost', N'Action',
         N'Execute a costing repair. Does not replace the source document POST or ROLLBACK permission.', 40, 1,
         SYSUTCDATETIME(), N'SEED');
END
GO

DECLARE @inventoryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'INVENTORY');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @inventoryId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'INV_INQUIRY', N'Inquiry', @inventoryId, NULL, 3, 0, 1, SYSUTCDATETIME(), N'SEED');

    DECLARE @inquiryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'INV_INQUIRY');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_COSTING_CENTER')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'INV_COSTING_CENTER', N'Costing Diagnostic & Repair Center', @inquiryId, N'/inventory/costing-center', 11, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Costing Diagnostic & Repair Center',
        ParentMenuId = @inquiryId,
        Route = N'/inventory/costing-center',
        SortOrder = 11,
        IsActive = 1
    WHERE MenuCode = N'INV_COSTING_CENTER';
END
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'VIEW_COST', N'REPAIR_COST')
WHERE m.MenuCode = N'INV_COSTING_CENTER'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'INV_COSTING_CENTER'
  AND p.PermissionCode IN (N'ACCESS', N'VIEW_COST', N'REPAIR_COST')
  AND mp.IsActive <> 1;
GO
