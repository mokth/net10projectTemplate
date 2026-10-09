using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

public interface IProductionFinishedGoodReceiptService
{
    Task<IvMasterOperationResult<FinishedGoodReceiptPage>> SearchAsync(FinishedGoodReceiptQuery query, CancellationToken ct = default);
    Task<IvMasterOperationResult<FinishedGoodSourcePage>> SearchSourcesAsync(FinishedGoodSourceQuery query, CancellationToken ct = default);
    Task<IvMasterOperationResult<FinishedGoodSourceFilterOptions>> GetSourceFilterOptionsAsync(CancellationToken ct = default);
    Task<IvMasterOperationResult<FinishedGoodSourceFilterOptions>> GetSourceFilterOptionsAsync(long workOrderId, CancellationToken ct = default);
    Task<IvMasterOperationResult<FinishedGoodReceiptDocument>> GetAsync(int id, CancellationToken ct = default);
    Task<IvMasterOperationResult<FinishedGoodCostTrace>> GetCostTraceAsync(
        int receiptId, long sourceId, CancellationToken ct = default);
    Task<IvMasterOperationResult<FinishedGoodReceiptDocument>> SaveAsync(FinishedGoodReceiptSaveRequest request, CancellationToken ct = default);
    Task<IvMasterOperationResult<bool>> DeleteAsync(int id, byte[] expectedVersion, CancellationToken ct = default);
    Task<IvMasterOperationResult<FinishedGoodReceiptDocument>> PostAsync(FinishedGoodReceiptCommand request, CancellationToken ct = default);
    Task<IvMasterOperationResult<FinishedGoodReceiptDocument>> RollbackAsync(FinishedGoodReceiptCommand request, CancellationToken ct = default);
    Task<IvMasterOperationResult<FinishedGoodReceiptDocument>> CreateCorrectionAsync(int id, CancellationToken ct = default);
}

public sealed class FinishedGoodReceiptOptions
{
    public bool PostingEnabled { get; set; }
}

public sealed class FinishedGoodReceiptQuery
{
    public string? SearchText { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? Status { get; set; }
    public long? WorkOrderId { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 30;
}

public sealed class FinishedGoodSourceQuery
{
    public string? SearchText { get; set; }
    public long? WorkOrderId { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? WorkCentreCode { get; set; }
    public string? ProcessCode { get; set; }
    public string? ItemCode { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class FinishedGoodSourceFilterOptions
{
    public IReadOnlyList<ProductionOutputChoice> WorkOrders { get; init; } = [];
    public IReadOnlyList<ProductionOutputChoice> WorkCentres { get; init; } = [];
    public IReadOnlyList<ProductionOutputChoice> Processes { get; init; } = [];
    public IReadOnlyList<ProductionOutputChoice> Items { get; init; } = [];
}

public sealed record FinishedGoodReceiptPage(IReadOnlyList<FinishedGoodReceiptSummary> Rows, int TotalCount, bool PostingEnabled);
public sealed record FinishedGoodSourcePage(IReadOnlyList<FinishedGoodSourceRow> Rows, int TotalCount);
public sealed record FinishedGoodReceiptSummary(
    int Id,
    int BatchNo,
    string WorkOrderNo,
    string ItemSummary,
    string LotSummary,
    string QtySummary,
    string WarehouseSummary,
    string Status,
    DateTime EffectiveDate,
    byte[] RowVersion);
public sealed record FinishedGoodSourceRow(
    long Id,
    long WorkOrderId,
    string WorkOrderNo,
    string ItemCode,
    string LotNo,
    string Uom,
    decimal AvailableQty,
    string? WorkCentre,
    string? Process,
    bool LotControl,
    decimal? EstimatedValue,
    string Readiness);

public sealed class FinishedGoodReceiptDocument
{
    public int Id { get; set; }
    public int BatchNo { get; set; }
    public long WorkOrderId { get; set; }
    public string WorkOrderNo { get; set; } = "";
    public string Status { get; set; } = "NEW";
    public DateTime EffectiveDate { get; set; }
    public string? RefNo { get; set; }
    public string? Remarks { get; set; }
    public int Revision { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public long? PostingId { get; set; }
    public long? ReversalPostingId { get; set; }
    public int? CorrectedBatchId { get; set; }
    public bool CanViewCost { get; set; }
    public bool PostingEnabled { get; set; }
    public List<string> ReadinessErrors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<FinishedGoodReceiptLine> Lines { get; set; } = [];
}
public sealed class FinishedGoodReceiptLine
{
    public long SourceId { get; set; }
    public long ProductionBalLotId { get; set; }
    public string ItemCode { get; set; } = "";
    public string SourceLot { get; set; } = "";
    public string? WorkCentre { get; set; }
    public string? Process { get; set; }
    public decimal AvailableQty { get; set; }
    public decimal Quantity { get; set; }
    public string SourceUom { get; set; } = "";
    public decimal DestinationQty { get; set; }
    public string DestinationUom { get; set; } = "";
    public string Warehouse { get; set; } = "";
    public string Location { get; set; } = "";
    public string LotNo { get; set; } = "";
    public DateTime? ExpiryDate { get; set; }
    public bool LotControl { get; set; }
    public decimal? TotalValue { get; set; }
}
public sealed class FinishedGoodReceiptSaveRequest
{
    public int Id { get; set; }
    public byte[] ExpectedVersion { get; set; } = [];
    public long WorkOrderId { get; set; }
    public DateTime EffectiveDate { get; set; }
    public string? RefNo { get; set; }
    public string? Remarks { get; set; }
    public List<FinishedGoodReceiptLineInput> Lines { get; set; } = [];
}
public sealed class FinishedGoodReceiptLineInput
{
    public long ProductionBalLotId { get; set; }
    public decimal Quantity { get; set; }
    public string Warehouse { get; set; } = "";
    public string Location { get; set; } = "";
    public string? LotNo { get; set; }
    public DateTime? ExpiryDate { get; set; }
}
public sealed record FinishedGoodReceiptCommand(int Id, byte[] ExpectedVersion, Guid RequestId, string? Reason = null);

public sealed class FinishedGoodCostTrace
{
    public int ReceiptId { get; init; }
    public int BatchNo { get; init; }
    public long SourceId { get; init; }
    public long ProductionBalLotId { get; init; }
    public string DocumentStatus { get; init; } = "";
    public string TraceStatus { get; init; } = "";
    public string BreakdownBasis { get; init; } = "DERIVED_POOLED_COMPONENT_TRACE";
    public string WorkOrderNo { get; init; } = "";
    public string ItemCode { get; init; } = "";
    public string SourceLot { get; init; } = "";
    public decimal SourceQty { get; init; }
    public string SourceUom { get; init; } = "";
    public decimal BaseQty { get; init; }
    public decimal DestinationQty { get; init; }
    public string DestinationUom { get; init; } = "";
    public decimal ExactPostedValue { get; init; }
    public decimal EffectiveLineUnitCost { get; init; }
    public decimal? DestinationPostedUnitPrice { get; init; }
    public int ValuationGeneration { get; init; }
    public decimal PoolBaseQtyBeforePosting { get; init; }
    public decimal PoolValueBeforePosting { get; init; }
    public long OriginalPostingId { get; init; }
    public long ProductionMovementId { get; init; }
    public long? ReversalPostingId { get; init; }
    public IReadOnlyList<FinishedGoodCostTraceComponent> Components { get; init; } = [];
    public IReadOnlyList<FinishedGoodCostTraceDetail> Details { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class FinishedGoodCostTraceComponent
{
    public string ComponentType { get; init; } = "";
    public string Label { get; init; } = "";
    public decimal Amount { get; init; }
}

public sealed record FinishedGoodCostTraceDetail
{
    public string ComponentType { get; init; } = "";
    public string SourceKind { get; init; } = "";
    public long? SourceFactId { get; init; }
    public long? SourceMovementId { get; init; }
    public string SourceDocumentType { get; init; } = "";
    public string SourceDocumentNo { get; init; } = "";
    public string? OriginDocumentType { get; init; }
    public string? OriginDocumentNo { get; init; }
    public string? ProductionOutputNo { get; init; }
    public string ItemCode { get; init; } = "";
    public string? Lot { get; init; }
    public decimal? Quantity { get; init; }
    public string? Uom { get; init; }
    public decimal? Rate { get; init; }
    public string? CostMethod { get; init; }
    public string ValuationSource { get; init; } = "";
    public decimal Amount { get; init; }
}
