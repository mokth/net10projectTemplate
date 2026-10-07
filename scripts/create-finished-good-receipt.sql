-- Generated from the EF model by scripts/FinishedGoodSchema. Rerunnable; preserves all existing data.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO
IF OBJECT_ID(N'dbo.PrFinishedGoodReceipt', N'U') IS NULL
BEGIN
CREATE TABLE [PrFinishedGoodReceipt] (
    [BatchId] int NOT NULL,
    [CompanyCode] nvarchar(5) NOT NULL,
    [BranchCode] nvarchar(5) NOT NULL,
    [WorkOrderId] bigint NOT NULL,
    [DocumentRevision] int NOT NULL,
    [RowVersion] rowversion NOT NULL,
    [PostingId] bigint NULL,
    [ReversalPostingId] bigint NULL,
    [CorrectedBatchId] int NULL,
    [ReversalReason] nvarchar(500) NULL,
    CONSTRAINT [PK_PrFinishedGoodReceipt] PRIMARY KEY ([BatchId]),
    CONSTRAINT [FK_PrFinishedGoodReceipt_IvTrxBatch_BatchId] FOREIGN KEY ([BatchId]) REFERENCES [IvTrxBatch] ([ID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrFinishedGoodReceipt_PrFinishedGoodReceipt_CorrectedBatchId] FOREIGN KEY ([CorrectedBatchId]) REFERENCES [PrFinishedGoodReceipt] ([BatchId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrFinishedGoodReceipt_PrWorkOrder_WorkOrderId] FOREIGN KEY ([WorkOrderId]) REFERENCES [PrWorkOrder] ([UID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrFinishedGoodReceipt_StockPosting_PostingId] FOREIGN KEY ([PostingId]) REFERENCES [StockPosting] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrFinishedGoodReceipt_StockPosting_ReversalPostingId] FOREIGN KEY ([ReversalPostingId]) REFERENCES [StockPosting] ([Id]) ON DELETE NO ACTION
);
END;
GO
IF OBJECT_ID(N'dbo.PrFinishedGoodPriceSnapshot', N'U') IS NULL
BEGIN
CREATE TABLE [PrFinishedGoodPriceSnapshot] (
    [Id] bigint NOT NULL IDENTITY,
    [StockPostingId] bigint NOT NULL,
    [DestinationBalanceId] int NOT NULL,
    [PreviousUnitPrice] decimal(18,4) NULL,
    [PreviousCost] decimal(18,4) NULL,
    [PreviousPriceEvidence] nvarchar(200) NULL,
    [PostedUnitPrice] decimal(18,4) NOT NULL,
    CONSTRAINT [PK_PrFinishedGoodPriceSnapshot] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PrFinishedGoodPriceSnapshot_IvBalLoc_DestinationBalanceId] FOREIGN KEY ([DestinationBalanceId]) REFERENCES [IvBalLoc] ([ID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrFinishedGoodPriceSnapshot_StockPosting_StockPostingId] FOREIGN KEY ([StockPostingId]) REFERENCES [StockPosting] ([Id]) ON DELETE NO ACTION
);
END;
GO
IF OBJECT_ID(N'dbo.PrFinishedGoodLotOrigin', N'U') IS NULL
BEGIN
CREATE TABLE [PrFinishedGoodLotOrigin] (
    [LotId] int NOT NULL,
    [CompanyCode] nvarchar(5) NOT NULL,
    [OriginatingBranch] nvarchar(5) NOT NULL,
    [WorkOrderId] bigint NOT NULL,
    [RouteStepId] bigint NOT NULL,
    [OperationId] bigint NOT NULL,
    [PhysicalLotNo] nvarchar(50) NOT NULL,
    CONSTRAINT [PK_PrFinishedGoodLotOrigin] PRIMARY KEY ([LotId]),
    CONSTRAINT [FK_PrFinishedGoodLotOrigin_IvLot_LotId] FOREIGN KEY ([LotId]) REFERENCES [IvLot] ([ID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrFinishedGoodLotOrigin_PrWorkOrderOperation_OperationId] FOREIGN KEY ([OperationId]) REFERENCES [PrWorkOrderOperation] ([UID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrFinishedGoodLotOrigin_PrWorkOrderRouteStep_RouteStepId] FOREIGN KEY ([RouteStepId]) REFERENCES [PrWorkOrderRouteStep] ([UID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrFinishedGoodLotOrigin_PrWorkOrder_WorkOrderId] FOREIGN KEY ([WorkOrderId]) REFERENCES [PrWorkOrder] ([UID]) ON DELETE NO ACTION
);
END;
GO
IF OBJECT_ID(N'dbo.PrFinishedGoodFact', N'U') IS NULL
BEGIN
CREATE TABLE [PrFinishedGoodFact] (
    [Id] bigint NOT NULL IDENTITY,
    [BatchId] int NOT NULL,
    [SourceId] bigint NOT NULL,
    [StockPostingId] bigint NOT NULL,
    [ProductionMovementId] bigint NOT NULL,
    [InventoryHistoryId] int NOT NULL,
    [DestinationBalanceId] int NOT NULL,
    [BaseQty] decimal(18,4) NOT NULL,
    [TotalValue] decimal(19,6) NOT NULL,
    [ReversesFactId] bigint NULL,
    CONSTRAINT [PK_PrFinishedGoodFact] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PrFinishedGoodFact_IvBalLoc_DestinationBalanceId] FOREIGN KEY ([DestinationBalanceId]) REFERENCES [IvBalLoc] ([ID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrFinishedGoodFact_IvTrxHistory_InventoryHistoryId] FOREIGN KEY ([InventoryHistoryId]) REFERENCES [IvTrxHistory] ([ID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrFinishedGoodFact_PrFinishedGoodFact_ReversesFactId] FOREIGN KEY ([ReversesFactId]) REFERENCES [PrFinishedGoodFact] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrFinishedGoodFact_PrFinishedGoodReceipt_BatchId] FOREIGN KEY ([BatchId]) REFERENCES [PrFinishedGoodReceipt] ([BatchId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrFinishedGoodFact_StockPosting_StockPostingId] FOREIGN KEY ([StockPostingId]) REFERENCES [StockPosting] ([Id]) ON DELETE NO ACTION
);
END;
GO
IF OBJECT_ID(N'dbo.PrFinishedGoodSource', N'U') IS NULL
BEGIN
CREATE TABLE [PrFinishedGoodSource] (
    [Id] bigint NOT NULL IDENTITY,
    [BatchId] int NOT NULL,
    [DetailId] int NOT NULL,
    [ProductionBalLotId] bigint NOT NULL,
    [RequestedQty] decimal(18,4) NOT NULL,
    [SourceUom] nvarchar(10) NOT NULL,
    [DestinationUom] nvarchar(10) NOT NULL,
    [BaseUom] nvarchar(10) NOT NULL,
    [SourceFactor] decimal(18,8) NOT NULL,
    [DestinationFactor] decimal(18,8) NOT NULL,
    [BaseQty] decimal(18,4) NOT NULL,
    CONSTRAINT [PK_PrFinishedGoodSource] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PrFinishedGoodSource_IvTrxBatchDetail_DetailId] FOREIGN KEY ([DetailId]) REFERENCES [IvTrxBatchDetail] ([ID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrFinishedGoodSource_PrFinishedGoodReceipt_BatchId] FOREIGN KEY ([BatchId]) REFERENCES [PrFinishedGoodReceipt] ([BatchId]) ON DELETE NO ACTION
);
END;
GO
IF OBJECT_ID(N'dbo.PrPoolValuation', N'U') IS NULL
BEGIN
CREATE TABLE [PrPoolValuation] (
    [ProductionBalLotId] bigint NOT NULL,
    [Generation] int NOT NULL,
    [Status] nvarchar(200) NOT NULL,
    [TrackedBaseQty] decimal(18,4) NOT NULL,
    [TrackedValue] decimal(19,6) NOT NULL,
    CONSTRAINT [PK_PrPoolValuation] PRIMARY KEY ([ProductionBalLotId]),
    CONSTRAINT [FK_PrPoolValuation_PrProductionBalLot_ProductionBalLotId] FOREIGN KEY ([ProductionBalLotId]) REFERENCES [PrProductionBalLot] ([UID]) ON DELETE NO ACTION
);
END;
GO
IF OBJECT_ID(N'dbo.PrPoolDependency', N'U') IS NULL
BEGIN
CREATE TABLE [PrPoolDependency] (
    [Id] bigint NOT NULL IDENTITY,
    [ContributorMovementId] bigint NOT NULL,
    [ConsumerMovementId] bigint NOT NULL,
    [StockPostingId] bigint NOT NULL,
    [ReversesDependencyId] bigint NULL,
    CONSTRAINT [PK_PrPoolDependency] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_PrPoolDependency_PrPoolDependency_ReversesDependencyId] FOREIGN KEY ([ReversesDependencyId]) REFERENCES [PrPoolDependency] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrPoolDependency_PrProductionBalLotMovement_ConsumerMovementId] FOREIGN KEY ([ConsumerMovementId]) REFERENCES [PrProductionBalLotMovement] ([UID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrPoolDependency_PrProductionBalLotMovement_ContributorMovementId] FOREIGN KEY ([ContributorMovementId]) REFERENCES [PrProductionBalLotMovement] ([UID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrPoolDependency_StockPosting_StockPostingId] FOREIGN KEY ([StockPostingId]) REFERENCES [StockPosting] ([Id]) ON DELETE NO ACTION
);
END;
GO
IF OBJECT_ID(N'dbo.PrValuationEvidence', N'U') IS NULL
BEGIN
CREATE TABLE [PrValuationEvidence] (
    [MovementId] bigint NOT NULL,
    [ProductionBalLotId] bigint NOT NULL,
    [Generation] int NOT NULL,
    [Status] nvarchar(200) NOT NULL,
    [Basis] nvarchar(200) NOT NULL,
    [Currency] nvarchar(200) NULL,
    [PriceUom] nvarchar(200) NULL,
    [Price] decimal(19,6) NULL,
    [ConversionFactor] decimal(18,8) NULL,
    [InventoryHistoryId] int NULL,
    [OriginalMovementId] bigint NULL,
    CONSTRAINT [PK_PrValuationEvidence] PRIMARY KEY ([MovementId]),
    CONSTRAINT [FK_PrValuationEvidence_IvTrxHistory_InventoryHistoryId] FOREIGN KEY ([InventoryHistoryId]) REFERENCES [IvTrxHistory] ([ID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrValuationEvidence_PrProductionBalLotMovement_MovementId] FOREIGN KEY ([MovementId]) REFERENCES [PrProductionBalLotMovement] ([UID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrValuationEvidence_PrProductionBalLotMovement_OriginalMovementId] FOREIGN KEY ([OriginalMovementId]) REFERENCES [PrProductionBalLotMovement] ([UID]) ON DELETE NO ACTION,
    CONSTRAINT [FK_PrValuationEvidence_PrProductionBalLot_ProductionBalLotId] FOREIGN KEY ([ProductionBalLotId]) REFERENCES [PrProductionBalLot] ([UID]) ON DELETE NO ACTION
);
END;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodFact') AND name=N'IX_PrFinishedGoodFact_BatchId')
CREATE INDEX [IX_PrFinishedGoodFact_BatchId] ON [PrFinishedGoodFact] ([BatchId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodFact') AND name=N'IX_PrFinishedGoodFact_DestinationBalanceId')
CREATE INDEX [IX_PrFinishedGoodFact_DestinationBalanceId] ON [PrFinishedGoodFact] ([DestinationBalanceId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodFact') AND name=N'IX_PrFinishedGoodFact_InventoryHistoryId')
CREATE UNIQUE INDEX [IX_PrFinishedGoodFact_InventoryHistoryId] ON [PrFinishedGoodFact] ([InventoryHistoryId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodFact') AND name=N'IX_PrFinishedGoodFact_ProductionMovementId')
CREATE UNIQUE INDEX [IX_PrFinishedGoodFact_ProductionMovementId] ON [PrFinishedGoodFact] ([ProductionMovementId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodFact') AND name=N'IX_PrFinishedGoodFact_ReversesFactId')
CREATE UNIQUE INDEX [IX_PrFinishedGoodFact_ReversesFactId] ON [PrFinishedGoodFact] ([ReversesFactId]) WHERE [ReversesFactId] IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodFact') AND name=N'IX_PrFinishedGoodFact_SourceId')
CREATE INDEX [IX_PrFinishedGoodFact_SourceId] ON [PrFinishedGoodFact] ([SourceId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodFact') AND name=N'IX_PrFinishedGoodFact_StockPostingId_SourceId')
CREATE UNIQUE INDEX [IX_PrFinishedGoodFact_StockPostingId_SourceId] ON [PrFinishedGoodFact] ([StockPostingId], [SourceId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodLotOrigin') AND name=N'IX_PrFinishedGoodLotOrigin_CompanyCode_OriginatingBranch_WorkOrderId')
CREATE INDEX [IX_PrFinishedGoodLotOrigin_CompanyCode_OriginatingBranch_WorkOrderId] ON [PrFinishedGoodLotOrigin] ([CompanyCode], [OriginatingBranch], [WorkOrderId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodLotOrigin') AND name=N'IX_PrFinishedGoodLotOrigin_OperationId')
CREATE INDEX [IX_PrFinishedGoodLotOrigin_OperationId] ON [PrFinishedGoodLotOrigin] ([OperationId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodLotOrigin') AND name=N'IX_PrFinishedGoodLotOrigin_RouteStepId')
CREATE INDEX [IX_PrFinishedGoodLotOrigin_RouteStepId] ON [PrFinishedGoodLotOrigin] ([RouteStepId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodLotOrigin') AND name=N'IX_PrFinishedGoodLotOrigin_WorkOrderId')
CREATE INDEX [IX_PrFinishedGoodLotOrigin_WorkOrderId] ON [PrFinishedGoodLotOrigin] ([WorkOrderId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodPriceSnapshot') AND name=N'IX_PrFinishedGoodPriceSnapshot_DestinationBalanceId')
CREATE INDEX [IX_PrFinishedGoodPriceSnapshot_DestinationBalanceId] ON [PrFinishedGoodPriceSnapshot] ([DestinationBalanceId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodPriceSnapshot') AND name=N'IX_PrFinishedGoodPriceSnapshot_StockPostingId_DestinationBalanceId')
CREATE UNIQUE INDEX [IX_PrFinishedGoodPriceSnapshot_StockPostingId_DestinationBalanceId] ON [PrFinishedGoodPriceSnapshot] ([StockPostingId], [DestinationBalanceId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodReceipt') AND name=N'IX_PrFinishedGoodReceipt_CompanyCode_BranchCode_WorkOrderId')
CREATE INDEX [IX_PrFinishedGoodReceipt_CompanyCode_BranchCode_WorkOrderId] ON [PrFinishedGoodReceipt] ([CompanyCode], [BranchCode], [WorkOrderId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodReceipt') AND name=N'IX_PrFinishedGoodReceipt_CorrectedBatchId')
CREATE INDEX [IX_PrFinishedGoodReceipt_CorrectedBatchId] ON [PrFinishedGoodReceipt] ([CorrectedBatchId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodReceipt') AND name=N'IX_PrFinishedGoodReceipt_PostingId')
CREATE UNIQUE INDEX [IX_PrFinishedGoodReceipt_PostingId] ON [PrFinishedGoodReceipt] ([PostingId]) WHERE [PostingId] IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodReceipt') AND name=N'IX_PrFinishedGoodReceipt_ReversalPostingId')
CREATE UNIQUE INDEX [IX_PrFinishedGoodReceipt_ReversalPostingId] ON [PrFinishedGoodReceipt] ([ReversalPostingId]) WHERE [ReversalPostingId] IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodReceipt') AND name=N'IX_PrFinishedGoodReceipt_WorkOrderId')
CREATE INDEX [IX_PrFinishedGoodReceipt_WorkOrderId] ON [PrFinishedGoodReceipt] ([WorkOrderId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodSource') AND name=N'IX_PrFinishedGoodSource_BatchId_ProductionBalLotId')
CREATE INDEX [IX_PrFinishedGoodSource_BatchId_ProductionBalLotId] ON [PrFinishedGoodSource] ([BatchId], [ProductionBalLotId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodSource') AND name=N'IX_PrFinishedGoodSource_DetailId')
CREATE UNIQUE INDEX [IX_PrFinishedGoodSource_DetailId] ON [PrFinishedGoodSource] ([DetailId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrFinishedGoodSource') AND name=N'IX_PrFinishedGoodSource_ProductionBalLotId')
CREATE INDEX [IX_PrFinishedGoodSource_ProductionBalLotId] ON [PrFinishedGoodSource] ([ProductionBalLotId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrPoolDependency') AND name=N'IX_PrPoolDependency_ConsumerMovementId_ContributorMovementId_StockPostingId')
CREATE UNIQUE INDEX [IX_PrPoolDependency_ConsumerMovementId_ContributorMovementId_StockPostingId] ON [PrPoolDependency] ([ConsumerMovementId], [ContributorMovementId], [StockPostingId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrPoolDependency') AND name=N'IX_PrPoolDependency_ContributorMovementId')
CREATE INDEX [IX_PrPoolDependency_ContributorMovementId] ON [PrPoolDependency] ([ContributorMovementId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrPoolDependency') AND name=N'IX_PrPoolDependency_ReversesDependencyId')
CREATE UNIQUE INDEX [IX_PrPoolDependency_ReversesDependencyId] ON [PrPoolDependency] ([ReversesDependencyId]) WHERE [ReversesDependencyId] IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrPoolDependency') AND name=N'IX_PrPoolDependency_StockPostingId')
CREATE INDEX [IX_PrPoolDependency_StockPostingId] ON [PrPoolDependency] ([StockPostingId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrValuationEvidence') AND name=N'IX_PrValuationEvidence_InventoryHistoryId')
CREATE INDEX [IX_PrValuationEvidence_InventoryHistoryId] ON [PrValuationEvidence] ([InventoryHistoryId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrValuationEvidence') AND name=N'IX_PrValuationEvidence_OriginalMovementId')
CREATE INDEX [IX_PrValuationEvidence_OriginalMovementId] ON [PrValuationEvidence] ([OriginalMovementId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrValuationEvidence') AND name=N'IX_PrValuationEvidence_ProductionBalLotId_Generation')
CREATE INDEX [IX_PrValuationEvidence_ProductionBalLotId_Generation] ON [PrValuationEvidence] ([ProductionBalLotId], [Generation]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PrFinishedGoodFact') AND name=N'FK_PrFinishedGoodFact_PrFinishedGoodSource_SourceId')
ALTER TABLE [PrFinishedGoodFact] ADD CONSTRAINT [FK_PrFinishedGoodFact_PrFinishedGoodSource_SourceId] FOREIGN KEY ([SourceId]) REFERENCES [PrFinishedGoodSource] ([Id]) ON DELETE NO ACTION;
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PrFinishedGoodFact') AND name=N'FK_PrFinishedGoodFact_PrProductionBalLotMovement_ProductionMovementId')
ALTER TABLE [PrFinishedGoodFact] ADD CONSTRAINT [FK_PrFinishedGoodFact_PrProductionBalLotMovement_ProductionMovementId] FOREIGN KEY ([ProductionMovementId]) REFERENCES [PrProductionBalLotMovement] ([UID]) ON DELETE NO ACTION;
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PrFinishedGoodSource') AND name=N'FK_PrFinishedGoodSource_PrProductionBalLot_ProductionBalLotId')
ALTER TABLE [PrFinishedGoodSource] ADD CONSTRAINT [FK_PrFinishedGoodSource_PrProductionBalLot_ProductionBalLotId] FOREIGN KEY ([ProductionBalLotId]) REFERENCES [PrProductionBalLot] ([UID]) ON DELETE NO ACTION;
GO
IF COL_LENGTH(N'dbo.IvTrxBatchDetail', N'PriceEvidence') IS NULL ALTER TABLE dbo.IvTrxBatchDetail ADD PriceEvidence nvarchar(200) NULL;
GO
IF COL_LENGTH(N'dbo.IvBalLoc', N'PriceEvidence') IS NULL ALTER TABLE dbo.IvBalLoc ADD PriceEvidence nvarchar(200) NULL;
GO
IF COL_LENGTH(N'dbo.IvTrxHistory', N'PriceEvidence') IS NULL ALTER TABLE dbo.IvTrxHistory ADD PriceEvidence nvarchar(200) NULL;
GO
IF COL_LENGTH(N'dbo.IvTrxHistory', N'ExactTransferredValue') IS NULL ALTER TABLE dbo.IvTrxHistory ADD ExactTransferredValue decimal(19,6) NULL;
GO
IF COL_LENGTH(N'dbo.IvTrxHistory', N'ValuationStatus') IS NULL ALTER TABLE dbo.IvTrxHistory ADD ValuationStatus nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.IvTrxHistory', N'EvidenceBaseQty') IS NULL ALTER TABLE dbo.IvTrxHistory ADD EvidenceBaseQty decimal(18,4) NULL;
GO
IF COL_LENGTH(N'dbo.IvTrxHistory', N'EvidenceBaseUom') IS NULL ALTER TABLE dbo.IvTrxHistory ADD EvidenceBaseUom nvarchar(10) NULL;
GO
