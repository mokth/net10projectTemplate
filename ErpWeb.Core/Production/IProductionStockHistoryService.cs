using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

public interface IProductionStockHistoryService
{
    Task<IvMasterOperationResult<ProductionStockCardResult>> GetCardAsync(
        ProductionStockHistoryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionStockMovementResult>> GetMovementsAsync(
        ProductionStockHistoryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionStockAsOfResult>> GetAsOfAsync(
        ProductionStockHistoryQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionStockReconcileResult>> ReconcileAsync(
        ProductionStockHistoryQuery query, CancellationToken cancellationToken = default);
}

public sealed class ProductionStockHistoryQuery
{
    public string? ItemCode { get; set; }
    public string? BaseUom { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public DateTime? AsOf { get; set; }
    public long? Watermark { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? BalanceStage { get; set; }
    public string? ProductionLocationCode { get; set; }
    public string? LotIdentity { get; set; }
    public string? StockStatusCode { get; set; }
    public string? SourceDocumentType { get; set; }
}

public static class ProductionStockCoverageCodes
{
    public const string V2 = "V2";
    public const string HistoryBeforeCutover = "HISTORY_BEFORE_CUTOVER";
    public const string NoActiveEpoch = "NO_ACTIVE_EPOCH";
}

public sealed class ProductionStockCardResult
{
    public string CoverageCode { get; init; } = ProductionStockCoverageCodes.V2;
    public string? CoverageWarning { get; init; }
    public DateTime? EpochEffectiveFrom { get; init; }
    public long Watermark { get; init; }
    public decimal OpeningQty { get; init; }
    public decimal PeriodInQty { get; init; }
    public decimal PeriodOutQty { get; init; }
    public decimal ClosingQty { get; init; }
    public IReadOnlyList<ProductionStockHistoryRow> Rows { get; init; } = [];
}

public sealed class ProductionStockMovementResult
{
    public string CoverageCode { get; init; } = ProductionStockCoverageCodes.V2;
    public string? CoverageWarning { get; init; }
    public DateTime? EpochEffectiveFrom { get; init; }
    public long Watermark { get; init; }
    public IReadOnlyList<ProductionStockHistoryRow> Rows { get; init; } = [];
}

public sealed class ProductionStockAsOfResult
{
    public string CoverageCode { get; init; } = ProductionStockCoverageCodes.V2;
    public string? CoverageWarning { get; init; }
    public DateTime? EpochEffectiveFrom { get; init; }
    public long Watermark { get; init; }
    public IReadOnlyList<ProductionStockAsOfRow> Rows { get; init; } = [];
}

public sealed class ProductionStockAsOfRow
{
    public string ItemCode { get; init; } = string.Empty;
    public string BaseUom { get; init; } = string.Empty;
    public string? WorkOrderNo { get; init; }
    public string? BalanceStage { get; init; }
    public string? ProductionLocationCode { get; init; }
    public string? LotIdentity { get; init; }
    public string? StockStatusCode { get; init; }
    public decimal BaseQty { get; init; }
}

public sealed class ProductionStockReconcileResult
{
    public string CoverageCode { get; init; } = ProductionStockCoverageCodes.V2;
    public string? CoverageWarning { get; init; }
    public DateTime? EpochEffectiveFrom { get; init; }
    public long Watermark { get; init; }
    public IReadOnlyList<ProductionStockReconcileFinding> Findings { get; init; } = [];
}

public sealed class ProductionStockReconcileFinding
{
    public string Code { get; init; } = string.Empty;
    public string Severity { get; init; } = "ERROR";
    public string Message { get; init; } = string.Empty;
    public long? ProductionBalLotId { get; init; }
    public string? ItemCode { get; init; }
    public string? WorkOrderNo { get; init; }
    public string? BalanceStage { get; init; }
    public string? ProductionLocationCode { get; init; }
    public string? LotIdentity { get; init; }
    public string? StockStatusCode { get; init; }
    public decimal? LiveBaseQty { get; init; }
    public decimal? LedgerBaseQty { get; init; }
    public decimal? Delta { get; init; }
}

public static class ProductionStockReconcileCodes
{
    public const string QtyMismatch = "QTY_MISMATCH";
    public const string MissingLive = "MISSING_LIVE";
    public const string MissingLedger = "MISSING_LEDGER";
    public const string ZeroIdentity = "ZERO_IDENTITY";
}

public sealed class ProductionStockHistoryRow
{
    public DateTime EffectiveAt { get; init; }
    public string SourceDocumentType { get; init; } = string.Empty;
    public string SourceDocumentNo { get; init; } = string.Empty;
    public string? WorkOrderNo { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public string? BalanceStage { get; init; }
    public string? ProductionLocationCode { get; init; }
    public string? LotIdentity { get; init; }
    public string? StockStatusCode { get; init; }
    public string MovementType { get; init; } = string.Empty;
    public decimal InQty { get; init; }
    public decimal OutQty { get; init; }
    public decimal RunningQty { get; init; }
    public string? ValuationStatus { get; init; }
    public long MovementId { get; init; }
    public long PostingSequence { get; init; }
    public int PostingLineNo { get; init; }
    public decimal SignedBaseQty { get; init; }
}
