USE ERPLiteEx;
GO

IF OBJECT_ID('dbo.StockSnapshot', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockSnapshot (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_StockSnapshot PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        PeriodId bigint NOT NULL,
        WarehouseId bigint NOT NULL,
        ItemVariantId bigint NOT NULL,
        ClosingQty decimal(19,6) NOT NULL,
        ClosingCost decimal(19,6) NOT NULL,
        ClosingValue decimal(19,4) NOT NULL,
        SnapshotDate datetime2 NOT NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_StockSnapshot_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_StockSnapshot_Grain UNIQUE (CompanyId, BranchId, PeriodId, WarehouseId, ItemVariantId),
        CONSTRAINT FK_StockSnapshot_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_StockSnapshot_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id),
        CONSTRAINT FK_StockSnapshot_Period FOREIGN KEY (PeriodId) REFERENCES dbo.InventoryPeriod(Id)
    );
END
GO
