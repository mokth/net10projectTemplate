-- Deployment artifact: add surrogate identity PKs and movement FKs on unposted
-- inventory batches so EF can insert IvTrxBatch / IvTrxBatchDetail.
-- Do NOT run automatically at application startup. Apply via normal SQL deploy.
--
-- Live ERPWeb currently has:
--   IvTrxBatch        no PK, no ID
--   IvTrxBatchDetail  PK (BatchNo, TrxLineNo), no ID / BatchId / BalLoc / Lot ids
-- Target: hybrid keys from ErpWeb/docs/inventory-stock-lot.md
--   IvTrxBatch.ID PK + UQ (CompanyCode, BranchCode, BatchNo)
--   IvTrxBatchDetail.ID PK + BatchId + UQ (CompanyCode, BranchCode, BatchNo, TrxLineNo)
--
-- Target database: ConnectionStrings:DefaultConnection (e.g. ERPWeb).
-- USE ERPWeb;
GO

-- ---------------------------------------------------------------------------
-- IvTrxBatch
-- ---------------------------------------------------------------------------
IF COL_LENGTH('dbo.IvTrxBatch', 'ID') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatch
        ADD ID int IDENTITY(1, 1) NOT NULL;
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints
    WHERE name = N'PK_IvTrxBatch'
      AND parent_object_id = OBJECT_ID(N'dbo.IvTrxBatch'))
BEGIN
    ALTER TABLE dbo.IvTrxBatch
        ADD CONSTRAINT PK_IvTrxBatch PRIMARY KEY (ID);
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UQ_IvTrxBatch_Company_Branch_BatchNo'
      AND object_id = OBJECT_ID(N'dbo.IvTrxBatch'))
BEGIN
    CREATE UNIQUE INDEX UQ_IvTrxBatch_Company_Branch_BatchNo
        ON dbo.IvTrxBatch (CompanyCode, BranchCode, BatchNo);
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_IvTrxBatch_Company_BatchStatus'
      AND object_id = OBJECT_ID(N'dbo.IvTrxBatch'))
BEGIN
    CREATE INDEX IX_IvTrxBatch_Company_BatchStatus
        ON dbo.IvTrxBatch (CompanyCode, BatchStatus);
END
GO

-- ---------------------------------------------------------------------------
-- IvTrxBatchDetail: replace natural PK with identity ID
-- ---------------------------------------------------------------------------
IF EXISTS (
    SELECT 1
    FROM sys.key_constraints
    WHERE name = N'PK_IvTrxBatchDetail'
      AND parent_object_id = OBJECT_ID(N'dbo.IvTrxBatchDetail'))
   AND COL_LENGTH('dbo.IvTrxBatchDetail', 'ID') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        DROP CONSTRAINT PK_IvTrxBatchDetail;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.key_constraints
    WHERE name = N'IX_IvTrxBatchDetail'
      AND parent_object_id = OBJECT_ID(N'dbo.IvTrxBatchDetail'))
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        DROP CONSTRAINT IX_IvTrxBatchDetail;
END
GO

IF COL_LENGTH('dbo.IvTrxBatchDetail', 'ID') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD ID int IDENTITY(1, 1) NOT NULL;
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.key_constraints
    WHERE name = N'PK_IvTrxBatchDetail'
      AND parent_object_id = OBJECT_ID(N'dbo.IvTrxBatchDetail'))
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD CONSTRAINT PK_IvTrxBatchDetail PRIMARY KEY (ID);
END
GO

IF COL_LENGTH('dbo.IvTrxBatchDetail', 'BatchId') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD BatchId int NULL;
END
GO

IF COL_LENGTH('dbo.IvTrxBatchDetail', 'FromBalLocId') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD FromBalLocId int NULL;
END
GO

IF COL_LENGTH('dbo.IvTrxBatchDetail', 'ToBalLocId') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD ToBalLocId int NULL;
END
GO

IF COL_LENGTH('dbo.IvTrxBatchDetail', 'FromLotId') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD FromLotId int NULL;
END
GO

IF COL_LENGTH('dbo.IvTrxBatchDetail', 'ToLotId') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD ToLotId int NULL;
END
GO

UPDATE d
SET d.BatchId = b.ID
FROM dbo.IvTrxBatchDetail d
INNER JOIN dbo.IvTrxBatch b
    ON b.BatchNo = d.BatchNo
   AND ISNULL(b.CompanyCode, N'') = ISNULL(d.CompanyCode, N'')
   AND ISNULL(b.BranchCode, N'') = ISNULL(d.BranchCode, N'')
WHERE d.BatchId IS NULL;
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UQ_IvTrxBatchDetail_Company_Branch_Batch_Line'
      AND object_id = OBJECT_ID(N'dbo.IvTrxBatchDetail'))
BEGIN
    CREATE UNIQUE INDEX UQ_IvTrxBatchDetail_Company_Branch_Batch_Line
        ON dbo.IvTrxBatchDetail (CompanyCode, BranchCode, BatchNo, TrxLineNo);
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_IvTrxBatchDetail_Company_ICode'
      AND object_id = OBJECT_ID(N'dbo.IvTrxBatchDetail'))
BEGIN
    CREATE INDEX IX_IvTrxBatchDetail_Company_ICode
        ON dbo.IvTrxBatchDetail (CompanyCode, ICode);
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_IvTrxBatchDetail_BatchId'
      AND object_id = OBJECT_ID(N'dbo.IvTrxBatchDetail'))
BEGIN
    CREATE INDEX IX_IvTrxBatchDetail_BatchId
        ON dbo.IvTrxBatchDetail (BatchId);
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_IvTrxBatchDetail_Batch')
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD CONSTRAINT FK_IvTrxBatchDetail_Batch
        FOREIGN KEY (BatchId) REFERENCES dbo.IvTrxBatch (ID);
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_IvTrxBatchDetail_FromBalLoc')
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD CONSTRAINT FK_IvTrxBatchDetail_FromBalLoc
        FOREIGN KEY (FromBalLocId) REFERENCES dbo.IvBalLoc (ID);
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_IvTrxBatchDetail_ToBalLoc')
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD CONSTRAINT FK_IvTrxBatchDetail_ToBalLoc
        FOREIGN KEY (ToBalLocId) REFERENCES dbo.IvBalLoc (ID);
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_IvTrxBatchDetail_FromLot')
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD CONSTRAINT FK_IvTrxBatchDetail_FromLot
        FOREIGN KEY (FromLotId) REFERENCES dbo.IvLot (ID);
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_IvTrxBatchDetail_ToLot')
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD CONSTRAINT FK_IvTrxBatchDetail_ToLot
        FOREIGN KEY (ToLotId) REFERENCES dbo.IvLot (ID);
END
GO
