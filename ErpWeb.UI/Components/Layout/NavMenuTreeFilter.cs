namespace ErpWeb.UI.Components.Layout;

internal sealed record NavTreeItem
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Route { get; init; }
    public string? Icon { get; init; }
    public Dictionary<string, object> Attributes { get; init; } = new();
    public IReadOnlyList<NavTreeItem> Children { get; init; } = Array.Empty<NavTreeItem>();
}

internal static class NavMenuTreeFilter
{
    public static IReadOnlyList<NavTreeItem> Filter(
        IReadOnlyList<NavTreeItem> items,
        string? searchText)
    {
        if (items.Count == 0 || string.IsNullOrWhiteSpace(searchText))
        {
            return items;
        }

        var tokens = searchText.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            return items;
        }

        return FilterNodes(items, tokens, Array.Empty<string>());
    }

    public static bool HasExpandableNodes(IReadOnlyList<NavTreeItem>? items)
    {
        if (items is null || items.Count == 0)
        {
            return false;
        }

        foreach (var item in items)
        {
            if (item.Children.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    public static bool AreTreesEqual(
        IReadOnlyList<NavTreeItem>? left,
        IReadOnlyList<NavTreeItem>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Code, b.Code, StringComparison.Ordinal)
                || !string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                || !string.Equals(a.Route, b.Route, StringComparison.Ordinal)
                || !string.Equals(a.Icon, b.Icon, StringComparison.Ordinal)
                || !AreTreesEqual(a.Children, b.Children))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<NavTreeItem> FilterNodes(
        IReadOnlyList<NavTreeItem> nodes,
        string[] tokens,
        IReadOnlyList<string> ancestorNames)
    {
        List<NavTreeItem>? matched = null;

        foreach (var node in nodes)
        {
            var currentPath = new string[ancestorNames.Count + 1];
            for (var i = 0; i < ancestorNames.Count; i++)
            {
                currentPath[i] = ancestorNames[i];
            }
            currentPath[ancestorNames.Count] = node.Name;

            var nodeMatchesAllTokens = MatchesAllTokens(currentPath, tokens);
            if (nodeMatchesAllTokens)
            {
                // When a group's path itself satisfies the entire query (e.g. searching "Inventory"),
                // keep its full subtree so users can browse everything inside that module.
                matched ??= new List<NavTreeItem>();
                matched.Add(node);
                continue;
            }

            if (node.Children.Count == 0)
            {
                continue;
            }

            var filteredChildren = FilterNodes(node.Children, tokens, currentPath);
            if (filteredChildren.Count == 0)
            {
                continue;
            }

            matched ??= new List<NavTreeItem>();
            matched.Add(node with { Children = filteredChildren });
        }

        return matched ?? (IReadOnlyList<NavTreeItem>)Array.Empty<NavTreeItem>();
    }

    private static bool MatchesAllTokens(IReadOnlyList<string> pathSegments, string[] tokens)
    {
        foreach (var token in tokens)
        {
            var found = false;
            foreach (var segment in pathSegments)
            {
                if (segment.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }
}
