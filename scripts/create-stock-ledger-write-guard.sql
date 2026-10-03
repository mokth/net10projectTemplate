/*
  Additive, rerunnable branch-epoch write guard. Install before activation.
  Legacy binaries have no STOCK_LEDGER_V2_POSTING_ID session context and cannot
  change live stock or append facts for an ACTIVE branch. V2 writers must set the
  context to their unsealed posting ID within the same transaction/connection.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.StockLedgerEpoch', N'U') IS NULL OR OBJECT_ID(N'dbo.StockPosting', N'U') IS NULL
    THROW 51009, 'Install stock ledger schema before its write guards.', 1;

DECLARE @Targets table (TableName sysname NOT NULL PRIMARY KEY);
INSERT INTO @Targets (TableName) VALUES
    (N'IvBalLoc'), (N'IvTrxHistory'),
    (N'PrProductionBalLot'), (N'PrProductionBalLotMovement'),
    (N'PrMaterialMovement'), (N'PrProductionMovementAllocation');

DECLARE @TableName sysname;
DECLARE @Sql nvarchar(max);
DECLARE target_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT TableName FROM @Targets ORDER BY TableName;
OPEN target_cursor;
FETCH NEXT FROM target_cursor INTO @TableName;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'dbo.' + @TableName, N'U') IS NULL
        THROW 51010, 'A guarded stock table is missing.', 1;

    SET @Sql = N'CREATE OR ALTER TRIGGER dbo.' + QUOTENAME(N'TR_' + @TableName + N'_V2WriteGuard')
        + N' ON dbo.' + QUOTENAME(@TableName) + N' AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS
    (
        SELECT 1
        FROM (SELECT CompanyCode, BranchCode FROM inserted
              UNION SELECT CompanyCode, BranchCode FROM deleted) AS touched
        INNER JOIN dbo.StockLedgerEpoch AS epoch
            ON epoch.CompanyCode = touched.CompanyCode
           AND epoch.BranchCode = touched.BranchCode
           AND epoch.Status = N''ACTIVE''
        WHERE NOT EXISTS
        (
            SELECT 1 FROM dbo.StockPosting AS posting
            WHERE posting.Id = TRY_CONVERT(bigint, SESSION_CONTEXT(N''STOCK_LEDGER_V2_POSTING_ID''))
              AND posting.CompanyCode = touched.CompanyCode
              AND posting.BranchCode = touched.BranchCode
              AND posting.LedgerEpochId = epoch.Id
              AND posting.SealedAtUtc IS NULL
        )
    )
        THROW 51011, ''An active stock-ledger branch requires an unsealed V2 posting write context.'', 1;
END';
    EXEC sys.sp_executesql @Sql;
    FETCH NEXT FROM target_cursor INTO @TableName;
END;
CLOSE target_cursor;
DEALLOCATE target_cursor;
