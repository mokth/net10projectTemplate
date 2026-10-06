-- Archive markers for costing-safe historical delete.
-- Nullable only. Existing rows stay active (DeletedAtUtc remains NULL).
-- Do NOT run at application startup. Apply via normal SQL deploy. Idempotent.
-- Does not add cascading deletes, and does not change posting identities.

SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRAN;

    IF OBJECT_ID(N'dbo.IvTrxBatch', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.IvTrxBatch', N'DeletedAtUtc') IS NULL
            ALTER TABLE dbo.IvTrxBatch ADD DeletedAtUtc datetime2 NULL;
        IF COL_LENGTH(N'dbo.IvTrxBatch', N'DeletedBy') IS NULL
            ALTER TABLE dbo.IvTrxBatch ADD DeletedBy nvarchar(10) NULL;
        IF COL_LENGTH(N'dbo.IvTrxBatch', N'DeleteReason') IS NULL
            ALTER TABLE dbo.IvTrxBatch ADD DeleteReason nvarchar(250) NULL;
    END

    IF OBJECT_ID(N'dbo.SaInvoice', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.SaInvoice', N'DeletedAtUtc') IS NULL
            ALTER TABLE dbo.SaInvoice ADD DeletedAtUtc datetime2 NULL;
        IF COL_LENGTH(N'dbo.SaInvoice', N'DeletedBy') IS NULL
            ALTER TABLE dbo.SaInvoice ADD DeletedBy nvarchar(20) NULL;
        IF COL_LENGTH(N'dbo.SaInvoice', N'DeleteReason') IS NULL
            ALTER TABLE dbo.SaInvoice ADD DeleteReason nvarchar(250) NULL;
    END

    IF OBJECT_ID(N'dbo.SaDO', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.SaDO', N'DeletedAtUtc') IS NULL
            ALTER TABLE dbo.SaDO ADD DeletedAtUtc datetime2 NULL;
        IF COL_LENGTH(N'dbo.SaDO', N'DeletedBy') IS NULL
            ALTER TABLE dbo.SaDO ADD DeletedBy nvarchar(20) NULL;
        IF COL_LENGTH(N'dbo.SaDO', N'DeleteReason') IS NULL
            ALTER TABLE dbo.SaDO ADD DeleteReason nvarchar(250) NULL;
    END

    IF OBJECT_ID(N'dbo.SaCDN', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.SaCDN', N'DeletedAtUtc') IS NULL
            ALTER TABLE dbo.SaCDN ADD DeletedAtUtc datetime2 NULL;
        IF COL_LENGTH(N'dbo.SaCDN', N'DeletedBy') IS NULL
            ALTER TABLE dbo.SaCDN ADD DeletedBy nvarchar(20) NULL;
        IF COL_LENGTH(N'dbo.SaCDN', N'DeleteReason') IS NULL
            ALTER TABLE dbo.SaCDN ADD DeleteReason nvarchar(250) NULL;
    END

    IF OBJECT_ID(N'dbo.POInvoice', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.POInvoice', N'DeletedAtUtc') IS NULL
            ALTER TABLE dbo.POInvoice ADD DeletedAtUtc datetime2 NULL;
        IF COL_LENGTH(N'dbo.POInvoice', N'DeletedBy') IS NULL
            ALTER TABLE dbo.POInvoice ADD DeletedBy nvarchar(20) NULL;
        IF COL_LENGTH(N'dbo.POInvoice', N'DeleteReason') IS NULL
            ALTER TABLE dbo.POInvoice ADD DeleteReason nvarchar(250) NULL;
    END

    IF OBJECT_ID(N'dbo.PoCdn', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.PoCdn', N'DeletedAtUtc') IS NULL
            ALTER TABLE dbo.PoCdn ADD DeletedAtUtc datetime2 NULL;
        IF COL_LENGTH(N'dbo.PoCdn', N'DeletedBy') IS NULL
            ALTER TABLE dbo.PoCdn ADD DeletedBy nvarchar(20) NULL;
        IF COL_LENGTH(N'dbo.PoCdn', N'DeleteReason') IS NULL
            ALTER TABLE dbo.PoCdn ADD DeleteReason nvarchar(250) NULL;
    END

    IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.PrProductionOutput', N'DeletedAtUtc') IS NULL
            ALTER TABLE dbo.PrProductionOutput ADD DeletedAtUtc datetime2 NULL;
        IF COL_LENGTH(N'dbo.PrProductionOutput', N'DeletedBy') IS NULL
            ALTER TABLE dbo.PrProductionOutput ADD DeletedBy nvarchar(10) NULL;
        IF COL_LENGTH(N'dbo.PrProductionOutput', N'DeleteReason') IS NULL
            ALTER TABLE dbo.PrProductionOutput ADD DeleteReason nvarchar(250) NULL;
    END

    IF OBJECT_ID(N'dbo.PrFinishedGoodReceipt', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.PrFinishedGoodReceipt', N'DeletedAtUtc') IS NULL
            ALTER TABLE dbo.PrFinishedGoodReceipt ADD DeletedAtUtc datetime2 NULL;
        IF COL_LENGTH(N'dbo.PrFinishedGoodReceipt', N'DeletedBy') IS NULL
            ALTER TABLE dbo.PrFinishedGoodReceipt ADD DeletedBy nvarchar(10) NULL;
        IF COL_LENGTH(N'dbo.PrFinishedGoodReceipt', N'DeleteReason') IS NULL
            ALTER TABLE dbo.PrFinishedGoodReceipt ADD DeleteReason nvarchar(250) NULL;
    END

    COMMIT;
    PRINT N'TRANSACTION_DELETE_ARCHIVE_APPLIED';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT N'MIGRATION_ABORTED';
    THROW;
END CATCH
GO
