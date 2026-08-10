namespace ErpWeb.Core.Menus;

public interface IAccessRightService
{
    Task<bool> CanAccessAsync(string menuCode, CancellationToken cancellationToken = default);

    Task<bool> CanAsync(string menuCode, string permissionCode, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetPermissionsAsync(string menuCode, CancellationToken cancellationToken = default);

    Task<MenuAccessRight?> GetAccessAsync(string menuCode, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, MenuAccessRight>> GetAllAccessAsync(CancellationToken cancellationToken = default);

    Task RefreshPermissionsAsync(CancellationToken cancellationToken = default);
}
