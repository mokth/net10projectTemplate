using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Inquiry;

/// <summary>Sales Price History — POSTED invoice lines; currency and UOM never normalized.</summary>
public partial class SaPriceHistoryInquiry : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaSalesPriceHistoryRow> DataSource { get; private set; } = default!;

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "Date", FieldName = nameof(SaSalesPriceHistoryRow.InvDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 1 },
        new() { Caption = "Invoice no.", FieldName = nameof(SaSalesPriceHistoryRow.InvNo), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "Customer", FieldName = nameof(SaSalesPriceHistoryRow.CustCode), Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Name", FieldName = nameof(SaSalesPriceHistoryRow.CustName), VisibleIndex = 4 },
        new() { Caption = "Item", FieldName = nameof(SaSalesPriceHistoryRow.ICode), Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Description", FieldName = nameof(SaSalesPriceHistoryRow.IDesc), VisibleIndex = 6 },
        new() { Caption = "Qty", FieldName = nameof(SaSalesPriceHistoryRow.Qty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 7 },
        new() { Caption = "UOM", FieldName = nameof(SaSalesPriceHistoryRow.Uom), Width = "80px", VisibleIndex = 8 },
        new() { Caption = "Unit price", FieldName = nameof(SaSalesPriceHistoryRow.UnitPrice), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 9 },
        new() { Caption = "Discount", FieldName = nameof(SaSalesPriceHistoryRow.ItemDiscAmount), DataType = "decimal", DisplayFormat = "n2", Width = "100px", VisibleIndex = 10 },
        new() { Caption = "Net amount", FieldName = nameof(SaSalesPriceHistoryRow.NetAmount), DataType = "decimal", DisplayFormat = "n2", Width = "120px", VisibleIndex = 11 },
        new() { Caption = "Net unit", FieldName = nameof(SaSalesPriceHistoryRow.NetUnitPrice), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 12 },
        new() { Caption = "Currency", FieldName = nameof(SaSalesPriceHistoryRow.Currency), Width = "90px", VisibleIndex = 13 },
        new() { Caption = "Salesman", FieldName = nameof(SaSalesPriceHistoryRow.SalesmanCode), Width = "100px", VisibleIndex = 14 },
        new() { Caption = "Warehouse", FieldName = nameof(SaSalesPriceHistoryRow.FrWarehouse), Width = "110px", VisibleIndex = 15 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new SaInquiryGridDataSource<SaSalesPriceHistoryRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    private void OnError(string? message) => ErrorMessage = message;

    private Task OnDraftItemCodeChangedAsync(string? code)
    {
        DraftItemCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        return Task.CompletedTask;
    }

    private Task OnDraftItemSelectedAsync(IvStockMasterLookupRow item)
    {
        DraftItemCode = item?.ICode;
        return Task.CompletedTask;
    }

    private Task<IvMasterOperationResult<SaInquiryPage<SaSalesPriceHistoryRow>>> SearchPageAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetSalesPriceHistoryAsync(MenuCodes.SalesPriceHistory, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<SaSalesPriceHistoryRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/inquiry/price-history/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<SaSalesPriceHistoryRow> info)
    {
        if (info.SelectedRow is { } row)
        {
            TryOpenByDocType("INV", row.InvNo);
        }

        return Task.CompletedTask;
    }
}
