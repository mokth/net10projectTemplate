/*
   Advanced machine scheduling schema: business planning segments, schedule holds, and optional
   Daily Production segment trace.
   Idempotent. Does NOT backfill, reschedule, or enable advanced writes.
   Rollback is a separate, reviewed operation; do not drop historical segment rows casually.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.PrMachineScheduleSegment', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrMachineScheduleSegment
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrMachineScheduleSegment PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        WorkOrderID bigint NOT NULL,
        WorkOrderOperationID bigint NOT NULL,
        WorkOrderMachineID bigint NOT NULL,
        MachineCode nvarchar(30) NOT NULL,
        SegmentSequence int NOT NULL,
        SegmentKind nvarchar(30) NOT NULL,
        SegmentStatus nvarchar(20) NOT NULL,
        SourceSegmentID bigint NULL,
        PlannedOutputQty decimal(18,4) NOT NULL CONSTRAINT DF_PrMachineScheduleSegment_OutputQty DEFAULT (0),
        OutputUOM nvarchar(10) NULL,
        PlannedCycleCount decimal(18,4) NOT NULL CONSTRAINT DF_PrMachineScheduleSegment_Cycles DEFAULT (0),
        PlannedCycleSlots decimal(18,4) NOT NULL CONSTRAINT DF_PrMachineScheduleSegment_Slots DEFAULT (0),
        ScheduledRunMinutes decimal(18,4) NOT NULL CONSTRAINT DF_PrMachineScheduleSegment_RunMinutes DEFAULT (0),
        ScheduledSetupSeconds decimal(18,4) NOT NULL CONSTRAINT DF_PrMachineScheduleSegment_Setup DEFAULT (0),
        ScheduledConversionSeconds decimal(18,4) NOT NULL CONSTRAINT DF_PrMachineScheduleSegment_Conversion DEFAULT (0),
        ScheduledQueueSeconds decimal(18,4) NOT NULL CONSTRAINT DF_PrMachineScheduleSegment_Queue DEFAULT (0),
        PlannedStartDateTime datetime2 NOT NULL,
        PlannedCompletionDateTime datetime2 NOT NULL,
        CalendarHorizonStart datetime2 NULL,
        CalendarHorizonEnd datetime2 NULL,
        SegmentScheduleSourceHash char(64) NULL,
        MachineScheduleRevision int NOT NULL CONSTRAINT DF_PrMachineScheduleSegment_Revision DEFAULT (0),
        ReasonCode nvarchar(30) NULL,
        Reason nvarchar(500) NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        SupersededDate datetime2 NULL,
        SupersededBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_PrMachineScheduleSegment_PositiveInterval
            CHECK (PlannedStartDateTime < PlannedCompletionDateTime),
        CONSTRAINT CK_PrMachineScheduleSegment_Sequence CHECK (SegmentSequence > 0),
        CONSTRAINT CK_PrMachineScheduleSegment_Qty CHECK (PlannedOutputQty >= 0),
        CONSTRAINT CK_PrMachineScheduleSegment_Kind CHECK (SegmentKind IN
            ('ORIGINAL','MANUAL_SPLIT','URGENT_INSERT','RESUME','HOLD_RESUME','MAINTENANCE_RESUME','REMAINING_REPLAN')),
        CONSTRAINT CK_PrMachineScheduleSegment_Status CHECK (SegmentStatus IN
            ('PLANNED','EXECUTION_LOCKED','SUPERSEDED'))
    );
END;
GO

-- Deliberately no cascade/restrict FK to PrWorkOrderOperation/PrWorkOrderMachine: Draft
-- Refresh/Change Definition may replace those snapshot child rows, and historical segments must
-- survive. Identity is validated by the scheduling services.

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleSegment')
               AND name = N'IX_PrMachineScheduleSegment_Machine_Range')
    CREATE NONCLUSTERED INDEX IX_PrMachineScheduleSegment_Machine_Range
        ON dbo.PrMachineScheduleSegment(CompanyCode, MachineCode, PlannedStartDateTime, PlannedCompletionDateTime);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleSegment')
               AND name = N'IX_PrMachineScheduleSegment_Order_Operation_Status')
    CREATE NONCLUSTERED INDEX IX_PrMachineScheduleSegment_Order_Operation_Status
        ON dbo.PrMachineScheduleSegment(CompanyCode, BranchCode, WorkOrderID, WorkOrderOperationID, SegmentStatus);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleSegment')
               AND name = N'IX_PrMachineScheduleSegment_Machine_Revision_Sequence')
    CREATE NONCLUSTERED INDEX IX_PrMachineScheduleSegment_Machine_Revision_Sequence
        ON dbo.PrMachineScheduleSegment(WorkOrderMachineID, MachineScheduleRevision, SegmentSequence);
GO

IF COL_LENGTH(N'dbo.PrMachineScheduleAllocation', N'MachineScheduleSegmentID') IS NULL
BEGIN
    ALTER TABLE dbo.PrMachineScheduleAllocation
        ADD MachineScheduleSegmentID bigint NULL;
END;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleAllocation')
               AND name = N'IX_PrMachineScheduleAllocation_Segment')
    CREATE NONCLUSTERED INDEX IX_PrMachineScheduleAllocation_Segment
        ON dbo.PrMachineScheduleAllocation(MachineScheduleSegmentID);
GO

IF COL_LENGTH(N'dbo.PrProductionOutput', N'MachineScheduleSegmentID') IS NULL
BEGIN
    ALTER TABLE dbo.PrProductionOutput
        ADD MachineScheduleSegmentID bigint NULL;
END;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionOutput')
               AND name = N'IX_PrProductionOutput_MachineScheduleSegment')
    CREATE NONCLUSTERED INDEX IX_PrProductionOutput_MachineScheduleSegment
        ON dbo.PrProductionOutput(MachineScheduleSegmentID);
GO

IF OBJECT_ID(N'dbo.PrWorkOrderScheduleHold', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkOrderScheduleHold
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrWorkOrderScheduleHold PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        WorkOrderID bigint NOT NULL,
        WorkOrderOperationID bigint NOT NULL,
        WorkOrderMachineID bigint NOT NULL,
        HoldType nvarchar(30) NOT NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_PrWorkOrderScheduleHold_Status DEFAULT (N'ACTIVE'),
        HoldFrom datetime2 NOT NULL,
        ResumeNotBefore datetime2 NULL,
        Reason nvarchar(500) NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ReleasedDate datetime2 NULL,
        ReleasedBy nvarchar(10) NULL,
        CancelledDate datetime2 NULL,
        CancelledBy nvarchar(10) NULL,
        ResolutionReason nvarchar(500) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_PrWorkOrderScheduleHold_Type CHECK (HoldType IN
            ('MATERIAL_SHORTAGE','QUALITY_HOLD','CUSTOMER_HOLD','OTHER')),
        CONSTRAINT CK_PrWorkOrderScheduleHold_Status CHECK (Status IN
            ('ACTIVE','RELEASED','CANCELLED'))
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderScheduleHold')
               AND name = N'IX_PrWorkOrderScheduleHold_Operation_Status')
    CREATE NONCLUSTERED INDEX IX_PrWorkOrderScheduleHold_Operation_Status
        ON dbo.PrWorkOrderScheduleHold(CompanyCode, BranchCode, WorkOrderOperationID, Status);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderScheduleHold')
               AND name = N'IX_PrWorkOrderScheduleHold_Machine_Status')
    CREATE NONCLUSTERED INDEX IX_PrWorkOrderScheduleHold_Machine_Status
        ON dbo.PrWorkOrderScheduleHold(WorkOrderMachineID, Status);
GO

PRINT N'=== create-machine-schedule-segments complete ===';
