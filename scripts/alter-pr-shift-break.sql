-- Dynamic shift breaks: additive schema plus optional promotion from Break_Tm1..5 to PrShiftBreak.
--
-- Deployment sequence (see docs/shift-groups-study.md §2.10):
--   0) scripts/preflight-pr-shift-break.sql          -- read-only anomaly report
--   A) @PromoteBreakStorage = 0                      -- schema only (version stays 0)
--   B) classic WorkShift/ShiftGroup smoke at v0
--   C) freeze edits; @PromoteBreakStorage = 1         -- remigrate version=0 (replace children + repack wide)
--   D) deploy child-aware ErpWeb; classic READ only for breaks
-- Rollback: scripts/rollback-pr-shift-break-authority.sql then remigrate before redeploy.
--
-- IMPORTANT: GO separators are required so ALTER TABLE ADD BreakStorageVersion is visible
-- to later batches (SQL Server compiles each batch separately).
--
-- Phase A schema smoke: leave @PromoteBreakStorage = 0 in the promote batch below.
-- Phase C migration/remigration: set @PromoteBreakStorage = 1 after freezing shift edits.

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.PrShift', N'U') IS NULL
    THROW 51000, 'dbo.PrShift is required before running alter-pr-shift-break.sql.', 1;
GO

IF COL_LENGTH(N'dbo.PrShift', N'BreakStorageVersion') IS NULL
BEGIN
    ALTER TABLE dbo.PrShift ADD BreakStorageVersion tinyint NOT NULL
        CONSTRAINT DF_PrShift_BreakStorageVersion DEFAULT (0);
    PRINT N'Added dbo.PrShift.BreakStorageVersion.';
END
ELSE
    PRINT N'dbo.PrShift.BreakStorageVersion already exists.';
GO

IF EXISTS (
    SELECT Shift_Cd
    FROM dbo.PrShift
    GROUP BY Shift_Cd
    HAVING COUNT(*) > 1
)
    THROW 51001, 'Duplicate PrShift.Shift_Cd values block the required composite unique index.', 1;
GO

IF EXISTS (
    SELECT 1
    FROM dbo.PrShift
    WHERE BreakStorageVersion = 0
      AND (CompCode IS NULL OR LTRIM(RTRIM(CompCode)) = N'')
)
    THROW 51002, 'Break migration found PrShift rows with blank CompCode. Repair tenant data first.', 1;
GO

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
    WHERE ISNULL(s.BreakStorageVersion, 0) = 0
)
SELECT CompCode, Shift_Cd, Seq, BreakFrom, BreakTo
INTO #OneSidedBreaks
FROM WidePairs
WHERE (BreakFrom IS NULL AND BreakTo IS NOT NULL)
   OR (BreakFrom IS NOT NULL AND BreakTo IS NULL);

IF EXISTS (SELECT 1 FROM #OneSidedBreaks)
BEGIN
    SELECT * FROM #OneSidedBreaks;
    THROW 51003, 'One-sided legacy break pairs found. Repair before promotion.', 1;
END
DROP TABLE #OneSidedBreaks;
GO

-- Schema objects + optional promotion (single batch so @Promote / @Midnight stay in scope)
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @PromoteBreakStorage bit = 0; -- set to 1 only during the frozen Phase C promotion/remigration
DECLARE @Midnight datetime = CONVERT(datetime, CONVERT(date, GETDATE()));

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.PrShiftBreak', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrShiftBreak
    (
        CompCode nvarchar(10) NOT NULL,
        Shift_Cd nvarchar(10) NOT NULL,
        BreakSeq tinyint NOT NULL,
        BreakFrom datetime NOT NULL,
        BreakTo datetime NOT NULL,
        Created datetime NULL,
        UserID nvarchar(10) NULL,
        CONSTRAINT PK_PrShiftBreak PRIMARY KEY (CompCode, Shift_Cd, BreakSeq),
        CONSTRAINT CK_PrShiftBreak_BreakSeq CHECK (BreakSeq BETWEEN 1 AND 5)
    );
    PRINT N'Created dbo.PrShiftBreak.';
END
ELSE
    PRINT N'dbo.PrShiftBreak already exists.';

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PrShift')
      AND name = N'UX_PrShift_ShiftCd_CompCode'
)
BEGIN
    CREATE UNIQUE INDEX UX_PrShift_ShiftCd_CompCode ON dbo.PrShift (Shift_Cd, CompCode);
    PRINT N'Created UX_PrShift_ShiftCd_CompCode.';
END
ELSE
    PRINT N'UX_PrShift_ShiftCd_CompCode already exists.';

IF NOT EXISTS (
    SELECT 1
    FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID(N'dbo.PrShiftBreak')
      AND name = N'FK_PrShiftBreak_PrShift'
)
BEGIN
    ALTER TABLE dbo.PrShiftBreak WITH CHECK
    ADD CONSTRAINT FK_PrShiftBreak_PrShift
    FOREIGN KEY (Shift_Cd, CompCode)
    REFERENCES dbo.PrShift (Shift_Cd, CompCode)
    ON DELETE CASCADE;
    PRINT N'Created FK_PrShiftBreak_PrShift ON DELETE CASCADE.';
END
ELSE
    PRINT N'FK_PrShiftBreak_PrShift already exists.';

IF @PromoteBreakStorage = 1
BEGIN
    IF OBJECT_ID(N'tempdb..#CanonicalBreaks') IS NOT NULL DROP TABLE #CanonicalBreaks;

    ;WITH SourceRows AS (
        SELECT s.CompCode, s.Shift_Cd, s.Start_Tm, s.End_Tm,
               StartMin = DATEDIFF(MINUTE, CONVERT(date, s.Start_Tm), s.Start_Tm),
               EndMin0 = DATEDIFF(MINUTE, CONVERT(date, s.End_Tm), s.End_Tm),
               v.Seq, v.BreakFrom, v.BreakTo
        FROM dbo.PrShift s
        CROSS APPLY (VALUES
            (1, s.Break_Tm1From, s.Break_Tm1To),
            (2, s.Break_Tm2From, s.Break_Tm2To),
            (3, s.Break_Tm3From, s.Break_Tm3To),
            (4, s.Break_Tm4From, s.Break_Tm4To),
            (5, s.Break_Tm5From, s.Break_Tm5To)
        ) v(Seq, BreakFrom, BreakTo)
        WHERE s.BreakStorageVersion = 0
          AND s.CompCode IS NOT NULL
          AND LTRIM(RTRIM(s.CompCode)) <> N''
          AND s.Start_Tm IS NOT NULL
          AND s.End_Tm IS NOT NULL
    ),
    UsedBreaks AS (
        SELECT *,
               EndMin = CASE WHEN EndMin0 < StartMin THEN EndMin0 + 1440 ELSE EndMin0 END,
               BreakStart0 = DATEDIFF(MINUTE, CONVERT(date, BreakFrom), BreakFrom),
               BreakEnd0 = DATEDIFF(MINUTE, CONVERT(date, BreakTo), BreakTo)
        FROM SourceRows
        WHERE BreakFrom IS NOT NULL
          AND BreakTo IS NOT NULL
          AND NOT (CONVERT(time(0), BreakFrom) = '00:00' AND CONVERT(time(0), BreakTo) = '00:00')
    ),
    Normalized AS (
        SELECT CompCode, Shift_Cd, Seq, StartMin, EndMin, EndMin0,
               BreakStart = CASE WHEN EndMin0 < StartMin AND BreakStart0 < StartMin THEN BreakStart0 + 1440 ELSE BreakStart0 END,
               BreakEnd = CASE
                   WHEN BreakEnd0 < BreakStart0 THEN BreakEnd0 + 1440
                   WHEN EndMin0 < StartMin AND BreakStart0 < StartMin THEN BreakEnd0 + 1440
                   ELSE BreakEnd0
               END
        FROM UsedBreaks
    ),
    Validated AS (
        SELECT CompCode, Shift_Cd, StartMin, EndMin, BreakStart, BreakEnd, Seq,
               BreakSeq = ROW_NUMBER() OVER (PARTITION BY CompCode, Shift_Cd ORDER BY BreakStart, BreakEnd, Seq)
        FROM Normalized
    )
    SELECT CompCode, Shift_Cd, StartMin, EndMin, BreakSeq, BreakStart, BreakEnd
    INTO #CanonicalBreaks
    FROM Validated;

    IF EXISTS (SELECT 1 FROM #CanonicalBreaks WHERE BreakStart = BreakEnd)
        THROW 51004, 'Zero-duration legacy break found. 00:00-00:00 is the only unused sentinel.', 1;

    IF EXISTS (SELECT 1 FROM #CanonicalBreaks WHERE BreakStart < StartMin OR BreakEnd > EndMin)
        THROW 51005, 'Legacy break outside shift span found.', 1;

    IF EXISTS (
        SELECT 1
        FROM (
            SELECT *, PrevEnd = LAG(BreakEnd) OVER (PARTITION BY CompCode, Shift_Cd ORDER BY BreakStart, BreakEnd)
            FROM #CanonicalBreaks
        ) q
        WHERE PrevEnd IS NOT NULL AND BreakStart < PrevEnd
    )
        THROW 51006, 'Overlapping legacy breaks found.', 1;

    IF EXISTS (
        SELECT 1
        FROM dbo.PrShift s
        OUTER APPLY (
            SELECT BreakMinutes = SUM(c.BreakEnd - c.BreakStart)
            FROM #CanonicalBreaks c
            WHERE c.CompCode = s.CompCode AND c.Shift_Cd = s.Shift_Cd
        ) b
        CROSS APPLY (
            SELECT StartMin = DATEDIFF(MINUTE, CONVERT(date, s.Start_Tm), s.Start_Tm),
                   EndMin0 = DATEDIFF(MINUTE, CONVERT(date, s.End_Tm), s.End_Tm)
        ) t
        CROSS APPLY (
            SELECT EndMin = CASE WHEN t.EndMin0 < t.StartMin THEN t.EndMin0 + 1440 ELSE t.EndMin0 END
        ) n
        WHERE s.BreakStorageVersion = 0
          AND n.EndMin - t.StartMin - ISNULL(b.BreakMinutes, 0) <= 0
    )
        THROW 51007, 'Net shift minutes would be zero or negative after break migration.', 1;

    DELETE b
    FROM dbo.PrShiftBreak b
    INNER JOIN dbo.PrShift s
        ON s.CompCode = b.CompCode
       AND s.Shift_Cd = b.Shift_Cd
       AND s.BreakStorageVersion = 0;

    INSERT dbo.PrShiftBreak (CompCode, Shift_Cd, BreakSeq, BreakFrom, BreakTo, Created, UserID)
    SELECT c.CompCode,
           c.Shift_Cd,
           CONVERT(tinyint, c.BreakSeq),
           DATEADD(MINUTE, c.BreakStart % 1440, @Midnight),
           DATEADD(MINUTE, c.BreakEnd % 1440, @Midnight),
           GETDATE(),
           NULL
    FROM #CanonicalBreaks c;

    ;WITH Packed AS (
        SELECT s.CompCode, s.Shift_Cd,
               B1F = MAX(CASE WHEN c.BreakSeq = 1 THEN DATEADD(MINUTE, c.BreakStart % 1440, @Midnight) END),
               B1T = MAX(CASE WHEN c.BreakSeq = 1 THEN DATEADD(MINUTE, c.BreakEnd % 1440, @Midnight) END),
               B2F = MAX(CASE WHEN c.BreakSeq = 2 THEN DATEADD(MINUTE, c.BreakStart % 1440, @Midnight) END),
               B2T = MAX(CASE WHEN c.BreakSeq = 2 THEN DATEADD(MINUTE, c.BreakEnd % 1440, @Midnight) END),
               B3F = MAX(CASE WHEN c.BreakSeq = 3 THEN DATEADD(MINUTE, c.BreakStart % 1440, @Midnight) END),
               B3T = MAX(CASE WHEN c.BreakSeq = 3 THEN DATEADD(MINUTE, c.BreakEnd % 1440, @Midnight) END),
               B4F = MAX(CASE WHEN c.BreakSeq = 4 THEN DATEADD(MINUTE, c.BreakStart % 1440, @Midnight) END),
               B4T = MAX(CASE WHEN c.BreakSeq = 4 THEN DATEADD(MINUTE, c.BreakEnd % 1440, @Midnight) END),
               B5F = MAX(CASE WHEN c.BreakSeq = 5 THEN DATEADD(MINUTE, c.BreakStart % 1440, @Midnight) END),
               B5T = MAX(CASE WHEN c.BreakSeq = 5 THEN DATEADD(MINUTE, c.BreakEnd % 1440, @Midnight) END)
        FROM dbo.PrShift s
        LEFT JOIN #CanonicalBreaks c ON c.CompCode = s.CompCode AND c.Shift_Cd = s.Shift_Cd
        WHERE s.BreakStorageVersion = 0
        GROUP BY s.CompCode, s.Shift_Cd
    )
    UPDATE s
    SET Break_Tm1From = ISNULL(p.B1F, @Midnight),
        Break_Tm1To = ISNULL(p.B1T, @Midnight),
        Break_Tm2From = ISNULL(p.B2F, @Midnight),
        Break_Tm2To = ISNULL(p.B2T, @Midnight),
        Break_Tm3From = ISNULL(p.B3F, @Midnight),
        Break_Tm3To = ISNULL(p.B3T, @Midnight),
        Break_Tm4From = ISNULL(p.B4F, @Midnight),
        Break_Tm4To = ISNULL(p.B4T, @Midnight),
        Break_Tm5From = ISNULL(p.B5F, @Midnight),
        Break_Tm5To = ISNULL(p.B5T, @Midnight),
        BreakStorageVersion = 1
    FROM dbo.PrShift s
    INNER JOIN Packed p ON p.CompCode = s.CompCode AND p.Shift_Cd = s.Shift_Cd;

    PRINT N'Promotion completed: BreakStorageVersion set to 1 for migrated shifts.';
END
ELSE
    PRINT N'Phase A only: Promotion skipped (@PromoteBreakStorage = 0).';

COMMIT;

PRINT N'PrShiftBreak schema ensured. Promotion setting:';
SELECT @PromoteBreakStorage AS PromoteBreakStorage;
GO
