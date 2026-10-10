-- Idempotently configure continuous DT (Delivery Trip) numbering for existing company/branch scopes.
-- Seq is the next number to issue. This script never resets an existing sequence.

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.AdSmNum', N'U') IS NULL
    THROW 51001, 'AdSmNum must exist before Delivery Trip numbering is configured.', 1;
GO

INSERT INTO dbo.AdSmNum
    (CompanyCode, BranchCode, LocationCode, NumCd, NumDes, TotLength, Prefix, Seq, Created, UserID)
SELECT scopes.CompanyCode,
       scopes.BranchCode,
       NULL,
       N'DT',
       N'Delivery Trip',
       8,
       N'DT',
       1,
       GETDATE(),
       N'SYSTEM'
FROM
(
    SELECT DISTINCT CompanyCode, BranchCode
    FROM dbo.SaDO
    UNION
    SELECT DISTINCT CompanyCode, BranchCode
    FROM dbo.AdSmNum
) AS scopes
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.AdSmNum existing
    WHERE existing.CompanyCode = scopes.CompanyCode
      AND existing.BranchCode = scopes.BranchCode
      AND existing.NumCd = N'DT'
);
GO

PRINT N'DT numbering is configured where a continuous numbering scope already exists.';
GO
