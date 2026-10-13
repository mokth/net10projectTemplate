/* Read-only contract check for the production rollback-to-NEW posting generations. */
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.PrProductionPostingLink', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrProductionOutputMaterial', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrProductionStockReturnLine', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrProductionStockAdjustmentLine', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrFinishedGoodSource', N'U') IS NULL
    THROW 51400, 'Production posting-generation preflight failed: a scoped transaction table is missing.', 1;

DECLARE @ExpectedColumns TABLE(TableName sysname, ColumnName sysname, TypeName sysname, IsNullable bit);
INSERT @ExpectedColumns VALUES
(N'PrProductionPostingLink',N'DocumentRevision',N'int',0),
(N'PrProductionOutput',N'DocumentRevision',N'int',0),
(N'PrProductionOutput',N'CreateRequestId',N'nvarchar',0),
(N'PrProductionOutput',N'CreateRequestFingerprint',N'varchar',0),
(N'PrProductionOutput',N'PostingId',N'bigint',1),
(N'PrProductionOutput',N'ReversalPostingId',N'bigint',1),
(N'PrProductionOutputMaterial',N'DocumentRevision',N'int',0),
(N'PrProductionStockReturnLine',N'DocumentRevision',N'int',0),
(N'PrProductionStockAdjustmentLine',N'DocumentRevision',N'int',0),
(N'PrFinishedGoodSource',N'DocumentRevision',N'int',0);
IF EXISTS (
    SELECT 1 FROM @ExpectedColumns e
    LEFT JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.' + e.TableName) AND c.name = e.ColumnName
    WHERE c.column_id IS NULL OR TYPE_NAME(c.user_type_id) <> e.TypeName OR c.is_nullable <> e.IsNullable)
    THROW 51401, 'Production posting-generation preflight failed: a revision or idempotency column is missing or incompatible.', 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints c
    WHERE c.parent_object_id = OBJECT_ID(N'dbo.PrProductionOutput')
      AND c.name = N'CK_PrProductionOutput_Status'
      AND c.is_disabled = 0 AND c.is_not_trusted = 0
      AND CHARINDEX(N'NEW', c.definition) > 0
      AND CHARINDEX(N'POSTED', c.definition) > 0
      AND CHARINDEX(N'REVERSED', c.definition) = 0)
    THROW 51402, 'Production posting-generation preflight failed: Daily Production status must be NEW or POSTED only.', 1;

DECLARE @ExpectedIndexes TABLE(TableName sysname, IndexName sysname, IsUnique bit, ColumnNames nvarchar(1000));
INSERT @ExpectedIndexes VALUES
(N'PrProductionPostingLink',N'UQ_PrProductionPostingLink_MaterialIssueBatchRevision',1,N'CompanyCode,BranchCode,CommandType,InventoryBatchNo,DocumentRevision'),
(N'PrProductionPostingLink',N'UQ_PrProductionPostingLink_OutputGeneration',1,N'CompanyCode,BranchCode,CommandType,ProductionDocumentLineID,DocumentRevision'),
(N'PrProductionPostingLink',N'UQ_PrProductionPostingLink_StockTransactionBatchRevision',1,N'CompanyCode,BranchCode,CommandType,InventoryBatchNo,DocumentRevision'),
(N'PrProductionOutput',N'UQ_PrProductionOutput_CreateRequest',1,N'CompanyCode,BranchCode,CreateRequestId'),
(N'PrProductionOutputMaterial',N'UQ_PrProductionOutputMaterial_Output_Revision_Material',1,N'ProductionOutputID,DocumentRevision,WorkOrderMaterialID'),
(N'PrProductionOutputMaterial',N'UQ_PrProductionOutputMaterial_Output_Revision_Handoff',1,N'ProductionOutputID,DocumentRevision'),
(N'PrProductionStockReturnLine',N'IX_PrProductionStockReturnLine_BatchId_DocumentRevision_ProductionBalLotId',1,N'BatchId,DocumentRevision,ProductionBalLotId'),
(N'PrProductionStockAdjustmentLine',N'IX_PrProductionStockAdjustmentLine_BatchId_DocumentRevision_ProductionBalLotId',1,N'BatchId,DocumentRevision,ProductionBalLotId'),
(N'PrFinishedGoodSource',N'IX_PrFinishedGoodSource_BatchId_DocumentRevision_ProductionBalLotId',0,N'BatchId,DocumentRevision,ProductionBalLotId');
IF EXISTS (
    SELECT 1 FROM @ExpectedIndexes e
    LEFT JOIN sys.indexes i ON i.object_id = OBJECT_ID(N'dbo.' + e.TableName) AND i.name = e.IndexName
    WHERE i.index_id IS NULL OR i.is_disabled = 1 OR i.is_unique <> e.IsUnique
       OR e.ColumnNames <> (
            SELECT STRING_AGG(CONVERT(nvarchar(max), c.name), N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.index_columns ic
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0))
    THROW 51403, 'Production posting-generation preflight failed: revision-scoped unique indexes are missing or incompatible.', 1;

PRINT N'Production posting-generation schema is ready.';
