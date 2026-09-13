using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Purchase.Masters;

public partial class PoSuppList : PageBase, IDisposable
{
    [Inject] private IPoSupplierService Suppliers { get; set; } = default!;
    [Inject] private IPoSupplierLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<PoSupplierListRow> _selectedRows = [];
    private PendingBulkAction _pendingAction = PendingBulkAction.None;

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<PoSupplierListRow> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;
    protected bool CanExport;

    protected string? AppliedType;
    protected string? AppliedGroup;
    protected string? AppliedArea;
    protected bool? AppliedIsActive = true;

    protected string DraftType = string.Empty;
    protected string DraftGroup = string.Empty;
    protected string? DraftArea;
    protected string DraftActiveKey = "active";

    protected IReadOnlyList<IvCodeLookupRow> Areas { get; set; } = [];

    protected PoSuppGridDataSource DataSource { get; private set; } = default!;

    protected string TotalCountLabel => TotalCount == 1 ? "1 supplier" : $"{TotalCount:N0} suppliers";

    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedType)
        || !string.IsNullOrWhiteSpace(AppliedGroup)
        || !string.IsNullOrWhiteSpace(AppliedArea)
        || AppliedIsActive is not null;

    protected string ConfirmMessage { get; set; } = string.Empty;
    protected string ConfirmButtonText { get; set; } = "Confirm";
    protected ButtonRenderStyle ConfirmButtonStyle { get; set; } = ButtonRenderStyle.Primary;

    protected IReadOnlyList<ActiveFilterOption> ActiveFilterOptions { get; } =
    [
        new("all", "All"),
        new("active", "Active"),
        new("inactive", "Inactive")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Code", FieldName = nameof(PoSupplierListRow.SuppCode), Width = "120px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Name", FieldName = nameof(PoSupplierListRow.SuppName), VisibleIndex = 2 },
        new() { Caption = "Type", FieldName = nameof(PoSupplierListRow.SuppType), Width = "90px", VisibleIndex = 3 },
        new() { Caption = "Group", FieldName = nameof(PoSupplierListRow.CategoryCode), Width = "100px", VisibleIndex = 4 },
        new() { Caption = "Area", FieldName = nameof(PoSupplierListRow.AreaCode), Width = "90px", VisibleIndex = 5 },
        new() { Caption = "City", FieldName = nameof(PoSupplierListRow.City), Width = "110px", VisibleIndex = 6 },
        new() { Caption = "Tel", FieldName = nameof(PoSupplierListRow.Tel), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Active", FieldName = nameof(PoSupplierListRow.IsActive), DataType = "bool", Width = "80px", VisibleIndex = 8 }
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new PoSuppGridDataSource(SearchPageAsync);

        CanAdd = await AccessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Delete);
        CanExport = await AccessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Export);

        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "COPY", IConClass = "fa-regular fa-copy", Style = "primary", Enabled = CanAdd },
            new() { Text = "ACTIVATE", IConClass = "fa-solid fa-toggle-on", Style = "success", Enabled = CanEdit },
            new() { Text = "DEACTIVATE", IConClass = "fa-solid fa-toggle-off", Style = "warning", Enabled = CanEdit },
            new() { Text = "DELETE", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete },
            new() { Text = "EXPORT", IConClass = "fa-solid fa-file-excel", Style = "primary", Enabled = CanExport }
        ];

        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View supplier" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit supplier", Enabled = CanEdit }
        ];

        await LoadLookupsAsync();
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<PoSupplierListRow> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
    }

    protected async Task OnButtonClick(SelectedButtonInfo<PoSupplierListRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW":
                if (!CanAdd) { StatusMessage = "Access Denied!!"; return; }
                Navigation.NavigateTo("/purchase/suppliers/new");
                break;
            case "COPY":
                await OnCopyAsync();
                break;
            case "ACTIVATE":
                await BeginBulkAsync(PendingBulkAction.Activate);
                break;
            case "DEACTIVATE":
                await BeginBulkAsync(PendingBulkAction.Deactivate);
                break;
            case "DELETE":
                await BeginBulkAsync(PendingBulkAction.Delete);
                break;
            case "EXPORT":
                await OnExportAsync();
                break;
            case "REFRESH":
                await ReloadGridAsync();
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoSupplierListRow> info)
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
                NavigateView(info.SelectedRow.SuppCode);
                break;
            case "EDIT":
                if (!CanEdit)
                {
                    StatusMessage = "Access Denied!!";
                    break;
                }

                Navigation.NavigateTo($"/purchase/suppliers/edit/{info.SelectedRow.SuppCode}");
                break;
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(string code) =>
        Navigation.NavigateTo($"/purchase/suppliers/view/{code}");

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
        DraftType = AppliedType ?? string.Empty;
        DraftGroup = AppliedGroup ?? string.Empty;
        DraftArea = AppliedArea;
        DraftActiveKey = AppliedIsActive switch
        {
            true => "active",
            false => "inactive",
            _ => "all"
        };
        FilterPopupVisible = true;
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedType = string.IsNullOrWhiteSpace(DraftType) ? null : DraftType.Trim();
        AppliedGroup = string.IsNullOrWhiteSpace(DraftGroup) ? null : DraftGroup.Trim();
        AppliedArea = string.IsNullOrWhiteSpace(DraftArea) ? null : DraftArea.Trim();
        AppliedIsActive = DraftActiveKey switch
        {
            "active" => true,
            "inactive" => false,
            _ => null
        };
        FilterPopupVisible = false;
        SyncDataSourceFilters();
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftType = string.Empty;
        DraftGroup = string.Empty;
        DraftArea = null;
        DraftActiveKey = "active";
        AppliedType = null;
        AppliedGroup = null;
        AppliedArea = null;
        AppliedIsActive = true;
        FilterPopupVisible = false;
        SyncDataSourceFilters();
        await ReloadGridAsync();
    }

    protected async Task ConfirmActionAsync()
    {
        if (IsSubmitting || _pendingAction == PendingBulkAction.None || _selectedRows.Count == 0)
        {
            ConfirmVisible = false;
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            var tokens = _selectedRows
                .Select(x => new IvMasterKeyToken { Code = x.SuppCode, RowVersion = x.RowVersion })
                .ToList();

            IvMasterOperationResult<object> result = _pendingAction switch
            {
                PendingBulkAction.Activate => await Suppliers.SetActiveAsync(tokens, true),
                PendingBulkAction.Deactivate => await Suppliers.SetActiveAsync(tokens, false),
                PendingBulkAction.Delete => await Suppliers.DeleteAsync(tokens),
                _ => IvMasterOperationResult<object>.Fail(IvMasterErrorCode.Validation, "Unknown action.")
            };

            if (result.Succeeded)
            {
                StatusMessage = _pendingAction switch
                {
                    PendingBulkAction.Activate => "Supplier(s) activated.",
                    PendingBulkAction.Deactivate => "Supplier(s) deactivated.",
                    PendingBulkAction.Delete => "Supplier(s) deleted.",
                    _ => "Done."
                };
                ConfirmVisible = false;
                _selectedRows.Clear();
                await ReloadGridAsync();
            }
            else
            {
                ErrorMessage = result.Message
                    ?? result.DeleteCheck?.Message
                    ?? "Unable to complete the operation.";
                ConfirmVisible = false;
            }
        }
        finally
        {
            IsSubmitting = false;
            _pendingAction = PendingBulkAction.None;
        }
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    public void Dispose()
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
    }

    private async Task OnCopyAsync()
    {
        if (!CanAdd)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (_selectedRows.Count != 1)
        {
            StatusMessage = "Select exactly one supplier to copy.";
            return;
        }

        Navigation.NavigateTo($"/purchase/suppliers/new?copy={Uri.EscapeDataString(_selectedRows[0].SuppCode)}");
        await Task.CompletedTask;
    }

    private async Task OnExportAsync()
    {
        if (!CanExport)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        var query = BuildQueryDictionary();
        var url = QueryHelpers.AddQueryString("/purchase/suppliers/export", query);
        Navigation.NavigateTo(url, forceLoad: true);
        await Task.CompletedTask;
    }

    private async Task BeginBulkAsync(PendingBulkAction action)
    {
        if (action is PendingBulkAction.Activate or PendingBulkAction.Deactivate)
        {
            if (!CanEdit)
            {
                StatusMessage = "Access Denied!!";
                return;
            }
        }
        else if (action == PendingBulkAction.Delete && !CanDelete)
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return;
        }

        if (action == PendingBulkAction.Delete)
        {
            var check = await Suppliers.CanDeleteBulkAsync(_selectedRows.Select(x => x.SuppCode).ToList());
            if (!check.CanDelete)
            {
                ErrorMessage = check.Message ?? "One or more suppliers cannot be deleted.";
                return;
            }
        }

        _pendingAction = action;
        ConfirmButtonStyle = action == PendingBulkAction.Delete
            ? ButtonRenderStyle.Danger
            : ButtonRenderStyle.Primary;
        ConfirmButtonText = action switch
        {
            PendingBulkAction.Activate => "Activate",
            PendingBulkAction.Deactivate => "Deactivate",
            PendingBulkAction.Delete => "Delete",
            _ => "Confirm"
        };
        ConfirmMessage = action switch
        {
            PendingBulkAction.Activate => $"Activate {_selectedRows.Count} selected supplier(s)?",
            PendingBulkAction.Deactivate => $"Deactivate {_selectedRows.Count} selected supplier(s)?",
            PendingBulkAction.Delete => $"Permanently delete {_selectedRows.Count} selected supplier(s)?",
            _ => "Confirm?"
        };
        ConfirmVisible = true;
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
        DataSource.UpdateFilters(new PoSupplierListQuery
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            IsActive = AppliedIsActive,
            SuppType = AppliedType,
            CategoryCode = AppliedGroup,
            AreaCode = AppliedArea
        });
    }

    private async Task RefreshCompactPreviewAsync()
    {
        var query = DataSource.CurrentQuery;
        query.Skip = 0;
        query.Take = 50;
        var result = await Suppliers.SearchAsync(query);
        if (result.Succeeded && result.Data is not null)
        {
            CompactRows = result.Data.Rows.ToList();
            TotalCount = result.Data.TotalCount;
        }
        else
        {
            CompactRows = [];
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                ErrorMessage = result.Message;
            }
        }
    }

    private async Task<(IReadOnlyList<PoSupplierListRow> Rows, int TotalCount)> SearchPageAsync(
        PoSupplierListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await Suppliers.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.Message ?? "Unable to load suppliers.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.Data.TotalCount);
        return (result.Data.Rows, result.Data.TotalCount);
    }

    private async Task LoadLookupsAsync()
    {
        Areas = await Lookups.ListAreasForAssignmentAsync();
    }

    private Dictionary<string, string?> BuildQueryDictionary()
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var query = DataSource.CurrentQuery;

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            dict["searchText"] = query.SearchText.Trim();
        }

        if (query.IsActive is bool active)
        {
            dict["isActive"] = active.ToString().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(query.SuppType))
        {
            dict["suppType"] = query.SuppType;
        }

        if (!string.IsNullOrWhiteSpace(query.CategoryCode))
        {
            dict["categoryCode"] = query.CategoryCode;
        }

        if (!string.IsNullOrWhiteSpace(query.AreaCode))
        {
            dict["areaCode"] = query.AreaCode;
        }

        if (!string.IsNullOrWhiteSpace(query.SortField))
        {
            dict["sortField"] = query.SortField;
        }

        if (query.SortDescending)
        {
            dict["sortDescending"] = "true";
        }

        return dict;
    }

    protected sealed record ActiveFilterOption(string Key, string Name);

    private enum PendingBulkAction
    {
        None,
        Activate,
        Deactivate,
        Delete
    }
}

/// <summary>
/// Server-side paging source for Supplier Profile via <see cref="IPoSupplierService.SearchAsync"/>.
/// Bound to DxGrid.Data as a <see cref="GridCustomDataSource"/>.
/// </summary>
public sealed class PoSuppGridDataSource : GridCustomDataSource
{
    private readonly Func<PoSupplierListQuery, CancellationToken, Task<(IReadOnlyList<PoSupplierListRow> Rows, int TotalCount)>> _loader;
    private PoSupplierListQuery _filters = new();

    public PoSuppGridDataSource(
        Func<PoSupplierListQuery, CancellationToken, Task<(IReadOnlyList<PoSupplierListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public PoSupplierListQuery CurrentQuery => Clone(_filters);

    public void UpdateFilters(PoSupplierListQuery query) =>
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

    private static PoSupplierListQuery Clone(PoSupplierListQuery source) =>
        new()
        {
            SearchText = source.SearchText,
            IsActive = source.IsActive,
            SuppType = source.SuppType,
            CategoryCode = source.CategoryCode,
            AreaCode = source.AreaCode,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}
