/* Additive, idempotent DBA-run schema for Quality Assurance Phase 4.
   Physical hold is a reservation on an existing IvBalLoc slice.
   It does NOT convert IStatus to QCHOLD and does not change qty or cost.
   Safe to execute repeatedly. CompanyCode / BranchCode nvarchar(5).
   Do not enable PhysicalHoldEnabled until outflow guards are live. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.QaPhysicalHold', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaPhysicalHold
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        CaseId bigint NOT NULL,
        CaseLineId bigint NOT NULL,
        BalLocId int NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        WarehouseCode nvarchar(20) NOT NULL,
        LocationCode nvarchar(10) NOT NULL,
        LotNo nvarchar(50) NOT NULL,
        ItemStatus nvarchar(10) NOT NULL,
        ReservedBaseQty decimal(18,4) NOT NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_QaPhysicalHold_Status DEFAULT (N'ACTIVE'),
        Cause nvarchar(30) NOT NULL CONSTRAINT DF_QaPhysicalHold_Cause DEFAULT (N'KEEP_HOLD'),
        CreatedAtUtc datetime2 NOT NULL,
        ReleasedAtUtc datetime2 NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_QaPhysicalHold PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaPhysicalHold_Case FOREIGN KEY (CaseId)
            REFERENCES dbo.QaCase (UID),
        CONSTRAINT FK_QaPhysicalHold_Line FOREIGN KEY (CaseLineId)
            REFERENCES dbo.QaCaseLine (UID)
    );
END
GO

IF OBJECT_ID(N'dbo.QaPhysicalAllocation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QaPhysicalAllocation
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        HoldId bigint NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        Qty decimal(18,4) NOT NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_QaPhysicalAllocation_Status DEFAULT (N'POSTED'),
        TrxType nvarchar(10) NOT NULL,
        BatchNo int NOT NULL,
        BatchDetailId int NOT NULL,
        PostingOperationId uniqueidentifier NULL,
        PostedAtUtc datetime2 NOT NULL,
        ReversedAtUtc datetime2 NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_QaPhysicalAllocation PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_QaPhysicalAllocation_Hold FOREIGN KEY (HoldId)
            REFERENCES dbo.QaPhysicalHold (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaPhysicalHold_CaseLine' AND object_id = OBJECT_ID(N'dbo.QaPhysicalHold'))
    CREATE UNIQUE INDEX UX_QaPhysicalHold_CaseLine
        ON dbo.QaPhysicalHold (CaseLineId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QaPhysicalHold_BalLoc' AND object_id = OBJECT_ID(N'dbo.QaPhysicalHold'))
    CREATE INDEX IX_QaPhysicalHold_BalLoc
        ON dbo.QaPhysicalHold (CompanyCode, BranchCode, BalLocId, Status);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QaPhysicalAllocation_Posted_Hold_Detail' AND object_id = OBJECT_ID(N'dbo.QaPhysicalAllocation'))
    CREATE UNIQUE INDEX UX_QaPhysicalAllocation_Posted_Hold_Detail
        ON dbo.QaPhysicalAllocation (HoldId, BatchDetailId)
        WHERE [Status] = N'POSTED';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QaPhysicalAllocation_Batch' AND object_id = OBJECT_ID(N'dbo.QaPhysicalAllocation'))
    CREATE INDEX IX_QaPhysicalAllocation_Batch
        ON dbo.QaPhysicalAllocation (CompanyCode, BranchCode, BatchNo, Status);
GO

PRINT N'create-qa-physical-hold.sql complete. Physical holds stay off until an admin enables Physical warehouse hold.';
GO
