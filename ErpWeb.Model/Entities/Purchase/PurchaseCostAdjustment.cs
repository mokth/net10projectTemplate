namespace ErpWeb.Model.Entities.Purchase;

/// <summary>Immutable audit row for a procurement value adjustment.</summary>
public sealed class PurchaseCostAdjustment
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string AdjustmentType { get; set; } = string.Empty;
    public string SourceDocumentType { get; set; } = string.Empty;
    public string SourceDocumentNo { get; set; } = string.Empty;
    public int SourceDocumentLine { get; set; }
    public int SourceCostingRevision { get; set; }
    public string? PoNo { get; set; }
    public short? PoRelNo { get; set; }
    public short? PoLineNo { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string CostMethod { get; set; } = StockLedger.StockCostMethods.MovingAverage;
    public decimal BaseQty { get; set; }
    public decimal ActualBaseAmount { get; set; }
    public decimal ReferenceBaseAmount { get; set; }
    public decimal? CommercialReferenceAmount { get; set; }
    public decimal TotalAdjustmentAmount { get; set; }
    public decimal InventoryAdjustmentAmount { get; set; }
    public decimal ConsumedVarianceAmount { get; set; }
    public long StockPostingId { get; set; }
    public StockLedger.StockPosting? StockPosting { get; set; }
    public long? InventoryAdjustmentFactId { get; set; }
    public StockLedger.StockValuationFact? InventoryAdjustmentFact { get; set; }
    public long? ReversesAdjustmentId { get; set; }
    public PurchaseCostAdjustment? ReversesAdjustment { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
