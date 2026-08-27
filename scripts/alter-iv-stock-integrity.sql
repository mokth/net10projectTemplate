-- Inventory stock integrity for posting engine.
-- Do NOT run at application startup. Apply via normal SQL deploy.
-- Target: ConnectionStrings:DefaultConnection (e.g. ERPWeb).
--
-- Verified live ERPWeb (2026-08-26):
--   IvBalLoc: PK only; no UQ_IvBalLoc_StockSlice; StdQty decimal(18,4) NULL; no LotId; no rowversion
--   IvLot: table missing
--   IvTrxHistory: legacy columns; no ID PK; no BalLoc/Lot FKs; no unique batch/line index
--   Dup BalLoc slices: 0; Null StdQty: 0
GO

-- ---------------------------------------------------------------------------
-- IvLot (create if missing)
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.IvLot', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IvLot
    (
        ID int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_IvLot PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        ICode nvarchar(30) NOT NULL,
        LotNo nvarchar(50) NOT NULL,
        SourceType nvarchar(20) NULL,
        SourceDocNo nvarchar(50) NULL,
        SupplierCode nvarchar(20) NULL,
        ReceiptDate datetime NULL,
        MfgDate datetime NULL,
        ExpiryDate datetime NULL,
        QcStatus nvarchar(10) NULL,
        Remarks nvarchar(250) NULL,
        LocationCode nvarchar(10) NULL,
        Active bit NOT NULL CONSTRAINT DF_IvLot_Active DEFAULT (1),
        Created datetime NULL,
        UserID nvarchar(10) NULL,
        Updated datetime NULL,
        UpdatedUID nvarchar(10) NULL
    );
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_IvLot_Company_ICode_LotNo' AND object_id = OBJECT_ID(N'dbo.IvLot'))
BEGIN
    CREATE UNIQUE INDEX UQ_IvLot_Company_ICode_LotNo
        ON dbo.IvLot (CompanyCode, ICode, LotNo);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_IvLot_StockMaster')
BEGIN
    -- Live ERPWeb IvStockMaster PK may be ICode-only (legacy). Only add FK when a matching
    -- unique/primary key on (CompanyCode, ICode) exists.
    IF EXISTS (
        SELECT 1
        FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID(N'dbo.IvStockMaster')
          AND i.is_unique = 1
          AND EXISTS (
              SELECT 1 FROM sys.index_columns ic
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND c.name = N'CompanyCode')
          AND EXISTS (
              SELECT 1 FROM sys.index_columns ic
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND c.name = N'ICode'))
    BEGIN
        ALTER TABLE dbo.IvLot
            ADD CONSTRAINT FK_IvLot_StockMaster
            FOREIGN KEY (CompanyCode, ICode) REFERENCES dbo.IvStockMaster (CompanyCode, ICode);
    END
END
GO

-- ---------------------------------------------------------------------------
-- IvBalLoc: LotId, RowVersion, NOT NULL StdQty, unique slice, CHECK >= 0
-- ---------------------------------------------------------------------------
IF COL_LENGTH(N'dbo.IvBalLoc', N'LotId') IS NULL
BEGIN
    ALTER TABLE dbo.IvBalLoc ADD LotId int NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.IvBalLoc') AND name = N'RowVersion')
BEGIN
    ALTER TABLE dbo.IvBalLoc ADD RowVersion rowversion NOT NULL;
END
GO

UPDATE dbo.IvBalLoc SET StdQty = 0 WHERE StdQty IS NULL;
GO

UPDATE dbo.IvBalLoc SET CompanyCode = N'' WHERE CompanyCode IS NULL;
UPDATE dbo.IvBalLoc SET BranchCode = N'' WHERE BranchCode IS NULL;
UPDATE dbo.IvBalLoc SET LocCode = N'' WHERE LocCode IS NULL;
UPDATE dbo.IvBalLoc SET LotNo = N'' WHERE LotNo IS NULL;
UPDATE dbo.IvBalLoc SET IStatus = N'' WHERE IStatus IS NULL;
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'dbo.IvBalLoc')
      AND c.name = N'StdQty'
      AND c.is_nullable = 1)
BEGIN
    ALTER TABLE dbo.IvBalLoc
        ALTER COLUMN StdQty decimal(18, 4) NOT NULL;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'dbo.IvBalLoc')
      AND c.name = N'CompanyCode'
      AND c.is_nullable = 1)
BEGIN
    ALTER TABLE dbo.IvBalLoc ALTER COLUMN CompanyCode nvarchar(5) NOT NULL;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'dbo.IvBalLoc')
      AND c.name = N'BranchCode'
      AND c.is_nullable = 1)
BEGIN
    ALTER TABLE dbo.IvBalLoc ALTER COLUMN BranchCode nvarchar(5) NOT NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_IvBalLoc_StdQty_NonNegative'
      AND parent_object_id = OBJECT_ID(N'dbo.IvBalLoc'))
BEGIN
    ALTER TABLE dbo.IvBalLoc
        ADD CONSTRAINT CK_IvBalLoc_StdQty_NonNegative CHECK (StdQty >= 0);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_IvBalLoc_StockSlice' AND object_id = OBJECT_ID(N'dbo.IvBalLoc'))
BEGIN
    CREATE UNIQUE INDEX UQ_IvBalLoc_StockSlice
        ON dbo.IvBalLoc (CompanyCode, BranchCode, ICode, WHCode, LocCode, LotNo, IStatus);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_IvBalLoc_Lot')
BEGIN
    ALTER TABLE dbo.IvBalLoc
        ADD CONSTRAINT FK_IvBalLoc_Lot
        FOREIGN KEY (LotId) REFERENCES dbo.IvLot (ID);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_IvBalLoc_LotId' AND object_id = OBJECT_ID(N'dbo.IvBalLoc'))
BEGIN
    CREATE INDEX IX_IvBalLoc_LotId ON dbo.IvBalLoc (LotId);
END
GO

-- ---------------------------------------------------------------------------
-- IvTrxHistory: identity PK + movement FKs + unique batch/line
-- ---------------------------------------------------------------------------
IF COL_LENGTH(N'dbo.IvTrxHistory', N'ID') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxHistory ADD ID int IDENTITY(1, 1) NOT NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE name = N'PK_IvTrxHistory' AND parent_object_id = OBJECT_ID(N'dbo.IvTrxHistory'))
BEGIN
    ALTER TABLE dbo.IvTrxHistory ADD CONSTRAINT PK_IvTrxHistory PRIMARY KEY (ID);
END
GO

IF COL_LENGTH(N'dbo.IvTrxHistory', N'FromBalLocId') IS NULL
    ALTER TABLE dbo.IvTrxHistory ADD FromBalLocId int NULL;
GO
IF COL_LENGTH(N'dbo.IvTrxHistory', N'ToBalLocId') IS NULL
    ALTER TABLE dbo.IvTrxHistory ADD ToBalLocId int NULL;
GO
IF COL_LENGTH(N'dbo.IvTrxHistory', N'FromLotId') IS NULL
    ALTER TABLE dbo.IvTrxHistory ADD FromLotId int NULL;
GO
IF COL_LENGTH(N'dbo.IvTrxHistory', N'ToLotId') IS NULL
    ALTER TABLE dbo.IvTrxHistory ADD ToLotId int NULL;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_IvTrxHistory_Company_Branch_Batch_Line'
      AND object_id = OBJECT_ID(N'dbo.IvTrxHistory'))
BEGIN
    CREATE UNIQUE INDEX UQ_IvTrxHistory_Company_Branch_Batch_Line
        ON dbo.IvTrxHistory (CompanyCode, BranchCode, BatchNo, TrxLineNo);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_IvTrxHistory_FromBalLoc')
BEGIN
    ALTER TABLE dbo.IvTrxHistory
        ADD CONSTRAINT FK_IvTrxHistory_FromBalLoc
        FOREIGN KEY (FromBalLocId) REFERENCES dbo.IvBalLoc (ID);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_IvTrxHistory_ToBalLoc')
BEGIN
    ALTER TABLE dbo.IvTrxHistory
        ADD CONSTRAINT FK_IvTrxHistory_ToBalLoc
        FOREIGN KEY (ToBalLocId) REFERENCES dbo.IvBalLoc (ID);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_IvTrxHistory_FromLot')
BEGIN
    ALTER TABLE dbo.IvTrxHistory
        ADD CONSTRAINT FK_IvTrxHistory_FromLot
        FOREIGN KEY (FromLotId) REFERENCES dbo.IvLot (ID);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_IvTrxHistory_ToLot')
BEGIN
    ALTER TABLE dbo.IvTrxHistory
        ADD CONSTRAINT FK_IvTrxHistory_ToLot
        FOREIGN KEY (ToLotId) REFERENCES dbo.IvLot (ID);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_IvTrxBatchDetail_FromLot')
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD CONSTRAINT FK_IvTrxBatchDetail_FromLot
        FOREIGN KEY (FromLotId) REFERENCES dbo.IvLot (ID);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_IvTrxBatchDetail_ToLot')
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ADD CONSTRAINT FK_IvTrxBatchDetail_ToLot
        FOREIGN KEY (ToLotId) REFERENCES dbo.IvLot (ID);
END
GO
