-- SaDocApplication ledger + backfill skip audit (manual DBA script — do NOT run at app startup).
-- Idempotent IF OBJECT_ID. Company/Branch on every row.
GO

IF OBJECT_ID(N'dbo.SaDocApplication', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDocApplication (
        Id bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        SourceDocType nvarchar(10) NOT NULL,
        SourceDocId nvarchar(30) NOT NULL,
        SourceCustRel smallint NOT NULL,
        SourceLineId smallint NOT NULL,
        TargetDocType nvarchar(10) NOT NULL,
        TargetDocId nvarchar(30) NOT NULL,
        TargetCustRel smallint NOT NULL CONSTRAINT DF_SaDocApplication_TargetCustRel DEFAULT (0),
        TargetLineId smallint NOT NULL,
        RelatedSONo nvarchar(30) NOT NULL,
        RelatedCustRel smallint NOT NULL,
        RelatedSOLine smallint NOT NULL,
        AppliedQty decimal(18,4) NOT NULL,
        AppliedAmount decimal(18,2) NULL,
        Created datetime2 NULL,
        CreatedUID nvarchar(20) NULL,
        CONSTRAINT PK_SaDocApplication PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT CK_SaDocApplication_AppliedQty CHECK (AppliedQty > 0)
    );
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_SaDocApplication_SourceTarget' AND object_id = OBJECT_ID(N'dbo.SaDocApplication'))
    CREATE UNIQUE INDEX UQ_SaDocApplication_SourceTarget
    ON dbo.SaDocApplication (
        CompanyCode, BranchCode,
        SourceDocType, SourceDocId, SourceCustRel, SourceLineId,
        TargetDocType, TargetDocId, TargetLineId);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_SaDocApplication_TargetLine' AND object_id = OBJECT_ID(N'dbo.SaDocApplication'))
    CREATE UNIQUE INDEX UQ_SaDocApplication_TargetLine
    ON dbo.SaDocApplication (CompanyCode, BranchCode, TargetDocType, TargetDocId, TargetLineId)
    INCLUDE (AppliedQty, RelatedSONo, RelatedCustRel, RelatedSOLine, SourceDocType, SourceDocId, SourceLineId, TargetCustRel);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_SaDocApplication_Source' AND object_id = OBJECT_ID(N'dbo.SaDocApplication'))
    CREATE INDEX IX_SaDocApplication_Source
    ON dbo.SaDocApplication (CompanyCode, BranchCode, SourceDocType, SourceDocId, SourceCustRel, SourceLineId)
    INCLUDE (AppliedQty, TargetDocType, TargetDocId, TargetLineId);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_SaDocApplication_RelatedSO' AND object_id = OBJECT_ID(N'dbo.SaDocApplication'))
    CREATE INDEX IX_SaDocApplication_RelatedSO
    ON dbo.SaDocApplication (CompanyCode, BranchCode, TargetDocType, RelatedSONo, RelatedCustRel, RelatedSOLine)
    INCLUDE (AppliedQty);
GO

IF OBJECT_ID(N'dbo.SaDocApplicationBackfillSkip', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDocApplicationBackfillSkip (
        Id bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DocType nvarchar(10) NOT NULL,
        DocId nvarchar(30) NOT NULL,
        Line smallint NOT NULL,
        ViolationCode nvarchar(40) NOT NULL,
        Reason nvarchar(500) NOT NULL,
        SourceValues nvarchar(1000) NULL,
        TargetValues nvarchar(1000) NULL,
        CreatedUtc datetime2 NOT NULL CONSTRAINT DF_SaDocApplicationBackfillSkip_CreatedUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_SaDocApplicationBackfillSkip PRIMARY KEY CLUSTERED (Id)
    );
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_SaDocApplicationBackfillSkip_Key' AND object_id = OBJECT_ID(N'dbo.SaDocApplicationBackfillSkip'))
    CREATE UNIQUE INDEX UQ_SaDocApplicationBackfillSkip_Key
    ON dbo.SaDocApplicationBackfillSkip (CompanyCode, BranchCode, DocType, DocId, Line, ViolationCode);
GO

PRINT N'create-sadocapplication.sql completed.';
GO
