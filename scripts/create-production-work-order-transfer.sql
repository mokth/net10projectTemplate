-- Idempotent additive schema for V2 Production Work Order Transfer.
-- Does not alter ProductionBalLot identity indexes or existing stock balances.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

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

DECLARE @ColumnName sysname, @ColumnType nvarchar(40), @IsRequired bit;
DECLARE transfer_column_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT ColumnName, ColumnType, IsRequired FROM @TransferColumns ORDER BY ColumnName;
OPEN transfer_column_cursor;
FETCH NEXT FROM transfer_column_cursor INTO @ColumnName, @ColumnType, @IsRequired;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', @ColumnName) IS NULL
    BEGIN
        DECLARE @Nullability nvarchar(10) = CASE WHEN @IsRequired = 1 THEN N' NOT NULL' ELSE N' NULL' END;
        EXEC(N'ALTER TABLE dbo.PrWorkOrderTransfer ADD ' + QUOTENAME(@ColumnName) + N' ' + @ColumnType + @Nullability + N';');
    END;
    FETCH NEXT FROM transfer_column_cursor INTO @ColumnName, @ColumnType, @IsRequired;
END;
CLOSE transfer_column_cursor;
DEALLOCATE transfer_column_cursor;

IF COL_LENGTH(N'dbo.PrWorkOrderTransfer', N'RowVersion') IS NULL
    ALTER TABLE dbo.PrWorkOrderTransfer ADD RowVersion rowversion NOT NULL;

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

COMMIT;
GO
