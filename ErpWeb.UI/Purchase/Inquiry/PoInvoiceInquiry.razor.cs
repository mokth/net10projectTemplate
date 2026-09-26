using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>Purchase invoice inquiry — INV and CN with OPEN.</summary>
public partial class PoInvoiceInquiry : PoInquiryPageBase
{
    protected PoInquiryGridDataSource<PoInvoiceInquiryRow> DataSource { get; private set; } = default!;

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> ActionButtons =>
    [
        new() { Text = "OPEN", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "Open invoice" }
    ];

    protected IReadOnlyList<IvCodeLookupRow> TypeOptions { get; set; } =
    [
        new IvCodeLookupRow { Code = PoInvoiceTypes.Invoice, Desc = "Invoice" },
        new IvCodeLookupRow { Code = PoInvoiceTypes.CreditNote, Desc = "Credit Note" }
    ];

    protected IReadOnlyList<StatusFilterOption> StatusOptions { get; } =
    [
        new(PoInvoiceStatuses.New, "NEW"),
        new(PoInvoiceStatuses.Posted, "POSTED")
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "Doc no.", FieldName = nameof(PoInvoiceInquiryRow.DocNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Date", FieldName = nameof(PoInvoiceInquiryRow.DocDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 2 },
        new() { Caption = "Type", FieldName = nameof(PoInvoiceInquiryRow.Type), Width = "70px", VisibleIndex = 3 },
        new() { Caption = "Status", FieldName = nameof(PoInvoiceInquiryRow.Status), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Supplier", FieldName = nameof(PoInvoiceInquiryRow.VendorCode), Width = "110px", VisibleIndex = 5 },
        new() { Caption = "Name", FieldName = nameof(PoInvoiceInquiryRow.VendorName), VisibleIndex = 6 },
        new() { Caption = "Amount", FieldName = nameof(PoInvoiceInquiryRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "130px", VisibleIndex = 7 },
        new() { Caption = "Tax", FieldName = nameof(PoInvoiceInquiryRow.Taxes), DataType = "decimal", DisplayFormat = "n2", Width = "110px", VisibleIndex = 8 },
        new() { Caption = "Currency", FieldName = nameof(PoInvoiceInquiryRow.Currency), Width = "90px", VisibleIndex = 9 },
        new() { Caption = "External doc", FieldName = nameof(PoInvoiceInquiryRow.ExternalDocNo), Width = "130px", VisibleIndex = 10 },
        new() { Caption = "Location", FieldName = nameof(PoInvoiceInquiryRow.LocationCode), Width = "100px", VisibleIndex = 11 },
        new() { Caption = "Posted", FieldName = nameof(PoInvoiceInquiryRow.PostedDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 12 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new PoInquiryGridDataSource<PoInvoiceInquiryRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<PoInquiryPage<PoInvoiceInquiryRow>>> SearchPageAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetInvoiceInquiryAsync(MenuCodes.PurchaseInvoiceInquiry, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<PoInvoiceInquiryRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/purchase/inquiry/invoices/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoInvoiceInquiryRow> info)
    {
        if (!string.Equals(info.SelectedButton.Text, "OPEN", StringComparison.OrdinalIgnoreCase)
            || info.SelectedRow is null)
        {
            return Task.CompletedTask;
        }

        if (PoInquiryNavigation.TryResolveInvoice(info.SelectedRow.DocNo, out var url))
        {
            OpenDocument(url);
        }
        else
        {
            NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
        }

        return Task.CompletedTask;
    }
}
