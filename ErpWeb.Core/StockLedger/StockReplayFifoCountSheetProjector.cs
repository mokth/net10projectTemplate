using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoCountCandidate(
    StockReplayEventKey Key, decimal SignedAdjustmentQuantity,
    decimal? FrozenFallbackUnitCost, string BaseUom);

public sealed record StockReplayFifoProjectedCount(
    StockReplayEventKey Key, decimal SignedAdjustmentQuantity,
    StockReplayFifoLayerPosition? Receipt,
    StockReplayFifoProjectedIssue? Issue);

public sealed record StockReplayFifoCountSheetProjection(
    StockReplayFifoHistoricalPool BeforeFirstCount,
    IReadOnlyList<StockReplayFifoProjectedCount> Counts,
    IReadOnlyList<StockReplayFifoProjectedIssue> LaterIssues,
    IReadOnlyList<StockReplayFifoLayerPosition> FinalLayers,
    decimal FinalQuantity, decimal FinalValue);

/// <summary>
/// Projects every line of one FIFO count sheet against a shared item pool.
/// Later ordinary receipts and issues are replayed after all count-class
/// events at the same tick. This is read-only and requires a writer to bind
/// every virtual layer and corrected issue to persisted append-only facts.
/// </summary>
public static class StockReplayFifoCountSheetProjector
{
    private sealed record Event(
        StockReplayEventKey Key, StockReplayFifoCountCandidate? Count,
        StockReplayFifoLayerPosition? Receipt,
        IReadOnlyList<StockReplayFifoConsumptionPosition>? Consumptions);

    public static StockReplayFifoCountSheetProjection Insert(
        StockReplayFifoActiveLayerSnapshot snapshot,
        IReadOnlyCollection<StockReplayFifoCountCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidates);
        var ordered = candidates.OrderBy(c => c.Key).ToArray();
        if (ordered.Length == 0
            || ordered.Select(c => c.Key).Distinct().Count() != ordered.Length
            || ordered.Select(c => c.BaseUom).Distinct(StringComparer.Ordinal).Count() != 1
            || ordered.Any(c => c.Key.ClassPriority != StockReplayEventKey.CountClass
                || c.Key.EffectiveAt != ordered[0].Key.EffectiveAt
                || c.Key.OriginalPostingSequence
                    != ordered[0].Key.OriginalPostingSequence
                || c.Key.EffectiveAt != StockReplayEventKey.CountEndOfDay(c.Key.EffectiveAt)
                || c.Key.OriginalPostingSequence <= 0
                || string.IsNullOrWhiteSpace(c.Key.LineIdentity)
                || c.Key.AttachedSuborder != 0
                || IvQty.Round(c.SignedAdjustmentQuantity)
                    != c.SignedAdjustmentQuantity
                || c.SignedAdjustmentQuantity > 0m
                    && c.FrozenFallbackUnitCost is null
                || c.FrozenFallbackUnitCost is < 0m
                || c.FrozenFallbackUnitCost is { } fallback
                    && StockLedgerPrecision.Money(fallback) != fallback)
            || snapshot.Layers.Any(l => l.BaseUom != ordered[0].BaseUom))
            throw Invalid("FIFO count sheet needs distinct same-day count keys and exact approved costs.");

        // Reuse the single-count baseline proof before projecting a shared pool.
        var baseline = StockReplayFifoTailProjector.InsertCountAdjustment(
            snapshot, ordered[0].Key, 0m, null, ordered[0].BaseUom);
        var before = baseline.BeforeCandidate;
        var laterReceipts = before.LaterReceipts.Select(l =>
            new Event(l.ReceiptKey!.Value, null, l, null));
        var laterConsumptions = before.LaterConsumptions
            .GroupBy(c => c.IssueKey)
            .Select(g => new Event(g.Key, null, null, g.ToArray()));
        var events = laterReceipts.Concat(laterConsumptions)
            .Concat(ordered.Select(c => new Event(c.Key, c, null, null)))
            .OrderBy(e => e.Key).ToArray();
        if (events.Select(e => e.Key).Distinct().Count() != events.Length
            || events.Any(e => e.Count is null
                && e.Key.ClassPriority != StockReplayEventKey.OrdinaryClass)
            || events.Any(e => e.Consumptions is { } rows
                && rows.Select(c => c.DepletesPool).Distinct().Count() != 1))
            throw Invalid("FIFO count sheet has a duplicate or unsupported later event.");

        var layers = before.Layers.ToList();
        var projectedCounts = new List<StockReplayFifoProjectedCount>();
        var laterIssues = new List<StockReplayFifoProjectedIssue>();
        foreach (var @event in events)
        {
            if (@event.Count is { } count)
            {
                if (count.SignedAdjustmentQuantity > 0m)
                {
                    var quantityBefore = StockLedgerPrecision.Quantity(
                        layers.Sum(l => l.RemainingQuantity));
                    var valueBefore = StockLedgerPrecision.Money(
                        layers.Sum(l => l.RemainingValue));
                    var unitCost = quantityBefore > 0m
                        ? StockLedgerPrecision.Money(valueBefore / quantityBefore)
                        : count.FrozenFallbackUnitCost!.Value;
                    var value = StockLedgerPrecision.Money(
                        count.SignedAdjustmentQuantity * unitCost);
                    var virtualId = checked(long.MaxValue - projectedCounts.Count);
                    if (layers.Any(l => l.LayerId == virtualId
                        || l.OriginFactId == virtualId))
                        throw Invalid("FIFO count sheet virtual layer conflicts with an active layer.");
                    var receipt = new StockReplayFifoLayerPosition(
                        virtualId, virtualId, virtualId,
                        count.Key.OriginalPostingSequence, count.Key.EffectiveAt,
                        count.SignedAdjustmentQuantity, value, 0m,
                        count.SignedAdjustmentQuantity, value,
                        StockLedgerPrecision.Money(value / count.SignedAdjustmentQuantity),
                        count.BaseUom, StockFifoLayerStatuses.Open,
                        "PROJECTED", count.Key);
                    layers.Add(receipt);
                    projectedCounts.Add(new(count.Key,
                        count.SignedAdjustmentQuantity, receipt, null));
                }
                else if (count.SignedAdjustmentQuantity < 0m)
                {
                    var countIssueQuantity = -count.SignedAdjustmentQuantity;
                    var allocation = StockReplayFifoLayerAllocator.AllocateIssue(
                        layers, count.Key, countIssueQuantity);
                    layers = allocation.Layers.ToList();
                    var issue = new StockReplayFifoProjectedIssue(count.Key,
                        true, true, [], allocation.Allocations,
                        countIssueQuantity, allocation.TotalValue);
                    projectedCounts.Add(new(count.Key,
                        count.SignedAdjustmentQuantity, null, issue));
                }
                else
                    projectedCounts.Add(new(count.Key, 0m, null, null));
                continue;
            }
            if (@event.Receipt is { } laterReceipt)
            {
                if (layers.Any(l => l.LayerId == laterReceipt.LayerId))
                    throw Invalid("A later FIFO receipt layer appears twice.");
                layers.Add(laterReceipt);
                continue;
            }
            var previous = @event.Consumptions!;
            var quantity = StockLedgerPrecision.Quantity(
                previous.Sum(c => c.Quantity));
            var allocationForIssue = StockReplayFifoLayerAllocator.AllocateIssue(
                layers, @event.Key, quantity);
            var depletes = previous[0].DepletesPool;
            laterIssues.Add(new(@event.Key, false, depletes, previous,
                allocationForIssue.Allocations, quantity,
                allocationForIssue.TotalValue));
            if (depletes)
                layers = allocationForIssue.Layers.ToList();
        }
        var finalQuantity = StockLedgerPrecision.Quantity(
            layers.Sum(l => l.RemainingQuantity));
        var finalValue = StockLedgerPrecision.Money(
            layers.Sum(l => l.RemainingValue));
        var receiptValue = projectedCounts.Sum(c => c.Receipt?.OriginalValue ?? 0m);
        var issueValue = projectedCounts.Sum(c => c.Issue?.Value ?? 0m);
        var changedLaterIssueValue = laterIssues.Where(i => i.DepletesPool)
            .Sum(i => i.Value - i.PreviousConsumptions.Sum(c => c.Value));
        if (finalQuantity != StockLedgerPrecision.Quantity(
                snapshot.CurrentQuantity
                + ordered.Sum(c => c.SignedAdjustmentQuantity))
            || finalValue != StockLedgerPrecision.Money(
                snapshot.CurrentValue + receiptValue - issueValue
                - changedLaterIssueValue))
            throw Invalid("FIFO count sheet does not conserve item quantity and value.");
        return new(before, projectedCounts, laterIssues, layers,
            finalQuantity, finalValue);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
