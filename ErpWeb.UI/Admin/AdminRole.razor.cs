using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Admin;

public partial class AdminRole : PageBase
{
    [Inject] private IRoleAdminService RoleAdmin { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    private DxGrid? _grid;

    protected bool PopupVisible;
    protected Role selectedRow { get; set; } = new();
    protected bool IsEditMode;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool editEnable;
    protected string? StatusMessage;

    protected IEnumerable<Role>? Data;
    private readonly List<Role> _selectedRows = [];

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Role Code",
            FieldName = nameof(Role.RoleCode),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "120px"
        },
        new()
        {
            Caption = "Role Name",
            FieldName = nameof(Role.RoleName),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(Role.IsActive),
            DataType = "bool",
            VisibleIndex = 3,
            Width = "80px"
        },
        new()
        {
            Caption = "Created By",
            FieldName = nameof(Role.CreatedBy),
            DataType = "string",
            VisibleIndex = 4,
            Width = "100px"
        },
        new()
        {
            Caption = "Created On",
            FieldName = nameof(Role.CreatedDate),
            DataType = "datetime",
            VisibleIndex = 5,
            Visible = false,
            Width = "120px"
        },
        new()
        {
            Caption = "Update By",
            FieldName = nameof(Role.ModifiedBy),
            DataType = "string",
            VisibleIndex = 6,
            Width = "100px"
        },
        new()
        {
            Caption = "Update On",
            FieldName = nameof(Role.ModifiedDate),
            DataType = "datetime",
            VisibleIndex = 7,
            Visible = false,
            Width = "120px"
        }
    ];

    public List<ButtonInfo> Buttons { get; set; } =
    [
        new()
        {
            IConClass = "fas fa-plus",
            Style = "primary",
            Text = "New"
        },
        new()
        {
            IConClass = "fa-solid fa-file-excel",
            Style = "primary",
            Text = "Export"
        },
        new()
        {
            IConClass = "far fa-trash-alt",
            Style = "danger",
            Text = "Delete",
            ToolTip = "Delete Role"
        }
    ];

    public List<ButtonInfo> ActionButtons { get; set; } =
    [
        new()
        {
            IConClass = "fa-regular fa-eye",
            Style = "primary",
            Text = "View",
            ToolTip = "View Role"
        },
        new()
        {
            IConClass = "far fa-edit",
            Style = "primary",
            Text = "Edit",
            ToolTip = "Edit Role"
        }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        await LoadPageDataAsync();
    }

    private async Task LoadPageDataAsync()
    {
        IsLoading = true;
        StatusMessage = null;
        ErrorMessage = string.Empty;

        var result = await RoleAdmin.GetRolesAsync();
        if (!result.Succeeded)
        {
            StatusMessage = result.ErrorMessage ?? "Unable to load roles.";
            Data = [];
        }
        else
        {
            Data = result.Roles;
            selectedRow = Data.FirstOrDefault() ?? new Role();
        }

        IsLoading = false;
        _selectedRows.Clear();
        _grid?.Reload();
    }

    public void OnCancel()
    {
        PopupVisible = false;
        ErrorMessage = string.Empty;
        IsSubmitting = false;
    }

    public async Task OnActionClick(SelectedButtonInfo<Role> info)
    {
        var mode = info.SelectedButton.Text.ToLowerInvariant();
        if (info.SelectedRow is null)
        {
            StatusMessage = "No record selected.";
            return;
        }

        switch (mode)
        {
            case "edit":
                await OnEditClick(info.SelectedRow);
                break;
            case "view":
                await OnViewClick(info.SelectedRow);
                break;
        }
    }

    public async Task OnButtonClick(SelectedButtonInfo<Role> info)
    {
        var mode = info.SelectedButton.Text.ToLowerInvariant();
        switch (mode)
        {
            case "new":
                await OnAddNewClick();
                break;
            case "delete":
                await OnDeleteClick();
                break;
            case "refresh":
                await LoadPageDataAsync();
                break;
        }
    }

    public async Task OnAddNewClick()
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminRoles, PermissionCodes.Add))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        selectedRow = new Role
        {
            IsActive = true,
            CompanyCode = CurrentUser.CompanyCode ?? string.Empty
        };
        ErrorMessage = string.Empty;
        IsEditMode = false;
        PopupVisible = true;
        editEnable = true;
    }

    public async Task OnEditClick(Role data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminRoles, PermissionCodes.Edit))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        selectedRow = CloneForEdit(data);
        ErrorMessage = string.Empty;
        IsEditMode = true;
        PopupVisible = true;
        editEnable = true;
    }

    protected async Task OnViewClick(Role data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminRoles, PermissionCodes.Access))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        selectedRow = CloneForEdit(data);
        ErrorMessage = string.Empty;
        IsEditMode = true;
        editEnable = false;
        PopupVisible = true;
    }

    public void OnGridInstance(DxGrid gridInstance)
    {
        _grid = gridInstance;
    }

    public async Task OnDeleteClick()
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminRoles, PermissionCodes.Delete))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (_selectedRows.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return;
        }

        var confirmed = await JsRuntime.InvokeAsync<bool>(
            "confirm",
            $"Delete {_selectedRows.Count} selected role(s)?");
        if (!confirmed)
        {
            return;
        }

        var result = await RoleAdmin.DeleteRolesAsync(_selectedRows.Select(x => x.RoleId).ToList());
        if (result.Succeeded)
        {
            StatusMessage = "Role(s) deleted successfully.";
            await LoadPageDataAsync();
        }
        else
        {
            StatusMessage = result.ErrorMessage ?? "Unable to delete role(s).";
        }
    }

    protected async Task HandleValidSubmit()
    {
        if (IsSubmitting || !editEnable)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = string.Empty;

        RoleAdminOperationResult result;
        if (IsEditMode)
        {
            result = await RoleAdmin.UpdateRoleAsync(selectedRow);
        }
        else
        {
            result = await RoleAdmin.AddRoleAsync(selectedRow);
        }

        if (result.Succeeded)
        {
            PopupVisible = false;
            StatusMessage = IsEditMode
                ? "Role updated successfully."
                : "Role added successfully.";
            await LoadPageDataAsync();
        }
        else
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to save role.";
        }

        IsSubmitting = false;
    }

    protected void OnSelectionsEvent(List<Role> list)
    {
        if (Data is null)
        {
            _selectedRows.Clear();
            return;
        }

        _selectedRows.Clear();
        _selectedRows.AddRange(list.Where(item => Data.Any(d => d.RoleId == item.RoleId)));
    }

    private static Role CloneForEdit(Role data) =>
        new()
        {
            RoleId = data.RoleId,
            CompanyCode = data.CompanyCode,
            RoleCode = data.RoleCode,
            RoleName = data.RoleName,
            IsActive = data.IsActive,
            CreatedDate = data.CreatedDate,
            CreatedBy = data.CreatedBy,
            ModifiedDate = data.ModifiedDate,
            ModifiedBy = data.ModifiedBy
        };
}
