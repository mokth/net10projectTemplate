namespace ErpWeb.Core.Menus;

/// <summary>
/// Outcome of reconciling a user's authoritative <c>userlevel</c> role into the
/// <c>UserRoleMapping</c> table during login. Reconciliation is a security gate: any
/// non-success outcome must fail authentication rather than mint authorization metadata.
/// </summary>
public enum UserRoleSyncStatus
{
    /// <summary>Sentinel so a default-initialized result is never treated as success.</summary>
    None = 0,

    Success = 1,
    BlankRole = 2,
    RoleMissing = 3,
    RoleInactive = 4,
    WrongCompany = 5,
    ConcurrencyFailure = 6
}

public readonly record struct UserRoleSyncResult(UserRoleSyncStatus Status, string? Detail)
{
    public bool Succeeded => Status == UserRoleSyncStatus.Success;

    public static UserRoleSyncResult Ok() => new(UserRoleSyncStatus.Success, null);

    public static UserRoleSyncResult Fail(UserRoleSyncStatus status, string? detail = null) =>
        new(status, detail);
}

public interface IUserRoleSyncService
{
    Task<UserRoleSyncResult> ReconcileFromUserLevelAsync(
        int uid,
        string companyCode,
        string? userlevel,
        CancellationToken cancellationToken = default);
}
