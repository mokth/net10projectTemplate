-- Run after create-product-definition-routing.sql and the BOM schema scripts.
-- Existing materials remain unassigned: users choose the consuming process in a draft.
-- Do not infer ownership from process order or duplicate materials for finishing steps.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF COL_LENGTH('dbo.PrBomOperation', 'OperationKey') IS NULL
    ALTER TABLE dbo.PrBomOperation ADD OperationKey uniqueidentifier NOT NULL
        CONSTRAINT DF_PrBomOperation_OperationKey DEFAULT NEWID() WITH VALUES;
IF COL_LENGTH('dbo.PrDefBOM', 'OperationKey') IS NULL
    ALTER TABLE dbo.PrDefBOM ADD OperationKey uniqueidentifier NULL;
COMMIT;
GO
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.PrBomOperation') AND name = 'IX_PrBomOperation_BomHdrId_OperationKey')
    CREATE UNIQUE INDEX IX_PrBomOperation_BomHdrId_OperationKey ON dbo.PrBomOperation(BomHdrID, OperationKey);
IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID('dbo.PrDefBOM') AND name = 'UQ_PrDefBOM_Company_Hdr_ICode')
    ALTER TABLE dbo.PrDefBOM DROP CONSTRAINT UQ_PrDefBOM_Company_Hdr_ICode;
ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.PrDefBOM') AND name = 'UQ_PrDefBOM_Company_Hdr_ICode')
    DROP INDEX UQ_PrDefBOM_Company_Hdr_ICode ON dbo.PrDefBOM;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.PrDefBOM') AND name = 'UQ_PrDefBOM_Company_Hdr_Process_ICode')
    CREATE UNIQUE INDEX UQ_PrDefBOM_Company_Hdr_Process_ICode ON dbo.PrDefBOM(CompanyCode, BomHdrId, OperationKey, ICode);
COMMIT;
GO
