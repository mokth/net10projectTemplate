-- Sales Delivery Order v1 schema (manual DBA script — do NOT run at app startup).
-- Option A PK: (CompanyCode, BranchCode, DONo). Idempotent IF OBJECT_ID / COL_LENGTH.
GO

IF OBJECT_ID(N'dbo.SaDO', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDO (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DONo nvarchar(30) NOT NULL,
        DODate datetime2 NOT NULL,
        Status nvarchar(20) NOT NULL,
        CustCode nvarchar(60) NOT NULL,
        CustName nvarchar(200) NULL,
        ShipName nvarchar(100) NULL,
        ShipAddress1 nvarchar(100) NULL,
        ShipAddress2 nvarchar(100) NULL,
        ShipAddress3 nvarchar(100) NULL,
        ShipAddress4 nvarchar(100) NULL,
        ShipCity nvarchar(50) NULL,
        ShipState nvarchar(50) NULL,
        ShipPostalCode nvarchar(20) NULL,
        ShipCountry nvarchar(50) NULL,
        ShipTel nvarchar(50) NULL,
        ShipFax nvarchar(50) NULL,
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
        TaxGrCode nvarchar(20) NULL,
        ShipVia nvarchar(50) NULL,
        Currency nvarchar(20) NULL,
        CurrRate decimal(18,6) NOT NULL CONSTRAINT DF_SaDO_CurrRate DEFAULT (1),
        PayCode nvarchar(20) NULL,
        Remarks nvarchar(500) NULL,
        Departure nvarchar(100) NULL,
        Destination nvarchar(100) NULL,
        Vessel nvarchar(100) NULL,
        GrossAmnt decimal(18,2) NOT NULL CONSTRAINT DF_SaDO_GrossAmnt DEFAULT (0),
        Taxes decimal(18,2) NOT NULL CONSTRAINT DF_SaDO_Taxes DEFAULT (0),
        TotAmnt decimal(18,2) NOT NULL CONSTRAINT DF_SaDO_TotAmnt DEFAULT (0),
        Prefix nvarchar(20) NULL,
        ShipWarehouse nvarchar(20) NULL,
        LocationCode nvarchar(10) NULL,
        CustDiscount decimal(18,6) NOT NULL CONSTRAINT DF_SaDO_CustDiscount DEFAULT (0),
        ContactPerson nvarchar(100) NULL,
        ProjID nvarchar(20) NULL,
        SalesRep nvarchar(20) NULL,
        Ref1 nvarchar(50) NULL,
        Ref2 nvarchar(50) NULL,
        Ref3 nvarchar(50) NULL,
        Ref4 nvarchar(50) NULL,
        PrintCounter int NULL,
        ShipOutDate datetime2 NULL,
        DriverName nvarchar(100) NULL,
        DriverPlate nvarchar(50) NULL,
        RecordTime datetime2 NULL,
        ShipOutRemark nvarchar(500) NULL,
        IsPack bit NULL,
        PostedDate datetime2 NULL,
        PostedBy nvarchar(20) NULL,
        RollbackDate datetime2 NULL,
        RollbackBy nvarchar(20) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SaDO PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DONo)
    );
END
GO

IF OBJECT_ID(N'dbo.SaDODetail', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDODetail (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DONo nvarchar(30) NOT NULL,
        Line smallint NOT NULL,
        InvNo nvarchar(30) NULL,
        SONo nvarchar(30) NOT NULL CONSTRAINT DF_SaDODetail_SONo DEFAULT (N''),
        CustPO nvarchar(50) NULL,
        CustRel smallint NULL,
        SOLine smallint NULL,
        ICode nvarchar(30) NULL,
        IDesc nvarchar(200) NULL,
        CustICode nvarchar(30) NULL,
        Qty decimal(18,4) NOT NULL CONSTRAINT DF_SaDODetail_Qty DEFAULT (0),
        UnitPrice decimal(18,4) NOT NULL CONSTRAINT DF_SaDODetail_UnitPrice DEFAULT (0),
        SellingUOM nvarchar(10) NULL,
        StdUOM nvarchar(10) NULL,
        WtUOM nvarchar(10) NULL,
        StdQty decimal(18,4) NOT NULL CONSTRAINT DF_SaDODetail_StdQty DEFAULT (0),
        WtQty decimal(18,4) NOT NULL CONSTRAINT DF_SaDODetail_WtQty DEFAULT (0),
        StdPSize decimal(18,4) NOT NULL CONSTRAINT DF_SaDODetail_StdPSize DEFAULT (0),
        TaxAmt decimal(18,2) NOT NULL CONSTRAINT DF_SaDODetail_TaxAmt DEFAULT (0),
        OrderType nvarchar(20) NULL,
        Remarks nvarchar(250) NULL,
        Amount decimal(18,2) NOT NULL CONSTRAINT DF_SaDODetail_Amount DEFAULT (0),
        InvDesc nvarchar(200) NULL,
        ItemGLCode nvarchar(20) NULL,
        Discount decimal(18,6) NOT NULL CONSTRAINT DF_SaDODetail_Discount DEFAULT (0),
        NetAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaDODetail_NetAmount DEFAULT (0),
        ItemDiscount decimal(18,6) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscount DEFAULT (0),
        ItemDiscount2 decimal(18,6) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscount2 DEFAULT (0),
        ItemDiscount3 decimal(18,6) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscount3 DEFAULT (0),
        ItemDiscount4 decimal(18,6) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscount4 DEFAULT (0),
        ItemDiscount5 decimal(18,6) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscount5 DEFAULT (0),
        ItemDiscount6 decimal(18,6) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscount6 DEFAULT (0),
        ItemDiscAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscAmount DEFAULT (0),
        ItemDiscAmount1 decimal(18,2) NOT NULL CONSTRAINT DF_SaDODetail_ItemDiscAmount1 DEFAULT (0),
        IDiscountType nvarchar(20) NULL,
        Dept nvarchar(20) NULL,
        WorkOrderNo nvarchar(30) NULL,
        FrWarehouse nvarchar(20) NULL,
        TaxGroup nvarchar(20) NULL,
        IsInclusive bit NOT NULL CONSTRAINT DF_SaDODetail_IsInclusive DEFAULT (0),
        LocalAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaDODetail_LocalAmount DEFAULT (0),
        StockControl bit NOT NULL CONSTRAINT DF_SaDODetail_StockControl DEFAULT (1),
        Classification nvarchar(50) NULL,
        CONSTRAINT PK_SaDODetail PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DONo, Line),
        CONSTRAINT FK_SaDODetail_SaDO FOREIGN KEY (CompanyCode, BranchCode, DONo)
            REFERENCES dbo.SaDO (CompanyCode, BranchCode, DONo) ON DELETE CASCADE
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SaDODetail_Company_Branch_DoNo_Line' AND object_id = OBJECT_ID(N'dbo.SaDODetail'))
    CREATE UNIQUE INDEX UQ_SaDODetail_Company_Branch_DoNo_Line
    ON dbo.SaDODetail (CompanyCode, BranchCode, DONo, Line);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaDO_Company_Branch_Status_DoDate' AND object_id = OBJECT_ID(N'dbo.SaDO'))
    CREATE INDEX IX_SaDO_Company_Branch_Status_DoDate
    ON dbo.SaDO (CompanyCode, BranchCode, Status, DODate);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaDO_Company_Branch_CustCode' AND object_id = OBJECT_ID(N'dbo.SaDO'))
    CREATE INDEX IX_SaDO_Company_Branch_CustCode
    ON dbo.SaDO (CompanyCode, BranchCode, CustCode);
GO

IF OBJECT_ID(N'dbo.IvTrxBatch', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IvTrxBatch_Company_Branch_TrxType_RefNo' AND object_id = OBJECT_ID(N'dbo.IvTrxBatch'))
        CREATE INDEX IX_IvTrxBatch_Company_Branch_TrxType_RefNo
        ON dbo.IvTrxBatch (CompanyCode, BranchCode, TrxType, RefNo);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IvTrxBatch_SP_Ref' AND object_id = OBJECT_ID(N'dbo.IvTrxBatch'))
        AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IvTrxBatch_SP_RefNo' AND object_id = OBJECT_ID(N'dbo.IvTrxBatch'))
        CREATE UNIQUE INDEX UQ_IvTrxBatch_SP_Ref
        ON dbo.IvTrxBatch (CompanyCode, BranchCode, RefNo)
        WHERE TrxType = N'SP' AND RefNo IS NOT NULL;
END
GO
