-- Patch: add PODetail.OverRecvQty and PODetail.InvoicedQty (procurement plan v2.3 Phase 1).
-- OverRecvQty / BalanceQty are always recomputed from RecvQty / ReturnQty / PoPurQty.
-- InvoicedQty is written by purchase invoice / credit note posting (Phase 3).
-- Safe to re-run. Usage: sqlcmd -S .\SQLEXPRESS -d ERPWeb -E -i scripts\alter-podetail-overrecv-invoiced.sql
GO

IF OBJECT_ID(N'dbo.PODetail', N'U') IS NULL
    RAISERROR(N'PODetail missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.PODetail', N'OverRecvQty') IS NULL
BEGIN
    ALTER TABLE dbo.PODetail ADD OverRecvQty decimal(18,4) NOT NULL
        CONSTRAINT DF_PODetail_OverRecvQty DEFAULT (0);
    PRINT N'Added PODetail.OverRecvQty';
END
ELSE
    PRINT N'PODetail.OverRecvQty already exists';
GO

IF COL_LENGTH(N'dbo.PODetail', N'InvoicedQty') IS NULL
BEGIN
    ALTER TABLE dbo.PODetail ADD InvoicedQty decimal(18,4) NOT NULL
        CONSTRAINT DF_PODetail_InvoicedQty DEFAULT (0);
    PRINT N'Added PODetail.InvoicedQty';
END
ELSE
    PRINT N'PODetail.InvoicedQty already exists';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_PODetail_OverRecvQty' AND parent_object_id = OBJECT_ID(N'dbo.PODetail'))
    ALTER TABLE dbo.PODetail ADD CONSTRAINT CK_PODetail_OverRecvQty CHECK (OverRecvQty >= 0);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_PODetail_InvoicedQty' AND parent_object_id = OBJECT_ID(N'dbo.PODetail'))
    ALTER TABLE dbo.PODetail ADD CONSTRAINT CK_PODetail_InvoicedQty CHECK (InvoicedQty >= 0);
GO
