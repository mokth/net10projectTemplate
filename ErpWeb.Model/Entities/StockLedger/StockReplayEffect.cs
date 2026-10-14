namespace ErpWeb.Model.Entities.StockLedger;

/// <summary>Names the business owner of a correction written by a different root posting.</summary>
public sealed class StockReplayEffect
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long ReplayRunId { get; set; }
    public long RootStockPostingId { get; set; }
    public long BusinessOwnerPostingId { get; set; }
    public string EffectKind { get; set; } = string.Empty;
    public string AffectedEntityType { get; set; } = string.Empty;
    public long AffectedEntityId { get; set; }
    public long? OriginalEntityId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
