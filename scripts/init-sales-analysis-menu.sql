/* ============================================================================
   Sales Analysis Phase 1 — menu triad (SA_ANALYSIS + SA_SALES_SUMMARY /
   SA_SALES_ATTAINMENT / SA_QT_CONVERSION)
   ----------------------------------------------------------------------------
   THREE read-only analysis screens:
     /sales/analysis/summary        — period sales by dimension + INV/CN/DN chips
     /sales/analysis/attainment     — company-wide monthly targets vs posted actuals
     /sales/analysis/qt-conversion  — current-revision quotation win/loss

   TWO artefacts are required and BOTH must stay in step — this is the repo trap that
   MenuDeploymentParityTests guards:
     1. ErpWeb/Menus/menus.xml  — the AUTHORITATIVE row. MenuSyncService SOFT-DISABLES any
        dbo.Menu row whose MenuCode is absent from the XML, and AccessRightService then hides
        it from navigation and locks out the page.
     2. This script — seeds the rows and their permissions for FRESH databases.
   Adding the XML rows alone leaves a fresh database without menus; adding this script alone
   gets the rows soft-disabled on the next startup.

   ACCESS only. All three screens are read-only aggregate inquiries: they create no data, so
   ADD / EDIT / DELETE / EXPORT are deliberately NOT seeded. (The CSV downloads call the same
   service methods as the grid and are gated by the same ACCESS check, so no EXPORT permission
   is involved.)

   Idempotent: guarded inserts, so a second run is a clean no-op. Safe on a populated database.

   Apply TWICE on a scratch database (second run must be a clean no-op) before touching dev.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

DECLARE @salesId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SALES');

/* RETURN only exits its OWN batch, so the guards must live in the SAME batch as the INSERTs. */
IF OBJECT_ID(N'dbo.Menu', N'U') IS NULL OR @salesId IS NULL
BEGIN
    PRINT N'dbo.Menu or SALES missing - run init-menu-access.sql first, then re-run this script. Rows NOT created.';
END
ELSE
BEGIN
    DECLARE @analysisId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SA_ANALYSIS');
    IF @analysisId IS NULL
    BEGIN
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_ANALYSIS', N'Analysis', @salesId, NULL, 3, 0, 1, SYSUTCDATETIME(), N'SEED');
        SET @analysisId = SCOPE_IDENTITY();
        PRINT N'Menu row SA_ANALYSIS created.';
    END
    ELSE
    BEGIN
        PRINT N'Menu row SA_ANALYSIS already present - no change.';
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_SALES_SUMMARY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_SALES_SUMMARY', N'Sales Summary', @analysisId, N'/sales/analysis/summary', 1, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_SALES_ATTAINMENT')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_SALES_ATTAINMENT', N'Sales Rep Attainment', @analysisId, N'/sales/analysis/attainment', 2, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_QT_CONVERSION')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_QT_CONVERSION', N'Quotation Conversion', @analysisId, N'/sales/analysis/qt-conversion', 3, 0, 1, SYSUTCDATETIME(), N'SEED');
END
GO

/* Keep existing rows aligned with menus.xml (the XML is authoritative; this just avoids the surprise
   of a silent correction on the next startup). */
UPDATE dbo.Menu
SET MenuName  = N'Sales Summary',
    Route     = N'/sales/analysis/summary',
    SortOrder = 1,
    IsActive  = 1
WHERE MenuCode = N'SA_SALES_SUMMARY'
  AND (Route IS NULL OR Route <> N'/sales/analysis/summary' OR SortOrder <> 1 OR IsActive <> 1);

UPDATE dbo.Menu
SET MenuName  = N'Sales Rep Attainment',
    Route     = N'/sales/analysis/attainment',
    SortOrder = 2,
    IsActive  = 1
WHERE MenuCode = N'SA_SALES_ATTAINMENT'
  AND (Route IS NULL OR Route <> N'/sales/analysis/attainment' OR SortOrder <> 2 OR IsActive <> 1);

UPDATE dbo.Menu
SET MenuName  = N'Quotation Conversion',
    Route     = N'/sales/analysis/qt-conversion',
    SortOrder = 3,
    IsActive  = 1
WHERE MenuCode = N'SA_QT_CONVERSION'
  AND (Route IS NULL OR Route <> N'/sales/analysis/qt-conversion' OR SortOrder <> 3 OR IsActive <> 1);
GO

/* ACCESS only — the screens are read-only. */
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE m.MenuCode IN (N'SA_SALES_SUMMARY', N'SA_SALES_ATTAINMENT', N'SA_QT_CONVERSION')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

/* Verification — every leaf row, its parent and its ACCESS grant must be present. */
SELECT
    MenuCode  = m.MenuCode,
    MenuName  = m.MenuName,
    Route     = m.Route,
    SortOrder = m.SortOrder,
    IsActive  = m.IsActive,
    Parent    = parent.MenuCode
FROM dbo.Menu m
LEFT JOIN dbo.Menu parent ON parent.MenuId = m.ParentMenuId
WHERE m.MenuCode IN (N'SA_ANALYSIS', N'SA_SALES_SUMMARY', N'SA_SALES_ATTAINMENT', N'SA_QT_CONVERSION')
ORDER BY m.MenuCode;
GO

SELECT
    MenuCode       = m.MenuCode,
    PermissionCode = p.PermissionCode
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m       ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode IN (N'SA_SALES_SUMMARY', N'SA_SALES_ATTAINMENT', N'SA_QT_CONVERSION')
ORDER BY m.MenuCode, p.PermissionCode;
GO

/* Role grants are deliberately NOT seeded: a deployment owner decides who may see sales analysis.
   Mirror the roles already granted on SA_INVOICE, e.g.
       INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsActive)
       SELECT r.RoleId, m.MenuId, p.PermissionId, 1
       FROM ... ;
*/
