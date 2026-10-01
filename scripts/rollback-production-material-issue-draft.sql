SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF EXISTS (SELECT 1 FROM dbo.PrMaterialIssueLine l JOIN dbo.IvTrxBatch b ON b.ID = l.InventoryBatchID WHERE b.BatchStatus IN (N'NEW', N'CANCELLED'))
    THROW 51310, 'NEW/CANCELLED IP drafts exist. Migrate or remove them before rolling back the draft schema.', 1;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionPostingLink') AND name = N'UQ_PrProductionPostingLink_MaterialIssueBatch')
    DROP INDEX UQ_PrProductionPostingLink_MaterialIssueBatch ON dbo.PrProductionPostingLink;
DROP TABLE IF EXISTS dbo.PrMaterialIssueLine;
IF COL_LENGTH(N'dbo.PrProductionPostingLink', N'SnapshotHash') IS NOT NULL
    ALTER TABLE dbo.PrProductionPostingLink DROP COLUMN SnapshotHash;
IF COL_LENGTH(N'dbo.PrProductionPostingLink', N'SnapshotRevision') IS NOT NULL
    ALTER TABLE dbo.PrProductionPostingLink DROP COLUMN SnapshotRevision;

COMMIT TRANSACTION;
