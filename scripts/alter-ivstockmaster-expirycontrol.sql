-- Deployment artifact: separate item expiry policy from lot tracking.
-- Do NOT run automatically at application startup. Apply via the normal SQL deploy process.
--
-- The backfill is intentionally inside the first-create block. Re-running this script must not
-- overwrite policies selected by users after the column has been deployed.

IF COL_LENGTH('dbo.IvStockMaster', 'ExpiryControl') IS NULL
BEGIN
    ALTER TABLE dbo.IvStockMaster
        ADD ExpiryControl nvarchar(10) NOT NULL
            CONSTRAINT DF_IvStockMaster_ExpiryControl DEFAULT ('NONE');

    UPDATE dbo.IvStockMaster
       SET ExpiryControl = 'REQUIRED'
     WHERE LotControl = 1;
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.IvStockMaster')
      AND name = N'CK_IvStockMaster_ExpiryControl')
BEGIN
    ALTER TABLE dbo.IvStockMaster WITH CHECK
        ADD CONSTRAINT CK_IvStockMaster_ExpiryControl
        CHECK (ExpiryControl IN ('NONE', 'OPTIONAL', 'REQUIRED'));

    ALTER TABLE dbo.IvStockMaster
        CHECK CONSTRAINT CK_IvStockMaster_ExpiryControl;
END
GO
