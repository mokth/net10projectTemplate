namespace ErpWeb.Core.Menus;

/// <summary>
/// In-memory expand/collapse state for the sidebar. Auto-expands the active path
/// unless the user has manually collapsed an ancestor.
/// </summary>
public sealed class MenuExpansionState
{
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _userCollapsed = new(StringComparer.OrdinalIgnoreCase);

    public bool IsExpanded(string menuCode) => _expanded.Contains(menuCode);

    public void Toggle(string menuCode)
    {
        if (_expanded.Contains(menuCode))
        {
            _expanded.Remove(menuCode);
            _userCollapsed.Add(menuCode);
        }
        else
        {
            _expanded.Add(menuCode);
            _userCollapsed.Remove(menuCode);
        }
    }

    /// <summary>
    /// Expands ancestors of the active route. Skips codes the user manually collapsed.
    /// When the active branch changes, clears collapse markers that are no longer on the active path
    /// is NOT done — manual collapse is respected until the user expands again or
    /// <paramref name="forceOpenActivePath"/> opens a newly active branch's ancestors that were not user-collapsed.
    /// For a newly active branch, ancestors not in <see cref="_userCollapsed"/> are expanded.
    /// </summary>
    public void ApplyActivePath(IEnumerable<string> ancestorCodesInclusiveOfActiveGroupPath)
    {
        foreach (var code in ancestorCodesInclusiveOfActiveGroupPath)
        {
            if (_userCollapsed.Contains(code))
            {
                continue;
            }

            _expanded.Add(code);
        }
    }

    /// <summary>
    /// When navigating to a different branch, expand the new active path even if those
    /// nodes were previously user-collapsed on the old branch — only if
    /// <paramref name="clearCollapseForPath"/> is true for codes on the new path.
    /// Plan: "navigation to another branch requires the active path to be opened."
    /// </summary>
    public void ExpandActiveBranch(IEnumerable<string> ancestorCodes)
    {
        foreach (var code in ancestorCodes)
        {
            _userCollapsed.Remove(code);
            _expanded.Add(code);
        }
    }

    public static IReadOnlyList<string> FindAncestorGroupCodes(
        IReadOnlyList<MenuNavItem> tree,
        string? activeRoute)
    {
        if (string.IsNullOrWhiteSpace(activeRoute))
        {
            return Array.Empty<string>();
        }

        var path = new List<string>();
        if (TryFindPath(tree, activeRoute, path))
        {
            // path includes the leaf; groups are all but the last when last is leaf
            if (path.Count <= 1)
            {
                return Array.Empty<string>();
            }

            return path.Take(path.Count - 1).ToList();
        }

        return Array.Empty<string>();
    }

    private static bool TryFindPath(IReadOnlyList<MenuNavItem> nodes, string activeRoute, List<string> path)
    {
        foreach (var node in nodes)
        {
            path.Add(node.Code);

            if (node.Children.Count == 0 &&
                RoutesMatch(node.Route, activeRoute))
            {
                return true;
            }

            if (node.Children.Count > 0 && TryFindPath(node.Children, activeRoute, path))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    public static bool RoutesMatch(string? menuRoute, string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(menuRoute) || string.IsNullOrWhiteSpace(currentPath))
        {
            return false;
        }

        if (string.Equals(menuRoute, "/home", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(currentPath, "/home", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(currentPath, "/", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(menuRoute, currentPath, StringComparison.OrdinalIgnoreCase);
    }
}
