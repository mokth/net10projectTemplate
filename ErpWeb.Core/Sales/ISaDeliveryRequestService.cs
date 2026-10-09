using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Sales;

public sealed class SaDeliveryRequestListQuery
{
    public string? SearchText { get; set; }
    public string? Status { get; set; }
    public string? ProductCode { get; set; }
    public DateTime? RequiredDateFrom { get; set; }
    public DateTime? RequiredDateTo { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; } = true;
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class SaDeliveryRequestListRow
{
    public long Uid { get; init; }
    public string DeliveryRequestNo { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public string? ProductDescription { get; init; }
    public string ProductionUom { get; init; } = string.Empty;
    public decimal RequestedQty { get; init; }
    public decimal WoAllocatedQty { get; init; }
    public decimal UnplannedQty { get; init; }
    public decimal ProducedQty { get; init; }
    public DateTime RequiredDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public int SourceCount { get; init; }
    public int WorkOrderCount { get; init; }
    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
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
    public decimal AllocatedProductionQty { get; init; }
    public decimal ActiveAllocatedProductionQty { get; init; }
    public decimal AvailableForDr { get; init; }
    public string? CustomerCode { get; init; }
    public DateTime? RequestedDeliveryDate { get; init; }
    public bool IsActive { get; init; }
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
    public IReadOnlyList<SaDeliveryRequestAuditTrace> AuditEvents { get; init; } = [];
}

public sealed class SaDeliveryRequestEligibleSourceQuery
{
    public string? SoNo { get; set; }
    public short? CustRel { get; set; }
    public string? ProductCode { get; set; }
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
    public decimal AvailableForDr { get; init; }
    public string? CustomerCode { get; init; }
    public DateTime? RequestedDeliveryDate { get; init; }
    public string? WarehouseCode { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public interface ISaDeliveryRequestService
{
    Task<IvMasterOperationResult<SaDeliveryRequestListPage>> SearchAsync(
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
