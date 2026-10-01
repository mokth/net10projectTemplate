-- Multiple Product Definitions per Product + Work Order Definition Selection.
-- Sequence: run after Product Definition Phase 1 / Work Order schemas exist.
-- Manual DBA script — do NOT run at app startup.
-- Fail-fast: aborts when unexpected multi-ACTIVE Product Definitions exist.
-- Does NOT drop EffectiveFrom/EffectiveTo/SnapshotAsOfDate/SourceEffectiveFrom
-- (retained for historical hash-version-1 compatibility).
--
-- Optional repair (commented): keep highest Version ACTIVE when multi-ACTIVE exists.
-- Do not uncomment against production-like data without deliberate review.

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.PrBomHdr', N'U') IS NULL
    THROW 53000, 'dbo.PrBomHdr is required.', 1;
IF OBJECT_ID(N'dbo.PrDefBOM', N'U') IS NULL
    THROW 53001, 'dbo.PrDefBOM is required.', 1;
GO

-- ═══════════════════════════════════════════════════════════════════════════
-- Stage A — PrBomHdr DefinitionCode / Name / IsDefaultDefinition
-- ═══════════════════════════════════════════════════════════════════════════

IF COL_LENGTH(N'dbo.PrBomHdr', N'DefinitionCode') IS NULL
BEGIN
    ALTER TABLE dbo.PrBomHdr ADD DefinitionCode nvarchar(30) NULL;
    PRINT N'Added dbo.PrBomHdr.DefinitionCode (nullable for backfill).';
END
ELSE PRINT N'dbo.PrBomHdr.DefinitionCode already exists.';
GO

IF COL_LENGTH(N'dbo.PrBomHdr', N'DefinitionName') IS NULL
BEGIN
    ALTER TABLE dbo.PrBomHdr ADD DefinitionName nvarchar(100) NULL;
    PRINT N'Added dbo.PrBomHdr.DefinitionName.';
END
ELSE PRINT N'dbo.PrBomHdr.DefinitionName already exists.';
GO

IF COL_LENGTH(N'dbo.PrBomHdr', N'IsDefaultDefinition') IS NULL
BEGIN
    ALTER TABLE dbo.PrBomHdr ADD IsDefaultDefinition bit NOT NULL
        CONSTRAINT DF_PrBomHdr_IsDefaultDefinition DEFAULT (0);
    PRINT N'Added dbo.PrBomHdr.IsDefaultDefinition.';
END
ELSE PRINT N'dbo.PrBomHdr.IsDefaultDefinition already exists.';
GO

-- Fail-fast: more than one ACTIVE revision per product (pre multi-definition world).
IF EXISTS (
    SELECT CompanyCode, ProdCode
    FROM dbo.PrBomHdr
    WHERE Status = N'ACTIVE'
    GROUP BY CompanyCode, ProdCode
    HAVING COUNT(*) > 1
)
BEGIN
    SELECT CompanyCode, ProdCode, COUNT(*) AS ActiveCount,
           STRING_AGG(CAST(Version AS nvarchar(10)), N', ') WITHIN GROUP (ORDER BY Version) AS Versions
    FROM dbo.PrBomHdr
    WHERE Status = N'ACTIVE'
    GROUP BY CompanyCode, ProdCode
    HAVING COUNT(*) > 1;

    THROW 53010,
        N'Migration cannot continue: multiple ACTIVE PrBomHdr rows exist for one or more products. Resolve Product Definition status before migration. See result set for CompanyCode/ProdCode/Versions.',
        1;
END
GO

-- /*
-- Optional deliberate repair (dev only): keep highest Version ACTIVE, supersede others.
-- UPDATE h
-- SET Status = N'SUPERSEDED',
--     Updated = SYSUTCDATETIME(),
--     UpdatedUID = N'MIGRATE'
-- FROM dbo.PrBomHdr h
-- INNER JOIN (
--     SELECT CompanyCode, ProdCode, MAX(Version) AS KeepVersion
--     FROM dbo.PrBomHdr
--     WHERE Status = N'ACTIVE'
--     GROUP BY CompanyCode, ProdCode
--     HAVING COUNT(*) > 1
-- ) d ON d.CompanyCode = h.CompanyCode AND d.ProdCode = h.ProdCode
-- WHERE h.Status = N'ACTIVE' AND h.Version <> d.KeepVersion;
-- */

UPDATE dbo.PrBomHdr
SET DefinitionCode = N'STANDARD',
    DefinitionName = COALESCE(DefinitionName, N'Standard Production')
WHERE DefinitionCode IS NULL OR LTRIM(RTRIM(DefinitionCode)) = N'';
PRINT N'Backfilled PrBomHdr.DefinitionCode = STANDARD.';
GO

-- Mark the retained ACTIVE STANDARD definition as default per product.
UPDATE h
SET IsDefaultDefinition = 1
FROM dbo.PrBomHdr h
WHERE h.Status = N'ACTIVE'
  AND h.DefinitionCode = N'STANDARD'
  AND NOT EXISTS (
        SELECT 1
        FROM dbo.PrBomHdr x
        WHERE x.CompanyCode = h.CompanyCode
          AND x.ProdCode = h.ProdCode
          AND x.Status = N'ACTIVE'
          AND x.IsDefaultDefinition = 1
          AND x.UID <> h.UID);
PRINT N'Marked ACTIVE STANDARD definitions as default where missing.';
GO

IF EXISTS (
    SELECT 1 FROM dbo.PrBomHdr
    WHERE DefinitionCode IS NULL OR LTRIM(RTRIM(DefinitionCode)) = N''
)
    THROW 53011, 'PrBomHdr.DefinitionCode still null/blank after backfill.', 1;
GO

-- Enforce NOT NULL on DefinitionCode.
IF EXISTS (
    SELECT 1
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'dbo.PrBomHdr')
      AND c.name = N'DefinitionCode'
      AND c.is_nullable = 1
)
BEGIN
    ALTER TABLE dbo.PrBomHdr ALTER COLUMN DefinitionCode nvarchar(30) NOT NULL;
    PRINT N'Altered dbo.PrBomHdr.DefinitionCode to NOT NULL.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_PrBomHdr_DefinitionCode_NotBlank'
      AND parent_object_id = OBJECT_ID(N'dbo.PrBomHdr')
)
BEGIN
    ALTER TABLE dbo.PrBomHdr WITH CHECK
    ADD CONSTRAINT CK_PrBomHdr_DefinitionCode_NotBlank
        CHECK (LEN(LTRIM(RTRIM(DefinitionCode))) > 0);
    PRINT N'Added CK_PrBomHdr_DefinitionCode_NotBlank.';
END
GO

-- Align Status default to DRAFT for new greenfield inserts.
IF EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE name = N'DF_PrBomHdr_Status'
      AND parent_object_id = OBJECT_ID(N'dbo.PrBomHdr')
)
BEGIN
    ALTER TABLE dbo.PrBomHdr DROP CONSTRAINT DF_PrBomHdr_Status;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE name = N'DF_PrBomHdr_Status'
      AND parent_object_id = OBJECT_ID(N'dbo.PrBomHdr')
)
BEGIN
    ALTER TABLE dbo.PrBomHdr
        ADD CONSTRAINT DF_PrBomHdr_Status DEFAULT (N'DRAFT') FOR Status;
    PRINT N'Set DF_PrBomHdr_Status default to DRAFT.';
END
GO

-- Replace uniqueness: (Company, Prod, Version) → (Company, Prod, Definition, Version)
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_PrBomHdr_Company_Prod_Version'
      AND object_id = OBJECT_ID(N'dbo.PrBomHdr')
)
BEGIN
    ALTER TABLE dbo.PrBomHdr DROP CONSTRAINT UQ_PrBomHdr_Company_Prod_Version;
    PRINT N'Dropped UQ_PrBomHdr_Company_Prod_Version.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_PrBomHdr_Company_Prod_Definition_Version'
      AND object_id = OBJECT_ID(N'dbo.PrBomHdr')
)
BEGIN
    ALTER TABLE dbo.PrBomHdr
        ADD CONSTRAINT UQ_PrBomHdr_Company_Prod_Definition_Version
            UNIQUE (CompanyCode, ProdCode, DefinitionCode, Version);
    PRINT N'Created UQ_PrBomHdr_Company_Prod_Definition_Version.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UX_PrBomHdr_OneActiveRevision'
      AND object_id = OBJECT_ID(N'dbo.PrBomHdr')
)
BEGIN
    CREATE UNIQUE INDEX UX_PrBomHdr_OneActiveRevision
        ON dbo.PrBomHdr (CompanyCode, ProdCode, DefinitionCode)
        WHERE Status = N'ACTIVE';
    PRINT N'Created UX_PrBomHdr_OneActiveRevision.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UX_PrBomHdr_OneActiveDefault'
      AND object_id = OBJECT_ID(N'dbo.PrBomHdr')
)
BEGIN
    CREATE UNIQUE INDEX UX_PrBomHdr_OneActiveDefault
        ON dbo.PrBomHdr (CompanyCode, ProdCode)
        WHERE Status = N'ACTIVE' AND IsDefaultDefinition = 1;
    PRINT N'Created UX_PrBomHdr_OneActiveDefault.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_PrBomHdr_Company_Prod_Definition_Status'
      AND object_id = OBJECT_ID(N'dbo.PrBomHdr')
)
BEGIN
    CREATE INDEX IX_PrBomHdr_Company_Prod_Definition_Status
        ON dbo.PrBomHdr (CompanyCode, ProdCode, DefinitionCode, Status);
    PRINT N'Created IX_PrBomHdr_Company_Prod_Definition_Status.';
END
GO

-- ═══════════════════════════════════════════════════════════════════════════
-- Stage B — PrDefBOM.ComponentDefinitionCode
-- ═══════════════════════════════════════════════════════════════════════════

IF COL_LENGTH(N'dbo.PrDefBOM', N'ComponentDefinitionCode') IS NULL
BEGIN
    ALTER TABLE dbo.PrDefBOM ADD ComponentDefinitionCode nvarchar(30) NULL;
    PRINT N'Added dbo.PrDefBOM.ComponentDefinitionCode.';
END
ELSE PRINT N'dbo.PrDefBOM.ComponentDefinitionCode already exists.';
GO

-- Backfill SEPARATE lines from component default ACTIVE definition (normally STANDARD).
;WITH ComponentDefault AS (
    SELECT CompanyCode, ProdCode, DefinitionCode,
           ROW_NUMBER() OVER (
               PARTITION BY CompanyCode, ProdCode
               ORDER BY CASE WHEN IsDefaultDefinition = 1 THEN 0 ELSE 1 END, Version DESC
           ) AS rn
    FROM dbo.PrBomHdr
    WHERE Status = N'ACTIVE'
)
UPDATE b
SET ComponentDefinitionCode = d.DefinitionCode
FROM dbo.PrDefBOM b
INNER JOIN ComponentDefault d
    ON d.CompanyCode = b.CompanyCode
   AND d.ProdCode = b.ICode
   AND d.rn = 1
WHERE b.SupplySource = N'SEPARATE_PRODUCT_DEFINITION'
  AND (b.ComponentDefinitionCode IS NULL OR LTRIM(RTRIM(b.ComponentDefinitionCode)) = N'');
PRINT N'Backfilled SEPARATE ComponentDefinitionCode from component default ACTIVE.';
GO

IF EXISTS (
    SELECT 1
    FROM dbo.PrDefBOM b
    WHERE b.SupplySource = N'SEPARATE_PRODUCT_DEFINITION'
      AND (b.ComponentDefinitionCode IS NULL OR LTRIM(RTRIM(b.ComponentDefinitionCode)) = N'')
)
BEGIN
    SELECT b.UID, b.CompanyCode, b.ProdCode, b.ICode, b.SupplySource
    FROM dbo.PrDefBOM b
    WHERE b.SupplySource = N'SEPARATE_PRODUCT_DEFINITION'
      AND (b.ComponentDefinitionCode IS NULL OR LTRIM(RTRIM(b.ComponentDefinitionCode)) = N'');

    THROW 53020,
        N'SEPARATE_PRODUCT_DEFINITION lines lack ComponentDefinitionCode and no ACTIVE child definition was found. See result set.',
        1;
END
GO

-- Clear ComponentDefinitionCode on non-SEPARATE rows (defensive).
UPDATE dbo.PrDefBOM
SET ComponentDefinitionCode = NULL
WHERE SupplySource <> N'SEPARATE_PRODUCT_DEFINITION'
  AND ComponentDefinitionCode IS NOT NULL;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_PrDefBOM_ComponentDefinitionCode'
      AND parent_object_id = OBJECT_ID(N'dbo.PrDefBOM')
)
BEGIN
    ALTER TABLE dbo.PrDefBOM WITH CHECK
    ADD CONSTRAINT CK_PrDefBOM_ComponentDefinitionCode CHECK (
        (SupplySource = N'SEPARATE_PRODUCT_DEFINITION' AND ComponentDefinitionCode IS NOT NULL)
        OR (SupplySource <> N'SEPARATE_PRODUCT_DEFINITION' AND ComponentDefinitionCode IS NULL)
    );
    PRINT N'Added CK_PrDefBOM_ComponentDefinitionCode.';
END
GO

-- ═══════════════════════════════════════════════════════════════════════════
-- Stage C — PrWorkOrder SourceDefinition* + material ComponentDefinitionCode
-- ═══════════════════════════════════════════════════════════════════════════

IF OBJECT_ID(N'dbo.PrWorkOrder', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrWorkOrder', N'SourceDefinitionCode') IS NULL
BEGIN
    ALTER TABLE dbo.PrWorkOrder ADD SourceDefinitionCode nvarchar(30) NULL;
    PRINT N'Added dbo.PrWorkOrder.SourceDefinitionCode (nullable for backfill).';
END
GO

IF OBJECT_ID(N'dbo.PrWorkOrder', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrWorkOrder', N'SourceDefinitionName') IS NULL
BEGIN
    ALTER TABLE dbo.PrWorkOrder ADD SourceDefinitionName nvarchar(100) NULL;
    PRINT N'Added dbo.PrWorkOrder.SourceDefinitionName.';
END
GO

IF OBJECT_ID(N'dbo.PrWorkOrder', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrWorkOrder', N'SourceDefinitionCode') IS NOT NULL
BEGIN
    UPDATE wo
    SET SourceDefinitionCode = COALESCE(NULLIF(LTRIM(RTRIM(h.DefinitionCode)), N''), N'STANDARD'),
        SourceDefinitionName = COALESCE(wo.SourceDefinitionName, h.DefinitionName)
    FROM dbo.PrWorkOrder wo
    INNER JOIN dbo.PrBomHdr h ON h.UID = wo.SourceBomHdrID
    WHERE wo.SourceDefinitionCode IS NULL OR LTRIM(RTRIM(wo.SourceDefinitionCode)) = N'';

    UPDATE dbo.PrWorkOrder
    SET SourceDefinitionCode = N'STANDARD'
    WHERE SourceDefinitionCode IS NULL OR LTRIM(RTRIM(SourceDefinitionCode)) = N'';

    IF EXISTS (
        SELECT 1 FROM dbo.PrWorkOrder
        WHERE SourceDefinitionCode IS NULL OR LTRIM(RTRIM(SourceDefinitionCode)) = N''
    )
        THROW 53030, 'PrWorkOrder.SourceDefinitionCode still null/blank after backfill.', 1;

    IF EXISTS (
        SELECT 1
        FROM sys.columns c
        WHERE c.object_id = OBJECT_ID(N'dbo.PrWorkOrder')
          AND c.name = N'SourceDefinitionCode'
          AND c.is_nullable = 1
    )
    BEGIN
        ALTER TABLE dbo.PrWorkOrder ALTER COLUMN SourceDefinitionCode nvarchar(30) NOT NULL;
        PRINT N'Altered dbo.PrWorkOrder.SourceDefinitionCode to NOT NULL.';
    END
END
GO

IF OBJECT_ID(N'dbo.PrWorkOrder', N'U') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE name = N'IX_PrWorkOrder_Company_Product_Definition'
          AND object_id = OBJECT_ID(N'dbo.PrWorkOrder')
   )
BEGIN
    CREATE INDEX IX_PrWorkOrder_Company_Product_Definition
        ON dbo.PrWorkOrder (CompanyCode, ProductCode, SourceDefinitionCode);
    PRINT N'Created IX_PrWorkOrder_Company_Product_Definition.';
END
GO

IF OBJECT_ID(N'dbo.PrWorkOrderMaterial', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrWorkOrderMaterial', N'ComponentDefinitionCode') IS NULL
BEGIN
    ALTER TABLE dbo.PrWorkOrderMaterial ADD ComponentDefinitionCode nvarchar(30) NULL;
    PRINT N'Added dbo.PrWorkOrderMaterial.ComponentDefinitionCode.';
END
GO

IF OBJECT_ID(N'dbo.PrWorkOrderMaterial', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrWorkOrderMaterial', N'ComponentDefinitionCode') IS NOT NULL
BEGIN
    UPDATE m
    SET ComponentDefinitionCode = b.ComponentDefinitionCode
    FROM dbo.PrWorkOrderMaterial m
    INNER JOIN dbo.PrDefBOM b ON b.UID = m.SourceBomLineID
    WHERE m.ComponentDefinitionCode IS NULL
      AND b.ComponentDefinitionCode IS NOT NULL;
    PRINT N'Backfilled PrWorkOrderMaterial.ComponentDefinitionCode from source BOM lines.';
END
GO

PRINT N'alter-product-definition-multiple-definitions.sql completed. Date columns retained for hash compatibility.';
GO
