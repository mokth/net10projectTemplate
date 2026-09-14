-- Purchase Credit & Debit Notes (PoCdn) Phase 1 schema.
-- Manual DBA script — do NOT run at app startup.
-- PK: (CompanyCode, BranchCode, DocNo). Idempotent: IF OBJECT_ID / COL_LENGTH / sys.indexes.
--
-- PREREQUISITE: scripts\rename-po-cdn-to-po-invoice.sql must already have been run.
--   SQL Server's case-insensitive collation makes PoCdn and the retired POCDN the same name,
--   so this script may only create dbo.PoCdn once POCDN has been renamed to POInvoice.
--
-- Run order:
--   1. rename-po-cdn-to-po-invoice.sql   (existing databases only)
--   2. create-pocdn.sql                  (this file)
--   3. seed-pocdn-numbering.sql
--   4. init-pocdn-menu.sql               (after menus.xml has been synced at least once)
--
-- Design notes (plan controls):
--   * Type = CN|DN. CN may reference a posted POInvoice INV; DN may stand alone.
--   * ReturnStock is a header gate; each line declares IsStockReturn (C43).
--   * InvLineNo points at the source POInvoiceDetail.Line (C24); no GrNo/GrLineNo (C4).
--   * SupplierDocNo is the supplier's own number and the only place it is stored (C14).
--   * IRBM* / SelfBilled are created now (nullable, unused in Phase 1) so the future
--     e-invoice phase needs no migration. AccountingStatus arrives with the AP/GL phase.
GO

IF OBJECT_ID(N'dbo.PoCdn', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoCdn (
        CompanyCode    nvarchar(10)  NOT NULL,
        BranchCode     nvarchar(10)  NOT NULL,
        DocNo          nvarchar(30)  NOT NULL,
        DocDate        datetime2     NOT NULL,
        Status         nvarchar(20)  NOT NULL,
        Type           nvarchar(20)  NOT NULL,
        Prefix         nvarchar(20)  NULL,

        VendorCode     nvarchar(60)  NOT NULL,
        VendorName     nvarchar(200) NULL,
        InvAddress1    nvarchar(100) NULL,
        InvAddress2    nvarchar(100) NULL,
        InvAddress3    nvarchar(100) NULL,
        InvAddress4    nvarchar(100) NULL,
        City           nvarchar(50)  NULL,
        State          nvarchar(50)  NULL,
        PostalCode     nvarchar(20)  NULL,
        Country        nvarchar(50)  NULL,
        Tel            nvarchar(50)  NULL,
        Fax            nvarchar(50)  NULL,
        PayCode        nvarchar(20)  NULL,

        Currency       nvarchar(20)  NULL,
        CurrRate       decimal(18,6) NOT NULL CONSTRAINT DF_PoCdn_CurrRate DEFAULT (1),
        TaxGrCode      nvarchar(20)  NULL,
        Remarks        nvarchar(500) NULL,

        GrossAmnt      decimal(18,2) NOT NULL CONSTRAINT DF_PoCdn_GrossAmnt DEFAULT (0),
        Taxes          decimal(18,2) NOT NULL CONSTRAINT DF_PoCdn_Taxes     DEFAULT (0),
        TotAmnt        decimal(18,2) NOT NULL CONSTRAINT DF_PoCdn_TotAmnt   DEFAULT (0),

        LocationCode   nvarchar(10)  NULL,
        ProjID         nvarchar(20)  NULL,
        BuyerCode      nvarchar(20)  NULL,
        Dept           nvarchar(20)  NULL,
        ExportStatus   bit           NULL,

        RefNo          nvarchar(50)  NULL,
        ExternalDocNo  nvarchar(50)  NULL,

        -- C2 / C14 / C16 / C44: supplier's own document number; the only place it is stored.
        SupplierDocNo  nvarchar(50)  NULL,
        SupplierDocDate date         NULL,

        -- C9: union CHECK only; the per-Type taxonomy is enforced by PoCdnCalc/PoCdnService.
        ReasonCode     nvarchar(30)  NULL,

        -- C24: posted POInvoice target. Header-level only; line-level detail is InvLineNo.
        InvNo          nvarchar(30)  NULL,

        -- C7 / C43: header gate for physical return; the owned VR batch (C7 one-to-one).
        ReturnStock    bit           NOT NULL CONSTRAINT DF_PoCdn_ReturnStock DEFAULT (0),
        VrBatchNo      int           NULL,

        -- Reserved for the MyInvois phase; nullable and unused in Phase 1.
        IRBMSubmitID   nvarchar(50)  NULL,
        IRBMUUID       nvarchar(50)  NULL,
        IRBMORIUUID    nvarchar(50)  NULL,
        IRBMSentOn     datetime2     NULL,
        IRBMValidOn    datetime2     NULL,
        IRBMError      nvarchar(500) NULL,
        IRBMStatus     nvarchar(50)  NULL,
        SelfBilled     bit           NULL,

        -- C1: POSTED is operational finalization, not AP/GL posting.
        PostedDate     datetime2     NULL,
        PostedBy       nvarchar(20)  NULL,
        RollbackDate   datetime2     NULL,
        RollbackBy     nvarchar(20)  NULL,

        Created        datetime2     NULL,
        UserID         nvarchar(20)  NULL,
        Updated        datetime2     NULL,
        UpdatedUID     nvarchar(20)  NULL,
        RowVersion     rowversion    NOT NULL,

        CONSTRAINT PK_PoCdn PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocNo),
        CONSTRAINT CK_PoCdn_Type CHECK (Type IN (N'CN', N'DN')),
        CONSTRAINT CK_PoCdn_Status CHECK (Status IN (N'NEW', N'POSTED')),
        CONSTRAINT CK_PoCdn_ReasonCode CHECK (
            ReasonCode IS NULL OR ReasonCode IN (
                N'RETURN', N'DAMAGED', N'SHORT_SUPPLY',
                N'PRICE_ADJUSTMENT', N'REBATE', N'OVERBILL', N'TAX_ADJUSTMENT',
                N'QUANTITY_ADJUSTMENT', N'FREIGHT_ADJUSTMENT', N'REBATE_REVERSAL',
                N'SUPPLIER_CLAIM', N'INTERNAL_ADJUSTMENT', N'OTHER')),
        CONSTRAINT CK_PoCdn_CurrRate CHECK (CurrRate > 0),
        CONSTRAINT CK_PoCdn_Amounts CHECK (GrossAmnt >= 0 AND Taxes >= 0 AND TotAmnt >= 0)
    );
    PRINT N'Created table dbo.PoCdn';
END
ELSE
    PRINT N'Table dbo.PoCdn already exists';
GO

-- Forward-compatibility guards for databases created by an earlier revision of this script.
IF OBJECT_ID(N'dbo.PoCdn', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PoCdn', N'SupplierDocDate') IS NULL
    ALTER TABLE dbo.PoCdn ADD SupplierDocDate date NULL;
GO
IF OBJECT_ID(N'dbo.PoCdn', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PoCdn', N'VrBatchNo') IS NULL
    ALTER TABLE dbo.PoCdn ADD VrBatchNo int NULL;
GO
IF OBJECT_ID(N'dbo.PoCdn', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PoCdn', N'InvLineNo') IS NULL
    PRINT N'Note: InvLineNo lives on dbo.PoCdnDetail, not dbo.PoCdn';
GO

IF OBJECT_ID(N'dbo.PoCdnDetail', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoCdnDetail (
        CompanyCode   nvarchar(10)  NOT NULL,
        BranchCode    nvarchar(10)  NOT NULL,
        DocNo         nvarchar(30)  NOT NULL,
        Line          smallint      NOT NULL,

        -- C24: source POInvoiceDetail.Line. NULL = header-level adjustment.
        InvLineNo     smallint      NULL,

        -- C43: declared physical-return intent. Never inferred.
        IsStockReturn bit           NOT NULL CONSTRAINT DF_PoCdnDetail_IsStockReturn DEFAULT (0),

        ICode         nvarchar(30)  NULL,   -- blank on a tax-only line (C37)
        IDesc         nvarchar(200) NULL,
        Qty           decimal(18,4) NOT NULL CONSTRAINT DF_PoCdnDetail_Qty       DEFAULT (0),
        UnitPrice     decimal(18,4) NOT NULL CONSTRAINT DF_PoCdnDetail_UnitPrice DEFAULT (0),
        SellingUOM    nvarchar(10)  NULL,
        StdUOM        nvarchar(10)  NULL,
        WtUOM         nvarchar(10)  NULL,
        StdQty        decimal(18,4) NOT NULL CONSTRAINT DF_PoCdnDetail_StdQty    DEFAULT (0),
        WtQty         decimal(18,4) NOT NULL CONSTRAINT DF_PoCdnDetail_WtQty     DEFAULT (0),
        StdCustPSize  decimal(18,4) NOT NULL CONSTRAINT DF_PoCdnDetail_StdCustPSize DEFAULT (0),

        TaxAmt        decimal(18,2) NOT NULL CONSTRAINT DF_PoCdnDetail_TaxAmt    DEFAULT (0),
        Amount        decimal(18,2) NOT NULL CONSTRAINT DF_PoCdnDetail_Amount    DEFAULT (0),
        Remarks       nvarchar(250) NULL,
        ItemGLCode    nvarchar(20)  NULL,
        TaxGroup      nvarchar(20)  NULL,
        IsInclusive   bit           NOT NULL CONSTRAINT DF_PoCdnDetail_IsInclusive DEFAULT (0),

        Discount      decimal(18,6) NOT NULL CONSTRAINT DF_PoCdnDetail_Discount      DEFAULT (0),
        ItemDiscount  decimal(18,6) NOT NULL CONSTRAINT DF_PoCdnDetail_ItemDiscount  DEFAULT (0),
        ItemDiscount1 decimal(18,6) NOT NULL CONSTRAINT DF_PoCdnDetail_ItemDiscount1 DEFAULT (0),
        ItemDiscount2 decimal(18,6) NOT NULL CONSTRAINT DF_PoCdnDetail_ItemDiscount2 DEFAULT (0),
        ItemDiscount3 decimal(18,6) NOT NULL CONSTRAINT DF_PoCdnDetail_ItemDiscount3 DEFAULT (0),
        ItemDiscount4 decimal(18,6) NOT NULL CONSTRAINT DF_PoCdnDetail_ItemDiscount4 DEFAULT (0),
        ItemDiscount5 decimal(18,6) NOT NULL CONSTRAINT DF_PoCdnDetail_ItemDiscount5 DEFAULT (0),
        ItemDiscount6 decimal(18,6) NOT NULL CONSTRAINT DF_PoCdnDetail_ItemDiscount6 DEFAULT (0),
        IDiscountType  nvarchar(20) NULL,
        IDiscountType1 nvarchar(20) NULL,

        NetAmount     decimal(18,2) NOT NULL CONSTRAINT DF_PoCdnDetail_NetAmount DEFAULT (0),

        -- C28: informational snapshot only. Never read for VR inventory valuation.
        CostPrice     decimal(18,4) NOT NULL CONSTRAINT DF_PoCdnDetail_CostPrice DEFAULT (0),
        Classification nvarchar(50) NULL,

        -- C47: PO link. Optional; required on a stock-return line.
        PoNo          nvarchar(20)  NULL,
        PoRelNo       smallint      NULL,
        PoLineNo      smallint      NULL,

        -- C27 / C35: inventory source. FromBalLocId is authoritative.
        FrWarehouse   nvarchar(20)  NULL,
        LocCode       nvarchar(20)  NULL,
        IStatus       nvarchar(20)  NULL,
        LotNo         nvarchar(50)  NULL,
        ExpiryDate    date          NULL,
        StockControl  bit           NOT NULL CONSTRAINT DF_PoCdnDetail_StockControl DEFAULT (0),
        FromBalLocId  int           NULL,

        CONSTRAINT PK_PoCdnDetail PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DocNo, Line),
        CONSTRAINT FK_PoCdnDetail_PoCdn FOREIGN KEY (CompanyCode, BranchCode, DocNo)
            REFERENCES dbo.PoCdn (CompanyCode, BranchCode, DocNo) ON DELETE CASCADE,
        -- C30: positive quantities and values; the tax-only shape is the only zero shape.
        CONSTRAINT CK_PoCdnDetail_NonNegative CHECK (Qty >= 0 AND UnitPrice >= 0 AND TaxAmt >= 0)
    );
    PRINT N'Created table dbo.PoCdnDetail';
END
ELSE
    PRINT N'Table dbo.PoCdnDetail already exists';
GO

-- Forward-compatibility guard: IsStockReturn was added in Round 6.
IF OBJECT_ID(N'dbo.PoCdnDetail', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PoCdnDetail', N'IsStockReturn') IS NULL
BEGIN
    ALTER TABLE dbo.PoCdnDetail
        ADD IsStockReturn bit NOT NULL CONSTRAINT DF_PoCdnDetail_IsStockReturn DEFAULT (0);
    PRINT N'Added dbo.PoCdnDetail.IsStockReturn';
END
GO

-- ── Indexes: header ───────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.PoCdn', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoCdn_Company_Branch_Type_Status_DocDate' AND object_id = OBJECT_ID(N'dbo.PoCdn'))
    CREATE INDEX IX_PoCdn_Company_Branch_Type_Status_DocDate
    ON dbo.PoCdn (CompanyCode, BranchCode, Type, Status, DocDate);
GO

IF OBJECT_ID(N'dbo.PoCdn', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoCdn_Company_Branch_VendorCode' AND object_id = OBJECT_ID(N'dbo.PoCdn'))
    CREATE INDEX IX_PoCdn_Company_Branch_VendorCode
    ON dbo.PoCdn (CompanyCode, BranchCode, VendorCode);
GO

IF OBJECT_ID(N'dbo.PoCdn', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoCdn_Company_Branch_InvNo' AND object_id = OBJECT_ID(N'dbo.PoCdn'))
    CREATE INDEX IX_PoCdn_Company_Branch_InvNo
    ON dbo.PoCdn (CompanyCode, BranchCode, InvNo);
GO

-- ── C2 / C29: supplier-document duplicate control (branch-local, Type-specific) ──
-- Filtered on SupplierDocNo IS NOT NULL so an internal adjustment (C16/C44) never collides
-- and a blank supplier number can never occupy the key. C44 normalises NULL/''/whitespace to NULL.
IF OBJECT_ID(N'dbo.PoCdn', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoCdn_SupplierDoc' AND object_id = OBJECT_ID(N'dbo.PoCdn'))
BEGIN
    CREATE UNIQUE INDEX UX_PoCdn_SupplierDoc
    ON dbo.PoCdn (CompanyCode, BranchCode, VendorCode, Type, SupplierDocNo)
    WHERE SupplierDocNo IS NOT NULL;
    PRINT N'Created UX_PoCdn_SupplierDoc';
END
GO

-- ── Indexes: detail ───────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.PoCdnDetail', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoCdnDetail_Company_Branch_PoLine' AND object_id = OBJECT_ID(N'dbo.PoCdnDetail'))
    CREATE INDEX IX_PoCdnDetail_Company_Branch_PoLine
    ON dbo.PoCdnDetail (CompanyCode, BranchCode, PoNo, PoRelNo, PoLineNo);
GO

-- C34 / C41: source-line consumption is aggregated per PoCdn.InvNo + Detail.InvLineNo.
-- InvNo lives on the header, so the leading columns are the FK (Company, Branch, DocNo).
IF OBJECT_ID(N'dbo.PoCdnDetail', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoCdnDetail_Company_Branch_Doc_InvLineNo' AND object_id = OBJECT_ID(N'dbo.PoCdnDetail'))
    CREATE INDEX IX_PoCdnDetail_Company_Branch_Doc_InvLineNo
    ON dbo.PoCdnDetail (CompanyCode, BranchCode, DocNo, InvLineNo);
GO

-- ── C7: one PoCdn owns at most one VR batch (and vice versa) ───────────────────
IF OBJECT_ID(N'dbo.IvTrxBatch', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.IvTrxBatch', N'SourceFingerprint') IS NULL
BEGIN
    ALTER TABLE dbo.IvTrxBatch ADD SourceFingerprint nvarchar(64) NULL;
    PRINT N'Added dbo.IvTrxBatch.SourceFingerprint';
END
GO

IF OBJECT_ID(N'dbo.IvTrxBatch', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'UQ_IvTrxBatch_VR_PcnRef'
          AND object_id = OBJECT_ID(N'dbo.IvTrxBatch')
    )
    BEGIN
        -- Filtered indexes cannot use LIKE. Range is equivalent to RefNo LIKE N'PCN/%'.
        CREATE UNIQUE INDEX UQ_IvTrxBatch_VR_PcnRef
        ON dbo.IvTrxBatch
        (
            CompanyCode,
            BranchCode,
            RefNo
        )
        WHERE TrxType = N'VR'
          AND RefNo >= N'PCN/'
          AND RefNo < N'PCN0';
        PRINT N'Created UQ_IvTrxBatch_VR_PcnRef';
    END
END;
GO

PRINT N'create-pocdn.sql complete.';
GO
