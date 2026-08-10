USE ERPLiteEx;
GO

IF OBJECT_ID('dbo.InventoryDocument', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventoryDocument (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryDocument PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        DocNo nvarchar(30) NOT NULL,
        DocType int NOT NULL,
        DocDate datetime2 NOT NULL,
        WarehouseId bigint NULL,
        SourceWarehouseId bigint NULL,
        DestinationWarehouseId bigint NULL,
        SourceLocationId bigint NULL,
        DestinationLocationId bigint NULL,
        ReferenceNo nvarchar(50) NULL,
        Status int NOT NULL CONSTRAINT DF_InventoryDocument_Status DEFAULT (0),
        Remarks nvarchar(500) NULL,
        AllowZeroCost bit NOT NULL CONSTRAINT DF_InventoryDocument_AllowZeroCost DEFAULT (0),
        ApprovedBy nvarchar(50) NULL,
        ApprovedAtUtc datetime2 NULL,
        PostedBy nvarchar(50) NULL,
        PostedAtUtc datetime2 NULL,
        ReversalOfDocumentId bigint NULL,
        StockTakeId bigint NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_InventoryDocument_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_InventoryDocument_Company_Branch_DocType_DocNo UNIQUE (CompanyId, BranchId, DocType, DocNo),
        CONSTRAINT FK_InventoryDocument_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_InventoryDocument_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id)
    );
END
GO

IF OBJECT_ID('dbo.InventoryDocumentLine', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventoryDocumentLine (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryDocumentLine PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        DocumentId bigint NOT NULL,
        LineNo int NOT NULL,
        ItemVariantId bigint NOT NULL,
        UOMId bigint NOT NULL,
        Qty decimal(19,6) NOT NULL,
        ConversionRateUsed decimal(19,10) NOT NULL CONSTRAINT DF_InventoryDocumentLine_Conv DEFAULT (1),
        QtyInBase decimal(19,6) NOT NULL,
        UnitCost decimal(19,6) NOT NULL CONSTRAINT DF_InventoryDocumentLine_UnitCost DEFAULT (0),
        TotalCost decimal(19,4) NOT NULL CONSTRAINT DF_InventoryDocumentLine_TotalCost DEFAULT (0),
        LocationId bigint NOT NULL,
        LotNo nvarchar(50) NULL,
        LotId bigint NULL,
        Direction int NULL,
        ReasonCodeId bigint NULL,
        Remarks nvarchar(500) NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_InventoryDocumentLine_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_InventoryDocumentLine_DocumentId_LineNo UNIQUE (DocumentId, LineNo),
        CONSTRAINT FK_InventoryDocumentLine_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_InventoryDocumentLine_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id),
        CONSTRAINT FK_InventoryDocumentLine_Document FOREIGN KEY (DocumentId) REFERENCES dbo.InventoryDocument(Id)
    );
END
GO

IF OBJECT_ID('dbo.StockLedger', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockLedger (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockLedger PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        TransactionDate datetime2 NOT NULL,
        LedgerSequence bigint NOT NULL,
        DocType int NOT NULL,
        DocNo nvarchar(30) NOT NULL,
        LineNo int NOT NULL,
        DocumentId bigint NOT NULL,
        DocumentLineId bigint NOT NULL,
        ReferenceNo nvarchar(50) NULL,
        PostedBy nvarchar(50) NULL,
        PostedAtUtc datetime2 NOT NULL,
        ItemVariantId bigint NOT NULL,
        SKU nvarchar(40) NOT NULL,
        ItemDescription nvarchar(200) NOT NULL,
        UOMId bigint NOT NULL,
        UOMCode nvarchar(10) NOT NULL,
        UnitQty decimal(19,6) NOT NULL,
        ConversionRateUsed decimal(19,10) NOT NULL,
        QtyInBase decimal(19,6) NOT NULL,
        QtyOutBase decimal(19,6) NOT NULL,
        WarehouseId bigint NOT NULL,
        WarehouseCode nvarchar(20) NOT NULL,
        LocationId bigint NOT NULL,
        LocationCode nvarchar(20) NOT NULL,
        LotId bigint NULL,
        LotNo nvarchar(50) NULL,
        ReasonCodeId bigint NULL,
        ReasonCodeValue nvarchar(20) NULL,
        UnitCost decimal(19,6) NOT NULL,
        Amount decimal(19,4) NOT NULL,
        CurrencyCode nvarchar(3) NOT NULL CONSTRAINT DF_StockLedger_Currency DEFAULT (N'MYR'),
        ExchangeRate decimal(19,10) NOT NULL CONSTRAINT DF_StockLedger_Fx DEFAULT (1),
        BaseAmount decimal(19,4) NOT NULL,
        CostingMethod int NOT NULL CONSTRAINT DF_StockLedger_Costing DEFAULT (1),
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_StockLedger_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_StockLedger_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_StockLedger_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id)
    );
    CREATE INDEX IX_StockLedger_Item_WH_Date ON dbo.StockLedger (CompanyId, BranchId, ItemVariantId, WarehouseId, TransactionDate, LedgerSequence, Id);
    CREATE INDEX IX_StockLedger_Doc ON dbo.StockLedger (CompanyId, BranchId, DocType, DocNo);
    CREATE INDEX IX_StockLedger_DocumentId ON dbo.StockLedger (CompanyId, BranchId, DocumentId);
    CREATE INDEX IX_StockLedger_WH_Date ON dbo.StockLedger (CompanyId, BranchId, WarehouseId, TransactionDate);
END
GO

IF OBJECT_ID('dbo.StockMovementAllocation', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockMovementAllocation (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockMovementAllocation PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        DocumentLineId bigint NOT NULL,
        StockLedgerId bigint NOT NULL,
        SourceLotId bigint NULL,
        TargetLotId bigint NULL,
        Quantity decimal(19,6) NOT NULL,
        UnitCost decimal(19,6) NOT NULL,
        Amount decimal(19,4) NOT NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_StockMovementAllocation_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_StockMovementAllocation_StockLedgerId UNIQUE (StockLedgerId),
        CONSTRAINT FK_StockMovementAllocation_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_StockMovementAllocation_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id),
        CONSTRAINT FK_StockMovementAllocation_StockLedger FOREIGN KEY (StockLedgerId) REFERENCES dbo.StockLedger(Id),
        CONSTRAINT FK_StockMovementAllocation_DocumentLine FOREIGN KEY (DocumentLineId) REFERENCES dbo.InventoryDocumentLine(Id)
    );
END
GO

IF OBJECT_ID('dbo.StockBalance', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockBalance (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockBalance PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        WarehouseId bigint NOT NULL,
        LocationId bigint NOT NULL,
        ItemVariantId bigint NOT NULL,
        QtyOnHand decimal(19,6) NOT NULL CONSTRAINT DF_StockBalance_Qty DEFAULT (0),
        ReservedQty decimal(19,6) NOT NULL CONSTRAINT DF_StockBalance_Reserved DEFAULT (0),
        LastUpdatedAtUtc datetime2 NOT NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_StockBalance_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_StockBalance_Grain UNIQUE (CompanyId, BranchId, WarehouseId, LocationId, ItemVariantId),
        CONSTRAINT FK_StockBalance_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_StockBalance_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id)
    );
END
GO

IF OBJECT_ID('dbo.ItemCost', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ItemCost (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ItemCost PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        WarehouseId bigint NOT NULL,
        ItemVariantId bigint NOT NULL,
        AverageCost decimal(19,6) NOT NULL CONSTRAINT DF_ItemCost_Avg DEFAULT (0),
        LastCost decimal(19,6) NOT NULL CONSTRAINT DF_ItemCost_Last DEFAULT (0),
        LastUpdatedAtUtc datetime2 NOT NULL,
        LastDocumentId bigint NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_ItemCost_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_ItemCost_Grain UNIQUE (CompanyId, BranchId, WarehouseId, ItemVariantId),
        CONSTRAINT FK_ItemCost_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_ItemCost_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id)
    );
END
GO

IF OBJECT_ID('dbo.DocumentSequence', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DocumentSequence (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_DocumentSequence PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        DocType nvarchar(10) NOT NULL,
        Prefix nvarchar(10) NOT NULL,
        YearMonth int NOT NULL,
        CurrentNumber bigint NOT NULL CONSTRAINT DF_DocumentSequence_Current DEFAULT (0),
        NumberLength int NOT NULL CONSTRAINT DF_DocumentSequence_Len DEFAULT (4),
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_DocumentSequence_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_DocumentSequence_Grain UNIQUE (CompanyId, BranchId, DocType, YearMonth),
        CONSTRAINT FK_DocumentSequence_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_DocumentSequence_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id)
    );
END
GO

IF OBJECT_ID('dbo.LedgerSequence', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LedgerSequence (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_LedgerSequence PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        CurrentNumber bigint NOT NULL CONSTRAINT DF_LedgerSequence_Current DEFAULT (0),
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_LedgerSequence_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_LedgerSequence_Company_Branch UNIQUE (CompanyId, BranchId),
        CONSTRAINT FK_LedgerSequence_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_LedgerSequence_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id)
    );
END
GO

IF OBJECT_ID('dbo.StockTake', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockTake (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockTake PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        StockTakeNo nvarchar(30) NOT NULL,
        CountDate datetime2 NOT NULL,
        WarehouseId bigint NOT NULL,
        Status int NOT NULL CONSTRAINT DF_StockTake_Status DEFAULT (0),
        GeneratedAdjustmentDocumentId bigint NULL,
        ApprovedBy nvarchar(50) NULL,
        ApprovedAtUtc datetime2 NULL,
        Remarks nvarchar(500) NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_StockTake_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_StockTake_Company_Branch_No UNIQUE (CompanyId, BranchId, StockTakeNo),
        CONSTRAINT FK_StockTake_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_StockTake_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id)
    );
END
GO

IF OBJECT_ID('dbo.StockTakeLine', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockTakeLine (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockTakeLine PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        StockTakeId bigint NOT NULL,
        LineNo int NOT NULL,
        ItemVariantId bigint NOT NULL,
        LocationId bigint NOT NULL,
        LotId bigint NULL,
        SystemQty decimal(19,6) NOT NULL,
        CountedQty decimal(19,6) NOT NULL,
        VarianceQty decimal(19,6) NOT NULL,
        ReasonCodeId bigint NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_StockTakeLine_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_StockTakeLine_StockTakeId_LineNo UNIQUE (StockTakeId, LineNo),
        CONSTRAINT FK_StockTakeLine_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_StockTakeLine_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id),
        CONSTRAINT FK_StockTakeLine_StockTake FOREIGN KEY (StockTakeId) REFERENCES dbo.StockTake(Id)
    );
END
GO

IF OBJECT_ID('dbo.InventoryPeriod', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventoryPeriod (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryPeriod PRIMARY KEY,
        CompanyId int NOT NULL,
        FiscalYear int NOT NULL,
        FiscalMonth int NOT NULL,
        StartDate datetime2 NOT NULL,
        EndDate datetime2 NOT NULL,
        IsClosed bit NOT NULL CONSTRAINT DF_InventoryPeriod_IsClosed DEFAULT (0),
        ClosedBy nvarchar(50) NULL,
        ClosedAtUtc datetime2 NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_InventoryPeriod_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_InventoryPeriod_Company_Year_Month UNIQUE (CompanyId, FiscalYear, FiscalMonth),
        CONSTRAINT FK_InventoryPeriod_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId)
    );
END
GO
