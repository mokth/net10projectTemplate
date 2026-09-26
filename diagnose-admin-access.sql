-- Comprehensive Admin Access Diagnostic
-- Run this to identify exactly what's wrong

USE ERPWeb;
GO

PRINT '=== Admin Access Diagnostic ===';
PRINT '';

-- 1. Check Admin User
PRINT '1. Admin User Details:';
SELECT 
    uid,
    id,
    CompanyCode,
    BranchCode,
    LocationCode,
    userlevel,
    CASE 
        WHEN userlevel IN ('ADMIN', 'SYSTEM_ADMIN') THEN '✓ Valid'
        WHEN userlevel IS NULL THEN '✗ NULL'
        ELSE '✗ Invalid: ' + userlevel
    END AS UserlevelStatus
FROM dbo.userlogin
WHERE id = 'admin';

-- 2. Check if userlevel is a valid role
PRINT '';
PRINT '2. Userlevel vs Role Check:';
DECLARE @AdminUserlevel nvarchar(20);
SELECT @AdminUserlevel = userlevel FROM dbo.userlogin WHERE id = 'admin';

IF @AdminUserlevel IN ('ADMIN', 'SYSTEM_ADMIN')
    PRINT '   ✓ Userlevel is valid for admin bypass';
ELSE
    PRINT '   ✗ Userlevel is NOT valid: ' + ISNULL(@AdminUserlevel, 'NULL');

-- 3. Check Role Table
PRINT '';
PRINT '3. Roles for DEMO Company:';
SELECT 
    RoleId,
    RoleCode,
    RoleName,
    IsActive
FROM dbo.Role
WHERE CompanyCode = 'DEMO';

-- 4. Check UserRoleMapping
PRINT '';
PRINT '4. Admin Role Mappings:';
SELECT 
    u.id AS UserId,
    r.RoleCode,
    r.CompanyCode,
    r.IsActive AS RoleIsActive
FROM dbo.userlogin u
JOIN dbo.UserRoleMapping urm ON u.uid = urm.UserUid
JOIN dbo.Role r ON urm.RoleId = r.RoleId
WHERE u.id = 'admin';

-- 5. Check if admin has SYSTEM_ADMIN role
PRINT '';
PRINT '5. SYSTEM_ADMIN Role Check:';
IF EXISTS (
    SELECT 1 
    FROM dbo.userlogin u
    JOIN dbo.UserRoleMapping urm ON u.uid = urm.UserUid
    JOIN dbo.Role r ON urm.RoleId = r.RoleId
    WHERE u.id = 'admin' 
      AND r.RoleCode = 'SYSTEM_ADMIN'
      AND r.CompanyCode = 'DEMO'
)
    PRINT '   ✓ Admin has SYSTEM_ADMIN role';
ELSE
    PRINT '   ✗ Admin does NOT have SYSTEM_ADMIN role';

-- 6. Check Menu Permissions
PRINT '';
PRINT '6. Menu Permissions:';
DECLARE @MenuPermCount int;
SELECT @MenuPermCount = COUNT(*) FROM dbo.MenuPermission;
PRINT '   MenuPermission rows: ' + CAST(@MenuPermCount AS NVARCHAR);

IF @MenuPermCount > 0
    PRINT '   ✓ Menu permissions exist';
ELSE
    PRINT '   ✗ Menu permissions missing!';

-- 7. Check RoleMenuPermissions
PRINT '';
PRINT '7. Role Menu Permissions:';
DECLARE @RoleMenuPermCount int;
SELECT @RoleMenuPermCount = COUNT(*) FROM dbo.RoleMenuPermission;
PRINT '   RoleMenuPermission rows: ' + CAST(@RoleMenuPermCount AS NVARCHAR);

IF @RoleMenuPermCount > 0
    PRINT '   ✓ Role menu permissions exist';
ELSE
    PRINT '   ✗ Role menu permissions missing!';

-- 8. Check Menus
PRINT '';
PRINT '8. Menus:';
DECLARE @MenuCount int;
SELECT @MenuCount = COUNT(*) FROM dbo.Menu;
PRINT '   Menu rows: ' + CAST(@MenuCount AS NVARCHAR);

IF @MenuCount > 0
    PRINT '   ✓ Menus loaded';
ELSE
    PRINT '   ✗ Menus NOT loaded!';

-- 9. Summary
PRINT '';
PRINT '=== Diagnosis ===';
DECLARE @HasValidUserlevel bit;
DECLARE @HasRoleMapping bit;
DECLARE @HasMenuPerms bit;
DECLARE @HasMenus bit;

SET @HasValidUserlevel = CASE WHEN @AdminUserlevel IN ('ADMIN', 'SYSTEM_ADMIN') THEN 1 ELSE 0 END;

IF EXISTS (
    SELECT 1 
    FROM dbo.userlogin u
    JOIN dbo.UserRoleMapping urm ON u.uid = urm.UserUid
    JOIN dbo.Role r ON urm.RoleId = r.RoleId
    WHERE u.id = 'admin' 
      AND r.RoleCode = 'SYSTEM_ADMIN'
      AND r.CompanyCode = 'DEMO'
)
    SET @HasRoleMapping = 1;
ELSE
    SET @HasRoleMapping = 0;

SET @HasMenuPerms = CASE WHEN @MenuPermCount > 0 THEN 1 ELSE 0 END;
SET @HasMenus = CASE WHEN @MenuCount > 0 THEN 1 ELSE 0 END;

PRINT 'Valid userlevel (ADMIN/SYSTEM_ADMIN): ' + CASE WHEN @HasValidUserlevel = 1 THEN '✓' ELSE '✗' END;
PRINT 'Has SYSTEM_ADMIN role mapping: ' + CASE WHEN @HasRoleMapping = 1 THEN '✓' ELSE '✗' END;
PRINT 'Has menu permissions: ' + CASE WHEN @HasMenuPerms = 1 THEN '✓' ELSE '✗' END;
PRINT 'Has menus loaded: ' + CASE WHEN @HasMenus = 1 THEN '✓' ELSE '✗' END;
PRINT '';

IF @HasValidUserlevel = 1 AND @HasRoleMapping = 1 AND @HasMenus = 1
    PRINT '✓ Setup looks correct. Admin should have access.';
ELSE IF @HasValidUserlevel = 0
    PRINT '✗ FIX NEEDED: Admin userlevel is not ADMIN or SYSTEM_ADMIN';
ELSE IF @HasRoleMapping = 0
    PRINT '✗ FIX NEEDED: Admin has no SYSTEM_ADMIN role mapping';
ELSE IF @HasMenus = 0
    PRINT '✗ FIX NEEDED: Menus not loaded (run menu sync)';

GO