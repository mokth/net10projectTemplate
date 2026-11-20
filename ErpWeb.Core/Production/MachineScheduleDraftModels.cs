namespace ErpWeb.Core.Production;

public enum MachineScheduleDraftCommandKind
{
    Move = 0,
    InsertSplit = 1,
    Hold = 2,
    ReleaseHold = 3,
    ReplanRemaining = 4,
    MaintenanceReplan = 5
}

public sealed record MachineScheduleDraftCommand
{
    public Guid CommandId { get; init; } = Guid.NewGuid();
    public MachineScheduleDraftCommandKind Kind { get; init; }

    public long WorkOrderId { get; init; }
    public long OperationId { get; init; }
    public long WorkOrderMachineId { get; init; }
    public string MachineCode { get; init; } = string.Empty;
    public string WorkOrderNo { get; init; } = string.Empty;

    public DateTime? RequestedStart { get; init; }
    public decimal? PartialQuantity { get; init; }
    public bool IncludeRestartSetup { get; init; } = true;
    public string Reason { get; init; } = string.Empty;

    public string? HoldType { get; init; }
    public DateTime? ResumeNotBefore { get; init; }
    public long? HoldId { get; init; }

    public long? MaintenanceId { get; init; }
    public DateTime? DowntimeStart { get; init; }
    public DateTime? DowntimeEnd { get; init; }
    public string? MaintenanceType { get; init; }

    public MachineScheduleAdvancedRequest ToAdvancedRequest(
        MachineScheduleDraftExpectedVersion expected) => new()
    {
        Action = Kind switch
        {
            MachineScheduleDraftCommandKind.InsertSplit => MachineScheduleAdvancedAction.InsertSplit,
            MachineScheduleDraftCommandKind.Hold => MachineScheduleAdvancedAction.Hold,
            MachineScheduleDraftCommandKind.ReleaseHold => MachineScheduleAdvancedAction.ReleaseHold,
            MachineScheduleDraftCommandKind.ReplanRemaining => MachineScheduleAdvancedAction.ReplanRemaining,
            MachineScheduleDraftCommandKind.MaintenanceReplan => MachineScheduleAdvancedAction.MaintenanceReplan,
            _ => throw new InvalidOperationException($"Draft command kind {Kind} is not an advanced single action.")
        },
        WorkOrderId = WorkOrderId,
        OperationId = OperationId,
        WorkOrderMachineId = WorkOrderMachineId,
        MachineCode = MachineCode,
        ExpectedWorkOrderScheduleRevision = expected.ExpectedWorkOrderScheduleRevision,
        ExpectedWorkOrderRowVersion = expected.ExpectedWorkOrderRowVersion,
        ExpectedMachineScheduleRevision = expected.ExpectedMachineScheduleRevision,
        ExpectedMachineRowVersion = expected.ExpectedMachineRowVersion,
        RequestedStart = RequestedStart,
        PartialQuantity = PartialQuantity,
        IncludeRestartSetup = IncludeRestartSetup,
        Reason = Reason,
        HoldType = HoldType,
        ResumeNotBefore = ResumeNotBefore,
        HoldId = HoldId,
        MaintenanceId = MaintenanceId,
        DowntimeStart = DowntimeStart,
        DowntimeEnd = DowntimeEnd,
        MaintenanceType = MaintenanceType
    };
}

public sealed record MachineScheduleDraftExpectedVersion
{
    public long WorkOrderId { get; init; }
    public long WorkOrderMachineId { get; init; }
    public int ExpectedWorkOrderScheduleRevision { get; init; }
    public byte[] ExpectedWorkOrderRowVersion { get; init; } = [];
    public int ExpectedMachineScheduleRevision { get; init; }
    public byte[] ExpectedMachineRowVersion { get; init; } = [];
}

public sealed record MachineScheduleDraftRequest
{
    public string BoardBaseFingerprint { get; init; } = string.Empty;
    public IReadOnlyList<MachineScheduleDraftCommand> Commands { get; init; } = [];
    public IReadOnlyList<MachineScheduleDraftExpectedVersion> ExpectedVersions { get; init; } = [];
    public string? PreviewToken { get; init; }
}

public sealed record MachineScheduleDraftHiddenInterval
{
    public string MachineCode { get; init; } = string.Empty;
    public long? WorkOrderId { get; init; }
    public long? WorkOrderMachineId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool IsExternal { get; init; }
    public bool IsMaintenance { get; init; }
    public string? Label { get; init; }
}

public sealed record MachineScheduleDraftConflict
{
    public string MachineCode { get; init; } = string.Empty;
    public string AppointmentIdA { get; init; } = string.Empty;
    public string AppointmentIdB { get; init; } = string.Empty;
    public long? WorkOrderIdA { get; init; }
    public long? WorkOrderIdB { get; init; }
    public string WorkOrderNoA { get; init; } = string.Empty;
    public string WorkOrderNoB { get; init; } = string.Empty;
    public DateTime OverlapStart { get; init; }
    public DateTime OverlapEnd { get; init; }
    public bool IsOffFilterConflict { get; init; }
    public bool IsExternalOrMaintenanceConflict { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed record MachineScheduleDraftSnapshot
{
    public IReadOnlyList<MachineScheduleDraftCommand> PendingCommands { get; init; } = [];
    public IReadOnlyList<MachineSchedulePlannerAppointment> DraftAppointments { get; init; } = [];
    public IReadOnlyList<MachineScheduleDraftConflict> Conflicts { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class MachineScheduleDraftPreview
{
    public string BoardBaseFingerprint { get; init; } = string.Empty;
    public IReadOnlyList<MachineScheduleAdvancedSegmentPreview> ResultingSegments { get; init; } = [];
    public IReadOnlyList<MachineScheduleAdvancedWorkOrderPreview> AffectedWorkOrders { get; init; } = [];
    public IReadOnlyList<MachineScheduleDraftConflict> Conflicts { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; set; } = [];
    public IReadOnlyList<string> Blockers { get; set; } = [];
    public string? PreviewToken { get; set; }
    public bool CanCommit => Blockers.Count == 0 && Conflicts.Count == 0 && !string.IsNullOrWhiteSpace(PreviewToken);
}

public sealed class MachineScheduleDraftCommitResult
{
    public int AppliedCommandCount { get; init; }
    public int AffectedWorkOrderCount { get; init; }
    public IReadOnlyList<long> AffectedWorkOrderIds { get; init; } = [];
    public bool HasUnscheduledRemaining { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<MachineScheduleAdvancedWorkOrderPreview> AffectedWorkOrders { get; init; } = [];
    public string Message { get; init; } = string.Empty;
}
