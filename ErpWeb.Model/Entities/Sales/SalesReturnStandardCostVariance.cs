namespace ErpWeb.Model.Entities.Sales;

/// <summary>
/// Immutable evidence for the difference between a Standard-valued customer-return receipt and
/// the exact original COGS basis that the return reverses. This is a management/GL bridge and is
/// deliberately outside Inventory value.
/// </summary>
public sealed class SalesReturnStandardCostVariance
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long StockPostingId { get; set; }
    public long ReturnValuationFactId { get; set; }
    public StockLedger.StockValuationFact? ReturnValuationFact { get; set; }
    public string ReturnDocumentType { get; set; } = string.Empty;
    public string ReturnDocumentNo { get; set; } = string.Empty;
    public int ReturnDocumentLine { get; set; }
    public int ReturnCostingRevision { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public decimal BaseQty { get; set; }
    public decimal CurrentStandardReceiptValue { get; set; }
    public decimal OriginalCogsReversalValue { get; set; }
    public decimal VarianceAmount { get; set; }
    public long? ReversesVarianceId { get; set; }
    public SalesReturnStandardCostVariance? ReversesVariance { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
