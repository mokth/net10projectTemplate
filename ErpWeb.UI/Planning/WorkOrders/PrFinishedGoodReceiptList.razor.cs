using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Planning.WorkOrders;

public partial class PrFinishedGoodReceiptList : PageBase, IDisposable
{
    [Inject] private IProductionFinishedGoodReceiptService Receipts { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<FinishedGoodReceiptSummary> _selectedRows = [];
    private readonly Dictionary<(int, string), FinishedGoodReceiptCommand> _requests = [];

    protected bool IsBootstrapping = true;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected bool IsApplyingAction;
    protected bool PostingEnabled;
    protected string ConfirmAction = string.Empty;
    protected string ConfirmMessage = string.Empty;
    protected string ConfirmReason = string.Empty;
    protected bool ConfirmNeedsReason =>
        string.Equals(ConfirmAction, "ROLLBACK", StringComparison.Ordinal);
    protected string ConfirmButtonText => ConfirmAction switch
    {
        "POST" => "Post",
        "ROLLBACK" => "Rollback",
        _ => "Confirm"
    };
    protected ButtonRenderStyle ConfirmButtonStyle => ConfirmAction switch
    {
        "POST" => ButtonRenderStyle.Primary,
        "ROLLBACK" => ButtonRenderStyle.Warning,
        _ => ButtonRenderStyle.Primary
    };

    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected bool CanAdd;
    protected bool CanPost;
    protected bool CanEdit;
    protected bool CanRollback;
    protected List<FinishedGoodReceiptSummary> CompactRows { get; set; } = [];
    protected FinishedGoodReceiptGridDataSource DataSource { get; private set; } = default!;
    protected string? AppliedWorkOrderNo;
    protected string? AppliedStatus;
    protected string DraftWorkOrderNo = string.Empty;
    protected string DraftStatus = string.Empty;
    protected string TotalCountLabel => TotalCount == 1 ? "1 document" : $"{TotalCount:N0} documents";
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedWorkOrderNo)
        || !string.IsNullOrWhiteSpace(AppliedStatus);

    protected IReadOnlyList<FilterOption> StatusOptions { get; } =
    [
        new(string.Empty, "All statuses"),
        new("NEW", "New"),
        new("POSTED", "Posted"),
        new("REVERSED", "Reversed")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Receipt", FieldName = nameof(FinishedGoodReceiptSummary.BatchNo), Width = "110px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Date", FieldName = nameof(FinishedGoodReceiptSummary.EffectiveDate), DataType = "date", DisplayFormat = "dd MMM yyyy", Width = "120px", VisibleIndex = 2 },
        new() { Caption = "Work Order", FieldName = nameof(FinishedGoodReceiptSummary.WorkOrderNo), Width = "140px", VisibleIndex = 3 },
        new() { Caption = "Item", FieldName = nameof(FinishedGoodReceiptSummary.ItemSummary), Width = "120px", VisibleIndex = 4 },
        new() { Caption = "Lot", FieldName = nameof(FinishedGoodReceiptSummary.LotSummary), Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Qty", FieldName = nameof(FinishedGoodReceiptSummary.QtySummary), Width = "130px", VisibleIndex = 6 },
        new() { Caption = "Warehouse", FieldName = nameof(FinishedGoodReceiptSummary.WarehouseSummary), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Status", FieldName = nameof(FinishedGoodReceiptSummary.Status), Width = "100px", VisibleIndex = 8 }
    ];

    protected List<ButtonInfo> Buttons { get; private set; } = [];
    protected List<ButtonInfo> ActionButtons { get; private set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new FinishedGoodReceiptGridDataSource(LoadPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Add);
        CanPost = await AccessRights.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Post);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Edit);
        CanRollback = await AccessRights.CanAsync(MenuCodes.PlanningFinishedGoodReceipt, PermissionCodes.Rollback);

        SyncFilters();
        await RefreshCompactAsync();
        RebuildButtons();
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View finished good receipt" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit draft", Enabled = CanEdit }
        ];
        IsBootstrapping = false;
    }

    private void RebuildButtons()
    {
        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "POST", IConClass = "fas fa-check", Style = "success", Enabled = CanPost && PostingEnabled },
            new() { Text = "ROLLBACK", IConClass = "fas fa-rotate-left", Style = "warning", Enabled = CanRollback }
        ];
    }

    protected void OnGridInstance(DxGrid grid) => _grid = grid;

    protected void OnSelectionsEvent(List<FinishedGoodReceiptSummary> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
    }

    protected async Task OnButtonClick(SelectedButtonInfo<FinishedGoodReceiptSummary> info)
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

                Navigation.NavigateTo("/planning/finished-good-receipts/new");
                break;
            case "POST":
                await BeginPostAsync();
                break;
            case "ROLLBACK":
                await BeginRollbackAsync();
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<FinishedGoodReceiptSummary> info)
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
                OpenView(info.SelectedRow.Id);
                break;
            case "EDIT":
                if (!CanEdit)
                {
                    StatusMessage = "Access Denied!!";
                    break;
                }

                if (!string.Equals(info.SelectedRow.Status, "NEW", StringComparison.OrdinalIgnoreCase))
                {
                    StatusMessage = "Only NEW documents can be edited.";
                    OpenView(info.SelectedRow.Id);
                    break;
                }

                Navigation.NavigateTo($"/planning/finished-good-receipts/{info.SelectedRow.Id}/edit");
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

        if (ConfirmAction == "ROLLBACK")
        {
            var rollbackReason = (ConfirmReason ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(rollbackReason))
            {
                ErrorMessage = "Rollback reason is required.";
                return;
            }

            if (rollbackReason.Length > 500)
            {
                ErrorMessage = "Rollback reason cannot exceed 500 characters.";
                return;
            }
        }

        IsApplyingAction = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var succeeded = 0;
            string? firstError = null;

            foreach (var row in _selectedRows.ToArray())
            {
                var key = (row.Id, ConfirmAction);
                if (!_requests.TryGetValue(key, out var request))
                {
                    request = new(row.Id, row.RowVersion ?? [], Guid.NewGuid(), ConfirmAction == "ROLLBACK" ? ConfirmReason.Trim() : null);
                    _requests[key] = request;
                }

                var result = ConfirmAction == "POST"
                    ? await Receipts.PostAsync(request)
                    : await Receipts.RollbackAsync(request);
                if (result.Succeeded)
                {
                    succeeded++;
                    _requests.Remove(key);
                }
                else
                    firstError ??= result.Message ?? $"Unable to {ConfirmAction.ToLowerInvariant()} FG {row.BatchNo}.";
            }

            if (succeeded == _selectedRows.Count)
            {
                StatusMessage = ConfirmAction == "POST"
                    ? $"Posted {succeeded} document(s)."
                    : $"Rolled back {succeeded} document(s).";
                ConfirmVisible = false;
                ConfirmReason = string.Empty;
                _selectedRows.Clear();
                await ReloadAsync();
            }
            else
            {
                ErrorMessage = firstError ?? $"Unable to {ConfirmAction.ToLowerInvariant()} document(s).";
                ConfirmVisible = false;
                if (succeeded > 0)
                    await ReloadAsync();
            }
        }
        catch (Exception)
        {
            ErrorMessage = "The response could not be received. Retry with the same action to resolve its saved request.";
        }
        finally
        {
            IsApplyingAction = false;
        }
    }

    protected void OpenView(int id) =>
        Navigation.NavigateTo($"/planning/finished-good-receipts/{id}/view");

    protected void OpenFilterPopup()
    {
        DraftWorkOrderNo = AppliedWorkOrderNo ?? string.Empty;
        DraftStatus = AppliedStatus ?? string.Empty;
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedWorkOrderNo = NullIfEmpty(DraftWorkOrderNo);
        AppliedStatus = NullIfEmpty(DraftStatus);
        FilterPopupVisible = false;
        await ReloadAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftWorkOrderNo = DraftStatus = string.Empty;
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

    private Task BeginPostAsync()
    {
        if (!CanPost || !PostingEnabled)
        {
            StatusMessage = PostingEnabled ? "Access Denied!!" : "FG posting is disabled pending release acceptance.";
            return Task.CompletedTask;
        }

        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return Task.CompletedTask;
        }

        var notNew = _selectedRows
            .Where(x => !string.Equals(x.Status, "NEW", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.BatchNo.ToString())
            .ToList();
        if (notNew.Count > 0)
        {
            ErrorMessage = $"Only NEW documents can be posted. Non-NEW: {string.Join(", ", notNew)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "POST";
        ConfirmMessage = $"Post {_selectedRows.Count} selected finished good receipt(s)?";
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

        var notPosted = _selectedRows
            .Where(x => !string.Equals(x.Status, "POSTED", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.BatchNo.ToString())
            .ToList();
        if (notPosted.Count > 0)
        {
            ErrorMessage = $"Only POSTED documents can be rolled back. Non-POSTED: {string.Join(", ", notPosted)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "ROLLBACK";
        ConfirmReason = string.Empty;
        ConfirmMessage = $"Roll back {_selectedRows.Count} selected finished good receipt(s)?";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private async Task ReloadAsync()
    {
        SyncFilters();
        await RefreshCompactAsync();
        RebuildButtons();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncFilters() => DataSource.UpdateFilters(new FinishedGoodReceiptQuery
    {
        SearchText = NullIfEmpty(SearchText),
        WorkOrderNo = AppliedWorkOrderNo,
        Status = AppliedStatus
    });

    private async Task<(IReadOnlyList<FinishedGoodReceiptSummary>, int)> LoadPageAsync(
        FinishedGoodReceiptQuery query, CancellationToken ct)
    {
        var result = await Receipts.SearchAsync(query, ct);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() => ErrorMessage = result.Message ?? "Unable to load finished good receipts.");
            return ([], 0);
        }

        await InvokeAsync(() =>
        {
            TotalCount = result.Data.TotalCount;
            PostingEnabled = result.Data.PostingEnabled;
        });
        return (result.Data.Rows, result.Data.TotalCount);
    }

    private async Task RefreshCompactAsync()
    {
        var query = DataSource.CurrentQuery;
        query.Skip = 0;
        query.Take = 50;
        var result = await Receipts.SearchAsync(query);
        CompactRows = result.Data?.Rows.ToList() ?? [];
        TotalCount = result.Data?.TotalCount ?? 0;
        PostingEnabled = result.Data?.PostingEnabled ?? false;
        RebuildButtons();
        if (!result.Succeeded)
            ErrorMessage = result.Message ?? "Unable to load finished good receipts.";
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

public sealed class FinishedGoodReceiptGridDataSource : GridCustomDataSource
{
    private readonly Func<FinishedGoodReceiptQuery, CancellationToken, Task<(IReadOnlyList<FinishedGoodReceiptSummary>, int)>> _loader;
    private FinishedGoodReceiptQuery _filters = new();

    public FinishedGoodReceiptGridDataSource(
        Func<FinishedGoodReceiptQuery, CancellationToken, Task<(IReadOnlyList<FinishedGoodReceiptSummary>, int)>> loader)
        => _loader = loader;

    public FinishedGoodReceiptQuery CurrentQuery => Clone(_filters);

    public void UpdateFilters(FinishedGoodReceiptQuery query) => _filters = Clone(query);

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
        var (rows, _) = await _loader(q, ct);
        return rows.ToList();
    }

    private static FinishedGoodReceiptQuery Clone(FinishedGoodReceiptQuery x) => new()
    {
        SearchText = x.SearchText,
        WorkOrderNo = x.WorkOrderNo,
        Status = x.Status,
        WorkOrderId = x.WorkOrderId,
        Skip = x.Skip,
        Take = x.Take
    };
}
