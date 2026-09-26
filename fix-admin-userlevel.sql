-- Check and fix admin user's userlevel
-- Run this script on your production database

-- First, check what the current userlevel is for the admin user
SELECT 
    uid,
    id,
    CompanyCode,
    userlevel,
    CASE 
        WHEN userlevel = 'SYSTEM_ADMIN' THEN '✓ Correct'
        WHEN userlevel = 'ADMIN' THEN '✓ Correct (Admin)'
        WHEN userlevel IS NULL THEN '✗ NULL - Needs fix'
        ELSE '✗ Wrong value - Needs fix'
    END AS Status
FROM dbo.userlogin
WHERE id = 'admin';

-- If the admin user doesn't have the correct userlevel, update it
-- Uncomment and run the UPDATE statement below if needed:

/*
UPDATE dbo.userlogin
SET userlevel = N'SYSTEM_ADMIN',
    Updated = GETDATE(),
    UpdatedUID = N'SEED'
WHERE id = N'admin'
  AND (userlevel IS NULL OR userlevel NOT IN (N'SYSTEM_ADMIN', N'ADMIN'));
*/

-- Verify the fix
SELECT 
    uid,
    id,
    CompanyCode,
    userlevel
FROM dbo.userlogin
WHERE id = 'admin';