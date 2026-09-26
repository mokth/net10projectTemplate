using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Purchase.Inquiry;

/// <summary>Combined purchase credit/debit note inquiry with OPEN CDN and OPEN invoice.</summary>
public partial class PoCdnInquiry : PoInquiryPageBase
{
    protected PoInquiryGridDataSource<PoCdnInquiryRow> DataSource { get; private set; } = default!;

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<ButtonInfo> ActionButtons =>
    [
        new() { Text = "OPEN", IConClass = "fa-solid fa-up-right-from-square", ToolTip = "Open credit/debit note" },
        new() { Text = "OPEN INV", IConClass = "fa-solid fa-file-invoice", ToolTip = "Open linked invoice" }
    ];

    protected IReadOnlyList<IvCodeLookupRow> TypeOptions { get; set; } =
    [
        new IvCodeLookupRow { Code = PoCdnTypes.CreditNote, Desc = "Credit Note" },
        new IvCodeLookupRow { Code = PoCdnTypes.DebitNote, Desc = "Debit Note" }
    ];

    protected IReadOnlyList<StatusFilterOption> StatusOptions { get; } =
    [
        new(PoCdnStatuses.New, "NEW"),
        new(PoCdnStatuses.Posted, "POSTED")
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "Doc no.", FieldName = nameof(PoCdnInquiryRow.DocNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Date", FieldName = nameof(PoCdnInquiryRow.DocDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 2 },
        new() { Caption = "Type", FieldName = nameof(PoCdnInquiryRow.Type), Width = "70px", VisibleIndex = 3 },
        new() { Caption = "Status", FieldName = nameof(PoCdnInquiryRow.Status), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Invoice no.", FieldName = nameof(PoCdnInquiryRow.InvNo), Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Supplier", FieldName = nameof(PoCdnInquiryRow.VendorCode), Width = "110px", VisibleIndex = 6 },
        new() { Caption = "Name", FieldName = nameof(PoCdnInquiryRow.VendorName), VisibleIndex = 7 },
        new() { Caption = "Amount", FieldName = nameof(PoCdnInquiryRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "130px", VisibleIndex = 8 },
        new() { Caption = "Return stock", FieldName = nameof(PoCdnInquiryRow.ReturnStock), DataType = "bool", Width = "110px", VisibleIndex = 9 },
        new() { Caption = "VR batch", FieldName = nameof(PoCdnInquiryRow.VrBatchNo), Width = "120px", VisibleIndex = 10 },
        new() { Caption = "Tax group", FieldName = nameof(PoCdnInquiryRow.TaxGrCode), Width = "100px", VisibleIndex = 11 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new PoInquiryGridDataSource<PoCdnInquiryRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<PoInquiryPage<PoCdnInquiryRow>>> SearchPageAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetCdnInquiryAsync(MenuCodes.PurchaseCdnInquiry, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<PoCdnInquiryRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/purchase/inquiry/cdn/export", []), forceLoad: true);
                break;
        }
    }

    protected Task OnActionClick(SelectedButtonInfo<PoCdnInquiryRow> info)
    {
        if (info.SelectedRow is null)
        {
            return Task.CompletedTask;
        }

        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        if (mode == "OPEN")
        {
            if (PoInquiryNavigation.TryResolveCdn(info.SelectedRow.DocNo, info.SelectedRow.Type, out var url))
            {
                OpenDocument(url);
            }
            else
            {
                NavMessage = PoInquiryNavigation.DocumentUnavailableMessage;
            }
        }
        else if (mode == "OPEN INV")
        {
            if (PoInquiryNavigation.TryResolveInvoice(info.SelectedRow.InvNo, out var url))
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
}
