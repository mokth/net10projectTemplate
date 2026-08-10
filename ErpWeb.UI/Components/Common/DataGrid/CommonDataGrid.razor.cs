using DevExpress.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace ErpWeb.UI.Components.Common.DataGrid;

public partial class CommonDataGrid<T>
{
    private DxGrid? _grid;

    [Inject]
    private IGridLayoutStorage LayoutStorage { get; set; } = default!;

    [Inject]
    private ILogger<CommonDataGrid<T>> Logger { get; set; } = default!;

    [Parameter] public string GridKey { get; set; } = string.Empty;
    [Parameter] public string? Title { get; set; }
    [Parameter] public string KeyFieldName { get; set; } = "Id";
    [Parameter] public IEnumerable<T>? Data { get; set; }
    [Parameter] public IReadOnlyList<GridColumnDefinition> Columns { get; set; } = Array.Empty<GridColumnDefinition>();
    [Parameter] public IReadOnlyList<GridToolbarButton> ToolbarButtons { get; set; } = Array.Empty<GridToolbarButton>();
    [Parameter] public IReadOnlyList<GridRowAction> RowActions { get; set; } = Array.Empty<GridRowAction>();
    [Parameter] public IReadOnlyList<GridSummaryDefinition> TotalSummaries { get; set; } = Array.Empty<GridSummaryDefinition>();
    [Parameter] public IReadOnlyList<GridSummaryDefinition> GroupSummaries { get; set; } = Array.Empty<GridSummaryDefinition>();
    [Parameter] public bool ShowSearchBox { get; set; } = true;
    [Parameter] public bool ShowFilterRow { get; set; } = true;
    [Parameter] public bool ShowGroupPanel { get; set; } = true;
    [Parameter] public bool AllowSelection { get; set; }
    [Parameter] public bool AllowMultipleSelection { get; set; }
    [Parameter] public bool AllowExport { get; set; } = true;
    [Parameter] public bool ShowColumnChooser { get; set; } = true;
    [Parameter] public bool PersistLayout { get; set; } = true;
    [Parameter] public RenderFragment? ToolbarContent { get; set; }
    [Parameter] public EventCallback<GridToolbarClickEventArgs> OnToolbarButtonClick { get; set; }
    [Parameter] public EventCallback<GridRowActionEventArgs<T>> OnRowActionClick { get; set; }
    [Parameter] public EventCallback<T> OnSelectedItemChanged { get; set; }
    [Parameter] public EventCallback<IReadOnlyList<T>> OnSelectedItemsChanged { get; set; }

    private GridSelectionMode SelectionMode =>
        AllowMultipleSelection ? GridSelectionMode.Multiple : GridSelectionMode.Single;

    private string GetActionColumnWidth() => $"{Math.Max(RowActions.Count, 1) * 36}px";

    private async Task OnToolbarClickAsync(GridToolbarButton button)
    {
        if (OnToolbarButtonClick.HasDelegate)
        {
            await OnToolbarButtonClick.InvokeAsync(new GridToolbarClickEventArgs { Button = button });
        }
    }

    private async Task OnRowActionAsync(GridCommandColumnCellDisplayTemplateContext context, GridRowAction action)
    {
        if (!OnRowActionClick.HasDelegate || context.DataItem is not T row)
        {
            return;
        }

        await OnRowActionClick.InvokeAsync(new GridRowActionEventArgs<T>
        {
            Action = action,
            Row = row
        });
    }

    private async Task HandleSelectedItemChanged(object selected)
    {
        if (OnSelectedItemChanged.HasDelegate && selected is T row)
        {
            await OnSelectedItemChanged.InvokeAsync(row);
        }
    }

    private async Task HandleSelectedItemsChanged(IReadOnlyList<object> selected)
    {
        if (!OnSelectedItemsChanged.HasDelegate)
        {
            return;
        }

        var rows = selected.OfType<T>().ToList();
        await OnSelectedItemsChanged.InvokeAsync(rows);
    }

    private async Task ExportAsync()
    {
        if (_grid is null)
        {
            return;
        }

        var fileName = $"{(string.IsNullOrWhiteSpace(GridKey) ? typeof(T).Name : GridKey)}_{DateTime.Now:yyMMddHHmmss}.xlsx";
        await _grid.ExportToXlsxAsync(fileName);
    }

    private void ShowChooser()
    {
        _grid?.ShowColumnChooser();
    }

    private async Task OnLayoutLoadingAsync(GridPersistentLayoutEventArgs e)
    {
        if (!PersistLayout || string.IsNullOrWhiteSpace(GridKey))
        {
            return;
        }

        var layout = await LayoutStorage.LoadAsync(GridKey);
        if (layout is not null)
        {
            e.Layout = layout;
        }
    }

    private async Task OnLayoutSavingAsync(GridPersistentLayoutEventArgs e)
    {
        if (!PersistLayout || string.IsNullOrWhiteSpace(GridKey) || e.Layout is null)
        {
            return;
        }

        await LayoutStorage.SaveAsync(GridKey, e.Layout);
    }
}
