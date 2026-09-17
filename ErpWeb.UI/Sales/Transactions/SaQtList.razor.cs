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

public partial class SaQtList : PageBase, IDisposable
{
    [Inject] private ISaQtService Qts { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<SaQtListRow> _selectedRows = [];

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<SaQtListRow> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;

    protected string? AppliedStatus;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;
    protected string DraftStatusKey = "all";
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;

    protected string ConfirmMessage { get; set; } = string.Empty;

    protected SaQtGridDataSource DataSource { get; private set; } = default!;
    protected string TotalCountLabel => TotalCount == 1 ? "1 quotation" : $"{TotalCount:N0} quotations";
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedDateFrom is not null
        || AppliedDateTo is not null;

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        new(SaQtStatuses.New, "NEW"),
        new(SaQtStatuses.Sent, "SENT"),
        new(SaQtStatuses.Accepted, "ACCEPTED"),
        new(SaQtStatuses.Expired, "EXPIRED"),
        new(SaQtStatuses.Lost, "LOST"),
        new(SaQtStatuses.Cancelled, "CANCELLED"),
        new(SaQtStatuses.Closed, "CLOSED")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Quotation No.", FieldName = nameof(SaQtListRow.QtNo), Width = "150px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Rev", FieldName = nameof(SaQtListRow.CustRel), Width = "60px", VisibleIndex = 2 },
        new() { Caption = "Date", FieldName = nameof(SaQtListRow.QtDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "105px", VisibleIndex = 3 },
        new() { Caption = "Valid until", FieldName = nameof(SaQtListRow.ValidUntil), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "105px", VisibleIndex = 4 },
        new() { Caption = "Status", FieldName = nameof(SaQtListRow.Status), Width = "100px", VisibleIndex = 5 },
        new() { Caption = "Conversion", FieldName = nameof(SaQtListRow.ConversionStatus), Width = "100px", VisibleIndex = 6 },
        new() { Caption = "Customer", FieldName = nameof(SaQtListRow.CustCode), Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Name", FieldName = nameof(SaQtListRow.CustName), VisibleIndex = 8 },
        new() { Caption = "Customer RFQ / Ref", FieldName = nameof(SaQtListRow.CustPo), Width = "150px", VisibleIndex = 9 },
        new() { Caption = "Lines", FieldName = nameof(SaQtListRow.LineCount), Width = "70px", VisibleIndex = 10 },
        new() { Caption = "Total", FieldName = nameof(SaQtListRow.TotAmnt), DataType = "number", DisplayFormat = "n2", Width = "110px", VisibleIndex = 11 },
        new() { Caption = "Sales Order", FieldName = nameof(SaQtListRow.ConvertedSoNo), Width = "130px", VisibleIndex = 12 }
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new SaQtGridDataSource(SearchPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.SalesQuotation, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.SalesQuotation, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.SalesQuotation, PermissionCodes.Delete);
        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "REVISE", IConClass = "fa-solid fa-code-branch", Style = "secondary", Enabled = CanEdit },
            new() { Text = "DELETE", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete }
        ];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View quotation" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit quotation", Enabled = CanEdit }
        ];
        SyncToolbarEnabled();
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<SaQtListRow> list)
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
                "REVISE" => CanEdit && _selectedRows.Count == 1 && _selectedRows[0].CanRevise,
                "DELETE" => CanDelete
                    && _selectedRows.Count > 0
                    && _selectedRows.All(x => x.CanDelete),
                _ => button.Enabled
            };
        }
    }

    protected Task OnButtonClick(SelectedButtonInfo<SaQtListRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW":
                if (!CanAdd) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
                Navigation.NavigateTo("/sales/quotations/new");
                break;
            case "DELETE":
                return BeginDeleteAsync();
            case "REVISE":
                BeginRevise();
                break;
            case "REFRESH":
                return ReloadGridAsync();
        }

        return Task.CompletedTask;
    }

    protected Task OnActionClick(SelectedButtonInfo<SaQtListRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (mode == "VIEW")
        {
            NavigateView(info.SelectedRow.QtNo);
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
                ErrorMessage = $"Only NEW quotations can be edited. Use Revise to change a {info.SelectedRow.Status} quotation.";
                return Task.CompletedTask;
            }

            Navigation.NavigateTo($"/sales/quotations/edit/{info.SelectedRow.QtNo}");
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(string qtNo) =>
        Navigation.NavigateTo($"/sales/quotations/view/{qtNo}");

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
                .Select(x => new SaQtKeyedRequest { QtNo = x.QtNo, CustRel = x.CustRel, RowVersion = x.RowVersion })
                .ToList();

            var result = await Qts.DeleteAsync(keyed);
            if (result.Succeeded)
            {
                StatusMessage = "Quotation(s) deleted.";
                ConfirmVisible = false;
                _selectedRows.Clear();
                await ReloadGridAsync();
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to delete the quotation(s).";
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

    /// <summary>
    /// Status chip colouring: converted/closed reads as complete, lapsed or lost reads as closed,
    /// everything still live reads as in-progress.
    /// </summary>
    protected static string StatusDisplay(SaQtListRow row) =>
        row.IsExpired && row.Status is SaQtStatuses.New or SaQtStatuses.Sent
            ? SaQtStatuses.Expired
            : row.Status;

    protected static string StatusChipClass(SaQtListRow row) =>
        row.Status switch
        {
            SaQtStatuses.Closed => "is-on",
            SaQtStatuses.Lost or SaQtStatuses.Cancelled or SaQtStatuses.Expired => "is-off",
            _ => row.IsExpired ? "is-off" : "is-hold"
        };

    private Task BeginDeleteAsync()
    {
        if (!CanDelete) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }

        var blocked = _selectedRows
            .Where(x => !x.CanDelete)
            .Select(x =>
            {
                if (!string.Equals(x.Status, SaQtStatuses.New, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{x.QtNo} — not NEW ({x.Status})";
                }

                var reason = string.IsNullOrWhiteSpace(x.MutationBlockReason)
                    ? "already in use"
                    : x.MutationBlockReason;
                return $"{x.QtNo} — {reason}";
            })
            .ToList();
        if (blocked.Count > 0)
        {
            ErrorMessage = "Cannot delete:\n" + string.Join("\n", blocked);
            return Task.CompletedTask;
        }

        ConfirmMessage = $"Permanently delete {_selectedRows.Count} selected quotation(s)?";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private void BeginRevise()
    {
        if (!CanEdit) { StatusMessage = "Access Denied!!"; return; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return; }
        if (_selectedRows.Count != 1) { ErrorMessage = "Select exactly one quotation to revise."; return; }

        var row = _selectedRows[0];
        if (!row.CanRevise)
        {
            if (!SaQtStatuses.Revisable.Contains(row.Status))
            {
                ErrorMessage = $"Only NEW, SENT or ACCEPTED quotations can be revised. {row.QtNo} is {row.Status}.";
                return;
            }

            ErrorMessage =
                "This quotation cannot be revised because it is already linked to a Sales Order.";
            return;
        }

        Navigation.NavigateTo($"/sales/quotations/revise/{row.QtNo}");
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
        DataSource.UpdateFilters(new SaQtListQuery
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
        var result = await Qts.SearchAsync(query);
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

    private async Task<(IReadOnlyList<SaQtListRow> Rows, int TotalCount)> SearchPageAsync(
        SaQtListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Qts.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.ListPage is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load quotations.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.ListPage.TotalCount);
        return (result.ListPage.Rows, result.ListPage.TotalCount);
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}

public sealed class SaQtGridDataSource : GridCustomDataSource
{
    private readonly Func<SaQtListQuery, CancellationToken, Task<(IReadOnlyList<SaQtListRow> Rows, int TotalCount)>> _loader;
    private SaQtListQuery _filters = new();

    public SaQtGridDataSource(
        Func<SaQtListQuery, CancellationToken, Task<(IReadOnlyList<SaQtListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public SaQtListQuery CurrentQuery => Clone(_filters);
    public void UpdateFilters(SaQtListQuery query) => _filters = Clone(query);

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

    private static SaQtListQuery Clone(SaQtListQuery source) =>
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
