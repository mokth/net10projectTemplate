/* =============================================================================
   Subcontracting Phase G3 Hardening (M-01 Supplier-to-Custody-Location Map &
   20-char WarehouseCode alignment with IvWarehouse.WarehouseCode).
   Idempotent, DBA-run, non-destructive. Safe to execute repeatedly.
   ============================================================================= */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

/* 1. Widen Subcon warehouse code columns from nvarchar(10) -> nvarchar(20) to match IvWarehouse.WarehouseCode */
IF COL_LENGTH(N'dbo.SubconPolicy', N'DefaultCustodyWarehouseCode') IS NOT NULL
   AND COL_LENGTH(N'dbo.SubconPolicy', N'DefaultCustodyWarehouseCode') < 40
    ALTER TABLE dbo.SubconPolicy ALTER COLUMN DefaultCustodyWarehouseCode nvarchar(20) NULL;
GO

IF COL_LENGTH(N'dbo.SubconDispatchLine', N'CustodyWarehouseCode') IS NOT NULL
   AND COL_LENGTH(N'dbo.SubconDispatchLine', N'CustodyWarehouseCode') < 40
    ALTER TABLE dbo.SubconDispatchLine ALTER COLUMN CustodyWarehouseCode nvarchar(20) NULL;
GO

IF COL_LENGTH(N'dbo.SubconReceiptLine', N'ToWarehouseCode') IS NOT NULL
   AND COL_LENGTH(N'dbo.SubconReceiptLine', N'ToWarehouseCode') < 40
    ALTER TABLE dbo.SubconReceiptLine ALTER COLUMN ToWarehouseCode nvarchar(20) NULL;
GO

IF COL_LENGTH(N'dbo.SubconCustodyAllocation', N'CustodyWarehouseCode') IS NOT NULL
   AND COL_LENGTH(N'dbo.SubconCustodyAllocation', N'CustodyWarehouseCode') < 40
    ALTER TABLE dbo.SubconCustodyAllocation ALTER COLUMN CustodyWarehouseCode nvarchar(20) NOT NULL;
GO

/* 2. SubconSupplierCustodyMap: explicit 20-char SupplierCode -> (20-char SUBCON WH, 10-char IvLocation.LocCode) */
IF OBJECT_ID(N'dbo.SubconSupplierCustodyMap', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubconSupplierCustodyMap
    (
        UID                  bigint IDENTITY(1,1) NOT NULL,
        CompanyCode          nvarchar(5)          NOT NULL,
        BranchCode           nvarchar(5)          NOT NULL,
        SupplierCode         nvarchar(20)         NOT NULL,
        CustodyWarehouseCode nvarchar(20)         NOT NULL,
        CustodyLocationCode  nvarchar(10)         NOT NULL,
        IsActive             bit                  NOT NULL CONSTRAINT DF_SubconSupplierCustodyMap_IsActive DEFAULT (1),
        CreatedDate          datetime2            NOT NULL,
        CreatedBy            nvarchar(20)         NOT NULL,
        ModifiedDate         datetime2            NULL,
        ModifiedBy           nvarchar(20)         NULL,
        RowVersion           rowversion           NOT NULL,
        CONSTRAINT PK_SubconSupplierCustodyMap PRIMARY KEY CLUSTERED (UID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubconSupplierCustodyMap_Supplier' AND object_id = OBJECT_ID(N'dbo.SubconSupplierCustodyMap'))
    CREATE UNIQUE INDEX UX_SubconSupplierCustodyMap_Supplier
        ON dbo.SubconSupplierCustodyMap (CompanyCode, BranchCode, SupplierCode);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SubconSupplierCustodyMap_ActiveBin' AND object_id = OBJECT_ID(N'dbo.SubconSupplierCustodyMap'))
    CREATE UNIQUE INDEX UX_SubconSupplierCustodyMap_ActiveBin
        ON dbo.SubconSupplierCustodyMap (CompanyCode, BranchCode, CustodyWarehouseCode, CustodyLocationCode)
        WHERE IsActive = 1;
GO
