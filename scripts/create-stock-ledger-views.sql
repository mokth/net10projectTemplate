/* ============================================================================
   Stock-ledger reporting views (P3 quantity inquiries).
   Manual DBA script — do NOT run at app startup. Rerunnable: CREATE OR ALTER VIEW.
   Target database: the same database as ConnectionStrings:DefaultConnection (ERPWeb).
   Run with:  sqlcmd -E -d ERPWeb -i scripts/create-stock-ledger-views.sql

   SignedBaseQty for production matches ProductionBalLotSignedQty / StockMovementRegistry:
     ISSUE / PRODUCE / CONSUME_REVERSAL / RETURN_REVERSAL / OPENING_IN /
     TRANSFER_IN / STATUS_IN / ADJUST_IN  = +BaseQty
     ISSUE_REVERSAL / PRODUCE_REVERSAL / CONSUME / RETURN /
     TRANSFER_OUT / STATUS_OUT / ADJUST_OUT / SCRAP_OUT / FG_RECEIPT_OUT = -BaseQty
   Inventory FROM/TO legs: -FrStdQty / +ToStdQty. Zero or null magnitudes yield no leg.
   ============================================================================ */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER VIEW dbo.vw_PrTrxHistory
AS
SELECT
    m.CompanyCode,
    m.BranchCode,
    m.LedgerEpochID AS LedgerEpochId,
    m.StockPostingID AS StockPostingId,
    p.PostingSequence,
    m.PostingLineNo,
    p.EffectiveAt,
    p.PostedAtUtc,
    p.SourceDocumentType,
    p.SourceDocumentNo,
    m.ItemCode,
    m.BaseUOM AS BaseUom,
    m.BalanceStage,
    m.WorkOrderNo,
    m.ProcessCode,
    m.ProductionLocationCode,
    m.LotIdentity,
    m.StockStatusCode,
    m.MovementType,
    m.Qty,
    m.BaseQty,
    CAST(
        CASE m.MovementType
            WHEN N'ISSUE' THEN m.BaseQty
            WHEN N'PRODUCE' THEN m.BaseQty
            WHEN N'CONSUME_REVERSAL' THEN m.BaseQty
            WHEN N'RETURN_REVERSAL' THEN m.BaseQty
            WHEN N'FG_RECEIPT_REVERSAL' THEN m.BaseQty
            WHEN N'OPENING_IN' THEN m.BaseQty
            WHEN N'TRANSFER_IN' THEN m.BaseQty
            WHEN N'STATUS_IN' THEN m.BaseQty
            WHEN N'ADJUST_IN' THEN m.BaseQty
            WHEN N'ISSUE_REVERSAL' THEN -m.BaseQty
            WHEN N'PRODUCE_REVERSAL' THEN -m.BaseQty
            WHEN N'CONSUME' THEN -m.BaseQty
            WHEN N'RETURN' THEN -m.BaseQty
            WHEN N'TRANSFER_OUT' THEN -m.BaseQty
            WHEN N'STATUS_OUT' THEN -m.BaseQty
            WHEN N'ADJUST_OUT' THEN -m.BaseQty
            WHEN N'SCRAP_OUT' THEN -m.BaseQty
            WHEN N'FG_RECEIPT_OUT' THEN -m.BaseQty
        END AS decimal(18, 4)) AS SignedBaseQty,
    m.OriginalMovementID AS ReversesMovementId,
    m.ValuationStatus,
    m.UID AS MovementId
FROM dbo.PrProductionBalLotMovement AS m
INNER JOIN dbo.StockPosting AS p
    ON p.Id = m.StockPostingID
   AND p.CompanyCode = m.CompanyCode
   AND p.BranchCode = m.BranchCode
INNER JOIN dbo.StockLedgerEpoch AS e
    ON e.Id = p.LedgerEpochId
   AND e.CompanyCode = p.CompanyCode
   AND e.BranchCode = p.BranchCode
WHERE m.LedgerVersion = 2
  AND m.StockPostingID IS NOT NULL
  AND p.SealedAtUtc IS NOT NULL
  AND e.Status = N'ACTIVE';
GO

CREATE OR ALTER VIEW dbo.vw_IvStockLedgerLeg
AS
SELECT
    h.CompanyCode,
    h.BranchCode,
    h.LedgerEpochID AS LedgerEpochId,
    h.StockPostingID AS StockPostingId,
    p.PostingSequence,
    h.PostingLineNo,
    p.EffectiveAt,
    p.PostedAtUtc,
    p.SourceDocumentType,
    p.SourceDocumentNo,
    h.ICode AS ItemCode,
    h.FrStdUOM AS BaseUom,
    CAST(NULL AS nvarchar(20)) AS BalanceStage,
    CAST(NULL AS nvarchar(30)) AS WorkOrderNo,
    CAST(NULL AS nvarchar(30)) AS ProcessCode,
    CAST(NULL AS nvarchar(20)) AS ProductionLocationCode,
    h.FrLot AS LotIdentity,
    h.IStatus AS StockStatusCode,
    h.TrxType AS MovementType,
    h.FrStdQty AS Qty,
    h.FrStdQty AS BaseQty,
    CAST(-h.FrStdQty AS decimal(18, 4)) AS SignedBaseQty,
    CAST(h.ReversesHistoryID AS bigint) AS ReversesMovementId,
    CAST(NULL AS nvarchar(20)) AS ValuationStatus,
    CAST(h.ID AS bigint) AS MovementId,
    h.EntryRole,
    h.ReversesHistoryID AS ReversesHistoryId,
    h.FrWarehouse AS WarehouseCode,
    h.FrLocation AS LocationCode,
    CAST(N'FROM' AS nvarchar(8)) AS LegKind
FROM dbo.IvTrxHistory AS h
INNER JOIN dbo.StockPosting AS p
    ON p.Id = h.StockPostingID
   AND p.CompanyCode = h.CompanyCode
   AND p.BranchCode = h.BranchCode
INNER JOIN dbo.StockLedgerEpoch AS e
    ON e.Id = p.LedgerEpochId
   AND e.CompanyCode = p.CompanyCode
   AND e.BranchCode = p.BranchCode
WHERE h.LedgerVersion = 2
  AND h.StockPostingID IS NOT NULL
  AND p.SealedAtUtc IS NOT NULL
  AND e.Status = N'ACTIVE'
  AND h.FrStdQty IS NOT NULL
  AND h.FrStdQty > 0

UNION ALL

SELECT
    h.CompanyCode,
    h.BranchCode,
    h.LedgerEpochID AS LedgerEpochId,
    h.StockPostingID AS StockPostingId,
    p.PostingSequence,
    h.PostingLineNo,
    p.EffectiveAt,
    p.PostedAtUtc,
    p.SourceDocumentType,
    p.SourceDocumentNo,
    h.ICode AS ItemCode,
    h.ToStdUOM AS BaseUom,
    CAST(NULL AS nvarchar(20)) AS BalanceStage,
    CAST(NULL AS nvarchar(30)) AS WorkOrderNo,
    CAST(NULL AS nvarchar(30)) AS ProcessCode,
    CAST(NULL AS nvarchar(20)) AS ProductionLocationCode,
    h.ToLot AS LotIdentity,
    h.IStatus AS StockStatusCode,
    h.TrxType AS MovementType,
    h.ToStdQty AS Qty,
    h.ToStdQty AS BaseQty,
    CAST(h.ToStdQty AS decimal(18, 4)) AS SignedBaseQty,
    CAST(h.ReversesHistoryID AS bigint) AS ReversesMovementId,
    CAST(NULL AS nvarchar(20)) AS ValuationStatus,
    CAST(h.ID AS bigint) AS MovementId,
    h.EntryRole,
    h.ReversesHistoryID AS ReversesHistoryId,
    h.ToWarehouse AS WarehouseCode,
    h.ToLocation AS LocationCode,
    CAST(N'TO' AS nvarchar(8)) AS LegKind
FROM dbo.IvTrxHistory AS h
INNER JOIN dbo.StockPosting AS p
    ON p.Id = h.StockPostingID
   AND p.CompanyCode = h.CompanyCode
   AND p.BranchCode = h.BranchCode
INNER JOIN dbo.StockLedgerEpoch AS e
    ON e.Id = p.LedgerEpochId
   AND e.CompanyCode = p.CompanyCode
   AND e.BranchCode = p.BranchCode
WHERE h.LedgerVersion = 2
  AND h.StockPostingID IS NOT NULL
  AND p.SealedAtUtc IS NOT NULL
  AND e.Status = N'ACTIVE'
  AND h.ToStdQty IS NOT NULL
  AND h.ToStdQty > 0;
GO

CREATE OR ALTER VIEW dbo.vw_StockLedgerLeg
AS
SELECT
    CAST(N'PRODUCTION' AS nvarchar(20)) AS LedgerArea,
    v.CompanyCode,
    v.BranchCode,
    v.LedgerEpochId,
    v.StockPostingId,
    v.PostingSequence,
    v.PostingLineNo,
    v.EffectiveAt,
    v.PostedAtUtc,
    v.SourceDocumentType,
    v.SourceDocumentNo,
    v.ItemCode,
    v.BaseUom,
    v.BalanceStage,
    v.WorkOrderNo,
    v.ProcessCode,
    v.ProductionLocationCode,
    v.LotIdentity,
    v.StockStatusCode,
    v.MovementType,
    v.Qty,
    v.BaseQty,
    v.SignedBaseQty,
    v.ReversesMovementId,
    v.ValuationStatus,
    v.MovementId,
    CAST(NULL AS nvarchar(20)) AS EntryRole,
    CAST(NULL AS int) AS ReversesHistoryId,
    CAST(NULL AS nvarchar(20)) AS WarehouseCode,
    CAST(NULL AS nvarchar(10)) AS LocationCode,
    CAST(N'PROD' AS nvarchar(8)) AS LegKind
FROM dbo.vw_PrTrxHistory AS v

UNION ALL

SELECT
    CAST(N'INVENTORY' AS nvarchar(20)) AS LedgerArea,
    i.CompanyCode,
    i.BranchCode,
    i.LedgerEpochId,
    i.StockPostingId,
    i.PostingSequence,
    i.PostingLineNo,
    i.EffectiveAt,
    i.PostedAtUtc,
    i.SourceDocumentType,
    i.SourceDocumentNo,
    i.ItemCode,
    i.BaseUom,
    i.BalanceStage,
    i.WorkOrderNo,
    i.ProcessCode,
    i.ProductionLocationCode,
    i.LotIdentity,
    i.StockStatusCode,
    i.MovementType,
    i.Qty,
    i.BaseQty,
    i.SignedBaseQty,
    i.ReversesMovementId,
    i.ValuationStatus,
    i.MovementId,
    i.EntryRole,
    i.ReversesHistoryId,
    i.WarehouseCode,
    i.LocationCode,
    i.LegKind
FROM dbo.vw_IvStockLedgerLeg AS i;
GO
