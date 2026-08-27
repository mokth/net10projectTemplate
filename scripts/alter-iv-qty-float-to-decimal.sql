-- Convert legacy inventory quantity columns from float/real to decimal(18,4).
-- Matches app model (decimal?) and inventory-stock-lot.md guidance.
-- Backup recommended before running: float→decimal can round values.
-- Run this script on each environment BEFORE deploying EF mapping that removes float converters.

-- IvTrxBatchDetail
IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.IvTrxBatchDetail')
      AND c.name = N'FrStdQty'
      AND t.name IN (N'float', N'real'))
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ALTER COLUMN FrStdQty decimal(18,4) NULL;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.IvTrxBatchDetail')
      AND c.name = N'FrPurQty'
      AND t.name IN (N'float', N'real'))
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ALTER COLUMN FrPurQty decimal(18,4) NULL;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.IvTrxBatchDetail')
      AND c.name = N'ToStdQty'
      AND t.name IN (N'float', N'real'))
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ALTER COLUMN ToStdQty decimal(18,4) NULL;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.IvTrxBatchDetail')
      AND c.name = N'ToPurQty'
      AND t.name IN (N'float', N'real'))
BEGIN
    ALTER TABLE dbo.IvTrxBatchDetail
        ALTER COLUMN ToPurQty decimal(18,4) NULL;
END
GO

-- IvTrxHistory
IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.IvTrxHistory')
      AND c.name = N'FrStdQty'
      AND t.name IN (N'float', N'real'))
BEGIN
    ALTER TABLE dbo.IvTrxHistory
        ALTER COLUMN FrStdQty decimal(18,4) NULL;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.IvTrxHistory')
      AND c.name = N'FrPurQty'
      AND t.name IN (N'float', N'real'))
BEGIN
    ALTER TABLE dbo.IvTrxHistory
        ALTER COLUMN FrPurQty decimal(18,4) NULL;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.IvTrxHistory')
      AND c.name = N'ToStdQty'
      AND t.name IN (N'float', N'real'))
BEGIN
    ALTER TABLE dbo.IvTrxHistory
        ALTER COLUMN ToStdQty decimal(18,4) NULL;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.IvTrxHistory')
      AND c.name = N'ToPurQty'
      AND t.name IN (N'float', N'real'))
BEGIN
    ALTER TABLE dbo.IvTrxHistory
        ALTER COLUMN ToPurQty decimal(18,4) NULL;
END
GO

-- IvBalLoc
IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.IvBalLoc')
      AND c.name = N'StdQty'
      AND t.name IN (N'float', N'real'))
BEGIN
    ALTER TABLE dbo.IvBalLoc
        ALTER COLUMN StdQty decimal(18,4) NULL;
END
GO
