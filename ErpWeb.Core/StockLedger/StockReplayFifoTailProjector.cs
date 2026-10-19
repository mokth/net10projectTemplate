using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoProjectedIssue(
    StockReplayEventKey Key, bool IsCandidate, bool DepletesPool,
    IReadOnlyList<StockReplayFifoConsumptionPosition> PreviousConsumptions,
    IReadOnlyList<StockReplayFifoIssueAllocation> Allocations,
    decimal Quantity, decimal Value);

public sealed record StockReplayFifoTailProjection(
    StockReplayFifoHistoricalPool BeforeCandidate,
    IReadOnlyList<StockReplayFifoProjectedIssue> Issues,
    IReadOnlyList<StockReplayFifoLayerPosition> FinalLayers,
    decimal FinalQuantity, decimal FinalValue,
    StockReplayFifoLayerPosition? CandidateReceipt,
    bool IsRemoval = false);

/// <summary>
/// Pure insertion/removal projection for FIFO issues, receipts, and count adjustments. Reprices
/// every later active issue and transfer OUT allocation in canonical order.
/// It does not write facts, layers, cost state, or dependent module amounts.
/// </summary>
public static class StockReplayFifoTailProjector
{
    private sealed record TailEvent(
        StockReplayEventKey Key,
        StockReplayFifoLayerPosition? Receipt,
        IReadOnlyList<StockReplayFifoConsumptionPosition>? Consumptions,
        bool Candidate);

    public static StockReplayFifoTailProjection InsertIssue(
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayEventKey candidateKey, decimal candidateQuantity)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (candidateKey.ClassPriority != StockReplayEventKey.OrdinaryClass
            || candidateKey.OriginalPostingSequence <= 0
            || candidateQuantity <= 0m
            || StockLedgerPrecision.Quantity(candidateQuantity) != candidateQuantity)
            throw Invalid("FIFO issue insertion requires a positive ordinary event.");
        return Project(snapshot, candidateKey, candidateQuantity, null);
    }

    public static StockReplayFifoTailProjection InsertTransfer(
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayEventKey candidateKey, decimal candidateQuantity)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (candidateKey.ClassPriority != StockReplayEventKey.OrdinaryClass
            || candidateKey.OriginalPostingSequence <= 0
            || candidateQuantity <= 0m
            || StockLedgerPrecision.Quantity(candidateQuantity) != candidateQuantity)
            throw Invalid("FIFO transfer insertion requires a positive ordinary event.");
        return Project(snapshot, candidateKey, candidateQuantity, null,
            candidateDepletesPool: false);
    }

    public static StockReplayFifoTailProjection InsertReceipt(
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayEventKey candidateKey, decimal candidateQuantity,
        decimal candidateValue, string baseUom)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (candidateKey.ClassPriority != StockReplayEventKey.OrdinaryClass
            || candidateKey.OriginalPostingSequence <= 0
            || candidateQuantity <= 0m
            || StockLedgerPrecision.Quantity(candidateQuantity) != candidateQuantity
            || candidateValue < 0m
            || StockLedgerPrecision.Money(candidateValue) != candidateValue
            || string.IsNullOrWhiteSpace(baseUom)
            || snapshot.Layers.Any(l => l.BaseUom != baseUom)
            || snapshot.Layers.Any(l => l.LayerId == long.MaxValue
                || l.OriginFactId == long.MaxValue))
            throw Invalid("FIFO receipt insertion requires an exact positive ordinary receipt.");
        var candidateLayer = new StockReplayFifoLayerPosition(
            long.MaxValue, long.MaxValue, long.MaxValue,
            candidateKey.OriginalPostingSequence,
            candidateKey.EffectiveAt, candidateQuantity, candidateValue,
            0m, candidateQuantity, candidateValue,
            StockLedgerPrecision.Money(candidateValue / candidateQuantity),
            baseUom, ErpWeb.Model.Entities.StockLedger.StockFifoLayerStatuses.Open,
            "PROJECTED", candidateKey);
        return Project(snapshot, candidateKey, candidateQuantity,
            candidateLayer);
    }

    public static StockReplayFifoTailProjection InsertCountAdjustment(
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayEventKey countKey, decimal signedAdjustmentQuantity,
        decimal? frozenFallbackUnitCost, string baseUom)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (countKey.ClassPriority != StockReplayEventKey.CountClass
            || countKey.EffectiveAt != StockReplayEventKey.CountEndOfDay(countKey.EffectiveAt)
            || countKey.OriginalPostingSequence <= 0
            || string.IsNullOrWhiteSpace(countKey.LineIdentity)
            || countKey.AttachedSuborder != 0
            || IvQty.Round(signedAdjustmentQuantity)
                != signedAdjustmentQuantity
            || frozenFallbackUnitCost is < 0m
            || frozenFallbackUnitCost is { } fallback
                && StockLedgerPrecision.Money(fallback) != fallback
            || string.IsNullOrWhiteSpace(baseUom)
            || snapshot.Layers.Any(l => l.BaseUom != baseUom)
            || snapshot.Layers.Any(l => l.LayerId == long.MaxValue
                || l.OriginFactId == long.MaxValue))
            throw Invalid("FIFO count needs an exact end-of-day key, quantity, UOM and approved fallback.");

        var before = StockReplayFifoLayerRewinder.RewindBefore(snapshot, countKey);
        if (signedAdjustmentQuantity > 0m)
        {
            if (frozenFallbackUnitCost is null)
                throw Invalid("Positive FIFO count adjustment has no frozen cost basis.");
            var unitCost = before.Quantity > 0m
                ? StockLedgerPrecision.Money(before.Value / before.Quantity)
                : frozenFallbackUnitCost.Value;
            var value = StockLedgerPrecision.Money(
                signedAdjustmentQuantity * unitCost);
            var layer = new StockReplayFifoLayerPosition(
                long.MaxValue, long.MaxValue, long.MaxValue,
                countKey.OriginalPostingSequence, countKey.EffectiveAt,
                signedAdjustmentQuantity, value, 0m,
                signedAdjustmentQuantity, value,
                StockLedgerPrecision.Money(value / signedAdjustmentQuantity), baseUom,
                ErpWeb.Model.Entities.StockLedger.StockFifoLayerStatuses.Open,
                "PROJECTED", countKey);
            return Project(snapshot, countKey, signedAdjustmentQuantity, layer);
        }
        if (signedAdjustmentQuantity < 0m)
            return Project(snapshot, countKey, -signedAdjustmentQuantity, null);

        var events = BuildEvents(before);
        ValidateBaseline(snapshot, before, events);
        var projected = Replay(before.Layers, events, 0m);
        return new(before, projected.Issues, projected.Layers,
            snapshot.CurrentQuantity, snapshot.CurrentValue, null);
    }

    public static StockReplayFifoTailProjection RemoveReceipt(
        StockReplayFifoActiveLayerSnapshot snapshot, long layerId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var origin = snapshot.Layers.SingleOrDefault(l => l.LayerId == layerId);
        if (origin?.ReceiptKey is not { } targetKey)
            throw Invalid($"FIFO receipt layer {layerId} has no canonical key.");
        var before = StockReplayFifoLayerRewinder.RewindBefore(
            snapshot, targetKey);
        var events = BuildEvents(before);
        if (events.Count(e => e.Receipt?.LayerId == layerId) != 1)
            throw Invalid($"FIFO receipt layer {layerId} is not active at its key.");
        ValidateBaseline(snapshot, before, events);
        var projected = Replay(before.Layers,
            events.Where(e => e.Receipt?.LayerId != layerId), 0m);
        var finalQuantity = StockLedgerPrecision.Quantity(
            projected.Layers.Sum(l => l.RemainingQuantity));
        var finalValue = StockLedgerPrecision.Money(
            projected.Layers.Sum(l => l.RemainingValue));
        var changedLaterIssueValue = projected.Issues
            .Where(i => i.DepletesPool)
            .Sum(i => i.Value - i.PreviousConsumptions.Sum(c => c.Value));
        if (finalQuantity != StockLedgerPrecision.Quantity(
                snapshot.CurrentQuantity - origin.OriginalQuantity)
            || finalValue != StockLedgerPrecision.Money(
                snapshot.CurrentValue - origin.OriginalValue
                - changedLaterIssueValue))
            throw Invalid("FIFO receipt removal does not conserve quantity and value.");
        return new(before, projected.Issues, projected.Layers,
            finalQuantity, finalValue, origin, IsRemoval: true);
    }

    public static StockReplayFifoTailProjection RemoveIssue(
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayEventKey issueKey)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var before = StockReplayFifoLayerRewinder.RewindBefore(
            snapshot, issueKey);
        var events = BuildEvents(before);
        var target = events.SingleOrDefault(e => e.Key == issueKey
            && e.Consumptions is not null);
        if (target?.Consumptions is not { } removed)
            throw Invalid("FIFO issue removal needs one active canonical issue.");
        if (!removed[0].DepletesPool)
            throw Invalid("A FIFO transfer requires paired physical removal.");
        ValidateBaseline(snapshot, before, events);
        var projected = Replay(before.Layers,
            events.Where(e => e != target), 0m);
        var finalQuantity = StockLedgerPrecision.Quantity(
            projected.Layers.Sum(l => l.RemainingQuantity));
        var finalValue = StockLedgerPrecision.Money(
            projected.Layers.Sum(l => l.RemainingValue));
        var depletes = removed[0].DepletesPool;
        var removedQuantity = depletes
            ? StockLedgerPrecision.Quantity(removed.Sum(c => c.Quantity)) : 0m;
        var removedValue = depletes
            ? StockLedgerPrecision.Money(removed.Sum(c => c.Value)) : 0m;
        var changedLaterIssueValue = projected.Issues
            .Where(i => i.DepletesPool)
            .Sum(i => i.Value - i.PreviousConsumptions.Sum(c => c.Value));
        if (finalQuantity != StockLedgerPrecision.Quantity(
                snapshot.CurrentQuantity + removedQuantity)
            || finalValue != StockLedgerPrecision.Money(
                snapshot.CurrentValue + removedValue
                - changedLaterIssueValue))
            throw Invalid("FIFO issue removal does not conserve quantity and value.");
        return new(before, projected.Issues, projected.Layers,
            finalQuantity, finalValue, null, IsRemoval: true);
    }

    public static StockReplayFifoTailProjection RemoveTransfer(
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayEventKey transferKey)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var before = StockReplayFifoLayerRewinder.RewindBefore(
            snapshot, transferKey);
        var events = BuildEvents(before);
        var target = events.SingleOrDefault(e => e.Key == transferKey
            && e.Consumptions is not null);
        if (target?.Consumptions is not { } removed
            || removed.Count == 0 || removed[0].DepletesPool)
            throw Invalid("FIFO transfer removal needs one active nondepleting allocation.");
        ValidateBaseline(snapshot, before, events);
        var projected = Replay(before.Layers,
            events.Where(e => e != target), 0m);
        var finalQuantity = StockLedgerPrecision.Quantity(
            projected.Layers.Sum(l => l.RemainingQuantity));
        var finalValue = StockLedgerPrecision.Money(
            projected.Layers.Sum(l => l.RemainingValue));
        if (finalQuantity != snapshot.CurrentQuantity
            || finalValue != snapshot.CurrentValue
            || projected.Issues.Any(i => i.DepletesPool
                && i.Value != StockLedgerPrecision.Money(
                    i.PreviousConsumptions.Sum(c => c.Value))))
            throw Invalid("FIFO transfer removal changed the item pool.");
        return new(before, projected.Issues, projected.Layers,
            finalQuantity, finalValue, null, IsRemoval: true);
    }

    private static StockReplayFifoTailProjection Project(
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayEventKey candidateKey, decimal candidateQuantity,
        StockReplayFifoLayerPosition? candidateReceipt,
        bool candidateDepletesPool = true)
    {
        var before = StockReplayFifoLayerRewinder.RewindBefore(
            snapshot, candidateKey);
        var events = BuildEvents(before);
        events.Add(new TailEvent(candidateKey, candidateReceipt, null, true));
        if (events.GroupBy(e => e.Key).Any(g => g.Count() != 1))
            throw Invalid("FIFO tail has duplicate canonical event keys.");
        ValidateBaseline(snapshot, before,
            events.Where(e => !e.Candidate));

        var projected = Replay(before.Layers, events, candidateQuantity,
            candidateDepletesPool);
        var finalQuantity = StockLedgerPrecision.Quantity(
            projected.Layers.Sum(l => l.RemainingQuantity));
        var finalValue = StockLedgerPrecision.Money(
            projected.Layers.Sum(l => l.RemainingValue));
        if (finalQuantity != StockLedgerPrecision.Quantity(
                snapshot.CurrentQuantity
                + (candidateReceipt is not null ? candidateQuantity
                    : candidateDepletesPool ? -candidateQuantity : 0m)))
            throw Invalid("FIFO insertion projection does not conserve final quantity.");
        var candidateIssueValue = projected.Issues
            .Where(i => i.IsCandidate).Sum(i => i.Value);
        var changedLaterIssueValue = projected.Issues
            .Where(i => !i.IsCandidate && i.DepletesPool)
            .Sum(i => i.Value - i.PreviousConsumptions.Sum(c => c.Value));
        if (finalValue != StockLedgerPrecision.Money(
                snapshot.CurrentValue
                + (candidateReceipt?.OriginalValue ?? 0m)
                - (candidateDepletesPool ? candidateIssueValue : 0m)
                - changedLaterIssueValue))
            throw Invalid("FIFO insertion projection does not conserve final value.");
        return new(before, projected.Issues, projected.Layers,
            finalQuantity, finalValue, candidateReceipt);
    }

    private static List<TailEvent> BuildEvents(
        StockReplayFifoHistoricalPool before)
    {
        var events = before.LaterReceipts.Select(l =>
                new TailEvent(l.ReceiptKey!.Value, l, null, false))
            .Concat(before.LaterConsumptions
                .GroupBy(c => c.IssueKey)
                .Select(g => new TailEvent(g.Key, null, g.ToArray(), false)))
            .ToList();
        if (events.GroupBy(e => e.Key).Any(g => g.Count() != 1)
            || events.Any(e => e.Consumptions is { } rows
                && rows.Select(c => c.DepletesPool).Distinct().Count() != 1))
            throw Invalid("FIFO tail has duplicate or mixed canonical events.");
        return events;
    }

    private static void ValidateBaseline(
        StockReplayFifoActiveLayerSnapshot snapshot,
        StockReplayFifoHistoricalPool before,
        IEnumerable<TailEvent> events)
    {
        // Prove that the same event adapter reproduces the sealed present
        // before trusting its changed-cost projection.
        var baseline = Replay(before.Layers, events, 0m);
        var liveById = snapshot.Layers.ToDictionary(l => l.LayerId);
        if (baseline.Layers.Count != liveById.Count
            || baseline.Layers.Any(l => !liveById.TryGetValue(l.LayerId,
                    out var live)
                || l.RemainingQuantity != live.RemainingQuantity
                || l.RemainingValue != live.RemainingValue)
            || baseline.Issues.Any(issue => !issue.Allocations
                .Select(a => (a.LayerId, a.Quantity, a.Value))
                .OrderBy(a => a.LayerId)
                .SequenceEqual(issue.PreviousConsumptions
                    .Select(c => (LayerId: c.LayerId,
                        Quantity: c.Quantity, Value: c.Value))
                    .OrderBy(c => c.LayerId))))
            throw Invalid("FIFO active tail cannot reproduce its sealed layer allocations.");
    }

    private static (List<StockReplayFifoLayerPosition> Layers,
        List<StockReplayFifoProjectedIssue> Issues) Replay(
        IReadOnlyList<StockReplayFifoLayerPosition> opening,
        IEnumerable<TailEvent> events, decimal candidateQuantity,
        bool candidateDepletesPool = true)
    {
        var layers = opening.ToList();
        var projectedIssues = new List<StockReplayFifoProjectedIssue>();
        foreach (var @event in events.OrderBy(e => e.Key))
        {
            if (@event.Receipt is { } receipt)
            {
                if (layers.Any(l => l.LayerId == receipt.LayerId))
                    throw Invalid($"FIFO receipt layer {receipt.LayerId} appears twice.");
                layers.Add(receipt);
                continue;
            }
            var previous = @event.Consumptions ?? [];
            var quantity = @event.Candidate ? candidateQuantity
                : StockLedgerPrecision.Quantity(previous.Sum(c => c.Quantity));
            var depletes = @event.Candidate
                ? candidateDepletesPool : previous[0].DepletesPool;
            var allocation = StockReplayFifoLayerAllocator.AllocateIssue(
                layers, @event.Key, quantity);
            projectedIssues.Add(new(@event.Key, @event.Candidate,
                depletes, previous, allocation.Allocations,
                quantity, allocation.TotalValue));
            if (depletes)
                layers = allocation.Layers.ToList();
        }
        return (layers, projectedIssues);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
