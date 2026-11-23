using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
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
    protected bool CanEdit;
    protected string? StatusMessage;
    protected SubconPolicyDto Policy { get; set; } = SubconApplicationService.DefaultPolicy();
    protected bool EditSubconEnabled;
    protected bool EditInventoryCustodyPostEnabled;
    protected string? EditDefaultCustodyWarehouseCode;
    protected IReadOnlyList<IvCodeLookupRow> Warehouses { get; private set; } = [];

    protected bool CanSave => CanEdit && !IsSubmitting && !IsLoading;
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
        var result = await Subcon.GetPolicyAsync();
        var whResult = await whTask;
        if (whResult.Succeeded)
        {
            Warehouses = whResult.Rows;
        }

        if (!result.Succeeded || result.Policy is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to load subcontracting settings.";
            IsLoading = false;
            return;
        }

        ApplyPolicy(result.Policy);
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

    private void ApplyPolicy(SubconPolicyDto policy)
    {
        Policy = policy;
        EditSubconEnabled = policy.SubconEnabled;
        EditInventoryCustodyPostEnabled = policy.InventoryCustodyPostEnabled;
        EditDefaultCustodyWarehouseCode = policy.DefaultCustodyWarehouseCode;
    }

    protected readonly record struct FrozenGate(string Title, string Help);
}
