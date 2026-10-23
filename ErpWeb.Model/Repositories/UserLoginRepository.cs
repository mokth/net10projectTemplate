using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Model.Repositories;

public interface IUserLoginRepository
{
    Task<UserLogin?> FindByLoginAsync(string companyCode, string username, CancellationToken cancellationToken = default);
    Task<UserLogin?> GetByUidAsync(int uid, CancellationToken cancellationToken = default);
    Task<UserLogin?> UpdatePasswordAsync(int uid, string passwordHash, string? updatedUid, CancellationToken cancellationToken = default);

    /// <summary>Live, company-scoped current-user row (active, not soft-deleted).</summary>
    Task<UserLogin?> GetActiveByUidAsync(int uid, string companyCode, CancellationToken cancellationToken = default);

    /// <summary>Compare-and-set own-password change: updates only when the verified security state still holds. Returns false on any predicate mismatch (including concurrency conflicts).</summary>
    Task<bool> TryChangeOwnPasswordAsync(int uid, string companyCode, string expectedPasswordHash, bool expectedChangePass, string newPasswordHash, string updatedUid, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserAdminRow>> ListByCompanyAsync(string companyCode, CancellationToken cancellationToken = default);

    /// <summary>Company-scoped admin row; soft-deleted rows are excluded.</summary>
    Task<UserAdminRow?> GetAdminRowAsync(int uid, string companyCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Company-scoped, non-deleted security state for admin reset/force-change. The password hash is consumed only
    /// by the guarded mutation (compare-and-set predicate) and never surfaces to the UI.
    /// </summary>
    Task<UserAdminSecurity?> GetAdminSecurityAsync(int uid, string companyCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-set admin reset guarded by the security state originally inspected: expected password hash,
    /// expected changepass, company, non-deleted and protected-role state. A stale read matches 0 rows and is
    /// reported as a concurrency conflict rather than overwriting newer state.
    /// </summary>
    Task<UserSecurityMutationResult> TryResetPasswordAsync(
        int uid,
        string companyCode,
        string expectedPasswordHash,
        bool expectedChangePass,
        string newPasswordHash,
        string updatedUid,
        string? protectRoleCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compare-and-set force-change toggle guarded by the expected password hash and changepass (not changepass
    /// alone), so a stale force-clear cannot defeat a concurrent password reset.
    /// </summary>
    Task<UserSecurityMutationResult> TrySetChangePassAsync(
        int uid,
        string companyCode,
        string expectedPasswordHash,
        bool expectedChangePass,
        bool changePass,
        string updatedUid,
        string? protectRoleCode,
        CancellationToken cancellationToken = default);

    /// <summary>Company users for the CRUD grid as a password-free projection. Soft-deleted rows are excluded.</summary>
    Task<IReadOnlyList<UserAdminRow>> ListForAdminAsync(string companyCode, CancellationToken cancellationToken = default);

    /// <summary>True when the login id is taken in the company, including soft-deleted rows whose ids remain reserved.</summary>
    Task<bool> LoginExistsAsync(string companyCode, string loginId, int? excludeUid = null, CancellationToken cancellationToken = default);

    Task<UserLogin?> AddAsync(UserLogin user, CancellationToken cancellationToken = default);

    /// <summary>Allow-list profile update: name, email, mobileno, active, userlevel and CanViewPrice only. Guarded against soft-deleted and protected-role targets.</summary>
    Task<bool> UpdateProfileAsync(EditUserRequest user, string companyCode, string updatedUid, string? protectRoleCode, CancellationToken cancellationToken = default);

    /// <summary>All-or-none soft delete of the exact uid set; returns 0 (deleting nothing) if any selected row is missing, already deleted, or protected.</summary>
    Task<int> SoftDeleteAsync(IReadOnlyCollection<int> uids, string companyCode, string updatedUid, string? protectRoleCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoleOptionRow>> ListActiveRolesAsync(string companyCode, CancellationToken cancellationToken = default);

    /// <summary>True when the role exists, belongs to the company, and is active.</summary>
    Task<bool> IsActiveRoleAsync(string companyCode, string roleCode, CancellationToken cancellationToken = default);
}

public sealed class RoleOptionRow
{
    public string RoleCode { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
}

/// <summary>
/// Security state read for admin reset/force-change authorization and compare-and-set. The password hash is
/// Model/Core-only: the service uses it solely as the CAS predicate and never returns it to the UI.
/// </summary>
public sealed class UserAdminSecurity
{
    public string LoginId { get; init; } = string.Empty;

    public string? RoleCode { get; init; }

    public string PasswordHash { get; init; } = string.Empty;

    public bool ChangePass { get; init; }
}

/// <summary>Explicit outcome of a guarded password/security mutation so callers can distinguish conflict from not-found and protected targets.</summary>
public enum UserSecurityMutationStatus
{
    Success = 0,
    NotFoundOrDeleted = 1,
    ProtectedTarget = 2,
    ConcurrencyConflict = 3
}

public readonly record struct UserSecurityMutationResult(UserSecurityMutationStatus Status)
{
    public bool Succeeded => Status == UserSecurityMutationStatus.Success;

    public static UserSecurityMutationResult Ok() => new(UserSecurityMutationStatus.Success);

    public static UserSecurityMutationResult Fail(UserSecurityMutationStatus status) => new(status);
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

    public async Task<UserLogin?> GetActiveByUidAsync(
        int uid,
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserLogins
            .AsNoTracking()
            .Where(x => x.uid == uid && x.CompanyCode == companyCode)
            .Where(x => x.active == true)
            .Where(x => x.UpdatedUID == null || x.UpdatedUID != DeletedMarker)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> TryChangeOwnPasswordAsync(
        int uid,
        string companyCode,
        string expectedPasswordHash,
        bool expectedChangePass,
        string newPasswordHash,
        string updatedUid,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var updated = DateTime.Now;
        var rows = await db.UserLogins
            .Where(x => x.uid == uid && x.CompanyCode == companyCode)
            .Where(x => x.active == true)
            .Where(x => x.UpdatedUID == null || x.UpdatedUID != DeletedMarker)
            .Where(x => x.password == expectedPasswordHash)
            .Where(x => x.changepass == expectedChangePass)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.password, newPasswordHash)
                    .SetProperty(x => x.changepass, false)
                    .SetProperty(x => x.Updated, updated)
                    .SetProperty(x => x.UpdatedUID, updatedUid),
                cancellationToken);

        return rows > 0;
    }

    public async Task<IReadOnlyList<UserAdminRow>> ListByCompanyAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserLogins
            .AsNoTracking()
            .Where(x => x.CompanyCode == companyCode)
            .OrderBy(x => x.id)
            .Select(ProjectUserAdminRow())
            .ToListAsync(cancellationToken);
    }

    public async Task<UserAdminRow?> GetAdminRowAsync(
        int uid,
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserLogins
            .AsNoTracking()
            .Where(x => x.uid == uid && x.CompanyCode == companyCode)
            .Where(x => x.UpdatedUID == null || x.UpdatedUID != DeletedMarker)
            .Select(ProjectUserAdminRow())
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<UserAdminSecurity?> GetAdminSecurityAsync(
        int uid,
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserLogins
            .AsNoTracking()
            .Where(x => x.uid == uid && x.CompanyCode == companyCode)
            .Where(x => x.UpdatedUID == null || x.UpdatedUID != DeletedMarker)
            .Select(x => new UserAdminSecurity
            {
                LoginId = x.id,
                RoleCode = x.userlevel,
                PasswordHash = x.password,
                ChangePass = x.changepass
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<UserSecurityMutationResult> TryResetPasswordAsync(
        int uid,
        string companyCode,
        string expectedPasswordHash,
        bool expectedChangePass,
        string newPasswordHash,
        string updatedUid,
        string? protectRoleCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var classification = await ClassifyTargetAsync(db, uid, companyCode, protectRoleCode, cancellationToken);
        if (classification is not null)
        {
            return UserSecurityMutationResult.Fail(classification.Value);
        }

        var updated = DateTime.Now;
        var query = db.UserLogins
            .Where(x => x.uid == uid && x.CompanyCode == companyCode)
            .Where(x => x.UpdatedUID == null || x.UpdatedUID != DeletedMarker)
            .Where(x => x.password == expectedPasswordHash)
            .Where(x => x.changepass == expectedChangePass);
        query = WhereNotProtectedRole(query, protectRoleCode);

        var rows = await query.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(x => x.password, newPasswordHash)
                .SetProperty(x => x.changepass, true)
                .SetProperty(x => x.Updated, updated)
                .SetProperty(x => x.UpdatedUID, updatedUid),
            cancellationToken);

        return rows > 0
            ? UserSecurityMutationResult.Ok()
            : UserSecurityMutationResult.Fail(UserSecurityMutationStatus.ConcurrencyConflict);
    }

    public async Task<UserSecurityMutationResult> TrySetChangePassAsync(
        int uid,
        string companyCode,
        string expectedPasswordHash,
        bool expectedChangePass,
        bool changePass,
        string updatedUid,
        string? protectRoleCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var classification = await ClassifyTargetAsync(db, uid, companyCode, protectRoleCode, cancellationToken);
        if (classification is not null)
        {
            return UserSecurityMutationResult.Fail(classification.Value);
        }

        var updated = DateTime.Now;
        var query = db.UserLogins
            .Where(x => x.uid == uid && x.CompanyCode == companyCode)
            .Where(x => x.UpdatedUID == null || x.UpdatedUID != DeletedMarker)
            .Where(x => x.password == expectedPasswordHash)
            .Where(x => x.changepass == expectedChangePass);
        query = WhereNotProtectedRole(query, protectRoleCode);

        // Never mutates the password hash; a concurrent reset (new hash) makes this CAS match 0 rows.
        var rows = await query.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(x => x.changepass, changePass)
                .SetProperty(x => x.Updated, updated)
                .SetProperty(x => x.UpdatedUID, updatedUid),
            cancellationToken);

        return rows > 0
            ? UserSecurityMutationResult.Ok()
            : UserSecurityMutationResult.Fail(UserSecurityMutationStatus.ConcurrencyConflict);
    }

    public async Task<IReadOnlyList<UserAdminRow>> ListForAdminAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserLogins
            .AsNoTracking()
            .Where(x => x.CompanyCode == companyCode &&
                        (x.UpdatedUID == null || x.UpdatedUID != DeletedMarker))
            .OrderBy(x => x.id)
            .Select(ProjectUserAdminRow())
            .ToListAsync(cancellationToken);
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
                        x.id == loginId);

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

    public async Task<bool> UpdateProfileAsync(
        EditUserRequest user,
        string companyCode,
        string updatedUid,
        string? protectRoleCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var updated = DateTime.Now;
        var query = db.UserLogins
            .Where(x => x.uid == user.Uid && x.CompanyCode == companyCode)
            .Where(x => x.UpdatedUID == null || x.UpdatedUID != DeletedMarker);
        query = WhereNotProtectedRole(query, protectRoleCode);

        // Allow-list: never writes password, id, CompanyCode, BranchCode, LocationCode,
        // ImagePath, Created, UserID or changepass.
        var rows = await query.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(x => x.name, user.Name)
                .SetProperty(x => x.email, user.Email)
                .SetProperty(x => x.mobileno, user.MobileNo)
                .SetProperty(x => x.active, user.Active)
                .SetProperty(x => x.userlevel, user.RoleCode)
                .SetProperty(x => x.CanViewPrice, user.CanViewPrice)
                .SetProperty(x => x.Updated, updated)
                .SetProperty(x => x.UpdatedUID, updatedUid),
            cancellationToken);

        return rows > 0;
    }

    public async Task<int> SoftDeleteAsync(
        IReadOnlyCollection<int> uids,
        string companyCode,
        string updatedUid,
        string? protectRoleCode,
        CancellationToken cancellationToken = default)
    {
        var distinct = uids.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return 0;
        }

        // Soft-delete marker matches legacy AdminUser behavior (UpdatedUID = "deleted").
        _ = updatedUid;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var updated = DateTime.Now;
        var query = db.UserLogins
            .Where(x => distinct.Contains(x.uid) &&
                        x.CompanyCode == companyCode &&
                        (x.UpdatedUID == null || x.UpdatedUID != DeletedMarker));
        query = WhereNotProtectedRole(query, protectRoleCode);

        var rows = await query.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(x => x.active, false)
                .SetProperty(x => x.Updated, updated)
                .SetProperty(x => x.UpdatedUID, DeletedMarker),
            cancellationToken);

        // All-or-none: every requested uid must be matched exactly once; otherwise roll back.
        if (rows != distinct.Length)
        {
            await tx.RollbackAsync(cancellationToken);
            return 0;
        }

        await tx.CommitAsync(cancellationToken);
        return rows;
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

    public async Task<bool> IsActiveRoleAsync(
        string companyCode,
        string roleCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Roles
            .AsNoTracking()
            .AnyAsync(r => r.CompanyCode == companyCode &&
                           r.RoleCode == roleCode &&
                           r.IsActive, cancellationToken);
    }

    private static IQueryable<UserLogin> WhereNotProtectedRole(IQueryable<UserLogin> query, string? protectRoleCode)
    {
        if (string.IsNullOrWhiteSpace(protectRoleCode))
        {
            return query;
        }

        var lower = protectRoleCode.ToLowerInvariant();
        return query.Where(x => (x.userlevel ?? string.Empty).ToLower() != lower);
    }

    private static bool IsProtectedRoleCode(string? roleCode, string? protectRoleCode)
    {
        if (string.IsNullOrWhiteSpace(protectRoleCode))
        {
            return false;
        }

        return string.Equals(roleCode, protectRoleCode, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Company-scoped, non-deleted existence and protection check. Returns a failure status when the target is
    /// missing/deleted or its current role is protected; returns null when the guarded update may proceed.
    /// </summary>
    private static async Task<UserSecurityMutationStatus?> ClassifyTargetAsync(
        AppDbContext db,
        int uid,
        string companyCode,
        string? protectRoleCode,
        CancellationToken cancellationToken)
    {
        var row = await db.UserLogins
            .AsNoTracking()
            .Where(x => x.uid == uid && x.CompanyCode == companyCode)
            .Where(x => x.UpdatedUID == null || x.UpdatedUID != DeletedMarker)
            .Select(x => new { x.userlevel })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return UserSecurityMutationStatus.NotFoundOrDeleted;
        }

        if (IsProtectedRoleCode(row.userlevel, protectRoleCode))
        {
            return UserSecurityMutationStatus.ProtectedTarget;
        }

        return null;
    }

    private static System.Linq.Expressions.Expression<Func<UserLogin, UserAdminRow>> ProjectUserAdminRow() =>
        x => new UserAdminRow
        {
            Uid = x.uid,
            LoginId = x.id,
            Name = x.name,
            Email = x.email,
            MobileNo = x.mobileno,
            Active = x.active,
            RoleCode = x.userlevel,
            ChangePass = x.changepass,
            CanViewPrice = x.CanViewPrice,
            Created = x.Created,
            CreatedBy = x.UserID,
            Updated = x.Updated,
            UpdatedBy = x.UpdatedUID
        };

    private const string DeletedMarker = "deleted";
}
