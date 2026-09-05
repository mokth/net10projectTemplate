using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Masters;

public partial class SaCurrRateList : SaKeyedRefListPageBase<SaCurrRateListRow, SaCurrRateKey>
{
    protected override string MenuCode => MenuCodes.SalesCurrRate;
    protected override string EntityLabel => "Currency Rate";

    protected SaCurrRateEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }
    protected IReadOnlyList<SaCurrencyListRow> CurrencyOptions { get; set; } = [];

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Currency",
            FieldName = nameof(SaCurrRateListRow.CurrCode),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "100px"
        },
        new()
        {
            Caption = "Start Date",
            FieldName = nameof(SaCurrRateListRow.StartDate),
            DataType = "date",
            VisibleIndex = 2,
            Width = "120px"
        },
        new()
        {
            Caption = "End Date",
            FieldName = nameof(SaCurrRateListRow.EndDate),
            DataType = "date",
            VisibleIndex = 3,
            Width = "120px"
        },
        new()
        {
            Caption = "Rate",
            FieldName = nameof(SaCurrRateListRow.HomeCurPerUnit),
            DataType = "number",
            VisibleIndex = 4,
            Width = "120px"
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(SaCurrRateListRow.Status),
            DataType = "bool",
            VisibleIndex = 5,
            Width = "80px"
        }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        await LoadCurrencyOptionsAsync();
        await ReloadListAsync();
    }

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await RefService.ListCurrRatesAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load currency rates.";
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

    protected override SaCurrRateKey ToKey(SaCurrRateListRow row) =>
        new()
        {
            CurrCode = row.CurrCode,
            StartDate = row.StartDate,
            EndDate = row.EndDate
        };

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<SaCurrRateListRow> rows) =>
        RefService.CanDeleteCurrRatesAsync(rows.Select(ToKey).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<SaCurrRateKey> keys) =>
        RefService.DeleteCurrRatesAsync(keys);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        await LoadCurrencyOptionsAsync();
        EditModel = new SaCurrRateEditVm
        {
            StartDate = DateTime.Today,
            EndDate = DateTime.Today,
            HomeCurPerUnit = 1,
            Status = true
        };
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(SaCurrRateListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access))
        {
            return;
        }

        if (!await LoadEditModelAsync(row))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(SaCurrRateListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        if (!await LoadEditModelAsync(row))
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
            var result = await RefService.SaveCurrRateAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Currency rate updated successfully." : "Currency rate added successfully.";
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

    private async Task LoadCurrencyOptionsAsync()
    {
        var result = await RefService.ListCurrenciesAsync();
        CurrencyOptions = result.Succeeded ? result.Data ?? [] : [];
    }

    private async Task<bool> LoadEditModelAsync(SaCurrRateListRow row)
    {
        await LoadCurrencyOptionsAsync();
        var result = await RefService.GetCurrRateAsync(ToKey(row));
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load currency rate.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }
}
