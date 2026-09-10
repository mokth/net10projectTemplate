-- Patch: add SoConsumedQty / LinkDo / SO link columns required by SO Phase 1.
-- Safe to re-run. Targets ERPWeb DO + Invoice detail tables.
-- Usage: sqlcmd -S .\SQLEXPRESS -d ERPWeb -E -i scripts\alter-saso-doc-consume-columns.sql
GO

IF OBJECT_ID(N'dbo.SaDODetail', N'U') IS NULL
    RAISERROR(N'SaDODetail missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.SaDODetail', N'SoConsumedQty') IS NULL
BEGIN
    ALTER TABLE dbo.SaDODetail ADD SoConsumedQty decimal(18,4) NOT NULL
        CONSTRAINT DF_SaDODetail_SoConsumedQty DEFAULT (0);
    PRINT N'Added SaDODetail.SoConsumedQty';
END
ELSE
    PRINT N'SaDODetail.SoConsumedQty already exists';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_SaDODetail_SoConsumedQty' AND parent_object_id = OBJECT_ID(N'dbo.SaDODetail'))
    ALTER TABLE dbo.SaDODetail ADD CONSTRAINT CK_SaDODetail_SoConsumedQty CHECK (SoConsumedQty >= 0);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaDODetail_Company_Branch_SoNo_SoLine' AND object_id = OBJECT_ID(N'dbo.SaDODetail'))
    CREATE INDEX IX_SaDODetail_Company_Branch_SoNo_SoLine
    ON dbo.SaDODetail (CompanyCode, BranchCode, SONo, SOLine);
GO

IF OBJECT_ID(N'dbo.SaInvoiceDetail', N'U') IS NULL
    RAISERROR(N'SaInvoiceDetail missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'SONo') IS NULL
    ALTER TABLE dbo.SaInvoiceDetail ADD SONo nvarchar(30) NOT NULL
        CONSTRAINT DF_SaInvoiceDetail_SONo DEFAULT (N'');
GO

IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'SOLine') IS NULL
    ALTER TABLE dbo.SaInvoiceDetail ADD SOLine smallint NULL;
GO

IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'CustRel') IS NULL
    ALTER TABLE dbo.SaInvoiceDetail ADD CustRel smallint NULL;
GO

IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'LinkDo') IS NULL
    ALTER TABLE dbo.SaInvoiceDetail ADD LinkDo bit NOT NULL
        CONSTRAINT DF_SaInvoiceDetail_LinkDo DEFAULT (0);
GO

IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'SoConsumedQty') IS NULL
BEGIN
    ALTER TABLE dbo.SaInvoiceDetail ADD SoConsumedQty decimal(18,4) NOT NULL
        CONSTRAINT DF_SaInvoiceDetail_SoConsumedQty DEFAULT (0);
    PRINT N'Added SaInvoiceDetail.SoConsumedQty';
END
ELSE
    PRINT N'SaInvoiceDetail.SoConsumedQty already exists';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_SaInvoiceDetail_SoConsumedQty' AND parent_object_id = OBJECT_ID(N'dbo.SaInvoiceDetail'))
    ALTER TABLE dbo.SaInvoiceDetail ADD CONSTRAINT CK_SaInvoiceDetail_SoConsumedQty CHECK (SoConsumedQty >= 0);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaInvoiceDetail_Company_Branch_SoNo_SoLine' AND object_id = OBJECT_ID(N'dbo.SaInvoiceDetail'))
    CREATE INDEX IX_SaInvoiceDetail_Company_Branch_SoNo_SoLine
    ON dbo.SaInvoiceDetail (CompanyCode, BranchCode, SONo, SOLine);
GO

PRINT N'alter-saso-doc-consume-columns.sql completed.';
GO
