-- Inventory Period Close (month end) + stored closing balances — additive DDL.
--
-- MANUAL DBA SCRIPT — do NOT run at application startup. Idempotent: safe to re-run (every block is
-- guarded on its own object). Target database: the same database as ConnectionStrings:DefaultConnection
-- (ERPWeb). Run with:  sqlcmd -E -d ERPWeb -i scripts/create-iv-period-close.sql
--
-- WHAT THIS CREATES
--   dbo.IvPeriodCloseHdr — one row per closed period per company+branch (CLOSED / REOPENED).
--   dbo.IvPeriodCloseBal — one row per 7-part stock slice with a NON-ZERO closing quantity (D13).
--   Nothing else. No system-of-record table (IvTrxBatch / IvTrxBatchDetail / IvTrxHistory / IvBalLoc)
--   is touched by this feature, and none of them is ever purged: this script only adds the two
--   feature-owned tables. IvPeriodCloseBal is a DERIVED snapshot deleted on reopen and regenerated on
--   the next close.
--
-- BATCHING RULES (learned the hard way in this repo)
--   * CREATE TABLE and each CREATE INDEX live in SEPARATE batches.
--   * SET QUOTED_IDENTIFIER ON is set here explicitly (FOR XML verification queries need it).
--   * A guarded block stops only its OWN batch — every block below checks for itself.

SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;
GO

-- ── Header ──────────────────────────────────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.IvPeriodCloseHdr', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IvPeriodCloseHdr
    (
        ID                           int           IDENTITY(1,1) NOT NULL,

        CompanyCode                  nvarchar(5)   NOT NULL,
        BranchCode                   nvarchar(5)   NOT NULL,
        PeriodFrom                   date          NOT NULL,   -- first day of the closed period (inclusive)
        PeriodTo                     date          NOT NULL,   -- last day of the closed period (inclusive)
        Status                       nvarchar(20)  NOT NULL,   -- CLOSED / REOPENED

        ClosedBy                     nvarchar(10)  NULL,
        ClosedOn                     datetime2     NULL,

        -- Reopen audit, kept across a re-close.
        ReopenCount                  int           NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_ReopenCount DEFAULT (0),
        ReopenedBy                   nvarchar(10)  NULL,
        ReopenedOn                   datetime2     NULL,
        ReopenReason                 nvarchar(250) NULL,

        -- Snapshot shape (evidence of what the close produced).
        LineCount                    int           NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_LineCount DEFAULT (0),
        SkippedZeroSlices            int           NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_SkippedZero DEFAULT (0),
        OpeningAdjustSlices          int           NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_OpeningAdjust DEFAULT (0),
        CarryForwardMismatchSlices   int           NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_CFMismatch DEFAULT (0),
        CurrentBalanceCheckApplies   bit           NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_CurCheckApplies DEFAULT (0),
        CurrentBalanceMismatchSlices int           NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_CurMismatch DEFAULT (0),

        TotalOpeningValue            decimal(18,4) NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_TotalOpening DEFAULT (0),
        TotalInValue                 decimal(18,4) NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_TotalIn DEFAULT (0),
        TotalOutValue                decimal(18,4) NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_TotalOut DEFAULT (0),
        TotalClosingValue            decimal(18,4) NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_TotalClosing DEFAULT (0),

        LastReopenLineCount          int           NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_LastReopenLines DEFAULT (0),
        LastReopenClosingValue       decimal(18,4) NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_LastReopenValue DEFAULT (0),

        UnpostedBatchCount           int           NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_Unposted DEFAULT (0),
        ReconcileFindingCount        int           NOT NULL CONSTRAINT DF_IvPeriodCloseHdr_ReconcileCount DEFAULT (0),

        Remark                       nvarchar(250) NULL,

        Created                      datetime      NULL,
        UserID                       nvarchar(10)  NULL,
        Updated                      datetime      NULL,
        UpdatedUID                   nvarchar(10)  NULL,
        RowVersion                   rowversion    NOT NULL,

        CONSTRAINT PK_IvPeriodCloseHdr PRIMARY KEY CLUSTERED (ID)
    );
END
GO

-- One closed period per company+branch+PeriodFrom: periods are identified by dates (D5/D6).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IvPeriodCloseHdr_Period' AND object_id = OBJECT_ID(N'dbo.IvPeriodCloseHdr'))
    CREATE UNIQUE INDEX UQ_IvPeriodCloseHdr_Period
        ON dbo.IvPeriodCloseHdr (CompanyCode, BranchCode, PeriodFrom);
GO

-- The guard's hot path: the latest CLOSED period for a tenant, with its closed-through date.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IvPeriodCloseHdr_Tenant_Status' AND object_id = OBJECT_ID(N'dbo.IvPeriodCloseHdr'))
    CREATE INDEX IX_IvPeriodCloseHdr_Tenant_Status
        ON dbo.IvPeriodCloseHdr (CompanyCode, BranchCode, Status)
        INCLUDE (PeriodTo);
GO

-- ── Balances (derived snapshot) ────────────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.IvPeriodCloseBal', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IvPeriodCloseBal
    (
        ID                  int           IDENTITY(1,1) NOT NULL,
        PeriodCloseId       int           NOT NULL,

        CompanyCode         nvarchar(5)   NOT NULL,
        BranchCode          nvarchar(5)   NOT NULL,
        ICode               nvarchar(30)  NOT NULL,
        WhCode              nvarchar(20)  NOT NULL,
        LocCode             nvarchar(10)  NOT NULL CONSTRAINT DF_IvPeriodCloseBal_LocCode DEFAULT (''),
        LotNo               nvarchar(50)  NOT NULL CONSTRAINT DF_IvPeriodCloseBal_LotNo DEFAULT (''),
        IStatus             nvarchar(10)  NOT NULL CONSTRAINT DF_IvPeriodCloseBal_IStatus DEFAULT (''),

        -- Ledger replay + the first-close plug (all 4 dp via IvQty.Round).
        OpeningQty          decimal(18,4) NOT NULL CONSTRAINT DF_IvPeriodCloseBal_OpeningQty DEFAULT (0),
        OpeningAdjustQty    decimal(18,4) NOT NULL CONSTRAINT DF_IvPeriodCloseBal_OpeningAdjust DEFAULT (0),
        InQty               decimal(18,4) NOT NULL CONSTRAINT DF_IvPeriodCloseBal_InQty DEFAULT (0),
        OutQty              decimal(18,4) NOT NULL CONSTRAINT DF_IvPeriodCloseBal_OutQty DEFAULT (0),
        AdjustNetQty        decimal(18,4) NOT NULL CONSTRAINT DF_IvPeriodCloseBal_AdjustNet DEFAULT (0),
        ClosingQty          decimal(18,4) NOT NULL CONSTRAINT DF_IvPeriodCloseBal_ClosingQty DEFAULT (0),

        StdUom              nvarchar(10)  NULL,
        UnitPrice           decimal(18,4) NOT NULL CONSTRAINT DF_IvPeriodCloseBal_UnitPrice DEFAULT (0),
        ClosingValue        decimal(18,4) NOT NULL CONSTRAINT DF_IvPeriodCloseBal_ClosingValue DEFAULT (0),

        LegCount            int           NOT NULL CONSTRAINT DF_IvPeriodCloseBal_LegCount DEFAULT (0),
        CarryForwardOk      bit           NOT NULL CONSTRAINT DF_IvPeriodCloseBal_CarryForwardOk DEFAULT (1),
        CurrentBalanceDelta decimal(18,4) NULL,   -- NULL when the D11 check did not apply

        Created             datetime      NULL,
        UserID              nvarchar(10)  NULL,

        CONSTRAINT PK_IvPeriodCloseBal PRIMARY KEY CLUSTERED (ID),
        CONSTRAINT FK_IvPeriodCloseBal_Hdr FOREIGN KEY (PeriodCloseId)
            REFERENCES dbo.IvPeriodCloseHdr (ID) ON DELETE NO ACTION
    );
END
GO

-- One snapshot line per stock slice per closed period (D3/D13).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IvPeriodCloseBal_Slice' AND object_id = OBJECT_ID(N'dbo.IvPeriodCloseBal'))
    CREATE UNIQUE INDEX UQ_IvPeriodCloseBal_Slice
        ON dbo.IvPeriodCloseBal (PeriodCloseId, ICode, WhCode, LocCode, LotNo, IStatus);
GO

-- ── Verification ───────────────────────────────────────────────────────────────────────────────
DECLARE @hdrCount int = (SELECT COUNT(*) FROM sys.tables WHERE name = N'IvPeriodCloseHdr');
DECLARE @balCount int = (SELECT COUNT(*) FROM sys.tables WHERE name = N'IvPeriodCloseBal');
DECLARE @hdrIdx int = (SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.IvPeriodCloseHdr') AND name IN (N'UQ_IvPeriodCloseHdr_Period', N'IX_IvPeriodCloseHdr_Tenant_Status'));
DECLARE @balIdx int = (SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.IvPeriodCloseBal') AND name = N'UQ_IvPeriodCloseBal_Slice');
DECLARE @fkCount int = (SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_IvPeriodCloseBal_Hdr');

PRINT 'IvPeriodCloseHdr table(s): ' + CAST(@hdrCount AS varchar(10)) + ' (expect 1)';
PRINT 'IvPeriodCloseBal table(s): ' + CAST(@balCount AS varchar(10)) + ' (expect 1)';
PRINT 'IvPeriodCloseHdr indexes:  ' + CAST(@hdrIdx AS varchar(10)) + ' (expect 2)';
PRINT 'IvPeriodCloseBal indexes:  ' + CAST(@balIdx AS varchar(10)) + ' (expect 1)';
PRINT 'IvPeriodCloseBal foreign key(s): ' + CAST(@fkCount AS varchar(10)) + ' (expect 1)';
GO
