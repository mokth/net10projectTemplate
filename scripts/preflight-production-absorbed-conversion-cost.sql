/* V4 absorbed conversion-cost deployment preflight. Read-only apart from THROW on failure. */

IF OBJECT_ID(N'dbo.PrProductionConversionCostFact', N'U') IS NULL
    THROW 51000, 'V4 schema missing PrProductionConversionCostFact.', 1;
IF COL_LENGTH(N'dbo.PrBomOperation', N'UtilitiesOverheadCostPerOutputUnit') IS NULL
    THROW 51000, 'V4 schema missing PrBomOperation utilities/overhead rate.', 1;
IF COL_LENGTH(N'dbo.PrBomOperation', N'OtherCostPerOutputUnit') IS NULL
    THROW 51000, 'V4 schema missing PrBomOperation other rate.', 1;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'UtilitiesOverheadCostPerOutputUnit') IS NULL
    THROW 51000, 'V4 schema missing PrWorkOrderOperation utilities/overhead rate.', 1;
IF COL_LENGTH(N'dbo.PrWorkOrderOperation', N'OtherCostPerOutputUnit') IS NULL
    THROW 51000, 'V4 schema missing PrWorkOrderOperation other rate.', 1;
IF COL_LENGTH(N'dbo.PrWorkOrderMachine', N'PlannedCostAmount') IS NULL
    THROW 51000, 'V4 schema missing PrWorkOrderMachine planned cost.', 1;
IF COL_LENGTH(N'dbo.PrWorkOrderMachine', N'CostPerOutputUnit') IS NULL
    THROW 51000, 'V4 schema missing PrWorkOrderMachine cost per output.', 1;

IF EXISTS (SELECT 1 FROM dbo.PrBomOperation
           WHERE UtilitiesOverheadCostPerOutputUnit < 0 OR OtherCostPerOutputUnit < 0)
    THROW 51000, 'Negative authored conversion cost exists on PrBomOperation.', 1;
IF EXISTS (SELECT 1 FROM dbo.PrWorkOrderOperation
           WHERE UtilitiesOverheadCostPerOutputUnit < 0 OR OtherCostPerOutputUnit < 0)
    THROW 51000, 'Negative frozen conversion cost exists on PrWorkOrderOperation.', 1;
IF EXISTS (SELECT 1 FROM dbo.PrWorkOrderMachine
           WHERE PlannedCostAmount < 0 OR CostPerOutputUnit < 0)
    THROW 51000, 'Negative frozen machine cost exists on PrWorkOrderMachine.', 1;
IF EXISTS (SELECT 1 FROM dbo.PrBomMachineOption
           WHERE MachineRatePerHour < 0)
    THROW 51000, 'Negative authored machine rate exists on PrBomMachineOption.', 1;
IF EXISTS (SELECT 1 FROM dbo.PrBomLabourStandard
           WHERE CostPerOutputUnit < 0)
    THROW 51000, 'Negative authored labour rate exists on PrBomLabourStandard.', 1;
IF EXISTS (SELECT 1 FROM dbo.PrWorkOrderLabour
           WHERE Rate < 0 OR PlannedAmount < 0)
    THROW 51000, 'Negative frozen labour rate/amount exists on PrWorkOrderLabour.', 1;
IF EXISTS (SELECT 1 FROM dbo.PrProductionConversionCostFact
           WHERE BasisQty <= 0 OR RatePerOutputUnit <= 0 OR CostAmount <= 0)
    THROW 51000, 'Non-positive absorbed conversion fact exists.', 1;

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.PrBomOperation')
             AND name IN (N'UtilitiesOverheadCostPerOutputUnit', N'OtherCostPerOutputUnit')
             AND (precision <> 19 OR scale <> 6))
    THROW 51000, 'PrBomOperation absorbed rates are not decimal(19,6).', 1;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderOperation')
             AND name IN (N'UtilitiesOverheadCostPerOutputUnit', N'OtherCostPerOutputUnit')
             AND (precision <> 19 OR scale <> 6))
    THROW 51000, 'PrWorkOrderOperation absorbed rates are not decimal(19,6).', 1;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderMachine')
             AND name IN (N'PlannedCostAmount', N'CostPerOutputUnit')
             AND (precision <> 19 OR scale <> 6))
    THROW 51000, 'PrWorkOrderMachine absorbed costs are not decimal(19,6).', 1;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.PrProductionConversionCostFact')
             AND name IN (N'BasisQty', N'RatePerOutputUnit', N'CostAmount')
             AND ((name = N'BasisQty' AND (precision <> 18 OR scale <> 4))
               OR (name IN (N'RatePerOutputUnit', N'CostAmount') AND (precision <> 19 OR scale <> 6))))
    THROW 51000, 'PrProductionConversionCostFact money/quantity precision is incorrect.', 1;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.PrBomMachineOption')
             AND name = N'MachineRatePerHour'
             AND (precision <> 19 OR scale <> 6))
    THROW 51000, 'PrBomMachineOption machine rate is not decimal(19,6).', 1;
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderLabour')
             AND name IN (N'Rate', N'PlannedAmount')
             AND (precision <> 19 OR scale <> 6))
    THROW 51000, 'PrWorkOrderLabour cost rates/amounts are not decimal(19,6).', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionConversionCostFact')
               AND name = N'UQ_PrProductionConversionCostFact_Movement_Source')
    THROW 51000, 'V4 schema missing unique conversion-fact source index.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionConversionCostFact')
               AND name = N'UQ_PrProductionConversionCostFact_Reversal')
    THROW 51000, 'V4 schema missing unique conversion-fact reversal index.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionConversionCostFact')
               AND name = N'IX_PrProductionConversionCostFact_Posting_Output')
    THROW 51000, 'V4 schema missing conversion-fact posting/output index.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PrProductionConversionCostFact')
               AND name = N'IX_PrProductionConversionCostFact_WorkOrder_Operation')
    THROW 51000, 'V4 schema missing conversion-fact work-order/operation index.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrProductionConversionCostFact_SourceShape')
    THROW 51000, 'V4 schema missing conversion-fact source-shape constraint.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrProductionConversionCostFact_Type')
    THROW 51000, 'V4 schema missing conversion-fact type constraint.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrProductionConversionCostFact_Positive')
    THROW 51000, 'V4 schema missing conversion-fact positive-value constraint.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE parent_object_id = OBJECT_ID(N'dbo.PrBomMachineOption')
                 AND name = N'CK_PrBomMachineOption_Cost')
    THROW 51000, 'V4 schema missing machine-rate non-negative constraint.', 1;

DECLARE @requiredFactFks TABLE (Name sysname NOT NULL);
INSERT INTO @requiredFactFks (Name) VALUES
    (N'FK_PrProductionConversionCostFact_StockPosting'),
    (N'FK_PrProductionConversionCostFact_Output'),
    (N'FK_PrProductionConversionCostFact_Movement'),
    (N'FK_PrProductionConversionCostFact_WorkOrder'),
    (N'FK_PrProductionConversionCostFact_RouteStep'),
    (N'FK_PrProductionConversionCostFact_Operation'),
    (N'FK_PrProductionConversionCostFact_Labour'),
    (N'FK_PrProductionConversionCostFact_Machine'),
    (N'FK_PrProductionConversionCostFact_Reversal');
IF EXISTS (
    SELECT 1
    FROM @requiredFactFks required
    WHERE NOT EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE fk.name = required.Name
                      AND fk.parent_object_id = OBJECT_ID(N'dbo.PrProductionConversionCostFact')))
    THROW 51000, 'V4 schema is missing a required conversion-fact foreign key.', 1;

SELECT
    SnapshotHashVersion,
    COUNT_BIG(*) AS WorkOrderCount,
    CASE WHEN SnapshotHashVersion >= 4 THEN N'ABSORBED_CONVERSION_ENABLED' ELSE N'LEGACY_V3_MATERIAL_WIP_ONLY' END AS Contract
FROM dbo.PrWorkOrder
GROUP BY SnapshotHashVersion
ORDER BY SnapshotHashVersion;

SELECT COUNT_BIG(*) AS ConversionFactCount
FROM dbo.PrProductionConversionCostFact;
