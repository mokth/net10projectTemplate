-- Sales Credit/Debit Note Phase 1 schema (manual DBA script — do NOT run at app startup).
-- Option A PK: (CompanyCode, BranchCode, DocNo). Idempotent IF OBJECT_ID / COL_LENGTH.
-- Also adds IvTrxBatch.SourceFingerprint and UQ_IvTrxBatch_CR_CnRef.
GO

IF OBJECT_ID(N'dbo.SaCDN', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaCDN (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DocNo nvarchar(30) NOT NULL,
        DocDate datetime2 NOT NULL,
        Status nvarchar(20) NOT NULL,
        InvNo nvarchar(30) NULL,
        DONo nvarchar(30) NULL,
        Type nvarchar(20) NOT NULL,
        CustCode nvarchar(60) NOT NULL,
        CustName nvarchar(200) NULL,
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
        GrossAmnt decimal(18,2) NOT NULL CONSTRAINT DF_SaCDN_GrossAmnt DEFAULT (0),
        Taxes decimal(18,2) NOT NULL CONSTRAINT DF_SaCDN_Taxes DEFAULT (0),
        TotAmnt decimal(18,2) NOT NULL CONSTRAINT DF_SaCDN_TotAmnt DEFAULT (0),
        Prefix nvarchar(20) NULL,
        LocationCode nvarchar(10) NULL,
        ProjID nvarchar(20) NULL,
        SalesRep nvarchar(20) NULL,
        Dept nvarchar(20) NULL,
        ExportStatus bit NULL,
        CurrRate decimal(18,6) NOT NULL CONSTRAINT DF_SaCDN_CurrRate DEFAULT (1),
        RefNo nvarchar(50) NULL,
        ExternalDocNo nvarchar(50) NULL,
        ReturnStock bit NOT NULL CONSTRAINT DF_SaCDN_ReturnStock DEFAULT (0),
        IRNMCancelOn datetime2 NULL,
        IRBMSubmitID nvarchar(50) NULL,
        IRBMUUID nvarchar(50) NULL,
        IRBMORIUUID nvarchar(50) NULL,
        IRBMSentOn datetime2 NULL,
        IRBMValidOn datetime2 NULL,
        IRBMError nvarchar(500) NULL,
        IRBMStatus nvarchar(50) NULL,
        PostedDate datetime2 NULL,
        PostedBy nvarchar(20) NULL,
        RollbackDate datetime2 NULL,
        RollbackBy nvarchar(20) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SaCDN PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocNo),
        CONSTRAINT CK_SaCDN_Type CHECK (Type IN (N'CN', N'DN')),
        CONSTRAINT CK_SaCDN_Status CHECK (Status IN (N'NEW', N'POSTED'))
    );
END
GO

IF OBJECT_ID(N'dbo.SaCDNDetail', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaCDNDetail (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DocNo nvarchar(30) NOT NULL,
        Line smallint NOT NULL,
        ICode nvarchar(30) NOT NULL,
        IDesc nvarchar(200) NULL,
        CustICode nvarchar(30) NULL,
        Qty decimal(18,4) NOT NULL CONSTRAINT DF_SaCDNDetail_Qty DEFAULT (0),
        UnitPrice decimal(18,4) NOT NULL CONSTRAINT DF_SaCDNDetail_UnitPrice DEFAULT (0),
        SellingUOM nvarchar(10) NULL,
        StdUOM nvarchar(10) NULL,
        WtUOM nvarchar(10) NULL,
        StdQty decimal(18,4) NOT NULL CONSTRAINT DF_SaCDNDetail_StdQty DEFAULT (0),
        WtQty decimal(18,4) NOT NULL CONSTRAINT DF_SaCDNDetail_WtQty DEFAULT (0),
        StdCustPSize decimal(18,4) NOT NULL CONSTRAINT DF_SaCDNDetail_StdCustPSize DEFAULT (0),
        TaxAmt decimal(18,2) NOT NULL CONSTRAINT DF_SaCDNDetail_TaxAmt DEFAULT (0),
        Amount decimal(18,2) NOT NULL CONSTRAINT DF_SaCDNDetail_Amount DEFAULT (0),
        CustPO nvarchar(50) NULL,
        Remarks nvarchar(250) NULL,
        ItemGLCode nvarchar(20) NULL,
        TaxGroup nvarchar(20) NULL,
        IsInclusive bit NOT NULL CONSTRAINT DF_SaCDNDetail_IsInclusive DEFAULT (0),
        Discount decimal(18,6) NOT NULL CONSTRAINT DF_SaCDNDetail_Discount DEFAULT (0),
        ItemDiscount decimal(18,6) NOT NULL CONSTRAINT DF_SaCDNDetail_ItemDiscount DEFAULT (0),
        ItemDiscount1 decimal(18,6) NOT NULL CONSTRAINT DF_SaCDNDetail_ItemDiscount1 DEFAULT (0),
        ItemDiscount2 decimal(18,6) NOT NULL CONSTRAINT DF_SaCDNDetail_ItemDiscount2 DEFAULT (0),
        ItemDiscount3 decimal(18,6) NOT NULL CONSTRAINT DF_SaCDNDetail_ItemDiscount3 DEFAULT (0),
        ItemDiscount4 decimal(18,6) NOT NULL CONSTRAINT DF_SaCDNDetail_ItemDiscount4 DEFAULT (0),
        ItemDiscount5 decimal(18,6) NOT NULL CONSTRAINT DF_SaCDNDetail_ItemDiscount5 DEFAULT (0),
        ItemDiscount6 decimal(18,6) NOT NULL CONSTRAINT DF_SaCDNDetail_ItemDiscount6 DEFAULT (0),
        IDiscountType nvarchar(20) NULL,
        IDiscountType1 nvarchar(20) NULL,
        NetAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaCDNDetail_NetAmount DEFAULT (0),
        CostPrice decimal(18,4) NOT NULL CONSTRAINT DF_SaCDNDetail_CostPrice DEFAULT (0),
        Classification nvarchar(50) NULL,
        FrWarehouse nvarchar(20) NULL,
        LocCode nvarchar(20) NULL,
        IStatus nvarchar(20) NULL,
        LotNo nvarchar(50) NULL,
        ExpiryDate date NULL,
        StockControl bit NOT NULL CONSTRAINT DF_SaCDNDetail_StockControl DEFAULT (0),
        OriginalUnitPrice decimal(18,4) NULL,
        OverrideReason nvarchar(100) NULL,
        CONSTRAINT PK_SaCDNDetail PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocNo, Line),
        CONSTRAINT FK_SaCDNDetail_SaCDN FOREIGN KEY (CompanyCode, BranchCode, DocNo)
            REFERENCES dbo.SaCDN (CompanyCode, BranchCode, DocNo) ON DELETE CASCADE
    );
END
GO

IF OBJECT_ID(N'dbo.SaCDN', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaCDN_Company_Branch_Status_DocDate' AND object_id = OBJECT_ID(N'dbo.SaCDN'))
    CREATE INDEX IX_SaCDN_Company_Branch_Status_DocDate
    ON dbo.SaCDN (CompanyCode, BranchCode, Status, DocDate);
GO

IF OBJECT_ID(N'dbo.SaCDN', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaCDN_Company_Branch_Type_Status' AND object_id = OBJECT_ID(N'dbo.SaCDN'))
    CREATE INDEX IX_SaCDN_Company_Branch_Type_Status
    ON dbo.SaCDN (CompanyCode, BranchCode, Type, Status);
GO

IF OBJECT_ID(N'dbo.SaCDN', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaCDN_Company_Branch_CustCode' AND object_id = OBJECT_ID(N'dbo.SaCDN'))
    CREATE INDEX IX_SaCDN_Company_Branch_CustCode
    ON dbo.SaCDN (CompanyCode, BranchCode, CustCode);
GO

IF OBJECT_ID(N'dbo.SaCDN', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaCDN_Company_Branch_InvNo' AND object_id = OBJECT_ID(N'dbo.SaCDN'))
    CREATE INDEX IX_SaCDN_Company_Branch_InvNo
    ON dbo.SaCDN (CompanyCode, BranchCode, InvNo);
GO

-- IvTrxBatch fingerprint for CN-generated CR idempotency
IF OBJECT_ID(N'dbo.IvTrxBatch', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.IvTrxBatch', N'SourceFingerprint') IS NULL
    ALTER TABLE dbo.IvTrxBatch ADD SourceFingerprint nvarchar(64) NULL;
GO

IF OBJECT_ID(N'dbo.IvTrxBatch', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'UQ_IvTrxBatch_CR_CnRef'
          AND object_id = OBJECT_ID(N'dbo.IvTrxBatch')
    )
    BEGIN
        -- Filtered indexes cannot use LIKE. Range is equivalent to RefNo LIKE N'CN/%'.
        CREATE UNIQUE INDEX UQ_IvTrxBatch_CR_CnRef
        ON dbo.IvTrxBatch
        (
            CompanyCode,
            BranchCode,
            RefNo
        )
        WHERE TrxType = N'CR'
          AND RefNo >= N'CN/'
          AND RefNo < N'CN0';
    END
END;
GO
