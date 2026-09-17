/* ============================================================================================
   Sales Item Family — Phase 1 schema (scripts/init-sales-item-family.sql)

   Plan:      plans/sales-item-family-v2-plan.md  (§13 DDL strategy, §13.2 migration cases, §13.4 schema table)
   Findings:  ErpWeb/docs/sales-item-family-phase0-findings.md  (live facts, 2026-09-15, PASS)

   ADDITIVE + IDEMPOTENT + DBA-RUN. Never executed at application startup.

   Live reality this script is written against (verified 2026-09-15 on ERPWeb):
     - IvCustPriceGroup, IvCustPrice, SaDisGroupItem  -> ABSENT  => created here
     - SaItemCust                                     -> EXISTS (0 rows), PK (ICode, CustCode, SellingUOM, MOQ)
                                                         without CompanyCode and with CompanyCode NULLABLE
                                                         and no RowVersion => widened here (migration case
                                                         "table exists, additive change", §8.8 Branch A)
     - widths taken from the LIVE database, not from EF config:
         ICode 20, UOM 5 (= MsUOM.UOMCode), IClass 10 (= IvClass.IClassCode),
         CustPriceCode 20 (= SaCust.CustPriceCode), money decimal(18,4) (= IvStockMaster.SellingPrice)

   STOP semantics: this script THROWs (and leaves the database untouched) when a widening cannot be
   applied safely — rows with a blank CompanyCode, or duplicate business keys. It never silently alters
   or drops data. Anything it throws about is DBA remediation, not an application concern.

   Rollback: additive only; there is no auto-revert. See plan §25 — rollback is a menu/app action, and
   any destructive reversal is a separate, explicitly approved DBA script.
   ============================================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
/* The verification result sets use XML methods (FOR XML ... .value()), which require
   QUOTED_IDENTIFIER ON. sqlcmd defaults it OFF, so it is set explicitly here. */
SET QUOTED_IDENTIFIER ON;
GO

/* --------------------------------------------------------------------------------------------
   1. IvCustPriceGroup — price list header (ABSENT -> created)
   -------------------------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.IvCustPriceGroup', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IvCustPriceGroup
    (
        CompanyCode     nvarchar(5)   NOT NULL,
        CustPriceCode   nvarchar(20)  NOT NULL,
        CustPriceDesc   nvarchar(50)  NULL,
        Active          bit           NOT NULL CONSTRAINT DF_IvCustPriceGroup_Active DEFAULT (1),
        Created         datetime      NULL,
        UserID          nvarchar(10)  NULL,
        Updated         datetime      NULL,
        UpdatedUID      nvarchar(10)  NULL,
        BranchCode      nvarchar(5)   NULL,
        LocationCode    nvarchar(10)  NULL,
        RowVersion      rowversion    NOT NULL,
        CONSTRAINT PK_IvCustPriceGroup PRIMARY KEY CLUSTERED (CompanyCode, CustPriceCode)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.IvCustPriceGroup')
                 AND name = N'IX_IvCustPriceGroup_Company_Active')
    CREATE NONCLUSTERED INDEX IX_IvCustPriceGroup_Company_Active
        ON dbo.IvCustPriceGroup (CompanyCode, Active);
GO

/* --------------------------------------------------------------------------------------------
   2. IvCustPrice — item price inside a price list (ABSENT -> created)
      Child of the header: no RowVersion (the header version is the concurrency gate, plan §10).
   -------------------------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.IvCustPrice', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IvCustPrice
    (
        /* Phase 3: SURROGATE primary key. The natural key CANNOT be the PK because two quantity bands
           legitimately start on the SAME ValidFrom (a MinQty 1 tier and a MinQty 10 tier both effective
           2026-09-01), so a PK containing ValidFrom would make tiered pricing impossible. The natural key
           is a separate UNIQUE index below — the same shape SaDisGroupItem uses in this same script. */
        Id              int            IDENTITY(1,1) NOT NULL,
        CompanyCode     nvarchar(5)    NOT NULL,
        CustPriceCode   nvarchar(20)   NOT NULL,
        ICode           nvarchar(20)   NOT NULL,
        UOM             nvarchar(5)    NOT NULL,
        IDesc           nvarchar(200)  NULL,
        CustPriceDesc   nvarchar(50)   NULL,
        SellingPrice    decimal(18,4)  NULL,
        SellPackSize    decimal(18,4)  NULL,
        /* Phase 3: NOT NULL so the natural unique index can CONTAIN them. SQL Server treats NULLs as
           EQUAL inside a unique index, so a nullable ValidFrom/MinQty would admit only ONE undated or
           unbanded row per key. The 1900-01-01 sentinel means "always"; MinQty 0 means "any quantity". */
        ValidFrom       date           NOT NULL CONSTRAINT DF_IvCustPrice_ValidFrom DEFAULT ('1900-01-01'),
        ValidTo         date           NULL,
        MinQty          decimal(18,4)  NOT NULL CONSTRAINT DF_IvCustPrice_MinQty DEFAULT (0),
        MaxQty          decimal(18,4)  NULL,
        CurrencyCode    nvarchar(5)    NULL,
        Created         datetime       NULL,
        UserID          nvarchar(10)   NULL,
        Updated         datetime       NULL,
        UpdatedUID      nvarchar(10)   NULL,
        BranchCode      nvarchar(5)    NULL,
        LocationCode    nvarchar(10)   NULL,
        CONSTRAINT PK_IvCustPrice_Id PRIMARY KEY CLUSTERED (Id)
    );

    /* The natural key: exactly ONE row per (list, item, UOM, effective-from, band-floor, CURRENCY).
       CurrencyCode IS included, which deviates from the letter of plan 3.2 ("CurrencyCode stays OUT of
       the unique index"). It has to be: plan 3.4 requires a MYR tier and a USD tier over the SAME band
       to coexist, and that is impossible if the index forbids two rows on one band. ValidTo and MaxQty
       stay OUT so a tier or a promotion can be edited without key churn. */
    CREATE UNIQUE NONCLUSTERED INDEX UX_IvCustPrice_BusinessKey
        ON dbo.IvCustPrice (CompanyCode, CustPriceCode, ICode, UOM, ValidFrom, MinQty, CurrencyCode);

    /* The resolver filters list + item + UOM and then by the window. */
    CREATE NONCLUSTERED INDEX IX_IvCustPrice_Resolve
        ON dbo.IvCustPrice (CompanyCode, CustPriceCode, ICode, UOM, ValidFrom, ValidTo);

    /* Currency is filtered after the window/band filters. */
    CREATE NONCLUSTERED INDEX IX_IvCustPrice_Currency
        ON dbo.IvCustPrice (CompanyCode, CustPriceCode, ICode, UOM, CurrencyCode);

    PRINT N'IvCustPrice created WITH the Phase 3 shape (surrogate Id PK, validity window, quantity band, currency).';
END
ELSE
BEGIN
    /* The table already exists. A database created BEFORE Phase 3 still holds the old 4-part composite
       PK and none of the new columns, and the EF model expects them — so say so LOUDLY rather than
       leaving a silently divergent schema that fails at runtime. */
    IF COL_LENGTH(N'dbo.IvCustPrice', N'ValidFrom') IS NULL
        PRINT N'STOP: IvCustPrice exists WITHOUT the Phase 3 columns. Run scripts/alter-ivcustprice-phase3.sql before using the pricing screens.';
    ELSE
        PRINT N'IvCustPrice already present with the Phase 3 shape - no change.';
END
GO

/* --------------------------------------------------------------------------------------------
   3. SaItemCust — EXISTS: add the missing tenant/integrity pieces, widen the key (Branch A)

      Split into two batches on purpose (3a / 3b): under SET XACT_ABORT ON a failure in a later
      statement of the same batch rolls the earlier ones back, which would make a re-run unable to
      make progress. Each step below is independently re-runnable and leaves a valid state.

      3a  CompanyCode must be NOT NULL before it can join the PK  -> abort on blank values
      3b  the widened key must be unique                          -> abort on duplicates
          then drop/recreate the PK with CompanyCode first, and add RowVersion
   -------------------------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.SaItemCust', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.SaItemCust') AND name = N'CompanyCode' AND is_nullable = 1)
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.SaItemCust
                   WHERE CompanyCode IS NULL OR LTRIM(RTRIM(CompanyCode)) = N'')
            THROW 51000, 'STOP: SaItemCust has rows with a blank CompanyCode. Backfill or delete them (DBA decision), then re-run. Refusing to make CompanyCode NOT NULL.', 1;

        ALTER TABLE dbo.SaItemCust ALTER COLUMN CompanyCode nvarchar(5) NOT NULL;
    END
END
GO

IF OBJECT_ID(N'dbo.SaItemCust', N'U') IS NOT NULL
BEGIN
    /* duplicate check on the widened business key */
    IF EXISTS (SELECT 1 FROM dbo.SaItemCust
               GROUP BY CompanyCode, CustCode, ICode, SellingUOM, MOQ
               HAVING COUNT(*) > 1)
        THROW 51001, 'STOP: SaItemCust has duplicate rows on (CompanyCode, CustCode, ICode, SellingUOM, MOQ). Resolve them (DBA decision), then re-run.', 1;

    /* widen the primary key when it does not already start with CompanyCode */
    IF NOT EXISTS (SELECT 1
                   FROM sys.indexes i
                   JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                   JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE i.object_id = OBJECT_ID(N'dbo.SaItemCust')
                     AND i.is_primary_key = 1
                     AND c.name = N'CompanyCode')
    BEGIN
        DECLARE @pkName sysname, @sql nvarchar(400);

        SELECT @pkName = kc.name
        FROM sys.key_constraints kc
        WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SaItemCust') AND kc.type = N'PK';

        IF @pkName IS NOT NULL
        BEGIN
            SET @sql = N'ALTER TABLE dbo.SaItemCust DROP CONSTRAINT ' + QUOTENAME(@pkName) + N';';
            EXEC sp_executesql @sql;
        END

        ALTER TABLE dbo.SaItemCust
            ADD CONSTRAINT PK_SaItemCust PRIMARY KEY CLUSTERED
                (CompanyCode, CustCode, ICode, SellingUOM, MOQ);
    END

    /* RowVersion (additive) */
    IF COL_LENGTH(N'dbo.SaItemCust', N'RowVersion') IS NULL
        ALTER TABLE dbo.SaItemCust ADD RowVersion rowversion NOT NULL;
END
GO

/* --------------------------------------------------------------------------------------------
   4. SaDisGroupItem — item discount rule (ABSENT -> created)

      Deliberate differences from the legacy designer (plan §6 of the findings):
        - ID is int IDENTITY, never smallint (32,767 ceiling)
        - QtyFr/QtyTo/Discount/Discount1 are decimal(18,4), never float
        - DateFr/DateTo are date, not datetime
        - GroupName / GroupLevel / Discount2 / Discount3 are NOT carried (write-only / never read in legacy)
   -------------------------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.SaDisGroupItem', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDisGroupItem
    (
        ID              int             IDENTITY(1,1) NOT NULL,
        CompanyCode     nvarchar(5)     NOT NULL,
        ICode           nvarchar(20)    NOT NULL,
        IDesc           nvarchar(200)   NULL,
        IClass          nvarchar(10)    NULL,          -- NULL/blank = all classes (IvClass.IClassCode is nvarchar(10))
        QtyFr           decimal(18,4)   NOT NULL,
        QtyTo           decimal(18,4)   NOT NULL,
        DateFr          date            NOT NULL,
        DateTo          date            NULL,          -- NULL = open-ended
        Discount        decimal(18,4)   NULL,
        DiscountType    nvarchar(10)    NULL,          -- PERCENTAGE | AMOUNT
        Discount1       decimal(18,4)   NULL,
        DiscountType1   nvarchar(10)    NULL,          -- PERCENTAGE | AMOUNT
        EffectPrice     nvarchar(10)    NULL,          -- DEALER | SELLING  (stored, inert in this phase)
        GroupStatus     nvarchar(10)    NULL,          -- carried for compatibility, NOT authoritative
        Created         datetime        NULL,
        UserID          nvarchar(20)    NULL,
        Updated         datetime        NULL,
        UpdatedUID      nvarchar(20)    NULL,
        BranchCode      nvarchar(5)     NULL,
        LocationCode    nvarchar(10)    NULL,
        RowVersion      rowversion      NOT NULL,
        CONSTRAINT PK_SaDisGroupItem PRIMARY KEY CLUSTERED (ID)
    );
END
GO

/* Business key: one rule per company/item/class/band/window date (plan §8.8 + §8.3 overlap rule).
   Also serves the runtime match pattern (CompanyCode, ICode, ...). */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.SaDisGroupItem')
                 AND name = N'UX_SaDisGroupItem_BusinessKey')
    CREATE UNIQUE NONCLUSTERED INDEX UX_SaDisGroupItem_BusinessKey
        ON dbo.SaDisGroupItem (CompanyCode, ICode, IClass, QtyFr, QtyTo, DateFr);
GO

/* --------------------------------------------------------------------------------------------
   5. Verification — print the resulting shape so the apply can be diffed against plan §13.4
   -------------------------------------------------------------------------------------------- */
SELECT t.name AS TableName, c.name AS ColumnName, ty.name AS TypeName,
       c.max_length AS Bytes, c.precision AS Prec, c.scale AS Scale, c.is_nullable AS IsNullable
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE t.name IN (N'IvCustPriceGroup', N'IvCustPrice', N'SaItemCust', N'SaDisGroupItem')
ORDER BY t.name, c.column_id;

SELECT OBJECT_NAME(i.object_id) AS TableName, i.name AS IndexName,
       i.is_primary_key AS IsPk, i.is_unique AS IsUnique,
       STUFF((SELECT N',' + c2.name
              FROM sys.index_columns ic2
              JOIN sys.columns c2 ON c2.object_id = ic2.object_id AND c2.column_id = ic2.column_id
              WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 0
              ORDER BY ic2.key_ordinal
              FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N'') AS KeyColumns
FROM sys.indexes i
WHERE OBJECT_NAME(i.object_id) IN (N'IvCustPriceGroup', N'IvCustPrice', N'SaItemCust', N'SaDisGroupItem')
ORDER BY TableName, IndexName;
GO
