namespace ErpWeb.Model.Entities.StockLedger;

/// <summary>Immutable cost authority captured when a physical count is first posted.</summary>
public sealed class StockCountCostBasis
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public int StockCountLineId { get; set; }
    public decimal FrozenFallbackUnitCost { get; set; }
    public string CostBasisKind { get; set; } = string.Empty;
    public long? StandardCostRevisionId { get; set; }
    public long? CostBasisWatermark { get; set; }
    public string? CostBasisHash { get; set; }
    public string? CostEvidenceType { get; set; }
    public string? CostOverrideReason { get; set; }
    public string? CostApprovedBy { get; set; }
    public DateTime? CostApprovedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public static class StockCountCostBasisKinds
{
    public const string LedgerWac = "LEDGER_WAC";
    public const string LedgerFifo = "LEDGER_FIFO";
    public const string StandardRevision = "STANDARD_REVISION";
    public const string ManualApproved = "MANUAL_APPROVED";
    public const string ZeroCostApproved = "ZERO_COST_APPROVED";
}
