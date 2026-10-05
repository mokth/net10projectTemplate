/* ============================================================================
   Planning Daily Production + Production Balance inquiry menus, permissions and
   PR_DAILY_OUTPUT running-number seed.
   Manual DBA script; safe to re-run. Ship together with ErpWeb/Menus/menus.xml.
   Role grants remain a deployment-owner decision.
   ============================================================================ */

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

DECLARE @inquiryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PLN_INQUIRY');
IF @inquiryId IS NULL
BEGIN
    INSERT INTO dbo.Menu
        (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES
        (N'PLN_INQUIRY', N'Inquiry', @planningId, NULL, 3, 0, 1, SYSUTCDATETIME(), N'SEED');
    SET @inquiryId = SCOPE_IDENTITY();
END
ELSE
BEGIN
    UPDATE dbo.Menu
    SET ParentMenuId = @planningId,
        MenuName = N'Inquiry',
        Route = NULL,
        SortOrder = 3,
        IsActive = 1,
        ModifiedDate = SYSUTCDATETIME(),
        ModifiedBy = N'SEED'
    WHERE MenuId = @inquiryId;
END;

-- Daily Production under PLN_TRANSACTIONS
IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_DAILY_PRODUCTION')
BEGIN
    INSERT INTO dbo.Menu
        (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES
        (N'PLN_DAILY_PRODUCTION', N'Daily Production', @transactionsId, N'/planning/daily-production', 3, 0, 1,
         SYSUTCDATETIME(), N'SEED');
END
ELSE
BEGIN
    UPDATE dbo.Menu
    SET ParentMenuId = @transactionsId,
        MenuName = N'Daily Production',
        Route = N'/planning/daily-production',
        SortOrder = 3,
        IsActive = 1,
        ModifiedDate = SYSUTCDATETIME(),
        ModifiedBy = N'SEED'
    WHERE MenuCode = N'PLN_DAILY_PRODUCTION';
END;

-- Production Balance under PLN_INQUIRY
IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_PRODUCTION_BALANCE')
BEGIN
    INSERT INTO dbo.Menu
        (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES
        (N'PLN_PRODUCTION_BALANCE', N'Production Balance by Lot', @inquiryId, N'/planning/production-balance', 2, 0, 1,
         SYSUTCDATETIME(), N'SEED');
END
ELSE
BEGIN
    UPDATE dbo.Menu
    SET ParentMenuId = @inquiryId,
        MenuName = N'Production Balance by Lot',
        Route = N'/planning/production-balance',
        SortOrder = 2,
        IsActive = 1,
        ModifiedDate = SYSUTCDATETIME(),
        ModifiedBy = N'SEED'
    WHERE MenuCode = N'PLN_PRODUCTION_BALANCE';
END;

-- Daily Production permissions: ACCESS/ADD/EDIT/DELETE/POST/ROLLBACK
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'POST', N'ROLLBACK')
WHERE m.MenuCode = N'PLN_DAILY_PRODUCTION'
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId
        AND mp.PermissionId = p.PermissionId
  );

UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'PLN_DAILY_PRODUCTION'
  AND p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'POST', N'ROLLBACK')
  AND mp.IsActive <> 1;

-- Production Balance permissions: ACCESS only
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS')
WHERE m.MenuCode = N'PLN_PRODUCTION_BALANCE'
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId
        AND mp.PermissionId = p.PermissionId
  );

UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'PLN_PRODUCTION_BALANCE'
  AND p.PermissionCode IN (N'ACCESS')
  AND mp.IsActive <> 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_MAT_CONSUME_VAR')
BEGIN
    INSERT INTO dbo.Menu
        (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES
        (N'PLN_MAT_CONSUME_VAR', N'Material Consume Variance', @inquiryId, N'/planning/material-consume-variance', 7, 0, 1,
         SYSUTCDATETIME(), N'SEED');
END
ELSE
BEGIN
    UPDATE dbo.Menu
    SET ParentMenuId = @inquiryId,
        MenuName = N'Material Consume Variance',
        Route = N'/planning/material-consume-variance',
        SortOrder = 7,
        IsActive = 1,
        ModifiedDate = SYSUTCDATETIME(),
        ModifiedBy = N'SEED'
    WHERE MenuCode = N'PLN_MAT_CONSUME_VAR';
END;

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE m.MenuCode = N'PLN_MAT_CONSUME_VAR'
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId
        AND mp.PermissionId = p.PermissionId
  );

UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'PLN_MAT_CONSUME_VAR'
  AND p.PermissionCode IN (N'ACCESS')
  AND mp.IsActive <> 1;

IF OBJECT_ID(N'dbo.MsRunningNo', N'U') IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM dbo.MsRunningNo
       WHERE CompanyCode = N'DEMO' AND DocKey = N'PR_DAILY_OUTPUT'
   )
BEGIN
    INSERT INTO dbo.MsRunningNo (CompanyCode, DocKey, LastNo)
    VALUES (N'DEMO', N'PR_DAILY_OUTPUT', 0);
END;

PRINT N'PLN_DAILY_PRODUCTION, PLN_PRODUCTION_BALANCE and PLN_MAT_CONSUME_VAR menus/permissions ensured.';
GO
