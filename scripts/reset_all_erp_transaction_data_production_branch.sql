SET NOCOUNT ON;
SET XACT_ABORT ON;

-----------------------------------------------------------------------
-- 0. SAFETY SWITCHES
-----------------------------------------------------------------------
DECLARE @Execute bit =1;  -- 0 = preview only; 1 = actually delete
DECLARE @ExpectedDatabase sysname = N'ERPWeb';
DECLARE @SafetyPhrase nvarchar(100) = N'CLEAR ERP TRANSACTIONS';
DECLARE @ResetTransactionNumbering bit = 1;

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
    THROW 51000, 'Refusing to run against a SQL Server system database.', 1;

-----------------------------------------------------------------------
-- 1. EXPLICIT ALLOW-LIST OF TRANSACTION / DERIVED TABLES
--    Anything not listed here is preserved.
-----------------------------------------------------------------------
DECLARE @Targets TABLE
(
    DeleteOrder int NOT NULL,
    ModuleName nvarchar(30) NOT NULL,
    SchemaName sysname NOT NULL,
    TableName sysname NOT NULL,
    ResetIdentity bit NOT NULL DEFAULT (1),
    PRIMARY KEY (SchemaName, TableName)
);

-- Costing / diagnostic / month-end derived state
INSERT INTO @Targets (DeleteOrder, ModuleName, SchemaName, TableName, ResetIdentity) VALUES
(  10, N'COSTING',   N'dbo', N'CostingRepairAuditEvent',                 1),
(  20, N'COSTING',   N'dbo', N'CostingRepairCase',                       1),
(  30, N'MONTHEND',  N'dbo', N'StockValuationPeriodSnapshotLine',        1),
(  40, N'MONTHEND',  N'dbo', N'StockValuationPeriodSnapshotHdr',         1),
(  50, N'MONTHEND',  N'dbo', N'StockPeriodSnapshotLine',                 1),
(  60, N'MONTHEND',  N'dbo', N'StockPeriodSnapshotHdr',                  1),
(  70, N'COSTING',   N'dbo', N'SalesReturnStandardCostVariance',         1),
(  80, N'COSTING',   N'dbo', N'SalesReturnCostAllocation',               1),
(  90, N'COSTING',   N'dbo', N'PurchaseCostAdjustment',                  1),
( 100, N'COSTING',   N'dbo', N'PurchaseReceiptCostSettlement',           1),
( 110, N'COSTING',   N'dbo', N'ProductionStandardCostVariance',          1),
( 120, N'COSTING',   N'dbo', N'StockFifoLayerConsumption',               1),
( 130, N'COSTING',   N'dbo', N'StockFifoLayer',                          1),
( 140, N'COSTING',   N'dbo', N'StockCostState',                          1);

-- Production costing / FG immutable evidence
INSERT INTO @Targets (DeleteOrder, ModuleName, SchemaName, TableName, ResetIdentity) VALUES
( 200, N'PRODUCTION', N'dbo', N'PrPoolDependency',                       1),
( 210, N'PRODUCTION', N'dbo', N'PrValuationEvidence',                    1),
( 220, N'PRODUCTION', N'dbo', N'PrPoolValuation',                        0),
( 230, N'PRODUCTION', N'dbo', N'PrFinishedGoodPriceSnapshot',            1),
( 240, N'PRODUCTION', N'dbo', N'PrFinishedGoodFact',                     1),
( 250, N'PRODUCTION', N'dbo', N'PrFinishedGoodSource',                   1),
( 260, N'PRODUCTION', N'dbo', N'PrFinishedGoodLotOrigin',                0),
( 270, N'PRODUCTION', N'dbo', N'PrFinishedGoodReceipt',                  0);

-- Production execution: IP / Daily / WIP / WO
INSERT INTO @Targets (DeleteOrder, ModuleName, SchemaName, TableName, ResetIdentity) VALUES
( 300, N'PRODUCTION', N'dbo', N'PrProductionMovementAllocation',         1),
( 310, N'PRODUCTION', N'dbo', N'PrMaterialIssueLine',                    1),
( 320, N'PRODUCTION', N'dbo', N'PrMaterialMovement',                     1),
( 330, N'PRODUCTION', N'dbo', N'PrProductionBalLotMovement',             1),
( 340, N'PRODUCTION', N'dbo', N'PrProductionBalLot',                     1),
( 350, N'PRODUCTION', N'dbo', N'PrProductionOutputMaterial',             1),
( 360, N'PRODUCTION', N'dbo', N'PrProductionOutput',                     1),
( 370, N'PRODUCTION', N'dbo', N'PrProductionPostingLink',                1),
( 380, N'PRODUCTION', N'dbo', N'WIPItemBalLoc',                          0),
( 390, N'PRODUCTION', N'dbo', N'PrWorkOrderChangeLine',                  1),
( 400, N'PRODUCTION', N'dbo', N'PrWorkOrderChange',                      1),
( 410, N'PRODUCTION', N'dbo', N'PrWorkOrderLabour',                      1),
( 420, N'PRODUCTION', N'dbo', N'PrWorkOrderMachine',                     1),
( 430, N'PRODUCTION', N'dbo', N'PrWorkOrderResource',                    1),
( 440, N'PRODUCTION', N'dbo', N'PrWorkOrderMaterial',                    1),
( 450, N'PRODUCTION', N'dbo', N'PrWorkOrderOperation',                   1),
( 460, N'PRODUCTION', N'dbo', N'PrWorkOrderRouteStep',                   1),
( 470, N'PRODUCTION', N'dbo', N'PrWorkOrderAudit',                       1),
( 480, N'PRODUCTION', N'dbo', N'PrWorkOrder',                            1);

-- Legacy/current schedule snapshots used by production planning/execution
INSERT INTO @Targets (DeleteOrder, ModuleName, SchemaName, TableName, ResetIdentity) VALUES
( 500, N'PRODUCTION', N'dbo', N'PrSchDailyProd',                         1),
( 510, N'PRODUCTION', N'dbo', N'PrSchBOM',                               0),
( 520, N'PRODUCTION', N'dbo', N'PrSchLabour',                            0),
( 530, N'PRODUCTION', N'dbo', N'PrSchMachine',                           0),
( 540, N'PRODUCTION', N'dbo', N'PrSchProcess',                           0),
( 550, N'PRODUCTION', N'dbo', N'PrSchWCenter',                           0),
( 560, N'PRODUCTION', N'dbo', N'PrSchMas',                               0);

-- Machine maintenance activity is transaction history; preventive definitions remain
INSERT INTO @Targets (DeleteOrder, ModuleName, SchemaName, TableName, ResetIdentity) VALUES
( 570, N'PRODUCTION', N'dbo', N'PrMacMaintenanceImages',                 1),
( 580, N'PRODUCTION', N'dbo', N'PrMacMaintenance',                       1);

-- Sales documents / application links / e-Invoice logs
INSERT INTO @Targets (DeleteOrder, ModuleName, SchemaName, TableName, ResetIdentity) VALUES
( 600, N'SALES',      N'dbo', N'SaDocApplicationBackfillSkip',           1),
( 610, N'SALES',      N'dbo', N'SaDocApplication',                       1),
( 620, N'SALES',      N'dbo', N'SaEInvoiceLog',                          1),
( 630, N'SALES',      N'dbo', N'EInvDocSubmission',                      1),
( 640, N'SALES',      N'dbo', N'SaCDNDetail',                            0),
( 650, N'SALES',      N'dbo', N'SaCDN',                                  0),
( 660, N'SALES',      N'dbo', N'SaInvoiceDetail',                        1),
( 670, N'SALES',      N'dbo', N'SaInvoice',                              0),
( 680, N'SALES',      N'dbo', N'SaDODetail',                             0),
( 690, N'SALES',      N'dbo', N'SaDO',                                   0),
( 700, N'SALES',      N'dbo', N'SaSODetail',                             0),
( 710, N'SALES',      N'dbo', N'SaSO',                                   0),
( 720, N'SALES',      N'dbo', N'SaQTDetail',                             0),
( 730, N'SALES',      N'dbo', N'SaQT',                                   0);

-- Procurement documents / transaction attachments
INSERT INTO @Targets (DeleteOrder, ModuleName, SchemaName, TableName, ResetIdentity) VALUES
( 800, N'PROCUREMENT', N'dbo', N'POPRAttachFile',                        0),
( 810, N'PROCUREMENT', N'dbo', N'POAttachFile',                          0),
( 820, N'PROCUREMENT', N'dbo', N'PoCdnDetail',                           0),
( 830, N'PROCUREMENT', N'dbo', N'PoCdn',                                 0),
( 840, N'PROCUREMENT', N'dbo', N'POSbCdnDetail',                         0),
( 850, N'PROCUREMENT', N'dbo', N'POSbCdn',                               0),
( 860, N'PROCUREMENT', N'dbo', N'POSbInvoiceDetail',                     0),
( 870, N'PROCUREMENT', N'dbo', N'POSbInvoice',                           0),
( 880, N'PROCUREMENT', N'dbo', N'POInvoiceDetail',                       0),
( 890, N'PROCUREMENT', N'dbo', N'POInvoice',                             0),
( 900, N'PROCUREMENT', N'dbo', N'POCJDetail',                            0),
( 910, N'PROCUREMENT', N'dbo', N'POCJ',                                  0),
( 920, N'PROCUREMENT', N'dbo', N'PODetail',                              0),
( 930, N'PROCUREMENT', N'dbo', N'POOrder',                               0),
( 940, N'PROCUREMENT', N'dbo', N'POPRDtl',                               0),
( 950, N'PROCUREMENT', N'dbo', N'POPR',                                  0);

-- Inventory execution / quantity state / month-end
INSERT INTO @Targets (DeleteOrder, ModuleName, SchemaName, TableName, ResetIdentity) VALUES
(1000, N'INVENTORY',  N'dbo', N'IvStockCountLine',                       1),
(1010, N'INVENTORY',  N'dbo', N'IvStockCountHdr',                        1),
(1020, N'MONTHEND',   N'dbo', N'IvPeriodCloseBal',                       1),
(1030, N'MONTHEND',   N'dbo', N'IvPeriodCloseHdr',                       1),
(1040, N'INVENTORY',  N'dbo', N'IvTrxHistory',                           1),
(1050, N'INVENTORY',  N'dbo', N'IvTrxBatchDetail',                       1),
(1060, N'INVENTORY',  N'dbo', N'IvTrxBatch',                             1),
(1070, N'INVENTORY',  N'dbo', N'IvBalLoc',                               1),
(1080, N'INVENTORY',  N'dbo', N'IvLot',                                  1);

-- Stock valuation fact must be removed after its dependent evidence above.
-- StockPosting is the immutable posting envelope; preserve only protocol epoch/sequence controls.
INSERT INTO @Targets (DeleteOrder, ModuleName, SchemaName, TableName, ResetIdentity) VALUES
(1100, N'COSTING',    N'dbo', N'StockValuationFact',                     1),
(1110, N'COSTING',    N'dbo', N'StockPosting',                           1);

-----------------------------------------------------------------------
-- 2. PREVIEW
-----------------------------------------------------------------------
;WITH TargetObjects AS
(
    SELECT
        t.DeleteOrder,
        t.ModuleName,
        t.SchemaName,
        t.TableName,
        OBJECT_ID(QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName), N'U') AS ObjectId
    FROM @Targets t
)
SELECT
    DeleteOrder,
    ModuleName,
    SchemaName,
    TableName,
    CASE WHEN ObjectId IS NULL THEN N'MISSING / SKIPPED' ELSE N'FOUND' END AS TableStatus,
    COALESCE
    (
        (
            SELECT SUM(ps.row_count)
            FROM sys.dm_db_partition_stats ps
            WHERE ps.object_id = TargetObjects.ObjectId
              AND ps.index_id IN (0, 1)
        ), 0
    ) AS ApproxRows
FROM TargetObjects
ORDER BY DeleteOrder, SchemaName, TableName;

PRINT N'Preserved by design: StockCostPolicyRevision, ItemStandardCostRevision, StockLedgerEpoch, StockPostingBranchSequence.';

IF OBJECT_ID(N'dbo.StockLedgerEpoch', N'U') IS NOT NULL
BEGIN
    SELECT
        N'PRESERVED_LEDGER_CONTROL' AS Scope,
        CompanyCode,
        BranchCode,
        Id AS LedgerEpochId,
        Status,
        EffectiveFrom,
        CutoverPostingSequence,
        Version
    FROM dbo.StockLedgerEpoch
    ORDER BY CompanyCode, BranchCode, Id;
END;

IF @Execute = 0
BEGIN
    PRINT N'PREVIEW ONLY. No data was changed.';
    PRINT N'To execute: set @Execute = 1, set @ExpectedDatabase to DB_NAME(), and set @SafetyPhrase = N''CLEAR ERP TRANSACTIONS''.';
    RETURN;
END;

-----------------------------------------------------------------------
-- 3. EXECUTION SAFETY GATES
-----------------------------------------------------------------------
IF NULLIF(LTRIM(RTRIM(@ExpectedDatabase)), N'') IS NULL
   OR @ExpectedDatabase = N'YOUR_TEST_DATABASE'
    THROW 51001, 'Set @ExpectedDatabase to the exact test/debug database name before executing.', 1;

IF DB_NAME() <> @ExpectedDatabase
    THROW 51002, 'Current database does not match @ExpectedDatabase. Aborting.', 1;

IF @SafetyPhrase <> N'CLEAR ERP TRANSACTIONS'
    THROW 51003, 'Safety phrase is incorrect. Aborting.', 1;

-----------------------------------------------------------------------
-- 4. WORK TABLES
-----------------------------------------------------------------------
CREATE TABLE #DeleteCounts
(
    DeleteOrder int NOT NULL,
    ModuleName nvarchar(30) NOT NULL,
    TableName nvarchar(300) NOT NULL,
    RowsDeleted bigint NOT NULL
);

CREATE TABLE #TouchedFk
(
    ParentSchema sysname NOT NULL,
    ParentTable sysname NOT NULL,
    ConstraintName sysname NOT NULL,
    WasDisabled bit NOT NULL,
    WasNotTrusted bit NOT NULL,
    PRIMARY KEY (ParentSchema, ParentTable, ConstraintName)
);

CREATE TABLE #TouchedTrigger
(
    TableSchema sysname NOT NULL,
    TableName sysname NOT NULL,
    TriggerName sysname NOT NULL,
    PRIMARY KEY (TableSchema, TableName, TriggerName)
);

BEGIN TRY
    BEGIN TRANSACTION;

    -------------------------------------------------------------------
    -- 5. CAPTURE + TEMPORARILY DISABLE ONLY RELEVANT FKs
    --
    -- Includes FKs where a target is either the child or referenced parent.
    -- This lets the allow-listed wipe proceed without guessing delete order.
    -- At the end every originally-enabled FK is restored and validated.
    -------------------------------------------------------------------
    INSERT INTO #TouchedFk (ParentSchema, ParentTable, ConstraintName, WasDisabled, WasNotTrusted)
    SELECT DISTINCT
        OBJECT_SCHEMA_NAME(fk.parent_object_id),
        OBJECT_NAME(fk.parent_object_id),
        fk.name,
        fk.is_disabled,
        fk.is_not_trusted
    FROM sys.foreign_keys fk
    WHERE
        fk.parent_object_id IN
        (
            SELECT OBJECT_ID(QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName), N'U')
            FROM @Targets t
            WHERE OBJECT_ID(QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName), N'U') IS NOT NULL
        )
        OR fk.referenced_object_id IN
        (
            SELECT OBJECT_ID(QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName), N'U')
            FROM @Targets t
            WHERE OBJECT_ID(QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName), N'U') IS NOT NULL
        );

    DECLARE
        @Schema sysname,
        @Table sysname,
        @Constraint sysname,
        @Sql nvarchar(max),
        @WasNotTrusted bit;

    DECLARE fk_disable CURSOR LOCAL FAST_FORWARD FOR
        SELECT ParentSchema, ParentTable, ConstraintName
        FROM #TouchedFk
        WHERE WasDisabled = 0
        ORDER BY ParentSchema, ParentTable, ConstraintName;

    OPEN fk_disable;
    FETCH NEXT FROM fk_disable INTO @Schema, @Table, @Constraint;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @Sql =
            N'ALTER TABLE ' + QUOTENAME(@Schema) + N'.' + QUOTENAME(@Table) +
            N' NOCHECK CONSTRAINT ' + QUOTENAME(@Constraint) + N';';
        EXEC sys.sp_executesql @Sql;

        FETCH NEXT FROM fk_disable INTO @Schema, @Table, @Constraint;
    END;

    CLOSE fk_disable;
    DEALLOCATE fk_disable;

    -------------------------------------------------------------------
    -- 6. CAPTURE + TEMPORARILY DISABLE ENABLED DML TRIGGERS ON TARGETS
    --    Prevents delete-side audit/posting side effects during reset.
    -------------------------------------------------------------------
    INSERT INTO #TouchedTrigger (TableSchema, TableName, TriggerName)
    SELECT
        OBJECT_SCHEMA_NAME(tr.parent_id),
        OBJECT_NAME(tr.parent_id),
        tr.name
    FROM sys.triggers tr
    WHERE tr.parent_class = 1
      AND tr.is_disabled = 0
      AND tr.parent_id IN
      (
          SELECT OBJECT_ID(QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName), N'U')
          FROM @Targets t
          WHERE OBJECT_ID(QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName), N'U') IS NOT NULL
      );

    DECLARE trigger_disable CURSOR LOCAL FAST_FORWARD FOR
        SELECT TableSchema, TableName, TriggerName
        FROM #TouchedTrigger
        ORDER BY TableSchema, TableName, TriggerName;

    OPEN trigger_disable;
    FETCH NEXT FROM trigger_disable INTO @Schema, @Table, @Constraint;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @Sql =
            N'DISABLE TRIGGER ' + QUOTENAME(@Constraint) +
            N' ON ' + QUOTENAME(@Schema) + N'.' + QUOTENAME(@Table) + N';';
        EXEC sys.sp_executesql @Sql;

        FETCH NEXT FROM trigger_disable INTO @Schema, @Table, @Constraint;
    END;

    CLOSE trigger_disable;
    DEALLOCATE trigger_disable;

    -------------------------------------------------------------------
    -- 7. DELETE EXPLICIT ALLOW-LIST
    -------------------------------------------------------------------
    DECLARE
        @DeleteOrder int,
        @ModuleName nvarchar(30),
        @ResetIdentity bit,
        @Rows bigint;

    DECLARE target_delete CURSOR LOCAL FAST_FORWARD FOR
        SELECT DeleteOrder, ModuleName, SchemaName, TableName, ResetIdentity
        FROM @Targets
        ORDER BY DeleteOrder;

    OPEN target_delete;
    FETCH NEXT FROM target_delete
        INTO @DeleteOrder, @ModuleName, @Schema, @Table, @ResetIdentity;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF OBJECT_ID(QUOTENAME(@Schema) + N'.' + QUOTENAME(@Table), N'U') IS NOT NULL
        BEGIN
            SET @Rows = 0;
            SET @Sql =
                N'DELETE FROM ' + QUOTENAME(@Schema) + N'.' + QUOTENAME(@Table) +
                N'; SET @DeletedRows = @@ROWCOUNT;';

            EXEC sys.sp_executesql
                @Sql,
                N'@DeletedRows bigint OUTPUT',
                @DeletedRows = @Rows OUTPUT;

            INSERT INTO #DeleteCounts (DeleteOrder, ModuleName, TableName, RowsDeleted)
            VALUES
            (
                @DeleteOrder,
                @ModuleName,
                QUOTENAME(@Schema) + N'.' + QUOTENAME(@Table),
                @Rows
            );
        END;

        FETCH NEXT FROM target_delete
            INTO @DeleteOrder, @ModuleName, @Schema, @Table, @ResetIdentity;
    END;

    CLOSE target_delete;
    DEALLOCATE target_delete;

    -------------------------------------------------------------------
    -- 8. RESET IDENTITY VALUES FOR EMPTIED TARGET TABLES
    -------------------------------------------------------------------
    DECLARE
        @ObjectId int,
        @Seed decimal(38,0),
        @Increment decimal(38,0),
        @Reseed decimal(38,0);

    DECLARE identity_reset CURSOR LOCAL FAST_FORWARD FOR
        SELECT
            t.SchemaName,
            t.TableName,
            OBJECT_ID(QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName), N'U')
        FROM @Targets t
        WHERE t.ResetIdentity = 1
          AND OBJECT_ID(QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName), N'U') IS NOT NULL
          AND EXISTS
          (
              SELECT 1
              FROM sys.identity_columns ic
              WHERE ic.object_id = OBJECT_ID(QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName), N'U')
          )
        ORDER BY t.DeleteOrder;

    OPEN identity_reset;
    FETCH NEXT FROM identity_reset INTO @Schema, @Table, @ObjectId;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SELECT TOP (1)
            @Seed = CONVERT(decimal(38,0), ic.seed_value),
            @Increment = CONVERT(decimal(38,0), ic.increment_value)
        FROM sys.identity_columns ic
        WHERE ic.object_id = @ObjectId;

        SET @Reseed = @Seed - @Increment;

        SET @Sql =
            N'DBCC CHECKIDENT (N''' +
            REPLACE(@Schema + N'.' + @Table, N'''', N'''''') +
            N''', RESEED, ' + CONVERT(nvarchar(50), @Reseed) +
            N') WITH NO_INFOMSGS;';

        EXEC sys.sp_executesql @Sql;

        FETCH NEXT FROM identity_reset INTO @Schema, @Table, @ObjectId;
    END;

    CLOSE identity_reset;
    DEALLOCATE identity_reset;

    -------------------------------------------------------------------
    -- 9. RESET TRANSACTION DOCUMENT NUMBERS ONLY
    --
    -- AdSmNum / AdSmNumDate store the NEXT sequence to issue, therefore 1.
    -- MsRunningNo stores the LAST issued sequence, therefore 0.
    -- Master numbering modules are deliberately untouched.
    -------------------------------------------------------------------
    IF @ResetTransactionNumbering = 1
    BEGIN
        IF OBJECT_ID(N'dbo.MsRunningNo', N'U') IS NOT NULL
        BEGIN
            UPDATE dbo.MsRunningNo
            SET LastNo = 0,
                [Updated] = GETDATE(),
                [UpdatedUID] = N'RESET'
            WHERE DocKey IN
            (
                N'IV_BATCH',
                N'IV_STOCK_COUNT',
                N'PR_WORK_ORDER',
                N'PR_DAILY_OUTPUT',
                N'SA_INV'
            )
            OR DocKey LIKE N'SA_INV[_]%';
        END;

        IF OBJECT_ID(N'dbo.AdSmNum', N'U') IS NOT NULL
        BEGIN
            UPDATE dbo.AdSmNum
            SET Seq = 1,
                Updated = GETDATE(),
                UpdatedUID = N'RESET'
            WHERE NumCd IN
            (
                N'QT', N'SO', N'DO', N'INV', N'CN', N'DN',
                N'PR', N'PO', N'PO_INV', N'PCN', N'PDN',
                N'PO_SBI', N'PO_SBC', N'PO_SBD'
            );
        END;

        IF OBJECT_ID(N'dbo.AdSmNumDate', N'U') IS NOT NULL
        BEGIN
            UPDATE dbo.AdSmNumDate
            SET Seq = 1,
                Updated = GETDATE(),
                UserID = N'RESET'
            WHERE NumCd IN
            (
                N'QT', N'SO', N'DO', N'INV', N'CN', N'DN',
                N'PR', N'PO', N'PO_INV', N'PCN', N'PDN',
                N'PO_SBI', N'PO_SBC', N'PO_SBD'
            );
        END;
    END;

    -------------------------------------------------------------------
    -- 10. RESTORE DML TRIGGERS
    -------------------------------------------------------------------
    DECLARE trigger_enable CURSOR LOCAL FAST_FORWARD FOR
        SELECT TableSchema, TableName, TriggerName
        FROM #TouchedTrigger
        ORDER BY TableSchema, TableName, TriggerName;

    OPEN trigger_enable;
    FETCH NEXT FROM trigger_enable INTO @Schema, @Table, @Constraint;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @Sql =
            N'ENABLE TRIGGER ' + QUOTENAME(@Constraint) +
            N' ON ' + QUOTENAME(@Schema) + N'.' + QUOTENAME(@Table) + N';';
        EXEC sys.sp_executesql @Sql;

        FETCH NEXT FROM trigger_enable INTO @Schema, @Table, @Constraint;
    END;

    CLOSE trigger_enable;
    DEALLOCATE trigger_enable;

    -------------------------------------------------------------------
    -- 11. RESTORE + VALIDATE ORIGINALLY ENABLED FK CONSTRAINTS
    --
    -- If WasNotTrusted = 0, restore as trusted with WITH CHECK CHECK.
    -- If it was already untrusted before this reset, enable it without
    -- changing its prior trust semantics.
    -------------------------------------------------------------------
    DECLARE fk_enable CURSOR LOCAL FAST_FORWARD FOR
        SELECT ParentSchema, ParentTable, ConstraintName, WasNotTrusted
        FROM #TouchedFk
        WHERE WasDisabled = 0
        ORDER BY ParentSchema, ParentTable, ConstraintName;

    OPEN fk_enable;
    FETCH NEXT FROM fk_enable INTO @Schema, @Table, @Constraint, @WasNotTrusted;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF @WasNotTrusted = 0
            SET @Sql =
                N'ALTER TABLE ' + QUOTENAME(@Schema) + N'.' + QUOTENAME(@Table) +
                N' WITH CHECK CHECK CONSTRAINT ' + QUOTENAME(@Constraint) + N';';
        ELSE
            SET @Sql =
                N'ALTER TABLE ' + QUOTENAME(@Schema) + N'.' + QUOTENAME(@Table) +
                N' CHECK CONSTRAINT ' + QUOTENAME(@Constraint) + N';';

        EXEC sys.sp_executesql @Sql;

        FETCH NEXT FROM fk_enable INTO @Schema, @Table, @Constraint, @WasNotTrusted;
    END;

    CLOSE fk_enable;
    DEALLOCATE fk_enable;

    -------------------------------------------------------------------
    -- 12. HARD VERIFICATION: EVERY EXISTING TARGET MUST NOW BE EMPTY
    -------------------------------------------------------------------
    IF EXISTS
    (
        SELECT 1
        FROM @Targets t
        CROSS APPLY
        (
            SELECT OBJECT_ID(QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName), N'U') AS ObjectId
        ) o
        CROSS APPLY
        (
            SELECT COALESCE(SUM(ps.row_count), 0) AS RowCount2
            FROM sys.dm_db_partition_stats ps
            WHERE ps.object_id = o.ObjectId
              AND ps.index_id IN (0, 1)
        ) rc
        WHERE o.ObjectId IS NOT NULL
          AND rc.RowCount2 <> 0
    )
        THROW 51004, 'Verification failed: one or more reset target tables still contains rows.', 1;

    -------------------------------------------------------------------
    -- 13. IMPORTANT LEDGER-CONTROL VERIFICATION
    -------------------------------------------------------------------
    IF OBJECT_ID(N'dbo.StockLedgerEpoch', N'U') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM dbo.StockLedgerEpoch WHERE Status = N'ACTIVE')
    BEGIN
        PRINT N'WARNING: No ACTIVE StockLedgerEpoch exists. Current V2 stock posting/costing will operate in disabled mode until an epoch is activated.';
    END;

    COMMIT TRANSACTION;

    -------------------------------------------------------------------
    -- 14. RESULT SUMMARY
    -------------------------------------------------------------------
    SELECT
        ModuleName,
        SUM(RowsDeleted) AS RowsDeleted
    FROM #DeleteCounts
    GROUP BY ModuleName
    ORDER BY ModuleName;

    SELECT
        DeleteOrder,
        ModuleName,
        TableName,
        RowsDeleted
    FROM #DeleteCounts
    ORDER BY DeleteOrder;

    PRINT N'ERP transaction reset completed successfully.';
    PRINT N'Master/reference/configuration data was preserved.';
    PRINT N'StockLedgerEpoch and StockPostingBranchSequence were preserved intentionally.';
    PRINT N'StockCostPolicyRevision and ItemStandardCostRevision were preserved intentionally.';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    DECLARE @ErrorMessage nvarchar(4000) = ERROR_MESSAGE();
    DECLARE @ErrorNumber int = ERROR_NUMBER();
    DECLARE @ErrorLine int = ERROR_LINE();

    PRINT N'ERP transaction reset FAILED. The transaction was rolled back.';
    PRINT N'Error ' + CONVERT(nvarchar(20), @ErrorNumber) +
          N' at line ' + CONVERT(nvarchar(20), @ErrorLine) +
          N': ' + @ErrorMessage;

    THROW;
END CATCH;