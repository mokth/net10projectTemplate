-- Additive invoice post snapshots + line Classification (manual DBA script — do NOT run at app startup).
-- Target database: same as ConnectionStrings:DefaultConnection.

SET XACT_ABORT ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SaInvoice', N'U') IS NULL
BEGIN
    RAISERROR(N'SaInvoice table does not exist.', 16, 1);
    RETURN;
END

IF OBJECT_ID(N'dbo.SaInvoiceDetail', N'U') IS NULL
BEGIN
    RAISERROR(N'SaInvoiceDetail table does not exist.', 16, 1);
    RETURN;
END

BEGIN TRY
    BEGIN TRAN;

    IF COL_LENGTH(N'dbo.SaInvoice', N'DueDate') IS NULL
        ALTER TABLE dbo.SaInvoice ADD DueDate datetime2 NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'ArGlCode') IS NULL
        ALTER TABLE dbo.SaInvoice ADD ArGlCode nvarchar(20) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'InvEmail') IS NULL
        ALTER TABLE dbo.SaInvoice ADD InvEmail nvarchar(100) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'BuyerTin') IS NULL
        ALTER TABLE dbo.SaInvoice ADD BuyerTin nvarchar(20) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'BuyerBrn') IS NULL
        ALTER TABLE dbo.SaInvoice ADD BuyerBrn nvarchar(50) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'BuyerRegType') IS NULL
        ALTER TABLE dbo.SaInvoice ADD BuyerRegType nvarchar(20) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'GstregNo') IS NULL
        ALTER TABLE dbo.SaInvoice ADD GstregNo nvarchar(50) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'CustType') IS NULL
        ALTER TABLE dbo.SaInvoice ADD CustType nvarchar(20) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'CustGroupCode') IS NULL
        ALTER TABLE dbo.SaInvoice ADD CustGroupCode nvarchar(20) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'AreaCode') IS NULL
        ALTER TABLE dbo.SaInvoice ADD AreaCode nvarchar(20) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'IndustryCode') IS NULL
        ALTER TABLE dbo.SaInvoice ADD IndustryCode nvarchar(20) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'ChannelCode') IS NULL
        ALTER TABLE dbo.SaInvoice ADD ChannelCode nvarchar(20) NULL;

    IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'Classification') IS NULL
        ALTER TABLE dbo.SaInvoiceDetail ADD Classification nvarchar(50) NULL;

    COMMIT;
    PRINT N'SAINVOICE_POST_SNAPSHOTS_APPLIED';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT N'MIGRATION_ABORTED';
    THROW;
END CATCH
