using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevExpress.Blazor;
using DevExpress.Export;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Components.Common.DataGrid;

public class CommonDataGridExBase<T> : ComponentBase
{
    [Inject] protected IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] protected IGridLayoutStorage LayoutStorage { get; set; } = default!;
    [Inject] protected ILogger<CommonDataGridExBase<T>> Logger { get; set; } = default!;

    [Parameter] public string KeyName { get; set; } = "Id";
    [Parameter] public string? GridKey { get; set; }
    [Parameter] public string Title { get; set; } = "";
    [Parameter] public List<GridColumnData> Columns { get; set; } = new();
    [Parameter] public List<ButtonInfo> Buttons { get; set; } = new();
    [Parameter] public List<ButtonInfo> ActionButtons { get; set; } = new();
    [Parameter] public EventCallback<SelectedButtonInfo<T>> OnButtonEventHandle { get; set; }
    [Parameter] public EventCallback<SelectedButtonInfo<T>> OnActionEventHandle { get; set; }
    [Parameter] public EventCallback<SelectedColumnInfo> OnSelectedColumnHandle { get; set; }
    [Parameter] public EventCallback<T> OnSelectionEventHandle { get; set; }
    [Parameter] public EventCallback<List<T>> OnSelectionsEventHandle { get; set; }
    [Parameter] public EventCallback<DxGrid> OnGridInstance { get; set; }

    [Parameter] public Action<GridCustomizeCellDisplayTextEventArgs>? OnCustomizeCellDisplayTextHandle { get; set; }
    [Parameter] public Func<string, Task<IEnumerable<T>>>? LoadGridData { get; set; }
    [Parameter] public object? GridData { get; set; }
    /// <summary>
    /// When set, binds DxGrid.Data to this custom source (server paging) instead of GridData.
    /// </summary>
    [Parameter] public GridCustomDataSource? CustomDataSource { get; set; }
    /// <summary>When true, toolbar items show Text beside icons.</summary>
    [Parameter] public bool ShowToolbarText { get; set; }
    /// <summary>When false, EXPORT toolbar clicks raise OnButtonEventHandle instead of XLSX export.</summary>
    [Parameter] public bool UseBuiltInExport { get; set; } = true;
    [Parameter] public bool ShowResetLayoutButton { get; set; }

    [Parameter] public bool allowSelect { get; set; }
    [Parameter] public bool allowCustomizeCell { get; set; }
    [Parameter] public List<GridSummItemInfo>? GroupSummaryItems { get; set; }
    [Parameter] public bool ShowFilterRow { get; set; } = true;
    [Parameter] public bool ShowGroupPanel { get; set; } = true;
    [Parameter] public bool ShowColChoose { get; set; } = true;
    [Parameter] public bool ShowSearchBox { get; set; } = true;
    [Parameter] public bool ShowTotalSummary { get; set; }
    [Parameter] public bool PersistLayout { get; set; } = true;

    [Parameter] public GridSelectionMode GridSelectionMode { get; set; } = GridSelectionMode.Multiple;
    [Parameter] public GridGroupFooterDisplayMode GroupFooterDisplayMode { get; set; } = GridGroupFooterDisplayMode.Auto;

    public DxGrid? grid;

    public T? SelectedRow { get; set; }
    public object? SelectedDataItem { get; set; }
    protected bool PreRendered { get; set; }
    protected bool IsLoading { get; set; }
    protected GridSelectionMode SelectionMode { get; set; }

    protected object? BoundData => CustomDataSource is not null ? CustomDataSource : GridData;
    protected bool UsesCustomData => CustomDataSource is not null;
    protected bool EffectiveShowFilterRow => !UsesCustomData && ShowFilterRow;
    protected bool EffectiveShowGroupPanel => !UsesCustomData && ShowGroupPanel;
    protected bool EffectiveShowSearchBox => !UsesCustomData && ShowSearchBox;
    protected bool PageSizeSelectorAllRowsVisible => !UsesCustomData;

    private bool _gridInstanceFired;
    private string? LayoutKey =>
        !string.IsNullOrWhiteSpace(GridKey)
            ? GridKey
            : (string.IsNullOrWhiteSpace(Title) ? null : Title.Replace(" ", string.Empty));

    protected override void OnInitialized()
    {
        base.OnInitialized();
        SelectionMode = allowSelect ? GridSelectionMode.Multiple : GridSelectionMode.Single;
        if (!string.IsNullOrWhiteSpace(Title))
        {
            Title = Title.ToUpperInvariant();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            PreRendered = true;
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (_gridInstanceFired || grid is null || !OnGridInstance.HasDelegate)
        {
            return;
        }

        _gridInstanceFired = true;
        await OnGridInstance.InvokeAsync(grid);
    }

    public async Task<IEnumerable<T>> LoadDataAsync(CancellationToken token)
    {
        if (LoadGridData is null)
        {
            return Array.Empty<T>();
        }

        return await LoadGridData.Invoke("a");
    }

    public ButtonRenderStyle GetGridButtinRenderStyle(string? style)
    {
        return (style ?? string.Empty).ToLowerInvariant() switch
        {
            "danger" => ButtonRenderStyle.Danger,
            "info" => ButtonRenderStyle.Info,
            "warning" => ButtonRenderStyle.Warning,
            "success" => ButtonRenderStyle.Success,
            "dark" => ButtonRenderStyle.Dark,
            _ => ButtonRenderStyle.Primary
        };
    }

    public string GetGridActionColWidth()
    {
        var count = Math.Max(ActionButtons?.Count ?? 0, 1);
        return $"{count * 35}px";
    }

    public async Task OnRefreshClick()
    {
        if (!OnButtonEventHandle.HasDelegate)
        {
            return;
        }

        await OnButtonEventHandle.InvokeAsync(new SelectedButtonInfo<T>
        {
            SelectedButton = new ButtonInfo { Text = "REFRESH" },
            SelectedRow = SelectedRow
        });
    }

    public async Task OnButtonClick(ButtonInfo btn)
    {
        if (string.Equals(btn.Text, "EXPORT", StringComparison.OrdinalIgnoreCase) && UseBuiltInExport)
        {
            if (IsLoading)
            {
                return;
            }

            IsLoading = true;
            await InvokeAsync(StateHasChanged);

            try
            {
                await ExportReport();
            }
            finally
            {
                IsLoading = false;
                await InvokeAsync(StateHasChanged);
            }

            return;
        }

        if (string.Equals(btn.Text, "RESET LAYOUT", StringComparison.OrdinalIgnoreCase))
        {
            await ResetLayoutAsync();
            return;
        }

        if (!OnButtonEventHandle.HasDelegate)
        {
            return;
        }

        await OnButtonEventHandle.InvokeAsync(new SelectedButtonInfo<T>
        {
            SelectedButton = btn,
            SelectedRow = SelectedRow
        });
    }

    public async Task ResetLayoutAsync()
    {
        if (!string.IsNullOrWhiteSpace(LayoutKey))
        {
            await LayoutStorage.ClearAsync(LayoutKey);
        }

        // Remount so LayoutAutoLoading applies defaults (storage cleared).
        _gridInstanceFired = false;
        PreRendered = false;
        await InvokeAsync(StateHasChanged);
        PreRendered = true;
        await InvokeAsync(StateHasChanged);
    }

    public async Task OnEditClick(GridCommandColumnCellDisplayTemplateContext context, ButtonInfo btn)
    {
        if (context?.DataItem is null)
        {
            Logger.LogWarning("No row selected for grid action {Action}", btn.Text);
            return;
        }

        var row = UnwrapRow(context.DataItem);
        if (row is null)
        {
            return;
        }

        SelectedRow = row;
        SelectedDataItem = row;

        if (!OnActionEventHandle.HasDelegate)
        {
            return;
        }

        await OnActionEventHandle.InvokeAsync(new SelectedButtonInfo<T>
        {
            SelectedButton = btn,
            SelectedRow = row
        });
    }

    protected async Task OnSelectionChanged(object selected)
    {
        SelectedDataItem = selected;
        var row = UnwrapRow(selected);
        if (row is null)
        {
            return;
        }

        SelectedRow = row;
        if (OnSelectionEventHandle.HasDelegate)
        {
            await OnSelectionEventHandle.InvokeAsync(row);
        }
    }

    protected async Task OnSelectedDataItemsChanged(IReadOnlyList<object> newSelection)
    {
        var list = newSelection
            .Select(UnwrapRow)
            .Where(row => row is not null)
            .Cast<T>()
            .ToList();

        if (OnSelectionsEventHandle.HasDelegate)
        {
            await OnSelectionsEventHandle.InvokeAsync(list);
        }
    }

    protected Task OnEditModelSaving(GridEditModelSavingEventArgs e)
    {
        Logger.LogDebug("OnEditModelSaving");
        return Task.CompletedTask;
    }

    private async Task ExportReport()
    {
        if (grid is null)
        {
            return;
        }

        var xlsFilename = $"{typeof(T).Name}_{DateTime.Now:yyMMddHHmmss}.xlsx";
        await grid.ExportToXlsxAsync(xlsFilename, new GridXlExportOptions
        {
            CustomizeCell = OnCustomizeCell
        });
    }

    private static void OnCustomizeCell(GridExportCustomizeCellEventArgs args)
    {
        if (args.ColumnFieldName == "ContactName" && args.AreaType == SheetAreaType.DataArea)
        {
            args.Formatting.Font = new XlCellFont { Italic = true };
        }

        args.Handled = true;
    }

    protected void OnColChoose()
    {
        grid?.ShowColumnChooser();
    }

    protected async Task OnItemLinkClick(GridDataColumnCellDisplayTemplateContext context, string fieldName)
    {
        if (!OnSelectedColumnHandle.HasDelegate || context.DataItem is null)
        {
            return;
        }

        var dataItem = UnwrapDataItem(context.DataItem) ?? context.DataItem;
        var info = new SelectedColumnInfo
        {
            Fieldname = fieldName,
            Value = context.Value?.ToString(),
            FileGuid = TryGetStringProperty(dataItem, "FileGuid"),
            Context = dataItem
        };

        await OnSelectedColumnHandle.InvokeAsync(info);
    }

    protected IEnumerable<KeyValuePair<string, object>> GetLinkAttributes(
        GridDataColumnCellDisplayTemplateContext context,
        string fieldName)
    {
        var callback = EventCallback.Factory.Create(this, () => OnItemLinkClick(context, fieldName));
        yield return new KeyValuePair<string, object>("ontouchstart", callback);
        yield return new KeyValuePair<string, object>("onmouseover", callback);
    }

    protected IEnumerable<KeyValuePair<string, object>> GetLinkAttributes2(
        GridDataColumnCellDisplayTemplateContext context,
        string fieldName)
    {
        var callback = EventCallback.Factory.Create(this, () => OnItemLinkClick(context, fieldName));
        yield return new KeyValuePair<string, object>("onmousedown", callback);
    }

    protected string GetIConClss(GridDataColumnCellDisplayTemplateContext context, string fieldName, string val)
    {
        _ = context;
        if (fieldName.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return val.ToLowerInvariant() switch
            {
                "approved" => "fa-regular fa-circle-check text-success",
                "rejected" => "fa-solid fa-ban text-danger",
                "pending" => "fa-regular fa-clock text-warning",
                "cancelled" => "fa-solid fa-circle-xmark text-secondary",
                _ => string.Empty
            };
        }

        if (fieldName.Equals("leavetypeid", StringComparison.OrdinalIgnoreCase) && val.Contains("(EL)", StringComparison.OrdinalIgnoreCase))
        {
            return "fa-solid fa-flag text-danger";
        }

        return string.Empty;
    }

    public string Base64Encode(string plainText) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

    protected async Task Grid_LayoutAutoLoading(GridPersistentLayoutEventArgs e)
    {
        if (!PersistLayout || string.IsNullOrWhiteSpace(LayoutKey))
        {
            return;
        }

        try
        {
            var layout = await LayoutStorage.LoadAsync(LayoutKey);
            if (layout is not null)
            {
                // Keep sort/visibility/order, but ignore saved widths so a stale layout
                // cannot shrink the grid to a fraction of the page.
                e.Layout = StripColumnWidths(layout);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load grid layout for {LayoutKey}", LayoutKey);
        }
    }

    protected async Task Grid_LayoutAutoSaving(GridPersistentLayoutEventArgs e)
    {
        if (!PersistLayout || string.IsNullOrWhiteSpace(LayoutKey) || e.Layout is null)
        {
            return;
        }

        try
        {
            var layout = StripFilterCriteria(e.Layout);
            await LayoutStorage.SaveAsync(LayoutKey, layout);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to save grid layout for {LayoutKey}", LayoutKey);
        }
    }

    private static T? UnwrapRow(object? dataItem)
    {
        var unwrapped = UnwrapDataItem(dataItem);
        return unwrapped is T row ? row : default;
    }

    private static object? UnwrapDataItem(object? dataItem)
    {
        if (dataItem is null)
        {
            return null;
        }

        if (dataItem is DevExpress.Data.Async.Helpers.ReadonlyThreadSafeProxyForObjectFromAnotherThread proxy)
        {
            return proxy.OriginalRow;
        }

        return dataItem;
    }

    private static string? TryGetStringProperty(object dataItem, string propertyName)
    {
        var property = dataItem.GetType().GetProperty(propertyName);
        return property?.GetValue(dataItem)?.ToString();
    }

    private static GridPersistentLayout StripFilterCriteria(GridPersistentLayout layout)
    {
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

    private static GridPersistentLayout StripColumnWidths(GridPersistentLayout layout)
    {
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
}
