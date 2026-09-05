using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Masters;

public partial class SaPaymentTermList : SaCodeRefListPageBase<SaPaymentTermListRow>
{
    protected override string MenuCode => MenuCodes.SalesPayTerm;
    protected override string EntityLabel => "Payment Term";
    protected override bool SupportsActivate => true;

    protected SaPaymentTermEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }
    private string? _loadedFingerprint;

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(SaPaymentTermListRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "100px"
        },
        new()
        {
            Caption = "Desc",
            FieldName = nameof(SaPaymentTermListRow.Desc),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Days",
            FieldName = nameof(SaPaymentTermListRow.Days),
            DataType = "int",
            VisibleIndex = 3,
            Width = "80px"
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(SaPaymentTermListRow.IsActive),
            DataType = "bool",
            VisibleIndex = 4,
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
            var result = await RefService.ListPaymentTermsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load payment terms.";
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

    protected override string GetRowCode(SaPaymentTermListRow row) => row.Code;

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<string> codes) =>
        RefService.CanDeletePaymentTermsAsync(codes);

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<string> codes) =>
        RefService.DeletePaymentTermsAsync(codes);

    protected override Task<IvMasterOperationResult<object>> SetActiveByCodesAsync(
        IReadOnlyList<string> codes,
        bool isActive) =>
        RefService.SetPaymentTermActiveAsync(codes, isActive);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new SaPaymentTermEditVm { IsActive = true };
        _loadedFingerprint = null;
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(SaPaymentTermListRow row)
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

    protected override async Task OnEditClickAsync(SaPaymentTermListRow row)
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
            var result = await RefService.SavePaymentTermAsync(
                EditModel,
                isNew: !IsEditMode,
                expectedFingerprint: IsEditMode ? _loadedFingerprint : null);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Payment term updated successfully." : "Payment term added successfully.";
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
        var result = await RefService.GetPaymentTermAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load payment term.";
            return false;
        }

        EditModel = result.Data;
        _loadedFingerprint = SaMasterFingerprint.PaymentTerm(EditModel);
        ErrorMessage = null;
        return true;
    }
}
