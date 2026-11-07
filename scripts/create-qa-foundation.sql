/* Additive, idempotent DBA-run schema for Quality Assurance Phase 1 (record-only).
   No startup migration. Safe to execute repeatedly.
   QaLogicalHold is in create-qa-logical-hold.sql. QaReceiptGrant is in create-qa-receipt-grant.sql. QaFgGrant is in create-qa-fg-grant.sql.
   Does NOT mutate inventory, WIP, costing, or posting tables.
   CompanyCode / BranchCode are nvarchar(5) to match inventory/production. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.QaPolicy', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaPolicy
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        Version int NOT NULL,
        Enabled bit NOT NULL CONSTRAINT DF_QaPolicy_Enabled DEFAULT (0),
        IncomingInspectionEnabled bit NOT NULL CONSTRAINT DF_QaPolicy_Incoming DEFAULT (0),
        FinalFgInspectionEnabled bit NOT NULL CONSTRAINT DF_QaPolicy_FinalFg DEFAULT (0),
        LogicalHoldEnabled bit NOT NULL CONSTRAINT DF_QaPolicy_LogicalHold DEFAULT (0),
        PhysicalHoldEnabled bit NOT NULL CONSTRAINT DF_QaPolicy_PhysicalHold DEFAULT (0),
        NonGoodEnabled bit NOT NULL CONSTRAINT DF_QaPolicy_NonGood DEFAULT (0),
        InspectorApproverSeparation bit NOT NULL CONSTRAINT DF_QaPolicy_Sep DEFAULT (1),
        IsPublished bit NOT NULL CONSTRAINT DF_QaPolicy_Published DEFAULT (0),
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(20) NOT NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_QaPolicy PRIMARY KEY CLUSTERED (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaPolicy_Company_Version' AND object_id = OBJECT_ID(N'dbo.QaPolicy'))
    CREATE UNIQUE INDEX UX_QaPolicy_Company_Version
        ON dbo.QaPolicy (CompanyCode, Version);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaPolicy_Company_Published' AND object_id = OBJECT_ID(N'dbo.QaPolicy'))
    CREATE UNIQUE INDEX UX_QaPolicy_Company_Published
        ON dbo.QaPolicy (CompanyCode)
        WHERE IsPublished = 1;
GO

IF OBJECT_ID(N'dbo.QaCase', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaCase
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        CaseNo nvarchar(20) NOT NULL,
        CaseDate datetime2 NOT NULL,
        Status nvarchar(20) NOT NULL,
        SourceType nvarchar(40) NOT NULL,
        SourceDocType nvarchar(10) NULL,
        SourceDocNo nvarchar(30) NULL,
        SourceHeaderId bigint NULL,
        SourceDocumentRevision int NULL,
        SupplierCode nvarchar(20) NULL,
        SupplierName nvarchar(100) NULL,
        DeliveryNote nvarchar(50) NULL,
        DeliveryInstanceId uniqueidentifier NULL,
        PoNo nvarchar(30) NULL,
        PoRelNo smallint NULL,
        WorkOrderNo nvarchar(30) NULL,
        ProductionBalLotId bigint NULL,
        SourceHref nvarchar(200) NULL,
        EligibilityNote nvarchar(250) NOT NULL,
        Remarks nvarchar(500) NULL,
        PolicyVersion int NOT NULL CONSTRAINT DF_QaCase_PolicyVersion DEFAULT (0),
        SubmittedBy nvarchar(20) NULL,
        SubmittedAtUtc datetime2 NULL,
        InspectedBy nvarchar(20) NULL,
        InspectedAtUtc datetime2 NULL,
        ReviewedBy nvarchar(20) NULL,
        ReviewedAtUtc datetime2 NULL,
        ReviewDecision nvarchar(20) NULL,
        ReviewRemarks nvarchar(500) NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(20) NOT NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_QaCase PRIMARY KEY CLUSTERED (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaCase_Company_Branch_No' AND object_id = OBJECT_ID(N'dbo.QaCase'))
    CREATE UNIQUE INDEX UX_QaCase_Company_Branch_No
        ON dbo.QaCase (CompanyCode, BranchCode, CaseNo);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QaCase_Workbench' AND object_id = OBJECT_ID(N'dbo.QaCase'))
    CREATE INDEX IX_QaCase_Workbench
        ON dbo.QaCase (CompanyCode, BranchCode, Status, CaseDate);
GO

IF OBJECT_ID(N'dbo.QaCaseLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaCaseLine
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CaseId bigint NOT NULL,
        -- Bracketed: SQL Server (compat 160) treats a bare LineNo as the LINENO keyword.
        [LineNo] int NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        ItemName nvarchar(100) NULL,
        Uom nvarchar(10) NOT NULL,
        Qty decimal(18,4) NOT NULL,
        InspectedQty decimal(18,4) NOT NULL CONSTRAINT DF_QaCaseLine_Inspected DEFAULT (0),
        LotNo nvarchar(50) NULL,
        WarehouseCode nvarchar(10) NULL,
        LocationCode nvarchar(10) NULL,
        ItemStatus nvarchar(10) NULL,
        SourceLineId bigint NULL,
        PoLineNo smallint NULL,
        SourceIdentity nvarchar(200) NULL,
        LineEligibilityNote nvarchar(250) NULL,
        Remarks nvarchar(500) NULL,
        CONSTRAINT PK_QaCaseLine PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaCaseLine_Case FOREIGN KEY (CaseId)
            REFERENCES dbo.QaCase (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaCaseLine_Case_LineNo' AND object_id = OBJECT_ID(N'dbo.QaCaseLine'))
    CREATE UNIQUE INDEX UX_QaCaseLine_Case_LineNo
        ON dbo.QaCaseLine (CaseId, [LineNo]);
GO

IF OBJECT_ID(N'dbo.QaInspection', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaInspection
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CaseId bigint NOT NULL,
        CaseLineId bigint NOT NULL,
        Result nvarchar(20) NOT NULL,
        Inspector nvarchar(20) NOT NULL,
        InspectedAtUtc datetime2 NOT NULL,
        Notes nvarchar(500) NULL,
        MeasuredValues nvarchar(500) NULL,
        CONSTRAINT PK_QaInspection PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaInspection_Case FOREIGN KEY (CaseId)
            REFERENCES dbo.QaCase (UID),
        CONSTRAINT FK_QaInspection_Line FOREIGN KEY (CaseLineId)
            REFERENCES dbo.QaCaseLine (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaInspection_CaseLine' AND object_id = OBJECT_ID(N'dbo.QaInspection'))
    CREATE UNIQUE INDEX UX_QaInspection_CaseLine
        ON dbo.QaInspection (CaseLineId);
GO

IF OBJECT_ID(N'dbo.QaDispositionLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaDispositionLine
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CaseId bigint NOT NULL,
        CaseLineId bigint NOT NULL,
        Kind nvarchar(30) NOT NULL,
        Qty decimal(18,4) NOT NULL,
        ReasonCode nvarchar(30) NULL,
        Remarks nvarchar(500) NULL,
        CONSTRAINT PK_QaDispositionLine PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaDisposition_Case FOREIGN KEY (CaseId)
            REFERENCES dbo.QaCase (UID),
        CONSTRAINT FK_QaDisposition_Line FOREIGN KEY (CaseLineId)
            REFERENCES dbo.QaCaseLine (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaDisposition_Line_Kind' AND object_id = OBJECT_ID(N'dbo.QaDispositionLine'))
    CREATE UNIQUE INDEX UX_QaDisposition_Line_Kind
        ON dbo.QaDispositionLine (CaseLineId, Kind);
GO

IF OBJECT_ID(N'dbo.QaAuditEvent', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaAuditEvent
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CaseId bigint NOT NULL,
        EventType nvarchar(40) NOT NULL,
        AtUtc datetime2 NOT NULL,
        ByUser nvarchar(20) NOT NULL,
        Payload nvarchar(1000) NULL,
        CONSTRAINT PK_QaAuditEvent PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaAuditEvent_Case FOREIGN KEY (CaseId)
            REFERENCES dbo.QaCase (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QaAuditEvent_Case_At' AND object_id = OBJECT_ID(N'dbo.QaAuditEvent'))
    CREATE INDEX IX_QaAuditEvent_Case_At
        ON dbo.QaAuditEvent (CaseId, AtUtc);
GO

IF OBJECT_ID(N'dbo.QaPolicy', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.QaPolicy', N'LogicalHoldEnabled') IS NULL
    ALTER TABLE dbo.QaPolicy ADD LogicalHoldEnabled bit NOT NULL
        CONSTRAINT DF_QaPolicy_LogicalHold DEFAULT (0);
GO

IF OBJECT_ID(N'dbo.QaPolicy', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.QaPolicy', N'NonGoodEnabled') IS NULL
    ALTER TABLE dbo.QaPolicy ADD NonGoodEnabled bit NOT NULL
        CONSTRAINT DF_QaPolicy_NonGood DEFAULT (0);
GO

PRINT N'create-qa-foundation.sql complete. Record-only QA tables only; no stock/WIP/cost columns.';
GO
