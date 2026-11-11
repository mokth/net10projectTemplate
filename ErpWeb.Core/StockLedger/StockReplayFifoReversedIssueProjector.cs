using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

internal sealed record StockReplayFifoReversedIssueProjection(
    string SnapshotHash,
    StockReplayEventKey ReceiptKey,
    StockReplayEventKey IssueKey,
    StockReplayEventKey ReversalKey,
    IReadOnlyList<StockReplayFifoIssueAllocation> OriginalSplits,
    IReadOnlyList<StockReplayFifoIssueAllocation> ProjectedSplits,
    decimal OriginalValue,
    decimal ProjectedValue);

/// <summary>
/// Calculates the isolated historical FIFO split for an issue that has already
/// been reversed. This is a read-only calculation, not permission to post: the
/// caller must separately prove the sealed issue/reversal and receipt owners,
/// all intervening histories (including reversed ones), periods, and the
/// unchanged plan again under the branch lock before writing corrections.
/// </summary>
internal static class StockReplayFifoReversedIssueProjector
{
    private sealed record HistoricalSplit(long LayerId, decimal Quantity, decimal Value, int Ordinal);

    internal static StockReplayFifoReversedIssueProjection InsertReceipt(
        StockReplayFifoActiveLayerSnapshot snapshot, StockReplayEventKey issueKey,
        StockReplayEventKey reversalKey, IReadOnlyList<StockReplayFifoConsumptionPosition> original,
        StockReplayFifoLayerPosition candidate) =>
        Insert(snapshot, issueKey, reversalKey, OriginalSplits(original, issueKey), candidate);

    internal static StockReplayFifoReversedIssueProjection InsertReceipt(
        StockReplayFifoActiveLayerSnapshot snapshot, StockReplayEventKey issueKey,
        StockReplayEventKey reversalKey, IReadOnlyList<StockFifoReversedIssueSplit> original,
        StockReplayFifoLayerPosition candidate) =>
        Insert(snapshot, issueKey, reversalKey, GenerationSplits(original), candidate);

    private static StockReplayFifoReversedIssueProjection Insert(
        StockReplayFifoActiveLayerSnapshot snapshot, StockReplayEventKey issueKey,
        StockReplayEventKey reversalKey, IReadOnlyList<HistoricalSplit> original,
        StockReplayFifoLayerPosition candidate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.ReceiptKey is not { } receiptKey
            || candidate.LayerId != long.MaxValue || candidate.OriginFactId != long.MaxValue
            || candidate.OriginPostingId != long.MaxValue
            || candidate.RemainingQuantity != candidate.OriginalQuantity
            || candidate.RemainingValue != candidate.OriginalValue
            || candidate.AccumulatedAdjustment != 0m
            || snapshot.Layers.Any(l => l.BaseUom != candidate.BaseUom))
            throw Invalid("The FIFO receipt candidate is not an unconsumed projected layer.");
        return Project(snapshot, receiptKey, issueKey, reversalKey, original,
            layers => layers.Append(candidate).ToArray());
    }

    internal static StockReplayFifoReversedIssueProjection RemoveReceipt(
        StockReplayFifoActiveLayerSnapshot snapshot, StockReplayEventKey issueKey,
        StockReplayEventKey reversalKey, IReadOnlyList<StockReplayFifoConsumptionPosition> original,
        long receiptLayerId) =>
        Remove(snapshot, issueKey, reversalKey, OriginalSplits(original, issueKey), receiptLayerId);

    internal static StockReplayFifoReversedIssueProjection RemoveReceipt(
        StockReplayFifoActiveLayerSnapshot snapshot, StockReplayEventKey issueKey,
        StockReplayEventKey reversalKey, IReadOnlyList<StockFifoReversedIssueSplit> original,
        long receiptLayerId) =>
        Remove(snapshot, issueKey, reversalKey, GenerationSplits(original), receiptLayerId);

    private static StockReplayFifoReversedIssueProjection Remove(
        StockReplayFifoActiveLayerSnapshot snapshot, StockReplayEventKey issueKey,
        StockReplayEventKey reversalKey, IReadOnlyList<HistoricalSplit> original, long receiptLayerId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var receipt = snapshot.Layers.SingleOrDefault(l => l.LayerId == receiptLayerId);
        if (receipt?.ReceiptKey is not { } receiptKey || receipt.AccumulatedAdjustment != 0m)
            throw Invalid("The removed FIFO receipt has no exact active layer.");
        return Project(snapshot, receiptKey, issueKey, reversalKey, original, layers =>
        {
            var source = layers.SingleOrDefault(l => l.LayerId == receiptLayerId);
            if (source is null || source.RemainingQuantity != source.OriginalQuantity
                || source.RemainingValue != source.OriginalValue)
                throw Invalid("The removed FIFO receipt was consumed before the isolated issue.");
            return layers.Where(l => l.LayerId != receiptLayerId).ToArray();
        });
    }

    private static StockReplayFifoReversedIssueProjection Project(
        StockReplayFifoActiveLayerSnapshot snapshot, StockReplayEventKey receiptKey,
        StockReplayEventKey issueKey, StockReplayEventKey reversalKey,
        IReadOnlyList<HistoricalSplit> original,
        Func<IReadOnlyList<StockReplayFifoLayerPosition>, StockReplayFifoLayerPosition[]> change)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(original);
        if (receiptKey.ClassPriority != StockReplayEventKey.OrdinaryClass
            || issueKey.ClassPriority != StockReplayEventKey.OrdinaryClass
            || reversalKey.ClassPriority != StockReplayEventKey.OrdinaryClass
            || receiptKey.OriginalPostingSequence <= 0 || issueKey.OriginalPostingSequence <= 0
            || reversalKey.OriginalPostingSequence <= 0 || receiptKey.AttachedSuborder != 0
            || issueKey.AttachedSuborder != 0 || reversalKey.AttachedSuborder != 0
            || string.IsNullOrWhiteSpace(receiptKey.LineIdentity)
            || string.IsNullOrWhiteSpace(issueKey.LineIdentity) || string.IsNullOrWhiteSpace(reversalKey.LineIdentity)
            || receiptKey.CompareTo(issueKey) >= 0 || issueKey.CompareTo(reversalKey) >= 0
            || original.Count == 0 || original.Any(c => c.LayerId <= 0 || c.Quantity <= 0m || c.Value < 0m)
            || original.Select(c => c.Ordinal).Order().Where((n, index) => n != index).Any()
            || snapshot.Layers.Any(l => l.AccumulatedAdjustment != 0m || l.SalesReturn is not null)
            || snapshot.ActiveConsumptions.Any(c => c.IssueKey.CompareTo(receiptKey) >= 0
                && c.IssueKey.CompareTo(reversalKey) < 0))
            throw Invalid("The reversed FIFO issue is not an isolated ordered split.");

        var before = StockReplayFifoLayerRewinder.RewindBefore(snapshot, issueKey);
        var quantity = StockLedgerPrecision.Quantity(original.Sum(c => c.Quantity));
        var baseline = StockReplayFifoLayerAllocator.AllocateIssue(before.Layers, issueKey, quantity);
        var recorded = original.OrderBy(c => c.Ordinal)
            .Select(c => (c.LayerId, c.Quantity, c.Value)).ToArray();
        if (!baseline.Allocations.Select(a => (a.LayerId, a.Quantity, a.Value)).SequenceEqual(recorded))
            throw Invalid("The historical FIFO split cannot be reproduced from its verified layers.");

        var projected = StockReplayFifoLayerAllocator.AllocateIssue(change(before.Layers), issueKey, quantity);
        return new(snapshot.SnapshotHash, receiptKey, issueKey, reversalKey,
            baseline.Allocations.ToArray(), projected.Allocations.ToArray(), baseline.TotalValue, projected.TotalValue);
    }

    private static HistoricalSplit[] OriginalSplits(
        IReadOnlyList<StockReplayFifoConsumptionPosition> original, StockReplayEventKey issueKey)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Any(c => !c.DepletesPool || c.IssueKey != issueKey || c.ConsumptionId <= 0 || c.IssueFactId <= 0)
            || original.Select(c => c.ConsumptionId).Distinct().Count() != original.Count
            || original.Select(c => c.IssueFactId).Distinct().Count() != original.Count)
            throw Invalid("The reversed FIFO issue has invalid original consumption identities.");
        return original.Select(c => new HistoricalSplit(c.LayerId, c.Quantity, c.Value, c.SplitOrdinal)).ToArray();
    }

    private static HistoricalSplit[] GenerationSplits(IReadOnlyList<StockFifoReversedIssueSplit> original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Any(s => s.Id <= 0 || s.GenerationId <= 0)
            || original.Select(s => s.Id).Distinct().Count() != original.Count
            || original.Select(s => s.GenerationId).Distinct().Count() != 1)
            throw Invalid("The reversed FIFO issue has invalid correction split identities.");
        return original.Select(s => new HistoricalSplit(s.FifoLayerId, s.ConsumedQty, s.ConsumedValue, s.SplitOrdinal)).ToArray();
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
