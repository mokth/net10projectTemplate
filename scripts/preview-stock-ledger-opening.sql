/*
  Production stock-ledger opening preview (READ ONLY).
  Manual DBA script — do NOT run at app startup.
  Does not write balances, movements, epochs, or postings.
  Contains no credentials or connection strings.

  Reconstructs remaining MATERIAL_IN via ISSUE minus consume/return using
  TWO-HOP consume reversal: CONSUME_REVERSAL → CONSUME → ISSUE.
  Lists remaining WIP/handoff lots from current PrProductionBalLot Qty > 0.
  Flags unmapped location / owner / lot as exceptions.

  Replace @CompanyCode / @BranchCode as needed.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @CompanyCode nvarchar(5) = N'DEMO';
DECLARE @BranchCode  nvarchar(5) = N'HQ';

PRINT N'=== preview-stock-ledger-opening.sql (read-only, no writes) ===';
PRINT N'Company=' + @CompanyCode + N' Branch=' + @BranchCode;

IF OBJECT_ID(N'dbo.PrMaterialMovement', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrProductionBalLot', N'U') IS NULL
BEGIN
    SELECT N'SCHEMA_MISSING' AS FindingCode,
           N'PrMaterialMovement / PrProductionBalLot required' AS Detail;
    RETURN;
END;

/* ── Proposed MATERIAL_IN remaining from two-hop lineage ──────────────────── */

;WITH Issue AS
(
    SELECT
        i.UID AS IssueMovementID,
        i.CompanyCode,
        i.BranchCode,
        i.WorkOrderID,
        i.WorkOrderMaterialID,
        i.ItemCode,
        i.Qty AS IssueQty,
        i.UOM,
        i.BaseQty AS IssueBaseQty,
        i.BaseUOM,
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
    LEFT JOIN dbo.PrWorkOrder wo ON wo.UID = i.WorkOrderID
    LEFT JOIN dbo.PrWorkOrderMaterial mat ON mat.UID = i.WorkOrderMaterialID
    WHERE i.CompanyCode = @CompanyCode
      AND i.BranchCode = @BranchCode
      AND i.MovementType = N'ISSUE'
),
IssueReversal AS
(
    SELECT
        d.OriginalMovementID AS IssueMovementID,
        SUM(d.BaseQty) AS BaseQty
    FROM dbo.PrMaterialMovement d
    WHERE d.CompanyCode = @CompanyCode
      AND d.BranchCode = @BranchCode
      AND d.MovementType = N'ISSUE_REVERSAL'
      AND d.OriginalMovementID IS NOT NULL
    GROUP BY d.OriginalMovementID
),
Consume AS
(
    SELECT
        c.UID AS ConsumeMovementID,
        c.OriginalMovementID AS IssueMovementID,
        c.BaseQty
    FROM dbo.PrMaterialMovement c
    WHERE c.CompanyCode = @CompanyCode
      AND c.BranchCode = @BranchCode
      AND c.MovementType = N'CONSUME'
),
ConsumeByIssue AS
(
    SELECT IssueMovementID, SUM(BaseQty) AS BaseQty
    FROM Consume
    GROUP BY IssueMovementID
),
/* TWO-HOP: CONSUME_REVERSAL.OriginalMovementID → CONSUME → ISSUE.
   Do not join CONSUME_REVERSAL.OriginalMovementID directly to ISSUE. */
ConsumeReversalByIssue AS
(
    SELECT
        c.IssueMovementID,
        SUM(cr.BaseQty) AS BaseQty
    FROM dbo.PrMaterialMovement cr
    INNER JOIN Consume c ON c.ConsumeMovementID = cr.OriginalMovementID
    WHERE cr.CompanyCode = @CompanyCode
      AND cr.BranchCode = @BranchCode
      AND cr.MovementType = N'CONSUME_REVERSAL'
    GROUP BY c.IssueMovementID
),
ReturnByIssue AS
(
    SELECT
        d.OriginalMovementID AS IssueMovementID,
        SUM(d.BaseQty) AS BaseQty
    FROM dbo.PrMaterialMovement d
    WHERE d.CompanyCode = @CompanyCode
      AND d.BranchCode = @BranchCode
      AND d.MovementType = N'RETURN'
      AND d.OriginalMovementID IS NOT NULL
    GROUP BY d.OriginalMovementID
),
AdjustByIssue AS
(
    SELECT
        d.OriginalMovementID AS IssueMovementID,
        SUM(d.BaseQty) AS BaseQty
    FROM dbo.PrMaterialMovement d
    WHERE d.CompanyCode = @CompanyCode
      AND d.BranchCode = @BranchCode
      AND d.MovementType = N'ADJUST'
      AND d.OriginalMovementID IS NOT NULL
    GROUP BY d.OriginalMovementID
),
Proposed AS
(
    SELECT
        i.*,
        ISNULL(rev.BaseQty, 0) AS IssueReversalBaseQty,
        ISNULL(c.BaseQty, 0) AS ConsumeBaseQty,
        ISNULL(cr.BaseQty, 0) AS ConsumeReversalBaseQty,
        ISNULL(ret.BaseQty, 0) AS ReturnBaseQty,
        ISNULL(adj.BaseQty, 0) AS AdjustBaseQty,
        RemainingBaseQty = ROUND(
            i.IssueBaseQty
            - ISNULL(rev.BaseQty, 0)
            - ISNULL(c.BaseQty, 0)
            + ISNULL(cr.BaseQty, 0),
            4)
    FROM Issue i
    LEFT JOIN IssueReversal rev ON rev.IssueMovementID = i.IssueMovementID
    LEFT JOIN ConsumeByIssue c ON c.IssueMovementID = i.IssueMovementID
    LEFT JOIN ConsumeReversalByIssue cr ON cr.IssueMovementID = i.IssueMovementID
    LEFT JOIN ReturnByIssue ret ON ret.IssueMovementID = i.IssueMovementID
    LEFT JOIN AdjustByIssue adj ON adj.IssueMovementID = i.IssueMovementID
)
SELECT
    N'PROPOSED_MATERIAL_IN' AS FindingCode,
    p.IssueMovementID,
    p.CompanyCode,
    p.BranchCode,
    p.WorkOrderID,
    p.WorkOrderNo,
    p.WorkOrderMaterialID,
    p.ItemCode,
    p.IssueBaseQty,
    p.IssueReversalBaseQty,
    p.ConsumeBaseQty,
    p.ConsumeReversalBaseQty,
    p.ReturnBaseQty,
    p.AdjustBaseQty,
    p.RemainingBaseQty,
    RemainingQty = CASE
        WHEN p.ConversionFactorToBase > 0 THEN ROUND(p.RemainingBaseQty / p.ConversionFactorToBase, 4)
        ELSE p.RemainingBaseQty END,
    RemainingTotalCost = ROUND(p.RemainingBaseQty * p.UnitCost, 4),
    p.UOM,
    p.BaseUOM,
    p.ConversionFactorToBase,
    p.WarehouseCode,
    p.LocationCode,
    p.LotNo,
    p.FromBalLocID,
    ExistingLotID = lot.UID,
    ExistingLotQty = lot.Qty,
    ExistingLotBaseQty = lot.BaseQty,
    ExceptionFlags = CONCAT_WS(N',',
        CASE WHEN p.RemainingBaseQty < 0 THEN N'NEGATIVE_REMAINING' END,
        CASE WHEN p.ReturnBaseQty > 0 THEN N'EXCEPTION_RETURN' END,
        CASE WHEN p.AdjustBaseQty > 0 THEN N'EXCEPTION_ADJUST' END,
        CASE WHEN p.ConversionFactorToBase <= 0 THEN N'UOM_FACTOR_LE_ZERO' END,
        CASE WHEN p.WorkOrderID IS NULL OR wo.UID IS NULL THEN N'UNMAPPED_OWNER' END,
        CASE WHEN NULLIF(LTRIM(RTRIM(p.WarehouseCode)), N'') IS NULL
               OR NULLIF(LTRIM(RTRIM(p.LocationCode)), N'') IS NULL
             THEN N'UNMAPPED_LOCATION' END,
        CASE WHEN NULLIF(LTRIM(RTRIM(p.LotNo)), N'') IS NULL THEN N'UNMAPPED_LOT' END,
        CASE WHEN lot.UID IS NULL AND p.RemainingBaseQty > 0 THEN N'NO_LIVE_LOT' END)
FROM Proposed p
LEFT JOIN dbo.PrWorkOrder wo ON wo.UID = p.WorkOrderID
LEFT JOIN dbo.PrProductionBalLot lot
    ON lot.OriginalIssueMovementID = p.IssueMovementID
   AND lot.Kind = N'MATERIAL_IN'
WHERE p.RemainingBaseQty <> 0
   OR p.ReturnBaseQty > 0
   OR p.AdjustBaseQty > 0
ORDER BY p.WorkOrderID, p.IssueMovementID;

/* ── Remaining WIP / handoff lots from live Qty > 0 ───────────────────────── */

SELECT
    N'CURRENT_POSITIVE_LOT' AS FindingCode,
    lot.UID AS ProductionBalLotID,
    lot.CompanyCode,
    lot.BranchCode,
    lot.Kind,
    lot.ItemCode,
    lot.WorkOrderID,
    lot.WorkOrderNo,
    lot.WorkOrderMaterialID,
    lot.WorkOrderOperationID,
    lot.ProducingRouteStepID,
    lot.Qty,
    lot.UOM,
    lot.BaseQty,
    lot.BaseUOM,
    lot.ConversionFactorToBase,
    lot.WarehouseCode,
    lot.LocationCode,
    lot.LotNo,
    lot.OriginalIssueMovementID,
    ExceptionFlags = CONCAT_WS(N',',
        CASE WHEN lot.ConversionFactorToBase <= 0 THEN N'UOM_FACTOR_LE_ZERO' END,
        CASE WHEN wo.UID IS NULL THEN N'UNMAPPED_OWNER' END,
        CASE WHEN NULLIF(LTRIM(RTRIM(lot.WarehouseCode)), N'') IS NULL
               OR NULLIF(LTRIM(RTRIM(lot.LocationCode)), N'') IS NULL
             THEN N'UNMAPPED_LOCATION' END,
        CASE WHEN NULLIF(LTRIM(RTRIM(lot.LotNo)), N'') IS NULL THEN N'UNMAPPED_LOT' END,
        CASE WHEN lot.Kind = N'WIP' AND lot.WorkOrderOperationID IS NULL
              AND lot.ProducingRouteStepID IS NULL
             THEN N'UNMAPPED_OWNER' END)
FROM dbo.PrProductionBalLot lot
LEFT JOIN dbo.PrWorkOrder wo ON wo.UID = lot.WorkOrderID
WHERE lot.CompanyCode = @CompanyCode
  AND lot.BranchCode = @BranchCode
  AND lot.Qty > 0
ORDER BY lot.Kind, lot.WorkOrderID, lot.UID;

IF COL_LENGTH(N'dbo.PrProductionBalLot', N'ProductionLocationID') IS NOT NULL
BEGIN
    DECLARE @V2Preview nvarchar(max) = N'
    SELECT
        N''UNMAPPED_V2_DIMENSION'' AS FindingCode,
        lot.UID AS ProductionBalLotID,
        lot.Kind,
        lot.ItemCode,
        lot.WorkOrderNo,
        lot.ProductionLocationID,
        lot.StockStatusCode,
        lot.PhysicalLotNo,
        lot.PoolCode,
        CONCAT_WS(N'','',
            CASE WHEN lot.ProductionLocationID IS NULL THEN N''UNMAPPED_LOCATION'' END,
            CASE WHEN lot.StockStatusCode IS NULL THEN N''UNMAPPED_STATUS'' END,
            CASE WHEN NULLIF(LTRIM(RTRIM(lot.LotNo)), N'''') IS NULL
                  AND NULLIF(LTRIM(RTRIM(lot.PhysicalLotNo)), N'''') IS NULL
                  AND NULLIF(LTRIM(RTRIM(lot.PoolCode)), N'''') IS NULL
                 THEN N''UNMAPPED_LOT'' END) AS ExceptionFlags
    FROM dbo.PrProductionBalLot lot
    WHERE lot.CompanyCode = @CompanyCode
      AND lot.BranchCode = @BranchCode
      AND lot.Qty > 0
      AND (
            lot.ProductionLocationID IS NULL
            OR lot.StockStatusCode IS NULL
            OR (
                NULLIF(LTRIM(RTRIM(lot.LotNo)), N'''') IS NULL
                AND NULLIF(LTRIM(RTRIM(lot.PhysicalLotNo)), N'''') IS NULL
                AND NULLIF(LTRIM(RTRIM(lot.PoolCode)), N'''') IS NULL
            )
          )
    ORDER BY lot.Kind, lot.UID;';
    EXEC sp_executesql @V2Preview,
        N'@CompanyCode nvarchar(5), @BranchCode nvarchar(5)',
        @CompanyCode = @CompanyCode, @BranchCode = @BranchCode;
END;

/* ── Exception-only extract ───────────────────────────────────────────────── */

SELECT
    N'OPENING_EXCEPTION' AS FindingCode,
    src.SourceKind,
    src.IdentityId,
    src.ItemCode,
    src.WorkOrderID,
    src.ExceptionFlags
FROM
(
    SELECT
        N'MATERIAL_IN_PROPOSED' AS SourceKind,
        p.IssueMovementID AS IdentityId,
        p.ItemCode,
        p.WorkOrderID,
        CONCAT_WS(N',',
            CASE WHEN p.RemainingBaseQty < 0 THEN N'NEGATIVE_REMAINING' END,
            CASE WHEN p.ReturnBaseQty > 0 THEN N'EXCEPTION_RETURN' END,
            CASE WHEN p.AdjustBaseQty > 0 THEN N'EXCEPTION_ADJUST' END,
            CASE WHEN p.ConversionFactorToBase <= 0 THEN N'UOM_FACTOR_LE_ZERO' END,
            CASE WHEN wo.UID IS NULL THEN N'UNMAPPED_OWNER' END,
            CASE WHEN NULLIF(LTRIM(RTRIM(p.WarehouseCode)), N'') IS NULL
                   OR NULLIF(LTRIM(RTRIM(p.LocationCode)), N'') IS NULL
                 THEN N'UNMAPPED_LOCATION' END,
            CASE WHEN NULLIF(LTRIM(RTRIM(p.LotNo)), N'') IS NULL THEN N'UNMAPPED_LOT' END) AS ExceptionFlags
    FROM
    (
        SELECT
            i.UID AS IssueMovementID,
            i.ItemCode,
            i.WorkOrderID,
            i.WarehouseCode,
            i.LocationCode,
            i.LotNo,
            i.ConversionFactorToBase,
            i.BaseQty AS IssueBaseQty,
            RemainingBaseQty = ROUND(
                i.BaseQty
                - ISNULL(rev.BaseQty, 0)
                - ISNULL(csum.BaseQty, 0)
                + ISNULL(crsum.BaseQty, 0), 4),
            ISNULL(ret.BaseQty, 0) AS ReturnBaseQty,
            ISNULL(adj.BaseQty, 0) AS AdjustBaseQty
        FROM dbo.PrMaterialMovement i
        LEFT JOIN (
            SELECT OriginalMovementID, SUM(BaseQty) AS BaseQty
            FROM dbo.PrMaterialMovement
            WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode
              AND MovementType = N'ISSUE_REVERSAL' AND OriginalMovementID IS NOT NULL
            GROUP BY OriginalMovementID
        ) rev ON rev.OriginalMovementID = i.UID
        LEFT JOIN (
            SELECT OriginalMovementID, SUM(BaseQty) AS BaseQty
            FROM dbo.PrMaterialMovement
            WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode
              AND MovementType = N'CONSUME'
            GROUP BY OriginalMovementID
        ) csum ON csum.OriginalMovementID = i.UID
        LEFT JOIN (
            SELECT c.OriginalMovementID, SUM(cr.BaseQty) AS BaseQty
            FROM dbo.PrMaterialMovement cr
            INNER JOIN dbo.PrMaterialMovement c ON c.UID = cr.OriginalMovementID AND c.MovementType = N'CONSUME'
            WHERE cr.CompanyCode = @CompanyCode AND cr.BranchCode = @BranchCode
              AND cr.MovementType = N'CONSUME_REVERSAL'
            GROUP BY c.OriginalMovementID
        ) crsum ON crsum.OriginalMovementID = i.UID
        LEFT JOIN (
            SELECT OriginalMovementID, SUM(BaseQty) AS BaseQty
            FROM dbo.PrMaterialMovement
            WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode
              AND MovementType = N'RETURN' AND OriginalMovementID IS NOT NULL
            GROUP BY OriginalMovementID
        ) ret ON ret.OriginalMovementID = i.UID
        LEFT JOIN (
            SELECT OriginalMovementID, SUM(BaseQty) AS BaseQty
            FROM dbo.PrMaterialMovement
            WHERE CompanyCode = @CompanyCode AND BranchCode = @BranchCode
              AND MovementType = N'ADJUST' AND OriginalMovementID IS NOT NULL
            GROUP BY OriginalMovementID
        ) adj ON adj.OriginalMovementID = i.UID
        WHERE i.CompanyCode = @CompanyCode
          AND i.BranchCode = @BranchCode
          AND i.MovementType = N'ISSUE'
    ) p
    LEFT JOIN dbo.PrWorkOrder wo ON wo.UID = p.WorkOrderID

    UNION ALL

    SELECT
        N'LIVE_POSITIVE_LOT',
        lot.UID,
        lot.ItemCode,
        lot.WorkOrderID,
        CONCAT_WS(N',',
            CASE WHEN lot.ConversionFactorToBase <= 0 THEN N'UOM_FACTOR_LE_ZERO' END,
            CASE WHEN wo.UID IS NULL THEN N'UNMAPPED_OWNER' END,
            CASE WHEN NULLIF(LTRIM(RTRIM(lot.WarehouseCode)), N'') IS NULL
                   OR NULLIF(LTRIM(RTRIM(lot.LocationCode)), N'') IS NULL
                 THEN N'UNMAPPED_LOCATION' END,
            CASE WHEN NULLIF(LTRIM(RTRIM(lot.LotNo)), N'') IS NULL THEN N'UNMAPPED_LOT' END)
    FROM dbo.PrProductionBalLot lot
    LEFT JOIN dbo.PrWorkOrder wo ON wo.UID = lot.WorkOrderID
    WHERE lot.CompanyCode = @CompanyCode
      AND lot.BranchCode = @BranchCode
      AND lot.Qty > 0
) src
WHERE NULLIF(src.ExceptionFlags, N'') IS NOT NULL
ORDER BY src.SourceKind, src.IdentityId;

PRINT N'preview-stock-ledger-opening.sql complete. No rows were written.';
