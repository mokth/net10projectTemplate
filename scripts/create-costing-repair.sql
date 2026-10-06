/* ============================================================================
   Costing repair case and append-only audit event.
   Manual DBA script. Idempotent. Does not seed data.
   Pair with scripts/init-inv-costing-center-menu.sql for the menu permissions.
   ============================================================================ */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.CostingRepairCase', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CostingRepairCase
    (
        Id                  bigint IDENTITY(1,1) NOT NULL,
        RepairRequestId     uniqueidentifier NOT NULL,
        CompanyCode         nvarchar(5) NOT NULL,
        BranchCode          nvarchar(5) NOT NULL,
        RootFindingCode     nvarchar(20) NULL,
        RootStockPostingId  bigint NULL,
        RootValuationFactId bigint NULL,
        Strategy            nvarchar(40) NOT NULL,
        PreviewHash         nvarchar(128) NOT NULL,
        Status              nvarchar(30) NOT NULL,
        RequestedBy         nvarchar(50) NOT NULL,
        RequestedAtUtc      datetime2 NOT NULL,
        StartedAtUtc        datetime2 NULL,
        CompletedAtUtc      datetime2 NULL,
        Reason              nvarchar(500) NOT NULL,
        RowVersion          rowversion NOT NULL,
        CONSTRAINT PK_CostingRepairCase PRIMARY KEY (Id)
    );

    CREATE UNIQUE INDEX UQ_CostingRepairCase_Request
        ON dbo.CostingRepairCase (CompanyCode, BranchCode, RepairRequestId);
    CREATE INDEX IX_CostingRepairCase_Status
        ON dbo.CostingRepairCase (CompanyCode, BranchCode, Status);
    CREATE INDEX IX_CostingRepairCase_RootPosting
        ON dbo.CostingRepairCase (RootStockPostingId);
END
GO

IF OBJECT_ID(N'dbo.CostingRepairAuditEvent', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CostingRepairAuditEvent
    (
        Id                      bigint IDENTITY(1,1) NOT NULL,
        RepairCaseId            bigint NOT NULL,
        Sequence                int NOT NULL,
        EventType               nvarchar(40) NOT NULL,
        StepNo                  int NOT NULL,
        StableStepId            nvarchar(80) NOT NULL,
        AttemptNo               int NOT NULL CONSTRAINT DF_CostingRepairAuditEvent_Attempt DEFAULT (1),
        Module                  nvarchar(40) NOT NULL,
        DocumentType            nvarchar(40) NOT NULL,
        DocumentNo              nvarchar(40) NOT NULL,
        DesiredAction           nvarchar(20) NULL,
        ModuleRequestId         uniqueidentifier NULL,
        BeforeStatus            nvarchar(20) NULL,
        AfterStatus             nvarchar(20) NULL,
        ResultStockPostingId    bigint NULL,
        DependencyFingerprint   nvarchar(128) NULL,
        PostingWatermark        bigint NULL,
        EvidenceJson            nvarchar(max) NOT NULL,
        CreatedAtUtc            datetime2 NOT NULL,
        CreatedBy               nvarchar(50) NOT NULL,
        CONSTRAINT PK_CostingRepairAuditEvent PRIMARY KEY (Id),
        CONSTRAINT FK_CostingRepairAuditEvent_Case
            FOREIGN KEY (RepairCaseId) REFERENCES dbo.CostingRepairCase (Id)
    );

    CREATE UNIQUE INDEX UQ_CostingRepairAuditEvent_Sequence
        ON dbo.CostingRepairAuditEvent (RepairCaseId, Sequence);
END
GO
