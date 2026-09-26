/* ============================================================================
   Sales Dashboard — menu triad (SA_DASHBOARD)
   ----------------------------------------------------------------------------
   One read-only screen:
     /sales/dashboard  — KPI chips + charts over the current company

   TWO artefacts are required and BOTH must stay in step (the repo trap that
   MenuDeploymentParityTests guards):
     1. ErpWeb/Menus/menus.xml  — the AUTHORITATIVE row.
     2. This script — seeds the row and its permissions for FRESH databases.

   ACCESS only. The screen is read-only: no ADD / EDIT / DELETE / EXPORT are seeded.
   NOTE: the dashboard's chart payloads reuse the analysis service, so a role that
   should see the charts also needs ACCESS on SA_SALES_SUMMARY / SA_SALES_CATEGORY /
   SA_SALES_ITEM (the KPI chips always render for a SA_DASHBOARD holder).

   Idempotent: guarded inserts, so a second run is a clean no-op.

   Apply TWICE on a scratch database (second run must be a clean no-op) before touching dev.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

DECLARE @salesId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SALES');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NULL OR @salesId IS NULL
BEGIN
    PRINT N'dbo.Menu or SALES missing - run init-menu-access.sql first, then re-run this script. Row NOT created.';
END
ELSE
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_DASHBOARD')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_DASHBOARD', N'Sales Dashboard', @salesId, N'/sales/dashboard', 0, 0, 1, SYSUTCDATETIME(), N'SEED');
END
GO

/* Keep an existing row aligned with menus.xml (the XML is authoritative). */
UPDATE dbo.Menu
SET MenuName  = N'Sales Dashboard',
    Route     = N'/sales/dashboard',
    SortOrder = 0,
    IsActive  = 1
WHERE MenuCode = N'SA_DASHBOARD'
  AND (Route IS NULL OR Route <> N'/sales/dashboard' OR SortOrder <> 0 OR IsActive <> 1);
GO

/* ACCESS only — the screen is read-only. */
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE m.MenuCode = N'SA_DASHBOARD'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

/* Verification. */
SELECT
    MenuCode  = m.MenuCode,
    MenuName  = m.MenuName,
    Route     = m.Route,
    SortOrder = m.SortOrder,
    IsActive  = m.IsActive,
    Parent    = parent.MenuCode
FROM dbo.Menu m
LEFT JOIN dbo.Menu parent ON parent.MenuId = m.ParentMenuId
WHERE m.MenuCode = N'SA_DASHBOARD';
GO
