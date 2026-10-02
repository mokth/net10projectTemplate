SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.PrProductionPostingLink', N'SnapshotRevision') IS NULL
    ALTER TABLE dbo.PrProductionPostingLink ADD SnapshotRevision int NULL;
IF COL_LENGTH(N'dbo.PrProductionPostingLink', N'SnapshotHash') IS NULL
    ALTER TABLE dbo.PrProductionPostingLink ADD SnapshotHash varchar(64) NULL;
IF COL_LENGTH(N'dbo.PrProductionPostingLink', N'ProductionQtyThisIssue') IS NULL
    ALTER TABLE dbo.PrProductionPostingLink ADD ProductionQtyThisIssue decimal(18,4) NULL;

IF OBJECT_ID(N'dbo.PrMaterialIssueLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrMaterialIssueLine
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrMaterialIssueLine PRIMARY KEY,
        CompanyCode varchar(5) NOT NULL,
        BranchCode varchar(5) NOT NULL,
        PostingLinkID bigint NOT NULL,
        InventoryBatchID int NOT NULL,
        InventoryBatchDetailID int NOT NULL,
        InventoryBatchNo int NOT NULL,
        InventoryTrxLineNo smallint NOT NULL,
        WorkOrderID bigint NOT NULL,
        WorkOrderOperationID bigint NOT NULL,
        WorkOrderMaterialID bigint NOT NULL,
        IssueQty decimal(18,4) NOT NULL,
        BaseQty decimal(18,4) NOT NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy varchar(10) NOT NULL,
        CONSTRAINT CK_PrMaterialIssueLine_IssueQty CHECK (IssueQty > 0),
        CONSTRAINT CK_PrMaterialIssueLine_BaseQty CHECK (BaseQty > 0),
        CONSTRAINT FK_PrMaterialIssueLine_PostingLink FOREIGN KEY (PostingLinkID) REFERENCES dbo.PrProductionPostingLink(UID),
        CONSTRAINT FK_PrMaterialIssueLine_Batch FOREIGN KEY (InventoryBatchID) REFERENCES dbo.IvTrxBatch(ID),
        CONSTRAINT FK_PrMaterialIssueLine_Detail FOREIGN KEY (InventoryBatchDetailID) REFERENCES dbo.IvTrxBatchDetail(ID),
        CONSTRAINT FK_PrMaterialIssueLine_WorkOrder FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder(UID),
        CONSTRAINT FK_PrMaterialIssueLine_Operation FOREIGN KEY (WorkOrderOperationID) REFERENCES dbo.PrWorkOrderOperation(UID),
        CONSTRAINT FK_PrMaterialIssueLine_Material FOREIGN KEY (WorkOrderMaterialID) REFERENCES dbo.PrWorkOrderMaterial(UID)
    );
    CREATE UNIQUE INDEX UQ_PrMaterialIssueLine_InventoryDetail ON dbo.PrMaterialIssueLine(InventoryBatchDetailID);
    CREATE UNIQUE INDEX UQ_PrMaterialIssueLine_PostingLine ON dbo.PrMaterialIssueLine(PostingLinkID, InventoryTrxLineNo);
    CREATE INDEX IX_PrMaterialIssueLine_Batch ON dbo.PrMaterialIssueLine(CompanyCode, BranchCode, InventoryBatchNo);
    CREATE INDEX IX_PrMaterialIssueLine_Material ON dbo.PrMaterialIssueLine(CompanyCode, BranchCode, WorkOrderMaterialID);
    CREATE INDEX IX_PrMaterialIssueLine_Operation ON dbo.PrMaterialIssueLine(WorkOrderOperationID, WorkOrderMaterialID);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionPostingLink') AND name = N'UQ_PrProductionPostingLink_MaterialIssueBatch')
    CREATE UNIQUE INDEX UQ_PrProductionPostingLink_MaterialIssueBatch
        ON dbo.PrProductionPostingLink(CompanyCode, BranchCode, CommandType, InventoryBatchNo)
        WHERE InventoryBatchNo IS NOT NULL AND CommandType = N'MATERIAL_ISSUE_POST';

COMMIT TRANSACTION;
