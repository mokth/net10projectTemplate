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

public partial class PrMaterialIssueList : PageBase, IDisposable
{
    [Inject] private IProductionMaterialIssueService MaterialIssues { get; set; } = default!;
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
    protected List<ProductionMaterialIssueListRow> CompactRows { get; set; } = [];
    protected ProductionMaterialIssueGridDataSource DataSource { get; private set; } = default!;
    protected string? AppliedWorkOrderNo;
    protected string? AppliedProductCode;
    protected string? AppliedStatus;
    protected int? AppliedBatchNo;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;
    protected string DraftWorkOrderNo = string.Empty;
    protected string DraftProductCode = string.Empty;
    protected string DraftStatus = string.Empty;
    protected int? DraftBatchNo;
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;
    protected string TotalCountLabel => TotalCount == 1 ? "1 batch" : $"{TotalCount:N0} batches";
    protected bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchText) || !string.IsNullOrWhiteSpace(AppliedWorkOrderNo)
        || !string.IsNullOrWhiteSpace(AppliedProductCode) || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedBatchNo.HasValue || AppliedDateFrom.HasValue || AppliedDateTo.HasValue;
    protected IReadOnlyList<FilterOption> StatusOptions { get; } =
    [
        new(string.Empty, "All statuses"), new("POSTED", "Posted"), new("CANCELLED", "Reversed")
    ];
    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Batch No", FieldName = nameof(ProductionMaterialIssueListRow.BatchNo), Width = "95px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Issue Date", FieldName = nameof(ProductionMaterialIssueListRow.IssueDate), DataType = "date", DisplayFormat = "dd MMM yyyy", Width = "115px", VisibleIndex = 2 },
        new() { Caption = "Work Order", FieldName = nameof(ProductionMaterialIssueListRow.WorkOrderNo), Width = "135px", VisibleIndex = 3 },
        new() { Caption = "Product", FieldName = nameof(ProductionMaterialIssueListRow.ProductCode), Width = "120px", VisibleIndex = 4 },
        new() { Caption = "Description", FieldName = nameof(ProductionMaterialIssueListRow.ProductDescription), VisibleIndex = 5 },
        new() { Caption = "Status", FieldName = nameof(ProductionMaterialIssueListRow.Status), Width = "100px", VisibleIndex = 6 },
        new() { Caption = "Materials", FieldName = nameof(ProductionMaterialIssueListRow.LineCount), DataType = "number", Width = "85px", VisibleIndex = 7 },
        new() { Caption = "Posted By", FieldName = nameof(ProductionMaterialIssueListRow.PostedBy), Width = "105px", VisibleIndex = 8 },
        new() { Caption = "Posted Date", FieldName = nameof(ProductionMaterialIssueListRow.PostedDate), DataType = "date", DisplayFormat = "dd MMM yyyy HH:mm", Width = "145px", VisibleIndex = 9 },
        new() { Caption = "Reversed", FieldName = nameof(ProductionMaterialIssueListRow.RollbackDate), DataType = "date", DisplayFormat = "dd MMM yyyy HH:mm", Width = "145px", VisibleIndex = 10 }
    ];
    protected List<ButtonInfo> Buttons { get; private set; } = [];
    protected List<ButtonInfo> ActionButtons { get; private set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new ProductionMaterialIssueGridDataSource(LoadPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Add);
        Buttons = [new() { Text = "NEW ISSUE", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd }];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View" },
            new() { Text = "ROLLBACK", IConClass = "fas fa-rotate-left", Style = "warning", ToolTip = "Rollback is enabled in Phase 7", Enabled = false }
        ];
        SyncFilters();
        await RefreshCompactAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid grid) => _grid = grid;
    protected Task OnButtonClick(SelectedButtonInfo<ProductionMaterialIssueListRow> info)
    {
        if (CanAdd) Navigation.NavigateTo("/planning/material-issues/new"); else StatusMessage = "Access denied.";
        return Task.CompletedTask;
    }
    protected Task OnActionClick(SelectedButtonInfo<ProductionMaterialIssueListRow> info)
    {
        if (info.SelectedRow is { } row && string.Equals(info.SelectedButton.Text, "VIEW", StringComparison.OrdinalIgnoreCase)) OpenView(row.BatchNo);
        return Task.CompletedTask;
    }
    protected void OpenView(int batchNo) => Navigation.NavigateTo($"/planning/material-issues/{batchNo}");
    protected void OpenFilterPopup()
    {
        DraftWorkOrderNo = AppliedWorkOrderNo ?? string.Empty; DraftProductCode = AppliedProductCode ?? string.Empty;
        DraftStatus = AppliedStatus ?? string.Empty; DraftBatchNo = AppliedBatchNo; DraftDateFrom = AppliedDateFrom; DraftDateTo = AppliedDateTo;
        FilterPopupVisible = true;
    }
    protected async Task ApplyFiltersAsync()
    {
        AppliedWorkOrderNo = NullIfEmpty(DraftWorkOrderNo); AppliedProductCode = NullIfEmpty(DraftProductCode);
        AppliedStatus = NullIfEmpty(DraftStatus); AppliedBatchNo = DraftBatchNo is > 0 ? DraftBatchNo : null;
        AppliedDateFrom = DraftDateFrom?.Date; AppliedDateTo = DraftDateTo?.Date; FilterPopupVisible = false;
        await ReloadAsync();
    }
    protected async Task ClearFiltersAsync()
    {
        DraftWorkOrderNo = DraftProductCode = DraftStatus = string.Empty; DraftBatchNo = null; DraftDateFrom = DraftDateTo = null;
        await ApplyFiltersAsync();
    }
    protected Task OnSearchTextChanged(string value)
    {
        SearchText = value ?? string.Empty; _searchDebounce?.Stop(); _searchDebounce?.Dispose();
        _searchDebounce = new Timer(400) { AutoReset = false }; var version = Interlocked.Increment(ref _searchVersion);
        _searchDebounce.Elapsed += async (_, _) => { if (version == _searchVersion) await InvokeAsync(ReloadAsync); };
        _searchDebounce.Start(); return Task.CompletedTask;
    }
    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;
    private async Task ReloadAsync() { SyncFilters(); await RefreshCompactAsync(); _grid?.Reload(); await InvokeAsync(StateHasChanged); }
    private void SyncFilters() => DataSource.UpdateFilters(new ProductionMaterialIssueListQuery
    {
        SearchText = NullIfEmpty(SearchText), WorkOrderNo = AppliedWorkOrderNo, ProductCode = AppliedProductCode,
        Status = AppliedStatus, BatchNo = AppliedBatchNo, DateFrom = AppliedDateFrom, DateTo = AppliedDateTo
    });
    private async Task<(IReadOnlyList<ProductionMaterialIssueListRow>, int)> LoadPageAsync(ProductionMaterialIssueListQuery query, CancellationToken ct)
    {
        var result = await MaterialIssues.SearchAsync(query, ct);
        if (!result.Succeeded || result.Data is null) { await InvokeAsync(() => ErrorMessage = result.Message ?? "Unable to load material issues."); return ([], 0); }
        await InvokeAsync(() => TotalCount = result.Data.TotalCount); return (result.Data.Rows, result.Data.TotalCount);
    }
    private async Task RefreshCompactAsync()
    {
        var query = DataSource.CurrentQuery; query.Skip = 0; query.Take = 50; var result = await MaterialIssues.SearchAsync(query);
        CompactRows = result.Data?.Rows.ToList() ?? []; TotalCount = result.Data?.TotalCount ?? 0;
        if (!result.Succeeded) ErrorMessage = result.Message ?? "Unable to load material issues.";
    }
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public void Dispose() { _searchDebounce?.Stop(); _searchDebounce?.Dispose(); }
    protected sealed record FilterOption(string Key, string Name);
}

public sealed class ProductionMaterialIssueGridDataSource : GridCustomDataSource
{
    private readonly Func<ProductionMaterialIssueListQuery, CancellationToken, Task<(IReadOnlyList<ProductionMaterialIssueListRow>, int)>> _loader;
    private ProductionMaterialIssueListQuery _filters = new();
    public ProductionMaterialIssueGridDataSource(Func<ProductionMaterialIssueListQuery, CancellationToken, Task<(IReadOnlyList<ProductionMaterialIssueListRow>, int)>> loader) => _loader = loader;
    public ProductionMaterialIssueListQuery CurrentQuery => Clone(_filters);
    public void UpdateFilters(ProductionMaterialIssueListQuery query) => _filters = Clone(query);
    public override async Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions options, CancellationToken ct) { var q = Clone(_filters); q.Skip = 0; q.Take = 1; var (_, count) = await _loader(q, ct); return count; }
    public override async Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions options, CancellationToken ct)
    {
        var q = Clone(_filters); q.Skip = Math.Max(0, options.StartIndex); q.Take = Math.Clamp(options.Count <= 0 ? 20 : options.Count, 1, 100);
        if (options.SortInfo is { Count: > 0 }) { q.SortField = options.SortInfo[0].FieldName; q.SortDescending = options.SortInfo[0].DescendingSortOrder; }
        var (rows, _) = await _loader(q, ct); return rows.ToList();
    }
    private static ProductionMaterialIssueListQuery Clone(ProductionMaterialIssueListQuery x) => new()
    {
        SearchText = x.SearchText, DateFrom = x.DateFrom, DateTo = x.DateTo, WorkOrderNo = x.WorkOrderNo,
        ProductCode = x.ProductCode, Status = x.Status, BatchNo = x.BatchNo, SortField = x.SortField,
        SortDescending = x.SortDescending, Skip = x.Skip, Take = x.Take
    };
}
