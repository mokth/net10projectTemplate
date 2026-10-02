using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

public sealed class ProductionWorkOrderListQuery
{
    public string? SearchText { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? ProductCode { get; set; }
    public string? DefinitionCode { get; set; }
    public string? Status { get; set; }
    public DateTime? StartDateFrom { get; set; }
    public DateTime? StartDateTo { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class ProductionWorkOrderListRow
{
    public string WorkOrderNo { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public string SourceDefinitionCode { get; init; } = string.Empty;
    public string? SourceDefinitionName { get; init; }
    public string? OutputUom { get; init; }
    public string Status { get; init; } = string.Empty;
    public int BomVersion { get; init; }
    public int SnapshotRevision { get; init; }
    public decimal PlannedQty { get; init; }
    public decimal GoodQty { get; init; }
    public decimal RemainingQty { get; init; }
    public DateTime PlannedStartDate { get; init; }
    public DateTime PlannedCompletionDate { get; init; }
    public string SourceType { get; init; } = string.Empty;
    public string? SourceReference { get; init; }
    public decimal MaterialProgressPercent { get; init; }
    public decimal ProductionProgressPercent { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }
}

public sealed class ProductionWorkOrderListPage
{
    public IReadOnlyList<ProductionWorkOrderListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class ProductionWorkOrderDraftRequest
{
    public string? WorkOrderNo { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string DefinitionCode { get; set; } = string.Empty;
    public decimal PlannedQty { get; set; } = 1m;
    public DateTime PlannedStartDate { get; set; } = DateTime.UtcNow.Date;
    public DateTime PlannedCompletionDate { get; set; } = DateTime.UtcNow.Date;
    public string SchedulingDirection { get; set; } = ProductionSchedulingDirections.Forward;
    public string SourceType { get; set; } = ProductionSourceTypes.Manual;
    public string? SourceReference { get; set; }
    public string? Remark { get; set; }
    public byte[]? RowVersion { get; set; }
}

public sealed class ProductionWorkOrderMaterialVm
{
    public long Uid { get; init; }
    public int LineNo { get; init; }
    public long? SourceBomHdrId { get; init; }
    public int? SourceBomVersion { get; init; }
    public long? SourceBomLineId { get; init; }
    public string? ParentProductCode { get; init; }
    public string BomPath { get; init; } = string.Empty;
    public string ComponentCode { get; init; } = string.Empty;
    public string? ComponentDescription { get; init; }
    public string MfgType { get; init; } = string.Empty;
    public decimal ComponentQtyPerParent { get; init; }
    public decimal BomOutputQty { get; init; }
    public string? BomOutputUom { get; init; }
    public decimal ScrapPercent { get; init; }
    public decimal Tolerance { get; init; }
    public int MaterialSequence { get; init; }
    public long? WorkOrderOperationId { get; init; }
    public string? ConsumingOperationCode { get; init; }
    public string? AlternateGroupCode { get; init; }
    public string? StandardUom { get; init; }
    public string? IssueMethod { get; init; }
    public string? SupplySource { get; init; }
    public string? ComponentDefinitionCode { get; init; }
    public decimal RequiredBaseQty { get; init; }
    public string? BaseUom { get; init; }
    public decimal RequiredQty { get; init; }
    public string? RequiredUom { get; init; }
    public string? WarehouseCode { get; init; }
    public string? LocationCode { get; init; }
    public decimal ReservedQty { get; init; }
    public decimal PickedQty { get; init; }
    public decimal IssuedQty { get; init; }
    public decimal ReturnedQty { get; init; }
    public decimal ConsumedQty { get; init; }
    public decimal VarianceQty { get; init; }
    public decimal OpenRequirementQty { get; init; }
}

public sealed class ProductionWorkOrderResourceVm
{
    public long Uid { get; init; }
    public int SequenceNo { get; init; }
    public string ResourceType { get; init; } = string.Empty;
    public string ResourceCode { get; init; } = string.Empty;
    public string? ResourceDescription { get; init; }
    public decimal PlannedUnits { get; init; }
    public decimal SetupMinutes { get; init; }
    public decimal RunMinutes { get; init; }
    public decimal QueueMinutes { get; init; }
    public decimal Rate { get; init; }
    public decimal PlannedAmount { get; init; }
}

public sealed class ProductionWorkOrderOperationVm
{
    public long Uid { get; init; }
    public int SequenceNo { get; init; }
    public string? WorkCentreCode { get; init; }
    public string? WorkCentreDescription { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public string? OperationDescription { get; init; }
    public bool IsFinalOperation { get; init; }
    public DateTime? PlannedStartDate { get; set; }
    public DateTime? PlannedCompletionDate { get; set; }
    public decimal PlannedQty { get; init; }
    public decimal SetupLossQty { get; init; }
    public decimal OperationLossQty { get; init; }
    public int ProcessSequence { get; init; }
    public string ProcessType { get; init; } = string.Empty;
    public decimal StandardDurationMinutes { get; init; }
    public decimal PlannedInputQty { get; init; }
    public string? PlannedInputUom { get; init; }
    public decimal PlannedOutputQty { get; init; }
    public string? PlannedOutputUom { get; init; }
    public string? ScheduleSourceHash { get; init; }
    public IReadOnlyList<ProductionWorkOrderMachineVm> Machines { get; init; } = [];
    public IReadOnlyList<ProductionWorkOrderLabourVm> Labours { get; init; } = [];
    public decimal InputQty { get; init; }
    public decimal ProcessedQty { get; init; }
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }
    public decimal RejectQty { get; init; }
    public decimal HoldQty { get; init; }
    public decimal ReworkQty { get; init; }
    public decimal TransferredQty { get; init; }
    public decimal RemainingQty { get; init; }
    public IReadOnlyList<ProductionWorkOrderResourceVm> Resources { get; init; } = [];
}

public sealed class ProductionAuditEventVm
{
    public long Uid { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string? FromStatus { get; init; }
    public string? ToStatus { get; init; }
    public int SnapshotRevision { get; init; }
    public string? Reason { get; init; }
    public DateTime OccurredDate { get; init; }
    public string ActorUserId { get; init; } = string.Empty;
}

public sealed class ProductionWorkOrderPreview
{
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public string? OutputUom { get; init; }
    public string SourceDefinitionCode { get; init; } = string.Empty;
    public string? SourceDefinitionName { get; init; }
    public long SourceBomHdrId { get; init; }
    public int SourceBomVersion { get; init; }
    public decimal BomBaseQty { get; init; }
    public string? BomBaseUom { get; init; }
    public decimal PlannedQty { get; init; }
    public DateTime PlannedStartDate { get; init; }
    public DateTime PlannedCompletionDate { get; init; }
    public string SchedulingDirection { get; init; } = string.Empty;
    public DateTime ScheduleAnchorDateTime { get; init; }
    public string SnapshotHash { get; init; } = string.Empty;
    public long? SourceProductDefinitionRevisionId { get; init; }
    public IReadOnlyList<ProductionWorkOrderRouteStepVm> RouteSteps { get; init; } = [];
    public IReadOnlyList<ProductionWorkOrderMaterialVm> Materials { get; init; } = [];
    public IReadOnlyList<ProductionWorkOrderOperationVm> Operations { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class ProductionWorkOrderDetail
{
    public long Uid { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public string CompanyCode { get; init; } = string.Empty;
    public string BranchCode { get; init; } = string.Empty;
    public string? LocationCode { get; init; }
    public int SnapshotRevision { get; init; }
    public string SnapshotHash { get; init; } = string.Empty;
    public string SourceDefinitionCode { get; init; } = string.Empty;
    public string? SourceDefinitionName { get; init; }
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public string? OutputUom { get; init; }
    public long SourceBomHdrId { get; init; }
    public int SourceBomVersion { get; init; }
    public decimal BomBaseQty { get; init; }
    public string? BomBaseUom { get; init; }
    public decimal PlannedQty { get; init; }
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }
    public decimal RejectQty { get; init; }
    public decimal HoldQty { get; init; }
    public decimal ApprovedVarianceQty { get; init; }
    public decimal RemainingQty { get; init; }
    public DateTime PlannedStartDate { get; init; }
    public DateTime PlannedCompletionDate { get; init; }
    public string SchedulingDirection { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public string? SourceReference { get; init; }
    public string? Remark { get; init; }
    public DateTime? ReleasedDate { get; init; }
    public string? ReleasedBy { get; init; }
    public DateTime? CancelledDate { get; init; }
    public string? CancelledBy { get; init; }
    public string? CancellationReason { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public int SnapshotFormatVersion { get; init; }
    public bool IsLegacySnapshot { get; init; }
    public long? SourceProductDefinitionRevisionId { get; init; }
    public string? DefinitionSourceHash { get; init; }
    public DateTime? ScheduleAnchorDateTime { get; init; }
    public IReadOnlyList<ProductionWorkOrderRouteStepVm> RouteSteps { get; init; } = [];
    public IReadOnlyList<ProductionWorkOrderMaterialVm> Materials { get; init; } = [];
    public IReadOnlyList<ProductionWorkOrderOperationVm> Operations { get; init; } = [];
    public IReadOnlyList<ProductionAuditEventVm> AuditEvents { get; init; } = [];
}

public sealed class ProductionPredicateReason
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>Contract returned by future CanComplete/CanClose evaluators.</summary>
public sealed class ProductionPredicateResult
{
    public bool Allowed { get; init; }
    public IReadOnlyList<ProductionPredicateReason> Reasons { get; init; } = [];

    public static ProductionPredicateResult Allow() => new() { Allowed = true };
    public static ProductionPredicateResult Deny(params ProductionPredicateReason[] reasons) =>
        new() { Allowed = false, Reasons = reasons };
}

public static class ProductionPredicateReasonCodes
{
    public const string InvalidStatus = "INVALID_STATUS";
    public const string OutputNotAccounted = "OUTPUT_NOT_ACCOUNTED";
    public const string OperationsUnresolved = "OPERATIONS_UNRESOLVED";
    public const string BlockingDisposition = "BLOCKING_DISPOSITION";
    public const string PendingExecutionDocument = "PENDING_EXECUTION_DOCUMENT";
    public const string OpenMaterialRequirement = "OPEN_MATERIAL_REQUIREMENT";
    public const string OpenWip = "OPEN_WIP";
    public const string CostingIncomplete = "COSTING_INCOMPLETE";
    public const string VarianceApprovalPending = "VARIANCE_APPROVAL_PENDING";
    public const string ChangeOrderPending = "CHANGE_ORDER_PENDING";
    public const string ReservationPending = "RESERVATION_PENDING";
    public const string DemandAllocationUnreconciled = "DEMAND_ALLOCATION_UNRECONCILED";
}

public sealed class ProductionChangeOrderRequest
{
    public string WorkOrderNo { get; set; } = string.Empty;
    public int SourceSnapshotRevision { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime? EffectiveDate { get; set; }
    public IReadOnlyList<ProductionChangeOrderLineRequest> Lines { get; set; } = [];
    public byte[]? WorkOrderRowVersion { get; set; }
}

public sealed class ProductionChangeOrderLineRequest
{
    public string ChangeType { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public long? TargetUid { get; set; }
    public string? FieldName { get; set; }
    public string? BeforeValue { get; set; }
    public string? AfterValue { get; set; }
}

public sealed class ProductionWorkOrderRouteStepVm
{
    public long Uid { get; init; }
    public int StageSequence { get; init; }
    public string WorkCentreCode { get; init; } = string.Empty;
    public string OutputItemCode { get; init; } = string.Empty;
    public decimal PlannedQty { get; init; }
    public string? OutputUom { get; init; }
    public DateTime? PlannedStartDateTime { get; init; }
    public DateTime? PlannedCompletionDateTime { get; init; }
    public IReadOnlyList<ProductionWorkOrderOperationVm> Operations { get; init; } = [];
}

public sealed class ProductionWorkOrderMachineVm
{
    public long Uid { get; init; }
    public long WorkOrderOperationId { get; init; }
    public string? OperationCode { get; init; }
    public int Priority { get; init; }
    public string MachineCode { get; init; } = string.Empty;
    public string? MachineDescription { get; init; }
    public bool IsDefault { get; init; }
    public bool IsSelected { get; init; }
    public int ParallelMachineCount { get; init; }
    public string CycleQuantityMode { get; init; } = string.Empty;
    public decimal CycleSeconds { get; init; }
    public decimal OutputPerCycle { get; init; }
    public string? OutputPerCycleUom { get; init; }
    public decimal RequiredMachineOutputQty { get; init; }
    public string? RequiredMachineOutputUom { get; init; }
    public decimal PlannedCycleCount { get; init; }
    public decimal PlannedCycleSlots { get; init; }
    public decimal PlannedRunMinutes { get; init; }
    public decimal SetupSeconds { get; init; }
    public decimal ConversionSeconds { get; init; }
    public decimal QueueSeconds { get; init; }
    public DateTime? PlannedStartDateTime { get; init; }
    public DateTime? PlannedCompletionDateTime { get; init; }
    public string? ScheduleSourceHash { get; init; }
    public IReadOnlyList<ProductionWorkOrderLabourVm> Labours { get; init; } = [];
}

public sealed class ProductionWorkOrderLabourVm
{
    public long Uid { get; init; }
    public string? OperationCode { get; init; }
    public string? MachineCode { get; init; }
    public string LabourCode { get; init; } = string.Empty;
    public string? LabourDescription { get; init; }
    public decimal? PlannedUnits { get; init; }
    public decimal? PlannedMinutes { get; init; }
    public string RateBasis { get; init; } = string.Empty;
    public decimal Rate { get; init; }
    public bool ContributesToPlan { get; init; }
    public decimal PlannedAmount { get; init; }
}

public sealed class ProductionWorkOrderHeaderUpdate
{
    public string WorkOrderNo { get; set; } = string.Empty;
    public decimal PlannedQty { get; set; }
    public DateTime? ScheduleAnchorDateTime { get; set; }
    public string SchedulingDirection { get; set; } = ProductionSchedulingDirections.Forward;
    public string? SourceReference { get; set; }
    public string? Remark { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ProductionWorkOrderRecalculateRequest
{
    public string WorkOrderNo { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
    public int SnapshotRevision { get; set; }
    public string SnapshotHash { get; set; } = string.Empty;
}

public sealed class ProductionWorkOrderRefreshPreview
{
    public byte[] RowVersion { get; init; } = [];
    public long? SourceProductDefinitionRevisionId { get; init; }
    public int DefinitionSourceHashVersion { get; init; }
    public string DefinitionSourceHash { get; init; } = string.Empty;
    public IReadOnlyList<string> Added { get; init; } = [];
    public IReadOnlyList<string> Removed { get; init; } = [];
    public IReadOnlyList<string> Changed { get; init; } = [];
}

public sealed class ProductionWorkOrderRefreshConfirm
{
    public string WorkOrderNo { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
    public long SourceProductDefinitionRevisionId { get; set; }
    public int DefinitionSourceHashVersion { get; set; }
    public string DefinitionSourceHash { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class ProductionWorkOrderChangeDefinitionPreview
{
    public byte[] RowVersion { get; init; } = [];
    public string TargetDefinitionCode { get; init; } = string.Empty;
    public string? TargetDefinitionName { get; init; }
    public long? TargetSourceProductDefinitionRevisionId { get; init; }
    public int TargetSourceBomVersion { get; init; }
    public int TargetDefinitionSourceHashVersion { get; init; }
    public string TargetDefinitionSourceHash { get; init; } = string.Empty;
    public IReadOnlyList<string> Added { get; init; } = [];
    public IReadOnlyList<string> Removed { get; init; } = [];
    public IReadOnlyList<string> Changed { get; init; } = [];
}

public sealed class ProductionWorkOrderChangeDefinitionConfirm
{
    public string WorkOrderNo { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
    public string TargetDefinitionCode { get; set; } = string.Empty;
    public long TargetSourceProductDefinitionRevisionId { get; set; }
    public int TargetDefinitionSourceHashVersion { get; set; }
    public string TargetDefinitionSourceHash { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class ProductionWorkOrderReleaseRequest
{
    public string WorkOrderNo { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
    public long? SourceProductDefinitionRevisionId { get; set; }
    public int SnapshotRevision { get; set; }
    public string SnapshotHash { get; set; } = string.Empty;
}

/// <summary>
/// Lifecycle-only reopen of a Released Work Order back to Draft.
/// Status, release metadata and snapshot identity are read from the locked server entity.
/// </summary>
public sealed class ProductionWorkOrderReopenRequest
{
    public string WorkOrderNo { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
    public string Reason { get; set; } = string.Empty;
}

public sealed class ProductionWorkOrderMachineSelectRequest
{
    public string WorkOrderNo { get; set; } = string.Empty;
    public byte[]? RowVersion { get; set; }
    public int SnapshotRevision { get; set; }
    public string SnapshotHash { get; set; } = string.Empty;
    public long WorkOrderOperationId { get; set; }
    public long WorkOrderMachineId { get; set; }
    public string? Reason { get; set; }
}

public sealed class ProductionWorkOrderMaterialSubstituteRequest
{
    public string WorkOrderNo { get; set; } = string.Empty;
    public byte[]? RowVersion { get; set; }
    public int SnapshotRevision { get; set; }
    public string SnapshotHash { get; set; } = string.Empty;
    public long WorkOrderMaterialId { get; set; }
    public long ReplacementSourceBomLineId { get; set; }
    public string? Reason { get; set; }
}

public sealed class ProductionWorkOrderMaterialAlternateVm
{
    public long SourceBomLineId { get; init; }
    public string ComponentCode { get; init; } = string.Empty;
    public string? ComponentDescription { get; init; }
    public bool BomDefault { get; init; }
    public string? AlternateGroupCode { get; init; }
    public decimal ComponentQtyPerParent { get; init; }
    public string? StandardUom { get; init; }
    public string SupplySource { get; init; } = string.Empty;
    public string? ComponentDefinitionCode { get; init; }
}

public interface IProductionWorkOrderService
{
    Task<IvMasterOperationResult<ProductionWorkOrderListPage>> SearchAsync(
        ProductionWorkOrderListQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderDetail>> GetAsync(
        string workOrderNo,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderPreview>> ProcessPreviewAsync(
        ProductionWorkOrderDraftRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderDetail>> SaveDraftAsync(
        ProductionWorkOrderDraftRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderDetail>> ReleaseAsync(
        string workOrderNo,
        byte[] rowVersion,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CancelDraftAsync(
        string workOrderNo,
        byte[] rowVersion,
        string reason,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CreateDraftAsync(
        ProductionWorkOrderDraftRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderDetail>> UpdateDraftHeaderAsync(
        ProductionWorkOrderHeaderUpdate request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderDetail>> RecalculateDraftScheduleAsync(
        ProductionWorkOrderRecalculateRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderRefreshPreview>> PreviewRefreshFromDefinitionAsync(
        string workOrderNo,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderDetail>> RefreshDraftFromDefinitionAsync(
        ProductionWorkOrderRefreshConfirm request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderChangeDefinitionPreview>> PreviewChangeDefinitionAsync(
        string workOrderNo,
        string targetDefinitionCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderDetail>> ConfirmChangeDefinitionAsync(
        ProductionWorkOrderChangeDefinitionConfirm request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderDetail>> ReleaseCurrentAsync(
        ProductionWorkOrderReleaseRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a Released Work Order to Draft when no production execution has started.
    /// Requires REOPEN; subsequent Draft mutations continue to require EDIT.
    /// </summary>
    Task<IvMasterOperationResult<ProductionWorkOrderDetail>> ReopenForEditAsync(
        ProductionWorkOrderReopenRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderDetail>> SelectDraftMachineAsync(
        ProductionWorkOrderMachineSelectRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IReadOnlyList<ProductionWorkOrderMaterialAlternateVm>>> GetDraftMaterialAlternatesAsync(
        string workOrderNo,
        long workOrderMaterialId,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<ProductionWorkOrderDetail>> SubstituteDraftMaterialAsync(
        ProductionWorkOrderMaterialSubstituteRequest request,
        CancellationToken cancellationToken = default);
}

