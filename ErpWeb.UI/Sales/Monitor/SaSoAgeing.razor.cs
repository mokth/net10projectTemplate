using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Sales.Inquiry;

namespace ErpWeb.UI.Sales.Monitor;

/// <summary>
/// A1 — Open Sales Order ageing and overdue delivery (plan-salesDecisionSupport.prompt.md).
///
/// <para>
/// The three ageing columns answer three different questions and the grid keeps them visibly apart:
/// <b>SO Age</b> is the age of the order, <b>Delivery Due Date</b> is the line's expected delivery, and
/// <b>Overdue Days</b> is derived from the delivery date alone. A delivery due today is not overdue.
/// </para>
/// </summary>
public partial class SaSoAgeing : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaSoAgeingRow> DataSource { get; private set; } = default!;

    protected SaSoAgeingSummary Summary { get; private set; } = new();
    protected bool SummaryLoading { get; private set; }

    protected override string MenuCode => MenuCodes.SalesSoAgeing;

    protected IReadOnlyList<StatusFilterOption> StatusOptions { get; } =
    [
        new(SaSoStatuses.New, "NEW"),
        new(SaSoStatuses.Shipped, "SHIPPED"),
        new(SaSoStatuses.Closed, "CLOSED")
    ];

    /// <summary>Ageing buckets — monitoring vocabulary from one place, never re-spelled here.</summary>
    protected IReadOnlyList<string> BucketOptions => SaMonitorBuckets.AgeBuckets;

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "SO no.", FieldName = nameof(SaSoAgeingRow.SoNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Rev", FieldName = nameof(SaSoAgeingRow.Rev), DataType = "int", Width = "70px", VisibleIndex = 2 },
        new() { Caption = "SO date", FieldName = nameof(SaSoAgeingRow.SoDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 3 },
        new() { Caption = "SO Age (days)", FieldName = nameof(SaSoAgeingRow.AgeDays), DataType = "int", Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending, VisibleIndex = 4 },
        new() { Caption = "Age bucket", FieldName = nameof(SaSoAgeingRow.AgeBucket), Width = "100px", VisibleIndex = 5 },
        new() { Caption = "Status", FieldName = nameof(SaSoAgeingRow.Status), Width = "100px", VisibleIndex = 6 },
        new() { Caption = "Fulfillment", FieldName = nameof(SaSoAgeingRow.FulfillmentStatus), Width = "110px", VisibleIndex = 7 },
        new() { Caption = "Billing", FieldName = nameof(SaSoAgeingRow.BillingStatus), Width = "90px", VisibleIndex = 8 },
        new() { Caption = "Customer", FieldName = nameof(SaSoAgeingRow.CustCode), Width = "110px", VisibleIndex = 9 },
        new() { Caption = "Name", FieldName = nameof(SaSoAgeingRow.CustName), VisibleIndex = 10 },
        new() { Caption = "Sales rep", FieldName = nameof(SaSoAgeingRow.SalesRep), Width = "100px", VisibleIndex = 11 },
        new() { Caption = "Amount", FieldName = nameof(SaSoAgeingRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "130px", VisibleIndex = 12 },
        new() { Caption = "Line", FieldName = nameof(SaSoAgeingRow.Line), DataType = "int", Width = "70px", VisibleIndex = 13 },
        new() { Caption = "Item", FieldName = nameof(SaSoAgeingRow.ICode), Width = "120px", VisibleIndex = 14 },
        new() { Caption = "Description", FieldName = nameof(SaSoAgeingRow.IDesc), VisibleIndex = 15 },
        new() { Caption = "Ordered", FieldName = nameof(SaSoAgeingRow.OrderQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 16 },
        new() { Caption = "Delivered", FieldName = nameof(SaSoAgeingRow.DeliveredQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 17 },
        new() { Caption = "Invoiced", FieldName = nameof(SaSoAgeingRow.InvoicedQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 18 },
        new() { Caption = "Balance", FieldName = nameof(SaSoAgeingRow.BalanceQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 19 },
        new() { Caption = "Written off", FieldName = nameof(SaSoAgeingRow.WrittenOffQty), DataType = "decimal", DisplayFormat = "n4", Width = "100px", VisibleIndex = 20 },
        new() { Caption = "Delivery Due Date", FieldName = nameof(SaSoAgeingRow.DeliveryDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "130px", VisibleIndex = 21 },
        new() { Caption = "Overdue", FieldName = nameof(SaSoAgeingRow.IsOverdueDelivery), DataType = "bool", Width = "90px", VisibleIndex = 22 },
        new() { Caption = "Overdue Days", FieldName = nameof(SaSoAgeingRow.OverdueDays), DataType = "int", Width = "110px", VisibleIndex = 23 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new SaInquiryGridDataSource<SaSoAgeingRow>(SearchPageAsync, OnError);
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
            // The strip is not paged, but it is the same filter — a chip must never describe a different
            // population than the grid below it.
            var summaryQuery = BuildQuery();
            summaryQuery.Skip = 0;
            summaryQuery.Take = 0;

            var result = await Inquiry.GetSoAgeingSummaryAsync(MenuCode, summaryQuery);
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                Summary = new SaSoAgeingSummary();
            }
            else
            {
                Summary = result.Data ?? new SaSoAgeingSummary();
            }
        }
        finally
        {
            SummaryLoading = false;
        }
    }

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<SaInquiryPage<SaSoAgeingRow>>> SearchPageAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetSoAgeingAsync(MenuCode, query, cancellationToken);

    protected Task OnBucketClick(string? bucket)
    {
        Bucket = string.Equals(Bucket, bucket, StringComparison.Ordinal) ? null : bucket;
        return ReloadAsync();
    }

    protected string BucketChipClass(string bucket) =>
        string.Equals(Bucket, bucket, StringComparison.Ordinal) ? "iv-chip iv-chip--active" : "iv-chip";

    protected Task OnOverdueChipClick()
    {
        OverdueOnly = !OverdueOnly;
        return ReloadAsync();
    }

    protected async Task OnButtonClick(SelectedButtonInfo<SaSoAgeingRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/monitor/so-ageing/export", []), forceLoad: true);
                break;
        }
    }

    /// <summary>Opens the SO revision the row shows — never a different revision than displayed.</summary>
    protected Task OnActionClick(SelectedButtonInfo<SaSoAgeingRow> info)
    {
        if (info.SelectedRow is { } row)
        {
            TryOpenByDocType("SO", row.SoNo, row.Rev);
        }

        return Task.CompletedTask;
    }
}
