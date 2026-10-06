namespace ErpWeb.Model.Entities.StockLedger;

/// <summary>
/// Financial FIFO layer pooled by company + branch + item. Warehouse/lot fields are provenance
/// only; they must not be used to create separate financial FIFO pools.
/// </summary>
public sealed class StockFifoLayer
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;
    public string BaseUom { get; set; } = string.Empty;

    public long OriginValuationFactId { get; set; }
    public StockValuationFact? OriginValuationFact { get; set; }
    public long OriginStockPostingId { get; set; }
    public StockPosting? OriginStockPosting { get; set; }
    public DateTime ReceiptEffectiveAt { get; set; }

    public decimal OriginalQty { get; set; }
    public decimal RemainingQty { get; set; }
    public decimal OriginalValue { get; set; }
    public decimal AccumulatedAdjustment { get; set; }
    public decimal RemainingValue { get; set; }
    public decimal CurrentUnitCost { get; set; }

    public string SourceDocumentType { get; set; } = string.Empty;
    public string SourceDocumentNo { get; set; } = string.Empty;
    public string? SourceDocumentLine { get; set; }
    public string? WarehouseCode { get; set; }
    public int? LotId { get; set; }
    public string? LotNo { get; set; }
    public string Status { get; set; } = StockFifoLayerStatuses.Open;
    public byte[] RowVersion { get; set; } = [];

    public ICollection<StockFifoLayerConsumption> Consumptions { get; set; } = [];
}

public static class StockFifoLayerStatuses
{
    public const string Open = "OPEN";
    public const string Closed = "CLOSED";
}

/// <summary>Immutable quantity/value allocation of one FIFO issue to one layer.</summary>
public sealed class StockFifoLayerConsumption
{
    public long Id { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long IssueValuationFactId { get; set; }
    public StockValuationFact? IssueValuationFact { get; set; }
    public long FifoLayerId { get; set; }
    public StockFifoLayer? FifoLayer { get; set; }
    public decimal ConsumedQty { get; set; }
    public decimal ConsumedValue { get; set; }
    public int SplitOrdinal { get; set; }
    public long? ReversesConsumptionId { get; set; }
    public StockFifoLayerConsumption? ReversesConsumption { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}
