-- Patch: add POVendorByItem.PriceTolerance + POCDN.PriceTolerance override (procurement plan v2.3 Phase 3).
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

IF OBJECT_ID(N'dbo.POCDN', N'U') IS NULL
    RAISERROR(N'POCDN missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.POCDN', N'PriceTolerance') IS NULL
BEGIN
    ALTER TABLE dbo.POCDN ADD PriceTolerance decimal(18,4) NULL;
    PRINT N'Added POCDN.PriceTolerance';
END
ELSE
    PRINT N'POCDN.PriceTolerance already exists';
GO

IF COL_LENGTH(N'dbo.POCDN', N'PostedDate') IS NULL
BEGIN
    ALTER TABLE dbo.POCDN ADD PostedDate datetime2 NULL;
    PRINT N'Added POCDN.PostedDate';
END
ELSE
    PRINT N'POCDN.PostedDate already exists';
GO

IF COL_LENGTH(N'dbo.POCDN', N'PostedBy') IS NULL
BEGIN
    ALTER TABLE dbo.POCDN ADD PostedBy nvarchar(20) NULL;
    PRINT N'Added POCDN.PostedBy';
END
ELSE
    PRINT N'POCDN.PostedBy already exists';
GO

IF COL_LENGTH(N'dbo.POCDN', N'RollbackDate') IS NULL
BEGIN
    ALTER TABLE dbo.POCDN ADD RollbackDate datetime2 NULL;
    PRINT N'Added POCDN.RollbackDate';
END
ELSE
    PRINT N'POCDN.RollbackDate already exists';
GO

IF COL_LENGTH(N'dbo.POCDN', N'RollbackBy') IS NULL
BEGIN
    ALTER TABLE dbo.POCDN ADD RollbackBy nvarchar(20) NULL;
    PRINT N'Added POCDN.RollbackBy';
END
ELSE
    PRINT N'POCDN.RollbackBy already exists';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_POCDN_PriceTolerance' AND parent_object_id = OBJECT_ID(N'dbo.POCDN'))
    ALTER TABLE dbo.POCDN ADD CONSTRAINT CK_POCDN_PriceTolerance
        CHECK (PriceTolerance IS NULL OR (PriceTolerance >= 0 AND PriceTolerance <= 100));
GO
