namespace ErpWeb.Core.Production;

public enum MachineScheduleAdvancedAction
{
    InsertSplit = 0,
    Hold = 1,
    ReleaseHold = 2,
    ReplanRemaining = 3,
    MaintenanceReplan = 4
}

public sealed record MachineScheduleAdvancedRequest
{
    public MachineScheduleAdvancedAction Action { get; init; }

    public long WorkOrderId { get; init; }
    public long OperationId { get; init; }
    public long WorkOrderMachineId { get; init; }
    public string MachineCode { get; init; } = string.Empty;

    public int ExpectedWorkOrderScheduleRevision { get; init; }
    public byte[] ExpectedWorkOrderRowVersion { get; init; } = [];
    public int ExpectedMachineScheduleRevision { get; init; }
    public byte[] ExpectedMachineRowVersion { get; init; } = [];

    /// <summary>Machine-local requested start. The scheduler computes the completion.</summary>
    public DateTime? RequestedStart { get; init; }

    /// <summary>Partial quantity for Insert/Split. Null or zero means the scheduled remaining quantity.</summary>
    public decimal? PartialQuantity { get; init; }

    /// <summary>Defaults to true: a real resume repeats setup + conversion, never queue.</summary>
    public bool IncludeRestartSetup { get; init; } = true;

    public string Reason { get; init; } = string.Empty;

    // Hold
    public string? HoldType { get; init; }
    public DateTime? ResumeNotBefore { get; init; }
    public long? HoldId { get; init; }

    // Maintenance
    public long? MaintenanceId { get; init; }
    public DateTime? DowntimeStart { get; init; }
    public DateTime? DowntimeEnd { get; init; }
    public string? MaintenanceType { get; init; }

    public string? PreviewToken { get; init; }
}

public sealed class MachineScheduleAdvancedSegmentPreview
{
    public long? SegmentId { get; init; }
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public long OperationId { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public long WorkOrderMachineId { get; init; }
    public string MachineCode { get; init; } = string.Empty;
    public int Sequence { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal PlannedOutputQty { get; init; }
    public decimal CycleCount { get; init; }
    public decimal CycleSlots { get; init; }
    public decimal RunMinutes { get; init; }
    public decimal SetupSeconds { get; init; }
    public decimal ConversionSeconds { get; init; }
    public decimal QueueSeconds { get; init; }
    public DateTime Start { get; init; }
    public DateTime Completion { get; init; }
    public string? Reason { get; init; }
    public bool IsExisting { get; init; }
    public IReadOnlyList<MachineScheduleAdvancedInterval> ProductiveIntervals { get; init; } = [];
}

public sealed class MachineScheduleAdvancedInterval
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
}

public sealed class MachineScheduleAdvancedWorkOrderPreview
{
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public DateTime StartBefore { get; init; }
    public DateTime CompletionBefore { get; init; }
    public DateTime? StartAfter { get; init; }
    public DateTime? CompletionAfter { get; init; }
    public DateTime? RequiredDate { get; init; }
    public string? Priority { get; init; }
    public bool LateBefore { get; init; }
    public bool LateAfter { get; init; }
    public bool HasUnscheduledRemaining { get; init; }
    public string? DrAdvisory { get; init; }
}

public sealed class MachineScheduleAdvancedPreview
{
    public MachineScheduleAdvancedAction Action { get; init; }
    public long WorkOrderId { get; init; }
    public string WorkOrderNo { get; init; } = string.Empty;
    public long TargetOperationId { get; init; }
    public string TargetOperationCode { get; init; } = string.Empty;
    public string MachineCode { get; init; } = string.Empty;

    public DateTime CurrentStart { get; init; }
    public DateTime CurrentCompletion { get; init; }
    public DateTime? ProposedStart { get; init; }
    public DateTime? ProposedCompletion { get; init; }

    public DateTime RequestedSplit { get; init; }
    public DateTime ResolvedSplit { get; init; }
    public string SplitExplanation { get; init; } = string.Empty;

    public decimal RequestedQty { get; init; }
    public decimal ScheduledQty { get; init; }
    public decimal CycleCount { get; init; }
    public decimal CycleSlots { get; init; }
    public decimal RestartOverheadSeconds { get; init; }

    public bool HasUnscheduledRemaining { get; init; }
    public DateTime? ForecastCompletion { get; init; }
    public string? UnscheduledMessage { get; init; }

    public IReadOnlyList<MachineScheduleAdvancedSegmentPreview> Segments { get; init; } = [];
    public IReadOnlyList<MachineScheduleAdvancedWorkOrderPreview> AffectedWorkOrders { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; set; } = [];
    public IReadOnlyList<string> Blockers { get; set; } = [];

    public string? PreviewToken { get; set; }
    public bool CanCommit => Blockers.Count == 0 && !string.IsNullOrWhiteSpace(PreviewToken);
}

public sealed class MachineScheduleAdvancedCommitResult
{
    public MachineScheduleAdvancedAction Action { get; init; }
    public long WorkOrderId { get; init; }
    public long AuditEventId { get; init; }
    public long? HoldId { get; init; }
    public int NewWorkOrderScheduleRevision { get; init; }
    public IReadOnlyList<long> AffectedWorkOrderIds { get; init; } = [];
    public DateTime? CommittedStart { get; init; }
    public DateTime? CommittedCompletion { get; init; }
    public bool HasUnscheduledRemaining { get; init; }
    public string Message { get; init; } = string.Empty;
}

public interface IMachineScheduleAdvancedPlanningService
{
    Task<Inventory.IvMasterOperationResult<MachineScheduleAdvancedPreview>> PreviewInsertAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default);
    Task<Inventory.IvMasterOperationResult<MachineScheduleAdvancedCommitResult>> CommitInsertAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default);

    Task<Inventory.IvMasterOperationResult<MachineScheduleAdvancedPreview>> PreviewHoldAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default);
    Task<Inventory.IvMasterOperationResult<MachineScheduleAdvancedCommitResult>> CommitHoldAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default);
    Task<Inventory.IvMasterOperationResult<MachineScheduleAdvancedPreview>> PreviewReleaseHoldAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default);
    Task<Inventory.IvMasterOperationResult<MachineScheduleAdvancedCommitResult>> CommitReleaseHoldAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default);

    Task<Inventory.IvMasterOperationResult<MachineScheduleAdvancedPreview>> PreviewReplanRemainingAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default);
    Task<Inventory.IvMasterOperationResult<MachineScheduleAdvancedCommitResult>> CommitReplanRemainingAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default);

    Task<Inventory.IvMasterOperationResult<MachineScheduleAdvancedPreview>> PreviewMaintenanceReplanAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default);
    Task<Inventory.IvMasterOperationResult<MachineScheduleAdvancedCommitResult>> CommitMaintenanceReplanAsync(
        MachineScheduleAdvancedRequest request, CancellationToken cancellationToken = default);
}
