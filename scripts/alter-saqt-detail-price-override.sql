/* ============================================================================================
   alter-saqt-detail-price-override.sql
   Plan: sales-pricing-engine, Phase 4 (price-override governance) — SaQtDetail addition.

   WHY THIS SCRIPT IS SEPARATE FROM alter-sa-detail-price-override.sql
   ---------------------------------------------------------------------------------------------
   The Phase 4 migration script covered the four POSTING documents (SaSoDetail, SaDoDetail,
   SaInvoiceDetail, SaCdnDetail). SaQTDetail was deliberately left out at the time because a
   quotation is a proposal rather than a posted financial document.

   That exclusion left an ungoverned editable price: SaQt.razor exposes UnitPrice on the line popup
   with no PRICE_OVERRIDE gate, while every other sales document now enforces one. A quotation is
   in fact the FIRST place a price is offered, which makes it the most valuable place to govern.
   This script closes that gap.

   SHAPE (identical to the four posting documents)
   ---------------------------------------------------------------------------------------------
     OriginalUnitPrice  decimal(18,4)  NULL   -- engine price, retained ONLY on a real override
     OverrideReason     nvarchar(100)  NULL   -- why it was changed; required on any override

   NULL means "never overridden" — it does NOT mean "resolved to zero". The service normalises a
   line that merely echoes the resolved price back to NULL, so the two cannot be confused.

   SAFETY
   ---------------------------------------------------------------------------------------------
   Additive and idempotent: both columns are guarded with COL_LENGTH. No data is touched, no
   constraint is dropped, and existing rows are left NULL (correct: they predate the feature and
   were never recorded as overrides).
   ============================================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.SaQTDetail', 'U') IS NULL
BEGIN
    RAISERROR('STOP: dbo.SaQTDetail does not exist in this database. Point at the right database with -d before running.', 16, 1);
END
GO

/* NOTE: RETURN exits only its own batch, so the guard above CANNOT stop the batches below.
   Every ALTER below therefore repeats the table-existence check itself - that is belt-and-braces,
   not redundancy. (This exact trap already cost a run once in init-sales-price-inquiry-menu.sql.) */

/* ---- OriginalUnitPrice -------------------------------------------------------------------- */
IF OBJECT_ID('dbo.SaQTDetail', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.SaQTDetail', 'OriginalUnitPrice') IS NULL
BEGIN
    PRINT 'Adding dbo.SaQTDetail.OriginalUnitPrice ...';
    ALTER TABLE dbo.SaQTDetail ADD OriginalUnitPrice decimal(18,4) NULL;
END
ELSE IF OBJECT_ID('dbo.SaQTDetail', 'U') IS NOT NULL
    PRINT 'dbo.SaQTDetail.OriginalUnitPrice already present - skipped.';
GO

/* ---- OverrideReason ----------------------------------------------------------------------- */
IF OBJECT_ID('dbo.SaQTDetail', 'U') IS NOT NULL
   AND COL_LENGTH('dbo.SaQTDetail', 'OverrideReason') IS NULL
BEGIN
    PRINT 'Adding dbo.SaQTDetail.OverrideReason ...';
    ALTER TABLE dbo.SaQTDetail ADD OverrideReason nvarchar(100) NULL;
END
ELSE IF OBJECT_ID('dbo.SaQTDetail', 'U') IS NOT NULL
    PRINT 'dbo.SaQTDetail.OverrideReason already present - skipped.';
GO

/* ---- Menu grant: PRICE_OVERRIDE on the SA_QT screen -------------------------------------- */
/* The permission itself is seeded by init-sales-item-family-menu.sql. This grants it to SA_QT so
   the page's override control can light up; a ROLE still has to be granted it separately. Guarded
   on both tables existing and on the row being absent, so this script is safe on any database.   */
IF OBJECT_ID('dbo.MenuPermission', 'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM dbo.Menu       WHERE MenuCode       = N'SA_QT')
   AND EXISTS (SELECT 1 FROM dbo.Permission WHERE PermissionCode = N'PRICE_OVERRIDE')
BEGIN
    DECLARE @menuId       int = (SELECT MenuId       FROM dbo.Menu       WHERE MenuCode       = N'SA_QT');
    DECLARE @permissionId int = (SELECT PermissionId FROM dbo.Permission WHERE PermissionCode = N'PRICE_OVERRIDE');

    IF NOT EXISTS (SELECT 1 FROM dbo.MenuPermission WHERE MenuId = @menuId AND PermissionId = @permissionId)
    BEGIN
        PRINT 'Granting PRICE_OVERRIDE to menu SA_QT ...';
        INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
        VALUES (@menuId, @permissionId, 22, 1);
    END
    ELSE
        PRINT 'Menu SA_QT already has PRICE_OVERRIDE - skipped.';
END
ELSE
    PRINT 'Skipped menu grant: SA_QT menu or PRICE_OVERRIDE permission not present. Run init-sales-item-family-menu.sql first.';
GO

/* ---- Verification ------------------------------------------------------------------------- */
SELECT  c.name            AS ColumnName,
        t.name            AS DataType,
        c.max_length      AS MaxLength,
        c.precision       AS Precision,
        c.scale           AS Scale,
        c.is_nullable     AS IsNullable
FROM    sys.columns c
JOIN    sys.types   t ON t.user_type_id = c.user_type_id
WHERE   c.object_id = OBJECT_ID('dbo.SaQTDetail')
  AND   c.name IN ('OriginalUnitPrice', 'OverrideReason')
ORDER BY c.name;
GO
