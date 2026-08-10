namespace ErpWeb.Core.Menus;

public sealed class MenusOptions
{
    public const string SectionName = "Menus";

    public string XmlPath { get; set; } = "Menus/menus.xml";

    public bool SyncOnStartup { get; set; }
}
