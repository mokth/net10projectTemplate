/* ============================================================================
   Inventory Balance by Lot inquiry — per-user price visibility flag
   ----------------------------------------------------------------------------
   Adds userlogin.CanViewPrice bit NOT NULL DEFAULT (0).

   Semantics (plan §4.7 / §5 D4):
     - A SECOND price-visibility mechanism alongside the menu-based
       VIEW_COST / VIEW_PRICE permissions. It gates the *estimated value*
       column on the Balance by Lot inquiry only (V1).
     - Baked into the auth cookie ONCE at sign-in (AuthService.CreatePrincipal),
       so a change takes effect only after the user signs out and back in (D13).

   Purely additive: the column is not referenced in the same batch that adds
   it, so this script needs NO dynamic SQL.

   Idempotent: the COL_LENGTH guard makes a second run a clean no-op.

   Safe to run against a populated table: adding a NOT NULL column with a
   DEFAULT is a metadata-only change in SQL Server; every existing row reads
   DEFAULT (0), which is exactly the pre-change behaviour (nobody may see the
   value column until an administrator opts a user in).

   Apply TWICE on a scratch database (second run must be a clean no-op) before
   touching dev, per the plan's Verification step 5.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.userlogin', N'U') IS NULL
BEGIN
    PRINT 'dbo.userlogin does not exist - apply scripts/init-userlogin.sql first, then re-run this script.';
    RETURN;
END
GO

IF COL_LENGTH(N'dbo.userlogin', N'CanViewPrice') IS NULL
BEGIN
    ALTER TABLE dbo.userlogin ADD CanViewPrice bit NOT NULL
        CONSTRAINT DF_userlogin_CanViewPrice DEFAULT (0);
    PRINT 'userlogin.CanViewPrice added.';
END
ELSE
BEGIN
    PRINT 'userlogin.CanViewPrice already present - no change.';
END
GO

/* Verification — the column must exist as a non-nullable bit with the default. */
SELECT
    ColumnName   = c.name,
    TypeName     = t.name,
    MaxLength    = c.max_length,
    IsNullable   = c.is_nullable,
    DefaultName  = dc.name
FROM sys.columns c
JOIN sys.types t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints dc
       ON dc.parent_object_id = c.object_id
      AND dc.parent_column_id = c.column_id
WHERE c.object_id = OBJECT_ID(N'dbo.userlogin')
  AND c.name = N'CanViewPrice';
GO
