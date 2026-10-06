/*
   Additive costing foundation for the V2 stock ledger.
   Safe to run more than once on SQL Server. Existing valuation facts are not rewritten.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.StockCostPolicyRevision', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockCostPolicyRevision
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockCostPolicyRevision PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        CostMethod nvarchar(30) NOT NULL,
        EffectiveFrom date NOT NULL,
        EffectiveTo date NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_StockCostPolicyRevision_Status DEFAULT N'ACTIVE',
        Reason nvarchar(250) NULL,
        ApprovedBy nvarchar(100) NOT NULL,
        ApprovedAtUtc datetime2(7) NOT NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(100) NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_StockCostPolicyRevision_Method CHECK (CostMethod IN (N'MOVING_AVERAGE', N'FIFO', N'STANDARD')),
        CONSTRAINT CK_StockCostPolicyRevision_Status CHECK (Status IN (N'ACTIVE', N'SUPERSEDED')),
        CONSTRAINT CK_StockCostPolicyRevision_Range CHECK (EffectiveTo IS NULL OR EffectiveTo > EffectiveFrom)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockCostPolicyRevision_EffectiveFrom'
              AND object_id = OBJECT_ID(N'dbo.StockCostPolicyRevision'))
    CREATE INDEX IX_StockCostPolicyRevision_EffectiveFrom
        ON dbo.StockCostPolicyRevision (CompanyCode, BranchCode, EffectiveFrom);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockCostPolicyRevision_Status'
              AND object_id = OBJECT_ID(N'dbo.StockCostPolicyRevision'))
    CREATE INDEX IX_StockCostPolicyRevision_Status
        ON dbo.StockCostPolicyRevision (CompanyCode, BranchCode, Status);

IF OBJECT_ID(N'dbo.ItemStandardCostRevision', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ItemStandardCostRevision
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ItemStandardCostRevision PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        EffectiveFrom date NOT NULL,
        EffectiveTo date NULL,
        MaterialCost decimal(19,6) NOT NULL CONSTRAINT DF_ItemStandardCostRevision_MaterialCost DEFAULT (0),
        LabourCost decimal(19,6) NOT NULL CONSTRAINT DF_ItemStandardCostRevision_LabourCost DEFAULT (0),
        MachineCost decimal(19,6) NOT NULL CONSTRAINT DF_ItemStandardCostRevision_MachineCost DEFAULT (0),
        OverheadCost decimal(19,6) NOT NULL CONSTRAINT DF_ItemStandardCostRevision_OverheadCost DEFAULT (0),
        SubcontractCost decimal(19,6) NOT NULL CONSTRAINT DF_ItemStandardCostRevision_SubcontractCost DEFAULT (0),
        TotalStandardCost decimal(19,6) NOT NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_ItemStandardCostRevision_Status DEFAULT N'APPROVED',
        Revision int NOT NULL,
        ApprovedBy nvarchar(100) NOT NULL,
        ApprovedAtUtc datetime2(7) NOT NULL,
        Reason nvarchar(250) NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(100) NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_ItemStandardCostRevision_Range CHECK (EffectiveTo IS NULL OR EffectiveTo > EffectiveFrom),
        CONSTRAINT CK_ItemStandardCostRevision_Costs CHECK
            (MaterialCost >= 0 AND LabourCost >= 0 AND MachineCost >= 0
             AND OverheadCost >= 0 AND SubcontractCost >= 0 AND TotalStandardCost >= 0),
        CONSTRAINT CK_ItemStandardCostRevision_Status CHECK (Status IN (N'APPROVED', N'SUPERSEDED')),
        CONSTRAINT CK_ItemStandardCostRevision_Revision CHECK (Revision > 0)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_ItemStandardCostRevision_Revision'
              AND object_id = OBJECT_ID(N'dbo.ItemStandardCostRevision'))
    CREATE UNIQUE INDEX UQ_ItemStandardCostRevision_Revision
        ON dbo.ItemStandardCostRevision (CompanyCode, BranchCode, ItemCode, EffectiveFrom, Revision);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemStandardCostRevision_EffectiveFrom'
              AND object_id = OBJECT_ID(N'dbo.ItemStandardCostRevision'))
    CREATE INDEX IX_ItemStandardCostRevision_EffectiveFrom
        ON dbo.ItemStandardCostRevision (CompanyCode, BranchCode, ItemCode, EffectiveFrom);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemStandardCostRevision_Status'
              AND object_id = OBJECT_ID(N'dbo.ItemStandardCostRevision'))
    CREATE INDEX IX_ItemStandardCostRevision_Status
        ON dbo.ItemStandardCostRevision (CompanyCode, BranchCode, ItemCode, Status);

IF OBJECT_ID(N'dbo.StockFifoLayer', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockFifoLayer
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockFifoLayer PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        BaseUom nvarchar(10) NOT NULL,
        OriginValuationFactId bigint NOT NULL,
        OriginStockPostingId bigint NOT NULL,
        ReceiptEffectiveAt datetime2(7) NOT NULL,
        OriginalQty decimal(19,6) NOT NULL,
        RemainingQty decimal(19,6) NOT NULL,
        OriginalValue decimal(19,6) NOT NULL,
        AccumulatedAdjustment decimal(19,6) NOT NULL CONSTRAINT DF_StockFifoLayer_AccumulatedAdjustment DEFAULT (0),
        RemainingValue decimal(19,6) NOT NULL,
        CurrentUnitCost decimal(19,6) NOT NULL,
        SourceDocumentType nvarchar(40) NOT NULL,
        SourceDocumentNo nvarchar(50) NOT NULL,
        SourceDocumentLine nvarchar(50) NULL,
        WarehouseCode nvarchar(20) NULL,
        LotId int NULL,
        LotNo nvarchar(50) NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_StockFifoLayer_Status DEFAULT N'OPEN',
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_StockFifoLayer_Quantities CHECK (OriginalQty >= 0 AND RemainingQty >= 0 AND RemainingQty <= OriginalQty),
        CONSTRAINT CK_StockFifoLayer_Values CHECK (OriginalValue >= 0 AND RemainingValue >= 0),
        CONSTRAINT CK_StockFifoLayer_Status CHECK (Status IN (N'OPEN', N'CLOSED')),
        CONSTRAINT FK_StockFifoLayer_OriginFact FOREIGN KEY (OriginValuationFactId) REFERENCES dbo.StockValuationFact(Id),
        CONSTRAINT FK_StockFifoLayer_OriginPosting FOREIGN KEY (OriginStockPostingId) REFERENCES dbo.StockPosting(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockFifoLayer_OpenOrder'
              AND object_id = OBJECT_ID(N'dbo.StockFifoLayer'))
    CREATE INDEX IX_StockFifoLayer_OpenOrder
        ON dbo.StockFifoLayer (CompanyCode, BranchCode, ItemCode, Status, ReceiptEffectiveAt, Id);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockFifoLayer_OriginFact'
              AND object_id = OBJECT_ID(N'dbo.StockFifoLayer'))
    CREATE INDEX IX_StockFifoLayer_OriginFact
        ON dbo.StockFifoLayer (OriginValuationFactId);

IF OBJECT_ID(N'dbo.StockFifoLayerConsumption', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockFifoLayerConsumption
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockFifoLayerConsumption PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        IssueValuationFactId bigint NOT NULL,
        FifoLayerId bigint NOT NULL,
        ConsumedQty decimal(19,6) NOT NULL,
        ConsumedValue decimal(19,6) NOT NULL,
        SplitOrdinal int NOT NULL,
        ReversesConsumptionId bigint NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(100) NOT NULL,
        CONSTRAINT CK_StockFifoLayerConsumption_Qty CHECK (ConsumedQty > 0),
        CONSTRAINT CK_StockFifoLayerConsumption_Value CHECK (ConsumedValue >= 0),
        CONSTRAINT CK_StockFifoLayerConsumption_Split CHECK (SplitOrdinal >= 0),
        CONSTRAINT FK_StockFifoLayerConsumption_IssueFact FOREIGN KEY (IssueValuationFactId) REFERENCES dbo.StockValuationFact(Id),
        CONSTRAINT FK_StockFifoLayerConsumption_Layer FOREIGN KEY (FifoLayerId) REFERENCES dbo.StockFifoLayer(Id),
        CONSTRAINT FK_StockFifoLayerConsumption_Reversal FOREIGN KEY (ReversesConsumptionId) REFERENCES dbo.StockFifoLayerConsumption(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_StockFifoLayerConsumption_IssueSplit'
              AND object_id = OBJECT_ID(N'dbo.StockFifoLayerConsumption'))
    CREATE UNIQUE INDEX UQ_StockFifoLayerConsumption_IssueSplit
        ON dbo.StockFifoLayerConsumption (IssueValuationFactId, SplitOrdinal);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockFifoLayerConsumption_Layer'
              AND object_id = OBJECT_ID(N'dbo.StockFifoLayerConsumption'))
    CREATE INDEX IX_StockFifoLayerConsumption_Layer
        ON dbo.StockFifoLayerConsumption (CompanyCode, BranchCode, FifoLayerId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_StockFifoLayerConsumption_Reversal'
              AND object_id = OBJECT_ID(N'dbo.StockFifoLayerConsumption'))
    CREATE UNIQUE INDEX UQ_StockFifoLayerConsumption_Reversal
        ON dbo.StockFifoLayerConsumption (ReversesConsumptionId)
        WHERE ReversesConsumptionId IS NOT NULL;

IF OBJECT_ID(N'dbo.ProductionStandardCostVariance', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductionStandardCostVariance
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProductionStandardCostVariance PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        StockPostingId bigint NOT NULL,
        ProductionPostingLinkId bigint NULL,
        FinishedGoodReceiptId int NULL,
        InventoryValuationFactId bigint NOT NULL,
        BaseQty decimal(19,6) NOT NULL,
        ActualProductionValue decimal(19,6) NOT NULL,
        StandardInventoryValue decimal(19,6) NOT NULL,
        VarianceAmount decimal(19,6) NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        EffectiveAt datetime2(7) NOT NULL,
        ReversesVarianceId bigint NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(100) NOT NULL,
        CONSTRAINT CK_ProductionStandardCostVariance_Qty CHECK ([BaseQty] > 0),
        CONSTRAINT FK_ProductionStandardCostVariance_Fact FOREIGN KEY (InventoryValuationFactId) REFERENCES dbo.StockValuationFact(Id),
        CONSTRAINT FK_ProductionStandardCostVariance_Reversal FOREIGN KEY (ReversesVarianceId) REFERENCES dbo.ProductionStandardCostVariance(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProductionStandardCostVariance_InventoryFact'
              AND object_id = OBJECT_ID(N'dbo.ProductionStandardCostVariance'))
    CREATE INDEX IX_ProductionStandardCostVariance_InventoryFact
        ON dbo.ProductionStandardCostVariance (InventoryValuationFactId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProductionStandardCostVariance_Posting'
              AND object_id = OBJECT_ID(N'dbo.ProductionStandardCostVariance'))
    CREATE INDEX IX_ProductionStandardCostVariance_Posting
        ON dbo.ProductionStandardCostVariance (CompanyCode, BranchCode, StockPostingId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_ProductionStandardCostVariance_Reversal'
              AND object_id = OBJECT_ID(N'dbo.ProductionStandardCostVariance'))
    CREATE UNIQUE INDEX UQ_ProductionStandardCostVariance_Reversal
        ON dbo.ProductionStandardCostVariance (ReversesVarianceId)
        WHERE ReversesVarianceId IS NOT NULL;

/* UPDATE must be dynamic SQL: SQL Server compiles the whole batch before ALTER runs. */
IF COL_LENGTH(N'dbo.StockCostState', N'CurrentUnitCost') IS NULL
BEGIN
    ALTER TABLE dbo.StockCostState ADD CurrentUnitCost decimal(19,6) NOT NULL
        CONSTRAINT DF_StockCostState_CurrentUnitCost DEFAULT (0);
    EXEC(N'UPDATE dbo.StockCostState SET CurrentUnitCost = AverageUnitCost;');
END;

IF COL_LENGTH(N'dbo.PoInvoice', N'CostingRevision') IS NULL
    ALTER TABLE dbo.PoInvoice ADD CostingRevision int NOT NULL
        CONSTRAINT DF_PoInvoice_CostingRevision DEFAULT (0);

IF COL_LENGTH(N'dbo.PoCdn', N'CostingRevision') IS NULL
    ALTER TABLE dbo.PoCdn ADD CostingRevision int NOT NULL
        CONSTRAINT DF_PoCdn_CostingRevision DEFAULT (0);

IF COL_LENGTH(N'dbo.PoInvoiceDetail', N'ReferencedInvoiceLineNo') IS NULL
    ALTER TABLE dbo.PoInvoiceDetail ADD ReferencedInvoiceLineNo smallint NULL;

/* Structured manual inventory-cost evidence. Legacy PriceEvidence remains readable for history. */
IF COL_LENGTH(N'dbo.IvTrxBatchDetail', N'CostEvidenceType') IS NULL
    ALTER TABLE dbo.IvTrxBatchDetail ADD CostEvidenceType nvarchar(30) NULL;
IF COL_LENGTH(N'dbo.IvTrxBatchDetail', N'CostOverrideReason') IS NULL
    ALTER TABLE dbo.IvTrxBatchDetail ADD CostOverrideReason nvarchar(250) NULL;
IF COL_LENGTH(N'dbo.IvTrxBatchDetail', N'CostApprovedBy') IS NULL
    ALTER TABLE dbo.IvTrxBatchDetail ADD CostApprovedBy nvarchar(100) NULL;
IF COL_LENGTH(N'dbo.IvTrxBatchDetail', N'CostApprovedAtUtc') IS NULL
    ALTER TABLE dbo.IvTrxBatchDetail ADD CostApprovedAtUtc datetime2(7) NULL;

IF COL_LENGTH(N'dbo.IvTrxHistory', N'CostEvidenceType') IS NULL
    ALTER TABLE dbo.IvTrxHistory ADD CostEvidenceType nvarchar(30) NULL;
IF COL_LENGTH(N'dbo.IvTrxHistory', N'CostOverrideReason') IS NULL
    ALTER TABLE dbo.IvTrxHistory ADD CostOverrideReason nvarchar(250) NULL;
IF COL_LENGTH(N'dbo.IvTrxHistory', N'CostApprovedBy') IS NULL
    ALTER TABLE dbo.IvTrxHistory ADD CostApprovedBy nvarchar(100) NULL;
IF COL_LENGTH(N'dbo.IvTrxHistory', N'CostApprovedAtUtc') IS NULL
    ALTER TABLE dbo.IvTrxHistory ADD CostApprovedAtUtc datetime2(7) NULL;

IF OBJECT_ID(N'dbo.PurchaseReceiptCostSettlement', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PurchaseReceiptCostSettlement
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseReceiptCostSettlement PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        PiDocNo nvarchar(30) NOT NULL,
        PiLineNo smallint NOT NULL,
        PiCostingRevision int NOT NULL,
        PoNo nvarchar(50) NOT NULL,
        PoRelNo smallint NOT NULL,
        PoLineNo smallint NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        ReceiptValuationFactId bigint NOT NULL,
        ReceiptInventoryHistoryId int NULL,
        ReceiptBatchNo int NULL,
        SettledBaseQty decimal(19,6) NOT NULL,
        ReceiptCommercialUnitCost decimal(19,6) NOT NULL,
        ReceiptCommercialBaseAmount decimal(19,6) NOT NULL,
        ReceiptValuationUnitCost decimal(19,6) NOT NULL,
        ReceiptValuationBaseAmount decimal(19,6) NOT NULL,
        AllocatedActualBaseAmount decimal(19,6) NOT NULL,
        CommercialVarianceAmount decimal(19,6) NOT NULL,
        ValuationVarianceAmount decimal(19,6) NOT NULL,
        StockPostingId bigint NOT NULL,
        ReversesSettlementId bigint NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(100) NOT NULL,
        CONSTRAINT CK_PurchaseReceiptCostSettlement_Qty CHECK (SettledBaseQty > 0),
        CONSTRAINT CK_PurchaseReceiptCostSettlement_Amounts CHECK
            (ReceiptCommercialBaseAmount >= 0 AND ReceiptValuationBaseAmount >= 0 AND AllocatedActualBaseAmount >= 0),
        CONSTRAINT FK_PurchaseReceiptCostSettlement_ValuationFact
            FOREIGN KEY (ReceiptValuationFactId) REFERENCES dbo.StockValuationFact(Id),
        CONSTRAINT FK_PurchaseReceiptCostSettlement_Posting
            FOREIGN KEY (StockPostingId) REFERENCES dbo.StockPosting(Id),
        CONSTRAINT FK_PurchaseReceiptCostSettlement_Reversal
            FOREIGN KEY (ReversesSettlementId) REFERENCES dbo.PurchaseReceiptCostSettlement(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PurchaseReceiptCostSettlement_Receipt'
              AND object_id = OBJECT_ID(N'dbo.PurchaseReceiptCostSettlement'))
    CREATE INDEX IX_PurchaseReceiptCostSettlement_Receipt
        ON dbo.PurchaseReceiptCostSettlement
            (CompanyCode, BranchCode, PoNo, PoRelNo, PoLineNo, ReceiptValuationFactId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PurchaseReceiptCostSettlement_Invoice'
              AND object_id = OBJECT_ID(N'dbo.PurchaseReceiptCostSettlement'))
    CREATE INDEX IX_PurchaseReceiptCostSettlement_Invoice
        ON dbo.PurchaseReceiptCostSettlement
            (CompanyCode, BranchCode, PiDocNo, PiCostingRevision, PiLineNo);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PurchaseReceiptCostSettlement_Reversal'
              AND object_id = OBJECT_ID(N'dbo.PurchaseReceiptCostSettlement'))
    CREATE UNIQUE INDEX UX_PurchaseReceiptCostSettlement_Reversal
        ON dbo.PurchaseReceiptCostSettlement (ReversesSettlementId)
        WHERE ReversesSettlementId IS NOT NULL;

IF OBJECT_ID(N'dbo.PurchaseCostAdjustment', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PurchaseCostAdjustment
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PurchaseCostAdjustment PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        AdjustmentType nvarchar(30) NOT NULL,
        SourceDocumentType nvarchar(30) NOT NULL,
        SourceDocumentNo nvarchar(50) NOT NULL,
        SourceDocumentLine int NOT NULL,
        SourceCostingRevision int NOT NULL,
        PoNo nvarchar(50) NULL,
        PoRelNo smallint NULL,
        PoLineNo smallint NULL,
        ItemCode nvarchar(30) NOT NULL,
        CostMethod nvarchar(30) NOT NULL,
        BaseQty decimal(19,6) NOT NULL,
        ActualBaseAmount decimal(19,6) NOT NULL,
        ReferenceBaseAmount decimal(19,6) NOT NULL,
        CommercialReferenceAmount decimal(19,6) NULL,
        TotalAdjustmentAmount decimal(19,6) NOT NULL,
        InventoryAdjustmentAmount decimal(19,6) NOT NULL,
        ConsumedVarianceAmount decimal(19,6) NOT NULL,
        StockPostingId bigint NOT NULL,
        InventoryAdjustmentFactId bigint NULL,
        ReversesAdjustmentId bigint NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(100) NOT NULL,
        CONSTRAINT CK_PurchaseCostAdjustment_Qty CHECK (BaseQty >= 0),
        CONSTRAINT CK_PurchaseCostAdjustment_Amounts CHECK
            (ActualBaseAmount >= 0 AND ReferenceBaseAmount >= 0),
        CONSTRAINT FK_PurchaseCostAdjustment_Posting
            FOREIGN KEY (StockPostingId) REFERENCES dbo.StockPosting(Id),
        CONSTRAINT FK_PurchaseCostAdjustment_Fact
            FOREIGN KEY (InventoryAdjustmentFactId) REFERENCES dbo.StockValuationFact(Id),
        CONSTRAINT FK_PurchaseCostAdjustment_Reversal
            FOREIGN KEY (ReversesAdjustmentId) REFERENCES dbo.PurchaseCostAdjustment(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PurchaseCostAdjustment_Source'
              AND object_id = OBJECT_ID(N'dbo.PurchaseCostAdjustment'))
    CREATE INDEX IX_PurchaseCostAdjustment_Source
        ON dbo.PurchaseCostAdjustment
            (CompanyCode, BranchCode, SourceDocumentType, SourceDocumentNo, SourceCostingRevision);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PurchaseCostAdjustment_Reversal'
              AND object_id = OBJECT_ID(N'dbo.PurchaseCostAdjustment'))
    CREATE UNIQUE INDEX UX_PurchaseCostAdjustment_Reversal
        ON dbo.PurchaseCostAdjustment (ReversesAdjustmentId)
        WHERE ReversesAdjustmentId IS NOT NULL;

IF OBJECT_ID(N'dbo.SalesReturnCostAllocation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalesReturnCostAllocation
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesReturnCostAllocation PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        ReturnDocumentType nvarchar(40) NOT NULL,
        ReturnDocumentNo nvarchar(50) NOT NULL,
        ReturnDocumentLine int NOT NULL,
        ReturnCostingRevision int NOT NULL,
        OriginalValuationFactId bigint NOT NULL,
        OriginalOwnerType nvarchar(40) NOT NULL,
        OriginalOwnerDocumentNo nvarchar(50) NOT NULL,
        OriginalOwnerDocumentLine nvarchar(50) NULL,
        ReturnedBaseQty decimal(19,6) NOT NULL,
        ReturnedCostAmount decimal(19,6) NOT NULL,
        StockPostingId bigint NOT NULL,
        ReturnValuationFactId bigint NULL,
        ReversesAllocationId bigint NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(100) NOT NULL,
        CONSTRAINT CK_SalesReturnCostAllocation_Qty CHECK (ReturnedBaseQty > 0),
        CONSTRAINT CK_SalesReturnCostAllocation_Amount CHECK (ReturnedCostAmount >= 0),
        CONSTRAINT CK_SalesReturnCostAllocation_Line CHECK
            (ReturnDocumentLine > 0 AND ReturnCostingRevision >= 0),
        CONSTRAINT FK_SalesReturnCostAllocation_OriginalFact
            FOREIGN KEY (OriginalValuationFactId) REFERENCES dbo.StockValuationFact(Id),
        CONSTRAINT FK_SalesReturnCostAllocation_ReturnFact
            FOREIGN KEY (ReturnValuationFactId) REFERENCES dbo.StockValuationFact(Id),
        CONSTRAINT FK_SalesReturnCostAllocation_Posting
            FOREIGN KEY (StockPostingId) REFERENCES dbo.StockPosting(Id),
        CONSTRAINT FK_SalesReturnCostAllocation_Reversal
            FOREIGN KEY (ReversesAllocationId) REFERENCES dbo.SalesReturnCostAllocation(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalesReturnCostAllocation_Return'
              AND object_id = OBJECT_ID(N'dbo.SalesReturnCostAllocation'))
    CREATE INDEX IX_SalesReturnCostAllocation_Return
        ON dbo.SalesReturnCostAllocation
            (CompanyCode, BranchCode, ReturnDocumentType, ReturnDocumentNo,
             ReturnCostingRevision, ReturnDocumentLine);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalesReturnCostAllocation_Original'
              AND object_id = OBJECT_ID(N'dbo.SalesReturnCostAllocation'))
    CREATE INDEX IX_SalesReturnCostAllocation_Original
        ON dbo.SalesReturnCostAllocation
            (CompanyCode, BranchCode, OriginalValuationFactId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SalesReturnCostAllocation_Reversal'
              AND object_id = OBJECT_ID(N'dbo.SalesReturnCostAllocation'))
    CREATE UNIQUE INDEX UX_SalesReturnCostAllocation_Reversal
        ON dbo.SalesReturnCostAllocation (ReversesAllocationId)
        WHERE ReversesAllocationId IS NOT NULL;

IF OBJECT_ID(N'dbo.SalesReturnStandardCostVariance', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalesReturnStandardCostVariance
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SalesReturnStandardCostVariance PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        StockPostingId bigint NOT NULL,
        ReturnValuationFactId bigint NOT NULL,
        ReturnDocumentType nvarchar(30) NOT NULL,
        ReturnDocumentNo nvarchar(50) NOT NULL,
        ReturnDocumentLine int NOT NULL,
        ReturnCostingRevision int NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        BaseQty decimal(19,6) NOT NULL,
        CurrentStandardReceiptValue decimal(19,6) NOT NULL,
        OriginalCogsReversalValue decimal(19,6) NOT NULL,
        VarianceAmount decimal(19,6) NOT NULL,
        ReversesVarianceId bigint NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(100) NOT NULL,
        CONSTRAINT CK_SalesReturnStandardCostVariance_Qty CHECK (BaseQty > 0),
        CONSTRAINT FK_SalesReturnStandardCostVariance_Fact
            FOREIGN KEY (ReturnValuationFactId) REFERENCES dbo.StockValuationFact(Id),
        CONSTRAINT FK_SalesReturnStandardCostVariance_Reversal
            FOREIGN KEY (ReversesVarianceId) REFERENCES dbo.SalesReturnStandardCostVariance(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalesReturnStandardCostVariance_ReturnFact'
              AND object_id = OBJECT_ID(N'dbo.SalesReturnStandardCostVariance'))
    CREATE INDEX IX_SalesReturnStandardCostVariance_ReturnFact
        ON dbo.SalesReturnStandardCostVariance (CompanyCode, BranchCode, ReturnValuationFactId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SalesReturnStandardCostVariance_Reversal'
              AND object_id = OBJECT_ID(N'dbo.SalesReturnStandardCostVariance'))
    CREATE UNIQUE INDEX UQ_SalesReturnStandardCostVariance_Reversal
        ON dbo.SalesReturnStandardCostVariance (ReversesVarianceId)
        WHERE ReversesVarianceId IS NOT NULL;

/* Make the branch's existing V2 epoch explicit as MOVING_AVERAGE without changing facts. */
INSERT dbo.StockCostPolicyRevision
    (CompanyCode, BranchCode, CostMethod, EffectiveFrom, Status,
     ApprovedBy, ApprovedAtUtc, CreatedAtUtc, CreatedBy)
SELECT e.CompanyCode, e.BranchCode, N'MOVING_AVERAGE', CONVERT(date, e.EffectiveFrom), N'ACTIVE',
       N'SYSTEM', SYSUTCDATETIME(), SYSUTCDATETIME(), N'SYSTEM'
FROM dbo.StockLedgerEpoch e
WHERE e.Status = N'ACTIVE'
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.StockCostPolicyRevision p
      WHERE p.CompanyCode = e.CompanyCode
        AND p.BranchCode = e.BranchCode
        AND p.Status = N'ACTIVE'
        AND p.EffectiveFrom <= CONVERT(date, e.EffectiveFrom)
        AND (p.EffectiveTo IS NULL OR p.EffectiveTo > CONVERT(date, e.EffectiveFrom))
  );

COMMIT TRANSACTION;
