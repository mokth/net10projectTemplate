-- Purchase Invoice schema (manual DBA script — do NOT run at app startup).
-- PK: (CompanyCode, BranchCode, DocNo).
-- Run scripts/alter-poinvoicedetail-po-link.sql afterwards for the PO link columns.
GO

IF OBJECT_ID(N'dbo.POInvoice', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POInvoice (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DocNo nvarchar(30) NOT NULL,
        DocDate datetime2 NOT NULL,
        Status nvarchar(20) NOT NULL,
        InvNo nvarchar(30) NULL,
        DONo nvarchar(30) NULL,
        Type nvarchar(20) NOT NULL,
        VendorCode nvarchar(60) NOT NULL,
        VendorName nvarchar(200) NULL,
        InvAddress1 nvarchar(100) NULL,
        InvAddress2 nvarchar(100) NULL,
        InvAddress3 nvarchar(100) NULL,
        InvAddress4 nvarchar(100) NULL,
        City nvarchar(50) NULL,
        State nvarchar(50) NULL,
        PostalCode nvarchar(20) NULL,
        Country nvarchar(50) NULL,
        Tel nvarchar(50) NULL,
        Fax nvarchar(50) NULL,
        PayCode nvarchar(20) NULL,
        Currency nvarchar(20) NULL,
        TaxGrCode nvarchar(20) NULL,
        Remarks nvarchar(500) NULL,
        GrossAmnt decimal(18,2) NOT NULL CONSTRAINT DF_POInvoice_GrossAmnt DEFAULT (0),
        Taxes decimal(18,2) NOT NULL CONSTRAINT DF_POInvoice_Taxes DEFAULT (0),
        TotAmnt decimal(18,2) NOT NULL CONSTRAINT DF_POInvoice_TotAmnt DEFAULT (0),
        Prefix nvarchar(20) NULL,
        LocationCode nvarchar(10) NULL,
        ProjID nvarchar(20) NULL,
        SalesRep nvarchar(20) NULL,
        Dept nvarchar(20) NULL,
        ExportStatus bit NULL,
        CurrRate decimal(18,6) NOT NULL CONSTRAINT DF_POInvoice_CurrRate DEFAULT (1),
        RefNo nvarchar(50) NULL,
        ExternalDocNo nvarchar(50) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_POInvoice PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocNo)
    );
END
GO

IF OBJECT_ID(N'dbo.POInvoiceDetail', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POInvoiceDetail (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DocNo nvarchar(30) NOT NULL,
        Line smallint NOT NULL,
        ICode nvarchar(30) NOT NULL,
        IDesc nvarchar(200) NULL,
        CustICode nvarchar(30) NULL,
        Qty decimal(18,4) NOT NULL CONSTRAINT DF_POInvoiceDetail_Qty DEFAULT (0),
        UnitPrice decimal(18,4) NOT NULL CONSTRAINT DF_POInvoiceDetail_UnitPrice DEFAULT (0),
        SellingUOM nvarchar(10) NULL,
        StdUOM nvarchar(10) NULL,
        WtUOM nvarchar(10) NULL,
        StdQty decimal(18,4) NOT NULL CONSTRAINT DF_POInvoiceDetail_StdQty DEFAULT (0),
        WtQty decimal(18,4) NOT NULL CONSTRAINT DF_POInvoiceDetail_WtQty DEFAULT (0),
        StdCustPSize decimal(18,4) NOT NULL CONSTRAINT DF_POInvoiceDetail_StdCustPSize DEFAULT (0),
        TaxAmt decimal(18,2) NOT NULL CONSTRAINT DF_POInvoiceDetail_TaxAmt DEFAULT (0),
        Amount decimal(18,2) NOT NULL CONSTRAINT DF_POInvoiceDetail_Amount DEFAULT (0),
        CustPO nvarchar(50) NULL,
        Remarks nvarchar(250) NULL,
        ItemGLCode nvarchar(20) NULL,
        TaxGroup nvarchar(20) NULL,
        IsInclusive bit NOT NULL CONSTRAINT DF_POInvoiceDetail_IsInclusive DEFAULT (0),
        Discount decimal(18,6) NOT NULL CONSTRAINT DF_POInvoiceDetail_Discount DEFAULT (0),
        ItemDiscount decimal(18,6) NOT NULL CONSTRAINT DF_POInvoiceDetail_ItemDiscount DEFAULT (0),
        ItemDiscount1 decimal(18,6) NOT NULL CONSTRAINT DF_POInvoiceDetail_ItemDiscount1 DEFAULT (0),
        ItemDiscount2 decimal(18,6) NOT NULL CONSTRAINT DF_POInvoiceDetail_ItemDiscount2 DEFAULT (0),
        ItemDiscount3 decimal(18,6) NOT NULL CONSTRAINT DF_POInvoiceDetail_ItemDiscount3 DEFAULT (0),
        IDiscountType nvarchar(20) NULL,
        IDiscountType1 nvarchar(20) NULL,
        NetAmount decimal(18,2) NOT NULL CONSTRAINT DF_POInvoiceDetail_NetAmount DEFAULT (0),
        OneTime bit NULL,
        CONSTRAINT PK_POInvoiceDetail PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocNo, Line),
        CONSTRAINT FK_POInvoiceDetail_POInvoice FOREIGN KEY (CompanyCode, BranchCode, DocNo)
            REFERENCES dbo.POInvoice (CompanyCode, BranchCode, DocNo) ON DELETE CASCADE
    );
END
GO

IF OBJECT_ID(N'dbo.POInvoice', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POInvoice_Company_Branch_Status_DocDate' AND object_id = OBJECT_ID(N'dbo.POInvoice'))
    CREATE INDEX IX_POInvoice_Company_Branch_Status_DocDate ON dbo.POInvoice (CompanyCode, BranchCode, Status, DocDate);
GO

IF OBJECT_ID(N'dbo.POInvoice', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POInvoice_Company_Branch_Type_Status' AND object_id = OBJECT_ID(N'dbo.POInvoice'))
    CREATE INDEX IX_POInvoice_Company_Branch_Type_Status ON dbo.POInvoice (CompanyCode, BranchCode, Type, Status);
GO

IF OBJECT_ID(N'dbo.POInvoice', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POInvoice_Company_Branch_VendorCode' AND object_id = OBJECT_ID(N'dbo.POInvoice'))
    CREATE INDEX IX_POInvoice_Company_Branch_VendorCode ON dbo.POInvoice (CompanyCode, BranchCode, VendorCode);
GO

IF OBJECT_ID(N'dbo.POInvoice', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_POInvoice_Company_Branch_InvNo' AND object_id = OBJECT_ID(N'dbo.POInvoice'))
    CREATE INDEX IX_POInvoice_Company_Branch_InvNo ON dbo.POInvoice (CompanyCode, BranchCode, InvNo);
GO
