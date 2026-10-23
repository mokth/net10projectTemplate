using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
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
    [Inject] private IPasswordRecoveryService Recovery { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    private DxGrid? _grid;

    protected bool PopupVisible;
    protected bool PopupPassword;
    protected bool IsEditMode;
    protected bool editEnable;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected string? StatusMessage;

    protected IEnumerable<UserAdminRow>? Data;
    protected IEnumerable<RoleOptionRow> levelgroup { get; set; } = [];

    protected IReadOnlyList<PasswordResetRequestRow>? PendingRequests;

    private readonly List<UserAdminRow> _selectedUsers = [];

    protected CreateUserRequest CreateModel { get; set; } = new();
    protected EditUserRequest EditModel { get; set; } = new();
    protected ResetUserPasswordRequest ResetModel { get; set; } = new();

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Login ID",
            FieldName = nameof(UserAdminRow.LoginId),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "110px"
        },
        new()
        {
            Caption = "User Name",
            FieldName = nameof(UserAdminRow.Name),
            DataType = "string",
            VisibleIndex = 2,
            Width = "150px"
        },
        new()
        {
            Caption = "Group Level",
            FieldName = nameof(UserAdminRow.RoleCode),
            DataType = "string",
            VisibleIndex = 3,
            Width = "120px"
        },
        new()
        {
            Caption = "User Email",
            FieldName = nameof(UserAdminRow.Email),
            DataType = "string",
            VisibleIndex = 4
        },
        new()
        {
            Caption = "User Mobile",
            FieldName = nameof(UserAdminRow.MobileNo),
            DataType = "string",
            VisibleIndex = 5,
            Width = "120px"
        },
        new()
        {
            Caption = "User Active",
            FieldName = nameof(UserAdminRow.Active),
            DataType = "bool",
            VisibleIndex = 6,
            Width = "80px"
        },
        new()
        {
            Caption = "Force Change Pwd",
            FieldName = nameof(UserAdminRow.ChangePass),
            DataType = "bool",
            VisibleIndex = 7,
            Width = "110px"
        },
        new()
        {
            Caption = "Can View Price",
            FieldName = nameof(UserAdminRow.CanViewPrice),
            DataType = "bool",
            VisibleIndex = 8,
            Width = "110px"
        },
        new()
        {
            Caption = AuditColumns.CreatedCaption,
            FieldName = nameof(UserAdminRow.Created),
            DataType = "datetime",
            VisibleIndex = 9,
            Width = "140px"
        },
        new()
        {
            Caption = AuditColumns.CreatedByCaption,
            FieldName = nameof(UserAdminRow.CreatedBy),
            DataType = "string",
            VisibleIndex = 10,
            Width = "110px"
        },
        new()
        {
            Caption = AuditColumns.ModifiedCaption,
            FieldName = nameof(UserAdminRow.Updated),
            DataType = "datetime",
            VisibleIndex = 11,
            Width = "140px"
        },
        new()
        {
            Caption = AuditColumns.ModifiedByCaption,
            FieldName = nameof(UserAdminRow.UpdatedBy),
            DataType = "string",
            VisibleIndex = 12,
            Width = "110px"
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
        }

        PendingRequests = await Recovery.ListPendingAsync(CurrentUser.CompanyCode ?? string.Empty);

        IsLoading = false;
        _selectedUsers.Clear();
        _grid?.Reload();
    }

    public void OnCancel()
    {
        PopupVisible = false;
        PopupPassword = false;
        ErrorMessage = string.Empty;
        IsSubmitting = false;
    }

    public async Task OnActionClick(SelectedButtonInfo<UserAdminRow> info)
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

    public async Task OnButtonClick(SelectedButtonInfo<UserAdminRow> info)
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

        CreateModel = new CreateUserRequest
        {
            Active = true
        };
        ErrorMessage = string.Empty;
        IsEditMode = false;
        PopupVisible = true;
        editEnable = true;
    }

    public async Task OnEditClick(UserAdminRow data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Edit))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        EditModel = MapToEdit(data);
        ErrorMessage = string.Empty;
        IsEditMode = true;
        PopupVisible = true;
        editEnable = true;
    }

    public async Task OnChgPassClick(UserAdminRow data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Edit))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        ResetModel = new ResetUserPasswordRequest
        {
            Uid = data.Uid,
            LoginId = data.LoginId,
            Name = data.Name
        };
        ErrorMessage = string.Empty;
        PopupPassword = true;
    }

    public async Task OnForceChangeClick(UserAdminRow data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Edit))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        var force = !data.ChangePass;
        var result = await UserAdmin.ForceChangeAsync(data.Uid, force);
        if (result.Succeeded)
        {
            StatusMessage = force
                ? $"Password change required at next login for {data.LoginId}."
                : $"Password-change requirement cleared for {data.LoginId}.";
            await LoadPageDataAsync();
        }
        else
        {
            StatusMessage = result.ErrorMessage ?? "Unable to update password-change requirement.";
        }
    }

    protected async Task OnViewClick(UserAdminRow data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Access))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        EditModel = MapToEdit(data);
        ErrorMessage = string.Empty;
        IsEditMode = true;
        editEnable = false;
        PopupVisible = true;
    }

    protected async Task OnQueueResetAsync(PasswordResetRequestRow request)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Edit))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        ResetModel = new ResetUserPasswordRequest
        {
            Uid = request.UserUid,
            LoginId = request.LoginId,
            Name = request.Name
        };
        ErrorMessage = string.Empty;
        PopupPassword = true;
    }

    protected async Task OnQueueCancelAsync(PasswordResetRequestRow request)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminUsers, PermissionCodes.Edit))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        var actorUid = int.TryParse(CurrentUser.SubjectUid, out var a) ? a : 0;
        var result = await Recovery.CancelAsync(
            request.PasswordResetRequestId,
            CurrentUser.CompanyCode ?? string.Empty,
            CurrentUser.UserId ?? string.Empty,
            actorUid);

        StatusMessage = result.Succeeded
            ? $"Recovery request for {request.LoginId} cancelled."
            : (result.Detail ?? "Unable to cancel the recovery request.");
        await LoadPageDataAsync();
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

        var result = await UserAdmin.DeleteUsersAsync(_selectedUsers.Select(x => x.Uid).ToList());
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

        var result = await UserAdmin.ResetPasswordAsync(
            ResetModel.Uid,
            ResetModel.NewPassword,
            ResetModel.ConfirmPassword);

        if (result.Succeeded)
        {
            PopupPassword = false;
            ResetModel.NewPassword = string.Empty;
            ResetModel.ConfirmPassword = string.Empty;
            StatusMessage = $"Password reset for {ResetModel.LoginId}. The user must change the password at next login.";
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
            result = await UserAdmin.UpdateUserAsync(EditModel);
        }
        else
        {
            result = await UserAdmin.AddUserAsync(CreateModel);
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

    protected void OnSelectionsEvent(List<UserAdminRow> list)
    {
        if (Data is null)
        {
            _selectedUsers.Clear();
            return;
        }

        _selectedUsers.Clear();
        _selectedUsers.AddRange(list.Where(item => Data.Any(d => d.Uid == item.Uid)));
    }

    private static EditUserRequest MapToEdit(UserAdminRow data) =>
        new()
        {
            Uid = data.Uid,
            LoginId = data.LoginId,
            Name = data.Name,
            Email = data.Email,
            MobileNo = data.MobileNo,
            Active = data.Active ?? true,
            RoleCode = data.RoleCode ?? string.Empty,
            CanViewPrice = data.CanViewPrice
        };
}
