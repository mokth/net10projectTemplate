-- Product Definition / BOM master (PrBomHdr + PrDefBOM) — greenfield create.
-- Manual DBA script — do NOT run at app startup.
-- Idempotent: safe to re-run.
-- For upgrading an existing PrDefBOM without header, run alter-prdefbom-multilevel.sql instead.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.PrBomHdr', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrBomHdr (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        ProdCode nvarchar(30) NOT NULL,
        DefinitionCode nvarchar(30) NOT NULL,
        DefinitionName nvarchar(100) NULL,
        IsDefaultDefinition bit NOT NULL CONSTRAINT DF_PrBomHdr_IsDefaultDefinition DEFAULT (0),
        Version int NOT NULL CONSTRAINT DF_PrBomHdr_Version DEFAULT (1),
        Status nvarchar(20) NOT NULL CONSTRAINT DF_PrBomHdr_Status DEFAULT (N'DRAFT'),
        -- Deprecated: retained for historical hash-version-1 compatibility only.
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
        CONSTRAINT UQ_PrBomHdr_Company_Prod_Definition_Version UNIQUE (CompanyCode, ProdCode, DefinitionCode, Version),
        CONSTRAINT CK_PrBomHdr_BaseQty_Positive CHECK (BaseQty > 0),
        CONSTRAINT CK_PrBomHdr_Status CHECK (Status IN (N'DRAFT', N'ACTIVE', N'SUPERSEDED', N'INACTIVE')),
        CONSTRAINT CK_PrBomHdr_DefinitionCode_NotBlank CHECK (LEN(LTRIM(RTRIM(DefinitionCode))) > 0)
    );

    CREATE UNIQUE INDEX UX_PrBomHdr_OneActiveRevision
        ON dbo.PrBomHdr (CompanyCode, ProdCode, DefinitionCode)
        WHERE Status = N'ACTIVE';

    CREATE UNIQUE INDEX UX_PrBomHdr_OneActiveDefault
        ON dbo.PrBomHdr (CompanyCode, ProdCode)
        WHERE Status = N'ACTIVE' AND IsDefaultDefinition = 1;

    CREATE INDEX IX_PrBomHdr_Company_Prod_Definition_Status
        ON dbo.PrBomHdr (CompanyCode, ProdCode, DefinitionCode, Status);

    CREATE INDEX IX_PrBomHdr_Company_Prod_Status
        ON dbo.PrBomHdr (CompanyCode, ProdCode, Status);

    PRINT N'Created dbo.PrBomHdr.';
END
ELSE
    PRINT N'dbo.PrBomHdr already exists — left unchanged.';
GO

IF OBJECT_ID(N'dbo.PrDefBOM', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrDefBOM (
        UID bigint IDENTITY(1,1) NOT NULL,
        BomHdrId bigint NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        ProdCode nvarchar(30) NOT NULL,
        ICode nvarchar(30) NOT NULL,
        IName nvarchar(200) NULL,
        StdQty decimal(18,4) NOT NULL,
        StdUOM nvarchar(10) NULL,
        SeqNo int NOT NULL CONSTRAINT DF_PrDefBOM_SeqNo DEFAULT (0),
        ScrapPercent decimal(18,4) NOT NULL CONSTRAINT DF_PrDefBOM_ScrapPercent DEFAULT (0),
        Warehouse nvarchar(20) NULL,
        BomDefault bit NOT NULL CONSTRAINT DF_PrDefBOM_BomDefault DEFAULT (1),
        WIPBomDefault bit NOT NULL CONSTRAINT DF_PrDefBOM_WIPBomDefault DEFAULT (0),
        Tolerance decimal(18,4) NOT NULL CONSTRAINT DF_PrDefBOM_Tolerance DEFAULT (0),
        BranchCode nvarchar(5) NULL,
        LocationCode nvarchar(10) NULL,
        Created datetime2 NULL,
        UserID nvarchar(10) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PrDefBOM PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PrDefBOM_PrBomHdr FOREIGN KEY (BomHdrId) REFERENCES dbo.PrBomHdr (UID) ON DELETE CASCADE,
        CONSTRAINT UQ_PrDefBOM_Company_Hdr_ICode UNIQUE (CompanyCode, BomHdrId, ICode),
        CONSTRAINT CK_PrDefBOM_StdQty_Positive CHECK (StdQty > 0),
        CONSTRAINT CK_PrDefBOM_Tolerance_NonNegative CHECK (Tolerance >= 0),
        CONSTRAINT CK_PrDefBOM_ScrapPercent_NonNegative CHECK (ScrapPercent >= 0)
    );

    CREATE INDEX IX_PrDefBOM_Company_ProdCode
        ON dbo.PrDefBOM (CompanyCode, ProdCode);

    CREATE INDEX IX_PrDefBOM_Company_ICode
        ON dbo.PrDefBOM (CompanyCode, ICode);

    CREATE INDEX IX_PrDefBOM_BomHdrId
        ON dbo.PrDefBOM (BomHdrId);

    PRINT N'Created dbo.PrDefBOM.';
END
ELSE
    PRINT N'dbo.PrDefBOM already exists — left unchanged. Run alter-prdefbom-multilevel.sql if upgrading.';
GO

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
