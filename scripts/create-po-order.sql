-- Purchase Order schema (manual DBA script — do NOT run at app startup).
-- PK: (CompanyCode, BranchCode, PONo, PORelNo).
GO

IF OBJECT_ID(N'dbo.POOrder', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POOrder (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        PONo nvarchar(30) NOT NULL,
        PORelNo smallint NOT NULL,
        PODt datetime2 NULL,
        Buyer nvarchar(20) NULL,
        POType nvarchar(20) NULL,
        OneTime bit NULL,
        VendCode nvarchar(60) NULL,
        VendName nvarchar(200) NULL,
        VendAddress1 nvarchar(100) NULL,
        VendAddress2 nvarchar(100) NULL,
        VendAddress3 nvarchar(100) NULL,
        VendAddress4 nvarchar(100) NULL,
        VendCity nvarchar(50) NULL,
        VendState nvarchar(50) NULL,
        VendPostal nvarchar(20) NULL,
        VendCountryCode nvarchar(50) NULL,
        VendTel nvarchar(50) NULL,
        VendFax nvarchar(50) NULL,
        CurCode nvarchar(20) NULL,
        ShipCode nvarchar(20) NULL,
        TermCode nvarchar(20) NULL,
        ContactPerson nvarchar(100) NULL,
        Email nvarchar(100) NULL,
        Website nvarchar(100) NULL,
        ShipName nvarchar(100) NULL,
        ShipAddress1 nvarchar(100) NULL,
        ShipAddress2 nvarchar(100) NULL,
        ShipAddress3 nvarchar(100) NULL,
        ShipAddress4 nvarchar(100) NULL,
        ShipCity nvarchar(50) NULL,
        ShipState nvarchar(50) NULL,
        ShipPostal nvarchar(20) NULL,
        ShipCountryCode nvarchar(50) NULL,
        ShipTel nvarchar(50) NULL,
        ShipFax nvarchar(50) NULL,
        TaxGrpCode nvarchar(20) NULL,
        TaxPercentage decimal(18,6) NOT NULL CONSTRAINT DF_POOrder_TaxPercentage DEFAULT (0),
        TaxAmount decimal(18,2) NOT NULL CONSTRAINT DF_POOrder_TaxAmount DEFAULT (0),
        Discount decimal(18,6) NOT NULL CONSTRAINT DF_POOrder_Discount DEFAULT (0),
        SIRemark nvarchar(500) NULL,
        POStat nvarchar(20) NOT NULL,
        RegNo nvarchar(50) NULL,
        DeptCode nvarchar(20) NULL,
        OneTime_ItemYN bit NULL,
        VCode nvarchar(60) NULL,
        BuyingTerm nvarchar(20) NULL,
        DONo nvarchar(30) NULL,
        InvNo nvarchar(30) NULL,
        CostCode nvarchar(20) NULL,
        LocationCode nvarchar(10) NULL,
        POCosting bit NULL,
        ProjID nvarchar(20) NULL,
        Type nvarchar(20) NULL,
        prefix nvarchar(20) NULL,
        CheckBy nvarchar(20) NULL,
        CheckOn datetime2 NULL,
        ApprovedBy nvarchar(20) NULL,
        ApprovedOn datetime2 NULL,
        AuthorisedBy nvarchar(20) NULL,
        QuatationNo nvarchar(50) NULL,
        ExpiryDate datetime2 NULL,
        PrintCounter int NULL,
        HdrType nvarchar(20) NULL,
        Ref1 nvarchar(50) NULL,
        Ref2 nvarchar(50) NULL,
        Ref3 nvarchar(50) NULL,
        Ref4 nvarchar(50) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_POOrder PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, PONo, PORelNo)
    );
END
GO

IF OBJECT_ID(N'dbo.PODetail', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PODetail (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        PONo nvarchar(30) NOT NULL,
        PORelNo smallint NOT NULL,
        Line smallint NOT NULL,
        PRNo nvarchar(30) NULL,
        PRLineNo smallint NULL,
        OneTime bit NULL,
        ICode nvarchar(30) NULL,
        IDes nvarchar(200) NULL,
        POUnitPrice decimal(18,4) NOT NULL CONSTRAINT DF_PODetail_POUnitPrice DEFAULT (0),
        POQty decimal(18,4) NOT NULL CONSTRAINT DF_PODetail_POQty DEFAULT (0),
        POPurQty decimal(18,4) NOT NULL CONSTRAINT DF_PODetail_POPurQty DEFAULT (0),
        WtQty decimal(18,4) NOT NULL CONSTRAINT DF_PODetail_WtQty DEFAULT (0),
        Amount decimal(18,2) NOT NULL CONSTRAINT DF_PODetail_Amount DEFAULT (0),
        RecvQty decimal(18,4) NOT NULL CONSTRAINT DF_PODetail_RecvQty DEFAULT (0),
        ReturnQty decimal(18,4) NOT NULL CONSTRAINT DF_PODetail_ReturnQty DEFAULT (0),
        ReturnQtyCN decimal(18,4) NOT NULL CONSTRAINT DF_PODetail_ReturnQtyCN DEFAULT (0),
        BalanceQty decimal(18,4) NOT NULL CONSTRAINT DF_PODetail_BalanceQty DEFAULT (0),
        PackSz decimal(18,4) NOT NULL CONSTRAINT DF_PODetail_PackSz DEFAULT (0),
        StdUOM nvarchar(10) NULL,
        WtUOM nvarchar(10) NULL,
        PurchaseUOM nvarchar(10) NULL,
        ETADate datetime2 NULL,
        CurCode nvarchar(20) NULL,
        Remarks nvarchar(250) NULL,
        PODesc nvarchar(200) NULL,
        RecvDate datetime2 NULL,
        RepairType nvarchar(20) NULL,
        Discount decimal(18,6) NOT NULL CONSTRAINT DF_PODetail_Discount DEFAULT (0),
        ItemDiscount decimal(18,6) NOT NULL CONSTRAINT DF_PODetail_ItemDiscount DEFAULT (0),
        DiscountType nvarchar(20) NULL,
        NetAmount decimal(18,2) NOT NULL CONSTRAINT DF_PODetail_NetAmount DEFAULT (0),
        ItemDiscount1 decimal(18,6) NOT NULL CONSTRAINT DF_PODetail_ItemDiscount1 DEFAULT (0),
        DiscountType1 nvarchar(20) NULL,
        CJNo nvarchar(30) NULL,
        CJRelNo int NULL,
        ProjID nvarchar(20) NULL,
        VendorPartNo nvarchar(50) NULL,
        TaxGroup nvarchar(20) NULL,
        TaxAmount decimal(18,2) NOT NULL CONSTRAINT DF_PODetail_TaxAmount DEFAULT (0),
        IsInclusive bit NOT NULL CONSTRAINT DF_PODetail_IsInclusive DEFAULT (0),
        M2UnitPrice decimal(18,4) NOT NULL CONSTRAINT DF_PODetail_M2UnitPrice DEFAULT (0),
        Trim bit NULL,
        ToWarehouse nvarchar(20) NULL,
        Requester nvarchar(50) NULL,
        CONSTRAINT PK_PODetail PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, PONo, PORelNo, Line),
        CONSTRAINT FK_PODetail_POOrder FOREIGN KEY (CompanyCode, BranchCode, PONo, PORelNo)
            REFERENCES dbo.POOrder (CompanyCode, BranchCode, PONo, PORelNo) ON DELETE CASCADE
    );
END
GO

IF OBJECT_ID(N'dbo.POAttachFile', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POAttachFile (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DocID nvarchar(50) NOT NULL,
        DocName nvarchar(200) NOT NULL,
        DocKey nvarchar(30) NOT NULL,
        DocPath nvarchar(500) NULL,
        RevNo nvarchar(20) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        CONSTRAINT PK_POAttachFile PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocID, DocName)
    );
END
GO

IF OBJECT_ID(N'dbo.POOrder', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POOrder_Company_Branch_Status_PoDate' AND object_id = OBJECT_ID(N'dbo.POOrder'))
    CREATE INDEX IX_POOrder_Company_Branch_Status_PoDate ON dbo.POOrder (CompanyCode, BranchCode, POStat, PODt);
GO

IF OBJECT_ID(N'dbo.POOrder', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POOrder_Company_Branch_VendCode' AND object_id = OBJECT_ID(N'dbo.POOrder'))
    CREATE INDEX IX_POOrder_Company_Branch_VendCode ON dbo.POOrder (CompanyCode, BranchCode, VendCode);
GO

IF OBJECT_ID(N'dbo.POAttachFile', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POAttachFile_Company_Branch_DocKey' AND object_id = OBJECT_ID(N'dbo.POAttachFile'))
    CREATE INDEX IX_POAttachFile_Company_Branch_DocKey ON dbo.POAttachFile (CompanyCode, BranchCode, DocKey);
GO
