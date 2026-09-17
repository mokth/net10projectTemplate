-- Sales Invoice v1 schema (manual DBA script — do NOT run at app startup).
-- Target database: ERPWeb (ConnectionStrings:DefaultConnection). Do not USE ERPLiteEx.
-- Idempotent: IF OBJECT_ID / COL_LENGTH / sys.indexes.
-- Option A PK: (CompanyCode, BranchCode, InvNo).
--
-- Filtered indexes created below require ANSI_NULLS ON and QUOTED_IDENTIFIER ON.
-- SSMS sets both, but sqlcmd defaults QUOTED_IDENTIFIER OFF, which makes
-- CREATE INDEX ... WHERE fail with Msg 1934. Set them explicitly so the script behaves
-- the same no matter which client runs it.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo.SaTaxGroup', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaTaxGroup (
        CompanyCode nvarchar(10) NOT NULL,
        TaxGrCode nvarchar(20) NOT NULL,
        TaxGrDesc nvarchar(100) NULL,
        Percentage decimal(18,6) NOT NULL CONSTRAINT DF_SaTaxGroup_Percentage DEFAULT (0),
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        Updated datetime2 NULL,
        UpdatedUID nvarchar(20) NULL,
        BranchCode nvarchar(10) NULL,
        LocationCode nvarchar(20) NULL,
        CONSTRAINT PK_SaTaxGroup PRIMARY KEY (CompanyCode, TaxGrCode)
    );
END
GO

IF COL_LENGTH(N'dbo.SaTaxGroup', N'TaxGrDesc') IS NULL
    ALTER TABLE dbo.SaTaxGroup ADD TaxGrDesc nvarchar(100) NULL;
GO

IF COL_LENGTH(N'dbo.SaTaxGroup', N'Percentage') IS NULL
    ALTER TABLE dbo.SaTaxGroup ADD Percentage decimal(18,6) NOT NULL CONSTRAINT DF_SaTaxGroup_Percentage DEFAULT (0);
GO

IF COL_LENGTH(N'dbo.SaTaxGroup', N'CompanyCode') IS NULL
    ALTER TABLE dbo.SaTaxGroup ADD CompanyCode nvarchar(10) NOT NULL CONSTRAINT DF_SaTaxGroup_CompanyCode DEFAULT (N'');
GO

IF COL_LENGTH(N'dbo.SaTaxGroup', N'BranchCode') IS NULL
    ALTER TABLE dbo.SaTaxGroup ADD BranchCode nvarchar(10) NULL;
GO

IF COL_LENGTH(N'dbo.SaTaxGroup', N'LocationCode') IS NULL
    ALTER TABLE dbo.SaTaxGroup ADD LocationCode nvarchar(20) NULL;
GO

-- Re-align the SaTaxGroup PK to (CompanyCode, TaxGrCode) to match SaTaxGroupConfiguration.
-- Three hazards are handled here:
--   1. CompanyCode is NULLABLE in databases created before this script (restored/legacy schemas).
--      ADD PRIMARY KEY on a nullable column fails with Msg 8111, surfaced as
--      Msg 1750 "Could not create constraint or index". Backfill + NOT NULL fixes it.
--   2. ADD CONSTRAINT ... PRIMARY KEY is validated when the batch is *compiled*, so a nullable
--      CompanyCode raises Msg 8111 before the batch starts running: TRY/CATCH cannot catch it and
--      even PRINT output is lost. Every DDL statement below therefore runs through
--      sp_executesql, which is compiled at execution time and is genuinely catchable.
--   3. The rebuild used to DROP the existing PK before attempting the ADD, so any failure left
--      SaTaxGroup with no primary key at all. DROP + ADD now run in one atomic transaction and
--      roll back together, so a failed rebuild can never destroy the existing key.
IF OBJECT_ID(N'dbo.SaTaxGroup', N'U') IS NOT NULL
BEGIN
    DECLARE @taxPkName sysname;
    DECLARE @taxPkCols int;
    DECLARE @taxPkHasCc int;
    DECLARE @taxPkHasTg int;
    DECLARE @taxCcNullable bit;
    DECLARE @taxSql nvarchar(300);

    SELECT @taxPkName = kc.name
    FROM sys.key_constraints kc
    WHERE kc.parent_object_id = OBJECT_ID(N'dbo.SaTaxGroup')
      AND kc.[type] = N'PK';

    -- PK shape is inspected with COUNT comparisons rather than FOR XML: XML methods require
    -- QUOTED_IDENTIFIER ON, which sqlcmd does not set by default (Msg 1934).
    SELECT @taxPkCols = COUNT(*)
    FROM sys.index_columns ic
    WHERE ic.object_id = OBJECT_ID(N'dbo.SaTaxGroup')
      AND ic.index_id = (SELECT kc2.unique_index_id
                         FROM sys.key_constraints kc2
                         WHERE kc2.parent_object_id = OBJECT_ID(N'dbo.SaTaxGroup')
                           AND kc2.[type] = N'PK')
      AND ic.is_included_column = 0;

    SELECT @taxPkHasCc = COUNT(*)
    FROM sys.index_columns ic
    INNER JOIN sys.columns c
        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE ic.object_id = OBJECT_ID(N'dbo.SaTaxGroup')
      AND ic.index_id = (SELECT kc2.unique_index_id
                         FROM sys.key_constraints kc2
                         WHERE kc2.parent_object_id = OBJECT_ID(N'dbo.SaTaxGroup')
                           AND kc2.[type] = N'PK')
      AND c.name = N'CompanyCode';

    SELECT @taxPkHasTg = COUNT(*)
    FROM sys.index_columns ic
    INNER JOIN sys.columns c
        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE ic.object_id = OBJECT_ID(N'dbo.SaTaxGroup')
      AND ic.index_id = (SELECT kc2.unique_index_id
                         FROM sys.key_constraints kc2
                         WHERE kc2.parent_object_id = OBJECT_ID(N'dbo.SaTaxGroup')
                           AND kc2.[type] = N'PK')
      AND c.name = N'TaxGrCode';

    SELECT @taxCcNullable = c.is_nullable
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'dbo.SaTaxGroup') AND c.name = N'CompanyCode';

    IF @taxCcNullable IS NULL
    BEGIN
        PRINT N'SKIPPED SaTaxGroup PK: the CompanyCode column is missing.';
    END
    ELSE IF @taxPkCols = 2 AND @taxPkHasCc = 1 AND @taxPkHasTg = 1 AND @taxCcNullable = 0
    BEGIN
        PRINT N'SaTaxGroup PK already (CompanyCode, TaxGrCode). Skipping.';
    END
    ELSE
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;

            IF @taxCcNullable = 1
            BEGIN
                UPDATE dbo.SaTaxGroup SET CompanyCode = N'' WHERE CompanyCode IS NULL;
                -- nvarchar(10) matches SaTaxGroupConfiguration (CompanyCode HasMaxLength(10)).
                EXEC sp_executesql N'ALTER TABLE dbo.SaTaxGroup ALTER COLUMN CompanyCode nvarchar(10) NOT NULL;';
            END

            IF @taxPkName IS NOT NULL
            BEGIN
                SET @taxSql = N'ALTER TABLE dbo.SaTaxGroup DROP CONSTRAINT ' + QUOTENAME(@taxPkName) + N';';
                EXEC sp_executesql @taxSql;
            END

            EXEC sp_executesql N'ALTER TABLE dbo.SaTaxGroup ADD CONSTRAINT PK_SaTaxGroup PRIMARY KEY (CompanyCode, TaxGrCode);';

            COMMIT TRANSACTION;
            PRINT N'SaTaxGroup PK rebuilt as (CompanyCode, TaxGrCode).';
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
            PRINT N'SKIPPED SaTaxGroup PK rebuild: ' + ERROR_MESSAGE();
        END CATCH
    END
END
GO

IF OBJECT_ID(N'dbo.SaInvoice', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaInvoice (
        CompanyCode nvarchar(10) NOT NULL,
        InvNo nvarchar(30) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        LocationCode nvarchar(10) NULL,
        CustCode nvarchar(60) NOT NULL,
        InvDate datetime2 NOT NULL,
        Status nvarchar(20) NOT NULL,
        DONo nvarchar(30) NOT NULL,
        Currency nvarchar(20) NULL,
        CurrRate decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoice_CurrRate DEFAULT (1),
        GrossAmnt decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoice_GrossAmnt DEFAULT (0),
        Taxes decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoice_Taxes DEFAULT (0),
        TotAmnt decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoice_TotAmnt DEFAULT (0),
        InvPrefix nvarchar(20) NULL,
        PayCode nvarchar(20) NULL,
        TaxGrCode nvarchar(20) NULL,
        SalesmanCode nvarchar(20) NULL,
        PoNo nvarchar(50) NULL,
        Remark nvarchar(500) NULL,
        CustName nvarchar(200) NULL,
        InvName nvarchar(100) NULL,
        InvAddress1 nvarchar(100) NULL,
        InvAddress2 nvarchar(100) NULL,
        InvAddress3 nvarchar(100) NULL,
        InvAddress4 nvarchar(100) NULL,
        InvCity nvarchar(50) NULL,
        InvState nvarchar(50) NULL,
        InvPostalCode nvarchar(20) NULL,
        InvCountry nvarchar(50) NULL,
        InvTel nvarchar(50) NULL,
        InvFax nvarchar(50) NULL,
        ShipName nvarchar(100) NULL,
        ShipAddress1 nvarchar(100) NULL,
        ShipAddress2 nvarchar(100) NULL,
        ShipAddress3 nvarchar(100) NULL,
        ShipCity nvarchar(50) NULL,
        ShipState nvarchar(50) NULL,
        ShipPostalCode nvarchar(20) NULL,
        ShipCountry nvarchar(50) NULL,
        ShipTel nvarchar(50) NULL,
        ShipFax nvarchar(50) NULL,
        PostedDate datetime NULL,
        PostedBy nvarchar(20) NULL,
        RollbackDate datetime NULL,
        RollbackBy nvarchar(20) NULL,
        -- LHDN e-Invoice state (names/spellings mirror dbo.SaCDN and are the EF contract).
        IRNMCancelOn datetime2 NULL,
        IRBMSubmitID nvarchar(50) NULL,
        IRBMUUID nvarchar(50) NULL,
        IRBMORIUUID nvarchar(50) NULL,
        IRBMSentOn datetime2 NULL,
        IRBMValidOn datetime2 NULL,
        IRBMError nvarchar(500) NULL,
        IRBMStatus nvarchar(50) NULL,
        IRBMOutcome nvarchar(30) NULL,
        Created datetime2 NULL,
        UserID nvarchar(20) NULL,
        -- dbo.SaInvoice uses ModifiedDate/ModifiedBy, NOT the SaCust/SaSo/SaDo/SaCdn pair
        -- Updated/UpdatedUID. SaInvoiceConfiguration maps to these names.
        ModifiedDate datetime2 NULL,
        ModifiedBy nvarchar(40) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_SaInvoice PRIMARY KEY (CompanyCode, BranchCode, InvNo)
    );
END
GO

IF COL_LENGTH(N'dbo.SaInvoice', N'BranchCode') IS NULL ALTER TABLE dbo.SaInvoice ADD BranchCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'LocationCode') IS NULL ALTER TABLE dbo.SaInvoice ADD LocationCode nvarchar(10) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'PostedDate') IS NULL ALTER TABLE dbo.SaInvoice ADD PostedDate datetime NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'PostedBy') IS NULL ALTER TABLE dbo.SaInvoice ADD PostedBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'RollbackDate') IS NULL ALTER TABLE dbo.SaInvoice ADD RollbackDate datetime NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'RollbackBy') IS NULL ALTER TABLE dbo.SaInvoice ADD RollbackBy nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'RowVersion') IS NULL ALTER TABLE dbo.SaInvoice ADD RowVersion rowversion NOT NULL;
GO
-- Audit pair + LHDN e-Invoice state. SaInvoiceConfiguration maps every one of these, and a
-- full-entity read (the invoice list) selects ALL mapped columns - so a single missing column
-- breaks the page with "Invalid column name" even though the grid shows none of them.
IF COL_LENGTH(N'dbo.SaInvoice', N'ModifiedDate') IS NULL ALTER TABLE dbo.SaInvoice ADD ModifiedDate datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ModifiedBy') IS NULL ALTER TABLE dbo.SaInvoice ADD ModifiedBy nvarchar(40) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'IRNMCancelOn') IS NULL ALTER TABLE dbo.SaInvoice ADD IRNMCancelOn datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMSubmitID') IS NULL ALTER TABLE dbo.SaInvoice ADD IRBMSubmitID nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMUUID') IS NULL ALTER TABLE dbo.SaInvoice ADD IRBMUUID nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMORIUUID') IS NULL ALTER TABLE dbo.SaInvoice ADD IRBMORIUUID nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMSentOn') IS NULL ALTER TABLE dbo.SaInvoice ADD IRBMSentOn datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMValidOn') IS NULL ALTER TABLE dbo.SaInvoice ADD IRBMValidOn datetime2 NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMError') IS NULL ALTER TABLE dbo.SaInvoice ADD IRBMError nvarchar(500) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMStatus') IS NULL ALTER TABLE dbo.SaInvoice ADD IRBMStatus nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'IRBMOutcome') IS NULL ALTER TABLE dbo.SaInvoice ADD IRBMOutcome nvarchar(30) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvPrefix') IS NULL ALTER TABLE dbo.SaInvoice ADD InvPrefix nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'PayCode') IS NULL ALTER TABLE dbo.SaInvoice ADD PayCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'TaxGrCode') IS NULL ALTER TABLE dbo.SaInvoice ADD TaxGrCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'SalesmanCode') IS NULL ALTER TABLE dbo.SaInvoice ADD SalesmanCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'PoNo') IS NULL ALTER TABLE dbo.SaInvoice ADD PoNo nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'Remark') IS NULL ALTER TABLE dbo.SaInvoice ADD Remark nvarchar(500) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'CustName') IS NULL ALTER TABLE dbo.SaInvoice ADD CustName nvarchar(200) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvName') IS NULL ALTER TABLE dbo.SaInvoice ADD InvName nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvAddress1') IS NULL ALTER TABLE dbo.SaInvoice ADD InvAddress1 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvAddress2') IS NULL ALTER TABLE dbo.SaInvoice ADD InvAddress2 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvAddress3') IS NULL ALTER TABLE dbo.SaInvoice ADD InvAddress3 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvAddress4') IS NULL ALTER TABLE dbo.SaInvoice ADD InvAddress4 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvCity') IS NULL ALTER TABLE dbo.SaInvoice ADD InvCity nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvState') IS NULL ALTER TABLE dbo.SaInvoice ADD InvState nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvPostalCode') IS NULL ALTER TABLE dbo.SaInvoice ADD InvPostalCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvCountry') IS NULL ALTER TABLE dbo.SaInvoice ADD InvCountry nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvTel') IS NULL ALTER TABLE dbo.SaInvoice ADD InvTel nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'InvFax') IS NULL ALTER TABLE dbo.SaInvoice ADD InvFax nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipName') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipName nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipAddress1') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipAddress1 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipAddress2') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipAddress2 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipAddress3') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipAddress3 nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipCity') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipCity nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipState') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipState nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipPostalCode') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipPostalCode nvarchar(20) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipCountry') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipCountry nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipTel') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipTel nvarchar(50) NULL;
GO
IF COL_LENGTH(N'dbo.SaInvoice', N'ShipFax') IS NULL ALTER TABLE dbo.SaInvoice ADD ShipFax nvarchar(50) NULL;
GO

-- Department / Project reference masters (MsDept / MsProject). Optional; nvarchar(20) matches
-- the master code width and the TruncateOptional(..., 20) save paths.
-- Names must match SaInvoiceConfiguration: Dept, and ProjId mapped as HasColumnName("ProjID").
IF OBJECT_ID(N'dbo.SaInvoice', N'U') IS NULL
BEGIN
    RAISERROR(N'dbo.SaInvoice is missing. Create it before adding Dept / ProjID.', 16, 1);
END
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.SaInvoice', N'Dept') IS NULL
    BEGIN
        ALTER TABLE dbo.SaInvoice ADD Dept nvarchar(20) NULL;
        PRINT N'Added SaInvoice.Dept';
    END

    IF COL_LENGTH(N'dbo.SaInvoice', N'ProjID') IS NULL
    BEGIN
        ALTER TABLE dbo.SaInvoice ADD ProjID nvarchar(20) NULL;
        PRINT N'Added SaInvoice.ProjID';
    END
END
GO

IF OBJECT_ID(N'dbo.SaInvoiceDetail', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaInvoiceDetail (
        ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SaInvoiceDetail PRIMARY KEY,
        OriginalUnitPrice decimal(18,4) NULL,
        OverrideReason nvarchar(100) NULL,
        CompanyCode nvarchar(10) NOT NULL,
        BranchCode nvarchar(10) NOT NULL,
        InvNo nvarchar(30) NOT NULL,
        Line int NOT NULL,
        ICode nvarchar(30) NULL,
        IDesc nvarchar(200) NULL,
        Qty decimal(18,4) NOT NULL CONSTRAINT DF_SaInvoiceDetail_Qty DEFAULT (0),
        StdQty decimal(18,4) NOT NULL CONSTRAINT DF_SaInvoiceDetail_StdQty DEFAULT (0),
        StdUom nvarchar(10) NULL,
        FrWarehouse nvarchar(20) NULL,
        UnitPrice decimal(18,4) NOT NULL CONSTRAINT DF_SaInvoiceDetail_UnitPrice DEFAULT (0),
        Amount decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoiceDetail_Amount DEFAULT (0),
        ItemDiscount decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscount DEFAULT (0),
        ItemDiscount2 decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscount2 DEFAULT (0),
        ItemDiscount3 decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscount3 DEFAULT (0),
        ItemDiscount4 decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscount4 DEFAULT (0),
        ItemDiscount5 decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscount5 DEFAULT (0),
        ItemDiscount6 decimal(18,6) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscount6 DEFAULT (0),
        ItemDiscAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscAmount DEFAULT (0),
        ItemDiscAmount1 decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoiceDetail_ItemDiscAmount1 DEFAULT (0),
        IsInclusive bit NOT NULL CONSTRAINT DF_SaInvoiceDetail_IsInclusive DEFAULT (0),
        TaxGrCode nvarchar(20) NULL,
        TaxAmt decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoiceDetail_TaxAmt DEFAULT (0),
        NetAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoiceDetail_NetAmount DEFAULT (0),
        LocalAmount decimal(18,2) NOT NULL CONSTRAINT DF_SaInvoiceDetail_LocalAmount DEFAULT (0),
        OrderType nvarchar(20) NULL,
        StockControl bit NOT NULL CONSTRAINT DF_SaInvoiceDetail_StockControl DEFAULT (1),
        SellingGlCode nvarchar(20) NULL,
        Remarks nvarchar(250) NULL,
        CONSTRAINT FK_SaInvoiceDetail_Header FOREIGN KEY (CompanyCode, BranchCode, InvNo)
            REFERENCES dbo.SaInvoice (CompanyCode, BranchCode, InvNo)
    );
END
GO

IF COL_LENGTH(N'dbo.SaInvoiceDetail', N'BranchCode') IS NULL
    ALTER TABLE dbo.SaInvoiceDetail ADD BranchCode nvarchar(10) NULL;
GO

IF OBJECT_ID(N'dbo.SaInvoiceDetail', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.SaInvoiceDetail', N'BranchCode') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SaInvoiceDetail_Company_Branch_InvNo_Line' AND object_id = OBJECT_ID(N'dbo.SaInvoiceDetail'))
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SaInvoiceDetail_Company_InvNo_Line' AND object_id = OBJECT_ID(N'dbo.SaInvoiceDetail'))
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.SaInvoiceDetail
               GROUP BY CompanyCode, BranchCode, InvNo, Line
               HAVING COUNT(*) > 1)
    BEGIN
        PRINT N'SKIPPED UQ_SaInvoiceDetail_Company_Branch_InvNo_Line: duplicate (CompanyCode, BranchCode, InvNo, Line) rows exist. Resolve them, then re-run this script.';
        SELECT TOP (20) CompanyCode, BranchCode, InvNo, Line, COUNT(*) AS DuplicateCount
        FROM dbo.SaInvoiceDetail
        GROUP BY CompanyCode, BranchCode, InvNo, Line
        HAVING COUNT(*) > 1
        ORDER BY DuplicateCount DESC;
    END
    ELSE
    BEGIN
        CREATE UNIQUE INDEX UQ_SaInvoiceDetail_Company_Branch_InvNo_Line
        ON dbo.SaInvoiceDetail (CompanyCode, BranchCode, InvNo, Line);
        PRINT N'Created UQ_SaInvoiceDetail_Company_Branch_InvNo_Line';
    END
END
GO

IF OBJECT_ID(N'dbo.SaInvoice', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaInvoice_Company_Status' AND object_id = OBJECT_ID(N'dbo.SaInvoice'))
BEGIN
    CREATE INDEX IX_SaInvoice_Company_Status
    ON dbo.SaInvoice (CompanyCode, Status);
    PRINT N'Created IX_SaInvoice_Company_Status';
END
GO

IF OBJECT_ID(N'dbo.SaInvoice', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SaInvoice_Company_CustCode' AND object_id = OBJECT_ID(N'dbo.SaInvoice'))
BEGIN
    CREATE INDEX IX_SaInvoice_Company_CustCode
    ON dbo.SaInvoice (CompanyCode, CustCode);
    PRINT N'Created IX_SaInvoice_Company_CustCode';
END
GO

-- The SP RefNo guard is duplicated in create-sado.sql / alter-sado-option-a.sql. EF and the
-- current scripts name this index UQ_IvTrxBatch_SP_Ref; UQ_IvTrxBatch_SP_RefNo is the older name.
-- Both are treated as "already present" so a deploy never ends up with two identical unique
-- indexes, and the create is skipped (instead of failing) when duplicate SP rows exist.
IF OBJECT_ID(N'dbo.IvTrxBatch', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IvTrxBatch_SP_Ref' AND object_id = OBJECT_ID(N'dbo.IvTrxBatch'))
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_IvTrxBatch_SP_RefNo' AND object_id = OBJECT_ID(N'dbo.IvTrxBatch'))
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.IvTrxBatch
               WHERE TrxType = N'SP' AND RefNo IS NOT NULL
               GROUP BY CompanyCode, BranchCode, RefNo
               HAVING COUNT(*) > 1)
    BEGIN
        PRINT N'SKIPPED UQ_IvTrxBatch_SP_Ref: duplicate (CompanyCode, BranchCode, RefNo) SP rows exist. Resolve them, then re-run this script.';
        SELECT TOP (20) CompanyCode, BranchCode, RefNo, COUNT(*) AS DuplicateCount
        FROM dbo.IvTrxBatch
        WHERE TrxType = N'SP' AND RefNo IS NOT NULL
        GROUP BY CompanyCode, BranchCode, RefNo
        HAVING COUNT(*) > 1
        ORDER BY DuplicateCount DESC;
    END
    ELSE
    BEGIN
        CREATE UNIQUE INDEX UQ_IvTrxBatch_SP_Ref
        ON dbo.IvTrxBatch (CompanyCode, BranchCode, RefNo)
        WHERE TrxType = N'SP' AND RefNo IS NOT NULL;
        PRINT N'Created UQ_IvTrxBatch_SP_Ref';
    END
END
GO

PRINT N'create-sainvoice.sql complete. Review any SKIPPED messages above.';
GO
