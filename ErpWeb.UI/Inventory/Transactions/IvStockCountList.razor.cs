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

public partial class IvStockCountList : PageBase, IDisposable
{
    [Inject] private IIvStockCountService StockCount { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<IvStockCountListRow> _selectedRows = [];

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<IvStockCountListRow> CompactRows { get; set; } = [];

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
    protected string ConfirmReason { get; set; } = string.Empty;
    protected bool ConfirmNeedsReason => string.Equals(ConfirmAction, "ROLLBACK", StringComparison.Ordinal);

    protected string ConfirmButtonText => ConfirmAction switch
    {
        "POST" => "Post",
        "ROLLBACK" => "Rollback",
        "CANCEL" => "Cancel counts",
        _ => "Delete"
    };

    protected ButtonRenderStyle ConfirmButtonStyle => ConfirmAction switch
    {
        "POST" => ButtonRenderStyle.Primary,
        "ROLLBACK" => ButtonRenderStyle.Warning,
        "CANCEL" => ButtonRenderStyle.Warning,
        _ => ButtonRenderStyle.Danger
    };

    protected IvStockCountGridDataSource DataSource { get; private set; } = default!;

    protected string TotalCountLabel => TotalCount == 1 ? "1 sheet" : $"{TotalCount:N0} sheets";

    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedDateFrom is not null
        || AppliedDateTo is not null;

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        new(IvStockCountStatuses.Draft, "DRAFT"),
        new(IvStockCountStatuses.Counted, "COUNTED"),
        new(IvStockCountStatuses.Posted, "POSTED"),
        new(IvStockCountStatuses.RolledBack, "ROLLED_BACK"),
        new(IvStockCountStatuses.Cancelled, "CANCELLED")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Count No", FieldName = nameof(IvStockCountListRow.CountNo), Width = "110px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Count Date", FieldName = nameof(IvStockCountListRow.CountDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 2 },
        new() { Caption = "Status", FieldName = nameof(IvStockCountListRow.Status), Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Warehouse", FieldName = nameof(IvStockCountListRow.WHCode), Width = "100px", VisibleIndex = 4 },
        new() { Caption = "Lines", FieldName = nameof(IvStockCountListRow.LineCount), DataType = "int", Width = "70px", VisibleIndex = 5 },
        new() { Caption = "Counted", FieldName = nameof(IvStockCountListRow.CountedLines), DataType = "int", Width = "80px", VisibleIndex = 6 },
        new() { Caption = "Batch", FieldName = nameof(IvStockCountListRow.PostedBatchNo), DataType = "int", Width = "80px", VisibleIndex = 7 },
        new() { Caption = "Stale", FieldName = nameof(IvStockCountListRow.PostedStaleLines), DataType = "int", Width = "70px", VisibleIndex = 8 },
        new() { Caption = "Counted By", FieldName = nameof(IvStockCountListRow.CountedBy), Width = "100px", VisibleIndex = 9 },
        ..AuditColumns.For(startVisibleIndex: 10)
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new IvStockCountGridDataSource(SearchPageAsync);

        CanAdd = await AccessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Delete);
        CanPost = await AccessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Post);
        CanRollback = await AccessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Rollback);
        CanCancel = await AccessRights.CanAsync(MenuCodes.InventoryStockCount, PermissionCodes.Cancel);

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
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View count sheet" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit count sheet", Enabled = CanEdit },
            new() { Text = "COUNT", IConClass = "fa-solid fa-clipboard-check", Style = "primary", ToolTip = "Enter physical counts", Enabled = CanEdit }
        ];

        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<IvStockCountListRow> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
    }

    protected async Task OnButtonClick(SelectedButtonInfo<IvStockCountListRow> info)
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

                Navigation.NavigateTo("/inventory/stock-count/new");
                break;
            case "DELETE":
                BeginDelete();
                break;
            case "POST":
                BeginPost();
                break;
            case "ROLLBACK":
                BeginRollback();
                break;
            case "CANCEL":
                BeginCancel();
                break;
            case "REFRESH":
                await ReloadGridAsync();
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<IvStockCountListRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return Task.CompletedTask;
        }

        var row = info.SelectedRow;
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "VIEW":
                NavigateView(row.CountNo);
                break;
            case "EDIT":
                if (!CanEdit)
                {
                    StatusMessage = "Access Denied!!";
                    break;
                }

                if (!string.Equals(row.Status, IvStockCountStatuses.Draft, StringComparison.OrdinalIgnoreCase))
                {
                    StatusMessage = "Only DRAFT stock counts can be edited.";
                    NavigateView(row.CountNo);
                    break;
                }

                Navigation.NavigateTo($"/inventory/stock-count/edit/{row.CountNo}");
                break;
            case "COUNT":
                if (!CanEdit)
                {
                    StatusMessage = "Access Denied!!";
                    break;
                }

                if (row.Status is not (IvStockCountStatuses.Draft
                    or IvStockCountStatuses.Counted
                    or IvStockCountStatuses.RolledBack))
                {
                    StatusMessage = $"{row.CountNo} cannot be counted while it is {row.Status}.";
                    break;
                }

                Navigation.NavigateTo($"/inventory/stock-count/count/{row.CountNo}");
                break;
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(string countNo) =>
        Navigation.NavigateTo($"/inventory/stock-count/view/{countNo}");

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

        using var blocking = BeginBlockingWork("Please wait. This action is still running.");

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            string? message;
            bool succeeded;
            var row = _selectedRows[0];

            switch (ConfirmAction)
            {
                case "POST":
                {
                    var result = await StockCount.PostAsync(row.Id);
                    succeeded = result.Succeeded;
                    message = result.Succeeded
                        ? result.PostedBatchNo is int batchNo
                            ? $"Posted {row.CountNo} to batch {batchNo}."
                                + (result.PostedStaleLines > 0 ? $" {result.PostedStaleLines} stale line(s) flagged." : string.Empty)
                            : $"No variance found on {row.CountNo}. Marked POSTED with no batch."
                        : result.ErrorMessage ?? $"Unable to post {row.CountNo}.";
                    break;
                }

                case "ROLLBACK":
                {
                    var result = await StockCount.RollbackAsync(row.Id, ConfirmReason);
                    succeeded = result.Succeeded;
                    message = result.Succeeded
                        ? $"Rolled back {row.CountNo}. Stock restored."
                        : result.ErrorMessage ?? $"Unable to roll back {row.CountNo}.";
                    break;
                }

                case "CANCEL":
                {
                    var ids = _selectedRows.Select(x => x.Id).ToList();
                    var result = await StockCount.CancelAsync(ids, ConfirmReason);
                    succeeded = result.Succeeded;
                    message = result.Succeeded
                        ? $"Cancelled {ids.Count} stock count(s)."
                        : result.ErrorMessage ?? "Unable to cancel the selected stock counts.";
                    break;
                }

                default:
                {
                    var ids = _selectedRows.Select(x => x.Id).ToList();
                    var result = await StockCount.DeleteAsync(ids);
                    succeeded = result.Succeeded;
                    message = result.Succeeded
                        ? $"Deleted {ids.Count} stock count(s)."
                        : result.ErrorMessage ?? "Unable to delete the selected stock counts.";
                    break;
                }
            }

            if (succeeded)
            {
                StatusMessage = message;
                ConfirmVisible = false;
                _selectedRows.Clear();
                await ReloadGridAsync();
            }
            else
            {
                ErrorMessage = message;
                ConfirmVisible = false;
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

    protected static string StatusChipClass(string? status) => status?.ToUpperInvariant() switch
    {
        IvStockCountStatuses.Posted => "is-on",
        IvStockCountStatuses.Draft => "is-hold",
        IvStockCountStatuses.Counted => "is-hold",
        _ => "is-off"
    };

    private void BeginDelete()
    {
        if (!CanDelete)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return;
        }

        var notDraft = _selectedRows
            .Where(x => !string.Equals(x.Status, IvStockCountStatuses.Draft, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.CountNo)
            .ToList();
        if (notDraft.Count > 0)
        {
            ErrorMessage = $"Only DRAFT stock counts can be deleted. Not DRAFT: {string.Join(", ", notDraft)}";
            return;
        }

        ConfirmAction = "DELETE";
        ConfirmReason = string.Empty;
        ConfirmMessage = $"Permanently delete {_selectedRows.Count} selected stock count(s)? The count evidence is lost.";
        ConfirmVisible = true;
    }

    private void BeginCancel()
    {
        if (!CanCancel)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return;
        }

        var notCancellable = _selectedRows
            .Where(x => x.Status is not (IvStockCountStatuses.Draft
                or IvStockCountStatuses.Counted
                or IvStockCountStatuses.RolledBack))
            .Select(x => x.CountNo)
            .ToList();
        if (notCancellable.Count > 0)
        {
            ErrorMessage = $"Only DRAFT, COUNTED or ROLLED_BACK counts can be cancelled. Not cancellable: {string.Join(", ", notCancellable)}";
            return;
        }

        ConfirmAction = "CANCEL";
        ConfirmReason = string.Empty;
        ConfirmMessage = $"Cancel {_selectedRows.Count} selected stock count(s)? Cancelling is terminal.";
        ConfirmVisible = true;
    }

    private void BeginPost()
    {
        if (!CanPost)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (!RequireSingleSelection(out var row))
        {
            return;
        }

        if (row!.Status is not (IvStockCountStatuses.Counted or IvStockCountStatuses.RolledBack))
        {
            ErrorMessage = $"Only a COUNTED or ROLLED_BACK sheet can be posted. {row.CountNo} is {row.Status}.";
            return;
        }

        ConfirmAction = "POST";
        ConfirmReason = string.Empty;
        ConfirmMessage = $"Post {row.CountNo}? Every counted line is adjusted against LIVE stock in one transaction.";
        ConfirmVisible = true;
    }

    private void BeginRollback()
    {
        if (!CanRollback)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (!RequireSingleSelection(out var row))
        {
            return;
        }

        if (!string.Equals(row!.Status, IvStockCountStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = $"Only a POSTED sheet can be rolled back. {row.CountNo} is {row.Status}.";
            return;
        }

        ConfirmAction = "ROLLBACK";
        ConfirmReason = string.Empty;
        ConfirmMessage = $"Roll back {row.CountNo}? Stock is restored by the posted quantities.";
        ConfirmVisible = true;
    }

    private bool RequireSingleSelection(out IvStockCountListRow? row)
    {
        row = null;
        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return false;
        }

        if (_selectedRows.Count > 1)
        {
            ErrorMessage = "Select exactly one stock count for this action.";
            return false;
        }

        if (_selectedRows.Count > IvPostingLimits.MaxPostSelection)
        {
            ErrorMessage = $"Select at most {IvPostingLimits.MaxPostSelection} records.";
            return false;
        }

        row = _selectedRows[0];
        return true;
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
        DataSource.UpdateFilters(new IvStockCountListQuery
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            Status = AppliedStatus,
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
        var result = await StockCount.SearchAsync(query);
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

    private async Task<(IReadOnlyList<IvStockCountListRow> Rows, int TotalCount)> SearchPageAsync(
        IvStockCountListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await StockCount.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.ListPage is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load stock counts.";
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
/// Server-side paging source for Stock Count via <see cref="IIvStockCountService.SearchAsync"/>.
/// </summary>
public sealed class IvStockCountGridDataSource : GridCustomDataSource
{
    private readonly Func<IvStockCountListQuery, CancellationToken, Task<(IReadOnlyList<IvStockCountListRow> Rows, int TotalCount)>> _loader;
    private IvStockCountListQuery _filters = new();

    public IvStockCountGridDataSource(
        Func<IvStockCountListQuery, CancellationToken, Task<(IReadOnlyList<IvStockCountListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public IvStockCountListQuery CurrentQuery => Clone(_filters);

    public void UpdateFilters(IvStockCountListQuery query) =>
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

    private static IvStockCountListQuery Clone(IvStockCountListQuery source) =>
        new()
        {
            SearchText = source.SearchText,
            Status = source.Status,
            DateFrom = source.DateFrom,
            DateTo = source.DateTo,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}
