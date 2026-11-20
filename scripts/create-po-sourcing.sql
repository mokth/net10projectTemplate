-- =============================================================================
-- Purchase Sourcing, RFQ & Supplier Quotation Comparison (dbo.PoSourcing*)
-- Optional company-scoped feature (PROCUREMENT.SOURCING_ENABLED, default OFF).
-- Safe to run multiple times (idempotent). Does not modify existing PR/PO/Stock tables.
-- =============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.PoSourcingHdr', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoSourcingHdr
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        SourcingNo nvarchar(30) NOT NULL,
        Title nvarchar(200) NOT NULL,
        SourceType nvarchar(20) NOT NULL CONSTRAINT DF_PoSourcingHdr_SourceType DEFAULT (N'DIRECT'),
        SourcePrNo nvarchar(30) NULL,
        Requester nvarchar(50) NULL,
        BuyerCode nvarchar(20) NULL,
        DeptCode nvarchar(20) NULL,
        ProjectCode nvarchar(20) NULL,
        WorkOrderNo nvarchar(30) NULL,
        DeliveryWhse nvarchar(20) NULL,
        RequestDate datetime2 NOT NULL,
        ReplyDeadline datetime2 NULL,
        RequiredDate datetime2 NULL,
        BaseCurrency nvarchar(20) NOT NULL,
        Status nvarchar(30) NOT NULL CONSTRAINT DF_PoSourcingHdr_Status DEFAULT (N'DRAFT'),
        Remarks nvarchar(500) NULL,
        CancelReason nvarchar(500) NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(20) NOT NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PoSourcingHdr PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT AK_PoSourcingHdr_Tenant_Id UNIQUE (CompanyCode, BranchCode, UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoSourcingHdr_Tenant_SourcingNo' AND object_id = OBJECT_ID(N'dbo.PoSourcingHdr'))
    CREATE UNIQUE INDEX UX_PoSourcingHdr_Tenant_SourcingNo
        ON dbo.PoSourcingHdr (CompanyCode, BranchCode, SourcingNo);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoSourcingHdr_Tenant_Status_Date' AND object_id = OBJECT_ID(N'dbo.PoSourcingHdr'))
    CREATE INDEX IX_PoSourcingHdr_Tenant_Status_Date
        ON dbo.PoSourcingHdr (CompanyCode, BranchCode, Status, RequestDate DESC);
GO

IF OBJECT_ID(N'dbo.PoSourcingLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoSourcingLine
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        SourcingId bigint NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        [LineNo] smallint NOT NULL,
        ICode nvarchar(50) NULL,
        [Description] nvarchar(200) NOT NULL,
        Specification nvarchar(500) NULL,
        OneTimeItemYn bit NOT NULL CONSTRAINT DF_PoSourcingLine_OneTime DEFAULT (0),
        RequestedUom nvarchar(20) NOT NULL,
        StdUom nvarchar(20) NOT NULL,
        PackSz decimal(18,6) NOT NULL CONSTRAINT DF_PoSourcingLine_PackSz DEFAULT (1),
        RequestedQty decimal(18,4) NOT NULL,
        RequestedStdQty decimal(18,4) NOT NULL,
        RequiredDate datetime2 NULL,
        DeliveryWhse nvarchar(20) NULL,
        PrNo nvarchar(30) NULL,
        PrLineNo smallint NULL,
        PrOriginalVendorCd nvarchar(20) NULL,
        Notes nvarchar(500) NULL,
        CONSTRAINT PK_PoSourcingLine PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PoSourcingLine_Hdr FOREIGN KEY (CompanyCode, BranchCode, SourcingId)
            REFERENCES dbo.PoSourcingHdr (CompanyCode, BranchCode, UID),
        CONSTRAINT CK_PoSourcingLine_PositiveQty CHECK (RequestedQty > 0 AND RequestedStdQty > 0 AND PackSz > 0)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoSourcingLine_Sourcing_LineNo' AND object_id = OBJECT_ID(N'dbo.PoSourcingLine'))
    CREATE UNIQUE INDEX UX_PoSourcingLine_Sourcing_LineNo
        ON dbo.PoSourcingLine (SourcingId, [LineNo]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoSourcingLine_Tenant_PrLine' AND object_id = OBJECT_ID(N'dbo.PoSourcingLine'))
    CREATE INDEX IX_PoSourcingLine_Tenant_PrLine
        ON dbo.PoSourcingLine (CompanyCode, BranchCode, PrNo, PrLineNo);
GO

IF OBJECT_ID(N'dbo.PoSourcingSupplier', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoSourcingSupplier
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        SourcingId bigint NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        SuppCode nvarchar(20) NULL,
        SupplierName nvarchar(100) NOT NULL,
        ContactPerson nvarchar(100) NULL,
        ContactEmail nvarchar(100) NULL,
        ContactPhone nvarchar(50) NULL,
        DefaultCurrency nvarchar(20) NULL,
        ResponseStatus nvarchar(20) NOT NULL CONSTRAINT DF_PoSourcingSupplier_Status DEFAULT (N'INVITED'),
        Notes nvarchar(500) NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(20) NOT NULL,
        CONSTRAINT PK_PoSourcingSupplier PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PoSourcingSupplier_Hdr FOREIGN KEY (CompanyCode, BranchCode, SourcingId)
            REFERENCES dbo.PoSourcingHdr (CompanyCode, BranchCode, UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoSourcingSupplier_Sourcing_Name' AND object_id = OBJECT_ID(N'dbo.PoSourcingSupplier'))
    CREATE INDEX IX_PoSourcingSupplier_Sourcing_Name
        ON dbo.PoSourcingSupplier (SourcingId, SupplierName);
GO

IF OBJECT_ID(N'dbo.PoSourcingQuote', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoSourcingQuote
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        SourcingId bigint NOT NULL,
        SourcingSupplierId bigint NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        SupplierQuoteRef nvarchar(50) NOT NULL,
        QuoteRevNo smallint NOT NULL CONSTRAINT DF_PoSourcingQuote_Rev DEFAULT (1),
        QuoteDate datetime2 NOT NULL,
        ExpiryDate datetime2 NULL,
        Currency nvarchar(20) NOT NULL,
        ExchangeRateToBase decimal(18,8) NULL,
        ExchangeRateDate datetime2 NULL,
        PayTermCode nvarchar(20) NULL,
        BuyingTermCode nvarchar(20) NULL,
        QuoteChannel nvarchar(30) NULL,
        HeaderFreightAmount decimal(18,2) NOT NULL CONSTRAINT DF_PoSourcingQuote_Freight DEFAULT (0),
        HeaderOtherCharges decimal(18,2) NOT NULL CONSTRAINT DF_PoSourcingQuote_Charges DEFAULT (0),
        FreightAllocationMode nvarchar(20) NOT NULL CONSTRAINT DF_PoSourcingQuote_AllocMode DEFAULT (N'VALUE_PRO_RATA'),
        IsInclusive bit NOT NULL CONSTRAINT DF_PoSourcingQuote_Inclusive DEFAULT (0),
        Status nvarchar(20) NOT NULL CONSTRAINT DF_PoSourcingQuote_Status DEFAULT (N'RECEIVED'),
        BuyerNotes nvarchar(500) NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(20) NOT NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PoSourcingQuote PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT AK_PoSourcingQuote_Tenant_Id UNIQUE (CompanyCode, BranchCode, UID),
        CONSTRAINT FK_PoSourcingQuote_Hdr FOREIGN KEY (CompanyCode, BranchCode, SourcingId)
            REFERENCES dbo.PoSourcingHdr (CompanyCode, BranchCode, UID),
        CONSTRAINT FK_PoSourcingQuote_Supplier FOREIGN KEY (SourcingSupplierId)
            REFERENCES dbo.PoSourcingSupplier (UID),
        CONSTRAINT CK_PoSourcingQuote_Charges CHECK (HeaderFreightAmount >= 0 AND HeaderOtherCharges >= 0)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoSourcingQuote_Supplier_Rev' AND object_id = OBJECT_ID(N'dbo.PoSourcingQuote'))
    CREATE UNIQUE INDEX UX_PoSourcingQuote_Supplier_Rev
        ON dbo.PoSourcingQuote (SourcingId, SourcingSupplierId, QuoteRevNo);
GO

IF OBJECT_ID(N'dbo.PoSourcingQuoteLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoSourcingQuoteLine
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        QuoteId bigint NOT NULL,
        SourcingId bigint NOT NULL,
        SourcingLineId bigint NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        LineStatus nvarchar(20) NOT NULL CONSTRAINT DF_PoSourcingQuoteLine_Status DEFAULT (N'QUOTED'),
        VendorPartNo nvarchar(50) NULL,
        OfferedSpec nvarchar(500) NULL,
        QuotedUom nvarchar(20) NOT NULL,
        QuotedPackSz decimal(18,6) NOT NULL CONSTRAINT DF_PoSourcingQuoteLine_PackSz DEFAULT (1),
        QuotedQty decimal(18,4) NOT NULL CONSTRAINT DF_PoSourcingQuoteLine_Qty DEFAULT (0),
        QuotedStdQty decimal(18,4) NOT NULL CONSTRAINT DF_PoSourcingQuoteLine_StdQty DEFAULT (0),
        UnitPrice decimal(18,6) NOT NULL CONSTRAINT DF_PoSourcingQuoteLine_Price DEFAULT (0),
        DiscPer decimal(9,4) NOT NULL CONSTRAINT DF_PoSourcingQuoteLine_DiscPer DEFAULT (0),
        DiscAmt decimal(18,2) NOT NULL CONSTRAINT DF_PoSourcingQuoteLine_DiscAmt DEFAULT (0),
        TaxGroupCode nvarchar(20) NULL,
        TaxPer decimal(9,4) NOT NULL CONSTRAINT DF_PoSourcingQuoteLine_TaxPer DEFAULT (0),
        NonRecoverableTaxPer decimal(9,4) NOT NULL CONSTRAINT DF_PoSourcingQuoteLine_NonRecTax DEFAULT (0),
        LineCharges decimal(18,2) NOT NULL CONSTRAINT DF_PoSourcingQuoteLine_Charges DEFAULT (0),
        LeadTimeDays int NULL,
        EstimatedDeliveryDate datetime2 NULL,
        Moq decimal(18,4) NULL,
        WarrantyTerms nvarchar(200) NULL,
        ExceptionsNote nvarchar(500) NULL,
        IsComparable bit NOT NULL CONSTRAINT DF_PoSourcingQuoteLine_Comparable DEFAULT (0),
        NonComparableReason nvarchar(200) NULL,
        NormalizedBaseUnitCost decimal(18,6) NULL,
        NormalizedComparableLandedTotal decimal(18,2) NULL,
        NormalizedTaxInclusivePayableTotal decimal(18,2) NULL,
        CONSTRAINT PK_PoSourcingQuoteLine PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PoSourcingQuoteLine_Quote FOREIGN KEY (CompanyCode, BranchCode, QuoteId)
            REFERENCES dbo.PoSourcingQuote (CompanyCode, BranchCode, UID),
        CONSTRAINT FK_PoSourcingQuoteLine_Line FOREIGN KEY (SourcingLineId)
            REFERENCES dbo.PoSourcingLine (UID),
        CONSTRAINT CK_PoSourcingQuoteLine_NonNeg CHECK (QuotedQty >= 0 AND QuotedStdQty >= 0 AND UnitPrice >= 0 AND QuotedPackSz > 0)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoSourcingQuoteLine_Quote_SourcingLine' AND object_id = OBJECT_ID(N'dbo.PoSourcingQuoteLine'))
    CREATE UNIQUE INDEX UX_PoSourcingQuoteLine_Quote_SourcingLine
        ON dbo.PoSourcingQuoteLine (QuoteId, SourcingLineId);
GO

IF OBJECT_ID(N'dbo.PoSourcingAward', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoSourcingAward
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        SourcingId bigint NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        AwardVersion int NOT NULL CONSTRAINT DF_PoSourcingAward_Version DEFAULT (1),
        Status nvarchar(20) NOT NULL CONSTRAINT DF_PoSourcingAward_Status DEFAULT (N'PROPOSED'),
        OverallReason nvarchar(500) NULL,
        ExceptionReason nvarchar(500) NULL,
        ComparisonSnapshotJson nvarchar(max) NULL,
        ProposedBy nvarchar(20) NOT NULL,
        ProposedAtUtc datetime2 NOT NULL,
        DecidedBy nvarchar(20) NULL,
        DecidedAtUtc datetime2 NULL,
        DecisionReason nvarchar(500) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PoSourcingAward PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT AK_PoSourcingAward_Tenant_Id UNIQUE (CompanyCode, BranchCode, UID),
        CONSTRAINT FK_PoSourcingAward_Hdr FOREIGN KEY (CompanyCode, BranchCode, SourcingId)
            REFERENCES dbo.PoSourcingHdr (CompanyCode, BranchCode, UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoSourcingAward_Sourcing_Version' AND object_id = OBJECT_ID(N'dbo.PoSourcingAward'))
    CREATE UNIQUE INDEX UX_PoSourcingAward_Sourcing_Version
        ON dbo.PoSourcingAward (SourcingId, AwardVersion);
GO

IF OBJECT_ID(N'dbo.PoSourcingAwardLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoSourcingAwardLine
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        AwardId bigint NOT NULL,
        SourcingId bigint NOT NULL,
        SourcingLineId bigint NOT NULL,
        QuoteId bigint NOT NULL,
        QuoteLineId bigint NOT NULL,
        SourcingSupplierId bigint NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        SuppCodeSnapshot nvarchar(20) NULL,
        SupplierNameSnapshot nvarchar(100) NOT NULL,
        ICodeSnapshot nvarchar(50) NULL,
        OneTimeItemYnSnapshot bit NOT NULL CONSTRAINT DF_PoSourcingAwardLine_OneTime DEFAULT (0),
        CurrencySnapshot nvarchar(20) NOT NULL,
        ExchangeRateSnapshot decimal(18,8) NOT NULL CONSTRAINT DF_PoSourcingAwardLine_Fx DEFAULT (1),
        IsInclusiveSnapshot bit NOT NULL CONSTRAINT DF_PoSourcingAwardLine_Incl DEFAULT (0),
        AwardedUom nvarchar(20) NOT NULL,
        StdUomSnapshot nvarchar(20) NOT NULL,
        PackSzSnapshot decimal(18,6) NOT NULL CONSTRAINT DF_PoSourcingAwardLine_PackSz DEFAULT (1),
        AwardedQty decimal(18,4) NOT NULL,
        AwardedStdQty decimal(18,4) NOT NULL,
        AwardedUnitPrice decimal(18,6) NOT NULL,
        DiscPerSnapshot decimal(9,4) NOT NULL CONSTRAINT DF_PoSourcingAwardLine_DiscPer DEFAULT (0),
        TaxGroupSnapshot nvarchar(20) NULL,
        TaxPerSnapshot decimal(9,4) NOT NULL CONSTRAINT DF_PoSourcingAwardLine_TaxPer DEFAULT (0),
        ComparableBaseUnitCostSnapshot decimal(18,6) NOT NULL CONSTRAINT DF_PoSourcingAwardLine_BaseCost DEFAULT (0),
        ComparableTotalSnapshot decimal(18,2) NOT NULL CONSTRAINT DF_PoSourcingAwardLine_Total DEFAULT (0),
        IsLowestComparableOffer bit NOT NULL CONSTRAINT DF_PoSourcingAwardLine_Lowest DEFAULT (0),
        SelectionReason nvarchar(500) NULL,
        IsQuarantined bit NOT NULL CONSTRAINT DF_PoSourcingAwardLine_Quarantined DEFAULT (0),
        QuarantineReason nvarchar(500) NULL,
        CONSTRAINT PK_PoSourcingAwardLine PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PoSourcingAwardLine_Award FOREIGN KEY (CompanyCode, BranchCode, AwardId)
            REFERENCES dbo.PoSourcingAward (CompanyCode, BranchCode, UID),
        CONSTRAINT FK_PoSourcingAwardLine_Line FOREIGN KEY (SourcingLineId)
            REFERENCES dbo.PoSourcingLine (UID),
        CONSTRAINT CK_PoSourcingAwardLine_Positive CHECK (AwardedQty > 0 AND AwardedStdQty > 0 AND AwardedUnitPrice >= 0 AND PackSzSnapshot > 0)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoSourcingAwardLine_Award_Line_Quote' AND object_id = OBJECT_ID(N'dbo.PoSourcingAwardLine'))
    CREATE UNIQUE INDEX UX_PoSourcingAwardLine_Award_Line_Quote
        ON dbo.PoSourcingAwardLine (AwardId, SourcingLineId, QuoteLineId);
GO

IF OBJECT_ID(N'dbo.PoSourcingConversion', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoSourcingConversion
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        SourcingId bigint NOT NULL,
        AwardId bigint NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        RequestId nvarchar(80) NOT NULL,
        PayloadHash nvarchar(128) NOT NULL,
        TargetDocType nvarchar(10) NOT NULL,
        TargetDocNo nvarchar(30) NOT NULL,
        TargetDocRelNo smallint NOT NULL CONSTRAINT DF_PoSourcingConversion_Rel DEFAULT (0),
        TargetSupplierCode nvarchar(20) NULL,
        TargetCurrency nvarchar(20) NOT NULL,
        TargetIsInclusive bit NOT NULL CONSTRAINT DF_PoSourcingConversion_Incl DEFAULT (0),
        TargetOneTimeItemYn bit NOT NULL CONSTRAINT DF_PoSourcingConversion_OneTime DEFAULT (0),
        Status nvarchar(30) NOT NULL CONSTRAINT DF_PoSourcingConversion_Status DEFAULT (N'ACTIVE'),
        CreatedAtUtc datetime2 NOT NULL,
        CreatedBy nvarchar(20) NOT NULL,
        LastReconciledAtUtc datetime2 NULL,
        ReconciliationNote nvarchar(500) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_PoSourcingConversion PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT AK_PoSourcingConversion_Tenant_Id UNIQUE (CompanyCode, BranchCode, UID),
        CONSTRAINT FK_PoSourcingConversion_Hdr FOREIGN KEY (CompanyCode, BranchCode, SourcingId)
            REFERENCES dbo.PoSourcingHdr (CompanyCode, BranchCode, UID),
        CONSTRAINT FK_PoSourcingConversion_Award FOREIGN KEY (AwardId)
            REFERENCES dbo.PoSourcingAward (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_PoSourcingConversion_Tenant_RequestId' AND object_id = OBJECT_ID(N'dbo.PoSourcingConversion'))
    CREATE UNIQUE INDEX UX_PoSourcingConversion_Tenant_RequestId
        ON dbo.PoSourcingConversion (CompanyCode, BranchCode, RequestId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoSourcingConversion_Tenant_TargetDoc' AND object_id = OBJECT_ID(N'dbo.PoSourcingConversion'))
    CREATE INDEX IX_PoSourcingConversion_Tenant_TargetDoc
        ON dbo.PoSourcingConversion (CompanyCode, BranchCode, TargetDocType, TargetDocNo, TargetDocRelNo);
GO

IF OBJECT_ID(N'dbo.PoSourcingConversionLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoSourcingConversionLine
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        ConversionId bigint NOT NULL,
        SourcingId bigint NOT NULL,
        SourcingLineId bigint NOT NULL,
        AwardId bigint NOT NULL,
        AwardLineId bigint NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        TargetDocType nvarchar(10) NOT NULL,
        TargetDocNo nvarchar(30) NOT NULL,
        TargetDocRelNo smallint NOT NULL CONSTRAINT DF_PoSourcingConversionLine_Rel DEFAULT (0),
        TargetLineNo smallint NOT NULL,
        OriginalTargetLineNo smallint NOT NULL,
        TargetUom nvarchar(20) NOT NULL,
        TargetPackSz decimal(18,6) NOT NULL CONSTRAINT DF_PoSourcingConversionLine_PackSz DEFAULT (1),
        ConvertedPurQty decimal(18,4) NOT NULL,
        ConvertedStdQty decimal(18,4) NOT NULL,
        EffectiveActiveStdQty decimal(18,4) NOT NULL,
        TargetUnitPrice decimal(18,6) NOT NULL,
        SourcePrNo nvarchar(30) NULL,
        SourcePrLineNo smallint NULL,
        LineLinkStatus nvarchar(30) NOT NULL CONSTRAINT DF_PoSourcingConversionLine_Status DEFAULT (N'ACTIVE'),
        CONSTRAINT PK_PoSourcingConversionLine PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PoSourcingConversionLine_Conv FOREIGN KEY (CompanyCode, BranchCode, ConversionId)
            REFERENCES dbo.PoSourcingConversion (CompanyCode, BranchCode, UID),
        CONSTRAINT FK_PoSourcingConversionLine_AwardLine FOREIGN KEY (AwardLineId)
            REFERENCES dbo.PoSourcingAwardLine (UID),
        CONSTRAINT CK_PoSourcingConversionLine_Qty CHECK (ConvertedPurQty > 0 AND ConvertedStdQty > 0 AND EffectiveActiveStdQty >= 0)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoSourcingConversionLine_Tenant_TargetLine' AND object_id = OBJECT_ID(N'dbo.PoSourcingConversionLine'))
    CREATE INDEX IX_PoSourcingConversionLine_Tenant_TargetLine
        ON dbo.PoSourcingConversionLine (CompanyCode, BranchCode, TargetDocType, TargetDocNo, TargetDocRelNo, TargetLineNo);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoSourcingConversionLine_Tenant_AwardLine' AND object_id = OBJECT_ID(N'dbo.PoSourcingConversionLine'))
    CREATE INDEX IX_PoSourcingConversionLine_Tenant_AwardLine
        ON dbo.PoSourcingConversionLine (CompanyCode, BranchCode, AwardLineId);
GO

IF OBJECT_ID(N'dbo.PoSourcingAttachment', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoSourcingAttachment
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        SourcingId bigint NOT NULL,
        QuoteId bigint NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        ScopeKind nvarchar(20) NOT NULL CONSTRAINT DF_PoSourcingAttachment_Scope DEFAULT (N'RFQ'),
        OriginalFileName nvarchar(255) NOT NULL,
        StoredRelativePath nvarchar(500) NOT NULL,
        ContentType nvarchar(100) NOT NULL,
        FileSizeBytes bigint NOT NULL,
        ContainsSensitivePricing bit NOT NULL CONSTRAINT DF_PoSourcingAttachment_Sensitive DEFAULT (1),
        UploadedAtUtc datetime2 NOT NULL,
        UploadedBy nvarchar(20) NOT NULL,
        CONSTRAINT PK_PoSourcingAttachment PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PoSourcingAttachment_Hdr FOREIGN KEY (CompanyCode, BranchCode, SourcingId)
            REFERENCES dbo.PoSourcingHdr (CompanyCode, BranchCode, UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoSourcingAttachment_Tenant_Sourcing_Quote' AND object_id = OBJECT_ID(N'dbo.PoSourcingAttachment'))
    CREATE INDEX IX_PoSourcingAttachment_Tenant_Sourcing_Quote
        ON dbo.PoSourcingAttachment (CompanyCode, BranchCode, SourcingId, QuoteId);
GO

IF OBJECT_ID(N'dbo.PoSourcingEvent', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoSourcingEvent
    (
        UID bigint IDENTITY(1,1) NOT NULL,
        SourcingId bigint NOT NULL,
        QuoteId bigint NULL,
        AwardId bigint NULL,
        ConversionId bigint NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        EventType nvarchar(40) NOT NULL,
        PriorStatus nvarchar(30) NULL,
        NewStatus nvarchar(30) NULL,
        Reason nvarchar(500) NULL,
        SnapshotHash nvarchar(128) NULL,
        ActorUserId nvarchar(20) NOT NULL,
        OccurredAtUtc datetime2 NOT NULL,
        CONSTRAINT PK_PoSourcingEvent PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_PoSourcingEvent_Hdr FOREIGN KEY (CompanyCode, BranchCode, SourcingId)
            REFERENCES dbo.PoSourcingHdr (CompanyCode, BranchCode, UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PoSourcingEvent_Tenant_Sourcing_Date' AND object_id = OBJECT_ID(N'dbo.PoSourcingEvent'))
    CREATE INDEX IX_PoSourcingEvent_Tenant_Sourcing_Date
        ON dbo.PoSourcingEvent (CompanyCode, BranchCode, SourcingId, OccurredAtUtc);
GO
