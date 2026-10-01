-- Enhance PrMachine / PrPreventive / PrMacMaintenance + PrMaintenanceReason.
-- Sequence: preflight-production-machine-maintenance.sql -> this script.
-- Rollback: rollback-production-machine-maintenance.sql
-- GO separators required so ALTER ADD is visible to later batches.

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.PrMachine', N'U') IS NULL
    THROW 52000, 'dbo.PrMachine is required.', 1;
IF OBJECT_ID(N'dbo.PrPreventive', N'U') IS NULL
    THROW 52001, 'dbo.PrPreventive is required.', 1;
GO

-- Create legacy maintenance table when absent (greenfield / incomplete DBs)
IF OBJECT_ID(N'dbo.PrMacMaintenance', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrMacMaintenance
    (
        ID          int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrMacMaintenance PRIMARY KEY,
        TrxDate     datetime2 NULL,
        MacCode     nvarchar(30) NULL,
        Description nvarchar(max) NULL,
        ActionTaken nvarchar(max) NULL,
        Status      nvarchar(20) NULL,
        ReportBy    nvarchar(20) NULL,
        ActionBy    nvarchar(20) NULL,
        ActionOn    datetime2 NULL,
        RefCode     nvarchar(25) NULL,
        RepType     nvarchar(25) NULL,
        MType       nvarchar(10) NULL,
        Name        nvarchar(100) NULL,
        Reminder    nvarchar(10) NULL
    );
    PRINT N'Created dbo.PrMacMaintenance (base schema).';
END
ELSE
    PRINT N'dbo.PrMacMaintenance already exists.';
GO

IF OBJECT_ID(N'dbo.PrMacMaintenanceImages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrMacMaintenanceImages
    (
        UID      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrMacMaintenanceImages PRIMARY KEY,
        RefCode  nvarchar(25) NULL,
        ImageURL nvarchar(100) NULL,
        filename nvarchar(100) NULL
    );
    PRINT N'Created dbo.PrMacMaintenanceImages.';
END
GO

IF EXISTS (
    SELECT CompCode, Machine_Cd
    FROM dbo.PrMachine
    GROUP BY CompCode, Machine_Cd
    HAVING COUNT(*) > 1
)
    THROW 52003, 'Duplicate PrMachine.Machine_Cd within CompCode. Repair before enhance.', 1;
GO

-- ─── PrMachine ───────────────────────────────────────────────────────────────
IF COL_LENGTH(N'dbo.PrMachine', N'Active') IS NULL
BEGIN
    ALTER TABLE dbo.PrMachine ADD Active bit NOT NULL
        CONSTRAINT DF_PrMachine_Active DEFAULT (1);
    PRINT N'Added dbo.PrMachine.Active.';
END
ELSE PRINT N'dbo.PrMachine.Active already exists.';
GO

IF COL_LENGTH(N'dbo.PrMachine', N'MachineType') IS NULL
BEGIN
    ALTER TABLE dbo.PrMachine ADD MachineType nvarchar(30) NULL;
    PRINT N'Added dbo.PrMachine.MachineType.';
END
ELSE PRINT N'dbo.PrMachine.MachineType already exists.';
GO

IF COL_LENGTH(N'dbo.PrMachine', N'SerialNo') IS NULL
BEGIN
    ALTER TABLE dbo.PrMachine ADD SerialNo nvarchar(50) NULL;
    PRINT N'Added dbo.PrMachine.SerialNo.';
END
ELSE PRINT N'dbo.PrMachine.SerialNo already exists.';
GO

IF COL_LENGTH(N'dbo.PrMachine', N'HourlyCost') IS NULL
BEGIN
    ALTER TABLE dbo.PrMachine ADD HourlyCost decimal(19,6) NOT NULL
        CONSTRAINT DF_PrMachine_HourlyCost DEFAULT (0);
    PRINT N'Added dbo.PrMachine.HourlyCost.';
END
ELSE PRINT N'dbo.PrMachine.HourlyCost already exists.';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PrMachine_Company_Active_Process' AND object_id = OBJECT_ID(N'dbo.PrMachine'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_PrMachine_Company_Active_Process
        ON dbo.PrMachine (CompCode, Active, Process_Cd);
    PRINT N'Created IX_PrMachine_Company_Active_Process.';
END
GO

-- ─── PrMaintenanceReason ─────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.PrMaintenanceReason', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrMaintenanceReason
    (
        ReasonCd    nvarchar(10)  NOT NULL,
        Description nvarchar(100) NOT NULL,
        ReasonType  nvarchar(20)  NOT NULL,
        Active      bit           NOT NULL CONSTRAINT DF_PrMaintenanceReason_Active DEFAULT (1),
        CompCode    nvarchar(10)  NOT NULL,
        BranchCode  nvarchar(10)  NULL,
        LocCode     nvarchar(10)  NULL,
        Created     datetime2     NULL,
        Updated     datetime2     NULL,
        UserID      nvarchar(10)  NULL,
        UpdatedUID  nvarchar(10)  NULL,
        CONSTRAINT PK_PrMaintenanceReason PRIMARY KEY (CompCode, ReasonCd)
    );
    CREATE NONCLUSTERED INDEX IX_PrMaintenanceReason_Company_Active_Type
        ON dbo.PrMaintenanceReason (CompCode, Active, ReasonType);
    PRINT N'Created dbo.PrMaintenanceReason.';
END
ELSE PRINT N'dbo.PrMaintenanceReason already exists.';
GO

-- ─── PrPreventive tenant / status (nullable first) ───────────────────────────
IF COL_LENGTH(N'dbo.PrPreventive', N'Status') IS NULL
BEGIN
    ALTER TABLE dbo.PrPreventive ADD Status nvarchar(15) NULL;
    PRINT N'Added dbo.PrPreventive.Status (nullable).';
END
GO
IF COL_LENGTH(N'dbo.PrPreventive', N'CompletedOn') IS NULL
    ALTER TABLE dbo.PrPreventive ADD CompletedOn datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.PrPreventive', N'CompletedBy') IS NULL
    ALTER TABLE dbo.PrPreventive ADD CompletedBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.PrPreventive', N'CompCode') IS NULL
    ALTER TABLE dbo.PrPreventive ADD CompCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.PrPreventive', N'BranchCode') IS NULL
    ALTER TABLE dbo.PrPreventive ADD BranchCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.PrPreventive', N'LocCode') IS NULL
    ALTER TABLE dbo.PrPreventive ADD LocCode nvarchar(10) NULL;
GO

UPDATE p
SET Status = N'PLANNED'
FROM dbo.PrPreventive p
WHERE p.Status IS NULL OR LTRIM(RTRIM(p.Status)) = N'';
GO

-- Backfill CompCode from PrMachine only when unambiguous
UPDATE p
SET CompCode = x.CompCode,
    BranchCode = COALESCE(p.BranchCode, x.BranchCode),
    LocCode = COALESCE(p.LocCode, x.LocCode)
FROM dbo.PrPreventive p
INNER JOIN (
    SELECT Machine_Cd, MIN(CompCode) AS CompCode, MIN(BranchCode) AS BranchCode, MIN(LocCode) AS LocCode
    FROM dbo.PrMachine
    GROUP BY Machine_Cd
    HAVING COUNT(DISTINCT CompCode) = 1
) x ON x.Machine_Cd = p.Machine_Cd
WHERE p.CompCode IS NULL OR LTRIM(RTRIM(p.CompCode)) = N'';
GO

IF EXISTS (
    SELECT 1 FROM dbo.PrPreventive
    WHERE CompCode IS NULL OR LTRIM(RTRIM(CompCode)) = N''
)
BEGIN
    SELECT UID, Machine_Cd, Down_Dt FROM dbo.PrPreventive
    WHERE CompCode IS NULL OR LTRIM(RTRIM(CompCode)) = N'';
    THROW 52010, 'PrPreventive CompCode backfill incomplete / ambiguous. Repair then re-run.', 1;
END
GO

IF EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.PrPreventive') AND name = N'DF_PrPreventive_Status'
)
    ALTER TABLE dbo.PrPreventive DROP CONSTRAINT DF_PrPreventive_Status;
GO
ALTER TABLE dbo.PrPreventive ALTER COLUMN Status nvarchar(15) NOT NULL;
ALTER TABLE dbo.PrPreventive ADD CONSTRAINT DF_PrPreventive_Status DEFAULT (N'PLANNED') FOR Status;
GO
ALTER TABLE dbo.PrPreventive ALTER COLUMN CompCode nvarchar(10) NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PrPreventive_Company_Machine_Date' AND object_id = OBJECT_ID(N'dbo.PrPreventive'))
    CREATE NONCLUSTERED INDEX IX_PrPreventive_Company_Machine_Date
        ON dbo.PrPreventive (CompCode, Machine_Cd, Down_Dt);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PrPreventive_Company_Status_Date' AND object_id = OBJECT_ID(N'dbo.PrPreventive'))
    CREATE NONCLUSTERED INDEX IX_PrPreventive_Company_Status_Date
        ON dbo.PrPreventive (CompCode, Status, Down_Dt);
GO

-- ─── PrMacMaintenance enhancements ───────────────────────────────────────────
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'StartDateTime') IS NULL
    ALTER TABLE dbo.PrMacMaintenance ADD StartDateTime datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'EndDateTime') IS NULL
    ALTER TABLE dbo.PrMacMaintenance ADD EndDateTime datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'ReasonCd') IS NULL
    ALTER TABLE dbo.PrMacMaintenance ADD ReasonCd nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'PartsCost') IS NULL
BEGIN
    ALTER TABLE dbo.PrMacMaintenance ADD PartsCost decimal(19,2) NOT NULL
        CONSTRAINT DF_PrMacMaintenance_PartsCost DEFAULT (0);
END
GO
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'LabourCost') IS NULL
BEGIN
    ALTER TABLE dbo.PrMacMaintenance ADD LabourCost decimal(19,2) NOT NULL
        CONSTRAINT DF_PrMacMaintenance_LabourCost DEFAULT (0);
END
GO
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'OtherCost') IS NULL
BEGIN
    ALTER TABLE dbo.PrMacMaintenance ADD OtherCost decimal(19,2) NOT NULL
        CONSTRAINT DF_PrMacMaintenance_OtherCost DEFAULT (0);
END
GO
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'PreventiveUid') IS NULL
    ALTER TABLE dbo.PrMacMaintenance ADD PreventiveUid int NULL;
GO
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'Remark') IS NULL
    ALTER TABLE dbo.PrMacMaintenance ADD Remark nvarchar(500) NULL;
GO
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'CompCode') IS NULL
    ALTER TABLE dbo.PrMacMaintenance ADD CompCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'BranchCode') IS NULL
    ALTER TABLE dbo.PrMacMaintenance ADD BranchCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.PrMacMaintenance', N'LocCode') IS NULL
    ALTER TABLE dbo.PrMacMaintenance ADD LocCode nvarchar(10) NULL;
GO

UPDATE m
SET CompCode = x.CompCode,
    BranchCode = COALESCE(m.BranchCode, x.BranchCode),
    LocCode = COALESCE(m.LocCode, x.LocCode)
FROM dbo.PrMacMaintenance m
INNER JOIN (
    SELECT Machine_Cd, MIN(CompCode) AS CompCode, MIN(BranchCode) AS BranchCode, MIN(LocCode) AS LocCode
    FROM dbo.PrMachine
    GROUP BY Machine_Cd
    HAVING COUNT(DISTINCT CompCode) = 1
) x ON x.Machine_Cd = m.MacCode
WHERE m.CompCode IS NULL OR LTRIM(RTRIM(m.CompCode)) = N'';
GO

-- Rows with blank MacCode cannot be tenant-mapped safely — leave for operator if any remain
IF EXISTS (
    SELECT 1 FROM dbo.PrMacMaintenance
    WHERE (CompCode IS NULL OR LTRIM(RTRIM(CompCode)) = N'')
      AND MacCode IS NOT NULL AND LTRIM(RTRIM(MacCode)) <> N''
)
BEGIN
    SELECT ID, MacCode, TrxDate FROM dbo.PrMacMaintenance
    WHERE (CompCode IS NULL OR LTRIM(RTRIM(CompCode)) = N'')
      AND MacCode IS NOT NULL AND LTRIM(RTRIM(MacCode)) <> N'';
    THROW 52011, 'PrMacMaintenance CompCode backfill incomplete / ambiguous. Repair then re-run.', 1;
END
GO

-- Blank-machine legacy rows: assign a sentinel only if none remain with machine codes; else leave nullable until cleaned
-- Prefer fail-closed when company cannot be known for machine-linked rows (handled above).
-- For fully blank MacCode rows, set CompCode from single-company DB if exactly one company exists.
IF EXISTS (SELECT 1 FROM dbo.PrMacMaintenance WHERE CompCode IS NULL OR LTRIM(RTRIM(CompCode)) = N'')
AND (SELECT COUNT(DISTINCT CompCode) FROM dbo.PrMachine WHERE CompCode IS NOT NULL) = 1
BEGIN
    DECLARE @OnlyComp nvarchar(10) = (SELECT TOP 1 CompCode FROM dbo.PrMachine WHERE CompCode IS NOT NULL);
    UPDATE dbo.PrMacMaintenance SET CompCode = @OnlyComp
    WHERE CompCode IS NULL OR LTRIM(RTRIM(CompCode)) = N'';
END
GO

IF EXISTS (
    SELECT 1 FROM dbo.PrMacMaintenance
    WHERE CompCode IS NULL OR LTRIM(RTRIM(CompCode)) = N''
)
BEGIN
    SELECT ID, MacCode, TrxDate FROM dbo.PrMacMaintenance
    WHERE CompCode IS NULL OR LTRIM(RTRIM(CompCode)) = N'';
    THROW 52012, 'PrMacMaintenance still missing CompCode. Repair then re-run.', 1;
END
GO

ALTER TABLE dbo.PrMacMaintenance ALTER COLUMN CompCode nvarchar(10) NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrMacMaintenance_PartsCost')
    ALTER TABLE dbo.PrMacMaintenance ADD CONSTRAINT CK_PrMacMaintenance_PartsCost CHECK (PartsCost >= 0);
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrMacMaintenance_LabourCost')
    ALTER TABLE dbo.PrMacMaintenance ADD CONSTRAINT CK_PrMacMaintenance_LabourCost CHECK (LabourCost >= 0);
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrMacMaintenance_OtherCost')
    ALTER TABLE dbo.PrMacMaintenance ADD CONSTRAINT CK_PrMacMaintenance_OtherCost CHECK (OtherCost >= 0);
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_PrMacMaintenance_EndAfterStart')
    ALTER TABLE dbo.PrMacMaintenance ADD CONSTRAINT CK_PrMacMaintenance_EndAfterStart
        CHECK (EndDateTime IS NULL OR StartDateTime IS NULL OR EndDateTime >= StartDateTime);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PrMacMaintenance_Company_Machine_Date' AND object_id = OBJECT_ID(N'dbo.PrMacMaintenance'))
    CREATE NONCLUSTERED INDEX IX_PrMacMaintenance_Company_Machine_Date
        ON dbo.PrMacMaintenance (CompCode, MacCode, TrxDate);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PrMacMaintenance_Company_Type_Date' AND object_id = OBJECT_ID(N'dbo.PrMacMaintenance'))
    CREATE NONCLUSTERED INDEX IX_PrMacMaintenance_Company_Type_Date
        ON dbo.PrMacMaintenance (CompCode, MType, TrxDate);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PrMacMaintenance_Company_Status_Date' AND object_id = OBJECT_ID(N'dbo.PrMacMaintenance'))
    CREATE NONCLUSTERED INDEX IX_PrMacMaintenance_Company_Status_Date
        ON dbo.PrMacMaintenance (CompCode, Status, TrxDate);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PrMacMaintenance_PreventiveUid' AND object_id = OBJECT_ID(N'dbo.PrMacMaintenance'))
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_PrMacMaintenance_PreventiveUid
        ON dbo.PrMacMaintenance (PreventiveUid)
        WHERE PreventiveUid IS NOT NULL;
    PRINT N'Created UX_PrMacMaintenance_PreventiveUid.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_PrMacMaintenance_Preventive'
)
AND OBJECT_ID(N'dbo.PrPreventive', N'U') IS NOT NULL
BEGIN
    ALTER TABLE dbo.PrMacMaintenance
        ADD CONSTRAINT FK_PrMacMaintenance_Preventive
        FOREIGN KEY (PreventiveUid) REFERENCES dbo.PrPreventive (UID);
    PRINT N'Added FK_PrMacMaintenance_Preventive (NO ACTION).';
END
GO

PRINT N'=== enhance-production-machine-maintenance complete ===';
