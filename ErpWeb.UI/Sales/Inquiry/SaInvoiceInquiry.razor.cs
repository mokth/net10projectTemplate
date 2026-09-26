using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Inquiry;

/// <summary>Sales Invoice Inquiry — NEW/POSTED headers with separately decorated e-Invoice status.</summary>
public partial class SaInvoiceInquiry : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaInvoiceInquiryRow> DataSource { get; private set; } = default!;

    protected IReadOnlyList<StatusFilterOption> StatusOptions { get; } =
    [
        new(SaInvoiceStatuses.New, "NEW"),
        new(SaInvoiceStatuses.Posted, "POSTED")
    ];

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> InvoiceActionButtons =>
    [
        new() { Text = "VIEW", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "View invoice" },
        new() { Text = "SO", IConClass = "fa-solid fa-file-lines", ToolTip = "View related sales order" },
        new() { Text = "DO", IConClass = "fa-solid fa-truck", ToolTip = "View related delivery order" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "Invoice no.", FieldName = nameof(SaInvoiceInquiryRow.InvNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Date", FieldName = nameof(SaInvoiceInquiryRow.InvDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 2 },
        new() { Caption = "Customer", FieldName = nameof(SaInvoiceInquiryRow.CustCode), Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Name", FieldName = nameof(SaInvoiceInquiryRow.CustName), VisibleIndex = 4 },
        new() { Caption = "Salesman", FieldName = nameof(SaInvoiceInquiryRow.SalesmanCode), Width = "100px", VisibleIndex = 5 },
        new() { Caption = "Location", FieldName = nameof(SaInvoiceInquiryRow.LocationCode), Width = "100px", VisibleIndex = 6 },
        new() { Caption = "Reference", FieldName = nameof(SaInvoiceInquiryRow.PoNo), Width = "120px", VisibleIndex = 7 },
        new() { Caption = "Currency", FieldName = nameof(SaInvoiceInquiryRow.Currency), Width = "90px", VisibleIndex = 8 },
        new() { Caption = "Gross", FieldName = nameof(SaInvoiceInquiryRow.GrossAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "110px", VisibleIndex = 9 },
        new() { Caption = "Tax", FieldName = nameof(SaInvoiceInquiryRow.Taxes), DataType = "decimal", DisplayFormat = "n2", Width = "100px", VisibleIndex = 10 },
        new() { Caption = "Total", FieldName = nameof(SaInvoiceInquiryRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "120px", VisibleIndex = 11 },
        new() { Caption = "Status", FieldName = nameof(SaInvoiceInquiryRow.Status), Width = "90px", VisibleIndex = 12 },
        new() { Caption = "Due date", FieldName = nameof(SaInvoiceInquiryRow.DueDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 13 },
        new() { Caption = "e-Invoice", FieldName = nameof(SaInvoiceInquiryRow.EInvoiceStatusLabel), Width = "130px", VisibleIndex = 14 },
        new() { Caption = "Related SO", FieldName = nameof(SaInvoiceInquiryRow.RelatedSoNo), Width = "120px", VisibleIndex = 15 },
        new() { Caption = "Related DO", FieldName = nameof(SaInvoiceInquiryRow.RelatedDoNo), Width = "120px", VisibleIndex = 16 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new SaInquiryGridDataSource<SaInvoiceInquiryRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<SaInquiryPage<SaInvoiceInquiryRow>>> SearchPageAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetInvoiceInquiryAsync(MenuCodes.SalesInvoiceInquiry, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<SaInvoiceInquiryRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/inquiry/invoice/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<SaInvoiceInquiryRow> info)
    {
        if (info.SelectedRow is not { } row)
        {
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "VIEW":
                TryOpenByDocType("INV", row.InvNo);
                break;
            case "SO":
                if (string.IsNullOrWhiteSpace(row.RelatedSoNo))
                {
                    NavMessage = SaInquiryNavigation.DocumentUnavailableMessage;
                }
                else
                {
                    TryOpenByDocType("SO", row.RelatedSoNo, row.RelatedSoCustRel);
                }

                break;
            case "DO":
                if (string.IsNullOrWhiteSpace(row.RelatedDoNo))
                {
                    NavMessage = SaInquiryNavigation.DocumentUnavailableMessage;
                }
                else
                {
                    TryOpenByDocType("DO", row.RelatedDoNo);
                }

                break;
        }

        return Task.CompletedTask;
    }
}
