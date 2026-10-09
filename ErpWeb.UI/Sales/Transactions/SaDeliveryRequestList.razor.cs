using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Sales.Transactions;

public partial class SaDeliveryRequestList : PageBase, IDisposable
{
    [Inject] private ISaDeliveryRequestService Requests { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<SaDeliveryRequestListRow> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;

    protected string? AppliedStatus;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;
    protected string DraftStatusKey = "all";
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;

    protected SaDeliveryRequestGridDataSource DataSource { get; private set; } = default!;
    protected string TotalCountLabel => TotalCount == 1
        ? "1 Delivery Request"
        : $"{TotalCount:N0} Delivery Requests";
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedDateFrom is not null
        || AppliedDateTo is not null;

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        new(SaDeliveryRequestStatuses.Draft, "DRAFT"),
        new(SaDeliveryRequestStatuses.Released, "RELEASED"),
        new(SaDeliveryRequestStatuses.InProduction, "IN PRODUCTION"),
        new(SaDeliveryRequestStatuses.Completed, "COMPLETED"),
        new(SaDeliveryRequestStatuses.Cancelled, "CANCELLED")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "DR No.", FieldName = nameof(SaDeliveryRequestListRow.DeliveryRequestNo), Width = "140px", VisibleIndex = 1 },
        new() { Caption = "Product", FieldName = nameof(SaDeliveryRequestListRow.ProductCode), Width = "125px", VisibleIndex = 2 },
        new() { Caption = "Description", FieldName = nameof(SaDeliveryRequestListRow.ProductDescription), VisibleIndex = 3, AllowSort = false },
        new() { Caption = "UOM", FieldName = nameof(SaDeliveryRequestListRow.ProductionUom), Width = "90px", VisibleIndex = 4 },
        new() { Caption = "Requested", FieldName = nameof(SaDeliveryRequestListRow.RequestedQty), DataType = "decimal", DisplayFormat = "n4", Width = "115px", VisibleIndex = 5 },
        new() { Caption = "WO allocated", FieldName = nameof(SaDeliveryRequestListRow.WoAllocatedQty), DataType = "decimal", DisplayFormat = "n4", Width = "125px", VisibleIndex = 6, AllowSort = false },
        new() { Caption = "Unplanned", FieldName = nameof(SaDeliveryRequestListRow.UnplannedQty), DataType = "decimal", DisplayFormat = "n4", Width = "115px", VisibleIndex = 7, AllowSort = false },
        new() { Caption = "Produced", FieldName = nameof(SaDeliveryRequestListRow.ProducedQty), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 8, AllowSort = false },
        new() { Caption = "Required date", FieldName = nameof(SaDeliveryRequestListRow.RequiredDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "125px", VisibleIndex = 9, SortIndex = 0, SortOrder = GridColumnSortOrder.Descending },
        new() { Caption = "Status", FieldName = nameof(SaDeliveryRequestListRow.Status), Width = "125px", VisibleIndex = 10, AllowSort = false },
        new() { Caption = "SO sources", FieldName = nameof(SaDeliveryRequestListRow.SourceCount), DataType = "int", Width = "100px", VisibleIndex = 11, AllowSort = false },
        new() { Caption = "Work Orders", FieldName = nameof(SaDeliveryRequestListRow.WorkOrderCount), DataType = "int", Width = "110px", VisibleIndex = 12, AllowSort = false },
        new() { Caption = "Created by", FieldName = nameof(SaDeliveryRequestListRow.CreatedBy), Width = "120px", VisibleIndex = 13 },
        new() { Caption = "Created date", FieldName = nameof(SaDeliveryRequestListRow.CreatedDate), DataType = "datetime", DisplayFormat = "dd/MM/yyyy HH:mm", Width = "150px", VisibleIndex = 14 }
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new SaDeliveryRequestGridDataSource(SearchPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.SalesDeliveryRequest, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.SalesDeliveryRequest, PermissionCodes.Edit);
        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd }
        ];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View Delivery Request" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit Delivery Request", Enabled = CanEdit }
        ];

        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected async Task OnButtonClick(SelectedButtonInfo<SaDeliveryRequestListRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW":
                if (!CanAdd)
                {
                    StatusMessage = "Access Denied!!";
                    return;
                }

                Navigation.NavigateTo("/sales/delivery-requests/new");
                break;
            case "REFRESH":
                await ReloadGridAsync();
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<SaDeliveryRequestListRow> info)
    {
        if (info.SelectedRow is null)
        {
            ErrorMessage = "No Delivery Request selected.";
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (mode == "VIEW")
        {
            NavigateView(info.SelectedRow.Uid);
        }
        else if (mode == "EDIT")
        {
            if (!CanEdit)
            {
                ErrorMessage = "You do not have permission to edit Delivery Requests.";
                return Task.CompletedTask;
            }

            if (!string.Equals(info.SelectedRow.Status, SaDeliveryRequestStatuses.Draft, StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "Only Draft Delivery Requests can be edited.";
                return Task.CompletedTask;
            }

            Navigation.NavigateTo($"/sales/delivery-requests/edit/{info.SelectedRow.Uid}");
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(long uid) =>
        Navigation.NavigateTo($"/sales/delivery-requests/view/{uid}");

    protected async Task OnSearchTextChanged(string text)
    {
        SearchText = text ?? string.Empty;
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
        _searchDebounce = new Timer(400) { AutoReset = false };
        var version = Interlocked.Increment(ref _searchVersion);
        _searchDebounce.Elapsed += async (_, _) =>
        {
            if (version != _searchVersion)
            {
                return;
            }

            await InvokeAsync(async () =>
            {
                SyncDataSourceFilters();
                await ReloadGridAsync();
            });
        };
        _searchDebounce.Start();
        await Task.CompletedTask;
    }

    protected void OpenFilterPopup()
    {
        DraftStatusKey = string.IsNullOrWhiteSpace(AppliedStatus) ? "all" : AppliedStatus;
        DraftDateFrom = AppliedDateFrom;
        DraftDateTo = AppliedDateTo;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedStatus = string.Equals(DraftStatusKey, "all", StringComparison.OrdinalIgnoreCase) ? null : DraftStatusKey;
        AppliedDateFrom = DraftDateFrom;
        AppliedDateTo = DraftDateTo;
        FilterPopupVisible = false;
        SyncDataSourceFilters();
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftStatusKey = "all";
        DraftDateFrom = null;
        DraftDateTo = null;
        AppliedStatus = null;
        AppliedDateFrom = null;
        AppliedDateTo = null;
        FilterPopupVisible = false;
        SyncDataSourceFilters();
        await ReloadGridAsync();
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    public void Dispose()
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
    }

    protected static string StatusChipClass(string? status) =>
        string.Equals(status, SaDeliveryRequestStatuses.Cancelled, StringComparison.OrdinalIgnoreCase) ? "is-off" :
        string.Equals(status, SaDeliveryRequestStatuses.Draft, StringComparison.OrdinalIgnoreCase) ? "is-hold" : "is-on";

    private async Task ReloadGridAsync()
    {
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncDataSourceFilters()
    {
        DataSource.UpdateFilters(new SaDeliveryRequestListQuery
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            Status = AppliedStatus,
            RequiredDateFrom = AppliedDateFrom,
            RequiredDateTo = AppliedDateTo,
            SortDescending = true
        });
    }

    private async Task RefreshCompactPreviewAsync()
    {
        var query = DataSource.CurrentQuery;
        query.Skip = 0;
        query.Take = 50;
        var result = await Requests.SearchAsync(query);
        if (result.Succeeded && result.Data is not null)
        {
            CompactRows = result.Data.Rows.ToList();
            TotalCount = result.Data.TotalCount;
        }
        else
        {
            CompactRows = [];
            TotalCount = 0;
            ErrorMessage = result.Message ?? "Unable to load Delivery Requests.";
        }
    }

    private async Task<(IReadOnlyList<SaDeliveryRequestListRow> Rows, int TotalCount)> SearchPageAsync(
        SaDeliveryRequestListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Requests.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.Message ?? "Unable to load Delivery Requests.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.Data.TotalCount);
        return (result.Data.Rows, result.Data.TotalCount);
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}

public sealed class SaDeliveryRequestGridDataSource : GridCustomDataSource
{
    private readonly Func<SaDeliveryRequestListQuery, CancellationToken, Task<(IReadOnlyList<SaDeliveryRequestListRow> Rows, int TotalCount)>> _loader;
    private SaDeliveryRequestListQuery _filters = new();

    public SaDeliveryRequestGridDataSource(
        Func<SaDeliveryRequestListQuery, CancellationToken, Task<(IReadOnlyList<SaDeliveryRequestListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public SaDeliveryRequestListQuery CurrentQuery => Clone(_filters);

    public void UpdateFilters(SaDeliveryRequestListQuery query) =>
        _filters = Clone(query);

    public override async Task<int> GetItemCountAsync(
        GridCustomDataSourceCountOptions options,
        CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = 0;
        query.Take = 1;
        var (_, total) = await _loader(query, cancellationToken);
        return total;
    }

    public override async Task<IList> GetItemsAsync(
        GridCustomDataSourceItemsOptions options,
        CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = Math.Max(0, options.StartIndex);
        query.Take = Math.Clamp(options.Count <= 0 ? 20 : options.Count, 1, 100);

        if (options.SortInfo is { Count: > 0 })
        {
            var sort = options.SortInfo[0];
            query.SortField = sort.FieldName;
            query.SortDescending = sort.DescendingSortOrder;
        }

        var (rows, _) = await _loader(query, cancellationToken);
        return rows.ToList();
    }

    private static SaDeliveryRequestListQuery Clone(SaDeliveryRequestListQuery source) =>
        new()
        {
            SearchText = source.SearchText,
            Status = source.Status,
            ProductCode = source.ProductCode,
            RequiredDateFrom = source.RequiredDateFrom,
            RequiredDateTo = source.RequiredDateTo,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}
