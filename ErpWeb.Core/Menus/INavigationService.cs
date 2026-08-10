namespace ErpWeb.Core.Menus;

public interface INavigationService
{
    Task<IReadOnlyList<MenuNavItem>> GetSidebarAsync(CancellationToken cancellationToken = default);
}
