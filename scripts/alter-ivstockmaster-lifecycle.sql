-- Deployment artifact: Stock Item lifecycle (ACTIVE -> DISCONTINUED -> INACTIVE).
-- Do NOT run automatically at application startup. Apply through the normal SQL deploy process.
--
-- Additive only:
--   * IvStockMaster.IsDiscontinued  bit NOT NULL DEFAULT 0  (legacy rows untouched)
--   * IvStockMaster.DiscontinuedAtUtc datetime2 NULL
--   * CK_IvStockMaster_LifecycleState rejects (Active = 0 AND IsDiscontinued = 1)
--   * IvStockMasterLifecycleEvent append-only audit table (no FK, no cascade)
--
-- Safe to re-run: every object is created only when missing and no existing business value is
-- rewritten. Existing Active values, item codes, quantities and costing history are never changed.
-- Rollback: the pre-upgrade binary ignores the new column/table, so the schema may stay in place.

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

---------------------------------------------------------------------------------------------------
-- Preflight: the target table must exist and must not already carry an incompatible column type.
---------------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.IvStockMaster', N'U') IS NULL
BEGIN
    THROW 50001, 'Preflight failed: dbo.IvStockMaster does not exist in this database.', 1;
END
GO

IF COL_LENGTH('dbo.IvStockMaster', 'IsDiscontinued') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1
        FROM sys.columns c
        JOIN sys.types t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = OBJECT_ID(N'dbo.IvStockMaster')
          AND c.name = N'IsDiscontinued'
          AND t.name = N'bit'
          AND c.is_nullable = 0)
BEGIN
    THROW 50002, 'Preflight failed: dbo.IvStockMaster.IsDiscontinued exists but is not bit NOT NULL.', 1;
END
GO

---------------------------------------------------------------------------------------------------
-- 1. Columns.
---------------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.IvStockMaster', 'IsDiscontinued') IS NULL
BEGIN
    ALTER TABLE dbo.IvStockMaster
        ADD IsDiscontinued bit NOT NULL
            CONSTRAINT DF_IvStockMaster_IsDiscontinued DEFAULT (0);
END
GO

IF COL_LENGTH('dbo.IvStockMaster', 'DiscontinuedAtUtc') IS NULL
BEGIN
    ALTER TABLE dbo.IvStockMaster
        ADD DiscontinuedAtUtc datetime2(7) NULL;
END
GO

---------------------------------------------------------------------------------------------------
-- 2. Valid-state CHECK constraint. Existing data can never violate it: IsDiscontinued defaults to 0.
---------------------------------------------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1
    FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.IvStockMaster')
      AND name = N'CK_IvStockMaster_LifecycleState')
BEGIN
    ALTER TABLE dbo.IvStockMaster WITH CHECK
        ADD CONSTRAINT CK_IvStockMaster_LifecycleState
        CHECK (NOT ([Active] = 0 AND [IsDiscontinued] = 1));

    ALTER TABLE dbo.IvStockMaster
        CHECK CONSTRAINT CK_IvStockMaster_LifecycleState;
END
GO

---------------------------------------------------------------------------------------------------
-- 3. Supporting index for lifecycle filtering.
---------------------------------------------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.IvStockMaster')
      AND name = N'IX_IvStockMaster_Company_Discontinued')
BEGIN
    CREATE INDEX IX_IvStockMaster_Company_Discontinued
        ON dbo.IvStockMaster (CompanyCode, IsDiscontinued);
END
GO

---------------------------------------------------------------------------------------------------
-- 4. Append-only lifecycle audit. No FK / no cascade: the evidence must survive item deletion.
---------------------------------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.IvStockMasterLifecycleEvent', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IvStockMasterLifecycleEvent
    (
        Id              bigint IDENTITY(1,1) NOT NULL,
        CompanyCode     nvarchar(5)  NOT NULL,
        ICode           nvarchar(30) NOT NULL,
        OldStatus       nvarchar(16) NOT NULL,
        NewStatus       nvarchar(16) NOT NULL,
        OccurredAtUtc   datetime2(7) NOT NULL,
        ActorUserId     nvarchar(10) NOT NULL,
        Reason          nvarchar(500) NOT NULL,
        SourceAction    nvarchar(20) NOT NULL,
        CorrelationId   nvarchar(64) NULL,
        CONSTRAINT PK_IvStockMasterLifecycleEvent PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT CK_IvStockMasterLifecycleEvent_Status
            CHECK (OldStatus IN (N'ACTIVE', N'DISCONTINUED', N'INACTIVE')
               AND NewStatus IN (N'ACTIVE', N'DISCONTINUED', N'INACTIVE')),
        CONSTRAINT CK_IvStockMasterLifecycleEvent_Change
            CHECK (OldStatus <> NewStatus),
        CONSTRAINT CK_IvStockMasterLifecycleEvent_Source
            CHECK (SourceAction IN (N'ITEM_FORM', N'BULK', N'SYSTEM')),
        CONSTRAINT CK_IvStockMasterLifecycleEvent_Reason
            CHECK (LEN(LTRIM(RTRIM(Reason))) > 0)
    );

    CREATE INDEX IX_IvStockMasterLifecycleEvent_Company_Item_Time
        ON dbo.IvStockMasterLifecycleEvent (CompanyCode, ICode, OccurredAtUtc);
END
GO

---------------------------------------------------------------------------------------------------
-- 5. Post-deployment verification (expected results).
---------------------------------------------------------------------------------------------------
-- 5a. Exactly one row, type bit, not nullable:
SELECT c.name, t.name AS SqlType, c.is_nullable, c.max_length
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.IvStockMaster')
  AND c.name IN (N'IsDiscontinued', N'DiscontinuedAtUtc', N'Active');

-- 5b. Row count and Active distribution must match the pre-deployment baseline:
SELECT COUNT_BIG(*) AS TotalItems,
       SUM(CASE WHEN Active = 1 THEN 1 ELSE 0 END) AS ActiveItems,
       SUM(CASE WHEN Active = 0 THEN 1 ELSE 0 END) AS InactiveItems,
       SUM(CASE WHEN IsDiscontinued = 1 THEN 1 ELSE 0 END) AS DiscontinuedItems
FROM dbo.IvStockMaster;

-- 5c. Must return 0 rows (invalid combined state):
SELECT CompanyCode, ICode, Active, IsDiscontinued
FROM dbo.IvStockMaster
WHERE Active = 0 AND IsDiscontinued = 1;

-- 5d. Audit table and indexes exist:
SELECT i.name
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID(N'dbo.IvStockMasterLifecycleEvent');
GO
