-- Self-billed purchase credit / debit note (LHDN e-Invoice document types 12 / 13) — schema.
-- Manual DBA script — do NOT run at app startup. Idempotent (IF OBJECT_ID / sys.indexes guards).
--
-- Run order:
--   1. create-po-sb-invoice.sql
--   2. create-po-sb-cdn.sql       (this file)
--   3. seed-po-sb-numbering.sql
--   4. init-pobsb-menu.sql        (after menus.xml has been synced at least once)
--
-- Design notes (plans/plan-poSelfBilledEInvoice.prompt.md):
--   * Type = CN (12) | DN (13). Both store positive amounts.
--   * OriginSbInvNo is a SOFT reference to dbo.POSbInvoice.DocNo, resolved by
--     (CompanyCode, BranchCode, DocNo) — never a bare number lookup, and deliberately no FK so the
--     origin's status can be validated in one place (PoSbOriginResolver). The origin must be VALID at
--     MyInvois with a UUID before the note can be submitted.
--   * No tax-only line shape: the e-Invoice validator refuses Qty <= 0, so every line is quantity-bearing.
--   * IRBM* mirrors SaInvoice/SaCDN spellings exactly (IRBMOutcome + IRNMCancelOn included).
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.POSbCdn', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POSbCdn (
        CompanyCode    nvarchar(10)  NOT NULL,
        BranchCode     nvarchar(10)  NOT NULL,
        DocNo          nvarchar(30)  NOT NULL,
        DocDate        datetime2     NOT NULL,
        Status         nvarchar(20)  NOT NULL,
        Type           nvarchar(20)  NOT NULL,
        Prefix         nvarchar(20)  NULL,

        VendorCode     nvarchar(60)  NOT NULL,
        VendorName     nvarchar(200) NULL,

        OriginSbInvNo  nvarchar(30)  NULL,

        Currency       nvarchar(20)  NULL,
        CurrRate       decimal(18,6) NOT NULL CONSTRAINT DF_POSbCdn_CurrRate DEFAULT (1),
        TaxGrCode      nvarchar(20)  NULL,
        Remarks        nvarchar(500) NULL,

        GrossAmnt      decimal(18,2) NOT NULL CONSTRAINT DF_POSbCdn_GrossAmnt DEFAULT (0),
        Taxes          decimal(18,2) NOT NULL CONSTRAINT DF_POSbCdn_Taxes     DEFAULT (0),
        TotAmnt        decimal(18,2) NOT NULL CONSTRAINT DF_POSbCdn_TotAmnt   DEFAULT (0),

        LocationCode   nvarchar(10)  NULL,

        -- LHDN e-Invoice state (nullable until submitted).
        IRBMSubmitID   nvarchar(50)  NULL,
        IRBMUUID       nvarchar(50)  NULL,
        IRBMORIUUID    nvarchar(50)  NULL,
        IRBMSentOn     datetime2     NULL,
        IRBMValidOn    datetime2     NULL,
        IRBMError      nvarchar(500) NULL,
        IRBMStatus     nvarchar(50)  NULL,
        IRBMOutcome    nvarchar(30)  NULL,
        IRNMCancelOn   datetime2     NULL,

        PostedDate     datetime2     NULL,
        PostedBy       nvarchar(20)  NULL,
        RollbackDate   datetime2     NULL,
        RollbackBy     nvarchar(20)  NULL,

        Created        datetime2     NULL,
        UserID         nvarchar(20)  NULL,
        Updated        datetime2     NULL,
        UpdatedUID     nvarchar(20)  NULL,
        RowVersion     rowversion    NOT NULL,

        CONSTRAINT PK_POSbCdn PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocNo),
        CONSTRAINT CK_POSbCdn_Type CHECK (Type IN (N'CN', N'DN')),
        CONSTRAINT CK_POSbCdn_Status CHECK (Status IN (N'NEW', N'POSTED')),
        CONSTRAINT CK_POSbCdn_CurrRate CHECK (CurrRate > 0),
        CONSTRAINT CK_POSbCdn_Amounts CHECK (GrossAmnt >= 0 AND Taxes >= 0 AND TotAmnt >= 0)
    );
    PRINT N'Created table dbo.POSbCdn';
END
ELSE
    PRINT N'Table dbo.POSbCdn already exists';
GO

IF OBJECT_ID(N'dbo.POSbCdnDetail', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POSbCdnDetail (
        CompanyCode   nvarchar(10)  NOT NULL,
        BranchCode    nvarchar(10)  NOT NULL,
        DocNo         nvarchar(30)  NOT NULL,
        Line          smallint      NOT NULL,

        ICode         nvarchar(30)  NULL,
        IDesc         nvarchar(200) NULL,

        Qty           decimal(18,4) NOT NULL CONSTRAINT DF_POSbCdnDetail_Qty       DEFAULT (0),
        UnitPrice     decimal(18,4) NOT NULL CONSTRAINT DF_POSbCdnDetail_UnitPrice DEFAULT (0),
        SellingUOM    nvarchar(10)  NULL,
        StdUOM        nvarchar(10)  NULL,

        Amount        decimal(18,2) NOT NULL CONSTRAINT DF_POSbCdnDetail_Amount    DEFAULT (0),
        TaxAmt        decimal(18,2) NOT NULL CONSTRAINT DF_POSbCdnDetail_TaxAmt    DEFAULT (0),
        NetAmount     decimal(18,2) NOT NULL CONSTRAINT DF_POSbCdnDetail_NetAmount DEFAULT (0),

        TaxGroup      nvarchar(20)  NULL,
        IsInclusive   bit           NOT NULL CONSTRAINT DF_POSbCdnDetail_IsInclusive DEFAULT (0),

        Discount      decimal(18,6) NOT NULL CONSTRAINT DF_POSbCdnDetail_Discount      DEFAULT (0),
        ItemDiscount  decimal(18,6) NOT NULL CONSTRAINT DF_POSbCdnDetail_ItemDiscount  DEFAULT (0),
        ItemDiscount1 decimal(18,6) NOT NULL CONSTRAINT DF_POSbCdnDetail_ItemDiscount1 DEFAULT (0),
        IDiscountType  nvarchar(20) NULL,
        IDiscountType1 nvarchar(20) NULL,

        Classification nvarchar(50) NULL,
        Remarks        nvarchar(250) NULL,

        CONSTRAINT PK_POSbCdnDetail PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocNo, Line),
        CONSTRAINT FK_POSbCdnDetail_Header FOREIGN KEY (CompanyCode, BranchCode, DocNo)
            REFERENCES dbo.POSbCdn (CompanyCode, BranchCode, DocNo) ON DELETE CASCADE,
        CONSTRAINT CK_POSbCdnDetail_Qty       CHECK (Qty >= 0),
        CONSTRAINT CK_POSbCdnDetail_UnitPrice CHECK (UnitPrice >= 0),
        CONSTRAINT CK_POSbCdnDetail_Amounts   CHECK (Amount >= 0 AND TaxAmt >= 0 AND NetAmount >= 0)
    );
    PRINT N'Created table dbo.POSbCdnDetail';
END
ELSE
    PRINT N'Table dbo.POSbCdnDetail already exists';
GO

IF OBJECT_ID(N'dbo.POSbCdn', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_POSbCdn_Company_Branch_Type_Status_DocDate'
                     AND object_id = OBJECT_ID(N'dbo.POSbCdn'))
    CREATE INDEX IX_POSbCdn_Company_Branch_Type_Status_DocDate
        ON dbo.POSbCdn (CompanyCode, BranchCode, Type, Status, DocDate);
GO

IF OBJECT_ID(N'dbo.POSbCdn', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_POSbCdn_Company_Branch_VendorCode'
                     AND object_id = OBJECT_ID(N'dbo.POSbCdn'))
    CREATE INDEX IX_POSbCdn_Company_Branch_VendorCode
        ON dbo.POSbCdn (CompanyCode, BranchCode, VendorCode);
GO

IF OBJECT_ID(N'dbo.POSbCdn', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_POSbCdn_Company_Branch_OriginSbInvNo'
                     AND object_id = OBJECT_ID(N'dbo.POSbCdn'))
    CREATE INDEX IX_POSbCdn_Company_Branch_OriginSbInvNo
        ON dbo.POSbCdn (CompanyCode, BranchCode, OriginSbInvNo);
GO

IF OBJECT_ID(N'dbo.POSbCdn', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_POSbCdn_Company_Branch_IrbmStatus'
                     AND object_id = OBJECT_ID(N'dbo.POSbCdn'))
    CREATE INDEX IX_POSbCdn_Company_Branch_IrbmStatus
        ON dbo.POSbCdn (CompanyCode, BranchCode, IRBMStatus);
GO

IF OBJECT_ID(N'dbo.POSbCdnDetail', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_POSbCdnDetail_Company_Branch_ICode'
                     AND object_id = OBJECT_ID(N'dbo.POSbCdnDetail'))
    CREATE INDEX IX_POSbCdnDetail_Company_Branch_ICode
        ON dbo.POSbCdnDetail (CompanyCode, BranchCode, ICode);
GO

-- Verification: expect 36 columns on the header and 22 on the detail.
SELECT N'POSbCdn' AS ObjectName, COUNT(*) AS ColumnCount
FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.POSbCdn')
UNION ALL
SELECT N'POSbCdnDetail', COUNT(*)
FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.POSbCdnDetail');
GO
