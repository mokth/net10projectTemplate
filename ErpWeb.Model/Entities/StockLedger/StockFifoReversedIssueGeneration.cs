namespace ErpWeb.Model.Entities.StockLedger;

/// <summary>
/// An append-only effective allocation for a Production issue whose original
/// physical issue and reversal are already sealed. A generation supersedes
/// the preceding allocation without attempting to reverse either fact twice.
/// </summary>
public sealed class StockFifoReversedIssueGeneration
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long ReplayRunId { get; set; }
    public long ReplayStockPostingId { get; set; }
    public int IssueHistoryId { get; set; }
    public int ReversalHistoryId { get; set; }
    public long IssueMovementId { get; set; }
    public long ReversalMovementId { get; set; }
    public long? PreviousGenerationId { get; set; }
    public StockFifoReversedIssueGeneration? PreviousGeneration { get; set; }
    public decimal BaseQty { get; set; }
    public decimal TotalCost { get; set; }
    public string SplitHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public ICollection<StockFifoReversedIssueSplit> Splits { get; set; } = [];
}

/// <summary>One layer in the complete effective issue/reversal allocation.</summary>
public sealed class StockFifoReversedIssueSplit
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long GenerationId { get; set; }
    public StockFifoReversedIssueGeneration? Generation { get; set; }
    public int SplitOrdinal { get; set; }
    public long FifoLayerId { get; set; }
    public StockFifoLayer? FifoLayer { get; set; }
    public decimal ConsumedQty { get; set; }
    public decimal ConsumedValue { get; set; }
}
