using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Core.Production;

/// <summary>Net material-movement totals used by Issue and Daily Production projections.</summary>
public static class ProductionMaterialMovementTotals
{
    public static decimal EffectiveIssue(decimal issueQty, decimal issueReversalQty) =>
        IvQty.Round(issueQty - issueReversalQty);

    public static decimal EffectiveConsumed(decimal consumeQty, decimal consumeReversalQty) =>
        IvQty.Round(consumeQty - consumeReversalQty);

    public static (decimal Issued, decimal Returned, decimal Consumed) FromRows(
        IEnumerable<(string MovementType, decimal Qty)> rows)
    {
        var issue = 0m;
        var issueReversal = 0m;
        var returned = 0m;
        var consume = 0m;
        var consumeReversal = 0m;
        foreach (var row in rows)
        {
            switch (row.MovementType)
            {
                case ProductionMaterialMovementTypes.Issue:
                    issue += row.Qty;
                    break;
                case ProductionMaterialMovementTypes.IssueReversal:
                    issueReversal += row.Qty;
                    break;
                case ProductionMaterialMovementTypes.Return:
                    returned += row.Qty;
                    break;
                case ProductionMaterialMovementTypes.Consume:
                    consume += row.Qty;
                    break;
                case ProductionMaterialMovementTypes.ConsumeReversal:
                    consumeReversal += row.Qty;
                    break;
            }
        }

        return (
            EffectiveIssue(issue, issueReversal),
            IvQty.Round(returned),
            EffectiveConsumed(consume, consumeReversal));
    }

    /// <summary>
    /// True when an ISSUE still has an unreversed CONSUME, or any RETURN/ADJUST dependency.
    /// </summary>
    public static bool HasBlockingDownstreamDependency(
        IReadOnlyList<ProductionMaterialMovement> movementsPointingAtIssue)
    {
        var consumeIds = movementsPointingAtIssue
            .Where(x => x.MovementType == ProductionMaterialMovementTypes.Consume)
            .Select(x => x.Uid)
            .ToHashSet();
        var reversedConsumeIds = movementsPointingAtIssue
            .Where(x => x.MovementType == ProductionMaterialMovementTypes.ConsumeReversal
                        && x.OriginalMovementId.HasValue)
            .Select(x => x.OriginalMovementId!.Value)
            .ToHashSet();

        if (consumeIds.Any(id => !reversedConsumeIds.Contains(id)))
            return true;

        return movementsPointingAtIssue.Any(x =>
            x.MovementType is ProductionMaterialMovementTypes.Return
                or ProductionMaterialMovementTypes.Adjustment);
    }
}

/// <summary>Signed BaseQty for ProductionBalLot reconciliation and prefix-balance replay.</summary>
public static class ProductionBalLotSignedQty
{
    public static decimal SignedBaseQty(string movementType, decimal baseQty) =>
        movementType switch
        {
            ProductionBalLotMovementTypes.Issue
                or ProductionBalLotMovementTypes.Produce
                or ProductionBalLotMovementTypes.ConsumeReversal
                or ProductionBalLotMovementTypes.ReturnReversal => baseQty,
            ProductionBalLotMovementTypes.IssueReversal
                or ProductionBalLotMovementTypes.ProduceReversal
                or ProductionBalLotMovementTypes.Consume
                or ProductionBalLotMovementTypes.Return => -baseQty,
            _ => throw new ArgumentOutOfRangeException(nameof(movementType), movementType, "Unknown bal-lot movement type."),
        };

    /// <summary>
    /// Collapse reversal pairs (OriginalMovementId) then return remaining effective movements
    /// ordered by MovementDate, CreatedDate, Uid.
    /// </summary>
    public static IReadOnlyList<ProductionBalLotMovement> EffectiveMovements(
        IEnumerable<ProductionBalLotMovement> all)
    {
        var list = all.ToList();
        var reversedOriginalIds = list
            .Where(x => x.OriginalMovementId.HasValue
                        && IsReversalType(x.MovementType))
            .Select(x => x.OriginalMovementId!.Value)
            .ToHashSet();
        var reversalUids = list
            .Where(x => x.OriginalMovementId.HasValue
                        && IsReversalType(x.MovementType)
                        && reversedOriginalIds.Contains(x.OriginalMovementId.Value))
            .Select(x => x.Uid)
            .ToHashSet();

        return list
            .Where(x => !reversedOriginalIds.Contains(x.Uid) && !reversalUids.Contains(x.Uid))
            .OrderBy(x => x.MovementDate)
            .ThenBy(x => x.CreatedDate)
            .ThenBy(x => x.Uid)
            .ToList();
    }

    public static decimal SumEffectiveBaseQty(IEnumerable<ProductionBalLotMovement> all) =>
        IvQty.Round(EffectiveMovements(all).Sum(x => SignedBaseQty(x.MovementType, x.BaseQty)));

    /// <summary>
    /// Replay effective movements excluding <paramref name="produceUid"/>; returns false if
    /// running BaseQty would go negative.
    /// </summary>
    public static bool CanRollbackProduce(IEnumerable<ProductionBalLotMovement> all, long produceUid)
    {
        var effective = EffectiveMovements(all).Where(x => x.Uid != produceUid);
        var running = 0m;
        foreach (var movement in effective)
        {
            running = IvQty.Round(running + SignedBaseQty(movement.MovementType, movement.BaseQty));
            if (running < 0m)
                return false;
        }

        return true;
    }

    public static DateTime? LatestEffectiveMovementDate(IEnumerable<ProductionBalLotMovement> all)
    {
        var effective = EffectiveMovements(all);
        return effective.Count == 0 ? null : effective[^1].MovementDate;
    }

    private static bool IsReversalType(string movementType) =>
        movementType is ProductionBalLotMovementTypes.IssueReversal
            or ProductionBalLotMovementTypes.ProduceReversal
            or ProductionBalLotMovementTypes.ConsumeReversal
            or ProductionBalLotMovementTypes.ReturnReversal;
}
