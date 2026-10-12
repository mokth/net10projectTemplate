using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

public interface IProductionWorkOrderTransferService
{
    Task<IvMasterOperationResult<ProductionWorkOrderTransferPage>> SearchAsync(ProductionWorkOrderTransferQuery query, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionWorkOrderTransferDocument>> GetAsync(long id, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionWorkOrderTransferDocument>> CreateAsync(ProductionWorkOrderTransferCreateRequest request, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionWorkOrderTransferDocument>> UpdateAsync(ProductionWorkOrderTransferUpdateRequest request, CancellationToken ct = default);
    Task<IvMasterOperationResult<bool>> DeleteAsync(long id, byte[] expectedVersion, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionWorkOrderTransferDocument>> RefreshSourceSnapshotAsync(long id, byte[] expectedVersion, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionWorkOrderTransferDocument>> PostAsync(long id, byte[] expectedVersion, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionWorkOrderTransferSourcePage>> SearchEligibleSourcesAsync(ProductionWorkOrderTransferSourceQuery query, CancellationToken ct = default);
    Task<IvMasterOperationResult<ProductionWorkOrderTransferTargetPage>> SearchEligibleTargetsAsync(long sourceProductionBalLotId, ProductionWorkOrderTransferTargetQuery query, CancellationToken ct = default);
}

public sealed class ProductionWorkOrderTransferQuery
{
    public string? SearchText { get; set; }
    public string? Status { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 30;
}

public sealed class ProductionWorkOrderTransferSourceQuery
{
    public string? SearchText { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 30;
}

public sealed class ProductionWorkOrderTransferTargetQuery
{
    public string? SearchText { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 30;
}

public sealed record ProductionWorkOrderTransferPage(IReadOnlyList<ProductionWorkOrderTransferSummary> Rows, int TotalCount);
public sealed record ProductionWorkOrderTransferSummary(long Id, string TransferNo, DateTime TransferDateTime,
    string ItemCode, string LotNo, decimal Qty, string Uom, string SourceWorkOrderNo, string SourceWorkCentreCode,
    string SourceProcessCode, string TargetWorkOrderNo, string TargetConsumingWorkCentreCode,
    string TargetConsumingProcessCode, string Status, string CreatedBy, string? PostedBy,
    decimal? TransferTotalCost, byte[] RowVersion);

public sealed record ProductionWorkOrderTransferSourcePage(IReadOnlyList<ProductionWorkOrderTransferSource> Rows, int TotalCount);
public sealed record ProductionWorkOrderTransferSource(long ProductionBalLotId, string WorkOrderNo, string ItemCode,
    string Description, string LotNo, decimal Qty, string Uom, decimal BaseQty, string BaseUom,
    string WorkCentreCode, string ProcessCode, DateTime? LastMovementDate, decimal? TotalCost, byte[] RowVersion);

public sealed record ProductionWorkOrderTransferTargetPage(IReadOnlyList<ProductionWorkOrderTransferTarget> Rows, int TotalCount);
public sealed record ProductionWorkOrderTransferTarget(long WorkOrderId, string WorkOrderNo, string ProductCode,
    string? ProductDescription, string Status, long WorkOrderMaterialId, string ComponentCode,
    decimal RequiredQty, string? RequiredUom, string ConsumingWorkCentreCode, string ConsumingProcessCode,
    string ProducerWorkCentreCode, string ProducerProcessCode, long ProducerRouteStepId, long ProducerOperationId,
    decimal? TargetCurrentWipValue, decimal? ProjectedTargetWipValue);

public sealed class ProductionWorkOrderTransferCreateRequest
{
    public long SourceProductionBalLotId { get; set; }
    public long TargetWorkOrderMaterialId { get; set; }
    public DateTime TransferDateTime { get; set; }
    public string? Remark { get; set; }
}

public sealed class ProductionWorkOrderTransferUpdateRequest
{
    public long Id { get; set; }
    public byte[] ExpectedVersion { get; set; } = [];
    public long TargetWorkOrderMaterialId { get; set; }
    public DateTime TransferDateTime { get; set; }
    public string? Remark { get; set; }
}

public sealed class ProductionWorkOrderTransferDocument
{
    public long Id { get; set; }
    public string TransferNo { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime TransferDateTime { get; set; }
    public string? Remark { get; set; }
    public long SourceProductionBalLotId { get; set; }
    public string SourceWorkOrderNo { get; set; } = string.Empty;
    public string SourceWorkOrderStatus { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;
    public string ItemDescription { get; set; } = string.Empty;
    public string LotNo { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public string Uom { get; set; } = string.Empty;
    public decimal BaseQty { get; set; }
    public string BaseUom { get; set; } = string.Empty;
    public string SourceWorkCentreCode { get; set; } = string.Empty;
    public string SourceProcessCode { get; set; } = string.Empty;
    public DateTime? SourceLastMovementDate { get; set; }
    public decimal? TransferTotalCost { get; set; }
    public long TargetWorkOrderId { get; set; }
    public string TargetWorkOrderNo { get; set; } = string.Empty;
    public string TargetProductCode { get; set; } = string.Empty;
    public string? TargetProductDescription { get; set; }
    public string TargetWorkOrderStatus { get; set; } = string.Empty;
    public long TargetWorkOrderMaterialId { get; set; }
    public string TargetConsumingWorkCentreCode { get; set; } = string.Empty;
    public string TargetConsumingProcessCode { get; set; } = string.Empty;
    public string TargetProducerWorkCentreCode { get; set; } = string.Empty;
    public string TargetProducerProcessCode { get; set; } = string.Empty;
    public decimal? TargetCurrentWipValue { get; set; }
    public decimal? ProjectedTargetWipValue { get; set; }
    public bool CanViewCost { get; set; }
    public int TargetSnapshotRevision { get; set; }
    public string TargetSnapshotHash { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
    public long? StockPostingId { get; set; }
    public DateTime? PostedDate { get; set; }
    public string? PostedBy { get; set; }
}
