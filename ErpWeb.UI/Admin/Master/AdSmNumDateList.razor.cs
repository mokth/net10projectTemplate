using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Admin.Master;

public partial class AdSmNumDateList : AdSmNumListPageBase<AdSmNumDateListRow, AdSmNumDateKey>
{
    protected override string MenuCode => MenuCodes.AdminSmNumDate;
    protected override string EntityLabel => "Period Number";

    protected AdSmNumDateEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }

    protected bool NamespaceFrozen =>
        !EditEnabled || (IsEditMode && EditModel.OriginalSeq > 1);

    protected string YearMonthHint =>
        "0/0 = continuous date (no monthly reset); year/0 = yearly; year/month = monthly. Year=0 with Month 1–12 is invalid.";

    protected string SampleNumber
    {
        get
        {
            SampleError = null;
            try
            {
                var doc = AdSmNumAdminService.FormatPeriodSample(
                    EditModel.Prefix,
                    EditModel.Seq,
                    EditModel.TotLength,
                    EditModel.NumberingDelimeter,
                    EditModel.NumberingFormat,
                    EditModel.Year,
                    EditModel.Month);
                DocumentNumberFormatter.EnsureFitsMaxLength(doc);
                return doc;
            }
            catch (Exception ex) when (
                ex is DocumentNumberingConfigurationException or DocumentNumberingOverflowException)
            {
                SampleError = ex.Message;
                return string.Empty;
            }
        }
    }

    protected string? SampleError { get; set; }

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "NumCd",
            FieldName = nameof(AdSmNumDateListRow.NumCd),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "90px"
        },
        new()
        {
            Caption = "Year",
            FieldName = nameof(AdSmNumDateListRow.Year),
            DataType = "number",
            VisibleIndex = 2,
            Width = "70px"
        },
        new()
        {
            Caption = "Month",
            FieldName = nameof(AdSmNumDateListRow.Month),
            DataType = "number",
            VisibleIndex = 3,
            Width = "70px"
        },
        new()
        {
            Caption = "Prefix",
            FieldName = nameof(AdSmNumDateListRow.Prefix),
            DataType = "string",
            VisibleIndex = 4,
            Width = "90px"
        },
        new()
        {
            Caption = "Digits",
            FieldName = nameof(AdSmNumDateListRow.TotLength),
            DataType = "number",
            VisibleIndex = 5,
            Width = "70px"
        },
        new()
        {
            Caption = "Next seq",
            FieldName = nameof(AdSmNumDateListRow.Seq),
            DataType = "number",
            VisibleIndex = 6,
            Width = "90px"
        },
        new()
        {
            Caption = "Delimiter",
            FieldName = nameof(AdSmNumDateListRow.NumberingDelimeter),
            DataType = "string",
            VisibleIndex = 7,
            Width = "80px"
        },
        new()
        {
            Caption = "Format",
            FieldName = nameof(AdSmNumDateListRow.NumberingFormat),
            DataType = "string",
            VisibleIndex = 8,
            Width = "140px"
        },
        new()
        {
            Caption = "Description",
            FieldName = nameof(AdSmNumDateListRow.NumDes),
            DataType = "string",
            VisibleIndex = 9
        }
    ];

    protected override async Task OnPageInitializedAsync() => await ReloadListAsync();

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await NumService.ListPeriodAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load period numbers.";
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

    protected override AdSmNumDateKey ToKey(AdSmNumDateListRow row) =>
        new() { Uid = row.Uid, RowVersion = row.RowVersion };

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<AdSmNumDateListRow> rows) =>
        NumService.CanDeletePeriodAsync(rows.Select(ToKey).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<AdSmNumDateKey> keys) =>
        NumService.DeletePeriodAsync(keys);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        var today = DateTime.Today;
        EditModel = new AdSmNumDateEditVm
        {
            Seq = 1,
            TotLength = 4,
            Year = (short)today.Year,
            Month = (short)today.Month,
            NumberingDelimeter = "-",
            OriginalSeq = 1
        };
        ErrorMessage = null;
        SampleError = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(AdSmNumDateListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access))
        {
            return;
        }

        if (!await LoadEditModelAsync(row.Uid))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(AdSmNumDateListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        if (!await LoadEditModelAsync(row.Uid))
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
            var result = await NumService.SavePeriodAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode
                    ? "Period number updated successfully."
                    : "Period number added successfully.";
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

    private async Task<bool> LoadEditModelAsync(int uid)
    {
        var result = await NumService.GetPeriodAsync(uid);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load period numbering.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        SampleError = null;
        return true;
    }
}
