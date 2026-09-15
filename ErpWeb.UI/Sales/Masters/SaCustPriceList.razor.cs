using DevExpress.Blazor;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Admin.Master;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Masters;

/// <summary>
/// Customer Prices — the item prices of one price group, read-only.
///
/// This is the line <em>listing</em> that the legacy <c>CustPriceGroupItemsView</c> provided. Editing a
/// line belongs to the price group aggregate (header + lines save in one transaction), so the write
/// actions here redirect the operator to the Price Groups screen instead of pretending to be an editor.
/// </summary>
public partial class SaCustPriceList : SaRefListPageBase<IvCustPriceListRow>
{
    protected override string MenuCode => MenuCodes.SalesCustPrice;
    protected override string EntityLabel => "Price Line";
    protected override string? ExportRoute => "/sales/customer-prices/export";

    protected List<string> PriceGroupOptions { get; set; } = [];
    protected string? SelectedPriceGroup { get; set; }
    protected bool CanViewPrice { get; set; }

    public List<GridColumnData> Columns()
    {
        var columns = new List<GridColumnData>
        {
            new()
            {
                Caption = "Item",
                FieldName = nameof(IvCustPriceListRow.ICode),
                DataType = "string",
                SortIndex = 0,
                SortOrder = GridColumnSortOrder.Ascending,
                VisibleIndex = 1,
                Width = "150px"
            },
            new()
            {
                Caption = "Description",
                FieldName = nameof(IvCustPriceListRow.IDesc),
                DataType = "string",
                VisibleIndex = 2
            },
            new()
            {
                Caption = "UOM",
                FieldName = nameof(IvCustPriceListRow.UOM),
                DataType = "string",
                VisibleIndex = 3,
                Width = "90px"
            }
        };

        if (CanViewPrice)
        {
            columns.Add(new GridColumnData
            {
                Caption = "Selling price",
                FieldName = nameof(IvCustPriceListRow.SellingPrice),
                DataType = "number",
                VisibleIndex = 4,
                Width = "130px"
            });
        }

        columns.Add(new GridColumnData
        {
            Caption = "Pack size",
            FieldName = nameof(IvCustPriceListRow.SellPackSize),
            DataType = "number",
            VisibleIndex = 5,
            Width = "110px"
        });

        return columns;
    }

    protected override async Task OnPageInitializedAsync()
    {
        CanViewPrice = await AccessRights.CanAsync(MenuCode, PermissionCodes.ViewPrice);

        var groups = await RefService.ListCustPriceGroupsAsync();
        PriceGroupOptions = groups.Succeeded
            ? (groups.Data ?? []).Select(g => g.CustPriceCode).ToList()
            : [];

        SelectedPriceGroup = PriceGroupOptions.FirstOrDefault();
        await ReloadListAsync();
    }

    protected async Task OnPriceGroupChanged(string? value)
    {
        SelectedPriceGroup = value;
        await ReloadListAsync();
    }

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            if (string.IsNullOrWhiteSpace(SelectedPriceGroup))
            {
                Data = [];
                Grid?.Reload();
                return;
            }

            var result = await RefService.ListCustPricesAsync(SelectedPriceGroup);
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load item prices.";
                Data = [];
            }
            else
            {
                Data = result.Data ?? [];
            }

            Grid?.Reload();
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected override IvMasterKeyToken ToKeyToken(IvCustPriceListRow row) =>
        Key(row.Key, []);

    protected override Task<IvMasterOperationResult<object>> SetActiveCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive) =>
        Task.FromResult(IvMasterOperationResult<object>.Fail(
            IvMasterErrorCode.Validation,
            "Item prices have no Active flag; deactivate the price group instead."));

    protected override Task<DeleteCheckResult> CanDeleteCoreAsync(IReadOnlyList<IvCustPriceListRow> rows) =>
        Task.FromResult(DeleteCheckResult.Blocked(
            "Item prices are removed from their price group. Open Price Groups to change the list.",
            []));

    protected override Task<IvMasterOperationResult<object>> DeleteCoreAsync(
        IReadOnlyList<IvMasterKeyToken> items) =>
        Task.FromResult(IvMasterOperationResult<object>.Fail(
            IvMasterErrorCode.Validation,
            "Item prices are removed from their price group. Open Price Groups to change the list."));

    protected override Task OnNewClickAsync()
    {
        StatusMessage = "Open Price Groups to add an item price to a list.";
        return Task.CompletedTask;
    }

    protected override Task OnViewClickAsync(IvCustPriceListRow row)
    {
        StatusMessage = "Open Price Groups to view or edit this item price.";
        return Task.CompletedTask;
    }

    protected override Task OnEditClickAsync(IvCustPriceListRow row)
    {
        StatusMessage = "Open Price Groups to edit this item price.";
        return Task.CompletedTask;
    }
}
