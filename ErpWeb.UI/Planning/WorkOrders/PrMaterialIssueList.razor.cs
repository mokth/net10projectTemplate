using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
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
    private readonly List<ProductionMaterialIssueListRow> _selectedRows = [];

    protected bool IsBootstrapping = true;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected bool IsApplyingAction;
    protected string ConfirmAction = string.Empty;
    protected string ConfirmMessage = string.Empty;
    protected string ConfirmReason = string.Empty;
    protected bool ConfirmNeedsReason =>
        string.Equals(ConfirmAction, "ROLLBACK", StringComparison.Ordinal);
    protected string ConfirmButtonText => ConfirmAction switch
    {
        "POST" => "Post",
        "ROLLBACK" => "Rollback",
        "CANCEL" => "Cancel Issues",
        _ => "Delete"
    };
    protected ButtonRenderStyle ConfirmButtonStyle => ConfirmAction switch
    {
        "POST" => ButtonRenderStyle.Primary,
        "ROLLBACK" => ButtonRenderStyle.Warning,
        "CANCEL" => ButtonRenderStyle.Warning,
        _ => ButtonRenderStyle.Danger
    };

    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected bool CanAdd;
    protected bool CanPost;
    protected bool CanEdit;
    protected bool CanCancel;
    protected bool CanDelete;
    protected bool CanRollback;
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
        new(string.Empty, "All statuses"), new("NEW", "New"), new("POSTED", "Posted"), new("CANCELLED", "Cancelled")
    ];
    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Batch No", FieldName = nameof(ProductionMaterialIssueListRow.BatchNo), SortIndex = 0, VisibleIndex = 1, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Issue Date", FieldName = nameof(ProductionMaterialIssueListRow.IssueDate), DataType = "date", DisplayFormat = "dd MMM yyyy", VisibleIndex = 2, Size = GridColumnSize.Date },
        new() { Caption = "Work Order", FieldName = nameof(ProductionMaterialIssueListRow.WorkOrderNo), VisibleIndex = 3, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Product", FieldName = nameof(ProductionMaterialIssueListRow.ProductCode), VisibleIndex = 4, Size = GridColumnSize.Code },
        new() { Caption = "Description", FieldName = nameof(ProductionMaterialIssueListRow.ProductDescription), VisibleIndex = 5, Size = GridColumnSize.LongText },
        new() { Caption = "Status", FieldName = nameof(ProductionMaterialIssueListRow.Status), VisibleIndex = 6, Size = GridColumnSize.Status },
        new() { Caption = "Materials", FieldName = nameof(ProductionMaterialIssueListRow.LineCount), DataType = "number", Width = "85px", VisibleIndex = 7, Size = GridColumnSize.Tiny },
        new() { Caption = "Posted By", FieldName = nameof(ProductionMaterialIssueListRow.PostedBy), VisibleIndex = 8, Size = GridColumnSize.Name },
        new() { Caption = "Posted Date", FieldName = nameof(ProductionMaterialIssueListRow.PostedDate), DataType = "date", DisplayFormat = "dd MMM yyyy HH:mm", VisibleIndex = 9, Size = GridColumnSize.DateTime },
        new() { Caption = "Reversed", FieldName = nameof(ProductionMaterialIssueListRow.RollbackDate), DataType = "date", DisplayFormat = "dd MMM yyyy HH:mm", VisibleIndex = 10, Size = GridColumnSize.DateTime }
    ];
    protected List<ButtonInfo> Buttons { get; private set; } = [];
    protected List<ButtonInfo> ActionButtons { get; private set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new ProductionMaterialIssueGridDataSource(LoadPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Add);
        CanPost = await AccessRights.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Post);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Edit);
        CanCancel = await AccessRights.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Cancel);
        CanDelete = await AccessRights.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Delete);
        CanRollback = await AccessRights.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Rollback);

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
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View issue" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit issue", Enabled = CanEdit }
        ];

        SyncFilters();
        await RefreshCompactAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid grid) => _grid = grid;

    protected void OnSelectionsEvent(List<ProductionMaterialIssueListRow> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
    }

    protected async Task OnButtonClick(SelectedButtonInfo<ProductionMaterialIssueListRow> info)
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

                Navigation.NavigateTo("/planning/material-issues/new");
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
            case "DELETE":
                await BeginDeleteAsync();
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<ProductionMaterialIssueListRow> info)
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
                OpenView(info.SelectedRow.BatchNo);
                break;
            case "EDIT":
                if (!CanEdit)
                {
                    StatusMessage = "Access Denied!!";
                    break;
                }

                if (!string.Equals(info.SelectedRow.Status, "NEW", StringComparison.OrdinalIgnoreCase))
                {
                    StatusMessage = "Only NEW issues can be edited.";
                    OpenView(info.SelectedRow.BatchNo);
                    break;
                }

                Navigation.NavigateTo($"/planning/material-issues/edit/{info.SelectedRow.BatchNo}");
                break;
        }

        return Task.CompletedTask;
    }

    protected async Task ConfirmActionAsync()
    {
        if (IsApplyingAction || _selectedRows.Count == 0)
        {
            ConfirmVisible = false;
            return;
        }

        var action = ConfirmAction;
        string? rollbackReason = null;
        if (action == "ROLLBACK")
        {
            rollbackReason = (ConfirmReason ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(rollbackReason))
            {
                ErrorMessage = "Rollback reason is required.";
                return;
            }

            if (rollbackReason.Length > 250)
            {
                ErrorMessage = "Rollback reason cannot exceed 250 characters.";
                return;
            }
        }

        IsApplyingAction = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var batchNos = _selectedRows.Select(x => x.BatchNo).ToList();

            if (action == "ROLLBACK")
            {
                var succeeded = 0;
                string? firstError = null;
                foreach (var batchNo in batchNos)
                {
                    var rollback = await MaterialIssues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
                    {
                        PostingRequestId = Guid.NewGuid().ToString("N"),
                        InventoryBatchNo = batchNo,
                        Reason = rollbackReason!
                    });
                    if (rollback.Succeeded)
                        succeeded++;
                    else
                    {
                        firstError ??= FormatRollbackError(batchNo, rollback);
                    }
                }

                if (succeeded == batchNos.Count)
                {
                    ConfirmVisible = false;
                    ConfirmReason = string.Empty;
                    _selectedRows.Clear();
                    if (succeeded == 1)
                    {
                        Navigation.NavigateTo($"/planning/material-issues/edit/{batchNos[0]}");
                        return;
                    }

                    StatusMessage = $"Rolled back {succeeded} issue(s). Open each NEW draft to correct and re-post.";
                    await ReloadAsync();
                }
                else
                {
                    ErrorMessage = firstError ?? "Unable to roll back issue(s).";
                    ConfirmVisible = false;
                    if (succeeded > 0)
                        await ReloadAsync();
                }

                return;
            }

            IvMasterOperationResult<ProductionMaterialIssueBatchActionResult> result = action switch
            {
                "POST" => await MaterialIssues.PostAsync(batchNos),
                "CANCEL" => await MaterialIssues.CancelAsync(batchNos),
                "DELETE" => await MaterialIssues.DeleteAsync(batchNos),
                _ => IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>.Fail(
                    IvMasterErrorCode.Validation, "Unsupported action.")
            };

            var succeededCount = result.Data?.SucceededCount
                ?? result.Data?.Batches.Count(x => x.Succeeded)
                ?? 0;
            var failedCount = result.Data?.FailedCount
                ?? result.Data?.Batches.Count(x => !x.Succeeded)
                ?? 0;

            if (result.Succeeded && failedCount == 0)
            {
                StatusMessage = action switch
                {
                    "POST" => $"Posted {succeededCount} issue(s).",
                    "CANCEL" => $"Cancelled {batchNos.Count} issue(s).",
                    _ => "Issue(s) deleted."
                };
                ConfirmVisible = false;
                _selectedRows.Clear();
                await ReloadAsync();
            }
            else
            {
                ErrorMessage = result.Message
                    ?? result.Data?.Batches.FirstOrDefault(x => !x.Succeeded)?.Message
                    ?? $"Unable to {action.ToLowerInvariant()} issue(s).";
                ConfirmVisible = false;
                if (succeededCount > 0)
                    await ReloadAsync();
            }
        }
        finally
        {
            IsApplyingAction = false;
        }
    }

    protected void OpenView(int batchNo) => Navigation.NavigateTo($"/planning/material-issues/view/{batchNo}");

    protected void OpenFilterPopup()
    {
        DraftWorkOrderNo = AppliedWorkOrderNo ?? string.Empty;
        DraftProductCode = AppliedProductCode ?? string.Empty;
        DraftStatus = AppliedStatus ?? string.Empty;
        DraftBatchNo = AppliedBatchNo;
        DraftDateFrom = AppliedDateFrom;
        DraftDateTo = AppliedDateTo;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedWorkOrderNo = NullIfEmpty(DraftWorkOrderNo);
        AppliedProductCode = NullIfEmpty(DraftProductCode);
        AppliedStatus = NullIfEmpty(DraftStatus);
        AppliedBatchNo = DraftBatchNo is > 0 ? DraftBatchNo : null;
        AppliedDateFrom = DraftDateFrom?.Date;
        AppliedDateTo = DraftDateTo?.Date;
        FilterPopupVisible = false;
        await ReloadAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftWorkOrderNo = DraftProductCode = DraftStatus = string.Empty;
        DraftBatchNo = null;
        DraftDateFrom = DraftDateTo = null;
        await ApplyFiltersAsync();
    }

    protected Task OnSearchTextChanged(string value)
    {
        SearchText = value ?? string.Empty;
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
        _searchDebounce = new Timer(400) { AutoReset = false };
        var version = Interlocked.Increment(ref _searchVersion);
        _searchDebounce.Elapsed += async (_, _) =>
        {
            if (version == _searchVersion)
                await InvokeAsync(ReloadAsync);
        };
        _searchDebounce.Start();
        return Task.CompletedTask;
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

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
            .Where(x => !string.Equals(x.Status, "NEW", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.BatchNo)
            .ToList();
        if (notNew.Count > 0)
        {
            ErrorMessage = $"Only NEW issues can be deleted. Non-NEW: {string.Join(", ", notNew)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "DELETE";
        ConfirmMessage = $"Permanently delete {_selectedRows.Count} selected issue(s)?";
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
            .Where(x => !string.Equals(x.Status, "NEW", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.BatchNo)
            .ToList();
        if (notNew.Count > 0)
        {
            ErrorMessage = $"Only NEW issues can be cancelled. Non-NEW: {string.Join(", ", notNew)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "CANCEL";
        ConfirmMessage = $"Cancel {_selectedRows.Count} selected issue(s)? Status will be set to CANCELLED.";
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
            ErrorMessage = $"Select at most {IvPostingLimits.MaxPostSelection} issues to post.";
            return Task.CompletedTask;
        }

        var notNew = _selectedRows
            .Where(x => !string.Equals(x.Status, "NEW", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.BatchNo)
            .ToList();
        if (notNew.Count > 0)
        {
            ErrorMessage = $"Only NEW issues can be posted. Non-NEW: {string.Join(", ", notNew)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "POST";
        ConfirmMessage = $"Post {_selectedRows.Count} selected issue(s) from stock?";
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
            ErrorMessage = $"Select at most {IvPostingLimits.MaxPostSelection} issues to roll back.";
            return Task.CompletedTask;
        }

        var notPosted = _selectedRows
            .Where(x => !string.Equals(x.Status, "POSTED", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.BatchNo)
            .ToList();
        if (notPosted.Count > 0)
        {
            ErrorMessage = $"Only POSTED issues can be rolled back. Non-POSTED: {string.Join(", ", notPosted)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "ROLLBACK";
        ConfirmReason = string.Empty;
        ConfirmMessage = $"Roll back {_selectedRows.Count} selected issue(s)? Stock will be restored and the draft reopened for correction.";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private async Task ReloadAsync()
    {
        SyncFilters();
        await RefreshCompactAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncFilters() => DataSource.UpdateFilters(new ProductionMaterialIssueListQuery
    {
        SearchText = NullIfEmpty(SearchText),
        WorkOrderNo = AppliedWorkOrderNo,
        ProductCode = AppliedProductCode,
        Status = AppliedStatus,
        BatchNo = AppliedBatchNo,
        DateFrom = AppliedDateFrom,
        DateTo = AppliedDateTo
    });

    private async Task<(IReadOnlyList<ProductionMaterialIssueListRow>, int)> LoadPageAsync(
        ProductionMaterialIssueListQuery query,
        CancellationToken ct)
    {
        var result = await MaterialIssues.SearchAsync(query, ct);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() => ErrorMessage = result.Message ?? "Unable to load material issues.");
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.Data.TotalCount);
        return (result.Data.Rows, result.Data.TotalCount);
    }

    private async Task RefreshCompactAsync()
    {
        var query = DataSource.CurrentQuery;
        query.Skip = 0;
        query.Take = 50;
        var result = await MaterialIssues.SearchAsync(query);
        CompactRows = result.Data?.Rows.ToList() ?? [];
        TotalCount = result.Data?.TotalCount ?? 0;
        if (!result.Succeeded)
            ErrorMessage = result.Message ?? "Unable to load material issues.";
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatRollbackError(
        int batchNo,
        IvMasterOperationResult<ProductionMaterialIssueRollbackResult> rollback)
    {
        if (rollback.ValidationErrors.Count > 0)
        {
            var detail = string.Join("; ", rollback.ValidationErrors.Select(x => $"{x.Key}: {x.Value}"));
            return $"Unable to roll back IP {batchNo}. {rollback.Message ?? "Validation failed."} ({detail})";
        }

        return rollback.Message ?? $"Unable to roll back IP {batchNo}.";
    }

    public void Dispose()
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
    }

    protected sealed record FilterOption(string Key, string Name);
}

public sealed class ProductionMaterialIssueGridDataSource : GridCustomDataSource
{
    private readonly Func<ProductionMaterialIssueListQuery, CancellationToken, Task<(IReadOnlyList<ProductionMaterialIssueListRow>, int)>> _loader;
    private ProductionMaterialIssueListQuery _filters = new();

    public ProductionMaterialIssueGridDataSource(
        Func<ProductionMaterialIssueListQuery, CancellationToken, Task<(IReadOnlyList<ProductionMaterialIssueListRow>, int)>> loader)
        => _loader = loader;

    public ProductionMaterialIssueListQuery CurrentQuery => Clone(_filters);

    public void UpdateFilters(ProductionMaterialIssueListQuery query) => _filters = Clone(query);

    public override async Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions options, CancellationToken ct)
    {
        var q = Clone(_filters);
        q.Skip = 0;
        q.Take = 1;
        var (_, count) = await _loader(q, ct);
        return count;
    }

    public override async Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions options, CancellationToken ct)
    {
        var q = Clone(_filters);
        q.Skip = Math.Max(0, options.StartIndex);
        q.Take = Math.Clamp(options.Count <= 0 ? 20 : options.Count, 1, 100);
        if (options.SortInfo is { Count: > 0 })
        {
            q.SortField = options.SortInfo[0].FieldName;
            q.SortDescending = options.SortInfo[0].DescendingSortOrder;
        }

        var (rows, _) = await _loader(q, ct);
        return rows.ToList();
    }

    private static ProductionMaterialIssueListQuery Clone(ProductionMaterialIssueListQuery x) => new()
    {
        SearchText = x.SearchText,
        DateFrom = x.DateFrom,
        DateTo = x.DateTo,
        WorkOrderNo = x.WorkOrderNo,
        ProductCode = x.ProductCode,
        Status = x.Status,
        BatchNo = x.BatchNo,
        SortField = x.SortField,
        SortDescending = x.SortDescending,
        Skip = x.Skip,
        Take = x.Take
    };
}
