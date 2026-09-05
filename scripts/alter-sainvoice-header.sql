-- Sales Invoice commercial/address header columns (manual DBA script — do NOT run at app startup).
-- Target database: ERPWeb (ConnectionStrings:DefaultConnection).
-- Idempotent: IF COL_LENGTH. All new columns nullable, no defaults.
-- Deploy this script BEFORE deploying application code that reads these columns.
-- Backup/rollback: restore from backup. Do not auto-drop columns that may hold production data.
GO

IF OBJECT_ID(N'dbo.SaInvoice', N'U') IS NULL
BEGIN
    RAISERROR(N'SaInvoice table does not exist. Run create-sainvoice.sql first.', 16, 1);
    RETURN;
END
GO

IF COL_LENGTH(N'dbo.SaInvoice', N'InvPrefix') IS NULL ALTER TABLE dbo.SaInvoice ADD InvPrefix nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'PayCode') IS NULL ALTER TABLE dbo.SaInvoice ADD PayCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'TaxGrCode') IS NULL ALTER TABLE dbo.SaInvoice ADD TaxGrCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'SalesmanCode') IS NULL ALTER TABLE dbo.SaInvoice ADD SalesmanCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'PoNo') IS NULL ALTER TABLE dbo.SaInvoice ADD PoNo nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'Remark') IS NULL ALTER TABLE dbo.SaInvoice ADD Remark nvarchar(500) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'CustName') IS NULL ALTER TABLE dbo.SaInvoice ADD CustName nvarchar(200) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvName') IS NULL ALTER TABLE dbo.SaInvoice ADD InvName nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvAddress1') IS NULL ALTER TABLE dbo.SaInvoice ADD InvAddress1 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvAddress2') IS NULL ALTER TABLE dbo.SaInvoice ADD InvAddress2 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvAddress3') IS NULL ALTER TABLE dbo.SaInvoice ADD InvAddress3 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvAddress4') IS NULL ALTER TABLE dbo.SaInvoice ADD InvAddress4 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvCity') IS NULL ALTER TABLE dbo.SaInvoice ADD InvCity nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvState') IS NULL ALTER TABLE dbo.SaInvoice ADD InvState nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvPostalCode') IS NULL ALTER TABLE dbo.SaInvoice ADD InvPostalCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvCountry') IS NULL ALTER TABLE dbo.SaInvoice ADD InvCountry nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvTel') IS NULL ALTER TABLE dbo.SaInvoice ADD InvTel nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvFax') IS NULL ALTER TABLE dbo.SaInvoice ADD InvFax nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipName') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipName nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipAddress1') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipAddress1 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipAddress2') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipAddress2 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipAddress3') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipAddress3 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipCity') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipCity nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipState') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipState nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipPostalCode') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipPostalCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipCountry') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipCountry nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipTel') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipTel nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipFax') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipFax nvarchar(50) NULL;
GO
