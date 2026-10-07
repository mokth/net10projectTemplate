-- Preserve the original invoice line on a sales credit/debit note.
-- SaCDNDetail.Line is the credit-note line and may be renumbered.
-- SourceInvLine is the posted invoice line used when ReturnStock builds a customer return.
-- Nullable. Existing rows stay NULL until copied from an invoice. Idempotent.
-- Do NOT run at application startup. Apply via normal SQL deploy.

SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRAN;

    IF OBJECT_ID(N'dbo.SaCDNDetail', N'U') IS NOT NULL
       AND COL_LENGTH(N'dbo.SaCDNDetail', N'SourceInvLine') IS NULL
    BEGIN
        ALTER TABLE dbo.SaCDNDetail ADD SourceInvLine smallint NULL;
    END

    COMMIT TRAN;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRAN;
    THROW;
END CATCH
