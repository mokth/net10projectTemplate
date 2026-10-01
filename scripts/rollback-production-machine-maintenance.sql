-- Best-effort rollback for enhance-production-machine-maintenance.sql.
-- Does NOT drop legacy PrMacMaintenance / PrPreventive / PrMachine tables.
-- Review before running on shared environments.

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrMacMaintenance_Preventive')
    ALTER TABLE dbo.PrMacMaintenance DROP CONSTRAINT FK_PrMacMaintenance_Preventive;
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PrMacMaintenance_PreventiveUid' AND object_id = OBJECT_ID(N'dbo.PrMacMaintenance'))
    DROP INDEX UX_PrMacMaintenance_PreventiveUid ON dbo.PrMacMaintenance;
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrMacMaintenance_EndAfterStart')
    ALTER TABLE dbo.PrMacMaintenance DROP CONSTRAINT CK_PrMacMaintenance_EndAfterStart;
GO
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrMacMaintenance_PartsCost')
    ALTER TABLE dbo.PrMacMaintenance DROP CONSTRAINT CK_PrMacMaintenance_PartsCost;
GO
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrMacMaintenance_LabourCost')
    ALTER TABLE dbo.PrMacMaintenance DROP CONSTRAINT CK_PrMacMaintenance_LabourCost;
GO
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrMacMaintenance_OtherCost')
    ALTER TABLE dbo.PrMacMaintenance DROP CONSTRAINT CK_PrMacMaintenance_OtherCost;
GO

-- Drop added maintenance columns (data loss for new fields)
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'StartDateTime') IS NOT NULL ALTER TABLE dbo.PrMacMaintenance DROP COLUMN StartDateTime;
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'EndDateTime') IS NOT NULL ALTER TABLE dbo.PrMacMaintenance DROP COLUMN EndDateTime;
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'ReasonCd') IS NOT NULL ALTER TABLE dbo.PrMacMaintenance DROP COLUMN ReasonCd;
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'PreventiveUid') IS NOT NULL ALTER TABLE dbo.PrMacMaintenance DROP COLUMN PreventiveUid;
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'Remark') IS NOT NULL ALTER TABLE dbo.PrMacMaintenance DROP COLUMN Remark;
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'BranchCode') IS NOT NULL ALTER TABLE dbo.PrMacMaintenance DROP COLUMN BranchCode;
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'LocCode') IS NOT NULL ALTER TABLE dbo.PrMacMaintenance DROP COLUMN LocCode;
GO

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.PrMacMaintenance') AND name = N'DF_PrMacMaintenance_PartsCost')
    ALTER TABLE dbo.PrMacMaintenance DROP CONSTRAINT DF_PrMacMaintenance_PartsCost;
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'PartsCost') IS NOT NULL ALTER TABLE dbo.PrMacMaintenance DROP COLUMN PartsCost;
GO
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.PrMacMaintenance') AND name = N'DF_PrMacMaintenance_LabourCost')
    ALTER TABLE dbo.PrMacMaintenance DROP CONSTRAINT DF_PrMacMaintenance_LabourCost;
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'LabourCost') IS NOT NULL ALTER TABLE dbo.PrMacMaintenance DROP COLUMN LabourCost;
GO
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.PrMacMaintenance') AND name = N'DF_PrMacMaintenance_OtherCost')
    ALTER TABLE dbo.PrMacMaintenance DROP CONSTRAINT DF_PrMacMaintenance_OtherCost;
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'OtherCost') IS NOT NULL ALTER TABLE dbo.PrMacMaintenance DROP COLUMN OtherCost;
GO
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'CompCode') IS NOT NULL ALTER TABLE dbo.PrMacMaintenance DROP COLUMN CompCode;
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PrPreventive_Company_Machine_Date' AND object_id = OBJECT_ID(N'dbo.PrPreventive'))
    DROP INDEX IX_PrPreventive_Company_Machine_Date ON dbo.PrPreventive;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PrPreventive_Company_Status_Date' AND object_id = OBJECT_ID(N'dbo.PrPreventive'))
    DROP INDEX IX_PrPreventive_Company_Status_Date ON dbo.PrPreventive;
GO

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.PrPreventive') AND name = N'DF_PrPreventive_Status')
    ALTER TABLE dbo.PrPreventive DROP CONSTRAINT DF_PrPreventive_Status;
IF COL_LENGTH(N'dbo.PrPreventive', N'Status') IS NOT NULL ALTER TABLE dbo.PrPreventive DROP COLUMN Status;
IF COL_LENGTH(N'dbo.PrPreventive', N'CompletedOn') IS NOT NULL ALTER TABLE dbo.PrPreventive DROP COLUMN CompletedOn;
IF COL_LENGTH(N'dbo.PrPreventive', N'CompletedBy') IS NOT NULL ALTER TABLE dbo.PrPreventive DROP COLUMN CompletedBy;
IF COL_LENGTH(N'dbo.PrPreventive', N'CompCode') IS NOT NULL ALTER TABLE dbo.PrPreventive DROP COLUMN CompCode;
IF COL_LENGTH(N'dbo.PrPreventive', N'BranchCode') IS NOT NULL ALTER TABLE dbo.PrPreventive DROP COLUMN BranchCode;
IF COL_LENGTH(N'dbo.PrPreventive', N'LocCode') IS NOT NULL ALTER TABLE dbo.PrPreventive DROP COLUMN LocCode;
GO

IF OBJECT_ID(N'dbo.PrMaintenanceReason', N'U') IS NOT NULL
    DROP TABLE dbo.PrMaintenanceReason;
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PrMachine_Company_Active_Process' AND object_id = OBJECT_ID(N'dbo.PrMachine'))
    DROP INDEX IX_PrMachine_Company_Active_Process ON dbo.PrMachine;
GO
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.PrMachine') AND name = N'DF_PrMachine_Active')
    ALTER TABLE dbo.PrMachine DROP CONSTRAINT DF_PrMachine_Active;
IF COL_LENGTH(N'dbo.PrMachine', N'Active') IS NOT NULL ALTER TABLE dbo.PrMachine DROP COLUMN Active;
IF COL_LENGTH(N'dbo.PrMachine', N'MachineType') IS NOT NULL ALTER TABLE dbo.PrMachine DROP COLUMN MachineType;
IF COL_LENGTH(N'dbo.PrMachine', N'SerialNo') IS NOT NULL ALTER TABLE dbo.PrMachine DROP COLUMN SerialNo;
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.PrMachine') AND name = N'DF_PrMachine_HourlyCost')
    ALTER TABLE dbo.PrMachine DROP CONSTRAINT DF_PrMachine_HourlyCost;
IF COL_LENGTH(N'dbo.PrMachine', N'HourlyCost') IS NOT NULL ALTER TABLE dbo.PrMachine DROP COLUMN HourlyCost;
GO

PRINT N'=== rollback-production-machine-maintenance complete ===';
