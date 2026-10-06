namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Immutable bridge between actual Production transfer value and the Standard-valued Inventory
/// receipt. This is variance evidence, not an Inventory valuation fact.
/// </summary>
public sealed class ProductionStandardCostVariance
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long StockPostingId { get; set; }
    public long? ProductionPostingLinkId { get; set; }
    public int? FinishedGoodReceiptId { get; set; }
    public long InventoryValuationFactId { get; set; }
    public decimal BaseQty { get; set; }
    public decimal ActualProductionValue { get; set; }
    public decimal StandardInventoryValue { get; set; }
    public decimal VarianceAmount { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public DateTime EffectiveAt { get; set; }
    public long? ReversesVarianceId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    public StockLedger.StockValuationFact? InventoryValuationFact { get; set; }
    public ProductionStandardCostVariance? ReversesVariance { get; set; }
}
