/* ============================================================================
   Sales Monitor — menu triad (SA_MONITOR + the four Phase A screens)
   ----------------------------------------------------------------------------
   plan-salesDecisionSupport.prompt.md, Phase A:

     /sales/monitor/so-ageing                      — open order ageing & overdue delivery (A1)
     /sales/monitor/delivered-not-fully-invoiced   — delivered not fully invoiced (A2)
     /sales/monitor/quotation-expiry               — quotation expiry watch (A3)
     /sales/monitor/einvoice-action                — e-Invoice action queue (A4)

   The Phase-1 "Inquiry" group stays the raw row-level detail. This group answers
   "what needs doing", from the columns the ERP already persists — no schema change.

   TWO artefacts are required and BOTH must stay in step — the repo trap that
   MenuDeploymentParityTests guards:
     1. ErpWeb/Menus/menus.xml  — the AUTHORITATIVE row. MenuSyncService SOFT-DISABLES any
        dbo.Menu row whose MenuCode is absent from the XML, and AccessRightService then hides
        it from navigation and locks out the page.
     2. This script — seeds the rows and their permissions for FRESH databases.

   ACCESS only. Every screen is read-only, so ADD / EDIT / DELETE are deliberately NOT
   seeded. The CSV downloads call the same service methods as the grid and are gated by the
   same ACCESS check inside the service, so no EXPORT permission is involved — the same
   convention as init-sales-inquiry-menu.sql.

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
    DECLARE @monitorId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SA_MONITOR');
    IF @monitorId IS NULL
    BEGIN
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_MONITOR', N'Monitor', @salesId, NULL, 5, 0, 1, SYSUTCDATETIME(), N'SEED');
        SET @monitorId = SCOPE_IDENTITY();
        PRINT N'Menu row SA_MONITOR created.';
    END
    ELSE
    BEGIN
        PRINT N'Menu row SA_MONITOR already present - no change.';
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_SO_AGEING')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_SO_AGEING', N'Open Order Ageing', @monitorId, N'/sales/monitor/so-ageing', 1, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_DO_NOT_FULLY_INVOICED')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_DO_NOT_FULLY_INVOICED', N'Delivered Not Fully Invoiced', @monitorId, N'/sales/monitor/delivered-not-fully-invoiced', 2, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_QT_EXPIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_QT_EXPIRY', N'Quotation Expiry Watch', @monitorId, N'/sales/monitor/quotation-expiry', 3, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_EINV_ACTION')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_EINV_ACTION', N'e-Invoice Action Queue', @monitorId, N'/sales/monitor/einvoice-action', 4, 0, 1, SYSUTCDATETIME(), N'SEED');
END
GO

/* Keep existing rows aligned with menus.xml (the XML is authoritative; this just avoids the surprise
   of a silent correction on the next startup). */
UPDATE dbo.Menu SET MenuName = N'Monitor', Route = NULL, SortOrder = 5, IsActive = 1
WHERE MenuCode = N'SA_MONITOR' AND (Route IS NOT NULL OR SortOrder <> 5 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Open Order Ageing', Route = N'/sales/monitor/so-ageing', SortOrder = 1, IsActive = 1
WHERE MenuCode = N'SA_SO_AGEING' AND (Route IS NULL OR Route <> N'/sales/monitor/so-ageing' OR SortOrder <> 1 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Delivered Not Fully Invoiced', Route = N'/sales/monitor/delivered-not-fully-invoiced', SortOrder = 2, IsActive = 1
WHERE MenuCode = N'SA_DO_NOT_FULLY_INVOICED' AND (Route IS NULL OR Route <> N'/sales/monitor/delivered-not-fully-invoiced' OR SortOrder <> 2 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Quotation Expiry Watch', Route = N'/sales/monitor/quotation-expiry', SortOrder = 3, IsActive = 1
WHERE MenuCode = N'SA_QT_EXPIRY' AND (Route IS NULL OR Route <> N'/sales/monitor/quotation-expiry' OR SortOrder <> 3 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'e-Invoice Action Queue', Route = N'/sales/monitor/einvoice-action', SortOrder = 4, IsActive = 1
WHERE MenuCode = N'SA_EINV_ACTION' AND (Route IS NULL OR Route <> N'/sales/monitor/einvoice-action' OR SortOrder <> 4 OR IsActive <> 1);
GO

/* ACCESS only — the screens are read-only. */
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE m.MenuCode IN (N'SA_SO_AGEING', N'SA_DO_NOT_FULLY_INVOICED', N'SA_QT_EXPIRY', N'SA_EINV_ACTION')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

/* Verification — every row and the ACCESS grants the script expects (4 ACTIVE grants). */
SELECT
    MenuCode  = m.MenuCode,
    MenuName  = m.MenuName,
    Route     = m.Route,
    SortOrder = m.SortOrder,
    IsActive  = m.IsActive,
    Parent    = parent.MenuCode
FROM dbo.Menu m
LEFT JOIN dbo.Menu parent ON parent.MenuId = m.ParentMenuId
WHERE m.MenuCode IN (N'SA_MONITOR', N'SA_SO_AGEING', N'SA_DO_NOT_FULLY_INVOICED',
                     N'SA_QT_EXPIRY', N'SA_EINV_ACTION')
ORDER BY m.MenuCode;
GO

SELECT
    MenuCode   = m.MenuCode,
    Permission = p.PermissionCode,
    IsActive   = mp.IsActive
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode IN (N'SA_SO_AGEING', N'SA_DO_NOT_FULLY_INVOICED', N'SA_QT_EXPIRY', N'SA_EINV_ACTION')
  AND p.PermissionCode IN (N'ACCESS', N'EXPORT', N'VIEW_PRICE')
ORDER BY m.MenuCode, p.PermissionCode;
GO
