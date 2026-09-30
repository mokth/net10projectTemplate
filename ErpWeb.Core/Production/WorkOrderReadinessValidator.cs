using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>Inputs the readiness validator cannot derive from the snapshot alone.</summary>
public sealed class WorkOrderReadinessContext
{
    /// <summary>
    /// Fail-closed Release toggle (plan §16). When false, Release is refused with
    /// <see cref="ProductionReadinessErrorCodes.ReleaseDisabled"/>.
    /// </summary>
    public bool ReleaseEnabled { get; init; } = true;

    /// <summary>
    /// When true, a legacy version-1 snapshot must complete an explicit refresh before Release.
    /// </summary>
    public bool RequireCurrentSnapshotFormat { get; init; } = true;

    /// <summary>
    /// The snapshot hash recomputed from the persisted snapshot. When supplied and different from
    /// the stored <c>SnapshotHash</c>, the Release is refused. Never trust the stored value alone.
    /// </summary>
    public string? CurrentSnapshotHash { get; init; }
}

public sealed class WorkOrderReadinessReport
{
    private readonly List<WorkOrderCalculationError> _errors = [];
    private readonly List<string> _warnings = [];

    public IReadOnlyList<WorkOrderCalculationError> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;
    public bool IsReady => _errors.Count == 0;

    internal void Add(string code, string message, string? target = null) =>
        _errors.Add(new WorkOrderCalculationError(code, message, target));

    internal void Warn(string message) => _warnings.Add(message);

    public string Summary => string.Join("; ", _errors.Select(e => $"{e.Code}: {e.Message}"));
}

/// <summary>
/// Release / Recalculate readiness gate (plan §9.1). Structural validation over the persisted
/// version-2 snapshot: route shape, process type contract, material ownership, WIP producer graph,
/// snapshot format and hash. Quantity/UOM arithmetic is validated by
/// <see cref="IWorkOrderQuantityCalculator"/>; the caller composes both.
/// </summary>
public interface IWorkOrderReadinessValidator
{
    WorkOrderReadinessReport Validate(ProductionWorkOrder workOrder, WorkOrderReadinessContext context);
}

public sealed class WorkOrderReadinessValidator : IWorkOrderReadinessValidator
{
    public WorkOrderReadinessReport Validate(ProductionWorkOrder workOrder, WorkOrderReadinessContext context)
    {
        var report = new WorkOrderReadinessReport();

        if (!context.ReleaseEnabled)
        {
            report.Add(ProductionReadinessErrorCodes.ReleaseDisabled,
                "The new Release path is disabled by configuration.");
        }

        if (context.RequireCurrentSnapshotFormat
            && (workOrder.IsLegacySnapshot
                || workOrder.SnapshotFormatVersion < ProductionSnapshotFormatVersions.Current))
        {
            report.Add(ProductionReadinessErrorCodes.LegacySnapshotRefreshRequired,
                "This Work Order still carries a version-1 legacy snapshot. Refresh the definition "
                + "and confirm before releasing.");
        }

        if (!string.IsNullOrWhiteSpace(context.CurrentSnapshotHash)
            && !string.Equals(context.CurrentSnapshotHash, workOrder.SnapshotHash, StringComparison.Ordinal))
        {
            report.Add(ProductionReadinessErrorCodes.SnapshotHashInvalid,
                "The recomputed snapshot hash does not match the stored snapshot hash.");
        }

        if (workOrder.RouteSteps.Count == 0)
        {
            report.Add(ProductionReadinessErrorCodes.NoRoute,
                "The Work Order snapshot has no route step.");
            return report;
        }

        ValidateRouteShape(workOrder, report);
        ValidateTerminalOutput(workOrder, report);
        ValidateMaterials(workOrder, report);
        ValidateDependencies(workOrder, report);

        return report;
    }

    private static void ValidateRouteShape(ProductionWorkOrder workOrder, WorkOrderReadinessReport report)
    {
        foreach (var routeStep in workOrder.RouteSteps)
        {
            var target = $"PrWorkOrderRouteStep/{routeStep.StageSequence}";

            if (routeStep.Operations.Count == 0)
            {
                report.Add(ProductionReadinessErrorCodes.NoOperation,
                    $"Route step {routeStep.StageSequence} ({routeStep.WorkCentreCode}) has no process operation.",
                    target);
                continue;
            }

            var finals = routeStep.Operations.Count(o => o.IsFinalOperation);
            if (finals == 0)
            {
                report.Add(ProductionReadinessErrorCodes.FinalProcessMissing,
                    $"Route step {routeStep.StageSequence} has no final process.",
                    target);
            }
            else if (finals > 1)
            {
                report.Add(ProductionReadinessErrorCodes.FinalProcessAmbiguous,
                    $"Route step {routeStep.StageSequence} has {finals} final processes; exactly one is required.",
                    target);
            }

            foreach (var operation in routeStep.Operations)
            {
                ValidateOperation(operation, routeStep, report);
            }
        }
    }

    private static void ValidateOperation(
        ProductionWorkOrderOperation operation,
        ProductionWorkOrderRouteStep routeStep,
        WorkOrderReadinessReport report)
    {
        var target = $"PrWorkOrderOperation/{operation.OperationCode}";
        var processType = Normalize(operation.ProcessType);

        if (!PrProcessTypes.IsValid(processType))
        {
            report.Add(ProductionReadinessErrorCodes.ProcessTypeInvalid,
                $"Operation {operation.OperationCode} has an unknown process type '{operation.ProcessType}'.",
                target);
            return;
        }

        var selected = operation.Machines.Count(m => m.IsSelected);

        if (PrProcessTypes.SupportsMachine(processType))
        {
            if (operation.Machines.Count == 0)
            {
                report.Add(ProductionReadinessErrorCodes.MachineRequired,
                    $"Operation {operation.OperationCode} ({processType}) requires a machine option.",
                    target);
            }
            else if (selected != 1)
            {
                report.Add(ProductionReadinessErrorCodes.MachineRequired,
                    $"Operation {operation.OperationCode} must have exactly one selected machine option "
                    + $"but has {selected}.",
                    target);
            }
        }
        else
        {
            // MANUAL / INSPECTION / WAIT / SUBCONTRACT are fixed-duration and must not depend on a machine.
            if (operation.StandardDurationMinutes <= 0m)
            {
                report.Add(ProductionReadinessErrorCodes.OperationDurationRequired,
                    $"Operation {operation.OperationCode} ({processType}) requires a positive standard duration.",
                    target);
            }

            if (operation.Machines.Count > 0)
            {
                report.Warn(
                    $"Operation {operation.OperationCode} ({processType}) carries {operation.Machines.Count} "
                    + "machine option(s) that are ignored because the process is fixed-duration.");
            }
        }

        foreach (var machine in operation.Machines)
        {
            if (machine.OutputPerCycle <= 0m)
            {
                report.Add(ProductionReadinessErrorCodes.OutputPerCycleMissing,
                    $"Machine {machine.MachineCode} has a non-positive output per cycle.",
                    $"PrWorkOrderMachine/{machine.MachineCode}");
            }

            if (machine.ParallelMachineCount < 1)
            {
                report.Add(ProductionReadinessErrorCodes.ParallelMachineInvalid,
                    $"Machine {machine.MachineCode} has a non-positive parallel machine count.",
                    $"PrWorkOrderMachine/{machine.MachineCode}");
            }

            if (!ProductionMachineCycleQuantityModes.IsKnown(machine.CycleQuantityMode))
            {
                report.Add(ProductionReadinessErrorCodes.DurationBasisInvalid,
                    $"Machine {machine.MachineCode} has an unknown cycle quantity mode "
                    + $"'{machine.CycleQuantityMode}'.",
                    $"PrWorkOrderMachine/{machine.MachineCode}");
            }
        }

        foreach (var labour in operation.Labours)
        {
            ValidateLabour(labour, report);
        }

        foreach (var machine in operation.Machines)
        {
            foreach (var labour in machine.Labours)
            {
                ValidateLabour(labour, report);
            }
        }

        if (routeStep.OutputBaseQty <= 0m)
        {
            report.Add(ProductionReadinessErrorCodes.RouteOutputBaseQtyInvalid,
                $"Route step {routeStep.StageSequence} has a non-positive output base quantity.");
        }
    }

    private static void ValidateLabour(ProductionWorkOrderLabour labour, WorkOrderReadinessReport report)
    {
        var hasMachine = labour.MachineId is not null;
        var hasOperation = labour.OperationId is not null;

        if (hasMachine == hasOperation)
        {
            // Option A1 requires exactly one owner.
            report.Add(ProductionReadinessErrorCodes.LabourRateUomInvalid,
                $"Labour {labour.LabourCode} must be owned by exactly one of a machine or an operation.",
                $"PrWorkOrderLabour/{labour.LabourCode}");
        }

        if (!ProductionLabourRateBases.IsKnown(labour.RateBasis))
        {
            report.Add(ProductionReadinessErrorCodes.LabourRateUomInvalid,
                $"Labour {labour.LabourCode} has an unknown rate basis '{labour.RateBasis}'.",
                $"PrWorkOrderLabour/{labour.LabourCode}");
        }
    }

    private static void ValidateTerminalOutput(ProductionWorkOrder workOrder, WorkOrderReadinessReport report)
    {
        // The terminal route step's output must be the Work Order finished good.
        if (!workOrder.RouteSteps.Any(rs =>
                string.Equals(rs.OutputItemCode, workOrder.ProductCode, StringComparison.OrdinalIgnoreCase)))
        {
            report.Add(ProductionReadinessErrorCodes.FinalOutputMismatch,
                $"No route step produces the Work Order item {workOrder.ProductCode}.");
        }
    }

    private static void ValidateMaterials(ProductionWorkOrder workOrder, WorkOrderReadinessReport report)
    {
        var producersByItem = workOrder.RouteSteps
            .GroupBy(rs => rs.OutputItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var routeStep in workOrder.RouteSteps)
        {
            foreach (var operation in routeStep.Operations)
            {
                foreach (var material in operation.Materials)
                {
                    ValidateMaterial(material, operation, producersByItem, report);
                }
            }
        }
    }

    private static void ValidateMaterial(
        ProductionWorkOrderMaterial material,
        ProductionWorkOrderOperation operation,
        IReadOnlyDictionary<string, List<ProductionWorkOrderRouteStep>> producersByItem,
        WorkOrderReadinessReport report)
    {
        var target = $"PrWorkOrderMaterial/{material.ComponentCode}";

        if (material.WorkOrderOperationId is null)
        {
            report.Add(ProductionReadinessErrorCodes.MaterialOperationMissing,
                $"Material {material.ComponentCode} has no consuming operation.", target);
        }

        if (!PrMaterialIssueMethods.IsValid(material.IssueMethod))
        {
            report.Add(ProductionReadinessErrorCodes.IssueMethodInvalid,
                $"Material {material.ComponentCode} has an unknown issue method '{material.IssueMethod}'.",
                target);
        }

        if (!PrMaterialSupplySources.IsValid(material.SupplySource))
        {
            report.Add(ProductionReadinessErrorCodes.SupplySourceInvalid,
                $"Material {material.ComponentCode} has an unknown supply source '{material.SupplySource}'.",
                target);
        }

        if (!string.Equals(material.SupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal))
        {
            return;
        }

        if (material.ProducingRouteStepId is null)
        {
            report.Add(ProductionReadinessErrorCodes.WipProducerMissing,
                $"Internal route WIP material {material.ComponentCode} has no producing route step.", target);
            return;
        }

        // The producer must be in this Work Order and must actually output the consumed item.
        var producers = RouteStepsForItem(producersByItem, material.ComponentCode);
        if (producers.Count > 1)
        {
            report.Add(ProductionReadinessErrorCodes.WipProducerAmbiguous,
                $"Item {material.ComponentCode} is produced by {producers.Count} route steps; the producer is ambiguous.",
                target);
        }

        var declared = producers.FirstOrDefault(rs => rs.Uid == material.ProducingRouteStepId);
        if (declared is not null
            && !string.Equals(declared.OutputItemCode, material.ComponentCode, StringComparison.OrdinalIgnoreCase))
        {
            report.Add(ProductionReadinessErrorCodes.ReferenceInvalid,
                $"The declared producer of {material.ComponentCode} outputs {declared.OutputItemCode}.",
                target);
        }
    }

    private static List<ProductionWorkOrderRouteStep> RouteStepsForItem(
        IReadOnlyDictionary<string, List<ProductionWorkOrderRouteStep>> producersByItem,
        string itemCode) =>
        producersByItem.TryGetValue(itemCode, out var list) ? list : [];

    private static void ValidateDependencies(ProductionWorkOrder workOrder, WorkOrderReadinessReport report)
    {
        // Edge: producing route step -> consuming route step, derived from INTERNAL_ROUTE_WIP
        // ownership rather than from sequence alone (plan §8.1).
        var byItem = workOrder.RouteSteps
            .Where(rs => !string.IsNullOrWhiteSpace(rs.OutputItemCode))
            .GroupBy(rs => rs.OutputItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var edges = new Dictionary<long, HashSet<long>>();

        foreach (var consumer in workOrder.RouteSteps)
        {
            foreach (var operation in consumer.Operations)
            {
                foreach (var material in operation.Materials)
                {
                    if (!string.Equals(material.SupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (material.ProducingRouteStepId is not long producerId)
                    {
                        continue;
                    }

                    var producer = workOrder.RouteSteps.FirstOrDefault(rs => rs.Uid == producerId);
                    if (producer is null || producer.Uid == consumer.Uid)
                    {
                        continue;
                    }

                    if (producer.StageSequence >= consumer.StageSequence)
                    {
                        report.Add(ProductionReadinessErrorCodes.SequenceDependencyConflict,
                            $"Route step {consumer.StageSequence} consumes {material.ComponentCode} produced by "
                            + $"route step {producer.StageSequence}; the producer must be sequenced earlier.",
                            $"PrWorkOrderRouteStep/{consumer.StageSequence}");
                    }

                    if (!edges.TryGetValue(producer.Uid, out var targets))
                    {
                        targets = [];
                        edges[producer.Uid] = targets;
                    }

                    targets.Add(consumer.Uid);
                }
            }
        }

        if (HasCycle(edges))
        {
            report.Add(ProductionReadinessErrorCodes.DependencyCycle,
                "The internal route WIP dependency graph contains a cycle.");
        }
    }

    private static bool HasCycle(IReadOnlyDictionary<long, HashSet<long>> edges)
    {
        var state = new Dictionary<long, int>(); // 0 = unvisited, 1 = on stack, 2 = done

        bool Visit(long node)
        {
            if (state.TryGetValue(node, out var current))
            {
                return current == 1;
            }

            state[node] = 1;
            if (edges.TryGetValue(node, out var next))
            {
                foreach (var child in next)
                {
                    if (Visit(child))
                    {
                        return true;
                    }
                }
            }

            state[node] = 2;
            return false;
        }

        return edges.Keys.Any(Visit);
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
