-- Additive customer lead-source column (manual DBA script — do NOT run at app startup).
-- Target database: same as ConnectionStrings:DefaultConnection.
--
-- Phase 1 sales analysis joins SaCust.CustSource LIVE, so sales-by-source is current attribution,
-- not a historical snapshot. Apply BEFORE the SOURCE seeds in init-sales-masters.sql.

SET XACT_ABORT ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SaCust', N'U') IS NULL
BEGIN
    RAISERROR(N'SaCust table does not exist.', 16, 1);
    RETURN;
END

BEGIN TRY
    BEGIN TRAN;

    IF COL_LENGTH(N'dbo.SaCust', N'CustSource') IS NULL
        ALTER TABLE dbo.SaCust ADD CustSource nvarchar(20) NULL;

    COMMIT;
    PRINT N'SACUST_CUSTSOURCE_APPLIED';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT N'MIGRATION_ABORTED';
    THROW;
END CATCH
