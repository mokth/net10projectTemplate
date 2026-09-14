using DevExpress.Blazor;
using ErpWeb.Core.Admin;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Admin.Master;

/// <summary>
/// Shared list + popup behaviour for the Department / Project reference masters.
///
/// Deliberately NOT derived from SaRefListPageBase / SaKeyedRefListPageBase /
/// SaCodeRefListPageBase: all three hard-inject ISaSalesRefService. This base injects
/// IMsRefService instead and covers the NEW / ACTIVATE / DEACTIVATE / DELETE / EXPORT toolbar.
/// </summary>
public abstract class MsRefListPageBase<TRow> : PageBase
    where TRow : class
{
    [Inject] protected IAccessRightService AccessRights { get; set; } = default!;
    [Inject] protected IMsRefService MsRef { get; set; } = default!;

    protected DxGrid? Grid;
    protected bool PopupVisible;
    protected bool ConfirmDeleteVisible;
    protected bool IsEditMode;
    protected bool EditEnabled;
    protected bool CanEditFromView;
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
        new() { IConClass = "fa-solid fa-check", Style = "success", Text = "ACTIVATE", ToolTip = "Activate selected" },
        new() { IConClass = "fa-solid fa-ban", Style = "warning", Text = "DEACTIVATE", ToolTip = "Deactivate selected" },
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
            case "ACTIVATE":
                await SetActiveBulkAsync(isActive: true);
                break;
            case "DEACTIVATE":
                await SetActiveBulkAsync(isActive: false);
                break;
            case "DELETE":
                await BeginDeleteAsync();
                break;
            case "REFRESH":
                await ReloadListAsync();
                break;
            case "EXPORT":
                await OnExportAsync();
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

    protected abstract IvMasterKeyToken ToKeyToken(TRow row);
    protected abstract string GetRowCode(TRow row);

    protected abstract Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive);

    protected abstract Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<string> codes);

    protected abstract Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items);

    protected virtual Task OnExportAsync()
    {
        StatusMessage = "Export is not available for this master yet.";
        return Task.CompletedTask;
    }

    protected async Task<bool> EnsurePermissionAsync(string permissionCode)
    {
        if (await AccessRights.CanAsync(MenuCode, permissionCode))
        {
            return true;
        }

        StatusMessage = "Access Denied!!";
        return false;
    }

    protected async Task SetActiveBulkAsync(bool isActive)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        if (SelectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return;
        }

        if (IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var keys = SelectedRows.Select(ToKeyToken).ToList();
            var result = await SetActiveCoreAsync(keys, isActive);
            if (result.Succeeded)
            {
                StatusMessage = isActive
                    ? $"{EntityLabel} record(s) activated."
                    : $"{EntityLabel} record(s) deactivated.";
                await ReloadListAsync();
            }
            else
            {
                ErrorMessage = SaRefListMessages.FormatResultMessage(result);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
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

        var codes = SelectedRows.Select(GetRowCode).ToList();
        var check = await CanDeleteCoreAsync(codes);
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
            var keys = SelectedRows.Select(ToKeyToken).ToList();
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

    protected static IvMasterKeyToken Key(string code, byte[] rowVersion) =>
        new()
        {
            Code = code,
            RowVersion = rowVersion
        };
}
