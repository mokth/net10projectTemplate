namespace ErpWeb.Core.Menus;

public sealed class MenuAccessRight
{
    public required string MenuCode { get; init; }
    public IReadOnlySet<string> Permissions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool CanAccess => Permissions.Contains(PermissionCodes.Access);
    public bool Can(string permissionCode) =>
        CanAccess && Permissions.Contains(permissionCode);
}
