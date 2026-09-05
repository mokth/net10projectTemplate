-- Sales Invoice v1 schema (manual DBA script — do NOT run at app startup).
-- Target database: ERPWeb (ConnectionStrings:DefaultConnection). Do not USE ERPLiteEx.
-- Idempotent: IF OBJECT_ID / COL_LENGTH / sys.indexes.
-- Option A PK: (CompanyCode, BranchCode, InvNo).
GO

IF OBJECT_ID(N'dbo.SaTaxGroup', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaTaxGroup (
        CompanyCode nvarchar(10) NOT NULL,
        TaxGrCode nvarchar(20) NOT NULL,
        TaxGrDesc nvarchar(100) NULL,
        Percentage decimal(18,6) NOT NULL CONSTRAINT DF_SaTaxGroup_Percentage DEFAULT (0),
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        BranchCode nvarchar(10) NULL,
        LocationCode nvarchar(20) NULL,
        CONSTRAINT PK_SaTaxGroup PRIMARY KEY (CompanyCode, TaxGrCode)
    );
END
GO

IF COL_LENGTH(N'dbo.SaTaxGroup', N'TaxGrDesc') IS NULL
    ALTER TABLE dbo.SaTaxGroup ADD TaxGrDesc nvarchar(100) NULL;
GO

IF COL_LENGTH(N'dbo.SaTaxGroup', N'Percentage') IS NULL
    ALTER TABLE dbo.SaTaxGroup ADD Percentage decimal(18,6) NOT NULL CONSTRAINT DF_SaTaxGroup_Percentage DEFAULT (0);
GO

IF COL_LENGTH(N'dbo.SaTaxGroup', N'CompanyCode') IS NULL
    ALTER TABLE dbo.SaTaxGroup ADD CompanyCode nvarchar(10) NOT NULL CONSTRAINT DF_SaTaxGroup_CompanyCode DEFAULT (N'');
GO

IF COL_LENGTH(N'dbo.SaTaxGroup', N'BranchCode') IS NULL
    ALTER TABLE dbo.SaTaxGroup ADD BranchCode nvarchar(10) NULL;
GO

IF COL_LENGTH(N'dbo.SaTaxGroup', N'LocationCode') IS NULL
    ALTER TABLE dbo.SaTaxGroup ADD LocationCode nvarchar(20) NULL;
GO

IF EXISTS (
    SELECT 1
    FROM sys.key_constraints kc
    INNER JOIN sys.index_columns ic
        ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
    WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SaTaxGroup')
      AND kc.[type] = N'PK'
    GROUP BY kc.[name]
    HAVING COUNT(*) = 1
)
BEGIN
    ALTER TABLE dbo.SaTaxGroup DROP CONSTRAINT PK_SaTaxGroup;
    ALTER TABLE dbo.SaTaxGroup ADD CONSTRAINT PK_SaTaxGroup PRIMARY KEY (CompanyCode, TaxGrCode);
END
GO

IF OBJECT_ID(N'dbo.SaInvoice', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaInvoice (
        CompanyCode nvarchar(10) NOT NULL,
        InvNo nvarchar(30) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        LocationCode nvarchar(10) NULL,
        CustCode nvarchar(60) NOT NULL,
        InvDate datetime2 NOT NULL,
        Status nvarchar(20) NOT NULL,
        DONo nvarchar(30) NOT NULL,
        Currency nvarchar(20) NULL,
        CurrRate decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoice_CurrRate DEFAULT (1),
        GrossAmnt decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoice_GrossAmnt DEFAULT (0),
        Taxes decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoice_Taxes DEFAULT (0),
        TotAmnt decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoice_TotAmnt DEFAULT (0),
        InvPrefix nvarchar(20) NULL,
        PayCode nvarchar(20) NULL,
        TaxGrCode nvarchar(20) NULL,
        SalesmanCode nvarchar(20) NULL,
        PoNo nvarchar(50) NULL,
        Remark nvarchar(500) NULL,
        CustName nvarchar(200) NULL,
        InvName nvarchar(100) NULL,
        InvAddress1 nvarchar(100) NULL,
        InvAddress2 nvarchar(100) NULL,
        InvAddress3 nvarchar(100) NULL,
        InvAddress4 nvarchar(100) NULL,
        InvCity nvarchar(50) NULL,
        InvState nvarchar(50) NULL,
        InvPostalCode nvarchar(20) NULL,
        InvCountry nvarchar(50) NULL,
        InvTel nvarchar(50) NULL,
        InvFax nvarchar(50) NULL,
        ShipName nvarchar(100) NULL,
        ShipAddress1 nvarchar(100) NULL,
        ShipAddress2 nvarchar(100) NULL,
        ShipAddress3 nvarchar(100) NULL,
        ShipCity nvarchar(50) NULL,
        ShipState nvarchar(50) NULL,
        ShipPostalCode nvarchar(20) NULL,
        ShipCountry nvarchar(50) NULL,
        ShipTel nvarchar(50) NULL,
        ShipFax nvarchar(50) NULL,
        PostedDate datetime NULL,
        PostedBy nvarchar(20) NULL,
        RollbackDate datetime NULL,
        RollbackBy nvarchar(20) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SaInvoice PRIMARY KEY (CompanyCode, BranchCode, InvNo)
    );
END
GO

IF COL_LENGTH(N'dbo.SaInvoice', N'BranchCode') IS NULL ALTER TABLE dbo.SaInvoice ADD BranchCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'LocationCode') IS NULL ALTER TABLE dbo.SaInvoice ADD LocationCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'PostedDate') IS NULL ALTER TABLE dbo.SaInvoice ADD PostedDate datetime NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'PostedBy') IS NULL ALTER TABLE dbo.SaInvoice ADD PostedBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'RollbackDate') IS NULL ALTER TABLE dbo.SaInvoice ADD RollbackDate datetime NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'RollbackBy') IS NULL ALTER TABLE dbo.SaInvoice ADD RollbackBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'RowVersion') IS NULL ALTER TABLE dbo.SaInvoice ADD RowVersion rowversion NOT NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvPrefix') IS NULL ALTER TABLE dbo.SaInvoice ADD InvPrefix nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'PayCode') IS NULL ALTER TABLE dbo.SaInvoice ADD PayCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'TaxGrCode') IS NULL ALTER TABLE dbo.SaInvoice ADD TaxGrCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'SalesmanCode') IS NULL ALTER TABLE dbo.SaInvoice ADD SalesmanCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'PoNo') IS NULL ALTER TABLE dbo.SaInvoice ADD PoNo nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'Remark') IS NULL ALTER TABLE dbo.SaInvoice ADD Remark nvarchar(500) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'CustName') IS NULL ALTER TABLE dbo.SaInvoice ADD CustName nvarchar(200) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvName') IS NULL ALTER TABLE dbo.SaInvoice ADD InvName nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvAddress1') IS NULL ALTER TABLE dbo.SaInvoice ADD InvAddress1 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvAddress2') IS NULL ALTER TABLE dbo.SaInvoice ADD InvAddress2 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvAddress3') IS NULL ALTER TABLE dbo.SaInvoice ADD InvAddress3 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvAddress4') IS NULL ALTER TABLE dbo.SaInvoice ADD InvAddress4 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvCity') IS NULL ALTER TABLE dbo.SaInvoice ADD InvCity nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvState') IS NULL ALTER TABLE dbo.SaInvoice ADD InvState nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvPostalCode') IS NULL ALTER TABLE dbo.SaInvoice ADD InvPostalCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvCountry') IS NULL ALTER TABLE dbo.SaInvoice ADD InvCountry nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvTel') IS NULL ALTER TABLE dbo.SaInvoice ADD InvTel nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvFax') IS NULL ALTER TABLE dbo.SaInvoice ADD InvFax nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipName') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipName nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipAddress1') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipAddress1 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipAddress2') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipAddress2 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipAddress3') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipAddress3 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipCity') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipCity nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipState') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipState nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipPostalCode') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipPostalCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipCountry') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipCountry nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipTel') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipTel nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipFax') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipFax nvarchar(50) NULL;
GO

IF OBJECT_ID(N'dbo.SaInvoiceDetail', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaInvoiceDetail (
        ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SaInvoiceDetail PRIMARY KEY,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        InvNo nvarchar(30) NOT NULL,
        Line int NOT NULL,
        ICode nvarchar(30) NULL,
        IDesc nvarchar(200) NULL,
        Qty decimal(18,4) NOT NULL CONSTRAINT DF_SaInvoiceDetail_Qty DEFAULT (0),
        StdQty decimal(18,4) NOT NULL CONSTRAINT DF_SaInvoiceDetail_StdQty DEFAULT (0),
        StdUom nvarchar(10) NULL,
        FrWarehouse nvarchar(20) NULL,
        UnitPrice decimal(18,4) NOT NULL CONSTRAINT DF_SaInvoiceDetail_UnitPrice DEFAULT (0),
        Amount decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoiceDetail_Amount DEFAULT (0),
        ItemDiscount decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscount DEFAULT (0),
        ItemDiscount2 decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscount2 DEFAULT (0),
        ItemDiscount3 decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscount3 DEFAULT (0),
        ItemDiscount4 decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscount4 DEFAULT (0),
        ItemDiscount5 decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscount5 DEFAULT (0),
        ItemDiscount6 decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscount6 DEFAULT (0),
        ItemDiscAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscAmount DEFAULT (0),
        ItemDiscAmount1 decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscAmount1 DEFAULT (0),
        IsInclusive bit NOT NULL CONSTRAINT DF_SaInvoiceDetail_IsInclusive DEFAULT (0),
        TaxGrCode nvarchar(20) NULL,
        TaxAmt decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoiceDetail_TaxAmt DEFAULT (0),
        NetAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoiceDetail_NetAmount DEFAULT (0),
        LocalAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoiceDetail_LocalAmount DEFAULT (0),
        OrderType nvarchar(20) NULL,
        StockControl bit NOT NULL CONSTRAINT DF_SaInvoiceDetail_StockControl DEFAULT (1),
        SellingGlCode nvarchar(20) NULL,
        Remarks nvarchar(250) NULL,
        CONSTRAINT FK_SaInvoiceDetail_Header FOREIGN KEY (CompanyCode, BranchCode, InvNo)
            REFERENCES dbo.SaInvoice (CompanyCode, BranchCode, InvNo)
    );
END
GO

IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'BranchCode') IS NULL
    ALTER TABLE dbo.SaInvoiceDetail ADD BranchCode nvarchar(10) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SaInvoiceDetail_Company_Branch_InvNo_Line' AND object_id = OBJECT_ID(N'dbo.SaInvoiceDetail'))
   AND COL_LENGTH(N'dbo.SaInvoiceDetail', N'BranchCode') IS NOT NULL
    CREATE UNIQUE INDEX UQ_SaInvoiceDetail_Company_Branch_InvNo_Line
    ON dbo.SaInvoiceDetail (CompanyCode, BranchCode, InvNo, Line);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaInvoice_Company_Status' AND object_id = OBJECT_ID(N'dbo.SaInvoice'))
    CREATE INDEX IX_SaInvoice_Company_Status
    ON dbo.SaInvoice (CompanyCode, Status);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaInvoice_Company_CustCode' AND object_id = OBJECT_ID(N'dbo.SaInvoice'))
    CREATE INDEX IX_SaInvoice_Company_CustCode
    ON dbo.SaInvoice (CompanyCode, CustCode);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IvTrxBatch_SP_RefNo' AND object_id = OBJECT_ID(N'dbo.IvTrxBatch'))
    CREATE UNIQUE INDEX UQ_IvTrxBatch_SP_RefNo
    ON dbo.IvTrxBatch (CompanyCode, BranchCode, RefNo)
    WHERE TrxType = N'SP' AND RefNo IS NOT NULL;
GO
