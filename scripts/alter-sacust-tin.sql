-- Additive customer TinNo for e-invoice buyer ID snapshots (manual DBA script — do NOT run at app startup).
-- Target database: same as ConnectionStrings:DefaultConnection.

SET XACT_ABORT ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SaCust', N'U') IS NULL
BEGIN
    RAISERROR(N'SaCust table does not exist.', 16, 1);
    RETURN;
END

BEGIN TRY
    BEGIN TRAN;

    IF COL_LENGTH(N'dbo.SaCust', N'TinNo') IS NULL
        ALTER TABLE dbo.SaCust ADD TinNo nvarchar(20) NULL;

    COMMIT;
    PRINT N'SACUST_TINNO_APPLIED';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT N'MIGRATION_ABORTED';
    THROW;
END CATCH
