/* Additive, idempotent DBA-run schema for Quality Assurance Phase 2C.
   Logical WIP reservation only. Does not change inventory/WIP qty, cost, or status.
   Safe to execute repeatedly. CompanyCode / BranchCode nvarchar(5).
   Do not enable LogicalHoldEnabled until all WIP outflow guards are live. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.QaPolicy', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.QaPolicy', N'LogicalHoldEnabled') IS NULL
    ALTER TABLE dbo.QaPolicy ADD LogicalHoldEnabled bit NOT NULL
        CONSTRAINT DF_QaPolicy_LogicalHold DEFAULT (0);
GO

IF OBJECT_ID(N'dbo.QaLogicalHold', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaLogicalHold
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        CaseId bigint NOT NULL,
        CaseLineId bigint NOT NULL,
        ProductionBalLotId bigint NOT NULL,
        ReservedBaseQty decimal(18,4) NOT NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_QaLogicalHold_Status DEFAULT (N'ACTIVE'),
        Cause nvarchar(30) NOT NULL CONSTRAINT DF_QaLogicalHold_Cause DEFAULT (N'KEEP_HOLD'),
        CreatedAtUtc datetime2 NOT NULL,
        ReleasedAtUtc datetime2 NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_QaLogicalHold PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaLogicalHold_Case FOREIGN KEY (CaseId)
            REFERENCES dbo.QaCase (UID),
        CONSTRAINT FK_QaLogicalHold_Line FOREIGN KEY (CaseLineId)
            REFERENCES dbo.QaCaseLine (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaLogicalHold_CaseLine' AND object_id = OBJECT_ID(N'dbo.QaLogicalHold'))
    CREATE UNIQUE INDEX UX_QaLogicalHold_CaseLine
        ON dbo.QaLogicalHold (CaseLineId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QaLogicalHold_Lot' AND object_id = OBJECT_ID(N'dbo.QaLogicalHold'))
    CREATE INDEX IX_QaLogicalHold_Lot
        ON dbo.QaLogicalHold (CompanyCode, BranchCode, ProductionBalLotId, Status);
GO

PRINT N'create-qa-logical-hold.sql complete. Logical holds stay off until an admin enables Logical WIP hold.';
GO
