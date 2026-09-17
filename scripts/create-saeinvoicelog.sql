-- Append-only LHDN e-Invoice audit history (manual DBA script - do NOT run at app startup).
-- Target database: same as ConnectionStrings:DefaultConnection.
--
-- The document row (SaInvoice / SaCDN) holds only the CURRENT snapshot in its IRBM* columns.
-- This table holds the full sequence of actions so a chain such as
-- "submit attempt 1 -> timeout -> recover -> valid" stays traceable.
--
-- Never write access tokens, client secrets, certificate passwords or full signed payloads here.
-- Safe to run twice (guarded CREATE).

SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SaEInvoiceLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaEInvoiceLog
    (
        Id            bigint IDENTITY(1,1) NOT NULL,
        CompanyCode   nvarchar(10)  NOT NULL,
        DocumentType  nvarchar(10)  NOT NULL,   -- INV, CN, DN (self-bill prefixes to follow)
        DocumentNo    nvarchar(30)  NOT NULL,
        SubmissionId  nvarchar(50)  NULL,
        DocumentUuid  nvarchar(50)  NULL,
        Action        nvarchar(20)  NOT NULL,   -- Validate|Submit|Recover|Refresh|Cancel|Retry
        Status        nvarchar(50)  NULL,       -- resulting IRBMStatus / outcome
        AttemptNo     int           NOT NULL CONSTRAINT DF_SaEInvoiceLog_AttemptNo DEFAULT (0),
        CorrelationId uniqueidentifier NULL,
        RequestTime   datetime2     NULL,
        ResponseTime  datetime2     NULL,
        DurationMs    bigint        NULL,
        ErrorCode     nvarchar(100) NULL,
        ErrorMessage  nvarchar(4000) NULL,
        CreatedBy     nvarchar(20)  NULL,
        CreatedOn     datetime2     NOT NULL CONSTRAINT DF_SaEInvoiceLog_CreatedOn DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_SaEInvoiceLog PRIMARY KEY CLUSTERED (Id)
    );

    CREATE INDEX IX_SaEInvoiceLog_Document
        ON dbo.SaEInvoiceLog (CompanyCode, DocumentType, DocumentNo, Id);

    CREATE INDEX IX_SaEInvoiceLog_Company_CreatedOn
        ON dbo.SaEInvoiceLog (CompanyCode, CreatedOn);

    PRINT N'SAEINVOICELOG_CREATED';
END
ELSE
BEGIN
    PRINT N'SAEINVOICELOG_ALREADY_EXISTS';
END
GO
