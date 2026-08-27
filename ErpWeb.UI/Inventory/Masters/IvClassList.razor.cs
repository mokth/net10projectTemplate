using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Inventory.Masters;

public partial class IvClassList : IvRefListPageBase<IvClassListRow>
{
    protected override string MenuCode => MenuCodes.InventoryClass;
    protected override string EntityLabel => "Class";

    protected IvClassEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(IvClassListRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "140px"
        },
        new()
        {
            Caption = "Desc",
            FieldName = nameof(IvClassListRow.Desc),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(IvClassListRow.IsActive),
            DataType = "bool",
            VisibleIndex = 3,
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
            var result = await RefService.ListClassesAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load classes.";
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

    protected override IvMasterKeyToken ToKeyToken(IvClassListRow row) =>
        Key(row.Code, row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive) =>
        RefService.SetClassActiveAsync(items, isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<IvClassListRow> rows) =>
        RefService.CanDeleteClassesAsync(rows.Select(r => r.Code).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        RefService.DeleteClassesAsync(items);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new IvClassEditVm
        {
            IsActive = true,
            SubClasses = []
        };
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(IvClassListRow row)
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

    protected override async Task OnEditClickAsync(IvClassListRow row)
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

    protected void AddSubclassRow()
    {
        EditModel.SubClasses.Add(new IvSubClassEditVm { IsActive = true });
    }

    protected void RemoveSubclassRow(IvSubClassEditVm row)
    {
        EditModel.SubClasses.Remove(row);
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
            var result = await RefService.SaveClassAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Class updated successfully." : "Class added successfully.";
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
        var result = await RefService.GetClassAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load class.";
            return false;
        }

        EditModel = result.Data;
        EditModel.SubClasses ??= [];
        ErrorMessage = null;
        return true;
    }
}
