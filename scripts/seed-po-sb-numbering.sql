-- Seed AdSmNumDate for the self-billed e-Invoice documents:
--   PO_SBI = Self-billed Invoice (LHDN 11)
--   PO_SBC = Self-billed Credit Note (LHDN 12)
--   PO_SBD = Self-billed Debit Note (LHDN 13)
-- Monthly DEMO pattern. Adjust CompanyCode / BranchCode / Year / Month to match your tenant.
--
-- Requires dbo.AdSmNumDate (run scripts/init-adsmnum.sql first if missing).
-- Do NOT also seed dbo.AdSmNum for the same NumCd when using AdSmNumDate (the date path wins).
-- QUOTED_IDENTIFIER ON is required because AdSmNumDate carries a filtered unique index.
--
-- Run after create-po-sb-invoice.sql / create-po-sb-cdn.sql. Idempotent.
GO

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

-- Self-billed Invoice
IF NOT EXISTS (
    SELECT 1 FROM dbo.AdSmNumDate
    WHERE CompanyCode = @Company
      AND BranchCode = @Branch
      AND NumCd = N'PO_SBI'
      AND [Year] = @Year
      AND [Month] = @Month)
BEGIN
    INSERT INTO dbo.AdSmNumDate (
        CompanyCode, BranchCode, LocationCode,
        [Year], [Month], NumCd, NumDes, TotLength, Prefix, Seq,
        Created, UserID, NumberingDelimeter, NumberingFormat)
    VALUES (
        @Company, @Branch, N'MAIN',
        @Year, @Month, N'PO_SBI', N'Self-billed Invoice', 4, N'SBI', 1,
        GETDATE(), N'SYSTEM', N'-', NULL);
    PRINT N'Seeded AdSmNumDate PO_SBI';
END
ELSE
    PRINT N'AdSmNumDate PO_SBI already seeded for this period';
GO

-- Self-billed Credit Note
DECLARE @Company nvarchar(10) = N'DEMO';
DECLARE @Branch nvarchar(10) = N'HQ';
DECLARE @Year smallint = 2026;
DECLARE @Month smallint = 9;

IF NOT EXISTS (
    SELECT 1 FROM dbo.AdSmNumDate
    WHERE CompanyCode = @Company
      AND BranchCode = @Branch
      AND NumCd = N'PO_SBC'
      AND [Year] = @Year
      AND [Month] = @Month)
BEGIN
    INSERT INTO dbo.AdSmNumDate (
        CompanyCode, BranchCode, LocationCode,
        [Year], [Month], NumCd, NumDes, TotLength, Prefix, Seq,
        Created, UserID, NumberingDelimeter, NumberingFormat)
    VALUES (
        @Company, @Branch, N'MAIN',
        @Year, @Month, N'PO_SBC', N'Self-billed Credit Note', 4, N'SBC', 1,
        GETDATE(), N'SYSTEM', N'-', NULL);
    PRINT N'Seeded AdSmNumDate PO_SBC';
END
ELSE
    PRINT N'AdSmNumDate PO_SBC already seeded for this period';
GO

-- Self-billed Debit Note
DECLARE @Company nvarchar(10) = N'DEMO';
DECLARE @Branch nvarchar(10) = N'HQ';
DECLARE @Year smallint = 2026;
DECLARE @Month smallint = 9;

IF NOT EXISTS (
    SELECT 1 FROM dbo.AdSmNumDate
    WHERE CompanyCode = @Company
      AND BranchCode = @Branch
      AND NumCd = N'PO_SBD'
      AND [Year] = @Year
      AND [Month] = @Month)
BEGIN
    INSERT INTO dbo.AdSmNumDate (
        CompanyCode, BranchCode, LocationCode,
        [Year], [Month], NumCd, NumDes, TotLength, Prefix, Seq,
        Created, UserID, NumberingDelimeter, NumberingFormat)
    VALUES (
        @Company, @Branch, N'MAIN',
        @Year, @Month, N'PO_SBD', N'Self-billed Debit Note', 4, N'SBD', 1,
        GETDATE(), N'SYSTEM', N'-', NULL);
    PRINT N'Seeded AdSmNumDate PO_SBD';
END
ELSE
    PRINT N'AdSmNumDate PO_SBD already seeded for this period';
GO

-- Verification: one row per module for this tenant/period.
SELECT NumCd, NumDes, Prefix, TotLength
FROM dbo.AdSmNumDate
WHERE CompanyCode = N'DEMO' AND BranchCode = N'HQ'
  AND NumCd IN (N'PO_SBI', N'PO_SBC', N'PO_SBD')
ORDER BY NumCd;
GO
