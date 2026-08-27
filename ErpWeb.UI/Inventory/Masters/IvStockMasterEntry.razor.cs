using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Masters;

public partial class IvStockMasterEntry : PageBase
{
    [Parameter] public string Mode { get; set; } = "view";
    [Parameter] public string? ICode { get; set; }
    [SupplyParameterFromQuery(Name = "copy")] public string? CopyFrom { get; set; }

    [Inject] private IIvStockMasterService StockMasters { get; set; } = default!;
    [Inject] private IIvInventoryLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private int _subClassLoadVersion;
    private int _locationLoadVersion;
    private string? _pendingSubClass;
    private string? _pendingLocation;
    private string _cleanSnapshot = string.Empty;
    private bool _lookupsLoaded;
    private string? _loadedKey;

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool ConfirmDiscardVisible;
    protected bool ConcurrencyVisible;
    protected bool SubClassesLoading;
    protected bool LocationsLoading;
    protected bool CanEdit;
    protected string? StatusMessage;
    protected IvStockMasterEditVm Model { get; set; } = CreateBlank();
    protected Dictionary<string, string> ValidationErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    protected IReadOnlyList<IvCodeLookupRow> Types { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Classes { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> SubClasses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Uoms { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; set; } = [];
    protected IReadOnlyList<IvCodeLookupRow> Locations { get; set; } = [];

    protected bool IsNewMode => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsEditMode => string.Equals(Mode, "edit", StringComparison.OrdinalIgnoreCase);
    protected bool IsViewMode => !IsNewMode && !IsEditMode;

    protected string PageHeading => IsNewMode
        ? (string.IsNullOrWhiteSpace(CopyFrom) ? "New item" : "Copy item")
        : IsEditMode
            ? "Edit item"
            : "View item";

    protected string ModeChip => IsNewMode ? "New" : IsEditMode ? "Edit" : "View";

    protected bool IsDirty =>
        !IsViewMode
        && !IsLoading
        && !string.Equals(_cleanSnapshot, Snapshot(Model), StringComparison.Ordinal);

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        if (!_lookupsLoaded)
        {
            CanEdit = await AccessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Edit);
            await LoadLookupsAsync();
            _lookupsLoaded = true;
        }

        var key = $"{Mode}|{ICode}|{CopyFrom}";
        if (string.Equals(key, _loadedKey, StringComparison.Ordinal))
        {
            return;
        }

        _loadedKey = key;
        await LoadPageAsync();
    }

    protected override Task OnPageInitializedAsync() => Task.CompletedTask;

    protected async Task OnClassChangedAsync(string? value)
    {
        Model.IClassCode = value;
        Model.ISubClassCode = null;
        _pendingSubClass = null;
        await LoadSubClassesAsync(Model.IClassCode);
    }

    protected async Task OnWarehouseChangedAsync(string? value)
    {
        Model.DefWarehouse = value;
        Model.DefLocation = null;
        _pendingLocation = null;
        await LoadLocationsAsync(Model.DefWarehouse);
    }

    protected async Task OnSaveAsync()
    {
        if (IsSubmitting || IsViewMode)
        {
            return;
        }

        var permission = IsNewMode ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await AccessRights.CanAsync(MenuCodes.InventoryItemMaster, permission))
        {
            ErrorMessage = "Access denied.";
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var result = await StockMasters.SaveAsync(Model, IsNewMode);
            if (result.Succeeded)
            {
                Navigation.NavigateTo("/inventory/items");
                return;
            }

            if (result.ErrorCode == IvMasterErrorCode.Concurrency)
            {
                ConcurrencyVisible = true;
                ErrorMessage = result.Message ?? "Concurrency conflict.";
            }
            else
            {
                ErrorMessage = result.Message ?? "Unable to save item.";
                ValidationErrors = result.ValidationErrors.ToDictionary(
                    x => x.Key,
                    x => x.Value,
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected Task OnCancelAsync()
    {
        if (IsDirty)
        {
            ConfirmDiscardVisible = true;
            return Task.CompletedTask;
        }

        Navigation.NavigateTo("/inventory/items");
        return Task.CompletedTask;
    }

    protected Task OnCloseAsync()
    {
        Navigation.NavigateTo("/inventory/items");
        return Task.CompletedTask;
    }

    protected void OnEditFromView()
    {
        if (string.IsNullOrWhiteSpace(Model.ICode))
        {
            return;
        }

        Navigation.NavigateTo($"/inventory/items/edit/{Uri.EscapeDataString(Model.ICode)}");
    }

    protected void ConfirmDiscardAsync()
    {
        ConfirmDiscardVisible = false;
        Navigation.NavigateTo("/inventory/items");
    }

    protected async Task ReloadLatestAsync()
    {
        ConcurrencyVisible = false;
        if (string.IsNullOrWhiteSpace(Model.ICode) && string.IsNullOrWhiteSpace(ICode))
        {
            return;
        }

        var code = Model.ICode;
        if (string.IsNullOrWhiteSpace(code))
        {
            code = ICode!;
        }

        var result = await StockMasters.GetAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            ErrorMessage = result.Message ?? "Unable to reload item.";
            return;
        }

        Model = Clone(result.Data);
        ValidationErrors.Clear();
        ErrorMessage = null;
        StatusMessage = "Loaded latest version.";
        await AfterModelLoadedAsync(preservePending: false);
        CaptureCleanSnapshot();
    }

    protected async Task KeepMyChangesAsync()
    {
        ConcurrencyVisible = false;
        var code = Model.ICode;
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var result = await StockMasters.GetAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            ErrorMessage = result.Message ?? "Unable to refresh concurrency token.";
            return;
        }

        // Keep field edits; adopt latest RowVersion so the next save can overwrite.
        Model.RowVersion = result.Data.RowVersion;
        StatusMessage = "Kept your changes. Save again to overwrite.";
        await Task.CompletedTask;
    }

    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    protected static string FormatUtc(DateTime? value) =>
        value.HasValue ? value.Value.ToLocalTime().ToString("g") : "—";

    protected static string FormatDec(decimal? value, string format = "n4") =>
        value.HasValue ? value.Value.ToString(format) : "—";

    private async Task LoadPageAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = null;
        ValidationErrors.Clear();
        ConcurrencyVisible = false;
        ConfirmDiscardVisible = false;

        try
        {
            if (IsNewMode)
            {
                if (!string.IsNullOrWhiteSpace(CopyFrom))
                {
                    var copyResult = await StockMasters.GetAsync(CopyFrom);
                    if (!copyResult.Succeeded || copyResult.Data is null)
                    {
                        ErrorMessage = copyResult.Message ?? "Unable to copy item.";
                        Model = CreateBlank();
                    }
                    else
                    {
                        Model = Clone(copyResult.Data);
                        Model.ICode = string.Empty;
                        Model.Barcode = null;
                        Model.RowVersion = null;
                        Model.CreatedBy = null;
                        Model.CreatedDate = null;
                        Model.ModifiedBy = null;
                        Model.ModifiedDate = null;
                        StatusMessage = $"Copied from {CopyFrom}.";
                    }
                }
                else
                {
                    Model = CreateBlank();
                }

                await AfterModelLoadedAsync(preservePending: true);
                CaptureCleanSnapshot();
                return;
            }

            var code = ICode ?? string.Empty;
            if (string.IsNullOrWhiteSpace(code))
            {
                ErrorMessage = "Item code is required.";
                Model = CreateBlank();
                return;
            }

            var result = await StockMasters.GetAsync(code);
            if (!result.Succeeded || result.Data is null)
            {
                ErrorMessage = result.Message ?? "Item was not found.";
                Model = CreateBlank();
                return;
            }

            Model = Clone(result.Data);
            await AfterModelLoadedAsync(preservePending: true);
            CaptureCleanSnapshot();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task AfterModelLoadedAsync(bool preservePending)
    {
        if (preservePending)
        {
            _pendingSubClass = Model.ISubClassCode;
            _pendingLocation = Model.DefLocation;
        }

        await LoadSubClassesAsync(Model.IClassCode);
        await LoadLocationsAsync(Model.DefWarehouse);

        if (!string.IsNullOrWhiteSpace(_pendingSubClass) &&
            SubClasses.Any(x => string.Equals(x.Code, _pendingSubClass, StringComparison.OrdinalIgnoreCase)))
        {
            Model.ISubClassCode = _pendingSubClass;
        }

        if (!string.IsNullOrWhiteSpace(_pendingLocation) &&
            Locations.Any(x => string.Equals(x.Code, _pendingLocation, StringComparison.OrdinalIgnoreCase)))
        {
            Model.DefLocation = _pendingLocation;
        }

        _pendingSubClass = null;
        _pendingLocation = null;
    }

    private async Task LoadLookupsAsync()
    {
        var types = await Lookups.ListActiveTypesAsync();
        var classes = await Lookups.ListActiveClassesAsync();
        var uoms = await Lookups.ListActiveUomsAsync();
        var warehouses = await Lookups.ListActiveWarehousesAsync();

        Types = types.Succeeded ? types.Rows : [];
        Classes = classes.Succeeded ? classes.Rows : [];
        Uoms = uoms.Succeeded ? uoms.Rows : [];
        Warehouses = warehouses.Succeeded ? warehouses.Rows : [];

        if (!types.Succeeded || !classes.Succeeded || !uoms.Succeeded || !warehouses.Succeeded)
        {
            ErrorMessage = types.ErrorMessage ?? classes.ErrorMessage ?? uoms.ErrorMessage ?? warehouses.ErrorMessage
                ?? "Unable to load lookups.";
        }
    }

    private async Task LoadSubClassesAsync(string? classCode)
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

    private async Task LoadLocationsAsync(string? warehouseCode)
    {
        Locations = [];
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            LocationsLoading = false;
            return;
        }

        var version = Interlocked.Increment(ref _locationLoadVersion);
        LocationsLoading = true;
        var result = await Lookups.ListActiveLocationsAsync(warehouseCode);
        if (version != _locationLoadVersion)
        {
            return;
        }

        LocationsLoading = false;
        Locations = result.Succeeded ? result.Rows : [];
    }

    private void CaptureCleanSnapshot() => _cleanSnapshot = Snapshot(Model);

    private static string Snapshot(IvStockMasterEditVm model) =>
        JsonSerializer.Serialize(model);

    private static IvStockMasterEditVm CreateBlank() =>
        new()
        {
            IsActive = true,
            StockControl = true
        };

    private static IvStockMasterEditVm Clone(IvStockMasterEditVm source) =>
        new()
        {
            ICode = source.ICode,
            IDesc = source.IDesc,
            Barcode = source.Barcode,
            Brand = source.Brand,
            IsActive = source.IsActive,
            IType = source.IType,
            IClassCode = source.IClassCode,
            ISubClassCode = source.ISubClassCode,
            StdUom = source.StdUom,
            SellingUom = source.SellingUom,
            PurUom = source.PurUom,
            StockControl = source.StockControl,
            LotControl = source.LotControl,
            DefWarehouse = source.DefWarehouse,
            DefLocation = source.DefLocation,
            MinStock = source.MinStock,
            MaxStock = source.MaxStock,
            StdPackSize = source.StdPackSize,
            PurStdPackSize = source.PurStdPackSize,
            SellingPrice = source.SellingPrice,
            PurchasePrice = source.PurchasePrice,
            SellingGlCode = source.SellingGlCode,
            PurchaseGlCode = source.PurchaseGlCode,
            TaxGroup = source.TaxGroup,
            PurchaseTaxGroup = source.PurchaseTaxGroup,
            Classification = source.Classification,
            Size = source.Size,
            Color = source.Color,
            RowVersion = source.RowVersion,
            CreatedDate = source.CreatedDate,
            CreatedBy = source.CreatedBy,
            ModifiedDate = source.ModifiedDate,
            ModifiedBy = source.ModifiedBy
        };
}
