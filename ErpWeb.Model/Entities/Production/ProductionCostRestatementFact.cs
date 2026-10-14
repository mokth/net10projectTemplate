namespace ErpWeb.Model.Entities.Production;

/// <summary>Append-only effective cost correction for a posted Production movement.</summary>
public sealed class ProductionCostRestatementFact
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public Guid ReplayRequestId { get; set; }
    public long ReplayStockPostingId { get; set; }
    public long ProductionBalLotMovementId { get; set; }
    public DateTime EffectiveAt { get; set; }
    public decimal PriorEffectiveTotalCost { get; set; }
    public decimal CorrectedEffectiveTotalCost { get; set; }
    public decimal CorrectedUnitCost { get; set; }
    public decimal DeltaCost { get; set; }
    public long? ReversesRestatementFactId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
