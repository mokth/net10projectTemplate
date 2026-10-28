using DevExpress.Blazor;
using ErpWeb.Core.Admin;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Admin;

/// <summary>
/// Generic configurable lookup maintenance (<c>/admin/lookups</c>).
///
/// <para>
/// One screen for every approved lookup family. The family is chosen from
/// <see cref="AdLookupTypeRegistry"/> — never typed — so the generic table cannot become an arbitrary
/// classification store. Activation/deactivation replaces hard delete: a value may be referenced by
/// many modules and there is no reliable dependency scan.
/// </para>
/// </summary>
public partial class AdLookupList : PageBase
{
    private const string MenuCode = MenuCodes.AdminLookups;

    [Inject] private IAdLookupService Lookups { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    private DxGrid? Grid;
    private bool PopupVisible;
    private bool IsEditMode;
    private bool EditEnabled;
    private bool CanEditFromView;
    private bool IsLoading = true;
    private bool IsSubmitting;
    private string? StatusMessage;
    private IEnumerable<AdLookupRow>? Data;
    private readonly List<AdLookupRow> SelectedRows = [];

    protected string? SelectedLookupType { get; set; } =
        AdLookupTypeRegistry.Maintainable.FirstOrDefault()?.Code;

    protected AdLookupEditVm EditModel { get; set; } = new();

    private AdLookupTypeDefinition? EditDefinition =>
        AdLookupTypeRegistry.Find(EditModel.LookupType) ?? AdLookupTypeRegistry.Find(SelectedLookupType);

    protected IReadOnlyList<AdLookupTypeDefinition> LookupTypes => AdLookupTypeRegistry.Maintainable;

    protected List<ButtonInfo> ToolbarButtons { get; } =
    [
        new() { IConClass = "fas fa-plus", Style = "primary", Text = "NEW" },
        new() { IConClass = "fa-solid fa-check", Style = "success", Text = "ACTIVATE", ToolTip = "Activate selected" },
        new() { IConClass = "fa-solid fa-ban", Style = "warning", Text = "DEACTIVATE", ToolTip = "Deactivate selected" },
        new() { IConClass = "fa-solid fa-rotate", Style = "primary", Text = "REFRESH", ToolTip = "Reload" }
    ];

    protected List<ButtonInfo> RowActionButtons { get; } =
    [
        new() { IConClass = "fa-regular fa-eye", Style = "primary", Text = "VIEW", ToolTip = "View" },
        new() { IConClass = "far fa-edit", Style = "primary", Text = "EDIT", ToolTip = "Edit" }
    ];

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(AdLookupRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "180px"
        },
        new()
        {
            Caption = "Description",
            FieldName = nameof(AdLookupRow.Description),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Sort",
            FieldName = nameof(AdLookupRow.SortOrder),
            DataType = "number",
            VisibleIndex = 3,
            Width = "80px"
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(AdLookupRow.IsActive),
            DataType = "bool",
            VisibleIndex = 4,
            Width = "80px"
        },
        ..AuditColumns.For(startVisibleIndex: 5)
    ];

    protected override async Task OnPageInitializedAsync() => await ReloadListAsync();

    protected async Task OnLookupTypeChanged(string? lookupType)
    {
        SelectedLookupType = lookupType;
        await ReloadListAsync();
    }

    protected void DismissStatus() => StatusMessage = null;

    protected void DismissError() => ErrorMessage = null;

    protected void OnGridInstance(DxGrid grid) => Grid = grid;

    protected void ClosePopup()
    {
        PopupVisible = false;
        ErrorMessage = null;
        IsSubmitting = false;
    }

    protected void OnSelectionsEvent(List<AdLookupRow> list)
    {
        SelectedRows.Clear();
        if (Data is null)
        {
            return;
        }

        SelectedRows.AddRange(list.Where(item => Data.Contains(item)));
    }

    protected async Task OnToolbarButtonAsync(SelectedButtonInfo<AdLookupRow> info)
    {
        switch ((info.SelectedButton.Text ?? string.Empty).ToUpperInvariant())
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
            case "REFRESH":
                await ReloadListAsync();
                break;
        }
    }

    protected async Task OnRowActionAsync(SelectedButtonInfo<AdLookupRow> info)
    {
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return;
        }

        switch ((info.SelectedButton.Text ?? string.Empty).ToUpperInvariant())
        {
            case "VIEW":
                await OnViewClickAsync(info.SelectedRow);
                break;
            case "EDIT":
                await OnEditClickAsync(info.SelectedRow);
                break;
        }
    }

    protected async Task ReloadListAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedLookupType))
        {
            Data = [];
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            Data = await Lookups.ListForMaintenanceAsync(SelectedLookupType);
            SelectedRows.Clear();
            Grid?.Reload();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        var definition = AdLookupTypeRegistry.Find(SelectedLookupType);
        if (definition is null)
        {
            StatusMessage = "Select a lookup type first.";
            return;
        }

        EditModel = new AdLookupEditVm
        {
            LookupType = definition.Code,
            IsActive = true,
            SortOrder = NextSortOrder(),
            MaxCodeLength = definition.MaxCodeLength
        };
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    private async Task OnViewClickAsync(AdLookupRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access))
        {
            return;
        }

        if (!await LoadEditModelAsync(row))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    private async Task OnEditClickAsync(AdLookupRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        if (!await LoadEditModelAsync(row))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    private async Task SwitchViewToEdit()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        EditEnabled = true;
        CanEditFromView = false;
    }

    private async Task<bool> LoadEditModelAsync(AdLookupRow row)
    {
        var result = await Lookups.GetAsync(row.Uid);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = FormatResult(result);
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }

    private async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await Lookups.SaveAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Lookup value updated successfully." : "Lookup value added successfully.";
                await ReloadListAsync();
            }
            else
            {
                ErrorMessage = FormatResult(result);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task SetActiveBulkAsync(bool isActive)
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
            var tokens = SelectedRows
                .Select(r => new AdLookupKeyToken { Uid = r.Uid, RowVersion = r.RowVersion })
                .ToList();
            var result = await Lookups.SetActiveAsync(tokens, isActive);
            if (result.Succeeded)
            {
                StatusMessage = isActive
                    ? "Lookup value(s) activated."
                    : "Lookup value(s) deactivated.";
                await ReloadListAsync();
            }
            else
            {
                ErrorMessage = FormatResult(result);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task<bool> EnsurePermissionAsync(string permissionCode)
    {
        if (await AccessRights.CanAsync(MenuCode, permissionCode))
        {
            return true;
        }

        StatusMessage = "Access Denied!!";
        return false;
    }

    private int NextSortOrder()
    {
        var max = Data?.Select(x => x.SortOrder).DefaultIfEmpty(0).Max() ?? 0;
        return max + AdLookupDefaults.SeedSortStep;
    }

    private static string FormatResult<T>(IvMasterOperationResult<T> result) =>
        result.ValidationErrors.Count > 0
            ? string.Join(" ", result.ValidationErrors.Select(kv => $"{kv.Key}: {kv.Value}"))
            : result.Message ?? "Operation failed.";
}
