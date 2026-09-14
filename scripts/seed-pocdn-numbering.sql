-- Seed AdSmNumDate for Purchase Credit Note (NumCd = 'PCN') and
-- Purchase Debit Note (NumCd = 'PDN') - monthly DEMO pattern.
-- Adjust CompanyCode / BranchCode / Year / Month to match your tenant before running.
-- Requires dbo.AdSmNumDate (run scripts/init-adsmnum.sql first if missing).
-- Do NOT also seed AdSmNum for the same NumCd when using AdSmNumDate (date path wins).
-- QUOTED_IDENTIFIER ON is required because AdSmNumDate has a filtered unique index.
--
-- IMPORTANT (plan C-Numbering): PCN / PDN are deliberately NOT 'CN' / 'DN'.
--   AdSmNum already holds CN / DN for the SALES credit/debit notes, and the numbering
--   module is the document type (SaCdnService passes the doc type as the module).
--   Reusing CN/DN here would collide with the sales sequence.
--
-- Run after create-pocdn.sql. Idempotent.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @Company nvarchar(10) = N'DEMO';
DECLARE @Branch nvarchar(10) = N'HQ';
DECLARE @Year smallint = 2026;
DECLARE @Month smallint = 9;

IF OBJECT_ID(N'dbo.AdSmNumDate', N'U') IS NULL
BEGIN
    RAISERROR(N'dbo.AdSmNumDate is missing. Run scripts/init-adsmnum.sql first.', 16, 1);
    RETURN;
END

-- Purchase Credit Note
IF NOT EXISTS (
    SELECT 1 FROM dbo.AdSmNumDate
    WHERE CompanyCode = @Company
      AND BranchCode = @Branch
      AND NumCd = N'PCN'
      AND [Year] = @Year
      AND [Month] = @Month)
BEGIN
    INSERT INTO dbo.AdSmNumDate (
        CompanyCode, BranchCode, LocationCode,
        [Year], [Month], NumCd, NumDes, TotLength, Prefix, Seq,
        Created, UserID, NumberingDelimeter, NumberingFormat)
    VALUES (
        @Company, @Branch, N'MAIN',
        @Year, @Month, N'PCN', N'Purchase Credit Note', 4, N'PCN', 1,
        GETDATE(), N'SYSTEM', N'-', NULL);
    PRINT N'Seeded AdSmNumDate PCN';
END
ELSE
    PRINT N'AdSmNumDate PCN already seeded for this period';
GO

-- Purchase Debit Note
DECLARE @Company nvarchar(10) = N'DEMO';
DECLARE @Branch nvarchar(10) = N'HQ';
DECLARE @Year smallint = 2026;
DECLARE @Month smallint = 9;

IF NOT EXISTS (
    SELECT 1 FROM dbo.AdSmNumDate
    WHERE CompanyCode = @Company
      AND BranchCode = @Branch
      AND NumCd = N'PDN'
      AND [Year] = @Year
      AND [Month] = @Month)
BEGIN
    INSERT INTO dbo.AdSmNumDate (
        CompanyCode, BranchCode, LocationCode,
        [Year], [Month], NumCd, NumDes, TotLength, Prefix, Seq,
        Created, UserID, NumberingDelimeter, NumberingFormat)
    VALUES (
        @Company, @Branch, N'MAIN',
        @Year, @Month, N'PDN', N'Purchase Debit Note', 4, N'PDN', 1,
        GETDATE(), N'SYSTEM', N'-', NULL);
    PRINT N'Seeded AdSmNumDate PDN';
END
ELSE
    PRINT N'AdSmNumDate PDN already seeded for this period';
GO

PRINT N'seed-pocdn-numbering.sql complete.';
GO
