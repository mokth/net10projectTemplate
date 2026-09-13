-- Purchase Contract / CJ schema (manual DBA script — do NOT run at app startup).
-- PK: (CompanyCode, BranchCode, CJNo, RelNo).
GO

IF OBJECT_ID(N'dbo.POCJ', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POCJ (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        CJNo nvarchar(30) NOT NULL,
        RelNo int NOT NULL,
        CJDtFrom datetime2 NULL,
        CJDtTo datetime2 NULL,
        CJOrderQty decimal(18,4) NOT NULL CONSTRAINT DF_POCJ_CJOrderQty DEFAULT (0),
        CJBalQty decimal(18,4) NOT NULL CONSTRAINT DF_POCJ_CJBalQty DEFAULT (0),
        ICode nvarchar(30) NULL,
        IDesc nvarchar(200) NULL,
        Active bit NOT NULL CONSTRAINT DF_POCJ_Active DEFAULT (1),
        Remark nvarchar(500) NULL,
        LocationCode nvarchar(10) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_POCJ PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, CJNo, RelNo)
    );
END
GO

IF OBJECT_ID(N'dbo.POCJDetail', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POCJDetail (
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        CJNo nvarchar(30) NOT NULL,
        RelNo int NOT NULL,
        Line int NOT NULL,
        ICode nvarchar(30) NULL,
        IDesc nvarchar(200) NULL,
        CJDOrderQty decimal(18,4) NOT NULL CONSTRAINT DF_POCJDetail_CJDOrderQty DEFAULT (0),
        CJDBalQty decimal(18,4) NOT NULL CONSTRAINT DF_POCJDetail_CJDBalQty DEFAULT (0),
        CONSTRAINT PK_POCJDetail PRIMARY KEY CLUSTERED (CompanyCode, BranchCode, CJNo, RelNo, Line),
        CONSTRAINT FK_POCJDetail_POCJ FOREIGN KEY (CompanyCode, BranchCode, CJNo, RelNo)
            REFERENCES dbo.POCJ (CompanyCode, BranchCode, CJNo, RelNo) ON DELETE CASCADE
    );
END
GO
