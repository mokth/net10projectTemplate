USE ERPLiteEx;
GO

IF OBJECT_ID('dbo.MsRunningNo', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MsRunningNo (
        CompanyCode nvarchar(5) NOT NULL,
        DocKey nvarchar(20) NOT NULL,
        LastNo int NOT NULL CONSTRAINT DF_MsRunningNo_LastNo DEFAULT (0),
        Created datetime2 NULL,
        UserID nvarchar(10) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(10) NULL,
        CONSTRAINT PK_MsRunningNo PRIMARY KEY (CompanyCode, DocKey)
    );
END
GO
