-- Multi-level BOM: PrBomHdr + PrDefBOM.BomHdrId/SeqNo/ScrapPercent + IvStockMaster.MfgType.
-- Manual DBA script — do NOT run at app startup.
-- Idempotent: safe to re-run.
-- Requires dbo.PrDefBOM (see create-prdefbom.sql).

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ─── PrBomHdr ───────────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.PrBomHdr', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrBomHdr (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        ProdCode nvarchar(30) NOT NULL,
        Version int NOT NULL CONSTRAINT DF_PrBomHdr_Version DEFAULT (1),
        Status nvarchar(20) NOT NULL CONSTRAINT DF_PrBomHdr_Status DEFAULT (N'ACTIVE'),
        EffectiveFrom datetime2 NULL,
        EffectiveTo datetime2 NULL,
        BaseQty decimal(18,4) NOT NULL CONSTRAINT DF_PrBomHdr_BaseQty DEFAULT (1),
        BaseUOM nvarchar(10) NULL,
        BranchCode nvarchar(5) NULL,
        LocationCode nvarchar(10) NULL,
        Created datetime2 NULL,
        UserID nvarchar(10) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PrBomHdr PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT UQ_PrBomHdr_Company_Prod_Version UNIQUE (CompanyCode, ProdCode, Version),
        CONSTRAINT CK_PrBomHdr_BaseQty_Positive CHECK (BaseQty > 0),
        CONSTRAINT CK_PrBomHdr_Status CHECK (Status IN (N'DRAFT', N'ACTIVE', N'SUPERSEDED', N'INACTIVE'))
    );

    CREATE INDEX IX_PrBomHdr_Company_Prod_Status
        ON dbo.PrBomHdr (CompanyCode, ProdCode, Status);

    PRINT N'Created dbo.PrBomHdr.';
END
ELSE
    PRINT N'dbo.PrBomHdr already exists — left unchanged.';
GO

-- ─── Backfill headers for existing single-level BOMs ────────────────────────
IF OBJECT_ID(N'dbo.PrDefBOM', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.PrBomHdr', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrDefBOM', N'BomHdrId') IS NULL
BEGIN
    INSERT INTO dbo.PrBomHdr (CompanyCode, ProdCode, Version, Status, EffectiveFrom, BaseQty, BaseUOM, BranchCode, LocationCode, Created, UserID, Updated, UpdatedUID)
    SELECT
        b.CompanyCode,
        b.ProdCode,
        1,
        N'ACTIVE',
        CAST(SYSUTCDATETIME() AS date),
        1,
        (SELECT TOP 1 s.StdUOM FROM dbo.IvStockMaster s
         WHERE s.CompanyCode = b.CompanyCode AND s.ICode = b.ProdCode),
        MAX(b.BranchCode),
        MAX(b.LocationCode),
        MIN(b.Created),
        MIN(b.UserID),
        MAX(b.Updated),
        MAX(b.UpdatedUID)
    FROM dbo.PrDefBOM b
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.PrBomHdr h
        WHERE h.CompanyCode = b.CompanyCode AND h.ProdCode = b.ProdCode AND h.Version = 1)
    GROUP BY b.CompanyCode, b.ProdCode;

    PRINT N'Backfilled PrBomHdr Version=1 for existing PrDefBOM products.';
END
GO

-- ─── PrDefBOM.BomHdrId ──────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.PrDefBOM', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrDefBOM', N'BomHdrId') IS NULL
BEGIN
    ALTER TABLE dbo.PrDefBOM ADD BomHdrId bigint NULL;
    PRINT N'Added dbo.PrDefBOM.BomHdrId (nullable for backfill).';
END
GO

IF OBJECT_ID(N'dbo.PrDefBOM', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrDefBOM', N'BomHdrId') IS NOT NULL
BEGIN
    UPDATE b
    SET BomHdrId = h.UID
    FROM dbo.PrDefBOM b
    INNER JOIN dbo.PrBomHdr h
        ON h.CompanyCode = b.CompanyCode
       AND h.ProdCode = b.ProdCode
       AND h.Version = 1
    WHERE b.BomHdrId IS NULL;

    PRINT N'Backfilled PrDefBOM.BomHdrId.';
END
GO

-- ─── SeqNo / ScrapPercent ───────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.PrDefBOM', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrDefBOM', N'SeqNo') IS NULL
BEGIN
    ALTER TABLE dbo.PrDefBOM ADD SeqNo int NOT NULL
        CONSTRAINT DF_PrDefBOM_SeqNo DEFAULT (0);
    PRINT N'Added dbo.PrDefBOM.SeqNo.';
END
GO

IF OBJECT_ID(N'dbo.PrDefBOM', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrDefBOM', N'SeqNo') IS NOT NULL
BEGIN
    ;WITH ranked AS (
        SELECT UID,
               ROW_NUMBER() OVER (PARTITION BY CompanyCode, BomHdrId ORDER BY ICode) AS rn
        FROM dbo.PrDefBOM
        WHERE SeqNo = 0
    )
    UPDATE r SET SeqNo = ranked.rn
    FROM dbo.PrDefBOM r
    INNER JOIN ranked ON ranked.UID = r.UID;
END
GO

-- ADD column and CHECK must be separate batches (SQL Server compiles CHECK against current schema).
IF OBJECT_ID(N'dbo.PrDefBOM', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrDefBOM', N'ScrapPercent') IS NULL
BEGIN
    ALTER TABLE dbo.PrDefBOM ADD ScrapPercent decimal(18,4) NOT NULL
        CONSTRAINT DF_PrDefBOM_ScrapPercent DEFAULT (0);
    PRINT N'Added dbo.PrDefBOM.ScrapPercent.';
END
GO

IF OBJECT_ID(N'dbo.PrDefBOM', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrDefBOM', N'ScrapPercent') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.check_constraints
       WHERE name = N'CK_PrDefBOM_ScrapPercent_NonNegative'
         AND parent_object_id = OBJECT_ID(N'dbo.PrDefBOM'))
BEGIN
    ALTER TABLE dbo.PrDefBOM WITH CHECK
        ADD CONSTRAINT CK_PrDefBOM_ScrapPercent_NonNegative CHECK (ScrapPercent >= 0);
    PRINT N'Added CK_PrDefBOM_ScrapPercent_NonNegative.';
END
GO

-- ─── Enforce BomHdrId NOT NULL + FK + new unique ─────────────────────────────
IF OBJECT_ID(N'dbo.PrDefBOM', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PrDefBOM', N'BomHdrId') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM dbo.PrDefBOM WHERE BomHdrId IS NULL)
BEGIN
    -- Drop old unique if present
    IF EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE name = N'UQ_PrDefBOM_Company_Prod_ICode'
          AND object_id = OBJECT_ID(N'dbo.PrDefBOM'))
    BEGIN
        ALTER TABLE dbo.PrDefBOM DROP CONSTRAINT UQ_PrDefBOM_Company_Prod_ICode;
        PRINT N'Dropped UQ_PrDefBOM_Company_Prod_ICode.';
    END

    -- Make BomHdrId NOT NULL (if still nullable)
    IF EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.PrDefBOM')
          AND name = N'BomHdrId'
          AND is_nullable = 1)
    BEGIN
        ALTER TABLE dbo.PrDefBOM ALTER COLUMN BomHdrId bigint NOT NULL;
        PRINT N'PrDefBOM.BomHdrId set NOT NULL.';
    END

    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys
        WHERE name = N'FK_PrDefBOM_PrBomHdr'
          AND parent_object_id = OBJECT_ID(N'dbo.PrDefBOM'))
    BEGIN
        ALTER TABLE dbo.PrDefBOM
            ADD CONSTRAINT FK_PrDefBOM_PrBomHdr
            FOREIGN KEY (BomHdrId) REFERENCES dbo.PrBomHdr (UID)
            ON DELETE CASCADE;
        PRINT N'Added FK_PrDefBOM_PrBomHdr.';
    END

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE name = N'UQ_PrDefBOM_Company_Hdr_ICode'
          AND object_id = OBJECT_ID(N'dbo.PrDefBOM'))
    BEGIN
        CREATE UNIQUE INDEX UQ_PrDefBOM_Company_Hdr_ICode
            ON dbo.PrDefBOM (CompanyCode, BomHdrId, ICode);
        PRINT N'Created UQ_PrDefBOM_Company_Hdr_ICode.';
    END

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE name = N'IX_PrDefBOM_BomHdrId'
          AND object_id = OBJECT_ID(N'dbo.PrDefBOM'))
    BEGIN
        CREATE INDEX IX_PrDefBOM_BomHdrId ON dbo.PrDefBOM (BomHdrId);
    END
END
GO

-- ─── IvStockMaster.MfgType ──────────────────────────────────────────────────
-- ADD column and CHECK must be separate batches (SQL Server compiles CHECK against current schema).
IF OBJECT_ID(N'dbo.IvStockMaster', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.IvStockMaster', N'MfgType') IS NULL
BEGIN
    ALTER TABLE dbo.IvStockMaster ADD MfgType nvarchar(10) NOT NULL
        CONSTRAINT DF_IvStockMaster_MfgType DEFAULT (N'BUY');
    PRINT N'Added dbo.IvStockMaster.MfgType.';
END
GO

IF OBJECT_ID(N'dbo.IvStockMaster', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.IvStockMaster', N'MfgType') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.check_constraints
       WHERE name = N'CK_IvStockMaster_MfgType'
         AND parent_object_id = OBJECT_ID(N'dbo.IvStockMaster'))
BEGIN
    ALTER TABLE dbo.IvStockMaster WITH CHECK
        ADD CONSTRAINT CK_IvStockMaster_MfgType CHECK (MfgType IN (N'BUY', N'MAKE', N'PHANTOM'));
    PRINT N'Added CK_IvStockMaster_MfgType.';
END
GO

IF OBJECT_ID(N'dbo.IvStockMaster', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.IvStockMaster', N'MfgType') IS NOT NULL
   AND OBJECT_ID(N'dbo.PrBomHdr', N'U') IS NOT NULL
BEGIN
    UPDATE s
    SET MfgType = N'MAKE'
    FROM dbo.IvStockMaster s
    WHERE EXISTS (
        SELECT 1 FROM dbo.PrBomHdr h
        WHERE h.CompanyCode = s.CompanyCode AND h.ProdCode = s.ICode)
      AND s.MfgType = N'BUY';

    PRINT N'Set MfgType=MAKE for items that own a BOM header.';
END
GO

PRINT N'alter-prdefbom-multilevel.sql complete.';
GO
