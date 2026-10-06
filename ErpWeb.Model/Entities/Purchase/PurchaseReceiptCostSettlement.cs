namespace ErpWeb.Model.Entities.Purchase;

/// <summary>
/// Immutable slice linking a purchase invoice line to one exact GR valuation fact.
/// Commercial receipt evidence and financial valuation evidence are intentionally stored
/// separately because Standard Cost receipts use different bases.
/// </summary>
public sealed class PurchaseReceiptCostSettlement
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string PiDocNo { get; set; } = string.Empty;
    public short PiLineNo { get; set; }
    public int PiCostingRevision { get; set; }
    public string PoNo { get; set; } = string.Empty;
    public short PoRelNo { get; set; }
    public short PoLineNo { get; set; }
    public string ItemCode { get; set; } = string.Empty;

    public long ReceiptValuationFactId { get; set; }
    public StockLedger.StockValuationFact? ReceiptValuationFact { get; set; }
    public int? ReceiptInventoryHistoryId { get; set; }
    public int? ReceiptBatchNo { get; set; }
    public decimal SettledBaseQty { get; set; }
    public decimal ReceiptCommercialUnitCost { get; set; }
    public decimal ReceiptCommercialBaseAmount { get; set; }
    public decimal ReceiptValuationUnitCost { get; set; }
    public decimal ReceiptValuationBaseAmount { get; set; }
    public decimal AllocatedActualBaseAmount { get; set; }
    public decimal CommercialVarianceAmount { get; set; }
    public decimal ValuationVarianceAmount { get; set; }

    public long StockPostingId { get; set; }
    public StockLedger.StockPosting? StockPosting { get; set; }
    public long? ReversesSettlementId { get; set; }
    public PurchaseReceiptCostSettlement? ReversesSettlement { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
