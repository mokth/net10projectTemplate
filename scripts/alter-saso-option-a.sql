-- SaSO Option A: PK (CompanyCode, BranchCode, SONo).
-- Manual DBA script. Do NOT run at app startup.
-- Rollback = restore from pre-window backup.
-- Also adds SoConsumedQty / LinkDo / SO columns on DO and Invoice detail.
GO

-- Empty DB: prefer create-saso.sql. This alter is for existing tenants.
IF OBJECT_ID(N'dbo.SaSO', N'U') IS NULL
BEGIN
    RAISERROR(N'SaSO does not exist. Run create-saso.sql for empty databases.', 16, 1);
    RETURN;
END
GO

IF OBJECT_ID(N'dbo.SaSODetail', N'U') IS NULL
BEGIN
    RAISERROR(N'SaSODetail does not exist. Run create-saso.sql for empty databases.', 16, 1);
    RETURN;
END
GO

-- Header columns
IF COL_LENGTH(N'dbo.SaSO', N'ClosedReason') IS NULL
    ALTER TABLE dbo.SaSO ADD ClosedReason nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaSO', N'ClosedDate') IS NULL
    ALTER TABLE dbo.SaSO ADD ClosedDate datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.SaSO', N'ClosedBy') IS NULL
    ALTER TABLE dbo.SaSO ADD ClosedBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaSO', N'RowVersion') IS NULL
    ALTER TABLE dbo.SaSO ADD RowVersion rowversion NOT NULL;
GO
IF COL_LENGTH(N'dbo.SaSO', N'LocationCode') IS NULL
    ALTER TABLE dbo.SaSO ADD LocationCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SaSO', N'CurrRate') IS NULL
    ALTER TABLE dbo.SaSO ADD CurrRate decimal(18,6) NOT NULL CONSTRAINT DF_SaSO_CurrRate_Alter DEFAULT (1);
GO

PRINT N'Pre-check: blank CompanyCode/BranchCode on SaSO';
IF EXISTS (
    SELECT 1 FROM dbo.SaSO
    WHERE CompanyCode IS NULL OR LTRIM(RTRIM(CompanyCode)) = N''
       OR BranchCode IS NULL OR LTRIM(RTRIM(BranchCode)) = N'')
BEGIN
    RAISERROR(N'SaSO has null/blank CompanyCode or BranchCode. Backfill before Option A PK change.', 16, 1);
    RETURN;
END
GO

PRINT N'Pre-check: duplicate Option A keys';
IF EXISTS (
    SELECT CompanyCode, BranchCode, SONo
    FROM dbo.SaSO
    GROUP BY CompanyCode, BranchCode, SONo
    HAVING COUNT(*) > 1)
BEGIN
    RAISERROR(N'SaSO has duplicate (CompanyCode, BranchCode, SONo).', 16, 1);
    RETURN;
END
GO

-- Detail qty CHECKs if missing
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_SaSODetail_OrderQty' AND parent_object_id = OBJECT_ID(N'dbo.SaSODetail'))
    ALTER TABLE dbo.SaSODetail ADD CONSTRAINT CK_SaSODetail_OrderQty CHECK (OrderQty > 0);
GO
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_SaSODetail_ShippedQty' AND parent_object_id = OBJECT_ID(N'dbo.SaSODetail'))
    ALTER TABLE dbo.SaSODetail ADD CONSTRAINT CK_SaSODetail_ShippedQty CHECK (ShippedQty >= 0 AND ShippedQty <= OrderQty);
GO
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_SaSODetail_BalanceQty' AND parent_object_id = OBJECT_ID(N'dbo.SaSODetail'))
    ALTER TABLE dbo.SaSODetail ADD CONSTRAINT CK_SaSODetail_BalanceQty CHECK (BalanceQty = OrderQty - ShippedQty);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_SaSO_Status_ClosedReason' AND parent_object_id = OBJECT_ID(N'dbo.SaSO'))
    ALTER TABLE dbo.SaSO ADD CONSTRAINT CK_SaSO_Status_ClosedReason CHECK (
        (Status <> N'CLOSED' AND ClosedReason IS NULL)
        OR
        (Status = N'CLOSED' AND ClosedReason IN (N'FULLY_CONSUMED', N'FORCE_CLOSED'))
    );
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaSO_Company_Branch_Status_SoDate' AND object_id = OBJECT_ID(N'dbo.SaSO'))
    CREATE INDEX IX_SaSO_Company_Branch_Status_SoDate
    ON dbo.SaSO (CompanyCode, BranchCode, Status, SODate);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaSO_Company_Branch_CustCode' AND object_id = OBJECT_ID(N'dbo.SaSO'))
    CREATE INDEX IX_SaSO_Company_Branch_CustCode
    ON dbo.SaSO (CompanyCode, BranchCode, CustCode);
GO

-- DO / Invoice consume columns
IF OBJECT_ID(N'dbo.SaDODetail', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.SaDODetail', N'SoConsumedQty') IS NULL
        ALTER TABLE dbo.SaDODetail ADD SoConsumedQty decimal(18,4) NOT NULL
            CONSTRAINT DF_SaDODetail_SoConsumedQty_Alter DEFAULT (0);

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_SaDODetail_SoConsumedQty' AND parent_object_id = OBJECT_ID(N'dbo.SaDODetail'))
        ALTER TABLE dbo.SaDODetail ADD CONSTRAINT CK_SaDODetail_SoConsumedQty CHECK (SoConsumedQty >= 0);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaDODetail_Company_Branch_SoNo_SoLine' AND object_id = OBJECT_ID(N'dbo.SaDODetail'))
        CREATE INDEX IX_SaDODetail_Company_Branch_SoNo_SoLine
        ON dbo.SaDODetail (CompanyCode, BranchCode, SONo, SOLine);
END
GO

IF OBJECT_ID(N'dbo.SaInvoiceDetail', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'SONo') IS NULL
        ALTER TABLE dbo.SaInvoiceDetail ADD SONo nvarchar(30) NOT NULL
            CONSTRAINT DF_SaInvoiceDetail_SONo_Alter DEFAULT (N'');

    IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'SOLine') IS NULL
        ALTER TABLE dbo.SaInvoiceDetail ADD SOLine smallint NULL;

    IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'CustRel') IS NULL
        ALTER TABLE dbo.SaInvoiceDetail ADD CustRel smallint NULL;

    IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'LinkDo') IS NULL
        ALTER TABLE dbo.SaInvoiceDetail ADD LinkDo bit NOT NULL
            CONSTRAINT DF_SaInvoiceDetail_LinkDo_Alter DEFAULT (0);

    IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'SoConsumedQty') IS NULL
        ALTER TABLE dbo.SaInvoiceDetail ADD SoConsumedQty decimal(18,4) NOT NULL
            CONSTRAINT DF_SaInvoiceDetail_SoConsumedQty_Alter DEFAULT (0);

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = N'CK_SaInvoiceDetail_SoConsumedQty' AND parent_object_id = OBJECT_ID(N'dbo.SaInvoiceDetail'))
        ALTER TABLE dbo.SaInvoiceDetail ADD CONSTRAINT CK_SaInvoiceDetail_SoConsumedQty CHECK (SoConsumedQty >= 0);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaInvoiceDetail_Company_Branch_SoNo_SoLine' AND object_id = OBJECT_ID(N'dbo.SaInvoiceDetail'))
        CREATE INDEX IX_SaInvoiceDetail_Company_Branch_SoNo_SoLine
        ON dbo.SaInvoiceDetail (CompanyCode, BranchCode, SONo, SOLine);
END
GO

PRINT N'alter-saso-option-a.sql completed.';
GO
