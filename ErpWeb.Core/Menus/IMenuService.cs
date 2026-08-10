namespace ErpWeb.Core.Menus;

public interface IMenuService
{
    Task<IReadOnlyList<MenuNavItem>> GetActiveTreeAsync(CancellationToken cancellationToken = default);
    void InvalidateCache();
}
