/*
   Supplier Delivery Follow-up — additive operational log only.

   Idempotent deployment: creates only dbo.PoDeliveryFollowUp and its lookup index.
   No foreign keys, triggers, jobs, or changes to existing purchase/inventory tables.

   Rollback (new objects only, after disabling Features:SupplierDeliveryFollowUp and
   PO_DELIVERY_FOLLOWUP):
       DROP TABLE dbo.PoDeliveryFollowUp;
   Dropping the table also removes IX_PoDeliveryFollowUp_LineContact.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.PoDeliveryFollowUp', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PoDeliveryFollowUp
    (
        Id bigint IDENTITY(1,1) NOT NULL,
        CompanyCode varchar(10) NOT NULL,
        BranchCode varchar(10) NOT NULL,
        PoNo varchar(30) NOT NULL,
        PoRelNo smallint NOT NULL,
        PoLineNo smallint NOT NULL,
        ContactAtUtc datetime2(7) NOT NULL,
        Outcome varchar(20) NOT NULL,
        SupplierStatedDate date NULL,
        NextFollowUpDate date NULL,
        Note nvarchar(500) NULL,
        CreatedBy varchar(10) NOT NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        CONSTRAINT PK_PoDeliveryFollowUp PRIMARY KEY CLUSTERED (Id)
    );
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PoDeliveryFollowUp')
      AND name = N'IX_PoDeliveryFollowUp_LineContact'
)
BEGIN
    CREATE INDEX IX_PoDeliveryFollowUp_LineContact
        ON dbo.PoDeliveryFollowUp
        (CompanyCode, BranchCode, PoNo, PoRelNo, PoLineNo, ContactAtUtc);
END;
GO

SELECT TableName = N'PoDeliveryFollowUp',
       IndexName = i.name
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID(N'dbo.PoDeliveryFollowUp')
  AND i.name IN (N'PK_PoDeliveryFollowUp', N'IX_PoDeliveryFollowUp_LineContact');
GO
