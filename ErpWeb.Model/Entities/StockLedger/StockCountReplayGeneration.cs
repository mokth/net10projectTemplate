namespace ErpWeb.Model.Entities.StockLedger;

/// <summary>Append-only adjustment generation for one immutable counted pile.</summary>
public sealed class StockCountReplayGeneration
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public int StockCountLineId { get; set; }
    public long AnchorPostingId { get; set; }
    public long WritePostingId { get; set; }
    public long? PreviousGenerationId { get; set; }
    public DateTime EffectiveAt { get; set; }
    public long AnchorPostingSequence { get; set; }
    public decimal SignedAdjustmentQty { get; set; }
    public int? AdjustmentBatchNo { get; set; }
    public int? AdjustmentHistoryId { get; set; }
    public int? ReversalBatchNo { get; set; }
    public int? ReversalHistoryId { get; set; }
    public bool IsRemoval { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
