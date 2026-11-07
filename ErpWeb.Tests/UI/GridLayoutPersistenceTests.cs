using System.Text.Json;
using System.Text.Json.Nodes;
using DevExpress.Blazor;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.Tests.UI;

public sealed class GridLayoutPersistenceTests
{
    [Fact]
    public void Version_two_key_appends_suffix()
    {
        Assert.Equal("inv-misc-receipt-list:v2", GridLayoutPersistence.GetVersionTwoKey("inv-misc-receipt-list"));
    }

    [Fact]
    public void Strip_column_widths_leaves_index_and_field()
    {
        var layout = ParseLayout("""
            {
              "Columns": [
                { "ColumnType": "Data", "FieldName": "BatchNo", "VisibleIndex": 2, "Visible": true, "Width": "165px" }
              ]
            }
            """);

        var stripped = GridLayoutPersistence.StripColumnWidths(layout);
        var column = FirstColumn(stripped);

        Assert.Null(WidthOf(column));
        Assert.Equal("BatchNo", FieldNameOf(column));
        Assert.Equal(2, VisibleIndexOf(column));
    }

    [Fact]
    public void Strip_filter_criteria_clears_filter_only()
    {
        var layout = ParseLayout("""
            {
              "FilterCriteria": "[BatchNo] = 'X'",
              "Columns": [
                { "ColumnType": "Data", "FieldName": "BatchNo", "VisibleIndex": 1, "Width": "165px" }
              ]
            }
            """);

        var stripped = GridLayoutPersistence.StripFilterCriteria(layout);
        var json = JsonSerializer.Serialize(stripped);
        Assert.DoesNotContain("\"FilterCriteria\":{", json.Replace(" ", string.Empty));
        Assert.Equal("165px", WidthOf(FirstColumn(stripped)));
    }

    [Theory]
    [InlineData(true, true, 0, 1)]
    [InlineData(true, false, 0, null)]
    [InlineData(false, true, null, 0)]
    public void Enforce_sets_command_then_selection_indexes(
        bool hasActions,
        bool hasSelection,
        int? commandIndex,
        int? selectionIndex)
    {
        var layout = ParseLayout("""
            {
              "Columns": [
                { "ColumnType": "Selection", "VisibleIndex": 4, "Visible": false, "Width": "60px" },
                { "ColumnType": "Command", "VisibleIndex": 9, "Visible": false },
                { "ColumnType": "Data", "FieldName": "BatchNo", "VisibleIndex": 2, "Visible": true, "Width": "180px" },
                { "ColumnType": "Data", "FieldName": "DocDate", "VisibleIndex": 3, "Visible": false, "Width": "120px" }
              ]
            }
            """);

        var normalized = GridLayoutPersistence.EnforceLeftActionColumns(layout, hasActions, hasSelection);
        Assert.NotNull(normalized);

        var columns = ColumnsOf(normalized);
        var command = columns.FirstOrDefault(c => GridLayoutPersistence.IsColumnType(c, "Command"));
        var selection = columns.FirstOrDefault(c => GridLayoutPersistence.IsColumnType(c, "Selection"));
        var batch = columns.Single(c => FieldNameOf(c) == "BatchNo");
        var date = columns.Single(c => FieldNameOf(c) == "DocDate");

        if (commandIndex is null)
        {
            Assert.Null(command);
        }
        else
        {
            Assert.NotNull(command);
            Assert.Equal(commandIndex, VisibleIndexOf(command));
            Assert.True(VisibleOf(command));
        }

        if (selectionIndex is null)
        {
            Assert.Null(selection);
        }
        else
        {
            Assert.NotNull(selection);
            Assert.Equal(selectionIndex, VisibleIndexOf(selection));
            Assert.True(VisibleOf(selection));
        }

        Assert.Equal(2, VisibleIndexOf(batch));
        Assert.Equal("180px", WidthOf(batch));
        Assert.True(VisibleOf(batch));
        Assert.Equal(3, VisibleIndexOf(date));
        Assert.Equal("120px", WidthOf(date));
        Assert.False(VisibleOf(date));
    }

    [Fact]
    public void Neither_variant_drops_optional_columns_and_keeps_data()
    {
        var layout = ParseLayout("""
            {
              "Columns": [
                { "ColumnType": "Command", "VisibleIndex": 0 },
                { "ColumnType": "Selection", "VisibleIndex": 1 },
                { "ColumnType": "Data", "FieldName": "BatchNo", "VisibleIndex": 2, "Width": "165px" }
              ]
            }
            """);

        var normalized = GridLayoutPersistence.NormalizeLeftActionColumns(layout, hasActions: false, hasSelection: false);
        Assert.NotNull(normalized);
        var columns = ColumnsOf(normalized);
        Assert.DoesNotContain(columns, c => GridLayoutPersistence.IsColumnType(c, "Command"));
        Assert.DoesNotContain(columns, c => GridLayoutPersistence.IsColumnType(c, "Selection"));
        Assert.Equal("165px", WidthOf(columns.Single(c => FieldNameOf(c) == "BatchNo")));
    }

    [Fact]
    public void Missing_optional_columns_are_inserted_in_declaration_order()
    {
        var layout = ParseLayout("""
            {
              "Columns": [
                { "ColumnType": "Data", "FieldName": "BatchNo", "VisibleIndex": 0, "Width": "165px" },
                { "ColumnType": "Data", "FieldName": "Status", "VisibleIndex": 1, "Width": "115px" }
              ]
            }
            """);

        var normalized = GridLayoutPersistence.NormalizeLeftActionColumns(layout, hasActions: true, hasSelection: true);
        Assert.NotNull(normalized);
        var columns = ColumnsOf(normalized);

        Assert.True(GridLayoutPersistence.IsColumnType(columns[0], "Selection"));
        Assert.True(GridLayoutPersistence.IsColumnType(columns[1], "Command"));
        Assert.Equal(1, VisibleIndexOf(columns[0]));
        Assert.Equal(0, VisibleIndexOf(columns[1]));
        Assert.Equal("BatchNo", FieldNameOf(columns[2]));
        Assert.Equal("165px", WidthOf(columns[2]));
        Assert.Equal("Status", FieldNameOf(columns[3]));
        Assert.Equal("115px", WidthOf(columns[3]));
    }

    [Fact]
    public void Hidden_saved_command_is_forced_visible_at_index_zero()
    {
        var layout = ParseLayout("""
            {
              "Columns": [
                { "ColumnType": "Data", "FieldName": "BatchNo", "VisibleIndex": 0, "Width": "200px" },
                { "ColumnType": "Command", "VisibleIndex": 8, "Visible": false }
              ]
            }
            """);

        var normalized = GridLayoutPersistence.NormalizeLeftActionColumns(layout, hasActions: true, hasSelection: false);
        Assert.NotNull(normalized);
        var command = ColumnsOf(normalized).Single(c => GridLayoutPersistence.IsColumnType(c, "Command"));
        Assert.Equal(0, VisibleIndexOf(command));
        Assert.True(VisibleOf(command));
        Assert.Equal("200px", WidthOf(ColumnsOf(normalized).Single(c => FieldNameOf(c) == "BatchNo")));
    }

    [Fact]
    public void Duplicate_command_columns_are_blocked()
    {
        var layout = ParseLayout("""
            {
              "Columns": [
                { "ColumnType": "Command", "VisibleIndex": 0 },
                { "ColumnType": "Command", "VisibleIndex": 1 },
                { "ColumnType": "Data", "FieldName": "BatchNo", "VisibleIndex": 2 }
              ]
            }
            """);

        Assert.Null(GridLayoutPersistence.NormalizeLeftActionColumns(layout, hasActions: true, hasSelection: false));
    }

    [Fact]
    public void Unnamed_column_without_type_is_blocked()
    {
        var layout = ParseLayout("""
            {
              "Columns": [
                { "VisibleIndex": 0 },
                { "ColumnType": "Data", "FieldName": "BatchNo", "VisibleIndex": 1 }
              ]
            }
            """);

        Assert.Null(GridLayoutPersistence.NormalizeLeftActionColumns(layout, hasActions: true, hasSelection: true));
    }

    [Fact]
    public void Cross_variant_actions_only_to_actions_and_selection_keeps_data()
    {
        var saved = ParseLayout("""
            {
              "Columns": [
                { "ColumnType": "Command", "VisibleIndex": 0 },
                { "ColumnType": "Data", "FieldName": "BatchNo", "VisibleIndex": 1, "Width": "190px", "Visible": true },
                { "ColumnType": "Data", "FieldName": "Warehouse", "VisibleIndex": 4, "Width": "160px", "Visible": false }
              ]
            }
            """);

        var loaded = GridLayoutPersistence.NormalizeLeftActionColumns(saved, hasActions: true, hasSelection: true);
        Assert.NotNull(loaded);
        var columns = ColumnsOf(loaded);
        Assert.True(GridLayoutPersistence.IsColumnType(columns[0], "Selection"));
        Assert.True(GridLayoutPersistence.IsColumnType(columns[1], "Command"));
        Assert.Equal("190px", WidthOf(columns.Single(c => FieldNameOf(c) == "BatchNo")));
        Assert.False(VisibleOf(columns.Single(c => FieldNameOf(c) == "Warehouse")));
        Assert.Equal(4, VisibleIndexOf(columns.Single(c => FieldNameOf(c) == "Warehouse")));
    }

    [Fact]
    public void Legacy_strip_then_normalize_keeps_data_and_inserts_actions()
    {
        var layout = ParseLayout("""
            {
              "FilterCriteria": "[BatchNo] = 'X'",
              "Columns": [
                { "ColumnType": "Data", "FieldName": "BatchNo", "VisibleIndex": 2, "Visible": true, "Width": "165px" },
                { "ColumnType": "Data", "FieldName": "Status", "VisibleIndex": 3, "Visible": false, "Width": "90px" }
              ]
            }
            """);

        var migrated = GridLayoutPersistence.StripFilterCriteria(
            GridLayoutPersistence.StripColumnWidths(layout));
        var normalized = GridLayoutPersistence.NormalizeLeftActionColumns(migrated, hasActions: true, hasSelection: true);
        Assert.NotNull(normalized);

        var json = JsonSerializer.Serialize(normalized);
        Assert.DoesNotContain("\"FilterCriteria\":\"[BatchNo] = 'X'\"", json.Replace(" ", string.Empty));

        var columns = ColumnsOf(normalized);
        Assert.True(GridLayoutPersistence.IsColumnType(columns[0], "Selection"));
        Assert.True(GridLayoutPersistence.IsColumnType(columns[1], "Command"));
        Assert.Null(WidthOf(columns.Single(c => FieldNameOf(c) == "BatchNo")));
        Assert.Equal(2, VisibleIndexOf(columns.Single(c => FieldNameOf(c) == "BatchNo")));
        Assert.False(VisibleOf(columns.Single(c => FieldNameOf(c) == "Status")));
    }

    [Fact]
    public void Numeric_column_type_enum_is_recognized()
    {
        var commandNumber = (int)GridPersistentLayoutColumnType.Command;
        var selectionNumber = (int)GridPersistentLayoutColumnType.Selection;
        var dataNumber = (int)GridPersistentLayoutColumnType.Data;
        var layout = ParseLayout($$"""
            {
              "Columns": [
                { "ColumnType": {{selectionNumber}}, "VisibleIndex": 5, "Visible": false },
                { "ColumnType": {{commandNumber}}, "VisibleIndex": 6, "Visible": false },
                { "ColumnType": {{dataNumber}}, "FieldName": "BatchNo", "VisibleIndex": 1, "Width": "165px" }
              ]
            }
            """);

        var normalized = GridLayoutPersistence.NormalizeLeftActionColumns(layout, hasActions: true, hasSelection: true);
        Assert.NotNull(normalized);
        var columns = ColumnsOf(normalized);
        Assert.Equal(0, VisibleIndexOf(columns.Single(c => GridLayoutPersistence.IsColumnType(c, "Command"))));
        Assert.Equal(1, VisibleIndexOf(columns.Single(c => GridLayoutPersistence.IsColumnType(c, "Selection"))));
        Assert.Equal("165px", WidthOf(columns.Single(c => FieldNameOf(c) == "BatchNo")));
    }

    private static GridPersistentLayout ParseLayout(string json) =>
        JsonSerializer.Deserialize<GridPersistentLayout>(json)
        ?? throw new InvalidOperationException("Failed to deserialize layout.");

    private static List<JsonObject> ColumnsOf(GridPersistentLayout layout)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(layout));
        return node?["Columns"] is JsonArray array
            ? array.OfType<JsonObject>().ToList()
            : [];
    }

    private static JsonObject FirstColumn(GridPersistentLayout layout) => ColumnsOf(layout)[0];

    private static string? FieldNameOf(JsonObject column) =>
        column["FieldName"]?.GetValue<string>();

    private static string? WidthOf(JsonObject column) =>
        column.ContainsKey("Width") ? column["Width"]?.GetValue<string>() : null;

    private static int? VisibleIndexOf(JsonObject column) =>
        column["VisibleIndex"]?.GetValue<int>();

    private static bool VisibleOf(JsonObject column) =>
        column["Visible"]?.GetValue<bool>() ?? false;
}
