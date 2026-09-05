-- Customer Profile schema migration (manual DBA script — do NOT run at app startup).
-- Target database: same as ConnectionStrings:DefaultConnection.
-- Prints exactly one of: PER_COMPANY_KEY_APPLIED | LEGACY_GLOBAL_KEY_PRESERVED | MIGRATION_ABORTED
--
-- Index log (verify before create):
--   IX_SaCust_Company_CustName — needed by list search — create if missing
--   IX_SaCust_Company_Active — needed by list filter — create if missing

SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @KeyOutcome nvarchar(64) = N'MIGRATION_ABORTED';
DECLARE @PkCustCodeOnly bit = 0;

IF OBJECT_ID(N'dbo.SaCust', N'U') IS NULL
BEGIN
    RAISERROR(N'SaCust table does not exist.', 16, 1);
    RETURN;
END

-- Detect physical PK on SaCust
IF EXISTS (
    SELECT 1
    FROM sys.key_constraints kc
    JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SaCust')
      AND kc.type = N'PK'
    GROUP BY kc.name
    HAVING COUNT(*) = 1 AND MAX(c.name) = N'CustCode')
BEGIN
    SET @PkCustCodeOnly = 1;
    SET @KeyOutcome = N'LEGACY_GLOBAL_KEY_PRESERVED';
END
ELSE
BEGIN
    SET @KeyOutcome = N'PER_COMPANY_KEY_APPLIED';
END

BEGIN TRY
    BEGIN TRAN;

    -- Header additive columns (nullable, no default)
    IF COL_LENGTH(N'dbo.SaCust', N'InvoicePrefix') IS NULL ALTER TABLE dbo.SaCust ADD InvoicePrefix nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'CustGroupCode') IS NULL ALTER TABLE dbo.SaCust ADD CustGroupCode nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'LmwAts') IS NULL ALTER TABLE dbo.SaCust ADD LmwAts bit NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'SalesmanCode') IS NULL ALTER TABLE dbo.SaCust ADD SalesmanCode nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'AreaCode') IS NULL ALTER TABLE dbo.SaCust ADD AreaCode nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'SubGroupCode') IS NULL ALTER TABLE dbo.SaCust ADD SubGroupCode nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'Address4') IS NULL ALTER TABLE dbo.SaCust ADD Address4 nvarchar(100) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'CjLmw') IS NULL ALTER TABLE dbo.SaCust ADD CjLmw nvarchar(50) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'CustBrn') IS NULL ALTER TABLE dbo.SaCust ADD CustBrn nvarchar(50) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'RegType') IS NULL ALTER TABLE dbo.SaCust ADD RegType nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'Remark') IS NULL ALTER TABLE dbo.SaCust ADD Remark nvarchar(500) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'DiscountMethod') IS NULL ALTER TABLE dbo.SaCust ADD DiscountMethod nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'PriceMethod') IS NULL ALTER TABLE dbo.SaCust ADD PriceMethod nvarchar(50) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'AgingType') IS NULL ALTER TABLE dbo.SaCust ADD AgingType nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'PaidUpCapital') IS NULL ALTER TABLE dbo.SaCust ADD PaidUpCapital decimal(18,2) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'GlCode') IS NULL ALTER TABLE dbo.SaCust ADD GlCode nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'OpeningAmount') IS NULL ALTER TABLE dbo.SaCust ADD OpeningAmount decimal(18,2) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'CreditTerm') IS NULL ALTER TABLE dbo.SaCust ADD CreditTerm nvarchar(50) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'CreditLimit') IS NULL ALTER TABLE dbo.SaCust ADD CreditLimit decimal(18,2) NULL;
    IF COL_LENGTH(N'dbo.SaCust', N'RowVersion') IS NULL ALTER TABLE dbo.SaCust ADD RowVersion rowversion NOT NULL;

    -- Ensure CompanyCode exists on SaCust
    IF COL_LENGTH(N'dbo.SaCust', N'CompanyCode') IS NULL
        ALTER TABLE dbo.SaCust ADD CompanyCode nvarchar(5) NULL;

    -- SaCustAdd: create or extend
    IF OBJECT_ID(N'dbo.SaCustAdd', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.SaCustAdd (
            CompanyCode nvarchar(5) NOT NULL,
            CustCode nvarchar(30) NOT NULL,
            Line int NOT NULL,
            AddName nvarchar(100) NULL,
            DeliverTo nvarchar(100) NULL,
            Address1 nvarchar(100) NULL,
            Address2 nvarchar(100) NULL,
            Address3 nvarchar(100) NULL,
            Address4 nvarchar(100) NULL,
            City nvarchar(50) NULL,
            State nvarchar(50) NULL,
            PostalCode nvarchar(20) NULL,
            Country nvarchar(50) NULL,
            Tel nvarchar(50) NULL,
            Fax nvarchar(50) NULL,
            CONSTRAINT PK_SaCustAdd PRIMARY KEY (CompanyCode, CustCode, Line)
        );
    END
    ELSE
    BEGIN
        IF COL_LENGTH(N'dbo.SaCustAdd', N'CompanyCode') IS NULL
            ALTER TABLE dbo.SaCustAdd ADD CompanyCode nvarchar(5) NULL;
        IF COL_LENGTH(N'dbo.SaCustAdd', N'AddName') IS NULL
            ALTER TABLE dbo.SaCustAdd ADD AddName nvarchar(100) NULL;
        IF COL_LENGTH(N'dbo.SaCustAdd', N'DeliverTo') IS NULL
            ALTER TABLE dbo.SaCustAdd ADD DeliverTo nvarchar(100) NULL;

        IF EXISTS (SELECT 1 FROM dbo.SaCustAdd)
        BEGIN
            IF EXISTS (
                SELECT CustCode
                FROM dbo.SaCust
                GROUP BY CustCode
                HAVING COUNT(DISTINCT CompanyCode) > 1)
            BEGIN
                RAISERROR(N'SaCustAdd backfill aborted: CustCode maps to multiple companies.', 16, 1);
            END

            IF EXISTS (
                SELECT a.CustCode
                FROM dbo.SaCustAdd a
                LEFT JOIN dbo.SaCust c ON c.CustCode = a.CustCode
                WHERE a.CompanyCode IS NULL AND c.CustCode IS NULL)
            BEGIN
                RAISERROR(N'SaCustAdd backfill aborted: orphan address rows.', 16, 1);
            END

            UPDATE a
            SET a.CompanyCode = c.CompanyCode
            FROM dbo.SaCustAdd a
            INNER JOIN dbo.SaCust c ON c.CustCode = a.CustCode
            WHERE a.CompanyCode IS NULL;

            IF EXISTS (SELECT 1 FROM dbo.SaCustAdd WHERE CompanyCode IS NULL)
                RAISERROR(N'SaCustAdd backfill aborted: NULL CompanyCode remains.', 16, 1);

            ALTER TABLE dbo.SaCustAdd ALTER COLUMN CompanyCode nvarchar(5) NOT NULL;
        END
    END

    -- SaCustContact: create if missing
    IF OBJECT_ID(N'dbo.SaCustContact', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.SaCustContact (
            CompanyCode nvarchar(5) NOT NULL,
            CustCode nvarchar(30) NOT NULL,
            Line int NOT NULL,
            ContactPerson nvarchar(100) NULL,
            Title nvarchar(50) NULL,
            Department nvarchar(50) NULL,
            ContactEmail nvarchar(100) NULL,
            ContactTelp nvarchar(50) NULL,
            ContactFax nvarchar(50) NULL,
            CONSTRAINT PK_SaCustContact PRIMARY KEY (CompanyCode, CustCode, Line)
        );
    END

    -- Indexes (create if missing)
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaCust_Company_CustName' AND object_id = OBJECT_ID(N'dbo.SaCust'))
        CREATE INDEX IX_SaCust_Company_CustName ON dbo.SaCust (CompanyCode, CustName);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaCust_Company_Active' AND object_id = OBJECT_ID(N'dbo.SaCust'))
        CREATE INDEX IX_SaCust_Company_Active ON dbo.SaCust (CompanyCode, Active);

  COMMIT;
    PRINT @KeyOutcome;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT N'MIGRATION_ABORTED';
    THROW;
END CATCH

-- DBA section: PK/FK rebuild and FK trust validation — run separately after data section succeeds.
-- Post-checklist: PK columns, FK enabled+trusted, no orphans, no duplicate (CompanyCode,CustCode,Line).
