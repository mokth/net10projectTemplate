using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ErpWeb.UI.Inventory.Lookups;

public partial class IvStockMasterSearchPopup
{
    private DxGrid? _grid;
    private IReadOnlyList<IvStockMasterLookupRow> _items = [];
    private IvStockMasterLookupRow? _focused;
    private string? _error;
    private bool _loaded;

    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;

    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }
    [Parameter] public EventCallback<IvStockMasterLookupRow> Selected { get; set; }

    protected override async Task OnParametersSetAsync()
    {
        if (Visible && !_loaded)
        {
            await LoadAsync();
        }

        if (!Visible)
        {
            _loaded = false;
            _focused = null;
            _error = null;
        }
    }

    private async Task LoadAsync()
    {
        _error = null;
        var result = await Lookups.SearchStockMastersAsync(new IvStockMasterSearchRequest());
        if (!result.Succeeded)
        {
            _error = result.ErrorMessage ?? "Unable to load items.";
            _items = [];
        }
        else
        {
            _items = result.Items;
        }

        _loaded = true;
    }

    private void OnSelectedItemChanged(object? item)
    {
        _focused = item as IvStockMasterLookupRow;
    }

    private async Task OnRowDoubleClick(GridRowClickEventArgs args)
    {
        if (args.Grid.GetDataItem(args.VisibleIndex) is IvStockMasterLookupRow row)
        {
            await SelectAsync(row);
        }
    }

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await SelectFocusedAsync();
        }
        else if (e.Key == "Escape")
        {
            await CloseAsync();
        }
    }

    private async Task SelectFocusedAsync()
    {
        if (_focused is not null)
        {
            await SelectAsync(_focused);
        }
    }

    private async Task SelectAsync(IvStockMasterLookupRow row)
    {
        if (Selected.HasDelegate)
        {
            await Selected.InvokeAsync(row);
        }

        await CloseAsync();
    }

    private Task OnClosing(PopupClosingEventArgs _) => CloseAsync();

    private async Task CloseAsync()
    {
        Visible = false;
        if (VisibleChanged.HasDelegate)
        {
            await VisibleChanged.InvokeAsync(false);
        }
    }
}
