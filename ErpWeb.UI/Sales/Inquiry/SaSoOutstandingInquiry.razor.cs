using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Inquiry;

/// <summary>Sales Order outstanding — current SO lines with the persisted quantity rollups.</summary>
public partial class SaSoOutstandingInquiry : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaSoOutstandingRow> DataSource { get; private set; } = default!;

    /// <summary>
    /// Only the statuses a LIVE (<c>IsCurrent</c>) revision may hold — the page filters to current
    /// revisions, so <c>SUPERSEDED</c> can never match and is deliberately absent.
    /// </summary>
    protected IReadOnlyList<StatusFilterOption> StatusOptions { get; } =
    [
        new(SaSoStatuses.New, "NEW"),
        new(SaSoStatuses.Shipped, "SHIPPED"),
        new(SaSoStatuses.Closed, "CLOSED")
    ];

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "SO no.", FieldName = nameof(SaSoOutstandingRow.SoNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Rev", FieldName = nameof(SaSoOutstandingRow.Rev), DataType = "int", Width = "70px", VisibleIndex = 2 },
        new() { Caption = "Date", FieldName = nameof(SaSoOutstandingRow.SoDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Status", FieldName = nameof(SaSoOutstandingRow.Status), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Fulfillment", FieldName = nameof(SaSoOutstandingRow.FulfillmentStatus), Width = "110px", VisibleIndex = 5 },
        new() { Caption = "Billing", FieldName = nameof(SaSoOutstandingRow.BillingStatus), Width = "90px", VisibleIndex = 6 },
        new() { Caption = "Customer", FieldName = nameof(SaSoOutstandingRow.CustCode), Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Name", FieldName = nameof(SaSoOutstandingRow.CustName), VisibleIndex = 8 },
        new() { Caption = "Sales rep", FieldName = nameof(SaSoOutstandingRow.SalesRep), Width = "100px", VisibleIndex = 9 },
        new() { Caption = "Amount", FieldName = nameof(SaSoOutstandingRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "130px", VisibleIndex = 10 },
        new() { Caption = "Line", FieldName = nameof(SaSoOutstandingRow.Line), DataType = "int", Width = "70px", VisibleIndex = 11 },
        new() { Caption = "Item", FieldName = nameof(SaSoOutstandingRow.ICode), Width = "120px", VisibleIndex = 12 },
        new() { Caption = "Description", FieldName = nameof(SaSoOutstandingRow.IDesc), VisibleIndex = 13 },
        new() { Caption = "Ordered", FieldName = nameof(SaSoOutstandingRow.OrderQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 14 },
        new() { Caption = "Shipped", FieldName = nameof(SaSoOutstandingRow.ShippedQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 15 },
        new() { Caption = "Delivered", FieldName = nameof(SaSoOutstandingRow.DeliveredQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 16 },
        new() { Caption = "Invoiced", FieldName = nameof(SaSoOutstandingRow.InvoicedQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 17 },
        new() { Caption = "Balance", FieldName = nameof(SaSoOutstandingRow.BalanceQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 18 },
        new() { Caption = "Written off", FieldName = nameof(SaSoOutstandingRow.WrittenOffQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 19 },
        new() { Caption = "Delivery date", FieldName = nameof(SaSoOutstandingRow.DeliveryDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 20 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new SaInquiryGridDataSource<SaSoOutstandingRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<SaInquiryPage<SaSoOutstandingRow>>> SearchPageAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetSoOutstandingAsync(MenuCodes.SalesSoOutstanding, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<SaSoOutstandingRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/inquiry/so-outstanding/export", []), forceLoad: true);
                break;
        }
    }

    /// <summary>Opens the SO revision the row shows — never a different revision than displayed.</summary>
    protected Task OnActionClick(SelectedButtonInfo<SaSoOutstandingRow> info)
    {
        if (info.SelectedRow is { } row)
        {
            TryOpenByDocType("SO", row.SoNo, row.Rev);
        }

        return Task.CompletedTask;
    }
}
