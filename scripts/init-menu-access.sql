USE ERPLiteEx;
GO

IF OBJECT_ID('dbo.RoleMenuPermission', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Permission (
        PermissionId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Permission PRIMARY KEY,
        PermissionCode nvarchar(50) NOT NULL,
        PermissionName nvarchar(100) NOT NULL,
        PermissionType nvarchar(20) NOT NULL,
        Description nvarchar(250) NULL,
        SortOrder int NOT NULL CONSTRAINT DF_Permission_SortOrder DEFAULT (0),
        IsActive bit NOT NULL CONSTRAINT DF_Permission_IsActive DEFAULT (1),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        CONSTRAINT UQ_Permission_PermissionCode UNIQUE (PermissionCode)
    );

    CREATE TABLE dbo.Menu (
        MenuId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Menu PRIMARY KEY,
        MenuCode nvarchar(50) NOT NULL,
        MenuName nvarchar(100) NOT NULL,
        ParentMenuId int NULL,
        Route nvarchar(200) NULL,
        Icon nvarchar(100) NULL,
        SortOrder int NOT NULL CONSTRAINT DF_Menu_SortOrder DEFAULT (0),
        AlwaysVisible bit NOT NULL CONSTRAINT DF_Menu_AlwaysVisible DEFAULT (0),
        IsActive bit NOT NULL CONSTRAINT DF_Menu_IsActive DEFAULT (1),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        CONSTRAINT UQ_Menu_MenuCode UNIQUE (MenuCode),
        CONSTRAINT FK_Menu_Parent FOREIGN KEY (ParentMenuId) REFERENCES dbo.Menu(MenuId)
    );

    CREATE TABLE dbo.Role (
        RoleId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Role PRIMARY KEY,
        CompanyCode nvarchar(5) NOT NULL,
        RoleCode nvarchar(20) NOT NULL,
        RoleName nvarchar(100) NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_Role_IsActive DEFAULT (1),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        CONSTRAINT UQ_Role_Company_RoleCode UNIQUE (CompanyCode, RoleCode)
    );

    CREATE INDEX IX_Role_CompanyCode ON dbo.Role(CompanyCode);

    CREATE TABLE dbo.UserRoleMapping (
        UserUid int NOT NULL,
        RoleId int NOT NULL,
        CONSTRAINT PK_UserRoleMapping PRIMARY KEY (UserUid, RoleId),
        CONSTRAINT UQ_UserRoleMapping_User_Role UNIQUE (UserUid, RoleId),
        CONSTRAINT FK_UserRoleMapping_User FOREIGN KEY (UserUid) REFERENCES dbo.userlogin(uid),
        CONSTRAINT FK_UserRoleMapping_Role FOREIGN KEY (RoleId) REFERENCES dbo.Role(RoleId)
    );

    CREATE INDEX IX_UserRoleMapping_UserUid ON dbo.UserRoleMapping(UserUid);

    CREATE TABLE dbo.MenuPermission (
        MenuId int NOT NULL,
        PermissionId int NOT NULL,
        SortOrder int NOT NULL CONSTRAINT DF_MenuPermission_SortOrder DEFAULT (0),
        IsActive bit NOT NULL CONSTRAINT DF_MenuPermission_IsActive DEFAULT (1),
        CONSTRAINT PK_MenuPermission PRIMARY KEY (MenuId, PermissionId),
        CONSTRAINT UQ_MenuPermission_Menu_Permission UNIQUE (MenuId, PermissionId),
        CONSTRAINT FK_MenuPermission_Menu FOREIGN KEY (MenuId) REFERENCES dbo.Menu(MenuId),
        CONSTRAINT FK_MenuPermission_Permission FOREIGN KEY (PermissionId) REFERENCES dbo.Permission(PermissionId)
    );

    CREATE TABLE dbo.RoleMenuPermission (
        RoleMenuPermissionId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_RoleMenuPermission PRIMARY KEY,
        RoleId int NOT NULL,
        MenuId int NOT NULL,
        PermissionId int NOT NULL,
        IsAllowed bit NOT NULL CONSTRAINT DF_RoleMenuPermission_IsAllowed DEFAULT (0),
        CreatedDate datetime2 NULL,
        CreatedBy nvarchar(10) NULL,
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(10) NULL,
        CONSTRAINT UQ_RoleMenuPermission_Role_Menu_Permission UNIQUE (RoleId, MenuId, PermissionId),
        CONSTRAINT FK_RoleMenuPermission_Role FOREIGN KEY (RoleId) REFERENCES dbo.Role(RoleId),
        CONSTRAINT FK_RoleMenuPermission_Menu FOREIGN KEY (MenuId) REFERENCES dbo.Menu(MenuId),
        CONSTRAINT FK_RoleMenuPermission_Permission FOREIGN KEY (PermissionId) REFERENCES dbo.Permission(PermissionId)
    );

    CREATE INDEX IX_RoleMenuPermission_Menu_Permission ON dbo.RoleMenuPermission(MenuId, PermissionId);
END
GO

-- Standard permissions
MERGE dbo.Permission AS t
USING (VALUES
    (N'ACCESS', N'Access', N'Navigation', N'Menu/page access', 1),
    (N'ADD', N'Add', N'Action', NULL, 2),
    (N'EDIT', N'Edit', N'Action', NULL, 3),
    (N'DELETE', N'Delete', N'Action', NULL, 4),
    (N'PRINT', N'Print', N'Action', NULL, 5),
    (N'POST', N'Post', N'Action', NULL, 6),
    (N'ROLLBACK', N'Rollback', N'Action', NULL, 7),
    (N'APPROVE', N'Approve', N'Action', NULL, 8),
    (N'REJECT', N'Reject', N'Action', NULL, 9),
    (N'CANCEL', N'Cancel', N'Action', NULL, 10),
    (N'VOID', N'Void', N'Action', NULL, 11),
    (N'REVERSE', N'Reverse', N'Action', NULL, 12),
    (N'EXPORT', N'Export', N'Action', NULL, 13),
    (N'IMPORT', N'Import', N'Action', NULL, 14),
    (N'EMAIL', N'Email', N'Action', NULL, 15),
    (N'SUBMIT', N'Submit', N'Action', NULL, 16),
    (N'CLOSE', N'Close', N'Action', NULL, 17),
    (N'REOPEN', N'Reopen', N'Action', NULL, 18),
    (N'VIEW_COST', N'View Cost', N'Data', NULL, 19),
    (N'VIEW_PROFIT', N'View Profit', N'Data', NULL, 20)
) AS s(PermissionCode, PermissionName, PermissionType, Description, SortOrder)
ON t.PermissionCode = s.PermissionCode
WHEN NOT MATCHED THEN
    INSERT (PermissionCode, PermissionName, PermissionType, Description, SortOrder, IsActive, CreatedDate, CreatedBy)
    VALUES (s.PermissionCode, s.PermissionName, s.PermissionType, s.Description, s.SortOrder, 1, SYSUTCDATETIME(), N'SEED');
GO

-- DEMO roles
IF NOT EXISTS (SELECT 1 FROM dbo.Role WHERE CompanyCode = N'DEMO' AND RoleCode = N'ADMIN')
    INSERT INTO dbo.Role (CompanyCode, RoleCode, RoleName, IsActive, CreatedDate, CreatedBy)
    VALUES (N'DEMO', N'ADMIN', N'Demo Administrator', 1, SYSUTCDATETIME(), N'SEED');

IF NOT EXISTS (SELECT 1 FROM dbo.Role WHERE CompanyCode = N'DEMO' AND RoleCode = N'USER')
    INSERT INTO dbo.Role (CompanyCode, RoleCode, RoleName, IsActive, CreatedDate, CreatedBy)
    VALUES (N'DEMO', N'USER', N'Demo User', 1, SYSUTCDATETIME(), N'SEED');
GO

-- Seed menus (3-level hierarchy matching menus.xml; XML sync preserves MenuId)
DECLARE @operationsId int;
DECLARE @overviewId int;
DECLARE @securityId int;
DECLARE @adminId int;

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'HOME')
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'HOME', N'Home', NULL, N'/home', 1, 1, 1, SYSUTCDATETIME(), N'SEED');

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'OPERATIONS')
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'OPERATIONS', N'Operations', NULL, NULL, 2, 0, 1, SYSUTCDATETIME(), N'SEED');

SELECT @operationsId = MenuId FROM dbo.Menu WHERE MenuCode = N'OPERATIONS';

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'OVERVIEW')
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'OVERVIEW', N'Overview', @operationsId, NULL, 1, 0, 1, SYSUTCDATETIME(), N'SEED');

SELECT @overviewId = MenuId FROM dbo.Menu WHERE MenuCode = N'OVERVIEW';

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'DASHBOARD')
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'DASHBOARD', N'Dashboard', @overviewId, N'/dashboard', 1, 0, 1, SYSUTCDATETIME(), N'SEED');

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'INVENTORY_DEMO')
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'INVENTORY_DEMO', N'Inventory', @overviewId, N'/inventory-demo', 2, 0, 1, SYSUTCDATETIME(), N'SEED');

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'SECURITY')
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'SECURITY', N'Security', NULL, NULL, 3, 0, 1, SYSUTCDATETIME(), N'SEED');

SELECT @securityId = MenuId FROM dbo.Menu WHERE MenuCode = N'SECURITY';

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'ADMIN')
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'ADMIN', N'Administration', @securityId, NULL, 1, 0, 1, SYSUTCDATETIME(), N'SEED');

SELECT @adminId = MenuId FROM dbo.Menu WHERE MenuCode = N'ADMIN';

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'ADMIN_DEMO')
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'ADMIN_DEMO', N'Admin', @adminId, N'/admin-demo', 1, 0, 1, SYSUTCDATETIME(), N'SEED');

-- Legacy menu removed from app UI; keep row inactive if present
IF EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'ADMIN_PASSWORDS')
    UPDATE dbo.Menu SET IsActive = 0, Route = NULL WHERE MenuCode = N'ADMIN_PASSWORDS';

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'ADMIN_USERS')
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'ADMIN_USERS', N'User Accounts', @adminId, N'/adminuser', 2, 0, 1, SYSUTCDATETIME(), N'SEED');

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'ADMIN_ROLES')
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'ADMIN_ROLES', N'Roles', @adminId, N'/admin/roles', 3, 0, 1, SYSUTCDATETIME(), N'SEED');

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'ADMIN_PERMISSIONS')
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'ADMIN_PERMISSIONS', N'Permissions', @adminId, N'/admin/permissions', 4, 0, 1, SYSUTCDATETIME(), N'SEED');

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'ADMIN_ROLE_PERMISSIONS')
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'ADMIN_ROLE_PERMISSIONS', N'Role Permissions', @adminId, N'/admin/role-permissions', 5, 0, 1, SYSUTCDATETIME(), N'SEED');

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'CHANGE_PASSWORD')
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'CHANGE_PASSWORD', N'Change password', NULL, N'/change-password', 4, 1, 1, SYSUTCDATETIME(), N'SEED');

-- Repair parents if menus already existed from an older seed
UPDATE dbo.Menu SET ParentMenuId = @operationsId, Route = NULL, SortOrder = 1
WHERE MenuCode = N'OVERVIEW';
UPDATE dbo.Menu SET ParentMenuId = @overviewId, SortOrder = 1 WHERE MenuCode = N'DASHBOARD';
UPDATE dbo.Menu SET ParentMenuId = @overviewId, SortOrder = 2 WHERE MenuCode = N'INVENTORY_DEMO';
UPDATE dbo.Menu SET ParentMenuId = @securityId, Route = NULL, SortOrder = 1 WHERE MenuCode = N'ADMIN';
UPDATE dbo.Menu SET ParentMenuId = @adminId, SortOrder = 1 WHERE MenuCode = N'ADMIN_DEMO';
UPDATE dbo.Menu SET IsActive = 0, Route = NULL, SortOrder = 99 WHERE MenuCode = N'ADMIN_PASSWORDS';
UPDATE dbo.Menu SET ParentMenuId = @adminId, Route = N'/adminuser', SortOrder = 2 WHERE MenuCode = N'ADMIN_USERS';
UPDATE dbo.Menu SET ParentMenuId = @adminId, Route = N'/admin/roles', SortOrder = 3 WHERE MenuCode = N'ADMIN_ROLES';
UPDATE dbo.Menu SET ParentMenuId = @adminId, Route = N'/admin/permissions', SortOrder = 4 WHERE MenuCode = N'ADMIN_PERMISSIONS';
UPDATE dbo.Menu SET ParentMenuId = @adminId, Route = N'/admin/role-permissions', SortOrder = 5 WHERE MenuCode = N'ADMIN_ROLE_PERMISSIONS';
UPDATE dbo.Menu SET SortOrder = 2, Route = NULL WHERE MenuCode = N'OPERATIONS';
UPDATE dbo.Menu SET SortOrder = 3, Route = NULL WHERE MenuCode = N'SECURITY';
UPDATE dbo.Menu SET SortOrder = 4 WHERE MenuCode = N'CHANGE_PASSWORD';
GO


-- ACCESS MenuPermission for all seeded menus
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, 1, 1
FROM dbo.Menu m
CROSS JOIN dbo.Permission p
WHERE p.PermissionCode = N'ACCESS'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

-- Applicable action permissions for leaf menus (MenuPermission catalog)
INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode IN (N'ADD', N'EDIT', N'DELETE')
WHERE m.MenuCode = N'INVENTORY_DEMO'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode IN (N'ADD', N'EDIT', N'DELETE')
WHERE m.MenuCode IN (N'ADMIN_USERS', N'ADMIN_ROLES', N'ADMIN_PERMISSIONS', N'ADMIN_ROLE_PERMISSIONS')
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode = N'POST'
WHERE m.MenuCode = N'DASHBOARD'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);
GO

-- Map DEMO users from userlevel
INSERT INTO dbo.UserRoleMapping (UserUid, RoleId)
SELECT u.uid, r.RoleId
FROM dbo.userlogin u
INNER JOIN dbo.Role r ON r.CompanyCode = u.CompanyCode AND r.RoleCode = u.userlevel
WHERE u.CompanyCode = N'DEMO'
  AND u.userlevel IS NOT NULL
  AND NOT EXISTS (
      SELECT 1 FROM dbo.UserRoleMapping m
      WHERE m.UserUid = u.uid AND m.RoleId = r.RoleId);
GO

-- USER role: ACCESS on DASHBOARD and INVENTORY_DEMO
INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsAllowed, CreatedDate, CreatedBy)
SELECT r.RoleId, m.MenuId, p.PermissionId, 1, SYSUTCDATETIME(), N'SEED'
FROM dbo.Role r
CROSS JOIN dbo.Menu m
CROSS JOIN dbo.Permission p
WHERE r.CompanyCode = N'DEMO' AND r.RoleCode = N'USER'
  AND m.MenuCode IN (N'DASHBOARD', N'INVENTORY_DEMO')
  AND p.PermissionCode = N'ACCESS'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.RoleMenuPermission x
      WHERE x.RoleId = r.RoleId AND x.MenuId = m.MenuId AND x.PermissionId = p.PermissionId);
GO
