/* ============================================================================================
   AdLookupValue — company-scoped configurable lookup master (scripts/alter-ad-lookup-value.sql)

   Plan: plans/productionv2_GENERIC_CONFIGURABLE_LOOKUP_MASTER_FINAL_AI_AGENT_PLAN_BIZ_LOGIC_SAFE_REVIEWED.md
         (§3.2 schema, §6 families, §16 backfill, §24 deployment)

   ADDITIVE + IDEMPOTENT + DBA-RUN. Never executed at application startup.

   What this script does
     1. creates dbo.AdLookupValue only when absent (with rowversion);
     2. creates the two indexes only when absent;
     3. runs a data-quality PREFLIGHT over the five approved free-text classifications;
     4. backfills existing distinct nonblank values, keeping the exact stored code/casing;
     5. seeds the standard WAREHOUSE_TYPE values for every existing company;
     6. commits atomically and prints AD_LOOKUP_VALUE_APPLIED.

   What it deliberately does NOT do
     - no consumer column is altered, rewritten or trimmed;
     - no semantic mapping (FG is NOT translated to FINISHED_GOODS);
     - IvMSCode and every dedicated reference table stay untouched;
     - no FK from a consumer string column to AdLookupValue (validation is service-level).

   PREFLIGHT STOP semantics: if any current value has surrounding whitespace, differs only by case
   within one company, exceeds its consumer column width, or is owned by a blank/unknown company,
   the script THROWs and rolls the data phase back. It never silently "fixes" classification data —
   that is a DBA remediation decision.

   Re-run safety: the table/index/backfill/seed steps are all guarded, so running twice is a no-op
   apart from re-printing the verification counts.

   Batch layout note: CREATE TABLE and CREATE INDEX cannot share a batch (SQL Server compiles the
   whole batch before executing, so the index would reference a table that does not exist yet at
   compile time). The indexes therefore use dynamic SQL in the schema batch.
   ============================================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

/* --------------------------------------------------------------------------------------------
   0. Preflight — the authoritative company table must exist with CompanyCode.
   -------------------------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.Company', N'U') IS NULL
BEGIN
    THROW 51000, N'STOP: dbo.Company does not exist. This is not the ERP database (or the schema is incomplete); no change was made.', 1;
END

IF COL_LENGTH(N'dbo.Company', N'CompanyCode') IS NULL
BEGIN
    THROW 51001, N'STOP: dbo.Company has no CompanyCode column; cannot seed per-company lookup defaults.', 1;
END
GO

/* --------------------------------------------------------------------------------------------
   1. Schema — table + rowversion (ABSENT -> created).
   -------------------------------------------------------------------------------------------- */
BEGIN TRY
    BEGIN TRAN;

    IF OBJECT_ID(N'dbo.AdLookupValue', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.AdLookupValue
        (
            Uid             bigint          IDENTITY(1,1) NOT NULL,
            CompanyCode     nvarchar(5)     NOT NULL,   -- matches Company.CompanyCode
            LookupType      nvarchar(40)    NOT NULL,   -- controlled family, see AdLookupTypes
            Code            nvarchar(50)    NOT NULL,   -- physical ceiling ONLY (per-family limit is code-side)
            Description     nvarchar(150)   NOT NULL,
            SortOrder       int             NOT NULL CONSTRAINT DF_AdLookupValue_SortOrder DEFAULT (0),
            IsActive        bit             NOT NULL CONSTRAINT DF_AdLookupValue_IsActive DEFAULT (1),
            CreatedDate     datetime2       NULL,
            CreatedBy       nvarchar(20)    NULL,
            ModifiedDate    datetime2       NULL,
            ModifiedBy      nvarchar(20)    NULL,
            RowVersion      rowversion      NOT NULL,
            CONSTRAINT PK_AdLookupValue PRIMARY KEY CLUSTERED (Uid)
        );

        PRINT N'dbo.AdLookupValue created.';
    END
    ELSE
    BEGIN
        PRINT N'dbo.AdLookupValue already present - no change.';

        /* A pre-existing table must still carry the concurrency token the EF model expects. */
        IF COL_LENGTH(N'dbo.AdLookupValue', N'RowVersion') IS NULL
            THROW 51002, N'STOP: dbo.AdLookupValue exists WITHOUT RowVersion. Resolve the divergent schema (DBA decision), then re-run.', 1;
    END

    /* Dynamic SQL: an index created statically in the same batch as CREATE TABLE would be compiled
       before the table exists. */
    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.AdLookupValue')
                     AND name = N'UX_AdLookupValue_Company_Type_Code')
        EXEC (N'CREATE UNIQUE NONCLUSTERED INDEX UX_AdLookupValue_Company_Type_Code
                ON dbo.AdLookupValue (CompanyCode, LookupType, Code);');

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.AdLookupValue')
                     AND name = N'IX_AdLookupValue_Company_Type_Active_Sort')
        EXEC (N'CREATE NONCLUSTERED INDEX IX_AdLookupValue_Company_Type_Active_Sort
                ON dbo.AdLookupValue (CompanyCode, LookupType, IsActive, SortOrder);');

    COMMIT;
    PRINT N'AD_LOOKUP_VALUE_SCHEMA_APPLIED';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    PRINT N'AD_LOOKUP_VALUE_SCHEMA_ABORTED';
    THROW;
END CATCH
GO

/* --------------------------------------------------------------------------------------------
   2. Data phase — preflight, backfill, seed. Single transaction: all or nothing.
   -------------------------------------------------------------------------------------------- */
BEGIN TRY
    BEGIN TRAN;

    DECLARE @msg nvarchar(2048);

    /* 2a. Collect every current nonblank classification that must resolve after the cutover.
           MaxLen is the REAL consumer column width from AdLookupTypeRegistry — never the 50 ceiling. */
    CREATE TABLE #src
    (
        CompanyCode nvarchar(10) NULL,
        LookupType  nvarchar(40) NOT NULL,
        Code        nvarchar(100) NULL,
        MaxLen      int          NOT NULL
    );

    IF OBJECT_ID(N'dbo.IvWarehouse', N'U') IS NOT NULL
        INSERT INTO #src (CompanyCode, LookupType, Code, MaxLen)
        SELECT CompanyCode, N'WAREHOUSE_TYPE', WarehouseType, 20
        FROM dbo.IvWarehouse
        WHERE WarehouseType IS NOT NULL AND LTRIM(RTRIM(WarehouseType)) <> N'';

    IF OBJECT_ID(N'dbo.PrMachine', N'U') IS NOT NULL
        INSERT INTO #src (CompanyCode, LookupType, Code, MaxLen)
        SELECT CompCode, N'MACHINE_TYPE', MachineType, 30
        FROM dbo.PrMachine
        WHERE MachineType IS NOT NULL AND LTRIM(RTRIM(MachineType)) <> N'';

    IF OBJECT_ID(N'dbo.PrWorkCentre', N'U') IS NOT NULL
        INSERT INTO #src (CompanyCode, LookupType, Code, MaxLen)
        SELECT CompCode, N'WORK_CENTRE_CLASS', Class, 10
        FROM dbo.PrWorkCentre
        WHERE Class IS NOT NULL AND LTRIM(RTRIM(Class)) <> N'';

    IF OBJECT_ID(N'dbo.POSupplier', N'U') IS NOT NULL
    BEGIN
        INSERT INTO #src (CompanyCode, LookupType, Code, MaxLen)
        SELECT CompanyCode, N'SUPPLIER_TYPE', SuppType, 20
        FROM dbo.POSupplier
        WHERE SuppType IS NOT NULL AND LTRIM(RTRIM(SuppType)) <> N'';

        INSERT INTO #src (CompanyCode, LookupType, Code, MaxLen)
        SELECT CompanyCode, N'SUPPLIER_SUBGROUP', CreditorSubGroup, 20
        FROM dbo.POSupplier
        WHERE CreditorSubGroup IS NOT NULL AND LTRIM(RTRIM(CreditorSubGroup)) <> N'';
    END

    /* 2b. PREFLIGHT — missing company ownership. */
    IF EXISTS (SELECT 1 FROM #src WHERE CompanyCode IS NULL OR LTRIM(RTRIM(CompanyCode)) = N'')
    BEGIN
        SELECT TOP (1) @msg =
            N'STOP: a nonblank classification has no CompanyCode. LookupType=' + s.LookupType
            + N', Code=N''' + s.Code + N'''. Fix the owning row (DBA decision), then re-run.'
        FROM #src s
        WHERE s.CompanyCode IS NULL OR LTRIM(RTRIM(s.CompanyCode)) = N'';

        THROW 51003, @msg, 1;
    END

    /* 2c. PREFLIGHT — company code unknown to dbo.Company (or wider than the lookup column). */
    IF EXISTS (SELECT 1 FROM #src WHERE LEN(CompanyCode) > 5)
    BEGIN
        SELECT TOP (1) @msg =
            N'STOP: CompanyCode ''' + s.CompanyCode + N''' is longer than 5 characters (LookupType=' + s.LookupType
            + N', Code=N''' + s.Code + N'''). Fix the owning row (DBA decision), then re-run.'
        FROM #src s
        WHERE LEN(s.CompanyCode) > 5;

        THROW 51004, @msg, 1;
    END

    IF EXISTS (SELECT 1 FROM #src s
               WHERE NOT EXISTS (SELECT 1 FROM dbo.Company c WHERE c.CompanyCode = s.CompanyCode))
    BEGIN
        SELECT TOP (1) @msg =
            N'STOP: CompanyCode ''' + s.CompanyCode + N''' does not exist in dbo.Company (LookupType=' + s.LookupType
            + N', Code=N''' + s.Code + N'''). Fix the owning row (DBA decision), then re-run.'
        FROM #src s
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Company c WHERE c.CompanyCode = s.CompanyCode);

        THROW 51005, @msg, 1;
    END

    /* 2d. PREFLIGHT — surrounding whitespace (never silently trimmed). */
    IF EXISTS (SELECT 1 FROM #src WHERE Code <> LTRIM(RTRIM(Code)))
    BEGIN
        SELECT TOP (1) @msg =
            N'STOP: a classification has leading/trailing whitespace. CompanyCode=''' + s.CompanyCode
            + N''', LookupType=' + s.LookupType + N', Code=N''' + s.Code + N'''.'
        FROM #src s
        WHERE s.Code <> LTRIM(RTRIM(s.Code));

        THROW 51006, @msg, 1;
    END

    /* 2e. PREFLIGHT — case-insensitive duplicates within company+type that differ only by casing.
           BIN2 makes COUNT(DISTINCT) case-sensitive regardless of the database collation. */
    IF EXISTS (
        SELECT 1
        FROM #src
        GROUP BY CompanyCode, LookupType, UPPER(Code)
        HAVING COUNT(DISTINCT Code COLLATE Latin1_General_BIN2) > 1)
    BEGIN
        SELECT TOP (1) @msg =
            N'STOP: values differ only by casing for company ''' + s.CompanyCode + N''', LookupType=' + s.LookupType
            + N', key=''' + UPPER(s.Code) + N'''. Consolidate them deliberately (DBA decision), then re-run.'
        FROM #src s
        GROUP BY s.CompanyCode, s.LookupType, UPPER(s.Code)
        HAVING COUNT(DISTINCT s.Code COLLATE Latin1_General_BIN2) > 1;

        THROW 51007, @msg, 1;
    END

    /* 2f. PREFLIGHT — value cannot fit the consumer column. */
    IF EXISTS (SELECT 1 FROM #src WHERE LEN(Code) > MaxLen)
    BEGIN
        SELECT TOP (1) @msg =
            N'STOP: classification is longer than its consumer column. CompanyCode=''' + s.CompanyCode
            + N''', LookupType=' + s.LookupType + N', Code=N''' + s.Code + N''' ('
            + CONVERT(nvarchar(10), LEN(s.Code)) + N' > ' + CONVERT(nvarchar(10), s.MaxLen) + N').'
        FROM #src s
        WHERE LEN(s.Code) > s.MaxLen;

        THROW 51008, @msg, 1;
    END

    /* 2g. BACKFILL — one row per distinct company/type/value, exact stored casing, Description = Code.
           The case-insensitive comparison relies on the database's normal CI collation, matching the
           service's StringComparison.OrdinalIgnoreCase semantics. */
    INSERT INTO dbo.AdLookupValue
        (CompanyCode, LookupType, Code, Description, SortOrder, IsActive, CreatedDate, CreatedBy)
    SELECT DISTINCT
        s.CompanyCode,
        s.LookupType,
        s.Code,
        s.Code,
        0,
        1,
        SYSUTCDATETIME(),
        N'MIGRATION'
    FROM #src s
    WHERE NOT EXISTS (
        SELECT 1
        FROM dbo.AdLookupValue v
        WHERE v.CompanyCode = s.CompanyCode
          AND v.LookupType  = s.LookupType
          AND v.Code        = s.Code);

    PRINT N'Backfilled ' + CONVERT(nvarchar(20), @@ROWCOUNT) + N' classification value(s) from existing data.';

    /* 2h. SEED — standard WAREHOUSE_TYPE values for every existing company (idempotent). */
    INSERT INTO dbo.AdLookupValue
        (CompanyCode, LookupType, Code, Description, SortOrder, IsActive, CreatedDate, CreatedBy)
    SELECT
        c.CompanyCode,
        N'WAREHOUSE_TYPE',
        v.Code,
        v.Description,
        v.SortOrder,
        1,
        SYSUTCDATETIME(),
        N'MIGRATION'
    FROM dbo.Company c
    CROSS JOIN (VALUES
        (N'GENERAL',        N'General Warehouse', 10),
        (N'RAW_MATERIAL',   N'Raw Material',      20),
        (N'FINISHED_GOODS', N'Finished Goods',    30),
        (N'QUARANTINE',     N'Quarantine',        40),
        (N'RETURN',         N'Return',            50),
        (N'TRANSIT',        N'Transit',           60),
        (N'CONSIGNMENT',    N'Consignment',       70)
    ) v (Code, Description, SortOrder)
    WHERE c.CompanyCode IS NOT NULL
      AND LTRIM(RTRIM(c.CompanyCode)) <> N''
      AND NOT EXISTS (
          SELECT 1
          FROM dbo.AdLookupValue v2
          WHERE v2.CompanyCode = c.CompanyCode
            AND v2.LookupType  = N'WAREHOUSE_TYPE'
            AND v2.Code        = v.Code);

    PRINT N'Seeded ' + CONVERT(nvarchar(20), @@ROWCOUNT) + N' standard WAREHOUSE_TYPE row(s).';

    /* A backfilled legacy value that happens to equal a standard code still gets the curated
       description/sort order, but only while it is still untouched (Description = Code). */
    UPDATE v
    SET Description = seed.Description,
        SortOrder   = seed.SortOrder
    FROM dbo.AdLookupValue v
    JOIN (VALUES
        (N'GENERAL',        N'General Warehouse', 10),
        (N'RAW_MATERIAL',   N'Raw Material',      20),
        (N'FINISHED_GOODS', N'Finished Goods',    30),
        (N'QUARANTINE',     N'Quarantine',        40),
        (N'RETURN',         N'Return',            50),
        (N'TRANSIT',        N'Transit',           60),
        (N'CONSIGNMENT',    N'Consignment',       70)
    ) seed (Code, Description, SortOrder)
      ON  seed.Code = v.Code
    WHERE v.LookupType = N'WAREHOUSE_TYPE'
      AND v.Description = v.Code;

    COMMIT;

    /* 2i. Deterministic verification output (also proves the data phase committed). */
    SELECT v.CompanyCode, v.LookupType, ValueCount = COUNT(*), ActiveCount = SUM(CASE WHEN v.IsActive = 1 THEN 1 ELSE 0 END)
    FROM dbo.AdLookupValue v
    GROUP BY v.CompanyCode, v.LookupType
    ORDER BY v.CompanyCode, v.LookupType;

    /* Any current classification that still does not resolve after the backfill is a real problem. */
    IF EXISTS (
        SELECT 1
        FROM #src s
        WHERE NOT EXISTS (
            SELECT 1
            FROM dbo.AdLookupValue v
            WHERE v.CompanyCode = s.CompanyCode
              AND v.LookupType  = s.LookupType
              AND v.Code        = s.Code
              AND v.IsActive    = 1))
    BEGIN
        PRINT N'WARNING: at least one current classification still has no ACTIVE lookup value. Run the unmatched-value query below.';

        SELECT DISTINCT s.CompanyCode, s.LookupType, s.Code
        FROM #src s
        WHERE NOT EXISTS (
            SELECT 1
            FROM dbo.AdLookupValue v
            WHERE v.CompanyCode = s.CompanyCode
              AND v.LookupType  = s.LookupType
              AND v.Code        = s.Code
              AND v.IsActive    = 1)
        ORDER BY s.CompanyCode, s.LookupType, s.Code;
    END
    ELSE
    BEGIN
        PRINT N'All current classifications resolve to an ACTIVE lookup value.';
    END

    DROP TABLE #src;

    PRINT N'AD_LOOKUP_VALUE_APPLIED';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF OBJECT_ID(N'tempdb..#src') IS NOT NULL DROP TABLE #src;
    PRINT N'AD_LOOKUP_VALUE_ABORTED';
    THROW;
END CATCH
GO
