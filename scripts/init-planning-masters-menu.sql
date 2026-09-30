-- Menu + permission seed for Planning masters (Rev 4.1).
-- Idempotent. MenuSyncService reconciles dbo.Menu from menus.xml on startup — ship together.
-- Run after menus.xml deploy. Role grants left to deployment owner.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @masterId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PLN_MASTER');

IF @masterId IS NULL
BEGIN
    PRINT N'PLN_MASTER missing — start the app once so MenuSyncService creates menus from menus.xml, then re-run.';
END
ELSE
BEGIN
    ;WITH Menus(Code, Name, Route, SortOrder) AS (
        SELECT * FROM (VALUES
            (N'PLN_WC_HIERARCHY', N'WC / Process / Machine', N'/planning/hierarchy', 2),
            (N'PLN_WORK_CENTRE', N'Work Centres', N'/planning/work-centres', 3),
            (N'PLN_WORK_PROCESS', N'Processes', N'/planning/processes', 4),
            (N'PLN_WORK_MACHINE', N'Machines', N'/planning/machines', 5),
            (N'PLN_SHIFT', N'Shifts', N'/planning/shifts', 6),
            (N'PLN_SHIFT_GROUP', N'Shift Groups', N'/planning/shift-groups', 7),
            (N'PLN_COMPANY_CAL', N'Company Calendar', N'/planning/company-calendar', 8),
            (N'PLN_MAC_SHIFT_CAL', N'Machine Shift Calendar', N'/planning/machine-calendars', 9),
            (N'PLN_OPERATOR', N'Operators', N'/planning/operators', 10),
            (N'PLN_WORK_PREFIX', N'Work Prefixes', N'/planning/work-prefixes', 11),
            (N'PLN_MAC_SEQ', N'Machine Sequence', N'/planning/machine-sequences', 12),
            (N'PLN_MAC_PREVENTIVE', N'Machine Preventive', N'/planning/machine-preventive', 13),
            (N'PLN_IMPORT_PRDDEF', N'Import Product Definition', N'/planning/import-product-def', 14)
        ) v(Code, Name, Route, SortOrder)
    )
    INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    SELECT m.Code, m.Name, @masterId, m.Route, m.SortOrder, 0, 1, SYSUTCDATETIME(), N'SEED'
    FROM Menus m
    WHERE NOT EXISTS (SELECT 1 FROM dbo.Menu x WHERE x.MenuCode = m.Code);

    INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
    SELECT menu.MenuId, p.PermissionId, p.SortOrder, 1
    FROM dbo.Menu menu
    INNER JOIN dbo.Permission p ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE')
    WHERE menu.MenuCode IN (
        N'PLN_WC_HIERARCHY', N'PLN_WORK_CENTRE', N'PLN_WORK_PROCESS', N'PLN_WORK_MACHINE',
        N'PLN_SHIFT', N'PLN_SHIFT_GROUP', N'PLN_COMPANY_CAL', N'PLN_MAC_SHIFT_CAL',
        N'PLN_OPERATOR', N'PLN_WORK_PREFIX', N'PLN_MAC_SEQ', N'PLN_MAC_PREVENTIVE', N'PLN_IMPORT_PRDDEF')
      AND NOT EXISTS (
          SELECT 1 FROM dbo.MenuPermission mp
          WHERE mp.MenuId = menu.MenuId AND mp.PermissionId = p.PermissionId);

    PRINT N'Planning master MenuPermission rows ensured.';
END
GO

-- Optional: grant all Planning master permissions to a role (edit @RoleCode before run).
-- DECLARE @RoleCode nvarchar(50) = N'ADMIN';
-- INSERT INTO dbo.RoleMenuPermission (RoleId, MenuPermissionId, IsGranted)
-- SELECT r.RoleId, mp.MenuPermissionId, 1
-- FROM dbo.Role r
-- CROSS JOIN dbo.MenuPermission mp
-- INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
-- WHERE r.RoleCode = @RoleCode
--   AND m.MenuCode LIKE N'PLN_%'
--   AND NOT EXISTS (
--       SELECT 1 FROM dbo.RoleMenuPermission x
--       WHERE x.RoleId = r.RoleId AND x.MenuPermissionId = mp.MenuPermissionId);
-- GO
