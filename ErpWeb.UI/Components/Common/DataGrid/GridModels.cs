using DevExpress.Blazor;

namespace ErpWeb.UI.Components.Common.DataGrid;

public class GridColumnDefinition
{
    public string FieldName { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
    public string DataType { get; set; } = "string";
    public string? Width { get; set; }
    public string? DisplayFormat { get; set; }
    public int VisibleIndex { get; set; }
    public int SortIndex { get; set; } = -1;
    public int GroupIndex { get; set; } = -1;
    public GridColumnSortOrder SortOrder { get; set; } = GridColumnSortOrder.Ascending;
}

public class GridToolbarButton
{
    public string Text { get; set; } = string.Empty;
    public string? ToolTip { get; set; }
    public string? IconCssClass { get; set; }
    public bool Enabled { get; set; } = true;
}

public class GridRowAction
{
    public string Text { get; set; } = string.Empty;
    public string? ToolTip { get; set; }
    public string? IconCssClass { get; set; }
}

public class GridSummaryDefinition
{
    public string FieldName { get; set; } = string.Empty;
    public GridSummaryItemType SummaryType { get; set; } = GridSummaryItemType.Sum;
    public string? DisplayFormat { get; set; }
}

public class GridToolbarClickEventArgs
{
    public required GridToolbarButton Button { get; init; }
}

public class GridRowActionEventArgs<T>
{
    public required GridRowAction Action { get; init; }
    public required T Row { get; init; }
}
