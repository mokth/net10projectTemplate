-- Inventory Stock Count (cycle count) — additive DDL.
--
-- MANUAL DBA SCRIPT — do NOT run at application startup. Idempotent: safe to re-run (every block is
-- guarded on its own object). Target database: the same database as ConnectionStrings:DefaultConnection
-- (ERPWeb). Run with:  sqlcmd -E -d ERPWeb -i scripts/create-iv-stock-count.sql
--
-- WHAT THIS CREATES
--   dbo.IvStockCountHdr  — the physical-count sheet header (one row per count document).
--   dbo.IvStockCountLine — one row per countable pile (IvBalLoc) on that sheet.
--   Nothing else. A count posts through the EXISTING ADJ engine, so there is no new posting table
--   and no new transaction type: the generated IvTrxBatch has TrxType = 'ADJ' and RefNo = CountNo.
--
-- BATCHING RULES (learned the hard way in this repo)
--   * CREATE TABLE and each CREATE INDEX live in SEPARATE batches: SQL Server compiles a whole batch
--     before executing any of it, so a later failure under SET XACT_ABORT ON would otherwise roll the
--     earlier statements of the same batch back and a re-run could not progress.
--   * SET QUOTED_IDENTIFIER ON is set here explicitly, because a filtered index (and any
--     FOR XML verification query) fails with a confusing Msg 1934 otherwise.
--   * A guarded RETURN stops only its OWN batch — every block below checks for itself.

SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;
GO

-- ── Header ──────────────────────────────────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.IvStockCountHdr', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IvStockCountHdr
    (
        ID                int           IDENTITY(1,1) NOT NULL,

        CompanyCode       nvarchar(5)   NOT NULL,
        BranchCode        nvarchar(5)   NOT NULL,
        CountNo           nvarchar(30)  NOT NULL,   -- 'CC000001' (never an SC… prefix: SC is IvTrxTypes.Scrap)
        CountDate         datetime2     NOT NULL,   -- the physical-count business date; becomes the ADJ batch TrxDtTime
        Status            nvarchar(20)  NOT NULL,   -- DRAFT / COUNTED / POSTED / ROLLED_BACK / CANCELLED

        -- Generate scope stamps, so a sheet can be re-edited and re-printed.
        WHCode            nvarchar(20)  NULL,
        LocCode           nvarchar(10)  NULL,
        IClassCode        nvarchar(10)  NULL,
        ISubClassCode     nvarchar(10)  NULL,
        IType             nvarchar(20)  NULL,
        IStatus           nvarchar(20)  NULL,       -- comma-separated status scope; SCRAPS excluded by default
        ICodeList         nvarchar(1000) NULL,      -- optional item IN-list
        IncludeZeroQty    bit           NOT NULL CONSTRAINT DF_IvStockCountHdr_IncludeZeroQty DEFAULT (1),
        CountedBy         nvarchar(10)  NULL,
        Remark            nvarchar(250) NULL,

        -- Posting audit. PostedBatchNo is NULL only for the all-zero-variance post.
        PostedBatchNo     int           NULL,
        PostedBy          nvarchar(10)  NULL,
        PostedOn          datetime2     NULL,
        PostedStaleLines  int           NULL,       -- how many lines were stale when posted (0 = none)

        -- Rollback audit, kept across a re-post.
        RolledBackBy      nvarchar(10)  NULL,
        RolledBackOn      datetime2     NULL,
        RollbackReason    nvarchar(250) NULL,

        Created           datetime      NULL,
        UserID            nvarchar(10)  NULL,
        Updated           datetime      NULL,
        UpdatedUID        nvarchar(10)  NULL,
        RowVersion        rowversion    NOT NULL,

        CONSTRAINT PK_IvStockCountHdr PRIMARY KEY CLUSTERED (ID)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IvStockCountHdr_No' AND object_id = OBJECT_ID(N'dbo.IvStockCountHdr'))
    CREATE UNIQUE INDEX UQ_IvStockCountHdr_No
        ON dbo.IvStockCountHdr (CompanyCode, BranchCode, CountNo);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IvStockCountHdr_Status' AND object_id = OBJECT_ID(N'dbo.IvStockCountHdr'))
    CREATE INDEX IX_IvStockCountHdr_Status
        ON dbo.IvStockCountHdr (CompanyCode, BranchCode, Status);
GO

-- ── Lines ──────────────────────────────────────────────────────────────────────────────────────
-- The line carries NO CompanyCode/BranchCode column: it inherits the header's, and the service
-- re-validates that BalLocId belongs to that company AND branch at Generate, SaveCounts and Post.
IF OBJECT_ID(N'dbo.IvStockCountLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IvStockCountLine
    (
        ID                 int           IDENTITY(1,1) NOT NULL,
        StockCountId       int           NOT NULL,
        -- Named LineNumber, not LineNo: SQL Server 2022 (compat level 160) treats a bare LineNo as a
        -- keyword, so the column could not be created or referenced unquoted.
        LineNumber         smallint      NOT NULL,

        BalLocId           int           NOT NULL,  -- the pile: the only posting identity the ADJ engine needs

        -- Denormalised display / slice-match columns, read from the balance at Generate.
        ICode              nvarchar(30)  NOT NULL,
        IDesc              nvarchar(200) NULL,
        WHCode             nvarchar(20)  NULL,
        LocCode            nvarchar(10)  NULL,
        LotNo              nvarchar(50)  NULL,
        IStatus            nvarchar(20)  NOT NULL,
        IClassCode         nvarchar(10)  NULL,
        StdUom             nvarchar(10)  NULL,
        ExpiryDate         datetime2     NULL,

        SystemQty          decimal(18,4) NOT NULL,  -- live qty at Generate: EVIDENCE, never a posting input
        PhysicalQty        decimal(18,4) NULL,      -- NULL = not counted => excluded from posting
        SnapshotUnitPrice  decimal(18,4) NULL,      -- EVIDENCE ONLY: posting prices from the locked balance

        RecountCount       smallint      NOT NULL CONSTRAINT DF_IvStockCountLine_RecountCount DEFAULT (0),
        CountedBy          nvarchar(10)  NULL,
        CountedOn          datetime2     NULL,
        RowVersion         rowversion    NOT NULL,

        CONSTRAINT PK_IvStockCountLine PRIMARY KEY CLUSTERED (ID),
        CONSTRAINT FK_IvStockCountLine_Hdr FOREIGN KEY (StockCountId)
            REFERENCES dbo.IvStockCountHdr (ID) ON DELETE NO ACTION
    );
END
GO

-- One line per pile. This is what makes the post-time net-per-BalLoc unambiguous.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IvStockCountLine_BalLoc' AND object_id = OBJECT_ID(N'dbo.IvStockCountLine'))
    CREATE UNIQUE INDEX UQ_IvStockCountLine_BalLoc
        ON dbo.IvStockCountLine (StockCountId, BalLocId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IvStockCountLine_No' AND object_id = OBJECT_ID(N'dbo.IvStockCountLine'))
    CREATE UNIQUE INDEX UQ_IvStockCountLine_No
        ON dbo.IvStockCountLine (StockCountId, LineNumber);
GO

-- ── Verification ───────────────────────────────────────────────────────────────────────────────
SET NOCOUNT ON;

SELECT
    TableName = t.name,
    IsPresent = CONVERT(bit, 1)
FROM sys.tables t
WHERE t.name IN (N'IvStockCountHdr', N'IvStockCountLine');

SELECT
    IndexName = i.name,
    TableName = OBJECT_NAME(i.object_id),
    IsUnique = i.is_unique
FROM sys.indexes i
WHERE i.object_id IN (OBJECT_ID(N'dbo.IvStockCountHdr'), OBJECT_ID(N'dbo.IvStockCountLine'))
  AND i.name IN (N'UQ_IvStockCountHdr_No', N'IX_IvStockCountHdr_Status',
                 N'UQ_IvStockCountLine_BalLoc', N'UQ_IvStockCountLine_No')
ORDER BY TableName, IndexName;

SELECT
    ForeignKeyName = fk.name,
    TableName = OBJECT_NAME(fk.parent_object_id)
FROM sys.foreign_keys fk
WHERE fk.name = N'FK_IvStockCountLine_Hdr';

PRINT N'create-iv-stock-count.sql: expected 2 tables, 4 indexes (2 unique) and 1 foreign key above.';
GO
