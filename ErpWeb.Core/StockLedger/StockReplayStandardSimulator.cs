using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;

namespace ErpWeb.Core.StockLedger;

public static class StockReplayStandardEventKinds
{
    public const string Movement = "MOVEMENT";
    public const string Count = "COUNT";
    public const string Revaluation = "REVALUATION";
}

/// <summary>
/// One Standard Cost effect at its original business key. UnitCost is the
/// approved revision effective at that event, never a current item price.
/// </summary>
public sealed record StockReplayStandardEvent(
    long EventId,
    StockReplayEventKey Key,
    string Kind,
    decimal OldSignedQuantity,
    decimal OldSignedValue,
    decimal ReplaySignedQuantity,
    decimal UnitCost,
    long StandardCostRevisionId,
    decimal? AbsoluteCountQty = null,
    decimal? CountPreQuantity = null);

public sealed record StockReplayStandardStep(
    long EventId,
    decimal QuantityBefore,
    decimal ValueBefore,
    decimal SignedQuantity,
    decimal SignedValue,
    decimal QuantityAfter,
    decimal ValueAfter);

public sealed record StockReplayStandardResult(
    decimal HistoricalQuantityBeforeCandidate,
    decimal HistoricalValueBeforeCandidate,
    StockReplayStandardStep Candidate,
    IReadOnlyList<StockReplayStandardStep> LaterSteps,
    decimal FinalQuantity,
    decimal FinalValue);

public sealed record StockReplayStandardRemovalResult(
    decimal HistoricalQuantityBeforeRemovedEvent,
    decimal HistoricalValueBeforeRemovedEvent,
    IReadOnlyList<StockReplayStandardStep> LaterSteps,
    decimal FinalQuantity,
    decimal FinalValue);

/// <summary>
/// Rewinds the active Standard tail and replays movements at each event's
/// approved revision. A revaluation targets the replayed quantity times its
/// revision, so an earlier count changes that later value-only delta.
/// </summary>
public static class StockReplayStandardSimulator
{
    public static StockReplayStandardRemovalResult SimulateRemoval(
        decimal currentQuantity,
        decimal currentValue,
        StockReplayStandardEvent removedActiveEvent,
        IReadOnlyCollection<StockReplayStandardEvent> laterActiveEvents)
    {
        ArgumentNullException.ThrowIfNull(removedActiveEvent);
        ArgumentNullException.ThrowIfNull(laterActiveEvents);
        Validate(removedActiveEvent);
        if (removedActiveEvent.Kind == StockReplayStandardEventKinds.Count
            && removedActiveEvent.OldSignedQuantity != IvQty.Round(
                removedActiveEvent.AbsoluteCountQty!.Value
                - removedActiveEvent.CountPreQuantity!.Value))
            throw Invalid(removedActiveEvent.EventId,
                "has an active count adjustment inconsistent with its observation");
        EnsureState(currentQuantity, currentValue,
            removedActiveEvent.EventId, "current");
        var later = OrderAndValidateLater(removedActiveEvent, laterActiveEvents);
        var quantity = currentQuantity;
        var value = currentValue;
        Rewind(later, ref quantity, ref value);
        quantity = IvQty.Round(quantity - removedActiveEvent.OldSignedQuantity);
        value = StockLedgerPrecision.Money(value - removedActiveEvent.OldSignedValue);
        EnsureState(quantity, value, removedActiveEvent.EventId, "removing");
        var historicalQuantity = quantity;
        var historicalValue = value;
        var steps = new List<StockReplayStandardStep>(later.Length);
        foreach (var movement in later)
            steps.Add(Apply(movement, ref quantity, ref value));
        return new(historicalQuantity, historicalValue, steps,
            quantity, value);
    }

    public static StockReplayStandardResult SimulateInsertion(
        decimal currentQuantity,
        decimal currentValue,
        StockReplayStandardEvent candidate,
        IReadOnlyCollection<StockReplayStandardEvent> laterActiveEvents)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(laterActiveEvents);
        Validate(candidate);
        if (candidate.OldSignedQuantity != 0m || candidate.OldSignedValue != 0m)
            throw new ArgumentException(
                "An inserted Standard candidate has no prior active effect.",
                nameof(candidate));
        EnsureState(currentQuantity, currentValue, candidate.EventId, "current");

        var later = OrderAndValidateLater(candidate, laterActiveEvents);

        var quantity = currentQuantity;
        var value = currentValue;
        Rewind(later, ref quantity, ref value);
        var historicalQuantity = quantity;
        var historicalValue = value;
        var candidateStep = Apply(candidate, ref quantity, ref value);
        var steps = new List<StockReplayStandardStep>(later.Length);
        foreach (var movement in later)
            steps.Add(Apply(movement, ref quantity, ref value));
        return new(historicalQuantity, historicalValue,
            candidateStep, steps, quantity, value);
    }

    private static StockReplayStandardEvent[] OrderAndValidateLater(
        StockReplayStandardEvent anchor,
        IReadOnlyCollection<StockReplayStandardEvent> laterActiveEvents)
    {
        var later = laterActiveEvents.OrderBy(e => e.Key)
            .ThenBy(e => e.EventId).ToArray();
        var ids = new HashSet<long> { anchor.EventId };
        for (var i = 0; i < later.Length; i++)
        {
            Validate(later[i]);
            if (!ids.Add(later[i].EventId)
                || later[i].Key.CompareTo(anchor.Key) <= 0
                || i > 0 && later[i].Key.CompareTo(later[i - 1].Key) == 0
                || later[i].Kind != StockReplayStandardEventKinds.Count
                    && later[i].ReplaySignedQuantity
                        != later[i].OldSignedQuantity)
                throw new ArgumentException(
                    "Later Standard effects need unique ordered identities and unchanged ordinary quantities.",
                    nameof(laterActiveEvents));
        }
        return later;
    }

    private static void Rewind(
        StockReplayStandardEvent[] later,
        ref decimal quantity,
        ref decimal value)
    {
        var currentQuantity = quantity;
        var currentValue = value;
        for (var i = later.Length - 1; i >= 0; i--)
        {
            quantity = IvQty.Round(quantity - later[i].OldSignedQuantity);
            value = StockLedgerPrecision.Money(value - later[i].OldSignedValue);
            EnsureState(quantity, value, later[i].EventId, "rewinding");
        }

        var originalQuantity = quantity;
        var originalValue = value;
        foreach (var movement in later)
        {
            originalQuantity = IvQty.Round(
                originalQuantity + movement.OldSignedQuantity);
            originalValue = StockLedgerPrecision.Money(
                originalValue + movement.OldSignedValue);
            EnsureState(originalQuantity, originalValue,
                movement.EventId, "checking the active tail");
            if (movement.Kind == StockReplayStandardEventKinds.Revaluation
                && originalValue != StockLedgerPrecision.Money(
                    originalQuantity * movement.UnitCost))
                throw Invalid(movement.EventId,
                    "has an active revaluation that does not target its approved revision");
        }
        if (originalQuantity != currentQuantity || originalValue != currentValue)
            throw Invalid(later.LastOrDefault()?.EventId ?? 0,
                "does not reconcile to the current Standard state");
    }

    private static StockReplayStandardStep Apply(
        StockReplayStandardEvent movement,
        ref decimal quantity,
        ref decimal value)
    {
        var beforeQuantity = quantity;
        var beforeValue = value;
        var signedQuantity = movement.Kind == StockReplayStandardEventKinds.Count
            ? IvQty.Round(movement.AbsoluteCountQty!.Value
                - movement.CountPreQuantity!.Value)
            : movement.ReplaySignedQuantity;
        var afterQuantity = IvQty.Round(beforeQuantity + signedQuantity);
        if (afterQuantity < 0m)
            throw Invalid(movement.EventId, "would make the Standard stock pool negative");
        var signedValue = movement.Kind == StockReplayStandardEventKinds.Revaluation
            ? StockLedgerPrecision.Money(
                afterQuantity * movement.UnitCost - beforeValue)
            : StockLedgerPrecision.Money(signedQuantity * movement.UnitCost);
        var afterValue = StockLedgerPrecision.Money(beforeValue + signedValue);
        EnsureState(afterQuantity, afterValue, movement.EventId, "replaying");
        quantity = afterQuantity;
        value = afterValue;
        return new(movement.EventId, beforeQuantity, beforeValue,
            signedQuantity, signedValue, afterQuantity, afterValue);
    }

    private static void Validate(StockReplayStandardEvent movement)
    {
        if (movement.EventId == 0 || movement.Key.OriginalPostingSequence <= 0
            || string.IsNullOrWhiteSpace(movement.Key.LineIdentity)
            || movement.Key.AttachedSuborder < 0
            || IvQty.Round(movement.OldSignedQuantity)
                != movement.OldSignedQuantity
            || IvQty.Round(movement.ReplaySignedQuantity)
                != movement.ReplaySignedQuantity
            || StockLedgerPrecision.Money(movement.OldSignedValue)
                != movement.OldSignedValue
            || movement.StandardCostRevisionId <= 0
            || movement.UnitCost < 0m
            || StockLedgerPrecision.Money(movement.UnitCost)
                != movement.UnitCost)
            throw new ArgumentException(
                $"Standard event {movement.EventId} has invalid evidence.");
        switch (movement.Kind)
        {
            case StockReplayStandardEventKinds.Count:
                if (movement.Key.ClassPriority != StockReplayEventKey.CountClass
                    || movement.Key.EffectiveAt
                        != StockReplayEventKey.CountEndOfDay(
                            movement.Key.EffectiveAt)
                    || movement.AbsoluteCountQty is not decimal observation
                    || movement.CountPreQuantity is not decimal pre
                    || observation < 0m || pre < 0m
                    || IvQty.Round(observation) != observation
                    || IvQty.Round(pre) != pre
                    || movement.ReplaySignedQuantity
                        != IvQty.Round(observation - pre)
                    || movement.OldSignedValue != StockLedgerPrecision.Money(
                        movement.OldSignedQuantity * movement.UnitCost))
                    throw new ArgumentException(
                        $"Standard count {movement.EventId} has invalid physical evidence.");
                break;
            case StockReplayStandardEventKinds.Movement:
                if (movement.Key.ClassPriority != StockReplayEventKey.OrdinaryClass
                    || movement.AbsoluteCountQty.HasValue
                    || movement.CountPreQuantity.HasValue
                    || movement.OldSignedValue != StockLedgerPrecision.Money(
                        movement.OldSignedQuantity * movement.UnitCost))
                    throw new ArgumentException(
                        $"Standard movement {movement.EventId} lacks exact revision value.");
                break;
            case StockReplayStandardEventKinds.Revaluation:
                if (movement.Key.ClassPriority != StockReplayEventKey.OrdinaryClass
                    || movement.OldSignedQuantity != 0m
                    || movement.ReplaySignedQuantity != 0m
                    || movement.AbsoluteCountQty.HasValue
                    || movement.CountPreQuantity.HasValue)
                    throw new ArgumentException(
                        $"Standard revaluation {movement.EventId} has a physical effect.");
                break;
            default:
                throw new ArgumentException(
                    $"Standard event {movement.EventId} has an unknown kind.");
        }
    }

    private static void EnsureState(
        decimal quantity, decimal value, long eventId, string phase)
    {
        if (quantity < 0m || value < 0m
            || IvQty.Round(quantity) != quantity
            || StockLedgerPrecision.Money(value) != value
            || quantity == 0m && value != 0m)
            throw Invalid(eventId,
                $"has an invalid quantity/value state while {phase}");
    }

    private static StockLedgerException Invalid(long eventId, string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch,
            $"Standard event {eventId} {message}."));
}
