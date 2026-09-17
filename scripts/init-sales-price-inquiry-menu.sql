/* ============================================================================
   Phase 6 — Price Inquiry menu (SA_PRICE_INQUIRY)
   ----------------------------------------------------------------------------
   Deploys the read-only price-inquiry screen.

   TWO artefacts are required and BOTH must stay in step — this is the repo trap that
   MenuDeploymentParityTests guards:
     1. ErpWeb/Menus/menus.xml  — the AUTHORITATIVE row. MenuSyncService SOFT-DISABLES any
        dbo.Menu row whose MenuCode is absent from the XML, and AccessRightService then hides
        it from navigation and locks out the page.
     2. This script — seeds the row and its permissions for FRESH databases.
   Adding the XML row alone leaves a fresh database without the menu; adding this script alone
   gets the row soft-disabled on the next startup.

   ACCESS only. The screen is read-only: it explains which level produced a price and why the
   others did not apply. It creates no data, so ADD / EDIT / DELETE / EXPORT are deliberately
   NOT seeded. It is NOT gated on VIEW_PRICE either, because a user who may not see prices on a
   document has no reason to open a pricing inquiry; grant VIEW_PRICE to the role if the level
   results should show amounts.

   Idempotent: guarded inserts, so a second run is a clean no-op. Safe on a populated database.

   Apply TWICE on a scratch database (second run must be a clean no-op) before touching dev.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

DECLARE @saMasterId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SA_MASTER');

/* NOTE: RETURN only exits its OWN batch, so the "menu tree missing" guard must live in the SAME batch
   as the INSERT. Splitting them (a RETURN in one batch, the INSERT in the next) would print the warning
   and then create the row anyway, parentless — which is how this looked on a database with no menu
   tree at all. */
IF OBJECT_ID(N'dbo.Menu', N'U') IS NULL OR @saMasterId IS NULL
BEGIN
    PRINT N'dbo.Menu or SA_MASTER missing - run init-menu-access.sql first, then re-run this script. Row NOT created.';
END
ELSE IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_PRICE_INQUIRY')
BEGIN
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'SA_PRICE_INQUIRY', N'Price Inquiry', @saMasterId, N'/sales/price-inquiry', 21, 0, 1, SYSUTCDATETIME(), N'SEED');

    PRINT N'Menu row SA_PRICE_INQUIRY created.';
END
ELSE
BEGIN
    PRINT N'Menu row SA_PRICE_INQUIRY already present - no change.';
END
GO

/* Keep an existing row aligned with menus.xml (the XML is authoritative, so a route/sort drift here
   would be corrected by MenuSyncService anyway - this just avoids the surprise). */
UPDATE dbo.Menu
SET Route     = N'/sales/price-inquiry',
    MenuName  = N'Price Inquiry',
    SortOrder = 21,
    IsActive  = 1
WHERE MenuCode = N'SA_PRICE_INQUIRY'
  AND (Route IS NULL OR Route <> N'/sales/price-inquiry' OR SortOrder <> 21 OR IsActive <> 1);
GO

/* ACCESS only - the screen is read-only. */
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE m.MenuCode = N'SA_PRICE_INQUIRY'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

/* Verification — the row, its parent and its permission must all be present. */
SELECT
    MenuCode    = m.MenuCode,
    MenuName    = m.MenuName,
    Route       = m.Route,
    SortOrder   = m.SortOrder,
    IsActive    = m.IsActive,
    Parent      = parent.MenuCode
FROM dbo.Menu m
LEFT JOIN dbo.Menu parent ON parent.MenuId = m.ParentMenuId
WHERE m.MenuCode = N'SA_PRICE_INQUIRY';
GO

SELECT
    PermissionCode = p.PermissionCode
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m       ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode = N'SA_PRICE_INQUIRY'
ORDER BY p.PermissionCode;
GO

/* Role grants are deliberately NOT seeded: a deployment owner decides who may inquire about prices.
   Mirror the roles already granted to SA_CUST_PRICE_GROUP, e.g.
       INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsActive)
       SELECT r.RoleId, m.MenuId, p.PermissionId, 1
       FROM ... ;
*/
