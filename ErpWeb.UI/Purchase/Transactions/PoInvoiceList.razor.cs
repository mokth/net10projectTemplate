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

public partial class PoInvoiceList : PageBase, IDisposable
{
    [Inject] private IPoInvoiceService Invoices { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<PoInvoiceListRow> _selectedRows = [];

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected string? AppliedType = "INV";
    protected string? AppliedStatus;
    protected DateTime? AppliedDateFrom;
    protected DateTime? AppliedDateTo;
    protected string DraftStatusKey = "all";
    protected DateTime? DraftDateFrom;
    protected DateTime? DraftDateTo;

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;
    protected bool CanPost;
    protected bool CanRollback;

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

    protected PoInvoiceGridDataSource DataSource { get; private set; } = default!;
    protected string TotalCountLabel => TotalCount == 1 ? "1 document" : $"{TotalCount:N0} documents";
    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedStatus)
        || AppliedDateFrom is not null
        || AppliedDateTo is not null
        || !string.IsNullOrWhiteSpace(AppliedType);

    protected IReadOnlyList<StatusFilterOption> StatusFilterOptions { get; } =
    [
        new("all", "All"),
        new(PoInvoiceStatuses.New, "NEW"),
        new(PoInvoiceStatuses.Posted, "POSTED")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Doc No.", FieldName = nameof(PoInvoiceListRow.DocNo), Width = "140px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Date", FieldName = nameof(PoInvoiceListRow.DocDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 2 },
        new() { Caption = "Type", FieldName = nameof(PoInvoiceListRow.Type), Width = "70px", VisibleIndex = 3 },
        new() { Caption = "Status", FieldName = nameof(PoInvoiceListRow.Status), Width = "100px", VisibleIndex = 4 },
        new() { Caption = "Vendor", FieldName = nameof(PoInvoiceListRow.VendorCode), Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Name", FieldName = nameof(PoInvoiceListRow.VendorName), VisibleIndex = 6 },
        new() { Caption = "Inv Ref", FieldName = nameof(PoInvoiceListRow.InvNo), Width = "130px", VisibleIndex = 7 },
        new() { Caption = "Total", FieldName = nameof(PoInvoiceListRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "120px", VisibleIndex = 8 },
        new() { Caption = "Lines", FieldName = nameof(PoInvoiceListRow.LineCount), Width = "80px", VisibleIndex = 9 }
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new PoInvoiceGridDataSource(SearchPageAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.PurchaseInvoice, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PurchaseInvoice, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.PurchaseInvoice, PermissionCodes.Delete);
        CanPost = await AccessRights.CanAsync(MenuCodes.PurchaseInvoice, PermissionCodes.Post);
        CanRollback = await AccessRights.CanAsync(MenuCodes.PurchaseInvoice, PermissionCodes.Rollback);
        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "POST", IConClass = "fas fa-check", Style = "success", Enabled = CanPost },
            new() { Text = "ROLLBACK", IConClass = "fas fa-rotate-left", Style = "warning", Enabled = CanRollback },
            new() { Text = "DELETE", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete }
        ];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View document" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit document", Enabled = CanEdit }
        ];
        SyncDataSourceFilters();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<PoInvoiceListRow> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
    }

    protected async Task OnButtonClick(SelectedButtonInfo<PoInvoiceListRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW":
                if (!CanAdd) { ErrorMessage = "Access Denied!!"; return; }
                Navigation.NavigateTo("/purchase/invoices/new");
                break;
            case "DELETE": await BeginConfirmAsync("DELETE"); break;
            case "POST": await BeginConfirmAsync("POST"); break;
            case "ROLLBACK": await BeginConfirmAsync("ROLLBACK"); break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoInvoiceListRow> info)
    {
        if (info.SelectedRow is null)
        {
            ErrorMessage = "No record selected.";
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (mode == "VIEW")
        {
            Navigation.NavigateTo($"/purchase/invoices/view/{info.SelectedRow.DocNo}");
        }
        else if (mode == "EDIT")
        {
            if (!CanEdit || !info.SelectedRow.CanEdit)
            {
                ErrorMessage = $"Document {info.SelectedRow.DocNo} cannot be edited.";
                return Task.CompletedTask;
            }

            Navigation.NavigateTo($"/purchase/invoices/edit/{info.SelectedRow.DocNo}");
        }

        return Task.CompletedTask;
    }

    protected async Task SetTypeAsync(string? type)
    {
        AppliedType = type;
        SyncDataSourceFilters();
        await ReloadGridAsync();
    }

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

    private Task BeginConfirmAsync(string action)
    {
        if (_selectedRows.Count == 0)
        {
            ErrorMessage = "Select at least one document.";
            return Task.CompletedTask;
        }

        ConfirmAction = action;
        ConfirmMessage = action switch
        {
            "POST" => $"Post {_selectedRows.Count} document(s)?",
            "ROLLBACK" => $"Rollback {_selectedRows.Count} document(s)?",
            _ => $"Delete {_selectedRows.Count} document(s)?"
        };
        ConfirmVisible = true;
        return Task.CompletedTask;
    }

    protected async Task ConfirmActionAsync()
    {
        if (IsSubmitting || _selectedRows.Count == 0)
        {
            ConfirmVisible = false;
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var keys = _selectedRows.Select(x => new PoInvoiceKeyedRequest
            {
                DocNo = x.DocNo,
                RowVersion = x.RowVersion
            }).ToList();

            var result = ConfirmAction switch
            {
                "POST" => await Invoices.PostAsync(keys),
                "ROLLBACK" => await Invoices.RollbackAsync(keys),
                _ => await Invoices.DeleteAsync(keys)
            };

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage) && !result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage;
            }
            else
            {
                StatusMessage = $"{ConfirmAction} completed. OK={result.SucceededCount}, Failed={result.FailedCount}";
            }

            ConfirmVisible = false;
            await ReloadGridAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private void SyncDataSourceFilters()
    {
        DataSource.UpdateFilters(new PoInvoiceListQuery
        {
            Type = AppliedType,
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            Status = AppliedStatus,
            DateFrom = AppliedDateFrom,
            DateTo = AppliedDateTo,
            SortDescending = true
        });
    }

    private async Task ReloadGridAsync()
    {
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private async Task<(IReadOnlyList<PoInvoiceListRow> Rows, int TotalCount)> SearchPageAsync(
        PoInvoiceListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Invoices.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.ListPage is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.ErrorMessage ?? "Unable to load documents.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.ListPage.TotalCount);
        return (result.ListPage.Rows, result.ListPage.TotalCount);
    }

    public void Dispose()
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
    }

    protected sealed record StatusFilterOption(string Key, string Name);
}

public sealed class PoInvoiceGridDataSource : GridCustomDataSource
{
    private readonly Func<PoInvoiceListQuery, CancellationToken, Task<(IReadOnlyList<PoInvoiceListRow> Rows, int TotalCount)>> _loader;
    private PoInvoiceListQuery _filters = new();

    public PoInvoiceGridDataSource(
        Func<PoInvoiceListQuery, CancellationToken, Task<(IReadOnlyList<PoInvoiceListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public void UpdateFilters(PoInvoiceListQuery query) => _filters = Clone(query);

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

    private static PoInvoiceListQuery Clone(PoInvoiceListQuery q) => new()
    {
        Type = q.Type,
        SearchText = q.SearchText,
        Status = q.Status,
        DateFrom = q.DateFrom,
        DateTo = q.DateTo,
        SortField = q.SortField,
        SortDescending = q.SortDescending,
        Skip = q.Skip,
        Take = q.Take
    };
}
