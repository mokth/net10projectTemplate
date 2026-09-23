-- Self-billed purchase invoice (LHDN e-Invoice document type 11) — schema.
-- Manual DBA script — do NOT run at app startup. Idempotent (IF OBJECT_ID / sys.indexes guards).
--
-- Run order:
--   1. create-po-sb-invoice.sql   (this file)
--   2. create-po-sb-cdn.sql
--   3. seed-po-sb-numbering.sql
--   4. init-pobsb-menu.sql        (after menus.xml has been synced at least once)
--
-- Design notes (plans/plan-poSelfBilledEInvoice.prompt.md):
--   * The company issues a self-billed invoice on behalf of the vendor, so the company is the payload
--     Supplier (issuer) and the vendor is the Customer. The vendor block is read LIVE from dbo.PoSupplier
--     when the payload is built — there is deliberately NO address/identity snapshot here (D4/D7).
--   * Deliberately slim: no PO/GR lineage, no stock or accounting effect, no supplier document number.
--   * Only Status = POSTED may be submitted, and IRBM* mirrors SaInvoice/SaCDN spellings exactly
--     (IRBMOutcome + IRNMCancelOn included) so the e-Invoice façade's load/apply cases are identical.
--   * GrossAmnt / Taxes / TotAmnt are derived from the detail lines (GrossAmnt + Taxes = TotAmnt);
--     the e-Invoice validator cross-checks the header against the sum of the lines within 0.05.
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.POSbInvoice', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POSbInvoice (
        CompanyCode    nvarchar(10)  NOT NULL,
        BranchCode     nvarchar(10)  NOT NULL,
        DocNo          nvarchar(30)  NOT NULL,
        DocDate        datetime2     NOT NULL,
        Status         nvarchar(20)  NOT NULL,
        Prefix         nvarchar(20)  NULL,

        VendorCode     nvarchar(60)  NOT NULL,
        VendorName     nvarchar(200) NULL,

        Currency       nvarchar(20)  NULL,
        CurrRate       decimal(18,6) NOT NULL CONSTRAINT DF_POSbInvoice_CurrRate DEFAULT (1),
        TaxGrCode      nvarchar(20)  NULL,
        Remarks        nvarchar(500) NULL,

        GrossAmnt      decimal(18,2) NOT NULL CONSTRAINT DF_POSbInvoice_GrossAmnt DEFAULT (0),
        Taxes          decimal(18,2) NOT NULL CONSTRAINT DF_POSbInvoice_Taxes     DEFAULT (0),
        TotAmnt        decimal(18,2) NOT NULL CONSTRAINT DF_POSbInvoice_TotAmnt   DEFAULT (0),

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

        CONSTRAINT PK_POSbInvoice PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocNo),
        CONSTRAINT CK_POSbInvoice_Status CHECK (Status IN (N'NEW', N'POSTED')),
        CONSTRAINT CK_POSbInvoice_CurrRate CHECK (CurrRate > 0),
        CONSTRAINT CK_POSbInvoice_Amounts CHECK (GrossAmnt >= 0 AND Taxes >= 0 AND TotAmnt >= 0)
    );
    PRINT N'Created table dbo.POSbInvoice';
END
ELSE
    PRINT N'Table dbo.POSbInvoice already exists';
GO

IF OBJECT_ID(N'dbo.POSbInvoiceDetail', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POSbInvoiceDetail (
        CompanyCode   nvarchar(10)  NOT NULL,
        BranchCode    nvarchar(10)  NOT NULL,
        DocNo         nvarchar(30)  NOT NULL,
        Line          smallint      NOT NULL,

        ICode         nvarchar(30)  NULL,
        IDesc         nvarchar(200) NULL,

        Qty           decimal(18,4) NOT NULL CONSTRAINT DF_POSbInvoiceDetail_Qty       DEFAULT (0),
        UnitPrice     decimal(18,4) NOT NULL CONSTRAINT DF_POSbInvoiceDetail_UnitPrice DEFAULT (0),
        SellingUOM    nvarchar(10)  NULL,
        StdUOM        nvarchar(10)  NULL,

        Amount        decimal(18,2) NOT NULL CONSTRAINT DF_POSbInvoiceDetail_Amount    DEFAULT (0),
        TaxAmt        decimal(18,2) NOT NULL CONSTRAINT DF_POSbInvoiceDetail_TaxAmt    DEFAULT (0),
        NetAmount     decimal(18,2) NOT NULL CONSTRAINT DF_POSbInvoiceDetail_NetAmount DEFAULT (0),

        TaxGroup      nvarchar(20)  NULL,
        IsInclusive   bit           NOT NULL CONSTRAINT DF_POSbInvoiceDetail_IsInclusive DEFAULT (0),

        Discount      decimal(18,6) NOT NULL CONSTRAINT DF_POSbInvoiceDetail_Discount      DEFAULT (0),
        ItemDiscount  decimal(18,6) NOT NULL CONSTRAINT DF_POSbInvoiceDetail_ItemDiscount  DEFAULT (0),
        ItemDiscount1 decimal(18,6) NOT NULL CONSTRAINT DF_POSbInvoiceDetail_ItemDiscount1 DEFAULT (0),
        IDiscountType  nvarchar(20) NULL,
        IDiscountType1 nvarchar(20) NULL,

        -- LHDN item classification code. Required on every submissible line.
        Classification nvarchar(50) NULL,
        Remarks        nvarchar(250) NULL,

        CONSTRAINT PK_POSbInvoiceDetail PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocNo, Line),
        CONSTRAINT FK_POSbInvoiceDetail_Header FOREIGN KEY (CompanyCode, BranchCode, DocNo)
            REFERENCES dbo.POSbInvoice (CompanyCode, BranchCode, DocNo) ON DELETE CASCADE,
        CONSTRAINT CK_POSbInvoiceDetail_Qty      CHECK (Qty >= 0),
        CONSTRAINT CK_POSbInvoiceDetail_UnitPrice CHECK (UnitPrice >= 0),
        CONSTRAINT CK_POSbInvoiceDetail_Amounts  CHECK (Amount >= 0 AND TaxAmt >= 0 AND NetAmount >= 0)
    );
    PRINT N'Created table dbo.POSbInvoiceDetail';
END
ELSE
    PRINT N'Table dbo.POSbInvoiceDetail already exists';
GO

-- Indexes. Guarded individually so a re-run is a clean no-op and a table created by an earlier
-- revision of this script converges.
IF OBJECT_ID(N'dbo.POSbInvoice', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_POSbInvoice_Company_Branch_Status_DocDate'
                     AND object_id = OBJECT_ID(N'dbo.POSbInvoice'))
    CREATE INDEX IX_POSbInvoice_Company_Branch_Status_DocDate
        ON dbo.POSbInvoice (CompanyCode, BranchCode, Status, DocDate);
GO

IF OBJECT_ID(N'dbo.POSbInvoice', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_POSbInvoice_Company_Branch_VendorCode'
                     AND object_id = OBJECT_ID(N'dbo.POSbInvoice'))
    CREATE INDEX IX_POSbInvoice_Company_Branch_VendorCode
        ON dbo.POSbInvoice (CompanyCode, BranchCode, VendorCode);
GO

IF OBJECT_ID(N'dbo.POSbInvoice', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_POSbInvoice_Company_Branch_IrbmStatus'
                     AND object_id = OBJECT_ID(N'dbo.POSbInvoice'))
    CREATE INDEX IX_POSbInvoice_Company_Branch_IrbmStatus
        ON dbo.POSbInvoice (CompanyCode, BranchCode, IRBMStatus);
GO

IF OBJECT_ID(N'dbo.POSbInvoiceDetail', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_POSbInvoiceDetail_Company_Branch_ICode'
                     AND object_id = OBJECT_ID(N'dbo.POSbInvoiceDetail'))
    CREATE INDEX IX_POSbInvoiceDetail_Company_Branch_ICode
        ON dbo.POSbInvoiceDetail (CompanyCode, BranchCode, ICode);
GO

-- Verification: expect 34 columns on the header and 22 on the detail.
SELECT N'POSbInvoice' AS ObjectName, COUNT(*) AS ColumnCount
FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.POSbInvoice')
UNION ALL
SELECT N'POSbInvoiceDetail', COUNT(*)
FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.POSbInvoiceDetail');
GO
