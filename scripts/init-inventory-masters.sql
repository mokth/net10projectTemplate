USE ERPLiteEx;
GO

IF OBJECT_ID('dbo.UOM', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.UOM (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_UOM PRIMARY KEY,
        CompanyId int NOT NULL,
        UOMCode nvarchar(10) NOT NULL,
        UOMName nvarchar(50) NOT NULL,
        DecimalPlaces int NOT NULL CONSTRAINT DF_UOM_DecimalPlaces DEFAULT (4),
        IsActive bit NOT NULL CONSTRAINT DF_UOM_IsActive DEFAULT (1),
        IsDeleted bit NOT NULL CONSTRAINT DF_UOM_IsDeleted DEFAULT (0),
        DeletedAtUtc datetime2 NULL,
        DeletedBy nvarchar(50) NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_UOM_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_UOM_CompanyId_UOMCode UNIQUE (CompanyId, UOMCode),
        CONSTRAINT FK_UOM_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId)
    );
END
GO

IF OBJECT_ID('dbo.Item', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Item (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Item PRIMARY KEY,
        CompanyId int NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        ItemDescription nvarchar(200) NOT NULL,
        BaseUOMId bigint NOT NULL,
        IsStockItem bit NOT NULL CONSTRAINT DF_Item_IsStockItem DEFAULT (1),
        IsBatchItem bit NOT NULL CONSTRAINT DF_Item_IsBatchItem DEFAULT (0),
        CostingMethod int NOT NULL CONSTRAINT DF_Item_CostingMethod DEFAULT (1),
        MinStockQty decimal(19,6) NOT NULL CONSTRAINT DF_Item_MinStockQty DEFAULT (0),
        MaxStockQty decimal(19,6) NOT NULL CONSTRAINT DF_Item_MaxStockQty DEFAULT (0),
        ReorderQty decimal(19,6) NOT NULL CONSTRAINT DF_Item_ReorderQty DEFAULT (0),
        TaxCode nvarchar(20) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_Item_IsActive DEFAULT (1),
        IsDeleted bit NOT NULL CONSTRAINT DF_Item_IsDeleted DEFAULT (0),
        DeletedAtUtc datetime2 NULL,
        DeletedBy nvarchar(50) NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_Item_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_Item_CompanyId_ItemCode UNIQUE (CompanyId, ItemCode),
        CONSTRAINT FK_Item_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_Item_BaseUOM FOREIGN KEY (BaseUOMId) REFERENCES dbo.UOM(Id)
    );
END
GO

IF OBJECT_ID('dbo.ItemVariant', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ItemVariant (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ItemVariant PRIMARY KEY,
        CompanyId int NOT NULL,
        ItemId bigint NOT NULL,
        SKU nvarchar(40) NOT NULL,
        Barcode nvarchar(50) NULL,
        VariantDescription nvarchar(200) NULL,
        IsDefault bit NOT NULL CONSTRAINT DF_ItemVariant_IsDefault DEFAULT (0),
        IsActive bit NOT NULL CONSTRAINT DF_ItemVariant_IsActive DEFAULT (1),
        IsDeleted bit NOT NULL CONSTRAINT DF_ItemVariant_IsDeleted DEFAULT (0),
        DeletedAtUtc datetime2 NULL,
        DeletedBy nvarchar(50) NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_ItemVariant_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_ItemVariant_CompanyId_SKU UNIQUE (CompanyId, SKU),
        CONSTRAINT FK_ItemVariant_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_ItemVariant_Item FOREIGN KEY (ItemId) REFERENCES dbo.Item(Id)
    );
END
GO

IF OBJECT_ID('dbo.UOMConversion', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.UOMConversion (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_UOMConversion PRIMARY KEY,
        CompanyId int NOT NULL,
        ItemId bigint NOT NULL,
        FromUOMId bigint NOT NULL,
        ToUOMId bigint NOT NULL,
        ConversionRate decimal(19,10) NOT NULL,
        IsDeleted bit NOT NULL CONSTRAINT DF_UOMConversion_IsDeleted DEFAULT (0),
        DeletedAtUtc datetime2 NULL,
        DeletedBy nvarchar(50) NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_UOMConversion_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_UOMConversion_Item_From_To UNIQUE (CompanyId, ItemId, FromUOMId, ToUOMId),
        CONSTRAINT FK_UOMConversion_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_UOMConversion_Item FOREIGN KEY (ItemId) REFERENCES dbo.Item(Id),
        CONSTRAINT FK_UOMConversion_FromUOM FOREIGN KEY (FromUOMId) REFERENCES dbo.UOM(Id),
        CONSTRAINT FK_UOMConversion_ToUOM FOREIGN KEY (ToUOMId) REFERENCES dbo.UOM(Id)
    );
END
GO

IF OBJECT_ID('dbo.Warehouse', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Warehouse (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Warehouse PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        WarehouseCode nvarchar(20) NOT NULL,
        WarehouseName nvarchar(100) NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_Warehouse_IsActive DEFAULT (1),
        IsDeleted bit NOT NULL CONSTRAINT DF_Warehouse_IsDeleted DEFAULT (0),
        DeletedAtUtc datetime2 NULL,
        DeletedBy nvarchar(50) NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_Warehouse_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_Warehouse_Company_Branch_Code UNIQUE (CompanyId, BranchId, WarehouseCode),
        CONSTRAINT FK_Warehouse_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_Warehouse_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id)
    );
END
GO

IF OBJECT_ID('dbo.WarehouseLocation', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WarehouseLocation (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_WarehouseLocation PRIMARY KEY,
        CompanyId int NOT NULL,
        BranchId bigint NOT NULL,
        WarehouseId bigint NOT NULL,
        LocationCode nvarchar(20) NOT NULL,
        LocationName nvarchar(100) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_WarehouseLocation_IsActive DEFAULT (1),
        IsDeleted bit NOT NULL CONSTRAINT DF_WarehouseLocation_IsDeleted DEFAULT (0),
        DeletedAtUtc datetime2 NULL,
        DeletedBy nvarchar(50) NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_WarehouseLocation_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_WarehouseLocation_WH_Code UNIQUE (CompanyId, BranchId, WarehouseId, LocationCode),
        CONSTRAINT FK_WarehouseLocation_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId),
        CONSTRAINT FK_WarehouseLocation_Branch FOREIGN KEY (BranchId) REFERENCES dbo.Branch(Id),
        CONSTRAINT FK_WarehouseLocation_Warehouse FOREIGN KEY (WarehouseId) REFERENCES dbo.Warehouse(Id)
    );
END
GO

IF OBJECT_ID('dbo.ReasonCode', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReasonCode (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ReasonCode PRIMARY KEY,
        CompanyId int NOT NULL,
        ReasonCodeValue nvarchar(20) NOT NULL,
        ReasonName nvarchar(100) NOT NULL,
        AppliesTo nvarchar(50) NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_ReasonCode_IsActive DEFAULT (1),
        IsDeleted bit NOT NULL CONSTRAINT DF_ReasonCode_IsDeleted DEFAULT (0),
        DeletedAtUtc datetime2 NULL,
        DeletedBy nvarchar(50) NULL,
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_ReasonCode_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy nvarchar(50) NULL,
        ModifiedAtUtc datetime2 NULL,
        ModifiedBy nvarchar(50) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT UQ_ReasonCode_Company_Code UNIQUE (CompanyId, ReasonCodeValue),
        CONSTRAINT FK_ReasonCode_Company FOREIGN KEY (CompanyId) REFERENCES dbo.Company(CompanyId)
    );
END
GO
