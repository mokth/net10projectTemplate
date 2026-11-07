/* Additive, idempotent DBA-run schema for Quality Assurance Phase 2A.
   Incoming GRN allowance + posted allocation. No inventory/WIP/cost columns.
   Safe to execute repeatedly. CompanyCode / BranchCode nvarchar(5). */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH(N'dbo.QaCase', N'DeliveryInstanceId') IS NULL
    ALTER TABLE dbo.QaCase ADD DeliveryInstanceId uniqueidentifier NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaCase_DeliveryInstance' AND object_id = OBJECT_ID(N'dbo.QaCase'))
    CREATE UNIQUE INDEX UX_QaCase_DeliveryInstance
        ON dbo.QaCase (CompanyCode, BranchCode, DeliveryInstanceId)
        WHERE DeliveryInstanceId IS NOT NULL;
GO

IF OBJECT_ID(N'dbo.QaReceiptGrant', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaReceiptGrant
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        CaseId bigint NOT NULL,
        CaseLineId bigint NOT NULL,
        DeliveryInstanceId uniqueidentifier NOT NULL,
        LineKey int NOT NULL,
        PoNo nvarchar(30) NULL,
        PoRelNo smallint NULL,
        PoLineNo smallint NULL,
        SupplierCode nvarchar(20) NULL,
        DeliveryNote nvarchar(50) NULL,
        ItemCode nvarchar(30) NOT NULL,
        Uom nvarchar(10) NOT NULL,
        LotNo nvarchar(50) NULL,
        AcceptedQty decimal(18,4) NOT NULL,
        RejectedQty decimal(18,4) NOT NULL CONSTRAINT DF_QaReceiptGrant_Rejected DEFAULT (0),
        PolicyVersion int NOT NULL CONSTRAINT DF_QaReceiptGrant_Policy DEFAULT (0),
        Status nvarchar(20) NOT NULL CONSTRAINT DF_QaReceiptGrant_Status DEFAULT (N'OPEN'),
        ApprovedAtUtc datetime2 NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_QaReceiptGrant PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaReceiptGrant_Case FOREIGN KEY (CaseId)
            REFERENCES dbo.QaCase (UID),
        CONSTRAINT FK_QaReceiptGrant_Line FOREIGN KEY (CaseLineId)
            REFERENCES dbo.QaCaseLine (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaReceiptGrant_CaseLine' AND object_id = OBJECT_ID(N'dbo.QaReceiptGrant'))
    CREATE UNIQUE INDEX UX_QaReceiptGrant_CaseLine
        ON dbo.QaReceiptGrant (CaseLineId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QaReceiptGrant_PoLine' AND object_id = OBJECT_ID(N'dbo.QaReceiptGrant'))
    CREATE INDEX IX_QaReceiptGrant_PoLine
        ON dbo.QaReceiptGrant (CompanyCode, BranchCode, PoNo, PoRelNo, PoLineNo, Status);
GO

IF OBJECT_ID(N'dbo.QaReceiptAllocation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaReceiptAllocation
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        GrantId bigint NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        Qty decimal(18,4) NOT NULL,
        Status nvarchar(20) NOT NULL,
        BatchNo int NOT NULL,
        BatchDetailId int NOT NULL,
        DocumentRevision int NOT NULL CONSTRAINT DF_QaReceiptAllocation_Rev DEFAULT (0),
        PostingOperationId uniqueidentifier NULL,
        PostedAtUtc datetime2 NOT NULL,
        ReversedAtUtc datetime2 NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_QaReceiptAllocation PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaReceiptAllocation_Grant FOREIGN KEY (GrantId)
            REFERENCES dbo.QaReceiptGrant (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaReceiptAllocation_Posted_Grant_Detail' AND object_id = OBJECT_ID(N'dbo.QaReceiptAllocation'))
    CREATE UNIQUE INDEX UX_QaReceiptAllocation_Posted_Grant_Detail
        ON dbo.QaReceiptAllocation (GrantId, BatchDetailId)
        WHERE Status = N'POSTED';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QaReceiptAllocation_Batch' AND object_id = OBJECT_ID(N'dbo.QaReceiptAllocation'))
    CREATE INDEX IX_QaReceiptAllocation_Batch
        ON dbo.QaReceiptAllocation (CompanyCode, BranchCode, BatchNo, Status);
GO

PRINT N'create-qa-receipt-grant.sql complete. Incoming GRN grants only; flags stay off until an admin enables Incoming inspection.';
GO
