namespace ErpWeb.Core.Production;

/// <summary>Forward posting is opt-in after SQL Server acceptance. Rollback never uses these switches.</summary>
public sealed class ProductionStockTransactionOptions
{
    public const string SectionName = "Production:StockTransactions";
    public bool ReturnPostingEnabled { get; set; }
    public bool AdjustmentPostingEnabled { get; set; }
}
