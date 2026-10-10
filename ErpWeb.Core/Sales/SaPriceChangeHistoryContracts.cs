namespace ErpWeb.Core.Sales;

public sealed class SaPriceChangeHistoryQuery
{
    public DateTime? ChangedDateFromUtc { get; set; }
    public DateTime? ChangedDateToUtc { get; set; }
    public DateTime? EffectiveDateFrom { get; set; }
    public DateTime? EffectiveDateTo { get; set; }

    public string? Origin { get; set; }
    public string? TargetType { get; set; }
    public string? ChangeKind { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemType { get; set; }
    public string? ItemClass { get; set; }
    public string? ItemSubClass { get; set; }
    public string? Brand { get; set; }
    public string? CustCode { get; set; }
    public string? CustType { get; set; }
    public string? CustGroup { get; set; }
    public string? CustPriceCode { get; set; }
    public string? ChangedBy { get; set; }
    public string? ReasonSearch { get; set; }

    public int Skip { get; set; }
    public int Take { get; set; } = SaPriceChangeHistoryLimits.DefaultPageSize;
}

public static class SaPriceChangeHistoryLimits
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxExportRows = 50_000;
}

public sealed class SaPriceChangeHistoryPage
{
    public IReadOnlyList<SaPriceChangeHistoryRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class SaPriceChangeHistoryRow
{
    public long PriceChangeLineId { get; init; }
    public long PriceChangeBatchId { get; init; }
    public string BatchReference { get; init; } = string.Empty;

    public DateTime ChangedAtUtc { get; init; }
    public DateTime EffectiveDate { get; init; }
    public string Origin { get; init; } = string.Empty;
    public string TargetType { get; init; } = string.Empty;
    public string ChangeKind { get; init; } = string.Empty;

    public string ItemCode { get; init; } = string.Empty;
    public string? ItemDescription { get; init; }
    public string? ItemType { get; init; }
    public string? ItemClass { get; init; }
    public string? ItemSubClass { get; init; }
    public string? Brand { get; init; }

    public string? Uom { get; init; }
    public string? CustCode { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerType { get; init; }
    public string? CustomerGroup { get; init; }
    public string? CustPriceCode { get; init; }
    public string? PriceListDescription { get; init; }
    public int? SourcePriceListLineId { get; init; }
    public int? Moq { get; init; }
    public string? CurrencyCode { get; init; }

    public decimal? OldPrice { get; init; }
    public decimal? NewPrice { get; init; }
    public decimal? DifferenceAmount { get; init; }
    public decimal? DifferencePercent { get; init; }
    public string? Reason { get; init; }
    public string ChangedBy { get; init; } = string.Empty;

    public string? AdjustmentMethod { get; init; }
    public decimal? AdjustmentValue { get; init; }
    public string? RoundingMode { get; init; }
    public int? DecimalPlaces { get; init; }
}

public sealed class SaPriceChangeHistoryBatch
{
    public long PriceChangeBatchId { get; init; }
    public string BatchReference { get; init; } = string.Empty;
    public DateTime ChangedAtUtc { get; init; }
    public DateTime EffectiveDate { get; init; }
    public string Origin { get; init; } = string.Empty;
    public string TargetType { get; init; } = string.Empty;
    public string? AdjustmentMethod { get; init; }
    public decimal? AdjustmentValue { get; init; }
    public string? RoundingMode { get; init; }
    public int? DecimalPlaces { get; init; }
    public string? Reason { get; init; }
    public string? ItemSearchFilter { get; init; }
    public string? ItemTypeFilter { get; init; }
    public string? ItemClassFilter { get; init; }
    public string? ItemSubClassFilter { get; init; }
    public string? BrandFilter { get; init; }
    public string? CustCodeFilter { get; init; }
    public string? CustTypeFilter { get; init; }
    public string? CustGroupFilter { get; init; }
    public string? CustPriceCodeFilter { get; init; }
    public string? CurrencyFilter { get; init; }
    public string? UomFilter { get; init; }
    public int ChangedRowCount { get; init; }
    public string ChangedBy { get; init; } = string.Empty;
    public IReadOnlyList<SaPriceChangeHistoryRow> Lines { get; init; } = [];
    public int TotalLineCount { get; init; }
}
