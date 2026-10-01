SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.PrMaterialIssueLine', N'U') IS NOT NULL
    THROW 51300, 'PrMaterialIssueLine already exists; review the installed draft schema before continuing.', 1;

IF EXISTS (
    SELECT 1 FROM dbo.PrProductionPostingLink
    WHERE CommandType = N'MATERIAL_ISSUE_POST' AND InventoryBatchNo IS NOT NULL
    GROUP BY CompanyCode, BranchCode, InventoryBatchNo HAVING COUNT(*) > 1)
    THROW 51301, 'Duplicate material-issue posting links exist for an inventory batch.', 1;

IF EXISTS (
    SELECT 1 FROM dbo.PrMaterialMovement m
    LEFT JOIN dbo.IvTrxBatchDetail d ON d.ID = m.InventoryBatchDetailID
    WHERE m.MovementType = N'ISSUE' AND d.ID IS NULL)
    THROW 51302, 'An ISSUE movement references a missing inventory detail.', 1;

IF EXISTS (
    SELECT 1 FROM dbo.PrMaterialMovement m
    LEFT JOIN dbo.PrProductionPostingLink l ON l.UID = m.PostingLinkID
    LEFT JOIN dbo.PrWorkOrderMaterial wm ON wm.UID = m.WorkOrderMaterialID
    LEFT JOIN dbo.PrWorkOrderOperation o ON o.UID = m.WorkOrderOperationID
    WHERE m.MovementType = N'ISSUE' AND (l.UID IS NULL OR wm.UID IS NULL OR o.UID IS NULL))
    THROW 51303, 'An ISSUE movement cannot resolve its posting link/material/operation.', 1;
