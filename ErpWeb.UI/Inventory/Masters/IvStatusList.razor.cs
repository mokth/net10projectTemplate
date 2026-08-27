using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Inventory.Masters;

public partial class IvStatusList : IvRefListPageBase<IvStatusListRow>
{
    protected override string MenuCode => MenuCodes.InventoryStatus;
    protected override string EntityLabel => "Status";

    protected IvStatusEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(IvStatusListRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "120px"
        },
        new()
        {
            Caption = "Desc",
            FieldName = nameof(IvStatusListRow.Desc),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(IvStatusListRow.IsActive),
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
            var result = await RefService.ListStatusesAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load statuses.";
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

    protected override IvMasterKeyToken ToKeyToken(IvStatusListRow row) =>
        Key(row.Code, row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive) =>
        RefService.SetStatusActiveAsync(items, isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<IvStatusListRow> rows) =>
        RefService.CanDeleteStatusesAsync(rows.Select(r => r.Code).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        RefService.DeleteStatusesAsync(items);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new IvStatusEditVm { IsActive = true };
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(IvStatusListRow row)
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

    protected override async Task OnEditClickAsync(IvStatusListRow row)
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
            var result = await RefService.SaveStatusAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Status updated successfully." : "Status added successfully.";
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
        var result = await RefService.GetStatusAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load status.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }
}
