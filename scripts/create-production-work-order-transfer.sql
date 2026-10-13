-- Idempotent additive schema for V2 Production Work Order Transfer.
-- Does not alter ProductionBalLot identity indexes or existing stock balances.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- Validate the Production V2 foundation before changing the schema. Show every
-- missing prerequisite in one result set so deployment does not fail one table at a time.
DECLARE @RequiredInfrastructure TABLE
(
    TableName sysname NOT NULL PRIMARY KEY,
    ApplyScript nvarchar(128) NOT NULL
);
INSERT INTO @RequiredInfrastructure(TableName, ApplyScript) VALUES
    (N'PrProductionBalLot', N'scripts/create-production-daily-output.sql'),
    (N'PrWorkOrder', N'scripts/create-production-workorder.sql'),
    (N'PrProductionPostingLink', N'scripts/create-production-workorder.sql'),
    (N'PrProductionBalLotMovement', N'scripts/create-production-daily-output.sql'),
    (N'PrProductionMovementAllocation', N'scripts/alter-production-stock-ledger.sql'),
    (N'PrValuationEvidence', N'scripts/create-finished-good-receipt.sql'),
    (N'PrPoolDependency', N'scripts/create-finished-good-receipt.sql'),
    (N'PrPoolValuation', N'scripts/create-finished-good-receipt.sql'),
    (N'StockPosting', N'scripts/create-stock-posting-ledger.sql');

IF EXISTS
(
    SELECT 1
    FROM @RequiredInfrastructure r
    LEFT JOIN sys.tables t
      ON t.name = r.TableName AND t.schema_id = SCHEMA_ID(N'dbo')
    WHERE t.object_id IS NULL
)
BEGIN
    SELECT N'dbo.' + r.TableName AS MissingTable, r.ApplyScript
    FROM @RequiredInfrastructure r
    LEFT JOIN sys.tables t
      ON t.name = r.TableName AND t.schema_id = SCHEMA_ID(N'dbo')
    WHERE t.object_id IS NULL
    ORDER BY r.TableName;
    ;THROW 51011, 'Missing Production V2 prerequisites; see the result set for required scripts.', 1;
END;

DECLARE @RequiredIndexes TABLE (TableName sysname NOT NULL, IndexName sysname NOT NULL, PRIMARY KEY (TableName, IndexName));
INSERT INTO @RequiredIndexes(TableName, IndexName) VALUES
    (N'PrProductionBalLot', N'UQ_PrProductionBalLot_Wip'),
    (N'PrProductionBalLot', N'UQ_PrProductionBalLot_V2_Wip'),
    (N'PrProductionBalLotMovement', N'UQ_PrProductionBalLotMovement_V2_PostingLine'),
    (N'PrProductionBalLotMovement', N'UQ_PrProductionBalLotMovement_V2_Reversal'),
    (N'PrProductionMovementAllocation', N'UQ_PrProductionMovementAllocation_Normal'),
    (N'PrProductionMovementAllocation', N'UQ_PrProductionMovementAllocation_Reversal'),
    (N'PrProductionPostingLink', N'UQ_PrProductionPostingLink_Idempotency'),
    (N'StockPosting', N'UQ_StockPosting_SourceRevisionRole'),
    (N'StockPosting', N'UQ_StockPosting_Reversal');
IF EXISTS
(
    SELECT 1
    FROM @RequiredIndexes r
    LEFT JOIN sys.tables t
      ON t.name = r.TableName AND t.schema_id = SCHEMA_ID(N'dbo')
    LEFT JOIN sys.indexes i
      ON i.object_id = t.object_id
     AND i.name = r.IndexName
    WHERE i.index_id IS NULL OR i.is_unique = 0 OR i.is_disabled = 1
)
    THROW 51013, 'A required Production WIP identity or posting uniqueness index is missing, non-unique, or disabled.', 1;

IF COL_LENGTH(N'dbo.PrProductionPostingLink', N'CommandType') IS NULL
   OR COL_LENGTH(N'dbo.PrProductionPostingLink', N'ProductionDocumentLineID') IS NULL
   OR COL_LENGTH(N'dbo.PrProductionPostingLink', N'DocumentRevision') IS NULL
   OR COL_LENGTH(N'dbo.PrProductionPostingLink', N'PostingRequestId') IS NULL
   OR COL_LENGTH(N'dbo.StockPosting', N'PostingRole') IS NULL
   OR COL_LENGTH(N'dbo.StockPosting', N'SealedAtUtc') IS NULL
   OR COL_LENGTH(N'dbo.StockPosting', N'ProductionPostingLinkId') IS NULL
    THROW 51014, 'ProductionPostingLink or StockPosting is missing current lifecycle columns required by Work Order Transfer.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.PrProductionBalLotMovement')
      AND name = N'CK_PrProductionBalLotMovement_Type'
      AND is_disabled = 0 AND is_not_trusted = 0
      AND CHARINDEX(N'TRANSFER_OUT', UPPER(definition)) > 0
      AND CHARINDEX(N'TRANSFER_IN', UPPER(definition)) > 0
)
    THROW 51015, 'The trusted Production movement type constraint does not allow transfer movements.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID(N'dbo.PrProductionBalLotMovement')
      AND name = N'FK_PrProductionBalLotMovement_StockPosting'
      AND is_disabled = 0 AND is_not_trusted = 0
)
    THROW 51016, 'The Production movement to StockPosting foreign key must be enabled and trusted.', 1;

DECLARE @RequiredForeignKeys TABLE (TableName sysname NOT NULL, ConstraintName sysname NOT NULL, PRIMARY KEY (TableName, ConstraintName));
INSERT INTO @RequiredForeignKeys(TableName, ConstraintName) VALUES
    (N'PrProductionBalLot', N'FK_PrProductionBalLot_WorkOrder'),
    (N'PrProductionBalLot', N'FK_PrProductionBalLot_ProductionLocation'),
    (N'PrProductionBalLotMovement', N'FK_PrBalLotMov_Lot'),
    (N'PrProductionMovementAllocation', N'FK_PrProductionMovementAllocation_Posting'),
    (N'PrProductionMovementAllocation', N'FK_PrProductionMovementAllocation_Receipt'),
    (N'PrProductionMovementAllocation', N'FK_PrProductionMovementAllocation_Outbound'),
    (N'PrProductionMovementAllocation', N'FK_PrProductionMovementAllocation_Reverses'),
    (N'PrPoolValuation', N'FK_PrPoolValuation_PrProductionBalLot_ProductionBalLotId'),
    (N'PrPoolDependency', N'FK_PrPoolDependency_PrPoolDependency_ReversesDependencyId'),
    (N'PrPoolDependency', N'FK_PrPoolDependency_PrProductionBalLotMovement_ConsumerMovementId'),
    (N'PrPoolDependency', N'FK_PrPoolDependency_PrProductionBalLotMovement_ContributorMovementId'),
    (N'PrPoolDependency', N'FK_PrPoolDependency_StockPosting_StockPostingId'),
    (N'PrValuationEvidence', N'FK_PrValuationEvidence_IvTrxHistory_InventoryHistoryId'),
    (N'PrValuationEvidence', N'FK_PrValuationEvidence_PrProductionBalLotMovement_MovementId'),
    (N'PrValuationEvidence', N'FK_PrValuationEvidence_PrProductionBalLotMovement_OriginalMovementId'),
    (N'PrValuationEvidence', N'FK_PrValuationEvidence_PrProductionBalLot_ProductionBalLotId'),
    (N'StockPosting', N'FK_StockPosting_Epoch'),
    (N'StockPosting', N'FK_StockPosting_Reverses');
IF EXISTS
(
    SELECT 1
    FROM @RequiredForeignKeys r
    LEFT JOIN sys.tables t
      ON t.name = r.TableName AND t.schema_id = SCHEMA_ID(N'dbo')
    LEFT JOIN sys.foreign_keys fk
      ON fk.parent_object_id = t.object_id
     AND fk.name = r.ConstraintName
    WHERE fk.object_id IS NULL OR fk.is_disabled = 1 OR fk.is_not_trusted = 1
)
    THROW 51019, 'A required Production valuation, allocation, dependency, balance, or posting foreign key is missing, disabled, or untrusted.', 1;

IF OBJECT_ID(N'dbo.PrWorkOrderTransfer', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkOrderTransfer
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrWorkOrderTransfer PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        TransferNo nvarchar(30) NOT NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_PrWorkOrderTransfer_Status DEFAULT N'NEW',
        TransferDateTime datetime2(7) NOT NULL,
        Remark nvarchar(250) NULL,
        SourceProductionBalLotID bigint NOT NULL,
        SourceWorkOrderID bigint NOT NULL,
        SourceWorkOrderNo nvarchar(30) NOT NULL,
        SourceProducingRouteStepID bigint NOT NULL,
        SourceProducerOperationID bigint NOT NULL,
        SourceWorkCentreCode nvarchar(20) NOT NULL,
        SourceProcessCode nvarchar(30) NOT NULL,
        SourceProductionLocationID bigint NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        LotNo nvarchar(50) NOT NULL,
        PhysicalLotNo nvarchar(50) NULL,
        PoolCode nvarchar(64) NULL,
        LotIdentityKind nvarchar(20) NULL,
        Qty decimal(18,4) NOT NULL,
        Uom nvarchar(10) NOT NULL,
        BaseQty decimal(18,4) NOT NULL,
        BaseUom nvarchar(10) NOT NULL,
        ConversionFactorToBase decimal(18,8) NOT NULL,
        TransferTotalCost decimal(19,6) NOT NULL,
        SourceLastMovementDate datetime2(7) NULL,
        SourceLastStockEventEffectiveAt datetime2(7) NULL,
        SourceBalanceRowVersion varbinary(8) NOT NULL,
        TargetWorkOrderID bigint NOT NULL,
        TargetWorkOrderNo nvarchar(30) NOT NULL,
        TargetProducingRouteStepID bigint NOT NULL,
        TargetProducerOperationID bigint NOT NULL,
        TargetProducerWorkCentreCode nvarchar(20) NOT NULL,
        TargetProducerProcessCode nvarchar(30) NOT NULL,
        TargetProducerProductionLocationID bigint NOT NULL,
        TargetWorkOrderMaterialID bigint NOT NULL,
        TargetConsumingRouteStepID bigint NOT NULL,
        TargetConsumingOperationID bigint NOT NULL,
        TargetConsumingWorkCentreCode nvarchar(20) NOT NULL,
        TargetConsumingProcessCode nvarchar(30) NOT NULL,
        TargetConsumerProductionLocationID bigint NULL,
        TargetSnapshotRevision int NOT NULL,
        TargetSnapshotHash nvarchar(64) NOT NULL,
        PostingRequestID nvarchar(32) NOT NULL,
        ProductionPostingLinkID bigint NOT NULL,
        StockPostingID bigint NULL,
        DestinationProductionBalLotID bigint NULL,
        TransferOutMovementID bigint NULL,
        TransferInMovementID bigint NULL,
        MovementGroupID uniqueidentifier NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(10) NOT NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        PostedDate datetime2 NULL,
        PostedBy nvarchar(10) NULL,
        DeletedAtUtc datetime2(7) NULL,
        DeletedBy nvarchar(10) NULL,
        DeleteReason nvarchar(250) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_PrWorkOrderTransfer_Status CHECK (Status IN (N'NEW',N'POSTED')),
        CONSTRAINT CK_PrWorkOrderTransfer_Quantities CHECK
            (Qty > 0 AND BaseQty > 0 AND ConversionFactorToBase > 0 AND TransferTotalCost >= 0),
        CONSTRAINT CK_PrWorkOrderTransfer_Contexts CHECK
            (SourceWorkOrderID <> TargetWorkOrderID AND SourceProducingRouteStepID > 0 AND TargetProducingRouteStepID > 0),
        CONSTRAINT FK_PrWorkOrderTransfer_SourceBalance FOREIGN KEY (SourceProductionBalLotID)
            REFERENCES dbo.PrProductionBalLot(UID),
        CONSTRAINT FK_PrWorkOrderTransfer_DestinationBalance FOREIGN KEY (DestinationProductionBalLotID)
            REFERENCES dbo.PrProductionBalLot(UID),
        CONSTRAINT FK_PrWorkOrderTransfer_SourceWorkOrder FOREIGN KEY (SourceWorkOrderID)
            REFERENCES dbo.PrWorkOrder(UID),
        CONSTRAINT FK_PrWorkOrderTransfer_TargetWorkOrder FOREIGN KEY (TargetWorkOrderID)
            REFERENCES dbo.PrWorkOrder(UID),
        CONSTRAINT FK_PrWorkOrderTransfer_PostingLink FOREIGN KEY (ProductionPostingLinkID)
            REFERENCES dbo.PrProductionPostingLink(UID),
        CONSTRAINT FK_PrWorkOrderTransfer_StockPosting FOREIGN KEY (StockPostingID)
            REFERENCES dbo.StockPosting(Id),
        CONSTRAINT FK_PrWorkOrderTransfer_OutMovement FOREIGN KEY (TransferOutMovementID)
            REFERENCES dbo.PrProductionBalLotMovement(UID),
        CONSTRAINT FK_PrWorkOrderTransfer_InMovement FOREIGN KEY (TransferInMovementID)
            REFERENCES dbo.PrProductionBalLotMovement(UID)
    );
END;

-- Recover an interrupted deployment. A populated partial table cannot be assigned
-- invented required evidence, so fail before changing it when required columns are absent.
DECLARE @TransferColumns TABLE
(
    ColumnName sysname NOT NULL PRIMARY KEY,
    ColumnType nvarchar(40) NOT NULL,
    IsRequired bit NOT NULL
);
INSERT INTO @TransferColumns(ColumnName, ColumnType, IsRequired) VALUES
    (N'CompanyCode', N'nvarchar(5)', 1),
    (N'BranchCode', N'nvarchar(5)', 1),
    (N'TransferNo', N'nvarchar(30)', 1),
    (N'Status', N'nvarchar(20)', 1),
    (N'TransferDateTime', N'datetime2(7)', 1),
    (N'Remark', N'nvarchar(250)', 0),
    (N'SourceProductionBalLotID', N'bigint', 1),
    (N'SourceWorkOrderID', N'bigint', 1),
    (N'SourceWorkOrderNo', N'nvarchar(30)', 1),
    (N'SourceProducingRouteStepID', N'bigint', 1),
    (N'SourceProducerOperationID', N'bigint', 1),
    (N'SourceWorkCentreCode', N'nvarchar(20)', 1),
    (N'SourceProcessCode', N'nvarchar(30)', 1),
    (N'SourceProductionLocationID', N'bigint', 1),
    (N'ItemCode', N'nvarchar(30)', 1),
    (N'LotNo', N'nvarchar(50)', 1),
    (N'PhysicalLotNo', N'nvarchar(50)', 0),
    (N'PoolCode', N'nvarchar(64)', 0),
    (N'LotIdentityKind', N'nvarchar(20)', 0),
    (N'Qty', N'decimal(18,4)', 1),
    (N'Uom', N'nvarchar(10)', 1),
    (N'BaseQty', N'decimal(18,4)', 1),
    (N'BaseUom', N'nvarchar(10)', 1),
    (N'ConversionFactorToBase', N'decimal(18,8)', 1),
    (N'TransferTotalCost', N'decimal(19,6)', 1),
    (N'SourceLastMovementDate', N'datetime2(7)', 0),
    (N'SourceLastStockEventEffectiveAt', N'datetime2(7)', 0),
    (N'SourceBalanceRowVersion', N'varbinary(8)', 1),
    (N'TargetWorkOrderID', N'bigint', 1),
    (N'TargetWorkOrderNo', N'nvarchar(30)', 1),
    (N'TargetProducingRouteStepID', N'bigint', 1),
    (N'TargetProducerOperationID', N'bigint', 1),
    (N'TargetProducerWorkCentreCode', N'nvarchar(20)', 1),
    (N'TargetProducerProcessCode', N'nvarchar(30)', 1),
    (N'TargetProducerProductionLocationID', N'bigint', 1),
    (N'TargetWorkOrderMaterialID', N'bigint', 1),
    (N'TargetConsumingRouteStepID', N'bigint', 1),
    (N'TargetConsumingOperationID', N'bigint', 1),
    (N'TargetConsumingWorkCentreCode', N'nvarchar(20)', 1),
    (N'TargetConsumingProcessCode', N'nvarchar(30)', 1),
    (N'TargetConsumerProductionLocationID', N'bigint', 0),
    (N'TargetSnapshotRevision', N'int', 1),
    (N'TargetSnapshotHash', N'nvarchar(64)', 1),
    (N'PostingRequestID', N'nvarchar(32)', 1),
    (N'ProductionPostingLinkID', N'bigint', 1),
    (N'StockPostingID', N'bigint', 0),
    (N'DestinationProductionBalLotID', N'bigint', 0),
    (N'TransferOutMovementID', N'bigint', 0),
    (N'TransferInMovementID', N'bigint', 0),
    (N'MovementGroupID', N'uniqueidentifier', 0),
    (N'CreatedDate', N'datetime2(7)', 1),
    (N'CreatedBy', N'nvarchar(10)', 1),
    (N'ModifiedDate', N'datetime2(7)', 0),
    (N'ModifiedBy', N'nvarchar(10)', 0),
    (N'PostedDate', N'datetime2(7)', 0),
    (N'PostedBy', N'nvarchar(10)', 0),
    (N'DeletedAtUtc', N'datetime2(7)', 0),
    (N'DeletedBy', N'nvarchar(10)', 0),
    (N'DeleteReason', N'nvarchar(250)', 0);

IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', N'UID') IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.PrWorkOrderTransfer)
    BEGIN
        ;THROW 51000, 'Cannot add transfer UID to a populated partial table safely.', 1;
    END;
    ALTER TABLE dbo.PrWorkOrderTransfer ADD UID bigint IDENTITY(1,1) NOT NULL;
END;

IF EXISTS (SELECT 1 FROM dbo.PrWorkOrderTransfer)
   AND EXISTS
   (
       SELECT 1 FROM @TransferColumns c
       WHERE c.IsRequired = 1
         AND COL_LENGTH(N'dbo.PrWorkOrderTransfer', c.ColumnName) IS NULL
   )
BEGIN
    ;THROW 51001, 'Transfer table has rows and is missing required evidence columns; review the partial deployment before continuing.', 1;
END;

IF COLUMNPROPERTY(OBJECT_ID(N'dbo.PrWorkOrderTransfer'), N'UID', 'IsIdentity') <> 1
BEGIN
    ;THROW 51002, 'Transfer UID must be an identity column; review the partial deployment before continuing.', 1;
END;

DECLARE @ColumnName sysname, @ColumnType nvarchar(40), @IsRequired bit,
        @Nullability nvarchar(10), @AddColumnSql nvarchar(max);
DECLARE transfer_column_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT ColumnName, ColumnType, IsRequired FROM @TransferColumns ORDER BY ColumnName;
OPEN transfer_column_cursor;
FETCH NEXT FROM transfer_column_cursor INTO @ColumnName, @ColumnType, @IsRequired;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', @ColumnName) IS NULL
    BEGIN
        SET @Nullability = CASE WHEN @IsRequired = 1 THEN N' NOT NULL' ELSE N' NULL' END;
        SET @AddColumnSql = N'ALTER TABLE dbo.PrWorkOrderTransfer ADD ' + QUOTENAME(@ColumnName) + N' ' + @ColumnType + @Nullability + N';';
        EXEC sys.sp_executesql @AddColumnSql;
    END;
    FETCH NEXT FROM transfer_column_cursor INTO @ColumnName, @ColumnType, @IsRequired;
END;
CLOSE transfer_column_cursor;
DEALLOCATE transfer_column_cursor;

IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', N'RowVersion') IS NULL
    ALTER TABLE dbo.PrWorkOrderTransfer ADD RowVersion rowversion NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND type=N'PK')
    ALTER TABLE dbo.PrWorkOrderTransfer ADD CONSTRAINT PK_PrWorkOrderTransfer PRIMARY KEY (UID);

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND parent_column_id=COLUMNPROPERTY(OBJECT_ID(N'dbo.PrWorkOrderTransfer'), N'Status', 'ColumnId'))
    ALTER TABLE dbo.PrWorkOrderTransfer ADD CONSTRAINT DF_PrWorkOrderTransfer_Status DEFAULT N'NEW' FOR Status;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'CK_PrWorkOrderTransfer_Status')
    ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK ADD CONSTRAINT CK_PrWorkOrderTransfer_Status CHECK (Status IN (N'NEW',N'POSTED'));
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'CK_PrWorkOrderTransfer_Quantities')
    ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK ADD CONSTRAINT CK_PrWorkOrderTransfer_Quantities CHECK
        (Qty > 0 AND BaseQty > 0 AND ConversionFactorToBase > 0 AND TransferTotalCost >= 0);
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'CK_PrWorkOrderTransfer_Contexts')
    ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK ADD CONSTRAINT CK_PrWorkOrderTransfer_Contexts CHECK
        (SourceWorkOrderID <> TargetWorkOrderID AND SourceProducingRouteStepID > 0 AND TargetProducingRouteStepID > 0);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'FK_PrWorkOrderTransfer_SourceBalance')
    ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK ADD CONSTRAINT FK_PrWorkOrderTransfer_SourceBalance FOREIGN KEY (SourceProductionBalLotID) REFERENCES dbo.PrProductionBalLot(UID);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'FK_PrWorkOrderTransfer_DestinationBalance')
    ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK ADD CONSTRAINT FK_PrWorkOrderTransfer_DestinationBalance FOREIGN KEY (DestinationProductionBalLotID) REFERENCES dbo.PrProductionBalLot(UID);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'FK_PrWorkOrderTransfer_SourceWorkOrder')
    ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK ADD CONSTRAINT FK_PrWorkOrderTransfer_SourceWorkOrder FOREIGN KEY (SourceWorkOrderID) REFERENCES dbo.PrWorkOrder(UID);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'FK_PrWorkOrderTransfer_TargetWorkOrder')
    ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK ADD CONSTRAINT FK_PrWorkOrderTransfer_TargetWorkOrder FOREIGN KEY (TargetWorkOrderID) REFERENCES dbo.PrWorkOrder(UID);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'FK_PrWorkOrderTransfer_PostingLink')
    ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK ADD CONSTRAINT FK_PrWorkOrderTransfer_PostingLink FOREIGN KEY (ProductionPostingLinkID) REFERENCES dbo.PrProductionPostingLink(UID);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'FK_PrWorkOrderTransfer_StockPosting')
    ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK ADD CONSTRAINT FK_PrWorkOrderTransfer_StockPosting FOREIGN KEY (StockPostingID) REFERENCES dbo.StockPosting(Id);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'FK_PrWorkOrderTransfer_OutMovement')
    ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK ADD CONSTRAINT FK_PrWorkOrderTransfer_OutMovement FOREIGN KEY (TransferOutMovementID) REFERENCES dbo.PrProductionBalLotMovement(UID);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'FK_PrWorkOrderTransfer_InMovement')
    ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK ADD CONSTRAINT FK_PrWorkOrderTransfer_InMovement FOREIGN KEY (TransferInMovementID) REFERENCES dbo.PrProductionBalLotMovement(UID);

ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK CHECK CONSTRAINT
    CK_PrWorkOrderTransfer_Status, CK_PrWorkOrderTransfer_Quantities, CK_PrWorkOrderTransfer_Contexts,
    FK_PrWorkOrderTransfer_SourceBalance, FK_PrWorkOrderTransfer_DestinationBalance,
    FK_PrWorkOrderTransfer_SourceWorkOrder, FK_PrWorkOrderTransfer_TargetWorkOrder,
    FK_PrWorkOrderTransfer_PostingLink, FK_PrWorkOrderTransfer_StockPosting,
    FK_PrWorkOrderTransfer_OutMovement, FK_PrWorkOrderTransfer_InMovement;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'UQ_PrWorkOrderTransfer_Tenant_No')
    CREATE UNIQUE INDEX UQ_PrWorkOrderTransfer_Tenant_No
        ON dbo.PrWorkOrderTransfer(CompanyCode,BranchCode,TransferNo);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'UQ_PrWorkOrderTransfer_NewSource')
    CREATE UNIQUE INDEX UQ_PrWorkOrderTransfer_NewSource
        ON dbo.PrWorkOrderTransfer(CompanyCode,BranchCode,SourceProductionBalLotID)
        WHERE Status=N'NEW' AND DeletedAtUtc IS NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'IX_PrWorkOrderTransfer_Status_Date')
    CREATE INDEX IX_PrWorkOrderTransfer_Status_Date
        ON dbo.PrWorkOrderTransfer(CompanyCode,BranchCode,Status,TransferDateTime,UID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer') AND name=N'IX_PrWorkOrderTransfer_Target')
    CREATE INDEX IX_PrWorkOrderTransfer_Target
        ON dbo.PrWorkOrderTransfer(CompanyCode,BranchCode,TargetWorkOrderID,TargetWorkOrderMaterialID);

-- Lifecycle, source snapshot, and rollback evidence are additive. Source snapshot columns
-- remain nullable so legacy WOT drafts can be refreshed instead of receiving invented evidence.
IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', N'DocumentRevision') IS NULL
    ALTER TABLE dbo.PrWorkOrderTransfer ADD DocumentRevision int NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', N'SourceSnapshotRevision') IS NULL
    ALTER TABLE dbo.PrWorkOrderTransfer ADD SourceSnapshotRevision int NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', N'SourceSnapshotHash') IS NULL
    ALTER TABLE dbo.PrWorkOrderTransfer ADD SourceSnapshotHash nvarchar(64) NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', N'ReversalPostingID') IS NULL
    ALTER TABLE dbo.PrWorkOrderTransfer ADD ReversalPostingID bigint NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', N'ReversalReason') IS NULL
    ALTER TABLE dbo.PrWorkOrderTransfer ADD ReversalReason nvarchar(500) NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', N'RollbackDate') IS NULL
    ALTER TABLE dbo.PrWorkOrderTransfer ADD RollbackDate datetime2(7) NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', N'RollbackBy') IS NULL
    ALTER TABLE dbo.PrWorkOrderTransfer ADD RollbackBy nvarchar(10) NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', N'RollbackCount') IS NULL
    ALTER TABLE dbo.PrWorkOrderTransfer ADD RollbackCount int NULL;
IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', N'RollbackOperationID') IS NULL
    ALTER TABLE dbo.PrWorkOrderTransfer ADD RollbackOperationID uniqueidentifier NULL;
GO

IF EXISTS
(
    SELECT 1 FROM dbo.PrWorkOrderTransfer t
    OUTER APPLY
    (
        SELECT COUNT_BIG(*) AS MatchCount, MIN(p.DocumentRevision) AS DocumentRevision
        FROM dbo.StockPosting p
        WHERE p.Id = t.StockPostingID AND p.CompanyCode = t.CompanyCode AND p.BranchCode = t.BranchCode
          AND p.CommandType = N'WO_TRANSFER_POST' AND p.PostingRole = N'PRIMARY' AND p.SealedAtUtc IS NOT NULL
          AND p.SourceModule = N'PRODUCTION' AND p.SourceDocumentType = N'WORK_ORDER_TRANSFER'
          AND p.SourceDocumentId = CONVERT(nvarchar(30), t.UID)
    ) m
    WHERE t.Status = N'POSTED' AND (t.StockPostingID IS NULL OR m.MatchCount <> 1)
)
    THROW 51010, 'A POSTED Work Order Transfer has no unique matching sealed PRIMARY posting; resolve before migration.', 1;

UPDATE t SET DocumentRevision = m.DocumentRevision
FROM dbo.PrWorkOrderTransfer t
CROSS APPLY
(
    SELECT MIN(p.DocumentRevision) AS DocumentRevision
    FROM dbo.StockPosting p
    WHERE p.Id = t.StockPostingID AND p.CompanyCode = t.CompanyCode AND p.BranchCode = t.BranchCode
      AND p.CommandType = N'WO_TRANSFER_POST' AND p.PostingRole = N'PRIMARY' AND p.SealedAtUtc IS NOT NULL
      AND p.SourceModule = N'PRODUCTION' AND p.SourceDocumentType = N'WORK_ORDER_TRANSFER'
      AND p.SourceDocumentId = CONVERT(nvarchar(30), t.UID)
) m
WHERE t.Status = N'POSTED' AND t.DocumentRevision IS NULL;
UPDATE dbo.PrWorkOrderTransfer SET DocumentRevision = 1 WHERE DocumentRevision IS NULL;
UPDATE dbo.PrWorkOrderTransfer SET RollbackCount = 0 WHERE RollbackCount IS NULL;
ALTER TABLE dbo.PrWorkOrderTransfer ALTER COLUMN DocumentRevision int NOT NULL;
ALTER TABLE dbo.PrWorkOrderTransfer ALTER COLUMN RollbackCount int NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer')
    AND parent_column_id=COLUMNPROPERTY(OBJECT_ID(N'dbo.PrWorkOrderTransfer'), N'DocumentRevision', 'ColumnId'))
    ALTER TABLE dbo.PrWorkOrderTransfer ADD CONSTRAINT DF_PrWorkOrderTransfer_DocumentRevision DEFAULT 1 FOR DocumentRevision;
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer')
    AND parent_column_id=COLUMNPROPERTY(OBJECT_ID(N'dbo.PrWorkOrderTransfer'), N'RollbackCount', 'ColumnId'))
    ALTER TABLE dbo.PrWorkOrderTransfer ADD CONSTRAINT DF_PrWorkOrderTransfer_RollbackCount DEFAULT 0 FOR RollbackCount;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer')
    AND name=N'CK_PrWorkOrderTransfer_Lifecycle')
    ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK ADD CONSTRAINT CK_PrWorkOrderTransfer_Lifecycle CHECK
        (DocumentRevision > 0 AND RollbackCount >= 0 AND (ReversalPostingID IS NULL OR (StockPostingID IS NOT NULL AND ReversalPostingID <> StockPostingID)));
ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK CHECK CONSTRAINT CK_PrWorkOrderTransfer_Lifecycle;
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PrWorkOrderTransfer')
    AND name=N'FK_PrWorkOrderTransfer_ReversalPosting')
    ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK ADD CONSTRAINT FK_PrWorkOrderTransfer_ReversalPosting
        FOREIGN KEY (ReversalPostingID) REFERENCES dbo.StockPosting(Id);
ALTER TABLE dbo.PrWorkOrderTransfer WITH CHECK CHECK CONSTRAINT FK_PrWorkOrderTransfer_ReversalPosting;

IF OBJECT_ID(N'dbo.PrProductionWorkOrderTransferFact', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrProductionWorkOrderTransferFact
    (
        ID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrProductionWorkOrderTransferFact PRIMARY KEY,
        TransferID bigint NOT NULL,
        DocumentRevision int NOT NULL,
        StockPostingID bigint NOT NULL,
        ProductionPostingLinkID bigint NOT NULL,
        SourceProductionBalLotID bigint NOT NULL,
        DestinationProductionBalLotID bigint NOT NULL,
        SourceLegMovementID bigint NOT NULL,
        TargetLegMovementID bigint NOT NULL,
        MovementGroupID uniqueidentifier NOT NULL,
        Qty decimal(18,4) NOT NULL,
        BaseQty decimal(18,4) NOT NULL,
        BaseUom nvarchar(10) NOT NULL,
        TotalValue decimal(19,6) NOT NULL,
        UnitCost decimal(19,6) NOT NULL,
        SourceBaseBefore decimal(18,4) NOT NULL,
        SourceValueBefore decimal(19,6) NOT NULL,
        SourceBaseAfter decimal(18,4) NOT NULL,
        SourceValueAfter decimal(19,6) NOT NULL,
        TargetBaseBefore decimal(18,4) NOT NULL,
        TargetValueBefore decimal(19,6) NOT NULL,
        TargetBaseAfter decimal(18,4) NOT NULL,
        TargetValueAfter decimal(19,6) NOT NULL,
        SourceGenerationBefore int NULL,
        SourceGenerationAfter int NULL,
        TargetGenerationBefore int NULL,
        TargetGenerationAfter int NULL,
        ReversesFactID bigint NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CONSTRAINT CK_PrProductionWorkOrderTransferFact_Values CHECK
            (DocumentRevision > 0 AND Qty > 0 AND BaseQty > 0 AND TotalValue >= 0 AND UnitCost >= 0),
        CONSTRAINT CK_PrProductionWorkOrderTransferFact_Reversal CHECK
            (ReversesFactID IS NULL OR ReversesFactID <> ID),
        CONSTRAINT FK_PrProductionWorkOrderTransferFact_Transfer FOREIGN KEY (TransferID)
            REFERENCES dbo.PrWorkOrderTransfer(UID),
        CONSTRAINT FK_PrProductionWorkOrderTransferFact_StockPosting FOREIGN KEY (StockPostingID)
            REFERENCES dbo.StockPosting(Id),
        CONSTRAINT FK_PrProductionWorkOrderTransferFact_PostingLink FOREIGN KEY (ProductionPostingLinkID)
            REFERENCES dbo.PrProductionPostingLink(UID),
        CONSTRAINT FK_PrProductionWorkOrderTransferFact_SourceBalance FOREIGN KEY (SourceProductionBalLotID)
            REFERENCES dbo.PrProductionBalLot(UID),
        CONSTRAINT FK_PrProductionWorkOrderTransferFact_DestinationBalance FOREIGN KEY (DestinationProductionBalLotID)
            REFERENCES dbo.PrProductionBalLot(UID),
        CONSTRAINT FK_PrProductionWorkOrderTransferFact_SourceMovement FOREIGN KEY (SourceLegMovementID)
            REFERENCES dbo.PrProductionBalLotMovement(UID),
        CONSTRAINT FK_PrProductionWorkOrderTransferFact_TargetMovement FOREIGN KEY (TargetLegMovementID)
            REFERENCES dbo.PrProductionBalLotMovement(UID),
        CONSTRAINT FK_PrProductionWorkOrderTransferFact_ReversesFact FOREIGN KEY (ReversesFactID)
            REFERENCES dbo.PrProductionWorkOrderTransferFact(ID)
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrProductionWorkOrderTransferFact') AND name=N'UQ_PrProductionWorkOrderTransferFact_Posting')
    CREATE UNIQUE INDEX UQ_PrProductionWorkOrderTransferFact_Posting ON dbo.PrProductionWorkOrderTransferFact(StockPostingID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrProductionWorkOrderTransferFact') AND name=N'UQ_PrProductionWorkOrderTransferFact_SourceMovement')
    CREATE UNIQUE INDEX UQ_PrProductionWorkOrderTransferFact_SourceMovement ON dbo.PrProductionWorkOrderTransferFact(SourceLegMovementID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrProductionWorkOrderTransferFact') AND name=N'UQ_PrProductionWorkOrderTransferFact_TargetMovement')
    CREATE UNIQUE INDEX UQ_PrProductionWorkOrderTransferFact_TargetMovement ON dbo.PrProductionWorkOrderTransferFact(TargetLegMovementID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrProductionWorkOrderTransferFact') AND name=N'UQ_PrProductionWorkOrderTransferFact_MovementGroup')
    CREATE UNIQUE INDEX UQ_PrProductionWorkOrderTransferFact_MovementGroup ON dbo.PrProductionWorkOrderTransferFact(MovementGroupID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrProductionWorkOrderTransferFact') AND name=N'UQ_PrProductionWorkOrderTransferFact_ReversesFact')
    CREATE UNIQUE INDEX UQ_PrProductionWorkOrderTransferFact_ReversesFact ON dbo.PrProductionWorkOrderTransferFact(ReversesFactID)
        WHERE ReversesFactID IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrProductionWorkOrderTransferFact') AND name=N'IX_PrProductionWorkOrderTransferFact_TransferRevision')
    CREATE INDEX IX_PrProductionWorkOrderTransferFact_TransferRevision
        ON dbo.PrProductionWorkOrderTransferFact(TransferID,DocumentRevision);

UPDATE l SET ProductionDocumentLineID = t.UID, DocumentRevision = t.DocumentRevision
FROM dbo.PrProductionPostingLink l
JOIN dbo.PrWorkOrderTransfer t ON t.ProductionPostingLinkID = l.UID
WHERE l.CommandType = N'WO_TRANSFER_POST' AND l.ProductionDocumentLineID IS NULL;

IF EXISTS
(
    SELECT 1
    FROM dbo.PrProductionPostingLink
    WHERE CommandType = N'WO_TRANSFER_POST' AND ProductionDocumentLineID IS NOT NULL
    GROUP BY CompanyCode, BranchCode, CommandType, ProductionDocumentLineID, DocumentRevision
    HAVING COUNT_BIG(*) > 1
)
    THROW 51018, 'Duplicate Work Order Transfer posting-link generations prevent the required unique index.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PrProductionPostingLink')
    AND name=N'UQ_PrProductionPostingLink_WorkOrderTransferGeneration')
    CREATE UNIQUE INDEX UQ_PrProductionPostingLink_WorkOrderTransferGeneration
        ON dbo.PrProductionPostingLink(CompanyCode,BranchCode,CommandType,ProductionDocumentLineID,DocumentRevision)
        WHERE ProductionDocumentLineID IS NOT NULL AND CommandType = 'WO_TRANSFER_POST';

ALTER TABLE dbo.PrProductionWorkOrderTransferFact WITH CHECK CHECK CONSTRAINT ALL;
IF EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id IN (OBJECT_ID(N'dbo.PrWorkOrderTransfer'), OBJECT_ID(N'dbo.PrProductionWorkOrderTransferFact'))
      AND (is_disabled = 1 OR is_not_trusted = 1)
)
    THROW 51012, 'A Work Order Transfer header or fact foreign key is disabled or untrusted.', 1;

IF EXISTS
(
    SELECT 1
    FROM (VALUES
        (N'UQ_PrProductionWorkOrderTransferFact_Posting'),
        (N'UQ_PrProductionWorkOrderTransferFact_SourceMovement'),
        (N'UQ_PrProductionWorkOrderTransferFact_TargetMovement'),
        (N'UQ_PrProductionWorkOrderTransferFact_MovementGroup'),
        (N'UQ_PrProductionWorkOrderTransferFact_ReversesFact'),
        (N'IX_PrProductionWorkOrderTransferFact_TransferRevision')) required(IndexName)
    LEFT JOIN sys.indexes i ON i.object_id=OBJECT_ID(N'dbo.PrProductionWorkOrderTransferFact') AND i.name=required.IndexName
    WHERE i.index_id IS NULL OR i.is_disabled=1
       OR (required.IndexName LIKE N'UQ_%' AND i.is_unique=0)
)
    THROW 51017, 'A required Work Order Transfer fact index is missing, disabled, or not unique.', 1;

COMMIT;
GO
