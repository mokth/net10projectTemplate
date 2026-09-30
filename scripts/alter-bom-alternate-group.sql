-- Adds AlternateGroupCode to Product Definition materials and Work Order material snapshots.
-- Backward-compatible: nullable column; legacy BomDefault=1 rows backfill to normalized ICode;
-- legacy BomDefault=0 rows remain NULL (not guessed into a group).
-- Run after alter-prdefbom-process-ownership.sql.
-- GO separators are required so ALTER TABLE is visible to later UPDATE/INDEX statements.

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF COL_LENGTH('dbo.PrDefBOM', 'AlternateGroupCode') IS NULL
BEGIN
    ALTER TABLE dbo.PrDefBOM ADD AlternateGroupCode nvarchar(30) NULL;
    PRINT N'Added dbo.PrDefBOM.AlternateGroupCode.';
END
ELSE
    PRINT N'dbo.PrDefBOM.AlternateGroupCode already exists.';
GO

-- Safe legacy backfill: defaults become singleton groups keyed by their item code.
UPDATE dbo.PrDefBOM
SET AlternateGroupCode = UPPER(LTRIM(RTRIM(ICode)))
WHERE BomDefault = 1
  AND (AlternateGroupCode IS NULL OR LTRIM(RTRIM(AlternateGroupCode)) = N'');
PRINT N'Backfilled default PrDefBOM.AlternateGroupCode rows.';
GO

IF OBJECT_ID(N'dbo.PrWorkOrderMaterial', N'U') IS NOT NULL
   AND COL_LENGTH('dbo.PrWorkOrderMaterial', 'AlternateGroupCode') IS NULL
BEGIN
    ALTER TABLE dbo.PrWorkOrderMaterial ADD AlternateGroupCode nvarchar(30) NULL;
    PRINT N'Added dbo.PrWorkOrderMaterial.AlternateGroupCode.';
END
ELSE IF OBJECT_ID(N'dbo.PrWorkOrderMaterial', N'U') IS NULL
    PRINT N'dbo.PrWorkOrderMaterial does not exist — skipped.';
ELSE
    PRINT N'dbo.PrWorkOrderMaterial.AlternateGroupCode already exists.';
GO

IF OBJECT_ID(N'dbo.PrDefBOM', N'U') IS NOT NULL
   AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_PrDefBOM_OneDefaultPerGroup'
      AND object_id = OBJECT_ID(N'dbo.PrDefBOM'))
BEGIN
    -- Skip index creation when legacy data would violate it (duplicate defaults with same group).
    IF NOT EXISTS (
        SELECT BomHdrId, OperationID, AlternateGroupCode
        FROM dbo.PrDefBOM
        WHERE BomDefault = 1
          AND OperationID IS NOT NULL
          AND AlternateGroupCode IS NOT NULL
        GROUP BY BomHdrId, OperationID, AlternateGroupCode
        HAVING COUNT(*) > 1)
    BEGIN
        CREATE UNIQUE INDEX UX_PrDefBOM_OneDefaultPerGroup
            ON dbo.PrDefBOM (BomHdrId, OperationID, AlternateGroupCode)
            WHERE BomDefault = 1
              AND OperationID IS NOT NULL
              AND AlternateGroupCode IS NOT NULL;
        PRINT N'Created UX_PrDefBOM_OneDefaultPerGroup.';
    END
    ELSE
        PRINT N'Skipped UX_PrDefBOM_OneDefaultPerGroup — resolve duplicate defaults first.';
END
ELSE IF EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_PrDefBOM_OneDefaultPerGroup'
      AND object_id = OBJECT_ID(N'dbo.PrDefBOM'))
    PRINT N'UX_PrDefBOM_OneDefaultPerGroup already exists.';
GO

PRINT N'alter-bom-alternate-group.sql complete.';
GO
