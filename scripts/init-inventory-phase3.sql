USE ERPLiteEx;
GO

IF OBJECT_ID('dbo.Lot', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Lot (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Lot PRIMARY KEY,
        CompanyId int NOT NULL,
        ItemVariantId bigint NOT NULL,
        LotNo nvarchar(50) NOT NULL,
        SupplierRef nvarchar(50) NULL,
        SourceDocType nvarchar(10) NULL,
        SourceDocNo nvarchar(30) NULL,
        SourceDocLineNo int NULL,
        ReceivedDate datetime2 NOT NULL,
        ReceivedQty decimal(19,6) NOT NULL,
        ReceivedUnitCost decimal(19,6) NOT NULL,
        CostCurrencyCode nvarchar(3) NOT NULL CONSTRAINT DF_Lot_Currency DEFAULT (N'MYR'),
        ManufactureDate datetime2 NULL,
        ExpiryDate datetime2 NULL,
        Status int NOT NULL CONSTRAINT DF_Lot_Status DEFAULT (1),
        Remarks nvarchar(500) NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_Lot_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_Lot_Company_Variant_LotNo UNIQUE (CompanyId, ItemVariantId, LotNo),
        CONSTRAINT FK_Lot_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_Lot_ItemVariant FOREIGN KEY (ItemVariantId) REFERENCES dbo.ItemVariant(Id)
    );
END
GO

IF OBJECT_ID('dbo.LotBalance', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.LotBalance (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_LotBalance PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        LotId bigint NOT NULL,
        WarehouseId bigint NOT NULL,
        LocationId bigint NOT NULL,
        QtyOnHand decimal(19,6) NOT NULL CONSTRAINT DF_LotBalance_Qty DEFAULT (0),
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_LotBalance_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_LotBalance_Grain UNIQUE (CompanyId, BranchId, LotId, WarehouseId, LocationId),
        CONSTRAINT FK_LotBalance_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_LotBalance_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id),
        CONSTRAINT FK_LotBalance_Lot FOREIGN KEY (LotId) REFERENCES dbo.Lot(Id)
    );
END
GO

IF OBJECT_ID('dbo.InventoryDocumentLineLotSplit', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventoryDocumentLineLotSplit (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InventoryDocumentLineLotSplit PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        DocumentLineId bigint NOT NULL,
        LotId bigint NULL,
        LotNo nvarchar(50) NULL,
        Qty decimal(19,6) NOT NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_InventoryDocumentLineLotSplit_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_InventoryDocumentLineLotSplit_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_InventoryDocumentLineLotSplit_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id),
        CONSTRAINT FK_InventoryDocumentLineLotSplit_Line FOREIGN KEY (DocumentLineId) REFERENCES dbo.InventoryDocumentLine(Id)
    );
    CREATE INDEX IX_InventoryDocumentLineLotSplit_Line ON dbo.InventoryDocumentLineLotSplit (DocumentLineId, LotId, LotNo);
END
GO

IF COL_LENGTH('dbo.StockLedger', 'LotId') IS NULL
BEGIN
    ALTER TABLE dbo.StockLedger ADD LotId bigint NULL;
END
GO
