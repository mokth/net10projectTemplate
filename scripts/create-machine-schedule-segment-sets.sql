/*
   Phase B additive migration. Run after create-machine-schedule-segments.sql.
   Keeps the legacy allocation and Daily Production segment columns intact.
   Does not enable MACHINE_SCHEDULE_SEGMENT_WRITE_ENABLED.

   SegmentSetID stays nullable during the transitional writer period. Historical rows are
   preserved as one-row historical sets: the old flat schema cannot prove which historical
   segments formed the same plan. Their Reason records that migration finding.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.PrMachineScheduleSegment', N'U') IS NULL
    THROW 51000, 'Deploy create-machine-schedule-segments.sql first.', 1;
IF OBJECT_ID(N'dbo.PrMachineScheduleAllocation', N'U') IS NULL
    THROW 51001, 'PrMachineScheduleAllocation is required.', 1;

IF OBJECT_ID(N'dbo.PrMachineScheduleSegmentSet', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrMachineScheduleSegmentSet
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrMachineScheduleSegmentSet PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        WorkOrderID bigint NOT NULL,
        WorkOrderOperationID bigint NOT NULL,
        WorkOrderMachineID bigint NOT NULL,
        MachineCode nvarchar(30) NOT NULL,
        Mode nvarchar(20) NOT NULL,
        PlanningState nvarchar(30) NOT NULL,
        IsCurrent bit NOT NULL,
        PlannedQtyBasis decimal(18,4) NOT NULL,
        OutputUOM nvarchar(10) NULL,
        CalendarHorizonStart datetime2 NULL,
        CalendarHorizonEnd datetime2 NULL,
        ScheduleSourceHash char(64) NULL,
        MachineScheduleRevision int NOT NULL,
        Reason nvarchar(500) NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        SupersededDate datetime2 NULL,
        SupersededBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_PrMachineScheduleSegmentSet_Mode CHECK (Mode IN ('FULL_PLAN','REMAINING_ONLY')),
        CONSTRAINT CK_PrMachineScheduleSegmentSet_PlanningState CHECK (PlanningState IN
            ('SCHEDULED','HELD_UNSCHEDULED','MAINTENANCE_UNSCHEDULED','REPLAN_REQUIRED','COMPLETED')),
        CONSTRAINT CK_PrMachineScheduleSegmentSet_Qty CHECK (PlannedQtyBasis >= 0),
        CONSTRAINT CK_PrMachineScheduleSegmentSet_Horizon CHECK
            (CalendarHorizonStart IS NULL OR CalendarHorizonEnd IS NULL
             OR CalendarHorizonStart <= CalendarHorizonEnd)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleSegmentSet')
                 AND name = N'UX_PrMachineScheduleSegmentSet_OneCurrent')
    CREATE UNIQUE INDEX UX_PrMachineScheduleSegmentSet_OneCurrent
        ON dbo.PrMachineScheduleSegmentSet(WorkOrderMachineID) WHERE IsCurrent = 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleSegmentSet')
                 AND name = N'IX_PrMachineScheduleSegmentSet_Order_Operation')
    CREATE INDEX IX_PrMachineScheduleSegmentSet_Order_Operation
        ON dbo.PrMachineScheduleSegmentSet(CompanyCode, BranchCode, WorkOrderID, WorkOrderOperationID);

IF COL_LENGTH(N'dbo.PrMachineScheduleSegment', N'SegmentSetID') IS NULL
    ALTER TABLE dbo.PrMachineScheduleSegment ADD SegmentSetID bigint NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleSegment')
                 AND name = N'IX_PrMachineScheduleSegment_Set')
    CREATE INDEX IX_PrMachineScheduleSegment_Set ON dbo.PrMachineScheduleSegment(SegmentSetID);

IF OBJECT_ID(N'dbo.PrMachineScheduleAllocationSegment', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrMachineScheduleAllocationSegment
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrMachineScheduleAllocationSegment PRIMARY KEY,
        AllocationID bigint NOT NULL,
        SegmentID bigint NOT NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleAllocationSegment')
                 AND name = N'UX_PrMachineScheduleAllocationSegment_Allocation')
    CREATE UNIQUE INDEX UX_PrMachineScheduleAllocationSegment_Allocation
        ON dbo.PrMachineScheduleAllocationSegment(AllocationID);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.PrMachineScheduleAllocationSegment')
                 AND name = N'IX_PrMachineScheduleAllocationSegment_Segment')
    CREATE INDEX IX_PrMachineScheduleAllocationSegment_Segment
        ON dbo.PrMachineScheduleAllocationSegment(SegmentID);
GO

BEGIN TRY
BEGIN TRANSACTION;

-- Old code uses SegmentStatus as currentness. Refuse ambiguous current groups instead of
-- assigning a fabricated plan identity. Phase C will switch readers and writers to IsCurrent.
IF EXISTS
(
    SELECT 1 FROM dbo.PrMachineScheduleSegment AS s
    LEFT JOIN dbo.PrWorkOrderMachine AS m ON m.UID = s.WorkOrderMachineID
    WHERE s.SegmentStatus <> N'SUPERSEDED'
      AND (m.UID IS NULL OR m.IsSelected = 0 OR m.MachineCode <> s.MachineCode
           OR m.OperationID <> s.WorkOrderOperationID)
)
    THROW 51007, 'A current flat segment is not attached to its selected machine.', 1;

IF EXISTS
(
    SELECT 1 FROM dbo.PrMachineScheduleSegment AS s
    LEFT JOIN dbo.PrMachineScheduleSegmentSet AS ss ON ss.UID = s.SegmentSetID
    WHERE s.SegmentSetID IS NOT NULL
      AND (ss.UID IS NULL OR ss.WorkOrderMachineID <> s.WorkOrderMachineID
           OR ss.WorkOrderID <> s.WorkOrderID OR ss.WorkOrderOperationID <> s.WorkOrderOperationID
           OR ss.CompanyCode <> s.CompanyCode OR ss.BranchCode <> s.BranchCode
           OR ss.IsCurrent <> CASE WHEN s.SegmentStatus = N'SUPERSEDED' THEN 0 ELSE 1 END)
)
    THROW 51008, 'An existing SegmentSetID disagrees with its segment lifecycle or identity.', 1;

IF EXISTS
(
    SELECT 1
    FROM dbo.PrMachineScheduleSegment AS s
    WHERE s.SegmentStatus <> N'SUPERSEDED'
    GROUP BY s.WorkOrderMachineID
    HAVING MIN(s.CompanyCode) <> MAX(s.CompanyCode)
        OR MIN(s.BranchCode) <> MAX(s.BranchCode)
        OR MIN(s.WorkOrderID) <> MAX(s.WorkOrderID)
        OR MIN(s.WorkOrderOperationID) <> MAX(s.WorkOrderOperationID)
        OR MIN(s.MachineCode) <> MAX(s.MachineCode)
        OR MIN(s.MachineScheduleRevision) <> MAX(s.MachineScheduleRevision)
        OR MIN(COALESCE(s.OutputUOM, N'')) <> MAX(COALESCE(s.OutputUOM, N''))
)
    THROW 51002, 'Current flat segments disagree within a WorkOrderMachine; resolve before migration.', 1;

IF EXISTS
(
    SELECT 1
    FROM dbo.PrMachineScheduleSegment AS s
    INNER JOIN dbo.PrMachineScheduleSegmentSet AS ss ON ss.WorkOrderMachineID = s.WorkOrderMachineID
        AND ss.IsCurrent = 1
    WHERE s.SegmentStatus <> N'SUPERSEDED'
      AND (ss.CompanyCode <> s.CompanyCode OR ss.BranchCode <> s.BranchCode
           OR ss.WorkOrderID <> s.WorkOrderID OR ss.WorkOrderOperationID <> s.WorkOrderOperationID
           OR ss.MachineCode <> s.MachineCode
           OR ss.MachineScheduleRevision <> s.MachineScheduleRevision
           OR (s.SegmentSetID IS NOT NULL AND s.SegmentSetID <> ss.UID))
)
    THROW 51003, 'An existing current SegmentSet conflicts with flat segments.', 1;

;WITH CurrentGroups AS
(
    SELECT s.WorkOrderMachineID, MIN(s.CompanyCode) AS CompanyCode,
           MIN(s.BranchCode) AS BranchCode, MIN(s.WorkOrderID) AS WorkOrderID,
           MIN(s.WorkOrderOperationID) AS WorkOrderOperationID,
           MIN(s.MachineCode) AS MachineCode,
           CASE WHEN MAX(CASE WHEN s.SegmentKind = N'REMAINING_REPLAN' THEN 1 ELSE 0 END) = 1
                THEN N'REMAINING_ONLY' ELSE N'FULL_PLAN' END AS Mode,
           SUM(s.PlannedOutputQty) AS PlannedQtyBasis,
           MIN(s.OutputUOM) AS OutputUOM,
           MIN(s.CalendarHorizonStart) AS CalendarHorizonStart,
           MAX(s.CalendarHorizonEnd) AS CalendarHorizonEnd,
           MIN(s.MachineScheduleRevision) AS MachineScheduleRevision,
           MIN(s.CreatedDate) AS CreatedDate,
           MIN(s.CreatedBy) AS CreatedBy
    FROM dbo.PrMachineScheduleSegment AS s
    WHERE s.SegmentStatus <> N'SUPERSEDED'
    GROUP BY s.WorkOrderMachineID
)
INSERT dbo.PrMachineScheduleSegmentSet
    (CompanyCode, BranchCode, WorkOrderID, WorkOrderOperationID, WorkOrderMachineID,
     MachineCode, Mode, PlanningState, IsCurrent, PlannedQtyBasis, OutputUOM,
     CalendarHorizonStart, CalendarHorizonEnd, ScheduleSourceHash,
     MachineScheduleRevision, Reason, CreatedDate, CreatedBy)
SELECT g.CompanyCode, g.BranchCode, g.WorkOrderID, g.WorkOrderOperationID,
       g.WorkOrderMachineID, g.MachineCode, g.Mode, N'SCHEDULED', 1,
       g.PlannedQtyBasis, g.OutputUOM,
       COALESCE(m.CalendarHorizonStart, g.CalendarHorizonStart),
       COALESCE(m.CalendarHorizonEnd, g.CalendarHorizonEnd),
       m.ScheduleSourceHash, g.MachineScheduleRevision,
       N'Migrated current flat segment collection.', g.CreatedDate, g.CreatedBy
FROM CurrentGroups AS g
LEFT JOIN dbo.PrWorkOrderMachine AS m ON m.UID = g.WorkOrderMachineID
WHERE NOT EXISTS
    (SELECT 1 FROM dbo.PrMachineScheduleSegmentSet AS ss
     WHERE ss.WorkOrderMachineID = g.WorkOrderMachineID AND ss.IsCurrent = 1);

UPDATE s SET SegmentSetID = ss.UID
FROM dbo.PrMachineScheduleSegment AS s
JOIN dbo.PrMachineScheduleSegmentSet AS ss
    ON ss.WorkOrderMachineID = s.WorkOrderMachineID AND ss.IsCurrent = 1
WHERE s.SegmentStatus <> N'SUPERSEDED' AND s.SegmentSetID IS NULL;

IF EXISTS
(
    SELECT 1 FROM dbo.PrMachineScheduleSegment AS s
    LEFT JOIN dbo.PrMachineScheduleSegmentSet AS ss ON ss.UID = s.SegmentSetID
    WHERE s.SegmentStatus <> N'SUPERSEDED' AND (ss.UID IS NULL OR ss.IsCurrent = 0)
)
    THROW 51009, 'A current flat segment was not attached to a current SegmentSet.', 1;

-- Historical grouping is unknowable from flat rows. A one-row set keeps every row and
-- explicitly records the uncertainty rather than combining unrelated plans by revision.
DECLARE @historySegmentID bigint;
DECLARE @historySetID bigint;
DECLARE history_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT s.UID FROM dbo.PrMachineScheduleSegment AS s
    WHERE s.SegmentStatus = N'SUPERSEDED' AND s.SegmentSetID IS NULL
    ORDER BY s.UID;
OPEN history_cursor;
FETCH NEXT FROM history_cursor INTO @historySegmentID;
WHILE @@FETCH_STATUS = 0
BEGIN
    INSERT dbo.PrMachineScheduleSegmentSet
        (CompanyCode, BranchCode, WorkOrderID, WorkOrderOperationID, WorkOrderMachineID,
         MachineCode, Mode, PlanningState, IsCurrent, PlannedQtyBasis, OutputUOM,
         CalendarHorizonStart, CalendarHorizonEnd, ScheduleSourceHash,
         MachineScheduleRevision, Reason, CreatedDate, CreatedBy,
         SupersededDate, SupersededBy)
    SELECT s.CompanyCode, s.BranchCode, s.WorkOrderID, s.WorkOrderOperationID,
           s.WorkOrderMachineID, s.MachineCode,
           CASE WHEN s.SegmentKind = N'REMAINING_REPLAN' THEN N'REMAINING_ONLY' ELSE N'FULL_PLAN' END,
           N'SCHEDULED', 0, s.PlannedOutputQty, s.OutputUOM,
           s.CalendarHorizonStart, s.CalendarHorizonEnd, s.SegmentScheduleSourceHash,
           s.MachineScheduleRevision,
           CONCAT(N'MIGRATION_UNGROUPED_HISTORY: original segment UID ', s.UID),
           s.CreatedDate, s.CreatedBy, s.SupersededDate, s.SupersededBy
    FROM dbo.PrMachineScheduleSegment AS s WHERE s.UID = @historySegmentID;

    SET @historySetID = CONVERT(bigint, SCOPE_IDENTITY());
    UPDATE dbo.PrMachineScheduleSegment SET SegmentSetID = @historySetID
    WHERE UID = @historySegmentID;
    FETCH NEXT FROM history_cursor INTO @historySegmentID;
END;
CLOSE history_cursor;
DEALLOCATE history_cursor;

IF EXISTS
(
    SELECT 1 FROM dbo.PrMachineScheduleAllocation AS a
    JOIN dbo.PrMachineScheduleSegment AS s ON s.UID = a.MachineScheduleSegmentID
    WHERE a.MachineScheduleSegmentID IS NOT NULL
      AND (a.WorkOrderMachineID <> s.WorkOrderMachineID
           OR a.WorkOrderID <> s.WorkOrderID OR a.WorkOrderOperationID <> s.WorkOrderOperationID
           OR a.CompanyCode <> s.CompanyCode OR a.BranchCode <> s.BranchCode
           OR s.SegmentStatus = N'SUPERSEDED')
)
    THROW 51004, 'A legacy allocation points to an inconsistent or historical segment.', 1;

IF EXISTS
(
    SELECT 1 FROM dbo.PrMachineScheduleAllocation AS a
    LEFT JOIN dbo.PrMachineScheduleSegment AS s ON s.UID = a.MachineScheduleSegmentID
    WHERE a.MachineScheduleSegmentID IS NOT NULL AND s.UID IS NULL
)
    THROW 51005, 'A legacy allocation points to a missing segment.', 1;

IF EXISTS
(
    SELECT 1 FROM dbo.PrMachineScheduleAllocationSegment AS l
    JOIN dbo.PrMachineScheduleAllocation AS a ON a.UID = l.AllocationID
    WHERE a.MachineScheduleSegmentID IS NOT NULL AND a.MachineScheduleSegmentID <> l.SegmentID
)
    THROW 51006, 'An existing allocation link disagrees with the legacy segment ID.', 1;

IF EXISTS
(
    SELECT 1 FROM dbo.PrMachineScheduleAllocationSegment AS l
    LEFT JOIN dbo.PrMachineScheduleAllocation AS a ON a.UID = l.AllocationID
    LEFT JOIN dbo.PrMachineScheduleSegment AS s ON s.UID = l.SegmentID
    WHERE a.UID IS NULL OR s.UID IS NULL OR s.SegmentSetID IS NULL
       OR s.SegmentStatus = N'SUPERSEDED'
       OR a.WorkOrderMachineID <> s.WorkOrderMachineID
       OR a.WorkOrderID <> s.WorkOrderID OR a.WorkOrderOperationID <> s.WorkOrderOperationID
       OR a.CompanyCode <> s.CompanyCode OR a.BranchCode <> s.BranchCode
)
    THROW 51010, 'An existing allocation link is orphaned or points outside its machine.', 1;

INSERT dbo.PrMachineScheduleAllocationSegment (AllocationID, SegmentID, CreatedDate, CreatedBy)
SELECT a.UID, a.MachineScheduleSegmentID, a.CreatedDate, a.CreatedBy
FROM dbo.PrMachineScheduleAllocation AS a
WHERE a.MachineScheduleSegmentID IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.PrMachineScheduleAllocationSegment AS l
                  WHERE l.AllocationID = a.UID);

IF EXISTS
(
    SELECT 1 FROM dbo.PrMachineScheduleAllocation AS a
    LEFT JOIN dbo.PrMachineScheduleAllocationSegment AS l ON l.AllocationID = a.UID
    WHERE a.MachineScheduleSegmentID IS NOT NULL
      AND (l.UID IS NULL OR l.SegmentID <> a.MachineScheduleSegmentID)
)
    THROW 51011, 'A legacy segment allocation was not linked exactly once.', 1;

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO

-- These are rollout findings, not inferred business segments. Resolve before the Phase C
-- authority switch or advanced-write enablement.
SELECT s.UID AS UnattachedSegmentID FROM dbo.PrMachineScheduleSegment AS s
WHERE s.SegmentSetID IS NULL;
SELECT a.UID AS UnlinkedAllocationID FROM dbo.PrMachineScheduleAllocation AS a
LEFT JOIN dbo.PrMachineScheduleAllocationSegment AS l ON l.AllocationID = a.UID
WHERE l.UID IS NULL;
SELECT ss.UID AS UngroupedHistoricalSetID, ss.Reason
FROM dbo.PrMachineScheduleSegmentSet AS ss
WHERE ss.Reason LIKE N'MIGRATION_UNGROUPED_HISTORY:%';
