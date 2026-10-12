using ErpWeb.Model.Entities.Inventory;

namespace ErpWeb.Model.Entities.Production;

/// <summary>Production-only physical correction of existing pools on one Work Order.</summary>
public sealed class ProductionStockAdjustment
{
    public int BatchId { get; set; }
    public IvTrxBatch Batch { get; set; } = null!;
    public string CompanyCode { get; set; } = "";
    public string BranchCode { get; set; } = "";
    public long WorkOrderId { get; set; }
    public int SnapshotRevision { get; set; }
    public string SnapshotHash { get; set; } = "";
    public int DocumentRevision { get; set; } = 1;
    public byte[] RowVersion { get; set; } = [];
    public long? PostingId { get; set; }
    public long? ReversalPostingId { get; set; }
    public int? CorrectedBatchId { get; set; }
    public string? ReversalReason { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
    public ICollection<ProductionStockAdjustmentLine> Lines { get; set; } = [];
}

public sealed class ProductionStockAdjustmentLine
{
    public long Id { get; set; }
    public int BatchId { get; set; }
    public ProductionStockAdjustment Adjustment { get; set; } = null!;
    public int DetailId { get; set; }
    public IvTrxBatchDetail Detail { get; set; } = null!;
    public long ProductionBalLotId { get; set; }
    /// <summary>Signed quantity in the frozen Production UOM. BaseQty is always a magnitude.</summary>
    public decimal AdjustmentQty { get; set; }
    public string SourceUom { get; set; } = "";
    public string BaseUom { get; set; } = "";
    public decimal SourceFactor { get; set; }
    public decimal BaseQty { get; set; }
    /// <summary>Six-decimal authority for an explicitly approved override only.</summary>
    public decimal? ProposedUnitCost { get; set; }
    public string? CostEvidenceType { get; set; }
    public string? CostOverrideReason { get; set; }
    public string? CostApprovedBy { get; set; }
    public DateTime? CostApprovedAtUtc { get; set; }
}

/// <summary>Immutable resolved cost authority. Reversal appends a linked fact.</summary>
public sealed class ProductionStockAdjustmentFact
{
    public long Id { get; set; }
    public int BatchId { get; set; }
    public long AdjustmentLineId { get; set; }
    public long StockPostingId { get; set; }
    public long ProductionMovementId { get; set; }
    public decimal BaseQty { get; set; }
    public decimal TotalValue { get; set; }
    public decimal UnitCost { get; set; }
    public string CostBasisCode { get; set; } = "";
    public long? OriginalIssueMovementId { get; set; }
    public long? StandardCostRevisionId { get; set; }
    public string? CostEvidenceType { get; set; }
    public string? CostOverrideReason { get; set; }
    public string? CostApprovedBy { get; set; }
    public DateTime? CostApprovedAtUtc { get; set; }
    public long? ReversesFactId { get; set; }
}
