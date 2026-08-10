USE ERPLiteEx;
GO

IF OBJECT_ID('dbo.Company', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Company (
        CompanyId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Company PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        CompanyName nvarchar(100) NOT NULL,
        LegalName nvarchar(150) NULL,
        RegistrationNo nvarchar(50) NULL,
        TaxNo nvarchar(50) NULL,
        Phone nvarchar(30) NULL,
        Fax nvarchar(30) NULL,
        Email nvarchar(100) NULL,
        Website nvarchar(200) NULL,
        Address1 nvarchar(100) NULL,
        Address2 nvarchar(100) NULL,
        Address3 nvarchar(100) NULL,
        City nvarchar(50) NULL,
        State nvarchar(50) NULL,
        PostCode nvarchar(20) NULL,
        Country nvarchar(50) NULL,
        LogoUrl nvarchar(500) NULL,
        CurrencyCode nvarchar(3) NULL,
        TimeZoneId nvarchar(64) NULL,
        FiscalYearStartMonth tinyint NULL,
        IsActive bit NOT NULL CONSTRAINT DF_Company_IsActive DEFAULT (1),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        CONSTRAINT UQ_Company_CompanyCode UNIQUE (CompanyCode)
    );

    CREATE INDEX IX_Company_IsActive ON dbo.Company(IsActive);
END
GO

-- Seed DEMO company (matches seed users / roles)
IF NOT EXISTS (SELECT 1 FROM dbo.Company WHERE CompanyCode = N'DEMO')
BEGIN
    INSERT INTO dbo.Company (
        CompanyCode, CompanyName, LegalName, RegistrationNo, TaxNo,
        Phone, Email, Website,
        Address1, City, State, PostCode, Country,
        CurrencyCode, TimeZoneId, FiscalYearStartMonth,
        IsActive, CreatedDate, CreatedBy)
    VALUES (
        N'DEMO', N'Demo Company', N'Demo Company Sdn. Bhd.', N'202001000001', N'MY-TAX-DEMO',
        N'+60 3-0000 0000', N'info@demo.local', N'https://demo.local',
        N'1 Demo Street', N'Kuala Lumpur', N'WP Kuala Lumpur', N'50000', N'MY',
        N'MYR', N'Asia/Kuala_Lumpur', 1,
        1, SYSUTCDATETIME(), N'SEED');
END
GO

-- Ensure a Company row exists for every distinct CompanyCode on userlogin
INSERT INTO dbo.Company (CompanyCode, CompanyName, IsActive, CreatedDate, CreatedBy, Country, CurrencyCode)
SELECT DISTINCT
    u.CompanyCode,
    u.CompanyCode + N' Company',
    1,
    SYSUTCDATETIME(),
    N'SEED',
    N'MY',
    N'MYR'
FROM dbo.userlogin u
WHERE u.CompanyCode IS NOT NULL
  AND LTRIM(RTRIM(u.CompanyCode)) <> N''
  AND NOT EXISTS (
      SELECT 1 FROM dbo.Company c WHERE c.CompanyCode = u.CompanyCode);
GO
