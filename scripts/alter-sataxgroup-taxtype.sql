-- Additive LHDN tax type on tax groups (manual DBA script — do NOT run at app startup).
-- Target database: same as ConnectionStrings:DefaultConnection.
--
-- SaTaxGroup.TaxType holds the LHDN (MyInvois) tax type used on the e-Invoice line, resolved from the
-- IvMSCode TAX family by SaTaxGroup.TaxGrCode. The backfill is MANDATORY: 06 ("Not Applicable") is the
-- application default for new and blank rows, so existing rows must agree or the e-Invoice payload and
-- the master screen would disagree for the same record.
--
-- Verification (expect 1 row with max_length 4 = nvarchar(2), and 0 NULL/blank rows):
--   SELECT c.name, c.max_length FROM sys.columns c
--   JOIN sys.tables t ON t.object_id = c.object_id
--   WHERE t.name = N'SaTaxGroup' AND c.name = N'TaxType';
--   SELECT COUNT(*) FROM dbo.SaTaxGroup WHERE TaxType IS NULL OR LTRIM(RTRIM(TaxType)) = N'';

SET XACT_ABORT ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SaTaxGroup', N'U') IS NULL
BEGIN
    RAISERROR(N'SaTaxGroup table does not exist.', 16, 1);
    RETURN;
END

BEGIN TRY
    BEGIN TRAN;

    IF COL_LENGTH(N'dbo.SaTaxGroup', N'TaxType') IS NULL
        ALTER TABLE dbo.SaTaxGroup ADD TaxType nvarchar(2) NULL;

    UPDATE dbo.SaTaxGroup
    SET TaxType = N'06'
    WHERE TaxType IS NULL
       OR LTRIM(RTRIM(TaxType)) = N'';

    COMMIT;
    PRINT N'SATAXGROUP_TAXTYPE_APPLIED';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT N'MIGRATION_ABORTED';
    THROW;
END CATCH
