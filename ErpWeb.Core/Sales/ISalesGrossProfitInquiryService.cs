namespace ErpWeb.Core.Sales;

/// <summary>
/// Stage 5 gross-profit read model. COGS is resolved only from the exact valuation owner and
/// source line; unresolved stock lines remain visible but are excluded from final totals.
/// </summary>
public interface ISalesGrossProfitInquiryService
{
    Task<SalesGrossProfitInquiryPage> SearchAsync(
        SalesGrossProfitInquiryQuery? query = null,
        CancellationToken cancellationToken = default);
}

public sealed class SalesGrossProfitInquiryQuery
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? InvoiceNo { get; set; }
    public string? CustomerCode { get; set; }
    public string? ItemCode { get; set; }
    public bool IncludeUnresolvedRows { get; set; } = true;
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

public sealed class SalesGrossProfitInquiryPage
{
    public IReadOnlyList<SalesGrossProfitInquiryRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
    public SalesGrossProfitInquiryTotals Totals { get; init; } = new();
}

public sealed class SalesGrossProfitInquiryTotals
{
    public int ResolvedLineCount { get; init; }
    public int UnresolvedLineCount { get; init; }
    public decimal Qty { get; init; }
    public decimal NetSales { get; init; }
    public decimal Cogs { get; init; }
    public decimal GrossProfit { get; init; }
    public decimal? GrossMarginPercent { get; init; }
}

public sealed class SalesGrossProfitInquiryRow
{
    public string Invoice { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public string Customer { get; init; } = string.Empty;
    public string? CustomerName { get; init; }
    public int InvoiceLine { get; init; }
    public string Item { get; init; } = string.Empty;
    public decimal Qty { get; init; }
    public decimal NetSales { get; init; }
    public decimal? Cogs { get; init; }
    public decimal? GrossProfit { get; init; }
    public decimal? GrossMarginPercent { get; init; }
    public string CogsOwnerType { get; init; } = string.Empty;
    public string CogsOwnerNo { get; init; } = string.Empty;
    public string? CogsOwnerLine { get; init; }
    public bool CogsResolved { get; init; }
}
