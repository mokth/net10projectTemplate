using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;

namespace ErpWeb.Core.Production;

internal sealed record ProductionTerminalAdjustmentCostStep(long MovementId, decimal BaseQty);

internal sealed record ProductionTerminalAdjustmentProjectedCost(
    decimal RemainingBaseQty,
    decimal RemainingValue,
    IReadOnlyDictionary<long, decimal> OutboundValues);

/// <summary>
/// Replays a material pool with one receipt and terminal pool-average outbounds
/// in canonical posting order. The final outbound takes all remaining value.
/// </summary>
internal static class ProductionTerminalAdjustmentCostProjection
{
    public static ProductionTerminalAdjustmentProjectedCost Project(
        decimal issueBaseQty, decimal issueValue,
        IReadOnlyList<ProductionTerminalAdjustmentCostStep> orderedAdjustments)
    {
        ArgumentNullException.ThrowIfNull(orderedAdjustments);
        if (issueBaseQty <= 0m || issueValue < 0m
            || StockLedgerPrecision.Money(issueValue) != issueValue
            || orderedAdjustments.Count == 0
            || orderedAdjustments.Any(a => a.MovementId <= 0 || a.BaseQty <= 0m)
            || orderedAdjustments.Select(a => a.MovementId).Distinct().Count()
                != orderedAdjustments.Count)
            throw Invalid("Production terminal adjustment projection has invalid inputs.");

        var remainingQty = issueBaseQty;
        var remainingValue = issueValue;
        var values = new Dictionary<long, decimal>(orderedAdjustments.Count);
        foreach (var adjustment in orderedAdjustments)
        {
            if (adjustment.BaseQty > remainingQty)
                throw Invalid($"Production ADJUST_OUT {adjustment.MovementId} exceeds its preceding pool quantity.");
            var cost = ProductionStockAdjustmentMath.ProportionalTotal(
                remainingQty, remainingValue, adjustment.BaseQty);
            remainingQty = StockLedgerPrecision.Quantity(
                remainingQty - adjustment.BaseQty);
            remainingValue = StockLedgerPrecision.Money(remainingValue - cost);
            if (remainingValue < 0m || remainingQty == 0m && remainingValue != 0m)
                throw Invalid($"Production ADJUST_OUT {adjustment.MovementId} strands pool value.");
            values.Add(adjustment.MovementId, cost);
        }
        if (StockLedgerPrecision.Money(values.Values.Sum() + remainingValue)
            != issueValue)
            throw Invalid("Production terminal adjustments do not conserve issue value.");
        return new(remainingQty, remainingValue, values);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
