-- Migration: rename the purchase invoice family from the legacy POCDN naming
-- to POInvoice / POInvoiceDetail, and move the app-visible keys off the old names.
--
--   Tables     POCDN          -> POInvoice
--              POCDNDetail    -> POInvoiceDetail
--   Menu code  PO_CDN         -> PO_INVOICE      (dbo.Menu.MenuCode)
--   Numbering  POCDN          -> PO_INV          (dbo.AdSmNum / dbo.AdSmNumDate.NumCd)
--
-- WHEN TO RUN
--   Once per database, in the same maintenance window as the deployment of the
--   application code that expects the new names, BEFORE the app starts. The app
--   reads dbo.POInvoice / dbo.POInvoiceDetail and the PO_INV numbering module, so
--   it will fail against an un-migrated database and this script will fail against
--   an already-migrated one (it is guarded, so re-running is safe).
--
-- DO NOT RUN if a legacy (non-Blazor) application still reads dbo.POCDN /
-- dbo.POCDNDetail from the same database.
--
-- Usage: sqlcmd -S .\SQLEXPRESS -d ERPWeb -E -i scripts\rename-po-cdn-to-po-invoice.sql
--
-- Notes:
--   * PK / FK / index / default constraint names are all renamed. The DF_* rename
--     is generated from the catalog so no constraint is missed.
--   * MenuPermission / RoleMenuPermission are keyed by MenuId, so existing grants
--     follow the renamed menu row automatically.
--   * HARDENED FOR THE NEW PoCdn FAMILY: scripts/create-pocdn.sql creates a table
--     whose name is identical to the retired POCDN under SQL Server's default
--     case-insensitive collation. "POCDN exists" is therefore no longer proof that
--     the legacy purchase-invoice table is still present. The discriminators used
--     below are columns that only the new family has (PoCdn.VrBatchNo,
--     PoCdnDetail.IsStockReturn); when they are present this script is a terminal
--     no-op for the table renames instead of raising "both exist" or renaming PoCdn
--     into POInvoice.
GO

-- ── Pre-flight ────────────────────────────────────────────────────────────────
-- The "both exist" check only fires when the object named POCDN is the LEGACY table.
-- A table created by scripts/create-pocdn.sql also answers to POCDN, but carries the
-- new family's VrBatchNo / IsStockReturn columns; that is the expected post-migration
-- state (POInvoice renamed, new PoCdn created) and must not raise.
IF OBJECT_ID(N'dbo.POCDN', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.POInvoice', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.POCDN', N'VrBatchNo') IS NULL
    RAISERROR(N'Both POCDN and POInvoice exist. Resolve manually before running.', 16, 1);
GO

IF OBJECT_ID(N'dbo.POCDNDetail', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.POInvoiceDetail', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.POCDNDetail', N'IsStockReturn') IS NULL
    RAISERROR(N'Both POCDNDetail and POInvoiceDetail exist. Resolve manually before running.', 16, 1);
GO

-- ── Tables ────────────────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.POCDN', N'U') IS NULL
    PRINT N'Table POCDN already migrated';
ELSE IF COL_LENGTH(N'dbo.POCDN', N'VrBatchNo') IS NOT NULL
    PRINT N'POCDN is the new PoCdn family (VrBatchNo present), not the legacy purchase-invoice table. Terminal no-op.';
ELSE
BEGIN
    EXEC sp_rename N'dbo.POCDN', N'POInvoice';
    PRINT N'Renamed table POCDN -> POInvoice';
END
GO

IF OBJECT_ID(N'dbo.POCDNDetail', N'U') IS NULL
    PRINT N'Table POCDNDetail already migrated';
ELSE IF COL_LENGTH(N'dbo.POCDNDetail', N'IsStockReturn') IS NOT NULL
    PRINT N'POCDNDetail is the new PoCdnDetail family (IsStockReturn present). Terminal no-op.';
ELSE
BEGIN
    EXEC sp_rename N'dbo.POCDNDetail', N'POInvoiceDetail';
    PRINT N'Renamed table POCDNDetail -> POInvoiceDetail';
END
GO

-- ── Indexes ───────────────────────────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POCDN_Company_Branch_Status_DocDate' AND object_id = OBJECT_ID(N'dbo.POInvoice'))
    EXEC sp_rename N'dbo.POInvoice.IX_POCDN_Company_Branch_Status_DocDate', N'IX_POInvoice_Company_Branch_Status_DocDate', N'INDEX';
GO
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POCDN_Company_Branch_Type_Status' AND object_id = OBJECT_ID(N'dbo.POInvoice'))
    EXEC sp_rename N'dbo.POInvoice.IX_POCDN_Company_Branch_Type_Status', N'IX_POInvoice_Company_Branch_Type_Status', N'INDEX';
GO
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POCDN_Company_Branch_VendorCode' AND object_id = OBJECT_ID(N'dbo.POInvoice'))
    EXEC sp_rename N'dbo.POInvoice.IX_POCDN_Company_Branch_VendorCode', N'IX_POInvoice_Company_Branch_VendorCode', N'INDEX';
GO
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POCDN_Company_Branch_InvNo' AND object_id = OBJECT_ID(N'dbo.POInvoice'))
    EXEC sp_rename N'dbo.POInvoice.IX_POCDN_Company_Branch_InvNo', N'IX_POInvoice_Company_Branch_InvNo', N'INDEX';
GO
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POCDNDetail_PO_Link' AND object_id = OBJECT_ID(N'dbo.POInvoiceDetail'))
    EXEC sp_rename N'dbo.POInvoiceDetail.IX_POCDNDetail_PO_Link', N'IX_POInvoiceDetail_PO_Link', N'INDEX';
GO

-- ── Constraint names (PK / FK / DF) ───────────────────────────────────────────
-- Cosmetic: nothing reads constraint names. Wrapped in TRY/CATCH so a rename SQL
-- Server refuses cannot leave the migration looking half-applied — the table and
-- column names above are what the application actually depends on.
BEGIN TRY
    IF EXISTS (SELECT 1 FROM sys.objects WHERE name = N'PK_POCDN' AND parent_object_id = OBJECT_ID(N'dbo.POInvoice'))
        EXEC sp_rename N'dbo.POInvoice.PK_POCDN', N'PK_POInvoice', N'OBJECT';

    IF EXISTS (SELECT 1 FROM sys.objects WHERE name = N'PK_POCDNDetail' AND parent_object_id = OBJECT_ID(N'dbo.POInvoiceDetail'))
        EXEC sp_rename N'dbo.POInvoiceDetail.PK_POCDNDetail', N'PK_POInvoiceDetail', N'OBJECT';

    IF EXISTS (SELECT 1 FROM sys.objects WHERE name = N'FK_POCDNDetail_POCDN' AND parent_object_id = OBJECT_ID(N'dbo.POInvoiceDetail'))
        EXEC sp_rename N'dbo.POInvoiceDetail.FK_POCDNDetail_POCDN', N'FK_POInvoiceDetail_POInvoice', N'OBJECT';

    -- Default constraints, generated from the catalog so none is missed.
    DECLARE @renameDefaults nvarchar(max) = N'';

    SELECT @renameDefaults = @renameDefaults
         + N'EXEC sp_rename N''dbo.' + OBJECT_NAME(dc.parent_object_id) + N'.' + dc.name
         + N''', N''' + REPLACE(dc.name, N'DF_POCDN', N'DF_POInvoice') + N''', N''OBJECT'';'
         + CHAR(13) + CHAR(10)
    FROM sys.default_constraints AS dc
    WHERE dc.name LIKE N'DF_POCDN%'
      AND dc.parent_object_id IN (OBJECT_ID(N'dbo.POInvoice'), OBJECT_ID(N'dbo.POInvoiceDetail'));

    IF LEN(@renameDefaults) > 0
        EXEC sp_executesql @renameDefaults;

    PRINT N'Constraint names renamed (PK / FK / DF).';
END TRY
BEGIN CATCH
    PRINT N'Constraint renames skipped: ' + ERROR_MESSAGE();
END CATCH
GO

-- ── Menu code ─────────────────────────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_CDN')
   AND EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_INVOICE')
    RAISERROR(N'dbo.Menu has both PO_CDN and PO_INVOICE rows. Resolve manually before running.', 16, 1);
GO

IF EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_CDN')
BEGIN
    UPDATE dbo.Menu SET MenuCode = N'PO_INVOICE' WHERE MenuCode = N'PO_CDN';
    PRINT N'Menu code PO_CDN -> PO_INVOICE';
END
ELSE
    PRINT N'Menu code already migrated';
GO

-- ── Numbering module ──────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.AdSmNum', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.AdSmNum WHERE NumCd = N'POCDN')
       AND EXISTS (SELECT 1 FROM dbo.AdSmNum WHERE NumCd = N'PO_INV')
        RAISERROR(N'dbo.AdSmNum has both POCDN and PO_INV rows. Resolve manually before running.', 16, 1);
    ELSE
    BEGIN
        UPDATE dbo.AdSmNum SET NumCd = N'PO_INV' WHERE NumCd = N'POCDN';
        IF @@ROWCOUNT > 0 PRINT N'AdSmNum numbering module POCDN -> PO_INV';
    END
END
GO

IF OBJECT_ID(N'dbo.AdSmNumDate', N'U') IS NOT NULL
BEGIN
    -- A filtered unique index guards (Company, Branch, Year, Month, NumCd). If a
    -- PO_INV row already exists for a period that also has a POCDN row, this raises
    -- a duplicate-key error — merge those rows manually and re-run.
    UPDATE dbo.AdSmNumDate SET NumCd = N'PO_INV' WHERE NumCd = N'POCDN';
    IF @@ROWCOUNT > 0 PRINT N'AdSmNumDate numbering module POCDN -> PO_INV';
END
GO

-- ── Verify ────────────────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.POInvoice', N'U') IS NULL OR OBJECT_ID(N'dbo.POInvoiceDetail', N'U') IS NULL
    RAISERROR(N'Migration incomplete: POInvoice / POInvoiceDetail not found.', 16, 1);
ELSE
    PRINT N'Purchase invoice migration complete.';
GO
