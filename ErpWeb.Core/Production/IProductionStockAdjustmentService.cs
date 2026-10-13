using ErpWeb.Core.Inventory;
using ErpWeb.Core.Lookups;

namespace ErpWeb.Core.Production;

public interface IProductionStockAdjustmentService
{
    Task<IvMasterOperationResult<ProductionStockAdjustmentPage>> SearchAsync(ProductionStockAdjustmentQuery query, CancellationToken ct = default);
    Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>> SearchEligibleWorkOrdersAsync(LargeLookupSearchRequest request, CancellationToken ct = default);
    Task<LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>> ResolveEligibleWorkOrderAsync(string workOrderNo, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionStockAdjustmentSourcePage>> SearchSourcesAsync(ProductionStockAdjustmentSourceQuery query, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionStockAdjustmentDocument>> GetAsync(int id, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionStockAdjustmentDocument>> SaveAsync(ProductionStockAdjustmentSaveRequest request, CancellationToken ct = default);
    Task<IvMasterOperationResult<bool>> DeleteAsync(int id, byte[] expectedVersion, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionStockAdjustmentDocument>> PostAsync(ProductionStockAdjustmentCommand request, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionStockAdjustmentDocument>> RollbackAsync(ProductionStockAdjustmentCommand request, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionStockAdjustmentDocument>> CreateCorrectionAsync(int id, CancellationToken ct = default);
}

public sealed class ProductionStockAdjustmentQuery
{
    public string? SearchText { get; set; }
    public string? Status { get; set; }
    public long? WorkOrderId { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 30;
}

public sealed class ProductionStockAdjustmentSourceQuery
{
    public required long WorkOrderId { get; set; }
    public string? SearchText { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 30;
}

public sealed record ProductionStockAdjustmentPage(IReadOnlyList<ProductionStockAdjustmentSummary> Rows, int TotalCount);
public sealed record ProductionStockAdjustmentSummary(int Id, int BatchNo, long WorkOrderId, string WorkOrderNo,
    string ItemSummary, string QtySummary, string Status, DateTime EffectiveDate, byte[] RowVersion);
public sealed record ProductionStockAdjustmentSourcePage(IReadOnlyList<ProductionStockAdjustmentSource> Rows, int TotalCount);
public sealed record ProductionStockAdjustmentSource(long ProductionBalLotId, string WorkOrderNo, string ItemCode,
    string Description, string PoolKind, string LotNo, decimal Qty, string Uom, decimal BaseQty, string BaseUom,
    string Readiness, decimal? EstimatedValue, bool RequiresCostApproval);

public sealed class ProductionStockAdjustmentDocument
{
    public int Id { get; set; }
    public int BatchNo { get; set; }
    public long WorkOrderId { get; set; }
    public string WorkOrderNo { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string? ProductDescription { get; set; }
    public string WorkOrderStatus { get; set; } = "";
    public string Status { get; set; } = "NEW";
    public DateTime EffectiveDate { get; set; }
    public string? RefNo { get; set; }
    public string? Remarks { get; set; }
    public int Revision { get; set; }
    public int SnapshotRevision { get; set; }
    public string SnapshotHash { get; set; } = "";
    public byte[] RowVersion { get; set; } = [];
    public long? PostingId { get; set; }
    public long? ReversalPostingId { get; set; }
    public int? CorrectedBatchId { get; set; }
    public bool CanViewCost { get; set; }
    public List<string> ReadinessErrors { get; set; } = [];
    public List<ProductionStockAdjustmentLineDto> Lines { get; set; } = [];
}

public sealed class ProductionStockAdjustmentLineDto
{
    public long ProductionBalLotId { get; set; }
    public string ItemCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string PoolKind { get; set; } = "";
    public string LotNo { get; set; } = "";
    public decimal CurrentQty { get; set; }
    public string Uom { get; set; } = "";
    public decimal AdjustmentQty { get; set; }
    public decimal BaseQty { get; set; }
    public decimal CurrentBaseQty { get; set; }
    public string SourceUom { get; set; } = "";
    public string BaseUom { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? Remarks { get; set; }
    public decimal? ProposedUnitCost { get; set; }
    public string? CostEvidenceType { get; set; }
    public string? CostOverrideReason { get; set; }
    public decimal? TotalValue { get; set; }
    public string? CostBasisCode { get; set; }
}

public sealed class ProductionStockAdjustmentSaveRequest
{
    public int Id { get; set; }
    public byte[] ExpectedVersion { get; set; } = [];
    public long WorkOrderId { get; set; }
    public int SnapshotRevision { get; set; }
    public string SnapshotHash { get; set; } = "";
    public DateTime EffectiveDate { get; set; }
    public string? RefNo { get; set; }
    public string? Remarks { get; set; }
    public List<ProductionStockAdjustmentLineInput> Lines { get; set; } = [];
}

public sealed class ProductionStockAdjustmentLineInput
{
    public long ProductionBalLotId { get; set; }
    public decimal AdjustmentQty { get; set; }
    public string Reason { get; set; } = "";
    public string? Remarks { get; set; }
    public decimal? ProposedUnitCost { get; set; }
    public string? CostEvidenceType { get; set; }
    public string? CostOverrideReason { get; set; }
}

public sealed record ProductionStockAdjustmentCommand(int Id, byte[] ExpectedVersion, Guid RequestId, string? Reason = null);
