namespace ErpWeb.Core.Menus;

public sealed record MenuNavItem
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Route { get; init; }
    public string? Icon { get; init; }
    public int SortOrder { get; init; }
    public bool AlwaysVisible { get; init; }
    public IReadOnlyList<MenuNavItem> Children { get; init; } = Array.Empty<MenuNavItem>();
}
