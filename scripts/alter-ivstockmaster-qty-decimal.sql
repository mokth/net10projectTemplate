-- Deployment artifact: align IvStockMaster qty columns with EF decimal(18,4) mapping.
-- Do NOT run automatically at application startup. Apply via normal SQL deploy process.
--
-- Problem: MinStock / MaxStock / StdPackSize / PurStdPackSize are float/real in legacy DBs.
-- EF maps them as decimal?, so list/get/save Reload throws:
--   InvalidCastException: Unable to cast object of type 'System.Double' to type 'System.Decimal'.
--
-- Verification (expect decimal for all four after apply):
--   SELECT c.name, t.name AS SqlType, c.precision, c.scale
--   FROM sys.columns c
--   JOIN sys.types t ON c.user_type_id = t.user_type_id
--   WHERE c.object_id = OBJECT_ID(N'dbo.IvStockMaster')
--     AND c.name IN (N'MinStock', N'MaxStock', N'StdPackSize', N'PurStdPackSize')
--   ORDER BY c.name;

-- Target database: use the same DB as ConnectionStrings:DefaultConnection (e.g. ERPWeb).
-- USE ERPWeb;
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.IvStockMaster')
      AND c.name = N'MinStock'
      AND t.name IN (N'float', N'real'))
BEGIN
    ALTER TABLE dbo.IvStockMaster ALTER COLUMN MinStock decimal(18, 4) NULL;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.IvStockMaster')
      AND c.name = N'MaxStock'
      AND t.name IN (N'float', N'real'))
BEGIN
    ALTER TABLE dbo.IvStockMaster ALTER COLUMN MaxStock decimal(18, 4) NULL;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.IvStockMaster')
      AND c.name = N'StdPackSize'
      AND t.name IN (N'float', N'real'))
BEGIN
    ALTER TABLE dbo.IvStockMaster ALTER COLUMN StdPackSize decimal(18, 4) NULL;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.IvStockMaster')
      AND c.name = N'PurStdPackSize'
      AND t.name IN (N'float', N'real'))
BEGIN
    ALTER TABLE dbo.IvStockMaster ALTER COLUMN PurStdPackSize decimal(18, 4) NULL;
END
GO
