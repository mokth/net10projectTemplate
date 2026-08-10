using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Security;

public sealed class RoleAdminService : IRoleAdminService
{
    public const string AdminRole = "ADMIN";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICurrentUserService _currentUser;
    private readonly IAccessRightService _accessRights;
    private readonly ILogger<RoleAdminService> _logger;

    public RoleAdminService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICurrentUserService currentUser,
        IAccessRightService accessRights,
        ILogger<RoleAdminService> logger)
    {
        _dbFactory = dbFactory;
        _currentUser = currentUser;
        _accessRights = accessRights;
        _logger = logger;
    }

    public async Task<RoleAdminOperationResult> GetRolesAsync(CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return RoleAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminRoles, PermissionCodes.Access, cancellationToken))
        {
            return RoleAdminOperationResult.Fail("Not authorized.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var roles = await db.Roles
            .AsNoTracking()
            .Where(r => r.CompanyCode == context.CompanyCode)
            .OrderBy(r => r.RoleCode)
            .ToListAsync(cancellationToken);

        return RoleAdminOperationResult.Ok(roles);
    }

    public async Task<RoleAdminOperationResult> AddRoleAsync(
        Role role,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return RoleAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminRoles, PermissionCodes.Add, cancellationToken))
        {
            return RoleAdminOperationResult.Fail("Not authorized.");
        }

        var roleCode = (role.RoleCode ?? string.Empty).Trim().ToUpperInvariant();
        var roleName = (role.RoleName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(roleCode))
        {
            return RoleAdminOperationResult.Fail("Role code is required.");
        }

        if (roleCode.Length > 20)
        {
            return RoleAdminOperationResult.Fail("Role code must be at most 20 characters.");
        }

        if (string.IsNullOrWhiteSpace(roleName))
        {
            return RoleAdminOperationResult.Fail("Role name is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var exists = await db.Roles.AnyAsync(
            r => r.CompanyCode == context.CompanyCode && r.RoleCode == roleCode,
            cancellationToken);
        if (exists)
        {
            return RoleAdminOperationResult.Fail("Role code already exists.");
        }

        var entity = new Role
        {
            CompanyCode = context.CompanyCode!,
            RoleCode = roleCode,
            RoleName = roleName,
            IsActive = role.IsActive,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = Truncate(context.UserId!, 10)
        };

        db.Roles.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Role created. AdminUserId={AdminUserId} RoleCode={RoleCode} CompanyCode={CompanyCode}",
            context.UserId,
            entity.RoleCode,
            context.CompanyCode);

        return RoleAdminOperationResult.Ok();
    }

    public async Task<RoleAdminOperationResult> UpdateRoleAsync(
        Role role,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return RoleAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminRoles, PermissionCodes.Edit, cancellationToken))
        {
            return RoleAdminOperationResult.Fail("Not authorized.");
        }

        if (role.RoleId <= 0)
        {
            return RoleAdminOperationResult.Fail("Invalid role.");
        }

        var roleName = (role.RoleName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(roleName))
        {
            return RoleAdminOperationResult.Fail("Role name is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Roles.FirstOrDefaultAsync(
            r => r.RoleId == role.RoleId && r.CompanyCode == context.CompanyCode,
            cancellationToken);
        if (entity is null)
        {
            return RoleAdminOperationResult.Fail("Role not found.");
        }

        // RoleCode is immutable after create (maps to userlevel).
        entity.RoleName = roleName;
        entity.IsActive = role.IsActive;
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = Truncate(context.UserId!, 10);

        await db.SaveChangesAsync(cancellationToken);
        return RoleAdminOperationResult.Ok();
    }

    public async Task<RoleAdminOperationResult> DeleteRolesAsync(
        IReadOnlyCollection<int> roleIds,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateAdminContext();
        if (context.Error is not null)
        {
            return RoleAdminOperationResult.Fail(context.Error);
        }

        if (!await _accessRights.CanAsync(MenuCodes.AdminRoles, PermissionCodes.Delete, cancellationToken))
        {
            return RoleAdminOperationResult.Fail("Not authorized.");
        }

        if (roleIds.Count == 0)
        {
            return RoleAdminOperationResult.Fail("No record selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var roles = await db.Roles
            .Where(r => roleIds.Contains(r.RoleId) && r.CompanyCode == context.CompanyCode)
            .ToListAsync(cancellationToken);

        if (roles.Count == 0)
        {
            return RoleAdminOperationResult.Fail("Unable to delete role(s).");
        }

        if (roles.Any(r => string.Equals(r.RoleCode, AdminRole, StringComparison.OrdinalIgnoreCase)))
        {
            return RoleAdminOperationResult.Fail("The ADMIN role cannot be deleted.");
        }

        var ids = roles.Select(r => r.RoleId).ToList();
        var hasUsers = await db.UserRoleMappings.AnyAsync(m => ids.Contains(m.RoleId), cancellationToken);
        if (hasUsers)
        {
            return RoleAdminOperationResult.Fail(
                "One or more roles are assigned to users. Deactivate them instead of deleting.");
        }

        var permissionRows = await db.RoleMenuPermissions
            .Where(x => ids.Contains(x.RoleId))
            .ToListAsync(cancellationToken);
        if (permissionRows.Count > 0)
        {
            db.RoleMenuPermissions.RemoveRange(permissionRows);
        }

        db.Roles.RemoveRange(roles);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Roles deleted. AdminUserId={AdminUserId} Count={Count} CompanyCode={CompanyCode}",
            context.UserId,
            roles.Count,
            context.CompanyCode);

        return RoleAdminOperationResult.Ok();
    }

    private AdminContext ValidateAdminContext()
    {
        if (!_currentUser.IsAuthenticated)
        {
            return AdminContext.Fail("Not authorized.");
        }

        if (!_currentUser.IsInRole(AdminRole))
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
