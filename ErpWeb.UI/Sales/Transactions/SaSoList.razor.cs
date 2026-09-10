using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Sales.Transactions;

public partial class SaSoList : PageBase, IDisposable
{
    [Inject] private ISaSoService Sos { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<SaSoListRow> _selectedRows = [];

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<SaSoListRow> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;
    protected bool CanForceClose;

    protected string? AppliedStatus;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;
    protected string DraftStatusKey = "all";
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;

    protected string ConfirmMessage { get; set; } = string.Empty;
    protected string ConfirmAction { get; set; } = "DELETE";
    protected string ConfirmButtonText => ConfirmAction == "FORCE_CLOSE" ? "Force Close" : "Delete";
    protected ButtonRenderStyle ConfirmButtonStyle =>
        ConfirmAction == "FORCE_CLOSE" ? ButtonRenderStyle.Danger : ButtonRenderStyle.Danger;

    protected SaSoGridDataSource DataSource { get; private set; } = default!;
    protected string TotalCountLabel => TotalCount == 1 ? "1 sales order" : $"{TotalCount:N0} sales orders";
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedDateFrom is not null
        || AppliedDateTo is not null;

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        new(SaSoStatuses.New, "NEW"),
        new(SaSoStatuses.Shipped, "SHIPPED"),
        new(SaSoStatuses.Closed, "CLOSED")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "SO No.", FieldName = nameof(SaSoListRow.SoNo), Width = "140px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Rev", FieldName = nameof(SaSoListRow.CustRel), Width = "70px", VisibleIndex = 2 },
        new() { Caption = "Date", FieldName = nameof(SaSoListRow.SoDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Status", FieldName = nameof(SaSoListRow.Status), Width = "100px", VisibleIndex = 4 },
        new() { Caption = "Fulfill %", FieldName = nameof(SaSoListRow.FulfillmentPct), Width = "90px", VisibleIndex = 5, DisplayFormat = "n0" },
        new() { Caption = "Bill %", FieldName = nameof(SaSoListRow.BillingPct), Width = "90px", VisibleIndex = 6, DisplayFormat = "n0" },
        new() { Caption = "Customer", FieldName = nameof(SaSoListRow.CustCode), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Name", FieldName = nameof(SaSoListRow.CustName), VisibleIndex = 8 },
        new() { Caption = "Customer PO", FieldName = nameof(SaSoListRow.CustPo), Width = "160px", VisibleIndex = 9 },
        new() { Caption = "Lines", FieldName = nameof(SaSoListRow.LineCount), Width = "80px", VisibleIndex = 10 }
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new SaSoGridDataSource(SearchPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.SalesOrder, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.SalesOrder, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.SalesOrder, PermissionCodes.Delete);
        CanForceClose = await AccessRights.CanAsync(MenuCodes.SalesOrder, PermissionCodes.Close);
        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "REVISE", IConClass = "fa-solid fa-code-branch", Style = "secondary", Enabled = CanEdit },
            new() { Text = "FORCE CLOSE", IConClass = "fas fa-lock", Style = "secondary", Enabled = CanForceClose },
            new() { Text = "DELETE", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete }
        ];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View sales order" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit sales order", Enabled = CanEdit }
        ];
        SyncToolbarEnabled();
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<SaSoListRow> list)
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
                "REVISE" => CanEdit
                    && _selectedRows.Count == 1
                    && _selectedRows[0].CanRevise,
                "DELETE" => CanDelete
                    && _selectedRows.Count > 0
                    && _selectedRows.All(x => x.CanDelete),
                "FORCE CLOSE" => CanForceClose,
                _ => button.Enabled
            };
        }
    }

    protected async Task OnButtonClick(SelectedButtonInfo<SaSoListRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW":
                if (!CanAdd) { StatusMessage = "Access Denied!!"; return; }
                Navigation.NavigateTo("/sales/sales-orders/new");
                break;
            case "DELETE":
                await BeginDeleteAsync();
                break;
            case "FORCE CLOSE":
                await BeginForceCloseAsync();
                break;
            case "REVISE":
                BeginRevise();
                break;
            case "REFRESH":
                await ReloadGridAsync();
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<SaSoListRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (mode == "VIEW")
        {
            NavigateView(info.SelectedRow.SoNo);
        }
        else if (mode == "EDIT")
        {
            if (!CanEdit)
            {
                StatusMessage = "Access Denied!!";
                return Task.CompletedTask;
            }

            if (string.Equals(info.SelectedRow.Status, SaSoStatuses.Closed, StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "Closed sales orders are view-only.";
                return Task.CompletedTask;
            }

            Navigation.NavigateTo($"/sales/sales-orders/edit/{info.SelectedRow.SoNo}");
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(string soNo) =>
        Navigation.NavigateTo($"/sales/sales-orders/view/{soNo}");

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
            var keyed = _selectedRows
                .Select(x => new SaSoKeyedRequest { SoNo = x.SoNo, RowVersion = x.RowVersion })
                .ToList();

            var result = ConfirmAction == "FORCE_CLOSE"
                ? await Sos.ForceCloseAsync(keyed)
                : await Sos.DeleteAsync(keyed);

            if (result.Succeeded)
            {
                StatusMessage = ConfirmAction == "DELETE"
                    ? "Sales order(s) deleted."
                    : "Sales order(s) force-closed.";
                ConfirmVisible = false;
                _selectedRows.Clear();
                await ReloadGridAsync();
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to complete the action.";
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

    protected static string StatusChipClass(string? status) =>
        string.Equals(status, SaSoStatuses.Shipped, StringComparison.OrdinalIgnoreCase) ? "is-on" :
        string.Equals(status, SaSoStatuses.Closed, StringComparison.OrdinalIgnoreCase) ? "is-off" : "is-hold";

    private Task BeginDeleteAsync()
    {
        if (!CanDelete) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }

        var blocked = _selectedRows
            .Where(x => !x.CanDelete)
            .Select(x =>
            {
                if (!string.Equals(x.Status, SaSoStatuses.New, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{x.SoNo} — not NEW ({x.Status})";
                }

                var reason = string.IsNullOrWhiteSpace(x.MutationBlockReason)
                    ? "already in use"
                    : x.MutationBlockReason;
                return $"{x.SoNo} — {reason}";
            })
            .ToList();
        if (blocked.Count > 0)
        {
            ErrorMessage = "Cannot delete:\n" + string.Join("\n", blocked);
            return Task.CompletedTask;
        }

        ConfirmAction = "DELETE";
        ConfirmMessage = $"Permanently delete {_selectedRows.Count} selected sales order(s)?";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private Task BeginForceCloseAsync()
    {
        if (!CanForceClose) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }
        if (_selectedRows.Count > SaSoLimits.MaxForceCloseSelection)
        {
            ErrorMessage = $"Select at most {SaSoLimits.MaxForceCloseSelection} sales orders to force-close.";
            return Task.CompletedTask;
        }

        var closed = _selectedRows
            .Where(x => string.Equals(x.Status, SaSoStatuses.Closed, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.SoNo)
            .ToList();
        if (closed.Count > 0)
        {
            ErrorMessage = $"Closed sales orders cannot be force-closed again. Closed: {string.Join(", ", closed)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "FORCE_CLOSE";
        ConfirmMessage = $"Force-close {_selectedRows.Count} selected sales order(s)? Remaining balance will be closed.";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private void BeginRevise()
    {
        if (!CanEdit) { StatusMessage = "Access Denied!!"; return; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return; }
        if (_selectedRows.Count != 1) { ErrorMessage = "Select exactly one sales order to revise."; return; }

        var row = _selectedRows[0];
        if (!row.CanRevise)
        {
            if (string.Equals(row.Status, SaSoStatuses.Closed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.Status, SaSoStatuses.Shipped, StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "CLOSED/SHIPPED sales orders cannot be revised.";
                return;
            }

            if (!string.Equals(row.Status, SaSoStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "Only NEW sales orders can be revised.";
                return;
            }

            var reason = row.MutationBlockReason ?? string.Empty;
            if (reason.Contains("draft Delivery Order", StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage =
                    "This Sales Order cannot be revised because a draft Delivery Order is holding quantity on the current revision.";
            }
            else if (reason.Contains("draft Invoice", StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage =
                    "This Sales Order cannot be revised because a draft Invoice is holding quantity on the current revision.";
            }
            else
            {
                ErrorMessage = "This Sales Order cannot be revised because the current revision is already in use.";
            }

            return;
        }

        Navigation.NavigateTo($"/sales/sales-orders/revise/{row.SoNo}");
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
        DataSource.UpdateFilters(new SaSoListQuery
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
        var result = await Sos.SearchAsync(query);
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

    private async Task<(IReadOnlyList<SaSoListRow> Rows, int TotalCount)> SearchPageAsync(
        SaSoListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Sos.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.ListPage is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load sales orders.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.ListPage.TotalCount);
        return (result.ListPage.Rows, result.ListPage.TotalCount);
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}

public sealed class SaSoGridDataSource : GridCustomDataSource
{
    private readonly Func<SaSoListQuery, CancellationToken, Task<(IReadOnlyList<SaSoListRow> Rows, int TotalCount)>> _loader;
    private SaSoListQuery _filters = new();

    public SaSoGridDataSource(
        Func<SaSoListQuery, CancellationToken, Task<(IReadOnlyList<SaSoListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public SaSoListQuery CurrentQuery => Clone(_filters);
    public void UpdateFilters(SaSoListQuery query) => _filters = Clone(query);

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

    private static SaSoListQuery Clone(SaSoListQuery source) =>
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
