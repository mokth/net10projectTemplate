-- Grants the dynamic settings screen (ADMIN_SETTINGS) to one or more roles.
-- Manual DBA script - do NOT run at app startup. Idempotent: safe to re-run.
--
-- WHY THIS IS NEEDED AT ALL
--   init-admin-settings-menu.sql creates the menu row and its ACCESS / EDIT permissions. That grants
--   NOTHING to anybody: dbo.MenuPermission says which permissions a MENU offers, and
--   dbo.RoleMenuPermission says which ROLE may use them. Without a row here, the screen is reachable only
--   through AccessRightService.HasAdminBypass() (ADMIN / SYSTEM_ADMIN), and would LOOK fine to an
--   administrator while being invisible and read-only for every other user.
--
-- GOTCHA: dbo.RoleMenuPermission uses IsAllowed, NOT IsActive. It is the one grant table that differs.
-- Writing IsActive here is a hard Msg 207 - and a script that does it can fail at the very END, after all
-- its real work succeeded, which reads like success until you check the exit code.
--
-- NOTE ON DUPLICATE ROLE CODES: this development database has TWO roles with RoleCode 'ADMIN' and two with
-- 'USER'. Granting by CODE therefore grants BOTH rows, which is correct for an administrator role and is
-- why the script is written by code rather than by a single RoleId.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── Batch 1: dependency guard ────────────────────────────────────────────────
-- Every batch needs its OWN guard: RETURN exits only the batch it appears in, so this block cannot
-- protect the granting batch below it.
IF OBJECT_ID(N'dbo.Menu', N'U') IS NULL
   OR OBJECT_ID(N'dbo.Permission', N'U') IS NULL
   OR OBJECT_ID(N'dbo.MenuPermission', N'U') IS NULL
   OR OBJECT_ID(N'dbo.RoleMenuPermission', N'U') IS NULL
BEGIN
    RAISERROR(N'Menu / Permission / MenuPermission / RoleMenuPermission is missing - run init-menu-access.sql first.', 16, 1);
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'ADMIN_SETTINGS')
BEGIN
    RAISERROR(N'The ADMIN_SETTINGS menu row does not exist - run init-admin-settings-menu.sql first.', 16, 1);
    RETURN;
END

PRINT N'ADMIN_SETTINGS dependency check passed.';
GO

-- ── Batch 2: grant ───────────────────────────────────────────────────────────
DECLARE @roleCodes TABLE (RoleCode nvarchar(50) PRIMARY KEY);

-- Roles that should be able to open and edit system settings.
-- ADMIN / SYSTEM_ADMIN already reach the screen through HasAdminBypass(); the explicit row records the
-- intent and keeps working if that bypass ever changes.
INSERT INTO @roleCodes (RoleCode) VALUES (N'ADMIN'), (N'SYSTEM_ADMIN');

INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsAllowed, CreatedDate, CreatedBy)
SELECT r.RoleId, mp.MenuId, mp.PermissionId, 1, SYSUTCDATETIME(), N'DEPLOY'
FROM dbo.Role r
INNER JOIN @roleCodes rc ON rc.RoleCode = r.RoleCode
INNER JOIN dbo.Menu m ON m.MenuCode = N'ADMIN_SETTINGS'
INNER JOIN dbo.MenuPermission mp ON mp.MenuId = m.MenuId
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.RoleMenuPermission x
    WHERE x.RoleId = r.RoleId AND x.MenuId = mp.MenuId AND x.PermissionId = mp.PermissionId);

PRINT N'grant-admin-settings-role.sql complete.';
GO

-- Verification: one row per role x permission.
SELECT r.RoleCode + N' -> ' + p.PermissionCode + N' (IsAllowed=' + CAST(rmp.IsAllowed AS varchar(1)) + N')' AS Grant_
FROM dbo.RoleMenuPermission rmp
INNER JOIN dbo.Role r ON r.RoleId = rmp.RoleId
INNER JOIN dbo.Menu m ON m.MenuId = rmp.MenuId
INNER JOIN dbo.Permission p ON p.PermissionId = rmp.PermissionId
WHERE m.MenuCode = N'ADMIN_SETTINGS'
ORDER BY r.RoleCode, p.PermissionCode;
GO
