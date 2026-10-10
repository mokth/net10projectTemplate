namespace ErpWeb.UI.Sales.Transactions;

/// <summary>
/// Immutable presentation snapshot of the price information already recorded on one Sales line.
/// It deliberately contains no entity or mutable line-model references.
/// </summary>
public sealed class SaPriceInfoContext
{
    public string DocumentType { get; init; } = string.Empty;
    public string? DocumentNo { get; init; }
    public int? LineNo { get; init; }

    public string CustCode { get; init; } = string.Empty;
    public string ItemCode { get; init; } = string.Empty;
    public string? ItemDescription { get; init; }
    public string Uom { get; init; } = string.Empty;
    public decimal Qty { get; init; }
    public DateTime DocDate { get; init; }
    public string? Currency { get; init; }

    public decimal UnitPrice { get; init; }
    public string? PricingSource { get; init; }
    public string? PricingRef { get; init; }
    public decimal? OriginalUnitPrice { get; init; }
    public string? OverrideReason { get; init; }

    public bool IsInclusive { get; init; }
    public decimal TaxPercent { get; init; }

    public string? SourceDocumentType { get; init; }
    public string? SourceDocumentNo { get; init; }
    public int? SourceDocumentLine { get; init; }
}
