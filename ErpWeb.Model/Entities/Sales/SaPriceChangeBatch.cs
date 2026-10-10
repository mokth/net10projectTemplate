namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Append-only company-scoped evidence that one or more official sales-price masters changed.
/// The row deliberately has no relationship to live price masters: history must survive a later
/// item, customer, price-list, or price-line delete.
/// </summary>
public sealed class SaPriceChangeBatch
{
    public long PriceChangeBatchId { get; set; }
    public string CompanyCode { get; set; } = string.Empty;

    public string Origin { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public string? AdjustmentMethod { get; set; }
    public decimal? AdjustmentValue { get; set; }
    public string? RoundingMode { get; set; }
    public int? DecimalPlaces { get; set; }

    public DateTime EffectiveDate { get; set; }
    public string? Reason { get; set; }

    public string? ItemSearchFilter { get; set; }
    public string? ItemTypeFilter { get; set; }
    public string? ItemClassFilter { get; set; }
    public string? ItemSubClassFilter { get; set; }
    public string? BrandFilter { get; set; }

    public string? CustCodeFilter { get; set; }
    public string? CustTypeFilter { get; set; }
    public string? CustGroupFilter { get; set; }
    public string? CustPriceCodeFilter { get; set; }
    public string? CurrencyFilter { get; set; }
    public string? UomFilter { get; set; }

    public int ChangedRowCount { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public string ChangedBy { get; set; } = string.Empty;

    public ICollection<SaPriceChangeLine> Lines { get; set; } = new List<SaPriceChangeLine>();
}
