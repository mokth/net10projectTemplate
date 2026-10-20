namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// One productive interval reserved by a selected Work Order machine operation. Intervals use
/// half-open [StartDateTime, EndDateTime) semantics and are copied directly from the canonical
/// calendar scheduler's ConsumedIntervals result.
/// </summary>
public sealed class ProductionMachineScheduleAllocation
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string MachineCode { get; set; } = string.Empty;
    public long WorkOrderId { get; set; }
    public long WorkOrderOperationId { get; set; }
    public long WorkOrderMachineId { get; set; }

    /// <summary>
    /// Business planning segment this productive fragment belongs to. Nullable so legacy/backfill
    /// rows keep working; segment-aware rows always link to a current segment.
    /// </summary>
    public long? MachineScheduleSegmentId { get; set; }

    public int ScheduleRevision { get; set; }
    public int FragmentNo { get; set; }
    public DateTime StartDateTime { get; set; }
    public DateTime EndDateTime { get; set; }
    public string? ScheduleSourceHash { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
}
