using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Masters;

/// <summary>
/// LMW licence register. Key token is <c>Key(LicenseNo, rowVersion, parentCode: CustCode)</c> — the
/// <see cref="SaRefListPageBase{TRow}"/> shell is used rather than the keyed variant because the
/// keyed shell carries no row version (D-10/T2). There is no <c>Active</c> column, so the
/// ACTIVATE/DEACTIVATE buttons stay hidden (<c>SupportsActivate</c> keeps its false default).
/// </summary>
public partial class SaLMWList : SaRefListPageBase<SaLMWListRow>
{
    protected override string MenuCode => MenuCodes.SalesLmw;
    protected override string EntityLabel => "LMW Licence";
    protected override string? ExportRoute => "/sales/lmw/export";

    protected SaLMWEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Licence No",
            FieldName = nameof(SaLMWListRow.LicenseNo),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "150px"
        },
        new()
        {
            Caption = "Customer",
            FieldName = nameof(SaLMWListRow.CustCode),
            DataType = "string",
            VisibleIndex = 2,
            Width = "120px"
        },
        new()
        {
            Caption = "Customer Name",
            FieldName = nameof(SaLMWListRow.CustName),
            DataType = "string",
            VisibleIndex = 3
        },
        new()
        {
            Caption = "Licence Start",
            FieldName = nameof(SaLMWListRow.LicenseStartDate),
            DataType = "date",
            VisibleIndex = 4,
            Width = "110px"
        },
        new()
        {
            Caption = "Licence End",
            FieldName = nameof(SaLMWListRow.LicenseEndDate),
            DataType = "date",
            VisibleIndex = 5,
            Width = "110px"
        },
        new()
        {
            Caption = "System Start",
            FieldName = nameof(SaLMWListRow.SystemStartDate),
            DataType = "date",
            VisibleIndex = 6,
            Width = "110px"
        },
        new()
        {
            Caption = "System End",
            FieldName = nameof(SaLMWListRow.SystemEndDate),
            DataType = "date",
            VisibleIndex = 7,
            Width = "110px"
        },
        new()
        {
            Caption = "Name",
            FieldName = nameof(SaLMWListRow.Name),
            DataType = "string",
            VisibleIndex = 8
        }
    ];

    protected override async Task OnPageInitializedAsync() => await ReloadListAsync();

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await RefService.ListLmwsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load LMW licences.";
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

    protected override IvMasterKeyToken ToKeyToken(SaLMWListRow row) =>
        Key(row.LicenseNo, row.RowVersion, parentCode: row.CustCode);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive) =>
        Task.FromResult(IvMasterOperationResult<object>.Fail(
            IvMasterErrorCode.Validation,
            "LMW licences have no Active flag."));

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<SaLMWListRow> rows) =>
        RefService.CanDeleteLmwsAsync(rows
            .Select(r => new SaLMWKey { LicenseNo = r.LicenseNo, CustCode = r.CustCode })
            .ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        RefService.DeleteLmwsAsync(ToSaKeys(items));

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new SaLMWEditVm
        {
            LicenseStartDate = DateTime.Today,
            LicenseEndDate = DateTime.Today,
            SystemStartDate = DateTime.Today,
            SystemEndDate = DateTime.Today
        };
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(SaLMWListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access))
        {
            return;
        }

        if (!await LoadEditModelAsync(row.LicenseNo, row.CustCode))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(SaLMWListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        if (!await LoadEditModelAsync(row.LicenseNo, row.CustCode))
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
            var result = await RefService.SaveLmwAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode
                    ? "LMW licence updated successfully."
                    : "LMW licence added successfully.";
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

    private async Task<bool> LoadEditModelAsync(string licenseNo, string custCode)
    {
        var result = await RefService.GetLmwAsync(licenseNo, custCode);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load the LMW licence.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }
}
