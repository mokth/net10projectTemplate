namespace ErpWeb.Model.Entities.StockLedger;

/// <summary>One historical replay committed by an unsealed root stock posting.</summary>
public sealed class StockReplayRun
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long RootStockPostingId { get; set; }
    public Guid RootRequestId { get; set; }
    public string RootDocumentType { get; set; } = string.Empty;
    public string RootDocumentId { get; set; } = string.Empty;
    public DateTime EarliestEffectiveAt { get; set; }
    public DateTime LatestEffectiveAt { get; set; }
    public long PlanWatermark { get; set; }
    public string PlanHash { get; set; } = string.Empty;
    public int AffectedHistoryCount { get; set; }
    public int AffectedItemCount { get; set; }
    public int AffectedProductionMovementCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
