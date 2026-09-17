/* ============================================================================
   Phase 3 — IvCustPrice: surrogate key + validity window + quantity band + currency
   ----------------------------------------------------------------------------
   Adds, to the price-list LINE table:
       Id            int IDENTITY(1,1)   -> NEW primary key (replaces the 4-part PK)
       ValidFrom     date NOT NULL       default '1900-01-01'  (sentinel = "always")
       ValidTo       date NULL           (open-ended)
       MinQty        decimal(18,4) NOT NULL default 0          (0 = any quantity)
       MaxQty        decimal(18,4) NULL  (unlimited)
       CurrencyCode  nvarchar(5) NULL    (blank = company base currency)

   WHY A SURROGATE KEY (plan 3.2)
   -----------------------------
   The natural key CANNOT gain ValidFrom and stay unique: two quantity bands legitimately start on the
   SAME ValidFrom (a MinQty 1 tier and a MinQty 10 tier both effective 2026-09-01), so a PK of
   (CompanyCode, CustPriceCode, ICode, UOM, ValidFrom) would collide and make tiered pricing
   impossible. The natural key therefore stays a UNIQUE INDEX, not the primary key. This mirrors the
   shipped SaDisGroupItem, which solved the same shape of problem the same way.

   WHY ValidFrom / MinQty ARE NOT NULL
   ----------------------------------
   SQL Server treats NULLs as EQUAL inside a unique index, so a nullable ValidFrom would admit only ONE
   undated row per key. The 1900-01-01 sentinel and MinQty 0 exist so the unique index can contain them.
   ValidTo, MaxQty stay nullable and OUT of the index so a tier or a promotion can be edited without
   key churn.

   WHY CurrencyCode IS IN THE UNIQUE INDEX (deviates from the letter of plan 3.2)
   -----------------------------------------------------------------------------
   Plan 3.2 says "CurrencyCode stays OUT of the unique index", but plan 3.4 says "a MYR tier and a USD
   tier over the same band may coexist, because the resolver filters by currency before ranking". Those
   two statements cannot both hold. Coexistence is the capability the shipped resolver was designed
   for (it ranks an explicit currency match above a blank base-currency line and BLOCKS when only
   another currency exists), so currency is part of the natural key here. The alternative would
   silently make mixed-currency price lists illegal.

   MIGRATION SAFETY
   ----------------
   Safe on a POPULATED table: every pre-existing row maps to ValidFrom = '1900-01-01' and MinQty = 0,
   so no two migrated rows for the same key can overlap — the unique index admits exactly one of them.
   Adding a NOT NULL column WITH a default backfills existing rows, and adding an IDENTITY column to a
   table that already has rows is a supported ALTER in SQL Server.

   BATCHING (the expensive lesson from alter-company-sales-price-method.sql)
   -------------------------------------------------------------------------
   SQL Server compiles a WHOLE batch before executing any of it, so a batch that adds a column and then
   references that column fails with "Invalid column name". Every step that USES a new column is
   therefore in its OWN batch below. Note the contrast with alter-sa-detail-pricing-source.sql and
   alter-sacustgroup-pricelist.sql, which only ever ADD and need no such separation.

   Idempotent: every step is guarded, so a second run is a clean no-op.

   APPLY TWICE on a scratch database (second run must be a clean no-op) before touching dev.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* The verification query below uses FOR XML PATH, which requires QUOTED_IDENTIFIER ON. SSMS defaults
   it ON, but sqlcmd defaults it OFF, so without these the script prints its verification tables and
   THEN fails with "Msg 1934 ... SET options have incorrect settings: 'QUOTED_IDENTIFIER'" - a
   non-zero exit code from a script that actually succeeded. Set them explicitly so the script is
   self-sufficient whichever tool runs it (the equivalent of sqlcmd's -I flag). */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---------------------------------------------------------------------------
   Batch 1 — the new columns. Nothing here references another new column.
   --------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.IvCustPrice', N'U') IS NULL
BEGIN
    PRINT 'IvCustPrice does not exist - run init-sales-item-family.sql first.';
    RETURN;
END
GO

IF COL_LENGTH(N'dbo.IvCustPrice', N'Id') IS NULL
BEGIN
    ALTER TABLE dbo.IvCustPrice ADD Id int IDENTITY(1,1) NOT NULL;
    PRINT 'IvCustPrice.Id added (identity).';
END
ELSE
BEGIN
    PRINT 'IvCustPrice.Id already present - no change.';
END
GO

IF COL_LENGTH(N'dbo.IvCustPrice', N'ValidFrom') IS NULL
BEGIN
    ALTER TABLE dbo.IvCustPrice ADD ValidFrom date NOT NULL
        CONSTRAINT DF_IvCustPrice_ValidFrom DEFAULT ('1900-01-01');
    PRINT 'IvCustPrice.ValidFrom added (NOT NULL, sentinel 1900-01-01).';
END
ELSE
BEGIN
    PRINT 'IvCustPrice.ValidFrom already present - no change.';
END
GO

IF COL_LENGTH(N'dbo.IvCustPrice', N'ValidTo') IS NULL
BEGIN
    ALTER TABLE dbo.IvCustPrice ADD ValidTo date NULL;
    PRINT 'IvCustPrice.ValidTo added.';
END
ELSE
BEGIN
    PRINT 'IvCustPrice.ValidTo already present - no change.';
END
GO

IF COL_LENGTH(N'dbo.IvCustPrice', N'MinQty') IS NULL
BEGIN
    ALTER TABLE dbo.IvCustPrice ADD MinQty decimal(18,4) NOT NULL
        CONSTRAINT DF_IvCustPrice_MinQty DEFAULT (0);
    PRINT 'IvCustPrice.MinQty added (NOT NULL, default 0).';
END
ELSE
BEGIN
    PRINT 'IvCustPrice.MinQty already present - no change.';
END
GO

IF COL_LENGTH(N'dbo.IvCustPrice', N'MaxQty') IS NULL
BEGIN
    ALTER TABLE dbo.IvCustPrice ADD MaxQty decimal(18,4) NULL;
    PRINT 'IvCustPrice.MaxQty added.';
END
ELSE
BEGIN
    PRINT 'IvCustPrice.MaxQty already present - no change.';
END
GO

IF COL_LENGTH(N'dbo.IvCustPrice', N'CurrencyCode') IS NULL
BEGIN
    ALTER TABLE dbo.IvCustPrice ADD CurrencyCode nvarchar(5) NULL;
    PRINT 'IvCustPrice.CurrencyCode added.';
END
ELSE
BEGIN
    PRINT 'IvCustPrice.CurrencyCode already present - no change.';
END
GO

/* Backfill guard: any row that somehow holds NULL in the NOT NULL columns (only possible if the
   columns were added by an earlier, less strict script) is normalised to the sentinel. A fresh
   ALTER already backfilled them, so this is a no-op in the normal path. It runs in its own batch
   because it REFERENCES the columns added above. */
UPDATE dbo.IvCustPrice SET ValidFrom = '1900-01-01' WHERE ValidFrom IS NULL;
UPDATE dbo.IvCustPrice SET MinQty = 0 WHERE MinQty IS NULL;
GO

/* ---------------------------------------------------------------------------
   Batch 2 — swap the primary key from the 4-part composite to Id.
   This batch REFERENCES Id, which is why it is a separate batch.
   --------------------------------------------------------------------------- */
DECLARE @pk sysname =
    (SELECT kc.name
     FROM sys.key_constraints kc
     WHERE kc.parent_object_id = OBJECT_ID(N'dbo.IvCustPrice')
       AND kc.type = N'PK');

IF @pk IS NULL
BEGIN
    PRINT 'IvCustPrice has no primary key - unexpected, inspect the table.';
END
ELSE IF @pk = N'PK_IvCustPrice_Id'
BEGIN
    PRINT 'IvCustPrice primary key is already on Id - no change.';
END
ELSE
BEGIN
    /* Dropping the clustered composite PK turns the table into a heap; the new clustered PK on Id
       re-establishes clustering. All existing rows keep their values and gain sequential Ids. */
    EXEC(N'ALTER TABLE dbo.IvCustPrice DROP CONSTRAINT ' + @pk + N';');
    PRINT 'Dropped old primary key ' + @pk + '.';
END
GO

IF OBJECT_ID(N'PK_IvCustPrice_Id', N'PK') IS NULL
   AND NOT EXISTS (SELECT 1 FROM sys.key_constraints
                   WHERE parent_object_id = OBJECT_ID(N'dbo.IvCustPrice') AND type = N'PK')
BEGIN
    ALTER TABLE dbo.IvCustPrice
        ADD CONSTRAINT PK_IvCustPrice_Id PRIMARY KEY CLUSTERED (Id);
    PRINT 'IvCustPrice primary key created on Id.';
END
ELSE
BEGIN
    PRINT 'IvCustPrice primary key already present - no change.';
END
GO

/* ---------------------------------------------------------------------------
   Batch 3 — the natural key as a UNIQUE index (currency included: see the header),
   plus the resolver's query index and the currency-covering index.
   --------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.IvCustPrice')
                 AND name = N'UX_IvCustPrice_BusinessKey')
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_IvCustPrice_BusinessKey
        ON dbo.IvCustPrice (CompanyCode, CustPriceCode, ICode, UOM, ValidFrom, MinQty, CurrencyCode);
    PRINT 'UX_IvCustPrice_BusinessKey created.';
END
ELSE
BEGIN
    PRINT 'UX_IvCustPrice_BusinessKey already present - no change.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.IvCustPrice')
                 AND name = N'IX_IvCustPrice_Resolve')
BEGIN
    CREATE NONCLUSTERED INDEX IX_IvCustPrice_Resolve
        ON dbo.IvCustPrice (CompanyCode, CustPriceCode, ICode, UOM, ValidFrom, ValidTo);
    PRINT 'IX_IvCustPrice_Resolve created.';
END
ELSE
BEGIN
    PRINT 'IX_IvCustPrice_Resolve already present - no change.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.IvCustPrice')
                 AND name = N'IX_IvCustPrice_Currency')
BEGIN
    CREATE NONCLUSTERED INDEX IX_IvCustPrice_Currency
        ON dbo.IvCustPrice (CompanyCode, CustPriceCode, ICode, UOM, CurrencyCode);
    PRINT 'IX_IvCustPrice_Currency created.';
END
ELSE
BEGIN
    PRINT 'IX_IvCustPrice_Currency already present - no change.';
END
GO

/* ---------------------------------------------------------------------------
   Verification — shape, key and indexes.
   --------------------------------------------------------------------------- */
SELECT
    ColumnName = c.name,
    TypeName   = t.name,
    MaxLength  = c.max_length,
    IsNullable = c.is_nullable,
    IsIdentity = c.is_identity
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.IvCustPrice')
  AND c.name IN (N'Id', N'ValidFrom', N'ValidTo', N'MinQty', N'MaxQty', N'CurrencyCode')
ORDER BY c.column_id;
GO

SELECT
    ConstraintName = kc.name,
    Columns = STUFF((
        SELECT N', ' + col.name
        FROM sys.index_columns ic
        JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
        WHERE ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
        ORDER BY ic.key_ordinal
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'')
FROM sys.key_constraints kc
WHERE kc.parent_object_id = OBJECT_ID(N'dbo.IvCustPrice') AND kc.type = N'PK';
GO

SELECT IndexName = i.name, IsUnique = i.is_unique
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID(N'dbo.IvCustPrice')
  AND i.name IN (N'UX_IvCustPrice_BusinessKey', N'IX_IvCustPrice_Resolve', N'IX_IvCustPrice_Currency')
ORDER BY i.name;
GO

SELECT MigratedRows = COUNT(*), AllAlwaysValid = MIN(CASE WHEN ValidFrom = '1900-01-01' THEN 1 ELSE 0 END)
FROM dbo.IvCustPrice;
GO
