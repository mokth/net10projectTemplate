namespace ErpWeb.Core.StockLedger;

/// <summary>Stable entity identity for a replayed Inventory history leg.</summary>
public static class StockReplayEffectTypes
{
    public const string InventoryHistory = "IV_TRX_HISTORY";
    public const string PhysicalReplacement = "PHYSICAL_REPLACEMENT";
    public const string PurchaseCreditNoteCostReplacement = "PO_CN_COST_REPLACEMENT";
    public const string PurchaseReceiptSettlement = "PO_RECEIPT_SETTLEMENT";
    public const string PurchaseCostAdjustment = "PO_COST_ADJUSTMENT";
    public const string FinishedGoodReceiptValue = "FG_RECEIPT_VALUE";
    public const string StockValuationFact = "STOCK_VALUATION_FACT";
}
