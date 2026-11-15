-- =============================================================================
-- Menu + permission + numbering seed for Purchase Sourcing (RFQ) (PO_SOURCING).
-- Manual DBA script - Idempotent: safe to re-run.
--
-- MENU DEPLOYMENT COUPLING:
--   MenuSyncService syncs dbo.Menu rows from ErpWeb/Menus/menus.xml at startup
--   and preserves MenuId. PO_SOURCING is defined in ErpWeb/Menus/menus.xml
--   under PO_TRANSACTIONS (Route = '/purchase/sourcing', SortOrder = 10).
--
-- Run order:
--   1. create-po-sourcing.sql
--   2. init-po-sourcing-menu.sql
-- =============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── 1. Ensure standard permissions exist in dbo.Permission ────────────────────
IF OBJECT_ID(N'dbo.Permission', N'U') IS NOT NULL
BEGIN
    MERGE dbo.Permission AS t
    USING (VALUES
        (N'ACCESS',    N'Access',    N'Navigation', N'Menu/page access', 1),
        (N'ADD',       N'Add',       N'Action',     NULL,                2),
        (N'EDIT',      N'Edit',      N'Action',     NULL,                3),
        (N'DELETE',    N'Delete',    N'Action',     NULL,                4),
        (N'PRINT',     N'Print',     N'Action',     NULL,                5),
        (N'APPROVE',   N'Approve',   N'Action',     NULL,                8),
        (N'CANCEL',    N'Cancel',    N'Action',     NULL,               10),
        (N'EXPORT',    N'Export',    N'Action',     NULL,               13),
        (N'VIEW_COST', N'View Cost', N'Data',       NULL,               19)
    ) AS s(PermissionCode, PermissionName, PermissionType, Description, SortOrder)
    ON t.PermissionCode = s.PermissionCode
    WHEN NOT MATCHED THEN
        INSERT (PermissionCode, PermissionName, PermissionType, Description, SortOrder, IsActive, CreatedDate, CreatedBy)
        VALUES (s.PermissionCode, s.PermissionName, s.PermissionType, s.Description, s.SortOrder, 1, SYSUTCDATETIME(), N'SEED');
END
GO

-- ── 2. Menu row under PO_TRANSACTIONS ─────────────────────────────────────────
DECLARE @poTransactionsId int = (SELECT MenuId FROM dbo.Menu WHERE MenuCode = N'PO_TRANSACTIONS');

IF OBJECT_ID(N'dbo.Menu', N'U') IS NOT NULL AND @poTransactionsId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.Menu WHERE MenuCode = N'PO_SOURCING')
    BEGIN
        INSERT INTO dbo.Menu (MenuCode, MenuName, ParentMenuId, Route, Icon, SortOrder, AlwaysVisible, IsActive, CreatedDate, CreatedBy)
        VALUES (N'PO_SOURCING', N'Purchase Sourcing (RFQ)', @poTransactionsId, N'/purchase/sourcing', N'fa-solid fa-scale-balanced', 10, 0, 1, SYSUTCDATETIME(), N'SEED');
        PRINT N'Inserted dbo.Menu row for PO_SOURCING.';
    END
    ELSE
    BEGIN
        UPDATE dbo.Menu
        SET MenuName = N'Purchase Sourcing (RFQ)',
            ParentMenuId = @poTransactionsId,
            Route = N'/purchase/sourcing',
            Icon = N'fa-solid fa-scale-balanced',
            SortOrder = 10,
            IsActive = 1,
            ModifiedDate = SYSUTCDATETIME(),
            ModifiedBy = N'SEED'
        WHERE MenuCode = N'PO_SOURCING';
        PRINT N'Updated dbo.Menu row for PO_SOURCING.';
    END
END
ELSE
BEGIN
    PRINT N'dbo.Menu or PO_TRANSACTIONS missing - run init-menu-access.sql first, then re-run this script.';
END
GO

-- ── 3. MenuPermission rows for PO_SOURCING ────────────────────────────────────
-- Permissions used by PoSourcingService and PoSourcingWorkbench:
--   ACCESS, ADD, EDIT, DELETE, APPROVE, CANCEL, PRINT, EXPORT, VIEW_COST
IF OBJECT_ID(N'dbo.MenuPermission', N'U') IS NOT NULL
BEGIN
    INSERT INTO dbo.MenuPermission (MenuId, PermissionId, SortOrder, IsActive)
    SELECT m.MenuId, p.PermissionId, p.SortOrder, 1
    FROM dbo.Menu m
    INNER JOIN dbo.Permission p
        ON p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'APPROVE', N'CANCEL', N'PRINT', N'EXPORT', N'VIEW_COST')
    WHERE m.MenuCode = N'PO_SOURCING'
      AND NOT EXISTS (
          SELECT 1 FROM dbo.MenuPermission mp
          WHERE mp.MenuId = m.MenuId AND mp.PermissionId = p.PermissionId);

    UPDATE mp
    SET mp.IsActive = 1
    FROM dbo.MenuPermission mp
    INNER JOIN dbo.Menu m ON m.MenuId = mp.MenuId
    INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
    WHERE m.MenuCode = N'PO_SOURCING'
      AND p.PermissionCode IN (N'ACCESS', N'ADD', N'EDIT', N'DELETE', N'APPROVE', N'CANCEL', N'PRINT', N'EXPORT', N'VIEW_COST')
      AND mp.IsActive <> 1;
END
GO

-- ── 4. RoleMenuPermission grants for ADMIN / SYSTEM_ADMIN / USER roles ────────
IF OBJECT_ID(N'dbo.RoleMenuPermission', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.Role', N'U') IS NOT NULL
BEGIN
    INSERT INTO dbo.RoleMenuPermission (RoleId, MenuId, PermissionId, IsAllowed, CreatedDate, CreatedBy)
    SELECT r.RoleId, m.MenuId, p.PermissionId, 1, SYSUTCDATETIME(), N'SEED'
    FROM dbo.Role r
    INNER JOIN dbo.Menu m ON m.MenuCode = N'PO_SOURCING'
    INNER JOIN dbo.MenuPermission mp ON mp.MenuId = m.MenuId AND mp.IsActive = 1
    INNER JOIN dbo.Permission p ON p.PermissionId = mp.PermissionId
    WHERE r.RoleCode IN (N'ADMIN', N'SYSTEM_ADMIN')
      AND r.IsActive = 1
      AND NOT EXISTS (
          SELECT 1 FROM dbo.RoleMenuPermission x
          WHERE x.RoleId = r.RoleId AND x.MenuId = m.MenuId AND x.PermissionId = p.PermissionId);

    UPDATE x
    SET x.IsAllowed = 1
    FROM dbo.RoleMenuPermission x
    INNER JOIN dbo.Role r ON r.RoleId = x.RoleId
    INNER JOIN dbo.Menu m ON m.MenuId = x.MenuId
    WHERE m.MenuCode = N'PO_SOURCING'
      AND r.RoleCode IN (N'ADMIN', N'SYSTEM_ADMIN')
      AND r.IsActive = 1
      AND x.IsAllowed <> 1;
END
GO

-- ── 5. Enable PROCUREMENT.SOURCING_ENABLED for existing companies in AdSmParam ─
IF OBJECT_ID(N'dbo.AdSmParam', N'U') IS NOT NULL
BEGIN
    ;WITH Companies AS (
        SELECT DISTINCT CompanyCode FROM dbo.Role WHERE CompanyCode IS NOT NULL
    )
    INSERT INTO dbo.AdSmParam
        (ModuleCode, ParamKey, ScopeCode, CompanyCode, BranchCode, ValueFlag, IsActive, Remark, CreatedDate, CreatedBy)
    SELECT
        N'PROCUREMENT',
        N'SOURCING_ENABLED',
        N'COMPANY',
        c.CompanyCode,
        NULL,
        1,
        1,
        N'Enable Purchase Sourcing (RFQ) & Supplier Quotation Comparison',
        GETDATE(),
        N'SEED'
    FROM Companies c
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.AdSmParam p
        WHERE p.ModuleCode = N'PROCUREMENT'
          AND p.ParamKey = N'SOURCING_ENABLED'
          AND p.ScopeCode = N'COMPANY'
          AND p.CompanyCode = c.CompanyCode);
END
GO

-- ── 6. Seed RFQ Document Numbering (AdSmNum / AdSmNumDate) for existing branches
IF OBJECT_ID(N'dbo.AdSmNum', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.AdBranch', N'U') IS NOT NULL
BEGIN
    INSERT INTO dbo.AdSmNum (CompanyCode, BranchCode, NumCd, Prefix, Seq, TotLength)
    SELECT b.CompanyCode, b.BranchCode, N'RFQ', N'RFQ', 1, 4
    FROM dbo.AdBranch b
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.AdSmNum n
        WHERE n.CompanyCode = b.CompanyCode AND n.BranchCode = b.BranchCode AND n.NumCd = N'RFQ');
END
GO

IF OBJECT_ID(N'dbo.AdSmNumDate', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.AdBranch', N'U') IS NOT NULL
BEGIN
    INSERT INTO dbo.AdSmNumDate (CompanyCode, BranchCode, NumCd, Prefix, Seq, TotLength, [Year], [Month])
    SELECT b.CompanyCode, b.BranchCode, N'RFQ', N'RFQ', 1, 4, 0, 0
    FROM dbo.AdBranch b
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.AdSmNumDate nd
        WHERE nd.CompanyCode = b.CompanyCode
          AND nd.BranchCode = b.BranchCode
          AND nd.NumCd = N'RFQ'
          AND ISNULL(nd.[Year], 0) = 0
          AND ISNULL(nd.[Month], 0) = 0);
END
GO

PRINT N'init-po-sourcing-menu.sql complete.';
GO
