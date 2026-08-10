using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Repositories;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Admin;

public partial class AdminUser : PageBase
{
    [Inject] private IUserAdminService UserAdmin { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    private DxGrid? _grid;

    protected bool PopupVisible;
    protected bool PopupPassword;
    protected UserLogin selectedRow { get; set; } = new();
    protected bool IsEditMode;
    protected bool IsChgPass;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected string confirmPassword = string.Empty;
    protected bool editEnable;
    protected string? StatusMessage;

    protected IEnumerable<UserLogin>? Data;
    protected IEnumerable<RoleOptionRow> levelgroup { get; set; } = [];

    private readonly List<UserLogin> _selectedUsers = [];

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "User ID",
            FieldName = nameof(UserLogin.id),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "110px"
        },
        new()
        {
            Caption = "User Name",
            FieldName = nameof(UserLogin.name),
            DataType = "string",
            VisibleIndex = 2,
            Width = "150px"
        },
        new()
        {
            Caption = "Group Level",
            FieldName = nameof(UserLogin.userlevel),
            DataType = "string",
            VisibleIndex = 3,
            Width = "120px"
        },
        new()
        {
            Caption = "User Email",
            FieldName = nameof(UserLogin.email),
            DataType = "string",
            VisibleIndex = 4
        },
        new()
        {
            Caption = "User Mobile",
            FieldName = nameof(UserLogin.mobileno),
            DataType = "string",
            VisibleIndex = 5,
            Width = "120px"
        },
        new()
        {
            Caption = "User Active",
            FieldName = nameof(UserLogin.active),
            DataType = "bool",
            VisibleIndex = 6,
            Width = "80px"
        },
        new()
        {
            Caption = "Force Change Pwd",
            FieldName = nameof(UserLogin.changepass),
            DataType = "bool",
            VisibleIndex = 7,
            Width = "110px"
        },
        new()
        {
            Caption = "Created By",
            FieldName = nameof(UserLogin.UserID),
            DataType = "string",
            VisibleIndex = 8,
            Width = "100px"
        },
        new()
        {
            Caption = "Created On",
            FieldName = nameof(UserLogin.Created),
            DataType = "datetime",
            VisibleIndex = 9,
            Visible = false,
            Width = "100px"
        },
        new()
        {
            Caption = "Update By",
            FieldName = nameof(UserLogin.UpdatedUID),
            DataType = "string",
            VisibleIndex = 10,
            Width = "100px"
        },
        new()
        {
            Caption = "Update On",
            FieldName = nameof(UserLogin.Updated),
            DataType = "datetime",
            VisibleIndex = 11,
            Visible = false,
            Width = "100px"
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
            ToolTip = "Delete User"
        }
    ];

    public List<ButtonInfo> ActionButtons { get; set; } =
    [
        new()
        {
            IConClass = "fa-regular fa-eye",
            Style = "primary",
            Text = "View",
            ToolTip = "View User"
        },
        new()
        {
            IConClass = "far fa-edit",
            Style = "primary",
            Text = "Edit",
            ToolTip = "Edit User"
        },
        new()
        {
            IConClass = "fas fa-lock",
            Style = "primary",
            Text = "ChangePass",
            ToolTip = "Reset user's password"
        },
        new()
        {
            IConClass = "fas fa-key",
            Style = "primary",
            Text = "ForceChange",
            ToolTip = "Toggle force password change at next login"
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

        var rolesResult = await UserAdmin.GetRolesAsync();
        if (rolesResult.Succeeded)
        {
            levelgroup = rolesResult.Roles;
        }

        var usersResult = await UserAdmin.GetUsersAsync();
        if (!usersResult.Succeeded)
        {
            StatusMessage = usersResult.ErrorMessage ?? "Unable to load users.";
            Data = [];
        }
        else
        {
            Data = usersResult.Users;
            selectedRow = Data.FirstOrDefault() ?? new UserLogin();
        }

        IsLoading = false;
        _selectedUsers.Clear();
        _grid?.Reload();
    }

    public void OnCancel()
    {
        PopupVisible = false;
        PopupPassword = false;
        ErrorMessage = string.Empty;
        confirmPassword = string.Empty;
        IsSubmitting = false;
    }

    public async Task OnActionClick(SelectedButtonInfo<UserLogin> info)
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
            case "changepass":
                await OnChgPassClick(info.SelectedRow);
                break;
            case "forcechange":
                await OnForceChangeClick(info.SelectedRow);
                break;
            case "view":
                await OnViewClick(info.SelectedRow);
                break;
        }
    }

    public async Task OnButtonClick(SelectedButtonInfo<UserLogin> info)
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
        if (!await AccessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Add))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        selectedRow = new UserLogin
        {
            active = true,
            CompanyCode = CurrentUser.CompanyCode ?? string.Empty,
            BranchCode = CurrentUser.BranchCode ?? string.Empty,
            LocationCode = CurrentUser.LocationCode ?? string.Empty
        };
        confirmPassword = string.Empty;
        ErrorMessage = string.Empty;
        IsEditMode = false;
        IsChgPass = false;
        PopupVisible = true;
        editEnable = true;
    }

    public async Task OnEditClick(UserLogin data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Edit))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        selectedRow = CloneForEdit(data);
        ErrorMessage = string.Empty;
        IsEditMode = true;
        IsChgPass = false;
        PopupVisible = true;
        editEnable = true;
    }

    public async Task OnChgPassClick(UserLogin data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Edit))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        selectedRow = CloneForEdit(data);
        selectedRow.password = string.Empty;
        confirmPassword = string.Empty;
        ErrorMessage = string.Empty;
        PopupPassword = true;
    }

    public async Task OnForceChangeClick(UserLogin data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Edit))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        var force = !data.changepass;
        var result = await UserAdmin.ForceChangeAsync(data.uid, force);
        if (result.Succeeded)
        {
            StatusMessage = force
                ? $"Password change required at next login for {data.id}."
                : $"Password-change requirement cleared for {data.id}.";
            await LoadPageDataAsync();
        }
        else
        {
            StatusMessage = result.ErrorMessage ?? "Unable to update password-change requirement.";
        }
    }

    protected async Task OnViewClick(UserLogin data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Access))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        selectedRow = CloneForEdit(data);
        ErrorMessage = string.Empty;
        IsEditMode = true;
        editEnable = false;
        IsChgPass = true;
        PopupVisible = true;
    }

    public void OnGridInstance(DxGrid gridInstance)
    {
        _grid = gridInstance;
    }

    public async Task OnDeleteClick()
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Delete))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        if (_selectedUsers.Count == 0)
        {
            StatusMessage = "No Record Selected!";
            return;
        }

        var confirmed = await JsRuntime.InvokeAsync<bool>(
            "confirm",
            $"Delete {_selectedUsers.Count} selected user(s)?");
        if (!confirmed)
        {
            return;
        }

        var result = await UserAdmin.DeleteUsersAsync(_selectedUsers.Select(x => x.uid).ToList());
        if (result.Succeeded)
        {
            StatusMessage = "User(s) deleted successfully.";
            await LoadPageDataAsync();
        }
        else
        {
            StatusMessage = result.ErrorMessage ?? "Unable to delete user(s).";
        }
    }

    protected async Task HandlePasswordSubmit()
    {
        if (IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = string.Empty;

        var result = await UserAdmin.ChangePasswordAsync(
            selectedRow.uid,
            selectedRow.password,
            confirmPassword);

        if (result.Succeeded)
        {
            PopupPassword = false;
            confirmPassword = string.Empty;
            StatusMessage = $"Password reset for {selectedRow.id}. The user must change the password at next login.";
            await LoadPageDataAsync();
        }
        else
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to change password.";
        }

        IsSubmitting = false;
    }

    protected async Task HandleValidSubmit()
    {
        if (IsSubmitting || !editEnable)
        {
            return;
        }

        IsSubmitting = true;
        ErrorMessage = string.Empty;

        UserAdminOperationResult result;
        if (IsEditMode)
        {
            result = await UserAdmin.UpdateUserAsync(selectedRow);
        }
        else
        {
            result = await UserAdmin.AddUserAsync(selectedRow, selectedRow.password);
        }

        if (result.Succeeded)
        {
            PopupVisible = false;
            StatusMessage = IsEditMode
                ? "User updated successfully."
                : "User added successfully.";
            await LoadPageDataAsync();
        }
        else
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to save user.";
        }

        IsSubmitting = false;
    }

    protected void OnSelectionsEvent(List<UserLogin> list)
    {
        if (Data is null)
        {
            _selectedUsers.Clear();
            return;
        }

        _selectedUsers.Clear();
        _selectedUsers.AddRange(list.Where(item => Data.Any(d => d.uid == item.uid)));
    }

    private static UserLogin CloneForEdit(UserLogin data) =>
        new()
        {
            uid = data.uid,
            id = data.id,
            name = data.name,
            password = string.Empty,
            email = data.email,
            mobileno = data.mobileno,
            active = data.active,
            userlevel = data.userlevel,
            CompanyCode = data.CompanyCode,
            BranchCode = data.BranchCode,
            LocationCode = data.LocationCode,
            ImagePath = data.ImagePath,
            UserID = data.UserID,
            Created = data.Created,
            Updated = data.Updated,
            UpdatedUID = data.UpdatedUID,
            changepass = data.changepass
        };
}
