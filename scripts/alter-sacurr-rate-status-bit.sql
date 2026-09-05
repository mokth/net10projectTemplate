-- Convert SaCurrRate.Status from nvarchar to bit (manual deploy).
-- Target: same database as ConnectionStrings:DefaultConnection

SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo.SaCurrRate', N'U') IS NULL
BEGIN
    RAISERROR(N'SaCurrRate table does not exist.', 16, 1);
    RETURN;
END
GO

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.SaCurrRate')
      AND c.name = N'Status'
      AND t.name IN (N'nvarchar', N'varchar', N'nchar', N'char'))
BEGIN
    IF COL_LENGTH(N'dbo.SaCurrRate', N'StatusBit') IS NULL
        ALTER TABLE dbo.SaCurrRate ADD StatusBit bit NULL;
END
GO

IF COL_LENGTH(N'dbo.SaCurrRate', N'StatusBit') IS NOT NULL
   AND COL_LENGTH(N'dbo.SaCurrRate', N'Status') IS NOT NULL
BEGIN
    UPDATE dbo.SaCurrRate
    SET StatusBit = CASE
        WHEN Status IS NULL THEN 1
        WHEN UPPER(LTRIM(RTRIM(Status))) IN (N'A', N'Y', N'1', N'TRUE', N'ACTIVE', N'YES') THEN 1
        ELSE 0
    END;

    ALTER TABLE dbo.SaCurrRate DROP COLUMN Status;
END
GO

IF COL_LENGTH(N'dbo.SaCurrRate', N'StatusBit') IS NOT NULL
   AND COL_LENGTH(N'dbo.SaCurrRate', N'Status') IS NULL
BEGIN
    EXEC sp_rename N'dbo.SaCurrRate.StatusBit', N'Status', N'COLUMN';
END
GO

IF COL_LENGTH(N'dbo.SaCurrRate', N'Status') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.default_constraints
       WHERE parent_object_id = OBJECT_ID(N'dbo.SaCurrRate')
         AND COL_NAME(parent_object_id, parent_column_id) = N'Status')
BEGIN
    ALTER TABLE dbo.SaCurrRate ADD CONSTRAINT DF_SaCurrRate_Status DEFAULT (1) FOR Status;
END
GO

IF COL_LENGTH(N'dbo.SaCurrRate', N'Status') IS NOT NULL
BEGIN
    UPDATE dbo.SaCurrRate SET Status = 1 WHERE Status IS NULL;
    ALTER TABLE dbo.SaCurrRate ALTER COLUMN Status bit NOT NULL;
END
GO
