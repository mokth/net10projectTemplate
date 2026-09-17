/* ============================================================================
   Phase 4 — price override governance
   ----------------------------------------------------------------------------
   Adds to each of the four sales document detail tables:
       OriginalUnitPrice  decimal(18,4) NULL  -- the price the ENGINE resolved
       OverrideReason     nvarchar(100) NULL  -- why the operator changed it

   Semantics (plan 4.2/4.4):
     - A persisted row may carry several engine-resolved prices over time, but only ONE of them was
       overridden, so OriginalUnitPrice records the ENGINE price for the line, not a history.
     - NULL OriginalUnitPrice means "never overridden", which is the normal case. That is why the
       columns are nullable and cost nothing until used.
     - OverrideReason is REQUIRED by the service whenever OriginalUnitPrice is set, so an unexplained
       price change cannot reach the ledger.
     - The permission (PRICE_OVERRIDE) is enforced SERVER-SIDE, not only in the UI, because the page
       is not the execution point.

   Purely additive: no column is referenced in the same batch that adds it, so unlike
   alter-company-sales-price-method.sql and alter-ivcustprice-phase3.sql this needs NO dynamic SQL and
   no batch separation. Idempotent via the COL_LENGTH guards.

   SaQTDetail is NOT covered here. It is handled separately by alter-saqt-detail-price-override.sql,
   because it was excluded from the first cut of Phase 4 and added afterwards. A quotation is the first
   place a price is offered, so leaving it ungoverned left the one screen where an override matters most
   with no gate. That script also grants PRICE_OVERRIDE to the SA_QT menu.

   Apply TWICE on a scratch database (second run must be a clean no-op) before touching dev.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF COL_LENGTH(N'dbo.SaSODetail', N'OriginalUnitPrice') IS NULL
BEGIN
    ALTER TABLE dbo.SaSODetail ADD OriginalUnitPrice decimal(18,4) NULL;
    PRINT 'SaSODetail.OriginalUnitPrice added.';
END
ELSE PRINT 'SaSODetail.OriginalUnitPrice already present - no change.';
GO

IF COL_LENGTH(N'dbo.SaSODetail', N'OverrideReason') IS NULL
BEGIN
    ALTER TABLE dbo.SaSODetail ADD OverrideReason nvarchar(100) NULL;
    PRINT 'SaSODetail.OverrideReason added.';
END
ELSE PRINT 'SaSODetail.OverrideReason already present - no change.';
GO

IF COL_LENGTH(N'dbo.SaDODetail', N'OriginalUnitPrice') IS NULL
BEGIN
    ALTER TABLE dbo.SaDODetail ADD OriginalUnitPrice decimal(18,4) NULL;
    PRINT 'SaDODetail.OriginalUnitPrice added.';
END
ELSE PRINT 'SaDODetail.OriginalUnitPrice already present - no change.';
GO

IF COL_LENGTH(N'dbo.SaDODetail', N'OverrideReason') IS NULL
BEGIN
    ALTER TABLE dbo.SaDODetail ADD OverrideReason nvarchar(100) NULL;
    PRINT 'SaDODetail.OverrideReason added.';
END
ELSE PRINT 'SaDODetail.OverrideReason already present - no change.';
GO

IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'OriginalUnitPrice') IS NULL
BEGIN
    ALTER TABLE dbo.SaInvoiceDetail ADD OriginalUnitPrice decimal(18,4) NULL;
    PRINT 'SaInvoiceDetail.OriginalUnitPrice added.';
END
ELSE PRINT 'SaInvoiceDetail.OriginalUnitPrice already present - no change.';
GO

IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'OverrideReason') IS NULL
BEGIN
    ALTER TABLE dbo.SaInvoiceDetail ADD OverrideReason nvarchar(100) NULL;
    PRINT 'SaInvoiceDetail.OverrideReason added.';
END
ELSE PRINT 'SaInvoiceDetail.OverrideReason already present - no change.';
GO

IF COL_LENGTH(N'dbo.SaCDNDetail', N'OriginalUnitPrice') IS NULL
BEGIN
    ALTER TABLE dbo.SaCDNDetail ADD OriginalUnitPrice decimal(18,4) NULL;
    PRINT 'SaCDNDetail.OriginalUnitPrice added.';
END
ELSE PRINT 'SaCDNDetail.OriginalUnitPrice already present - no change.';
GO

IF COL_LENGTH(N'dbo.SaCDNDetail', N'OverrideReason') IS NULL
BEGIN
    ALTER TABLE dbo.SaCDNDetail ADD OverrideReason nvarchar(100) NULL;
    PRINT 'SaCDNDetail.OverrideReason added.';
END
ELSE PRINT 'SaCDNDetail.OverrideReason already present - no change.';
GO

/* Verification — every table must report both columns as nullable, 36 bytes for decimal(18,4)
   (9 bytes in sys.columns terms is the default for decimal(18,4), so the type/length pair is the
   reliable check) and 200 bytes for nvarchar(100). */
SELECT
    TableName  = t.name,
    ColumnName = c.name,
    TypeName   = ty.name,
    MaxLength  = c.max_length,
    IsNullable = c.is_nullable
FROM sys.columns c
JOIN sys.tables t  ON t.object_id = c.object_id
JOIN sys.types  ty ON ty.user_type_id = c.user_type_id
WHERE t.name IN (N'SaSODetail', N'SaDODetail', N'SaInvoiceDetail', N'SaCDNDetail')
  AND c.name IN (N'OriginalUnitPrice', N'OverrideReason')
ORDER BY t.name, c.name;
GO
