namespace ErpWeb.Core.Menus;

/// <summary>
/// Immutable effective permissions for one menu after role allow/deny resolution
/// and MenuPermission applicability filtering.
/// </summary>
public sealed record EffectiveMenuPermissions(
    string MenuCode,
    IReadOnlySet<string> Permissions)
{
    public bool CanAccess =>
        Permissions.Contains(PermissionCodes.Access);

    public bool Can(string permissionCode) =>
        CanAccess && Permissions.Contains(permissionCode);
}
