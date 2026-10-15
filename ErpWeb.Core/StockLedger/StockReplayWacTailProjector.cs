using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Combines one-pile physical insertion with the item-level WAC tail. Every
/// later count is bound to its own pile's projected pre-count quantity before
/// the financial simulator applies that pile's delta to the item pool.
/// </summary>
public static class StockReplayWacTailProjector
{
    public static StockReplayWacResult SimulateSinglePileInsertion(
        StockReplayWacFinancialTail tail,
        StockReplayPhysicalPlan physicalPlan,
        StockReplayWacEvent candidate)
    {
        ArgumentNullException.ThrowIfNull(tail);
        ArgumentNullException.ThrowIfNull(physicalPlan);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.EventId != physicalPlan.Candidate.EventId
            || candidate.Key != physicalPlan.Candidate.Key
            || !tail.PhysicalByPile.TryGetValue(physicalPlan.BalLocId, out var candidateTail)
            || !tail.CurrentQuantityByPile.TryGetValue(physicalPlan.BalLocId,
                out var candidatePileQuantity)
            || candidatePileQuantity != physicalPlan.CurrentQuantity
            || !candidateTail.SequenceEqual(physicalPlan.LaterEvents)
            || tail.CurrentQuantityByPile.Count != tail.PhysicalByPile.Count
            || tail.CurrentQuantityByPile.Sum(p => p.Value) != tail.CurrentQuantity)
            throw Corrupt("The WAC and physical insertion plans differ.");

        var candidateEvent = physicalPlan.Candidate.AbsoluteCountQty.HasValue
            ? StockReplayWacCountEventFactory.BindProjectedPhysicalStep(
                candidate, physicalPlan.Projection.Candidate)
            : candidate;
        if (candidateEvent.ReplaySignedQuantity
                != physicalPlan.Projection.Candidate.SignedDelta
            && candidateEvent.AbsoluteCountQty is null)
            throw Corrupt("The WAC candidate quantity differs from the physical insertion.");

        var (historicalItemQuantity, bound) = BindLater(
            tail, physicalPlan.BalLocId, candidate.Key,
            physicalPlan.Projection.HistoricalQuantityBeforeCandidate,
            physicalPlan.Projection.LaterSteps);
        var financial = StockReplayWacSimulator.SimulateInsertion(
            tail.CurrentQuantity, tail.CurrentValue, candidateEvent, bound);
        if (financial.HistoricalQuantityBeforeCandidate != historicalItemQuantity
            || financial.FinalQuantity != IvQty.Round(tail.CurrentQuantity
                - physicalPlan.CurrentQuantity
                + physicalPlan.Projection.FinalQuantity))
            throw Corrupt("The projected WAC item quantity does not reconcile to its piles.");
        return financial;
    }

    public static StockReplayWacRemovalResult SimulateSinglePileRemoval(
        StockReplayWacFinancialTail tail,
        StockReplayPhysicalRemovalPlan physicalPlan,
        StockReplayWacEvent removedActiveEvent)
    {
        ArgumentNullException.ThrowIfNull(tail);
        ArgumentNullException.ThrowIfNull(physicalPlan);
        ArgumentNullException.ThrowIfNull(removedActiveEvent);
        if (removedActiveEvent.EventId != physicalPlan.RemovedActiveEvent.EventId
            || removedActiveEvent.Key != physicalPlan.RemovedActiveEvent.Key
            || !tail.PhysicalByPile.TryGetValue(physicalPlan.BalLocId,
                out var removedPileTail)
            || !tail.CurrentQuantityByPile.TryGetValue(physicalPlan.BalLocId,
                out var removedPileCurrent)
            || removedPileCurrent != physicalPlan.CurrentQuantity
            || !removedPileTail.SequenceEqual(physicalPlan.LaterEvents)
            || tail.CurrentQuantityByPile.Count != tail.PhysicalByPile.Count
            || tail.CurrentQuantityByPile.Sum(p => p.Value) != tail.CurrentQuantity)
            throw Corrupt("The WAC and physical removal plans differ.");

        var removed = physicalPlan.RemovedActiveEvent.AbsoluteCountQty.HasValue
            ? StockReplayWacCountEventFactory.BindProjectedPhysicalStep(
                removedActiveEvent,
                new StockReplayPhysicalStep(removedActiveEvent.EventId,
                    physicalPlan.Projection.HistoricalQuantityBeforeRemovedEvent,
                    physicalPlan.RemovedActiveEvent.SignedDelta,
                    physicalPlan.RemovedActiveEvent.AbsoluteCountQty!.Value))
            : removedActiveEvent;
        if (removed.OldSignedQuantity
                != physicalPlan.RemovedActiveEvent.SignedDelta)
            throw Corrupt("The removed WAC quantity differs from its active physical event.");

        var (historicalItemQuantity, bound) = BindLater(
            tail, physicalPlan.BalLocId, removed.Key,
            physicalPlan.Projection.HistoricalQuantityBeforeRemovedEvent,
            physicalPlan.Projection.LaterSteps);
        var financial = StockReplayWacSimulator.SimulateRemoval(
            tail.CurrentQuantity, tail.CurrentValue, removed, bound);
        if (financial.HistoricalQuantityBeforeRemovedEvent != historicalItemQuantity
            || financial.FinalQuantity != IvQty.Round(tail.CurrentQuantity
                - physicalPlan.CurrentQuantity
                + physicalPlan.Projection.FinalQuantity))
            throw Corrupt("The removed WAC item quantity does not reconcile to its piles.");
        return financial;
    }

    private static (decimal HistoricalItemQuantity,
        IReadOnlyList<StockReplayWacEvent> BoundEvents) BindLater(
        StockReplayWacFinancialTail tail,
        int changedPileId,
        StockReplayEventKey after,
        decimal changedPileHistoricalQuantity,
        IReadOnlyList<StockReplayPhysicalStep> changedPileLaterSteps)
    {
        var steps = new Dictionary<long, List<StockReplayPhysicalStep>>();
        var historicalItemQuantity = 0m;
        foreach (var pile in tail.PhysicalByPile)
        {
            if (!tail.CurrentQuantityByPile.TryGetValue(pile.Key, out var current))
                throw Corrupt($"Physical pile {pile.Key} has no current quantity.");
            StockReplayPhysicalTailResult projection;
            if (pile.Key == changedPileId)
                projection = new StockReplayPhysicalTailResult(
                    changedPileHistoricalQuantity, changedPileLaterSteps, current);
            else
                projection = StockReplayPhysicalSimulator.ProjectUnchangedTail(
                    current, after, pile.Value);
            historicalItemQuantity = IvQty.Round(
                historicalItemQuantity + projection.HistoricalQuantityBeforeTail);
            foreach (var step in projection.LaterSteps)
            {
                if (!steps.TryGetValue(step.EventId, out var eventSteps))
                    steps.Add(step.EventId, eventSteps = []);
                eventSteps.Add(step);
            }
        }

        var bound = new List<StockReplayWacEvent>(tail.LaterEvents.Count);
        foreach (var eventRow in tail.LaterEvents)
        {
            if (steps.TryGetValue(eventRow.EventId, out var physicalSteps))
            {
                if (eventRow.AbsoluteCountQty.HasValue)
                {
                    if (physicalSteps.Count != 1)
                        throw Corrupt($"Count event {eventRow.EventId} spans multiple piles.");
                    bound.Add(StockReplayWacCountEventFactory.BindProjectedPhysicalStep(
                        eventRow, physicalSteps[0]));
                }
                else
                {
                    if (IvQty.Round(physicalSteps.Sum(s => s.SignedDelta))
                            != eventRow.OldSignedQuantity
                        || physicalSteps.Any(s => s.SignedDelta == 0m))
                        throw Corrupt($"WAC event {eventRow.EventId} differs from its physical legs.");
                    bound.Add(eventRow);
                }
                steps.Remove(eventRow.EventId);
            }
            else if (eventRow.EventId > int.MaxValue
                && eventRow.Key.LineIdentity.StartsWith('V')
                && eventRow.OldSignedQuantity == 0m
                && eventRow.ReplaySignedQuantity == 0m
                && eventRow.AbsoluteCountQty is null)
                bound.Add(eventRow);
            else
                throw Corrupt($"WAC event {eventRow.EventId} has no physical authority.");
        }
        if (steps.Count != 0)
            throw Corrupt("A physical descendant has no active WAC event.");
        return (historicalItemQuantity, bound);
    }

    private static StockLedgerException Corrupt(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
