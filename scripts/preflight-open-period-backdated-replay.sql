/* Read-only readiness findings. Every returned row blocks branch replay activation.
   Run after alter-open-period-backdated-replay.sql; investigate each count/line.
   This script deliberately does not guess a legacy zero-count posting order. */
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

IF OBJECT_ID(N'dbo.StockCountCostBasis', N'U') IS NULL
    OR OBJECT_ID(N'dbo.StockCountReplayGeneration', N'U') IS NULL
    OR OBJECT_ID(N'dbo.StockReplayRun', N'U') IS NULL
    OR OBJECT_ID(N'dbo.StockReplayEffect', N'U') IS NULL
    THROW 51110, 'Replay lineage schema is missing; deploy it before preflight.', 1;

DECLARE @Findings table
(
    CompanyCode nvarchar(5) NOT NULL,
    BranchCode nvarchar(5) NOT NULL,
    CountNo nvarchar(30) NULL,
    StockCountLineId int NULL,
    FindingCode nvarchar(50) NOT NULL,
    Detail nvarchar(500) NOT NULL
);

INSERT INTO @Findings
SELECT e.CompanyCode, e.BranchCode, NULL, NULL, N'UNSEALED_STOCK_POSTING',
       CONCAT(N'StockPosting ', p.Id, N' is unsealed')
FROM dbo.StockLedgerEpoch e
JOIN dbo.StockPosting p ON p.CompanyCode = e.CompanyCode AND p.BranchCode = e.BranchCode
    AND p.LedgerEpochId = e.Id
WHERE e.Status = N'ACTIVE' AND p.SealedAtUtc IS NULL;

INSERT INTO @Findings
SELECT h.CompanyCode, h.BranchCode, h.CountNo, l.ID,
       CASE WHEN h.PostedBatchNo IS NULL THEN N'LEGACY_ZERO_COUNT_UNORDERED'
            ELSE N'LEGACY_COUNT_UNANCHORED' END,
       CASE WHEN h.PostedBatchNo IS NULL
            THEN N'No V2 anchor or trustworthy posting sequence exists for this zero-variance count.'
            ELSE CONCAT(N'Batch ', h.PostedBatchNo,
                        N' requires exact sealed V2 history/sequence reconciliation before backfill.') END
FROM dbo.IvStockCountHdr h
JOIN dbo.IvStockCountLine l ON l.StockCountId = h.ID AND l.PhysicalQty IS NOT NULL
WHERE h.Status = N'POSTED'
  AND NOT EXISTS
      (SELECT 1 FROM dbo.IvPeriodCloseHdr pc
       WHERE pc.CompanyCode = h.CompanyCode AND pc.BranchCode = h.BranchCode
         AND pc.Status = N'CLOSED' AND h.CountDate BETWEEN pc.PeriodFrom AND pc.PeriodTo)
  AND NOT EXISTS (SELECT 1 FROM dbo.StockCountReplayGeneration g WHERE g.StockCountLineId = l.ID);

INSERT INTO @Findings
SELECT h.CompanyCode, h.BranchCode, h.CountNo, l.ID,
       N'POSITIVE_COUNT_MISSING_BASIS',
       N'A positive physical observation has no frozen approved cost basis.'
FROM dbo.IvStockCountHdr h
JOIN dbo.IvStockCountLine l ON l.StockCountId = h.ID
WHERE h.Status IN (N'POSTED', N'ROLLED_BACK') AND l.PhysicalQty > 0
  AND EXISTS (SELECT 1 FROM dbo.StockCountReplayGeneration g WHERE g.StockCountLineId = l.ID)
  AND NOT EXISTS (SELECT 1 FROM dbo.StockCountCostBasis b WHERE b.StockCountLineId = l.ID);

INSERT INTO @Findings
SELECT h.CompanyCode, h.BranchCode, h.CountNo, l.ID,
       N'BASIS_TENANT_MISMATCH',
       CONCAT(N'Basis ', b.Id, N' tenant differs from count header.')
FROM dbo.IvStockCountHdr h
JOIN dbo.IvStockCountLine l ON l.StockCountId = h.ID
JOIN dbo.StockCountCostBasis b ON b.StockCountLineId = l.ID
WHERE b.CompanyCode <> h.CompanyCode OR b.BranchCode <> h.BranchCode;

INSERT INTO @Findings
SELECT h.CompanyCode, h.BranchCode, h.CountNo, l.ID,
       N'COUNT_BASIS_INVALID',
       CONCAT(N'Basis ', b.Id, N' has invalid item, revision, or approved cost evidence.')
FROM dbo.IvStockCountHdr h
JOIN dbo.IvStockCountLine l ON l.StockCountId = h.ID
JOIN dbo.StockCountCostBasis b ON b.StockCountLineId = l.ID
LEFT JOIN dbo.ItemStandardCostRevision s ON s.Id = b.StandardCostRevisionId
WHERE b.FrozenFallbackUnitCost < 0
   OR (b.CostBasisKind IN (N'LEDGER_WAC', N'LEDGER_FIFO')
       AND (b.CostBasisWatermark IS NULL OR b.CostBasisHash IS NULL))
   OR (b.CostBasisKind = N'STANDARD_REVISION'
       AND (s.Id IS NULL OR s.CompanyCode <> h.CompanyCode OR s.BranchCode <> h.BranchCode
            OR s.ItemCode <> l.ICode OR s.Status <> N'APPROVED'
            OR s.EffectiveFrom > h.CountDate
            OR (s.EffectiveTo IS NOT NULL AND s.EffectiveTo < h.CountDate)))
   OR (b.CostBasisKind = N'MANUAL_APPROVED'
       AND (ISNULL(b.CostEvidenceType, N'') <> N'MANUAL_APPROVED' OR b.FrozenFallbackUnitCost <= 0
            OR NULLIF(LTRIM(RTRIM(b.CostOverrideReason)), N'') IS NULL
            OR b.CostApprovedBy IS NULL OR b.CostApprovedAtUtc IS NULL))
   OR (b.CostBasisKind = N'ZERO_COST_APPROVED'
       AND (ISNULL(b.CostEvidenceType, N'') <> N'ZERO_COST_APPROVED' OR b.FrozenFallbackUnitCost <> 0
            OR NULLIF(LTRIM(RTRIM(b.CostOverrideReason)), N'') IS NULL
            OR b.CostApprovedBy IS NULL OR b.CostApprovedAtUtc IS NULL));

INSERT INTO @Findings
SELECT h.CompanyCode, h.BranchCode, h.CountNo, l.ID,
       N'COUNT_GENERATION_CORRUPT',
       CONCAT(N'Expected one active leaf; found ', leaves.LeafCount, N'.')
FROM dbo.IvStockCountHdr h
JOIN dbo.IvStockCountLine l ON l.StockCountId = h.ID
CROSS APPLY
(
    SELECT COUNT(*) AS LeafCount
    FROM dbo.StockCountReplayGeneration g
    WHERE g.StockCountLineId = l.ID
      AND NOT EXISTS (SELECT 1 FROM dbo.StockCountReplayGeneration child
                      WHERE child.PreviousGenerationId = g.Id)
) leaves
WHERE EXISTS (SELECT 1 FROM dbo.StockCountReplayGeneration g WHERE g.StockCountLineId = l.ID)
  AND leaves.LeafCount <> 1;

INSERT INTO @Findings
SELECT h.CompanyCode, h.BranchCode, h.CountNo, l.ID,
       N'COUNT_ANCHOR_MISMATCH',
       CONCAT(N'Generation ', g.Id, N' anchor/tenant/sequence/effective date is invalid.')
FROM dbo.StockCountReplayGeneration g
JOIN dbo.IvStockCountLine l ON l.ID = g.StockCountLineId
JOIN dbo.IvStockCountHdr h ON h.ID = l.StockCountId
LEFT JOIN dbo.StockPosting p ON p.Id = g.AnchorPostingId
WHERE p.Id IS NULL OR p.SealedAtUtc IS NULL
   OR p.CompanyCode <> h.CompanyCode OR p.BranchCode <> h.BranchCode
   OR g.CompanyCode <> h.CompanyCode OR g.BranchCode <> h.BranchCode
   OR g.AnchorPostingSequence <> p.PostingSequence
   OR g.EffectiveAt <> DATEADD(NANOSECOND, -100, DATEADD(DAY, 1, CONVERT(datetime2(7), CONVERT(date, h.CountDate))));

INSERT INTO @Findings
SELECT h.CompanyCode, h.BranchCode, h.CountNo, l.ID,
       N'COUNT_GENERATION_HISTORY_INVALID',
       CONCAT(N'Generation ', g.Id, N' adjustment/reversal does not match its counted pile or parent.')
FROM dbo.StockCountReplayGeneration g
JOIN dbo.IvStockCountLine l ON l.ID = g.StockCountLineId
JOIN dbo.IvStockCountHdr h ON h.ID = l.StockCountId
LEFT JOIN dbo.IvTrxHistory a ON a.ID = g.AdjustmentHistoryId
LEFT JOIN dbo.IvTrxHistory r ON r.ID = g.ReversalHistoryId
LEFT JOIN dbo.StockCountReplayGeneration p ON p.Id = g.PreviousGenerationId
WHERE (g.SignedAdjustmentQty = 0 AND (g.AdjustmentHistoryId IS NOT NULL OR g.AdjustmentBatchNo IS NOT NULL))
   OR (g.SignedAdjustmentQty <> 0 AND (a.ID IS NULL OR g.AdjustmentBatchNo IS NULL
       OR a.CompanyCode <> h.CompanyCode OR a.BranchCode <> h.BranchCode
       OR ISNULL(a.EntryRole, N'') <> N'ORIGINAL' OR a.TrxType <> N'ADJ'
       OR (g.SignedAdjustmentQty > 0 AND
           (ISNULL(a.ToBalLocId, 0) <> l.BalLocId OR ISNULL(a.ToStdQty, -1) <> g.SignedAdjustmentQty))
       OR (g.SignedAdjustmentQty < 0 AND
           (ISNULL(a.FromBalLocId, 0) <> l.BalLocId OR ISNULL(a.FrStdQty, -1) <> -g.SignedAdjustmentQty))))
   OR (g.PreviousGenerationId IS NULL AND (g.ReversalHistoryId IS NOT NULL OR g.IsRemoval = 1))
   OR (g.PreviousGenerationId IS NOT NULL AND
       (p.Id IS NULL OR p.EffectiveAt <> g.EffectiveAt
        OR (p.IsRemoval = 0 AND
            (p.AnchorPostingId <> g.AnchorPostingId OR p.AnchorPostingSequence <> g.AnchorPostingSequence))
        OR (p.IsRemoval = 1 AND
            (p.AnchorPostingId = g.AnchorPostingId OR p.AnchorPostingSequence >= g.AnchorPostingSequence
             OR g.IsRemoval = 1))
        OR (p.AdjustmentHistoryId IS NULL AND g.ReversalHistoryId IS NOT NULL)
        OR (p.AdjustmentHistoryId IS NOT NULL AND
            (r.ID IS NULL OR ISNULL(r.ReversesHistoryId, 0) <> p.AdjustmentHistoryId
             OR ISNULL(r.EntryRole, N'') <> N'REVERSAL' OR r.CompanyCode <> h.CompanyCode
             OR r.BranchCode <> h.BranchCode))));

INSERT INTO @Findings
SELECT h.CompanyCode, h.BranchCode, h.CountNo, l.ID,
       N'COUNT_ACTIVE_STATUS_MISMATCH',
       CONCAT(N'Count status ', h.Status, N' disagrees with active generation ', leaf.Id, N'.')
FROM dbo.IvStockCountHdr h
JOIN dbo.IvStockCountLine l ON l.StockCountId = h.ID
JOIN dbo.StockCountReplayGeneration leaf ON leaf.StockCountLineId = l.ID
WHERE NOT EXISTS (SELECT 1 FROM dbo.StockCountReplayGeneration child WHERE child.PreviousGenerationId = leaf.Id)
  AND ((h.Status = N'POSTED' AND leaf.IsRemoval = 1)
       OR (h.Status = N'ROLLED_BACK' AND leaf.IsRemoval = 0)
       OR l.PhysicalQty IS NULL);

INSERT INTO @Findings
SELECT e.CompanyCode, e.BranchCode, NULL, NULL,
       N'REPLAY_EFFECT_OWNER_INVALID',
       CONCAT(N'Replay effect ', e.Id, N' root or business owner is inconsistent.')
FROM dbo.StockReplayEffect e
JOIN dbo.StockReplayRun run ON run.Id = e.ReplayRunId
JOIN dbo.StockPosting owner ON owner.Id = e.BusinessOwnerPostingId
WHERE run.RootStockPostingId <> e.RootStockPostingId
   OR run.CompanyCode <> e.CompanyCode OR run.BranchCode <> e.BranchCode
   OR owner.CompanyCode <> e.CompanyCode OR owner.BranchCode <> e.BranchCode
   OR owner.SealedAtUtc IS NULL;

SELECT CompanyCode, BranchCode, CountNo, StockCountLineId, FindingCode, Detail
FROM @Findings
ORDER BY CompanyCode, BranchCode, CountNo, StockCountLineId, FindingCode;

SELECT CompanyCode, BranchCode, FindingCode, COUNT(*) AS FindingCount
FROM @Findings
GROUP BY CompanyCode, BranchCode, FindingCode
ORDER BY CompanyCode, BranchCode, FindingCode;
