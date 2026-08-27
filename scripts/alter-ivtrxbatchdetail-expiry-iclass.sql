-- Add staging-line classification and lot expiry for Misc Receipt (and later inventory trx).
-- Nullable for backward compatibility. Do not modify IvStockMaster.

IF COL_LENGTH('dbo.IvTrxBatchDetail', 'IClassCode') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD IClassCode nvarchar(30) NULL;
END
GO

IF COL_LENGTH('dbo.IvTrxBatchDetail', 'ExpiryDate') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD ExpiryDate datetime NULL;
END
GO
