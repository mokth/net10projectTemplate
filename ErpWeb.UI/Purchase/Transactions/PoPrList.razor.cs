using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Purchase.Transactions;

public partial class PoPrList : PageBase, IDisposable
{
    [Inject] private IPoPrService Prs { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<PoPrListRow> _selectedRows = [];

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmDeleteVisible;
    protected bool ConfirmCancelVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<PoPrListRow> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;
    protected bool CanCancel;

    protected string? AppliedStatus;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;
    protected string DraftStatusKey = "all";
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;

    protected string ConfirmMessage { get; set; } = string.Empty;
    protected string CancelReason { get; set; } = string.Empty;

    protected PoPrGridDataSource DataSource { get; private set; } = default!;
    protected string TotalCountLabel => TotalCount == 1 ? "1 requisition" : $"{TotalCount:N0} requisitions";
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedDateFrom is not null
        || AppliedDateTo is not null;

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        new(PoPrStatuses.New, "NEW"),
        new(PoPrStatuses.Open, "OPEN"),
        new(PoPrStatuses.Approved, "APPROVED"),
        new(PoPrStatuses.PartiallyOrdered, "PARTIALLY ORDERED"),
        new(PoPrStatuses.FullyOrdered, "FULLY ORDERED"),
        new(PoPrStatuses.Cancelled, "CANCELLED")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "PR No.", FieldName = nameof(PoPrListRow.PrNo), Width = "140px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Date", FieldName = nameof(PoPrListRow.CreateDt), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 2 },
        new() { Caption = "Status", FieldName = nameof(PoPrListRow.Status), Width = "140px", VisibleIndex = 3 },
        new() { Caption = "Requester", FieldName = nameof(PoPrListRow.Requester), Width = "120px", VisibleIndex = 4 },
        new() { Caption = "Dept", FieldName = nameof(PoPrListRow.DeptCode), Width = "100px", VisibleIndex = 5 },
        new() { Caption = "Type", FieldName = nameof(PoPrListRow.PrType), Width = "110px", VisibleIndex = 6 },
        new() { Caption = "Remaining", FieldName = nameof(PoPrListRow.RemainingQty), Width = "110px", DisplayFormat = "n4", VisibleIndex = 7 },
        new() { Caption = "Currency", FieldName = nameof(PoPrListRow.Currency), Width = "90px", VisibleIndex = 8 },
        new() { Caption = "Total", FieldName = nameof(PoPrListRow.Total), Width = "110px", VisibleIndex = 9, DisplayFormat = "n2" },
        new() { Caption = "Lines", FieldName = nameof(PoPrListRow.LineCount), Width = "80px", VisibleIndex = 10 },
        new() { Caption = "PO No.", FieldName = nameof(PoPrListRow.PoNo), Width = "120px", VisibleIndex = 11 },
        new() { Caption = "Remarks", FieldName = nameof(PoPrListRow.Remarks), VisibleIndex = 12 }
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new PoPrGridDataSource(SearchPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.PurchaseRequisition, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PurchaseRequisition, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.PurchaseRequisition, PermissionCodes.Delete);
        CanCancel = await AccessRights.CanAsync(MenuCodes.PurchaseRequisition, PermissionCodes.Cancel);
        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "COPY", IConClass = "fa-regular fa-copy", Style = "secondary", Enabled = CanAdd },
            new() { Text = "CANCEL", IConClass = "fas fa-ban", Style = "warning", Enabled = CanCancel },
            new() { Text = "DELETE", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete }
        ];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View purchase requisition" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit purchase requisition", Enabled = CanEdit }
        ];
        SyncToolbarEnabled();
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<PoPrListRow> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
        SyncToolbarEnabled();
    }

    private void SyncToolbarEnabled()
    {
        foreach (var button in Buttons)
        {
            var text = (button.Text ?? string.Empty).ToUpperInvariant();
            button.Enabled = text switch
            {
                "NEW" => CanAdd,
                "COPY" => CanAdd && _selectedRows.Count == 1,
                "DELETE" => CanDelete
                    && _selectedRows.Count > 0
                    && _selectedRows.All(CanDeleteRow),
                "CANCEL" => CanCancel
                    && _selectedRows.Count > 0
                    && _selectedRows.All(CanCancelRow),
                _ => button.Enabled
            };
        }
    }

    private static bool CanDeleteRow(PoPrListRow row) =>
        row.CanDelete
        && !row.HasConsumedLines
        && string.IsNullOrWhiteSpace(row.PoNo);

    private static bool CanCancelRow(PoPrListRow row) =>
        row.CanCancel
        && !row.HasConsumedLines
        && string.IsNullOrWhiteSpace(row.PoNo);

    protected async Task OnButtonClick(SelectedButtonInfo<PoPrListRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW":
                if (!CanAdd) { StatusMessage = "Access Denied!!"; return; }
                Navigation.NavigateTo("/purchase/requisitions/new");
                break;
            case "COPY":
                BeginCopy();
                break;
            case "DELETE":
                await BeginDeleteAsync();
                break;
            case "CANCEL":
                await BeginCancelAsync();
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoPrListRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (mode == "VIEW")
        {
            NavigateView(info.SelectedRow.PrNo);
        }
        else if (mode == "EDIT")
        {
            if (!CanEdit)
            {
                StatusMessage = "Access Denied!!";
                return Task.CompletedTask;
            }

            if (!info.SelectedRow.CanEdit)
            {
                ErrorMessage = $"Purchase requisition {info.SelectedRow.PrNo} cannot be edited ({info.SelectedRow.Status}).";
                return Task.CompletedTask;
            }

            Navigation.NavigateTo($"/purchase/requisitions/edit/{info.SelectedRow.PrNo}");
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(string prNo) =>
        Navigation.NavigateTo($"/purchase/requisitions/view/{prNo}");

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

    protected async Task ConfirmDeleteAsync()
    {
        if (IsSubmitting || _selectedRows.Count == 0)
        {
            ConfirmDeleteVisible = false;
            return;
        }

        using var blocking = BeginBlockingWork("Please wait. This action is still running.");
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var failed = new List<string>();
            var deleted = 0;
            foreach (var row in _selectedRows.ToList())
            {
                var result = await Prs.DeleteAsync(new PoPrKeyedRequest
                {
                    PrNo = row.PrNo,
                    RowVersion = row.RowVersion
                });
                if (result.Succeeded)
                {
                    deleted++;
                }
                else
                {
                    failed.Add($"{row.PrNo}: {result.ErrorMessage ?? "Unable to delete."}");
                }
            }

            if (failed.Count == 0)
            {
                StatusMessage = deleted == 1 ? "Purchase requisition deleted." : $"{deleted} purchase requisition(s) deleted.";
                ConfirmDeleteVisible = false;
                _selectedRows.Clear();
                await ReloadGridAsync();
            }
            else
            {
                ErrorMessage = string.Join("\n", failed);
                ConfirmDeleteVisible = false;
                await ReloadGridAsync();
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task ConfirmCancelAsync()
    {
        if (IsSubmitting || _selectedRows.Count == 0)
        {
            ConfirmCancelVisible = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(CancelReason))
        {
            ErrorMessage = "Cancel reason is required.";
            return;
        }

        using var blocking = BeginBlockingWork("Please wait. This action is still running.");
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var reason = CancelReason.Trim();
            var failed = new List<string>();
            var cancelled = 0;
            foreach (var row in _selectedRows.ToList())
            {
                var result = await Prs.CancelAsync(new PoPrCancelRequest
                {
                    PrNo = row.PrNo,
                    RowVersion = row.RowVersion,
                    ApprReason = reason
                });
                if (result.Succeeded)
                {
                    cancelled++;
                }
                else
                {
                    failed.Add($"{row.PrNo}: {result.ErrorMessage ?? "Unable to cancel."}");
                }
            }

            if (failed.Count == 0)
            {
                StatusMessage = cancelled == 1
                    ? "Purchase requisition cancelled."
                    : $"{cancelled} purchase requisition(s) cancelled.";
                ConfirmCancelVisible = false;
                CancelReason = string.Empty;
                _selectedRows.Clear();
                await ReloadGridAsync();
            }
            else
            {
                ErrorMessage = string.Join("\n", failed);
                ConfirmCancelVisible = false;
                await ReloadGridAsync();
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

    protected static string StatusChipClass(string? status) =>
        string.Equals(status, PoPrStatuses.Approved, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, PoPrStatuses.FullyOrdered, StringComparison.OrdinalIgnoreCase) ? "is-on" :
        string.Equals(status, PoPrStatuses.Cancelled, StringComparison.OrdinalIgnoreCase) ? "is-off" :
        string.Equals(status, PoPrStatuses.PartiallyOrdered, StringComparison.OrdinalIgnoreCase) ? "is-hold" : "is-hold";

    private Task BeginDeleteAsync()
    {
        if (!CanDelete) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }

        var blocked = _selectedRows
            .Where(x => !CanDeleteRow(x))
            .Select(DescribeBlocked)
            .ToList();
        if (blocked.Count > 0)
        {
            ErrorMessage = "Cannot delete:\n" + string.Join("\n", blocked);
            return Task.CompletedTask;
        }

        ConfirmMessage = $"Permanently delete {_selectedRows.Count} selected purchase requisition(s)?";
        ConfirmDeleteVisible = true;
        return Task.CompletedTask;
    }

    private Task BeginCancelAsync()
    {
        if (!CanCancel) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }

        var blocked = _selectedRows
            .Where(x => !CanCancelRow(x))
            .Select(DescribeBlocked)
            .ToList();
        if (blocked.Count > 0)
        {
            ErrorMessage = "Cannot cancel:\n" + string.Join("\n", blocked);
            return Task.CompletedTask;
        }

        CancelReason = string.Empty;
        ConfirmMessage = $"Cancel {_selectedRows.Count} selected purchase requisition(s)? Status will be set to CANCELLED.";
        ConfirmCancelVisible = true;
        return Task.CompletedTask;
    }

    private void BeginCopy()
    {
        if (!CanAdd) { StatusMessage = "Access Denied!!"; return; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return; }
        if (_selectedRows.Count != 1)
        {
            ErrorMessage = "Select exactly one purchase requisition to copy.";
            return;
        }

        Navigation.NavigateTo($"/purchase/requisitions/copy/{_selectedRows[0].PrNo}");
    }

    private static string DescribeBlocked(PoPrListRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.PoNo) || row.HasConsumedLines)
        {
            return $"{row.PrNo} — linked to a purchase order";
        }

        if (!string.Equals(row.Status, PoPrStatuses.New, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(row.Status, PoPrStatuses.Open, StringComparison.OrdinalIgnoreCase))
        {
            return $"{row.PrNo} — not NEW/OPEN ({row.Status})";
        }

        return $"{row.PrNo} — not eligible";
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
        DataSource.UpdateFilters(new PoPrListQuery
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
        var result = await Prs.SearchAsync(query);
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

    private async Task<(IReadOnlyList<PoPrListRow> Rows, int TotalCount)> SearchPageAsync(
        PoPrListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Prs.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.ListPage is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load purchase requisitions.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.ListPage.TotalCount);
        return (result.ListPage.Rows, result.ListPage.TotalCount);
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}

public sealed class PoPrGridDataSource : GridCustomDataSource
{
    private readonly Func<PoPrListQuery, CancellationToken, Task<(IReadOnlyList<PoPrListRow> Rows, int TotalCount)>> _loader;
    private PoPrListQuery _filters = new();

    public PoPrGridDataSource(
        Func<PoPrListQuery, CancellationToken, Task<(IReadOnlyList<PoPrListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public PoPrListQuery CurrentQuery => Clone(_filters);
    public void UpdateFilters(PoPrListQuery query) => _filters = Clone(query);

    public override async Task<int> GetItemCountAsync(GridCustomDataSourceCountOptions options, CancellationToken cancellationToken)
    {
        var query = Clone(_filters);
        query.Skip = 0;
        query.Take = 1;
        var (_, total) = await _loader(query, cancellationToken);
        return total;
    }

    public override async Task<IList> GetItemsAsync(GridCustomDataSourceItemsOptions options, CancellationToken cancellationToken)
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

    private static PoPrListQuery Clone(PoPrListQuery source) =>
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
