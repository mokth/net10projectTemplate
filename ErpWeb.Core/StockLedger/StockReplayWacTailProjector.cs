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
    /// Removes every count event for one item together, then replays the
    /// unchanged physical descendants through the shared WAC pool.
    /// </summary>
    public static StockReplayWacRemovalResult SimulateCountSheetRemoval(
        StockReplayWacFinancialTail tail,
        IReadOnlyCollection<StockReplayPhysicalRemovalPlan> physicalPlans,
        IReadOnlyCollection<StockReplayWacEvent> removedEvents)
    {
        ArgumentNullException.ThrowIfNull(tail);
        ArgumentNullException.ThrowIfNull(physicalPlans);
        ArgumentNullException.ThrowIfNull(removedEvents);
        if (physicalPlans.Count == 0
            || physicalPlans.Count != removedEvents.Count
            || tail.PhysicalByPile.Count != tail.CurrentQuantityByPile.Count
            || tail.CurrentQuantityByPile.Sum(p => p.Value) != tail.CurrentQuantity)
            throw Corrupt("The count removal has incomplete item-level WAC evidence.");

        var plans = physicalPlans.OrderBy(p => p.RemovedActiveEvent.Key).ToArray();
        var first = plans[0];
        var firstKey = first.RemovedActiveEvent.Key;
        var lastKey = plans[^1].RemovedActiveEvent.Key;
        var byPile = new Dictionary<int, StockReplayPhysicalRemovalPlan>();
        foreach (var plan in plans)
        {
            var key = plan.RemovedActiveEvent.Key;
            if (plan.CompanyCode != first.CompanyCode
                || plan.BranchCode != first.BranchCode
                || plan.LedgerEpochId != first.LedgerEpochId
                || plan.BranchWatermark != first.BranchWatermark
                || key.ClassPriority != StockReplayEventKey.CountClass
                || key.EffectiveAt != firstKey.EffectiveAt
                || key.OriginalPostingSequence
                    != firstKey.OriginalPostingSequence
                || key.AttachedSuborder != 0
                || key.EffectiveAt != StockReplayEventKey.CountEndOfDay(
                    key.EffectiveAt)
                || plan.RemovedActiveEvent.EventId >= 0
                || plan.RemovedActiveEvent.AbsoluteCountQty is null
                || !byPile.TryAdd(plan.BalLocId, plan))
                throw Corrupt("The removed count lines do not share one item anchor.");
        }
        var removedById = new Dictionary<long, StockReplayWacEvent>();
        foreach (var removed in removedEvents)
            if (!removedById.TryAdd(removed.EventId, removed))
                throw Corrupt("A WAC count removal repeats an event identity.");
        if (tail.LaterEvents.Count(e => removedById.ContainsKey(e.EventId))
                != plans.Length
            || tail.LaterEvents.Any(e => !removedById.ContainsKey(e.EventId)
                && e.Key.CompareTo(lastKey) <= 0))
            throw Corrupt("The WAC item tail overlaps the removed count sheet.");

        var physicalSteps = new Dictionary<long, List<StockReplayPhysicalStep>>();
        var boundRemoved = new List<StockReplayWacEvent>(plans.Length);
        decimal historicalQuantity = 0m;
        decimal finalQuantity = 0m;
        foreach (var pile in tail.PhysicalByPile)
        {
            if (!tail.CurrentQuantityByPile.TryGetValue(pile.Key,
                    out var current))
                throw Corrupt($"Physical pile {pile.Key} has no current quantity.");
            StockReplayPhysicalTailResult projection;
            if (byPile.TryGetValue(pile.Key, out var plan))
            {
                if (!removedById.TryGetValue(
                        plan.RemovedActiveEvent.EventId, out var removed)
                    || removed.Key != plan.RemovedActiveEvent.Key
                    || removed.OldSignedQuantity
                        != plan.RemovedActiveEvent.SignedDelta
                    || removed.AbsoluteCountQty
                        != plan.RemovedActiveEvent.AbsoluteCountQty
                    || plan.CurrentQuantity != current
                    || pile.Value.Count != plan.LaterEvents.Count + 1
                    || pile.Value[0] != plan.RemovedActiveEvent
                    || !pile.Value.Skip(1).SequenceEqual(plan.LaterEvents))
                    throw Corrupt($"Count event on pile {pile.Key} differs from its WAC tail.");
                boundRemoved.Add(
                    StockReplayWacCountEventFactory.BindProjectedPhysicalStep(
                        removed, new StockReplayPhysicalStep(
                            removed.EventId,
                            plan.Projection.HistoricalQuantityBeforeRemovedEvent,
                            plan.RemovedActiveEvent.SignedDelta,
                            plan.RemovedActiveEvent.AbsoluteCountQty!.Value)));
                projection = new StockReplayPhysicalTailResult(
                    plan.Projection.HistoricalQuantityBeforeRemovedEvent,
                    plan.Projection.LaterSteps,
                    plan.Projection.FinalQuantity);
            }
            else
                projection = StockReplayPhysicalSimulator.ProjectUnchangedTail(
                    current, lastKey, pile.Value);
            historicalQuantity = IvQty.Round(historicalQuantity
                + projection.HistoricalQuantityBeforeTail);
            finalQuantity = IvQty.Round(finalQuantity
                + projection.FinalQuantity);
            foreach (var step in projection.LaterSteps)
            {
                if (!physicalSteps.TryGetValue(step.EventId, out var steps))
                    physicalSteps.Add(step.EventId, steps = []);
                steps.Add(step);
            }
        }
        if (byPile.Count != plans.Length)
            throw Corrupt("A counted pile is missing from the WAC item tail.");
        var later = tail.LaterEvents.Where(e =>
            !removedById.ContainsKey(e.EventId)).ToArray();
        var bound = BindFinancialEvents(later, physicalSteps);
        var projected = StockReplayWacSimulator.SimulateCountSheetRemoval(
            tail.CurrentQuantity, tail.CurrentValue, boundRemoved, bound);
        if (projected.HistoricalQuantityBeforeRemovedEvent
                != historicalQuantity
            || projected.FinalQuantity != finalQuantity)
            throw Corrupt("The removed WAC count pool does not reconcile to its piles.");
        return projected;
    }

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
            if (plan.CountStatus is not ("COUNTED" or "ROLLED_BACK")
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

    // Receipt lines on different piles have one shared item-level WAC effect,
    // but every pile must be rewound and replayed with its own quantity.
    internal static StockReplayWacResult SimulateMultiPileReceiptInsertion(
        StockReplayWacFinancialTail tail, IReadOnlyList<StockReplayPhysicalPlan> plans,
        StockReplayWacEvent candidate)
    {
        if (plans.Count < 2 || plans.Select(p => p.BalLocId).Distinct().Count() != plans.Count
            || candidate.OldSignedQuantity != 0m || candidate.OldSignedValue != 0m
            || candidate.AbsoluteCountQty is not null || candidate.FixedSignedValue is null
            || plans.Any(p => p.Candidate.EventId != candidate.EventId || p.Candidate.Key != candidate.Key
                || p.Candidate.AbsoluteCountQty is not null || p.Candidate.SignedDelta <= 0m
                || p.CompanyCode != plans[0].CompanyCode || p.BranchCode != plans[0].BranchCode
                || p.LedgerEpochId != plans[0].LedgerEpochId || p.BranchWatermark != plans[0].BranchWatermark)
            || IvQty.Round(plans.Sum(p => p.Candidate.SignedDelta)) != candidate.ReplaySignedQuantity)
            throw Corrupt("The receipt does not have consistent item and pile candidates.");
        var changed = new Dictionary<int, StockReplayPhysicalTailResult>();
        foreach (var plan in plans)
        {
            if (!tail.CurrentQuantityByPile.TryGetValue(plan.BalLocId, out var current)
                || current != plan.CurrentQuantity
                || !tail.PhysicalByPile.TryGetValue(plan.BalLocId, out var later)
                || !later.SequenceEqual(plan.LaterEvents))
                throw Corrupt("A receipt pile differs from its WAC tail.");
            changed.Add(plan.BalLocId, new StockReplayPhysicalTailResult(
                plan.Projection.HistoricalQuantityBeforeCandidate, plan.Projection.LaterSteps,
                plan.Projection.FinalQuantity));
        }
        var (historical, final, bound) = BindMultiPileReceiptTail(tail, candidate.Key, changed);
        var result = StockReplayWacSimulator.SimulateInsertion(
            tail.CurrentQuantity, tail.CurrentValue, candidate, bound);
        if (result.HistoricalQuantityBeforeCandidate != historical || result.FinalQuantity != final)
            throw Corrupt("The receipt WAC projection does not reconcile to its piles.");
        return result;
    }

    internal static StockReplayWacRemovalResult SimulateMultiPileReceiptRemoval(
        StockReplayWacFinancialTail tail, IReadOnlyList<StockReplayPhysicalRemovalPlan> plans,
        StockReplayWacEvent removed)
    {
        if (plans.Count < 2 || plans.Select(p => p.BalLocId).Distinct().Count() != plans.Count
            || removed.AbsoluteCountQty is not null || removed.OldSignedQuantity <= 0m
            || plans.Any(p => p.RemovedActiveEvent.AbsoluteCountQty is not null
                || p.RemovedActiveEvent.SignedDelta <= 0m
                || p.CompanyCode != plans[0].CompanyCode || p.BranchCode != plans[0].BranchCode
                || p.LedgerEpochId != plans[0].LedgerEpochId || p.BranchWatermark != plans[0].BranchWatermark
                || p.RemovedActiveEvent.Key.EffectiveAt != removed.Key.EffectiveAt
                || p.RemovedActiveEvent.Key.OriginalPostingSequence != removed.Key.OriginalPostingSequence)
            || IvQty.Round(plans.Sum(p => p.RemovedActiveEvent.SignedDelta)) != removed.OldSignedQuantity)
            throw Corrupt("The removed receipt does not have consistent item and pile events.");
        var changed = new Dictionary<int, StockReplayPhysicalTailResult>();
        foreach (var plan in plans)
        {
            if (!tail.CurrentQuantityByPile.TryGetValue(plan.BalLocId, out var current)
                || current != plan.CurrentQuantity
                || !tail.PhysicalByPile.TryGetValue(plan.BalLocId, out var later)
                || !later.SequenceEqual(plan.LaterEvents))
                throw Corrupt("A removed receipt pile differs from its WAC tail.");
            changed.Add(plan.BalLocId, new StockReplayPhysicalTailResult(
                plan.Projection.HistoricalQuantityBeforeRemovedEvent, plan.Projection.LaterSteps,
                plan.Projection.FinalQuantity));
        }
        var (historical, final, bound) = BindMultiPileReceiptTail(tail, removed.Key, changed);
        var result = StockReplayWacSimulator.SimulateRemoval(
            tail.CurrentQuantity, tail.CurrentValue, removed, bound);
        if (result.HistoricalQuantityBeforeRemovedEvent != historical || result.FinalQuantity != final)
            throw Corrupt("The removed receipt WAC projection does not reconcile to its piles.");
        return result;
    }

    private static (decimal Historical, decimal Final, IReadOnlyList<StockReplayWacEvent> Bound)
        BindMultiPileReceiptTail(StockReplayWacFinancialTail tail, StockReplayEventKey after,
            IReadOnlyDictionary<int, StockReplayPhysicalTailResult> changed)
    {
        if (tail.CurrentQuantityByPile.Count != tail.PhysicalByPile.Count
            || tail.CurrentQuantityByPile.Sum(p => p.Value) != tail.CurrentQuantity
            || tail.LaterEvents.Any(e => e.AbsoluteCountQty is not null))
            throw Corrupt("The receipt has an unsupported or incomplete item tail.");
        var steps = new Dictionary<long, List<StockReplayPhysicalStep>>();
        decimal historical = 0m, final = 0m;
        foreach (var pile in tail.PhysicalByPile)
        {
            if (!tail.CurrentQuantityByPile.TryGetValue(pile.Key, out var current))
                throw Corrupt("A receipt tail pile has no current quantity.");
            var projection = changed.TryGetValue(pile.Key, out var altered)
                ? altered : StockReplayPhysicalSimulator.ProjectUnchangedTail(current, after, pile.Value);
            historical = IvQty.Round(historical + projection.HistoricalQuantityBeforeTail);
            final = IvQty.Round(final + projection.FinalQuantity);
            foreach (var step in projection.LaterSteps)
            {
                if (!steps.TryGetValue(step.EventId, out var values)) steps.Add(step.EventId, values = []);
                values.Add(step);
            }
        }
        if (changed.Keys.Any(id => !tail.PhysicalByPile.ContainsKey(id)))
            throw Corrupt("A changed receipt pile is missing from its WAC tail.");
        return (historical, final, BindFinancialEvents(tail.LaterEvents, steps));
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
