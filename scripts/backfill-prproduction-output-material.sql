/*
  Path B: reconstruct PrProductionOutputMaterial for Daily Production documents
  that pre-date the consume-variance feature.

  Real-material ConsumeQty = original CONSUME only (do not subtract CONSUME_REVERSAL).
  Handoff ConsumeQty = PrProductionBalLotMovement CONSUME with NULL WorkOrderMaterialID.
  Variance reasons are LEGACY_UNCLASSIFIED only. Idempotent: skips outputs that already have facts.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.PrProductionOutputMaterial', N'U') IS NULL
BEGIN
    PRINT N'PrProductionOutputMaterial is missing — run alter-prproduction-output-material.sql first.';
    RETURN;
END;

INSERT INTO dbo.PrProductionOutputMaterial
(
    CompanyCode, BranchCode, ProductionOutputID, WorkOrderMaterialID, IsHandoff, HandoffFromOperationID,
    ComponentCode, RequiredUOM, SupplySource, IssueMethod, TolerancePercent, WoBomRequiredQty,
    ConversionFactorToBase, BaseUOM, StandardQty, ConsumeQty, VarianceQty,
    VarianceReasonCode, VarianceReasonText, CreatedDate, CreatedBy
)
SELECT
    o.CompanyCode,
    o.BranchCode,
    o.UID,
    m.UID,
    0,
    NULL,
    m.ComponentCode,
    ISNULL(m.RequiredUOM, N''),
    m.SupplySource,
    m.IssueMethod,
    m.Tolerance,
    m.RequiredQty,
    CASE WHEN m.ConversionFactorToBase <= 0 THEN 1 ELSE m.ConversionFactorToBase END,
    m.BaseUOM,
    CASE
        WHEN o.GoodQty + o.ScrapQty + o.RejectQty + o.HoldQty <= 0 OR op.PlannedOutputQty <= 0 THEN 0
        ELSE ROUND(m.RequiredQty * (o.GoodQty + o.ScrapQty + o.RejectQty + o.HoldQty) / op.PlannedOutputQty, 4)
    END,
    CASE
        WHEN o.Status = N'NEW' THEN
            CASE
                WHEN o.GoodQty + o.ScrapQty + o.RejectQty + o.HoldQty <= 0 OR op.PlannedOutputQty <= 0 THEN 0
                ELSE ROUND(m.RequiredQty * (o.GoodQty + o.ScrapQty + o.RejectQty + o.HoldQty) / op.PlannedOutputQty, 4)
            END
        ELSE ISNULL((
            SELECT SUM(mm.Qty)
            FROM dbo.PrMaterialMovement mm
            WHERE mm.ProductionOutputID = o.UID
              AND mm.WorkOrderMaterialID = m.UID
              AND mm.MovementType = N'CONSUME'
        ), 0)
    END,
    0,
    NULL,
    NULL,
    SYSUTCDATETIME(),
    N'SEED'
FROM dbo.PrProductionOutput o
INNER JOIN dbo.PrWorkOrderOperation op ON op.UID = o.WorkOrderOperationID
INNER JOIN dbo.PrWorkOrderMaterial m ON m.WorkOrderOperationID = op.UID
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.PrProductionOutputMaterial existing
    WHERE existing.ProductionOutputID = o.UID);

UPDATE f
SET f.VarianceQty = ROUND(f.ConsumeQty - f.StandardQty, 4),
    f.VarianceReasonCode = CASE
        WHEN ROUND(f.ConsumeQty - f.StandardQty, 4) = 0 THEN NULL
        ELSE N'LEGACY_UNCLASSIFIED'
    END
FROM dbo.PrProductionOutputMaterial f
WHERE f.CreatedBy = N'SEED'
  AND f.IsHandoff = 0
  AND f.VarianceReasonCode IS NULL
  AND ROUND(f.ConsumeQty - f.StandardQty, 4) <> 0;

INSERT INTO dbo.PrProductionOutputMaterial
(
    CompanyCode, BranchCode, ProductionOutputID, WorkOrderMaterialID, IsHandoff, HandoffFromOperationID,
    ComponentCode, RequiredUOM, SupplySource, IssueMethod, TolerancePercent, WoBomRequiredQty,
    ConversionFactorToBase, BaseUOM, StandardQty, ConsumeQty, VarianceQty,
    CreatedDate, CreatedBy
)
SELECT
    o.CompanyCode,
    o.BranchCode,
    o.UID,
    NULL,
    1,
    prior.UID,
    rs.OutputItemCode,
    ISNULL(op.PlannedOutputUOM, ISNULL(rs.OutputUOM, o.OutputUOM)),
    N'PREVIOUS_PROCESS',
    N'HANDOFF',
    0,
    0,
    1,
    ISNULL(op.PlannedOutputUOM, ISNULL(rs.OutputUOM, o.OutputUOM)),
    ROUND(o.GoodQty + o.ScrapQty + o.RejectQty + o.HoldQty, 4),
    ISNULL((
        SELECT SUM(bm.Qty)
        FROM dbo.PrProductionBalLotMovement bm
        WHERE bm.ProductionOutputID = o.UID
          AND bm.MovementType = N'CONSUME'
          AND bm.WorkOrderMaterialID IS NULL
    ), ROUND(o.GoodQty + o.ScrapQty + o.RejectQty + o.HoldQty, 4)),
    0,
    SYSUTCDATETIME(),
    N'SEED'
FROM dbo.PrProductionOutput o
INNER JOIN dbo.PrWorkOrderOperation op ON op.UID = o.WorkOrderOperationID
INNER JOIN dbo.PrWorkOrderRouteStep rs ON rs.UID = op.RouteStepID
INNER JOIN dbo.PrWorkOrderOperation prior
    ON prior.RouteStepID = op.RouteStepID
   AND prior.ProcessSequence < op.ProcessSequence
   AND prior.UID = (
        SELECT TOP (1) p2.UID
        FROM dbo.PrWorkOrderOperation p2
        WHERE p2.RouteStepID = op.RouteStepID
          AND p2.ProcessSequence < op.ProcessSequence
        ORDER BY p2.ProcessSequence DESC, p2.UID DESC)
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.PrProductionOutputMaterial existing
    WHERE existing.ProductionOutputID = o.UID
      AND existing.IsHandoff = 1);

PRINT N'Path B PrProductionOutputMaterial backfill complete.';
GO
