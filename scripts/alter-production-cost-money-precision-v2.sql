/*
   Production cost authority precision upgrade.

   This migration is intentionally separate from the fresh-install scripts. It
   validates every existing value before changing the SQL type, so deployment
   cannot silently round or truncate production authority money.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @Targets TABLE
    (
        SchemaName sysname NOT NULL,
        TableName sysname NOT NULL,
        ColumnName sysname NOT NULL,
        IsNullable bit NOT NULL
    );

    INSERT INTO @Targets (SchemaName, TableName, ColumnName, IsNullable)
    VALUES
        (N'dbo', N'PrMaterialMovement',        N'UnitCost',             0),
        (N'dbo', N'PrMaterialMovement',        N'TotalCost',            0),
        (N'dbo', N'PrProductionBalLot',       N'TotalCost',             0),
        (N'dbo', N'PrProductionBalLot',       N'AverageUnitCost',       0),
        (N'dbo', N'PrProductionBalLotMovement', N'UnitCost',             0),
        (N'dbo', N'PrProductionBalLotMovement', N'TotalCost',            0),
        (N'dbo', N'PrFinishedGoodFact',       N'TotalValue',             0),
        (N'dbo', N'PrPoolValuation',          N'TrackedValue',           0),
        (N'dbo', N'PrValuationEvidence',      N'Price',                  1),
        (N'dbo', N'IvTrxHistory',             N'ExactTransferredValue',   1);

    DECLARE @MissingObject sysname;
    SELECT TOP (1)
        @MissingObject = CONCAT(t.SchemaName, N'.', t.TableName)
    FROM @Targets t
    WHERE OBJECT_ID(CONCAT(t.SchemaName, N'.', t.TableName), N'U') IS NULL;

    IF @MissingObject IS NOT NULL
    BEGIN
        DECLARE @MissingObjectMessage nvarchar(2048) =
            N'Production cost precision migration requires table ' + @MissingObject + N'.';
        THROW 51000, @MissingObjectMessage, 1;
    END

    DECLARE @MissingColumn sysname;
    SELECT TOP (1)
        @MissingColumn = CONCAT(t.SchemaName, N'.', t.TableName, N'.', t.ColumnName)
    FROM @Targets t
    WHERE COL_LENGTH(CONCAT(t.SchemaName, N'.', t.TableName), t.ColumnName) IS NULL;

    IF @MissingColumn IS NOT NULL
    BEGIN
        DECLARE @MissingColumnMessage nvarchar(2048) =
            N'Production cost precision migration requires column ' + @MissingColumn + N'.';
        THROW 51001, @MissingColumnMessage, 1;
    END

    /* Fail before ALTER COLUMN if an existing value would overflow or lose
       fractional precision when converted to decimal(19,6). */
    DECLARE @OverflowSql nvarchar(max) = N'';
    SELECT @OverflowSql +=
        N'IF EXISTS (SELECT 1 FROM ' + QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName)
        + N' WHERE ' + QUOTENAME(t.ColumnName) + N' IS NOT NULL'
        + N' AND (TRY_CONVERT(decimal(19,6), ' + QUOTENAME(t.ColumnName) + N') IS NULL'
        + N' OR ' + QUOTENAME(t.ColumnName) + N' <> ROUND(' + QUOTENAME(t.ColumnName) + N', 6)'
        + N')' + N') BEGIN THROW 51002, N''Existing value cannot be represented as decimal(19,6): '
        + t.SchemaName + N'.' + t.TableName + N'.' + t.ColumnName + N''', 1; END;'
    FROM @Targets t;

    EXEC sys.sp_executesql @OverflowSql;

    DECLARE @NullabilitySql nvarchar(max) = N'';
    SELECT @NullabilitySql +=
        CASE WHEN t.IsNullable = 0 THEN
            N'IF EXISTS (SELECT 1 FROM ' + QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName)
            + N' WHERE ' + QUOTENAME(t.ColumnName) + N' IS NULL) BEGIN THROW 51003, N''Existing NULL cannot be preserved in required production authority column: '
            + t.SchemaName + N'.' + t.TableName + N'.' + t.ColumnName + N''', 1; END;'
        ELSE N'' END
    FROM @Targets t;

    EXEC sys.sp_executesql @NullabilitySql;

    /* Alter only columns that are not already at the target precision/scale. */
    DECLARE @AlterSql nvarchar(max) = N'';
    SELECT @AlterSql +=
        N'IF EXISTS (SELECT 1 FROM sys.columns'
        + N' WHERE object_id = OBJECT_ID(N''' + t.SchemaName + N'.' + t.TableName + N''', N''U'')'
        + N' AND name = N''' + t.ColumnName + N''''
        + N' AND ([precision] <> 19 OR [scale] <> 6 OR [is_nullable] <> '
        + CONVERT(nvarchar(1), t.IsNullable) + N'))'
        + N' ALTER TABLE ' + QUOTENAME(t.SchemaName) + N'.' + QUOTENAME(t.TableName)
        + N' ALTER COLUMN ' + QUOTENAME(t.ColumnName) + N' decimal(19,6) '
        + CASE WHEN t.IsNullable = 1 THEN N'NULL' ELSE N'NOT NULL' END + N';'
    FROM @Targets t;

    EXEC sys.sp_executesql @AlterSql;

    COMMIT TRANSACTION;
    PRINT N'Production authority money precision is decimal(19,6).';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
