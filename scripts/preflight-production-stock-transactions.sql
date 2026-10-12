-- Read-only release gate. Does not enable posting.
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
-- Read-only prerequisite checks. Apply existing Production/Stock Ledger migrations first.
IF EXISTS (SELECT 1 FROM (VALUES
 (N'PrMaterialMovement',N'FromBalLocID'), (N'PrMaterialMovement',N'InventoryBatchID'),
 (N'PrMaterialMovement',N'InventoryBatchNo'), (N'PrMaterialMovement',N'InventoryBatchDetailID'),
 (N'PrMaterialMovement',N'InventoryTrxLineNo')) required(TableName,ColumnName)
 LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(N'dbo.'+required.TableName) AND c.name=required.ColumnName
 WHERE c.column_id IS NULL OR c.is_nullable=0)
 THROW 51170, 'Production stock prerequisite failed: material lineage columns must exist and allow NULL. Apply create-production-daily-output.sql first.', 1;

IF EXISTS (SELECT 1 FROM (VALUES
 (N'PrProductionBalLotMovement',N'TR_PrProductionBalLotMovement_Seal'),
 (N'PrMaterialMovement',N'TR_PrMaterialMovement_Seal'),
 (N'PrProductionMovementAllocation',N'TR_PrProductionMovementAllocation_Seal')) required(TableName,TriggerName)
 LEFT JOIN sys.triggers t ON t.parent_id=OBJECT_ID(N'dbo.'+required.TableName) AND t.name=required.TriggerName
 WHERE t.object_id IS NULL OR t.is_disabled=1)
 THROW 51171, 'Production stock prerequisite failed: required V2 seal triggers are missing or disabled.', 1;

IF EXISTS (SELECT 1 FROM (VALUES
 (N'PrProductionBalLotMovement',N'UnitCost'),(N'PrProductionBalLotMovement',N'TotalCost'),
 (N'PrMaterialMovement',N'UnitCost'),(N'PrMaterialMovement',N'TotalCost'),
 (N'PrProductionBalLot',N'TotalCost'),(N'PrProductionBalLot',N'AverageUnitCost'),
 (N'IvTrxHistory',N'ExactTransferredValue')) required(TableName,ColumnName)
 LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(N'dbo.'+required.TableName) AND c.name=required.ColumnName
 WHERE c.column_id IS NULL OR TYPE_NAME(c.user_type_id)<>N'decimal' OR c.precision<>19 OR c.scale<>6)
 THROW 51172, 'Production stock prerequisite failed: Production financial authority must be decimal(19,6).', 1;

IF EXISTS (SELECT 1 FROM (VALUES (N'RETURN'),(N'RETURN_REVERSAL'),(N'ADJUST_IN'),(N'ADJUST_OUT')) required(Code)
 WHERE NOT EXISTS (SELECT 1 FROM sys.check_constraints c
 WHERE c.parent_object_id=OBJECT_ID(N'dbo.PrProductionBalLotMovement')
 AND c.name=N'CK_PrProductionBalLotMovement_Type' AND c.is_disabled=0 AND c.is_not_trusted=0
 AND CHARINDEX(N''''+required.Code+N'''',c.definition)>0))
 THROW 51173, 'Production stock prerequisite failed: current trusted movement constraint is required.', 1;

IF EXISTS (SELECT 1 FROM (VALUES
 (N'PrMaterialMovement',N'UQ_PrMaterialMovement_V2_SourceLine'),
 (N'PrMaterialMovement',N'UQ_PrMaterialMovement_V2_Reversal'),
 (N'PrProductionBalLotMovement',N'UQ_PrProductionBalLotMovement_V2_PostingLine')) required(TableName,IndexName)
 LEFT JOIN sys.indexes i ON i.object_id=OBJECT_ID(N'dbo.'+required.TableName) AND i.name=required.IndexName
 WHERE i.index_id IS NULL OR i.is_disabled=1 OR i.is_unique=0)
 THROW 51174, 'Production stock prerequisite failed: V2 source, posting-line and reversal unique indexes are required.', 1;

IF EXISTS (SELECT 1 FROM (VALUES (N'SourceIssueMovementID'),(N'ReversesMaterialMovementID')) required(ColumnName)
 WHERE NOT EXISTS (SELECT 1 FROM sys.foreign_key_columns fc JOIN sys.foreign_keys f ON f.object_id=fc.constraint_object_id
 JOIN sys.columns c ON c.object_id=fc.parent_object_id AND c.column_id=fc.parent_column_id
 WHERE fc.parent_object_id=OBJECT_ID(N'dbo.PrMaterialMovement') AND c.name=required.ColumnName
 AND fc.referenced_object_id=OBJECT_ID(N'dbo.PrMaterialMovement') AND f.is_disabled=0 AND f.is_not_trusted=0))
 THROW 51175, 'Production stock prerequisite failed: material source and reversal foreign keys are required.', 1;

-- Verify the deployed schema, including reruns against an existing database.
DECLARE @ExpectedColumns TABLE(TableName sysname,ColumnName sysname,TypeName sysname,MaxLength int NULL,PrecisionValue int NULL,ScaleValue int NULL,IsNullable bit,IsIdentity bit);
INSERT @ExpectedColumns VALUES
(N'PrProductionStockAdjustment',N'BatchId',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockAdjustment',N'CompanyCode',N'nvarchar',10,NULL,NULL,0,0),
(N'PrProductionStockAdjustment',N'BranchCode',N'nvarchar',10,NULL,NULL,0,0),
(N'PrProductionStockAdjustment',N'WorkOrderId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockAdjustment',N'SnapshotRevision',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockAdjustment',N'SnapshotHash',N'varchar',64,NULL,NULL,0,0),
(N'PrProductionStockAdjustment',N'DocumentRevision',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockAdjustment',N'RowVersion',N'timestamp',8,NULL,NULL,0,0),
(N'PrProductionStockAdjustment',N'PostingId',N'bigint',NULL,NULL,NULL,1,0),
(N'PrProductionStockAdjustment',N'ReversalPostingId',N'bigint',NULL,NULL,NULL,1,0),
(N'PrProductionStockAdjustment',N'CorrectedBatchId',N'int',NULL,NULL,NULL,1,0),
(N'PrProductionStockAdjustment',N'ReversalReason',N'nvarchar',1000,NULL,NULL,1,0),
(N'PrProductionStockAdjustment',N'DeletedAtUtc',N'datetime2',NULL,NULL,7,1,0),
(N'PrProductionStockAdjustment',N'DeletedBy',N'nvarchar',20,NULL,NULL,1,0),
(N'PrProductionStockAdjustment',N'DeleteReason',N'nvarchar',500,NULL,NULL,1,0),
(N'PrProductionStockReturn',N'BatchId',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturn',N'CompanyCode',N'nvarchar',10,NULL,NULL,0,0),
(N'PrProductionStockReturn',N'BranchCode',N'nvarchar',10,NULL,NULL,0,0),
(N'PrProductionStockReturn',N'WorkOrderId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturn',N'SnapshotRevision',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturn',N'SnapshotHash',N'varchar',64,NULL,NULL,0,0),
(N'PrProductionStockReturn',N'DocumentRevision',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturn',N'RowVersion',N'timestamp',8,NULL,NULL,0,0),
(N'PrProductionStockReturn',N'PostingId',N'bigint',NULL,NULL,NULL,1,0),
(N'PrProductionStockReturn',N'ReversalPostingId',N'bigint',NULL,NULL,NULL,1,0),
(N'PrProductionStockReturn',N'CorrectedBatchId',N'int',NULL,NULL,NULL,1,0),
(N'PrProductionStockReturn',N'ReversalReason',N'nvarchar',1000,NULL,NULL,1,0),
(N'PrProductionStockReturn',N'DeletedAtUtc',N'datetime2',NULL,NULL,7,1,0),
(N'PrProductionStockReturn',N'DeletedBy',N'nvarchar',20,NULL,NULL,1,0),
(N'PrProductionStockReturn',N'DeleteReason',N'nvarchar',500,NULL,NULL,1,0),
(N'PrProductionStockReturnPriceSnapshot',N'Id',N'bigint',NULL,NULL,NULL,0,1),
(N'PrProductionStockReturnPriceSnapshot',N'StockPostingId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnPriceSnapshot',N'DestinationBalanceId',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnPriceSnapshot',N'PreviousUnitPrice',N'decimal',NULL,18,4,1,0),
(N'PrProductionStockReturnPriceSnapshot',N'PreviousCost',N'decimal',NULL,18,4,1,0),
(N'PrProductionStockReturnPriceSnapshot',N'PreviousPriceEvidence',N'nvarchar',400,NULL,NULL,1,0),
(N'PrProductionStockReturnPriceSnapshot',N'PostedUnitPrice',N'decimal',NULL,18,4,1,0),
(N'PrProductionStockReturnPriceSnapshot',N'PostedCost',N'decimal',NULL,18,4,1,0),
(N'PrProductionStockReturnPriceSnapshot',N'PostedPriceEvidence',N'nvarchar',400,NULL,NULL,1,0),
(N'PrProductionStockAdjustmentLine',N'Id',N'bigint',NULL,NULL,NULL,0,1),
(N'PrProductionStockAdjustmentLine',N'BatchId',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockAdjustmentLine',N'DetailId',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockAdjustmentLine',N'ProductionBalLotId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockAdjustmentLine',N'AdjustmentQty',N'decimal',NULL,18,4,0,0),
(N'PrProductionStockAdjustmentLine',N'SourceUom',N'nvarchar',20,NULL,NULL,0,0),
(N'PrProductionStockAdjustmentLine',N'BaseUom',N'nvarchar',20,NULL,NULL,0,0),
(N'PrProductionStockAdjustmentLine',N'SourceFactor',N'decimal',NULL,18,8,0,0),
(N'PrProductionStockAdjustmentLine',N'BaseQty',N'decimal',NULL,18,4,0,0),
(N'PrProductionStockAdjustmentLine',N'ProposedUnitCost',N'decimal',NULL,19,6,1,0),
(N'PrProductionStockAdjustmentLine',N'CostEvidenceType',N'nvarchar',60,NULL,NULL,1,0),
(N'PrProductionStockAdjustmentLine',N'CostOverrideReason',N'nvarchar',500,NULL,NULL,1,0),
(N'PrProductionStockAdjustmentLine',N'CostApprovedBy',N'nvarchar',20,NULL,NULL,1,0),
(N'PrProductionStockAdjustmentLine',N'CostApprovedAtUtc',N'datetime2',NULL,NULL,7,1,0),
(N'PrProductionStockReturnLine',N'Id',N'bigint',NULL,NULL,NULL,0,1),
(N'PrProductionStockReturnLine',N'BatchId',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnLine',N'DetailId',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnLine',N'ProductionBalLotId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnLine',N'SourceIssueMovementId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnLine',N'RequestedQty',N'decimal',NULL,18,4,0,0),
(N'PrProductionStockReturnLine',N'SourceUom',N'nvarchar',20,NULL,NULL,0,0),
(N'PrProductionStockReturnLine',N'BaseUom',N'nvarchar',20,NULL,NULL,0,0),
(N'PrProductionStockReturnLine',N'SourceFactor',N'decimal',NULL,18,8,0,0),
(N'PrProductionStockReturnLine',N'BaseQty',N'decimal',NULL,18,4,0,0),
(N'PrProductionStockAdjustmentFact',N'Id',N'bigint',NULL,NULL,NULL,0,1),
(N'PrProductionStockAdjustmentFact',N'BatchId',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockAdjustmentFact',N'AdjustmentLineId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockAdjustmentFact',N'StockPostingId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockAdjustmentFact',N'ProductionMovementId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockAdjustmentFact',N'BaseQty',N'decimal',NULL,18,4,0,0),
(N'PrProductionStockAdjustmentFact',N'TotalValue',N'decimal',NULL,19,6,0,0),
(N'PrProductionStockAdjustmentFact',N'UnitCost',N'decimal',NULL,19,6,0,0),
(N'PrProductionStockAdjustmentFact',N'CostBasisCode',N'nvarchar',80,NULL,NULL,0,0),
(N'PrProductionStockAdjustmentFact',N'OriginalIssueMovementId',N'bigint',NULL,NULL,NULL,1,0),
(N'PrProductionStockAdjustmentFact',N'StandardCostRevisionId',N'bigint',NULL,NULL,NULL,1,0),
(N'PrProductionStockAdjustmentFact',N'CostEvidenceType',N'nvarchar',60,NULL,NULL,1,0),
(N'PrProductionStockAdjustmentFact',N'CostOverrideReason',N'nvarchar',500,NULL,NULL,1,0),
(N'PrProductionStockAdjustmentFact',N'CostApprovedBy',N'nvarchar',20,NULL,NULL,1,0),
(N'PrProductionStockAdjustmentFact',N'CostApprovedAtUtc',N'datetime2',NULL,NULL,7,1,0),
(N'PrProductionStockAdjustmentFact',N'ReversesFactId',N'bigint',NULL,NULL,NULL,1,0),
(N'PrProductionStockReturnFact',N'Id',N'bigint',NULL,NULL,NULL,0,1),
(N'PrProductionStockReturnFact',N'BatchId',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnFact',N'ReturnLineId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnFact',N'StockPostingId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnFact',N'ProductionMovementId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnFact',N'ProductionMaterialMovementId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnFact',N'InventoryHistoryId',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnFact',N'DestinationBalanceId',N'int',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnFact',N'SourceIssueMovementId',N'bigint',NULL,NULL,NULL,0,0),
(N'PrProductionStockReturnFact',N'BaseQty',N'decimal',NULL,18,4,0,0),
(N'PrProductionStockReturnFact',N'TotalValue',N'decimal',NULL,19,6,0,0),
(N'PrProductionStockReturnFact',N'ReversesFactId',N'bigint',NULL,NULL,NULL,1,0);
IF EXISTS (SELECT 1 FROM @ExpectedColumns e LEFT JOIN sys.columns c
 ON c.object_id=OBJECT_ID(N'dbo.'+e.TableName) AND c.name=e.ColumnName
 WHERE c.column_id IS NULL OR TYPE_NAME(c.user_type_id)<>e.TypeName OR c.is_nullable<>e.IsNullable OR c.is_identity<>e.IsIdentity
 OR (e.MaxLength IS NOT NULL AND c.max_length<>e.MaxLength)
 OR (e.PrecisionValue IS NOT NULL AND c.precision<>e.PrecisionValue)
 OR (e.ScaleValue IS NOT NULL AND c.scale<>e.ScaleValue))
 THROW 51176, 'Production stock schema failed: missing or incompatible sidecar/fact column.', 1;
DECLARE @ExpectedFks TABLE(TableName sysname,ConstraintName sysname,ColumnName sysname,TargetTable sysname,TargetColumn sysname);
INSERT @ExpectedFks VALUES
(N'PrProductionStockAdjustment',N'FK_PrProductionStockAdjustment_IvTrxBatch_BatchId',N'BatchId',N'IvTrxBatch',N'ID'),
(N'PrProductionStockAdjustment',N'FK_PrProductionStockAdjustment_PrProductionStockAdjustment_CorrectedBatchId',N'CorrectedBatchId',N'PrProductionStockAdjustment',N'BatchId'),
(N'PrProductionStockAdjustment',N'FK_PrProductionStockAdjustment_PrWorkOrder_WorkOrderId',N'WorkOrderId',N'PrWorkOrder',N'UID'),
(N'PrProductionStockAdjustment',N'FK_PrProductionStockAdjustment_StockPosting_PostingId',N'PostingId',N'StockPosting',N'Id'),
(N'PrProductionStockAdjustment',N'FK_PrProductionStockAdjustment_StockPosting_ReversalPostingId',N'ReversalPostingId',N'StockPosting',N'Id'),
(N'PrProductionStockReturn',N'FK_PrProductionStockReturn_IvTrxBatch_BatchId',N'BatchId',N'IvTrxBatch',N'ID'),
(N'PrProductionStockReturn',N'FK_PrProductionStockReturn_PrProductionStockReturn_CorrectedBatchId',N'CorrectedBatchId',N'PrProductionStockReturn',N'BatchId'),
(N'PrProductionStockReturn',N'FK_PrProductionStockReturn_PrWorkOrder_WorkOrderId',N'WorkOrderId',N'PrWorkOrder',N'UID'),
(N'PrProductionStockReturn',N'FK_PrProductionStockReturn_StockPosting_PostingId',N'PostingId',N'StockPosting',N'Id'),
(N'PrProductionStockReturn',N'FK_PrProductionStockReturn_StockPosting_ReversalPostingId',N'ReversalPostingId',N'StockPosting',N'Id'),
(N'PrProductionStockReturnPriceSnapshot',N'FK_PrProductionStockReturnPriceSnapshot_IvBalLoc_DestinationBalanceId',N'DestinationBalanceId',N'IvBalLoc',N'ID'),
(N'PrProductionStockReturnPriceSnapshot',N'FK_PrProductionStockReturnPriceSnapshot_StockPosting_StockPostingId',N'StockPostingId',N'StockPosting',N'Id'),
(N'PrProductionStockAdjustmentLine',N'FK_PrProductionStockAdjustmentLine_IvTrxBatchDetail_DetailId',N'DetailId',N'IvTrxBatchDetail',N'ID'),
(N'PrProductionStockAdjustmentLine',N'FK_PrProductionStockAdjustmentLine_PrProductionBalLot_ProductionBalLotId',N'ProductionBalLotId',N'PrProductionBalLot',N'UID'),
(N'PrProductionStockAdjustmentLine',N'FK_PrProductionStockAdjustmentLine_PrProductionStockAdjustment_BatchId',N'BatchId',N'PrProductionStockAdjustment',N'BatchId'),
(N'PrProductionStockReturnLine',N'FK_PrProductionStockReturnLine_IvTrxBatchDetail_DetailId',N'DetailId',N'IvTrxBatchDetail',N'ID'),
(N'PrProductionStockReturnLine',N'FK_PrProductionStockReturnLine_PrMaterialMovement_SourceIssueMovementId',N'SourceIssueMovementId',N'PrMaterialMovement',N'UID'),
(N'PrProductionStockReturnLine',N'FK_PrProductionStockReturnLine_PrProductionBalLot_ProductionBalLotId',N'ProductionBalLotId',N'PrProductionBalLot',N'UID'),
(N'PrProductionStockReturnLine',N'FK_PrProductionStockReturnLine_PrProductionStockReturn_BatchId',N'BatchId',N'PrProductionStockReturn',N'BatchId'),
(N'PrProductionStockAdjustmentFact',N'FK_PrProductionStockAdjustmentFact_ItemStandardCostRevision_StandardCostRevisionId',N'StandardCostRevisionId',N'ItemStandardCostRevision',N'Id'),
(N'PrProductionStockAdjustmentFact',N'FK_PrProductionStockAdjustmentFact_PrMaterialMovement_OriginalIssueMovementId',N'OriginalIssueMovementId',N'PrMaterialMovement',N'UID'),
(N'PrProductionStockAdjustmentFact',N'FK_PrProductionStockAdjustmentFact_PrProductionBalLotMovement_ProductionMovementId',N'ProductionMovementId',N'PrProductionBalLotMovement',N'UID'),
(N'PrProductionStockAdjustmentFact',N'FK_PrProductionStockAdjustmentFact_PrProductionStockAdjustmentFact_ReversesFactId',N'ReversesFactId',N'PrProductionStockAdjustmentFact',N'Id'),
(N'PrProductionStockAdjustmentFact',N'FK_PrProductionStockAdjustmentFact_PrProductionStockAdjustmentLine_AdjustmentLineId',N'AdjustmentLineId',N'PrProductionStockAdjustmentLine',N'Id'),
(N'PrProductionStockAdjustmentFact',N'FK_PrProductionStockAdjustmentFact_PrProductionStockAdjustment_BatchId',N'BatchId',N'PrProductionStockAdjustment',N'BatchId'),
(N'PrProductionStockAdjustmentFact',N'FK_PrProductionStockAdjustmentFact_StockPosting_StockPostingId',N'StockPostingId',N'StockPosting',N'Id'),
(N'PrProductionStockReturnFact',N'FK_PrProductionStockReturnFact_IvBalLoc_DestinationBalanceId',N'DestinationBalanceId',N'IvBalLoc',N'ID'),
(N'PrProductionStockReturnFact',N'FK_PrProductionStockReturnFact_IvTrxHistory_InventoryHistoryId',N'InventoryHistoryId',N'IvTrxHistory',N'ID'),
(N'PrProductionStockReturnFact',N'FK_PrProductionStockReturnFact_PrMaterialMovement_ProductionMaterialMovementId',N'ProductionMaterialMovementId',N'PrMaterialMovement',N'UID'),
(N'PrProductionStockReturnFact',N'FK_PrProductionStockReturnFact_PrMaterialMovement_SourceIssueMovementId',N'SourceIssueMovementId',N'PrMaterialMovement',N'UID'),
(N'PrProductionStockReturnFact',N'FK_PrProductionStockReturnFact_PrProductionBalLotMovement_ProductionMovementId',N'ProductionMovementId',N'PrProductionBalLotMovement',N'UID'),
(N'PrProductionStockReturnFact',N'FK_PrProductionStockReturnFact_PrProductionStockReturnFact_ReversesFactId',N'ReversesFactId',N'PrProductionStockReturnFact',N'Id'),
(N'PrProductionStockReturnFact',N'FK_PrProductionStockReturnFact_PrProductionStockReturnLine_ReturnLineId',N'ReturnLineId',N'PrProductionStockReturnLine',N'Id'),
(N'PrProductionStockReturnFact',N'FK_PrProductionStockReturnFact_PrProductionStockReturn_BatchId',N'BatchId',N'PrProductionStockReturn',N'BatchId'),
(N'PrProductionStockReturnFact',N'FK_PrProductionStockReturnFact_StockPosting_StockPostingId',N'StockPostingId',N'StockPosting',N'Id');
IF EXISTS (SELECT 1 FROM @ExpectedFks e WHERE NOT EXISTS
 (SELECT 1 FROM sys.foreign_keys f JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=f.object_id
 JOIN sys.columns c ON c.object_id=fc.parent_object_id AND c.column_id=fc.parent_column_id
 JOIN sys.columns rc ON rc.object_id=fc.referenced_object_id AND rc.column_id=fc.referenced_column_id
 WHERE f.name=e.ConstraintName AND f.parent_object_id=OBJECT_ID(N'dbo.'+e.TableName) AND c.name=e.ColumnName
 AND f.referenced_object_id=OBJECT_ID(N'dbo.'+e.TargetTable) AND rc.name=e.TargetColumn
 AND f.is_disabled=0 AND f.is_not_trusted=0 AND f.delete_referential_action=0 AND f.update_referential_action=0))
 THROW 51177, 'Production stock schema failed: missing, untrusted or non-restrict lineage foreign key.', 1;
DECLARE @ExpectedIndexes TABLE(TableName sysname,IndexName sysname,IsUnique bit,HasFilter bit,ColumnNames nvarchar(1000));
INSERT @ExpectedIndexes VALUES
(N'PrProductionPostingLink',N'UQ_PrProductionPostingLink_StockTransactionBatch',1,1,N'CompanyCode,BranchCode,CommandType,InventoryBatchNo'),
(N'PrProductionStockAdjustment',N'IX_PrProductionStockAdjustment_CompanyCode_BranchCode_WorkOrderId',0,0,N'CompanyCode,BranchCode,WorkOrderId'),
(N'PrProductionStockAdjustment',N'IX_PrProductionStockAdjustment_CorrectedBatchId',0,0,N'CorrectedBatchId'),
(N'PrProductionStockAdjustment',N'IX_PrProductionStockAdjustment_PostingId',1,1,N'PostingId'),
(N'PrProductionStockAdjustment',N'IX_PrProductionStockAdjustment_ReversalPostingId',1,1,N'ReversalPostingId'),
(N'PrProductionStockAdjustment',N'IX_PrProductionStockAdjustment_WorkOrderId',0,0,N'WorkOrderId'),
(N'PrProductionStockAdjustmentFact',N'IX_PrProductionStockAdjustmentFact_AdjustmentLineId',0,0,N'AdjustmentLineId'),
(N'PrProductionStockAdjustmentFact',N'IX_PrProductionStockAdjustmentFact_BatchId',0,0,N'BatchId'),
(N'PrProductionStockAdjustmentFact',N'IX_PrProductionStockAdjustmentFact_OriginalIssueMovementId',0,0,N'OriginalIssueMovementId'),
(N'PrProductionStockAdjustmentFact',N'IX_PrProductionStockAdjustmentFact_ProductionMovementId',1,0,N'ProductionMovementId'),
(N'PrProductionStockAdjustmentFact',N'IX_PrProductionStockAdjustmentFact_ReversesFactId',1,1,N'ReversesFactId'),
(N'PrProductionStockAdjustmentFact',N'IX_PrProductionStockAdjustmentFact_StandardCostRevisionId',0,0,N'StandardCostRevisionId'),
(N'PrProductionStockAdjustmentFact',N'IX_PrProductionStockAdjustmentFact_StockPostingId_AdjustmentLineId',1,0,N'StockPostingId,AdjustmentLineId'),
(N'PrProductionStockAdjustmentLine',N'IX_PrProductionStockAdjustmentLine_BatchId_ProductionBalLotId',1,0,N'BatchId,ProductionBalLotId'),
(N'PrProductionStockAdjustmentLine',N'IX_PrProductionStockAdjustmentLine_DetailId',1,0,N'DetailId'),
(N'PrProductionStockAdjustmentLine',N'IX_PrProductionStockAdjustmentLine_ProductionBalLotId',0,0,N'ProductionBalLotId'),
(N'PrProductionStockReturn',N'IX_PrProductionStockReturn_CompanyCode_BranchCode_WorkOrderId',0,0,N'CompanyCode,BranchCode,WorkOrderId'),
(N'PrProductionStockReturn',N'IX_PrProductionStockReturn_CorrectedBatchId',0,0,N'CorrectedBatchId'),
(N'PrProductionStockReturn',N'IX_PrProductionStockReturn_PostingId',1,1,N'PostingId'),
(N'PrProductionStockReturn',N'IX_PrProductionStockReturn_ReversalPostingId',1,1,N'ReversalPostingId'),
(N'PrProductionStockReturn',N'IX_PrProductionStockReturn_WorkOrderId',0,0,N'WorkOrderId'),
(N'PrProductionStockReturnFact',N'IX_PrProductionStockReturnFact_BatchId',0,0,N'BatchId'),
(N'PrProductionStockReturnFact',N'IX_PrProductionStockReturnFact_DestinationBalanceId',0,0,N'DestinationBalanceId'),
(N'PrProductionStockReturnFact',N'IX_PrProductionStockReturnFact_InventoryHistoryId',1,0,N'InventoryHistoryId'),
(N'PrProductionStockReturnFact',N'IX_PrProductionStockReturnFact_ProductionMaterialMovementId',1,0,N'ProductionMaterialMovementId'),
(N'PrProductionStockReturnFact',N'IX_PrProductionStockReturnFact_ProductionMovementId',1,0,N'ProductionMovementId'),
(N'PrProductionStockReturnFact',N'IX_PrProductionStockReturnFact_ReturnLineId',0,0,N'ReturnLineId'),
(N'PrProductionStockReturnFact',N'IX_PrProductionStockReturnFact_ReversesFactId',1,1,N'ReversesFactId'),
(N'PrProductionStockReturnFact',N'IX_PrProductionStockReturnFact_SourceIssueMovementId',0,0,N'SourceIssueMovementId'),
(N'PrProductionStockReturnFact',N'IX_PrProductionStockReturnFact_StockPostingId_ReturnLineId',1,0,N'StockPostingId,ReturnLineId'),
(N'PrProductionStockReturnLine',N'IX_PrProductionStockReturnLine_BatchId_ProductionBalLotId',1,0,N'BatchId,ProductionBalLotId'),
(N'PrProductionStockReturnLine',N'IX_PrProductionStockReturnLine_DetailId',1,0,N'DetailId'),
(N'PrProductionStockReturnLine',N'IX_PrProductionStockReturnLine_ProductionBalLotId',0,0,N'ProductionBalLotId'),
(N'PrProductionStockReturnLine',N'IX_PrProductionStockReturnLine_SourceIssueMovementId',0,0,N'SourceIssueMovementId'),
(N'PrProductionStockReturnPriceSnapshot',N'IX_PrProductionStockReturnPriceSnapshot_DestinationBalanceId',0,0,N'DestinationBalanceId'),
(N'PrProductionStockReturnPriceSnapshot',N'IX_PrProductionStockReturnPriceSnapshot_StockPostingId_DestinationBalanceId',1,0,N'StockPostingId,DestinationBalanceId');
IF EXISTS (SELECT 1 FROM @ExpectedIndexes e LEFT JOIN sys.indexes i ON i.object_id=OBJECT_ID(N'dbo.'+e.TableName) AND i.name=e.IndexName
 WHERE i.index_id IS NULL OR i.is_disabled=1 OR i.is_unique<>e.IsUnique OR i.has_filter<>e.HasFilter
 OR e.ColumnNames<>(SELECT STRING_AGG(CONVERT(nvarchar(max),c.name),N',') WITHIN GROUP(ORDER BY ic.key_ordinal)
 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
 WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0))
 THROW 51178, 'Production stock schema failed: missing or incompatible document/fact unique index.', 1;
DECLARE @ExpectedChecks TABLE(TableName sysname,ConstraintName sysname);
INSERT @ExpectedChecks VALUES
(N'PrProductionStockAdjustment',N'CK_PrProductionStockAdjustment_Header'),
(N'PrProductionStockReturn',N'CK_PrProductionStockReturn_Header'),
(N'PrProductionStockAdjustmentLine',N'CK_PrProductionStockAdjustmentLine_Cost'),
(N'PrProductionStockAdjustmentLine',N'CK_PrProductionStockAdjustmentLine_Qty'),
(N'PrProductionStockReturnLine',N'CK_PrProductionStockReturnLine_Qty'),
(N'PrProductionStockAdjustmentFact',N'CK_PrProductionStockAdjustmentFact_Authority'),
(N'PrProductionStockAdjustmentFact',N'CK_PrProductionStockAdjustmentFact_Reversal'),
(N'PrProductionStockAdjustmentFact',N'CK_PrProductionStockAdjustmentFact_Value'),
(N'PrProductionStockReturnFact',N'CK_PrProductionStockReturnFact_Reversal'),
(N'PrProductionStockReturnFact',N'CK_PrProductionStockReturnFact_Value');
IF EXISTS (SELECT 1 FROM @ExpectedChecks e LEFT JOIN sys.check_constraints c
 ON c.parent_object_id=OBJECT_ID(N'dbo.'+e.TableName) AND c.name=e.ConstraintName
 WHERE c.object_id IS NULL OR c.is_disabled=1 OR c.is_not_trusted=1)
 THROW 51179, 'Production stock schema failed: required trusted check constraint is missing.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.PrMaterialMovement')
 AND name=N'CK_PrMaterialMovement_Type' AND is_disabled=0 AND is_not_trusted=0 AND CHARINDEX(N'''RETURN_REVERSAL''',definition)>0)
 THROW 51180, 'Production stock schema failed: material RETURN_REVERSAL is not enabled.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.PrProductionStockReturnFact') AND name=N'TR_PrProductionStockReturnFact_Immutable' AND is_disabled=0)
 THROW 51183, 'Production stock schema failed: immutable fact trigger missing or disabled.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.PrProductionStockReturnPriceSnapshot') AND name=N'TR_PrProductionStockReturnPriceSnapshot_Immutable' AND is_disabled=0)
 THROW 51183, 'Production stock schema failed: immutable fact trigger missing or disabled.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.PrProductionStockAdjustmentFact') AND name=N'TR_PrProductionStockAdjustmentFact_Immutable' AND is_disabled=0)
 THROW 51183, 'Production stock schema failed: immutable fact trigger missing or disabled.', 1;

SELECT N'PASS' AS ProductionStockSchemaPreflight, DB_NAME() AS DatabaseName;
