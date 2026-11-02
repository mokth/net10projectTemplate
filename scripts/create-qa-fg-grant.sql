/* Additive, idempotent DBA-run schema for Quality Assurance Phase 2B.
   Finished-goods posting allowance + posted allocation. No inventory/WIP/cost columns.
   PADJ of FG_STAGING is fail-closed in application code when FinalFgInspectionEnabled is on.
   Safe to execute repeatedly. CompanyCode / BranchCode nvarchar(5). */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.QaFgGrant', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaFgGrant
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        CaseId bigint NOT NULL,
        CaseLineId bigint NOT NULL,
        ProductionBalLotId bigint NOT NULL,
        LineKey int NOT NULL,
        WorkOrderNo nvarchar(30) NULL,
        ItemCode nvarchar(30) NOT NULL,
        Uom nvarchar(10) NOT NULL,
        LotNo nvarchar(50) NULL,
        AcceptedQty decimal(18,4) NOT NULL,
        RejectedQty decimal(18,4) NOT NULL CONSTRAINT DF_QaFgGrant_Rejected DEFAULT (0),
        PolicyVersion int NOT NULL CONSTRAINT DF_QaFgGrant_Policy DEFAULT (0),
        Status nvarchar(20) NOT NULL CONSTRAINT DF_QaFgGrant_Status DEFAULT (N'OPEN'),
        ApprovedAtUtc datetime2 NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_QaFgGrant PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaFgGrant_Case FOREIGN KEY (CaseId)
            REFERENCES dbo.QaCase (UID),
        CONSTRAINT FK_QaFgGrant_Line FOREIGN KEY (CaseLineId)
            REFERENCES dbo.QaCaseLine (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaFgGrant_CaseLine' AND object_id = OBJECT_ID(N'dbo.QaFgGrant'))
    CREATE UNIQUE INDEX UX_QaFgGrant_CaseLine
        ON dbo.QaFgGrant (CaseLineId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QaFgGrant_Lot' AND object_id = OBJECT_ID(N'dbo.QaFgGrant'))
    CREATE INDEX IX_QaFgGrant_Lot
        ON dbo.QaFgGrant (CompanyCode, BranchCode, ProductionBalLotId, Status);
GO

IF OBJECT_ID(N'dbo.QaFgAllocation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaFgAllocation
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        GrantId bigint NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        Qty decimal(18,4) NOT NULL,
        Status nvarchar(20) NOT NULL,
        BatchId int NOT NULL,
        SourceId bigint NOT NULL,
        DocumentRevision int NOT NULL CONSTRAINT DF_QaFgAllocation_Rev DEFAULT (0),
        PostingOperationId uniqueidentifier NULL,
        PostedAtUtc datetime2 NOT NULL,
        ReversedAtUtc datetime2 NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_QaFgAllocation PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaFgAllocation_Grant FOREIGN KEY (GrantId)
            REFERENCES dbo.QaFgGrant (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaFgAllocation_Posted_Grant_Source' AND object_id = OBJECT_ID(N'dbo.QaFgAllocation'))
    CREATE UNIQUE INDEX UX_QaFgAllocation_Posted_Grant_Source
        ON dbo.QaFgAllocation (GrantId, SourceId)
        WHERE Status = N'POSTED';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QaFgAllocation_Batch' AND object_id = OBJECT_ID(N'dbo.QaFgAllocation'))
    CREATE INDEX IX_QaFgAllocation_Batch
        ON dbo.QaFgAllocation (CompanyCode, BranchCode, BatchId, Status);
GO

PRINT N'create-qa-fg-grant.sql complete. Finished-goods grants only; flags stay off until an admin enables Finished-goods inspection.';
GO
