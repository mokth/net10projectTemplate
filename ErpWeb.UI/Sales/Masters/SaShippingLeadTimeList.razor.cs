using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Masters;

public partial class SaShippingLeadTimeList : SaRefListPageBase<SaShippingLeadTimeListRow>
{
    protected override string MenuCode => MenuCodes.SalesShipLeadTime;
    protected override string EntityLabel => "Shipping Lead Time";
    protected override bool SupportsActivate => true;
    protected override string? ExportRoute => "/sales/shipping-lead-time/export";

    protected SaShippingLeadTimeEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }

    /// <summary>Only these two values are ever stored non-null (D-5).</summary>
    protected static readonly string[] LeadTimeTypes = ["INTERNAL", "EXTERNAL"];

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Lead Time Code",
            FieldName = nameof(SaShippingLeadTimeListRow.Code),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "150px"
        },
        new()
        {
            Caption = "Description",
            FieldName = nameof(SaShippingLeadTimeListRow.Desc),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Days",
            FieldName = nameof(SaShippingLeadTimeListRow.Days),
            DataType = "int",
            VisibleIndex = 3,
            Width = "80px"
        },
        new()
        {
            Caption = "Type",
            FieldName = nameof(SaShippingLeadTimeListRow.Type),
            DataType = "string",
            VisibleIndex = 4,
            Width = "110px"
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(SaShippingLeadTimeListRow.IsActive),
            DataType = "bool",
            VisibleIndex = 5,
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
            var result = await RefService.ListShippingLeadTimesAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load shipping lead times.";
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

    protected override IvMasterKeyToken ToKeyToken(SaShippingLeadTimeListRow row) =>
        Key(row.Code, row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive) =>
        RefService.SetShippingLeadTimeActiveAsync(ToSaKeys(items), isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<SaShippingLeadTimeListRow> rows) =>
        RefService.CanDeleteShippingLeadTimesAsync(rows.Select(r => r.Code).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        RefService.DeleteShippingLeadTimesAsync(ToSaKeys(items));

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new SaShippingLeadTimeEditVm { IsActive = true };
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(SaShippingLeadTimeListRow row)
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

    protected override async Task OnEditClickAsync(SaShippingLeadTimeListRow row)
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
            var result = await RefService.SaveShippingLeadTimeAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode
                    ? "Shipping lead time updated successfully."
                    : "Shipping lead time added successfully.";
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
        var result = await RefService.GetShippingLeadTimeAsync(code);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load shipping lead time.";
            return false;
        }

        EditModel = result.Data;
        ErrorMessage = null;
        return true;
    }
}
