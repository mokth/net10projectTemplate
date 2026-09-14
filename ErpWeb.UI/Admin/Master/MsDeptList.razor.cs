using DevExpress.Blazor;
using ErpWeb.Core.Admin;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Admin.Master;

public partial class MsDeptList : MsRefListPageBase<MsDeptListRow>
{
    protected override string MenuCode => MenuCodes.AdminDept;
    protected override string EntityLabel => "Department";

    protected MsDeptEditVm EditModel { get; set; } = new();

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(MsDeptListRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "110px"
        },
        new()
        {
            Caption = "Name",
            FieldName = nameof(MsDeptListRow.Name),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Manager",
            FieldName = nameof(MsDeptListRow.ManagerEmpId),
            DataType = "string",
            VisibleIndex = 3,
            Width = "120px"
        },
        new()
        {
            Caption = "GL Code",
            FieldName = nameof(MsDeptListRow.GlCode),
            DataType = "string",
            VisibleIndex = 4,
            Width = "110px"
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(MsDeptListRow.IsActive),
            DataType = "bool",
            VisibleIndex = 5,
            Width = "80px"
        }
    ];

    protected override async Task OnPageInitializedAsync() => await ReloadListAsync();

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await MsRef.ListDepartmentsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load departments.";
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

    protected override string GetRowCode(MsDeptListRow row) => row.Code;

    protected override IvMasterKeyToken ToKeyToken(MsDeptListRow row) => Key(row.Code, row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive) =>
        MsRef.SetDepartmentActiveAsync(items, isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<string> codes) =>
        MsRef.CanDeleteDepartmentsAsync(codes);

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        MsRef.DeleteDepartmentsAsync(items);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new MsDeptEditVm();
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(MsDeptListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access))
        {
            return;
        }

        if (!await LoadEditModelAsync(row.Code))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(MsDeptListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        if (!await LoadEditModelAsync(row.Code))
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
            var result = await MsRef.SaveDepartmentAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode
                    ? "Department updated successfully."
                    : "Department added successfully.";
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

    private async Task<bool> LoadEditModelAsync(string code)
    {
        var result = await MsRef.GetDepartmentAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load department.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }
}
