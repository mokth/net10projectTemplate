SET NOCOUNT ON;
SET XACT_ABORT ON;

INSERT dbo.PrMaterialIssueLine
    (CompanyCode, BranchCode, PostingLinkID, InventoryBatchID, InventoryBatchDetailID,
     InventoryBatchNo, InventoryTrxLineNo, WorkOrderID, WorkOrderOperationID,
     WorkOrderMaterialID, IssueQty, BaseQty, CreatedDate, CreatedBy)
SELECT m.CompanyCode, m.BranchCode, m.PostingLinkID, m.InventoryBatchID, m.InventoryBatchDetailID,
       m.InventoryBatchNo, m.InventoryTrxLineNo, m.WorkOrderID, m.WorkOrderOperationID,
       m.WorkOrderMaterialID, m.Qty, m.BaseQty, m.CreatedDate, m.CreatedBy
FROM dbo.PrMaterialMovement m
WHERE m.MovementType = N'ISSUE'
  AND NOT EXISTS (SELECT 1 FROM dbo.PrMaterialIssueLine x WHERE x.InventoryBatchDetailID = m.InventoryBatchDetailID);
