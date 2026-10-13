/*
  Daily Production / ProductionBalLot schema + PrMaterialMovement lineage expansion.
  Idempotent. Safe to re-run.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ── Expand PrMaterialMovement for production CONSUME ─────────────────────────────────────── */

IF COL_LENGTH(N'dbo.PrMaterialMovement', N'FromBalLocID') IS NOT NULL
   AND EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement')
          AND name = N'FromBalLocID' AND is_nullable = 0)
    ALTER TABLE dbo.PrMaterialMovement ALTER COLUMN FromBalLocID int NULL;
GO

IF COL_LENGTH(N'dbo.PrMaterialMovement', N'InventoryBatchID') IS NOT NULL
   AND EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement')
          AND name = N'InventoryBatchID' AND is_nullable = 0)
    ALTER TABLE dbo.PrMaterialMovement ALTER COLUMN InventoryBatchID int NULL;
GO

IF COL_LENGTH(N'dbo.PrMaterialMovement', N'InventoryBatchNo') IS NOT NULL
   AND EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement')
          AND name = N'InventoryBatchNo' AND is_nullable = 0)
    ALTER TABLE dbo.PrMaterialMovement ALTER COLUMN InventoryBatchNo int NULL;
GO

IF COL_LENGTH(N'dbo.PrMaterialMovement', N'InventoryBatchDetailID') IS NOT NULL
   AND EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement')
          AND name = N'InventoryBatchDetailID' AND is_nullable = 0)
    ALTER TABLE dbo.PrMaterialMovement ALTER COLUMN InventoryBatchDetailID int NULL;
GO

IF COL_LENGTH(N'dbo.PrMaterialMovement', N'InventoryTrxLineNo') IS NOT NULL
   AND EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement')
          AND name = N'InventoryTrxLineNo' AND is_nullable = 0)
    ALTER TABLE dbo.PrMaterialMovement ALTER COLUMN InventoryTrxLineNo smallint NULL;
GO

IF COL_LENGTH(N'dbo.PrMaterialMovement', N'ProductionBalLotID') IS NULL
    ALTER TABLE dbo.PrMaterialMovement ADD ProductionBalLotID bigint NULL;
GO
IF COL_LENGTH(N'dbo.PrMaterialMovement', N'ProductionBalLotMovementID') IS NULL
    ALTER TABLE dbo.PrMaterialMovement ADD ProductionBalLotMovementID bigint NULL;
GO
IF COL_LENGTH(N'dbo.PrMaterialMovement', N'ProductionOutputID') IS NULL
    ALTER TABLE dbo.PrMaterialMovement ADD ProductionOutputID bigint NULL;
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrMaterialMovement_Type')
    ALTER TABLE dbo.PrMaterialMovement DROP CONSTRAINT CK_PrMaterialMovement_Type;
GO
ALTER TABLE dbo.PrMaterialMovement WITH CHECK ADD CONSTRAINT CK_PrMaterialMovement_Type
    CHECK ([MovementType] IN (N'ISSUE', N'ISSUE_REVERSAL', N'RETURN', N'RETURN_REVERSAL', N'CONSUME', N'CONSUME_REVERSAL', N'ADJUST'));
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_PrMaterialMovement_PostingLine' AND object_id = OBJECT_ID(N'dbo.PrMaterialMovement'))
    DROP INDEX UQ_PrMaterialMovement_PostingLine ON dbo.PrMaterialMovement;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_PrMaterialMovement_PostingInventoryLine' AND object_id = OBJECT_ID(N'dbo.PrMaterialMovement'))
    CREATE UNIQUE INDEX UQ_PrMaterialMovement_PostingInventoryLine
        ON dbo.PrMaterialMovement (PostingLinkID, InventoryBatchDetailID, MovementType)
        WHERE InventoryBatchDetailID IS NOT NULL;
GO

/* ── PrProductionOutput ───────────────────────────────────────────────────────────────────── */

IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrProductionOutput
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrProductionOutput PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        DocumentNo nvarchar(30) NOT NULL,
        Status nvarchar(20) NOT NULL,
        DocumentRevision int NOT NULL CONSTRAINT DF_PrProductionOutput_DocumentRevision DEFAULT (1),
        WipConsumptionPolicy nvarchar(30) NOT NULL CONSTRAINT DF_PrProductionOutput_WipConsumptionPolicy DEFAULT (N'STRICT'),
        WorkOrderID bigint NOT NULL,
        RouteStepID bigint NOT NULL,
        WorkOrderOperationID bigint NOT NULL,
        ProductionDate datetime2 NOT NULL,
        ShiftCode nvarchar(20) NULL,
        PlannedMachineCode nvarchar(30) NULL,
        ActualMachineCode nvarchar(30) NULL,
        OperatorCode nvarchar(30) NULL,
        GoodQty decimal(18,4) NOT NULL CONSTRAINT DF_PrProductionOutput_Good DEFAULT (0),
        ScrapQty decimal(18,4) NOT NULL CONSTRAINT DF_PrProductionOutput_Scrap DEFAULT (0),
        RejectQty decimal(18,4) NOT NULL CONSTRAINT DF_PrProductionOutput_Reject DEFAULT (0),
        HoldQty decimal(18,4) NOT NULL CONSTRAINT DF_PrProductionOutput_Hold DEFAULT (0),
        OutputUOM nvarchar(10) NOT NULL,
        OutputItemCode nvarchar(30) NOT NULL,
        OutputType nvarchar(20) NULL,
        OutputLotNo nvarchar(50) NOT NULL,
        SnapshotRevision int NOT NULL,
        SnapshotHash nvarchar(64) NOT NULL,
        CreateRequestId nvarchar(64) NOT NULL,
        CreateRequestFingerprint varchar(64) NOT NULL,
        PostingId bigint NULL,
        ReversalPostingId bigint NULL,
        ReversalReason nvarchar(500) NULL,
        PostedDate datetime2 NULL,
        PostedBy nvarchar(10) NULL,
        ReversedDate datetime2 NULL,
        ReversedBy nvarchar(10) NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(10) NOT NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_PrProductionOutput_WorkOrder FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID),
        CONSTRAINT FK_PrProductionOutput_StockPosting_PostingId FOREIGN KEY (PostingId) REFERENCES dbo.StockPosting (Id),
        CONSTRAINT FK_PrProductionOutput_StockPosting_ReversalPostingId FOREIGN KEY (ReversalPostingId) REFERENCES dbo.StockPosting (Id),
        CONSTRAINT CK_PrProductionOutput_Status CHECK ([Status] IN (N'NEW', N'POSTED')),
        CONSTRAINT CK_PrProductionOutput_DocumentRevision CHECK ([DocumentRevision] > 0),
        CONSTRAINT CK_PrProductionOutput_WipConsumptionPolicy CHECK ([WipConsumptionPolicy] IN (N'STRICT', N'SHARED_BOM_WIP')),
        CONSTRAINT CK_PrProductionOutput_Qty CHECK ([GoodQty] >= 0 AND [ScrapQty] >= 0 AND [RejectQty] >= 0 AND [HoldQty] >= 0)
    );
END;
GO

IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.check_constraints
       WHERE parent_object_id = OBJECT_ID(N'dbo.PrProductionOutput')
         AND name = N'CK_PrProductionOutput_Status'
         AND is_disabled = 0 AND is_not_trusted = 0
         AND CHARINDEX(N'NEW', definition) > 0
         AND CHARINDEX(N'POSTED', definition) > 0
         AND CHARINDEX(N'REVERSED', definition) = 0)
BEGIN
    IF EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE parent_object_id = OBJECT_ID(N'dbo.PrProductionOutput')
                 AND name = N'CK_PrProductionOutput_Status')
        ALTER TABLE dbo.PrProductionOutput DROP CONSTRAINT CK_PrProductionOutput_Status;

    ALTER TABLE dbo.PrProductionOutput WITH CHECK
        ADD CONSTRAINT CK_PrProductionOutput_Status CHECK ([Status] IN (N'NEW', N'POSTED'));
END;
GO

/* Upgrade an existing Daily Production table before generation-scoped posting/index work. */
IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrProductionOutput', N'DocumentRevision') IS NULL
BEGIN
    ALTER TABLE dbo.PrProductionOutput
        ADD DocumentRevision int NOT NULL
            CONSTRAINT DF_PrProductionOutput_DocumentRevision DEFAULT (1) WITH VALUES;
END;
GO

IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.check_constraints
       WHERE parent_object_id = OBJECT_ID(N'dbo.PrProductionOutput')
         AND name = N'CK_PrProductionOutput_DocumentRevision')
BEGIN
    ALTER TABLE dbo.PrProductionOutput WITH CHECK
        ADD CONSTRAINT CK_PrProductionOutput_DocumentRevision CHECK ([DocumentRevision] > 0);
END;
GO

/* Upgrade older Daily Production tables with stable create-request identity. */
IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrProductionOutput', N'CreateRequestId') IS NULL
    ALTER TABLE dbo.PrProductionOutput ADD CreateRequestId nvarchar(64) NULL;
GO

IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrProductionOutput', N'CreateRequestFingerprint') IS NULL
    ALTER TABLE dbo.PrProductionOutput ADD CreateRequestFingerprint varchar(64) NULL;
GO

IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
BEGIN
    UPDATE dbo.PrProductionOutput
       SET CreateRequestId = COALESCE(CreateRequestId, LOWER(REPLACE(CONVERT(varchar(36), NEWID()), '-', ''))),
           CreateRequestFingerprint = COALESCE(
               CreateRequestFingerprint,
               CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT('LEGACY|', CONVERT(varchar(20), UID), '|', DocumentNo)), 2))
     WHERE CreateRequestId IS NULL OR CreateRequestFingerprint IS NULL;
END;
GO

IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.PrProductionOutput') AND name = N'CreateRequestId' AND is_nullable = 1)
    ALTER TABLE dbo.PrProductionOutput ALTER COLUMN CreateRequestId nvarchar(64) NOT NULL;
IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.PrProductionOutput') AND name = N'CreateRequestFingerprint' AND is_nullable = 1)
    ALTER TABLE dbo.PrProductionOutput ALTER COLUMN CreateRequestFingerprint varchar(64) NOT NULL;
GO

IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrProductionOutput', N'PostingId') IS NULL
    ALTER TABLE dbo.PrProductionOutput ADD PostingId bigint NULL;
IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrProductionOutput', N'ReversalPostingId') IS NULL
    ALTER TABLE dbo.PrProductionOutput ADD ReversalPostingId bigint NULL;
GO

IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
   AND EXISTS (
       SELECT 1
       FROM dbo.PrProductionOutput o
       OUTER APPLY (
           SELECT COUNT_BIG(*) AS MatchCount
           FROM dbo.StockPosting p
           WHERE p.CompanyCode = o.CompanyCode
             AND p.BranchCode = o.BranchCode
             AND p.SourceModule = N'PRODUCTION'
             AND p.SourceDocumentType = N'PRODUCTION_OUTPUT'
             AND p.SourceDocumentId = CONVERT(nvarchar(64), o.UID)
             AND p.DocumentRevision = o.DocumentRevision
             AND p.PostingRole = N'PRIMARY') posting
       WHERE o.Status = N'POSTED' AND o.PostingId IS NULL AND posting.MatchCount <> 1)
    THROW 51101, 'Daily Production upgrade failed: a POSTED output does not have exactly one matching primary StockPosting.', 1;
GO

UPDATE o
   SET PostingId = p.Id
FROM dbo.PrProductionOutput o
JOIN dbo.StockPosting p
  ON p.CompanyCode = o.CompanyCode
 AND p.BranchCode = o.BranchCode
 AND p.SourceModule = N'PRODUCTION'
 AND p.SourceDocumentType = N'PRODUCTION_OUTPUT'
 AND p.SourceDocumentId = CONVERT(nvarchar(64), o.UID)
 AND p.DocumentRevision = o.DocumentRevision
 AND p.PostingRole = N'PRIMARY'
WHERE o.Status = N'POSTED' AND o.PostingId IS NULL;
GO

IF OBJECT_ID(N'dbo.PrProductionPostingLink', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrProductionPostingLink', N'DocumentRevision') IS NULL
BEGIN
    ALTER TABLE dbo.PrProductionPostingLink
        ADD DocumentRevision int NOT NULL
            CONSTRAINT DF_PrProductionPostingLink_DocumentRevision DEFAULT (1) WITH VALUES;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.PrProductionOutput') AND name = N'FK_PrProductionOutput_StockPosting_PostingId')
    ALTER TABLE dbo.PrProductionOutput WITH CHECK ADD CONSTRAINT FK_PrProductionOutput_StockPosting_PostingId FOREIGN KEY (PostingId) REFERENCES dbo.StockPosting (Id);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.PrProductionOutput') AND name = N'FK_PrProductionOutput_StockPosting_ReversalPostingId')
    ALTER TABLE dbo.PrProductionOutput WITH CHECK ADD CONSTRAINT FK_PrProductionOutput_StockPosting_ReversalPostingId FOREIGN KEY (ReversalPostingId) REFERENCES dbo.StockPosting (Id);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionOutput') AND name = N'UQ_PrProductionOutput_DocumentNo')
    CREATE UNIQUE INDEX UQ_PrProductionOutput_DocumentNo ON dbo.PrProductionOutput (CompanyCode, BranchCode, DocumentNo);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionOutput') AND name = N'UQ_PrProductionOutput_CreateRequest')
    CREATE UNIQUE INDEX UQ_PrProductionOutput_CreateRequest ON dbo.PrProductionOutput (CompanyCode, BranchCode, CreateRequestId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionOutput') AND name = N'IX_PrProductionOutput_PostingId')
    CREATE UNIQUE INDEX IX_PrProductionOutput_PostingId ON dbo.PrProductionOutput (PostingId) WHERE PostingId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionOutput') AND name = N'IX_PrProductionOutput_ReversalPostingId')
    CREATE UNIQUE INDEX IX_PrProductionOutput_ReversalPostingId ON dbo.PrProductionOutput (ReversalPostingId) WHERE ReversalPostingId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionOutput') AND name = N'IX_PrProductionOutput_WorkOrder_Date')
    CREATE INDEX IX_PrProductionOutput_WorkOrder_Date ON dbo.PrProductionOutput (WorkOrderID, ProductionDate);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionPostingLink') AND name = N'UQ_PrProductionPostingLink_OutputGeneration')
    CREATE UNIQUE INDEX UQ_PrProductionPostingLink_OutputGeneration
        ON dbo.PrProductionPostingLink (CompanyCode, BranchCode, CommandType, ProductionDocumentLineID, DocumentRevision)
        WHERE ProductionDocumentLineID IS NOT NULL AND CommandType = N'OUTPUT_POST';
GO

/* Upgrade an existing Daily Production table. Existing and historical rows remain STRICT. */
IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrProductionOutput', N'WipConsumptionPolicy') IS NULL
BEGIN
    ALTER TABLE dbo.PrProductionOutput
        ADD WipConsumptionPolicy nvarchar(30) NOT NULL
            CONSTRAINT DF_PrProductionOutput_WipConsumptionPolicy DEFAULT (N'STRICT') WITH VALUES;
END;
GO

IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.check_constraints
       WHERE parent_object_id = OBJECT_ID(N'dbo.PrProductionOutput')
         AND name = N'CK_PrProductionOutput_WipConsumptionPolicy')
BEGIN
    ALTER TABLE dbo.PrProductionOutput WITH CHECK
        ADD CONSTRAINT CK_PrProductionOutput_WipConsumptionPolicy
        CHECK ([WipConsumptionPolicy] IN (N'STRICT', N'SHARED_BOM_WIP'));
END;
GO

/* ── PrProductionBalLot ───────────────────────────────────────────────────────────────────── */

IF OBJECT_ID(N'dbo.PrProductionBalLot', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrProductionBalLot
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrProductionBalLot PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        Kind nvarchar(20) NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        Description nvarchar(200) NULL,
        Qty decimal(18,4) NOT NULL CONSTRAINT DF_PrProductionBalLot_Qty DEFAULT (0),
        UOM nvarchar(10) NOT NULL,
        BaseQty decimal(18,4) NOT NULL CONSTRAINT DF_PrProductionBalLot_BaseQty DEFAULT (0),
        BaseUOM nvarchar(10) NOT NULL,
        ConversionFactorToBase decimal(18,8) NOT NULL CONSTRAINT DF_PrProductionBalLot_Factor DEFAULT (1),
        TotalCost decimal(19,6) NOT NULL CONSTRAINT DF_PrProductionBalLot_TotalCost DEFAULT (0),
        AverageUnitCost decimal(19,6) NOT NULL CONSTRAINT DF_PrProductionBalLot_AvgCost DEFAULT (0),
        WorkOrderID bigint NOT NULL,
        WorkOrderNo nvarchar(30) NOT NULL,
        WorkOrderMaterialID bigint NULL,
        OriginalIssueMovementID bigint NULL,
        SourceIvBalLocID int NULL,
        WarehouseCode nvarchar(20) NOT NULL CONSTRAINT DF_PrProductionBalLot_Wh DEFAULT (N''),
        LocationCode nvarchar(10) NOT NULL CONSTRAINT DF_PrProductionBalLot_Loc DEFAULT (N''),
        LotNo nvarchar(50) NOT NULL CONSTRAINT DF_PrProductionBalLot_Lot DEFAULT (N''),
        ProducingRouteStepID bigint NULL,
        WorkOrderOperationID bigint NULL,
        OutputType nvarchar(20) NULL,
        WorkCentreCode nvarchar(20) NULL,
        ProcessCode nvarchar(30) NULL,
        LastMovementDate datetime2 NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_PrProductionBalLot_WorkOrder FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID),
        CONSTRAINT CK_PrProductionBalLot_Kind CHECK ([Kind] IN (N'MATERIAL_IN', N'WIP')),
        CONSTRAINT CK_PrProductionBalLot_Qty CHECK ([Qty] >= 0 AND [BaseQty] >= 0 AND [ConversionFactorToBase] > 0),
        CONSTRAINT CK_PrProductionBalLot_Cost CHECK ([TotalCost] >= 0 AND [AverageUnitCost] >= 0)
    );
    CREATE UNIQUE INDEX UQ_PrProductionBalLot_MaterialIn
        ON dbo.PrProductionBalLot (CompanyCode, BranchCode, Kind, WorkOrderID, WorkOrderMaterialID, OriginalIssueMovementID)
        WHERE Kind = N'MATERIAL_IN';
    CREATE UNIQUE INDEX UQ_PrProductionBalLot_Wip
        ON dbo.PrProductionBalLot (CompanyCode, BranchCode, Kind, WorkOrderID, ProducingRouteStepID, ItemCode, LotNo)
        WHERE Kind = N'WIP';
    CREATE INDEX IX_PrProductionBalLot_Item_Lot ON dbo.PrProductionBalLot (CompanyCode, BranchCode, ItemCode, LotNo);
END;
GO

IF OBJECT_ID(N'dbo.PrProductionBalLotMovement', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrProductionBalLotMovement
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrProductionBalLotMovement PRIMARY KEY,
        ProductionBalLotID bigint NOT NULL,
        MovementType nvarchar(20) NOT NULL,
        Qty decimal(18,4) NOT NULL,
        UOM nvarchar(10) NOT NULL,
        BaseQty decimal(18,4) NOT NULL,
        BaseUOM nvarchar(10) NOT NULL,
        UnitCost decimal(19,6) NOT NULL CONSTRAINT DF_PrBalLotMov_UnitCost DEFAULT (0),
        TotalCost decimal(19,6) NOT NULL CONSTRAINT DF_PrBalLotMov_TotalCost DEFAULT (0),
        WorkOrderID bigint NOT NULL,
        WorkOrderMaterialID bigint NULL,
        WorkOrderOperationID bigint NULL,
        RouteStepID bigint NULL,
        ProductionOutputID bigint NULL,
        PostingLinkID bigint NOT NULL,
        OriginalMovementID bigint NULL,
        DocumentType nvarchar(30) NOT NULL,
        DocumentNo nvarchar(30) NOT NULL,
        MovementDate datetime2 NOT NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(10) NOT NULL,
        CONSTRAINT FK_PrBalLotMov_Lot FOREIGN KEY (ProductionBalLotID) REFERENCES dbo.PrProductionBalLot (UID),
        CONSTRAINT CK_PrProductionBalLotMovement_Qty CHECK ([Qty] > 0 AND [BaseQty] > 0),
        CONSTRAINT CK_PrProductionBalLotMovement_Type CHECK ([MovementType] IN (
            N'ISSUE', N'ISSUE_REVERSAL', N'PRODUCE', N'PRODUCE_REVERSAL',
            N'CONSUME', N'CONSUME_REVERSAL', N'RETURN', N'RETURN_REVERSAL'))
    );
    CREATE INDEX IX_PrProductionBalLotMovement_Lot_Date
        ON dbo.PrProductionBalLotMovement (ProductionBalLotID, MovementDate, UID);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_PrMaterialMovement_PostingBalLotLine' AND object_id = OBJECT_ID(N'dbo.PrMaterialMovement'))
    CREATE UNIQUE INDEX UQ_PrMaterialMovement_PostingBalLotLine
        ON dbo.PrMaterialMovement (PostingLinkID, ProductionBalLotMovementID, MovementType)
        WHERE ProductionBalLotMovementID IS NOT NULL;
GO

PRINT N'Daily Production schema ready.';
GO
