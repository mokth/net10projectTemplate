/* =============================================================================
   Subcontracting Phase G1–G3 Additive Schema (Custody Isolation & Atomic TR Posting)
   Preserves all Phase-1 & Phase-G2 Subcon tables.
   Idempotent, DBA-run, non-destructive. No startup migration.
   All live capability flags remain OFF (0) by default.
   ============================================================================= */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

/* 1. SubconPolicy: granular per-capability flags & default custody warehouse */
IF COL_LENGTH(N'dbo.SubconPolicy', N'InventoryCustodyPostEnabled') IS NULL
    ALTER TABLE dbo.SubconPolicy ADD InventoryCustodyPostEnabled bit NOT NULL
        CONSTRAINT DF_SubconPolicy_InventoryCustodyPostEnabled DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SubconPolicy', N'LotReturnEnabled') IS NULL
    ALTER TABLE dbo.SubconPolicy ADD LotReturnEnabled bit NOT NULL
        CONSTRAINT DF_SubconPolicy_LotReturnEnabled DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SubconPolicy', N'BomConversionEnabled') IS NULL
    ALTER TABLE dbo.SubconPolicy ADD BomConversionEnabled bit NOT NULL
        CONSTRAINT DF_SubconPolicy_BomConversionEnabled DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SubconPolicy', N'ProductionWipEnabled') IS NULL
    ALTER TABLE dbo.SubconPolicy ADD ProductionWipEnabled bit NOT NULL
        CONSTRAINT DF_SubconPolicy_ProductionWipEnabled DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SubconPolicy', N'FeeCapitalizationEnabled') IS NULL
    ALTER TABLE dbo.SubconPolicy ADD FeeCapitalizationEnabled bit NOT NULL
        CONSTRAINT DF_SubconPolicy_FeeCapitalizationEnabled DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SubconPolicy', N'DefaultCustodyWarehouseCode') IS NULL
    ALTER TABLE dbo.SubconPolicy ADD DefaultCustodyWarehouseCode nvarchar(10) NULL;
GO

/* 2. SubconDispatch: Job link, revision, and posting/rollback audit fields */
IF COL_LENGTH(N'dbo.SubconDispatch', N'JobId') IS NULL
    ALTER TABLE dbo.SubconDispatch ADD JobId bigint NULL;
GO
IF OBJECT_ID(N'dbo.SubconJob', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SubconDispatch_Job')
    ALTER TABLE dbo.SubconDispatch
        ADD CONSTRAINT FK_SubconDispatch_Job FOREIGN KEY (JobId) REFERENCES dbo.SubconJob (UID);
GO
IF COL_LENGTH(N'dbo.SubconDispatch', N'DocumentRevision') IS NULL
    ALTER TABLE dbo.SubconDispatch ADD DocumentRevision int NOT NULL
        CONSTRAINT DF_SubconDispatch_DocumentRevision DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SubconDispatch', N'PostedDate') IS NULL
    ALTER TABLE dbo.SubconDispatch ADD PostedDate datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.SubconDispatch', N'PostedBy') IS NULL
    ALTER TABLE dbo.SubconDispatch ADD PostedBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SubconDispatch', N'PostedCount') IS NULL
    ALTER TABLE dbo.SubconDispatch ADD PostedCount int NOT NULL
        CONSTRAINT DF_SubconDispatch_PostedCount DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SubconDispatch', N'RollbackDate') IS NULL
    ALTER TABLE dbo.SubconDispatch ADD RollbackDate datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.SubconDispatch', N'RollbackBy') IS NULL
    ALTER TABLE dbo.SubconDispatch ADD RollbackBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SubconDispatch', N'RollbackCount') IS NULL
    ALTER TABLE dbo.SubconDispatch ADD RollbackCount int NOT NULL
        CONSTRAINT DF_SubconDispatch_RollbackCount DEFAULT (0);
GO

/* 3. SubconDispatchLine: canonical base qty, stock status, custody destination */
IF COL_LENGTH(N'dbo.SubconDispatchLine', N'BaseQty') IS NULL
    ALTER TABLE dbo.SubconDispatchLine ADD BaseQty decimal(18,4) NULL;
GO
IF COL_LENGTH(N'dbo.SubconDispatchLine', N'ConvFactor') IS NULL
    ALTER TABLE dbo.SubconDispatchLine ADD ConvFactor decimal(18,6) NULL;
GO
IF COL_LENGTH(N'dbo.SubconDispatchLine', N'StockStatus') IS NULL
    ALTER TABLE dbo.SubconDispatchLine ADD StockStatus nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SubconDispatchLine', N'CustodyWarehouseCode') IS NULL
    ALTER TABLE dbo.SubconDispatchLine ADD CustodyWarehouseCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SubconDispatchLine', N'CustodyLocationCode') IS NULL
    ALTER TABLE dbo.SubconDispatchLine ADD CustodyLocationCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SubconDispatchLine', N'CustodyBalLocId') IS NULL
    ALTER TABLE dbo.SubconDispatchLine ADD CustodyBalLocId int NULL;
GO

/* 4. SubconReceipt: revision & posting/rollback audit fields */
IF COL_LENGTH(N'dbo.SubconReceipt', N'DocumentRevision') IS NULL
    ALTER TABLE dbo.SubconReceipt ADD DocumentRevision int NOT NULL
        CONSTRAINT DF_SubconReceipt_DocumentRevision DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SubconReceipt', N'PostedDate') IS NULL
    ALTER TABLE dbo.SubconReceipt ADD PostedDate datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.SubconReceipt', N'PostedBy') IS NULL
    ALTER TABLE dbo.SubconReceipt ADD PostedBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SubconReceipt', N'PostedCount') IS NULL
    ALTER TABLE dbo.SubconReceipt ADD PostedCount int NOT NULL
        CONSTRAINT DF_SubconReceipt_PostedCount DEFAULT (0);
GO
IF COL_LENGTH(N'dbo.SubconReceipt', N'RollbackDate') IS NULL
    ALTER TABLE dbo.SubconReceipt ADD RollbackDate datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.SubconReceipt', N'RollbackBy') IS NULL
    ALTER TABLE dbo.SubconReceipt ADD RollbackBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SubconReceipt', N'RollbackCount') IS NULL
    ALTER TABLE dbo.SubconReceipt ADD RollbackCount int NOT NULL
        CONSTRAINT DF_SubconReceipt_RollbackCount DEFAULT (0);
GO

/* 5. SubconReceiptLine: canonical base qty & receiving warehouse/location */
IF COL_LENGTH(N'dbo.SubconReceiptLine', N'BaseQty') IS NULL
    ALTER TABLE dbo.SubconReceiptLine ADD BaseQty decimal(18,4) NULL;
GO
IF COL_LENGTH(N'dbo.SubconReceiptLine', N'ConvFactor') IS NULL
    ALTER TABLE dbo.SubconReceiptLine ADD ConvFactor decimal(18,6) NULL;
GO
IF COL_LENGTH(N'dbo.SubconReceiptLine', N'ToWarehouseCode') IS NULL
    ALTER TABLE dbo.SubconReceiptLine ADD ToWarehouseCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SubconReceiptLine', N'ToLocationCode') IS NULL
    ALTER TABLE dbo.SubconReceiptLine ADD ToLocationCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SubconReceiptLine', N'ToBalLocId') IS NULL
    ALTER TABLE dbo.SubconReceiptLine ADD ToBalLocId int NULL;
GO

/* 6. SubconPostingLink: immutable audit & active generation link to Inventory TR / StockPosting */
IF OBJECT_ID(N'dbo.SubconPostingLink', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubconPostingLink
    (
        UID              bigint IDENTITY(1,1) NOT NULL,
        CompanyCode      nvarchar(5)          NOT NULL,
        BranchCode       nvarchar(5)          NOT NULL,
        DocKind          nvarchar(20)         NOT NULL,
        DocId            bigint               NOT NULL,
        DocNo            nvarchar(20)         NOT NULL,
        JobId            bigint               NULL,
        DocumentRevision int                  NOT NULL,
        PostingRole      nvarchar(20)         NOT NULL,
        TrxType          nvarchar(10)         NOT NULL,
        TrxBatchId       int                  NOT NULL,
        TrxBatchNo       int                  NOT NULL,
        StockPostingId   bigint               NULL,
        OperationId      uniqueidentifier     NOT NULL,
        RequestId        uniqueidentifier     NOT NULL,
        EffectiveAt      datetime2            NOT NULL,
        IsActive         bit                  NOT NULL CONSTRAINT DF_SubconPostingLink_IsActive DEFAULT (1),
        ReversedByLinkId bigint               NULL,
        CreatedDate      datetime2            NOT NULL,
        CreatedBy        nvarchar(20)         NOT NULL,
        RowVersion       rowversion           NOT NULL,
        CONSTRAINT PK_SubconPostingLink PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT CK_SubconPostingLink_DocKind CHECK (DocKind IN (N'DISPATCH', N'RECEIPT')),
        CONSTRAINT CK_SubconPostingLink_Role CHECK (PostingRole IN (N'PRIMARY', N'REVERSAL'))
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubconPostingLink_ActiveDoc' AND object_id = OBJECT_ID(N'dbo.SubconPostingLink'))
    CREATE UNIQUE INDEX UX_SubconPostingLink_ActiveDoc
        ON dbo.SubconPostingLink (CompanyCode, BranchCode, DocKind, DocId)
        WHERE IsActive = 1 AND PostingRole = N'PRIMARY';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubconPostingLink_Batch_Role_Rev' AND object_id = OBJECT_ID(N'dbo.SubconPostingLink'))
    CREATE UNIQUE INDEX UX_SubconPostingLink_Batch_Role_Rev
        ON dbo.SubconPostingLink (CompanyCode, BranchCode, TrxBatchId, DocumentRevision, PostingRole);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SubconPostingLink_BatchLookup' AND object_id = OBJECT_ID(N'dbo.SubconPostingLink'))
    CREATE INDEX IX_SubconPostingLink_BatchLookup
        ON dbo.SubconPostingLink (CompanyCode, BranchCode, TrxBatchId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubconPostingLink_RequestId' AND object_id = OBJECT_ID(N'dbo.SubconPostingLink'))
    CREATE UNIQUE INDEX UX_SubconPostingLink_RequestId
        ON dbo.SubconPostingLink (CompanyCode, BranchCode, DocKind, PostingRole, RequestId);
GO

/* 7. SubconCustodyAllocation: per-dispatch-line custody balance tracking (reconciles to IvBalLoc) */
IF OBJECT_ID(N'dbo.SubconCustodyAllocation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubconCustodyAllocation
    (
        UID                  bigint IDENTITY(1,1) NOT NULL,
        CompanyCode          nvarchar(5)          NOT NULL,
        BranchCode           nvarchar(5)          NOT NULL,
        JobId                bigint               NULL,
        DispatchId           bigint               NOT NULL,
        DispatchLineId       bigint               NOT NULL,
        SupplierCode         nvarchar(20)         NOT NULL,
        ItemCode             nvarchar(30)         NOT NULL,
        BaseUom              nvarchar(10)         NOT NULL,
        StockStatus          nvarchar(10)         NOT NULL,
        CustodyBalLocId      int                  NOT NULL,
        CustodyWarehouseCode nvarchar(10)         NOT NULL,
        CustodyLocationCode  nvarchar(10)         NOT NULL,
        DispatchedBaseQty    decimal(18,4)        NOT NULL,
        ReturnedBaseQty      decimal(18,4)        NOT NULL CONSTRAINT DF_SubconCustodyAlloc_Returned DEFAULT (0),
        ConsumedBaseQty      decimal(18,4)        NOT NULL CONSTRAINT DF_SubconCustodyAlloc_Consumed DEFAULT (0),
        ScrapBaseQty         decimal(18,4)        NOT NULL CONSTRAINT DF_SubconCustodyAlloc_Scrap DEFAULT (0),
        IsActive             bit                  NOT NULL CONSTRAINT DF_SubconCustodyAlloc_IsActive DEFAULT (1),
        PostingLinkId        bigint               NOT NULL,
        CreatedDate          datetime2            NOT NULL,
        CreatedBy            nvarchar(20)         NOT NULL,
        ModifiedDate         datetime2            NULL,
        ModifiedBy           nvarchar(20)         NULL,
        RowVersion           rowversion           NOT NULL,
        CONSTRAINT PK_SubconCustodyAllocation PRIMARY KEY CLUSTERED (UID),
        CONSTRAINT FK_SubconCustodyAlloc_Dispatch FOREIGN KEY (DispatchId)
            REFERENCES dbo.SubconDispatch (UID),
        CONSTRAINT FK_SubconCustodyAlloc_DispatchLine FOREIGN KEY (DispatchLineId)
            REFERENCES dbo.SubconDispatchLine (UID),
        CONSTRAINT FK_SubconCustodyAlloc_PostingLink FOREIGN KEY (PostingLinkId)
            REFERENCES dbo.SubconPostingLink (UID),
        CONSTRAINT CK_SubconCustodyAlloc_Quantities CHECK (
            DispatchedBaseQty > 0
            AND ReturnedBaseQty >= 0
            AND ConsumedBaseQty >= 0
            AND ScrapBaseQty >= 0
            AND (ReturnedBaseQty + ConsumedBaseQty + ScrapBaseQty) <= DispatchedBaseQty
        )
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubconCustodyAllocation_ActiveDispatchLine' AND object_id = OBJECT_ID(N'dbo.SubconCustodyAllocation'))
    CREATE UNIQUE INDEX UX_SubconCustodyAllocation_ActiveDispatchLine
        ON dbo.SubconCustodyAllocation (DispatchLineId)
        WHERE IsActive = 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SubconCustodyAllocation_BalLoc' AND object_id = OBJECT_ID(N'dbo.SubconCustodyAllocation'))
    CREATE INDEX IX_SubconCustodyAllocation_BalLoc
        ON dbo.SubconCustodyAllocation (CompanyCode, BranchCode, CustodyBalLocId, IsActive);
GO
