-- Menu + permission seed for the self-billed e-Invoice documents (SBI / SBC / SBD).
-- Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
--
-- MENU DEPLOYMENT COUPLING (same rule as init-pocdn-menu.sql)
--   MenuSyncService creates dbo.Menu rows from ErpWeb/Menus/menus.xml at startup and preserves
--   MenuId, but it SOFT-DISABLES every dbo.Menu row whose MenuCode is ABSENT from menus.xml.
--   PO_SB_INVOICE / PO_SB_CN / PO_SB_DN are therefore added to menus.xml in the same change; the
--   two must ship together or AccessRightService (which filters menu.IsActive) locks the screens out.
--
-- Run order:
--   create-po-sb-invoice.sql -> create-po-sb-cdn.sql -> seed-po-sb-numbering.sql -> init-pobsb-menu.sql
--   (init-menu-access.sql must have been run first: it creates the PO_TRANSACTIONS parent menu.)

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @poTransactionsId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PO_TRANSACTIONS');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @poTransactionsId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_SB_INVOICE')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_SB_INVOICE', N'Self-billed Invoice', @poTransactionsId, N'/purchase/self-billed-invoices', 7, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_SB_CN')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_SB_CN', N'Self-billed Credit Note', @poTransactionsId, N'/purchase/self-billed-credit-notes', 8, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_SB_DN')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_SB_DN', N'Self-billed Debit Note', @poTransactionsId, N'/purchase/self-billed-debit-notes', 9, 0, 1, SYSUTCDATETIME(), N'SEED');

    PRINT N'Menu rows PO_SB_INVOICE / PO_SB_CN / PO_SB_DN ensured (requires matching entries in menus.xml).';
END
ELSE
    PRINT N'dbo.Menu or PO_TRANSACTIONS missing - run init-menu-access.sql first, then re-run this script.';
GO

-- ── Menu permissions ──────────────────────────────────────────────────────────
-- ACCESS/ADD/EDIT/DELETE for the document lifecycle, plus SUBMIT/CANCEL for the MyInvois actions
-- (the façade checks SUBMIT on these menus, not on the ordinary purchase ones).
--
-- DELIBERATELY ABSENT: POST and ROLLBACK. The self-billed families have no ERP finalisation step —
-- their NEW/POSTED dimension is retired and no code writes POSTED any more — so the e-Invoice state is
-- their whole lifecycle. Any grant an earlier run created is deactivated at the bottom of this script.
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'SUBMIT', N'CANCEL')
WHERE m.MenuCode IN (N'PO_SB_INVOICE', N'PO_SB_CN', N'PO_SB_DN')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

-- Re-activate a grant that an earlier state left disabled (MenuPermission has IsActive; the ROLE
-- grant table dbo.RoleMenuPermission does NOT - it uses IsAllowed, and role grants are deliberately
-- left to the deployment owner).
UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode IN (N'PO_SB_INVOICE', N'PO_SB_CN', N'PO_SB_DN')
  AND p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'SUBMIT', N'CANCEL')
  AND mp.IsActive <> 1;
GO

-- Retire the POST/ROLLBACK grants. Soft-disable, never delete: dbo.RoleMenuPermission may point at the
-- same PermissionId and the house convention is a soft disable. A role that still holds a
-- RoleMenuPermission row for either permission keeps an INERT grant that no code consults; clearing
-- those is a deployment-owner step (plan-poSelfBilledLifecycle "Phase D").
UPDATE mp
SET mp.IsActive = 0
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode IN (N'PO_SB_INVOICE', N'PO_SB_CN', N'PO_SB_DN')
  AND p.PermissionCode IN (N'POST', N'ROLLBACK')
  AND mp.IsActive = 1;
GO

-- Verification: expect 18 ACTIVE rows (3 menus x 6 permissions).
SELECT COUNT(*) AS ExpectedSelfBilledMenuPermissions
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode IN (N'PO_SB_INVOICE', N'PO_SB_CN', N'PO_SB_DN')
  AND p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'SUBMIT', N'CANCEL')
  AND mp.IsActive = 1;
GO

-- Verification: expect 6 RETIRED rows (3 menus x POST/ROLLBACK), all inactive.
SELECT COUNT(*) AS RetiredSelfBilledMenuPermissions
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode IN (N'PO_SB_INVOICE', N'PO_SB_CN', N'PO_SB_DN')
  AND p.PermissionCode IN (N'POST', N'ROLLBACK')
  AND mp.IsActive = 0;
GO

PRINT N'init-pobsb-menu.sql complete. A ROLE still needs RoleMenuPermission (IsAllowed = 1) before users can act.';
GO
