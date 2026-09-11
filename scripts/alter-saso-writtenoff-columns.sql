-- Patch: add SaSODetail.WrittenOffQty (R3 write-off model, Phase 2).
-- A force-closed DO writes off the unallocated remainder of its SO line. The column is monotonic
-- and additive; it is written only by SaDoService.ForceCloseOneAsync.
-- Safe to re-run. Usage: sqlcmd -S .\SQLEXPRESS -d ERPWeb -E -i scripts\alter-saso-writtenoff-columns.sql
GO

IF OBJECT_ID(N'dbo.SaSODetail', N'U') IS NULL
    RAISERROR(N'SaSODetail missing.', 16, 1);
GO

IF COL_LENGTH(N'dbo.SaSODetail', N'WrittenOffQty') IS NULL
BEGIN
    ALTER TABLE dbo.SaSODetail ADD WrittenOffQty decimal(18,4) NOT NULL
        CONSTRAINT DF_SaSODetail_WrittenOffQty DEFAULT (0);
    PRINT N'Added SaSODetail.WrittenOffQty';
END
ELSE
    PRINT N'SaSODetail.WrittenOffQty already exists';
GO

-- I4: WrittenOffQty >= 0
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_SaSODetail_WrittenOffQty' AND parent_object_id = OBJECT_ID(N'dbo.SaSODetail'))
    ALTER TABLE dbo.SaSODetail ADD CONSTRAINT CK_SaSODetail_WrittenOffQty CHECK (WrittenOffQty >= 0);
GO
