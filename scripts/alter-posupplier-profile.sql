-- Patch: add POSupplier.GlCode for Purchase Supplier Profile.
-- Additive only. Safe to re-run.
-- Usage: sqlcmd -S .\SQLEXPRESS -d ERPWeb -E -i scripts\alter-posupplier-profile.sql
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.POSupplier', N'U') IS NULL
        RAISERROR(N'POSupplier missing.', 16, 1);

    IF COL_LENGTH(N'dbo.POSupplier', N'GlCode') IS NULL
    BEGIN
        ALTER TABLE dbo.POSupplier ADD GlCode nvarchar(20) NULL;
        PRINT N'POSUPPLIER_GLCODE_APPLIED';
    END
    ELSE
        PRINT N'POSUPPLIER_GLCODE_PRESENT';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH
GO
