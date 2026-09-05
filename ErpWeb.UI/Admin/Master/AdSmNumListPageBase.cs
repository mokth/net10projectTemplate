using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Admin.Master;

/// <summary>
/// Numbering admin list pages (continuous / period). Injects IAdSmNumAdminService.
/// </summary>
public abstract class AdSmNumListPageBase<TRow, TKey> : PageBase
    where TRow : class
    where TKey : class
{
    [Inject] protected IAccessRightService AccessRights { get; set; } = default!;
    [Inject] protected IAdSmNumAdminService NumService { get; set; } = default!;

    protected DxGrid? Grid;
    protected bool PopupVisible;
    protected bool ConfirmDeleteVisible;
    protected bool IsEditMode;
    protected bool EditEnabled;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected string? StatusMessage;
    protected IEnumerable<TRow>? Data;
    protected readonly List<TRow> SelectedRows = [];

    protected abstract string MenuCode { get; }
    protected abstract string EntityLabel { get; }

    protected List<ButtonInfo> ToolbarButtons { get; } =
    [
        new() { IConClass = "fas fa-plus", Style = "primary", Text = "NEW" },
        new() { IConClass = "far fa-trash-alt", Style = "danger", Text = "DELETE", ToolTip = "Delete selected" },
        new() { IConClass = "fa-solid fa-file-excel", Style = "primary", Text = "EXPORT" }
    ];

    protected List<ButtonInfo> RowActionButtons { get; } =
    [
        new() { IConClass = "fa-regular fa-eye", Style = "primary", Text = "VIEW", ToolTip = "View" },
        new() { IConClass = "far fa-edit", Style = "primary", Text = "EDIT", ToolTip = "Edit" }
    ];

    protected string ConfirmDeleteMessage =>
        SelectedRows.Count == 1
            ? $"Delete the selected {EntityLabel}?"
            : $"Delete {SelectedRows.Count} selected {EntityLabel} records?";

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    protected void OnGridInstance(DxGrid gridInstance) => Grid = gridInstance;

    protected void ClosePopup()
    {
        PopupVisible = false;
        ErrorMessage = null;
        IsSubmitting = false;
    }

    protected void CloseConfirmDelete()
    {
        ConfirmDeleteVisible = false;
        IsSubmitting = false;
    }

    protected void OnSelectionsEvent(List<TRow> list)
    {
        SelectedRows.Clear();
        if (Data is null)
        {
            return;
        }

        SelectedRows.AddRange(list.Where(item => Data.Contains(item)));
    }

    protected async Task OnToolbarButtonAsync(SelectedButtonInfo<TRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "NEW":
                await OnNewClickAsync();
                break;
            case "DELETE":
                await BeginDeleteAsync();
                break;
            case "REFRESH":
                await ReloadListAsync();
                break;
        }
    }

    protected async Task OnRowActionAsync(SelectedButtonInfo<TRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "VIEW":
                await OnViewClickAsync(info.SelectedRow);
                break;
            case "EDIT":
                await OnEditClickAsync(info.SelectedRow);
                break;
        }
    }

    protected abstract Task OnNewClickAsync();
    protected abstract Task OnViewClickAsync(TRow row);
    protected abstract Task OnEditClickAsync(TRow row);
    protected abstract Task ReloadListAsync();
    protected abstract TKey ToKey(TRow row);
    protected abstract Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<TRow> rows);
    protected abstract Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<TKey> keys);

    protected async Task<bool> EnsurePermissionAsync(string permissionCode)
    {
        if (await AccessRights.CanAsync(MenuCode, permissionCode))
        {
            return true;
        }

        StatusMessage = "Access Denied!!";
        return false;
    }

    protected async Task BeginDeleteAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Delete))
        {
            return;
        }

        if (SelectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return;
        }

        var check = await CanDeleteCoreAsync(SelectedRows);
        if (!check.CanDelete)
        {
            ErrorMessage = SaRefListMessages.FormatDeleteBlocked(check);
            StatusMessage = null;
            return;
        }

        ConfirmDeleteVisible = true;
    }

    protected async Task ConfirmDeleteAsync()
    {
        if (IsSubmitting || SelectedRows.Count == 0)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var keys = SelectedRows.Select(ToKey).ToList();
            var result = await DeleteCoreAsync(keys);
            if (result.Succeeded)
            {
                ConfirmDeleteVisible = false;
                StatusMessage = $"{EntityLabel} record(s) deleted successfully.";
                await ReloadListAsync();
            }
            else
            {
                ErrorMessage = SaRefListMessages.FormatResultMessage(result);
                if (result.DeleteCheck is { CanDelete: false })
                {
                    ConfirmDeleteVisible = false;
                }
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }
}
