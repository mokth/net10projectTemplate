-- Department (MsDept) + Project (MsProject) reference masters.
-- Manual deploy only — do NOT run at app startup.
-- Target: same database as ConnectionStrings:DefaultConnection (ERPWeb).
--
-- Idempotent: IF OBJECT_ID guards for tables, IF COL_LENGTH guards for late columns.
-- Safe to run twice.
--
-- COLUMN NAMING (authoritative): these are brand-new tables, so they use the current
-- convention — IsActive / CreatedDate / CreatedBy / ModifiedDate / ModifiedBy. The EF
-- configurations map 1:1 to these names. Do NOT alias to Active/Created/UserID/UpdatedUID
-- (those aliases exist only on pre-existing tables that predate this convention).
--
-- Code width is load-bearing: DeptCode / ProjCode MUST stay nvarchar(20) to match the
-- transaction columns and the TruncateOptional(..., 20) save paths. A wider master code
-- would silently truncate and break lookup matching.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

-- ── dbo.MsDept ────────────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.MsDept', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MsDept (
        CompanyCode   nvarchar(10)   NOT NULL,
        BranchCode    nvarchar(10)   NOT NULL,
        DeptCode      nvarchar(20)   NOT NULL,
        DeptName      nvarchar(100)  NULL,
        ManagerEmpId  nvarchar(20)   NULL,
        GlCode        nvarchar(20)   NULL,
        IsActive      bit            NOT NULL CONSTRAINT DF_MsDept_IsActive DEFAULT (1),
        Remarks       nvarchar(500)  NULL,
        CreatedDate   datetime2      NULL,
        CreatedBy     nvarchar(20)   NULL,
        ModifiedDate  datetime2      NULL,
        ModifiedBy    nvarchar(20)   NULL,
        RowVersion    rowversion     NOT NULL,
        CONSTRAINT PK_MsDept PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DeptCode)
    );
    PRINT N'Created table dbo.MsDept';
END
ELSE
    PRINT N'Table dbo.MsDept already present.';
GO

IF COL_LENGTH(N'dbo.MsDept', N'GlCode') IS NULL
    ALTER TABLE dbo.MsDept ADD GlCode nvarchar(20) NULL;
GO

IF COL_LENGTH(N'dbo.MsDept', N'IsActive') IS NULL
    ALTER TABLE dbo.MsDept ADD IsActive bit NOT NULL CONSTRAINT DF_MsDept_IsActive DEFAULT (1);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_MsDept_Company_Branch_Active' AND object_id = OBJECT_ID(N'dbo.MsDept'))
    CREATE INDEX IX_MsDept_Company_Branch_Active
    ON dbo.MsDept (CompanyCode, BranchCode, IsActive, DeptCode);
GO

-- ── dbo.MsProject ─────────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.MsProject', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MsProject (
        CompanyCode   nvarchar(10)   NOT NULL,
        BranchCode    nvarchar(10)   NOT NULL,
        ProjCode      nvarchar(20)   NOT NULL,
        ProjName      nvarchar(150)  NULL,
        CustCode      nvarchar(20)   NULL,
        DeptCode      nvarchar(20)   NULL,
        ManagerEmpId  nvarchar(20)   NULL,
        StartDate     date           NULL,
        EndDate       date           NULL,
        CloseDate     date           NULL,
        Status        nvarchar(20)   NOT NULL CONSTRAINT DF_MsProject_Status DEFAULT (N'ACTIVE'),
        BudgetAmnt    decimal(18,4)  NULL,
        Remarks       nvarchar(500)  NULL,
        CreatedDate   datetime2      NULL,
        CreatedBy     nvarchar(20)   NULL,
        ModifiedDate  datetime2      NULL,
        ModifiedBy    nvarchar(20)   NULL,
        RowVersion    rowversion     NOT NULL,
        CONSTRAINT PK_MsProject PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, ProjCode)
    );
    PRINT N'Created table dbo.MsProject';
END
ELSE
    PRINT N'Table dbo.MsProject already present.';
GO

IF COL_LENGTH(N'dbo.MsProject', N'DeptCode') IS NULL
    ALTER TABLE dbo.MsProject ADD DeptCode nvarchar(20) NULL;
GO

IF COL_LENGTH(N'dbo.MsProject', N'Status') IS NULL
    ALTER TABLE dbo.MsProject ADD Status nvarchar(20) NOT NULL CONSTRAINT DF_MsProject_Status DEFAULT (N'ACTIVE');
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_MsProject_Company_Branch_Active' AND object_id = OBJECT_ID(N'dbo.MsProject'))
    CREATE INDEX IX_MsProject_Company_Branch_Active
    ON dbo.MsProject (CompanyCode, BranchCode, Status, ProjCode);
GO

PRINT N'create-ms-dept-project.sql complete.';
GO
