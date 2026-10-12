namespace ErpWeb.Core.Production;

/// <summary>Work Order context available to PRTN and PADJ operators through their own transaction permissions.</summary>
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
    public int CandidateSourceCount { get; init; }
}
