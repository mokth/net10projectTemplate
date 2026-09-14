-- Patch: add POVendorByItem.PriceTolerance + POInvoice.PriceTolerance override (procurement plan v2.3 Phase 3).
-- Safe to re-run. Usage: sqlcmd -S .\SQLEXPRESS -d ERPWeb -E -i scripts\alter-povendorbyitem-pricetolerance.sql
GO

IF OBJECT_ID(N'dbo.POVendorByItem', N'U') IS NULL
    RAISERROR(N'POVendorByItem missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.POVendorByItem', N'PriceTolerance') IS NULL
BEGIN
    ALTER TABLE dbo.POVendorByItem ADD PriceTolerance decimal(18,4) NOT NULL
        CONSTRAINT DF_POVendorByItem_PriceTolerance DEFAULT (0);
    PRINT N'Added POVendorByItem.PriceTolerance';
END
ELSE
    PRINT N'POVendorByItem.PriceTolerance already exists';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_POVendorByItem_PriceTolerance' AND parent_object_id = OBJECT_ID(N'dbo.POVendorByItem'))
    ALTER TABLE dbo.POVendorByItem ADD CONSTRAINT CK_POVendorByItem_PriceTolerance
        CHECK (PriceTolerance >= 0 AND PriceTolerance <= 100);
GO

IF OBJECT_ID(N'dbo.POInvoice', N'U') IS NULL
    RAISERROR(N'POInvoice missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.POInvoice', N'PriceTolerance') IS NULL
BEGIN
    ALTER TABLE dbo.POInvoice ADD PriceTolerance decimal(18,4) NULL;
    PRINT N'Added POInvoice.PriceTolerance';
END
ELSE
    PRINT N'POInvoice.PriceTolerance already exists';
GO

IF COL_LENGTH(N'dbo.POInvoice', N'PostedDate') IS NULL
BEGIN
    ALTER TABLE dbo.POInvoice ADD PostedDate datetime2 NULL;
    PRINT N'Added POInvoice.PostedDate';
END
ELSE
    PRINT N'POInvoice.PostedDate already exists';
GO

IF COL_LENGTH(N'dbo.POInvoice', N'PostedBy') IS NULL
BEGIN
    ALTER TABLE dbo.POInvoice ADD PostedBy nvarchar(20) NULL;
    PRINT N'Added POInvoice.PostedBy';
END
ELSE
    PRINT N'POInvoice.PostedBy already exists';
GO

IF COL_LENGTH(N'dbo.POInvoice', N'RollbackDate') IS NULL
BEGIN
    ALTER TABLE dbo.POInvoice ADD RollbackDate datetime2 NULL;
    PRINT N'Added POInvoice.RollbackDate';
END
ELSE
    PRINT N'POInvoice.RollbackDate already exists';
GO

IF COL_LENGTH(N'dbo.POInvoice', N'RollbackBy') IS NULL
BEGIN
    ALTER TABLE dbo.POInvoice ADD RollbackBy nvarchar(20) NULL;
    PRINT N'Added POInvoice.RollbackBy';
END
ELSE
    PRINT N'POInvoice.RollbackBy already exists';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_POInvoice_PriceTolerance' AND parent_object_id = OBJECT_ID(N'dbo.POInvoice'))
    ALTER TABLE dbo.POInvoice ADD CONSTRAINT CK_POInvoice_PriceTolerance
        CHECK (PriceTolerance IS NULL OR (PriceTolerance >= 0 AND PriceTolerance <= 100));
GO
