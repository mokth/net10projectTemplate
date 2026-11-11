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

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool CanEdit;
    protected string? StatusMessage;
    protected SubconPolicyDto Policy { get; set; } = SubconApplicationService.DefaultPolicy();

    protected bool CanSave => CanEdit && !IsSubmitting && !IsLoading;

    protected IReadOnlyList<FrozenGate> FrozenGates =>
    [
        new("Live subcontracting", SubconBlockedReasons.LiveEnablementFrozen),
        new("Inventory send-out", SubconBlockedReasons.InventoryModeFrozen),
        new("Work Order send-out", SubconBlockedReasons.ProductionModeFrozen)
    ];

    protected override async Task OnPageInitializedAsync()
    {
        CanEdit = await AccessRights.CanAsync(MenuCodes.SubconSettings, PermissionCodes.Edit);
        var result = await Subcon.GetPolicyAsync();
        if (!result.Succeeded || result.Policy is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to load subcontracting settings.";
            IsLoading = false;
            return;
        }

        Policy = result.Policy;
        IsLoading = false;
    }

    protected async Task OnSaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        IsSubmitting = true;
        var result = await Subcon.SavePolicyAsync(new SubconPolicySaveCommand
        {
            RowVersion = Policy.RowVersion
        });
        IsSubmitting = false;
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        Policy = result.Policy ?? Policy;
        StatusMessage = "Settings saved. Live modes stay off.";
    }

    protected readonly record struct FrozenGate(string Title, string Help);
}
