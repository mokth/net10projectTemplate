-- Roll back PrShiftBreak authority without dropping child rows.
-- Freeze shift edits first. Old applications will use Break_Tm1..5 after this demotion.
-- Before redeploying child-aware ErpWeb, rerun alter-pr-shift-break.sql with promotion enabled
-- so version=0 shifts delete/rebuild PrShiftBreak from the current wide columns.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @CompCode nvarchar(10) = NULL; -- optional scope; leave NULL for all companies

BEGIN TRANSACTION;

UPDATE dbo.PrShift
SET BreakStorageVersion = 0
WHERE BreakStorageVersion >= 1
  AND (@CompCode IS NULL OR CompCode = @CompCode);

COMMIT;

PRINT N'PrShift BreakStorageVersion demoted. Existing PrShiftBreak rows are dormant until remigration.';
