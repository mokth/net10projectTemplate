-- ============================================================================
-- Additive migration for dbo.EInvDocSubmission  (ErpWeb-owned e-Invoice history)
--
-- Plan of record : plans/plan-einvDocSubmissionHistory.prompt.md   (Phase 2.6)
-- Phase 0 evidence: docs/einvoice-history-phase0-findings.txt
--
-- WHY THIS IS AN ALTER SCRIPT, NOT A CREATE SCRIPT
--   dbo.EInvDocSubmission is a LEGACY physical table that ErpWeb now owns. The
--   legacy column names are preserved verbatim; nothing is renamed or dropped.
--
-- LIVE SHAPE DISCOVERED IN PHASE 0 (see the findings file)
--   42 columns. HEAP: no primary key, no index, no unique/check/default constraint,
--   no foreign key, and NO TRIGGER (the legacy EInvoiceContext declared
--   HasTrigger("tr_EInvDocSubmission_ForInsert") - that declaration is STALE for this
--   database, so the ErpWeb model must NOT declare it).
--   `status` is NOT NULL with no default            -> the app must always supply it.
--   `document` is TEXT (deprecated LOB)             -> mapped, never written.
--   date columns are `datetime`, NOT datetime2      -> existing columns keep their type.
--   `documentType` nvarchar(3), `companyID` nvarchar(20).
--   Row count at discovery: 0 -> no backfill needed and no NULL companyID exists,
--   so the plain (unfiltered) unique index is used and no data repair step exists.
--
-- STEPS: preflight -> add columns -> primary key + indexes -> postflight.
-- Safe to run twice: every step is guarded.
-- Run with:  sqlcmd -E -d ERPWeb -I -i scripts/alter-einvdocsubmission-einvoice.sql
-- ============================================================================

SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.EInvDocSubmission', N'U') IS NULL
BEGIN
    THROW 51000, N'dbo.EInvDocSubmission does not exist. This is an ADDITIVE migration over an existing legacy table - it deliberately does not create it. Run scripts/discover-einvdocsubmission.sql first.', 1;
END
GO

-- ---------------------------------------------------------------------------
-- 1. PREFLIGHT - blocking duplicate check on the proposed four-part key.
--    (plan D-11: fail loudly; never silently fall back to a non-unique index)
-- ---------------------------------------------------------------------------
IF EXISTS
(
    SELECT 1
    FROM dbo.EInvDocSubmission
    GROUP BY companyID, submissionUUID, documentType, documentNo
    HAVING COUNT(*) > 1
)
BEGIN
    THROW 51001, N'EInvDocSubmission already contains duplicate (companyID, submissionUUID, documentType, documentNo) rows, so UX_EInvDocSubmission_Submission cannot be created. Resolve them deliberately (merge or quarantine). This script will NOT delete or merge data.', 1;
END
PRINT N'PREFLIGHT OK: no duplicate candidate keys.';
GO

-- ---------------------------------------------------------------------------
-- 2. PREFLIGHT - existing index inventory. If an equivalent UNIQUE index already
--    covers the four key columns under another name, creating ours is still
--    harmless, but the DBA should know (plan: "preflight existing index check").
--    The PRIMARY KEY is excluded deliberately: it is unique by definition but is
--    not an alternative key for the four business columns, and counting it made a
--    second run of this script emit a misleading warning every time.
-- ---------------------------------------------------------------------------
IF EXISTS
(
    SELECT 1
    FROM sys.indexes i
    WHERE i.object_id = OBJECT_ID(N'dbo.EInvDocSubmission')
      AND i.is_unique = 1
      AND i.is_primary_key = 0
      AND i.name <> N'UX_EInvDocSubmission_Submission'
)
BEGIN
    PRINT N'NOTE: a different UNIQUE index already exists on dbo.EInvDocSubmission. Review it before relying on UX_EInvDocSubmission_Submission.';
    SELECT i.name AS existing_unique_index, i.is_primary_key, i.is_unique_constraint
    FROM sys.indexes i
    WHERE i.object_id = OBJECT_ID(N'dbo.EInvDocSubmission')
      AND i.is_unique = 1 AND i.is_primary_key = 0;
END
ELSE
BEGIN
    PRINT N'PREFLIGHT OK: no pre-existing unique index other than the primary key.';
END
GO

-- ---------------------------------------------------------------------------
-- 3. ADDITIVE COLUMNS (the five new columns - plan 2.1).
--    Each guarded, so a second run is a no-op.
-- ---------------------------------------------------------------------------
IF COL_LENGTH(N'dbo.EInvDocSubmission', N'BranchCode') IS NULL
    ALTER TABLE dbo.EInvDocSubmission ADD BranchCode nvarchar(5) NULL;
GO
IF COL_LENGTH(N'dbo.EInvDocSubmission', N'OverallStatus') IS NULL
    ALTER TABLE dbo.EInvDocSubmission ADD OverallStatus nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.EInvDocSubmission', N'DocumentCount') IS NULL
    ALTER TABLE dbo.EInvDocSubmission ADD DocumentCount int NULL;
GO
IF COL_LENGTH(N'dbo.EInvDocSubmission', N'CreatedOn') IS NULL
    ALTER TABLE dbo.EInvDocSubmission ADD CreatedOn datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.EInvDocSubmission', N'LastSyncedOn') IS NULL
    ALTER TABLE dbo.EInvDocSubmission ADD LastSyncedOn datetime2 NULL;
GO

-- ---------------------------------------------------------------------------
-- 4. PRIMARY KEY.
--    PHASE 0 FINDING, NOT IN THE ORIGINAL PLAN: the legacy table is a HEAP with
--    no primary key while ID is IDENTITY(1,1). The ErpWeb model declares
--    HasKey(Id), so the identity must be made authoritative in the database too.
--    Safe: ID is identity (already unique in practice) and the table held 0 rows.
-- ---------------------------------------------------------------------------
IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.EInvDocSubmission') AND is_primary_key = 1
)
BEGIN
    ALTER TABLE dbo.EInvDocSubmission
        ADD CONSTRAINT PK_EInvDocSubmission PRIMARY KEY CLUSTERED (ID);
    PRINT N'PK_EInvDocSubmission created.';
END
ELSE
BEGIN
    PRINT N'PREFLIGHT OK: a primary key already exists.';
END
GO

-- ---------------------------------------------------------------------------
-- 5. INDEXES.
--    UX = the authority for "one row per submitted document within one submission"
--         (plan R1/R4/D-9). Nullable columns are fine here: the writer always
--         supplies a company, and Phase 0 found no NULL companyID rows.
--    IX = serves "the current row for a document is the highest ID for that
--         document" (plan R5), i.e. a re-submission creates an extra row.
-- ---------------------------------------------------------------------------
IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.EInvDocSubmission') AND name = N'UX_EInvDocSubmission_Submission'
)
BEGIN
    CREATE UNIQUE INDEX UX_EInvDocSubmission_Submission
        ON dbo.EInvDocSubmission (companyID, submissionUUID, documentType, documentNo);
    PRINT N'UX_EInvDocSubmission_Submission created.';
END
ELSE
BEGIN
    PRINT N'UX_EInvDocSubmission_Submission already exists.';
END
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.EInvDocSubmission') AND name = N'IX_EInvDocSubmission_Document'
)
BEGIN
    CREATE INDEX IX_EInvDocSubmission_Document
        ON dbo.EInvDocSubmission (companyID, documentType, documentNo, ID);
    PRINT N'IX_EInvDocSubmission_Document created.';
END
ELSE
BEGIN
    PRINT N'IX_EInvDocSubmission_Document already exists.';
END
GO

-- ---------------------------------------------------------------------------
-- 6. POSTFLIGHT - separate batch on purpose: SQL Server compiles a whole batch
--    before running it, so a COL_LENGTH check on a column added in the SAME
--    batch would not compile (this trap has cost a run in this repo before).
-- ---------------------------------------------------------------------------
PRINT N'=== POSTFLIGHT ===';

SELECT COL_LENGTH(N'dbo.EInvDocSubmission', N'BranchCode')    AS BranchCode_bytes,
       COL_LENGTH(N'dbo.EInvDocSubmission', N'OverallStatus') AS OverallStatus_bytes,
       COL_LENGTH(N'dbo.EInvDocSubmission', N'DocumentCount') AS DocumentCount_bytes,
       COL_LENGTH(N'dbo.EInvDocSubmission', N'CreatedOn')     AS CreatedOn_bytes,
       COL_LENGTH(N'dbo.EInvDocSubmission', N'LastSyncedOn')  AS LastSyncedOn_bytes;

SELECT i.name, i.is_unique, i.is_primary_key
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID(N'dbo.EInvDocSubmission')
ORDER BY i.is_primary_key DESC, i.name;

IF COL_LENGTH(N'dbo.EInvDocSubmission', N'BranchCode') IS NULL
   OR COL_LENGTH(N'dbo.EInvDocSubmission', N'OverallStatus') IS NULL
   OR COL_LENGTH(N'dbo.EInvDocSubmission', N'DocumentCount') IS NULL
   OR COL_LENGTH(N'dbo.EInvDocSubmission', N'CreatedOn') IS NULL
   OR COL_LENGTH(N'dbo.EInvDocSubmission', N'LastSyncedOn') IS NULL
BEGIN
    PRINT N'STOP: not all five additive columns are present.';
END

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.EInvDocSubmission') AND name = N'UX_EInvDocSubmission_Submission'
)
BEGIN
    PRINT N'STOP: UX_EInvDocSubmission_Submission is MISSING. Do not rely on application-level uniqueness - the concurrency guarantee (plan D-10) requires the index.';
END
ELSE
BEGIN
    PRINT N'POSTFLIGHT OK: 5 columns + UX + IX + PK present.';
END
GO
