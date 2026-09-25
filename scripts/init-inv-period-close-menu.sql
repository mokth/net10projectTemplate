/* ============================================================================
   Menu + permission seed for the Inventory Period Close page
   (/inventory/period-close, plan-inventoryPeriodClose Phase 3).
   Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
   Target database: the same database as ConnectionStrings:DefaultConnection (ERPWeb).
   Run with:  sqlcmd -E -d ERPWeb -i scripts/init-inv-period-close-menu.sql

   MENU DEPLOYMENT COUPLING: the INV_PERIOD_CLOSE row in ErpWeb/Menus/menus.xml and this script MUST
   ship together (MenuSyncService soft-disables any dbo.Menu whose MenuCode is absent from the XML).

   PERMISSIONS: ACCESS to open, CLOSE to close a period, REOPEN to reopen one. All three are
   BUILT-IN permissions (PermissionCodes.Close / PermissionCodes.Reopen already exist), so this
   script makes NO dbo.Permission MERGE — it only grants the three built-ins on the menu.

   A ROLE still needs a dbo.RoleMenuPermission row (IsAllowed) before any user sees the page. That
   grant is deliberately the deployment owner's step and is NOT made here.
   ============================================================================ */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @invTransactionsId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'INV_TRANSACTIONS');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @invTransactionsId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INV_PERIOD_CLOSE')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'INV_PERIOD_CLOSE', N'Period Close', @invTransactionsId, N'/inventory/period-close', 10, 0, 1, SYSUTCDATETIME(), N'SEED');

    UPDATE dbo.Menu
    SET MenuName = N'Period Close',
        ParentMenuId = @invTransactionsId,
        Route = N'/inventory/period-close',
        SortOrder = 10,
        IsActive = 1
    WHERE MenuCode = N'INV_PERIOD_CLOSE';

    PRINT N'Menu row INV_PERIOD_CLOSE ensured (requires the matching row in menus.xml).';
END
ELSE
    PRINT N'dbo.Menu or INV_TRANSACTIONS missing - run init-menu-access.sql first, then re-run this script.';
GO

-- ── Menu permissions: ACCESS + CLOSE + REOPEN (all three are built-ins) ──────────────────────────
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'CLOSE', N'REOPEN')
WHERE m.MenuCode = N'INV_PERIOD_CLOSE'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

UPDATE mp
SET mp.IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'INV_PERIOD_CLOSE'
  AND p.PermissionCode IN (N'ACCESS', N'CLOSE', N'REOPEN')
  AND mp.IsActive <> 1;
GO

-- ── Verification ─────────────────────────────────────────────────────────────────────────────────
SELECT
    m.MenuCode,
    p.PermissionCode,
    IsActive = mp.IsActive
FROM dbo.Menu m
INNER JOIN dbo.MenuPermission mp ON mp.MenuId = m.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'INV_PERIOD_CLOSE'
  AND p.PermissionCode IN (N'ACCESS', N'CLOSE', N'REOPEN')
ORDER BY p.PermissionCode;

PRINT N'init-inv-period-close-menu.sql: 3 ACTIVE MenuPermission rows expected above.';
GO
