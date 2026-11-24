using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Security;
using ErpWeb.Core.Subcon;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Subcon;

public partial class SubconSettings : PageBase
{
    [Inject] private ISubconApplicationService Subcon { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IIvInventoryLookupService InventoryLookups { get; set; } = default!;

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool IsSavingMap;
    protected bool CanEdit;
    protected string? StatusMessage;
    protected SubconPolicyDto Policy { get; set; } = SubconApplicationService.DefaultPolicy();
    protected bool EditSubconEnabled;
    protected bool EditInventoryCustodyPostEnabled;
    protected string? EditDefaultCustodyWarehouseCode;
    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; private set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> MapLocations { get; private set; } = [];
    protected IReadOnlyList<SubconSupplierCustodyMapDto> SupplierMaps { get; private set; } = [];

    protected string MapSupplierCode { get; set; } = string.Empty;
    protected string? MapSupplierName { get; set; }
    protected string? MapWarehouseCode { get; set; }
    protected string? MapLocationCode { get; set; }
    protected bool MapIsActive { get; set; } = true;
    protected byte[]? MapRowVersion { get; set; }

    protected bool CanSave => CanEdit && !IsSubmitting && !IsLoading;
    protected bool CanSaveMap =>
        CanEdit
        && !IsSavingMap
        && !IsLoading
        && !string.IsNullOrWhiteSpace(MapSupplierCode)
        && !string.IsNullOrWhiteSpace(MapWarehouseCode)
        && !string.IsNullOrWhiteSpace(MapLocationCode);
    protected string BannerMessage => Policy.LiveCustodyReady ? string.Empty : SubconBlockedReasons.PostingNotEnabled;

    protected IReadOnlyList<FrozenGate> FrozenGates =>
    [
        new("Lot-controlled return", SubconBlockedReasons.LotReturnFrozen),
        new("BOM / assembly conversion", SubconBlockedReasons.BomConversionFrozen),
        new("Work Order external WIP", SubconBlockedReasons.ProductionModeFrozen),
        new("Processing fee capitalization", SubconBlockedReasons.FeeCapitalizationFrozen)
    ];

    protected override async Task OnPageInitializedAsync()
    {
        CanEdit = await AccessRights.CanAsync(MenuCodes.SubconSettings, PermissionCodes.Edit);
        var whTask = InventoryLookups.ListActiveWarehousesAsync();
        var mapTask = Subcon.GetSupplierCustodyMapsAsync();
        var result = await Subcon.GetPolicyAsync();
        var whResult = await whTask;
        var mapResult = await mapTask;
        if (whResult.Succeeded)
        {
            Warehouses = whResult.Rows;
        }

        if (mapResult.Succeeded && mapResult.SupplierCustodyMaps is not null)
        {
            SupplierMaps = mapResult.SupplierCustodyMaps;
        }

        if (!result.Succeeded || result.Policy is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to load subcontracting settings.";
            IsLoading = false;
            return;
        }

        ApplyPolicy(result.Policy);
        if (!string.IsNullOrWhiteSpace(Policy.DefaultCustodyWarehouseCode))
        {
            MapWarehouseCode = Policy.DefaultCustodyWarehouseCode;
            await LoadMapLocationsAsync(MapWarehouseCode);
        }

        IsLoading = false;
    }

    protected async Task OnSaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;
        IsSubmitting = true;
        var result = await Subcon.SavePolicyAsync(new SubconPolicySaveCommand
        {
            SubconEnabled = EditSubconEnabled,
            InventoryModeEnabled = EditInventoryCustodyPostEnabled,
            InventoryCustodyPostEnabled = EditInventoryCustodyPostEnabled,
            DefaultCustodyWarehouseCode = EditDefaultCustodyWarehouseCode,
            RowVersion = Policy.RowVersion
        });
        IsSubmitting = false;
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        if (result.Policy is not null)
        {
            ApplyPolicy(result.Policy);
        }

        StatusMessage = Policy.LiveCustodyReady
            ? "Settings saved. Non-lot same-SKU custody posting is enabled for this company."
            : "Settings saved. Live posting remains off.";
    }

    protected void OnMapSupplierSelected(PoSupplierLookupRow row)
    {
        MapSupplierCode = row.SuppCode;
        MapSupplierName = row.SuppName;
        var existing = SupplierMaps.FirstOrDefault(x =>
            string.Equals(x.SupplierCode, row.SuppCode, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            MapRowVersion = existing.RowVersion;
            MapIsActive = existing.IsActive;
        }
        else
        {
            MapRowVersion = null;
            MapIsActive = true;
        }
    }

    protected async Task OnMapWarehouseChangedAsync(string? warehouseCode)
    {
        MapWarehouseCode = warehouseCode;
        MapLocationCode = null;
        await LoadMapLocationsAsync(warehouseCode);
    }

    protected async Task EditSupplierMapAsync(SubconSupplierCustodyMapDto row)
    {
        MapSupplierCode = row.SupplierCode;
        MapSupplierName = row.SupplierName;
        MapWarehouseCode = row.CustodyWarehouseCode;
        await LoadMapLocationsAsync(row.CustodyWarehouseCode);
        MapLocationCode = row.CustodyLocationCode;
        MapIsActive = row.IsActive;
        MapRowVersion = row.RowVersion;
    }

    protected void ClearSupplierMapEditor()
    {
        MapSupplierCode = string.Empty;
        MapSupplierName = null;
        MapWarehouseCode = Policy.DefaultCustodyWarehouseCode;
        MapLocationCode = null;
        MapIsActive = true;
        MapRowVersion = null;
    }

    protected async Task OnSaveSupplierMapAsync()
    {
        if (!CanSaveMap)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;
        IsSavingMap = true;
        var result = await Subcon.SaveSupplierCustodyMapAsync(new SubconSupplierCustodyMapSaveCommand
        {
            SupplierCode = MapSupplierCode,
            CustodyWarehouseCode = MapWarehouseCode ?? string.Empty,
            CustodyLocationCode = MapLocationCode ?? string.Empty,
            IsActive = MapIsActive,
            RowVersion = MapRowVersion
        });
        IsSavingMap = false;
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        var reload = await Subcon.GetSupplierCustodyMapsAsync();
        if (reload.Succeeded && reload.SupplierCustodyMaps is not null)
        {
            SupplierMaps = reload.SupplierCustodyMaps;
        }

        StatusMessage = result.InfoMessage ?? "Supplier custody mapping saved.";
        ClearSupplierMapEditor();
    }

    private async Task LoadMapLocationsAsync(string? warehouseCode)
    {
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            MapLocations = [];
            return;
        }

        var locs = await InventoryLookups.ListActiveLocationsAsync(warehouseCode.Trim());
        MapLocations = locs.Succeeded ? locs.Rows : [];
    }

    private void ApplyPolicy(SubconPolicyDto policy)
    {
        Policy = policy;
        EditSubconEnabled = policy.SubconEnabled;
        EditInventoryCustodyPostEnabled = policy.InventoryCustodyPostEnabled;
        EditDefaultCustodyWarehouseCode = policy.DefaultCustodyWarehouseCode;
    }

    protected readonly record struct FrozenGate(string Title, string Help);
}
