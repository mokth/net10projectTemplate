/* ============================================================================
   Purchase Inquiry — menu triad (PO_INQUIRY + Phase 1/2 read-only screens)
   ----------------------------------------------------------------------------
   TWO artefacts required (MenuDeploymentParityTests):
     1. ErpWeb/Menus/menus.xml  — authoritative
     2. This script — seeds fresh databases

   ACCESS only. CSV export gated by ACCESS inside the service (Sales Inquiry pattern).

   Idempotent: guarded inserts. Apply TWICE on a scratch DB before touching dev.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

DECLARE @purchaseId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PURCHASE');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NULL OR @purchaseId IS NULL
BEGIN
    PRINT N'dbo.Menu or PURCHASE missing - run init-menu-access.sql first, then re-run this script. Rows NOT created.';
END
ELSE
BEGIN
    DECLARE @inquiryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PO_INQUIRY');
    IF @inquiryId IS NULL
    BEGIN
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_INQUIRY', N'Inquiry', @purchaseId, NULL, 3, 0, 1, SYSUTCDATETIME(), N'SEED');
        SET @inquiryId = SCOPE_IDENTITY();
        PRINT N'Menu row PO_INQUIRY created.';
    END
    ELSE
    BEGIN
        PRINT N'Menu row PO_INQUIRY already present - no change.';
    END

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_ORDER_OUTSTANDING')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_ORDER_OUTSTANDING', N'PO Outstanding', @inquiryId, N'/purchase/inquiry/po-outstanding', 1, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_PR_STATUS')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_PR_STATUS', N'PR Status / Outstanding', @inquiryId, N'/purchase/inquiry/pr-status', 2, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_SUPP_TRX')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_SUPP_TRX', N'Supplier Transactions', @inquiryId, N'/purchase/inquiry/supplier', 3, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_INV_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_INV_INQUIRY', N'Purchase Invoices', @inquiryId, N'/purchase/inquiry/invoices', 4, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_CDN_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_CDN_INQUIRY', N'Credit / Debit Notes', @inquiryId, N'/purchase/inquiry/credit-debit-notes', 5, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_DOC_REL')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_DOC_REL', N'Document Relationships', @inquiryId, N'/purchase/inquiry/doc-relationships', 6, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_SB_EINV_INQUIRY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_SB_EINV_INQUIRY', N'Self-billed e-Invoice', @inquiryId, N'/purchase/inquiry/einvoice', 7, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_PRICE_HISTORY')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_PRICE_HISTORY', N'Purchase Price History', @inquiryId, N'/purchase/inquiry/price-history', 8, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_MATCHING')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_MATCHING', N'PO / GR / Invoice Matching', @inquiryId, N'/purchase/inquiry/matching', 9, 0, 1, SYSUTCDATETIME(), N'SEED');

    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_DELIVERY_PERF')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_DELIVERY_PERF', N'Supplier Delivery Performance', @inquiryId, N'/purchase/inquiry/delivery-performance', 10, 0, 1, SYSUTCDATETIME(), N'SEED');
END
GO

UPDATE dbo.Menu SET MenuName = N'Inquiry', Route = NULL, SortOrder = 3, IsActive = 1
WHERE MenuCode = N'PO_INQUIRY' AND (Route IS NOT NULL OR SortOrder <> 3 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'PO Outstanding', Route = N'/purchase/inquiry/po-outstanding', SortOrder = 1, IsActive = 1
WHERE MenuCode = N'PO_ORDER_OUTSTANDING' AND (Route IS NULL OR Route <> N'/purchase/inquiry/po-outstanding' OR SortOrder <> 1 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'PR Status / Outstanding', Route = N'/purchase/inquiry/pr-status', SortOrder = 2, IsActive = 1
WHERE MenuCode = N'PO_PR_STATUS' AND (Route IS NULL OR Route <> N'/purchase/inquiry/pr-status' OR SortOrder <> 2 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Supplier Transactions', Route = N'/purchase/inquiry/supplier', SortOrder = 3, IsActive = 1
WHERE MenuCode = N'PO_SUPP_TRX' AND (Route IS NULL OR Route <> N'/purchase/inquiry/supplier' OR SortOrder <> 3 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Purchase Invoices', Route = N'/purchase/inquiry/invoices', SortOrder = 4, IsActive = 1
WHERE MenuCode = N'PO_INV_INQUIRY' AND (Route IS NULL OR Route <> N'/purchase/inquiry/invoices' OR SortOrder <> 4 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Credit / Debit Notes', Route = N'/purchase/inquiry/credit-debit-notes', SortOrder = 5, IsActive = 1
WHERE MenuCode = N'PO_CDN_INQUIRY' AND (Route IS NULL OR Route <> N'/purchase/inquiry/credit-debit-notes' OR SortOrder <> 5 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Document Relationships', Route = N'/purchase/inquiry/doc-relationships', SortOrder = 6, IsActive = 1
WHERE MenuCode = N'PO_DOC_REL' AND (Route IS NULL OR Route <> N'/purchase/inquiry/doc-relationships' OR SortOrder <> 6 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Self-billed e-Invoice', Route = N'/purchase/inquiry/einvoice', SortOrder = 7, IsActive = 1
WHERE MenuCode = N'PO_SB_EINV_INQUIRY' AND (Route IS NULL OR Route <> N'/purchase/inquiry/einvoice' OR SortOrder <> 7 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Purchase Price History', Route = N'/purchase/inquiry/price-history', SortOrder = 8, IsActive = 1
WHERE MenuCode = N'PO_PRICE_HISTORY' AND (Route IS NULL OR Route <> N'/purchase/inquiry/price-history' OR SortOrder <> 8 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'PO / GR / Invoice Matching', Route = N'/purchase/inquiry/matching', SortOrder = 9, IsActive = 1
WHERE MenuCode = N'PO_MATCHING' AND (Route IS NULL OR Route <> N'/purchase/inquiry/matching' OR SortOrder <> 9 OR IsActive <> 1);

UPDATE dbo.Menu SET MenuName = N'Supplier Delivery Performance', Route = N'/purchase/inquiry/delivery-performance', SortOrder = 10, IsActive = 1
WHERE MenuCode = N'PO_DELIVERY_PERF' AND (Route IS NULL OR Route <> N'/purchase/inquiry/delivery-performance' OR SortOrder <> 10 OR IsActive <> 1);
GO

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE m.MenuCode IN (N'PO_ORDER_OUTSTANDING', N'PO_PR_STATUS', N'PO_SUPP_TRX', N'PO_INV_INQUIRY',
                     N'PO_CDN_INQUIRY', N'PO_DOC_REL', N'PO_SB_EINV_INQUIRY',
                     N'PO_PRICE_HISTORY', N'PO_MATCHING', N'PO_DELIVERY_PERF')
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
WHERE m.MenuCode IN (N'PO_INQUIRY', N'PO_ORDER_OUTSTANDING', N'PO_PR_STATUS', N'PO_SUPP_TRX',
                     N'PO_INV_INQUIRY', N'PO_CDN_INQUIRY', N'PO_DOC_REL', N'PO_SB_EINV_INQUIRY',
                     N'PO_PRICE_HISTORY', N'PO_MATCHING', N'PO_DELIVERY_PERF')
ORDER BY m.MenuCode;
GO
