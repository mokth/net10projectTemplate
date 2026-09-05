-- SaInvoice Option A: PK (CompanyCode, BranchCode, InvNo).
-- Manual DBA script. Do NOT run at app startup.
-- Fails if any header BranchCode is null/blank after backfill, or if duplicates exist on the new key.
GO

IF OBJECT_ID(N'dbo.SaInvoice', N'U') IS NULL
BEGIN
    RAISERROR(N'SaInvoice does not exist.', 16, 1);
    RETURN;
END
GO

-- Ensure BranchCode column exists on header
IF COL_LENGTH(N'dbo.SaInvoice', N'BranchCode') IS NULL
    ALTER TABLE dbo.SaInvoice ADD BranchCode nvarchar(10) NULL;
GO

-- Ensure BranchCode on detail
IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'BranchCode') IS NULL
    ALTER TABLE dbo.SaInvoiceDetail ADD BranchCode nvarchar(10) NULL;
GO

-- Fail if any header still null/blank (do not invent HQ)
IF EXISTS (
    SELECT 1 FROM dbo.SaInvoice
    WHERE BranchCode IS NULL OR LTRIM(RTRIM(BranchCode)) = N'')
BEGIN
    RAISERROR(N'SaInvoice has null/blank BranchCode. Backfill BranchCode before Option A PK change.', 16, 1);
    RETURN;
END
GO

-- Copy BranchCode from header onto details
UPDATE d
SET d.BranchCode = h.BranchCode
FROM dbo.SaInvoiceDetail d
INNER JOIN dbo.SaInvoice h
    ON h.CompanyCode = d.CompanyCode AND h.InvNo = d.InvNo
WHERE d.BranchCode IS NULL OR LTRIM(RTRIM(d.BranchCode)) = N'' OR d.BranchCode <> h.BranchCode;
GO

IF EXISTS (
    SELECT 1 FROM dbo.SaInvoiceDetail
    WHERE BranchCode IS NULL OR LTRIM(RTRIM(BranchCode)) = N'')
BEGIN
    RAISERROR(N'SaInvoiceDetail still has null/blank BranchCode after header copy.', 16, 1);
    RETURN;
END
GO

IF EXISTS (
    SELECT CompanyCode, BranchCode, InvNo, COUNT(*) AS Cnt
    FROM dbo.SaInvoice
    GROUP BY CompanyCode, BranchCode, InvNo
    HAVING COUNT(*) > 1)
BEGIN
    RAISERROR(N'Duplicate (CompanyCode, BranchCode, InvNo) exists. Resolve before Option A PK.', 16, 1);
    RETURN;
END
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
END
ELSE
BEGIN
    -- Drop detail unique index
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaInvoiceDetail') AND name = N'UQ_SaInvoiceDetail_Company_InvNo_Line')
        DROP INDEX UQ_SaInvoiceDetail_Company_InvNo_Line ON dbo.SaInvoiceDetail;

    -- Drop FK from detail to header (name may vary)
    DECLARE @fkName sysname;
    SELECT TOP 1 @fkName = fk.name
    FROM sys.foreign_keys fk
    WHERE fk.parent_object_id = OBJECT_ID(N'dbo.SaInvoiceDetail')
      AND fk.referenced_object_id = OBJECT_ID(N'dbo.SaInvoice');
    IF @fkName IS NOT NULL
        EXEC(N'ALTER TABLE dbo.SaInvoiceDetail DROP CONSTRAINT [' + @fkName + N']');

    -- Drop old PK
    DECLARE @pkName sysname;
    SELECT @pkName = kc.name
    FROM sys.key_constraints kc
    WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SaInvoice') AND kc.type = N'PK';
    IF @pkName IS NOT NULL
        EXEC(N'ALTER TABLE dbo.SaInvoice DROP CONSTRAINT [' + @pkName + N']');

    ALTER TABLE dbo.SaInvoice ALTER COLUMN BranchCode nvarchar(10) NOT NULL;
    ALTER TABLE dbo.SaInvoiceDetail ALTER COLUMN BranchCode nvarchar(10) NOT NULL;

    ALTER TABLE dbo.SaInvoice
        ADD CONSTRAINT PK_SaInvoice PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, InvNo);

    ALTER TABLE dbo.SaInvoiceDetail
        ADD CONSTRAINT FK_SaInvoiceDetail_SaInvoice
        FOREIGN KEY (CompanyCode, BranchCode, InvNo)
        REFERENCES dbo.SaInvoice (CompanyCode, BranchCode, InvNo)
        ON DELETE CASCADE;

    CREATE UNIQUE INDEX UQ_SaInvoiceDetail_Company_Branch_InvNo_Line
        ON dbo.SaInvoiceDetail (CompanyCode, BranchCode, InvNo, Line);
END
GO
