/* Additive, idempotent DBA-run schema for immutable sales-price change history.
   No startup migration. Safe to execute repeatedly. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.SaPriceChangeBatch', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaPriceChangeBatch
    (
        PriceChangeBatchId bigint IDENTITY(1,1) NOT NULL,
        CompanyCode nvarchar(5) NOT NULL,
        Origin nvarchar(40) NOT NULL,
        TargetType nvarchar(30) NOT NULL,
        AdjustmentMethod nvarchar(30) NULL,
        AdjustmentValue decimal(18,4) NULL,
        RoundingMode nvarchar(20) NULL,
        DecimalPlaces int NULL,
        EffectiveDate date NOT NULL,
        Reason nvarchar(200) NULL,
        ItemSearchFilter nvarchar(100) NULL,
        ItemTypeFilter nvarchar(20) NULL,
        ItemClassFilter nvarchar(20) NULL,
        ItemSubClassFilter nvarchar(20) NULL,
        BrandFilter nvarchar(50) NULL,
        CustCodeFilter nvarchar(20) NULL,
        CustTypeFilter nvarchar(20) NULL,
        CustGroupFilter nvarchar(20) NULL,
        CustPriceCodeFilter nvarchar(20) NULL,
        CurrencyFilter nvarchar(5) NULL,
        UomFilter nvarchar(10) NULL,
        ChangedRowCount int NOT NULL,
        ChangedAtUtc datetime2 NOT NULL,
        ChangedBy nvarchar(10) NOT NULL,
        CONSTRAINT PK_SaPriceChangeBatch PRIMARY KEY CLUSTERED (PriceChangeBatchId)
    );
END
GO

IF OBJECT_ID(N'dbo.SaPriceChangeLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaPriceChangeLine
    (
        PriceChangeLineId bigint IDENTITY(1,1) NOT NULL,
        PriceChangeBatchId bigint NOT NULL,
        ChangeKind nvarchar(20) NOT NULL,
        ItemCode nvarchar(30) NOT NULL,
        ItemDescriptionSnapshot nvarchar(200) NULL,
        ItemTypeSnapshot nvarchar(20) NULL,
        ItemClassSnapshot nvarchar(20) NULL,
        ItemSubClassSnapshot nvarchar(20) NULL,
        BrandSnapshot nvarchar(50) NULL,
        CustCode nvarchar(20) NULL,
        CustomerNameSnapshot nvarchar(200) NULL,
        CustomerTypeSnapshot nvarchar(20) NULL,
        CustomerGroupSnapshot nvarchar(20) NULL,
        CustPriceCode nvarchar(20) NULL,
        PriceListDescriptionSnapshot nvarchar(50) NULL,
        SourcePriceListLineId int NULL,
        Moq int NULL,
        OldUom nvarchar(10) NULL,
        NewUom nvarchar(10) NULL,
        OldCurrencyCode nvarchar(5) NULL,
        NewCurrencyCode nvarchar(5) NULL,
        OldMinQty decimal(18,4) NULL,
        OldMaxQty decimal(18,4) NULL,
        NewMinQty decimal(18,4) NULL,
        NewMaxQty decimal(18,4) NULL,
        OldValidFrom date NULL,
        OldValidTo date NULL,
        NewValidFrom date NULL,
        NewValidTo date NULL,
        OldPrice decimal(18,4) NULL,
        NewPrice decimal(18,4) NULL,
        CONSTRAINT PK_SaPriceChangeLine PRIMARY KEY CLUSTERED (PriceChangeLineId),
        CONSTRAINT FK_SaPriceChangeLine_Batch
            FOREIGN KEY (PriceChangeBatchId) REFERENCES dbo.SaPriceChangeBatch(PriceChangeBatchId)
            ON DELETE NO ACTION
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaPriceChangeBatch')
               AND name = N'IX_SaPriceChangeBatch_Company_ChangedAtUtc')
    CREATE NONCLUSTERED INDEX IX_SaPriceChangeBatch_Company_ChangedAtUtc
        ON dbo.SaPriceChangeBatch (CompanyCode, ChangedAtUtc DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaPriceChangeBatch')
               AND name = N'IX_SaPriceChangeBatch_Company_Target_ChangedAtUtc')
    CREATE NONCLUSTERED INDEX IX_SaPriceChangeBatch_Company_Target_ChangedAtUtc
        ON dbo.SaPriceChangeBatch (CompanyCode, TargetType, ChangedAtUtc DESC);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaPriceChangeBatch')
               AND name = N'IX_SaPriceChangeBatch_Company_EffectiveDate')
    CREATE NONCLUSTERED INDEX IX_SaPriceChangeBatch_Company_EffectiveDate
        ON dbo.SaPriceChangeBatch (CompanyCode, EffectiveDate);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaPriceChangeLine')
               AND name = N'IX_SaPriceChangeLine_Batch')
    CREATE NONCLUSTERED INDEX IX_SaPriceChangeLine_Batch
        ON dbo.SaPriceChangeLine (PriceChangeBatchId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaPriceChangeLine')
               AND name = N'IX_SaPriceChangeLine_Item_Batch')
    CREATE NONCLUSTERED INDEX IX_SaPriceChangeLine_Item_Batch
        ON dbo.SaPriceChangeLine (ItemCode, PriceChangeBatchId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaPriceChangeLine')
               AND name = N'IX_SaPriceChangeLine_Customer_Batch')
    CREATE NONCLUSTERED INDEX IX_SaPriceChangeLine_Customer_Batch
        ON dbo.SaPriceChangeLine (CustCode, PriceChangeBatchId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaPriceChangeLine')
               AND name = N'IX_SaPriceChangeLine_PriceList_Batch')
    CREATE NONCLUSTERED INDEX IX_SaPriceChangeLine_PriceList_Batch
        ON dbo.SaPriceChangeLine (CustPriceCode, PriceChangeBatchId);
GO
