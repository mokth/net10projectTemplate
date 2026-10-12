/*
   Report-only preflight for planner allocation readiness. No updates are performed.
   Existing Work Orders are never moved or silently repaired by this script.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.PrMachineScheduleAllocation', N'U') IS NULL
BEGIN
    SELECT N'ALLOCATION_SCHEMA_MISSING' AS Finding, CAST(NULL AS bigint) AS CountValue,
           N'Install scripts/create-machine-schedule-planner.sql first.' AS Detail;
    RETURN;
END;

SELECT N'ACTIVE_CAPACITY_OPERATIONS' AS Finding, COUNT_BIG(*) AS CountValue,
       N'Released/In Progress selected machine operations with a positive planned duration.' AS Detail
FROM dbo.PrWorkOrderOperation o
LEFT JOIN dbo.PrWorkOrderRouteStep s ON s.UID = o.RouteStepID
JOIN dbo.PrWorkOrder w ON w.UID = COALESCE(o.WorkOrderID, s.WorkOrderID)
JOIN dbo.PrWorkOrderMachine m ON m.OperationID = o.UID AND m.IsSelected = 1
WHERE w.Status IN (N'RELEASED', N'IN_PROGRESS')
  AND (m.SetupSeconds + m.ConversionSeconds + m.QueueSeconds + (m.PlannedRunMinutes * 60)) > 0;

SELECT N'UNVERIFIED_OR_MISSING_CURRENT_ALLOCATIONS' AS Finding, COUNT_BIG(*) AS CountValue,
       N'Current selected machine revisions without at least one allocation fragment; writes must remain disabled.' AS Detail
FROM dbo.PrWorkOrderOperation o
LEFT JOIN dbo.PrWorkOrderRouteStep s ON s.UID = o.RouteStepID
JOIN dbo.PrWorkOrder w ON w.UID = COALESCE(o.WorkOrderID, s.WorkOrderID)
JOIN dbo.PrWorkOrderMachine m ON m.OperationID = o.UID AND m.IsSelected = 1
WHERE w.Status IN (N'RELEASED', N'IN_PROGRESS')
  AND (m.SetupSeconds + m.ConversionSeconds + m.QueueSeconds + (m.PlannedRunMinutes * 60)) > 0
  AND NOT EXISTS
  (
      SELECT 1 FROM dbo.PrMachineScheduleAllocation a
      WHERE a.CompanyCode = w.CompanyCode
        AND a.BranchCode = w.BranchCode
        AND a.WorkOrderID = w.UID
        AND a.WorkOrderOperationID = o.UID
        AND a.WorkOrderMachineID = m.UID
        AND a.ScheduleRevision = m.ScheduleRevision
  );

SELECT N'STALE_ACTIVE_ALLOCATION_PROVENANCE' AS Finding, COUNT_BIG(*) AS CountValue,
       N'Active allocation rows with an obsolete revision, wrong machine/operation/order identity, or mismatched calendar hash.' AS Detail
FROM dbo.PrMachineScheduleAllocation a
LEFT JOIN dbo.PrWorkOrder w ON w.UID = a.WorkOrderID
LEFT JOIN dbo.PrWorkOrderMachine m ON m.UID = a.WorkOrderMachineID
LEFT JOIN dbo.PrWorkOrderOperation o ON o.UID = a.WorkOrderOperationID
WHERE w.UID IS NULL
   OR (w.Status IN (N'RELEASED', N'IN_PROGRESS')
       AND (m.UID IS NULL
            OR o.UID IS NULL
            OR m.ScheduleRevision <> a.ScheduleRevision
            OR m.IsSelected <> 1
            OR m.OperationID <> a.WorkOrderOperationID
            OR m.MachineCode <> a.MachineCode
            OR o.WorkOrderID IS NULL
            OR o.WorkOrderID <> a.WorkOrderID
            OR m.ScheduleSourceHash IS NULL
            OR a.ScheduleSourceHash IS NULL
            OR m.ScheduleSourceHash <> a.ScheduleSourceHash));

SELECT N'UNEXPLAINED_ACTIVE_FRAGMENT_OVERLAPS' AS Finding, COUNT_BIG(*) AS CountValue,
       N'Positive half-open interval intersections between current allocations on the same company machine.' AS Detail
FROM dbo.PrMachineScheduleAllocation a
JOIN dbo.PrMachineScheduleAllocation b
  ON b.CompanyCode = a.CompanyCode
 AND b.MachineCode = a.MachineCode
 AND b.UID > a.UID
 AND a.StartDateTime < b.EndDateTime
 AND a.EndDateTime > b.StartDateTime
JOIN dbo.PrWorkOrderMachine ma ON ma.UID = a.WorkOrderMachineID AND ma.ScheduleRevision = a.ScheduleRevision
JOIN dbo.PrWorkOrderMachine mb ON mb.UID = b.WorkOrderMachineID AND mb.ScheduleRevision = b.ScheduleRevision
JOIN dbo.PrWorkOrder wa ON wa.UID = a.WorkOrderID AND wa.Status IN (N'RELEASED', N'IN_PROGRESS')
JOIN dbo.PrWorkOrder wb ON wb.UID = b.WorkOrderID AND wb.Status IN (N'RELEASED', N'IN_PROGRESS');
