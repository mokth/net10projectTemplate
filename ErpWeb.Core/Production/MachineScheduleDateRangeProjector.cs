using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

public readonly record struct MachineScheduleOperationDates(
    long OperationId,
    DateTime Start,
    DateTime Completion);

public readonly record struct MachineScheduleProjectionPlacement(
    DateTime Start,
    DateTime Completion,
    string? Error = null)
{
    public bool Succeeded => Error is null;
    public static MachineScheduleProjectionPlacement Failed(string error) => new(default, default, error);
}

/// <summary>
/// Resolves the canonical placement of one operation when the projector reaches it. Returning the
/// saved dates leaves an operation untouched; returning new dates moves it.
/// </summary>
public delegate MachineScheduleProjectionPlacement MachineScheduleProjectionPlacementResolver(
    ProductionWorkOrderRouteStep step,
    ProductionWorkOrderOperation operation,
    DateTime earliest);

public sealed class MachineScheduleDateRangeProjection
{
    public IReadOnlyDictionary<long, MachineScheduleOperationDates> ByOperation { get; init; }
        = new Dictionary<long, MachineScheduleOperationDates>();

    public IReadOnlyDictionary<long, (DateTime Start, DateTime Completion)> ByRouteStep { get; init; }
        = new Dictionary<long, (DateTime Start, DateTime Completion)>();

    public DateTime WorkOrderStart { get; init; }
    public DateTime WorkOrderCompletion { get; init; }
    public IReadOnlyList<string> Blockers { get; init; } = [];

    public bool Succeeded => Blockers.Count == 0;
}

/// <summary>
/// Pure forward date-hierarchy projector for advanced planning.
/// <para>
/// It deliberately mirrors <see cref="WorkOrderScheduleCalculator"/>'s current planning semantics:
/// equal <c>StageSequence</c> route steps are parallel; inside one route step equal
/// <c>ProcessSequence</c> operations are parallel; the next process group starts at the previous
/// group's slowest completion; the next stage starts at the previous stage's slowest route-step
/// completion; an <c>INTERNAL_ROUTE_WIP</c> producer adds a lower bound. The Work Order header is
/// the earliest route-step start and the latest route-step completion.
/// </para>
/// <para>
/// The projector is read-only. The caller persists the returned dates, and it never touches
/// SnapshotHash, SnapshotRevision, ScheduleAnchorDateTime, quantities, or costing.
/// </para>
/// </summary>
public static class MachineScheduleDateRangeProjector
{
    public static MachineScheduleDateRangeProjection ProjectForward(
        ProductionWorkOrder order,
        DateTime anchor,
        MachineScheduleProjectionPlacementResolver resolve,
        IReadOnlyDictionary<long, MachineScheduleOperationDates>? fixedDates = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(resolve);

        var blockers = new List<string>();
        var operations = new Dictionary<long, MachineScheduleOperationDates>();
        var stepBounds = new Dictionary<long, (DateTime Start, DateTime Completion)>();
        var completionByStep = new Dictionary<long, DateTime>();

        var stageAnchor = anchor;
        foreach (var stage in order.RouteSteps
                     .GroupBy(step => step.StageSequence)
                     .OrderBy(group => group.Key))
        {
            DateTime? slowestStep = null;
            foreach (var step in DeterministicStepOrder(stage))
            {
                var earliest = stageAnchor;
                foreach (var predecessor in InternalRouteWipPredecessors(step))
                {
                    if (completionByStep.TryGetValue(predecessor.Uid, out var completion) && completion > earliest)
                        earliest = completion;
                }

                var cursor = earliest;
                DateTime? stepStart = null;
                DateTime? stepCompletion = null;

                foreach (var processGroup in step.Operations
                             .GroupBy(operation => operation.ProcessSequence)
                             .OrderBy(group => group.Key))
                {
                    DateTime? slowestOperation = null;
                    foreach (var operation in DeterministicOperationOrder(processGroup))
                    {
                        MachineScheduleOperationDates dates;
                        if (fixedDates is not null && fixedDates.TryGetValue(operation.Uid, out var fixedDate))
                        {
                            dates = fixedDate;
                        }
                        else
                        {
                            var placed = resolve(step, operation, cursor);
                            if (!placed.Succeeded)
                            {
                                blockers.Add(placed.Error!);
                                continue;
                            }

                            dates = new MachineScheduleOperationDates(operation.Uid, placed.Start, placed.Completion);
                        }

                        operations[operation.Uid] = dates;
                        stepStart = stepStart is null || dates.Start < stepStart ? dates.Start : stepStart;
                        stepCompletion = stepCompletion is null || dates.Completion > stepCompletion
                            ? dates.Completion
                            : stepCompletion;
                        slowestOperation = slowestOperation is null || dates.Completion > slowestOperation
                            ? dates.Completion
                            : slowestOperation;
                    }

                    if (slowestOperation is not null)
                        cursor = slowestOperation.Value;
                }

                if (stepStart is null || stepCompletion is null)
                {
                    blockers.Add($"Route step {step.StageSequence} {step.WorkCentreCode} has incomplete operation dates.");
                    continue;
                }

                stepBounds[step.Uid] = (stepStart.Value, stepCompletion.Value);
                completionByStep[step.Uid] = stepCompletion.Value;
                slowestStep = slowestStep is null || stepCompletion.Value > slowestStep
                    ? stepCompletion.Value
                    : slowestStep;
            }

            if (slowestStep is not null)
                stageAnchor = slowestStep.Value;
        }

        if (stepBounds.Count == 0)
        {
            blockers.Add("The Work Order route has no schedulable route step.");
            return new MachineScheduleDateRangeProjection { Blockers = blockers };
        }

        return new MachineScheduleDateRangeProjection
        {
            ByOperation = operations,
            ByRouteStep = stepBounds,
            WorkOrderStart = stepBounds.Values.Min(x => x.Start),
            WorkOrderCompletion = stepBounds.Values.Max(x => x.Completion),
            Blockers = blockers
        };
    }

    /// <summary>Applies a projection to the tracked aggregate. Dates only.</summary>
    public static void Apply(ProductionWorkOrder order, MachineScheduleDateRangeProjection projection)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(projection);
        if (!projection.Succeeded)
            return;

        foreach (var step in order.RouteSteps)
        {
            foreach (var operation in step.Operations)
            {
                if (!projection.ByOperation.TryGetValue(operation.Uid, out var dates))
                    continue;
                operation.PlannedStartDateTime = dates.Start;
                operation.PlannedCompletionDateTime = dates.Completion;
            }

            if (projection.ByRouteStep.TryGetValue(step.Uid, out var bounds))
            {
                step.PlannedStartDateTime = bounds.Start;
                step.PlannedCompletionDateTime = bounds.Completion;
            }
        }

        order.PlannedStartDateTime = projection.WorkOrderStart;
        order.PlannedCompletionDateTime = projection.WorkOrderCompletion;
    }

    public static IEnumerable<ProductionWorkOrderRouteStep> DeterministicStepOrder(
        IEnumerable<ProductionWorkOrderRouteStep> steps) =>
        steps.OrderBy(step => step.WorkCentreCode, StringComparer.Ordinal)
            .ThenBy(step => step.OutputItemCode, StringComparer.Ordinal)
            .ThenBy(step => step.SourceRouteStepKey ?? Guid.Empty)
            .ThenBy(step => step.Uid);

    public static IEnumerable<ProductionWorkOrderOperation> DeterministicOperationOrder(
        IEnumerable<ProductionWorkOrderOperation> operations) =>
        operations.OrderBy(operation => operation.OperationCode, StringComparer.Ordinal)
            .ThenBy(operation => operation.SourceOperationKey ?? Guid.Empty)
            .ThenBy(operation => operation.Uid);

    private static IEnumerable<ProductionWorkOrderRouteStep> InternalRouteWipPredecessors(
        ProductionWorkOrderRouteStep step) =>
        step.Operations
            .SelectMany(operation => operation.Materials)
            .Where(material => string.Equals(
                material.SupplySource,
                ErpWeb.Model.Entities.Planning.PrMaterialSupplySources.InternalRouteWip,
                StringComparison.Ordinal))
            .Select(material => material.ProducingRouteStep)
            .Where(producer => producer is not null)
            .Cast<ProductionWorkOrderRouteStep>()
            .Distinct();
}
