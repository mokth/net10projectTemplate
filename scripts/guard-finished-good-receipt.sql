/* Run after create-finished-good-receipt.sql. Append-only evidence, including direct SQL paths. */
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
DECLARE @table sysname, @sql nvarchar(max);
DECLARE fg_fact_tables CURSOR LOCAL FAST_FORWARD FOR
 SELECT name FROM sys.tables WHERE name IN ('PrFinishedGoodFact','PrFinishedGoodPriceSnapshot','PrFinishedGoodLotOrigin','PrValuationEvidence','PrPoolDependency');
OPEN fg_fact_tables;
FETCH NEXT FROM fg_fact_tables INTO @table;
WHILE @@FETCH_STATUS=0
BEGIN
 SET @sql=N'CREATE OR ALTER TRIGGER dbo.'+QUOTENAME(N'TR_'+@table+N'_Immutable')+N' ON dbo.'+QUOTENAME(@table)+N'
 AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted) THROW 51090,''FG posting and provenance facts are immutable. Append a linked reversal.'',1; END;';
 EXEC sys.sp_executesql @sql;
 FETCH NEXT FROM fg_fact_tables INTO @table;
END;
CLOSE fg_fact_tables;
DEALLOCATE fg_fact_tables;
GO
CREATE OR ALTER TRIGGER dbo.TR_IvLot_FgOrigin ON dbo.IvLot AFTER UPDATE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN deleted d ON i.ID=d.ID JOIN dbo.PrFinishedGoodLotOrigin o ON o.LotId=i.ID
   WHERE i.CompanyCode<>d.CompanyCode OR i.ICode<>d.ICode OR i.LotNo<>d.LotNo
    OR ISNULL(CONVERT(date,i.ExpiryDate),'00010101')<>ISNULL(CONVERT(date,d.ExpiryDate),'00010101'))
 THROW 51091,'FG-owned lot identity and expiry cannot be changed.',1;
END;
GO
CREATE OR ALTER VIEW dbo.vw_FinishedGoodReceiptReconciliation AS
SELECT r.CompanyCode,r.BranchCode,f.BatchId,f.SourceId,f.StockPostingId,f.ReversesFactId,
 f.BaseQty,f.TotalValue,m.BaseQty AS ProductionBaseQty,m.TotalCost AS ProductionValue,
 h.EvidenceBaseQty AS InventoryBaseQty,h.EvidenceBaseUom,h.ExactTransferredValue AS InventoryValue,
 CONVERT(bit,CASE WHEN f.BaseQty=m.BaseQty AND f.BaseQty=h.EvidenceBaseQty
   AND f.TotalValue=m.TotalCost AND f.TotalValue=h.ExactTransferredValue THEN 1 ELSE 0 END) AS IsBalanced
FROM dbo.PrFinishedGoodFact f JOIN dbo.PrFinishedGoodReceipt r ON r.BatchId=f.BatchId
JOIN dbo.PrProductionBalLotMovement m ON m.UID=f.ProductionMovementId
JOIN dbo.IvTrxHistory h ON h.ID=f.InventoryHistoryId;
GO
