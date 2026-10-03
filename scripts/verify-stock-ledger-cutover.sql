/*
  Stock-ledger cutover verification (READ ONLY).
  Manual DBA script — do NOT run at app startup.
  Does not modify data. Contains no credentials or connection strings.

  Checks:
    - Opening + signed V2 movements (OPENING_IN counted once) equals current
      production balances
    - Every sealed posting has at least one quantity leg, or is a zero-variance
      confirmation (OPENING with legs is allowed)
    - No unsealed StockPosting
    - An ACTIVE epoch exists
    - Mismatches are reported with finding codes

  Replace @CompanyCode / @BranchCode as needed.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @CompanyCode nvarchar(5) = N'DEMO';
DECLARE @BranchCode  nvarchar(5) = N'HQ';

PRINT N'=== verify-stock-ledger-cutover.sql (read-only) ===';
PRINT N'Company=' + @CompanyCode + N' Branch=' + @BranchCode;

IF OBJECT_ID(N'dbo.StockLedgerEpoch', N'U') IS NULL
   OR OBJECT_ID(N'dbo.StockPosting', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrProductionBalLot', N'U') IS NULL
   OR COL_LENGTH(N'dbo.PrProductionBalLotMovement', N'LedgerVersion') IS NULL
BEGIN
    SELECT N'SCHEMA_MISSING' AS FindingCode,
           N'Ledger schema incomplete. Run create/alter scripts and preflight first.' AS Detail;
    RETURN;
END;

/* ── Active epoch ─────────────────────────────────────────────────────────── */

SELECT
    N'ACTIVE_EPOCH' AS FindingCode,
    e.Id,
    e.Status,
    e.EffectiveFrom,
    e.CutoverPostingSequence,
    e.MigrationBatchId,
    e.ActivatedAtUtc,
    e.ActivatedBy
FROM dbo.StockLedgerEpoch e
WHERE e.CompanyCode = @CompanyCode
  AND e.BranchCode = @BranchCode
  AND e.Status = N'ACTIVE';

IF NOT EXISTS (
    SELECT 1
    FROM dbo.StockLedgerEpoch e
    WHERE e.CompanyCode = @CompanyCode
      AND e.BranchCode = @BranchCode
      AND e.Status = N'ACTIVE')
    SELECT N'NO_ACTIVE_EPOCH' AS FindingCode,
           N'No ACTIVE StockLedgerEpoch for this company/branch.' AS Detail;

/* ── Unsealed postings ────────────────────────────────────────────────────── */

SELECT
    N'UNSEALED_STOCK_POSTING' AS FindingCode,
    p.Id,
    p.CommandType,
    p.PostingRole,
    p.SourceModule,
    p.SourceDocumentType,
    p.SourceDocumentId,
    p.PostingSequence,
    p.PostedAtUtc
FROM dbo.StockPosting p
WHERE p.CompanyCode = @CompanyCode
  AND p.BranchCode = @BranchCode
  AND p.SealedAtUtc IS NULL;

/* ── Sealed posting must have a quantity leg or be a zero-variance confirm ── */

SELECT
    N'SEALED_POSTING_WITHOUT_LEGS' AS FindingCode,
    p.Id,
    p.CommandType,
    p.PostingRole,
    p.SourceModule,
    p.SourceDocumentType,
    p.SourceDocumentNo,
    p.PostingSequence,
    p.SealedAtUtc
FROM dbo.StockPosting p
WHERE p.CompanyCode = @CompanyCode
  AND p.BranchCode = @BranchCode
  AND p.SealedAtUtc IS NOT NULL
  AND p.CommandType NOT IN (N'COUNT_CONFIRM', N'COUNT_CONFIRMATION', N'ZERO_VARIANCE')
  AND p.PostingRole NOT IN (N'COUNT_CONFIRM', N'ZERO_VARIANCE')
  AND NOT EXISTS (
        SELECT 1
        FROM dbo.PrProductionBalLotMovement m
        WHERE m.StockPostingID = p.Id
          AND m.CompanyCode = p.CompanyCode
          AND m.BranchCode = p.BranchCode)
  AND NOT EXISTS (
        SELECT 1
        FROM dbo.IvTrxHistory h
        WHERE h.StockPostingID = p.Id
          AND h.CompanyCode = p.CompanyCode
          AND h.BranchCode = p.BranchCode);

/* ── Production: opening + later signed V2 = live BaseQty ─────────────────── */

;WITH SignedV2 AS
(
    SELECT
        m.ProductionBalLotID,
        m.MovementType,
        SignedBaseQty = ROUND(m.BaseQty *
            CASE m.MovementType
                WHEN N'OPENING_IN' THEN 1
                WHEN N'ISSUE' THEN 1
                WHEN N'ISSUE_REVERSAL' THEN -1
                WHEN N'PRODUCE' THEN 1
                WHEN N'PRODUCE_REVERSAL' THEN -1
                WHEN N'CONSUME' THEN -1
                WHEN N'CONSUME_REVERSAL' THEN 1
                WHEN N'RETURN' THEN -1
                WHEN N'RETURN_REVERSAL' THEN 1
                WHEN N'TRANSFER_OUT' THEN -1
                WHEN N'TRANSFER_IN' THEN 1
                WHEN N'STATUS_OUT' THEN -1
                WHEN N'STATUS_IN' THEN 1
                WHEN N'ADJUST_IN' THEN 1
                WHEN N'ADJUST_OUT' THEN -1
                WHEN N'SCRAP_OUT' THEN -1
                WHEN N'FG_RECEIPT_OUT' THEN -1
                ELSE NULL
            END, 4)
    FROM dbo.PrProductionBalLotMovement m
    INNER JOIN dbo.StockPosting p ON p.Id = m.StockPostingID
        AND p.CompanyCode = m.CompanyCode
        AND p.BranchCode = m.BranchCode
    WHERE m.LedgerVersion = 2
      AND m.CompanyCode = @CompanyCode
      AND m.BranchCode = @BranchCode
      AND p.SealedAtUtc IS NOT NULL
),
LotLedger AS
(
    SELECT
        lot.UID AS ProductionBalLotID,
        lot.Kind,
        lot.ItemCode,
        lot.WorkOrderNo,
        lot.Qty,
        lot.BaseQty AS LiveBaseQty,
        OpeningBaseQty = ISNULL((
            SELECT SUM(s.SignedBaseQty)
            FROM SignedV2 s
            WHERE s.ProductionBalLotID = lot.UID
              AND s.MovementType = N'OPENING_IN'), 0),
        LaterSignedBaseQty = ISNULL((
            SELECT SUM(s.SignedBaseQty)
            FROM SignedV2 s
            WHERE s.ProductionBalLotID = lot.UID
              AND s.MovementType <> N'OPENING_IN'), 0),
        UnknownMovementCount = ISNULL((
            SELECT COUNT(*)
            FROM SignedV2 s
            WHERE s.ProductionBalLotID = lot.UID
              AND s.SignedBaseQty IS NULL), 0)
    FROM dbo.PrProductionBalLot lot
    WHERE lot.CompanyCode = @CompanyCode
      AND lot.BranchCode = @BranchCode
)
SELECT
    N'PRODUCTION_LEDGER_MISMATCH' AS FindingCode,
    l.ProductionBalLotID,
    l.Kind,
    l.ItemCode,
    l.WorkOrderNo,
    l.LiveBaseQty,
    l.OpeningBaseQty,
    l.LaterSignedBaseQty,
    LedgerBaseQty = ROUND(l.OpeningBaseQty + l.LaterSignedBaseQty, 4),
    DeltaBaseQty = ROUND(l.LiveBaseQty - (l.OpeningBaseQty + l.LaterSignedBaseQty), 4),
    l.UnknownMovementCount
FROM LotLedger l
WHERE l.UnknownMovementCount > 0
   OR ROUND(l.LiveBaseQty - (l.OpeningBaseQty + l.LaterSignedBaseQty), 4) <> 0;

SELECT
    N'PRODUCTION_LEDGER_MATCH_SUMMARY' AS FindingCode,
    COUNT(*) AS LotCount,
    SUM(CASE WHEN ROUND(l.LiveBaseQty - (l.OpeningBaseQty + l.LaterSignedBaseQty), 4) = 0
              AND l.UnknownMovementCount = 0 THEN 1 ELSE 0 END) AS MatchedLots,
    SUM(CASE WHEN ROUND(l.LiveBaseQty - (l.OpeningBaseQty + l.LaterSignedBaseQty), 4) <> 0
               OR l.UnknownMovementCount > 0 THEN 1 ELSE 0 END) AS MismatchedLots
FROM
(
    SELECT
        lot.UID,
        lot.BaseQty AS LiveBaseQty,
        OpeningBaseQty = ISNULL((
            SELECT SUM(ROUND(m.BaseQty, 4))
            FROM dbo.PrProductionBalLotMovement m
            INNER JOIN dbo.StockPosting p ON p.Id = m.StockPostingID
            WHERE m.ProductionBalLotID = lot.UID
              AND m.LedgerVersion = 2
              AND m.MovementType = N'OPENING_IN'
              AND p.SealedAtUtc IS NOT NULL), 0),
        LaterSignedBaseQty = ISNULL((
            SELECT SUM(ROUND(m.BaseQty *
                CASE m.MovementType
                    WHEN N'ISSUE' THEN 1
                    WHEN N'ISSUE_REVERSAL' THEN -1
                    WHEN N'PRODUCE' THEN 1
                    WHEN N'PRODUCE_REVERSAL' THEN -1
                    WHEN N'CONSUME' THEN -1
                    WHEN N'CONSUME_REVERSAL' THEN 1
                    WHEN N'RETURN' THEN -1
                    WHEN N'RETURN_REVERSAL' THEN 1
                    WHEN N'TRANSFER_OUT' THEN -1
                    WHEN N'TRANSFER_IN' THEN 1
                    WHEN N'STATUS_OUT' THEN -1
                    WHEN N'STATUS_IN' THEN 1
                    WHEN N'ADJUST_IN' THEN 1
                    WHEN N'ADJUST_OUT' THEN -1
                    WHEN N'SCRAP_OUT' THEN -1
                    WHEN N'FG_RECEIPT_OUT' THEN -1
                    ELSE 0
                END, 4))
            FROM dbo.PrProductionBalLotMovement m
            INNER JOIN dbo.StockPosting p ON p.Id = m.StockPostingID
            WHERE m.ProductionBalLotID = lot.UID
              AND m.LedgerVersion = 2
              AND m.MovementType <> N'OPENING_IN'
              AND p.SealedAtUtc IS NOT NULL), 0),
        UnknownMovementCount = ISNULL((
            SELECT COUNT(*)
            FROM dbo.PrProductionBalLotMovement m
            WHERE m.ProductionBalLotID = lot.UID
              AND m.LedgerVersion = 2
              AND m.MovementType NOT IN (
                    N'OPENING_IN', N'ISSUE', N'ISSUE_REVERSAL', N'PRODUCE', N'PRODUCE_REVERSAL',
                    N'CONSUME', N'CONSUME_REVERSAL', N'RETURN', N'RETURN_REVERSAL',
                    N'TRANSFER_OUT', N'TRANSFER_IN', N'STATUS_OUT', N'STATUS_IN',
                    N'ADJUST_IN', N'ADJUST_OUT', N'SCRAP_OUT', N'FG_RECEIPT_OUT')), 0)
    FROM dbo.PrProductionBalLot lot
    WHERE lot.CompanyCode = @CompanyCode
      AND lot.BranchCode = @BranchCode
) l;

/* ── Inventory opening + later V2 legs vs live StdQty (informational) ─────── */

IF OBJECT_ID(N'dbo.IvBalLoc', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.IvTrxHistory', N'LedgerVersion') IS NOT NULL
   AND COL_LENGTH(N'dbo.IvTrxHistory', N'EntryRole') IS NOT NULL
BEGIN
    SELECT
        N'INVENTORY_LEDGER_MISMATCH' AS FindingCode,
        b.ID AS BalLocId,
        b.ICode,
        b.WHCode,
        b.LocCode,
        b.LotNo,
        b.StdQty AS LiveStdQty,
        OpeningQty,
        LaterSignedQty,
        LedgerQty = ROUND(OpeningQty + LaterSignedQty, 4),
        DeltaQty = ROUND(b.StdQty - (OpeningQty + LaterSignedQty), 4)
    FROM dbo.IvBalLoc b
    CROSS APPLY
    (
        SELECT
            OpeningQty = ISNULL((
                SELECT SUM(ISNULL(h.ToStdQty, 0))
                FROM dbo.IvTrxHistory h
                INNER JOIN dbo.StockPosting p ON p.Id = h.StockPostingID
                    AND p.CompanyCode = h.CompanyCode AND p.BranchCode = h.BranchCode
                WHERE h.LedgerVersion = 2
                  AND h.EntryRole = N'OPENING'
                  AND h.ToBalLocId = b.ID
                  AND p.SealedAtUtc IS NOT NULL), 0),
            LaterSignedQty =
                ISNULL((
                    SELECT SUM(ISNULL(h.ToStdQty, 0))
                    FROM dbo.IvTrxHistory h
                    INNER JOIN dbo.StockPosting p ON p.Id = h.StockPostingID
                        AND p.CompanyCode = h.CompanyCode AND p.BranchCode = h.BranchCode
                    WHERE h.LedgerVersion = 2
                      AND ISNULL(h.EntryRole, N'') <> N'OPENING'
                      AND h.ToBalLocId = b.ID
                      AND p.SealedAtUtc IS NOT NULL), 0)
                - ISNULL((
                    SELECT SUM(ISNULL(h.FrStdQty, 0))
                    FROM dbo.IvTrxHistory h
                    INNER JOIN dbo.StockPosting p ON p.Id = h.StockPostingID
                        AND p.CompanyCode = h.CompanyCode AND p.BranchCode = h.BranchCode
                    WHERE h.LedgerVersion = 2
                      AND ISNULL(h.EntryRole, N'') <> N'OPENING'
                      AND h.FromBalLocId = b.ID
                      AND p.SealedAtUtc IS NOT NULL), 0)
    ) led
    WHERE b.CompanyCode = @CompanyCode
      AND b.BranchCode = @BranchCode
      AND ROUND(b.StdQty - (OpeningQty + LaterSignedQty), 4) <> 0;
END;

PRINT N'verify-stock-ledger-cutover.sql complete (read-only).';
