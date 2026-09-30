-- Menu + permission seed for Product Definition (Planning master).
-- Manual DBA script - do NOT run at app startup.
-- Idempotent: safe to re-run.
--
-- MENU DEPLOYMENT COUPLING
--   MenuSyncService reconciles dbo.Menu against ErpWeb/Menus/menus.xml on EVERY startup.
--   This script and menus.xml MUST ship together.
--
-- Run after init-menu-access.sql. Role grants are left to the deployment owner.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @planningId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PLANNING');
DECLARE @masterId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PLN_MASTER');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL
BEGIN
    IF @planningId IS NULL
    BEGIN
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PLANNING', N'Planning', NULL, NULL, 5, 0, 1, SYSUTCDATETIME(), N'SEED');
        SET @planningId = SCOPE_IDENTITY();
        PRINT N'Inserted PLANNING menu.';
    END

    IF @masterId IS NULL AND @planningId IS NOT NULL
    BEGIN
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PLN_MASTER', N'Master', @planningId, NULL, 1, 0, 1, SYSUTCDATETIME(), N'SEED');
        SET @masterId = SCOPE_IDENTITY();
        PRINT N'Inserted PLN_MASTER menu.';
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_PRODUCT_DEF') AND @masterId IS NOT NULL
    BEGIN
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PLN_PRODUCT_DEF', N'Product Definition', @masterId, N'/planning/product-definitions', 1, 0, 1, SYSUTCDATETIME(), N'SEED');
        PRINT N'Inserted PLN_PRODUCT_DEF menu.';
    END
END
ELSE
    PRINT N'dbo.Menu missing - run init-menu-access.sql first.';
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE')
WHERE m.MenuCode = N'PLN_PRODUCT_DEF'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

PRINT N'PLN_PRODUCT_DEF MenuPermission rows ensured (ACCESS/ADD/EDIT/DELETE).';
GO
