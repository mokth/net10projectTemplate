-- Widen IvLot.SupplierCode to match PoSuppliers.SuppCode (60).
-- Idempotent: only alters when the current length is shorter than 60.

IF COL_LENGTH(N'dbo.IvLot', N'SupplierCode') IS NOT NULL
   AND EXISTS (
        SELECT 1
        FROM sys.columns c
        WHERE c.object_id = OBJECT_ID(N'dbo.IvLot')
          AND c.name = N'SupplierCode'
          AND c.max_length > 0
          AND c.max_length < 120) -- nvarchar(n) stores length in bytes (2 * chars)
BEGIN
    ALTER TABLE dbo.IvLot
        ALTER COLUMN SupplierCode nvarchar(60) NULL;
END
GO
