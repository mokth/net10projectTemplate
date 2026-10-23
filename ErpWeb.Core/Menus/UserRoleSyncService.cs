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

    public async Task<UserRoleSyncResult> ReconcileFromUserLevelAsync(
        int uid,
        string companyCode,
        string? userlevel,
        CancellationToken cancellationToken = default)
    {
        if (uid <= 0 || string.IsNullOrWhiteSpace(companyCode))
        {
            return UserRoleSyncResult.Fail(UserRoleSyncStatus.RoleMissing, "Invalid user or company context.");
        }

        var level = (userlevel ?? string.Empty).Trim();
        if (level.Length == 0)
        {
            // Blank userlevel is an invalid configuration: fail without touching mappings.
            return UserRoleSyncResult.Fail(UserRoleSyncStatus.BlankRole, "User has no assigned role.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // The authoritative role must already exist and belong to the user's company.
        // No role is ever created here: authentication must not mint authorization metadata.
        var companyRoles = await db.Roles
            .AsNoTracking()
            .Where(r => r.CompanyCode == companyCode)
            .ToListAsync(cancellationToken);

        var role = companyRoles.FirstOrDefault(r =>
            string.Equals(r.RoleCode, level, StringComparison.OrdinalIgnoreCase));

        if (role is null)
        {
            var existsElsewhere = await db.Roles
                .AsNoTracking()
                .AnyAsync(r =>
                    r.CompanyCode != companyCode &&
                    r.RoleCode.ToUpper() == level.ToUpperInvariant(),
                    cancellationToken);

            return existsElsewhere
                ? UserRoleSyncResult.Fail(UserRoleSyncStatus.WrongCompany, $"Role '{level}' belongs to another company.")
                : UserRoleSyncResult.Fail(UserRoleSyncStatus.RoleMissing, $"Role '{level}' does not exist.");
        }

        if (!role.IsActive)
        {
            return UserRoleSyncResult.Fail(UserRoleSyncStatus.RoleInactive, $"Role '{level}' is inactive.");
        }

        var companyRoleIds = companyRoles.Select(r => r.RoleId).ToHashSet();

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Remove stale current-company mappings that no longer match the authoritative
            // role. Mappings pointing at another company's roles are deliberately untouched.
            await db.UserRoleMappings
                .Where(m => m.UserUid == uid &&
                            companyRoleIds.Contains(m.RoleId) &&
                            m.RoleId != role.RoleId)
                .ExecuteDeleteAsync(cancellationToken);

            var exists = await db.UserRoleMappings
                .AnyAsync(m => m.UserUid == uid && m.RoleId == role.RoleId, cancellationToken);
            if (!exists)
            {
                db.UserRoleMappings.Add(new UserRoleMapping { UserUid = uid, RoleId = role.RoleId });
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return UserRoleSyncResult.Ok();
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(cancellationToken);

            // A concurrent login may have committed the exact required mapping between our
            // existence check and our insert (unique-key race). If the final state is already
            // valid, treat the race as success rather than surfacing a spurious failure.
            var valid = await HasValidFinalStateAsync(uid, companyCode, role.RoleId, cancellationToken);
            if (valid)
            {
                return UserRoleSyncResult.Ok();
            }

            // Do not swallow unrelated database failures.
            throw;
        }
    }

    private async Task<bool> HasValidFinalStateAsync(
        int uid,
        string companyCode,
        int targetRoleId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var companyRoleIds = await db.Roles
            .AsNoTracking()
            .Where(r => r.CompanyCode == companyCode)
            .Select(r => r.RoleId)
            .ToListAsync(cancellationToken);

        var mappings = await db.UserRoleMappings
            .AsNoTracking()
            .Where(m => m.UserUid == uid)
            .Select(m => m.RoleId)
            .ToListAsync(cancellationToken);

        var currentCompanyMappings = mappings.Where(companyRoleIds.Contains).ToList();
        return currentCompanyMappings.Count == 1 && currentCompanyMappings[0] == targetRoleId;
    }
}
