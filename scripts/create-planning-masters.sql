-- Create Planning master tables when missing (local/dev parity with EF entities).
-- Idempotent. Does NOT drop data.
-- Live production schemas may already exist with additional constraints — Phase 0 matrix must be re-verified against prod.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.PrWorkCentre', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkCentre (
        Wrk_Ctr_Cd nvarchar(10) NOT NULL PRIMARY KEY,
        Wrk_Ctr_Des nvarchar(30) NULL,
        Class nvarchar(10) NULL,
        Created datetime NULL,
        Updated datetime NULL,
        UserID nvarchar(10) NULL,
        UpdatedUID nvarchar(10) NULL,
        CompCode nvarchar(10) NULL,
        BranchCode nvarchar(10) NULL,
        LocCode nvarchar(10) NULL
    );
END
GO

IF OBJECT_ID(N'dbo.PrProcess', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrProcess (
        Process_Cd nvarchar(10) NOT NULL,
        Process_Des nvarchar(30) NULL,
        Sequence int NULL,
        Work_Centre nvarchar(10) NOT NULL,
        Stock bit NULL,
        Created datetime NULL,
        Updated datetime NULL,
        UserID nvarchar(20) NULL,
        UpdatedUID nvarchar(20) NULL,
        CompCode nvarchar(10) NULL,
        BranchCode nvarchar(10) NULL,
        LocCode nvarchar(10) NULL,
        CONSTRAINT PK_PrProcess PRIMARY KEY (Process_Cd, Work_Centre)
    );
END
GO

IF OBJECT_ID(N'dbo.PrMachine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrMachine (
        Machine_Cd nvarchar(10) NOT NULL,
        Machine_Des nvarchar(30) NULL,
        Process_Cd nvarchar(10) NOT NULL,
        Created datetime NULL,
        Updated datetime NULL,
        UserID nvarchar(20) NULL,
        UpdatedUID nvarchar(20) NULL,
        CompCode nvarchar(10) NULL,
        BranchCode nvarchar(10) NULL,
        LocCode nvarchar(10) NULL,
        ConversionTime float NULL,
        StartupTime float NULL,
        QueueTime float NULL,
        CONSTRAINT PK_PrMachine PRIMARY KEY (Machine_Cd, Process_Cd)
    );
    CREATE UNIQUE INDEX UX_PrMachine_MachineComp ON dbo.PrMachine(Machine_Cd, CompCode) WHERE CompCode IS NOT NULL;
END
GO

IF OBJECT_ID(N'dbo.PrShift', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrShift (
        Shift_Cd nvarchar(10) NOT NULL PRIMARY KEY,
        Shift_Des nvarchar(30) NULL,
        Start_Tm datetime NULL,
        End_Tm datetime NULL,
        Break_Tm1From datetime NULL,
        Break_Tm1To datetime NULL,
        Break_Tm2From datetime NULL,
        Break_Tm2To datetime NULL,
        Break_Tm3From datetime NULL,
        Break_Tm3To datetime NULL,
        Break_Tm4From datetime NULL,
        Break_Tm4To datetime NULL,
        Break_Tm5From datetime NULL,
        Break_Tm5To datetime NULL,
        BreakStorageVersion tinyint NOT NULL CONSTRAINT DF_PrShift_BreakStorageVersion DEFAULT (0),
        OT_StartTime datetime NULL,
        Override_MRP_Plan nvarchar(1) NULL,
        Created datetime NULL,
        Updated datetime NULL,
        UserID nvarchar(10) NULL,
        UpdatedUID nvarchar(10) NULL,
        CompCode nvarchar(10) NULL,
        BranchCode nvarchar(10) NULL,
        LocCode nvarchar(10) NULL,
        totaltime float NULL
    );
END
GO

IF COL_LENGTH(N'dbo.PrShift', N'BreakStorageVersion') IS NULL
BEGIN
    ALTER TABLE dbo.PrShift ADD BreakStorageVersion tinyint NOT NULL
        CONSTRAINT DF_PrShift_BreakStorageVersion DEFAULT (0);
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PrShift')
      AND name = N'UX_PrShift_ShiftCd_CompCode'
)
BEGIN
    CREATE UNIQUE INDEX UX_PrShift_ShiftCd_CompCode ON dbo.PrShift (Shift_Cd, CompCode);
END
GO

IF OBJECT_ID(N'dbo.PrShiftBreak', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrShiftBreak (
        CompCode nvarchar(10) NOT NULL,
        Shift_Cd nvarchar(10) NOT NULL,
        BreakSeq tinyint NOT NULL,
        BreakFrom datetime NOT NULL,
        BreakTo datetime NOT NULL,
        Created datetime NULL,
        UserID nvarchar(10) NULL,
        CONSTRAINT PK_PrShiftBreak PRIMARY KEY (CompCode, Shift_Cd, BreakSeq),
        CONSTRAINT CK_PrShiftBreak_BreakSeq CHECK (BreakSeq BETWEEN 1 AND 5),
        CONSTRAINT FK_PrShiftBreak_PrShift FOREIGN KEY (Shift_Cd, CompCode)
            REFERENCES dbo.PrShift (Shift_Cd, CompCode)
            ON DELETE CASCADE
    );
END
GO

IF OBJECT_ID(N'dbo.PrShiftGroup', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrShiftGroup (
        ShfGrp_Cd nvarchar(10) NOT NULL,
        ShfGrp_Des nvarchar(30) NOT NULL,
        Shift_Cd nvarchar(10) NOT NULL,
        TotalTime int NULL,
        Created datetime NULL,
        Updated datetime NULL,
        UserID nvarchar(10) NULL,
        UpdatedUID nvarchar(10) NULL,
        CompCode nvarchar(10) NULL,
        BranchCode nvarchar(10) NULL,
        LocCode nvarchar(10) NULL,
        DefaultGrp bit NULL,
        ShiftColor nvarchar(20) NULL,
        CONSTRAINT PK_PrShiftGroup PRIMARY KEY (ShfGrp_Cd, Shift_Cd)
    );
END
GO

IF OBJECT_ID(N'dbo.PrCalendar', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrCalendar (
        Dt datetime NOT NULL PRIMARY KEY,
        Date_Cd nvarchar(1) NULL,
        Created datetime NULL,
        Updated datetime NULL,
        UserID nvarchar(10) NULL,
        CompCode nvarchar(10) NULL,
        BranchCode nvarchar(10) NULL,
        LocCode nvarchar(10) NULL
    );
END
GO

IF OBJECT_ID(N'dbo.PrShiftCalendar', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrShiftCalendar (
        Dt datetime NOT NULL,
        Date_Cd nvarchar(1) NULL,
        ShfGrp_Cd nvarchar(10) NULL,
        MachineCode nvarchar(10) NOT NULL,
        Created datetime NULL,
        Updated datetime NULL,
        UserID nvarchar(10) NULL,
        CompCode nvarchar(10) NULL,
        BranchCode nvarchar(10) NULL,
        LocCode nvarchar(10) NULL,
        CONSTRAINT PK_PrShiftCalendar PRIMARY KEY (Dt, MachineCode)
    );
END
GO

IF OBJECT_ID(N'dbo.PrHoliday', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrHoliday (
        UID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        DateOff datetime NULL,
        Description nvarchar(100) NULL,
        Year int NULL
    );
END
GO

IF OBJECT_ID(N'dbo.PrOperator', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrOperator (
        Code nvarchar(20) NOT NULL PRIMARY KEY,
        Name nvarchar(100) NOT NULL,
        Active bit NULL,
        Created datetime NULL,
        Updated datetime NULL,
        UserID nvarchar(10) NULL,
        UpdatedUID nvarchar(10) NULL,
        CompanyCode nvarchar(5) NULL,
        BranchCode nvarchar(5) NULL,
        LocationCode nvarchar(5) NULL
    );
END
GO

IF OBJECT_ID(N'dbo.PrWorkPefix', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkPefix (
        Prefix nvarchar(10) NOT NULL PRIMARY KEY,
        Description nvarchar(30) NULL,
        Created datetime NULL,
        Updated datetime NULL,
        UserID nvarchar(10) NULL,
        UpdatedUID nvarchar(10) NULL,
        CompCode nvarchar(10) NULL,
        BranchCode nvarchar(10) NULL,
        LocCode nvarchar(10) NULL
    );
END
GO

IF OBJECT_ID(N'dbo.PrMacSeq', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrMacSeq (
        Machine_Cd nvarchar(10) NOT NULL,
        Process_Cd nvarchar(10) NOT NULL,
        SeqNo int NULL,
        Created datetime NULL,
        Updated datetime NULL,
        UserID nvarchar(10) NULL,
        UpdatedUID nvarchar(10) NULL,
        CompCode nvarchar(10) NULL,
        BranchCode nvarchar(10) NULL,
        LocCode nvarchar(10) NULL,
        CONSTRAINT PK_PrMacSeq PRIMARY KEY (Machine_Cd, Process_Cd)
    );
END
GO

IF OBJECT_ID(N'dbo.PrPreventive', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrPreventive (
        UID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Machine_Cd nvarchar(10) NOT NULL,
        Down_Dt datetime NOT NULL,
        Start_Tm datetime NOT NULL,
        End_Tm datetime NOT NULL,
        Reason_Cd nvarchar(10) NULL,
        Created datetime NULL,
        Updated datetime NULL,
        UserID nvarchar(10) NULL,
        Remark nvarchar(100) NULL
    );
END
GO

PRINT N'Planning master tables ensured.';
GO
