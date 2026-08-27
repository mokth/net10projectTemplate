using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Inventory.Masters;

public partial class IvTypeList : IvRefListPageBase<IvTypeListRow>
{
    protected override string MenuCode => MenuCodes.InventoryType;
    protected override string EntityLabel => "Type";

    protected IvTypeEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(IvTypeListRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "110px"
        },
        new()
        {
            Caption = "Name",
            FieldName = nameof(IvTypeListRow.TypeName),
            DataType = "string",
            VisibleIndex = 2,
            Width = "160px"
        },
        new()
        {
            Caption = "Desc",
            FieldName = nameof(IvTypeListRow.Desc),
            DataType = "string",
            VisibleIndex = 3
        },
        new()
        {
            Caption = "KeepStock",
            FieldName = nameof(IvTypeListRow.KeepStock),
            DataType = "bool",
            VisibleIndex = 4,
            Width = "100px"
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(IvTypeListRow.IsActive),
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
            var result = await RefService.ListTypesAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load types.";
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

    protected override IvMasterKeyToken ToKeyToken(IvTypeListRow row) =>
        Key(row.Code, row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive) =>
        RefService.SetTypeActiveAsync(items, isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<IvTypeListRow> rows) =>
        RefService.CanDeleteTypesAsync(rows.Select(r => r.Code).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        RefService.DeleteTypesAsync(items);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new IvTypeEditVm { IsActive = true, KeepStock = true };
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(IvTypeListRow row)
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

    protected override async Task OnEditClickAsync(IvTypeListRow row)
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
            var result = await RefService.SaveTypeAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Type updated successfully." : "Type added successfully.";
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

    private async Task<bool> LoadEditModelAsync(string code)
    {
        var result = await RefService.GetTypeAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load type.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }
}
