using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.Core.Planning;

/// <summary>
/// Detects circular BOM references within a company-scoped component graph.
/// Shared by Product Definition Save/Activate and BOM explosion.
/// </summary>
public static class CircularBomValidator
{
    /// <summary>
    /// Builds adjacency from prod → direct component codes, then checks whether
    /// adding/using <paramref name="prodCode"/> → <paramref name="componentCodes"/> creates a cycle.
    /// </summary>
    public static string? FindCyclePath(
        string prodCode,
        IReadOnlyList<string> componentCodes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> existingGraph)
    {
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (parent, children) in existingGraph)
        {
            if (!graph.TryGetValue(parent, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                graph[parent] = set;
            }

            foreach (var child in children)
            {
                set.Add(child);
            }
        }

        var prod = Normalize(prodCode);
        if (!graph.TryGetValue(prod, out var prodChildren))
        {
            prodChildren = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            graph[prod] = prodChildren;
        }
        else
        {
            prodChildren.Clear();
        }

        foreach (var c in componentCodes)
        {
            var child = Normalize(c);
            if (child.Length == 0)
            {
                continue;
            }

            if (string.Equals(child, prod, StringComparison.OrdinalIgnoreCase))
            {
                return $"{prod} → {child}";
            }

            prodChildren.Add(child);
        }

        foreach (var child in prodChildren)
        {
            var path = new List<string> { prod };
            if (Dfs(child, prod, graph, path, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
            {
                return string.Join(" → ", path);
            }
        }

        return null;
    }

    /// <summary>
    /// Walks from <paramref name="start"/> looking for a return to any ancestor (cycle).
    /// </summary>
    public static string? FindCycleFromRoot(
        string start,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        var adj = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in graph)
        {
            adj[Normalize(kv.Key)] = kv.Value.Select(Normalize).Where(x => x.Length > 0).ToList();
        }

        var root = Normalize(start);
        var path = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (DfsVisit(root, adj, path, visiting, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
        {
            return string.Join(" → ", path);
        }

        return null;
    }

    private static bool Dfs(
        string current,
        string target,
        IReadOnlyDictionary<string, HashSet<string>> graph,
        List<string> path,
        HashSet<string> visiting)
    {
        path.Add(current);
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
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
        string current,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph,
        List<string> path,
        HashSet<string> visiting,
        HashSet<string> visited)
    {
        path.Add(current);
        if (!visiting.Add(current))
        {
            // cycle — path already ends with repeat; keep cycle suffix
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
