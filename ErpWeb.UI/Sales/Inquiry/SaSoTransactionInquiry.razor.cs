using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Inquiry;

/// <summary>SO Transaction Inquiry — all revisions with persisted qty rollups; Outstanding unchanged.</summary>
public partial class SaSoTransactionInquiry : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaSoTransactionRow> DataSource { get; private set; } = default!;

    protected IReadOnlyList<StatusFilterOption> StatusOptions { get; } =
    [
        new(SaSoStatuses.New, "NEW"),
        new(SaSoStatuses.Shipped, "SHIPPED"),
        new(SaSoStatuses.Closed, "CLOSED"),
        new(SaSoStatuses.Superseded, "SUPERSEDED")
    ];

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> SoActionButtons =>
    [
        new() { Text = "VIEW", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "View this SO revision" },
        new() { Text = "QT", IConClass = "fa-solid fa-file-contract", ToolTip = "View related quotation" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "SO no.", FieldName = nameof(SaSoTransactionRow.SoNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Rev", FieldName = nameof(SaSoTransactionRow.Rev), DataType = "int", Width = "70px", VisibleIndex = 2 },
        new() { Caption = "Current", FieldName = nameof(SaSoTransactionRow.IsCurrent), Width = "80px", VisibleIndex = 3 },
        new() { Caption = "Date", FieldName = nameof(SaSoTransactionRow.SoDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Status", FieldName = nameof(SaSoTransactionRow.Status), Width = "110px", VisibleIndex = 5 },
        new() { Caption = "Fulfillment", FieldName = nameof(SaSoTransactionRow.FulfillmentStatus), Width = "110px", VisibleIndex = 6 },
        new() { Caption = "Billing", FieldName = nameof(SaSoTransactionRow.BillingStatus), Width = "90px", VisibleIndex = 7 },
        new() { Caption = "Customer", FieldName = nameof(SaSoTransactionRow.CustCode), Width = "110px", VisibleIndex = 8 },
        new() { Caption = "Name", FieldName = nameof(SaSoTransactionRow.CustName), VisibleIndex = 9 },
        new() { Caption = "Sales rep", FieldName = nameof(SaSoTransactionRow.SalesRep), Width = "100px", VisibleIndex = 10 },
        new() { Caption = "Amount", FieldName = nameof(SaSoTransactionRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "120px", VisibleIndex = 11 },
        new() { Caption = "QT no.", FieldName = nameof(SaSoTransactionRow.QtNo), Width = "120px", VisibleIndex = 12 },
        new() { Caption = "Line", FieldName = nameof(SaSoTransactionRow.Line), DataType = "int", Width = "70px", VisibleIndex = 13 },
        new() { Caption = "Item", FieldName = nameof(SaSoTransactionRow.ICode), Width = "120px", VisibleIndex = 14 },
        new() { Caption = "Description", FieldName = nameof(SaSoTransactionRow.IDesc), VisibleIndex = 15 },
        new() { Caption = "Ordered", FieldName = nameof(SaSoTransactionRow.OrderQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 16 },
        new() { Caption = "Delivered", FieldName = nameof(SaSoTransactionRow.DeliveredQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 17 },
        new() { Caption = "Invoiced", FieldName = nameof(SaSoTransactionRow.InvoicedQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 18 },
        new() { Caption = "Balance", FieldName = nameof(SaSoTransactionRow.BalanceQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 19 },
        new() { Caption = "Written off", FieldName = nameof(SaSoTransactionRow.WrittenOffQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 20 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new SaInquiryGridDataSource<SaSoTransactionRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<SaInquiryPage<SaSoTransactionRow>>> SearchPageAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetSoTransactionsAsync(MenuCodes.SalesSoTransactions, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<SaSoTransactionRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/inquiry/so-transactions/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<SaSoTransactionRow> info)
    {
        if (info.SelectedRow is not { } row)
        {
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "VIEW":
                TryOpenByDocType("SO", row.SoNo, row.Rev);
                break;
            case "QT":
                if (string.IsNullOrWhiteSpace(row.QtNo))
                {
                    NavMessage = SaInquiryNavigation.DocumentUnavailableMessage;
                }
                else
                {
                    TryOpenByDocType("QT", row.QtNo, row.QtCustRel);
                }

                break;
        }

        return Task.CompletedTask;
    }
}
