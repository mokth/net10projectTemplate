-- READ-ONLY preflight for PrShiftBreak cutover.
-- Prefer running after Phase A schema (BreakStorageVersion + PrShiftBreak exist).
-- Safe before Phase A: version-dependent sections are skipped when the column is missing.
-- Does not modify data. Operator must repair any blocking rows before Phase C promotion.

SET NOCOUNT ON;

PRINT N'=== PrShiftBreak preflight ===';

IF OBJECT_ID(N'dbo.PrShift', N'U') IS NULL
BEGIN
    PRINT N'BLOCKING: dbo.PrShift does not exist.';
    RETURN;
END

PRINT N'-- Duplicate Shift_Cd (blocks UX_PrShift_ShiftCd_CompCode)';
SELECT Shift_Cd, COUNT(*) AS DupCount
FROM dbo.PrShift
GROUP BY Shift_Cd
HAVING COUNT(*) > 1;

PRINT N'-- Blank / NULL CompCode (blocking for version=0 promotion)';
SELECT Shift_Cd, CompCode
FROM dbo.PrShift
WHERE CompCode IS NULL OR LTRIM(RTRIM(CompCode)) = N'';

PRINT N'-- One-sided legacy break pairs (blocking)';
;WITH WidePairs AS (
    SELECT s.CompCode, s.Shift_Cd, v.Seq, v.BreakFrom, v.BreakTo
    FROM dbo.PrShift s
    CROSS APPLY (VALUES
        (1, s.Break_Tm1From, s.Break_Tm1To),
        (2, s.Break_Tm2From, s.Break_Tm2To),
        (3, s.Break_Tm3From, s.Break_Tm3To),
        (4, s.Break_Tm4From, s.Break_Tm4To),
        (5, s.Break_Tm5From, s.Break_Tm5To)
    ) v(Seq, BreakFrom, BreakTo)
)
SELECT CompCode, Shift_Cd, Seq, BreakFrom, BreakTo
FROM WidePairs
WHERE (BreakFrom IS NULL AND BreakTo IS NOT NULL)
   OR (BreakFrom IS NOT NULL AND BreakTo IS NULL);

IF COL_LENGTH(N'dbo.PrShift', N'BreakStorageVersion') IS NULL
BEGIN
    PRINT N'-- BreakStorageVersion missing: run Phase A alter-pr-shift-break.sql first.';
    PRINT N'-- Skipping used-slot / authority summary that require the column.';
END
ELSE
BEGIN
    PRINT N'-- Blank CompCode on version=0 rows (blocking for promotion)';
    SELECT Shift_Cd, CompCode, BreakStorageVersion
    FROM dbo.PrShift
    WHERE BreakStorageVersion = 0
      AND (CompCode IS NULL OR LTRIM(RTRIM(CompCode)) = N'');

    PRINT N'-- Used legacy slots (excluding NULL/NULL and 00:00-00:00 sentinel)';
    ;WITH WidePairs AS (
        SELECT s.CompCode, s.Shift_Cd, s.BreakStorageVersion, v.Seq, v.BreakFrom, v.BreakTo
        FROM dbo.PrShift s
        CROSS APPLY (VALUES
            (1, s.Break_Tm1From, s.Break_Tm1To),
            (2, s.Break_Tm2From, s.Break_Tm2To),
            (3, s.Break_Tm3From, s.Break_Tm3To),
            (4, s.Break_Tm4From, s.Break_Tm4To),
            (5, s.Break_Tm5From, s.Break_Tm5To)
        ) v(Seq, BreakFrom, BreakTo)
    )
    SELECT CompCode, Shift_Cd, BreakStorageVersion, Seq,
           CONVERT(time(0), BreakFrom) AS BreakFromTime,
           CONVERT(time(0), BreakTo) AS BreakToTime
    FROM WidePairs
    WHERE BreakFrom IS NOT NULL
      AND BreakTo IS NOT NULL
      AND NOT (CONVERT(time(0), BreakFrom) = '00:00' AND CONVERT(time(0), BreakTo) = '00:00')
    ORDER BY CompCode, Shift_Cd, Seq;

    PRINT N'-- Authority summary';
    SELECT BreakStorageVersion, COUNT(*) AS ShiftCount
    FROM dbo.PrShift
    GROUP BY BreakStorageVersion
    ORDER BY BreakStorageVersion;
END

IF OBJECT_ID(N'dbo.PrShiftBreak', N'U') IS NOT NULL
BEGIN
    PRINT N'-- Existing PrShiftBreak row counts by shift';
    SELECT CompCode, Shift_Cd, COUNT(*) AS BreakCount
    FROM dbo.PrShiftBreak
    GROUP BY CompCode, Shift_Cd
    ORDER BY CompCode, Shift_Cd;

    PRINT N'-- Orphan child rows (parent missing)';
    SELECT b.CompCode, b.Shift_Cd, b.BreakSeq
    FROM dbo.PrShiftBreak b
    WHERE NOT EXISTS (
        SELECT 1
        FROM dbo.PrShift s
        WHERE s.CompCode = b.CompCode
          AND s.Shift_Cd = b.Shift_Cd
    );

    PRINT N'-- FK cascade expected: FK_PrShiftBreak_PrShift ON DELETE CASCADE';
    SELECT fk.name AS ForeignKeyName,
           fk.delete_referential_action_desc AS OnDelete,
           fk.is_disabled,
           fk.is_not_trusted
    FROM sys.foreign_keys fk
    WHERE fk.parent_object_id = OBJECT_ID(N'dbo.PrShiftBreak')
      AND fk.name = N'FK_PrShiftBreak_PrShift';
END
ELSE
    PRINT N'dbo.PrShiftBreak does not exist yet (run Phase A schema first).';

PRINT N'=== End preflight. Repair BLOCKING rows before Phase C. ===';
PRINT N'Legacy writers of Break_Tm1..5 must be retired/blocked/synchronized after version promotion.';
PRINT N'Classic WorkShift may READ after cutover; independent break WRITES are out of scope for this release.';
GO
