using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

/// <summary>
/// Shared shell for Planning reference master list + popup pages.
/// Mirrors Inventory <c>IvRefListPageBase</c> (CommonDataGridEx toolbar, View/Edit popup, confirm delete).
/// </summary>
public abstract class PrRefListPageBase<TRow> : PageBase
    where TRow : class
{
    [Inject] protected IAccessRightService AccessRights { get; set; } = default!;

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
    protected abstract string HeroIconClass { get; }
    protected virtual string HeroTitle => EntityLabel + "s";
    protected virtual bool SupportsActivate => false;

    protected List<ButtonInfo> ToolbarButtons { get; private set; } = [];
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
        ToolbarButtons = BuildToolbarButtons();
        await ReloadListAsync();
    }

    protected virtual List<ButtonInfo> BuildToolbarButtons()
    {
        var buttons = new List<ButtonInfo>
        {
            new() { IConClass = "fas fa-plus", Style = "primary", Text = "NEW" }
        };
        if (SupportsActivate)
        {
            buttons.Add(new() { IConClass = "fa-solid fa-check", Style = "success", Text = "ACTIVATE", ToolTip = "Activate selected" });
            buttons.Add(new() { IConClass = "fa-solid fa-ban", Style = "warning", Text = "DEACTIVATE", ToolTip = "Deactivate selected" });
        }
        buttons.Add(new() { IConClass = "far fa-trash-alt", Style = "danger", Text = "DELETE", ToolTip = "Delete selected" });
        buttons.Add(new() { IConClass = "fa-solid fa-file-excel", Style = "primary", Text = "EXPORT" });
        return buttons;
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
        if (Data is null) return;
        SelectedRows.AddRange(list.Where(item => Data.Contains(item)));
    }

    protected virtual async Task OnToolbarButtonAsync(SelectedButtonInfo<TRow> info)
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
    protected abstract Task<PlanningServiceResult> DeleteSelectedAsync();

    protected virtual Task<PlanningServiceResult> SetActiveSelectedAsync(bool isActive) =>
        Task.FromResult(PlanningServiceResult.Fail(PlanningErrorCode.ValidationFailed, "Activate is not supported."));

    protected async Task SwitchViewToEditAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
            return;
        EditEnabled = true;
        CanEditFromView = false;
    }

    protected async Task<bool> EnsurePermissionAsync(string permissionCode)
    {
        if (await AccessRights.CanAsync(MenuCode, permissionCode))
            return true;
        StatusMessage = "Access Denied!!";
        return false;
    }

    protected async Task SetActiveBulkAsync(bool isActive)
    {
        if (!SupportsActivate) return;
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        if (SelectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return;
        }
        if (IsSubmitting) return;

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await SetActiveSelectedAsync(isActive);
            if (result.Succeeded)
            {
                StatusMessage = isActive
                    ? $"{EntityLabel} record(s) activated."
                    : $"{EntityLabel} record(s) deactivated.";
                await ReloadListAsync();
            }
            else
            {
                ErrorMessage = FormatPlanningResult(result);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected async Task BeginDeleteAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Delete)) return;
        if (SelectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return;
        }
        ConfirmDeleteVisible = true;
    }

    protected async Task ConfirmDeleteAsync()
    {
        if (IsSubmitting || SelectedRows.Count == 0) return;
        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await DeleteSelectedAsync();
            if (result.Succeeded)
            {
                ConfirmDeleteVisible = false;
                StatusMessage = $"{EntityLabel} record(s) deleted successfully.";
                await ReloadListAsync();
            }
            else
            {
                ErrorMessage = FormatPlanningResult(result);
                ConfirmDeleteVisible = false;
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected static string FormatPlanningResult(PlanningServiceResult result)
    {
        if (result.Issues.Count > 0)
            return string.Join(" ", result.Issues.Select(i => i.Message));
        if (result.Dependencies.Count > 0)
        {
            var deps = string.Join("; ", result.Dependencies.Select(d => $"{d.SourceTable}: {d.BusinessKey}"));
            return $"{result.Message} {deps}".Trim();
        }
        return result.Message ?? "Operation failed.";
    }
}
