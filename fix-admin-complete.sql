-- Complete Admin Fix Script
-- Run this to fix all admin user issues

USE ERPWeb;
GO

PRINT '=== Fixing Admin User Setup ===';
PRINT '';

-- 1. Fix Admin User's userlevel
PRINT '1. Fixing Admin Userlevel...';
DECLARE @AdminUid int;
SELECT @AdminUid = uid FROM dbo.userlogin WHERE id = 'admin' AND CompanyCode = 'DEMO';

IF @AdminUid IS NULL
BEGIN
    PRINT '   ✗ Admin user not found!';
    RETURN;
END

UPDATE dbo.userlogin
SET userlevel = N'SYSTEM_ADMIN',
    Updated = GETDATE(),
    UpdatedUID = N'FIX'
WHERE uid = @AdminUid
  AND (userlevel IS NULL OR userlevel <> N'SYSTEM_ADMIN');

PRINT '   ✓ Admin userlevel set to SYSTEM_ADMIN';

-- 2. Ensure SYSTEM_ADMIN role exists
PRINT '';
PRINT '2. Checking SYSTEM_ADMIN Role...';
DECLARE @SystemAdminRoleId int;
SELECT @SystemAdminRoleId = RoleId 
FROM dbo.Role 
WHERE RoleCode = 'SYSTEM_ADMIN' AND CompanyCode = 'DEMO';

IF @SystemAdminRoleId IS NULL
BEGIN
    PRINT '   Creating SYSTEM_ADMIN role...';
    INSERT INTO dbo.Role (CompanyCode, RoleCode, RoleName, IsActive, CreatedDate, CreatedBy)
    VALUES (N'DEMO', N'SYSTEM_ADMIN', N'System Administrator', 1, GETDATE(), N'FIX');
    
    SELECT @SystemAdminRoleId = RoleId 
    FROM dbo.Role 
    WHERE RoleCode = 'SYSTEM_ADMIN' AND CompanyCode = 'DEMO';
    
    PRINT '   ✓ SYSTEM_ADMIN role created';
END
ELSE
    PRINT '   ✓ SYSTEM_ADMIN role exists';

-- 3. Ensure admin user is mapped to SYSTEM_ADMIN role
PRINT '';
PRINT '3. Fixing Admin Role Mapping...';
IF NOT EXISTS (
    SELECT 1 
    FROM dbo.UserRoleMapping 
    WHERE UserUid = @AdminUid AND RoleId = @SystemAdminRoleId
)
BEGIN
    INSERT INTO dbo.UserRoleMapping (UserUid, RoleId)
    VALUES (@AdminUid, @SystemAdminRoleId);
    PRINT '   ✓ Admin mapped to SYSTEM_ADMIN role';
END
ELSE
    PRINT '   ✓ Admin already mapped to SYSTEM_ADMIN role';

-- 4. Verify the fix
PRINT '';
PRINT '4. Verification...';
SELECT 
    u.id,
    u.CompanyCode,
    u.userlevel,
    r.RoleCode
FROM dbo.userlogin u
LEFT JOIN dbo.UserRoleMapping urm ON u.uid = urm.UserUid
LEFT JOIN dbo.Role r ON urm.RoleId = r.RoleId
WHERE u.id = 'admin';

PRINT '';
PRINT '=== Fix Complete ===';
PRINT 'Admin user should now have full access.';
PRINT 'Please logout and login again to see the changes.';

GO