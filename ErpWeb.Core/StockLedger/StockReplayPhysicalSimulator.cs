using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Logical order of an original stock event and any corrections attached to it.
/// PostingSequence is the original business owner's sequence, not the later writer's.
/// </summary>
public readonly record struct StockReplayEventKey(
    DateTime EffectiveAt,
    int ClassPriority,
    long OriginalPostingSequence,
    string LineIdentity,
    int AttachedSuborder) : IComparable<StockReplayEventKey>
{
    public const int OrdinaryClass = 0;
    public const int CountClass = 1;

    public static DateTime CountEndOfDay(DateTime countDate)
    {
        if (countDate.Date >= DateTime.MaxValue.Date)
            throw new ArgumentOutOfRangeException(nameof(countDate));
        return countDate.Date.AddDays(1).AddTicks(-1);
    }

    public int CompareTo(StockReplayEventKey other)
    {
        var comparison = EffectiveAt.CompareTo(other.EffectiveAt);
        if (comparison != 0) return comparison;
        comparison = ClassPriority.CompareTo(other.ClassPriority);
        if (comparison != 0) return comparison;
        comparison = OriginalPostingSequence.CompareTo(other.OriginalPostingSequence);
        if (comparison != 0) return comparison;
        comparison = StringComparer.Ordinal.Compare(LineIdentity, other.LineIdentity);
        return comparison != 0 ? comparison : AttachedSuborder.CompareTo(other.AttachedSuborder);
    }
}

/// <summary>
/// An active physical event on one exact stock pile. SignedDelta is its current
/// active adjustment and is used to rewind the existing live balance. An
/// AbsoluteCountQty makes the forward event an end-of-day count reset.
/// </summary>
public sealed record StockReplayPhysicalEvent(
    long EventId,
    StockReplayEventKey Key,
    decimal SignedDelta,
    decimal? AbsoluteCountQty = null);

public sealed record StockReplayPhysicalStep(
    long EventId,
    decimal QuantityBefore,
    decimal SignedDelta,
    decimal QuantityAfter);

public sealed record StockReplayPhysicalResult(
    decimal HistoricalQuantityBeforeCandidate,
    StockReplayPhysicalStep Candidate,
    IReadOnlyList<StockReplayPhysicalStep> LaterSteps,
    decimal FinalQuantity);

public sealed record StockReplayPhysicalRemovalResult(
    decimal HistoricalQuantityBeforeRemovedEvent,
    IReadOnlyList<StockReplayPhysicalStep> LaterSteps,
    decimal FinalQuantity);

public sealed record StockReplayPhysicalTailResult(
    decimal HistoricalQuantityBeforeTail,
    IReadOnlyList<StockReplayPhysicalStep> LaterSteps,
    decimal FinalQuantity);

/// <summary>
/// Rewinds active later deltas from the current pile, inserts one candidate at
/// its original position, and replays the tail. It is read-only; callers still
/// have to prove lineage, period state, cost effects and rowversions before a
/// writer may persist the result.
/// </summary>
public static class StockReplayPhysicalSimulator
{
    /// <summary>Projects a later pile tail when the inserted event affects another pile.</summary>
    public static StockReplayPhysicalTailResult ProjectUnchangedTail(
        decimal currentQuantity,
        StockReplayEventKey after,
        IReadOnlyCollection<StockReplayPhysicalEvent> laterActiveEvents)
    {
        ArgumentNullException.ThrowIfNull(laterActiveEvents);
        if (currentQuantity < 0m || IvQty.Round(currentQuantity) != currentQuantity)
            throw new ArgumentOutOfRangeException(nameof(currentQuantity));
        var later = laterActiveEvents.OrderBy(x => x.Key).ThenBy(x => x.EventId)
            .ToArray();
        for (var i = 0; i < later.Length; i++)
        {
            ValidateEvent(later[i]);
            if (later[i].Key.CompareTo(after) <= 0
                || i > 0 && later[i].Key.CompareTo(later[i - 1].Key) == 0)
                throw new ArgumentException("The physical tail has duplicate or earlier keys.",
                    nameof(laterActiveEvents));
        }
        var quantity = Rewind(currentQuantity, later);
        var historical = quantity;
        var steps = new List<StockReplayPhysicalStep>(later.Length);
        foreach (var movement in later)
            steps.Add(Apply(movement, ref quantity));
        if (quantity != currentQuantity)
            throw new StockLedgerException(new(StockLedgerErrorCodes.LedgerMismatch,
                "The unchanged physical tail does not reconcile to its current pile."));
        return new(historical, steps, quantity);
    }

    /// <summary>
    /// Removes an active event at its original key, then replays later events. For a
    /// count this removes the reset itself, so later quantities may become invalid.
    /// </summary>
    public static StockReplayPhysicalRemovalResult SimulateRemoval(
        decimal currentQuantity,
        StockReplayPhysicalEvent removedActiveEvent,
        IReadOnlyCollection<StockReplayPhysicalEvent> laterActiveEvents)
    {
        ArgumentNullException.ThrowIfNull(laterActiveEvents);
        ValidateEvent(removedActiveEvent);
        if (currentQuantity < 0m || IvQty.Round(currentQuantity) != currentQuantity)
            throw new ArgumentOutOfRangeException(nameof(currentQuantity));

        var later = OrderAndValidateLater(removedActiveEvent, laterActiveEvents);
        var quantity = Rewind(currentQuantity, later);
        quantity = IvQty.Round(quantity - removedActiveEvent.SignedDelta);
        EnsureNonnegative(quantity, removedActiveEvent.EventId, "removing");
        var historicalBefore = quantity;
        var steps = new List<StockReplayPhysicalStep>(later.Length);
        foreach (var movement in later)
            steps.Add(Apply(movement, ref quantity));
        return new(historicalBefore, steps, quantity);
    }

    public static StockReplayPhysicalResult Simulate(
        decimal currentQuantity,
        StockReplayPhysicalEvent candidate,
        IReadOnlyCollection<StockReplayPhysicalEvent> laterActiveEvents)
    {
        ArgumentNullException.ThrowIfNull(laterActiveEvents);
        ValidateEvent(candidate);
        if (currentQuantity < 0m || IvQty.Round(currentQuantity) != currentQuantity)
            throw new ArgumentOutOfRangeException(nameof(currentQuantity));

        var later = OrderAndValidateLater(candidate, laterActiveEvents);
        var quantity = Rewind(currentQuantity, later);

        var historicalQuantity = quantity;
        var candidateStep = Apply(candidate, ref quantity);
        var steps = new List<StockReplayPhysicalStep>(later.Length);
        foreach (var movement in later)
            steps.Add(Apply(movement, ref quantity));

        return new(historicalQuantity, candidateStep, steps, quantity);
    }

    private static StockReplayPhysicalEvent[] OrderAndValidateLater(
        StockReplayPhysicalEvent candidate,
        IReadOnlyCollection<StockReplayPhysicalEvent> laterActiveEvents)
    {
        var later = laterActiveEvents.OrderBy(x => x.Key).ThenBy(x => x.EventId).ToArray();
        if (later.Any(x => x.EventId == candidate.EventId))
            throw new ArgumentException("Candidate and later event identities must differ.", nameof(laterActiveEvents));
        for (var i = 0; i < later.Length; i++)
        {
            ValidateEvent(later[i]);
            if (later[i].Key.CompareTo(candidate.Key) <= 0
                || i > 0 && later[i].Key.CompareTo(later[i - 1].Key) == 0)
                throw new ArgumentException("Later active events must have unique keys after the candidate.", nameof(laterActiveEvents));
        }
        return later;
    }

    private static decimal Rewind(decimal currentQuantity, StockReplayPhysicalEvent[] later)
    {
        var quantity = currentQuantity;
        for (var i = later.Length - 1; i >= 0; i--)
        {
            quantity = IvQty.Round(quantity - later[i].SignedDelta);
            EnsureNonnegative(quantity, later[i].EventId, "rewinding");
        }
        return quantity;
    }

    private static StockReplayPhysicalStep Apply(StockReplayPhysicalEvent movement, ref decimal quantity)
    {
        var before = quantity;
        var delta = movement.AbsoluteCountQty is decimal physical
            ? IvQty.Round(physical - before) : movement.SignedDelta;
        quantity = IvQty.Round(before + delta);
        EnsureNonnegative(quantity, movement.EventId, "replaying");
        return new(movement.EventId, before, delta, quantity);
    }

    private static void ValidateEvent(StockReplayPhysicalEvent movement)
    {
        // Persisted inventory histories use positive IDs; count generations use negative IDs
        // so one timeline can identify both without an arbitrary remapping.
        if (movement.EventId == 0 || movement.Key.OriginalPostingSequence <= 0
            || string.IsNullOrWhiteSpace(movement.Key.LineIdentity)
            || movement.Key.AttachedSuborder < 0
            || movement.Key.ClassPriority is not (StockReplayEventKey.OrdinaryClass or StockReplayEventKey.CountClass)
            || IvQty.Round(movement.SignedDelta) != movement.SignedDelta
            || movement.AbsoluteCountQty is decimal physical
                && (physical < 0m || IvQty.Round(physical) != physical))
            throw new ArgumentException($"Physical replay event {movement.EventId} has invalid evidence.");
        if ((movement.Key.ClassPriority == StockReplayEventKey.CountClass)
            != movement.AbsoluteCountQty.HasValue)
            throw new ArgumentException($"Physical replay event {movement.EventId} has inconsistent count identity.");
        if (movement.AbsoluteCountQty.HasValue
            && movement.Key.EffectiveAt != StockReplayEventKey.CountEndOfDay(movement.Key.EffectiveAt))
            throw new ArgumentException($"Count event {movement.EventId} is not at the logical end of day.");
    }

    private static void EnsureNonnegative(decimal quantity, long eventId, string phase)
    {
        if (quantity < 0m)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.InsufficientBaseQty,
                $"Physical replay would make the stock slice negative at event {eventId} while {phase}."));
    }
}
