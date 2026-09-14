-- Sales master tables not present in ERPWeb at Phase 0 audit.
-- Manual deploy only — do NOT run at app startup.
-- Target: same database as ConnectionStrings:DefaultConnection

SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.IvAreaCode', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IvAreaCode (
        CompanyCode   nvarchar(10)  NOT NULL,
        AreaCode      nvarchar(40)  NOT NULL,
        AreaDesc      nvarchar(200) NULL,
        BranchCode    nvarchar(10)  NULL,
        LocationCode  nvarchar(20)  NULL,
        Created       datetime2     NULL,
        Updated       datetime2     NULL,
        UserID        nvarchar(20)  NULL,
        UpdatedUID    nvarchar(20)  NULL,
        latitude      nvarchar(50)  NULL,
        longitude     nvarchar(50)  NULL,
        CONSTRAINT PK_IvAreaCode PRIMARY KEY (CompanyCode, AreaCode)
    );
END
GO

IF OBJECT_ID(N'dbo.IvMSCode', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IvMSCode (
        ID         int IDENTITY(1,1) NOT NULL,
        Code       nvarchar(50)  NOT NULL,
        Name       nvarchar(100) NULL,
        CodeType   nvarchar(20)  NOT NULL,
        Latitude   decimal(9,6)  NULL,
        Longitude  decimal(9,6)  NULL,
        CONSTRAINT PK_IvMSCode PRIMARY KEY (ID),
        CONSTRAINT UX_IvMSCode_CodeType_Code UNIQUE (CodeType, Code)
    );
END
GO

-- Live ERPWeb.IvMSCode.Code is nvarchar(5). Widen before INDUSTRY/CHANNEL seeds (DEALER/ENDUSER).
IF COL_LENGTH(N'dbo.IvMSCode', N'Code') IS NOT NULL AND COL_LENGTH(N'dbo.IvMSCode', N'Code') < 100
    ALTER TABLE dbo.IvMSCode ALTER COLUMN Code nvarchar(50) NULL;
GO

IF OBJECT_ID(N'dbo.SaCountry', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaCountry (
        CountryCode  nvarchar(20)  NOT NULL,
        CountryName  nvarchar(100) NULL,
        Latitude     decimal(9,6)  NULL,
        Longitude    decimal(9,6)  NULL,
        CONSTRAINT PK_SaCountry PRIMARY KEY (CountryCode)
    );
END
GO

IF OBJECT_ID(N'dbo.SaCurrency', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaCurrency (
        CompanyCode   nvarchar(10)  NOT NULL,
        CurrCode      nvarchar(20)  NOT NULL,
        CurrDesc      nvarchar(100) NULL,
        Active        bit           NULL,
        Created       datetime2     NULL,
        Updated       datetime2     NULL,
        UserID        nvarchar(20)  NULL,
        UpdatedUID    nvarchar(20)  NULL,
        BranchCode    nvarchar(10)  NULL,
        LocationCode  nvarchar(20)  NULL,
        CONSTRAINT PK_SaCurrency PRIMARY KEY (CompanyCode, CurrCode)
    );
END
GO

IF OBJECT_ID(N'dbo.SaDisGroup', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDisGroup (
        CompanyCode   nvarchar(10)  NOT NULL,
        GroupName     nvarchar(40)  NOT NULL,
        PayCode       nvarchar(40)  NOT NULL,
        GroupLevel    smallint      NULL,
        Discount      float         NULL,
        Discount2     float         NULL,
        Discount3     float         NULL,
        DiscountType  nvarchar(20)  NULL,
        GroupStatus   nvarchar(20)  NULL,
        BranchCode    nvarchar(10)  NULL,
        LocationCode  nvarchar(20)  NULL,
        Created       datetime2     NULL,
        Updated       datetime2     NULL,
        UserID        nvarchar(20)  NULL,
        UpdatedUID    nvarchar(20)  NULL,
        CONSTRAINT PK_SaDisGroup PRIMARY KEY (CompanyCode, GroupName, PayCode)
    );
END
GO

IF OBJECT_ID(N'dbo.SaDisCust', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDisCust (
        CompanyCode  nvarchar(10) NOT NULL,
        GroupName    nvarchar(40) NOT NULL,
        PayCode      nvarchar(40) NOT NULL,
        CustCode     nvarchar(30) NOT NULL,
        CustName     nvarchar(200) NOT NULL,
        CONSTRAINT PK_SaDisCust PRIMARY KEY (CompanyCode, GroupName, PayCode, CustCode)
    );
END
GO

IF OBJECT_ID(N'dbo.SaPaymentTerm', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaPaymentTerm (
        CompanyCode   nvarchar(10)  NOT NULL,
        PayCode       nvarchar(20)  NOT NULL,
        PayDesc       nvarchar(100) NULL,
        Days          int           NULL,
        Active        bit           NULL,
        Created       datetime2     NULL,
        Updated       datetime2     NULL,
        UserID        nvarchar(20)  NULL,
        UpdatedUID    nvarchar(20)  NULL,
        BranchCode    nvarchar(10)  NULL,
        LocationCode  nvarchar(20)  NULL,
        CONSTRAINT PK_SaPaymentTerm PRIMARY KEY (CompanyCode, PayCode)
    );
END
GO

IF OBJECT_ID(N'dbo.SaSalesRep', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaSalesRep (
        CompanyCode     nvarchar(10)   NOT NULL,
        SRepCode        nvarchar(20)   NOT NULL,
        SRepName        nvarchar(200)  NULL,
        Address1        nvarchar(100)  NULL,
        Address2        nvarchar(100)  NULL,
        Address3        nvarchar(100)  NULL,
        City            nvarchar(50)   NULL,
        State           nvarchar(50)   NULL,
        PostalCode      nvarchar(20)   NULL,
        Country         nvarchar(50)   NULL,
        Tel             nvarchar(50)   NULL,
        Mobile          nvarchar(50)   NULL,
        Email           nvarchar(100)  NULL,
        Active          bit            NULL,
        CommissionRate  decimal(18,6)  NULL,
        Created         datetime2      NULL,
        Updated         datetime2      NULL,
        UserID          nvarchar(20)   NULL,
        UpdatedUID      nvarchar(20)   NULL,
        BranchCode      nvarchar(10)   NULL,
        LocationCode    nvarchar(20)   NULL,
        CONSTRAINT PK_SaSalesRep PRIMARY KEY (CompanyCode, SRepCode)
    );
END
GO

IF OBJECT_ID(N'dbo.SaTaxGroup', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaTaxGroup (
        CompanyCode   nvarchar(10)  NOT NULL,
        TaxGrCode     nvarchar(20)  NOT NULL,
        TaxGrDesc     nvarchar(100) NULL,
        Percentage    decimal(18,6) NOT NULL CONSTRAINT DF_SaTaxGroup_Percentage DEFAULT (0),
        Created       datetime2     NULL,
        Updated       datetime2     NULL,
        UserID        nvarchar(20)  NULL,
        UpdatedUID    nvarchar(20)  NULL,
        BranchCode    nvarchar(10)  NULL,
        LocationCode  nvarchar(20)  NULL,
        CONSTRAINT PK_SaTaxGroup PRIMARY KEY (CompanyCode, TaxGrCode)
    );
END
GO

IF COL_LENGTH(N'dbo.SaTaxGroup', N'CompanyCode') IS NULL
    ALTER TABLE dbo.SaTaxGroup ADD CompanyCode nvarchar(10) NOT NULL CONSTRAINT DF_SaTaxGroup_CompanyCode DEFAULT (N'');
GO

IF COL_LENGTH(N'dbo.SaTaxGroup', N'BranchCode') IS NULL
    ALTER TABLE dbo.SaTaxGroup ADD BranchCode nvarchar(10) NULL;
GO

IF COL_LENGTH(N'dbo.SaTaxGroup', N'LocationCode') IS NULL
    ALTER TABLE dbo.SaTaxGroup ADD LocationCode nvarchar(20) NULL;
GO

-- Re-align the SaTaxGroup PK to (CompanyCode, TaxGrCode) to match SaTaxGroupConfiguration.
-- Three hazards are handled here:
--   1. CompanyCode is NULLABLE in databases created before this script (restored/legacy schemas).
--      ADD PRIMARY KEY on a nullable column fails with Msg 8111, surfaced as
--      Msg 1750 "Could not create constraint or index". Backfill + NOT NULL fixes it.
--   2. ADD CONSTRAINT ... PRIMARY KEY is validated when the batch is *compiled*, so a nullable
--      CompanyCode raises Msg 8111 before the batch starts running: TRY/CATCH cannot catch it and
--      even PRINT output is lost. Every DDL statement below therefore runs through
--      sp_executesql, which is compiled at execution time and is genuinely catchable.
--   3. The rebuild used to DROP the existing PK before attempting the ADD, so any failure left
--      SaTaxGroup with no primary key at all. DROP + ADD now run in one atomic transaction and
--      roll back together, so a failed rebuild can never destroy the existing key.
IF OBJECT_ID(N'dbo.SaTaxGroup', N'U') IS NOT NULL
BEGIN
    DECLARE @taxPkName sysname;
    DECLARE @taxPkCols int;
    DECLARE @taxPkHasCc int;
    DECLARE @taxPkHasTg int;
    DECLARE @taxCcNullable bit;
    DECLARE @taxSql nvarchar(300);

    SELECT @taxPkName = kc.name
    FROM sys.key_constraints kc
    WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SaTaxGroup')
      AND kc.[type] = N'PK';

    -- PK shape is inspected with COUNT comparisons rather than FOR XML: XML methods require
    -- QUOTED_IDENTIFIER ON, which sqlcmd does not set by default (Msg 1934).
    SELECT @taxPkCols = COUNT(*)
    FROM sys.index_columns ic
    WHERE ic.object_id = OBJECT_ID(N'dbo.SaTaxGroup')
      AND ic.index_id = (SELECT kc2.unique_index_id
                         FROM sys.key_constraints kc2
                         WHERE kc2.parent_object_id = OBJECT_ID(N'dbo.SaTaxGroup')
                           AND kc2.[type] = N'PK')
      AND ic.is_included_column = 0;

    SELECT @taxPkHasCc = COUNT(*)
    FROM sys.index_columns ic
    INNER JOIN sys.columns c
        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE ic.object_id = OBJECT_ID(N'dbo.SaTaxGroup')
      AND ic.index_id = (SELECT kc2.unique_index_id
                         FROM sys.key_constraints kc2
                         WHERE kc2.parent_object_id = OBJECT_ID(N'dbo.SaTaxGroup')
                           AND kc2.[type] = N'PK')
      AND c.name = N'CompanyCode';

    SELECT @taxPkHasTg = COUNT(*)
    FROM sys.index_columns ic
    INNER JOIN sys.columns c
        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE ic.object_id = OBJECT_ID(N'dbo.SaTaxGroup')
      AND ic.index_id = (SELECT kc2.unique_index_id
                         FROM sys.key_constraints kc2
                         WHERE kc2.parent_object_id = OBJECT_ID(N'dbo.SaTaxGroup')
                           AND kc2.[type] = N'PK')
      AND c.name = N'TaxGrCode';

    SELECT @taxCcNullable = c.is_nullable
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'dbo.SaTaxGroup') AND c.name = N'CompanyCode';

    IF @taxCcNullable IS NULL
    BEGIN
        PRINT N'SKIPPED SaTaxGroup PK: the CompanyCode column is missing.';
    END
    ELSE IF @taxPkCols = 2 AND @taxPkHasCc = 1 AND @taxPkHasTg = 1 AND @taxCcNullable = 0
    BEGIN
        PRINT N'SaTaxGroup PK already (CompanyCode, TaxGrCode). Skipping.';
    END
    ELSE
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;

            IF @taxCcNullable = 1
            BEGIN
                UPDATE dbo.SaTaxGroup SET CompanyCode = N'' WHERE CompanyCode IS NULL;
                -- nvarchar(10) matches SaTaxGroupConfiguration (CompanyCode HasMaxLength(10)).
                EXEC sp_executesql N'ALTER TABLE dbo.SaTaxGroup ALTER COLUMN CompanyCode nvarchar(10) NOT NULL;';
            END

            IF @taxPkName IS NOT NULL
            BEGIN
                SET @taxSql = N'ALTER TABLE dbo.SaTaxGroup DROP CONSTRAINT ' + QUOTENAME(@taxPkName) + N';';
                EXEC sp_executesql @taxSql;
            END

            EXEC sp_executesql N'ALTER TABLE dbo.SaTaxGroup ADD CONSTRAINT PK_SaTaxGroup PRIMARY KEY (CompanyCode, TaxGrCode);';

            COMMIT TRANSACTION;
            PRINT N'SaTaxGroup PK rebuilt as (CompanyCode, TaxGrCode).';
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            PRINT N'SKIPPED SaTaxGroup PK rebuild: ' + ERROR_MESSAGE();
        END CATCH
    END
END
GO

IF OBJECT_ID(N'dbo.SaCurrRate', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaCurrRate (
        SDate           datetime2    NOT NULL,
        EDate           datetime2    NOT NULL,
        CurrCode        nvarchar(20) NOT NULL,
        HomeCurPerUnit  float        NOT NULL,
        Status          bit          NOT NULL CONSTRAINT DF_SaCurrRate_Status DEFAULT (1),
        Created         datetime2    NULL,
        Updated         datetime2    NULL,
        UserID          nvarchar(20) NULL,
        UpdatedUID      nvarchar(20) NULL,
        CONSTRAINT PK_SaCurrRate PRIMARY KEY (SDate, EDate, CurrCode)
    );
    CREATE INDEX IX_SaCurrRate_CurrCode_Dates ON dbo.SaCurrRate (CurrCode, SDate, EDate);
END
GO

-- Seed sample rows for DEMO
IF NOT EXISTS (SELECT 1 FROM dbo.SaCountry WHERE CountryCode = N'MY')
    INSERT INTO dbo.SaCountry (CountryCode, CountryName) VALUES (N'MY', N'Malaysia');

IF NOT EXISTS (SELECT 1 FROM dbo.SaCurrency WHERE CompanyCode = N'DEMO' AND CurrCode = N'MYR')
    INSERT INTO dbo.SaCurrency (CompanyCode, CurrCode, CurrDesc, Active, Created, UserID)
    VALUES (N'DEMO', N'MYR', N'Malaysian Ringgit', 1, SYSUTCDATETIME(), N'SEED');

IF NOT EXISTS (SELECT 1 FROM dbo.IvAreaCode WHERE CompanyCode = N'DEMO' AND AreaCode = N'KL')
    INSERT INTO dbo.IvAreaCode (CompanyCode, AreaCode, AreaDesc, Created, UserID)
    VALUES (N'DEMO', N'KL', N'Kuala Lumpur', SYSUTCDATETIME(), N'SEED');

IF NOT EXISTS (SELECT 1 FROM dbo.IvMSCode WHERE CodeType = N'STATE' AND Code = N'SEL')
    INSERT INTO dbo.IvMSCode (Code, Name, CodeType) VALUES (N'SEL', N'Selangor', N'STATE');

IF NOT EXISTS (SELECT 1 FROM dbo.IvMSCode WHERE CodeType = N'TAX' AND Code = N'SR')
    INSERT INTO dbo.IvMSCode (Code, Name, CodeType) VALUES (N'SR', N'Standard Rated', N'TAX');

IF NOT EXISTS (SELECT 1 FROM dbo.IvMSCode WHERE CodeType = N'PAYCODE' AND Code = N'NET30')
    INSERT INTO dbo.IvMSCode (Code, Name, CodeType) VALUES (N'NET30', N'Net 30 days', N'PAYCODE');

IF NOT EXISTS (SELECT 1 FROM dbo.IvMSCode WHERE CodeType = N'INDUSTRY' AND Code = N'ELEC')
    INSERT INTO dbo.IvMSCode (Code, Name, CodeType) VALUES (N'ELEC', N'Electronics', N'INDUSTRY');

IF NOT EXISTS (SELECT 1 FROM dbo.IvMSCode WHERE CodeType = N'INDUSTRY' AND Code = N'FB')
    INSERT INTO dbo.IvMSCode (Code, Name, CodeType) VALUES (N'FB', N'Food and Beverage', N'INDUSTRY');

IF NOT EXISTS (SELECT 1 FROM dbo.IvMSCode WHERE CodeType = N'INDUSTRY' AND Code = N'CONST')
    INSERT INTO dbo.IvMSCode (Code, Name, CodeType) VALUES (N'CONST', N'Construction', N'INDUSTRY');

IF NOT EXISTS (SELECT 1 FROM dbo.IvMSCode WHERE CodeType = N'CHANNEL' AND Code = N'OEM')
    INSERT INTO dbo.IvMSCode (Code, Name, CodeType) VALUES (N'OEM', N'OEM', N'CHANNEL');

IF NOT EXISTS (SELECT 1 FROM dbo.IvMSCode WHERE CodeType = N'CHANNEL' AND Code = N'DIST')
    INSERT INTO dbo.IvMSCode (Code, Name, CodeType) VALUES (N'DIST', N'Distributor', N'CHANNEL');

IF NOT EXISTS (SELECT 1 FROM dbo.IvMSCode WHERE CodeType = N'CHANNEL' AND Code = N'DEALER')
    INSERT INTO dbo.IvMSCode (Code, Name, CodeType) VALUES (N'DEALER', N'Dealer', N'CHANNEL');

IF NOT EXISTS (SELECT 1 FROM dbo.IvMSCode WHERE CodeType = N'CHANNEL' AND Code = N'ENDUSER')
    INSERT INTO dbo.IvMSCode (Code, Name, CodeType) VALUES (N'ENDUSER', N'End user', N'CHANNEL');

IF NOT EXISTS (SELECT 1 FROM dbo.IvMSCode WHERE CodeType = N'CHANNEL' AND Code = N'AGENT')
    INSERT INTO dbo.IvMSCode (Code, Name, CodeType) VALUES (N'AGENT', N'Agent', N'CHANNEL');

IF NOT EXISTS (SELECT 1 FROM dbo.SaCountry WHERE CountryCode = N'SG')
    INSERT INTO dbo.SaCountry (CountryCode, CountryName) VALUES (N'SG', N'Singapore');

IF NOT EXISTS (SELECT 1 FROM dbo.SaPaymentTerm WHERE CompanyCode = N'DEMO' AND PayCode = N'NET30')
    INSERT INTO dbo.SaPaymentTerm (CompanyCode, PayCode, PayDesc, Days, Active, Created, UserID)
    VALUES (N'DEMO', N'NET30', N'Net 30 days', 30, 1, SYSUTCDATETIME(), N'SEED');

IF NOT EXISTS (SELECT 1 FROM dbo.SaSalesRep WHERE CompanyCode = N'DEMO' AND SRepCode = N'SM1')
    INSERT INTO dbo.SaSalesRep (CompanyCode, SRepCode, SRepName, Active, Created, UserID)
    VALUES (N'DEMO', N'SM1', N'Sample Sales Rep', 1, SYSUTCDATETIME(), N'SEED');

IF NOT EXISTS (SELECT 1 FROM dbo.SaTaxGroup WHERE CompanyCode = N'DEMO' AND TaxGrCode = N'SR')
    INSERT INTO dbo.SaTaxGroup (CompanyCode, TaxGrCode, TaxGrDesc, Percentage, Created, UserID)
    VALUES (N'DEMO', N'SR', N'Standard Rated', 6, SYSUTCDATETIME(), N'SEED');
GO
