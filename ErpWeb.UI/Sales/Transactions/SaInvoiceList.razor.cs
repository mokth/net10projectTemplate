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

public partial class SaInvoiceList : PageBase, IDisposable
{
    [Inject] private ISaInvoiceService Invoices { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<SaInvoiceListRow> _selectedRows = [];

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<SaInvoiceListRow> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;
    protected bool CanPost;
    protected bool CanRollback;

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
        _ => "Delete"
    };
    protected ButtonRenderStyle ConfirmButtonStyle => ConfirmAction switch
    {
        "POST" => ButtonRenderStyle.Primary,
        "ROLLBACK" => ButtonRenderStyle.Warning,
        _ => ButtonRenderStyle.Danger
    };

    protected SaInvoiceGridDataSource DataSource { get; private set; } = default!;
    protected string TotalCountLabel => TotalCount == 1 ? "1 invoice" : $"{TotalCount:N0} invoices";
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedDateFrom is not null
        || AppliedDateTo is not null;

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        new(SaInvoiceStatuses.New, "NEW"),
        new(SaInvoiceStatuses.Posted, "POSTED")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Invoice", FieldName = nameof(SaInvoiceListRow.InvNo), Width = "140px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Date", FieldName = nameof(SaInvoiceListRow.InvDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 2 },
        new() { Caption = "Status", FieldName = nameof(SaInvoiceListRow.Status), Width = "100px", VisibleIndex = 3 },
        new() { Caption = "Customer", FieldName = nameof(SaInvoiceListRow.CustCode), Width = "120px", VisibleIndex = 4 },
        new() { Caption = "Name", FieldName = nameof(SaInvoiceListRow.CustName), VisibleIndex = 5 },
        new() { Caption = "Total", FieldName = nameof(SaInvoiceListRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "110px", VisibleIndex = 6 },
        new() { Caption = "Lines", FieldName = nameof(SaInvoiceListRow.LineCount), Width = "80px", VisibleIndex = 7 }
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new SaInvoiceGridDataSource(SearchPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Delete);
        CanPost = await AccessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Post);
        CanRollback = await AccessRights.CanAsync(MenuCodes.SalesInvoice, PermissionCodes.Rollback);
        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "POST", IConClass = "fas fa-check", Style = "success", Enabled = CanPost },
            new() { Text = "ROLLBACK", IConClass = "fas fa-rotate-left", Style = "warning", Enabled = CanRollback },
            new() { Text = "DELETE", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete }
        ];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View invoice" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit invoice", Enabled = CanEdit }
        ];
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<SaInvoiceListRow> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
    }

    protected async Task OnButtonClick(SelectedButtonInfo<SaInvoiceListRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW":
                if (!CanAdd) { StatusMessage = "Access Denied!!"; return; }
                Navigation.NavigateTo("/sales/invoices/new");
                break;
            case "DELETE": await BeginDeleteAsync(); break;
            case "POST": await BeginPostAsync(); break;
            case "ROLLBACK": await BeginRollbackAsync(); break;
            case "REFRESH": await ReloadGridAsync(); break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<SaInvoiceListRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (mode == "VIEW")
        {
            NavigateView(info.SelectedRow.InvNo);
        }
        else if (mode == "EDIT")
        {
            if (!CanEdit)
            {
                StatusMessage = "Access Denied!!";
                return Task.CompletedTask;
            }

            if (!string.Equals(info.SelectedRow.Status, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "Only NEW invoices can be edited.";
                return Task.CompletedTask;
            }

            Navigation.NavigateTo($"/sales/invoices/edit/{info.SelectedRow.InvNo}");
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(string invNo) =>
        Navigation.NavigateTo($"/sales/invoices/view/{invNo}");

    protected async Task OnSearchTextChanged(string text)
    {
        SearchText = text ?? string.Empty;
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
        _searchDebounce = new Timer(400) { AutoReset = false };
        var version = Interlocked.Increment(ref _searchVersion);
        _searchDebounce.Elapsed += async (_, _) =>
        {
            if (version != _searchVersion) return;
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
            var nos = _selectedRows.Select(x => x.InvNo).ToList();
            SaInvoiceOperationResult result = ConfirmAction switch
            {
                "POST" => await Invoices.PostAsync(nos),
                "ROLLBACK" => await Invoices.RollbackAsync(nos),
                _ => await Invoices.DeleteAsync(nos)
            };

            if (result.Posting.Count > 0)
            {
                var lines = result.Posting.Select(x => $"{x.InvNo}: {x.Outcome}");
                var summary = string.Join(" · ", lines);
                if (result.Succeeded)
                {
                    StatusMessage = summary;
                    ConfirmVisible = false;
                    _selectedRows.Clear();
                    await ReloadGridAsync();
                }
                else
                {
                    ErrorMessage = summary;
                    ConfirmVisible = false;
                    await ReloadGridAsync();
                }
            }
            else if (result.Succeeded)
            {
                StatusMessage = ConfirmAction == "DELETE" ? "Invoice(s) deleted." : "Done.";
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
        string.Equals(status, SaInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase) ? "is-on" : "is-hold";

    private Task BeginDeleteAsync()
    {
        if (!CanDelete) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }
        var notNew = _selectedRows.Where(x => !string.Equals(x.Status, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase)).Select(x => x.InvNo).ToList();
        if (notNew.Count > 0)
        {
            ErrorMessage = $"Only NEW invoices can be deleted. Non-NEW: {string.Join(", ", notNew)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "DELETE";
        ConfirmMessage = $"Permanently delete {_selectedRows.Count} selected invoice(s)?";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private Task BeginPostAsync()
    {
        if (!CanPost) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }
        if (_selectedRows.Count > SaInvoiceLimits.MaxPostSelection)
        {
            ErrorMessage = $"Select at most {SaInvoiceLimits.MaxPostSelection} invoices to post.";
            return Task.CompletedTask;
        }

        var notNew = _selectedRows.Where(x => !string.Equals(x.Status, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase)).Select(x => x.InvNo).ToList();
        if (notNew.Count > 0)
        {
            ErrorMessage = $"Only NEW invoices can be posted. Non-NEW: {string.Join(", ", notNew)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "POST";
        ConfirmMessage = $"Post {_selectedRows.Count} selected invoice(s)? Stock will be issued from the shipment piles.";
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    private Task BeginRollbackAsync()
    {
        if (!CanRollback) { StatusMessage = "Access Denied!!"; return Task.CompletedTask; }
        if (_selectedRows.Count == 0) { StatusMessage = "No Record Selected!"; return Task.CompletedTask; }
        if (_selectedRows.Count > SaInvoiceLimits.MaxPostSelection)
        {
            ErrorMessage = $"Select at most {SaInvoiceLimits.MaxPostSelection} invoices to roll back.";
            return Task.CompletedTask;
        }

        var notPosted = _selectedRows.Where(x => !string.Equals(x.Status, SaInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase)).Select(x => x.InvNo).ToList();
        if (notPosted.Count > 0)
        {
            ErrorMessage = $"Only POSTED invoices can be rolled back. Non-POSTED: {string.Join(", ", notPosted)}";
            return Task.CompletedTask;
        }

        ConfirmAction = "ROLLBACK";
        ConfirmMessage = $"Roll back {_selectedRows.Count} selected invoice(s)? Stock will be restored.";
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
        DataSource.UpdateFilters(new SaInvoiceListQuery
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
        var result = await Invoices.SearchAsync(query);
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

    private async Task<(IReadOnlyList<SaInvoiceListRow> Rows, int TotalCount)> SearchPageAsync(
        SaInvoiceListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Invoices.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.ListPage is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load invoices.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.ListPage.TotalCount);
        return (result.ListPage.Rows, result.ListPage.TotalCount);
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}

public sealed class SaInvoiceGridDataSource : GridCustomDataSource
{
    private readonly Func<SaInvoiceListQuery, CancellationToken, Task<(IReadOnlyList<SaInvoiceListRow> Rows, int TotalCount)>> _loader;
    private SaInvoiceListQuery _filters = new();

    public SaInvoiceGridDataSource(
        Func<SaInvoiceListQuery, CancellationToken, Task<(IReadOnlyList<SaInvoiceListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public SaInvoiceListQuery CurrentQuery => Clone(_filters);
    public void UpdateFilters(SaInvoiceListQuery query) => _filters = Clone(query);

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

    private static SaInvoiceListQuery Clone(SaInvoiceListQuery source) =>
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
