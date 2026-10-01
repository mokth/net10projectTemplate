using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.Core.Planning;

/// <summary>
/// Detects circular BOM references within a company-scoped Product+Definition graph.
/// Shared by Product Definition Save/Activate and BOM explosion.
/// </summary>
public static class CircularBomValidator
{
    /// <summary>Graph node identity: Product + DefinitionCode.</summary>
    public readonly record struct NodeKey(string ProdCode, string DefinitionCode)
    {
        public string Display => Format(ProdCode, DefinitionCode);

        public static NodeKey Create(string? prodCode, string? definitionCode) =>
            new(Normalize(prodCode), Normalize(definitionCode));

        public static string Format(string? prodCode, string? definitionCode) =>
            $"{Normalize(prodCode)}[{Normalize(definitionCode)}]";
    }

    /// <summary>
    /// Builds adjacency from parent definition → child definitions, then checks whether
    /// adding/using <paramref name="prod"/> → <paramref name="componentNodes"/> creates a cycle.
    /// </summary>
    public static string? FindCyclePath(
        NodeKey prod,
        IReadOnlyList<NodeKey> componentNodes,
        IReadOnlyDictionary<NodeKey, IReadOnlyList<NodeKey>> existingGraph)
    {
        var graph = new Dictionary<NodeKey, HashSet<NodeKey>>();
        foreach (var (parent, children) in existingGraph)
        {
            var parentKey = NodeKey.Create(parent.ProdCode, parent.DefinitionCode);
            if (!graph.TryGetValue(parentKey, out var set))
            {
                set = [];
                graph[parentKey] = set;
            }

            foreach (var child in children)
            {
                set.Add(NodeKey.Create(child.ProdCode, child.DefinitionCode));
            }
        }

        var prodKey = NodeKey.Create(prod.ProdCode, prod.DefinitionCode);
        if (!graph.TryGetValue(prodKey, out var prodChildren))
        {
            prodChildren = [];
            graph[prodKey] = prodChildren;
        }
        else
        {
            prodChildren.Clear();
        }

        foreach (var c in componentNodes)
        {
            var child = NodeKey.Create(c.ProdCode, c.DefinitionCode);
            if (child.ProdCode.Length == 0 || child.DefinitionCode.Length == 0)
            {
                continue;
            }

            if (child == prodKey)
            {
                return $"{prodKey.Display} → {child.Display}";
            }

            prodChildren.Add(child);
        }

        foreach (var child in prodChildren)
        {
            var path = new List<NodeKey> { prodKey };
            if (Dfs(child, prodKey, graph, path, []))
            {
                return string.Join(" → ", path.Select(x => x.Display));
            }
        }

        return null;
    }

    /// <summary>
    /// Walks from <paramref name="start"/> looking for a return to any ancestor (cycle).
    /// </summary>
    public static string? FindCycleFromRoot(
        NodeKey start,
        IReadOnlyDictionary<NodeKey, IReadOnlyList<NodeKey>> graph)
    {
        var adj = new Dictionary<NodeKey, IReadOnlyList<NodeKey>>();
        foreach (var kv in graph)
        {
            var key = NodeKey.Create(kv.Key.ProdCode, kv.Key.DefinitionCode);
            adj[key] = kv.Value
                .Select(x => NodeKey.Create(x.ProdCode, x.DefinitionCode))
                .Where(x => x.ProdCode.Length > 0 && x.DefinitionCode.Length > 0)
                .ToList();
        }

        var root = NodeKey.Create(start.ProdCode, start.DefinitionCode);
        var path = new List<NodeKey>();
        var visiting = new HashSet<NodeKey>();
        if (DfsVisit(root, adj, path, visiting, []))
        {
            return string.Join(" → ", path.Select(x => x.Display));
        }

        return null;
    }

    private static bool Dfs(
        NodeKey current,
        NodeKey target,
        IReadOnlyDictionary<NodeKey, HashSet<NodeKey>> graph,
        List<NodeKey> path,
        HashSet<NodeKey> visiting)
    {
        path.Add(current);
        if (current == target)
        {
            return true;
        }

        if (!visiting.Add(current))
        {
            path.RemoveAt(path.Count - 1);
            return false;
        }

        if (graph.TryGetValue(current, out var children))
        {
            foreach (var child in children)
            {
                if (Dfs(child, target, graph, path, visiting))
                {
                    return true;
                }
            }
        }

        visiting.Remove(current);
        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static bool DfsVisit(
        NodeKey current,
        IReadOnlyDictionary<NodeKey, IReadOnlyList<NodeKey>> graph,
        List<NodeKey> path,
        HashSet<NodeKey> visiting,
        HashSet<NodeKey> visited)
    {
        path.Add(current);
        if (!visiting.Add(current))
        {
            return true;
        }

        if (visited.Add(current) && graph.TryGetValue(current, out var children))
        {
            foreach (var child in children)
            {
                if (DfsVisit(child, graph, path, visiting, visited))
                {
                    return true;
                }
            }
        }

        visiting.Remove(current);
        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();
}
