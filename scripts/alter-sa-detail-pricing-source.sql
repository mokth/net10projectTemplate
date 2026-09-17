-- Persist the pricing provenance on the four sales document detail tables (plan Phase 2).
-- Manual DBA script — do NOT run at app startup.
-- Target database: same as ConnectionStrings:DefaultConnection.
--
-- Adds, to SaSODetail / SaDODetail / SaInvoiceDetail / SaCDNDetail / SaQTDetail:
--   PricingSource nvarchar(40) NULL  -- the persisted SaPriceSourceTokens value, e.g. CUSTOMER_ITEM
--   PricingRef    nvarchar(60) NULL  -- readable reference: a list code, MOQ=100, QTY 10-99
--
-- BOTH columns are nullable and carry NO default, so this script cannot change an existing
-- document. Historical lines keep NULL provenance, which the UI must render as "not recorded"
-- rather than as "no price source". The recorded price itself was, and remains, authoritative.
--
-- Unlike alter-company-sales-price-method.sql this script needs NO dynamic SQL: it only ADDs
-- columns and never references them in the same batch. See that script's header for why a static
-- reference to a just-added column fails to compile.
--
-- Safe to run twice: every ADD is guarded by COL_LENGTH.

SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRAN;

    IF OBJECT_ID(N'dbo.SaSODetail', N'U') IS NOT NULL
       AND COL_LENGTH(N'dbo.SaSODetail', N'PricingSource') IS NULL
    BEGIN
        ALTER TABLE dbo.SaSODetail ADD PricingSource nvarchar(40) NULL;
        ALTER TABLE dbo.SaSODetail ADD PricingRef nvarchar(60) NULL;
    END

    IF OBJECT_ID(N'dbo.SaDODetail', N'U') IS NOT NULL
       AND COL_LENGTH(N'dbo.SaDODetail', N'PricingSource') IS NULL
    BEGIN
        ALTER TABLE dbo.SaDODetail ADD PricingSource nvarchar(40) NULL;
        ALTER TABLE dbo.SaDODetail ADD PricingRef nvarchar(60) NULL;
    END

    IF OBJECT_ID(N'dbo.SaInvoiceDetail', N'U') IS NOT NULL
       AND COL_LENGTH(N'dbo.SaInvoiceDetail', N'PricingSource') IS NULL
    BEGIN
        ALTER TABLE dbo.SaInvoiceDetail ADD PricingSource nvarchar(40) NULL;
        ALTER TABLE dbo.SaInvoiceDetail ADD PricingRef nvarchar(60) NULL;
    END

    IF OBJECT_ID(N'dbo.SaCDNDetail', N'U') IS NOT NULL
       AND COL_LENGTH(N'dbo.SaCDNDetail', N'PricingSource') IS NULL
    BEGIN
        ALTER TABLE dbo.SaCDNDetail ADD PricingSource nvarchar(40) NULL;
        ALTER TABLE dbo.SaCDNDetail ADD PricingRef nvarchar(60) NULL;
    END

    -- SaQTDetail is normally created WITH these columns by create-saqt.sql. This guard only exists
    -- for a database that ran an EARLIER version of that script, and is a no-op otherwise.
    IF OBJECT_ID(N'dbo.SaQTDetail', N'U') IS NOT NULL
       AND COL_LENGTH(N'dbo.SaQTDetail', N'PricingSource') IS NULL
    BEGIN
        ALTER TABLE dbo.SaQTDetail ADD PricingSource nvarchar(40) NULL;
        ALTER TABLE dbo.SaQTDetail ADD PricingRef nvarchar(60) NULL;
    END

    COMMIT;
    PRINT N'SA_DETAIL_PRICING_SOURCE_APPLIED';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT N'MIGRATION_ABORTED';
    THROW;
END CATCH
