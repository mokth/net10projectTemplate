-- Purchase Requisition schema (manual DBA script — do NOT run at app startup).
-- PK: (CompanyCode, BranchCode, PRNo).
GO

IF OBJECT_ID(N'dbo.POPR', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POPR (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        PRNo nvarchar(30) NOT NULL,
        CreateDt datetime2 NOT NULL,
        Requester nvarchar(50) NULL,
        PRStat nvarchar(20) NOT NULL,
        DeptCode nvarchar(20) NULL,
        CheckedBy nvarchar(20) NULL,
        AuthorisedBy nvarchar(20) NULL,
        ApprovedBy nvarchar(20) NULL,
        ApprovedDate datetime2 NULL,
        Remarks nvarchar(500) NULL,
        AuthorisedBy2nd nvarchar(20) NULL,
        PRType nvarchar(20) NULL,
        LocationCode nvarchar(10) NULL,
        PONo nvarchar(30) NULL,
        ApprReason nvarchar(200) NULL,
        ProjID nvarchar(20) NULL,
        ApprovedBy2 nvarchar(20) NULL,
        ApprovedDate2 nvarchar(50) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_POPR PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, PRNo)
    );
END
GO

IF OBJECT_ID(N'dbo.POPRDtl', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POPRDtl (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        PRNo nvarchar(30) NOT NULL,
        Line smallint NOT NULL,
        ETADt datetime2 NULL,
        OneTime_ItemYN bit NULL,
        ICd nvarchar(30) NULL,
        IDes nvarchar(200) NULL,
        Category nvarchar(20) NULL,
        Qty decimal(18,4) NOT NULL CONSTRAINT DF_POPRDtl_Qty DEFAULT (0),
        PackSz decimal(18,4) NOT NULL CONSTRAINT DF_POPRDtl_PackSz DEFAULT (0),
        StdUOM nvarchar(10) NULL,
        PurchaseQty decimal(18,4) NOT NULL CONSTRAINT DF_POPRDtl_PurchaseQty DEFAULT (0),
        PurchaseUOM nvarchar(10) NULL,
        Currency nvarchar(20) NULL,
        UnitPrice decimal(18,4) NOT NULL CONSTRAINT DF_POPRDtl_UnitPrice DEFAULT (0),
        OneTimeVendor bit NULL,
        VendorCd nvarchar(60) NULL,
        VendNm nvarchar(200) NULL,
        Purpose nvarchar(250) NULL,
        PRStat nvarchar(20) NULL,
        StdQty decimal(18,4) NOT NULL CONSTRAINT DF_POPRDtl_StdQty DEFAULT (0),
        WtQty decimal(18,4) NOT NULL CONSTRAINT DF_POPRDtl_WtQty DEFAULT (0),
        WtUOM nvarchar(10) NULL,
        PaymentTerm nvarchar(20) NULL,
        BuyingTerm nvarchar(20) NULL,
        RepairType nvarchar(20) NULL,
        Amount decimal(18,2) NOT NULL CONSTRAINT DF_POPRDtl_Amount DEFAULT (0),
        PONo nvarchar(30) NULL,
        TaxGroup nvarchar(20) NULL,
        TaxAmount decimal(18,2) NOT NULL CONSTRAINT DF_POPRDtl_TaxAmount DEFAULT (0),
        IsInclusive bit NOT NULL CONSTRAINT DF_POPRDtl_IsInclusive DEFAULT (0),
        ToWarehouse nvarchar(20) NULL,
        SONo nvarchar(30) NULL,
        SOLine int NULL,
        CONSTRAINT PK_POPRDtl PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, PRNo, Line),
        CONSTRAINT FK_POPRDtl_POPR FOREIGN KEY (CompanyCode, BranchCode, PRNo)
            REFERENCES dbo.POPR (CompanyCode, BranchCode, PRNo) ON DELETE CASCADE
    );
END
GO

IF OBJECT_ID(N'dbo.POPRAttachFile', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POPRAttachFile (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DocID nvarchar(50) NOT NULL,
        DocName nvarchar(200) NOT NULL,
        DocKey nvarchar(30) NULL,
        DocName2 nvarchar(200) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        CONSTRAINT PK_POPRAttachFile PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocID, DocName)
    );
END
GO

IF OBJECT_ID(N'dbo.POPR', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POPR_Company_Branch_Status_CreateDt' AND object_id = OBJECT_ID(N'dbo.POPR'))
    CREATE INDEX IX_POPR_Company_Branch_Status_CreateDt ON dbo.POPR (CompanyCode, BranchCode, PRStat, CreateDt);
GO

IF OBJECT_ID(N'dbo.POPRAttachFile', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POPRAttachFile_Company_Branch_DocKey' AND object_id = OBJECT_ID(N'dbo.POPRAttachFile'))
    CREATE INDEX IX_POPRAttachFile_Company_Branch_DocKey ON dbo.POPRAttachFile (CompanyCode, BranchCode, DocKey);
GO
