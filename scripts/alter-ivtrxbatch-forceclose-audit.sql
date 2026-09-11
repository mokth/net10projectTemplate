-- Force-close audit columns on IvTrxBatch (R4).
-- A DO force-close retains the SP batch (BatchStatus stays POSTED) and stamps it with these
-- three columns. The stamp is the tombstone: every SP-batch mutation path must reject a batch
-- where ForceCloseDate IS NOT NULL.
-- Do NOT run at application startup. Apply via normal SQL deploy. Idempotent.
GO

IF COL_LENGTH(N'dbo.IvTrxBatch', N'ForceCloseDate') IS NULL
    ALTER TABLE dbo.IvTrxBatch ADD ForceCloseDate datetime NULL;
GO

IF COL_LENGTH(N'dbo.IvTrxBatch', N'ForceCloseBy') IS NULL
    ALTER TABLE dbo.IvTrxBatch ADD ForceCloseBy nvarchar(10) NULL;
GO

IF COL_LENGTH(N'dbo.IvTrxBatch', N'ForceCloseReason') IS NULL
    ALTER TABLE dbo.IvTrxBatch ADD ForceCloseReason nvarchar(250) NULL;
GO
