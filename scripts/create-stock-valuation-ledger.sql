/*
  Authoritative financial inventory valuation (V1 moving weighted average).
  Run after scripts/create-stock-posting-ledger.sql and the V2 inventory lineage migration.
  Safe to re-run; existing objects are preserved.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

/* Cost evidence/status is part of the valuation contract, not an FG-only dependency. */
IF COL_LENGTH(N'dbo.IvTrxBatchDetail', N'PriceEvidence') IS NULL
    ALTER TABLE dbo.IvTrxBatchDetail ADD PriceEvidence nvarchar(200) NULL;
IF COL_LENGTH(N'dbo.IvTrxHistory', N'PriceEvidence') IS NULL
    ALTER TABLE dbo.IvTrxHistory ADD PriceEvidence nvarchar(200) NULL;
IF COL_LENGTH(N'dbo.IvTrxHistory', N'ExactTransferredValue') IS NULL
    ALTER TABLE dbo.IvTrxHistory ADD ExactTransferredValue decimal(19,6) NULL;
IF COL_LENGTH(N'dbo.IvTrxHistory', N'ValuationStatus') IS NULL
    ALTER TABLE dbo.IvTrxHistory ADD ValuationStatus nvarchar(20) NULL;

IF OBJECT_ID(N'dbo.StockValuationFact', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockValuationFact
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockValuationFact PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        LedgerEpochId bigint NOT NULL,
        StockPostingId bigint NOT NULL,
        PostingLineNo int NOT NULL,
        SplitOrdinal int NOT NULL,
        SourceLineId nvarchar(120) NOT NULL,
        SourceDocumentType nvarchar(40) NOT NULL,
        SourceDocumentId nvarchar(64) NOT NULL,
        SourceDocumentNo nvarchar(50) NOT NULL,
        SourceDocumentLine nvarchar(50) NULL,
        EffectiveAt datetime2(7) NOT NULL,
        BusinessDate date NOT NULL,
        PeriodKey char(7) NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        WarehouseCode nvarchar(20) NULL,
        LocationCode nvarchar(10) NULL,
        LotId int NULL,
        LotNo nvarchar(50) NULL,
        ItemStatus nvarchar(10) NULL,
        BaseUom nvarchar(10) NOT NULL,
        MovementCode nvarchar(40) NOT NULL,
        Direction int NOT NULL,
        BaseQty decimal(19,6) NOT NULL,
        CostMethod nvarchar(30) NOT NULL,
        UnitCost decimal(19,6) NOT NULL,
        CostAmount decimal(19,6) NOT NULL,
        TransactionCurrency nvarchar(3) NULL,
        TransactionCostAmount decimal(19,6) NULL,
        ExchangeRate decimal(19,8) NULL,
        BaseCurrency nvarchar(3) NULL,
        BaseCostAmount decimal(19,6) NOT NULL,
        ValuationSource nvarchar(40) NOT NULL,
        ValuationStatus nvarchar(20) NOT NULL,
        ValuationVersion int NOT NULL,
        InventoryHistoryId int NULL,
        FromBalLocId int NULL,
        ToBalLocId int NULL,
        ProductionMovementId bigint NULL,
        WorkOrderId bigint NULL,
        WorkOrderOperationId bigint NULL,
        ProductionPostingLinkId bigint NULL,
        OriginalValuationFactId bigint NULL,
        ReversesValuationFactId bigint NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(100) NOT NULL,
        CONSTRAINT CK_StockValuationFact_Direction CHECK (Direction IN (-1,1)),
        CONSTRAINT CK_StockValuationFact_Quantity CHECK (BaseQty >= 0),
        CONSTRAINT CK_StockValuationFact_Amount CHECK (CostAmount >= 0 AND BaseCostAmount >= 0),
        CONSTRAINT CK_StockValuationFact_Identity CHECK (PostingLineNo > 0 AND SplitOrdinal >= 0),
        CONSTRAINT CK_StockValuationFact_NoSelfReverse CHECK
            ((OriginalValuationFactId IS NULL OR OriginalValuationFactId <> Id)
             AND (ReversesValuationFactId IS NULL OR ReversesValuationFactId <> Id)),
        CONSTRAINT FK_StockValuationFact_Epoch FOREIGN KEY (CompanyCode, BranchCode, LedgerEpochId)
            REFERENCES dbo.StockLedgerEpoch(CompanyCode, BranchCode, Id),
        CONSTRAINT FK_StockValuationFact_Posting FOREIGN KEY (CompanyCode, BranchCode, StockPostingId)
            REFERENCES dbo.StockPosting(CompanyCode, BranchCode, Id),
        CONSTRAINT FK_StockValuationFact_History FOREIGN KEY (InventoryHistoryId) REFERENCES dbo.IvTrxHistory(ID),
        CONSTRAINT FK_StockValuationFact_FromBal FOREIGN KEY (FromBalLocId) REFERENCES dbo.IvBalLoc(ID),
        CONSTRAINT FK_StockValuationFact_ToBal FOREIGN KEY (ToBalLocId) REFERENCES dbo.IvBalLoc(ID),
        CONSTRAINT FK_StockValuationFact_Lot FOREIGN KEY (LotId) REFERENCES dbo.IvLot(ID),
        CONSTRAINT FK_StockValuationFact_Original FOREIGN KEY (OriginalValuationFactId) REFERENCES dbo.StockValuationFact(Id),
        CONSTRAINT FK_StockValuationFact_Reverses FOREIGN KEY (ReversesValuationFactId) REFERENCES dbo.StockValuationFact(Id)
    );
    CREATE UNIQUE INDEX UQ_StockValuationFact_PostingLineSplit
        ON dbo.StockValuationFact(StockPostingId, PostingLineNo, SplitOrdinal);
    CREATE INDEX IX_StockValuationFact_ItemEffectiveAt
        ON dbo.StockValuationFact(CompanyCode, BranchCode, ItemCode, EffectiveAt);
    CREATE INDEX IX_StockValuationFact_Period
        ON dbo.StockValuationFact(CompanyCode, BranchCode, PeriodKey);
    CREATE INDEX IX_StockValuationFact_History ON dbo.StockValuationFact(InventoryHistoryId);
    CREATE INDEX IX_StockValuationFact_Source
        ON dbo.StockValuationFact(CompanyCode, BranchCode, SourceDocumentType, SourceDocumentId);
    CREATE INDEX IX_StockValuationFact_Original ON dbo.StockValuationFact(OriginalValuationFactId);
    CREATE UNIQUE INDEX UQ_StockValuationFact_Reversal
        ON dbo.StockValuationFact(ReversesValuationFactId) WHERE ReversesValuationFactId IS NOT NULL;
    CREATE INDEX IX_StockValuationFact_WorkOrder
        ON dbo.StockValuationFact(WorkOrderId, WorkOrderOperationId);
END;

IF OBJECT_ID(N'dbo.StockCostState', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockCostState
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockCostState PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        CostMethod nvarchar(30) NOT NULL,
        OnHandBaseQty decimal(19,6) NOT NULL,
        InventoryValue decimal(19,6) NOT NULL,
        AverageUnitCost decimal(19,6) NOT NULL,
        LastValuationFactId bigint NULL,
        LastPostingSequence bigint NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_StockCostState_Quantity CHECK (OnHandBaseQty >= 0),
        CONSTRAINT CK_StockCostState_Value CHECK (InventoryValue >= 0),
        CONSTRAINT FK_StockCostState_LastFact FOREIGN KEY (LastValuationFactId) REFERENCES dbo.StockValuationFact(Id)
    );
    CREATE UNIQUE INDEX UQ_StockCostState_Pool
        ON dbo.StockCostState(CompanyCode, BranchCode, ItemCode, CostMethod);
END;

IF OBJECT_ID(N'dbo.StockValuationPeriodSnapshotHdr', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockValuationPeriodSnapshotHdr
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockValuationPeriodSnapshotHdr PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        LedgerEpochId bigint NOT NULL,
        PeriodKey char(7) NOT NULL,
        Revision int NOT NULL,
        PostingSequenceWatermark bigint NOT NULL,
        SourceDataHash char(64) NOT NULL,
        ValuationStatus nvarchar(20) NOT NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(100) NOT NULL,
        CONSTRAINT CK_StockValuationPeriodSnapshotHdr_Revision CHECK (Revision > 0),
        CONSTRAINT CK_StockValuationPeriodSnapshotHdr_Watermark CHECK (PostingSequenceWatermark >= 0),
        CONSTRAINT FK_StockValuationPeriodSnapshotHdr_Epoch FOREIGN KEY (CompanyCode, BranchCode, LedgerEpochId)
            REFERENCES dbo.StockLedgerEpoch(CompanyCode, BranchCode, Id)
    );
    CREATE UNIQUE INDEX UQ_StockValuationPeriodSnapshotHdr_Revision
        ON dbo.StockValuationPeriodSnapshotHdr(CompanyCode, BranchCode, PeriodKey, Revision);
END;

IF OBJECT_ID(N'dbo.StockValuationPeriodSnapshotLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockValuationPeriodSnapshotLine
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockValuationPeriodSnapshotLine PRIMARY KEY,
        HeaderId bigint NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        BaseUom nvarchar(10) NOT NULL,
        CostMethod nvarchar(30) NOT NULL,
        OpeningQty decimal(19,6) NOT NULL,
        OpeningValue decimal(19,6) NOT NULL,
        InQty decimal(19,6) NOT NULL,
        InValue decimal(19,6) NOT NULL,
        AdjustmentQty decimal(19,6) NOT NULL,
        AdjustmentValue decimal(19,6) NOT NULL,
        OutQty decimal(19,6) NOT NULL,
        OutValue decimal(19,6) NOT NULL,
        ClosingQty decimal(19,6) NOT NULL,
        ClosingValue decimal(19,6) NOT NULL,
        CONSTRAINT FK_StockValuationPeriodSnapshotLine_Header FOREIGN KEY (HeaderId)
            REFERENCES dbo.StockValuationPeriodSnapshotHdr(Id)
    );
    CREATE UNIQUE INDEX UQ_StockValuationPeriodSnapshotLine_Pool
        ON dbo.StockValuationPeriodSnapshotLine(HeaderId, ItemCode, CostMethod);
END;

COMMIT TRANSACTION;
GO

CREATE OR ALTER TRIGGER dbo.TR_StockValuationFact_Immutable
ON dbo.StockValuationFact
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51020, 'StockValuationFact is immutable; append a linked reversal.', 1;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_StockValuationPeriodSnapshotHdr_Immutable
ON dbo.StockValuationPeriodSnapshotHdr
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51021, 'Financial valuation snapshots are immutable; append a new revision.', 1;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_StockValuationPeriodSnapshotLine_Immutable
ON dbo.StockValuationPeriodSnapshotLine
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51022, 'Financial valuation snapshot lines are immutable.', 1;
END;
GO
