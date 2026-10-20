namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Business planning segment above the physical allocation fragments
/// (<c>PrMachineScheduleSegment</c>).
/// <para>
/// A segment answers <i>what production chunk is planned and why it exists</i>; an allocation
/// fragment answers <i>what exact machine-working interval is occupied</i>. The two meanings are
/// deliberately separate. Segments never drive production quantity, stock, WIP, FG, or costing.
/// </para>
/// <para>
/// Identity columns are scalar IDs without foreign keys. Draft Refresh/Change Definition can
/// replace the snapshot child rows an FK would point at, and historical segment rows must survive
/// that replacement for audit.
/// </para>
/// </summary>
public sealed class ProductionMachineScheduleSegment
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;

    public long WorkOrderId { get; set; }
    public long WorkOrderOperationId { get; set; }
    public long WorkOrderMachineId { get; set; }
    public string MachineCode { get; set; } = string.Empty;

    public int SegmentSequence { get; set; }

    /// <summary>ORIGINAL | MANUAL_SPLIT | URGENT_INSERT | RESUME | HOLD_RESUME | MAINTENANCE_RESUME | REMAINING_REPLAN.</summary>
    public string SegmentKind { get; set; } = ProductionMachineScheduleSegmentKinds.Original;

    /// <summary>PLANNED | EXECUTION_LOCKED | SUPERSEDED.</summary>
    public string SegmentStatus { get; set; } = ProductionMachineScheduleSegmentStatuses.Planned;

    /// <summary>The segment this positive-remaining replacement was derived from, when known.</summary>
    public long? SourceSegmentId { get; set; }

    public decimal PlannedOutputQty { get; set; }
    public string? OutputUom { get; set; }

    public decimal PlannedCycleCount { get; set; }
    public decimal PlannedCycleSlots { get; set; }
    public decimal ScheduledRunMinutes { get; set; }

    public decimal ScheduledSetupSeconds { get; set; }
    public decimal ScheduledConversionSeconds { get; set; }
    public decimal ScheduledQueueSeconds { get; set; }

    public DateTime PlannedStartDateTime { get; set; }
    public DateTime PlannedCompletionDateTime { get; set; }

    public DateTime? CalendarHorizonStart { get; set; }
    public DateTime? CalendarHorizonEnd { get; set; }

    /// <summary>Segment-scoped calendar/source hash.</summary>
    public string? SegmentScheduleSourceHash { get; set; }

    /// <summary>Selected-machine schedule revision this segment belongs to.</summary>
    public int MachineScheduleRevision { get; set; }

    public string? ReasonCode { get; set; }
    public string? Reason { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime? SupersededDate { get; set; }
    public string? SupersededBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Active or historical pause/hold against a selected machine operation
/// (<c>PrWorkOrderScheduleHold</c>). Holds affect planning capacity only; the Work Order status
/// stays Released/InProgress.
/// </summary>
public sealed class ProductionWorkOrderScheduleHold
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;

    public long WorkOrderId { get; set; }
    public long WorkOrderOperationId { get; set; }
    public long WorkOrderMachineId { get; set; }

    /// <summary>MATERIAL_SHORTAGE | QUALITY_HOLD | CUSTOMER_HOLD | OTHER.</summary>
    public string HoldType { get; set; } = ProductionScheduleHoldTypes.Other;

    /// <summary>ACTIVE | RELEASED | CANCELLED.</summary>
    public string Status { get; set; } = ProductionScheduleHoldStatuses.Active;

    public DateTime HoldFrom { get; set; }

    /// <summary>Known resume boundary. Null means the completion is genuinely unknown.</summary>
    public DateTime? ResumeNotBefore { get; set; }

    public string? Reason { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ReleasedDate { get; set; }
    public string? ReleasedBy { get; set; }
    public DateTime? CancelledDate { get; set; }
    public string? CancelledBy { get; set; }
    public string? ResolutionReason { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

public static class ProductionMachineScheduleSegmentKinds
{
    public const string Original = "ORIGINAL";
    public const string ManualSplit = "MANUAL_SPLIT";
    public const string UrgentInsert = "URGENT_INSERT";
    public const string Resume = "RESUME";
    public const string HoldResume = "HOLD_RESUME";
    public const string MaintenanceResume = "MAINTENANCE_RESUME";
    public const string RemainingReplan = "REMAINING_REPLAN";

    public static readonly string[] All =
    [
        Original, ManualSplit, UrgentInsert, Resume, HoldResume, MaintenanceResume, RemainingReplan
    ];

    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);

    public static bool IsResumeKind(string? value) =>
        value is Resume or HoldResume or MaintenanceResume;
}

public static class ProductionMachineScheduleSegmentStatuses
{
    public const string Planned = "PLANNED";
    public const string ExecutionLocked = "EXECUTION_LOCKED";
    public const string Superseded = "SUPERSEDED";

    public static readonly string[] All = [Planned, ExecutionLocked, Superseded];

    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);
}

public static class ProductionScheduleHoldTypes
{
    public const string MaterialShortage = "MATERIAL_SHORTAGE";
    public const string QualityHold = "QUALITY_HOLD";
    public const string CustomerHold = "CUSTOMER_HOLD";
    public const string Other = "OTHER";

    public static readonly string[] All = [MaterialShortage, QualityHold, CustomerHold, Other];

    public static bool IsKnown(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);
}

public static class ProductionScheduleHoldStatuses
{
    public const string Active = "ACTIVE";
    public const string Released = "RELEASED";
    public const string Cancelled = "CANCELLED";

    public static bool IsActive(string? value) =>
        string.Equals(value, Active, StringComparison.Ordinal);
}
