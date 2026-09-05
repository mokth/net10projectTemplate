using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Masters;

public partial class SaSalesRepList : SaCodeRefListPageBase<SaSalesRepListRow>
{
    protected override string MenuCode => MenuCodes.SalesSalesRep;
    protected override string EntityLabel => "Sales Rep";
    protected override bool SupportsActivate => true;

    protected SaSalesRepEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }
    private string? _loadedFingerprint;

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(SaSalesRepListRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "100px"
        },
        new()
        {
            Caption = "Name",
            FieldName = nameof(SaSalesRepListRow.Name),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Tel",
            FieldName = nameof(SaSalesRepListRow.Tel),
            DataType = "string",
            VisibleIndex = 3,
            Width = "120px"
        },
        new()
        {
            Caption = "Email",
            FieldName = nameof(SaSalesRepListRow.Email),
            DataType = "string",
            VisibleIndex = 4,
            Width = "180px"
        },
        new()
        {
            Caption = "Commission",
            FieldName = nameof(SaSalesRepListRow.CommissionRate),
            DataType = "decimal",
            VisibleIndex = 5,
            Width = "110px"
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(SaSalesRepListRow.IsActive),
            DataType = "bool",
            VisibleIndex = 6,
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
            var result = await RefService.ListSalesRepsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load sales reps.";
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

    protected override string GetRowCode(SaSalesRepListRow row) => row.Code;

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<string> codes) =>
        RefService.CanDeleteSalesRepsAsync(codes);

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<string> codes) =>
        RefService.DeleteSalesRepsAsync(codes);

    protected override Task<IvMasterOperationResult<object>> SetActiveByCodesAsync(
        IReadOnlyList<string> codes,
        bool isActive) =>
        RefService.SetSalesRepActiveAsync(codes, isActive);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new SaSalesRepEditVm { IsActive = true };
        _loadedFingerprint = null;
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(SaSalesRepListRow row)
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

    protected override async Task OnEditClickAsync(SaSalesRepListRow row)
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
            var result = await RefService.SaveSalesRepAsync(
                EditModel,
                isNew: !IsEditMode,
                expectedFingerprint: IsEditMode ? _loadedFingerprint : null);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Sales rep updated successfully." : "Sales rep added successfully.";
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
        var result = await RefService.GetSalesRepAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load sales rep.";
            return false;
        }

        EditModel = result.Data;
        _loadedFingerprint = SaMasterFingerprint.SalesRep(EditModel);
        ErrorMessage = null;
        return true;
    }
}
