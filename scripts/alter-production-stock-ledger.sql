SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.PrProductionLocation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrProductionLocation
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrProductionLocation PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        Code nvarchar(20) NOT NULL,
        Description nvarchar(200) NOT NULL,
        WorkCentreCode nvarchar(20) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_PrProductionLocation_Active DEFAULT (1),
        RowVersion rowversion NOT NULL,
        CONSTRAINT AK_PrProductionLocation_Tenant_Id UNIQUE (CompanyCode, BranchCode, Id),
        CONSTRAINT UQ_PrProductionLocation_Code UNIQUE (CompanyCode, BranchCode, Code)
    );
END;
GO

IF COL_LENGTH(N'dbo.PrProductionBalLot', N'BalanceStage') IS NULL ALTER TABLE dbo.PrProductionBalLot ADD BalanceStage nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLot', N'ProductionLocationID') IS NULL ALTER TABLE dbo.PrProductionBalLot ADD ProductionLocationID bigint NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLot', N'StockStatusCode') IS NULL ALTER TABLE dbo.PrProductionBalLot ADD StockStatusCode nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLot', N'PoolCode') IS NULL ALTER TABLE dbo.PrProductionBalLot ADD PoolCode nvarchar(64) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLot', N'PhysicalLotNo') IS NULL ALTER TABLE dbo.PrProductionBalLot ADD PhysicalLotNo nvarchar(50) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLot', N'LotIdentityKind') IS NULL ALTER TABLE dbo.PrProductionBalLot ADD LotIdentityKind nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLot', N'FirstReceiptEffectiveAt') IS NULL ALTER TABLE dbo.PrProductionBalLot ADD FirstReceiptEffectiveAt datetime2(7) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLot', N'LastStockEventEffectiveAt') IS NULL ALTER TABLE dbo.PrProductionBalLot ADD LastStockEventEffectiveAt datetime2(7) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLot', N'ContributionKey') IS NULL ALTER TABLE dbo.PrProductionBalLot ADD ContributionKey uniqueidentifier NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLot', N'OriginType') IS NULL ALTER TABLE dbo.PrProductionBalLot ADD OriginType nvarchar(20) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrProductionBalLot_ProductionLocation')
    ALTER TABLE dbo.PrProductionBalLot ADD CONSTRAINT FK_PrProductionBalLot_ProductionLocation
        FOREIGN KEY (CompanyCode, BranchCode, ProductionLocationID)
        REFERENCES dbo.PrProductionLocation(CompanyCode, BranchCode, Id);
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrProductionBalLot_V2')
    ALTER TABLE dbo.PrProductionBalLot WITH NOCHECK ADD CONSTRAINT CK_PrProductionBalLot_V2
        CHECK (BalanceStage IS NULL OR (ProductionLocationID IS NOT NULL AND StockStatusCode IS NOT NULL AND OriginType IS NOT NULL));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionBalLot') AND name = N'UQ_PrProductionBalLot_V2_Material')
    CREATE UNIQUE INDEX UQ_PrProductionBalLot_V2_Material
        ON dbo.PrProductionBalLot(CompanyCode, BranchCode, ContributionKey, ProductionLocationID, StockStatusCode)
        WHERE BalanceStage = N'MATERIAL' AND ContributionKey IS NOT NULL;
GO

IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'LedgerVersion') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD LedgerVersion tinyint NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'LedgerEpochID') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD LedgerEpochID bigint NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'StockPostingID') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD StockPostingID bigint NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'PostingLineNo') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD PostingLineNo int NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'CompanyCode') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD CompanyCode nvarchar(5) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'BranchCode') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD BranchCode nvarchar(5) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'ItemCode') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD ItemCode nvarchar(30) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'ItemDescription') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD ItemDescription nvarchar(200) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'BalanceStage') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD BalanceStage nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'ProductionLocationID') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD ProductionLocationID bigint NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'ProductionLocationCode') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD ProductionLocationCode nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'WorkCentreCode') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD WorkCentreCode nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'ProcessCode') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD ProcessCode nvarchar(30) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'LotIdentity') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD LotIdentity nvarchar(64) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'PhysicalLotNo') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD PhysicalLotNo nvarchar(50) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'StockStatusCode') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD StockStatusCode nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'WorkOrderNo') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD WorkOrderNo nvarchar(30) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'ConversionFactorToBase') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD ConversionFactorToBase decimal(18,8) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'MovementGroupId') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD MovementGroupId uniqueidentifier NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'SourceLineId') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD SourceLineId nvarchar(64) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'SplitOrdinal') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD SplitOrdinal int NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'InventoryHistoryId') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD InventoryHistoryId int NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'ValuationStatus') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD ValuationStatus nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'CostBasisVersion') IS NULL ALTER TABLE dbo.PrProductionBalLotMovement ADD CostBasisVersion int NULL;
GO

DECLARE @ProductionMovementTableId int = OBJECT_ID(N'dbo.PrProductionBalLotMovement', N'U');
DECLARE @MovementTypeColumnId int = COLUMNPROPERTY(@ProductionMovementTableId, N'MovementType', 'ColumnId');
DECLARE @MovementTypeSystemType sysname;
DECLARE @MovementTypeMaxLength smallint;
DECLARE @MovementTypeConstraintId int;
DECLARE @MovementTypeConstraintDefinition nvarchar(max);
DECLARE @NeedsWiden bit = 0;
DECLARE @NeedsConstraintRefresh bit = 0;
DECLARE @MustDropMovementTypeConstraint bit = 0;

IF @MovementTypeColumnId IS NULL
    THROW 51012, 'PrProductionBalLotMovement.MovementType is missing; manual remediation is required.', 1;

SELECT
    @MovementTypeSystemType = type_name(c.system_type_id),
    @MovementTypeMaxLength = c.max_length
FROM sys.columns AS c
WHERE c.object_id = @ProductionMovementTableId
  AND c.column_id = @MovementTypeColumnId;

IF @MovementTypeSystemType <> N'nvarchar'
    THROW 51012, 'PrProductionBalLotMovement.MovementType is not nvarchar; manual remediation is required.', 1;

SELECT
    @MovementTypeConstraintId = cc.object_id,
    @MovementTypeConstraintDefinition = cc.definition
FROM sys.check_constraints AS cc
WHERE cc.parent_object_id = @ProductionMovementTableId
  AND cc.name = N'CK_PrProductionBalLotMovement_Type';

DECLARE @ExpectedMovementTypes table (Code nvarchar(32) NOT NULL PRIMARY KEY);
INSERT INTO @ExpectedMovementTypes (Code) VALUES
    (N'OPENING_IN'), (N'ISSUE'), (N'ISSUE_REVERSAL'), (N'PRODUCE'), (N'PRODUCE_REVERSAL'),
    (N'CONSUME'), (N'CONSUME_REVERSAL'), (N'RETURN'), (N'RETURN_REVERSAL'),
    (N'TRANSFER_OUT'), (N'TRANSFER_IN'), (N'STATUS_OUT'), (N'STATUS_IN'),
    (N'ADJUST_IN'), (N'ADJUST_OUT'), (N'SCRAP_OUT'), (N'FG_RECEIPT_OUT'), (N'FG_RECEIPT_REVERSAL');

SET @NeedsWiden = CASE
    WHEN @MovementTypeMaxLength > 0 AND @MovementTypeMaxLength < 64 THEN 1
    ELSE 0
END;

SET @NeedsConstraintRefresh = CASE
    WHEN @MovementTypeConstraintId IS NULL THEN 1
    WHEN EXISTS
    (
        SELECT 1
        FROM @ExpectedMovementTypes AS expected
        WHERE CHARINDEX(
            N'''' + expected.Code + N'''',
            UPPER(@MovementTypeConstraintDefinition) COLLATE Latin1_General_100_CI_AS) = 0
    ) THEN 1
    ELSE 0
END;

SET @MustDropMovementTypeConstraint = @NeedsConstraintRefresh;

-- SQL Server normally permits a widening ALTER with this check in place. If the
-- deployed check is recorded as a column dependency, remove and recreate only it.
IF @NeedsWiden = 1
   AND @MovementTypeConstraintId IS NOT NULL
   AND EXISTS
   (
       SELECT 1
       FROM sys.sql_expression_dependencies AS dependency
       WHERE dependency.referencing_id = @MovementTypeConstraintId
         AND dependency.referenced_id = @ProductionMovementTableId
         AND dependency.referenced_minor_id = @MovementTypeColumnId
   )
    SET @MustDropMovementTypeConstraint = 1;

IF @MustDropMovementTypeConstraint = 1
   AND @MovementTypeConstraintId IS NOT NULL
    ALTER TABLE dbo.PrProductionBalLotMovement DROP CONSTRAINT CK_PrProductionBalLotMovement_Type;

IF @NeedsWiden = 1
    ALTER TABLE dbo.PrProductionBalLotMovement ALTER COLUMN MovementType nvarchar(32) NOT NULL;

IF @MovementTypeConstraintId IS NULL OR @MustDropMovementTypeConstraint = 1
    ALTER TABLE dbo.PrProductionBalLotMovement ADD CONSTRAINT CK_PrProductionBalLotMovement_Type CHECK
        (MovementType IN (N'OPENING_IN',N'ISSUE',N'ISSUE_REVERSAL',N'PRODUCE',N'PRODUCE_REVERSAL',N'CONSUME',N'CONSUME_REVERSAL',N'RETURN',N'RETURN_REVERSAL',N'TRANSFER_OUT',N'TRANSFER_IN',N'STATUS_OUT',N'STATUS_IN',N'ADJUST_IN',N'ADJUST_OUT',N'SCRAP_OUT',N'FG_RECEIPT_OUT',N'FG_RECEIPT_REVERSAL'));
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrProductionBalLotMovement_V2')
    ALTER TABLE dbo.PrProductionBalLotMovement WITH NOCHECK ADD CONSTRAINT CK_PrProductionBalLotMovement_V2 CHECK
        (LedgerVersion IS NULL OR (LedgerVersion = 2 AND LedgerEpochID IS NOT NULL AND StockPostingID IS NOT NULL AND PostingLineNo > 0 AND CompanyCode IS NOT NULL AND BranchCode IS NOT NULL AND ConversionFactorToBase > 0));
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrProductionBalLotMovement_StockPosting')
    ALTER TABLE dbo.PrProductionBalLotMovement ADD CONSTRAINT FK_PrProductionBalLotMovement_StockPosting
        FOREIGN KEY (CompanyCode, BranchCode, StockPostingID) REFERENCES dbo.StockPosting(CompanyCode, BranchCode, Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionBalLotMovement') AND name = N'UQ_PrProductionBalLotMovement_V2_PostingLine')
    CREATE UNIQUE INDEX UQ_PrProductionBalLotMovement_V2_PostingLine ON dbo.PrProductionBalLotMovement(StockPostingID, PostingLineNo) WHERE StockPostingID IS NOT NULL;
IF COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'OriginalMovementID') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionBalLotMovement') AND name = N'UQ_PrProductionBalLotMovement_V2_Reversal')
    CREATE UNIQUE INDEX UQ_PrProductionBalLotMovement_V2_Reversal ON dbo.PrProductionBalLotMovement(OriginalMovementID) WHERE LedgerVersion = 2 AND OriginalMovementID IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionBalLotMovement') AND name = N'IX_PrProductionBalLotMovement_V2_Card')
    CREATE INDEX IX_PrProductionBalLotMovement_V2_Card ON dbo.PrProductionBalLotMovement(CompanyCode, BranchCode, LedgerEpochID, ItemCode, MovementDate, StockPostingID, PostingLineNo);
GO

IF COL_LENGTH(N'dbo.PrMaterialMovement', N'StockPostingID') IS NULL ALTER TABLE dbo.PrMaterialMovement ADD StockPostingID bigint NULL;
IF COL_LENGTH(N'dbo.PrMaterialMovement', N'SourceLineId') IS NULL ALTER TABLE dbo.PrMaterialMovement ADD SourceLineId nvarchar(64) NULL;
IF COL_LENGTH(N'dbo.PrMaterialMovement', N'SplitOrdinal') IS NULL ALTER TABLE dbo.PrMaterialMovement ADD SplitOrdinal int NULL;
IF COL_LENGTH(N'dbo.PrMaterialMovement', N'SourceIssueMovementID') IS NULL ALTER TABLE dbo.PrMaterialMovement ADD SourceIssueMovementID bigint NULL;
IF COL_LENGTH(N'dbo.PrMaterialMovement', N'ReversesMaterialMovementID') IS NULL ALTER TABLE dbo.PrMaterialMovement ADD ReversesMaterialMovementID bigint NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrMaterialMovement_StockPosting')
    ALTER TABLE dbo.PrMaterialMovement ADD CONSTRAINT FK_PrMaterialMovement_StockPosting
        FOREIGN KEY (CompanyCode, BranchCode, StockPostingID) REFERENCES dbo.StockPosting(CompanyCode, BranchCode, Id);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrMaterialMovement_SourceIssue')
    ALTER TABLE dbo.PrMaterialMovement ADD CONSTRAINT FK_PrMaterialMovement_SourceIssue FOREIGN KEY (SourceIssueMovementID) REFERENCES dbo.PrMaterialMovement(UID);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrMaterialMovement_Reverses')
    ALTER TABLE dbo.PrMaterialMovement ADD CONSTRAINT FK_PrMaterialMovement_Reverses FOREIGN KEY (ReversesMaterialMovementID) REFERENCES dbo.PrMaterialMovement(UID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement') AND name = N'UQ_PrMaterialMovement_V2_SourceLine')
    CREATE UNIQUE INDEX UQ_PrMaterialMovement_V2_SourceLine ON dbo.PrMaterialMovement(StockPostingID, SourceLineId, SplitOrdinal, MovementType) WHERE StockPostingID IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrMaterialMovement') AND name = N'UQ_PrMaterialMovement_V2_Reversal')
    CREATE UNIQUE INDEX UQ_PrMaterialMovement_V2_Reversal ON dbo.PrMaterialMovement(ReversesMaterialMovementID) WHERE ReversesMaterialMovementID IS NOT NULL;
GO

IF OBJECT_ID(N'dbo.PrProductionMovementAllocation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrProductionMovementAllocation
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrProductionMovementAllocation PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        StockPostingID bigint NOT NULL,
        ReceiptMovementId bigint NOT NULL,
        OutboundMovementId bigint NOT NULL,
        BaseQty decimal(18,4) NOT NULL,
        ReversesAllocationId bigint NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CONSTRAINT FK_PrProductionMovementAllocation_Posting FOREIGN KEY (CompanyCode, BranchCode, StockPostingID) REFERENCES dbo.StockPosting(CompanyCode, BranchCode, Id),
        CONSTRAINT FK_PrProductionMovementAllocation_Receipt FOREIGN KEY (ReceiptMovementId) REFERENCES dbo.PrProductionBalLotMovement(UID),
        CONSTRAINT FK_PrProductionMovementAllocation_Outbound FOREIGN KEY (OutboundMovementId) REFERENCES dbo.PrProductionBalLotMovement(UID),
        CONSTRAINT FK_PrProductionMovementAllocation_Reverses FOREIGN KEY (ReversesAllocationId) REFERENCES dbo.PrProductionMovementAllocation(Id),
        CONSTRAINT CK_PrProductionMovementAllocation_Qty CHECK (BaseQty > 0),
        CONSTRAINT CK_PrProductionMovementAllocation_Distinct CHECK (ReceiptMovementId <> OutboundMovementId),
        CONSTRAINT CK_PrProductionMovementAllocation_NoSelfReverse CHECK (ReversesAllocationId IS NULL OR ReversesAllocationId <> Id)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionMovementAllocation') AND name = N'UQ_PrProductionMovementAllocation_Normal')
    CREATE UNIQUE INDEX UQ_PrProductionMovementAllocation_Normal ON dbo.PrProductionMovementAllocation(StockPostingID, ReceiptMovementId, OutboundMovementId) WHERE ReversesAllocationId IS NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionMovementAllocation') AND name = N'UQ_PrProductionMovementAllocation_Reversal')
    CREATE UNIQUE INDEX UQ_PrProductionMovementAllocation_Reversal ON dbo.PrProductionMovementAllocation(ReversesAllocationId) WHERE ReversesAllocationId IS NOT NULL;
GO

CREATE OR ALTER TRIGGER dbo.TR_PrProductionBalLotMovement_Seal ON dbo.PrProductionBalLotMovement AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted d JOIN dbo.StockPosting p ON p.Id = d.StockPostingID WHERE p.SealedAtUtc IS NOT NULL)
       OR EXISTS (SELECT 1 FROM inserted i JOIN dbo.StockPosting p ON p.Id = i.StockPostingID WHERE p.SealedAtUtc IS NOT NULL)
        THROW 51001, 'SEALED_PRODUCTION_MOVEMENT_IMMUTABLE', 1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_PrMaterialMovement_Seal ON dbo.PrMaterialMovement AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted d JOIN dbo.StockPosting p ON p.Id = d.StockPostingID WHERE p.SealedAtUtc IS NOT NULL)
       OR EXISTS (SELECT 1 FROM inserted i JOIN dbo.StockPosting p ON p.Id = i.StockPostingID WHERE p.SealedAtUtc IS NOT NULL)
        THROW 51002, 'SEALED_MATERIAL_MOVEMENT_IMMUTABLE', 1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_PrProductionMovementAllocation_Seal ON dbo.PrProductionMovementAllocation AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted d JOIN dbo.StockPosting p ON p.Id = d.StockPostingID WHERE p.SealedAtUtc IS NOT NULL)
       OR EXISTS (SELECT 1 FROM inserted i JOIN dbo.StockPosting p ON p.Id = i.StockPostingID WHERE p.SealedAtUtc IS NOT NULL)
        THROW 51003, 'SEALED_PRODUCTION_ALLOCATION_IMMUTABLE', 1;
END;
GO
