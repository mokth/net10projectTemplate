-- Deployment artifact: add SQL Server rowversion concurrency tokens to inventory masters.
-- Do NOT run automatically at application startup. Apply via normal SQL deploy process.
--
-- Verification (expect 0 rows before apply; 8 rows after for the listed tables):
--   SELECT t.name AS TableName, c.name AS ColumnName, ty.name AS TypeName
--   FROM sys.tables t
--   JOIN sys.columns c ON c.object_id = t.object_id
--   JOIN sys.types ty ON ty.user_type_id = c.user_type_id
--   WHERE t.name IN (
--       N'IvStockMaster', N'IvWarehouse', N'IvLocation', N'IvStatus',
--       N'MsUOM', N'IvType', N'IvClass', N'IvSubClass')
--     AND (ty.name IN (N'timestamp', N'rowversion') OR c.name = N'RowVersion')
--   ORDER BY t.name;

-- Target database: use the same DB as ConnectionStrings:DefaultConnection (e.g. ERPWeb).
-- USE ERPWeb;
GO

IF COL_LENGTH('dbo.IvStockMaster', 'RowVersion') IS NULL
BEGIN
    ALTER TABLE dbo.IvStockMaster ADD RowVersion rowversion NOT NULL;
END
GO

IF COL_LENGTH('dbo.IvWarehouse', 'RowVersion') IS NULL
BEGIN
    ALTER TABLE dbo.IvWarehouse ADD RowVersion rowversion NOT NULL;
END
GO

IF COL_LENGTH('dbo.IvLocation', 'RowVersion') IS NULL
BEGIN
    ALTER TABLE dbo.IvLocation ADD RowVersion rowversion NOT NULL;
END
GO

IF COL_LENGTH('dbo.IvStatus', 'RowVersion') IS NULL
BEGIN
    ALTER TABLE dbo.IvStatus ADD RowVersion rowversion NOT NULL;
END
GO

IF COL_LENGTH('dbo.MsUOM', 'RowVersion') IS NULL
BEGIN
    ALTER TABLE dbo.MsUOM ADD RowVersion rowversion NOT NULL;
END
GO

IF COL_LENGTH('dbo.IvType', 'RowVersion') IS NULL
BEGIN
    ALTER TABLE dbo.IvType ADD RowVersion rowversion NOT NULL;
END
GO

IF COL_LENGTH('dbo.IvClass', 'RowVersion') IS NULL
BEGIN
    ALTER TABLE dbo.IvClass ADD RowVersion rowversion NOT NULL;
END
GO

IF COL_LENGTH('dbo.IvSubClass', 'RowVersion') IS NULL
BEGIN
    ALTER TABLE dbo.IvSubClass ADD RowVersion rowversion NOT NULL;
END
GO
