using ErpWeb.Core.Menus;
using ErpWeb.Core.Quality;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Quality;

public partial class QaSettings : PageBase
{
    [Inject] private IQaPolicyService Policies { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool CanEdit;
    protected bool InspectorApproverSeparation = true;
    protected bool IncomingInspectionEnabled;
    protected bool FinalFgInspectionEnabled;
    protected bool LogicalHoldEnabled;
    protected bool PhysicalHoldEnabled;
    protected bool NonGoodEnabled;
    protected string? StatusMessage;
    protected QaPolicyDto Policy { get; set; } = QaPolicyService.DefaultPolicy();

    protected bool CanSave => CanEdit && !IsSubmitting && !IsLoading;

    protected override async Task OnPageInitializedAsync()
    {
        CanEdit = await AccessRights.CanAsync(MenuCodes.QualitySettings, PermissionCodes.Edit);
        var result = await Policies.GetAsync();
        if (!result.Succeeded || result.Policy is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to load quality settings.";
            IsLoading = false;
            return;
        }

        Policy = result.Policy;
        InspectorApproverSeparation = Policy.InspectorApproverSeparation;
        IncomingInspectionEnabled = Policy.IncomingInspectionEnabled;
        FinalFgInspectionEnabled = Policy.FinalFgInspectionEnabled;
        LogicalHoldEnabled = Policy.LogicalHoldEnabled;
        PhysicalHoldEnabled = Policy.PhysicalHoldEnabled;
        NonGoodEnabled = Policy.NonGoodEnabled;
        IsLoading = false;
    }

    protected async Task OnSaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        IsSubmitting = true;
        var result = await Policies.SaveAsync(new QaPolicySaveCommand
        {
            InspectorApproverSeparation = InspectorApproverSeparation,
            Enabled = IncomingInspectionEnabled,
            IncomingInspectionEnabled = IncomingInspectionEnabled,
            FinalFgInspectionEnabled = FinalFgInspectionEnabled,
            LogicalHoldEnabled = LogicalHoldEnabled,
            PhysicalHoldEnabled = PhysicalHoldEnabled,
            NonGoodEnabled = NonGoodEnabled,
            RowVersion = Policy.RowVersion
        });
        IsSubmitting = false;
        if (!result.Succeeded || result.Policy is null)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        Policy = result.Policy;
        InspectorApproverSeparation = Policy.InspectorApproverSeparation;
        IncomingInspectionEnabled = Policy.IncomingInspectionEnabled;
        FinalFgInspectionEnabled = Policy.FinalFgInspectionEnabled;
        LogicalHoldEnabled = Policy.LogicalHoldEnabled;
        PhysicalHoldEnabled = Policy.PhysicalHoldEnabled;
        NonGoodEnabled = Policy.NonGoodEnabled;
        StatusMessage = StatusFor(
            IncomingInspectionEnabled, FinalFgInspectionEnabled, LogicalHoldEnabled, PhysicalHoldEnabled, NonGoodEnabled);
    }

    protected void OnClose() => Navigation.NavigateTo("/quality/cases");
    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    private static string StatusFor(bool incoming, bool finalFg, bool logicalHold, bool physicalHold, bool nonGood)
    {
        if (nonGood)
        {
            return "Quality settings saved. Posting scrap/reject/hold on Daily Production now needs an approved inspection. It does not create a valued lot.";
        }

        if (physicalHold)
        {
            return "Quality settings saved. Approving a posted-stock hold now reserves that qty on the warehouse pile. It does not convert IStatus to QCHOLD.";
        }

        if (incoming && finalFg && logicalHold)
        {
            return "Quality settings saved. Incoming receipts, finished-good receipts, and WIP holds now need an approved inspection.";
        }

        if (incoming && finalFg)
        {
            return "Quality settings saved. Incoming receipts and finished-good receipts now need an approved inspection.";
        }

        if (incoming)
        {
            return "Quality settings saved. Incoming goods receipts now need an approved inspection.";
        }

        if (finalFg)
        {
            return "Quality settings saved. Finished-good receipts now need an approved inspection.";
        }

        if (logicalHold)
        {
            return "Quality settings saved. Approving a WIP hold now reserves that qty on the production lot. It does not move stock to a QA warehouse.";
        }

        return "Quality settings saved. Incoming, finished-goods, WIP-hold, physical-hold, and non-good Daily Production inspection stay off — posting is unchanged.";
    }
}
