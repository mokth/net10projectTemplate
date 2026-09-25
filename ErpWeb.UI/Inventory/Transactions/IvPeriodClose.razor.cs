using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Transactions;

/// <summary>
/// Inventory Period Close (month end) — close / reopen a stock period. The CLOSE and REOPEN buttons
/// are gated by the built-in permissions of the same name; the period guard (posting + document-date)
/// is the server-side enforcement this screen drives.
/// </summary>
public partial class IvPeriodClose : PageBase
{
    [Inject] private IIvPeriodCloseService PeriodClose { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected string? StatusMessage;

    protected bool CanClose;
    protected bool CanReopen;

    protected DateTime? PeriodFrom;
    protected DateTime? PeriodTo;
    protected string? Remark;

    protected IvPeriodCloseListPage? Page;
    protected IReadOnlyList<IvPeriodCloseHeaderRow> Headers => Page?.Headers ?? [];

    protected bool CloseConfirmVisible;
    protected bool ReopenPopupVisible;
    protected int ReopenTargetId;
    protected string ReopenReason = string.Empty;
    protected IvPeriodCloseHeaderRow? ReopenTarget;

    protected string NextPeriodCaption =>
        Page?.Next is { } next
            ? $"{next.PeriodFrom:dd/MM/yyyy} – {next.PeriodTo:dd/MM/yyyy}"
            : "—";

    protected override async Task OnPageInitializedAsync()
    {
        CanClose = await AccessRights.CanAsync(MenuCodes.InventoryPeriodClose, PermissionCodes.Close);
        CanReopen = await AccessRights.CanAsync(MenuCodes.InventoryPeriodClose, PermissionCodes.Reopen);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var result = await PeriodClose.ListAsync();
            if (!result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage;
                return;
            }

            Page = result.ListPage;
            PeriodFrom = result.ListPage!.Next.PeriodFrom;
            PeriodTo = result.ListPage.Next.PeriodTo;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void DismissStatus() => StatusMessage = null;
    private void DismissError() => ErrorMessage = null;

    private void OpenCloseConfirm()
    {
        if (PeriodFrom is null || PeriodTo is null)
        {
            ErrorMessage = "Both the period start and end dates are required.";
            return;
        }

        ErrorMessage = null;
        CloseConfirmVisible = true;
    }

    private void CancelClose() => CloseConfirmVisible = false;

    private async Task ConfirmCloseAsync()
    {
        CloseConfirmVisible = false;
        IsSubmitting = true;
        try
        {
            var result = await PeriodClose.CloseAsync(new IvPeriodCloseRequest
            {
                PeriodFrom = PeriodFrom!.Value,
                PeriodTo = PeriodTo!.Value,
                Remark = Remark
            });

            if (!result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage;
                return;
            }

            StatusMessage = "Period closed.";
            await LoadAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private void OpenReopen(IvPeriodCloseHeaderRow row)
    {
        ReopenTarget = row;
        ReopenTargetId = row.Id;
        ReopenReason = string.Empty;
        ReopenPopupVisible = true;
    }

    private void CancelReopen() => ReopenPopupVisible = false;

    private async Task ConfirmReopenAsync()
    {
        ReopenPopupVisible = false;
        IsSubmitting = true;
        try
        {
            var result = await PeriodClose.ReopenAsync(new IvPeriodCloseRequest
            {
                Id = ReopenTargetId,
                RowVersion = ReopenTarget!.RowVersion,
                ReopenReason = ReopenReason
            });

            if (!result.Succeeded)
            {
                ErrorMessage = result.ErrorMessage;
                return;
            }

            StatusMessage = "Period reopened — its stored balances were withdrawn.";
            await LoadAsync();
        }
        finally
        {
            IsSubmitting = false;
        }
    }
}
