-- Additive LHDN e-Invoice state columns on SaInvoice (manual DBA script - do NOT run at app startup).
-- Target database: same as ConnectionStrings:DefaultConnection.
--
-- Column names and spellings mirror dbo.SaCDN (including IRNMCancelOn) so the two sales document
-- tables stay consistent. All columns are nullable with no default, so existing invoices are
-- untouched and remain "never submitted" (IRBMStatus NULL).
--
-- Safe to run twice: every ADD is guarded by COL_LENGTH.

SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SaInvoice', N'U') IS NULL
BEGIN
    RAISERROR(N'SaInvoice table does not exist.', 16, 1);
    RETURN;
END

BEGIN TRY
    BEGIN TRAN;

    IF COL_LENGTH(N'dbo.SaInvoice', N'IRNMCancelOn') IS NULL
        ALTER TABLE dbo.SaInvoice ADD IRNMCancelOn datetime2 NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMSubmitID') IS NULL
        ALTER TABLE dbo.SaInvoice ADD IRBMSubmitID nvarchar(50) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMUUID') IS NULL
        ALTER TABLE dbo.SaInvoice ADD IRBMUUID nvarchar(50) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMORIUUID') IS NULL
        ALTER TABLE dbo.SaInvoice ADD IRBMORIUUID nvarchar(50) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMSentOn') IS NULL
        ALTER TABLE dbo.SaInvoice ADD IRBMSentOn datetime2 NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMValidOn') IS NULL
        ALTER TABLE dbo.SaInvoice ADD IRBMValidOn datetime2 NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMError') IS NULL
        ALTER TABLE dbo.SaInvoice ADD IRBMError nvarchar(500) NULL;

    IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMStatus') IS NULL
        ALTER TABLE dbo.SaInvoice ADD IRBMStatus nvarchar(50) NULL;

    -- When IRBMStatus = FAILED: ConfirmedFailure (safe to retry) or Unknown (recover first).
    IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMOutcome') IS NULL
        ALTER TABLE dbo.SaInvoice ADD IRBMOutcome nvarchar(30) NULL;

    -- Supports the "stuck SUBMITTING" sweep, which looks for rows still SUBMITTING.
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE name = N'IX_SaInvoice_Company_IRBMStatus'
          AND object_id = OBJECT_ID(N'dbo.SaInvoice')
    )
        -- INCLUDE must name the live last-modified column. dbo.SaInvoice is ModifiedDate (NOT the
        -- "Modified" that appears in some legacy notes): naming a missing column here throws AFTER
        -- the ADDs above, and the single TRY/transaction rolls every one of them back.
        CREATE INDEX IX_SaInvoice_Company_IRBMStatus
            ON dbo.SaInvoice (CompanyCode, IRBMStatus)
            INCLUDE (BranchCode, InvNo, IRBMSentOn, IRBMValidOn, ModifiedDate);

    COMMIT;
    PRINT N'SAINVOICE_EINVOICE_APPLIED';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT N'MIGRATION_ABORTED';
    THROW;
END CATCH
GO
