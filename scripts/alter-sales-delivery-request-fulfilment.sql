-- Delivery Request fulfilment-control upgrade.
-- Rerunnable SQL Server deployment. This script adds lineage/reservation metadata only;
-- it never backfills historical links, changes physical stock, or changes posting facts.
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.SaDeliveryRequest', N'U') IS NULL
   OR OBJECT_ID(N'dbo.SaDeliveryRequestSource', N'U') IS NULL
   OR OBJECT_ID(N'dbo.SaDODetail', N'U') IS NULL
   OR OBJECT_ID(N'dbo.POPRDtl', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrWorkOrderMaterial', N'U') IS NULL
   OR OBJECT_ID(N'dbo.IvBalLoc', N'U') IS NULL
    THROW 51000, 'Delivery Request fulfilment upgrade requires the current Sales, Inventory, Production and Purchase tables.', 1;
GO

IF OBJECT_ID(N'dbo.SaDeliveryRequestStockReservation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDeliveryRequestStockReservation
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SaDeliveryRequestStockReservation PRIMARY KEY,
        DeliveryRequestID bigint NOT NULL,
        DeliveryRequestSourceID bigint NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        BalLocID int NOT NULL,
        ReservedQty decimal(18,4) NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_DrStockReservation_IsActive DEFAULT (1),
        ReleasedDate datetime2 NULL,
        ReleasedBy nvarchar(20) NULL,
        ReleaseReason nvarchar(500) NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_DrStockReservation_ReservedQty CHECK (ReservedQty > 0)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_DrStockReservation_Request')
BEGIN
    ALTER TABLE dbo.SaDeliveryRequestStockReservation WITH CHECK ADD CONSTRAINT FK_DrStockReservation_Request
        FOREIGN KEY (DeliveryRequestID) REFERENCES dbo.SaDeliveryRequest (UID);
    ALTER TABLE dbo.SaDeliveryRequestStockReservation CHECK CONSTRAINT FK_DrStockReservation_Request;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_DrStockReservation_Source')
BEGIN
    ALTER TABLE dbo.SaDeliveryRequestStockReservation WITH CHECK ADD CONSTRAINT FK_DrStockReservation_Source
        FOREIGN KEY (DeliveryRequestSourceID) REFERENCES dbo.SaDeliveryRequestSource (UID);
    ALTER TABLE dbo.SaDeliveryRequestStockReservation CHECK CONSTRAINT FK_DrStockReservation_Source;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_DrStockReservation_BalLoc')
BEGIN
    ALTER TABLE dbo.SaDeliveryRequestStockReservation WITH CHECK ADD CONSTRAINT FK_DrStockReservation_BalLoc
        FOREIGN KEY (BalLocID) REFERENCES dbo.IvBalLoc (ID);
    ALTER TABLE dbo.SaDeliveryRequestStockReservation CHECK CONSTRAINT FK_DrStockReservation_BalLoc;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryRequestStockReservation') AND name = N'IX_DrStockReservation_Dr_Active')
    CREATE INDEX IX_DrStockReservation_Dr_Active ON dbo.SaDeliveryRequestStockReservation (CompanyCode, BranchCode, DeliveryRequestID, IsActive);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryRequestStockReservation') AND name = N'IX_DrStockReservation_Source_Active')
    CREATE INDEX IX_DrStockReservation_Source_Active ON dbo.SaDeliveryRequestStockReservation (CompanyCode, BranchCode, DeliveryRequestSourceID, IsActive);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryRequestStockReservation') AND name = N'IX_DrStockReservation_BalLoc_Active')
    CREATE INDEX IX_DrStockReservation_BalLoc_Active ON dbo.SaDeliveryRequestStockReservation (CompanyCode, BranchCode, BalLocID, IsActive);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryRequestStockReservation') AND name = N'UX_DrStockReservation_Source_BalLoc_Active')
    CREATE UNIQUE INDEX UX_DrStockReservation_Source_BalLoc_Active
        ON dbo.SaDeliveryRequestStockReservation (DeliveryRequestSourceID, BalLocID)
        WHERE IsActive = 1;
GO

IF COL_LENGTH(N'dbo.SaDODetail', N'DeliveryRequestSourceID') IS NULL
    ALTER TABLE dbo.SaDODetail ADD DeliveryRequestSourceID bigint NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SaDODetail_DeliveryRequestSource')
BEGIN
    ALTER TABLE dbo.SaDODetail WITH CHECK ADD CONSTRAINT FK_SaDODetail_DeliveryRequestSource
        FOREIGN KEY (DeliveryRequestSourceID) REFERENCES dbo.SaDeliveryRequestSource (UID);
    ALTER TABLE dbo.SaDODetail CHECK CONSTRAINT FK_SaDODetail_DeliveryRequestSource;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDODetail') AND name = N'IX_SaDODetail_Company_Branch_DeliveryRequestSourceID')
    CREATE INDEX IX_SaDODetail_Company_Branch_DeliveryRequestSourceID
        ON dbo.SaDODetail (CompanyCode, BranchCode, DeliveryRequestSourceID);
GO

IF COL_LENGTH(N'dbo.POPRDtl', N'WorkOrderMaterialID') IS NULL
    ALTER TABLE dbo.POPRDtl ADD WorkOrderMaterialID bigint NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_POPRDtl_WorkOrderMaterial')
BEGIN
    ALTER TABLE dbo.POPRDtl WITH CHECK ADD CONSTRAINT FK_POPRDtl_WorkOrderMaterial
        FOREIGN KEY (WorkOrderMaterialID) REFERENCES dbo.PrWorkOrderMaterial (UID);
    ALTER TABLE dbo.POPRDtl CHECK CONSTRAINT FK_POPRDtl_WorkOrderMaterial;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.POPRDtl') AND name = N'IX_POPRDtl_Company_Branch_WorkOrderMaterialID')
    CREATE INDEX IX_POPRDtl_Company_Branch_WorkOrderMaterialID
        ON dbo.POPRDtl (CompanyCode, BranchCode, WorkOrderMaterialID);
GO

PRINT N'Delivery Request fulfilment lineage and soft-reservation schema is ensured.';
GO
