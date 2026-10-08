-- Sales Delivery Request menu, permissions and document-numbering seed.
-- Manual DBA script; safe to re-run. Ship together with ErpWeb/Menus/menus.xml.
-- Run after scripts/init-menu-access.sql and scripts/init-adsmnum.sql.

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

DECLARE @saTransactionsId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SA_TRANSACTIONS');
IF @saTransactionsId IS NULL
BEGIN
    PRINT N'SA_TRANSACTIONS is missing - run the base menu seed, then re-run this script.';
    RETURN;
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_DR')
BEGIN
    INSERT INTO dbo.Menu
        (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES
        (N'SA_DR', N'Delivery Request', @saTransactionsId, N'/sales/delivery-requests', 3, 0, 1,
         SYSUTCDATETIME(), N'SEED');
END
ELSE
BEGIN
    UPDATE dbo.Menu
    SET MenuName = N'Delivery Request',
        ParentMenuId = @saTransactionsId,
        Route = N'/sales/delivery-requests',
        SortOrder = 3,
        IsActive = 1,
        ModifiedDate = SYSUTCDATETIME(),
        ModifiedBy = N'SEED'
    WHERE MenuCode = N'SA_DR';
END;

-- Keep the transaction order aligned with menus.xml after inserting SA_DR.
UPDATE dbo.Menu SET SortOrder = 1 WHERE MenuCode = N'SA_QT';
UPDATE dbo.Menu SET SortOrder = 2 WHERE MenuCode = N'SA_SO';
UPDATE dbo.Menu SET SortOrder = 4 WHERE MenuCode = N'SA_INVOICE';
UPDATE dbo.Menu SET SortOrder = 5 WHERE MenuCode = N'SA_DO';
UPDATE dbo.Menu SET SortOrder = 6 WHERE MenuCode = N'SA_CN';
UPDATE dbo.Menu SET SortOrder = 7 WHERE MenuCode = N'SA_DN';
UPDATE dbo.Menu SET SortOrder = 8 WHERE MenuCode = N'SA_CN_RESERVATIONS';
UPDATE dbo.Menu SET SortOrder = 9 WHERE MenuCode = N'SA_EINVOICE_TIN';
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'APPROVE', N'CANCEL')
WHERE m.MenuCode = N'SA_DR'
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId
        AND mp.PermissionId = p.PermissionId
  );
GO

-- Monthly DR numbering. Change the tenant/branch/location before deployment.
-- The service uses the date-based row when one exists for DR.
DECLARE @Company nvarchar(10) = N'DEMO';
DECLARE @Branch nvarchar(10) = N'HQ';
DECLARE @Year smallint = CONVERT(smallint, YEAR(GETDATE()));
DECLARE @Month smallint = CONVERT(smallint, MONTH(GETDATE()));

IF OBJECT_ID(N'dbo.AdSmNumDate', N'U') IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM dbo.AdSmNumDate
       WHERE CompanyCode = @Company
         AND BranchCode = @Branch
         AND NumCd = N'DR'
         AND [Year] = @Year
         AND [Month] = @Month
   )
BEGIN
    INSERT INTO dbo.AdSmNumDate
        (CompanyCode, BranchCode, LocationCode, [Year], [Month], NumCd, NumDes,
         TotLength, Prefix, Seq, Created, UserID, NumberingDelimeter, NumberingFormat)
    VALUES
        (@Company, @Branch, N'MAIN', @Year, @Month, N'DR', N'Delivery Request',
         4, N'DR', 1, GETDATE(), N'SYSTEM', N'-', NULL);
END;
GO

PRINT N'SA_DR menu, ACCESS/ADD/EDIT/DELETE/APPROVE/CANCEL permissions and DR numbering seed ensured.';
GO
