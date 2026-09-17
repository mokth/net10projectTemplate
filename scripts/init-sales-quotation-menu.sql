-- Sales Quotation menu + permission + document-numbering seed.
-- Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
--
-- MENU DEPLOYMENT COUPLING
--   MenuSyncService reconciles dbo.Menu against ErpWeb/Menus/menus.xml on EVERY startup and
--   SOFT-DISABLES (IsActive = 0) every row whose MenuCode is absent from the XML. The XML entry
--   for SA_QT and this script MUST ship together, and MenuName / Route / SortOrder must stay in
--   step (the XML wins).
--
-- Run order: scripts/create-saqt.sql -> alter-saso-qt-link-columns.sql -> this script -> role grants.
--
-- PERMISSION MAPPING (MVP). No approval workflow: the quotation lifecycle maps onto the existing
-- permission codes so nothing new has to be invented.
--   ACCESS  list / open / lookups
--   ADD     save a new quotation
--   EDIT    update, revise
--   SUBMIT  send to customer
--   APPROVE accept
--   REJECT  mark lost
--   CANCEL  cancel
--   CLOSE   convert to Sales Order (CLOSED / CONVERTED)
--   DELETE  delete a NEW, unconverted revision
--   PRINT   print / preview
--
-- ROLE MAPPING IS NOT PRESCRIBED HERE. Mirror whatever roles already hold the equivalent grants on
-- SA_SO; the copy block at the bottom is commented out until the deployment owner confirms.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── Menu row under SA_TRANSACTIONS ────────────────────────────────────────────
DECLARE @saTransactionsId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SA_TRANSACTIONS');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @saTransactionsId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SA_QT')
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'SA_QT', N'Sales Quotation', @saTransactionsId, N'/sales/quotations', 0, 0, 1, SYSUTCDATETIME(), N'SEED');

    -- Keep the reconstructed order in step with menus.xml (QT first, then SO / INV / DO / CN / DN).
    UPDATE dbo.Menu SET SortOrder = 1, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'SEED' WHERE MenuCode = N'SA_SO';
    UPDATE dbo.Menu SET SortOrder = 2, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'SEED' WHERE MenuCode = N'SA_INVOICE';
    UPDATE dbo.Menu SET SortOrder = 3, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'SEED' WHERE MenuCode = N'SA_DO';
    UPDATE dbo.Menu SET SortOrder = 4, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'SEED' WHERE MenuCode = N'SA_CN';
    UPDATE dbo.Menu SET SortOrder = 5, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'SEED' WHERE MenuCode = N'SA_DN';
    UPDATE dbo.Menu SET SortOrder = 6, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'SEED' WHERE MenuCode = N'SA_CN_RESERVATIONS';
END
GO

-- ── MenuPermission grants for SA_QT ───────────────────────────────────────────
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p
    ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'PRINT',
                            N'SUBMIT', N'APPROVE', N'REJECT', N'CANCEL', N'CLOSE')
WHERE m.MenuCode = N'SA_QT'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

-- ── Document numbering: NumCd 'QT' ────────────────────────────────────────────
-- Monthly DEMO/HQ pattern, matching the other sales documents. Adjust CompanyCode / BranchCode /
-- Year / Month to match your tenant before running, and seed the month the module goes live.
-- Do NOT also seed dbo.AdSmNum for NumCd 'QT' when using AdSmNumDate (the date path wins).
DECLARE @Company nvarchar(10) = N'DEMO';
DECLARE @Branch nvarchar(10) = N'HQ';
DECLARE @Year smallint = 2026;
DECLARE @Month smallint = 9;

IF OBJECT_ID(N'dbo.AdSmNumDate', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM dbo.AdSmNumDate
        WHERE CompanyCode = @Company
          AND BranchCode = @Branch
          AND NumCd = N'QT'
          AND [Year] = @Year
          AND [Month] = @Month)
    BEGIN
        INSERT INTO dbo.AdSmNumDate (
            CompanyCode, BranchCode, LocationCode,
            [Year], [Month], NumCd, NumDes, TotLength, Prefix, Seq,
            Created, UserID, NumberingDelimeter, NumberingFormat)
        VALUES (
            @Company, @Branch, N'MAIN',
            @Year, @Month, N'QT', N'Sales Quotation', 4, N'QT', 1,
            GETDATE(), N'SYSTEM', N'-', NULL);
    END
END
GO

-- ── Optional: copy SA_SO role grants onto SA_QT ───────────────────────────────
-- Uncomment once the deployment owner has confirmed which roles may quote.
--
-- INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId)
-- SELECT DISTINCT rmp.RoleId, qt.MenuId, rmp.PermissionId
-- FROM dbo.RoleMenuPermission rmp
-- INNER JOIN dbo.Menu so ON so.MenuId = rmp.MenuId AND so.MenuCode = N'SA_SO'
-- CROSS JOIN (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'SA_QT') qt
-- INNER JOIN dbo.Permission p ON p.PermissionId = rmp.PermissionId
-- WHERE p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'PRINT',
--                            N'SUBMIT', N'APPROVE', N'REJECT', N'CANCEL', N'CLOSE')
--   AND NOT EXISTS (
--       SELECT 1 FROM dbo.RoleMenuPermission x
--       WHERE x.RoleId = rmp.RoleId AND x.MenuId = qt.MenuId AND x.PermissionId = rmp.PermissionId);
GO

PRINT N'SA_QT menu, MenuPermission grants and QT numbering seed ensured.';
GO
