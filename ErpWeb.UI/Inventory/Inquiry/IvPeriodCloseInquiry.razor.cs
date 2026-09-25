using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Inquiry;

/// <summary>
/// Stored Closing-Balance Inquiry — the read-only view over <c>IvPeriodCloseBal</c> for one closed
/// period. The D13 operator note is part of the markup: the snapshot is ending stock, not a movement
/// ledger.
/// </summary>
public partial class IvPeriodCloseInquiry : PageBase
{
    [Inject] private IIvPeriodCloseService PeriodClose { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected bool IsLoading = true;

    protected bool CanViewPrice;

    protected IReadOnlyList<IvPeriodCloseHeaderRow> Periods { get; set; } = [];
    protected int SelectedPeriodId;
    protected IvPeriodCloseInquiryPage? Page;

    protected string PeriodCaption =>
        Page?.Header is { } h
            ? $"{h.PeriodFrom:dd/MM/yyyy} – {h.PeriodTo:dd/MM/yyyy}"
            : "—";

    protected override async Task OnPageInitializedAsync()
    {
        CanViewPrice = await AccessRights.CanAsync(MenuCodes.InventoryPeriodCloseInq, PermissionCodes.ViewPrice);
        await LoadPeriodsAsync();
    }

    private async Task LoadPeriodsAsync()
    {
        IsLoading = true;
        try
        {
            var result = await PeriodClose.ListInquiryPeriodsAsync();
            if (!result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage;
                return;
            }

            Periods = result.ListPage?.Headers ?? [];
            if (Periods.Count > 0 && SelectedPeriodId == 0)
            {
                SelectedPeriodId = Periods[0].Id;
                await LoadAsync();
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task OnPeriodChangedAsync(int periodId)
    {
        SelectedPeriodId = periodId;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (SelectedPeriodId <= 0)
        {
            Page = null;
            return;
        }

        var result = await PeriodClose.InquiryAsync(SelectedPeriodId);
        if (!result.Succeeded)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        Page = result.InquiryPage;
    }
}
