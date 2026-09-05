using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Inventory.Transactions;

public partial class IvStockAdjustmentList : PageBase, IDisposable
{
    [Inject] private IIvStockAdjustmentService StockAdjustment { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<IvStockAdjustmentListRow> _selectedRows = [];

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<IvStockAdjustmentListRow> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;
    protected bool CanPost;
    protected bool CanRollback;
    protected bool CanCancel;

    protected string? AppliedStatus;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;

    protected string DraftStatusKey = "all";
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;

    protected string ConfirmMessage { get; set; } = string.Empty;
    protected string ConfirmAction { get; set; } = "DELETE";
    protected string ConfirmButtonText => ConfirmAction switch
    {
        "POST" => "Post",
        "ROLLBACK" => "Rollback",
        "CANCEL" => "Cancel adjustments",
        _ => "Delete"
    };
    protected ButtonRenderStyle ConfirmButtonStyle => ConfirmAction switch
    {
        "POST" => ButtonRenderStyle.Primary,
        "ROLLBACK" => ButtonRenderStyle.Warning,
        "CANCEL" => ButtonRenderStyle.Warning,
        _ => ButtonRenderStyle.Danger
    };

    protected IvStockAdjustmentGridDataSource DataSource { get; private set; } = default!;

    protected string TotalCountLabel => TotalCount == 1 ? "1 batch" : $"{TotalCount:N0} batches";

    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedDateFrom is not null
        || AppliedDateTo is not null;

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        new(IvBatchStatuses.New, "NEW"),
        new(IvBatchStatuses.Posted, "POSTED"),
        new(IvBatchStatuses.Cancelled, "CANCELLED")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Batch No", FieldName = nameof(IvStockAdjustmentListRow.BatchNo), Width = "100px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Date", FieldName = nameof(IvStockAdjustmentListRow.TrxDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 2 },
        new() { Caption = "Status", FieldName = nameof(IvStockAdjustmentListRow.BatchStatus), Width = "100px", VisibleIndex = 3 },
        new() { Caption = "Ref No", FieldName = nameof(IvStockAdjustmentListRow.RefNo), Width = "120px", VisibleIndex = 4 },
        new() { Caption = "Remarks", FieldName = nameof(IvStockAdjustmentListRow.Remarks), VisibleIndex = 5 },
        new() { Caption = "Lines", FieldName = nameof(IvStockAdjustmentListRow.LineCount), Width = "80px", VisibleIndex = 6 },
        new() { Caption = "Total", FieldName = nameof(IvStockAdjustmentListRow.TotalAmount), DataType = "decimal", DisplayFormat = "n2", Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Created", FieldName = nameof(IvStockAdjustmentListRow.CreatedDate), DataType = "date", DisplayFormat = "dd/MM/yyyy HH:mm", Width = "140px", VisibleIndex = 8 },
        new() { Caption = "Created by", FieldName = nameof(IvStockAdjustmentListRow.CreatedBy), Visible = false, VisibleIndex = 9 }
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new IvStockAdjustmentGridDataSource(SearchPageAsync);

        CanAdd = await AccessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Delete);
        CanPost = await AccessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Post);
        CanRollback = await AccessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Rollback);
        CanCancel = await AccessRights.CanAsync(MenuCodes.InventoryStockAdjustment, PermissionCodes.Cancel);

        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "POST", IConClass = "fas fa-check", Style = "success", Enabled = CanPost },
            new() { Text = "ROLLBACK", IConClass = "fas fa-rotate-left", Style = "warning", Enabled = CanRollback },
            new() { Text = "CANCEL", IConClass = "fas fa-ban", Style = "warning", Enabled = CanCancel },
            new() { Text = "DELETE", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete }
        ];

        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View adjustment" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit adjustment", Enabled = CanEdit }
        ];

        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<IvStockAdjustmentListRow> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
    }

    protected async Task OnButtonClick(SelectedButtonInfo<IvStockAdjustmentListRow> info)
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

                Navigation.NavigateTo("/inventory/stock-adjustment/new");
                break;
            case "DELETE":
                await BeginDeleteAsync();
                break;
            case "POST":
                await BeginPostAsync();
                break;
            case "ROLLBACK":
                await BeginRollbackAsync();
                break;
            case "CANCEL":
                await BeginCancelAsync();
                break;
            case "REFRESH":
                await ReloadGridAsync();
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<IvStockAdjustmentListRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "VIEW":
                NavigateView(info.SelectedRow.BatchNo);
                break;
            case "EDIT":
                if (!CanEdit)
                {
                    StatusMessage = "Access Denied!!";
                    break;
                }

                if (!string.Equals(info.SelectedRow.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
                {
                    StatusMessage = "Only NEW adjustments can be edited.";
                    NavigateView(info.SelectedRow.BatchNo);
                    break;
                }

                Navigation.NavigateTo($"/inventory/stock-adjustment/edit/{info.SelectedRow.BatchNo}");
                break;
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(int batchNo) =>
        Navigation.NavigateTo($"/inventory/stock-adjustment/view/{batchNo}");

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
        AppliedStatus = string.Equals(DraftStatusKey, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : DraftStatusKey;
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

    protected async Task ConfirmActionAsync()
    {
        if (IsSubmitting || _selectedRows.Count == 0)
        {
            ConfirmVisible = false;
            return;
        }

        using var blocking = BeginBlockingWork(
            "Please wait. This action is still running.");

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var batchNos = _selectedRows.Select(x => x.BatchNo).ToList();
            IvStockAdjustmentOperationResult result = ConfirmAction switch
            {
                "POST" => await StockAdjustment.PostAsync(batchNos),
                "ROLLBACK" => await StockAdjustment.RollbackAsync(batchNos),
                "CANCEL" => await StockAdjustment.CancelAsync(batchNos),
                _ => await StockAdjustment.DeleteAsync(batchNos)
            };

            if (result.Succeeded)
            {
                StatusMessage = ConfirmAction switch
                {
                    "POST" => $"Posted {result.SucceededCount} adjustment(s).",
                    "ROLLBACK" => $"Rolled back {result.SucceededCount} adjustment(s).",
                    "CANCEL" => $"Cancelled {_selectedRows.Count} adjustment(s).",
                    _ => "adjustment(s) deleted."
                };
                ConfirmVisible = false;
                _selectedRows.Clear();
                await ReloadGridAsync();
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? $"Unable to {ConfirmAction.ToLowerInvariant()} adjustment(s).";
                ConfirmVisible = false;
                if (result.SucceededCount > 0)
                {
                    await ReloadGridAsync();
                }
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    public void Dispose()
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
    }

    protected static string StatusChipClass(string? status)
    {
        if (string.Equals(status, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            return "is-hold";
        }

        if (string.Equals(status, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            return "is-on";
        }

        return "is-off";
    }

    private Task BeginDeleteAsync()
    {
        if (!CanDelete)
        {
            StatusMessage = "Access Denied!!";
            return Task.CompletedTask;
        }

        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return Task.CompletedTask;
        }

        var notNew = _selectedRows
            .Where(x => !string.Equals(x.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.BatchNo)
            .ToList();
        if (notNew.Count > 0)
        {
            ErrorMessage = $"Only NEW adjustments can be deleted. Non-NEW: {string.Join(", ", notNew)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "DELETE";
        ConfirmMessage = $"Permanently delete {_selectedRows.Count} selected adjustment(s)?";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private Task BeginCancelAsync()
    {
        if (!CanCancel)
        {
            StatusMessage = "Access Denied!!";
            return Task.CompletedTask;
        }

        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return Task.CompletedTask;
        }

        var notNew = _selectedRows
            .Where(x => !string.Equals(x.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.BatchNo)
            .ToList();
        if (notNew.Count > 0)
        {
            ErrorMessage = $"Only NEW adjustments can be cancelled. Non-NEW: {string.Join(", ", notNew)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "CANCEL";
        ConfirmMessage = $"Cancel {_selectedRows.Count} selected adjustment(s)? Status will be set to CANCELLED.";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private Task BeginPostAsync()
    {
        if (!CanPost)
        {
            StatusMessage = "Access Denied!!";
            return Task.CompletedTask;
        }

        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return Task.CompletedTask;
        }

        if (_selectedRows.Count > IvPostingLimits.MaxPostSelection)
        {
            ErrorMessage = $"Select at most {IvPostingLimits.MaxPostSelection} adjustments to post.";
            return Task.CompletedTask;
        }

        var notNew = _selectedRows
            .Where(x => !string.Equals(x.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.BatchNo)
            .ToList();
        if (notNew.Count > 0)
        {
            ErrorMessage = $"Only NEW adjustments can be posted. Non-NEW: {string.Join(", ", notNew)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "POST";
        ConfirmMessage = $"Post {_selectedRows.Count} selected adjustment(s) from stock?";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private Task BeginRollbackAsync()
    {
        if (!CanRollback)
        {
            StatusMessage = "Access Denied!!";
            return Task.CompletedTask;
        }

        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return Task.CompletedTask;
        }

        if (_selectedRows.Count > IvPostingLimits.MaxPostSelection)
        {
            ErrorMessage = $"Select at most {IvPostingLimits.MaxPostSelection} adjustments to roll back.";
            return Task.CompletedTask;
        }

        var notPosted = _selectedRows
            .Where(x => !string.Equals(x.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.BatchNo)
            .ToList();
        if (notPosted.Count > 0)
        {
            ErrorMessage = $"Only POSTED adjustments can be rolled back. Non-POSTED: {string.Join(", ", notPosted)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "ROLLBACK";
        ConfirmMessage = $"Roll back {_selectedRows.Count} selected adjustment(s)? Stock will be restored by the posted quantities.";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private async Task ReloadGridAsync()
    {
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncDataSourceFilters()
    {
        DataSource.UpdateFilters(new IvStockAdjustmentListQuery
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            BatchStatus = AppliedStatus,
            DateFrom = AppliedDateFrom,
            DateTo = AppliedDateTo,
            SortDescending = true
        });
    }

    private async Task RefreshCompactPreviewAsync()
    {
        var query = DataSource.CurrentQuery;
        query.Skip = 0;
        query.Take = 50;
        var result = await StockAdjustment.SearchAsync(query);
        if (result.Succeeded && result.ListPage is not null)
        {
            CompactRows = result.ListPage.Rows.ToList();
            TotalCount = result.ListPage.TotalCount;
        }
        else
        {
            CompactRows = [];
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                ErrorMessage = result.ErrorMessage;
            }
        }
    }

    private async Task<(IReadOnlyList<IvStockAdjustmentListRow> Rows, int TotalCount)> SearchPageAsync(
        IvStockAdjustmentListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await StockAdjustment.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.ListPage is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load adjustments.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.ListPage.TotalCount);
        return (result.ListPage.Rows, result.ListPage.TotalCount);
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}

/// <summary>
/// Server-side paging source for Stock Adjustment via <see cref="IIvStockAdjustmentService.SearchAsync"/>.
/// </summary>
public sealed class IvStockAdjustmentGridDataSource : GridCustomDataSource
{
    private readonly Func<IvStockAdjustmentListQuery, CancellationToken, Task<(IReadOnlyList<IvStockAdjustmentListRow> Rows, int TotalCount)>> _loader;
    private IvStockAdjustmentListQuery _filters = new();

    public IvStockAdjustmentGridDataSource(
        Func<IvStockAdjustmentListQuery, CancellationToken, Task<(IReadOnlyList<IvStockAdjustmentListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public IvStockAdjustmentListQuery CurrentQuery => Clone(_filters);

    public void UpdateFilters(IvStockAdjustmentListQuery query) =>
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
            query.SortField = MapSortField(sort.FieldName);
            query.SortDescending = sort.DescendingSortOrder;
        }

        var (rows, _) = await _loader(query, cancellationToken);
        return rows.ToList();
    }

    private static string? MapSortField(string? fieldName)
    {
        if (string.Equals(fieldName, nameof(IvStockAdjustmentListRow.TrxDate), StringComparison.OrdinalIgnoreCase))
        {
            return "TrxDtTime";
        }

        return fieldName;
    }

    private static IvStockAdjustmentListQuery Clone(IvStockAdjustmentListQuery source) =>
        new()
        {
            SearchText = source.SearchText,
            BatchStatus = source.BatchStatus,
            DateFrom = source.DateFrom,
            DateTo = source.DateTo,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}
