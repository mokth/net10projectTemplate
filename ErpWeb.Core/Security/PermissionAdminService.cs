using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Security;

public sealed class PermissionAdminService : IPermissionAdminService
{
    public const string AdminRole = "ADMIN";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;
    private readonly ILogger<PermissionAdminService> _logger;

    public PermissionAdminService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        ILogger<PermissionAdminService> logger)
    {
        _dbFactory = dbFactory;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _logger = logger;
    }

    public async Task<PermissionAdminOperationResult> GetPermissionsAsync(
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return PermissionAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminPermissions, PermissionCodes.Access, cancellationToken))
        {
            return PermissionAdminOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var permissions = await db.Permissions
            .AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.PermissionCode)
            .ToListAsync(cancellationToken);

        return PermissionAdminOperationResult.Ok(permissions);
    }

    public async Task<PermissionAdminOperationResult> AddPermissionAsync(
        Permission permission,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return PermissionAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminPermissions, PermissionCodes.Add, cancellationToken))
        {
            return PermissionAdminOperationResult.Fail("Not authorized.");
        }

        var code = (permission.PermissionCode ?? string.Empty).Trim().ToUpperInvariant();
        var name = (permission.PermissionName ?? string.Empty).Trim();
        var type = (permission.PermissionType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return PermissionAdminOperationResult.Fail("Permission code is required.");
        }

        if (code.Length > 50)
        {
            return PermissionAdminOperationResult.Fail("Permission code must be at most 50 characters.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return PermissionAdminOperationResult.Fail("Permission name is required.");
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            return PermissionAdminOperationResult.Fail("Permission type is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var exists = await db.Permissions.AnyAsync(p => p.PermissionCode == code, cancellationToken);
        if (exists)
        {
            return PermissionAdminOperationResult.Fail("Permission code already exists.");
        }

        var entity = new Permission
        {
            PermissionCode = code,
            PermissionName = name,
            PermissionType = type,
            Description = string.IsNullOrWhiteSpace(permission.Description)
                ? null
                : permission.Description.Trim(),
            SortOrder = permission.SortOrder,
            IsActive = permission.IsActive,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = Truncate(context.UserId!, 10)
        };

        db.Permissions.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Permission created. AdminUserId={AdminUserId} PermissionCode={PermissionCode}",
            context.UserId,
            entity.PermissionCode);

        return PermissionAdminOperationResult.Ok();
    }

    public async Task<PermissionAdminOperationResult> UpdatePermissionAsync(
        Permission permission,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return PermissionAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminPermissions, PermissionCodes.Edit, cancellationToken))
        {
            return PermissionAdminOperationResult.Fail("Not authorized.");
        }

        if (permission.PermissionId <= 0)
        {
            return PermissionAdminOperationResult.Fail("Invalid permission.");
        }

        var name = (permission.PermissionName ?? string.Empty).Trim();
        var type = (permission.PermissionType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return PermissionAdminOperationResult.Fail("Permission name is required.");
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            return PermissionAdminOperationResult.Fail("Permission type is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Permissions.FirstOrDefaultAsync(
            p => p.PermissionId == permission.PermissionId,
            cancellationToken);
        if (entity is null)
        {
            return PermissionAdminOperationResult.Fail("Permission not found.");
        }

        // PermissionCode is immutable after create.
        entity.PermissionName = name;
        entity.PermissionType = type;
        entity.Description = string.IsNullOrWhiteSpace(permission.Description)
            ? null
            : permission.Description.Trim();
        entity.SortOrder = permission.SortOrder;
        entity.IsActive = permission.IsActive;
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = Truncate(context.UserId!, 10);

        await db.SaveChangesAsync(cancellationToken);
        return PermissionAdminOperationResult.Ok();
    }

    public async Task<PermissionAdminOperationResult> DeletePermissionsAsync(
        IReadOnlyCollection<int> permissionIds,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return PermissionAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminPermissions, PermissionCodes.Delete, cancellationToken))
        {
            return PermissionAdminOperationResult.Fail("Not authorized.");
        }

        if (permissionIds.Count == 0)
        {
            return PermissionAdminOperationResult.Fail("No record selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var permissions = await db.Permissions
            .Where(p => permissionIds.Contains(p.PermissionId))
            .ToListAsync(cancellationToken);

        if (permissions.Count == 0)
        {
            return PermissionAdminOperationResult.Fail("Unable to delete permission(s).");
        }

        if (permissions.Any(p => PermissionCodes.All.Contains(p.PermissionCode)))
        {
            return PermissionAdminOperationResult.Fail(
                "Built-in permissions cannot be deleted. Deactivate them instead.");
        }

        var ids = permissions.Select(p => p.PermissionId).ToList();
        var inUse = await db.MenuPermissions.AnyAsync(mp => ids.Contains(mp.PermissionId), cancellationToken)
            || await db.RoleMenuPermissions.AnyAsync(rmp => ids.Contains(rmp.PermissionId), cancellationToken);
        if (inUse)
        {
            return PermissionAdminOperationResult.Fail(
                "One or more permissions are in use. Deactivate them instead of deleting.");
        }

        db.Permissions.RemoveRange(permissions);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Permissions deleted. AdminUserId={AdminUserId} Count={Count}",
            context.UserId,
            permissions.Count);

        return PermissionAdminOperationResult.Ok();
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
