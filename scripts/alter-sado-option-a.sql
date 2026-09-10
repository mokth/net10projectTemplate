-- SaDO Option A: PK (CompanyCode, BranchCode, DONo).
-- Manual DBA script. Do NOT run at app startup.
-- Rollback = restore from pre-window backup (not an in-place PK downgrade).
-- Fails closed on blank Company/Branch, duplicates, or orphan details. Does not invent HQ.
GO

IF OBJECT_ID(N'dbo.SaDO', N'U') IS NULL
BEGIN
    RAISERROR(N'SaDO does not exist.', 16, 1);
    RETURN;
END
GO

IF OBJECT_ID(N'dbo.SaDODetail', N'U') IS NULL
BEGIN
    RAISERROR(N'SaDODetail does not exist.', 16, 1);
    RETURN;
END
GO

-- Ensure tenant columns on header
IF COL_LENGTH(N'dbo.SaDO', N'CompanyCode') IS NULL
    ALTER TABLE dbo.SaDO ADD CompanyCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SaDO', N'BranchCode') IS NULL
    ALTER TABLE dbo.SaDO ADD BranchCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SaDO', N'LocationCode') IS NULL
    ALTER TABLE dbo.SaDO ADD LocationCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SaDO', N'CurrRate') IS NULL
    ALTER TABLE dbo.SaDO ADD CurrRate decimal(18,6) NOT NULL CONSTRAINT DF_SaDO_CurrRate DEFAULT (1);
GO
IF COL_LENGTH(N'dbo.SaDO', N'PostedDate') IS NULL
    ALTER TABLE dbo.SaDO ADD PostedDate datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.SaDO', N'PostedBy') IS NULL
    ALTER TABLE dbo.SaDO ADD PostedBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaDO', N'RollbackDate') IS NULL
    ALTER TABLE dbo.SaDO ADD RollbackDate datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.SaDO', N'RollbackBy') IS NULL
    ALTER TABLE dbo.SaDO ADD RollbackBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaDO', N'RowVersion') IS NULL
    ALTER TABLE dbo.SaDO ADD RowVersion rowversion NOT NULL;
GO

-- Ensure tenant + commercial columns on detail
IF COL_LENGTH(N'dbo.SaDODetail', N'CompanyCode') IS NULL
    ALTER TABLE dbo.SaDODetail ADD CompanyCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SaDODetail', N'BranchCode') IS NULL
    ALTER TABLE dbo.SaDODetail ADD BranchCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SaDODetail', N'StockControl') IS NULL
    ALTER TABLE dbo.SaDODetail ADD StockControl bit NOT NULL CONSTRAINT DF_SaDODetail_StockControl DEFAULT (1);
GO
IF COL_LENGTH(N'dbo.SaDODetail', N'ItemDiscount2') IS NULL
    ALTER TABLE dbo.SaDODetail ADD ItemDiscount2 decimal(18,6) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscount2 DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SaDODetail', N'ItemDiscount3') IS NULL
    ALTER TABLE dbo.SaDODetail ADD ItemDiscount3 decimal(18,6) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscount3 DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SaDODetail', N'ItemDiscount4') IS NULL
    ALTER TABLE dbo.SaDODetail ADD ItemDiscount4 decimal(18,6) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscount4 DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SaDODetail', N'ItemDiscount5') IS NULL
    ALTER TABLE dbo.SaDODetail ADD ItemDiscount5 decimal(18,6) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscount5 DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SaDODetail', N'ItemDiscount6') IS NULL
    ALTER TABLE dbo.SaDODetail ADD ItemDiscount6 decimal(18,6) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscount6 DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SaDODetail', N'ItemDiscAmount') IS NULL
    ALTER TABLE dbo.SaDODetail ADD ItemDiscAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscAmount DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SaDODetail', N'ItemDiscAmount1') IS NULL
    ALTER TABLE dbo.SaDODetail ADD ItemDiscAmount1 decimal(18,2) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscAmount1 DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SaDODetail', N'LocalAmount') IS NULL
    ALTER TABLE dbo.SaDODetail ADD LocalAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaDODetail_LocalAmount DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SaDODetail', N'Classification') IS NULL
    ALTER TABLE dbo.SaDODetail ADD Classification nvarchar(50) NULL;
GO

PRINT N'Pre-check: blank CompanyCode/BranchCode on SaDO';
IF EXISTS (
    SELECT 1 FROM dbo.SaDO
    WHERE CompanyCode IS NULL OR LTRIM(RTRIM(CompanyCode)) = N''
       OR BranchCode IS NULL OR LTRIM(RTRIM(BranchCode)) = N'')
BEGIN
    RAISERROR(N'SaDO has null/blank CompanyCode or BranchCode. Backfill before Option A PK change.', 16, 1);
    RETURN;
END
GO

-- Backfill detail tenant from header (by DONo only when detail Company blank)
UPDATE d
SET d.CompanyCode = h.CompanyCode,
    d.BranchCode = h.BranchCode
FROM dbo.SaDODetail d
INNER JOIN dbo.SaDO h ON h.DONo = d.DONo
WHERE d.CompanyCode IS NULL OR LTRIM(RTRIM(d.CompanyCode)) = N''
   OR d.BranchCode IS NULL OR LTRIM(RTRIM(d.BranchCode)) = N''
   OR d.CompanyCode <> h.CompanyCode
   OR d.BranchCode <> h.BranchCode;
GO

IF EXISTS (
    SELECT 1 FROM dbo.SaDODetail
    WHERE CompanyCode IS NULL OR LTRIM(RTRIM(CompanyCode)) = N''
       OR BranchCode IS NULL OR LTRIM(RTRIM(BranchCode)) = N'')
BEGIN
    RAISERROR(N'SaDODetail still has null/blank CompanyCode or BranchCode after header copy.', 16, 1);
    RETURN;
END
GO

IF EXISTS (
    SELECT CompanyCode, BranchCode, DONo, COUNT(*) AS Cnt
    FROM dbo.SaDO
    GROUP BY CompanyCode, BranchCode, DONo
    HAVING COUNT(*) > 1)
BEGIN
    RAISERROR(N'Duplicate (CompanyCode, BranchCode, DONo) exists. Resolve before Option A PK.', 16, 1);
    RETURN;
END
GO

IF EXISTS (
    SELECT 1
    FROM dbo.SaDODetail d
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.SaDO h
        WHERE h.CompanyCode = d.CompanyCode AND h.BranchCode = d.BranchCode AND h.DONo = d.DONo))
BEGIN
    RAISERROR(N'SaDODetail orphan rows exist (no matching SaDO header). Resolve before Option A PK.', 16, 1);
    RETURN;
END
GO

-- Informational: SP refs that look like DO numbers
IF OBJECT_ID(N'dbo.IvTrxBatch', N'U') IS NOT NULL
BEGIN
    SELECT COUNT(*) AS SpRefMatchingDoNo
    FROM dbo.IvTrxBatch b
    WHERE b.TrxType = N'SP'
      AND b.RefNo IS NOT NULL
      AND (
          EXISTS (SELECT 1 FROM dbo.SaDO d WHERE d.DONo = b.RefNo)
          OR EXISTS (SELECT 1 FROM dbo.SaDO d WHERE (N'DO/' + d.DONo) = b.RefNo)
      );
END
GO

-- Already on Option A?
IF EXISTS (
    SELECT 1
    FROM sys.key_constraints kc
    INNER JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
    INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SaDO')
      AND kc.type = N'PK'
      AND c.name = N'BranchCode')
BEGIN
    PRINT N'SaDO PK already includes BranchCode. Skipping PK recreate.';
END
ELSE
BEGIN
    DECLARE @fkName sysname;
    SELECT TOP 1 @fkName = fk.name
    FROM sys.foreign_keys fk
    WHERE fk.parent_object_id = OBJECT_ID(N'dbo.SaDODetail')
      AND fk.referenced_object_id = OBJECT_ID(N'dbo.SaDO');
    IF @fkName IS NOT NULL
        EXEC(N'ALTER TABLE dbo.SaDODetail DROP CONSTRAINT [' + @fkName + N']');

    DECLARE @detailPk sysname;
    SELECT @detailPk = kc.name
    FROM sys.key_constraints kc
    WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SaDODetail') AND kc.type = N'PK';
    IF @detailPk IS NOT NULL
        EXEC(N'ALTER TABLE dbo.SaDODetail DROP CONSTRAINT [' + @detailPk + N']');

    DECLARE @pkName sysname;
    SELECT @pkName = kc.name
    FROM sys.key_constraints kc
    WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SaDO') AND kc.type = N'PK';
    IF @pkName IS NOT NULL
        EXEC(N'ALTER TABLE dbo.SaDO DROP CONSTRAINT [' + @pkName + N']');

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDO') AND name = N'IX_SaDO_Company_Status')
        DROP INDEX IX_SaDO_Company_Status ON dbo.SaDO;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDO') AND name = N'IX_SaDO_Company_CustCode')
        DROP INDEX IX_SaDO_Company_CustCode ON dbo.SaDO;

    ALTER TABLE dbo.SaDO ALTER COLUMN CompanyCode nvarchar(10) NOT NULL;
    ALTER TABLE dbo.SaDO ALTER COLUMN BranchCode nvarchar(10) NOT NULL;
    ALTER TABLE dbo.SaDODetail ALTER COLUMN CompanyCode nvarchar(10) NOT NULL;
    ALTER TABLE dbo.SaDODetail ALTER COLUMN BranchCode nvarchar(10) NOT NULL;

    ALTER TABLE dbo.SaDO
        ADD CONSTRAINT PK_SaDO PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DONo);

    ALTER TABLE dbo.SaDODetail
        ADD CONSTRAINT PK_SaDODetail PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DONo, Line);

    ALTER TABLE dbo.SaDODetail
        ADD CONSTRAINT FK_SaDODetail_SaDO
        FOREIGN KEY (CompanyCode, BranchCode, DONo)
        REFERENCES dbo.SaDO (CompanyCode, BranchCode, DONo)
        ON DELETE CASCADE;

    CREATE UNIQUE INDEX UQ_SaDODetail_Company_Branch_DoNo_Line
        ON dbo.SaDODetail (CompanyCode, BranchCode, DONo, Line);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaDO_Company_Branch_Status_DoDate' AND object_id = OBJECT_ID(N'dbo.SaDO'))
    CREATE INDEX IX_SaDO_Company_Branch_Status_DoDate
    ON dbo.SaDO (CompanyCode, BranchCode, Status, DODate);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaDO_Company_Branch_CustCode' AND object_id = OBJECT_ID(N'dbo.SaDO'))
    CREATE INDEX IX_SaDO_Company_Branch_CustCode
    ON dbo.SaDO (CompanyCode, BranchCode, CustCode);
GO

IF OBJECT_ID(N'dbo.IvTrxBatch', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IvTrxBatch_Company_Branch_TrxType_RefNo' AND object_id = OBJECT_ID(N'dbo.IvTrxBatch'))
        CREATE INDEX IX_IvTrxBatch_Company_Branch_TrxType_RefNo
        ON dbo.IvTrxBatch (CompanyCode, BranchCode, TrxType, RefNo);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IvTrxBatch_SP_Ref' AND object_id = OBJECT_ID(N'dbo.IvTrxBatch'))
        AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IvTrxBatch_SP_RefNo' AND object_id = OBJECT_ID(N'dbo.IvTrxBatch'))
        CREATE UNIQUE INDEX UQ_IvTrxBatch_SP_Ref
        ON dbo.IvTrxBatch (CompanyCode, BranchCode, RefNo)
        WHERE TrxType = N'SP' AND RefNo IS NOT NULL;
END
GO

PRINT N'SaDO Option A migration complete. Validate row counts against pre-window backup.';
GO
