using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Menus;

public sealed class MenuDefinitionService : IMenuDefinitionService
{
    private readonly object _gate = new();
    private readonly string _resolvedPath;
    private readonly ILogger<MenuDefinitionService> _logger;
    private IReadOnlyList<MenuDefinitionNode>? _tree;
    private IReadOnlyDictionary<string, MenuDefinitionNode>? _flat;
    private IReadOnlyList<string>? _validationErrors;

    public MenuDefinitionService(
        IOptions<MenusOptions> options,
        IHostEnvironment environment,
        ILogger<MenuDefinitionService> logger)
    {
        _logger = logger;
        var configured = options.Value.XmlPath;
        _resolvedPath = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
    }

    public IReadOnlyList<MenuDefinitionNode> GetTree()
    {
        EnsureLoaded();
        return _tree!;
    }

    public IReadOnlyDictionary<string, MenuDefinitionNode> GetFlatByCode()
    {
        EnsureLoaded();
        return _flat!;
    }

    public IReadOnlyList<string> Validate()
    {
        EnsureLoaded();
        return _validationErrors!;
    }

    private void EnsureLoaded()
    {
        if (_tree is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_tree is not null)
            {
                return;
            }

            Load();
        }
    }

    private void Load()
    {
        if (!File.Exists(_resolvedPath))
        {
            throw new FileNotFoundException(
                $"Menu XML file was not found at '{_resolvedPath}'. Check Menus:XmlPath.",
                _resolvedPath);
        }

        XDocument document;
        try
        {
            document = XDocument.Load(_resolvedPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Menu XML at '{_resolvedPath}' is not valid XML.", ex);
        }

        var root = document.Root ?? throw new InvalidOperationException("Menu XML root element is missing.");
        var errors = new List<string>();
        var flat = new Dictionary<string, MenuDefinitionNode>(StringComparer.OrdinalIgnoreCase);
        var tree = ParseChildren(root, parentCode: null, flat, errors);

        ValidateStructure(flat, errors);
        DetectCycles(flat, errors);

        _validationErrors = errors;
        _flat = flat;
        _tree = tree;

        if (errors.Count > 0)
        {
            _logger.LogError("Menu XML validation failed with {ErrorCount} error(s): {Errors}",
                errors.Count, string.Join("; ", errors));
        }
        else
        {
            _logger.LogInformation("Loaded menu definitions from {Path} ({Count} menus)", _resolvedPath, flat.Count);
        }
    }

    private static List<MenuDefinitionNode> ParseChildren(
        XElement parent,
        string? parentCode,
        Dictionary<string, MenuDefinitionNode> flat,
        List<string> errors)
    {
        var nodes = new List<MenuDefinitionNode>();

        foreach (var element in parent.Elements("Menu"))
        {
            var code = (element.Attribute("Code")?.Value ?? string.Empty).Trim();
            var name = (element.Attribute("Name")?.Value ?? string.Empty).Trim();
            var route = NullIfEmpty(element.Attribute("Route")?.Value);
            var icon = NullIfEmpty(element.Attribute("Icon")?.Value);
            var sortText = element.Attribute("SortOrder")?.Value;
            var alwaysVisibleText = element.Attribute("AlwaysVisible")?.Value;

            if (string.IsNullOrWhiteSpace(code))
            {
                errors.Add("MenuCode is required.");
                continue;
            }

            if (flat.ContainsKey(code))
            {
                errors.Add($"Duplicate MenuCode: {code}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"Menu '{code}' is missing Name.");
            }

            if (!int.TryParse(sortText, out var sortOrder))
            {
                errors.Add($"Menu '{code}' has invalid SortOrder.");
                sortOrder = 0;
            }

            var alwaysVisible = false;
            if (!string.IsNullOrWhiteSpace(alwaysVisibleText) &&
                !bool.TryParse(alwaysVisibleText, out alwaysVisible))
            {
                errors.Add($"Menu '{code}' has invalid AlwaysVisible.");
            }

            var children = ParseChildren(element, code, flat, errors);
            var node = new MenuDefinitionNode
            {
                Code = code,
                Name = name,
                Route = route,
                Icon = icon,
                SortOrder = sortOrder,
                AlwaysVisible = alwaysVisible,
                ParentCode = parentCode,
                Children = children
            };

            flat[code] = node;
            nodes.Add(node);
        }

        return nodes
            .OrderBy(n => n.SortOrder)
            .ThenBy(n => n.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ValidateStructure(
        Dictionary<string, MenuDefinitionNode> flat,
        List<string> errors)
    {
        var routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in flat.Values)
        {
            if (node.IsGroup)
            {
                if (!string.IsNullOrWhiteSpace(node.Route))
                {
                    errors.Add($"Group menu cannot have Route: {node.Code}");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(node.Route))
                {
                    errors.Add($"Leaf menu requires Route: {node.Code}");
                    continue;
                }

                if (!node.Route.StartsWith('/'))
                {
                    errors.Add($"Leaf menu Route must start with '/': {node.Code}");
                    continue;
                }

                if (routes.TryGetValue(node.Route, out var otherCode))
                {
                    errors.Add($"Duplicate Route: {node.Route} (used by '{otherCode}' and '{node.Code}')");
                }
                else
                {
                    routes[node.Route] = node.Code;
                }
            }
        }
    }

    private static void DetectCycles(Dictionary<string, MenuDefinitionNode> flat, List<string> errors)
    {
        foreach (var code in flat.Keys)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = code;
            while (true)
            {
                if (!seen.Add(current))
                {
                    errors.Add($"Circular menu hierarchy involving '{code}'.");
                    break;
                }

                if (!flat.TryGetValue(current, out var node) || node.ParentCode is null)
                {
                    break;
                }

                if (!flat.ContainsKey(node.ParentCode))
                {
                    errors.Add($"Menu '{current}' references missing parent '{node.ParentCode}'.");
                    break;
                }

                current = node.ParentCode;
            }
        }
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
