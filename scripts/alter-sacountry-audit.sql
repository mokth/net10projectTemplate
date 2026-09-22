/* ============================================================================
   SaCountry — audit columns
   ----------------------------------------------------------------------------
   SaCountry was the ONE sales master table that shipped without an audit
   quadruple, so its list view could show no Created / UserID / Updated /
   UpdatedUID values at all.

   This adds the four legacy-named audit columns used by every other sales
   master (see SaCustTypeConfiguration / SaCurrencyConfiguration):

       Created     datetime2 NULL   <- entity CreatedDate
       UserID      nvarchar(20)     <- entity CreatedBy
       Updated     datetime2 NULL   <- entity ModifiedDate
       UpdatedUID  nvarchar(20)     <- entity ModifiedBy

   Purely additive. No statement references a column added in the same batch,
   so (unlike alter-company-sales-price-method.sql) this script needs NO
   dynamic SQL.

   Idempotent: each COL_LENGTH guard makes a second run a clean no-op.

   Safe against a populated table: adding a NULL column is a metadata-only
   change in SQL Server, and existing rows correctly read as "audit not
   recorded" (NULL) rather than as a fabricated timestamp.

   Apply TWICE on a scratch database (the second run must be a clean no-op)
   before touching dev.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.SaCountry') IS NULL
BEGIN
    RAISERROR('dbo.SaCountry does not exist in this database - nothing to alter.', 16, 1);
    RETURN;
END
GO

IF COL_LENGTH(N'dbo.SaCountry', N'Created') IS NULL
BEGIN
    ALTER TABLE dbo.SaCountry ADD [Created] datetime2 NULL;
    PRINT 'SaCountry.Created added.';
END
ELSE
BEGIN
    PRINT 'SaCountry.Created already present - no change.';
END
GO

IF COL_LENGTH(N'dbo.SaCountry', N'UserID') IS NULL
BEGIN
    ALTER TABLE dbo.SaCountry ADD [UserID] nvarchar(20) NULL;
    PRINT 'SaCountry.UserID added.';
END
ELSE
BEGIN
    PRINT 'SaCountry.UserID already present - no change.';
END
GO

IF COL_LENGTH(N'dbo.SaCountry', N'Updated') IS NULL
BEGIN
    ALTER TABLE dbo.SaCountry ADD [Updated] datetime2 NULL;
    PRINT 'SaCountry.Updated added.';
END
ELSE
BEGIN
    PRINT 'SaCountry.Updated already present - no change.';
END
GO

IF COL_LENGTH(N'dbo.SaCountry', N'UpdatedUID') IS NULL
BEGIN
    ALTER TABLE dbo.SaCountry ADD [UpdatedUID] nvarchar(20) NULL;
    PRINT 'SaCountry.UpdatedUID added.';
END
ELSE
BEGIN
    PRINT 'SaCountry.UpdatedUID already present - no change.';
END
GO

/* Verification — all four audit columns must now exist. */
SELECT
    ColumnName = c.name,
    TypeName   = t.name,
    MaxLength  = c.max_length,
    IsNullable = c.is_nullable
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.SaCountry')
  AND c.name IN (N'Created', N'UserID', N'Updated', N'UpdatedUID')
ORDER BY c.name;
GO

/* Verification — how many countries already carry a creator (expected: 0 on a
   freshly migrated table, because the new columns are NULL for every existing
   row). */
SELECT CountriesWithAudit = COUNT([UserID]) FROM dbo.SaCountry;
GO
