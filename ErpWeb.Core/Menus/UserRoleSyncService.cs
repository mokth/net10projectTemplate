using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Menus;

public sealed class UserRoleSyncService : IUserRoleSyncService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<UserRoleSyncService> _logger;

    public UserRoleSyncService(IDbContextFactory<AppDbContext> dbFactory, ILogger<UserRoleSyncService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task ReconcileFromUserLevelAsync(
        int uid,
        string companyCode,
        string? userlevel,
        CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || string.IsNullOrWhiteSpace(companyCode))
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var companyRoles = await db.Roles
            .Where(r => r.CompanyCode == companyCode)
            .ToListAsync(cancellationToken);

        var companyRoleIds = companyRoles.Select(r => r.RoleId).ToHashSet();

        var mappings = await db.UserRoleMappings
            .Where(m => m.UserUid == uid && companyRoleIds.Contains(m.RoleId))
            .ToListAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(userlevel))
        {
            if (mappings.Count > 0)
            {
                db.UserRoleMappings.RemoveRange(mappings);
                await db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "Cleared company role mappings for uid {UserUid} company {CompanyCode} (blank userlevel)",
                    uid, companyCode);
            }

            return;
        }

        var role = companyRoles.FirstOrDefault(r =>
            string.Equals(r.RoleCode, userlevel, StringComparison.OrdinalIgnoreCase));

        if (role is null)
        {
            role = new Role
            {
                CompanyCode = companyCode,
                RoleCode = userlevel.Trim(),
                RoleName = userlevel.Trim(),
                IsActive = true,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = "LOGIN"
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Created role {RoleCode} for company {CompanyCode} during login reconcile",
                role.RoleCode, companyCode);
        }

        var stale = mappings.Where(m => m.RoleId != role.RoleId).ToList();
        if (stale.Count > 0)
        {
            db.UserRoleMappings.RemoveRange(stale);
        }

        if (!mappings.Any(m => m.RoleId == role.RoleId))
        {
            db.UserRoleMappings.Add(new UserRoleMapping
            {
                UserUid = uid,
                RoleId = role.RoleId
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
