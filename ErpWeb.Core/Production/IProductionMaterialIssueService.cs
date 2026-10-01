using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

/// <summary>
/// Issue-to-Production application service. The initial contract exposes the Phase-3 workspace;
/// posting and rollback commands are added with their transactional implementations in Phase 4/7.
/// </summary>
public interface IProductionMaterialIssueService
{
    Task<IvMasterOperationResult<ProductionMaterialIssueWorkspace>> GetWorkspaceAsync(
        string workOrderNo,
        long? operationId = null,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssuePostResult>> PostAsync(
        ProductionMaterialIssuePostRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ProductionMaterialIssueWorkspace
{
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public decimal PlannedQty { get; init; }
    public string? OutputUom { get; init; }
    public int SnapshotRevision { get; init; }
    public string SnapshotHash { get; init; } = string.Empty;
    public DateTime IssueDate { get; init; }
    public IReadOnlyList<ProductionMaterialIssueRouteStep> RouteSteps { get; init; } = [];
    public IReadOnlyList<ProductionMaterialIssueOperation> Operations { get; init; } = [];
    public IReadOnlyList<ProductionMaterialIssueMaterial> Materials { get; init; } = [];
    public bool CanViewCost { get; init; }
}

public sealed class ProductionMaterialIssueRouteStep
{
    public long RouteStepId { get; init; }
    public int StageSequence { get; init; }
    public string WorkCentreCode { get; init; } = string.Empty;
    public string? WorkCentreDescription { get; init; }
}

public sealed class ProductionMaterialIssueOperation
{
    public long WorkOrderOperationId { get; init; }
    public long? RouteStepId { get; init; }
    public int ProcessSequence { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public string? OperationDescription { get; init; }
    public string? WorkCentreCode { get; init; }
}

public sealed class ProductionMaterialIssueMaterial
{
    public long WorkOrderMaterialId { get; init; }
    public long WorkOrderOperationId { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public string? WorkCentreCode { get; init; }
    public string ComponentCode { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string IssueMethod { get; init; } = string.Empty;
    public string SupplySource { get; init; } = string.Empty;
    public decimal RequiredQty { get; init; }
    public string RequiredUom { get; init; } = string.Empty;
    public decimal RequiredBaseQty { get; init; }
    public string BaseUom { get; init; } = string.Empty;
    public decimal ConversionFactorToBase { get; init; }
    public decimal IssuedQty { get; init; }
    public decimal ReturnedQty { get; init; }
    public decimal NetIssuedQty { get; init; }
    public decimal ConsumedQty { get; init; }
    public decimal OutstandingQty { get; init; }
    public decimal TolerancePercent { get; init; }
    public decimal MaxAllowedNetIssue { get; init; }
    public decimal AvailableBaseQty { get; init; }
    public decimal AvailableQty { get; init; }
    public decimal ShortageQty { get; init; }
    public string? WarehouseCode { get; init; }
    public string? LocationCode { get; init; }
    public bool LotControl { get; init; }
    public bool CanManualIssue { get; init; }
    public string? BlockingReason { get; init; }
}

public sealed class ProductionMaterialIssuePostRequest
{
    public string PostingRequestId { get; set; } = string.Empty;
    public string WorkOrderNo { get; set; } = string.Empty;
    public int SnapshotRevision { get; set; }
    public string SnapshotHash { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<ProductionMaterialIssueLineRequest> Lines { get; set; } = [];
}

public sealed class ProductionMaterialIssueLineRequest
{
    public long WorkOrderMaterialId { get; set; }
    public decimal IssueQty { get; set; }
    public IReadOnlyList<ProductionMaterialIssueAllocationRequest> Allocations { get; set; } = [];
}

public sealed class ProductionMaterialIssueAllocationRequest
{
    public int FromBalLocId { get; set; }
    public decimal BaseQty { get; set; }
}

public sealed class ProductionMaterialIssuePostResult
{
    public string PostingRequestId { get; init; } = string.Empty;
    public int BatchNo { get; init; }
    public string? PostingOperationId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public string WorkOrderStatus { get; init; } = string.Empty;
    public DateTime PostedDate { get; init; }
    public IReadOnlyList<ProductionMaterialIssuePostedMaterial> Materials { get; init; } = [];
}

public sealed class ProductionMaterialIssuePostedMaterial
{
    public long WorkOrderMaterialId { get; init; }
    public decimal IssuedQty { get; init; }
    public decimal ReturnedQty { get; init; }
    public decimal NetIssuedQty { get; init; }
    public decimal OutstandingQty { get; init; }
}
