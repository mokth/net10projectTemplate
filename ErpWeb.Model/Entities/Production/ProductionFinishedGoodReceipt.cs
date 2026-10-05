using ErpWeb.Model.Entities.Inventory;

namespace ErpWeb.Model.Entities.Production;

/// <summary>The concurrency token covers the complete FG document, including its source rows.</summary>
public sealed class ProductionFinishedGoodReceipt
{
    public int BatchId { get; set; }
    public IvTrxBatch Batch { get; set; } = null!;
    public string CompanyCode { get; set; } = "";
    public string BranchCode { get; set; } = "";
    public long WorkOrderId { get; set; }
    public int DocumentRevision { get; set; } = 1;
    public byte[] RowVersion { get; set; } = [];
    public long? PostingId { get; set; }
    public long? ReversalPostingId { get; set; }
    public int? CorrectedBatchId { get; set; }
    public string? ReversalReason { get; set; }
    public ICollection<ProductionFinishedGoodSource> Sources { get; set; } = [];
}

public sealed class ProductionFinishedGoodSource
{
    public long Id { get; set; }
    public int BatchId { get; set; }
    public ProductionFinishedGoodReceipt Receipt { get; set; } = null!;
    public int DetailId { get; set; }
    public IvTrxBatchDetail Detail { get; set; } = null!;
    public long ProductionBalLotId { get; set; }
    public decimal RequestedQty { get; set; }
    public string SourceUom { get; set; } = "";
    public string DestinationUom { get; set; } = "";
    public string BaseUom { get; set; } = "";
    public decimal SourceFactor { get; set; }
    public decimal DestinationFactor { get; set; }
    public decimal BaseQty { get; set; }
}

/// <summary>Immutable evidence of both legs; reversal adds another fact.</summary>
public sealed class ProductionFinishedGoodFact
{
    public long Id { get; set; }
    public int BatchId { get; set; }
    public long SourceId { get; set; }
    public long StockPostingId { get; set; }
    public long ProductionMovementId { get; set; }
    public int InventoryHistoryId { get; set; }
    public int DestinationBalanceId { get; set; }
    public decimal BaseQty { get; set; }
    public decimal TotalValue { get; set; }
    public long? ReversesFactId { get; set; }
}

public sealed class ProductionFinishedGoodPriceSnapshot
{
    public long Id { get; set; }
    public long StockPostingId { get; set; }
    public int DestinationBalanceId { get; set; }
    public decimal? PreviousUnitPrice { get; set; }
    public decimal? PreviousCost { get; set; }
    public string? PreviousPriceEvidence { get; set; }
    public decimal PostedUnitPrice { get; set; }
}

/// <summary>Company-wide physical lot ownership survives receipts and reversals.</summary>
public sealed class ProductionFinishedGoodLotOrigin
{
    public int LotId { get; set; }
    public string CompanyCode { get; set; } = "";
    public string OriginatingBranch { get; set; } = "";
    public long WorkOrderId { get; set; }
    public long RouteStepId { get; set; }
    public long OperationId { get; set; }
    public string PhysicalLotNo { get; set; } = "";
}

/// <summary>Generation stays contaminated until both quantity and value are zero.</summary>
public sealed class ProductionPoolValuation
{
    public long ProductionBalLotId { get; set; }
    public int Generation { get; set; } = 1;
    public string Status { get; set; } = "UNVALUED";
    public decimal TrackedBaseQty { get; set; }
    public decimal TrackedValue { get; set; }
}

public sealed class ProductionValuationEvidence
{
    public long MovementId { get; set; }
    public long ProductionBalLotId { get; set; }
    public int Generation { get; set; }
    public string Status { get; set; } = "UNVALUED";
    public string Basis { get; set; } = "";
    public string? Currency { get; set; }
    public string? PriceUom { get; set; }
    public decimal? Price { get; set; }
    public decimal? ConversionFactor { get; set; }
    public int? InventoryHistoryId { get; set; }
    public long? OriginalMovementId { get; set; }
}

/// <summary>Value dependencies deliberately include contributors with zero FIFO allocation.</summary>
public sealed class ProductionPoolDependency
{
    public long Id { get; set; }
    public long ContributorMovementId { get; set; }
    public long ConsumerMovementId { get; set; }
    public long StockPostingId { get; set; }
    public long? ReversesDependencyId { get; set; }
}
