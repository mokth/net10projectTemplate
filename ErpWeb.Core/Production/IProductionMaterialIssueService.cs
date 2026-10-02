using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

/// <summary>
/// Issue-to-Production application service. The initial contract exposes the Phase-3 workspace;
/// posting and rollback commands are added with their transactional implementations in Phase 4/7.
/// </summary>
public interface IProductionMaterialIssueService
{
    Task<IvMasterOperationResult<int>> PeekNextBatchNoAsync(CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssueSaveResult>> CreateAsync(
        ProductionMaterialIssueSaveRequest request, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssueSaveResult>> UpdateAsync(
        int batchNo, ProductionMaterialIssueSaveRequest request, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>> DeleteAsync(
        IReadOnlyList<int> batchNos, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>> CancelAsync(
        IReadOnlyList<int> batchNos, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssueBatchActionResult>> PostAsync(
        IReadOnlyList<int> batchNos, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssueListPage>> SearchAsync(
        ProductionMaterialIssueListQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssueDocument>> GetAsync(
        int batchNo,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssueWorkspace>> GetWorkspaceAsync(
        string workOrderNo,
        long? operationId = null,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssueOperationPage>> SearchEligibleOperationsAsync(
        ProductionMaterialIssueOperationQuery query, CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssueFilterOptions>> GetEligibleOperationFilterOptionsAsync(
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssueBomPreview>> GetBomPreviewAsync(
        long workOrderOperationId, decimal productionQtyThisIssue, DateTime trxDateTime,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssuePostResult>> PostAsync(
        ProductionMaterialIssuePostRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionMaterialIssueRollbackResult>> RollbackAsync(
        ProductionMaterialIssueRollbackRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ProductionMaterialIssueSaveRequest
{
    public string WorkOrderNo { get; set; } = string.Empty;
    public long WorkOrderOperationId { get; set; }
    public int SnapshotRevision { get; set; }
    public string SnapshotHash { get; set; } = string.Empty;
    public DateTime TrxDateTime { get; set; }
    public string? RefNo { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<ProductionMaterialIssueLineRequest> Lines { get; set; } = [];
}

public sealed class ProductionMaterialIssueSaveResult
{
    public int BatchNo { get; init; }
    public string PostingRequestId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}

public sealed class ProductionMaterialIssueBatchActionResult
{
    public int SucceededCount { get; init; }
    public int FailedCount { get; init; }
    public IReadOnlyList<ProductionMaterialIssueBatchActionItem> Batches { get; init; } = [];
}

public sealed class ProductionMaterialIssueBatchActionItem
{
    public int BatchNo { get; init; }
    public bool Succeeded { get; init; }
    public string? Message { get; init; }
}

public sealed class ProductionMaterialIssueListQuery
{
    public string? SearchText { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? ProductCode { get; set; }
    public string? Status { get; set; }
    public int? BatchNo { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class ProductionMaterialIssueListPage
{
    public IReadOnlyList<ProductionMaterialIssueListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class ProductionMaterialIssueListRow
{
    public int BatchNo { get; init; }
    public DateTime IssueDate { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public string Status { get; init; } = string.Empty;
    public int LineCount { get; set; }
    public string? PostedBy { get; init; }
    public DateTime? PostedDate { get; init; }
    public DateTime? RollbackDate { get; init; }
}

public sealed class ProductionMaterialIssueDocument
{
    public int BatchNo { get; init; }
    public DateTime IssueDate { get; init; }
    public string? RefNo { get; init; }
    public string Status { get; init; } = string.Empty;
    public string WorkOrderNo { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public decimal PlannedQty { get; init; }
    public string? OutputUom { get; init; }
    public long WorkOrderId { get; init; }
    public long WorkOrderOperationId { get; init; }
    public int? SnapshotRevision { get; init; }
    public string? SnapshotHash { get; init; }
    public string? Remark { get; init; }
    public string? PostedBy { get; init; }
    public DateTime? PostedDate { get; init; }
    public DateTime? RollbackDate { get; init; }
    public bool CanViewCost { get; init; }
    public IReadOnlyList<ProductionMaterialIssueDocumentLine> Lines { get; init; } = [];
}

public sealed class ProductionMaterialIssueDocumentLine
{
    public int FromBalLocId { get; init; }
    public long WorkOrderMaterialId { get; init; }
    public short InventoryLineNo { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public decimal IssueQty { get; init; }
    public string Uom { get; init; } = string.Empty;
    public decimal BaseQty { get; init; }
    public string BaseUom { get; init; } = string.Empty;
    public string Warehouse { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string LotNo { get; init; } = string.Empty;
    public string ItemStatus { get; init; } = string.Empty;
    public decimal? UnitCost { get; init; }
    public decimal? TotalCost { get; init; }
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
    public decimal PlannedOutputQty { get; init; }
    public string? PlannedOutputUom { get; init; }
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
    public decimal OtherOpenDraftQty { get; init; }
    public decimal StandardRemaining { get; init; }
    public decimal AvailableToDraft { get; init; }
    public decimal AvailableBaseQty { get; init; }
    public decimal AvailableQty { get; init; }
    public decimal ShortageQty { get; init; }
    public string? WarehouseCode { get; init; }
    public string? LocationCode { get; init; }
    public bool LotControl { get; init; }
    public bool CanManualIssue { get; init; }
    public string? BlockingReason { get; init; }
}

public sealed class ProductionMaterialIssueOperationQuery
{
    public string? WorkOrderNo { get; set; }
    public string? Product { get; set; }
    public string? WorkCentre { get; set; }
    public string? Process { get; set; }
    public string? OutputItem { get; set; }
    public string? RawMaterial { get; set; }
    public string? Machine { get; set; }
    public bool ExactMatch { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class ProductionMaterialIssueOperationPage
{
    public IReadOnlyList<ProductionMaterialIssueOperationRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class ProductionMaterialIssueFilterOptions
{
    public IReadOnlyList<ProductionMaterialIssueFilterChoice> WorkOrders { get; init; } = [];
    public IReadOnlyList<string> Products { get; init; } = [];
    public IReadOnlyList<string> WorkCentres { get; init; } = [];
    public IReadOnlyList<ProductionMaterialIssueFilterChoice> Processes { get; init; } = [];
    public IReadOnlyList<string> OutputItems { get; init; } = [];
    public IReadOnlyList<string> RawMaterials { get; init; } = [];
    public IReadOnlyList<string> Machines { get; init; } = [];
}

public sealed record ProductionMaterialIssueFilterChoice(string Code, string Label);

public sealed class ProductionMaterialIssueOperationRow
{
    public long WorkOrderOperationId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public string? WorkCentreCode { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public string? OperationDescription { get; init; }
    public string? OutputItemCode { get; init; }
    public string? SelectedMachineCode { get; init; }
    public string RawMaterialCodes { get; set; } = string.Empty;
    public decimal PlannedOutputQty { get; init; }
    public string? PlannedOutputUom { get; init; }
}

public sealed class ProductionMaterialIssueBomPreview
{
    public long WorkOrderOperationId { get; init; }
    public decimal OperationPlannedOutputQty { get; init; }
    public decimal ProductionQtyThisIssue { get; init; }
    public IReadOnlyList<ProductionMaterialIssueBomPreviewLine> Lines { get; init; } = [];
}

public sealed class ProductionMaterialIssueBomPreviewLine
{
    public long WorkOrderMaterialId { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public decimal RequestedMaterialQty { get; init; }
    public decimal SuggestedIssueQty { get; init; }
    public decimal AvailableToDraft { get; init; }
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

public sealed class ProductionMaterialIssueRollbackRequest
{
    public string PostingRequestId { get; set; } = string.Empty;
    public int InventoryBatchNo { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class ProductionMaterialIssueRollbackResult
{
    public string PostingRequestId { get; init; } = string.Empty;
    public int BatchNo { get; init; }
    public string? RollbackOperationId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public string WorkOrderStatus { get; init; } = string.Empty;
    public DateTime RollbackDate { get; init; }
    public IReadOnlyList<ProductionMaterialIssuePostedMaterial> Materials { get; init; } = [];
}
