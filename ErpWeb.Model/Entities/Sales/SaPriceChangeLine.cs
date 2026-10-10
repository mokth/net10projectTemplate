namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// One immutable before/after price-semantic change. Descriptive fields are snapshots, not live-master
/// foreign keys, so later analysis remains historically accurate.
/// </summary>
public sealed class SaPriceChangeLine
{
    public long PriceChangeLineId { get; set; }
    public long PriceChangeBatchId { get; set; }
    public SaPriceChangeBatch PriceChangeBatch { get; set; } = null!;

    public string ChangeKind { get; set; } = string.Empty;

    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescriptionSnapshot { get; set; }
    public string? ItemTypeSnapshot { get; set; }
    public string? ItemClassSnapshot { get; set; }
    public string? ItemSubClassSnapshot { get; set; }
    public string? BrandSnapshot { get; set; }

    public string? CustCode { get; set; }
    public string? CustomerNameSnapshot { get; set; }
    public string? CustomerTypeSnapshot { get; set; }
    public string? CustomerGroupSnapshot { get; set; }

    public string? CustPriceCode { get; set; }
    public string? PriceListDescriptionSnapshot { get; set; }
    public int? SourcePriceListLineId { get; set; }

    public int? Moq { get; set; }

    public string? OldUom { get; set; }
    public string? NewUom { get; set; }
    public string? OldCurrencyCode { get; set; }
    public string? NewCurrencyCode { get; set; }

    public decimal? OldMinQty { get; set; }
    public decimal? OldMaxQty { get; set; }
    public decimal? NewMinQty { get; set; }
    public decimal? NewMaxQty { get; set; }

    public DateTime? OldValidFrom { get; set; }
    public DateTime? OldValidTo { get; set; }
    public DateTime? NewValidFrom { get; set; }
    public DateTime? NewValidTo { get; set; }

    public decimal? OldPrice { get; set; }
    public decimal? NewPrice { get; set; }
}
