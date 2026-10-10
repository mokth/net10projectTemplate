/* ============================================================================
   Sales Price Review & Price Change History menu seed
   ----------------------------------------------------------------------------
   This is a manual, idempotent DBA script. Keep it aligned with:
     - ErpWeb/Menus/menus.xml
     - ErpWeb.Core/Menus/MenuCodes.cs

   The workbench uses existing ACCESS / EDIT / VIEW_PRICE / EXPORT / IMPORT
   permissions. The dedicated history inquiry uses ACCESS / VIEW_PRICE /
   EXPORT. Role grants are intentionally left to the deployment owner.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @saMasterId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SA_MASTER');
DECLARE @saInquiryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SA_INQUIRY');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NULL OR @saMasterId IS NULL OR @saInquiryId IS NULL
BEGIN
    PRINT N'dbo.Menu, SA_MASTER, or SA_INQUIRY is missing - run the Sales menu seed scripts first. Rows NOT created.';
END
ELSE
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_PRICE_MAINTENANCE')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_PRICE_MAINTENANCE', N'Price Review & Update', @saMasterId, N'/sales/pricing/review', 22, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_PRICE_CHANGE_HISTORY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_PRICE_CHANGE_HISTORY', N'Price Change History', @saInquiryId, N'/sales/inquiry/price-change-history', 11, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Price Review & Update',
        ParentMenuId = @saMasterId,
        Route = N'/sales/pricing/review',
        SortOrder = 22,
        IsActive = 1
    WHERE MenuCode = N'SA_PRICE_MAINTENANCE';

    UPDATE dbo.Menu
    SET MenuName = N'Price Change History',
        ParentMenuId = @saInquiryId,
        Route = N'/sales/inquiry/price-change-history',
        SortOrder = 11,
        IsActive = 1
    WHERE MenuCode = N'SA_PRICE_CHANGE_HISTORY';
END
GO

IF EXISTS (
    SELECT 1
    FROM (VALUES (N'ACCESS'), (N'EDIT'), (N'VIEW_PRICE'), (N'EXPORT'), (N'IMPORT')) required(PermissionCode)
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.Permission p
        WHERE p.PermissionCode = required.PermissionCode
          AND p.IsActive = 1))
BEGIN
    SELECT MissingPermission = required.PermissionCode
    FROM (VALUES (N'ACCESS'), (N'EDIT'), (N'VIEW_PRICE'), (N'EXPORT'), (N'IMPORT')) required(PermissionCode)
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.Permission p
        WHERE p.PermissionCode = required.PermissionCode
          AND p.IsActive = 1);
    PRINT N'Expected built-in permission rows are missing or inactive. MenuPermission grants were NOT completed.';
END
ELSE
BEGIN
    INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
    SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
    FROM dbo.Menu m
    INNER JOIN dbo.Permission p
        ON p.PermissionCode IN (N'ACCESS', N'EDIT', N'VIEW_PRICE', N'EXPORT', N'IMPORT')
    WHERE m.MenuCode = N'SA_PRICE_MAINTENANCE'
      AND NOT EXISTS (
          SELECT 1 FROM dbo.MenuPermission mp
          WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);

    INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
    SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
    FROM dbo.Menu m
    INNER JOIN dbo.Permission p
        ON p.PermissionCode IN (N'ACCESS', N'VIEW_PRICE', N'EXPORT')
    WHERE m.MenuCode = N'SA_PRICE_CHANGE_HISTORY'
      AND NOT EXISTS (
          SELECT 1 FROM dbo.MenuPermission mp
          WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);

    UPDATE mp
    SET IsActive = 1
    FROM dbo.MenuPermission mp
    INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
    INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
    WHERE m.MenuCode = N'SA_PRICE_MAINTENANCE'
      AND p.PermissionCode IN (N'ACCESS', N'EDIT', N'VIEW_PRICE', N'EXPORT', N'IMPORT');

    UPDATE mp
    SET IsActive = 1
    FROM dbo.MenuPermission mp
    INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
    INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
    WHERE m.MenuCode = N'SA_PRICE_CHANGE_HISTORY'
      AND p.PermissionCode IN (N'ACCESS', N'VIEW_PRICE', N'EXPORT');
END
GO

SELECT
    m.MenuCode,
    m.MenuName,
    m.Route,
    m.SortOrder,
    m.IsActive,
    Parent = parent.MenuCode
FROM dbo.Menu m
LEFT JOIN dbo.Menu parent ON parent.MenuId = m.ParentMenuId
WHERE m.MenuCode IN (N'SA_PRICE_MAINTENANCE', N'SA_PRICE_CHANGE_HISTORY')
ORDER BY m.MenuCode;
GO

SELECT
    m.MenuCode,
    p.PermissionCode,
    mp.IsActive
FROM dbo.Menu m
INNER JOIN dbo.MenuPermission mp ON mp.MenuId = m.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode IN (N'SA_PRICE_MAINTENANCE', N'SA_PRICE_CHANGE_HISTORY')
ORDER BY m.MenuCode, p.PermissionCode;
GO
