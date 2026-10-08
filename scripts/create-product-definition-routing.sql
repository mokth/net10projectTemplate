-- Version-owned Product Definition routing, machine and labour standards.
-- Run after create-prdefbom.sql (or alter-prdefbom-multilevel.sql).
-- Manual DBA script; idempotent and does not modify legacy PrDef* tables.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.PrBomOperation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrBomOperation (
        UID bigint IDENTITY(1,1) NOT NULL,
        BomHdrID bigint NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        WorkCentreCode nvarchar(20) NOT NULL,
        OutputItemCode nvarchar(30) NOT NULL,
        CentralSequence int NOT NULL,
        OutputBaseQty decimal(18,4) NOT NULL,
        OutputUOM nvarchar(10) NULL,
        OperationCode nvarchar(20) NOT NULL,
        ProcessSequence int NOT NULL,
        SetupLossQty decimal(18,4) NOT NULL CONSTRAINT DF_PrBomOperation_SetupLoss DEFAULT (0),
        OperationLossQty decimal(18,4) NOT NULL CONSTRAINT DF_PrBomOperation_OperationLoss DEFAULT (0),
        UtilitiesOverheadCostPerOutputUnit decimal(19,6) NOT NULL CONSTRAINT DF_PrBomOperation_UtilitiesOverhead DEFAULT (0),
        OtherCostPerOutputUnit decimal(19,6) NOT NULL CONSTRAINT DF_PrBomOperation_OtherCost DEFAULT (0),
        IsFinalOperation bit NOT NULL CONSTRAINT DF_PrBomOperation_Final DEFAULT (0),
        Remark nvarchar(500) NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PrBomOperation PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PrBomOperation_PrBomHdr FOREIGN KEY (BomHdrID) REFERENCES dbo.PrBomHdr (UID) ON DELETE CASCADE,
        CONSTRAINT UQ_PrBomOperation_Header_Route UNIQUE (BomHdrID, WorkCentreCode, OutputItemCode, OperationCode),
        CONSTRAINT CK_PrBomOperation_Sequences CHECK (CentralSequence > 0 AND ProcessSequence > 0),
        CONSTRAINT CK_PrBomOperation_OutputQty CHECK (OutputBaseQty > 0),
        CONSTRAINT CK_PrBomOperation_Loss CHECK (SetupLossQty >= 0 AND OperationLossQty >= 0),
        CONSTRAINT CK_PrBomOperation_AbsorbedCost CHECK (UtilitiesOverheadCostPerOutputUnit >= 0 AND OtherCostPerOutputUnit >= 0)
    );
    CREATE INDEX IX_PrBomOperation_Company_WorkCentre
        ON dbo.PrBomOperation (CompanyCode, WorkCentreCode);
END
GO

IF OBJECT_ID(N'dbo.PrBomMachineOption', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrBomMachineOption (
        UID bigint IDENTITY(1,1) NOT NULL,
        OperationID bigint NOT NULL,
        MachineCode nvarchar(20) NOT NULL,
        MachineDescription nvarchar(200) NULL,
        ResourceSequence int NOT NULL,
        IsPrimary bit NOT NULL CONSTRAINT DF_PrBomMachineOption_Primary DEFAULT (1),
        CycleSeconds decimal(18,4) NOT NULL CONSTRAINT DF_PrBomMachineOption_Cycle DEFAULT (0),
        ConversionSeconds decimal(18,4) NOT NULL CONSTRAINT DF_PrBomMachineOption_Conversion DEFAULT (0),
        SetupSeconds decimal(18,4) NOT NULL CONSTRAINT DF_PrBomMachineOption_Setup DEFAULT (0),
        QueueSeconds decimal(18,4) NOT NULL CONSTRAINT DF_PrBomMachineOption_Queue DEFAULT (0),
        MachineRatePerHour decimal(19,6) NOT NULL CONSTRAINT DF_PrBomMachineOption_Rate DEFAULT (0),
        ParallelMachineCount int NOT NULL CONSTRAINT DF_PrBomMachineOption_Parallel DEFAULT (1),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PrBomMachineOption PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PrBomMachineOption_Operation FOREIGN KEY (OperationID) REFERENCES dbo.PrBomOperation (UID) ON DELETE CASCADE,
        CONSTRAINT UQ_PrBomMachineOption_Operation_Machine UNIQUE (OperationID, MachineCode),
        CONSTRAINT CK_PrBomMachineOption_Sequence CHECK (ResourceSequence > 0),
        CONSTRAINT CK_PrBomMachineOption_Times CHECK (CycleSeconds >= 0 AND ConversionSeconds >= 0 AND SetupSeconds >= 0 AND QueueSeconds >= 0 AND MachineRatePerHour >= 0),
        CONSTRAINT CK_PrBomMachineOption_Parallel CHECK (ParallelMachineCount > 0)
    );
END
GO

IF OBJECT_ID(N'dbo.PrBomLabourStandard', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrBomLabourStandard (
        UID bigint IDENTITY(1,1) NOT NULL,
        MachineOptionID bigint NOT NULL,
        LabourCode nvarchar(20) NOT NULL,
        LabourDescription nvarchar(200) NULL,
        CostPerOutputUnit decimal(19,6) NOT NULL CONSTRAINT DF_PrBomLabourStandard_Cost DEFAULT (0),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PrBomLabourStandard PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PrBomLabourStandard_Machine FOREIGN KEY (MachineOptionID) REFERENCES dbo.PrBomMachineOption (UID) ON DELETE CASCADE,
        CONSTRAINT UQ_PrBomLabourStandard_Machine_Labour UNIQUE (MachineOptionID, LabourCode),
        CONSTRAINT CK_PrBomLabourStandard_Cost CHECK (CostPerOutputUnit >= 0)
    );
END
GO

-- Optional one-time compatibility backfill from the legacy current/mutable route.
-- Targets the migrated/default STANDARD definition only (highest Version of STANDARD).
-- Never rewrites an existing version-owned route. Archived legacy PrDefRev* import remains separate.
IF OBJECT_ID(N'dbo.PrDefWCenter', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.PrDefProcess', N'U') IS NOT NULL
BEGIN
    ;WITH latest AS (
        SELECT h.*,
               ROW_NUMBER() OVER (
                   PARTITION BY h.CompanyCode, h.ProdCode
                   ORDER BY h.Version DESC
               ) AS rn
        FROM dbo.PrBomHdr h
        WHERE h.DefinitionCode = N'STANDARD'
           OR (h.DefinitionCode IS NULL AND NOT EXISTS (
                SELECT 1 FROM dbo.PrBomHdr x
                WHERE x.CompanyCode = h.CompanyCode AND x.ProdCode = h.ProdCode
                  AND x.DefinitionCode = N'STANDARD'))
    )
    INSERT INTO dbo.PrBomOperation (
        BomHdrID, CompanyCode, WorkCentreCode, OutputItemCode, CentralSequence,
        OutputBaseQty, OutputUOM, OperationCode, ProcessSequence,
        SetupLossQty, OperationLossQty, IsFinalOperation, Remark,
        CreatedDate, CreatedBy, ModifiedDate, ModifiedBy)
    SELECT h.UID,
           h.CompanyCode,
           p.WCCode,
           p.WCICode,
           COALESCE(NULLIF(w.SeqNo, 0), 1),
           COALESCE(NULLIF(CONVERT(decimal(18,4), w.StdPackSize), 0), h.BaseQty),
           w.StdUOM,
           p.ProcessCode,
           COALESCE(NULLIF(p.SeqNo, 0), 1),
           COALESCE(CONVERT(decimal(18,4), p.SetupLostQty), 0),
           COALESCE(CONVERT(decimal(18,4), p.OperationLostQty), 0),
           COALESCE(p.FinalProcess, 0),
           p.Remark,
           SYSUTCDATETIME(), N'migration', SYSUTCDATETIME(), N'migration'
    FROM latest h
    INNER JOIN dbo.PrDefProcess p
        ON p.ProdCode = h.ProdCode
       AND (p.CompCode IS NULL OR p.CompCode = h.CompanyCode)
    INNER JOIN dbo.PrDefWCenter w
        ON w.ProdCode = p.ProdCode
       AND w.WCCode = p.WCCode
       AND w.ICode = p.WCICode
    WHERE h.rn = 1
      AND NOT EXISTS (SELECT 1 FROM dbo.PrBomOperation o WHERE o.BomHdrID = h.UID);
END
GO

IF OBJECT_ID(N'dbo.PrDefMachine', N'U') IS NOT NULL
BEGIN
    INSERT INTO dbo.PrBomMachineOption (
        OperationID, MachineCode, MachineDescription, ResourceSequence, IsPrimary,
        CycleSeconds, ConversionSeconds, SetupSeconds, QueueSeconds, ParallelMachineCount,
        CreatedDate, CreatedBy, ModifiedDate, ModifiedBy)
    SELECT o.UID,
           m.MachineCode,
           m.MachineName,
           COALESCE(NULLIF(m.SeqNo, 0), 1),
           COALESCE(m.MacDefault, 0),
           COALESCE(CONVERT(decimal(18,4), m.CycleTime), 0),
           COALESCE(CONVERT(decimal(18,4), m.ConversionTime), 0),
           COALESCE(CONVERT(decimal(18,4), m.StartupTime), 0),
           COALESCE(CONVERT(decimal(18,4), m.QueueTime), 0),
           1,
           SYSUTCDATETIME(), N'migration', SYSUTCDATETIME(), N'migration'
    FROM dbo.PrBomOperation o
    INNER JOIN dbo.PrBomHdr h ON h.UID = o.BomHdrID
    INNER JOIN dbo.PrDefMachine m
        ON m.ProdCode = h.ProdCode
       AND m.WCCode = o.WorkCentreCode
       AND m.WCICode = o.OutputItemCode
       AND m.ProcessCode = o.OperationCode
       AND (m.CompCode IS NULL OR m.CompCode = h.CompanyCode)
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.PrBomMachineOption x
        WHERE x.OperationID = o.UID AND x.MachineCode = m.MachineCode);
END
GO

IF OBJECT_ID(N'dbo.PrDefLabour', N'U') IS NOT NULL
BEGIN
    INSERT INTO dbo.PrBomLabourStandard (
        MachineOptionID, LabourCode, LabourDescription, CostPerOutputUnit,
        CreatedDate, CreatedBy, ModifiedDate, ModifiedBy)
    SELECT mo.UID,
           l.LabourCode,
           NULL,
           COALESCE(CONVERT(decimal(18,6), l.LabourCost), 0),
           SYSUTCDATETIME(), N'migration', SYSUTCDATETIME(), N'migration'
    FROM dbo.PrBomMachineOption mo
    INNER JOIN dbo.PrBomOperation o ON o.UID = mo.OperationID
    INNER JOIN dbo.PrBomHdr h ON h.UID = o.BomHdrID
    INNER JOIN dbo.PrDefLabour l
        ON l.ProdCode = h.ProdCode
       AND l.WCCode = o.WorkCentreCode
       AND l.WCICode = o.OutputItemCode
       AND l.ProcessCode = o.OperationCode
       AND l.MachineCode = mo.MachineCode
       AND (l.CompCode IS NULL OR l.CompCode = h.CompanyCode)
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.PrBomLabourStandard x
        WHERE x.MachineOptionID = mo.UID AND x.LabourCode = l.LabourCode);
END
GO

PRINT N'Product Definition version-owned routing schema is ready.';
GO
