-- Inventory stock integrity cutover preflight (Gate A / Gate B)
-- Run read-only against the target SQL Server company/branch before enabling strict chronology.
-- Replace @Company / @Branch as needed.

DECLARE @Company nvarchar(5) = N'DEMO';
DECLARE @Branch  nvarchar(5) = N'HQ';

PRINT '=== Gate A: structural / data corruption (must be zero before strict cutover) ===';

-- Positive stock with null TransDate
SELECT 'NULL_TRANSDATE' AS Finding, b.ID, b.ICode, b.WhCode, b.LocCode, b.LotNo, b.IStatus, b.StdQty, b.TransDate
FROM dbo.IvBalLoc b
WHERE b.CompanyCode = @Company AND b.BranchCode = @Branch
  AND b.StdQty > 0 AND b.TransDate IS NULL;

-- Negative balances
SELECT 'NEGATIVE_BALANCE' AS Finding, b.ID, b.ICode, b.WhCode, b.LocCode, b.LotNo, b.IStatus, b.StdQty
FROM dbo.IvBalLoc b
WHERE b.CompanyCode = @Company AND b.BranchCode = @Branch
  AND b.StdQty < 0;

-- StdUom mismatch vs stock master
SELECT 'STDUOM_MISMATCH' AS Finding, b.ID, b.ICode, b.StdUom AS BalUom, m.StdUom AS MasterUom, b.StdQty
FROM dbo.IvBalLoc b
INNER JOIN dbo.IvStockMaster m
  ON m.CompanyCode = b.CompanyCode AND m.ICode = b.ICode
WHERE b.CompanyCode = @Company AND b.BranchCode = @Branch
  AND NULLIF(LTRIM(RTRIM(b.StdUom)), N'') IS NOT NULL
  AND NULLIF(LTRIM(RTRIM(m.StdUom)), N'') IS NOT NULL
  AND UPPER(LTRIM(RTRIM(b.StdUom))) <> UPPER(LTRIM(RTRIM(m.StdUom)));

-- Duplicate stock slices
SELECT 'DUPLICATE_SLICE' AS Finding, CompanyCode, BranchCode, ICode, WhCode, LocCode, LotNo, IStatus, COUNT(*) AS RowCount
FROM dbo.IvBalLoc
WHERE CompanyCode = @Company AND BranchCode = @Branch
GROUP BY CompanyCode, BranchCode, ICode, WhCode, LocCode, LotNo, IStatus
HAVING COUNT(*) > 1;

-- Orphan history (missing BalLoc FK)
SELECT 'ORPHAN_HISTORY' AS Finding, h.ID, h.BatchNo, h.TrxLineNo, h.FromBalLocId, h.ToBalLocId
FROM dbo.IvTrxHistory h
WHERE h.CompanyCode = @Company AND h.BranchCode = @Branch
  AND (
    (h.FromBalLocId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.IvBalLoc b WHERE b.ID = h.FromBalLocId))
    OR (h.ToBalLocId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.IvBalLoc b WHERE b.ID = h.ToBalLocId))
  );

PRINT '=== Live constraint presence (deployment check) ===';
SELECT name
FROM sys.check_constraints
WHERE parent_object_id = OBJECT_ID(N'dbo.IvBalLoc')
  AND name IN (N'CK_IvBalLoc_StdQty_NonNegative');

SELECT name
FROM sys.indexes
WHERE object_id = OBJECT_ID(N'dbo.IvBalLoc')
  AND name = N'UQ_IvBalLoc_StockSlice';

PRINT '=== Gate B: opening-baseline / mismatch classification (review, do not blindly zero) ===';
PRINT 'Use IvInventoryReconciliationService for MISMATCH / UNEXPECTED_BALANCE.';
PRINT 'Positive BalLoc with no history = opening-baseline candidate: establish opening date before Gate C.';
PRINT 'Preferred null-date backfill: latest IvTrxHistory.TrxDtTime for that BalLoc; never auto-stamp today.';

-- Suggested TransDate backfill preview (does not update)
SELECT b.ID, b.ICode, b.StdQty, b.TransDate AS CurrentTransDate,
       (SELECT MAX(h.TrxDtTime)
        FROM dbo.IvTrxHistory h
        WHERE h.CompanyCode = b.CompanyCode AND h.BranchCode = b.BranchCode
          AND (h.FromBalLocId = b.ID OR h.ToBalLocId = b.ID)) AS SuggestedTransDate
FROM dbo.IvBalLoc b
WHERE b.CompanyCode = @Company AND b.BranchCode = @Branch
  AND b.StdQty > 0 AND b.TransDate IS NULL;
