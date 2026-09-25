/* ============================================================================
   Inventory inquiry suite - Phase 1 index.
   Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
   Target database: the same database as ConnectionStrings:DefaultConnection (ERPWeb).
   Run with:  sqlcmd -E -d ERPWeb -i scripts/alter-ivtrxhistory-inquiry-index.sql

   WHY: the only indexes on dbo.IvTrxHistory today are
     UQ_IvTrxHistory_Company_Branch_Batch_Line, IX_IvTrxHistory_ICode_TrxDtTime,
     IX_IvTrxHistory_BatchNo and the four FK indexes.
   The transaction inquiry's DEFAULT view is a type-and-date window with NO item filter
   (CompanyCode, BranchCode, TrxType, TrxDtTime), which those indexes cannot serve - it scans.

   This index is PURELY ADDITIVE: no column is added or changed, so it cannot alter behaviour.

   A further index is deliberately NOT added here (D20). The stock card's dominant predicate is
   ICode + TrxDtTime, which IX_IvTrxHistory_ICode_TrxDtTime already covers; the remaining slice
   columns are a residual filter on a narrow seek, which is the expected shape. If a real execution
   plan on a representative table shows a scan or a sort spill, design the next index FROM that plan.
   ============================================================================ */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.IvTrxHistory', N'U') IS NULL
BEGIN
    PRINT N'STOP: dbo.IvTrxHistory does not exist - run the inventory schema scripts first.';
    RETURN;
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_IvTrxHistory_Inquiry'
      AND object_id = OBJECT_ID(N'dbo.IvTrxHistory'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_IvTrxHistory_Inquiry
        ON dbo.IvTrxHistory (CompanyCode, BranchCode, TrxType, TrxDtTime);
    PRINT N'Created IX_IvTrxHistory_Inquiry.';
END
ELSE
    PRINT N'IX_IvTrxHistory_Inquiry already present - no-op.';
GO

/* Verification: expect exactly one row for IX_IvTrxHistory_Inquiry. */
SELECT
    IndexName = i.name,
    KeyColumns = STUFF((
        SELECT N', ' + c.name
        FROM sys.index_columns ic
        INNER JOIN sys.columns c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE ic.object_id = i.object_id
          AND ic.index_id = i.index_id
          AND ic.is_included_column = 0
        ORDER BY ic.key_ordinal
        FOR XML PATH(N''), TYPE).value(N'.[1]', N'nvarchar(max)'), 1, 2, N''),
    IsUnique = i.is_unique
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID(N'dbo.IvTrxHistory')
  AND i.name = N'IX_IvTrxHistory_Inquiry';

PRINT N'alter-ivtrxhistory-inquiry-index.sql: exactly 1 row expected above.';
GO
