using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Production;
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
    protected bool CanDelete;
    protected bool CanReopen;
    protected bool ReopenConfirmVisible;
    protected bool IsReopening;
    protected string ReopenReason = string.Empty;
    protected ProductionWorkOrderDetail? ReopenTarget;
    protected bool DeleteConfirmVisible;
    protected bool IsDeleting;
    protected ProductionWorkOrderDetail? DeleteTarget;
    protected List<ProductionWorkOrderListRow> CompactRows { get; set; } = [];
    protected ProductionWorkOrderGridDataSource DataSource { get; private set; } = default!;

    protected string? AppliedWorkOrderNo;
    protected string? AppliedProductCode;
    protected string? AppliedDefinitionCode;
    protected string? AppliedStatus;
    protected DateTime? AppliedStartFrom;
    protected DateTime? AppliedStartTo;

    protected string DraftWorkOrderNo = string.Empty;
    protected string DraftProductCode = string.Empty;
    protected string DraftDefinitionCode = string.Empty;
    protected string DraftStatus = string.Empty;
    protected DateTime? DraftStartFrom;
    protected DateTime? DraftStartTo;

    protected string TotalCountLabel => TotalCount == 1 ? "1 Work Order" : $"{TotalCount:N0} Work Orders";
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedWorkOrderNo)
        || !string.IsNullOrWhiteSpace(AppliedProductCode)
        || !string.IsNullOrWhiteSpace(AppliedDefinitionCode)
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
        new() { Caption = "Definition", FieldName = nameof(ProductionWorkOrderListRow.SourceDefinitionCode), Width = "120px", VisibleIndex = 4 },
        new() { Caption = "Status", FieldName = nameof(ProductionWorkOrderListRow.Status), Width = "115px", VisibleIndex = 5 },
        new() { Caption = "Def. Ver.", FieldName = nameof(ProductionWorkOrderListRow.BomVersion), DataType = "number", Width = "85px", VisibleIndex = 6 },
        new() { Caption = "Planned Qty", FieldName = nameof(ProductionWorkOrderListRow.PlannedQty), DataType = "number", DecimalPlace = 4, Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Good Qty", FieldName = nameof(ProductionWorkOrderListRow.GoodQty), DataType = "number", DecimalPlace = 4, Width = "105px", VisibleIndex = 8 },
        new() { Caption = "Remaining", FieldName = nameof(ProductionWorkOrderListRow.RemainingQty), DataType = "number", DecimalPlace = 4, Width = "105px", VisibleIndex = 9 },
        new() { Caption = "Start", FieldName = nameof(ProductionWorkOrderListRow.PlannedStartDate), DataType = "date", DisplayFormat = "dd MMM yyyy", Width = "115px", VisibleIndex = 10 },
        new() { Caption = "Completion", FieldName = nameof(ProductionWorkOrderListRow.PlannedCompletionDate), DataType = "date", DisplayFormat = "dd MMM yyyy", Width = "115px", VisibleIndex = 11 },
        new() { Caption = "Source", FieldName = nameof(ProductionWorkOrderListRow.SourceReference), Width = "130px", VisibleIndex = 12 },
        new() { Caption = "Material %", FieldName = nameof(ProductionWorkOrderListRow.MaterialProgressPercent), DataType = "number", DecimalPlace = 2, Width = "100px", VisibleIndex = 13 },
        new() { Caption = "Production %", FieldName = nameof(ProductionWorkOrderListRow.ProductionProgressPercent), DataType = "number", DecimalPlace = 2, Width = "110px", VisibleIndex = 14 },
        ..AuditColumns.For(startVisibleIndex: 15)
    ];

    protected List<ButtonInfo> Buttons { get; private set; } = [];
    protected List<ButtonInfo> ActionButtons { get; private set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new ProductionWorkOrderGridDataSource(LoadPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Delete);
        CanReopen = await AccessRights.CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Reopen);
        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd }
        ];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View" },
            new()
            {
                Text = "EDIT",
                IConClass = "far fa-edit",
                Style = "primary",
                ToolTip = "Edit Draft, or reopen Released for edit",
                Enabled = CanEdit
            },
            new()
            {
                Text = "DELETE",
                IConClass = "fa-regular fa-trash-can",
                Style = "danger",
                ToolTip = "Delete Draft Work Order",
                Enabled = CanDelete
            }
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

    protected async Task OnActionClick(SelectedButtonInfo<ProductionWorkOrderListRow> info)
    {
        var row = info.SelectedRow;
        if (row is null)
        {
            StatusMessage = "No Work Order selected.";
            return;
        }

        var action = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (action == "VIEW")
        {
            OpenView(row.WorkOrderNo);
            return;
        }

        if (action == "DELETE")
        {
            await BeginDeleteAsync(row);
            return;
        }

        if (action != "EDIT")
        {
            return;
        }

        if (!CanEdit)
        {
            StatusMessage = "Access denied.";
            return;
        }

        var status = (row.Status ?? string.Empty).Trim().ToUpperInvariant();
        if (status == ProductionWorkOrderStatuses.Draft)
        {
            Navigation.NavigateTo($"/planning/work-orders/edit/{Uri.EscapeDataString(row.WorkOrderNo)}");
            return;
        }

        if (status == ProductionWorkOrderStatuses.Released)
        {
            if (!CanReopen)
            {
                StatusMessage = "This Work Order is Released. Reopen permission is required before it can be edited.";
                return;
            }

            await BeginReopenConfirmAsync(row.WorkOrderNo);
            return;
        }

        StatusMessage = status switch
        {
            ProductionWorkOrderStatuses.InProgress =>
                "This Work Order has entered production and cannot be reopened as Draft. Use the controlled production change/correction process.",
            ProductionWorkOrderStatuses.Completed or ProductionWorkOrderStatuses.Closed =>
                "Completed or Closed Work Orders cannot be reopened for direct editing.",
            ProductionWorkOrderStatuses.Cancelled =>
                "Cancelled Work Orders cannot be edited.",
            _ =>
                "This Work Order cannot be edited in its current status."
        };
    }

    private async Task BeginReopenConfirmAsync(string workOrderNo)
    {
        StatusMessage = null;
        ErrorMessage = null;
        var latest = await WorkOrders.GetAsync(workOrderNo);
        if (!latest.Succeeded || latest.Data is null)
        {
            ErrorMessage = latest.Message ?? "Unable to load the Work Order for reopen.";
            return;
        }

        if (!string.Equals(latest.Data.Status, ProductionWorkOrderStatuses.Released, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = latest.Data.Status switch
            {
                ProductionWorkOrderStatuses.Draft =>
                    "This Work Order is already Draft. Use Edit to continue.",
                ProductionWorkOrderStatuses.InProgress =>
                    "This Work Order has entered production and cannot be reopened as Draft. Use the controlled production change/correction process.",
                ProductionWorkOrderStatuses.Completed or ProductionWorkOrderStatuses.Closed =>
                    "Completed or Closed Work Orders cannot be reopened for direct editing.",
                ProductionWorkOrderStatuses.Cancelled =>
                    "Cancelled Work Orders cannot be edited.",
                _ =>
                    "Only a Released Work Order can be reopened for editing."
            };
            return;
        }

        ReopenTarget = latest.Data;
        ReopenReason = string.Empty;
        ReopenConfirmVisible = true;
    }

    protected void CloseReopenPopup()
    {
        if (IsReopening)
        {
            return;
        }

        ReopenConfirmVisible = false;
        ReopenTarget = null;
        ReopenReason = string.Empty;
    }

    private async Task BeginDeleteAsync(ProductionWorkOrderListRow row)
    {
        if (!CanDelete)
        {
            StatusMessage = "Access denied.";
            return;
        }

        if (!string.Equals(row.Status, ProductionWorkOrderStatuses.Draft, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Only Draft Work Orders can be deleted.";
            return;
        }

        StatusMessage = null;
        ErrorMessage = null;
        var latest = await WorkOrders.GetAsync(row.WorkOrderNo);
        if (!latest.Succeeded || latest.Data is null)
        {
            ErrorMessage = latest.Message ?? "Unable to load the Work Order for deletion.";
            return;
        }

        if (!string.Equals(latest.Data.Status, ProductionWorkOrderStatuses.Draft, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = $"Only Draft Work Orders can be deleted. This Work Order is currently {latest.Data.Status}.";
            return;
        }

        DeleteTarget = latest.Data;
        DeleteConfirmVisible = true;
    }

    protected void CloseDeletePopup()
    {
        if (IsDeleting)
        {
            return;
        }

        DeleteConfirmVisible = false;
        DeleteTarget = null;
    }

    protected async Task ConfirmDeleteAsync()
    {
        if (DeleteTarget is null || IsDeleting)
        {
            return;
        }

        IsDeleting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var deletedNo = DeleteTarget.WorkOrderNo;
            var result = await WorkOrders.DeleteDraftAsync(new ProductionWorkOrderDeleteRequest
            {
                WorkOrderNo = deletedNo,
                RowVersion = DeleteTarget.RowVersion
            });
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to delete the Draft Work Order.";
                return;
            }

            DeleteConfirmVisible = false;
            DeleteTarget = null;
            await ReloadGridAsync();
            StatusMessage = $"Draft Work Order {result.Data} deleted.";
        }
        finally
        {
            IsDeleting = false;
        }
    }

    protected async Task ReopenForEditAsync()
    {
        if (ReopenTarget is null || IsReopening)
        {
            return;
        }

        var reason = (ReopenReason ?? string.Empty).Trim();
        if (reason.Length == 0)
        {
            ErrorMessage = "Reopen reason is required.";
            return;
        }

        if (reason.Length > 500)
        {
            ErrorMessage = "Reopen reason must be 500 characters or fewer.";
            return;
        }

        IsReopening = true;
        ErrorMessage = null;
        try
        {
            var result = await WorkOrders.ReopenForEditAsync(new ProductionWorkOrderReopenRequest
            {
                WorkOrderNo = ReopenTarget.WorkOrderNo,
                RowVersion = ReopenTarget.RowVersion,
                Reason = reason
            });

            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Unable to reopen the Work Order.";
                return;
            }

            ReopenConfirmVisible = false;
            ReopenTarget = null;
            ReopenReason = string.Empty;
            Navigation.NavigateTo(
                $"/planning/work-orders/edit/{Uri.EscapeDataString(result.Data.WorkOrderNo)}");
        }
        finally
        {
            IsReopening = false;
        }
    }

    protected void OpenView(string number) =>
        Navigation.NavigateTo($"/planning/work-orders/view/{Uri.EscapeDataString(number)}");

    protected void OpenFilterPopup()
    {
        DraftWorkOrderNo = AppliedWorkOrderNo ?? string.Empty;
        DraftProductCode = AppliedProductCode ?? string.Empty;
        DraftDefinitionCode = AppliedDefinitionCode ?? string.Empty;
        DraftStatus = AppliedStatus ?? string.Empty;
        DraftStartFrom = AppliedStartFrom;
        DraftStartTo = AppliedStartTo;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedWorkOrderNo = NullIfEmpty(DraftWorkOrderNo);
        AppliedProductCode = NullIfEmpty(DraftProductCode);
        AppliedDefinitionCode = NullIfEmpty(DraftDefinitionCode);
        AppliedStatus = NullIfEmpty(DraftStatus);
        AppliedStartFrom = DraftStartFrom?.Date;
        AppliedStartTo = DraftStartTo?.Date;
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftWorkOrderNo = DraftProductCode = DraftDefinitionCode = DraftStatus = string.Empty;
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
            DefinitionCode = AppliedDefinitionCode,
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
        DefinitionCode = source.DefinitionCode,
        Status = source.Status,
        StartDateFrom = source.StartDateFrom,
        StartDateTo = source.StartDateTo,
        SortField = source.SortField,
        SortDescending = source.SortDescending,
        Skip = source.Skip,
        Take = source.Take
    };
}

