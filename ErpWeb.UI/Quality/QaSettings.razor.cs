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
    protected bool HelpVisible;
    protected bool InspectorApproverSeparation = true;
    protected string? StatusMessage;
    protected string? InfoMessage;
    protected QaPolicyDto Policy { get; set; } = QaPolicyService.DefaultPolicy();

    protected bool CanSave => CanEdit && !IsSubmitting && !IsLoading;

    protected IReadOnlyList<FrozenGate> FrozenGates =>
    [
        new("Incoming inspection",
            "Supplier deliveries can be inspected as a record. This gate cannot authorize goods receipt.",
            Policy.IncomingInspectionEnabled),
        new("Finished-goods inspection",
            "FG staging can be inspected as a record. This gate cannot authorize finished-good receipt.",
            Policy.FinalFgInspectionEnabled),
        new("Non-good Daily Production",
            "Scrap, reject and hold counters can be recorded. This gate cannot authorize Daily Production POST.",
            Policy.NonGoodEnabled),
        new("Logical WIP hold",
            "Hold qty can be recorded on a production lot. Settings cannot turn this posting gate on.",
            Policy.LogicalHoldEnabled),
        new("Physical warehouse hold",
            "Hold and reject qty can be recorded on a warehouse pile. This is not a QCHOLD conversion.",
            Policy.PhysicalHoldEnabled)
    ];

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
        InfoMessage = result.InfoMessage;
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
            Enabled = Policy.IncomingInspectionEnabled,
            IncomingInspectionEnabled = Policy.IncomingInspectionEnabled,
            FinalFgInspectionEnabled = Policy.FinalFgInspectionEnabled,
            LogicalHoldEnabled = Policy.LogicalHoldEnabled,
            PhysicalHoldEnabled = Policy.PhysicalHoldEnabled,
            NonGoodEnabled = Policy.NonGoodEnabled,
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
        InfoMessage = result.InfoMessage;
        StatusMessage = "Quality settings saved. Inspector-approval rules were updated. Posting gates stay frozen.";
    }

    protected void OnClose() => Navigation.NavigateTo("/quality/cases");
    protected void DismissStatus() => StatusMessage = null;
    protected void DismissError() => ErrorMessage = null;

    protected sealed record FrozenGate(string Title, string Help, bool Enabled);
}
