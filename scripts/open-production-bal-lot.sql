/*
  Opening script: reconstruct MATERIAL_IN ProductionBalLot piles from ISSUE contributions.
  BaseQty-authoritative (plan §11). Fail-closed on unattributable RETURN / ADJUST.

  Manual DBA script — review output before applying. Prefer clearing production execution
  data in non-prod if history cannot be attributed cleanly.

  Prerequisites: create-production-daily-output.sql has been applied.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.PrProductionBalLot', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrMaterialMovement', N'U') IS NULL
BEGIN
    PRINT N'PrProductionBalLot / PrMaterialMovement missing — run create-production-daily-output.sql first.';
    RETURN;
END;

/* Fail-closed diagnostics: any RETURN or ADJUST pointing at an ISSUE blocks opening. */
IF EXISTS (
    SELECT 1
    FROM dbo.PrMaterialMovement m
    WHERE m.MovementType IN (N'RETURN', N'ADJUST')
      AND m.OriginalMovementID IS NOT NULL
)
BEGIN
    SELECT
        m.UID,
        m.CompanyCode,
        m.BranchCode,
        m.WorkOrderID,
        m.MovementType,
        m.OriginalMovementID,
        m.ItemCode,
        m.BaseQty
    FROM dbo.PrMaterialMovement m
    WHERE m.MovementType IN (N'RETURN', N'ADJUST')
      AND m.OriginalMovementID IS NOT NULL
    ORDER BY m.UID;

    RAISERROR(N'Opening stopped: RETURN and/or ADJUST movements exist. Clear execution data or resolve manually.', 16, 1);
    RETURN;
END;

/* Preview remaining BaseQty per ISSUE that has no active MATERIAL_IN pile yet. */
;WITH Issue AS (
    SELECT
        i.UID AS IssueMovementID,
        i.CompanyCode,
        i.BranchCode,
        i.WorkOrderID,
        i.WorkOrderMaterialID,
        i.ItemCode,
        i.Qty AS IssueQty,
        i.Uom,
        i.BaseQty AS IssueBaseQty,
        i.BaseUom,
        i.ConversionFactorToBase,
        i.UnitCost,
        i.TotalCost,
        i.WarehouseCode,
        i.LocationCode,
        i.LotNo,
        i.FromBalLocID,
        i.MovementDate,
        wo.WorkOrderNo,
        mat.ComponentDescription
    FROM dbo.PrMaterialMovement i
    INNER JOIN dbo.PrWorkOrder wo ON wo.UID = i.WorkOrderID
    LEFT JOIN dbo.PrWorkOrderMaterial mat ON mat.UID = i.WorkOrderMaterialID
    WHERE i.MovementType = N'ISSUE'
),
Downstream AS (
    SELECT
        d.OriginalMovementID AS IssueMovementID,
        d.MovementType,
        SUM(d.BaseQty) AS BaseQty
    FROM dbo.PrMaterialMovement d
    WHERE d.OriginalMovementID IS NOT NULL
      AND d.MovementType IN (N'ISSUE_REVERSAL', N'CONSUME', N'CONSUME_REVERSAL')
    GROUP BY d.OriginalMovementID, d.MovementType
),
Pivoted AS (
    SELECT
        i.*,
        ISNULL(rev.BaseQty, 0) AS ReversalBaseQty,
        ISNULL(c.BaseQty, 0) AS ConsumeBaseQty,
        ISNULL(cr.BaseQty, 0) AS ConsumeReversalBaseQty
    FROM Issue i
    LEFT JOIN Downstream rev ON rev.IssueMovementID = i.IssueMovementID AND rev.MovementType = N'ISSUE_REVERSAL'
    LEFT JOIN Downstream c ON c.IssueMovementID = i.IssueMovementID AND c.MovementType = N'CONSUME'
    LEFT JOIN Downstream cr ON cr.IssueMovementID = i.IssueMovementID AND cr.MovementType = N'CONSUME_REVERSAL'
),
Remaining AS (
    SELECT
        *,
        RemainingBaseQty = ROUND(IssueBaseQty - ReversalBaseQty - (ConsumeBaseQty - ConsumeReversalBaseQty), 4)
    FROM Pivoted
)
SELECT
    r.*,
    RemainingQty = CASE
        WHEN ConversionFactorToBase > 0 THEN ROUND(RemainingBaseQty / ConversionFactorToBase, 4)
        ELSE RemainingBaseQty END,
    RemainingTotalCost = ROUND(RemainingBaseQty * UnitCost, 4),
    AlreadyHasPile = CASE WHEN EXISTS (
        SELECT 1 FROM dbo.PrProductionBalLot lot
        WHERE lot.OriginalIssueMovementID = r.IssueMovementID) THEN 1 ELSE 0 END
FROM Remaining r
WHERE RemainingBaseQty > 0
ORDER BY CompanyCode, BranchCode, WorkOrderID, IssueMovementID;

PRINT N'Review the preview. To insert missing MATERIAL_IN piles, uncomment the INSERT block below after validation.';
GO

/*
-- INSERT missing active piles (idempotent on OriginalIssueMovementID unique index).
;WITH Issue AS (
    SELECT
        i.UID AS IssueMovementID,
        i.CompanyCode,
        i.BranchCode,
        i.WorkOrderID,
        i.WorkOrderMaterialID,
        i.ItemCode,
        i.Uom,
        i.BaseQty AS IssueBaseQty,
        i.BaseUom,
        i.ConversionFactorToBase,
        i.UnitCost,
        i.WarehouseCode,
        i.LocationCode,
        i.LotNo,
        i.FromBalLocID,
        i.MovementDate,
        wo.WorkOrderNo,
        mat.ComponentDescription
    FROM dbo.PrMaterialMovement i
    INNER JOIN dbo.PrWorkOrder wo ON wo.UID = i.WorkOrderID
    LEFT JOIN dbo.PrWorkOrderMaterial mat ON mat.UID = i.WorkOrderMaterialID
    WHERE i.MovementType = N'ISSUE'
),
Downstream AS (
    SELECT
        d.OriginalMovementID AS IssueMovementID,
        d.MovementType,
        SUM(d.BaseQty) AS BaseQty
    FROM dbo.PrMaterialMovement d
    WHERE d.OriginalMovementID IS NOT NULL
      AND d.MovementType IN (N'ISSUE_REVERSAL', N'CONSUME', N'CONSUME_REVERSAL')
    GROUP BY d.OriginalMovementID, d.MovementType
),
Remaining AS (
    SELECT
        i.*,
        RemainingBaseQty = ROUND(
            i.IssueBaseQty
            - ISNULL(rev.BaseQty, 0)
            - (ISNULL(c.BaseQty, 0) - ISNULL(cr.BaseQty, 0)), 4)
    FROM Issue i
    LEFT JOIN Downstream rev ON rev.IssueMovementID = i.IssueMovementID AND rev.MovementType = N'ISSUE_REVERSAL'
    LEFT JOIN Downstream c ON c.IssueMovementID = i.IssueMovementID AND c.MovementType = N'CONSUME'
    LEFT JOIN Downstream cr ON cr.IssueMovementID = i.IssueMovementID AND cr.MovementType = N'CONSUME_REVERSAL'
)
INSERT INTO dbo.PrProductionBalLot (
    CompanyCode, BranchCode, Kind, ItemCode, Description,
    Qty, Uom, BaseQty, BaseUom, ConversionFactorToBase,
    TotalCost, AverageUnitCost,
    WorkOrderID, WorkOrderNo, WorkOrderMaterialID, OriginalIssueMovementID, SourceIvBalLocID,
    WarehouseCode, LocationCode, LotNo, LastMovementDate)
SELECT
    r.CompanyCode,
    r.BranchCode,
    N'MATERIAL_IN',
    r.ItemCode,
    r.ComponentDescription,
    CASE WHEN r.ConversionFactorToBase > 0 THEN ROUND(r.RemainingBaseQty / r.ConversionFactorToBase, 4) ELSE r.RemainingBaseQty END,
    r.Uom,
    r.RemainingBaseQty,
    r.BaseUom,
    CASE WHEN r.ConversionFactorToBase > 0 THEN r.ConversionFactorToBase ELSE 1 END,
    ROUND(r.RemainingBaseQty * r.UnitCost, 4),
    CASE WHEN r.RemainingBaseQty > 0 THEN ROUND(r.UnitCost, 4) ELSE 0 END,
    r.WorkOrderID,
    r.WorkOrderNo,
    r.WorkOrderMaterialID,
    r.IssueMovementID,
    r.FromBalLocID,
    ISNULL(r.WarehouseCode, N''),
    ISNULL(r.LocationCode, N''),
    ISNULL(r.LotNo, N''),
    r.MovementDate
FROM Remaining r
WHERE r.RemainingBaseQty > 0
  AND NOT EXISTS (
      SELECT 1 FROM dbo.PrProductionBalLot lot
      WHERE lot.OriginalIssueMovementID = r.IssueMovementID);

-- Seed ISSUE bal-lot movement for each new pile (audit).
INSERT INTO dbo.PrProductionBalLotMovement (
    ProductionBalLotID, MovementType, Qty, Uom, BaseQty, BaseUom,
    UnitCost, TotalCost, WorkOrderID, WorkOrderMaterialID,
    PostingLinkID, DocumentType, DocumentNo, MovementDate, CreatedDate, CreatedBy)
SELECT
    lot.UID,
    N'ISSUE',
    lot.Qty,
    lot.Uom,
    lot.BaseQty,
    lot.BaseUom,
    lot.AverageUnitCost,
    lot.TotalCost,
    lot.WorkOrderID,
    lot.WorkOrderMaterialID,
    ISNULL(iss.PostingLinkID, 0),
    N'MATERIAL_ISSUE',
    ISNULL(CONVERT(nvarchar(50), iss.InventoryBatchNo), N''),
    ISNULL(lot.LastMovementDate, SYSUTCDATETIME()),
    SYSUTCDATETIME(),
    N'OPENING'
FROM dbo.PrProductionBalLot lot
INNER JOIN dbo.PrMaterialMovement iss ON iss.UID = lot.OriginalIssueMovementID
WHERE lot.Kind = N'MATERIAL_IN'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.PrProductionBalLotMovement m
      WHERE m.ProductionBalLotID = lot.UID AND m.MovementType = N'ISSUE');
*/

PRINT N'open-production-bal-lot.sql preview complete.';
GO
