/* Additive, idempotent DBA-run schema for Quality Assurance Phase 5.
   Non-good Daily Production posting allowance. Does NOT create valued lots
   and does not split absorbed conversion cost.
   Safe to execute repeatedly. CompanyCode / BranchCode nvarchar(5).
   Do not enable NonGoodEnabled until WI POST/rollback claim hooks are live. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.QaPolicy', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.QaPolicy', N'NonGoodEnabled') IS NULL
    ALTER TABLE dbo.QaPolicy ADD NonGoodEnabled bit NOT NULL
        CONSTRAINT DF_QaPolicy_NonGood DEFAULT (0);
GO

IF OBJECT_ID(N'dbo.QaNonGoodGrant', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaNonGoodGrant
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        CaseId bigint NOT NULL,
        CaseLineId bigint NOT NULL,
        ProductionOutputId bigint NOT NULL,
        LineKey int NOT NULL,
        WorkOrderNo nvarchar(30) NULL,
        DocumentNo nvarchar(30) NULL,
        ItemCode nvarchar(30) NOT NULL,
        Uom nvarchar(10) NOT NULL,
        LotNo nvarchar(50) NULL,
        AcceptedQty decimal(18,4) NOT NULL,
        RejectedQty decimal(18,4) NOT NULL CONSTRAINT DF_QaNonGoodGrant_Rejected DEFAULT (0),
        PolicyVersion int NOT NULL CONSTRAINT DF_QaNonGoodGrant_Policy DEFAULT (0),
        Status nvarchar(20) NOT NULL CONSTRAINT DF_QaNonGoodGrant_Status DEFAULT (N'OPEN'),
        ApprovedAtUtc datetime2 NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_QaNonGoodGrant PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaNonGoodGrant_Case FOREIGN KEY (CaseId)
            REFERENCES dbo.QaCase (UID),
        CONSTRAINT FK_QaNonGoodGrant_Line FOREIGN KEY (CaseLineId)
            REFERENCES dbo.QaCaseLine (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaNonGoodGrant_CaseLine' AND object_id = OBJECT_ID(N'dbo.QaNonGoodGrant'))
    CREATE UNIQUE INDEX UX_QaNonGoodGrant_CaseLine
        ON dbo.QaNonGoodGrant (CaseLineId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QaNonGoodGrant_Output' AND object_id = OBJECT_ID(N'dbo.QaNonGoodGrant'))
    CREATE INDEX IX_QaNonGoodGrant_Output
        ON dbo.QaNonGoodGrant (CompanyCode, BranchCode, ProductionOutputId, Status);
GO

IF OBJECT_ID(N'dbo.QaNonGoodAllocation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaNonGoodAllocation
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        GrantId bigint NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        Qty decimal(18,4) NOT NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_QaNonGoodAllocation_Status DEFAULT (N'POSTED'),
        ProductionOutputId bigint NOT NULL,
        DocumentRevision int NOT NULL CONSTRAINT DF_QaNonGoodAllocation_Rev DEFAULT (0),
        PostingOperationId uniqueidentifier NULL,
        PostedAtUtc datetime2 NOT NULL,
        ReversedAtUtc datetime2 NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_QaNonGoodAllocation PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaNonGoodAllocation_Grant FOREIGN KEY (GrantId)
            REFERENCES dbo.QaNonGoodGrant (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaNonGoodAllocation_Posted_Grant_Output' AND object_id = OBJECT_ID(N'dbo.QaNonGoodAllocation'))
    CREATE UNIQUE INDEX UX_QaNonGoodAllocation_Posted_Grant_Output
        ON dbo.QaNonGoodAllocation (GrantId, ProductionOutputId)
        WHERE [Status] = N'POSTED';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QaNonGoodAllocation_Output' AND object_id = OBJECT_ID(N'dbo.QaNonGoodAllocation'))
    CREATE INDEX IX_QaNonGoodAllocation_Output
        ON dbo.QaNonGoodAllocation (CompanyCode, BranchCode, ProductionOutputId, Status);
GO

PRINT N'create-qa-nongood.sql complete. Non-good inspection stays off. Valued lots are not created.';
GO
