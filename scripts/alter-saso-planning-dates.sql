-- Patch: add SaSODetail planning dates for Production (DeliveryDate, ETA, ETD).
-- User-authored promise dates on the SO line. Nullable; not fulfillment actuals.
-- Safe to re-run. Usage: sqlcmd -S .\SQLEXPRESS -d ERPWeb -E -i scripts\alter-saso-planning-dates.sql
GO

IF OBJECT_ID(N'dbo.SaSODetail', N'U') IS NULL
    RAISERROR(N'SaSODetail missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.SaSODetail', N'DeliveryDate') IS NULL
BEGIN
    ALTER TABLE dbo.SaSODetail ADD DeliveryDate datetime2 NULL;
    PRINT N'Added SaSODetail.DeliveryDate';
END
ELSE
    PRINT N'SaSODetail.DeliveryDate already exists';
GO

IF COL_LENGTH(N'dbo.SaSODetail', N'ETA') IS NULL
BEGIN
    ALTER TABLE dbo.SaSODetail ADD ETA datetime2 NULL;
    PRINT N'Added SaSODetail.ETA';
END
ELSE
    PRINT N'SaSODetail.ETA already exists';
GO

IF COL_LENGTH(N'dbo.SaSODetail', N'ETD') IS NULL
BEGIN
    ALTER TABLE dbo.SaSODetail ADD ETD datetime2 NULL;
    PRINT N'Added SaSODetail.ETD';
END
ELSE
    PRINT N'SaSODetail.ETD already exists';
GO
