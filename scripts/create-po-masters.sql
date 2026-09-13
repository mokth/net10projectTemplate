-- Purchase master tables (manual DBA script — do NOT run at app startup).
-- PKs include CompanyCode for tenant isolation, matching Sales masters.
GO

IF OBJECT_ID(N'dbo.POVendor', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POVendor (
        CompanyCode nvarchar(10) NOT NULL,
        CustCode nvarchar(60) NOT NULL,
        CustName nvarchar(200) NOT NULL,
        CustShortName nvarchar(100) NULL,
        CustType nvarchar(20) NULL,
        Address1 nvarchar(100) NULL,
        Address2 nvarchar(100) NULL,
        Address3 nvarchar(100) NULL,
        City nvarchar(50) NULL,
        State nvarchar(50) NULL,
        PostalCode nvarchar(20) NULL,
        Country nvarchar(50) NULL,
        Tel nvarchar(50) NULL,
        Fax nvarchar(50) NULL,
        Telex nvarchar(50) NULL,
        Email nvarchar(100) NULL,
        Website nvarchar(100) NULL,
        GSTRegNo nvarchar(50) NULL,
        PayCode nvarchar(20) NULL,
        Currency nvarchar(20) NULL,
        ContactPerson nvarchar(100) NULL,
        Title nvarchar(50) NULL,
        Department nvarchar(50) NULL,
        ContactEmail nvarchar(100) NULL,
        ContactTelp nvarchar(50) NULL,
        ContactFax nvarchar(50) NULL,
        Taxable bit NULL,
        TaxGrCode nvarchar(20) NULL,
        AppShip bit NULL,
        AppInvoice bit NULL,
        Active bit NOT NULL CONSTRAINT DF_POVendor_Active DEFAULT (1),
        ShipAddress1 nvarchar(100) NULL,
        ShipAddress2 nvarchar(100) NULL,
        ShipAddress3 nvarchar(100) NULL,
        ShipCity nvarchar(50) NULL,
        ShipState nvarchar(50) NULL,
        ShipPostalCode nvarchar(20) NULL,
        ShipCountry nvarchar(50) NULL,
        ShipTel nvarchar(50) NULL,
        ShipFax nvarchar(50) NULL,
        ShipTelex nvarchar(50) NULL,
        ShipEmail nvarchar(100) NULL,
        ShipWebsite nvarchar(100) NULL,
        InvAddress1 nvarchar(100) NULL,
        InvAddress2 nvarchar(100) NULL,
        InvAddress3 nvarchar(100) NULL,
        InvCity nvarchar(50) NULL,
        InvState nvarchar(50) NULL,
        InvPostalCode nvarchar(20) NULL,
        InvCountry nvarchar(50) NULL,
        InvTel nvarchar(50) NULL,
        InvFax nvarchar(50) NULL,
        InvTelex nvarchar(50) NULL,
        InvEmail nvarchar(100) NULL,
        InvWebsite nvarchar(100) NULL,
        BuyingTerm nvarchar(20) NULL,
        BranchCode nvarchar(10) NULL,
        LocationCode nvarchar(10) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_POVendor PRIMARY KEY CLUSTERED (CompanyCode, CustCode)
    );
END
GO

IF OBJECT_ID(N'dbo.POVendor', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POVendor_Company_CustName' AND object_id = OBJECT_ID(N'dbo.POVendor'))
    CREATE INDEX IX_POVendor_Company_CustName ON dbo.POVendor (CompanyCode, CustName);
GO

IF OBJECT_ID(N'dbo.POVendor', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POVendor_Company_Active' AND object_id = OBJECT_ID(N'dbo.POVendor'))
    CREATE INDEX IX_POVendor_Company_Active ON dbo.POVendor (CompanyCode, Active);
GO

IF OBJECT_ID(N'dbo.POBuyer', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POBuyer (
        CompanyCode nvarchar(10) NOT NULL,
        BuyerCode nvarchar(20) NOT NULL,
        BuyerName nvarchar(100) NULL,
        BuyerDesc nvarchar(200) NULL,
        Active bit NOT NULL CONSTRAINT DF_POBuyer_Active DEFAULT (1),
        BranchCode nvarchar(10) NULL,
        LocationCode nvarchar(10) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_POBuyer PRIMARY KEY CLUSTERED (CompanyCode, BuyerCode)
    );
END
GO

IF OBJECT_ID(N'dbo.POBuyingTerm', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POBuyingTerm (
        CompanyCode nvarchar(10) NOT NULL,
        BuyingTerm nvarchar(20) NOT NULL,
        Description nvarchar(200) NULL,
        Active bit NOT NULL CONSTRAINT DF_POBuyingTerm_Active DEFAULT (1),
        BranchCode nvarchar(10) NULL,
        LocationCode nvarchar(10) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_POBuyingTerm PRIMARY KEY CLUSTERED (CompanyCode, BuyingTerm)
    );
END
GO

IF OBJECT_ID(N'dbo.POCategory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POCategory (
        CompanyCode nvarchar(10) NOT NULL,
        Category nvarchar(20) NOT NULL,
        Description nvarchar(200) NULL,
        Active bit NOT NULL CONSTRAINT DF_POCategory_Active DEFAULT (1),
        BranchCode nvarchar(10) NULL,
        LocationCode nvarchar(10) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_POCategory PRIMARY KEY CLUSTERED (CompanyCode, Category)
    );
END
GO

IF OBJECT_ID(N'dbo.POAuthorised', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POAuthorised (
        CompanyCode nvarchar(10) NOT NULL,
        Authorised nvarchar(20) NOT NULL,
        Name nvarchar(100) NULL,
        Email nvarchar(100) NULL,
        MobileNo nvarchar(50) NULL,
        Active bit NOT NULL CONSTRAINT DF_POAuthorised_Active DEFAULT (1),
        BranchCode nvarchar(10) NULL,
        LocationCode nvarchar(10) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_POAuthorised PRIMARY KEY CLUSTERED (CompanyCode, Authorised)
    );
END
GO

IF OBJECT_ID(N'dbo.PODesc', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PODesc (
        CompanyCode nvarchar(10) NOT NULL,
        ItemDesc nvarchar(200) NOT NULL,
        DeptCode nvarchar(20) NULL,
        UnitPrice decimal(18,4) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PODesc PRIMARY KEY CLUSTERED (CompanyCode, ItemDesc)
    );
END
GO

IF OBJECT_ID(N'dbo.POPurItem', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POPurItem (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NULL,
        LocationCode nvarchar(10) NULL,
        ICode nvarchar(30) NOT NULL,
        IDesc nvarchar(200) NULL,
        Category nvarchar(20) NULL,
        SubCategory nvarchar(20) NULL,
        Dept nvarchar(20) NULL,
        PurQty decimal(18,4) NOT NULL CONSTRAINT DF_POPurItem_PurQty DEFAULT (0),
        PurUOM nvarchar(10) NULL,
        Vendor nvarchar(60) NOT NULL,
        VendName nvarchar(200) NULL,
        VendorPartNo nvarchar(50) NULL,
        Currency nvarchar(20) NULL,
        UnitPrice decimal(18,4) NULL,
        MOQ decimal(18,4) NOT NULL CONSTRAINT DF_POPurItem_MOQ DEFAULT (0),
        LeadTime int NULL,
        Status nvarchar(20) NULL,
        Remarks nvarchar(250) NULL,
        PurchaseGLCode nvarchar(20) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_POPurItem PRIMARY KEY CLUSTERED (ID)
    );
END
GO

IF OBJECT_ID(N'dbo.POPurItem', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_POPurItem_Company_ICode_Vendor' AND object_id = OBJECT_ID(N'dbo.POPurItem'))
    CREATE UNIQUE INDEX UX_POPurItem_Company_ICode_Vendor ON dbo.POPurItem (CompanyCode, ICode, Vendor);
GO

IF OBJECT_ID(N'dbo.POVendorByItem', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POVendorByItem (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NULL,
        LocationCode nvarchar(10) NULL,
        Vendor nvarchar(60) NULL,
        VendorPartNo nvarchar(50) NULL,
        LeadTime int NULL,
        Country nvarchar(50) NULL,
        Currency nvarchar(20) NULL,
        UnitPrice decimal(18,4) NULL,
        PurUOM nvarchar(10) NULL,
        PackSize decimal(18,4) NOT NULL CONSTRAINT DF_POVendorByItem_PackSize DEFAULT (0),
        Tolerance decimal(18,4) NOT NULL CONSTRAINT DF_POVendorByItem_Tolerance DEFAULT (0),
        ICode nvarchar(30) NULL,
        IDesc nvarchar(200) NULL,
        Buyer nvarchar(20) NULL,
        OrdLevel decimal(18,4) NOT NULL CONSTRAINT DF_POVendorByItem_OrdLevel DEFAULT (0),
        SafetyStock decimal(18,4) NOT NULL CONSTRAINT DF_POVendorByItem_SafetyStock DEFAULT (0),
        Status nvarchar(20) NULL,
        PayCode nvarchar(20) NULL,
        MaterialType nvarchar(50) NULL,
        PODesc nvarchar(200) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_POVendorByItem PRIMARY KEY CLUSTERED (ID)
    );
END
GO

IF OBJECT_ID(N'dbo.POVendorByItem', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POVendorByItem_Company_ICode_Vendor' AND object_id = OBJECT_ID(N'dbo.POVendorByItem'))
    CREATE INDEX IX_POVendorByItem_Company_ICode_Vendor ON dbo.POVendorByItem (CompanyCode, ICode, Vendor);
GO

IF OBJECT_ID(N'dbo.POSupplier', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POSupplier (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        SuppCode nvarchar(60) NOT NULL,
        SuppName nvarchar(200) NOT NULL,
        SuppShortName nvarchar(100) NULL,
        SuppType nvarchar(20) NULL,
        Address1 nvarchar(100) NULL,
        Address2 nvarchar(100) NULL,
        Address3 nvarchar(100) NULL,
        Address4 nvarchar(100) NULL,
        City nvarchar(50) NULL,
        State nvarchar(50) NULL,
        PostalCode nvarchar(20) NULL,
        Country nvarchar(50) NULL,
        Tel nvarchar(50) NULL,
        Fax nvarchar(50) NULL,
        Telex nvarchar(50) NULL,
        Email nvarchar(100) NULL,
        Website nvarchar(100) NULL,
        GSTRegNo nvarchar(50) NULL,
        PayCode nvarchar(20) NULL,
        Currency nvarchar(20) NULL,
        ContactPerson nvarchar(100) NULL,
        Title nvarchar(50) NULL,
        Department nvarchar(50) NULL,
        ContactEmail nvarchar(100) NULL,
        ContactTelp nvarchar(50) NULL,
        ContactFax nvarchar(50) NULL,
        Taxable bit NULL,
        TaxGrCode nvarchar(20) NULL,
        Active bit NOT NULL CONSTRAINT DF_POSupplier_Active DEFAULT (1),
        Suspend bit NULL,
        CategoryCode nvarchar(20) NULL,
        ContactPerson2 nvarchar(100) NULL,
        Title2 nvarchar(50) NULL,
        Department2 nvarchar(50) NULL,
        ContactEmail2 nvarchar(100) NULL,
        ContactTelp2 nvarchar(50) NULL,
        ContactFax2 nvarchar(50) NULL,
        ContactPerson3 nvarchar(100) NULL,
        Title3 nvarchar(50) NULL,
        Department3 nvarchar(50) NULL,
        ContactEmail3 nvarchar(100) NULL,
        ContactTelp3 nvarchar(50) NULL,
        ContactFax3 nvarchar(50) NULL,
        ContactPerson4 nvarchar(100) NULL,
        Title4 nvarchar(50) NULL,
        Department4 nvarchar(50) NULL,
        ContactEmail4 nvarchar(100) NULL,
        ContactTelp4 nvarchar(50) NULL,
        ContactFax4 nvarchar(50) NULL,
        POPrefix nvarchar(20) NULL,
        TaxGroup nvarchar(20) NULL,
        BuyingTerm nvarchar(20) NULL,
        SupplierBRN nvarchar(50) NULL,
        BankName nvarchar(100) NULL,
        AccountNo nvarchar(50) NULL,
        Remark nvarchar(500) NULL,
        LMW bit NULL,
        CreditorGroup nvarchar(20) NULL,
        CreditorSubGroup nvarchar(20) NULL,
        CreditLimit decimal(18,2) NULL,
        AreaCode nvarchar(20) NULL,
        AgingType nvarchar(20) NULL,
        StatementType nvarchar(20) NULL,
        TINNo nvarchar(20) NULL,
        RegType nvarchar(20) NULL,
        StateCode nvarchar(20) NULL,
        CountryCode nvarchar(20) NULL,
        MISCCode nvarchar(20) NULL,
        BizDesc nvarchar(200) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_POSupplier PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, SuppCode)
    );
END
GO

IF OBJECT_ID(N'dbo.POSupplierAdd', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POSupplierAdd (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        SuppCode nvarchar(60) NOT NULL,
        Line int NOT NULL,
        SuppName nvarchar(200) NULL,
        Address1 nvarchar(100) NULL,
        Address2 nvarchar(100) NULL,
        Address3 nvarchar(100) NULL,
        Address4 nvarchar(100) NULL,
        City nvarchar(50) NULL,
        State nvarchar(50) NULL,
        PostalCode nvarchar(20) NULL,
        Country nvarchar(50) NULL,
        Tel nvarchar(50) NULL,
        Fax nvarchar(50) NULL,
        CONSTRAINT PK_POSupplierAdd PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, SuppCode, Line),
        CONSTRAINT FK_POSupplierAdd_POSupplier FOREIGN KEY (CompanyCode, BranchCode, SuppCode)
            REFERENCES dbo.POSupplier (CompanyCode, BranchCode, SuppCode) ON DELETE CASCADE
    );
END
GO

IF OBJECT_ID(N'dbo.POSupplier', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POSupplier_Company_Branch_SuppName' AND object_id = OBJECT_ID(N'dbo.POSupplier'))
    CREATE INDEX IX_POSupplier_Company_Branch_SuppName ON dbo.POSupplier (CompanyCode, BranchCode, SuppName);
GO

IF OBJECT_ID(N'dbo.POSupplier', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POSupplier_Company_Branch_Active' AND object_id = OBJECT_ID(N'dbo.POSupplier'))
    CREATE INDEX IX_POSupplier_Company_Branch_Active ON dbo.POSupplier (CompanyCode, BranchCode, Active);
GO
