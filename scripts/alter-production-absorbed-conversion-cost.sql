/*
   V4 absorbed conversion costing. Safe to run repeatedly after the existing production schema
   scripts. This migration only adds columns, constraints, indexes, and the append-only fact table.
*/

IF OBJECT_ID(N'dbo.PrBomOperation', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.PrBomOperation', N'UtilitiesOverheadCostPerOutputUnit') IS NULL
    BEGIN
        ALTER TABLE dbo.PrBomOperation ADD UtilitiesOverheadCostPerOutputUnit decimal(19,6) NOT NULL
            CONSTRAINT DF_PrBomOperation_UtilitiesOverheadCost DEFAULT (0) WITH VALUES;
    END;
    IF COL_LENGTH(N'dbo.PrBomOperation', N'OtherCostPerOutputUnit') IS NULL
    BEGIN
        ALTER TABLE dbo.PrBomOperation ADD OtherCostPerOutputUnit decimal(19,6) NOT NULL
            CONSTRAINT DF_PrBomOperation_OtherCost DEFAULT (0) WITH VALUES;
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE parent_object_id = OBJECT_ID(N'dbo.PrBomOperation')
                     AND name = N'CK_PrBomOperation_AbsorbedCost')
        EXEC(N'ALTER TABLE dbo.PrBomOperation WITH CHECK ADD CONSTRAINT CK_PrBomOperation_AbsorbedCost
            CHECK ([UtilitiesOverheadCostPerOutputUnit] >= 0 AND [OtherCostPerOutputUnit] >= 0);');
END;

IF OBJECT_ID(N'dbo.PrWorkOrderOperation', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'UtilitiesOverheadCostPerOutputUnit') IS NULL
    BEGIN
        ALTER TABLE dbo.PrWorkOrderOperation ADD UtilitiesOverheadCostPerOutputUnit decimal(19,6) NOT NULL
            CONSTRAINT DF_PrWorkOrderOperation_UtilitiesOverhead DEFAULT (0) WITH VALUES;
    END;
    IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'OtherCostPerOutputUnit') IS NULL
    BEGIN
        ALTER TABLE dbo.PrWorkOrderOperation ADD OtherCostPerOutputUnit decimal(19,6) NOT NULL
            CONSTRAINT DF_PrWorkOrderOperation_OtherCost DEFAULT (0) WITH VALUES;
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE parent_object_id = OBJECT_ID(N'dbo.PrWorkOrderOperation')
                     AND name = N'CK_PrWorkOrderOperation_AbsorbedCost')
        EXEC(N'ALTER TABLE dbo.PrWorkOrderOperation WITH CHECK ADD CONSTRAINT CK_PrWorkOrderOperation_AbsorbedCost
            CHECK ([UtilitiesOverheadCostPerOutputUnit] >= 0 AND [OtherCostPerOutputUnit] >= 0);');
END;

IF OBJECT_ID(N'dbo.PrWorkOrderMachine', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.PrWorkOrderMachine', N'PlannedCostAmount') IS NULL
    BEGIN
        ALTER TABLE dbo.PrWorkOrderMachine ADD PlannedCostAmount decimal(19,6) NOT NULL
            CONSTRAINT DF_PrWorkOrderMachine_PlannedCost DEFAULT (0) WITH VALUES;
    END;
    IF COL_LENGTH(N'dbo.PrWorkOrderMachine', N'CostPerOutputUnit') IS NULL
    BEGIN
        ALTER TABLE dbo.PrWorkOrderMachine ADD CostPerOutputUnit decimal(19,6) NOT NULL
            CONSTRAINT DF_PrWorkOrderMachine_CostPerOutput DEFAULT (0) WITH VALUES;
    END;
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE parent_object_id = OBJECT_ID(N'dbo.PrWorkOrderMachine')
                     AND name = N'CK_PrWorkOrderMachine_AbsorbedCost')
        EXEC(N'ALTER TABLE dbo.PrWorkOrderMachine WITH CHECK ADD CONSTRAINT CK_PrWorkOrderMachine_AbsorbedCost
            CHECK ([PlannedCostAmount] >= 0 AND [CostPerOutputUnit] >= 0);');
END;

IF OBJECT_ID(N'dbo.PrWorkOrderLabour', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderLabour') AND name = N'PlannedAmount'
               AND precision = 19 AND scale = 4)
    ALTER TABLE dbo.PrWorkOrderLabour ALTER COLUMN PlannedAmount decimal(19,6) NOT NULL;

IF OBJECT_ID(N'dbo.PrBomMachineOption', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.PrBomMachineOption') AND name = N'MachineRatePerHour'
               AND precision = 18 AND scale = 6)
    ALTER TABLE dbo.PrBomMachineOption ALTER COLUMN MachineRatePerHour decimal(19,6) NOT NULL;

IF OBJECT_ID(N'dbo.PrBomMachineOption', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE parent_object_id = OBJECT_ID(N'dbo.PrBomMachineOption')
                     AND name = N'CK_PrBomMachineOption_Cost')
    ALTER TABLE dbo.PrBomMachineOption WITH CHECK ADD CONSTRAINT CK_PrBomMachineOption_Cost
        CHECK (MachineRatePerHour >= 0);

IF OBJECT_ID(N'dbo.PrProductionConversionCostFact', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrProductionConversionCostFact
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrProductionConversionCostFact PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        StockPostingID bigint NOT NULL,
        ProductionOutputID bigint NOT NULL,
        ProductionMovementID bigint NOT NULL,
        WorkOrderID bigint NOT NULL,
        RouteStepID bigint NOT NULL,
        WorkOrderOperationID bigint NOT NULL,
        CostType nvarchar(20) NOT NULL,
        SourceLineKey nvarchar(80) NOT NULL,
        WorkOrderLabourID bigint NULL,
        WorkOrderMachineID bigint NULL,
        BasisQty decimal(18,4) NOT NULL,
        BasisUom nvarchar(10) NOT NULL,
        RatePerOutputUnit decimal(19,6) NOT NULL,
        CostAmount decimal(19,6) NOT NULL,
        ReversesFactID bigint NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CreatedBy nvarchar(10) NOT NULL,
        CONSTRAINT FK_PrProductionConversionCostFact_StockPosting
            FOREIGN KEY (CompanyCode, BranchCode, StockPostingID)
            REFERENCES dbo.StockPosting (CompanyCode, BranchCode, Id),
        CONSTRAINT FK_PrProductionConversionCostFact_Output
            FOREIGN KEY (ProductionOutputID) REFERENCES dbo.PrProductionOutput (UID),
        CONSTRAINT FK_PrProductionConversionCostFact_Movement
            FOREIGN KEY (ProductionMovementID) REFERENCES dbo.PrProductionBalLotMovement (UID),
        CONSTRAINT FK_PrProductionConversionCostFact_WorkOrder
            FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID),
        CONSTRAINT FK_PrProductionConversionCostFact_RouteStep
            FOREIGN KEY (RouteStepID) REFERENCES dbo.PrWorkOrderRouteStep (UID),
        CONSTRAINT FK_PrProductionConversionCostFact_Operation
            FOREIGN KEY (WorkOrderOperationID) REFERENCES dbo.PrWorkOrderOperation (UID),
        CONSTRAINT FK_PrProductionConversionCostFact_Labour
            FOREIGN KEY (WorkOrderLabourID) REFERENCES dbo.PrWorkOrderLabour (UID),
        CONSTRAINT FK_PrProductionConversionCostFact_Machine
            FOREIGN KEY (WorkOrderMachineID) REFERENCES dbo.PrWorkOrderMachine (UID),
        CONSTRAINT FK_PrProductionConversionCostFact_Reversal
            FOREIGN KEY (ReversesFactID) REFERENCES dbo.PrProductionConversionCostFact (Id),
        CONSTRAINT CK_PrProductionConversionCostFact_Type CHECK
            (CostType IN (N'LABOUR', N'MACHINE', N'UTILITIES_OVERHEAD', N'OTHER')),
        CONSTRAINT CK_PrProductionConversionCostFact_Positive CHECK
            (BasisQty > 0 AND RatePerOutputUnit > 0 AND CostAmount > 0),
        CONSTRAINT CK_PrProductionConversionCostFact_SourceShape CHECK
        (
            (CostType = N'LABOUR' AND WorkOrderLabourID IS NOT NULL AND WorkOrderMachineID IS NULL)
            OR (CostType = N'MACHINE' AND WorkOrderLabourID IS NULL AND WorkOrderMachineID IS NOT NULL)
            OR (CostType IN (N'UTILITIES_OVERHEAD', N'OTHER')
                AND WorkOrderLabourID IS NULL AND WorkOrderMachineID IS NULL)
        )
    );
END;

IF OBJECT_ID(N'dbo.PrProductionConversionCostFact', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.PrProductionConversionCostFact')
                   AND name = N'CK_PrProductionConversionCostFact_Type')
        EXEC(N'ALTER TABLE dbo.PrProductionConversionCostFact WITH CHECK ADD CONSTRAINT CK_PrProductionConversionCostFact_Type
            CHECK ([CostType] IN (''LABOUR'', ''MACHINE'', ''UTILITIES_OVERHEAD'', ''OTHER''));');
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.PrProductionConversionCostFact')
                   AND name = N'CK_PrProductionConversionCostFact_Positive')
        EXEC(N'ALTER TABLE dbo.PrProductionConversionCostFact WITH CHECK ADD CONSTRAINT CK_PrProductionConversionCostFact_Positive
            CHECK ([BasisQty] > 0 AND [RatePerOutputUnit] > 0 AND [CostAmount] > 0);');
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.PrProductionConversionCostFact')
                   AND name = N'CK_PrProductionConversionCostFact_SourceShape')
        EXEC(N'ALTER TABLE dbo.PrProductionConversionCostFact WITH CHECK ADD CONSTRAINT CK_PrProductionConversionCostFact_SourceShape
        CHECK
        (
            ([CostType] = ''LABOUR'' AND [WorkOrderLabourID] IS NOT NULL AND [WorkOrderMachineID] IS NULL)
            OR ([CostType] = ''MACHINE'' AND [WorkOrderLabourID] IS NULL AND [WorkOrderMachineID] IS NOT NULL)
            OR ([CostType] IN (''UTILITIES_OVERHEAD'', ''OTHER'')
                AND [WorkOrderLabourID] IS NULL AND [WorkOrderMachineID] IS NULL)
        );');
END;

IF OBJECT_ID(N'dbo.PrProductionConversionCostFact', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionConversionCostFact')
                   AND name = N'UQ_PrProductionConversionCostFact_Movement_Source')
        EXEC(N'CREATE UNIQUE INDEX UQ_PrProductionConversionCostFact_Movement_Source
            ON dbo.PrProductionConversionCostFact ([ProductionMovementID], [SourceLineKey]);');
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionConversionCostFact')
                   AND name = N'UQ_PrProductionConversionCostFact_Reversal')
        EXEC(N'CREATE UNIQUE INDEX UQ_PrProductionConversionCostFact_Reversal
            ON dbo.PrProductionConversionCostFact ([ReversesFactID])
            WHERE [ReversesFactID] IS NOT NULL;');
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionConversionCostFact')
                   AND name = N'IX_PrProductionConversionCostFact_Posting_Output')
        EXEC(N'CREATE INDEX IX_PrProductionConversionCostFact_Posting_Output
            ON dbo.PrProductionConversionCostFact ([StockPostingID], [ProductionOutputID]);');
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionConversionCostFact')
                   AND name = N'IX_PrProductionConversionCostFact_WorkOrder_Operation')
        EXEC(N'CREATE INDEX IX_PrProductionConversionCostFact_WorkOrder_Operation
            ON dbo.PrProductionConversionCostFact ([WorkOrderID], [WorkOrderOperationID]);');
END;
