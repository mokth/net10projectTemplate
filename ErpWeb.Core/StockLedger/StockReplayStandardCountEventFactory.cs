using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Binds an absolute pile count to its frozen Standard revision and a proven
/// physical projection. The item's Standard pool receives only the pile delta.
/// </summary>
public static class StockReplayStandardCountEventFactory
{
    public static StockReplayStandardEvent CreateActive(
        StockReplayPhysicalEvent countEvent,
        StockCountReplayGeneration generation,
        string itemCode,
        EffectiveStandardCost revision,
        IReadOnlyCollection<StockValuationFact> activeFacts,
        StockCountCostBasis? frozenBasis,
        StockValuationFact? originalAdjustmentFact = null)
    {
        ArgumentNullException.ThrowIfNull(countEvent);
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(activeFacts);
        if (generation.Id <= 0 || generation.IsRemoval
            || countEvent.EventId != -generation.Id
            || countEvent.Key.ClassPriority != StockReplayEventKey.CountClass
            || countEvent.Key.EffectiveAt != generation.EffectiveAt
            || countEvent.Key.EffectiveAt != StockReplayEventKey.CountEndOfDay(
                countEvent.Key.EffectiveAt)
            || countEvent.Key.OriginalPostingSequence
                != generation.AnchorPostingSequence
            || countEvent.Key.AttachedSuborder != 0
            || countEvent.Key.LineIdentity
                != $"C{generation.StockCountLineId:D10}"
            || countEvent.SignedDelta != generation.SignedAdjustmentQty
            || countEvent.AbsoluteCountQty is not decimal observation
            || observation < 0m || IvQty.Round(observation) != observation
            || revision.Id <= 0 || revision.ItemCode != itemCode
            || revision.EffectiveFrom.Date > countEvent.Key.EffectiveAt.Date
            || revision.EffectiveTo is DateTime end
                && end.Date <= countEvent.Key.EffectiveAt.Date)
            throw Invalid("The active Standard count has inconsistent physical or revision evidence.");
        if (observation > 0m
            && (frozenBasis is null
                || frozenBasis.StockCountLineId != generation.StockCountLineId
                || frozenBasis.CompanyCode != generation.CompanyCode
                || frozenBasis.BranchCode != generation.BranchCode
                || frozenBasis.CostBasisKind != StockCountCostBasisKinds.StandardRevision
                || frozenBasis.StandardCostRevisionId != revision.Id
                || frozenBasis.FrozenFallbackUnitCost != revision.TotalStandardCost
                || !StockCountAnchorCommandFactory.ValidBasis(frozenBasis))
            || observation == 0m && frozenBasis is not null)
            throw Invalid("The active Standard count lacks its frozen revision basis.");

        decimal oldValue = 0m;
        var oldQuantity = generation.SignedAdjustmentQty;
        if (oldQuantity == 0m)
        {
            if (generation.AdjustmentHistoryId is not null || activeFacts.Count != 0)
                throw Invalid("A zero Standard count has an unexpected adjustment valuation.");
        }
        else
        {
            if (generation.AdjustmentHistoryId is not int historyId
                || activeFacts.Count != 1)
                throw Invalid("The active Standard count has no single adjustment fact.");
            var active = activeFacts.Single();
            if (active.Id <= 0 || active.InventoryHistoryId != historyId
                || active.CompanyCode != generation.CompanyCode
                || active.BranchCode != generation.BranchCode
                || active.ItemCode != itemCode
                || active.EffectiveAt != generation.EffectiveAt
                || active.CostMethod != StockCostMethods.Standard
                || active.ValuationStatus != StockValuationStatuses.Valued
                || active.ReversesValuationFactId is not null
                || active.Direction != Math.Sign(oldQuantity)
                || active.BaseQty != Math.Abs(oldQuantity)
                || active.CostAmount != StockLedgerPrecision.Money(
                    Math.Abs(oldQuantity) * revision.TotalStandardCost))
                throw Invalid("The Standard count adjustment differs from its approved revision.");
            if (active.StockPostingId != generation.WritePostingId
                && (originalAdjustmentFact is null
                    || originalAdjustmentFact.Id <= 0
                    || originalAdjustmentFact.StockPostingId
                        != generation.WritePostingId
                    || originalAdjustmentFact.InventoryHistoryId != historyId
                    || originalAdjustmentFact.CompanyCode
                        != generation.CompanyCode
                    || originalAdjustmentFact.BranchCode
                        != generation.BranchCode
                    || originalAdjustmentFact.ItemCode != itemCode
                    || originalAdjustmentFact.EffectiveAt != active.EffectiveAt
                    || originalAdjustmentFact.CostMethod != active.CostMethod
                    || originalAdjustmentFact.Direction != active.Direction
                    || originalAdjustmentFact.BaseQty != active.BaseQty
                    || originalAdjustmentFact.ReversesValuationFactId is not null
                    || active.OriginalValuationFactId
                        != (originalAdjustmentFact.OriginalValuationFactId
                            ?? originalAdjustmentFact.Id)))
                throw Invalid("The repriced Standard count has no original adjustment lineage.");
            oldValue = StockLedgerPrecision.Money(
                active.SignedValue);
        }
        var pre = IvQty.Round(observation - oldQuantity);
        if (pre < 0m)
            throw Invalid("The active Standard count has a negative pre-count pile.");
        return new StockReplayStandardEvent(countEvent.EventId,
            countEvent.Key, StockReplayStandardEventKinds.Count,
            oldQuantity, oldValue, oldQuantity,
            revision.TotalStandardCost, revision.Id, observation, pre);
    }

    public static StockReplayStandardEvent CreateInitial(
        StockReplayPhysicalPlan physicalPlan,
        string itemCode,
        EffectiveStandardCost revision,
        StockCountCostBasis? frozenBasis)
    {
        ArgumentNullException.ThrowIfNull(physicalPlan);
        ArgumentNullException.ThrowIfNull(revision);
        var candidate = physicalPlan.Candidate;
        var step = physicalPlan.Projection.Candidate;
        if (physicalPlan.CountStatus is not ("COUNTED" or "ROLLED_BACK")
            || physicalPlan.CountLineId is not int lineId || lineId <= 0
            || physicalPlan.CountHeaderId is not > 0
            || candidate.EventId != -long.MaxValue + lineId
            || candidate.Key.ClassPriority != StockReplayEventKey.CountClass
            || candidate.Key.LineIdentity != $"C{lineId:D10}"
            || candidate.Key.AttachedSuborder != 0
            || candidate.Key.EffectiveAt != StockReplayEventKey.CountEndOfDay(
                candidate.Key.EffectiveAt)
            || candidate.AbsoluteCountQty is not decimal observation
            || observation < 0m || IvQty.Round(observation) != observation
            || step.EventId != candidate.EventId
            || step.QuantityAfter != observation
            || step.SignedDelta != IvQty.Round(
                observation - step.QuantityBefore))
            throw Invalid("The Standard count has no valid physical insertion plan.");

        var day = candidate.Key.EffectiveAt.Date;
        if (revision.Id <= 0 || string.IsNullOrWhiteSpace(itemCode)
            || revision.ItemCode != itemCode.Trim()
            || revision.EffectiveFrom.Date > day
            || revision.EffectiveTo is DateTime end && end.Date <= day
            || revision.TotalStandardCost < 0m)
            throw Invalid("The Standard count has no revision covering its event date.");
        if (observation > 0m
            && (frozenBasis is null
                || frozenBasis.StockCountLineId != lineId
                || frozenBasis.CompanyCode != physicalPlan.CompanyCode
                || frozenBasis.BranchCode != physicalPlan.BranchCode
                || frozenBasis.CostBasisKind != StockCountCostBasisKinds.StandardRevision
                || frozenBasis.StandardCostRevisionId != revision.Id
                || frozenBasis.FrozenFallbackUnitCost != revision.TotalStandardCost
                || !StockCountAnchorCommandFactory.ValidBasis(frozenBasis))
            || observation == 0m && frozenBasis is not null)
            throw Invalid("The Standard count lacks its exact frozen revision basis.");

        return new StockReplayStandardEvent(candidate.EventId, candidate.Key,
            StockReplayStandardEventKinds.Count,
            0m, 0m, step.SignedDelta,
            revision.TotalStandardCost, revision.Id,
            observation, step.QuantityBefore);
    }

    public static StockReplayStandardEvent BindProjectedPhysicalStep(
        StockReplayStandardEvent countEvent,
        StockReplayPhysicalStep physicalStep)
    {
        ArgumentNullException.ThrowIfNull(countEvent);
        ArgumentNullException.ThrowIfNull(physicalStep);
        if (countEvent.Kind != StockReplayStandardEventKinds.Count
            || countEvent.Key.ClassPriority != StockReplayEventKey.CountClass
            || countEvent.EventId != physicalStep.EventId
            || countEvent.AbsoluteCountQty is not decimal observation
            || physicalStep.QuantityBefore < 0m
            || IvQty.Round(physicalStep.QuantityBefore)
                != physicalStep.QuantityBefore
            || physicalStep.QuantityAfter != observation
            || physicalStep.SignedDelta != IvQty.Round(
                observation - physicalStep.QuantityBefore))
            throw Invalid("The Standard count has no matching physical pile projection.");
        return countEvent with
        {
            ReplaySignedQuantity = physicalStep.SignedDelta,
            CountPreQuantity = physicalStep.QuantityBefore
        };
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
