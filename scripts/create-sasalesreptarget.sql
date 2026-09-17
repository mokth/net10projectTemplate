-- Company-wide monthly sales-rep targets (manual DBA script — do NOT run at app startup).
-- Target database: same as ConnectionStrings:DefaultConnection.
--
-- Phase 1 sales analysis: target attainment is ALWAYS company-wide. The key is
-- (CompanyCode, SRepCode, Year, Month) and there is deliberately NO BranchCode, because
-- SaSalesRep itself is company-scoped; the optional branch filter on Sales Summary must never
-- change an attainment figure. A missing month row means a target of 0 for that month.
--
-- Idempotent: the guard makes a second run a clean no-op.

SET XACT_ABORT ON;
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.SaSalesRepTarget', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaSalesRepTarget (
        CompanyCode   nvarchar(10)  NOT NULL,
        SRepCode      nvarchar(20)  NOT NULL,
        [Year]        int           NOT NULL,
        [Month]       int           NOT NULL,
        TargetAmount  decimal(18,2) NOT NULL,
        Created       datetime2     NULL,
        UserID        nvarchar(20)  NULL,
        Updated       datetime2     NULL,
        UpdatedUID    nvarchar(20)  NULL,
        CONSTRAINT PK_SaSalesRepTarget PRIMARY KEY (CompanyCode, SRepCode, [Year], [Month]),
        CONSTRAINT CK_SaSalesRepTarget_Month CHECK ([Month] >= 1 AND [Month] <= 12),
        CONSTRAINT CK_SaSalesRepTarget_Year CHECK ([Year] > 0),
        CONSTRAINT CK_SaSalesRepTarget_Amount CHECK (TargetAmount >= 0)
    );

    PRINT N'SASALESREPTARGET_CREATED';
END
ELSE
BEGIN
    PRINT N'SASALESREPTARGET_ALREADY_PRESENT';
END
GO

PRINT N'SaSALESREPTARGET table ensured.';
GO
