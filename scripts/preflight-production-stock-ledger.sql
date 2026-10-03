/*
  Production stock-ledger cutover preflight (READ ONLY).
  Manual DBA script — do NOT run at app startup.
  Does not modify data. Contains no credentials or connection strings.

  Prints finding-coded result sets. Review every non-empty finding before
  preview-stock-ledger-opening.sql / activate-stock-ledger-epoch.sql.

  Replace @CompanyCode / @BranchCode as needed.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @CompanyCode nvarchar(5) = N'DEMO';
DECLARE @BranchCode  nvarchar(5) = N'HQ';

PRINT N'=== preflight-production-stock-ledger.sql (read-only) ===';
PRINT N'Company=' + @CompanyCode + N' Branch=' + @BranchCode;

/* ── Schema presence ───────────────────────────────────────────────────────── */

SELECT
    FindingCode,
    ObjectName,
    Present,
    Detail
FROM
(
    SELECT N'SCHEMA_TABLE' AS FindingCode, N'dbo.StockLedgerEpoch' AS ObjectName,
           CASE WHEN OBJECT_ID(N'dbo.StockLedgerEpoch', N'U') IS NULL THEN 0 ELSE 1 END AS Present,
           CASE WHEN OBJECT_ID(N'dbo.StockLedgerEpoch', N'U') IS NULL
                THEN N'Missing — run create-stock-posting-ledger.sql'
                ELSE N'Present' END AS Detail
    UNION ALL
    SELECT N'SCHEMA_TABLE', N'dbo.StockPosting',
           CASE WHEN OBJECT_ID(N'dbo.StockPosting', N'U') IS NULL THEN 0 ELSE 1 END,
           CASE WHEN OBJECT_ID(N'dbo.StockPosting', N'U') IS NULL
                THEN N'Missing — run create-stock-posting-ledger.sql'
                ELSE N'Present' END
    UNION ALL
    SELECT N'SCHEMA_TABLE', N'dbo.StockPostingBranchSequence',
           CASE WHEN OBJECT_ID(N'dbo.StockPostingBranchSequence', N'U') IS NULL THEN 0 ELSE 1 END,
           CASE WHEN OBJECT_ID(N'dbo.StockPostingBranchSequence', N'U') IS NULL
                THEN N'Missing — run create-stock-posting-ledger.sql'
                ELSE N'Present' END
    UNION ALL
    SELECT N'SCHEMA_TABLE', N'dbo.PrProductionBalLot',
           CASE WHEN OBJECT_ID(N'dbo.PrProductionBalLot', N'U') IS NULL THEN 0 ELSE 1 END,
           CASE WHEN OBJECT_ID(N'dbo.PrProductionBalLot', N'U') IS NULL
                THEN N'Missing — run create-production-daily-output.sql'
                ELSE N'Present' END
    UNION ALL
    SELECT N'SCHEMA_TABLE', N'dbo.PrProductionBalLotMovement',
           CASE WHEN OBJECT_ID(N'dbo.PrProductionBalLotMovement', N'U') IS NULL THEN 0 ELSE 1 END,
           CASE WHEN OBJECT_ID(N'dbo.PrProductionBalLotMovement', N'U') IS NULL
                THEN N'Missing — run create-production-daily-output.sql'
                ELSE N'Present' END
    UNION ALL
    SELECT N'SCHEMA_TABLE', N'dbo.PrMaterialMovement',
           CASE WHEN OBJECT_ID(N'dbo.PrMaterialMovement', N'U') IS NULL THEN 0 ELSE 1 END,
           CASE WHEN OBJECT_ID(N'dbo.PrMaterialMovement', N'U') IS NULL
                THEN N'Missing — production material facts required'
                ELSE N'Present' END
    UNION ALL
    SELECT N'SCHEMA_TABLE', N'dbo.IvTrxHistory',
           CASE WHEN OBJECT_ID(N'dbo.IvTrxHistory', N'U') IS NULL THEN 0 ELSE 1 END,
           CASE WHEN OBJECT_ID(N'dbo.IvTrxHistory', N'U') IS NULL
                THEN N'Missing — inventory history required'
                ELSE N'Present' END
    UNION ALL
    SELECT N'SCHEMA_V2_COLUMN', N'dbo.PrProductionBalLotMovement.LedgerVersion',
           CASE WHEN COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'LedgerVersion') IS NULL THEN 0 ELSE 1 END,
           CASE WHEN COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'LedgerVersion') IS NULL
                THEN N'Missing — run alter-production-stock-ledger.sql'
                ELSE N'Present' END
    UNION ALL
    SELECT N'SCHEMA_V2_COLUMN', N'dbo.PrProductionBalLotMovement.LedgerEpochID',
           CASE WHEN COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'LedgerEpochID') IS NULL THEN 0 ELSE 1 END,
           CASE WHEN COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'LedgerEpochID') IS NULL
                THEN N'Missing — run alter-production-stock-ledger.sql'
                ELSE N'Present' END
    UNION ALL
    SELECT N'SCHEMA_V2_COLUMN', N'dbo.PrProductionBalLotMovement.StockPostingID',
           CASE WHEN COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'StockPostingID') IS NULL THEN 0 ELSE 1 END,
           CASE WHEN COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'StockPostingID') IS NULL
                THEN N'Missing — run alter-production-stock-ledger.sql'
                ELSE N'Present' END
    UNION ALL
    SELECT N'SCHEMA_V2_COLUMN', N'dbo.IvTrxHistory.LedgerVersion',
           CASE WHEN COL_LENGTH(N'dbo.IvTrxHistory', N'LedgerVersion') IS NULL THEN 0 ELSE 1 END,
           CASE WHEN COL_LENGTH(N'dbo.IvTrxHistory', N'LedgerVersion') IS NULL
                THEN N'Missing — run alter-inventory-history-ledger.sql'
                ELSE N'Present' END
    UNION ALL
    SELECT N'SCHEMA_V2_COLUMN', N'dbo.IvTrxHistory.LedgerEpochID',
           CASE WHEN COL_LENGTH(N'dbo.IvTrxHistory', N'LedgerEpochID') IS NULL THEN 0 ELSE 1 END,
           CASE WHEN COL_LENGTH(N'dbo.IvTrxHistory', N'LedgerEpochID') IS NULL
                THEN N'Missing — run alter-inventory-history-ledger.sql'
                ELSE N'Present' END
    UNION ALL
    SELECT N'SCHEMA_V2_COLUMN', N'dbo.IvTrxHistory.StockPostingID',
           CASE WHEN COL_LENGTH(N'dbo.IvTrxHistory', N'StockPostingID') IS NULL THEN 0 ELSE 1 END,
           CASE WHEN COL_LENGTH(N'dbo.IvTrxHistory', N'StockPostingID') IS NULL
                THEN N'Missing — run alter-inventory-history-ledger.sql'
                ELSE N'Present' END
    UNION ALL
    SELECT N'SCHEMA_V2_COLUMN', N'dbo.IvTrxHistory.EntryRole',
           CASE WHEN COL_LENGTH(N'dbo.IvTrxHistory', N'EntryRole') IS NULL THEN 0 ELSE 1 END,
           CASE WHEN COL_LENGTH(N'dbo.IvTrxHistory', N'EntryRole') IS NULL
                THEN N'Missing — run alter-inventory-history-ledger.sql'
                ELSE N'Present' END
) s
ORDER BY Present, FindingCode, ObjectName;

/* ── Negative / duplicate production balances ─────────────────────────────── */

IF OBJECT_ID(N'dbo.PrProductionBalLot', N'U') IS NULL
    SELECT N'SCHEMA_SKIP' AS FindingCode, N'NEGATIVE_PRODUCTION_BALANCE' AS CheckName,
           N'dbo.PrProductionBalLot missing' AS Detail;
ELSE
    SELECT
        N'NEGATIVE_PRODUCTION_BALANCE' AS FindingCode,
        lot.UID,
        lot.CompanyCode,
        lot.BranchCode,
        lot.Kind,
        lot.ItemCode,
        lot.WorkOrderID,
        lot.WorkOrderNo,
        lot.Qty,
        lot.BaseQty
    FROM dbo.PrProductionBalLot lot
    WHERE lot.CompanyCode = @CompanyCode
      AND lot.BranchCode = @BranchCode
      AND (lot.Qty < 0 OR lot.BaseQty < 0);

IF OBJECT_ID(N'dbo.PrProductionBalLot', N'U') IS NULL
    SELECT N'SCHEMA_SKIP' AS FindingCode, N'DUPLICATE_PRODUCTION_BALANCE' AS CheckName,
           N'dbo.PrProductionBalLot missing' AS Detail;
ELSE
BEGIN
    SELECT
        N'DUPLICATE_MATERIAL_IN' AS FindingCode,
        CompanyCode,
        BranchCode,
        Kind,
        WorkOrderID,
        WorkOrderMaterialID,
        OriginalIssueMovementID,
        COUNT(*) AS FindingRowCount
    FROM dbo.PrProductionBalLot
    WHERE CompanyCode = @CompanyCode
      AND BranchCode = @BranchCode
      AND Kind = N'MATERIAL_IN'
    GROUP BY CompanyCode, BranchCode, Kind, WorkOrderID, WorkOrderMaterialID, OriginalIssueMovementID
    HAVING COUNT(*) > 1;

    SELECT
        N'DUPLICATE_WIP' AS FindingCode,
        CompanyCode,
        BranchCode,
        Kind,
        WorkOrderID,
        ProducingRouteStepID,
        ItemCode,
        LotNo,
        COUNT(*) AS FindingRowCount
    FROM dbo.PrProductionBalLot
    WHERE CompanyCode = @CompanyCode
      AND BranchCode = @BranchCode
      AND Kind = N'WIP'
    GROUP BY CompanyCode, BranchCode, Kind, WorkOrderID, ProducingRouteStepID, ItemCode, LotNo
    HAVING COUNT(*) > 1;
END;

/* ── CONSUME_REVERSAL lineage: must hop CR → CONSUME → ISSUE ─────────────── */

IF OBJECT_ID(N'dbo.PrMaterialMovement', N'U') IS NULL
    SELECT N'SCHEMA_SKIP' AS FindingCode, N'CONSUME_REVERSAL_LINEAGE' AS CheckName,
           N'dbo.PrMaterialMovement missing' AS Detail;
ELSE
BEGIN
    /* Wrong one-hop: reversal.OriginalMovementID points straight at ISSUE. */
    SELECT
        N'CONSUME_REVERSAL_POINTS_AT_ISSUE' AS FindingCode,
        cr.UID AS ConsumeReversalID,
        cr.OriginalMovementID,
        tgt.MovementType AS TargetMovementType,
        cr.WorkOrderID,
        cr.ItemCode,
        cr.BaseQty
    FROM dbo.PrMaterialMovement cr
    INNER JOIN dbo.PrMaterialMovement tgt ON tgt.UID = cr.OriginalMovementID
    WHERE cr.CompanyCode = @CompanyCode
      AND cr.BranchCode = @BranchCode
      AND cr.MovementType = N'CONSUME_REVERSAL'
      AND tgt.MovementType = N'ISSUE';

    SELECT
        N'CONSUME_REVERSAL_ORPHAN' AS FindingCode,
        cr.UID AS ConsumeReversalID,
        cr.OriginalMovementID,
        cr.WorkOrderID,
        cr.ItemCode,
        cr.BaseQty
    FROM dbo.PrMaterialMovement cr
    WHERE cr.CompanyCode = @CompanyCode
      AND cr.BranchCode = @BranchCode
      AND cr.MovementType = N'CONSUME_REVERSAL'
      AND (
            cr.OriginalMovementID IS NULL
            OR NOT EXISTS (
                SELECT 1
                FROM dbo.PrMaterialMovement tgt
                WHERE tgt.UID = cr.OriginalMovementID
            )
          );

    SELECT
        N'CONSUME_REVERSAL_UNEXPECTED_TARGET' AS FindingCode,
        cr.UID AS ConsumeReversalID,
        cr.OriginalMovementID,
        tgt.MovementType AS TargetMovementType,
        cr.WorkOrderID,
        cr.ItemCode,
        cr.BaseQty
    FROM dbo.PrMaterialMovement cr
    INNER JOIN dbo.PrMaterialMovement tgt ON tgt.UID = cr.OriginalMovementID
    WHERE cr.CompanyCode = @CompanyCode
      AND cr.BranchCode = @BranchCode
      AND cr.MovementType = N'CONSUME_REVERSAL'
      AND tgt.MovementType NOT IN (N'CONSUME', N'ISSUE');

    /* CR → CONSUME exists, but that CONSUME does not point at an ISSUE. */
    SELECT
        N'CONSUME_REVERSAL_CONSUME_NOT_ON_ISSUE' AS FindingCode,
        cr.UID AS ConsumeReversalID,
        cr.OriginalMovementID AS ConsumeMovementID,
        c.OriginalMovementID AS ConsumeOriginalMovementID,
        c.MovementType AS ConsumeType,
        src.MovementType AS ConsumeTargetType,
        cr.WorkOrderID,
        cr.ItemCode,
        cr.BaseQty
    FROM dbo.PrMaterialMovement cr
    INNER JOIN dbo.PrMaterialMovement c ON c.UID = cr.OriginalMovementID
    LEFT JOIN dbo.PrMaterialMovement src ON src.UID = c.OriginalMovementID
    WHERE cr.CompanyCode = @CompanyCode
      AND cr.BranchCode = @BranchCode
      AND cr.MovementType = N'CONSUME_REVERSAL'
      AND c.MovementType = N'CONSUME'
      AND (src.UID IS NULL OR src.MovementType <> N'ISSUE');

    SELECT
        N'CONSUME_REVERSAL_TWO_HOP_OK' AS FindingCode,
        COUNT(*) AS FindingRowCount
    FROM dbo.PrMaterialMovement cr
    INNER JOIN dbo.PrMaterialMovement c ON c.UID = cr.OriginalMovementID AND c.MovementType = N'CONSUME'
    INNER JOIN dbo.PrMaterialMovement i ON i.UID = c.OriginalMovementID AND i.MovementType = N'ISSUE'
    WHERE cr.CompanyCode = @CompanyCode
      AND cr.BranchCode = @BranchCode
      AND cr.MovementType = N'CONSUME_REVERSAL';

    SELECT
        N'UNATTRIBUTABLE_RETURN' AS FindingCode,
        m.UID,
        m.OriginalMovementID,
        m.WorkOrderID,
        m.ItemCode,
        m.BaseQty
    FROM dbo.PrMaterialMovement m
    WHERE m.CompanyCode = @CompanyCode
      AND m.BranchCode = @BranchCode
      AND m.MovementType = N'RETURN';

    SELECT
        N'UNATTRIBUTABLE_ADJUST' AS FindingCode,
        m.UID,
        m.OriginalMovementID,
        m.WorkOrderID,
        m.ItemCode,
        m.BaseQty
    FROM dbo.PrMaterialMovement m
    WHERE m.CompanyCode = @CompanyCode
      AND m.BranchCode = @BranchCode
      AND m.MovementType = N'ADJUST';
END;

/* ── Open NEW/POSTED IP and Daily Production documents ────────────────────── */

IF OBJECT_ID(N'dbo.IvTrxBatch', N'U') IS NULL
    SELECT N'SCHEMA_SKIP' AS FindingCode, N'OPEN_IP_DOCUMENT' AS CheckName,
           N'dbo.IvTrxBatch missing' AS Detail;
ELSE
    SELECT
        N'OPEN_IP_DOCUMENT' AS FindingCode,
        b.ID,
        b.CompanyCode,
        b.BranchCode,
        b.BatchNo,
        b.TrxType,
        b.BatchStatus,
        b.TrxDtTime,
        b.RefNo
    FROM dbo.IvTrxBatch b
    WHERE b.CompanyCode = @CompanyCode
      AND b.BranchCode = @BranchCode
      AND b.TrxType = N'IP'
      AND b.BatchStatus IN (N'NEW', N'POSTED')
    ORDER BY b.BatchStatus, b.BatchNo;

IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NULL
    SELECT N'SCHEMA_SKIP' AS FindingCode, N'OPEN_DAILY_PRODUCTION' AS CheckName,
           N'dbo.PrProductionOutput missing' AS Detail;
ELSE
    SELECT
        N'OPEN_DAILY_PRODUCTION' AS FindingCode,
        o.UID,
        o.CompanyCode,
        o.BranchCode,
        o.DocumentNo,
        o.Status,
        o.WorkOrderID,
        o.ProductionDate,
        o.OutputItemCode,
        o.GoodQty,
        o.HoldQty,
        o.RejectQty,
        o.ScrapQty
    FROM dbo.PrProductionOutput o
    WHERE o.CompanyCode = @CompanyCode
      AND o.BranchCode = @BranchCode
      AND o.Status IN (N'NEW', N'POSTED')
    ORDER BY o.Status, o.DocumentNo;

/* ── Existing epoch status ────────────────────────────────────────────────── */

IF OBJECT_ID(N'dbo.StockLedgerEpoch', N'U') IS NULL
    SELECT N'SCHEMA_SKIP' AS FindingCode, N'EPOCH_STATUS' AS CheckName,
           N'dbo.StockLedgerEpoch missing' AS Detail;
ELSE
    SELECT
        N'EPOCH_STATUS' AS FindingCode,
        e.Id,
        e.CompanyCode,
        e.BranchCode,
        e.Status,
        e.Version,
        e.EffectiveFrom,
        e.CutoverPostingSequence,
        e.MigrationBatchId,
        e.ActivatedAtUtc,
        e.ActivatedBy
    FROM dbo.StockLedgerEpoch e
    WHERE e.CompanyCode = @CompanyCode
      AND e.BranchCode = @BranchCode
    ORDER BY e.Id;

/* ── UOM factor <= 0 ──────────────────────────────────────────────────────── */

IF OBJECT_ID(N'dbo.PrProductionBalLot', N'U') IS NULL
    SELECT N'SCHEMA_SKIP' AS FindingCode, N'UOM_FACTOR_LE_ZERO' AS CheckName,
           N'dbo.PrProductionBalLot missing' AS Detail;
ELSE
    SELECT
        N'UOM_FACTOR_LE_ZERO_LOT' AS FindingCode,
        lot.UID,
        lot.Kind,
        lot.ItemCode,
        lot.WorkOrderNo,
        lot.ConversionFactorToBase
    FROM dbo.PrProductionBalLot lot
    WHERE lot.CompanyCode = @CompanyCode
      AND lot.BranchCode = @BranchCode
      AND lot.ConversionFactorToBase <= 0;

IF OBJECT_ID(N'dbo.PrMaterialMovement', N'U') IS NULL
    SELECT N'SCHEMA_SKIP' AS FindingCode, N'UOM_FACTOR_LE_ZERO_MATERIAL' AS CheckName,
           N'dbo.PrMaterialMovement missing' AS Detail;
ELSE
    SELECT
        N'UOM_FACTOR_LE_ZERO_MATERIAL' AS FindingCode,
        m.UID,
        m.MovementType,
        m.ItemCode,
        m.WorkOrderID,
        m.ConversionFactorToBase
    FROM dbo.PrMaterialMovement m
    WHERE m.CompanyCode = @CompanyCode
      AND m.BranchCode = @BranchCode
      AND m.ConversionFactorToBase <= 0;

IF OBJECT_ID(N'dbo.PrProductionBalLotMovement', N'U') IS NULL
    SELECT N'SCHEMA_SKIP' AS FindingCode, N'UOM_FACTOR_LE_ZERO_MOVEMENT' AS CheckName,
           N'dbo.PrProductionBalLotMovement missing' AS Detail;
ELSE IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'ConversionFactorToBase') IS NULL
    SELECT N'SCHEMA_SKIP' AS FindingCode, N'UOM_FACTOR_LE_ZERO_MOVEMENT' AS CheckName,
           N'ConversionFactorToBase column missing' AS Detail;
ELSE
    SELECT
        N'UOM_FACTOR_LE_ZERO_MOVEMENT' AS FindingCode,
        mv.UID,
        mv.ProductionBalLotID,
        mv.MovementType,
        mv.ConversionFactorToBase
    FROM dbo.PrProductionBalLotMovement mv
    INNER JOIN dbo.PrProductionBalLot lot ON lot.UID = mv.ProductionBalLotID
    WHERE lot.CompanyCode = @CompanyCode
      AND lot.BranchCode = @BranchCode
      AND mv.ConversionFactorToBase IS NOT NULL
      AND mv.ConversionFactorToBase <= 0;

PRINT N'preflight-production-stock-ledger.sql complete (read-only).';
