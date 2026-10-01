-- Issue-to-Production production execution ledger.
-- Idempotent incremental deployment; safe to rerun after prerequisite schemas exist.

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.PrWorkOrder', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrWorkOrderMaterial', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrWorkOrderOperation', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrProductionPostingLink', N'U') IS NULL
    THROW 51200, 'Production Work Order schema is required. Run create-production-workorder.sql first.', 1;

IF OBJECT_ID(N'dbo.IvTrxBatch', N'U') IS NULL
   OR OBJECT_ID(N'dbo.IvTrxBatchDetail', N'U') IS NULL
   OR OBJECT_ID(N'dbo.IvBalLoc', N'U') IS NULL
   OR OBJECT_ID(N'dbo.IvLot', N'U') IS NULL
    THROW 51201, 'Inventory transaction, balance and lot tables are required.', 1;
GO

IF OBJECT_ID(N'dbo.PrMaterialMovement', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrMaterialMovement
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrMaterialMovement PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        WorkOrderID bigint NOT NULL,
        WorkOrderMaterialID bigint NOT NULL,
        WorkOrderOperationID bigint NOT NULL,
        MovementType nvarchar(20) NOT NULL,
        MovementDate datetime2 NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        Qty decimal(18,4) NOT NULL,
        UOM nvarchar(10) NOT NULL,
        BaseQty decimal(18,4) NOT NULL,
        BaseUOM nvarchar(10) NOT NULL,
        ConversionFactorToBase decimal(18,8) NOT NULL,
        WarehouseCode nvarchar(20) NOT NULL,
        LocationCode nvarchar(10) NOT NULL,
        LotNo nvarchar(50) NOT NULL,
        LotID int NULL,
        FromBalLocID int NOT NULL,
        ItemStatus nvarchar(10) NOT NULL,
        InventoryBatchID int NOT NULL,
        InventoryBatchNo int NOT NULL,
        InventoryBatchDetailID int NOT NULL,
        InventoryTrxLineNo smallint NOT NULL,
        -- Informational only: inventory rollback can physically remove IvTrxHistory rows.
        InventoryHistoryID int NULL,
        InventoryPostingOperationID nvarchar(64) NULL,
        UnitCost decimal(18,4) NOT NULL,
        TotalCost decimal(18,4) NOT NULL,
        PostingLinkID bigint NOT NULL,
        OriginalMovementID bigint NULL,
        Reason nvarchar(50) NULL,
        Remarks nvarchar(250) NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(10) NOT NULL,
        CONSTRAINT CK_PrMaterialMovement_Qty CHECK (Qty > 0 AND BaseQty > 0),
        CONSTRAINT CK_PrMaterialMovement_Conversion CHECK (ConversionFactorToBase > 0),
        CONSTRAINT CK_PrMaterialMovement_Cost CHECK (UnitCost >= 0 AND TotalCost >= 0),
        CONSTRAINT CK_PrMaterialMovement_Type CHECK
            (MovementType IN (N'ISSUE', N'ISSUE_REVERSAL', N'RETURN', N'CONSUME', N'ADJUST')),
        CONSTRAINT FK_PrMaterialMovement_PrWorkOrder
            FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID),
        CONSTRAINT FK_PrMaterialMovement_PrWorkOrderMaterial
            FOREIGN KEY (WorkOrderMaterialID) REFERENCES dbo.PrWorkOrderMaterial (UID),
        CONSTRAINT FK_PrMaterialMovement_PrWorkOrderOperation
            FOREIGN KEY (WorkOrderOperationID) REFERENCES dbo.PrWorkOrderOperation (UID),
        CONSTRAINT FK_PrMaterialMovement_PrProductionPostingLink
            FOREIGN KEY (PostingLinkID) REFERENCES dbo.PrProductionPostingLink (UID),
        CONSTRAINT FK_PrMaterialMovement_OriginalMovement
            FOREIGN KEY (OriginalMovementID) REFERENCES dbo.PrMaterialMovement (UID),
        CONSTRAINT FK_PrMaterialMovement_IvTrxBatch
            FOREIGN KEY (InventoryBatchID) REFERENCES dbo.IvTrxBatch (ID),
        CONSTRAINT FK_PrMaterialMovement_IvTrxBatchDetail
            FOREIGN KEY (InventoryBatchDetailID) REFERENCES dbo.IvTrxBatchDetail (ID),
        CONSTRAINT FK_PrMaterialMovement_IvBalLoc
            FOREIGN KEY (FromBalLocID) REFERENCES dbo.IvBalLoc (ID),
        CONSTRAINT FK_PrMaterialMovement_IvLot
            FOREIGN KEY (LotID) REFERENCES dbo.IvLot (ID)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement') AND name = N'IX_PrMaterialMovement_WorkOrder_Date')
    CREATE INDEX IX_PrMaterialMovement_WorkOrder_Date ON dbo.PrMaterialMovement (WorkOrderID, MovementDate);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement') AND name = N'IX_PrMaterialMovement_Material_Type')
    CREATE INDEX IX_PrMaterialMovement_Material_Type ON dbo.PrMaterialMovement (WorkOrderMaterialID, MovementType);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement') AND name = N'IX_PrMaterialMovement_Operation_Date')
    CREATE INDEX IX_PrMaterialMovement_Operation_Date ON dbo.PrMaterialMovement (WorkOrderOperationID, MovementDate);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement') AND name = N'IX_PrMaterialMovement_InventoryBatch')
    CREATE INDEX IX_PrMaterialMovement_InventoryBatch ON dbo.PrMaterialMovement (CompanyCode, BranchCode, InventoryBatchNo);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement') AND name = N'IX_PrMaterialMovement_OriginalMovement')
    CREATE INDEX IX_PrMaterialMovement_OriginalMovement ON dbo.PrMaterialMovement (OriginalMovementID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement') AND name = N'UQ_PrMaterialMovement_PostingLine')
    CREATE UNIQUE INDEX UQ_PrMaterialMovement_PostingLine
        ON dbo.PrMaterialMovement (PostingLinkID, InventoryBatchDetailID, MovementType);
GO

IF OBJECT_ID(N'dbo.PrMaterialMovement', N'U') IS NULL
    THROW 51202, 'PrMaterialMovement was not created.', 1;

IF (SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement')
    AND name IN (N'IX_PrMaterialMovement_WorkOrder_Date', N'IX_PrMaterialMovement_Material_Type',
                 N'IX_PrMaterialMovement_Operation_Date', N'IX_PrMaterialMovement_InventoryBatch',
                 N'IX_PrMaterialMovement_OriginalMovement', N'UQ_PrMaterialMovement_PostingLine')) <> 6
    THROW 51203, 'PrMaterialMovement indexes are incomplete.', 1;

PRINT N'Issue-to-Production material movement schema verified successfully.';
GO
