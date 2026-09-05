using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Masters;

public partial class SaCurrencyList : SaCodeRefListPageBase<SaCurrencyListRow>
{
    protected override string MenuCode => MenuCodes.SalesCurrency;
    protected override string EntityLabel => "Currency";
    protected override bool SupportsActivate => true;

    protected SaCurrencyEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(SaCurrencyListRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "100px"
        },
        new()
        {
            Caption = "Desc",
            FieldName = nameof(SaCurrencyListRow.Desc),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(SaCurrencyListRow.IsActive),
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
            var result = await RefService.ListCurrenciesAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load currencies.";
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

    protected override string GetRowCode(SaCurrencyListRow row) => row.Code;

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<string> codes) =>
        RefService.CanDeleteCurrenciesAsync(codes);

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<string> codes) =>
        RefService.DeleteCurrenciesAsync(codes);

    protected override Task<IvMasterOperationResult<object>> SetActiveByCodesAsync(
        IReadOnlyList<string> codes,
        bool isActive) =>
        RefService.SetCurrencyActiveAsync(codes, isActive);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new SaCurrencyEditVm { IsActive = true };
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(SaCurrencyListRow row)
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

    protected override async Task OnEditClickAsync(SaCurrencyListRow row)
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
            var result = await RefService.SaveCurrencyAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Currency updated successfully." : "Currency added successfully.";
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
        var result = await RefService.GetCurrencyAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load currency.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }
}
