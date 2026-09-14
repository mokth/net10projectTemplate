-- Patch: add POInvoiceDetail PO link columns (procurement plan v2.3 Phase 3).
-- Safe to re-run. Usage: sqlcmd -S .\SQLEXPRESS -d ERPWeb -E -i scripts\alter-poinvoicedetail-po-link.sql
GO

IF OBJECT_ID(N'dbo.POInvoiceDetail', N'U') IS NULL
    RAISERROR(N'POInvoiceDetail missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.POInvoiceDetail', N'PONo') IS NULL
BEGIN
    ALTER TABLE dbo.POInvoiceDetail ADD PONo nvarchar(30) NULL;
    PRINT N'Added POInvoiceDetail.PONo';
END
ELSE
    PRINT N'POInvoiceDetail.PONo already exists';
GO

IF COL_LENGTH(N'dbo.POInvoiceDetail', N'PORelNo') IS NULL
BEGIN
    ALTER TABLE dbo.POInvoiceDetail ADD PORelNo smallint NULL;
    PRINT N'Added POInvoiceDetail.PORelNo';
END
ELSE
    PRINT N'POInvoiceDetail.PORelNo already exists';
GO

IF COL_LENGTH(N'dbo.POInvoiceDetail', N'POLineNo') IS NULL
BEGIN
    ALTER TABLE dbo.POInvoiceDetail ADD POLineNo smallint NULL;
    PRINT N'Added POInvoiceDetail.POLineNo';
END
ELSE
    PRINT N'POInvoiceDetail.POLineNo already exists';
GO

IF OBJECT_ID(N'dbo.POInvoiceDetail', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POInvoiceDetail_PO_Link' AND object_id = OBJECT_ID(N'dbo.POInvoiceDetail'))
    CREATE INDEX IX_POInvoiceDetail_PO_Link ON dbo.POInvoiceDetail (CompanyCode, BranchCode, PONo, PORelNo, POLineNo);
GO
