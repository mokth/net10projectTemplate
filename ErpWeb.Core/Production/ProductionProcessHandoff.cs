using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>
/// Same-route-step process handoff: non-final good quantity stages a WIP lot that the immediate
/// next process consumes. Units, sequence, and final-process rules are shared by post, workspace,
/// and readiness.
/// </summary>
public static class ProductionProcessHandoff
{
    public const string IssueMethod = "HANDOFF";
    public const string SupplySource = "PREVIOUS_PROCESS";

    public sealed record QtyContract(string Uom, string BaseUom, decimal ConversionFactorToBase);

    public static string HandoffLotNo(long producingOperationId) => $"OP:{producingOperationId}";

    public static long SyntheticMaterialId(long priorOperationId) => -priorOperationId;

    public static string? ValidateFinalIsLast(IReadOnlyList<ProductionWorkOrderOperation> siblings)
    {
        if (siblings.Count == 0)
            return "The final process must be the last process on the route step.";

        var finals = siblings.Where(x => x.IsFinalOperation).ToList();
        if (finals.Count != 1)
            return "The final process must be the last process on the route step.";

        var maxSequence = siblings.Max(x => x.ProcessSequence);
        if (finals[0].ProcessSequence != maxSequence)
            return "The final process must be the last process on the route step.";

        var last = siblings.Where(x => x.ProcessSequence == maxSequence).ToList();
        if (last.Count != 1)
            return "The final process must be the last process on the route step.";

        return null;
    }

    /// <summary>
    /// Unique immediate next process: lowest <see cref="ProductionWorkOrderOperation.ProcessSequence"/>
    /// strictly greater than <paramref name="current"/>. Null next means this process has no successor.
    /// </summary>
    public static string? TryGetImmediateNext(
        IReadOnlyList<ProductionWorkOrderOperation> siblings,
        ProductionWorkOrderOperation current,
        out ProductionWorkOrderOperation? next)
    {
        next = null;
        var later = siblings.Where(x => x.ProcessSequence > current.ProcessSequence).ToList();
        if (later.Count == 0)
            return null;

        var minSequence = later.Min(x => x.ProcessSequence);
        var atSequence = later.Where(x => x.ProcessSequence == minSequence).ToList();
        if (atSequence.Count != 1)
            return "Parallel processes at the next sequence are not supported for process handoff.";

        next = atSequence[0];
        return null;
    }

    /// <summary>
    /// Unique immediate prior process: greatest sequence strictly below <paramref name="current"/>.
    /// </summary>
    public static string? TryGetImmediatePrior(
        IReadOnlyList<ProductionWorkOrderOperation> siblings,
        ProductionWorkOrderOperation current,
        out ProductionWorkOrderOperation? prior)
    {
        prior = null;
        var earlier = siblings.Where(x => x.ProcessSequence < current.ProcessSequence).ToList();
        if (earlier.Count == 0)
            return null;

        var maxSequence = earlier.Max(x => x.ProcessSequence);
        var atSequence = earlier.Where(x => x.ProcessSequence == maxSequence).ToList();
        if (atSequence.Count != 1)
            return "Parallel processes at the prior sequence are not supported for process handoff.";

        prior = atSequence[0];
        return null;
    }

    public static bool IsHandoffProducer(
        ProductionWorkOrderOperation operation,
        ProductionWorkOrderOperation? immediateNext) =>
        !operation.IsFinalOperation && immediateNext is not null;

    public static string? ResolveProducerContract(
        ProductionWorkOrderOperation producer,
        ProductionWorkOrderRouteStep routeStep,
        out QtyContract? contract)
    {
        contract = null;
        var routeUom = Normalize(routeStep.OutputUom);
        var producerUom = Normalize(producer.PlannedOutputUom);

        string uom;
        if (producerUom is null)
        {
            if (routeUom is null)
                return "Route OutputUom is blank; the handoff lot would have no UOM.";
            uom = routeUom;
        }
        else
        {
            if (routeUom is null
                || !string.Equals(producerUom, routeUom, StringComparison.OrdinalIgnoreCase))
            {
                return "Process PlannedOutputUom must equal route OutputUom for process handoff.";
            }

            uom = producerUom;
        }

        return BuildContract(routeStep, uom, out contract);
    }

    public static string? ResolveConsumerContract(
        ProductionWorkOrderOperation consumer,
        ProductionWorkOrderRouteStep routeStep,
        string handoffUom,
        out QtyContract? contract)
    {
        contract = null;
        var routeUom = Normalize(routeStep.OutputUom);
        var input = Normalize(consumer.PlannedInputUom) ?? routeUom;
        var output = Normalize(consumer.PlannedOutputUom) ?? routeUom;

        if (input is null || output is null)
            return "Route OutputUom is blank; the handoff lot would have no UOM.";

        if (!string.Equals(input, handoffUom, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(output, handoffUom, StringComparison.OrdinalIgnoreCase))
        {
            return "Next process UOM must equal the previous-process handoff UOM.";
        }

        return BuildContract(routeStep, handoffUom, out contract);
    }

    public static decimal ToBaseQty(decimal qty, decimal conversionFactorToBase) =>
        IvQty.Round(qty * conversionFactorToBase);

    private static string? BuildContract(
        ProductionWorkOrderRouteStep routeStep,
        string uom,
        out QtyContract? contract)
    {
        contract = null;
        var baseUom = Normalize(routeStep.OutputBaseUom) ?? uom;
        decimal factor;
        if (routeStep.OutputConversionFactorToBase is > 0m)
            factor = routeStep.OutputConversionFactorToBase.Value;
        else if (string.Equals(baseUom, uom, StringComparison.OrdinalIgnoreCase))
            factor = 1m;
        else
            return "Route OutputConversionFactorToBase is missing.";

        contract = new QtyContract(uom, baseUom, factor);
        return null;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
