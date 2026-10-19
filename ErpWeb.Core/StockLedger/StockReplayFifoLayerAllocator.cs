using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoIssueAllocation(
    long LayerId, decimal Quantity, decimal Value, decimal UnitCost);

public sealed record StockReplayFifoIssueAllocationResult(
    IReadOnlyList<StockReplayFifoLayerPosition> Layers,
    IReadOnlyList<StockReplayFifoIssueAllocation> Allocations,
    decimal TotalQuantity, decimal TotalValue);

/// <summary>
/// Forward-only, read-only FIFO issue allocation over a cloned layer snapshot.
/// The caller must first establish the historical opening layers and ensure
/// all earlier replay events have been applied in canonical order.
/// </summary>
public static class StockReplayFifoLayerAllocator
{
    public static StockReplayFifoIssueAllocationResult AllocateIssue(
        IReadOnlyCollection<StockReplayFifoLayerPosition> source,
        StockReplayEventKey issueKey, decimal quantity)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (issueKey.ClassPriority is not (StockReplayEventKey.OrdinaryClass
                or StockReplayEventKey.CountClass)
            || issueKey.OriginalPostingSequence <= 0
            || quantity <= 0m
            || StockLedgerPrecision.Quantity(quantity) != quantity)
            throw Invalid("FIFO issue needs a valid ordinary key and positive quantity.");
        var layers = source.ToArray();
        if (layers.Select(l => l.LayerId).Distinct().Count() != layers.Length
            || layers.Select(l => l.OriginFactId).Distinct().Count()
                != layers.Length
            || layers.Any(l => l.LayerId <= 0 || l.OriginFactId <= 0
                || l.OriginPostingSequence <= 0
                || l.OriginalQuantity <= 0m || l.OriginalValue < 0m
                || l.ReceiptKey is { } receiptKey
                    && (receiptKey.EffectiveAt != l.ReceiptEffectiveAt
                        || receiptKey.OriginalPostingSequence
                            != l.OriginPostingSequence
                        || receiptKey.ClassPriority is not
                            (StockReplayEventKey.OrdinaryClass
                                or StockReplayEventKey.CountClass))
                || l.RemainingQuantity < 0m || l.RemainingValue < 0m
                || l.RemainingQuantity > l.OriginalQuantity
                || l.RemainingQuantity == 0m && l.RemainingValue != 0m
                || l.Status != (l.RemainingQuantity == 0m
                    ? StockFifoLayerStatuses.Closed
                    : StockFifoLayerStatuses.Open)
                || StockLedgerPrecision.Quantity(l.RemainingQuantity)
                    != l.RemainingQuantity
                || StockLedgerPrecision.Money(l.RemainingValue)
                    != l.RemainingValue))
            throw Invalid("FIFO issue has invalid or duplicate source layers.");
        var available = StockLedgerPrecision.Quantity(layers
            .Where(l => AvailableAt(l, issueKey))
            .Sum(l => l.RemainingQuantity));
        if (available < quantity)
            throw new StockLedgerException(new(
                StockLedgerErrorCodes.InsufficientBaseQty,
                $"FIFO layers at {issueKey.EffectiveAt:O} contain {available}, but {quantity} is required."));

        var updated = layers.ToDictionary(l => l.LayerId);
        var allocations = new List<StockReplayFifoIssueAllocation>();
        var remaining = quantity;
        foreach (var layer in layers
            .Where(l => AvailableAt(l, issueKey)
                && l.RemainingQuantity > 0m)
            .OrderBy(ReceiptOrderKey)
            .ThenBy(l => l.OriginFactId)
            .ThenBy(l => l.LayerId))
        {
            if (remaining == 0m)
                break;
            var take = StockLedgerPrecision.Quantity(
                Math.Min(remaining, layer.RemainingQuantity));
            var value = take == layer.RemainingQuantity
                ? layer.RemainingValue
                : StockLedgerPrecision.Money(
                    layer.RemainingValue * take / layer.RemainingQuantity);
            var afterQty = StockLedgerPrecision.Quantity(
                layer.RemainingQuantity - take);
            var afterValue = StockLedgerPrecision.Money(
                layer.RemainingValue - value);
            if (take <= 0m || value < 0m || afterValue < 0m)
                throw Invalid($"FIFO layer {layer.LayerId} has an invalid allocation.");
            allocations.Add(new StockReplayFifoIssueAllocation(layer.LayerId,
                take, value, StockLedgerPrecision.Money(value / take)));
            updated[layer.LayerId] = layer with
            {
                RemainingQuantity = afterQty,
                RemainingValue = afterValue,
                CurrentUnitCost = afterQty == 0m ? 0m
                    : StockLedgerPrecision.Money(afterValue / afterQty),
                Status = afterQty == 0m
                    ? StockFifoLayerStatuses.Closed
                    : StockFifoLayerStatuses.Open
            };
            remaining = StockLedgerPrecision.Quantity(remaining - take);
        }
        if (remaining != 0m)
            throw Invalid("FIFO issue did not consume its full planned quantity.");
        var result = layers.Select(l => updated[l.LayerId]).ToArray();
        var totalValue = StockLedgerPrecision.Money(
            allocations.Sum(a => a.Value));
        if (StockLedgerPrecision.Quantity(result.Sum(l => l.RemainingQuantity))
                != StockLedgerPrecision.Quantity(
                    layers.Sum(l => l.RemainingQuantity) - quantity)
            || StockLedgerPrecision.Money(result.Sum(l => l.RemainingValue))
                != StockLedgerPrecision.Money(
                    layers.Sum(l => l.RemainingValue) - totalValue))
            throw Invalid("FIFO issue does not conserve layer quantity and value.");
        return new(result, allocations, quantity, totalValue);
    }

    private static bool AvailableAt(StockReplayFifoLayerPosition layer,
        StockReplayEventKey issueKey) =>
        layer.ReceiptKey is { } receiptKey
            ? receiptKey.CompareTo(issueKey) < 0
            : layer.ReceiptEffectiveAt < issueKey.EffectiveAt
                || layer.ReceiptEffectiveAt == issueKey.EffectiveAt
                    && (issueKey.ClassPriority == StockReplayEventKey.CountClass
                        || layer.OriginPostingSequence
                            < issueKey.OriginalPostingSequence);

    private static StockReplayEventKey ReceiptOrderKey(
        StockReplayFifoLayerPosition layer) =>
        layer.ReceiptKey ?? new StockReplayEventKey(
            layer.ReceiptEffectiveAt, StockReplayEventKey.OrdinaryClass,
            layer.OriginPostingSequence, string.Empty, 0);

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
