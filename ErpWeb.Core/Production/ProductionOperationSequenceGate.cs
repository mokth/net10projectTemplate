using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>
/// Applies the released Work Order routing snapshot's execution dependencies to a Daily
/// Production operation. Equal stage/process sequences are parallel; only lower sequences block.
/// </summary>
public static class ProductionOperationSequenceGate
{
    private const string InvalidSnapshotMessage =
        "The Work Order routing snapshot is incomplete or invalid. Refresh/re-release the Work Order before recording production.";

    /// <summary>
    /// An operation unlocks its dependants only when posted good output has satisfied its planned
    /// output. Scrap, reject, hold, drafts, and WIP availability are deliberately excluded.
    /// </summary>
    public static bool IsOperationComplete(ProductionWorkOrderOperation operation) =>
        IsOperationComplete(operation.PlannedOutputQty, operation.GoodQty);

    public static bool IsOperationComplete(decimal plannedOutputQty, decimal goodQty) =>
        IvQty.Round(plannedOutputQty - goodQty) <= 0m;

    /// <summary>
    /// Evaluates all lower sequence groups defensively. The caller supplies the complete route and
    /// operation snapshot for one Work Order; malformed snapshots fail closed.
    /// </summary>
    public static ProductionSequenceGateResult Evaluate(
        ProductionWorkOrderOperation selectedOperation,
        IReadOnlyCollection<ProductionWorkOrderRouteStep> routeSteps,
        IReadOnlyCollection<ProductionWorkOrderOperation> operations)
    {
        if (selectedOperation is null
            || selectedOperation.WorkOrderId is not long workOrderId
            || selectedOperation.RouteStepId is not long selectedRouteStepId
            || workOrderId <= 0
            || selectedRouteStepId <= 0)
        {
            return InvalidSnapshot();
        }

        var steps = routeSteps
            .Where(x => x.WorkOrderId == workOrderId)
            .OrderBy(x => x.StageSequence)
            .ThenBy(x => x.Uid)
            .ToList();
        var workOrderOperations = operations
            .Where(x => x.WorkOrderId == workOrderId)
            .OrderBy(x => x.RouteStepId)
            .ThenBy(x => x.ProcessSequence)
            .ThenBy(x => x.Uid)
            .ToList();

        var selectedRouteStep = steps.SingleOrDefault(x => x.Uid == selectedRouteStepId);
        var selected = workOrderOperations.SingleOrDefault(x => x.Uid == selectedOperation.Uid);
        if (selectedRouteStep is null
            || selected is null
            || selected.RouteStepId != selectedRouteStep.Uid
            || selectedRouteStep.StageSequence <= 0
            || selected.ProcessSequence <= 0)
        {
            return InvalidSnapshot();
        }

        if (steps.Count == 0
            || steps.Any(x => x.Uid <= 0 || x.StageSequence <= 0)
            || workOrderOperations.Any(x => x.Uid <= 0
                || x.RouteStepId is null
                || x.ProcessSequence <= 0
                || !steps.Any(step => step.Uid == x.RouteStepId)))
        {
            return InvalidSnapshot();
        }

        // Every route step must own at least one executable operation. Otherwise an absent
        // predecessor could silently open a later stage.
        if (steps.Any(step => !workOrderOperations.Any(operation => operation.RouteStepId == step.Uid)))
            return InvalidSnapshot();

        foreach (var stageGroup in steps
                     .Where(x => x.StageSequence < selectedRouteStep.StageSequence)
                     .GroupBy(x => x.StageSequence)
                     .OrderBy(x => x.Key))
        {
            var incomplete = stageGroup
                .SelectMany(step => workOrderOperations.Where(operation => operation.RouteStepId == step.Uid)
                    .Select(operation => new { Step = step, Operation = operation }))
                .Where(x => !IsOperationComplete(x.Operation))
                .OrderBy(x => x.Step.WorkCentreCode, StringComparer.Ordinal)
                .ThenBy(x => x.Operation.ProcessSequence)
                .ThenBy(x => x.Operation.Uid)
                .FirstOrDefault();
            if (incomplete is not null)
            {
                return new ProductionSequenceGateResult
                {
                    Allowed = false,
                    BlockingLevel = ProductionSequenceBlockingLevels.Stage,
                    BlockingSequence = stageGroup.Key,
                    BlockingWorkCentreCode = incomplete.Step.WorkCentreCode,
                    BlockingOperationCode = incomplete.Operation.OperationCode,
                    BlockingOperationIds = stageGroup
                        .SelectMany(step => workOrderOperations.Where(operation => operation.RouteStepId == step.Uid))
                        .Where(operation => !IsOperationComplete(operation))
                        .Select(operation => operation.Uid).OrderBy(id => id).ToList(),
                    Message = $"Daily Production cannot start for {selectedRouteStep.WorkCentreCode} (Stage {selectedRouteStep.StageSequence}). "
                        + $"Previous Stage {stageGroup.Key} is not complete. Waiting for {incomplete.Step.WorkCentreCode}: "
                        + $"{incomplete.Operation.GoodQty:n4} of {incomplete.Operation.PlannedOutputQty:n4} completed.",
                };
            }
        }

        foreach (var processGroup in workOrderOperations
                     .Where(x => x.RouteStepId == selectedRouteStep.Uid
                         && x.ProcessSequence < selected.ProcessSequence)
                     .GroupBy(x => x.ProcessSequence)
                     .OrderBy(x => x.Key))
        {
            var incomplete = processGroup
                .Where(x => !IsOperationComplete(x))
                .OrderBy(x => x.Uid)
                .FirstOrDefault();
            if (incomplete is not null)
            {
                return new ProductionSequenceGateResult
                {
                    Allowed = false,
                    BlockingLevel = ProductionSequenceBlockingLevels.Process,
                    BlockingSequence = processGroup.Key,
                    BlockingWorkCentreCode = selectedRouteStep.WorkCentreCode,
                    BlockingOperationCode = incomplete.OperationCode,
                    BlockingOperationIds = processGroup.Where(operation => !IsOperationComplete(operation))
                        .Select(operation => operation.Uid).OrderBy(id => id).ToList(),
                    Message = $"Daily Production cannot start for {selected.OperationCode} (Process Seq {selected.ProcessSequence}). "
                        + $"Process Seq {processGroup.Key} in {selectedRouteStep.WorkCentreCode} must complete first. "
                        + $"Waiting for {incomplete.OperationCode}: {incomplete.GoodQty:n4} of "
                        + $"{incomplete.PlannedOutputQty:n4} completed.",
                };
            }
        }

        return new ProductionSequenceGateResult { Allowed = true };
    }

    private static ProductionSequenceGateResult InvalidSnapshot() => new()
    {
        Allowed = false,
        BlockingLevel = ProductionSequenceBlockingLevels.Snapshot,
        Message = InvalidSnapshotMessage,
    };
}

public sealed record ProductionSequenceGateResult
{
    public bool Allowed { get; init; }
    public string? BlockingLevel { get; init; }
    public int? BlockingSequence { get; init; }
    public string? BlockingWorkCentreCode { get; init; }
    public string? BlockingOperationCode { get; init; }
    public IReadOnlyList<long> BlockingOperationIds { get; init; } = [];
    public string? Message { get; init; }
}

public static class ProductionSequenceBlockingLevels
{
    public const string Snapshot = "SNAPSHOT";
    public const string Stage = "STAGE";
    public const string Process = "PROCESS";
}
