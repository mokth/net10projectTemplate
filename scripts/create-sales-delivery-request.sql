-- Delivery Request schema: Sales Order production demand -> Work Order allocation.
-- Idempotent SQL Server deployment. This script creates only the new relational bridge;
-- it does not backfill or invent historical Work Order allocations.

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.SaSO', N'U') IS NULL
   OR OBJECT_ID(N'dbo.SaSODetail', N'U') IS NULL
   OR OBJECT_ID(N'dbo.PrWorkOrder', N'U') IS NULL
    THROW 51000, 'SaSO, SaSODetail, and PrWorkOrder are required before Delivery Request deployment.', 1;
GO

IF OBJECT_ID(N'dbo.SaDeliveryRequest', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDeliveryRequest
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SaDeliveryRequest PRIMARY KEY,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DeliveryRequestNo nvarchar(30) NOT NULL,
        ProductCode nvarchar(30) NOT NULL,
        ProductDescription nvarchar(200) NULL,
        ProductionUOM nvarchar(10) NOT NULL,
        RequestedQty decimal(18,4) NOT NULL,
        RequiredDate datetime2 NOT NULL,
        DefinitionCode nvarchar(30) NULL,
        WarehouseCode nvarchar(20) NULL,
        ProjectCode nvarchar(20) NULL,
        Priority nvarchar(20) NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_SaDeliveryRequest_Status DEFAULT (N'DRAFT'),
        Remark nvarchar(500) NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(20) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_SaDeliveryRequest_Status CHECK
            (Status IN (N'DRAFT', N'RELEASED', N'IN_PRODUCTION', N'COMPLETED', N'CANCELLED')),
        CONSTRAINT CK_SaDeliveryRequest_RequestedQty CHECK (RequestedQty > 0)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryRequest')
      AND name = N'UQ_SaDeliveryRequest_Company_Branch_No'
)
    CREATE UNIQUE INDEX UQ_SaDeliveryRequest_Company_Branch_No
        ON dbo.SaDeliveryRequest (CompanyCode, BranchCode, DeliveryRequestNo);
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryRequest')
      AND name = N'IX_SaDeliveryRequest_Company_Branch_Status_Date'
)
    CREATE INDEX IX_SaDeliveryRequest_Company_Branch_Status_Date
        ON dbo.SaDeliveryRequest (CompanyCode, BranchCode, Status, RequiredDate);
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryRequest')
      AND name = N'IX_SaDeliveryRequest_Company_Branch_Product_Uom'
)
    CREATE INDEX IX_SaDeliveryRequest_Company_Branch_Product_Uom
        ON dbo.SaDeliveryRequest (CompanyCode, BranchCode, ProductCode, ProductionUOM);
GO

IF OBJECT_ID(N'dbo.SaDeliveryRequestSource', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDeliveryRequestSource
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SaDeliveryRequestSource PRIMARY KEY,
        DeliveryRequestID bigint NOT NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        SONo nvarchar(30) NOT NULL,
        CustRel smallint NOT NULL,
        SOLine smallint NOT NULL,
        ProductCode nvarchar(30) NOT NULL,
        SourceUOM nvarchar(10) NOT NULL,
        ProductionUOM nvarchar(10) NOT NULL,
        SourceQty decimal(18,4) NOT NULL,
        AllocatedProductionQty decimal(18,4) NOT NULL,
        CustomerCode nvarchar(60) NULL,
        RequestedDeliveryDate datetime2 NULL,
        IsActive bit NOT NULL CONSTRAINT DF_SaDeliveryRequestSource_IsActive DEFAULT (1),
        ReleasedDate datetime2 NULL,
        ReleasedBy nvarchar(20) NULL,
        ReleaseReason nvarchar(500) NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_SaDeliveryRequestSource_Request
            FOREIGN KEY (DeliveryRequestID) REFERENCES dbo.SaDeliveryRequest (UID),
        CONSTRAINT FK_SaDeliveryRequestSource_SoDetail
            FOREIGN KEY (CompanyCode, BranchCode, SONo, CustRel, SOLine)
            REFERENCES dbo.SaSODetail (CompanyCode, BranchCode, SONo, CustRel, Line),
        CONSTRAINT CK_SaDeliveryRequestSource_AllocatedQty CHECK (AllocatedProductionQty > 0)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryRequestSource')
      AND name = N'IX_SaDeliveryRequestSource_DeliveryRequest'
)
    CREATE INDEX IX_SaDeliveryRequestSource_DeliveryRequest
        ON dbo.SaDeliveryRequestSource (DeliveryRequestID);
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryRequestSource')
      AND name = N'IX_SaDeliveryRequestSource_SO_Line'
)
    CREATE INDEX IX_SaDeliveryRequestSource_SO_Line
        ON dbo.SaDeliveryRequestSource (CompanyCode, BranchCode, SONo, CustRel, SOLine);
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryRequestSource')
      AND name = N'UQ_SaDeliveryRequestSource_Request_SO_Line'
)
    CREATE UNIQUE INDEX UQ_SaDeliveryRequestSource_Request_SO_Line
        ON dbo.SaDeliveryRequestSource
            (DeliveryRequestID, CompanyCode, BranchCode, SONo, CustRel, SOLine);
GO

IF OBJECT_ID(N'dbo.SaDeliveryRequestAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDeliveryRequestAudit
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SaDeliveryRequestAudit PRIMARY KEY,
        DeliveryRequestID bigint NOT NULL,
        EventType nvarchar(40) NOT NULL,
        SourceID bigint NULL,
        WorkOrderID bigint NULL,
        DetailsJson nvarchar(max) NULL,
        Reason nvarchar(500) NULL,
        OccurredDate datetime2 NOT NULL,
        ActorUserId nvarchar(20) NOT NULL,
        CONSTRAINT FK_SaDeliveryRequestAudit_Request
            FOREIGN KEY (DeliveryRequestID) REFERENCES dbo.SaDeliveryRequest (UID)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryRequestAudit')
      AND name = N'IX_SaDeliveryRequestAudit_Request_Date'
)
    CREATE INDEX IX_SaDeliveryRequestAudit_Request_Date
        ON dbo.SaDeliveryRequestAudit (DeliveryRequestID, OccurredDate);
GO

IF OBJECT_ID(N'dbo.PrWorkOrderDemandAllocation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PrWorkOrderDemandAllocation
    (
        UID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_PrWorkOrderDemandAllocation PRIMARY KEY,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        WorkOrderID bigint NOT NULL,
        DeliveryRequestID bigint NOT NULL,
        AllocatedQty decimal(18,4) NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_PrWorkOrderDemandAllocation_IsActive DEFAULT (1),
        ReleasedDate datetime2 NULL,
        ReleasedBy nvarchar(20) NULL,
        ReleaseReason nvarchar(500) NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_PrWorkOrderDemandAllocation_WorkOrder
            FOREIGN KEY (WorkOrderID) REFERENCES dbo.PrWorkOrder (UID),
        CONSTRAINT FK_PrWorkOrderDemandAllocation_Request
            FOREIGN KEY (DeliveryRequestID) REFERENCES dbo.SaDeliveryRequest (UID),
        CONSTRAINT CK_PrWorkOrderDemandAllocation_AllocatedQty CHECK (AllocatedQty > 0)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderDemandAllocation')
      AND name = N'UQ_PrWorkOrderDemandAllocation_WorkOrder'
)
    CREATE UNIQUE INDEX UQ_PrWorkOrderDemandAllocation_WorkOrder
        ON dbo.PrWorkOrderDemandAllocation (WorkOrderID);
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderDemandAllocation')
      AND name = N'IX_PrWorkOrderDemandAllocation_Request'
)
    CREATE INDEX IX_PrWorkOrderDemandAllocation_Request
        ON dbo.PrWorkOrderDemandAllocation (CompanyCode, BranchCode, DeliveryRequestID);
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PrWorkOrderDemandAllocation')
      AND name = N'IX_PrWorkOrderDemandAllocation_WorkOrder'
)
    CREATE INDEX IX_PrWorkOrderDemandAllocation_WorkOrder
        ON dbo.PrWorkOrderDemandAllocation (CompanyCode, BranchCode, WorkOrderID);
GO

PRINT N'SaDeliveryRequest, SaDeliveryRequestSource, SaDeliveryRequestAudit, and PrWorkOrderDemandAllocation are ensured.';
GO
