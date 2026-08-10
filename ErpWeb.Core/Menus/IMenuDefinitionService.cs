namespace ErpWeb.Core.Menus;

public interface IMenuDefinitionService
{
    IReadOnlyList<MenuDefinitionNode> GetTree();
    IReadOnlyDictionary<string, MenuDefinitionNode> GetFlatByCode();
    IReadOnlyList<string> Validate();
}
