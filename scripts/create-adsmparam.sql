-- Dynamic application settings registry (manual DBA script — do NOT run at app startup).
-- Target database: the same database as ConnectionStrings:DefaultConnection (e.g. ERPWeb).
--
-- ErpWeb.Core.Settings.AppSettingCatalogue owns the set of legal keys. A row here is an OVERRIDE
-- only: no row means the code default applies. An empty table is therefore a valid, fully working
-- state, and applying this script changes NO behaviour on its own.
--
-- Idempotent: every block is guarded on its own object, and CREATE TABLE / CREATE INDEX are in
-- separate batches because SQL Server compiles a whole batch before executing any of it.
--
-- GOTCHA (cost a run before): a guard using RETURN stops only its OWN batch, so a RETURN can never
-- protect the batches below it. Each block below therefore checks for itself.
--
-- WHY THE INDEXES ARE NOT FILTERED. A filtered index forces SET QUOTED_IDENTIFIER ON on EVERY
-- INSERT/UPDATE against the table (SQL Server error 1934), which makes ad-hoc DML and any deploy
-- script that does not set the option fail with a confusing message. The filter would only ever have
-- excluded IsActive = 0 rows, and those never exist here: clearing a value DELETEs the row rather
-- than deactivating it (see CK_AdSmParam_OneValue). So the filter buys nothing and costs a foot-gun.
-- The rebuild blocks below convert indexes created filtered by an earlier run of this script.

SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.AdSmParam', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AdSmParam
    (
        ParamId      int            IDENTITY(1,1) NOT NULL,

        ModuleCode   nvarchar(20)   NOT NULL,
        ParamKey     nvarchar(60)   NOT NULL,
        ScopeCode    nvarchar(10)   NOT NULL,
        CompanyCode  nvarchar(5)    NULL,
        BranchCode   nvarchar(5)    NULL,

        ValueText    nvarchar(400)  NULL,
        ValueNumber  decimal(28,10) NULL,
        ValueDate    date           NULL,
        ValueFlag    bit            NULL,

        IsActive     bit            NOT NULL CONSTRAINT DF_AdSmParam_IsActive DEFAULT (1),
        Remark       nvarchar(400)  NULL,
        CreatedDate  datetime       NULL,
        CreatedBy    nvarchar(10)   NULL,
        ModifiedDate datetime       NULL,
        ModifiedBy   nvarchar(10)   NULL,
        RowVersion   rowversion     NOT NULL,

        CONSTRAINT PK_AdSmParam PRIMARY KEY CLUSTERED (ParamId),

        -- ModuleCode is deliberately NOT constrained: adding Planning / Production / QA later must
        -- not require DDL. The catalogue in code is the authority for which modules and keys exist.
        CONSTRAINT CK_AdSmParam_Scope CHECK (ScopeCode IN (N'GLOBAL', N'COMPANY', N'BRANCH')),

        -- The scope token and the scope columns can never disagree.
        CONSTRAINT CK_AdSmParam_ScopeCols CHECK (
            (ScopeCode = N'GLOBAL'  AND CompanyCode IS NULL     AND BranchCode IS NULL) OR
            (ScopeCode = N'COMPANY' AND CompanyCode IS NOT NULL AND BranchCode IS NULL) OR
            (ScopeCode = N'BRANCH'  AND CompanyCode IS NOT NULL AND BranchCode IS NOT NULL)),

        -- Exactly one typed value column is populated. "Reset to default" is therefore a DELETE of
        -- the row, never a null-out of all four columns.
        CONSTRAINT CK_AdSmParam_OneValue CHECK (
            (CASE WHEN ValueText   IS NULL THEN 0 ELSE 1 END) +
            (CASE WHEN ValueNumber IS NULL THEN 0 ELSE 1 END) +
            (CASE WHEN ValueDate   IS NULL THEN 0 ELSE 1 END) +
            (CASE WHEN ValueFlag   IS NULL THEN 0 ELSE 1 END) = 1)
    );

    PRINT N'ADSM_PARAM_CREATED';
END
ELSE
    PRINT N'ADSM_PARAM_ALREADY_PRESENT';
GO

-- One row per key per scope target. NULL counts as EQUAL in a SQL Server unique index, which is
-- exactly what keeps GLOBAL rows (CompanyCode / BranchCode both NULL) unique.
--
-- Rebuild step: drop a filtered index left behind by an earlier run of this script.
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'UQ_AdSmParam_Key'
             AND object_id = OBJECT_ID(N'dbo.AdSmParam')
             AND has_filter = 1)
BEGIN
    DROP INDEX UQ_AdSmParam_Key ON dbo.AdSmParam;
    PRINT N'ADSM_PARAM_UNIQUE_KEY_DROPPED_FOR_REBUILD';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UQ_AdSmParam_Key' AND object_id = OBJECT_ID(N'dbo.AdSmParam'))
BEGIN
    CREATE UNIQUE INDEX UQ_AdSmParam_Key
        ON dbo.AdSmParam (ModuleCode, ParamKey, ScopeCode, CompanyCode, BranchCode);

    PRINT N'ADSM_PARAM_UNIQUE_KEY_CREATED';
END
GO

-- Covering lookup index for the read path (resolve one module's snapshot for one company/branch).
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = N'IX_AdSmParam_Lookup'
             AND object_id = OBJECT_ID(N'dbo.AdSmParam')
             AND has_filter = 1)
BEGIN
    DROP INDEX IX_AdSmParam_Lookup ON dbo.AdSmParam;
    PRINT N'ADSM_PARAM_LOOKUP_INDEX_DROPPED_FOR_REBUILD';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_AdSmParam_Lookup' AND object_id = OBJECT_ID(N'dbo.AdSmParam'))
BEGIN
    CREATE INDEX IX_AdSmParam_Lookup
        ON dbo.AdSmParam (CompanyCode, ModuleCode, ParamKey)
        INCLUDE (ScopeCode, BranchCode, ValueText, ValueNumber, ValueDate, ValueFlag);

    PRINT N'ADSM_PARAM_LOOKUP_INDEX_CREATED';
END
GO

PRINT N'create-adsmparam.sql complete. No rows are seeded: an empty table means every setting resolves to its code default.';
GO
