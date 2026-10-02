-- Planning Work Order menu, permissions and running-number seed.
-- Manual DBA script; safe to re-run. Ship together with ErpWeb/Menus/menus.xml.
-- Role grants remain a deployment-owner decision.

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.Menu', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Permission', N'U') IS NULL
   OR OBJECT_ID(N'dbo.MenuPermission', N'U') IS NULL
BEGIN
    PRINT N'Menu security tables are missing - run init-menu-access.sql first.';
    RETURN;
END;

DECLARE @planningId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PLANNING');
IF @planningId IS NULL
BEGIN
    INSERT INTO dbo.Menu
        (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES
        (N'PLANNING', N'Planning', NULL, NULL, 5, 0, 1, SYSUTCDATETIME(), N'SEED');
    SET @planningId = SCOPE_IDENTITY();
END;

DECLARE @transactionsId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PLN_TRANSACTIONS');
IF @transactionsId IS NULL
BEGIN
    INSERT INTO dbo.Menu
        (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES
        (N'PLN_TRANSACTIONS', N'Transactions', @planningId, NULL, 2, 0, 1, SYSUTCDATETIME(), N'SEED');
    SET @transactionsId = SCOPE_IDENTITY();
END
ELSE
BEGIN
    UPDATE dbo.Menu
    SET ParentMenuId = @planningId,
        MenuName = N'Transactions',
        Route = NULL,
        SortOrder = 2,
        IsActive = 1,
        ModifiedDate = SYSUTCDATETIME(),
        ModifiedBy = N'SEED'
    WHERE MenuId = @transactionsId;
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_WORK_ORDER')
BEGIN
    INSERT INTO dbo.Menu
        (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES
        (N'PLN_WORK_ORDER', N'Work Orders', @transactionsId, N'/planning/work-orders', 1, 0, 1,
         SYSUTCDATETIME(), N'SEED');
END
ELSE
BEGIN
    UPDATE dbo.Menu
    SET ParentMenuId = @transactionsId,
        MenuName = N'Work Orders',
        Route = N'/planning/work-orders',
        SortOrder = 1,
        IsActive = 1,
        ModifiedDate = SYSUTCDATETIME(),
        ModifiedBy = N'SEED'
    WHERE MenuCode = N'PLN_WORK_ORDER';
END;

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'APPROVE', N'CANCEL', N'REOPEN')
WHERE m.MenuCode = N'PLN_WORK_ORDER'
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId
        AND mp.PermissionId = p.PermissionId
  );

IF OBJECT_ID(N'dbo.MsRunningNo', N'U') IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM dbo.MsRunningNo
       WHERE CompanyCode = N'DEMO' AND DocKey = N'PR_WORK_ORDER'
   )
BEGIN
    -- Audit columns differ between the CLR property names and the physical
    -- MsRunningNo schema. Insert only the required, stable columns here.
    INSERT INTO dbo.MsRunningNo (CompanyCode, DocKey, LastNo)
    VALUES (N'DEMO', N'PR_WORK_ORDER', 0);
END;

PRINT N'PLN_WORK_ORDER menu and ACCESS/ADD/EDIT/APPROVE/CANCEL/REOPEN permissions ensured.';
GO

