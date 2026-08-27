using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Inventory.Masters;

public partial class IvStockMasterList : PageBase, IDisposable
{
    [Inject] private IIvStockMasterService StockMasters { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private int _subClassLoadVersion;
    private readonly List<IvStockMasterListRow> _selectedRows = [];
    private PendingBulkAction _pendingAction = PendingBulkAction.None;

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected bool SubClassesLoading;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<IvStockMasterListRow> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;
    protected bool CanExport;

    protected string? AppliedType;
    protected string? AppliedClass;
    protected string? AppliedSubClass;
    protected string? AppliedWarehouse;
    protected string? AppliedBrand;
    protected bool? AppliedIsActive = true;

    protected string? DraftType;
    protected string? DraftClass;
    protected string? DraftSubClass;
    protected string? DraftWarehouse;
    protected string DraftBrand = string.Empty;
    protected string DraftActiveKey = "active";

    protected IReadOnlyList<IvCodeLookupRow> Types { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Classes { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> SubClasses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];

    protected IvStockMasterGridDataSource DataSource { get; private set; } = default!;

    protected string TotalCountLabel => TotalCount == 1 ? "1 item" : $"{TotalCount:N0} items";

    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedType)
        || !string.IsNullOrWhiteSpace(AppliedClass)
        || !string.IsNullOrWhiteSpace(AppliedSubClass)
        || !string.IsNullOrWhiteSpace(AppliedWarehouse)
        || !string.IsNullOrWhiteSpace(AppliedBrand)
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
        new() { Caption = "Code", FieldName = nameof(IvStockMasterListRow.ICode), Width = "120px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Description", FieldName = nameof(IvStockMasterListRow.IDesc), VisibleIndex = 2 },
        new() { Caption = "Type", FieldName = nameof(IvStockMasterListRow.IType), Width = "90px", VisibleIndex = 3 },
        new() { Caption = "Class", FieldName = nameof(IvStockMasterListRow.IClassCode), Width = "100px", VisibleIndex = 4 },
        new() { Caption = "Subclass", FieldName = nameof(IvStockMasterListRow.ISubClassCode), Width = "110px", VisibleIndex = 5 },
        new() { Caption = "Brand", FieldName = nameof(IvStockMasterListRow.Brand), Width = "110px", VisibleIndex = 6 },
        new() { Caption = "Std UOM", FieldName = nameof(IvStockMasterListRow.StdUom), Width = "90px", VisibleIndex = 7 },
        new() { Caption = "Warehouse", FieldName = nameof(IvStockMasterListRow.DefWarehouse), Width = "110px", VisibleIndex = 8 },
        new() { Caption = "Sell price", FieldName = nameof(IvStockMasterListRow.SellingPrice), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 9 },
        new() { Caption = "Buy price", FieldName = nameof(IvStockMasterListRow.PurchasePrice), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 10 },
        new() { Caption = "Active", FieldName = nameof(IvStockMasterListRow.IsActive), DataType = "bool", Width = "80px", VisibleIndex = 11 },
        new() { Caption = "Barcode", FieldName = nameof(IvStockMasterListRow.Barcode), Visible = false, VisibleIndex = 12 },
        new() { Caption = "Sell UOM", FieldName = nameof(IvStockMasterListRow.SellingUom), Visible = false, VisibleIndex = 13 },
        new() { Caption = "Buy UOM", FieldName = nameof(IvStockMasterListRow.PurUom), Visible = false, VisibleIndex = 14 },
        new() { Caption = "Sell GL", FieldName = nameof(IvStockMasterListRow.SellingGlCode), Visible = false, VisibleIndex = 15 },
        new() { Caption = "Buy GL", FieldName = nameof(IvStockMasterListRow.PurchaseGlCode), Visible = false, VisibleIndex = 16 },
        new() { Caption = "Classification", FieldName = nameof(IvStockMasterListRow.Classification), Visible = false, VisibleIndex = 17 }
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new IvStockMasterGridDataSource(SearchPageAsync);

        CanAdd = await AccessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Delete);
        CanExport = await AccessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Export);

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
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View item" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit item", Enabled = CanEdit }
        ];

        await LoadLookupsAsync();
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<IvStockMasterListRow> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
    }

    protected async Task OnButtonClick(SelectedButtonInfo<IvStockMasterListRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW":
                if (!CanAdd) { StatusMessage = "Access Denied!!"; return; }
                Navigation.NavigateTo("/inventory/items/new");
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

    protected Task OnActionClick(SelectedButtonInfo<IvStockMasterListRow> info)
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
                NavigateView(info.SelectedRow.ICode);
                break;
            case "EDIT":
                if (!CanEdit)
                {
                    StatusMessage = "Access Denied!!";
                    break;
                }

                Navigation.NavigateTo($"/inventory/items/edit/{Uri.EscapeDataString(info.SelectedRow.ICode)}");
                break;
        }

        return Task.CompletedTask;
    }

    protected void NavigateView(string code) =>
        Navigation.NavigateTo($"/inventory/items/view/{Uri.EscapeDataString(code)}");

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
        DraftType = AppliedType;
        DraftClass = AppliedClass;
        DraftSubClass = AppliedSubClass;
        DraftWarehouse = AppliedWarehouse;
        DraftBrand = AppliedBrand ?? string.Empty;
        DraftActiveKey = AppliedIsActive switch
        {
            true => "active",
            false => "inactive",
            _ => "all"
        };
        FilterPopupVisible = true;
        _ = LoadDraftSubClassesAsync(DraftClass);
    }

    protected async Task OnDraftClassChangedAsync(string? value)
    {
        DraftClass = value;
        DraftSubClass = null;
        await LoadDraftSubClassesAsync(DraftClass);
    }

    protected async Task ApplyFiltersAsync()
    {
        AppliedType = DraftType;
        AppliedClass = DraftClass;
        AppliedSubClass = DraftSubClass;
        AppliedWarehouse = DraftWarehouse;
        AppliedBrand = string.IsNullOrWhiteSpace(DraftBrand) ? null : DraftBrand.Trim();
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
        DraftType = null;
        DraftClass = null;
        DraftSubClass = null;
        DraftWarehouse = null;
        DraftBrand = string.Empty;
        DraftActiveKey = "active";
        SubClasses = [];
        AppliedType = null;
        AppliedClass = null;
        AppliedSubClass = null;
        AppliedWarehouse = null;
        AppliedBrand = null;
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
                .Select(x => new IvMasterKeyToken { Code = x.ICode, RowVersion = x.RowVersion })
                .ToList();

            IvMasterOperationResult<object> result = _pendingAction switch
            {
                PendingBulkAction.Activate => await StockMasters.SetActiveAsync(tokens, true),
                PendingBulkAction.Deactivate => await StockMasters.SetActiveAsync(tokens, false),
                PendingBulkAction.Delete => await StockMasters.DeleteAsync(tokens),
                _ => IvMasterOperationResult<object>.Fail(IvMasterErrorCode.Validation, "Unknown action.")
            };

            if (result.Succeeded)
            {
                StatusMessage = _pendingAction switch
                {
                    PendingBulkAction.Activate => "Item(s) activated.",
                    PendingBulkAction.Deactivate => "Item(s) deactivated.",
                    PendingBulkAction.Delete => "Item(s) deleted.",
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
            StatusMessage = "Select exactly one item to copy.";
            return;
        }

        Navigation.NavigateTo($"/inventory/items/new?copy={Uri.EscapeDataString(_selectedRows[0].ICode)}");
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
        var url = QueryHelpers.AddQueryString("/inventory/items/export", query);
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
            var check = await StockMasters.CanDeleteBulkAsync(_selectedRows.Select(x => x.ICode).ToList());
            if (!check.CanDelete)
            {
                ErrorMessage = check.Message ?? "One or more items cannot be deleted.";
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
            PendingBulkAction.Activate => $"Activate {_selectedRows.Count} selected item(s)?",
            PendingBulkAction.Deactivate => $"Deactivate {_selectedRows.Count} selected item(s)?",
            PendingBulkAction.Delete => $"Permanently delete {_selectedRows.Count} selected item(s)?",
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
        DataSource.UpdateFilters(new IvStockMasterListQuery
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            IsActive = AppliedIsActive,
            IType = AppliedType,
            IClassCode = AppliedClass,
            ISubClassCode = AppliedSubClass,
            DefWarehouse = AppliedWarehouse,
            Brand = AppliedBrand
        });
    }

    private async Task RefreshCompactPreviewAsync()
    {
        var query = DataSource.CurrentQuery;
        query.Skip = 0;
        query.Take = 50;
        var result = await StockMasters.SearchAsync(query);
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

    private async Task<(IReadOnlyList<IvStockMasterListRow> Rows, int TotalCount)> SearchPageAsync(
        IvStockMasterListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await StockMasters.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.Message ?? "Unable to load items.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.Data.TotalCount);
        return (result.Data.Rows, result.Data.TotalCount);
    }

    private async Task LoadLookupsAsync()
    {
        var types = await Lookups.ListActiveTypesAsync();
        var classes = await Lookups.ListActiveClassesAsync();
        var warehouses = await Lookups.ListActiveWarehousesAsync();

        Types = types.Succeeded ? types.Rows : [];
        Classes = classes.Succeeded ? classes.Rows : [];
        Warehouses = warehouses.Succeeded ? warehouses.Rows : [];

        if (!types.Succeeded || !classes.Succeeded || !warehouses.Succeeded)
        {
            ErrorMessage = types.ErrorMessage ?? classes.ErrorMessage ?? warehouses.ErrorMessage
                ?? "Unable to load filter lookups.";
        }
    }

    private async Task LoadDraftSubClassesAsync(string? classCode)
    {
        SubClasses = [];
        if (string.IsNullOrWhiteSpace(classCode))
        {
            SubClassesLoading = false;
            return;
        }

        var version = Interlocked.Increment(ref _subClassLoadVersion);
        SubClassesLoading = true;
        var result = await Lookups.ListActiveSubClassesAsync(classCode);
        if (version != _subClassLoadVersion)
        {
            return;
        }

        SubClassesLoading = false;
        SubClasses = result.Succeeded ? result.Rows : [];
    }

    private Dictionary<string, string?> BuildQueryDictionary()
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            dict["q"] = SearchText.Trim();
        }

        if (AppliedIsActive is bool active)
        {
            dict["active"] = active ? "1" : "0";
        }

        if (!string.IsNullOrWhiteSpace(AppliedType))
        {
            dict["type"] = AppliedType;
        }

        if (!string.IsNullOrWhiteSpace(AppliedClass))
        {
            dict["class"] = AppliedClass;
        }

        if (!string.IsNullOrWhiteSpace(AppliedSubClass))
        {
            dict["subclass"] = AppliedSubClass;
        }

        if (!string.IsNullOrWhiteSpace(AppliedWarehouse))
        {
            dict["warehouse"] = AppliedWarehouse;
        }

        if (!string.IsNullOrWhiteSpace(AppliedBrand))
        {
            dict["brand"] = AppliedBrand;
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
/// Server-side paging source for Item Master via <see cref="IIvStockMasterService.SearchAsync"/>.
/// Bound to DxGrid.Data as a <see cref="GridCustomDataSource"/>.
/// </summary>
public sealed class IvStockMasterGridDataSource : GridCustomDataSource
{
    private readonly Func<IvStockMasterListQuery, CancellationToken, Task<(IReadOnlyList<IvStockMasterListRow> Rows, int TotalCount)>> _loader;
    private IvStockMasterListQuery _filters = new();

    public IvStockMasterGridDataSource(
        Func<IvStockMasterListQuery, CancellationToken, Task<(IReadOnlyList<IvStockMasterListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public IvStockMasterListQuery CurrentQuery => Clone(_filters);

    public void UpdateFilters(IvStockMasterListQuery query) =>
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

    private static IvStockMasterListQuery Clone(IvStockMasterListQuery source) =>
        new()
        {
            SearchText = source.SearchText,
            IsActive = source.IsActive,
            IClassCode = source.IClassCode,
            ISubClassCode = source.ISubClassCode,
            IType = source.IType,
            DefWarehouse = source.DefWarehouse,
            Brand = source.Brand,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}
