using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using Microsoft.Extensions.Configuration;

namespace ErpWeb.Core.Purchase;

/// <summary>Read-only KPI projection composed from the existing, permission-gated inquiry contracts.</summary>
public sealed class PoProcurementKpiService : IPoProcurementKpiService
{
    private const int MaxMetricRows = 50_000;
    private readonly IPoPurchaseInquiryService _inquiry;
    private readonly IConfiguration _configuration;
    private readonly ICurrentDateService _dates;

    public PoProcurementKpiService(
        IPoPurchaseInquiryService inquiry,
        IConfiguration configuration,
        ICurrentDateService dates)
    {
        _inquiry = inquiry;
        _configuration = configuration;
        _dates = dates;
    }

    public async Task<IvMasterOperationResult<PoProcurementKpiSummary>> GetSummaryAsync(
        PoInquiryQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!_configuration.GetValue<bool>("Features:ProcurementKpi"))
        {
            return IvMasterOperationResult<PoProcurementKpiSummary>.Fail(
                IvMasterErrorCode.AccessDenied, "Procurement KPI is not enabled.");
        }

        var prepared = query ?? new PoInquiryQuery();
        var asOf = prepared.AsOfDate?.Date ?? _dates.Today.Date;
        var summary = new PoProcurementKpiSummary
        {
            AsOfDate = asOf,
            DateFrom = prepared.DateFrom?.Date,
            DateTo = prepared.DateTo?.Date
        };

        var outstanding = await _inquiry.GetPoOutstandingSummaryAsync(
            MenuCodes.PurchaseKpi, Clone(prepared), cancellationToken);
        if (outstanding.Succeeded && outstanding.Data is not null)
        {
            summary.OutstandingPoLines = outstanding.Data.OpenLineCount;
            summary.OverduePoLines = outstanding.Data.OverdueLineCount;
        }
        else
        {
            summary.OutstandingUnavailableReason = outstanding.Message ?? "PO outstanding inquiry is unavailable.";
            summary.OverdueUnavailableReason = summary.OutstandingUnavailableReason;
        }

        var matchingQuery = Clone(prepared);
        matchingQuery.WorkbenchPreset = PoInquiryWorkbenchPresets.ReceivedNotInvoiced;
        var matching = await _inquiry.GetPoMatchingSummaryAsync(MenuCodes.PurchaseKpi, matchingQuery, cancellationToken);
        if (matching.Succeeded && matching.Data is not null)
        {
            summary.ReceivedNotInvoicedLines = matching.Data.ReceivedNotInvoicedCount;
        }
        else
        {
            summary.ReceivedNotInvoicedUnavailableReason = matching.Message ?? "PO matching inquiry is unavailable.";
        }

        var deliveryQuery = Clone(prepared);
        deliveryQuery.WorkbenchPreset = PoInquiryWorkbenchPresets.All;
        deliveryQuery.Skip = 0;
        deliveryQuery.Take = MaxMetricRows;
        var delivery = await _inquiry.GetDeliveryPerformanceLinesAsync(
            MenuCodes.PurchaseKpi, deliveryQuery, cancellationToken);
        if (!delivery.Succeeded || delivery.Data is null)
        {
            summary.DeliveryUnavailableReason = delivery.Message ?? "Delivery performance inquiry is unavailable.";
        }
        else if (delivery.Data.TotalCount > delivery.Data.Rows.Count)
        {
            summary.DeliveryUnavailableReason = $"Period contains more than {MaxMetricRows:N0} PO lines; narrow the date range.";
        }
        else
        {
            var rows = delivery.Data.Rows;
            var completed = rows.Where(x => PoDeliveryPerformanceCalc.IsCompletedOtifStatus(x.DeliveryStatus)).ToList();
            var onTime = completed.Count(x =>
                string.Equals(x.DeliveryStatus, PoDeliveryPerformanceStatuses.Early, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.DeliveryStatus, PoDeliveryPerformanceStatuses.OnTime, StringComparison.OrdinalIgnoreCase));
            summary.OnTimeCompletedLines = onTime;
            summary.CompletedDeliveryLines = completed.Count;
            summary.OnTimePercentage = completed.Count > 0 ? (decimal)onTime / completed.Count * 100m : null;
            summary.MissingEtaLines = rows.Count(x => x.EtaDate is null);
            summary.PartialDeliveryLines = rows.Count(x =>
                string.Equals(x.DeliveryStatus, PoDeliveryPerformanceStatuses.PartiallyReceived, StringComparison.OrdinalIgnoreCase));
        }

        await LoadPriceMovementAsync(prepared, summary, cancellationToken);
        return IvMasterOperationResult<PoProcurementKpiSummary>.Ok(summary);
    }

    private async Task LoadPriceMovementAsync(
        PoInquiryQuery query,
        PoProcurementKpiSummary summary,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.ItemCode) || string.IsNullOrWhiteSpace(query.Currency))
        {
            summary.PriceUnavailableReason = "Select one item and one currency to compare posted invoice prices.";
            return;
        }

        var priceQuery = Clone(query);
        priceQuery.Skip = 0;
        priceQuery.Take = 2;
        var prices = await _inquiry.GetPurchasePriceHistoryAsync(MenuCodes.PurchaseKpi, priceQuery, cancellationToken);
        if (!prices.Succeeded || prices.Data is null)
        {
            summary.PriceUnavailableReason = prices.Message ?? "Posted purchase price history is unavailable.";
            return;
        }

        summary.PriceSampleCount = prices.Data.TotalCount;
        var rows = prices.Data.Rows;
        if (rows.Count < 2)
        {
            summary.PriceUnavailableReason = "At least two posted invoice lines are required for a comparison.";
            return;
        }

        var latest = rows[0];
        var previous = rows[1];
        var sameUom = !string.IsNullOrWhiteSpace(latest.Uom)
                      && string.Equals(latest.Uom, previous.Uom, StringComparison.OrdinalIgnoreCase);
        var sameCurrency = !string.IsNullOrWhiteSpace(latest.Currency)
                           && string.Equals(latest.Currency, previous.Currency, StringComparison.OrdinalIgnoreCase);
        if (!sameUom || !sameCurrency || latest.NetUnitPrice is null || previous.NetUnitPrice is null)
        {
            summary.PriceUnavailableReason = "The two latest posted lines do not share a verified purchase UOM and currency.";
            return;
        }

        summary.LatestNetUnitPrice = latest.NetUnitPrice;
        summary.PreviousNetUnitPrice = previous.NetUnitPrice;
        summary.LatestPriceDate = latest.DocDate;
        summary.PreviousPriceDate = previous.DocDate;
        summary.PriceUom = latest.Uom;
        summary.PriceCurrency = latest.Currency;
    }

    private static PoInquiryQuery Clone(PoInquiryQuery query) => new()
    {
        DateFrom = query.DateFrom,
        DateTo = query.DateTo,
        SuppCode = query.SuppCode,
        BuyerCode = query.BuyerCode,
        ItemCode = query.ItemCode,
        Currency = query.Currency,
        Warehouse = query.Warehouse,
        AsOfDate = query.AsOfDate,
        Status = query.Status,
        Type = query.Type,
        SearchText = query.SearchText,
        BranchCode = query.BranchCode,
        WorkbenchPreset = query.WorkbenchPreset,
        Skip = query.Skip,
        Take = query.Take
    };
}
