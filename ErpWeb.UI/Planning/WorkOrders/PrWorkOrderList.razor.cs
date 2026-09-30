using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrWorkOrderList : PageBase, IDisposable
{
    [Inject] private IProductionWorkOrderService WorkOrders { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;

    protected bool IsBootstrapping = true;
    protected bool FilterPopupVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected bool CanAdd;
    protected bool CanEdit;
    protected List<ProductionWorkOrderListRow> CompactRows { get; set; } = [];
    protected ProductionWorkOrderGridDataSource DataSource { get; private set; } = default!;

    protected string? AppliedWorkOrderNo;
    protected string? AppliedProductCode;
    protected string? AppliedStatus;
    protected DateTime? AppliedStartFrom;
    protected DateTime? AppliedStartTo;

    protected string DraftWorkOrderNo = string.Empty;
    protected string DraftProductCode = string.Empty;
    protected string DraftStatus = string.Empty;
    protected DateTime? DraftStartFrom;
    protected DateTime? DraftStartTo;

    protected string TotalCountLabel => TotalCount == 1 ? "1 Work Order" : $"{TotalCount:N0} Work Orders";
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedWorkOrderNo)
        || !string.IsNullOrWhiteSpace(AppliedProductCode)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedStartFrom is not null
        || AppliedStartTo is not null;

    protected IReadOnlyList<FilterOption> StatusOptions { get; } =
    [
        new(string.Empty, "All statuses"),
        new("DRAFT", "Draft"),
        new("RELEASED", "Released"),
        new("IN_PROGRESS", "In progress"),
        new("COMPLETED", "Completed"),
        new("CLOSED", "Closed"),
        new("CANCELLED", "Cancelled")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Work Order", FieldName = nameof(ProductionWorkOrderListRow.WorkOrderNo), Width = "145px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Product", FieldName = nameof(ProductionWorkOrderListRow.ProductCode), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "Description", FieldName = nameof(ProductionWorkOrderListRow.ProductDescription), VisibleIndex = 3 },
        new() { Caption = "Status", FieldName = nameof(ProductionWorkOrderListRow.Status), Width = "115px", VisibleIndex = 4 },
        new() { Caption = "BOM Ver.", FieldName = nameof(ProductionWorkOrderListRow.BomVersion), DataType = "number", Width = "85px", VisibleIndex = 5 },
        new() { Caption = "Planned Qty", FieldName = nameof(ProductionWorkOrderListRow.PlannedQty), DataType = "number", DecimalPlace = 4, Width = "110px", VisibleIndex = 6 },
        new() { Caption = "Good Qty", FieldName = nameof(ProductionWorkOrderListRow.GoodQty), DataType = "number", DecimalPlace = 4, Width = "105px", VisibleIndex = 7 },
        new() { Caption = "Remaining", FieldName = nameof(ProductionWorkOrderListRow.RemainingQty), DataType = "number", DecimalPlace = 4, Width = "105px", VisibleIndex = 8 },
        new() { Caption = "Start", FieldName = nameof(ProductionWorkOrderListRow.PlannedStartDate), DataType = "date", DisplayFormat = "dd MMM yyyy", Width = "115px", VisibleIndex = 9 },
        new() { Caption = "Completion", FieldName = nameof(ProductionWorkOrderListRow.PlannedCompletionDate), DataType = "date", DisplayFormat = "dd MMM yyyy", Width = "115px", VisibleIndex = 10 },
        new() { Caption = "Source", FieldName = nameof(ProductionWorkOrderListRow.SourceReference), Width = "130px", VisibleIndex = 11 },
        new() { Caption = "Material %", FieldName = nameof(ProductionWorkOrderListRow.MaterialProgressPercent), DataType = "number", DecimalPlace = 2, Width = "100px", VisibleIndex = 12 },
        new() { Caption = "Production %", FieldName = nameof(ProductionWorkOrderListRow.ProductionProgressPercent), DataType = "number", DecimalPlace = 2, Width = "110px", VisibleIndex = 13 },
        ..AuditColumns.For(startVisibleIndex: 14)
    ];

    protected List<ButtonInfo> Buttons { get; private set; } = [];
    protected List<ButtonInfo> ActionButtons { get; private set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new ProductionWorkOrderGridDataSource(LoadPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Edit);
        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd }
        ];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit", Enabled = CanEdit }
        ];

        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected Task OnButtonClick(SelectedButtonInfo<ProductionWorkOrderListRow> info)
    {
        if (string.Equals(info.SelectedButton.Text, "NEW", StringComparison.OrdinalIgnoreCase))
        {
            if (!CanAdd)
            {
                StatusMessage = "Access denied.";
            }
            else
            {
                Navigation.NavigateTo("/planning/work-orders/new");
            }
        }

        return Task.CompletedTask;
    }

    protected Task OnActionClick(SelectedButtonInfo<ProductionWorkOrderListRow> info)
    {
        var row = info.SelectedRow;
        if (row is null)
        {
            StatusMessage = "No Work Order selected.";
            return Task.CompletedTask;
        }

        var action = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (action == "VIEW")
        {
            OpenView(row.WorkOrderNo);
        }
        else if (action == "EDIT")
        {
            if (!CanEdit)
            {
                StatusMessage = "Access denied.";
            }
            else if (!string.Equals(row.Status, "DRAFT", StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = "Released or completed Work Orders are read-only. Use View or a controlled Change Order.";
            }
            else
            {
                Navigation.NavigateTo($"/planning/work-orders/edit/{Uri.EscapeDataString(row.WorkOrderNo)}");
            }
        }

        return Task.CompletedTask;
    }

    protected void OpenView(string number) =>
        Navigation.NavigateTo($"/planning/work-orders/view/{Uri.EscapeDataString(number)}");

    protected void OpenFilterPopup()
    {
        DraftWorkOrderNo = AppliedWorkOrderNo ?? string.Empty;
        DraftProductCode = AppliedProductCode ?? string.Empty;
        DraftStatus = AppliedStatus ?? string.Empty;
        DraftStartFrom = AppliedStartFrom;
        DraftStartTo = AppliedStartTo;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedWorkOrderNo = NullIfEmpty(DraftWorkOrderNo);
        AppliedProductCode = NullIfEmpty(DraftProductCode);
        AppliedStatus = NullIfEmpty(DraftStatus);
        AppliedStartFrom = DraftStartFrom?.Date;
        AppliedStartTo = DraftStartTo?.Date;
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftWorkOrderNo = DraftProductCode = DraftStatus = string.Empty;
        DraftStartFrom = DraftStartTo = null;
        await ApplyFiltersAsync();
    }

    protected async Task OnSearchTextChanged(string text)
    {
        SearchText = text ?? string.Empty;
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
        _searchDebounce = new Timer(400) { AutoReset = false };
        var version = Interlocked.Increment(ref _searchVersion);
        _searchDebounce.Elapsed += async (_, _) =>
        {
            if (version == _searchVersion)
            {
                await InvokeAsync(ReloadGridAsync);
            }
        };
        _searchDebounce.Start();
        await Task.CompletedTask;
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private async Task ReloadGridAsync()
    {
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncDataSourceFilters() =>
        DataSource.UpdateFilters(new ProductionWorkOrderListQuery
        {
            SearchText = NullIfEmpty(SearchText),
            WorkOrderNo = AppliedWorkOrderNo,
            ProductCode = AppliedProductCode,
            Status = AppliedStatus,
            StartDateFrom = AppliedStartFrom,
            StartDateTo = AppliedStartTo
        });

    private async Task<(IReadOnlyList<ProductionWorkOrderListRow> Rows, int TotalCount)> LoadPageAsync(
        ProductionWorkOrderListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await WorkOrders.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.Message ?? "Unable to load Work Orders.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.Data.TotalCount);
        return (result.Data.Rows, result.Data.TotalCount);
    }

    private async Task RefreshCompactPreviewAsync()
    {
        var query = DataSource.CurrentQuery;
        query.Skip = 0;
        query.Take = 50;
        var result = await WorkOrders.SearchAsync(query);
        CompactRows = result.Succeeded && result.Data is not null ? result.Data.Rows.ToList() : [];
        TotalCount = result.Data?.TotalCount ?? 0;
        if (!result.Succeeded)
        {
            ErrorMessage = result.Message ?? "Unable to load Work Orders.";
        }
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
    }

    protected sealed record FilterOption(string Key, string Name);
}

public sealed class ProductionWorkOrderGridDataSource : GridCustomDataSource
{
    private readonly Func<ProductionWorkOrderListQuery, CancellationToken,
        Task<(IReadOnlyList<ProductionWorkOrderListRow> Rows, int TotalCount)>> _loader;
    private ProductionWorkOrderListQuery _filters = new();

    public ProductionWorkOrderGridDataSource(
        Func<ProductionWorkOrderListQuery, CancellationToken,
            Task<(IReadOnlyList<ProductionWorkOrderListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public ProductionWorkOrderListQuery CurrentQuery => Clone(_filters);
    public void UpdateFilters(ProductionWorkOrderListQuery query) => _filters = Clone(query);

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
            query.SortField = options.SortInfo[0].FieldName;
            query.SortDescending = options.SortInfo[0].DescendingSortOrder;
        }

        var (rows, _) = await _loader(query, cancellationToken);
        return rows.ToList();
    }

    private static ProductionWorkOrderListQuery Clone(ProductionWorkOrderListQuery source) => new()
    {
        SearchText = source.SearchText,
        WorkOrderNo = source.WorkOrderNo,
        ProductCode = source.ProductCode,
        Status = source.Status,
        StartDateFrom = source.StartDateFrom,
        StartDateTo = source.StartDateTo,
        SortField = source.SortField,
        SortDescending = source.SortDescending,
        Skip = source.Skip,
        Take = source.Take
    };
}

