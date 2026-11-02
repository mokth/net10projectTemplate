/* Additive, idempotent DBA-run schema for PR/PO approval.
   No startup migration. Safe to execute repeatedly.
   Feature-off does not drop tables; audit remains. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.PoApprovalPolicy', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoApprovalPolicy
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        DocType nvarchar(10) NOT NULL,
        Version int NOT NULL,
        Enabled bit NOT NULL CONSTRAINT DF_PoApprovalPolicy_Enabled DEFAULT (0),
        IsPublished bit NOT NULL CONSTRAINT DF_PoApprovalPolicy_IsPublished DEFAULT (0),
        LegacyDraftMode nvarchar(40) NOT NULL CONSTRAINT DF_PoApprovalPolicy_Legacy DEFAULT (N'REQUIRE_GOING_FORWARD'),
        CutoverAtUtc datetime2 NULL,
        EffectiveAtUtc datetime2 NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(20) NOT NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PoApprovalPolicy PRIMARY KEY CLUSTERED (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoApprovalPolicy_Company_DocType_Version' AND object_id = OBJECT_ID(N'dbo.PoApprovalPolicy'))
    CREATE UNIQUE INDEX UX_PoApprovalPolicy_Company_DocType_Version
        ON dbo.PoApprovalPolicy (CompanyCode, DocType, Version);
GO

IF OBJECT_ID(N'dbo.PoApprovalBand', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoApprovalBand
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        PolicyId bigint NOT NULL,
        Currency nvarchar(20) NOT NULL,
        MinInclusive decimal(18,2) NOT NULL,
        MaxExclusive decimal(18,2) NULL,
        NotRequired bit NOT NULL CONSTRAINT DF_PoApprovalBand_NotRequired DEFAULT (0),
        Sequence int NOT NULL,
        CONSTRAINT PK_PoApprovalBand PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PoApprovalBand_Policy FOREIGN KEY (PolicyId)
            REFERENCES dbo.PoApprovalPolicy (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoApprovalBand_Policy_Currency_Seq' AND object_id = OBJECT_ID(N'dbo.PoApprovalBand'))
    CREATE UNIQUE INDEX UX_PoApprovalBand_Policy_Currency_Seq
        ON dbo.PoApprovalBand (PolicyId, Currency, Sequence);
GO

IF OBJECT_ID(N'dbo.PoApprovalLevel', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoApprovalLevel
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        BandId bigint NOT NULL,
        Sequence int NOT NULL,
        MatchMode nvarchar(10) NOT NULL,
        CONSTRAINT PK_PoApprovalLevel PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PoApprovalLevel_Band FOREIGN KEY (BandId)
            REFERENCES dbo.PoApprovalBand (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoApprovalLevel_Band_Seq' AND object_id = OBJECT_ID(N'dbo.PoApprovalLevel'))
    CREATE UNIQUE INDEX UX_PoApprovalLevel_Band_Seq
        ON dbo.PoApprovalLevel (BandId, Sequence);
GO

IF OBJECT_ID(N'dbo.PoApprovalLevelAssignee', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoApprovalLevelAssignee
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        LevelId bigint NOT NULL,
        Kind nvarchar(10) NOT NULL,
        RefCode nvarchar(50) NOT NULL,
        CONSTRAINT PK_PoApprovalLevelAssignee PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PoApprovalLevelAssignee_Level FOREIGN KEY (LevelId)
            REFERENCES dbo.PoApprovalLevel (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoApprovalLevelAssignee_Level_Kind_Ref' AND object_id = OBJECT_ID(N'dbo.PoApprovalLevelAssignee'))
    CREATE UNIQUE INDEX UX_PoApprovalLevelAssignee_Level_Kind_Ref
        ON dbo.PoApprovalLevelAssignee (LevelId, Kind, RefCode);
GO

IF OBJECT_ID(N'dbo.PoApprovalInstance', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoApprovalInstance
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DocType nvarchar(10) NOT NULL,
        DocNo nvarchar(30) NOT NULL,
        DocRev smallint NOT NULL CONSTRAINT DF_PoApprovalInstance_DocRev DEFAULT (0),
        Cycle int NOT NULL,
        PolicyId bigint NOT NULL,
        PolicyVersion int NOT NULL,
        BandId bigint NULL,
        Status nvarchar(20) NOT NULL,
        CurrentLevel int NOT NULL,
        Currency nvarchar(20) NOT NULL,
        Gross decimal(18,2) NOT NULL,
        Taxes decimal(18,2) NOT NULL,
        Total decimal(18,2) NOT NULL,
        MaterialFingerprint nvarchar(128) NOT NULL,
        SubmittedBy nvarchar(20) NULL,
        SubmittedAtUtc datetime2 NULL,
        IdempotencyKey nvarchar(80) NULL,
        CreatedDate datetime2 NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PoApprovalInstance PRIMARY KEY CLUSTERED (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoApprovalInstance_Doc_Cycle' AND object_id = OBJECT_ID(N'dbo.PoApprovalInstance'))
    CREATE UNIQUE INDEX UX_PoApprovalInstance_Doc_Cycle
        ON dbo.PoApprovalInstance (CompanyCode, BranchCode, DocType, DocNo, DocRev, Cycle);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoApprovalInstance_Active' AND object_id = OBJECT_ID(N'dbo.PoApprovalInstance'))
    CREATE UNIQUE INDEX UX_PoApprovalInstance_Active
        ON dbo.PoApprovalInstance (CompanyCode, BranchCode, DocType, DocNo, DocRev)
        WHERE Status IN (N'PENDING', N'APPROVED', N'NOT_REQUIRED');
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoApprovalInstance_Inbox' AND object_id = OBJECT_ID(N'dbo.PoApprovalInstance'))
    CREATE INDEX IX_PoApprovalInstance_Inbox
        ON dbo.PoApprovalInstance (CompanyCode, Status, SubmittedAtUtc);
GO

IF OBJECT_ID(N'dbo.PoApprovalAssignee', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoApprovalAssignee
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        InstanceId bigint NOT NULL,
        LevelSequence int NOT NULL,
        UserId nvarchar(20) NOT NULL,
        MatchMode nvarchar(10) NOT NULL,
        CONSTRAINT PK_PoApprovalAssignee PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PoApprovalAssignee_Instance FOREIGN KEY (InstanceId)
            REFERENCES dbo.PoApprovalInstance (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoApprovalAssignee_Instance_Level_User' AND object_id = OBJECT_ID(N'dbo.PoApprovalAssignee'))
    CREATE UNIQUE INDEX UX_PoApprovalAssignee_Instance_Level_User
        ON dbo.PoApprovalAssignee (InstanceId, LevelSequence, UserId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoApprovalAssignee_User' AND object_id = OBJECT_ID(N'dbo.PoApprovalAssignee'))
    CREATE INDEX IX_PoApprovalAssignee_User
        ON dbo.PoApprovalAssignee (UserId, InstanceId);
GO

IF OBJECT_ID(N'dbo.PoApprovalDecision', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoApprovalDecision
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        InstanceId bigint NOT NULL,
        LevelSequence int NOT NULL,
        UserId nvarchar(20) NOT NULL,
        Decision nvarchar(20) NOT NULL,
        Reason nvarchar(500) NULL,
        DecidedAtUtc datetime2 NOT NULL,
        CONSTRAINT PK_PoApprovalDecision PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PoApprovalDecision_Instance FOREIGN KEY (InstanceId)
            REFERENCES dbo.PoApprovalInstance (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoApprovalDecision_Vote' AND object_id = OBJECT_ID(N'dbo.PoApprovalDecision'))
    CREATE UNIQUE INDEX UX_PoApprovalDecision_Vote
        ON dbo.PoApprovalDecision (InstanceId, LevelSequence, UserId);
GO

IF OBJECT_ID(N'dbo.PoApprovalEvent', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoApprovalEvent
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        InstanceId bigint NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DocType nvarchar(10) NOT NULL,
        DocNo nvarchar(30) NOT NULL,
        DocRev smallint NOT NULL CONSTRAINT DF_PoApprovalEvent_DocRev DEFAULT (0),
        Cycle int NOT NULL CONSTRAINT DF_PoApprovalEvent_Cycle DEFAULT (0),
        EventType nvarchar(40) NOT NULL,
        PriorStatus nvarchar(20) NULL,
        NewStatus nvarchar(20) NULL,
        PolicyVersion int NOT NULL CONSTRAINT DF_PoApprovalEvent_PolicyVersion DEFAULT (0),
        Reason nvarchar(500) NULL,
        IdempotencyKey nvarchar(80) NULL,
        ActorUserId nvarchar(20) NOT NULL,
        OccurredAtUtc datetime2 NOT NULL,
        CONSTRAINT PK_PoApprovalEvent PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PoApprovalEvent_Instance FOREIGN KEY (InstanceId)
            REFERENCES dbo.PoApprovalInstance (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoApprovalEvent_Doc_Date' AND object_id = OBJECT_ID(N'dbo.PoApprovalEvent'))
    CREATE INDEX IX_PoApprovalEvent_Doc_Date
        ON dbo.PoApprovalEvent (CompanyCode, DocType, DocNo, OccurredAtUtc);
GO

IF OBJECT_ID(N'dbo.PoApprovalCutoverExemption', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoApprovalCutoverExemption
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DocType nvarchar(10) NOT NULL,
        DocNo nvarchar(30) NOT NULL,
        DocRev smallint NOT NULL CONSTRAINT DF_PoApprovalCutover_DocRev DEFAULT (0),
        PolicyId bigint NOT NULL,
        CutoverAtUtc datetime2 NOT NULL,
        Reason nvarchar(200) NOT NULL,
        ActorUserId nvarchar(20) NOT NULL,
        CreatedDate datetime2 NOT NULL,
        CONSTRAINT PK_PoApprovalCutoverExemption PRIMARY KEY CLUSTERED (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoApprovalCutover_Doc_Policy' AND object_id = OBJECT_ID(N'dbo.PoApprovalCutoverExemption'))
    CREATE UNIQUE INDEX UX_PoApprovalCutover_Doc_Policy
        ON dbo.PoApprovalCutoverExemption (CompanyCode, BranchCode, DocType, DocNo, DocRev, PolicyId);
GO
