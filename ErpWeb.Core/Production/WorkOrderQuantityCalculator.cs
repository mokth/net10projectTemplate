using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>A single blocking calculation failure, reported with a stable <see cref="Code"/>.</summary>
public sealed record WorkOrderCalculationError(string Code, string Message, string? Target = null);

public sealed class WorkOrderQuantityResult
{
    private readonly List<WorkOrderCalculationError> _errors = [];

    public IReadOnlyList<WorkOrderCalculationError> Errors => _errors;
    public bool Succeeded => _errors.Count == 0;

    internal void Add(string code, string message, string? target = null) =>
        _errors.Add(new WorkOrderCalculationError(code, message, target));

    public string Summary => string.Join("; ", _errors.Select(e => $"{e.Code}: {e.Message}"));
}

/// <summary>
/// Quantity contract for the version-2 Work Order snapshot (plan §7.3).
/// <para>
/// Every derived quantity is written straight onto the snapshot rows, so a later recalculation is
/// reproducible from the snapshot alone and never has to reread the current Product Definition.
/// </para>
/// <para>
/// UOM handling is strict: a missing or ambiguous conversion is a blocking failure. A UOM is never
/// inferred from the item code, and a same-UOM "conversion" is only the identity when both sides
/// are literally equal after normalization.
/// </para>
/// <para>
/// Deviation from the literal §7.3 text, documented deliberately: labour has no snapshotted
/// <c>RateUOM</c> column (§6.4) and the frozen labour contract states that the amount is calculated
/// from the consuming operation's planned output quantity, so <c>PER_OUTPUT_UNIT</c> means
/// <c>Rate × PlannedOutputQty</c> in the operation's own output UOM with no extra conversion.
/// </para>
/// </summary>
public interface IWorkOrderQuantityCalculator
{
    decimal RoutePlannedQty(decimal workOrderQty, decimal productDefinitionBaseQty, decimal routeOutputBaseQty);

    /// <summary>
    /// Recalculates every derived quantity on <paramref name="workOrder"/> in place.
    /// </summary>
    Task<WorkOrderQuantityResult> CalculateAsync(ProductionWorkOrder workOrder, CancellationToken cancellationToken = default);
}

public sealed class WorkOrderQuantityCalculator : IWorkOrderQuantityCalculator
{
    private readonly IUomConversionService _uom;

    public WorkOrderQuantityCalculator(IUomConversionService uom) => _uom = uom;

    /// <summary>
    /// <c>WorkOrderQty × RouteOutputBaseQty ÷ ProductDefinitionBaseQty</c>, rounded to four
    /// decimals. Both base quantities must be positive (plan §7.3).
    /// </summary>
    public decimal RoutePlannedQty(decimal workOrderQty, decimal productDefinitionBaseQty, decimal routeOutputBaseQty) =>
        Round4(workOrderQty * routeOutputBaseQty / productDefinitionBaseQty);

    public async Task<WorkOrderQuantityResult> CalculateAsync(
        ProductionWorkOrder workOrder,
        CancellationToken cancellationToken = default)
    {
        var result = new WorkOrderQuantityResult();

        if (workOrder.PlannedQty <= 0m)
        {
            result.Add(ProductionReadinessErrorCodes.WorkOrderQtyInvalid,
                "Work Order planned quantity must be positive.");
        }

        if (workOrder.BomBaseQty <= 0m)
        {
            result.Add(ProductionReadinessErrorCodes.DefinitionBaseQtyInvalid,
                "The Product Definition base quantity is missing or zero; the route quantity cannot be derived.");
            return result;
        }

        var company = workOrder.CompanyCode;
        var product = workOrder.ProductCode;
        var productBaseUom = Normalize(workOrder.BomBaseUom) ?? Normalize(workOrder.OutputUom);

        foreach (var routeStep in workOrder.RouteSteps)
        {
            if (routeStep.OutputBaseQty <= 0m)
            {
                result.Add(ProductionReadinessErrorCodes.RouteOutputBaseQtyInvalid,
                    $"Route step {routeStep.StageSequence} has a missing or zero output base quantity.",
                    Describe(routeStep));
                continue;
            }

            routeStep.PlannedQty = RoutePlannedQty(workOrder.PlannedQty, workOrder.BomBaseQty, routeStep.OutputBaseQty);

            var routeUom = Normalize(routeStep.OutputUom);
            if (routeUom is null)
            {
                result.Add(ProductionReadinessErrorCodes.OperationUomInvalid,
                    $"Route step {routeStep.StageSequence} has no output UOM.",
                    Describe(routeStep));
                continue;
            }

            foreach (var operation in routeStep.Operations)
            {
                await CalculateOperationAsync(workOrder, routeStep, operation, company, product, routeUom, result, cancellationToken);
            }
        }

        return result;
    }

    private async Task CalculateOperationAsync(
        ProductionWorkOrder workOrder,
        ProductionWorkOrderRouteStep routeStep,
        ProductionWorkOrderOperation operation,
        string company,
        string product,
        string routeUom,
        WorkOrderQuantityResult result,
        CancellationToken cancellationToken)
    {
        // Input/output quantities carry the full owning route-step quantity (plan §7.3). Parallel
        // process sequences share the same quantity; they are parallel in time, not split in qty.
        var inputUom = Normalize(operation.PlannedInputUom);
        var outputUom = Normalize(operation.PlannedOutputUom);

        if (inputUom is null || outputUom is null)
        {
            result.Add(ProductionReadinessErrorCodes.OperationUomInvalid,
                $"Operation {operation.OperationCode} must declare both a planned input UOM and a planned output UOM.",
                Describe(operation));
            return;
        }

        var input = await ConvertAsync(company, product, routeStep.PlannedQty, routeUom, inputUom, result, Describe(operation), cancellationToken);
        if (input is null)
        {
            return;
        }

        var output = await ConvertAsync(company, product, routeStep.PlannedQty, routeUom, outputUom, result, Describe(operation), cancellationToken);
        if (output is null)
        {
            return;
        }

        operation.PlannedInputQty = Round4(input.Value);
        operation.PlannedOutputQty = Round4(output.Value);

        foreach (var material in operation.Materials)
        {
            await CalculateMaterialAsync(company, operation, material, outputUom, result, cancellationToken);
        }

        foreach (var machine in operation.Machines)
        {
            await CalculateMachineAsync(company, operation, machine, outputUom, result, cancellationToken);
        }

        foreach (var labour in operation.Labours)
        {
            CalculateLabour(operation, labour);
        }

        // Machine-owned labour follows the selected machine option (plan §6.4).
        foreach (var machine in operation.Machines)
        {
            foreach (var labour in machine.Labours)
            {
                CalculateLabour(operation, labour);
            }
        }
    }

    private async Task CalculateMaterialAsync(
        string company,
        ProductionWorkOrderOperation operation,
        ProductionWorkOrderMaterial material,
        string operationOutputUom,
        WorkOrderQuantityResult result,
        CancellationToken cancellationToken)
    {
        if (material.BomOutputQty <= 0m)
        {
            result.Add(ProductionReadinessErrorCodes.BomOutputQtyInvalid,
                $"Material {material.ComponentCode} has a missing or zero BOM output quantity.",
                Describe(material));
            return;
        }

        if (material.ComponentQtyPerParent <= 0m)
        {
            result.Add(ProductionReadinessErrorCodes.MaterialStandardQtyInvalid,
                $"Material {material.ComponentCode} has a missing or zero standard quantity.",
                Describe(material));
            return;
        }

        var bomOutputUom = Normalize(material.BomOutputUom);
        if (bomOutputUom is null)
        {
            result.Add(ProductionReadinessErrorCodes.OperationUomInvalid,
                $"Material {material.ComponentCode} has no BOM output UOM.",
                Describe(material));
            return;
        }

        // OperationMaterialBasisQty: the consuming operation's output expressed in the material's
        // BOM denominator UOM.
        var basis = await ConvertAsync(
            company, material.ComponentCode, operation.PlannedOutputQty, operationOutputUom, bomOutputUom,
            result, Describe(material), cancellationToken);
        if (basis is null)
        {
            return;
        }

        // RequiredQty never includes tolerance: tolerance is an issue/variance control (plan §7.3).
        var required = basis.Value * material.ComponentQtyPerParent / material.BomOutputQty
                       * (1m + material.ScrapPercent / 100m);
        material.RequiredQty = Round4(required);

        var requiredUom = Normalize(material.RequiredUom) ?? bomOutputUom;
        var baseUom = Normalize(material.BaseUom) ?? requiredUom;

        var requiredBase = await ConvertAsync(
            company, material.ComponentCode, material.RequiredQty, requiredUom, baseUom,
            result, Describe(material), cancellationToken);
        if (requiredBase is null)
        {
            return;
        }

        var factor = await ConvertAsync(
            company, material.ComponentCode, 1m, requiredUom, baseUom,
            result, Describe(material), cancellationToken);
        if (factor is null)
        {
            return;
        }

        if (factor.Value <= 0m)
        {
            result.Add(ProductionReadinessErrorCodes.UomConversionMissing,
                $"Material {material.ComponentCode} resolves to a non-positive base conversion factor.",
                Describe(material));
            return;
        }

        material.RequiredBaseQty = Round4(requiredBase.Value);
        material.ConversionFactorToBase = decimal.Round(factor.Value, 8, MidpointRounding.AwayFromZero);
    }

    private async Task CalculateMachineAsync(
        string company,
        ProductionWorkOrderOperation operation,
        ProductionWorkOrderMachine machine,
        string operationOutputUom,
        WorkOrderQuantityResult result,
        CancellationToken cancellationToken)
    {
        if (machine.OutputPerCycle <= 0m)
        {
            result.Add(ProductionReadinessErrorCodes.OutputPerCycleMissing,
                $"Machine {machine.MachineCode} has a missing or zero output per cycle.",
                Describe(machine));
            return;
        }

        if (machine.ParallelMachineCount < 1)
        {
            result.Add(ProductionReadinessErrorCodes.ParallelMachineInvalid,
                $"Machine {machine.MachineCode} has a non-positive parallel machine count.",
                Describe(machine));
            return;
        }

        var cycleUom = Normalize(machine.OutputPerCycleUom) ?? operationOutputUom;

        var requiredMachineOutput = await ConvertAsync(
            company, operation.OperationCode, operation.PlannedOutputQty, operationOutputUom, cycleUom,
            result, Describe(machine), cancellationToken);
        if (requiredMachineOutput is null)
        {
            return;
        }

        machine.RequiredMachineOutputQty = Round4(requiredMachineOutput.Value);

        // Ceiling applies to both the discrete cycle count and the parallel cycle slots (plan §7.3).
        var cycleCount = Math.Ceiling(requiredMachineOutput.Value / machine.OutputPerCycle);
        machine.PlannedCycleCount = cycleCount;
        machine.PlannedCycleSlots = Math.Ceiling(cycleCount / machine.ParallelMachineCount);
        machine.PlannedRunMinutes = Round4(machine.PlannedCycleSlots * machine.CycleSeconds / 60m);
    }

    /// <summary>
    /// <c>PlannedAmount = Rate × basis</c> where the basis is the consuming operation's planned
    /// output quantity (see the interface note on the documented deviation).
    /// </summary>
    private static void CalculateLabour(ProductionWorkOrderOperation operation, ProductionWorkOrderLabour labour)
    {
        if (!labour.ContributesToPlan)
        {
            labour.PlannedAmount = 0m;
            return;
        }

        labour.PlannedAmount = decimal.Round(
            labour.Rate * operation.PlannedOutputQty, IvQty.Scale, MidpointRounding.AwayFromZero);
    }

    private async Task<decimal?> ConvertAsync(
        string company,
        string itemCode,
        decimal quantity,
        string fromUom,
        string toUom,
        WorkOrderQuantityResult result,
        string target,
        CancellationToken cancellationToken)
    {
        if (string.Equals(fromUom, toUom, StringComparison.Ordinal))
        {
            return quantity;
        }

        var conversion = await _uom.ConvertAsync(company, itemCode, quantity, fromUom, toUom, cancellationToken);
        if (conversion.Succeeded)
        {
            return conversion.Quantity;
        }

        result.Add(
            conversion.FailureCode == UomConversionFailureCodes.MissingConversion
                ? ProductionReadinessErrorCodes.UomConversionMissing
                : ProductionReadinessErrorCodes.OperationUomInvalid,
            conversion.FailureMessage ?? $"No approved conversion from {fromUom} to {toUom}.",
            target);
        return null;
    }

    private static string? Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().ToUpperInvariant();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static decimal Round4(decimal value) =>
        decimal.Round(value, IvQty.Scale, MidpointRounding.AwayFromZero);

    private static string Describe(ProductionWorkOrderRouteStep routeStep) =>
        $"PrWorkOrderRouteStep/{routeStep.StageSequence}";

    private static string Describe(ProductionWorkOrderOperation operation) =>
        $"PrWorkOrderOperation/{operation.OperationCode}";

    private static string Describe(ProductionWorkOrderMaterial material) =>
        $"PrWorkOrderMaterial/{material.ComponentCode}";

    private static string Describe(ProductionWorkOrderMachine machine) =>
        $"PrWorkOrderMachine/{machine.MachineCode}";
}
