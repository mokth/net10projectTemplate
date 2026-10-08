using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>One deterministic frozen conversion-cost line for a posted GoodQty.</summary>
public sealed record ProductionAbsorbedCostLine(
    string CostType,
    string SourceLineKey,
    long? WorkOrderLabourId,
    long? WorkOrderMachineId,
    decimal BasisQty,
    string BasisUom,
    decimal RatePerOutputUnit,
    decimal CostAmount);

/// <summary>Result of the pure absorbed conversion-cost calculation.</summary>
public sealed class ProductionAbsorbedCostResult
{
    private ProductionAbsorbedCostResult(
        IReadOnlyList<ProductionAbsorbedCostLine> lines,
        string? failureMessage)
    {
        Lines = lines;
        FailureMessage = failureMessage;
    }

    public IReadOnlyList<ProductionAbsorbedCostLine> Lines { get; }
    public string? FailureMessage { get; }
    public bool Succeeded => FailureMessage is null;
    public decimal TotalCost => StockLedgerPrecision.Money(Lines.Sum(x => x.CostAmount));

    internal static ProductionAbsorbedCostResult Success(IReadOnlyList<ProductionAbsorbedCostLine> lines) =>
        new(lines, null);

    internal static ProductionAbsorbedCostResult Fail(string message) =>
        new([], message);
}

/// <summary>
/// Pure, database-free calculator for V4 absorbed conversion cost. It reads only frozen Work Order
/// rows and never consults a current Product Definition or machine/labour master.
/// </summary>
public sealed class ProductionAbsorbedCostCalculator
{
    public ProductionAbsorbedCostResult Calculate(
        ProductionWorkOrderOperation operation,
        decimal goodQty,
        string? outputUom)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (goodQty <= 0m)
        {
            return ProductionAbsorbedCostResult.Success([]);
        }

        var expectedUom = Normalize(operation.PlannedOutputUom);
        var actualUom = Normalize(outputUom);
        if (expectedUom is null || actualUom is null || !string.Equals(expectedUom, actualUom, StringComparison.Ordinal))
        {
            return ProductionAbsorbedCostResult.Fail(
                $"Operation {operation.OperationCode} output UOM does not match the frozen operation output UOM.");
        }

        var basisQty = goodQty;
        var lines = new List<ProductionAbsorbedCostLine>();

        foreach (var labour in operation.Labours.Where(x => x.ContributesToPlan))
        {
            var error = AddLabourLine(lines, labour, basisQty, expectedUom);
            if (error is not null)
            {
                return ProductionAbsorbedCostResult.Fail(error);
            }
        }

        var selectedMachines = operation.Machines.Where(x => x.IsSelected).ToList();
        if (PrProcessTypes.SupportsMachine(operation.ProcessType) && operation.Machines.Count > 0)
        {
            if (selectedMachines.Count != 1)
            {
                return ProductionAbsorbedCostResult.Fail(
                    $"Operation {operation.OperationCode} must have exactly one selected frozen machine option.");
            }

            var selectedMachine = selectedMachines[0];
            if (selectedMachine.CostPerOutputUnit > 0m)
            {
                AddLine(
                    lines,
                    ProductionConversionCostTypes.Machine,
                    $"MACHINE:{selectedMachine.Uid}",
                    null,
                    selectedMachine.Uid > 0 ? selectedMachine.Uid : null,
                    basisQty,
                    expectedUom,
                    selectedMachine.CostPerOutputUnit);
            }

            foreach (var labour in selectedMachine.Labours.Where(x => x.ContributesToPlan))
            {
                var error = AddLabourLine(lines, labour, basisQty, expectedUom);
                if (error is not null)
                {
                    return ProductionAbsorbedCostResult.Fail(error);
                }
            }
        }

        AddOperationRate(
            lines,
            ProductionConversionCostTypes.UtilitiesOverhead,
            $"UTILITIES_OVERHEAD:{operation.Uid}",
            operation.UtilitiesOverheadCostPerOutputUnit,
            basisQty,
            expectedUom);
        AddOperationRate(
            lines,
            ProductionConversionCostTypes.Other,
            $"OTHER:{operation.Uid}",
            operation.OtherCostPerOutputUnit,
            basisQty,
            expectedUom);

        return ProductionAbsorbedCostResult.Success(lines);
    }

    private static string? AddLabourLine(
        ICollection<ProductionAbsorbedCostLine> lines,
        ProductionWorkOrderLabour labour,
        decimal basisQty,
        string basisUom)
    {
        if (!string.Equals(labour.RateBasis, ProductionLabourRateBases.PerOutputUnit, StringComparison.Ordinal))
        {
            return $"Contributing labour {labour.LabourCode} uses unsupported rate basis {labour.RateBasis}.";
        }

        if (labour.Rate > 0m)
        {
            AddLine(
                lines,
                ProductionConversionCostTypes.Labour,
                $"LABOUR:{labour.Uid}",
                labour.Uid > 0 ? labour.Uid : null,
                null,
                basisQty,
                basisUom,
                labour.Rate);
        }

        return null;
    }

    private static void AddOperationRate(
        ICollection<ProductionAbsorbedCostLine> lines,
        string costType,
        string sourceLineKey,
        decimal rate,
        decimal basisQty,
        string basisUom)
    {
        if (rate <= 0m)
        {
            return;
        }

        AddLine(lines, costType, sourceLineKey, null, null, basisQty, basisUom, rate);
    }

    private static void AddLine(
        ICollection<ProductionAbsorbedCostLine> lines,
        string costType,
        string sourceLineKey,
        long? labourId,
        long? machineId,
        decimal basisQty,
        string basisUom,
        decimal rate)
    {
        var normalizedRate = StockLedgerPrecision.Money(rate);
        var amount = StockLedgerPrecision.Money(basisQty * normalizedRate);
        if (normalizedRate <= 0m || amount <= 0m)
        {
            return;
        }

        lines.Add(new ProductionAbsorbedCostLine(
            costType,
            sourceLineKey,
            labourId,
            machineId,
            basisQty,
            basisUom,
            normalizedRate,
            amount));
    }

    private static string? Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }
}
