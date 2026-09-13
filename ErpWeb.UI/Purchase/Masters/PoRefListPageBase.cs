using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Purchase.Masters;

/// <summary>
/// Shared list + popup page for Purchase masters keyed by RowVersion.
/// </summary>
public abstract class PoRefListPageBase<TRow, TVm> : PageBase
    where TRow : class
    where TVm : class
{
    [Inject] protected IAccessRightService AccessRights { get; set; } = default!;

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
    protected TVm EditModel { get; set; } = default!;
    protected bool CanEditFromView { get; set; }
    protected bool CanExport { get; set; }

    protected abstract string MenuCode { get; }
    protected abstract string EntityLabel { get; }
    protected virtual bool SupportsActivate => false;
    protected abstract string ExportUrl { get; }

    protected abstract Task<IvMasterOperationResult<IReadOnlyList<TRow>>> LoadRowsAsync();
    protected abstract TVm CreateNewModel();
    protected abstract Task<IvMasterOperationResult<TVm>> LoadModelAsync(string code);
    protected abstract Task<IvMasterOperationResult<TVm>> SaveModelAsync(TVm model, bool isNew);
    protected abstract string GetRowCode(TRow row);
    protected abstract string GetModelCode(TVm model);
    protected abstract IvMasterKeyToken ToKeyToken(TRow row);
    protected abstract Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive);
    protected abstract Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<TRow> rows);
    protected abstract Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items);

    protected List<ButtonInfo> ToolbarButtons
    {
        get
        {
            var buttons = new List<ButtonInfo>
            {
                new() { IConClass = "fas fa-plus", Style = "primary", Text = "NEW" }
            };

            if (SupportsActivate)
            {
                buttons.Add(new()
                {
                    IConClass = "fa-solid fa-check",
                    Style = "success",
                    Text = "ACTIVATE",
                    ToolTip = "Activate selected"
                });
                buttons.Add(new()
                {
                    IConClass = "fa-solid fa-ban",
                    Style = "warning",
                    Text = "DEACTIVATE",
                    ToolTip = "Deactivate selected"
                });
            }

            buttons.Add(new()
            {
                IConClass = "far fa-trash-alt",
                Style = "danger",
                Text = "DELETE",
                ToolTip = "Delete selected"
            });

            if (CanExport)
            {
                buttons.Add(new()
                {
                    IConClass = "fa-solid fa-file-excel",
                    Style = "primary",
                    Text = "EXPORT"
                });
            }

            return buttons;
        }
    }

    protected List<ButtonInfo> RowActionButtons { get; } =
    [
        new() { IConClass = "fa-regular fa-eye", Style = "primary", Text = "VIEW", ToolTip = "View" },
        new() { IConClass = "far fa-edit", Style = "primary", Text = "EDIT", ToolTip = "Edit" }
    ];

    protected string ConfirmDeleteMessage =>
        SelectedRows.Count == 1
            ? $"Delete the selected {EntityLabel}?"
            : $"Delete {SelectedRows.Count} selected {EntityLabel} records?";

    protected override async Task OnPageInitializedAsync()
    {
        EditModel = CreateNewModel();
        CanExport = await AccessRights.CanAsync(MenuCode, PermissionCodes.Export);
        await ReloadListAsync();
    }

    protected async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await LoadRowsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? $"Unable to load {EntityLabel} records.";
                Data = [];
            }
            else
            {
                Data = result.Data ?? [];
            }

            SelectedRows.Clear();
            Grid?.Reload();
        }
        finally
        {
            IsLoading = false;
        }
    }

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
            case "EXPORT":
                await OnExportAsync();
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

    protected async Task<bool> EnsurePermissionAsync(string permissionCode)
    {
        if (await AccessRights.CanAsync(MenuCode, permissionCode))
        {
            return true;
        }

        StatusMessage = "Access Denied!!";
        return false;
    }

    protected async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = CreateNewModel();
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected async Task OnViewClickAsync(TRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access))
        {
            return;
        }

        if (!await LoadEditModelAsync(GetRowCode(row)))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    protected async Task OnEditClickAsync(TRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        if (!await LoadEditModelAsync(GetRowCode(row)))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected async Task SwitchViewToEditAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        EditEnabled = true;
        CanEditFromView = false;
    }

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await SaveModelAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode
                    ? $"{EntityLabel} updated successfully."
                    : $"{EntityLabel} added successfully.";
                await ReloadListAsync();
            }
            else if (result.ErrorCode == IvMasterErrorCode.Concurrency)
            {
                ErrorMessage = result.Message ?? "This record was modified by another user.";
                if (IsEditMode)
                {
                    await LoadEditModelAsync(GetModelCode(EditModel));
                }
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

    protected async Task SetActiveBulkAsync(bool isActive)
    {
        if (!SupportsActivate)
        {
            return;
        }

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

    protected async Task OnExportAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Export))
        {
            return;
        }

        Navigation.NavigateTo(ExportUrl, forceLoad: true);
    }

    private async Task<bool> LoadEditModelAsync(string code)
    {
        var result = await LoadModelAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? $"Unable to load {EntityLabel}.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }

    protected static IvMasterKeyToken Key(string code, byte[]? rowVersion) =>
        new() { Code = code, RowVersion = rowVersion ?? [] };
}
