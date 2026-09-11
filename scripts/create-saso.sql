-- Sales Order schema with revision identity (manual DBA script — do NOT run at app startup).
-- PK: (CompanyCode, BranchCode, SONo, CustRel). Current revision: UX_SaSO_Current WHERE IsCurrent = 1.
GO

IF OBJECT_ID(N'dbo.SaSO', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaSO (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        SONo nvarchar(30) NOT NULL,
        CustRel smallint NOT NULL CONSTRAINT DF_SaSO_CustRel DEFAULT (1),
        IsCurrent bit NOT NULL CONSTRAINT DF_SaSO_IsCurrent DEFAULT (1),
        LastCustRel smallint NOT NULL CONSTRAINT DF_SaSO_LastCustRel DEFAULT (1),
        RevisionReason nvarchar(200) NULL,
        SODate datetime2 NOT NULL,
        Status nvarchar(20) NOT NULL,
        FulfillmentStatus nvarchar(10) NOT NULL CONSTRAINT DF_SaSO_FulfillmentStatus DEFAULT (N'NONE'),
        BillingStatus nvarchar(10) NOT NULL CONSTRAINT DF_SaSO_BillingStatus DEFAULT (N'NONE'),
        ClosedReason nvarchar(20) NULL,
        ClosedDate datetime2 NULL,
        ClosedBy nvarchar(20) NULL,
        CustCode nvarchar(60) NOT NULL,
        CustName nvarchar(200) NULL,
        CustPO nvarchar(50) NULL,
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
        Currency nvarchar(20) NULL,
        CurrRate decimal(18,6) NOT NULL CONSTRAINT DF_SaSO_CurrRate DEFAULT (1),
        PayCode nvarchar(20) NULL,
        Remarks nvarchar(500) NULL,
        GrossAmnt decimal(18,2) NOT NULL CONSTRAINT DF_SaSO_GrossAmnt DEFAULT (0),
        Taxes decimal(18,2) NOT NULL CONSTRAINT DF_SaSO_Taxes DEFAULT (0),
        TotAmnt decimal(18,2) NOT NULL CONSTRAINT DF_SaSO_TotAmnt DEFAULT (0),
        Prefix nvarchar(20) NULL,
        LocationCode nvarchar(10) NULL,
        CustDiscount decimal(18,6) NOT NULL CONSTRAINT DF_SaSO_CustDiscount DEFAULT (0),
        ContactPerson nvarchar(100) NULL,
        ProjID nvarchar(20) NULL,
        SalesRep nvarchar(20) NULL,
        Ref1 nvarchar(50) NULL,
        Ref2 nvarchar(50) NULL,
        Ref3 nvarchar(50) NULL,
        Ref4 nvarchar(50) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SaSO PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, SONo, CustRel),
        CONSTRAINT CK_SaSO_Status_ClosedReason CHECK (
            (Status IN (N'NEW', N'SHIPPED', N'SUPERSEDED') AND ClosedReason IS NULL)
            OR
            (Status = N'CLOSED' AND ClosedReason IN (N'FULLY_CONSUMED', N'FORCE_CLOSED'))
        ),
        CONSTRAINT CK_SaSO_IsCurrent_Status CHECK (
            (IsCurrent = 1 AND Status IN (N'NEW', N'SHIPPED', N'CLOSED'))
            OR
            (IsCurrent = 0 AND Status = N'SUPERSEDED')
        )
    );
END
GO

IF OBJECT_ID(N'dbo.SaSODetail', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaSODetail (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        SONo nvarchar(30) NOT NULL,
        CustRel smallint NOT NULL CONSTRAINT DF_SaSODetail_CustRel DEFAULT (1),
        Line smallint NOT NULL,
        ICode nvarchar(30) NULL,
        IDesc nvarchar(200) NULL,
        CustICode nvarchar(30) NULL,
        OrderQty decimal(18,4) NOT NULL,
        ShippedQty decimal(18,4) NOT NULL CONSTRAINT DF_SaSODetail_ShippedQty DEFAULT (0),
        BalanceQty decimal(18,4) NOT NULL,
        DeliveredQty decimal(18,4) NOT NULL CONSTRAINT DF_SaSODetail_DeliveredQty DEFAULT (0),
        InvoicedQty decimal(18,4) NOT NULL CONSTRAINT DF_SaSODetail_InvoicedQty DEFAULT (0),
        UnitPrice decimal(18,4) NOT NULL CONSTRAINT DF_SaSODetail_UnitPrice DEFAULT (0),
        SellingUOM nvarchar(10) NULL,
        StdUOM nvarchar(10) NULL,
        WtUOM nvarchar(10) NULL,
        StdQty decimal(18,4) NOT NULL CONSTRAINT DF_SaSODetail_StdQty DEFAULT (0),
        WtQty decimal(18,4) NOT NULL CONSTRAINT DF_SaSODetail_WtQty DEFAULT (0),
        StdPSize decimal(18,4) NOT NULL CONSTRAINT DF_SaSODetail_StdPSize DEFAULT (0),
        TaxAmt decimal(18,2) NOT NULL CONSTRAINT DF_SaSODetail_TaxAmt DEFAULT (0),
        OrderType nvarchar(20) NULL,
        Remarks nvarchar(250) NULL,
        Amount decimal(18,2) NOT NULL CONSTRAINT DF_SaSODetail_Amount DEFAULT (0),
        ItemGLCode nvarchar(20) NULL,
        Discount decimal(18,6) NOT NULL CONSTRAINT DF_SaSODetail_Discount DEFAULT (0),
        NetAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaSODetail_NetAmount DEFAULT (0),
        ItemDiscount decimal(18,6) NOT NULL CONSTRAINT DF_SaSODetail_ItemDiscount DEFAULT (0),
        ItemDiscount2 decimal(18,6) NOT NULL CONSTRAINT DF_SaSODetail_ItemDiscount2 DEFAULT (0),
        ItemDiscount3 decimal(18,6) NOT NULL CONSTRAINT DF_SaSODetail_ItemDiscount3 DEFAULT (0),
        ItemDiscount4 decimal(18,6) NOT NULL CONSTRAINT DF_SaSODetail_ItemDiscount4 DEFAULT (0),
        ItemDiscount5 decimal(18,6) NOT NULL CONSTRAINT DF_SaSODetail_ItemDiscount5 DEFAULT (0),
        ItemDiscount6 decimal(18,6) NOT NULL CONSTRAINT DF_SaSODetail_ItemDiscount6 DEFAULT (0),
        ItemDiscAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaSODetail_ItemDiscAmount DEFAULT (0),
        ItemDiscAmount1 decimal(18,2) NOT NULL CONSTRAINT DF_SaSODetail_ItemDiscAmount1 DEFAULT (0),
        IDiscountType nvarchar(20) NULL,
        Warehouse nvarchar(20) NULL,
        TaxGroup nvarchar(20) NULL,
        IsInclusive bit NOT NULL CONSTRAINT DF_SaSODetail_IsInclusive DEFAULT (0),
        LocalAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaSODetail_LocalAmount DEFAULT (0),
        StockControl bit NOT NULL CONSTRAINT DF_SaSODetail_StockControl DEFAULT (1),
        Classification nvarchar(50) NULL,
        DeliveryDate datetime2 NULL,
        ETA datetime2 NULL,
        ETD datetime2 NULL,
        CONSTRAINT PK_SaSODetail PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, SONo, CustRel, Line),
        CONSTRAINT FK_SaSODetail_SaSO FOREIGN KEY (CompanyCode, BranchCode, SONo, CustRel)
            REFERENCES dbo.SaSO (CompanyCode, BranchCode, SONo, CustRel) ON DELETE CASCADE,
        CONSTRAINT CK_SaSODetail_OrderQty CHECK (OrderQty > 0),
        CONSTRAINT CK_SaSODetail_ShippedQty CHECK (ShippedQty >= 0 AND ShippedQty <= OrderQty),
        CONSTRAINT CK_SaSODetail_BalanceQty CHECK (BalanceQty = OrderQty - ShippedQty)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SaSO_Current' AND object_id = OBJECT_ID(N'dbo.SaSO'))
    CREATE UNIQUE INDEX UX_SaSO_Current
    ON dbo.SaSO (CompanyCode, BranchCode, SONo)
    WHERE IsCurrent = 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaSO_Company_Branch_Status_SoDate' AND object_id = OBJECT_ID(N'dbo.SaSO'))
    CREATE INDEX IX_SaSO_Company_Branch_Status_SoDate
    ON dbo.SaSO (CompanyCode, BranchCode, Status, SODate);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaSO_Company_Branch_CustCode' AND object_id = OBJECT_ID(N'dbo.SaSO'))
    CREATE INDEX IX_SaSO_Company_Branch_CustCode
    ON dbo.SaSO (CompanyCode, BranchCode, CustCode);
GO

-- DO detail: SoConsumedQty + SO ref index
IF OBJECT_ID(N'dbo.SaDODetail', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.SaDODetail', N'SoConsumedQty') IS NULL
        ALTER TABLE dbo.SaDODetail ADD SoConsumedQty decimal(18,4) NOT NULL
            CONSTRAINT DF_SaDODetail_SoConsumedQty DEFAULT (0);

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_SaDODetail_SoConsumedQty' AND parent_object_id = OBJECT_ID(N'dbo.SaDODetail'))
        ALTER TABLE dbo.SaDODetail ADD CONSTRAINT CK_SaDODetail_SoConsumedQty CHECK (SoConsumedQty >= 0);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaDODetail_Company_Branch_SoNo_SoLine' AND object_id = OBJECT_ID(N'dbo.SaDODetail'))
        CREATE INDEX IX_SaDODetail_Company_Branch_SoNo_SoLine
        ON dbo.SaDODetail (CompanyCode, BranchCode, SONo, SOLine);
END
GO

-- Invoice detail: SO link columns + SoConsumedQty + LinkDo
IF OBJECT_ID(N'dbo.SaInvoiceDetail', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'SONo') IS NULL
        ALTER TABLE dbo.SaInvoiceDetail ADD SONo nvarchar(30) NOT NULL
            CONSTRAINT DF_SaInvoiceDetail_SONo DEFAULT (N'');

    IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'SOLine') IS NULL
        ALTER TABLE dbo.SaInvoiceDetail ADD SOLine smallint NULL;

    IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'CustRel') IS NULL
        ALTER TABLE dbo.SaInvoiceDetail ADD CustRel smallint NULL;

    IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'LinkDo') IS NULL
        ALTER TABLE dbo.SaInvoiceDetail ADD LinkDo bit NOT NULL
            CONSTRAINT DF_SaInvoiceDetail_LinkDo DEFAULT (0);

    IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'SoConsumedQty') IS NULL
        ALTER TABLE dbo.SaInvoiceDetail ADD SoConsumedQty decimal(18,4) NOT NULL
            CONSTRAINT DF_SaInvoiceDetail_SoConsumedQty DEFAULT (0);

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_SaInvoiceDetail_SoConsumedQty' AND parent_object_id = OBJECT_ID(N'dbo.SaInvoiceDetail'))
        ALTER TABLE dbo.SaInvoiceDetail ADD CONSTRAINT CK_SaInvoiceDetail_SoConsumedQty CHECK (SoConsumedQty >= 0);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaInvoiceDetail_Company_Branch_SoNo_SoLine' AND object_id = OBJECT_ID(N'dbo.SaInvoiceDetail'))
        CREATE INDEX IX_SaInvoiceDetail_Company_Branch_SoNo_SoLine
        ON dbo.SaInvoiceDetail (CompanyCode, BranchCode, SONo, SOLine);
END
GO
