USE ERPLiteEx;
GO

IF OBJECT_ID('dbo.Branch', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Branch (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Branch PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        BranchName nvarchar(100) NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_Branch_IsActive DEFAULT (1),
        IsDeleted bit NOT NULL CONSTRAINT DF_Branch_IsDeleted DEFAULT (0),
        DeletedAtUtc datetime2 NULL,
        DeletedBy nvarchar(50) NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_Branch_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_Branch_CompanyId_BranchCode UNIQUE (CompanyId, BranchCode),
        CONSTRAINT FK_Branch_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId)
    );

    CREATE INDEX IX_Branch_Company_Active ON dbo.Branch(CompanyId, IsDeleted, IsActive);
END
GO

-- Seed HQ for every company that lacks it
INSERT INTO dbo.Branch (CompanyId, BranchCode, BranchName, IsActive, IsDeleted, CreatedAtUtc, CreatedBy)
SELECT
    c.CompanyId,
    N'HQ',
    N'Head Office',
    1,
    0,
    SYSUTCDATETIME(),
    N'SEED'
FROM dbo.Company c
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.Branch b
    WHERE b.CompanyId = c.CompanyId
      AND b.BranchCode = N'HQ'
      AND b.IsDeleted = 0);
GO

-- Backfill distinct BranchCode values from userlogin (non-HQ)
INSERT INTO dbo.Branch (CompanyId, BranchCode, BranchName, IsActive, IsDeleted, CreatedAtUtc, CreatedBy)
SELECT DISTINCT
    c.CompanyId,
    UPPER(LTRIM(RTRIM(u.BranchCode))),
    UPPER(LTRIM(RTRIM(u.BranchCode))),
    1,
    0,
    SYSUTCDATETIME(),
    N'SEED'
FROM dbo.userlogin u
INNER JOIN dbo.Company c ON c.CompanyCode = u.CompanyCode
WHERE u.BranchCode IS NOT NULL
  AND LTRIM(RTRIM(u.BranchCode)) <> N''
  AND UPPER(LTRIM(RTRIM(u.BranchCode))) <> N'HQ'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.Branch b
      WHERE b.CompanyId = c.CompanyId
        AND b.BranchCode = UPPER(LTRIM(RTRIM(u.BranchCode)))
        AND b.IsDeleted = 0);
GO
