SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @parent int=(SELECT MenuId FROM dbo.Menu WHERE MenuCode=N'PLN_TRANSACTIONS');
IF @parent IS NULL THROW 51000,'Run the planning menu seed first.',1;
IF NOT EXISTS(SELECT 1 FROM dbo.Menu WHERE MenuCode=N'PLN_FINISHED_GOOD_RECEIPT')
 INSERT dbo.Menu(MenuCode,MenuName,ParentMenuId,Route,SortOrder,AlwaysVisible,IsActive,CreatedDate,CreatedBy)
 VALUES(N'PLN_FINISHED_GOOD_RECEIPT',N'Finished Good Receipt',@parent,N'/planning/finished-good-receipts',4,0,1,SYSUTCDATETIME(),N'SEED');
DECLARE @menu int=(SELECT MenuId FROM dbo.Menu WHERE MenuCode=N'PLN_FINISHED_GOOD_RECEIPT');
INSERT dbo.MenuPermission(MenuId,PermissionId,SortOrder,IsActive)
SELECT @menu,p.PermissionId,p.SortOrder,1 FROM dbo.Permission p WHERE p.PermissionCode IN(N'ACCESS',N'ADD',N'EDIT',N'DELETE',N'POST',N'ROLLBACK',N'VIEW_COST')
AND NOT EXISTS(SELECT 1 FROM dbo.MenuPermission mp WHERE mp.MenuId=@menu AND mp.PermissionId=p.PermissionId);
COMMIT;
-- Role grants remain explicit deployment choices. This seed never enables FG posting.
