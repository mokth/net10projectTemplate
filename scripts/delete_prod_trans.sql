/*
  Clear work-order / issue-to-production / daily-production transactional data.
  KEEPS: PrBom*, PrDef*, process/machine/workcentre, shifts/calendars, IvStockMaster, etc.
  Restores IvBalLoc qty for deleted IP history.

  sqlcmd -S .\SQLEXPRESS -E -d ERPWeb -I -i clear-production-transactions.sql
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @CompanyCode nvarchar(5) = N'DEMO';  -- NULL = all companies
DECLARE @BranchCode  nvarchar(5) = N'HQ';    -- NULL = all branches
DECLARE @ResetRunningNumbers bit = 1;

BEGIN TRAN;

IF OBJECT_ID(N'dbo.TR_PrProductionBalLotMovement_Seal', N'TR') IS NOT NULL
    DISABLE TRIGGER dbo.TR_PrProductionBalLotMovement_Seal ON dbo.PrProductionBalLotMovement;
IF OBJECT_ID(N'dbo.TR_PrMaterialMovement_Seal', N'TR') IS NOT NULL
    DISABLE TRIGGER dbo.TR_PrMaterialMovement_Seal ON dbo.PrMaterialMovement;
IF OBJECT_ID(N'dbo.TR_PrProductionMovementAllocation_Seal', N'TR') IS NOT NULL
    DISABLE TRIGGER dbo.TR_PrProductionMovementAllocation_Seal ON dbo.PrProductionMovementAllocation;
IF OBJECT_ID(N'dbo.TR_IvTrxHistory_Seal', N'TR') IS NOT NULL
    DISABLE TRIGGER dbo.TR_IvTrxHistory_Seal ON dbo.IvTrxHistory;

/* ?? 0) Break production ? inventory FKs BEFORE deleting IP batches ???????? */
IF OBJECT_ID(N'dbo.PrMaterialIssueLine', N'U') IS NOT NULL
    DELETE lil
    FROM dbo.PrMaterialIssueLine lil
    INNER JOIN dbo.PrProductionPostingLink l ON l.UID = lil.PostingLinkID
    WHERE (@CompanyCode IS NULL OR l.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR l.BranchCode  = @BranchCode);

IF OBJECT_ID(N'dbo.PrMaterialMovement', N'U') IS NOT NULL
BEGIN
    -- clear self-refs first
    UPDATE m SET m.OriginalMovementID = NULL
    FROM dbo.PrMaterialMovement m
    WHERE (@CompanyCode IS NULL OR m.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR m.BranchCode  = @BranchCode);

    IF COL_LENGTH(N'dbo.PrMaterialMovement', N'SourceIssueMovementID') IS NOT NULL
        UPDATE m SET m.SourceIssueMovementID = NULL
        FROM dbo.PrMaterialMovement m
        WHERE (@CompanyCode IS NULL OR m.CompanyCode = @CompanyCode)
          AND (@BranchCode  IS NULL OR m.BranchCode  = @BranchCode);

    IF COL_LENGTH(N'dbo.PrMaterialMovement', N'ReversesMaterialMovementID') IS NOT NULL
        UPDATE m SET m.ReversesMaterialMovementID = NULL
        FROM dbo.PrMaterialMovement m
        WHERE (@CompanyCode IS NULL OR m.CompanyCode = @CompanyCode)
          AND (@BranchCode  IS NULL OR m.BranchCode  = @BranchCode);

    -- drop inventory FKs (needed before IvTrxBatchDetail / IvTrxBatch delete)
    UPDATE m
       SET m.InventoryBatchID = NULL,
           m.InventoryBatchDetailID = NULL,
           m.InventoryHistoryID = NULL
    FROM dbo.PrMaterialMovement m
    WHERE (@CompanyCode IS NULL OR m.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR m.BranchCode  = @BranchCode);
END;

/* ?? 1) Restore IvBalLoc from IP history, then delete IP inventory docs ????? */
IF OBJECT_ID(N'dbo.IvTrxHistory', N'U') IS NOT NULL
BEGIN
    ;WITH IpBatches AS
    (
        SELECT DISTINCT l.CompanyCode, l.BranchCode, l.InventoryBatchNo AS BatchNo
        FROM dbo.PrProductionPostingLink l
        WHERE l.InventoryBatchNo IS NOT NULL
          AND (@CompanyCode IS NULL OR l.CompanyCode = @CompanyCode)
          AND (@BranchCode  IS NULL OR l.BranchCode  = @BranchCode)

        UNION

        SELECT DISTINCT h.CompanyCode, h.BranchCode, h.BatchNo
        FROM dbo.IvTrxHistory h
        WHERE h.TrxType = N'IP'
          AND (@CompanyCode IS NULL OR h.CompanyCode = @CompanyCode)
          AND (@BranchCode  IS NULL OR h.BranchCode  = @BranchCode)

        UNION

        SELECT DISTINCT b.CompanyCode, b.BranchCode, b.BatchNo
        FROM dbo.IvTrxBatch b
        WHERE b.TrxType = N'IP'
          AND (@CompanyCode IS NULL OR b.CompanyCode = @CompanyCode)
          AND (@BranchCode  IS NULL OR b.BranchCode  = @BranchCode)
    ),
    RestoreRows AS
    (
        SELECT
            h.FromBalLocId AS BalLocId,
            SUM(ISNULL(h.FrStdQty, 0)) AS RestoreQty
        FROM dbo.IvTrxHistory h
        INNER JOIN IpBatches i
            ON i.CompanyCode = h.CompanyCode
           AND i.BranchCode  = h.BranchCode
           AND i.BatchNo     = h.BatchNo
        WHERE h.FromBalLocId IS NOT NULL
          AND ISNULL(h.FrStdQty, 0) > 0
          AND (COL_LENGTH(N'dbo.IvTrxHistory', N'LedgerVersion') IS NULL
               OR h.LedgerVersion IS NULL
               OR ISNULL(h.EntryRole, N'ORIGINAL') = N'ORIGINAL')
          AND
          (
              COL_LENGTH(N'dbo.IvTrxHistory', N'ReversesHistoryID') IS NULL
              OR NOT EXISTS
              (
                  SELECT 1
                  FROM dbo.IvTrxHistory r
                  WHERE r.ReversesHistoryID = h.ID
              )
          )
        GROUP BY h.FromBalLocId
    )
    UPDATE b
       SET b.StdQty = b.StdQty + r.RestoreQty,
           b.Updated = SYSUTCDATETIME()
    FROM dbo.IvBalLoc b
    INNER JOIN RestoreRows r ON r.BalLocId = b.ID;

    DELETE d
    FROM dbo.IvTrxBatchDetail d
    INNER JOIN dbo.IvTrxBatch b ON b.ID = d.BatchID
    WHERE b.TrxType = N'IP'
      AND (@CompanyCode IS NULL OR b.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR b.BranchCode  = @BranchCode);

    DELETE h
    FROM dbo.IvTrxHistory h
    WHERE h.TrxType = N'IP'
      AND (@CompanyCode IS NULL OR h.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR h.BranchCode  = @BranchCode);

    DELETE b
    FROM dbo.IvTrxBatch b
    WHERE b.TrxType = N'IP'
      AND (@CompanyCode IS NULL OR b.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR b.BranchCode  = @BranchCode);
END;

/* ?? 2) Production ledger / movements / outputs ???????????????????????????? */
IF OBJECT_ID(N'dbo.PrProductionMovementAllocation', N'U') IS NOT NULL
    DELETE a
    FROM dbo.PrProductionMovementAllocation a
    WHERE (@CompanyCode IS NULL OR a.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR a.BranchCode  = @BranchCode);

IF OBJECT_ID(N'dbo.PrProductionBalLot', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrProductionBalLot', N'OriginalIssueMovementID') IS NOT NULL
    UPDATE lot SET lot.OriginalIssueMovementID = NULL
    FROM dbo.PrProductionBalLot lot
    WHERE (@CompanyCode IS NULL OR lot.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR lot.BranchCode  = @BranchCode);

IF OBJECT_ID(N'dbo.PrMaterialMovement', N'U') IS NOT NULL
BEGIN
    UPDATE m
       SET m.ProductionBalLotID = NULL,
           m.ProductionBalLotMovementID = NULL,
           m.ProductionOutputID = NULL
    FROM dbo.PrMaterialMovement m
    WHERE (@CompanyCode IS NULL OR m.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR m.BranchCode  = @BranchCode);

    DELETE m
    FROM dbo.PrMaterialMovement m
    WHERE (@CompanyCode IS NULL OR m.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR m.BranchCode  = @BranchCode);
END;

IF OBJECT_ID(N'dbo.PrProductionBalLotMovement', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'OriginalMovementID') IS NOT NULL
        UPDATE mv SET mv.OriginalMovementID = NULL
        FROM dbo.PrProductionBalLotMovement mv
        INNER JOIN dbo.PrProductionBalLot lot ON lot.UID = mv.ProductionBalLotID
        WHERE (@CompanyCode IS NULL OR lot.CompanyCode = @CompanyCode)
          AND (@BranchCode  IS NULL OR lot.BranchCode  = @BranchCode);

    DELETE mv
    FROM dbo.PrProductionBalLotMovement mv
    INNER JOIN dbo.PrProductionBalLot lot ON lot.UID = mv.ProductionBalLotID
    WHERE (@CompanyCode IS NULL OR lot.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR lot.BranchCode  = @BranchCode);

    IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'CompanyCode') IS NOT NULL
        DELETE mv
        FROM dbo.PrProductionBalLotMovement mv
        WHERE (@CompanyCode IS NULL OR mv.CompanyCode = @CompanyCode)
          AND (@BranchCode  IS NULL OR mv.BranchCode  = @BranchCode);
END;

IF OBJECT_ID(N'dbo.PrProductionBalLot', N'U') IS NOT NULL
    DELETE lot
    FROM dbo.PrProductionBalLot lot
    WHERE (@CompanyCode IS NULL OR lot.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR lot.BranchCode  = @BranchCode);

IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
    DELETE o
    FROM dbo.PrProductionOutput o
    WHERE (@CompanyCode IS NULL OR o.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR o.BranchCode  = @BranchCode);

IF OBJECT_ID(N'dbo.PrProductionPostingLink', N'U') IS NOT NULL
BEGIN
    UPDATE l SET l.OriginalPostingLinkID = NULL
    FROM dbo.PrProductionPostingLink l
    WHERE (@CompanyCode IS NULL OR l.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR l.BranchCode  = @BranchCode);

    DELETE l
    FROM dbo.PrProductionPostingLink l
    WHERE (@CompanyCode IS NULL OR l.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR l.BranchCode  = @BranchCode);
END;

IF OBJECT_ID(N'dbo.WIPItemBalLoc', N'U') IS NOT NULL
    DELETE FROM dbo.WIPItemBalLoc;

IF OBJECT_ID(N'dbo.PrSchDailyProd', N'U') IS NOT NULL
    DELETE FROM dbo.PrSchDailyProd;

/* ?? 3) Work-order aggregate ??????????????????????????????????????????????? */
IF OBJECT_ID(N'dbo.PrWorkOrderChangeLine', N'U') IS NOT NULL
    DELETE cl
    FROM dbo.PrWorkOrderChangeLine cl
    INNER JOIN dbo.PrWorkOrderChange c ON c.UID = cl.ChangeOrderID
    INNER JOIN dbo.PrWorkOrder wo ON wo.UID = c.WorkOrderID
    WHERE (@CompanyCode IS NULL OR wo.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR wo.BranchCode  = @BranchCode);

IF OBJECT_ID(N'dbo.PrWorkOrderChange', N'U') IS NOT NULL
    DELETE c
    FROM dbo.PrWorkOrderChange c
    INNER JOIN dbo.PrWorkOrder wo ON wo.UID = c.WorkOrderID
    WHERE (@CompanyCode IS NULL OR wo.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR wo.BranchCode  = @BranchCode);

IF OBJECT_ID(N'dbo.PrWorkOrderAudit', N'U') IS NOT NULL
    DELETE a
    FROM dbo.PrWorkOrderAudit a
    INNER JOIN dbo.PrWorkOrder wo ON wo.UID = a.WorkOrderID
    WHERE (@CompanyCode IS NULL OR wo.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR wo.BranchCode  = @BranchCode);

IF OBJECT_ID(N'dbo.PrWorkOrderLabour', N'U') IS NOT NULL
    DELETE lab
    FROM dbo.PrWorkOrderLabour lab
    WHERE EXISTS
    (
        SELECT 1
        FROM dbo.PrWorkOrderOperation op
        INNER JOIN dbo.PrWorkOrder wo ON wo.UID = op.WorkOrderID
        WHERE (lab.OperationID = op.UID
               OR lab.MachineID IN (SELECT m.UID FROM dbo.PrWorkOrderMachine m WHERE m.OperationID = op.UID))
          AND (@CompanyCode IS NULL OR wo.CompanyCode = @CompanyCode)
          AND (@BranchCode  IS NULL OR wo.BranchCode  = @BranchCode)
    );

IF OBJECT_ID(N'dbo.PrWorkOrderResource', N'U') IS NOT NULL
    DELETE r
    FROM dbo.PrWorkOrderResource r
    INNER JOIN dbo.PrWorkOrderOperation op ON op.UID = r.OperationID
    INNER JOIN dbo.PrWorkOrder wo ON wo.UID = op.WorkOrderID
    WHERE (@CompanyCode IS NULL OR wo.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR wo.BranchCode  = @BranchCode);

IF OBJECT_ID(N'dbo.PrWorkOrderMachine', N'U') IS NOT NULL
    DELETE m
    FROM dbo.PrWorkOrderMachine m
    INNER JOIN dbo.PrWorkOrderOperation op ON op.UID = m.OperationID
    INNER JOIN dbo.PrWorkOrder wo ON wo.UID = op.WorkOrderID
    WHERE (@CompanyCode IS NULL OR wo.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR wo.BranchCode  = @BranchCode);

IF OBJECT_ID(N'dbo.PrWorkOrderMaterial', N'U') IS NOT NULL
    DELETE mat
    FROM dbo.PrWorkOrderMaterial mat
    INNER JOIN dbo.PrWorkOrder wo ON wo.UID = mat.WorkOrderID
    WHERE (@CompanyCode IS NULL OR wo.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR wo.BranchCode  = @BranchCode);

IF OBJECT_ID(N'dbo.PrWorkOrderOperation', N'U') IS NOT NULL
    DELETE op
    FROM dbo.PrWorkOrderOperation op
    INNER JOIN dbo.PrWorkOrder wo ON wo.UID = op.WorkOrderID
    WHERE (@CompanyCode IS NULL OR wo.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR wo.BranchCode  = @BranchCode);

IF OBJECT_ID(N'dbo.PrWorkOrderRouteStep', N'U') IS NOT NULL
    DELETE rs
    FROM dbo.PrWorkOrderRouteStep rs
    INNER JOIN dbo.PrWorkOrder wo ON wo.UID = rs.WorkOrderID
    WHERE (@CompanyCode IS NULL OR wo.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR wo.BranchCode  = @BranchCode);

IF OBJECT_ID(N'dbo.PrWorkOrder', N'U') IS NOT NULL
    DELETE wo
    FROM dbo.PrWorkOrder wo
    WHERE (@CompanyCode IS NULL OR wo.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR wo.BranchCode  = @BranchCode);

/* ?? 4) Unused production/test StockPosting rows ??????????????????????????? */
IF OBJECT_ID(N'dbo.StockPosting', N'U') IS NOT NULL
BEGIN
    DELETE p
    FROM dbo.StockPosting p
    WHERE p.SourceModule IN (N'PRODUCTION', N'PR', N'TEST')
      AND (@CompanyCode IS NULL OR p.CompanyCode = @CompanyCode)
      AND (@BranchCode  IS NULL OR p.BranchCode  = @BranchCode)
      AND NOT EXISTS (SELECT 1 FROM dbo.IvTrxHistory h WHERE h.StockPostingID = p.Id)
      AND NOT EXISTS (SELECT 1 FROM dbo.PrProductionBalLotMovement m WHERE m.StockPostingID = p.Id)
      AND NOT EXISTS (SELECT 1 FROM dbo.PrMaterialMovement m WHERE m.StockPostingID = p.Id);
END;

/* ?? 5) Reset WO / Daily Production running numbers ???????????????????????? */
IF @ResetRunningNumbers = 1 AND OBJECT_ID(N'dbo.MsRunningNo', N'U') IS NOT NULL
BEGIN
    UPDATE n
       SET n.LastNo = 0,
           n.Updated = SYSUTCDATETIME(),
           n.UpdatedUID = N'RESET'
    FROM dbo.MsRunningNo n
    WHERE n.DocKey IN (N'PR_WORK_ORDER', N'PR_DAILY_OUTPUT')
      AND (@CompanyCode IS NULL OR n.CompanyCode = @CompanyCode);
END;

IF OBJECT_ID(N'dbo.TR_PrProductionBalLotMovement_Seal', N'TR') IS NOT NULL
    ENABLE TRIGGER dbo.TR_PrProductionBalLotMovement_Seal ON dbo.PrProductionBalLotMovement;
IF OBJECT_ID(N'dbo.TR_PrMaterialMovement_Seal', N'TR') IS NOT NULL
    ENABLE TRIGGER dbo.TR_PrMaterialMovement_Seal ON dbo.PrMaterialMovement;
IF OBJECT_ID(N'dbo.TR_PrProductionMovementAllocation_Seal', N'TR') IS NOT NULL
    ENABLE TRIGGER dbo.TR_PrProductionMovementAllocation_Seal ON dbo.PrProductionMovementAllocation;
IF OBJECT_ID(N'dbo.TR_IvTrxHistory_Seal', N'TR') IS NOT NULL
    ENABLE TRIGGER dbo.TR_IvTrxHistory_Seal ON dbo.IvTrxHistory;

SELECT 'PrWorkOrder' AS T, COUNT(*) AS RowsLeft FROM dbo.PrWorkOrder
WHERE (@CompanyCode IS NULL OR CompanyCode = @CompanyCode) AND (@BranchCode IS NULL OR BranchCode = @BranchCode)
UNION ALL SELECT 'PrProductionOutput', COUNT(*) FROM dbo.PrProductionOutput
WHERE (@CompanyCode IS NULL OR CompanyCode = @CompanyCode) AND (@BranchCode IS NULL OR BranchCode = @BranchCode)
UNION ALL SELECT 'PrMaterialMovement', COUNT(*) FROM dbo.PrMaterialMovement
WHERE (@CompanyCode IS NULL OR CompanyCode = @CompanyCode) AND (@BranchCode IS NULL OR BranchCode = @BranchCode)
UNION ALL SELECT 'PrProductionBalLot', COUNT(*) FROM dbo.PrProductionBalLot
WHERE (@CompanyCode IS NULL OR CompanyCode = @CompanyCode) AND (@BranchCode IS NULL OR BranchCode = @BranchCode)
UNION ALL SELECT 'IvTrxBatch IP', COUNT(*) FROM dbo.IvTrxBatch
WHERE TrxType = N'IP'
  AND (@CompanyCode IS NULL OR CompanyCode = @CompanyCode) AND (@BranchCode IS NULL OR BranchCode = @BranchCode)
UNION ALL SELECT 'PrBomHdr (kept)', COUNT(*) FROM dbo.PrBomHdr
WHERE (@CompanyCode IS NULL OR CompanyCode = @CompanyCode);

COMMIT TRAN;
PRINT N'Production transactional reset complete. Masters/product definitions kept.';