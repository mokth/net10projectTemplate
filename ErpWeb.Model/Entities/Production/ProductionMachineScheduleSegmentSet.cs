namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// One version of the business plan for a selected Work Order machine. Historical sets remain
/// available after a replan; only the current set describes today's scheduling intent.
/// Physical capacity is still reserved by PrMachineScheduleAllocation.
/// </summary>
public sealed class ProductionMachineScheduleSegmentSet
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long WorkOrderId { get; set; }
    public long WorkOrderOperationId { get; set; }
    public long WorkOrderMachineId { get; set; }
    public string MachineCode { get; set; } = string.Empty;
    public string Mode { get; set; } = ProductionMachineScheduleSegmentSetModes.FullPlan;
    public string PlanningState { get; set; } = ProductionMachineSchedulePlanningStates.Scheduled;
    public bool IsCurrent { get; set; }
    public decimal PlannedQtyBasis { get; set; }
    public string? OutputUom { get; set; }
    public DateTime? CalendarHorizonStart { get; set; }
    public DateTime? CalendarHorizonEnd { get; set; }
    public string? ScheduleSourceHash { get; set; }
    public int MachineScheduleRevision { get; set; }
    public string? Reason { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime? SupersededDate { get; set; }
    public string? SupersededBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public static class ProductionMachineScheduleSegmentSetModes
{
    public const string FullPlan = "FULL_PLAN";
    public const string RemainingOnly = "REMAINING_ONLY";
}

public static class ProductionMachineSchedulePlanningStates
{
    public const string Scheduled = "SCHEDULED";
    public const string HeldUnscheduled = "HELD_UNSCHEDULED";
    public const string MaintenanceUnscheduled = "MAINTENANCE_UNSCHEDULED";
    public const string ReplanRequired = "REPLAN_REQUIRED";
    public const string Completed = "COMPLETED";
}

/// <summary>Explicit one-to-one assignment of a productive allocation to its business segment.</summary>
public sealed class ProductionMachineScheduleAllocationSegment
{
    public long Uid { get; set; }
    public long AllocationId { get; set; }
    public long SegmentId { get; set; }
    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
