using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Production;

public static class ProductionMaterialExecutionCalc
{
    public static decimal MaxAllowedNetIssue(decimal requiredQty, decimal tolerancePercent)
    {
        if (requiredQty < 0m) throw new ArgumentOutOfRangeException(nameof(requiredQty));
        if (tolerancePercent < 0m) throw new ArgumentOutOfRangeException(nameof(tolerancePercent));
        return IvQty.Round(requiredQty * (1m + tolerancePercent / 100m));
    }

    /// <summary>
    /// Standard BOM quantity for a desired production quantity (tolerance not included).
    /// </summary>
    public static decimal RequestedForProductionQty(
        decimal fullRequiredQty, decimal plannedOutputQty, decimal desiredOutputQty)
    {
        if (fullRequiredQty < 0m) throw new ArgumentOutOfRangeException(nameof(fullRequiredQty));
        if (plannedOutputQty <= 0m) throw new ArgumentOutOfRangeException(nameof(plannedOutputQty));
        if (desiredOutputQty < 0m) throw new ArgumentOutOfRangeException(nameof(desiredOutputQty));
        return IvQty.Round(fullRequiredQty * desiredOutputQty / plannedOutputQty);
    }

    /// <summary>
    /// Maximum issue for a desired-output standard quantity after configured tolerance.
    /// </summary>
    public static decimal MaxForProductionQty(decimal requestedQty, decimal tolerancePercent) =>
        MaxAllowedNetIssue(requestedQty, tolerancePercent);

    public static decimal MovementEffectiveIssue(decimal issueQty, decimal issueReversalQty) =>
        IvQty.Round(issueQty - issueReversalQty);

    public static decimal Outstanding(decimal requiredQty, decimal effectiveIssueQty, decimal returnedQty) =>
        IvQty.Round(Math.Max(requiredQty - effectiveIssueQty + returnedQty, 0m));

    public static decimal BaseQtyForIssueQty(decimal issueQty, decimal conversionFactorToBase)
    {
        EnsureConversion(conversionFactorToBase);
        return IvQty.Round(issueQty * conversionFactorToBase);
    }

    public static decimal IssueQtyForBaseQty(decimal baseQty, decimal conversionFactorToBase)
    {
        EnsureConversion(conversionFactorToBase);
        return IvQty.Round(baseQty / conversionFactorToBase);
    }

    public static IReadOnlyList<decimal> AllocateIssueQty(
        decimal issueQty,
        IReadOnlyList<decimal> allocationBaseQuantities,
        decimal conversionFactorToBase)
    {
        EnsureConversion(conversionFactorToBase);
        if (issueQty <= 0m) throw new ArgumentOutOfRangeException(nameof(issueQty));
        if (allocationBaseQuantities.Count == 0) throw new ArgumentException("At least one allocation is required.", nameof(allocationBaseQuantities));
        if (allocationBaseQuantities.Any(x => x <= 0m)) throw new ArgumentException("Allocation quantities must be positive.", nameof(allocationBaseQuantities));

        var result = new decimal[allocationBaseQuantities.Count];
        var assigned = 0m;
        for (var i = 0; i < result.Length - 1; i++)
        {
            result[i] = IssueQtyForBaseQty(allocationBaseQuantities[i], conversionFactorToBase);
            assigned = IvQty.Round(assigned + result[i]);
        }

        result[^1] = IvQty.Round(issueQty - assigned);
        if (result[^1] <= 0m)
            throw new ArgumentException("Rounding leaves a non-positive final allocation.", nameof(allocationBaseQuantities));
        return result;
    }

    private static void EnsureConversion(decimal conversionFactorToBase)
    {
        if (conversionFactorToBase <= 0m)
            throw new ArgumentOutOfRangeException(nameof(conversionFactorToBase));
    }
}
