/* Additive replay lineage schema. Deploy while OpenPeriodBackdatedReplayEnabled is OFF.
   Rerunnable; existing posted rows are never changed by this script. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.StockPosting', N'U') IS NULL
    OR OBJECT_ID(N'dbo.IvStockCountLine', N'U') IS NULL
    OR OBJECT_ID(N'dbo.ItemStandardCostRevision', N'U') IS NULL
    OR OBJECT_ID(N'dbo.IvTrxHistory', N'U') IS NULL
    OR OBJECT_ID(N'dbo.IvTrxBatchDetail', N'U') IS NULL
    OR OBJECT_ID(N'dbo.PrProductionBalLotMovement', N'U') IS NULL
    OR OBJECT_ID(N'dbo.PrPoolDependency', N'U') IS NULL
    OR OBJECT_ID(N'dbo.PrProductionMovementAllocation', N'U') IS NULL
    OR OBJECT_ID(N'dbo.PrMaterialMovement', N'U') IS NULL
    OR OBJECT_ID(N'dbo.StockValuationFact', N'U') IS NULL
    THROW 51100, 'Install stock-ledger, count and costing schema before replay schema.', 1;
GO

IF OBJECT_ID(N'dbo.StockCountCostBasis', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockCountCostBasis
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockCountCostBasis PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        StockCountLineId int NOT NULL,
        FrozenFallbackUnitCost decimal(19,6) NOT NULL,
        CostBasisKind nvarchar(30) NOT NULL,
        StandardCostRevisionId bigint NULL,
        CostBasisWatermark bigint NULL,
        CostBasisHash char(64) NULL,
        CostEvidenceType nvarchar(30) NULL,
        CostOverrideReason nvarchar(250) NULL,
        CostApprovedBy nvarchar(100) NULL,
        CostApprovedAtUtc datetime2(7) NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CONSTRAINT UQ_StockCountCostBasis_Line UNIQUE (StockCountLineId),
        CONSTRAINT FK_StockCountCostBasis_Line FOREIGN KEY (StockCountLineId)
            REFERENCES dbo.IvStockCountLine(ID),
        CONSTRAINT FK_StockCountCostBasis_StandardRevision FOREIGN KEY (StandardCostRevisionId)
            REFERENCES dbo.ItemStandardCostRevision(Id),
        CONSTRAINT CK_StockCountCostBasis_Cost CHECK (FrozenFallbackUnitCost >= 0),
        CONSTRAINT CK_StockCountCostBasis_Kind CHECK (CostBasisKind IN
            (N'LEDGER_WAC', N'LEDGER_FIFO', N'STANDARD_REVISION', N'MANUAL_APPROVED', N'ZERO_COST_APPROVED')),
        CONSTRAINT CK_StockCountCostBasis_Provenance CHECK
        (
            (CostBasisKind IN (N'LEDGER_WAC', N'LEDGER_FIFO')
                AND CostBasisWatermark IS NOT NULL AND CostBasisHash IS NOT NULL)
            OR (CostBasisKind = N'STANDARD_REVISION' AND StandardCostRevisionId IS NOT NULL)
            OR (CostBasisKind IN (N'MANUAL_APPROVED', N'ZERO_COST_APPROVED')
                AND CostEvidenceType IS NOT NULL AND CostApprovedBy IS NOT NULL
                AND CostApprovedAtUtc IS NOT NULL AND CostOverrideReason IS NOT NULL)
        ),
        CONSTRAINT CK_StockCountCostBasis_Approval CHECK
        (
            (CostBasisKind = N'MANUAL_APPROVED' AND CostEvidenceType = N'MANUAL_APPROVED'
                AND FrozenFallbackUnitCost > 0 AND LEN(LTRIM(RTRIM(CostOverrideReason))) > 0)
            OR (CostBasisKind = N'ZERO_COST_APPROVED' AND CostEvidenceType = N'ZERO_COST_APPROVED'
                AND FrozenFallbackUnitCost = 0 AND LEN(LTRIM(RTRIM(CostOverrideReason))) > 0)
            OR CostBasisKind NOT IN (N'MANUAL_APPROVED', N'ZERO_COST_APPROVED')
        )
    );
END;

IF COL_LENGTH(N'dbo.IvTrxBatchDetail', N'StockCountCostBasisId') IS NULL
    ALTER TABLE dbo.IvTrxBatchDetail ADD StockCountCostBasisId bigint NULL;
IF COL_LENGTH(N'dbo.IvTrxHistory', N'StockCountCostBasisId') IS NULL
    ALTER TABLE dbo.IvTrxHistory ADD StockCountCostBasisId bigint NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_IvTrxBatchDetail_StockCountCostBasis')
    ALTER TABLE dbo.IvTrxBatchDetail ADD CONSTRAINT FK_IvTrxBatchDetail_StockCountCostBasis
        FOREIGN KEY (StockCountCostBasisId) REFERENCES dbo.StockCountCostBasis(Id);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_IvTrxHistory_StockCountCostBasis')
    ALTER TABLE dbo.IvTrxHistory ADD CONSTRAINT FK_IvTrxHistory_StockCountCostBasis
        FOREIGN KEY (StockCountCostBasisId) REFERENCES dbo.StockCountCostBasis(Id);
GO

IF OBJECT_ID(N'dbo.StockCountReplayGeneration', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockCountReplayGeneration
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockCountReplayGeneration PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        StockCountLineId int NOT NULL,
        AnchorPostingId bigint NOT NULL,
        WritePostingId bigint NOT NULL,
        PreviousGenerationId bigint NULL,
        EffectiveAt datetime2(7) NOT NULL,
        AnchorPostingSequence bigint NOT NULL,
        SignedAdjustmentQty decimal(18,4) NOT NULL,
        AdjustmentBatchNo int NULL,
        AdjustmentHistoryId int NULL,
        ReversalBatchNo int NULL,
        ReversalHistoryId int NULL,
        IsRemoval bit NOT NULL CONSTRAINT DF_StockCountReplayGeneration_IsRemoval DEFAULT (0),
        CreatedAtUtc datetime2(7) NOT NULL,
        CONSTRAINT AK_StockCountReplayGeneration_TenantLineId
            UNIQUE (CompanyCode, BranchCode, StockCountLineId, Id),
        CONSTRAINT FK_StockCountReplayGeneration_Line FOREIGN KEY (StockCountLineId)
            REFERENCES dbo.IvStockCountLine(ID),
        CONSTRAINT FK_StockCountReplayGeneration_Anchor FOREIGN KEY (CompanyCode, BranchCode, AnchorPostingId)
            REFERENCES dbo.StockPosting(CompanyCode, BranchCode, Id),
        CONSTRAINT FK_StockCountReplayGeneration_Writer FOREIGN KEY (CompanyCode, BranchCode, WritePostingId)
            REFERENCES dbo.StockPosting(CompanyCode, BranchCode, Id),
        CONSTRAINT FK_StockCountReplayGeneration_Previous FOREIGN KEY
            (CompanyCode, BranchCode, StockCountLineId, PreviousGenerationId)
            REFERENCES dbo.StockCountReplayGeneration(CompanyCode, BranchCode, StockCountLineId, Id),
        CONSTRAINT FK_StockCountReplayGeneration_Adjustment FOREIGN KEY (AdjustmentHistoryId)
            REFERENCES dbo.IvTrxHistory(ID),
        CONSTRAINT FK_StockCountReplayGeneration_Reversal FOREIGN KEY (ReversalHistoryId)
            REFERENCES dbo.IvTrxHistory(ID),
        CONSTRAINT CK_StockCountReplayGeneration_Adjustment CHECK
            ((SignedAdjustmentQty = 0 AND AdjustmentHistoryId IS NULL)
             OR (SignedAdjustmentQty <> 0 AND AdjustmentHistoryId IS NOT NULL)),
        CONSTRAINT CK_StockCountReplayGeneration_Batches CHECK
            (((AdjustmentBatchNo IS NULL AND AdjustmentHistoryId IS NULL)
              OR (AdjustmentBatchNo IS NOT NULL AND AdjustmentHistoryId IS NOT NULL))
             AND ((ReversalBatchNo IS NULL AND ReversalHistoryId IS NULL)
              OR (ReversalBatchNo IS NOT NULL AND ReversalHistoryId IS NOT NULL))),
        CONSTRAINT CK_StockCountReplayGeneration_Removal CHECK
            (IsRemoval = 0 OR (SignedAdjustmentQty = 0 AND AdjustmentHistoryId IS NULL))
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockCountReplayGeneration') AND name = N'UQ_StockCountReplayGeneration_Child')
    CREATE UNIQUE INDEX UQ_StockCountReplayGeneration_Child ON dbo.StockCountReplayGeneration(PreviousGenerationId)
        WHERE PreviousGenerationId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockCountReplayGeneration') AND name = N'UQ_StockCountReplayGeneration_Initial')
    CREATE UNIQUE INDEX UQ_StockCountReplayGeneration_Initial ON dbo.StockCountReplayGeneration(StockCountLineId)
        WHERE PreviousGenerationId IS NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockCountReplayGeneration') AND name = N'IX_StockCountReplayGeneration_Line')
    CREATE INDEX IX_StockCountReplayGeneration_Line ON dbo.StockCountReplayGeneration(StockCountLineId, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockCountReplayGeneration') AND name = N'UQ_StockCountReplayGeneration_AdjustmentHistory')
    CREATE UNIQUE INDEX UQ_StockCountReplayGeneration_AdjustmentHistory ON dbo.StockCountReplayGeneration(AdjustmentHistoryId)
        WHERE AdjustmentHistoryId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockCountReplayGeneration') AND name = N'UQ_StockCountReplayGeneration_ReversalHistory')
    CREATE UNIQUE INDEX UQ_StockCountReplayGeneration_ReversalHistory ON dbo.StockCountReplayGeneration(ReversalHistoryId)
        WHERE ReversalHistoryId IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockCountReplayGeneration') AND name = N'IX_StockCountReplayGeneration_Chronology')
    CREATE INDEX IX_StockCountReplayGeneration_Chronology
        ON dbo.StockCountReplayGeneration(CompanyCode, BranchCode, EffectiveAt, AnchorPostingSequence, StockCountLineId);
GO

IF OBJECT_ID(N'dbo.StockReplayRun', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockReplayRun
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockReplayRun PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        RootStockPostingId bigint NOT NULL,
        RootRequestId uniqueidentifier NOT NULL,
        RootDocumentType nvarchar(40) NOT NULL,
        RootDocumentId nvarchar(64) NOT NULL,
        EarliestEffectiveAt datetime2(7) NOT NULL,
        LatestEffectiveAt datetime2(7) NOT NULL,
        PlanWatermark bigint NOT NULL,
        PlanHash char(64) NOT NULL,
        AffectedHistoryCount int NOT NULL,
        AffectedItemCount int NOT NULL,
        AffectedProductionMovementCount int NOT NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(100) NOT NULL,
        CONSTRAINT AK_StockReplayRun_TenantId UNIQUE (CompanyCode, BranchCode, Id),
        CONSTRAINT UQ_StockReplayRun_Root UNIQUE (RootStockPostingId),
        CONSTRAINT FK_StockReplayRun_Root FOREIGN KEY (CompanyCode, BranchCode, RootStockPostingId)
            REFERENCES dbo.StockPosting(CompanyCode, BranchCode, Id),
        CONSTRAINT CK_StockReplayRun_Range CHECK (EarliestEffectiveAt <= LatestEffectiveAt),
        CONSTRAINT CK_StockReplayRun_Counts CHECK
            (AffectedHistoryCount >= 0 AND AffectedItemCount >= 0 AND AffectedProductionMovementCount >= 0)
    );
END;
GO

IF OBJECT_ID(N'dbo.StockReplayEffect', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockReplayEffect
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockReplayEffect PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        ReplayRunId bigint NOT NULL,
        RootStockPostingId bigint NOT NULL,
        BusinessOwnerPostingId bigint NOT NULL,
        EffectKind nvarchar(40) NOT NULL,
        AffectedEntityType nvarchar(40) NOT NULL,
        AffectedEntityId bigint NOT NULL,
        OriginalEntityId bigint NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CONSTRAINT FK_StockReplayEffect_Run FOREIGN KEY (CompanyCode, BranchCode, ReplayRunId)
            REFERENCES dbo.StockReplayRun(CompanyCode, BranchCode, Id),
        CONSTRAINT FK_StockReplayEffect_Root FOREIGN KEY (CompanyCode, BranchCode, RootStockPostingId)
            REFERENCES dbo.StockPosting(CompanyCode, BranchCode, Id),
        CONSTRAINT FK_StockReplayEffect_Owner FOREIGN KEY (CompanyCode, BranchCode, BusinessOwnerPostingId)
            REFERENCES dbo.StockPosting(CompanyCode, BranchCode, Id),
        CONSTRAINT UQ_StockReplayEffect_Entity
            UNIQUE (ReplayRunId, EffectKind, AffectedEntityType, AffectedEntityId)
    );
END;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockReplayEffect') AND name = N'IX_StockReplayEffect_Owner')
    CREATE INDEX IX_StockReplayEffect_Owner
        ON dbo.StockReplayEffect(CompanyCode, BranchCode, BusinessOwnerPostingId, EffectKind);
GO

IF OBJECT_ID(N'dbo.ProductionCostRestatementFact', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductionCostRestatementFact
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProductionCostRestatementFact PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        ReplayRequestId uniqueidentifier NOT NULL,
        ReplayStockPostingId bigint NOT NULL,
        ProductionBalLotMovementId bigint NOT NULL,
        EffectiveAt datetime2(7) NOT NULL,
        PriorEffectiveTotalCost decimal(19,6) NOT NULL,
        CorrectedEffectiveTotalCost decimal(19,6) NOT NULL,
        CorrectedUnitCost decimal(19,6) NOT NULL,
        DeltaCost decimal(19,6) NOT NULL,
        ReversesRestatementFactId bigint NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(100) NOT NULL,
        CONSTRAINT FK_ProductionCostRestatementFact_Posting
            FOREIGN KEY (CompanyCode, BranchCode, ReplayStockPostingId)
            REFERENCES dbo.StockPosting(CompanyCode, BranchCode, Id),
        CONSTRAINT FK_ProductionCostRestatementFact_Movement FOREIGN KEY (ProductionBalLotMovementId)
            REFERENCES dbo.PrProductionBalLotMovement(Uid),
        CONSTRAINT FK_ProductionCostRestatementFact_Reversal FOREIGN KEY (ReversesRestatementFactId)
            REFERENCES dbo.ProductionCostRestatementFact(Id),
        CONSTRAINT CK_ProductionCostRestatementFact_Delta CHECK
            (DeltaCost = CorrectedEffectiveTotalCost - PriorEffectiveTotalCost)
    );
END;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ProductionCostRestatementFact') AND name = N'IX_ProductionCostRestatementFact_Movement')
    CREATE INDEX IX_ProductionCostRestatementFact_Movement
        ON dbo.ProductionCostRestatementFact(ProductionBalLotMovementId, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ProductionCostRestatementFact') AND name = N'IX_ProductionCostRestatementFact_Root')
    CREATE INDEX IX_ProductionCostRestatementFact_Root
        ON dbo.ProductionCostRestatementFact(ReplayStockPostingId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ProductionCostRestatementFact') AND name = N'UQ_ProductionCostRestatementFact_Reversal')
    CREATE UNIQUE INDEX UQ_ProductionCostRestatementFact_Reversal
        ON dbo.ProductionCostRestatementFact(ReversesRestatementFactId)
        WHERE ReversesRestatementFactId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockPosting') AND name = N'IX_StockPosting_ReplayChronology')
    CREATE INDEX IX_StockPosting_ReplayChronology
        ON dbo.StockPosting(CompanyCode, BranchCode, EffectiveAt, PostingSequence);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.IvTrxHistory') AND name = N'IX_IvTrxHistory_ReplayFromPile')
    CREATE INDEX IX_IvTrxHistory_ReplayFromPile
        ON dbo.IvTrxHistory(CompanyCode, BranchCode, FromBalLocId, TrxDtTime, ID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.IvTrxHistory') AND name = N'IX_IvTrxHistory_ReplayToPile')
    CREATE INDEX IX_IvTrxHistory_ReplayToPile
        ON dbo.IvTrxHistory(CompanyCode, BranchCode, ToBalLocId, TrxDtTime, ID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.IvStockCountLine') AND name = N'IX_IvStockCountLine_ReplayPile')
    CREATE INDEX IX_IvStockCountLine_ReplayPile
        ON dbo.IvStockCountLine(BalLocId, StockCountId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockValuationFact') AND name = N'IX_StockValuationFact_ReplayOwnerLine')
    CREATE INDEX IX_StockValuationFact_ReplayOwnerLine
        ON dbo.StockValuationFact(CompanyCode, BranchCode, SourceDocumentType,
            SourceDocumentNo, SourceDocumentLine, ItemCode);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrPoolDependency') AND name = N'IX_PrPoolDependency_ContributorConsumer')
    CREATE INDEX IX_PrPoolDependency_ContributorConsumer
        ON dbo.PrPoolDependency(ContributorMovementId, ConsumerMovementId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionMovementAllocation') AND name = N'IX_PrProductionMovementAllocation_ReceiptOutbound')
    CREATE INDEX IX_PrProductionMovementAllocation_ReceiptOutbound
        ON dbo.PrProductionMovementAllocation(ReceiptMovementId, OutboundMovementId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionMovementAllocation') AND name = N'IX_PrProductionMovementAllocation_OutboundReceipt')
    CREATE INDEX IX_PrProductionMovementAllocation_OutboundReceipt
        ON dbo.PrProductionMovementAllocation(OutboundMovementId, ReceiptMovementId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement') AND name = N'IX_PrMaterialMovement_InventoryHistory')
    CREATE INDEX IX_PrMaterialMovement_InventoryHistory
        ON dbo.PrMaterialMovement(CompanyCode, BranchCode, InventoryHistoryID);
GO

/* Each replay evidence insert must belong to the unsealed posting in this SQL session.
   Application writes cannot update or delete immutable generations; audited migration
   backfill must use a separate privileged deployment procedure. */
DECLARE @ReplayTables table (TableName sysname NOT NULL PRIMARY KEY);
INSERT INTO @ReplayTables (TableName) VALUES
    (N'StockCountCostBasis'), (N'StockCountReplayGeneration'),
    (N'StockReplayRun'), (N'StockReplayEffect'), (N'ProductionCostRestatementFact');

DECLARE @ReplayTable sysname;
DECLARE @ReplaySql nvarchar(max);
DECLARE replay_table_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT TableName FROM @ReplayTables ORDER BY TableName;
OPEN replay_table_cursor;
FETCH NEXT FROM replay_table_cursor INTO @ReplayTable;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @ReplaySql = N'CREATE OR ALTER TRIGGER dbo.' + QUOTENAME(N'TR_' + @ReplayTable + N'_ReplayWriteGuard')
        + N' ON dbo.' + QUOTENAME(@ReplayTable) + N' AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted)
        THROW 51101, ''Replay evidence is append-only.'', 1;
    IF EXISTS
    (
        SELECT 1 FROM inserted AS evidence
        WHERE NOT EXISTS
        (
            SELECT 1 FROM dbo.StockPosting AS posting
            INNER JOIN dbo.StockLedgerEpoch AS epoch
                ON epoch.Id = posting.LedgerEpochId
               AND epoch.CompanyCode = posting.CompanyCode
               AND epoch.BranchCode = posting.BranchCode
            WHERE posting.Id = TRY_CONVERT(bigint, SESSION_CONTEXT(N''STOCK_LEDGER_V2_POSTING_ID''))
              AND posting.CompanyCode = evidence.CompanyCode
              AND posting.BranchCode = evidence.BranchCode
              AND epoch.Status = N''ACTIVE'' AND epoch.Version = 2
              AND posting.SealedAtUtc IS NULL
        )
    )
        THROW 51102, ''Replay evidence requires the active unsealed V2 root write context.'', 1;
END';
    EXEC sys.sp_executesql @ReplaySql;
    FETCH NEXT FROM replay_table_cursor INTO @ReplayTable;
END;
CLOSE replay_table_cursor;
DEALLOCATE replay_table_cursor;
GO

/* A posted generation freezes the physical observation and its dated sheet
   identity. Status and rollback audit fields on the header remain mutable. */
CREATE OR ALTER TRIGGER dbo.TR_IvStockCountLine_ReplayObservationGuard
ON dbo.IvStockCountLine AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
        SELECT 1 FROM deleted d
        JOIN dbo.StockCountReplayGeneration g ON g.StockCountLineId = d.ID
    )
        THROW 51111, 'A count line with a posted generation is immutable.', 1;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_IvStockCountHdr_ReplayIdentityGuard
ON dbo.IvStockCountHdr AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
        SELECT 1 FROM deleted d
        JOIN dbo.IvStockCountLine l ON l.StockCountId = d.ID
        JOIN dbo.StockCountReplayGeneration g ON g.StockCountLineId = l.ID
        LEFT JOIN inserted i ON i.ID = d.ID
        WHERE i.ID IS NULL OR i.CompanyCode <> d.CompanyCode
           OR i.BranchCode <> d.BranchCode OR i.CountNo <> d.CountNo
           OR i.CountDate <> d.CountDate
           OR ISNULL(i.WHCode, N'') <> ISNULL(d.WHCode, N'')
           OR ISNULL(i.LocCode, N'') <> ISNULL(d.LocCode, N'')
           OR ISNULL(i.IClassCode, N'') <> ISNULL(d.IClassCode, N'')
           OR ISNULL(i.ISubClassCode, N'') <> ISNULL(d.ISubClassCode, N'')
           OR ISNULL(i.IType, N'') <> ISNULL(d.IType, N'')
           OR ISNULL(i.IStatus, N'') <> ISNULL(d.IStatus, N'')
           OR ISNULL(i.ICodeList, N'') <> ISNULL(d.ICodeList, N'')
           OR i.IncludeZeroQty <> d.IncludeZeroQty
    )
        THROW 51112, 'A posted count sheet cannot change its dated scope or identity.', 1;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_StockCountCostBasis_Tenant
ON dbo.StockCountCostBasis AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
        SELECT 1 FROM inserted b
        JOIN dbo.IvStockCountLine l ON l.ID = b.StockCountLineId
        JOIN dbo.IvStockCountHdr h ON h.ID = l.StockCountId
        LEFT JOIN dbo.ItemStandardCostRevision s ON s.Id = b.StandardCostRevisionId
        WHERE h.CompanyCode <> b.CompanyCode OR h.BranchCode <> b.BranchCode
           OR (b.StandardCostRevisionId IS NOT NULL AND
               (s.CompanyCode <> b.CompanyCode OR s.BranchCode <> b.BranchCode
                OR s.ItemCode <> l.ICode OR s.Status <> N'APPROVED'
                OR s.EffectiveFrom > h.CountDate
                OR (s.EffectiveTo IS NOT NULL AND s.EffectiveTo < h.CountDate)))
    )
        THROW 51103, 'Count cost basis tenant, item, or approved standard revision is invalid.', 1;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_StockCountReplayGeneration_Tenant
ON dbo.StockCountReplayGeneration AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
        SELECT 1 FROM inserted g
        JOIN dbo.IvStockCountLine l ON l.ID = g.StockCountLineId
        JOIN dbo.IvStockCountHdr h ON h.ID = l.StockCountId
        JOIN dbo.StockPosting anchor ON anchor.Id = g.AnchorPostingId
        LEFT JOIN dbo.IvTrxBatch legacyBatch
            ON legacyBatch.CompanyCode = h.CompanyCode
           AND legacyBatch.BranchCode = h.BranchCode
           AND legacyBatch.BatchNo = h.PostedBatchNo
        WHERE h.CompanyCode <> g.CompanyCode OR h.BranchCode <> g.BranchCode
           OR g.WritePostingId <> TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))
           OR anchor.PostingSequence <> g.AnchorPostingSequence
           OR NOT
           (
               (anchor.SourceDocumentType = N'IV_STOCK_COUNT'
                    AND anchor.SourceModule = N'INVENTORY'
                    AND anchor.SourceDocumentId = CONVERT(nvarchar(64), h.ID)
                    AND anchor.SourceDocumentNo = h.CountNo
                    AND anchor.EffectiveAt = g.EffectiveAt)
               OR (anchor.SourceModule = N'INVENTORY'
                    AND anchor.SourceDocumentType = N'ADJ'
                    AND anchor.SealedAtUtc IS NOT NULL
                    AND anchor.BusinessDate = CONVERT(date, h.CountDate)
                    AND anchor.SourceDocumentId = CONVERT(nvarchar(64), legacyBatch.ID)
                    AND legacyBatch.RefNo = h.CountNo)
           )
           OR g.EffectiveAt <> DATEADD(NANOSECOND, -100,
                DATEADD(DAY, 1, CONVERT(datetime2(7), CONVERT(date, h.CountDate))))
    )
        THROW 51104, 'Count generation identity or end-of-day anchor is invalid.', 1;
    IF EXISTS
    (
        SELECT 1 FROM inserted g
        JOIN dbo.IvStockCountLine l ON l.ID = g.StockCountLineId
        JOIN dbo.IvTrxHistory a ON a.ID = g.AdjustmentHistoryId
        WHERE a.CompanyCode <> g.CompanyCode OR a.BranchCode <> g.BranchCode
           OR a.StockPostingID <> g.WritePostingId
           OR a.BatchNo <> g.AdjustmentBatchNo
           OR a.LedgerVersion <> 2 OR a.BatchStatus <> N'POSTED'
           OR a.ICode <> l.ICode
           OR ISNULL(a.EntryRole, N'') <> N'ORIGINAL' OR a.TrxType <> N'ADJ'
           OR a.ReversesHistoryID IS NOT NULL
           OR (g.SignedAdjustmentQty > 0 AND
               (ISNULL(a.ToBalLocId, 0) <> l.BalLocId
                OR a.FromBalLocId IS NOT NULL OR a.FrStdQty IS NOT NULL
                OR ISNULL(a.ToStdQty, -1) <> g.SignedAdjustmentQty))
           OR (g.SignedAdjustmentQty < 0 AND
               (ISNULL(a.FromBalLocId, 0) <> l.BalLocId
                OR a.ToBalLocId IS NOT NULL OR a.ToStdQty IS NOT NULL
                OR ISNULL(a.FrStdQty, -1) <> -g.SignedAdjustmentQty))
    )
        THROW 51107, 'Count adjustment history does not match the immutable counted pile and quantity.', 1;
    IF EXISTS
    (
        SELECT 1 FROM inserted g
        JOIN dbo.IvTrxHistory r ON r.ID = g.ReversalHistoryId
        JOIN dbo.StockCountReplayGeneration p ON p.Id = g.PreviousGenerationId
        JOIN dbo.IvTrxHistory a ON a.ID = p.AdjustmentHistoryId
        WHERE r.CompanyCode <> g.CompanyCode OR r.BranchCode <> g.BranchCode
           OR r.StockPostingID <> g.WritePostingId
           OR r.BatchNo <> g.ReversalBatchNo
           OR r.LedgerVersion <> 2 OR r.BatchStatus <> N'POSTED'
           OR r.ICode <> a.ICode OR r.TrxType <> N'ADJ'
           OR ISNULL(r.EntryRole, N'') <> N'REVERSAL'
           OR ISNULL(r.ReversesHistoryId, 0) <> p.AdjustmentHistoryId
           OR ISNULL(r.FromBalLocId, 0) <> ISNULL(a.ToBalLocId, 0)
           OR ISNULL(r.ToBalLocId, 0) <> ISNULL(a.FromBalLocId, 0)
           OR ISNULL(r.FrStdQty, 0) <> ISNULL(a.ToStdQty, 0)
           OR ISNULL(r.ToStdQty, 0) <> ISNULL(a.FrStdQty, 0)
           OR ISNULL(r.StockCountCostBasisId, 0) <> ISNULL(a.StockCountCostBasisId, 0)
    )
        THROW 51108, 'Count reversal history does not reverse the prior generation.', 1;
    IF EXISTS
    (
        SELECT 1 FROM inserted g
        JOIN dbo.StockCountReplayGeneration p ON p.Id = g.PreviousGenerationId
        WHERE p.EffectiveAt <> g.EffectiveAt
           OR (p.IsRemoval = 0 AND
               (p.AnchorPostingId <> g.AnchorPostingId
                OR p.AnchorPostingSequence <> g.AnchorPostingSequence))
           OR (p.IsRemoval = 1 AND
               (p.AnchorPostingId = g.AnchorPostingId
                OR p.AnchorPostingSequence >= g.AnchorPostingSequence
                OR g.IsRemoval = 1))
           OR (p.AdjustmentHistoryId IS NULL AND g.ReversalHistoryId IS NOT NULL)
           OR (p.AdjustmentHistoryId IS NOT NULL AND g.ReversalHistoryId IS NULL)
    )
        THROW 51110, 'Count correction/repost changes its anchor illegally or omits a required reversal.', 1;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_StockReplayRun_Identity
ON dbo.StockReplayRun AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
        SELECT 1 FROM inserted run
        JOIN dbo.StockPosting root ON root.Id = run.RootStockPostingId
        WHERE run.RootStockPostingId <> TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))
           OR run.RootRequestId <> root.RequestId
           OR run.RootDocumentType <> root.SourceDocumentType
           OR run.RootDocumentId <> root.SourceDocumentId
    )
        THROW 51109, 'Replay run does not match its active root posting.', 1;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_StockReplayEffect_Ownership
ON dbo.StockReplayEffect AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
        SELECT 1 FROM inserted effect
        JOIN dbo.StockReplayRun run ON run.Id = effect.ReplayRunId
        JOIN dbo.StockPosting owner ON owner.Id = effect.BusinessOwnerPostingId
        WHERE run.RootStockPostingId <> effect.RootStockPostingId
           OR effect.RootStockPostingId <> TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))
           OR (owner.SealedAtUtc IS NULL AND owner.Id <> effect.RootStockPostingId)
    )
        THROW 51105, 'Replay effect root or business owner is invalid.', 1;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_ProductionCostRestatementFact_Tenant
ON dbo.ProductionCostRestatementFact AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
        SELECT 1 FROM inserted restatement
        JOIN dbo.PrProductionBalLotMovement movement
            ON movement.UID = restatement.ProductionBalLotMovementId
        WHERE restatement.ReplayStockPostingId <> TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))
           OR movement.CompanyCode <> restatement.CompanyCode
           OR movement.BranchCode <> restatement.BranchCode
    )
        THROW 51106, 'Production cost restatement tenant differs from its movement.', 1;
END;
GO
