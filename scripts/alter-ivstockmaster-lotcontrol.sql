USE ERPLiteEx;
GO

IF COL_LENGTH('dbo.IvStockMaster', 'LotControl') IS NULL
BEGIN
    ALTER TABLE dbo.IvStockMaster
        ADD LotControl bit NOT NULL
            CONSTRAINT DF_IvStockMaster_LotControl DEFAULT (0);
END
GO
