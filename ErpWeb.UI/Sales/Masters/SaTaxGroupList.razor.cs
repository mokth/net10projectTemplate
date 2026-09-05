using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Masters;

public partial class SaTaxGroupList : SaCodeRefListPageBase<SaTaxGroupListRow>
{
    protected override string MenuCode => MenuCodes.SalesTaxGroup;
    protected override string EntityLabel => "Tax Group";

    protected SaTaxGroupEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }
    private string? _loadedFingerprint;

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(SaTaxGroupListRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "100px"
        },
        new()
        {
            Caption = "Desc",
            FieldName = nameof(SaTaxGroupListRow.Desc),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Percentage",
            FieldName = nameof(SaTaxGroupListRow.Percentage),
            DataType = "decimal",
            VisibleIndex = 3,
            Width = "110px"
        },
        new()
        {
            Caption = "Company",
            FieldName = nameof(SaTaxGroupListRow.CompanyCode),
            DataType = "string",
            VisibleIndex = 4,
            Width = "100px"
        },
        new()
        {
            Caption = "Branch",
            FieldName = nameof(SaTaxGroupListRow.BranchCode),
            DataType = "string",
            VisibleIndex = 5,
            Width = "90px"
        },
        new()
        {
            Caption = "Location",
            FieldName = nameof(SaTaxGroupListRow.LocationCode),
            DataType = "string",
            VisibleIndex = 6,
            Width = "100px"
        }
    ];

    protected override async Task OnPageInitializedAsync() => await ReloadListAsync();

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await RefService.ListTaxGroupsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load tax groups.";
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

    protected override string GetRowCode(SaTaxGroupListRow row) => row.Code;

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<string> codes) =>
        RefService.CanDeleteTaxGroupsAsync(codes);

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<string> codes) =>
        RefService.DeleteTaxGroupsAsync(codes);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new SaTaxGroupEditVm();
        _loadedFingerprint = null;
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(SaTaxGroupListRow row)
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

    protected override async Task OnEditClickAsync(SaTaxGroupListRow row)
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
            var result = await RefService.SaveTaxGroupAsync(
                EditModel,
                isNew: !IsEditMode,
                expectedFingerprint: IsEditMode ? _loadedFingerprint : null);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Tax group updated successfully." : "Tax group added successfully.";
                await ReloadListAsync();
            }
            else if (result.ErrorCode == IvMasterErrorCode.Concurrency)
            {
                ErrorMessage = result.Message ?? "This record was modified by another user.";
                if (IsEditMode)
                {
                    await LoadEditModelAsync(EditModel.Code);
                }
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
        var result = await RefService.GetTaxGroupAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load tax group.";
            return false;
        }

        EditModel = result.Data;
        _loadedFingerprint = SaMasterFingerprint.TaxGroup(EditModel);
        ErrorMessage = null;
        return true;
    }
}
