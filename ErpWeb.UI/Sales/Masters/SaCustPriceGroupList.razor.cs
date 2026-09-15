using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Masters;

/// <summary>
/// Price Groups (<c>IvCustPriceGroup</c>) with their item prices (<c>IvCustPrice</c>).
///
/// The header and its lines are saved as ONE aggregate in one transaction (plan §10): this page only
/// assembles the payload, the service does header → lines → duplicates → item/UOM existence → write, and
/// rolls everything back if any line fails.
/// </summary>
public partial class SaCustPriceGroupList : SaRefListPageBase<IvCustPriceGroupListRow>
{
    protected override string MenuCode => MenuCodes.SalesCustPriceGroup;
    protected override string EntityLabel => "Price Group";

    /// <summary>The header carries the Active flag, so this screen supports the activate toolbar.</summary>
    protected override bool SupportsActivate => true;

    protected override string? ExportRoute => "/sales/price-groups/export";

    protected IvCustPriceGroupEditVm EditModel { get; set; } = new();
    protected bool CanEditFromView { get; set; }
    protected bool CanViewPrice { get; set; }

    // Line editor state.
    protected string? LineItem { get; set; }
    protected string? LineUom { get; set; }
    protected decimal? LinePrice { get; set; }
    protected decimal? LinePack { get; set; }

    /// <summary>Key of the line currently being edited; null = the editor is adding a new line.</summary>
    protected string? LineEditKey { get; set; }

    public List<GridColumnData> Columns() =>
    [
        new()
        {
            Caption = "Code",
            FieldName = nameof(IvCustPriceGroupListRow.CustPriceCode),
            DataType = "string",
            SortIndex = 0,
            SortOrder = GridColumnSortOrder.Ascending,
            VisibleIndex = 1,
            Width = "150px"
        },
        new()
        {
            Caption = "Description",
            FieldName = nameof(IvCustPriceGroupListRow.CustPriceDesc),
            DataType = "string",
            VisibleIndex = 2
        },
        new()
        {
            Caption = "Items",
            FieldName = nameof(IvCustPriceGroupListRow.LineCount),
            DataType = "number",
            VisibleIndex = 3,
            Width = "90px"
        },
        new()
        {
            Caption = "Active",
            FieldName = nameof(IvCustPriceGroupListRow.IsActive),
            DataType = "boolean",
            VisibleIndex = 4,
            Width = "90px"
        }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        CanViewPrice = await AccessRights.CanAsync(MenuCode, PermissionCodes.ViewPrice);
        await ReloadListAsync();
    }

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await RefService.ListCustPriceGroupsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load price groups.";
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

    protected override IvMasterKeyToken ToKeyToken(IvCustPriceGroupListRow row) =>
        Key(row.CustPriceCode, row.RowVersion);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive) =>
        RefService.SetCustPriceGroupActiveAsync(ToTokens(items), isActive);

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<IvCustPriceGroupListRow> rows) =>
        RefService.CanDeleteCustPriceGroupsAsync(rows.Select(r => r.CustPriceCode).ToList());

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        RefService.DeleteCustPriceGroupsAsync(ToTokens(items));

    private static List<SaItemFamilyKeyToken> ToTokens(IReadOnlyList<IvMasterKeyToken> items) =>
        items.Select(i => new SaItemFamilyKeyToken { Key = i.Code, RowVersion = i.RowVersion }).ToList();

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add))
        {
            return;
        }

        EditModel = new IvCustPriceGroupEditVm { IsActive = true };
        ResetLineEditor();
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(IvCustPriceGroupListRow row)
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

    protected override async Task OnEditClickAsync(IvCustPriceGroupListRow row)
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

    /// <summary>Adds the editor's line, or replaces the one being edited. Item + UOM form the line key.</summary>
    protected void OnLineAddOrUpdate()
    {
        var item = (LineItem ?? string.Empty).Trim().ToUpperInvariant();
        var uom = (LineUom ?? string.Empty).Trim().ToUpperInvariant();
        if (item.Length == 0 || uom.Length == 0)
        {
            ErrorMessage = "Item and UOM are required for a price line.";
            return;
        }

        var key = $"{item};{uom}";
        var existing = EditModel.Lines.FirstOrDefault(l =>
            string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));

        if (LineEditKey is not null && !string.Equals(LineEditKey, key, StringComparison.OrdinalIgnoreCase))
        {
            // The key was edited: remove the old line before adding the new one.
            var previous = EditModel.Lines.FirstOrDefault(l =>
                string.Equals(l.Key, LineEditKey, StringComparison.OrdinalIgnoreCase));
            if (previous is not null)
            {
                EditModel.Lines.Remove(previous);
            }

            existing = null;
        }

        if (existing is null)
        {
            if (EditModel.Lines.Any(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                ErrorMessage = $"Item {item} / UOM {uom} is already on this price list.";
                return;
            }

            EditModel.Lines.Add(new IvCustPriceLineVm
            {
                ICode = item,
                UOM = uom,
                SellingPrice = LinePrice,
                SellPackSize = LinePack
            });
        }
        else
        {
            existing.SellingPrice = LinePrice;
            existing.SellPackSize = LinePack;
        }

        ErrorMessage = null;
        ResetLineEditor();
    }

    protected void OnLineEdit(IvCustPriceLineVm line)
    {
        LineItem = line.ICode;
        LineUom = line.UOM;
        LinePrice = line.SellingPrice;
        LinePack = line.SellPackSize;
        LineEditKey = line.Key;
    }

    protected void OnLineRemove(IvCustPriceLineVm line)
    {
        EditModel.Lines.Remove(line);
        if (LineEditKey is not null && string.Equals(LineEditKey, line.Key, StringComparison.OrdinalIgnoreCase))
        {
            ResetLineEditor();
        }
    }

    protected void ResetLineEditor()
    {
        LineItem = null;
        LineUom = null;
        LinePrice = null;
        LinePack = null;
        LineEditKey = null;
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
            var result = await RefService.SaveCustPriceGroupAsync(EditModel, isNew: !IsEditMode);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode
                    ? "Price group updated successfully."
                    : "Price group added successfully.";
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

    private async Task<bool> LoadEditModelAsync(IvCustPriceGroupListRow row)
    {
        var result = await RefService.GetCustPriceGroupAsync(row.CustPriceCode);
        if (!result.Succeeded || result.Data is null)
        {
            StatusMessage = result.Message ?? "Unable to load price group.";
            return false;
        }

        EditModel = result.Data;
        ResetLineEditor();
        ErrorMessage = null;
        return true;
    }
}
