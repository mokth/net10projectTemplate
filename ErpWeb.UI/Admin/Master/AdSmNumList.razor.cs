using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Admin.Master;

public partial class AdSmNumList : AdSmNumListPageBase<AdSmNumListRow, string>
{
    protected override string MenuCode => MenuCodes.AdminSmNum;
    protected override string EntityLabel => "Continuous Number";

    protected AdSmNumEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }

    protected bool NamespaceFrozen =>
        !EditEnabled || (IsEditMode && EditModel.OriginalSeq > 1);

    protected string SampleNumber
    {
        get
        {
            SampleError = null;
            try
            {
                var doc = AdSmNumAdminService.FormatContinuousSample(
                    EditModel.Prefix, EditModel.Seq, EditModel.TotLength);
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
            FieldName = nameof(AdSmNumListRow.NumCd),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "100px"
        },
        new()
        {
            Caption = "Prefix",
            FieldName = nameof(AdSmNumListRow.Prefix),
            DataType = "string",
            VisibleIndex = 2,
            Width = "100px"
        },
        new()
        {
            Caption = "Total length",
            FieldName = nameof(AdSmNumListRow.TotLength),
            DataType = "number",
            VisibleIndex = 3,
            Width = "110px"
        },
        new()
        {
            Caption = "Next seq",
            FieldName = nameof(AdSmNumListRow.Seq),
            DataType = "number",
            VisibleIndex = 4,
            Width = "100px"
        },
        new()
        {
            Caption = "Description",
            FieldName = nameof(AdSmNumListRow.NumDes),
            DataType = "string",
            VisibleIndex = 5
        }
    ];

    protected override async Task OnPageInitializedAsync() => await ReloadListAsync();

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await NumService.ListContinuousAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load continuous numbers.";
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

    protected override string ToKey(AdSmNumListRow row) => row.NumCd;

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<AdSmNumListRow> rows) =>
        NumService.CanDeleteContinuousAsync(rows.Select(r => r.NumCd).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(IReadOnlyList<string> keys) =>
        NumService.DeleteContinuousAsync(keys);

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new AdSmNumEditVm { Seq = 1, TotLength = 10, OriginalSeq = 1 };
        ErrorMessage = null;
        SampleError = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(AdSmNumListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access))
        {
            return;
        }

        if (!await LoadEditModelAsync(row.NumCd))
        {
            return;
        }

        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(AdSmNumListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit))
        {
            return;
        }

        if (!await LoadEditModelAsync(row.NumCd))
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
            var result = await NumService.SaveContinuousAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode
                    ? "Continuous number updated successfully."
                    : "Continuous number added successfully.";
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

    private async Task<bool> LoadEditModelAsync(string numCd)
    {
        var result = await NumService.GetContinuousAsync(numCd);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load continuous numbering.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        SampleError = null;
        return true;
    }
}
