namespace ErpWeb.Core.Menus;

public sealed class MenuDefinitionNode
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Route { get; init; }
    public string? Icon { get; init; }
    public int SortOrder { get; init; }
    public bool AlwaysVisible { get; init; }
    public string? ParentCode { get; init; }
    public IReadOnlyList<MenuDefinitionNode> Children { get; init; } = Array.Empty<MenuDefinitionNode>();

    public bool IsGroup => Children.Count > 0;
    public bool IsLeaf => Children.Count == 0;
}
