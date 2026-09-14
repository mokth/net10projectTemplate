-- Dedicated Reason column for Misc Receipt lines (separate from Remarks).
-- Nullable for backward compatibility. No backfill of legacy "reason: remarks" text.

IF COL_LENGTH(N'dbo.IvTrxBatchDetail', N'Reason') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD Reason nvarchar(50) NULL;
END
GO
