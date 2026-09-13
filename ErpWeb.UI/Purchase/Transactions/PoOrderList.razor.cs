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

public partial class PoOrderList : PageBase, IDisposable
{
    [Inject] private IPoOrderService Orders { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<PoOrderListRow> _selectedRows = [];

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<PoOrderListRow> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;
    protected bool CanCancel;
    protected bool CanClose;
    protected bool CanReopen;

    protected string? AppliedStatus;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;
    protected string DraftStatusKey = "all";
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;

    protected string ConfirmTitle { get; set; } = "Confirm";
    protected string ConfirmMessage { get; set; } = string.Empty;
    protected string ConfirmAction { get; set; } = "DELETE";
    protected string CloseReason { get; set; } = string.Empty;
    protected string ConfirmButtonText => ConfirmAction switch
    {
        "CANCEL" => "Cancel PO",
        "CLOSE" => "Close PO",
        "REOPEN" => "Reopen PO",
        _ => "Delete"
    };
    protected ButtonRenderStyle ConfirmButtonStyle =>
        ConfirmAction is "DELETE" or "CANCEL" ? ButtonRenderStyle.Danger : ButtonRenderStyle.Primary;

    protected PoOrderGridDataSource DataSource { get; private set; } = default!;
    protected string TotalCountLabel => TotalCount == 1 ? "1 purchase order" : $"{TotalCount:N0} purchase orders";
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedDateFrom is not null
        || AppliedDateTo is not null;

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        new(PoOrderStatuses.New, "NEW"),
        new(PoOrderStatuses.Open, "OPEN"),
        new(PoOrderStatuses.Received, "RECEIVED"),
        new(PoOrderStatuses.Closed, "CLOSED"),
        new(PoOrderStatuses.Cancelled, "CANCELLED"),
        new(PoOrderStatuses.Pending, "PENDING"),
        new(PoOrderStatuses.Checked, "CHECKED")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Date", FieldName = nameof(PoOrderListRow.PoDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 1 },
        new() { Caption = "PO No", FieldName = nameof(PoOrderListRow.PoNo), Width = "140px", SortIndex = 0, VisibleIndex = 2 },
        new() { Caption = "Rev", FieldName = nameof(PoOrderListRow.PoRelNo), Width = "70px", VisibleIndex = 3 },
        new() { Caption = "Vendor", FieldName = nameof(PoOrderListRow.VendCode), Width = "120px", VisibleIndex = 4 },
        new() { Caption = "Name", FieldName = nameof(PoOrderListRow.VendName), VisibleIndex = 5 },
        new() { Caption = "Amount", FieldName = nameof(PoOrderListRow.Total), Width = "120px", DisplayFormat = "n2", VisibleIndex = 6 },
        new() { Caption = "Buyer", FieldName = nameof(PoOrderListRow.Buyer), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Status", FieldName = nameof(PoOrderListRow.Status), Width = "110px", VisibleIndex = 8 },
        new() { Caption = "ETA", FieldName = nameof(PoOrderListRow.EtaDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 9 },
        new() { Caption = "Currency", FieldName = nameof(PoOrderListRow.CurCode), Width = "90px", VisibleIndex = 10 },
        new() { Caption = "Created", FieldName = nameof(PoOrderListRow.CreatedDate), DataType = "date", DisplayFormat = "dd/MM/yyyy HH:mm", Width = "150px", VisibleIndex = 11 }
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new PoOrderGridDataSource(SearchPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Delete);
        CanCancel = await AccessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Cancel);
        CanClose = await AccessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Close);
        CanReopen = await AccessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.Reopen);
        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "COPY", IConClass = "fa-regular fa-copy", Style = "secondary", Enabled = CanAdd },
            new() { Text = "REVISE", IConClass = "fa-solid fa-code-branch", Style = "secondary", Enabled = CanEdit },
            new() { Text = "CANCEL", IConClass = "fas fa-ban", Style = "warning", Enabled = CanCancel },
            new() { Text = "CLOSE", IConClass = "fas fa-lock", Style = "secondary", Enabled = CanClose },
            new() { Text = "REOPEN", IConClass = "fa-solid fa-lock-open", Style = "secondary", Enabled = CanReopen },
            new() { Text = "DELETE", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete }
        ];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View purchase order" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit purchase order", Enabled = CanEdit }
        ];
        SyncToolbarEnabled();
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<PoOrderListRow> list)
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
                "REVISE" => CanEdit && _selectedRows.Count == 1 && _selectedRows[0].CanRevise,
                "DELETE" => CanDelete && _selectedRows.Count > 0 && _selectedRows.All(x => x.CanDelete),
                "CANCEL" => CanCancel && _selectedRows.Count > 0 && _selectedRows.All(x => x.CanCancel),
                "CLOSE" => CanClose && _selectedRows.Count > 0 && _selectedRows.All(x => x.CanForceClose),
                "REOPEN" => CanReopen && _selectedRows.Count > 0 && _selectedRows.All(x => x.CanReopen),
                _ => button.Enabled
            };
        }
    }

    protected async Task OnButtonClick(SelectedButtonInfo<PoOrderListRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW":
                if (!CanAdd) { StatusMessage = "Access Denied!!"; return; }
                Navigation.NavigateTo("/purchase/orders/new");
                break;
            case "COPY":
                BeginCopy();
                break;
            case "REVISE":
                BeginRevise();
                break;
            case "DELETE":
            case "CANCEL":
            case "CLOSE":
            case "REOPEN":
                BeginConfirmedAction(mode);
                break;
            case "REFRESH":
                await ReloadGridAsync();
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoOrderListRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (mode == "VIEW")
        {
            NavigateView(info.SelectedRow.PoNo);
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
                ErrorMessage = $"Purchase order {info.SelectedRow.PoNo} cannot be edited ({info.SelectedRow.Status}).";
                return Task.CompletedTask;
            }

            Navigation.NavigateTo($"/purchase/orders/edit/{info.SelectedRow.PoNo}");
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(string poNo) =>
        Navigation.NavigateTo($"/purchase/orders/view/{poNo}");

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
            if (ConfirmAction == "CLOSE" && string.IsNullOrWhiteSpace(CloseReason))
            {
                ErrorMessage = "Close reason is required.";
                return;
            }

            var failed = new List<string>();
            var completed = 0;
            foreach (var row in _selectedRows.ToList())
            {
                var request = new PoOrderKeyedRequest
                {
                    PoNo = row.PoNo,
                    PoRelNo = row.PoRelNo,
                    RowVersion = row.RowVersion,
                    CloseReason = ConfirmAction == "CLOSE" ? CloseReason.Trim() : null
                };

                var result = ConfirmAction switch
                {
                    "CANCEL" => await Orders.CancelAsync(request),
                    "CLOSE" => await Orders.ForceCloseAsync(request),
                    "REOPEN" => await Orders.ReopenAsync(request),
                    _ => await Orders.DeleteAsync(request)
                };

                if (result.Succeeded)
                {
                    completed++;
                }
                else
                {
                    failed.Add($"{row.PoNo}: {result.ErrorMessage ?? "Unable to complete."}");
                }
            }

            ConfirmVisible = false;
            if (failed.Count == 0)
            {
                StatusMessage = BuildCompletedMessage(completed);
                _selectedRows.Clear();
            }
            else
            {
                ErrorMessage = string.Join("\n", failed);
            }

            await ReloadGridAsync();
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
        string.Equals(status, PoOrderStatuses.Closed, StringComparison.OrdinalIgnoreCase) ? "is-off" :
        string.Equals(status, PoOrderStatuses.Cancelled, StringComparison.OrdinalIgnoreCase) ? "is-off" :
        string.Equals(status, PoOrderStatuses.Received, StringComparison.OrdinalIgnoreCase) ? "is-on" : "is-hold";

    private void BeginCopy()
    {
        if (!CanAdd) { StatusMessage = "Access Denied!!"; return; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return; }
        if (_selectedRows.Count != 1)
        {
            ErrorMessage = "Select exactly one purchase order to copy.";
            return;
        }

        Navigation.NavigateTo($"/purchase/orders/copy/{_selectedRows[0].PoNo}");
    }

    private void BeginRevise()
    {
        if (!CanEdit) { StatusMessage = "Access Denied!!"; return; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return; }
        if (_selectedRows.Count != 1) { ErrorMessage = "Select exactly one purchase order to revise."; return; }
        if (!_selectedRows[0].CanRevise)
        {
            ErrorMessage = $"Purchase order {_selectedRows[0].PoNo} cannot be revised ({_selectedRows[0].Status}).";
            return;
        }

        Navigation.NavigateTo($"/purchase/orders/revise/{_selectedRows[0].PoNo}");
    }

    private void BeginConfirmedAction(string action)
    {
        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return;
        }

        var blocked = _selectedRows.Where(x => !CanRunAction(x, action)).Select(x => $"{x.PoNo} - not eligible ({x.Status})").ToList();
        if (blocked.Count > 0)
        {
            ErrorMessage = $"Cannot {action.ToLowerInvariant()}:\n" + string.Join("\n", blocked);
            return;
        }

        ConfirmAction = action;
        CloseReason = string.Empty;
        ConfirmTitle = action switch
        {
            "CANCEL" => "Cancel purchase order",
            "CLOSE" => "Close purchase order",
            "REOPEN" => "Reopen purchase order",
            _ => "Confirm delete"
        };
        ConfirmMessage = action switch
        {
            "CANCEL" => $"Cancel {_selectedRows.Count} selected purchase order(s)?",
            "CLOSE" => $"Close {_selectedRows.Count} selected purchase order(s)? Remaining balance will be force-closed. Service-only POs must be force-closed deliberately.",
            "REOPEN" => $"Reopen {_selectedRows.Count} selected purchase order(s)?",
            _ => $"Permanently delete {_selectedRows.Count} selected purchase order(s)?"
        };
        ConfirmVisible = true;
    }

    private bool CanRunAction(PoOrderListRow row, string action) =>
        action switch
        {
            "DELETE" => CanDelete && row.CanDelete,
            "CANCEL" => CanCancel && row.CanCancel,
            "CLOSE" => CanClose && row.CanForceClose,
            "REOPEN" => CanReopen && row.CanReopen,
            _ => false
        };

    private string BuildCompletedMessage(int count) =>
        ConfirmAction switch
        {
            "CANCEL" => count == 1 ? "Purchase order cancelled." : $"{count} purchase order(s) cancelled.",
            "CLOSE" => count == 1 ? "Purchase order closed." : $"{count} purchase order(s) closed.",
            "REOPEN" => count == 1 ? "Purchase order reopened." : $"{count} purchase order(s) reopened.",
            _ => count == 1 ? "Purchase order deleted." : $"{count} purchase order(s) deleted."
        };

    private async Task ReloadGridAsync()
    {
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncDataSourceFilters()
    {
        DataSource.UpdateFilters(new PoOrderListQuery
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
        var result = await Orders.SearchAsync(query);
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

    private async Task<(IReadOnlyList<PoOrderListRow> Rows, int TotalCount)> SearchPageAsync(
        PoOrderListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Orders.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.ListPage is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load purchase orders.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.ListPage.TotalCount);
        return (result.ListPage.Rows, result.ListPage.TotalCount);
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}

public sealed class PoOrderGridDataSource : GridCustomDataSource
{
    private readonly Func<PoOrderListQuery, CancellationToken, Task<(IReadOnlyList<PoOrderListRow> Rows, int TotalCount)>> _loader;
    private PoOrderListQuery _filters = new();

    public PoOrderGridDataSource(
        Func<PoOrderListQuery, CancellationToken, Task<(IReadOnlyList<PoOrderListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public PoOrderListQuery CurrentQuery => Clone(_filters);
    public void UpdateFilters(PoOrderListQuery query) => _filters = Clone(query);

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

    private static PoOrderListQuery Clone(PoOrderListQuery source) =>
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
