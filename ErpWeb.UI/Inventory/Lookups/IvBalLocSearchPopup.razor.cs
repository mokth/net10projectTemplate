using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ErpWeb.UI.Inventory.Lookups;

public partial class IvBalLocSearchPopup
{
    private const int _pageSize = 20;
    private DxGrid? _grid;
    private IReadOnlyList<IvBalLocLookupRow> _items = [];
    private IvBalLocLookupRow? _focused;
    private string? _error;
    private string? _searchText;
    private int _skip;
    private int _totalCount;
    private bool _loaded;

    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;

    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }
    [Parameter] public EventCallback<IvBalLocLookupRow> Selected { get; set; }
    [Parameter] public string? ICodeFilter { get; set; }

    protected override async Task OnParametersSetAsync()
    {
        if (Visible && !_loaded)
        {
            _skip = 0;
            await LoadAsync();
        }

        if (!Visible)
        {
            _loaded = false;
            _focused = null;
            _error = null;
        }
    }

    private Task SearchAsync()
    {
        _skip = 0;
        return LoadAsync();
    }

    private Task PrevPageAsync()
    {
        _skip = Math.Max(0, _skip - _pageSize);
        return LoadAsync();
    }

    private Task NextPageAsync()
    {
        _skip += _pageSize;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        _error = null;
        var result = await Lookups.SearchOnHandAsync(new IvOnHandSearchRequest
        {
            ICode = ICodeFilter,
            SearchText = _searchText,
            Skip = _skip,
            Take = _pageSize
        });

        if (!result.Succeeded)
        {
            _error = result.ErrorMessage ?? "Unable to load on-hand balances.";
            _items = [];
            _totalCount = 0;
        }
        else
        {
            _items = result.Rows;
            _totalCount = result.TotalCount;
        }

        _loaded = true;
    }

    private void OnSelectedItemChanged(object? item)
    {
        _focused = item as IvBalLocLookupRow;
    }

    private async Task OnRowDoubleClick(GridRowClickEventArgs args)
    {
        if (args.Grid.GetDataItem(args.VisibleIndex) is IvBalLocLookupRow row)
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

    private async Task SelectAsync(IvBalLocLookupRow row)
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
