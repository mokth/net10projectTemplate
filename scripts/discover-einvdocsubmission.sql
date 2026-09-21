-- ============================================================================
-- READ-ONLY discovery probe for dbo.EInvDocSubmission  (Phase 0 of
-- plans/plan-einvDocSubmissionHistory.prompt.md)
--
-- This is NOT a migration. It changes nothing and is safe to run repeatedly.
-- It produces the preflight report and schema snapshot that the migration in
-- scripts/alter-einvdocsubmission-einvoice.sql is written against.
--
-- Run with (ALWAYS pass -d, and -I so QUOTED_IDENTIFIER is set):
--     sqlcmd -E -d ERPWeb -I -i scripts/discover-einvdocsubmission.sql
--
-- An omitted -d runs against the login's default database and yields a
-- confident, wrong "object does not exist" answer. Do not omit it.
-- ============================================================================

SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

DECLARE @obj int = OBJECT_ID(N'dbo.EInvDocSubmission', N'U');

IF @obj IS NULL
BEGIN
    PRINT N'STOP: dbo.EInvDocSubmission does not exist in this database.';
    SELECT DB_NAME() AS database_name, N'dbo.EInvDocSubmission missing' AS problem;
    RETURN;
END

PRINT N'>>> database / object';
SELECT DB_NAME() AS database_name, @@SERVERNAME AS server_name, @obj AS object_id;

PRINT N'>>> 0.1 COLUMNS';
SELECT c.column_id,
       c.name AS column_name,
       ty.name AS type_name,
       c.max_length,
       c.precision,
       c.scale,
       c.is_nullable,
       c.is_identity
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = @obj
ORDER BY c.column_id;

PRINT N'>>> 0.1b NO-NULL COLUMNS WITHOUT A DEFAULT (insert-blockers for the app)';
SELECT c.name AS column_name, ty.name AS type_name, c.is_identity
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints d
       ON d.parent_object_id = c.object_id AND d.parent_column_id = c.column_id
WHERE c.object_id = @obj
  AND c.is_nullable = 0
  AND c.is_identity = 0
  AND d.object_id IS NULL
ORDER BY c.column_id;

PRINT N'>>> 0.2 TRIGGERS  (a row here means AbstractContext must declare HasTrigger)';
SELECT tr.name, tr.is_disabled, tr.is_instead_of_trigger
FROM sys.triggers tr
WHERE tr.parent_id = @obj;

PRINT N'>>> 0.3 INDEXES';
SELECT i.index_id, i.name, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint,
       i.has_filter, i.filter_definition
FROM sys.indexes i
WHERE i.object_id = @obj
ORDER BY i.index_id;

PRINT N'>>> 0.3b INDEX COLUMNS';
SELECT i.name AS index_name, ic.key_ordinal, ic.is_included_column, c.name AS column_name
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = @obj
ORDER BY i.index_id, ic.key_ordinal, ic.index_column_id;

PRINT N'>>> 0.3c FOREIGN KEYS';
SELECT fk.name, OBJECT_NAME(fk.referenced_object_id) AS referenced_table
FROM sys.foreign_keys fk
WHERE fk.parent_object_id = @obj;

PRINT N'>>> 0.3d DEFAULT CONSTRAINTS';
SELECT d.name, c.name AS column_name, d.definition
FROM sys.default_constraints d
JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
WHERE d.parent_object_id = @obj;

PRINT N'>>> 0.3e CHECK CONSTRAINTS';
SELECT cc.name, cc.definition
FROM sys.check_constraints cc
WHERE cc.parent_object_id = @obj;

PRINT N'>>> 0.3f ROW COUNT';
SELECT COUNT_BIG(*) AS row_count FROM dbo.EInvDocSubmission;

PRINT N'>>> 0.3g THE FIVE ADDITIVE COLUMNS (NULL = not yet added)';
SELECT COL_LENGTH(N'dbo.EInvDocSubmission', N'BranchCode')    AS BranchCode_bytes,
       COL_LENGTH(N'dbo.EInvDocSubmission', N'OverallStatus') AS OverallStatus_bytes,
       COL_LENGTH(N'dbo.EInvDocSubmission', N'DocumentCount') AS DocumentCount_bytes,
       COL_LENGTH(N'dbo.EInvDocSubmission', N'CreatedOn')     AS CreatedOn_bytes,
       COL_LENGTH(N'dbo.EInvDocSubmission', N'LastSyncedOn')  AS LastSyncedOn_bytes;

PRINT N'>>> 0.4 DUPLICATE CANDIDATE KEYS - BLOCKING (empty result set = safe to add the unique index)';
SELECT companyID, submissionUUID, documentType, documentNo, COUNT(*) AS Cnt
FROM dbo.EInvDocSubmission
GROUP BY companyID, submissionUUID, documentType, documentNo
HAVING COUNT(*) > 1;

PRINT N'>>> 0.5 NULL companyID (NULL counts as EQUAL in a SQL Server unique index)';
SELECT COUNT_BIG(*) AS null_companyID_rows
FROM dbo.EInvDocSubmission
WHERE companyID IS NULL;

PRINT N'>>> 0.5b companyID widths actually in use';
SELECT LEN(companyID) AS companyID_len, COUNT_BIG(*) AS rows_count
FROM dbo.EInvDocSubmission
GROUP BY LEN(companyID)
ORDER BY companyID_len;

PRINT N'>>> 0.6 QUICK SAMPLE (newest 20 rows)';
SELECT TOP (20) ID, companyID, submissionUUID, uuid, documentNo, documentType,
       status, dateTimeIssued, createdByUserId
FROM dbo.EInvDocSubmission
ORDER BY ID DESC;

PRINT N'>>> 0.7 documentType / status DISTRIBUTION (legacy writes documentType = ''POS'')';
SELECT documentType, status, COUNT_BIG(*) AS Cnt
FROM dbo.EInvDocSubmission
GROUP BY documentType, status
ORDER BY documentType, status;

PRINT N'>>> END OF REPORT';
