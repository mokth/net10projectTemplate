using ErpWeb.Model.Entities.Inventory;

namespace ErpWeb.Model.Entities.Production;

/// <summary>Single-work-order return document. Posted generations are never reopened.</summary>
public sealed class ProductionStockReturn
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
    public ICollection<ProductionStockReturnLine> Lines { get; set; } = [];
}

public sealed class ProductionStockReturnLine
{
    public long Id { get; set; }
    public int BatchId { get; set; }
    public ProductionStockReturn Return { get; set; } = null!;
    public int DetailId { get; set; }
    public IvTrxBatchDetail Detail { get; set; } = null!;
    public int DocumentRevision { get; set; } = 1;
    public long ProductionBalLotId { get; set; }
    public long SourceIssueMovementId { get; set; }
    public decimal RequestedQty { get; set; }
    public string SourceUom { get; set; } = "";
    public string BaseUom { get; set; } = "";
    public decimal SourceFactor { get; set; }
    public decimal BaseQty { get; set; }
}

/// <summary>Append-only evidence joining the exact Production and Inventory legs.</summary>
public sealed class ProductionStockReturnFact
{
    public long Id { get; set; }
    public int BatchId { get; set; }
    public long ReturnLineId { get; set; }
    public long StockPostingId { get; set; }
    public long ProductionMovementId { get; set; }
    public long ProductionMaterialMovementId { get; set; }
    public int InventoryHistoryId { get; set; }
    public int DestinationBalanceId { get; set; }
    public long SourceIssueMovementId { get; set; }
    public decimal BaseQty { get; set; }
    public decimal TotalValue { get; set; }
    public long? ReversesFactId { get; set; }
}

/// <summary>Operational price evidence; financial authority remains in the valuation facts.</summary>
public sealed class ProductionStockReturnPriceSnapshot
{
    public long Id { get; set; }
    public long StockPostingId { get; set; }
    public int DestinationBalanceId { get; set; }
    public decimal? PreviousUnitPrice { get; set; }
    public decimal? PreviousCost { get; set; }
    public string? PreviousPriceEvidence { get; set; }
    public decimal? PostedUnitPrice { get; set; }
    public decimal? PostedCost { get; set; }
    public string? PostedPriceEvidence { get; set; }
}
