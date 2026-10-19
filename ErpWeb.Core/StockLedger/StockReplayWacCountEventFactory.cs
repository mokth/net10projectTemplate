using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Converts a sealed active count generation and its complete active WAC fact
/// set into one item-level financial reset at the original count key.
/// </summary>
public static class StockReplayWacCountEventFactory
{
    /// <summary>
    /// Creates the financial candidate for an initial, still unposted count.
    /// The physical insertion plan supplies its historical adjustment; the
    /// frozen basis is used only if the historical item pool is empty.
    /// </summary>
    public static StockReplayWacEvent CreateInitial(
        StockReplayPhysicalPlan physicalPlan,
        StockCountCostBasis? frozenBasis)
    {
        ArgumentNullException.ThrowIfNull(physicalPlan);
        var candidate = physicalPlan.Candidate;
        if (physicalPlan.CountStatus is not ("COUNTED" or "ROLLED_BACK")
            || physicalPlan.CountLineId is not int lineId || lineId <= 0
            || physicalPlan.CountHeaderId is not > 0
            || candidate.EventId != -long.MaxValue + lineId
            || candidate.Key.ClassPriority != StockReplayEventKey.CountClass
            || candidate.Key.LineIdentity != $"C{lineId:D10}"
            || candidate.Key.AttachedSuborder != 0
            || candidate.AbsoluteCountQty is not decimal physical
            || physical < 0m || IvQty.Round(physical) != physical
            || physicalPlan.Projection.Candidate.EventId != candidate.EventId
            || physicalPlan.Projection.Candidate.QuantityAfter != physical
            || physicalPlan.Projection.Candidate.SignedDelta
                != IvQty.Round(physical
                    - physicalPlan.Projection.Candidate.QuantityBefore))
            throw Invalid("The initial count has no valid physical insertion plan.");
        if (physical > 0m
            && (frozenBasis is null
                || frozenBasis.StockCountLineId != lineId
                || frozenBasis.CompanyCode != physicalPlan.CompanyCode
                || frozenBasis.BranchCode != physicalPlan.BranchCode
                || frozenBasis.CostBasisKind is not (
                    StockCountCostBasisKinds.LedgerWac
                    or StockCountCostBasisKinds.ManualApproved
                    or StockCountCostBasisKinds.ZeroCostApproved)
                || !StockCountAnchorCommandFactory.ValidBasis(frozenBasis)
                || frozenBasis.FrozenFallbackUnitCost < 0m
                || StockLedgerPrecision.Money(frozenBasis.FrozenFallbackUnitCost)
                    != frozenBasis.FrozenFallbackUnitCost)
            || physical == 0m && frozenBasis is not null)
            throw Invalid("The initial count lacks its exact frozen WAC basis.");

        return new StockReplayWacEvent(candidate.EventId, candidate.Key,
            0m, 0m, physicalPlan.Projection.Candidate.SignedDelta,
            AbsoluteCountQty: physical,
            CountFallbackUnitCost: frozenBasis?.FrozenFallbackUnitCost);
    }

    /// <summary>
    /// Binds the count to a separately proven physical projection of its pile.
    /// The financial simulator then applies that pile's delta to the item pool.
    /// </summary>
    public static StockReplayWacEvent BindProjectedPhysicalStep(
        StockReplayWacEvent countEvent,
        StockReplayPhysicalStep physicalStep)
    {
        ArgumentNullException.ThrowIfNull(countEvent);
        ArgumentNullException.ThrowIfNull(physicalStep);
        if (countEvent.Key.ClassPriority != StockReplayEventKey.CountClass
            || countEvent.EventId != physicalStep.EventId
            || countEvent.AbsoluteCountQty is not decimal observation
            || physicalStep.QuantityBefore < 0m
            || IvQty.Round(physicalStep.QuantityBefore) != physicalStep.QuantityBefore
            || physicalStep.QuantityAfter != observation
            || physicalStep.SignedDelta
                != IvQty.Round(observation - physicalStep.QuantityBefore))
            throw Invalid("The count WAC event has no matching physical pile projection.");
        return countEvent with { CountPreQuantity = physicalStep.QuantityBefore };
    }

    public static StockReplayWacEvent Create(
        StockReplayPhysicalEvent countEvent,
        StockCountReplayGeneration generation,
        IReadOnlyCollection<StockValuationFact> activeFacts,
        StockCountCostBasis? frozenBasis,
        StockValuationFact? originalAdjustmentFact = null)
    {
        ArgumentNullException.ThrowIfNull(countEvent);
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(activeFacts);
        if (generation.Id <= 0 || generation.IsRemoval
            || countEvent.EventId != -generation.Id
            || countEvent.Key.ClassPriority != StockReplayEventKey.CountClass
            || countEvent.Key.EffectiveAt != generation.EffectiveAt
            || countEvent.Key.OriginalPostingSequence
                != generation.AnchorPostingSequence
            || countEvent.Key.AttachedSuborder != 0
            || countEvent.Key.LineIdentity
                != $"C{generation.StockCountLineId:D10}"
            || countEvent.SignedDelta != generation.SignedAdjustmentQty
            || countEvent.AbsoluteCountQty is not decimal physical
            || physical < 0m || IvQty.Round(physical) != physical)
            throw Invalid("The count's physical event differs from its active generation.");
        if (physical > 0m
            && (frozenBasis is null
                || frozenBasis.StockCountLineId != generation.StockCountLineId
                || frozenBasis.CompanyCode != generation.CompanyCode
                || frozenBasis.BranchCode != generation.BranchCode
                || frozenBasis.CostBasisKind is not (
                    StockCountCostBasisKinds.LedgerWac
                    or StockCountCostBasisKinds.ManualApproved
                    or StockCountCostBasisKinds.ZeroCostApproved)
                || !StockCountAnchorCommandFactory.ValidBasis(frozenBasis)
                || frozenBasis.FrozenFallbackUnitCost < 0m
                || StockLedgerPrecision.Money(frozenBasis.FrozenFallbackUnitCost)
                    != frozenBasis.FrozenFallbackUnitCost)
            || physical == 0m && frozenBasis is not null)
            throw Invalid("The count lacks one immutable fallback basis for its physical observation.");

        if (generation.SignedAdjustmentQty == 0m)
        {
            if (generation.AdjustmentHistoryId is not null || activeFacts.Count != 0)
                throw Invalid("A zero count adjustment has an unexpected valuation fact.");
            return new StockReplayWacEvent(countEvent.EventId, countEvent.Key,
                0m, 0m, 0m, AbsoluteCountQty: physical,
                CountFallbackUnitCost: frozenBasis?.FrozenFallbackUnitCost);
        }

        if (generation.AdjustmentHistoryId is not int historyId
            || activeFacts.Count != 1
            || activeFacts.Any(f => f.InventoryHistoryId != historyId
                || f.CompanyCode != generation.CompanyCode
                || f.BranchCode != generation.BranchCode
                || f.EffectiveAt != generation.EffectiveAt
                || f.CostMethod != StockCostMethods.MovingAverage
                || f.ValuationStatus != StockValuationStatuses.Valued
                || f.ReversesValuationFactId is not null
                || f.Direction != Math.Sign(generation.SignedAdjustmentQty)
                || f.BaseQty != Math.Abs(generation.SignedAdjustmentQty)
                || f.CostAmount < 0m))
            throw Invalid("The count adjustment has no complete active WAC valuation.");
        var active = activeFacts.Single();
        if (active.StockPostingId != generation.WritePostingId
            && (originalAdjustmentFact is null
                || originalAdjustmentFact.Id <= 0
                || originalAdjustmentFact.StockPostingId != generation.WritePostingId
                || originalAdjustmentFact.InventoryHistoryId != historyId
                || originalAdjustmentFact.CompanyCode != generation.CompanyCode
                || originalAdjustmentFact.BranchCode != generation.BranchCode
                || originalAdjustmentFact.ItemCode != active.ItemCode
                || originalAdjustmentFact.EffectiveAt != active.EffectiveAt
                || originalAdjustmentFact.BusinessDate != active.BusinessDate
                || originalAdjustmentFact.PeriodKey != active.PeriodKey
                || originalAdjustmentFact.Direction != active.Direction
                || originalAdjustmentFact.BaseQty != active.BaseQty
                || originalAdjustmentFact.CostMethod != active.CostMethod
                || originalAdjustmentFact.ReversesValuationFactId is not null
                || active.OriginalValuationFactId
                    != (originalAdjustmentFact.OriginalValuationFactId
                        ?? originalAdjustmentFact.Id)))
            throw Invalid("The corrected count fact has no original adjustment lineage.");
        var oldValue = StockLedgerPrecision.Money(active.SignedValue);
        return new StockReplayWacEvent(countEvent.EventId, countEvent.Key,
            generation.SignedAdjustmentQty, oldValue,
            generation.SignedAdjustmentQty,
            AbsoluteCountQty: physical,
            CountFallbackUnitCost: frozenBasis?.FrozenFallbackUnitCost);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
