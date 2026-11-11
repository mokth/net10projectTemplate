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

    protected bool IsBootstrapping = true;
    protected int TotalCount;
    protected string SearchText = string.Empty;
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
        var page = await Subcon.SearchAsync(new SubconListQuery { Take = 1 });
        if (!page.Succeeded)
        {
            ErrorMessage = page.ErrorMessage;
        }

        TotalCount = page.ListPage?.TotalCount ?? 0;
        IsBootstrapping = false;
    }

    protected void OnSearchTextChanged(string value)
    {
        SearchText = value;
        _searchDebounce?.Dispose();
        _searchDebounce = new Timer(300) { AutoReset = false };
        _searchDebounce.Elapsed += async (_, _) => await InvokeAsync(async () =>
        {
            var page = await Subcon.SearchAsync(new SubconListQuery { SearchText = SearchText, Take = 1 });
            TotalCount = page.ListPage?.TotalCount ?? 0;
            StateHasChanged();
        });
        _searchDebounce.Start();
    }

    protected Task OnActionClick(SelectedButtonInfo<SubconListRow> info)
    {
        if (info.SelectedRow is not null)
        {
            Navigation.NavigateTo($"/subcon/send-out/view/{info.SelectedRow.DocNo}");
        }

        return Task.CompletedTask;
    }

    private async Task<(IReadOnlyList<SubconListRow> Rows, int TotalCount)> SearchAsync(
        SubconListQuery query, CancellationToken cancellationToken)
    {
        var result = await Subcon.SearchAsync(query with { SearchText = SearchText, Kind = SubconListKinds.Dispatch }, cancellationToken);
        TotalCount = result.ListPage?.TotalCount ?? 0;
        return result.ListPage is null ? ([], 0) : (result.ListPage.Rows, result.ListPage.TotalCount);
    }

    public void Dispose() => _searchDebounce?.Dispose();
}
