-- Additive LHDN e-Invoice outcome discriminator + supplier profile (manual DBA script - do NOT run at app startup).
-- Target database: same as ConnectionStrings:DefaultConnection.
--
-- 1) SaCDN.IRBMOutcome - matches SaInvoice.IRBMOutcome. When IRBMStatus = FAILED it records whether
--    the failure is ConfirmedFailure (safe to retry) or Unknown (recover from MyInvois first).
-- 2) Company e-Invoice supplier profile - NON-SECRET fields only. The client id/secret and the
--    certificate password must come from configuration/user-secrets, never from the database.
--
-- Safe to run twice: every ADD is guarded by COL_LENGTH.

SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SaCDN', N'U') IS NULL
BEGIN
    RAISERROR(N'SaCDN table does not exist.', 16, 1);
    RETURN;
END

IF OBJECT_ID(N'dbo.Company', N'U') IS NULL
BEGIN
    RAISERROR(N'Company table does not exist.', 16, 1);
    RETURN;
END

BEGIN TRY
    BEGIN TRAN;

    IF COL_LENGTH(N'dbo.SaCDN', N'IRBMOutcome') IS NULL
        ALTER TABLE dbo.SaCDN ADD IRBMOutcome nvarchar(30) NULL;

    IF COL_LENGTH(N'dbo.Company', N'EInvEnabled') IS NULL
        ALTER TABLE dbo.Company ADD EInvEnabled bit NOT NULL CONSTRAINT DF_Company_EInvEnabled DEFAULT (0);

    IF COL_LENGTH(N'dbo.Company', N'EInvMSICCode') IS NULL
        ALTER TABLE dbo.Company ADD EInvMSICCode nvarchar(20) NULL;

    IF COL_LENGTH(N'dbo.Company', N'EInvBizDescription') IS NULL
        ALTER TABLE dbo.Company ADD EInvBizDescription nvarchar(300) NULL;

    IF COL_LENGTH(N'dbo.Company', N'EInvSstNo') IS NULL
        ALTER TABLE dbo.Company ADD EInvSstNo nvarchar(50) NULL;

    IF COL_LENGTH(N'dbo.Company', N'EInvRegType') IS NULL
        ALTER TABLE dbo.Company ADD EInvRegType nvarchar(20) NULL;

    IF COL_LENGTH(N'dbo.Company', N'EInvStateCode') IS NULL
        ALTER TABLE dbo.Company ADD EInvStateCode nvarchar(10) NULL;

    IF COL_LENGTH(N'dbo.Company', N'EInvCountryCode') IS NULL
        ALTER TABLE dbo.Company ADD EInvCountryCode nvarchar(10) NULL;

    IF COL_LENGTH(N'dbo.Company', N'EInvOnBehalfTin') IS NULL
        ALTER TABLE dbo.Company ADD EInvOnBehalfTin nvarchar(50) NULL;

    IF COL_LENGTH(N'dbo.Company', N'EInvDocumentVersion') IS NULL
        ALTER TABLE dbo.Company ADD EInvDocumentVersion nvarchar(10) NULL;

    COMMIT;
    PRINT N'EINVOICE_OUTCOME_AND_COMPANY_PROFILE_APPLIED';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT N'MIGRATION_ABORTED';
    THROW;
END CATCH
GO
