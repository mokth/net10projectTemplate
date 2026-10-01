using System.Collections;
using System.Timers;
using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Timer = System.Timers.Timer;

namespace ErpWeb.UI.Planning.Masters;

public partial class PrProductDefList : PageBase, IDisposable
{
    [Inject] private IPrProductDefService ProductDefs { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? _grid;
    private Timer? _searchDebounce;
    private int _searchVersion;
    private readonly List<PrProductDefListRow> _selectedRows = [];

    protected bool IsBootstrapping = true;
    protected bool IsSubmitting;
    protected bool FilterPopupVisible;
    protected bool ConfirmVisible;
    protected string? StatusMessage;
    protected string SearchText = string.Empty;
    protected int TotalCount;
    protected List<PrProductDefListRow> CompactRows { get; set; } = [];

    protected bool CanAdd;
    protected bool CanEdit;
    protected bool CanDelete;

    protected string? AppliedProdCode;
    protected string? AppliedProdDesc;
    protected string? AppliedComponentCode;
    protected string? AppliedComponentDesc;
    protected string? AppliedWarehouse;
    protected bool? AppliedIsActive;

    protected string DraftProdCode = string.Empty;
    protected string DraftProdDesc = string.Empty;
    protected string DraftComponentCode = string.Empty;
    protected string DraftComponentDesc = string.Empty;
    protected string? DraftWarehouse;
    protected string DraftActiveKey = "all";

    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected PrProductDefGridDataSource DataSource { get; private set; } = default!;

    protected string TotalCountLabel => TotalCount == 1 ? "1 definition" : $"{TotalCount:N0} definitions";

    protected bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.IsNullOrWhiteSpace(AppliedProdCode)
        || !string.IsNullOrWhiteSpace(AppliedProdDesc)
        || !string.IsNullOrWhiteSpace(AppliedComponentCode)
        || !string.IsNullOrWhiteSpace(AppliedComponentDesc)
        || !string.IsNullOrWhiteSpace(AppliedWarehouse)
        || AppliedIsActive is not null;

    protected string ConfirmMessage { get; set; } = string.Empty;

    protected IReadOnlyList<ActiveFilterOption> ActiveFilterOptions { get; } =
    [
        new("all", "All"),
        new("active", "Active"),
        new("inactive", "Inactive")
    ];

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Product Code", FieldName = nameof(PrProductDefListRow.ProdCode), Width = "140px", SortIndex = 0, VisibleIndex = 1 },
        new() { Caption = "Product Description", FieldName = nameof(PrProductDefListRow.ProdDesc), VisibleIndex = 2 },
        new() { Caption = "Definition Code", FieldName = nameof(PrProductDefListRow.DefinitionCode), Width = "130px", VisibleIndex = 3 },
        new() { Caption = "Definition Name", FieldName = nameof(PrProductDefListRow.DefinitionName), Width = "160px", VisibleIndex = 4 },
        new() { Caption = "Default", FieldName = nameof(PrProductDefListRow.IsDefaultDefinition), DataType = "bool", Width = "90px", VisibleIndex = 5 },
        new() { Caption = "Active", FieldName = nameof(PrProductDefListRow.ActiveVersion), DataType = "number", Width = "90px", VisibleIndex = 6 },
        new() { Caption = "Latest", FieldName = nameof(PrProductDefListRow.LatestVersion), DataType = "number", Width = "90px", VisibleIndex = 7 },
        new() { Caption = "Status", FieldName = nameof(PrProductDefListRow.BomStatus), Width = "110px", VisibleIndex = 8 },
        new() { Caption = "Mfg Type", FieldName = nameof(PrProductDefListRow.MfgType), Width = "90px", VisibleIndex = 9 },
        new() { Caption = "BOM Item Count", FieldName = nameof(PrProductDefListRow.BomItemCount), DataType = "number", Width = "120px", VisibleIndex = 10 },
        ..AuditColumns.For(startVisibleIndex: 11)
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new PrProductDefGridDataSource(LoadPageAsync);

        CanAdd = await AccessRights.CanAsync(MenuCodes.PlanningProductDef, PermissionCodes.Add);
        CanEdit = await AccessRights.CanAsync(MenuCodes.PlanningProductDef, PermissionCodes.Edit);
        CanDelete = await AccessRights.CanAsync(MenuCodes.PlanningProductDef, PermissionCodes.Delete);

        Buttons =
        [
            new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd },
            new() { Text = "DELETE", IConClass = "far fa-trash-alt", Style = "danger", Enabled = CanDelete }
        ];

        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary", ToolTip = "View" },
            new() { Text = "EDIT / REVISE", IConClass = "far fa-edit", Style = "primary", ToolTip = "Edit draft or create a new version", Enabled = CanEdit || CanAdd },
            new() { Text = "EXPLODE", IConClass = "fa-solid fa-sitemap", Style = "primary", ToolTip = "Explode BOM" }
        ];

        var warehouses = await Lookups.ListActiveWarehousesAsync();
        Warehouses = warehouses.Succeeded ? warehouses.Rows : [];
        if (!warehouses.Succeeded)
        {
            ErrorMessage = warehouses.ErrorMessage ?? "Unable to load warehouses.";
        }

        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        IsBootstrapping = false;
    }

    protected void OnGridInstance(DxGrid gridInstance) => _grid = gridInstance;

    protected void OnSelectionsEvent(List<PrProductDefListRow> list)
    {
        _selectedRows.Clear();
        _selectedRows.AddRange(list);
    }

    protected async Task OnButtonClick(SelectedButtonInfo<PrProductDefListRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW":
                if (!CanAdd) { StatusMessage = "Access Denied!!"; return; }
                Navigation.NavigateTo("/planning/product-definitions/new");
                break;
            case "DELETE":
                if (!CanDelete) { StatusMessage = "Access Denied!!"; return; }
                if (_selectedRows.Count == 0)
                {
                    ErrorMessage = "Select at least one product definition.";
                    return;
                }

                ConfirmMessage = _selectedRows.Count == 1
                    ? $"Delete product definition {_selectedRows[0].ProdCode}[{_selectedRows[0].DefinitionCode}]?"
                    : $"Delete {_selectedRows.Count} product definitions?";
                ConfirmVisible = true;
                break;
        }

        await Task.CompletedTask;
    }

    protected async Task OnActionClick(SelectedButtonInfo<PrProductDefListRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return;
        }

        var row = info.SelectedRow;
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "VIEW":
                OpenDefinition("view", row.ProdCode, row.DefinitionCode);
                break;
            case "EDIT / REVISE":
                if (string.Equals(row.BomStatus, PrBomStatuses.Draft, StringComparison.OrdinalIgnoreCase))
                {
                    if (!CanEdit)
                    {
                        StatusMessage = "Access Denied!!";
                        break;
                    }
                    OpenDefinition("edit", row.ProdCode, row.DefinitionCode);
                    break;
                }

                if (!CanAdd)
                {
                    StatusMessage = "Add permission is required to create a new version.";
                    break;
                }
                var revision = await ProductDefs.CreateNewVersionAsync(
                    row.ProdCode, row.DefinitionCode, row.LatestVersion);
                if (!revision.Succeeded)
                {
                    ErrorMessage = revision.Message ?? "Unable to create a new Product Definition version.";
                    break;
                }
                OpenDefinition("edit", row.ProdCode, row.DefinitionCode);
                break;
            case "EXPLODE":
                Navigation.NavigateTo(
                    $"/planning/product-definitions/explode/{Uri.EscapeDataString(row.ProdCode)}/{Uri.EscapeDataString(row.DefinitionCode)}");
                break;
        }

    }

    protected void OpenDefinition(string mode, string prodCode, string definitionCode) =>
        Navigation.NavigateTo(
            $"/planning/product-definitions/{mode}/{Uri.EscapeDataString(prodCode)}/{Uri.EscapeDataString(definitionCode)}");

    protected void OpenView(PrProductDefListRow row) =>
        OpenDefinition("view", row.ProdCode, row.DefinitionCode);

    protected void OpenFilterPopup()
    {
        DraftProdCode = AppliedProdCode ?? string.Empty;
        DraftProdDesc = AppliedProdDesc ?? string.Empty;
        DraftComponentCode = AppliedComponentCode ?? string.Empty;
        DraftComponentDesc = AppliedComponentDesc ?? string.Empty;
        DraftWarehouse = AppliedWarehouse;
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
        AppliedProdCode = NullIfEmpty(DraftProdCode);
        AppliedProdDesc = NullIfEmpty(DraftProdDesc);
        AppliedComponentCode = NullIfEmpty(DraftComponentCode);
        AppliedComponentDesc = NullIfEmpty(DraftComponentDesc);
        AppliedWarehouse = NullIfEmpty(DraftWarehouse);
        AppliedIsActive = DraftActiveKey switch
        {
            "active" => true,
            "inactive" => false,
            _ => null
        };
        FilterPopupVisible = false;
        await ReloadGridAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DraftProdCode = DraftProdDesc = DraftComponentCode = DraftComponentDesc = string.Empty;
        DraftWarehouse = null;
        DraftActiveKey = "all";
        await ApplyFiltersAsync();
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
            if (version != _searchVersion)
            {
                return;
            }

            await InvokeAsync(async () => await ReloadGridAsync());
        };
        _searchDebounce.Start();
        await Task.CompletedTask;
    }

    protected async Task ConfirmDeleteAsync()
    {
        IsSubmitting = true;
        try
        {
            var codes = _selectedRows.Select(x => new PrProductDefinitionKey
            {
                ProdCode = x.ProdCode,
                DefinitionCode = x.DefinitionCode
            }).ToList();
            var result = await ProductDefs.DeleteAsync(codes);
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message ?? "Delete failed.";
                return;
            }

            StatusMessage = codes.Count == 1
                ? $"Deleted product definition {codes[0].ProdCode}[{codes[0].DefinitionCode}]."
                : $"Deleted {codes.Count} product definitions.";
            ConfirmVisible = false;
            _selectedRows.Clear();
            await ReloadGridAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private async Task ReloadGridAsync()
    {
        SyncDataSourceFilters();
        await RefreshCompactPreviewAsync();
        _grid?.Reload();
        await InvokeAsync(StateHasChanged);
    }

    private void SyncDataSourceFilters() =>
        DataSource.UpdateFilters(new PrProductDefListQuery
        {
            SearchText = NullIfEmpty(SearchText),
            ProdCode = AppliedProdCode,
            ProdDesc = AppliedProdDesc,
            ComponentCode = AppliedComponentCode,
            ComponentDesc = AppliedComponentDesc,
            Warehouse = AppliedWarehouse,
            IsActive = AppliedIsActive
        });

    private async Task<(IReadOnlyList<PrProductDefListRow> Rows, int TotalCount)> LoadPageAsync(
        PrProductDefListQuery query,
        CancellationToken cancellationToken)
    {
        var result = await ProductDefs.SearchAsync(query, cancellationToken);
        if (!result.Succeeded || result.Data is null)
        {
            await InvokeAsync(() =>
            {
                ErrorMessage = result.Message ?? "Unable to load product definitions.";
                TotalCount = 0;
            });
            return ([], 0);
        }

        await InvokeAsync(() => TotalCount = result.Data.TotalCount);
        return (result.Data.Rows, result.Data.TotalCount);
    }

    private async Task RefreshCompactPreviewAsync()
    {
        var query = DataSource.CurrentQuery;
        query.Skip = 0;
        query.Take = 50;
        var result = await ProductDefs.SearchAsync(query);
        if (result.Succeeded && result.Data is not null)
        {
            CompactRows = result.Data.Rows.ToList();
            TotalCount = result.Data.TotalCount;
        }
        else
        {
            CompactRows = [];
        }
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Dispose();
    }

    protected sealed record ActiveFilterOption(string Key, string Name);
}

public sealed class PrProductDefGridDataSource : GridCustomDataSource
{
    private readonly Func<PrProductDefListQuery, CancellationToken, Task<(IReadOnlyList<PrProductDefListRow> Rows, int TotalCount)>> _loader;
    private PrProductDefListQuery _filters = new();

    public PrProductDefGridDataSource(
        Func<PrProductDefListQuery, CancellationToken, Task<(IReadOnlyList<PrProductDefListRow> Rows, int TotalCount)>> loader)
    {
        _loader = loader;
    }

    public PrProductDefListQuery CurrentQuery => Clone(_filters);

    public void UpdateFilters(PrProductDefListQuery query) =>
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

    private static PrProductDefListQuery Clone(PrProductDefListQuery source) =>
        new()
        {
            SearchText = source.SearchText,
            ProdCode = source.ProdCode,
            ProdDesc = source.ProdDesc,
            ComponentCode = source.ComponentCode,
            ComponentDesc = source.ComponentDesc,
            Warehouse = source.Warehouse,
            IsActive = source.IsActive,
            SortField = source.SortField,
            SortDescending = source.SortDescending,
            Skip = source.Skip,
            Take = source.Take
        };
}
