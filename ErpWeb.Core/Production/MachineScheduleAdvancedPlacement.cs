using ErpWeb.Core.Planning;

namespace ErpWeb.Core.Production;

internal sealed class MachineSchedulePlacement
{
    public DateTime Start { get; init; }
    public DateTime Completion { get; init; }
    public IReadOnlyList<TimeInterval> Fragments { get; init; } = [];
    public string SourceHash { get; init; } = string.Empty;
    public DateOnly HorizonStart { get; init; }
    public DateOnly HorizonEnd { get; init; }
    public long? CalendarSourceId { get; init; }
    public DateTime? CalendarSourceLastModified { get; init; }
    public string? Error { get; init; }
    public bool Succeeded => Error is null;

    public static MachineSchedulePlacement Failed(string message) => new() { Error = message };
}

/// <summary>
/// Forward placement against the same calendar scheduler used by Release and the simple
/// rescheduler. Occupied intervals are always persisted allocation fragments, never a raw
/// calendar reconstruction.
/// </summary>
internal sealed class MachineScheduleAdvancedPlacementEngine
{
    private readonly IProductionCalendarScheduleDataLoader _machineCalendars;
    private readonly IWorkOrderCalendarProvider _plantCalendars;

    public MachineScheduleAdvancedPlacementEngine(
        IProductionCalendarScheduleDataLoader machineCalendars,
        IWorkOrderCalendarProvider plantCalendars)
    {
        _machineCalendars = machineCalendars;
        _plantCalendars = plantCalendars;
    }

    public async Task<MachineSchedulePlacement> PlaceMachineAsync(
        string companyCode,
        string machineCode,
        DateTime anchor,
        TimeSpan duration,
        Dictionary<string, List<TimeInterval>> occupiedByMachine,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
            return MachineSchedulePlacement.Failed($"Machine {machineCode} has no positive schedule duration.");
        if (anchor == default)
            return MachineSchedulePlacement.Failed($"Machine {machineCode} has no valid placement anchor.");

        var key = Normalize(machineCode);
        if (!occupiedByMachine.TryGetValue(key, out var occupied))
        {
            occupied = [];
            occupiedByMachine[key] = occupied;
        }

        var span = WorkOrderSchedulingHorizon.InitialDays;
        while (true)
        {
            DateOnly horizonStart;
            DateOnly horizonEnd;
            try
            {
                var anchorDate = DateOnly.FromDateTime(anchor);
                horizonStart = anchorDate.AddDays(-WorkOrderSchedulingHorizon.OvernightBufferDays);
                horizonEnd = anchorDate.AddDays(span);
            }
            catch (ArgumentOutOfRangeException)
            {
                return MachineSchedulePlacement.Failed($"Machine {machineCode} is outside the supported scheduling horizon.");
            }

            try
            {
                var data = await _machineCalendars.LoadAsync(
                    companyCode, machineCode, horizonStart, horizonEnd, cancellationToken);
                var hash = WorkOrderScheduleSourceHasher.Compute(data, horizonStart, horizonEnd);
                var effective = ProductionCalendarScheduler.ExcludeOccupiedIntervals(data, occupied);
                var scheduled = ProductionCalendarScheduler.ScheduleMachine(
                    effective, anchor, duration, ScheduleDirection.Forward);
                return new MachineSchedulePlacement
                {
                    Start = scheduled.ActualStart,
                    Completion = scheduled.ActualCompletion,
                    Fragments = scheduled.ConsumedIntervals,
                    SourceHash = hash,
                    HorizonStart = horizonStart,
                    HorizonEnd = horizonEnd
                };
            }
            catch (ScheduleFailureException) when (span < WorkOrderSchedulingHorizon.MaxDays)
            {
                span = Math.Min(span + WorkOrderSchedulingHorizon.ExtensionDays, WorkOrderSchedulingHorizon.MaxDays);
            }
            catch (ScheduleFailureException ex)
            {
                return MachineSchedulePlacement.Failed(
                    $"Machine {machineCode} has no usable capacity within {WorkOrderSchedulingHorizon.MaxDays} days. {ex.Message}");
            }
        }
    }

    public async Task<MachineSchedulePlacement> PlacePlantAsync(
        string companyCode,
        string? branchCode,
        DateTime anchor,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
            return MachineSchedulePlacement.Failed("The operation has no positive schedule duration.");

        var span = WorkOrderSchedulingHorizon.InitialDays;
        while (true)
        {
            DateOnly horizonStart;
            DateOnly horizonEnd;
            try
            {
                var anchorDate = DateOnly.FromDateTime(anchor);
                horizonStart = anchorDate.AddDays(-WorkOrderSchedulingHorizon.OvernightBufferDays);
                horizonEnd = anchorDate.AddDays(span);
            }
            catch (ArgumentOutOfRangeException)
            {
                return MachineSchedulePlacement.Failed("The operation is outside the supported scheduling horizon.");
            }

            try
            {
                var slice = await _plantCalendars.LoadPlantAsync(
                    companyCode, branchCode, horizonStart, horizonEnd, cancellationToken);
                var hash = WorkOrderScheduleSourceHasher.Compute(slice.Data, horizonStart, horizonEnd);
                var scheduled = ProductionCalendarScheduler.ScheduleMachine(
                    slice.Data, anchor, duration, ScheduleDirection.Forward);
                return new MachineSchedulePlacement
                {
                    Start = scheduled.ActualStart,
                    Completion = scheduled.ActualCompletion,
                    Fragments = scheduled.ConsumedIntervals,
                    SourceHash = hash,
                    HorizonStart = horizonStart,
                    HorizonEnd = horizonEnd,
                    CalendarSourceId = slice.SourceId,
                    CalendarSourceLastModified = slice.LastModified
                };
            }
            catch (ScheduleFailureException) when (span < WorkOrderSchedulingHorizon.MaxDays)
            {
                span = Math.Min(span + WorkOrderSchedulingHorizon.ExtensionDays, WorkOrderSchedulingHorizon.MaxDays);
            }
            catch (ScheduleFailureException ex)
            {
                return MachineSchedulePlacement.Failed(
                    $"The operation has no usable calendar capacity within {WorkOrderSchedulingHorizon.MaxDays} days. {ex.Message}");
            }
        }
    }

    internal static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
