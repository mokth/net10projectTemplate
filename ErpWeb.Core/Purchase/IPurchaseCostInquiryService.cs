using ErpWeb.Model.Entities.Purchase;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// Read-only procurement costing inquiry. The report is intentionally built from immutable
/// receipt-settlement and cost-adjustment evidence rather than from the current PO or item master.
/// </summary>
public interface IPurchaseCostInquiryService
{
    Task<PurchaseCostInquiryPage> SearchAsync(
        PurchaseCostInquiryQuery? query = null,
        CancellationToken cancellationToken = default);
}

public sealed class PurchaseCostInquiryQuery
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? VendorCode { get; set; }
    public string? PoNo { get; set; }
    public string? ItemCode { get; set; }
    public string? PiDocNo { get; set; }
    public bool IncludeUnresolvedRows { get; set; } = true;
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
}

public sealed class PurchaseCostInquiryPage
{
    public IReadOnlyList<PurchaseCostInquiryRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
    public PurchaseCostInquiryTotals Totals { get; init; } = new();
}

public sealed class PurchaseCostInquiryTotals
{
    public int ResolvedLineCount { get; init; }
    public int UnresolvedLineCount { get; init; }
    public decimal SettledBaseQty { get; init; }
    public decimal PoProvisionalCost { get; init; }
    public decimal GrReceiptCost { get; init; }
    public decimal PiActualCost { get; init; }
    public decimal PiVariance { get; init; }
    public decimal InventoryCapitalizedVariance { get; init; }
    public decimal ConsumedVariance { get; init; }
    public decimal LandedCost { get; init; }
}

/// <summary>
/// Minimum Stage 5 procurement costing grain: vendor + PO + PO line + item + PI + PI line.
/// Amounts from settlement/adjustment evidence are in company base currency. PO provisional cost
/// is nullable when the exact PO line or a safe provisional rate is unavailable.
/// </summary>
public sealed class PurchaseCostInquiryRow
{
    public string VendorCode { get; init; } = string.Empty;
    public string? VendorName { get; init; }
    public string PoNo { get; init; } = string.Empty;
    public short? PoRelNo { get; init; }
    public short? PoLineNo { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public string PiDocNo { get; init; } = string.Empty;
    public short PiLineNo { get; init; }
    public DateTime PiDate { get; init; }
    public string? CurrencyCode { get; init; }

    public decimal SettledBaseQty { get; init; }
    public decimal? PoProvisionalCost { get; init; }
    public decimal GrReceiptCost { get; init; }
    public decimal GrCommercialCost { get; init; }
    public decimal PiActualCost { get; init; }
    public decimal PiVariance { get; init; }
    public decimal InventoryCapitalizedVariance { get; init; }
    public decimal ConsumedVariance { get; init; }
    public decimal LandedCost { get; init; }

    public string CostMethod { get; init; } = string.Empty;
    public string ValuationStatus { get; init; } = string.Empty;
}
