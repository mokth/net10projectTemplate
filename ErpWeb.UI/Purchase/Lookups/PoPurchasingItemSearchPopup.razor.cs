using DevExpress.Blazor;
using ErpWeb.Core.Lookups;
using ErpWeb.Core.Purchase;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace ErpWeb.UI.Purchase.Lookups;

public partial class PoPurchasingItemSearchPopup
{
    private const int _pageSize = LargeLookupSearchRequest.DefaultPageSize;
    private DxGrid? _grid;
    private IReadOnlyList<PoPurchasingItemLookupRow> _items = [];
    private PoPurchasingItemLookupRow? _focused;
    private string? _error;
    private string? _searchText;
    private int _skip;
    private int _totalCount;
    private bool _loaded;
    private bool _loading;
    private string? _appliedInitialSearch;
    private CancellationTokenSource? _loadCts;
    private long _loadSequence;

    [Inject] private IPoPurchasingItemLookupService Lookups { get; set; } = default!;

    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }
    [Parameter] public EventCallback<PoPurchasingItemLookupRow> Selected { get; set; }
    [Parameter] public string? InitialSearchText { get; set; }
    [Parameter] public bool IncludeIndirect { get; set; } = true;
    [Parameter] public string? MenuCode { get; set; }
    [Parameter] public bool ShowUnitPrice { get; set; }

    protected override async Task OnParametersSetAsync()
    {
        if (Visible && !_loaded)
        {
            _skip = 0;
            _searchText = InitialSearchText?.Trim();
            _appliedInitialSearch = _searchText;
            await LoadAsync();
        }
        else if (Visible
                 && _loaded
                 && !string.Equals(InitialSearchText?.Trim(), _appliedInitialSearch, StringComparison.Ordinal))
        {
            _skip = 0;
            _searchText = InitialSearchText?.Trim();
            _appliedInitialSearch = _searchText;
            await LoadAsync();
        }

        if (!Visible)
        {
            CancelLoad();
            _loaded = false;
            _focused = null;
            _error = null;
            _appliedInitialSearch = null;
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

    private async Task OnSearchKeyDownAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            await SearchAsync();
        }
    }

    private async Task LoadAsync()
    {
        CancelLoad();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var sequence = Interlocked.Increment(ref _loadSequence);

        _loading = true;
        _error = null;

        try
        {
            var result = await Lookups.SearchPagedAsync(
                new LargeLookupSearchRequest
                {
                    SearchText = _searchText,
                    Skip = _skip,
                    Take = _pageSize
                },
                MenuCode,
                IncludeIndirect,
                cts.Token);

            if (cts.IsCancellationRequested || sequence != _loadSequence)
            {
                return;
            }

            if (!result.Succeeded)
            {
                _error = result.ErrorMessage ?? "Unable to load items.";
                _items = [];
                _totalCount = 0;
            }
            else
            {
                _items = result.Rows;
                _totalCount = result.TotalCount;
                _focused = null;
            }

            _loaded = true;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (sequence == _loadSequence)
            {
                _loading = false;
            }
        }
    }

    private void OnSelectedItemChanged(object? item) =>
        _focused = item as PoPurchasingItemLookupRow;

    private async Task OnRowDoubleClick(GridRowClickEventArgs args)
    {
        if (args.Grid.GetDataItem(args.VisibleIndex) is PoPurchasingItemLookupRow row)
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

    private async Task SelectAsync(PoPurchasingItemLookupRow row)
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
        CancelLoad();
        Visible = false;
        if (VisibleChanged.HasDelegate)
        {
            await VisibleChanged.InvokeAsync(false);
        }
    }

    private void CancelLoad()
    {
        try
        {
            _loadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _loadCts?.Dispose();
        _loadCts = null;
    }
}
