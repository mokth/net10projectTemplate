using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// One item-level WAC event. Old signed quantity/value rewind the active tail;
/// fixed value is the receipt or value-only authority on replay; a bound Sales
/// return instead follows its repriced sale. Count events carry a frozen observation.
/// </summary>
public sealed record StockReplayWacEvent(
    long EventId,
    StockReplayEventKey Key,
    decimal OldSignedQuantity,
    decimal OldSignedValue,
    decimal ReplaySignedQuantity,
    decimal? FixedSignedValue = null,
    decimal? AbsoluteCountQty = null,
    decimal? CountFallbackUnitCost = null,
    decimal? CountPreQuantity = null,
    long? ReturnOfSaleEventId = null,
    long? SalesReturnAllocationId = null);

public sealed record StockReplayWacStep(
    long EventId,
    decimal QuantityBefore,
    decimal ValueBefore,
    decimal SignedQuantity,
    decimal SignedValue,
    decimal QuantityAfter,
    decimal ValueAfter);

public sealed record StockReplayWacResult(
    decimal HistoricalQuantityBeforeCandidate,
    decimal HistoricalValueBeforeCandidate,
    StockReplayWacStep Candidate,
    IReadOnlyList<StockReplayWacStep> LaterSteps,
    decimal FinalQuantity,
    decimal FinalValue);

public sealed record StockReplayWacRemovalResult(
    decimal HistoricalQuantityBeforeRemovedEvent,
    decimal HistoricalValueBeforeRemovedEvent,
    IReadOnlyList<StockReplayWacStep> LaterSteps,
    decimal FinalQuantity,
    decimal FinalValue);

/// <summary>
/// Read-only WAC projection for an inserted receipt, issue, value adjustment,
/// or count reset. It neither invents source receipt values nor changes facts.
/// The caller must prove the supplied old active valuation set and immutable
/// receipt authority before using a result in a replay root.
/// </summary>
public static class StockReplayWacSimulator
{
    public static StockReplayWacRemovalResult SimulateCountSheetRemoval(
        decimal currentQuantity, decimal currentValue,
        IReadOnlyCollection<StockReplayWacEvent> removedActiveEvents,
        IReadOnlyCollection<StockReplayWacEvent> laterActiveEvents)
    {
        ArgumentNullException.ThrowIfNull(removedActiveEvents);
        ArgumentNullException.ThrowIfNull(laterActiveEvents);
        var removed = removedActiveEvents.OrderBy(e => e.Key).ToArray();
        if (removed.Length == 0)
            throw new ArgumentException("The count sheet has no active WAC events.",
                nameof(removedActiveEvents));
        var ids = new HashSet<long>();
        for (var i = 0; i < removed.Length; i++)
        {
            var movement = removed[i];
            Validate(movement);
            if (!ids.Add(movement.EventId)
                || i > 0 && movement.Key == removed[i - 1].Key
                || movement.AbsoluteCountQty is null
                || movement.Key.ClassPriority != StockReplayEventKey.CountClass
                || movement.Key.EffectiveAt != removed[0].Key.EffectiveAt
                || movement.Key.OriginalPostingSequence
                    != removed[0].Key.OriginalPostingSequence
                || IvQty.Round(movement.AbsoluteCountQty.Value
                    - movement.CountPreQuantity!.Value)
                    != movement.OldSignedQuantity)
                throw Invalid(movement.EventId,
                    "does not belong to the same active count sheet");
        }
        EnsureState(currentQuantity, currentValue, removed[0].EventId,
            "current");
        var later = OrderAndValidateLater(removed[^1], laterActiveEvents);
        if (later.Any(e => ids.Contains(e.EventId)))
            throw Invalid(removed[^1].EventId,
                "repeats an active count identity in its later tail");
        var quantity = currentQuantity;
        var value = currentValue;
        Rewind(later, ref quantity, ref value);
        for (var i = removed.Length - 1; i >= 0; i--)
        {
            quantity = IvQty.Round(quantity - removed[i].OldSignedQuantity);
            value = StockLedgerPrecision.Money(value - removed[i].OldSignedValue);
            EnsureState(quantity, value, removed[i].EventId, "removing");
        }
        var historicalQuantity = quantity;
        var historicalValue = value;
        var steps = new List<StockReplayWacStep>(later.Length);
        var applied = new Dictionary<long, StockReplayWacStep>();
        foreach (var movement in later)
            steps.Add(Apply(movement, ref quantity, ref value, applied));
        return new(historicalQuantity, historicalValue, steps,
            quantity, value);
    }

    public static StockReplayWacRemovalResult SimulateRemoval(
        decimal currentQuantity,
        decimal currentValue,
        StockReplayWacEvent removedActiveEvent,
        IReadOnlyCollection<StockReplayWacEvent> laterActiveEvents)
    {
        ArgumentNullException.ThrowIfNull(laterActiveEvents);
        Validate(removedActiveEvent);
        if (removedActiveEvent.AbsoluteCountQty is decimal observation
            && IvQty.Round(observation
                - removedActiveEvent.CountPreQuantity!.Value)
                != removedActiveEvent.OldSignedQuantity)
            throw Invalid(removedActiveEvent.EventId,
                "has a count observation inconsistent with its active adjustment");
        EnsureState(currentQuantity, currentValue, removedActiveEvent.EventId, "current");
        var later = OrderAndValidateLater(removedActiveEvent, laterActiveEvents);
        var quantity = currentQuantity;
        var value = currentValue;
        Rewind(later, ref quantity, ref value);
        quantity = IvQty.Round(quantity - removedActiveEvent.OldSignedQuantity);
        value = StockLedgerPrecision.Money(value - removedActiveEvent.OldSignedValue);
        EnsureState(quantity, value, removedActiveEvent.EventId, "removing");
        var historicalQuantity = quantity;
        var historicalValue = value;
        var steps = new List<StockReplayWacStep>(later.Length);
        var applied = new Dictionary<long, StockReplayWacStep>();
        foreach (var movement in later)
            steps.Add(Apply(movement, ref quantity, ref value, applied));
        return new(historicalQuantity, historicalValue, steps, quantity, value);
    }

    public static StockReplayWacResult SimulateInsertion(
        decimal currentQuantity,
        decimal currentValue,
        StockReplayWacEvent candidate,
        IReadOnlyCollection<StockReplayWacEvent> laterActiveEvents)
    {
        ArgumentNullException.ThrowIfNull(laterActiveEvents);
        Validate(candidate);
        if (candidate.OldSignedQuantity != 0m || candidate.OldSignedValue != 0m)
            throw new ArgumentException("An inserted candidate has no prior active WAC effect.",
                nameof(candidate));
        EnsureState(currentQuantity, currentValue, candidate.EventId, "current");

        var later = OrderAndValidateLater(candidate, laterActiveEvents);

        var quantity = currentQuantity;
        var value = currentValue;
        Rewind(later, ref quantity, ref value);

        var historicalQuantity = quantity;
        var historicalValue = value;
        var applied = new Dictionary<long, StockReplayWacStep>();
        var candidateStep = Apply(candidate, ref quantity, ref value, applied);
        var steps = new List<StockReplayWacStep>(later.Length);
        foreach (var movement in later)
            steps.Add(Apply(movement, ref quantity, ref value, applied));
        return new(historicalQuantity, historicalValue,
            candidateStep, steps, quantity, value);
    }

    private static StockReplayWacEvent[] OrderAndValidateLater(
        StockReplayWacEvent anchor,
        IReadOnlyCollection<StockReplayWacEvent> laterActiveEvents)
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
                || later[i].AbsoluteCountQty is null
                    && later[i].OldSignedQuantity != later[i].ReplaySignedQuantity)
                throw new ArgumentException(
                    "Later WAC effects must have unique identities and ordered keys.",
                    nameof(laterActiveEvents));
        }
        return later;
    }

    private static void Rewind(
        StockReplayWacEvent[] later, ref decimal quantity, ref decimal value)
    {
        for (var i = later.Length - 1; i >= 0; i--)
        {
            quantity = IvQty.Round(quantity - later[i].OldSignedQuantity);
            value = StockLedgerPrecision.Money(value - later[i].OldSignedValue);
            EnsureState(quantity, value, later[i].EventId, "rewinding");
        }
    }

    private static StockReplayWacStep Apply(
        StockReplayWacEvent movement, ref decimal quantity, ref decimal value,
        IDictionary<long, StockReplayWacStep> applied)
    {
        var beforeQty = quantity;
        var beforeValue = value;
        // A count observes one pile. Its absolute quantity must never replace
        // the WAC quantity for the entire item across all piles.
        var deltaQty = movement.AbsoluteCountQty is decimal physical
            ? IvQty.Round(physical - movement.CountPreQuantity!.Value)
            : movement.ReplaySignedQuantity;
        decimal deltaValue;
        if (movement.AbsoluteCountQty.HasValue)
        {
            deltaValue = deltaQty > 0m
                ? StockLedgerPrecision.Money(deltaQty * UnitCost(
                    beforeQty, beforeValue, movement.CountFallbackUnitCost,
                    movement.EventId))
                : IssueValue(deltaQty, beforeQty, beforeValue, movement.EventId);
        }
        else if (movement.ReturnOfSaleEventId is long saleId)
        {
            if (!applied.TryGetValue(saleId, out var sale)
                || sale.SignedQuantity >= 0m || sale.SignedValue > 0m
                || deltaQty <= 0m || deltaQty > -sale.SignedQuantity)
                throw Invalid(movement.EventId, "has no matching earlier replayed sale");
            deltaValue = StockLedgerPrecision.Money(
                -sale.SignedValue * deltaQty / -sale.SignedQuantity);
        }
        else if (deltaQty > 0m)
        {
            deltaValue = movement.FixedSignedValue!.Value;
        }
        else if (deltaQty < 0m)
        {
            deltaValue = IssueValue(deltaQty, beforeQty, beforeValue, movement.EventId);
        }
        else
        {
            deltaValue = movement.FixedSignedValue!.Value;
        }

        quantity = IvQty.Round(beforeQty + deltaQty);
        value = StockLedgerPrecision.Money(beforeValue + deltaValue);
        EnsureState(quantity, value, movement.EventId, "replaying");
        var step = new StockReplayWacStep(movement.EventId, beforeQty, beforeValue,
            deltaQty, deltaValue, quantity, value);
        if (!applied.TryAdd(movement.EventId, step))
            throw Invalid(movement.EventId, "has a repeated replay identity");
        return step;
    }

    private static decimal IssueValue(
        decimal signedQty, decimal beforeQty, decimal beforeValue, long eventId)
    {
        if (signedQty == 0m) return 0m;
        var issueQty = -signedQty;
        if (issueQty > beforeQty || beforeQty <= 0m)
            throw Invalid(eventId, "issues more stock than the historical WAC pool holds");
        return issueQty == beforeQty
            ? -beforeValue
            : -StockLedgerPrecision.Money(issueQty *
                StockLedgerPrecision.Money(beforeValue / beforeQty));
    }

    private static decimal UnitCost(
        decimal beforeQty, decimal beforeValue,
        decimal? fallback, long eventId)
    {
        if (beforeQty > 0m)
            return StockLedgerPrecision.Money(beforeValue / beforeQty);
        if (fallback is decimal approved)
            return approved;
        throw Invalid(eventId, "has no approved zero-stock count cost fallback");
    }

    private static void Validate(StockReplayWacEvent movement)
    {
        if (movement.EventId == 0 || movement.Key.OriginalPostingSequence <= 0
            || string.IsNullOrWhiteSpace(movement.Key.LineIdentity)
            || movement.Key.AttachedSuborder < 0
            || movement.Key.ClassPriority is not (
                StockReplayEventKey.OrdinaryClass or StockReplayEventKey.CountClass)
            || IvQty.Round(movement.OldSignedQuantity) != movement.OldSignedQuantity
            || IvQty.Round(movement.ReplaySignedQuantity) != movement.ReplaySignedQuantity
            || StockLedgerPrecision.Money(movement.OldSignedValue) != movement.OldSignedValue
            || movement.FixedSignedValue is decimal fixedValue
                && StockLedgerPrecision.Money(fixedValue) != fixedValue
            || movement.CountFallbackUnitCost is decimal fallback
                && (fallback < 0m
                    || StockLedgerPrecision.Money(fallback) != fallback)
            || movement.OldSignedQuantity > 0m && movement.OldSignedValue < 0m
            || movement.OldSignedQuantity < 0m && movement.OldSignedValue > 0m
            || movement.AbsoluteCountQty.HasValue
                && movement.OldSignedQuantity == 0m
                && movement.OldSignedValue != 0m)
            throw new ArgumentException($"WAC event {movement.EventId} has invalid evidence.");
        if (movement.Key.ClassPriority == StockReplayEventKey.CountClass)
        {
            if (movement.AbsoluteCountQty is not decimal physical
                || physical < 0m || IvQty.Round(physical) != physical
                || movement.CountPreQuantity is not decimal preQuantity
                || preQuantity < 0m || IvQty.Round(preQuantity) != preQuantity
                || movement.FixedSignedValue.HasValue
                || movement.ReturnOfSaleEventId.HasValue
                || movement.SalesReturnAllocationId.HasValue
                || movement.Key.EffectiveAt
                    != StockReplayEventKey.CountEndOfDay(movement.Key.EffectiveAt))
                throw new ArgumentException($"Count WAC event {movement.EventId} is invalid.");
        }
        else if (movement.AbsoluteCountQty.HasValue
            || movement.CountFallbackUnitCost.HasValue
            || movement.CountPreQuantity.HasValue
            || movement.ReturnOfSaleEventId is long returnSaleId
                && (returnSaleId <= 0 || returnSaleId == movement.EventId
                    || movement.ReplaySignedQuantity <= 0m
                    || movement.SalesReturnAllocationId is not > 0)
            || movement.ReturnOfSaleEventId is null
                && movement.SalesReturnAllocationId.HasValue
            || movement.ReplaySignedQuantity < 0m && movement.FixedSignedValue.HasValue
            || movement.ReplaySignedQuantity >= 0m
                && movement.FixedSignedValue is not decimal authorizedValue
            || movement.ReplaySignedQuantity > 0m
                && movement.FixedSignedValue < 0m)
            throw new ArgumentException($"Ordinary WAC event {movement.EventId} lacks cost authority.");
    }

    private static void EnsureState(
        decimal quantity, decimal value, long eventId, string phase)
    {
        if (quantity < 0m || value < 0m
            || IvQty.Round(quantity) != quantity
            || StockLedgerPrecision.Money(value) != value
            || quantity == 0m && value != 0m)
            throw Invalid(eventId, $"has an invalid quantity/value state while {phase}");
    }

    private static StockLedgerException Invalid(long eventId, string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch,
            $"WAC event {eventId} {message}."));
}
