using DevExpress.Blazor;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ErpWeb.UI.Admin;

public partial class AdminPermission : PageBase
{
    [Inject] private IPermissionAdminService PermissionAdmin { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    private DxGrid? _grid;

    protected bool PopupVisible;
    protected Permission selectedRow { get; set; } = new();
    protected bool IsEditMode;
    protected bool IsLoading = true;
    protected bool IsSubmitting;
    protected bool editEnable;
    protected string? StatusMessage;

    protected IEnumerable<Permission>? Data;
    private readonly List<Permission> _selectedRows = [];

    protected static readonly string[] PermissionTypes = ["Navigation", "Action", "Data"];

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(Permission.PermissionCode),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "130px"
        },
        new()
        {
            Caption = "Name",
            FieldName = nameof(Permission.PermissionName),
            DataType = "string",
            VisibleIndex = 2,
            Width = "150px"
        },
        new()
        {
            Caption = "Type",
            FieldName = nameof(Permission.PermissionType),
            DataType = "string",
            VisibleIndex = 3,
            Width = "110px"
        },
        new()
        {
            Caption = "Sort",
            FieldName = nameof(Permission.SortOrder),
            DataType = "int",
            VisibleIndex = 4,
            Width = "70px"
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(Permission.IsActive),
            DataType = "bool",
            VisibleIndex = 5,
            Width = "80px"
        },
        new()
        {
            Caption = "Description",
            FieldName = nameof(Permission.Description),
            DataType = "string",
            VisibleIndex = 6
        },
        new()
        {
            Caption = "Created By",
            FieldName = nameof(Permission.CreatedBy),
            DataType = "string",
            VisibleIndex = 7,
            Width = "100px"
        },
        new()
        {
            Caption = "Update By",
            FieldName = nameof(Permission.ModifiedBy),
            DataType = "string",
            VisibleIndex = 8,
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
            ToolTip = "Delete Permission"
        }
    ];

    public List<ButtonInfo> ActionButtons { get; set; } =
    [
        new()
        {
            IConClass = "fa-regular fa-eye",
            Style = "primary",
            Text = "View",
            ToolTip = "View Permission"
        },
        new()
        {
            IConClass = "far fa-edit",
            Style = "primary",
            Text = "Edit",
            ToolTip = "Edit Permission"
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

        var result = await PermissionAdmin.GetPermissionsAsync();
        if (!result.Succeeded)
        {
            StatusMessage = result.ErrorMessage ?? "Unable to load permissions.";
            Data = [];
        }
        else
        {
            Data = result.Permissions;
            selectedRow = Data.FirstOrDefault() ?? new Permission();
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

    public async Task OnActionClick(SelectedButtonInfo<Permission> info)
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

    public async Task OnButtonClick(SelectedButtonInfo<Permission> info)
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
        if (!await AccessRights.CanAsync(MenuCodes.AdminPermissions, PermissionCodes.Add))
        {
            StatusMessage = "Access Denied!!";
            return;
        }

        selectedRow = new Permission
        {
            IsActive = true,
            PermissionType = "Action",
            SortOrder = 100
        };
        ErrorMessage = string.Empty;
        IsEditMode = false;
        PopupVisible = true;
        editEnable = true;
    }

    public async Task OnEditClick(Permission data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminPermissions, PermissionCodes.Edit))
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

    protected async Task OnViewClick(Permission data)
    {
        if (!await AccessRights.CanAsync(MenuCodes.AdminPermissions, PermissionCodes.Access))
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
        if (!await AccessRights.CanAsync(MenuCodes.AdminPermissions, PermissionCodes.Delete))
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
            $"Delete {_selectedRows.Count} selected permission(s)?");
        if (!confirmed)
        {
            return;
        }

        var result = await PermissionAdmin.DeletePermissionsAsync(
            _selectedRows.Select(x => x.PermissionId).ToList());
        if (result.Succeeded)
        {
            StatusMessage = "Permission(s) deleted successfully.";
            await LoadPageDataAsync();
        }
        else
        {
            StatusMessage = result.ErrorMessage ?? "Unable to delete permission(s).";
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

        PermissionAdminOperationResult result;
        if (IsEditMode)
        {
            result = await PermissionAdmin.UpdatePermissionAsync(selectedRow);
        }
        else
        {
            result = await PermissionAdmin.AddPermissionAsync(selectedRow);
        }

        if (result.Succeeded)
        {
            PopupVisible = false;
            StatusMessage = IsEditMode
                ? "Permission updated successfully."
                : "Permission added successfully.";
            await LoadPageDataAsync();
        }
        else
        {
            ErrorMessage = result.ErrorMessage ?? "Unable to save permission.";
        }

        IsSubmitting = false;
    }

    protected void OnSelectionsEvent(List<Permission> list)
    {
        if (Data is null)
        {
            _selectedRows.Clear();
            return;
        }

        _selectedRows.Clear();
        _selectedRows.AddRange(list.Where(item => Data.Any(d => d.PermissionId == item.PermissionId)));
    }

    private static Permission CloneForEdit(Permission data) =>
        new()
        {
            PermissionId = data.PermissionId,
            PermissionCode = data.PermissionCode,
            PermissionName = data.PermissionName,
            PermissionType = data.PermissionType,
            Description = data.Description,
            SortOrder = data.SortOrder,
            IsActive = data.IsActive,
            CreatedDate = data.CreatedDate,
            CreatedBy = data.CreatedBy,
            ModifiedDate = data.ModifiedDate,
            ModifiedBy = data.ModifiedBy
        };
}
