-- Diagnostic script to check menu access for admin user
-- Run this on your production database

-- 1. Check admin user's userlevel
PRINT '=== Admin User Details ===';
SELECT 
    uid,
    id,
    CompanyCode,
    userlevel,
    BranchCode,
    LocationCode
FROM dbo.userlogin
WHERE id = 'admin';

-- 2. Check if the admin user has a role assigned
PRINT '';
PRINT '=== Admin User Role Assignment ===';
SELECT 
    urm.UserUid,
    r.RoleId,
    r.RoleCode,
    r.CompanyCode,
    r.IsActive AS RoleIsActive
FROM dbo.UserRoleMappings urm
JOIN dbo.Roles r ON urm.RoleId = r.RoleId
WHERE urm.UserUid = (SELECT uid FROM dbo.userlogin WHERE id = 'admin');

-- 3. Check if the role has menu permissions
PRINT '';
PRINT '=== Role Menu Permissions ===';
SELECT 
    r.RoleCode,
    m.MenuCode,
    m.MenuName,
    p.PermissionCode,
    rmp.IsAllowed
FROM dbo.RoleMenuPermissions rmp
JOIN dbo.Roles r ON rmp.RoleId = r.RoleId
JOIN dbo.Menus m ON rmp.MenuId = m.MenuId
JOIN dbo.Permissions p ON rmp.PermissionId = p.PermissionId
WHERE r.RoleCode = 'SYSTEM_ADMIN'
  AND r.CompanyCode = (SELECT CompanyCode FROM dbo.userlogin WHERE id = 'admin')
ORDER BY m.MenuCode, p.PermissionCode;

-- 4. Check if MenuPermissions table has entries
PRINT '';
PRINT '=== Menu Permissions (Active) ===';
SELECT 
    m.MenuCode,
    m.MenuName,
    p.PermissionCode,
    mp.IsActive
FROM dbo.MenuPermissions mp
JOIN dbo.Menus m ON mp.MenuId = m.MenuId
JOIN dbo.Permissions p ON mp.PermissionId = p.PermissionId
WHERE mp.IsActive = 1
ORDER BY m.MenuCode, p.PermissionCode;

-- 5. Check if menus are active
PRINT '';
PRINT '=== Active Menus ===';
SELECT 
    MenuCode,
    MenuName,
    Route,
    IsActive,
    AlwaysVisible
FROM dbo.Menus
WHERE IsActive = 1
ORDER BY SortOrder, MenuCode;

-- 6. Check if ACCESS permission exists
PRINT '';
PRINT '=== ACCESS Permission ===';
SELECT 
    PermissionId,
    PermissionCode,
    PermissionName,
    IsActive
FROM dbo.Permissions
WHERE PermissionCode = 'ACCESS';

-- 7. Summary
PRINT '';
PRINT '=== Summary ===';
DECLARE @AdminUserlevel NVARCHAR(20);
DECLARE @RoleCount INT;
DECLARE @MenuPermCount INT;
DECLARE @ActiveMenuCount INT;

SELECT @AdminUserlevel = userlevel FROM dbo.userlogin WHERE id = 'admin';

SELECT @RoleCount = COUNT(*)
FROM dbo.UserRoleMappings urm
JOIN dbo.Roles r ON urm.RoleId = r.RoleId
WHERE urm.UserUid = (SELECT uid FROM dbo.userlogin WHERE id = 'admin');

SELECT @MenuPermCount = COUNT(*)
FROM dbo.RoleMenuPermissions rmp
JOIN dbo.Roles r ON rmp.RoleId = r.RoleId
WHERE r.RoleCode = 'SYSTEM_ADMIN'
  AND r.CompanyCode = (SELECT CompanyCode FROM dbo.userlogin WHERE id = 'admin');

SELECT @ActiveMenuCount = COUNT(*)
FROM dbo.Menus
WHERE IsActive = 1;

PRINT 'Admin userlevel: ' + ISNULL(@AdminUserlevel, 'NULL');
PRINT 'Role assignments: ' + CAST(@RoleCount AS NVARCHAR);
PRINT 'Role menu permissions: ' + CAST(@MenuPermCount AS NVARCHAR);
PRINT 'Active menus: ' + CAST(@ActiveMenuCount AS NVARCHAR);

IF @AdminUserlevel IS NULL OR @AdminUserlevel NOT IN ('ADMIN', 'SYSTEM_ADMIN')
    PRINT 'WARNING: Admin user does not have ADMIN or SYSTEM_ADMIN userlevel!';
    
IF @RoleCount = 0
    PRINT 'WARNING: Admin user has no role assignments!';
    
IF @MenuPermCount = 0
    PRINT 'WARNING: SYSTEM_ADMIN role has no menu permissions!';
    
IF @ActiveMenuCount = 0
    PRINT 'WARNING: No active menus found!';
