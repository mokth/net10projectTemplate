-- Sales Quotation (SaQT) schema — manual DBA script. Do NOT run at app startup.
-- Mirrors the SaSO revision identity model (see scripts/create-saso.sql + alter-saso-revision.sql).
-- PK: (CompanyCode, BranchCode, QTNo, CustRel). Current revision: UX_SaQT_Current WHERE IsCurrent = 1.
--
-- MVP state machine (see plans/sa-quotation_88ba372e.plan.md):
--   NEW  -> Send -> SENT -> Accept -> ACCEPTED -> Convert -> CLOSED (CONVERTED)
--   NEW/SENT -> Lose -> LOST | Cancel -> CANCELLED | Expire -> EXPIRED
--   NEW/SENT/ACCEPTED -> Revise -> old SUPERSEDED (IsCurrent = 0) + new CustRel NEW
--   CANCELLED / LOST / EXPIRED / CLOSED are terminal on their (still current) revision.
--
-- The filtered index UX_SaQT_Current (WHERE IsCurrent = 1) requires ANSI_NULLS ON and
-- QUOTED_IDENTIFIER ON. SSMS sets both, but sqlcmd defaults QUOTED_IDENTIFIER OFF, which makes
-- CREATE INDEX ... WHERE fail with Msg 1934. Set them explicitly so the script behaves the same
-- no matter which client runs it.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.SaQT', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaQT (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        QTNo nvarchar(30) NOT NULL,
        CustRel smallint NOT NULL CONSTRAINT DF_SaQT_CustRel DEFAULT (1),
        IsCurrent bit NOT NULL CONSTRAINT DF_SaQT_IsCurrent DEFAULT (1),
        LastCustRel smallint NOT NULL CONSTRAINT DF_SaQT_LastCustRel DEFAULT (1),
        RevisionReason nvarchar(200) NULL,
        QTDate datetime2 NOT NULL,
        ValidUntil datetime2 NOT NULL,
        Status nvarchar(20) NOT NULL,
        ConversionStatus nvarchar(10) NOT NULL CONSTRAINT DF_SaQT_ConversionStatus DEFAULT (N'NONE'),
        ClosedReason nvarchar(20) NULL,
        ClosedDate datetime2 NULL,
        ClosedBy nvarchar(20) NULL,
        SentDate datetime2 NULL,
        SentBy nvarchar(20) NULL,
        AcceptedDate datetime2 NULL,
        AcceptedBy nvarchar(20) NULL,
        LostDate datetime2 NULL,
        LostBy nvarchar(20) NULL,
        LostReason nvarchar(200) NULL,
        ExpiredDate datetime2 NULL,
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
        ContactPerson nvarchar(100) NULL,
        TaxGrCode nvarchar(20) NULL,
        Currency nvarchar(20) NULL,
        CurrRate decimal(18,6) NOT NULL CONSTRAINT DF_SaQT_CurrRate DEFAULT (1),
        PayCode nvarchar(20) NULL,
        Remarks nvarchar(500) NULL,
        InternalRemarks nvarchar(500) NULL,
        -- QT-only commercial terms. SaSO has no ShipVia / DeliveryTerms; these are persisted on the
        -- quotation for print/terms only and are deliberately NOT mapped onto the created SO.
        ShipVia nvarchar(100) NULL,
        DeliveryTerms nvarchar(200) NULL,
        GrossAmnt decimal(18,2) NOT NULL CONSTRAINT DF_SaQT_GrossAmnt DEFAULT (0),
        Taxes decimal(18,2) NOT NULL CONSTRAINT DF_SaQT_Taxes DEFAULT (0),
        TotAmnt decimal(18,2) NOT NULL CONSTRAINT DF_SaQT_TotAmnt DEFAULT (0),
        Prefix nvarchar(20) NULL,
        LocationCode nvarchar(10) NULL,
        CustDiscount decimal(18,6) NOT NULL CONSTRAINT DF_SaQT_CustDiscount DEFAULT (0),
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
        CONSTRAINT PK_SaQT PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, QTNo, CustRel),
        CONSTRAINT CK_SaQT_Status CHECK (
            Status IN (N'NEW', N'SENT', N'ACCEPTED', N'CANCELLED', N'LOST', N'EXPIRED', N'CLOSED', N'SUPERSEDED')
        ),
        CONSTRAINT CK_SaQT_ConversionStatus CHECK (
            ConversionStatus IN (N'NONE', N'PARTIAL', N'FULL')
        ),
        -- ClosedReason is CONVERTED-only in MVP (no ForceClose).
        CONSTRAINT CK_SaQT_Status_ClosedReason CHECK (
            (Status = N'CLOSED' AND ClosedReason = N'CONVERTED')
            OR
            (Status <> N'CLOSED' AND ClosedReason IS NULL)
        ),
        -- A superseded revision is always IsCurrent = 0; every live status is IsCurrent = 1.
        CONSTRAINT CK_SaQT_IsCurrent_Status CHECK (
            (IsCurrent = 1 AND Status IN (N'NEW', N'SENT', N'ACCEPTED', N'CANCELLED', N'LOST', N'EXPIRED', N'CLOSED'))
            OR
            (IsCurrent = 0 AND Status = N'SUPERSEDED')
        ),
        -- FULL conversion implies the quotation is CLOSED; CLOSED implies FULL.
        CONSTRAINT CK_SaQT_Closed_Conversion CHECK (
            (Status = N'CLOSED' AND ConversionStatus = N'FULL')
            OR
            (Status <> N'CLOSED' AND ConversionStatus IN (N'NONE', N'PARTIAL'))
        )
    );
END
GO

IF OBJECT_ID(N'dbo.SaQTDetail', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaQTDetail (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        QTNo nvarchar(30) NOT NULL,
        CustRel smallint NOT NULL CONSTRAINT DF_SaQTDetail_CustRel DEFAULT (1),
        Line smallint NOT NULL,
        ICode nvarchar(30) NULL,
        IDesc nvarchar(200) NULL,
        CustICode nvarchar(30) NULL,
        OrderQty decimal(18,4) NOT NULL,
        -- Quantity already turned into a Sales Order. Monotonic. MVP always moves 0 -> OrderQty
        -- (full conversion), so PARTIAL never appears; the column exists so partial conversion can
        -- be added later without a migration.
        ConvertedQty decimal(18,4) NOT NULL CONSTRAINT DF_SaQTDetail_ConvertedQty DEFAULT (0),
        UnitPrice decimal(18,4) NOT NULL CONSTRAINT DF_SaQTDetail_UnitPrice DEFAULT (0),
        SellingUOM nvarchar(10) NULL,
        StdUOM nvarchar(10) NULL,
        WtUOM nvarchar(10) NULL,
        StdQty decimal(18,4) NOT NULL CONSTRAINT DF_SaQTDetail_StdQty DEFAULT (0),
        WtQty decimal(18,4) NOT NULL CONSTRAINT DF_SaQTDetail_WtQty DEFAULT (0),
        StdPSize decimal(18,4) NOT NULL CONSTRAINT DF_SaQTDetail_StdPSize DEFAULT (0),
        TaxAmt decimal(18,2) NOT NULL CONSTRAINT DF_SaQTDetail_TaxAmt DEFAULT (0),
        OrderType nvarchar(20) NULL,
        Remarks nvarchar(250) NULL,
        Amount decimal(18,2) NOT NULL CONSTRAINT DF_SaQTDetail_Amount DEFAULT (0),
        ItemGLCode nvarchar(20) NULL,
        Discount decimal(18,6) NOT NULL CONSTRAINT DF_SaQTDetail_Discount DEFAULT (0),
        NetAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaQTDetail_NetAmount DEFAULT (0),
        ItemDiscount decimal(18,6) NOT NULL CONSTRAINT DF_SaQTDetail_ItemDiscount DEFAULT (0),
        ItemDiscount2 decimal(18,6) NOT NULL CONSTRAINT DF_SaQTDetail_ItemDiscount2 DEFAULT (0),
        ItemDiscount3 decimal(18,6) NOT NULL CONSTRAINT DF_SaQTDetail_ItemDiscount3 DEFAULT (0),
        ItemDiscount4 decimal(18,6) NOT NULL CONSTRAINT DF_SaQTDetail_ItemDiscount4 DEFAULT (0),
        ItemDiscount5 decimal(18,6) NOT NULL CONSTRAINT DF_SaQTDetail_ItemDiscount5 DEFAULT (0),
        ItemDiscount6 decimal(18,6) NOT NULL CONSTRAINT DF_SaQTDetail_ItemDiscount6 DEFAULT (0),
        ItemDiscAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaQTDetail_ItemDiscAmount DEFAULT (0),
        ItemDiscAmount1 decimal(18,2) NOT NULL CONSTRAINT DF_SaQTDetail_ItemDiscAmount1 DEFAULT (0),
        IDiscountType nvarchar(20) NULL,
        Warehouse nvarchar(20) NULL,
        TaxGroup nvarchar(20) NULL,
        IsInclusive bit NOT NULL CONSTRAINT DF_SaQTDetail_IsInclusive DEFAULT (0),
        LocalAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaQTDetail_LocalAmount DEFAULT (0),
        StockControl bit NOT NULL CONSTRAINT DF_SaQTDetail_StockControl DEFAULT (1),
        Classification nvarchar(50) NULL,
        DeliveryDate datetime2 NULL,
        ETA datetime2 NULL,
        ETD datetime2 NULL,
        -- Pricing provenance (explanatory only — the price itself is frozen in UnitPrice).
        PricingSource nvarchar(40) NULL,
        PricingRef nvarchar(60) NULL,
        OriginalUnitPrice decimal(18,4) NULL,
        OverrideReason nvarchar(100) NULL,
        CONSTRAINT PK_SaQTDetail PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, QTNo, CustRel, Line),
        CONSTRAINT FK_SaQTDetail_SaQT FOREIGN KEY (CompanyCode, BranchCode, QTNo, CustRel)
            REFERENCES dbo.SaQT (CompanyCode, BranchCode, QTNo, CustRel) ON DELETE CASCADE,
        CONSTRAINT CK_SaQTDetail_OrderQty CHECK (OrderQty > 0),
        CONSTRAINT CK_SaQTDetail_ConvertedQty CHECK (ConvertedQty >= 0 AND ConvertedQty <= OrderQty)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SaQT_Current' AND object_id = OBJECT_ID(N'dbo.SaQT'))
    CREATE UNIQUE INDEX UX_SaQT_Current
    ON dbo.SaQT (CompanyCode, BranchCode, QTNo)
    WHERE IsCurrent = 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaQT_Company_Branch_Status_QTDate' AND object_id = OBJECT_ID(N'dbo.SaQT'))
    CREATE INDEX IX_SaQT_Company_Branch_Status_QTDate
    ON dbo.SaQT (CompanyCode, BranchCode, Status, QTDate);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaQT_Company_Branch_CustCode' AND object_id = OBJECT_ID(N'dbo.SaQT'))
    CREATE INDEX IX_SaQT_Company_Branch_CustCode
    ON dbo.SaQT (CompanyCode, BranchCode, CustCode);
GO

-- Lazy-expire scan + "what is about to lapse" reporting.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaQT_Company_Branch_ValidUntil' AND object_id = OBJECT_ID(N'dbo.SaQT'))
    CREATE INDEX IX_SaQT_Company_Branch_ValidUntil
    ON dbo.SaQT (CompanyCode, BranchCode, ValidUntil);
GO
