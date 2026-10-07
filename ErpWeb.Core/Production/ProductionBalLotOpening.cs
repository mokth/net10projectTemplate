using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>
/// Opening / reconciliation helpers for ProductionBalLot piles (plan §11 / tests 44–46, 50).
/// </summary>
public static class ProductionBalLotOpening
{
    public sealed record IssueContribution(
        long IssueMovementId,
        decimal IssueBaseQty,
        decimal ConversionFactorToBase,
        decimal UnitCost,
        IReadOnlyList<(string MovementType, decimal BaseQty, long? OriginalMovementId)> Downstream);

    public sealed record OpeningResult(
        bool Succeeded,
        decimal RemainingBaseQty,
        decimal RemainingQty,
        decimal RemainingTotalCost,
        string? Error);

    /// <summary>
    /// BaseQty-authoritative remaining pile for one ISSUE contribution.
    /// Stops fail-closed on unattributable RETURN/ADJUST (or ADJUST with undefined direction).
    /// </summary>
    public static OpeningResult ComputeRemaining(IssueContribution issue)
    {
        var issueReversal = 0m;
        var returned = 0m;
        var consume = 0m;
        var consumeReversal = 0m;

        foreach (var row in issue.Downstream)
        {
            switch (row.MovementType)
            {
                case ProductionMaterialMovementTypes.IssueReversal:
                    if (row.OriginalMovementId != issue.IssueMovementId)
                        return Fail("Unattributable ISSUE_REVERSAL.");
                    issueReversal += row.BaseQty;
                    break;
                case ProductionMaterialMovementTypes.Consume:
                    if (row.OriginalMovementId != issue.IssueMovementId)
                        return Fail("Unattributable CONSUME.");
                    consume += row.BaseQty;
                    break;
                case ProductionMaterialMovementTypes.ConsumeReversal:
                    consumeReversal += row.BaseQty;
                    break;
                case ProductionMaterialMovementTypes.Return:
                    return Fail("Opening stopped: RETURN is present and cannot be attributed safely yet.");
                case ProductionMaterialMovementTypes.Adjustment:
                    return Fail("Opening stopped: ADJUST is present and ProductionBalLot ADJUST is not supported.");
                default:
                    break;
            }
        }

        var remainingBase = IvQty.Round(
            issue.IssueBaseQty
            - issueReversal
            - returned
            - (consume - consumeReversal));

        if (remainingBase < 0m)
            return Fail("Remaining BaseQty would be negative.");
        if (remainingBase == 0m)
            return new OpeningResult(true, 0m, 0m, 0m, null);

        var factor = issue.ConversionFactorToBase <= 0m ? 1m : issue.ConversionFactorToBase;
        var remainingQty = IvQty.Round(remainingBase / factor);
        var remainingCost = StockLedgerPrecision.Money(remainingBase * issue.UnitCost);
        return new OpeningResult(true, remainingBase, remainingQty, remainingCost, null);
    }

    /// <summary>ProductionBalLot.BaseQty must equal sum of effective signed bal-lot movement BaseQty.</summary>
    public static bool Reconciles(decimal lotBaseQty, IEnumerable<ProductionBalLotMovement> movements) =>
        IvQty.Round(lotBaseQty) == ProductionBalLotSignedQty.SumEffectiveBaseQty(movements);

    private static OpeningResult Fail(string error) =>
        new(false, 0m, 0m, 0m, error);
}
