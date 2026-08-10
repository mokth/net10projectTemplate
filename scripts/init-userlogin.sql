IF DB_ID('ERPLiteEx') IS NULL
    CREATE DATABASE ERPLiteEx;
GO

USE ERPLiteEx;
GO

IF OBJECT_ID('dbo.userlogin', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.userlogin (
        uid int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        id nvarchar(10) NOT NULL,
        name nvarchar(50) NOT NULL,
        password nvarchar(100) NOT NULL,
        email nvarchar(50) NULL,
        mobileno nvarchar(20) NULL,
        active bit NULL,
        userlevel nvarchar(20) NULL,
        Created datetime NULL,
        Updated datetime NULL,
        UserID nvarchar(10) NULL,
        UpdatedUID nvarchar(10) NULL,
        CompanyCode nvarchar(5) NOT NULL,
        BranchCode nvarchar(5) NOT NULL,
        LocationCode nvarchar(10) NOT NULL,
        changepass bit NOT NULL CONSTRAINT DF_userlogin_changepass DEFAULT (0),
        ImagePath nvarchar(100) NULL
    );

    CREATE UNIQUE INDEX IX_userlogin_id_company ON dbo.userlogin(id, CompanyCode);
END
GO

-- Password for all seed users: Demo@123 (BCrypt)
DECLARE @hash nvarchar(100) = N'$2a$11$OGTOHFW7V/Xsy.oHE5qtA.x0tMqbFqByKo/rHqfxthhL85sZCEz9u';

IF NOT EXISTS (SELECT 1 FROM dbo.userlogin WHERE id = N'admin' AND CompanyCode = N'DEMO')
BEGIN
    INSERT INTO dbo.userlogin (id, name, password, email, active, userlevel, Created, UserID, CompanyCode, BranchCode, LocationCode, changepass)
    VALUES
    (N'admin', N'Demo Admin', @hash, N'admin@demo.local', 1, N'SYSTEM_ADMIN', GETDATE(), N'ADM01', N'DEMO', N'HQ', N'MAIN', 0),
    (N'chguser', N'Change Pass User', @hash, N'chg@demo.local', 1, N'USER', GETDATE(), N'USR01', N'DEMO', N'HQ', N'MAIN', 1),
    (N'inactive', N'Inactive User', @hash, N'off@demo.local', 0, N'USER', GETDATE(), N'USR02', N'DEMO', N'HQ', N'MAIN', 0);
END
GO

-- Promote existing DEMO platform admin to SYSTEM_ADMIN (company-directory access)
UPDATE dbo.userlogin
SET userlevel = N'SYSTEM_ADMIN',
    Updated = GETDATE(),
    UpdatedUID = N'SEED'
WHERE id = N'admin'
  AND CompanyCode = N'DEMO'
  AND (userlevel IS NULL OR userlevel <> N'SYSTEM_ADMIN');
GO
