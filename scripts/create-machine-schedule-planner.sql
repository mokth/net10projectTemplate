/*
   Machine schedule productive allocation schema.
   This script only installs version columns and storage/indexes. It does not backfill,
   reschedule, or enable planner writes.
*/
IF COL_LENGTH(N'dbo.PrWorkOrder', N'ScheduleRevision') IS NULL
BEGIN
    ALTER TABLE dbo.PrWorkOrder
        ADD ScheduleRevision int NOT NULL
            CONSTRAINT DF_PrWorkOrder_ScheduleRevision DEFAULT (0);
END;

IF COL_LENGTH(N'dbo.PrWorkOrderMachine', N'ScheduleRevision') IS NULL
BEGIN
    ALTER TABLE dbo.PrWorkOrderMachine
        ADD ScheduleRevision int NOT NULL
            CONSTRAINT DF_PrWorkOrderMachine_ScheduleRevision DEFAULT (0);
END;

IF OBJECT_ID(N'dbo.PrMachineScheduleAllocation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrMachineScheduleAllocation
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrMachineScheduleAllocation PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        MachineCode nvarchar(30) NOT NULL,
        WorkOrderID bigint NOT NULL,
        WorkOrderOperationID bigint NOT NULL,
        WorkOrderMachineID bigint NOT NULL,
        ScheduleRevision int NOT NULL CONSTRAINT DF_PrMachineScheduleAllocation_Revision DEFAULT (0),
        FragmentNo int NOT NULL CONSTRAINT DF_PrMachineScheduleAllocation_Fragment DEFAULT (1),
        StartDateTime datetime2 NOT NULL,
        EndDateTime datetime2 NOT NULL,
        ScheduleSourceHash char(64) NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(10) NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_PrMachineScheduleAllocation_PositiveInterval
            CHECK (StartDateTime < EndDateTime)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleAllocation')
               AND name = N'UQ_PrMachineScheduleAllocation_Machine_Revision_Fragment')
    CREATE UNIQUE INDEX UQ_PrMachineScheduleAllocation_Machine_Revision_Fragment
        ON dbo.PrMachineScheduleAllocation(WorkOrderMachineID, ScheduleRevision, FragmentNo);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleAllocation')
               AND name = N'IX_PrMachineScheduleAllocation_Machine_Start_End')
    CREATE INDEX IX_PrMachineScheduleAllocation_Machine_Start_End
        ON dbo.PrMachineScheduleAllocation(CompanyCode, MachineCode, StartDateTime, EndDateTime);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleAllocation')
               AND name = N'IX_PrMachineScheduleAllocation_Order_Operation')
    CREATE INDEX IX_PrMachineScheduleAllocation_Order_Operation
        ON dbo.PrMachineScheduleAllocation(CompanyCode, BranchCode, WorkOrderID, WorkOrderOperationID);
