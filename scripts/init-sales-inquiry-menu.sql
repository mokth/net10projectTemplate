/* ============================================================================
   Sales Inquiry Phase 1 — menu triad (SA_INQUIRY + the seven read-only screens)
   ----------------------------------------------------------------------------
   Seven read-only operational inquiry screens:
     /sales/inquiry/customer              — customer transactions + sales history (two tabs)
     /sales/inquiry/quotation-status      — quotation status / expiry
     /sales/inquiry/so-outstanding        — sales order outstanding / backorder
     /sales/inquiry/do-status             — delivery order status
     /sales/inquiry/invoice-vs-doc        — invoice vs DO / SO relationships
     /sales/inquiry/credit-debit-notes    — combined CN/DN inquiry (Type filter)
     /sales/inquiry/einvoice              — e-Invoice status / reconciliation

   TWO artefacts are required and BOTH must stay in step — this is the repo trap that
   MenuDeploymentParityTests guards:
     1. ErpWeb/Menus/menus.xml  — the AUTHORITATIVE row. MenuSyncService SOFT-DISABLES any
        dbo.Menu row whose MenuCode is absent from the XML, and AccessRightService then hides
        it from navigation and locks out the page.
     2. This script — seeds the rows and their permissions for FRESH databases.
   Adding the XML rows alone leaves a fresh database without menus; adding this script alone
   gets the rows soft-disabled on the next startup.

   ACCESS only. All seven screens are read-only inquiries: they create no data, so ADD / EDIT /
   DELETE / EXPORT are deliberately NOT seeded. (The CSV downloads call the same service methods
   as the grid and are gated by the same ACCESS check inside the service, so no EXPORT permission
   is involved — the same convention as init-sales-analysis-menu.sql.)

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
    DECLARE @inquiryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SA_INQUIRY');
    IF @inquiryId IS NULL
    BEGIN
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_INQUIRY', N'Inquiry', @salesId, NULL, 4, 0, 1, SYSUTCDATETIME(), N'SEED');
        SET @inquiryId = SCOPE_IDENTITY();
        PRINT N'Menu row SA_INQUIRY created.';
    END
    ELSE
    BEGIN
        PRINT N'Menu row SA_INQUIRY already present - no change.';
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_CUST_TRX')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_CUST_TRX', N'Customer Transactions', @inquiryId, N'/sales/inquiry/customer', 1, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_QT_STATUS')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_QT_STATUS', N'Quotation Status / Expiry', @inquiryId, N'/sales/inquiry/quotation-status', 2, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_SO_OUTSTANDING')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_SO_OUTSTANDING', N'Sales Order Outstanding', @inquiryId, N'/sales/inquiry/so-outstanding', 3, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_DO_STATUS')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_DO_STATUS', N'Delivery Status', @inquiryId, N'/sales/inquiry/do-status', 4, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_INV_VS_DOC')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_INV_VS_DOC', N'Invoice vs DO / SO', @inquiryId, N'/sales/inquiry/invoice-vs-doc', 5, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_CDN_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_CDN_INQUIRY', N'Credit / Debit Notes', @inquiryId, N'/sales/inquiry/credit-debit-notes', 6, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_EINV_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_EINV_INQUIRY', N'e-Invoice Status', @inquiryId, N'/sales/inquiry/einvoice', 7, 0, 1, SYSUTCDATETIME(), N'SEED');
END
GO

/* Keep existing rows aligned with menus.xml (the XML is authoritative; this just avoids the surprise
   of a silent correction on the next startup). */
UPDATE dbo.Menu SET MenuName = N'Inquiry', Route = NULL, SortOrder = 4, IsActive = 1
WHERE MenuCode = N'SA_INQUIRY' AND (Route IS NOT NULL OR SortOrder <> 4 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Customer Transactions', Route = N'/sales/inquiry/customer', SortOrder = 1, IsActive = 1
WHERE MenuCode = N'SA_CUST_TRX' AND (Route IS NULL OR Route <> N'/sales/inquiry/customer' OR SortOrder <> 1 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Quotation Status / Expiry', Route = N'/sales/inquiry/quotation-status', SortOrder = 2, IsActive = 1
WHERE MenuCode = N'SA_QT_STATUS' AND (Route IS NULL OR Route <> N'/sales/inquiry/quotation-status' OR SortOrder <> 2 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Sales Order Outstanding', Route = N'/sales/inquiry/so-outstanding', SortOrder = 3, IsActive = 1
WHERE MenuCode = N'SA_SO_OUTSTANDING' AND (Route IS NULL OR Route <> N'/sales/inquiry/so-outstanding' OR SortOrder <> 3 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Delivery Status', Route = N'/sales/inquiry/do-status', SortOrder = 4, IsActive = 1
WHERE MenuCode = N'SA_DO_STATUS' AND (Route IS NULL OR Route <> N'/sales/inquiry/do-status' OR SortOrder <> 4 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Invoice vs DO / SO', Route = N'/sales/inquiry/invoice-vs-doc', SortOrder = 5, IsActive = 1
WHERE MenuCode = N'SA_INV_VS_DOC' AND (Route IS NULL OR Route <> N'/sales/inquiry/invoice-vs-doc' OR SortOrder <> 5 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Credit / Debit Notes', Route = N'/sales/inquiry/credit-debit-notes', SortOrder = 6, IsActive = 1
WHERE MenuCode = N'SA_CDN_INQUIRY' AND (Route IS NULL OR Route <> N'/sales/inquiry/credit-debit-notes' OR SortOrder <> 6 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'e-Invoice Status', Route = N'/sales/inquiry/einvoice', SortOrder = 7, IsActive = 1
WHERE MenuCode = N'SA_EINV_INQUIRY' AND (Route IS NULL OR Route <> N'/sales/inquiry/einvoice' OR SortOrder <> 7 OR IsActive <> 1);
GO

/* ACCESS only — the screens are read-only. */
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE m.MenuCode IN (N'SA_CUST_TRX', N'SA_QT_STATUS', N'SA_SO_OUTSTANDING', N'SA_DO_STATUS',
                     N'SA_INV_VS_DOC', N'SA_CDN_INQUIRY', N'SA_EINV_INQUIRY')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

/* Verification — every leaf row and its ACCESS grant must be present. */
SELECT
    MenuCode  = m.MenuCode,
    MenuName  = m.MenuName,
    Route     = m.Route,
    SortOrder = m.SortOrder,
    IsActive  = m.IsActive,
    Parent    = parent.MenuCode
FROM dbo.Menu m
LEFT JOIN dbo.Menu parent ON parent.MenuId = m.ParentMenuId
WHERE m.MenuCode IN (N'SA_INQUIRY', N'SA_CUST_TRX', N'SA_QT_STATUS', N'SA_SO_OUTSTANDING',
                     N'SA_DO_STATUS', N'SA_INV_VS_DOC', N'SA_CDN_INQUIRY', N'SA_EINV_INQUIRY')
ORDER BY m.MenuCode;
GO
