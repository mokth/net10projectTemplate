using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;

namespace ErpWeb.Core.Production;

internal sealed record ProductionStockReturnSequenceStep(
    long MovementId, decimal BaseQty, decimal OriginalValue, decimal EffectiveValue);

internal sealed record ProductionStockReturnSequenceProjectedCost(
    decimal RemainingBaseQty,
    decimal OriginalRemainingValue,
    decimal EffectiveRemainingValue,
    decimal ProjectedRemainingValue,
    decimal CorrectedTargetValue,
    IReadOnlyDictionary<long, decimal> CorrectedReturnValues);

/// <summary>
/// Projects sequential returns from one ISSUE using the same pool-average rule
/// as ordinary return posting. Earlier returns must already carry their
/// corrected values; later returns must still carry their original values.
/// Ledger identity and pool-state checks belong to the calling authority.
/// </summary>
internal static class ProductionStockReturnSequenceCostProjection
{
    public static ProductionStockReturnSequenceProjectedCost Project(
        decimal issueBaseQty, decimal originalIssueValue, decimal correctedIssueValue,
        IReadOnlyList<ProductionStockReturnSequenceStep> orderedReturns,
        long targetMovementId)
    {
        ArgumentNullException.ThrowIfNull(orderedReturns);
        if (issueBaseQty <= 0m || originalIssueValue < 0m || correctedIssueValue < 0m
            || StockLedgerPrecision.Quantity(issueBaseQty) != issueBaseQty
            || StockLedgerPrecision.Money(originalIssueValue) != originalIssueValue
            || StockLedgerPrecision.Money(correctedIssueValue) != correctedIssueValue
            || targetMovementId <= 0 || orderedReturns.Count == 0
            || orderedReturns.Count(x => x.MovementId == targetMovementId) != 1
            || orderedReturns.Any(x => x.MovementId <= 0 || x.BaseQty <= 0m
                || StockLedgerPrecision.Quantity(x.BaseQty) != x.BaseQty
                || x.OriginalValue < 0m || x.EffectiveValue < 0m
                || StockLedgerPrecision.Money(x.OriginalValue) != x.OriginalValue
                || StockLedgerPrecision.Money(x.EffectiveValue) != x.EffectiveValue)
            || orderedReturns.Select(x => x.MovementId).Distinct().Count()
                != orderedReturns.Count)
            throw Invalid("Return sequence cost projection has invalid inputs.");

        var originalQty = issueBaseQty;
        var originalValue = originalIssueValue;
        var correctedQty = issueBaseQty;
        var correctedValue = correctedIssueValue;
        var effectiveTotal = 0m;
        var projectedTotal = 0m;
        var passedTarget = false;
        var values = new Dictionary<long, decimal>(orderedReturns.Count);
        decimal targetValue = 0m;
        foreach (var step in orderedReturns)
        {
            if (step.BaseQty > originalQty || step.BaseQty > correctedQty)
                throw Invalid($"Return {step.MovementId} exceeds its preceding pool quantity.");
            var originalReturn = FinishedGoodReceiptMath.AllocateValue(
                originalQty, originalValue, [(step.MovementId, step.BaseQty)])[step.MovementId];
            var correctedReturn = FinishedGoodReceiptMath.AllocateValue(
                correctedQty, correctedValue, [(step.MovementId, step.BaseQty)])[step.MovementId];
            if (step.OriginalValue != originalReturn)
                throw Invalid($"Return {step.MovementId} differs from its original pool allocation.");
            if (!passedTarget && step.MovementId != targetMovementId
                && step.EffectiveValue != correctedReturn
                || passedTarget && step.EffectiveValue != step.OriginalValue
                || step.MovementId == targetMovementId
                    && step.EffectiveValue != step.OriginalValue
                    && step.EffectiveValue != correctedReturn)
                throw Invalid($"Return {step.MovementId} has an out-of-order cost correction.");

            if (step.MovementId == targetMovementId)
            {
                passedTarget = true;
                targetValue = correctedReturn;
            }
            originalQty = StockLedgerPrecision.Quantity(originalQty - step.BaseQty);
            correctedQty = StockLedgerPrecision.Quantity(correctedQty - step.BaseQty);
            originalValue = StockLedgerPrecision.Money(originalValue - originalReturn);
            correctedValue = StockLedgerPrecision.Money(correctedValue - correctedReturn);
            effectiveTotal = StockLedgerPrecision.Money(effectiveTotal + step.EffectiveValue);
            projectedTotal = StockLedgerPrecision.Money(projectedTotal
                + (step.MovementId == targetMovementId ? correctedReturn : step.EffectiveValue));
            if (originalValue < 0m || correctedValue < 0m
                || originalQty == 0m && originalValue != 0m
                || correctedQty == 0m && correctedValue != 0m)
                throw Invalid($"Return {step.MovementId} strands its source pool value.");
            values.Add(step.MovementId, correctedReturn);
        }

        var effectiveRemaining = StockLedgerPrecision.Money(
            correctedIssueValue - effectiveTotal);
        var projectedRemaining = StockLedgerPrecision.Money(
            correctedIssueValue - projectedTotal);
        if (effectiveRemaining < 0m || projectedRemaining < 0m
            || originalQty == 0m && projectedRemaining != 0m
            || correctedValue != StockLedgerPrecision.Money(
                correctedIssueValue - values.Values.Sum()))
            throw Invalid("Return sequence does not conserve source ISSUE value.");
        return new(originalQty, originalValue, effectiveRemaining,
            projectedRemaining, targetValue, values);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
