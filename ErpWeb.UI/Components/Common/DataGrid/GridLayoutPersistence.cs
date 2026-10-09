using System.Text.Json;
using System.Text.Json.Nodes;
using DevExpress.Blazor;

namespace ErpWeb.UI.Components.Common.DataGrid;

internal static class GridLayoutPersistence
{
    public const string VersionTwoSuffix = ":v2";

    public static string GetVersionTwoKey(string gridKey) => $"{gridKey}{VersionTwoSuffix}";

    public static GridPersistentLayout StripColumnWidths(GridPersistentLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var node = JsonNode.Parse(JsonSerializer.Serialize(layout));
        if (node is null)
        {
            return layout;
        }

        if (node["Columns"] is JsonArray columns)
        {
            foreach (var column in columns.OfType<JsonObject>())
            {
                column.Remove("Width");
            }
        }

        return JsonSerializer.Deserialize<GridPersistentLayout>(node.ToJsonString()) ?? layout;
    }

    public static GridPersistentLayout StripFilterCriteria(GridPersistentLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (layout.FilterCriteria is null)
        {
            return layout;
        }

        var node = JsonNode.Parse(JsonSerializer.Serialize(layout));
        if (node is null)
        {
            return layout;
        }

        node["FilterCriteria"] = null;
        return JsonSerializer.Deserialize<GridPersistentLayout>(node.ToJsonString()) ?? layout;
    }
}
