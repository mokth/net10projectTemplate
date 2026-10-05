using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

public interface IProductionFinishedGoodReceiptService
{
    Task<IvMasterOperationResult<FinishedGoodReceiptPage>> SearchAsync(FinishedGoodReceiptQuery query, CancellationToken ct = default);
    Task<IvMasterOperationResult<FinishedGoodSourcePage>> SearchSourcesAsync(FinishedGoodSourceQuery query, CancellationToken ct = default);
    Task<IvMasterOperationResult<FinishedGoodSourceFilterOptions>> GetSourceFilterOptionsAsync(CancellationToken ct = default);
    Task<IvMasterOperationResult<FinishedGoodSourceFilterOptions>> GetSourceFilterOptionsAsync(long workOrderId, CancellationToken ct = default);
    Task<IvMasterOperationResult<FinishedGoodReceiptDocument>> GetAsync(int id, CancellationToken ct = default);
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
