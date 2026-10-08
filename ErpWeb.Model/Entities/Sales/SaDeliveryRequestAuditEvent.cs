namespace ErpWeb.Model.Entities.Sales;

/// <summary>Append-only Delivery Request lifecycle and lineage audit.</summary>
public class SaDeliveryRequestAuditEvent
{
    public long Uid { get; set; }
    public long DeliveryRequestId { get; set; }
    public SaDeliveryRequest? DeliveryRequest { get; set; }
    public string EventType { get; set; } = string.Empty;
    public long? SourceId { get; set; }
    public long? WorkOrderId { get; set; }
    public string? DetailsJson { get; set; }
    public string? Reason { get; set; }
    public DateTime OccurredDate { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
}

public static class SaDeliveryRequestAuditEventTypes
{
    public const string Created = "CREATED";
    public const string SourceAdded = "SOURCE_ADDED";
    public const string SourceRemoved = "SOURCE_REMOVED";
    public const string Updated = "UPDATED";
    public const string Released = "RELEASED";
    public const string Cancelled = "CANCELLED";
    public const string WorkOrderCreated = "WORK_ORDER_CREATED";
    public const string AllocationChanged = "ALLOCATION_CHANGED";
    public const string AllocationDeleted = "ALLOCATION_DELETED";
    public const string Deleted = "DELETED";
}
