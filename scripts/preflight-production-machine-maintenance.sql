-- READ-ONLY preflight for machine / preventive / maintenance enhancement.
-- Does not modify data. Repair blocking rows before running enhance-production-machine-maintenance.sql.

SET NOCOUNT ON;

PRINT N'=== Production machine maintenance preflight ===';

IF OBJECT_ID(N'dbo.PrMachine', N'U') IS NULL
BEGIN
    PRINT N'BLOCKING: dbo.PrMachine does not exist.';
    RETURN;
END

PRINT N'-- Duplicate Machine_Cd inside company (blocks Active / one-process invariant)';
SELECT CompCode, Machine_Cd, COUNT(*) AS DupCount
FROM dbo.PrMachine
GROUP BY CompCode, Machine_Cd
HAVING COUNT(*) > 1;

IF OBJECT_ID(N'dbo.PrPreventive', N'U') IS NOT NULL
BEGIN
    PRINT N'-- Preventive machine not found in PrMachine (any company)';
    SELECT p.UID, p.Machine_Cd, p.Down_Dt
    FROM dbo.PrPreventive p
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.PrMachine m WHERE m.Machine_Cd = p.Machine_Cd
    );

    PRINT N'-- Ambiguous CompCode backfill (same Machine_Cd under multiple companies)';
    SELECT p.UID, p.Machine_Cd, COUNT(DISTINCT m.CompCode) AS CompanyCount
    FROM dbo.PrPreventive p
    INNER JOIN dbo.PrMachine m ON m.Machine_Cd = p.Machine_Cd
    GROUP BY p.UID, p.Machine_Cd
    HAVING COUNT(DISTINCT m.CompCode) > 1;

    PRINT N'-- Invalid year-0001 / invalid Start_Tm End_Tm';
    SELECT UID, Machine_Cd, Down_Dt, Start_Tm, End_Tm
    FROM dbo.PrPreventive
    WHERE Start_Tm < '1900-01-01'
       OR End_Tm < '1900-01-01'
       OR YEAR(Start_Tm) = 1
       OR YEAR(End_Tm) = 1;
END
ELSE
    PRINT N'dbo.PrPreventive missing — enhance script will require it.';

IF OBJECT_ID(N'dbo.PrMacMaintenance', N'U') IS NOT NULL
BEGIN
    PRINT N'-- Maintenance machine not found in PrMachine';
    SELECT m.ID, m.MacCode, m.TrxDate
    FROM dbo.PrMacMaintenance m
    WHERE m.MacCode IS NOT NULL
      AND LTRIM(RTRIM(m.MacCode)) <> N''
      AND NOT EXISTS (
          SELECT 1 FROM dbo.PrMachine pm WHERE pm.Machine_Cd = m.MacCode
      );

    PRINT N'-- Ambiguous CompCode backfill for maintenance';
    SELECT m.ID, m.MacCode, COUNT(DISTINCT pm.CompCode) AS CompanyCount
    FROM dbo.PrMacMaintenance m
    INNER JOIN dbo.PrMachine pm ON pm.Machine_Cd = m.MacCode
    WHERE m.MacCode IS NOT NULL AND LTRIM(RTRIM(m.MacCode)) <> N''
    GROUP BY m.ID, m.MacCode
    HAVING COUNT(DISTINCT pm.CompCode) > 1;

    PRINT N'-- Existing MType sample (review non-standard values)';
    SELECT MType, COUNT(*) AS Cnt
    FROM dbo.PrMacMaintenance
    GROUP BY MType
    ORDER BY Cnt DESC;

    PRINT N'-- Existing Status sample';
    SELECT Status, COUNT(*) AS Cnt
    FROM dbo.PrMacMaintenance
    GROUP BY Status
    ORDER BY Cnt DESC;
END
ELSE
    PRINT N'dbo.PrMacMaintenance missing — enhance script will require it.';

PRINT N'=== Preflight complete (read-only) ===';
