/*
  Daily Production material facts: standard / consume / variance / reason.
  Composite tenant FK to PrProductionOutput. Idempotent.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.PrProductionOutput', N'U') IS NULL
BEGIN
    PRINT N'dbo.PrProductionOutput is missing — run create-production-daily-output.sql first.';
    RETURN;
END;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_PrProductionOutput_Uid_Tenant'
      AND object_id = OBJECT_ID(N'dbo.PrProductionOutput'))
    CREATE UNIQUE INDEX UQ_PrProductionOutput_Uid_Tenant
        ON dbo.PrProductionOutput (UID, CompanyCode, BranchCode);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_PrProductionOutput_Tenant_Date_Status'
      AND object_id = OBJECT_ID(N'dbo.PrProductionOutput'))
    CREATE INDEX IX_PrProductionOutput_Tenant_Date_Status
        ON dbo.PrProductionOutput (CompanyCode, BranchCode, ProductionDate, Status);
GO

IF OBJECT_ID(N'dbo.PrProductionOutputMaterial', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrProductionOutputMaterial
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrProductionOutputMaterial PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        ProductionOutputID bigint NOT NULL,
        WorkOrderMaterialID bigint NULL,
        IsHandoff bit NOT NULL CONSTRAINT DF_PrPOM_IsHandoff DEFAULT (0),
        HandoffFromOperationID bigint NULL,
        ComponentCode nvarchar(30) NOT NULL,
        RequiredUOM nvarchar(10) NOT NULL,
        SupplySource nvarchar(40) NOT NULL,
        IssueMethod nvarchar(20) NOT NULL,
        TolerancePercent decimal(18,4) NOT NULL,
        WoBomRequiredQty decimal(18,4) NOT NULL,
        ConversionFactorToBase decimal(18,8) NOT NULL CONSTRAINT DF_PrPOM_Factor DEFAULT (1),
        BaseUOM nvarchar(10) NULL,
        StandardQty decimal(18,4) NOT NULL,
        ConsumeQty decimal(18,4) NOT NULL,
        VarianceQty decimal(18,4) NOT NULL,
        VarianceReasonCode nvarchar(30) NULL,
        VarianceReasonText nvarchar(250) NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(10) NOT NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_PrPOM_Output_Tenant FOREIGN KEY (ProductionOutputID, CompanyCode, BranchCode)
            REFERENCES dbo.PrProductionOutput (UID, CompanyCode, BranchCode) ON DELETE CASCADE,
        CONSTRAINT FK_PrPOM_WorkOrderMaterial FOREIGN KEY (WorkOrderMaterialID)
            REFERENCES dbo.PrWorkOrderMaterial (UID),
        CONSTRAINT CK_PrProductionOutputMaterial_Qty CHECK (
            [ConsumeQty] >= 0 AND [StandardQty] >= 0 AND [TolerancePercent] >= 0
            AND [ConversionFactorToBase] > 0),
        CONSTRAINT CK_PrProductionOutputMaterial_Identity CHECK (
            ([IsHandoff] = 0 AND [WorkOrderMaterialID] IS NOT NULL AND [HandoffFromOperationID] IS NULL)
            OR ([IsHandoff] = 1 AND [WorkOrderMaterialID] IS NULL AND [HandoffFromOperationID] IS NOT NULL))
    );

    CREATE UNIQUE INDEX UQ_PrProductionOutputMaterial_Output_Material
        ON dbo.PrProductionOutputMaterial (ProductionOutputID, WorkOrderMaterialID)
        WHERE WorkOrderMaterialID IS NOT NULL;
    CREATE UNIQUE INDEX UQ_PrProductionOutputMaterial_Output_Handoff
        ON dbo.PrProductionOutputMaterial (ProductionOutputID)
        WHERE IsHandoff = 1;
    CREATE INDEX IX_PrProductionOutputMaterial_Output
        ON dbo.PrProductionOutputMaterial (ProductionOutputID);
    CREATE INDEX IX_PrProductionOutputMaterial_WorkOrderMaterial
        ON dbo.PrProductionOutputMaterial (WorkOrderMaterialID);
    CREATE INDEX IX_PrProductionOutputMaterial_Component
        ON dbo.PrProductionOutputMaterial (CompanyCode, BranchCode, ComponentCode);
    CREATE INDEX IX_PrProductionOutputMaterial_Reason
        ON dbo.PrProductionOutputMaterial (CompanyCode, BranchCode, VarianceReasonCode);
END;
GO

PRINT N'PrProductionOutputMaterial ready.';
GO
