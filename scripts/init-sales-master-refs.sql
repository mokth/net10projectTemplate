-- Sales reference masters (flat code-reference family).
-- Manual deploy only — do NOT run at app startup.
-- Target: same database as ConnectionStrings:DefaultConnection
--
-- Pre-flight (Step 0.0) re-run 2026-09-15 against ERPWeb on .\SQLEXPRESS:
--   SELECT ... WHERE t.name IN (N'SaCustSubGroup', N'SaShipVia', N'SaSOType',
--                               N'SaComment', N'SaShippingLeadTime', N'SaLMW')
--   => 0 rows. All six tables are ABSENT, so this script CREATES rather than migrates.
--   This is a point-in-time observation — re-run the query at deploy time.
-- Sibling shape confirmed from the same live DB: SaCustType is Level A (RowVersion present);
-- SaPaymentTerm has no RowVersion (Level B). SaCustType is the clone template for all six.
--
-- Post-apply verification (paste the result into this header):
--   SELECT t.name, c.name, ty.name AS type_name, c.max_length, c.is_nullable
--   FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id
--        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
--   WHERE t.name IN (N'SaCustSubGroup', N'SaShipVia', N'SaSOType', N'SaComment',
--                    N'SaShippingLeadTime', N'SaLMW')
--   ORDER BY t.name, c.column_id;
--
-- APPLIED AND VERIFIED 2026-09-15 against a clean scratch database (ERPWeb_SalesRefDdlTest on
-- .\SQLEXPRESS). Result matches the §9.2 matrix exactly:
--   * 62 columns across the six tables; RowVersion = timestamp NOT NULL on all six.
--   * SaLMW's four window columns are `date NOT NULL` (max_length 3).
--   * nvarchar max_length is bytes: CompanyCode 20 (=10 chars), CustSubGroupCode 40 (=20),
--     [Comment] 2000 (=1000), SaLMW LicenseNo 80 (=40), CustCode 60 (=30).
--   * SaComment has NO `Module` and NO legacy identity `ID` column (D-4).
--   * SaSOType has NO `FOCAuto` column (D-3).
--   * Constraints present: CK_SaShippingLeadTime_Days, CK_SaShippingLeadTime_Type,
--     CK_SaLMW_LicenseDates, CK_SaLMW_SystemDates, CK_SaLMW_Contained. Index present:
--     IX_SaLMW_Overlap.
--   * Re-running the script is a no-op (idempotent).
--
-- Verified absent 2026-09-15 (re-check at deploy — §7.2a of docs/sales-master-plan.md).

SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.SaCustSubGroup', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaCustSubGroup (
        CompanyCode       nvarchar(10)  NOT NULL,
        CustSubGroupCode  nvarchar(20)  NOT NULL,
        CustSubGroupDesc  nvarchar(100) NULL,
        BranchCode        nvarchar(10)  NULL,
        LocationCode      nvarchar(20)  NULL,
        Created           datetime2     NULL,
        Updated           datetime2     NULL,
        UserID            nvarchar(20)  NULL,
        UpdatedUID        nvarchar(20)  NULL,
        RowVersion        rowversion    NOT NULL,
        CONSTRAINT PK_SaCustSubGroup PRIMARY KEY (CompanyCode, CustSubGroupCode)
    );
END
GO

IF OBJECT_ID(N'dbo.SaShipVia', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaShipVia (
        CompanyCode   nvarchar(10)  NOT NULL,
        ShipViaCode   nvarchar(20)  NOT NULL,
        ShipViaDesc   nvarchar(100) NULL,
        Active        bit           NOT NULL CONSTRAINT DF_SaShipVia_Active DEFAULT (1),
        BranchCode    nvarchar(10)  NULL,
        LocationCode  nvarchar(20)  NULL,
        Created       datetime2     NULL,
        Updated       datetime2     NULL,
        UserID        nvarchar(20)  NULL,
        UpdatedUID    nvarchar(20)  NULL,
        RowVersion    rowversion    NOT NULL,
        CONSTRAINT PK_SaShipVia PRIMARY KEY (CompanyCode, ShipViaCode)
    );
END
GO

IF OBJECT_ID(N'dbo.SaSOType', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaSOType (
        CompanyCode   nvarchar(10)  NOT NULL,
        SOTypeCode    nvarchar(20)  NOT NULL,
        SOTypeDesc    nvarchar(100) NULL,
        Active        bit           NOT NULL CONSTRAINT DF_SaSOType_Active DEFAULT (1),
        BranchCode    nvarchar(10)  NULL,
        LocationCode  nvarchar(20)  NULL,
        Created       datetime2     NULL,
        Updated       datetime2     NULL,
        UserID        nvarchar(20)  NULL,
        UpdatedUID    nvarchar(20)  NULL,
        RowVersion    rowversion    NOT NULL,
        CONSTRAINT PK_SaSOType PRIMARY KEY (CompanyCode, SOTypeCode)
    );
END
GO

IF OBJECT_ID(N'dbo.SaComment', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaComment (
        CompanyCode   nvarchar(10)   NOT NULL,
        CommID        nvarchar(20)   NOT NULL,
        [Comment]     nvarchar(1000) NULL,
        Active        bit            NOT NULL CONSTRAINT DF_SaComment_Active DEFAULT (1),
        BranchCode    nvarchar(10)   NULL,
        LocationCode  nvarchar(20)   NULL,
        Created       datetime2      NULL,
        Updated       datetime2      NULL,
        UserID        nvarchar(20)   NULL,
        UpdatedUID    nvarchar(20)   NULL,
        RowVersion    rowversion     NOT NULL,
        CONSTRAINT PK_SaComment PRIMARY KEY (CompanyCode, CommID)
    );
END
GO

IF OBJECT_ID(N'dbo.SaShippingLeadTime', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaShippingLeadTime (
        CompanyCode    nvarchar(10)  NOT NULL,
        LeadTimeCode   nvarchar(20)  NOT NULL,
        LeadTimeDesc   nvarchar(100) NULL,
        Days           int           NULL,
        Type           nvarchar(10)  NULL,
        Active         bit           NOT NULL CONSTRAINT DF_SaShippingLeadTime_Active DEFAULT (1),
        BranchCode     nvarchar(10)  NULL,
        LocationCode   nvarchar(20)  NULL,
        Created        datetime2     NULL,
        Updated        datetime2     NULL,
        UserID         nvarchar(20)  NULL,
        UpdatedUID     nvarchar(20)  NULL,
        RowVersion     rowversion    NOT NULL,
        CONSTRAINT PK_SaShippingLeadTime PRIMARY KEY (CompanyCode, LeadTimeCode),
        CONSTRAINT CK_SaShippingLeadTime_Days CHECK (Days IS NULL OR (Days >= 0 AND Days <= 3650)),
        CONSTRAINT CK_SaShippingLeadTime_Type CHECK (Type IS NULL OR Type IN (N'INTERNAL', N'EXTERNAL'))
    );
END
GO

IF OBJECT_ID(N'dbo.SaLMW', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaLMW (
        CompanyCode      nvarchar(10)  NOT NULL,
        LicenseNo        nvarchar(40)  NOT NULL,
        CustCode         nvarchar(30)  NOT NULL,
        LicenseID        nvarchar(40)  NULL,
        LicenseType      nvarchar(20)  NULL,
        LicenseStartDate date          NOT NULL,
        LicenseEndDate   date          NOT NULL,
        SystemStartDate  date          NOT NULL,
        SystemEndDate    date          NOT NULL,
        Name             nvarchar(100) NULL,
        IC               nvarchar(30)  NULL,
        Position         nvarchar(50)  NULL,
        CustName         nvarchar(200) NULL,
        BranchCode       nvarchar(10)  NULL,
        LocationCode     nvarchar(20)  NULL,
        Created          datetime2     NULL,
        Updated          datetime2     NULL,
        UserID           nvarchar(20)  NULL,
        UpdatedUID       nvarchar(20)  NULL,
        RowVersion       rowversion    NOT NULL,
        CONSTRAINT PK_SaLMW PRIMARY KEY (CompanyCode, LicenseNo, CustCode),
        CONSTRAINT CK_SaLMW_LicenseDates CHECK (LicenseStartDate <= LicenseEndDate),
        CONSTRAINT CK_SaLMW_SystemDates  CHECK (SystemStartDate  <= SystemEndDate),
        CONSTRAINT CK_SaLMW_Contained    CHECK (LicenseStartDate <= SystemStartDate
                                            AND SystemEndDate   <= LicenseEndDate)
    );
END
GO

-- Supports the overlap range scan AND gives the Serializable plan a range-lock target (D-9).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaLMW_Overlap' AND object_id = OBJECT_ID(N'dbo.SaLMW'))
    CREATE NONCLUSTERED INDEX IX_SaLMW_Overlap
        ON dbo.SaLMW (CompanyCode, CustCode, SystemStartDate, SystemEndDate)
        INCLUDE (LicenseNo);
GO
