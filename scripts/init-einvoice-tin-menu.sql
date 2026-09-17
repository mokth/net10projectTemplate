/* ============================================================================
   LHDN e-Invoice TIN tools — menu row SA_EINVOICE_TIN
   ----------------------------------------------------------------------------
   ONE read-only utility screen:
     /sales/einvoice/tin   — validate a TIN against an identity document (NRIC /
                             BRN / PASSPORT / ARMY) and search taxpayers by
                             name and/or identity document.

   TWO artefacts are required and BOTH must stay in step — this is the repo trap that
   MenuDeploymentParityTests guards:
     1. ErpWeb/Menus/menus.xml  — the AUTHORITATIVE row. MenuSyncService SOFT-DISABLES any
        dbo.Menu row whose MenuCode is absent from the XML, and AccessRightService then hides
        it from navigation and locks out the page.
     2. This script — seeds the rows and their permissions for FRESH databases.
   Adding the XML row alone leaves a fresh database without a menu; adding this script alone
   gets the row soft-disabled on the next startup.

   ACCESS only. The screen is a read-only inquiry against MyInvois: it creates no ERP data,
   so ADD / EDIT / DELETE / EXPORT are deliberately NOT seeded.

   Idempotent: guarded insert + guarded permission grant, so a second run is a clean no-op.
   Safe on a populated database.

   Apply TWICE on a scratch database (second run must be a clean no-op) before touching dev.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

DECLARE @salesTransactionsId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SA_TRANSACTIONS');

/* RETURN only exits its OWN batch, so the guards must live in the SAME batch as the INSERTs. */
IF OBJECT_ID(N'dbo.Menu', N'U') IS NULL OR @salesTransactionsId IS NULL
BEGIN
    PRINT N'dbo.Menu or SA_TRANSACTIONS missing - run init-menu-access.sql first, then re-run this script. Row NOT created.';
END
ELSE
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_EINVOICE_TIN')
    BEGIN
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_EINVOICE_TIN', N'e-Invoice TIN Tools', @salesTransactionsId, N'/sales/einvoice/tin', 7, 0, 1, SYSUTCDATETIME(), N'SEED');
        PRINT N'Menu row SA_EINVOICE_TIN created.';
    END
    ELSE
    BEGIN
        PRINT N'Menu row SA_EINVOICE_TIN already present - no change.';
    END
END
GO

/* Keep an existing row aligned with menus.xml (the XML is authoritative; this just avoids the
   surprise of a silent correction on the next startup). */
UPDATE dbo.Menu
SET MenuName  = N'e-Invoice TIN Tools',
    Route     = N'/sales/einvoice/tin',
    SortOrder = 7,
    IsActive  = 1
WHERE MenuCode = N'SA_EINVOICE_TIN'
  AND (Route IS NULL OR Route <> N'/sales/einvoice/tin' OR SortOrder <> 7 OR IsActive <> 1);
GO

/* ACCESS only — the screen is read-only. */
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE m.MenuCode = N'SA_EINVOICE_TIN'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

/* Verification — the row, its parent and its ACCESS grant must all be present. */
SELECT
    MenuCode  = m.MenuCode,
    MenuName  = m.MenuName,
    Route     = m.Route,
    SortOrder = m.SortOrder,
    IsActive  = m.IsActive,
    Parent    = parent.MenuCode
FROM dbo.Menu m
LEFT JOIN dbo.Menu parent ON parent.MenuId = m.ParentMenuId
WHERE m.MenuCode = N'SA_EINVOICE_TIN';
GO

SELECT
    MenuCode       = m.MenuCode,
    PermissionCode = p.PermissionCode
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m       ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'SA_EINVOICE_TIN'
ORDER BY p.PermissionCode;
GO

/* Role grants are deliberately NOT seeded: a deployment owner decides who may use the TIN tools.
   Mirror the roles already granted on SA_INVOICE, e.g.
       INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsActive)
       SELECT r.RoleId, m.MenuId, p.PermissionId, 1
       FROM ... ;
*/
