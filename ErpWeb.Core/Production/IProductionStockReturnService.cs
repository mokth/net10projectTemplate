using ErpWeb.Core.Inventory;
using ErpWeb.Core.Lookups;

namespace ErpWeb.Core.Production;

public interface IProductionStockReturnService
{
    Task<IvMasterOperationResult<ProductionStockReturnPage>> SearchAsync(ProductionStockReturnQuery query, CancellationToken ct = default);
    Task<LargeLookupPage<ProductionStockWorkOrderLookupRow>> SearchEligibleWorkOrdersAsync(LargeLookupSearchRequest request, CancellationToken ct = default);
    Task<LargeLookupResolveResult<ProductionStockWorkOrderLookupRow>> ResolveEligibleWorkOrderAsync(string workOrderNo, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionStockReturnSourcePage>> SearchSourcesAsync(ProductionStockReturnSourceQuery query, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionStockReturnDocument>> GetAsync(int id, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionStockReturnDocument>> SaveAsync(ProductionStockReturnSaveRequest request, CancellationToken ct = default);
    Task<IvMasterOperationResult<bool>> DeleteAsync(int id, byte[] expectedVersion, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionStockReturnDocument>> PostAsync(ProductionStockReturnCommand request, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionStockReturnDocument>> RollbackAsync(ProductionStockReturnCommand request, CancellationToken ct = default);
}

public sealed class ProductionStockReturnQuery
{
    public string? SearchText { get; set; }
    public string? Status { get; set; }
    public long? WorkOrderId { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 30;
}

public sealed class ProductionStockReturnSourceQuery
{
    public required long WorkOrderId { get; set; }
    public string? SearchText { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 30;
}

public sealed record ProductionStockReturnPage(IReadOnlyList<ProductionStockReturnSummary> Rows, int TotalCount);
public sealed record ProductionStockReturnSummary(int Id, int BatchNo, long WorkOrderId, string WorkOrderNo,
    string ItemSummary, string LotSummary, string QtySummary, string Status, DateTime EffectiveDate, byte[] RowVersion);
public sealed record ProductionStockReturnSourcePage(IReadOnlyList<ProductionStockReturnSource> Rows, int TotalCount);
public sealed record ProductionStockReturnSource(long ProductionBalLotId, long SourceIssueMovementId, string WorkOrderNo,
    string ItemCode, string Description, string LotNo, decimal ProductionQty, decimal ReturnableQty, string Uom,
    int SourceBalanceId, string Warehouse, string Location, string InventoryLotNo, string ItemStatus, string BaseUom,
    DateTime? ExpiryDate, string IssueBatchNo, string Readiness, decimal? EstimatedValue);

public sealed class ProductionStockReturnDocument
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
    public List<ProductionStockReturnLineDto> Lines { get; set; } = [];
}

public sealed class ProductionStockReturnLineDto
{
    public long ProductionBalLotId { get; set; }
    public long SourceIssueMovementId { get; set; }
    public string ItemCode { get; set; } = "";
    public string ItemDescription { get; set; } = "";
    public string LotNo { get; set; } = "";
    public decimal ProductionQty { get; set; }
    public decimal ReturnableQty { get; set; }
    public decimal CurrentReturnableQty { get; set; }
    public string CurrentReadiness { get; set; } = "";
    public decimal RequestedQty { get; set; }
    public string SourceUom { get; set; } = "";
    public decimal BaseQty { get; set; }
    public string BaseUom { get; set; } = "";
    public string Warehouse { get; set; } = "";
    public string Location { get; set; } = "";
    public string InventoryLotNo { get; set; } = "";
    public string ItemStatus { get; set; } = "";
    public DateTime? ExpiryDate { get; set; }
    public string Reason { get; set; } = "";
    public string? Remarks { get; set; }
    public decimal? TotalValue { get; set; }
}

public sealed class ProductionStockReturnSaveRequest
{
    public int Id { get; set; }
    public byte[] ExpectedVersion { get; set; } = [];
    public long WorkOrderId { get; set; }
    public int SnapshotRevision { get; set; }
    public string SnapshotHash { get; set; } = "";
    public DateTime EffectiveDate { get; set; }
    public string? RefNo { get; set; }
    public string? Remarks { get; set; }
    public List<ProductionStockReturnLineInput> Lines { get; set; } = [];
}

public sealed class ProductionStockReturnLineInput
{
    public long ProductionBalLotId { get; set; }
    public decimal Quantity { get; set; }
    public string Reason { get; set; } = "";
    public string? Remarks { get; set; }
}

public sealed record ProductionStockReturnCommand(int Id, byte[] ExpectedVersion, Guid RequestId, string? Reason = null);
