-- SaInvoice Option A: PK (CompanyCode, BranchCode, InvNo).
-- Manual DBA script. Do NOT run at app startup.
-- Fails if any header BranchCode is null/blank after backfill, or if duplicates exist on the new key.
--
-- Two things this script must get right:
--   1. The guards must actually stop the migration. A `RETURN` only ends the batch it appears in,
--      so separate guard batches would still let the destructive DROP/ADD batch run. All guards
--      live in the same batch as the work.
--   2. The migration must be atomic. It drops the detail unique index, the header/detail FK and
--      the old PK; if anything after that fails, SaInvoice would be left with no primary key at
--      all. Everything from the drops to the new PK/FK is therefore one transaction with
--      TRY/CATCH, and a failure rolls the whole thing back.
--
-- Narrowing BranchCode to nvarchar(10) truncates silently-badly (Msg 8152), so length is checked
-- up front rather than discovered halfway through the migration.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.SaInvoice', N'U') IS NULL
BEGIN
    RAISERROR(N'SaInvoice does not exist.', 16, 1);
    RETURN;
END

-- Ensure BranchCode exists on header and detail. Kept in its own batch: statements referencing
-- BranchCode below are resolved when their batch is compiled, so the column must exist first.
IF COL_LENGTH(N'dbo.SaInvoice', N'BranchCode') IS NULL
BEGIN
    ALTER TABLE dbo.SaInvoice ADD BranchCode nvarchar(10) NULL;
    PRINT N'Added SaInvoice.BranchCode';
END

IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'BranchCode') IS NULL
BEGIN
    ALTER TABLE dbo.SaInvoiceDetail ADD BranchCode nvarchar(10) NULL;
    PRINT N'Added SaInvoiceDetail.BranchCode';
END
GO

-- Copy BranchCode from header onto details. Safe to run on its own: it touches no keys, so it
-- stays useful even if the PK migration below is refused by a guard.
UPDATE d
SET d.BranchCode = h.BranchCode
FROM dbo.SaInvoiceDetail d
INNER JOIN dbo.SaInvoice h
    ON h.CompanyCode = d.CompanyCode AND h.InvNo = d.InvNo
WHERE d.BranchCode IS NULL OR LTRIM(RTRIM(d.BranchCode)) = N'' OR d.BranchCode <> h.BranchCode;
GO

-- Already on Option A?
IF EXISTS (
    SELECT 1
    FROM sys.key_constraints kc
    INNER JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
    INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SaInvoice')
      AND kc.type = N'PK'
      AND c.name = N'BranchCode')
BEGIN
    PRINT N'SaInvoice PK already includes BranchCode. Skipping PK recreate.';
    RETURN;
END

BEGIN TRY
    BEGIN TRANSACTION;

    -- Re-checked inside the transaction: nothing is dropped unless the migration can complete.
    IF EXISTS (SELECT 1 FROM dbo.SaInvoice
               WHERE BranchCode IS NULL OR LTRIM(RTRIM(BranchCode)) = N'')
        RAISERROR(N'SaInvoice has null/blank BranchCode. Backfill BranchCode before Option A PK change.', 16, 1);

    IF EXISTS (SELECT 1 FROM dbo.SaInvoiceDetail
               WHERE BranchCode IS NULL OR LTRIM(RTRIM(BranchCode)) = N'')
        RAISERROR(N'SaInvoiceDetail still has null/blank BranchCode after header copy.', 16, 1);

    IF EXISTS (SELECT 1 FROM dbo.SaInvoice WHERE LEN(BranchCode) > 10)
        RAISERROR(N'SaInvoice.BranchCode has a value longer than 10 characters; narrowing the column would truncate it.', 16, 1);

    IF EXISTS (SELECT 1 FROM dbo.SaInvoiceDetail WHERE LEN(BranchCode) > 10)
        RAISERROR(N'SaInvoiceDetail.BranchCode has a value longer than 10 characters; narrowing the column would truncate it.', 16, 1);

    IF EXISTS (SELECT 1 FROM dbo.SaInvoice
               GROUP BY CompanyCode, BranchCode, InvNo
               HAVING COUNT(*) > 1)
        RAISERROR(N'Duplicate (CompanyCode, BranchCode, InvNo) exists. Resolve before Option A PK.', 16, 1);

    -- ALTER COLUMN BranchCode is blocked by any index that keys on BranchCode, so every such
    -- index is dropped here and recreated after the PK/FK exist. The unique index that Option A
    -- wants already exists in some databases, and recreating it without dropping it first would
    -- fail with Msg 1913.
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaInvoiceDetail') AND name = N'UQ_SaInvoiceDetail_Company_InvNo_Line')
        DROP INDEX UQ_SaInvoiceDetail_Company_InvNo_Line ON dbo.SaInvoiceDetail;

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaInvoiceDetail') AND name = N'UQ_SaInvoiceDetail_Company_Branch_InvNo_Line')
        DROP INDEX UQ_SaInvoiceDetail_Company_Branch_InvNo_Line ON dbo.SaInvoiceDetail;

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaInvoiceDetail') AND name = N'IX_SaInvoiceDetail_Company_Branch_SoNo_SoLine')
        DROP INDEX IX_SaInvoiceDetail_Company_Branch_SoNo_SoLine ON dbo.SaInvoiceDetail;

    -- Drop FK from detail to header (name may vary). Dynamic SQL keeps the batch compiling
    -- regardless of which constraint name is actually present.
    DECLARE @fkName sysname;
    DECLARE @fkSql nvarchar(300);
    SELECT TOP 1 @fkName = fk.name
    FROM sys.foreign_keys fk
    WHERE fk.parent_object_id = OBJECT_ID(N'dbo.SaInvoiceDetail')
      AND fk.referenced_object_id = OBJECT_ID(N'dbo.SaInvoice');
    IF @fkName IS NOT NULL
    BEGIN
        SET @fkSql = N'ALTER TABLE dbo.SaInvoiceDetail DROP CONSTRAINT ' + QUOTENAME(@fkName) + N';';
        EXEC sp_executesql @fkSql;
    END

    -- Drop old PK
    DECLARE @pkName sysname;
    DECLARE @pkSql nvarchar(300);
    SELECT @pkName = kc.name
    FROM sys.key_constraints kc
    WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SaInvoice') AND kc.type = N'PK';
    IF @pkName IS NOT NULL
    BEGIN
        SET @pkSql = N'ALTER TABLE dbo.SaInvoice DROP CONSTRAINT ' + QUOTENAME(@pkName) + N';';
        EXEC sp_executesql @pkSql;
    END

    EXEC sp_executesql N'ALTER TABLE dbo.SaInvoice ALTER COLUMN BranchCode nvarchar(10) NOT NULL;';
    EXEC sp_executesql N'ALTER TABLE dbo.SaInvoiceDetail ALTER COLUMN BranchCode nvarchar(10) NOT NULL;';

    EXEC sp_executesql N'ALTER TABLE dbo.SaInvoice
        ADD CONSTRAINT PK_SaInvoice PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, InvNo);';

    EXEC sp_executesql N'ALTER TABLE dbo.SaInvoiceDetail
        ADD CONSTRAINT FK_SaInvoiceDetail_SaInvoice
        FOREIGN KEY (CompanyCode, BranchCode, InvNo)
        REFERENCES dbo.SaInvoice (CompanyCode, BranchCode, InvNo)
        ON DELETE CASCADE;';

    EXEC sp_executesql N'CREATE UNIQUE INDEX UQ_SaInvoiceDetail_Company_Branch_InvNo_Line
        ON dbo.SaInvoiceDetail (CompanyCode, BranchCode, InvNo, Line);';

    EXEC sp_executesql N'CREATE INDEX IX_SaInvoiceDetail_Company_Branch_SoNo_SoLine
        ON dbo.SaInvoiceDetail (CompanyCode, BranchCode, SONo, SOLine);';

    COMMIT TRANSACTION;
    PRINT N'SaInvoice migrated to Option A PK (CompanyCode, BranchCode, InvNo).';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'SaInvoice Option A migration FAILED and was rolled back: ' + ERROR_MESSAGE();
    RAISERROR(N'SaInvoice Option A migration was not applied. Nothing was changed.', 16, 1);
END CATCH
GO
