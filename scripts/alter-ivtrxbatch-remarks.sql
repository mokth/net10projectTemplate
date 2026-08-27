-- Document-level remark on unposted inventory batches (Misc Receipt header).
-- Nullable for backward compatibility.

IF COL_LENGTH('dbo.IvTrxBatch', 'Remarks') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatch
        ADD Remarks nvarchar(250) NULL;
END
GO
