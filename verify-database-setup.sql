-- Quick Verification Script
-- Run this to check if database setup is complete

USE ERPWeb;
GO

PRINT '=== Database Setup Verification ===';
PRINT '';

-- 1. Check if tables exist
PRINT '1. Checking Tables...';
DECLARE @TableCount int;
SELECT @TableCount = COUNT(*)
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE = 'BASE TABLE'
  AND TABLE_NAME IN ('Permission', 'Menu', 'Role', 'UserRoleMapping', 'MenuPermission', 'RoleMenuPermission');

IF @TableCount = 6
    PRINT '   ✓ All 6 tables exist';
ELSE
    PRINT '   ✗ Missing tables! Expected 6, found ' + CAST(@TableCount AS NVARCHAR);

-- 2. Check Permission table
PRINT '';
PRINT '2. Checking Permissions...';
DECLARE @PermCount int;
SELECT @PermCount = COUNT(*) FROM dbo.Permission;
PRINT '   Permissions: ' + CAST(@PermCount AS NVARCHAR);

IF @PermCount >= 14
    PRINT '   ✓ Standard permissions loaded';
ELSE
    PRINT '   ✗ Missing permissions! Expected at least 14';

-- 3. Check Role table
PRINT '';
PRINT '3. Checking Roles...';
SELECT RoleCode, CompanyCode, IsActive
FROM dbo.Role
WHERE RoleCode = 'SYSTEM_ADMIN';

IF EXISTS (SELECT 1 FROM dbo.Role WHERE RoleCode = 'SYSTEM_ADMIN')
    PRINT '   ✓ SYSTEM_ADMIN role exists';
ELSE
    PRINT '   ✗ SYSTEM_ADMIN role missing!';

-- 4. Check Admin User
PRINT '';
PRINT '4. Checking Admin User...';
DECLARE @AdminUserlevel nvarchar(20);
SELECT @AdminUserlevel = userlevel FROM dbo.userlogin WHERE id = 'admin';

IF @AdminUserlevel = 'SYSTEM_ADMIN'
    PRINT '   ✓ Admin user has SYSTEM_ADMIN userlevel';
ELSE
    PRINT '   ✗ Admin userlevel is: ' + ISNULL(@AdminUserlevel, 'NULL');

-- 5. Check UserRoleMapping
PRINT '';
PRINT '5. Checking UserRoleMapping...';
DECLARE @MappingCount int;
SELECT @MappingCount = COUNT(*)
FROM dbo.UserRoleMapping urm
JOIN dbo.userlogin u ON urm.UserUid = u.uid
WHERE u.id = 'admin';

IF @MappingCount > 0
    PRINT '   ✓ Admin user has role mapping';
ELSE
    PRINT '   ✗ Admin user has no role mapping!';

-- 6. Check Menu table
PRINT '';
PRINT '6. Checking Menus...';
DECLARE @MenuCount int;
SELECT @MenuCount = COUNT(*) FROM dbo.Menu;

IF @MenuCount > 0
    PRINT '   ✓ Menus loaded: ' + CAST(@MenuCount AS NVARCHAR);
ELSE
    PRINT '   ✗ Menu table is empty! Menu sync may not have run.';

-- 7. Summary
PRINT '';
PRINT '=== Summary ===';
PRINT 'Tables: ' + CAST(@TableCount AS NVARCHAR) + '/6';
PRINT 'Permissions: ' + CAST(@PermCount AS NVARCHAR);
PRINT 'Admin Userlevel: ' + ISNULL(@AdminUserlevel, 'NULL');
PRINT 'Admin Role Mapping: ' + CASE WHEN @MappingCount > 0 THEN 'Yes' ELSE 'No' END;
PRINT 'Menus: ' + CAST(@MenuCount AS NVARCHAR);
PRINT '';

IF @TableCount = 6 AND @PermCount >= 14 AND @AdminUserlevel = 'SYSTEM_ADMIN' AND @MappingCount > 0
    PRINT '✓ Database setup is COMPLETE!';
ELSE
    PRINT '✗ Database setup is INCOMPLETE. Please run create-menu-access-tables.sql';

GO