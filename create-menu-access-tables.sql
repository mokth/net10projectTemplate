-- Create Menu Access System Tables
-- Run this script on your production database (ERPWeb)

USE ERPWeb;
GO

-- Check if tables already exist
IF OBJECT_ID('dbo.Permission', 'U') IS NOT NULL
BEGIN
    PRINT 'Tables already exist. Skipping creation.';
    RETURN;
END

PRINT 'Creating Menu Access System Tables...';

-- 1. Permission table
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

-- 2. Menu table
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

-- 3. Role table
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

-- 4. UserRoleMapping table
CREATE TABLE dbo.UserRoleMapping (
    UserUid int NOT NULL,
    RoleId int NOT NULL,
    CONSTRAINT PK_UserRoleMapping PRIMARY KEY (UserUid, RoleId),
    CONSTRAINT UQ_UserRoleMapping_User_Role UNIQUE (UserUid, RoleId),
    CONSTRAINT FK_UserRoleMapping_User FOREIGN KEY (UserUid) REFERENCES dbo.userlogin(uid),
    CONSTRAINT FK_UserRoleMapping_Role FOREIGN KEY (RoleId) REFERENCES dbo.Role(RoleId)
);

CREATE INDEX IX_UserRoleMapping_UserUid ON dbo.UserRoleMapping(UserUid);

-- 5. MenuPermission table
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

-- 6. RoleMenuPermission table
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

-- Insert standard permissions
PRINT 'Inserting standard permissions...';

SET IDENTITY_INSERT dbo.Permission ON;

INSERT INTO dbo.Permission (PermissionId, PermissionCode, PermissionName, PermissionType, Description, SortOrder, IsActive)
VALUES
    (1, N'ACCESS', N'Access', N'Navigation', N'Menu/page access', 1, 1),
    (2, N'ADD', N'Add', N'Action', NULL, 2, 1),
    (3, N'EDIT', N'Edit', N'Action', NULL, 3, 1),
    (4, N'DELETE', N'Delete', N'Action', NULL, 4, 1),
    (5, N'VIEW', N'View', N'Action', NULL, 5, 1),
    (6, N'PRINT', N'Print', N'Action', NULL, 6, 1),
    (7, N'EXPORT', N'Export', N'Action', NULL, 7, 1),
    (8, N'POST', N'Post', N'Action', NULL, 8, 1),
    (9, N'UNPOST', N'Unpost', N'Action', NULL, 9, 1),
    (10, N'CLOSE', N'Close', N'Action', NULL, 10, 1),
    (11, N'REOPEN', N'Reopen', N'Action', NULL, 11, 1),
    (12, N'APPROVE', N'Approve', N'Action', NULL, 12, 1),
    (13, N'REJECT', N'Reject', N'Action', NULL, 13, 1),
    (14, N'VIEW_PRICE', N'View Price', N'Action', NULL, 14, 1);

SET IDENTITY_INSERT dbo.Permission OFF;

-- Insert SYSTEM_ADMIN role for DEMO company
PRINT 'Inserting SYSTEM_ADMIN role...';

INSERT INTO dbo.Role (CompanyCode, RoleCode, RoleName, IsActive, CreatedDate, CreatedBy)
VALUES (N'DEMO', N'SYSTEM_ADMIN', N'System Administrator', 1, GETDATE(), N'SEED');

-- Get the RoleId for SYSTEM_ADMIN
DECLARE @SystemAdminRoleId int;
SELECT @SystemAdminRoleId = RoleId FROM dbo.Role WHERE RoleCode = 'SYSTEM_ADMIN' AND CompanyCode = 'DEMO';

-- Get admin user's uid
DECLARE @AdminUid int;
SELECT @AdminUid = uid FROM dbo.userlogin WHERE id = 'admin' AND CompanyCode = 'DEMO';

-- Map admin user to SYSTEM_ADMIN role
IF @AdminUid IS NOT NULL AND @SystemAdminRoleId IS NOT NULL
BEGIN
    PRINT 'Mapping admin user to SYSTEM_ADMIN role...';
    INSERT INTO dbo.UserRoleMapping (UserUid, RoleId)
    VALUES (@AdminUid, @SystemAdminRoleId);
END

-- Update admin user's userlevel
PRINT 'Updating admin user userlevel...';
UPDATE dbo.userlogin
SET userlevel = N'SYSTEM_ADMIN',
    Updated = GETDATE(),
    UpdatedUID = N'SEED'
WHERE id = N'admin'
  AND CompanyCode = N'DEMO'
  AND (userlevel IS NULL OR userlevel <> N'SYSTEM_ADMIN');

PRINT 'Menu Access System Tables created successfully!';
PRINT '';
PRINT 'Next steps:';
PRINT '1. Run the menu sync to populate Menu table from menus.xml';
PRINT '2. Assign menu permissions to roles';
PRINT '3. Test admin login';

GO