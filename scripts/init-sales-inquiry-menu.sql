/* ============================================================================
   Sales Inquiry — menu triad (SA_INQUIRY + read-only screens)
   ----------------------------------------------------------------------------
   Operational inquiry screens:
     /sales/inquiry/customer              — customer transactions + sales history
     /sales/inquiry/quotation-status      — quotation status / expiry
     /sales/inquiry/so-outstanding        — sales order outstanding / backorder
     /sales/inquiry/so-transactions       — SO transaction / revision history
     /sales/inquiry/do-status             — delivery order status
     /sales/inquiry/invoice               — sales invoice inquiry
     /sales/inquiry/invoice-vs-doc        — invoice vs DO / SO relationships
     /sales/inquiry/credit-debit-notes    — combined CN/DN inquiry
     /sales/inquiry/price-history         — posted invoice line price history
     /sales/inquiry/einvoice              — e-Invoice status / reconciliation

   TWO artefacts are required and BOTH must stay in step — MenuDeploymentParityTests:
     1. ErpWeb/Menus/menus.xml  — AUTHORITATIVE
     2. This script — seeds rows for FRESH databases

   ACCESS only. Idempotent.
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

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_SO_TRX')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_SO_TRX', N'Sales Order Transactions', @inquiryId, N'/sales/inquiry/so-transactions', 4, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_DO_STATUS')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_DO_STATUS', N'Delivery Status', @inquiryId, N'/sales/inquiry/do-status', 5, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_INV_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_INV_INQUIRY', N'Sales Invoices', @inquiryId, N'/sales/inquiry/invoice', 6, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_INV_VS_DOC')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_INV_VS_DOC', N'Invoice vs DO / SO', @inquiryId, N'/sales/inquiry/invoice-vs-doc', 7, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_CDN_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_CDN_INQUIRY', N'Credit / Debit Notes', @inquiryId, N'/sales/inquiry/credit-debit-notes', 8, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_PRICE_HISTORY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_PRICE_HISTORY', N'Sales Price History', @inquiryId, N'/sales/inquiry/price-history', 9, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_EINV_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_EINV_INQUIRY', N'e-Invoice Status', @inquiryId, N'/sales/inquiry/einvoice', 10, 0, 1, SYSUTCDATETIME(), N'SEED');
END
GO

UPDATE dbo.Menu SET MenuName = N'Inquiry', Route = NULL, SortOrder = 4, IsActive = 1
WHERE MenuCode = N'SA_INQUIRY' AND (Route IS NOT NULL OR SortOrder <> 4 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Customer Transactions', Route = N'/sales/inquiry/customer', SortOrder = 1, IsActive = 1
WHERE MenuCode = N'SA_CUST_TRX' AND (Route IS NULL OR Route <> N'/sales/inquiry/customer' OR SortOrder <> 1 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Quotation Status / Expiry', Route = N'/sales/inquiry/quotation-status', SortOrder = 2, IsActive = 1
WHERE MenuCode = N'SA_QT_STATUS' AND (Route IS NULL OR Route <> N'/sales/inquiry/quotation-status' OR SortOrder <> 2 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Sales Order Outstanding', Route = N'/sales/inquiry/so-outstanding', SortOrder = 3, IsActive = 1
WHERE MenuCode = N'SA_SO_OUTSTANDING' AND (Route IS NULL OR Route <> N'/sales/inquiry/so-outstanding' OR SortOrder <> 3 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Sales Order Transactions', Route = N'/sales/inquiry/so-transactions', SortOrder = 4, IsActive = 1
WHERE MenuCode = N'SA_SO_TRX' AND (Route IS NULL OR Route <> N'/sales/inquiry/so-transactions' OR SortOrder <> 4 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Delivery Status', Route = N'/sales/inquiry/do-status', SortOrder = 5, IsActive = 1
WHERE MenuCode = N'SA_DO_STATUS' AND (Route IS NULL OR Route <> N'/sales/inquiry/do-status' OR SortOrder <> 5 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Sales Invoices', Route = N'/sales/inquiry/invoice', SortOrder = 6, IsActive = 1
WHERE MenuCode = N'SA_INV_INQUIRY' AND (Route IS NULL OR Route <> N'/sales/inquiry/invoice' OR SortOrder <> 6 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Invoice vs DO / SO', Route = N'/sales/inquiry/invoice-vs-doc', SortOrder = 7, IsActive = 1
WHERE MenuCode = N'SA_INV_VS_DOC' AND (Route IS NULL OR Route <> N'/sales/inquiry/invoice-vs-doc' OR SortOrder <> 7 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Credit / Debit Notes', Route = N'/sales/inquiry/credit-debit-notes', SortOrder = 8, IsActive = 1
WHERE MenuCode = N'SA_CDN_INQUIRY' AND (Route IS NULL OR Route <> N'/sales/inquiry/credit-debit-notes' OR SortOrder <> 8 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Sales Price History', Route = N'/sales/inquiry/price-history', SortOrder = 9, IsActive = 1
WHERE MenuCode = N'SA_PRICE_HISTORY' AND (Route IS NULL OR Route <> N'/sales/inquiry/price-history' OR SortOrder <> 9 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'e-Invoice Status', Route = N'/sales/inquiry/einvoice', SortOrder = 10, IsActive = 1
WHERE MenuCode = N'SA_EINV_INQUIRY' AND (Route IS NULL OR Route <> N'/sales/inquiry/einvoice' OR SortOrder <> 10 OR IsActive <> 1);
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE m.MenuCode IN (N'SA_CUST_TRX', N'SA_QT_STATUS', N'SA_SO_OUTSTANDING', N'SA_SO_TRX', N'SA_DO_STATUS',
                     N'SA_INV_INQUIRY', N'SA_INV_VS_DOC', N'SA_CDN_INQUIRY', N'SA_PRICE_HISTORY', N'SA_EINV_INQUIRY')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

SELECT
    MenuCode  = m.MenuCode,
    MenuName  = m.MenuName,
    Route     = m.Route,
    SortOrder = m.SortOrder,
    IsActive  = m.IsActive,
    Parent    = parent.MenuCode
FROM dbo.Menu m
LEFT JOIN dbo.Menu parent ON parent.MenuId = m.ParentMenuId
WHERE m.MenuCode IN (N'SA_INQUIRY', N'SA_CUST_TRX', N'SA_QT_STATUS', N'SA_SO_OUTSTANDING', N'SA_SO_TRX',
                     N'SA_DO_STATUS', N'SA_INV_INQUIRY', N'SA_INV_VS_DOC', N'SA_CDN_INQUIRY',
                     N'SA_PRICE_HISTORY', N'SA_EINV_INQUIRY')
ORDER BY m.MenuCode;
GO
