/*
  PLN_MAT_CONSUME_VAR inquiry menu + ACCESS permission. Idempotent.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.Menu', N'U') IS NULL
BEGIN
    PRINT N'Menu table is missing.';
    RETURN;
END;

DECLARE @inquiryId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PLN_INQUIRY');
IF @inquiryId IS NULL
BEGIN
    PRINT N'PLN_INQUIRY is missing — run init-planning-daily-production-menu.sql first.';
    RETURN;
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PLN_MAT_CONSUME_VAR')
BEGIN
    INSERT INTO dbo.Menu
        (MenuCode, MenuName, ParentMenuId, Route, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
    VALUES
        (N'PLN_MAT_CONSUME_VAR', N'Material Consume Variance', @inquiryId, N'/planning/material-consume-variance', 7, 0, 1,
         SYSUTCDATETIME(), N'SEED');
END
ELSE
BEGIN
    UPDATE dbo.Menu
    SET ParentMenuId = @inquiryId,
        MenuName = N'Material Consume Variance',
        Route = N'/planning/material-consume-variance',
        SortOrder = 7,
        IsActive = 1,
        ModifiedDate = SYSUTCDATETIME(),
        ModifiedBy = N'SEED'
    WHERE MenuCode = N'PLN_MAT_CONSUME_VAR';
END;

INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
FROM dbo.Menu m
INNER JOIN dbo.Permission p ON p.PermissionCode = N'ACCESS'
WHERE m.MenuCode = N'PLN_MAT_CONSUME_VAR'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.MenuPermission mp
      WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);

PRINT N'PLN_MAT_CONSUME_VAR menu/ACCESS ensured.';
GO
