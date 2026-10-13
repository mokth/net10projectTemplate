namespace ErpWeb.Core.Production;

/// <summary>Work Order context available to Production stock and transfer operators through their own transaction permissions.</summary>
public sealed class ProductionStockWorkOrderLookupRow
{
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = "";
    public string ProductCode { get; init; } = "";
    public string? ProductDescription { get; init; }
    public string Status { get; init; } = "";
    public decimal PlannedQty { get; init; }
    public decimal RemainingQty { get; init; }
    public string? OutputUom { get; init; }
    public int SnapshotRevision { get; init; }
    public string SnapshotHash { get; init; } = "";
    /// <summary>Structural candidate count for the calling lookup; deep transaction readiness is checked after Work Order selection.</summary>
    public int CandidateSourceCount { get; init; }
}
