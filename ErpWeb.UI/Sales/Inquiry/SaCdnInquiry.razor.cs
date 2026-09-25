using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;

namespace ErpWeb.UI.Sales.Inquiry;

/// <summary>Combined credit/debit note inquiry, both types with a Type filter.</summary>
public partial class SaCdnInquiry : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaCdnInquiryRow> DataSource { get; private set; } = default!;

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected IReadOnlyList<IvCodeLookupRow> TypeOptions { get; set; } =
    [
        new IvCodeLookupRow { Code = SaCdnTypes.CreditNote, Desc = "Credit Note" },
        new IvCodeLookupRow { Code = SaCdnTypes.DebitNote, Desc = "Debit Note" }
    ];

    protected IReadOnlyList<StatusFilterOption> StatusOptions { get; } =
    [
        new(SaCdnStatuses.New, "NEW"),
        new(SaCdnStatuses.Posted, "POSTED")
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "Doc no.", FieldName = nameof(SaCdnInquiryRow.DocNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Date", FieldName = nameof(SaCdnInquiryRow.DocDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 2 },
        new() { Caption = "Type", FieldName = nameof(SaCdnInquiryRow.Type), Width = "70px", VisibleIndex = 3 },
        new() { Caption = "Status", FieldName = nameof(SaCdnInquiryRow.Status), Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Invoice no.", FieldName = nameof(SaCdnInquiryRow.InvNo), Width = "120px", VisibleIndex = 5 },
        new() { Caption = "Customer", FieldName = nameof(SaCdnInquiryRow.CustCode), Width = "110px", VisibleIndex = 6 },
        new() { Caption = "Name", FieldName = nameof(SaCdnInquiryRow.CustName), VisibleIndex = 7 },
        new() { Caption = "Sales rep", FieldName = nameof(SaCdnInquiryRow.SalesRep), Width = "100px", VisibleIndex = 8 },
        new() { Caption = "Reference", FieldName = nameof(SaCdnInquiryRow.RefNo), Width = "110px", VisibleIndex = 9 },
        new() { Caption = "Remarks", FieldName = nameof(SaCdnInquiryRow.Remarks), VisibleIndex = 10 },
        new() { Caption = "Amount", FieldName = nameof(SaCdnInquiryRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "130px", VisibleIndex = 11 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new SaInquiryGridDataSource<SaCdnInquiryRow>(SearchPageAsync, OnError);
        await LoadCommonLookupsAsync();
        await ReloadAsync();
        IsBootstrapping = false;
    }

    protected override void ApplyFiltersToGrids() => DataSource.UpdateFilters(AppliedQuery);

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<SaInquiryPage<SaCdnInquiryRow>>> SearchPageAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetCdnInquiryAsync(MenuCodes.SalesCdnInquiry, query, cancellationToken);

    protected async Task OnButtonClick(SelectedButtonInfo<SaCdnInquiryRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/inquiry/cdn/export", []), forceLoad: true);
                break;
        }
    }
}
