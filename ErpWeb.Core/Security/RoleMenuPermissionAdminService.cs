using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Security;

public sealed class RoleMenuPermissionAdminService : IRoleMenuPermissionAdminService
{
    public const string AdminRole = "ADMIN";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;
    private readonly ILogger<RoleMenuPermissionAdminService> _logger;

    public RoleMenuPermissionAdminService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        ILogger<RoleMenuPermissionAdminService> logger)
    {
        _dbFactory = dbFactory;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _logger = logger;
    }

    public async Task<RoleMenuPermissionAdminOperationResult> GetRowsAsync(
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return RoleMenuPermissionAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(
                MenuCodes.AdminRolePermissions, PermissionCodes.Access, cancellationToken))
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await (
            from rmp in db.RoleMenuPermissions.AsNoTracking()
            join role in db.Roles.AsNoTracking() on rmp.RoleId equals role.RoleId
            join menu in db.Menus.AsNoTracking() on rmp.MenuId equals menu.MenuId
            join perm in db.Permissions.AsNoTracking() on rmp.PermissionId equals perm.PermissionId
            where role.CompanyCode == context.CompanyCode
            orderby role.RoleCode, menu.MenuCode, perm.SortOrder, perm.PermissionCode
            select new RoleMenuPermissionRow
            {
                RoleMenuPermissionId = rmp.RoleMenuPermissionId,
                RoleId = rmp.RoleId,
                MenuId = rmp.MenuId,
                PermissionId = rmp.PermissionId,
                IsAllowed = rmp.IsAllowed,
                RoleCode = role.RoleCode,
                RoleName = role.RoleName,
                MenuCode = menu.MenuCode,
                MenuName = menu.MenuName,
                PermissionCode = perm.PermissionCode,
                PermissionName = perm.PermissionName,
                CreatedDate = rmp.CreatedDate,
                CreatedBy = rmp.CreatedBy,
                ModifiedDate = rmp.ModifiedDate,
                ModifiedBy = rmp.ModifiedBy
            }).ToListAsync(cancellationToken);

        return RoleMenuPermissionAdminOperationResult.Ok(rows);
    }

    public async Task<RoleMenuPermissionAdminOperationResult> GetLookupsAsync(
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return RoleMenuPermissionAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(
                MenuCodes.AdminRolePermissions, PermissionCodes.Access, cancellationToken))
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var roles = await db.Roles
            .AsNoTracking()
            .Where(r => r.CompanyCode == context.CompanyCode && r.IsActive)
            .OrderBy(r => r.RoleCode)
            .Select(r => new RoleOptionAdminRow
            {
                RoleId = r.RoleId,
                RoleCode = r.RoleCode,
                RoleName = r.RoleName
            })
            .ToListAsync(cancellationToken);

        var menus = await db.Menus
            .AsNoTracking()
            .Where(m => m.IsActive)
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.MenuCode)
            .Select(m => new MenuOptionRow
            {
                MenuId = m.MenuId,
                MenuCode = m.MenuCode,
                MenuName = m.MenuName
            })
            .ToListAsync(cancellationToken);

        var permissions = await db.Permissions
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.PermissionCode)
            .Select(p => new PermissionOptionRow
            {
                PermissionId = p.PermissionId,
                PermissionCode = p.PermissionCode,
                PermissionName = p.PermissionName
            })
            .ToListAsync(cancellationToken);

        // Top-level menus act as modules (Role + Module matrix filter).
        var moduleRows = await db.Menus
            .AsNoTracking()
            .Where(m => m.IsActive && m.ParentMenuId == null)
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.MenuCode)
            .Select(m => new MenuOptionRow
            {
                MenuId = m.MenuId,
                MenuCode = m.MenuCode,
                MenuName = m.MenuName
            })
            .ToListAsync(cancellationToken);

        return RoleMenuPermissionAdminOperationResult.OkLookups(roles, menus, permissions, moduleRows);
    }

    public async Task<RoleMenuPermissionAdminOperationResult> GetMatrixAsync(
        int moduleMenuId,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return RoleMenuPermissionAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(
                MenuCodes.AdminRolePermissions, PermissionCodes.Access, cancellationToken))
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Not authorized.");
        }

        if (moduleMenuId <= 0)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Module is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var allMenus = await db.Menus
            .AsNoTracking()
            .Where(m => m.IsActive)
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.MenuCode)
            .Select(m => new { m.MenuId, m.MenuCode, m.MenuName, m.ParentMenuId, m.SortOrder })
            .ToListAsync(cancellationToken);

        var module = allMenus.FirstOrDefault(m => m.MenuId == moduleMenuId);
        if (module is null)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Module not found.");
        }

        var childrenByParent = allMenus
            .Where(m => m.ParentMenuId.HasValue)
            .GroupBy(m => m.ParentMenuId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.SortOrder).ThenBy(x => x.MenuCode).ToList());

        var matrixRows = new List<RolePermissionMatrixRow>();
        void Walk(int menuId, int depth)
        {
            var node = allMenus.First(m => m.MenuId == menuId);
            matrixRows.Add(new RolePermissionMatrixRow
            {
                MenuId = node.MenuId,
                MenuCode = node.MenuCode,
                MenuName = node.MenuName,
                Depth = depth
            });

            if (!childrenByParent.TryGetValue(menuId, out var children))
            {
                return;
            }

            foreach (var child in children)
            {
                Walk(child.MenuId, depth + 1);
            }
        }

        Walk(moduleMenuId, 0);

        var menuIds = matrixRows.Select(r => r.MenuId).ToHashSet();

        var menuPermissionIds = await db.MenuPermissions
            .AsNoTracking()
            .Where(mp => menuIds.Contains(mp.MenuId) && mp.IsActive)
            .Select(mp => mp.PermissionId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var coreCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PermissionCodes.Access,
            PermissionCodes.Add,
            PermissionCodes.Edit,
            PermissionCodes.Delete,
            PermissionCodes.Print,
            PermissionCodes.Post,
            PermissionCodes.Rollback
        };

        var columns = await db.Permissions
            .AsNoTracking()
            .Where(p => p.IsActive &&
                        (coreCodes.Contains(p.PermissionCode) || menuPermissionIds.Contains(p.PermissionId)))
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.PermissionCode)
            .Select(p => new RolePermissionMatrixColumn
            {
                PermissionId = p.PermissionId,
                PermissionCode = p.PermissionCode,
                PermissionName = p.PermissionName
            })
            .ToListAsync(cancellationToken);

        return RoleMenuPermissionAdminOperationResult.OkMatrix(columns, matrixRows);
    }

    public async Task<RoleMenuPermissionAdminOperationResult> GetRoleGrantsAsync(
        int roleId,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return RoleMenuPermissionAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(
                MenuCodes.AdminRolePermissions, PermissionCodes.Access, cancellationToken))
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Not authorized.");
        }

        if (roleId <= 0)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Role is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var roleOk = await db.Roles.AnyAsync(
            r => r.RoleId == roleId && r.CompanyCode == context.CompanyCode && r.IsActive,
            cancellationToken);
        if (!roleOk)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Role not found for this company.");
        }

        var grants = await db.RoleMenuPermissions
            .AsNoTracking()
            .Where(rmp => rmp.RoleId == roleId)
            .Select(rmp => new RolePermissionGrant
            {
                MenuId = rmp.MenuId,
                PermissionId = rmp.PermissionId,
                IsAllowed = rmp.IsAllowed
            })
            .ToListAsync(cancellationToken);

        return RoleMenuPermissionAdminOperationResult.OkGrants(grants);
    }

    public async Task<RoleMenuPermissionAdminOperationResult> SaveMatrixAsync(
        int roleId,
        IReadOnlyList<RolePermissionMatrixChange> changes,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return RoleMenuPermissionAdminOperationResult.Fail(context.Error);
        }

        var canEdit = await _accessRights.CanAsync(
            MenuCodes.AdminRolePermissions, PermissionCodes.Edit, cancellationToken);
        var canAdd = await _accessRights.CanAsync(
            MenuCodes.AdminRolePermissions, PermissionCodes.Add, cancellationToken);
        if (!canEdit && !canAdd)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Not authorized.");
        }

        if (roleId <= 0)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Role is required.");
        }

        if (changes.Count == 0)
        {
            return RoleMenuPermissionAdminOperationResult.Ok();
        }

        // Last change wins for duplicate menu/permission keys.
        changes = changes
            .GroupBy(c => (c.MenuId, c.PermissionId))
            .Select(g => g.Last())
            .ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var roleOk = await db.Roles.AnyAsync(
            r => r.RoleId == roleId && r.CompanyCode == context.CompanyCode && r.IsActive,
            cancellationToken);
        if (!roleOk)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Role not found for this company.");
        }

        var menuIds = changes.Select(c => c.MenuId).Distinct().ToList();
        var permissionIds = changes.Select(c => c.PermissionId).Distinct().ToList();

        var validMenuCount = await db.Menus.CountAsync(
            m => menuIds.Contains(m.MenuId) && m.IsActive, cancellationToken);
        if (validMenuCount != menuIds.Count)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("One or more menus are invalid.");
        }

        var validPermissionCount = await db.Permissions.CountAsync(
            p => permissionIds.Contains(p.PermissionId) && p.IsActive, cancellationToken);
        if (validPermissionCount != permissionIds.Count)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("One or more permissions are invalid.");
        }

        var existing = await db.RoleMenuPermissions
            .Where(rmp => rmp.RoleId == roleId
                          && menuIds.Contains(rmp.MenuId)
                          && permissionIds.Contains(rmp.PermissionId))
            .ToListAsync(cancellationToken);

        var existingMap = existing.ToDictionary(x => (x.MenuId, x.PermissionId));
        var now = DateTime.UtcNow;
        var userId = Truncate(context.UserId!, 10);
        var added = 0;
        var updated = 0;
        var removed = 0;

        foreach (var change in changes)
        {
            var key = (change.MenuId, change.PermissionId);
            existingMap.TryGetValue(key, out var entity);

            if (change.IsAllowed)
            {
                await EnsureMenuPermissionAsync(db, change.MenuId, change.PermissionId, cancellationToken);

                if (entity is null)
                {
                    db.RoleMenuPermissions.Add(new RoleMenuPermission
                    {
                        RoleId = roleId,
                        MenuId = change.MenuId,
                        PermissionId = change.PermissionId,
                        IsAllowed = true,
                        CreatedDate = now,
                        CreatedBy = userId
                    });
                    added++;
                }
                else if (!entity.IsAllowed)
                {
                    entity.IsAllowed = true;
                    entity.ModifiedDate = now;
                    entity.ModifiedBy = userId;
                    updated++;
                }
            }
            else if (entity is not null)
            {
                // Unchecked = remove grant (and any explicit deny).
                db.RoleMenuPermissions.Remove(entity);
                removed++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await _accessRights.RefreshPermissionsAsync(cancellationToken);

        _logger.LogInformation(
            "RoleMenuPermission matrix saved. AdminUserId={AdminUserId} RoleId={RoleId} Added={Added} Updated={Updated} Removed={Removed}",
            context.UserId,
            roleId,
            added,
            updated,
            removed);

        return RoleMenuPermissionAdminOperationResult.Ok();
    }

    public async Task<RoleMenuPermissionAdminOperationResult> AddAsync(
        RoleMenuPermissionRow row,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return RoleMenuPermissionAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(
                MenuCodes.AdminRolePermissions, PermissionCodes.Add, cancellationToken))
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Not authorized.");
        }

        var validation = ValidateRow(row);
        if (validation is not null)
        {
            return RoleMenuPermissionAdminOperationResult.Fail(validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var roleOk = await db.Roles.AnyAsync(
            r => r.RoleId == row.RoleId && r.CompanyCode == context.CompanyCode && r.IsActive,
            cancellationToken);
        if (!roleOk)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Role not found for this company.");
        }

        var menuOk = await db.Menus.AnyAsync(m => m.MenuId == row.MenuId && m.IsActive, cancellationToken);
        if (!menuOk)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Menu not found.");
        }

        var permissionOk = await db.Permissions.AnyAsync(
            p => p.PermissionId == row.PermissionId && p.IsActive,
            cancellationToken);
        if (!permissionOk)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Permission not found.");
        }

        var duplicate = await db.RoleMenuPermissions.AnyAsync(
            x => x.RoleId == row.RoleId && x.MenuId == row.MenuId && x.PermissionId == row.PermissionId,
            cancellationToken);
        if (duplicate)
        {
            return RoleMenuPermissionAdminOperationResult.Fail(
                "This role/menu/permission assignment already exists.");
        }

        await EnsureMenuPermissionAsync(db, row.MenuId, row.PermissionId, cancellationToken);

        db.RoleMenuPermissions.Add(new RoleMenuPermission
        {
            RoleId = row.RoleId,
            MenuId = row.MenuId,
            PermissionId = row.PermissionId,
            IsAllowed = row.IsAllowed,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = Truncate(context.UserId!, 10)
        });

        await db.SaveChangesAsync(cancellationToken);
        await _accessRights.RefreshPermissionsAsync(cancellationToken);

        _logger.LogInformation(
            "RoleMenuPermission created. AdminUserId={AdminUserId} RoleId={RoleId} MenuId={MenuId} PermissionId={PermissionId} IsAllowed={IsAllowed}",
            context.UserId,
            row.RoleId,
            row.MenuId,
            row.PermissionId,
            row.IsAllowed);

        return RoleMenuPermissionAdminOperationResult.Ok();
    }

    public async Task<RoleMenuPermissionAdminOperationResult> UpdateAsync(
        RoleMenuPermissionRow row,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return RoleMenuPermissionAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(
                MenuCodes.AdminRolePermissions, PermissionCodes.Edit, cancellationToken))
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Not authorized.");
        }

        if (row.RoleMenuPermissionId <= 0)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Invalid record.");
        }

        var validation = ValidateRow(row);
        if (validation is not null)
        {
            return RoleMenuPermissionAdminOperationResult.Fail(validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var entity = await (
            from rmp in db.RoleMenuPermissions
            join role in db.Roles on rmp.RoleId equals role.RoleId
            where rmp.RoleMenuPermissionId == row.RoleMenuPermissionId
                  && role.CompanyCode == context.CompanyCode
            select rmp).FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Record not found.");
        }

        var roleOk = await db.Roles.AnyAsync(
            r => r.RoleId == row.RoleId && r.CompanyCode == context.CompanyCode && r.IsActive,
            cancellationToken);
        if (!roleOk)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Role not found for this company.");
        }

        var menuOk = await db.Menus.AnyAsync(m => m.MenuId == row.MenuId && m.IsActive, cancellationToken);
        if (!menuOk)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Menu not found.");
        }

        var permissionOk = await db.Permissions.AnyAsync(
            p => p.PermissionId == row.PermissionId && p.IsActive,
            cancellationToken);
        if (!permissionOk)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Permission not found.");
        }

        var duplicate = await db.RoleMenuPermissions.AnyAsync(
            x => x.RoleMenuPermissionId != row.RoleMenuPermissionId
                 && x.RoleId == row.RoleId
                 && x.MenuId == row.MenuId
                 && x.PermissionId == row.PermissionId,
            cancellationToken);
        if (duplicate)
        {
            return RoleMenuPermissionAdminOperationResult.Fail(
                "This role/menu/permission assignment already exists.");
        }

        await EnsureMenuPermissionAsync(db, row.MenuId, row.PermissionId, cancellationToken);

        entity.RoleId = row.RoleId;
        entity.MenuId = row.MenuId;
        entity.PermissionId = row.PermissionId;
        entity.IsAllowed = row.IsAllowed;
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = Truncate(context.UserId!, 10);

        await db.SaveChangesAsync(cancellationToken);
        await _accessRights.RefreshPermissionsAsync(cancellationToken);

        return RoleMenuPermissionAdminOperationResult.Ok();
    }

    public async Task<RoleMenuPermissionAdminOperationResult> DeleteAsync(
        IReadOnlyCollection<int> ids,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return RoleMenuPermissionAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(
                MenuCodes.AdminRolePermissions, PermissionCodes.Delete, cancellationToken))
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Not authorized.");
        }

        if (ids.Count == 0)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("No record selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await (
            from rmp in db.RoleMenuPermissions
            join role in db.Roles on rmp.RoleId equals role.RoleId
            where ids.Contains(rmp.RoleMenuPermissionId) && role.CompanyCode == context.CompanyCode
            select rmp).ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return RoleMenuPermissionAdminOperationResult.Fail("Unable to delete record(s).");
        }

        db.RoleMenuPermissions.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        await _accessRights.RefreshPermissionsAsync(cancellationToken);

        _logger.LogInformation(
            "RoleMenuPermissions deleted. AdminUserId={AdminUserId} Count={Count} CompanyCode={CompanyCode}",
            context.UserId,
            rows.Count,
            context.CompanyCode);

        return RoleMenuPermissionAdminOperationResult.Ok();
    }

    /// <summary>
    /// Ensures the permission is applicable to the menu so AccessRightService will honor it.
    /// </summary>
    private static async Task EnsureMenuPermissionAsync(
        AppDbContext db,
        int menuId,
        int permissionId,
        CancellationToken cancellationToken)
    {
        var exists = await db.MenuPermissions.AnyAsync(
            mp => mp.MenuId == menuId && mp.PermissionId == permissionId,
            cancellationToken);
        if (exists)
        {
            return;
        }

        var sortOrder = await db.Permissions
            .Where(p => p.PermissionId == permissionId)
            .Select(p => p.SortOrder)
            .FirstOrDefaultAsync(cancellationToken);

        db.MenuPermissions.Add(new MenuPermission
        {
            MenuId = menuId,
            PermissionId = permissionId,
            SortOrder = sortOrder,
            IsActive = true
        });
    }

    private static string? ValidateRow(RoleMenuPermissionRow row)
    {
        if (row.RoleId <= 0)
        {
            return "Role is required.";
        }

        if (row.MenuId <= 0)
        {
            return "Menu is required.";
        }

        if (row.PermissionId <= 0)
        {
            return "Permission is required.";
        }

        return null;
    }

    private AdminContext ValidateAdminContext()
    {
        if (!_currentUser.IsAuthenticated)
        {
            return AdminContext.Fail("Not authorized.");
        }

        if (!_currentUser.IsInRole(AdminRole) &&
            !_currentUser.IsInRole(CompanyService.SystemAdminRole))
        {
            return AdminContext.Fail("Not authorized.");
        }

        if (string.IsNullOrWhiteSpace(_currentUser.SubjectUid) ||
            string.IsNullOrWhiteSpace(_currentUser.UserId))
        {
            return AdminContext.Fail("Invalid user identity.");
        }

        var companyCode = _currentUser.CompanyCode?.Trim();
        if (string.IsNullOrWhiteSpace(companyCode))
        {
            return AdminContext.Fail("Invalid company context.");
        }

        return AdminContext.Ok(companyCode, _currentUser.UserId);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private readonly record struct AdminContext(string? CompanyCode, string? UserId, string? Error)
    {
        public static AdminContext Ok(string companyCode, string userId) => new(companyCode, userId, null);

        public static AdminContext Fail(string error) => new(null, null, error);
    }
}
