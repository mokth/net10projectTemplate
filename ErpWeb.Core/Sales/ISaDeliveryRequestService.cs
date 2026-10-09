using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

public sealed class SaDeliveryRequestListQuery
{
    public string? SearchText { get; set; }
    public string? SoNo { get; set; }
    public string? CustomerCode { get; set; }
    public string? Status { get; set; }
    public string? LifecycleStatus { get; set; }
    public string? ProductCode { get; set; }
    public string? WarehouseCode { get; set; }
    public string? ProjectCode { get; set; }
    public string? Priority { get; set; }
    public string? FulfilmentStatus { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? BlockerCode { get; set; }
    public DateTime? RequiredDateFrom { get; set; }
    public DateTime? RequiredDateTo { get; set; }
    public string? DueMode { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; } = true;
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public static class SaDeliveryRequestDueModes
{
    public const string Open = "OPEN";
    public const string DueToday = "DUE_TODAY";
    public const string DueThisWeek = "DUE_THIS_WEEK";
    public const string Overdue = "OVERDUE";
    public const string AtRisk = "AT_RISK";
    public const string ProductionRequired = "PRODUCTION_REQUIRED";
    public const string MaterialShortage = "MATERIAL_SHORTAGE";
    public const string ReadyForDelivery = "READY_FOR_DELIVERY";
    public const string Partial = "PARTIAL";
    public const string Completed = "COMPLETED";

    public static bool IsValid(string? value) => value is Open
        or DueToday
        or DueThisWeek
        or Overdue
        or AtRisk
        or ProductionRequired
        or MaterialShortage
        or ReadyForDelivery
        or Partial
        or Completed;
}

public static class SaDeliveryRequestPriorities
{
    public const string Normal = "NORMAL";
    public const string High = "HIGH";
    public const string Urgent = "URGENT";

    public static bool IsValid(string? value) => value is Normal or High or Urgent;
}

public sealed class SaDeliveryRequestListRow
{
    public long Uid { get; init; }
    public string DeliveryRequestNo { get; init; } = string.Empty;
    public string? CustomerCode { get; init; }
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public string ProductionUom { get; init; } = string.Empty;
    public decimal RequestedQty { get; init; }
    public decimal WoAllocatedQty { get; init; }
    public decimal UnplannedQty { get; init; }
    public decimal ProducedQty { get; init; }
    public decimal DeliveredQty { get; init; }
    public decimal ReadyQty { get; init; }
    public decimal DrStockReservedQty { get; init; }
    public decimal NewShipmentReservedQty { get; init; }
    public decimal ProductionRequiredQty { get; init; }
    public decimal ProductionUnplannedQty { get; init; }
    public decimal OutstandingWoSupplyQty { get; init; }
    public string? WarehouseCode { get; init; }
    public string? ProjectCode { get; init; }
    public string? Priority { get; init; }
    public string FulfilmentStatus { get; init; } = SaDeliveryRequestFulfilmentStatuses.Open;
    public string BlockerCode { get; init; } = SaDeliveryRequestBlockerCodes.None;
    public DateTime? FulfilledDate { get; init; }
    public DateTime? ForecastReadyDate { get; init; }
    public bool IsAtRisk { get; init; }
    public DateTime RequiredDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public int SourceCount { get; init; }
    public int WorkOrderCount { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaDeliveryRequestKpis
{
    public int OpenDrCount { get; init; }
    public decimal OpenDrQty { get; init; }
    public int DueTodayCount { get; init; }
    public int DueThisWeekCount { get; init; }
    public int OverdueCount { get; init; }
    public int AtRiskCount { get; init; }
    public int ReadyForDeliveryCount { get; init; }
    public decimal ReadyForDeliveryQty { get; init; }
    public int MaterialShortageCount { get; init; }
    public int PartialFulfilmentCount { get; init; }
    public decimal OnTimeFulfilmentPercent { get; init; }
}

public sealed class SaDeliveryRequestListPage
{
    public IReadOnlyList<SaDeliveryRequestListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class SaDeliveryRequestSourceInput
{
    public string SoNo { get; set; } = string.Empty;
    public short CustRel { get; set; }
    public short SoLine { get; set; }
    public decimal AllocatedProductionQty { get; set; }
}

public class SaDeliveryRequestDraftRequest
{
    public string? DeliveryRequestNo { get; set; }
    public string? ProductCode { get; set; }
    public string? ProductionUom { get; set; }
    public decimal RequestedQty { get; set; }
    // A missing date is resolved from the earliest selected SO delivery date.
    // Keeping the CLR default here also lets the service distinguish an omitted
    // value from an explicitly supplied date.
    public DateTime RequiredDate { get; set; }
    public string? DefinitionCode { get; set; }
    public string? WarehouseCode { get; set; }
    public string? ProjectCode { get; set; }
    public string? Priority { get; set; }
    public string? Remark { get; set; }
    public IReadOnlyList<SaDeliveryRequestSourceInput> Sources { get; set; } = [];
}

public sealed class SaDeliveryRequestUpdateRequest : SaDeliveryRequestDraftRequest
{
    public long Uid { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class SaDeliveryRequestCommandRequest
{
    public long Uid { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public string? Reason { get; set; }
}

public sealed class SaDeliveryRequestSourceChangeRequest
{
    public long DeliveryRequestId { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public SaDeliveryRequestSourceInput Source { get; set; } = new();
}

public sealed class SaDeliveryRequestSourceTrace
{
    public long Uid { get; init; }
    public string SoNo { get; init; } = string.Empty;
    public short CustRel { get; init; }
    public short SoLine { get; init; }
    public string ProductCode { get; init; } = string.Empty;
    public string SourceUom { get; init; } = string.Empty;
    public string ProductionUom { get; init; } = string.Empty;
    public decimal SourceQty { get; init; }
    public decimal ProductionDemandQty { get; init; }
    public decimal OpenProductionDemandQty { get; init; }
    public decimal AllocatedProductionQty { get; init; }
    public decimal ActiveAllocatedProductionQty { get; init; }
    public decimal ActiveDrLinkedDoQty { get; init; }
    public decimal ActiveDrOutstandingQty { get; init; }
    public decimal AvailableForDr { get; init; }
    public string? CustomerCode { get; init; }
    public DateTime? RequestedDeliveryDate { get; init; }
    public bool IsActive { get; init; }
    public string? WarehouseCode { get; init; }
    public string? ProjectCode { get; init; }
    public DateTime? ReleasedDate { get; init; }
    public string? ReleasedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaDeliveryRequestWorkOrderTrace
{
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public decimal AllocatedQty { get; init; }
    public bool IsActive { get; init; }
    public string Status { get; init; } = string.Empty;
    public decimal PlannedQty { get; init; }
    public decimal GoodQty { get; init; }
    public DateTime? ReleasedDate { get; init; }
    public DateTime? CreatedDate { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SaDeliveryRequestMaterialShortageTrace
{
    public long WorkOrderMaterialId { get; init; }
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public string ComponentCode { get; init; } = string.Empty;
    public string? ComponentDescription { get; init; }
    public string? BaseUom { get; init; }
    public string IssueMethod { get; init; } = string.Empty;
    public string SupplySource { get; init; } = string.Empty;
    public decimal RequiredBaseQty { get; init; }
    public decimal AvailableBaseQty { get; init; }
    public decimal PhysicalShortBaseQty { get; init; }
    public decimal OpenProcurementBaseQty { get; init; }
    public decimal NetProcurementRequiredBaseQty { get; init; }
    public bool IsConsistent { get; init; }
    public string? InconsistencyMessage { get; init; }
    public IReadOnlyList<SaDeliveryRequestProcurementTraceLine> Procurement { get; init; } = [];
}

public sealed class SaDeliveryRequestProcurementTraceLine
{
    public string? PrNo { get; init; }
    public short? PrLineNo { get; init; }
    public string? PoNo { get; init; }
    public short? PoRelNo { get; init; }
    public decimal PrStdQty { get; init; }
    public decimal PrUnorderedStdQty { get; init; }
    public decimal PoOrderedStdQty { get; init; }
    public decimal PoOpenStdQty { get; init; }
    public DateTime? EtaDate { get; init; }
    public bool IsConsistent { get; init; }
}

public sealed class SaDeliveryRequestAuditTrace
{
    public long Uid { get; init; }
    public string EventType { get; init; } = string.Empty;
    public long? SourceId { get; init; }
    public long? WorkOrderId { get; init; }
    public string? Reason { get; init; }
    public DateTime OccurredDate { get; init; }
    public string ActorUserId { get; init; } = string.Empty;
}

public sealed class SaDeliveryRequestDetail
{
    public long Uid { get; init; }
    public string DeliveryRequestNo { get; init; } = string.Empty;
    public string CompanyCode { get; init; } = string.Empty;
    public string BranchCode { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public string ProductionUom { get; init; } = string.Empty;
    public decimal RequestedQty { get; init; }
    public decimal WoAllocatedQty { get; init; }
    public decimal UnplannedQty { get; init; }
    public decimal ProducedQty { get; init; }
    public decimal DeliveredQty { get; init; }
    public decimal ReadyQty { get; init; }
    public decimal DrStockReservedQty { get; init; }
    public decimal NewShipmentReservedQty { get; init; }
    public decimal ProductionRequiredQty { get; init; }
    public decimal ProductionUnplannedQty { get; init; }
    public decimal OutstandingWoSupplyQty { get; init; }
    public string FulfilmentStatus { get; init; } = SaDeliveryRequestFulfilmentStatuses.Open;
    public string BlockerCode { get; init; } = SaDeliveryRequestBlockerCodes.None;
    public DateTime? FulfilledDate { get; init; }
    public DateTime? ForecastReadyDate { get; init; }
    public bool IsAtRisk { get; init; }
    public DateTime RequiredDate { get; init; }
    public string? DefinitionCode { get; init; }
    public string? WarehouseCode { get; init; }
    public string? ProjectCode { get; init; }
    public string? Priority { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Remark { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public IReadOnlyList<SaDeliveryRequestSourceTrace> Sources { get; init; } = [];
    public IReadOnlyList<SaDeliveryRequestWorkOrderTrace> WorkOrders { get; init; } = [];
    public IReadOnlyList<SaDeliveryRequestMaterialShortageTrace> MaterialShortages { get; init; } = [];
    public IReadOnlyList<SaDeliveryRequestAuditTrace> AuditEvents { get; init; } = [];
}

public sealed class SaDeliveryRequestEligibleSourceQuery
{
    public string? SoNo { get; set; }
    public short? CustRel { get; set; }
    public string? ProductCode { get; set; }
    public string? WarehouseCode { get; set; }
    public string? ProjectCode { get; set; }
    public string? CustomerCode { get; set; }
}

public sealed class SaDeliveryRequestEligibleSource
{
    public string CompanyCode { get; init; } = string.Empty;
    public string BranchCode { get; init; } = string.Empty;
    public string SoNo { get; init; } = string.Empty;
    public short CustRel { get; init; }
    public short SoLine { get; init; }
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public string ProductionUom { get; init; } = string.Empty;
    public decimal ProductionDemandQty { get; init; }
    public decimal ActiveDrAllocatedQty { get; init; }
    public decimal OpenProductionDemandQty { get; init; }
    public decimal ActiveDrLinkedDoQty { get; init; }
    public decimal ActiveDrOutstandingQty { get; init; }
    public decimal AvailableForDr { get; init; }
    public string? CustomerCode { get; init; }
    public DateTime? RequestedDeliveryDate { get; init; }
    public string? WarehouseCode { get; init; }
    public string? ProjectCode { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public interface ISaDeliveryRequestService
{
    Task<IvMasterOperationResult<SaDeliveryRequestListPage>> SearchAsync(
        SaDeliveryRequestListQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaDeliveryRequestKpis>> GetKpisAsync(
        SaDeliveryRequestListQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaDeliveryRequestDetail>> GetAsync(
        long uid,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<IReadOnlyList<SaDeliveryRequestEligibleSource>>> ListEligibleSalesOrderDemandAsync(
        SaDeliveryRequestEligibleSourceQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaDeliveryRequestDetail>> CreateDraftAsync(
        SaDeliveryRequestDraftRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaDeliveryRequestDetail>> UpdateDraftAsync(
        SaDeliveryRequestUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaDeliveryRequestDetail>> AddSourceAsync(
        SaDeliveryRequestSourceChangeRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaDeliveryRequestDetail>> RemoveSourceAsync(
        SaDeliveryRequestSourceChangeRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaDeliveryRequestDetail>> ReleaseAsync(
        SaDeliveryRequestCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<SaDeliveryRequestDetail>> CancelAsync(
        SaDeliveryRequestCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<long>> DeleteDraftAsync(
        SaDeliveryRequestCommandRequest request,
        CancellationToken cancellationToken = default);
}
