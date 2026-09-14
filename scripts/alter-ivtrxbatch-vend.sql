-- Misc Receipt vendor snapshot on unposted/posted inventory batches.
-- Nullable for backward compatibility (legacy MR rows remain readable).

IF COL_LENGTH(N'dbo.IvTrxBatch', N'VendCode') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatch
        ADD VendCode nvarchar(60) NULL;
END
GO

IF COL_LENGTH(N'dbo.IvTrxBatch', N'VendName') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatch
        ADD VendName nvarchar(200) NULL;
END
GO
