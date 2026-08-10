namespace ErpWeb.Core.Menus;

public sealed class NavigationService : INavigationService
{
    private readonly IMenuService _menuService;
    private readonly IAccessRightService _accessRights;

    public NavigationService(IMenuService menuService, IAccessRightService accessRights)
    {
        _menuService = menuService;
        _accessRights = accessRights;
    }

    public async Task<IReadOnlyList<MenuNavItem>> GetSidebarAsync(CancellationToken cancellationToken = default)
    {
        var tree = await _menuService.GetActiveTreeAsync(cancellationToken);
        var filtered = new List<MenuNavItem>();

        foreach (var node in tree)
        {
            var pruned = await FilterNodeAsync(node, cancellationToken);
            if (pruned is not null)
            {
                filtered.Add(pruned);
            }
        }

        return filtered;
    }

    private async Task<MenuNavItem?> FilterNodeAsync(MenuNavItem node, CancellationToken cancellationToken)
    {
        var isLeaf = node.Children.Count == 0;
        if (isLeaf)
        {
            if (node.AlwaysVisible || await _accessRights.CanAccessAsync(node.Code, cancellationToken))
            {
                return node with { Children = Array.Empty<MenuNavItem>() };
            }

            return null;
        }

        var childResults = new List<MenuNavItem>();
        foreach (var child in node.Children)
        {
            var prunedChild = await FilterNodeAsync(child, cancellationToken);
            if (prunedChild is not null)
            {
                childResults.Add(prunedChild);
            }
        }

        // Group: visible only when at least one descendant remains
        if (childResults.Count == 0)
        {
            return null;
        }

        return node with { Children = childResults };
    }
}
