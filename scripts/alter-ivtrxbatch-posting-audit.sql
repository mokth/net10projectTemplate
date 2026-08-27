-- Posting/rollback audit columns on IvTrxBatch.
-- Do NOT run at application startup. Apply via normal SQL deploy.
GO

IF COL_LENGTH(N'dbo.IvTrxBatch', N'PostedDate') IS NULL
    ALTER TABLE dbo.IvTrxBatch ADD PostedDate datetime NULL;
GO

IF COL_LENGTH(N'dbo.IvTrxBatch', N'PostedBy') IS NULL
    ALTER TABLE dbo.IvTrxBatch ADD PostedBy nvarchar(10) NULL;
GO

IF COL_LENGTH(N'dbo.IvTrxBatch', N'RollbackDate') IS NULL
    ALTER TABLE dbo.IvTrxBatch ADD RollbackDate datetime NULL;
GO

IF COL_LENGTH(N'dbo.IvTrxBatch', N'RollbackBy') IS NULL
    ALTER TABLE dbo.IvTrxBatch ADD RollbackBy nvarchar(10) NULL;
GO

IF COL_LENGTH(N'dbo.IvTrxBatch', N'PostedCount') IS NULL
    ALTER TABLE dbo.IvTrxBatch ADD PostedCount int NOT NULL CONSTRAINT DF_IvTrxBatch_PostedCount DEFAULT (0);
GO

IF COL_LENGTH(N'dbo.IvTrxBatch', N'RollbackCount') IS NULL
    ALTER TABLE dbo.IvTrxBatch ADD RollbackCount int NOT NULL CONSTRAINT DF_IvTrxBatch_RollbackCount DEFAULT (0);
GO

IF COL_LENGTH(N'dbo.IvTrxBatch', N'PostingOperationId') IS NULL
    ALTER TABLE dbo.IvTrxBatch ADD PostingOperationId uniqueidentifier NULL;
GO

IF COL_LENGTH(N'dbo.IvTrxBatch', N'RollbackOperationId') IS NULL
    ALTER TABLE dbo.IvTrxBatch ADD RollbackOperationId uniqueidentifier NULL;
GO
