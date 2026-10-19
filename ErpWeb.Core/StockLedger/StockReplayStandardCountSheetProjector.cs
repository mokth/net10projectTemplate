using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Projects every counted pile of one item in canonical line order through
/// the shared Standard pool and its later physical and financial events.
/// </summary>
public static class StockReplayStandardCountSheetProjector
{
    public static StockReplayStandardResult SimulateInsertion(
        StockReplayStandardFinancialTail tail,
        IReadOnlyCollection<StockReplayPhysicalPlan> physicalPlans,
        IReadOnlyCollection<StockReplayStandardEvent> candidates)
    {
        ArgumentNullException.ThrowIfNull(tail);
        ArgumentNullException.ThrowIfNull(physicalPlans);
        ArgumentNullException.ThrowIfNull(candidates);
        if (physicalPlans.Count == 0
            || physicalPlans.Count != candidates.Count
            || tail.PhysicalByPile.Count != tail.CurrentQuantityByPile.Count
            || tail.CurrentQuantityByPile.Sum(p => p.Value)
                != tail.CurrentQuantity)
            throw Invalid("The Standard count sheet has incomplete item evidence.");
        var plans = physicalPlans.OrderBy(p => p.Candidate.Key).ToArray();
        var first = plans[0];
        var earliest = first.Candidate.Key;
        var byPile = new Dictionary<int, StockReplayPhysicalPlan>();
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
                || plan.Candidate.Key.ClassPriority
                    != StockReplayEventKey.CountClass
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
                throw Invalid("Standard count candidates have inconsistent sheet identities.");
        }
        var byCandidate = new Dictionary<long, StockReplayStandardEvent>();
        foreach (var candidate in candidates)
        {
            if (!byCandidate.TryAdd(candidate.EventId, candidate))
                throw Invalid("The Standard count sheet repeats a candidate.");
        }
        if (tail.LaterEvents.Any(e => e.Key.CompareTo(earliest) <= 0
                || byCandidate.ContainsKey(e.EventId)))
            throw Invalid("The Standard tail overlaps a count candidate.");

        var steps = new Dictionary<long, List<StockReplayPhysicalStep>>();
        var candidateSteps = new Dictionary<long, StockReplayPhysicalStep>();
        decimal historicalItemQuantity = 0m;
        decimal finalItemQuantity = 0m;
        foreach (var pile in tail.PhysicalByPile)
        {
            if (!tail.CurrentQuantityByPile.TryGetValue(pile.Key,
                out var current))
                throw Invalid($"Standard pile {pile.Key} has no current quantity.");
            decimal historical;
            decimal final;
            var projected = new List<StockReplayPhysicalStep>();
            if (byPile.TryGetValue(pile.Key, out var plan))
            {
                if (!byCandidate.TryGetValue(plan.Candidate.EventId,
                        out var candidate)
                    || candidate.Kind != StockReplayStandardEventKinds.Count
                    || candidate.Key != plan.Candidate.Key
                    || candidate.OldSignedQuantity != 0m
                    || candidate.OldSignedValue != 0m
                    || candidate.ReplaySignedQuantity
                        != plan.Projection.Candidate.SignedDelta
                    || candidate.AbsoluteCountQty
                        != plan.Candidate.AbsoluteCountQty
                    || plan.CurrentQuantity != current
                    || pile.Value.Any(e => e.Key == plan.Candidate.Key))
                    throw Invalid($"Standard count on pile {pile.Key} differs from its plan.");
                var prefix = pile.Value.Where(e =>
                    e.Key.CompareTo(plan.Candidate.Key) < 0).ToArray();
                var suffix = pile.Value.Where(e =>
                    e.Key.CompareTo(plan.Candidate.Key) > 0).ToArray();
                if (!suffix.SequenceEqual(plan.LaterEvents))
                    throw Invalid($"Standard pile {pile.Key} has a changed active tail.");
                var physical = StockReplayPhysicalSimulator.Simulate(
                    current, plan.Candidate, plan.LaterEvents);
                if (physical.HistoricalQuantityBeforeCandidate
                            != plan.Projection.HistoricalQuantityBeforeCandidate
                        || physical.Candidate != plan.Projection.Candidate
                        || !physical.LaterSteps.SequenceEqual(
                            plan.Projection.LaterSteps)
                        || physical.FinalQuantity
                            != plan.Projection.FinalQuantity)
                    throw Invalid($"Standard pile {pile.Key} has a stale projection.");
                if (prefix.Length > 0)
                {
                    var before = StockReplayPhysicalSimulator.ProjectUnchangedTail(
                        physical.HistoricalQuantityBeforeCandidate,
                        earliest, prefix);
                    historical = before.HistoricalQuantityBeforeTail;
                    projected.AddRange(before.LaterSteps);
                }
                else
                    historical = physical.HistoricalQuantityBeforeCandidate;
                projected.AddRange(physical.LaterSteps);
                final = physical.FinalQuantity;
                candidateSteps.Add(plan.Candidate.EventId, physical.Candidate);
            }
            else
            {
                var unchanged = StockReplayPhysicalSimulator.ProjectUnchangedTail(
                    current, earliest, pile.Value);
                historical = unchanged.HistoricalQuantityBeforeTail;
                final = unchanged.FinalQuantity;
                projected.AddRange(unchanged.LaterSteps);
            }
            historicalItemQuantity = IvQty.Round(historicalItemQuantity
                + historical);
            finalItemQuantity = IvQty.Round(finalItemQuantity + final);
            foreach (var step in projected)
            {
                if (!steps.TryGetValue(step.EventId, out var eventSteps))
                    steps.Add(step.EventId, eventSteps = []);
                eventSteps.Add(step);
            }
        }
        if (byPile.Count != candidateSteps.Count)
            throw Invalid("A Standard count candidate pile is missing.");
        var boundTail = StockReplayStandardTailProjector.BindFinancialEvents(
            tail.LaterEvents, steps);
        var orderedCandidates = plans.Select(p =>
            StockReplayStandardCountEventFactory.BindProjectedPhysicalStep(
                byCandidate[p.Candidate.EventId],
                candidateSteps[p.Candidate.EventId])).ToArray();
        var financial = StockReplayStandardSimulator.SimulateInsertion(
            tail.CurrentQuantity, tail.CurrentValue,
            orderedCandidates[0],
            boundTail.Concat(orderedCandidates.Skip(1)).ToArray());
        if (financial.HistoricalQuantityBeforeCandidate
                != historicalItemQuantity
            || financial.FinalQuantity != finalItemQuantity)
            throw Invalid("The Standard count sheet does not reconcile to its piles.");
        return financial;
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
