SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF COL_LENGTH(N'dbo.IvTrxHistory', N'LedgerVersion') IS NULL ALTER TABLE dbo.IvTrxHistory ADD LedgerVersion tinyint NULL;
IF COL_LENGTH(N'dbo.IvTrxHistory', N'LedgerEpochID') IS NULL ALTER TABLE dbo.IvTrxHistory ADD LedgerEpochID bigint NULL;
IF COL_LENGTH(N'dbo.IvTrxHistory', N'StockPostingID') IS NULL ALTER TABLE dbo.IvTrxHistory ADD StockPostingID bigint NULL;
IF COL_LENGTH(N'dbo.IvTrxHistory', N'PostingLineNo') IS NULL ALTER TABLE dbo.IvTrxHistory ADD PostingLineNo int NULL;
IF COL_LENGTH(N'dbo.IvTrxHistory', N'DocumentRevision') IS NULL ALTER TABLE dbo.IvTrxHistory ADD DocumentRevision int NULL;
IF COL_LENGTH(N'dbo.IvTrxHistory', N'EntryRole') IS NULL ALTER TABLE dbo.IvTrxHistory ADD EntryRole nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.IvTrxHistory', N'ReversesHistoryID') IS NULL ALTER TABLE dbo.IvTrxHistory ADD ReversesHistoryID int NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_IvTrxHistory_V2')
    ALTER TABLE dbo.IvTrxHistory WITH NOCHECK ADD CONSTRAINT CK_IvTrxHistory_V2 CHECK
        (LedgerVersion IS NULL OR (LedgerVersion = 2 AND LedgerEpochID IS NOT NULL AND StockPostingID IS NOT NULL AND PostingLineNo > 0 AND DocumentRevision >= 0 AND EntryRole IS NOT NULL));
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_IvTrxHistory_StockPosting')
    ALTER TABLE dbo.IvTrxHistory ADD CONSTRAINT FK_IvTrxHistory_StockPosting
        FOREIGN KEY (CompanyCode, BranchCode, StockPostingID) REFERENCES dbo.StockPosting(CompanyCode, BranchCode, Id);
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_IvTrxHistory_Reverses')
    ALTER TABLE dbo.IvTrxHistory ADD CONSTRAINT FK_IvTrxHistory_Reverses
        FOREIGN KEY (ReversesHistoryID) REFERENCES dbo.IvTrxHistory(ID);
GO

IF EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.IvTrxHistory')
      AND name = N'UQ_IvTrxHistory_Company_Branch_Batch_Line'
      AND (filter_definition IS NULL OR filter_definition NOT LIKE N'%LedgerVersion%')
)
    DROP INDEX UQ_IvTrxHistory_Company_Branch_Batch_Line ON dbo.IvTrxHistory;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.IvTrxHistory') AND name = N'UQ_IvTrxHistory_Company_Branch_Batch_Line')
    CREATE UNIQUE INDEX UQ_IvTrxHistory_Company_Branch_Batch_Line
        ON dbo.IvTrxHistory(CompanyCode, BranchCode, BatchNo, TrxLineNo) WHERE LedgerVersion IS NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.IvTrxHistory') AND name = N'UQ_IvTrxHistory_V2_PostingLine')
    CREATE UNIQUE INDEX UQ_IvTrxHistory_V2_PostingLine
        ON dbo.IvTrxHistory(StockPostingID, PostingLineNo) WHERE StockPostingID IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.IvTrxHistory') AND name = N'UQ_IvTrxHistory_V2_Reversal')
    CREATE UNIQUE INDEX UQ_IvTrxHistory_V2_Reversal
        ON dbo.IvTrxHistory(ReversesHistoryID) WHERE ReversesHistoryID IS NOT NULL;
GO

CREATE OR ALTER TRIGGER dbo.TR_IvTrxHistory_Seal
ON dbo.IvTrxHistory
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted d JOIN dbo.StockPosting p ON p.Id = d.StockPostingID WHERE p.SealedAtUtc IS NOT NULL)
       OR EXISTS (SELECT 1 FROM inserted i JOIN dbo.StockPosting p ON p.Id = i.StockPostingID WHERE p.SealedAtUtc IS NOT NULL)
        THROW 51004, 'SEALED_INVENTORY_HISTORY_IMMUTABLE', 1;
END;
GO
