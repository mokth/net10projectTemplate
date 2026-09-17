/* ============================================================================
   Phase 5 — Customer Group default price list
   ----------------------------------------------------------------------------
   Adds SaCustGroup.CustPriceCode nvarchar(20) NULL, matching the live width of
   SaCust.CustPriceCode so a group can point at the same master the customer
   points at.

   Semantics (plan section 5.3):
     - It is a FALLBACK, never a price level of its own. The walk order is
       Customer Item -> Customer List -> Group List -> Item Default, so the
       customer's own CustPriceCode always beats the group's default.
     - NULL / blank means "no default"; the walk simply continues to the item's
       selling price. It is NOT an error.
     - An unknown or retired code also yields no lines (the loader inner-joins
       IvCustPriceGroup on IsActive), so the walk still continues.

   Purely additive: no column is referenced in the same batch that adds it, so
   unlike alter-company-sales-price-method.sql this script needs NO dynamic SQL.

   Idempotent: the COL_LENGTH guard makes a second run a clean no-op.

   Safe to run against a populated table: adding a NULL column is a metadata-only
   change in SQL Server, and every existing row correctly reads as "no default",
   which is exactly the pre-Phase-5 behaviour.

   Apply TWICE on a scratch database (second run must be a clean no-op) before
   touching dev, per the plan's Verification step 3.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF COL_LENGTH(N'dbo.SaCustGroup', N'CustPriceCode') IS NULL
BEGIN
    ALTER TABLE dbo.SaCustGroup ADD CustPriceCode nvarchar(20) NULL;
    PRINT 'SaCustGroup.CustPriceCode added.';
END
ELSE
BEGIN
    PRINT 'SaCustGroup.CustPriceCode already present - no change.';
END
GO

/* Verification — the column must exist and be nullable nvarchar(20). */
SELECT
    ColumnName = c.name,
    TypeName   = t.name,
    MaxLength  = c.max_length,
    IsNullable = c.is_nullable
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.SaCustGroup')
  AND c.name = N'CustPriceCode';
GO

/* Verification — how many groups already carry a default (expected: 0 after a
   fresh migration, because the column is NULL for every existing row). */
SELECT
    GroupsWithADefault = COUNT(CustPriceCode),
    TotalGroups        = COUNT(*)
FROM dbo.SaCustGroup;
GO
