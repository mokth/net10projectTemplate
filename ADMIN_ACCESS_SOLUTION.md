# Admin Access Solution

## Current Status
You've confirmed that the SYSTEM_ADMIN role exists for the DEMO company. Now we need to verify the complete setup.

## Step 1: Run the Diagnostic Script

Run this script to identify exactly what's wrong:

```
diagnose-admin-access.sql
```

This will check:
- Admin user's userlevel
- Role mappings
- Menu permissions
- Menu loading status

## Step 2: Based on Diagnostic Results

### If Admin userlevel is NOT 'SYSTEM_ADMIN' or 'ADMIN'

Run this fix script:

```
fix-admin-complete.sql
```

This will:
- Set admin userlevel to 'SYSTEM_ADMIN'
- Ensure SYSTEM_ADMIN role exists
- Map admin to SYSTEM_ADMIN role

### If Menus are NOT loaded

The menus should be loaded automatically by the application on startup. If they're not:

1. Check application logs for menu sync errors
2. Verify `menus.xml` file exists in the application directory
3. Check the `Menus:SyncOnStartup` configuration in `appsettings.json`

### If Menu Permissions are missing

The menu permissions should be created by the menu sync. If they're missing:

1. Run the application once to trigger menu sync
2. Or run the `init-menu-access.sql` script manually

## Step 3: Complete Verification

After fixing, run this verification:

```
verify-complete-setup.sql
```

This will confirm:
- Admin userlevel is correct
- Role mapping exists
- Menu permissions are configured
- Menus are loaded

## Step 4: Test the Application

1. **Logout** from the application (if logged in)
2. **Login** again as admin
3. **Check** that menus are visible
4. **Test** navigation to verify access

## Common Issues and Fixes

### Issue 1: Admin userlevel is NULL or wrong value

**Fix:**
```sql
UPDATE dbo.userlogin
SET userlevel = N'SYSTEM_ADMIN',
    Updated = GETDATE(),
    UpdatedUID = N'FIX'
WHERE id = N'admin'
  AND (userlevel IS NULL OR userlevel NOT IN (N'SYSTEM_ADMIN', N'ADMIN'));
```

### Issue 2: Admin has no SYSTEM_ADMIN role mapping

**Fix:**
```sql
-- Get admin uid and SYSTEM_ADMIN role id
DECLARE @AdminUid int, @RoleId int;
SELECT @AdminUid = uid FROM dbo.userlogin WHERE id = 'admin' AND CompanyCode = 'DEMO';
SELECT @RoleId = RoleId FROM dbo.Role WHERE RoleCode = 'SYSTEM_ADMIN' AND CompanyCode = 'DEMO';

-- Insert mapping
IF NOT EXISTS (SELECT 1 FROM dbo.UserRoleMapping WHERE UserUid = @AdminUid AND RoleId = @RoleId)
    INSERT INTO dbo.UserRoleMapping (UserUid, RoleId) VALUES (@AdminUid, @RoleId);
```

### Issue 3: Menu table is empty

**Fix:** The application should sync menus on startup. If not:
1. Check application logs
2. Verify `menus.xml` exists
3. Restart the application

### Issue 4: MenuPermissions table is empty

**Fix:** Run the `init-menu-access.sql` script or restart the application to trigger menu sync.

## Files Created

| File | Purpose |
|------|---------|
| `diagnose-admin-access.sql` | Comprehensive diagnostic |
| `fix-admin-complete.sql` | Fix all admin issues |
| `verify-complete-setup.sql` | Verify complete setup |
| `ADMIN_ACCESS_SOLUTION.md` | This file |

## Expected Behavior After Fix

When admin logs in:
1. `IsInRole("SYSTEM_ADMIN")` returns true
2. `HasAdminBypass()` returns true
3. All permission checks are bypassed
4. All menus are visible
5. Full access to all features

## Verification Checklist

After running the scripts:

- [ ] Admin userlevel = 'SYSTEM_ADMIN'
- [ ] Admin has SYSTEM_ADMIN role mapping
- [ ] Menu table has rows
- [ ] MenuPermission table has rows
- [ ] RoleMenuPermission table has rows
- [ ] Application shows menus after login

## Next Steps

1. Run `diagnose-admin-access.sql` to identify the issue
2. Run the appropriate fix script
3. Run `verify-complete-setup.sql` to confirm
4. Logout and login again
5. Test menu access

## Support

If issues persist after running the scripts:

1. Check application logs: `./logs/erpweb-logfile.txt`
2. Verify database connectivity
3. Ensure menu sync completed successfully
4. Check for any SQL errors in the scripts