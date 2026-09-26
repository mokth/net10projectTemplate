# Menu Access Troubleshooting Guide

## Problem
Admin user is logged in but all menus are hidden with "not access right" message.

## Root Cause
The admin user needs to have the correct `userlevel` in the database to bypass menu permission checks.

## How Menu Access Works

1. **Admin Bypass**: Users with `userlevel = 'ADMIN'` or `userlevel = 'SYSTEM_ADMIN'` bypass all permission checks
2. **Role-Based Access**: Other users need role assignments with menu permissions
3. **Menu Visibility**: Menus are shown only if the user has ACCESS permission for that menu

## Solution

### Step 1: Check Admin User's userlevel

Run this SQL query on your production database:

```sql
SELECT uid, id, CompanyCode, userlevel
FROM dbo.userlogin
WHERE id = 'admin';
```

**Expected Result**: `userlevel` should be `SYSTEM_ADMIN` or `ADMIN`

### Step 2: Fix Admin User's userlevel (if needed)

If the `userlevel` is NULL or incorrect, run this:

```sql
UPDATE dbo.userlogin
SET userlevel = N'SYSTEM_ADMIN',
    Updated = GETDATE(),
    UpdatedUID = N'SEED'
WHERE id = N'admin'
  AND (userlevel IS NULL OR userlevel NOT IN (N'SYSTEM_ADMIN', N'ADMIN'));
```

### Step 3: Verify the Fix

After updating, verify:

```sql
SELECT uid, id, CompanyCode, userlevel
FROM dbo.userlogin
WHERE id = 'admin';
```

The `userlevel` should now show `SYSTEM_ADMIN`.

### Step 4: Test the Application

1. Logout from the application
2. Login again as admin
3. Menus should now be visible

## Diagnostic Script

I've created a comprehensive diagnostic script: `diagnose-menu-access.sql`

Run this script to check:
- Admin user's userlevel
- Role assignments
- Menu permissions
- Active menus
- ACCESS permission

## How Admin Bypass Works

In `AccessRightService.cs` (lines 124-125):

```csharp
private bool HasAdminBypass() =>
    _currentUser.IsInRole(AdminRole) || _currentUser.IsInRole(SystemAdminRole);
```

This checks if the user has:
- `userlevel = 'ADMIN'` → IsInRole("ADMIN") returns true
- `userlevel = 'SYSTEM_ADMIN'` → IsInRole("SYSTEM_ADMIN") returns true

When `HasAdminBypass()` returns true, all permission checks are bypassed and the user has full access.

## Common Issues

### Issue 1: userlevel is NULL
**Solution**: Update userlevel to 'SYSTEM_ADMIN'

### Issue 2: userlevel is 'USER' or other value
**Solution**: Update userlevel to 'SYSTEM_ADMIN'

### Issue 3: Role not synced
**Solution**: The UserRoleSyncService should sync roles on login. Check if the role exists in the Roles table.

### Issue 4: Menu sync not completed
**Solution**: Check if menus are active in the Menus table. Run the menu sync if needed.

## Files Created

- `fix-admin-userlevel.sql` - Script to check and fix admin userlevel
- `diagnose-menu-access.sql` - Comprehensive diagnostic script
- `MENU_ACCESS_TROUBLESHOOTING.md` - This file

## Verification

After fixing the userlevel:

1. Logout from the application
2. Login again as admin
3. Check that all menus are visible
4. Test menu access

## Additional Notes

- The `userlevel` field is in the `dbo.userlogin` table
- It's stored as NVARCHAR(20)
- The role is added as a claim during login (see `AuthService.CreatePrincipal`)
- The `IsInRole()` method checks for this claim

## Support

If issues persist after fixing the userlevel:

1. Check the diagnostic script output
2. Verify database connectivity
3. Check application logs for errors
4. Ensure menu synchronization completed successfully