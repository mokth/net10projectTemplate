SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF COL_LENGTH(N'dbo.IvTrxBatchDetail', N'DocumentRevision') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail ADD DocumentRevision int NOT NULL
        CONSTRAINT DF_IvTrxBatchDetail_DocumentRevision DEFAULT (0);
END;

IF COL_LENGTH(N'dbo.PrMaterialIssueLine', N'DocumentRevision') IS NULL
BEGIN
    ALTER TABLE dbo.PrMaterialIssueLine ADD DocumentRevision int NOT NULL
        CONSTRAINT DF_PrMaterialIssueLine_DocumentRevision DEFAULT (0);
END;
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.IvTrxBatchDetail')
    AND name = N'UQ_IvTrxBatchDetail_Company_Branch_Batch_Line')
    DROP INDEX UQ_IvTrxBatchDetail_Company_Branch_Batch_Line ON dbo.IvTrxBatchDetail;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.IvTrxBatchDetail')
    AND name = N'UQ_IvTrxBatchDetail_Company_Branch_Batch_Revision_Line')
    CREATE UNIQUE INDEX UQ_IvTrxBatchDetail_Company_Branch_Batch_Revision_Line
        ON dbo.IvTrxBatchDetail(CompanyCode, BranchCode, BatchNo, DocumentRevision, TrxLineNo);

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMaterialIssueLine')
    AND name = N'UQ_PrMaterialIssueLine_PostingLine')
    DROP INDEX UQ_PrMaterialIssueLine_PostingLine ON dbo.PrMaterialIssueLine;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMaterialIssueLine')
    AND name = N'UQ_PrMaterialIssueLine_PostingRevisionLine')
    CREATE UNIQUE INDEX UQ_PrMaterialIssueLine_PostingRevisionLine
        ON dbo.PrMaterialIssueLine(PostingLinkID, DocumentRevision, InventoryTrxLineNo);
GO

IF OBJECT_ID(N'dbo.StockPeriodSnapshotHdr', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockPeriodSnapshotHdr
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockPeriodSnapshotHdr PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        LedgerEpochId bigint NOT NULL,
        PeriodKey char(7) NOT NULL,
        Revision int NOT NULL,
        PostingSequenceWatermark bigint NOT NULL,
        SourceDataHash char(64) NOT NULL,
        QuantityStatus nvarchar(20) NOT NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(100) NOT NULL,
        CONSTRAINT CK_StockPeriodSnapshotHdr_Revision CHECK (Revision > 0),
        CONSTRAINT CK_StockPeriodSnapshotHdr_Watermark CHECK (PostingSequenceWatermark >= 0),
        CONSTRAINT FK_StockPeriodSnapshotHdr_Epoch FOREIGN KEY
            (CompanyCode, BranchCode, LedgerEpochId)
            REFERENCES dbo.StockLedgerEpoch(CompanyCode, BranchCode, Id),
        CONSTRAINT UQ_StockPeriodSnapshotHdr_Revision
            UNIQUE (CompanyCode, BranchCode, PeriodKey, Revision)
    );
END;

IF OBJECT_ID(N'dbo.StockPeriodSnapshotLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockPeriodSnapshotLine
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockPeriodSnapshotLine PRIMARY KEY,
        HeaderId bigint NOT NULL,
        LedgerArea nvarchar(10) NOT NULL,
        StockIdentity nvarchar(250) NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        BaseUom nvarchar(10) NOT NULL,
        BaseQty decimal(18,4) NOT NULL,
        CONSTRAINT FK_StockPeriodSnapshotLine_Header FOREIGN KEY (HeaderId)
            REFERENCES dbo.StockPeriodSnapshotHdr(Id),
        CONSTRAINT UQ_StockPeriodSnapshotLine_Identity
            UNIQUE (HeaderId, LedgerArea, StockIdentity)
    );
END;
GO
