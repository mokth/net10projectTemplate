using System.Timers;
using ErpWeb.Core.Subcon;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Subcon;

public partial class SubconInquiry : PageBase, IDisposable
{
    [Inject] private ISubconApplicationService Subcon { get; set; } = default!;

    private Timer? _searchDebounce;

    protected CommonDataGridEx<SubconListRow>? Grid;
    protected bool IsBootstrapping = true;
    protected int TotalCount;
    protected string SearchText = string.Empty;
    protected bool FilterPopupVisible;
    protected string? StatusFilter;
    protected DateTime? DateFromFilter;
    protected DateTime? DateToFilter;
    protected string? DraftStatusFilter;
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;
    protected string[] StatusFilterOptions { get; } = [SubconStatuses.New, SubconStatuses.Cancelled];
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(StatusFilter)
        || DateFromFilter.HasValue
        || DateToFilter.HasValue;
    protected IReadOnlyList<SubconListRow> CompactRows { get; private set; } = [];
    protected SubconWorkbenchGridDataSource DataSource { get; private set; } = default!;
    protected string TotalCountLabel => TotalCount == 1 ? "1 document" : $"{TotalCount:N0} documents";

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Document", FieldName = nameof(SubconListRow.DocNo), SortIndex = 0, VisibleIndex = 1, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Date", FieldName = nameof(SubconListRow.TrxDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", VisibleIndex = 2, Size = GridColumnSize.Date },
        new() { Caption = "Supplier", FieldName = nameof(SubconListRow.SupplierName), VisibleIndex = 3, Size = GridColumnSize.Name },
        new() { Caption = "Item", FieldName = nameof(SubconListRow.ItemSummary), VisibleIndex = 4, Size = GridColumnSize.Code },
        new() { Caption = "Qty", FieldName = nameof(SubconListRow.SentQty), DataType = "decimal", DisplayFormat = "n4", VisibleIndex = 5, Size = GridColumnSize.Quantity },
        new() { Caption = "UOM", FieldName = nameof(SubconListRow.Uom), VisibleIndex = 6, Size = GridColumnSize.Tiny },
        new() { Caption = "Due", FieldName = nameof(SubconListRow.PromisedReturnDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", VisibleIndex = 7, Size = GridColumnSize.Date },
        new() { Caption = "Status", FieldName = nameof(SubconListRow.Status), VisibleIndex = 8, Size = GridColumnSize.Status }
    ];

    protected List<ButtonInfo> Buttons { get; } = [];
    protected List<ButtonInfo> ActionButtons { get; } =
    [
        new() { Text = "View", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View send-out" }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new SubconWorkbenchGridDataSource(SearchAsync);
        var page = await Subcon.SearchAsync(new SubconListQuery { Take = 20 });
        if (!page.Succeeded)
        {
            ErrorMessage = page.ErrorMessage;
        }

        TotalCount = page.ListPage?.TotalCount ?? 0;
        CompactRows = page.ListPage?.Rows ?? [];
        IsBootstrapping = false;
    }

    protected void OnSearchTextChanged(string value)
    {
        SearchText = value;
        _searchDebounce?.Dispose();
        _searchDebounce = new Timer(300) { AutoReset = false };
        _searchDebounce.Elapsed += async (_, _) => await InvokeAsync(ApplySearchAsync);
        _searchDebounce.Start();
    }

    protected async Task ApplySearchAsync()
    {
        _searchDebounce?.Stop();
        var page = await Subcon.SearchAsync(new SubconListQuery
        {
            SearchText = SearchText,
            Status = StatusFilter,
            DateFrom = DateFromFilter,
            DateTo = DateToFilter,
            Take = 20
        });
        if (!page.Succeeded)
        {
            ErrorMessage = page.ErrorMessage;
        }

        TotalCount = page.ListPage?.TotalCount ?? 0;
        CompactRows = page.ListPage?.Rows ?? [];
        if (Grid is not null)
        {
            await Grid.OnRefreshClick();
        }

        StateHasChanged();
    }

    protected void OpenFilterPopup()
    {
        DraftStatusFilter = StatusFilter;
        DraftDateFrom = DateFromFilter;
        DraftDateTo = DateToFilter;
        FilterPopupVisible = true;
    }

    protected async Task ClearFiltersAsync()
    {
        DraftStatusFilter = null;
        DraftDateFrom = null;
        DraftDateTo = null;
        StatusFilter = null;
        DateFromFilter = null;
        DateToFilter = null;
        FilterPopupVisible = false;
        await ApplySearchAsync();
    }

    protected async Task ApplyFiltersAsync()
    {
        StatusFilter = DraftStatusFilter;
        DateFromFilter = DraftDateFrom?.Date;
        DateToFilter = DraftDateTo?.Date;
        FilterPopupVisible = false;
        await ApplySearchAsync();
    }

    protected Task OnActionClick(SelectedButtonInfo<SubconListRow> info)
    {
        if (info.SelectedRow is not null)
        {
            NavigateView(info.SelectedRow.DocNo);
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(string docNo) =>
        Navigation.NavigateTo($"/subcon/send-out/view/{docNo}");

    private async Task<(IReadOnlyList<SubconListRow> Rows, int TotalCount)> SearchAsync(
        SubconListQuery query, CancellationToken cancellationToken)
    {
        var result = await Subcon.SearchAsync(query with
        {
            SearchText = SearchText,
            Status = StatusFilter,
            DateFrom = DateFromFilter,
            DateTo = DateToFilter,
            Kind = SubconListKinds.Dispatch
        }, cancellationToken);
        TotalCount = result.ListPage?.TotalCount ?? 0;
        if (query.Skip == 0 && result.ListPage is not null)
        {
            CompactRows = result.ListPage.Rows;
        }

        return result.ListPage is null ? ([], 0) : (result.ListPage.Rows, result.ListPage.TotalCount);
    }

    public void Dispose() => _searchDebounce?.Dispose();
}
