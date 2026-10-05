/* Read-only preflight. Run after create-finished-good-receipt.sql; no automatic cost migration. */
SET NOCOUNT ON;
SELECT p.CompanyCode,p.BranchCode,p.UID,p.WorkOrderNo,p.ItemCode,p.BaseQty,p.TotalCost,
 CASE WHEN v.ProductionBalLotId IS NULL OR v.Status <> 'VERIFIED' THEN 'UNVERIFIED_POOL'
      WHEN p.BaseQty=0 AND p.TotalCost<>0 THEN 'STRANDED_VALUE'
      WHEN p.BaseQty<>v.TrackedBaseQty OR p.TotalCost<>v.TrackedValue THEN 'PROJECTION_MISMATCH'
      WHEN p.ProductionLocationID IS NULL OR p.StockStatusCode IS NULL OR p.PhysicalLotNo IS NULL THEN 'MISSING_DIMENSIONS'
      ELSE 'READY' END AS Readiness
FROM dbo.PrProductionBalLot p LEFT JOIN dbo.PrPoolValuation v ON v.ProductionBalLotId=p.UID
WHERE p.BaseQty<>0 OR p.TotalCost<>0;

SELECT l.CompanyCode,l.ICode,l.LotNo,'MISSING_FG_ORIGIN' AS Exception
FROM dbo.IvLot l LEFT JOIN dbo.PrFinishedGoodLotOrigin o ON o.LotId=l.ID
WHERE l.SourceType='FG' AND o.LotId IS NULL;

SELECT f.BatchId,f.SourceId,f.StockPostingId,f.BaseQty,f.TotalValue,
 m.BaseQty AS ProductionBaseQty,m.TotalCost AS ProductionValue,h.EvidenceBaseQty AS InventoryBaseQty,h.ExactTransferredValue AS InventoryValue
FROM dbo.PrFinishedGoodFact f
JOIN dbo.PrProductionBalLotMovement m ON m.UID=f.ProductionMovementId
JOIN dbo.IvTrxHistory h ON h.ID=f.InventoryHistoryId
WHERE f.BaseQty<>m.BaseQty OR f.BaseQty<>h.EvidenceBaseQty OR h.EvidenceBaseQty IS NULL
 OR f.TotalValue<>m.TotalCost OR f.TotalValue<>h.ExactTransferredValue OR h.ExactTransferredValue IS NULL;

SELECT m.UID,m.CompanyCode,m.BranchCode,m.MovementType,'MISSING_PROVENANCE' AS Exception
FROM dbo.PrProductionBalLotMovement m LEFT JOIN dbo.PrValuationEvidence e ON e.MovementId=m.UID
WHERE m.LedgerVersion=2 AND e.MovementId IS NULL;

SELECT r.BatchId,'UNSEALED_OR_MISSING_POSTING' AS Exception
FROM dbo.PrFinishedGoodReceipt r JOIN dbo.IvTrxBatch b ON b.ID=r.BatchId
LEFT JOIN dbo.StockPosting p ON p.ID=r.PostingId
WHERE b.BatchStatus IN ('POSTED','REVERSED') AND (p.ID IS NULL OR p.SealedAtUtc IS NULL);
