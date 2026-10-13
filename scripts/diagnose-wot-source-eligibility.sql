-- Read-only diagnostic for why a Work Order is absent from the WOT source picker.
-- Run against the same database and tenant data used by the application.
DECLARE @WorkOrderNo nvarchar(50) = N'WO00000002';

SELECT
    w.UID AS WorkOrderId,
    w.CompanyCode,
    w.BranchCode,
    w.Status,
    w.SnapshotRevision,
    CASE WHEN w.SnapshotHash IS NULL THEN NULL ELSE LEN(w.SnapshotHash) END AS SnapshotHashLength,
    w.SnapshotFormatVersion,
    w.SnapshotHashVersion,
    w.IsLegacySnapshot,
    (SELECT COUNT(*)
     FROM dbo.PrProductionBalLot AS p
     WHERE p.WorkOrderId = w.UID AND p.Kind = 'WIP') AS WipLotCount
FROM dbo.PrWorkOrder AS w
WHERE w.WorkOrderNo = @WorkOrderNo;

SELECT
    p.UID AS PoolId,
    p.BalanceStage,
    p.OutputType,
    CASE WHEN p.Qty > 0 AND p.BaseQty > 0 AND p.ConversionFactorToBase > 0 THEN 1 ELSE 0 END AS QuantityShapeOk,
    CASE WHEN NULLIF(LTRIM(RTRIM(p.Uom)), '') IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(p.BaseUom)), '') IS NOT NULL THEN 1 ELSE 0 END AS UomShapeOk,
    CASE WHEN p.StockStatusCode = 'AVAILABLE' AND p.OriginType = 'PRODUCED' THEN 1 ELSE 0 END AS AvailabilityOriginOk,
    CASE WHEN p.WorkOrderMaterialId IS NULL AND p.OriginalIssueMovementId IS NULL THEN 1 ELSE 0 END AS NotMaterialOrIssueOk,
    CASE WHEN p.ProductionLocationId IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(p.LotNo)), '') IS NOT NULL THEN 1 ELSE 0 END AS LocationLotOk,
    CASE WHEN p.PhysicalLotNo IS NULL
          OR NULLIF(LTRIM(RTRIM(p.PhysicalLotNo)), '') IS NULL
          OR p.PhysicalLotNo = p.LotNo THEN 1 ELSE 0 END AS PhysicalLotOk,
    CASE WHEN p.ProducingRouteStepId IS NOT NULL AND p.WorkOrderOperationId IS NOT NULL THEN 1 ELSE 0 END AS ProducerRefsPresent,
    CASE WHEN w.Status IN ('RELEASED', 'IN_PROGRESS', 'COMPLETED')
          AND w.SnapshotRevision > 0
          AND NULLIF(LTRIM(RTRIM(w.SnapshotHash)), '') IS NOT NULL
          AND LEN(w.SnapshotHash) = 64
          AND w.SnapshotFormatVersion >= 2
          AND w.IsLegacySnapshot = 0
          AND w.SnapshotHashVersion >= 3 THEN 1 ELSE 0 END AS WorkOrderOk,
    CASE WHEN EXISTS (
        SELECT 1
        FROM dbo.PrWorkOrderRouteStep AS r
        WHERE r.UID = p.ProducingRouteStepId
          AND r.WorkOrderId = p.WorkOrderId
          AND r.OutputType = 'WIP_STOCKED'
          AND r.OutputItemCode = p.ItemCode
          AND r.OutputUom = p.Uom
          AND r.OutputBaseUom = p.BaseUom
          AND r.OutputConversionFactorToBase = p.ConversionFactorToBase
    ) THEN 1 ELSE 0 END AS RouteContractOk,
    CASE WHEN EXISTS (
        SELECT 1
        FROM dbo.PrWorkOrderOperation AS o
        WHERE o.UID = p.WorkOrderOperationId
          AND o.RouteStepId = p.ProducingRouteStepId
          AND o.IsFinalOperation = 1
    ) THEN 1 ELSE 0 END AS FinalOperationOk,
    CASE WHEN p.ConversionFactorToBase > 0
          AND ROUND(p.BaseQty / p.ConversionFactorToBase, 4) = ROUND(p.Qty, 4) THEN 1 ELSE 0 END AS QtyConversionOk,
    CASE WHEN EXISTS (
        SELECT 1
        FROM dbo.PrWorkOrderTransfer AS t
        WHERE t.CompanyCode = p.CompanyCode
          AND t.BranchCode = p.BranchCode
          AND t.SourceProductionBalLotId = p.UID
          AND t.Status = 'NEW'
          AND t.DeletedAtUtc IS NULL
    ) THEN 1 ELSE 0 END AS ReservedByNewTransfer
FROM dbo.PrProductionBalLot AS p
JOIN dbo.PrWorkOrder AS w ON w.UID = p.WorkOrderId
WHERE w.WorkOrderNo = @WorkOrderNo
  AND p.Kind = 'WIP'
ORDER BY p.UID;
