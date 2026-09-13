-- Seed AdSmNum / AdSmNumDate for Purchase Requisition (NumCd = 'PR').
-- Adjust CompanyCode / BranchCode to match your tenant before running.

DECLARE @Company nvarchar(10) = N'DEMO';
DECLARE @Branch nvarchar(10) = N'HQ';
DECLARE @Prefix nvarchar(20) = N'PR';
DECLARE @NumCd nvarchar(20) = N'PR';

IF NOT EXISTS (
    SELECT 1 FROM dbo.AdSmNum
    WHERE CompanyCode = @Company AND BranchCode = @Branch AND NumCd = @NumCd)
BEGIN
    INSERT INTO dbo.AdSmNum (CompanyCode, BranchCode, NumCd, Prefix, Seq, TotLength)
    VALUES (@Company, @Branch, @NumCd, @Prefix, 1, 4);
END

IF NOT EXISTS (
    SELECT 1 FROM dbo.AdSmNumDate
    WHERE CompanyCode = @Company AND BranchCode = @Branch AND NumCd = @NumCd
      AND ISNULL([Year], 0) = 0 AND ISNULL([Month], 0) = 0)
BEGIN
    INSERT INTO dbo.AdSmNumDate (CompanyCode, BranchCode, NumCd, Prefix, Seq, TotLength, [Year], [Month])
    VALUES (@Company, @Branch, @NumCd, @Prefix, 1, 4, 0, 0);
END
