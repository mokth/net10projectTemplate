using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

internal sealed class PlannedSegment
{
    public ProductionMachineScheduleSegment? Existing { get; init; }
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public long OperationId { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public long WorkOrderMachineId { get; init; }
    public string MachineCode { get; init; } = string.Empty;
    public int Sequence { get; set; }
    public string Kind { get; init; } = ProductionMachineScheduleSegmentKinds.Original;
    public string Status { get; init; } = ProductionMachineScheduleSegmentStatuses.Planned;
    public long? SourceSegmentId { get; init; }
    public decimal Qty { get; init; }
    public string? Uom { get; init; }
    public decimal CycleCount { get; init; }
    public decimal CycleSlots { get; init; }
    public decimal RunMinutes { get; init; }
    public decimal SetupSeconds { get; init; }
    public decimal ConversionSeconds { get; init; }
    public decimal QueueSeconds { get; init; }
    public DateTime Start { get; init; }
    public DateTime Completion { get; init; }
    public string SourceHash { get; init; } = string.Empty;
    public DateTime? HorizonStart { get; init; }
    public DateTime? HorizonEnd { get; init; }
    public IReadOnlyList<TimeInterval> Fragments { get; init; } = [];
    public string Reason { get; init; } = string.Empty;
}

internal sealed class AdvancedOrderPlan
{
    public ProductionWorkOrder Order { get; init; } = new();
    public ProductionWorkOrderRouteStep? TargetStep { get; init; }
    public ProductionWorkOrderOperation? TargetOperation { get; init; }
    public HashSet<long> MovingOperationIds { get; } = [];
    public (DateTime Start, DateTime Completion) Before { get; init; }
    public DateTime? AfterStart { get; set; }
    public DateTime? AfterCompletion { get; set; }
    public bool HasUnscheduledRemaining { get; set; }
}

internal sealed class MachineScheduleAdvancedPlan
{
    public MachineScheduleAdvancedAction Action { get; init; }
    public MachineScheduleAdvancedRequest Request { get; init; } = new();
    public InventoryTenantScope Scope { get; init; } = null!;
    public AppDbContext Db { get; init; } = null!;
    public IReadOnlyList<AdvancedOrderPlan> Orders { get; set; } = [];
    public ProductionWorkOrder TargetOrder { get; set; } = new();
    public ProductionWorkOrderOperation TargetOperation { get; set; } = new();
    public ProductionWorkOrderMachine TargetMachine { get; set; } = new();
    public ProductionWorkOrderRouteStep TargetStep { get; set; } = new();

    public Dictionary<long, List<PlannedSegment>> SegmentsByMachine { get; } = [];
    public HashSet<long> AffectedMachineIds { get; } = [];
    public List<ProductionMachineScheduleSegment> SegmentsToSupersede { get; } = [];
    public Dictionary<long, MachineSchedulePlacement> Placements { get; } = [];
    public Dictionary<long, MachineScheduleDateRangeProjection> Projections { get; } = [];
    public List<ProductionWorkOrderScheduleHold> HoldsToAdd { get; } = [];
    public ProductionWorkOrderScheduleHold? HoldToResolve { get; set; }
    public bool HasUnscheduledRemaining { get; set; }
    public bool TargetHasNoFutureSegment { get; set; }
    public DateTime? ForecastCompletion { get; set; }
    public string EventType { get; set; } = ProductionAuditEventTypes.ScheduleChanged;
    public string? HoldResolutionKind { get; set; }
    public string SplitExplanation { get; set; } = string.Empty;
    public DateTime RequestedSplit { get; set; }
    public DateTime ResolvedSplit { get; set; }
    public decimal RequestedQty { get; set; }
    public decimal ScheduledQty { get; set; }
    public decimal RestartOverheadSeconds { get; set; }
    public List<string> Warnings { get; } = [];
    public List<string> Blockers { get; } = [];
    public MachineScheduleAdvancedPreview Preview { get; set; } = new();

    public IReadOnlyList<PlannedSegment> AllSegments =>
        SegmentsByMachine.Values.SelectMany(x => x).ToList();

    public IReadOnlyList<long> AffectedWorkOrderIds =>
        Orders.Select(x => x.Order.Uid).Where(x => x > 0).Distinct().Order().ToList();

    public bool CanCommit => Blockers.Count == 0;
}
