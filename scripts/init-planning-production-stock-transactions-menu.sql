-- Idempotent menu/permission seed for Production Stock Return and Adjustment.
-- No role grants and no posting feature switches are changed by this script.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @parent int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PLN_TRANSACTIONS');
IF @parent IS NULL THROW 51000, 'Run the planning menu seed first.', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_STOCK_RETURN')
    INSERT dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'PLN_STOCK_RETURN', N'Production Stock Return', @parent, N'/planning/stock-returns', 5, 0, 1, SYSUTCDATETIME(), N'SEED');
ELSE
    UPDATE dbo.Menu SET MenuName = N'Production Stock Return', ParentMenuId = @parent, Route = N'/planning/stock-returns',
        SortOrder = 5, IsActive = 1, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'SEED'
    WHERE MenuCode = N'PLN_STOCK_RETURN';

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_STOCK_ADJUSTMENT')
    INSERT dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'PLN_STOCK_ADJUSTMENT', N'Production Stock Adjustment', @parent, N'/planning/stock-adjustments', 6, 0, 1, SYSUTCDATETIME(), N'SEED');
ELSE
    UPDATE dbo.Menu SET MenuName = N'Production Stock Adjustment', ParentMenuId = @parent, Route = N'/planning/stock-adjustments',
        SortOrder = 6, IsActive = 1, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'SEED'
    WHERE MenuCode = N'PLN_STOCK_ADJUSTMENT';

INSERT dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
JOIN dbo.Permission p ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'POST', N'ROLLBACK', N'VIEW_COST')
WHERE m.MenuCode IN (N'PLN_STOCK_RETURN', N'PLN_STOCK_ADJUSTMENT')
  AND NOT EXISTS (SELECT 1 FROM dbo.MenuPermission mp WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);

INSERT dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1 FROM dbo.Menu m CROSS JOIN dbo.Permission p
WHERE m.MenuCode = N'PLN_STOCK_ADJUSTMENT' AND p.PermissionCode = N'PRICE_OVERRIDE'
  AND NOT EXISTS (SELECT 1 FROM dbo.MenuPermission mp WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);

UPDATE dbo.Menu SET SortOrder = 7, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'SEED'
WHERE MenuCode = N'PLN_MAC_MAINT';

COMMIT;
GO
