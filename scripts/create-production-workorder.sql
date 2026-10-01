-- Production Work Order schema (Phase 1 foundation + Milestone 1 snapshot hierarchy).
-- Manual DBA script; idempotent for clean installs and safe to re-run.
-- No inventory movement is performed by this schema or the Release command.
--
-- Delete-graph contract (plan §6.6). SQL Server validates cascade paths against the schema
-- structure, so every descendant keeps at most ONE structural cascade path from PrWorkOrder:
--
--   PrWorkOrder --CASCADE--> PrWorkOrderRouteStep --CASCADE--> PrWorkOrderOperation
--                 PrWorkOrderOperation --CASCADE--> PrWorkOrderMaterial
--                 PrWorkOrderOperation --CASCADE--> PrWorkOrderMachine --CASCADE--> PrWorkOrderLabour
--                 PrWorkOrderOperation --NO ACTION--> PrWorkOrderLabour   (service deletes explicitly)
--
-- The direct WorkOrderID foreign keys on PrWorkOrderMaterial / PrWorkOrderOperation exist for
-- querying and for browsing legacy version-1 rows, but they are ON DELETE NO ACTION. If they
-- cascaded, PrWorkOrder would reach those tables by two paths and SQL Server would reject the
-- schema with a multiple-cascade-path error.

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.PrBomHdr', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrDefBOM', N'U') IS NULL
    THROW 51000, 'dbo.PrBomHdr and dbo.PrDefBOM are required. Run create-prdefbom.sql first.', 1;
GO

/* ══════════════════════════════════════════════════════════════════════════════════════════════
   PrWorkOrder (header)
   ══════════════════════════════════════════════════════════════════════════════════════════════ */

IF OBJECT_ID(N'dbo.PrWorkOrder', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkOrder
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrWorkOrder PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        LocationCode nvarchar(10) NULL,
        WorkOrderNo nvarchar(30) NOT NULL,
        SnapshotRevision int NOT NULL CONSTRAINT DF_PrWorkOrder_SnapshotRevision DEFAULT (1),
        SnapshotHash char(64) NOT NULL,
        -- DefinitionEffectiveDate. Physically named SnapshotAsOfDate: compatibility mapping, not a
        -- duplicate column (plan §6.6).
        SnapshotAsOfDate date NOT NULL,
        ProductCode nvarchar(30) NOT NULL,
        ProductDescription nvarchar(200) NULL,
        OutputUOM nvarchar(10) NULL,
        SourceDefinitionCode nvarchar(30) NOT NULL,
        SourceDefinitionName nvarchar(100) NULL,
        SourceBomHdrID bigint NOT NULL,
        SourceBomVersion int NOT NULL,
        BomBaseQty decimal(18,4) NOT NULL,
        BomBaseUOM nvarchar(10) NULL,
        PlannedQty decimal(18,4) NOT NULL,
        GoodQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrder_GoodQty DEFAULT (0),
        ScrapQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrder_ScrapQty DEFAULT (0),
        RejectQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrder_RejectQty DEFAULT (0),
        HoldQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrder_HoldQty DEFAULT (0),
        ApprovedVarianceQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrder_ApprovedVarianceQty DEFAULT (0),
        RemainingQty decimal(18,4) NOT NULL,
        -- Plant-local scheduling timestamps keep time-of-day (plan §6.5).
        PlannedStartDateTime datetime2 NOT NULL,
        PlannedCompletionDateTime datetime2 NOT NULL,
        SchedulingDirection nvarchar(10) NOT NULL,
        Status nvarchar(20) NOT NULL,
        SourceType nvarchar(30) NOT NULL,
        SourceReference nvarchar(80) NULL,
        Remark nvarchar(1000) NULL,
        -- Snapshot provenance (plan §6.6).
        SourceEffectiveFrom datetime2 NULL,
        SourceProductDefinitionRevisionID bigint NULL,
        DefinitionSourceHash char(64) NULL,
        DefinitionSourceHashVersion int NULL,
        SnapshotFormatVersion int NOT NULL CONSTRAINT DF_PrWorkOrder_SnapshotFormatVersion DEFAULT (1),
        SnapshotHashVersion int NOT NULL CONSTRAINT DF_PrWorkOrder_SnapshotHashVersion DEFAULT (1),
        IsLegacySnapshot bit NOT NULL CONSTRAINT DF_PrWorkOrder_IsLegacySnapshot DEFAULT (1),
        LegacySnapshotReason nvarchar(500) NULL,
        ScheduleAnchorDateTime datetime2 NULL,
        ScheduleCalculationTrace nvarchar(max) NULL,
        ReleasedDate datetime2 NULL,
        ReleasedBy nvarchar(10) NULL,
        CancelledDate datetime2 NULL,
        CancelledBy nvarchar(10) NULL,
        CancellationReason nvarchar(500) NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_PrWorkOrder_PrBomHdr
            FOREIGN KEY (SourceBomHdrID) REFERENCES dbo.PrBomHdr (UID),
        CONSTRAINT CK_PrWorkOrder_PlannedQty CHECK (PlannedQty > 0),
        CONSTRAINT CK_PrWorkOrder_DateRange CHECK (PlannedCompletionDateTime >= PlannedStartDateTime),
        CONSTRAINT CK_PrWorkOrder_Direction CHECK (SchedulingDirection IN (N'FORWARD', N'BACKWARD')),
        CONSTRAINT CK_PrWorkOrder_Status CHECK
            (Status IN (N'DRAFT', N'RELEASED', N'IN_PROGRESS', N'COMPLETED', N'CLOSED', N'CANCELLED')),
        CONSTRAINT CK_PrWorkOrder_SnapshotFormat CHECK (SnapshotFormatVersion IN (1, 2, 3))
    );

    CREATE UNIQUE INDEX UQ_PrWorkOrder_Company_WorkOrderNo
        ON dbo.PrWorkOrder (CompanyCode, WorkOrderNo);
    CREATE INDEX IX_PrWorkOrder_Company_Branch_Status_Start
        ON dbo.PrWorkOrder (CompanyCode, BranchCode, Status, PlannedStartDateTime);
    CREATE INDEX IX_PrWorkOrder_Company_Branch_Product
        ON dbo.PrWorkOrder (CompanyCode, BranchCode, ProductCode);
    CREATE INDEX IX_PrWorkOrder_Company_Product_Definition
        ON dbo.PrWorkOrder (CompanyCode, ProductCode, SourceDefinitionCode);
    CREATE INDEX IX_PrWorkOrder_SourceBomHdrID
        ON dbo.PrWorkOrder (SourceBomHdrID);
    CREATE INDEX IX_PrWorkOrder_Company_SnapshotFormat
        ON dbo.PrWorkOrder (CompanyCode, SnapshotFormatVersion);
END;
GO

-- ── Phase-1 header upgrade: date -> datetime2 and the Milestone 1 provenance columns ──────────
-- Drop enforced dependents before sp_rename (CHECK / indexes block column renames on SQL Server).
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrWorkOrder_DateRange')
    ALTER TABLE dbo.PrWorkOrder DROP CONSTRAINT CK_PrWorkOrder_DateRange;
GO

IF EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrder')
      AND name = N'IX_PrWorkOrder_Company_Branch_Status_Start'
      AND EXISTS
      (
          SELECT 1 FROM sys.index_columns ic
          INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
          WHERE ic.object_id = OBJECT_ID(N'dbo.PrWorkOrder')
            AND ic.index_id = index_id
            AND c.name IN (N'PlannedStartDate', N'PlannedStartDateTime')
      )
)
    DROP INDEX IX_PrWorkOrder_Company_Branch_Status_Start ON dbo.PrWorkOrder;
GO

IF COL_LENGTH(N'dbo.PrWorkOrder', N'PlannedStartDateTime') IS NULL
   AND COL_LENGTH(N'dbo.PrWorkOrder', N'PlannedStartDate') IS NOT NULL
BEGIN
    EXEC sp_rename N'dbo.PrWorkOrder.PlannedStartDate', N'PlannedStartDateTime', N'COLUMN';
END;
GO

IF COL_LENGTH(N'dbo.PrWorkOrder', N'PlannedCompletionDateTime') IS NULL
   AND COL_LENGTH(N'dbo.PrWorkOrder', N'PlannedCompletionDate') IS NOT NULL
BEGIN
    EXEC sp_rename N'dbo.PrWorkOrder.PlannedCompletionDate', N'PlannedCompletionDateTime', N'COLUMN';
END;
GO

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrder')
      AND name IN (N'PlannedStartDateTime', N'PlannedCompletionDateTime')
      AND system_type_id <> TYPE_ID(N'datetime2')
)
BEGIN
    ALTER TABLE dbo.PrWorkOrder ALTER COLUMN PlannedStartDateTime datetime2 NOT NULL;
    ALTER TABLE dbo.PrWorkOrder ALTER COLUMN PlannedCompletionDateTime datetime2 NOT NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrWorkOrder_DateRange')
   AND COL_LENGTH(N'dbo.PrWorkOrder', N'PlannedStartDateTime') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrWorkOrder', N'PlannedCompletionDateTime') IS NOT NULL
    ALTER TABLE dbo.PrWorkOrder WITH CHECK
        ADD CONSTRAINT CK_PrWorkOrder_DateRange
            CHECK (PlannedCompletionDateTime >= PlannedStartDateTime);
GO

IF COL_LENGTH(N'dbo.PrWorkOrder', N'SourceEffectiveFrom') IS NULL
    ALTER TABLE dbo.PrWorkOrder ADD SourceEffectiveFrom datetime2 NULL;
IF COL_LENGTH(N'dbo.PrWorkOrder', N'SourceProductDefinitionRevisionID') IS NULL
    ALTER TABLE dbo.PrWorkOrder ADD SourceProductDefinitionRevisionID bigint NULL;
IF COL_LENGTH(N'dbo.PrWorkOrder', N'DefinitionSourceHash') IS NULL
    ALTER TABLE dbo.PrWorkOrder ADD DefinitionSourceHash char(64) NULL;
IF COL_LENGTH(N'dbo.PrWorkOrder', N'DefinitionSourceHashVersion') IS NULL
    ALTER TABLE dbo.PrWorkOrder ADD DefinitionSourceHashVersion int NULL;
IF COL_LENGTH(N'dbo.PrWorkOrder', N'SnapshotFormatVersion') IS NULL
    ALTER TABLE dbo.PrWorkOrder
        ADD SnapshotFormatVersion int NOT NULL
            CONSTRAINT DF_PrWorkOrder_SnapshotFormatVersion DEFAULT (1) WITH VALUES;
IF COL_LENGTH(N'dbo.PrWorkOrder', N'SnapshotHashVersion') IS NULL
    ALTER TABLE dbo.PrWorkOrder
        ADD SnapshotHashVersion int NOT NULL
            CONSTRAINT DF_PrWorkOrder_SnapshotHashVersion DEFAULT (1) WITH VALUES;
IF COL_LENGTH(N'dbo.PrWorkOrder', N'IsLegacySnapshot') IS NULL
    ALTER TABLE dbo.PrWorkOrder
        ADD IsLegacySnapshot bit NOT NULL
            CONSTRAINT DF_PrWorkOrder_IsLegacySnapshot DEFAULT (1) WITH VALUES;
IF COL_LENGTH(N'dbo.PrWorkOrder', N'LegacySnapshotReason') IS NULL
    ALTER TABLE dbo.PrWorkOrder ADD LegacySnapshotReason nvarchar(500) NULL;
IF COL_LENGTH(N'dbo.PrWorkOrder', N'ScheduleAnchorDateTime') IS NULL
    ALTER TABLE dbo.PrWorkOrder ADD ScheduleAnchorDateTime datetime2 NULL;
IF COL_LENGTH(N'dbo.PrWorkOrder', N'ScheduleCalculationTrace') IS NULL
    ALTER TABLE dbo.PrWorkOrder ADD ScheduleCalculationTrace nvarchar(max) NULL;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.PrWorkOrder')
      AND name = N'CK_PrWorkOrder_SnapshotFormat'
)
    ALTER TABLE dbo.PrWorkOrder WITH CHECK
        ADD CONSTRAINT CK_PrWorkOrder_SnapshotFormat CHECK (SnapshotFormatVersion IN (1, 2, 3));
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrder')
      AND name = N'IX_PrWorkOrder_Company_Branch_Status_Start'
)
    CREATE INDEX IX_PrWorkOrder_Company_Branch_Status_Start
        ON dbo.PrWorkOrder (CompanyCode, BranchCode, Status, PlannedStartDateTime);
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrder')
      AND name = N'IX_PrWorkOrder_Company_SnapshotFormat'
)
    CREATE INDEX IX_PrWorkOrder_Company_SnapshotFormat
        ON dbo.PrWorkOrder (CompanyCode, SnapshotFormatVersion);
GO

/* ══════════════════════════════════════════════════════════════════════════════════════════════
   PrWorkOrderRouteStep
   ══════════════════════════════════════════════════════════════════════════════════════════════ */

IF OBJECT_ID(N'dbo.PrWorkOrderRouteStep', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkOrderRouteStep
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrWorkOrderRouteStep PRIMARY KEY,
        WorkOrderID bigint NOT NULL,
        SourceRouteStepID bigint NULL,
        SourceRouteStepKey uniqueidentifier NULL,
        StageSequence int NOT NULL,
        WorkCentreCode nvarchar(20) NOT NULL,
        WorkCentreDescription nvarchar(100) NULL,
        OutputItemCode nvarchar(30) NOT NULL,
        OutputItemDescription nvarchar(200) NULL,
        OutputBaseQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderRouteStep_BaseQty DEFAULT (1),
        OutputUOM nvarchar(10) NULL,
        PlannedQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderRouteStep_Planned DEFAULT (0),
        PlannedStartDateTime datetime2 NULL,
        PlannedCompletionDateTime datetime2 NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_PrWorkOrderRouteStep_PrWorkOrder
            FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID) ON DELETE CASCADE,
        CONSTRAINT CK_PrWorkOrderRouteStep_Stage CHECK (StageSequence > 0),
        CONSTRAINT CK_PrWorkOrderRouteStep_Qty CHECK (OutputBaseQty > 0 AND PlannedQty >= 0)
    );

    -- StageSequence is deliberately NOT unique: equal values are parallel route steps.
    CREATE INDEX IX_PrWorkOrderRouteStep_Order_Stage
        ON dbo.PrWorkOrderRouteStep (WorkOrderID, StageSequence);
    CREATE UNIQUE INDEX UQ_PrWorkOrderRouteStep_Order_SourceKey
        ON dbo.PrWorkOrderRouteStep (WorkOrderID, SourceRouteStepKey)
        WHERE SourceRouteStepKey IS NOT NULL;
    CREATE INDEX IX_PrWorkOrderRouteStep_OutputItem
        ON dbo.PrWorkOrderRouteStep (OutputItemCode);
END;
GO

/* ══════════════════════════════════════════════════════════════════════════════════════════════
   PrWorkOrderMaterial
   ══════════════════════════════════════════════════════════════════════════════════════════════ */

IF OBJECT_ID(N'dbo.PrWorkOrderMaterial', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkOrderMaterial
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrWorkOrderMaterial PRIMARY KEY,
        WorkOrderID bigint NOT NULL,
        WorkOrderOperationID bigint NULL,
        SourceOperationID bigint NULL,
        [LineNo] int NOT NULL,
        MaterialSequence int NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_Sequence DEFAULT (0),
        SourceBomHdrID bigint NULL,
        SourceBomVersion int NULL,
        SourceBomLineID bigint NULL,
        SourceMaterialKey uniqueidentifier NULL,
        ParentProductCode nvarchar(30) NULL,
        BomPath nvarchar(2000) NOT NULL,
        ComponentCode nvarchar(30) NOT NULL,
        ComponentDescription nvarchar(200) NULL,
        MfgType nvarchar(20) NOT NULL,
        ComponentQtyPerParent decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_ComponentQty DEFAULT (0),
        StandardUOM nvarchar(10) NULL,
        BomOutputQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_BomOutputQty DEFAULT (0),
        BomOutputUOM nvarchar(10) NULL,
        ScrapPercent decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_Scrap DEFAULT (0),
        Tolerance decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_Tolerance DEFAULT (0),
        IssueMethod nvarchar(20) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_IssueMethod DEFAULT (N'MANUAL'),
        SupplySource nvarchar(40) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_SupplySource DEFAULT (N'PURCHASED'),
        ComponentDefinitionCode nvarchar(30) NULL,
        ProducingRouteStepID bigint NULL,
        RequiredQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_Required DEFAULT (0),
        RequiredUOM nvarchar(10) NULL,
        RequiredBaseQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_RequiredBase DEFAULT (0),
        BaseUOM nvarchar(10) NULL,
        ConversionFactorToBase decimal(18,8) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_Conversion DEFAULT (1),
        WarehouseCode nvarchar(20) NULL,
        LocationCode nvarchar(20) NULL,
        ReservedQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_Reserved DEFAULT (0),
        PickedQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_Picked DEFAULT (0),
        IssuedQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_Issued DEFAULT (0),
        ReturnedQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_Returned DEFAULT (0),
        ConsumedQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_Consumed DEFAULT (0),
        VarianceQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMaterial_Variance DEFAULT (0),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        -- NO ACTION: the owning path reaches materials through RouteStep -> Operation (see header).
        CONSTRAINT FK_PrWorkOrderMaterial_PrWorkOrder
            FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID),
        -- FK_PrWorkOrderMaterial_PrWorkOrderOperation is added after
        -- PrWorkOrderOperation is created (see the deployment-order note below).
        CONSTRAINT FK_PrWorkOrderMaterial_PrWorkOrderRouteStep
            FOREIGN KEY (ProducingRouteStepID) REFERENCES dbo.PrWorkOrderRouteStep (UID),
        CONSTRAINT FK_PrWorkOrderMaterial_PrBomHdr
            FOREIGN KEY (SourceBomHdrID) REFERENCES dbo.PrBomHdr (UID),
        CONSTRAINT FK_PrWorkOrderMaterial_PrDefBOM
            FOREIGN KEY (SourceBomLineID) REFERENCES dbo.PrDefBOM (UID),
        CONSTRAINT CK_PrWorkOrderMaterial_Qty CHECK
            (RequiredQty >= 0 AND ScrapPercent >= 0 AND RequiredBaseQty >= 0 AND ConversionFactorToBase > 0),
        -- NULL-safe producer equivalence (plan §6.3). SupplySource is NOT NULL so a NULL source can
        -- never make the comparison UNKNOWN. Legacy version-1 rows carry PURCHASED with a NULL
        -- producer and therefore satisfy the second branch.
        CONSTRAINT CK_PrWorkOrderMaterial_InternalWipProducer CHECK
        (
            (SupplySource = N'INTERNAL_ROUTE_WIP' AND ProducingRouteStepID IS NOT NULL)
            OR (SupplySource <> N'INTERNAL_ROUTE_WIP' AND ProducingRouteStepID IS NULL)
        )
    );

    CREATE UNIQUE INDEX UQ_PrWorkOrderMaterial_Order_Line
        ON dbo.PrWorkOrderMaterial (WorkOrderID, [LineNo]);
    CREATE UNIQUE INDEX UQ_PrWorkOrderMaterial_Operation_SourceKey
        ON dbo.PrWorkOrderMaterial (WorkOrderOperationID, SourceMaterialKey)
        WHERE SourceMaterialKey IS NOT NULL;
    CREATE INDEX IX_PrWorkOrderMaterial_Component_Warehouse
        ON dbo.PrWorkOrderMaterial (ComponentCode, WarehouseCode);
    CREATE INDEX IX_PrWorkOrderMaterial_SourceBomHdrID
        ON dbo.PrWorkOrderMaterial (SourceBomHdrID);
    CREATE INDEX IX_PrWorkOrderMaterial_SourceBomLineID
        ON dbo.PrWorkOrderMaterial (SourceBomLineID);
    CREATE INDEX IX_PrWorkOrderMaterial_WorkOrderOperationID
        ON dbo.PrWorkOrderMaterial (WorkOrderOperationID);
    CREATE INDEX IX_PrWorkOrderMaterial_ProducingRouteStepID
        ON dbo.PrWorkOrderMaterial (ProducingRouteStepID);
END;
GO

/* ══════════════════════════════════════════════════════════════════════════════════════════════
   PrWorkOrderOperation
   ══════════════════════════════════════════════════════════════════════════════════════════════ */

IF OBJECT_ID(N'dbo.PrWorkOrderOperation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkOrderOperation
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrWorkOrderOperation PRIMARY KEY,
        WorkOrderID bigint NULL,
        RouteStepID bigint NULL,
        SequenceNo int NOT NULL CONSTRAINT DF_PrWorkOrderOperation_Sequence DEFAULT (0),
        SourceOperationID bigint NULL,
        SourceOperationKey uniqueidentifier NULL,
        ProcessSequence int NOT NULL CONSTRAINT DF_PrWorkOrderOperation_ProcessSeq DEFAULT (0),
        ProcessType nvarchar(20) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_ProcessType DEFAULT (N''),
        StandardDurationMinutes decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_StdDuration DEFAULT (0),
        PlannedInputQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_PlannedInput DEFAULT (0),
        PlannedInputUOM nvarchar(10) NULL,
        PlannedOutputQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_PlannedOutput DEFAULT (0),
        PlannedOutputUOM nvarchar(10) NULL,
        WorkCentreCode nvarchar(20) NULL,
        WorkCentreDescription nvarchar(100) NULL,
        OperationCode nvarchar(30) NOT NULL,
        OperationDescription nvarchar(200) NULL,
        IsFinalOperation bit NOT NULL CONSTRAINT DF_PrWorkOrderOperation_Final DEFAULT (0),
        CalendarSourceType nvarchar(20) NULL,
        CalendarSourceID bigint NULL,
        CalendarSourceLastModified datetime2 NULL,
        ScheduleSourceHash char(64) NULL,
        CalendarHorizonStart datetime2 NULL,
        CalendarHorizonEnd datetime2 NULL,
        PlannedStartDateTime datetime2 NULL,
        PlannedCompletionDateTime datetime2 NULL,
        PlannedQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_Planned DEFAULT (0),
        SetupLossQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_SetupLoss DEFAULT (0),
        OperationLossQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_OperationLoss DEFAULT (0),
        InputQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_Input DEFAULT (0),
        ProcessedQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_Processed DEFAULT (0),
        GoodQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_Good DEFAULT (0),
        ScrapQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_Scrap DEFAULT (0),
        RejectQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_Reject DEFAULT (0),
        HoldQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_Hold DEFAULT (0),
        ReworkQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_Rework DEFAULT (0),
        TransferredQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_Transferred DEFAULT (0),
        RemainingQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderOperation_Remaining DEFAULT (0),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        -- NO ACTION: the owning path reaches operations through RouteStep (see header).
        CONSTRAINT FK_PrWorkOrderOperation_PrWorkOrder
            FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID),
        CONSTRAINT FK_PrWorkOrderOperation_PrWorkOrderRouteStep
            FOREIGN KEY (RouteStepID) REFERENCES dbo.PrWorkOrderRouteStep (UID) ON DELETE CASCADE,
        CONSTRAINT CK_PrWorkOrderOperation_ProcessSeq CHECK (ProcessSequence >= 0),
        CONSTRAINT CK_PrWorkOrderOperation_StdDuration CHECK (StandardDurationMinutes >= 0)
    );

    -- Stable source identity inside a route step.
    CREATE UNIQUE INDEX UQ_PrWorkOrderOperation_RouteStep_SourceKey
        ON dbo.PrWorkOrderOperation (RouteStepID, SourceOperationKey)
        WHERE SourceOperationKey IS NOT NULL;
    -- ProcessSequence is deliberately NOT unique: parallel processes are supported.
    CREATE INDEX IX_PrWorkOrderOperation_RouteStep_Process
        ON dbo.PrWorkOrderOperation (RouteStepID, ProcessSequence);
    -- Version-1 rows have no route step and keep their historical identity.
    CREATE UNIQUE INDEX UQ_PrWorkOrderOperation_Order_Sequence_Legacy
        ON dbo.PrWorkOrderOperation (WorkOrderID, SequenceNo)
        WHERE RouteStepID IS NULL;
    CREATE INDEX IX_PrWorkOrderOperation_WorkOrderID
        ON dbo.PrWorkOrderOperation (WorkOrderID);
END;
GO

-- ── Phase-1 operation upgrade ─────────────────────────────────────────────────────────────────
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'RouteStepID') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation ADD RouteStepID bigint NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'SourceOperationID') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation ADD SourceOperationID bigint NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'SourceOperationKey') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation ADD SourceOperationKey uniqueidentifier NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'ProcessSequence') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation
        ADD ProcessSequence int NOT NULL
            CONSTRAINT DF_PrWorkOrderOperation_ProcessSeq DEFAULT (0) WITH VALUES;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'ProcessType') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation
        ADD ProcessType nvarchar(20) NOT NULL
            CONSTRAINT DF_PrWorkOrderOperation_ProcessType DEFAULT (N'') WITH VALUES;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'StandardDurationMinutes') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation
        ADD StandardDurationMinutes decimal(18,4) NOT NULL
            CONSTRAINT DF_PrWorkOrderOperation_StdDuration DEFAULT (0) WITH VALUES;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'PlannedInputQty') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation
        ADD PlannedInputQty decimal(18,4) NOT NULL
            CONSTRAINT DF_PrWorkOrderOperation_PlannedInput DEFAULT (0) WITH VALUES;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'PlannedInputUOM') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation ADD PlannedInputUOM nvarchar(10) NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'PlannedOutputQty') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation
        ADD PlannedOutputQty decimal(18,4) NOT NULL
            CONSTRAINT DF_PrWorkOrderOperation_PlannedOutput DEFAULT (0) WITH VALUES;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'PlannedOutputUOM') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation ADD PlannedOutputUOM nvarchar(10) NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'CalendarSourceType') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation ADD CalendarSourceType nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'CalendarSourceID') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation ADD CalendarSourceID bigint NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'CalendarSourceLastModified') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation ADD CalendarSourceLastModified datetime2 NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'ScheduleSourceHash') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation ADD ScheduleSourceHash char(64) NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'CalendarHorizonStart') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation ADD CalendarHorizonStart datetime2 NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'CalendarHorizonEnd') IS NULL
    ALTER TABLE dbo.PrWorkOrderOperation ADD CalendarHorizonEnd datetime2 NULL;
GO

IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'PlannedStartDateTime') IS NULL
   AND COL_LENGTH(N'dbo.PrWorkOrderOperation', N'PlannedStartDate') IS NOT NULL
    EXEC sp_rename N'dbo.PrWorkOrderOperation.PlannedStartDate', N'PlannedStartDateTime', N'COLUMN';
GO

IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'PlannedCompletionDateTime') IS NULL
   AND COL_LENGTH(N'dbo.PrWorkOrderOperation', N'PlannedCompletionDate') IS NOT NULL
    EXEC sp_rename N'dbo.PrWorkOrderOperation.PlannedCompletionDate', N'PlannedCompletionDateTime', N'COLUMN';
GO

-- WorkOrderID becomes nullable for version-2 rows owned through a route step.
IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderOperation')
      AND name = N'WorkOrderID'
      AND is_nullable = 0
)
BEGIN
    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrWorkOrderOperation_PrWorkOrder')
        ALTER TABLE dbo.PrWorkOrderOperation DROP CONSTRAINT FK_PrWorkOrderOperation_PrWorkOrder;
    ALTER TABLE dbo.PrWorkOrderOperation ALTER COLUMN WorkOrderID bigint NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrWorkOrderOperation_PrWorkOrder')
    ALTER TABLE dbo.PrWorkOrderOperation WITH CHECK
        ADD CONSTRAINT FK_PrWorkOrderOperation_PrWorkOrder
            FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrWorkOrderOperation_PrWorkOrderRouteStep')
    ALTER TABLE dbo.PrWorkOrderOperation WITH CHECK
        ADD CONSTRAINT FK_PrWorkOrderOperation_PrWorkOrderRouteStep
            FOREIGN KEY (RouteStepID) REFERENCES dbo.PrWorkOrderRouteStep (UID) ON DELETE CASCADE;
GO

-- Replace the Phase-1 unique (WorkOrderID, SequenceNo) index with the route-step identity indexes.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_PrWorkOrderOperation_Order_Sequence')
    DROP INDEX UQ_PrWorkOrderOperation_Order_Sequence ON dbo.PrWorkOrderOperation;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderOperation')
      AND name = N'UQ_PrWorkOrderOperation_RouteStep_SourceKey'
)
    CREATE UNIQUE INDEX UQ_PrWorkOrderOperation_RouteStep_SourceKey
        ON dbo.PrWorkOrderOperation (RouteStepID, SourceOperationKey)
        WHERE SourceOperationKey IS NOT NULL;
IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderOperation')
      AND name = N'IX_PrWorkOrderOperation_RouteStep_Process'
)
    CREATE INDEX IX_PrWorkOrderOperation_RouteStep_Process
        ON dbo.PrWorkOrderOperation (RouteStepID, ProcessSequence);
IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderOperation')
      AND name = N'UQ_PrWorkOrderOperation_Order_Sequence_Legacy'
)
    CREATE UNIQUE INDEX UQ_PrWorkOrderOperation_Order_Sequence_Legacy
        ON dbo.PrWorkOrderOperation (WorkOrderID, SequenceNo)
        WHERE RouteStepID IS NULL;
IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderOperation')
      AND name = N'IX_PrWorkOrderOperation_WorkOrderID'
)
    CREATE INDEX IX_PrWorkOrderOperation_WorkOrderID
        ON dbo.PrWorkOrderOperation (WorkOrderID);
GO

-- ── Phase-1 material upgrade ──────────────────────────────────────────────────────────────────
IF COL_LENGTH(N'dbo.PrWorkOrderMaterial', N'WorkOrderOperationID') IS NULL
    ALTER TABLE dbo.PrWorkOrderMaterial ADD WorkOrderOperationID bigint NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderMaterial', N'SourceOperationID') IS NULL
    ALTER TABLE dbo.PrWorkOrderMaterial ADD SourceOperationID bigint NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderMaterial', N'MaterialSequence') IS NULL
    ALTER TABLE dbo.PrWorkOrderMaterial
        ADD MaterialSequence int NOT NULL
            CONSTRAINT DF_PrWorkOrderMaterial_Sequence DEFAULT (0) WITH VALUES;
IF COL_LENGTH(N'dbo.PrWorkOrderMaterial', N'SourceMaterialKey') IS NULL
    ALTER TABLE dbo.PrWorkOrderMaterial ADD SourceMaterialKey uniqueidentifier NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderMaterial', N'StandardUOM') IS NULL
    ALTER TABLE dbo.PrWorkOrderMaterial ADD StandardUOM nvarchar(10) NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderMaterial', N'IssueMethod') IS NULL
    ALTER TABLE dbo.PrWorkOrderMaterial
        ADD IssueMethod nvarchar(20) NOT NULL
            CONSTRAINT DF_PrWorkOrderMaterial_IssueMethod DEFAULT (N'MANUAL') WITH VALUES;
IF COL_LENGTH(N'dbo.PrWorkOrderMaterial', N'SupplySource') IS NULL
    ALTER TABLE dbo.PrWorkOrderMaterial
        ADD SupplySource nvarchar(40) NOT NULL
            CONSTRAINT DF_PrWorkOrderMaterial_SupplySource DEFAULT (N'PURCHASED') WITH VALUES;
IF COL_LENGTH(N'dbo.PrWorkOrderMaterial', N'ProducingRouteStepID') IS NULL
    ALTER TABLE dbo.PrWorkOrderMaterial ADD ProducingRouteStepID bigint NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderMaterial', N'RequiredBaseQty') IS NULL
    ALTER TABLE dbo.PrWorkOrderMaterial
        ADD RequiredBaseQty decimal(18,4) NOT NULL
            CONSTRAINT DF_PrWorkOrderMaterial_RequiredBase DEFAULT (0) WITH VALUES;
IF COL_LENGTH(N'dbo.PrWorkOrderMaterial', N'BaseUOM') IS NULL
    ALTER TABLE dbo.PrWorkOrderMaterial ADD BaseUOM nvarchar(10) NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderMaterial', N'ConversionFactorToBase') IS NULL
    ALTER TABLE dbo.PrWorkOrderMaterial
        ADD ConversionFactorToBase decimal(18,8) NOT NULL
            CONSTRAINT DF_PrWorkOrderMaterial_Conversion DEFAULT (1) WITH VALUES;
GO

-- Backfill the material sequence from the legacy line number, and the base quantity from the
-- requirement so the inventory posting basis is never zero.
UPDATE dbo.PrWorkOrderMaterial
SET MaterialSequence = [LineNo]
WHERE MaterialSequence = 0;

UPDATE dbo.PrWorkOrderMaterial
SET RequiredBaseQty = RequiredQty
WHERE RequiredBaseQty = 0 AND RequiredQty <> 0;
GO

-- The direct WorkOrderID foreign key must not cascade: the owning path is via RouteStep.
IF EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_PrWorkOrderMaterial_PrWorkOrder'
      AND delete_referential_action <> 0
)
BEGIN
    ALTER TABLE dbo.PrWorkOrderMaterial DROP CONSTRAINT FK_PrWorkOrderMaterial_PrWorkOrder;
    ALTER TABLE dbo.PrWorkOrderMaterial WITH CHECK
        ADD CONSTRAINT FK_PrWorkOrderMaterial_PrWorkOrder
            FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID);
END
ELSE IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrWorkOrderMaterial_PrWorkOrder')
    ALTER TABLE dbo.PrWorkOrderMaterial WITH CHECK
        ADD CONSTRAINT FK_PrWorkOrderMaterial_PrWorkOrder
            FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID);
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrWorkOrderMaterial_PrWorkOrderOperation')
    ALTER TABLE dbo.PrWorkOrderMaterial WITH CHECK
        ADD CONSTRAINT FK_PrWorkOrderMaterial_PrWorkOrderOperation
            FOREIGN KEY (WorkOrderOperationID) REFERENCES dbo.PrWorkOrderOperation (UID) ON DELETE CASCADE;
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrWorkOrderMaterial_PrWorkOrderRouteStep')
    ALTER TABLE dbo.PrWorkOrderMaterial WITH CHECK
        ADD CONSTRAINT FK_PrWorkOrderMaterial_PrWorkOrderRouteStep
            FOREIGN KEY (ProducingRouteStepID) REFERENCES dbo.PrWorkOrderRouteStep (UID);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrWorkOrderMaterial_PrBomHdr')
    ALTER TABLE dbo.PrWorkOrderMaterial WITH CHECK
        ADD CONSTRAINT FK_PrWorkOrderMaterial_PrBomHdr
            FOREIGN KEY (SourceBomHdrID) REFERENCES dbo.PrBomHdr (UID);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrWorkOrderMaterial_PrDefBOM')
    ALTER TABLE dbo.PrWorkOrderMaterial WITH CHECK
        ADD CONSTRAINT FK_PrWorkOrderMaterial_PrDefBOM
            FOREIGN KEY (SourceBomLineID) REFERENCES dbo.PrDefBOM (UID);
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrWorkOrderMaterial_Qty')
    ALTER TABLE dbo.PrWorkOrderMaterial DROP CONSTRAINT CK_PrWorkOrderMaterial_Qty;
ALTER TABLE dbo.PrWorkOrderMaterial WITH CHECK
    ADD CONSTRAINT CK_PrWorkOrderMaterial_Qty CHECK
        (RequiredQty >= 0 AND ScrapPercent >= 0 AND RequiredBaseQty >= 0 AND ConversionFactorToBase > 0);
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrWorkOrderMaterial_InternalWipProducer')
    ALTER TABLE dbo.PrWorkOrderMaterial WITH CHECK
        ADD CONSTRAINT CK_PrWorkOrderMaterial_InternalWipProducer CHECK
        (
            (SupplySource = N'INTERNAL_ROUTE_WIP' AND ProducingRouteStepID IS NOT NULL)
            OR (SupplySource <> N'INTERNAL_ROUTE_WIP' AND ProducingRouteStepID IS NULL)
        );
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_PrWorkOrderMaterial_Operation_SourceKey')
    CREATE UNIQUE INDEX UQ_PrWorkOrderMaterial_Operation_SourceKey
        ON dbo.PrWorkOrderMaterial (WorkOrderOperationID, SourceMaterialKey)
        WHERE SourceMaterialKey IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PrWorkOrderMaterial_WorkOrderOperationID')
    CREATE INDEX IX_PrWorkOrderMaterial_WorkOrderOperationID
        ON dbo.PrWorkOrderMaterial (WorkOrderOperationID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PrWorkOrderMaterial_ProducingRouteStepID')
    CREATE INDEX IX_PrWorkOrderMaterial_ProducingRouteStepID
        ON dbo.PrWorkOrderMaterial (ProducingRouteStepID);
GO

/* ══════════════════════════════════════════════════════════════════════════════════════════════
   PrWorkOrderMachine
   ══════════════════════════════════════════════════════════════════════════════════════════════ */

IF OBJECT_ID(N'dbo.PrWorkOrderMachine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkOrderMachine
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrWorkOrderMachine PRIMARY KEY,
        OperationID bigint NOT NULL,
        SourceMachineOptionID bigint NULL,
        SourceMachineKey uniqueidentifier NULL,
        Priority int NOT NULL CONSTRAINT DF_PrWorkOrderMachine_Priority DEFAULT (1),
        MachineCode nvarchar(30) NOT NULL,
        MachineDescription nvarchar(200) NULL,
        IsDefault bit NOT NULL CONSTRAINT DF_PrWorkOrderMachine_IsDefault DEFAULT (0),
        IsSelected bit NOT NULL CONSTRAINT DF_PrWorkOrderMachine_IsSelected DEFAULT (0),
        ParallelMachineCount int NOT NULL CONSTRAINT DF_PrWorkOrderMachine_Parallel DEFAULT (1),
        CycleQuantityMode nvarchar(20) NOT NULL CONSTRAINT DF_PrWorkOrderMachine_CycleMode DEFAULT (N'DISCRETE'),
        CycleSeconds decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMachine_CycleSeconds DEFAULT (0),
        OutputPerCycle decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMachine_OutputPerCycle DEFAULT (1),
        OutputPerCycleUOM nvarchar(10) NULL,
        RequiredMachineOutputQty decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMachine_ReqOutput DEFAULT (0),
        RequiredMachineOutputUOM nvarchar(10) NULL,
        PlannedCycleCount decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMachine_CycleCount DEFAULT (0),
        PlannedCycleSlots decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMachine_CycleSlots DEFAULT (0),
        PlannedRunMinutes decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMachine_RunMinutes DEFAULT (0),
        ConversionSeconds decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMachine_Conversion DEFAULT (0),
        SetupSeconds decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMachine_Setup DEFAULT (0),
        QueueSeconds decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderMachine_Queue DEFAULT (0),
        MachineRatePerHour decimal(19,6) NOT NULL CONSTRAINT DF_PrWorkOrderMachine_Rate DEFAULT (0),
        CalendarSourceID bigint NULL,
        CalendarSourceLastModified datetime2 NULL,
        ScheduleSourceHash char(64) NULL,
        CalendarHorizonStart datetime2 NULL,
        CalendarHorizonEnd datetime2 NULL,
        PlannedStartDateTime datetime2 NULL,
        PlannedCompletionDateTime datetime2 NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_PrWorkOrderMachine_PrWorkOrderOperation
            FOREIGN KEY (OperationID) REFERENCES dbo.PrWorkOrderOperation (UID) ON DELETE CASCADE,
        CONSTRAINT CK_PrWorkOrderMachine_Parallel CHECK (ParallelMachineCount >= 1),
        CONSTRAINT CK_PrWorkOrderMachine_OutputPerCycle CHECK (OutputPerCycle > 0),
        CONSTRAINT CK_PrWorkOrderMachine_Times CHECK
            (CycleSeconds >= 0 AND ConversionSeconds >= 0 AND SetupSeconds >= 0
             AND QueueSeconds >= 0 AND MachineRatePerHour >= 0)
    );

    CREATE UNIQUE INDEX UQ_PrWorkOrderMachine_Operation_SourceKey
        ON dbo.PrWorkOrderMachine (OperationID, SourceMachineKey)
        WHERE SourceMachineKey IS NOT NULL;
    CREATE INDEX IX_PrWorkOrderMachine_Operation_Priority
        ON dbo.PrWorkOrderMachine (OperationID, Priority);
    -- Exactly one selected machine option per operation.
    CREATE UNIQUE INDEX UX_PrWorkOrderMachine_OneSelected
        ON dbo.PrWorkOrderMachine (OperationID)
        WHERE IsSelected = 1;
END;
GO

/* ══════════════════════════════════════════════════════════════════════════════════════════════
   PrWorkOrderLabour  (Option A1 — exclusive owner, single cascade path)
   ══════════════════════════════════════════════════════════════════════════════════════════════ */

IF OBJECT_ID(N'dbo.PrWorkOrderLabour', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkOrderLabour
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrWorkOrderLabour PRIMARY KEY,
        MachineID bigint NULL,
        OperationID bigint NULL,
        SourceLabourID bigint NULL,
        SourceLabourKey uniqueidentifier NULL,
        LabourCode nvarchar(20) NOT NULL,
        LabourDescription nvarchar(200) NULL,
        PlannedUnits decimal(18,4) NULL,
        PlannedMinutes decimal(18,4) NULL,
        RateBasis nvarchar(20) NOT NULL CONSTRAINT DF_PrWorkOrderLabour_RateBasis DEFAULT (N'PER_OUTPUT_UNIT'),
        Rate decimal(19,6) NOT NULL CONSTRAINT DF_PrWorkOrderLabour_Rate DEFAULT (0),
        ContributesToPlan bit NOT NULL CONSTRAINT DF_PrWorkOrderLabour_Contributes DEFAULT (1),
        PlannedAmount decimal(19,4) NOT NULL CONSTRAINT DF_PrWorkOrderLabour_Amount DEFAULT (0),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        -- Machine-owned labour cascades from the machine. That is the ONLY structural path from
        -- Operation to Labour, so SQL Server never sees two cascade paths.
        CONSTRAINT FK_PrWorkOrderLabour_PrWorkOrderMachine
            FOREIGN KEY (MachineID) REFERENCES dbo.PrWorkOrderMachine (UID) ON DELETE CASCADE,
        -- Direct operation-level labour: NO ACTION, deleted explicitly by the aggregate service.
        CONSTRAINT FK_PrWorkOrderLabour_PrWorkOrderOperation
            FOREIGN KEY (OperationID) REFERENCES dbo.PrWorkOrderOperation (UID),
        CONSTRAINT CK_PrWorkOrderLabour_ExclusiveOwner CHECK
        (
            ([MachineID] IS NOT NULL AND [OperationID] IS NULL)
            OR ([MachineID] IS NULL AND [OperationID] IS NOT NULL)
        ),
        CONSTRAINT CK_PrWorkOrderLabour_Amount CHECK (Rate >= 0 AND PlannedAmount >= 0)
    );

    CREATE UNIQUE INDEX UQ_PrWorkOrderLabour_Operation_SourceKey
        ON dbo.PrWorkOrderLabour (OperationID, SourceLabourKey)
        WHERE MachineID IS NULL AND SourceLabourKey IS NOT NULL;
    CREATE UNIQUE INDEX UQ_PrWorkOrderLabour_Machine_SourceKey
        ON dbo.PrWorkOrderLabour (MachineID, SourceLabourKey)
        WHERE MachineID IS NOT NULL AND SourceLabourKey IS NOT NULL;
    CREATE INDEX IX_PrWorkOrderLabour_OperationID
        ON dbo.PrWorkOrderLabour (OperationID);
    CREATE INDEX IX_PrWorkOrderLabour_MachineID
        ON dbo.PrWorkOrderLabour (MachineID);
END;
GO

/* ══════════════════════════════════════════════════════════════════════════════════════════════
   PrWorkOrderResource (legacy generic resource; superseded by machine + labour)
   ══════════════════════════════════════════════════════════════════════════════════════════════ */

IF OBJECT_ID(N'dbo.PrWorkOrderResource', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkOrderResource
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrWorkOrderResource PRIMARY KEY,
        OperationID bigint NOT NULL,
        SequenceNo int NOT NULL,
        ResourceType nvarchar(20) NOT NULL,
        ResourceCode nvarchar(30) NOT NULL,
        ResourceDescription nvarchar(200) NULL,
        PlannedUnits decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderResource_Units DEFAULT (0),
        SetupMinutes decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderResource_Setup DEFAULT (0),
        RunMinutes decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderResource_Run DEFAULT (0),
        QueueMinutes decimal(18,4) NOT NULL CONSTRAINT DF_PrWorkOrderResource_Queue DEFAULT (0),
        Rate decimal(19,6) NOT NULL CONSTRAINT DF_PrWorkOrderResource_Rate DEFAULT (0),
        PlannedAmount decimal(19,4) NOT NULL CONSTRAINT DF_PrWorkOrderResource_Amount DEFAULT (0),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        CONSTRAINT FK_PrWorkOrderResource_PrWorkOrderOperation
            FOREIGN KEY (OperationID) REFERENCES dbo.PrWorkOrderOperation (UID) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX UQ_PrWorkOrderResource_Operation_Sequence
        ON dbo.PrWorkOrderResource (OperationID, SequenceNo);
END;
GO

/* ══════════════════════════════════════════════════════════════════════════════════════════════
   Audit / change / posting-link tables (unchanged from Phase 1)
   ══════════════════════════════════════════════════════════════════════════════════════════════ */

IF OBJECT_ID(N'dbo.PrWorkOrderAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkOrderAudit
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrWorkOrderAudit PRIMARY KEY,
        WorkOrderID bigint NOT NULL,
        EventType nvarchar(40) NOT NULL,
        FromStatus nvarchar(20) NULL,
        ToStatus nvarchar(20) NULL,
        SnapshotRevision int NOT NULL,
        Reason nvarchar(500) NULL,
        DetailsJson nvarchar(max) NULL,
        OccurredDate datetime2 NOT NULL,
        ActorUserId nvarchar(10) NOT NULL,
        CONSTRAINT FK_PrWorkOrderAudit_PrWorkOrder
            FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID) ON DELETE CASCADE
    );
    CREATE INDEX IX_PrWorkOrderAudit_Order_Date
        ON dbo.PrWorkOrderAudit (WorkOrderID, OccurredDate);
END;
GO

IF OBJECT_ID(N'dbo.PrWorkOrderChange', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkOrderChange
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrWorkOrderChange PRIMARY KEY,
        WorkOrderID bigint NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        ChangeOrderNo nvarchar(30) NOT NULL,
        SourceSnapshotRevision int NOT NULL,
        ProposedSnapshotRevision int NOT NULL,
        Status nvarchar(20) NOT NULL,
        Reason nvarchar(500) NOT NULL,
        EffectiveDate datetime2 NULL,
        RequestedDate datetime2 NULL,
        RequestedBy nvarchar(10) NULL,
        ApprovedDate datetime2 NULL,
        ApprovedBy nvarchar(10) NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_PrWorkOrderChange_PrWorkOrder
            FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID)
    );
    CREATE UNIQUE INDEX UQ_PrWorkOrderChange_Company_Number
        ON dbo.PrWorkOrderChange (CompanyCode, ChangeOrderNo);
    CREATE INDEX IX_PrWorkOrderChange_Order_Status
        ON dbo.PrWorkOrderChange (WorkOrderID, Status);
END;
GO

IF OBJECT_ID(N'dbo.PrWorkOrderChangeLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkOrderChangeLine
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrWorkOrderChangeLine PRIMARY KEY,
        ChangeOrderID bigint NOT NULL,
        [LineNo] int NOT NULL,
        ChangeType nvarchar(30) NOT NULL,
        TargetType nvarchar(30) NOT NULL,
        TargetUID bigint NULL,
        FieldName nvarchar(100) NULL,
        BeforeValue nvarchar(2000) NULL,
        AfterValue nvarchar(2000) NULL,
        DetailJson nvarchar(max) NULL,
        CONSTRAINT FK_PrWorkOrderChangeLine_PrWorkOrderChange
            FOREIGN KEY (ChangeOrderID) REFERENCES dbo.PrWorkOrderChange (UID) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX UQ_PrWorkOrderChangeLine_Change_Line
        ON dbo.PrWorkOrderChangeLine (ChangeOrderID, [LineNo]);
END;
GO

IF OBJECT_ID(N'dbo.PrProductionPostingLink', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrProductionPostingLink
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrProductionPostingLink PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        CommandType nvarchar(40) NOT NULL,
        PostingRequestId nvarchar(64) NOT NULL,
        WorkOrderID bigint NOT NULL,
        ProductionDocumentType nvarchar(30) NULL,
        ProductionDocumentNo nvarchar(40) NULL,
        ProductionDocumentLineID bigint NULL,
        InventoryBatchNo int NULL,
        PostingOperationID nvarchar(64) NULL,
        OriginalPostingLinkID bigint NULL,
        Status nvarchar(20) NOT NULL,
        ResultCode nvarchar(50) NULL,
        ResultMessage nvarchar(1000) NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(10) NOT NULL,
        CompletedDate datetime2 NULL,
        CONSTRAINT FK_PrProductionPostingLink_PrWorkOrder
            FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID),
        CONSTRAINT FK_PrProductionPostingLink_Original
            FOREIGN KEY (OriginalPostingLinkID) REFERENCES dbo.PrProductionPostingLink (UID)
    );
    CREATE UNIQUE INDEX UQ_PrProductionPostingLink_Idempotency
        ON dbo.PrProductionPostingLink (CompanyCode, BranchCode, CommandType, PostingRequestId);
    CREATE INDEX IX_PrProductionPostingLink_Order_Command_Status
        ON dbo.PrProductionPostingLink (WorkOrderID, CommandType, Status);
END;
GO

/* ══════════════════════════════════════════════════════════════════════════════════════════════
   PrMaterialMovement (immutable production material execution ledger)
   Inventory tables remain the physical stock authority. All FKs are NO ACTION/RESTRICT.
   ══════════════════════════════════════════════════════════════════════════════════════════════ */

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
        CONSTRAINT FK_PrMaterialMovement_PrWorkOrder FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID),
        CONSTRAINT FK_PrMaterialMovement_PrWorkOrderMaterial FOREIGN KEY (WorkOrderMaterialID) REFERENCES dbo.PrWorkOrderMaterial (UID),
        CONSTRAINT FK_PrMaterialMovement_PrWorkOrderOperation FOREIGN KEY (WorkOrderOperationID) REFERENCES dbo.PrWorkOrderOperation (UID),
        CONSTRAINT FK_PrMaterialMovement_PrProductionPostingLink FOREIGN KEY (PostingLinkID) REFERENCES dbo.PrProductionPostingLink (UID),
        CONSTRAINT FK_PrMaterialMovement_OriginalMovement FOREIGN KEY (OriginalMovementID) REFERENCES dbo.PrMaterialMovement (UID),
        CONSTRAINT FK_PrMaterialMovement_IvTrxBatch FOREIGN KEY (InventoryBatchID) REFERENCES dbo.IvTrxBatch (ID),
        CONSTRAINT FK_PrMaterialMovement_IvTrxBatchDetail FOREIGN KEY (InventoryBatchDetailID) REFERENCES dbo.IvTrxBatchDetail (ID),
        CONSTRAINT FK_PrMaterialMovement_IvBalLoc FOREIGN KEY (FromBalLocID) REFERENCES dbo.IvBalLoc (ID),
        CONSTRAINT FK_PrMaterialMovement_IvLot FOREIGN KEY (LotID) REFERENCES dbo.IvLot (ID)
    );
    CREATE INDEX IX_PrMaterialMovement_WorkOrder_Date ON dbo.PrMaterialMovement (WorkOrderID, MovementDate);
    CREATE INDEX IX_PrMaterialMovement_Material_Type ON dbo.PrMaterialMovement (WorkOrderMaterialID, MovementType);
    CREATE INDEX IX_PrMaterialMovement_Operation_Date ON dbo.PrMaterialMovement (WorkOrderOperationID, MovementDate);
    CREATE INDEX IX_PrMaterialMovement_InventoryBatch ON dbo.PrMaterialMovement (CompanyCode, BranchCode, InventoryBatchNo);
    CREATE INDEX IX_PrMaterialMovement_OriginalMovement ON dbo.PrMaterialMovement (OriginalMovementID);
    CREATE UNIQUE INDEX UQ_PrMaterialMovement_PostingLine
        ON dbo.PrMaterialMovement (PostingLinkID, InventoryBatchDetailID, MovementType);
END;
GO

/* ══════════════════════════════════════════════════════════════════════════════════════════════
   Legacy snapshot migration (plan §6.7)
   Existing records are never reconstructed from today's Product Definition. They keep their data
   and are marked as version-1 legacy snapshots.
   ══════════════════════════════════════════════════════════════════════════════════════════════ */

UPDATE dbo.PrWorkOrder
SET IsLegacySnapshot = 1,
    LegacySnapshotReason = COALESCE
    (
        LegacySnapshotReason,
        N'Phase-1 version-1 snapshot. An explicit definition refresh is required before Release.'
    )
WHERE SnapshotFormatVersion = 1;
GO

/* ══════════════════════════════════════════════════════════════════════════════════════════════
   Verification
   ══════════════════════════════════════════════════════════════════════════════════════════════ */

IF EXISTS
(
    SELECT required.ObjectName
    FROM
    (
        VALUES
            (N'PrWorkOrder'),
            (N'PrWorkOrderRouteStep'),
            (N'PrWorkOrderMaterial'),
            (N'PrWorkOrderOperation'),
            (N'PrWorkOrderMachine'),
            (N'PrWorkOrderLabour'),
            (N'PrWorkOrderResource'),
            (N'PrWorkOrderAudit'),
            (N'PrWorkOrderChange'),
            (N'PrWorkOrderChangeLine'),
            (N'PrProductionPostingLink'),
            (N'PrMaterialMovement')
    ) required (ObjectName)
    WHERE OBJECT_ID(N'dbo.' + required.ObjectName, N'U') IS NULL
)
    THROW 51002, 'Production Work Order schema is incomplete. Review the preceding error and rerun this script.', 1;

-- Prove the delete graph has exactly one structural cascade path into labour.
IF
(
    SELECT COUNT(*)
    FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID(N'dbo.PrWorkOrderLabour')
      AND delete_referential_action = 1
) <> 1
    THROW 51003, 'PrWorkOrderLabour must have exactly one cascading foreign key (from PrWorkOrderMachine).', 1;

PRINT N'Production Work Order schema verified successfully.';
GO
