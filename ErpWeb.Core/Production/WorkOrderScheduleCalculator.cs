using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>Frozen horizon-expansion contract (plan §8.3).</summary>
public static class WorkOrderSchedulingHorizon
{
    public const int InitialDays = 365;
    public const int ExtensionDays = 365;
    public const int MaxDays = 1825;
    public const int OvernightBufferDays = 1;
}

/// <summary>One calendar load over an exact horizon, plus the provenance the snapshot must store.</summary>
public sealed class WorkOrderCalendarSlice
{
    public required ScheduleMachineData Data { get; init; }
    public long? SourceId { get; init; }
    public DateTime? LastModified { get; init; }
}

/// <summary>
/// Loads the calendar that places one activity. Machine-based steps use the machine calendar;
/// fixed-duration steps use the plant calendar (plan §8.3).
/// </summary>
public interface IWorkOrderCalendarProvider
{
    Task<WorkOrderCalendarSlice> LoadMachineAsync(
        string companyCode,
        string machineCode,
        DateOnly horizonStart,
        DateOnly horizonEnd,
        CancellationToken cancellationToken = default);

    Task<WorkOrderCalendarSlice> LoadPlantAsync(
        string companyCode,
        string? branchCode,
        DateOnly horizonStart,
        DateOnly horizonEnd,
        CancellationToken cancellationToken = default);
}

public sealed class WorkOrderScheduleResult
{
    public IReadOnlyList<WorkOrderCalculationError> Errors { get; init; } = [];
    public string? Trace { get; init; }
    public bool Succeeded => Errors.Count == 0;

    public string FailureCode => Errors.Count > 0 ? Errors[0].Code : string.Empty;
    public string FailureMessage => Errors.Count > 0 ? Errors[0].Message : string.Empty;
}

/// <summary>
/// Places a version-2 snapshot on the plant calendar (plan §8).
/// <para>
/// Route steps and the processes inside them schedule by sequence group: equal sequence is
/// parallel and shares an anchor, and the next group waits for the slowest member. A
/// <c>INTERNAL_ROUTE_WIP</c> producer is an additional predecessor, so sequence is not the only
/// source of precedence. Only the selected machine option contributes duration.
/// </para>
/// </summary>
public interface IWorkOrderScheduleCalculator
{
    Task<WorkOrderScheduleResult> ScheduleAsync(
        ProductionWorkOrder workOrder,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Recomputes the schedule-source hash over each saved horizon without moving timestamps.
    /// Release compares these with the stored hashes to detect a stale calendar (plan §9.2).
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> CurrentScheduleHashesAsync(
        ProductionWorkOrder workOrder,
        CancellationToken cancellationToken = default);
}

public sealed class WorkOrderScheduleCalculator : IWorkOrderScheduleCalculator
{
    private readonly IWorkOrderCalendarProvider _calendars;

    public WorkOrderScheduleCalculator(IWorkOrderCalendarProvider calendars) => _calendars = calendars;

    public async Task<WorkOrderScheduleResult> ScheduleAsync(
        ProductionWorkOrder workOrder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workOrder);

        var graphError = ValidateGraph(workOrder);
        if (graphError is not null)
        {
            return new WorkOrderScheduleResult { Errors = [graphError] };
        }

        var direction = string.Equals(workOrder.SchedulingDirection, ProductionSchedulingDirections.Backward, StringComparison.Ordinal)
            ? ScheduleDirection.Backward
            : ScheduleDirection.Forward;
        var anchor = workOrder.ScheduleAnchorDateTime
            ?? (direction == ScheduleDirection.Backward
                ? workOrder.PlannedCompletionDateTime
                : workOrder.PlannedStartDateTime);

        var trace = new List<string>
        {
            $"direction={workOrder.SchedulingDirection}",
            $"anchor={anchor:O}",
        };

        var completions = new Dictionary<ProductionWorkOrderRouteStep, DateTime>();
        var starts = new Dictionary<ProductionWorkOrderRouteStep, DateTime>();
        var groups = direction == ScheduleDirection.Forward
            ? workOrder.RouteSteps.GroupBy(s => s.StageSequence).OrderBy(g => g.Key).ToList()
            : workOrder.RouteSteps.GroupBy(s => s.StageSequence).OrderByDescending(g => g.Key).ToList();

        var groupAnchor = anchor;
        foreach (var group in groups)
        {
            DateTime? slowest = null;
            foreach (var step in group.OrderBy(s => s.WorkCentreCode, StringComparer.Ordinal).ThenBy(s => s.OutputItemCode, StringComparer.Ordinal))
            {
                var earliest = groupAnchor;
                foreach (var predecessor in Predecessors(workOrder, step))
                {
                    if (completions.TryGetValue(predecessor, out var done) && done > earliest)
                    {
                        earliest = done;
                    }
                }

                var placed = await ScheduleRouteStepAsync(workOrder, step, earliest, direction, trace, cancellationToken);
                if (placed.Error is not null)
                {
                    return new WorkOrderScheduleResult { Errors = [placed.Error], Trace = string.Join('\n', trace) };
                }

                starts[step] = placed.Start;
                completions[step] = placed.Completion;
                slowest = slowest is null || placed.Completion > slowest ? placed.Completion : slowest;
                if (direction == ScheduleDirection.Backward)
                {
                    slowest = slowest is null || placed.Start < slowest ? placed.Start : slowest;
                }
            }

            if (slowest is not null)
            {
                groupAnchor = slowest.Value;
            }
        }

        if (starts.Count > 0)
        {
            workOrder.PlannedStartDateTime = starts.Values.Min();
            workOrder.PlannedCompletionDateTime = completions.Values.Max();
        }

        workOrder.ScheduleCalculationTrace = string.Join('\n', trace);
        return new WorkOrderScheduleResult { Trace = workOrder.ScheduleCalculationTrace };
    }

    public async Task<IReadOnlyDictionary<string, string>> CurrentScheduleHashesAsync(
        ProductionWorkOrder workOrder,
        CancellationToken cancellationToken = default)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in workOrder.RouteSteps)
        {
            foreach (var operation in step.Operations)
            {
                if (PrProcessTypes.SupportsMachine(operation.ProcessType))
                {
                    var machine = operation.Machines.FirstOrDefault(m => m.IsSelected);
                    if (machine?.CalendarHorizonStart is null || machine.CalendarHorizonEnd is null)
                    {
                        continue;
                    }

                    var slice = await _calendars.LoadMachineAsync(
                        workOrder.CompanyCode,
                        machine.MachineCode,
                        DateOnly.FromDateTime(machine.CalendarHorizonStart.Value),
                        DateOnly.FromDateTime(machine.CalendarHorizonEnd.Value),
                        cancellationToken);
                    hashes[MachineKey(operation, machine)] = WorkOrderScheduleSourceHasher.Compute(
                        slice.Data,
                        DateOnly.FromDateTime(machine.CalendarHorizonStart.Value),
                        DateOnly.FromDateTime(machine.CalendarHorizonEnd.Value));
                }
                else if (operation.CalendarHorizonStart is not null && operation.CalendarHorizonEnd is not null)
                {
                    var slice = await _calendars.LoadPlantAsync(
                        workOrder.CompanyCode,
                        workOrder.BranchCode,
                        DateOnly.FromDateTime(operation.CalendarHorizonStart.Value),
                        DateOnly.FromDateTime(operation.CalendarHorizonEnd.Value),
                        cancellationToken);
                    hashes[OperationKey(operation)] = WorkOrderScheduleSourceHasher.Compute(
                        slice.Data,
                        DateOnly.FromDateTime(operation.CalendarHorizonStart.Value),
                        DateOnly.FromDateTime(operation.CalendarHorizonEnd.Value));
                }
            }
        }

        return hashes;
    }

    private async Task<(DateTime Start, DateTime Completion, WorkOrderCalculationError? Error)> ScheduleRouteStepAsync(
        ProductionWorkOrder workOrder,
        ProductionWorkOrderRouteStep step,
        DateTime earliest,
        ScheduleDirection direction,
        List<string> trace,
        CancellationToken cancellationToken)
    {
        var groups = direction == ScheduleDirection.Forward
            ? step.Operations.GroupBy(o => o.ProcessSequence).OrderBy(g => g.Key).ToList()
            : step.Operations.GroupBy(o => o.ProcessSequence).OrderByDescending(g => g.Key).ToList();

        var cursor = earliest;
        DateTime? stepStart = null;
        DateTime? stepEnd = null;

        foreach (var group in groups)
        {
            DateTime? slowest = null;
            foreach (var operation in group.OrderBy(o => o.OperationCode, StringComparer.Ordinal))
            {
                var placed = await PlaceOperationAsync(workOrder, operation, cursor, direction, trace, cancellationToken);
                if (placed.Error is not null)
                {
                    return (default, default, placed.Error);
                }

                stepStart = stepStart is null || placed.Start < stepStart ? placed.Start : stepStart;
                stepEnd = stepEnd is null || placed.Completion > stepEnd ? placed.Completion : stepEnd;
                var boundary = direction == ScheduleDirection.Forward ? placed.Completion : placed.Start;
                slowest = slowest is null
                    || (direction == ScheduleDirection.Forward && boundary > slowest)
                    || (direction == ScheduleDirection.Backward && boundary < slowest)
                    ? boundary
                    : slowest;
            }

            if (slowest is not null)
            {
                cursor = slowest.Value;
            }
        }

        step.PlannedStartDateTime = stepStart ?? earliest;
        step.PlannedCompletionDateTime = stepEnd ?? earliest;
        trace.Add($"step {step.StageSequence} {step.WorkCentreCode} {step.PlannedStartDateTime:O} -> {step.PlannedCompletionDateTime:O}");
        return (step.PlannedStartDateTime.Value, step.PlannedCompletionDateTime.Value, null);
    }

    private async Task<(DateTime Start, DateTime Completion, WorkOrderCalculationError? Error)> PlaceOperationAsync(
        ProductionWorkOrder workOrder,
        ProductionWorkOrderOperation operation,
        DateTime anchor,
        ScheduleDirection direction,
        List<string> trace,
        CancellationToken cancellationToken)
    {
        var machine = PrProcessTypes.SupportsMachine(operation.ProcessType)
            ? operation.Machines.FirstOrDefault(m => m.IsSelected)
            : null;

        if (PrProcessTypes.SupportsMachine(operation.ProcessType) && machine is null)
        {
            return (default, default, new WorkOrderCalculationError(
                ProductionReadinessErrorCodes.MachineRequired,
                $"Operation {operation.OperationCode} has no selected machine to schedule.",
                $"PrWorkOrderOperation/{operation.OperationCode}"));
        }

        var duration = machine is null
            ? TimeSpan.FromMinutes((double)operation.StandardDurationMinutes)
            : TimeSpan.FromSeconds((double)(machine.SetupSeconds + machine.ConversionSeconds + machine.QueueSeconds))
              + TimeSpan.FromMinutes((double)machine.PlannedRunMinutes);

        var (slice, horizonStart, horizonEnd, error) = await PlaceOnCalendarAsync(
            workOrder,
            operation,
            machine,
            anchor,
            duration,
            direction,
            cancellationToken);
        if (error is not null || slice is null)
        {
            return (default, default, error);
        }

        var scheduled = ProductionCalendarScheduler.ScheduleMachine(
            slice.Data,
            anchor,
            duration,
            direction);

        var hash = WorkOrderScheduleSourceHasher.Compute(slice.Data, horizonStart, horizonEnd);
        if (machine is not null)
        {
            machine.PlannedStartDateTime = scheduled.ActualStart;
            machine.PlannedCompletionDateTime = scheduled.ActualCompletion;
            machine.CalendarSourceId = slice.SourceId;
            machine.CalendarSourceLastModified = slice.LastModified;
            machine.CalendarHorizonStart = horizonStart.ToDateTime(TimeOnly.MinValue);
            machine.CalendarHorizonEnd = horizonEnd.ToDateTime(TimeOnly.MinValue);
            machine.ScheduleSourceHash = hash;
            operation.CalendarSourceType = ProductionCalendarSourceTypes.Machine;
            operation.CalendarSourceId = slice.SourceId;
            operation.CalendarHorizonStart = machine.CalendarHorizonStart;
            operation.CalendarHorizonEnd = machine.CalendarHorizonEnd;
            operation.ScheduleSourceHash = hash;
        }
        else
        {
            operation.CalendarSourceType = ProductionCalendarSourceTypes.PlantDefault;
            operation.CalendarSourceId = slice.SourceId;
            operation.CalendarSourceLastModified = slice.LastModified;
            operation.CalendarHorizonStart = horizonStart.ToDateTime(TimeOnly.MinValue);
            operation.CalendarHorizonEnd = horizonEnd.ToDateTime(TimeOnly.MinValue);
            operation.ScheduleSourceHash = hash;
        }

        operation.PlannedStartDateTime = scheduled.ActualStart;
        operation.PlannedCompletionDateTime = scheduled.ActualCompletion;
        trace.Add($"op {operation.OperationCode} {scheduled.ActualStart:O} -> {scheduled.ActualCompletion:O} {hash[..8]}");
        return (scheduled.ActualStart, scheduled.ActualCompletion, null);
    }

    private async Task<(WorkOrderCalendarSlice? Slice, DateOnly Start, DateOnly End, WorkOrderCalculationError? Error)> PlaceOnCalendarAsync(
        ProductionWorkOrder workOrder,
        ProductionWorkOrderOperation operation,
        ProductionWorkOrderMachine? machine,
        DateTime anchor,
        TimeSpan duration,
        ScheduleDirection direction,
        CancellationToken cancellationToken)
    {
        var anchorDate = DateOnly.FromDateTime(anchor);
        var span = WorkOrderSchedulingHorizon.InitialDays;
        while (true)
        {
            var start = direction == ScheduleDirection.Forward
                ? anchorDate.AddDays(-WorkOrderSchedulingHorizon.OvernightBufferDays)
                : anchorDate.AddDays(-span);
            var end = direction == ScheduleDirection.Forward
                ? anchorDate.AddDays(span)
                : anchorDate.AddDays(WorkOrderSchedulingHorizon.OvernightBufferDays);

            try
            {
                var slice = machine is null
                    ? await _calendars.LoadPlantAsync(workOrder.CompanyCode, workOrder.BranchCode, start, end, cancellationToken)
                    : await _calendars.LoadMachineAsync(workOrder.CompanyCode, machine.MachineCode, start, end, cancellationToken);
                ProductionCalendarScheduler.ScheduleMachine(slice.Data, anchor, duration, direction);
                return (slice, start, end, null);
            }
            catch (ScheduleFailureException) when (span < WorkOrderSchedulingHorizon.MaxDays)
            {
                span = Math.Min(span + WorkOrderSchedulingHorizon.ExtensionDays, WorkOrderSchedulingHorizon.MaxDays);
            }
            catch (ScheduleFailureException ex)
            {
                return (null, default, default, new WorkOrderCalculationError(
                    ProductionReadinessErrorCodes.CalendarCoverageMissing,
                    $"Operation {operation.OperationCode} has no calendar coverage within "
                    + $"{WorkOrderSchedulingHorizon.MaxDays} days. {ex.Message}",
                    $"PrWorkOrderOperation/{operation.OperationCode}"));
            }
        }
    }

    private static WorkOrderCalculationError? ValidateGraph(ProductionWorkOrder workOrder)
    {
        var steps = workOrder.RouteSteps.ToList();
        var index = steps.Select((step, i) => (step, i)).ToDictionary(x => x.step, x => x.i);
        var edges = new List<(ProductionWorkOrderRouteStep From, ProductionWorkOrderRouteStep To)>();

        foreach (var consumer in steps)
        {
            foreach (var material in consumer.Operations.SelectMany(o => o.Materials))
            {
                if (!string.Equals(material.SupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal)
                    || material.ProducingRouteStep is null)
                {
                    continue;
                }

                var producer = material.ProducingRouteStep;
                if (producer.StageSequence == consumer.StageSequence)
                {
                    return new WorkOrderCalculationError(
                        ProductionReadinessErrorCodes.SequenceDependencyConflict,
                        $"Route step {consumer.StageSequence} consumes {material.ComponentCode} from a producer on the same sequence.");
                }

                if (producer.StageSequence > consumer.StageSequence)
                {
                    return new WorkOrderCalculationError(
                        ProductionReadinessErrorCodes.SequenceDependencyConflict,
                        $"Route step {consumer.StageSequence} consumes {material.ComponentCode} before its producer at sequence {producer.StageSequence}.");
                }

                edges.Add((producer, consumer));
            }
        }

        var visiting = new HashSet<int>();
        var visited = new HashSet<int>();
        bool Dfs(int node)
        {
            if (visiting.Contains(node))
            {
                return true;
            }

            if (!visited.Add(node))
            {
                return false;
            }

            visiting.Add(node);
            foreach (var edge in edges.Where(e => index[e.From] == node))
            {
                if (Dfs(index[edge.To]))
                {
                    return true;
                }
            }

            visiting.Remove(node);
            return false;
        }

        if (steps.Select((_, i) => i).Any(Dfs))
        {
            return new WorkOrderCalculationError(
                ProductionReadinessErrorCodes.DependencyCycle,
                "The route contains a circular internal-WIP dependency.");
        }

        return null;
    }

    private static IEnumerable<ProductionWorkOrderRouteStep> Predecessors(
        ProductionWorkOrder workOrder,
        ProductionWorkOrderRouteStep step) =>
        step.Operations
            .SelectMany(o => o.Materials)
            .Where(m => string.Equals(m.SupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal))
            .Select(m => m.ProducingRouteStep)
            .Where(p => p is not null)
            .Cast<ProductionWorkOrderRouteStep>()
            .Distinct();

    internal static string MachineKey(ProductionWorkOrderOperation operation, ProductionWorkOrderMachine machine) =>
        $"M:{operation.SourceOperationKey}:{machine.MachineCode}";

    internal static string OperationKey(ProductionWorkOrderOperation operation) =>
        $"O:{operation.SourceOperationKey}";
}

/// <summary>Hash of the exact calendar horizon that placed an activity (plan §8.3).</summary>
public static class WorkOrderScheduleSourceHasher
{
    public static string Compute(ScheduleMachineData data, DateOnly horizonStart, DateOnly horizonEnd)
    {
        var w = new CanonicalHashWriter();
        w.Section("HORIZON");
        w.AddDate(horizonStart.ToDateTime(TimeOnly.MinValue));
        w.AddDate(horizonEnd.ToDateTime(TimeOnly.MinValue));
        w.Add(data.MachineCode);

        var days = data.CalendarByDate
            .Where(x => x.Key >= horizonStart && x.Key <= horizonEnd)
            .OrderBy(x => x.Key)
            .ToList();
        w.Section("DAYS");
        w.Count(days.Count);
        foreach (var day in days)
        {
            w.AddDate(day.Key.ToDateTime(TimeOnly.MinValue));
            w.Add(day.Value.DateCd);
            w.Add(day.Value.ShfGrpCd);
            if (!string.IsNullOrWhiteSpace(day.Value.ShfGrpCd)
                && data.ShiftGroups.TryGetValue(day.Value.ShfGrpCd, out var group))
            {
                var shifts = group.Shifts.OrderBy(s => s.Start).ThenBy(s => s.ShiftCd, StringComparer.Ordinal).ToList();
                w.Count(shifts.Count);
                foreach (var shift in shifts)
                {
                    w.Add(shift.ShiftCd);
                    w.Add(shift.Start.ToString("HH:mm:ss"));
                    w.Add(shift.End.ToString("HH:mm:ss"));
                    w.Count(shift.Breaks.Count);
                    foreach (var pair in shift.Breaks)
                    {
                        w.Add(pair.Start?.ToString("HH:mm:ss"));
                        w.Add(pair.End?.ToString("HH:mm:ss"));
                    }
                }
            }
            else
            {
                w.Count(0);
            }
        }

        w.Section("DOWNTIME");
        var downtime = data.PreventiveWindows
            .Where(x => x.End > horizonStart.ToDateTime(TimeOnly.MinValue)
                        && x.Start < horizonEnd.AddDays(1).ToDateTime(TimeOnly.MinValue))
            .OrderBy(x => x.Start)
            .ThenBy(x => x.End)
            .ToList();
        w.Count(downtime.Count);
        foreach (var window in downtime)
        {
            w.AddTimestamp(window.Start);
            w.AddTimestamp(window.End);
        }

        return w.ComputeHash();
    }
}
