using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Combines one-pile physical insertion with the item-level WAC tail. Every
/// later count is bound to its own pile's projected pre-count quantity before
/// the financial simulator applies that pile's delta to the item pool.
/// </summary>
public static class StockReplayWacTailProjector
{
    /// <summary>
    /// Projects all counted piles of one item through one shared WAC pool.
    /// Count candidates are applied in their canonical line-key order, with
    /// later sealed movements bound to each pile's projected physical step.
    /// </summary>
    public static StockReplayWacResult SimulateCountSheetInsertion(
        StockReplayWacFinancialTail tail,
        IReadOnlyCollection<StockReplayPhysicalPlan> physicalPlans,
        IReadOnlyCollection<StockReplayWacEvent> candidates)
    {
        ArgumentNullException.ThrowIfNull(tail);
        ArgumentNullException.ThrowIfNull(physicalPlans);
        ArgumentNullException.ThrowIfNull(candidates);
        if (physicalPlans.Count == 0 || physicalPlans.Count != candidates.Count
            || tail.PhysicalByPile.Count != tail.CurrentQuantityByPile.Count
            || tail.CurrentQuantityByPile.Sum(p => p.Value) != tail.CurrentQuantity)
            throw Corrupt("The count sheet has no complete item-level WAC evidence.");

        var plans = physicalPlans.OrderBy(p => p.Candidate.Key).ToArray();
        var first = plans[0];
        var earliest = first.Candidate.Key;
        var byPile = new Dictionary<int, StockReplayPhysicalPlan>();
        var byCandidate = new Dictionary<long, StockReplayWacEvent>();
        foreach (var plan in plans)
        {
            if (plan.CountStatus != "COUNTED"
                || plan.CountHeaderId != first.CountHeaderId
                || plan.CountHeaderId is not > 0
                || plan.CountLineId is not > 0
                || plan.Candidate.EventId
                    != -long.MaxValue + plan.CountLineId.Value
                || plan.CompanyCode != first.CompanyCode
                || plan.BranchCode != first.BranchCode
                || plan.LedgerEpochId != first.LedgerEpochId
                || plan.BranchWatermark != first.BranchWatermark
                || plan.Candidate.Key.ClassPriority != StockReplayEventKey.CountClass
                || plan.Candidate.Key.EffectiveAt != earliest.EffectiveAt
                || plan.Candidate.Key.OriginalPostingSequence
                    != earliest.OriginalPostingSequence
                || plan.Candidate.Key.LineIdentity
                    != $"C{plan.CountLineId:D10}"
                || plan.Candidate.Key.AttachedSuborder != 0
                || plan.Candidate.Key.EffectiveAt
                    != StockReplayEventKey.CountEndOfDay(
                        plan.Candidate.Key.EffectiveAt)
                || plan.Candidate.AbsoluteCountQty is null
                || !byPile.TryAdd(plan.BalLocId, plan))
                throw Corrupt("Count candidates have inconsistent sheet or pile identities.");
        }
        foreach (var candidate in candidates)
        {
            if (!byCandidate.TryAdd(candidate.EventId, candidate))
                throw Corrupt("The count sheet repeats a WAC candidate identity.");
        }
        if (tail.LaterEvents.Any(e => e.Key.CompareTo(earliest) <= 0
                || byCandidate.ContainsKey(e.EventId)))
            throw Corrupt("The WAC tail overlaps a count candidate.");

        var steps = new Dictionary<long, List<StockReplayPhysicalStep>>();
        var candidateSteps = new Dictionary<long, StockReplayPhysicalStep>();
        decimal historicalItemQuantity = 0m;
        decimal finalItemQuantity = 0m;
        foreach (var pile in tail.PhysicalByPile)
        {
            if (!tail.CurrentQuantityByPile.TryGetValue(pile.Key, out var current))
                throw Corrupt($"Physical pile {pile.Key} has no current quantity.");
            decimal historical;
            decimal final;
            var projectedSteps = new List<StockReplayPhysicalStep>();
            if (byPile.TryGetValue(pile.Key, out var plan))
            {
                if (!byCandidate.TryGetValue(plan.Candidate.EventId,
                        out var candidate)
                    || candidate.Key != plan.Candidate.Key
                    || candidate.OldSignedQuantity != 0m
                    || candidate.OldSignedValue != 0m
                    || candidate.ReplaySignedQuantity
                        != plan.Projection.Candidate.SignedDelta
                    || candidate.AbsoluteCountQty
                        != plan.Candidate.AbsoluteCountQty
                    || plan.CurrentQuantity != current
                    || plan.Candidate.Key.CompareTo(earliest) < 0
                    || pile.Value.Any(e => e.Key == plan.Candidate.Key))
                    throw Corrupt($"Count candidate on pile {pile.Key} differs from its WAC event.");
                var prefix = pile.Value.Where(e =>
                    e.Key.CompareTo(plan.Candidate.Key) < 0).ToArray();
                var suffix = pile.Value.Where(e =>
                    e.Key.CompareTo(plan.Candidate.Key) > 0).ToArray();
                if (!suffix.SequenceEqual(plan.LaterEvents))
                    throw Corrupt($"Count candidate on pile {pile.Key} has a different active tail.");
                var physical = StockReplayPhysicalSimulator.Simulate(
                    current, plan.Candidate, plan.LaterEvents);
                if (physical.HistoricalQuantityBeforeCandidate
                        != plan.Projection.HistoricalQuantityBeforeCandidate
                    || physical.Candidate != plan.Projection.Candidate
                    || !physical.LaterSteps.SequenceEqual(plan.Projection.LaterSteps)
                    || physical.FinalQuantity != plan.Projection.FinalQuantity)
                    throw Corrupt($"Count candidate on pile {pile.Key} has a changed projection.");
                if (prefix.Length > 0)
                {
                    var before = StockReplayPhysicalSimulator.ProjectUnchangedTail(
                        physical.HistoricalQuantityBeforeCandidate,
                        earliest, prefix);
                    historical = before.HistoricalQuantityBeforeTail;
                    projectedSteps.AddRange(before.LaterSteps);
                }
                else
                    historical = physical.HistoricalQuantityBeforeCandidate;
                projectedSteps.AddRange(physical.LaterSteps);
                final = physical.FinalQuantity;
                candidateSteps.Add(plan.Candidate.EventId, physical.Candidate);
            }
            else
            {
                var unchanged = StockReplayPhysicalSimulator.ProjectUnchangedTail(
                    current, earliest, pile.Value);
                historical = unchanged.HistoricalQuantityBeforeTail;
                final = unchanged.FinalQuantity;
                projectedSteps.AddRange(unchanged.LaterSteps);
            }
            historicalItemQuantity = IvQty.Round(historicalItemQuantity + historical);
            finalItemQuantity = IvQty.Round(finalItemQuantity + final);
            foreach (var step in projectedSteps)
            {
                if (!steps.TryGetValue(step.EventId, out var eventSteps))
                    steps.Add(step.EventId, eventSteps = []);
                eventSteps.Add(step);
            }
        }
        if (byPile.Count != candidateSteps.Count)
            throw Corrupt("A count candidate pile is missing from the item tail.");
        var bound = BindFinancialEvents(tail.LaterEvents, steps);
        var orderedCandidates = plans.Select(p =>
            StockReplayWacCountEventFactory.BindProjectedPhysicalStep(
                byCandidate[p.Candidate.EventId],
                candidateSteps[p.Candidate.EventId])).ToArray();
        var projection = StockReplayWacSimulator.SimulateInsertion(
            tail.CurrentQuantity, tail.CurrentValue, orderedCandidates[0],
            bound.Concat(orderedCandidates.Skip(1)).ToArray());
        if (projection.HistoricalQuantityBeforeCandidate
                != historicalItemQuantity
            || projection.FinalQuantity != finalItemQuantity)
            throw Corrupt("The count sheet WAC pool does not reconcile to its piles.");
        return projection;
    }

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

        return (historicalItemQuantity,
            BindFinancialEvents(tail.LaterEvents, steps));
    }

    private static IReadOnlyList<StockReplayWacEvent> BindFinancialEvents(
        IReadOnlyList<StockReplayWacEvent> financialEvents,
        Dictionary<long, List<StockReplayPhysicalStep>> steps)
    {
        var bound = new List<StockReplayWacEvent>(financialEvents.Count);
        foreach (var eventRow in financialEvents)
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
                && (eventRow.Key.LineIdentity.StartsWith('V')
                    || eventRow.Key.LineIdentity.StartsWith('H')
                        && eventRow.Key.AttachedSuborder > 0)
                && eventRow.OldSignedQuantity == 0m
                && eventRow.ReplaySignedQuantity == 0m
                && eventRow.AbsoluteCountQty is null)
                bound.Add(eventRow);
            else
                throw Corrupt($"WAC event {eventRow.EventId} has no physical authority.");
        }
        if (steps.Count != 0)
            throw Corrupt("A physical descendant has no active WAC event.");
        return bound;
    }

    private static StockLedgerException Corrupt(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
