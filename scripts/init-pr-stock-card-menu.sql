/* ============================================================================
   Menu + permission seed for Production quantity inquiries
   (/planning/stock-card, /planning/stock-movements, /planning/stock-as-of,
   /planning/stock-reconciliation). P3 of the production stock ledger.
   Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
   Target database: the same database as ConnectionStrings:DefaultConnection (ERPWeb).
   Run with:  sqlcmd -E -d ERPWeb -i scripts/init-pr-stock-card-menu.sql

   MENU DEPLOYMENT COUPLING: see the header of init-inv-stock-card-menu.sql. The XML row in
   ErpWeb/Menus/menus.xml and this script MUST ship together.

   PERMISSIONS: ACCESS to open each inquiry. The service also requires ACCESS on
   PLN_PRODUCTION_BALANCE. A ROLE still needs a dbo.RoleMenuPermission row (IsAllowed);
   that grant is deliberately manual.
   ============================================================================ */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @planningId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PLANNING');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @planningId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PLN_INQUIRY', N'Inquiry', @planningId, NULL, 3, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Inquiry',
        ParentMenuId = @planningId,
        Route = NULL,
        SortOrder = 3,
        IsActive = 1
    WHERE MenuCode = N'PLN_INQUIRY';

    DECLARE @inquiryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PLN_INQUIRY');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_STOCK_CARD')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PLN_STOCK_CARD', N'Production Stock Card', @inquiryId, N'/planning/stock-card', 3, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Production Stock Card',
        ParentMenuId = @inquiryId,
        Route = N'/planning/stock-card',
        SortOrder = 3,
        IsActive = 1
    WHERE MenuCode = N'PLN_STOCK_CARD';

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_STOCK_MOVEMENT')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PLN_STOCK_MOVEMENT', N'Production Stock Movements', @inquiryId, N'/planning/stock-movements', 4, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Production Stock Movements',
        ParentMenuId = @inquiryId,
        Route = N'/planning/stock-movements',
        SortOrder = 4,
        IsActive = 1
    WHERE MenuCode = N'PLN_STOCK_MOVEMENT';

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_STOCK_ASOF')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PLN_STOCK_ASOF', N'Production Stock As Of', @inquiryId, N'/planning/stock-as-of', 5, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Production Stock As Of',
        ParentMenuId = @inquiryId,
        Route = N'/planning/stock-as-of',
        SortOrder = 5,
        IsActive = 1
    WHERE MenuCode = N'PLN_STOCK_ASOF';

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_STOCK_RECONCILIATION')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PLN_STOCK_RECONCILIATION', N'Production Stock Reconciliation', @inquiryId, N'/planning/stock-reconciliation', 6, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Production Stock Reconciliation',
        ParentMenuId = @inquiryId,
        Route = N'/planning/stock-reconciliation',
        SortOrder = 6,
        IsActive = 1
    WHERE MenuCode = N'PLN_STOCK_RECONCILIATION';

    PRINT N'Menu rows PLN_INQUIRY + four production stock inquiry pages ensured (requires the matching rows in menus.xml).';
END
ELSE
    PRINT N'dbo.Menu or PLANNING missing - run init-menu-access.sql first, then re-run this script.';
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode = N'ACCESS'
WHERE m.MenuCode IN (N'PLN_STOCK_CARD', N'PLN_STOCK_MOVEMENT', N'PLN_STOCK_ASOF', N'PLN_STOCK_RECONCILIATION')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode IN (N'PLN_STOCK_CARD', N'PLN_STOCK_MOVEMENT', N'PLN_STOCK_ASOF', N'PLN_STOCK_RECONCILIATION')
  AND p.PermissionCode = N'ACCESS'
  AND mp.IsActive <> 1;
GO

SELECT
    m.MenuCode,
    p.PermissionCode,
    IsActive = mp.IsActive
FROM dbo.Menu m
INNER JOIN dbo.MenuPermission mp ON mp.MenuId = m.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode IN (N'PLN_STOCK_CARD', N'PLN_STOCK_MOVEMENT', N'PLN_STOCK_ASOF', N'PLN_STOCK_RECONCILIATION')
  AND p.PermissionCode = N'ACCESS'
ORDER BY m.MenuCode, p.PermissionCode;

PRINT N'init-pr-stock-card-menu.sql: 4 ACTIVE ACCESS MenuPermission rows expected above.';
GO
