# Database Setup Guide for ErpWeb

## Problem
The application is failing with "Invalid object name 'dbo.UserRoleMappings'" because the required database tables don't exist.

## Solution
Run the database setup scripts to create all required tables and initial data.

## Step-by-Step Setup

### Step 1: Create Menu Access Tables

Run this script on your production database:

```sql
-- Run: create-menu-access-tables.sql
-- This creates:
--   - Permission table
--   - Menu table
--   - Role table
--   - UserRoleMapping table
--   - MenuPermission table
--   - RoleMenuPermission table
--   - Inserts standard permissions
--   - Creates SYSTEM_ADMIN role
--   - Maps admin user to SYSTEM_ADMIN role
--   - Updates admin userlevel
```

### Step 2: Verify Tables Created

After running the script, verify tables exist:

```sql
SELECT TABLE_NAME 
FROM INFORMATION_SCHEMA.TABLES 
WHERE TABLE_TYPE = 'BASE TABLE' 
  AND TABLE_NAME IN ('Permission', 'Menu', 'Role', 'UserRoleMapping', 'MenuPermission', 'RoleMenuPermission')
ORDER BY TABLE_NAME;
```

Expected result: 6 rows showing all table names.

### Step 2A: Deploy Delivery Request Schema and Numbering (Mandatory)

Before enabling the Delivery Request menu, run this sequence on the same database:

1. Run `scripts/create-sales-delivery-request.sql`.
2. Verify `SaDeliveryRequest`, `SaDeliveryRequestSource`, `SaDeliveryRequestAudit`, and `PrWorkOrderDemandAllocation` exist.
3. Replace the `@Company` and `@Branch` `CHANGE_ME` inputs in `scripts/init-sales-delivery-request-menu.sql` with the real target values. `DEMO/HQ` and placeholders are rejected.
4. Run `scripts/init-sales-delivery-request-menu.sql`; it is safe to re-run after a successful deployment.
5. Verify `SA_DR` permissions and a `DR` row in `AdSmNumDate` for the configured company/branch.
6. Smoke test one `Sales Order → Delivery Request → Work Order` flow.

The menu/numbering script fails before enabling `SA_DR` when the four Delivery Request tables, numbering table, or deployment inputs are missing. The application does not create this schema automatically.

### Step 3: Verify Admin User Setup

Check admin user's userlevel and role:

```sql
-- Check admin user
SELECT uid, id, CompanyCode, userlevel
FROM dbo.userlogin
WHERE id = 'admin';

-- Check admin role mapping
SELECT u.id, u.CompanyCode, r.RoleCode
FROM dbo.userlogin u
JOIN dbo.UserRoleMapping urm ON u.uid = urm.UserUid
JOIN dbo.Role r ON urm.RoleId = r.RoleId
WHERE u.id = 'admin';
```

Expected result:
- `userlevel` = 'SYSTEM_ADMIN'
- `RoleCode` = 'SYSTEM_ADMIN'

### Step 4: Test Application

1. Restart the application (if needed)
2. Login as admin
3. Check that menus are visible

## Troubleshooting

### Issue: Tables already exist

If you get "Tables already exist" message, the script will skip creation. Check if the tables have data:

```sql
SELECT 'Permission' AS TableName, COUNT(*) AS RowCount FROM dbo.Permission
UNION ALL
SELECT 'Menu', COUNT(*) FROM dbo.Menu
UNION ALL
SELECT 'Role', COUNT(*) FROM dbo.Role
UNION ALL
SELECT 'UserRoleMapping', COUNT(*) FROM dbo.UserRoleMapping
UNION ALL
SELECT 'MenuPermission', COUNT(*) FROM dbo.MenuPermission
UNION ALL
SELECT 'RoleMenuPermission', COUNT(*) FROM dbo.RoleMenuPermission;
```

### Issue: Menu table is empty

The Menu table should be populated by the application on startup (menu sync). If it's empty:

1. Check the application logs for menu sync errors
2. Verify `menus.xml` file exists in the application directory
3. Check the `Menus:SyncOnStartup` configuration

### Issue: Admin user not found

If admin user doesn't exist in `dbo.userlogin`:

1. Check if the user exists: `SELECT * FROM dbo.userlogin WHERE id = 'admin'`
2. If not, you need to create the admin user first

### Issue: Permission errors

If you get permission errors running the script:

1. Ensure you're connected as a user with db_owner or sysadmin role
2. Or ask your DBA to run the script

## Database Schema Overview

### Permission Table
Stores available permissions (ACCESS, ADD, EDIT, DELETE, etc.)

### Menu Table
Stores menu structure from menus.xml

### Role Table
Stores roles per company (SYSTEM_ADMIN, ADMIN, USER, etc.)

### UserRoleMapping Table
Maps users to roles

### MenuPermission Table
Maps menus to permissions (which permissions are available for each menu)

### RoleMenuPermission Table
Maps roles to menu permissions (which permissions each role has for each menu)

## Files Created

- `create-menu-access-tables.sql` - Main setup script
- `DATABASE_SETUP_GUIDE.md` - This file

## Verification Checklist

After setup, verify:

- [ ] All 6 tables exist
- [ ] Permission table has 14 rows (standard permissions)
- [ ] Role table has SYSTEM_ADMIN role for your company
- [ ] Admin user has userlevel = 'SYSTEM_ADMIN'
- [ ] Admin user is mapped to SYSTEM_ADMIN role
- [ ] Application can login and show menus

## Next Steps

After database setup:

1. **Test Login**: Login as admin
2. **Verify Menus**: Check that all menus are visible
3. **Test Navigation**: Click on menus to verify they work
4. **Check Logs**: Review application logs for any errors

## Support

If issues persist:

1. Check application logs: `./logs/erpweb-logfile.txt`
2. Run diagnostic script: `diagnose-menu-access.sql`
3. Verify database connectivity
4. Check menu synchronization status
