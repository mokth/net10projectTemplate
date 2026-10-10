-- Optional Sales physical-delivery tracking schema.
-- Idempotent SQL Server deployment. Existing SaDO/stock/billing data is never changed.

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.SaDO', N'U') IS NULL
    THROW 51000, 'SaDO must exist before Sales Delivery Tracking is deployed.', 1;
GO

IF OBJECT_ID(N'dbo.SaDeliveryDriver', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDeliveryDriver
    (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        DriverId nvarchar(30) NOT NULL,
        DriverName nvarchar(100) NOT NULL,
        MobileNo nvarchar(50) NULL,
        DriverType nvarchar(20) NOT NULL,
        EmployeeCode nvarchar(30) NULL,
        TransporterName nvarchar(100) NULL,
        Active bit NOT NULL CONSTRAINT DF_SaDeliveryDriver_Active DEFAULT (1),
        Remarks nvarchar(500) NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(20) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SaDeliveryDriver PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, DriverId),
        CONSTRAINT CK_SaDeliveryDriver_DriverType CHECK (DriverType IN (N'EMPLOYEE', N'EXTERNAL'))
    );
END;
GO

IF OBJECT_ID(N'dbo.SaDeliveryVehicle', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDeliveryVehicle
    (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        VehicleId nvarchar(30) NOT NULL,
        RegistrationNo nvarchar(50) NOT NULL,
        VehicleType nvarchar(20) NOT NULL,
        Description nvarchar(100) NULL,
        CapacityWeight decimal(18,3) NULL,
        CapacityVolume decimal(18,3) NULL,
        TransportType nvarchar(20) NOT NULL,
        TransporterName nvarchar(100) NULL,
        Active bit NOT NULL CONSTRAINT DF_SaDeliveryVehicle_Active DEFAULT (1),
        Remarks nvarchar(500) NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(20) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SaDeliveryVehicle PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, VehicleId),
        CONSTRAINT CK_SaDeliveryVehicle_TransportType CHECK (TransportType IN (N'OWN', N'THIRD_PARTY')),
        CONSTRAINT CK_SaDeliveryVehicle_CapacityWeight CHECK (CapacityWeight IS NULL OR CapacityWeight >= 0),
        CONSTRAINT CK_SaDeliveryVehicle_CapacityVolume CHECK (CapacityVolume IS NULL OR CapacityVolume >= 0)
    );
END;
GO

IF OBJECT_ID(N'dbo.SaDeliveryExceptionReason', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDeliveryExceptionReason
    (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        ReasonCode nvarchar(30) NOT NULL,
        Description nvarchar(100) NOT NULL,
        Responsibility nvarchar(30) NOT NULL,
        Active bit NOT NULL CONSTRAINT DF_SaDeliveryExceptionReason_Active DEFAULT (1),
        SortOrder int NOT NULL CONSTRAINT DF_SaDeliveryExceptionReason_SortOrder DEFAULT (0),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(20) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SaDeliveryExceptionReason PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, ReasonCode),
        CONSTRAINT CK_SaDeliveryExceptionReason_Responsibility CHECK
            (Responsibility IN (N'DRIVER', N'WAREHOUSE', N'CUSTOMER', N'SALES', N'VEHICLE', N'EXTERNAL', N'WEATHER_TRAFFIC', N'OTHER'))
    );
END;
GO

-- Seed stable defaults for existing branches without overwriting customized reason rows.
INSERT INTO dbo.SaDeliveryExceptionReason
    (CompanyCode, BranchCode, ReasonCode, Description, Responsibility, Active, SortOrder, CreatedDate, CreatedBy)
SELECT tenants.CompanyCode,
       tenants.BranchCode,
       reasons.ReasonCode,
       reasons.Description,
       reasons.Responsibility,
       1,
       reasons.SortOrder,
       SYSUTCDATETIME(),
       N'SYSTEM'
FROM (SELECT DISTINCT CompanyCode, BranchCode FROM dbo.SaDO) AS tenants
CROSS JOIN
(
    VALUES
        (N'CUSTOMER_CLOSED', N'Customer closed', N'CUSTOMER', 10),
        (N'CUSTOMER_UNAVAILABLE', N'Customer unavailable', N'CUSTOMER', 20),
        (N'CUSTOMER_REJECTED', N'Customer rejected goods', N'CUSTOMER', 30),
        (N'WRONG_ADDRESS', N'Wrong delivery address', N'CUSTOMER', 40),
        (N'WAREHOUSE_NOT_READY', N'Warehouse not ready', N'WAREHOUSE', 50),
        (N'SHORT_GOODS', N'Short goods', N'WAREHOUSE', 60),
        (N'DAMAGED_GOODS', N'Damaged goods', N'WAREHOUSE', 70),
        (N'VEHICLE_BREAKDOWN', N'Vehicle breakdown', N'VEHICLE', 80),
        (N'TRAFFIC', N'Traffic delay', N'WEATHER_TRAFFIC', 90),
        (N'WEATHER', N'Weather delay', N'WEATHER_TRAFFIC', 100),
        (N'DRIVER_ISSUE', N'Driver issue', N'DRIVER', 110),
        (N'OTHER', N'Other', N'OTHER', 120)
) AS reasons(ReasonCode, Description, Responsibility, SortOrder)
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.SaDeliveryExceptionReason existing
    WHERE existing.CompanyCode = tenants.CompanyCode
      AND existing.BranchCode = tenants.BranchCode
      AND existing.ReasonCode = reasons.ReasonCode
);
GO

IF OBJECT_ID(N'dbo.SaDeliveryTrip', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDeliveryTrip
    (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        TripNo nvarchar(30) NOT NULL,
        TripDate date NOT NULL,
        Status nvarchar(30) NOT NULL,
        DriverId nvarchar(30) NULL,
        DriverNameSnapshot nvarchar(100) NULL,
        DriverMobileSnapshot nvarchar(50) NULL,
        VehicleId nvarchar(30) NULL,
        VehicleRegistrationSnapshot nvarchar(50) NULL,
        TransportType nvarchar(20) NOT NULL,
        TransporterNameSnapshot nvarchar(100) NULL,
        PlannedDepartureAt datetime2 NULL,
        ActualDepartureAt datetime2 NULL,
        CompletedAt datetime2 NULL,
        CancelledAt datetime2 NULL,
        CancelledBy nvarchar(20) NULL,
        CancelReason nvarchar(500) NULL,
        Remarks nvarchar(500) NULL,
        FuelCost decimal(18,2) NULL,
        TollCost decimal(18,2) NULL,
        ParkingCost decimal(18,2) NULL,
        OtherCost decimal(18,2) NULL,
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(20) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SaDeliveryTrip PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, TripNo),
        CONSTRAINT FK_SaDeliveryTrip_Driver FOREIGN KEY (CompanyCode, BranchCode, DriverId)
            REFERENCES dbo.SaDeliveryDriver (CompanyCode, BranchCode, DriverId) ON DELETE NO ACTION,
        CONSTRAINT FK_SaDeliveryTrip_Vehicle FOREIGN KEY (CompanyCode, BranchCode, VehicleId)
            REFERENCES dbo.SaDeliveryVehicle (CompanyCode, BranchCode, VehicleId) ON DELETE NO ACTION,
        CONSTRAINT CK_SaDeliveryTrip_Status CHECK (Status IN (N'PLANNED', N'OUT_FOR_DELIVERY', N'COMPLETED', N'CANCELLED')),
        CONSTRAINT CK_SaDeliveryTrip_TransportType CHECK (TransportType IN (N'OWN', N'THIRD_PARTY')),
        CONSTRAINT CK_SaDeliveryTrip_Costs CHECK
            ((FuelCost IS NULL OR FuelCost >= 0) AND (TollCost IS NULL OR TollCost >= 0)
             AND (ParkingCost IS NULL OR ParkingCost >= 0) AND (OtherCost IS NULL OR OtherCost >= 0))
    );
END;
GO

IF OBJECT_ID(N'dbo.SaDeliveryTripStop', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDeliveryTripStop
    (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        TripNo nvarchar(30) NOT NULL,
        StopID bigint IDENTITY(1,1) NOT NULL,
        StopSequence int NOT NULL,
        CustCode nvarchar(60) NOT NULL,
        CustNameSnapshot nvarchar(200) NULL,
        ShipNameSnapshot nvarchar(100) NULL,
        ShipAddress1Snapshot nvarchar(100) NULL,
        ShipAddress2Snapshot nvarchar(100) NULL,
        ShipAddress3Snapshot nvarchar(100) NULL,
        ShipAddress4Snapshot nvarchar(100) NULL,
        ShipCitySnapshot nvarchar(50) NULL,
        ShipStateSnapshot nvarchar(50) NULL,
        ShipPostalCodeSnapshot nvarchar(20) NULL,
        ShipCountrySnapshot nvarchar(50) NULL,
        ContactNameSnapshot nvarchar(100) NULL,
        ContactPhoneSnapshot nvarchar(50) NULL,
        PlannedArrivalAt datetime2 NULL,
        Status nvarchar(30) NOT NULL,
        LastAttemptNo int NULL,
        CompletedAt datetime2 NULL,
        Remarks nvarchar(500) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SaDeliveryTripStop PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, TripNo, StopID),
        CONSTRAINT FK_SaDeliveryTripStop_Trip FOREIGN KEY (CompanyCode, BranchCode, TripNo)
            REFERENCES dbo.SaDeliveryTrip (CompanyCode, BranchCode, TripNo) ON DELETE NO ACTION,
        CONSTRAINT CK_SaDeliveryTripStop_Status CHECK
            (Status IN (N'PLANNED', N'OUT_FOR_DELIVERY', N'DELIVERED', N'PARTIALLY_DELIVERED', N'FAILED', N'RESCHEDULED', N'CANCELLED')),
        CONSTRAINT CK_SaDeliveryTripStop_StopSequence CHECK (StopSequence > 0)
    );
END;
GO

IF OBJECT_ID(N'dbo.SaDeliveryTripStopDo', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDeliveryTripStopDo
    (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        TripNo nvarchar(30) NOT NULL,
        StopID bigint NOT NULL,
        DONo nvarchar(30) NOT NULL,
        PromisedDeliveryDateSnapshot datetime2 NULL,
        PromisedFromTimeSnapshot time(0) NULL,
        PromisedToTimeSnapshot time(0) NULL,
        DoDateSnapshot datetime2 NOT NULL,
        IsActiveAssignment bit NOT NULL CONSTRAINT DF_SaDeliveryTripStopDo_IsActiveAssignment DEFAULT (1),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(20) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SaDeliveryTripStopDo PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, TripNo, StopID, DONo),
        CONSTRAINT FK_SaDeliveryTripStopDo_Stop FOREIGN KEY (CompanyCode, BranchCode, TripNo, StopID)
            REFERENCES dbo.SaDeliveryTripStop (CompanyCode, BranchCode, TripNo, StopID) ON DELETE NO ACTION,
        CONSTRAINT FK_SaDeliveryTripStopDo_SaDO FOREIGN KEY (CompanyCode, BranchCode, DONo)
            REFERENCES dbo.SaDO (CompanyCode, BranchCode, DONo) ON DELETE NO ACTION
    );
END;
GO

IF OBJECT_ID(N'dbo.SaDeliveryAttempt', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDeliveryAttempt
    (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        AttemptID bigint IDENTITY(1,1) NOT NULL,
        TripNo nvarchar(30) NOT NULL,
        StopID bigint NOT NULL,
        AttemptNo int NOT NULL,
        StartedAt datetime2 NULL,
        ArrivedAt datetime2 NULL,
        CompletedAt datetime2 NOT NULL,
        Result nvarchar(20) NOT NULL,
        ReasonCode nvarchar(30) NULL,
        Responsibility nvarchar(30) NULL,
        ReceivedBy nvarchar(100) NULL,
        ReceiverContact nvarchar(50) NULL,
        Remark nvarchar(1000) NULL,
        Latitude decimal(9,6) NULL,
        Longitude decimal(9,6) NULL,
        RecordedBy nvarchar(20) NOT NULL,
        RecordedAt datetime2 NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SaDeliveryAttempt PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, AttemptID),
        CONSTRAINT FK_SaDeliveryAttempt_Stop FOREIGN KEY (CompanyCode, BranchCode, TripNo, StopID)
            REFERENCES dbo.SaDeliveryTripStop (CompanyCode, BranchCode, TripNo, StopID) ON DELETE NO ACTION,
        CONSTRAINT FK_SaDeliveryAttempt_ExceptionReason FOREIGN KEY (CompanyCode, BranchCode, ReasonCode)
            REFERENCES dbo.SaDeliveryExceptionReason (CompanyCode, BranchCode, ReasonCode) ON DELETE NO ACTION,
        CONSTRAINT CK_SaDeliveryAttempt_Result CHECK (Result IN (N'DELIVERED', N'PARTIAL', N'FAILED')),
        CONSTRAINT CK_SaDeliveryAttempt_Responsibility CHECK
            (Responsibility IS NULL OR Responsibility IN (N'DRIVER', N'WAREHOUSE', N'CUSTOMER', N'SALES', N'VEHICLE', N'EXTERNAL', N'WEATHER_TRAFFIC', N'OTHER')),
        CONSTRAINT CK_SaDeliveryAttempt_AttemptNo CHECK (AttemptNo > 0),
        CONSTRAINT CK_SaDeliveryAttempt_Latitude CHECK (Latitude IS NULL OR Latitude BETWEEN -90 AND 90),
        CONSTRAINT CK_SaDeliveryAttempt_Longitude CHECK (Longitude IS NULL OR Longitude BETWEEN -180 AND 180)
    );
END;
GO

IF OBJECT_ID(N'dbo.SaDeliveryAttemptDo', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDeliveryAttemptDo
    (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        AttemptID bigint NOT NULL,
        DONo nvarchar(30) NOT NULL,
        Result nvarchar(20) NOT NULL,
        Remark nvarchar(500) NULL,
        CONSTRAINT PK_SaDeliveryAttemptDo PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, AttemptID, DONo),
        CONSTRAINT FK_SaDeliveryAttemptDo_Attempt FOREIGN KEY (CompanyCode, BranchCode, AttemptID)
            REFERENCES dbo.SaDeliveryAttempt (CompanyCode, BranchCode, AttemptID) ON DELETE NO ACTION,
        CONSTRAINT FK_SaDeliveryAttemptDo_SaDO FOREIGN KEY (CompanyCode, BranchCode, DONo)
            REFERENCES dbo.SaDO (CompanyCode, BranchCode, DONo) ON DELETE NO ACTION,
        CONSTRAINT CK_SaDeliveryAttemptDo_Result CHECK (Result IN (N'DELIVERED', N'PARTIAL', N'FAILED'))
    );
END;
GO

IF OBJECT_ID(N'dbo.SaDeliveryPodAttachment', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaDeliveryPodAttachment
    (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        AttachmentID bigint IDENTITY(1,1) NOT NULL,
        AttemptID bigint NOT NULL,
        DONo nvarchar(30) NULL,
        AttachmentType nvarchar(20) NOT NULL,
        OriginalFileName nvarchar(255) NOT NULL,
        StoredFileName nvarchar(255) NOT NULL,
        ContentType nvarchar(100) NOT NULL,
        FileSize bigint NOT NULL,
        CreatedDate datetime2 NOT NULL,
        CreatedBy nvarchar(20) NOT NULL,
        CONSTRAINT PK_SaDeliveryPodAttachment PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, AttachmentID),
        CONSTRAINT FK_SaDeliveryPodAttachment_Attempt FOREIGN KEY (CompanyCode, BranchCode, AttemptID)
            REFERENCES dbo.SaDeliveryAttempt (CompanyCode, BranchCode, AttemptID) ON DELETE NO ACTION,
        CONSTRAINT FK_SaDeliveryPodAttachment_SaDO FOREIGN KEY (CompanyCode, BranchCode, DONo)
            REFERENCES dbo.SaDO (CompanyCode, BranchCode, DONo) ON DELETE NO ACTION,
        CONSTRAINT CK_SaDeliveryPodAttachment_Type CHECK (AttachmentType IN (N'PHOTO', N'SIGNATURE', N'DOCUMENT')),
        CONSTRAINT CK_SaDeliveryPodAttachment_FileSize CHECK (FileSize > 0)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryDriver') AND name = N'IX_SaDeliveryDriver_Tenant_Active_Name')
    CREATE INDEX IX_SaDeliveryDriver_Tenant_Active_Name ON dbo.SaDeliveryDriver (CompanyCode, BranchCode, Active, DriverName);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryVehicle') AND name = N'IX_SaDeliveryVehicle_Tenant_Active_Registration')
    CREATE INDEX IX_SaDeliveryVehicle_Tenant_Active_Registration ON dbo.SaDeliveryVehicle (CompanyCode, BranchCode, Active, RegistrationNo);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryExceptionReason') AND name = N'IX_SaDeliveryExceptionReason_Tenant_Active_Sort')
    CREATE INDEX IX_SaDeliveryExceptionReason_Tenant_Active_Sort ON dbo.SaDeliveryExceptionReason (CompanyCode, BranchCode, Active, SortOrder, Description);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryTrip') AND name = N'IX_SaDeliveryTrip_Tenant_Date_Status')
    CREATE INDEX IX_SaDeliveryTrip_Tenant_Date_Status ON dbo.SaDeliveryTrip (CompanyCode, BranchCode, TripDate, Status);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryTrip') AND name = N'IX_SaDeliveryTrip_Tenant_Driver_Date')
    CREATE INDEX IX_SaDeliveryTrip_Tenant_Driver_Date ON dbo.SaDeliveryTrip (CompanyCode, BranchCode, DriverId, TripDate);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryTrip') AND name = N'IX_SaDeliveryTrip_Tenant_Vehicle_Date')
    CREATE INDEX IX_SaDeliveryTrip_Tenant_Vehicle_Date ON dbo.SaDeliveryTrip (CompanyCode, BranchCode, VehicleId, TripDate);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryTripStop') AND name = N'UX_SaDeliveryTripStop_Tenant_Trip_Sequence')
    CREATE UNIQUE INDEX UX_SaDeliveryTripStop_Tenant_Trip_Sequence ON dbo.SaDeliveryTripStop (CompanyCode, BranchCode, TripNo, StopSequence);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryTripStop') AND name = N'IX_SaDeliveryTripStop_Tenant_Status_Trip')
    CREATE INDEX IX_SaDeliveryTripStop_Tenant_Status_Trip ON dbo.SaDeliveryTripStop (CompanyCode, BranchCode, Status, TripNo);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryTripStopDo') AND name = N'IX_SaDeliveryTripStopDo_Tenant_DONo_Active')
    CREATE INDEX IX_SaDeliveryTripStopDo_Tenant_DONo_Active ON dbo.SaDeliveryTripStopDo (CompanyCode, BranchCode, DONo, IsActiveAssignment);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryTripStopDo') AND name = N'UX_SaDeliveryTripStopDo_ActiveAssignment')
    CREATE UNIQUE INDEX UX_SaDeliveryTripStopDo_ActiveAssignment
        ON dbo.SaDeliveryTripStopDo (CompanyCode, BranchCode, DONo)
        WHERE IsActiveAssignment = 1;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryAttempt') AND name = N'IX_SaDeliveryAttempt_Tenant_Stop_Date_Result')
    CREATE INDEX IX_SaDeliveryAttempt_Tenant_Stop_Date_Result ON dbo.SaDeliveryAttempt (CompanyCode, BranchCode, TripNo, StopID, CompletedAt, Result);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryAttempt') AND name = N'IX_SaDeliveryAttempt_Tenant_Reason_Responsibility_Date')
    CREATE INDEX IX_SaDeliveryAttempt_Tenant_Reason_Responsibility_Date ON dbo.SaDeliveryAttempt (CompanyCode, BranchCode, ReasonCode, Responsibility, CompletedAt);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryAttempt') AND name = N'UX_SaDeliveryAttempt_Tenant_Stop_AttemptNo')
    CREATE UNIQUE INDEX UX_SaDeliveryAttempt_Tenant_Stop_AttemptNo ON dbo.SaDeliveryAttempt (CompanyCode, BranchCode, TripNo, StopID, AttemptNo);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryAttemptDo') AND name = N'IX_SaDeliveryAttemptDo_Tenant_DONo')
    CREATE INDEX IX_SaDeliveryAttemptDo_Tenant_DONo ON dbo.SaDeliveryAttemptDo (CompanyCode, BranchCode, DONo);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.SaDeliveryPodAttachment') AND name = N'IX_SaDeliveryPodAttachment_Tenant_Attempt_Date')
    CREATE INDEX IX_SaDeliveryPodAttachment_Tenant_Attempt_Date ON dbo.SaDeliveryPodAttachment (CompanyCode, BranchCode, AttemptID, CreatedDate);
GO

PRINT N'Sales Delivery Tracking tables and indexes are ensured.';
GO
