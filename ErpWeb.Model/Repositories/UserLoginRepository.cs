using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories;

public interface IUserLoginRepository
{
    Task<UserLogin?> FindByLoginAsync(string companyCode, string username, CancellationToken cancellationToken = default);
    Task<UserLogin?> GetByUidAsync(int uid, CancellationToken cancellationToken = default);
    Task<UserLogin?> UpdatePasswordAsync(int uid, string passwordHash, string? updatedUid, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PasswordAdminUserRow>> ListByCompanyAsync(string companyCode, CancellationToken cancellationToken = default);
    Task<PasswordAdminUserRow?> GetAdminRowAsync(int uid, string companyCode, CancellationToken cancellationToken = default);
    Task<bool> ResetPasswordAsync(int uid, string companyCode, string passwordHash, string updatedUid, CancellationToken cancellationToken = default);
    Task<bool> SetChangePassAsync(int uid, string companyCode, bool changePass, string updatedUid, CancellationToken cancellationToken = default);

    /// <summary>Company users for CRUD grid; password hashes are cleared. Soft-deleted rows are excluded.</summary>
    Task<IReadOnlyList<UserLogin>> ListForAdminAsync(string companyCode, CancellationToken cancellationToken = default);

    Task<bool> LoginExistsAsync(string companyCode, string loginId, int? excludeUid = null, CancellationToken cancellationToken = default);

    Task<UserLogin?> AddAsync(UserLogin user, CancellationToken cancellationToken = default);

    Task<bool> UpdateProfileAsync(UserLogin user, CancellationToken cancellationToken = default);

    Task<int> SoftDeleteAsync(IReadOnlyCollection<int> uids, string companyCode, string updatedUid, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoleOptionRow>> ListActiveRolesAsync(string companyCode, CancellationToken cancellationToken = default);
}

public sealed class RoleOptionRow
{
    public string RoleCode { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
}

public class UserLoginRepository : IUserLoginRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public UserLoginRepository(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<UserLogin?> FindByLoginAsync(string companyCode, string username, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserLogins
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.id == username && x.CompanyCode == companyCode, cancellationToken);
    }

    public async Task<UserLogin?> GetByUidAsync(int uid, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserLogins
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.uid == uid, cancellationToken);
    }

    public async Task<UserLogin?> UpdatePasswordAsync(
        int uid,
        string passwordHash,
        string? updatedUid,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.UserLogins.FirstOrDefaultAsync(x => x.uid == uid, cancellationToken);
        if (user is null)
        {
            return null;
        }

        user.password = passwordHash;
        user.changepass = false;
        user.Updated = DateTime.Now;
        user.UpdatedUID = updatedUid;
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task<IReadOnlyList<PasswordAdminUserRow>> ListByCompanyAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserLogins
            .AsNoTracking()
            .Where(x => x.CompanyCode == companyCode)
            .OrderBy(x => x.id)
            .Select(x => new PasswordAdminUserRow
            {
                Uid = x.uid,
                LoginId = x.id,
                Name = x.name,
                UserId = x.UserID,
                Active = x.active,
                ChangePass = x.changepass
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<PasswordAdminUserRow?> GetAdminRowAsync(
        int uid,
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserLogins
            .AsNoTracking()
            .Where(x => x.uid == uid && x.CompanyCode == companyCode)
            .Select(x => new PasswordAdminUserRow
            {
                Uid = x.uid,
                LoginId = x.id,
                Name = x.name,
                UserId = x.UserID,
                Active = x.active,
                ChangePass = x.changepass
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> ResetPasswordAsync(
        int uid,
        string companyCode,
        string passwordHash,
        string updatedUid,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var updated = DateTime.Now;
        var rows = await db.UserLogins
            .Where(x => x.uid == uid && x.CompanyCode == companyCode)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.password, passwordHash)
                    .SetProperty(x => x.changepass, true)
                    .SetProperty(x => x.Updated, updated)
                    .SetProperty(x => x.UpdatedUID, updatedUid),
                cancellationToken);

        return rows > 0;
    }

    public async Task<bool> SetChangePassAsync(
        int uid,
        string companyCode,
        bool changePass,
        string updatedUid,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var updated = DateTime.Now;
        var rows = await db.UserLogins
            .Where(x => x.uid == uid && x.CompanyCode == companyCode)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.changepass, changePass)
                    .SetProperty(x => x.Updated, updated)
                    .SetProperty(x => x.UpdatedUID, updatedUid),
                cancellationToken);

        return rows > 0;
    }

    public async Task<IReadOnlyList<UserLogin>> ListForAdminAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var users = await db.UserLogins
            .AsNoTracking()
            .Where(x => x.CompanyCode == companyCode &&
                        (x.UpdatedUID == null || x.UpdatedUID != DeletedMarker))
            .OrderBy(x => x.id)
            .ToListAsync(cancellationToken);

        foreach (var user in users)
        {
            user.password = string.Empty;
        }

        return users;
    }

    public async Task<bool> LoginExistsAsync(
        string companyCode,
        string loginId,
        int? excludeUid = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.UserLogins.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode &&
                        x.id == loginId &&
                        (x.UpdatedUID == null || x.UpdatedUID != DeletedMarker));

        if (excludeUid is int uid)
        {
            query = query.Where(x => x.uid != uid);
        }

        return await query.AnyAsync(cancellationToken);
    }

    public async Task<UserLogin?> AddAsync(UserLogin user, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.UserLogins.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        user.password = string.Empty;
        return user;
    }

    public async Task<bool> UpdateProfileAsync(UserLogin user, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.UserLogins
            .FirstOrDefaultAsync(
                x => x.uid == user.uid &&
                     x.CompanyCode == user.CompanyCode &&
                     (x.UpdatedUID == null || x.UpdatedUID != DeletedMarker),
                cancellationToken);

        if (existing is null)
        {
            return false;
        }

        existing.name = user.name;
        existing.email = user.email;
        existing.mobileno = user.mobileno;
        existing.active = user.active;
        existing.userlevel = user.userlevel;
        existing.BranchCode = user.BranchCode;
        existing.LocationCode = user.LocationCode;
        existing.ImagePath = user.ImagePath;
        existing.Updated = user.Updated;
        existing.UpdatedUID = user.UpdatedUID;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> SoftDeleteAsync(
        IReadOnlyCollection<int> uids,
        string companyCode,
        string updatedUid,
        CancellationToken cancellationToken = default)
    {
        if (uids.Count == 0)
        {
            return 0;
        }

        // Soft-delete marker matches legacy AdminUser behavior (UpdatedUID = "deleted").
        _ = updatedUid;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var updated = DateTime.Now;
        return await db.UserLogins
            .Where(x => uids.Contains(x.uid) &&
                        x.CompanyCode == companyCode &&
                        (x.UpdatedUID == null || x.UpdatedUID != DeletedMarker))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.active, false)
                    .SetProperty(x => x.Updated, updated)
                    .SetProperty(x => x.UpdatedUID, DeletedMarker),
                cancellationToken);
    }

    public async Task<IReadOnlyList<RoleOptionRow>> ListActiveRolesAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Roles
            .AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && x.IsActive)
            .OrderBy(x => x.RoleCode)
            .Select(x => new RoleOptionRow
            {
                RoleCode = x.RoleCode,
                RoleName = x.RoleName
            })
            .ToListAsync(cancellationToken);
    }

    private const string DeletedMarker = "deleted";
}
