SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.StockLedgerEpoch', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockLedgerEpoch
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockLedgerEpoch PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        EffectiveFrom datetime2(7) NOT NULL,
        CutoverPostingSequence bigint NOT NULL,
        Version int NOT NULL CONSTRAINT DF_StockLedgerEpoch_Version DEFAULT (2),
        Status nvarchar(10) NOT NULL,
        MigrationBatchId uniqueidentifier NOT NULL,
        ReconciliationManifestHash char(64) NOT NULL,
        ActivatedAtUtc datetime2(7) NULL,
        ActivatedBy nvarchar(100) NULL,
        CONSTRAINT AK_StockLedgerEpoch_Tenant_Id UNIQUE (CompanyCode, BranchCode, Id),
        CONSTRAINT CK_StockLedgerEpoch_Status CHECK (Status IN (N'PREPARED',N'ACTIVE',N'RETIRED')),
        CONSTRAINT CK_StockLedgerEpoch_Version CHECK (Version >= 2)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockLedgerEpoch') AND name = N'UQ_StockLedgerEpoch_ActiveBranch')
    CREATE UNIQUE INDEX UQ_StockLedgerEpoch_ActiveBranch
        ON dbo.StockLedgerEpoch(CompanyCode, BranchCode, Status) WHERE Status = N'ACTIVE';
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockLedgerEpoch') AND name = N'UQ_StockLedgerEpoch_MigrationBatch')
    CREATE UNIQUE INDEX UQ_StockLedgerEpoch_MigrationBatch
        ON dbo.StockLedgerEpoch(CompanyCode, BranchCode, MigrationBatchId);

IF OBJECT_ID(N'dbo.StockPostingBranchSequence', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockPostingBranchSequence
    (
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        LastSequence bigint NOT NULL,
        UpdatedAtUtc datetime2(7) NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_StockPostingBranchSequence PRIMARY KEY (CompanyCode, BranchCode),
        CONSTRAINT CK_StockPostingBranchSequence_Value CHECK (LastSequence >= 0)
    );
END;

IF OBJECT_ID(N'dbo.StockPosting', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockPosting
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockPosting PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        LedgerEpochId bigint NOT NULL,
        PostingSequence bigint NOT NULL,
        RequestId uniqueidentifier NOT NULL,
        CommandType nvarchar(40) NOT NULL,
        RequestFingerprint char(64) NOT NULL,
        SourceModule nvarchar(20) NOT NULL,
        SourceDocumentType nvarchar(40) NOT NULL,
        SourceDocumentId nvarchar(64) NOT NULL,
        SourceDocumentNo nvarchar(50) NOT NULL,
        DocumentRevision int NOT NULL,
        PostingRole nvarchar(20) NOT NULL,
        SourceSnapshotJson nvarchar(max) NOT NULL,
        SourceSnapshotHash char(64) NOT NULL,
        SourceSnapshotSchemaVersion int NOT NULL,
        EffectiveAt datetime2(7) NOT NULL,
        BusinessDate date NOT NULL,
        PeriodKey char(7) NOT NULL,
        PostedAtUtc datetime2(7) NOT NULL,
        PostedBy nvarchar(100) NOT NULL,
        ProductionPostingLinkId bigint NULL,
        ReversesPostingId bigint NULL,
        ReasonCode nvarchar(40) NULL,
        ReasonText nvarchar(500) NULL,
        SealedAtUtc datetime2(7) NULL,
        CONSTRAINT AK_StockPosting_Tenant_Id UNIQUE (CompanyCode, BranchCode, Id),
        CONSTRAINT FK_StockPosting_Epoch FOREIGN KEY (CompanyCode, BranchCode, LedgerEpochId)
            REFERENCES dbo.StockLedgerEpoch(CompanyCode, BranchCode, Id),
        CONSTRAINT FK_StockPosting_Reverses FOREIGN KEY (CompanyCode, BranchCode, ReversesPostingId)
            REFERENCES dbo.StockPosting(CompanyCode, BranchCode, Id),
        CONSTRAINT CK_StockPosting_Sequence CHECK (PostingSequence > 0),
        CONSTRAINT CK_StockPosting_Revision CHECK (DocumentRevision >= 0),
        CONSTRAINT CK_StockPosting_Hashes CHECK (LEN(RequestFingerprint) = 64 AND LEN(SourceSnapshotHash) = 64),
        CONSTRAINT CK_StockPosting_NoSelfReverse CHECK (ReversesPostingId IS NULL OR ReversesPostingId <> Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockPosting') AND name = N'UQ_StockPosting_BranchSequence')
    CREATE UNIQUE INDEX UQ_StockPosting_BranchSequence ON dbo.StockPosting(CompanyCode, BranchCode, PostingSequence);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockPosting') AND name = N'UQ_StockPosting_Request')
    CREATE UNIQUE INDEX UQ_StockPosting_Request ON dbo.StockPosting(CompanyCode, BranchCode, CommandType, RequestId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockPosting') AND name = N'UQ_StockPosting_SourceRevisionRole')
    CREATE UNIQUE INDEX UQ_StockPosting_SourceRevisionRole
        ON dbo.StockPosting(CompanyCode, BranchCode, SourceModule, SourceDocumentType, SourceDocumentId, DocumentRevision, PostingRole);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockPosting') AND name = N'UQ_StockPosting_Reversal')
    CREATE UNIQUE INDEX UQ_StockPosting_Reversal
        ON dbo.StockPosting(CompanyCode, BranchCode, ReversesPostingId) WHERE ReversesPostingId IS NOT NULL;
GO

CREATE OR ALTER TRIGGER dbo.TR_StockPosting_Seal
ON dbo.StockPosting
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted WHERE SealedAtUtc IS NOT NULL)
        THROW 51000, 'SEALED_STOCK_POSTING_IMMUTABLE', 1;
END;
GO
