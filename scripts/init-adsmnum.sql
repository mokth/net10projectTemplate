-- AdSmNum / AdSmNumDate (document numbering) + DEMO/HQ monthly INV seed.
-- Target: ERPWeb (ConnectionStrings:DefaultConnection). Manual DBA script — do NOT run at app startup.
-- Idempotent create + seed. Never resets a live Seq.
GO

-- ========== AdSmNum ==========
IF OBJECT_ID(N'dbo.AdSmNum', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AdSmNum (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        LocationCode nvarchar(20) NULL,
        NumCd nvarchar(10) NOT NULL,
        NumDes nvarchar(30) NULL,
        TotLength smallint NOT NULL,
        Prefix nvarchar(10) NULL,
        Seq bigint NOT NULL,
        Created datetime NULL,
        Updated datetime NULL,
        UserID nvarchar(10) NULL,
        UpdatedUID nvarchar(10) NULL,
        CONSTRAINT PK_AdSmNum PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, NumCd)
    );
END
ELSE IF COL_LENGTH(N'dbo.AdSmNum', N'CompanyCode') IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.AdSmNum)
    BEGIN
        RAISERROR(N'AdSmNum has rows without tenant columns. Migrate or empty the table before adding CompanyCode/BranchCode.', 16, 1);
        RETURN;
    END

    ALTER TABLE dbo.AdSmNum ADD CompanyCode nvarchar(10) NULL;
    ALTER TABLE dbo.AdSmNum ADD BranchCode nvarchar(10) NULL;
    ALTER TABLE dbo.AdSmNum ADD LocationCode nvarchar(20) NULL;

    DECLARE @pkAdSmNum sysname;
    SELECT @pkAdSmNum = kc.name
    FROM sys.key_constraints kc
    WHERE kc.parent_object_id = OBJECT_ID(N'dbo.AdSmNum') AND kc.type = N'PK';
    IF @pkAdSmNum IS NOT NULL
        EXEC(N'ALTER TABLE dbo.AdSmNum DROP CONSTRAINT [' + @pkAdSmNum + N']');

    ALTER TABLE dbo.AdSmNum ALTER COLUMN CompanyCode nvarchar(10) NOT NULL;
    ALTER TABLE dbo.AdSmNum ALTER COLUMN BranchCode nvarchar(10) NOT NULL;
    ALTER TABLE dbo.AdSmNum ADD CONSTRAINT PK_AdSmNum PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, NumCd);
END
GO

-- ========== AdSmNumDate ==========
IF OBJECT_ID(N'dbo.AdSmNumDate', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AdSmNumDate (
        [uid] int IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        LocationCode nvarchar(20) NULL,
        [Year] smallint NULL,
        [Month] smallint NULL,
        NumCd nvarchar(20) NULL,
        NumDes nvarchar(30) NULL,
        TotLength smallint NULL,
        Prefix nvarchar(20) NULL,
        Seq bigint NULL,
        Created datetime NULL CONSTRAINT DF_AdSmNumDate_Created DEFAULT (getdate()),
        Updated datetime NULL,
        UserID nvarchar(10) NULL,
        NumberingDelimeter nvarchar(5) NULL,
        RowVersion rowversion NULL,
        NumberingFormat nvarchar(50) NULL,
        CONSTRAINT PK_AdSmNumDate PRIMARY KEY CLUSTERED ([uid])
    );
END
ELSE IF COL_LENGTH(N'dbo.AdSmNumDate', N'CompanyCode') IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.AdSmNumDate)
    BEGIN
        RAISERROR(N'AdSmNumDate has rows without tenant columns. Migrate or empty the table before adding CompanyCode/BranchCode.', 16, 1);
        RETURN;
    END

    ALTER TABLE dbo.AdSmNumDate ADD CompanyCode nvarchar(10) NULL;
    ALTER TABLE dbo.AdSmNumDate ADD BranchCode nvarchar(10) NULL;
    ALTER TABLE dbo.AdSmNumDate ADD LocationCode nvarchar(20) NULL;

    ALTER TABLE dbo.AdSmNumDate ALTER COLUMN CompanyCode nvarchar(10) NOT NULL;
    ALTER TABLE dbo.AdSmNumDate ALTER COLUMN BranchCode nvarchar(10) NOT NULL;
END
GO

IF OBJECT_ID(N'dbo.AdSmNumDate', N'U') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.AdSmNumDate')
          AND name = N'UX_AdSmNumDate_Tenant_NumCd_Year_Month')
BEGIN
    CREATE UNIQUE INDEX UX_AdSmNumDate_Tenant_NumCd_Year_Month
    ON dbo.AdSmNumDate (CompanyCode, BranchCode, NumCd, [Year], [Month])
    WHERE NumCd IS NOT NULL AND [Year] IS NOT NULL AND [Month] IS NOT NULL;
END
GO

-- ========== DEMO monthly INV seed (Seq = next to issue) ==========
IF OBJECT_ID(N'dbo.AdSmNumDate', N'U') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM dbo.AdSmNumDate
        WHERE CompanyCode = N'DEMO'
          AND BranchCode = N'HQ'
          AND NumCd = N'INV'
          AND [Year] = 2026
          AND [Month] = 9)
BEGIN
    INSERT INTO dbo.AdSmNumDate (
        CompanyCode, BranchCode, LocationCode,
        [Year], [Month], NumCd, NumDes, TotLength, Prefix, Seq,
        Created, UserID, NumberingDelimeter, NumberingFormat)
    VALUES (
        N'DEMO', N'HQ', N'MAIN',
        2026, 9, N'INV', N'Sales Invoice', 4, N'INV', 1,
        GETDATE(), N'SYSTEM', N'-', NULL);
END
GO
