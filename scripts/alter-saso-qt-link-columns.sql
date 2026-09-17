-- Sales Quotation -> Sales Order source stamps (MVP conversion is by stamp, not by
-- SaDocApplication allocation; see plans/sa-quotation_88ba372e.plan.md).
-- Manual DBA script. Do NOT run at app startup. Idempotent: safe to re-run.
--
-- One SO per QT revision in MVP is enforced by UX_SaSO_QtSource: the source identity
-- (CompanyCode, BranchCode, QTNo, QTCustRel) is unique across non-null stamps.
-- Partial conversion would need this index relaxed; that is deliberately out of MVP scope.
--
-- The filtered index UX_SaSO_QtSource (WHERE QTNo IS NOT NULL) requires ANSI_NULLS ON and
-- QUOTED_IDENTIFIER ON. SSMS sets both, but sqlcmd defaults QUOTED_IDENTIFIER OFF, which makes
-- CREATE INDEX ... WHERE fail with Msg 1934. Set them explicitly so the script behaves the same
-- no matter which client runs it.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── Header stamps ─────────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.SaSO', N'U') IS NULL
BEGIN
    RAISERROR(N'SaSO does not exist. Run scripts/create-saso.sql for empty databases.', 16, 1);
    RETURN;
END
GO

IF COL_LENGTH(N'dbo.SaSO', N'QTNo') IS NULL
    ALTER TABLE dbo.SaSO ADD QTNo nvarchar(30) NULL;
GO

IF COL_LENGTH(N'dbo.SaSO', N'QTCustRel') IS NULL
    ALTER TABLE dbo.SaSO ADD QTCustRel smallint NULL;
GO

-- ── Detail stamps ─────────────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.SaSODetail', N'U') IS NULL
BEGIN
    RAISERROR(N'SaSODetail does not exist. Run scripts/create-saso.sql for empty databases.', 16, 1);
    RETURN;
END
GO

IF COL_LENGTH(N'dbo.SaSODetail', N'QTNo') IS NULL
    ALTER TABLE dbo.SaSODetail ADD QTNo nvarchar(30) NULL;
GO

IF COL_LENGTH(N'dbo.SaSODetail', N'QTLine') IS NULL
    ALTER TABLE dbo.SaSODetail ADD QTLine smallint NULL;
GO

IF COL_LENGTH(N'dbo.SaSODetail', N'QTCustRel') IS NULL
    ALTER TABLE dbo.SaSODetail ADD QTCustRel smallint NULL;
GO

-- Name parallels the existing SaDODetail.SoConsumedQty / SaInvoiceDetail.SoConsumedQty convention.
IF COL_LENGTH(N'dbo.SaSODetail', N'QtConsumedQty') IS NULL
    ALTER TABLE dbo.SaSODetail ADD QtConsumedQty decimal(18,4) NOT NULL
        CONSTRAINT DF_SaSODetail_QtConsumedQty DEFAULT (0);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_SaSODetail_QtConsumedQty' AND parent_object_id = OBJECT_ID(N'dbo.SaSODetail'))
    ALTER TABLE dbo.SaSODetail ADD CONSTRAINT CK_SaSODetail_QtConsumedQty CHECK (QtConsumedQty >= 0);
GO

-- ── Duplicate-conversion guard ────────────────────────────────────────────────
-- One SO per QT revision. Collation/type of QTNo matches dbo.SaQT.QTNo (nvarchar(30)).
IF EXISTS (
    SELECT 1
    FROM dbo.SaSO
    WHERE QTNo IS NOT NULL
    GROUP BY CompanyCode, BranchCode, QTNo, QTCustRel
    HAVING COUNT(*) > 1)
BEGIN
    RAISERROR(N'UX_SaSO_QtSource cannot be created: duplicate QT source stamps already exist.', 16, 1);
    RETURN;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_SaSO_QtSource' AND object_id = OBJECT_ID(N'dbo.SaSO'))
    CREATE UNIQUE INDEX UX_SaSO_QtSource
    ON dbo.SaSO (CompanyCode, BranchCode, QTNo, QTCustRel)
    WHERE QTNo IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaSODetail_Company_Branch_QTNo_QTLine' AND object_id = OBJECT_ID(N'dbo.SaSODetail'))
    CREATE INDEX IX_SaSODetail_Company_Branch_QTNo_QTLine
    ON dbo.SaSODetail (CompanyCode, BranchCode, QTNo, QTLine);
GO
