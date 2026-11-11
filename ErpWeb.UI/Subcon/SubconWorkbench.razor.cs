using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Core.Subcon;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Subcon;

public partial class SubconWorkbench : PageBase, IDisposable
{
    [Inject] private ISubconApplicationService Subcon { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private Timer? _searchDebounce;

    protected bool IsBootstrapping = true;
    protected bool CanAdd;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected SubconWorkbenchPage Page { get; set; } = new();
    protected SubconWorkbenchGridDataSource DataSource { get; private set; } = default!;

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

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new SubconWorkbenchGridDataSource(SearchPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.SubconSendOut, PermissionCodes.Add)
                 || await AccessRights.CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Add);
        ActionButtons =
        [
            new() { Text = "View", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View send-out" },
            new() { Text = "Edit", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit draft" }
        ];
        await RefreshAsync();
        IsBootstrapping = false;
    }

    protected void OnSearchTextChanged(string value)
    {
        SearchText = value;
        _searchDebounce?.Dispose();
        _searchDebounce = new Timer(300) { AutoReset = false };
        _searchDebounce.Elapsed += async (_, _) => await InvokeAsync(RefreshAsync);
        _searchDebounce.Start();
    }

    protected Task OnActionClick(SelectedButtonInfo<SubconListRow> info)
    {
        if (info.SelectedRow is null)
        {
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (mode == "EDIT")
        {
            Navigation.NavigateTo($"/subcon/send-out/edit/{info.SelectedRow.DocNo}");
        }
        else
        {
            NavigateView(info.SelectedRow.DocNo);
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(string docNo) =>
        Navigation.NavigateTo($"/subcon/send-out/view/{docNo}");

    protected void DismissStatus() => StatusMessage = null;

    private async Task RefreshAsync()
    {
        var result = await Subcon.GetWorkbenchAsync(new SubconListQuery { SearchText = SearchText, Take = 50 });
        Page = result.Workbench ?? new SubconWorkbenchPage();
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
        }
    }

    private async Task<(IReadOnlyList<SubconListRow> Rows, int TotalCount)> SearchPageAsync(
        SubconListQuery query, CancellationToken cancellationToken)
    {
        query.SearchText = SearchText;
        var result = await Subcon.SearchAsync(query, cancellationToken);
        return result.ListPage is null
            ? ([], 0)
            : (result.ListPage.Rows, result.ListPage.TotalCount);
    }

    public void Dispose()
    {
        _searchDebounce?.Dispose();
    }
}

public sealed class SubconWorkbenchGridDataSource : GridCustomDataSource
{
    private readonly Func<SubconListQuery, CancellationToken, Task<(IReadOnlyList<SubconListRow> Rows, int TotalCount)>> _loader;
    private SubconListQuery _filters = new();

    public SubconWorkbenchGridDataSource(
        Func<SubconListQuery, CancellationToken, Task<(IReadOnlyList<SubconListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public override async Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions options, CancellationToken cancellationToken)
    {
        var (_, total) = await _loader(_filters with { Skip = 0, Take = 1 }, cancellationToken);
        return total;
    }

    public override async Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions options, CancellationToken cancellationToken)
    {
        var (rows, _) = await _loader(_filters with
        {
            Skip = Math.Max(0, options.StartIndex),
            Take = Math.Clamp(options.Count <= 0 ? 20 : options.Count, 1, 100)
        }, cancellationToken);
        return rows.ToList();
    }
}
