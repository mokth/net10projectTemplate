-- Patch: SO/DO/Invoice projection + lineage columns for SaDocApplication.
-- Safe to re-run. Manual DBA script — do NOT run at app startup.
GO

IF OBJECT_ID(N'dbo.SaSO', N'U') IS NULL
    RAISERROR(N'SaSO missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.SaSO', N'FulfillmentStatus') IS NULL
    ALTER TABLE dbo.SaSO ADD FulfillmentStatus nvarchar(10) NOT NULL
        CONSTRAINT DF_SaSO_FulfillmentStatus DEFAULT (N'NONE');
GO

IF COL_LENGTH(N'dbo.SaSO', N'BillingStatus') IS NULL
    ALTER TABLE dbo.SaSO ADD BillingStatus nvarchar(10) NOT NULL
        CONSTRAINT DF_SaSO_BillingStatus DEFAULT (N'NONE');
GO

IF OBJECT_ID(N'dbo.SaSODetail', N'U') IS NULL
    RAISERROR(N'SaSODetail missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.SaSODetail', N'DeliveredQty') IS NULL
    ALTER TABLE dbo.SaSODetail ADD DeliveredQty decimal(18,4) NOT NULL
        CONSTRAINT DF_SaSODetail_DeliveredQty DEFAULT (0);
GO

IF COL_LENGTH(N'dbo.SaSODetail', N'InvoicedQty') IS NULL
    ALTER TABLE dbo.SaSODetail ADD InvoicedQty decimal(18,4) NOT NULL
        CONSTRAINT DF_SaSODetail_InvoicedQty DEFAULT (0);
GO

IF OBJECT_ID(N'dbo.SaDO', N'U') IS NULL
    RAISERROR(N'SaDO missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.SaDO', N'BillingStatus') IS NULL
    ALTER TABLE dbo.SaDO ADD BillingStatus nvarchar(10) NOT NULL
        CONSTRAINT DF_SaDO_BillingStatus DEFAULT (N'NONE');
GO

IF OBJECT_ID(N'dbo.SaInvoiceDetail', N'U') IS NULL
    RAISERROR(N'SaInvoiceDetail missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'DONo') IS NULL
    ALTER TABLE dbo.SaInvoiceDetail ADD DONo nvarchar(30) NOT NULL
        CONSTRAINT DF_SaInvoiceDetail_LineDONo DEFAULT (N'');
GO

IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'DOLine') IS NULL
    ALTER TABLE dbo.SaInvoiceDetail ADD DOLine smallint NULL;
GO

PRINT N'alter-saso-allocation-columns.sql completed.';
GO
