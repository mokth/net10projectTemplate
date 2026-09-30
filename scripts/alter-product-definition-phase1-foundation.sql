-- Product Definition Phase 1: additive domain/schema foundation.
-- Do not run this file directly on a legacy database. Use
-- deploy-product-definition-phase1.sql from the scripts directory in SQLCMD mode.
-- Required objects/columns are supplied by alter-prdefbom-multilevel.sql,
-- create-product-definition-routing.sql, and alter-prdefbom-process-ownership.sql.
--
-- This script is intentionally additive. It does not activate, supersede, or otherwise
-- rewrite a Product Definition revision. Existing rows are marked UNVERIFIED so the
-- centralized validator can audit them before enforcement is enabled.

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;

IF COL_LENGTH('dbo.PrBomHdr', 'ValidationRuleVersion') IS NULL
    ALTER TABLE dbo.PrBomHdr ADD ValidationRuleVersion nvarchar(30) NULL;
IF COL_LENGTH('dbo.PrBomHdr', 'ValidationStatus') IS NULL
    ALTER TABLE dbo.PrBomHdr ADD ValidationStatus nvarchar(20) NOT NULL
        CONSTRAINT DF_PrBomHdr_ValidationStatus DEFAULT N'UNVERIFIED' WITH VALUES;
IF COL_LENGTH('dbo.PrBomHdr', 'ValidatedDate') IS NULL
    ALTER TABLE dbo.PrBomHdr ADD ValidatedDate datetime2 NULL;
IF COL_LENGTH('dbo.PrBomHdr', 'ValidatedBy') IS NULL
    ALTER TABLE dbo.PrBomHdr ADD ValidatedBy nvarchar(10) NULL;
IF COL_LENGTH('dbo.PrBomHdr', 'ActivatedDate') IS NULL
    ALTER TABLE dbo.PrBomHdr ADD ActivatedDate datetime2 NULL;
IF COL_LENGTH('dbo.PrBomHdr', 'ActivatedBy') IS NULL
    ALTER TABLE dbo.PrBomHdr ADD ActivatedBy nvarchar(10) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.PrBomHdr') AND name = 'CK_PrBomHdr_ValidationStatus')
    ALTER TABLE dbo.PrBomHdr WITH CHECK ADD CONSTRAINT CK_PrBomHdr_ValidationStatus
        CHECK (ValidationStatus IN (N'UNVERIFIED', N'VALID', N'INVALID'));

IF OBJECT_ID(N'dbo.PrBomRouteStep', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrBomRouteStep (
        UID bigint IDENTITY(1,1) NOT NULL,
        RouteStepKey uniqueidentifier NOT NULL CONSTRAINT DF_PrBomRouteStep_Key DEFAULT NEWID(),
        BomHdrID bigint NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        WorkCentreCode nvarchar(20) NOT NULL,
        StageSequence int NOT NULL,
        OutputItemCode nvarchar(30) NOT NULL,
        OutputType nvarchar(20) NOT NULL,
        StandardOutputQty decimal(18,4) NOT NULL,
        OutputUOM nvarchar(10) NOT NULL,
        YieldPercent decimal(9,4) NOT NULL CONSTRAINT DF_PrBomRouteStep_Yield DEFAULT (100),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PrBomRouteStep PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PrBomRouteStep_Header FOREIGN KEY (BomHdrID) REFERENCES dbo.PrBomHdr (UID) ON DELETE CASCADE,
        CONSTRAINT CK_PrBomRouteStep_Stage CHECK (StageSequence > 0),
        CONSTRAINT CK_PrBomRouteStep_OutputQty CHECK (StandardOutputQty > 0),
        CONSTRAINT CK_PrBomRouteStep_Yield CHECK (YieldPercent > 0 AND YieldPercent <= 100),
        CONSTRAINT CK_PrBomRouteStep_OutputType CHECK (OutputType IN (N'WIP_STOCKED', N'WIP_NONSTOCK', N'FINISHED_GOODS')),
        CONSTRAINT UQ_PrBomRouteStep_Header_Key UNIQUE (BomHdrID, RouteStepKey)
    );
    CREATE INDEX IX_PrBomRouteStep_Header_Stage ON dbo.PrBomRouteStep (BomHdrID, StageSequence);
END;
GO

IF COL_LENGTH('dbo.PrBomOperation', 'RouteStepID') IS NULL
    ALTER TABLE dbo.PrBomOperation ADD RouteStepID bigint NULL;
IF COL_LENGTH('dbo.PrBomOperation', 'ProcessType') IS NULL
    ALTER TABLE dbo.PrBomOperation ADD ProcessType nvarchar(20) NOT NULL
        CONSTRAINT DF_PrBomOperation_ProcessType DEFAULT N'MACHINE' WITH VALUES;
IF COL_LENGTH('dbo.PrBomOperation', 'StandardDurationMinutes') IS NULL
    ALTER TABLE dbo.PrBomOperation ADD StandardDurationMinutes decimal(18,4) NOT NULL
        CONSTRAINT DF_PrBomOperation_Duration DEFAULT (0) WITH VALUES;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.PrBomOperation') AND name = 'CK_PrBomOperation_ProcessType')
    ALTER TABLE dbo.PrBomOperation WITH CHECK ADD CONSTRAINT CK_PrBomOperation_ProcessType
        CHECK (ProcessType IN (N'MACHINE', N'AUTOMATED', N'MANUAL', N'INSPECTION', N'WAIT', N'PACKING', N'SUBCONTRACT'));
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.PrBomOperation') AND name = 'CK_PrBomOperation_Duration')
    ALTER TABLE dbo.PrBomOperation WITH CHECK ADD CONSTRAINT CK_PrBomOperation_Duration
        CHECK (StandardDurationMinutes >= 0);

-- One route-step occurrence is recoverable for each legacy centre/output/sequence grouping.
-- No attempt is made to invent repeated occurrences that were previously flattened together.
INSERT INTO dbo.PrBomRouteStep (
    RouteStepKey, BomHdrID, CompanyCode, WorkCentreCode, StageSequence,
    OutputItemCode, OutputType, StandardOutputQty, OutputUOM, YieldPercent,
    CreatedDate, CreatedBy, ModifiedDate, ModifiedBy)
SELECT NEWID(), o.BomHdrID, o.CompanyCode, o.WorkCentreCode, o.CentralSequence,
       o.OutputItemCode,
       CASE WHEN o.OutputItemCode = h.ProdCode THEN N'FINISHED_GOODS' ELSE N'WIP_STOCKED' END,
       MAX(o.OutputBaseQty), COALESCE(MAX(o.OutputUOM), MAX(h.BaseUOM), N'EA'), 100,
       SYSUTCDATETIME(), N'migration', SYSUTCDATETIME(), N'migration'
FROM dbo.PrBomOperation o
INNER JOIN dbo.PrBomHdr h ON h.UID = o.BomHdrID
WHERE o.RouteStepID IS NULL
  AND NOT EXISTS (
      SELECT 1 FROM dbo.PrBomRouteStep s
      WHERE s.BomHdrID = o.BomHdrID
        AND s.WorkCentreCode = o.WorkCentreCode
        AND s.StageSequence = o.CentralSequence
        AND s.OutputItemCode = o.OutputItemCode)
GROUP BY o.BomHdrID, o.CompanyCode, o.WorkCentreCode, o.CentralSequence,
         o.OutputItemCode, h.ProdCode;

UPDATE o
SET RouteStepID = s.UID
FROM dbo.PrBomOperation o
INNER JOIN dbo.PrBomRouteStep s
    ON s.BomHdrID = o.BomHdrID
   AND s.WorkCentreCode = o.WorkCentreCode
   AND s.StageSequence = o.CentralSequence
   AND s.OutputItemCode = o.OutputItemCode
WHERE o.RouteStepID IS NULL;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PrBomOperation_RouteStep')
    ALTER TABLE dbo.PrBomOperation WITH CHECK
        ADD CONSTRAINT FK_PrBomOperation_RouteStep FOREIGN KEY (RouteStepID)
        REFERENCES dbo.PrBomRouteStep (UID);

IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID('dbo.PrBomOperation') AND name = 'UQ_PrBomOperation_Header_Route')
    ALTER TABLE dbo.PrBomOperation DROP CONSTRAINT UQ_PrBomOperation_Header_Route;
ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.PrBomOperation') AND name = 'UQ_PrBomOperation_Header_Route')
    DROP INDEX UQ_PrBomOperation_Header_Route ON dbo.PrBomOperation;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.PrBomOperation') AND name = 'UQ_PrBomOperation_RouteStep_Sequence')
    CREATE UNIQUE INDEX UQ_PrBomOperation_RouteStep_Sequence
        ON dbo.PrBomOperation (RouteStepID, ProcessSequence)
        WHERE RouteStepID IS NOT NULL;

IF COL_LENGTH('dbo.PrBomMachineOption', 'Priority') IS NULL
    ALTER TABLE dbo.PrBomMachineOption ADD Priority int NULL;
IF COL_LENGTH('dbo.PrBomMachineOption', 'OutputPerCycle') IS NULL
    ALTER TABLE dbo.PrBomMachineOption ADD OutputPerCycle decimal(18,4) NOT NULL
        CONSTRAINT DF_PrBomMachineOption_OutputPerCycle DEFAULT (1) WITH VALUES;
IF COL_LENGTH('dbo.PrBomMachineOption', 'MachineRatePerHour') IS NULL
    ALTER TABLE dbo.PrBomMachineOption ADD MachineRatePerHour decimal(18,6) NOT NULL
        CONSTRAINT DF_PrBomMachineOption_Rate DEFAULT (0) WITH VALUES;
GO

;WITH ranked AS (
    SELECT UID, ROW_NUMBER() OVER (PARTITION BY OperationID ORDER BY ResourceSequence, UID) AS rn
    FROM dbo.PrBomMachineOption
    WHERE Priority IS NULL
)
UPDATE m SET Priority = ranked.rn
FROM dbo.PrBomMachineOption m
INNER JOIN ranked ON ranked.UID = m.UID;

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PrBomMachineOption') AND name = 'Priority' AND is_nullable = 1)
    ALTER TABLE dbo.PrBomMachineOption ALTER COLUMN Priority int NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID('dbo.PrBomMachineOption') AND name = 'DF_PrBomMachineOption_Priority')
    ALTER TABLE dbo.PrBomMachineOption ADD CONSTRAINT DF_PrBomMachineOption_Priority DEFAULT (1) FOR Priority;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.PrBomMachineOption') AND name = 'CK_PrBomMachineOption_Phase1')
    ALTER TABLE dbo.PrBomMachineOption WITH CHECK ADD CONSTRAINT CK_PrBomMachineOption_Phase1
        CHECK (Priority > 0 AND OutputPerCycle > 0 AND MachineRatePerHour >= 0);

IF EXISTS (
    SELECT OperationID FROM dbo.PrBomMachineOption WHERE IsPrimary = 1
    GROUP BY OperationID HAVING COUNT(*) > 1)
    THROW 51001, 'Cannot add the one-default-machine index: an operation has multiple IsPrimary rows. Correct the draft data and rerun.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.PrBomMachineOption') AND name = 'UX_PrBomMachineOption_OneDefault')
    CREATE UNIQUE INDEX UX_PrBomMachineOption_OneDefault
        ON dbo.PrBomMachineOption (OperationID) WHERE IsPrimary = 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.PrBomMachineOption') AND name = 'UQ_PrBomMachineOption_Operation_Priority')
    CREATE UNIQUE INDEX UQ_PrBomMachineOption_Operation_Priority
        ON dbo.PrBomMachineOption (OperationID, Priority);

IF OBJECT_ID(N'dbo.PrBomLabourRequirement', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrBomLabourRequirement (
        UID bigint IDENTITY(1,1) NOT NULL,
        OperationID bigint NOT NULL,
        MachineOptionID bigint NULL,
        LabourCode nvarchar(20) NOT NULL,
        RequiredHeadcount decimal(9,4) NOT NULL,
        SetupMinutes decimal(18,4) NOT NULL CONSTRAINT DF_PrBomLabourRequirement_Setup DEFAULT (0),
        RunMinutes decimal(18,4) NOT NULL CONSTRAINT DF_PrBomLabourRequirement_Run DEFAULT (0),
        CostRate decimal(18,6) NOT NULL CONSTRAINT DF_PrBomLabourRequirement_Rate DEFAULT (0),
        CostBasis nvarchar(20) NOT NULL CONSTRAINT DF_PrBomLabourRequirement_Basis DEFAULT N'PER_HOUR',
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PrBomLabourRequirement PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PrBomLabourRequirement_Operation FOREIGN KEY (OperationID) REFERENCES dbo.PrBomOperation (UID) ON DELETE CASCADE,
        CONSTRAINT FK_PrBomLabourRequirement_Machine FOREIGN KEY (MachineOptionID) REFERENCES dbo.PrBomMachineOption (UID),
        CONSTRAINT CK_PrBomLabourRequirement_Headcount CHECK (RequiredHeadcount > 0),
        CONSTRAINT CK_PrBomLabourRequirement_TimeRate CHECK (SetupMinutes >= 0 AND RunMinutes >= 0 AND CostRate >= 0),
        CONSTRAINT CK_PrBomLabourRequirement_Basis CHECK (CostBasis IN (N'PER_HOUR', N'PER_OPERATION', N'PER_OUTPUT_UNIT'))
    );
    CREATE UNIQUE INDEX UX_PrBomLabourRequirement_OperationWide
        ON dbo.PrBomLabourRequirement (OperationID, LabourCode) WHERE MachineOptionID IS NULL;
    CREATE UNIQUE INDEX UX_PrBomLabourRequirement_MachineSpecific
        ON dbo.PrBomLabourRequirement (OperationID, MachineOptionID, LabourCode) WHERE MachineOptionID IS NOT NULL;
END;

IF COL_LENGTH('dbo.PrDefBOM', 'OperationID') IS NULL
    ALTER TABLE dbo.PrDefBOM ADD OperationID bigint NULL;
IF COL_LENGTH('dbo.PrDefBOM', 'IssueMethod') IS NULL
    ALTER TABLE dbo.PrDefBOM ADD IssueMethod nvarchar(20) NOT NULL
        CONSTRAINT DF_PrDefBOM_IssueMethod DEFAULT N'MANUAL' WITH VALUES;
IF COL_LENGTH('dbo.PrDefBOM', 'SupplySource') IS NULL
    ALTER TABLE dbo.PrDefBOM ADD SupplySource nvarchar(40) NOT NULL
        CONSTRAINT DF_PrDefBOM_SupplySource DEFAULT N'PURCHASED' WITH VALUES;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.PrDefBOM') AND name = 'CK_PrDefBOM_IssueMethod')
    ALTER TABLE dbo.PrDefBOM WITH CHECK ADD CONSTRAINT CK_PrDefBOM_IssueMethod
        CHECK (IssueMethod IN (N'MANUAL', N'BACKFLUSH', N'PICK_LIST'));
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.PrDefBOM') AND name = 'CK_PrDefBOM_SupplySource')
    ALTER TABLE dbo.PrDefBOM WITH CHECK ADD CONSTRAINT CK_PrDefBOM_SupplySource
        CHECK (SupplySource IN (N'PURCHASED', N'INTERNAL_ROUTE_WIP', N'SEPARATE_PRODUCT_DEFINITION', N'EXTERNAL_SUPPLY'));

UPDATE b SET OperationID = o.UID
FROM dbo.PrDefBOM b
INNER JOIN dbo.PrBomOperation o ON o.BomHdrID = b.BomHdrId AND o.OperationKey = b.OperationKey
WHERE b.OperationID IS NULL AND b.OperationKey IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PrDefBOM_Operation')
    ALTER TABLE dbo.PrDefBOM WITH CHECK
        ADD CONSTRAINT FK_PrDefBOM_Operation FOREIGN KEY (OperationID) REFERENCES dbo.PrBomOperation (UID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.PrDefBOM') AND name = 'IX_PrDefBOM_OperationID')
    CREATE INDEX IX_PrDefBOM_OperationID ON dbo.PrDefBOM (OperationID);
GO

-- In-house producer reference (plan section 5.4 and 6.6). NULL for purchased / external material.
-- The FK is deliberately NO ACTION so the only structural cascade path stays
-- PrBomHdr -> PrBomRouteStep; the service clears the reference before deleting route steps.
IF COL_LENGTH('dbo.PrDefBOM', 'ProducingRouteStepID') IS NULL
    ALTER TABLE dbo.PrDefBOM ADD ProducingRouteStepID bigint NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PrDefBOM_ProducingRouteStep')
    ALTER TABLE dbo.PrDefBOM WITH CHECK
        ADD CONSTRAINT FK_PrDefBOM_ProducingRouteStep FOREIGN KEY (ProducingRouteStepID)
            REFERENCES dbo.PrBomRouteStep (UID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.PrDefBOM') AND name = 'IX_PrDefBOM_ProducingRouteStepID')
    CREATE INDEX IX_PrDefBOM_ProducingRouteStepID ON dbo.PrDefBOM (ProducingRouteStepID);

-- NULL-safe equivalence: INTERNAL_ROUTE_WIP iff a producing route step is present. SupplySource is
-- NOT NULL, so the comparison never yields UNKNOWN and cannot be bypassed by a NULL SupplySource.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.PrDefBOM') AND name = 'CK_PrDefBOM_InternalWipProducer')
    ALTER TABLE dbo.PrDefBOM WITH CHECK ADD CONSTRAINT CK_PrDefBOM_InternalWipProducer
        CHECK ((SupplySource = N'INTERNAL_ROUTE_WIP' AND ProducingRouteStepID IS NOT NULL)
            OR (SupplySource <> N'INTERNAL_ROUTE_WIP' AND ProducingRouteStepID IS NULL));
GO

IF OBJECT_ID(N'dbo.PrBomMaterialBranchDefault', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrBomMaterialBranchDefault (
        UID bigint IDENTITY(1,1) NOT NULL,
        MaterialID bigint NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        WarehouseCode nvarchar(20) NOT NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PrBomMaterialBranchDefault PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PrBomMaterialBranchDefault_Material FOREIGN KEY (MaterialID) REFERENCES dbo.PrDefBOM (UID) ON DELETE CASCADE,
        CONSTRAINT UQ_PrBomMaterialBranchDefault_Material_Branch UNIQUE (MaterialID, BranchCode)
    );
END;

INSERT INTO dbo.PrBomMaterialBranchDefault (
    MaterialID, BranchCode, WarehouseCode, CreatedDate, CreatedBy, ModifiedDate, ModifiedBy)
SELECT b.UID, b.BranchCode, b.Warehouse, SYSUTCDATETIME(), N'migration', SYSUTCDATETIME(), N'migration'
FROM dbo.PrDefBOM b
WHERE NULLIF(LTRIM(RTRIM(b.BranchCode)), N'') IS NOT NULL
  AND NULLIF(LTRIM(RTRIM(b.Warehouse)), N'') IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.PrBomMaterialBranchDefault d WHERE d.MaterialID = b.UID AND d.BranchCode = b.BranchCode);

-- Legacy databases may key IvStockMaster by ICode alone. SQL Server requires the
-- exact referenced column list to be a declared candidate key before a composite FK
-- can target it. Refuse to guess if tenant/item duplicates already exist.
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    INNER JOIN sys.index_columns ic1
        ON ic1.object_id = i.object_id AND ic1.index_id = i.index_id AND ic1.key_ordinal = 1
    INNER JOIN sys.columns c1
        ON c1.object_id = ic1.object_id AND c1.column_id = ic1.column_id AND c1.name = N'CompanyCode'
    INNER JOIN sys.index_columns ic2
        ON ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.key_ordinal = 2
    INNER JOIN sys.columns c2
        ON c2.object_id = ic2.object_id AND c2.column_id = ic2.column_id AND c2.name = N'ICode'
    WHERE i.object_id = OBJECT_ID(N'dbo.IvStockMaster')
      AND i.is_unique = 1
      AND NOT EXISTS (
          SELECT 1 FROM sys.index_columns extra
          WHERE extra.object_id = i.object_id AND extra.index_id = i.index_id AND extra.key_ordinal > 2))
BEGIN
    IF EXISTS (
        SELECT CompanyCode, ICode
        FROM dbo.IvStockMaster
        GROUP BY CompanyCode, ICode
        HAVING COUNT_BIG(*) > 1)
        THROW 51002, 'Cannot create the IvStockMaster company/item candidate key because duplicate CompanyCode + ICode rows exist.', 1;

    CREATE UNIQUE INDEX UX_IvStockMaster_Company_ICode
        ON dbo.IvStockMaster (CompanyCode, ICode);
END;
GO

IF OBJECT_ID(N'dbo.IvItemUomConversion', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IvItemUomConversion (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        FromUOM nvarchar(10) NOT NULL,
        ToUOM nvarchar(10) NOT NULL,
        FromQty decimal(18,8) NOT NULL,
        ToQty decimal(18,8) NOT NULL,
        RoundingScale int NOT NULL CONSTRAINT DF_IvItemUomConversion_Scale DEFAULT (4),
        RoundingMode nvarchar(20) NOT NULL CONSTRAINT DF_IvItemUomConversion_Mode DEFAULT N'AWAY_FROM_ZERO',
        IsActive bit NOT NULL CONSTRAINT DF_IvItemUomConversion_Active DEFAULT (1),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_IvItemUomConversion PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_IvItemUomConversion_Item FOREIGN KEY (CompanyCode, ItemCode) REFERENCES dbo.IvStockMaster (CompanyCode, ICode),
        CONSTRAINT CK_IvItemUomConversion_Qty CHECK (FromQty > 0 AND ToQty > 0),
        CONSTRAINT CK_IvItemUomConversion_Scale CHECK (RoundingScale BETWEEN 0 AND 8),
        CONSTRAINT CK_IvItemUomConversion_DifferentUom CHECK (FromUOM <> ToUOM),
        CONSTRAINT CK_IvItemUomConversion_Mode CHECK (RoundingMode IN (N'AWAY_FROM_ZERO', N'TO_EVEN', N'CEILING', N'FLOOR'))
    );
    CREATE UNIQUE INDEX UX_IvItemUomConversion_ActivePair
        ON dbo.IvItemUomConversion (CompanyCode, ItemCode, FromUOM, ToUOM) WHERE IsActive = 1;
END;

COMMIT;
GO

PRINT N'Product Definition Phase 1 schema foundation is ready; existing revisions remain UNVERIFIED.';
GO
