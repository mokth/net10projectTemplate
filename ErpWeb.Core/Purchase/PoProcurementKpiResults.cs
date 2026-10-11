namespace ErpWeb.Core.Purchase;

public sealed class PoProcurementKpiSummary
{
    public DateTime AsOfDate { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public int? OutstandingPoLines { get; set; }
    public int? OverduePoLines { get; set; }
    public int? ReceivedNotInvoicedLines { get; set; }
    public int? OnTimeCompletedLines { get; set; }
    public int? CompletedDeliveryLines { get; set; }
    public decimal? OnTimePercentage { get; set; }
    public int? MissingEtaLines { get; set; }
    public int? PartialDeliveryLines { get; set; }
    public string? DeliveryUnavailableReason { get; set; }
    public decimal? LatestNetUnitPrice { get; set; }
    public decimal? PreviousNetUnitPrice { get; set; }
    public DateTime? LatestPriceDate { get; set; }
    public DateTime? PreviousPriceDate { get; set; }
    public string? PriceUom { get; set; }
    public string? PriceCurrency { get; set; }
    public string? PriceUnavailableReason { get; set; }
    public int? PriceSampleCount { get; set; }
    public string? OutstandingUnavailableReason { get; set; }
    public string? OverdueUnavailableReason { get; set; }
    public string? ReceivedNotInvoicedUnavailableReason { get; set; }
}
