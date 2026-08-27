using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Masters;

/// <summary>
/// Shared helpers for inventory reference master list + popup pages.
/// Warehouse/location-specific rules stay in the concrete pages.
/// </summary>
public abstract class IvRefListPageBase<TRow> : PageBase
    where TRow : class
{
    [Inject] protected IAccessRightService AccessRights { get; set; } = default!;
    [Inject] protected IIvInventoryRefService RefService { get; set; } = default!;

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

    protected abstract Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive);

    protected abstract Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<TRow> rows);

    protected abstract Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items);

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
                ErrorMessage = FormatResultMessage(result);
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

        var check = await CanDeleteCoreAsync(SelectedRows);
        if (!check.CanDelete)
        {
            ErrorMessage = FormatDeleteBlocked(check);
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
                ErrorMessage = FormatResultMessage(result);
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

    protected static string FormatResultMessage<T>(IvMasterOperationResult<T> result)
    {
        if (result.ValidationErrors.Count > 0)
        {
            return string.Join(" ", result.ValidationErrors.Select(kv => $"{kv.Key}: {kv.Value}"));
        }

        if (result.DeleteCheck is { CanDelete: false } check)
        {
            return FormatDeleteBlocked(check);
        }

        return result.Message ?? "Operation failed.";
    }

    protected static string FormatDeleteBlocked(DeleteCheckResult check)
    {
        if (check.References.Count == 0)
        {
            return check.Message ?? "Cannot delete because the record is in use.";
        }

        var refs = string.Join("; ",
            check.References.Select(r =>
                string.IsNullOrWhiteSpace(r.Detail)
                    ? $"{r.ReferenceType} ({r.Count})"
                    : $"{r.ReferenceType} ({r.Count}): {r.Detail}"));
        var prefix = check.Message ?? "Cannot delete because the record is in use.";
        return $"{prefix} {refs}";
    }

    protected static IvMasterKeyToken Key(string code, byte[] rowVersion, string? parentCode = null) =>
        new()
        {
            Code = code,
            RowVersion = rowVersion,
            ParentCode = parentCode
        };
}
