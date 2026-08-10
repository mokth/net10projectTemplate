using DevExpress.Blazor;

namespace ErpWeb.UI.Components.Common.DataGrid;

public class GridColumnData
{
    public string Caption { get; set; } = string.Empty;
    public string FieldName { get; set; } = string.Empty;
    public string DataType { get; set; } = "string";
    public string? Width { get; set; }
    public int DecimalPlace { get; set; }
    public int VisibleIndex { get; set; }
    public bool Visible { get; set; } = true;
    public int SortIndex { get; set; } = -1;
    public int GroupIndex { get; set; } = -1;
    public GridColumnSortOrder SortOrder { get; set; } = GridColumnSortOrder.Ascending;
    public string? DisplayFormat { get; set; }
}

public class ButtonInfo
{
    public string Text { get; set; } = string.Empty;
    public string? ToolTip { get; set; }
    public string? Style { get; set; }
    public string? IConClass { get; set; }
    public bool Enabled { get; set; } = true;
}

public class SelectedButtonInfo<T>
{
    public ButtonInfo SelectedButton { get; set; } = new();
    public T? SelectedRow { get; set; }
}

public class SelectedColumnInfo
{
    public string Fieldname { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string? FileGuid { get; set; }
    public object? Context { get; set; }
}

public class GridSummItemInfo
{
    public string FieldName { get; set; } = string.Empty;
    public GridSummaryItemType SummType { get; set; }
    public string? DisplayFormat { get; set; }
}
