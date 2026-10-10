USE ERPLiteEx;
GO

SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.IvStockMasterImage', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.IvStockMasterImage
        (
            UID         bigint IDENTITY(1,1) NOT NULL,
            CompanyCode nvarchar(5) NOT NULL,
            ICode       nvarchar(30) NOT NULL,
            ImagePath   nvarchar(500) NOT NULL,
            SortOrder   int NOT NULL,
            CreatedDate datetime2(7) NULL,
            CreatedBy   nvarchar(10) NULL,
            CONSTRAINT PK_IvStockMasterImage PRIMARY KEY CLUSTERED (UID),
            CONSTRAINT FK_IvStockMasterImage_IvStockMaster
                FOREIGN KEY (CompanyCode, ICode)
                REFERENCES dbo.IvStockMaster (CompanyCode, ICode)
                ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX UX_IvStockMasterImage_Company_Item_Path
            ON dbo.IvStockMasterImage (CompanyCode, ICode, ImagePath);
        CREATE UNIQUE INDEX UX_IvStockMasterImage_Company_Item_SortOrder
            ON dbo.IvStockMasterImage (CompanyCode, ICode, SortOrder);
        CREATE INDEX IX_IvStockMasterImage_Company_Item
            ON dbo.IvStockMasterImage (CompanyCode, ICode);

        INSERT INTO dbo.IvStockMasterImage
        (
            CompanyCode,
            ICode,
            ImagePath,
            SortOrder,
            CreatedDate,
            CreatedBy
        )
        SELECT
            m.CompanyCode,
            m.ICode,
            m.ImagePath,
            1,
            GETDATE(),
            N'MIGRATION'
        FROM dbo.IvStockMaster AS m
        WHERE NULLIF(LTRIM(RTRIM(m.ImagePath)), N'') IS NOT NULL;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO

-- Deployment verification: the first query must return zero rows.
SELECT m.CompanyCode, m.ICode, m.ImagePath
FROM dbo.IvStockMaster AS m
WHERE NULLIF(LTRIM(RTRIM(m.ImagePath)), N'') IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.IvStockMasterImage AS i
      WHERE i.CompanyCode = m.CompanyCode
        AND i.ICode = m.ICode
        AND i.ImagePath = m.ImagePath
  );

-- Both queries must return zero rows.
SELECT CompanyCode, ICode, COUNT(*) AS ImageCount
FROM dbo.IvStockMasterImage
GROUP BY CompanyCode, ICode
HAVING COUNT(*) > 6;

SELECT CompanyCode, ICode, ImagePath, COUNT(*) AS DuplicateCount
FROM dbo.IvStockMasterImage
GROUP BY CompanyCode, ICode, ImagePath
HAVING COUNT(*) > 1;
GO
