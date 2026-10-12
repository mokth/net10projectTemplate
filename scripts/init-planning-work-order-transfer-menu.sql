-- Idempotent menu and permission seed for Production Work Order Transfer.
-- No role grants or posting switches are changed here.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @parent int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PLN_TRANSACTIONS');
IF @parent IS NULL THROW 51000, 'Run the planning menu seed first.', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_WO_TRANSFER')
    INSERT dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES (N'PLN_WO_TRANSFER', N'Work Order Transfer', @parent, N'/planning/work-order-transfer', 7, 0, 1, SYSUTCDATETIME(), N'SEED');
ELSE
    UPDATE dbo.Menu SET MenuName=N'Work Order Transfer', ParentMenuId=@parent,
        Route=N'/planning/work-order-transfer', SortOrder=7, IsActive=1,
        ModifiedDate=SYSUTCDATETIME(), ModifiedBy=N'SEED'
    WHERE MenuCode=N'PLN_WO_TRANSFER';

INSERT dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
JOIN dbo.Permission p ON p.PermissionCode IN (N'ACCESS',N'ADD',N'EDIT',N'DELETE',N'POST',N'VIEW_COST')
WHERE m.MenuCode=N'PLN_WO_TRANSFER'
  AND NOT EXISTS (SELECT 1 FROM dbo.MenuPermission mp WHERE mp.MenuId=m.MenuId AND mp.PermissionId=p.PermissionId);

UPDATE dbo.Menu SET SortOrder=8, ModifiedDate=SYSUTCDATETIME(), ModifiedBy=N'SEED'
WHERE MenuCode=N'PLN_MAC_MAINT';

COMMIT;
GO
