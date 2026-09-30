-- Product Definition header: legacy Prefix / Remark on PrBomHdr (nullable).
IF COL_LENGTH(N'dbo.PrBomHdr', N'Prefix') IS NULL
BEGIN
    ALTER TABLE dbo.PrBomHdr ADD Prefix nvarchar(10) NULL;
    PRINT N'Added dbo.PrBomHdr.Prefix.';
END
GO

IF COL_LENGTH(N'dbo.PrBomHdr', N'Remark') IS NULL
BEGIN
    ALTER TABLE dbo.PrBomHdr ADD Remark nvarchar(1000) NULL;
    PRINT N'Added dbo.PrBomHdr.Remark.';
END
GO
