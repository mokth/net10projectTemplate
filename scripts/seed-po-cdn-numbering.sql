-- Seed AdSmNumDate for Purchase Invoice / CN (NumCd = 'POCDN') - monthly DEMO pattern.
-- Adjust CompanyCode / BranchCode / Year / Month to match your tenant before running.
-- Requires dbo.AdSmNumDate (run scripts/init-adsmnum.sql first if missing).
-- Do NOT also seed AdSmNum for the same NumCd when using AdSmNumDate (date path wins).
-- QUOTED_IDENTIFIER ON is required because AdSmNumDate has a filtered unique index.

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

IF NOT EXISTS (
    SELECT 1 FROM dbo.AdSmNumDate
    WHERE CompanyCode = @Company
      AND BranchCode = @Branch
      AND NumCd = N'POCDN'
      AND [Year] = @Year
      AND [Month] = @Month)
BEGIN
    INSERT INTO dbo.AdSmNumDate (
        CompanyCode, BranchCode, LocationCode,
        [Year], [Month], NumCd, NumDes, TotLength, Prefix, Seq,
        Created, UserID, NumberingDelimeter, NumberingFormat)
    VALUES (
        @Company, @Branch, N'MAIN',
        @Year, @Month, N'POCDN', N'Purchase Invoice', 4, N'PINV', 1,
        GETDATE(), N'SYSTEM', N'-', NULL);
END
GO
