/* Additive, idempotent DBA-run schema for Subcontracting Phase 1 (draft shell).
   No startup migration. Safe to execute repeatedly.
   Does NOT mutate inventory, WIP, costing, or posting tables.
   CompanyCode / BranchCode are nvarchar(5) to match inventory/production. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.SubconPolicy', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubconPolicy
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        Version int NOT NULL,
        SubconEnabled bit NOT NULL CONSTRAINT DF_SubconPolicy_Enabled DEFAULT (0),
        InventoryModeEnabled bit NOT NULL CONSTRAINT DF_SubconPolicy_Inventory DEFAULT (0),
        ProductionModeEnabled bit NOT NULL CONSTRAINT DF_SubconPolicy_Production DEFAULT (0),
        IsPublished bit NOT NULL CONSTRAINT DF_SubconPolicy_Published DEFAULT (0),
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(20) NOT NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SubconPolicy PRIMARY KEY CLUSTERED (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubconPolicy_Company_Version' AND object_id = OBJECT_ID(N'dbo.SubconPolicy'))
    CREATE UNIQUE INDEX UX_SubconPolicy_Company_Version
        ON dbo.SubconPolicy (CompanyCode, Version);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubconPolicy_Company_Published' AND object_id = OBJECT_ID(N'dbo.SubconPolicy'))
    CREATE UNIQUE INDEX UX_SubconPolicy_Company_Published
        ON dbo.SubconPolicy (CompanyCode)
        WHERE IsPublished = 1;
GO

IF OBJECT_ID(N'dbo.SubconDispatch', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubconDispatch
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        DocNo nvarchar(20) NOT NULL,
        TrxDate datetime2 NOT NULL,
        Status nvarchar(20) NOT NULL,
        Mode nvarchar(20) NOT NULL,
        SupplierCode nvarchar(20) NOT NULL,
        SupplierName nvarchar(100) NULL,
        PromisedReturnDate datetime2 NULL,
        PoNo nvarchar(30) NULL,
        PoRelNo smallint NULL,
        PoLineNo smallint NULL,
        Remarks nvarchar(500) NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(20) NOT NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SubconDispatch PRIMARY KEY CLUSTERED (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubconDispatch_Company_Branch_No' AND object_id = OBJECT_ID(N'dbo.SubconDispatch'))
    CREATE UNIQUE INDEX UX_SubconDispatch_Company_Branch_No
        ON dbo.SubconDispatch (CompanyCode, BranchCode, DocNo);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SubconDispatch_Workbench' AND object_id = OBJECT_ID(N'dbo.SubconDispatch'))
    CREATE INDEX IX_SubconDispatch_Workbench
        ON dbo.SubconDispatch (CompanyCode, BranchCode, Status, TrxDate);
GO

IF OBJECT_ID(N'dbo.SubconDispatchLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubconDispatchLine
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        DispatchId bigint NOT NULL,
        [LineNo] int NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        ItemName nvarchar(100) NULL,
        Uom nvarchar(10) NOT NULL,
        BaseUom nvarchar(10) NULL,
        Qty decimal(18,4) NOT NULL,
        WarehouseCode nvarchar(10) NULL,
        LocationCode nvarchar(10) NULL,
        LotNo nvarchar(50) NULL,
        IvBalLocId int NULL,
        ProductionBalLotUid bigint NULL,
        WorkOrderNo nvarchar(30) NULL,
        OperationCode nvarchar(20) NULL,
        PromisedReturnDate datetime2 NULL,
        Remarks nvarchar(500) NULL,
        CONSTRAINT PK_SubconDispatchLine PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_SubconDispatchLine_Dispatch FOREIGN KEY (DispatchId)
            REFERENCES dbo.SubconDispatch (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubconDispatchLine_Dispatch_LineNo' AND object_id = OBJECT_ID(N'dbo.SubconDispatchLine'))
    CREATE UNIQUE INDEX UX_SubconDispatchLine_Dispatch_LineNo
        ON dbo.SubconDispatchLine (DispatchId, [LineNo]);
GO

IF OBJECT_ID(N'dbo.SubconReceipt', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubconReceipt
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        DocNo nvarchar(20) NOT NULL,
        TrxDate datetime2 NOT NULL,
        Status nvarchar(20) NOT NULL,
        SupplierCode nvarchar(20) NOT NULL,
        SupplierName nvarchar(100) NULL,
        Remarks nvarchar(500) NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(20) NOT NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SubconReceipt PRIMARY KEY CLUSTERED (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubconReceipt_Company_Branch_No' AND object_id = OBJECT_ID(N'dbo.SubconReceipt'))
    CREATE UNIQUE INDEX UX_SubconReceipt_Company_Branch_No
        ON dbo.SubconReceipt (CompanyCode, BranchCode, DocNo);
GO

IF OBJECT_ID(N'dbo.SubconReceiptLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubconReceiptLine
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        ReceiptId bigint NOT NULL,
        [LineNo] int NOT NULL,
        DispatchId bigint NOT NULL,
        DispatchLineId bigint NOT NULL,
        Qty decimal(18,4) NOT NULL,
        Uom nvarchar(10) NOT NULL,
        Remarks nvarchar(500) NULL,
        CONSTRAINT PK_SubconReceiptLine PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_SubconReceiptLine_Receipt FOREIGN KEY (ReceiptId)
            REFERENCES dbo.SubconReceipt (UID),
        CONSTRAINT FK_SubconReceiptLine_Dispatch FOREIGN KEY (DispatchId)
            REFERENCES dbo.SubconDispatch (UID),
        CONSTRAINT FK_SubconReceiptLine_DispatchLine FOREIGN KEY (DispatchLineId)
            REFERENCES dbo.SubconDispatchLine (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubconReceiptLine_Receipt_LineNo' AND object_id = OBJECT_ID(N'dbo.SubconReceiptLine'))
    CREATE UNIQUE INDEX UX_SubconReceiptLine_Receipt_LineNo
        ON dbo.SubconReceiptLine (ReceiptId, [LineNo]);
GO
