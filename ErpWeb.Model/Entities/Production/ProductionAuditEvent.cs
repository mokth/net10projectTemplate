namespace ErpWeb.Model.Entities.Production;

/// <summary>Append-only lifecycle/audit event for a Work Order.</summary>
public class ProductionAuditEvent
{
    public long Uid { get; set; }
    public long WorkOrderId { get; set; }
    public ProductionWorkOrder? WorkOrder { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public int SnapshotRevision { get; set; }
    public string? Reason { get; set; }
    public string? DetailsJson { get; set; }
    public DateTime OccurredDate { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
}

