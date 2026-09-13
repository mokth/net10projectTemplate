-- Patch: add POOrder financial-close columns (procurement plan v2.3 Phase 3).
-- FinClosed is recomputed after GR/VR/INV/CN post and rollback.
-- Safe to re-run. Usage: sqlcmd -S .\SQLEXPRESS -d ERPWeb -E -i scripts\alter-poorder-finclosed.sql
GO

IF OBJECT_ID(N'dbo.POOrder', N'U') IS NULL
    RAISERROR(N'POOrder missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.POOrder', N'FinClosed') IS NULL
BEGIN
    ALTER TABLE dbo.POOrder ADD FinClosed bit NOT NULL
        CONSTRAINT DF_POOrder_FinClosed DEFAULT (0);
    PRINT N'Added POOrder.FinClosed';
END
ELSE
    PRINT N'POOrder.FinClosed already exists';
GO

IF COL_LENGTH(N'dbo.POOrder', N'FinClosedOn') IS NULL
BEGIN
    ALTER TABLE dbo.POOrder ADD FinClosedOn datetime2 NULL;
    PRINT N'Added POOrder.FinClosedOn';
END
ELSE
    PRINT N'POOrder.FinClosedOn already exists';
GO

IF COL_LENGTH(N'dbo.POOrder', N'FinClosedBy') IS NULL
BEGIN
    ALTER TABLE dbo.POOrder ADD FinClosedBy nvarchar(20) NULL;
    PRINT N'Added POOrder.FinClosedBy';
END
ELSE
    PRINT N'POOrder.FinClosedBy already exists';
GO
