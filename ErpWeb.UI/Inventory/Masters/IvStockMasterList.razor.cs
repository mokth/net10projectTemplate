using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Inventory.Lifecycle;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Inventory;
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
    protected string AppliedLifecycle = IvStockLifecycleFilterKeys.ActiveAndDiscontinued;

    protected string? DraftType;
    protected string? DraftClass;
    protected string? DraftSubClass;
    protected string? DraftWarehouse;
    protected string DraftBrand = string.Empty;
    protected string DraftLifecycleKey = IvStockLifecycleFilterKeys.ActiveAndDiscontinued;

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
        || !string.Equals(AppliedLifecycle, IvStockLifecycleFilterKeys.ActiveAndDiscontinued, StringComparison.OrdinalIgnoreCase);

    protected string ConfirmMessage { get; set; } = string.Empty;
    protected string ConfirmButtonText { get; set; } = "Confirm";
    protected ButtonRenderStyle ConfirmButtonStyle { get; set; } = ButtonRenderStyle.Primary;

    /// <summary>Audited reason for a lifecycle change; required (never inferred from the old status).</summary>
    protected string BulkReason { get; set; } = string.Empty;

    /// <summary>True for lifecycle commands, false for plain delete confirmation.</summary>
    protected bool BulkReasonVisible { get; set; }

    /// <summary>
    /// One dropdown for the whole lifecycle. The default view deliberately includes DISCONTINUED:
    /// hiding it would hide items that still carry open commitments.
    /// </summary>
    protected IReadOnlyList<LifecycleFilterOption> LifecycleFilterOptions { get; } =
    [
        new(IvStockLifecycleFilterKeys.ActiveAndDiscontinued, "Active + Discontinued"),
        new(IvStockLifecycleFilterKeys.Active, "Active only"),
        new(IvStockLifecycleFilterKeys.Discontinued, "Discontinued"),
        new(IvStockLifecycleFilterKeys.Inactive, "Inactive (retired)"),
        new(IvStockLifecycleFilterKeys.All, "All")
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
        new() { Caption = "Status", FieldName = nameof(IvStockMasterListRow.LifecycleLabel), Width = "130px", VisibleIndex = 11 },
        new() { Caption = "Discontinued", FieldName = nameof(IvStockMasterListRow.IsDiscontinued), DataType = "bool", Visible = false, VisibleIndex = 30 },
        // Reorder thresholds belong on the list: without them a low-stock alert cannot be explained
        // from the item it flags. Both are item-level columns and never alert when NULL or 0.
        new() { Caption = "Min stock", FieldName = nameof(IvStockMasterListRow.MinStock), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 12 },
        new() { Caption = "Max stock", FieldName = nameof(IvStockMasterListRow.MaxStock), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 13 },
        new() { Caption = "Barcode", FieldName = nameof(IvStockMasterListRow.Barcode), Visible = false, VisibleIndex = 14 },
        new() { Caption = "Sell UOM", FieldName = nameof(IvStockMasterListRow.SellingUom), Visible = false, VisibleIndex = 15 },
        new() { Caption = "Buy UOM", FieldName = nameof(IvStockMasterListRow.PurUom), Visible = false, VisibleIndex = 16 },
        new() { Caption = "Sell GL", FieldName = nameof(IvStockMasterListRow.SellingGlCode), Visible = false, VisibleIndex = 17 },
        new() { Caption = "Buy GL", FieldName = nameof(IvStockMasterListRow.PurchaseGlCode), Visible = false, VisibleIndex = 18 },
        new() { Caption = "Classification", FieldName = nameof(IvStockMasterListRow.Classification), Visible = false, VisibleIndex = 19 },
        new() { Caption = "Active", FieldName = nameof(IvStockMasterListRow.IsActive), DataType = "bool", Visible = false, VisibleIndex = 31 },
        ..AuditColumns.For(startVisibleIndex: 20)
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
            new() { Text = "DISCONTINUE", IConClass = "fa-solid fa-ban", Style = "warning", Enabled = CanEdit, ToolTip = "Stop new demand; existing obligations continue" },
            new() { Text = "SET INACTIVE", IConClass = "fa-solid fa-circle-stop", Style = "danger", Enabled = CanEdit, ToolTip = "Retire the item once every blocker is cleared" },
            new() { Text = "REACTIVATE", IConClass = "fa-solid fa-toggle-on", Style = "success", Enabled = CanEdit, ToolTip = "Return the item to ACTIVE" },
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
            case "DISCONTINUE":
                await BeginBulkAsync(PendingBulkAction.Discontinue);
                break;
            case "SET INACTIVE":
                await BeginBulkAsync(PendingBulkAction.Inactivate);
                break;
            case "REACTIVATE":
                await BeginBulkAsync(PendingBulkAction.Reactivate);
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
        DraftLifecycleKey = string.IsNullOrWhiteSpace(AppliedLifecycle)
            ? IvStockLifecycleFilterKeys.ActiveAndDiscontinued
            : AppliedLifecycle;
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
        AppliedLifecycle = string.IsNullOrWhiteSpace(DraftLifecycleKey)
            ? IvStockLifecycleFilterKeys.ActiveAndDiscontinued
            : DraftLifecycleKey;
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
        DraftLifecycleKey = IvStockLifecycleFilterKeys.ActiveAndDiscontinued;
        SubClasses = [];
        AppliedType = null;
        AppliedClass = null;
        AppliedSubClass = null;
        AppliedWarehouse = null;
        AppliedBrand = null;
        AppliedLifecycle = IvStockLifecycleFilterKeys.ActiveAndDiscontinued;
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

        var reason = (BulkReason ?? string.Empty).Trim();
        if (BulkReasonVisible && reason.Length < 3)
        {
            ErrorMessage = "Enter a reason for this lifecycle change (at least 3 characters).";
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

            if (_pendingAction == PendingBulkAction.Delete)
            {
                var deleteResult = await StockMasters.DeleteAsync(tokens);
                if (deleteResult.Succeeded)
                {
                    StatusMessage = "Item(s) deleted.";
                    ConfirmVisible = false;
                    _selectedRows.Clear();
                    await ReloadGridAsync();
                }
                else
                {
                    ErrorMessage = deleteResult.Message
                        ?? deleteResult.DeleteCheck?.Message
                        ?? "Unable to delete the selected item(s).";
                    ConfirmVisible = false;
                }

                return;
            }

            var targetStatus = _pendingAction switch
            {
                PendingBulkAction.Discontinue => IvStockItemLifecycleStatusCodes.Discontinued,
                PendingBulkAction.Inactivate => IvStockItemLifecycleStatusCodes.Inactive,
                PendingBulkAction.Reactivate => IvStockItemLifecycleStatusCodes.Active,
                _ => null
            };

            if (targetStatus is null)
            {
                ErrorMessage = "Unknown action.";
                ConfirmVisible = false;
                return;
            }

            var result = await StockMasters.ChangeLifecycleAsync(
                tokens,
                targetStatus,
                reason,
                IvStockLifecycleSourceActions.Bulk);

            if (result.Succeeded)
            {
                var changed = result.Data?.ChangedCount ?? tokens.Count;
                StatusMessage = $"{changed} item(s) changed to {LifecycleLabel(targetStatus)}.";
                ConfirmVisible = false;
                _selectedRows.Clear();
                await ReloadGridAsync();
            }
            else
            {
                ErrorMessage = BuildBulkLifecycleError(result);
                ConfirmVisible = false;
            }
        }
        finally
        {
            IsSubmitting = false;
            _pendingAction = PendingBulkAction.None;
            BulkReason = string.Empty;
        }
    }

    protected static string LifecycleCssClass(string? status) => status switch
    {
        IvStockItemLifecycleStatusCodes.Discontinued => "is-hold",
        IvStockItemLifecycleStatusCodes.Inactive => "is-off",
        _ => "is-on"
    };

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
        if (action is PendingBulkAction.Discontinue or PendingBulkAction.Inactivate or PendingBulkAction.Reactivate)
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

        // A retirement preview blocks the WHOLE batch. The server re-evaluates inside the
        // transition transaction; this check exists only to explain the outcome up front.
        if (action == PendingBulkAction.Inactivate)
        {
            var blocked = new List<string>();
            foreach (var row in _selectedRows)
            {
                var preview = await StockMasters.PreviewRetirementAsync(row.ICode);
                if (!preview.Succeeded || preview.Data is null)
                {
                    blocked.Add($"{row.ICode}: {preview.Message ?? "retirement could not be verified"}");
                }
                else if (!preview.Data.CanRetire)
                {
                    blocked.Add($"{row.ICode}: {preview.Data.Message ?? "outstanding obligations remain"}");
                }
            }

            if (blocked.Count > 0)
            {
                const int shown = 10;
                var detail = string.Join(" | ", blocked.Take(shown));
                var suffix = blocked.Count > shown ? $" (+{blocked.Count - shown} more)" : string.Empty;
                ErrorMessage =
                    $"{blocked.Count} of {_selectedRows.Count} selected items cannot be retired. No items changed. {detail}{suffix}";
                return;
            }
        }

        _pendingAction = action;
        BulkReason = string.Empty;
        BulkReasonVisible = action != PendingBulkAction.Delete;
        ConfirmButtonStyle = action switch
        {
            PendingBulkAction.Delete or PendingBulkAction.Inactivate => ButtonRenderStyle.Danger,
            PendingBulkAction.Discontinue => ButtonRenderStyle.Warning,
            _ => ButtonRenderStyle.Primary
        };
        ConfirmButtonText = action switch
        {
            PendingBulkAction.Discontinue => "Discontinue",
            PendingBulkAction.Inactivate => "Set inactive",
            PendingBulkAction.Reactivate => "Reactivate",
            PendingBulkAction.Delete => "Delete",
            _ => "Confirm"
        };
        ConfirmMessage = action switch
        {
            PendingBulkAction.Discontinue =>
                $"Discontinue {_selectedRows.Count} selected item(s)? New demand will be rejected immediately; existing obligations and corrections continue to be allowed.",
            PendingBulkAction.Inactivate =>
                $"Set {_selectedRows.Count} selected item(s) to INACTIVE? This retires the item for new business across the company. Existing history is preserved and nothing is deleted.",
            PendingBulkAction.Reactivate =>
                $"Reactivate {_selectedRows.Count} selected item(s)? They return to ACTIVE and accept new demand again. No existing document is reopened or altered.",
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
            LifecycleStatus = AppliedLifecycle,
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
            dict["searchText"] = SearchText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(AppliedLifecycle))
        {
            dict["lifecycle"] = AppliedLifecycle;
        }

        if (!string.IsNullOrWhiteSpace(AppliedType))
        {
            dict["iType"] = AppliedType;
        }

        if (!string.IsNullOrWhiteSpace(AppliedClass))
        {
            dict["iClassCode"] = AppliedClass;
        }

        if (!string.IsNullOrWhiteSpace(AppliedSubClass))
        {
            dict["iSubClassCode"] = AppliedSubClass;
        }

        if (!string.IsNullOrWhiteSpace(AppliedWarehouse))
        {
            dict["defWarehouse"] = AppliedWarehouse;
        }

        if (!string.IsNullOrWhiteSpace(AppliedBrand))
        {
            dict["brand"] = AppliedBrand;
        }

        return dict;
    }

    private static string LifecycleLabel(string status) => status switch
    {
        IvStockItemLifecycleStatusCodes.Active => "ACTIVE",
        IvStockItemLifecycleStatusCodes.Discontinued => "DISCONTINUED",
        IvStockItemLifecycleStatusCodes.Inactive => "INACTIVE",
        _ => status
    };

    private static string BuildBulkLifecycleError(IvMasterOperationResult<IvStockLifecycleBulkResult> result)
    {
        var baseMessage = result.Message ?? "Unable to change the item lifecycle.";
        var failures = result.Data?.Items
            .Where(x => !x.Succeeded)
            .Select(x => $"{x.ICode}: {x.Error}")
            .Take(10)
            .ToList() ?? [];

        return failures.Count == 0
            ? baseMessage
            : $"{baseMessage} {string.Join(" | ", failures)}";
    }

    protected sealed record LifecycleFilterOption(string Key, string Name);

    private enum PendingBulkAction
    {
        None,
        Discontinue,
        Inactivate,
        Reactivate,
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
            LifecycleStatus = source.LifecycleStatus,
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
