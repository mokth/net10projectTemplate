-- Patch: add POCDNDetail PO link columns (procurement plan v2.3 Phase 3).
-- Safe to re-run. Usage: sqlcmd -S .\SQLEXPRESS -d ERPWeb -E -i scripts\alter-pocdndetail-po-link.sql
GO

IF OBJECT_ID(N'dbo.POCDNDetail', N'U') IS NULL
    RAISERROR(N'POCDNDetail missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.POCDNDetail', N'PONo') IS NULL
BEGIN
    ALTER TABLE dbo.POCDNDetail ADD PONo nvarchar(30) NULL;
    PRINT N'Added POCDNDetail.PONo';
END
ELSE
    PRINT N'POCDNDetail.PONo already exists';
GO

IF COL_LENGTH(N'dbo.POCDNDetail', N'PORelNo') IS NULL
BEGIN
    ALTER TABLE dbo.POCDNDetail ADD PORelNo smallint NULL;
    PRINT N'Added POCDNDetail.PORelNo';
END
ELSE
    PRINT N'POCDNDetail.PORelNo already exists';
GO

IF COL_LENGTH(N'dbo.POCDNDetail', N'POLineNo') IS NULL
BEGIN
    ALTER TABLE dbo.POCDNDetail ADD POLineNo smallint NULL;
    PRINT N'Added POCDNDetail.POLineNo';
END
ELSE
    PRINT N'POCDNDetail.POLineNo already exists';
GO

IF OBJECT_ID(N'dbo.POCDNDetail', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POCDNDetail_PO_Link' AND object_id = OBJECT_ID(N'dbo.POCDNDetail'))
    CREATE INDEX IX_POCDNDetail_PO_Link ON dbo.POCDNDetail (CompanyCode, BranchCode, PONo, PORelNo, POLineNo);
GO
