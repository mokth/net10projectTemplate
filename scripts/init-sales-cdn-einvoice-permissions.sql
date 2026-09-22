/* ============================================================================
   Sales Credit / Debit Note — e-Invoice permissions (SUBMIT, CANCEL)
   ----------------------------------------------------------------------------
   `SA_CN` and `SA_DN` shipped with ADD / EDIT / DELETE / POST / ROLLBACK only
   (scripts/init-menu-access.sql), so the e-Invoice actions were unusable for
   every non-admin:

     * the CN/DN list toolbar's SUBMIT / E-STATUS / CANCEL buttons, and
     * the SaEInvoicePanel that has been on the CN/DN ENTRY page all along,

   all gate on PermissionCodes.Submit / .Cancel against the page's own menu
   code (SaEInvoiceService.AuthorizeAsync maps CN -> SA_CN, DN -> SA_DN). With
   no MenuPermission row the permission can never be granted to a role, so the
   buttons render disabled and the feature looks broken while being deployed.

   `dbo.MenuPermission` only makes the permission AVAILABLE. A role still needs
   a `dbo.RoleMenuPermission` row (with `IsAllowed = 1` — NOT `IsActive`, which
   is a hard Msg 207 on that table) before a user can actually submit. That
   grant is deliberately NOT seeded here, because it would hard-code role names
   into a migration; the four-step deployment runbook in
   plans/plan-saCdnEInvoiceParity.prompt.md covers it, including the two
   verification queries. Do not consider the feature delivered until it is done.

   Idempotent: guarded inserts, so a second run is a clean no-op. Safe on a
   populated database.

   Apply TWICE on a scratch database (second run must be a clean no-op) before
   touching dev.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* The two Permission rows are seeded by init-menu-access.sql. Report rather than assume, because a
   missing Permission row would make the guarded INSERT below match nothing and exit "successfully". */
IF (SELECT COUNT(*) FROM dbo.Permission WHERE PermissionCode IN (N'SUBMIT', N'CANCEL')) < 2
BEGIN
    PRINT N'dbo.Permission is missing SUBMIT and/or CANCEL - run init-menu-access.sql first, then re-run this script.';
END
ELSE IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode IN (N'SA_CN', N'SA_DN'))
BEGIN
    PRINT N'dbo.Menu is missing SA_CN and/or SA_DN - run init-menu-access.sql first, then re-run this script.';
END
ELSE
BEGIN
    INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
    SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
    FROM dbo.Menu m
    INNER JOIN dbo.Permission p ON p.PermissionCode IN (N'SUBMIT', N'CANCEL')
    WHERE m.MenuCode IN (N'SA_CN', N'SA_DN')
      AND NOT EXISTS (
          SELECT 1 FROM dbo.MenuPermission mp
          WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);

    PRINT CONCAT(N'MenuPermission rows processed for SA_CN/SA_DN x SUBMIT/CANCEL: ',
                 @@ROWCOUNT, N' inserted (2 menus x 2 permissions = 4 once; 0 on a re-run).');
END
GO

/* Ensure the grants are ACTIVE even if an earlier run created them disabled. Kept in its OWN batch so
   the verification below reads the committed state. */
UPDATE mp
SET IsActive = 1
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode IN (N'SA_CN', N'SA_DN')
  AND p.PermissionCode IN (N'SUBMIT', N'CANCEL')
  AND mp.IsActive <> 1;
GO

/* Verification — expect exactly four rows (SA_CN/SA_DN x SUBMIT/CANCEL), all IsActive = 1. */
SELECT m.MenuCode,
       m.MenuName,
       p.PermissionCode,
       mp.IsActive,
       mp.SortOrder
FROM dbo.MenuPermission mp
INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
WHERE m.MenuCode IN (N'SA_CN', N'SA_DN')
  AND p.PermissionCode IN (N'SUBMIT', N'CANCEL')
ORDER BY m.MenuCode, p.PermissionCode;
GO
