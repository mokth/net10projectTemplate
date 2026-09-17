-- Additive per-company sales pricing method (manual DBA script — do NOT run at app startup).
-- Target database: same as ConnectionStrings:DefaultConnection.
--
-- ErpWeb.Core.Sales.SaCompanyPriceMethod owns the token list and the mode-to-source map:
--   NULL / blank / unknown = CUSTOMER_ITEM_AND_LIST (the shipped specificity chain),
--   CUSTOMER_ITEM_ONLY | PRICE_LIST_ONLY | ITEM_DEFAULT_ONLY.
--
-- The column is left NULL for every existing company, which means "full chain" — so applying this
-- script changes NO pricing behaviour on its own. An administrator must opt a company into a
-- narrower method on the company screen.
--
-- Safe to run twice: the ADD is guarded, and it is a nullable column with no default, so the
-- existing rows are untouched.
--
-- GOTCHA (cost a run): SQL Server compiles EVERY statement in a batch BEFORE executing any of them,
-- so a static UPDATE referencing the new column fails with "Invalid column name" on the very first
-- run. The normalising UPDATE is therefore issued through sp_executesql, which compiles at execution
-- time, after the ADD has actually happened.

SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.Company', N'U') IS NULL
BEGIN
    RAISERROR(N'Company table does not exist.', 16, 1);
    RETURN;
END

BEGIN TRY
    BEGIN TRAN;

    IF COL_LENGTH(N'dbo.Company', N'SalesPriceMethod') IS NULL
        ALTER TABLE dbo.Company ADD SalesPriceMethod nvarchar(32) NULL;

    -- Normalise anything a previous partial deploy may have written outside the token set.
    -- MUST be dynamic: see the GOTCHA note in the header.
    EXEC sp_executesql N'
        UPDATE dbo.Company
        SET SalesPriceMethod = NULL
        WHERE SalesPriceMethod IS NOT NULL
          AND SalesPriceMethod NOT IN
              (N''CUSTOMER_ITEM_AND_LIST'', N''CUSTOMER_ITEM_ONLY'', N''PRICE_LIST_ONLY'', N''ITEM_DEFAULT_ONLY'');';

    COMMIT;
    PRINT N'COMPANY_SALES_PRICE_METHOD_APPLIED';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT N'MIGRATION_ABORTED';
    THROW;
END CATCH
