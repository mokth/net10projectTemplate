-- Sales Order revision migration (manual DBA script — do NOT run at app startup).
-- Transforms Phase 1 PK (Company, Branch, SONo) into (Company, Branch, SONo, CustRel).
-- Stops if unexpected FKs/indexes reference the old PK.
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.SaSO', N'U') IS NULL
BEGIN
    RAISERROR(N'SaSO does not exist. Run create-saso.sql for greenfield.', 16, 1);
    RETURN;
END
GO

-- 1) Dependency inventory: only FK_SaSODetail_SaSO (or no FKs) may reference SaSO PK.
DECLARE @unexpected nvarchar(max) = N'';
SELECT @unexpected = @unexpected + OBJECT_SCHEMA_NAME(fk.parent_object_id) + N'.' + OBJECT_NAME(fk.parent_object_id)
    + N' via ' + fk.name + N'; '
FROM sys.foreign_keys fk
WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.SaSO')
  AND fk.name <> N'FK_SaSODetail_SaSO';

IF LEN(@unexpected) > 0
BEGIN
    RAISERROR(N'Unclassified FK(s) reference SaSO. Classify before continuing: %s', 16, 1, @unexpected);
    RETURN;
END
GO

BEGIN TRANSACTION;

-- 2) Add nullable revision columns if missing
IF COL_LENGTH(N'dbo.SaSO', N'CustRel') IS NULL
    ALTER TABLE dbo.SaSO ADD CustRel smallint NULL;
IF COL_LENGTH(N'dbo.SaSO', N'IsCurrent') IS NULL
    ALTER TABLE dbo.SaSO ADD IsCurrent bit NULL;
IF COL_LENGTH(N'dbo.SaSO', N'LastCustRel') IS NULL
    ALTER TABLE dbo.SaSO ADD LastCustRel smallint NULL;
IF COL_LENGTH(N'dbo.SaSO', N'RevisionReason') IS NULL
    ALTER TABLE dbo.SaSO ADD RevisionReason nvarchar(200) NULL;
GO

-- 3) Backfill
UPDATE dbo.SaSO
SET CustRel = ISNULL(CustRel, 1),
    IsCurrent = ISNULL(IsCurrent, 1),
    LastCustRel = ISNULL(LastCustRel, 1),
    RevisionReason = RevisionReason
WHERE CustRel IS NULL OR IsCurrent IS NULL OR LastCustRel IS NULL;
GO

-- Align detail CustRel with header (Phase 1: always 1)
IF COL_LENGTH(N'dbo.SaSODetail', N'CustRel') IS NULL
    ALTER TABLE dbo.SaSODetail ADD CustRel smallint NOT NULL CONSTRAINT DF_SaSODetail_CustRel_Mig DEFAULT (1);
ELSE
    UPDATE dbo.SaSODetail SET CustRel = 1 WHERE CustRel IS NULL OR CustRel = 0;
GO

-- 4) Validate one header per SoNo and no duplicate current
IF EXISTS (
    SELECT CompanyCode, BranchCode, SONo
    FROM dbo.SaSO
    GROUP BY CompanyCode, BranchCode, SONo
    HAVING COUNT(*) > 1)
BEGIN
    ROLLBACK TRANSACTION;
    RAISERROR(N'Duplicate SaSO rows per (Company, Branch, SONo). Resolve before revision PK migration.', 16, 1);
    RETURN;
END

IF EXISTS (
    SELECT CompanyCode, BranchCode, SONo
    FROM dbo.SaSO
    WHERE IsCurrent = 1
    GROUP BY CompanyCode, BranchCode, SONo
    HAVING COUNT(*) > 1)
BEGIN
    ROLLBACK TRANSACTION;
    RAISERROR(N'Multiple IsCurrent=1 rows for a SoNo before migration.', 16, 1);
    RETURN;
END
GO

-- 5) Drop old FK then old header PK
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SaSODetail_SaSO')
    ALTER TABLE dbo.SaSODetail DROP CONSTRAINT FK_SaSODetail_SaSO;

IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = N'PK_SaSO' AND parent_object_id = OBJECT_ID(N'dbo.SaSO'))
    ALTER TABLE dbo.SaSO DROP CONSTRAINT PK_SaSO;
GO

-- Drop old detail PK and obsolete SoNo+Line unique
IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = N'PK_SaSODetail' AND parent_object_id = OBJECT_ID(N'dbo.SaSODetail'))
    ALTER TABLE dbo.SaSODetail DROP CONSTRAINT PK_SaSODetail;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SaSODetail_Company_Branch_SoNo_Line' AND object_id = OBJECT_ID(N'dbo.SaSODetail'))
    DROP INDEX UQ_SaSODetail_Company_Branch_SoNo_Line ON dbo.SaSODetail;
GO

-- 6) NOT NULL
ALTER TABLE dbo.SaSO ALTER COLUMN CustRel smallint NOT NULL;
ALTER TABLE dbo.SaSO ALTER COLUMN IsCurrent bit NOT NULL;
ALTER TABLE dbo.SaSO ALTER COLUMN LastCustRel smallint NOT NULL;
ALTER TABLE dbo.SaSODetail ALTER COLUMN CustRel smallint NOT NULL;
GO

-- 7) New header PK
ALTER TABLE dbo.SaSO
    ADD CONSTRAINT PK_SaSO PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, SONo, CustRel);
GO

-- 8) New detail PK + FK
ALTER TABLE dbo.SaSODetail
    ADD CONSTRAINT PK_SaSODetail PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, SONo, CustRel, Line);

ALTER TABLE dbo.SaSODetail
    ADD CONSTRAINT FK_SaSODetail_SaSO FOREIGN KEY (CompanyCode, BranchCode, SONo, CustRel)
        REFERENCES dbo.SaSO (CompanyCode, BranchCode, SONo, CustRel) ON DELETE CASCADE;
GO

-- 9) Filtered unique current
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SaSO_Current' AND object_id = OBJECT_ID(N'dbo.SaSO'))
    CREATE UNIQUE INDEX UX_SaSO_Current
    ON dbo.SaSO (CompanyCode, BranchCode, SONo)
    WHERE IsCurrent = 1;
GO

-- 10) Replace CHECKs
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SaSO_Status_ClosedReason' AND parent_object_id = OBJECT_ID(N'dbo.SaSO'))
    ALTER TABLE dbo.SaSO DROP CONSTRAINT CK_SaSO_Status_ClosedReason;

ALTER TABLE dbo.SaSO ADD CONSTRAINT CK_SaSO_Status_ClosedReason CHECK (
    (Status IN (N'NEW', N'SHIPPED', N'SUPERSEDED') AND ClosedReason IS NULL)
    OR
    (Status = N'CLOSED' AND ClosedReason IN (N'FULLY_CONSUMED', N'FORCE_CLOSED'))
);

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SaSO_IsCurrent_Status' AND parent_object_id = OBJECT_ID(N'dbo.SaSO'))
    ALTER TABLE dbo.SaSO DROP CONSTRAINT CK_SaSO_IsCurrent_Status;

ALTER TABLE dbo.SaSO ADD CONSTRAINT CK_SaSO_IsCurrent_Status CHECK (
    (IsCurrent = 1 AND Status IN (N'NEW', N'SHIPPED', N'CLOSED'))
    OR
    (IsCurrent = 0 AND Status = N'SUPERSEDED')
);
GO

-- 11) End assertions
IF EXISTS (
    SELECT CompanyCode, BranchCode, SONo
    FROM dbo.SaSO
    WHERE IsCurrent = 1
    GROUP BY CompanyCode, BranchCode, SONo
    HAVING COUNT(*) > 1)
BEGIN
    ROLLBACK TRANSACTION;
    RAISERROR(N'Assertion failed: >1 current revision.', 16, 1);
    RETURN;
END

IF EXISTS (
    SELECT h.CompanyCode, h.BranchCode, h.SONo
    FROM (SELECT DISTINCT CompanyCode, BranchCode, SONo FROM dbo.SaSO) h
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.SaSO c
        WHERE c.CompanyCode = h.CompanyCode AND c.BranchCode = h.BranchCode AND c.SONo = h.SONo AND c.IsCurrent = 1))
BEGIN
    ROLLBACK TRANSACTION;
    RAISERROR(N'Assertion failed: live SO chain with 0 current.', 16, 1);
    RETURN;
END

IF EXISTS (SELECT 1 FROM dbo.SaSO WHERE CustRel IS NULL OR LastCustRel IS NULL OR IsCurrent IS NULL)
BEGIN
    ROLLBACK TRANSACTION;
    RAISERROR(N'Assertion failed: NULL revision columns.', 16, 1);
    RETURN;
END

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SaSODetail_Company_Branch_SoNo_Line' AND object_id = OBJECT_ID(N'dbo.SaSODetail'))
BEGIN
    ROLLBACK TRANSACTION;
    RAISERROR(N'Assertion failed: old SoNo+Line unique still exists.', 16, 1);
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = N'PK_SaSO' AND parent_object_id = OBJECT_ID(N'dbo.SaSO'))
   OR NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = N'PK_SaSODetail' AND parent_object_id = OBJECT_ID(N'dbo.SaSODetail'))
   OR NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SaSO_Current' AND object_id = OBJECT_ID(N'dbo.SaSO'))
BEGIN
    ROLLBACK TRANSACTION;
    RAISERROR(N'Assertion failed: new PK or UX_SaSO_Current missing.', 16, 1);
    RETURN;
END

IF EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = N'FK_SaSODetail_SaSO' AND (is_disabled = 1 OR is_not_trusted = 1))
BEGIN
    ROLLBACK TRANSACTION;
    RAISERROR(N'Assertion failed: FK_SaSODetail_SaSO not enabled/trusted.', 16, 1);
    RETURN;
END

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.SaSO')
      AND name IN (N'CK_SaSO_Status_ClosedReason', N'CK_SaSO_IsCurrent_Status')
      AND (is_disabled = 1 OR is_not_trusted = 1))
BEGIN
    ROLLBACK TRANSACTION;
    RAISERROR(N'Assertion failed: CHECK constraints not enabled/trusted.', 16, 1);
    RETURN;
END

COMMIT TRANSACTION;
PRINT N'alter-saso-revision.sql completed successfully.';
GO
