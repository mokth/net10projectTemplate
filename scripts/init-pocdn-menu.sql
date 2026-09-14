-- Menu + permission seed for Purchase Credit & Debit Notes (PoCdn).
-- Manual DBA script - do NOT run at app startup.
-- Idempotent: safe to re-run.
--
-- MENU DEPLOYMENT COUPLING (plan "Menu deployment coupling")
--   MenuSyncService creates dbo.Menu rows from ErpWeb/Menus/menus.xml at startup and
--   preserves MenuId, so menus.xml and this script must ship together. This script
--   inserts the menu rows itself when they are missing, so it is safe to run before
--   the first post-deploy sync; a later sync will match them by MenuCode.
--
-- Run order: create-pocdn.sql -> seed-pocdn-numbering.sql -> init-pocdn-menu.sql.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── C44: the INTERNAL_ADJUSTMENT permission ───────────────────────────────────
-- INTERNAL_ADJUSTMENT is the only reason code that permits a blank SupplierDocNo
-- (C16). It is gated so it cannot be used simply to avoid entering a supplier
-- document number; the service additionally requires Remarks and writes an audit event.
IF OBJECT_ID(N'dbo.Permission', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.Permission WHERE PermissionCode = N'INTERNAL_ADJUSTMENT')
BEGIN
    INSERT INTO dbo.Permission
        (PermissionCode, PermissionName, PermissionType, Description, SortOrder, IsActive, CreatedDate, CreatedBy)
    VALUES
        (N'INTERNAL_ADJUSTMENT', N'Internal Adjustment', N'Action',
         N'Allows a CN/DN with a blank supplier document number (reason code INTERNAL_ADJUSTMENT)', 21, 1,
         SYSUTCDATETIME(), N'SEED');
    PRINT N'Created permission INTERNAL_ADJUSTMENT';
END
ELSE
    PRINT N'Permission INTERNAL_ADJUSTMENT already present (or dbo.Permission missing)';
GO

-- ── Menu rows under PO_TRANSACTIONS ───────────────────────────────────────────
-- IMPORTANT: these rows are NOT sufficient on their own. MenuSyncService runs at startup
-- (ErpWeb/Program.cs) and SOFT-DISABLES every dbo.Menu row whose MenuCode is absent from
-- ErpWeb/Menus/menus.xml. The same script must therefore also add PO_CN / PO_DN /
-- PO_CN_RESERVATIONS to menus.xml, otherwise _active is flipped to 0 on the next app start
-- and AccessRightService (which filters on menu.IsActive) will hide and lock out the
-- screens. Keep MenuName/Route/SortOrder here in step with menus.xml; the XML wins.
DECLARE @poTransactionsId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PO_TRANSACTIONS');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @poTransactionsId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_CN')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_CN', N'Credit Note', @poTransactionsId, N'/purchase/credit-notes', 4, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_DN')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_DN', N'Debit Note', @poTransactionsId, N'/purchase/debit-notes', 5, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_CN_RESERVATIONS')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_CN_RESERVATIONS', N'CN Reservations', @poTransactionsId, N'/purchase/cn-reservations', 6, 0, 1, SYSUTCDATETIME(), N'SEED');

    PRINT N'Menu rows PO_CN / PO_DN / PO_CN_RESERVATIONS ensured (requires matching entries in menus.xml).';
END
ELSE
    PRINT N'dbo.Menu or PO_TRANSACTIONS missing - run init-menu-access.sql first, then re-run this script.';
GO

-- ── Menu permissions ──────────────────────────────────────────────────────────
-- CN / DN: ACCESS/ADD/EDIT/DELETE/POST/ROLLBACK, plus INTERNAL_ADJUSTMENT (C44).
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'POST', N'ROLLBACK', N'INTERNAL_ADJUSTMENT')
WHERE m.MenuCode IN (N'PO_CN', N'PO_DN')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

-- Reservations is report-only.
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode IN (N'ACCESS', N'PRINT', N'EXPORT')
WHERE m.MenuCode = N'PO_CN_RESERVATIONS'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

PRINT N'init-pocdn-menu.sql complete.';
GO
