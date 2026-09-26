using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Sales.Inquiry;

namespace ErpWeb.UI.Sales.Monitor;

/// <summary>
/// A3 — Quotation expiry watch (plan-salesDecisionSupport.prompt.md).
///
/// <para>
/// This is a <b>watchlist</b>, not an exception list. <c>EXPIRED</c> is a normal state of the quotation
/// lifecycle — the lazy sweep moves <c>NEW</c>/<c>SENT</c> there — and <c>ACCEPTED</c> is never
/// auto-expired, so an accepted quotation past its date is deliberately not flagged.
/// </para>
/// </summary>
public partial class SaQtExpiry : SaInquiryPageBase
{
    protected SaInquiryGridDataSource<SaQtExpiryRow> DataSource { get; private set; } = default!;

    protected SaQtExpirySummary Summary { get; private set; } = new();
    protected bool SummaryLoading { get; private set; }

    protected override string MenuCode => MenuCodes.SalesQtExpiry;

    protected IReadOnlyList<StatusFilterOption> StatusOptions { get; } =
    [
        new(SaQtStatuses.New, "NEW"),
        new(SaQtStatuses.Sent, "SENT"),
        new(SaQtStatuses.Accepted, "ACCEPTED"),
        new(SaQtStatuses.Expired, "EXPIRED"),
        new(SaQtStatuses.Cancelled, "CANCELLED"),
        new(SaQtStatuses.Lost, "LOST"),
        new(SaQtStatuses.Closed, "CLOSED")
    ];

    protected IReadOnlyList<string> BucketOptions => SaMonitorBuckets.ExpiryBuckets;

    protected List<ButtonInfo> Buttons =>
    [
        new() { Text = "REFRESH", IConClass = "fa-solid fa-rotate", Style = "secondary" },
        new() { Text = "EXPORT", IConClass = "fa-solid fa-file-csv", Style = "primary" }
    ];

    protected List<GridColumnData> Columns =>
    [
        new() { Caption = "QT no.", FieldName = nameof(SaQtExpiryRow.QtNo), Width = "130px", VisibleIndex = 1 },
        new() { Caption = "Rev", FieldName = nameof(SaQtExpiryRow.Rev), DataType = "int", Width = "70px", VisibleIndex = 2 },
        new() { Caption = "QT date", FieldName = nameof(SaQtExpiryRow.QtDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 3 },
        new() { Caption = "Valid until", FieldName = nameof(SaQtExpiryRow.ValidUntil), DataType = "date", DisplayFormat = "dd/MM/yyyy", Width = "110px", VisibleIndex = 4 },
        new() { Caption = "Days to expiry", FieldName = nameof(SaQtExpiryRow.DaysToExpiry), DataType = "int", Width = "120px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Ascending, VisibleIndex = 5 },
        new() { Caption = "Expiry bucket", FieldName = nameof(SaQtExpiryRow.ExpiryBucket), Width = "110px", VisibleIndex = 6 },
        new() { Caption = "Expired", FieldName = nameof(SaQtExpiryRow.IsExpired), DataType = "bool", Width = "90px", VisibleIndex = 7 },
        new() { Caption = "Expiring soon", FieldName = nameof(SaQtExpiryRow.IsExpiringSoon), DataType = "bool", Width = "110px", VisibleIndex = 8 },
        new() { Caption = "Status", FieldName = nameof(SaQtExpiryRow.Status), Width = "110px", VisibleIndex = 9 },
        new() { Caption = "Conversion", FieldName = nameof(SaQtExpiryRow.ConversionStatus), Width = "110px", VisibleIndex = 10 },
        new() { Caption = "Customer", FieldName = nameof(SaQtExpiryRow.CustCode), Width = "110px", VisibleIndex = 11 },
        new() { Caption = "Name", FieldName = nameof(SaQtExpiryRow.CustName), VisibleIndex = 12 },
        new() { Caption = "Sales rep", FieldName = nameof(SaQtExpiryRow.SalesRep), Width = "100px", VisibleIndex = 13 },
        new() { Caption = "Amount", FieldName = nameof(SaQtExpiryRow.TotAmnt), DataType = "decimal", DisplayFormat = "n2", Width = "130px", VisibleIndex = 14 }
    ];

    protected override async Task OnInquiryInitializedAsync()
    {
        DataSource = new SaInquiryGridDataSource<SaQtExpiryRow>(SearchPageAsync, OnError);
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
            var summaryQuery = BuildQuery();
            summaryQuery.Skip = 0;
            summaryQuery.Take = 0;

            var result = await Inquiry.GetQtExpirySummaryAsync(MenuCode, summaryQuery);
            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                Summary = new SaQtExpirySummary();
            }
            else
            {
                Summary = result.Data ?? new SaQtExpirySummary();
            }
        }
        finally
        {
            SummaryLoading = false;
        }
    }

    private void OnError(string? message) => ErrorMessage = message;

    private Task<IvMasterOperationResult<SaInquiryPage<SaQtExpiryRow>>> SearchPageAsync(
        SaInquiryQuery query,
        CancellationToken cancellationToken) =>
        Inquiry.GetQtExpiryAsync(MenuCode, query, cancellationToken);

    protected Task OnBucketClick(string? bucket)
    {
        Bucket = string.Equals(Bucket, bucket, StringComparison.Ordinal) ? null : bucket;
        return ReloadAsync();
    }

    protected string BucketChipClass(string bucket) =>
        string.Equals(Bucket, bucket, StringComparison.Ordinal) ? "iv-chip iv-chip--active" : "iv-chip";

    protected Task OnExpiringSoonClick()
    {
        ExpiringSoonOnly = !ExpiringSoonOnly;
        if (ExpiringSoonOnly)
        {
            ExpiredOnly = false;
        }

        return ReloadAsync();
    }

    protected Task OnExpiredClick()
    {
        ExpiredOnly = !ExpiredOnly;
        if (ExpiredOnly)
        {
            ExpiringSoonOnly = false;
        }

        return ReloadAsync();
    }

    protected async Task OnButtonClick(SelectedButtonInfo<SaQtExpiryRow> info)
    {
        var mode = (info.SelectedButton.Text ?? string.Empty).ToUpperInvariant();
        switch (mode)
        {
            case "REFRESH":
                await ReloadAsync();
                break;
            case "EXPORT":
                Navigation.NavigateTo(BuildExportUrl("/sales/monitor/quotation-expiry/export", []), forceLoad: true);
                break;
        }
    }

    /// <summary>Opens the quotation revision the row shows — never a different revision than displayed.</summary>
    protected Task OnActionClick(SelectedButtonInfo<SaQtExpiryRow> info)
    {
        if (info.SelectedRow is { } row)
        {
            TryOpenByDocType("QT", row.QtNo, row.Rev);
        }

        return Task.CompletedTask;
    }
}
