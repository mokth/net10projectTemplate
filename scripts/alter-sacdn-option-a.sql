-- SaCDN Option A: PK (CompanyCode, BranchCode, DocNo) + return-stock columns + CR fingerprint.
-- Manual DBA script. Do NOT run at app startup.
--
-- Deployment checklist:
--   1. BACKUP the database before running.
--   2. Inventory live SaCDN for current PK, FKs, indexes, triggers, views, SPs, reports.
--   3. Ensure CompanyCode/BranchCode are populated (non-blank) on every row.
--   4. Run this script in a maintenance window.
--   5. Validate row counts and sample docs.
--   6. Deploy application.
-- Rollback / recovery: restore from the pre-window backup if PK migration fails.
GO

IF OBJECT_ID(N'dbo.SaCDN', N'U') IS NULL
BEGIN
    RAISERROR(N'SaCDN does not exist. Run create-sacdn.sql for empty databases.', 16, 1);
    RETURN;
END
GO

IF OBJECT_ID(N'dbo.SaCDNDetail', N'U') IS NULL
BEGIN
    RAISERROR(N'SaCDNDetail does not exist. Run create-sacdn.sql for empty databases.', 16, 1);
    RETURN;
END
GO

-- Header columns
IF COL_LENGTH(N'dbo.SaCDN', N'ReturnStock') IS NULL
    ALTER TABLE dbo.SaCDN ADD ReturnStock bit NOT NULL CONSTRAINT DF_SaCDN_ReturnStock_Alter DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SaCDN', N'PostedDate') IS NULL
    ALTER TABLE dbo.SaCDN ADD PostedDate datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.SaCDN', N'PostedBy') IS NULL
    ALTER TABLE dbo.SaCDN ADD PostedBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaCDN', N'RollbackDate') IS NULL
    ALTER TABLE dbo.SaCDN ADD RollbackDate datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.SaCDN', N'RollbackBy') IS NULL
    ALTER TABLE dbo.SaCDN ADD RollbackBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaCDN', N'RowVersion') IS NULL
    ALTER TABLE dbo.SaCDN ADD RowVersion rowversion NOT NULL;
GO

-- Detail return-stock columns
IF COL_LENGTH(N'dbo.SaCDNDetail', N'FrWarehouse') IS NULL
    ALTER TABLE dbo.SaCDNDetail ADD FrWarehouse nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaCDNDetail', N'LocCode') IS NULL
    ALTER TABLE dbo.SaCDNDetail ADD LocCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaCDNDetail', N'IStatus') IS NULL
    ALTER TABLE dbo.SaCDNDetail ADD IStatus nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaCDNDetail', N'LotNo') IS NULL
    ALTER TABLE dbo.SaCDNDetail ADD LotNo nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaCDNDetail', N'ExpiryDate') IS NULL
    ALTER TABLE dbo.SaCDNDetail ADD ExpiryDate date NULL;
GO
IF COL_LENGTH(N'dbo.SaCDNDetail', N'StockControl') IS NULL
    ALTER TABLE dbo.SaCDNDetail ADD StockControl bit NOT NULL CONSTRAINT DF_SaCDNDetail_StockControl_Alter DEFAULT (0);
GO

-- Tenant columns on detail if missing (legacy DocNo-only detail)
IF COL_LENGTH(N'dbo.SaCDNDetail', N'CompanyCode') IS NULL
    ALTER TABLE dbo.SaCDNDetail ADD CompanyCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SaCDNDetail', N'BranchCode') IS NULL
    ALTER TABLE dbo.SaCDNDetail ADD BranchCode nvarchar(10) NULL;
GO

PRINT N'Backfill detail CompanyCode/BranchCode from header when blank';
UPDATE d
SET d.CompanyCode = h.CompanyCode,
    d.BranchCode = h.BranchCode
FROM dbo.SaCDNDetail d
INNER JOIN dbo.SaCDN h ON h.DocNo = d.DocNo
WHERE d.CompanyCode IS NULL OR LTRIM(RTRIM(d.CompanyCode)) = N''
   OR d.BranchCode IS NULL OR LTRIM(RTRIM(d.BranchCode)) = N'';
GO

PRINT N'Pre-check: blank CompanyCode/BranchCode on SaCDN';
IF EXISTS (
    SELECT 1 FROM dbo.SaCDN
    WHERE CompanyCode IS NULL OR LTRIM(RTRIM(CompanyCode)) = N''
       OR BranchCode IS NULL OR LTRIM(RTRIM(BranchCode)) = N'')
BEGIN
    RAISERROR(N'SaCDN has null/blank CompanyCode or BranchCode. Backfill before Option A PK change.', 16, 1);
    RETURN;
END
GO

PRINT N'Pre-check: blank CompanyCode/BranchCode on SaCDNDetail';
IF EXISTS (
    SELECT 1 FROM dbo.SaCDNDetail
    WHERE CompanyCode IS NULL OR LTRIM(RTRIM(CompanyCode)) = N''
       OR BranchCode IS NULL OR LTRIM(RTRIM(BranchCode)) = N'')
BEGIN
    RAISERROR(N'SaCDNDetail has null/blank CompanyCode or BranchCode. Backfill before Option A PK change.', 16, 1);
    RETURN;
END
GO

PRINT N'Pre-check: duplicate Option A keys';
IF EXISTS (
    SELECT CompanyCode, BranchCode, DocNo
    FROM dbo.SaCDN
    GROUP BY CompanyCode, BranchCode, DocNo
    HAVING COUNT(*) > 1)
BEGIN
    RAISERROR(N'SaCDN has duplicate (CompanyCode, BranchCode, DocNo).', 16, 1);
    RETURN;
END
GO

-- CHECKs
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_SaCDN_Type' AND parent_object_id = OBJECT_ID(N'dbo.SaCDN'))
    ALTER TABLE dbo.SaCDN ADD CONSTRAINT CK_SaCDN_Type CHECK (Type IN (N'CN', N'DN'));
GO
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_SaCDN_Status' AND parent_object_id = OBJECT_ID(N'dbo.SaCDN'))
    ALTER TABLE dbo.SaCDN ADD CONSTRAINT CK_SaCDN_Status CHECK (Status IN (N'NEW', N'POSTED'));
GO

-- Rebuild header PK to Option A if still DocNo-only
DECLARE @pkName sysname;
SELECT @pkName = kc.name
FROM sys.key_constraints kc
WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SaCDN') AND kc.type = N'PK';

IF @pkName IS NOT NULL
   AND NOT EXISTS (
        SELECT 1
        FROM sys.index_columns ic
        INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE ic.object_id = OBJECT_ID(N'dbo.SaCDN')
          AND ic.index_id = (SELECT index_id FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaCDN') AND is_primary_key = 1)
          AND c.name = N'CompanyCode')
BEGIN
    -- Drop detail FK pointing at old PK
    DECLARE @fkName sysname;
    SELECT TOP 1 @fkName = fk.name
    FROM sys.foreign_keys fk
    WHERE fk.parent_object_id = OBJECT_ID(N'dbo.SaCDNDetail')
      AND fk.referenced_object_id = OBJECT_ID(N'dbo.SaCDN');
    IF @fkName IS NOT NULL
        EXEC(N'ALTER TABLE dbo.SaCDNDetail DROP CONSTRAINT [' + @fkName + N']');

    DECLARE @dtlPk sysname;
    SELECT @dtlPk = kc.name
    FROM sys.key_constraints kc
    WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SaCDNDetail') AND kc.type = N'PK';
    IF @dtlPk IS NOT NULL
        EXEC(N'ALTER TABLE dbo.SaCDNDetail DROP CONSTRAINT [' + @dtlPk + N']');

    EXEC(N'ALTER TABLE dbo.SaCDN DROP CONSTRAINT [' + @pkName + N']');

    ALTER TABLE dbo.SaCDN ALTER COLUMN CompanyCode nvarchar(10) NOT NULL;
    ALTER TABLE dbo.SaCDN ALTER COLUMN BranchCode nvarchar(10) NOT NULL;
    ALTER TABLE dbo.SaCDN ADD CONSTRAINT PK_SaCDN PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocNo);

    ALTER TABLE dbo.SaCDNDetail ALTER COLUMN CompanyCode nvarchar(10) NOT NULL;
    ALTER TABLE dbo.SaCDNDetail ALTER COLUMN BranchCode nvarchar(10) NOT NULL;
    ALTER TABLE dbo.SaCDNDetail ADD CONSTRAINT PK_SaCDNDetail PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocNo, Line);
    ALTER TABLE dbo.SaCDNDetail ADD CONSTRAINT FK_SaCDNDetail_SaCDN
        FOREIGN KEY (CompanyCode, BranchCode, DocNo)
        REFERENCES dbo.SaCDN (CompanyCode, BranchCode, DocNo) ON DELETE CASCADE;
END
GO

IF OBJECT_ID(N'dbo.IvTrxBatch', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.IvTrxBatch', N'SourceFingerprint') IS NULL
    ALTER TABLE dbo.IvTrxBatch ADD SourceFingerprint nvarchar(64) NULL;
GO

IF OBJECT_ID(N'dbo.IvTrxBatch', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IvTrxBatch_CR_CnRef' AND object_id = OBJECT_ID(N'dbo.IvTrxBatch'))
    -- Filtered indexes cannot use LIKE. Range is equivalent to RefNo LIKE N'CN/%'.
    CREATE UNIQUE INDEX UQ_IvTrxBatch_CR_CnRef
    ON dbo.IvTrxBatch (CompanyCode, BranchCode, RefNo)
    WHERE TrxType = N'CR' AND RefNo >= N'CN/' AND RefNo < N'CN0';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaCDN_Company_Branch_Status_DocDate' AND object_id = OBJECT_ID(N'dbo.SaCDN'))
    CREATE INDEX IX_SaCDN_Company_Branch_Status_DocDate
    ON dbo.SaCDN (CompanyCode, BranchCode, Status, DocDate);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaCDN_Company_Branch_Type_Status' AND object_id = OBJECT_ID(N'dbo.SaCDN'))
    CREATE INDEX IX_SaCDN_Company_Branch_Type_Status
    ON dbo.SaCDN (CompanyCode, BranchCode, Type, Status);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaCDN_Company_Branch_CustCode' AND object_id = OBJECT_ID(N'dbo.SaCDN'))
    CREATE INDEX IX_SaCDN_Company_Branch_CustCode
    ON dbo.SaCDN (CompanyCode, BranchCode, CustCode);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaCDN_Company_Branch_InvNo' AND object_id = OBJECT_ID(N'dbo.SaCDN'))
    CREATE INDEX IX_SaCDN_Company_Branch_InvNo
    ON dbo.SaCDN (CompanyCode, BranchCode, InvNo);
GO
