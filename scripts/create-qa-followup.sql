/* Additive, idempotent DBA-run schema for Quality follow-up and reinspection (record-only).
   No startup migration. Safe to execute repeatedly.
   Does NOT mutate inventory, WIP, costing, or posting tables.
   Does NOT add foreign keys into stock-posting documents.
   Backout: stop exposing follow-up UI; do not drop historical action/attempt rows. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.QaCorrectiveAction', N'U') IS NULL
   AND OBJECT_ID(N'dbo.QaCase', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.QaCaseLine', N'U') IS NOT NULL
BEGIN
    CREATE TABLE dbo.QaCorrectiveAction
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CaseId bigint NOT NULL,
        CaseLineId bigint NOT NULL,
        ActionNo int NOT NULL,
        ActionType nvarchar(20) NOT NULL,
        Qty decimal(18,4) NOT NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_QaCorrectiveAction_Status DEFAULT (N'OPEN'),
        AssignedTo nvarchar(20) NULL,
        AssignedDepartment nvarchar(30) NULL,
        Reason nvarchar(250) NULL,
        DueDate datetime2 NULL,
        Remarks nvarchar(500) NULL,
        CompletionEvidence nvarchar(500) NULL,
        RequestId uniqueidentifier NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(20) NOT NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        CompletedAtUtc datetime2 NULL,
        CompletedBy nvarchar(20) NULL,
        CancelledAtUtc datetime2 NULL,
        CancelledBy nvarchar(20) NULL,
        CancelReason nvarchar(250) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_QaCorrectiveAction PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaCorrectiveAction_Case FOREIGN KEY (CaseId)
            REFERENCES dbo.QaCase (UID),
        CONSTRAINT FK_QaCorrectiveAction_Line FOREIGN KEY (CaseLineId)
            REFERENCES dbo.QaCaseLine (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaCorrectiveAction_Case_No' AND object_id = OBJECT_ID(N'dbo.QaCorrectiveAction'))
    CREATE UNIQUE INDEX UX_QaCorrectiveAction_Case_No
        ON dbo.QaCorrectiveAction (CaseId, ActionNo);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaCorrectiveAction_RequestId' AND object_id = OBJECT_ID(N'dbo.QaCorrectiveAction'))
    CREATE UNIQUE INDEX UX_QaCorrectiveAction_RequestId
        ON dbo.QaCorrectiveAction (RequestId)
        WHERE RequestId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QaCorrectiveAction_Case_Status' AND object_id = OBJECT_ID(N'dbo.QaCorrectiveAction'))
    CREATE INDEX IX_QaCorrectiveAction_Case_Status
        ON dbo.QaCorrectiveAction (CaseId, Status);
GO

IF OBJECT_ID(N'dbo.QaInspectionAttempt', N'U') IS NULL
   AND OBJECT_ID(N'dbo.QaCase', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.QaCaseLine', N'U') IS NOT NULL
BEGIN
    CREATE TABLE dbo.QaInspectionAttempt
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CaseId bigint NOT NULL,
        CaseLineId bigint NOT NULL,
        AttemptNo int NOT NULL,
        Result nvarchar(20) NOT NULL,
        Inspector nvarchar(20) NOT NULL,
        InspectedAtUtc datetime2 NOT NULL,
        InspectedQty decimal(18,4) NOT NULL,
        Notes nvarchar(500) NULL,
        MeasuredValues nvarchar(500) NULL,
        DispositionSnapshot nvarchar(500) NULL,
        SourceIdentity nvarchar(200) NULL,
        SourceHeaderId bigint NULL,
        ProductionBalLotId bigint NULL,
        Reason nvarchar(250) NULL,
        CONSTRAINT PK_QaInspectionAttempt PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaInspectionAttempt_Case FOREIGN KEY (CaseId)
            REFERENCES dbo.QaCase (UID),
        CONSTRAINT FK_QaInspectionAttempt_Line FOREIGN KEY (CaseLineId)
            REFERENCES dbo.QaCaseLine (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaInspectionAttempt_Line_No' AND object_id = OBJECT_ID(N'dbo.QaInspectionAttempt'))
    CREATE UNIQUE INDEX UX_QaInspectionAttempt_Line_No
        ON dbo.QaInspectionAttempt (CaseLineId, AttemptNo);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QaInspectionAttempt_Case_At' AND object_id = OBJECT_ID(N'dbo.QaInspectionAttempt'))
    CREATE INDEX IX_QaInspectionAttempt_Case_At
        ON dbo.QaInspectionAttempt (CaseId, InspectedAtUtc);
GO

PRINT N'create-qa-followup.sql complete. Follow-up tasks are QA records only; they do not post stock.';
GO
