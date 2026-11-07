using System.Text.Json;
using System.Text.Json.Nodes;
using DevExpress.Blazor;

namespace ErpWeb.UI.Components.Common.DataGrid;

internal static class GridLayoutPersistence
{
    public const string VersionTwoSuffix = ":v2";
    public const string CommandColumnType = "Command";
    public const string SelectionColumnType = "Selection";
    public const string DataColumnType = "Data";
    public const string BandColumnType = "Band";

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

    /// <summary>
    /// Aligns optional Command/Selection membership and left-fixed VisibleIndex values
    /// with the columns currently rendered. Returns null when the saved schema cannot be
    /// reconciled without touching data/band columns.
    /// </summary>
    public static GridPersistentLayout? NormalizeLeftActionColumns(
        GridPersistentLayout layout,
        bool hasActions,
        bool hasSelection)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var node = JsonNode.Parse(JsonSerializer.Serialize(layout));
        if (node is not JsonObject)
        {
            return layout;
        }

        if (node["Columns"] is not JsonArray columns)
        {
            return hasActions || hasSelection ? null : layout;
        }

        if (!TryClassify(columns, out var command, out var selection, out var others))
        {
            return null;
        }

        if (command.Count > 1 || selection.Count > 1)
        {
            return null;
        }

        JsonObject? commandColumn = command.Count == 1 ? command[0] : null;
        JsonObject? selectionColumn = selection.Count == 1 ? selection[0] : null;

        if (hasActions && commandColumn is null)
        {
            commandColumn = CreateOptionalColumn(columns, CommandColumnType);
        }

        if (hasSelection && selectionColumn is null)
        {
            selectionColumn = CreateOptionalColumn(columns, SelectionColumnType);
        }

        if (!hasActions)
        {
            commandColumn = null;
        }

        if (!hasSelection)
        {
            selectionColumn = null;
        }

        var rebuilt = new JsonArray();
        // Keep the Razor declaration sequence: Selection then Command.
        if (selectionColumn is not null)
        {
            rebuilt.Add(selectionColumn.DeepClone());
        }

        if (commandColumn is not null)
        {
            rebuilt.Add(commandColumn.DeepClone());
        }

        foreach (var other in others)
        {
            rebuilt.Add(other.DeepClone());
        }

        ApplyVisibleIndexes(rebuilt, hasActions, hasSelection);
        node["Columns"] = rebuilt;
        return JsonSerializer.Deserialize<GridPersistentLayout>(node.ToJsonString());
    }

    /// <summary>
    /// Index-only correction for a layout that already has the expected optional columns.
    /// </summary>
    public static GridPersistentLayout EnforceLeftActionColumns(
        GridPersistentLayout layout,
        bool hasActions,
        bool hasSelection)
    {
        return NormalizeLeftActionColumns(layout, hasActions, hasSelection) ?? layout;
    }

    internal static bool IsColumnType(JsonObject column, string typeName)
    {
        if (column["ColumnType"] is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return string.Equals(text, typeName, StringComparison.OrdinalIgnoreCase);
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number == ToColumnTypeNumber(typeName);
        }

        if (value.TryGetValue<long>(out var longNumber))
        {
            return longNumber == ToColumnTypeNumber(typeName);
        }

        return false;
    }

    private static bool TryClassify(
        JsonArray columns,
        out List<JsonObject> command,
        out List<JsonObject> selection,
        out List<JsonObject> others)
    {
        command = [];
        selection = [];
        others = [];

        foreach (var item in columns)
        {
            if (item is not JsonObject column)
            {
                return false;
            }

            if (IsColumnType(column, CommandColumnType))
            {
                command.Add(column);
                continue;
            }

            if (IsColumnType(column, SelectionColumnType))
            {
                selection.Add(column);
                continue;
            }

            var hasFieldName = column["FieldName"] is JsonValue field
                && field.TryGetValue<string>(out var name)
                && !string.IsNullOrWhiteSpace(name);
            var hasKnownType = IsColumnType(column, DataColumnType) || IsColumnType(column, BandColumnType);

            if (!hasFieldName && !hasKnownType)
            {
                return false;
            }

            others.Add(column);
        }

        return true;
    }

    private static void ApplyVisibleIndexes(JsonArray columns, bool hasActions, bool hasSelection)
    {
        foreach (var column in columns.OfType<JsonObject>())
        {
            if (IsColumnType(column, CommandColumnType))
            {
                column["VisibleIndex"] = 0;
                column["Visible"] = true;
            }
            else if (IsColumnType(column, SelectionColumnType))
            {
                column["VisibleIndex"] = hasActions ? 1 : 0;
                column["Visible"] = true;
            }
        }
    }

    private static JsonObject CreateOptionalColumn(JsonArray existing, string typeName)
    {
        var created = new JsonObject
        {
            ["ColumnType"] = CreateTypeValue(existing, typeName),
            ["Visible"] = true
        };
        return created;
    }

    private static JsonNode CreateTypeValue(JsonArray existing, string typeName)
    {
        foreach (var column in existing.OfType<JsonObject>())
        {
            if (column["ColumnType"] is JsonValue value)
            {
                if (value.TryGetValue<int>(out _) || value.TryGetValue<long>(out _))
                {
                    return JsonValue.Create(ToColumnTypeNumber(typeName));
                }

                if (value.TryGetValue<string>(out _))
                {
                    return JsonValue.Create(typeName);
                }
            }
        }

        return JsonValue.Create(typeName);
    }

    private static int ToColumnTypeNumber(string typeName) =>
        (int)Enum.Parse<GridPersistentLayoutColumnType>(typeName, ignoreCase: true);
}
