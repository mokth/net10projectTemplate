-- Additive tax group TaxGlCode for AR/e-invoice post readiness (manual DBA script — do NOT run at app startup).
-- Target database: same as ConnectionStrings:DefaultConnection.

SET XACT_ABORT ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SaTaxGroup', N'U') IS NULL
BEGIN
    RAISERROR(N'SaTaxGroup table does not exist.', 16, 1);
    RETURN;
END

BEGIN TRY
    BEGIN TRAN;

    IF COL_LENGTH(N'dbo.SaTaxGroup', N'TaxGlCode') IS NULL
        ALTER TABLE dbo.SaTaxGroup ADD TaxGlCode nvarchar(20) NULL;

    COMMIT;
    PRINT N'SATAXGROUP_TAXGLCODE_APPLIED';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT N'MIGRATION_ABORTED';
    THROW;
END CATCH
