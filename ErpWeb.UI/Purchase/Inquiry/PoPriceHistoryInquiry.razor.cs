using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>Purchase Price History — POSTED INV lines; NetUnitPrice summaries; no FX/UOM conversion.</summary>
public partial class PoPriceHistoryInquiry : PoInquiryPageBase
{
    protected PoInquiryGridDataSource<PoPurchasePriceHistoryRow> DataSource { get; private set; } = default!;
    protected PoPurchasePriceHistorySummary Summary { get; private set; } = new();
    protected bool SummaryLoading { get; private set; }

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> ActionButtons =>
    [
        new() { Text = "OPEN INV", IConClass = "fa-solid fa-file-invoice", ToolTip = "Open purchase invoice" },
        new() { Text = "OPEN PO", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "Open purchase order" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "Date", FieldName = nameof(PoPurchasePriceHistoryRow.DocDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 1 },
        new() { Caption = "Invoice", FieldName = nameof(PoPurchasePriceHistoryRow.DocNo), Width = "130px", VisibleIndex = 2 },
        new() { Caption = "Supplier", FieldName = nameof(PoPurchasePriceHistoryRow.VendorCode), Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Name", FieldName = nameof(PoPurchasePriceHistoryRow.VendorName), VisibleIndex = 4 },
        new() { Caption = "Item", FieldName = nameof(PoPurchasePriceHistoryRow.ICode), Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Description", FieldName = nameof(PoPurchasePriceHistoryRow.IDesc), VisibleIndex = 6 },
        new() { Caption = "PO no.", FieldName = nameof(PoPurchasePriceHistoryRow.PoNo), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Qty", FieldName = nameof(PoPurchasePriceHistoryRow.Qty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 8 },
        new() { Caption = "UOM", FieldName = nameof(PoPurchasePriceHistoryRow.Uom), Width = "80px", VisibleIndex = 9 },
        new() { Caption = "Unit price", FieldName = nameof(PoPurchasePriceHistoryRow.UnitPrice), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 10 },
        new() { Caption = "Discount", FieldName = nameof(PoPurchasePriceHistoryRow.ItemDiscAmount), DataType = "decimal", DisplayFormat = "n2", Width = "100px", VisibleIndex = 11 },
        new() { Caption = "Net unit", FieldName = nameof(PoPurchasePriceHistoryRow.NetUnitPrice), DataType = "decimal", DisplayFormat = "n4", Width = "110px", VisibleIndex = 12 },
        new() { Caption = "Currency", FieldName = nameof(PoPurchasePriceHistoryRow.Currency), Width = "90px", VisibleIndex = 13 },
        new() { Caption = "Rate", FieldName = nameof(PoPurchasePriceHistoryRow.CurrRate), DataType = "decimal", DisplayFormat = "n6", Width = "90px", VisibleIndex = 14 },
        new() { Caption = "Local amount", FieldName = nameof(PoPurchasePriceHistoryRow.LocalAmount), DataType = "decimal", DisplayFormat = "n2", Width = "120px", VisibleIndex = 15 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new PoInquiryGridDataSource<PoPurchasePriceHistoryRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    protected override async Task ReloadAsync()
    {
        await base.ReloadAsync();
        await LoadSummaryAsync();
    }

    private async Task LoadSummaryAsync()
    {
        SummaryLoading = true;
        try
        {
            var result = await Inquiry.GetPurchasePriceHistorySummaryAsync(
                MenuCodes.PurchasePriceHistory, BuildQuery());
            Summary = result.Succeeded
                ? result.Data ?? new PoPurchasePriceHistorySummary()
                : new PoPurchasePriceHistorySummary();
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
            }
        }
        finally
        {
            SummaryLoading = false;
        }
    }

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

    private Task<IvMasterOperationResult<PoInquiryPage<PoPurchasePriceHistoryRow>>> SearchPageAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetPurchasePriceHistoryAsync(MenuCodes.PurchasePriceHistory, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<PoPurchasePriceHistoryRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/purchase/inquiry/price-history/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoPurchasePriceHistoryRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (info.SelectedRow is null)
        {
            return Task.CompletedTask;
        }

        if (mode == "OPEN INV")
        {
            if (PoInquiryNavigation.TryResolveInvoice(info.SelectedRow.DocNo, out var url))
            {
                OpenDocument(url);
            }
            else
            {
                NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
            }
        }
        else if (mode == "OPEN PO")
        {
            if (PoInquiryNavigation.TryResolvePo(info.SelectedRow.PoNo, info.SelectedRow.PoRelNo, out var url))
            {
                OpenDocument(url);
            }
            else
            {
                NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
            }
        }

        return Task.CompletedTask;
    }

    protected static string FormatPrice(decimal? value) =>
        value is decimal v ? v.ToString("N4") : "—";
}
