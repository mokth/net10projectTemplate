using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Binds a one-pile physical insertion to the item-level Standard tail.
/// Later count resets use their own projected pile quantities; revaluations
/// retain their original event keys and retarget the changed item quantity.
/// </summary>
public static class StockReplayStandardTailProjector
{
    public static StockReplayStandardRemovalResult SimulateSinglePileRemoval(
        StockReplayStandardFinancialTail tail,
        StockReplayPhysicalRemovalPlan physicalPlan,
        StockReplayStandardEvent removedActiveEvent)
    {
        ArgumentNullException.ThrowIfNull(tail);
        ArgumentNullException.ThrowIfNull(physicalPlan);
        ArgumentNullException.ThrowIfNull(removedActiveEvent);
        if (removedActiveEvent.EventId
                != physicalPlan.RemovedActiveEvent.EventId
            || removedActiveEvent.Key != physicalPlan.RemovedActiveEvent.Key
            || !tail.PhysicalByPile.TryGetValue(physicalPlan.BalLocId,
                out var removedPileTail)
            || !tail.CurrentQuantityByPile.TryGetValue(physicalPlan.BalLocId,
                out var pileQuantity)
            || pileQuantity != physicalPlan.CurrentQuantity
            || !removedPileTail.SequenceEqual(physicalPlan.LaterEvents)
            || removedActiveEvent.Kind == StockReplayStandardEventKinds.Count
                && physicalPlan.RemovedActiveEvent.AbsoluteCountQty is null
            || tail.CurrentQuantityByPile.Count != tail.PhysicalByPile.Count
            || tail.CurrentQuantityByPile.Sum(p => p.Value)
                != tail.CurrentQuantity)
            throw Invalid("The Standard and physical removal plans differ.");

        var removed = removedActiveEvent.Kind
                == StockReplayStandardEventKinds.Count
            ? StockReplayStandardCountEventFactory.BindProjectedPhysicalStep(
                removedActiveEvent,
                new StockReplayPhysicalStep(removedActiveEvent.EventId,
                    physicalPlan.Projection.HistoricalQuantityBeforeRemovedEvent,
                    physicalPlan.RemovedActiveEvent.SignedDelta,
                    physicalPlan.RemovedActiveEvent.AbsoluteCountQty!.Value))
            : removedActiveEvent;
        if (removed.OldSignedQuantity
                != physicalPlan.RemovedActiveEvent.SignedDelta)
            throw Invalid("The removed Standard value differs from its physical event.");
        var (historicalItemQuantity, boundLater) = BindLater(
            tail, physicalPlan.BalLocId, removed.Key,
            physicalPlan.Projection.HistoricalQuantityBeforeRemovedEvent,
            physicalPlan.Projection.LaterSteps);
        var financial = StockReplayStandardSimulator.SimulateRemoval(
            tail.CurrentQuantity, tail.CurrentValue, removed, boundLater);
        if (financial.HistoricalQuantityBeforeRemovedEvent
                != historicalItemQuantity
            || financial.FinalQuantity != IvQty.Round(
                tail.CurrentQuantity - physicalPlan.CurrentQuantity
                    + physicalPlan.Projection.FinalQuantity))
            throw Invalid("The removed Standard item differs from its piles.");
        return financial;
    }

    public static StockReplayStandardResult SimulateSinglePileInsertion(
        StockReplayStandardFinancialTail tail,
        StockReplayPhysicalPlan physicalPlan,
        StockReplayStandardEvent candidate)
    {
        ArgumentNullException.ThrowIfNull(tail);
        ArgumentNullException.ThrowIfNull(physicalPlan);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.EventId != physicalPlan.Candidate.EventId
            || candidate.Key != physicalPlan.Candidate.Key
            || !tail.PhysicalByPile.TryGetValue(physicalPlan.BalLocId,
                out var candidateTail)
            || !tail.CurrentQuantityByPile.TryGetValue(physicalPlan.BalLocId,
                out var pileQuantity)
            || pileQuantity != physicalPlan.CurrentQuantity
            || !candidateTail.SequenceEqual(physicalPlan.LaterEvents)
            || tail.CurrentQuantityByPile.Count != tail.PhysicalByPile.Count
            || tail.CurrentQuantityByPile.Sum(p => p.Value)
                != tail.CurrentQuantity)
            throw Invalid("The Standard and physical insertion plans differ.");

        var boundCandidate = candidate.Kind == StockReplayStandardEventKinds.Count
            ? StockReplayStandardCountEventFactory.BindProjectedPhysicalStep(
                candidate, physicalPlan.Projection.Candidate)
            : candidate;
        if (boundCandidate.ReplaySignedQuantity
                != physicalPlan.Projection.Candidate.SignedDelta)
            throw Invalid("The Standard candidate differs from its physical delta.");

        var (historicalItemQuantity, boundLater) = BindLater(
            tail, physicalPlan.BalLocId, candidate.Key,
            physicalPlan.Projection.HistoricalQuantityBeforeCandidate,
            physicalPlan.Projection.LaterSteps);
        var financial = StockReplayStandardSimulator.SimulateInsertion(
            tail.CurrentQuantity, tail.CurrentValue,
            boundCandidate, boundLater);
        if (financial.HistoricalQuantityBeforeCandidate
                != historicalItemQuantity
            || financial.FinalQuantity != IvQty.Round(
                tail.CurrentQuantity - physicalPlan.CurrentQuantity
                    + physicalPlan.Projection.FinalQuantity))
            throw Invalid("The Standard item projection differs from its piles.");
        return financial;
    }

    private static (decimal HistoricalQuantity,
        IReadOnlyList<StockReplayStandardEvent> BoundLater) BindLater(
        StockReplayStandardFinancialTail tail,
        int changedPileId,
        StockReplayEventKey after,
        decimal changedPileHistoricalQuantity,
        IReadOnlyList<StockReplayPhysicalStep> changedPileLaterSteps)
    {
        var steps = new Dictionary<long, List<StockReplayPhysicalStep>>();
        var historicalQuantity = 0m;
        foreach (var pile in tail.PhysicalByPile)
        {
            if (!tail.CurrentQuantityByPile.TryGetValue(pile.Key,
                out var current))
                throw Invalid($"Pile {pile.Key} has no current quantity.");
            var projection = pile.Key == changedPileId
                ? new StockReplayPhysicalTailResult(
                    changedPileHistoricalQuantity,
                    changedPileLaterSteps, current)
                : StockReplayPhysicalSimulator.ProjectUnchangedTail(
                    current, after, pile.Value);
            historicalQuantity = IvQty.Round(historicalQuantity
                + projection.HistoricalQuantityBeforeTail);
            foreach (var step in projection.LaterSteps)
            {
                if (!steps.TryGetValue(step.EventId, out var eventSteps))
                    steps.Add(step.EventId, eventSteps = []);
                eventSteps.Add(step);
            }
        }

        return (historicalQuantity,
            BindFinancialEvents(tail.LaterEvents, steps));
    }

    internal static IReadOnlyList<StockReplayStandardEvent> BindFinancialEvents(
        IReadOnlyList<StockReplayStandardEvent> financialEvents,
        Dictionary<long, List<StockReplayPhysicalStep>> steps)
    {
        var bound = new List<StockReplayStandardEvent>(financialEvents.Count);
        foreach (var e in financialEvents)
        {
            if (steps.TryGetValue(e.EventId, out var physicalSteps))
            {
                if (e.Kind == StockReplayStandardEventKinds.Count)
                {
                    if (physicalSteps.Count != 1)
                        throw Invalid($"Standard count {e.EventId} spans piles.");
                    bound.Add(StockReplayStandardCountEventFactory
                        .BindProjectedPhysicalStep(e, physicalSteps[0]));
                }
                else if (e.Kind == StockReplayStandardEventKinds.Movement)
                {
                    if (IvQty.Round(physicalSteps.Sum(s => s.SignedDelta))
                            != e.OldSignedQuantity
                        || physicalSteps.Any(s => s.SignedDelta == 0m))
                        throw Invalid($"Standard movement {e.EventId} differs from its physical legs.");
                    bound.Add(e);
                }
                else
                    throw Invalid($"Standard revaluation {e.EventId} has a physical leg.");
                steps.Remove(e.EventId);
            }
            else if (e.Kind == StockReplayStandardEventKinds.Revaluation
                && e.EventId > int.MaxValue
                && e.Key.LineIdentity.StartsWith('V')
                && e.OldSignedQuantity == 0m
                && e.ReplaySignedQuantity == 0m)
                bound.Add(e);
            else
                throw Invalid($"Standard event {e.EventId} has no physical authority.");
        }
        if (steps.Count != 0)
            throw Invalid("A physical descendant has no Standard event.");
        return bound;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
