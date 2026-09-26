-- Complete Verification Script
-- Run this to verify all aspects of the setup

USE ERPWeb;
GO

PRINT '=== Complete Setup Verification ===';
PRINT '';

-- 1. Check Admin User's userlevel
PRINT '1. Admin User Userlevel:';
SELECT 
    uid,
    id,
    CompanyCode,
    userlevel,
    CASE 
        WHEN userlevel = 'SYSTEM_ADMIN' THEN '✓ Correct'
        WHEN userlevel = 'ADMIN' THEN '✓ Correct'
        ELSE '✗ Wrong - Needs fix'
    END AS Status
FROM dbo.userlogin
WHERE id = 'admin';

-- 2. Check Admin User's Role Mapping
PRINT '';
PRINT '2. Admin User Role Mapping:';
SELECT 
    u.id AS UserId,
    u.CompanyCode,
    r.RoleCode,
    r.IsActive AS RoleIsActive,
    CASE 
        WHEN r.RoleCode = 'SYSTEM_ADMIN' THEN '✓ Correct'
        ELSE '✗ Wrong role'
    END AS Status
FROM dbo.userlogin u
LEFT JOIN dbo.UserRoleMapping urm ON u.uid = urm.UserUid
LEFT JOIN dbo.Role r ON urm.RoleId = r.RoleId
WHERE u.id = 'admin';

-- 3. Check if MenuPermissions table has data
PRINT '';
PRINT '3. Menu Permissions:';
DECLARE @MenuPermCount int;
SELECT @MenuPermCount = COUNT(*) FROM dbo.MenuPermission;
PRINT '   MenuPermission rows: ' + CAST(@MenuPermCount AS NVARCHAR);

IF @MenuPermCount > 0
    PRINT '   ✓ Menu permissions configured';
ELSE
    PRINT '   ✗ Menu permissions NOT configured!';

-- 4. Check if RoleMenuPermissions table has data
PRINT '';
PRINT '4. Role Menu Permissions:';
DECLARE @RoleMenuPermCount int;
SELECT @RoleMenuPermCount = COUNT(*) FROM dbo.RoleMenuPermission;
PRINT '   RoleMenuPermission rows: ' + CAST(@RoleMenuPermCount AS NVARCHAR);

IF @RoleMenuPermCount > 0
    PRINT '   ✓ Role menu permissions configured';
ELSE
    PRINT '   ✗ Role menu permissions NOT configured!';

-- 5. Check if Menu table has data
PRINT '';
PRINT '5. Menus:';
DECLARE @MenuCount int;
SELECT @MenuCount = COUNT(*) FROM dbo.Menu;
PRINT '   Menu rows: ' + CAST(@MenuCount AS NVARCHAR);

IF @MenuCount > 0
    PRINT '   ✓ Menus loaded';
ELSE
    PRINT '   ✗ Menus NOT loaded!';

-- 6. Check active menus
PRINT '';
PRINT '6. Active Menus:';
SELECT COUNT(*) AS ActiveMenuCount
FROM dbo.Menu
WHERE IsActive = 1;

-- 7. Check SYSTEM_ADMIN role permissions
PRINT '';
PRINT '7. SYSTEM_ADMIN Role Permissions:';
SELECT 
    r.RoleCode,
    m.MenuCode,
    m.MenuName,
    p.PermissionCode,
    rmp.IsAllowed
FROM dbo.RoleMenuPermission rmp
JOIN dbo.Role r ON rmp.RoleId = r.RoleId
JOIN dbo.Menu m ON rmp.MenuId = m.MenuId
JOIN dbo.Permission p ON rmp.PermissionId = p.PermissionId
WHERE r.RoleCode = 'SYSTEM_ADMIN'
  AND r.CompanyCode = 'DEMO'
ORDER BY m.MenuCode, p.PermissionCode;

-- 8. Summary
PRINT '';
PRINT '=== Summary ===';
DECLARE @AdminUserlevel nvarchar(20);
DECLARE @HasRoleMapping bit;
DECLARE @HasMenuPerms bit;

SELECT @AdminUserlevel = userlevel FROM dbo.userlogin WHERE id = 'admin';

IF EXISTS (
    SELECT 1 
    FROM dbo.UserRoleMapping urm
    JOIN dbo.userlogin u ON urm.UserUid = u.uid
    JOIN dbo.Role r ON urm.RoleId = r.RoleId
    WHERE u.id = 'admin' AND r.RoleCode = 'SYSTEM_ADMIN'
)
    SET @HasRoleMapping = 1;
ELSE
    SET @HasRoleMapping = 0;

IF EXISTS (
    SELECT 1 
    FROM dbo.RoleMenuPermission rmp
    JOIN dbo.Role r ON rmp.RoleId = r.RoleId
    WHERE r.RoleCode = 'SYSTEM_ADMIN' AND r.CompanyCode = 'DEMO'
)
    SET @HasMenuPerms = 1;
ELSE
    SET @HasMenuPerms = 0;

PRINT 'Admin userlevel: ' + ISNULL(@AdminUserlevel, 'NULL');
PRINT 'Has role mapping: ' + CASE WHEN @HasRoleMapping = 1 THEN 'Yes' ELSE 'No' END;
PRINT 'Has menu permissions: ' + CASE WHEN @HasMenuPerms = 1 THEN 'Yes' ELSE 'No' END;
PRINT 'Menus loaded: ' + CAST(@MenuCount AS NVARCHAR);
PRINT '';

IF @AdminUserlevel = 'SYSTEM_ADMIN' AND @HasRoleMapping = 1 AND @MenuCount > 0
    PRINT '✓ Setup is COMPLETE! Admin should have full access.';
ELSE
BEGIN
    PRINT '✗ Setup is INCOMPLETE. Issues:';
    IF @AdminUserlevel <> 'SYSTEM_ADMIN'
        PRINT '  - Admin userlevel is not SYSTEM_ADMIN';
    IF @HasRoleMapping = 0
        PRINT '  - Admin has no SYSTEM_ADMIN role mapping';
    IF @MenuCount = 0
        PRINT '  - Menus not loaded (menu sync may not have run)';
END

GO