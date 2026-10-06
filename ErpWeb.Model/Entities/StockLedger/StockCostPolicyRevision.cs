namespace ErpWeb.Model.Entities.StockLedger;

/// <summary>
/// Effective-dated financial costing policy for one company/branch.
/// Policy rows are append-only from the application's point of view once a covered period has
/// been sealed; a new revision supersedes an old row instead of mutating historical facts.
/// </summary>
public sealed class StockCostPolicyRevision
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string CostMethod { get; set; } = StockCostMethods.MovingAverage;
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string Status { get; set; } = StockCostPolicyStatuses.Active;
    public string? Reason { get; set; }
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime ApprovedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
}

public static class StockCostPolicyStatuses
{
    public const string Active = "ACTIVE";
    public const string Superseded = "SUPERSEDED";
}
