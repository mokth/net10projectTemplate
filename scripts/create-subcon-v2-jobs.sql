-- =============================================================================
-- Subcontracting Phase G2 Additive Schema (Draft-Only Job & BOM/Kit Snapshot)
-- Preserves all Phase-1 SubconPolicy / SubconDispatch / SubconReceipt tables.
-- =============================================================================
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SubconJob', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubconJob
    (
        UID                  BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SubconJob PRIMARY KEY,
        CompanyCode          NVARCHAR(5)          NOT NULL,
        BranchCode           NVARCHAR(5)          NOT NULL,
        JobNo                NVARCHAR(20)         NOT NULL,
        TrxDate              DATETIME2            NOT NULL,
        Status               NVARCHAR(20)         NOT NULL,
        JobKind              NVARCHAR(20)         NOT NULL,
        Mode                 NVARCHAR(20)         NOT NULL,
        SupplierCode         NVARCHAR(20)         NOT NULL,
        SupplierName         NVARCHAR(100)        NULL,
        TargetItemCode       NVARCHAR(30)         NULL,
        TargetItemName       NVARCHAR(100)        NULL,
        TargetUom            NVARCHAR(10)         NULL,
        TargetQty            DECIMAL(18,4)        NOT NULL CONSTRAINT DF_SubconJob_TargetQty DEFAULT (0),
        DefinitionCode       NVARCHAR(20)         NULL,
        BomVersion           INT                  NULL,
        IsManualKit          BIT                  NOT NULL CONSTRAINT DF_SubconJob_IsManualKit DEFAULT (0),
        PrimaryDispatchDocNo NVARCHAR(20)         NULL,
        PromisedReturnDate   DATETIME2            NULL,
        PoNo                 NVARCHAR(30)         NULL,
        PoRelNo              SMALLINT             NULL,
        PoLineNo             SMALLINT             NULL,
        Remarks              NVARCHAR(500)        NULL,
        CreatedDate          DATETIME2            NOT NULL,
        CreatedBy            NVARCHAR(20)         NOT NULL,
        ModifiedDate         DATETIME2            NULL,
        ModifiedBy           NVARCHAR(20)         NULL,
        RowVersion           ROWVERSION           NOT NULL
    );

    CREATE UNIQUE INDEX UX_SubconJob_Company_Branch_No
        ON dbo.SubconJob (CompanyCode, BranchCode, JobNo);
END;

IF OBJECT_ID(N'dbo.SubconJobSourceSnapshot', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubconJobSourceSnapshot
    (
        UID            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SubconJobSourceSnapshot PRIMARY KEY,
        JobId          BIGINT               NOT NULL,
        SourceKind     NVARCHAR(20)         NOT NULL,
        ProdCode       NVARCHAR(30)         NOT NULL,
        DefinitionCode NVARCHAR(20)         NULL,
        BomVersion     INT                  NULL,
        BomHdrUid      BIGINT               NULL,
        RootQty        DECIMAL(18,4)        NOT NULL,
        RootUom        NVARCHAR(10)         NULL,
        CapturedDate   DATETIME2            NOT NULL,
        CapturedBy     NVARCHAR(20)         NOT NULL,
        CONSTRAINT FK_SubconJobSourceSnapshot_Job
            FOREIGN KEY (JobId) REFERENCES dbo.SubconJob (UID)
    );
END;

IF OBJECT_ID(N'dbo.SubconJobMaterial', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubconJobMaterial
    (
        UID              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SubconJobMaterial PRIMARY KEY,
        JobId            BIGINT               NOT NULL,
        LineNo           INT                  NOT NULL,
        ItemCode         NVARCHAR(30)         NOT NULL,
        ItemName         NVARCHAR(100)        NULL,
        Uom              NVARCHAR(10)         NOT NULL,
        BaseUom          NVARCHAR(10)         NULL,
        RequiredQty      DECIMAL(18,4)        NOT NULL,
        SendNowQty       DECIMAL(18,4)        NOT NULL,
        ScrapPercent     DECIMAL(9,4)         NOT NULL CONSTRAINT DF_SubconJobMaterial_Scrap DEFAULT (0),
        WarehouseCode    NVARCHAR(10)         NULL,
        LocationCode     NVARCHAR(10)         NULL,
        LotNo            NVARCHAR(50)         NULL,
        IvBalLocId       INT                  NULL,
        IsManualAddition BIT                  NOT NULL CONSTRAINT DF_SubconJobMaterial_Manual DEFAULT (0),
        Remarks          NVARCHAR(500)        NULL,
        CONSTRAINT FK_SubconJobMaterial_Job
            FOREIGN KEY (JobId) REFERENCES dbo.SubconJob (UID)
    );

    CREATE UNIQUE INDEX UX_SubconJobMaterial_Job_LineNo
        ON dbo.SubconJobMaterial (JobId, LineNo);
END;

IF OBJECT_ID(N'dbo.SubconJobOutput', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubconJobOutput
    (
        UID           BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SubconJobOutput PRIMARY KEY,
        JobId         BIGINT               NOT NULL,
        LineNo        INT                  NOT NULL,
        OutputKind    NVARCHAR(20)         NOT NULL,
        ItemCode      NVARCHAR(30)         NOT NULL,
        ItemName      NVARCHAR(100)        NULL,
        Uom           NVARCHAR(10)         NOT NULL,
        ExpectedQty   DECIMAL(18,4)        NOT NULL,
        WarehouseCode NVARCHAR(10)         NULL,
        LocationCode  NVARCHAR(10)         NULL,
        Remarks       NVARCHAR(500)        NULL,
        CONSTRAINT FK_SubconJobOutput_Job
            FOREIGN KEY (JobId) REFERENCES dbo.SubconJob (UID)
    );

    CREATE UNIQUE INDEX UX_SubconJobOutput_Job_LineNo
        ON dbo.SubconJobOutput (JobId, LineNo);
END;

IF OBJECT_ID(N'dbo.SubconSupplierAssignment', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubconSupplierAssignment
    (
        UID                BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SubconSupplierAssignment PRIMARY KEY,
        JobId              BIGINT               NOT NULL,
        LineNo             INT                  NOT NULL,
        SupplierCode       NVARCHAR(20)         NOT NULL,
        SupplierName       NVARCHAR(100)        NULL,
        AssignedQty        DECIMAL(18,4)        NOT NULL,
        PoNo               NVARCHAR(30)         NULL,
        PoRelNo            SMALLINT             NULL,
        PoLineNo           SMALLINT             NULL,
        PromisedReturnDate DATETIME2            NULL,
        DispatchDocNo      NVARCHAR(20)         NULL,
        Remarks            NVARCHAR(500)        NULL,
        CONSTRAINT FK_SubconSupplierAssignment_Job
            FOREIGN KEY (JobId) REFERENCES dbo.SubconJob (UID)
    );

    CREATE UNIQUE INDEX UX_SubconSupplierAssignment_Job_LineNo
        ON dbo.SubconSupplierAssignment (JobId, LineNo);
END;
